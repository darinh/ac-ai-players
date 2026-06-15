// SPDX-License-Identifier: AGPL-3.0-or-later
// The "DONE (stage 3, complete)" contract note counts hand-in Talk goals via
// CountRecentTalkGoalsToName. That count read the perception/motion-dominated
// 256-event ring, so re-talks to a turn-in NPC spread across minutes were
// evicted before the count reached the hand-in threshold and the note silently
// never rendered for a genuinely re-talked NPC. The fix reads a DEDICATED
// durable goal-emission window that outlives the ring. These tests lock that
// goal history survives heavy perception traffic and that the count finds
// re-talks across it.

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
        // the durable-window read finds BOTH, so the hand-in threshold is met.
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
}
