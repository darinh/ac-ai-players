# 04 — Login handshake

The three-leg handshake establishes a session and the per-
direction ISAAC seeds before any game traffic flows.

## Sequence

```
 client                          server :9000           server :9001
   |                                |                       |
   |  1. LoginRequest               |                       |
   | -----------------------------> |                       |
   |     [account, password,        |                       |
   |      version="1802"]           |                       |
   |                                |                       |
   |                  (FindOrCreateSession)                 |
   |                  (Generate ServerSeed, ClientSeed,     |
   |                   ConnectionCookie, ClientId)          |
   |                                |                       |
   |  2. ConnectRequest             |                       |
   | <----------------------------- |                       |
   |     [serverTime, cookie,       |                       |
   |      clientId,                 |                       |
   |      serverSeed, clientSeed]   |                       |
   |                                |                       |
   |     Client init:                                       |
   |       cryptoSend = ISAAC(clientSeed)                   |
   |       cryptoRecv = ISAAC(serverSeed)                   |
   |                                                        |
   |  3. ConnectResponse                                    |
   | -----------------------------------------------------> |
   |     [cookie]                                           |
   |                                                        |
   |                                              (lookup by cookie,
   |                                               IP, state)
   |                                              session.State =
   |                                                AuthConnected
   |                                              HandleConnectResponse:
   |                                                push CharacterList,
   |                                                ServerName,
   |                                                DDDInterrogation
   |                                                        |
   |  4. AckSequence (Seq=0, body=01000000)                 |
   | <----------------------------- |                       |
   |                                |                       |
   |  5. game-message fragments     |                       |
   | <----------------------------- | (encrypted thereafter) |
```

The packet at line 4 is what we observed empirically in the
spike (server sends bare AckSequence every ~3.7s if the client
stays silent). The CharacterList push at line 5 is what we
expect once we send our first valid ACK back.

## State machine (server-side, mirror on client)

The server's `Session.State` field transitions:

```
            +--------+
            | New    |
            +---+----+
                |  receive LoginRequest
                v
   +-------------------------+
   | AuthLoginRequest        |  HandleLoginRequest runs;
   +-------------------------+  account verified;
                |               server sends ConnectRequest
                v
   +-------------------------+
   | AuthConnectResponse     |  waiting for client's
   +-------------------------+  ConnectResponse on port+1
                |
                |  receive ConnectResponse on :Port+1
                v
   +-------------------------+
   | AuthConnected           |  HandleConnectResponse runs;
   +-------------------------+  server pushes CharacterList;
                |               sendResync = true
                v
   +-------------------------+
   | WorldConnected          |  client has selected character
   +-------------------------+  and entered world

   (any state can transition to)
   +-------------------------+
   | Terminated              |
   +-------------------------+
```

**Source**: [`AuthenticationHandler.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Handlers/AuthenticationHandler.cs)
and the `SessionState` enum.

## Leg 1: LoginRequest

**Direction**: client → server, destination port = `Port`
(default 9000).

**Header**:

| field | value |
|---|---|
| `Sequence` | `0` |
| `Flags` | `LoginRequest` (`0x10000`) |
| `Checksum` | `headerChecksum + Hash32(body, body.length)` (no encryption — this is leg 1) |
| `Id` | `0` (no session id yet) |
| `Time` | `0` |
| `Size` | length of body in bytes |
| `Iteration` | `0` |

**Body** (in order, no padding except per string-type rules):

| field | type | description |
|---|---|---|
| `ClientVersion` | `string16L` | Must equal `"1802"`. The server asserts this in `AccountSelectCallback`. |
| `DataLen` | `u32` | Server reads but doesn't validate. Real clients seem to fill this with byte count of fields that follow (from `NetAuthType` through end). Setting to 0 also works. |
| `NetAuthType` | `u32` | `0 = Undefined, 1 = Unspecified, 2 = AccountPassword, 3 = GlsTicket`. Use `2`. |
| `AuthFlags` | `u32` | `0` for ordinary login. Bit `2` enables admin "login as" override. |
| `Timestamp` | `u32` | Client-side timestamp / nonce. Logged by server; not validated. |
| `Account` | `string16L` | Account name. Server lowercases it. |
| `AccountToLoginAs` | `string16L` | Empty unless `AuthFlags & 2`. |
| `Password` | `string32L` | Only present when `NetAuthType == 2`. |
| `GlsTicket` | `string32L` | Only present when `NetAuthType == 3`. Not implemented server-side as of this writing. |

**Source**: [`PacketInboundLoginRequest.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Packets/PacketInboundLoginRequest.cs).

### Server-side handling

1. `NetworkManager.ProcessPacket` sees `LoginRequest` flag and
   port == `Port`.
2. Checks server-full / shutdown gates.
3. Per-IP session-count limit check.
4. `FindOrCreateSession(connectionListener, endPoint)` —
   either reuses an existing session in `AuthLoginRequest`
   state from the same endpoint, or allocates a new session id
   (1..N) and creates a `SessionConnectionData` with:
   - `ServerSeed` = random 4 bytes
   - `ClientSeed` = random 4 bytes
   - `ConnectionCookie` = random `u64`
   - `CryptoClient` = `new CryptoSystem(ClientSeed)` (used to
     verify *incoming* client packets)
   - `IssacServer` = `new ISAAC(ServerSeed)` (used to encrypt
     *outgoing* server packets)
5. Calls `Session.ProcessPacket(packet)` which calls
   `HandleLoginRequest`, which parses the body into a
   `PacketInboundLoginRequest`.
6. Account lookup. If unknown and
   `Config.Server.Accounts.AllowAutoAccountCreation` is true
   (default on dev), auto-creates an account with the supplied
   password (lowercased account name).
7. `AccountSelectCallback` builds a `PacketOutboundConnectRequest`
   and **enqueues it for send immediately** (leg 2 fires now,
   on the world-manager thread).
8. `account.PasswordMatches(loginRequest.Password)` runs
   bcrypt. At work-factor 8 this costs ~20-30 ms.
9. Other gates: account-in-use, ban-expire, last-login update.
10. `session.State = SessionState.AuthConnectResponse`.

Steps 7-10 run sequentially on one thread, but step 7's
enqueue is asynchronous — leg 2 is on the wire ~µs after
step 7. Step 10 doesn't happen until step 8 (bcrypt)
completes. **The client receives leg 2 long before the
server is willing to accept leg 3.** See
[Race condition with server-side bcrypt password verification](#race-condition-with-server-side-bcrypt-password-verification)
below.

### Failure modes

| Server response | Client symptom | Cause |
|---|---|---|
| Server logs `Bad handshake`, no reply | Timeout on leg 2 | CRC failed on LoginRequest |
| `GameMessageBootAccount("client is not the correct version")` (sent and session terminated immediately) | Disconnect packet from server | `ClientVersion != "1802"` |
| `CharacterError.LogonServerFull` | Reject packet | Server full or per-IP cap exceeded |
| `CharacterError.AccountInvalid` | Reject packet | `NetAuthType < AccountPassword` (no password supplied) |
| `CharacterError.AccountDoesntExist` | Reject packet | Account unknown and auto-create disabled |
| Server logs `non matching password so booting`, sends `GameMessageBootAccount(" because the password entered for this account was not correct")` | Disconnect packet | Bad password (auto-created accounts can't hit this; existing accounts can) |
| Server logs `AccountInUse` | Reject packet | Account already has a live session and `account_login_boots_in_use` is false |
| `CharacterError.AccountBanned` | Reject packet | Account is in ban window |

## Leg 2: ConnectRequest

**Direction**: server → client. Sent from server port `Port`
to the client's source port (the ephemeral port the client's
LoginRequest came from).

**Header**:

| field | value |
|---|---|
| `Sequence` | `0` |
| `Flags` | `ConnectRequest` (`0x40000`) |
| `Checksum` | computed by server |
| `Id` | session id assigned by `FindOrCreateSession` |
| `Time` | `0` |
| `Size` | `32` |
| `Iteration` | `1` |

**Body** (32 bytes):

| offset | size | type | field | description |
|---|---|---|---|---|
| 0 | 8 | `f64` | `ServerTime` | `Timers.PortalYearTicks` — server's clock at issue time |
| 8 | 8 | `u64` | `ConnectionCookie` | Random nonce. Client must echo this back in leg 3. |
| 16 | 4 | `u32` | `ClientId` | Server-assigned session-map index. **All future client-to-server packets must put this value in `Header.Id`.** Do NOT confuse with the `Header.Id` on leg 2 itself — that one is the server's `ServerId` constant (typically `11`). See [`spec/02-network.md` "Header.Id is not symmetric"](../spec/02-network.md#packet-header). |
| 20 | 4 | `bytes[4]` | `ServerSeed` | ISAAC seed for server → client stream |
| 24 | 4 | `bytes[4]` | `ClientSeed` | ISAAC seed for client → server stream |
| 28 | 4 | `u32` | `Padding` | Always `0`. Comment in source: "Padding for alignment?" |

**Source**: [`PacketOutboundConnectRequest.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Packets/PacketOutboundConnectRequest.cs).

### Client-side handling

1. Parse the 32-byte body. Save `ConnectionCookie`, `ClientId`,
   `ServerSeed`, `ClientSeed`.
2. Initialize two ISAAC instances:
   - `cryptoSend = new CryptoSystem(ClientSeed)` — for
     out-going packet checksums (the lookahead window isn't
     strictly needed for sending since the sender controls
     ordering, but we use `CryptoSystem` for symmetry).
   - `cryptoRecv = new CryptoSystem(ServerSeed)` — for
     verifying incoming server packet checksums.
3. Discard `ServerSeed` and `ClientSeed` (the server does too,
   in `ConnectionData.DiscardSeeds()`).
4. Build and send leg 3.

⚠ A client SHOULD verify the checksum on this packet, but the
Phase 1 spike skipped that step (the formula needs the body
bytes that the server captured into the headerOptional
checksum, and `ConnectRequest` is not in the server's
headerOptional-capture list — so on the client side we treat
the inbound CRC as advisory only for handshake packets).

## Leg 3: ConnectResponse

**Direction**: client → server, destination port = `Port + 1`
(default 9001). NOT `Port`. This is the critical routing
difference.

**Header**:

| field | value |
|---|---|
| `Sequence` | `0` |
| `Flags` | `ConnectResponse` (`0x80000`) |
| `Checksum` | `headerChecksum + Hash32(cookie_bytes, 8)` |
| `Id` | `ClientId` from leg 2 |
| `Time` | `0` |
| `Size` | `8` |
| `Iteration` | `1` |

**Body** (8 bytes):

| offset | size | type | field | description |
|---|---|---|---|---|
| 0 | 8 | `u64` | `Cookie` | Echo `ConnectionCookie` from leg 2 verbatim |

### Server-side handling

1. `NetworkManager.ProcessPacket` runs on the listener for
   `Port + 1`.
2. Checks `ConnectResponse` flag. If absent and not
   `CICMDCommand`, logs error and drops.
3. Parses 8 bytes as `Check = packet.DataReader.ReadUInt64()`.
4. Walks `sessionMap` looking for a session where
   `State == AuthConnectResponse`,
   `ConnectionCookie == Check`, and `IP matches`.
5. If found:
   - `session.SetS2CEndpoint(endPoint)` — record the ephemeral
     port we sent leg 3 from. Future server-to-client packets
     go to this endpoint.
   - `session.State = SessionState.AuthConnected`.
   - `session.Network.sendResync = true` — schedule a
     `TimeSync` push.
   - `AuthenticationHandler.HandleConnectResponse(session)` —
     fetches characters from the shard DB and sends back
     `GameMessageCharacterList`, `GameMessageServerName`,
     `GameMessageDDDInterrogation`.
6. If not found, the packet is silently dropped.

### Why port+1?

This is a server-side dispatch optimization. The `Port`
listener is hot — it handles all post-handshake game traffic
plus new LoginRequests. The `Port+1` listener handles only the
two flag types that need special routing (`ConnectResponse`
and `CICMDCommand`). This lets the server `Port+1` handler
skip the per-session `sessionMap[Id]` lookup and instead do a
direct cookie-based search through pending handshakes.

For client implementers, the practical rule: **after parsing
the ConnectRequest reply, send your ConnectResponse to a
destination port of `original_port + 1`**. Don't change your
local source port; just change the destination.

## Race condition with server-side bcrypt password verification

**Symptom**: client completes legs 1-3 cleanly, server logs
`(Packets) <<< Seq: 0 ... ConnectResponse`, but no game data
ever flows. Server retransmits empty `AckSequence` packets
every ~3.7s for 17s and then logs
`Session ... dropped ... Reason: Network Timeout`.

**Root cause**: the server's session state transition is
*gated on bcrypt completion*, but the leg 2 reply is sent
*before* bcrypt runs.

Concretely, in
[`AuthenticationHandler.AccountSelectCallback`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Handlers/AuthenticationHandler.cs#L101):

| line | action | wall-clock cost |
|---|---|---|
| 127 | `session.Network.EnqueueSend(connectRequest)` (leg 2 on the wire) | ~µs |
| 167 | `account.PasswordMatches(loginRequest.Password)` (bcrypt) | ~20-30 ms (work-factor 8) |
| 232 | `session.State = SessionState.AuthConnectResponse` | ~µs |

Meanwhile,
[`NetworkManager.ProcessPacket`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/NetworkManager.cs#L50)
on port `Port+1` looks up the matching session with the
predicate `k.State == SessionState.AuthConnectResponse`. If
the session is still in `AuthLoginRequest` because bcrypt
hasn't finished yet, the lookup returns `null` and the
packet is silently dropped (no rejection log; the
`if (session == null)` arm at line 80 just falls through).

On loopback, a co-hosted spike's RTT is ~0.5 ms but bcrypt
takes ~20-30 ms. The client always wins the race; the
ConnectResponse is always dropped. The session sits in
`AuthConnectResponse` state forever, server emits
`HandleConnectResponse` AckSequence retries on a 3.7s timer,
client sends nothing back, and the server hits the 17s
network-timeout and tears down.

**Workaround (client-side)**: retransmit ConnectResponse a
few times with a short delay, well past bcrypt's worst-case
completion time. Three sends 100 ms apart covers it on
work-factor 8; bump to 5×150 ms if your server uses a
higher work factor.

The duplicate ConnectResponses that arrive after the state
transition are benign — the server's session lookup
succeeds on whichever one arrives first after state is set,
and the rest see `session.State == AuthConnected` (no
longer `AuthConnectResponse`) and are silently dropped by
the same `session == null` arm.

The Phase 1 spike implements this in
[`HandshakeDriver.cs`](../../../../experiments/headless-client/src/HeadlessAcClient/Handshake/HandshakeDriver.cs)
(`ConnectResponseRetries` and `ConnectResponseRetryDelayMs`).

**Cleaner fixes that are out of scope for the spike**:

- *Server-side*: move `session.State = AuthConnectResponse`
  ahead of `EnqueueSend(connectRequest)` in
  `AccountSelectCallback`. Bcrypt could then complete in the
  background while leg 3 is in-flight; on failure, terminate
  the session as today. This would fix the race for all
  clients including future real ones with tighter latency
  budgets. (It's a ~3-line patch but touches a sensitive
  auth path; defer until we own a fork branch dedicated to
  upstream-able server improvements.)
- *Client-side smarter*: after sending the first
  ConnectResponse, poll for the first inbound packet with
  `Sequence > 0` or `BlobFragments` flag. Stop retransmitting
  on first sight. Saves 200-400 ms in the common case and is
  the right shape once we have a recv loop running anyway.

## After leg 3

The server starts pushing game-message packets to the
endpoint it captured at leg 3. These packets:

- Have `Flags = BlobFragments` set.
- Have a real `Sequence` (starting at 1).
- Carry `GameMessageCharacterList` as the first significant
  payload.
- Are NOT yet `EncryptedChecksum`-flagged on the first few
  packets (the server's `sendResync` flag is true, so the
  first emission is the `TimeSync` push).
- After the `TimeSync`, packets carrying game messages have
  `EncryptedChecksum` set and require the client's
  `cryptoRecv` to verify the checksum.

The client SHOULD:

1. Send an `AckSequence` for `Sequence = 1` (and increment as
   server sequences arrive).
2. Send a `TimeSync` in response to the server's `TimeSync`.
3. Parse `GameMessageCharacterList` and present characters to
   the bot brain for selection.
4. Send `GameActionCharacterEnterWorld` with the chosen
   character GUID.

Game-message details are in `06-game-messages.md` (stub).

## Phase 1 spike result reference

The Phase 1 spike did legs 1-3 and observed legs 4-N for 30s
without sending any further response. Server kept retrying its
AckSequence push every ~3.7s. No teardown observed in that
window. See [`../phase1-results.md`](../phase1-results.md).
