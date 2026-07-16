// SPDX-License-Identifier: AGPL-3.0-or-later
// Contract prompt evidence counts Talk and Explore goals via the durable
// goal-emission window rather than the perception/motion-dominated event ring.
// These tests lock that raw history survives heavy perception traffic.

using System;
using System.Linq;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class DurableGoalEmissionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static StreamEvent TalkGoal(string npc, DateTimeOffset utc) => new()
    {
        Sequence = 0,
        Utc = utc,
        Kind = EventKind.GoalEmitted,
        Text = $"Talk target=name=\"{npc}\" item= source=llm",
    };

    // A high-volume non-goal event (perception/health/etc.) used to flood the
    // ring past its capacity so older goals are evicted from it.
    private static StreamEvent Noise(DateTimeOffset utc) => new()
    {
        Sequence = 0,
        Utc = utc,
        Kind = EventKind.HealthChanged,
    };

    [Fact]
    public void RecentGoalEmissions_SurviveRingEvictionByPerceptionTraffic()
    {
        var es = new EventStream(); // 256-event ring
        es.Append(TalkGoal("Npc", T0));
        // Flood well past the ring capacity so the first goal is evicted from it.
        for (int i = 0; i < 400; i++) es.Append(Noise(T0.AddSeconds(1)));
        es.Append(TalkGoal("Npc", T0.AddSeconds(2)));

        // The first goal is GONE from the perception-dominated ring...
        Assert.Single(es.Recent(EventStream.DefaultCapacity),
            e => e.Kind == EventKind.GoalEmitted);
        // ...but BOTH survive in the dedicated durable window.
        Assert.Equal(2, es.RecentGoalEmissions().Count);
    }

    [Fact]
    public void CountRecentTalkGoalsToName_CountsRetalksAcrossPerceptionEviction()
    {
        var es = new EventStream();
        var since = T0; // the contract reached stage 3 at T0
        es.Append(TalkGoal("Npc", T0.AddSeconds(1)));
        for (int i = 0; i < 400; i++) es.Append(Noise(T0.AddSeconds(2)));
        es.Append(TalkGoal("Npc", T0.AddSeconds(3)));

        // Ring-based counting would now see only ONE Talk (the other evicted);
        // the durable-window read finds BOTH for the prompt's raw history.
        Assert.Equal(2, LlmGoalPolicy.CountRecentTalkGoalsToName(es, "Npc", since));
    }

    [Fact]
    public void CountRecentTalkGoalsToName_ExcludesTalksBeforeSince()
    {
        var es = new EventStream();
        es.Append(TalkGoal("Npc", T0));                 // before `since`
        var since = T0.AddSeconds(10);
        es.Append(TalkGoal("Npc", T0.AddSeconds(11)));  // after `since`
        Assert.Equal(1, LlmGoalPolicy.CountRecentTalkGoalsToName(es, "Npc", since));
    }

    [Fact]
    public void CountRecentTalkGoalsToName_OnlyMatchesTheNamedNpc()
    {
        var es = new EventStream();
        es.Append(TalkGoal("Npc", T0.AddSeconds(1)));
        es.Append(TalkGoal("Other", T0.AddSeconds(2)));
        es.Append(TalkGoal("Npc", T0.AddSeconds(3)));
        Assert.Equal(2, LlmGoalPolicy.CountRecentTalkGoalsToName(es, "Npc", T0));
        Assert.Equal(1, LlmGoalPolicy.CountRecentTalkGoalsToName(es, "Other", T0));
    }

    [Fact]
    public void CountRecentTalkGoalsToName_SurvivesManyInterveningGoals_WithinRetention()
    {
        // The recurrence guard: a small fixed goal-count cap would trim the
        // early hand-in talk once enough UNRELATED goals follow (re-introducing
        // the missed-note bug, just delayed). Time-based retention keeps the
        // early talk because it is well within the retention window.
        var es = new EventStream();
        var since = T0;
        es.Append(TalkGoal("Npc", T0.AddSeconds(1)));
        for (int i = 0; i < 200; i++)
            es.Append(TalkGoal("Other", T0.AddSeconds(2 + i * 0.1)));
        es.Append(TalkGoal("Npc", T0.AddMinutes(1)));
        Assert.Equal(2, LlmGoalPolicy.CountRecentTalkGoalsToName(es, "Npc", since));
    }

    private static StreamEvent ExploreGoal(string target, DateTimeOffset utc) => new()
    {
        Sequence = 0,
        Utc = utc,
        Kind = EventKind.GoalEmitted,
        Text = $"Explore target=name=\"{target}\" item= source=llm",
    };

    [Fact]
    public void CountRecentExploreGoalsToName_CountsExploresToName_Since_OnlyTheNamed()
    {
        // cp030 diagnostic: a stage-3 "locate" contract is pursued via Explore, so
        // the Explore-pursuit count is the analogue of the Talk hand-in count. It
        // counts only Explore goals (not Talk), only to the named target, only
        // at/after `since`, and survives perception eviction via the durable window.
        var es = new EventStream();
        var since = T0.AddSeconds(10);
        es.Append(ExploreGoal("Npc", T0));                 // before `since` -> excluded
        es.Append(TalkGoal("Npc", T0.AddSeconds(11)));     // a Talk -> not an Explore
        es.Append(ExploreGoal("Npc", T0.AddSeconds(12)));
        for (int i = 0; i < 400; i++) es.Append(Noise(T0.AddSeconds(13)));
        es.Append(ExploreGoal("Other", T0.AddSeconds(14)));
        es.Append(ExploreGoal("Npc", T0.AddSeconds(15)));
        Assert.Equal(2, LlmGoalPolicy.CountRecentExploreGoalsToName(es, "Npc", since));
        Assert.Equal(1, LlmGoalPolicy.CountRecentExploreGoalsToName(es, "Other", since));
    }

    [Fact]
    public void CountRecentExploreGoalsToName_DoesNotCountTalkGoals()
    {
        // The Explore-pursuit count must be DISTINCT from the Talk hand-in count:
        // a stream of only Talk goals to the named NPC yields zero Explores.
        var es = new EventStream();
        es.Append(TalkGoal("Npc", T0.AddSeconds(1)));
        es.Append(TalkGoal("Npc", T0.AddSeconds(2)));
        Assert.Equal(0, LlmGoalPolicy.CountRecentExploreGoalsToName(es, "Npc", T0));
        Assert.Equal(2, LlmGoalPolicy.CountRecentTalkGoalsToName(es, "Npc", T0));
    }

    [Fact]
    public void RecentGoalEmissions_EvictsGoalsOlderThanRetention()
    {
        // A goal older than the retention window is evicted when a newer goal
        // arrives, so a stale attempt from far in the past cannot linger.
        var es = new EventStream();
        es.Append(TalkGoal("Npc", T0));
        es.Append(TalkGoal("Npc", T0.AddMinutes(45))); // 45 min later
        Assert.Single(es.RecentGoalEmissions());
    }

    [Fact]
    public void HasRecentRepeatedGoalOfKinds_DetectsRepeatAcrossPerceptionEviction()
    {
        // The loop-break guard reads the same durable window: two Talk goals to
        // the same target separated by heavy perception traffic must still be
        // detected as a repeat (the ring would have evicted the first, hiding
        // the loop).
        var es = new EventStream();
        es.Append(TalkGoal("Npc", T0));
        for (int i = 0; i < 400; i++) es.Append(Noise(T0.AddSeconds(1)));
        es.Append(TalkGoal("Npc", T0.AddSeconds(2)));
        Assert.True(LlmGoalPolicy.HasRecentRepeatedGoalOfKinds(es, "Talk"));
    }

    // --- Role-title-suffix normalization (the prompt renders objects as
    // `Name "role"`, and a model frequently copies that whole label into the
    // target selector; the Motor RESOLVES such a target by stripping the suffix,
    // but the prompt-history/refresh counters read the bot's OWN
    // emission text and previously matched only an EXACT bare name, so a
    // role-suffixed emission silently counted 0 and the guards keyed on it never
    // fired). These pin that the counters now match a role-suffixed emission
    // against the bare name, consistent with the resolver. ---

    [Fact]
    public void CountRecentExploreGoalsToName_CountsRoleSuffixedTargetAsBareName()
    {
        // A role-suffixed target must count against the bare contract NPC name.
        var es = new EventStream();
        es.Append(ExploreGoal("Buckminster \"Bartender Greeter\"", T0.AddSeconds(1)));
        es.Append(ExploreGoal("Buckminster \"Bartender Greeter\"", T0.AddSeconds(2)));
        es.Append(ExploreGoal("Buckminster \"Bartender Greeter\"", T0.AddSeconds(3)));
        Assert.Equal(3, LlmGoalPolicy.CountRecentExploreGoalsToName(es, "Buckminster", T0));
    }

    [Fact]
    public void CountRecentTalkGoalsToName_CountsRoleSuffixedTargetAsBareName()
    {
        var es = new EventStream();
        es.Append(TalkGoal("Wilomine \"Barkeeper\"", T0.AddSeconds(1)));
        es.Append(TalkGoal("Wilomine", T0.AddSeconds(2)));            // bare also counts
        Assert.Equal(2, LlmGoalPolicy.CountRecentTalkGoalsToName(es, "Wilomine", T0));
    }

    [Fact]
    public void CountRecentExploreGoalsToName_BareNameStillMatchesExactly_NoRegression()
    {
        // A plain (no role-title) emission keeps matching exactly.
        var es = new EventStream();
        es.Append(ExploreGoal("Npc", T0.AddSeconds(1)));
        es.Append(ExploreGoal("Npc", T0.AddSeconds(2)));
        Assert.Equal(2, LlmGoalPolicy.CountRecentExploreGoalsToName(es, "Npc", T0));
        Assert.Equal(0, LlmGoalPolicy.CountRecentExploreGoalsToName(es, "Other", T0));
    }

    [Fact]
    public void CountRecentEngageGoalsToName_CountsRoleSuffixedTalkAndUseAsBareName()
    {
        var es = new EventStream();
        es.Append(TalkGoal("Renald \"Shopkeeper\"", T0.AddSeconds(1)));
        es.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = T0.AddSeconds(2),
            Kind = EventKind.GoalEmitted,
            Text = "Use target=name=\"Renald \"Shopkeeper\"\" item= source=llm",
        });
        Assert.Equal(2, LlmGoalPolicy.CountRecentEngageGoalsToName(es, "Renald", T0));
    }

    [Theory]
    [InlineData("Buckminster \"Bartender Greeter\"", "Buckminster")] // full rendered label
    [InlineData("Buckminster ", "Buckminster")]                        // regex-truncated capture
    [InlineData("Buckminster", "Buckminster")]                         // already bare
    [InlineData("Foo\"Bar\"", "Foo\"Bar\"")]                           // no space before quote -> preserved
    [InlineData("  ", "")]                                              // whitespace-only -> empty
    public void NormalizeEmittedTargetName_StripsRenderedRoleTitle(string raw, string expected)
    {
        Assert.Equal(expected, LlmGoalPolicy.NormalizeEmittedTargetName(raw));
    }

    [Fact]
    public void NormalizeEmittedTargetName_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LlmGoalPolicy.NormalizeEmittedTargetName(null));
    }
}
