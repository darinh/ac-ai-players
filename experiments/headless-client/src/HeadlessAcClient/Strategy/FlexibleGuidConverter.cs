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

        throw new JsonException($"Could not parse Guid from '{s}'");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        // Canonical dashed form for downstream tools / training data.
        writer.WriteStringValue(value.ToString("D"));
    }
}
