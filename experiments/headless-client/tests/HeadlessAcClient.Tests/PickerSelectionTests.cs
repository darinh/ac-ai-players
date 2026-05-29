// SPDX-License-Identifier: AGPL-3.0-or-later
// Slice W.1 (#86) — PickerSelection unit tests.
//
// Audit-safe INVARIANT tests (per rubber-duck #7): we don't assert
// "NPCs beat doors" or any other strategic ordering. We assert:
//   - Distance is the only ordering signal (no type bumps).
//   - Self-bag items are excluded.
//   - Already-picked-by-name pickups are excluded (anti-respawn).
//   - Non-pickup repeats are NOT excluded (e.g. a door we've been
//     near isn't "picked-up" — visited-by-GUID is the caller's job).
//   - The schema-only picker NEVER prefers an NPC over a closer
//     door (the FORBIDDEN ladder is gone).
//   - The schema-only picker NEVER prefers a corpse over a closer
//     pickup (the FORBIDDEN loot bump is gone).

using System.Collections.Generic;
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
    private const uint PickupItemTypeMask = 0xD96F;

    // ItemType bits (mirrors enum values used in HandshakeDriver).
    private const uint ItemTypeCreature = 0x00000010;
    private const uint ItemTypeMeleeWeapon = 0x00000001;
    private const uint ItemTypeWritable = 0x00002000;

    // ObjectDescriptionFlag bits.
    private const uint FlagDoor = (uint)ObjectDescriptionFlag.Door;
    private const uint FlagPortal = (uint)ObjectDescriptionFlag.Portal;
    private const uint FlagCorpse = (uint)ObjectDescriptionFlag.Corpse;
    private const uint FlagStuck = (uint)ObjectDescriptionFlag.Stuck;

    private static WorldObjectSnapshot Self() =>
        new(SelfGuid) { CellId = CellId, Position = Vector3.Zero, Name = "Bot" };

    private static WorldObjectSnapshot Snap(
        uint guid, string name, float x,
        uint? itemType = null, uint? descFlags = null, uint? containerGuid = null) =>
        new(guid)
        {
            Name = name,
            CellId = CellId,
            Position = new Vector3(x, 0, 0),
            ItemType = itemType,
            ObjectDescriptionFlags = descFlags,
            ContainerGuid = containerGuid,
        };

    [Fact]
    public void Empty_ReturnsNull()
    {
        var picked = PickerSelection.PickNearest(
            new List<WorldObjectSnapshot>(),
            Self(), SelfGuid,
            new Dictionary<string, int>(),
            PickupItemTypeMask);
        Assert.Null(picked);
    }

    [Fact]
    public void NearestWins_DistanceOnly()
    {
        var a = Snap(0x100, "Far",   x: 30f);
        var b = Snap(0x101, "Mid",   x: 10f);
        var c = Snap(0x102, "Close", x: 3f);

        var picked = PickerSelection.PickNearest(
            new[] { a, b, c }, Self(), SelfGuid,
            new Dictionary<string, int>(), PickupItemTypeMask);
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
            new[] { npc, door }, Self(), SelfGuid,
            new Dictionary<string, int>(), PickupItemTypeMask);
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
            new[] { corpse, apple }, Self(), SelfGuid,
            new Dictionary<string, int>(), PickupItemTypeMask);
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
            new[] { inBag, farNpc }, Self(), SelfGuid,
            new Dictionary<string, int>(), PickupItemTypeMask);
        Assert.Equal(0x401u, picked!.Guid);
    }

    [Fact]
    public void ExcludesPickupsAlreadyPickedByName_AntiRespawn()
    {
        // Same Name, different GUID — visited-by-GUID upstream
        // filter wouldn't catch this. The pickedBefore filter
        // (anti-respawn) must.
        var freshApple = Snap(0x500, "Apple", x: 3f,
            itemType: ItemTypeMeleeWeapon);
        var npc = Snap(0x501, "Greeter", x: 10f,
            itemType: ItemTypeCreature);
        var counts = new Dictionary<string, int> { ["Apple"] = 1 };
        var picked = PickerSelection.PickNearest(
            new[] { freshApple, npc }, Self(), SelfGuid,
            counts, PickupItemTypeMask);
        // Apple is closer but excluded because pickedBefore > 0.
        // Greeter wins despite being farther.
        Assert.Equal(0x501u, picked!.Guid);
    }

    [Fact]
    public void DoesNotExcludeNonPickupRepeats()
    {
        // A door's Name appearing in pickupCountByName must NOT
        // exclude it — pickedBefore is about pickup-respawns,
        // not about Names in general. (Caller is responsible for
        // door-visit dedup via visited-GUID set, not via name
        // counts.)
        var door = Snap(0x600, "Door", x: 5f, descFlags: FlagDoor);
        var counts = new Dictionary<string, int> { ["Door"] = 99 };
        var picked = PickerSelection.PickNearest(
            new[] { door }, Self(), SelfGuid,
            counts, PickupItemTypeMask);
        Assert.Equal(0x600u, picked!.Guid);
    }

    [Fact]
    public void BookPickedBefore_Excluded()
    {
        // Writable + NOT Stuck = book (Pickup-able). After picking
        // one copy, a re-spawn must be excluded.
        var book = Snap(0x700, "Magic Tips", x: 3f,
            itemType: ItemTypeWritable);
        var npc  = Snap(0x701, "Greeter", x: 10f,
            itemType: ItemTypeCreature);
        var counts = new Dictionary<string, int> { ["Magic Tips"] = 1 };
        var picked = PickerSelection.PickNearest(
            new[] { book, npc }, Self(), SelfGuid,
            counts, PickupItemTypeMask);
        Assert.Equal(0x701u, picked!.Guid);
    }

    [Fact]
    public void SignPickedBeforeNotExcluded()
    {
        // Writable + Stuck = sign (USE-to-read). pickedBefore
        // tracks Names of items the bot put in its bag. A sign
        // can be USEd many times — the picker shouldn't exclude
        // it based on the name counter.
        var sign = Snap(0x800, "Tutorial Sign", x: 3f,
            itemType: ItemTypeWritable, descFlags: FlagStuck);
        var counts = new Dictionary<string, int> { ["Tutorial Sign"] = 5 };
        var picked = PickerSelection.PickNearest(
            new[] { sign }, Self(), SelfGuid,
            counts, PickupItemTypeMask);
        Assert.NotNull(picked);
        Assert.Equal(0x800u, picked!.Guid);
    }

    [Fact]
    public void PortalPickedBeforeNotExcluded()
    {
        // Portals carry the Portal description flag, which masks
        // them out of isPickup even though some carry ItemType bits
        // that overlap PickupItemTypeMask.
        var portal = Snap(0x900, "Academy Exit", x: 3f,
            itemType: ItemTypeMeleeWeapon, descFlags: FlagPortal);
        var counts = new Dictionary<string, int> { ["Academy Exit"] = 9 };
        var picked = PickerSelection.PickNearest(
            new[] { portal }, Self(), SelfGuid,
            counts, PickupItemTypeMask);
        Assert.NotNull(picked);
        Assert.Equal(0x900u, picked!.Guid);
    }
}
