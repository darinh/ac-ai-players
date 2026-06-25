// SPDX-License-Identifier: AGPL-3.0-or-later
// Strategy + Tactics Slice A foundation tests.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Tactics;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class StrategyFoundationTests
{
    // ---- Goal / Selector ----

    [Fact]
    public void Selector_IsEmpty_RequiresAtLeastOneField()
    {
        Assert.True(new Selector().IsEmpty);
        Assert.False(new Selector { Name = "Jonathan" }.IsEmpty);
        Assert.False(new Selector { Wcid = 29335 }.IsEmpty);
        Assert.False(new Selector { ItemTypeMask = ItemTypeMasks.Creature }.IsEmpty);
        Assert.False(new Selector { ShortDescContains = "exit" }.IsEmpty);
        Assert.False(new Selector { NameContains = "jonat" }.IsEmpty);
        Assert.False(new Selector { Guid = 0xABCDu }.IsEmpty);
    }

    [Fact]
    public void Goal_SerializesAndRoundTripsThroughJson()
    {
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            Kind = GoalKind.Give,
            Target = new Selector { Name = "Jonathan" },
            Item = new Selector { ShortDescContains = "exit" },
            Priority = 7,
            ExpiresInSeconds = 30,
            Rationale = "exit token text says give to Jonathan",
            Source = "llm:gpt-4o-mini",
        };

        var json = JsonSerializer.Serialize(goal);
        var rt = JsonSerializer.Deserialize<Goal>(json);

        Assert.NotNull(rt);
        Assert.Equal(goal.Id, rt!.Id);
        Assert.Equal(GoalKind.Give, rt.Kind);
        Assert.Equal("Jonathan", rt.Target.Name);
        Assert.Equal("exit", rt.Item?.ShortDescContains);
        Assert.Equal(7, rt.Priority);
        Assert.Equal(30, rt.ExpiresInSeconds);
        Assert.Equal("llm:gpt-4o-mini", rt.Source);
    }

    // ---- EventStream ----

    [Fact]
    public void EventStream_AppendAssignsMonotonicSequence()
    {
        var es = new EventStream(8);
        var a = es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "a" });
        var b = es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "b" });
        var c = es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "c" });

        Assert.Equal(0, a.Sequence);
        Assert.Equal(1, b.Sequence);
        Assert.Equal(2, c.Sequence);
        Assert.Equal(3, es.NextSequence);
    }

    [Fact]
    public void EventStream_EvictsOldestWhenCapacityExceeded()
    {
        var es = new EventStream(8);
        for (int i = 0; i < 20; i++)
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = $"e{i}" });
        Assert.Equal(8, es.Count);

        var recent = es.Recent(8);
        Assert.Equal("e19", recent[0].Text);
        Assert.Equal("e12", recent[7].Text);
    }

    [Fact]
    public void EventStream_RecentOfKindFilters()
    {
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString,         Text = "p1" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ServerMessage,       Text = "s1" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString,         Text = "p2" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.InventoryItemAdded,  ItemGuid = 0x100u, Wcid = 29335 });

        var popups = es.RecentOfKind(EventKind.PopupString, 5);
        Assert.Equal(2, popups.Count);
        Assert.Equal("p2", popups[0].Text);
        Assert.Equal("p1", popups[1].Text);

        var inv = es.RecentOfKind(EventKind.InventoryItemAdded, 5);
        Assert.Single(inv);
        Assert.Equal(29335u, inv[0].Wcid);
    }

    [Fact]
    public void EventStream_PersistentPopups_SurviveRingEviction()
    {
        // A one-time login/exit directive arrives early but the bot may not be
        // ready to act on it until far later, by which point it has aged out of
        // the bounded event ring. The persistent distinct-popup store must keep
        // it even though Recent() no longer has it.
        var es = new EventStream(8);
        var login = es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PopupString, Text = "Go talk to Jonathan to leave.",
        });
        for (int i = 0; i < 20; i++)
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ServerMessage, Text = $"chatter {i}" });

        Assert.DoesNotContain(es.Recent(EventStream.DefaultCapacity), e => e.Text == "Go talk to Jonathan to leave.");

        var persisted = es.PersistentPopupStrings();
        Assert.Contains(persisted, e => e.Text == "Go talk to Jonathan to leave.");
        Assert.Equal(login.Sequence, persisted[0].Sequence);
    }

    [Fact]
    public void EventStream_PersistentPopups_DistinctFirstSeenAndCapped()
    {
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "dup" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "dup" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ServerMessage, Text = "not a popup" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "" });
        for (int i = 0; i < 40; i++)
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = $"p{i}" });

        var persisted = es.PersistentPopupStrings();
        Assert.Equal("dup", persisted[0].Text);
        Assert.Equal(1, persisted.Count(e => e.Text == "dup"));
        Assert.DoesNotContain(persisted, e => e.Text == "not a popup");
        Assert.DoesNotContain(persisted, e => string.IsNullOrEmpty(e.Text));
        Assert.True(persisted.Count <= 24, $"persistent popups {persisted.Count} exceeds cap");
        // Cap is reached before all 40 distinct 'pN' popups are captured, so the
        // earliest anchor 'dup' stays locked in and a late popup does NOT displace it.
        Assert.DoesNotContain(persisted, e => e.Text == "p39");
    }

    [Fact]
    public void EventStream_PersistentNpcDialogs_SurviveRingEviction_FirstSeenCapped()
    {
        var es = new EventStream(8);
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Guide", Text = "Go to the next room." });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Guide", Text = "Go to the next room." }); // dup
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "not an npc line" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Guide", Text = "" }); // empty ignored
        for (int i = 0; i < 20; i++)
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = $"NPC{i}", Text = $"line {i:D2}" });

        var persisted = es.PersistentNpcDialogs();
        // Survives ring eviction (ring holds 8; the first line is long gone from Recent).
        Assert.DoesNotContain(es.Recent(EventStream.DefaultCapacity), e => e.Text == "Go to the next room.");
        Assert.Equal("Go to the next room.", persisted[0].Text);
        Assert.Equal("Guide", persisted[0].Name);
        Assert.Equal(1, persisted.Count(e => e.Text == "Go to the next room."));
        Assert.DoesNotContain(persisted, e => e.Text == "not an npc line");
        Assert.DoesNotContain(persisted, e => string.IsNullOrEmpty(e.Text));
        Assert.True(persisted.Count <= 12, $"persistent npc dialogs {persisted.Count} exceeds cap");
        Assert.DoesNotContain(persisted, e => e.Text == "line 19"); // beyond the earliest-N cap
    }

    [Fact]
    public void EventStream_RecentDirectives_KeepLatestPastEarliestCap()
    {
        // cp-2393: a LATE directive (e.g. "you have completed your training, take
        // the portal") arrives after the earliest store is already full, so it is
        // never added there — yet it is the CURRENT instruction. The recent
        // sliding window must keep it even though the earliest store does not.
        var es = new EventStream(8);
        // Fill the earliest NpcDialog cap (12) with distinct early lines.
        for (int i = 0; i < 12; i++)
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Greeter", Text = $"early line {i:D2}" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Master", Text = "You have finished. Take the portal." });

        // Earliest store is full and locked — the late line is NOT there.
        Assert.DoesNotContain(es.PersistentNpcDialogs(), e => e.Text == "You have finished. Take the portal.");
        // The recent window DOES carry the current instruction.
        Assert.Contains(es.RecentPersistentNpcDialogs(), e => e.Text == "You have finished. Take the portal.");
        Assert.Equal("Master", es.RecentPersistentNpcDialogs().Last(e => e.Text == "You have finished. Take the portal.").Name);
    }

    [Fact]
    public void EventStream_RecentNpcDialogs_CompletionDirectiveSurvivesTipFlood()
    {
        // Pins the sizing rationale: a one-time progression directive must stay in
        // the recent window through the trailing tutorial-tip dialogs a training
        // area emits during a grind (live: 6 distinct tip lines). With the 8-slot
        // recent window it survives 7 distinct later tips and is only pushed out by
        // the 8th — comfortably above the observed 6.
        var es = new EventStream(8);
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Master", Text = "You have finished. Take the portal." });
        bool Present() => es.RecentPersistentNpcDialogs().Any(e => e.Text == "You have finished. Take the portal.");

        for (int i = 0; i < 6; i++) // the observed tip-flood volume
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Golem", Text = $"combat tip {i:D2}" });
        Assert.True(Present(), "directive evicted by the observed 6-tip flood");

        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Golem", Text = "combat tip 06" }); // 7th distinct trailing tip
        Assert.True(Present(), "directive should still survive a 7th distinct trailing tip");

        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Golem", Text = "combat tip 07" }); // 8th distinct trailing tip
        Assert.False(Present(), "an 8th distinct trailing tip fills the window and evicts the directive");
    }

    [Fact]
    public void EventStream_RecentDirectives_MostRecentDistinctAndCapped()
    {
        // The recent window keeps the most-recent distinct directives, capped,
        // newest last; a repeat moves to newest rather than duplicating.
        var es = new EventStream();
        for (int i = 0; i < 10; i++)
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = $"hint {i:D2}" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "hint 09" }); // repeat of latest

        var recent = es.RecentPersistentPopupStrings();
        Assert.True(recent.Count <= 4, $"recent popups {recent.Count} exceeds cap");
        Assert.Equal("hint 09", recent[^1].Text);           // newest last
        Assert.Equal(1, recent.Count(e => e.Text == "hint 09")); // repeat not duplicated
        Assert.DoesNotContain(recent, e => e.Text == "hint 00"); // oldest evicted
    }

    // ---- SelectorResolver ----

    private const uint SelfGuid = 0x50000005;
    private const uint NpcGuid  = 0x90000010;
    private const uint MobGuid  = 0x90000020;
    private const uint ItemGuid = 0x80000030;

    private static WorldState BuildTinyWorld()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);

        // Inject snapshots directly via Apply isn't easy here, so we use reflection-free
        // approach: we hit GetOrCreate via the public TryGet (returns null) and populate
        // through the Apply path that's most convenient — but the simplest is to use
        // the test-only seam via TryGet-after-Apply. Instead, write straight to the
        // internal Objects dictionary by going through a synthesized ObjectCreate
        // wouldn't be worth it for a unit test. WorldState exposes Objects as readonly,
        // but TryGet returns null if not present. We use SelectorResolver against a
        // local handcrafted state by constructing snapshots and stuffing them via
        // an internal helper isn't available — so populate via the public Apply.
        //
        // Trick: WorldState has GetOrCreate accessible via TryGet+Apply chain. The
        // easiest path for tests is to seed via the existing GetOrCreateMutable
        // pattern used in other tests.
        return ws;
    }

    [Fact]
    public void SelectorResolver_EmptySelectorReturnsNothing()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        var result = SelectorResolver.Resolve(new Selector(), ws);
        Assert.Empty(result);
    }

    [Fact]
    public void SelectorResolver_NameMatch_FromSeededState()
    {
        var ws = BuildSeededWorld();
        var npc = SelectorResolver.Resolve(new Selector { Name = "Jonathan" }, ws);
        Assert.Single(npc);
        Assert.Equal(NpcGuid, npc[0].Guid);
    }

    [Fact]
    public void SelectorResolver_NameContains_CaseInsensitive()
    {
        var ws = BuildSeededWorld();
        var npc = SelectorResolver.Resolve(new Selector { NameContains = "jonat" }, ws);
        Assert.Single(npc);
        Assert.Equal(NpcGuid, npc[0].Guid);
    }

    [Fact]
    public void SelectorResolver_NameWithQuotedRoleSuffix_MatchesBareName()
    {
        // The prompt renders objects as `<Name> "<role>"`; the model often copies
        // the whole label into a target selector. That selector must still resolve
        // to the bare-named object (the role/title is not part of the wire name).
        var ws = BuildSeededWorld();
        var npc = SelectorResolver.Resolve(
            new Selector { Name = "Jonathan \"Lifestone Greeter\"" }, ws);
        Assert.Single(npc);
        Assert.Equal(NpcGuid, npc[0].Guid);
    }

    [Fact]
    public void SelectorResolver_NameWithQuotedRoleSuffix_WrongBaseName_StillMisses()
    {
        // Stripping the role must not over-match: a wrong base name still misses.
        var ws = BuildSeededWorld();
        var none = SelectorResolver.Resolve(
            new Selector { Name = "Someone Else \"Lifestone Greeter\"" }, ws);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData("Jonathan \"Lifestone Greeter\"", "Jonathan")]
    [InlineData("Contract Broker \"Armorer\"", "Contract Broker")]
    [InlineData("Captain  \"Town Guard\"  ", "Captain")]
    [InlineData("Foo \"\"", "Foo")]
    public void StripTrailingQuotedRoleTitle_StripsTrailingQuotedSegment(string input, string expected)
    {
        Assert.Equal(expected, SelectorResolver.StripTrailingQuotedRoleTitle(input));
    }

    [Theory]
    [InlineData("Jonathan")]      // no quoted suffix
    [InlineData("")]              // empty
    [InlineData("   ")]           // whitespace
    [InlineData("\"Orphan")]      // unbalanced — opening quote only
    [InlineData("\"role-only\"")] // no base name before the role
    [InlineData("Foo\"Bar\"")]    // no whitespace before the quote — real name, not a role label
    public void StripTrailingQuotedRoleTitle_ReturnsNull_WhenNoStrippableSuffix(string input)
    {
        Assert.Null(SelectorResolver.StripTrailingQuotedRoleTitle(input));
    }

    [Fact]
    public void SelectorResolver_PartialName_TitleLadenWireName_ResolvesUniquely()
    {
        // The model names an NPC by the distinctive part of an occupation/title-
        // laden wire name. With a single matching object, the fuzzy fallback
        // resolves it.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, 0x90001001u, "Barkeeper Wilomine", wcid: 710u, itemType: 0x10u, cellId: 0x86020001u);
        var npc = SelectorResolver.Resolve(new Selector { Name = "Wilomine" }, ws);
        Assert.Single(npc);
        Assert.Equal(0x90001001u, npc[0].Guid);
    }

    [Fact]
    public void SelectorResolver_PartialName_LeadingDistinctiveWord_Resolves()
    {
        // The distinctive word can lead the wire name (personal name + descriptor),
        // with punctuation between words; tokenization is whole-word so a comma
        // does not block the match.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, 0x90001002u, "Rand, Game Hunter", wcid: 711u, itemType: 0x10u, cellId: 0x86020001u);
        var npc = SelectorResolver.Resolve(new Selector { Name = "Rand" }, ws);
        Assert.Single(npc);
        Assert.Equal(0x90001002u, npc[0].Guid);
    }

    [Fact]
    public void SelectorResolver_PartialName_Ambiguous_ResolvesToNothing()
    {
        // Two different objects share the distinctive word — the partial is
        // ambiguous, so it must stay UNRESOLVED (empty) rather than snap to one.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, 0x90001003u, "Barkeeper Wilomine", wcid: 710u, itemType: 0x10u, cellId: 0x86020001u);
        SeedSnapshot(ws, 0x90001004u, "Apprentice Wilomine", wcid: 712u, itemType: 0x10u, cellId: 0x86020001u);
        var npc = SelectorResolver.Resolve(new Selector { Name = "Wilomine" }, ws);
        Assert.Empty(npc);
    }

    [Fact]
    public void SelectorResolver_PartialName_ExactMatchStillWinsOverFuzzy()
    {
        // When an EXACT name match exists, the fuzzy fallback never runs — even if
        // another object would also fuzzy-match the partial.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, 0x90001005u, "Wilomine", wcid: 710u, itemType: 0x10u, cellId: 0x86020001u);
        SeedSnapshot(ws, 0x90001006u, "Barkeeper Wilomine", wcid: 713u, itemType: 0x10u, cellId: 0x86020001u);
        var npc = SelectorResolver.Resolve(new Selector { Name = "Wilomine" }, ws);
        Assert.Single(npc);
        Assert.Equal(0x90001005u, npc[0].Guid);
    }

    [Theory]
    [InlineData("Barkeeper Wilomine", "Wilomine", true)]   // trailing distinctive word
    [InlineData("Sean the Speedy", "Sean", true)]          // leading distinctive word
    [InlineData("Rand, Game Hunter", "Rand", true)]        // punctuation tokenized away
    [InlineData("Rand, Game Hunter", "Game Hunter", true)] // contiguous multi-word subsequence
    [InlineData("Contract Broker", "broker", true)]        // case-insensitive
    [InlineData("Barkeeper Wilomine", "keeper", false)]    // whole-word, not substring
    [InlineData("Drudge Slinker", "Skulker", false)]       // absent word
    [InlineData("Drudge Slinker", "Slinker Drudge", false)]// out-of-order is not contiguous
    [InlineData("Wilomine", "Barkeeper Wilomine", false)]  // selector longer than object
    [InlineData("Barkeeper Wilomine", "", false)]          // empty selector
    public void MatchesNameWordSubsequence_WholeWordContiguous(string obj, string sel, bool expected)
    {
        Assert.Equal(expected, SelectorResolver.MatchesNameWordSubsequence(obj, sel));
    }

    [Fact]
    public void SelectorResolver_Wcid_ExactMatch()
    {
        var ws = BuildSeededWorld();
        var token = SelectorResolver.Resolve(new Selector { Wcid = 29335u }, ws);
        Assert.Single(token);
        Assert.Equal(ItemGuid, token[0].Guid);
    }

    [Fact]
    public void SelectorResolver_ItemTypeMask_BitmaskMatches()
    {
        var ws = BuildSeededWorld();
        // Creature bit = 0x10 — should match the NPC and the mob.
        var creatures = SelectorResolver.Resolve(
            new Selector { ItemTypeMask = ItemTypeMasks.Creature }, ws);
        Assert.Equal(2, creatures.Count);
    }

    [Fact]
    public void SelectorResolver_ShortDescContains_RequiresRepository()
    {
        var ws = BuildSeededWorld();
        var sel = new Selector { ShortDescContains = "leave the Training Academy" };

        var withoutRepo = SelectorResolver.Resolve(sel, ws);
        Assert.Empty(withoutRepo);

        var repo = new FakeWeenieRepo();
        repo.Seed(29335, "Academy Exit Token", "Give this token to Jonathan if you wish to leave the Training Academy early.");
        var withRepo = SelectorResolver.Resolve(sel, ws, repo);
        Assert.Single(withRepo);
        Assert.Equal(ItemGuid, withRepo[0].Guid);
    }

    [Fact]
    public void WorldStateProjection_FromWorldState_PopulatesUseDescWhenShortDescAbsent()
    {
        // Some quest items carry their actionable instruction ONLY in the
        // PropertyString.Use (type 15) field, with an empty ShortDesc. The
        // projection must surface UseDesc so the LLM can derive the goal from
        // the item's own text instead of seeing a bare item name.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);
        const uint TokenWcid = 4242u;
        SeedSnapshot(ws, ItemGuid, "Brass Token", wcid: TokenWcid, itemType: 0x800u, cellId: 0u, containerGuid: SelfGuid);

        var repo = new FakeWeenieRepo();
        repo.Seed(TokenWcid, "Brass Token", shortDesc: null, longDesc: null, useDesc: "Return this item to the instructor to proceed.");

        var proj = WorldStateProjection.FromWorldState(ws, repo);
        Assert.NotNull(proj);
        var item = proj!.Inventory.Single(i => i.Guid == ItemGuid);
        Assert.Equal("Return this item to the instructor to proceed.", item.UseDesc);
        Assert.Null(item.ShortDesc);
    }

    [Fact]
    public void WorldStateProjection_FromWorldState_PopulatesVisibleNpcTitleFromWeenieRepo()
    {
        // An NPC's role/title (weenie PropertyString.Quality, type 5) is
        // surfaced on the VISIBLE projection so the LLM can match a directive
        // that names a target by ROLE ("go talk to the Agent") to the visible
        // NPC whose title carries that role, even when its proper name differs.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);
        const uint NpcWcid = 5151u;
        SeedSnapshot(ws, NpcGuid, "Wyngrid", wcid: NpcWcid, itemType: 0x10u, cellId: 0x86020001u);

        var repo = new FakeWeenieRepo();
        repo.Seed(NpcWcid, "Wyngrid", shortDesc: null, longDesc: null, useDesc: null, title: "Exploration Society Agent");

        var proj = WorldStateProjection.FromWorldState(ws, repo);
        Assert.NotNull(proj);
        var npc = proj!.Visible.Single(v => v.Guid == NpcGuid);
        Assert.Equal("Exploration Society Agent", npc.Title);
    }

    // Regression for racefix-run-01: after Jonathan Free Ride teleported
    // the bot to Holtburg (landblock 0xA9B4), the academy Society Greeter
    // snapshot stayed in WorldState (server never sent ObjectDelete) and
    // SelectorResolver kept resolving it as the LLM goal target at
    // dist=34904u. The locality filter on the actor's landblock prevents
    // resolution to objects in foreign landblocks while keeping inventory
    // items (carried, ContainerGuid set) addressable from anywhere.
    [Fact]
    public void SelectorResolver_FiltersOutObjectsInForeignLandblock()
    {
        const uint AcademyCellId  = 0x860201ADu; // landblock 0x8602
        const uint HoltburgCellId = 0xA9B400ABu; // landblock 0xA9B4

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        // Self has moved to Holtburg.
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: HoltburgCellId);
        // Stale academy NPC snapshot still in WorldState.
        SeedSnapshot(ws, NpcGuid, "Society Greeter", wcid: 30991u, itemType: 0x10u, cellId: AcademyCellId);
        // Inventory item: ContainerGuid=self, CellId=0 (carried).
        SeedSnapshot(ws, ItemGuid, "Calling Stone", wcid: 29336u, itemType: 0x800u, cellId: 0u, containerGuid: SelfGuid);

        var self = ws.TryGet(SelfGuid);
        Assert.NotNull(self);

        // With actor=null (legacy contract): NPC still matches (backward compat).
        var noFilter = SelectorResolver.Resolve(new Selector { Name = "Society Greeter" }, ws);
        Assert.Single(noFilter);

        // With actor=self in Holtburg: the academy NPC is dropped.
        var filtered = SelectorResolver.Resolve(
            new Selector { Name = "Society Greeter" }, ws, weenies: null, actor: self);
        Assert.Empty(filtered);

        // ResolveSingleNearest wires referencePoint as the locality actor.
        var nearest = SelectorResolver.ResolveSingleNearest(
            new Selector { Name = "Society Greeter" }, ws, referencePoint: self);
        Assert.Null(nearest);

        // Carried inventory items resolve regardless of landblock.
        var carried = SelectorResolver.Resolve(
            new Selector { Name = "Calling Stone" }, ws, weenies: null, actor: self);
        Assert.Single(carried);
        Assert.Equal(ItemGuid, carried[0].Guid);
    }

    [Fact]
    public void SelectorResolver_AcceptsSameLandblockObjectsWithDifferentCells()
    {
        // Two cells inside the same landblock (top 16 bits = 0x8602) must
        // both resolve when the actor is in that landblock — locality is
        // landblock-scoped, not cell-scoped.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);
        SeedSnapshot(ws, NpcGuid, "Jonathan", wcid: 29324u, itemType: 0x10u, cellId: 0x860201FFu);
        SeedSnapshot(ws, MobGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x860202ABu);

        var self = ws.TryGet(SelfGuid);
        var npc = SelectorResolver.Resolve(
            new Selector { Name = "Jonathan" }, ws, weenies: null, actor: self);
        Assert.Single(npc);
        var mob = SelectorResolver.Resolve(
            new Selector { Name = "Sparring Golem" }, ws, weenies: null, actor: self);
        Assert.Single(mob);
    }

    [Fact]
    public void SelectorResolver_ResolvesObjectInAdjacentLandblock_AcrossSeam()
    {
        // loot-resolver-adjacent-seam: a mob killed at a landblock boundary
        // leaves a corpse one landblock over from where the bot ends up after
        // crossing the seam. The corpse is physically ~1u away but in an
        // ADJACENT landblock. The locality filter must resolve it so an
        // explicit LLM Use{Corpse} goal can loot the bot's own kill across the
        // seam (previously rejected -> MISS -> never looted under Hunt).
        const uint ActorCellId  = 0xAAB50003u; // landblock 0xAAB5 (X=0xAA, Y=0xB5)
        const uint CorpseCellId = 0xA9B5003Bu; // landblock 0xA9B5 (X=0xA9) — west neighbour

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: ActorCellId);
        SeedSnapshot(ws, MobGuid, "Corpse of Chicken", wcid: 21u, itemType: 0x200u, cellId: CorpseCellId);

        var self = ws.TryGet(SelfGuid);
        Assert.NotNull(self);

        var resolved = SelectorResolver.Resolve(
            new Selector { Name = "Corpse of Chicken" }, ws, weenies: null, actor: self);
        Assert.Single(resolved);
        Assert.Equal(MobGuid, resolved[0].Guid);

        var nearest = SelectorResolver.ResolveSingleNearest(
            new Selector { Name = "Corpse of Chicken" }, ws, referencePoint: self);
        Assert.NotNull(nearest);
        Assert.Equal(MobGuid, nearest!.Guid);
    }

    [Theory]
    [InlineData(0xA9B50003u)] // X-1, Y same  (west)
    [InlineData(0xABB50003u)] // X+1, Y same  (east)
    [InlineData(0xAAB40003u)] // X same, Y-1  (south)
    [InlineData(0xAAB60003u)] // X same, Y+1  (north)
    [InlineData(0xA9B40003u)] // X-1, Y-1     (diagonal)
    public void SelectorResolver_ResolvesObjectInEachAdjacentLandblock(uint corpseCellId)
    {
        // Guard the signed landblock-delta on every seam direction (the
        // Chebyshev <= 1 check must hold for negative AND positive X/Y deltas).
        const uint ActorCellId = 0xAAB50003u; // landblock 0xAAB5
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: ActorCellId);
        SeedSnapshot(ws, MobGuid, "Corpse of Chicken", wcid: 21u, itemType: 0x200u, cellId: corpseCellId);

        var self = ws.TryGet(SelfGuid);
        var resolved = SelectorResolver.Resolve(
            new Selector { Name = "Corpse of Chicken" }, ws, weenies: null, actor: self);
        Assert.Single(resolved);
    }

    [Fact]
    public void SelectorResolver_RejectsObjectTwoLandblocksAway()
    {
        // The adjacency tolerance is exactly one landblock. A world object two
        // landblocks away (Chebyshev distance 2) is still rejected, preserving
        // the stale-snapshot guard the locality filter exists for.
        const uint ActorCellId  = 0xAAB50003u; // landblock 0xAAB5
        const uint FarCellId    = 0xACB50003u; // landblock 0xACB5 (X+2)

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: ActorCellId);
        SeedSnapshot(ws, NpcGuid, "Society Greeter", wcid: 30991u, itemType: 0x10u, cellId: FarCellId);

        var self = ws.TryGet(SelfGuid);
        var resolved = SelectorResolver.Resolve(
            new Selector { Name = "Society Greeter" }, ws, weenies: null, actor: self);
        Assert.Empty(resolved);
    }

    [Fact]
    public void SelectorResolver_Nearest_PrefersSeamNearOverFarSameLandblock()
    {
        // When two matching objects exist — one just across the seam in the
        // adjacent landblock and one far away in the actor's own landblock —
        // ResolveSingleNearest must return the physically nearer (seam) one.
        // worldX = landblockX * 192 + localX, so an object at localX=190 in the
        // west-neighbour landblock sits ~7u from an actor at localX=5.
        const uint ActorCellId      = 0xAAB50003u; // landblock 0xAAB5
        const uint SeamNearCellId   = 0xA9B50031u; // landblock 0xA9B5 (west neighbour)
        const uint FarSameLbCellId  = 0xAAB50099u; // same landblock 0xAAB5, far cell

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: ActorCellId,
            position: new Vector3(5f, 96f, 0f));
        // Seam-near corpse: near the shared edge of the west neighbour.
        SeedSnapshot(ws, MobGuid, "Corpse of Chicken", wcid: 21u, itemType: 0x200u, cellId: SeamNearCellId,
            position: new Vector3(190f, 96f, 0f));
        // Far corpse: same landblock as the actor but ~150u away in X.
        SeedSnapshot(ws, ItemGuid, "Corpse of Chicken", wcid: 21u, itemType: 0x200u, cellId: FarSameLbCellId,
            position: new Vector3(160f, 96f, 0f));

        var self = ws.TryGet(SelfGuid);
        var nearest = SelectorResolver.ResolveSingleNearest(
            new Selector { Name = "Corpse of Chicken" }, ws, referencePoint: self);
        Assert.NotNull(nearest);
        Assert.Equal(MobGuid, nearest!.Guid);
    }

    [Fact]
    public void SelectorResolver_ExcludeCorpses_SkipsCorpseFlaggedMatch()
    {
        // Attack resolution passes excludeCorpses:true. A corpse keeps the
        // creature's name; the NEAR match is a corpse, the FAR one is live — the
        // resolver must return the LIVE (far) one, not the corpse it stands on.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));
        SeedSnapshot(ws, MobGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(10f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable | (uint)ObjectDescriptionFlag.Corpse);
        SeedSnapshot(ws, ItemGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(50f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable);

        var self = ws.TryGet(SelfGuid);
        var sel = new Selector { Name = "Sparring Golem" };
        // Default (no exclusion) picks the NEAR corpse.
        Assert.Equal(MobGuid, SelectorResolver.ResolveSingleNearest(sel, ws, self)!.Guid);
        // excludeCorpses skips the corpse and returns the LIVE far one.
        Assert.Equal(ItemGuid,
            SelectorResolver.ResolveSingleNearest(sel, ws, self, excludeCorpses: true)!.Guid);
    }

    [Fact]
    public void SelectorResolver_ExcludeGuids_SkipsKilledGuid_PicksNextNearest()
    {
        // Attack resolution passes the recently-killed guid set. The NEAR golem
        // was just killed (lingering, not corpse-flagged); the resolver must skip
        // its guid and return the next-nearest LIVE golem instead of re-locking
        // the dead body.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));
        SeedSnapshot(ws, MobGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(6f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable);
        SeedSnapshot(ws, ItemGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(50f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable);

        var self = ws.TryGet(SelfGuid);
        var sel = new Selector { Name = "Sparring Golem" };
        // Default picks the NEAR (just-killed) golem.
        Assert.Equal(MobGuid, SelectorResolver.ResolveSingleNearest(sel, ws, self)!.Guid);
        // Suppressing the near guid returns the next-nearest LIVE golem.
        var killed = new HashSet<uint> { MobGuid };
        Assert.Equal(ItemGuid,
            SelectorResolver.ResolveSingleNearest(sel, ws, self, excludeGuids: killed)!.Guid);
        // If ALL matches are suppressed, the resolver returns null (unresolved).
        var allKilled = new HashSet<uint> { MobGuid, ItemGuid };
        Assert.Null(SelectorResolver.ResolveSingleNearest(sel, ws, self, excludeGuids: allKilled));
    }

    // ---- TacticsExecutor.ResolveTarget perception-bounded Attack ----

    private sealed class FixedGoalPolicy : IGoalPolicy
    {
        private readonly Goal _goal;
        public FixedGoalPolicy(Goal goal) => _goal = goal;
        public string Source => "test-fixed";
        public Goal? ProposeGoal(WorldStateProjection projection, EventStream events, Goal? current) => _goal;
    }

    private static TacticsExecutor TacticsWithGoal(Goal goal)
    {
        var tactics = new TacticsExecutor(new FixedGoalPolicy(goal), new FakeWeenieRepo());
        // CurrentGoal is set only via Tick(projection, ...); the stub returns the
        // fixed goal regardless of the projection passed.
        tactics.Tick(FallbackWorldWith(), new EventStream());
        return tactics;
    }

    [Fact]
    public void ResolveTarget_Attack_NextMatchBeyondPerceptionRadius_ReturnsNull()
    {
        // Live repro (cp2385-deploy.log): the LLM named Attack{"Gnawer Shreth"}
        // with a near one visible+hostile; that creature died during the ~3.5s LLM
        // latency and was excluded as recently-killed, so the resolver fell to a
        // SAME-NAME creature 220u away — OUTSIDE the projection's visibleRadius
        // (the LLM never saw it). ResolveTarget must NOT commit the Attack to a
        // target beyond perception; it returns null so the policy re-picks from
        // the CURRENT visible set.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));
        // Near golem the LLM saw + named — but just killed (in excludeGuids).
        SeedSnapshot(ws, MobGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(6f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable);
        // Far same-name golem ~145u away (> DefaultVisibleRadiusUnits=120u).
        SeedSnapshot(ws, ItemGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(150f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable);

        var self = ws.TryGet(SelfGuid);
        var tactics = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Attack,
            Target = new Selector { Name = "Sparring Golem" },
            Source = "test",
        });

        // Sanity: with NOTHING excluded the near in-radius golem resolves (the
        // gate is transparent for a perceivable target).
        Assert.Equal(MobGuid, tactics.ResolveTarget(ws, self, null)!.Guid);

        // The near golem just died: the only remaining match is 145u away,
        // beyond perception -> unresolved (re-deliberate), NOT a 145u march.
        var killed = new HashSet<uint> { MobGuid };
        Assert.Null(tactics.ResolveTarget(ws, self, killed));
    }

    [Fact]
    public void ResolveTarget_Attack_NextMatchWithinPerceptionRadius_Resolves()
    {
        // The gate rejects ONLY matches beyond the perception radius. A live
        // same-name match still within visibleRadius (here ~95u < 120u) is a
        // valid target the LLM could see, so after the near one is excluded the
        // resolver returns it.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));
        SeedSnapshot(ws, MobGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(6f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable);
        SeedSnapshot(ws, ItemGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(100f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable);

        var self = ws.TryGet(SelfGuid);
        var tactics = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Attack,
            Target = new Selector { Name = "Sparring Golem" },
            Source = "test",
        });

        var killed = new HashSet<uint> { MobGuid };
        Assert.Equal(ItemGuid, tactics.ResolveTarget(ws, self, killed)!.Guid);
    }

    [Fact]
    public void ResolveTarget_NonAttack_BeyondPerceptionRadius_StillResolves()
    {
        // The perception bound is Attack-only. A Use/Pickup target (e.g. the
        // bot's own kill corpse one landblock over) must STILL resolve beyond the
        // radius — those goals are not perception-gated and the cross-seam loot
        // path (SelectorResolver same-or-adjacent landblock) depends on it.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));
        SeedSnapshot(ws, ItemGuid, "Chest", wcid: 9000u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(150f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Openable);

        var self = ws.TryGet(SelfGuid);
        var tactics = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Chest" },
            Source = "test",
        });

        Assert.Equal(ItemGuid, tactics.ResolveTarget(ws, self, null)!.Guid);
    }

    [Fact]
    public void ResolveTarget_Use_SelfPronoun_ResolvesToSelf()
    {
        // Live repro (iter5-validate): the LLM emitted Use{target: name="self",
        // item: "Letter From Home"} (per the "Use a readable/activate item on
        // YOURSELF first" rule). No world object is named "self", so the target
        // resolved to nothing (target=MISS) and the goal looped unresolved 8x.
        // A self-PRONOUN target must resolve to the bot's own snapshot.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));
        SeedSnapshot(ws, ItemGuid, "Letter From Home", wcid: 30988u, itemType: 0x2000u, cellId: 0x86020001u,
            containerGuid: SelfGuid, position: new Vector3(5f, 96f, 0f));

        var self = ws.TryGet(SelfGuid);
        foreach (var pronoun in new[] { "self", "Self", "me", "myself", "yourself" })
        {
            var tactics = TacticsWithGoal(new Goal
            {
                Kind = GoalKind.Use,
                Target = new Selector { Name = pronoun },
                Item = new Selector { Name = "Letter From Home" },
                Source = "test",
            });
            var resolved = tactics.ResolveTarget(ws, self, null);
            Assert.NotNull(resolved);
            Assert.Equal(SelfGuid, resolved!.Guid);
        }
    }

    [Fact]
    public void ResolveTarget_Use_OwnNameOrGuid_ResolveToSelf_ViaNativeResolution()
    {
        // The bot's own NAME and own GUID need NO self short-circuit: the bot is
        // in WorldState, so SelectorResolver resolves them to self at distance 0
        // (and, unlike a blind short-circuit, still honours other selector
        // constraints — which is why the own-name/own-guid short-circuit was
        // dropped). This locks that native behaviour.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));

        var self = ws.TryGet(SelfGuid);
        var byName = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Use, Target = new Selector { Name = "Headless" }, Source = "test",
        });
        Assert.Equal(SelfGuid, byName.ResolveTarget(ws, self, null)!.Guid);
        var byGuid = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Use, Target = new Selector { Guid = SelfGuid }, Source = "test",
        });
        Assert.Equal(SelfGuid, byGuid.ResolveTarget(ws, self, null)!.Guid);
    }

    [Fact]
    public void ResolveTarget_Attack_SelfReference_DoesNotTargetSelf()
    {
        // Self-targeting is meaningful for on-self verbs (Use/Wield), never for
        // Attack — the self short-circuit must NOT apply, so Attack{self} stays
        // unresolved (no attackable object is named "self") rather than the bot
        // resolving its own guid as a combat target.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));

        var self = ws.TryGet(SelfGuid);
        var tactics = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Attack,
            Target = new Selector { Name = "self" },
            Source = "test",
        });
        Assert.Null(tactics.ResolveTarget(ws, self, null));
    }

    [Fact]
    public void ResolveTarget_Use_NonSelfTarget_StillResolvesNormally()
    {
        // Regression: a non-self Use target is unaffected by the self
        // short-circuit and resolves to the named world object as before.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));
        SeedSnapshot(ws, ItemGuid, "Forge", wcid: 9000u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(8f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Openable);

        var self = ws.TryGet(SelfGuid);
        var tactics = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Forge" },
            Source = "test",
        });
        Assert.Equal(ItemGuid, tactics.ResolveTarget(ws, self, null)!.Guid);
    }

    [Fact]
    public void ResolveTarget_Pickup_ExcludesCorpseObject_KeepsContentsAndUse()
    {
        // A corpse is a CONTAINER, not a pickable item — the server rejects
        // PUTITEMINCONTAINER on it (live: Pickup{Corpse} looped on WeenieError
        // 0x29). Pickup resolution must EXCLUDE the corpse object (resolve null
        // -> re-deliberate -> Use opens it). Its CONTENTS (not IsCorpse) still
        // resolve for Pickup, and Use/Talk/Give still resolve the corpse.
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u,
            position: new Vector3(5f, 96f, 0f));
        SeedSnapshot(ws, MobGuid, "Corpse of Drudge", wcid: 19257u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(6f, 96f, 0f),
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Corpse);
        SeedSnapshot(ws, ItemGuid, "Leather Gloves", wcid: 5000u, itemType: 0x10u, cellId: 0x86020001u,
            position: new Vector3(7f, 96f, 0f));

        var self = ws.TryGet(SelfGuid);

        // Pickup{corpse object} -> null (excluded; re-deliberate -> Use).
        var tPickCorpse = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Pickup,
            Target = new Selector { Name = "Corpse of Drudge" },
            Source = "test",
        });
        Assert.Null(tPickCorpse.ResolveTarget(ws, self, null));

        // Pickup{content item} -> resolves (NOT a corpse).
        var tPickItem = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Pickup,
            Target = new Selector { Name = "Leather Gloves" },
            Source = "test",
        });
        Assert.Equal(ItemGuid, tPickItem.ResolveTarget(ws, self, null)!.Guid);

        // Use{corpse} STILL resolves the corpse (loot-open path unaffected).
        var tUseCorpse = TacticsWithGoal(new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Corpse of Drudge" },
            Source = "test",
        });
        Assert.Equal(MobGuid, tUseCorpse.ResolveTarget(ws, self, null)!.Guid);
    }

    [Fact]
    public void WorldStateProjection_FromWorldState_DerivesSchemaBitsFromDescriptionFlags()
    {
        // De-hardcoding contract: the projection sees IsDoor / IsPortal /
        // IsCorpse / IsLifestone / IsVendor / IsHealer / IsOpenable purely
        // from ObjectDescriptionFlag bits, not from English name strings.
        // A door named "Iron Gate" must still project as IsDoor=true. A
        // creature named "Door" (yes, AC has had such weenies historically)
        // must NOT project as IsDoor=true unless the server says so.
        const uint DoorGuid     = 0x70000001;
        const uint PortalGuid   = 0x70000002;
        const uint VendorGuid   = 0x70000003;
        const uint LifestoneGuid = 0x70000004;
        const uint HealerGuid   = 0x70000005;
        const uint CorpseGuid   = 0x70000006;
        const uint MisnamedGuid = 0x70000007;

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        // Self must exist as a snapshot too (FromWorldState reads world.Self).
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);
        SeedSnapshot(ws, DoorGuid, "Iron Gate",       wcid: 100u, itemType: 0x0u,    cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Door | (uint)ObjectDescriptionFlag.Openable);
        SeedSnapshot(ws, PortalGuid, "Glowing Vortex", wcid: 101u, itemType: 0x0u,    cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Portal);
        SeedSnapshot(ws, VendorGuid, "Shopkeeper Bob", wcid: 102u, itemType: 0x10u,   cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Vendor);
        SeedSnapshot(ws, LifestoneGuid, "A Lifestone", wcid: 103u, itemType: 0x0u,    cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.LifeStone);
        SeedSnapshot(ws, HealerGuid, "Town Healer",    wcid: 104u, itemType: 0x10u,   cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Healer);
        SeedSnapshot(ws, CorpseGuid, "Corpse of Foo",  wcid: 105u, itemType: 0x0u,    cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Corpse);
        // Sanity: an object literally NAMED "Door" but with NO Door bit
        // must NOT be projected as IsDoor. This is the entire point of
        // the bit-based classification.
        SeedSnapshot(ws, MisnamedGuid, "Door", wcid: 106u, itemType: 0x0u, cellId: 0x86020001u,
            objectDescriptionFlags: 0u);

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        var byGuid = proj!.Visible.ToDictionary(v => v.Guid);

        Assert.True(byGuid[DoorGuid].IsDoor);
        Assert.True(byGuid[DoorGuid].IsOpenable);
        Assert.False(byGuid[DoorGuid].IsPortal);
        Assert.True(byGuid[PortalGuid].IsPortal);
        Assert.False(byGuid[PortalGuid].IsDoor);
        Assert.True(byGuid[VendorGuid].IsVendor);
        Assert.True(byGuid[LifestoneGuid].IsLifestone);
        Assert.True(byGuid[HealerGuid].IsHealer);
        Assert.True(byGuid[CorpseGuid].IsCorpse);
        Assert.False(byGuid[MisnamedGuid].IsDoor); // name "Door" alone is NOT enough
    }

    [Fact]
    public void WorldStateProjection_FromWorldState_DecodesDoorOpenClosedFromEtherealBit()
    {
        // Door open/closed is decoded ONLY for Door-flagged objects from the
        // PhysicsState Ethereal bit (0x4): OPEN door => Ethereal set, CLOSED
        // door => Ethereal clear (Source/ACE.Server/WorldObjects/Door.cs).
        // Non-doors never carry IsDoorOpen; a door with unknown physics state
        // is null (we never assert "closed" without evidence).
        const uint OpenDoorGuid   = 0x71000001;
        const uint ClosedDoorGuid = 0x71000002;
        const uint UnknownDoorGuid = 0x71000003;
        const uint EtherealNonDoorGuid = 0x71000004;
        const uint Ethereal = 0x00000004u;
        const uint SomeOtherBit = 0x00000400u; // Gravity bit, not Ethereal

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);
        // Open door: Ethereal set (plus an unrelated bit to prove masking).
        SeedSnapshot(ws, OpenDoorGuid, "Training Area", wcid: 200u, itemType: 0x0u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Door | (uint)ObjectDescriptionFlag.Openable,
            physicsState: Ethereal | SomeOtherBit);
        // Closed door: physics state present, Ethereal clear.
        SeedSnapshot(ws, ClosedDoorGuid, "Iron Gate", wcid: 201u, itemType: 0x0u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Door | (uint)ObjectDescriptionFlag.Openable,
            physicsState: SomeOtherBit);
        // Door with unknown physics state => IsDoorOpen null.
        SeedSnapshot(ws, UnknownDoorGuid, "Mystery Door", wcid: 202u, itemType: 0x0u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Door | (uint)ObjectDescriptionFlag.Openable,
            physicsState: null);
        // Non-door that happens to be Ethereal => IsDoorOpen null (doors only).
        SeedSnapshot(ws, EtherealNonDoorGuid, "Ghostly Mote", wcid: 203u, itemType: 0x0u, cellId: 0x86020001u,
            objectDescriptionFlags: 0u, physicsState: Ethereal);

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        var byGuid = proj!.Visible.ToDictionary(v => v.Guid);

        Assert.True(byGuid[OpenDoorGuid].IsDoorOpen);
        Assert.False(byGuid[ClosedDoorGuid].IsDoorOpen);
        Assert.Null(byGuid[UnknownDoorGuid].IsDoorOpen);
        Assert.Null(byGuid[EtherealNonDoorGuid].IsDoorOpen);
    }

    [Fact]
    public void WorldStateProjection_ObservedHostile_SetFromRecentHostileNames()
    {
        // observed-hostile perception: a visible creature whose normalized
        // name is in world.RecentHostileNames (the TTL-pruned set of
        // attackers the server reported via DefenderNotification) projects
        // as ObservedHostile=true; a creature NOT in the set does not.
        const uint AttackerGuid = 0x90000041;
        const uint BystanderGuid = 0x90000042;

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);
        SeedSnapshot(ws, AttackerGuid, "Drudge Skulker", wcid: 7u, itemType: 0x10u, cellId: 0x86020001u);
        SeedSnapshot(ws, BystanderGuid, "Town Crier", wcid: 8u, itemType: 0x10u, cellId: 0x86020001u);
        // Set is already normalized (collapse + lower-invariant) by the
        // writer; here we seed the normalized form directly.
        ws.RecentHostileNames = new HashSet<string>(StringComparer.Ordinal) { "drudge skulker" };

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        var byGuid = proj!.Visible.ToDictionary(v => v.Guid);

        Assert.True(byGuid[AttackerGuid].ObservedHostile);
        Assert.False(byGuid[BystanderGuid].ObservedHostile);
    }

    [Fact]
    public void WorldStateProjection_ObservedHostile_FalseWhenSetNullOrEmpty()
    {
        // No recent attackers → nothing is flagged hostile (the default).
        const uint CreatureGuid = 0x90000043;
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);
        SeedSnapshot(ws, CreatureGuid, "Drudge Skulker", wcid: 7u, itemType: 0x10u, cellId: 0x86020001u);

        ws.RecentHostileNames = null;
        var projNull = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.False(projNull!.Visible.Single(v => v.Guid == CreatureGuid).ObservedHostile);

        ws.RecentHostileNames = new HashSet<string>(StringComparer.Ordinal);
        var projEmpty = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.False(projEmpty!.Visible.Single(v => v.Guid == CreatureGuid).ObservedHostile);
    }

    [Fact]
    public void WorldStateProjection_ObservedHostile_NameNormalizationMatchesCaseAndWhitespace()
    {
        // The projection normalizes the object name the same way the writer
        // normalizes the wire attacker name (collapse whitespace + lower).
        // A server object name with odd casing / extra spaces must still
        // match a normalized set entry.
        const uint CreatureGuid = 0x90000044;
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);
        SeedSnapshot(ws, CreatureGuid, "  Young   Banderling ", wcid: 9u, itemType: 0x10u, cellId: 0x86020001u);

        ws.RecentHostileNames = new HashSet<string>(StringComparer.Ordinal) { "young banderling" };
        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        Assert.True(proj!.Visible.Single(v => v.Guid == CreatureGuid).ObservedHostile);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Drudge Skulker", "drudge skulker")]
    [InlineData("  Young   Banderling ", "young banderling")]
    [InlineData("COW", "cow")]
    public void WorldStateProjection_NormalizeHostileName_CollapsesAndLowercases(string? input, string? expected)
    {
        Assert.Equal(expected, WorldStateProjection.NormalizeHostileName(input));
    }

    [Fact]
    public void WorldStateProjection_Slice_U_DistinguishesChestBookSignFromContainerAndWritable()
    {
        // Slice U — three new composite predicates derived from the
        // existing ObjectDescriptionFlag + ItemType wire bits:
        //   IsChest = Container itemType + Openable descFlag, !Corpse, !Door
        //   IsBook  = Writable itemType, !Stuck descFlag
        //   IsSign  = Writable itemType,  Stuck descFlag
        // Composition rules — chest and door BOTH have Openable, but a
        // door already has IsDoor=true and must NOT also be IsChest.
        // Sack (own bag) is filtered earlier by ContainerGuid; here we
        // only verify the in-world derivation logic.
        const uint ChestGuid       = 0x71000001;
        const uint BookshelfGuid   = 0x71000002;
        const uint BookGuid        = 0x71000003;
        const uint SignGuid        = 0x71000004;
        const uint DoorGuid        = 0x71000005;
        const uint CorpseGuid      = 0x71000006;
        const uint PlainBookcaseGuid = 0x71000007;

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);

        // Chest: Container itemType + Openable description flag.
        SeedSnapshot(ws, ChestGuid, "Wooden Chest", wcid: 200u,
            itemType: ItemTypeMasks.Container, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Openable);
        // Bookshelf: same wire shape as chest; should also be IsChest.
        SeedSnapshot(ws, BookshelfGuid, "Academy Bookshelf", wcid: 201u,
            itemType: ItemTypeMasks.Container, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Openable);
        // Book on a table: Writable itemType, NOT Stuck.
        SeedSnapshot(ws, BookGuid, "Magic Tips", wcid: 202u,
            itemType: ItemTypeMasks.Writable, cellId: 0x86020001u,
            objectDescriptionFlags: 0u);
        // Sign bolted to a wall: Writable itemType, IS Stuck.
        SeedSnapshot(ws, SignGuid, "VIEW CONTROLS", wcid: 203u,
            itemType: ItemTypeMasks.Writable, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Stuck);
        // Door: Door + Openable. Must NOT also be IsChest.
        SeedSnapshot(ws, DoorGuid, "Iron Gate", wcid: 204u,
            itemType: 0u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Door | (uint)ObjectDescriptionFlag.Openable);
        // Corpse: Corpse description flag. Must NOT also be IsChest
        // even if it carries Container itemType (corpses do).
        SeedSnapshot(ws, CorpseGuid, "Corpse of Foo", wcid: 205u,
            itemType: ItemTypeMasks.Container, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Corpse);
        // Plain Container with no Openable bit (decorative bookcase the
        // player can never open). Must NOT be IsChest — the LLM should
        // ignore it, not waste a Use cycle.
        SeedSnapshot(ws, PlainBookcaseGuid, "Decorative Bookcase", wcid: 206u,
            itemType: ItemTypeMasks.Container, cellId: 0x86020001u,
            objectDescriptionFlags: 0u);

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        var byGuid = proj!.Visible.ToDictionary(v => v.Guid);

        Assert.True (byGuid[ChestGuid].IsChest,        "chest with Openable bit must be IsChest");
        Assert.False(byGuid[ChestGuid].IsBook);
        Assert.False(byGuid[ChestGuid].IsSign);
        Assert.True (byGuid[BookshelfGuid].IsChest,    "openable bookshelf must be IsChest");

        Assert.True (byGuid[BookGuid].IsBook,          "Writable + !Stuck must be IsBook");
        Assert.False(byGuid[BookGuid].IsSign);
        Assert.False(byGuid[BookGuid].IsChest);

        Assert.True (byGuid[SignGuid].IsSign,          "Writable + Stuck must be IsSign");
        Assert.False(byGuid[SignGuid].IsBook);
        Assert.False(byGuid[SignGuid].IsChest);

        Assert.True (byGuid[DoorGuid].IsDoor);
        Assert.False(byGuid[DoorGuid].IsChest,         "door must not also be classified as chest");

        Assert.True (byGuid[CorpseGuid].IsCorpse);
        Assert.False(byGuid[CorpseGuid].IsChest,       "corpse must not also be classified as chest");

        Assert.False(byGuid[PlainBookcaseGuid].IsChest, "container without Openable bit is not a chest");
    }

    [Fact]
    public void WorldStateProjection_FromWorldState_DerivesIsMonsterFromAttackableAndRadarBlipColor()
    {
        // Slice H — IsMonster is a server-derived composite. The friend/
        // foe decision MUST come from the wire bits (Attackable +
        // RadarBlipColor), never from hardcoded wcid lists or English
        // name matching. Live observation: Sparring Golem wFlags
        // =0x00800036 (no RadarBlipColor=0x100000) and ObjectDescriptionFlag
        // .Attackable=true; civilian NPCs wFlags=0x00900036 (custom
        // RadarBlipColor) and ObjectDescriptionFlag.Attackable=true.
        const uint MonsterGuid       = 0x80000001;
        const uint NpcCivilianGuid   = 0x80000002;
        const uint VendorCreatureGuid = 0x80000003;
        const uint HealerCreatureGuid = 0x80000004;
        const uint NonCreatureGuid   = 0x80000005;

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);

        // Generic monster: Attackable, no custom radar color.
        SeedSnapshot(ws, MonsterGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable,
            weenieFlags: 0x00800036u);
        // Civilian NPC: Attackable AND has custom radar color → npc.
        SeedSnapshot(ws, NpcCivilianGuid, "Jonathan", wcid: 29324u, itemType: 0x10u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable,
            weenieFlags: 0x00900036u);
        // Vendor creature: Attackable + no radar color but the Vendor
        // bit MUST suppress monster classification (special-purpose NPC).
        SeedSnapshot(ws, VendorCreatureGuid, "Shopkeeper Bob", wcid: 102u, itemType: 0x10u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable | (uint)ObjectDescriptionFlag.Vendor,
            weenieFlags: 0x00800036u);
        // Healer creature: same logic — Healer bit suppresses monster.
        SeedSnapshot(ws, HealerCreatureGuid, "Town Healer", wcid: 104u, itemType: 0x10u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable | (uint)ObjectDescriptionFlag.Healer,
            weenieFlags: 0x00800036u);
        // Non-creature (no Creature bit in itemType) must never be monster.
        SeedSnapshot(ws, NonCreatureGuid, "Pretty Rock", wcid: 999u, itemType: 0x0u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable,
            weenieFlags: 0x00800036u);

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        var byGuid = proj!.Visible.ToDictionary(v => v.Guid);

        Assert.True(byGuid[MonsterGuid].IsMonster);
        Assert.True(byGuid[MonsterGuid].IsAttackable);
        Assert.False(byGuid[MonsterGuid].HasRadarBlipColor);

        Assert.False(byGuid[NpcCivilianGuid].IsMonster); // RadarBlipColor → npc
        Assert.True(byGuid[NpcCivilianGuid].IsAttackable);
        Assert.True(byGuid[NpcCivilianGuid].HasRadarBlipColor);

        Assert.False(byGuid[VendorCreatureGuid].IsMonster); // Vendor bit suppresses
        Assert.True(byGuid[VendorCreatureGuid].IsVendor);

        Assert.False(byGuid[HealerCreatureGuid].IsMonster); // Healer bit suppresses
        Assert.True(byGuid[HealerCreatureGuid].IsHealer);

        Assert.False(byGuid[NonCreatureGuid].IsMonster); // not a creature
        Assert.False(byGuid[NonCreatureGuid].IsCreature);
    }

    [Fact]
    public void WorldStateProjection_FromWorldState_WidenedRadius_SurfacesMonsterBeyond60u()
    {
        // Regression for the perception-radius widening: an object at d=70
        // (just outside the old 60u projection clip) was excluded from
        // `Visible`, so prompt sections that scan `Visible` (e.g.
        // nearest-monster) reported nothing even when the bot stood near it.
        // The widened default radius (120u) must surface a monster at d=70 and
        // d=110, and still exclude one beyond the radius (d=130). Synthetic
        // fixtures only — the assertions exercise wire-bit + geometry, not any
        // specific game entity.
        const uint NearMonsterGuid = 0x80000011;
        const uint MidMonsterGuid  = 0x80000012;
        const uint FarMonsterGuid  = 0x80000013;
        const uint OutdoorCell = 0x00010001u; // low16 < 0x100 → outdoor

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Self", wcid: 1u, itemType: 0u, cellId: OutdoorCell,
            position: new Vector3(0, 0, 0));

        // d=70 — was clipped by the old 60u radius, now must surface.
        SeedSnapshot(ws, NearMonsterGuid, "MonsterA", wcid: 1001u, itemType: 0x10u, cellId: OutdoorCell,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable, weenieFlags: 0x00800036u,
            position: new Vector3(70, 0, 0));
        // d=110 — inside the widened 120u radius.
        SeedSnapshot(ws, MidMonsterGuid, "MonsterB", wcid: 1002u, itemType: 0x10u, cellId: OutdoorCell,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable, weenieFlags: 0x00800036u,
            position: new Vector3(110, 0, 0));
        // d=130 — still beyond the radius, must remain excluded.
        SeedSnapshot(ws, FarMonsterGuid, "MonsterC", wcid: 1003u, itemType: 0x10u, cellId: OutdoorCell,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable, weenieFlags: 0x00800036u,
            position: new Vector3(130, 0, 0));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        var byGuid = proj!.Visible.ToDictionary(v => v.Guid);

        Assert.True(byGuid.ContainsKey(NearMonsterGuid), "monster at d=70 must surface under the widened radius");
        Assert.True(byGuid[NearMonsterGuid].IsMonster);
        Assert.True(byGuid.ContainsKey(MidMonsterGuid), "monster at d=110 must surface under the widened radius");
        Assert.False(byGuid.ContainsKey(FarMonsterGuid), "monster at d=130 stays beyond the 120u radius");
    }

    // Helper: build a tiny WorldState with three objects whose attributes
    // exercise every Selector field. We use Apply via a synthetic ObjectCreate
    // when feasible; otherwise we stand up snapshots through the snapshot
    // constructor reflectively. To avoid reflection, we use the internal
    // setters: WorldState.Objects exposes a snapshot per guid that we get
    // by going through TryGet + manual seeding. Since setters are internal
    // and InternalsVisibleTo includes HeadlessAcClient.Tests, we can do
    // this directly via SeedSnapshot below.
    private static WorldState BuildSeededWorld()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, NpcGuid, "Jonathan", wcid: 29324u, itemType: 0x10u, cellId: 0x86020001u);
        SeedSnapshot(ws, MobGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u);
        SeedSnapshot(ws, ItemGuid, "Academy Exit Token", wcid: 29335u, itemType: 0x800u, cellId: 0u, containerGuid: SelfGuid);
        return ws;
    }

    private static void SeedSnapshot(
        WorldState ws,
        uint guid,
        string name,
        uint wcid,
        uint itemType,
        uint cellId,
        uint? containerGuid = null,
        uint? objectDescriptionFlags = null,
        uint? weenieFlags = null,
        Vector3? position = null,
        uint? physicsState = null)
    {
        // WorldState lacks a public seed helper. The most direct path
        // without reflection is to mutate via internal setters on a
        // freshly-created snapshot, then attach it via the internal
        // GetOrCreate path. We use the SnapshotSeeding test seam.
        SnapshotSeeding.Seed(ws, guid, name, wcid, itemType, cellId, containerGuid, objectDescriptionFlags, weenieFlags, position, physicsState);
    }

    private sealed class FakeWeenieRepo : IWeenieRepository
    {
        private readonly System.Collections.Generic.Dictionary<uint, WeenieStringRecord> _map = new();
        public void Seed(uint wcid, string? name, string? shortDesc, string? longDesc = null, string? useDesc = null, string? title = null)
            => _map[wcid] = new WeenieStringRecord(wcid, name, shortDesc, longDesc, useDesc, title);
        public WeenieStringRecord? TryGet(uint wcid) => _map.TryGetValue(wcid, out var r) ? r : null;
        public System.Threading.Tasks.Task EnsureLoadedAsync(uint wcid, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.CompletedTask;
    }

    // ---- NoQuestKnowledgePolicy ----

    [Fact]
    public void NoQuestKnowledgePolicy_PicksAttackForObservedHostile()
    {
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid,
                Name = "Headless",
                Landblock = 0x8602u,
                CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = MobGuid, Name = "Sparring Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 5f, IsCreature = true, ObservedHostile = true,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind);
        Assert.Equal(MobGuid, goal.Target.Guid);
        Assert.Equal("fallback:no-quest-knowledge", goal.Source);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_DoesNotAttackHostilePlayer()
    {
        // A player is not an auto-attack target even if (hypothetically, on a PvP
        // server) it read ObservedHostile: the autonomous hostile-attack picker
        // excludes players, mirroring their exclusion from the monster/NPC paths.
        var policy = new NoQuestKnowledgePolicy();
        var proj = FallbackWorldWith(new VisibleObjectProjection
        {
            Guid = 0x500000A1u, Name = "Otherbot", Wcid = 1u,
            ItemType = 0x10u, Distance = 5f, IsCreature = true,
            IsPlayer = true, ObservedHostile = true,
        });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        // May fall through to Explore/Wait, but must NOT Attack the player.
        Assert.False(goal is { Kind: GoalKind.Attack } && goal.Target?.Guid == 0x500000A1u);
    }

    private static WorldStateProjection FallbackWorldWith(params VisibleObjectProjection[] visible) =>
        new()
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = visible,
        };

    [Fact]
    public void NoQuestKnowledgePolicy_SkipsDistantOpenable_WhenNotHunting()
    {
        // cp-2413: with no LLM and no active hunt, the fallback must NOT march
        // the bot a long way (here 80u) to Use an optional openable. It yields
        // and the bot Explores toward new area instead of marathoning to a
        // single far chest (live: 65-121u chest marches).
        var policy = new NoQuestKnowledgePolicy();
        var proj = FallbackWorldWith(new VisibleObjectProjection
        {
            Guid = 0x71000099u, Name = "Chest", Distance = 80f, IsOpenable = true,
        });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Explore, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_UsesNearOpenable_WithinDetourBound()
    {
        // A nearby openable (within the no-hunt detour bound) is still Used —
        // the bound only stops marathons, it does not disable the openable step.
        var policy = new NoQuestKnowledgePolicy();
        var proj = FallbackWorldWith(new VisibleObjectProjection
        {
            Guid = 0x71000099u, Name = "Chest", Distance = 20f, IsOpenable = true,
        });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(0x71000099u, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_SkipsDistantNpcTalk_WhenNotHunting()
    {
        // A distant non-hostile NPC (80u) is not worth marching to chat with in
        // the fallback; bounded out, the bot Explores instead.
        var policy = new NoQuestKnowledgePolicy();
        var proj = FallbackWorldWith(new VisibleObjectProjection
        {
            Guid = 0x90000099u, Name = "Town Crier", Distance = 80f, IsCreature = true,
        });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Talk, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_UsesNullDistanceOpenable_NotBoundedOut()
    {
        // A visible object can have a NULL (unmeasurable) Distance in the real
        // projection. The detour bound must NOT treat that as "far" and exclude
        // it — an adjacent object whose distance just couldn't be computed would
        // then be unreachable. Null distance is treated as near (included).
        var policy = new NoQuestKnowledgePolicy();
        var proj = FallbackWorldWith(new VisibleObjectProjection
        {
            Guid = 0x71000099u, Name = "Chest", Distance = null, IsOpenable = true,
        });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(0x71000099u, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_LowHealth_StillFightsHostile_NoHardcodedFloor()
    {
        // Regression for relocate-noquest-health-floor: the fallback
        // used to short-circuit to a `Wait` goal whenever self-health
        // dropped below a hardcoded 0.3 fraction. That magic threshold
        // was forbidden game knowledge (a rule-of-thumb the LLM must
        // own via an Intent predicate), AND a no-op fiction — this
        // fallback has no heal/flee action, so freezing a wounded bot
        // only stopped it defending itself. With the gate removed, a
        // wounded bot facing a hostile still proposes Attack.
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 0.05f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = MobGuid, Name = "Sparring Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 5f, IsCreature = true, ObservedHostile = true,
                },
            },
        };
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind);
        Assert.NotEqual(GoalKind.Wait, goal.Kind);
        Assert.Equal(MobGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_DoesNotGiveItem_BecauseThatRequiresQuestKnowledge()
    {
        // The whole point of this policy: even though the bot is holding
        // an Exit Token and Jonathan is right there, the fallback will
        // NOT propose a Give goal. Only the LLM does. Bot would
        // explore/talk instead.
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                {
                    Guid = ItemGuid, Name = "Academy Exit Token",
                    Wcid = 29335u, ItemType = 0x800u, ValidLocations = 0,
                    ShortDesc = "Give this token to Jonathan ...",
                },
            },
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = NpcGuid, Name = "Jonathan", Wcid = 29324u,
                    ItemType = 0x10u, Distance = 3f, IsCreature = true,
                    ObservedHostile = false,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Give, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_SkipsPickup_WhenTargetRecentlyRejected()
    {
        // Slice J — the fallback used to loop on the same Bruised
        // Apple guid when the server rejected the pickup (geometry
        // blocked or out of physical reach). Now ActionRejected
        // events carrying ItemGuid cause the policy to skip that
        // guid on subsequent ticks; with only one pickup candidate
        // and no other actionable goal, it falls through to Explore.
        const uint AppleGuid = 0x800004DC;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = AppleGuid, Name = "Bruised Apple", Wcid = 5090u,
                    ItemType = 0x20u, Distance = 4.9f, IsCreature = false,
                    ObservedHostile = false,
                },
            },
        };
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            Text = "Unreachable: 'Bruised Apple' (walk timeout 30s)",
            ItemGuid = AppleGuid,
            Name = "Bruised Apple",
            ErrorCode = 0xFFFE,
            ErrorLabel = "Unreachable",
        });
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Pickup, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_RotatesTalkTargets_AcrossSuccessiveTicks()
    {
        // Slice K — fallback used to lock onto the same nearest NPC
        // every tick when the LLM picked Explore (an unhandled goal
        // kind) and yielded the floor back to the fallback. Without
        // a "what did I just propose" memory, the same Bottle/NPC
        // got picked over and over. Now the policy remembers the
        // last N (=8) proposed Talk/Pickup targets and skips them,
        // forcing a round-robin across visible candidates.
        const uint NpcA = 0x800010A1;
        const uint NpcB = 0x800010A2;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0xA9B4u,
                CellId = 0xA9B40155u, PositionX = 100, PositionY = 40, PositionZ = 94,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = NpcA, Name = "Bottle", Wcid = 40526u,
                    ItemType = 0x10u, Distance = 6.7f, IsCreature = true,
                    ObservedHostile = false,
                },
                new VisibleObjectProjection
                {
                    Guid = NpcB, Name = "Pathwarden Thorolf", Wcid = 0u,
                    ItemType = 0x10u, Distance = 12.0f, IsCreature = true,
                    ObservedHostile = false,
                },
            },
        };
        var events = new EventStream();

        var first = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(first);
        Assert.Equal(GoalKind.Talk, first!.Kind);
        var firstGuid = first.Target?.Guid;
        Assert.NotNull(firstGuid);

        var second = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(second);
        Assert.Equal(GoalKind.Talk, second!.Kind);
        var secondGuid = second.Target?.Guid;
        Assert.NotNull(secondGuid);
        Assert.NotEqual(firstGuid, secondGuid);
    }

    // ---- Step 5b: fallback Use{openable} for visible openable objects ----
    //
    // Schema-only behavior: any visible object with the
    // ObjectDescriptionFlag.Openable bit (and not a Door, which has its
    // own dispatch path) becomes a fallback Use target. Mirrors the
    // existing step 4 (Pickup) and step 6 (Talk) shape — observation
    // drives behavior, no game-knowledge value judgment about whether
    // openable things are valuable to open. Adds a path for corpse
    // looting that does NOT require either the priority-bump (audited
    // out per Slice U revert) or the LLM (often quota-suppressed).

    [Fact]
    public void NoQuestKnowledgePolicy_PicksUse_ForVisibleOpenableCorpse()
    {
        const uint CorpseGuid = 0x5F000100;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = CorpseGuid, Name = "Corpse of Sparring Golem",
                    Wcid = 21u, ItemType = 0x200u, Distance = 1.6f,
                    IsOpenable = true, IsCorpse = true,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(CorpseGuid, goal.Target.Guid);
        Assert.Equal("fallback:no-quest-knowledge", goal.Source);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_PicksUse_ForGenericOpenableChest_NotCorpse()
    {
        // Generic openable (e.g. a treasure chest in a dungeon) MUST
        // be eligible too — the schema-only filter cannot single out
        // corpses. Wire-bit `Openable` is the affordance, regardless
        // of game-role.
        const uint ChestGuid = 0x70000200;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = ChestGuid, Name = "Treasure Chest",
                    Wcid = 13007u, ItemType = 0x200u, Distance = 3.0f,
                    IsOpenable = true, IsChest = true, IsCorpse = false,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(ChestGuid, goal.Target.Guid);
    }

    // ---- cp-2360/cp-2361: break a barren TOWN-OBJECT tour (fallback egress) ----
    // With no LLM (rate-limited), this fallback re-picks the nearest town object
    // (openable / lifestone / portal / NPC) every tick; a town full of static
    // objects becomes an endless tour. After touring a threshold of DISTINCT town
    // objects in one landblock with no egress and no productive inventory change,
    // the town-interaction steps tap out (shared across types) so the deliberation
    // falls through to Explore. Resets on landblock or inventory change.

    private static WorldStateProjection TourWorld(uint landblock, params VisibleObjectProjection[] visible) =>
        new()
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = landblock,
                CellId = (landblock << 16) | 0x0001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = visible,
        };

    private static VisibleObjectProjection TourChest(uint g, float d) => new()
    {
        Guid = g, Name = "Chest", Wcid = 13007u, ItemType = 0x200u, Distance = d,
        IsOpenable = true, IsChest = true,
    };

    private static VisibleObjectProjection TourNpc(uint g, float d) => new()
    {
        Guid = g, Name = "Townsfolk", Wcid = 1u, Distance = d,
        IsCreature = true, IsMonster = false,
    };

    private static VisibleObjectProjection[] ManyChests() => new[]
    {
        TourChest(0x7001u, 1f), TourChest(0x7002u, 2f), TourChest(0x7003u, 3f),
        TourChest(0x7004u, 4f), TourChest(0x7005u, 5f), TourChest(0x7006u, 6f),
        TourChest(0x7007u, 7f), TourChest(0x7008u, 8f),
    };

    // cp-2373: a ground pickup-eligible item (ItemType carries a Pickup-mask bit,
    // not openable/creature) — the fallback Pickup step targets these.
    private static VisibleObjectProjection TourPickup(uint g, float d) => new()
    {
        Guid = g, Name = "Ground Item", Wcid = 9999u, ItemType = 0x2u, Distance = d,
    };

    private static VisibleObjectProjection[] ManyPickups() => new[]
    {
        TourPickup(0x6001u, 1f), TourPickup(0x6002u, 2f), TourPickup(0x6003u, 3f),
        TourPickup(0x6004u, 4f), TourPickup(0x6005u, 5f), TourPickup(0x6006u, 6f),
        TourPickup(0x6007u, 7f), TourPickup(0x6008u, 8f),
    };

    [Fact]
    public void NoQuestKnowledgePolicy_TownTour_TapsOutAndEgresses()
    {
        var policy = new NoQuestKnowledgePolicy();
        var proj = TourWorld(0xA9B4u, ManyChests());
        var events = new EventStream();
        // Tour the forgiveness threshold of DISTINCT town objects.
        for (int i = 0; i < 6; i++)
            Assert.Equal(GoalKind.Use, policy.ProposeGoal(proj, events, null)!.Kind);
        // Tapped out: no town object proposed -> falls through to Explore.
        var last = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(last);
        Assert.NotEqual(GoalKind.Use, last!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_TownTour_SharedAcrossOpenableAndNpc()
    {
        // cp-2361: the tap is SHARED across town-object types — touring 3 chests
        // and 3 NPCs (6 distinct) taps out the same counter, so the 7th town
        // interaction (a Talk) is suppressed and the bot egresses.
        var policy = new NoQuestKnowledgePolicy();
        var proj = TourWorld(0xA9B4u,
            TourChest(0x7001u, 1f), TourChest(0x7002u, 2f), TourChest(0x7003u, 3f),
            TourNpc(0x8001u, 4f), TourNpc(0x8002u, 5f), TourNpc(0x8003u, 6f), TourNpc(0x8004u, 7f));
        var events = new EventStream();
        var kinds = new System.Collections.Generic.List<GoalKind>();
        for (int i = 0; i < 6; i++)
            kinds.Add(policy.ProposeGoal(proj, events, null)!.Kind);
        // The first 6 were town interactions (Use chests then Talk NPCs).
        Assert.All(kinds, k => Assert.True(k is GoalKind.Use or GoalKind.Talk));
        // 7th: shared tap -> neither a chest Use nor an NPC Talk -> Explore.
        var last = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(last);
        Assert.True(last!.Kind is not GoalKind.Use and not GoalKind.Talk);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_TownTour_ResetsOnLandblockChange()
    {
        var policy = new NoQuestKnowledgePolicy();
        var chests = ManyChests();
        var events = new EventStream();
        for (int i = 0; i < 6; i++)
            policy.ProposeGoal(TourWorld(0xA9B4u, chests), events, null);
        Assert.NotEqual(GoalKind.Use, policy.ProposeGoal(TourWorld(0xA9B4u, chests), events, null)!.Kind); // tapped
        // Egress to a new landblock resets the tour memory -> town objects eligible again.
        Assert.Equal(GoalKind.Use, policy.ProposeGoal(TourWorld(0xCCCCu, chests), events, null)!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_TownTour_ResetsOnInventoryAdd()
    {
        var policy = new NoQuestKnowledgePolicy();
        var chests = ManyChests();
        var events = new EventStream();
        for (int i = 0; i < 6; i++)
            policy.ProposeGoal(TourWorld(0xA9B4u, chests), events, null);
        Assert.NotEqual(GoalKind.Use, policy.ProposeGoal(TourWorld(0xA9B4u, chests), events, null)!.Kind); // tapped
        // A productive loot (InventoryItemAdded) resets the tour memory. The item
        // guid is not in Inventory, so the inv-Use step does not consume the turn.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.InventoryItemAdded,
            ItemGuid = 0x900u, Wcid = 273u, Name = "Pyreal",
        });
        Assert.Equal(GoalKind.Use, policy.ProposeGoal(TourWorld(0xA9B4u, chests), events, null)!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_TownTour_TapsOut_PickupChurn()
    {
        // cp-2373: a barren Pickup churn (pickup-eligible items that never enter
        // the bag — 0 InventoryItemAdded) counts toward the SHARED town-tour cap,
        // so the 7th re-proposed Pickup taps out and the bot falls through to
        // Explore instead of re-targeting un-acquirable ground items forever.
        var policy = new NoQuestKnowledgePolicy();
        var proj = TourWorld(0xA9B4u, ManyPickups());
        var events = new EventStream();
        for (int i = 0; i < 6; i++)
            Assert.Equal(GoalKind.Pickup, policy.ProposeGoal(proj, events, null)!.Kind);
        var last = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(last);
        Assert.NotEqual(GoalKind.Pickup, last!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_TownTour_PickupResetsOnInventoryAdd()
    {
        // A PRODUCTIVE Pickup (InventoryItemAdded) resets the cap, so genuine
        // looting is never falsely capped. The added item's guid is not in
        // Inventory, so the inv-Use step does not consume the turn.
        var policy = new NoQuestKnowledgePolicy();
        var pickups = ManyPickups();
        var events = new EventStream();
        for (int i = 0; i < 6; i++)
            policy.ProposeGoal(TourWorld(0xA9B4u, pickups), events, null);
        Assert.NotEqual(GoalKind.Pickup,
            policy.ProposeGoal(TourWorld(0xA9B4u, pickups), events, null)!.Kind); // tapped
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.InventoryItemAdded,
            ItemGuid = 0x901u, Wcid = 9999u, Name = "Picked Item",
        });
        Assert.Equal(GoalKind.Pickup,
            policy.ProposeGoal(TourWorld(0xA9B4u, pickups), events, null)!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_TownTour_TapsOut_SparseNpcReTalkLoop()
    {
        // cp-2361 correctness fix: a sparse landblock with only 1-2 NPCs never
        // reaches a DISTINCT threshold; the Talk-recycle re-Talks the same NPCs
        // forever. A TOTAL proposal counter (not distinct) catches it: re-Talking
        // a couple NPCs enough times taps out to Explore.
        var policy = new NoQuestKnowledgePolicy();
        var proj = TourWorld(0xA9B4u, TourNpc(0x8001u, 1f), TourNpc(0x8002u, 2f));
        var events = new EventStream();
        var egressed = false;
        for (int i = 0; i < 12; i++)
        {
            if (policy.ProposeGoal(proj, events, null)!.Kind is not GoalKind.Talk)
            {
                egressed = true;
                break;
            }
        }
        Assert.True(egressed, "re-Talking a few NPCs must eventually tap out to a non-Talk goal (Explore)");
    }

    [Fact]
    public void NoQuestKnowledgePolicy_DoesNotPickDoor_AsOpenable()
    {
        // Doors carry the Openable bit too, but they have their own
        // walk-tick door-USE dispatch path. Step 5b must skip them so
        // the door-handling pipeline is not duplicated/short-circuited
        // by a Use goal here.
        const uint DoorGuid = 0x78602000;
        const uint NpcGuid  = 0x800001AA;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = DoorGuid, Name = "Door", Wcid = 31064u,
                    ItemType = 0x80u, Distance = 2.0f,
                    IsOpenable = true, IsDoor = true,
                },
                new VisibleObjectProjection
                {
                    Guid = NpcGuid, Name = "Jonathan", Wcid = 29324u,
                    ItemType = 0x10u, Distance = 8.0f,
                    IsCreature = true, ObservedHostile = false,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        // The door is closer (d=2) and openable, but Step 5b skips
        // doors → Step 6 picks Jonathan instead.
        Assert.Equal(GoalKind.Talk, goal!.Kind);
        Assert.Equal(NpcGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_SkipsOpenable_WhenRecentlyRejected()
    {
        // Same dedup pattern as Step 4 (Pickup) — an ActionRejected
        // for the openable guid (e.g. server denied open because we
        // already opened and emptied it, or geometry blocked) must
        // suppress the candidate.
        const uint CorpseGuid = 0x5F000300;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = CorpseGuid, Name = "Corpse of Drudge",
                    Wcid = 22u, ItemType = 0x200u, Distance = 1.0f,
                    IsOpenable = true, IsCorpse = true,
                },
            },
        };
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            Text = "Unreachable: 'Corpse of Drudge'",
            ItemGuid = CorpseGuid,
            Name = "Corpse of Drudge",
            ErrorCode = 0xFFFE,
            ErrorLabel = "Unreachable",
        });
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Use, goal!.Kind);
    }

    // cp-2403: a visible openable Chest at a far distance — the world the
    // fallback's openable-Use step (5b) acts on. Only the chest is visible, so
    // with no earlier-step candidate the policy reaches step 5b.
    private static WorldStateProjection ChestWorld(uint chestGuid) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xA9B4u,
            CellId = 0xA9B40001u, PositionX = 0, PositionY = 0, PositionZ = 0,
            HealthFraction = 1.0f,
        },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = new[]
        {
            new VisibleObjectProjection
            {
                Guid = chestGuid, Name = "Chest", Wcid = 33609u,
                ItemType = 0x200u, Distance = 30.0f,
                IsOpenable = true, IsChest = true,
            },
        },
    };

    [Fact]
    public void NoQuestKnowledgePolicy_SkipsOpenable_WhenRecentlyEmptied()
    {
        // cp-2403: a container the Motor opened and found EMPTY (marked in the
        // shared TTL tracker) is skipped by the fallback openable-Use step, so
        // the bot stops marching to empty chests when the LLM throttles. With
        // only the empty chest visible, the policy falls through to Explore.
        const uint ChestGuid = 0x7A9B4001u;
        var emptied = new HeadlessAcClient.World.InteractUnreachableTracker();
        emptied.MarkUnreachable(ChestGuid, DateTime.UtcNow, TimeSpan.FromSeconds(180));
        var policy = new NoQuestKnowledgePolicy(null, null, emptied);
        var goal = policy.ProposeGoal(ChestWorld(ChestGuid), new EventStream(), null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Use, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_UsesOpenable_WhenNotEmptied()
    {
        // Same world, but the chest is NOT in the emptied tracker -> Use it.
        const uint ChestGuid = 0x7A9B4001u;
        var emptied = new HeadlessAcClient.World.InteractUnreachableTracker();
        var policy = new NoQuestKnowledgePolicy(null, null, emptied);
        var goal = policy.ProposeGoal(ChestWorld(ChestGuid), new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(ChestGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_UsesOpenable_AfterEmptiedCooldownExpires()
    {
        // TTL'd: a chest marked emptied with an already-elapsed cooldown is
        // retryable — a respawned chest may have refilled loot.
        const uint ChestGuid = 0x7A9B4001u;
        var emptied = new HeadlessAcClient.World.InteractUnreachableTracker();
        emptied.MarkUnreachable(ChestGuid, DateTime.UtcNow - TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        var policy = new NoQuestKnowledgePolicy(null, null, emptied);
        var goal = policy.ProposeGoal(ChestWorld(ChestGuid), new EventStream(), null);
        Assert.Equal(GoalKind.Use, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_NullEmptiedTracker_UsesOpenableNormally()
    {
        // Back-compat: the tracker is optional; a null one suppresses nothing.
        var policy = new NoQuestKnowledgePolicy(null, null, null);
        var goal = policy.ProposeGoal(ChestWorld(0x7A9B4001u), new EventStream(), null);
        Assert.Equal(GoalKind.Use, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_DoesNotProposeSameOpenableTwiceInARow()
    {
        // The shared _recentProposedGuids queue (size 8) prevents tight
        // re-Use loops on a single openable that doesn't despawn after
        // open. With only one openable visible and no other actionable
        // candidates, the second tick falls through to Explore (or the
        // recycle path).
        const uint CorpseGuid = 0x5F000400;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = CorpseGuid, Name = "Corpse of Drudge Sklavos",
                    Wcid = 31u, ItemType = 0x200u, Distance = 0.8f,
                    IsOpenable = true, IsCorpse = true,
                },
            },
        };
        var events = new EventStream();

        var first = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(first);
        Assert.Equal(GoalKind.Use, first!.Kind);
        Assert.Equal(CorpseGuid, first.Target.Guid);

        var second = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(second);
        // Second tick: same corpse is in _recentProposedGuids → must
        // NOT propose another Use against it.
        if (second!.Kind == GoalKind.Use)
            Assert.NotEqual(CorpseGuid, second.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_DoesNotProposeSameUnwieldedGearTwiceInARow()
    {
        // Regression for the portal02-spike bug: a wearable that the
        // server fails to actuate (PHASE6L GetAndWieldItem raced the
        // PUTITEMINCONTAINER ack and was rejected; WieldedAt stays
        // null) leaves step 3 (Wield) firing every tick forever.
        // Because the HandshakeDriver dispatcher has no Wield branch
        // in its action allowlist, the goal is silently no-op'd and
        // the picker takes over — meaning later steps (4 Pickup, 5b
        // openable, 5c lifestone, 5d portal, 6 Talk) never run.
        //
        // Symmetric dedup with step 4 Pickup must apply: emit Wield
        // once for the item, remember it, then fall through on
        // subsequent ticks so downstream steps can fire.
        const uint UnwieldedCapGuid = 0x80000483;
        const uint VisibleNpcGuid   = 0x80000700;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                {
                    Guid = UnwieldedCapGuid, Name = "Leather Cap", Wcid = 13239u,
                    ItemType = 0x2u, ValidLocations = 0x1u, WieldedAt = null,
                },
            },
            Visible = new[]
            {
                // Provide a non-Wield candidate so the second-tick
                // proposal is unambiguous (would be Talk via step 6).
                new VisibleObjectProjection
                {
                    Guid = VisibleNpcGuid, Name = "Society Greeter",
                    Wcid = 700u, ItemType = 0x10u, Distance = 4.0f,
                },
            },
        };
        var events = new EventStream();

        var first = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(first);
        Assert.Equal(GoalKind.Wield, first!.Kind);
        Assert.Equal(UnwieldedCapGuid, first.Item!.Guid);

        var second = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(second);
        // Second tick: cap is in _recentProposedGuids → step 3 must
        // skip it, allowing step 6 (Talk) to fire on the NPC.
        Assert.NotEqual(GoalKind.Wield, second!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_SkipsWield_WhenWeaponCollidesWithWieldedWeapon()
    {
        // Regression: a melee weapon is already wielded; the bag holds a
        // redundant missile weapon (Royal Atlatl) the server would refuse
        // to wield (CheckWeaponCollision). The fallback must NOT propose
        // Wield for it — otherwise the LLM Wield dispatch's dequip-before-
        // wield swap (cp-2244) dequips the working melee weapon and leaves
        // the bot holding an ammoless missile weapon (self-disarm). Step 3
        // must skip the colliding weapon and fall through to Talk.
        const uint WieldedSpadoneGuid = 0x80005514;
        const uint UnwieldedAtlatlGuid = 0x80009C7E;
        const uint VisibleNpcGuid = 0x80000700;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0xA9B4u,
                CellId = 0xA9B40001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                {
                    // Already wielded melee weapon (TwoHanded slot 0x2000000).
                    Guid = WieldedSpadoneGuid, Name = "Training Spadone", Wcid = 41512u,
                    ItemType = 0x1u, ValidLocations = 0x2000000u, WieldedAt = 0x2000000u,
                },
                new InventoryItemProjection
                {
                    // Unwielded missile weapon — collides, must be skipped.
                    Guid = UnwieldedAtlatlGuid, Name = "Royal Atlatl", Wcid = 20640u,
                    ItemType = 0x100u, ValidLocations = 0x400000u, WieldedAt = null,
                },
            },
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = VisibleNpcGuid, Name = "Society Greeter",
                    Wcid = 700u, ItemType = 0x10u, Distance = 4.0f,
                },
            },
        };
        var events = new EventStream();

        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        // Must not try to wield the colliding atlatl (which would self-disarm).
        Assert.NotEqual(GoalKind.Wield, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_ProposesWield_FirstWeaponIntoEmptySlot()
    {
        // Positive control: with NO weapon wielded, the first unwielded
        // weapon is NOT blocked (empty weapon slot) and the fallback DOES
        // propose Wield for it. The collision guard only suppresses a
        // SECOND weapon while one is already wielded.
        const uint UnwieldedAtlatlGuid = 0x80009C7E;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0xA9B4u,
                CellId = 0xA9B40001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                {
                    Guid = UnwieldedAtlatlGuid, Name = "Royal Atlatl", Wcid = 20640u,
                    ItemType = 0x100u, ValidLocations = 0x400000u, WieldedAt = null,
                },
            },
            Visible = System.Array.Empty<VisibleObjectProjection>(),
        };
        var events = new EventStream();

        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Wield, goal!.Kind);
        Assert.Equal(UnwieldedAtlatlGuid, goal.Item!.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_PrefersPickup_OverOpenable_BothVisible()
    {
        // Step 4 (Pickup) runs BEFORE Step 5b (Openable). A pickup-mask
        // item should win even if a closer openable container is in
        // sight, because Pickups are immediate and Step 4 returns
        // first. Documents the policy's step-order intent.
        const uint AppleGuid  = 0x80004001;
        const uint CorpseGuid = 0x5F000500;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = CorpseGuid, Name = "Corpse of Drudge",
                    Wcid = 22u, ItemType = 0x200u, Distance = 1.0f,
                    IsOpenable = true, IsCorpse = true,
                },
                new VisibleObjectProjection
                {
                    Guid = AppleGuid, Name = "Bruised Apple", Wcid = 5090u,
                    ItemType = 0x20u, Distance = 5.0f,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Pickup, goal!.Kind);
        Assert.Equal(AppleGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_PrefersInvUse_OverOpenable_WhenNewInvAdded()
    {
        // Step 5 (Use newly-acquired-inventory) runs BEFORE Step 5b
        // (Openable). When the bot just picked up an item, that
        // item's Use should fire first so the resulting popup feeds
        // the LLM next deliberation. The openable can wait one tick.
        const uint NewInvGuid = 0x80004002;
        const uint CorpseGuid = 0x5F000600;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                {
                    Guid = NewInvGuid, Name = "Letter From Home",
                    Wcid = 12222u, ItemType = 0x2000u, ValidLocations = 0,
                    ShortDesc = "Double-click to read.",
                },
            },
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = CorpseGuid, Name = "Corpse of Drudge",
                    Wcid = 22u, ItemType = 0x200u, Distance = 1.0f,
                    IsOpenable = true, IsCorpse = true,
                },
            },
        };
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.InventoryItemAdded,
            ItemGuid = NewInvGuid,
            Name = "Letter From Home",
        });
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        // Target must be the newly-acquired inventory item, NOT the corpse.
        Assert.Equal(NewInvGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_PicksNearestOpenable_WhenMultipleVisible()
    {
        // Within the openable set, pure distance wins (no
        // corpse-vs-chest value judgment).
        const uint NearCorpse = 0x5F000700;
        const uint FarChest   = 0x70000800;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = FarChest, Name = "Treasure Chest", Wcid = 13007u,
                    ItemType = 0x200u, Distance = 8.0f,
                    IsOpenable = true, IsChest = true,
                },
                new VisibleObjectProjection
                {
                    Guid = NearCorpse, Name = "Corpse of Drudge", Wcid = 22u,
                    ItemType = 0x200u, Distance = 2.0f,
                    IsOpenable = true, IsCorpse = true,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(NearCorpse, goal.Target.Guid);
    }

    // ---- Step 5c: fallback Use{lifestone} for visible lifestones ----
    //
    // Lifestones have their own wire bit (ObjectDescriptionFlag.LifeStone)
    // distinct from Openable — ACE Lifestone.cs only sets LifeStone in
    // SetEphemeralValues. So step 5b's openable predicate does NOT cover
    // them. Step 5c is the symmetric step for lifestones; same priority
    // (4, no bump), same dedup/rejection filters, same generic action
    // verb (Use). Attuning is a precondition for safe outdoor play (death
    // sends to last attuned lifestone); the bot must be able to attune
    // autonomously without LLM input.

    [Fact]
    public void NoQuestKnowledgePolicy_PicksUse_ForVisibleLifestone()
    {
        const uint LifestoneGuid = 0x80000900;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = LifestoneGuid, Name = "Lifestone", Wcid = 8329u,
                    ItemType = 0x40000u, Distance = 4.5f,
                    IsLifestone = true,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(LifestoneGuid, goal.Target.Guid);
        Assert.Equal("fallback:no-quest-knowledge", goal.Source);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_SkipsLifestone_WhenRecentlyRejected()
    {
        // Same dedup pattern as steps 4/5b/6 — an ActionRejected for
        // the lifestone guid (e.g. server denied because we moved too
        // far during the attune animation) must suppress the candidate.
        const uint LifestoneGuid = 0x80000901;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = LifestoneGuid, Name = "Lifestone", Wcid = 8329u,
                    ItemType = 0x40000u, Distance = 4.5f,
                    IsLifestone = true,
                },
            },
        };
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            Text = "YouHaveMovedTooFar: 'Lifestone'",
            ItemGuid = LifestoneGuid,
            Name = "Lifestone",
            ErrorCode = 0x06,
            ErrorLabel = "YouHaveMovedTooFar",
        });
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Use, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_DoesNotProposeSameLifestoneTwiceInARow()
    {
        // Tight-loop protection via the shared _recentProposedGuids
        // queue. After attune the lifestone stays visible, so without
        // this the bot would spam Use{lifestone} every tick.
        const uint LifestoneGuid = 0x80000902;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = LifestoneGuid, Name = "Lifestone", Wcid = 8329u,
                    ItemType = 0x40000u, Distance = 4.5f,
                    IsLifestone = true,
                },
            },
        };
        var events = new EventStream();

        var first = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(first);
        Assert.Equal(GoalKind.Use, first!.Kind);
        Assert.Equal(LifestoneGuid, first.Target.Guid);

        var second = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(second);
        if (second!.Kind == GoalKind.Use)
            Assert.NotEqual(LifestoneGuid, second.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_PrefersOpenable_OverLifestone_BothVisible()
    {
        // Step 5b (openable) runs BEFORE step 5c (lifestone). Document
        // the step-order intent: when both are visible the openable
        // wins for one tick; the lifestone gets its turn after the
        // openable enters _recentProposedGuids.
        const uint ChestGuid = 0x70000A00;
        const uint LifestoneGuid = 0x80000A01;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = LifestoneGuid, Name = "Lifestone", Wcid = 8329u,
                    ItemType = 0x40000u, Distance = 1.0f,
                    IsLifestone = true,
                },
                new VisibleObjectProjection
                {
                    Guid = ChestGuid, Name = "Treasure Chest", Wcid = 13007u,
                    ItemType = 0x200u, Distance = 10.0f,
                    IsOpenable = true, IsChest = true,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        // Chest wins step 5b even at d=10 vs lifestone at d=1 because
        // step 5b runs first in the early-return chain.
        Assert.Equal(ChestGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_PrefersTalk_OnlyAfterLifestoneIsDeduped()
    {
        // After a lifestone has been Use'd (now in _recentProposedGuids),
        // the next tick should fall through step 5c and pick the NPC
        // via step 6. Validates step 5c does not block downstream
        // steps once its candidate is deduped.
        const uint LifestoneGuid = 0x80000B00;
        const uint NpcGuid = 0x80000B01;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = LifestoneGuid, Name = "Lifestone", Wcid = 8329u,
                    ItemType = 0x40000u, Distance = 4.5f,
                    IsLifestone = true,
                },
                new VisibleObjectProjection
                {
                    Guid = NpcGuid, Name = "Alcott", Wcid = 5091u,
                    ItemType = 0x10u, Distance = 6.0f,
                    IsCreature = true, ObservedHostile = false,
                },
            },
        };
        var events = new EventStream();

        var first = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(first);
        Assert.Equal(GoalKind.Use, first!.Kind);
        Assert.Equal(LifestoneGuid, first.Target.Guid);

        var second = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(second);
        Assert.Equal(GoalKind.Talk, second!.Kind);
        Assert.Equal(NpcGuid, second.Target.Guid);
    }

    // ---- Step 5d: Use any visible Portal (M2 outdoor enabler) ----

    [Fact]
    public void NoQuestKnowledgePolicy_PortalVisible_PicksUseGoal()
    {
        // Mirror of step 5c (lifestone). Wire-bit-only gate: any
        // visible object with IsPortal should drive a Use goal so
        // a fallback-only bot is not trapped inside whichever
        // building it spawned in.
        const uint PortalGuid = 0x80000C00;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = PortalGuid, Name = "Portal", Wcid = 5500u,
                    ItemType = 0x40000u, Distance = 4.5f,
                    IsPortal = true,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(PortalGuid, goal.Target.Guid);
        Assert.Equal("fallback:no-quest-knowledge", goal.Source);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_SkipsPortal_WhenRecentlyRejected()
    {
        // Same dedup pattern as steps 4/5b/5c — an ActionRejected for
        // the portal guid (e.g. "You must have the proper rank to use
        // this portal") must suppress the candidate.
        const uint PortalGuid = 0x80000C01;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = PortalGuid, Name = "Portal", Wcid = 5500u,
                    ItemType = 0x40000u, Distance = 4.5f,
                    IsPortal = true,
                },
            },
        };
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            Text = "YouHaveMovedTooFar: 'Portal'",
            ItemGuid = PortalGuid,
            Name = "Portal",
            ErrorCode = 0x06,
            ErrorLabel = "YouHaveMovedTooFar",
        });
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Use, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_PrefersLifestone_OverPortal_BothVisible()
    {
        // Step 5c (lifestone) runs BEFORE step 5d (portal). Document
        // the step-order intent: when both are visible the lifestone
        // wins for one tick; the portal gets its turn after the
        // lifestone enters _recentProposedGuids. Symmetric to the
        // openable-over-lifestone test for step 5b vs 5c.
        const uint LifestoneGuid = 0x80000C10;
        const uint PortalGuid = 0x80000C11;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = PortalGuid, Name = "Portal", Wcid = 5500u,
                    ItemType = 0x40000u, Distance = 1.0f,
                    IsPortal = true,
                },
                new VisibleObjectProjection
                {
                    Guid = LifestoneGuid, Name = "Lifestone", Wcid = 8329u,
                    ItemType = 0x40000u, Distance = 10.0f,
                    IsLifestone = true,
                },
            },
        };
        var events = new EventStream();
        var goal = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        // Lifestone wins step 5c even at d=10 vs portal at d=1
        // because step 5c runs first in the early-return chain.
        Assert.Equal(LifestoneGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_PrefersTalk_OnlyAfterPortalIsDeduped()
    {
        // After a portal has been Use'd (now in _recentProposedGuids),
        // the next tick should fall through step 5d and pick the NPC
        // via step 6. Validates step 5d does not block downstream
        // steps once its candidate is deduped.
        const uint PortalGuid = 0x80000C20;
        const uint NpcGuid = 0x80000C21;
        var policy = new NoQuestKnowledgePolicy();
        var proj = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x860201ADu, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = PortalGuid, Name = "Portal", Wcid = 5500u,
                    ItemType = 0x40000u, Distance = 4.5f,
                    IsPortal = true,
                },
                new VisibleObjectProjection
                {
                    Guid = NpcGuid, Name = "Alcott", Wcid = 5091u,
                    ItemType = 0x10u, Distance = 6.0f,
                    IsCreature = true, ObservedHostile = false,
                },
            },
        };
        var events = new EventStream();

        var first = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(first);
        Assert.Equal(GoalKind.Use, first!.Kind);
        Assert.Equal(PortalGuid, first.Target.Guid);

        var second = policy.ProposeGoal(proj, events, null);
        Assert.NotNull(second);
        Assert.Equal(GoalKind.Talk, second!.Kind);
        Assert.Equal(NpcGuid, second.Target.Guid);
    }

    // ---- ItemTypeMasks.MeleeWeapon constant ----

    [Fact]
    public void ItemTypeMasks_MeleeWeapon_BitValue()
    {
        // The Asheron's Call wire-protocol bit for
        // ItemType.MeleeWeapon is 0x00000001 (see ACE-bots
        // Source/ACE.Entity/Enum/ItemType.cs). This constant is
        // the precondition gate for GameActionTargetedMeleeAttack
        // (opcode 0x0008) which is the only attack message the
        // driver currently ships.
        Assert.Equal(0x00000001u, ItemTypeMasks.MeleeWeapon);
    }

    // ---- Slice 0: Hunt-intent decomposer ----

    [Fact]
    public void WorldStateProjection_IsMonster_ExcludesCorpse()
    {
        // Slice 0 — corpses inherit Creature + Attackable from their
        // pre-death object record in some captures; IsMonster MUST
        // exclude them or the Hunt decomposer would loop on dead
        // bodies that are already handled by Step 5b (openable Use).
        const uint LivingGuid = 0x80000001;
        const uint CorpseGuid = 0x80000002;

        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SeedSnapshot(ws, SelfGuid, "Headless", wcid: 1u, itemType: 0u, cellId: 0x86020001u);

        // Living monster: Creature + Attackable, no Corpse bit.
        SeedSnapshot(ws, LivingGuid, "Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable,
            weenieFlags: 0x00800036u);
        // Corpse of dead Golem: Creature + Attackable bits still present,
        // PLUS Corpse bit. Must NOT be classified as monster.
        SeedSnapshot(ws, CorpseGuid, "Corpse of Sparring Golem", wcid: 12698u, itemType: 0x10u, cellId: 0x86020001u,
            objectDescriptionFlags: (uint)ObjectDescriptionFlag.Attackable | (uint)ObjectDescriptionFlag.Corpse,
            weenieFlags: 0x00800036u);

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        var byGuid = proj!.Visible.ToDictionary(v => v.Guid);

        Assert.True(byGuid[LivingGuid].IsMonster, "living creature with Attackable bit is a monster");
        Assert.False(byGuid[CorpseGuid].IsMonster, "corpse must NEVER be classified as monster (handled by Step 5b openable)");
        Assert.True(byGuid[CorpseGuid].IsCorpse, "corpse projection sanity check");
    }

    private static WorldStateProjection MakeHuntProjection(
        InventoryItemProjection[] inventory,
        VisibleObjectProjection[] visible,
        int? selfLevel = null) =>
        new()
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u,
                CellId = 0x86020001u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f, Level = selfLevel,
            },
            Inventory = inventory,
            Visible = visible,
        };

    private static HeadlessAcClient.Strategy.Intent.IntentStack MakeStackWithHunt()
    {
        var stack = new HeadlessAcClient.Strategy.Intent.IntentStack();
        var events = new EventStream();
        var proj = MakeHuntProjection(
            Array.Empty<InventoryItemProjection>(),
            Array.Empty<VisibleObjectProjection>());
        var baseline = HeadlessAcClient.Strategy.Intent.IntentBaseline.Capture(
            proj, events, DateTime.UtcNow, stats: null);
        var hunt = new HeadlessAcClient.Strategy.Intent.Intent
        {
            Id = "test-hunt-1",
            Kind = "Hunt",
            Rationale = "test operator push",
            Completion = new HeadlessAcClient.Strategy.Intent.AlwaysFalsePredicate(),
            Baseline = baseline,
            Status = HeadlessAcClient.Strategy.Intent.IntentLifecycle.Active,
        };
        var result = stack.TryPush(hunt);
        Assert.Equal(HeadlessAcClient.Strategy.Intent.StackOpResult.Ok, result);
        return stack;
    }

    [Fact]
    public void NoQuestKnowledgePolicy_NoStack_DoesNotAttackPassiveMonster()
    {
        // Sanity: without an authorised Hunt intent in scope, the
        // fallback MUST NOT initiate combat on a passive monster
        // even with a melee weapon wielded. This is the audit-
        // critical invariant — code never originates strategy.
        const uint GolemGuid = 0x80000010;
        const uint WeaponGuid = 0x80000011;
        var policy = new NoQuestKnowledgePolicy(); // no stack
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0,
                    WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = GolemGuid, Name = "Sparring Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 5f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                    ObservedHostile = false,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        if (goal is not null)
            Assert.NotEqual(GoalKind.Attack, goal.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_PicksAttack_WhenMeleeWielded()
    {
        // With operator-authorised Hunt on the stack + melee weapon
        // wielded + visible monster, the decomposer materialises
        // Attack against the nearest monster.
        const uint GolemNearGuid = 0x80000020;
        const uint GolemFarGuid  = 0x80000021;
        const uint WeaponGuid    = 0x80000022;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0,
                    WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = GolemFarGuid, Name = "Far Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 12f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
                new VisibleObjectProjection
                {
                    Guid = GolemNearGuid, Name = "Near Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 4f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind);
        Assert.Equal(GolemNearGuid, goal.Target.Guid);
        Assert.Contains("Hunt", goal.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_SkipsBeatenKind_AttacksWinnable()
    {
        // The bot's OWN combat-feel record marks "Drudge Skulker" as a repeated
        // loss (died, never killed). Even though it is NEARER, the Hunt
        // decomposer skips it and attacks the winnable "Black Rabbit" instead.
        const uint DrudgeGuid = 0x80000040;
        const uint RabbitGuid = 0x80000041;
        const uint WeaponGuid = 0x80000042;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0, WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = DrudgeGuid, Name = "Drudge Skulker", Wcid = 19257u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
                new VisibleObjectProjection
                {
                    Guid = RabbitGuid, Name = "Black Rabbit", Wcid = 2566u,
                    ItemType = 0x10u, Distance = 9f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            }) with
        {
            CombatHistoryFull = new[]
            {
                new CombatHistoryEntry("Drudge Skulker", 19257u, Kills: 0, Deaths: 4, NearDeaths: 1, Fights: 5, LastOutcome: "death"),
                new CombatHistoryEntry("Black Rabbit", 2566u, Kills: 12, Deaths: 0, NearDeaths: 0, Fights: 12, LastOutcome: "kill"),
            },
        };
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind);
        Assert.Equal(RabbitGuid, goal.Target.Guid); // nearer Drudge skipped (beaten kind)
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_AllBeatenKinds_NoAttack_Explores()
    {
        // Only a beaten kind is visible -> the Hunt decomposer emits NO Attack
        // (don't feed the bot to a kind it loses to) and falls through to Explore.
        const uint DrudgeGuid = 0x80000050;
        const uint WeaponGuid = 0x80000051;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0, WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = DrudgeGuid, Name = "Drudge Skulker", Wcid = 19257u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            }) with
        {
            CombatHistoryFull = new[]
            {
                new CombatHistoryEntry("Drudge Skulker", 19257u, Kills: 0, Deaths: 4, NearDeaths: 0, Fights: 4, LastOutcome: "death"),
            },
        };
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Attack, goal!.Kind); // no attack on a beaten kind -> Explore
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_SameNameVariants_AggregatesAcrossWcids()
    {
        // The wire assigns DIFFERENT wcids to variants that share one display
        // name. Here "Drudge Skulker" has TWO history rows: a loss against the
        // visible wcid (recency-first) AND a win against a sibling wcid. The
        // beaten verdict must AGGREGATE by name (total Kills>0 => NOT beaten),
        // matching the prompt's combat-history rule — NOT short-circuit on the
        // first (loss) row, which would wrongly skip a winnable kind.
        const uint DrudgeGuid    = 0x80000060;
        const uint RabbitGuid    = 0x80000061;
        const uint WeaponGuid    = 0x80000062;
        const uint DrudgeVisWcid = 19257u; // the variant currently in view
        const uint DrudgeSibWcid = 19258u; // a sibling variant, same display name
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0, WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = DrudgeGuid, Name = "Drudge Skulker", Wcid = DrudgeVisWcid,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
                new VisibleObjectProjection
                {
                    Guid = RabbitGuid, Name = "Black Rabbit", Wcid = 2566u,
                    ItemType = 0x10u, Distance = 9f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            }) with
        {
            CombatHistoryFull = new[]
            {
                // Recency-first: a loss against the visible wcid...
                new CombatHistoryEntry("Drudge Skulker", DrudgeVisWcid, Kills: 0, Deaths: 2, NearDeaths: 0, Fights: 2, LastOutcome: "death"),
                // ...but the bot HAS killed a same-name sibling wcid.
                new CombatHistoryEntry("Drudge Skulker", DrudgeSibWcid, Kills: 5, Deaths: 0, NearDeaths: 0, Fights: 5, LastOutcome: "kill"),
            },
        };
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind);
        // Aggregate Kills(0+5)=5 > 0 => name NOT beaten => attack the NEARER drudge,
        // not the farther rabbit (proves aggregation, not first-row short-circuit).
        Assert.Equal(DrudgeGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_RetestsNonLethalLoss_AfterLevelUp()
    {
        // The bot only ever NON-LETHALLY lost to "Drudge Skulker" (near-deaths,
        // never an actual death) at level 3. It is now level 8 — stronger — so
        // the fallback RE-TESTS the kind (adaptive learning) instead of skipping
        // it forever. Only that kind is visible -> it gets attacked.
        const uint DrudgeGuid = 0x80000070;
        const uint WeaponGuid = 0x80000071;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0, WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = DrudgeGuid, Name = "Drudge Skulker", Wcid = 19257u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            },
            selfLevel: 8) with
        {
            CombatHistoryFull = new[]
            {
                new CombatHistoryEntry("Drudge Skulker", 19257u, Kills: 0, Deaths: 0, NearDeaths: 3, Fights: 3, LastOutcome: "near-death", MaxLossBotLevel: 3),
            },
        };
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind); // out-leveled non-lethal loss -> re-test
        Assert.Equal(DrudgeGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_KeepsBeaten_NonLethalLoss_NotYetLeveled()
    {
        // Same non-lethal loss, but the bot is STILL at the loss level (8, not
        // > 8) -> the kind stays beaten and is skipped (no re-test until the bot
        // genuinely out-levels its highest loss).
        const uint DrudgeGuid = 0x80000080;
        const uint WeaponGuid = 0x80000081;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0, WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = DrudgeGuid, Name = "Drudge Skulker", Wcid = 19257u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            },
            selfLevel: 8) with
        {
            CombatHistoryFull = new[]
            {
                new CombatHistoryEntry("Drudge Skulker", 19257u, Kills: 0, Deaths: 0, NearDeaths: 3, Fights: 3, LastOutcome: "near-death", MaxLossBotLevel: 8),
            },
        };
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Attack, goal!.Kind); // not yet out-leveled -> skip
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_KeepsBeaten_DeathKind_EvenAfterLevelUp()
    {
        // A kind that has EVER killed the bot (Deaths>0) stays permanently
        // beaten regardless of level — protects the no-death record (flee
        // reflexes are reactive and cannot stop a first-hit burst). Even though
        // the bot (level 8) has out-leveled the loss (level 3), it is skipped.
        const uint DrudgeGuid = 0x80000090;
        const uint WeaponGuid = 0x80000091;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0, WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = DrudgeGuid, Name = "Drudge Skulker", Wcid = 19257u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            },
            selfLevel: 8) with
        {
            CombatHistoryFull = new[]
            {
                new CombatHistoryEntry("Drudge Skulker", 19257u, Kills: 0, Deaths: 2, NearDeaths: 1, Fights: 3, LastOutcome: "death", MaxLossBotLevel: 3),
            },
        };
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Attack, goal!.Kind); // death-kind stays beaten
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_KeepsBeaten_WhenSelfLevelUnknown()
    {
        // With the bot's level unknown (null) the re-test cannot fire, so a
        // non-lethal beaten kind stays beaten (safe default — never re-feeds on
        // missing data).
        const uint DrudgeGuid = 0x800000A0;
        const uint WeaponGuid = 0x800000A1;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0, WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = DrudgeGuid, Name = "Drudge Skulker", Wcid = 19257u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            },
            selfLevel: null) with
        {
            CombatHistoryFull = new[]
            {
                new CombatHistoryEntry("Drudge Skulker", 19257u, Kills: 0, Deaths: 0, NearDeaths: 3, Fights: 3, LastOutcome: "near-death", MaxLossBotLevel: 3),
            },
        };
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Attack, goal!.Kind); // unknown level -> stay beaten
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_NoAttack_WhenNoMeleeWielded()
    {
        // Hunt intent on stack + monster visible but no melee
        // weapon wielded → decomposer must NOT propose Attack
        // (would fail at the wire — GameActionTargetedMeleeAttack
        // requires a wielded weapon). Falls through to other
        // steps (likely Explore if nothing else applies).
        const uint GolemGuid = 0x80000030;
        const uint WeaponGuid = 0x80000031;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                // Weapon in inventory but UNWIELDED (WieldedAt is null).
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0x18,
                    WieldedAt = null,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = GolemGuid, Name = "Sparring Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 4f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        // Either no goal, OR a non-Attack goal (e.g. Wield, Explore).
        // The bot's other steps may fire — we just verify Hunt-decomposer
        // did not jump straight to Attack with a sheathed weapon.
        if (goal is not null)
            Assert.NotEqual(GoalKind.Attack, goal.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_NoAttack_WhenNoMonsterVisible()
    {
        // Hunt intent on stack + melee wielded but no IsMonster
        // visible (only NPCs / corpses) → no Attack proposed.
        const uint NpcGuid = 0x80000040;
        const uint WeaponGuid = 0x80000041;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0,
                    WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = NpcGuid, Name = "Jonathan", Wcid = 29324u,
                    ItemType = 0x10u, Distance = 4f,
                    IsCreature = true, IsAttackable = false, IsMonster = false,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        if (goal is not null)
            Assert.NotEqual(GoalKind.Attack, goal.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_NoAttack_OnCorpse()
    {
        // Defense-in-depth — even if a Corpse somehow slipped into
        // the projection with IsMonster=true (projection bug), the
        // decomposer's IsCorpse exclusion must catch it. We deliberately
        // construct a contradictory projection (IsMonster=true AND
        // IsCorpse=true) to verify the decomposer's belt-and-braces
        // guard. In live data the projection fix ensures IsMonster=false
        // for any corpse; this test guards the second layer.
        const uint CorpseGuid = 0x80000050;
        const uint WeaponGuid = 0x80000051;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0,
                    WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = CorpseGuid, Name = "Corpse of Sparring Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 2f,
                    IsCreature = true, IsAttackable = true,
                    IsMonster = true, IsCorpse = true,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        if (goal is not null)
            Assert.NotEqual(GoalKind.Attack, goal.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_ObservedHostile_BeatsHuntIntent()
    {
        // A monster that has ALREADY attacked the bot (ObservedHostile)
        // must remain the highest tactical priority. The Hunt decomposer
        // sits below the existing observed-hostile path so a fight-back
        // response isn't deferred for picking the nearest Hunt target.
        // Here both objects exist; the test asserts the observed-hostile
        // one wins regardless of distance to the Hunt candidate.
        const uint HostileGuid = 0x80000060;
        const uint PassiveGuid = 0x80000061;
        const uint WeaponGuid  = 0x80000062;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0,
                    WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                // Passive Hunt-eligible monster (closer).
                new VisibleObjectProjection
                {
                    Guid = PassiveGuid, Name = "Passive Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 2f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                    ObservedHostile = false,
                },
                // Hostile monster (farther — but priority matters more).
                new VisibleObjectProjection
                {
                    Guid = HostileGuid, Name = "Angry Drudge", Wcid = 22u,
                    ItemType = 0x10u, Distance = 8f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                    ObservedHostile = true,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind);
        Assert.Equal(HostileGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntIntent_PickupBeatsHunt_WhenBothEligible()
    {
        // Audit-driven invariant (slice 0 v2): the Hunt-intent
        // decomposer sits at the BOTTOM of the opportunity ladder
        // (priority 2, immediately before bare Explore). When a
        // pickup-eligible item AND a Hunt-eligible monster are both
        // visible on the same tick, Pickup MUST win. This codifies
        // the audit fix that flagged the v1 ordering ("combat during
        // Hunt outranks loot") as hardcoded urgency policy. The
        // policy now encodes only the minimal "decompose intent as
        // a last opportunity" semantics, not any value judgment
        // about combat vs loot.
        const uint LootGuid    = 0x80000070;
        const uint MonsterGuid = 0x80000071;
        const uint WeaponGuid  = 0x80000072;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0,
                    WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                // Pickup-eligible item on the ground (ItemTypeMasks.Pickup bit).
                new VisibleObjectProjection
                {
                    Guid = LootGuid, Name = "Mana Potion", Wcid = 1u,
                    ItemType = ItemTypeMasks.Pickup, Distance = 6f,
                },
                // Hunt-eligible monster, closer.
                new VisibleObjectProjection
                {
                    Guid = MonsterGuid, Name = "Sparring Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                    ObservedHostile = false,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Pickup, goal!.Kind);
        Assert.Equal(LootGuid, goal.Target.Guid);
    }

    // ---- fallback-hunt-skip-civilian-talk ----
    // Under an ACTIVE hunt commitment the fallback must NOT mill among
    // civilian NPCs (Talk steps 6/6b); it should fall through to the
    // hunt-decompose (6c, Attack a visible monster) or Explore egress (7)
    // so an LLM-outage bot still heads out to hunt. Reads only the typed
    // HuntAuthorization.IsActiveHunt label — no content, no urgency.

    private static HeadlessAcClient.Strategy.Intent.IntentStack MakeStackWithHuntKind(string kind)
    {
        var stack = new HeadlessAcClient.Strategy.Intent.IntentStack();
        var events = new EventStream();
        var proj = MakeHuntProjection(
            Array.Empty<InventoryItemProjection>(),
            Array.Empty<VisibleObjectProjection>());
        var baseline = HeadlessAcClient.Strategy.Intent.IntentBaseline.Capture(
            proj, events, DateTime.UtcNow, stats: null);
        var hunt = new HeadlessAcClient.Strategy.Intent.Intent
        {
            Id = "test-hunt-kind",
            Kind = kind,
            Rationale = "test push",
            Completion = new HeadlessAcClient.Strategy.Intent.AlwaysFalsePredicate(),
            Baseline = baseline,
            Status = HeadlessAcClient.Strategy.Intent.IntentLifecycle.Active,
        };
        Assert.Equal(HeadlessAcClient.Strategy.Intent.StackOpResult.Ok, stack.TryPush(hunt));
        return stack;
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntActive_SkipsCivilianTalk_ChoosesExplore()
    {
        // Active Hunt + only civilian NPCs in view (no monster, no melee):
        // the fallback must skip Talk and fall through to Explore egress.
        const uint NpcAGuid = 0x80000080;
        const uint NpcBGuid = 0x80000081;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: Array.Empty<InventoryItemProjection>(),
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = NpcAGuid, Name = "Pathwarden Thorolf", Wcid = 1u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsMonster = false, ObservedHostile = false,
                },
                new VisibleObjectProjection
                {
                    Guid = NpcBGuid, Name = "Alcott", Wcid = 2u,
                    ItemType = 0x10u, Distance = 6f,
                    IsCreature = true, IsMonster = false, ObservedHostile = false,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Explore, goal!.Kind);
        Assert.NotEqual(GoalKind.Talk, goal.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntActive_StillAttacksVisibleMonster_NotTalk()
    {
        // Active Hunt + a civilian NPC AND a visible monster + melee
        // wielded: the suppressed Talk must not block the decomposer —
        // the bot attacks the monster, not chats the NPC.
        const uint NpcGuid     = 0x80000082;
        const uint MonsterGuid = 0x80000083;
        const uint WeaponGuid  = 0x80000084;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0,
                    WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = NpcGuid, Name = "Pathwarden Thorolf", Wcid = 1u,
                    ItemType = 0x10u, Distance = 2f,
                    IsCreature = true, IsMonster = false, ObservedHostile = false,
                },
                new VisibleObjectProjection
                {
                    Guid = MonsterGuid, Name = "Sparring Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 5f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind);
        Assert.Equal(MonsterGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntExcursionActive_SkipsCivilianTalk()
    {
        // The broadened predicate: an LLM-authored "hunt-excursion" (not
        // just operator "Hunt") also suppresses civilian Talk.
        const uint NpcGuid = 0x80000085;
        var stack = MakeStackWithHuntKind("hunt-excursion");
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: Array.Empty<InventoryItemProjection>(),
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = NpcGuid, Name = "Alcott", Wcid = 2u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsMonster = false, ObservedHostile = false,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Explore, goal!.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_NoHunt_StillTalksToNpc()
    {
        // Regression guard: with NO hunt commitment in scope (e.g. academy
        // onboarding), the civilian Talk step is UNCHANGED — the bot still
        // talks to a nearby NPC to surface dialog.
        const uint NpcGuid = 0x80000086;
        var policy = new NoQuestKnowledgePolicy(); // no stack -> no hunt
        var proj = MakeHuntProjection(
            inventory: Array.Empty<InventoryItemProjection>(),
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = NpcGuid, Name = "Jonathan", Wcid = 29324u,
                    ItemType = 0x10u, Distance = 3f,
                    IsCreature = true, IsMonster = false, ObservedHostile = false,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Talk, goal!.Kind);
        Assert.Equal(NpcGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntActive_SkipsFarOpenable_ChoosesExplore()
    {
        // Active Hunt + only a FAR openable (decorative chest) in view:
        // the fallback must NOT detour to it (off-intent), and with no
        // monster to decompose to, it falls through to Explore egress.
        // This is the cp-2308 fix for the chest-touring milling observed
        // when the LLM was 429-rate-limited under AC_BOTS_INITIAL_INTENT=Hunt.
        const uint ChestGuid = 0x7A9B4002;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: Array.Empty<InventoryItemProjection>(),
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = ChestGuid, Name = "Chest", Wcid = 143u,
                    ItemType = 0x200u, Distance = 31f,
                    IsOpenable = true, IsChest = true, IsCorpse = false,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Explore, goal!.Kind);
        Assert.NotEqual(GoalKind.Use, goal.Kind);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntActive_StillUsesAdjacentOpenable()
    {
        // Active Hunt + an ADJACENT openable (the corpse of a kill we just
        // made, within the no-detour radius): the fallback must STILL Use
        // it so own-kill loot is not lost during an outage hunt. The gate
        // is geometry-only — corpses and chests are treated identically;
        // only proximity matters.
        const uint CorpseGuid = 0x7A9B4003;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: Array.Empty<InventoryItemProjection>(),
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = CorpseGuid, Name = "Corpse of Drudge", Wcid = 21u,
                    ItemType = 0x200u, Distance = 3f,
                    IsOpenable = true, IsCorpse = true, IsChest = false,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(CorpseGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_NoHunt_StillUsesFarOpenable()
    {
        // Regression guard: with NO hunt commitment in scope, the
        // openable-Use step is UNCHANGED — a far chest is still Used
        // (the no-detour gate only applies while a hunt is active).
        const uint ChestGuid = 0x7A9B4004;
        var policy = new NoQuestKnowledgePolicy(); // no stack -> no hunt
        var proj = MakeHuntProjection(
            inventory: Array.Empty<InventoryItemProjection>(),
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = ChestGuid, Name = "Chest", Wcid = 143u,
                    ItemType = 0x200u, Distance = 31f,
                    IsOpenable = true, IsChest = true, IsCorpse = false,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Use, goal!.Kind);
        Assert.Equal(ChestGuid, goal.Target.Guid);
    }

    [Fact]
    public void NoQuestKnowledgePolicy_HuntActive_FarOpenablePresent_AttacksVisibleMonster()
    {
        // Active Hunt + a FAR openable AND a visible monster + melee
        // wielded: with the far openable suppressed, the decomposer must
        // win — the bot attacks the monster instead of touring the chest.
        const uint ChestGuid   = 0x7A9B4005;
        const uint MonsterGuid = 0x80000090;
        const uint WeaponGuid  = 0x80000091;
        var stack = MakeStackWithHunt();
        var policy = new NoQuestKnowledgePolicy(stack);
        var proj = MakeHuntProjection(
            inventory: new[]
            {
                new InventoryItemProjection
                {
                    Guid = WeaponGuid, Name = "Training Spadone", Wcid = 31u,
                    ItemType = ItemTypeMasks.MeleeWeapon, ValidLocations = 0,
                    WieldedAt = 0x18,
                },
            },
            visible: new[]
            {
                new VisibleObjectProjection
                {
                    Guid = ChestGuid, Name = "Chest", Wcid = 143u,
                    ItemType = 0x200u, Distance = 31f,
                    IsOpenable = true, IsChest = true, IsCorpse = false,
                },
                new VisibleObjectProjection
                {
                    Guid = MonsterGuid, Name = "Sparring Golem", Wcid = 12698u,
                    ItemType = 0x10u, Distance = 5f,
                    IsCreature = true, IsAttackable = true, IsMonster = true,
                },
            });
        var goal = policy.ProposeGoal(proj, new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind);
        Assert.Equal(MonsterGuid, goal.Target.Guid);
    }
}

/// <summary>
/// Test seam: seed WorldObjectSnapshots into a WorldState without
/// running the wire-protocol Apply path. Lives in the test
/// assembly which has InternalsVisibleTo and can therefore mutate
/// the snapshot's internal setters directly. We attach the
/// snapshot to the dictionary via a private accessor: WorldState
/// exposes IReadOnlyDictionary Objects, so we go through the
/// underlying field via reflection ONCE, here.
/// </summary>
internal static class SnapshotSeeding
{
    public static void Seed(
        WorldState ws,
        uint guid,
        string name,
        uint wcid,
        uint itemType,
        uint cellId,
        uint? containerGuid,
        uint? objectDescriptionFlags = null,
        uint? weenieFlags = null,
        Vector3? position = null,
        uint? physicsState = null)
    {
        var snap = new WorldObjectSnapshot(guid)
        {
            Name = name,
            WeenieClassId = wcid,
            ItemType = itemType,
            CellId = cellId,
            ContainerGuid = containerGuid,
            ObjectDescriptionFlags = objectDescriptionFlags,
            WeenieFlags = weenieFlags,
            PhysicsState = physicsState,
            Position = position ?? new Vector3(0, 0, 0),
        };
        // Reach the underlying dictionary via reflection. The field
        // is named `_objects` (LinkedList-style private backing).
        var f = typeof(WorldState).GetField("_objects",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WorldState._objects field not found");
        var dict = (System.Collections.Generic.Dictionary<uint, WorldObjectSnapshot>)f.GetValue(ws)!;
        dict[guid] = snap;
    }
}
