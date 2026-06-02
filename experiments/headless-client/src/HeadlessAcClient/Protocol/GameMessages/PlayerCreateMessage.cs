// SPDX-License-Identifier: AGPL-3.0-or-later
// PlayerCreate (0xF746) - server tells client that the player's own
// avatar has materialized in the world.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessagePlayerCreate.cs
//
// Wire layout (8 bytes total):
//   u32  opcode = 0xF746
//   u32  guid   (ObjectGuid is a single u32 - verified Phase 3.2)
//
// Queue: SmartboxQueue (6).
//
// Semantics: arrives once per session, immediately after EnterWorld
// commit, carrying the GUID the player is now embodying. For us the
// captured value is 0x50000006 - matches the guid we sent in the
// 0xF657 CharacterEnterWorld commit. Subsequent 0xF74C Motion and
// 0xF745 ObjectCreate messages targeting this same guid describe
// our avatar's animation state and the surrounding world.

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record PlayerCreateMessage(uint Guid);
