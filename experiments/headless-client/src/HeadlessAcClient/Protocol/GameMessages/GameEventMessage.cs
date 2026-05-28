// SPDX-License-Identifier: AGPL-3.0-or-later
// GameEventMessage - decoded 0xF7B0 envelope header.
//
// Server encoder (base class):
//   Source/ACE.Server/Network/GameEvent/GameEventMessage.cs:14-26
//
// Wire layout (16-byte header, then event-specific payload):
//   u32  opcode = 0xF7B0
//   u32  receiverGuid       (session.Player.Guid, or 0 if logged out)
//   u32  serverEventSequence (auto-increment per server-side
//                              GameEventSequence++; allows the
//                              client to reorder out-of-order
//                              events. We log it but don't enforce.)
//   u32  eventType           (GameEventType enum)
//   ...  event-specific payload (length varies per eventType;
//                                NOT decoded at this layer - the
//                                caller can inspect PayloadBytes)
//
// Phase 4.5 scope: decode header only. Per-event payloads are
// decoded on demand as we hit events we care about (e.g. UpdateHealth,
// Tell, IdentifyObjectResponse, PlayerDescription).

using System;

namespace HeadlessAcClient.Protocol.GameMessages;

/// <summary>
/// Decoded 0xF7B0 envelope. <see cref="PayloadBytes"/> is the
/// post-header slice; decode it with an event-specific parser
/// keyed off <see cref="EventType"/>.
/// </summary>
internal sealed record GameEventMessage(
    uint ReceiverGuid,
    uint ServerEventSequence,
    GameEventType EventType,
    ReadOnlyMemory<byte> PayloadBytes)
{
    /// <summary>16 = u32 opcode + u32 guid + u32 seq + u32 eventType.</summary>
    public const int HeaderSize = 16;
}
