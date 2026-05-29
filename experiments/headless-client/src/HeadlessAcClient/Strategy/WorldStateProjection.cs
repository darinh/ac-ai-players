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

    /// <summary>True if observed-hostile (e.g. server-message indicated initial attack on us).</summary>
    [JsonPropertyName("observed_hostile")] public bool ObservedHostile { get; init; }
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
        float visibleRadius = 60f,
        int maxVisible = 32)
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
                if (WorldDistance.TrySquaredDistance(self, o, out var d2))
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
                };
            })
            .Where(v => v.Distance is null || v.Distance <= visibleRadius)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .Take(maxVisible)
            .ToList();

        var landblock = self.CellId is uint cell ? cell >> 16 : (uint?)null;

        int? level = null;
        float? hfrac = null;
        if (self.PropertyInts is { } props)
        {
            // PropertyInt.Level = 25, MaxHealth = 16, CurrentHealth not Int — but coarse level is fine
            if (props.TryGetValue(25u, out var lv)) level = lv;
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
            },
            Inventory = inv,
            Visible = visible,
        };
    }
}
