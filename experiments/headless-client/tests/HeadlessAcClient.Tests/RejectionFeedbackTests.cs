// SPDX-License-Identifier: AGPL-3.0-or-later
// Issue #79 — server-rejection feedback to the LLM.
//
// Verifies the end-to-end path that turns inbound WeenieError /
// WeenieErrorWithString into a structured "do not repeat" hint
// in the LLM's next prompt:
//
//   1) EventStream.Append(GoalRejected) round-trips the new
//      ErrorCode / RejectedGoalKind / ItemName fields.
//   2) WorldStateProjection.FromWorldState surfaces those events
//      as RejectionProjection entries in the projection.
//   3) LlmGoalPolicy.MatchesRecentRejection identifies a parsed
//      Goal as a repeat of a recent rejection on (kind, target,
//      item) match.
//
// The full HandshakeDriver attribution path (which is the source
// of the GoalRejected events in production) is covered by the
// live spike verification rather than a unit test — too much I/O.

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class RejectionFeedbackTests
{
    // ---- EventStream ----

    [Fact]
    public void EventStream_GoalRejected_RoundTripsAllFields()
    {
        var es = new EventStream();
        var stamped = es.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalRejected,
            Text = "InventoryDoesntBelongToAccount: You can't give this to that NPC",
            Name = "Society Greeter",
            ItemGuid = 0x80000123u,
            ItemName = "Calling Stone",
            ErrorCode = 0x046A,
            RejectedGoalKind = "Give",
        });

        Assert.Equal(EventKind.GoalRejected, stamped.Kind);
        Assert.Equal((uint?)0x046A, stamped.ErrorCode);
        Assert.Equal("Give", stamped.RejectedGoalKind);
        Assert.Equal("Calling Stone", stamped.ItemName);
        Assert.Equal((uint?)0x80000123u, stamped.ItemGuid);

        var recent = es.RecentOfKind(EventKind.GoalRejected, 8);
        Assert.Single(recent);
        Assert.Equal(stamped, recent[0]);
    }

    [Fact]
    public void EventStream_GoalRejected_ToString_IncludesKey()
    {
        var es = new EventStream();
        var ev = es.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalRejected,
            Text = "no",
            Name = "Greeter",
            ItemName = "Stone",
            ErrorCode = 0x046A,
            RejectedGoalKind = "Give",
        });
        var s = ev.ToString();
        Assert.Contains("GoalRejected", s);
        Assert.Contains("Give", s);
        Assert.Contains("Greeter", s);
        Assert.Contains("Stone", s);
        Assert.Contains("0x046A", s);
    }

    // ---- WorldStateProjection.FromWorldState ----

    [Fact]
    public void Projection_FromWorldState_SurfacesRecentRejections()
    {
        const uint SelfGuid = 0x50000001u;
        var world = new WorldState();
        world.SetSelf(SelfGuid);
        SnapshotSeeding.Seed(world, SelfGuid, "TestBot", wcid: 1u, itemType: 0u,
            cellId: 0x86020001u, containerGuid: null);

        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalRejected,
            Text = "rej1", Name = "NpcA", ItemName = "ItemA",
            ErrorCode = 0x046A, RejectedGoalKind = "Give",
        });
        es.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalRejected,
            Text = "rej2", Name = "NpcB",
            ErrorCode = 0x0042, RejectedGoalKind = "Use",
        });

        var proj = WorldStateProjection.FromWorldState(world, weenies: null, events: es);

        Assert.NotNull(proj);
        Assert.Equal(2, proj!.RecentRejections.Count);
        // Newest first (RecentOfKind contract).
        Assert.Equal("Use", proj.RecentRejections[0].Kind);
        Assert.Equal("NpcB", proj.RecentRejections[0].TargetName);
        Assert.Equal((uint?)0x0042, proj.RecentRejections[0].ErrorCode);
        Assert.Equal("Give", proj.RecentRejections[1].Kind);
        Assert.Equal("ItemA", proj.RecentRejections[1].ItemName);
    }

    [Fact]
    public void Projection_FromWorldState_NoEvents_EmptyRejections()
    {
        const uint SelfGuid = 0x50000002u;
        var world = new WorldState();
        world.SetSelf(SelfGuid);
        SnapshotSeeding.Seed(world, SelfGuid, "Bot", wcid: 1u, itemType: 0u,
            cellId: 0x86020001u, containerGuid: null);

        var proj = WorldStateProjection.FromWorldState(world, weenies: null);
        Assert.NotNull(proj);
        Assert.Empty(proj!.RecentRejections);
    }

    // ---- LlmGoalPolicy.MatchesRecentRejection ----

    private static RejectionProjection MakeRej(string kind, string target, string? item = null) =>
        new RejectionProjection
        {
            Kind = kind,
            TargetName = target,
            ItemName = item,
            ErrorCode = 0x046A,
            ErrorText = "test",
            Sequence = 1,
        };

    [Fact]
    public void Match_GiveSameTargetAndItem_True()
    {
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            Kind = GoalKind.Give,
            Target = new Selector { Name = "Society Greeter" },
            Item = new Selector { Name = "Calling Stone" },
            Rationale = "x",
        };
        var rejs = new List<RejectionProjection> { MakeRej("Give", "Society Greeter", "Calling Stone") };
        Assert.True(LlmGoalPolicy.MatchesRecentRejection(goal, rejs));
    }

    [Fact]
    public void Match_GiveDifferentItem_False()
    {
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            Kind = GoalKind.Give,
            Target = new Selector { Name = "Society Greeter" },
            Item = new Selector { Name = "Different Item" },
            Rationale = "x",
        };
        var rejs = new List<RejectionProjection> { MakeRej("Give", "Society Greeter", "Calling Stone") };
        Assert.False(LlmGoalPolicy.MatchesRecentRejection(goal, rejs));
    }

    [Fact]
    public void Match_UseAgainstSameInventoryItem_True()
    {
        // Use against an inventory item: target.Name == item name, no Item field.
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Academy Exit Token" },
            Rationale = "x",
        };
        var rejs = new List<RejectionProjection> { MakeRej("Use", "Academy Exit Token") };
        Assert.True(LlmGoalPolicy.MatchesRecentRejection(goal, rejs));
    }

    [Fact]
    public void Match_CaseInsensitive()
    {
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            Kind = GoalKind.Use,
            Target = new Selector { Name = "academy exit token" }, // lowercase
            Rationale = "x",
        };
        var rejs = new List<RejectionProjection> { MakeRej("Use", "Academy Exit Token") };
        Assert.True(LlmGoalPolicy.MatchesRecentRejection(goal, rejs));
    }

    [Fact]
    public void Match_DifferentKind_False()
    {
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            Kind = GoalKind.Talk,
            Target = new Selector { Name = "Society Greeter" },
            Rationale = "x",
        };
        var rejs = new List<RejectionProjection> { MakeRej("Use", "Society Greeter") };
        Assert.False(LlmGoalPolicy.MatchesRecentRejection(goal, rejs));
    }

    [Fact]
    public void Match_NoTargetName_False()
    {
        // Selector with only short-desc / wcid / etc — not a "named"
        // goal, so a name-based rejection cannot match it.
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            Kind = GoalKind.Use,
            Target = new Selector { ShortDescContains = "exit" },
            Rationale = "x",
        };
        var rejs = new List<RejectionProjection> { MakeRej("Use", "Academy Exit Token") };
        Assert.False(LlmGoalPolicy.MatchesRecentRejection(goal, rejs));
    }

    [Fact]
    public void Match_EmptyRejections_AlwaysFalse()
    {
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Anything" },
            Rationale = "x",
        };
        Assert.False(LlmGoalPolicy.MatchesRecentRejection(goal, Array.Empty<RejectionProjection>()));
    }

    [Fact]
    public void Match_GiveWithMissingItemInRejection_DoesNotFalseMatch()
    {
        // Defensive: if a Give rejection somehow lacks the item name
        // (legacy data or a server bug), we must NOT consider a new
        // Give to the same NPC as a repeat — the LLM should be free
        // to try a different item.
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            Kind = GoalKind.Give,
            Target = new Selector { Name = "NPC" },
            Item = new Selector { Name = "ItemA" },
            Rationale = "x",
        };
        var rejs = new List<RejectionProjection> { MakeRej("Give", "NPC", item: null) };
        Assert.False(LlmGoalPolicy.MatchesRecentRejection(goal, rejs));
    }
}
