// SPDX-License-Identifier: AGPL-3.0-or-later
// WorldObjectSnapshot — mutable per-guid record of everything
// the bot has observed about a single WorldObject. Fed by
// WorldState.Apply from the decoded message stream.
//
// Why a mutable class instead of an immutable record:
//   - Update rate is high (348 UpdatePosition events in one
//     academy capture). Per-update record-copy would churn GC
//     without buying us anything — the receive loop is single-
//     threaded and no consumer caches snapshot references
//     across ticks.
//   - Setters are marked `internal` to signal that mutation is a
//     WorldState concern; consumers should only read.
//
// Sequence-counter design (per rubber-duck pass):
//   - All sequence high-water marks are nullable (`ushort?`).
//     Zero is a valid sequence value, so we can't use 0 as a
//     sentinel for "never seen".
//   - The Advance* methods implement the wrap-aware accept /
//     drop rule in one place (see SequenceCompare).

using System;
using System.Collections.Generic;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;

namespace HeadlessAcClient.World;

internal sealed class WorldObjectSnapshot
{
    public WorldObjectSnapshot(uint guid)
    {
        Guid = guid;
        FirstSeen = DateTimeOffset.UtcNow;
        LastUpdated = FirstSeen;
    }

    public uint Guid { get; }

    // Identity / weenie-header fields (populated by ObjectCreate).
    // Optional because UpdatePosition/Motion may arrive before
    // ObjectCreate for objects we joined-too-late to see spawn.
    public string? Name { get; internal set; }
    public uint? WeenieClassId { get; internal set; }
    public uint? ItemType { get; internal set; }
    public uint? WeenieFlags { get; internal set; }
    public uint? WeenieFlags2 { get; internal set; }
    // ObjectDescriptionFlag bits (Door=0x1000, Portal=0x40000, Corpse=0x2000,
    // LifeStone=0x4000, Vendor=0x200, Healer=0x10000, Openable=0x1, ...).
    // Surfaced as a raw uint so consumers can test bits without taking a
    // protocol-enum dependency. See Protocol/GameMessages/ObjectCreateFlags.cs.
    // This replaces the prior English-string heuristic `Name == "Door"` that
    // wouldn't survive a localized server or a custom door named "Iron Gate".
    public uint? ObjectDescriptionFlags { get; internal set; }
    public uint? ValidLocations { get; internal set; }
    // AmmoType (W_AMMO_TYPE) wire value. For a missile launcher it is the ammo
    // type the launcher fires; for ammunition it is the ammo's own type. The
    // server only lets a launcher and loaded ammo coexist when these match, so a
    // consumer can mechanically tell compatible ammo from incompatible. Null when
    // the item carries no AmmoType.
    public ushort? AmmoType { get; internal set; }
    public uint? CurrentWieldedLocation { get; internal set; }

    // Container / wielder linkage (populated by ObjectCreate from the
    // server's CreateObject serializer). For an un-equipped item carried
    // in the bot's inventory, the ObjectCreate carries ContainerGuid = self
    // and WielderGuid = null - i.e. "in your bag, not yet equipped". The
    // startup equip-from-inventory pass uses these to decide what to wield.
    public uint? ContainerGuid { get; internal set; }
    public uint? WielderGuid { get; internal set; }

    // ObjectCreate weenie-header Monarch field (WeenieHeaderFlag.Monarch): the
    // guid at the TOP of this object's allegiance tree, when present. For the
    // self player it decodes the bot's own allegiance membership: null/absent =
    // unaffiliated; == this object's own guid = it is its own monarch; any other
    // guid = it is a vassal under that monarch. Wire fact only; no interpretation.
    public uint? MonarchGuid { get; internal set; }

    // Spatial state.
    public uint? CellId { get; internal set; }
    public Vector3 Position { get; internal set; }
    public Quaternion Rotation { get; internal set; }
    public Vector3? Velocity { get; internal set; }

    // Physics / motion state.
    public uint? PhysicsState { get; internal set; }
    public MovementType? LastMovementType { get; internal set; }
    public MotionFlags? LastMotionFlags { get; internal set; }
    public ushort? LastMotionStyle { get; internal set; }

    // Sequence high-water marks. Per ACE's wire protocol these
    // are tracked per-object across multiple message families.
    // Nullable because 0 is a valid sequence value.
    public ushort? SeqInstance { get; private set; }
    public ushort? SeqPosition { get; private set; }
    public ushort? SeqTeleport { get; private set; }
    public ushort? SeqForcePosition { get; private set; }
    public ushort? SeqState { get; private set; }
    public ushort? SeqMovement { get; private set; }
    public ushort? SeqServerControl { get; private set; }
    public ushort? SeqVisualDesc { get; private set; }
    public ushort? SeqVector { get; private set; }

    // Property bag — only populated for the bot's own player
    // (PrivateUpdatePropertyInt is implicitly scoped to the
    // receiving session). Keyed by PropertyInt enum value.
    public Dictionary<uint, int>? PropertyInts { get; internal set; }

    // i64 property bag — only populated for the bot's own player
    // (PrivateUpdatePropertyInt64 is implicitly scoped to the
    // receiving session, like PropertyInts). Keyed by PropertyInt64
    // enum value; carries player XP (TotalExperience=1,
    // AvailableExperience=2). Null until the first 0x02CF arrives.
    public Dictionary<uint, long>? PropertyInt64s { get; internal set; }

    // Self character-sheet attributes + skills, seeded once from the login
    // PlayerDescription (0x0013) bundle. Only populated for the bot's own
    // player. Login-only for now: there is no discrete skill/attribute
    // update decode yet, so raised ranks here go stale after a Raise* until
    // relogin (AvailableExperience still updates live). Null until the
    // login bundle is seeded. See PdAttribute / PdSkill.
    public IReadOnlyList<PdAttribute>? SelfAttributes { get; internal set; }
    public IReadOnlyList<PdSkill>? SelfSkills { get; internal set; }


    // Self HEALTH vital — only populated for the bot's own player
    // (PrivateUpdateVital is implicitly scoped to the receiving
    // session, like PropertyInts). HealthCurrent is the latest
    // observed current HP; HealthMax is the peak Current ever
    // observed (a max proxy that avoids reimplementing AC's
    // Endurance-derived max-vital formula — on a full-health
    // login/respawn the first update seeds it to the true max).
    // Both null until the first PrivateUpdateVital arrives.
    public uint? HealthCurrent { get; internal set; }
    public uint? HealthMax { get; internal set; }

    // Bot's own current STAMINA + a peak-observed max proxy (same
    // approach as Health above — the wire vital messages carry only the
    // current, the max is derived server-side, so the peak observed
    // Current stands in for the max). Both null until the first stamina
    // PrivateUpdateVital arrives. Stamina is the melee/run sustain pool;
    // surfaced so a melee bot can see when it is depleted (swings weaken,
    // cannot run) the way it already sees Health.
    public uint? StaminaCurrent { get; internal set; }
    public uint? StaminaMax { get; internal set; }

    // Raw observed health TREND: true when the last accepted current
    // reading was strictly GREATER than the prior accepted reading
    // (regen/heal), false when it was lower or equal, null until a
    // second reading establishes a direction. A rising reading proves
    // the bot is BELOW its true max, so HealthMax (the peak-observed
    // proxy) is an under-estimate and any fraction computed from it
    // OVERSTATES health — the LLM uses this to avoid trusting a
    // misleading "100%" while regenerating from a sub-max login value.
    public bool? HealthRising { get; internal set; }

    // Lifecycle timestamps.
    public DateTimeOffset FirstSeen { get; }
    public DateTimeOffset LastUpdated { get; internal set; }

    internal void Touch() => LastUpdated = DateTimeOffset.UtcNow;

    /// <summary>
    /// Called when the SeqInstance counter strictly advances — a
    /// new "instance epoch" on the server resets the per-instance
    /// sequence counters (Position, Movement, State, Vector,
    /// Teleport, ServerControl, ForcePosition, VisualDesc). They
    /// must reset to null on our side too, otherwise the
    /// wrap-aware high-water mark would reject the (smaller)
    /// values the server sends in the new epoch.
    /// SeqInstance itself is NOT reset — it's about to be
    /// advanced by the caller.
    /// </summary>
    internal void ResetForNewInstance()
    {
        SeqPosition = null;
        SeqTeleport = null;
        SeqForcePosition = null;
        SeqState = null;
        SeqMovement = null;
        SeqServerControl = null;
        SeqVisualDesc = null;
        SeqVector = null;
    }

    // ---- Sequence advance helpers ----
    // Each one accepts the incoming value and updates the slot
    // only if the wrap-aware compare says "current or newer".
    // Centralizing the compare here keeps WorldState clean.

    internal void AdvanceSeqInstance(ushort v)        { if (SequenceCompare.IsCurrentOrNewer(v, SeqInstance))       SeqInstance = v; }
    internal void AdvanceSeqPosition(ushort v)        { if (SequenceCompare.IsCurrentOrNewer(v, SeqPosition))       SeqPosition = v; }
    internal void AdvanceSeqTeleport(ushort v)        { if (SequenceCompare.IsCurrentOrNewer(v, SeqTeleport))       SeqTeleport = v; }
    internal void AdvanceSeqForcePosition(ushort v)   { if (SequenceCompare.IsCurrentOrNewer(v, SeqForcePosition))  SeqForcePosition = v; }
    internal void AdvanceSeqState(ushort v)           { if (SequenceCompare.IsCurrentOrNewer(v, SeqState))          SeqState = v; }
    internal void AdvanceSeqMovement(ushort v)        { if (SequenceCompare.IsCurrentOrNewer(v, SeqMovement))       SeqMovement = v; }
    internal void AdvanceSeqServerControl(ushort v)   { if (SequenceCompare.IsCurrentOrNewer(v, SeqServerControl))  SeqServerControl = v; }
    internal void AdvanceSeqVisualDesc(ushort v)      { if (SequenceCompare.IsCurrentOrNewer(v, SeqVisualDesc))     SeqVisualDesc = v; }
    internal void AdvanceSeqVector(ushort v)          { if (SequenceCompare.IsCurrentOrNewer(v, SeqVector))         SeqVector = v; }
}
