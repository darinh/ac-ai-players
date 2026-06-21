// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit tests for the autonomous kill-intent decomposition
// (reduce-llm-call-volume): CombatCommitment.IsActiveKillCommitment (the
// typed-predicate authorization gate), LlmGoalPolicy.ChooseCombatChainTarget
// (the pure target selection), the kill-switch (ResolveCombatChainEnabled), and
// the chain-interrupt routing classifier (IsChainInterruptingKind). The Motor
// only DECOMPOSES an LLM-authored typed kill-count commitment — these pin that
// it never fires on a generic hunt/visible_tag intent, never re-attacks a
// beaten kind, honors the name filter, perception bound, corpse exclusion, and
// chain cap, and that decision-worthy events (not combat noise) interrupt it.
// Knowledge-free: domain-neutral placeholder names only.

using System;
using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CombatChainTests
{
    private static WorldStateProjection BuildWorld() =>
        new()
        {
            Self = new SelfProjection
            {
                Guid = 0x50000005, Name = "Headless",
                Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0,
                Level = 1, HealthFraction = 1.0f,
            },
            Visible = Array.Empty<VisibleObjectProjection>(),
            Inventory = Array.Empty<InventoryItemProjection>(),
        };

    private static Intent NewIntent(
        IntentPredicate completion,
        IntentLifecycle status = IntentLifecycle.Active) =>
        new()
        {
            Id = "i-001",
            Kind = "quest:kill-task",
            Rationale = "test",
            Completion = completion,
            Baseline = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow),
            Status = status,
        };

    private static VisibleObjectProjection Mob(
        uint guid, string name, float distance,
        bool hostile = true, bool corpse = false, uint? wcid = null) =>
        new()
        {
            Guid = guid, Name = name, Wcid = wcid, Distance = distance,
            IsCreature = true, ObservedHostile = hostile, IsCorpse = corpse,
        };

    // ---- CombatCommitment.IsActiveKillCommitment ----------------------------

    [Fact]
    public void IsActiveKillCommitment_KillCountSincePush_True_NoNameFilter()
    {
        var ok = CombatCommitment.IsActiveKillCommitment(
            NewIntent(new KillCountSincePushAtLeastPredicate(3)), out var filter);
        Assert.True(ok);
        Assert.Null(filter);
    }

    [Fact]
    public void IsActiveKillCommitment_KillCountSincePush_ExtractsNameFilter()
    {
        var ok = CombatCommitment.IsActiveKillCommitment(
            NewIntent(new KillCountSincePushAtLeastPredicate(5, "Quarry")), out var filter);
        Assert.True(ok);
        Assert.Equal("Quarry", filter);
    }

    [Fact]
    public void IsActiveKillCommitment_KillCountTotal_True_NoNameFilter()
    {
        var ok = CombatCommitment.IsActiveKillCommitment(
            NewIntent(new KillCountTotalAtLeastPredicate(10)), out var filter);
        Assert.True(ok);
        Assert.Null(filter);
    }

    [Fact]
    public void IsActiveKillCommitment_Null_False()
        => Assert.False(CombatCommitment.IsActiveKillCommitment(null, out _));

    [Fact]
    public void IsActiveKillCommitment_BlockedKillCount_False()
        => Assert.False(CombatCommitment.IsActiveKillCommitment(
            NewIntent(new KillCountSincePushAtLeastPredicate(3), IntentLifecycle.Blocked), out _));

    [Fact]
    public void IsActiveKillCommitment_GenericHuntVisibleTag_False()
        => Assert.False(CombatCommitment.IsActiveKillCommitment(
            NewIntent(new VisibleTagPredicate("monster")), out _));

    [Fact]
    public void IsActiveKillCommitment_AlwaysFalse_False()
        => Assert.False(CombatCommitment.IsActiveKillCommitment(
            NewIntent(new AlwaysFalsePredicate()), out _));

    // ---- ResolveCombatChainEnabled (kill-switch) ----------------------------

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("off", false)]
    [InlineData("Off", false)]
    public void ResolveCombatChainEnabled_RespectsEnv(string? env, bool expected)
        => Assert.Equal(expected, LlmGoalPolicy.ResolveCombatChainEnabled(env));

    // ---- IsChainInterruptingKind --------------------------------------------

    [Fact]
    public void IsChainInterruptingKind_DecisionWorthy_True()
    {
        Assert.True(LlmGoalPolicy.IsChainInterruptingKind(EventKind.InventoryItemRemoved));
        Assert.True(LlmGoalPolicy.IsChainInterruptingKind(EventKind.NpcDialog));
        Assert.True(LlmGoalPolicy.IsChainInterruptingKind(EventKind.PopupString));
        Assert.True(LlmGoalPolicy.IsChainInterruptingKind(EventKind.BookText));
        Assert.True(LlmGoalPolicy.IsChainInterruptingKind(EventKind.ActionRejected));
    }

    [Fact]
    public void IsChainInterruptingKind_LandblockChanged_NotInterrupting()
    {
        // cp029: crossing a cell boundary mid-grind is not itself decision-worthy
        // for the kill-count chain. ChooseCombatChainTarget's visible-target filter
        // already yields (no-matching-monster) when the committed kind is absent in
        // the new area; if the same kind is still visible the grind simply continued
        // across the boundary. So LandblockChanged no longer preempts the chain —
        // bounded by the MaxCombatChainAttacks cap + the 0xFFFB disengage (a
        // non-excluded ActionRejected) which still interrupts on danger.
        Assert.False(LlmGoalPolicy.IsChainInterruptingKind(EventKind.LandblockChanged));
    }

    [Fact]
    public void IsChainInterruptingKind_OwnLootPickup_NotInterrupting()
    {
        // Picking up a kill's own drops (InventoryItemAdded) is an EXPECTED
        // byproduct of a committed grind, not a decision-worthy external change,
        // so it must NOT route the chain to a per-kill LLM call. An item LEAVING
        // inventory (give/use/sell) is a deliberate act and STILL interrupts.
        Assert.False(LlmGoalPolicy.IsChainInterruptingKind(EventKind.InventoryItemAdded));
        Assert.True(LlmGoalPolicy.IsChainInterruptingKind(EventKind.InventoryItemRemoved));
    }

    [Fact]
    public void IsChainInterruptingKind_CombatNoise_False()
    {
        Assert.False(LlmGoalPolicy.IsChainInterruptingKind(EventKind.ServerMessage));   // "you have slain ..."
        Assert.False(LlmGoalPolicy.IsChainInterruptingKind(EventKind.CombatFeedback));
        Assert.False(LlmGoalPolicy.IsChainInterruptingKind(EventKind.InboundDamageTaken));
        Assert.False(LlmGoalPolicy.IsChainInterruptingKind(EventKind.GoalCompleted));   // the kill's own lifecycle
        Assert.False(LlmGoalPolicy.IsChainInterruptingKind(EventKind.SelfProgressChanged));
    }

    // ---- ChooseCombatChainTarget --------------------------------------------

    private static readonly IReadOnlyList<VisibleObjectProjection> OneCloseMob =
        new[] { Mob(0x8001, "Quarry Alpha", 20f) };

    [Fact]
    public void ChooseChainTarget_PicksNearestMatchingHostile()
    {
        var visible = new[]
        {
            Mob(0x8001, "Quarry Alpha", 40f),
            Mob(0x8002, "Quarry Beta", 12f),
            Mob(0x8003, "Bystander Gamma", 5f),
        };
        var top = NewIntent(new KillCountSincePushAtLeastPredicate(3, "Quarry"));
        var chosen = LlmGoalPolicy.ChooseCombatChainTarget(
            top, visible, history: null, selfLevel: 5,
            enabled: true, chainCount: 0, maxChain: 6);
        Assert.NotNull(chosen);
        Assert.Equal(0x8002u, chosen!.Guid); // nearest "Quarry"; the closer Bystander is filtered out
    }

    [Fact]
    public void ChooseChainTarget_NoNameFilter_PicksNearestAnyHostile()
    {
        var visible = new[]
        {
            Mob(0x8001, "Quarry Alpha", 40f),
            Mob(0x8003, "Bystander Gamma", 5f),
        };
        var top = NewIntent(new KillCountTotalAtLeastPredicate(10));
        var chosen = LlmGoalPolicy.ChooseCombatChainTarget(
            top, visible, history: null, selfLevel: 5,
            enabled: true, chainCount: 0, maxChain: 6);
        Assert.Equal(0x8003u, chosen!.Guid); // nearest hostile, no kind filter
    }

    [Fact]
    public void ChooseChainTarget_Null_WhenDisabled()
        => Assert.Null(LlmGoalPolicy.ChooseCombatChainTarget(
            NewIntent(new KillCountSincePushAtLeastPredicate(3)), OneCloseMob,
            null, 5, enabled: false, chainCount: 0, maxChain: 6));

    [Fact]
    public void ChooseChainTarget_ChainsPassiveMonster_NotJustHostile()
    {
        // cp2918 (reduce-llm-call-volume): AC's weak grind kinds are PASSIVE
        // (IsMonster but NOT ObservedHostile). The chain must still execute an
        // LLM-authored kill-count commitment against them — the SAME set the
        // Hunt decomposition attacks — else a passive-monster grind (the common
        // case) falls back to a per-kill LLM call. Requiring ObservedHostile
        // (the prior behavior) defeated the whole purpose.
        var passive = new VisibleObjectProjection
        {
            Guid = 0x9001u, Name = "Quarry Passive", Distance = 8f,
            IsMonster = true, ObservedHostile = false, IsCorpse = false,
        };
        var chosen = LlmGoalPolicy.ChooseCombatChainTarget(
            NewIntent(new KillCountTotalAtLeastPredicate(10)), new[] { passive },
            history: null, selfLevel: 5, enabled: true, chainCount: 0, maxChain: 6);
        Assert.NotNull(chosen);
        Assert.Equal(0x9001u, chosen!.Guid);
    }

    [Fact]
    public void ChooseChainTarget_Null_WhenChainCapReached()
        => Assert.Null(LlmGoalPolicy.ChooseCombatChainTarget(
            NewIntent(new KillCountSincePushAtLeastPredicate(3)), OneCloseMob,
            null, 5, enabled: true, chainCount: 6, maxChain: 6));

    [Fact]
    public void ChooseChainTarget_Null_WhenNoKillCommitment()
        => Assert.Null(LlmGoalPolicy.ChooseCombatChainTarget(
            NewIntent(new VisibleTagPredicate("monster")), OneCloseMob,
            null, 5, enabled: true, chainCount: 0, maxChain: 6));

    [Fact]
    public void ChooseChainTarget_Null_WhenNoVisibleHostiles()
        => Assert.Null(LlmGoalPolicy.ChooseCombatChainTarget(
            NewIntent(new KillCountSincePushAtLeastPredicate(3)),
            new[] { Mob(0x8001, "Bystander", 5f, hostile: false) },
            null, 5, enabled: true, chainCount: 0, maxChain: 6));

    [Fact]
    public void ChooseChainTarget_ExcludesCorpses()
        => Assert.Null(LlmGoalPolicy.ChooseCombatChainTarget(
            NewIntent(new KillCountSincePushAtLeastPredicate(3, "Quarry")),
            new[] { Mob(0x8001, "Quarry Alpha", 5f, corpse: true) },
            null, 5, enabled: true, chainCount: 0, maxChain: 6));

    [Fact]
    public void ChooseChainTarget_ExcludesBeyondPerception()
        => Assert.Null(LlmGoalPolicy.ChooseCombatChainTarget(
            NewIntent(new KillCountSincePushAtLeastPredicate(3, "Quarry")),
            new[] { Mob(0x8001, "Quarry Alpha", 200f) },
            null, 5, enabled: true, chainCount: 0, maxChain: 6));

    [Fact]
    public void ChooseChainTarget_ExcludesBeatenKind()
    {
        // The bot has lost to (and never killed) this kind, with a death -> stays
        // beaten regardless of level: the chain must not re-engage it.
        var history = new[]
        {
            new CombatHistoryEntry(
                Name: "Quarry Alpha", Wcid: 0x4242, Kills: 0, Deaths: 2,
                NearDeaths: 0, Fights: 2, LastOutcome: "death", Ineffective: 0),
        };
        var chosen = LlmGoalPolicy.ChooseCombatChainTarget(
            NewIntent(new KillCountSincePushAtLeastPredicate(3, "Quarry")),
            new[] { Mob(0x8001, "Quarry Alpha", 10f, wcid: 0x4242) },
            history, selfLevel: 5,
            enabled: true, chainCount: 0, maxChain: 6);
        Assert.Null(chosen);
    }

    [Fact]
    public void ChooseChainTarget_SkipReason_ClassifiesEachNoMintCause()
    {
        // cp2925 diagnostic: the out-param overload reports WHY no target minted,
        // so the chain-never-fires tempo gap is observable. Behavior (the returned
        // target) is identical to the no-out-param overload.
        var commit = NewIntent(new KillCountSincePushAtLeastPredicate(3, "Quarry"));
        var oneQuarry = new[] { Mob(0x8001, "Quarry Alpha", 10f) };

        LlmGoalPolicy.ChooseCombatChainTarget(commit, oneQuarry, null, 5, enabled: false, 0, 6, out var r1);
        Assert.Equal("chain-disabled", r1);

        LlmGoalPolicy.ChooseCombatChainTarget(commit, oneQuarry, null, 5, true, chainCount: 6, maxChain: 6, out var r2);
        Assert.Equal("budget-exhausted", r2);

        LlmGoalPolicy.ChooseCombatChainTarget(commit, System.Array.Empty<VisibleObjectProjection>(), null, 5, true, 0, 6, out var r3);
        Assert.Equal("no-visible", r3);

        LlmGoalPolicy.ChooseCombatChainTarget(NewIntent(new VisibleTagPredicate("monster")), oneQuarry, null, 5, true, 0, 6, out var r4);
        Assert.Equal("no-active-commitment", r4);

        // The committed kind ("Quarry") is not among the visible mobs.
        LlmGoalPolicy.ChooseCombatChainTarget(commit, new[] { Mob(0x8002, "Bystander", 5f) }, null, 5, true, 0, 6, out var r5);
        Assert.Equal("no-matching-monster", r5);

        // A target IS found -> no skip reason.
        var chosen = LlmGoalPolicy.ChooseCombatChainTarget(commit, oneQuarry, null, 5, true, 0, 6, out var r6);
        Assert.NotNull(chosen);
        Assert.Null(r6);
    }

    [Fact]
    public void ChooseChainTarget_NotCombatCapable_SkipsEvenWithMatchingTarget()
    {
        // cp047: the chain must NOT mint an Attack while the bot cannot deal damage
        // (no usable weapon) — even with an active commitment and a matching target
        // in view. Yields to the LLM (which sees the UNARMED readiness line).
        var commit = NewIntent(new KillCountSincePushAtLeastPredicate(3, "Quarry"));
        var oneQuarry = new[] { Mob(0x8001, "Quarry Alpha", 10f) };

        var chosen = LlmGoalPolicy.ChooseCombatChainTarget(
            commit, oneQuarry, null, 5, true, 0, 6, out var reason, combatCapable: false);
        Assert.Null(chosen);
        Assert.Equal("not-combat-capable", reason);
    }

    [Fact]
    public void ChooseChainTarget_CombatCapable_MintsMatchingTarget()
    {
        // The guard does NOT block a combat-capable bot: an explicit combatCapable:true
        // still mints the matching committed target (no behavior change when armed).
        var commit = NewIntent(new KillCountSincePushAtLeastPredicate(3, "Quarry"));
        var oneQuarry = new[] { Mob(0x8001, "Quarry Alpha", 10f) };

        var chosen = LlmGoalPolicy.ChooseCombatChainTarget(
            commit, oneQuarry, null, 5, true, 0, 6, out var reason, combatCapable: true);
        Assert.NotNull(chosen);
        Assert.Null(reason);
    }

    [Fact]
    public void IsCombatCapable_TruthTable()
    {
        const uint meleeType = 0x1u, missileType = 0x100u;
        const uint meleeSlot = 0x100000u, missileSlot = 0x400000u, ammoSlot = 0x800000u;

        // A wielded melee weapon -> capable.
        Assert.True(LlmGoalPolicy.IsCombatCapable(new[]
        {
            new InventoryItemProjection { Guid = 0x1u, Name = "Blade", Wcid = 1u, ItemType = meleeType, WieldedAt = meleeSlot },
        }));
        // A wielded missile LAUNCHER (declares an AmmoType) WITH ammo loaded -> capable.
        Assert.True(LlmGoalPolicy.IsCombatCapable(new[]
        {
            new InventoryItemProjection { Guid = 0x2u, Name = "Launcher", Wcid = 2u, ItemType = missileType, WieldedAt = missileSlot, AmmoType = 1 },
            new InventoryItemProjection { Guid = 0x3u, Name = "Ammo", Wcid = 3u, ItemType = missileType, WieldedAt = ammoSlot, AmmoType = 1 },
        }));
        // A wielded missile LAUNCHER (declares an AmmoType) with NO ammo loaded -> NOT capable.
        Assert.False(LlmGoalPolicy.IsCombatCapable(new[]
        {
            new InventoryItemProjection { Guid = 0x2u, Name = "Launcher", Wcid = 2u, ItemType = missileType, WieldedAt = missileSlot, AmmoType = 1 },
        }));
        // A wielded THROWN weapon (missile bit, main slot, NO AmmoType — it is its own
        // projectile) -> capable WITHOUT any loaded ammo.
        Assert.True(LlmGoalPolicy.IsCombatCapable(new[]
        {
            new InventoryItemProjection { Guid = 0x4u, Name = "Throwable", Wcid = 4u, ItemType = missileType, WieldedAt = missileSlot, AmmoType = null },
        }));
        // Loaded ammo WITHOUT a launcher -> NOT capable. Ammo sits in the ammo slot
        // (outside the main-weapon slots) and can carry the MissileWeapon ItemType
        // bit, so it must NOT be mistaken for a wielded launcher.
        Assert.False(LlmGoalPolicy.IsCombatCapable(new[]
        {
            new InventoryItemProjection { Guid = 0x3u, Name = "Loose Ammo", Wcid = 3u, ItemType = missileType, WieldedAt = ammoSlot },
        }));
        // A weapon sitting un-wielded in the bag -> NOT capable.
        Assert.False(LlmGoalPolicy.IsCombatCapable(new[]
        {
            new InventoryItemProjection { Guid = 0x1u, Name = "Blade", Wcid = 1u, ItemType = meleeType, ValidLocations = meleeSlot, WieldedAt = null },
        }));
        // Empty inventory -> NOT capable.
        Assert.False(LlmGoalPolicy.IsCombatCapable(System.Array.Empty<InventoryItemProjection>()));
    }
}
