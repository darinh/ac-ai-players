// SPDX-License-Identifier: AGPL-3.0-or-later
// teammate-leader-election: tests for the deterministic bot-team leader election
// that stops two symmetric bots from both creating their own one-member fellowship
// (which blocks mutual recruitment). Covers:
//   1. TeammateCoordination.ParseNames (config parsing)
//   2. TeammateCoordination.Decide (role election + the critical SYMMETRY property)
//   3. the fellowship-guidance prompt DIRECTIVE branches (leader / follower / none)

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class TeammateCoordinationTests
{
    // ---- ParseNames ----

    [Fact]
    public void ParseNames_SplitsTrimsAndDedupes()
    {
        var set = TeammateCoordination.ParseNames("  Mba , Mbb ; Mba ");
        Assert.Equal(2, set.Count);
        Assert.Contains("Mba", set);
        Assert.Contains("Mbb", set);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseNames_BlankIsEmpty(string? env)
    {
        Assert.Empty(TeammateCoordination.ParseNames(env));
    }

    [Fact]
    public void ParseNames_IsCaseInsensitive()
    {
        var set = TeammateCoordination.ParseNames("Alcott;alcott");
        Assert.Single(set);
        Assert.Contains("ALCOTT", set);   // membership is ordinal-ignore-case
    }

    // ---- Decide: None ----

    [Fact]
    public void Decide_NoConfig_IsNone()
    {
        var r = TeammateCoordination.Decide("Mba", Array.Empty<string>(), new[] { "Mbb" });
        Assert.Equal(TeammateRole.None, r.Role);
        Assert.Null(r.CounterpartName);
    }

    [Fact]
    public void Decide_ConfiguredButNoneVisible_IsNone()
    {
        var r = TeammateCoordination.Decide("Mba", new[] { "Mbb" }, new[] { "SomeStranger" });
        Assert.Equal(TeammateRole.None, r.Role);
    }

    [Fact]
    public void Decide_BlankOwnName_IsNone()
    {
        var r = TeammateCoordination.Decide("  ", new[] { "Mbb" }, new[] { "Mbb" });
        Assert.Equal(TeammateRole.None, r.Role);
    }

    [Fact]
    public void Decide_IgnoresSameNamedVisiblePlayer()
    {
        // A visible player whose name equals the bot's own name must not make the bot
        // its own teammate.
        var r = TeammateCoordination.Decide("Mba", new[] { "Mba" }, new[] { "Mba" });
        Assert.Equal(TeammateRole.None, r.Role);
    }

    // ---- Decide: Leader / Follower ----

    [Fact]
    public void Decide_OwnNameSortsFirst_IsLeader()
    {
        var r = TeammateCoordination.Decide("Mba", new[] { "Mbb" }, new[] { "Mbb" });
        Assert.Equal(TeammateRole.Leader, r.Role);
        Assert.Equal("Mbb", r.CounterpartName);   // recruit the teammate
    }

    [Fact]
    public void Decide_OwnNameSortsAfter_IsFollower()
    {
        var r = TeammateCoordination.Decide("Mbb", new[] { "Mba" }, new[] { "Mba" });
        Assert.Equal(TeammateRole.Follower, r.Role);
        Assert.Equal("Mba", r.CounterpartName);   // wait for the leader
    }

    [Fact]
    public void Decide_IsSymmetric_ExactlyOneLeader()
    {
        // The core property: given two bots that each configure the other, exactly one
        // is Leader and the other Follower, and they agree on WHO leads.
        var names = new[] { "Mba", "Mbb" };
        var a = TeammateCoordination.Decide("Mba", new[] { "Mbb" }, new[] { "Mbb" });
        var b = TeammateCoordination.Decide("Mbb", new[] { "Mba" }, new[] { "Mba" });
        Assert.Equal(TeammateRole.Leader, a.Role);
        Assert.Equal(TeammateRole.Follower, b.Role);
        // both agree the leader is "Mba"
        Assert.Equal("Mbb", a.CounterpartName);   // A (leader) recruits B
        Assert.Equal("Mba", b.CounterpartName);   // B (follower) waits for A
    }

    [Fact]
    public void Decide_ThreeBots_LowestNameLeadsAllOthersFollow()
    {
        // With three configured co-present bots, only the alphabetically-first is Leader.
        var all = new[] { "botA", "botB", "botC" };
        var team = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);
        var leaders = all.Count(self =>
            TeammateCoordination.Decide(
                self, team.Where(n => n != self).ToArray(), all.Where(n => n != self)).Role
            == TeammateRole.Leader);
        Assert.Equal(1, leaders);   // exactly one leader across the trio
    }

    [Fact]
    public void Decide_MatchIsCaseInsensitive()
    {
        var r = TeammateCoordination.Decide("Mba", new[] { "MBB" }, new[] { "mbb" });
        Assert.Equal(TeammateRole.Leader, r.Role);
        Assert.Equal("mbb", r.CounterpartName);   // the visible name is echoed back
    }

    // ---- prompt DIRECTIVE branches (via the TeammateNames seam) ----

    private static WorldStateProjection ProjectionWith(string ownName, params string[] visiblePlayerNames)
    {
        var visible = visiblePlayerNames
            .Select((n, i) => new VisibleObjectProjection
            {
                Guid = 0x50000100u + (uint)i, Name = n, IsPlayer = true, Distance = 5f,
            })
            .ToArray();
        return new WorldStateProjection
        {
            Self = new SelfProjection { Guid = 0x5000000Bu, Name = ownName, HealthFraction = 1.0f },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = visible,
        };
    }

    private static string RenderWithTeammates(WorldStateProjection world, params string[] teammateNames)
    {
        var set = new HashSet<string>(teammateNames, StringComparer.OrdinalIgnoreCase);
        return LlmGoalPolicy.BuildUserPromptForTest(world, new EventStream(), set);
    }

    // Extract a single "## Header" section from the rendered prompt (up to the next
    // "## " header), so assertions can scope to the fellowship guidance rather than the
    // whole prompt (the JSON schema also mentions FellowshipRecruit).
    private static string Section(string prompt, string header)
    {
        int start = prompt.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int next = prompt.IndexOf("\n## ", start + header.Length, StringComparison.Ordinal);
        return next < 0 ? prompt.Substring(start) : prompt.Substring(start, next - start);
    }

    [Fact]
    public void Prompt_LeaderDirective_WhenOwnNameSortsFirst()
    {
        var prompt = RenderWithTeammates(ProjectionWith("Mba", "Mbb"), "Mbb");
        Assert.Contains("designated leader", prompt);
        Assert.Contains("FellowshipCreate", prompt);
        Assert.Contains("FellowshipRecruit", prompt);
        Assert.Contains("Mbb", prompt);
    }

    [Fact]
    public void Prompt_FollowerDirective_WhenOwnNameSortsAfter()
    {
        var prompt = RenderWithTeammates(ProjectionWith("Mbb", "Mba"), "Mba");
        Assert.Contains("designated leader (their name sorts first)", prompt);
        Assert.Contains("Do NOT `FellowshipCreate`", prompt);
        Assert.Contains("accept the pending invite", prompt);
    }

    [Fact]
    public void Prompt_OptionalCue_WhenNoTeammateConfigured()
    {
        // Same visible player, but no teammate config -> the OPTIONAL cue, not a directive.
        var prompt = RenderWithTeammates(ProjectionWith("Mba", "Mbb") /* no teammates */);
        Assert.Contains("OPTIONAL", prompt);
        Assert.DoesNotContain("designated leader", prompt);
    }

    // ---- CompareNames (total ordering) ----

    [Fact]
    public void CompareNames_CaseOnlyVariants_AreOrderedDeterministically()
    {
        // Case-insensitive first, ordinal tie-break: "Mba" vs "mba" must NOT tie (0),
        // and the order must be antisymmetric so both bots agree.
        var ab = TeammateCoordination.CompareNames("Mba", "mba");
        var ba = TeammateCoordination.CompareNames("mba", "Mba");
        Assert.NotEqual(0, ab);
        Assert.Equal(-Math.Sign(ab), Math.Sign(ba));
    }

    [Fact]
    public void CompareNames_DiffersByLetter_UsesCaseInsensitiveOrder()
    {
        // Casing does not decide when the names differ by a real letter: "mba" < "MBB".
        Assert.True(TeammateCoordination.CompareNames("mba", "MBB") < 0);
        Assert.True(TeammateCoordination.CompareNames("MBB", "mba") > 0);
    }

    // ---- FirstUnrecruitedTeammate (N-bot recruit progression) ----

    [Fact]
    public void FirstUnrecruited_ReturnsFirstNotYetMember()
    {
        var r = TeammateCoordination.FirstUnrecruitedTeammate(
            new[] { "Mbb", "Mbc" },
            visiblePlayerNames: new[] { "Mbb", "Mbc" },
            currentMemberNames: new[] { "Mba", "Mbb" });   // Mbb already joined
        Assert.Equal("Mbc", r);
    }

    [Fact]
    public void FirstUnrecruited_NullWhenAllMembersJoined()
    {
        var r = TeammateCoordination.FirstUnrecruitedTeammate(
            new[] { "Mbb" },
            visiblePlayerNames: new[] { "Mbb" },
            currentMemberNames: new[] { "Mba", "Mbb" });
        Assert.Null(r);
    }

    [Fact]
    public void FirstUnrecruited_NullWhenNoConfigOrNoneVisible()
    {
        Assert.Null(TeammateCoordination.FirstUnrecruitedTeammate(
            Array.Empty<string>(), new[] { "Mbb" }, Array.Empty<string?>()));
        Assert.Null(TeammateCoordination.FirstUnrecruitedTeammate(
            new[] { "Mbb" }, new[] { "Stranger" }, Array.Empty<string?>()));
    }

    [Fact]
    public void FirstUnrecruited_PicksLowestNameOrder()
    {
        var r = TeammateCoordination.FirstUnrecruitedTeammate(
            new[] { "Mbc", "Mbb" },
            visiblePlayerNames: new[] { "Mbc", "Mbb" },
            currentMemberNames: new[] { "Mba" });
        Assert.Equal("Mbb", r);   // lowest by NameOrder among unrecruited
    }

    // ---- 3-bot in-fellowship leader recruit render ----

    private static WorldStateProjection LeaderInFellowshipSeeing(
        string ownName, string[] memberNames, params string[] visibleOthers)
    {
        var members = memberNames
            .Select(n => new FellowshipMemberProjection
            {
                Name = n, Level = 5, IsSelf = string.Equals(n, ownName, StringComparison.OrdinalIgnoreCase),
                IsLeader = string.Equals(n, ownName, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();
        var visible = visibleOthers
            .Select((n, i) => new VisibleObjectProjection
            {
                Guid = 0x50000200u + (uint)i, Name = n, IsPlayer = true, Distance = 6f,
            })
            .ToArray();
        return new WorldStateProjection
        {
            Self = new SelfProjection { Guid = 0x5000000Bu, Name = ownName, HealthFraction = 1.0f },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = visible,
            Fellowship = new FellowshipProjection
            {
                Name = "Team", AmLeader = true, LeaderName = ownName,
                MemberCount = members.Count, Members = members,
                ShareXp = true, EvenShare = true, Open = false, Locked = false,
            },
        };
    }

    [Fact]
    public void Prompt_LeaderRecruitsNextTeammate_AfterFirstJoined()
    {
        // Leader (Mba) grouped with Mbb; Mbc is visible but not yet a member -> the
        // directive must name Mbc, so a 3-bot team does not stall after the first join.
        var world = LeaderInFellowshipSeeing("Mba", new[] { "Mba", "Mbb" }, "Mbc");
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(new[] { "Mbb", "Mbc" }, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("designated leader", prompt);
        Assert.Contains("Mbc", prompt);            // recruit the NEXT teammate
    }

    [Fact]
    public void Prompt_LeaderStopsRecruiting_WhenAllTeammatesJoined()
    {
        // All configured teammates present are members -> generic grouped cue, no
        // directed recruit (no spam).
        var world = LeaderInFellowshipSeeing("Mba", new[] { "Mba", "Mbb" } /* no other visible */);
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(new[] { "Mbb" }, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("NOT yet in the fellowship", prompt);
    }

    [Fact]
    public void Prompt_GroupedNonLeader_IsNotToldToRecruit()
    {
        // A grouped NON-leader member must not be told to manage membership; recruiting
        // is the leader's job. Bot Mbb is a member of Mba's fellowship (AmLeader=false).
        var members = new[]
        {
            new FellowshipMemberProjection { Name = "Mba", Level = 5, IsSelf = false, IsLeader = true },
            new FellowshipMemberProjection { Name = "Mbb", Level = 5, IsSelf = true, IsLeader = false },
        };
        var world = new WorldStateProjection
        {
            Self = new SelfProjection { Guid = 0x5000000Cu, Name = "Mbb", HealthFraction = 1.0f },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection { Guid = 0x50000201u, Name = "Mba", IsPlayer = true, Distance = 6f },
            },
            Fellowship = new FellowshipProjection
            {
                Name = "Team", AmLeader = false, LeaderName = "Mba",
                MemberCount = 2, Members = members,
                ShareXp = true, EvenShare = true, Open = false, Locked = false,
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase));
        var guidance = Section(prompt, "## Fellowship guidance");
        Assert.Contains("NOT the leader", guidance);
        Assert.DoesNotContain("FellowshipRecruit", guidance);
    }

    // ---- IsConfiguredTeammate ----

    [Fact]
    public void IsConfiguredTeammate_MatchesCaseInsensitively()
    {
        var team = new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase);
        Assert.True(TeammateCoordination.IsConfiguredTeammate("Mba", team));
        Assert.True(TeammateCoordination.IsConfiguredTeammate("mba", team));
        Assert.False(TeammateCoordination.IsConfiguredTeammate("Stranger", team));
    }

    [Fact]
    public void IsConfiguredTeammate_FalseForBlankOrEmptyConfig()
    {
        Assert.False(TeammateCoordination.IsConfiguredTeammate(null, new[] { "Mba" }));
        Assert.False(TeammateCoordination.IsConfiguredTeammate("  ", new[] { "Mba" }));
        Assert.False(TeammateCoordination.IsConfiguredTeammate("Mba", Array.Empty<string>()));
    }

    [Fact]
    public void IsConfiguredTeammate_TrimsPaddedServerText()
    {
        // Defensive against a padded/decorated invite text: the match trims first.
        var team = new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase);
        Assert.True(TeammateCoordination.IsConfiguredTeammate("  Mba ", team));
    }

    // ---- ## Fellowship invite: directive vs optional ----

    private static WorldStateProjection ProjectionWithPendingInvite(string ownName, string inviteText) =>
        new()
        {
            Self = new SelfProjection { Guid = 0x5000000Du, Name = ownName, HealthFraction = 1.0f },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = Array.Empty<VisibleObjectProjection>(),
            PendingFellowshipInvite = new PendingFellowshipInviteProjection { Text = inviteText },
        };

    [Fact]
    public void Prompt_InviteFromConfiguredTeammate_IsDirective()
    {
        // Invite text is the inviter's name; when it matches a configured teammate the
        // accept cue is a DIRECTIVE, symmetric to the leader's recruit directive.
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            ProjectionWithPendingInvite("Mbb", "Mba"), new EventStream(),
            new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase));
        var invite = Section(prompt, "## Fellowship invite");
        Assert.Contains("INVITED you", invite);
        Assert.Contains("DIRECTED team coordination", invite);
        Assert.Contains("FellowshipAccept", invite);
        // Not the optional-branch wording (the directive may still say "before an
        // OPTIONAL hunt/loot", so match the optional cue's signature phrase).
        Assert.DoesNotContain("you decide whether to accept", invite);
    }

    [Fact]
    public void Prompt_InviteFromNonTeammate_StaysOptional()
    {
        // An invite from a player NOT in the teammate config keeps the OPTIONAL wording.
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            ProjectionWithPendingInvite("Mbb", "RandomPlayer"), new EventStream(),
            new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase));
        var invite = Section(prompt, "## Fellowship invite");
        Assert.Contains("OPTIONAL", invite);
        Assert.DoesNotContain("DIRECTED team coordination", invite);
    }

    [Fact]
    public void Prompt_InviteFromTeammate_MixedCase_IsDirective()
    {
        // Prompt-level case-insensitivity: configured "Mba" + invite text "mba" (the
        // server sends the raw inviter name; casing must not drop the directive).
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            ProjectionWithPendingInvite("Zzz", "mba"), new EventStream(),
            new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase));
        var invite = Section(prompt, "## Fellowship invite");
        Assert.Contains("INVITED you", invite);
        Assert.Contains("DIRECTED team coordination", invite);
    }

    // ---- ## Allegiance guidance: vassal-swear directive ----

    private static WorldStateProjection AllegianceProjection(
        string ownName, uint? monarchGuid, params string[] visiblePlayerNames)
    {
        var visible = visiblePlayerNames
            .Select((n, i) => new VisibleObjectProjection
            {
                Guid = 0x50000180u + (uint)i, Name = n, IsPlayer = true, Distance = 6f,
            })
            .ToArray();
        return new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = 0x5000000Bu, Name = ownName, HealthFraction = 1.0f, MonarchGuid = monarchGuid,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = visible,
        };
    }

    [Fact]
    public void Prompt_Follower_NotYetVassal_IsDirectedToSwear()
    {
        // Own is its own monarch (MonarchGuid == own guid) => not yet anyone's vassal.
        var world = AllegianceProjection("Mbb", monarchGuid: 0x5000000Bu, "Mba");
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance guidance");
        Assert.Contains("SwearAllegiance", sec);
        Assert.Contains("your team's leader", sec);
        Assert.Contains("Mba", sec);
        Assert.Contains("DIRECTED team coordination", sec);
    }

    [Fact]
    public void Prompt_Leader_IsNotDirectedToSwear()
    {
        // The leader is the monarch, not a vassal -> no swear directive for it.
        var world = AllegianceProjection("Mba", monarchGuid: 0x5000000Bu, "Mbb");
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(new[] { "Mbb" }, StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance guidance");
        Assert.DoesNotContain("your team's leader", sec);
    }

    [Fact]
    public void Prompt_Follower_AlreadyVassal_IsNotDirectedToSwear()
    {
        // MonarchGuid is a DIFFERENT guid (already someone's vassal) -> no re-swear.
        var world = AllegianceProjection("Mbb", monarchGuid: 0x50000199u, "Mba");
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance guidance");
        Assert.DoesNotContain("your team's leader", sec);
    }

    [Fact]
    public void Prompt_Follower_SwearsToFellowshipLeader_WhenMonarchNotVisible()
    {
        // Drifted-apart-after-grouping: no teammate is VISIBLE, but the bot is in a
        // fellowship whose leader is its configured teammate -> the swear directive
        // still renders (monarch resolved via fellowship membership, GoTo first).
        var members = new[]
        {
            new FellowshipMemberProjection { Name = "Mba", Level = 5, IsSelf = false, IsLeader = true },
            new FellowshipMemberProjection { Name = "Mbb", Level = 5, IsSelf = true, IsLeader = false },
        };
        var world = new WorldStateProjection
        {
            Self = new SelfProjection { Guid = 0x5000000Fu, Name = "Mbb", HealthFraction = 1.0f, MonarchGuid = 0x5000000Fu },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = Array.Empty<VisibleObjectProjection>(),   // monarch NOT visible
            Fellowship = new FellowshipProjection
            {
                Name = "Team", AmLeader = false, LeaderName = "Mba",
                MemberCount = 2, Members = members,
                ShareXp = true, EvenShare = true, Open = false, Locked = false,
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance guidance");
        Assert.Contains("your team's leader", sec);
        Assert.Contains("Mba", sec);
        Assert.Contains("SwearAllegiance", sec);
        Assert.Contains("GoTo", sec);   // directed to approach the out-of-view monarch
        // With no visible player the generic visible-target affordances must stay closed
        // even though the fellowship-leader path forces the section open.
        Assert.DoesNotContain("A `player` is in view", sec);
        Assert.DoesNotContain("BreakAllegiance", sec);
    }

    [Fact]
    public void Prompt_VisibleElectedMonarch_TakesPrecedenceOverFellowshipLeader()
    {
        // When a configured teammate is VISIBLE, the elected monarch (that visible
        // teammate) wins over a DIFFERENT fellowship leader — the directive targets the
        // visible-elected monarch, not the fellowship leader.
        var members = new[]
        {
            new FellowshipMemberProjection { Name = "Mbz", Level = 5, IsSelf = false, IsLeader = true },
            new FellowshipMemberProjection { Name = "Mbc", Level = 5, IsSelf = true, IsLeader = false },
        };
        var world = new WorldStateProjection
        {
            Self = new SelfProjection { Guid = 0x5000000Fu, Name = "Mbc", HealthFraction = 1.0f, MonarchGuid = 0x5000000Fu },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection { Guid = 0x50000201u, Name = "Mba", IsPlayer = true, Distance = 6f },
            },
            Fellowship = new FellowshipProjection
            {
                Name = "Team", AmLeader = false, LeaderName = "Mbz",
                MemberCount = 2, Members = members,
                ShareXp = true, EvenShare = true, Open = false, Locked = false,
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(),
            new HashSet<string>(new[] { "Mba", "Mbz", "Mbc" }, StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance guidance");
        Assert.Contains("Mba", sec);         // visible elected teammate wins
        Assert.DoesNotContain("Mbz", sec);   // not the (different) fellowship leader
        Assert.Contains("SwearAllegiance", sec);
    }

    [Fact]
    public void Prompt_FellowshipLeader_DoesNotSwearToSelf()
    {
        // The fellowship LEADER (AmLeader) is the monarch, not a vassal -> no swear cue
        // even though it is fellowshipped with a configured teammate.
        var members = new[]
        {
            new FellowshipMemberProjection { Name = "Mba", Level = 5, IsSelf = true, IsLeader = true },
            new FellowshipMemberProjection { Name = "Mbb", Level = 5, IsSelf = false, IsLeader = false },
        };
        var world = new WorldStateProjection
        {
            Self = new SelfProjection { Guid = 0x5000000Fu, Name = "Mba", HealthFraction = 1.0f, MonarchGuid = 0x5000000Fu },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = Array.Empty<VisibleObjectProjection>(),
            Fellowship = new FellowshipProjection
            {
                Name = "Team", AmLeader = true, LeaderName = "Mba",
                MemberCount = 2, Members = members,
                ShareXp = true, EvenShare = true, Open = false, Locked = false,
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(new[] { "Mbb" }, StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance guidance");
        Assert.DoesNotContain("your team's leader", sec);
    }

    [Fact]
    public void Prompt_Follower_Unaffiliated_IsDirectedToSwear()
    {
        // Truly unaffiliated (MonarchGuid null) -> IsInAllegiance false -> still directed.
        var world = AllegianceProjection("Mbb", monarchGuid: null, "Mba");
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(new[] { "Mba" }, StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance guidance");
        Assert.Contains("your team's leader", sec);
        Assert.Contains("SwearAllegiance", sec);
    }

    [Fact]
    public void Prompt_AlreadyVassal_GenericSwearCueSuppressed()
    {
        // Once a vassal (monarch = another guid), the generic "swear to anyone" cue is
        // suppressed (an existing vassal must BreakAllegiance first).
        var world = AllegianceProjection("Solo", monarchGuid: 0x50000199u, "SomePlayer");
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            world, new EventStream(), new HashSet<string>(Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance guidance");
        Assert.DoesNotContain("You MAY `SwearAllegiance`", sec);
        Assert.Contains("BreakAllegiance", sec);   // but break is still offered
    }
}
