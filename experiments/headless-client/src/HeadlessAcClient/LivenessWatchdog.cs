// SPDX-License-Identifier: AGPL-3.0-or-later
// LivenessWatchdog — a process-level stall detector that force-exits the bot
// when its main loop stops making progress, so the restart supervisor can
// relaunch a fresh process.
//
// Motivation (live incident): the observe/tick loop can BLOCK inside its body —
// most often on a network/LLM `await` that never returns (a stuck TCP
// connection the outer multi-hour CancellationToken does not promptly cancel).
// A blocked loop stops producing output but the PROCESS stays alive, so the
// supervisor (which only relaunches on process EXIT) cannot recover it: a bot
// was found frozen mid-walk for ~2.7 hours. An IN-loop watchdog cannot help —
// the loop that would run it is itself blocked — so only a SEPARATE thread can
// detect the stall and force the process to exit.
//
// This runs a background thread that reads a heartbeat the main loop bumps
// each iteration and, when the heartbeat is older than the stall timeout, logs
// and calls Environment.Exit so the supervisor relaunches. Pure process
// liveness — no game knowledge, no decision-making, no target choice.

using System;
using System.Threading;

namespace HeadlessAcClient;

internal static class LivenessWatchdog
{
    // Heartbeat: a MONOTONIC timestamp (Environment.TickCount64 — milliseconds
    // since boot, immune to wall-clock/NTP/VM time corrections) of the last
    // recorded main-loop progress. Written by the loop thread and read by the
    // monitor thread; a single 64-bit long read/write is atomic, and Volatile
    // guards visibility/ordering so the monitor never sees a torn or stale value.
    // Monotonic (not DateTime.UtcNow): a forward clock jump larger than the timeout
    // must not make a healthy bot look stalled and force-exit, and a backward jump
    // must not mask a real stall.
    private static long _lastProgressMs = Environment.TickCount64;
    private static int _started; // 0/1 guard so Start is idempotent.

    // Default stall timeout: generously ABOVE any legitimate single loop iteration
    // (an LLM deliberation is a few seconds; the throttle floor is ~10s) yet far
    // BELOW the multi-hour freeze it guards against. Env override
    // AC_BOTS_STALL_TIMEOUT_SECONDS (clamped [MinStallTimeoutSeconds, 3600]).
    public const int DefaultStallTimeoutSeconds = 300;

    // Minimum stall timeout. RecordProgress runs ONLY inside the observe loop, so a
    // legitimate reconnect/backoff/handshake recovery (bounded by the driver's
    // reconnect constants, ~165s worst case) leaves the heartbeat un-bumped for that
    // whole window. The floor sits safely ABOVE that gap so no env override can make
    // a normal reconnect look like a stall.
    public const int MinStallTimeoutSeconds = 200;

    // Exit code the supervisor sees on a stall-exit — distinct from a clean 0 so a
    // stall is greppable in the supervisor ledger. The supervisor relaunches on ANY
    // exit, so the exact value is informational.
    public const int StallExitCode = 42;

    /// <summary>Record that the main loop made progress (bump the monotonic heartbeat).</summary>
    public static void RecordProgress()
        => Volatile.Write(ref _lastProgressMs, Environment.TickCount64);

    /// <summary>Current heartbeat (monotonic ms). Test/diagnostic seam.</summary>
    internal static long LastProgressMs => Volatile.Read(ref _lastProgressMs);

    /// <summary>
    /// Pure stall predicate (extracted for testing): true when the elapsed MONOTONIC
    /// milliseconds between <paramref name="nowMs"/> and the recorded heartbeat
    /// exceed <paramref name="timeout"/>. Both timestamps come from the same
    /// monotonic source (Environment.TickCount64).
    /// </summary>
    public static bool IsStalled(long lastProgressMs, long nowMs, TimeSpan timeout)
        => nowMs - lastProgressMs > (long)timeout.TotalMilliseconds;

    /// <summary>
    /// Resolve the stall timeout from AC_BOTS_STALL_TIMEOUT_SECONDS, clamped to
    /// [<see cref="MinStallTimeoutSeconds"/>, 3600s]; falls back to
    /// <see cref="DefaultStallTimeoutSeconds"/> when the var is absent or
    /// unparseable. Pure env read.
    /// </summary>
    public static TimeSpan ResolveStallTimeout(string? raw)
    {
        if (int.TryParse(raw, out var s) && s > 0)
            return TimeSpan.FromSeconds(Math.Clamp(s, MinStallTimeoutSeconds, 3600));
        return TimeSpan.FromSeconds(DefaultStallTimeoutSeconds);
    }

    /// <summary>
    /// Start the background stall monitor ONCE. Records an initial heartbeat, then a
    /// background thread checks staleness every <paramref name="pollInterval"/> and,
    /// on a stall, invokes <paramref name="onStall"/> (default: log to stderr +
    /// Environment.Exit(<see cref="StallExitCode"/>)). Idempotent — a second call is
    /// a no-op. The monitor thread is a background thread so it never blocks a normal
    /// process exit.
    /// </summary>
    public static void Start(
        TimeSpan? stallTimeout = null,
        TimeSpan? pollInterval = null,
        Action<string>? log = null,
        Action<string>? onStall = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var timeout = stallTimeout
            ?? ResolveStallTimeout(Environment.GetEnvironmentVariable("AC_BOTS_STALL_TIMEOUT_SECONDS"));
        var poll = pollInterval ?? TimeSpan.FromSeconds(15);
        var write = log ?? Console.WriteLine;
        var stall = onStall ?? (msg =>
        {
            Console.Error.WriteLine(msg);
            Environment.Exit(StallExitCode);
        });
        RecordProgress();
        write($"[watchdog] liveness monitor started: stall timeout {(int)timeout.TotalSeconds}s, poll {(int)poll.TotalSeconds}s");
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(poll);
                var last = Volatile.Read(ref _lastProgressMs);
                var now = Environment.TickCount64;
                if (IsStalled(last, now, timeout))
                {
                    var ageSeconds = (int)Math.Max(0L, (now - last) / 1000L);
                    stall($"[watchdog] MAIN LOOP STALLED — no progress for {ageSeconds}s " +
                          $"(timeout {(int)timeout.TotalSeconds}s); force-exiting so the supervisor relaunches");
                    // onStall normally Environment.Exit's and never returns; if a test
                    // hook returns instead, stop the monitor rather than spin.
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "liveness-watchdog",
        };
        t.Start();
    }
}
