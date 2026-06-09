// SPDX-License-Identifier: AGPL-3.0-or-later
// InventoryRemoveObject (0x0024) — server announces that an item has
// left the local-player inventory (a successful give to an NPC/player,
// a drop, a use that consumes/transforms the item, or a sale). The bot
// must drop the guid from its WorldState so its inventory perception
// stays in sync with the server; otherwise a later give/use of the
// removed guid is refused ("Item not found!") and the bot loops on a
// phantom item.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageInventoryRemoveObject.cs
//   (GameMessageOpcode.InventoryRemoveObject, GameMessageGroup.UIQueue, 8 bytes)
//
// Wire layout (fixed 8 bytes, body cursor-relative):
//   u32 opcode (0x0024)
//   u32 guid   (the item being removed)
//
// Distinct from ObjectDelete (0xF747), which removes a WorldObject from
// the visible world. World-space removal flows through ObjectDelete;
// inventory removal flows through this message.

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record InventoryRemoveObjectMessage(uint Guid)
{
    public const int PackedSize = 8;
}
