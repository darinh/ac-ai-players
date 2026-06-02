// SPDX-License-Identifier: AGPL-3.0-or-later
// ServerMessage (0xF7E0) - server chat / system text channel.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageSystemChat.cs
//
// Wire layout (variable):
//   u32       opcode = 0xF7E0
//   string16L text
//   i32       chatMessageType  (ChatMessageType enum)
//
// Queue: UIQueue (9).
//
// Common ChatMessageType values (from Source/ACE.Entity/Enum/ChatMessageType.cs):
//   0x01  WorldBroadcast
//   0x02  Combat
//   0x04  Magic
//   0x08  Help
//   0x10  Emote
//   0x20  Channel
//   0x100 Allegiance
//   0x200 Tell
//   0x400 Patron
//   0x800 Vassal
// The enum is [Flags]-style on the client display side but the
// wire value is a single channel-id; we decode it raw and let
// downstream logic interpret.

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record ServerMessageMessage(string Text, int ChatMessageType);
