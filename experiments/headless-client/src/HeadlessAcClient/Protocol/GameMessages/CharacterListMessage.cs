// SPDX-License-Identifier: AGPL-3.0-or-later
// CharacterListMessage — parsed GameMessageCharacterList payload
// (opcode 0xF658). Server-side encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacterList.cs

using System.Collections.Generic;

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record CharacterEntry(
    uint   Id,              // Character GUID (PropertyInt.Iid)
    string Name,            // May have a leading "+" if plussed/access-overridden
    uint   SecondsToDelete  // 0 = not pending deletion
);

internal sealed record CharacterListMessage(
    uint                    UnknownLeadingZero,  // Source writes 0u; semantics unknown
    IReadOnlyList<CharacterEntry> Characters,
    uint                    UnknownTrailingZero, // Source writes 0u; semantics unknown
    uint                    SlotCount,           // Server's `max_chars_per_account`
    string                  Account,             // String16L
    uint                    UseTurbineChat,      // 0 or 1
    uint                    HasThroneOfDestiny   // Server always writes 1
);
