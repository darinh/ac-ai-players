# Phase 1 result: handshake driver

**Status:** PASS on first run. No iteration required.

**Date:** 2026-05-28
**Branch:** `anvil/headless-client-spike`
**Server:** ACE NSSM service on `127.0.0.1:9000/9001`
**Client binary:** `experiments/headless-client/src/HeadlessAcClient/bin/Release/net10.0/HeadlessAcClient.dll`

## Goal

Prove a hand-written .NET UDP client can complete the AC
three-way login handshake against ACE, with no rendering, no
Turbine `acclient.exe`, and no in-process bot tricks.

If this works, the larger plan in
[`README.md`](README.md) is viable. If it does not, the spike
is dead and we go back to fixing `BotPlayer`.

## Method

Built the spike in `experiments/headless-client/`. Ran:

```powershell
dotnet run --project experiments\headless-client\src\HeadlessAcClient `
    -c Release --no-build -- 127.0.0.1 9000 spike-bot Test1234!
```

## Client-side log

```
[main]      target 127.0.0.1:9000, account 'spike-bot'
[handshake] bound UDP socket on 0.0.0.0:50765
[handshake] sending LoginRequest (76 bytes) to 127.0.0.1:9000
[handshake] received 52 bytes from 127.0.0.1:9000
[handshake]   header: Seq=0 Id=11 Iter=1 Flags=ConnectRequest Size=32 CRC=0xE2BAD90A
[handshake] received ConnectRequest:
             serverTime  = 294098305.3386851
             cookie      = 0x884AAFC027FCCB61
             clientId    = 1
             serverSeed  = 73fe04a2
             clientSeed  = c7b2ce7c
[handshake] sending ConnectResponse (28 bytes) to 127.0.0.1:9001
[handshake] received post-handshake packet (24 bytes) from 127.0.0.1:9000
[handshake]   header: Seq=0 Id=11 Iter=1 Flags=AckSequence Size=4 CRC=0x5079B0ED
[main] PHASE 1 PASS — server kept talking to us after the handshake.
```

## Server-side log (`C:\ACE\Logs\ACE_Log.txt`)

```
2026-05-28 07:58:25,644 DEBUG NetworkManager: Creating new session for 127.0.0.1:50765 with id 1
2026-05-28 07:58:25,645 INFO  AuthenticationHandler: Auto creating account for: spike-bot
2026-05-28 07:58:25,670 DEBUG AuthenticationHandler: new client connected: spike-bot. setting session properties
2026-05-28 07:58:25,693 INFO  AuthenticationHandler: client spike-bot connected with verified password
```

Both sides agree: the LoginRequest was parsed, the account was
auto-created (server has `AllowAutoAccountCreation=true` in the
dev config), the password was hashed and stored, the session
transitioned `AuthLoginRequest → AuthConnectResponse →
AuthConnected`, and the server began sending sequenced game
packets (the first being an `AckSequence`).

## What this tells us

- The packet header pack/unpack code is correct (server didn't
  drop the LoginRequest, and we successfully unpacked the
  ConnectRequest).
- `Hash32` is correct (otherwise server's
  `ClientPacket.VerifyCRC` would have failed the LoginRequest;
  it instead happily called `HandleLoginRequest`).
- `string16L` and `string32L` writers are correct.
- The two-port quirk (port for legs 1+2, port+1 for leg 3) is
  handled correctly.
- Auto-account-creation works against this dev server, so we
  don't need to pre-create accounts.

## What this does NOT validate

- No ISAAC stream cipher yet — the handshake is all plaintext.
  The `AckSequence` we received has `EncryptedChecksum` cleared
  because it was emitted before the server cut over (sequence
  0). The next packet from the server will be encrypted and our
  current code can't validate it.
- No retransmit / Ack / TimeSync — the session dies within ~15s
  when the server times out our missing keepalive.
- No character list parsing — we received post-handshake packets
  but only logged the first 20-byte header.

## Next: Phase 2

Implement the ISAAC stream window (`CryptoSystem` port) and the
sequence-number / ack flow. Goal: stay connected past 15s
without the server tearing us down, and parse the
`CharacterList` message so we know what characters exist on the
account.

See [`README.md`](README.md) §Phases for the full roadmap.

---

## Update 2026-05-28: original "PASS" was incomplete

Re-running with `Packets` log level at DEBUG revealed that the
original "PHASE 1 PASS" claim was over-stated. What looked like
post-handshake server traffic was actually the server's
**`HandleConnectResponse` retry loop** — empty `AckSequence`
packets (`Size=4`, body `01 00 00 00`) emitted every ~3.7s,
followed by a `Network Timeout` session teardown at 17s.

The session never reached `SessionState.AuthConnected`. No
`CharacterList`, `ServerName`, or `DDDInterrogation` was ever
pushed. The first `AckSequence` we received and called a "pass"
was the server in `AuthConnectResponse` state waiting for the
client's ACK that never came — because the server didn't
realize our ConnectResponse had arrived.

**Why**: a race condition in
`AuthenticationHandler.AccountSelectCallback`: the server sends
the leg-2 reply immediately, but the session state transition
to `AuthConnectResponse` is gated on bcrypt password
verification (~20-30 ms at work-factor 8). On loopback the
client's ConnectResponse arrives in ~0.5 ms and loses the race
— the `NetworkManager.ProcessPacket` lookup on port `+1` finds
no matching session and silently drops the packet.

See
[`spec/04-handshake.md` § "Race condition with server-side
bcrypt password verification"](spec/04-handshake.md#race-condition-with-server-side-bcrypt-password-verification)
for the full diagnosis and the per-line server source
references.

### Fix

`HandshakeDriver.cs` now retransmits ConnectResponse 3× with
100 ms gaps. Constants: `ConnectResponseRetries = 3`,
`ConnectResponseRetryDelayMs = 100`.

### Real Phase 1 PASS (post-fix run output)

After the retransmit fix and with `Packets` logger at DEBUG,
we observe the server actually entering `AuthConnected` and
pushing real game data:

```
[observe] #1 from 127.0.0.1:9001: Seq=2 Id=11 Iter=1 Flags=EncryptedChecksum, TimeSync Size=8
[observe]   body[8]: ba f5 d0 38 9f 87 b1 41

[observe] #2 from 127.0.0.1:9001: Seq=3 Id=11 Iter=1 Flags=EncryptedChecksum, BlobFragments Size=44
[observe]   body[44]: 00 00 00 00 00 00 00 80 01 00 2c 00 00 00 05 00
                       e5 f7 00 00 01 00 00 00 01 00 00 00 01 00 00 00
                       02 00 00 00 00 00 00 00 01 00 00 00
   ↑ CharacterList (group 5, opcode 0xf7e5, "0 characters this account")

[observe] #3 from 127.0.0.1:9001: Seq=4 Id=11 Iter=1 Flags=EncryptedChecksum, BlobFragments Size=100
[observe]   body[100]: ... 0d 00 'h' 'e' 'a' 'd' 'l' 'e' 's' 's' '-' 't' 'e' 's' 't' 00 ...
                       ... 09 00 e1 f7 ... 0a 00 'A' 'C' 'E' 'm' 'u' 'l' 'a' 't' 'o' 'r' ...
   ↑ Two fragments: ServerName ("headless-test" / "ACEmulator")
     and DDDInterrogation header (opcode 0xf7e1).
```

The session still hits a Network Timeout at +17s because the
spike doesn't yet send Acks or TimeSync responses — that's
the Phase 2 work. But the **handshake itself is now
demonstrably complete** end-to-end: server reached
`AuthConnected`, `HandleConnectResponse` ran, `CharacterList`
+ `ServerName` + `DDDInterrogation` pushed, packet sequence
numbers advancing (`Seq=2`, `3`, `4`), `EncryptedChecksum`
flag set on game traffic exactly as documented.

The original "What this does NOT validate" list still applies
(no ISAAC verification, no Ack/TimeSync, no CharacterList
parse) — none of that changes. What changes is that we now
*see* the data we need to start parsing in Phase 2, where
previously we saw only the server's "are you alive?" retries.

