// SPDX-License-Identifier: AGPL-3.0-or-later
// GameMessageOpcode — only the opcodes the Phase 2/3/4 spike
// observes or emits. Full enum lives in
// Source/ACE.Server/Network/GameMessages/GameMessageOpcode.cs.
//
// Numeric values are u32 on the wire (written by
// GameMessage.cs:26 via `Writer.Write((uint)Opcode)`).

namespace HeadlessAcClient.Protocol.GameMessages;

internal enum GameMessageOpcode : uint
{
    // Phase 2/3 server → client
    PrivateUpdatePropertyInt = 0x02CD,
    CharacterCreateResponse  = 0xF643,
    CharacterList            = 0xF658,
    CharacterError           = 0xF659,
    ObjectCreate             = 0xF745,
    PlayerCreate             = 0xF746,
    UpdatePosition           = 0xF748,
    Motion                   = 0xF74C,
    GameEvent                = 0xF7B0,
    CharacterEnterWorldServerReady = 0xF7DF,
    ServerMessage            = 0xF7E0,
    ServerName               = 0xF7E1,
    DDDInterrogation         = 0xF7E5,

    // Phase 3 client → server
    CharacterDelete            = 0xF655,
    CharacterCreate            = 0xF656,
    // Phase 3.3 verified: 0xF657 is the CLIENT-SIDE commit message,
    // sent AFTER receiving 0xF7DF CharacterEnterWorldServerReady. The
    // "request" probe (0xF7C8) is what kicks the handshake off.
    CharacterEnterWorld        = 0xF657,
    CharacterEnterWorldRequest = 0xF7C8,

    // Phase 4 client → server: GameAction wrapper for post-world-
    // entry commands (LoginComplete, movement, chat, ...). Carries
    // an inner GameActionType opcode (e.g. 0x00A1 LoginComplete) +
    // a per-action sequence number that the server reads but does
    // not currently validate (see GameActionPacket.cs:13 "TODO: verify
    // sequence"). See Protocol/GameMessages/GameActionMessages.cs.
    GameAction                 = 0xF7B1,
}
