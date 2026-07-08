// SPDX-License-Identifier: AGPL-3.0-or-later
// CrashDiagnostics — top-level crash/exit logging so a run that ends does not end
// SILENTLY.
//
// Motivation (live incident): deployed runs were observed ending abruptly
// mid-motion after only 6-22 minutes (the run budget is 24h) with NO logged
// cause — no clean `[main]` phase line, no exception, no death. Some ended by
// process EXIT (the supervisor relaunched), one HUNG (the LivenessWatchdog now
// force-exits that case). Diagnosing WHY the run ends is blocked because a crash
// on a BACKGROUND thread (or an unobserved Task) bypasses Program.Main's
// try/catch and terminates the process without printing anything useful.
//
// This installs the two process-global exception sinks (AppDomain.UnhandledException
// and TaskScheduler.UnobservedTaskException) so the NEXT such crash logs its full
// type + message + STACK, and provides pure formatters so the log text is unit
// tested. Pure diagnostics/logging — no game knowledge, no decision-making.

using System;
using System.Threading.Tasks;

namespace HeadlessAcClient;

internal static class CrashDiagnostics
{
    private static int _installed; // 0/1 guard so Install is idempotent.

    /// <summary>
    /// Best-effort stringify that never throws — a hostile/buggy <c>ToString()</c> (on an
    /// exception or a non-Exception payload) must not defeat the crash logger. Returns a
    /// typed fallback marker instead of propagating.
    /// </summary>
    private static string SafeToString(object o)
    {
        try { return o.ToString() ?? "(ToString returned null)"; }
        catch (Exception tex) { return $"(ToString threw {tex.GetType().Name})"; }
    }

    /// <summary>
    /// Format an AppDomain.UnhandledException payload into a single log line with the
    /// full exception detail (ToString includes the stack) and the terminating flag.
    /// Total — never throws (a hostile ToString is caught by SafeToString). Pure.
    /// </summary>
    public static string FormatUnhandled(object? exceptionObject, bool isTerminating)
    {
        var detail = exceptionObject is null ? "(null ExceptionObject)" : SafeToString(exceptionObject);
        return $"[crash] UNHANDLED EXCEPTION (terminating={isTerminating}): {detail}";
    }

    /// <summary>
    /// Format a TaskScheduler.UnobservedTaskException into a single log line with the
    /// full exception detail. Total — never throws. Pure.
    /// </summary>
    public static string FormatUnobservedTask(Exception? exception)
        => $"[crash] UNOBSERVED TASK EXCEPTION: {(exception is null ? "(null)" : SafeToString(exception))}";

    /// <summary>
    /// Install the process-global exception sinks ONCE. Both log via
    /// <paramref name="log"/> (default stderr) so a crash on ANY thread — not just
    /// the main async flow Program.Main already guards — leaves a diagnosable trace
    /// before the process dies. Idempotent. PURE diagnostics: it only OBSERVES and
    /// logs; it does not mark the unobserved task observed or otherwise alter the
    /// runtime's own unhandled/unobserved lifecycle policy (a diagnostic sink must
    /// not, e.g., suppress a configured fail-fast). Each handler body has an inner
    /// fallback so that even if formatting throws, SOMETHING is still logged (a
    /// swallowed formatter exception would silently defeat the whole point).
    /// </summary>
    public static void Install(Action<string>? log = null)
    {
        if (System.Threading.Interlocked.Exchange(ref _installed, 1) != 0) return;
        var write = log ?? Console.Error.WriteLine;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { write(FormatUnhandled(e.ExceptionObject, e.IsTerminating)); }
            catch
            {
                try { write($"[crash] UNHANDLED EXCEPTION (terminating={e.IsTerminating}): (crash logger failed to format the payload)"); }
                catch { /* never let the crash logger throw */ }
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { write(FormatUnobservedTask(e.Exception)); }
            catch
            {
                try { write("[crash] UNOBSERVED TASK EXCEPTION: (crash logger failed to format the exception)"); }
                catch { /* never let the crash logger throw */ }
            }
        };
    }
}
