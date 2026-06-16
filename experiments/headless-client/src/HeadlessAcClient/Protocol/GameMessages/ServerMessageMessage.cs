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
// ChatMessageType is the AUTHORITATIVE sequential enum from
//   Source/ACE.Entity/Enum/ChatMessageType.cs
// (a single channel-id per message, NOT a bitwise squelch mask). Values
// delivered on this 0xF7E0 ServerMessage opcode (per that enum's header) are
// 0x00, 0x03, 0x04, 0x05, 0x06, 0x07, 0x0D, 0x10, 0x11, 0x17, 0x18:
//   0x00 Broadcast (default/system status)
//   0x03 Tell          0x04 OutgoingTell    0x05 System
//   0x06 Combat        0x07 Magic           0x0D Advancement (level/xp)
//   0x10 Appraisal     0x11 Spellcasting    0x17 Recall      0x18 Craft
// Combat/Magic/Spellcasting (0x06/0x07/0x11) are the high-frequency per-action
// feedback channels. We decode the value raw and let downstream logic
// interpret it.

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record ServerMessageMessage(string Text, int ChatMessageType);
