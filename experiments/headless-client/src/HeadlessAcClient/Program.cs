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
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ACE.DatLoader;

using AcAiPlayers.Services;
using AcAiPlayers.WorldNav;

using HeadlessAcClient.Protocol;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;

namespace HeadlessAcClient;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Install top-level crash sinks FIRST so a crash on ANY thread (incl. a
        // background thread or an unobserved Task that bypasses the try/catch below)
        // logs its full stack instead of ending the run silently. See CrashDiagnostics.
        CrashDiagnostics.Install();

        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: HeadlessAcClient <host> <port> <account> <password> [character-name]");
            return 2;
        }

        var host = IPAddress.Parse(args[0]);
        var port = int.Parse(args[1]);
        var account = args[2];
        var password = args[3];
        var characterName = args.Length >= 5 ? args[4] : null;

        Console.WriteLine($"[main] target {host}:{port}, account '{account}', character '{characterName ?? "Headless01"}'");

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

        var indoorNav = TryInitIndoorNav();
        var contractCatalog = TryInitContractCatalog();

        // Outer cancellation budget. Must exceed HandshakeDriver.ObserveSeconds
        // (the per-run observe loop budget) plus the worst-case pre-observe
        // handshake/login-reconnect overhead. Derived directly from ObserveSeconds +
        // OuterBudgetHeadroomSeconds (the latter computed from the reconnect
        // constants), so a long-run override (AC_BOTS_OBSERVE_SECONDS, e.g.
        // 86400 = 24h) lifts BOTH bounds together instead of silently capping the
        // run. With the default observe budget the outer budget is 3600 + 165 = 3765s.
        // Previously hard-coded to 200s, which silently killed long-running
        // observations mid-cycle and exited the outer loop BEFORE the picker could
        // send its next walk — symptom: runs always terminated at ~3min 20s
        // regardless of ObserveSeconds. See spec/12 future ops notes.
        var outerBudgetSeconds = HandshakeDriver.ObserveSeconds + HandshakeDriver.OuterBudgetHeadroomSeconds;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(outerBudgetSeconds));
        using var driver = new HandshakeDriver(host, port, account, password, characterName, indoorNav, contractCatalog);
        try
        {
            var result = await driver.RunAsync(cts.Token).ConfigureAwait(false);
            if (result.PostHandshakePacketSeen)
            {
                var hasMessages = result.DDDInterrogation is not null
                    || result.CharacterList is not null
                    || result.ServerName is not null;
                var charCreateOk = result.CharacterCreateResponse is { Response: CharacterCreateResponse.Ok };
                var serverReady = result.EnterWorldServerReady is not null;

                // Phase 3.3 takes top priority: full two-step world entry
                // observed (ServerReady received + EnterWorld committed).
                if (result.EnterWorldSent && serverReady)
                {
                    Console.WriteLine($"[main] PHASE 3.3 PASS — EnterWorld two-step handshake committed (guid=0x{result.ChosenCharacterGuid:X8}); world-state firehose should follow.");
                    if (result.LastCharacterError is { } cerr)
                        Console.WriteLine($"[main] PHASE 3.3 NOTE — post-EnterWorld CharacterError observed: code=0x{cerr.ErrorCode:X4}");
                }
                else if (result.EnterWorldRequestSent && serverReady)
                {
                    Console.WriteLine($"[main] PHASE 3.3 PARTIAL — ServerReady received but EnterWorld send failed/skipped (guid=0x{result.ChosenCharacterGuid:X8})");
                }
                else if (result.EnterWorldRequestSent)
                {
                    if (result.LastCharacterError is { } cerr)
                        Console.WriteLine($"[main] PHASE 3.3 PARTIAL — EnterWorldRequest sent; server replied CharacterError code=0x{cerr.ErrorCode:X4}");
                    else
                        Console.WriteLine($"[main] PHASE 3.3 PARTIAL — EnterWorldRequest sent but no ServerReady/CharacterError received within window");
                }
                else if (charCreateOk)
                {
                    var ccr = result.CharacterCreateResponse!;
                    Console.WriteLine($"[main] PHASE 3.2 PASS — CharacterCreate accepted: guid=0x{ccr.CharacterGuid:X8} name=\"{ccr.Name}\"");
                }
                else if (result.CharacterCreateResponse is { } ccrErr)
                {
                    Console.WriteLine($"[main] PHASE 3.2 PARTIAL — CharacterCreate sent, server replied {ccrErr.Response} (code={(uint)ccrErr.Response})");
                }
                else if (hasMessages)
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
            // Full stack (ToString), not just the message — a run that dies here must
            // leave a diagnosable trace (the run-end cause was previously invisible).
            Console.Error.WriteLine($"[main] PHASE 1 FAIL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"[main] PHASE 1 FAIL stack: {ex}");
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

    /// <summary>
    /// Initialise the indoor-nav service (Phase 3 integration of
    /// AcAiPlayers.WorldNav). Gated on the AC_INDOOR_NAV env var
    /// (default "1" — opt-OUT, not opt-in, so the spike loop
    /// exercises the new code path by default). DAT directory
    /// comes from AC_DAT_DIR (default "C:\ACE\Dats").
    ///
    /// Returns a disabled service if anything goes wrong (env var
    /// off, DAT dir missing, DatManager init throws). The
    /// headless client keeps its existing straight-line walk
    /// behaviour when indoor-nav is disabled.
    /// </summary>
    private static IndoorNavService TryInitIndoorNav()
    {
        var flag = Environment.GetEnvironmentVariable("AC_INDOOR_NAV");
        if (flag == "0" || string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[indoor-nav] disabled (AC_INDOOR_NAV=0)");
            return new IndoorNavService();
        }

        var datDir = Environment.GetEnvironmentVariable("AC_DAT_DIR");
        if (string.IsNullOrWhiteSpace(datDir))
            datDir = @"C:\ACE\Dats";

        if (!System.IO.Directory.Exists(datDir))
        {
            Console.WriteLine($"[indoor-nav] disabled (DAT directory not found: {datDir})");
            return new IndoorNavService();
        }

        try
        {
            // ACE.DatLoader reads Windows-1252 strings via
            // Encoding.GetEncoding(1252); .NET Core only ships UTF
            // + a couple of ASCII variants in the base runtime.
            // System.Text.Encoding.CodePages adds the rest; we have
            // to register its provider before any DAT file with
            // embedded text is read.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            DatManager.Initialize(datDir, keepOpen: true, loadCell: true);

            var loader = new LandblockNavLoader(DatManager.CellDat, DatManager.PortalDat);
            var hops = ParseExpansionHops();
            Console.WriteLine($"[indoor-nav] enabled (DAT dir: {datDir}, expansion-hops={hops})");
            return new IndoorNavService(loader, expansionHops: hops);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[indoor-nav] FAILED to initialise: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("[indoor-nav] continuing with indoor-nav disabled");
            return new IndoorNavService();
        }
    }

    /// <summary>
    /// Build the contract catalog from the portal dat, independently of
    /// indoor-nav. A tracked contract's objective lives in client_portal.dat's
    /// ContractTable; reading it lets the bot project what an opaque contract
    /// id REQUIRES. Reuses the DATs indoor-nav already opened when present;
    /// otherwise opens the portal dat here (portal-only — contracts need no
    /// cell data) so enrichment works even when AC_INDOOR_NAV is disabled.
    /// Returns an empty catalog (lookups miss, prompt falls back to raw id +
    /// stage) when the DAT directory is absent or a read fails.
    /// </summary>
    private static IContractCatalog TryInitContractCatalog()
    {
        var datDir = Environment.GetEnvironmentVariable("AC_DAT_DIR");
        if (string.IsNullOrWhiteSpace(datDir))
            datDir = @"C:\ACE\Dats";

        if (!System.IO.Directory.Exists(datDir))
        {
            Console.WriteLine($"[contracts] catalog disabled (DAT directory not found: {datDir})");
            return new ContractCatalog();
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            // Reuse the DATs indoor-nav already opened; otherwise open the
            // portal dat here. loadCell:false — contracts need no cell data.
            if (DatManager.PortalDat is null)
                DatManager.Initialize(datDir, keepOpen: true, loadCell: false);

            var catalog = ContractCatalog.FromPortalDat(DatManager.PortalDat);
            Console.WriteLine($"[contracts] catalog loaded ({catalog.Count} definitions)");
            return catalog;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[contracts] catalog disabled ({ex.GetType().Name}: {ex.Message})");
            return new ContractCatalog();
        }
    }

    /// <summary>
    /// Parses AC_INDOOR_NAV_HOPS (default 4). Clamps to 0..16 so a
    /// typo can't blow up the BFS frontier on a 568-cell landblock.
    /// </summary>
    private static int ParseExpansionHops()
    {
        var raw = Environment.GetEnvironmentVariable("AC_INDOOR_NAV_HOPS");
        if (string.IsNullOrWhiteSpace(raw))
            return 4;
        if (!int.TryParse(raw, out var hops))
            return 4;
        return Math.Clamp(hops, 0, 16);
    }
}
