// SPDX-License-Identifier: AGPL-3.0-or-later
// ExplorationCandidate — Slice W.2 (ac-ai-players#87).
//
// One entry in the "## Exploration candidates" prompt block. The
// block lists the off-screen known objects the fallback picker is
// considering when the in-range queue is empty. The LLM uses it
// to make the strategic exploration call ("walk to the door I
// haven't been through" / "backtrack through the entry door to
// re-stimulate the prior room") that the picker no longer encodes.
//
// Architectural intent (mirror of PickerActivity.cs):
//   The picker's pure-distance fallback chooses ONE target. The
//   candidate list shows the LLM what else is on the menu so it
//   can override with `Explore{target=<guid|name>}` to pick a
//   different one. Schema-only — no game knowledge baked in.
//
// Fields are deliberately small and audit-safe: guid, name,
// distance, cell, a visited boolean, and a coarse wire-derived
// Kind (mob/npc/object). The Kind is the SAME single-source-of-
// truth classification used for visible objects and sighting
// memory (EntityClassifier.ClassifySighting) — a mechanical
// projection of ItemType/ObjectDescriptionFlag/WeenieHeader bits,
// NOT an English-name match and NOT a priority. It lets the LLM
// tell a creature candidate from an inert scenery marker among
// OFF-screen candidates (which, being off-screen, are usually
// absent from the "## Visible nearby" block). The bot assigns no
// preference to any Kind; the LLM owns the Explore choice.

using System;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// One off-screen exploration candidate surfaced to the LLM.
/// Position fields are present so the LLM can reason about cell
/// adjacency / direction; they are NOT acted on by the bot
/// directly (the bot resolves the target via guid/name at execute
/// time through the standard SelectorResolver).
/// </summary>
internal sealed record ExplorationCandidate
{
    /// <summary>Guid of the candidate; addressable via
    /// <see cref="Selector.Guid"/> in an Explore goal.</summary>
    public required uint Guid { get; init; }

    /// <summary>Display name; addressable via
    /// <see cref="Selector.Name"/> when unambiguous.</summary>
    public required string Name { get; init; }

    /// <summary>Straight-line distance from bot in world units.</summary>
    public required float Distance { get; init; }

    /// <summary>CellId — high 16 bits are the landblock.</summary>
    public required uint CellId { get; init; }

    /// <summary>True if this guid is in the bot's visited set —
    /// the LLM may still pick it to deliberately backtrack and
    /// re-stimulate cells the bot can no longer see.</summary>
    public required bool Visited { get; init; }

    /// <summary>Coarse wire-derived category (Mob / NPC / else
    /// Unknown) from <see cref="EntityClassifier.ClassifySighting"/>.
    /// Mechanical projection of ItemType/ObjectDescriptionFlag/
    /// WeenieHeader bits — not a name match, not a priority. Defaults
    /// to <see cref="EntityKind.Unknown"/> so existing call sites
    /// that don't classify (e.g. tests) compile unchanged; the live
    /// candidate-build site sets the real kind.</summary>
    public EntityKind Kind { get; init; } = EntityKind.Unknown;
}
