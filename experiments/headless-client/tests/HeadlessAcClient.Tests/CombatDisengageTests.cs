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
}
