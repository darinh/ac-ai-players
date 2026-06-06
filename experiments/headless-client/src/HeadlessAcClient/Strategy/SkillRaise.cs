// SPDX-License-Identifier: AGPL-3.0-or-later
// SkillRaise — pure protocol-level helpers for the RaiseSkill (0x0046)
// self-action verb.
//
// This file is deliberately knowledge-FREE. It maps a skill NAME the LLM may
// emit to its raw wire ordinal via the HeadlessAcClient.Protocol.Skill enum
// (the verbatim wire contract), and reuses AttributeRaise.TryValidateAndClampAmount
// for XP validation. It encodes NO mechanic (e.g. "War Magic casts spells"),
// NO preference for one skill over another, NO curated list of "good" skills,
// and NO default amount. The Strategy layer decides WHICH skill and HOW MUCH;
// this helper only translates a valid request into the bytes the opcode needs,
// and refuses an invalid one. Whether a named skill is actually trained/raisable
// is the SERVER's call (it rejects untrained/retired/unimplemented skills); the
// source does not encode that knowledge — it resolves any real Skill member
// (except None) to its ordinal and lets the server validate.

using System;

namespace HeadlessAcClient.Strategy;

internal static class SkillRaise
{
    /// <summary>
    /// Resolve a skill NAME (as the LLM names it in the goal target) to its
    /// raw wire ordinal from <see cref="HeadlessAcClient.Protocol.Skill"/>.
    /// The LLM may write the name with spaces, hyphens, or underscores
    /// ("War Magic", "melee-defense", "two_handed_combat"); those separators
    /// are stripped before a case-insensitive enum parse, so the CamelCase
    /// wire member name need not be reproduced exactly. Returns false for
    /// null/empty/unknown names and for <c>None</c> (ordinal 0, not a real
    /// skill) — the caller surfaces a motor error and sends nothing. It does
    /// NOT pre-judge whether the skill is trained or implemented; the server
    /// validates that and rejects with a chat message.
    /// </summary>
    public static bool TryResolveSkillId(string? name, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var normalized = name.Trim()
            .Replace(" ", "")
            .Replace("-", "")
            .Replace("_", "");
        if (normalized.Length == 0) return false;

        // A real skill name is pure letters. Rejecting anything else stops
        // Enum.TryParse from accepting a numeric string ("34" -> an arbitrary
        // ordinal) or a comma-separated flags list ("WarMagic,Healing" -> an
        // OR'd bogus value) as if it were a skill.
        foreach (var ch in normalized)
            if (!char.IsAsciiLetter(ch)) return false;

        if (!Enum.TryParse<HeadlessAcClient.Protocol.Skill>(normalized, ignoreCase: true, out var skill))
            return false;
        if (!Enum.IsDefined(skill)) return false;
        if (skill == HeadlessAcClient.Protocol.Skill.None) return false;

        id = (uint)skill;
        return true;
    }
}
