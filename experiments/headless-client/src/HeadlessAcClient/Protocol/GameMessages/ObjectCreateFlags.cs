// SPDX-License-Identifier: AGPL-3.0-or-later
// Flag enums consumed by the ObjectCreate (0xF745) decoder.
// Mirrors of:
//   ACE.Entity/Enum/WeenieHeaderFlags.cs  (WeenieHeaderFlag, WeenieHeaderFlag2)
//   ACE.Entity/Enum/ObjectDescriptionFlag.cs
//   ACE.Entity/Enum/PhysicsDescriptionFlag.cs
//
// These are READ-ONLY for the client - we consume ObjectCreate
// messages from the server but never emit them.

using System;

namespace HeadlessAcClient.Protocol.GameMessages;

[Flags]
internal enum WeenieHeaderFlag : uint
{
    None                        = 0x00000000,
    PluralName                  = 0x00000001,
    ItemsCapacity               = 0x00000002,
    ContainersCapacity          = 0x00000004,
    Value                       = 0x00000008,
    Usable                      = 0x00000010,
    UseRadius                   = 0x00000020,
    Monarch                     = 0x00000040,
    UiEffects                   = 0x00000080,
    AmmoType                    = 0x00000100,
    CombatUse                   = 0x00000200,
    Structure                   = 0x00000400,
    MaxStructure                = 0x00000800,
    StackSize                   = 0x00001000,
    MaxStackSize                = 0x00002000,
    Container                   = 0x00004000,
    Wielder                     = 0x00008000,
    ValidLocations              = 0x00010000,
    CurrentlyWieldedLocation    = 0x00020000,
    Priority                    = 0x00040000,
    TargetType                  = 0x00080000,
    RadarBlipColor              = 0x00100000,
    Burden                      = 0x00200000,
    Spell                       = 0x00400000,
    RadarBehavior               = 0x00800000,
    Workmanship                 = 0x01000000,
    HouseOwner                  = 0x02000000,
    HouseRestrictions           = 0x04000000,
    PScript                     = 0x08000000,
    HookType                    = 0x10000000,
    HookItemTypes               = 0x20000000,
    IconOverlay                 = 0x40000000,
    MaterialType                = 0x80000000,
}

[Flags]
internal enum WeenieHeaderFlag2 : uint
{
    None              = 0x00,
    IconUnderlay      = 0x01,
    Cooldown          = 0x02,
    CooldownDuration  = 0x04,
    PetOwner          = 0x08,
}

[Flags]
internal enum ObjectDescriptionFlag : uint
{
    None                   = 0x00000000,
    Openable               = 0x00000001,
    Inscribable            = 0x00000002,
    Stuck                  = 0x00000004,
    Player                 = 0x00000008,
    Attackable             = 0x00000010,
    PlayerKiller           = 0x00000020,
    HiddenAdmin            = 0x00000040,
    UiHidden               = 0x00000080,
    Book                   = 0x00000100,
    Vendor                 = 0x00000200,
    PkSwitch               = 0x00000400,
    NpkSwitch              = 0x00000800,
    Door                   = 0x00001000,
    Corpse                 = 0x00002000,
    LifeStone              = 0x00004000,
    Food                   = 0x00008000,
    Healer                 = 0x00010000,
    Lockpick               = 0x00020000,
    Portal                 = 0x00040000,
    Admin                  = 0x00100000,
    FreePkStatus           = 0x00200000,
    ImmuneCellRestrictions = 0x00400000,
    RequiresPackSlot       = 0x00800000,
    Retained               = 0x01000000,
    PkLiteStatus           = 0x02000000,
    IncludesSecondHeader   = 0x04000000,
    BindStone              = 0x08000000,
    VolatileRare           = 0x10000000,
    WieldOnUse             = 0x20000000,
    WieldLeft              = 0x40000000,
}

[Flags]
internal enum PhysicsDescriptionFlag : uint
{
    None                   = 0x000000,
    CSetup                 = 0x000001,
    MTable                 = 0x000002,
    Velocity               = 0x000004,
    Acceleration           = 0x000008,
    Omega                  = 0x000010,
    Parent                 = 0x000020,
    Children               = 0x000040,
    ObjScale               = 0x000080,
    Friction               = 0x000100,
    Elasticity             = 0x000200,
    Timestamps             = 0x000400,
    STable                 = 0x000800,
    PeTable                = 0x001000,
    DefaultScript          = 0x002000,
    DefaultScriptIntensity = 0x004000,
    Position               = 0x008000,
    Movement               = 0x010000,
    AnimationFrame         = 0x020000,
    Translucency           = 0x040000,
}
