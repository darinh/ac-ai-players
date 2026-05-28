// SPDX-License-Identifier: AGPL-3.0-or-later
// DDDInterrogationMessage — parsed GameMessageDDDInterrogation
// payload (opcode 0xF7E5). Server-side encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageDDDInterrogation.cs
//
// The server emits this immediately after CharacterList to ask
// the client to declare its data-file versions. The Phase 2 spike
// only observes/parses it; the client-side response
// (DDDInterrogationResponse 0xF7E6) is a Phase 3+ concern.

using System.Collections.Generic;

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record DDDInterrogationMessage(
    uint              ServersRegion,
    uint              NameRuleLanguage,
    uint              ProductId,
    IReadOnlyList<uint> SupportedLanguages
);
