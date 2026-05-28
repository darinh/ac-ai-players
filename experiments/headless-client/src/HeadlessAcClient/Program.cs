// SPDX-License-Identifier: AGPL-3.0-or-later
// Phase 1 driver entry point. Usage:
//
//   HeadlessAcClient <host> <port> <account> <password>
//
// Performs the AC three-way login handshake and prints the seeds
// + cookie it received. Exits 0 on full handshake (server replied
// with at least one post-handshake packet), 1 on handshake failure.
//
// Before connecting to ACE, also pings the API host at
// AC_BOTS_API_URL (default http://127.0.0.1:9100/). If the API
// host is unreachable, prints a warning and continues — the spike
// is intentionally tolerant of a missing API host because Phase 1
// of the spike doesn't *need* the API yet.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using AcAiPlayers.Services;

using HeadlessAcClient.Protocol;

namespace HeadlessAcClient;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: HeadlessAcClient <host> <port> <account> <password>");
            return 2;
        }

        var host = IPAddress.Parse(args[0]);
        var port = int.Parse(args[1]);
        var account = args[2];
        var password = args[3];

        Console.WriteLine($"[main] target {host}:{port}, account '{account}'");

        try
        {
            AcStrings.RunSelfChecks();
            OutboundSelfCheck.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[selfcheck] AcStrings: {ex.Message}");
            return 1;
        }

        await PingApiAsync().ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var driver = new HandshakeDriver(host, port, account, password);
        try
        {
            var result = await driver.RunAsync(cts.Token).ConfigureAwait(false);
            if (result.PostHandshakePacketSeen)
            {
                var hasMessages = result.DDDInterrogation is not null
                    || result.CharacterList is not null
                    || result.ServerName is not null;
                if (hasMessages)
                {
                    Console.WriteLine("[main] PHASE 2 PASS — handshake + crypto + keepalive + game-message decode all working.");
                }
                else
                {
                    Console.WriteLine("[main] PHASE 1 PASS — handshake completed; no decodable game messages observed (yet).");
                }
                return 0;
            }
            Console.WriteLine("[main] PHASE 1 PARTIAL — got ConnectRequest from server but no post-handshake packet within timeout.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[main] PHASE 1 FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task PingApiAsync()
    {
        try
        {
            using var api = new ApiClient(ApiClientOptions.FromEnvironment());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var health = await api.GetHealthAsync(cts.Token).ConfigureAwait(false);
            Console.WriteLine($"[api] {health.Service} v{health.Version}: {health.Status}");

            var plan = await api.PlanAsync(
                new AcAiPlayers.Services.Contracts.PlanRequest
                {
                    Goal = "phase-1 smoke test",
                    VocabularyVersion = "v1",
                    Perception = null,
                },
                botId: Guid.NewGuid(),
                cts.Token).ConfigureAwait(false);
            Console.WriteLine($"[api] plan id={plan.PlanId[..8]}.. model={plan.Model} ops={plan.Ops.Length} first={plan.Ops[0].Op}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[api] WARNING: API host unreachable: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("[api] continuing without API; Phase 1 spike doesn't require it");
        }
    }
}
