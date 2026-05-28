// SPDX-License-Identifier: AGPL-3.0-or-later
// Phase 1 driver entry point. Usage:
//
//   HeadlessAcClient <host> <port> <account> <password>
//
// Performs the AC three-way login handshake and prints the seeds
// + cookie it received. Exits 0 on full handshake (server replied
// with at least one post-handshake packet), 1 on handshake failure.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var driver = new HandshakeDriver(host, port, account, password);
        try
        {
            var result = await driver.RunAsync(cts.Token).ConfigureAwait(false);
            if (result.PostHandshakePacketSeen)
            {
                Console.WriteLine("[main] PHASE 1 PASS — server kept talking to us after the handshake.");
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
}
