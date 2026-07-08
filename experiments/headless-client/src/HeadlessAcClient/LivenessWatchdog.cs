// SPDX-License-Identifier: AGPL-3.0-or-later
// LivenessWatchdog — a process-level liveness detector that force-exits the bot
// when it stops making REAL progress (receiving server packets), so the restart
// supervisor can relaunch a fresh process.
//
// Motivation (live incidents): the network client can wedge in two ways the
// restart supervisor (which only relaunches on process EXIT) cannot recover:
//   1. BLOCKED loop — the observe/tick loop blocks inside its body on a
//      network/LLM `await` that never returns; the process stays alive but does
//      nothing (a bot was found frozen mid-walk for ~2.7 hours).
//   2. ZOMBIE loop — the server session dies (server stops sending) but the loop
//      keeps ITERATING quietly (the 250ms walk-tick timer wakes it, minimal CPU,
//      no packets, no actions); a bot sat in this state ~15 min. A heartbeat that
//      bumps on every loop ITERATION is defeated by this case, because the loop
//      IS iterating — it is just not making progress.
// An IN-loop watchdog cannot help either case (the loop is blocked, or spinning
// uselessly), so only a SEPARATE thread can detect the stall and force the exit.
//
// The fix for BOTH: the heartbeat means "the server session delivered a packet"
// (real progress / proof the connection is live), NOT "the loop iterated". The
// caller bumps it on each inbound CRC-valid datagram; a blocked loop processes no
// packets and a zombie session receives none, so either wedge ages the heartbeat
// past the timeout and the monitor thread force-exits. Pure process liveness —
// no game knowledge, no decision-making, no target choice.

using System;
using System.Threading;

namespace HeadlessAcClient;

internal static class LivenessWatchdog
{
    // Heartbeat: a MONOTONIC timestamp (Environment.TickCount64 — milliseconds
    // since boot, immune to wall-clock/NTP/VM time corrections) of the last
    // recorded REAL progress (an inbound server packet). Written by the loop thread
    // and read by the monitor thread; a single 64-bit long read/write is atomic,
    // and Volatile guards visibility/ordering so the monitor never sees a torn or
    // stale value. Monotonic (not DateTime.UtcNow): a forward clock jump larger than
    // the timeout must not make a healthy bot look stalled and force-exit, and a
    // backward jump must not mask a real stall.
    private static long _lastProgressMs = Environment.TickCount64;
    private static int _started; // 0/1 guard so Start is idempotent.

    // Default stall timeout: generously ABOVE any legitimate single loop iteration
    // (an LLM deliberation is a few seconds; the throttle floor is ~10s) yet far
    // BELOW the multi-hour freeze it guards against. Env override
    // AC_BOTS_STALL_TIMEOUT_SECONDS (clamped [MinStallTimeoutSeconds, 3600]).
    public const int DefaultStallTimeoutSeconds = 300;

    // Minimum stall timeout. The heartbeat bumps on an inbound server packet inside
    // the observe loop, so a legitimate reconnect/backoff/handshake recovery (bounded
    // by the driver's reconnect constants, ~165s worst case — during which the game
    // socket delivers no packets) leaves the heartbeat un-bumped for that whole
    // window. The floor sits safely ABOVE that gap so no env override can make a
    // normal reconnect look like a stall.
    public const int MinStallTimeoutSeconds = 200;

    // Exit code the supervisor sees on a stall-exit — distinct from a clean 0 so a
    // stall is greppable in the supervisor ledger. The supervisor relaunches on ANY
    // exit, so the exact value is informational.
    public const int StallExitCode = 42;

    // Grace period after the watchdog requests a graceful exit before it escalates
    // to a HARD kill. Environment.Exit runs finalizers + module unload, which can
    // themselves BLOCK on a wedged process (leaving it alive despite the exit
    // request); the hard-kill guard guarantees termination so the supervisor always
    // relaunches.
    public const int HardKillGraceSeconds = 10;

    /// <summary>
    /// Record REAL progress (an inbound server packet was received) — bump the
    /// monotonic heartbeat. Call this on genuine session activity, NOT on every loop
    /// iteration: a loop that keeps iterating with a dead session (no packets) must
    /// still age the heartbeat so the monitor can force-exit the zombie.
    /// </summary>
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
    /// on a stall, invokes <paramref name="onStall"/> (default: log to stderr, arm a
    /// hard-kill guard, then Environment.Exit(<see cref="StallExitCode"/>) — so the
    /// process terminates even if graceful exit blocks). Idempotent — a second call is
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
            Console.Error.Flush();
            // Guarantee termination: Environment.Exit runs finalizers/module unload,
            // which can themselves block on a wedged process. Arm a background hard-kill
            // that fires after the grace period regardless, THEN request the graceful
            // exit — whichever completes first ends the process so the supervisor relaunches.
            var killer = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(HardKillGraceSeconds));
                try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
                catch { /* nothing left to do if even Kill fails */ }
            })
            { IsBackground = true, Name = "liveness-watchdog-hardkill" };
            killer.Start();
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
                    stall($"[watchdog] NO PROGRESS (no inbound packet) for {ageSeconds}s " +
                          $"(timeout {(int)timeout.TotalSeconds}s) — session wedged/dead; force-exiting so the supervisor relaunches");
                    // onStall normally exits and never returns; if a test hook returns
                    // instead, stop the monitor rather than spin.
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
