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
//   0x01 Logon                          - Two accounts logged on simultaneously
//   0x03 AccountLogin                   - Server cannot access account info
//   0x04 ServerCrash1                   - Server disconnected
//   0x05 Logoff                         - Server cannot log off character
//   0x0B EnterGameGeneric               - Generic enter-world failure
//   0x0D EnterGameCharacterInWorld      - Character still in world (transient)
//   0x0E EnterGamePlayerAccountMissing  - Server cannot find player account
//   0x0F EnterGameCharacterNotOwned     - GUID/account mismatch on EnterWorld
//   0x10 EnterGameCharacterInWorldServer- Character currently in world (transient)
//   0x14 EnterGameCouldntPlaceCharacter - Heritage disabled etc.
//   0x15 LogonServerFull                - World closed or shutting down
//   0x17 EnterGameCharacterLocked       - A save is still in progress (transient)
// NOTE: these are the AUTHORITATIVE values from the ACE enum. (An earlier
// version of this comment mislabelled 0x06/0x07/0x09 — 0x06 is Delete,
// 0x07 is unused, 0x09 is AccountInvalid.)
//
// Decoded as a raw u32 here so we can log unknown codes without
// dropping them. The HandshakeDriver can pattern-match on specific
// values when it needs to.

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record CharacterErrorMessage(uint ErrorCode);
