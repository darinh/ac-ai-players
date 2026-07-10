// SPDX-License-Identifier: AGPL-3.0-or-later
// allegiance-request-accept: coverage for the capability that lets a monarch bot
// APPROVE a vassal's swear-allegiance request (the accepting side of criterion 6).
//   1. WorldState tracks a pending SwearAllegiance (0x01) confirmation separately from
//      a Fellowship (0x04) one; clears on the matching Done / on a sent response
//   2. type isolation: a Fellowship Done never clears an allegiance request (and v.v.)
//   3. AllegianceRequestResponse.Decide: approve-with-context vs fail-when-none
//   4. the "## Allegiance request" prompt cue (directive for a configured teammate,
//      optional otherwise, absent when none pending)

using System;
using System.Collections.Generic;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class AllegianceRequestAcceptTests
{
    private const uint FellowshipType = 0x04;
    private const uint SwearAllegianceType = 0x01;

    // ---- WorldState: store / clear ----

    [Fact]
    public void ApplyRequest_StoresSwearAllegianceSeparatelyFromFellowship()
    {
        var ws = new WorldState();
        Assert.True(ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(SwearAllegianceType, 5u, "Vassal")));
        Assert.NotNull(ws.PendingAllegianceRequest);
        Assert.Equal(5u, ws.PendingAllegianceRequest!.Context);
        Assert.Equal("Vassal", ws.PendingAllegianceRequest.Text);
        Assert.Null(ws.PendingFellowshipInvite);   // did not populate the fellowship slot
    }

    [Fact]
    public void ApplyRequest_IgnoresUnhandledConfirmationTypes()
    {
        var ws = new WorldState();
        // AlterAttribute (0x03) is not a tracked prompt kind.
        Assert.False(ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(0x03u, 5u, "x")));
        Assert.Null(ws.PendingAllegianceRequest);
        Assert.Null(ws.PendingFellowshipInvite);
    }

    [Fact]
    public void ApplyDone_ClearsMatchingAllegianceContext()
    {
        var ws = new WorldState();
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(SwearAllegianceType, 5u, "Vassal"));
        Assert.True(ws.ApplyConfirmationDone(new ConfirmationDonePayload(SwearAllegianceType, 5u)));
        Assert.Null(ws.PendingAllegianceRequest);
    }

    [Fact]
    public void ApplyDone_KeepsRequestWhenContextMismatched()
    {
        var ws = new WorldState();
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(SwearAllegianceType, 5u, "Vassal"));
        Assert.False(ws.ApplyConfirmationDone(new ConfirmationDonePayload(SwearAllegianceType, 4u)));
        Assert.NotNull(ws.PendingAllegianceRequest);
    }

    [Fact]
    public void ConfirmationTypes_AreIsolated()
    {
        // A Fellowship Done must not clear a pending allegiance request, and vice versa.
        var ws = new WorldState();
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(SwearAllegianceType, 5u, "Vassal"));
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(FellowshipType, 5u, "Inviter"));
        Assert.NotNull(ws.PendingAllegianceRequest);
        Assert.NotNull(ws.PendingFellowshipInvite);

        Assert.False(ws.ApplyConfirmationDone(new ConfirmationDonePayload(FellowshipType, 5u)) && ws.PendingAllegianceRequest is null);
        Assert.NotNull(ws.PendingAllegianceRequest);   // fellowship Done left allegiance intact
        Assert.Null(ws.PendingFellowshipInvite);       // but cleared the fellowship one

        Assert.True(ws.ApplyConfirmationDone(new ConfirmationDonePayload(SwearAllegianceType, 5u)));
        Assert.Null(ws.PendingAllegianceRequest);
    }

    [Fact]
    public void ClearPendingAllegianceRequest_DropsIt()
    {
        var ws = new WorldState();
        ws.ApplyConfirmationRequest(new ConfirmationRequestPayload(SwearAllegianceType, 5u, "Vassal"));
        Assert.True(ws.ClearPendingAllegianceRequest());
        Assert.Null(ws.PendingAllegianceRequest);
        Assert.False(ws.ClearPendingAllegianceRequest());   // idempotent
    }

    // ---- AllegianceRequestResponse.Decide ----

    [Fact]
    public void Decide_WithPendingRequest_ApprovesWithEchoedContext()
    {
        var r = AllegianceRequestResponse.Decide(new PendingAllegianceRequest(77u, "Vassal"));
        Assert.Equal(AllegianceRequestResponse.ResultKind.Approve, r.Kind);
        Assert.Equal(77u, r.Context);
        Assert.Null(r.FailReason);
    }

    [Fact]
    public void Decide_WithNoPendingRequest_Fails()
    {
        var r = AllegianceRequestResponse.Decide(null);
        Assert.Equal(AllegianceRequestResponse.ResultKind.Fail, r.Kind);
        Assert.Equal("no pending allegiance request", r.FailReason);
    }

    // ---- prompt cue: ## Allegiance request ----

    private static WorldStateProjection ProjectionWithPendingSwear(string? requestText) =>
        new()
        {
            Self = new SelfProjection { Guid = 0x5000000Eu, Name = "Monarch", HealthFraction = 1.0f },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = Array.Empty<VisibleObjectProjection>(),
            PendingAllegianceRequest = requestText is null
                ? null
                : new PendingAllegianceRequestProjection { Text = requestText },
        };

    private static string Section(string prompt, string header)
    {
        int start = prompt.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int next = prompt.IndexOf("\n## ", start + header.Length, StringComparison.Ordinal);
        return next < 0 ? prompt.Substring(start) : prompt.Substring(start, next - start);
    }

    [Fact]
    public void Prompt_SwearFromConfiguredTeammate_IsDirective()
    {
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            ProjectionWithPendingSwear("Vassal"), new EventStream(),
            new HashSet<string>(new[] { "Vassal" }, StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance request");
        Assert.Contains("SWEAR ALLEGIANCE", sec);
        Assert.Contains("AllegianceApprove", sec);
        Assert.Contains("DIRECTED team coordination", sec);
    }

    [Fact]
    public void Prompt_SwearFromNonTeammate_StaysOptional()
    {
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            ProjectionWithPendingSwear("Rando"), new EventStream(),
            new HashSet<string>(new[] { "Vassal" }, StringComparer.OrdinalIgnoreCase));
        var sec = Section(prompt, "## Allegiance request");
        Assert.Contains("AllegianceApprove", sec);
        Assert.Contains("OPTIONAL", sec);
        Assert.DoesNotContain("DIRECTED team coordination", sec);
    }

    [Fact]
    public void Prompt_OmitsCue_WhenNoRequestPending()
    {
        var prompt = LlmGoalPolicy.BuildUserPromptForTest(
            ProjectionWithPendingSwear(null), new EventStream(),
            new HashSet<string>(new[] { "Vassal" }, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("## Allegiance request", prompt);
    }
}
