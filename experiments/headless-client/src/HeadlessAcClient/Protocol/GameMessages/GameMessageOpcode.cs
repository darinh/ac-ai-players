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
    // Self i64-valued property update (no guid; implicitly the bot's
    // own player, like PrivateUpdatePropertyInt). Carries player XP
    // totals (PropertyInt64 TotalExperience=1, AvailableExperience=2);
    // 64-bit because lifetime XP exceeds 2^31. See
    // GameMessagePrivateUpdatePropertyInt64.
    PrivateUpdatePropertyInt64 = 0x02CF,
    // Self vital FULL DESCRIPTOR update (Ranks/StartingValue/ExpSpent/
    // Current). "Private" = sent only to the receiving session, so it is
    // implicitly about the bot's own player (no guid on the wire), like
    // PrivateUpdatePropertyInt. Sent on rank-raises + full sync; the
    // health descriptor is keyed by MaxHealth (Vital==1).
    PrivateUpdateVital       = 0x02E7,
    // Self vital CURRENT-LEVEL update (per-tick). This is the packet ACE
    // sends on every damage/regen/death/respawn tick — the timely source
    // for the bot's own current HP. Layout: u8 seq | u32 vital | u32
    // current. The health current is keyed by Health (Vital==2), NOT
    // MaxHealth. See ACE GameMessagePrivateUpdateAttribute2ndLevel.
    PrivateUpdateAttribute2ndLevel = 0x02E9,
    // Self SKILL full-descriptor update (no guid; implicitly the bot's own
    // player). Sent on a skill rank-raise (RaiseSkill 0x0046) + full sync.
    // Layout: u8 seq | u32 skillId | u16 Ranks | u16 adjustPP | u32
    // AdvancementClass | u32 ExperienceSpent | u32 InitLevel | u32
    // resistance | f64 lastUsed. NOTE Ranks is u16 (CreatureSkill.Ranks is
    // ushort). See ACE GameMessagePrivateUpdateSkill.
    PrivateUpdateSkill       = 0x02DD,
    // Self ATTRIBUTE full-descriptor update (no guid; implicitly the bot's
    // own player). Sent on an attribute rank-raise (RaiseAttribute 0x0045)
    // + full sync. Layout: u8 seq | u32 attrId | u32 Ranks | u32
    // StartingValue | u32 ExperienceSpent. See
    // ACE GameMessagePrivateUpdateAttribute.
    PrivateUpdateAttribute   = 0x02E3,
    HearSpeech               = 0x02BB,
    CharacterCreateResponse  = 0xF643,
    CharacterList            = 0xF658,
    CharacterError           = 0xF659,
    ObjectCreate             = 0xF745,
    PlayerCreate             = 0xF746,
    UpdatePosition           = 0xF748,
    SetState                 = 0xF74B,
    Motion                   = 0xF74C,
    ObjectDelete             = 0xF747,
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
