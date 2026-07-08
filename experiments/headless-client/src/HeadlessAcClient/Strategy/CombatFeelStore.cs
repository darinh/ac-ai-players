// SPDX-License-Identifier: AGPL-3.0-or-later
//
// CombatFeelStore — durable, per-character persistence for the
// CombatFeelLedger. Resolves a per-character file path, loads the ledger on
// startup, and rewrites it (atomically) when it has unsaved changes.
//
// Why this exists: CombatFeelLedger records the bot's OWN observed combat
// outcomes (kills / deaths / near-deaths / ineffective fights) per monster
// KIND. Without persistence the ledger is rebuilt empty on every process
// start, so the bot re-learns — and re-dies to — the same kinds each session.
// The headless client restarts frequently (each observe run, each pilot-loop
// kick, each crash), so without this the bot is effectively amnesiac. This
// makes the learning durable across restarts.
//
// AUDIT FRAMING (.github/skills/audit-hardcoded-knowledge/SKILL.md):
// This is bookkeeping of the bot's OWN runtime-observed experience, not game
// knowledge. The persisted file holds wcids/names the bot SAW and outcome
// counts it RECORDED — written by the bot at runtime, never a hardcoded list
// in source. The source contains no monster identities. It assigns no
// priority and makes no avoidance decision; the LLM still decides what to do
// with the counts (the existing "COMBAT SAFETY" prompt rule). Same class as
// the persistent NavGraph journal (runtime-learned spatial state on disk).

namespace HeadlessAcClient.Strategy;

using System;
using System.IO;
using System.Threading;

internal static class CombatFeelStore
{
    /// <summary>
    /// Directory holding per-character combat-feel files. Tries, in order,
    /// AC_BOTS_STATE_DIR (AC_BOTS_* convention), a "bot-state" folder next to
    /// the executable, then a temp subfolder — returning the first it can
    /// create. NEVER throws: a bad/unwritable AC_BOTS_STATE_DIR must not abort
    /// startup (persistence then silently degrades — saves are best-effort and
    /// SaveIfDirty swallows write failures). Falls back to the temp root as a
    /// last resort.
    /// </summary>
    internal static string ResolveDirectory()
    {
        // Temp candidate computed defensively: Path.GetTempPath() itself can
        // throw (e.g. SecurityException), so a hostile environment must not
        // crash startup here.
        string? temp = null;
        try { temp = Path.Combine(Path.GetTempPath(), "ac-bots-state"); }
        catch { temp = null; }

        string? configured = null;
        try { configured = Environment.GetEnvironmentVariable("AC_BOTS_STATE_DIR"); }
        catch { configured = null; }

        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(configured) ? null : configured,
            Path.Combine(AppContext.BaseDirectory, "bot-state"),
            temp,
        };
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                Directory.CreateDirectory(c);
                return c;
            }
            catch
            {
                // Any failure (permissions, invalid path, IO) — try the next.
            }
        }
        // Last resort: the executable's own directory is a real, existing path
        // and accessing AppContext.BaseDirectory does not throw. Writes there
        // are best-effort (SaveIfDirty swallows failures).
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Per-character ledger file path. The character name is sanitized so it
    /// is always a safe single file name (path separators and invalid chars
    /// collapse to '_'); an empty/blank name falls back to "default". Never
    /// throws (see <see cref="ResolveDirectory"/>).
    /// </summary>
    internal static string ResolvePath(string? characterName)
    {
        var safe = SanitizeFileName(characterName);
        return Path.Combine(ResolveDirectory(), $"combat-feel-{safe}.json");
    }

    /// <summary>
    /// Loads the ledger from <paramref name="path"/>, or returns a fresh empty
    /// ledger when the file is missing or unreadable. Never throws — a load
    /// failure must not stop the bot from playing (it just starts fresh).
    /// </summary>
    internal static CombatFeelLedger LoadOrNew(string path)
    {
        try
        {
            if (File.Exists(path))
                return CombatFeelLedger.FromJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"[combat-feel] load failed ({Path.GetFileName(path)}): " +
                $"{ex.GetType().Name}: {ex.Message}; starting with an empty ledger");
        }
        return new CombatFeelLedger();
    }

    private static bool _writeFailureLogged;

    /// <summary>
    /// Rewrites the ledger file only when the ledger has unsaved changes. The
    /// write is atomic-ish (temp file then replace/move) so a crash mid-write
    /// can't corrupt an existing good file. Never throws; the first write
    /// failure is logged once and subsequent ones suppressed.
    ///
    /// Single-writer invariant: the file is per-character and the AC server
    /// permits only one live session per character, so two processes shouldn't
    /// write the same combat-feel-&lt;character&gt;.json at once. As defense-in-depth
    /// the reload-merge-replace critical section is taken under a machine-wide
    /// named mutex keyed on the file path, closing the read-&gt;replace window if
    /// that invariant is ever violated. The mutex is best-effort: if it can't be
    /// created/acquired we still write (the max-merge + atomic replace already
    /// prevent corruption).
    /// </summary>
    internal static void SaveIfDirty(CombatFeelLedger ledger, string path)
    {
        if (!ledger.Dirty) return;

        Mutex? mutex = TryCreateFileMutex(path);
        bool held = false;
        try
        {
            try { held = mutex is not null && mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { held = true; } // prior holder crashed; we own it now

            try
            {
                // Defensive reload-merge: fold any on-disk observations (a richer
                // prior file, or a writer that slipped past the invariant) into
                // the in-memory ledger before the full-file rewrite, so a blind
                // overwrite can't drop them. Max-merge never double-counts. A
                // read failure here is non-fatal: we just write what we have.
                try
                {
                    if (File.Exists(path))
                        ledger.MergeFrom(CombatFeelLedger.FromJson(File.ReadAllText(path)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // ignore — proceed to write the in-memory ledger
                }

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, ledger.ToJson());
                if (TryPlaceTempFile(tmp, path))
                    ledger.MarkClean();
                else if (!_writeFailureLogged)
                {
                    _writeFailureLogged = true;
                    Console.Error.WriteLine(
                        $"[combat-feel] WARN save failed after retries ({Path.GetFileName(path)}); " +
                        "ledger stays dirty and retries on the next combat outcome; future failures suppressed");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The tmp WRITE (or the merge-reload above) itself failed — distinct from a
                // place/replace failure, which TryPlaceTempFile owns. Leave the ledger dirty
                // so the next outcome retries; log once.
                if (!_writeFailureLogged)
                {
                    _writeFailureLogged = true;
                    Console.Error.WriteLine(
                        $"[combat-feel] WARN first save failure ({Path.GetFileName(path)}): " +
                        $"{ex.GetType().Name}: {ex.Message}; future failures suppressed");
                }
            }
        }
        finally
        {
            if (held)
            {
                try { mutex!.ReleaseMutex(); } catch { /* not owned / abandoned */ }
            }
            mutex?.Dispose();
        }
    }

    // Default place-file retry policy: Windows File.Replace is prone to a
    // transient "Unable to remove the file to be replaced" IOException when an
    // AV / indexer / another handle momentarily locks the destination. A short
    // bounded retry usually rides it out.
    private const int DefaultPlaceMaxAttempts = 3;
    private const int DefaultPlaceBackoffMs = 40;

    /// <summary>
    /// Atomically move <paramref name="tmp"/> onto <paramref name="path"/> with a
    /// bounded retry, returning true on success. Uses <c>File.Replace</c> when the
    /// destination exists (else <c>File.Move</c>); on a transient
    /// IOException/UnauthorizedAccessException it backs off linearly and retries up
    /// to <paramref name="maxAttempts"/> times. Every attempt is ATOMIC — a failed
    /// <c>File.Replace</c> leaves the existing destination untouched, so a total
    /// failure never destroys the prior on-disk ledger (the caller then leaves the
    /// in-memory ledger dirty to rewrite on the next combat outcome). Returns false
    /// only if every attempt fails. The <paramref name="sleep"/> delegate is
    /// injectable so tests drive the retry deterministically without real waits.
    /// Pure file bookkeeping; no game data.
    /// </summary>
    internal static bool TryPlaceTempFile(
        string tmp, string path,
        int maxAttempts = DefaultPlaceMaxAttempts,
        int backoffMs = DefaultPlaceBackoffMs,
        Action<int>? sleep = null)
    {
        if (maxAttempts < 1) maxAttempts = 1;
        var doSleep = sleep ?? (ms => Thread.Sleep(ms));
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, destinationBackupFileName: null);
                else
                    File.Move(tmp, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts) break;
                doSleep(backoffMs * attempt); // linear backoff before the next attempt
            }
        }
        // Deliberately NO delete+move fallback: File.Delete + File.Move is
        // non-atomic — a delete that succeeds then a move that fails would destroy
        // the prior ledger, a durability REGRESSION vs the atomic File.Replace
        // (which preserves the destination on every failure). The bounded retry
        // above is the safe, standard fix; a persistent lock just returns false and
        // the caller keeps the ledger dirty to retry on the next combat outcome.
        return false;
    }

    /// <summary>
    /// A machine-wide mutex named by a stable hash of the file path, so all
    /// processes writing the SAME per-character file serialize on it. Returns
    /// null (caller proceeds lock-free) if a mutex can't be created. Uses a
    /// content hash (not String.GetHashCode, which is per-process randomized)
    /// so independent processes derive the SAME name for the same path.
    /// </summary>
    private static Mutex? TryCreateFileMutex(string path)
    {
        try
        {
            string norm;
            try { norm = Path.GetFullPath(path).ToLowerInvariant(); }
            catch { norm = path.ToLowerInvariant(); }
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(norm)));
            // "Local\" = per-logon-session scope on this machine (where all bots
            // run). The suffix carries no '\' (reserved by the mutex namespace).
            return new Mutex(initiallyOwned: false, name: "Local\\ac-bots-cf-" + hash);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "default";
        var chars = name.Trim().ToCharArray();
        var invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }
}
