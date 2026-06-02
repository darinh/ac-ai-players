// SPDX-License-Identifier: AGPL-3.0-or-later
// Constants and enums for the AC wire protocol packet header.
// Bit layout mirrors ACE-bots:
//   Source/ACE.Server/Network/PacketHeaderFlags.cs
//   Source/ACE.Server/Network/PacketHeader.cs

using System;

namespace HeadlessAcClient.Protocol;

[Flags]
internal enum PacketHeaderFlags : uint
{
    None                = 0x00000000,
    Retransmission      = 0x00000001,
    EncryptedChecksum   = 0x00000002,
    BlobFragments       = 0x00000004,
    ServerSwitch        = 0x00000100,
    LogonServerAddr     = 0x00000200,
    EmptyHeader1        = 0x00000400,
    Referral            = 0x00000800,
    RequestRetransmit   = 0x00001000,
    RejectRetransmit    = 0x00002000,
    AckSequence         = 0x00004000,
    Disconnect          = 0x00008000,
    LoginRequest        = 0x00010000,
    WorldLoginRequest   = 0x00020000,
    ConnectRequest      = 0x00040000,
    ConnectResponse     = 0x00080000,
    NetError            = 0x00100000,
    NetErrorDisconnect  = 0x00200000,
    CICMDCommand        = 0x00400000,
    TimeSync            = 0x01000000,
    EchoRequest         = 0x02000000,
    EchoResponse        = 0x04000000,
    Flow                = 0x08000000,
}

internal enum NetAuthType : uint
{
    Undef           = 0,
    AccountPasswordOld = 1,
    AccountPassword = 2,
    GlsTicket       = 3,
}
