// SPDX-License-Identifier: AGPL-3.0-or-later
// ObjectCreate (0xF745) - server tells client that a world object
// has come into the player's vision range.
//
// Wire layout: see docs/research/headless-client/spec/07-world-state.md.
// Sections (in order): u32 guid, ModelData, PhysicsData,
// WeenieHeader, final Align().
//
// Reference encoder:
//   ACE-bots/Source/ACE.Server/WorldObjects/WorldObject_Networking.cs:56-221.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record ObjectCreateMessage(
    uint Guid,
    ObjectModelData Model,
    ObjectPhysicsData Physics,
    ObjectWeenieHeader Weenie);

internal sealed record ObjectModelData(
    uint? PaletteId,
    IReadOnlyList<SubPaletteEntry> SubPalettes,
    IReadOnlyList<TextureChangeEntry> TextureChanges,
    IReadOnlyList<AnimPartChangeEntry> AnimPartChanges);

internal sealed record SubPaletteEntry(uint SubPaletteId, byte Offset, byte Length);
internal sealed record TextureChangeEntry(byte PartIndex, uint OldTexture, uint NewTexture);
internal sealed record AnimPartChangeEntry(byte Index, uint AnimationId);

internal sealed record ObjectPhysicsData(
    PhysicsDescriptionFlag DescriptionFlags,
    uint PhysicsState,
    // Movement is opaque for now (Phase 4.4 will decode the body)
    ReadOnlyMemory<byte>? MovementBody,
    bool? MovementIsAutonomous,
    uint? AnimationFramePlacement,
    ObjectPosition? Position,
    uint? MotionTableId,
    uint? SoundTableId,
    uint? PhysicsTableId,
    uint? SetupTableId,
    uint? ParentWielderId,
    uint? ParentLocation,
    IReadOnlyList<ChildEntry>? Children,
    float? ObjScale,
    float? Friction,
    float? Elasticity,
    float? Translucency,
    Vector3? Velocity,
    Vector3? Acceleration,
    Vector3? Omega,
    uint? DefaultScriptId,
    float? DefaultScriptIntensity,
    ushort SeqObjectPosition,
    ushort SeqObjectMovement,
    ushort SeqObjectState,
    ushort SeqObjectVector,
    ushort SeqObjectTeleport,
    ushort SeqObjectServerControl,
    ushort SeqObjectForcePosition,
    ushort SeqObjectVisualDesc,
    ushort SeqObjectInstance);

internal sealed record ObjectPosition(
    uint LandblockId,
    float X, float Y, float Z,
    float RotationW, float RotationX, float RotationY, float RotationZ);

internal sealed record ChildEntry(uint Guid, int LocationId);

internal sealed record ObjectWeenieHeader(
    WeenieHeaderFlag Flags,
    WeenieHeaderFlag2 Flags2,
    string Name,
    uint WeenieClassId,
    uint IconId,
    uint ItemType,
    ObjectDescriptionFlag DescriptionFlags,
    // Optional fields - present only if the corresponding flag bit is set
    string? PluralName,
    byte? ItemsCapacity,
    byte? ContainersCapacity,
    ushort? AmmoType,
    uint? Value,
    uint? Usable,
    float? UseRadius,
    uint? TargetType,
    uint? UiEffects,
    sbyte? CombatUse,
    ushort? Structure,
    ushort? MaxStructure,
    ushort? StackSize,
    ushort? MaxStackSize,
    uint? ContainerGuid,
    uint? WielderGuid,
    uint? ValidLocations,
    uint? CurrentlyWieldedLocation,
    uint? Priority,
    byte? RadarBlipColor,
    byte? RadarBehavior,
    ushort? PScript,
    float? Workmanship,
    ushort? Burden,
    ushort? Spell,
    uint? HouseOwner,
    uint? HookItemTypes,
    uint? MonarchGuid,
    ushort? HookType,
    uint? IconOverlay,
    uint? IconUnderlay,
    uint? MaterialType,
    int? CooldownId,
    double? CooldownDuration,
    uint? PetOwner);

internal static class ObjectCreateDecoder
{
    private const uint TYPE_PALETTE  = 0x04000000;
    private const uint TYPE_TEXTURE  = 0x05000000;
    private const uint TYPE_ICON     = 0x06000000;
    private const uint TYPE_ANIM     = 0x01000000;
    private const byte MODEL_DATA_MARKER = 0x11;

    public static ObjectCreateMessage? Decode(ReadOnlySpan<byte> payload)
    {
        // Copy out for MessageReader (which holds ReadOnlyMemory<byte>).
        // ObjectCreate is not the hot path on the read side; the
        // copy lets us keep the decoder interface uniform.
        var buf = new byte[payload.Length];
        payload.CopyTo(buf);
        var r = new MessageReader(buf);

        // Skip opcode (4 bytes). Alignment math stays anchored at
        // offset 0 of the message buffer, matching the encoder's
        // BaseStream.Length anchor (which includes the opcode).
        r.SkipBytes(4);

        try
        {
            var guid = r.ReadGuid();
            var model = DecodeModelData(r);
            var physics = DecodePhysicsData(r);
            var weenie = DecodeWeenieHeader(r);
            // Final Align happens inside DecodeWeenieHeader.

            return new ObjectCreateMessage(guid, model, physics, weenie);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ObjectCreateDecoder] decode failed at offset {r.Cursor}/{r.Length}: {ex.Message}");
            // Dump the FULL payload hex so we can manually trace
            // the bug without re-running. Phase 4.3 only.
            var sb = new System.Text.StringBuilder();
            sb.Append("[ObjectCreateDecoder] payload hex:");
            for (var i = 0; i < buf.Length; i++)
            {
                if ((i % 16) == 0) sb.Append($"\n  {i,4:D4}: ");
                sb.Append($"{buf[i]:X2} ");
            }
            Console.WriteLine(sb.ToString());
            return null;
        }
    }

    private static ObjectModelData DecodeModelData(MessageReader r)
    {
        var marker = r.ReadU8();
        if (marker != MODEL_DATA_MARKER)
            throw new InvalidOperationException(
                $"ModelData marker mismatch: expected 0x{MODEL_DATA_MARKER:X2}, got 0x{marker:X2}");

        var subPaletteCount = r.ReadU8();
        var textureChangeCount = r.ReadU8();
        var animPartChangeCount = r.ReadU8();

        uint? paletteId = null;
        if (subPaletteCount > 0)
            paletteId = r.ReadPackedDwordOfKnownType(TYPE_PALETTE);

        var subPalettes = new List<SubPaletteEntry>(subPaletteCount);
        for (var i = 0; i < subPaletteCount; i++)
        {
            var spId = r.ReadPackedDwordOfKnownType(TYPE_PALETTE);
            var off = r.ReadU8();
            var len = r.ReadU8();
            subPalettes.Add(new SubPaletteEntry(spId, off, len));
        }

        var textureChanges = new List<TextureChangeEntry>(textureChangeCount);
        for (var i = 0; i < textureChangeCount; i++)
        {
            var partIndex = r.ReadU8();
            var oldTex = r.ReadPackedDwordOfKnownType(TYPE_TEXTURE);
            var newTex = r.ReadPackedDwordOfKnownType(TYPE_TEXTURE);
            textureChanges.Add(new TextureChangeEntry(partIndex, oldTex, newTex));
        }

        var animParts = new List<AnimPartChangeEntry>(animPartChangeCount);
        for (var i = 0; i < animPartChangeCount; i++)
        {
            var index = r.ReadU8();
            var animId = r.ReadPackedDwordOfKnownType(TYPE_ANIM);
            animParts.Add(new AnimPartChangeEntry(index, animId));
        }

        r.Align4();

        return new ObjectModelData(paletteId, subPalettes, textureChanges, animParts);
    }

    private static ObjectPhysicsData DecodePhysicsData(MessageReader r)
    {
        var flags = (PhysicsDescriptionFlag)r.ReadU32();
        var physicsState = r.ReadU32();

        ReadOnlyMemory<byte>? movementBody = null;
        bool? movementIsAutonomous = null;
        uint? animPlacement = null;

        if ((flags & PhysicsDescriptionFlag.Movement) != 0)
        {
            var length = (int)r.ReadU32();
            if (length > 0)
            {
                var bodySpan = r.SkipBytes(length);
                var arr = new byte[length];
                bodySpan.CopyTo(arr);
                movementBody = arr;
                movementIsAutonomous = r.ReadU32() != 0;
            }
            else
            {
                movementBody = ReadOnlyMemory<byte>.Empty;
                movementIsAutonomous = null;
            }
        }
        else if ((flags & PhysicsDescriptionFlag.AnimationFrame) != 0)
        {
            animPlacement = r.ReadU32();
        }

        ObjectPosition? position = null;
        if ((flags & PhysicsDescriptionFlag.Position) != 0)
        {
            var landblock = r.ReadU32();
            var x = r.ReadF32();
            var y = r.ReadF32();
            var z = r.ReadF32();
            var rw = r.ReadF32();
            var rx = r.ReadF32();
            var ry = r.ReadF32();
            var rz = r.ReadF32();
            position = new ObjectPosition(landblock, x, y, z, rw, rx, ry, rz);
        }

        uint? mTable  = (flags & PhysicsDescriptionFlag.MTable)  != 0 ? r.ReadU32() : null;
        uint? sTable  = (flags & PhysicsDescriptionFlag.STable)  != 0 ? r.ReadU32() : null;
        uint? peTable = (flags & PhysicsDescriptionFlag.PeTable) != 0 ? r.ReadU32() : null;
        uint? cSetup  = (flags & PhysicsDescriptionFlag.CSetup)  != 0 ? r.ReadU32() : null;

        uint? parentWielder = null;
        uint? parentLoc = null;
        if ((flags & PhysicsDescriptionFlag.Parent) != 0)
        {
            parentWielder = r.ReadU32();
            parentLoc = r.ReadU32();
        }

        List<ChildEntry>? children = null;
        if ((flags & PhysicsDescriptionFlag.Children) != 0)
        {
            var k = r.ReadI32();
            children = new List<ChildEntry>(k);
            for (var i = 0; i < k; i++)
            {
                var cguid = r.ReadU32();
                var cloc = r.ReadI32();
                children.Add(new ChildEntry(cguid, cloc));
            }
        }

        float? objScale     = (flags & PhysicsDescriptionFlag.ObjScale)     != 0 ? r.ReadF32() : null;
        float? friction     = (flags & PhysicsDescriptionFlag.Friction)     != 0 ? r.ReadF32() : null;
        float? elasticity   = (flags & PhysicsDescriptionFlag.Elasticity)   != 0 ? r.ReadF32() : null;
        float? translucency = (flags & PhysicsDescriptionFlag.Translucency) != 0 ? r.ReadF32() : null;
        Vector3? velocity     = (flags & PhysicsDescriptionFlag.Velocity)     != 0 ? r.ReadVector3() : null;
        Vector3? acceleration = (flags & PhysicsDescriptionFlag.Acceleration) != 0 ? r.ReadVector3() : null;
        Vector3? omega        = (flags & PhysicsDescriptionFlag.Omega)        != 0 ? r.ReadVector3() : null;
        uint? defaultScript           = (flags & PhysicsDescriptionFlag.DefaultScript)          != 0 ? (uint?)r.ReadU32() : null;
        float? defaultScriptIntensity = (flags & PhysicsDescriptionFlag.DefaultScriptIntensity) != 0 ? (float?)r.ReadF32() : null;

        // 9 mandatory sequence timestamps. Each is a UShortSequence on
        // the server (Source/ACE.Server/Network/Sequence/SequenceManager.cs:171
        // returns UShortSequence.CurrentBytes — 2 bytes, not 4).
        var seqPos = r.ReadU16();
        var seqMov = r.ReadU16();
        var seqSta = r.ReadU16();
        var seqVec = r.ReadU16();
        var seqTel = r.ReadU16();
        var seqSvc = r.ReadU16();
        var seqFpo = r.ReadU16();
        var seqVis = r.ReadU16();
        var seqIns = r.ReadU16();

        r.Align4();

        return new ObjectPhysicsData(
            flags, physicsState, movementBody, movementIsAutonomous, animPlacement,
            position, mTable, sTable, peTable, cSetup, parentWielder, parentLoc, children,
            objScale, friction, elasticity, translucency, velocity, acceleration, omega,
            defaultScript, defaultScriptIntensity,
            seqPos, seqMov, seqSta, seqVec, seqTel, seqSvc, seqFpo, seqVis, seqIns);
    }

    private static ObjectWeenieHeader DecodeWeenieHeader(MessageReader r)
    {
        var flags = (WeenieHeaderFlag)r.ReadU32();
        var name = r.ReadString16L();
        var wcid = r.ReadPackedDword();
        var iconId = r.ReadPackedDwordOfKnownType(TYPE_ICON);
        var itemType = r.ReadU32();
        var objDescFlags = (ObjectDescriptionFlag)r.ReadU32();
        r.Align4();

        var flags2 = WeenieHeaderFlag2.None;
        if ((objDescFlags & ObjectDescriptionFlag.IncludesSecondHeader) != 0)
            flags2 = (WeenieHeaderFlag2)r.ReadU32();

        // Fail-fast on HouseRestrictions (variable-length RestrictionDB
        // body would corrupt cursor if skipped blindly).
        if ((flags & WeenieHeaderFlag.HouseRestrictions) != 0)
            throw new NotSupportedException(
                "ObjectCreate with HouseRestrictions flag set. RestrictionDB decode not implemented (Phase 4.3+).");

        // Conditional fields in ENCODER order (matches
        // Source/ACE.Server/WorldObjects/WorldObject_Networking.cs
        // SerializeCreateObject, NOT WeenieHeaderFlag bit order).
        var pluralName              = (flags & WeenieHeaderFlag.PluralName)               != 0 ? r.ReadString16L() : null;
        byte?   itemsCapacity       = (flags & WeenieHeaderFlag.ItemsCapacity)            != 0 ? (byte?)r.ReadU8() : null;
        byte?   containersCapacity  = (flags & WeenieHeaderFlag.ContainersCapacity)       != 0 ? (byte?)r.ReadU8() : null;
        ushort? ammoType            = (flags & WeenieHeaderFlag.AmmoType)                 != 0 ? (ushort?)r.ReadU16() : null;
        uint?   value               = (flags & WeenieHeaderFlag.Value)                    != 0 ? (uint?)r.ReadU32() : null;
        uint?   usable              = (flags & WeenieHeaderFlag.Usable)                   != 0 ? (uint?)r.ReadU32() : null;
        float?  useRadius           = (flags & WeenieHeaderFlag.UseRadius)                != 0 ? (float?)r.ReadF32() : null;
        uint?   targetType          = (flags & WeenieHeaderFlag.TargetType)               != 0 ? (uint?)r.ReadU32() : null;
        uint?   uiEffects           = (flags & WeenieHeaderFlag.UiEffects)                != 0 ? (uint?)r.ReadU32() : null;
        sbyte?  combatUse           = (flags & WeenieHeaderFlag.CombatUse)                != 0 ? (sbyte?)r.ReadI8() : null;
        ushort? structure           = (flags & WeenieHeaderFlag.Structure)                != 0 ? (ushort?)r.ReadU16() : null;
        ushort? maxStructure        = (flags & WeenieHeaderFlag.MaxStructure)             != 0 ? (ushort?)r.ReadU16() : null;
        ushort? stackSize           = (flags & WeenieHeaderFlag.StackSize)                != 0 ? (ushort?)r.ReadU16() : null;
        ushort? maxStackSize        = (flags & WeenieHeaderFlag.MaxStackSize)             != 0 ? (ushort?)r.ReadU16() : null;
        uint?   containerGuid       = (flags & WeenieHeaderFlag.Container)                != 0 ? (uint?)r.ReadU32() : null;
        uint?   wielderGuid         = (flags & WeenieHeaderFlag.Wielder)                  != 0 ? (uint?)r.ReadU32() : null;
        uint?   validLocations      = (flags & WeenieHeaderFlag.ValidLocations)           != 0 ? (uint?)r.ReadU32() : null;
        uint?   currentlyWielded    = (flags & WeenieHeaderFlag.CurrentlyWieldedLocation) != 0 ? (uint?)r.ReadU32() : null;
        uint?   priority            = (flags & WeenieHeaderFlag.Priority)                 != 0 ? (uint?)r.ReadU32() : null;
        byte?   radarBlipColor      = (flags & WeenieHeaderFlag.RadarBlipColor)           != 0 ? (byte?)r.ReadU8() : null;
        byte?   radarBehavior       = (flags & WeenieHeaderFlag.RadarBehavior)            != 0 ? (byte?)r.ReadU8() : null;
        ushort? pScript             = (flags & WeenieHeaderFlag.PScript)                  != 0 ? (ushort?)r.ReadU16() : null;
        float?  workmanship         = (flags & WeenieHeaderFlag.Workmanship)              != 0 ? (float?)r.ReadF32() : null;
        ushort? burden              = (flags & WeenieHeaderFlag.Burden)                   != 0 ? (ushort?)r.ReadU16() : null;
        ushort? spell               = (flags & WeenieHeaderFlag.Spell)                    != 0 ? (ushort?)r.ReadU16() : null;
        uint?   houseOwner          = (flags & WeenieHeaderFlag.HouseOwner)               != 0 ? (uint?)r.ReadU32() : null;
        // HouseRestrictions intentionally absent (fail-fast above).
        uint?   hookItemTypes       = (flags & WeenieHeaderFlag.HookItemTypes)            != 0 ? (uint?)r.ReadU32() : null;
        uint?   monarchGuid         = (flags & WeenieHeaderFlag.Monarch)                  != 0 ? (uint?)r.ReadU32() : null;
        ushort? hookType            = (flags & WeenieHeaderFlag.HookType)                 != 0 ? (ushort?)r.ReadU16() : null;
        uint?   iconOverlay         = (flags & WeenieHeaderFlag.IconOverlay)              != 0 ? (uint?)r.ReadPackedDwordOfKnownType(TYPE_ICON) : null;
        uint?   iconUnderlay        = (flags2 & WeenieHeaderFlag2.IconUnderlay)           != 0 ? (uint?)r.ReadPackedDwordOfKnownType(TYPE_ICON) : null;
        uint?   materialType        = (flags & WeenieHeaderFlag.MaterialType)             != 0 ? (uint?)r.ReadU32() : null;
        int?    cooldownId          = (flags2 & WeenieHeaderFlag2.Cooldown)               != 0 ? (int?)r.ReadI32() : null;
        double? cooldownDuration    = (flags2 & WeenieHeaderFlag2.CooldownDuration)       != 0 ? (double?)r.ReadF64() : null;
        uint?   petOwner            = (flags2 & WeenieHeaderFlag2.PetOwner)               != 0 ? (uint?)r.ReadU32() : null;

        r.Align4();

        return new ObjectWeenieHeader(
            flags, flags2, name, wcid, iconId, itemType, objDescFlags,
            pluralName, itemsCapacity, containersCapacity, ammoType, value, usable, useRadius,
            targetType, uiEffects, combatUse, structure, maxStructure, stackSize, maxStackSize,
            containerGuid, wielderGuid, validLocations, currentlyWielded, priority,
            radarBlipColor, radarBehavior, pScript, workmanship, burden, spell, houseOwner,
            hookItemTypes, monarchGuid, hookType, iconOverlay, iconUnderlay, materialType,
            cooldownId, cooldownDuration, petOwner);
    }
}
