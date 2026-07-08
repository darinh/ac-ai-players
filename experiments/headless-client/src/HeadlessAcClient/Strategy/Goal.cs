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

    /// <summary>
    /// Spend accumulated experience to raise one of the six primary
    /// attributes. A self-action (no world target / motion): the LLM
    /// names the attribute in <see cref="Goal.Target"/> (name = one of
    /// strength/endurance/quickness/coordination/focus/self) and the XP
    /// to invest in <see cref="Goal.Amount"/>. The motor maps the name
    /// to the wire enum id and sends the chunk; it makes NO decision
    /// about WHICH attribute or HOW MUCH (Strategy owns that).
    /// </summary>
    RaiseAttribute = 10,

    /// <summary>
    /// Spend accumulated experience to raise one of the three vital MAX
    /// pools (max health / max stamina / max mana). A self-action (no world
    /// target / motion): the LLM names the vital in <see cref="Goal.Target"/>
    /// (name = one of health/stamina/mana) and the XP to invest in
    /// <see cref="Goal.Amount"/>. The motor maps the name to the wire enum id
    /// and sends the chunk; it makes NO decision about WHICH vital or HOW
    /// MUCH (Strategy owns that).
    /// </summary>
    RaiseVital = 11,

    /// <summary>
    /// Spend accumulated experience to raise one trained skill. A self-action
    /// (no world target / motion): the LLM names the skill in
    /// <see cref="Goal.Target"/> (name = a skill, e.g. "war magic", "melee
    /// defense") and the XP to invest in <see cref="Goal.Amount"/>. The motor
    /// maps the name to the wire ordinal and sends the chunk; it makes NO
    /// decision about WHICH skill or HOW MUCH, and does NOT pre-judge whether
    /// the skill is trained (the server validates and rejects untrained ones).
    /// </summary>
    RaiseSkill = 12,

    /// <summary>
    /// Recall to the bot's attuned lifestone (sanctuary). A self-action
    /// with NO world target, item, amount, or motion: the motor sends the
    /// empty-body <c>TeleToLifestone</c> (0x0063) GameAction and the server
    /// teleports the bot to its tied lifestone after a recall animation.
    /// This is the escape hatch the Strategy layer can name when the bot is
    /// physically unable to move (see the <c>## Movement</c> prompt section).
    /// The motor makes NO decision about WHETHER to recall — Strategy owns
    /// that; the server owns the preconditions (an attuned sanctuary is
    /// required; recall is refused inside the training academy and shortly
    /// after PvP).
    /// </summary>
    Recall = 13,

    /// <summary>
    /// Buy an item from the vendor whose trade panel is currently open. The LLM
    /// names the for-sale item in <see cref="Goal.Target"/> (name = an item from
    /// the <c>## Vendor offerings</c> prompt section) and MAY set
    /// <see cref="Goal.Amount"/> to a quantity (default 1). The motor resolves
    /// the name to the open vendor's matching item guid and sends the Buy
    /// (0x005F) GameAction to that vendor; the server charges the cost and
    /// places the item in the bot's pack. The motor makes NO decision about WHAT
    /// or WHETHER to buy (Strategy owns that) and never opens a vendor on its
    /// own — a Buy with no vendor panel open fails so the LLM approaches the
    /// vendor (Use/Talk) first. No game-content knowledge lives here.
    /// </summary>
    Buy = 14,

    /// <summary>
    /// Sell an item from the bot's OWN inventory to the vendor whose trade panel
    /// is currently open. The LLM names a held item in <see cref="Goal.Target"/>
    /// (name = an item from the <c>## Inventory</c> prompt section) and MAY set
    /// <see cref="Goal.Amount"/> to a quantity (default 1). The motor resolves the
    /// name to the matching inventory item guid and sends the Sell (0x0060)
    /// GameAction to that vendor; the server credits the bot with the item's sell
    /// value and removes it from the pack. The motor makes NO decision about WHAT
    /// or WHETHER to sell (Strategy owns that) and never opens a vendor on its own
    /// — a Sell with no vendor panel open fails so the LLM approaches the vendor
    /// (Use/Talk) first. No game-content knowledge lives here.
    /// </summary>
    Sell = 15,

    /// <summary>
    /// Form a fellowship (a small player party) led by the bot, so members can
    /// share XP and coordinate. A self/social action with no world target: the
    /// LLM names the fellowship in <see cref="Goal.Target"/> (name = the desired
    /// fellowship name). The motor packs the FellowshipCreate wire action; it
    /// makes NO decision about WHETHER or WHEN to form one (Strategy owns that).
    /// The server replies with a FellowshipFullUpdate the client already decodes.
    /// </summary>
    FellowshipCreate = 16,

    /// <summary>
    /// Leave (or disband) the bot's current fellowship. A self/social action with
    /// no world target. The motor packs the FellowshipQuit wire action; it makes
    /// NO decision about WHETHER/WHEN to leave (Strategy owns that). The server
    /// replies with a FellowshipFullUpdate/Quit the client already decodes.
    /// </summary>
    FellowshipQuit = 17,

    /// <summary>
    /// Invite another player (the LLM-named target, a `player` in Visible nearby)
    /// into the bot's fellowship. The motor resolves the named target to its guid,
    /// validates it is a player, and packs the FellowshipRecruit wire action; it
    /// makes NO decision about WHO/WHETHER to recruit (Strategy owns that).
    /// </summary>
    FellowshipRecruit = 18,

    /// <summary>
    /// Swear allegiance to another player (the LLM-named target, a `player` in
    /// Visible nearby), becoming their vassal. The motor resolves the named target
    /// to its guid, validates it is a player, and packs the SwearAllegiance wire
    /// action; it makes NO decision about WHO/WHETHER to swear to (Strategy owns
    /// that). Mirrors FellowshipRecruit — a player-directed social action.
    /// </summary>
    SwearAllegiance = 19,

    /// <summary>
    /// Break allegiance with another player (the LLM-named target — a `player` in
    /// Visible nearby). The motor resolves the named target to its guid, validates it
    /// is a player, and packs the BreakAllegiance wire action; it makes NO decision
    /// about WHO or WHETHER to break (Strategy owns that). Mirrors SwearAllegiance — a
    /// player-directed social action.
    /// </summary>
    BreakAllegiance = 20,

    /// <summary>
    /// Say a line ALOUD as local chat (heard by nearby players/creatures). A
    /// self-broadcast with no world target: the LLM authors the line in
    /// <see cref="Goal.Message"/>; the motor sanitizes it (printable ASCII, no
    /// leading command '@', length-capped) and packs the Talk (0x0015) wire action.
    /// The motor invents NO text of its own (Strategy owns WHAT to say and WHETHER).
    /// </summary>
    Say = 21,
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
    /// Defaults to an empty selector so self-actions that have no
    /// world target (e.g. <see cref="GoalKind.Recall"/>) can omit it;
    /// <c>TryParseGoal</c> still rejects an empty target for every verb
    /// that needs one.
    /// </summary>
    [JsonPropertyName("target")]
    public Selector Target { get; init; } = new();

    /// <summary>
    /// Secondary object for two-actor goals: the item to GIVE,
    /// the item to PICKUP, the item to WIELD. Null otherwise.
    /// </summary>
    [JsonPropertyName("item")]
    public Selector? Item { get; init; }

    /// <summary>
    /// XP to spend, for the <see cref="GoalKind.RaiseAttribute"/> verb
    /// only. The LLM MUST supply a positive whole number — there is no
    /// source-side default, because choosing how much experience to
    /// invest is a strategic decision the Strategy layer owns. The motor
    /// clamps it down to the bot's observed available experience
    /// (mechanical safety so the server doesn't reject the spend); a
    /// null / non-positive / out-of-uint-range value is rejected with a
    /// motor error and no opcode is sent. Ignored by every other verb.
    /// </summary>
    [JsonPropertyName("amount")]
    public long? Amount { get; init; }

    /// <summary>
    /// Optional 8-way compass heading for an <see cref="GoalKind.Explore"/>
    /// goal (north, northeast, east, southeast, south, southwest, west,
    /// northwest; case-insensitive). When set, the Motor's outdoor frontier
    /// search BIASES its bearing toward this heading among near-tie unexplored
    /// sectors — letting Strategy steer a hunt excursion that has covered
    /// ground without finding monsters. Null (the default) keeps the existing
    /// undirected "walk to the nearest unexplored frontier" behavior. The LLM
    /// chooses the heading; the Motor only walks it (a near-tie bias that can
    /// never force a cooled or clearly worse-explored cell). Ignored by every
    /// other verb and by indoor exploration.
    /// </summary>
    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    /// <summary>
    /// The chat line to speak, for a <see cref="GoalKind.Say"/> goal only. The LLM
    /// authors this free text; the Motor sanitizes it (printable ASCII, no leading
    /// command '@', length-capped) and packs the Talk wire action. Null/empty for
    /// every other verb; a Say with no Message is rejected at parse time.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

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
    /// ACE.Entity.Enum.ItemType.MissileWeapon bit (0x100). An inventory
    /// item whose ItemType has this bit and is wielded (WieldedAt != 0)
    /// satisfies the wire-schema precondition for the
    /// GameActionTargetedMissileAttack message (bows, crossbows,
    /// atlatls). Used to pick the missile attack opcode + Missile combat
    /// mode instead of melee, mirroring <see cref="MeleeWeapon"/>.
    /// </summary>
    public const uint MissileWeapon = 0x00000100u;

    /// <summary>
    /// EquipMask.MissileAmmo SLOT bit (0x00800000) — NOT an ItemType.
    /// An item whose CurrentWieldedLocation equals this is loaded ammo
    /// (arrows/bolts/darts); an unwielded item whose ValidLocations has
    /// this bit is ammo the bot can wield. Ammo launchers (atlatl, bow)
    /// require loaded ammo before the server will resolve a missile
    /// attack; thrown weapons do not. Source only surfaces the fact.
    /// </summary>
    public const uint MissileAmmoSlot = 0x00800000u;

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
