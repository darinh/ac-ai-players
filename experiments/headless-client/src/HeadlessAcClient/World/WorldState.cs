// SPDX-License-Identifier: AGPL-3.0-or-later
// WorldState — guid-keyed in-memory accumulator for everything
// the bot knows about the world. Fed by the post-EnterWorld
// firehose decoded in Phase 4 (ObjectCreate, UpdatePosition,
// Motion, SetState, PrivateUpdatePropertyInt, PlayerCreate).
//
// Design notes:
//   - Single-threaded. Lives behind the receive loop in
//     HandshakeDriver. If we ever multi-thread the network
//     layer, wrap _objects with a lock.
//   - ObjectCreate for a known guid MERGES (preserves FirstSeen
//     and PropertyInts; refreshes weenie / physics; respects
//     sequence high-water marks per rubber-duck pass).
//   - Sequence gating per message family uses a two-level rule:
//       (a) if SeqInstance advanced, accept everything (new
//           instance epoch — the message-specific sequence
//           tracker starts fresh implicitly because the
//           server resets it on the wire too);
//       (b) if SeqInstance is equal, gate on the message-
//           specific sequence (SeqPosition for UpdatePosition,
//           SeqMovement for Motion, SeqState for SetState);
//       (c) if SeqInstance went backward, drop entirely.
//   - PrivateUpdatePropertyInt has no guid in the wire format —
//     it implicitly targets the receiving session's player. We
//     pre-seed SelfGuid from the character chosen at EnterWorld
//     so the initial property dump (which arrives BEFORE the
//     server-issued PlayerCreate) routes correctly.
//
// What's intentionally NOT here yet (Phase 5+):
//   - Spatial query API (EnumerateNearby) — defer until tactics
//     actually need it. Naive Vector3.Distance across CellIds
//     is wrong; doing it properly requires landblock
//     coordinate conversion.
//   - Full Motion body decode — only header fields surface
//     here; the polymorphic body is still raw bytes.

using System;
using System.Collections.Generic;
using HeadlessAcClient.Protocol.GameMessages;

namespace HeadlessAcClient.World;

internal sealed class WorldState
{
    private readonly Dictionary<uint, WorldObjectSnapshot> _objects = new();

    /// <summary>
    /// Highest byte-sequence we've seen on PrivateUpdatePropertyInt.
    /// On the wire, the byte sequence is shared across the whole
    /// property-update family (Private/Public * Int/Bool/Float/
    /// Int64/String) — they all advance a single 1-byte counter
    /// per session. We only DECODE PrivateUpdatePropertyInt today,
    /// so this field is effectively per-message. When the rest of
    /// the family comes online, they must AdvanceFamilyByteSeq
    /// through the same counter or stale-message gating will
    /// incorrectly accept resends after an undecoded sibling
    /// advanced the true server counter.
    /// Nullable because 0 is a valid sequence value.
    /// </summary>
    private byte? _selfPropertyByteSeq;

    /// <summary>
    /// Guid of the bot's own player. Set by SetSelf — typically
    /// pre-seeded from the chosen character guid at EnterWorld
    /// time so private property updates can be routed BEFORE
    /// PlayerCreate arrives.
    /// </summary>
    public uint? SelfGuid { get; private set; }

    /// <summary>
    /// Convenience accessor for the bot's own snapshot. Returns
    /// null if SelfGuid is unset OR if the snapshot for that
    /// guid hasn't been materialized yet.
    /// </summary>
    public WorldObjectSnapshot? Self
        => SelfGuid is uint g && _objects.TryGetValue(g, out var s) ? s : null;

    public int ObjectCount => _objects.Count;

    /// <summary>Read-only view of all known objects, keyed by guid.</summary>
    public IReadOnlyDictionary<uint, WorldObjectSnapshot> Objects => _objects;

    public WorldObjectSnapshot? TryGet(uint guid)
        => _objects.TryGetValue(guid, out var s) ? s : null;

    /// <summary>
    /// Pre-seed (or confirm) the bot's own guid. Safe to call
    /// repeatedly with the same value. If a different non-zero
    /// guid is passed after one is already set, logs the
    /// mismatch to stderr — would indicate a bug; the chosen
    /// guid should match what PlayerCreate eventually reports.
    /// </summary>
    public void SetSelf(uint guid)
    {
        if (guid == 0) return;
        if (SelfGuid is uint existing && existing != guid)
        {
            Console.Error.WriteLine(
                $"[worldstate] SelfGuid mismatch: had 0x{existing:X8}, got 0x{guid:X8}");
        }
        SelfGuid = guid;

        // Materialize an empty snapshot for the self guid so
        // PrivateUpdatePropertyInt has a place to land even if
        // ObjectCreate hasn't arrived yet for our own player.
        if (!_objects.ContainsKey(guid))
            _objects[guid] = new WorldObjectSnapshot(guid);
    }

    /// <summary>
    /// Dispatch a decoded message into the world-state model.
    /// Returns true if the message updated state, false if it
    /// was ignored (unknown type, stale sequence, etc.).
    /// </summary>
    public bool Apply(object? decoded)
    {
        return decoded switch
        {
            ObjectCreateMessage oc            => ApplyObjectCreate(oc),
            ObjectDeleteMessage od            => ApplyObjectDelete(od),
            UpdatePositionMessage up          => ApplyUpdatePosition(up),
            MotionMessage mm                  => ApplyMotion(mm),
            SetStateMessage ss                => ApplySetState(ss),
            PrivateUpdatePropertyIntMessage p => ApplyPrivatePropertyInt(p),
            PlayerCreateMessage pc            => ApplyPlayerCreate(pc),
            _                                 => false,
        };
    }

    private bool ApplyPlayerCreate(PlayerCreateMessage pc)
    {
        SetSelf(pc.Guid);
        return true;
    }

    /// <summary>
    /// Remove an object from the world snapshot. Stale-delete
    /// protection: drops the message if the incoming instance
    /// sequence is STRICTLY OLDER than the snapshot's current
    /// SeqInstance (wrap-aware). This handles the race where a
    /// respawn (new instance epoch) arrives before a stale
    /// delete from the previous epoch.
    ///
    /// If we never saw an ObjectCreate for this guid, the
    /// delete is a no-op (returns false). This is the common
    /// case where the bot enters the world after an object's
    /// brief lifetime, and the server flushes a delete for an
    /// object we never observed.
    ///
    /// Self-guid protection: refuse to delete our own player
    /// snapshot. A server-sent ObjectDelete for SelfGuid would
    /// indicate logout, which we model elsewhere — never wipe
    /// SelfGuid mid-session via the routine delete path.
    /// </summary>
    private bool ApplyObjectDelete(ObjectDeleteMessage od)
    {
        if (!_objects.TryGetValue(od.Guid, out var snap))
            return false;

        if (snap.SeqInstance is ushort cur
            && SequenceCompare.IsStrictlyNewer(cur, od.InstanceSequence))
            return false;

        if (SelfGuid is uint selfGuid && selfGuid == od.Guid)
        {
            Console.Error.WriteLine(
                $"[worldstate] ignoring ObjectDelete for SelfGuid 0x{od.Guid:X8}");
            return false;
        }

        _objects.Remove(od.Guid);
        return true;
    }

    private bool ApplyObjectCreate(ObjectCreateMessage oc)
    {
        // Merge semantics: refresh weenie/physics fields, but
        // preserve FirstSeen and PropertyInts. Sequence high-
        // water marks ADVANCE only — never reset to a lower
        // value the server happens to send in this ObjectCreate
        // (unless the instance epoch advanced; see below).
        var snap = GetOrCreateSnapshot(oc.Guid);

        // Two-level gate, mirroring ApplyUpdatePosition:
        // 1. If incoming instance is strictly older (wrap-aware)
        //    than what we already have, this is a stale resend
        //    that arrived after our world moved on — drop it
        //    entirely so it can't clobber identity/spatial fields
        //    with stale values.
        // 2. If incoming instance is strictly NEWER, the server
        //    has reset its per-instance counters, so reset ours
        //    too before AdvanceSeqX runs.
        // 3. If equal, we're in the same epoch; gate spatial
        //    fields on SeqObjectPosition so an out-of-order
        //    older ObjectCreate within the same epoch doesn't
        //    overwrite newer position state.
        if (!IsInstanceCurrentOrNewer(snap, oc.Physics.SeqObjectInstance))
            return false;

        var instanceAdvanced = IsInstanceStrictlyNewer(snap, oc.Physics.SeqObjectInstance);
        if (instanceAdvanced)
            snap.ResetForNewInstance();

        // Identity fields are intrinsic to the weenie/guid and
        // safe to refresh on every (non-stale-instance) ObjectCreate.
        snap.Name = oc.Weenie.Name;
        snap.WeenieClassId = oc.Weenie.WeenieClassId;
        snap.ItemType = oc.Weenie.ItemType;
        snap.WeenieFlags = (uint)oc.Weenie.Flags;
        snap.WeenieFlags2 = (uint)oc.Weenie.Flags2;

        // Spatial/physics fields are gated by SeqObjectPosition
        // within the same instance epoch. After a ResetForNewInstance,
        // snap.SeqPosition is null and the compare always accepts.
        var positionFresh = SequenceCompare.IsCurrentOrNewer(
            oc.Physics.SeqObjectPosition, snap.SeqPosition);

        if (positionFresh)
        {
            snap.PhysicsState = oc.Physics.PhysicsState;

            if (oc.Physics.Position is { } p)
            {
                snap.CellId = p.LandblockId;
                snap.Position = new System.Numerics.Vector3(p.X, p.Y, p.Z);
                snap.Rotation = new System.Numerics.Quaternion(p.RotationX, p.RotationY, p.RotationZ, p.RotationW);
            }

            if (oc.Physics.Velocity is { } v)
                snap.Velocity = v;
        }

        snap.AdvanceSeqInstance(oc.Physics.SeqObjectInstance);
        snap.AdvanceSeqPosition(oc.Physics.SeqObjectPosition);
        snap.AdvanceSeqTeleport(oc.Physics.SeqObjectTeleport);
        snap.AdvanceSeqForcePosition(oc.Physics.SeqObjectForcePosition);
        snap.AdvanceSeqState(oc.Physics.SeqObjectState);
        snap.AdvanceSeqMovement(oc.Physics.SeqObjectMovement);
        snap.AdvanceSeqServerControl(oc.Physics.SeqObjectServerControl);
        snap.AdvanceSeqVisualDesc(oc.Physics.SeqObjectVisualDesc);
        snap.AdvanceSeqVector(oc.Physics.SeqObjectVector);

        snap.Touch();
        return true;
    }

    private bool ApplyUpdatePosition(UpdatePositionMessage up)
    {
        var snap = GetOrCreateSnapshot(up.Guid);

        if (!IsInstanceCurrentOrNewer(snap, up.InstanceSequence))
            return false;

        // New instance epoch → reset per-instance counters before
        // gating, so the smaller position-sequence values the
        // server starts emitting in this epoch are accepted.
        if (IsInstanceStrictlyNewer(snap, up.InstanceSequence))
            snap.ResetForNewInstance();
        else if (!SequenceCompare.IsCurrentOrNewer(up.PositionSequence, snap.SeqPosition))
            return false;

        snap.CellId = up.CellId;
        snap.Position = up.Position;
        snap.Rotation = up.Rotation;
        if (up.Velocity is { } v) snap.Velocity = v;

        snap.AdvanceSeqInstance(up.InstanceSequence);
        snap.AdvanceSeqPosition(up.PositionSequence);
        snap.AdvanceSeqTeleport(up.TeleportSequence);
        snap.AdvanceSeqForcePosition(up.ForcePositionSequence);

        snap.Touch();
        return true;
    }

    private bool ApplyMotion(MotionMessage mm)
    {
        var snap = GetOrCreateSnapshot(mm.Guid);

        if (!IsInstanceCurrentOrNewer(snap, mm.InstanceSequence))
            return false;

        if (IsInstanceStrictlyNewer(snap, mm.InstanceSequence))
            snap.ResetForNewInstance();
        else if (!SequenceCompare.IsCurrentOrNewer(mm.MovementSequence, snap.SeqMovement))
            return false;

        snap.LastMovementType = mm.MovementType;
        snap.LastMotionFlags = mm.MotionFlags;
        snap.LastMotionStyle = mm.CurrentStyle;

        snap.AdvanceSeqInstance(mm.InstanceSequence);
        snap.AdvanceSeqMovement(mm.MovementSequence);
        snap.AdvanceSeqServerControl(mm.ServerControlSequence);

        snap.Touch();
        return true;
    }

    private bool ApplySetState(SetStateMessage ss)
    {
        var snap = GetOrCreateSnapshot(ss.Guid);

        if (!IsInstanceCurrentOrNewer(snap, ss.InstanceSequence))
            return false;

        if (IsInstanceStrictlyNewer(snap, ss.InstanceSequence))
            snap.ResetForNewInstance();
        else if (!SequenceCompare.IsCurrentOrNewer(ss.StateSequence, snap.SeqState))
            return false;

        snap.PhysicsState = ss.State;
        snap.AdvanceSeqInstance(ss.InstanceSequence);
        snap.AdvanceSeqState(ss.StateSequence);

        snap.Touch();
        return true;
    }

    private bool ApplyPrivatePropertyInt(PrivateUpdatePropertyIntMessage pup)
    {
        // No guid in wire format — implicitly targets the
        // receiving session's player. If SelfGuid is unset
        // (caller didn't pre-seed AND PlayerCreate hasn't
        // arrived), drop with a warning. Driver should pre-seed
        // at EnterWorld time.
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PrivateUpdatePropertyInt before SelfGuid known: " +
                $"prop={pup.PropertyName} val={pup.Value} (dropped)");
            return false;
        }

        // Family-wide byte-sequence check (the byte counter is
        // shared across all PrivateUpdateProperty* / Public-
        // UpdateProperty* messages — see PrivateUpdatePropertyInt-
        // Message header comment for the full family).
        if (!SequenceCompare.IsCurrentOrNewer(pup.Sequence, _selfPropertyByteSeq))
            return false;
        _selfPropertyByteSeq = pup.Sequence;

        var snap = GetOrCreateSnapshot(selfGuid);
        snap.PropertyInts ??= new Dictionary<uint, int>();
        snap.PropertyInts[pup.Property] = pup.Value;
        snap.Touch();
        return true;
    }

    private WorldObjectSnapshot GetOrCreateSnapshot(uint guid)
    {
        if (!_objects.TryGetValue(guid, out var snap))
        {
            snap = new WorldObjectSnapshot(guid);
            _objects[guid] = snap;
        }
        return snap;
    }

    /// <summary>
    /// True if <paramref name="incomingInstance"/> is at least
    /// as recent as the snapshot's current SeqInstance (or if
    /// SeqInstance is unset).
    /// </summary>
    private static bool IsInstanceCurrentOrNewer(WorldObjectSnapshot snap, ushort incomingInstance)
        => SequenceCompare.IsCurrentOrNewer(incomingInstance, snap.SeqInstance);

    /// <summary>
    /// True if the incoming instance is STRICTLY newer than the
    /// snapshot's SeqInstance (i.e., a new instance epoch).
    /// Returns false if SeqInstance is unset (no prior epoch
    /// to compare against).
    /// </summary>
    private static bool IsInstanceStrictlyNewer(WorldObjectSnapshot snap, ushort incomingInstance)
        => snap.SeqInstance is ushort cur
           && SequenceCompare.IsStrictlyNewer(incomingInstance, cur);

    /// <summary>
    /// Format a one-line debug summary suitable for periodic
    /// log output from the receive loop.
    /// </summary>
    public string FormatSummary()
    {
        var selfStr = "(unset)";
        if (Self is { } s)
        {
            var pos = s.CellId is uint c
                ? $"lb=0x{c:X8} xyz=({s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1})"
                : "(no pos)";
            selfStr = $"0x{s.Guid:X8} {pos}";
        }
        var props = Self?.PropertyInts?.Count ?? 0;
        return $"objects={_objects.Count} self={selfStr} selfProps={props}";
    }
}
