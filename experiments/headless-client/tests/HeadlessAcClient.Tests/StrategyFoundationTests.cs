// SPDX-License-Identifier: AGPL-3.0-or-later
// Strategy + Tactics Slice A foundation tests.

using System;
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
        uint? weenieFlags = null)
    {
        // WorldState lacks a public seed helper. The most direct path
        // without reflection is to mutate via internal setters on a
        // freshly-created snapshot, then attach it via the internal
        // GetOrCreate path. Since WorldState has no public Add, we
        // mimic an ObjectCreate using the public API: a minimal
        // ObjectCreateMessage would require a fixture file. Instead,
        // we use the SnapshotSeeding test seam (defined below).
        SnapshotSeeding.Seed(ws, guid, name, wcid, itemType, cellId, containerGuid, objectDescriptionFlags, weenieFlags);
    }

    private sealed class FakeWeenieRepo : IWeenieRepository
    {
        private readonly System.Collections.Generic.Dictionary<uint, WeenieStringRecord> _map = new();
        public void Seed(uint wcid, string? name, string? shortDesc, string? longDesc = null)
            => _map[wcid] = new WeenieStringRecord(wcid, name, shortDesc, longDesc);
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
        uint? weenieFlags = null)
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
            Position = new Vector3(0, 0, 0),
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
