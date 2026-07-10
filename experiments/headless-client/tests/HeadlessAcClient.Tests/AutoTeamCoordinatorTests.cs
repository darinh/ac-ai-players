// SPDX-License-Identifier: AGPL-3.0-or-later
// auto-approve-allegiance: tests for AutoTeamCoordinator.ShouldAutoApproveAllegiance —
// the config-flagged (AC_BOTS_AUTO_TEAM) decision that lets the Motor auto-emit
// AllegianceApprove for a pending swear-allegiance request from a configured teammate
// (allegiance has no native auto-accept option, unlike fellowship).

using System;
using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class AutoTeamCoordinatorTests
{
    private static readonly IReadOnlyCollection<string> Team =
        new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AutoApprove_True_WhenEnabledAndTeammateRequestPending()
    {
        var pending = new PendingAllegianceRequest(7u, "Mba");
        Assert.True(AutoTeamCoordinator.ShouldAutoApproveAllegiance(true, Team, pending));
    }

    [Fact]
    public void AutoApprove_CaseInsensitiveTeammateMatch()
    {
        var pending = new PendingAllegianceRequest(7u, "mba");
        Assert.True(AutoTeamCoordinator.ShouldAutoApproveAllegiance(true, Team, pending));
    }

    [Fact]
    public void AutoApprove_False_WhenDisabled()
    {
        var pending = new PendingAllegianceRequest(7u, "Mba");
        Assert.False(AutoTeamCoordinator.ShouldAutoApproveAllegiance(false, Team, pending));
    }

    [Fact]
    public void AutoApprove_False_WhenNoRequestPending()
    {
        Assert.False(AutoTeamCoordinator.ShouldAutoApproveAllegiance(true, Team, null));
    }

    [Fact]
    public void AutoApprove_False_WhenRequesterNotAConfiguredTeammate()
    {
        // A swear from a NON-configured player is left to the LLM (not auto-approved).
        var pending = new PendingAllegianceRequest(7u, "Stranger");
        Assert.False(AutoTeamCoordinator.ShouldAutoApproveAllegiance(true, Team, pending));
    }

    [Fact]
    public void AutoApprove_False_WhenTeamConfigEmpty()
    {
        var pending = new PendingAllegianceRequest(7u, "Mba");
        Assert.False(AutoTeamCoordinator.ShouldAutoApproveAllegiance(
            true, Array.Empty<string>(), pending));
    }
}
