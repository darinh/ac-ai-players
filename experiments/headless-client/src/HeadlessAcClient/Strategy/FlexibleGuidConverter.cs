// SPDX-License-Identifier: AGPL-3.0-or-later
// FlexibleGuidConverter — System.Text.Json converter that accepts a
// Guid in either dashed ("D") or dashless ("N") format, plus the
// braced and parenthesized variants Guid.Parse supports.
//
// Why this exists: the default System.Text.Json Guid converter
// rejects anything other than the dashed 36-char form. Llama-3.3-70B
// (and other models we've tried) frequently emit dashless 32-char
// hex GUIDs in their JSON responses, e.g.
//   { "goal_id": "d3c59293cfd04e2e8a587ca1a4c0af34", ... }
// which would fail to parse and cause the entire Goal response to be
// dropped (silently — only training data captured the rejection),
// turning the LLM into a no-op. The bot would then sit at PICKER
// ARRIVED no-action forever even though the LLM was emitting valid
// Attack/Use/Talk goals on every kickoff.
//
// We accept whatever Guid.Parse accepts (D, N, B, P, X formats) and
// emit the canonical dashed form on serialization to keep training
// logs consistent and downstream tooling simple.
//
// Hardening: models ALSO frequently emit a human-readable slug for the
// id, e.g. { "goal_id": "goal-001" } or { "goal_id": "goal_1" }, which
// is not any Guid format. Throwing on those dropped the ENTIRE goal
// response (turning the LLM into a no-op and silently degrading the bot
// to the keyword fallback policy — observed live 2026-06-03). The id is
// only a correlation/training handle, so a non-Guid string must NOT
// discard the response. We normalize it to Guid.Empty; the caller
// (LlmGoalPolicy.ProposeGoal) already assigns a fresh unique id when the
// parsed id is empty, which also preserves Goal.Id global uniqueness
// even when a model lazily reuses the same slug across goals.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy;

internal sealed class FlexibleGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected string for Guid, got {reader.TokenType}");

        var s = reader.GetString();
        if (string.IsNullOrEmpty(s))
            return Guid.Empty;

        if (Guid.TryParse(s, out var g))
            return g;

        // Not a Guid format (e.g. "goal-001"). Normalize to Empty so the
        // caller assigns a fresh unique id, rather than throwing and
        // dropping the whole goal.
        return Guid.Empty;
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        // Canonical dashed form for downstream tools / training data.
        writer.WriteStringValue(value.ToString("D"));
    }
}
