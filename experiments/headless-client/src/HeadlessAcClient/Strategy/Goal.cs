// SPDX-License-Identifier: AGPL-3.0-or-later
// Goal — what the bot wants to do next, derived by the Strategy
// layer (LLM or fallback policy) and consumed by the Tactics layer.
//
// Per the EPIC #67 architecture rule: the Strategy layer is the
// ONLY source of game-content knowledge. Goals NAME what they want
// (an NPC by name, an item matching a short-desc substring, a
// creature of an itemtype) — they DO NOT carry resolved guids.
// Tactics resolves selectors against the observed worldState.
//
// Selectors are deliberately fuzzy: the LLM doesn't see live guids
// across calls (they change every session), so it must address
// world objects by stable attributes (name, weenie-class id observed
// in inventory, item-type bitmask, short-desc text). Tactics
// turns those into a snapshot at execute time. If the selector
// fails to resolve (target moved out of view, item consumed),
// Tactics fails the goal and asks Strategy for a new one.
//
// `Wcid` is allowed in selectors because the LLM may legitimately
// reference an item's class id it has already observed THIS SESSION
// (e.g., "give the wcid=29335 token I'm holding to the named NPC").
// What is forbidden is hardcoding a wcid in source as a decision
// trigger. The wcid in a Goal is a runtime observation, not a
// developer-time literal.

using System;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy;

internal enum GoalKind
{
    Unknown = 0,
    Give    = 1,
    Use     = 2,
    Attack  = 3,
    Pickup  = 4,
    Wield   = 5,
    GoTo    = 6,
    Talk    = 7,
    Wait    = 8,
    Explore = 9,
}

/// <summary>
/// Fuzzy match descriptor. Tactics finds zero or more snapshots in
/// worldState matching the populated fields; at least one field
/// must be set. Multiple fields AND together.
/// </summary>
internal sealed record Selector
{
    /// <summary>Live observed guid (rare; mostly for replay/test).</summary>
    [JsonPropertyName("guid")]
    public uint? Guid { get; init; }

    /// <summary>Exact name match (case-insensitive).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Substring match on name (case-insensitive).</summary>
    [JsonPropertyName("name_contains")]
    public string? NameContains { get; init; }

    /// <summary>WeenieClassId observed earlier in this session.</summary>
    [JsonPropertyName("wcid")]
    public uint? Wcid { get; init; }

    /// <summary>
    /// ItemType bitmask. Matches any object whose ItemType bit-AND
    /// is non-zero. Schema constants only (see ItemTypeMasks).
    /// </summary>
    [JsonPropertyName("item_type_mask")]
    public uint? ItemTypeMask { get; init; }

    /// <summary>
    /// Substring match against the item's ShortDesc text (sourced
    /// from WeenieRepository). Lets the LLM say "the item whose
    /// short_desc contains 'exit'" without naming a wcid.
    /// </summary>
    [JsonPropertyName("short_desc_contains")]
    public string? ShortDescContains { get; init; }

    /// <summary>True iff at least one matching field is set.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Guid is null &&
        string.IsNullOrWhiteSpace(Name) &&
        string.IsNullOrWhiteSpace(NameContains) &&
        Wcid is null &&
        ItemTypeMask is null &&
        string.IsNullOrWhiteSpace(ShortDescContains);

    public override string ToString()
    {
        var parts = new System.Collections.Generic.List<string>(4);
        if (Guid is { } g) parts.Add($"guid=0x{g:X8}");
        if (!string.IsNullOrEmpty(Name)) parts.Add($"name=\"{Name}\"");
        if (!string.IsNullOrEmpty(NameContains)) parts.Add($"name_contains=\"{NameContains}\"");
        if (Wcid is { } w) parts.Add($"wcid={w}");
        if (ItemTypeMask is { } m) parts.Add($"item_type=0x{m:X}");
        if (!string.IsNullOrEmpty(ShortDescContains)) parts.Add($"short_desc~=\"{ShortDescContains}\"");
        return parts.Count == 0 ? "<empty>" : string.Join(" ", parts);
    }
}

/// <summary>
/// A unit of intent emitted by Strategy. Has a globally-unique id
/// so training-data and outcome records can be correlated.
/// </summary>
internal sealed record Goal
{
    [JsonPropertyName("goal_id")]
    [JsonConverter(typeof(FlexibleGuidConverter))]
    public Guid Id { get; init; } = Guid.Empty;

    [JsonPropertyName("kind")]
    public required GoalKind Kind { get; init; }

    /// <summary>
    /// Primary subject of the goal (the NPC to talk to, the door
    /// to use, the creature to attack, the location to walk to).
    /// </summary>
    [JsonPropertyName("target")]
    public required Selector Target { get; init; }

    /// <summary>
    /// Secondary object for two-actor goals: the item to GIVE,
    /// the item to PICKUP, the item to WIELD. Null otherwise.
    /// </summary>
    [JsonPropertyName("item")]
    public Selector? Item { get; init; }

    /// <summary>
    /// Free-form rationale from Strategy. Used for log readability
    /// and training-data audit ("why did the LLM pick this?").
    /// </summary>
    [JsonPropertyName("rationale")]
    public string Rationale { get; init; } = "";

    /// <summary>
    /// Higher = more urgent. Tactics will preempt a current goal
    /// if a new goal arrives with strictly higher priority. Default
    /// 5; reserve 9-10 for "drop everything" (health-critical,
    /// landblock-changed forces re-deliberation).
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; init; } = 5;

    /// <summary>
    /// Tactics abandons the goal after this many seconds of
    /// in-progress execution. Null means "no expiry, run until
    /// completion or failure".
    /// </summary>
    [JsonPropertyName("expires_in_seconds")]
    public int? ExpiresInSeconds { get; init; }

    /// <summary>
    /// Strategy source. "llm:gpt-4o-mini" / "fallback:no-quest-knowledge".
    /// Recorded into training-data so we can split corpus by source.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public override string ToString() =>
        $"Goal[{Kind}] target={Target}" +
        (Item is null ? "" : $" item={Item}") +
        $" prio={Priority}" +
        (ExpiresInSeconds is null ? "" : $" expires={ExpiresInSeconds}s") +
        $" src={Source}" +
        (string.IsNullOrEmpty(Rationale) ? "" : $" rationale=\"{Rationale}\"");
}

/// <summary>
/// Common ItemType bitmasks (schema, not game content). Mirror of
/// values referenced in HandshakeDriver picker logic. Forbidden:
/// adding "QuestX" or "PortalToY" — those would be content.
/// </summary>
internal static class ItemTypeMasks
{
    public const uint Creature = 0x00000010u; // NPCs + mobs
    public const uint Portal   = 0x00010000u;
    public const uint Writable = 0x00002000u; // signs, books
    public const uint Container = 0x00000200u; // sacks, corpses, chests, bookshelves

    /// <summary>
    /// ACE.Entity.Enum.ItemType.MeleeWeapon bit (0x1). Inventory item
    /// whose ItemType has this bit and is wielded (WieldedAt != 0)
    /// satisfies the wire-schema precondition for the
    /// GameActionTargetedMeleeAttack message — the only attack path
    /// the driver currently issues (see HandshakeDriver attack-loop
    /// notes). Used by NoQuestKnowledgePolicy to gate Attack goals on
    /// "do I actually have a weapon equipped".
    /// </summary>
    public const uint MeleeWeapon = 0x00000001u;

    /// <summary>
    /// Mirror of HandshakeDriver.PickupItemTypeMask (0xD96F).
    /// Bits: MeleeWeapon (0x1) | Armor (0x2) | Clothing (0x4) |
    /// Jewelry (0x8) | Food (0x20) | Money (0x40) | MissileWeapon
    /// (0x100) | Gem (0x800) | SpellComponents (0x1000) | Key
    /// (0x4000) | Caster (0x8000). Misc (0x80) excluded because
    /// doors carry Misc but aren't pickup-able.
    /// </summary>
    public const uint Pickup = 0xD96Fu;
}
