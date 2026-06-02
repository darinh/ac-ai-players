// SPDX-License-Identifier: AGPL-3.0-or-later
// Mirrors Source/ACE.Server/Network/GameMessages/GameMessageGroup.cs
//
// The Queue field on PacketFragmentHeader (offset 14, u16) tags every
// game-message fragment with a dispatch class. Server's dispatch is
// keyed on opcode (not queue), so the Queue value rarely matters for
// correctness — but matching what real clients use keeps captures
// readable and avoids surprises in any code path that does inspect it.
//
// Real client→server character-management messages (CharacterCreate,
// CharacterDelete, CharacterEnterWorldRequest, etc.) all use UIQueue,
// per Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacter*.cs.

namespace HeadlessAcClient.Protocol.GameMessages;

internal enum GameMessageGroup : ushort
{
    InvalidQueue        = 0x00,
    EventQueue          = 0x01,
    ControlQueue        = 0x02,
    WeenieQueue         = 0x03,
    LoginQueue          = 0x04,
    DatabaseQueue       = 0x05,
    SecureControlQueue  = 0x06,
    SecureWeenieQueue   = 0x07,
    SecureLoginQueue    = 0x08,
    UIQueue             = 0x09,
    SmartboxQueue       = 0x0A,
    ObserverQueue       = 0x0B,
    QueueMax            = 0x0C,
}