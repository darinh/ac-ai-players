// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the IsPlayer projection + its `player` render tag.
//
// A PLAYER (human/bot character) is classified purely by the AC player guid
// band 0x50000001..0x5FFFFFFF (mirrors ACE-bots ObjectGuid.IsPlayer), distinct
// from static NPCs/monsters (0x70000000..) and dynamic items (0x80000000..).
// The Visible-nearby render marks such a creature `player` so the LLM can tell
// a fellow player apart from an NPC/monster (e.g. to team up).

using System.Linq;
using System.Numerics;
using System.Text;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class VisibleIsPlayerTests
{
    private const uint ItemTypeCreature = 0x00000010u;

    [Fact]
    public void FromWorldState_OtherPlayer_AppearsInVisibleTaggedPlayer()
    {
        // Integration: another player must NOT be filtered out of world.Visible
        // (the old "skip other players" projection filter blocked the IsPlayer
        // tag from ever reaching the prompt). Only the bot's OWN object is excluded.
        const uint selfGuid = 0x500000E6u;        // the bot (a player)
        const uint otherPlayerGuid = 0x500000A1u; // another player
        const uint cell = 0xA9B40003u;
        var ws = new WorldState();
        ws.SetSelf(selfGuid);
        SnapshotSeeding.Seed(ws, selfGuid, "Headless", 1u, ItemTypeCreature, cell, null,
            position: new Vector3(5f, 5f, 0f));
        SnapshotSeeding.Seed(ws, otherPlayerGuid, "Otherbot", 1u, ItemTypeCreature, cell, null,
            position: new Vector3(7f, 5f, 0f));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        Assert.NotNull(proj);
        var other = proj!.Visible.FirstOrDefault(v => v.Guid == otherPlayerGuid);
        Assert.NotNull(other);                 // NOT filtered out
        Assert.True(other!.IsPlayer);          // tagged player by guid band
        Assert.True(other.IsCreature);
        Assert.DoesNotContain(proj.Visible, v => v.Guid == selfGuid); // self still excluded
    }

    [Fact]
    public void FromWorldState_OtherPlayer_WithAttackableFlags_NotClassifiedMonster()
    {
        // A player carries the Creature+Attackable wire bits a monster has (the
        // classifier sees only flags, not the guid). Without the guid-band guard the
        // player is classified IsMonster -> rendered in "Monsters in view" -> a hunting
        // LLM attacks it (live multi-bot evidence). The projection must classify a
        // player OUT of IsMonster (it stays a `player`; the LLM may still explicitly
        // Attack it by name/guid).
        const uint selfGuid = 0x500000E6u;
        const uint otherPlayerGuid = 0x500000A1u;
        const uint cell = 0xA9B40003u;
        const uint attackable = 0x00000010u; // ObjectDescriptionFlag.Attackable
        var ws = new WorldState();
        ws.SetSelf(selfGuid);
        SnapshotSeeding.Seed(ws, selfGuid, "Headless", 1u, ItemTypeCreature, cell, null,
            position: new Vector3(5f, 5f, 0f));
        SnapshotSeeding.Seed(ws, otherPlayerGuid, "Otherbot", 1u, ItemTypeCreature, cell, null,
            objectDescriptionFlags: attackable, position: new Vector3(7f, 5f, 0f));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        var other = proj!.Visible.FirstOrDefault(v => v.Guid == otherPlayerGuid);
        Assert.NotNull(other);
        Assert.True(other!.IsPlayer);
        Assert.False(other.IsMonster); // NOT a huntable monster
    }

    [Fact]
    public void FromWorldState_RealMonster_WithAttackableFlags_StillMonster()
    {
        // Regression guard: the player exclusion must not suppress a real monster.
        // A non-player (dynamic band) Creature+Attackable still classifies IsMonster.
        const uint selfGuid = 0x500000E6u;
        const uint monsterGuid = 0x80005D89u; // dynamic band, NOT a player
        const uint cell = 0xA9B40003u;
        const uint attackable = 0x00000010u;
        var ws = new WorldState();
        ws.SetSelf(selfGuid);
        SnapshotSeeding.Seed(ws, selfGuid, "Headless", 1u, ItemTypeCreature, cell, null,
            position: new Vector3(5f, 5f, 0f));
        SnapshotSeeding.Seed(ws, monsterGuid, "Cow", 1u, ItemTypeCreature, cell, null,
            objectDescriptionFlags: attackable, position: new Vector3(7f, 5f, 0f));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        var mob = proj!.Visible.FirstOrDefault(v => v.Guid == monsterGuid);
        Assert.NotNull(mob);
        Assert.False(mob!.IsPlayer);
        Assert.True(mob.IsMonster); // still a monster
    }

    [Theory]
    [InlineData(0x50000000u, false)] // one below PlayerMin
    [InlineData(0x50000001u, true)]  // PlayerMin
    [InlineData(0x500000E6u, true)]  // a real player guid
    [InlineData(0x5FFFFFFFu, true)]  // PlayerMax
    [InlineData(0x60000000u, false)] // above PlayerMax
    [InlineData(0x7A9B4022u, false)] // a static NPC/object
    [InlineData(0x80005D89u, false)] // a dynamic monster/item
    public void IsPlayerGuid_ClassifiesByBand(uint guid, bool expected)
    {
        Assert.Equal(expected, WorldStateProjection.IsPlayerGuid(guid));
    }

    [Fact]
    public void Render_PlayerCreature_TaggedPlayerNotNpc()
    {
        var sb = new StringBuilder();
        var visible = new[]
        {
            new VisibleObjectProjection
            { Guid = 0x500000A1u, Name = "Otherbot", IsCreature = true, IsPlayer = true, Distance = 5f },
        };
        LlmGoalPolicy.AppendVisibleNearby(sb, visible);
        var s = sb.ToString();
        Assert.Contains("Otherbot", s);
        Assert.Contains(" player", s);
        Assert.DoesNotContain(" npc", s);
    }

    [Fact]
    public void Render_Monster_StillTaggedMonster()
    {
        var sb = new StringBuilder();
        var visible = new[]
        {
            new VisibleObjectProjection
            { Guid = 0x80005D89u, Name = "Cow", IsCreature = true, IsMonster = true, IsPlayer = false, Distance = 5f },
        };
        LlmGoalPolicy.AppendVisibleNearby(sb, visible);
        Assert.Contains(" monster", sb.ToString());
    }

    [Fact]
    public void Render_PlainCreature_StillTaggedNpc()
    {
        var sb = new StringBuilder();
        var visible = new[]
        {
            new VisibleObjectProjection
            { Guid = 0x7A9B4022u, Name = "Townsperson", IsCreature = true, IsMonster = false, IsPlayer = false, Distance = 5f },
        };
        LlmGoalPolicy.AppendVisibleNearby(sb, visible);
        Assert.Contains(" npc", sb.ToString());
    }
}
