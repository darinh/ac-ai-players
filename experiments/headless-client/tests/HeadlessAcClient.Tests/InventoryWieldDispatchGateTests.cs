// SPDX-License-Identifier: AGPL-3.0-or-later

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class InventoryWieldDispatchGateTests
{
    [Fact]
    public void IsOwnedByActor_ItemInBag_IsTrue()
        => Assert.True(InventoryWieldDispatchGate.IsOwnedByActor(
            0x500u,
            containerGuid: 0x500u,
            wielderGuid: null));

    [Fact]
    public void IsOwnedByActor_ItemWornByActor_IsTrue()
        => Assert.True(InventoryWieldDispatchGate.IsOwnedByActor(
            0x500u,
            containerGuid: null,
            wielderGuid: 0x500u));

    [Fact]
    public void IsOwnedByActor_ItemOwnedByAnotherActor_IsFalse()
        => Assert.False(InventoryWieldDispatchGate.IsOwnedByActor(
            0x500u,
            containerGuid: 0x600u,
            wielderGuid: 0x600u));

    [Fact]
    public void Evaluate_NoPendingWield_Dispatches()
        => Assert.Equal(
            InventoryWieldDispatchDecision.Dispatch,
            InventoryWieldDispatchGate.Evaluate(
                0x100u,
                targetAlreadyWielded: false,
                pendingItemGuids: []));

    [Fact]
    public void Evaluate_AlreadyWielded_CompletesWithoutDispatch()
        => Assert.Equal(
            InventoryWieldDispatchDecision.AlreadyWielded,
            InventoryWieldDispatchGate.Evaluate(
                0x100u,
                targetAlreadyWielded: true,
                pendingItemGuids: []));

    [Fact]
    public void Evaluate_SameTargetPending_SerializesDuplicate()
        => Assert.Equal(
            InventoryWieldDispatchDecision.SameTargetPending,
            InventoryWieldDispatchGate.Evaluate(
                0x100u,
                targetAlreadyWielded: false,
                pendingItemGuids: [0x100u]));

    [Fact]
    public void Evaluate_DifferentTargetPending_SerializesRetarget()
        => Assert.Equal(
            InventoryWieldDispatchDecision.DifferentTargetPending,
            InventoryWieldDispatchGate.Evaluate(
                0x200u,
                targetAlreadyWielded: false,
                pendingItemGuids: [0x100u]));

    [Fact]
    public void Evaluate_AnyDifferentTargetWinsOverSameTarget()
        => Assert.Equal(
            InventoryWieldDispatchDecision.DifferentTargetPending,
            InventoryWieldDispatchGate.Evaluate(
                0x200u,
                targetAlreadyWielded: false,
                pendingItemGuids: [0x200u, 0x100u]));

    [Fact]
    public void Evaluate_PendingOtherTarget_WinsOverAlreadyWielded()
        => Assert.Equal(
            InventoryWieldDispatchDecision.DifferentTargetPending,
            InventoryWieldDispatchGate.Evaluate(
                0x200u,
                targetAlreadyWielded: true,
                pendingItemGuids: [0x100u]));

    [Fact]
    public void Evaluate_AlreadyWielded_WinsOverPendingSameTarget()
        => Assert.Equal(
            InventoryWieldDispatchDecision.AlreadyWielded,
            InventoryWieldDispatchGate.Evaluate(
                0x200u,
                targetAlreadyWielded: true,
                pendingItemGuids: [0x200u]));
}
