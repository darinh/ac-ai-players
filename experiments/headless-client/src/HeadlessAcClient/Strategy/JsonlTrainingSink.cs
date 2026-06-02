// SPDX-License-Identifier: AGPL-3.0-or-later
// JsonlTrainingSink — append-only newline-delimited JSON sink for
// LLM decision/outcome tuples. Each line is one event:
//
//   {"id": guid, "ts": iso, "kind": "decision",  ...}
//   {"id": guid, "ts": iso, "kind": "parse-error", "error": ...}
//   {"id": guid, "ts": iso, "kind": "emitted-goal", "goal": {...}}
//   {"id": goalId, "ts": iso, "kind": "outcome", "outcome": ..., "evidence": ...}
//
// File: experiments/headless-client/data/training/decisions-{sessionStart}.jsonl
// Path is relative to the process cwd at startup; the bot is run
// from the worktree root so this lands under
// .worktrees/<task-id>/experiments/headless-client/data/training/.
//
// The sink is fire-and-forget: failures are logged once and swallowed
// so a disk-full or permission glitch never takes the bot down.
// Writes are serialized through a single lock — line writes are
// short and the bot doesn't issue them at a high rate (one decision
// per LLM call, ~1 per several seconds).

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy;

internal sealed class JsonlTrainingSink : ITrainingDataSink, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private readonly string _path;
    private StreamWriter? _writer;
    private bool _firstFailureLogged;
    private bool _disposed;

    public string Path => _path;

    public JsonlTrainingSink(string? directory = null, DateTimeOffset? sessionStartUtc = null)
    {
        directory ??= System.IO.Path.Combine(
            Environment.CurrentDirectory,
            "experiments", "headless-client", "data", "training");
        var stamp = (sessionStartUtc ?? DateTimeOffset.UtcNow).ToString("yyyyMMdd-HHmmss");
        _path = System.IO.Path.Combine(directory, $"decisions-{stamp}.jsonl");

        try
        {
            Directory.CreateDirectory(directory);
            _writer = new StreamWriter(new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read))
            {
                AutoFlush = true,
            };
            Console.WriteLine($"[training] JsonlTrainingSink writing to {_path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[training] WARN open failed ({ex.GetType().Name}: {ex.Message}); training sink disabled");
            _writer = null;
        }
    }

    public void RecordDecision(TrainingDecision decision)
    {
        WriteLine(new
        {
            id = decision.Id,
            ts = decision.CreatedAtUtc,
            kind = "decision",
            trigger = decision.Trigger,
            model = decision.Model,
            endpoint = decision.Endpoint,
            user_prompt = decision.UserPrompt,
            world_projection = decision.WorldProjectionJson,
            llm_ok = decision.LlmOk,
            llm_latency_ms = decision.LlmLatencyMs,
            llm_raw_response = decision.LlmRawResponse,
            llm_error = decision.LlmError,
        });
    }

    public void RecordParseError(Guid decisionId, string error)
    {
        WriteLine(new
        {
            id = decisionId,
            ts = DateTimeOffset.UtcNow,
            kind = "parse-error",
            error,
        });
    }

    public void RecordEmittedGoal(Guid decisionId, Goal goal)
    {
        WriteLine(new
        {
            id = decisionId,
            goal_id = goal.Id,
            ts = DateTimeOffset.UtcNow,
            kind = "emitted-goal",
            goal,
        });
    }

    public void RecordOutcome(Guid goalId, string outcome, string? evidence = null)
    {
        WriteLine(new
        {
            id = goalId,
            ts = DateTimeOffset.UtcNow,
            kind = "outcome",
            outcome,
            evidence,
        });
    }

    private void WriteLine(object record)
    {
        if (_writer is null || _disposed) return;
        try
        {
            var line = JsonSerializer.Serialize(record, JsonOpts);
            lock (_lock)
            {
                _writer?.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            if (!_firstFailureLogged)
            {
                _firstFailureLogged = true;
                Console.Error.WriteLine(
                    $"[training] WARN first write failure: {ex.GetType().Name}: {ex.Message}; future failures suppressed");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Flush(); } catch { /* ignore */ }
            try { _writer?.Dispose(); } catch { /* ignore */ }
            _writer = null;
        }
    }
}
