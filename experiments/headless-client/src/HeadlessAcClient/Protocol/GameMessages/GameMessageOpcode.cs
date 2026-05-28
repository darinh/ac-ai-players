// SPDX-License-Identifier: AGPL-3.0-or-later
// GameMessageOpcode — only the opcodes the Phase 2/3 spike
// observes or emits. Full enum lives in
// Source/ACE.Server/Network/GameMessages/GameMessageOpcode.cs.
//
// Numeric values are u32 on the wire (written by
// GameMessage.cs:26 via `Writer.Write((uint)Opcode)`).

namespace HeadlessAcClient.Protocol.GameMessages;

internal enum GameMessageOpcode : uint
{
    CharacterList     = 0xF658,
    ServerName        = 0xF7E1,
    DDDInterrogation  = 0xF7E5,

    // Client → server, used by Phase 3.
    // Note: 0xF657 is `CharacterEnterWorld` (the server's confirmation
    // response). The client-side *request* lives at 0xF7C8.
    CharacterEnterWorldRequest = 0xF7C8,
    CharacterEnterWorld        = 0xF657,
}
