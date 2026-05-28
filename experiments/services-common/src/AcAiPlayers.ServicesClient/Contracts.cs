// SPDX-License-Identifier: AGPL-3.0-or-later
// Shared MessagePack contracts. Source-gen formatters live next
// to the contract types; reflection-based fallback is disabled
// in MessagePackSerializerOptions on both sides.

using MessagePack;

namespace AcAiPlayers.Services.Contracts;

[MessagePackObject]
public sealed class PlanRequest
{
    [Key(0)] public string Goal { get; init; } = "";
    [Key(1)] public string VocabularyVersion { get; init; } = "v1";
    [Key(2)] public PerceptionSnapshot? Perception { get; init; }
}

[MessagePackObject]
public sealed class PerceptionSnapshot
{
    [Key(0)] public SelfState? Self { get; init; }
    [Key(1)] public LocationState? Location { get; init; }
    [Key(2)] public NearbyObject[] Near { get; init; } = System.Array.Empty<NearbyObject>();
}

[MessagePackObject]
public sealed class SelfState
{
    [Key(0)] public uint Guid { get; init; }
    [Key(1)] public string Name { get; init; } = "";
    [Key(2)] public int Level { get; init; }
    [Key(3)] public int Health { get; init; }
    [Key(4)] public int Stamina { get; init; }
    [Key(5)] public int Mana { get; init; }
}

[MessagePackObject]
public sealed class LocationState
{
    [Key(0)] public uint LandblockId { get; init; }
    [Key(1)] public float X { get; init; }
    [Key(2)] public float Y { get; init; }
    [Key(3)] public float Z { get; init; }
    [Key(4)] public float Heading { get; init; }
}

[MessagePackObject]
public sealed class NearbyObject
{
    [Key(0)] public uint Guid { get; init; }
    [Key(1)] public string Name { get; init; } = "";
    [Key(2)] public uint Wcid { get; init; }
    [Key(3)] public float DistanceMeters { get; init; }
    [Key(4)] public string Kind { get; init; } = "";
}

[MessagePackObject]
public sealed class PlanResponse
{
    [Key(0)] public string PlanId { get; init; } = "";
    [Key(1)] public string Vocabulary { get; init; } = "fetch";
    [Key(2)] public PlanOp[] Ops { get; init; } = System.Array.Empty<PlanOp>();
    [Key(3)] public string Model { get; init; } = "";
    [Key(4)] public string TraceId { get; init; } = "";
}

[MessagePackObject]
public sealed class PlanOp
{
    [Key(0)] public string Op { get; init; } = "";
    [Key(1)] public System.Collections.Generic.Dictionary<string, string> Args { get; init; } = new();
}

[MessagePackObject]
public sealed class TrainingEventRequest
{
    [Key(0)] public string EventKind { get; init; } = "";
    [Key(1)] public long TimestampUnixMs { get; init; }
    [Key(2)] public string Summary { get; init; } = "";
    [Key(3)] public byte[] PayloadMsgpack { get; init; } = System.Array.Empty<byte>();
}
