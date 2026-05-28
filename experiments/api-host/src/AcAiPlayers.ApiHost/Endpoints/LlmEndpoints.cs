// SPDX-License-Identifier: AGPL-3.0-or-later
// /v1/llm/plan — MessagePack stub. Returns a canned "stand still"
// plan so bots can integrate the call site now and we can wire a
// real LLM behind this in Phase 2 without changing the contract.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AcAiPlayers.ApiHost.Auth;
using AcAiPlayers.Services.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using MessagePack;

namespace AcAiPlayers.ApiHost.Endpoints;

public static class LlmEndpoints
{
    private static readonly MessagePackSerializerOptions MsgPackOpts =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.None)
            .WithSecurity(MessagePackSecurity.UntrustedData);

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/llm/plan", HandlePlanAsync);
    }

    private static async Task HandlePlanAsync(HttpContext ctx, BearerTokenAuthenticator auth, CancellationToken ct)
    {
        if (!auth.TryAuthenticate(ctx, out var reason))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized", reason }, ct).ConfigureAwait(false);
            return;
        }

        PlanRequest? request;
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            request = MessagePackSerializer.Deserialize<PlanRequest>(ms.ToArray(), MsgPackOpts, ct);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "BadRequest", detail = ex.Message }, ct).ConfigureAwait(false);
            return;
        }

        var response = new PlanResponse
        {
            PlanId = Guid.NewGuid().ToString("N"),
            Vocabulary = "fetch",
            Model = "stub-canned",
            TraceId = ctx.Request.Headers["X-Request-Id"].ToString(),
            Ops = new[]
            {
                new PlanOp
                {
                    Op = "StandStill",
                    Args = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["goal"] = request?.Goal ?? "",
                        ["reason"] = "stub LLM — no real model wired yet",
                    },
                },
            },
        };

        var bytes = MessagePackSerializer.Serialize(response, MsgPackOpts, ct);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/x-msgpack";
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
    }
}
