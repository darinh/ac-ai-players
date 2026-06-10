// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for DecisionStarvationWatchdog — the pure tick-path watchdog that
// breaks the quiet-area receive-loop livelock (no packet -> no decision ->
// no movement -> no packet) by deciding when to re-assert position (poke)
// or escalate to a reconnect.

using HeadlessAcClient.Strategy;
using Xunit;

using Action = HeadlessAcClient.Strategy.DecisionStarvationWatchdog.Action;

namespace HeadlessAcClient.Tests;

public class DecisionStarvationWatchdogTests
{
    private const int StarvationMs = 4000;
    private const int PokeIntervalMs = 2000;
    private const int ReconnectThreshold = 5;

    // Helper that fills in the "healthy starved" defaults so each test only
    // varies the dimension under test.
    private static Action Eval(
        bool motionStopped = true,
        bool inCombat = false,
        bool recallQuiescing = false,
        bool actionQuiesceActive = false,
        bool haveSelfCell = true,
        double msSinceInboundPacket = StarvationMs + 1,
        double msSinceLastPoke = PokeIntervalMs + 1,
        int consecutivePokes = 0)
        => DecisionStarvationWatchdog.Evaluate(
            motionStopped, inCombat, recallQuiescing, actionQuiesceActive, haveSelfCell,
            msSinceInboundPacket, msSinceLastPoke, consecutivePokes,
            StarvationMs, PokeIntervalMs, ReconnectThreshold);

    // --- Gates that must suppress the watchdog entirely ---

    [Fact]
    public void ActiveMotion_DoesNotPoke()
        => Assert.Equal(Action.None, Eval(motionStopped: false));

    [Fact]
    public void InCombat_DoesNotPoke()
        => Assert.Equal(Action.None, Eval(inCombat: true));

    [Fact]
    public void RecallQuiescing_DoesNotPoke()
        => Assert.Equal(Action.None, Eval(recallQuiescing: true));

    [Fact]
    public void ActionQuiesceActive_DoesNotPoke()
        // A just-dispatched USE/portal cooldown must stay quiescent — a no-op
        // position then could abort a portal/teleport or move off-target.
        => Assert.Equal(Action.None, Eval(actionQuiesceActive: true));

    [Fact]
    public void NoSelfCell_DoesNotPoke()
        => Assert.Equal(Action.None, Eval(haveSelfCell: false));

    // --- Timing gates ---

    [Fact]
    public void IdleButNotYetStarved_DoesNotPoke()
        => Assert.Equal(Action.None, Eval(msSinceInboundPacket: StarvationMs - 1));

    [Fact]
    public void AtExactStarvationThreshold_Pokes()
        // `< starvationMs` suppresses; at exactly the threshold the watchdog acts.
        => Assert.Equal(Action.Poke, Eval(msSinceInboundPacket: StarvationMs));

    [Fact]
    public void Starved_ButPokedTooRecently_DoesNotPoke()
        => Assert.Equal(Action.None, Eval(msSinceLastPoke: PokeIntervalMs - 1));

    [Fact]
    public void AtExactPokeInterval_Pokes()
        => Assert.Equal(Action.Poke, Eval(msSinceLastPoke: PokeIntervalMs));

    // --- The core wedge case + escalation ladder ---

    [Fact]
    public void Starved_Idle_FirstTime_Pokes()
        => Assert.Equal(Action.Poke, Eval(consecutivePokes: 0));

    [Fact]
    public void Starved_BelowReconnectThreshold_Pokes()
        // consecutivePokes 0..3 => this tick is poke 1..4 => still Poke.
        => Assert.Equal(Action.Poke, Eval(consecutivePokes: ReconnectThreshold - 2));

    [Fact]
    public void Starved_AtReconnectThreshold_Reconnects()
        // consecutivePokes == 4 => this tick is the 5th => Reconnect.
        => Assert.Equal(Action.Reconnect, Eval(consecutivePokes: ReconnectThreshold - 1));

    [Fact]
    public void Starved_PastReconnectThreshold_StaysReconnect()
        => Assert.Equal(Action.Reconnect, Eval(consecutivePokes: ReconnectThreshold + 3));

    [Theory]
    [InlineData(0, (int)Action.Poke)]
    [InlineData(1, (int)Action.Poke)]
    [InlineData(2, (int)Action.Poke)]
    [InlineData(3, (int)Action.Poke)]
    [InlineData(4, (int)Action.Reconnect)]
    [InlineData(5, (int)Action.Reconnect)]
    public void EscalationLadder(int consecutivePokes, int expected)
        => Assert.Equal((Action)expected, Eval(consecutivePokes: consecutivePokes));

    // A blocked-target wedge keeps a non-null motionTarget, but the watchdog
    // never inspects the target — it only needs motionStopped. This documents
    // that the fix covers the observed motionTarget != null case (the gate is
    // motionStopped, NOT motionTarget == null).
    [Fact]
    public void BlockedTargetWedge_StillPokes_WhenStoppedAndStarved()
        => Assert.Equal(Action.Poke, Eval(motionStopped: true, consecutivePokes: 0));
}
