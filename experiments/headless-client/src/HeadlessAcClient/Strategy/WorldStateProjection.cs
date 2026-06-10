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
    /// For a Door-flagged object only: true if the door is currently OPEN,
    /// false if CLOSED, null if its physics state is not yet known. Decoded
    /// from the PhysicsState <c>Ethereal</c> bit (0x4): an ACE door becomes
    /// Ethereal when open (so players pass through) and non-Ethereal when
    /// closed (Source/ACE.Server/WorldObjects/Door.cs broadcasts this via
    /// SetState 0xF74B). Pure wire-bit decode — no game knowledge; the LLM
    /// decides whether a closed door is worth opening. null for non-doors.
    /// </summary>
    [JsonPropertyName("is_door_open")] public bool? IsDoorOpen { get; init; }

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
    /// <summary>Lifetime total experience (PropertyInt64 TotalExperience). i64: lifetime XP exceeds 2^31.</summary>
    [JsonPropertyName("total_experience")] public long? TotalExperience { get; init; }
    /// <summary>Unspent experience available to raise skills/attributes (PropertyInt64 AvailableExperience).</summary>
    [JsonPropertyName("unassigned_experience")] public long? AvailableExperience { get; init; }
    [JsonPropertyName("health_fraction")] public float? HealthFraction { get; init; }

    // Raw self-health perception facts (current HP is wire-authoritative;
    // observed peak is a max PROXY that can under-estimate the true max
    // when the bot logged in damaged). Surfaced so Strategy is not misled
    // by a fraction that reads "100%" at a sub-max observed peak.
    /// <summary>Latest wire-authoritative current HP (absolute).</summary>
    [JsonPropertyName("health_current")] public int? HealthCurrent { get; init; }
    /// <summary>Peak current HP ever observed this session — a max proxy; may under-estimate the true max if the first reading was sub-max.</summary>
    [JsonPropertyName("health_observed_peak")] public int? HealthObservedPeak { get; init; }
    /// <summary>True when current HP is strictly rising over the last two readings (regen) — proves the bot is BELOW its true max, so the fraction overstates health.</summary>
    [JsonPropertyName("health_rising")] public bool? HealthRising { get; init; }

    // Server-authoritative counters (read directly from PropertyInts;
    // ACE pushes these on character-load + on every change).
    /// <summary>PropertyInt.NumDeaths (43). Total deaths this character has ever suffered. Persists across sessions.</summary>
    [JsonPropertyName("num_deaths")] public int? NumDeaths { get; init; }

    /// <summary>PropertyInt.CoinValue (20). Pyreals in inventory (server-totaled).</summary>
    [JsonPropertyName("coin_value")] public int? CoinValue { get; init; }

    /// <summary>
    /// Character-sheet attribute base values (StartingValue + raised Ranks),
    /// seeded once from the login PlayerDescription. Null until the login
    /// bundle is decoded. Login-only: stale after a RaiseAttribute/RaiseVital
    /// until relogin.
    /// </summary>
    [JsonPropertyName("attributes")]
    public IReadOnlyList<SelfAttributeProjection>? Attributes { get; init; }

    /// <summary>
    /// The character's RAISABLE skills — those whose wire AdvancementClass is
    /// Trained or Specialized (Untrained/Inactive can't be raised). Seeded
    /// once from the login PlayerDescription. Null when none are known yet.
    /// </summary>
    [JsonPropertyName("trained_skills")]
    public IReadOnlyList<SelfSkillProjection>? TrainedSkills { get; init; }
}

/// <summary>One self attribute/vital base value for the prompt.</summary>
internal sealed record SelfAttributeProjection
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("base")] public uint Base { get; init; }
}

/// <summary>
/// One raisable self skill. <see cref="Advancement"/> is "trained" or
/// "specialized" (the wire SkillAdvancementClass). <see cref="RaisedRanks"/>
/// is the raised ranks only (excludes the creation/training InitLevel bonus
/// and the attribute contribution).
/// </summary>
internal sealed record SelfSkillProjection
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("advancement")] public required string Advancement { get; init; }
    [JsonPropertyName("raised_ranks")] public uint RaisedRanks { get; init; }
}

internal sealed record WorldStateProjection
{
    [JsonPropertyName("self")]
    public required SelfProjection Self { get; init; }

    [JsonPropertyName("inventory")]
    public required IReadOnlyList<InventoryItemProjection> Inventory { get; init; }

    [JsonPropertyName("visible")]
    public required IReadOnlyList<VisibleObjectProjection> Visible { get; init; }

    /// <summary>
    /// combat-damage-output: the live outcome of the current melee
    /// fight (swings landed vs evaded, damage dealt), or null when not
    /// in combat. Copied straight from <see cref="WorldState.CurrentFight"/>.
    /// Surfaced to the LLM as raw perception so it can judge whether its
    /// attacks are connecting and decide to disengage; source never makes
    /// that decision.
    /// </summary>
    [JsonPropertyName("current_fight")]
    public CombatFightStatus? CurrentFight { get; init; }

    /// <summary>
    /// active-combat-telemetry: rolling-window summary of recent inbound
    /// damage the bot has TAKEN. Copied straight from
    /// <see cref="WorldState.RecentInboundDamage"/>. Surfaced to the LLM as
    /// raw perception so it can judge whether it is losing a fight and decide
    /// to disengage or Recall; source never makes that decision.
    /// </summary>
    [JsonPropertyName("recent_inbound_damage")]
    public RecentInboundDamage? RecentInboundDamage { get; init; }

    /// <summary>
    /// loot bookkeeping: GUIDs of corpses/containers the bot has itself
    /// opened recently. Copied straight from
    /// <see cref="WorldState.OpenedCorpseGuids"/>. Used to annotate a
    /// visible corpse row with <c>opened_by_bot_recently=yes|no</c> so the
    /// LLM knows which corpses it has already looted; source never decides
    /// to loot — the LLM owns that.
    /// </summary>
    [JsonPropertyName("opened_corpse_guids")]
    public IReadOnlySet<uint>? OpenedCorpseGuids { get; init; }

    /// <summary>
    /// combat-feel: per-mob-identity summary of the bot's own observed
    /// combat outcomes this session (kills/deaths/near-deaths). Copied
    /// from <see cref="WorldState.CombatHistory"/>. Surfaced as raw
    /// recorded facts in the "## Combat history" prompt section; source
    /// records outcomes only and assigns no danger label.
    /// </summary>
    [JsonPropertyName("combat_history")]
    public IReadOnlyList<CombatHistoryEntry>? CombatHistory { get; init; }

    /// <summary>
    /// UNCAPPED combat-feel outcomes (every kind, not just the prompt's
    /// most-recent 6). Copied from <see cref="WorldState.CombatHistoryFull"/>.
    /// Runtime-only (not serialized): consumed by the
    /// <c>kill_count_since_push</c> predicate's per-kind baseline so an
    /// aged-out kind is not mis-counted. Not surfaced to the LLM.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<CombatHistoryEntry>? CombatHistoryFull { get; init; }

    /// <summary>
    /// cold-start egress: kind-keys (<see cref="CombatFeelLedger.KeyOf"/>
    /// form) of monster kinds the bot has killed since entering the current
    /// landblock. Copied from <see cref="WorldState.KilledKindsThisDwell"/>.
    /// Consumed only by the mechanical hunt-egress override to recognise an
    /// already-farmed-here kind once the bot is tapped out — raw bot-owned
    /// outcome data, no danger/value label. Not serialised to the LLM prompt.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string>? KilledKindsThisDwell { get; init; }

    /// <summary>
    /// immobile-stuck telemetry: consecutive full movement block-stops with
    /// no self-position change (copied from
    /// <see cref="WorldState.MovementBlockStopsSinceSelfMoved"/>). Rendered as
    /// raw facts in the "## Movement" prompt section; the LLM decides whether
    /// the bot is wedged and what to do. 0 ⇒ the section is omitted.
    /// </summary>
    [JsonPropertyName("movement_block_stops")]
    public int MovementBlockStopsSinceSelfMoved { get; init; }

    /// <summary>
    /// named-target frontier-search telemetry (copied from
    /// <see cref="WorldState.NamedSearchTargetName"/> et al.). When the bot is
    /// pursuing a named target it cannot see, these carry the current search run's
    /// target name, probe count, and distinct-cells-tried so the "## Search
    /// progress" prompt section can surface a stalled/repeating discovery search.
    /// Null/0 ⇒ not currently searching ⇒ the section is omitted.
    /// </summary>
    [JsonPropertyName("named_search_target")]
    public string? NamedSearchTargetName { get; init; }

    /// <summary>Consecutive discovery probes on the current named-target search.</summary>
    [JsonPropertyName("named_search_probes")]
    public int NamedSearchProbeCount { get; init; }

    /// <summary>Distinct frontier cells tried during the current named-target search.</summary>
    [JsonPropertyName("named_search_distinct_cells")]
    public int NamedSearchDistinctCells { get; init; }

    /// <summary>
    /// Every distinct object name observed via ObjectCreate since login
    /// (append-only, case-insensitive; see <see cref="WorldState.EverObservedNames"/>).
    /// Lets the prompt builder answer "has this name EVER been a real world object"
    /// for the active objective's named target — distinct from "is it visible now".
    /// Reference to the live set (read-only use); not serialized into training data.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> EverObservedNames { get; init; } =
        System.Collections.Immutable.ImmutableHashSet<string>.Empty;

    /// <summary>
    /// True if an object with this exact name (case-insensitive) has entered the
    /// world model at any point this session. False for null/empty. A dialog-named
    /// target that never resolves to a real object stays false however long it is
    /// sought — the signal behind the "## Unseen objective target" capsule.
    /// </summary>
    public bool WasNameEverObserved(string? name)
        => !string.IsNullOrEmpty(name) && EverObservedNames.Contains(name);

    /// <summary>
    /// PhysicsState <c>Ethereal</c> bit (Source/ACE.Entity/Enum/PhysicsState.cs).
    /// An ACE door is Ethereal when open and non-Ethereal when closed.
    /// </summary>
    private const uint PhysicsStateEthereal = 0x00000004u;

    /// <summary>
    /// Default perception radius (world units) for the projection's visible
    /// set: objects farther than this from self are NOT surfaced to the
    /// Strategy. This is the bot's sensor window — what the LLM can "see"
    /// when it chooses a goal. Exposed as a named constant so the Motor can
    /// bound target resolution to the SAME window the Strategy decided from
    /// (see <c>TacticsExecutor.ResolveTarget</c>). Sensor range only; encodes
    /// nothing about game content.
    /// </summary>
    public const float DefaultVisibleRadiusUnits = 120f;

    public static WorldStateProjection? FromWorldState(
        WorldState world,
        IWeenieRepository? weenies,
        float visibleRadius = DefaultVisibleRadiusUnits,
        int maxVisible = 48)
    {
        if (world.Self is not WorldObjectSnapshot self) return null;
        var selfGuid = self.Guid;

        // Snapshot the recently-attacked-us name set once (already
        // normalized + TTL-pruned by HandshakeDriver) for the per-object
        // ObservedHostile derivation below.
        var recentHostile = world.RecentHostileNames;

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

                // observed-hostile perception: the server told us (via a
                // recent DefenderNotification 0x01B2 / EvasionDefenderNotification
                // 0x01B4) that a creature with this NAME is attacking the bot.
                // The wire carries only the attacker's name, not a guid, so we
                // match by normalized name — two same-named mobs both read as
                // hostile, which is acceptable (no guid is available to
                // disambiguate). HandshakeDriver prunes the set by TTL before
                // each projection build, so a name here means "attacked us
                // recently". RAW perception — the LLM decides fight vs flee.
                var observedHostile = recentHostile is not null
                    && NormalizeHostileName(o.Name) is string hn
                    && recentHostile.Contains(hn);

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

                // Door open/closed affordance — decoded ONLY for Door-flagged
                // objects from the PhysicsState Ethereal bit (0x4). An ACE door
                // flips Ethereal on when open / off when closed and broadcasts
                // it (Door.cs). null when the door's physics state is unknown
                // (e.g. before the first ObjectCreate/SetState carries it) so we
                // never assert "closed" without evidence. Mechanical wire decode.
                bool? isDoorOpen = null;
                if (isDoor && o.PhysicsState is uint ps)
                    isDoorOpen = (ps & PhysicsStateEthereal) != 0;

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
                    IsDoorOpen = isDoorOpen,
                    IsChest = isChest,
                    IsBook = isBook,
                    IsSign = isSign,
                    IsAttackable = isAttackable,
                    HasRadarBlipColor = hasRadarBlipColor,
                    IsMonster = isMonster,
                    ObservedHostile = observedHostile,
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
        int? hcurOut = self.HealthCurrent is uint hc0 ? (int)hc0 : (int?)null;
        int? hpeakOut = self.HealthMax is uint hm0 ? (int)hm0 : (int?)null;
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

        long? totalXp = null;
        long? availXp = null;
        if (self.PropertyInt64s is { } props64)
        {
            // PropertyInt64 ids: see ACE-bots Source/ACE.Entity/Enum/Properties/PropertyInt64.cs
            //   1 = TotalExperience (lifetime), 2 = AvailableExperience (unspent)
            if (props64.TryGetValue(PrivateUpdatePropertyInt64Message.TotalExperienceId, out var tx)) totalXp = tx;
            if (props64.TryGetValue(PrivateUpdatePropertyInt64Message.AvailableExperienceId, out var ax)) availXp = ax;
        }

        // Character-sheet attributes + raisable skills, seeded from the login
        // PlayerDescription. Filtering skills to AdvancementClass Trained(2)/
        // Specialized(3) is a raw wire fact (these are the ones the character
        // actually has and can raise) — NOT a value judgement about which to
        // raise; that decision stays with the LLM.
        IReadOnlyList<SelfAttributeProjection>? attrProj = null;
        if (self.SelfAttributes is { Count: > 0 } sa)
        {
            attrProj = sa
                .Select(a => new SelfAttributeProjection { Name = a.Name, Base = a.Base })
                .ToList();
        }

        IReadOnlyList<SelfSkillProjection>? skillProj = null;
        if (self.SelfSkills is { } ss)
        {
            var raisable = ss
                .Where(s => s.AdvancementClass is 2u or 3u)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new SelfSkillProjection
                {
                    Name = s.Name,
                    Advancement = s.AdvancementClass == 3u ? "specialized" : "trained",
                    RaisedRanks = s.Ranks,
                })
                .ToList();
            if (raisable.Count > 0) skillProj = raisable;
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
                TotalExperience = totalXp,
                AvailableExperience = availXp,
                HealthFraction = hfrac,
                HealthCurrent = hcurOut,
                HealthObservedPeak = hpeakOut,
                HealthRising = self.HealthRising,
                NumDeaths = numDeaths,
                CoinValue = coinValue,
                Attributes = attrProj,
                TrainedSkills = skillProj,
            },
            Inventory = inv,
            Visible = visible,
            CurrentFight = world.CurrentFight,
            RecentInboundDamage = world.RecentInboundDamage,
            OpenedCorpseGuids = world.OpenedCorpseGuids,
            CombatHistory = world.CombatHistory,
            CombatHistoryFull = world.CombatHistoryFull,
            KilledKindsThisDwell = world.KilledKindsThisDwell,
            MovementBlockStopsSinceSelfMoved = world.MovementBlockStopsSinceSelfMoved,
            NamedSearchTargetName = world.NamedSearchTargetName,
            NamedSearchProbeCount = world.NamedSearchProbeCount,
            NamedSearchDistinctCells = world.NamedSearchDistinctCells,
            EverObservedNames = world.EverObservedNames,
        };
    }

    /// <summary>
    /// Normalizes a creature display name to a stable key for hostile-name
    /// matching: collapse internal whitespace + lower-invariant. Purely
    /// mechanical text normalization — no literal names or game knowledge.
    /// Must stay byte-identical to the keying HandshakeDriver uses when it
    /// records attacker names, so the projection and the tracker agree.
    /// Returns null for null/blank input.
    /// </summary>
    internal static string? NormalizeHostileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var collapsed = string.Join(' ',
            name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed.ToLowerInvariant();
    }
}
