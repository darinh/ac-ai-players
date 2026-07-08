// SPDX-License-Identifier: AGPL-3.0-or-later
// SayRouting — the pure decision behind a GoalKind.Say dispatch: given the LLM's
// (message, channel), decide whether to FAIL, say LOCALLY (Talk 0x0015), or say on
// a group CHANNEL (ChatChannel 0x0147). Extracted from the Motor so the branching
// — especially the leak-prevention rule that a NON-BLANK but unrecognised channel
// FAILS rather than downgrading to a local say — is unit-tested, not buried in the
// HandshakeDriver tick loop. The Motor calls Decide() and only packs bytes.
//
// The two safety rules encoded here:
//   1. An empty / unsayable message (after sanitisation) FAILS — nothing is sent.
//   2. A non-blank channel the motor cannot resolve FAILS — it is NOT downgraded to
//      a local say, so text meant for a group channel never leaks to nearby players.
// A blank/omitted channel is a local say; a resolved channel is a channel say.

namespace HeadlessAcClient.Protocol.GameMessages;

internal enum SayRouteKind
{
    /// <summary>Do not send; Fail the goal with <see cref="SayRoute.FailReason"/>.</summary>
    Fail,
    /// <summary>Local say (Talk 0x0015) of <see cref="SayRoute.Text"/>.</summary>
    Local,
    /// <summary>Channel say (ChatChannel 0x0147) of <see cref="SayRoute.Text"/> on <see cref="SayRoute.Channel"/>.</summary>
    Channel,
}

/// <summary>Immutable result of <see cref="SayRouting.Decide"/>.</summary>
internal readonly record struct SayRoute(
    SayRouteKind Kind,
    string? Text,
    uint Channel,
    string? FailReason)
{
    public static SayRoute Fail(string reason)   => new(SayRouteKind.Fail, null, 0, reason);
    public static SayRoute Local(string text)    => new(SayRouteKind.Local, text, 0, null);
    public static SayRoute ToChannel(uint channel, string text) => new(SayRouteKind.Channel, text, channel, null);
}

internal static class SayRouting
{
    /// <summary>
    /// Decide how to send a Say goal. <paramref name="message"/> is the LLM-authored
    /// line; <paramref name="channel"/> is its optional channel name. Sanitises the
    /// message and resolves the channel, then applies the two safety rules (empty
    /// message -> Fail; non-blank unresolved channel -> Fail, never a local downgrade).
    /// </summary>
    public static SayRoute Decide(string? message, string? channel)
    {
        var text = GameActionTalkMessage.SanitizeMessage(message);
        if (text is null)
            return SayRoute.Fail("say: empty or unsayable message");

        var resolved = GameActionChatChannelMessage.ResolveChannel(channel);
        if (resolved is null && !string.IsNullOrWhiteSpace(channel))
            return SayRoute.Fail("say: unrecognized channel");

        return resolved is uint chan
            ? SayRoute.ToChannel(chan, text)
            : SayRoute.Local(text);
    }
}
