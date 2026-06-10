// SPDX-License-Identifier: AGPL-3.0-or-later
// LlmTempoMeter — diagnostic-only instrument that quantifies how many LLM
// round-trips the bot spends per kill.
//
// Motivation: the bot currently makes a fresh multi-second LLM call per
// monster (picker walks in-range -> "awaiting LLM verb goal" -> a
// no-current-goal LLM call just to emit Attack -> kill -> repeat). That is the
// standing `reduce-llm-call-volume` tempo debt and the visible "why are the
// bots so slow?" symptom. You cannot optimize what you do not measure, so this
// meter surfaces a `[tempo]` log line per kill: LLM calls spent since the prior
// kill, the cumulative llm-calls-per-kill average, and wall-clock seconds
// between kills.
//
// This is PURE OBSERVABILITY: it never influences a goal, a target, or any
// decision; it reads only the bot's OWN counters (LLM call count it made, and
// its OWN combat-feel kill total). No game knowledge — no monster/type/wcid is
// inspected, only an aggregate kill count.

using System;
using System.Globalization;

namespace HeadlessAcClient.Strategy;

internal sealed class LlmTempoMeter
{
    private long _llmCallsTotal;
    private long _llmCallsSinceLastKill;
    private long _killsCounted;
    private long _lastTotalKills;
    private bool _initialized;
    private DateTimeOffset? _lastKillUtc;

    /// <summary>Total LLM calls observed this session (diagnostic accessor).</summary>
    public long LlmCallsTotal => _llmCallsTotal;

    /// <summary>Total kills counted since the meter's baseline (diagnostic accessor).</summary>
    public long KillsCounted => _killsCounted;

    /// <summary>
    /// Record one LLM request kickoff. Call exactly once per LLM call the
    /// policy makes (at the kickoff site).
    /// </summary>
    public void RecordLlmCall()
    {
        _llmCallsTotal++;
        _llmCallsSinceLastKill++;
    }

    /// <summary>
    /// Observe the bot's cumulative kill count (e.g. summed from the combat-feel
    /// ledger). The FIRST observation only anchors the baseline (so a persisted
    /// cross-session kill total is not reported as a burst) and returns null.
    /// When the count later RISES, returns a one-line `[tempo]` summary for the
    /// kill(s) just detected and resets the per-kill LLM-call counter; returns
    /// null otherwise. A DECREASE (e.g. a ledger/char reset) re-anchors the
    /// baseline without reporting. Idempotent within a tick if the count is
    /// unchanged.
    /// </summary>
    public string? ObserveTotalKills(long totalKills, DateTimeOffset nowUtc)
    {
        if (!_initialized)
        {
            _initialized = true;
            _lastTotalKills = totalKills;
            _lastKillUtc = nowUtc;
            return null;
        }

        if (totalKills <= _lastTotalKills)
        {
            if (totalKills < _lastTotalKills)
                _lastTotalKills = totalKills; // counter reset (new char / ledger cleared)
            return null;
        }

        var newKills = totalKills - _lastTotalKills;
        _lastTotalKills = totalKills;
        _killsCounted += newKills;

        var callsThisInterval = _llmCallsSinceLastKill;
        _llmCallsSinceLastKill = 0;

        double? secsSincePrev = _lastKillUtc is DateTimeOffset prev
            ? Math.Max(0.0, (nowUtc - prev).TotalSeconds)
            : null;
        _lastKillUtc = nowUtc;

        var avgPerKill = _killsCounted > 0
            ? (double)_llmCallsTotal / _killsCounted
            : 0.0;

        var inv = CultureInfo.InvariantCulture;
        var secsStr = secsSincePrev is double s ? s.ToString("F1", inv) : "n/a";
        var newKillsStr = newKills == 1 ? "kill" : $"kills+{newKills}";

        return
            $"{newKillsStr} (total {_killsCounted}): " +
            $"llm-calls-since-prev-kill={callsThisInterval}, " +
            $"cumulative-avg={avgPerKill.ToString("F1", inv)} llm-calls/kill, " +
            $"secs-since-prev-kill={secsStr}";
    }
}
