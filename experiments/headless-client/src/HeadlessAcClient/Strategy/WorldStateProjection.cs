// SPDX-License-Identifier: AGPL-3.0-or-later
// WorldStateProjection — distilled view of WorldState passed to
// the Strategy (LLM) and used by Tactics for selector resolution.
//
// Why a projection instead of the raw WorldState:
//   - WorldState carries 90+ snapshots in a populated Holtburg
//     landblock. Most are irrelevant to the next decision (other
//     bots, distant scenery). The LLM call cost would balloon.
//   - The LLM only needs: inventory contents, visible
//     NPCs/interactables within search radius, player state,
//     recent events. This projection extracts that.
//   - Tactics resolves selectors against the projection's
//     Objects list (a filtered view) rather than the raw map, so
//     both layers see the same "what the bot can act on now"
//     subset.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;

using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

internal sealed record InventoryItemProjection
{
    [JsonPropertyName("guid")]      public required uint Guid { get; init; }
    [JsonPropertyName("name")]      public required string Name { get; init; }
    [JsonPropertyName("wcid")]      public required uint Wcid { get; init; }
    [JsonPropertyName("item_type")] public uint? ItemType { get; init; }
    [JsonPropertyName("valid_locations")] public uint? ValidLocations { get; init; }
    [JsonPropertyName("wielded_at")] public uint? WieldedAt { get; init; }

    /// <summary>
    /// Sourced from WeenieRepository (MariaDB ace_world); the wire
    /// protocol does not deliver this. Null if unknown / not yet
    /// looked up. THIS is the field the LLM uses to derive quest
    /// intent ("Give this token to Jonathan...").
    /// </summary>
    [JsonPropertyName("short_desc")] public string? ShortDesc { get; init; }
    [JsonPropertyName("long_desc")]  public string? LongDesc { get; init; }
}

internal sealed record VisibleObjectProjection
{
    [JsonPropertyName("guid")]      public required uint Guid { get; init; }
    [JsonPropertyName("name")]      public required string Name { get; init; }
    [JsonPropertyName("wcid")]      public uint? Wcid { get; init; }
    [JsonPropertyName("item_type")] public uint? ItemType { get; init; }
    [JsonPropertyName("distance")]  public float? Distance { get; init; }

    [JsonPropertyName("cell")]      public uint? CellId { get; init; }

    /// <summary>True if ItemType has Creature bit AND object is not us.</summary>
    [JsonPropertyName("is_creature")] public bool IsCreature { get; init; }

    /// <summary>True if ObjectDescriptionFlag has Portal bit (0x40000).</summary>
    [JsonPropertyName("is_portal")] public bool IsPortal { get; init; }

    /// <summary>True if ObjectDescriptionFlag has Door bit (0x1000).</summary>
    [JsonPropertyName("is_door")] public bool IsDoor { get; init; }

    /// <summary>True if ObjectDescriptionFlag has Corpse bit (0x2000).</summary>
    [JsonPropertyName("is_corpse")] public bool IsCorpse { get; init; }

    /// <summary>True if ObjectDescriptionFlag has LifeStone bit (0x4000).</summary>
    [JsonPropertyName("is_lifestone")] public bool IsLifestone { get; init; }

    /// <summary>True if ObjectDescriptionFlag has Vendor bit (0x200).</summary>
    [JsonPropertyName("is_vendor")] public bool IsVendor { get; init; }

    /// <summary>True if ObjectDescriptionFlag has Healer bit (0x10000).</summary>
    [JsonPropertyName("is_healer")] public bool IsHealer { get; init; }

    /// <summary>True if ObjectDescriptionFlag has Openable bit (0x1). Doors, chests, etc.</summary>
    [JsonPropertyName("is_openable")] public bool IsOpenable { get; init; }

    /// <summary>
    /// Slice U — true if this is a non-corpse, non-door openable Container
    /// (treasure chest, bookshelf, coffer, lockbox). Composite of
    /// ItemType.Container (0x200) AND ObjectDescriptionFlag.Openable (0x1)
    /// AND NOT corpse / door. Surfaced so the LLM can prioritise loot
    /// without learning the difference between an itemType bit and a
    /// description-flag bit. Pure wire-protocol — no English-name matching.
    /// </summary>
    [JsonPropertyName("is_chest")] public bool IsChest { get; init; }

    /// <summary>
    /// Slice U — true if this is a pickup-able Writable (a book on a
    /// table, a scroll on the floor). ItemType.Writable (0x2000) AND NOT
    /// ObjectDescriptionFlag.Stuck (0x4). Surfaced so the LLM knows to
    /// emit Pickup instead of Use.
    /// </summary>
    [JsonPropertyName("is_book")] public bool IsBook { get; init; }

    /// <summary>
    /// Slice U — true if this is a fixed Writable (a wall sign, a
    /// bolted-down plaque). ItemType.Writable (0x2000) AND
    /// ObjectDescriptionFlag.Stuck (0x4). Use-only; cannot be picked up.
    /// </summary>
    [JsonPropertyName("is_sign")] public bool IsSign { get; init; }

    /// <summary>True if observed-hostile (e.g. server-message indicated initial attack on us).</summary>
    [JsonPropertyName("observed_hostile")] public bool ObservedHostile { get; init; }

    /// <summary>
    /// True if ObjectDescriptionFlag has Attackable bit (0x10). Server-
    /// asserted "this thing can be attacked by you". Set on training
    /// dummies and most monsters; usually also set on PK-flagged civilians.
    /// On its own NOT enough to classify a target as a monster — use
    /// IsMonster (composite predicate) for the friend/foe decision.
    /// </summary>
    [JsonPropertyName("is_attackable")] public bool IsAttackable { get; init; }

    /// <summary>
    /// True if WeenieHeaderFlag has RadarBlipColor bit (0x100000).
    /// Friendly NPCs (Greeter, Pathwarden, vendors, healers) get an
    /// explicit minimap blip color and have this set. Generic monsters
    /// rely on default minimap rendering and do NOT have this set.
    /// Live observation: NPC wFlags=0x00900036 includes 0x100000;
    /// Sparring Golem wFlags=0x00800036 does not.
    /// </summary>
    [JsonPropertyName("has_radar_blip_color")] public bool HasRadarBlipColor { get; init; }

    /// <summary>
    /// Composite friend/foe classification, derived per Slice H. True iff:
    ///   - object is a creature (ItemType has Creature bit), AND
    ///   - server flagged it Attackable, AND
    ///   - it has NO custom radar blip color (NPCs do, generic monsters don't), AND
    ///   - it is not a Vendor or Healer (those are special-purpose NPCs).
    /// Both signals are server-provided; this is NOT hardcoded knowledge
    /// about specific creatures. Surfaced as the `monster` tag in the
    /// LLM prompt's Visible nearby section so the LLM can pick targets
    /// for combat without misfiring on civilians.
    /// </summary>
    [JsonPropertyName("is_monster")] public bool IsMonster { get; init; }
}

internal sealed record SelfProjection
{
    [JsonPropertyName("guid")]    public required uint Guid { get; init; }
    [JsonPropertyName("name")]    public string? Name { get; init; }
    [JsonPropertyName("landblock")] public uint? Landblock { get; init; }
    [JsonPropertyName("cell")]    public uint? CellId { get; init; }
    [JsonPropertyName("position_x")] public float PositionX { get; init; }
    [JsonPropertyName("position_y")] public float PositionY { get; init; }
    [JsonPropertyName("position_z")] public float PositionZ { get; init; }
    [JsonPropertyName("level")]   public int? Level { get; init; }
    [JsonPropertyName("health_fraction")] public float? HealthFraction { get; init; }

    // Server-authoritative counters (read directly from PropertyInts;
    // ACE pushes these on character-load + on every change).
    /// <summary>PropertyInt.NumDeaths (43). Total deaths this character has ever suffered. Persists across sessions.</summary>
    [JsonPropertyName("num_deaths")] public int? NumDeaths { get; init; }

    /// <summary>PropertyInt.CoinValue (20). Pyreals in inventory (server-totaled).</summary>
    [JsonPropertyName("coin_value")] public int? CoinValue { get; init; }
}

internal sealed record WorldStateProjection
{
    [JsonPropertyName("self")]
    public required SelfProjection Self { get; init; }

    [JsonPropertyName("inventory")]
    public required IReadOnlyList<InventoryItemProjection> Inventory { get; init; }

    [JsonPropertyName("visible")]
    public required IReadOnlyList<VisibleObjectProjection> Visible { get; init; }

    public static WorldStateProjection? FromWorldState(
        WorldState world,
        IWeenieRepository? weenies,
        float visibleRadius = 120f,
        int maxVisible = 48)
    {
        if (world.Self is not WorldObjectSnapshot self) return null;
        var selfGuid = self.Guid;

        var inv = world.Objects.Values
            .Where(o => o.ContainerGuid is uint c && c == selfGuid)
            .Where(o => !string.IsNullOrEmpty(o.Name))
            .Select(o =>
            {
                string? sd = null, ld = null;
                if (o.WeenieClassId is uint wcid && weenies is not null)
                {
                    var rec = weenies.TryGet(wcid);
                    sd = rec?.ShortDesc;
                    ld = rec?.LongDesc;
                }
                return new InventoryItemProjection
                {
                    Guid = o.Guid,
                    Name = o.Name!,
                    Wcid = o.WeenieClassId ?? 0u,
                    ItemType = o.ItemType,
                    ValidLocations = o.ValidLocations,
                    WieldedAt = o.CurrentWieldedLocation,
                    ShortDesc = sd,
                    LongDesc = ld,
                };
            })
            .ToList();

        var visible = world.Objects.Values
            .Where(o => o.Guid != selfGuid)
            .Where(o => !string.IsNullOrEmpty(o.Name))
            .Where(o => (o.Guid & 0xFF000000u) != 0x50000000u) // skip other players
            .Where(o => o.ContainerGuid is null || o.ContainerGuid == 0u) // skip inventory'd items
            .Where(o => o.CellId is uint cc && cc != 0u)
            .Select(o =>
            {
                float? dist = null;
                if (WorldDistance.TrySelectionSquaredDistance(self, o, out var d2))
                    dist = (float)Math.Sqrt(d2);

                var itemType = o.ItemType ?? 0u;
                var isCreature = (itemType & ItemTypeMasks.Creature) != 0;

                // Protocol-level schema classification, NOT English-string
                // matching. Holds for localized servers and custom-named
                // doors / chests / vendors / lifestones.
                var descFlags = o.ObjectDescriptionFlags ?? 0u;
                var isDoor      = (descFlags & (uint)ObjectDescriptionFlag.Door)      != 0;
                var isPortal    = (descFlags & (uint)ObjectDescriptionFlag.Portal)    != 0;
                var isCorpse    = (descFlags & (uint)ObjectDescriptionFlag.Corpse)    != 0;
                var isLifestone = (descFlags & (uint)ObjectDescriptionFlag.LifeStone) != 0;
                var isVendor    = (descFlags & (uint)ObjectDescriptionFlag.Vendor)    != 0;
                var isHealer    = (descFlags & (uint)ObjectDescriptionFlag.Healer)    != 0;
                var isOpenable  = (descFlags & (uint)ObjectDescriptionFlag.Openable)  != 0;
                var isStuck     = (descFlags & (uint)ObjectDescriptionFlag.Stuck)     != 0;
                var isAttackable = (descFlags & (uint)ObjectDescriptionFlag.Attackable) != 0;

                // Slice U — composite predicates the LLM can read directly
                // out of the visible-nearby projection. Building them here
                // keeps the prompt rendering simple (just check the bool)
                // and means tests can pin the derivation logic without
                // reaching into HandshakeDriver's inline lambdas.
                var isContainer = (itemType & ItemTypeMasks.Container) != 0;
                var isWritable  = (itemType & ItemTypeMasks.Writable)  != 0;
                var isChest     = isContainer && isOpenable && !isCorpse && !isDoor;
                var isBook      = isWritable && !isStuck;
                var isSign      = isWritable &&  isStuck;

                // Slice H — server-derived friend/foe signals. Both bits are
                // extracted from the wire, not from hardcoded weenie lists.
                var weenieFlags = o.WeenieFlags ?? 0u;
                var hasRadarBlipColor = (weenieFlags & (uint)WeenieHeaderFlag.RadarBlipColor) != 0;
                // Slice 0 (Hunt) — exclude corpses from IsMonster. Corpses
                // can carry Creature+Attackable bits in some captures (the
                // server doesn't strip them on death); without this guard
                // a Hunt-intent decomposer would target a dead body
                // already covered by the Step 5b openable-Use path.
                var isMonster = EntityClassifier.IsMonster(itemType, descFlags, weenieFlags);

                return new VisibleObjectProjection
                {
                    Guid = o.Guid,
                    Name = o.Name!,
                    Wcid = o.WeenieClassId,
                    ItemType = o.ItemType,
                    Distance = dist,
                    CellId = o.CellId,
                    IsCreature = isCreature,
                    IsPortal = isPortal,
                    IsDoor = isDoor,
                    IsCorpse = isCorpse,
                    IsLifestone = isLifestone,
                    IsVendor = isVendor,
                    IsHealer = isHealer,
                    IsOpenable = isOpenable,
                    IsChest = isChest,
                    IsBook = isBook,
                    IsSign = isSign,
                    IsAttackable = isAttackable,
                    HasRadarBlipColor = hasRadarBlipColor,
                    IsMonster = isMonster,
                };
            })
            .Where(v => v.Distance is null || v.Distance <= visibleRadius)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .Take(maxVisible)
            .ToList();

        var landblock = self.CellId is uint cell ? cell >> 16 : (uint?)null;

        int? level = null;
        int? numDeaths = null;
        int? coinValue = null;
        float? hfrac = null;
        if (self.HealthCurrent is uint hcur && self.HealthMax is uint hmax && hmax > 0)
        {
            // Clamp to [0,1] — Current should never exceed the
            // peak-observed max, but guard against transient buff/vitae
            // edge cases so the fraction stays a clean ratio.
            hfrac = Math.Clamp((float)hcur / hmax, 0f, 1f);
        }
        if (self.PropertyInts is { } props)
        {
            // PropertyInt ids: see ACE-bots Source/ACE.Entity/Enum/Properties/PropertyInt.cs
            //   25 = Level, 43 = NumDeaths, 20 = CoinValue
            if (props.TryGetValue(25u, out var lv)) level = lv;
            if (props.TryGetValue(43u, out var nd)) numDeaths = nd;
            if (props.TryGetValue(20u, out var cv)) coinValue = cv;
        }

        return new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = self.Guid,
                Name = self.Name,
                Landblock = landblock,
                CellId = self.CellId,
                PositionX = self.Position.X,
                PositionY = self.Position.Y,
                PositionZ = self.Position.Z,
                Level = level,
                HealthFraction = hfrac,
                NumDeaths = numDeaths,
                CoinValue = coinValue,
            },
            Inventory = inv,
            Visible = visible,
        };
    }
}
