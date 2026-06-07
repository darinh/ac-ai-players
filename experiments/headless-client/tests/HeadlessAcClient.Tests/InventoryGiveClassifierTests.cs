using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Tests for <see cref="InventoryGiveClassifier.IsServerGive"/>: the
/// pure gate that recognizes an NPC quest-reward / hand-off (a fresh
/// ObjectCreate placed directly in the player's pack) so the Motor can
/// emit the InventoryItemAdded acquisition event the loot put-ack path
/// would otherwise be the only source of. Live repro: Worcer gave
/// "A List of Items" repeatedly; the give produced no acquisition
/// event, the quest intent's `+inv name~"List of Items">=1` predicate
/// never fired, and the bot Talk-looped Worcer hoarding 6 duplicates.
/// </summary>
public class InventoryGiveClassifierTests
{
    private const uint SelfGuid = 0x50000123u;
    private const uint OtherContainer = 0x70ABCDEFu; // e.g. a corpse

    [Fact]
    public void NewSelfContainerObjectAfterFirehose_IsServerGive()
    {
        // The live failure: a brand-new item lands in our own pack
        // after login has settled — a quest reward give.
        Assert.True(InventoryGiveClassifier.IsServerGive(
            applied: true,
            preCreateKnown: false,
            initialInventorySettled: true,
            selfGuid: SelfGuid,
            containerGuid: SelfGuid));
    }

    [Fact]
    public void StaleMessageNotApplied_IsNotGive()
    {
        // WorldState dropped the message (stale instance) — nothing
        // was actually added, so no acquisition event.
        Assert.False(InventoryGiveClassifier.IsServerGive(
            applied: false,
            preCreateKnown: false,
            initialInventorySettled: true,
            selfGuid: SelfGuid,
            containerGuid: SelfGuid));
    }

    [Fact]
    public void AlreadyKnownGuid_IsNotGive()
    {
        // A looted item's re-broadcast ObjectCreate (container flips to
        // self) is already known; its acquisition was surfaced by the
        // put-ack path, so this must not double-emit.
        Assert.False(InventoryGiveClassifier.IsServerGive(
            applied: true,
            preCreateKnown: true,
            initialInventorySettled: true,
            selfGuid: SelfGuid,
            containerGuid: SelfGuid));
    }

    [Fact]
    public void BeforeFirehoseSettled_IsNotGive()
    {
        // Items the character already carries at login arrive as
        // self-container ObjectCreates during the firehose; they must
        // not be reported as fresh acquisitions.
        Assert.False(InventoryGiveClassifier.IsServerGive(
            applied: true,
            preCreateKnown: false,
            initialInventorySettled: false,
            selfGuid: SelfGuid,
            containerGuid: SelfGuid));
    }

    [Fact]
    public void ContainerIsNotSelf_IsNotGive()
    {
        // A new object in someone else's container (e.g. corpse loot
        // contents before we take them) is not in our inventory.
        Assert.False(InventoryGiveClassifier.IsServerGive(
            applied: true,
            preCreateKnown: false,
            initialInventorySettled: true,
            selfGuid: SelfGuid,
            containerGuid: OtherContainer));
    }

    [Fact]
    public void NoContainer_IsNotGive()
    {
        // A landscape/world item carries no container.
        Assert.False(InventoryGiveClassifier.IsServerGive(
            applied: true,
            preCreateKnown: false,
            initialInventorySettled: true,
            selfGuid: SelfGuid,
            containerGuid: null));
    }

    [Fact]
    public void SelfGuidUnknown_IsNotGive()
    {
        // Before our own player object is established we cannot know
        // an item is in OUR pack.
        Assert.False(InventoryGiveClassifier.IsServerGive(
            applied: true,
            preCreateKnown: false,
            initialInventorySettled: true,
            selfGuid: null,
            containerGuid: SelfGuid));
    }

    // --- ShouldMarkInventorySettled: the one-shot firehose latch ---

    private const int Grace = 30;
    private const int LoginIdx = 100;

    [Fact]
    public void Settle_BeforeLogin_False()
    {
        Assert.False(InventoryGiveClassifier.ShouldMarkInventorySettled(
            loginCompleteSent: false,
            packetIndex: 200,
            loginCompletePacketIndex: -1,
            lastSelfInventoryCreatePacketIndex: -1,
            gracePackets: Grace));
    }

    [Fact]
    public void Settle_WithinPostLoginGrace_False()
    {
        // Only 10 packets past LoginComplete — firehose may still run.
        Assert.False(InventoryGiveClassifier.ShouldMarkInventorySettled(
            loginCompleteSent: true,
            packetIndex: LoginIdx + 10,
            loginCompletePacketIndex: LoginIdx,
            lastSelfInventoryCreatePacketIndex: -1,
            gracePackets: Grace));
    }

    [Fact]
    public void Settle_EmptyPackAfterGrace_True()
    {
        // No starter inventory ever (index stays -1): the `< 0` clause
        // lets the latch flip purely on the post-login grace, so a give
        // arriving later still emits.
        Assert.True(InventoryGiveClassifier.ShouldMarkInventorySettled(
            loginCompleteSent: true,
            packetIndex: LoginIdx + Grace,
            loginCompletePacketIndex: LoginIdx,
            lastSelfInventoryCreatePacketIndex: -1,
            gracePackets: Grace));
    }

    [Fact]
    public void Settle_SelfCreateTooRecent_False()
    {
        // A self-inventory create just happened (the firehose is still
        // bursting, OR — at this same packet — the create was recorded
        // before the latch eval); the quiescence window is not met, so
        // the latch must NOT flip on this packet. This is the ordering
        // guard that stops a fresh self-create emitting itself as a give.
        Assert.False(InventoryGiveClassifier.ShouldMarkInventorySettled(
            loginCompleteSent: true,
            packetIndex: LoginIdx + Grace + 5,
            loginCompletePacketIndex: LoginIdx,
            lastSelfInventoryCreatePacketIndex: LoginIdx + Grace + 5,
            gracePackets: Grace));
    }

    [Fact]
    public void Settle_AfterFirehoseQuietForGrace_True()
    {
        // Starter firehose's last self-create was a full grace window
        // ago and we're past the post-login grace: the latch flips.
        var lastCreate = LoginIdx + 5;
        Assert.True(InventoryGiveClassifier.ShouldMarkInventorySettled(
            loginCompleteSent: true,
            packetIndex: lastCreate + Grace,
            loginCompletePacketIndex: LoginIdx,
            lastSelfInventoryCreatePacketIndex: lastCreate,
            gracePackets: Grace));
    }

    [Fact]
    public void Settle_PastLoginGraceButFirehoseStillBursting_False()
    {
        // Past the post-login grace, but a starter create landed only a
        // few packets ago — the firehose is still active, so the longer
        // quiescence requirement keeps the latch closed (the v1-review
        // concern: a firehose longer than the raw packet grace).
        Assert.False(InventoryGiveClassifier.ShouldMarkInventorySettled(
            loginCompleteSent: true,
            packetIndex: LoginIdx + Grace + 2,
            loginCompletePacketIndex: LoginIdx,
            lastSelfInventoryCreatePacketIndex: LoginIdx + Grace,
            gracePackets: Grace));
    }
}
