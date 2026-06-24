// SPDX-License-Identifier: AGPL-3.0-or-later
// return-to-contract-source (cp035 + cp037): a relevance-gated nudge that, when the
// bot holds a FINISHED contract batch (every tracked contract DONE, stage 3) and NO
// contract source (vendor / un-talked npc) is in Visible nearby, surfaces the option
// to TRAVEL back toward the populated area the batch came from to find a fresh source
// — instead of grinding monsters that carry the bot further from any source. Two
// mutually-exclusive anchor branches share one marker: when a contract carries a dat
// location, point at the rendered BEARING (cp035); when none does (a coordless
// objective), point at the contract's turn-in / start NPC NAME (cp037) via the
// existing Explore-toward-name machinery. Navigation only, never a re-Talk of a
// settled turn-in NPC. The FIND-A-KILL-TASK-SOURCE rule (source IN VIEW) is the
// complement.

using System;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class ReturnToContractSourceTests
{
    private const uint SelfGuid = 0x5000000D;
    private const string Marker = "RETURN TO A CONTRACT SOURCE";
    // Branch-distinguishing substrings (the nudge shares one Marker but adapts):
    private const string BearingMarker = "bearing listed with your contracts below";
    private const string NameMarker = "no contract shows a travel";

    private static WorldStateProjection World(
        uint[] contractStages, bool withBearing,
        bool npcVisible = false, bool vendorVisible = false,
        bool vendorPanelOpen = false, bool monsterVisible = false,
        bool selfCellKnown = true, bool coordsOnFirstContract = true,
        bool nameOnFirstContract = true, bool coordsOnlyOnLast = false,
        bool padDescriptions = false, bool armed = true)
    {
        var visible = new System.Collections.Generic.List<VisibleObjectProjection>();
        if (npcVisible)
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9003u, Name = "Townsperson", IsCreature = true, IsMonster = false,
                Distance = 9f,
            });
        if (vendorVisible)
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9001u, Name = "Provisioner", IsVendor = true, Distance = 10f,
            });
        if (monsterVisible)
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9002u, Name = "Drudge", IsMonster = true, IsCreature = true,
                IsAttackable = true, Distance = 8f,
            });

        var contracts = new ContractProjection[contractStages.Length];
        for (int i = 0; i < contracts.Length; i++)
        {
            // Coords present when withBearing; coordsOnFirstContract lets a test
            // give coords only to a LATER contract (first row lacks them) to
            // exercise the conservative first-row gate. nameOnFirstContract does
            // the same for the turn-in NPC name (cp037's coordless anchor).
            // coordsOnlyOnLast + padDescriptions build a batch whose ONLY coords
            // sit on a row the capsule char budget DROPS, so no bearing renders.
            var isLast = i == contracts.Length - 1;
            var hasCoords = coordsOnlyOnLast
                ? isLast
                : withBearing && (i == 0 ? coordsOnFirstContract : true);
            var hasName = i == 0 ? nameOnFirstContract : true;
            contracts[i] = new ContractProjection
            {
                ContractId = (uint)(i + 1),
                Stage = contractStages[i],
                NpcEnd = hasName ? "Buckminster" : null,
                // A long objective inflates each rendered row so the protected char
                // budget drops later rows (exercises the rendered-bearing gate).
                Description = padDescriptions
                    ? new string('x', 200) + " objective text for budget padding"
                    : null,
                TurnInWorldX = hasCoords ? 2000f : (float?)null,
                TurnInWorldY = hasCoords ? 3000f : (float?)null,
            };
        }

        return new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u,
                CellId = selfCellKnown ? 0xAAB50003u : (uint?)null,
                PositionX = 1f, PositionY = 2f, PositionZ = 3f, HealthFraction = 1.0f,
            },
            Inventory = armed
                ? new[] { new InventoryItemProjection { Guid = 0x7E1u, Name = "Spadone", Wcid = 1u, ItemType = 0x1u, WieldedAt = 0x02000000u } }
                : Array.Empty<InventoryItemProjection>(),
            Visible = visible,
            Vendor = vendorPanelOpen ? new VendorProjection { VendorGuid = 0x9001u } : null,
            Contracts = contracts,
        };
    }

    private static string Prompt(WorldStateProjection w) =>
        LlmGoalPolicy.BuildUserPrompt(w, new EventStream(), null);

    [Fact]
    public void Present_WhenDoneBatch_NoSourceInView_WithBearing()
    {
        // Finished batch, nothing in view, a contract has a dat bearing -> nudge
        // via the BEARING branch (cp035): point at the rendered compass bearing.
        // The world here has the first contract carrying BOTH coords and a name, so
        // the absence of NameMarker also proves the bearing branch WINS when both
        // anchors are present (the if/else mutual-exclusivity precedence).
        var p = Prompt(World(new uint[] { 3u, 3u }, withBearing: true));
        Assert.Contains(Marker, p);
        Assert.Contains(BearingMarker, p);
        Assert.DoesNotContain(NameMarker, p);
    }

    [Fact]
    public void Present_EvenWithMonsterInView()
    {
        // A monster in view must NOT suppress the nudge: the whole point is to
        // pull the bot back toward a source rather than grind it further away.
        Assert.Contains(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, monsterVisible: true)));
    }

    [Fact]
    public void Absent_WhenUnarmed()
    {
        // Combat-readiness gate: when UNARMED, traveling back to a contract source
        // competes with the SELF-ARM loot-to-arm hunt, so the nudge is suppressed
        // until the bot is armed. Same finished-batch/no-source scene that fires it
        // when armed stays silent unarmed.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, armed: false)));
    }

    [Fact]
    public void Absent_WhenUntalkedNpcInView()
    {
        // A source IS in view -> the FIND-A-KILL-TASK-SOURCE rule owns it; the
        // return-travel nudge stays off (no point navigating away to find a
        // source that is right here).
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, npcVisible: true)));
    }

    [Fact]
    public void Absent_WhenVendorInView_PanelClosed()
    {
        // An unbrowsed vendor in view is also a source in view -> nudge off.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, vendorVisible: true)));
    }

    [Fact]
    public void Absent_WhenVendorInView_PanelOpen()
    {
        // A vendor in view is a reachable source (even mid-browse with the panel
        // open) -> do NOT travel away from it; the nudge stays off and the bot
        // engages/reads it via ## Vendor offerings.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true,
                vendorVisible: true, vendorPanelOpen: true)));
    }

    [Fact]
    public void Absent_WhenBatchNotAllDone()
    {
        // One contract still in progress (stage 2) -> not a finished batch -> off.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 2u }, withBearing: true)));
    }

    [Fact]
    public void Absent_WhenNoContracts()
    {
        Assert.DoesNotContain(Marker, Prompt(World(Array.Empty<uint>(), withBearing: true)));
    }

    [Fact]
    public void Present_WhenNoBearing_TurnInNameIsAnchor()
    {
        // cp037: done batch, no source in view, NO contract carries a dat location
        // (the bearing branch is dormant) -> the first contract's turn-in/start NPC
        // NAME is still a navigate-back anchor, so the nudge fires via the NAME
        // branch and points the bot to Explore toward that NPC's area.
        var p = Prompt(World(new uint[] { 3u, 3u }, withBearing: false));
        Assert.Contains(Marker, p);
        Assert.Contains(NameMarker, p);
        Assert.DoesNotContain(BearingMarker, p);
    }

    [Fact]
    public void NameBranch_CarriesReTalkProhibition()
    {
        // The name branch NAMES the turn-in NPC as an Explore target, so (unlike
        // cp035's bare-compass bearing branch) it MUST carry the same explicit
        // "do NOT re-Talk a done contract's settled turn-in NPC" guard the bearing
        // branch has -- otherwise, once the bot Explores to the now-visible (and
        // already-talked) turn-in NPC, nothing stands against a re-Talk hand-in loop.
        var p = Prompt(World(new uint[] { 3u, 3u }, withBearing: false));
        Assert.Contains(NameMarker, p);
        Assert.Contains("do NOT re-`Talk` a done contract's settled turn-in NPC", p);
    }

    [Fact]
    public void Absent_WhenSelfPositionUnknown()
    {
        // Coords exist but the bot's own position is unknown, so ## Contracts can
        // render NO bearing to copy AND the motor cannot travel toward a target
        // when the bot does not know where it is -> BOTH branches (bearing and
        // cp037 name) require self-position known, so the nudge stays off.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, selfCellKnown: false)));
    }

    [Fact]
    public void Absent_WhenFirstContractHasNeitherCoordsNorName()
    {
        // Conservative gate: only the FIRST contract row is GUARANTEED rendered (a
        // later row can be dropped by the contracts char budget). When the first
        // contract carries NEITHER a dat location NOR a turn-in/start NPC name --
        // even though a LATER contract has both -- both branches stay OFF rather
        // than risk pointing at a bearing/name the budget could drop.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true,
                coordsOnFirstContract: false, nameOnFirstContract: false)));
    }

    [Fact]
    public void Absent_WhenFirstHasNameButLaterContractRendersBearing()
    {
        // gpt-5.4 review: the name fallback must NOT fire (and must NOT assert "no
        // contract shows a travel bearing") when a LATER contract still RENDERS a
        // dat bearing. A precise compass bearing is a better anchor than a name. Here
        // both contracts render (a 2-row batch fits the budget), so the second row's
        // bearing is visible -> AnyRenderedContractBearing is true -> the name branch
        // stays off; cp035's bearing branch also stays off (it gates on the FIRST
        // row, which lacks coords), so the nudge is absent, not a false "no bearing".
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, coordsOnFirstContract: false)));
    }

    [Fact]
    public void Present_WhenLaterCoordsRowIsBudgetDropped_NameAnchorFires()
    {
        // gpt-5.4 review (precision): gate on RENDERED bearings, not raw coords. Here
        // a long-objective batch overflows the ## Contracts char budget so only the
        // first few (coordless, name-only) rows render and the ONLY coords-bearing
        // row (the last) is DROPPED -> NO bearing is visible to copy. The name branch
        // MUST fire (the turn-in NPC name is the only visible anchor); a raw-coords
        // gate would have wrongly suppressed it.
        var p = Prompt(World(new uint[] { 3u, 3u, 3u, 3u, 3u, 3u }, withBearing: false,
            coordsOnlyOnLast: true, padDescriptions: true));
        Assert.Contains(Marker, p);
        Assert.Contains(NameMarker, p);
        Assert.DoesNotContain(BearingMarker, p);
    }

    [Fact]
    public void Absent_WhenNoBearingAndNoName()
    {
        // Done batch, no source in view, the first contract has no coords AND no
        // name -> there is no anchor to head toward at all, so the nudge stays off.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: false, nameOnFirstContract: false)));
    }
}
