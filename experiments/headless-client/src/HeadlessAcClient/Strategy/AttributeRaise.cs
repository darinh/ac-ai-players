// SPDX-License-Identifier: AGPL-3.0-or-later
// AttributeRaise — pure protocol-level helpers for the RaiseAttribute
// (0x0045) self-action verb.
//
// This file is deliberately knowledge-FREE. It maps the six attribute
// NAMES the LLM may emit to their raw PropertyAttribute wire enum ids,
// and validates/clamps the LLM-supplied XP amount. It encodes NO mechanic
// (e.g. "Endurance raises health"), NO preference for one attribute over
// another, and NO default amount. The Strategy layer decides WHICH
// attribute and HOW MUCH; this helper only translates a valid request into
// the bytes the opcode needs, and refuses an invalid one.

using System;

namespace HeadlessAcClient.Strategy;

internal static class AttributeRaise
{
    /// <summary>
    /// Resolve an attribute NAME (as the LLM names it in the goal target)
    /// to its raw PropertyAttribute wire enum id (Strength=1, Endurance=2,
    /// Quickness=3, Coordination=4, Focus=5, Self=6). Case-insensitive,
    /// trims surrounding whitespace. Returns false for null/empty/unknown
    /// names — the caller surfaces a motor error and sends nothing.
    /// </summary>
    public static bool TryResolveAttributeId(string? name, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        switch (name.Trim().ToLowerInvariant())
        {
            case "strength":     id = 1; return true;
            case "endurance":    id = 2; return true;
            case "quickness":    id = 3; return true;
            case "coordination": id = 4; return true;
            case "focus":        id = 5; return true;
            case "self":         id = 6; return true;
            default:             return false;
        }
    }

    /// <summary>
    /// Validate the LLM-supplied XP amount and clamp it to the bot's
    /// observed available experience.
    ///
    /// There is NO source-side default — a missing amount is rejected so
    /// the source never decides how much to spend. The amount must be a
    /// whole positive integer in [1, uint.MaxValue]; zero / negative /
    /// out-of-range is rejected. (A fractional amount is rejected earlier
    /// at JSON deserialization, since the field is an integer.) Then the
    /// amount is clamped down to <paramref name="availableExperience"/>,
    /// purely mechanical safety so the server does not reject a spend that
    /// exceeds the bot's unspent XP. Returns false (and the caller sends
    /// nothing) when the amount is invalid OR there is no spendable XP.
    /// </summary>
    public static bool TryValidateAndClampAmount(
        long? amount, long? availableExperience, out uint clamped)
    {
        clamped = 0;
        if (amount is not long requested) return false;
        if (requested < 1 || requested > uint.MaxValue) return false;
        if (availableExperience is not long avail || avail < 1) return false;
        var capped = Math.Min(requested, avail);
        if (capped < 1 || capped > uint.MaxValue) return false;
        clamped = (uint)capped;
        return true;
    }
}
