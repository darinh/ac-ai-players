using System;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Tests for <see cref="InteractReachClassifier.IsOutOfReach"/>: the
/// pure gate that reclassifies a falsely-"arrived" Use/Pickup action
/// cycle as FAILED when the server answered our dispatch with a
/// MoveToObject for our own object toward the same target ("you are
/// not in range; walking you to it"). Live repro: a chest at Z=94
/// directly below the bot on a ledge at Z=113.88 — XY within 1u, so
/// the XY-only arrival check reported "arrived" while 3D distance was
/// ~20u, the Use never landed, and the bot looped forever.
/// </summary>
public class InteractReachClassifierTests
{
    private const uint ChestGuid = 0x7A9B4072u;
    private static readonly DateTime Dispatch = new(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Dispatched_ServerWalkedUsTowardSameTargetAfterUse_IsOutOfReach()
    {
        // The live failure: USE sent at Dispatch, server replied
        // MoveToObject(target=chest) ~5ms later.
        Assert.True(InteractReachClassifier.IsOutOfReach(
            worldInteractDispatched: true,
            targetGuid: ChestGuid,
            lastSelfMoveToObjectGuid: ChestGuid,
            useSentAt: Dispatch,
            lastSelfMoveToObjectAt: Dispatch.AddMilliseconds(5)));
    }

    [Fact]
    public void ServerReplyAtExactDispatchInstant_IsOutOfReach()
    {
        // The `>=` boundary: a same-instant reply still counts as
        // post-dispatch evidence.
        Assert.True(InteractReachClassifier.IsOutOfReach(
            worldInteractDispatched: true,
            targetGuid: ChestGuid,
            lastSelfMoveToObjectGuid: ChestGuid,
            useSentAt: Dispatch,
            lastSelfMoveToObjectAt: Dispatch));
    }

    [Fact]
    public void NoInteractionDispatched_NotOutOfReach()
    {
        // A nav-only arrival / Explore / Talk completion (no Use/Pickup
        // opcode) must never be classified as a failed interaction.
        Assert.False(InteractReachClassifier.IsOutOfReach(
            worldInteractDispatched: false,
            targetGuid: ChestGuid,
            lastSelfMoveToObjectGuid: ChestGuid,
            useSentAt: Dispatch,
            lastSelfMoveToObjectAt: Dispatch.AddMilliseconds(5)));
    }

    [Fact]
    public void NoServerMoveToObject_NotOutOfReach()
    {
        // In-range success: server draws a TurnToObject, not a
        // MoveToObject, so the recorded self-MoveToObject guid is null.
        Assert.False(InteractReachClassifier.IsOutOfReach(
            worldInteractDispatched: true,
            targetGuid: ChestGuid,
            lastSelfMoveToObjectGuid: null,
            useSentAt: Dispatch,
            lastSelfMoveToObjectAt: null));
    }

    [Fact]
    public void ServerMovedTowardDifferentObject_NotOutOfReach()
    {
        // The server repositioned us toward some OTHER object (not our
        // interaction target) — not evidence our target was unreachable.
        Assert.False(InteractReachClassifier.IsOutOfReach(
            worldInteractDispatched: true,
            targetGuid: ChestGuid,
            lastSelfMoveToObjectGuid: 0x8000A4DDu,
            useSentAt: Dispatch,
            lastSelfMoveToObjectAt: Dispatch.AddMilliseconds(5)));
    }

    [Fact]
    public void ServerMoveToObjectBeforeDispatch_NotOutOfReach()
    {
        // A MoveToObject observed during the APPROACH phase (before the
        // interaction was dispatched) must not mis-trigger: the `>=
        // useSentAt` gate rejects it.
        Assert.False(InteractReachClassifier.IsOutOfReach(
            worldInteractDispatched: true,
            targetGuid: ChestGuid,
            lastSelfMoveToObjectGuid: ChestGuid,
            useSentAt: Dispatch,
            lastSelfMoveToObjectAt: Dispatch.AddMilliseconds(-200)));
    }

    [Fact]
    public void NullTargetGuid_NotOutOfReach()
    {
        Assert.False(InteractReachClassifier.IsOutOfReach(
            worldInteractDispatched: true,
            targetGuid: null,
            lastSelfMoveToObjectGuid: ChestGuid,
            useSentAt: Dispatch,
            lastSelfMoveToObjectAt: Dispatch.AddMilliseconds(5)));
    }

    [Fact]
    public void NoUseSentTimestamp_NotOutOfReach()
    {
        Assert.False(InteractReachClassifier.IsOutOfReach(
            worldInteractDispatched: true,
            targetGuid: ChestGuid,
            lastSelfMoveToObjectGuid: ChestGuid,
            useSentAt: null,
            lastSelfMoveToObjectAt: Dispatch.AddMilliseconds(5)));
    }
}
