// SPDX-License-Identifier: AGPL-3.0-or-later
// FellowshipInviteResponse — pure decision for a FellowshipAccept goal. Given the
// bot's pending fellowship invite (or none), it decides whether the Motor should
// send a ConfirmationResponse accepting it, and which context id to echo back. No
// I/O and no game knowledge: the LLM already chose to accept; this only maps the
// perceived pending invite onto the wire fields the Motor packs. Mirrors
// SayRouting.Decide / RecallEscape as a unit-testable Motor-decision helper.

using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

internal static class FellowshipInviteResponse
{
    /// <summary>Outcome of <see cref="Decide"/>.</summary>
    internal enum ResultKind
    {
        /// <summary>Send a ConfirmationResponse accepting the pending invite.</summary>
        Accept,
        /// <summary>Do not send; no invite is outstanding.</summary>
        Fail,
    }

    /// <summary>
    /// The decision. <see cref="Context"/> is the invite's context id the response
    /// must echo (meaningful only when <see cref="Kind"/> is Accept);
    /// <see cref="FailReason"/> is set only on Fail.
    /// </summary>
    internal readonly record struct Result(ResultKind Kind, uint Context, string? FailReason)
    {
        internal static Result Accepting(uint context) => new(ResultKind.Accept, context, null);
        internal static Result Failed(string reason) => new(ResultKind.Fail, 0u, reason);
    }

    /// <summary>
    /// Decide how to answer a FellowshipAccept goal. Returns Accept (carrying the
    /// context to echo) when an invite is pending; otherwise Fail with a reason.
    /// </summary>
    internal static Result Decide(PendingFellowshipInvite? pending) =>
        pending is { } invite
            ? Result.Accepting(invite.Context)
            : Result.Failed("no pending fellowship invite");
}
