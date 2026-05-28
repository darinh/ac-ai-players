// SPDX-License-Identifier: AGPL-3.0-or-later
// ApiHost — HTTP/2 h2c Kestrel + Minimal API + MessagePack.
// Bind: AC_BOTS_API_BIND (default 127.0.0.1:9100)
// Auth: AC_BOTS_API_TOKEN (default dev-insecure-token; logged on startup)
//
// Endpoints:
//   GET  /v1/health          JSON   (no auth)
//   POST /v1/llm/plan        msgpack (stub: canned plan)

using System;
using System.Net;
using System.Threading.Tasks;

using AcAiPlayers.ApiHost.Auth;
using AcAiPlayers.ApiHost.Endpoints;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AcAiPlayers.ApiHost;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var bindRaw = Environment.GetEnvironmentVariable("AC_BOTS_API_BIND") ?? "127.0.0.1:9100";
        var parts = bindRaw.Split(':', 2);
        var bindHost = parts[0];
        var bindPort = parts.Length > 1 ? int.Parse(parts[1]) : 9100;
        var h2cPort = bindPort + 1;

        var token = Environment.GetEnvironmentVariable("AC_BOTS_API_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine("[api-host] WARNING: AC_BOTS_API_TOKEN not set; using dev-insecure-token");
            token = "dev-insecure-token";
        }

        var builder = WebApplication.CreateSlimBuilder(args);

        builder.WebHost.ConfigureKestrel(opt =>
        {
            opt.ListenAnyIP(bindPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
            });
            opt.ListenAnyIP(h2cPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });
            opt.AddServerHeader = false;
        });

        builder.Services.AddSingleton(new BearerTokenAuthenticator(token));

        var app = builder.Build();

        HealthEndpoints.Map(app);
        LlmEndpoints.Map(app);

        Console.WriteLine($"[api-host] http/1.1 (debug)      on http://{bindHost}:{bindPort}/");
        Console.WriteLine($"[api-host] http/2  h2c (hot path) on http://{bindHost}:{h2cPort}/");
        Console.WriteLine($"[api-host] token={(token == "dev-insecure-token" ? "dev-insecure-token (INSECURE)" : "<set>")}");

        await app.RunAsync().ConfigureAwait(false);
    }
}
