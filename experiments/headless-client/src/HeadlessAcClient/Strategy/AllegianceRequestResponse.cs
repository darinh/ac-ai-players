// SPDX-License-Identifier: AGPL-3.0-or-later
// AllegianceRequestResponse — pure decision for an AllegianceApprove goal. Given the
// bot's pending swear-allegiance request (a prospective vassal asked to pledge to it),
// it decides whether the Motor should send a ConfirmationResponse approving it, and
// which context id to echo back. No I/O and no game knowledge: the LLM already chose to
// approve; this only maps the perceived pending request onto the wire fields the Motor
// packs. Mirrors FellowshipInviteResponse as a unit-testable Motor-decision helper.

using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

internal static class AllegianceRequestResponse
{
    /// <summary>Outcome of <see cref="Decide"/>.</summary>
    internal enum ResultKind
    {
        /// <summary>Send a ConfirmationResponse approving the pending request.</summary>
        Approve,
        /// <summary>Do not send; no request is outstanding.</summary>
        Fail,
    }

    /// <summary>
    /// The decision. <see cref="Context"/> is the request's context id the response
    /// must echo (meaningful only when <see cref="Kind"/> is Approve);
    /// <see cref="FailReason"/> is set only on Fail.
    /// </summary>
    internal readonly record struct Result(ResultKind Kind, uint Context, string? FailReason)
    {
        internal static Result Approving(uint context) => new(ResultKind.Approve, context, null);
        internal static Result Failed(string reason) => new(ResultKind.Fail, 0u, reason);
    }

    /// <summary>
    /// Decide how to answer an AllegianceApprove goal. Returns Approve (carrying the
    /// context to echo) when a swear-allegiance request is pending; otherwise Fail.
    /// </summary>
    internal static Result Decide(PendingAllegianceRequest? pending) =>
        pending is { } request
            ? Result.Approving(request.Context)
            : Result.Failed("no pending allegiance request");
}
