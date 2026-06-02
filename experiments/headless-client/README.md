# Headless AC1 Client — Phase 1 spike

Goal: prove we can complete the AC three-way login handshake
against a running ACE server using a hand-written client, with no
rendering and no dependency on the Turbine `acclient.exe` binary.

If this works, the bigger plan in
[`../../docs/research/headless-client/README.md`](../../docs/research/headless-client/README.md)
becomes viable. If it does not, the spike is dead.

## What this does

1. Opens a UDP socket on a free local port.
2. Builds a `LoginRequest` packet (account + password, plaintext,
   client version "1802"). Sends it to `<host>:<port>`.
3. Waits up to 10s for a `ConnectRequest` reply containing the
   server time, session cookie, client id, and two ISAAC seeds.
4. Builds a `ConnectResponse` packet echoing the cookie. Sends it
   to `<host>:<port+1>` (AC's handshake quirk — the third leg
   goes to a different port).
5. Listens 5s for any post-handshake packet (character list,
   server name, DDD interrogation). If anything arrives, the
   server accepted us. Exit 0.

## How to run

Build:

```powershell
dotnet build experiments/headless-client/HeadlessAcClient.sln -c Release
```

Run against the local ACE dev server (NSSM service on 9000/9001):

```powershell
dotnet run --project experiments/headless-client/src/HeadlessAcClient -c Release -- 127.0.0.1 9000 spike-bot some-password
```

Expected log on success:

```
[main]      target 127.0.0.1:9000, account 'spike-bot'
[handshake] bound UDP socket on 0.0.0.0:<port>
[handshake] sending LoginRequest (NN bytes) to 127.0.0.1:9000
[handshake] received NN bytes from 127.0.0.1:9000
[handshake]   header: Seq=0 Id=N Iter=0 Flags=ConnectRequest Size=32 ...
[handshake] received ConnectRequest:
             serverTime  = ...
             cookie      = 0x...
             clientId    = N
             serverSeed  = XX XX XX XX
             clientSeed  = XX XX XX XX
[handshake] sending ConnectResponse (28 bytes) to 127.0.0.1:9001
[handshake] received post-handshake packet (NN bytes) from ...
[main]      PHASE 1 PASS — server kept talking to us after the handshake.
```

If the server rejects us before ConnectRequest, the log shows the
header flags of whatever reply we did get (NetError, Disconnect,
or no reply at all). The most common causes:

- ACE not running on the target port. Check the NSSM service.
- Client version mismatch — ACE pins to "1802".
- `AllowAutoAccountCreation` disabled in the server config and
  the account doesn't exist.
- Account name longer than 50 chars (server hard-rejects).

## What this does NOT do

- No ISAAC stream cipher (Phase 2 work).
- No game-message parsing beyond reading the first 20 bytes of
  whatever the server replies with post-handshake.
- No Ack/Retransmit/TimeSync. The session dies after ~15s when
  the server times out our missing keepalive.
- No character selection or world entry.

## Files

- `src/HeadlessAcClient/Crypto/Isaac.cs` — ISAAC stream cipher.
  Verbatim from ACE. AGPL3. Used in Phase 2; included here so
  Phase 1's build covers it.
- `src/HeadlessAcClient/Crypto/Hash32.cs` — AC packet checksum.
  Verbatim from ACE. AGPL3.
- `src/HeadlessAcClient/Protocol/PacketHeaderFlags.cs` — wire
  constants. Derived from ACE.
- `src/HeadlessAcClient/Protocol/PacketHeader.cs` — 20-byte
  packet header pack/unpack.
- `src/HeadlessAcClient/Protocol/AcStrings.cs` — write side of
  AC's `string16L` and `string32L` encodings.
- `src/HeadlessAcClient/Handshake/HandshakeDriver.cs` — the
  three-step handshake.
- `src/HeadlessAcClient/Program.cs` — CLI entry point.
