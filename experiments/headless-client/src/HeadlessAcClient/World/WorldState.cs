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
//   - Full Motion body decode — only header fields surface
//     here; the polymorphic body is still raw bytes.

using System;
using System.Collections.Generic;
using HeadlessAcClient.Protocol.GameMessages;

namespace HeadlessAcClient.World;

/// <summary>
/// combat-damage-output: a snapshot of the current melee fight's
/// outcome counters, surfaced to the LLM as raw perception. The Motor
/// (HandshakeDriver) owns and updates this; the policy only reads it.
/// </summary>
/// <param name="TargetGuid">Guid of the locked combat target.</param>
/// <param name="TargetName">Best-known display name of the target (may be null early).</param>
/// <param name="SwingsLanded">Count of swings that LANDED (AttackerNotification) this fight.</param>
/// <param name="SwingsEvaded">Count of swings the target EVADED (EvasionAttackerNotification) this fight.</param>
/// <param name="DamageDealt">Cumulative damage dealt to the target this fight.</param>
internal sealed record CombatFightStatus(
    uint TargetGuid,
    string? TargetName,
    int SwingsLanded,
    int SwingsEvaded,
    uint DamageDealt);

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
    /// Highest byte-sequence seen on PrivateUpdateVital for the HEALTH
    /// vital. This is a SEPARATE counter from _selfPropertyByteSeq: the
    /// vital update uses a per-(type,vital) UpdateAttribute2ndLevel
    /// ByteSequence (see GameMessagePrivateUpdateVital), not the
    /// shared property-update family counter. Nullable because 0 is a
    /// valid sequence value.
    /// </summary>
    private byte? _selfHealthVitalByteSeq;

    /// <summary>
    /// Highest byte-sequence seen on PrivateUpdateAttribute2ndLevel
    /// (0x02E9) for the HEALTH vital. ACE keys ByteSequences by
    /// (type, vital); the current-level health packet keys on Health (2)
    /// while the 0x02E7 descriptor keys on MaxHealth (1), so these are
    /// DISTINCT counters and must be gated independently. Nullable
    /// because 0 is a valid sequence value.
    /// </summary>
    private byte? _selfHealthLevelByteSeq;

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

    /// <summary>
    /// combat-damage-output: the Motor's live view of the current melee
    /// fight, set/cleared by HandshakeDriver as it tracks the active
    /// combat lock. Null when not in combat. Surfaced verbatim to the
    /// LLM in the "## Combat readiness" prompt section as RAW perception
    /// (swings landed vs evaded, damage dealt) so the LLM can judge
    /// whether it is actually hurting the target and decide to disengage
    /// — source never makes that decision itself.
    /// </summary>
    public CombatFightStatus? CurrentFight { get; set; }

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
            PrivateUpdateVitalMessage v       => ApplyPrivateVital(v),
            PrivateUpdateAttribute2ndLevelMessage a => ApplyPrivateVitalLevel(a),
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
        snap.ObjectDescriptionFlags = (uint)oc.Weenie.DescriptionFlags;
        snap.ValidLocations = oc.Weenie.ValidLocations;
        snap.CurrentWieldedLocation = oc.Weenie.CurrentlyWieldedLocation;
        // ContainerGuid/WielderGuid: populate when the header carries
        // them. ObjectCreate for landscape items omits both; for items
        // in someone's bag the server sets ContainerGuid; for equipped
        // items the server sets WielderGuid. Treat absence as "not in
        // container / not wielded" by overwriting with null - the
        // server re-emits ObjectCreate when the linkage changes.
        snap.ContainerGuid = oc.Weenie.ContainerGuid;
        snap.WielderGuid = oc.Weenie.WielderGuid;

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

    /// <summary>
    /// Apply a PrivateUpdateVital (0x02E7). Like
    /// PrivateUpdatePropertyInt it has no guid and is implicitly
    /// scoped to the receiving session's player. We only track the
    /// HEALTH vital; Stamina/Mana updates are accepted (to advance the
    /// shared vital sequence) but otherwise ignored. HealthMax is the
    /// peak Current observed so the projection can compute a fraction
    /// without AC's Endurance-derived max-vital formula.
    /// </summary>
    private bool ApplyPrivateVital(PrivateUpdateVitalMessage v)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PrivateUpdateVital before SelfGuid known: " +
                $"vital=0x{v.Vital:X} current={v.Current} (dropped)");
            return false;
        }

        // Only the health vital drives self-health state.
        if (!v.IsHealth)
            return false;

        // Per-(type,vital) byte-sequence gate — drop stale/reordered
        // health updates. Separate counter from the property family.
        if (!SequenceCompare.IsCurrentOrNewer(v.Sequence, _selfHealthVitalByteSeq))
            return false;
        _selfHealthVitalByteSeq = v.Sequence;

        WriteSelfHealth(selfGuid, v.Current);
        return true;
    }

    /// <summary>
    /// Apply a PrivateUpdateAttribute2ndLevel (0x02E9) - the per-tick
    /// CURRENT-LEVEL vital update. This is the timely source for the
    /// bot's own current HP (damage/regen/death/respawn). Only the HEALTH
    /// vital is tracked; here health is keyed by Health (2), unlike the
    /// 0x02E7 descriptor (keyed by MaxHealth (1)), and uses its own
    /// distinct sequence counter.
    /// </summary>
    private bool ApplyPrivateVitalLevel(PrivateUpdateAttribute2ndLevelMessage a)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PrivateUpdateAttribute2ndLevel before SelfGuid known: " +
                $"vital=0x{a.Vital:X} current={a.Current} (dropped)");
            return false;
        }

        if (!a.IsHealth)
            return false;

        if (!SequenceCompare.IsCurrentOrNewer(a.Sequence, _selfHealthLevelByteSeq))
            return false;
        _selfHealthLevelByteSeq = a.Sequence;

        WriteSelfHealth(selfGuid, a.Current);
        return true;
    }

    /// <summary>
    /// Write the bot's current health onto the self snapshot and update
    /// the peak-observed max. Shared by both health-bearing wire messages
    /// (0x02E7 descriptor and 0x02E9 current-level). Peak-observed max
    /// seeds to the true max on a full-health login/respawn, rises on
    /// level-up at full health, and never shrinks from taking damage -
    /// avoiding a reimplementation of AC's Endurance-derived max formula.
    /// </summary>
    private void WriteSelfHealth(uint selfGuid, uint current)
    {
        var snap = GetOrCreateSnapshot(selfGuid);
        snap.HealthCurrent = current;
        snap.HealthMax = snap.HealthMax is uint prevMax && prevMax >= current
            ? prevMax
            : current;
        snap.Touch();
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

    // ---- Spatial queries ----
    //
    // Public API for the future Motor/Tactics layer. All methods
    // return eagerly-materialized lists — never lazy enumerators
    // over the underlying dictionary — so a caller iterating
    // results while another callback applies new messages can't
    // trip a "Collection was modified" exception.
    //
    // All queries skip snapshots without a CellId (no spatial
    // state yet) and skip the origin itself (matched by Guid,
    // not reference identity, so callers can pass a copy/stub).

    /// <summary>
    /// Enumerate every snapshot in the world with a known position,
    /// paired with its squared distance to <paramref name="origin"/>.
    /// Excludes the origin itself and any object without a CellId.
    /// Returns empty if origin has no CellId.
    /// Results are NOT sorted — use NearestN if you need ordering.
    /// </summary>
    public IReadOnlyList<(WorldObjectSnapshot Object, float SquaredDistance)>
        EnumerateNearby(WorldObjectSnapshot origin)
    {
        if (origin is null) throw new ArgumentNullException(nameof(origin));
        if (origin.CellId is not uint originCell)
            return Array.Empty<(WorldObjectSnapshot, float)>();

        var result = new List<(WorldObjectSnapshot, float)>(_objects.Count);
        foreach (var snap in _objects.Values)
        {
            if (snap.Guid == origin.Guid) continue;
            if (snap.CellId is not uint cell) continue;
            var d2 = WorldDistance.SquaredDistanceBetween(
                originCell, origin.Position, cell, snap.Position);
            result.Add((snap, d2));
        }
        return result;
    }

    /// <summary>
    /// All objects within <paramref name="radius"/> game units of
    /// <paramref name="origin"/>. Order is unspecified — call
    /// NearestN if you need distance-sorted output.
    /// Throws on negative or NaN radius.
    /// </summary>
    public IReadOnlyList<WorldObjectSnapshot>
        WithinRadius(WorldObjectSnapshot origin, float radius)
    {
        if (origin is null) throw new ArgumentNullException(nameof(origin));
        if (float.IsNaN(radius) || radius < 0f)
            throw new ArgumentOutOfRangeException(nameof(radius),
                $"radius must be non-negative and finite (got {radius})");

        var radiusSq = radius * radius;
        var nearby = EnumerateNearby(origin);
        var result = new List<WorldObjectSnapshot>(nearby.Count);
        foreach (var (snap, d2) in nearby)
        {
            if (d2 <= radiusSq) result.Add(snap);
        }
        return result;
    }

    /// <summary>
    /// The <paramref name="count"/> objects closest to
    /// <paramref name="origin"/>, sorted ascending by distance.
    /// Tie-break: guid ascending (deterministic).
    /// If fewer objects than <paramref name="count"/> have known
    /// positions, returns all of them.
    /// Throws on negative count.
    /// </summary>
    public IReadOnlyList<WorldObjectSnapshot>
        NearestN(WorldObjectSnapshot origin, int count)
    {
        if (origin is null) throw new ArgumentNullException(nameof(origin));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count),
                $"count must be non-negative (got {count})");
        if (count == 0)
            return Array.Empty<WorldObjectSnapshot>();

        var nearby = EnumerateNearby(origin);
        if (nearby.Count == 0)
            return Array.Empty<WorldObjectSnapshot>();

        // Stable sort by (squared-distance asc, guid asc).
        var sorted = new List<(WorldObjectSnapshot Obj, float D2)>(nearby);
        sorted.Sort((a, b) =>
        {
            var cmp = a.D2.CompareTo(b.D2);
            return cmp != 0 ? cmp : a.Obj.Guid.CompareTo(b.Obj.Guid);
        });

        var take = Math.Min(count, sorted.Count);
        var result = new List<WorldObjectSnapshot>(take);
        for (var i = 0; i < take; i++) result.Add(sorted[i].Obj);
        return result;
    }

    /// <summary>
    /// All objects whose ItemType bitmask intersects
    /// <paramref name="itemTypeMask"/>. AC's ItemType is a bit
    /// field — `Creature | MeleeWeapon | ...` — so a mask of
    /// multiple bits matches objects of any of those types.
    /// Objects without an ItemType (no ObjectCreate yet) are
    /// excluded. A zero mask matches nothing.
    /// </summary>
    public IReadOnlyList<WorldObjectSnapshot> OfType(uint itemTypeMask)
    {
        if (itemTypeMask == 0)
            return Array.Empty<WorldObjectSnapshot>();

        var result = new List<WorldObjectSnapshot>();
        foreach (var snap in _objects.Values)
        {
            if (snap.ItemType is uint t && (t & itemTypeMask) != 0)
                result.Add(snap);
        }
        return result;
    }
}
