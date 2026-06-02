// SPDX-License-Identifier: AGPL-3.0-or-later
// CharacterCreateResponseMessage - decoded 0xF643 reply.
// Server-side encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacterCreateResponse.cs
//
// Wire layout:
//   u32         Response          // CharacterGenerationVerificationResponse
//   --- ONLY if Response == Ok ---
//   u32         guid lo           // ObjectGuid is written as u32 by WriteGuid
//   string16L   Name
//   u32         trailing zero     // server writes 0u; semantics unknown
//
// Critical: the optional fields are present ONLY on Ok. Eagerly
// reading them on any non-Ok response will overread the fragment
// and corrupt downstream cursor positions if/when we batch multiple
// game messages in one fragment.

namespace HeadlessAcClient.Protocol.GameMessages;

/// <summary>
/// Mirrors <c>ACE.Server.Network.Enum.CharacterGenerationVerificationResponse</c>.
/// </summary>
internal enum CharacterCreateResponse : uint
{
    Undef                = 0,
    Ok                   = 1,
    Pending              = 2,
    NameInUse            = 3,
    NameBanned           = 4,
    Corrupt              = 5,
    DatabaseDown         = 6,
    AdminPrivilegeDenied = 7,
    Count                = 8,
}

internal sealed record CharacterCreateResponseMessage(
    CharacterCreateResponse Response,
    uint   CharacterGuid,   // 0 unless Response == Ok
    string Name,            // "" unless Response == Ok
    uint   TrailingZero     // 0; server always writes 0
);
