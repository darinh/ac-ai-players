using System;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Tests for <see cref="LlmTempoMeter"/>: the diagnostic-only meter that
/// quantifies the LLM round-trips spent per kill (the reduce-llm-call-volume
/// tempo debt). Pure observability — these tests pin the counting/baseline
/// logic so the surfaced numbers are trustworthy.
/// </summary>
public class LlmTempoMeterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstObservation_AnchorsBaseline_NoReport_EvenWithPersistedKills()
    {
        // A persisted cross-session combat-feel total (e.g. 52 prior kills) must
        // NOT be reported as a burst on the first observation.
        var m = new LlmTempoMeter();
        Assert.Null(m.ObserveTotalKills(52, T0));
        Assert.Equal(0, m.KillsCounted);
    }

    [Fact]
    public void Kill_AfterCalls_ReportsCallsSincePrevKill_AndResetsInterval()
    {
        var m = new LlmTempoMeter();
        m.ObserveTotalKills(0, T0); // baseline
        m.RecordLlmCall();
        m.RecordLlmCall();
        m.RecordLlmCall();

        var line = m.ObserveTotalKills(1, T0.AddSeconds(15));
        Assert.NotNull(line);
        Assert.Contains("llm-calls-since-prev-kill=3", line);
        Assert.Contains("secs-since-prev-kill=15.0", line);
        Assert.Equal(1, m.KillsCounted);

        // Interval counter reset: 2 more calls then another kill reports 2.
        m.RecordLlmCall();
        m.RecordLlmCall();
        var line2 = m.ObserveTotalKills(2, T0.AddSeconds(20));
        Assert.NotNull(line2);
        Assert.Contains("llm-calls-since-prev-kill=2", line2);
        Assert.Contains("secs-since-prev-kill=5.0", line2);
    }

    [Fact]
    public void CumulativeAverage_IsTotalCallsOverTotalKills()
    {
        var m = new LlmTempoMeter();
        m.ObserveTotalKills(0, T0);
        // 4 calls -> kill #1; 2 calls -> kill #2. total calls=6, kills=2 => avg 3.0.
        m.RecordLlmCall(); m.RecordLlmCall(); m.RecordLlmCall(); m.RecordLlmCall();
        m.ObserveTotalKills(1, T0.AddSeconds(10));
        m.RecordLlmCall(); m.RecordLlmCall();
        var line = m.ObserveTotalKills(2, T0.AddSeconds(20));
        Assert.NotNull(line);
        Assert.Contains("cumulative-avg=3.0 llm-calls/kill", line);
        Assert.Equal(6, m.LlmCallsTotal);
        Assert.Equal(2, m.KillsCounted);
    }

    [Fact]
    public void UnchangedCount_ReturnsNull()
    {
        var m = new LlmTempoMeter();
        m.ObserveTotalKills(3, T0);     // baseline at 3
        Assert.Null(m.ObserveTotalKills(3, T0.AddSeconds(5)));
        Assert.Equal(0, m.KillsCounted);
    }

    [Fact]
    public void Decrease_ReanchorsBaseline_NoReport()
    {
        // A ledger/char reset drops the total; re-anchor silently, then count
        // forward from the new baseline.
        var m = new LlmTempoMeter();
        m.ObserveTotalKills(10, T0);            // baseline 10
        Assert.Null(m.ObserveTotalKills(2, T0.AddSeconds(5))); // reset to 2, no report
        m.RecordLlmCall();
        var line = m.ObserveTotalKills(3, T0.AddSeconds(6));   // 2 -> 3 = one kill
        Assert.NotNull(line);
        Assert.Contains("llm-calls-since-prev-kill=1", line);
        Assert.Equal(1, m.KillsCounted);
    }

    [Fact]
    public void MultipleKillsInOneObservation_ReportedAsDelta()
    {
        var m = new LlmTempoMeter();
        m.ObserveTotalKills(0, T0);
        m.RecordLlmCall();
        var line = m.ObserveTotalKills(3, T0.AddSeconds(8)); // +3 kills at once
        Assert.NotNull(line);
        Assert.Contains("kills+3", line);
        Assert.Equal(3, m.KillsCounted);
    }

    [Fact]
    public void FirstIntervalSeconds_AnchoredToBaselineTime()
    {
        // The first kill's "secs-since-prev-kill" is measured from the baseline
        // observation time, not "n/a".
        var m = new LlmTempoMeter();
        m.ObserveTotalKills(0, T0);
        var line = m.ObserveTotalKills(1, T0.AddSeconds(7));
        Assert.NotNull(line);
        Assert.Contains("secs-since-prev-kill=7.0", line);
    }
}
