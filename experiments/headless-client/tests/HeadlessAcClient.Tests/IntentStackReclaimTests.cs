// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit tests for IntentStackReclaim — the pure overflow-reclaim policy — and the
// IntentStack overflow-eviction behavior it backs.

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Strategy.Intent;
using Xunit;

namespace HeadlessAcClient.Tests;

public class IntentStackReclaimTests
{
    private static readonly DateTime Now = new(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc);

    private static IntentStackReclaim.FrameView Active(int? deadlineOffsetSec = null) =>
        new(IntentLifecycle.Active, deadlineOffsetSec is int s ? Now.AddSeconds(s) : null);

    private static IntentStackReclaim.FrameView Done() => new(IntentLifecycle.Completed, null);
    private static IntentStackReclaim.FrameView Expired() => new(IntentLifecycle.Expired, null);
    private static IntentStackReclaim.FrameView Blocked() => new(IntentLifecycle.Blocked, null);

    private static List<int> Survivors(IReadOnlyList<IntentStackReclaim.FrameView> f, int maxDepth, bool evict) =>
        IntentStackReclaim.SurvivorsForPush(f, maxDepth, Now, evict);

    // ---- tier 1 (terminal reclaim) — runs with eviction on OR off ----

    [Fact]
    public void EvictOff_AllActiveFull_KeepsAll()
    {
        var f = new[] { Active(), Active(), Active() };
        Assert.Equal(new[] { 0, 1, 2 }, Survivors(f, 3, evict: false));
    }

    [Fact]
    public void EvictOff_DropsBuriedTerminal_KeepsActiveAndBlocked()
    {
        var f = new[] { Done(), Active(), Expired(), Blocked() };
        Assert.Equal(new[] { 1, 3 }, Survivors(f, 4, evict: false));
    }

    [Fact]
    public void AllTerminal_KeepsNewest_NeverEmpty()
    {
        // Full (maxDepth 2) so the reclaim tiers run; all terminal -> keep newest.
        var f = new[] { Done(), Expired() };
        Assert.Equal(new[] { 1 }, Survivors(f, 2, evict: true));
    }

    [Fact]
    public void NotFull_NoEviction()
    {
        var f = new[] { Active(), Active() };
        Assert.Equal(new[] { 0, 1 }, Survivors(f, 3, evict: true));
    }

    // ---- tier 2 (deadline-elapsed buried Active) ----

    [Fact]
    public void EvictOn_BuriedDeadlineElapsed_DroppedBeforeOldest()
    {
        // root(no dl), buried Active past deadline, top(no dl). Tier 2 drops the
        // expired buried frame rather than the (gentler-first) tier-3 oldest.
        var f = new[] { Active(), Active(deadlineOffsetSec: -1), Active() };
        Assert.Equal(new[] { 0, 2 }, Survivors(f, 3, evict: true));
    }

    [Fact]
    public void EvictOn_MultipleElapsed_AllElapsedDropped()
    {
        var f = new[] { Active(), Active(-5), Active(-1), Active() };
        Assert.Equal(new[] { 0, 3 }, Survivors(f, 4, evict: true));
    }

    [Fact]
    public void EvictOn_FutureDeadline_NotDropped_FallsToOldest()
    {
        // No frame is past deadline (the buried one's deadline is in the future),
        // so tier 2 drops nothing and tier 3 evicts the single oldest non-root.
        var f = new[] { Active(), Active(deadlineOffsetSec: +600), Active() };
        Assert.Equal(new[] { 0, 2 }, Survivors(f, 3, evict: true));
    }

    [Fact]
    public void EvictOn_RootDeadlineElapsed_RootPreserved()
    {
        // The bottom frame (index 0) is never dropped by tiers 2-3 even if its own
        // deadline elapsed; tier 3 evicts the oldest NON-root instead.
        var f = new[] { Active(deadlineOffsetSec: -10), Active(), Active() };
        Assert.Equal(new[] { 0, 2 }, Survivors(f, 3, evict: true));
    }

    // ---- tier 3 (last-resort oldest non-root) ----

    [Fact]
    public void EvictOn_AllActiveNoDeadline_DropsOldestNonRoot()
    {
        var f = new[] { Active(), Active(), Active() };
        Assert.Equal(new[] { 0, 2 }, Survivors(f, 3, evict: true));
    }

    [Fact]
    public void EvictOn_PrefersActiveOverDurableBlocked()
    {
        // Blocked markers carry state the prompt relies on, so tier 3 keeps the
        // Blocked frame and evicts a non-root Active frame instead.
        var f = new[] { Active(), Blocked(), Active() };
        Assert.Equal(new[] { 0, 1 }, Survivors(f, 3, evict: true));
    }

    [Fact]
    public void EvictOn_AllNonRootBlocked_EvictsOldestBlockedAsFinalFallback()
    {
        // No non-root Active frame remains -> the last-resort tier still frees the
        // oldest non-root (a Blocked frame) so a new push is never permanently refused.
        var f = new[] { Active(), Blocked(), Blocked() };
        Assert.Equal(new[] { 0, 2 }, Survivors(f, 3, evict: true));
    }

    [Fact]
    public void EvictOn_MaxDepthTwo_DropsToRootOnly()
    {
        var f = new[] { Active(), Active() };
        Assert.Equal(new[] { 0 }, Survivors(f, 2, evict: true));
    }

    [Fact]
    public void EvictOn_TerminalReclaimAloneMakesRoom_NoFurtherEviction()
    {
        // A buried terminal frame is enough to make room -> tiers 2/3 do not run.
        var f = new[] { Done(), Active(), Active(), Active() };
        Assert.Equal(new[] { 1, 2, 3 }, Survivors(f, 4, evict: true));
    }

    // ---- env resolver (default ON; opt OUT) ----

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("off", false)]
    [InlineData("Off", false)]
    public void ResolveEvictOnOverflow_DefaultsOn_OptOutOnFalsey(string? env, bool expected)
    {
        Assert.Equal(expected, IntentStack.ResolveEvictOnOverflow(env));
    }
}
