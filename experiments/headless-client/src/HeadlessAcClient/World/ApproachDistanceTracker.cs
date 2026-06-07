namespace HeadlessAcClient.World;

using System;
using System.Collections.Generic;

/// <summary>
/// Mechanical nav bookkeeping: records the measured self→target distance
/// each time the Motor locks an interaction goal (Talk/Use/Pickup/Give/
/// Attack) on a target, keyed by guid, keeping a short rolling history per
/// target.
///
/// Why this exists: the Motor resolves an LLM-named interaction goal to a
/// guid and computes the current distance at lock time, but that distance
/// is Motor-only — the Strategy layer (LLM) cannot see, across ticks,
/// whether its repeated selections of the SAME target are actually
/// reducing the distance. When repeated locks on one target do not close
/// the distance, the LLM has no prompt-visible signal of that and may keep
/// re-selecting it. Surfacing the recent distance history lets the LLM read
/// the trend and decide.
///
/// Encodes no game knowledge: it stores only a guid, an optional display
/// name (supplied by the caller from its own world projection), and a list
/// of raw distance measurements. No object type, wcid, landblock, or
/// priority. It renders a fact; it makes no decision.
/// </summary>
public sealed class ApproachDistanceTracker
{
    /// <summary>Max distance samples retained per target (oldest dropped).</summary>
    public const int MaxSamplesPerTarget = 6;

    /// <summary>Max distinct targets tracked before the least-recent is evicted.</summary>
    public const int MaxTrackedTargets = 12;

    private sealed class Entry
    {
        public string? Name;
        public readonly List<double> Samples = new();
        public long Order;
        public DateTime LastRecordUtc;
    }

    private readonly Dictionary<uint, Entry> _byGuid = new();
    private long _order;

    /// <summary>
    /// Append a measured approach distance (in world units) to
    /// <paramref name="guid"/>'s rolling history at <paramref name="now"/>.
    /// A non-empty <paramref name="name"/> refreshes the stored display
    /// name. The history is capped at <see cref="MaxSamplesPerTarget"/>
    /// (oldest dropped); the tracked-target set is capped at
    /// <see cref="MaxTrackedTargets"/> (least-recently-recorded evicted).
    /// </summary>
    public void Record(uint guid, string? name, double distanceUnits, DateTime now)
    {
        if (!_byGuid.TryGetValue(guid, out var e))
        {
            e = new Entry();
            _byGuid[guid] = e;
            if (_byGuid.Count > MaxTrackedTargets)
                EvictLeastRecent(except: guid);
        }
        if (!string.IsNullOrEmpty(name))
            e.Name = name;
        e.Samples.Add(distanceUnits);
        while (e.Samples.Count > MaxSamplesPerTarget)
            e.Samples.RemoveAt(0);
        e.Order = ++_order;
        e.LastRecordUtc = now;
    }

    /// <summary>Distinct tracked-target count — for tests/diagnostics.</summary>
    public int Count => _byGuid.Count;

    /// <summary>
    /// The most-recently-recorded target's distance history, oldest→newest,
    /// provided it was recorded within <paramref name="freshness"/> of
    /// <paramref name="now"/> and holds at least <paramref name="minSamples"/>
    /// samples. Returns false otherwise. The freshness gate keeps a stale
    /// fixation (the bot has since moved on to other goals) from lingering
    /// in the prompt; the minSamples gate is a data-availability bound — at
    /// least that many points are needed to render a history, not a
    /// significance judgement.
    /// </summary>
    public bool TryGetMostRecent(
        DateTime now,
        TimeSpan freshness,
        int minSamples,
        out uint guid,
        out string? name,
        out IReadOnlyList<double> samples)
    {
        guid = 0;
        name = null;
        samples = Array.Empty<double>();

        Entry? best = null;
        uint bestGuid = 0;
        foreach (var kv in _byGuid)
        {
            if (best is null || kv.Value.Order > best.Order)
            {
                best = kv.Value;
                bestGuid = kv.Key;
            }
        }
        if (best is null)
            return false;
        if (now - best.LastRecordUtc > freshness)
            return false;
        if (best.Samples.Count < minSamples)
            return false;

        guid = bestGuid;
        name = best.Name;
        samples = best.Samples.ToArray();
        return true;
    }

    private void EvictLeastRecent(uint except)
    {
        uint victim = 0;
        long oldest = long.MaxValue;
        var found = false;
        foreach (var kv in _byGuid)
        {
            if (kv.Key == except)
                continue;
            if (kv.Value.Order < oldest)
            {
                oldest = kv.Value.Order;
                victim = kv.Key;
                found = true;
            }
        }
        if (found)
            _byGuid.Remove(victim);
    }
}
