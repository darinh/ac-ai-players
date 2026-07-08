// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for SayRouting.Decide — the pure Say-dispatch decision (fail / local / channel)
// extracted from the Motor. The two safety rules under test:
//   1. empty/unsayable message -> Fail (nothing sent)
//   2. non-blank UNRESOLVED channel -> Fail (NOT a local downgrade; no group-text leak)
// plus the happy paths (blank channel -> local; resolved channel -> channel say) and
// that the message text is sanitized (leading '@' stripped, non-ASCII dropped).

using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class SayRoutingTests
{
    // ---- Fail: empty / unsayable message ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@@@")]        // all command markers -> sanitizes to null
    public void EmptyOrUnsayableMessage_Fails(string? message)
    {
        var r = SayRouting.Decide(message, channel: null);
        Assert.Equal(SayRouteKind.Fail, r.Kind);
        Assert.Equal("say: empty or unsayable message", r.FailReason);
        Assert.Null(r.Text);
    }

    [Fact]
    public void EmptyMessage_FailsEvenWithAValidChannel()
    {
        // The message check comes first: a Say with a fellowship channel but no text
        // still fails on the empty message, not the channel.
        var r = SayRouting.Decide("   ", channel: "fellowship");
        Assert.Equal(SayRouteKind.Fail, r.Kind);
        Assert.Equal("say: empty or unsayable message", r.FailReason);
    }

    [Fact]
    public void EmptyMessage_FailsOnMessage_EvenWithAnInvalidChannel()
    {
        // Precedence lock: an empty message + an UNRESOLVED channel fails on the
        // MESSAGE ("empty or unsayable"), not the channel — message is checked first.
        var r = SayRouting.Decide("", channel: "felloship");
        Assert.Equal(SayRouteKind.Fail, r.Kind);
        Assert.Equal("say: empty or unsayable message", r.FailReason);
    }

    // ---- Fail: non-blank unresolved channel (no local downgrade / no leak) ----

    [Theory]
    [InlineData("allegiance")]   // deliberately not routed yet
    [InlineData("felloship")]    // typo
    [InlineData("general")]
    [InlineData("trade")]
    public void UnresolvedNonBlankChannel_Fails_NotLocalDowngrade(string channel)
    {
        var r = SayRouting.Decide("hello team", channel);
        Assert.Equal(SayRouteKind.Fail, r.Kind);
        Assert.Equal("say: unrecognized channel", r.FailReason);
        // Crucially NOT a local say — group-intended text must not leak locally.
        Assert.NotEqual(SayRouteKind.Local, r.Kind);
    }

    // ---- Local say (blank/omitted channel) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankChannel_RoutesLocal(string? channel)
    {
        var r = SayRouting.Decide("well met", channel);
        Assert.Equal(SayRouteKind.Local, r.Kind);
        Assert.Equal("well met", r.Text);
        Assert.Equal(0u, r.Channel);
        Assert.Null(r.FailReason);
    }

    // ---- Channel say (resolved channel) ----

    [Theory]
    [InlineData("fellowship")]
    [InlineData("Fellow")]
    [InlineData("  FELLOWSHIP  ")]
    public void FellowshipChannel_RoutesChannel(string channel)
    {
        var r = SayRouting.Decide("forming up", channel);
        Assert.Equal(SayRouteKind.Channel, r.Kind);
        Assert.Equal("forming up", r.Text);
        Assert.Equal(GameActionChatChannelMessage.FellowChannel, r.Channel);
        Assert.Null(r.FailReason);
    }

    // ---- Message sanitisation flows through ----

    [Fact]
    public void Message_IsSanitized_LeadingAtStripped()
    {
        // A leading '@' would be parsed by the server as a slash command; the router's
        // sanitized text must have it removed for both local and channel routes.
        var local = SayRouting.Decide("@hi there", channel: null);
        Assert.Equal(SayRouteKind.Local, local.Kind);
        Assert.Equal("hi there", local.Text);

        var chan = SayRouting.Decide("@@@go", "fellowship");
        Assert.Equal(SayRouteKind.Channel, chan.Kind);
        Assert.Equal("go", chan.Text);
    }

    [Fact]
    public void Message_IsSanitized_NonAsciiDropped()
    {
        var r = SayRouting.Decide("hi \u201cthere\u201d", channel: null);
        Assert.Equal(SayRouteKind.Local, r.Kind);
        Assert.Equal("hi there", r.Text);
    }
}
