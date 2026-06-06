// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the SelfProgressChanged structural wake (cp-2280): a one-shot,
// per-connection salience nudge fired the FIRST time the bot's unspent XP
// is known. A direct analogue of the CombatFeedback wake — it surfaces RAW
// self facts (unspent XP, total, current/peak HP, level), assigns no
// urgency, names no attribute, applies no magnitude judgment, and says
// nothing about spending, so the prompt RULES stay the sole owner of WHAT
// to do.

using HeadlessAcClient.Protocol;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class SelfProgressWakeTests
{
    [Fact]
    public void TryBuild_FirstKnownXp_EmitsAndSetsOneShot()
    {
        bool sent = false;
        var built = HandshakeDriver.TryBuildSelfProgressEvent(
            unspentXp: 82659, totalXp: 84199, level: 10,
            hpCurrent: 3, hpMax: 5, ref sent, out var ev, out var log);

        Assert.True(built);
        Assert.True(sent);
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
        bool sent = false;
        HandshakeDriver.TryBuildSelfProgressEvent(
            82659, 84199, 10, 3, 5, ref sent, out var ev, out _);

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
    public void TryBuild_SecondCall_IsSuppressedOneShot()
    {
        bool sent = false;
        Assert.True(HandshakeDriver.TryBuildSelfProgressEvent(
            82659, 84199, 10, 3, 5, ref sent, out _, out _));

        // Once fired, no later reading re-emits — even a large change. The
        // ## Self line and the SPEND XP prompt rule own ongoing reaction.
        Assert.False(HandshakeDriver.TryBuildSelfProgressEvent(
            150000, 160000, 11, 8, 12, ref sent, out _, out _));
        Assert.True(sent);
    }

    [Fact]
    public void TryBuild_UnknownXp_DoesNotEmitOrTripOneShot()
    {
        bool sent = false;
        Assert.False(HandshakeDriver.TryBuildSelfProgressEvent(
            unspentXp: null, totalXp: null, level: null,
            hpCurrent: null, hpMax: null, ref sent, out _, out _));
        // One-shot NOT tripped: a later known reading must still be able to
        // fire (login may surface XP after some null-XP messages).
        Assert.False(sent);
        Assert.True(HandshakeDriver.TryBuildSelfProgressEvent(
            500, null, null, null, null, ref sent, out _, out _));
        Assert.True(sent);
    }

    [Fact]
    public void TryBuild_MissingOptionalFacts_RendersUnknownPlaceholders()
    {
        bool sent = false;
        var built = HandshakeDriver.TryBuildSelfProgressEvent(
            unspentXp: 500, totalXp: null, level: null,
            hpCurrent: null, hpMax: null, ref sent, out var ev, out _);

        Assert.True(built);
        Assert.Contains("500 unspent experience", ev.Text);
        Assert.Contains("unknown total", ev.Text);
        Assert.Contains("unknown HP", ev.Text);
        Assert.DoesNotContain("level ", ev.Text);
    }

    [Fact]
    public void TryBuild_ZeroXpIsKnown_EmitsOnce()
    {
        bool sent = false;
        // 0 is a valid known balance — surface it once, then suppress.
        Assert.True(HandshakeDriver.TryBuildSelfProgressEvent(
            0, 0, 1, 10, 10, ref sent, out var ev, out _));
        Assert.Contains("0 unspent experience", ev.Text);
        Assert.False(HandshakeDriver.TryBuildSelfProgressEvent(
            0, 0, 1, 10, 10, ref sent, out _, out _));
    }

    [Fact]
    public void IsSalientKind_SelfProgressChanged_WakesLlm()
    {
        Assert.True(LlmGoalPolicy.IsSalientKind(EventKind.SelfProgressChanged));
    }
}
