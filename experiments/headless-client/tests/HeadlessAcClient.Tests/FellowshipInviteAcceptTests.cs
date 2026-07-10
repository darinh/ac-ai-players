// SPDX-License-Identifier: AGPL-3.0-or-later
// fellowship-invite-accept: end-to-end coverage for the capability that lets a
// bot ACCEPT a fellowship invite (the receiving side of criterion 5).
//   1. decode CharacterConfirmationRequest (0x0274) / ConfirmationDone (0x0276)
//   2. WorldState: store the pending Fellowship invite, clear on matching Done /
//      on join / explicitly; ignore non-Fellowship and context-mismatched events
//   3. FellowshipInviteResponse.Decide: accept-with-context vs fail-when-none
//   4. the "## Fellowship invite" prompt cue renders only when one is pending

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using HeadlessAcClient.Protocol;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class FellowshipInviteAcceptTests
{
    private const uint FellowshipType = 0x04;  // ConfirmationType.Fellowship

    // ---- decode: CharacterConfirmationRequest (0x0274) ----

    private static byte[] BuildConfirmationRequestBody(uint type, uint context, string text)
    {
        var body = new byte[8 + AcStrings.MeasureString16L(text)];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), type);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), context);
        AcStrings.WriteString16L(body.AsSpan(8), text);
        return body;
    }

    [Fact]
    public void Decode_ConfirmationRequest_ReadsTypeContextText()
    {
        var body = BuildConfirmationRequestBody(FellowshipType, 42u, "Alcott invites you to a fellowship");
        var p = GameEventPayloadDecoder.Decode(body, GameEventType.CharacterConfirmationRequest);
        Assert.NotNull(p?.ConfirmationRequest);
        Assert.Equal(FellowshipType, p!.ConfirmationRequest!.ConfirmationType);
        Assert.Equal(42u, p.ConfirmationRequest.Context);
        Assert.Equal("Alcott invites you to a fellowship", p.ConfirmationRequest.Text);
    }

    [Fact]
    public void Decode_ConfirmationRequest_TooShort_ReturnsNull()
    {
        // Malformed (fewer than the 8 fixed bytes) must not throw out of Decode.
        var p = GameEventPayloadDecoder.Decode(new byte[6], GameEventType.CharacterConfirmationRequest);
        Assert.Null(p);
    }

    [Fact]
    public void Decode_ConfirmationRequest_InlinedByteFixture_NoPad()
    {
        // Fully inlined wire bytes (NOT built with the shared String16L writer) so a
        // shared writer bug cannot mask a decode bug. type=Fellowship(4), context=42,
        // text="Hi": u32 type + u32 context + u16 len(2) + "Hi" -> cursor 12, 4-aligned,
        // no pad. 12 bytes total.
        var body = new byte[]
        {
            0x04, 0x00, 0x00, 0x00,   // u32 type = 4 (Fellowship)
            0x2A, 0x00, 0x00, 0x00,   // u32 context = 42
            0x02, 0x00,               // u16 String16L length = 2
            (byte)'H', (byte)'i',     // "Hi"
        };
        var p = GameEventPayloadDecoder.Decode(body, GameEventType.CharacterConfirmationRequest);
        Assert.NotNull(p?.ConfirmationRequest);
        Assert.Equal(4u, p!.ConfirmationRequest!.ConfirmationType);
        Assert.Equal(42u, p.ConfirmationRequest.Context);
        Assert.Equal("Hi", p.ConfirmationRequest.Text);
    }

    [Fact]
    public void Decode_ConfirmationRequest_InlinedByteFixture_WithPad()
    {
        // As above but text="abc": u16 len(3) + "abc" -> cursor 13, needs 3 pad bytes to
        // the next 4-byte boundary. Exercises the String16L pad-skip. 16 bytes total.
        var body = new byte[]
        {
            0x04, 0x00, 0x00, 0x00,              // u32 type = 4 (Fellowship)
            0x63, 0x00, 0x00, 0x00,              // u32 context = 99
            0x03, 0x00,                          // u16 String16L length = 3
            (byte)'a', (byte)'b', (byte)'c',     // "abc"
            0x00, 0x00, 0x00,                    // 3 pad bytes to 4-byte align
        };
        var p = GameEventPayloadDecoder.Decode(body, GameEventType.CharacterConfirmationRequest);
        Assert.NotNull(p?.ConfirmationRequest);
        Assert.Equal(99u, p!.ConfirmationRequest!.Context);
        Assert.Equal("abc", p.ConfirmationRequest.Text);
    }

    // ---- decode: CharacterConfirmationDone (0x0276) ----

    [Fact]
    public void Decode_ConfirmationDone_ReadsTypeAndContext()
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), FellowshipType);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 99u);
        var p = GameEventPayloadDecoder.Decode(body, GameEventType.CharacterConfirmationDone);
        Assert.NotNull(p?.ConfirmationDone);
        Assert.Equal(FellowshipType, p!.ConfirmationDone!.ConfirmationType);
        Assert.Equal(99u, p.ConfirmationDone.Context);
    }

    // ---- WorldState: store / clear ----

    [Fact]
    public void WorldState_ApplyRequest_StoresFellowshipInvite()
    {
        var ws = new WorldState();
        var applied = ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(FellowshipType, 7u, "join?"));
        Assert.True(applied);
        Assert.NotNull(ws.PendingFellowshipInvite);
        Assert.Equal(7u, ws.PendingFellowshipInvite!.Context);
        Assert.Equal("join?", ws.PendingFellowshipInvite.Text);
    }

    [Fact]
    public void WorldState_ApplyRequest_IgnoresNonFellowshipTypes()
    {
        var ws = new WorldState();
        // AlterAttribute (0x03) uses a confirmation prompt too, but it is not a tracked
        // kind (the client has no accept action for it), so it populates nothing.
        var applied = ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(0x03u, 7u, "raise?"));
        Assert.False(applied);
        Assert.Null(ws.PendingFellowshipInvite);
    }

    [Fact]
    public void WorldState_ApplyDone_ClearsMatchingContext()
    {
        var ws = new WorldState();
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(FellowshipType, 7u, "join?"));
        var cleared = ws.ApplyConfirmationDone(new ConfirmationDonePayload(FellowshipType, 7u));
        Assert.True(cleared);
        Assert.Null(ws.PendingFellowshipInvite);
    }

    [Fact]
    public void WorldState_ApplyDone_KeepsInviteWhenContextMismatched()
    {
        var ws = new WorldState();
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(FellowshipType, 7u, "join?"));
        // A stale Done for a superseded context must not drop the newer pending invite.
        var cleared = ws.ApplyConfirmationDone(new ConfirmationDonePayload(FellowshipType, 6u));
        Assert.False(cleared);
        Assert.NotNull(ws.PendingFellowshipInvite);
        Assert.Equal(7u, ws.PendingFellowshipInvite!.Context);
    }

    [Fact]
    public void WorldState_ApplyDone_IgnoresNonFellowshipType()
    {
        var ws = new WorldState();
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(FellowshipType, 7u, "join?"));
        var cleared = ws.ApplyConfirmationDone(new ConfirmationDonePayload(0x01u, 7u));
        Assert.False(cleared);
        Assert.NotNull(ws.PendingFellowshipInvite);
    }

    [Fact]
    public void WorldState_ClearPendingInvite_DropsIt()
    {
        var ws = new WorldState();
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(FellowshipType, 7u, "join?"));
        Assert.True(ws.ClearPendingFellowshipInvite());
        Assert.Null(ws.PendingFellowshipInvite);
        Assert.False(ws.ClearPendingFellowshipInvite());  // idempotent no-op
    }

    [Fact]
    public void WorldState_JoiningFellowship_ClearsPendingInvite()
    {
        var ws = new WorldState();
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(FellowshipType, 7u, "join?"));
        var members = new List<FellowMember>
        {
            new(0x50000001u, 5u, 100u, 100u, 100u, 100u, 100u, 100u, "Leader"),
        };
        ws.ApplyFellowshipFullUpdate(
            new FellowshipFullUpdatePayload(members, "Team", 0x50000001u, true, true, false, false));
        Assert.Null(ws.PendingFellowshipInvite);
    }

    // ---- FellowshipInviteResponse.Decide ----

    [Fact]
    public void Decide_WithPendingInvite_AcceptsWithEchoedContext()
    {
        var r = FellowshipInviteResponse.Decide(new PendingFellowshipInvite(123u, "join?"));
        Assert.Equal(FellowshipInviteResponse.ResultKind.Accept, r.Kind);
        Assert.Equal(123u, r.Context);
        Assert.Null(r.FailReason);
    }

    [Fact]
    public void Decide_WithNoPendingInvite_Fails()
    {
        var r = FellowshipInviteResponse.Decide(null);
        Assert.Equal(FellowshipInviteResponse.ResultKind.Fail, r.Kind);
        Assert.Equal("no pending fellowship invite", r.FailReason);
    }

    // ---- prompt cue: ## Fellowship invite ----

    private static WorldStateProjection ProjectionWith(PendingFellowshipInviteProjection? invite) => new()
    {
        Self = new SelfProjection { Guid = 0x5000000Bu, Name = "Headless", HealthFraction = 1.0f },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = Array.Empty<VisibleObjectProjection>(),
        PendingFellowshipInvite = invite,
    };

    [Fact]
    public void Prompt_RendersInviteCue_WhenPending()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            ProjectionWith(new PendingFellowshipInviteProjection { Text = "Alcott invites you" }),
            new EventStream(), null);
        Assert.Contains("## Fellowship invite", prompt);
        Assert.Contains("FellowshipAccept", prompt);
        Assert.Contains("Alcott invites you", prompt);
    }

    [Fact]
    public void Prompt_OmitsInviteCue_WhenNonePending()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            ProjectionWith(null), new EventStream(), null);
        Assert.DoesNotContain("## Fellowship invite", prompt);
    }
}
