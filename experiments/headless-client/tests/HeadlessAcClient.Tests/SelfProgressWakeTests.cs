// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the SelfProgressChanged structural wake (cp-2280, generalized to
// a consecutive value-edge): a per-connection salience nudge fired the FIRST
// time the bot's unspent XP is known AND whenever the decoded unspent-XP value
// changes. A direct analogue of the CombatFeedback wake — it surfaces RAW
// self facts (unspent XP, total, current/peak HP, level), assigns no urgency,
// names no attribute, applies no magnitude judgment, and says nothing about
// spending, so the prompt RULES stay the sole owner of WHAT to do.

using HeadlessAcClient.Protocol;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class SelfProgressWakeTests
{
    [Fact]
    public void TryBuild_FirstKnownXp_EmitsAndStampsValue()
    {
        long? last = null;
        var built = HandshakeDriver.TryBuildSelfProgressEvent(
            unspentXp: 82659, totalXp: 84199, level: 10,
            hpCurrent: 3, hpMax: 5, ref last, out var ev, out var log);

        Assert.True(built);
        Assert.Equal(82659, last);
        Assert.Equal(EventKind.SelfProgressChanged, ev.Kind);
        Assert.Contains("82659 unspent experience", ev.Text);
        Assert.Contains("84199 total", ev.Text);
        Assert.Contains("level 10", ev.Text);
        Assert.Contains("3/5 HP", ev.Text);
        Assert.Contains("unspent=82659", log);
    }

    [Fact]
    public void TryBuild_TextHasNoDirectiveOrAttribute()
    {
        long? last = null;
        HandshakeDriver.TryBuildSelfProgressEvent(
            82659, 84199, 10, 3, 5, ref last, out var ev, out _);

        // Raw facts only: never tell the LLM WHAT to do or WHICH attribute.
        var text = ev.Text!.ToLowerInvariant();
        Assert.DoesNotContain("should", text);
        Assert.DoesNotContain("spend", text);
        Assert.DoesNotContain("urgent", text);
        Assert.DoesNotContain("endurance", text);
        Assert.DoesNotContain("strength", text);
        Assert.DoesNotContain("raise", text);
    }

    [Fact]
    public void TryBuild_SameValueAgain_IsSuppressed()
    {
        long? last = null;
        Assert.True(HandshakeDriver.TryBuildSelfProgressEvent(
            82659, 84199, 10, 3, 5, ref last, out _, out _));

        // Unchanged unspent-XP value (even with other facts moving) does not
        // re-emit — only a value-edge wakes the LLM.
        Assert.False(HandshakeDriver.TryBuildSelfProgressEvent(
            82659, 90000, 11, 8, 12, ref last, out _, out _));
        Assert.Equal(82659, last);
    }

    [Fact]
    public void TryBuild_ChangedValue_ReEmits()
    {
        long? last = null;
        Assert.True(HandshakeDriver.TryBuildSelfProgressEvent(
            82659, 84199, 10, 3, 5, ref last, out _, out _));

        // A NEW unspent-XP value (e.g. after an instant XP-spend) re-emits so
        // the LLM wakes to re-read ## Self instead of idling to the stuck
        // timeout. This is the post-spend tempo fix.
        Assert.True(HandshakeDriver.TryBuildSelfProgressEvent(
            81659, 84199, 10, 3, 5, ref last, out var ev, out _));
        Assert.Contains("81659 unspent experience", ev.Text);
        Assert.Equal(81659, last);

        // And a further distinct change re-emits again.
        Assert.True(HandshakeDriver.TryBuildSelfProgressEvent(
            80659, 84199, 10, 3, 5, ref last, out _, out _));
        Assert.Equal(80659, last);
    }

    [Fact]
    public void TryBuild_UnknownXp_DoesNotEmitOrStamp()
    {
        long? last = null;
        Assert.False(HandshakeDriver.TryBuildSelfProgressEvent(
            unspentXp: null, totalXp: null, level: null,
            hpCurrent: null, hpMax: null, ref last, out _, out _));
        // Stamp NOT set: a later known reading must still be able to fire
        // (login may surface XP after some null-XP messages).
        Assert.Null(last);
        Assert.True(HandshakeDriver.TryBuildSelfProgressEvent(
            500, null, null, null, null, ref last, out _, out _));
        Assert.Equal(500, last);
    }

    [Fact]
    public void TryBuild_MissingOptionalFacts_RendersUnknownPlaceholders()
    {
        long? last = null;
        var built = HandshakeDriver.TryBuildSelfProgressEvent(
            unspentXp: 500, totalXp: null, level: null,
            hpCurrent: null, hpMax: null, ref last, out var ev, out _);

        Assert.True(built);
        Assert.Contains("500 unspent experience", ev.Text);
        Assert.Contains("unknown total", ev.Text);
        Assert.Contains("unknown HP", ev.Text);
        Assert.DoesNotContain("level ", ev.Text);
    }

    [Fact]
    public void TryBuild_ZeroXpIsKnown_EmitsOnceThenSuppressesSameValue()
    {
        long? last = null;
        // 0 is a valid known balance — surface it once, then suppress the
        // same value.
        Assert.True(HandshakeDriver.TryBuildSelfProgressEvent(
            0, 0, 1, 10, 10, ref last, out var ev, out _));
        Assert.Contains("0 unspent experience", ev.Text);
        Assert.Equal(0, last);
        Assert.False(HandshakeDriver.TryBuildSelfProgressEvent(
            0, 0, 1, 10, 10, ref last, out _, out _));
    }

    [Fact]
    public void IsSalientKind_SelfProgressChanged_WakesLlm()
    {
        Assert.True(LlmGoalPolicy.IsSalientKind(EventKind.SelfProgressChanged));
    }

    [Fact]
    public void IsSalientKind_InboundDamageTaken_WakesLlm()
    {
        // inbound-damage-onset-wake: the defensive analogue of CombatFeedback
        // must wake the LLM so it re-reads ## Combat readiness the moment it
        // starts taking damage.
        Assert.True(LlmGoalPolicy.IsSalientKind(EventKind.InboundDamageTaken));
    }
}
