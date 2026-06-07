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

    /// <summary>
    /// Mark <paramref name="guid"/> as unreachable until
    /// <paramref name="now"/> + <paramref name="ttl"/>. A later mark for
    /// the same guid refreshes (extends) the cooldown.
    /// </summary>
    public void MarkUnreachable(uint guid, DateTime now, TimeSpan ttl)
        => _until[guid] = now + ttl;

    /// <summary>
    /// True if <paramref name="guid"/> is still within its unreachable
    /// cooldown at <paramref name="now"/>. Lazily evicts the entry once the
    /// cooldown has elapsed so the dictionary self-prunes and the guid
    /// becomes resolvable again.
    /// </summary>
    public bool IsSuppressed(uint guid, DateTime now)
    {
        if (!_until.TryGetValue(guid, out var until)) return false;
        if (now < until) return true;
        _until.Remove(guid);
        return false;
    }

    /// <summary>Active (non-evicted) entry count — for tests/diagnostics.</summary>
    public int Count => _until.Count;
}
