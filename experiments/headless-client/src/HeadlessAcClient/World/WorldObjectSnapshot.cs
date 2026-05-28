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
    public uint? ValidLocations { get; internal set; }
    public uint? CurrentWieldedLocation { get; internal set; }

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
