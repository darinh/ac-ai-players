// SPDX-License-Identifier: AGPL-3.0-or-later
// FellowshipMembership — WorldState's own view of the bot's fellowship,
// distilled from the server's FellowshipFullUpdate (0x02BE) snapshot. Pure
// perception memory: it records WHO is in the fellowship and WHO leads so the
// LLM prompt can perceive it. It carries no priority, no game knowledge, and
// no decision about whether/how to use the fellowship — that stays with the
// LLM.

using System.Collections.Generic;

namespace HeadlessAcClient.World;

/// <summary>One fellowship member as the bot perceives it: identity, display
/// name, and level. Health/stamina/mana are intentionally not surfaced here —
/// the membership perception does not need per-member vitals.</summary>
internal sealed record FellowshipMember(uint Guid, string Name, uint Level);

/// <summary>
/// The bot's current fellowship as last reported by the server's
/// FellowshipFullUpdate snapshot. <see cref="LeaderGuid"/> identifies the
/// leader (compare against the bot's own guid to derive "am I the leader");
/// the four flags are the raw fellowship bools the server sends.
/// </summary>
internal sealed record FellowshipMembership(
    string Name,
    uint LeaderGuid,
    IReadOnlyList<FellowshipMember> Members,
    bool ShareXp,
    bool EvenShare,
    bool Open,
    bool IsLocked);

/// <summary>
/// A pending fellowship-invite prompt the server has sent the bot and that it has
/// not yet answered. Distilled from a CharacterConfirmationRequest (0x0274) whose
/// type is <c>ConfirmationType.Fellowship</c>. <see cref="Context"/> is the
/// server's context id, which a ConfirmationResponse (0x0275) must echo back;
/// <see cref="Text"/> is the server-supplied prompt line (surfaced to the LLM).
/// Pure perception memory — the LLM owns whether to accept.
/// </summary>
internal sealed record PendingFellowshipInvite(uint Context, string Text);

/// <summary>
/// A pending swear-allegiance request the server has sent the bot (as a prospective
/// PATRON/monarch) and that it has not yet answered. Distilled from a
/// CharacterConfirmationRequest (0x0274) whose type is
/// <c>ConfirmationType.SwearAllegiance</c>. <see cref="Context"/> is the server's
/// context id a ConfirmationResponse (0x0275) must echo; <see cref="Text"/> is the
/// server-supplied prompt line (the would-be vassal's name). Pure perception memory —
/// the LLM owns whether to approve.
/// </summary>
internal sealed record PendingAllegianceRequest(uint Context, string Text);
