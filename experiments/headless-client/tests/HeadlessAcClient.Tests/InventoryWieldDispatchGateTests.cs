// SPDX-License-Identifier: AGPL-3.0-or-later

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class InventoryWieldDispatchGateTests
{
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
    public void Evaluate_AlreadyWielded_WinsOverPendingOtherTarget()
        => Assert.Equal(
            InventoryWieldDispatchDecision.AlreadyWielded,
            InventoryWieldDispatchGate.Evaluate(
                0x200u,
                targetAlreadyWielded: true,
                pendingItemGuids: [0x100u]));
}
