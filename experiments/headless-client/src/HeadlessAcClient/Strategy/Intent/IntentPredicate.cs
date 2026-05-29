// SPDX-License-Identifier: AGPL-3.0-or-later
// IntentPredicate — typed completion-test DSL for IntentStack frames.
//
// Per the rubber-duck critique (Slice R design review), we DO NOT
// accept freeform LLM completion expressions. Instead the LLM picks
// from a small typed set of predicates, each of which evaluates
// against a deterministic EvalContext (world snapshot + event stream
// + baseline captured at push time + clock).
//
// Why baselines matter: without them, "talked to Jonathan" would
// false-complete on a Jonathan NpcDialog event from BEFORE the intent
// was pushed, and "inventory has Calling Stone" would false-complete
// if the bot already had one. Predicates that observe state CHANGE
// (events arriving, kill counter incrementing, landblock crossing,
// distance dropping) must consult the baseline; predicates that test
// CURRENT state may not need to.
//
// JSON shape (LLM emits one of these, identified by a `type` tag):
//
//   Composition:
//     {"type": "all_of",  "children": [...]}
//     {"type": "any_of",  "children": [...]}
//     {"type": "not",     "child":    {...}}
//
//   Event-based (delta from baseline event sequence):
//     {"type": "event_after_push", "kind": "NpcDialog", "name_contains": "Jonathan"}
//     {"type": "inventory_added_since_push_at_least",   "name_contains": "Pyreal", "count": 1000}
//     {"type": "inventory_removed_since_push_at_least", "name_contains": "Letter",  "count": 1}
//     {"type": "kill_count_since_push_at_least", "count": 10, "name_contains": "Golem"}
//
//   Current-state (no baseline):
//     {"type": "inventory_has_wcid", "wcid": 12709}
//     {"type": "inventory_has_name", "name_contains": "Calling Stone"}
//     {"type": "landblock_equals",   "landblock": 0x8602}
//     {"type": "level_at_least",     "level": 10}
//     {"type": "health_fraction_at_least", "fraction": 1.0}
//     {"type": "health_fraction_at_most",  "fraction": 0.3}
//     {"type": "visible_tag", "tag": "lifestone" | "vendor" | "healer" | "portal" | "door" | "corpse" | "monster"}
//     {"type": "no_monsters_visible"}
//
//   Delta-from-baseline (current vs push-time):
//     {"type": "landblock_changed_from_push"}
//     {"type": "level_gain_since_push_at_least", "count": 1}
//
//   Time / spatial:
//     {"type": "elapsed_seconds_at_least", "seconds": 60}
//     {"type": "within_distance", "target_guid": 0x800001CE, "max_distance": 5.0}
//     {"type": "not_visible_since_push_for_seconds", "target_guid": 0x..., "seconds": 30}
//
//   Sentinel:
//     {"type": "always_false"}   // intent only ends when explicitly
//                                // popped by the LLM (e.g. open-ended
//                                // exploration with no natural goalpost)
//
// Predicates are EVALUATED, never EXECUTED — they are pure functions
// from (world, events, baseline, now) -> bool. They do not push, pop,
// or mutate the stack.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy.Intent;

/// <summary>
/// Inputs available to a predicate when checking whether an intent
/// is complete. All fields are required so the caller is forced to
/// pass real data — there's no "default" that would silently
/// false-complete an intent.
/// </summary>
internal sealed record IntentEvalContext(
    WorldStateProjection World,
    EventStream Events,
    IntentBaseline Baseline,
    DateTime UtcNow);

/// <summary>
/// Base type. Sealed records below are the only valid implementations
/// — there is no "extend predicates" extension point at runtime so
/// every kind goes through the LLM JSON schema.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AllOfPredicate),                "all_of")]
[JsonDerivedType(typeof(AnyOfPredicate),                "any_of")]
[JsonDerivedType(typeof(NotPredicate),                  "not")]
[JsonDerivedType(typeof(EventAfterPushPredicate),       "event_after_push")]
[JsonDerivedType(typeof(InventoryHasWcidPredicate),     "inventory_has_wcid")]
[JsonDerivedType(typeof(InventoryHasNamePredicate),     "inventory_has_name")]
[JsonDerivedType(typeof(InventoryAddedSincePushAtLeastPredicate), "inventory_added_since_push_at_least")]
[JsonDerivedType(typeof(InventoryRemovedSincePushAtLeastPredicate), "inventory_removed_since_push_at_least")]
[JsonDerivedType(typeof(LandblockChangedFromPushPredicate), "landblock_changed_from_push")]
[JsonDerivedType(typeof(LandblockEqualsPredicate),      "landblock_equals")]
[JsonDerivedType(typeof(ElapsedSecondsAtLeastPredicate),"elapsed_seconds_at_least")]
[JsonDerivedType(typeof(WithinDistancePredicate),       "within_distance")]
[JsonDerivedType(typeof(NotVisibleSincePushForSecondsPredicate), "not_visible_since_push_for_seconds")]
[JsonDerivedType(typeof(LevelAtLeastPredicate),         "level_at_least")]
[JsonDerivedType(typeof(LevelGainSincePushAtLeastPredicate), "level_gain_since_push_at_least")]
[JsonDerivedType(typeof(KillCountSincePushAtLeastPredicate), "kill_count_since_push_at_least")]
[JsonDerivedType(typeof(HealthFractionAtLeastPredicate),"health_fraction_at_least")]
[JsonDerivedType(typeof(HealthFractionAtMostPredicate), "health_fraction_at_most")]
[JsonDerivedType(typeof(VisibleTagPredicate),           "visible_tag")]
[JsonDerivedType(typeof(NoMonstersVisiblePredicate),    "no_monsters_visible")]
[JsonDerivedType(typeof(AlwaysFalsePredicate),          "always_false")]
internal abstract record IntentPredicate
{
    public abstract bool IsSatisfied(IntentEvalContext ctx);

    /// <summary>
    /// Human-readable rendering for the LLM prompt's "## Intent stack"
    /// summary line on each frame ("until: kills>=5 AND level>=3").
    /// Short — the LLM sees this on every tick, don't bloat the prompt.
    /// </summary>
    public abstract string Summary();
}

/// <summary>All children must be satisfied.</summary>
internal sealed record AllOfPredicate(
    [property: JsonPropertyName("children")] IReadOnlyList<IntentPredicate> Children) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        Children is { Count: > 0 } && Children.All(c => c.IsSatisfied(ctx));

    public override string Summary() =>
        Children is { Count: > 0 } ? "(" + string.Join(" AND ", Children.Select(c => c.Summary())) + ")" : "(all_of:empty)";
}

/// <summary>Any child must be satisfied (empty list => false).</summary>
internal sealed record AnyOfPredicate(
    [property: JsonPropertyName("children")] IReadOnlyList<IntentPredicate> Children) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        Children is { Count: > 0 } && Children.Any(c => c.IsSatisfied(ctx));

    public override string Summary() =>
        Children is { Count: > 0 } ? "(" + string.Join(" OR ", Children.Select(c => c.Summary())) + ")" : "(any_of:empty)";
}

/// <summary>Negation. Inner child must be unsatisfied.</summary>
internal sealed record NotPredicate(
    [property: JsonPropertyName("child")] IntentPredicate Child) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        Child is not null && !Child.IsSatisfied(ctx);

    public override string Summary() => Child is null ? "NOT(null)" : "NOT" + Child.Summary();
}

/// <summary>
/// True iff an event of the given kind has been appended AFTER the
/// baseline event sequence. Optional NameContains additionally
/// constrains by event Name (case-insensitive substring), so e.g.
/// "NpcDialog from Jonathan" excludes NPC chatter from other NPCs.
/// </summary>
internal sealed record EventAfterPushPredicate(
    [property: JsonPropertyName("kind")] EventKind Kind,
    [property: JsonPropertyName("name_contains")] string? NameContains = null,
    [property: JsonPropertyName("text_contains")] string? TextContains = null) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx)
    {
        foreach (var e in ctx.Events.Recent())
        {
            if (e.Sequence <= ctx.Baseline.LastEventSequence) break;
            if (e.Kind != Kind) continue;
            if (NameContains is { Length: > 0 } nc &&
                (e.Name is null || e.Name.IndexOf(nc, StringComparison.OrdinalIgnoreCase) < 0))
                continue;
            if (TextContains is { Length: > 0 } tc &&
                (e.Text is null || e.Text.IndexOf(tc, StringComparison.OrdinalIgnoreCase) < 0))
                continue;
            return true;
        }
        return false;
    }

    public override string Summary()
    {
        var qual = "";
        if (NameContains is { Length: > 0 }) qual += $" name~\"{NameContains}\"";
        if (TextContains is { Length: > 0 }) qual += $" text~\"{TextContains}\"";
        return $"event[{Kind}{qual}]>push";
    }
}

/// <summary>
/// True iff the bot currently has an inventory item with the given
/// wcid. Does NOT compare to baseline — "I have the Calling Stone"
/// is a CURRENT-state predicate (you wouldn't have pushed an intent
/// to obtain it if you already had one). If you need a state-CHANGE
/// flavor, pair with EventAfterPushPredicate of InventoryItemAdded.
/// </summary>
internal sealed record InventoryHasWcidPredicate(
    [property: JsonPropertyName("wcid")] uint Wcid) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        ctx.World.Inventory.Any(i => i.Wcid == Wcid);

    public override string Summary() => $"inv has wcid={Wcid}";
}

/// <summary>Substring (case-insensitive) match on inventory item names.</summary>
internal sealed record InventoryHasNamePredicate(
    [property: JsonPropertyName("name_contains")] string NameContains) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        !string.IsNullOrWhiteSpace(NameContains) &&
        ctx.World.Inventory.Any(i =>
            i.Name?.IndexOf(NameContains, StringComparison.OrdinalIgnoreCase) >= 0);

    public override string Summary() => $"inv has \"{NameContains}\"";
}

/// <summary>
/// At least Count InventoryItemAdded events since push, optionally
/// filtered by item Name (case-insensitive substring) or wcid. Use
/// for "collect 10 pelts", "earn 1000 pyreals (1k unit stacks)", etc.
/// Note: pyreal stacks arrive as ONE InventoryItemAdded event per
/// stack — quantity-of-stack-items is not currently surfaced in the
/// projection, so for fungible currency the LLM should pair this with
/// inventory_has_name and re-push on need.
/// </summary>
internal sealed record InventoryAddedSincePushAtLeastPredicate(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("name_contains")] string? NameContains = null,
    [property: JsonPropertyName("wcid")] uint? Wcid = null) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx)
    {
        if (Count <= 0) return false;
        var seen = 0;
        foreach (var e in ctx.Events.Recent())
        {
            if (e.Sequence <= ctx.Baseline.LastEventSequence) break;
            if (e.Kind != EventKind.InventoryItemAdded) continue;
            if (Wcid is uint w && e.Wcid != w) continue;
            if (NameContains is { Length: > 0 } nc &&
                (e.Name is null || e.Name.IndexOf(nc, StringComparison.OrdinalIgnoreCase) < 0))
                continue;
            if (++seen >= Count) return true;
        }
        return false;
    }

    public override string Summary()
    {
        var q = NameContains is { Length: > 0 } ? $" name~\"{NameContains}\"" : (Wcid is uint w ? $" wcid={w}" : "");
        return $"+inv{q}>={Count}";
    }
}

/// <summary>Mirror of InventoryAdded — counts InventoryItemRemoved events since push.</summary>
internal sealed record InventoryRemovedSincePushAtLeastPredicate(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("name_contains")] string? NameContains = null,
    [property: JsonPropertyName("wcid")] uint? Wcid = null) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx)
    {
        if (Count <= 0) return false;
        var seen = 0;
        foreach (var e in ctx.Events.Recent())
        {
            if (e.Sequence <= ctx.Baseline.LastEventSequence) break;
            if (e.Kind != EventKind.InventoryItemRemoved) continue;
            if (Wcid is uint w && e.Wcid != w) continue;
            if (NameContains is { Length: > 0 } nc &&
                (e.Name is null || e.Name.IndexOf(nc, StringComparison.OrdinalIgnoreCase) < 0))
                continue;
            if (++seen >= Count) return true;
        }
        return false;
    }

    public override string Summary()
    {
        var q = NameContains is { Length: > 0 } ? $" name~\"{NameContains}\"" : (Wcid is uint w ? $" wcid={w}" : "");
        return $"-inv{q}>={Count}";
    }
}

/// <summary>
/// True iff the bot's current landblock differs from the landblock
/// captured at push time. Used for "go outside" / "leave this town"
/// style intents — completion is the act of crossing.
/// </summary>
internal sealed record LandblockChangedFromPushPredicate : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        ctx.Baseline.Landblock is uint b &&
        ctx.World.Self.Landblock is uint cur &&
        cur != b;

    public override string Summary() => "landblock!=push";
}

/// <summary>True iff the bot's current landblock equals the given value.</summary>
internal sealed record LandblockEqualsPredicate(
    [property: JsonPropertyName("landblock")] uint Landblock) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        ctx.World.Self.Landblock is uint cur && cur == Landblock;

    public override string Summary() => $"landblock==0x{Landblock:X4}";
}

/// <summary>True iff at least N seconds have elapsed since the intent was pushed.</summary>
internal sealed record ElapsedSecondsAtLeastPredicate(
    [property: JsonPropertyName("seconds")] int Seconds) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        Seconds > 0 &&
        (ctx.UtcNow - ctx.Baseline.PushedAtUtc).TotalSeconds >= Seconds;

    public override string Summary() => $"elapsed>={Seconds}s";
}

/// <summary>
/// True iff the visible-objects list contains TargetGuid AND its
/// distance is at or below MaxDistance. Used for "arrive at NPC"
/// (paired with EventAfterPushPredicate(NpcDialog) for the "talked"
/// proof) and for "approach target" style intents.
/// </summary>
internal sealed record WithinDistancePredicate(
    [property: JsonPropertyName("target_guid")] uint TargetGuid,
    [property: JsonPropertyName("max_distance")] float MaxDistance) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        TargetGuid != 0 &&
        ctx.World.Visible.Any(v =>
            v.Guid == TargetGuid &&
            v.Distance is float d &&
            d <= MaxDistance);

    public override string Summary() => $"dist(0x{TargetGuid:X8})<={MaxDistance:F1}u";
}

/// <summary>
/// True iff TargetGuid was visible at push time (recorded in
/// baseline) but has been ABSENT from the visible list for at least
/// N seconds since then. Used for failure detection ("the NPC
/// wandered off / decayed / got killed by another player").
/// </summary>
internal sealed record NotVisibleSincePushForSecondsPredicate(
    [property: JsonPropertyName("target_guid")] uint TargetGuid,
    [property: JsonPropertyName("seconds")] int Seconds) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx)
    {
        if (TargetGuid == 0 || Seconds <= 0) return false;
        if (!ctx.Baseline.VisibleAtPush.Contains(TargetGuid)) return false;
        var visibleNow = ctx.World.Visible.Any(v => v.Guid == TargetGuid);
        if (visibleNow)
        {
            return false;
        }
        return (ctx.UtcNow - ctx.Baseline.PushedAtUtc).TotalSeconds >= Seconds;
    }

    public override string Summary() => $"not_visible(0x{TargetGuid:X8})>={Seconds}s";
}

/// <summary>Self.Level &gt;= given level.</summary>
internal sealed record LevelAtLeastPredicate(
    [property: JsonPropertyName("level")] int Level) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        Level > 0 && ctx.World.Self.Level is int cur && cur >= Level;

    public override string Summary() => $"level>={Level}";
}

/// <summary>Levels gained since push &gt;= Count. Useful for "grind to next level"-style goals.</summary>
internal sealed record LevelGainSincePushAtLeastPredicate(
    [property: JsonPropertyName("count")] int Count) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx)
    {
        if (Count <= 0) return false;
        if (ctx.World.Self.Level is not int cur) return false;
        var baseline = ctx.Baseline.Level ?? cur;
        return (cur - baseline) >= Count;
    }

    public override string Summary() => $"level_gain>={Count}";
}

/// <summary>
/// At least Count GoalCompleted events of kind Attack since push,
/// optionally filtered by target name (case-insensitive substring on
/// the goal's Text — the GoalCompleted event carries the rendered
/// goal description). Used for "kill 10 Sparring Golems" style
/// intents. Counts a KILL only when an Attack goal completed
/// successfully — interrupted or expired Attack goals do not count.
/// </summary>
internal sealed record KillCountSincePushAtLeastPredicate(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("name_contains")] string? NameContains = null) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx)
    {
        if (Count <= 0) return false;
        var seen = 0;
        foreach (var e in ctx.Events.Recent())
        {
            if (e.Sequence <= ctx.Baseline.LastEventSequence) break;
            if (e.Kind != EventKind.GoalCompleted) continue;
            // GoalCompleted Text is the goal description ("Attack{name=Sparring Golem}").
            if (e.Text is null || e.Text.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (NameContains is { Length: > 0 } nc &&
                e.Text.IndexOf(nc, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (++seen >= Count) return true;
        }
        return false;
    }

    public override string Summary()
    {
        var q = NameContains is { Length: > 0 } ? $" \"{NameContains}\"" : "";
        return $"kills{q}>={Count}";
    }
}

/// <summary>Self.HealthFraction &gt;= fraction. Useful for "rest until healed".</summary>
internal sealed record HealthFractionAtLeastPredicate(
    [property: JsonPropertyName("fraction")] float Fraction) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        Fraction > 0f && ctx.World.Self.HealthFraction is float hf && hf >= Fraction;

    public override string Summary() => $"hp>={Fraction:P0}";
}

/// <summary>Self.HealthFraction &lt;= fraction. Useful for "flee when wounded".</summary>
internal sealed record HealthFractionAtMostPredicate(
    [property: JsonPropertyName("fraction")] float Fraction) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        Fraction > 0f && ctx.World.Self.HealthFraction is float hf && hf <= Fraction;

    public override string Summary() => $"hp<={Fraction:P0}";
}

/// <summary>
/// True iff at least one visible object carries the given semantic
/// tag (mirrors the LLM prompt's `tag` words). Tag values map to
/// VisibleObjectProjection bool flags; unknown tags evaluate false.
/// </summary>
internal sealed record VisibleTagPredicate(
    [property: JsonPropertyName("tag")] string Tag) : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx)
    {
        if (string.IsNullOrWhiteSpace(Tag)) return false;
        return Tag.ToLowerInvariant() switch
        {
            "lifestone" => ctx.World.Visible.Any(v => v.IsLifestone),
            "vendor"    => ctx.World.Visible.Any(v => v.IsVendor),
            "healer"    => ctx.World.Visible.Any(v => v.IsHealer),
            "portal"    => ctx.World.Visible.Any(v => v.IsPortal),
            "door"      => ctx.World.Visible.Any(v => v.IsDoor),
            "corpse"    => ctx.World.Visible.Any(v => v.IsCorpse),
            "monster"   => ctx.World.Visible.Any(v => v.IsMonster),
            _           => false,
        };
    }

    public override string Summary() => $"see[{Tag}]";
}

/// <summary>True iff no visible object is tagged as a monster.</summary>
internal sealed record NoMonstersVisiblePredicate : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) =>
        !ctx.World.Visible.Any(v => v.IsMonster);

    public override string Summary() => "no_monsters";
}

/// <summary>
/// Sentinel: never satisfied. Used when the LLM wants an intent that
/// only ends by an explicit pop_top op — e.g. "explore freely" with
/// no natural goalpost. Prevents callers from accidentally creating a
/// frame that never gets garbage-collected; the LLM must own the pop.
/// </summary>
internal sealed record AlwaysFalsePredicate : IntentPredicate
{
    public override bool IsSatisfied(IntentEvalContext ctx) => false;

    public override string Summary() => "never (manual-pop)";
}
