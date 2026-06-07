// SPDX-License-Identifier: AGPL-3.0-or-later
// Phase C (picker-hunt-suppress) — HuntAuthorization typed-predicate unit
// tests. These pin the shared "is the IntentStack carrying a hunt
// commitment?" definition used by BOTH the outdoor-frontier mob-bias and
// the autonomous-picker suppression. Knowledge-free: only Intent.Kind
// labels (LLM-authored) and the typed visible_tag:monster completion.

using System;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;
using Xunit;

namespace HeadlessAcClient.Tests;

public class HuntAuthorizationTests
{
    private static WorldStateProjection BuildWorld() =>
        new()
        {
            Self = new SelfProjection
            {
                Guid = 0x50000005,
                Name = "Headless",
                Landblock = 0x8602u,
                CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0,
                Level = 1,
                HealthFraction = 1.0f,
            },
            Visible = Array.Empty<VisibleObjectProjection>(),
            Inventory = Array.Empty<InventoryItemProjection>(),
        };

    private static IntentBaseline BuildBaseline() =>
        IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);

    private static Intent NewIntent(
        string kind,
        IntentPredicate? completion = null,
        IntentLifecycle status = IntentLifecycle.Active) =>
        new()
        {
            Id = "i-001",
            Kind = kind,
            Rationale = $"test:{kind}",
            Completion = completion ?? new AlwaysFalsePredicate(),
            Baseline = BuildBaseline(),
            Status = status,
        };

    // ---- IsHuntCommitment: positive cases ------------------------------

    [Fact]
    public void IsHuntCommitment_OperatorHuntKind_True()
        => Assert.True(HuntAuthorization.IsHuntCommitment(NewIntent("Hunt")));

    [Fact]
    public void IsHuntCommitment_LlmHuntExcursionKind_True()
        => Assert.True(HuntAuthorization.IsHuntCommitment(NewIntent("hunt-excursion")));

    [Fact]
    public void IsHuntCommitment_VisibleMonsterCompletion_True()
        => Assert.True(HuntAuthorization.IsHuntCommitment(
            NewIntent("quest:whatever", new VisibleTagPredicate("monster"))));

    [Fact]
    public void IsHuntCommitment_VisibleMonsterCompletion_CaseInsensitiveTag_True()
        => Assert.True(HuntAuthorization.IsHuntCommitment(
            NewIntent("quest:whatever", new VisibleTagPredicate("Monster"))));

    // ---- IsHuntCommitment: negative cases ------------------------------

    [Fact]
    public void IsHuntCommitment_Null_False()
        => Assert.False(HuntAuthorization.IsHuntCommitment(null));

    [Fact]
    public void IsHuntCommitment_UnrelatedKind_False()
        => Assert.False(HuntAuthorization.IsHuntCommitment(NewIntent("go-buy-spell-comps")));

    [Fact]
    public void IsHuntCommitment_NonMonsterVisibleTag_False()
        => Assert.False(HuntAuthorization.IsHuntCommitment(
            NewIntent("quest:lifestone", new VisibleTagPredicate("lifestone"))));

    [Fact]
    public void IsHuntCommitment_KindIsCaseSensitive_LowercaseHunt_False()
        => Assert.False(HuntAuthorization.IsHuntCommitment(NewIntent("hunt")));

    // ---- IsActiveHunt: lifecycle gating --------------------------------

    [Fact]
    public void IsActiveHunt_ActiveHuntKind_True()
        => Assert.True(HuntAuthorization.IsActiveHunt(
            NewIntent("Hunt", status: IntentLifecycle.Active)));

    [Fact]
    public void IsActiveHunt_BlockedHuntKind_False()
        => Assert.False(HuntAuthorization.IsActiveHunt(
            NewIntent("Hunt", status: IntentLifecycle.Blocked)));

    [Fact]
    public void IsActiveHunt_CompletedHuntKind_False()
        => Assert.False(HuntAuthorization.IsActiveHunt(
            NewIntent("Hunt", status: IntentLifecycle.Completed)));

    [Fact]
    public void IsActiveHunt_ExpiredVisibleMonster_False()
        => Assert.False(HuntAuthorization.IsActiveHunt(
            NewIntent("quest:whatever", new VisibleTagPredicate("monster"),
                      IntentLifecycle.Expired)));

    [Fact]
    public void IsActiveHunt_ActiveNonHunt_False()
        => Assert.False(HuntAuthorization.IsActiveHunt(
            NewIntent("go-buy-spell-comps", status: IntentLifecycle.Active)));

    [Fact]
    public void IsActiveHunt_Null_False()
        => Assert.False(HuntAuthorization.IsActiveHunt(null));
}
