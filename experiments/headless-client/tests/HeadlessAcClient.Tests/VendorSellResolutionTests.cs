// SPDX-License-Identifier: AGPL-3.0-or-later
// Vendor-sell motor helpers. These lock the two WorldState predicates the Sell
// motor relies on:
//   - ResolveOwnedInventoryItemExact: EXACT-only resolution of a BAGGED item by
//     name. Like the Buy resolver it is exact-only (no substring/fuzzy pick), and
//     it resolves ONLY items the bot directly contains (ContainerGuid == self),
//     which excludes WIELDED gear (equipped items carry no ContainerGuid) so the
//     motor never tries to sell the weapon the bot is using.
//   - IsOwnedInventoryGuid: the guid-precise "the sold item left my pack" signal
//     used to reconcile an in-flight sell.

using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class VendorSellResolutionTests
{
    private const uint SelfGuid = 0x5000000A;
    private const uint OtherContainer = 0x9999u;
    private const uint Cell = 0xAAB50003u;

    private static WorldState WithInventory()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SnapshotSeeding.Seed(ws, SelfGuid, "Headless", 1u, 0u, Cell, null);
        // Two bagged loot items + one identically-named bagged item (dup) + one
        // item sitting in another container (a chest — NOT owned) + one item with
        // NO container (the equipped shape: an equipped item carries WielderGuid
        // but no ContainerGuid) which must NOT be sellable.
        SnapshotSeeding.Seed(ws, 0x111u, "Sandstone Mace", 700u, 0u, Cell, SelfGuid);
        SnapshotSeeding.Seed(ws, 0x222u, "Leather Gloves", 701u, 0u, Cell, SelfGuid);
        SnapshotSeeding.Seed(ws, 0x333u, "Sandstone Mace", 700u, 0u, Cell, SelfGuid); // dup name, higher guid
        SnapshotSeeding.Seed(ws, 0x444u, "Sandstone Mace", 700u, 0u, Cell, OtherContainer); // in a chest
        SnapshotSeeding.Seed(ws, 0x555u, "Training Spadone", 702u, 0u, Cell, null); // no container (equipped)
        return ws;
    }

    [Fact]
    public void ResolveOwnedInventoryItemExact_MatchesExactNameCaseInsensitively()
    {
        var ws = WithInventory();
        var hit = ws.ResolveOwnedInventoryItemExact("leather gloves");
        Assert.NotNull(hit);
        Assert.Equal(0x222u, hit!.Guid);
    }

    [Fact]
    public void ResolveOwnedInventoryItemExact_DoesNotSubstringMatch()
    {
        var ws = WithInventory();
        Assert.Null(ws.ResolveOwnedInventoryItemExact("Mace"));
        Assert.Null(ws.ResolveOwnedInventoryItemExact("Gloves"));
        Assert.Null(ws.ResolveOwnedInventoryItemExact("Sandstone"));
    }

    [Fact]
    public void ResolveOwnedInventoryItemExact_NullOrEmptyOrNoMatch_ReturnsNull()
    {
        var ws = WithInventory();
        Assert.Null(ws.ResolveOwnedInventoryItemExact(null));
        Assert.Null(ws.ResolveOwnedInventoryItemExact("   "));
        Assert.Null(ws.ResolveOwnedInventoryItemExact("Mana Stone"));
    }

    [Fact]
    public void ResolveOwnedInventoryItemExact_ExcludesUnownedAndEquipped()
    {
        var ws = WithInventory();
        // Duplicate name resolves to the LOWEST owned guid (0x111), NOT the one in
        // the chest (0x444) and NOT a higher-guid dup (0x333), for determinism.
        var hit = ws.ResolveOwnedInventoryItemExact("Sandstone Mace");
        Assert.NotNull(hit);
        Assert.Equal(0x111u, hit!.Guid);
        // The no-container (equipped) item is never resolvable — the motor must
        // not try to sell gear the bot is using.
        Assert.Null(ws.ResolveOwnedInventoryItemExact("Training Spadone"));
    }

    [Fact]
    public void IsOwnedInventoryGuid_TrueOnlyForBaggedSelfContained()
    {
        var ws = WithInventory();
        Assert.True(ws.IsOwnedInventoryGuid(0x111u));   // bagged, owned
        Assert.True(ws.IsOwnedInventoryGuid(0x222u));   // bagged, owned
        Assert.False(ws.IsOwnedInventoryGuid(0x444u));  // in a chest, not owned
        Assert.False(ws.IsOwnedInventoryGuid(0x555u));  // no container (equipped)
        Assert.False(ws.IsOwnedInventoryGuid(0xDEADu)); // absent
    }

    // ── VendorBuysItemType (item-type acceptance gate, fails OPEN) ─────────

    [Fact]
    public void VendorBuysItemType_TrueWhenTypeBitInMask()
    {
        // mask 0x102 = two type bits set; an item of either type is accepted, an
        // item of a third type is not.
        const uint mask = 0x102u;
        Assert.True(WorldState.VendorBuysItemType(mask, 0x002u));
        Assert.True(WorldState.VendorBuysItemType(mask, 0x100u));
        Assert.False(WorldState.VendorBuysItemType(mask, 0x004u));
    }

    [Fact]
    public void VendorBuysItemType_FailsOpenOnUnknownMaskOrType()
    {
        // mask 0 (unknown/unrestricted) → never block.
        Assert.True(WorldState.VendorBuysItemType(0u, 0x004u));
        // unknown item type → don't block.
        Assert.True(WorldState.VendorBuysItemType(0x102u, null));
    }

    // ── SelfCoinValue (the "sale landed" reconcile signal) ────────────────

    [Fact]
    public void SelfCoinValue_ReadsPropertyInt20_OrNull()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SnapshotSeeding.Seed(ws, SelfGuid, "Headless", 1u, 0u, Cell, null);
        // No PropertyInts populated yet → unknown.
        Assert.Null(ws.SelfCoinValue);

        // Populate PropertyInt 20 (CoinValue) on the self snapshot.
        var self = ws.Objects[SelfGuid];
        self.PropertyInts = new System.Collections.Generic.Dictionary<uint, int> { [20u] = 137 };
        Assert.Equal(137, ws.SelfCoinValue);
    }

    // ── VendorSellReconcile.IsSettled (settle predicate; coin not sale-specific)

    [Fact]
    public void IsSettled_TrueWhenGuidLeftPack()
    {
        // Full-item sale: the sold guid is gone → settled regardless of coin.
        Assert.True(VendorSellReconcile.IsSettled(
            preCoin: 100, coinNow: 100, stillOwned: false, secondsElapsed: 1.0));
    }

    [Fact]
    public void IsSettled_TrueWhenCoinRose_EvenIfStillOwned()
    {
        // Partial-stack sale: object guid remains but coin rose → settled.
        Assert.True(VendorSellReconcile.IsSettled(
            preCoin: 100, coinNow: 150, stillOwned: true, secondsElapsed: 1.0));
    }

    [Fact]
    public void IsSettled_TrueOnTimeout()
    {
        Assert.True(VendorSellReconcile.IsSettled(
            preCoin: 100, coinNow: 100, stillOwned: true, secondsElapsed: 13.0));
    }

    [Fact]
    public void IsSettled_FalseWhenNoChangeWithinWindow()
    {
        Assert.False(VendorSellReconcile.IsSettled(
            preCoin: 100, coinNow: 100, stillOwned: true, secondsElapsed: 1.0));
    }

    [Fact]
    public void IsSettled_NullBaseline_DisablesCoinPath_StillSettlesOnGuidOrTimeout()
    {
        // Unknown coin baseline → coin path can't fire, but guid-gone + timeout
        // still settle. A partial-stack sale with an unknown baseline within the
        // window is NOT settled (it ages out via timeout).
        Assert.False(VendorSellReconcile.IsSettled(
            preCoin: null, coinNow: 150, stillOwned: true, secondsElapsed: 1.0));
        Assert.True(VendorSellReconcile.IsSettled(
            preCoin: null, coinNow: 150, stillOwned: false, secondsElapsed: 1.0));
        Assert.True(VendorSellReconcile.IsSettled(
            preCoin: null, coinNow: 150, stillOwned: true, secondsElapsed: 13.0));
    }

    [Fact]
    public void IsSettled_CoinMustStrictlyRise()
    {
        // Equal coin is not a rise (no false-settle from a no-op read).
        Assert.False(VendorSellReconcile.IsSettled(
            preCoin: 100, coinNow: 100, stillOwned: true, secondsElapsed: 1.0));
        // A coin DROP is also not a settle.
        Assert.False(VendorSellReconcile.IsSettled(
            preCoin: 100, coinNow: 90, stillOwned: true, secondsElapsed: 1.0));
    }
}
