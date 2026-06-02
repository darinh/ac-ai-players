// SPDX-License-Identifier: AGPL-3.0-or-later
// CharacterErrorMessage - decoded 0xF659 reply.
//
// Server-side encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacterError.cs
//
// Wire layout:
//   u32  opcode = 0xF659
//   u32  errorCode  (CharacterError enum)
//
// Common error codes (Source/ACE.Server/Network/Enum/CharacterError.cs):
//   0x01 Logon                  - Two accounts logged on simultaneously
//   0x03 AccountLogin           - Server cannot access account info
//   0x04 ServerCrash1           - Server disconnected
//   0x05 Logoff                 - Server cannot log off character
//   0x06 EnterGameCharacterNotOwned - GUID/account mismatch on EnterWorld
//   0x07 EnterGameCharacterInWorld  - Character already in world
//   0x08 EnterGameGeneric           - Generic enter-world failure
//   0x09 EnterGameCouldntPlaceCharacter - Heritage disabled etc.
//   0x0F LogonServerFull            - World closed or shutting down
//
// Decoded as a raw u32 here so we can log unknown codes without
// dropping them. The HandshakeDriver can pattern-match on specific
// values when it needs to.

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record CharacterErrorMessage(uint ErrorCode);
