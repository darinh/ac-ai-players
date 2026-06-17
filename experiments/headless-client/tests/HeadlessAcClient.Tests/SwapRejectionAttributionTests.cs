// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for SwapRejectionAttribution — the pure helper that re-attributes a
// dequip-for-swap rejection from the BLOCKER item to the TARGET weapon the LLM
// asked to wield. This pins the contract the HandshakeDriver wiring relies on:
// a server rejection of the blocker dequip must be surfaced as a failure of the
// TARGET (its name/wcid), so the policy's target-keyed recently-rejected dedup
// matches the LLM's repeated Wield{target} and the wield loop breaks.

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class SwapRejectionAttributionTests
{
    // Synthetic fixtures (no game knowledge): a target weapon the LLM tried to
    // wield, blocked by a currently-wielded weapon that could not be dequipped.
    private const uint BlockerGuid = 0xAAAA0001u;
    private const uint TargetGuid = 0xAAAA0002u;
    private const uint TargetWcid = 7001u;
    private const string TargetName = "TargetWeapon";

    private static uint? OneSwap(uint blocker) =>
        blocker == BlockerGuid ? TargetGuid : (uint?)null;

    [Fact]
    public void ForRejectedBlocker_RejectedGuidIsBlocker_ReattributesToTarget()
    {
        var attr = SwapRejectionAttribution.ForRejectedBlocker(
            BlockerGuid,
            OneSwap,
            g => g == TargetGuid ? (TargetName, TargetWcid) : (null, null));

        Assert.NotNull(attr);
        Assert.Equal(TargetGuid, attr!.Value.TargetGuid);
        Assert.Equal(TargetName, attr.Value.Name);
        Assert.Equal(TargetWcid, attr.Value.Wcid);
    }

    [Fact]
    public void ForRejectedBlocker_RejectedGuidNotABlocker_ReturnsNull()
    {
        // An ordinary inventory failure (the rejected guid is not a pending swap
        // blocker) must NOT be re-attributed — the caller surfaces it normally.
        var attr = SwapRejectionAttribution.ForRejectedBlocker(
            0x12345678u,
            OneSwap,
            g => (TargetName, TargetWcid));

        Assert.Null(attr);
    }

    [Fact]
    public void ForRejectedBlocker_StaleEntryFilteredByCaller_ReturnsNull()
    {
        // The caller passes a FRESHNESS-FILTERED resolver: a stale / abandoned
        // swap resolves to null even for a guid that was once a blocker. The
        // helper must then NOT re-attribute — so a later, unrelated rejection of
        // that same item guid (e.g. a different action on it) surfaces normally
        // instead of being swallowed by a dead swap.
        var attr = SwapRejectionAttribution.ForRejectedBlocker(
            BlockerGuid,
            _ => null, // stale: the caller's freshness window yields no target
            g => (TargetName, TargetWcid));

        Assert.Null(attr);
    }

    [Fact]
    public void ForRejectedBlocker_TextContainsTargetName_ForSubstringDedupFallback()
    {
        var attr = SwapRejectionAttribution.ForRejectedBlocker(
            BlockerGuid,
            OneSwap,
            g => (TargetName, TargetWcid));

        Assert.NotNull(attr);
        Assert.Contains(TargetName, attr!.Value.Text);
        // The reason should say the wield could not happen — a human/LLM-legible
        // signal, not a raw blocker reference.
        Assert.Contains("wield", attr.Value.Text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForRejectedBlocker_UnknownTargetName_FallsBackToPlaceholder()
    {
        // The target guid is a pending blocker but the live projection has no
        // name for it yet — fall back to a placeholder rather than an empty
        // name (an empty Name would defeat the policy's name-keyed dedup).
        var attr = SwapRejectionAttribution.ForRejectedBlocker(
            BlockerGuid,
            OneSwap,
            g => (null, null));

        Assert.NotNull(attr);
        Assert.Equal(TargetGuid, attr!.Value.TargetGuid);
        Assert.False(string.IsNullOrWhiteSpace(attr.Value.Name));
        Assert.Null(attr.Value.Wcid);
    }

    [Fact]
    public void ForRejectedBlocker_WhitespaceTargetName_FallsBackToPlaceholder()
    {
        var attr = SwapRejectionAttribution.ForRejectedBlocker(
            BlockerGuid,
            OneSwap,
            g => ("   ", TargetWcid));

        Assert.NotNull(attr);
        Assert.False(string.IsNullOrWhiteSpace(attr!.Value.Name));
        Assert.NotEqual("   ", attr.Value.Name);
    }
}
