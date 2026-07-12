// SPDX-License-Identifier: AGPL-3.0-or-later
// Stateful-tracker tests for LlmGoalPolicy.UpdateGoalProgressTracking, driven via
// the test-only TrackGoalProgressForTest hook (controlled clock, no async/network).
// These pin the remembered->visible SOURCE SWITCH invariant added with the
// remembered-approach-stuck slice: when an object-pursuit target that was tracked
// from the bot's out-of-view sighting memory becomes VISIBLE, the trend buffer is
// cleared so a 2D remembered-distance sample can never sit next to an engine
// visible-distance sample in one trend (the single-source invariant). Placeholder
// names only. reduce-llm-call-volume.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class RememberedApproachTrackerTests
{
    // The LLM client + weenie repo are constructor requirements only;
    // TrackGoalProgressForTest never touches the network or the repo.
    private sealed class NullHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private sealed class NullWeenieRepo : IWeenieRepository
    {
        public WeenieStringRecord? TryGet(uint wcid) => null;
        public Task EnsureLoadedAsync(uint wcid, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static LlmGoalPolicy NewPolicy() =>
        new(new LlmGoalClient(new HttpClient(new NullHandler()), "https://test.example/chat", "test-model", "key"),
            new NoQuestKnowledgePolicy(), new NullWeenieRepo());

    private static WorldStateProjection World(IReadOnlyList<VisibleObjectProjection> visible) => new()
    {
        Self = new SelfProjection
        {
            Guid = 0x50000009, Name = "Headless", Landblock = 0xA9B4u, CellId = 0xA9B40001u,
            PositionX = 0, PositionY = 0, PositionZ = 0, Level = 18, HealthFraction = 1.0f,
        },
        Visible = visible,
        Inventory = Array.Empty<InventoryItemProjection>(),
    };

    private static SightedRecallProjection Sighting(string name = "Quarry Beast") => new()
    {
        Name = name, Wcid = null, Kind = EntityKind.Mob, Landblock = 0xA9B4u,
        WorldX = 500f, WorldY = 500f, AgeSeconds = 5.0,
    };

    private static VisibleObjectProjection VisibleMob(string name, float distance, uint guid = 0x80001234u) =>
        new() { Guid = guid, Name = name, Distance = distance, IsCreature = true };

    private static Goal Attack(string name = "Quarry Beast") =>
        new() { Kind = GoalKind.Attack, Target = new Selector { Name = name } };

    private static readonly VisibleObjectProjection[] NoneVisible = Array.Empty<VisibleObjectProjection>();

    [Fact]
    public void RememberedOnly_BuildsRememberedTrend()
    {
        var p = NewPolicy();
        var goal = Attack();
        var sights = new List<SightedRecallProjection> { Sighting() };
        var t = DateTimeOffset.UtcNow;

        GoalProgressSnapshot? snap = null;
        for (int i = 0; i < 3; i++) // 2s spacing clears the >=1.5s throttle
            snap = p.TrackGoalProgressForTest(World(NoneVisible), goal, t.AddSeconds(i * 2), sights);

        Assert.NotNull(snap);
        Assert.Contains("(remembered)", snap!.TargetLabel);
        Assert.Equal(3, snap.Distances.Count); // one sample per tick
    }

    [Fact]
    public void RememberedThenVisible_ClearsBuffer_NoMixedTrend()
    {
        var p = NewPolicy();
        var goal = Attack();
        var sights = new List<SightedRecallProjection> { Sighting() };
        var t = DateTimeOffset.UtcNow;

        // Remembered phase: 3 ticks accumulate remembered (2D) samples.
        for (int i = 0; i < 3; i++)
            p.TrackGoalProgressForTest(World(NoneVisible), goal, t.AddSeconds(i * 2), sights);

        // Transition: the target becomes visible. The first visible tick clears the
        // remembered buffer (source switch) and starts a fresh visible trend.
        p.TrackGoalProgressForTest(World(new[] { VisibleMob("Quarry Beast", 10f) }), goal, t.AddSeconds(6), sights);
        var snap = p.TrackGoalProgressForTest(World(new[] { VisibleMob("Quarry Beast", 8f) }), goal, t.AddSeconds(8), sights);

        Assert.NotNull(snap);
        Assert.Equal("Quarry Beast (guid=0x80001234)", snap!.TargetLabel); // switched to the visible source label
        Assert.Equal(new[] { 10f, 8f }, snap.Distances);          // ONLY visible samples; remembered ones were cleared
    }

    [Fact]
    public void VisibleFromStart_NeverRemembered()
    {
        var p = NewPolicy();
        var goal = Attack();
        var sights = new List<SightedRecallProjection> { Sighting() }; // present but unused (target visible)
        var t = DateTimeOffset.UtcNow;

        GoalProgressSnapshot? snap = null;
        for (int i = 0; i < 3; i++)
            snap = p.TrackGoalProgressForTest(World(new[] { VisibleMob("Quarry Beast", 12f - i) }), goal, t.AddSeconds(i * 2), sights);

        Assert.NotNull(snap);
        Assert.DoesNotContain("(remembered)", snap!.TargetLabel);
        Assert.Contains("guid=", snap.TargetLabel);
        Assert.Equal(new[] { 12f, 11f, 10f }, snap.Distances);
    }

    [Fact]
    public void RememberedThenTargetGone_KeepsPriorTrend_NoNewSamples()
    {
        var p = NewPolicy();
        var goal = Attack();
        var sights = new List<SightedRecallProjection> { Sighting() };
        var t = DateTimeOffset.UtcNow;

        GoalProgressSnapshot? before = null;
        for (int i = 0; i < 3; i++)
            before = p.TrackGoalProgressForTest(World(NoneVisible), goal, t.AddSeconds(i * 2), sights);

        // Sightings drop out (target no longer remembered and not visible): the
        // tracker keeps the prior trend but adds nothing new.
        var snap = p.TrackGoalProgressForTest(World(NoneVisible), goal, t.AddSeconds(6), recentSightings: null);
        Assert.NotNull(before);
        Assert.NotNull(snap);
        Assert.Equal(before!.Distances, snap!.Distances); // list unchanged: no new sample added
        Assert.Contains("(remembered)", snap.TargetLabel);
    }

    [Fact]
    public void SourceSwitch_FirstVisibleSampleLands_DespiteThrottle()
    {
        // The remembered->visible switch clears the buffer (Count -> 0), so the first
        // visible sample lands even when it arrives INSIDE the >=1.5s sample-throttle
        // window (here 0.5s after the last remembered sample). Proves the buffer clear
        // — not merely the 2s cadence used elsewhere — is what admits the first visible
        // sample on the source switch.
        var p = NewPolicy();
        var goal = Attack();
        var sights = new List<SightedRecallProjection> { Sighting() };
        var t = DateTimeOffset.UtcNow;

        for (int i = 0; i < 3; i++)
            p.TrackGoalProgressForTest(World(NoneVisible), goal, t.AddSeconds(i * 2), sights);

        // First visible tick only 0.5s after the last remembered tick (t+4 -> t+4.5).
        p.TrackGoalProgressForTest(World(new[] { VisibleMob("Quarry Beast", 10f) }), goal, t.AddSeconds(4.5), sights);
        var snap = p.TrackGoalProgressForTest(World(new[] { VisibleMob("Quarry Beast", 8f) }), goal, t.AddSeconds(6.5), sights);

        Assert.NotNull(snap);
        Assert.Equal(new[] { 10f, 8f }, snap!.Distances); // 10 landed despite the 0.5s gap
    }
}
