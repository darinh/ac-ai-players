// SPDX-License-Identifier: AGPL-3.0-or-later
// ServerNameMessage — parsed GameMessageServerName payload
// (opcode 0xF7E1). Server-side encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageServerName.cs

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record ServerNameMessage(
    int    CurrentConnections,
    int    MaxConnections,    // -1 = unlimited in source; coerces to large positive on wire
    string ServerName         // String16L, e.g. "ACEmulator"
);
