// SPDX-License-Identifier: AGPL-3.0-or-later
// HearSpeech (0x02BB) - chat-text broadcast received by the player.
// Used for both NPC dialogue and other players' (and bots') chat.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageHearSpeech.cs
//
// Wire layout (variable):
//   u32          opcode (0x02BB)
//   String16L    messageText  (u16 len + ASCII/Latin-1 bytes + pad-to-4)
//   String16L    senderName   (same encoding)
//   u32          senderId
//   u32          chatMessageType (ChatMessageType enum)
//
// Sibling: HearRangedSpeech (0x02BC) is identical with an extra f32
// `range` field between senderId and chatMessageType — not yet
// observed in the Phase 4 firehose so deferred.

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record HearSpeechMessage(
    string Message,
    string SenderName,
    uint SenderId,
    uint ChatMessageType);
