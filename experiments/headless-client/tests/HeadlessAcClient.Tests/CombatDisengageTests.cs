// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for CombatDisengage — the pure self-preservation reflex
// decisions (break off, suppress re-engage, compute retreat point).

using System.Numerics;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CombatDisengageTests
{
    private const double DisengageFrac = 0.35;
    private const uint CriticalFloor = 2u;
    private const double ReengageFrac = 0.70;

    // ---- ShouldDisengage ----

    [Fact]
    public void ShouldDisengage_NotInCombat_NeverFires()
        => Assert.False(CombatDisengage.ShouldDisengage(1u, 100u, inCombat: false, DisengageFrac, CriticalFloor));

    [Fact]
    public void ShouldDisengage_UnknownMaxHealth_DoesNotFire()
        => Assert.False(CombatDisengage.ShouldDisengage(1u, 0u, inCombat: true, DisengageFrac, CriticalFloor));

    [Fact]
    public void ShouldDisengage_AlreadyDead_DoesNotFire()
        // Zero health is owned by the death/respawn path, not flee.
        => Assert.False(CombatDisengage.ShouldDisengage(0u, 100u, inCombat: true, DisengageFrac, CriticalFloor));

    [Fact]
    public void ShouldDisengage_HealthyAboveBothThresholds_DoesNotFire()
        => Assert.False(CombatDisengage.ShouldDisengage(50u, 100u, inCombat: true, DisengageFrac, CriticalFloor));

    [Fact]
    public void ShouldDisengage_BelowFraction_Fires()
        => Assert.True(CombatDisengage.ShouldDisengage(34u, 100u, inCombat: true, DisengageFrac, CriticalFloor));

    [Fact]
    public void ShouldDisengage_AtFractionBoundary_Fires()
        // 35 <= 100 * 0.35 == 35.0
        => Assert.True(CombatDisengage.ShouldDisengage(35u, 100u, inCombat: true, DisengageFrac, CriticalFloor));

    [Fact]
    public void ShouldDisengage_JustAboveFraction_DoesNotFire()
        => Assert.False(CombatDisengage.ShouldDisengage(36u, 100u, inCombat: true, DisengageFrac, CriticalFloor));

    [Fact]
    public void ShouldDisengage_AbsoluteFloor_FiresEvenWhenFractionWouldNot()
        // Low-max char: 2 of 5 max is 40% (above the 35% fraction) but
        // the absolute floor must still fire — two HP is one hit away.
        => Assert.True(CombatDisengage.ShouldDisengage(2u, 5u, inCombat: true, DisengageFrac, CriticalFloor));

    [Fact]
    public void ShouldDisengage_AbsoluteFloorBoundary_Fires()
        => Assert.True(CombatDisengage.ShouldDisengage(CriticalFloor, 1000u, inCombat: true, DisengageFrac, CriticalFloor));

    [Fact]
    public void ShouldDisengage_JustAboveFloorAndFraction_DoesNotFire()
        // 400 of 1000: above floor (2) and above fraction (350).
        => Assert.False(CombatDisengage.ShouldDisengage(400u, 1000u, inCombat: true, DisengageFrac, CriticalFloor));

    // ---- IsCombatSuppressed (hysteresis) ----

    [Fact]
    public void IsCombatSuppressed_UnknownMaxHealth_NotSuppressed()
        => Assert.False(CombatDisengage.IsCombatSuppressed(0u, 0u, ReengageFrac));

    [Fact]
    public void IsCombatSuppressed_BelowReengage_Suppressed()
        => Assert.True(CombatDisengage.IsCombatSuppressed(69u, 100u, ReengageFrac));

    [Fact]
    public void IsCombatSuppressed_AtReengage_NotSuppressed()
        // 70 < 100*0.70 == 70.0 is false → re-engage allowed.
        => Assert.False(CombatDisengage.IsCombatSuppressed(70u, 100u, ReengageFrac));

    [Fact]
    public void IsCombatSuppressed_FullHealth_NotSuppressed()
        => Assert.False(CombatDisengage.IsCombatSuppressed(100u, 100u, ReengageFrac));

    [Fact]
    public void Hysteresis_DisengagePointStaysSuppressed()
    {
        // At the disengage fraction the bot must remain suppressed (the
        // re-engage gate is strictly higher), so healing just past the
        // disengage threshold does not immediately re-engage.
        uint atDisengage = (uint)(100 * DisengageFrac); // 35
        Assert.True(CombatDisengage.IsCombatSuppressed(atDisengage, 100u, ReengageFrac));
    }

    // ---- ComputeFleeDestination ----

    [Fact]
    public void ComputeFleeDestination_MovesDirectlyAwayFromThreat()
    {
        // Threat to the west (-X); flee should head east (+X).
        var self = new Vector3(10f, 0f, 50f);
        var threat = new Vector3(0f, 0f, 50f);
        var dest = CombatDisengage.ComputeFleeDestination(self, threat, 15f);
        Assert.Equal(25f, dest.X, 3);
        Assert.Equal(0f, dest.Y, 3);
        Assert.Equal(50f, dest.Z, 3); // Z preserved
    }

    [Fact]
    public void ComputeFleeDestination_DistanceMatchesFleeDistance()
    {
        var self = new Vector3(5f, 5f, 12f);
        var threat = new Vector3(2f, 1f, 80f);
        var dest = CombatDisengage.ComputeFleeDestination(self, threat, 20f);
        var dx = dest.X - self.X;
        var dy = dest.Y - self.Y;
        var moved = System.MathF.Sqrt(dx * dx + dy * dy);
        Assert.Equal(20f, moved, 2);
    }

    [Fact]
    public void ComputeFleeDestination_PointsAwayFromThreat()
    {
        var self = new Vector3(5f, 5f, 0f);
        var threat = new Vector3(2f, 1f, 0f);
        var dest = CombatDisengage.ComputeFleeDestination(self, threat, 20f);
        // Dest must be farther from the threat than the bot was.
        var before = Vector3.Distance(self, threat);
        var after = Vector3.Distance(dest, threat);
        Assert.True(after > before);
    }

    [Fact]
    public void ComputeFleeDestination_Degenerate_FallsBackToPlusX()
    {
        // Bot and threat coincide in XY — still move (default +X).
        var self = new Vector3(7f, 7f, 3f);
        var threat = new Vector3(7f, 7f, 99f);
        var dest = CombatDisengage.ComputeFleeDestination(self, threat, 15f);
        Assert.Equal(22f, dest.X, 3);
        Assert.Equal(7f, dest.Y, 3);
        Assert.Equal(3f, dest.Z, 3);
    }

    // ---- ShouldDisengageUnwinnableLosing (early flee) ----

    private const int MinEvaded = 6;
    private const double LostFrac = 0.25;

    private static bool Unwinnable(
        int landed, uint dmg, int evaded, uint hc, uint hm, double? peak,
        bool inCombat = true)
        => CombatDisengage.ShouldDisengageUnwinnableLosing(
            inCombat, landed, dmg, evaded, MinEvaded, hc, hm, peak, LostFrac);

    [Fact]
    public void Unwinnable_NotInCombat_NeverFires()
        => Assert.False(Unwinnable(0, 0u, 10, 50u, 100u, 1.0, inCombat: false));

    [Fact]
    public void Unwinnable_UnknownMaxHealth_DoesNotFire()
        => Assert.False(Unwinnable(0, 0u, 10, 50u, 0u, 1.0));

    [Fact]
    public void Unwinnable_AlreadyDead_DoesNotFire()
        // Zero health is owned by the death/respawn path, not flee.
        => Assert.False(Unwinnable(0, 0u, 10, 0u, 100u, 1.0));

    [Fact]
    public void Unwinnable_ZeroLanded_ZeroDamage_EnoughEvaded_AndLosing_Fires()
        // 0 landed, 0 damage, 6 evaded, health fell 100%→75% (lost 25%).
        => Assert.True(Unwinnable(0, 0u, 6, 75u, 100u, 1.0));

    [Fact]
    public void Unwinnable_SomeSwingLanded_DoesNotFire()
        // Landed a hit → the fight is not unwinnable, regardless of health.
        => Assert.False(Unwinnable(1, 0u, 10, 50u, 100u, 1.0));

    [Fact]
    public void Unwinnable_SomeDamageDealt_DoesNotFire()
        // Dealt damage (even with landed==0 bookkeeping) → not unwinnable.
        => Assert.False(Unwinnable(0, 3u, 10, 50u, 100u, 1.0));

    [Fact]
    public void Unwinnable_TooFewEvadedSwings_DoesNotFire()
        // Only 5 evaded (< 6) — not yet conclusive it cannot damage.
        => Assert.False(Unwinnable(0, 0u, 5, 50u, 100u, 1.0));

    [Fact]
    public void Unwinnable_LosingTooLittleHealth_DoesNotFire()
        // 0 landed and plenty evaded, but only 10% health lost (a harmless
        // can't-hit stalemate, not a death risk) — the no-damage watchdog
        // owns that tempo case, not this flee reflex.
        => Assert.False(Unwinnable(0, 0u, 12, 90u, 100u, 1.0));

    [Fact]
    public void Unwinnable_NullPeak_DoesNotFire()
        // No high-water mark sampled yet → cannot measure health lost.
        => Assert.False(Unwinnable(0, 0u, 10, 50u, 100u, null));

    [Fact]
    public void Unwinnable_AtLossBoundary_Fires()
        // peak 1.00 - current 0.75 == 0.25 == threshold → fires (>=).
        => Assert.True(Unwinnable(0, 0u, 6, 75u, 100u, 1.0));

    [Fact]
    public void Unwinnable_JustBelowLossBoundary_DoesNotFire()
        // peak 1.00 - current 0.76 == 0.24 < 0.25 → does not fire.
        => Assert.False(Unwinnable(0, 0u, 6, 76u, 100u, 1.0));

    [Fact]
    public void Unwinnable_PeakBelowCurrent_DoesNotFire()
        // Health gained since the stored peak (loss negative) → not losing.
        => Assert.False(Unwinnable(0, 0u, 10, 90u, 100u, 0.50));

    [Fact]
    public void Unwinnable_FiresWhileWellAboveCriticalReflex()
    {
        // The decisive property: this trips while health (75%) is far above
        // the 35% critical reflex, so the bot flees with a safety margin.
        Assert.False(CombatDisengage.ShouldDisengage(75u, 100u, inCombat: true, DisengageFrac, CriticalFloor));
        Assert.True(Unwinnable(0, 0u, 6, 75u, 100u, 1.0));
    }

    [Fact]
    public void Unwinnable_LowMaxHealthChar_Fires()
        // 30-max char: peak 1.0, current 21/30 = 0.70 (lost 0.30 >= 0.25).
        => Assert.True(Unwinnable(0, 0u, 6, 21u, 30u, 1.0));

    // ---- DisengageReason (combined decision + reason tag) ----

    private static string? Reason(
        uint hc, uint hm, int landed, uint dmg, int evaded, double? peak,
        bool inCombat = true)
        => CombatDisengage.DisengageReason(
            hc, hm, inCombat, DisengageFrac, CriticalFloor,
            landed, dmg, evaded, MinEvaded, peak, LostFrac);

    [Fact]
    public void Reason_CriticalLowHealth_ReturnsLowHealth()
        // 30/100 is below the 35% critical reflex.
        => Assert.Equal("low-health", Reason(30u, 100u, 5, 40u, 0, 1.0));

    [Fact]
    public void Reason_UnwinnableLosing_ReturnsUnwinnableLosing()
        // 75/100 is above critical, but 0 landed + 6 evaded + lost 25%.
        => Assert.Equal("unwinnable-losing", Reason(75u, 100u, 0, 0u, 6, 1.0));

    [Fact]
    public void Reason_BothConditionsTrue_LowHealthTakesPrecedence()
        // 20/100 (critical) AND unwinnable-losing — low-health wins.
        => Assert.Equal("low-health", Reason(20u, 100u, 0, 0u, 8, 1.0));

    [Fact]
    public void Reason_Neither_ReturnsNull()
        // Healthy and landing hits — keep fighting.
        => Assert.Null(Reason(90u, 100u, 3, 25u, 1, 1.0));

    [Fact]
    public void Reason_NotInCombat_ReturnsNull()
        => Assert.Null(Reason(10u, 100u, 0, 0u, 10, 1.0, inCombat: false));
}
