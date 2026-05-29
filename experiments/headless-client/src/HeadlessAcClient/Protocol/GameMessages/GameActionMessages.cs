// SPDX-License-Identifier: AGPL-3.0-or-later
//
// GameAction outbound message family. GameActions are the
// general-purpose client→server command channel after world entry,
// wrapped in the 0xF7B1 game-message envelope.
//
// Wire layout (after the 4-byte 0xF7B1 envelope opcode):
//   u32  actionSequence   (server-side TODO: verify; safe to use 1)
//   u32  actionOpcode     (GameActionType, e.g. 0x00A1 LoginComplete)
//   ...  payload          (varies per action; LoginComplete is empty)
//
// References:
//   Source/ACE.Server/Network/GameAction/GameActionPacket.cs:9-17
//     [GameMessage(GameMessageOpcode.GameAction, WorldConnected)]
//     reads u32 sequence + u32 opcode, dispatches to action handler.
//   Source/ACE.Server/Network/GameAction/GameActionType.cs:54
//     LoginComplete = 0x00A1
//   Source/ACE.Server/Network/GameAction/Actions/GameActionLoginComplete.cs
//     Handler calls session.Player.OnTeleportComplete() to clear the
//     Teleporting flag (purple-portal-haze state). Without this the
//     character stays "in-portal" forever and cannot be interacted
//     with by other players or NPCs.
//
// IMPORTANT: LoginComplete MUST be sent only after the server has
// fully bound session.Player. Sending too early NPEs the handler.
// The canonical signal is PlayerCreate (0xF746) for our own guid —
// once we see ourselves in the world-state firehose, the server-
// side player object exists and OnTeleportComplete is safe.

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace HeadlessAcClient.Protocol.GameMessages;

internal enum GameActionType : uint
{
    LoginComplete       = 0x00A1,
    PutItemInContainer  = 0x0019,
    Use                 = 0x0036,
    MoveToState         = 0xF61C,
    AutonomousPosition  = 0xF753,
    TargetedMeleeAttack = 0x0008,
    GetAndWieldItem     = 0x001A,
    ChangeCombatMode    = 0x0053,
    QueryHealth         = 0x01BF,
}

/// <summary>
/// Mirror of ACE's RawMotionFlags
/// (Source/ACE.Server/Network/Enum/RawMotionFlags.cs).
/// The low 11 bits of <c>RawMotionState.PackedFlags</c> encode a
/// bitmask of which optional fields follow; the high 21 bits encode
/// <c>CommandListLength</c>. Fields are serialized in strict
/// numeric flag order: 0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x40, 0x80,
/// 0x100, 0x200, 0x400.
/// </summary>
[Flags]
internal enum RawMotionFlags : uint
{
    None            = 0x000,
    CurrentHoldKey  = 0x001,
    CurrentStyle    = 0x002,
    ForwardCommand  = 0x004,
    ForwardHoldKey  = 0x008,
    ForwardSpeed    = 0x010,
    SideStepCommand = 0x020,
    SideStepHoldKey = 0x040,
    SideStepSpeed   = 0x080,
    TurnCommand     = 0x100,
    TurnHoldKey     = 0x200,
    TurnSpeed       = 0x400,
}

/// <summary>
/// Subset of <c>ACE.Entity.Enum.MotionStance</c> values the spike
/// uses. NonCombat is the default standing stance for an unequipped
/// player. Full enum at
/// <c>Source/ACE.Entity/Enum/MotionStance.cs</c>.
/// </summary>
internal enum MotionStance : uint
{
    Invalid   = 0x80000000,
    NonCombat = 0x8000003D,
}

/// <summary>
/// Subset of <c>ACE.Entity.Enum.MotionCommand</c> values the spike
/// uses. Full enum at
/// <c>Source/ACE.Entity/Enum/MotionCommand.cs</c>.
/// </summary>
internal enum MotionCommand : uint
{
    Invalid     = 0x00000000,
    Ready       = 0x41000003,
    WalkForward = 0x45000005,
    RunForward  = 0x44000007,
}

/// <summary>
/// Mirror of <c>ACE.Entity.Enum.HoldKey</c>.
/// </summary>
internal enum HoldKey : uint
{
    Invalid = 0x0,
    None    = 0x1,
    Run     = 0x2,
}

internal static class GameActionMessage
{
    public const int HeaderSize = 4 + 4 + 4;  // envelope + actionSeq + actionOpcode

    /// <summary>
    /// Pack a payload-less GameAction (e.g. LoginComplete). The
    /// returned byte count is exactly <see cref="HeaderSize"/>.
    /// </summary>
    public static int Pack(Span<byte> dest, GameActionType action, uint actionSequence = 1)
    {
        if (dest.Length < HeaderSize)
            throw new ArgumentException($"buffer too small: need {HeaderSize}, got {dest.Length}");

        var cursor = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(
            dest.Slice(cursor),
            (uint)GameMessageOpcode.GameAction);
        cursor += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), actionSequence);
        cursor += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), (uint)action);
        cursor += 4;

        return cursor;
    }

    /// <summary>
    /// Write a 32-byte Position structure (cell+xyz+quaternion).
    /// Matches the wire layout consumed by ACE's
    /// <c>Position(BinaryReader)</c> constructor: u32 cell, then
    /// three float xyz, then four float quaternion in
    /// <b>W, X, Y, Z</b> order (NOT System.Numerics's X,Y,Z,W).
    /// Returns 32 (bytes written).
    /// </summary>
    public static int WritePosition(
        Span<byte> dest, uint cellId, Vector3 pos, Quaternion rot)
    {
        if (dest.Length < 32)
            throw new ArgumentException($"buffer too small: need 32, got {dest.Length}");

        var cursor = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), cellId);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), pos.X);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), pos.Y);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), pos.Z);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), rot.W);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), rot.X);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), rot.Y);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), rot.Z);
        cursor += 4;
        return cursor;
    }
}

internal static class GameActionLoginCompleteMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize;  // 12 bytes

    /// <summary>
    /// Pack the LoginComplete GameAction. Server handler:
    /// Source/ACE.Server/Network/GameAction/Actions/GameActionLoginComplete.cs
    /// </summary>
    public static int Pack(Span<byte> dest)
        => GameActionMessage.Pack(dest, GameActionType.LoginComplete);
}

/// <summary>
/// Use (0x0036). The single most useful interact opcode: sent by the
/// client when the user double-clicks a world object. Server handler
/// is <c>Source/ACE.Server/Network/GameAction/Actions/GameActionUseItem.cs</c>
/// which calls <c>session.Player.HandleActionUseItem(itemGuid)</c>.
/// HandleActionUseItem walks the player to the target if needed, then
/// invokes the target's ActivationResponse: items get picked up,
/// doors toggle, portals teleport, NPCs initiate dialog, etc.
///
/// Payload after the GameAction header is a single u32: the target
/// object's guid. No rotation, no position — the server resolves the
/// target from the in-world guid registry.
/// </summary>
internal static class GameActionUseMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, uint targetGuid, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.Use, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), targetGuid);
        cursor += 4;
        return cursor;
    }
}

/// <summary>
/// PutItemInContainer (0x0019). The pickup / move-between-containers
/// network event. Real clients emit this when the user drags an item
/// from the world (or another container) into a container; passing
/// the player's own guid as the container picks it up into the main
/// inventory.
///
/// Server handler at
/// <c>Source/ACE.Server/Network/GameAction/Actions/GameActionPutItemInContainer.cs</c>
/// calls <c>session.Player.HandleActionPutItemInContainer(itemGuid,
/// containerGuid, placement)</c>. That method enqueues a server-side
/// MoveTo chain to walk the player into UseRadius of the item, then
/// performs the pickup callback (broadcasts ObjectDelete on the item,
/// adds it to the player's inventory, emits Sound.PickUpItem).
///
/// Payload after the 12B GameAction header:
///   u32 itemGuid
///   u32 containerGuid
///   i32 placement       (0 = first available slot)
/// = 24 bytes total.
/// </summary>
internal static class GameActionPutItemInContainerMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 12;  // 24 bytes

    public static int Pack(Span<byte> dest, uint itemGuid, uint containerGuid, int placement = 0, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.PutItemInContainer, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), itemGuid);     cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), containerGuid); cursor += 4;
        BinaryPrimitives.WriteInt32LittleEndian (dest.Slice(cursor), placement);     cursor += 4;
        return cursor;
    }
}

/// <summary>
/// ChangeCombatMode (0x0053). Switches the player between non-combat
/// idle stance and an active combat stance (Melee / Missile / Magic).
/// Required before any TargetedMeleeAttack / TargetedMissileAttack /
/// CreateSpell game action — the server's HandleAction* methods all
/// validate that <c>Player.CombatMode</c> matches the action.
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionChangeCombatMode.cs</c>
///   reads u32 newMode and calls
///   <c>session.Player.HandleActionChangeCombatMode((CombatMode)newMode)</c>.
///
/// Payload after the 12B GameAction header:
///   u32 newCombatMode  (CombatMode enum:
///                       NonCombat=1, Melee=2, Missile=4, Magic=8)
/// = 16 bytes total.
///
/// On success the server broadcasts a Motion update flipping the
/// player into the new stance and emits SetState for nearby
/// observers. Subsequent attack actions become legal.
/// </summary>
internal static class GameActionChangeCombatModeMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, uint newCombatMode, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.ChangeCombatMode, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), newCombatMode); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// TargetedMeleeAttack (0x0008). Issues a melee strike against a
/// specific creature guid using the currently wielded weapon (or
/// unarmed strike). Requires the player to already be in
/// CombatMode.Melee — see <see cref="GameActionChangeCombatModeMessage"/>.
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionTargetedMeleeAttack.cs</c>
///   reads u32 targetGuid + u32 attackHeight + f32 powerLevel and
///   calls <c>session.Player.HandleActionTargetedMeleeAttack(
///       targetGuid, attackHeight, powerLevel)</c>.
///
/// Payload after the 12B GameAction header:
///   u32 targetGuid
///   u32 attackHeight   (AttackHeight enum: High=1, Medium=2, Low=3)
///   f32 powerLevel     ([0.0, 1.0] — 0.5 is the "half power" default
///                       a real client sends for an unmodified click)
/// = 24 bytes total.
///
/// The server walks the player into UseRadius, plays the swing
/// animation, rolls hit/miss/damage based on the wielded weapon and
/// the target's defenses, and broadcasts UpdateHealth (the target's
/// PrivateUpdatePropertyInt for Health) plus various combat events.
/// </summary>
internal static class GameActionTargetedMeleeAttackMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 12;  // 24 bytes

    public static int Pack(Span<byte> dest, uint targetGuid, uint attackHeight, float powerLevel, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.TargetedMeleeAttack, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), targetGuid);    cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), attackHeight);  cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), powerLevel);    cursor += 4;
        return cursor;
    }
}

/// <summary>
/// GetAndWieldItem (0x001A). Equips an item that is currently in the
/// player's inventory (or picks it up from the world and equips it in
/// one combined action) into the specified equipment slot.
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionGetAndWieldItem.cs</c>
///   reads u32 itemGuid + i32 location and calls
///   <c>session.Player.HandleActionGetAndWieldItem(itemGuid, location)</c>.
///
/// Payload after the 12B GameAction header:
///   u32 itemGuid
///   i32 equipLocation  (EquipMask enum, e.g. HeadWear=0x01,
///                       HandWear=0x20, ChestArmor=0x200,
///                       MeleeWeapon=0x100000, etc.)
/// = 20 bytes total.
///
/// The chosen location must be compatible with the item's
/// ValidLocations property OR the server rejects with a
/// WeenieErrorWithString. For unambiguously-slotted gear (gauntlets,
/// helmet, etc.) the location is typically a single bit; for
/// multi-slot items (rings, bracelets) the caller must pick one.
/// </summary>
internal static class GameActionGetAndWieldItemMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 8;  // 20 bytes

    public static int Pack(Span<byte> dest, uint itemGuid, int equipLocation, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.GetAndWieldItem, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), itemGuid);       cursor += 4;
        BinaryPrimitives.WriteInt32LittleEndian (dest.Slice(cursor), equipLocation);  cursor += 4;
        return cursor;
    }
}

/// <summary>
/// QueryHealth (0x01BF). Sent by a real client when the player
/// selects (left-clicks) a creature in the world — registers that
/// creature as the player's "appraisal / health-query target" so
/// the server emits ongoing UpdateHealth broadcasts as that
/// target's health changes.
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionQueryHealth.cs</c>
///   reads u32 objectGuid and calls
///   <c>session.Player.HandleActionQueryHealth(objectGuid)</c>.
///   That method sets <c>selectedTarget</c> + <c>HealthQueryTarget</c>
///   on the Player AND immediately invokes
///   <c>creature.QueryHealth(session)</c> which emits one UpdateHealth
///   right away. Subsequent UpdateHealth broadcasts come from
///   <c>Player_Vitals.HandleTargetVitals()</c> (called every
///   Player_Tick) — those only fire while <c>selectedTarget != null</c>.
///
/// Payload after the 12B GameAction header:
///   u32 objectGuid
/// = 16 bytes total.
///
/// Sending QueryHealth before/with TargetedMeleeAttack is what makes
/// damage visible to a headless client. Without it the swings still
/// land (server-side) but the client sees zero UpdateHealth events
/// and can't tell when the target died.
/// </summary>
internal static class GameActionQueryHealthMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, uint objectGuid, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.QueryHealth, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), objectGuid); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// AutonomousPosition (0xF753). Sent by a real client roughly once
/// per second WHILE moving to report its current authoritative
/// position to the server. Server handler:
/// Source/ACE.Server/Network/GameAction/Actions/GameActionAutonomousPosition.cs
///
/// Server-side behavior (read carefully — drives our gating):
///   - Reads Position (32B), four ushort sequences, one byte
///     ContactLongJump, then aligns to dword.
///   - Calls session.Player.SetRequestedLocation(position) IFF
///     <c>!session.Player.Teleporting</c>. So we MUST wait until
///     the server has processed our prior LoginComplete (which
///     clears Teleporting) before this is observable. Sending
///     while Teleporting=true is a silent no-op.
///   - SetRequestedLocation defaults broadcast=true, so a
///     successful AP eventually emits a server-originated
///     UpdatePosition broadcast back to us. That's how we
///     confirm acceptance — observable inbound UpdatePosition
///     with our own guid.
///
/// Wire layout (after GameAction 12B header):
///   Position    32B    (u32 cell, 3 floats xyz, 4 floats wxyz)
///   InstanceSequence       u16 (2B)
///   ServerControlSequence  u16 (2B)
///   TeleportSequence       u16 (2B)
///   ForcePositionSequence  u16 (2B)
///   ContactLongJump        u8  (1B)
///   align to 4B            +3B padding
/// Total payload: 12 + 32 + 8 + 4 = 56 bytes.
///
/// IMPORTANT: the four ushort sequences are echoes of the latest
/// inbound values the server sent us for our own guid. Do not
/// advance them client-side; the server-side handler does not
/// validate but ACE's own server-side autonomous position
/// constructor mirrors the current ObjectInstance / ServerControl
/// / Teleport / ForcePosition counters.
/// </summary>
internal static class GameActionAutonomousPositionMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 32 + 8 + 4;  // 56

    public static int Pack(
        Span<byte> dest,
        uint cellId, Vector3 pos, Quaternion rot,
        ushort instanceSequence,
        ushort serverControlSequence,
        ushort teleportSequence,
        ushort forcePositionSequence,
        bool contact,
        uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(
            dest, GameActionType.AutonomousPosition, actionSequence);

        cursor += GameActionMessage.WritePosition(dest.Slice(cursor), cellId, pos, rot);

        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), instanceSequence);
        cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), serverControlSequence);
        cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), teleportSequence);
        cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), forcePositionSequence);
        cursor += 2;

        dest[cursor++] = contact ? (byte)1 : (byte)0;

        // Align to 4-byte boundary. Cursor is currently at PackedSize - 3.
        dest[cursor++] = 0;
        dest[cursor++] = 0;
        dest[cursor++] = 0;

        return cursor;
    }
}

/// <summary>
/// Bag of optional <c>RawMotionState</c> fields. Each <c>(Flag, Value)</c>
/// pair is emitted IFF the flag bit is set; emitted in strict
/// numeric flag order regardless of how this struct is populated.
/// Speed fields are floats; all others are u32s.
/// </summary>
internal readonly record struct RawMotionStatePayload(
    RawMotionFlags Flags,
    HoldKey        CurrentHoldKey,
    MotionStance   CurrentStyle,
    MotionCommand  ForwardCommand,
    HoldKey        ForwardHoldKey,
    float          ForwardSpeed,
    MotionCommand  SidestepCommand,
    HoldKey        SidestepHoldKey,
    float          SidestepSpeed,
    MotionCommand  TurnCommand,
    HoldKey        TurnHoldKey,
    float          TurnSpeed,
    ushort         CommandListLength = 0)
{
    /// <summary>
    /// Convenience constructor for the most common case: forward
    /// motion with stance + holdkey + command + speed.
    /// </summary>
    public static RawMotionStatePayload ForwardMotion(
        HoldKey holdKey, MotionStance stance, MotionCommand command, float speed)
        => new(
            RawMotionFlags.CurrentHoldKey | RawMotionFlags.CurrentStyle |
            RawMotionFlags.ForwardCommand | RawMotionFlags.ForwardSpeed,
            CurrentHoldKey:  holdKey,
            CurrentStyle:    stance,
            ForwardCommand:  command,
            ForwardHoldKey:  default,
            ForwardSpeed:    speed,
            SidestepCommand: default,
            SidestepHoldKey: default,
            SidestepSpeed:   0f,
            TurnCommand:     default,
            TurnHoldKey:     default,
            TurnSpeed:       0f);

    /// <summary>
    /// "Stop" intent — explicitly cancel forward motion by sending
    /// the stance + a None holdkey + an Invalid forward command. A
    /// real client sends something close to this on key-release.
    /// </summary>
    public static RawMotionStatePayload Stop(MotionStance stance)
        => new(
            RawMotionFlags.CurrentHoldKey | RawMotionFlags.CurrentStyle |
            RawMotionFlags.ForwardCommand,
            CurrentHoldKey:  HoldKey.None,
            CurrentStyle:    stance,
            ForwardCommand:  MotionCommand.Invalid,
            ForwardHoldKey:  default,
            ForwardSpeed:    0f,
            SidestepCommand: default,
            SidestepHoldKey: default,
            SidestepSpeed:   0f,
            TurnCommand:     default,
            TurnHoldKey:     default,
            TurnSpeed:       0f);
}

/// <summary>
/// MoveToState (0xF61C). Sent by a real client on every movement
/// key press AND release to update the server's view of player
/// motion intent. Server handler:
/// Source/ACE.Server/Network/GameAction/Actions/GameActionMoveToState.cs
///
/// Important server-side caveat (per rubber-duck on Phase 5b):
///   - <c>Player.OnMoveToState</c> is gated by <c>Player.FastTick</c>,
///     which is FALSE for normal NPK players (returns true only
///     when <c>IsPKType</c>). So the server-side physics
///     integration that would actually MOVE a normal char in
///     response to one of these messages is short-circuited.
///   - <c>BroadcastMovement(moveToState)</c> is called
///     unconditionally though, so we DO see a Motion broadcast
///     for our own guid back at us proving wire-format acceptance.
///   - For continuous server-driven physics movement we would need
///     a PK char OR a different code path. Phase 5b's success
///     criterion is therefore "Motion broadcast observed", not
///     "UpdatePosition stream observed".
///
/// Wire layout (after the 12B GameAction header):
///   u32 packedFlags        (low 11 bits = RawMotionFlags,
///                           high 21 bits = CommandListLength)
///   ... flagged fields ... (u32 or float, in strict numeric flag
///                           order: 0x1 → 0x2 → 0x4 → ... → 0x400)
///   32B Position           (full cell + xyz + W,X,Y,Z quaternion)
///   8B sequences           (instance, serverControl, teleport, forcePos)
///   1B ContactLongJump     (bit 0 = Contact, bit 1 = StandingLongJump)
///   align to 4B boundary
/// </summary>
internal static class GameActionMoveToStateMessage
{
    /// <summary>
    /// Size of the fixed (non-RawMotionState) suffix: Position +
    /// sequences + ContactLongJump byte + 3B align.
    /// </summary>
    private const int FixedSuffixSize = 32 + 8 + 4;  // 44

    /// <summary>
    /// Total packed bytes for a given motion state. Does NOT include
    /// any MotionItems (CommandListLength is always 0 for the
    /// spike's needs).
    /// </summary>
    public static int CalcPackedSize(RawMotionFlags flags)
        => GameActionMessage.HeaderSize  // 12
         + 4                              // packedFlags
         + CountFlaggedFieldBytes(flags)
         + FixedSuffixSize;

    public static int Pack(
        Span<byte> dest,
        RawMotionStatePayload motion,
        uint cellId, Vector3 pos, Quaternion rot,
        ushort instanceSequence,
        ushort serverControlSequence,
        ushort teleportSequence,
        ushort forcePositionSequence,
        bool contact,
        bool standingLongJump = false,
        uint actionSequence = 1)
    {
        var needed = CalcPackedSize(motion.Flags);
        if (dest.Length < needed)
            throw new ArgumentException($"buffer too small: need {needed}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(
            dest, GameActionType.MoveToState, actionSequence);

        // packedFlags: bottom 11 bits = flags, top 21 bits = commandListLength.
        // Per rubber-duck: mask to 0x7FF defensively even though the
        // RawMotionFlags enum's highest value is 0x400 — guards
        // against accidentally setting reserved bits.
        var packedFlags = ((uint)motion.Flags & 0x7FF) | ((uint)motion.CommandListLength << 11);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), packedFlags);
        cursor += 4;

        // Emit fields in strict numeric flag order. The server's
        // RawMotionState(BinaryReader) parser reads them in this
        // exact order; any other order will misparse.
        if ((motion.Flags & RawMotionFlags.CurrentHoldKey) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.CurrentHoldKey);
        if ((motion.Flags & RawMotionFlags.CurrentStyle) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.CurrentStyle);
        if ((motion.Flags & RawMotionFlags.ForwardCommand) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.ForwardCommand);
        if ((motion.Flags & RawMotionFlags.ForwardHoldKey) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.ForwardHoldKey);
        if ((motion.Flags & RawMotionFlags.ForwardSpeed) != 0)
            cursor += WriteF32(dest.Slice(cursor), motion.ForwardSpeed);
        if ((motion.Flags & RawMotionFlags.SideStepCommand) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.SidestepCommand);
        if ((motion.Flags & RawMotionFlags.SideStepHoldKey) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.SidestepHoldKey);
        if ((motion.Flags & RawMotionFlags.SideStepSpeed) != 0)
            cursor += WriteF32(dest.Slice(cursor), motion.SidestepSpeed);
        if ((motion.Flags & RawMotionFlags.TurnCommand) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.TurnCommand);
        if ((motion.Flags & RawMotionFlags.TurnHoldKey) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.TurnHoldKey);
        if ((motion.Flags & RawMotionFlags.TurnSpeed) != 0)
            cursor += WriteF32(dest.Slice(cursor), motion.TurnSpeed);

        cursor += GameActionMessage.WritePosition(dest.Slice(cursor), cellId, pos, rot);

        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), instanceSequence);      cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), serverControlSequence); cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), teleportSequence);      cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), forcePositionSequence); cursor += 2;

        byte contactLongJump = 0;
        if (contact)           contactLongJump |= 0x1;
        if (standingLongJump)  contactLongJump |= 0x2;
        dest[cursor++] = contactLongJump;

        // Align to 4-byte boundary. After ContactLongJump cursor sits
        // 1 byte into the trailing dword, so 3 bytes of zero padding.
        dest[cursor++] = 0;
        dest[cursor++] = 0;
        dest[cursor++] = 0;

        return cursor;
    }

    private static int WriteU32(Span<byte> dest, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest, value);
        return 4;
    }

    private static int WriteF32(Span<byte> dest, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dest, value);
        return 4;
    }

    private static int CountFlaggedFieldBytes(RawMotionFlags flags)
    {
        var count = 0;
        // Every flag emits 4 bytes (either u32 or float). 11 possible flags.
        for (var bit = 0; bit < 11; bit++)
        {
            if (((uint)flags & (1u << bit)) != 0)
                count += 4;
        }
        return count;
    }
}
