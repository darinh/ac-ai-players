// SPDX-License-Identifier: AGPL-3.0-or-later
// GracefulShutdown — a file-triggered request to end the observe window CLEANLY.
//
// Motivation (live incident): a deploy stops the running bot with a hard
// process kill (Stop-Process). A hard kill during a mid-inventory operation
// strands the character's server-side inventory in a limbo state; after enough
// abrupt kills the server rejects EVERY wield on the next login (armed=False,
// zero WieldObject acks) even though the character otherwise loads and plays.
// The recovery is a full ACEServer restart. The prevention is to let the bot
// log off CLEANLY before it is stopped: the observe window already sends a
// CharacterLogOff (0xF653) at a graceful window-end (see HandshakeDriver's
// ShouldSendCleanLogoff), so the server frees the character in good order.
//
// A hard kill cannot be intercepted (no signal handler fires for Stop-Process
// on Windows), so a deploy instead CREATES a small request file; the bot polls
// for it and, on seeing it, cancels the observe token — the SAME cancellation
// the 24h run budget uses, which reaches the clean-logoff path — then exits so
// the supervisor relaunch (or the deploy) proceeds against a freed character.
//
// Pure path/existence helpers only; the poll loop and cancellation live in
// Program.cs. No game knowledge, no decision-making.

using System;
using System.IO;

namespace HeadlessAcClient;

internal static class GracefulShutdown
{
    /// <summary>
    /// Environment variable naming the shutdown-request file. When set to a
    /// non-blank path, the bot watches that path and logs off cleanly when the
    /// file appears. Unset/blank disables the watch (default behaviour).
    /// </summary>
    internal const string ShutdownFileEnvVar = "AC_BOTS_SHUTDOWN_FILE";

    /// <summary>
    /// Resolve the configured shutdown-request file path, or <c>null</c> when the
    /// watch is disabled (env unset, blank, or whitespace-only). Trims surrounding
    /// whitespace so a stray trailing space in the env value cannot silently point
    /// the watch at a different path than the deploy writes.
    /// </summary>
    internal static string? ResolveShutdownFilePath(string? envValue)
        => string.IsNullOrWhiteSpace(envValue) ? null : envValue.Trim();

    /// <summary>
    /// True when a graceful shutdown has been requested: the watch is enabled
    /// (non-blank <paramref name="path"/>) AND the request file exists AND it was
    /// written at/after <paramref name="processStartUtc"/> (this run started).
    ///
    /// The write-time guard is what makes the feature safe against a leftover
    /// request file. A file written by a PRIOR run (or one a deploy could not
    /// clear) has an older write time and is IGNORED — so a stale or undeletable
    /// file can never shut down or crash-loop a fresh run. Only a deploy writing
    /// the file NOW (after this launch) triggers the clean logoff. A null/blank
    /// path (watch disabled) is never a shutdown request.
    /// </summary>
    internal static bool IsShutdownRequested(string? path, DateTime processStartUtc)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            if (!File.Exists(path))
                return false;
            return File.GetLastWriteTimeUtc(path) >= processStartUtc;
        }
        catch
        {
            // A file that vanished between the Exists check and the stat, or is
            // momentarily unreadable, is not a shutdown request.
            return false;
        }
    }
}
