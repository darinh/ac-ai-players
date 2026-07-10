// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit tests for the per-run budget resolvers fed by AC_BOTS_OBSERVE_SECONDS
// and AC_BOTS_MAX_ACTIONS_PER_SESSION, plus the derived outer-cancellation
// headroom.
//
// Why this exists: the per-run process bounds were hard-coded — a 3600s (1 hr)
// observe budget and a 100-action session budget — leftovers from the
// short-observation spike. For a long autonomous run the time bound forced an
// hourly process exit (restarted by an external supervisor) AND the action bound
// left the bot idle after 100 actions. Both are now env-configurable. The outer
// cancellation budget in Program.cs is derived as ObserveSeconds +
// OuterBudgetHeadroomSeconds, where the headroom is computed from the actual
// login/handshake reconnect constants so it always exceeds the worst-case
// pre-observe overhead (a fixed 100s was smaller than that and could cancel a
// fresh run's observe window early). These resolvers are pure config parsing —
// no game knowledge — so they are safe to unit-test directly.

using HeadlessAcClient.Protocol;
using Xunit;

namespace HeadlessAcClient.Tests;

public class RunBudgetConfigTests
{
    [Theory]
    [InlineData(null, 3600)]       // unset -> default
    [InlineData("", 3600)]         // blank -> default
    [InlineData("   ", 3600)]      // whitespace -> default
    [InlineData("abc", 3600)]      // unparseable -> default
    [InlineData("3600", 3600)]     // explicit default value
    [InlineData("86400", 86400)]   // 24h long-run override
    [InlineData("60", 60)]         // min bound (accepted)
    [InlineData("59", 3600)]       // below min -> default
    [InlineData("0", 3600)]        // zero -> default
    [InlineData("-100", 3600)]     // negative -> default
    [InlineData("604800", 604800)] // 7 days == max (accepted)
    [InlineData("999999999", 604800)] // above max -> clamped to 7 days
    public void ResolveObserveSeconds_DefaultsAndClamps(string? env, int expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveObserveSeconds(env));
    }

    [Theory]
    [InlineData(null, 100)]          // unset -> default
    [InlineData("", 100)]            // blank -> default
    [InlineData("   ", 100)]         // whitespace -> default
    [InlineData("xyz", 100)]         // unparseable -> default
    [InlineData("100", 100)]         // explicit default value
    [InlineData("5000", 5000)]       // long-run override
    [InlineData("1", 1)]             // min bound (accepted)
    [InlineData("0", 100)]           // zero -> default
    [InlineData("-5", 100)]          // negative -> default
    [InlineData("10000000", 10000000)]   // max (accepted)
    [InlineData("99999999", 10000000)]   // above max -> clamped
    public void ResolveMaxActionsPerSession_DefaultsAndClamps(string? env, int expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveMaxActionsPerSession(env));
    }

    [Theory]
    [InlineData(null, 12)]      // unset -> default
    [InlineData("", 12)]        // blank -> default
    [InlineData("xyz", 12)]     // unparseable -> default
    [InlineData("12", 12)]      // explicit default value
    [InlineData("6", 6)]        // faster failed-raise recovery override
    [InlineData("3", 3)]        // min bound (accepted)
    [InlineData("2", 12)]       // below min -> default
    [InlineData("1", 12)]       // below min -> default
    [InlineData("0", 12)]       // zero -> default
    [InlineData("-5", 12)]      // negative -> default
    [InlineData("120", 120)]    // max (accepted)
    [InlineData("999", 120)]    // above max -> clamped
    public void ResolveRaiseConfirmTimeoutSeconds_DefaultsAndClamps(string? env, int expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveRaiseConfirmTimeoutSeconds(env));
    }

    [Theory]
    [InlineData(null, 60.0)]    // unset -> default
    [InlineData("", 60.0)]      // blank -> default
    [InlineData("   ", 60.0)]   // whitespace -> default
    [InlineData("abc", 60.0)]   // unparseable -> default
    [InlineData("60", 60.0)]    // explicit default value
    [InlineData("50", 50.0)]    // shorter cycle-off override (above floor)
    [InlineData("45.5", 45.5)]  // fractional override (accepted)
    [InlineData("45", 45.0)]    // min bound (accepted) — conservatively above the 30+s first-hit latency
    [InlineData("44.9", 60.0)]  // just below min -> default
    [InlineData("35", 60.0)]    // below min -> default
    [InlineData("0", 60.0)]     // zero -> default
    [InlineData("-5", 60.0)]    // negative -> default
    [InlineData("600", 600.0)]  // max (accepted)
    [InlineData("601", 600.0)]  // above max -> clamped
    [InlineData("100000", 600.0)] // far above max -> clamped
    public void ResolveAbandonNoDamageSeconds_DefaultsAndClamps(string? env, double expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveAbandonNoDamageSeconds(env));
    }

    [Theory]
    [InlineData(null, 0.35)]    // unset -> default
    [InlineData("", 0.35)]      // blank -> default
    [InlineData("abc", 0.35)]   // unparseable -> default
    [InlineData("0.35", 0.35)]  // explicit default
    [InlineData("0.45", 0.45)]  // more flee margin (accepted)
    [InlineData("0.05", 0.05)]  // min (accepted)
    [InlineData("0.65", 0.65)]  // max (accepted, stays below the 0.70 re-engage)
    [InlineData("0.04", 0.35)]  // below min -> default
    [InlineData("0", 0.35)]     // zero -> default
    [InlineData("-0.2", 0.35)]  // negative -> default
    [InlineData("0.70", 0.65)]  // at the re-engage fraction -> clamped below it
    [InlineData("0.9", 0.65)]   // above max -> clamped
    public void ResolveCombatDisengageHealthFraction_DefaultsAndClamps(string? env, double expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveCombatDisengageHealthFraction(env));
    }

    [Theory]
    [InlineData(null, 0.50)]    // unset -> default
    [InlineData("", 0.50)]      // blank -> default
    [InlineData("abc", 0.50)]   // unparseable -> default
    [InlineData("0.50", 0.50)]  // explicit default
    [InlineData("0.35", 0.35)]  // equal to the normal default -> disables the margin
    [InlineData("0.60", 0.60)]  // more spiral margin (accepted)
    [InlineData("0.05", 0.05)]  // min (accepted)
    [InlineData("0.65", 0.65)]  // max (accepted, below the 0.70 re-engage)
    [InlineData("0.04", 0.50)]  // below min -> default
    [InlineData("-0.2", 0.50)]  // negative -> default
    [InlineData("0.70", 0.65)]  // at the re-engage fraction -> clamped below it
    [InlineData("0.9", 0.65)]   // above max -> clamped
    public void ResolveSpiralDisengageHealthFraction_DefaultsAndClamps(string? env, double expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveSpiralDisengageHealthFraction(env));
    }

    [Theory]
    [InlineData(null, 2u)]      // unset -> default
    [InlineData("", 2u)]        // blank -> default
    [InlineData("abc", 2u)]     // unparseable -> default
    [InlineData("2", 2u)]       // explicit default
    [InlineData("0", 2u)]       // zero -> default (the absolute floor cannot be disabled)
    [InlineData("1", 1u)]       // min (accepted)
    [InlineData("5", 5u)]       // higher floor (accepted)
    [InlineData("100", 100u)]   // max (accepted)
    [InlineData("101", 100u)]   // above max -> clamped
    [InlineData("-3", 2u)]      // negative (unparseable as uint) -> default
    public void ResolveCombatDisengageCriticalHpFloor_DefaultsAndClamps(string? env, uint expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveCombatDisengageCriticalHpFloor(env));
    }

    [Fact]
    public void CombatDisengageFraction_CeilingStaysBelowReengageFraction()
    {
        // The resolver's max-clamp must keep ANY configured disengage fraction strictly
        // below the re-engage fraction so the disengage/re-engage hysteresis (no melee
        // oscillation) holds. Assert the relationship against the shared constant rather
        // than a magic literal, so this fails loudly if the re-engage fraction is lowered.
        var maxFraction = HandshakeDriver.ResolveCombatDisengageHealthFraction("0.99");
        Assert.True(maxFraction < HeadlessAcClient.Strategy.CombatDisengage.DefaultReengageHealthFraction,
            $"clamped disengage fraction {maxFraction} must stay below the re-engage fraction " +
            $"{HeadlessAcClient.Strategy.CombatDisengage.DefaultReengageHealthFraction}");
    }

    [Theory]
    [InlineData(null, 0.35, 0.70)]   // unset -> default
    [InlineData("", 0.35, 0.70)]     // blank -> default
    [InlineData("abc", 0.35, 0.70)]  // unparseable -> default
    [InlineData("0.70", 0.35, 0.70)] // explicit default
    [InlineData("0.55", 0.35, 0.55)] // lower re-engage (accepted; above the disengage floor)
    [InlineData("0.36", 0.35, 0.36)] // just above the floor (disengage 0.35 + 0.01)
    [InlineData("0.35", 0.35, 0.70)] // AT the disengage value (not above the floor) -> default
    [InlineData("0.30", 0.35, 0.70)] // below the floor -> default
    [InlineData("0.95", 0.35, 0.95)] // max (accepted)
    [InlineData("0.99", 0.35, 0.95)] // above max -> clamped
    [InlineData("0.15", 0.14, 0.15)] // float-jitter boundary: 0.14+0.01==0.15000000000000002; "0.15" still accepted
    [InlineData("0.06", 0.05, 0.06)] // another at-floor boundary
    public void ResolveCombatReengageHealthFraction_DefaultsAndClamps(string? env, double disengage, double expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveCombatReengageHealthFraction(env, disengage));
    }

    [Theory]
    // A HIGH disengage fraction raises the re-engage FLOOR so the invariant holds:
    [InlineData("0.60", 0.65, 0.70)] // env below the raised floor (0.66) -> default (0.70, > disengage)
    [InlineData("0.67", 0.65, 0.67)] // env above the raised floor -> accepted
    [InlineData(null, 0.65, 0.70)]   // default 0.70 already > disengage 0.65
    public void ResolveCombatReengageHealthFraction_StaysAboveHighDisengage(string? env, double disengage, double expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveCombatReengageHealthFraction(env, disengage));
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.35)]
    [InlineData(0.50)]
    [InlineData(0.65)]
    public void ResolveCombatReengageHealthFraction_AlwaysStrictlyAboveDisengage(double disengage)
    {
        // For ANY env value (including attempts to set it AT or BELOW the disengage), the
        // resolved re-engage fraction must stay strictly above the disengage so the
        // disengage/re-engage hysteresis cannot invert into oscillation.
        var envs = new string?[] { null, "", "abc", "0.01", "0.40", "0.99",
            disengage.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var env in envs)
        {
            var re = HandshakeDriver.ResolveCombatReengageHealthFraction(env, disengage);
            Assert.True(re > disengage,
                $"re-engage {re} must be strictly > disengage {disengage} (env={env ?? "null"})");
        }
    }

    [Fact]
    public void StrategyCombatChainGate_SharesMotorResolvedReengageFraction()
    {
        // The autonomous combat-chain gate (LlmGoalPolicy) MUST use the SAME env-resolved
        // re-engage fraction the Motor (HandshakeDriver) uses, or the chain mints Attacks
        // the Motor then refuses (the mint->REFUSE->MISS->re-mint loop the gate prevents).
        // Assert LlmGoalPolicy's resolved value equals the resolver output for the current
        // process env — proving the Strategy gate is wired to the shared resolved value,
        // not the fixed default.
        var expected = HandshakeDriver.ResolveCombatReengageHealthFraction(
            System.Environment.GetEnvironmentVariable("AC_BOTS_COMBAT_REENGAGE_HEALTH_FRACTION"),
            HandshakeDriver.ResolveCombatDisengageHealthFraction(
                System.Environment.GetEnvironmentVariable("AC_BOTS_COMBAT_DISENGAGE_HEALTH_FRACTION")));
        Assert.Equal(expected, HeadlessAcClient.Strategy.LlmGoalPolicy.CombatReengageHealthFraction);
    }

    [Fact]
    public void ChainCombatSelfHealthSuppressed_GatesOnResolvedReengageFraction()
    {
        // The combat-chain gate helper must suppress Attacks exactly when self-health is
        // below the RESOLVED re-engage fraction of max -- proving it reads the shared
        // CombatReengageHealthFraction (a revert to a fixed default would change this).
        var frac = HeadlessAcClient.Strategy.LlmGoalPolicy.CombatReengageHealthFraction;
        var thresholdHp = (int)(frac * 100.0);
        Assert.True(HeadlessAcClient.Strategy.LlmGoalPolicy.ChainCombatSelfHealthSuppressed(thresholdHp - 5, 100));  // well below -> suppressed
        Assert.False(HeadlessAcClient.Strategy.LlmGoalPolicy.ChainCombatSelfHealthSuppressed(thresholdHp + 5, 100)); // well above -> not suppressed
        Assert.False(HeadlessAcClient.Strategy.LlmGoalPolicy.ChainCombatSelfHealthSuppressed(null, 100));            // unknown current -> not suppressed
        Assert.False(HeadlessAcClient.Strategy.LlmGoalPolicy.ChainCombatSelfHealthSuppressed(80, 0));                // zero/unknown max -> not suppressed
    }

    [Theory]
    [InlineData(null, 5)]       // unset -> default
    [InlineData("", 5)]         // blank -> default
    [InlineData("abc", 5)]      // unparseable -> default
    [InlineData("5", 5)]        // explicit default
    [InlineData("1", 1)]        // min (accepted) -> escalation disabled (fixed base ttl)
    [InlineData("3", 3)]        // mid override
    [InlineData("20", 20)]      // max (accepted)
    [InlineData("0", 5)]        // below min -> default
    [InlineData("-2", 5)]       // negative -> default
    [InlineData("21", 20)]      // above max -> clamped
    [InlineData("100000", 20)]  // far above max -> clamped
    public void ResolveInteractUnreachableBackoffMax_DefaultsAndClamps(string? env, int expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveInteractUnreachableBackoffMax(env));
    }

    [Theory]
    [InlineData(null, 5)]       // unset -> default
    [InlineData("", 5)]         // blank -> default
    [InlineData("abc", 5)]      // unparseable -> default
    [InlineData("5", 5)]        // explicit default
    [InlineData("1", 1)]        // min (accepted) -> escalation disabled (fixed base ttl)
    [InlineData("3", 3)]        // mid override
    [InlineData("20", 20)]      // max (accepted)
    [InlineData("0", 5)]        // below min -> default
    [InlineData("-2", 5)]       // negative -> default
    [InlineData("21", 20)]      // above max -> clamped
    [InlineData("100000", 20)]  // far above max -> clamped
    public void ResolveNoDamageAbandonBackoffMax_DefaultsAndClamps(string? env, int expected)
    {
        Assert.Equal(expected, HandshakeDriver.ResolveNoDamageAbandonBackoffMax(env));
    }

    [Fact]
    public void OuterBudgetHeadroom_CoversWorstCaseReconnectOverhead()
    {
        // Headroom = login backoff ladder (5+10+15+20+25 = 75s) + per-attempt
        // connect waits (6 attempts * 10s = 60s) + 30s margin = 165s. It MUST
        // exceed the ~135s worst-case pre-observe reconnect overhead so a fresh
        // run gets its full observe window even after the maximum login retries.
        var headroom = HandshakeDriver.ComputeOuterBudgetHeadroomSeconds();
        Assert.Equal(165, headroom);
        Assert.Equal(headroom, HandshakeDriver.OuterBudgetHeadroomSeconds);
        Assert.True(headroom >= 135,
            $"headroom {headroom}s must cover the ~135s worst-case reconnect overhead");
    }

    [Fact]
    public void DerivedOuterBudget_ExceedsObserveWindowPlusOverhead()
    {
        // The outer CTS budget Program.cs uses is ObserveSeconds + headroom; for
        // the default observe window that is 3600 + 165 = 3765s. Assert the
        // relationship rather than a magic literal so the test tracks the derived
        // headroom.
        var outer = HandshakeDriver.ResolveObserveSeconds(null)
            + HandshakeDriver.OuterBudgetHeadroomSeconds;
        Assert.Equal(3765, outer);
        Assert.True(outer > HandshakeDriver.ResolveObserveSeconds(null),
            "outer budget must exceed the observe window");
    }

    [Fact]
    public void StaticFields_WireThroughTheirResolvers()
    {
        // The static fields read their env vars once at type-load via the
        // resolvers. Assert the wiring without hard-coding 3600/100 — this stays
        // correct even if the test environment sets the override env vars (the
        // earlier fixed-3600 assertion was fragile against exactly that).
        Assert.Equal(
            HandshakeDriver.ResolveObserveSeconds(
                System.Environment.GetEnvironmentVariable("AC_BOTS_OBSERVE_SECONDS")),
            HandshakeDriver.ObserveSeconds);
        Assert.Equal(
            HandshakeDriver.ResolveMaxActionsPerSession(
                System.Environment.GetEnvironmentVariable("AC_BOTS_MAX_ACTIONS_PER_SESSION")),
            HandshakeDriver.MaxActionsPerSession);
        Assert.Equal(
            HandshakeDriver.ResolveAbandonNoDamageSeconds(
                System.Environment.GetEnvironmentVariable("AC_BOTS_NO_DAMAGE_ABANDON_SECONDS")),
            HandshakeDriver.AbandonOnNoDamageSeconds);
    }

    [Theory]
    // (cleanEnabled, inWorldConfirmed, reconnectRequested, socketPresent) -> expected
    [InlineData(true,  true,  false, true,  true)]   // final graceful in-world exit -> SEND
    [InlineData(false, true,  false, true,  false)]  // feature disabled -> no send
    [InlineData(true,  false, false, true,  false)]  // world entry not confirmed -> no send
    [InlineData(true,  true,  true,  true,  false)]  // reconnect (re-enters to keep playing) -> no send
    [InlineData(true,  true,  false, false, false)]  // socket gone -> no send
    [InlineData(true,  false, true,  false, false)]  // multiple negatives -> no send
    public void ShouldSendCleanLogoff_OnlyOnFinalConfirmedInWorldExit(
        bool cleanEnabled, bool inWorld, bool reconnect, bool socket, bool expected)
    {
        Assert.Equal(expected,
            HandshakeDriver.ShouldSendCleanLogoff(cleanEnabled, inWorld, reconnect, socket));
    }

    [Fact]
    public void CleanLogoffOnExit_DefaultsOnAndParsesDisable()
    {
        // Default-on: the static field is true unless the env explicitly disables it.
        // (Assert the field reflects the current-process env parse, matching the
        // other static-field wiring tests.)
        var env = System.Environment.GetEnvironmentVariable("AC_BOTS_CLEAN_LOGOFF_ON_EXIT");
        var expected = (env ?? "1").Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off");
        Assert.Equal(expected, HandshakeDriver.CleanLogoffOnExit);
    }
}
