namespace HeadlessAcClient.World;

using System;
using System.Collections.Generic;

/// <summary>
/// Mechanical nav bookkeeping: tracks interaction targets (by guid) that
/// the SERVER refused as out-of-reach, with a TTL cooldown.
///
/// The Motor resolves an LLM-named interaction goal (e.g. Pickup{Chest})
/// to the nearest matching guid via <c>tactics.ResolveTarget</c>, which
/// does NOT consult the autonomous picker's <c>visitedTargetGuids</c>
/// filter. So when an interaction fails out-of-reach (see
/// <see cref="InteractReachClassifier.IsOutOfReach"/> — a chest on a ledge
/// the bot can XY-arrive at but not 3D-reach), the next goal cycle
/// re-resolves the SAME unreachable guid → lock → fail loop (live repro:
/// 5 consecutive re-locks on one chest, 10 out-of-reach failures in a
/// single run). Marking the guid here and suppressing its re-resolution
/// for a cooldown breaks the loop and lets the bot steer to a reachable
/// target.
///
/// TTL'd, not permanent: a server out-of-reach proves "not reachable from
/// here/now", not "globally unreachable" — the bot may approach the same
/// object from a different cell later, so the guid is retried after the
/// cooldown elapses. Mirrors the Motor's <c>combatAvoidUntil</c> cooldown.
///
/// Encodes no game knowledge: it keys only on a guid the server itself
/// refused as out-of-reach. No object type, name, wcid, or landblock.
/// </summary>
public sealed class InteractUnreachableTracker
{
    private readonly Dictionary<uint, DateTime> _until = new();

    // Per-guid consecutive-refusal streak, used ONLY for the optional escalating
    // backoff (a caller opts in with maxBackoffMultiplier > 1; the default-1 path
    // never touches this). A streak accrues while re-marks for the SAME guid stay
    // within that guid's current DECAY WINDOW; a gap of at least the window (the
    // object was reached, abandoned, or approached from a reachable angle) resets
    // it. Each entry stores when it goes stale (LastMark + window) so reads
    // (IsSuppressed / SnapshotSuppressed) and the next backoff mark can prune it,
    // keeping the dictionary bounded by the few guids recently refused even if
    // out-of-reach marks then stop entirely.
    private readonly Dictionary<uint, (int Strikes, DateTime StaleAfter)> _strikes = new();

    // The decay window scales with the cooldown the caller is escalating toward:
    // ttl * (maxBackoffMultiplier + 1). Because the same guid is SUPPRESSED for
    // its current cooldown, a natural re-mark arrives ~cooldown later, and at the
    // cap the cooldown is ttl*cap — so a window of ttl*(cap+1) (one base-ttl of
    // grace beyond the max cooldown) lets the streak climb to the cap and STAY
    // there for a persistently-refused target, while a gap longer than that means
    // the bot moved on -> reset to a fresh base-ttl cooldown.
    private static TimeSpan DecayWindow(TimeSpan ttl, int maxBackoffMultiplier)
        => TimeSpan.FromTicks(ttl.Ticks * (maxBackoffMultiplier + 1));

    /// <summary>
    /// Mark <paramref name="guid"/> as unreachable until
    /// <paramref name="now"/> + <paramref name="ttl"/>. A later mark for
    /// the same guid refreshes (extends) the cooldown.
    ///
    /// When <paramref name="maxBackoffMultiplier"/> &gt; 1 the cooldown
    /// ESCALATES: consecutive refusals of the SAME guid (re-marks within the
    /// scaling decay window, see <see cref="DecayWindow"/>) multiply the base
    /// ttl by the running streak, capped at the multiplier — so a persistently
    /// out-of-reach target is retried (re-locked + walked to) progressively less
    /// often, climbing to and holding at base-ttl × cap. A gap of at least the
    /// decay window resets the streak to a fresh base-ttl cooldown, so a target
    /// that becomes reachable from a new position is not suppressed forever. The
    /// default (1) keeps the original fixed-ttl behavior byte-for-byte for callers
    /// that do not opt in.
    /// </summary>
    public void MarkUnreachable(uint guid, DateTime now, TimeSpan ttl, int maxBackoffMultiplier = 1)
    {
        if (maxBackoffMultiplier <= 1)
        {
            // Original fixed-ttl behavior (unchanged for the non-backoff callers).
            _until[guid] = now + ttl;
            return;
        }

        var window = DecayWindow(ttl, maxBackoffMultiplier);
        var streak = 1;
        if (_strikes.TryGetValue(guid, out var prev) && now < prev.StaleAfter)
            streak = prev.Strikes + 1;
        _strikes[guid] = (streak, now + window);
        PruneStaleStrikes(now);

        var mult = Math.Min(streak, maxBackoffMultiplier);
        var until = now + TimeSpan.FromTicks(ttl.Ticks * mult);
        // Never SHORTEN an existing suppression. Within the window the streak grows
        // so `until` is monotonically later anyway; this guard only matters if the
        // wall clock steps backward (DateTime.UtcNow is not guaranteed monotonic).
        // It does not fight the decay reset: a reset only fires after a gap of at
        // least the window, by which point the prior (<= ttl×cap < window) cooldown
        // has already elapsed, so the fresh base-ttl `until` is later than it.
        _until[guid] = _until.TryGetValue(guid, out var existing) && existing > until ? existing : until;
    }

    // Drop strike entries whose decay window has elapsed (now at/after StaleAfter).
    private void PruneStaleStrikes(DateTime now)
    {
        List<uint>? stale = null;
        foreach (var kv in _strikes)
            if (now >= kv.Value.StaleAfter)
                (stale ??= new()).Add(kv.Key);
        if (stale is not null)
            foreach (var g in stale)
                _strikes.Remove(g);
    }

    /// <summary>
    /// True if <paramref name="guid"/> is still within its unreachable
    /// cooldown at <paramref name="now"/>. Lazily evicts the entry once the
    /// cooldown has elapsed so the dictionary self-prunes and the guid
    /// becomes resolvable again.
    /// </summary>
    public bool IsSuppressed(uint guid, DateTime now)
    {
        // Opportunistically drop a stale strike for this guid so the streak state
        // self-prunes even if out-of-reach marks stop arriving (it must outlive the
        // _until cooldown to keep accumulating, so it is keyed on its own StaleAfter).
        if (_strikes.TryGetValue(guid, out var s) && now >= s.StaleAfter)
            _strikes.Remove(guid);
        if (!_until.TryGetValue(guid, out var until)) return false;
        if (now < until) return true;
        _until.Remove(guid);
        return false;
    }

    /// <summary>Active (non-evicted) entry count — for tests/diagnostics.</summary>
    public int Count => _until.Count;

    /// <summary>Active strike-state entry count — for tests (self-prune coverage).</summary>
    internal int StrikeCount => _strikes.Count;

    /// <summary>
    /// Snapshot the currently-suppressed guids (those still within their
    /// cooldown at <paramref name="now"/>) as (guid, until) pairs, lazily
    /// evicting any that have already expired. Used to project the
    /// suppression set into the prompt so the LLM is not blind to which
    /// guids the Motor will currently drop. Keys only on guid + expiry —
    /// no name, type, wcid, or landblock (the caller looks up any display
    /// name from its own world projection).
    /// </summary>
    public IReadOnlyList<KeyValuePair<uint, DateTime>> SnapshotSuppressed(DateTime now)
    {
        List<KeyValuePair<uint, DateTime>>? live = null;
        List<uint>? expired = null;
        foreach (var kv in _until)
        {
            if (now < kv.Value)
                (live ??= new()).Add(kv);
            else
                (expired ??= new()).Add(kv.Key);
        }
        if (expired is not null)
            foreach (var g in expired)
                _until.Remove(g);
        // SnapshotSuppressed runs at prompt-build time (every decision), so it is
        // the regular self-prune point for strike state in quiescence: drop strikes
        // whose decay window has elapsed regardless of whether new marks arrive.
        PruneStaleStrikes(now);
        return live ?? (IReadOnlyList<KeyValuePair<uint, DateTime>>)System.Array.Empty<KeyValuePair<uint, DateTime>>();
    }
}
