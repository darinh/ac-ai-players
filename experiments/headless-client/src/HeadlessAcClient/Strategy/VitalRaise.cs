// SPDX-License-Identifier: AGPL-3.0-or-later
// VitalRaise — pure protocol-level helpers for the RaiseVital (0x0044)
// self-action verb.
//
// This file is deliberately knowledge-FREE. It maps the three vital NAMES
// the LLM may emit to their raw PropertyAttribute2nd wire enum ids (the
// raisable MAX pools), and reuses AttributeRaise.TryValidateAndClampAmount
// for XP validation. It encodes NO mechanic (e.g. "Mana fuels spells"), NO
// preference for one vital over another, and NO default amount. The Strategy
// layer decides WHICH vital and HOW MUCH; this helper only translates a valid
// request into the bytes the opcode needs, and refuses an invalid one.

namespace HeadlessAcClient.Strategy;

internal static class VitalRaise
{
    /// <summary>
    /// Resolve a vital NAME (as the LLM names it in the goal target) to its
    /// raw PropertyAttribute2nd wire enum id for the raisable MAX pools
    /// (MaxHealth=1, MaxStamina=3, MaxMana=5). Accepts the short pool name
    /// ("health"/"stamina"/"mana") or the explicit "max..." form.
    /// Case-insensitive, trims surrounding whitespace. Returns false for
    /// null/empty/unknown names — the caller surfaces a motor error and sends
    /// nothing.
    /// </summary>
    public static bool TryResolveVitalId(string? name, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        switch (name.Trim().ToLowerInvariant())
        {
            case "health":
            case "maxhealth":
            case "max health":  id = 1; return true;
            case "stamina":
            case "maxstamina":
            case "max stamina": id = 3; return true;
            case "mana":
            case "maxmana":
            case "max mana":    id = 5; return true;
            default:            return false;
        }
    }
}
