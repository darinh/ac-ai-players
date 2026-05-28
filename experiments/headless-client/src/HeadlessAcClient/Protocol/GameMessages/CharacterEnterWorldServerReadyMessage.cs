// SPDX-License-Identifier: AGPL-3.0-or-later
// CharacterEnterWorldServerReadyMessage - decoded 0xF7DF reply.
//
// Server-side encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacterEnterWorldServerReady.cs
//
// Wire layout: opcode only. No payload.
//
// Semantics: server is saying "ok, you may now send CharacterEnterWorld
// (0xF657) with your chosen character guid + account name."

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record CharacterEnterWorldServerReadyMessage();
