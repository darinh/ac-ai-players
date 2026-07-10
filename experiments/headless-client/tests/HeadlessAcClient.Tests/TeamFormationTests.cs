// SPDX-License-Identifier: AGPL-3.0-or-later
// team-formation-diagnostic: tests for TeamFormation.Describe, the pure snapshot the
// Motor logs (once per change) to make operator-declared multi-bot fellowship /
// allegiance formation progress greppable. Covers: no-team => null; ungrouped visible
// -teammate count; leader mid-formation next-recruit + complete; follower vassal
// states; self folded into the roster count; configured-only member counting (a
// stranger must not inflate progress); and change-detectability / stability.

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class TeamFormationTests
{
    private static readonly string[] NoNames = Array.Empty<string>();
    private static readonly string?[] None = Array.Empty<string?>();

    [Fact]
    public void NoTeammatesConfigured_ReturnsNull()
    {
        var s = TeamFormation.Describe(
            NoNames, selfName: "Mba", inFellowship: false, amLeader: false,
            fellowshipLeaderName: null, currentMemberNames: None,
            visiblePlayerNames: None, isInAllegiance: false, isOwnMonarch: false);
        Assert.Null(s);
    }

    [Fact]
    public void Ungrouped_NoVisibleTeammates_ReportsZeroVisible()
    {
        var s = TeamFormation.Describe(
            new[] { "Mbb" }, selfName: "Mba", inFellowship: false, amLeader: false,
            fellowshipLeaderName: null, currentMemberNames: None,
            visiblePlayerNames: None, isInAllegiance: false, isOwnMonarch: false);
        Assert.Equal("role=ungrouped fellowship=no members=0/2 visible-teammates=0", s);
    }

    [Fact]
    public void Ungrouped_WithVisibleTeammate_CountsIt()
    {
        // A non-teammate visible player must NOT be counted.
        var s = TeamFormation.Describe(
            new[] { "Mbb" }, selfName: "Mba", inFellowship: false, amLeader: false,
            fellowshipLeaderName: null, currentMemberNames: None,
            visiblePlayerNames: new string?[] { "Mbb", "SomeStranger" },
            isInAllegiance: false, isOwnMonarch: false);
        Assert.Equal("role=ungrouped fellowship=no members=0/2 visible-teammates=1", s);
    }

    [Fact]
    public void Leader_MidFormation_ShowsNextUnrecruitedTeammate()
    {
        // 6-bot roster (self listed); self + Mbb are members; Mbc..Mbf still to recruit.
        var roster = new[] { "Mba", "Mbb", "Mbc", "Mbd", "Mbe", "Mbf" };
        var s = TeamFormation.Describe(
            roster, selfName: "Mba", inFellowship: true, amLeader: true,
            fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb" },
            visiblePlayerNames: new string?[] { "Mbb", "Mbc", "Mbd", "Mbe", "Mbf" },
            isInAllegiance: true, isOwnMonarch: true);
        Assert.Equal("role=leader fellowship=yes members=2/6 next-recruit=Mbc", s);
    }

    [Fact]
    public void Leader_AllVisibleTeammatesRecruited_ShowsNone()
    {
        var roster = new[] { "Mba", "Mbb" };
        var s = TeamFormation.Describe(
            roster, selfName: "Mba", inFellowship: true, amLeader: true,
            fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb" },
            visiblePlayerNames: new string?[] { "Mbb" },
            isInAllegiance: false, isOwnMonarch: false);
        Assert.Equal("role=leader fellowship=yes members=2/2 next-recruit=none", s);
    }

    [Fact]
    public void Follower_NotYetVassal_ReportsVassalNo()
    {
        var s = TeamFormation.Describe(
            new[] { "Mba" }, selfName: "Mbb", inFellowship: true, amLeader: false,
            fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb" },
            visiblePlayerNames: None,
            isInAllegiance: true, isOwnMonarch: true);   // own monarch => not a vassal
        Assert.Equal("role=follower fellowship=yes members=2/2 leader=Mba vassal=no", s);
    }

    [Fact]
    public void Follower_Vassal_ReportsVassalYes()
    {
        var s = TeamFormation.Describe(
            new[] { "Mba" }, selfName: "Mbb", inFellowship: true, amLeader: false,
            fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb" },
            visiblePlayerNames: None,
            isInAllegiance: true, isOwnMonarch: false);  // vassal under another monarch
        Assert.Equal("role=follower fellowship=yes members=2/2 leader=Mba vassal=yes", s);
    }

    [Fact]
    public void Follower_UnknownLeaderName_ShowsQuestionMark()
    {
        var s = TeamFormation.Describe(
            new[] { "Mba" }, selfName: "Mbb", inFellowship: true, amLeader: false,
            fellowshipLeaderName: null,
            currentMemberNames: new string?[] { "Mbb" },
            visiblePlayerNames: None,
            isInAllegiance: false, isOwnMonarch: false);
        Assert.Contains("leader=? vassal=no", s);
    }

    [Fact]
    public void RosterFoldsInSelf_WhenOperatorListedOnlyOthers()
    {
        // 2-bot harness convention: AC_BOTS_TEAMMATE_NAMES is the OTHER bot only, so the
        // roster must fold in self => 2, not 1 (avoids an ugly "members=2/1").
        var s = TeamFormation.Describe(
            new[] { "Mbb" }, selfName: "Mba", inFellowship: true, amLeader: true,
            fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb" },
            visiblePlayerNames: new string?[] { "Mbb" },
            isInAllegiance: false, isOwnMonarch: false);
        Assert.Equal("role=leader fellowship=yes members=2/2 next-recruit=none", s);
    }

    [Fact]
    public void StrangerInFellowship_DoesNotInflateConfiguredProgress()
    {
        // A non-configured player grouped in must NOT count toward the operator-team
        // progress: roster {Mba,Mbb}; fellowship {Mba,Mbb,Stranger} => members=2/2, and
        // the stranger is not offered as a recruit target.
        var roster = new[] { "Mba", "Mbb" };
        var s = TeamFormation.Describe(
            roster, selfName: "Mba", inFellowship: true, amLeader: true,
            fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb", "Stranger" },
            visiblePlayerNames: new string?[] { "Mbb", "Stranger" },
            isInAllegiance: false, isOwnMonarch: false);
        Assert.Equal("role=leader fellowship=yes members=2/2 next-recruit=none", s);
    }

    [Fact]
    public void Follower_StrangerInFellowship_CountsOnlyConfigured()
    {
        // roster {Mba,Mbb}; fellowship {Mba,Mbb,Stranger} => members=2/2 (not 3/2).
        var s = TeamFormation.Describe(
            new[] { "Mba", "Mbb" }, selfName: "Mbb", inFellowship: true, amLeader: false,
            fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb", "Stranger" },
            visiblePlayerNames: None,
            isInAllegiance: false, isOwnMonarch: false);
        Assert.Equal("role=follower fellowship=yes members=2/2 leader=Mba vassal=no", s);
    }

    [Fact]
    public void Snapshot_ChangesAcrossFormationTransitions()
    {
        // The Motor logs on change, so the string MUST differ at each formation step.
        var roster = new[] { "Mba", "Mbb", "Mbc" };
        var ungrouped = TeamFormation.Describe(
            roster, "Mba", inFellowship: false, amLeader: false, fellowshipLeaderName: null,
            currentMemberNames: None,
            visiblePlayerNames: new string?[] { "Mbb", "Mbc" }, isInAllegiance: false, isOwnMonarch: false);
        var recruited1 = TeamFormation.Describe(
            roster, "Mba", inFellowship: true, amLeader: true, fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb" },
            visiblePlayerNames: new string?[] { "Mbb", "Mbc" }, isInAllegiance: false, isOwnMonarch: false);
        var recruited2 = TeamFormation.Describe(
            roster, "Mba", inFellowship: true, amLeader: true, fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb", "Mbc" },
            visiblePlayerNames: new string?[] { "Mbb", "Mbc" }, isInAllegiance: false, isOwnMonarch: false);
        Assert.NotEqual(ungrouped, recruited1);
        Assert.NotEqual(recruited1, recruited2);
        Assert.Equal("role=leader fellowship=yes members=3/3 next-recruit=none", recruited2);
    }

    [Fact]
    public void Snapshot_IsStableAcrossReorderedAndDuplicatedInputs()
    {
        // Throttle correctness depends on the string being invariant to benign per-tick
        // reordering / duplication of the visible + member sequences (else it would spam).
        var roster = new[] { "Mba", "Mbb", "Mbc" };
        var a = TeamFormation.Describe(
            roster, "Mba", inFellowship: true, amLeader: true, fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mba", "Mbb" },
            visiblePlayerNames: new string?[] { "Mbb", "Mbc" }, isInAllegiance: false, isOwnMonarch: false);
        var b = TeamFormation.Describe(
            roster, "Mba", inFellowship: true, amLeader: true, fellowshipLeaderName: "Mba",
            currentMemberNames: new string?[] { "Mbb", "Mba", "Mbb" },              // reordered + dup
            visiblePlayerNames: new string?[] { "Mbc", "Mbb", "Mbc" }, isInAllegiance: false, isOwnMonarch: false); // reordered + dup
        Assert.Equal(a, b);
    }

    [Fact]
    public void VisibleTeammateMatch_IsCaseInsensitive()
    {
        var s = TeamFormation.Describe(
            new[] { "Mbb" }, selfName: "Mba", inFellowship: false, amLeader: false,
            fellowshipLeaderName: null, currentMemberNames: None,
            visiblePlayerNames: new string?[] { "MBB" }, isInAllegiance: false, isOwnMonarch: false);
        Assert.Equal("role=ungrouped fellowship=no members=0/2 visible-teammates=1", s);
    }
}
