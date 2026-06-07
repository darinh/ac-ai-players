// SPDX-License-Identifier: AGPL-3.0-or-later
// Slice W.1 (#86) — PickerSelection unit tests.
//
// Audit-safe INVARIANT tests (per rubber-duck #7): we don't assert
// "NPCs beat doors" or any other strategic ordering. We assert:
//   - Distance is the only ordering signal (no type bumps).
//   - Self-bag / self-wielded items are excluded (loop-prevention).
//   - Duplicate-name pickups remain ELIGIBLE (picker-name-respawn-
//     audit removed the source-side anti-respawn filter — whether a
//     duplicate-named pickup is worth re-collecting is the LLM's
//     call now, surfaced via ExplorationCandidate.PickedNameCount).
//   - The schema-only picker NEVER prefers an NPC over a closer
//     door (the FORBIDDEN ladder is gone).
//   - The schema-only picker NEVER prefers a corpse over a closer
//     pickup (the FORBIDDEN loot bump is gone).

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class PickerSelectionTests
{
    private const uint SelfGuid = 0x50000005;
    private const uint CellId = 0x12340001;

    // ItemType bits (mirrors enum values used in HandshakeDriver).
    private const uint ItemTypeCreature = 0x00000010;
    private const uint ItemTypeMeleeWeapon = 0x00000001;

    // ObjectDescriptionFlag bits.
    private const uint FlagDoor = (uint)ObjectDescriptionFlag.Door;
    private const uint FlagCorpse = (uint)ObjectDescriptionFlag.Corpse;

    private static WorldObjectSnapshot Self() =>
        new(SelfGuid) { CellId = CellId, Position = Vector3.Zero, Name = "Bot" };

    private static WorldObjectSnapshot Snap(
        uint guid, string name, float x,
        uint? itemType = null, uint? descFlags = null,
        uint? containerGuid = null, uint? wielderGuid = null,
        uint? cellId = null) =>
        new(guid)
        {
            Name = name,
            CellId = cellId ?? CellId,
            Position = new Vector3(x, 0, 0),
            ItemType = itemType,
            ObjectDescriptionFlags = descFlags,
            ContainerGuid = containerGuid,
            WielderGuid = wielderGuid,
        };

    [Fact]
    public void Empty_ReturnsNull()
    {
        var picked = PickerSelection.PickNearest(
            new List<WorldObjectSnapshot>(),
            Self(), SelfGuid);
        Assert.Null(picked);
    }

    [Fact]
    public void NearestWins_DistanceOnly()
    {
        var a = Snap(0x100, "Far",   x: 30f);
        var b = Snap(0x101, "Mid",   x: 10f);
        var c = Snap(0x102, "Close", x: 3f);

        var picked = PickerSelection.PickNearest(
            new[] { a, b, c }, Self(), SelfGuid);
        Assert.NotNull(picked);
        Assert.Equal(0x102u, picked!.Guid);
    }

    [Fact]
    public void NoTypeBump_NpcBehindCloserDoor()
    {
        // FORBIDDEN before W.1: NPC (prio 0) would beat door (prio 2)
        // regardless of distance. After W.1 the closer door must win.
        var npc  = Snap(0x200, "Greeter", x: 20f, itemType: ItemTypeCreature);
        var door = Snap(0x201, "Door",    x: 5f,  descFlags: FlagDoor);
        var picked = PickerSelection.PickNearest(
            new[] { npc, door }, Self(), SelfGuid);
        Assert.Equal(0x201u, picked!.Guid);
    }

    [Fact]
    public void NoCorpseBump_PickupBeatsFartherCorpse()
    {
        // FORBIDDEN before W.1: corpse (prio 0) would beat pickup
        // (prio 3) regardless of distance.
        var corpse = Snap(0x300, "Corpse of Golem", x: 25f, descFlags: FlagCorpse);
        var apple  = Snap(0x301, "Apple",           x: 4f,  itemType: ItemTypeMeleeWeapon);
        var picked = PickerSelection.PickNearest(
            new[] { corpse, apple }, Self(), SelfGuid);
        Assert.Equal(0x301u, picked!.Guid);
    }

    [Fact]
    public void ExcludesItemsInSelfBag()
    {
        // ContainerGuid == self → item is in our inventory.
        // Even though it's at distance 0, the picker must not
        // re-target items already in the bag.
        var inBag = Snap(0x400, "Healing Kit", x: 0f,
            itemType: ItemTypeMeleeWeapon, containerGuid: SelfGuid);
        var farNpc = Snap(0x401, "Greeter", x: 50f,
            itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearest(
            new[] { inBag, farNpc }, Self(), SelfGuid);
        Assert.Equal(0x401u, picked!.Guid);
    }

    [Fact]
    public void DuplicateNamePickup_StillEligible()
    {
        // picker-name-respawn-audit: the source-side anti-respawn
        // filter is GONE. A pickup whose Name was picked before is no
        // longer dropped by the Motor — it's the nearest eligible
        // candidate and the picker returns it. "Is a duplicate worth
        // re-collecting?" is the LLM's call now (surfaced as
        // picked_name_count). Inverse of the old ...AntiRespawn test.
        var freshApple = Snap(0x500, "Apple", x: 3f,
            itemType: ItemTypeMeleeWeapon);
        var npc = Snap(0x501, "Greeter", x: 10f,
            itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearest(
            new[] { freshApple, npc }, Self(), SelfGuid);
        // Apple is closer and is no longer excluded — it wins.
        Assert.Equal(0x500u, picked!.Guid);
    }

    [Fact]
    public void ExcludesItemsWieldedBySelf()
    {
        // WielderGuid == self → item is currently equipped (weapon,
        // shield, jewellery). Regression from sliceW01 run-02: a
        // re-login flow surfaces an already-wielded Training Spadone
        // at d=0u with no ContainerGuid and no satisfied-wcid record
        // (server doesn't replay WieldObject for items wielded across
        // the session boundary). The picker MUST drop it via
        // WielderGuid==self or it bricks on a 0-unit walk to itself.
        var wielded = Snap(0xA00, "Training Spadone", x: 0f,
            itemType: ItemTypeMeleeWeapon, wielderGuid: SelfGuid);
        var farNpc  = Snap(0xA01, "Greeter", x: 50f,
            itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearest(
            new[] { wielded, farNpc }, Self(), SelfGuid);
        Assert.NotNull(picked);
        Assert.Equal(0xA01u, picked!.Guid);
    }

    [Fact]
    public void ItemsWieldedByOthersAreNotExcluded()
    {
        // A weapon held by another character (NPC/player) is a
        // legitimate world object the picker may walk toward —
        // the wielder filter is "ME specifically", not "anyone".
        const uint otherWielder = 0x50000099u;
        var theirSword = Snap(0xB00, "Bronze Long Sword", x: 4f,
            itemType: ItemTypeMeleeWeapon, wielderGuid: otherWielder);
        var farNpc = Snap(0xB01, "Greeter", x: 30f,
            itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearest(
            new[] { theirSword, farNpc }, Self(), SelfGuid);
        Assert.NotNull(picked);
        Assert.Equal(0xB00u, picked!.Guid);
    }

    // =============================================================
    // Slice W.2 (#87) — PickNearestFallback invariant tests.
    //
    // The fallback runs when the in-range queue is empty. It MUST
    // be just as mechanical as PickNearest plus an addressability
    // clip (same landblock). NO type-based bumps; NO visited-door
    // backtrack preference. Strategic exploration is the LLM's job.
    //
    // Notes:
    //  - The "visited" filter is applied by the CALLER (HandshakeDriver),
    //    not by PickNearestFallback. The fallback method itself
    //    accepts whatever candidate pool you hand it. That's tested
    //    indirectly via the caller's behavior in integration; here
    //    we verify the per-method invariants only.
    //  - SelfLandblock = (SelfCellId & 0xFFFF0000u). Different
    //    landblocks differ in the high 16 bits.
    // =============================================================

    private const uint SelfLandblock = CellId & 0xFFFF0000u;

    [Fact]
    public void Fallback_Empty_ReturnsNull()
    {
        var picked = PickerSelection.PickNearestFallback(
            new List<WorldObjectSnapshot>(),
            Self(), SelfGuid, SelfLandblock);
        Assert.Null(picked);
    }

    [Fact]
    public void Fallback_NearestWins_NoTypeBumps()
    {
        // Pure distance — an NPC at 30u must lose to a closer door
        // at 5u (the FORBIDDEN ladder used to bump NPC ahead).
        var npc  = Snap(0x100, "Greeter", x: 30f, itemType: ItemTypeCreature);
        var door = Snap(0x101, "Door",    x: 5f,  descFlags: FlagDoor);
        var pickup = Snap(0x102, "Apple", x: 12f, itemType: ItemTypeMeleeWeapon);
        var picked = PickerSelection.PickNearestFallback(
            new[] { npc, door, pickup },
            Self(), SelfGuid, SelfLandblock);
        Assert.NotNull(picked);
        Assert.Equal(0x101u, picked!.Guid);
    }

    [Fact]
    public void Fallback_ExcludesItemsInSelfBag()
    {
        // ContainerGuid == self → item is in our inventory. Anti-
        // self-targeting also applies to the fallback (a 0-unit
        // walk would brick motion same as the in-range case).
        var inBag = Snap(0x200, "Healing Kit", x: 0f,
            itemType: ItemTypeMeleeWeapon, containerGuid: SelfGuid);
        var farNpc = Snap(0x201, "Greeter", x: 50f,
            itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearestFallback(
            new[] { inBag, farNpc },
            Self(), SelfGuid, SelfLandblock);
        Assert.Equal(0x201u, picked!.Guid);
    }

    [Fact]
    public void Fallback_ExcludesItemsWieldedBySelf()
    {
        // Mirrors the in-range W.1 wielded-self regression fix —
        // the fallback must also drop items WielderGuid==self or
        // a re-login can brick on a 0-unit walk to the wielded
        // weapon.
        var wielded = Snap(0x300, "Training Spadone", x: 0f,
            itemType: ItemTypeMeleeWeapon, wielderGuid: SelfGuid);
        var farNpc  = Snap(0x301, "Greeter", x: 50f,
            itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearestFallback(
            new[] { wielded, farNpc },
            Self(), SelfGuid, SelfLandblock);
        Assert.Equal(0x301u, picked!.Guid);
    }

    [Fact]
    public void Fallback_DuplicateNamePickup_StillEligible()
    {
        // picker-name-respawn-audit: the fallback no longer drops a
        // pickup whose Name was picked before. The nearest Apple wins;
        // the LLM decides via picked_name_count whether to re-pick.
        var freshApple = Snap(0x400, "Apple", x: 3f,
            itemType: ItemTypeMeleeWeapon);
        var npc = Snap(0x401, "Greeter", x: 10f,
            itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearestFallback(
            new[] { freshApple, npc },
            Self(), SelfGuid, SelfLandblock);
        Assert.Equal(0x400u, picked!.Guid);
    }

    [Fact]
    public void Fallback_ExcludesDifferentLandblock()
    {
        // ADDRESSABILITY (rubber-duck #3): the bot can't walk
        // directly into another landblock without a cell hand-off.
        // A remembered object 5u away in landblock 0x86020000 from
        // landblock 0x12340000 is unreachable in one motion. The
        // fallback must drop it.
        const uint otherLandblockCell = 0x86020001u;
        var nearButOff  = Snap(0x500, "Worcer", x: 3f,
            itemType: ItemTypeCreature, cellId: otherLandblockCell);
        var farInLandblock = Snap(0x501, "Greeter", x: 30f,
            itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearestFallback(
            new[] { nearButOff, farInLandblock },
            Self(), SelfGuid, SelfLandblock);
        Assert.NotNull(picked);
        Assert.Equal(0x501u, picked!.Guid);
    }

    [Fact]
    public void Fallback_ExcludesObjectsWithNoCellId()
    {
        // An object with CellId == 0 (or null) has no spatial
        // position and can't be walked toward. Drop it.
        var noCell = Snap(0x600, "Floating Soul", x: 1f,
            itemType: ItemTypeCreature, cellId: 0u);
        var realObj = Snap(0x601, "Greeter", x: 8f,
            itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearestFallback(
            new[] { noCell, realObj },
            Self(), SelfGuid, SelfLandblock);
        Assert.Equal(0x601u, picked!.Guid);
    }

    [Fact]
    public void Fallback_EnumerateCandidates_SortedNearestFirst()
    {
        // The candidate-list surface (used by the LLM "##
        // Exploration candidates" prompt block) must enumerate
        // sorted nearest-first so the LLM sees the same order
        // the picker would pick.
        var far  = Snap(0x700, "Far",   x: 30f, itemType: ItemTypeCreature);
        var mid  = Snap(0x701, "Mid",   x: 10f, itemType: ItemTypeCreature);
        var near = Snap(0x702, "Near",  x: 3f,  itemType: ItemTypeCreature);

        var listed = PickerSelection.EnumerateFallbackCandidates(
            new[] { far, mid, near },
            Self(), SelfGuid, SelfLandblock).ToList();

        Assert.Equal(3, listed.Count);
        Assert.Equal(0x702u, listed[0].snap.Guid);
        Assert.Equal(0x701u, listed[1].snap.Guid);
        Assert.Equal(0x700u, listed[2].snap.Guid);
        // Distances should be increasing.
        Assert.True(listed[0].distance < listed[1].distance);
        Assert.True(listed[1].distance < listed[2].distance);
    }

    [Fact]
    public void Fallback_DoesNotBumpUnvisitedDoorAheadOfFartherNpc()
    {
        // FORBIDDEN before W.2: door > pickup, NPC > pickup, etc.
        // A door at 30u must lose to an NPC at 4u when both are
        // mechanically eligible. (The OLD fallback would have
        // ranked corpse > NPC > door > pickup regardless of
        // distance.)
        var farDoor  = Snap(0x800, "Door",    x: 30f, descFlags: FlagDoor);
        var nearNpc  = Snap(0x801, "Greeter", x: 4f,  itemType: ItemTypeCreature);
        var picked = PickerSelection.PickNearestFallback(
            new[] { farDoor, nearNpc },
            Self(), SelfGuid, SelfLandblock);
        Assert.Equal(0x801u, picked!.Guid);
    }

    [Fact]
    public void Fallback_NoCorpseBump()
    {
        // FORBIDDEN before W.2: corpse → prio 0 alongside NPC.
        // A far corpse must lose to a closer object.
        var farCorpse = Snap(0x900, "Corpse of Golem", x: 50f, descFlags: FlagCorpse);
        var nearPickup = Snap(0x901, "Apple", x: 3f, itemType: ItemTypeMeleeWeapon);
        var picked = PickerSelection.PickNearestFallback(
            new[] { farCorpse, nearPickup },
            Self(), SelfGuid, SelfLandblock);
        Assert.Equal(0x901u, picked!.Guid);
    }
}
