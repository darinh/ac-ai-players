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
}
