// SPDX-License-Identifier: AGPL-3.0-or-later
// Phase 1 handshake driver.
//
// The AC three-way login handshake:
//   1. C ->  S(port  ): LoginRequest         (account+password, plaintext)
//   2. C <-  S(port  ): ConnectRequest       (cookie + ISAAC seeds, plaintext)
//   3. C ->  S(port+1): ConnectResponse      (echo cookie, plaintext)
//
// After step 3 the server transitions our Session into AuthConnected
// and starts pushing CharacterList / ServerName / DDDInterrogation
// game messages on the original port.
//
// This driver does steps 1-3 and reports the seeds + cookie it
// extracted. It does NOT yet handle game messages, sequencing,
// retransmit, or the ISAAC stream cipher. Those land in later
// phases.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using HeadlessAcClient.Crypto;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;

using AcAiPlayers.WorldNav;
using HeadlessAcClient.Tactics;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Protocol;

internal sealed class HandshakeDriver : IDisposable
{
    private const int RecvBufferSize = 1024;
    private const int ClientVersion = 1802;
    // Phase 7f.5 — bumped from 1800 → 3600 (60 min). With the
    // exploration fallback, the bot keeps making progress past 30
    // min: in phase7f5-headless21-fullrun.log it killed 5 golems
    // and got the Academy Exit Token within 23 cycles / 30 min,
    // but ran out of time before reaching the Calling Stone (the
    // exit mechanism, sometimes 60+u away across multiple cells).
    // 60 min gives ~75-100 cycles of work, enough to reach the
    // Calling Stone, USE the exit, and start surveying landblock
    // 0x86020188 (the academy exit destination — needs research).
    private const int ObserveSeconds = 3600;

    // ConnectResponse retransmit constants — see spec/04-handshake.md
    // "Race condition with server-side bcrypt password verification".
    private const int ConnectResponseRetries = 3;
    private const int ConnectResponseRetryDelayMs = 100;

    // Login resilience — when the server rejects login with a transient
    // CharacterError (prior session for this account still being torn down
    // server-side after an abrupt kill+relaunch), re-run the FULL connect
    // handshake after a backoff. Bounded so a permanent failure can't
    // livelock. The lingering-session teardown observed on the co-hosted
    // ACE server clears within ~20-25s, so the cumulative backoff
    // (5s,10s,15s,... ≈ 75s over 5 retries) comfortably covers it.
    private const int MaxLoginReconnects = 5;
    private const int LoginReconnectBackoffBaseMs = 5000;

    // Slice 8 — max global-coord distance (meters) between two consecutive
    // self-observations that straddle a landblock boundary for the change
    // to be classified as an on-foot seam crossing rather than a teleport.
    // A walked seam step is a few meters physically; even with sparse
    // self-updates (~1/s at ~5 u/s walk speed) it stays well under this.
    // A landblock is 192 m and teleports move far more, so this cleanly
    // separates the two without risking false negatives on real crossings.
    private const float OnFootSeamMaxMeters = 48f;

    private readonly IPEndPoint _serverPort0;
    private readonly IPEndPoint _serverPort1;
    private readonly string _account;
    private readonly string _password;
    private readonly string _characterName;
    private readonly Strategy.IndoorNavService _indoorNav;
    /// <summary>
    /// Phase 3.1 — per-session fog-of-war: indoor cells the bot has
    /// directly perceived (its own cell, or any cell containing an
    /// observed object). The indoor pathfinder uses this set to
    /// restrict A* expansion so the bot never plans through cells
    /// it hasn't seen, matching the project rule "static GEOMETRY
    /// may be pre-loaded but dynamic content stays discovery-only".
    /// </summary>
    private readonly HashSet<uint> _seenIndoorCells = new();

    private Socket? _socket;

    public HandshakeDriver(IPAddress host, int port, string account, string password, string? characterName = null, Strategy.IndoorNavService? indoorNav = null)
    {
        _serverPort0 = new IPEndPoint(host, port);
        _serverPort1 = new IPEndPoint(host, port + 1);
        _account = account;
        _password = password;
        _characterName = string.IsNullOrWhiteSpace(characterName) ? "Headless01" : characterName;
        _indoorNav = indoorNav ?? new Strategy.IndoorNavService();
    }

    public async Task<HandshakeResult> RunAsync(CancellationToken ct)
    {
        var loginBuf = ArrayPool<byte>.Shared.Rent(RecvBufferSize);
        var recvBuf = ArrayPool<byte>.Shared.Rent(RecvBufferSize);
        try
        {
            // Login resilience loop: a transient CharacterError before
            // world-entry (lingering prior session) leaves the connection
            // dead, so recovery is a full reconnect with a fresh socket and
            // a fresh LoginRequest, after a backoff. Bounded by
            // MaxLoginReconnects so a permanent failure cannot livelock.
            for (int reconnect = 0; ; reconnect++)
            {
                // (Re)bind a fresh UDP socket for this attempt — mirrors a
                // real client relaunch and avoids reusing a source port the
                // server may still associate with the dead session.
                _socket?.Dispose();
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                var localPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
                Console.WriteLine($"[handshake] bound UDP socket on 0.0.0.0:{localPort}");

                ConnectRequestData connectReq;
                try
                {
                    var loginLen = BuildLoginRequest(loginBuf);
                    Console.WriteLine($"[handshake] sending LoginRequest ({loginLen} bytes) to {_serverPort0}");
                    await _socket.SendToAsync(new ArraySegment<byte>(loginBuf, 0, loginLen),
                                              SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);

                    connectReq = await ReceiveConnectRequestAsync(recvBuf, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    (ex is OperationCanceledException || ex is SocketException || ex is InvalidOperationException)
                    && !ct.IsCancellationRequested
                    && reconnect < MaxLoginReconnects)
                {
                    // A relaunch during the prior session's teardown can leave the
                    // server briefly unresponsive or rejecting at the connect stage:
                    // the LoginRequest is dropped (10s receive timeout -> Operation
                    // Canceled), a UDP packet is lost (SocketException), or the server
                    // replies with something other than a ConnectRequest because it is
                    // still tearing down the prior session (InvalidOperationException).
                    // Treat all of these as retryable attempt failures -- same bounded
                    // backoff as a transient CharacterError -- instead of letting them
                    // escape the reconnect loop. Past the cap (or on outer-ct
                    // cancellation) the exception propagates and the run ends, so a
                    // genuinely dead/misconfigured server still fails loud (and each
                    // attempt logs the cause).
                    var backoffMs = LoginReconnectBackoffBaseMs * (reconnect + 1);
                    Console.WriteLine($"[handshake] connect handshake failed ({ex.GetType().Name}: {ex.Message}) -> reconnect {reconnect + 1}/{MaxLoginReconnects} after {backoffMs}ms");
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                    continue;
                }
                Console.WriteLine($"[handshake] received ConnectRequest:");
                Console.WriteLine($"             serverTime  = {connectReq.ServerTime:R}");
                Console.WriteLine($"             cookie      = 0x{connectReq.Cookie:X16}");
                Console.WriteLine($"             clientId    = {connectReq.ClientId}");
                Console.WriteLine($"             serverSeed  = {Hex(connectReq.ServerSeed)}");
                Console.WriteLine($"             clientSeed  = {Hex(connectReq.ClientSeed)}");

                var respLen = BuildConnectResponse(loginBuf, connectReq.Cookie, connectReq.ClientId);
                Console.WriteLine($"[handshake] sending ConnectResponse ({respLen} bytes) to {_serverPort1}");
                Console.WriteLine($"[handshake]   bytes: {Hex(new ReadOnlySpan<byte>(loginBuf, 0, respLen))}");

                // ConnectResponse retransmit: the server's session-state transition
                // to AuthConnectResponse is gated on bcrypt password verification,
                // which races our ConnectResponse on a co-hosted server (bcrypt
                // ~20ms vs loopback RTT ~0.5ms). If our packet arrives before the
                // state transition, NetworkManager.cs lookup fails (state still
                // AuthLoginRequest) and the packet is silently dropped. Retransmit
                // a few times with a short delay to cover the bcrypt window.
                // See spec/04-handshake.md "Race condition" for details.
                for (int attempt = 0; attempt < ConnectResponseRetries; attempt++)
                {
                    await _socket.SendToAsync(new ArraySegment<byte>(loginBuf, 0, respLen),
                                              SocketFlags.None, _serverPort1, ct).ConfigureAwait(false);
                    if (attempt < ConnectResponseRetries - 1)
                        await Task.Delay(ConnectResponseRetryDelayMs, ct).ConfigureAwait(false);
                }
                Console.WriteLine($"[handshake]   sent {ConnectResponseRetries}× from local endpoint {_socket.LocalEndPoint}");

                // Phase 1 gate: receive whatever the server sends next on
                // port 0 (the handshake socket). Phase 2 wires CRC verification
                // using the ISAAC keystream seeded from ServerSeed -- every
                // EncryptedChecksum packet should now verify.
                //
                // Phase 3 adds the outbound encryption path. Per
                // SessionConnectionData.cs:60-61, the server seeds:
                //   CryptoClient = CryptoSystem(ClientSeed) -- for VERIFYING our packets
                //   IssacServer  = ISAAC(ServerSeed)         -- for ENCRYPTING server packets
                // So our client must mirror:
                //   cryptoRecv = CryptoSystem(ServerSeed)    -- to verify server packets
                //   cryptoSend = CryptoSystem(ClientSeed)    -- to encrypt our outbound
                var cryptoRecv = new CryptoSystem(connectReq.ServerSeed);
                var cryptoSend = new CryptoSystem(connectReq.ClientSeed);
                var observe = await ObservePostHandshakePackets(
                    recvBuf, ObserveSeconds, cryptoRecv, cryptoSend, connectReq.ClientId, ct).ConfigureAwait(false);

                if (observe.ReconnectRequested && reconnect < MaxLoginReconnects)
                {
                    var backoffMs = LoginReconnectBackoffBaseMs * (reconnect + 1);
                    Console.WriteLine($"[handshake] transient login rejection -> reconnect {reconnect + 1}/{MaxLoginReconnects} after {backoffMs}ms");
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                    continue;
                }
                if (observe.ReconnectRequested)
                    Console.WriteLine($"[handshake] transient login rejection persisted past {MaxLoginReconnects} reconnects - giving up");

                return new HandshakeResult(
                    connectReq.ServerTime,
                    connectReq.Cookie,
                    connectReq.ClientId,
                    connectReq.ServerSeed,
                    connectReq.ClientSeed,
                    observe.PacketCount > 0,
                    observe.CharacterList,
                    observe.ServerName,
                    observe.DDDInterrogation,
                    observe.CharacterCreateResponse,
                    observe.EnterWorldRequestSent,
                    observe.EnterWorldServerReady,
                    observe.EnterWorldSent,
                    observe.LastCharacterError,
                    observe.ChosenCharacterGuid);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(loginBuf);
            ArrayPool<byte>.Shared.Return(recvBuf);
        }
    }

    private int BuildLoginRequest(Span<byte> buffer)
    {
        // Lay out the body first, starting at offset 20.
        var bodyStart = PacketHeader.HeaderSize;
        var pos = bodyStart;

        // string16L ClientVersion ("1802")
        var verBytes = AcStrings.WriteString16L(buffer.Slice(pos), "1802");
        pos += verBytes;

        // Placeholder for the u32 "dataLen including ticket" field.
        var dataLenOffset = pos;
        pos += 4;
        var afterDataLenStart = pos;

        // u32 NetAuthType
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(pos), (uint)NetAuthType.AccountPassword);
        pos += 4;

        // u32 AuthFlags (0)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(pos), 0u);
        pos += 4;

        // u32 Timestamp (any value; server only logs)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(pos), (uint)Environment.TickCount);
        pos += 4;

        // string16L Account
        pos += AcStrings.WriteString16L(buffer.Slice(pos), _account);

        // string16L accountToLoginAs (empty)
        pos += AcStrings.WriteString16L(buffer.Slice(pos), string.Empty);

        // string32L Password
        pos += AcStrings.WriteString32L(buffer.Slice(pos), _password);

        // Fill in the dataLen field with the byte count of everything
        // after it (server reads but doesn't validate; populate to match
        // a real client).
        var dataLen = (uint)(pos - afterDataLenStart);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(dataLenOffset), dataLen);

        var bodyLen = pos - bodyStart;

        // Compute checksums. The HeaderOptional capture for LoginRequest
        // copies the entire body into the headerOptional checksum buffer,
        // so payloadChecksum = Hash32(body).
        var bodySpan = buffer.Slice(bodyStart, bodyLen);
        var payloadChecksum = Hash32.Calculate(bodySpan, bodyLen);

        var header = new PacketHeader
        {
            Sequence  = 0,
            Flags     = PacketHeaderFlags.LoginRequest,
            Checksum  = 0,
            Id        = 0,
            Time      = 0,
            Size      = (ushort)bodyLen,
            Iteration = 0,
        };
        var headerChecksum = header.CalculateHash32();
        header.Checksum = headerChecksum + payloadChecksum;
        header.Pack(buffer.Slice(0, PacketHeader.HeaderSize));

        return pos;
    }

    private int BuildConnectResponse(Span<byte> buffer, ulong cookie, uint clientId)
    {
        var bodyStart = PacketHeader.HeaderSize;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(bodyStart), cookie);
        var bodyLen = 8;

        // HeaderOptional ConnectResponse path captures these 8 bytes.
        var payloadChecksum = Hash32.Calculate(buffer.Slice(bodyStart, bodyLen), bodyLen);

        var header = new PacketHeader
        {
            Sequence  = 0,
            Flags     = PacketHeaderFlags.ConnectResponse,
            Checksum  = 0,
            Id        = (ushort)clientId,
            Time      = 0,
            Size      = (ushort)bodyLen,
            Iteration = 0,
        };
        var headerChecksum = header.CalculateHash32();
        header.Checksum = headerChecksum + payloadChecksum;
        header.Pack(buffer.Slice(0, PacketHeader.HeaderSize));

        return PacketHeader.HeaderSize + bodyLen;
    }

    private async Task<ObserveResult> ObservePostHandshakePackets(byte[] recvBuf, int seconds, CryptoSystem cryptoRecv, CryptoSystem cryptoSend, uint assignedClientId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var count = 0;
        var crcPass = 0;
        var crcFail = 0;
        uint lastReceivedSeq = 0;
        var sendBuf = new byte[1024]; // bare ack/timesync is tiny but encrypted blob fragments can fill a full packet
        var acksSent = 0;
        var timeSyncsSent = 0;
        CharacterListMessage? charList = null;
        ServerNameMessage? serverName = null;
        DDDInterrogationMessage? ddd = null;
        // Per ConnectRequest body, this is our index in NetworkManager.sessionMap.
        // Server's NetworkManager.ProcessPacket (line 147-156) uses it to find our
        // session. Setting Header.Id wrong = silent drop.
        ushort myClientId = (ushort)assignedClientId;

        // Phase 3 outbound counters. Server's NetworkSession.cs:57 inits
        // lastReceivedPacketSequence = 1; first valid C->S sequenced packet
        // must therefore be Sequence = 2 (lines 362-367 reject <= last).
        // Bare-control packets (AckSequence/TimeSync only) use Seq=0 and
        // bypass that check; they don't consume from this counter.
        uint nextOutboundPacketSequence = 2;
        // Fragment Sequence is per-MESSAGE (not per-fragment). Server-side
        // partialFragments dict is keyed by fragment.Header.Sequence
        // (NetworkSession.cs:42, 515-537), and lastReceivedFragmentSequence
        // advances by 1 per COMPLETE message (NetworkSession.cs:570-573).
        uint nextOutboundFragmentSequence = 1;
        // Server uses 0x80000000 as a constant Id marker (MessageFragment.cs:94).
        // Inbound code appears to ignore the field. Pick a non-high-bit constant
        // so the client/server origin remains distinguishable in captures.
        const uint OutboundFragmentId = 0x00000001;

        // Phase 3.2: send a CharacterCreate (opcode 0xF656) once
        // CharacterList confirms we're in AuthConnected state with zero
        // existing characters. Server replies with 0xF643
        // CharacterCreateResponse - decoded in GameMessageDecoder.
        var characterCreateSent = false;
        CharacterCreateResponseMessage? createResponse = null;
        // smoke-charlist-quirk: when CharacterList reports zero
        // characters but the account already owns the desired name, the
        // server rejects CharacterCreate with NameInUse and the old flow
        // wedged (no guid to enter the world with). On a name-retryable
        // rejection we pick a deterministic alternate name and re-issue
        // CharacterCreate. createName holds the name for the NEXT attempt;
        // createNameAttempt counts rename retries (capped).
        var createName = _characterName;
        var createNameAttempt = 0;
        const int MaxCreateNameAttempts = 5;
        // Sequence of the last packet whose CharacterCreateResponse we
        // acted on. A retransmitted packet reuses its original sequence,
        // so this dedups duplicates: without it a retransmit would
        // re-enter the retry branch and burn an extra rename attempt.
        var lastHandledCreateRespSeq = 0u;

        // Phase 3.3: two-step world-entry handshake.
        //   1. Send CharacterEnterWorldRequest (0xF7C8, payload-less)
        //      once we have a character guid (either just-created via
        //      Phase 3.2, or already-existing per CharacterList).
        //   2. Server replies with CharacterEnterWorldServerReady
        //      (0xF7DF, payload-less) OR CharacterError (0xF659,
        //      LogonServerFull) if shutting down.
        //   3. On ServerReady, send CharacterEnterWorld (0xF657)
        //      carrying the chosen guid + session account name.
        //   4. Server commits to WorldConnected state and starts the
        //      world-state firehose. (Decoding that firehose is
        //      Phase 4 territory.)
        var enterWorldRequestSent = false;
        var enterWorldSent = false;
        var loginCompleteSent = false;

        // Login resilience: if the server rejects this connection with a
        // TRANSIENT CharacterError BEFORE we commit to the world, the whole
        // login attempt is dead — recovery means re-running the full connect
        // handshake (the caller loops on this flag), not nudging an in-flight
        // sub-step. These fire when a prior session for the same account is
        // still being torn down server-side after an abrupt kill+relaunch.
        // Codes from the authoritative ACE enum
        // (Source/ACE.Server/Network/Enum/CharacterError.cs):
        //   0x01 Logon                       - two accounts logged on (pre-list)
        //   0x0D EnterGameCharacterInWorld   - character still in world
        //   0x10 EnterGameCharacterInWorldServer - character currently in world
        //   0x17 EnterGameCharacterLocked    - a save is still in progress
        // All resolve on their own once the prior session finishes its
        // server-side teardown (~20-25s observed locally). PERMANENT codes
        // (0x0B Generic, 0x0F NotOwned, 0x12 Corrupt, 0x14 CouldntPlace,
        // 0x15 ServerFull, 0x18 SubscriptionExpired, ...) are NOT reconnectable.
        const uint CharacterErrorLogon = 0x01;
        const uint CharacterErrorInWorld = 0x0D;
        const uint CharacterErrorInWorldServer = 0x10;
        const uint CharacterErrorCharacterLocked = 0x17;
        var reconnectRequested = false;
        var autonomousPositionSent = false;
        var moveToStateStartSent = false;
        var moveToStateStopSent = false;
        // Phase 6g — multi-action loop. After each motion+action cycle
        // completes, we reset all the per-action gates above so the
        // picker block can fire again and target the next object. We
        // track recently-visited guids so we don't immediately re-
        // target the same item, and we cap total actions so we don't
        // loop forever in a dense room.
        DateTime?            useSentAt = null;
        // Per-target post-action cooldown is owned by
        // Strategy/MotorPostActionCooldown.For(motionTarget). Most
        // targets return the 2-second default; Portal targets
        // return 6 seconds so the server-driven windup
        // (MoveToPosition pin → animation → Teleport packet) can
        // complete before the picker dispatches a competing AP/MS.
        // See files/portal03-spike.log:20224..20266 for the
        // smoking-gun trace of the 2-second-default regression.
        // Phase 7f.5 — bumped from 30 → 100. With the exploration
        // fallback the bot can keep finding new things to do well
        // past 30 actions (whole academy has ~50+ named objects:
        // 10 golems, 3-5 NPCs, ~20 signs, 5-6 doors, ~10 wearables).
        // The 30s damage-watchdog still protects against per-fight
        // runaways; this just lets the session-length budget breathe.
        const int            MaxActionsPerSession = 100;
        int                  actionsCompleted = 0;
        var                  visitedTargetGuids = new HashSet<uint>();
        // Diagnostic: throttle silent goal-kind fall-through logs to
        // once per (kind, source) pair per session. See dispatcher
        // fall-through branch later in this method.
        var                  loggedFallthroughKinds = new HashSet<string>();
        // Slice V (#86) — autonomous picker activity surface. The
        // picker code paths below select + dispatch targets without
        // pushing IntentStack frames (that's the architectural smell
        // ac-ai-players#86 will eventually fix). Until then, we
        // publish a single nullable PickerActivity record to the
        // LLM's prompt so the LLM can SEE what the picker is auto-
        // driving and decide whether to take strategic control by
        // emitting an explicit per-cycle goal. Set on candidate
        // selection, cleared when the picker picks nothing.
        PickerActivity? pickerActivityCurrent = null;
        // Phase 6l — pickup→equip handoff. When PutItemInContainer is
        // sent for a wearable item, we stash (itemGuid → equipLocation
        // bitmask) here. On InventoryPutObjInContainer arrival for the
        // same item guid, the next tick sends GetAndWieldItem in a
        // fresh packet. We cannot bundle equip in the same packet as
        // pickup — the server-side CreateMoveToChain captures rootOwner
        // at FindObject time (still on landscape), then races against
        // the pickup chain. By the time the equip MoveToChain callback
        // runs, the item is in inventory, rootOwner is null, and
        // line 1592 of Player_Inventory.cs fires ActionCancelled
        // (WeenieError 0x36). Verified empirically in
        // phase6l-equip-run-06-decoded.log on a fresh account.
        var                  pendingEquip = new Dictionary<uint, uint>();
        // Dequip-before-wield (weapon swap). The ACE server's
        // CheckWeaponCollision refuses to wield a weapon while another
        // weapon is equipped (silent InventoryServerSaveFailed err=None).
        // When an LLM Wield goal targets a weapon blocked by a currently-
        // wielded weapon, the motor first sends PutItemInContainer to move
        // the BLOCKER into the pack; this dict maps the blocker's guid ->
        // (targetWeaponGuid, targetEquipSlot) so the blocker's put-ack
        // (which confirms it is now unequipped) drives the follow-up
        // GetAndWieldItem of the intended weapon. Mirrors the PHASE6L
        // pickup->equip handoff, but decouples the put item (blocker) from
        // the wield item (target). The LLM still chose WHICH weapon; source
        // only inserts the mechanical dequip the server requires.
        var                  pendingWieldAfterDequip = new Dictionary<uint, (uint TargetGuid, uint TargetSlot, DateTime StartedUtc)>();
        // Phase 6n — anti-starvation: after an item is successfully
        // wielded, mark its weenie-class as "satisfied" so we stop
        // chasing duplicate quest-reward copies. Also count successful
        // pickups per name; after 1, deprioritize that name so we
        // don't pick a third Bruised Apple just because the academy
        // gifted us another one. Without this, the bot loops on
        // apples/armor reward respawns and never reaches NPCs or
        // doors.
        var                  satisfiedEquipSlots = new HashSet<uint>();
        var                  satisfiedWeenieClasses = new HashSet<uint>();
        var                  pickupCountByName = new Dictionary<string, int>();
        // Phase 6n — guid -> weenie class for items we put in pendingEquip,
        // so we can promote the class to satisfiedWeenieClasses when the
        // wield ack arrives.
        var                  pendingEquipWcid = new Dictionary<uint, uint>();
        // Phase 7f.4 — startup equip-from-inventory pass. Items the
        // server grants at character creation (Training Spadone via
        // Two-Handed-Combat skill, Handy Healing Kit via Healing, etc.)
        // arrive as ObjectCreate with ContainerGuid=self and
        // WielderGuid=null. The pickup-equip path (Phase 6m) doesn't
        // fire for these because there's no pickup - they're already
        // in the bag. This set tracks guids we've already issued
        // GetAndWieldItem for so a single tick re-scan doesn't spam
        // duplicate equip requests while we wait for the WieldObject
        // ack. The ack flips snap.WielderGuid != null which removes
        // the item from the inventory-equip candidate set on the
        // next tick.
        var                  inventoryEquipSent = new HashSet<uint>();
        // cp-2273 — distinguishes guids the SOURCE autonomously auto-equipped
        // (PHASE7F.4 below) from LLM-requested wields, so a server
        // InventoryServerSaveFailed for an autonomous auto-equip (e.g. a
        // level-gated starter cloak → 0x420 LevelTooLow) is NOT surfaced as a
        // plan-invalidating ActionRejected the LLM would mis-attribute to its
        // own current goal. inventoryEquipSent alone can't be used: it also
        // holds LLM Pickup→equip and LLM Wield guids.
        var                  autoEquipFailureFilter = new Strategy.AutoEquipFailureFilter();
        // Phase 7f — combat state. Locked when bot dispatches a melee
        // attack. While locked, the picker keeps targeting the same
        // creature (so we don't walk away mid-fight) and a retry timer
        // re-sends TargetedMeleeAttack every CombatRetryIntervalSec
        // until we observe the target dying (UpdateHealth==0) or
        // disappearing, or the wall-clock CombatTimeoutSec elapses.
        // Hostile-creature detection now lives in the Tactics layer:
        // the LLM (or schema fallback) emits Goal{Kind=Attack,...}
        // and the action-send branch dispatches melee. Source code
        // does NOT contain wcid literals (anti-hardcoding rule,
        // EPIC #67/#68).
        const double         CombatRetryIntervalSec = 5.0;
        // Anti-spam floor for cancel-driven fast re-sends (Phase 7f.2).
        // When the server reports the auto-repeat loop dropped
        // (AttackDone ActionCancelled) we re-send well before the 5s
        // safety net, but never faster than this.
        const double         CombatFastRetryMinIntervalSec = 0.35;
        // Phase 7f.2 — server-stick suppression. Against a MOBILE target
        // the server moves the bot into melee range by sticking the bot's
        // own object to the target (StickToObject). While that approach is
        // in flight the server-side Attacking flag is false, so a re-sent
        // TargetedMeleeAttack cancels the move-to and restarts it — a
        // perpetual self-cancellation loop that lands 0 damage. Track the
        // most recent server stick to the active combat target and suppress
        // the loop-keeper re-send (and the ActionCancelled fast-retry arm)
        // until the observation goes stale.
        const double         CombatStickSettleSec = 2.0;
        uint?                combatServerStickTarget = null;
        DateTime?            combatServerStickAt = null;
        // Phase 7f.2 — server auto-repeat quiescence gate. AC1 melee is a
        // server-side loop: one TargetedMeleeAttack starts it and the server
        // auto-repeats Attack() at weapon cadence. While that loop is mid-swing
        // the server is IsBusy, so a re-sent TargetedMeleeAttack is rejected
        // with WeenieError.YoureTooBusy (0x1D) and the server then fires
        // GameEventAttackDone(ActionCancelled), NULLING its own MeleeTarget —
        // killing the loop. Against a near-instant mob (Chicken) the fight ends
        // before any re-send; against a longer-lived mob the loop-keeper's
        // re-sends cancel the server loop every cadence → the fight stalls after
        // the first landed hit. Track the most recent SERVER signal that its
        // loop is ALIVE (a normal AttackDone(None) between-swings power-refill,
        // or a target health update) and suppress the loop-keeper until that
        // observation is older than CombatActivityQuiescenceSec — i.e. the loop
        // has genuinely dropped (target stepped out and back, LOS broke/restored)
        // and needs a real nudge. Must exceed the slowest weapon swing cadence
        // (a slow two-hander is ~3s) so a normal between-swings gap is not
        // mistaken for a dropped loop.
        const double         CombatActivityQuiescenceSec = 4.0;
        DateTime?            lastServerCombatActivityAt = null;
        const double         AbandonOnNoDamageSec   = 60.0;
        // Phase 7f — EARLY "cannot damage" abandon. The 60s no-damage
        // watchdog above is the ultimate backstop; this trips sooner once
        // the bot has swung enough times with EVERY swing evaded and zero
        // damage dealt that "0 landed" is conclusive (a fresh low-skill bot
        // routinely meets mobs it cannot hit and otherwise burns the full
        // 60s per such mob). Requires both a minimum all-evaded swing count
        // and a minimum elapsed time so a winnable fight's unlucky early
        // evade streak does not trip it. See CombatRetry.ShouldAbandonUnbeatable.
        const int            AbandonAllEvadedMinSwings = 12;
        const double         AbandonAllEvadedMinSec    = 25.0;
        // Phase 7f.D — reactive low-health disengage (self-preservation
        // reflex). Break off combat when our OWN health is at or below
        // EITHER a fraction of max OR an absolute HP floor (a low-level
        // char has so few max HP that a fraction alone rounds below one
        // hit). Refuse to re-engage until health recovers past the higher
        // re-engage fraction (hysteresis → no oscillation). On disengage,
        // flee this many units directly away from the threat and avoid
        // re-walking that specific threat for the cooldown window. These
        // are mechanical safety rails over the bot's OWN health only — no
        // game knowledge, no target choice (the LLM still owns WHAT to
        // fight; this only prevents dying mid-swing).
        const double         CombatDisengageHealthFraction = 0.35;
        const uint           CombatDisengageCriticalHpFloor = 2u;
        const double         CombatReengageHealthFraction = 0.70;
        const float          CombatFleeDistanceUnits = 15f;
        var                  combatAvoidCooldown = TimeSpan.FromSeconds(30);
        var                  combatAvoidUntil = new Dictionary<uint, DateTime>();
        // Set by the low-health attack-suppression dispatch guard so the
        // post-action reset cascade does NOT permanently add a suppressed
        // hostile to visitedTargetGuids (suppression is TEMPORARY — the
        // combatAvoidUntil cooldown + re-engage health hysteresis own
        // re-engagement, not a permanent visited blacklist).
        var                  suppressVisitedAddOnReset = false;
        uint?                combatTargetGuid = null;
        DateTime?            combatStartedAt = null;
        DateTime?            lastCombatAttackAt = null;
        // Set when an AttackDone(ActionCancelled) is observed for the
        // active combat target; consumed by the Phase 7f.2 loop-keeper to
        // re-send the melee attack early. Cleared on send + combat reset.
        var                  combatFastRetryRequested = false;
        DateTime?            lastDamageAt = null;
        float?               lastObservedTargetHealthFraction = null;
        // combat-damage-output: per-fight swing-outcome counters surfaced
        // to the LLM as raw perception (it never auto-disengages — the LLM
        // owns that). Counters belong to combatStatsForGuid; a notification
        // for a DIFFERENT locked target lazily resets them. combatTargetName
        // is the defender's display name pulled from the notifications (the
        // wire is the only place it appears at swing time).
        int                  combatSwingsLanded = 0;
        int                  combatSwingsEvaded = 0;
        uint                 combatDamageDealt = 0;
        bool                 combatFeedbackSent = false;
        string?              combatTargetName = null;
        uint?                combatStatsForGuid = null;
        // self-progress wake dedup (cp-2280, generalized to a value-edge):
        // last observed unspent-XP value (null = never observed). A
        // SelfProgressChanged event is emitted on the first known value and
        // again whenever the decoded value differs from this one — a
        // consecutive value-edge, no magnitude judgment. Reset naturally per
        // login (fresh handler scope). See MaybeEmitSelfProgress.
        long?                lastObservedUnspentXp = null;
        // observed-hostile perception: NORMALIZED attacker name -> last UTC
        // the server reported that creature attacking the bot (decoded from
        // DefenderNotification 0x01B2 / EvasionDefenderNotification 0x01B4).
        // Pruned by TTL and published to worldState.RecentHostileNames before
        // each projection build. Keyed by name because the wire defender
        // notification carries no attacker guid. Cleared on landblock change.
        var                  recentHostileAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // How long a creature stays flagged ObservedHostile after its last
        // observed swing/evasion against the bot. AC melee cadence is a few
        // seconds; 12s survives normal swing gaps within a fight and clears
        // shortly after the attacker dies/disengages. Refreshed on every
        // defender notification (re-stamps the name's UTC).
        const double         ObservedHostileTtlSeconds = 12.0;
        // cold-start egress: stable kind-keys (CombatFeelLedger.KeyOf) of
        // monster kinds the bot has KILLED since entering the current
        // landblock. Cleared on landblock change; published to
        // worldState.KilledKindsThisDwell before each projection build so the
        // hunt-egress override can see which visible kinds the bot has already
        // farmed HERE (proven non-leveling once the bot is tapped out). Bot's
        // own outcome bookkeeping — no danger/value label.
        var                  killedKindsThisLandblock = new HashSet<string>(StringComparer.Ordinal);
        // combat-missile-attack: the attack opcode family used for the
        // most recent ATTACK dispatch, for the cmd= log line only.
        AttackMode           combatAttackMode = AttackMode.Melee;
        var ownPlayerSeen = false;
        // One-time guard: enable the AutoRepeatAttacks character option
        // right after the first LoginComplete so the server runs its
        // native continuous melee swing loop (see the SetSingleCharacterOption
        // send below and Player_Melee.cs:375).
        var autoRepeatOptionSent = false;
        // Packet index at which LoginComplete was sent; we gate the
        // first AutonomousPosition probe on "saw at least this many
        // more inbound packets after LC" so the server has time to
        // process LoginComplete and clear the Teleporting flag.
        // Without this gate, the AP handler short-circuits on
        // (!Teleporting) and we observe nothing.
        int loginCompletePacketIndex = -1;
        int autonomousPositionPacketIndex = -1;
        const int PostLoginCompleteGracePackets = 30;
        const int PostAutonomousPositionGracePackets = 30;
        // cut-movement-ap-deadwait: the packet-count grace above stalls
        // 22-32s before motion starts on a quiet pre-walk link — while
        // the bot stands still the server only emits ~1 idle packet/sec,
        // so reaching 30 inbound packets takes tens of seconds. The grace
        // only needs to let the server APPLY the AutonomousPosition before
        // motion layers on top, which is a sub-second wall-clock concern,
        // not a packet-volume one. Cap the wait with a wall-clock ceiling
        // so MoveToState START fires promptly regardless of link chatter.
        const int PostAutonomousPositionGraceMaxMs = 750;
        DateTime? autonomousPositionGraceStartUtc = null;

        // Phase 7g — mid-session teleport recovery. When the bot
        // teleports cross-landblock (e.g. Academy Calling Stone →
        // Holtburg), the server again sets Teleporting=true on us
        // and the AP handler short-circuits, exactly as on initial
        // spawn. The cure (per memory + ACE.Server/Network/GameAction/
        // Actions/GameActionLoginComplete.cs) is to re-send
        // GameActionLoginComplete: its handler calls
        // OnTeleportComplete() which clears the flag. We detect a
        // mid-session teleport by watching for a change in the high-
        // 16 bits of worldState.Self.CellId (the landblock id).
        // `loginCompleteResendNeeded` flags the next LoginComplete
        // block iteration to fire a fresh LC even though we already
        // sent one at spawn.
        uint? lastObservedSelfLandblock = null;
        // Slice 8 — track the last observed self cell + position so a
        // landblock change can be classified as an on-foot seam crossing
        // (small global-coord delta, both cells outdoor) vs a teleport.
        uint lastObservedSelfCellId = 0u;
        System.Numerics.Vector3 lastObservedSelfPos = default;
        bool loginCompleteResendNeeded = false;
        // Phase 7g (intra-landblock teleport) — the teleport sequence the
        // self object carried the last time we (re)sent LoginComplete. A
        // strictly-newer value means a teleport happened AFTER our last
        // LoginComplete, leaving the server-side Teleporting flag set (the
        // server then rejects every client position update until we re-send
        // LoginComplete). Null until the first LoginComplete is sent.
        // Paired with the instance sequence below: a new instance epoch
        // resets the per-epoch teleport counter, so the comparison MUST be
        // instance-aware to avoid wrap-aware false negatives.
        ushort? loginCompleteAckedTeleportSeq = null;
        ushort? loginCompleteAckedInstanceSeq = null;
        // Commit B — track the last NavGraph node id the bot stood on.
        // Updated on every RecordVisit so RecordObservation can anchor
        // entity sightings to a real node, and RecordEdge can join the
        // pre-teleport node to the post-teleport node when a landblock
        // change occurs.
        Guid lastVisitNodeId = Guid.Empty;

        // Strategy/Tactics layer. The LlmGoalPolicy compiles
        // observations (visible NPCs, inventory ShortDescs, popup
        // strings) into a Goal that says "GIVE Academy Exit Token
        // to Jonathan" without any wcid literals baked into source.
        // The NoQuestKnowledgePolicy is a schema-only fallback used
        // when the LLM call fails. Disable the LLM with
        // AC_BOTS_LLM_DISABLE=1 to fall back to schema-only behavior.
        var llmDisabled = string.Equals(
            Environment.GetEnvironmentVariable("AC_BOTS_LLM_DISABLE"),
            "1", StringComparison.Ordinal);
        var weenies = new WeenieRepository();
        // Slice D — capture every LLM decision + outcome to a JSONL
        // sidecar for offline analysis and future fine-tuning. Sink
        // is fire-and-forget; write failures never take the bot down.
        var trainingSink = new JsonlTrainingSink();
        // Commit B — capture spatial trajectory + observed landmarks
        // + landblock-to-landblock edges in the persistent NavGraph
        // (append-on-write JSONL at experiments/headless-client/data/
        // nav/<character>/, replacing the per-landblock NavGraphRecorder
        // JSON files). The graph is PER-BOT: each character keeps its own
        // navmesh populated by its own exploration, so one bot's routes
        // never leak into another's. The LLM/planner queries it for
        // routes the bot has personally walked.
        var navGraph = new NavGraph(profile: _characterName);
        // Slice R wiring — the strategic intent stack persists across
        // LLM deliberations. The LLM authors push/pop/replace ops in
        // its response; the bot's per-tick code (below) checks the
        // TOP for completion via predicate evaluation and pops it
        // automatically when satisfied. BotStatistics is the lifetime
        // monotonic counter feeding the stats-based predicates
        // (kill_count_total_at_least, levels_gained_total_at_least,
        // units_traveled_since_push_at_least, etc.).
        var intentStack = new IntentStack();
        var intentIds   = new IntentIdAllocator();
        var botStats    = new BotStatistics();

        // Slice 0 (Hunt) — operator-pushed initial intent. Read at
        // startup; pushed on the first tick where a real projection
        // is available so the IntentBaseline captures real data
        // (push-with-empty-projection would leave the baseline
        // counters at zero and corrupt since-push predicates).
        //
        // Supported values (case-insensitive): "Hunt". Anything
        // else logs a warning and is ignored. Env var, not CLI flag,
        // to match the AC_BOTS_* convention (AC_BOTS_LLM_DISABLE,
        // AC_BOTS_API_TOKEN, AC_BOTS_LLM_ENDPOINT, AC_BOTS_LLM_MODEL).
        var initialIntentRaw = Environment.GetEnvironmentVariable("AC_BOTS_INITIAL_INTENT");
        var pendingInitialIntentKind = string.IsNullOrWhiteSpace(initialIntentRaw)
            ? null
            : initialIntentRaw.Trim();
        bool initialIntentPushed = false;
        if (pendingInitialIntentKind is not null &&
            !string.Equals(pendingInitialIntentKind, "Hunt", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[intent-stack] warning: AC_BOTS_INITIAL_INTENT='{initialIntentRaw}' is not a recognised intent kind (supported: Hunt) — ignored");
            pendingInitialIntentKind = null;
        }
        else if (pendingInitialIntentKind is not null)
        {
            Console.WriteLine($"[intent-stack] AC_BOTS_INITIAL_INTENT={pendingInitialIntentKind} queued for first-tick push (operator-authorised)");
        }

        IGoalPolicy goalPolicy;
        // Slice V (#86): if the active policy is LlmGoalPolicy, hold
        // an extra typed reference so the picker can publish its
        // autonomous activity into the LLM prompt. NoQuestKnowledgePolicy
        // is unaffected (the schema-only fallback doesn't render an
        // LLM prompt).
        LlmGoalPolicy? llmPolicyForPickerSurface = null;
        if (llmDisabled)
        {
            goalPolicy = new NoQuestKnowledgePolicy(intentStack);
            Console.WriteLine("[strategy] AC_BOTS_LLM_DISABLE=1 -> LLM disabled, using NoQuestKnowledgePolicy fallback only");
        }
        else
        {
            try
            {
                var llmClient = new LlmGoalClient();
                var llmPolicy = new LlmGoalPolicy(llmClient, new NoQuestKnowledgePolicy(intentStack), weenies, trainingSink, intentStack, intentIds);
                goalPolicy = llmPolicy;
                llmPolicyForPickerSurface = llmPolicy;
                Console.WriteLine($"[strategy] LlmGoalPolicy ready (model={llmClient.Model} endpoint={llmClient.Endpoint}) intent-stack=enabled max-depth={intentStack.MaxDepth}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[strategy] LlmGoalClient unavailable ({ex.GetType().Name}: {ex.Message}); using NoQuestKnowledgePolicy fallback only");
                goalPolicy = new NoQuestKnowledgePolicy(intentStack);
            }
        }
        var eventStream = new EventStream();
        var tactics = new TacticsExecutor(goalPolicy, weenies, trainingSink);
        // Track which wcids we have already preloaded so we don't
        // hit the DB more than once per class. Concurrent fetches
        // are coalesced inside WeenieRepository.
        var preloadedWeenieWcids = new HashSet<uint>();
        // Track inventory adds we have surfaced to the EventStream so
        // we don't re-emit InventoryItemAdded every tick. The
        // InventoryPutObjInContainer ack is the canonical signal.
        var observedInventoryAdds = new HashSet<uint>();
        // Set by the LLM-driven pre-emptor when CurrentGoal.Kind ==
        // Give. Consumed by the action-send block (replaces USE with
        // GiveObjectRequest). Cleared by the cooldown-reset block.
        uint? pendingGiveItemGuid = null;
        // Set by the LLM-driven pre-emptor when CurrentGoal.Kind == Use
        // AND the goal carries an inventory Item (e.g. a key). Consumed
        // by the action-send block (sends UseWithTarget instead of plain
        // Use). Cleared together with pendingGiveItemGuid by the
        // cooldown-reset block.
        uint? pendingUseWithItemGuid = null;
        // M1.6 — snapshot of the Goal.Kind at the moment the
        // pre-emptor locked motion. Used by the action-send block
        // (combat / use / give branch selection) so we don't read a
        // stale tactics.CurrentGoal that may have advanced or
        // cleared during the move. Cleared by the cooldown-reset
        // block alongside motionTarget / useSent / etc.
        GoalKind? lockedGoalKind = null;

        // M1.6+ — schema-vs-LLM race fix. When LlmGoalPolicy has a
        // call in flight, the schema-only picker at the bottom of
        // the AP block must defer so the LLM result isn't bypassed
        // by a hasty nearest-named lock. Safety timeout
        // (MaxLlmDeferralSec) caps the wait so a stuck network
        // call can't park the bot forever — past the cap, the
        // schema-only picker fires as a fallback.
        DateTime?            llmInflightSince = null;
        const int            MaxLlmDeferralSec = 4;

        // Phase 6 — first goal-directed motion. Locked at the AP send
        // boundary if a named-snapshot is in range. The chosen target
        // rotation is REPLICATED (not duplicated) on both the AP and
        // the MoveToState START.
        //
        // Phase 6b — the actual translation. Server-side, Player.OnMoveToState
        // short-circuits on !FastTick (true for NPK headless char), so
        // the MoveToState broadcast alone never advances our position.
        // BUT Player.UpdatePlayerPosition(RequestedLocation) runs each
        // tick regardless of FastTick, and GameActionAutonomousPosition.Handle
        // sets RequestedLocation. So we step the bot toward the target
        // by sending periodic AP packets ourselves. Real clients send AP
        // every ~1s while moving; we use a faster 4 Hz cadence so the
        // bot can cover ~5-10u within our 60s observation window.
        //
        // Phase 6c — generalize target picker. Default behavior: pick
        // the nearest named non-self snapshot within search radius.
        // Override: set env var HEADLESS_MOTION_TARGET_NAME (exact
        // match, case-sensitive) to force a specific target name.
        // Useful for repeatable testing against a known landmark.
        WorldObjectSnapshot? motionTarget = null;
        // Slice 4 (FOV consumption): when motionTarget is a REMEMBERED
        // sighted location (a recalled coordinate, not a live object),
        // motionRememberedDest holds that same synthetic snapshot and
        // motionRememberedSightingId the source SightedLocation.Id. The
        // walk-tick steers toward the fixed remembered coords instead of
        // re-finding a live guid in the current worldState.
        WorldObjectSnapshot? motionRememberedDest = null;
        Guid?                motionRememberedSightingId = null;
        // Autonomous frontier probe (road-to-endgame Phase A1): when the
        // current motion lock is a discovery step toward an unexplored
        // cell (NOT a goal action), this is true so the arrival reset
        // cascade preserves the original LLM goal instead of marking it
        // completed — the goal's named target becomes perceivable once the
        // probed room loads, so the selector must re-resolve it next tick.
        bool                 motionIsFrontierProbe = false;
        // Deliberation-race guard (road-to-endgame Phase A1): the Goal.Id
        // this motion lock was created to EXECUTE, or null for locks that
        // own no tactics goal (auto-loot, picker no-op arrival, frontier
        // probe). On arrival the reset cascade only signals GoalCompleted
        // when the still-current goal IS this one — so a freshly-landed
        // LLM goal that arrived mid-motion is never clobbered by an
        // unrelated (older / fallback) lock's completion.
        Guid?                motionLockedGoalId = null;
        // Tempo instrumentation (motor-dialog-cycle-tempo): lock-time stamp
        // so the action-cycle completion log can split per-interaction
        // latency into lock->stop (walk + AP move round-trips, paired with
        // the existing motionStoppedAt), stop->dispatch (post-stop interact
        // wait, paired with useSentAt), and dispatch->complete (post-action
        // cooldown). Pure timing bookkeeping — no game knowledge, no
        // behavior change.
        DateTime?            motionLockStartedUtc = null;
        Quaternion?          motionRotation = null;
        float?               motionInitialDistance = null;
        DateTime?            motionStartedAt = null;
        DateTime?            motionStoppedAt = null;
        // XP-spend (RaiseAttribute) pending-action bookkeeping. A self-action
        // RaiseAttribute/RaiseVital complete their goal on dispatch (like the
        // self-arm Wield), so the LLM is consulted again next deliberation;
        // this dedup window stops a re-spend before the server-confirmed
        // unspent-XP decrease arrives. ONE raise of ANY kind is allowed in
        // flight at a time (a vital raise and an attribute raise both draw
        // from the same AvailableExperience pool, so a single shared slot is
        // what the next confirmed unspent-XP drop reconciles). Records the
        // raise kind ("attribute"/"vital"), the wire id, the clamped amount,
        // the dispatch time, and the AvailableExperience observed
        // pre-dispatch. Reconciled at the next raise attempt (of either kind):
        // cleared on a confirmed AvailableExperience decrease (success) or
        // after the timeout (no confirmation seen).
        (string Kind, uint Id, uint Amount, DateTime At, long? PreAvailableXp)? pendingRaise = null;
        DateTime             nextWalkTickAt = DateTime.UtcNow;
        Vector3?             lastSentWaypointPos = null;
        // The last walk-tick waypoint's GLOBAL (frame-free) XY, captured
        // atomically with lastSentWaypointPos. STOP derives the canonical
        // outdoor cell from these so it never has to guess which landblock
        // frame the local pos was generated in (motionLockedCellId can slide
        // between the final walk-tick and STOP, and a wrong frame mis-projects
        // the stop ~192 m and derives a distant cell — caught in review).
        (float X, float Y)?  lastSentWaypointGlobalXY = null;
        int                  walkTickAps = 0;
        // Slice S — blocked-motion detection state. Tracked across
        // walk ticks within a single motion lock; reset whenever the
        // lock is released (see ResetMotion block).
        Vector3?             prevSelfBeforeAp = null;
        uint?                prevSelfCellBeforeAp = null;
        float                prevExpectedStepLen = 0f;
        int                  consecutiveBlockedTicks = 0;
        uint                 motionLockedCellId = 0;
        bool                 motionDone = false;
        bool                 useSent = false;
        // visible-recent-interaction: set ONLY when a spatial Use/Pickup
        // opcode is actually dispatched against motionTarget (not merely
        // when useSent is raised by an arrival/deliberation-race-guard
        // skip). Gates the WorldObjectInteracted echo so it never reports
        // a "no opcode sent" arrival as an interaction. Reset in the same
        // cascade as useSent.
        bool                 worldInteractDispatched = false;
        // Phase 3.1 — indoor-nav path-following state. Once per motion
        // lock we attempt to plan a collision-aware path through the
        // static indoor mesh; if that succeeds, the walk-tick steps
        // through Waypoints rather than aiming straight at the target.
        // motionIndoorPathAttempted is the "tried once" gate (we don't
        // re-plan every tick — Phase 3.2 may add replan triggers).
        IReadOnlyList<IndoorWaypoint>? motionIndoorPath = null;
        int                     motionIndoorPathIndex = 0;
        IReadOnlySet<uint>?     motionIndoorPathCells = null;
        bool                    motionIndoorPathAttempted = false;
        // Outdoor reactive local avoidance — when a straight-line outdoor
        // frontier/remembered-coord walk clips an obstacle (sustained
        // Blocked), try a bounded sequence of short lateral detour
        // waypoints before giving up, so Strategy re-deliberates a new
        // DIRECTION rather than another equally-blocked cell from the same
        // stuck pocket. Counts the detours tried in the current stuck
        // episode within one motion lock; reset on lock release. Pure
        // mechanical locomotion (collision response); no game knowledge.
        int                     outdoorAvoidanceAttempt = 0;
        // True ONLY while the current motion lock is an anonymous OUTDOOR
        // frontier probe (Explore with no resolvable target — arrival has
        // no verb and no target-specific completion, it just re-perceives).
        // Local avoidance may ONLY redirect motionRememberedDest when this
        // is set, so it never hijacks a remembered-coord motion that DOES
        // carry interaction semantics (e.g. a recalled sighted location the
        // LLM means to Use/Talk/Attack on arrival).
        bool                    motionIsOutdoorFrontierProbe = false;
        // Outdoor seam-cell AP override bookkeeping. Every cell this
        // motion has claimed via OutdoorSeamCell (a cross-landblock AP
        // cell derived from the bot's own stepped global coords) is
        // recorded here so the cell-reconciliation below recognises the
        // server's subsequent walkCell report as an EXPECTED on-foot seam
        // crossing and SLIDES the motion lock forward instead of stopping
        // the motion (outdoor approach motions have no rasterized
        // motionIndoorPathCells to vouch for the crossing).
        var                     motionOutdoorApCells = new HashSet<uint>();
        // Door-USE dispatch tracking: per-door cooldown so we don't
        // spam USE on the same door every walk-tick while waiting for
        // it to open. Keyed by door object guid; value is the wall-
        // clock tick we last dispatched USE.
        var doorUseDispatchedAt = new Dictionary<uint, DateTime>();
        // Slice 4 — per-sighting revisit cooldown so a stale remembered
        // location (entity moved/despawned by the time we arrive) is not
        // re-selected in a tight loop. Keyed by SightedLocation.Id.
        var rememberedSightedCooldownUntil = new Dictionary<Guid, DateTime>();
        var rememberedSightedRevisitCooldown = TimeSpan.FromSeconds(45);
        // Slice 5 — per-advance-node throttle for cross-landblock route
        // guidance: when the bot keeps re-selecting the same explored
        // waypoint without moving (stuck), this short cooldown forces a
        // wander/re-perception instead of re-locking the identical node.
        // Keyed by NavNode.Id of the advance waypoint.
        var crossLbAdvanceCooldownUntil = new Dictionary<Guid, DateTime>();
        var crossLbAdvanceCooldown = TimeSpan.FromSeconds(20);
        // Autonomous indoor frontier exploration (road-to-endgame
        // Phase A1) — per-cell revisit cooldown so a frontier cell the
        // bot targeted but couldn't reach (or reached without resolving
        // the goal) isn't re-selected every tick. Keyed by target cell
        // id. Coverage is otherwise bounded naturally: once entered, a
        // cell joins _seenIndoorCells and stops being a frontier.
        var frontierCellCooldownUntil = new Dictionary<uint, DateTime>();
        // Optional override: pick a specific named target instead of
        // nearest-named heuristic. Empty/null => no override.
        string?              motionTargetNameOverride =
            Environment.GetEnvironmentVariable("HEADLESS_MOTION_TARGET_NAME");
        // Phase 6f: stop radius lowered from 2.0u -> 1.0u so the bot
        // ends up inside ACE's default UseRadius (0.6u) plus the
        // cylinder-distance fudge. Server-side
        // <c>WorldObject.IsWithinUseRadiusOf</c> uses cylinder dist
        // (XY-only with bounding-cylinder radii); ending at 1.0u of
        // center keeps us comfortably inside the pickup threshold
        // without overshooting through the item collision.
        //
        // 2026-05-30: the terminal stop radius is now target-aware
        // (Portals need 4u because their stab obstacle blocks the
        // default 1u envelope). The walk-tick calls
        // <c>MotorStopRadius.For(motionTarget)</c> per tick; the
        // <c>DefaultUnits=1.0f</c> in that helper preserves the
        // original behaviour for items/NPCs/signs. See
        // <c>Strategy/MotorStopRadius.cs</c> for the audit framing.
        // Phase 7f.5 — bumped from 30 → 60. Academy rooms are
        // sometimes 40-50u across; a 30u radius left the bot blind
        // to half a room from a corner position. With 60u we pull in
        // every named object in any sparring/training room from any
        // position inside the room. Combined with the exploration
        // fallback (post-picker), this prevents the "idle at corner"
        // dead state observed in phase7f4-headless20-fullrun.log.
        const float          MotionSearchRadius = 60f;
        // Phase 6f: discriminate pickup-eligible items from interact-
        // only objects via the ItemType bitmask
        // (ACE.Entity.Enum.ItemType). Pickup mask covers MeleeWeapon
        // (0x1) | Armor (0x2) | Clothing (0x4) | Jewelry (0x8) | Food
        // (0x20) | Money (0x40) | MissileWeapon (0x100) | Gem (0x800)
        // | SpellComponents (0x1000) | Key (0x4000) | Caster (0x8000).
        // Misc (0x80) is deliberately EXCLUDED — doors carry Misc but
        // are not pickup-able. Creatures, Portals, LifeStones, etc.
        // fall through to the Use action.
        const uint           PickupItemTypeMask = 0xD96F;
        const int            WalkTickIntervalMs = 250;
        // Phase 7e — switch from WalkForward to RunForward. Bot now
        // moves at ~5 u/s instead of 2.5 u/s. Server gates run-speed
        // on the Run motion + HoldKey.Run; AP-predicted self position
        // must advance at the same rate or motion-done detection drifts.
        const float          WalkSpeedUnitsPerSec = 5.0f;
        const int            MotionWallClockTimeoutSec = 30;
        // cp-2272 (motor tempo): a "no-lock" motion — the pre-emptor could
        // not resolve the goal target to a live snapshot, a sighting-memory
        // steer, or an exploration frontier, and the schema picker found no
        // in-range candidate, so the bot sends a stationary AutonomousPosition
        // and drifts blindly — has nothing to walk toward. It used to burn the
        // full 30s safety timeout before re-deliberating (the dominant
        // cold-start tempo waste). Time it out fast so the Strategy layer picks
        // a new goal ~5x sooner. Productive motions (locked target, remembered
        // sighting, or explored-route follow) keep the long timeout.
        const int            MotionNoLockTimeoutSec = 6;
        // Slice S — blocked-motion detection. Server-authoritative
        // walkSelf.Position is updated by UpdatePosition packets from
        // the server. After we send an AP(intent), if the server's
        // next reported position has barely advanced from where we
        // were before sending — well under the step we requested —
        // server physics is clamping us against something (wall,
        // closed door, mob, NPC, geometry). Stop after a short run
        // of blocked ticks and surface ActionRejected so strategy
        // re-deliberates instead of (a) continuing to send APs the
        // server keeps clamping and (b) eventually drifting through
        // the obstacle via accumulated AP creep. No hardcoded
        // geometry: the detector only consumes server-reported self
        // position, which is the same signal a real player's local
        // physics engine consumes.
        const float          BlockedMoveRatioThreshold = 0.25f;
        const int            BlockedConsecutiveTicks   = 3;
        const float          BlockedMinExpectedStep    = 0.30f;
        // Slice Q — corpse loot extraction. After the bot opens a
        // corpse via USE, the server emits ObjectCreate for each
        // contained item with ContainerGuid=corpse.Guid and NO world
        // Position. WithinRadius can't see them; we dispatch
        // PUTITEMINCONTAINER directly. Tracker is proximity-gated:
        // we store the player's XY position at open time and only
        // loot when the bot has not wandered >LootContainerProximityRadius
        // away. TTL bounds memory + handles the case where a corpse
        // decays server-side without us hearing about it.
        const int            LootContainerTtlSec = 90;
        const float          LootContainerProximityRadius = 5.0f;
        // PickupItemTypeMask (0xD96F) plus Misc (0x80) for trophy
        // items (claws, teeth, etc.) which standard pickup excludes
        // because Misc collides with Door — but inside a corpse the
        // door false-positive risk vanishes.
        const uint           LootItemTypeMask = 0xD9EF;
        CharacterEnterWorldServerReadyMessage? enterWorldServerReady = null;
        CharacterErrorMessage? lastCharacterError = null;
        uint chosenCharacterGuid = 0;
        var  recentlyOpenedContainers = new Dictionary<uint, (DateTime OpenedAt, Vector3 OpenedAtPos)>();

        // Multi-fragment messages (ObjectCreate for players with active
        // motion, LoginCompletion GameEvent, etc.) split across multiple
        // UDP packets. The reassembler buffers fragments by Sequence
        // until all Count slots are populated, then emits the assembled
        // payload. See Protocol/FragmentReassembler.cs for the wire-
        // protocol rationale.
        var reassembler = new FragmentReassembler();

        // Phase 5 world-state accumulator. Every decoded
        // ObjectCreate / UpdatePosition / Motion / SetState /
        // PrivateUpdatePropertyInt / PlayerCreate flows through
        // worldState.Apply so the bot can later answer "where am
        // I?" / "what's nearby?" / "what's my health?" without
        // re-parsing the firehose.
        var worldState = new WorldState();

        // combat-feel ledger: per-mob-kind kill/death/near-death memory
        // for this session, surfaced to the LLM as raw "## Combat history"
        // facts. `lastCombatFoe` snapshots the kind we last engaged WITH a
        // timestamp so a death can be attributed even though the disengage
        // reflex clears combatTargetGuid/Name before health reaches 0. A
        // death is only attributed if the foe is fresh (the bot died
        // shortly after fighting it); stale or unknown foes are skipped so
        // the ledger is never poisoned with a wrong identity.
        // `selfDeathAttributed` debounces the multi-tick HealthCurrent==0
        // window to a single recorded death per life.
        var                  combatFeel = new CombatFeelLedger();
        (uint? Wcid, string? Name, DateTime At)? lastCombatFoe = null;
        var                  selfDeathAttributed = false;
        void PublishCombatHistory() => worldState.CombatHistory = combatFeel.Snapshot();
        // combat-feel: attribute the bot's own death to the monster KIND it
        // was fighting, debounced to once per life. Only attributes when the
        // engagement is recent (died shortly after fighting it) and the foe
        // identity resolves — a stale or unidentifiable foe is skipped so the
        // ledger is never poisoned. Called from the self-health decoders.
        void MaybeRecordSelfDeath(uint healthCurrent)
        {
            if (healthCurrent > 0)
            {
                // Alive or respawned — re-arm attribution for the next life.
                selfDeathAttributed = false;
                return;
            }
            if (selfDeathAttributed) return;
            selfDeathAttributed = true;
            if (lastCombatFoe is { } foe &&
                (DateTime.UtcNow - foe.At) < TimeSpan.FromSeconds(12) &&
                CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(foe.Wcid, foe.Name)) is not null)
            {
                combatFeel.RecordDeath(new CombatFeelLedger.MobIdentity(foe.Wcid, foe.Name));
                Console.WriteLine(
                    $"[combat-feel] self DEATH attributed to '{foe.Name ?? "?"}' " +
                    $"wcid={(foe.Wcid?.ToString() ?? "?")}");
                PublishCombatHistory();
            }
            else
            {
                Console.WriteLine("[combat-feel] self DEATH not attributed (no fresh combat foe).");
            }
            lastCombatFoe = null;
        }

        // combat-damage-output: resets the per-fight swing-outcome counters
        // and clears the surfaced CurrentFight status. Called at every
        // combat-lock clear site so a stale fight line never lingers in the
        // prompt after a fight ends, and lazily when a notification arrives
        // for a newly-locked target.
        void ClearCombatFightStats()
        {
            combatSwingsLanded = 0;
            combatSwingsEvaded = 0;
            combatDamageDealt = 0;
            combatFeedbackSent = false;
            combatTargetName = null;
            combatStatsForGuid = null;
            worldState.CurrentFight = null;
        }

        // combat-missile-attack: pick the attack opcode family (melee vs
        // missile) from the weapon the bot currently has WIELDED. Pure
        // mechanical projection of the wire-derived ItemType — the LLM
        // decides whether/whom to attack; this only decides HOW to
        // dispatch the swing given the weapon in hand, mirroring the
        // server's own CombatMode/equipped-weapon precondition.
        AttackMode SelectCurrentAttackMode()
        {
            var selfGuid = chosenCharacterGuid;
            return CombatWeaponSelection.SelectAttackMode(
                worldState.Objects.Values
                    .Where(s => s.WielderGuid is uint wg && wg == selfGuid)
                    .Select(s => (s.ItemType, Wielded: true)));
        }

        Console.WriteLine($"[observe] listening for post-handshake packets for {seconds}s; will send acks + timesync echoes ...");
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (reconnectRequested) break;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            // Phase 6b — wake the loop either when a packet arrives OR
            // when the next walk-tick is due, whichever is sooner. If we
            // only blocked on packets, walking would stall between
            // arrivals on quiet links.
            var timeUntilWalkTick = nextWalkTickAt - DateTime.UtcNow;
            if (timeUntilWalkTick < TimeSpan.Zero) timeUntilWalkTick = TimeSpan.Zero;
            var waitMs = Math.Max(1, (int)Math.Min(remaining.TotalMilliseconds,
                                                    timeUntilWalkTick.TotalMilliseconds));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(waitMs));
            try
            {
                var ep = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                var result = await _socket!.ReceiveFromAsync(new ArraySegment<byte>(recvBuf), SocketFlags.None, ep, cts.Token).ConfigureAwait(false);
                var len = result.ReceivedBytes;
                count++;
                if (len < PacketHeader.HeaderSize)
                {
                    Console.WriteLine($"[observe] #{count} short packet: {len} bytes from {result.RemoteEndPoint}");
                    continue;
                }
                var pkt = new InboundPacket();
                if (!pkt.Unpack(recvBuf, len))
                {
                    Console.WriteLine($"[observe] #{count} from {result.RemoteEndPoint}: UNPACK FAILED ({len} bytes)");
                    continue;
                }
                var verified = pkt.VerifyCRC(cryptoRecv);
                if (verified) crcPass++; else crcFail++;
                var verdict = verified
                    ? "CRC_OK"
                    : (pkt.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum) ? "CRC_FAIL_enc" : "CRC_FAIL_plain");
                Console.WriteLine($"[observe] #{count} from {result.RemoteEndPoint}: {pkt.Header}  [{verdict}]");

                if (pkt.Header.HasFlag(PacketHeaderFlags.TimeSync))
                    Console.WriteLine($"[observe]   optional: TimeSync={pkt.Optional.TimeSync:R}");
                if (pkt.Header.HasFlag(PacketHeaderFlags.AckSequence))
                    Console.WriteLine($"[observe]   optional: AckSequence={pkt.Optional.AckSequence}");

                // CRC-fail gate before any state mutation. A corrupted /
                // spoofed packet whose CRC fails must NOT be allowed to
                // feed FragmentReassembler — `fh.Count` is attacker-
                // controlled until CRC has authenticated the bytes, and
                // any partial state we accept here would pollute valid
                // in-flight messages on the same Sequence (and could
                // trigger pathological allocations via huge Count).
                if (!verified)
                    continue;

                foreach (var (fh, data) in pkt.Fragments)
                {
                    Console.WriteLine($"[observe]   {fh} payload[{data.Length}]: {Hex(data.AsSpan(0, Math.Min(data.Length, 32)))}{(data.Length > 32 ? " ..." : "")}");

                    // Reassemble before decoding. Multi-fragment messages
                    // arrive across separate UDP packets, possibly out
                    // of order. Reassembler returns null while waiting
                    // for more fragments; non-null = full message ready.
                    var assembled = reassembler.Add(fh, data);
                    if (assembled is null)
                    {
                        Console.WriteLine($"[observe]   -> buffering fragment {fh.Index + 1}/{fh.Count} for Seq={fh.Sequence} (in-flight messages: {reassembler.InFlightCount})");
                        continue;
                    }
                    if (fh.Count > 1)
                        Console.WriteLine($"[observe]   -> reassembled {fh.Count}-fragment message Seq={fh.Sequence} totalBytes={assembled.Length}");

                    var decoded = GameMessageDecoder.Decode(assembled);
                    var opcode = GameMessageDecoder.PeekOpcode(assembled);

                    // Snapshot whether an ObjectDelete target was
                    // present in WorldState BEFORE Apply runs, so the
                    // observer log can distinguish "removed" from
                    // "noop on unknown guid".
                    var preDeletePresent = decoded is ObjectDeleteMessage odPre
                        && worldState.TryGet(odPre.Guid) is not null;

                    // Feed the world-state accumulator BEFORE the
                    // logging switch. Apply is a no-op for message
                    // types it doesn't recognize (CharacterList,
                    // ServerName, GameEvent envelopes, etc.) so it's
                    // safe to call unconditionally here.
                    var applied = worldState.Apply(decoded);

                    switch (decoded)
                    {
                        case CharacterListMessage cl:
                            charList = cl;
                            Console.WriteLine($"[observe]   -> CharacterList: account=\"{cl.Account}\" slots={cl.SlotCount} characters={cl.Characters.Count} turbineChat={cl.UseTurbineChat} tod={cl.HasThroneOfDestiny}");
                            for (var i = 0; i < cl.Characters.Count; i++)
                            {
                                var c = cl.Characters[i];
                                Console.WriteLine($"[observe]      [{i}] id=0x{c.Id:X8} name=\"{c.Name}\" deleteIn={c.SecondsToDelete}s");
                            }
                            break;
                        case ServerNameMessage sn:
                            serverName = sn;
                            Console.WriteLine($"[observe]   -> ServerName: name=\"{sn.ServerName}\" connections={sn.CurrentConnections}/{sn.MaxConnections}");
                            break;
                        case DDDInterrogationMessage di:
                            ddd = di;
                            Console.WriteLine($"[observe]   -> DDDInterrogation: region={di.ServersRegion} lang={di.NameRuleLanguage} product={di.ProductId} supportedLangs=[{string.Join(",", di.SupportedLanguages)}]");
                            break;
                        case CharacterCreateResponseMessage ccr:
                            // Dedup retransmits: a retransmitted packet
                            // reuses its original sequence, so a duplicate
                            // CharacterCreateResponse must not re-enter the
                            // retry branch and consume an extra attempt.
                            if (pkt.Header.Sequence != 0 && pkt.Header.Sequence <= lastHandledCreateRespSeq)
                            {
                                Console.WriteLine($"[observe]   -> CharacterCreateResponse (duplicate seq={pkt.Header.Sequence}); ignored");
                                break;
                            }
                            lastHandledCreateRespSeq = pkt.Header.Sequence;
                            createResponse = ccr;
                            if (ccr.Response == CharacterCreateResponse.Ok)
                                Console.WriteLine($"[observe]   -> CharacterCreateResponse: Ok guid=0x{ccr.CharacterGuid:X8} name=\"{ccr.Name}\"");
                            else
                                Console.WriteLine($"[observe]   -> CharacterCreateResponse: {ccr.Response} (code={(uint)ccr.Response})");

                            // smoke-charlist-quirk recovery: a name-specific
                            // rejection (NameInUse / NameBanned) with no
                            // reusable character in the list is fixable by
                            // retrying under a fresh deterministic name.
                            // Re-arm the Phase 3.2 create gate so it fires
                            // again with createName on the next iteration.
                            if (CharacterNameFallback.IsNameRetryable(ccr.Response)
                                && (charList is null || charList.Characters.Count == 0)
                                && createNameAttempt < MaxCreateNameAttempts)
                            {
                                createNameAttempt++;
                                createName = CharacterNameFallback.NextName(_characterName, createNameAttempt);
                                characterCreateSent = false;
                                createResponse = null;
                                Console.WriteLine($"[observe]   -> CharacterCreate name rejected ({ccr.Response}); retry {createNameAttempt}/{MaxCreateNameAttempts} as name=\"{createName}\"");
                            }
                            break;
                        case CharacterEnterWorldServerReadyMessage ready:
                            enterWorldServerReady = ready;
                            Console.WriteLine($"[observe]   -> CharacterEnterWorldServerReady (server ready, send 0xF657)");
                            break;
                        case CharacterErrorMessage cerr:
                            lastCharacterError = cerr;
                            Console.WriteLine($"[observe]   -> CharacterError: code=0x{cerr.ErrorCode:X4}");
                            // A transient enter-world rejection received before
                            // we commit to the world (lingering prior session)
                            // is recoverable by reconnecting. Signal the caller
                            // and stop processing this dead connection.
                            if (!loginCompleteSent
                                && (cerr.ErrorCode == CharacterErrorLogon
                                    || cerr.ErrorCode == CharacterErrorInWorld
                                    || cerr.ErrorCode == CharacterErrorInWorldServer
                                    || cerr.ErrorCode == CharacterErrorCharacterLocked))
                            {
                                reconnectRequested = true;
                                Console.WriteLine($"[observe]      transient login error 0x{cerr.ErrorCode:X4} before world-entry -> request reconnect");
                            }
                            break;
                        case PlayerCreateMessage pc:
                            Console.WriteLine($"[observe]   -> PlayerCreate: guid=0x{pc.Guid:X8}");
                            // PlayerCreate for our chosen guid is the
                            // canonical "server has bound session.Player"
                            // signal. After this, we may safely send
                            // GameActionLoginComplete (0x00A1) — which
                            // server-side calls OnTeleportComplete() and
                            // clears the Teleporting flag (the purple-
                            // portal-haze state). Without LoginComplete
                            // the character stays in-portal forever and
                            // cannot interact with the world.
                            if (pc.Guid == chosenCharacterGuid)
                                ownPlayerSeen = true;
                            break;
                        case ServerMessageMessage sm:
                            // Trim huge welcome banners for log readability.
                            var preview = sm.Text.Length > 80 ? sm.Text.Substring(0, 80) + "..." : sm.Text;
                            Console.WriteLine($"[observe]   -> ServerMessage(chatType=0x{sm.ChatMessageType:X}): \"{preview}\"");
                            eventStream.Append(new StreamEvent
                            {
                                Sequence = 0,
                                Utc = DateTimeOffset.UtcNow,
                                Kind = EventKind.ServerMessage,
                                Text = sm.Text,
                                ChatType = (int)sm.ChatMessageType,
                            });
                            break;
                        case ObjectCreateMessage oc:
                            var loc = oc.Physics.Position is { } pos
                                ? $" lb=0x{pos.LandblockId:X8} xyz=({pos.X:F1},{pos.Y:F1},{pos.Z:F1})"
                                : "";
                            Console.WriteLine(
                                $"[observe]   -> ObjectCreate: guid=0x{oc.Guid:X8} wcid={oc.Weenie.WeenieClassId} " +
                                $"itemType=0x{oc.Weenie.ItemType:X} name=\"{oc.Weenie.Name}\"" +
                                $" wFlags=0x{(uint)oc.Weenie.Flags:X8}/0x{(uint)oc.Weenie.Flags2:X8}" +
                                $" pFlags=0x{(uint)oc.Physics.DescriptionFlags:X6}{loc}");
                            // Preload the weenie ShortDesc/LongDesc
                            // strings in the background. The next LLM
                            // call needs ShortDesc to reason about
                            // "give this token to Jonathan" — without
                            // this, projections see ShortDesc=null and
                            // the LLM has nothing to compile from.
                            // EnsureLoadedAsync coalesces concurrent
                            // requests for the same wcid.
                            if (oc.Weenie.WeenieClassId is uint preloadWcid &&
                                preloadedWeenieWcids.Add(preloadWcid))
                            {
                                _ = weenies.EnsureLoadedAsync(preloadWcid);
                            }
                            // Commit B — record named objects as nav
                            // observations anchored to the bot's
                            // current node. Lets the LLM later answer
                            // "where was Jonathan?" from cached graph
                            // data instead of needing to re-observe.
                            // The wire-derived EntityKind (Mob / NPC /
                            // Unknown) is computed below from the weenie
                            // header bits so remembered creatures carry
                            // the monster/npc label the recall prompt uses.
                            //
                            // Landblock-match gate (rubber-duck finding):
                            // ObjectCreate packets for the destination
                            // landblock can arrive after a teleport but
                            // BEFORE the per-loop self-position block
                            // refreshes lastVisitNodeId. Without this
                            // gate, a Holtburg NPC could be anchored to
                            // an academy node, corrupting semantic
                            // routing. Skip the observation when the
                            // object's landblock doesn't match the bot's
                            // currently-observed landblock.
                            if (oc.Physics.Position is { } lmPos &&
                                !string.IsNullOrEmpty(oc.Weenie.Name) &&
                                lastVisitNodeId != Guid.Empty &&
                                lastObservedSelfLandblock is uint selfLb)
                            {
                                var sightedPos = new System.Numerics.Vector3(lmPos.X, lmPos.Y, lmPos.Z);
                                // Wire-derived coarse kind (Mob / NPC / Unknown)
                                // from the ObjectCreate weenie header — same
                                // composite the visible projection uses, so
                                // remembered creatures carry the monster/npc
                                // label the recall prompt section surfaces.
                                // Pure perception; assigns no priority.
                                var sightedKind = EntityClassifier.ClassifySighting(
                                    oc.Weenie.ItemType,
                                    (uint)oc.Weenie.DescriptionFlags,
                                    (uint)oc.Weenie.Flags);

                                // Decide which sighting memories to write
                                // based purely on landblock distance — see
                                // SightingRecordPolicy for the per-memory
                                // coordinate-frame rationale.
                                var sightingDecision = SightingRecordPolicy.Decide(
                                    lmPos.LandblockId, selfLb);

                                // RecordObservation stores the entity position
                                // RELATIVE to the anchor node (rel =
                                // entityPosition - node.Position) for semantic
                                // recall — valid only in the bot's same
                                // landblock-LOCAL frame, so it is gated to an
                                // exact same-landblock sighting.
                                if (sightingDecision.RecordObservation)
                                    navGraph.RecordObservation(
                                        lastVisitNodeId,
                                        oc.Weenie.WeenieClassId,
                                        oc.Weenie.Name!,
                                        sightedPos,
                                        sightedKind,
                                        DateTimeOffset.UtcNow);

                                // FOV discovery: remember WHERE the entity is
                                // (its own cell + absolute coords) as a sighted
                                // location, so the bot can later navigate toward
                                // it. This is location memory, not a walkable
                                // node — see NavGraph.RecordSightedLocation. It
                                // stores ABSOLUTE cell+pos (landblock-offset aware
                                // via AcCoords), so a same-OR-adjacent landblock
                                // sighting is stored correctly — and the cross-
                                // landblock Attack/Explore resolver (cp-2271)
                                // NEEDS adjacent sightings to route toward a
                                // monster seen one landblock away. A far/
                                // disconnected landblock (e.g. a stale post-
                                // teleport ObjectCreate) is not adjacent and is
                                // still dropped. The observer node is provenance
                                // only; for a non-same-landblock sighting it is in
                                // a different landblock frame, so no observer node
                                // is anchored.
                                if (sightingDecision.RecordSightedLocation)
                                    navGraph.RecordSightedLocation(
                                        lmPos.LandblockId,
                                        sightedPos,
                                        oc.Weenie.WeenieClassId,
                                        oc.Weenie.Name!,
                                        sightedKind,
                                        sightingDecision.AnchorObserverNode
                                            ? lastVisitNodeId
                                            : (Guid?)null,
                                        DateTimeOffset.UtcNow);
                            }
                            break;
                        case GameEventMessage ge:
                            var geDesc = ge.Payload is not null
                                ? ge.Payload.ToString()
                                : $"raw[{ge.PayloadBytes.Length}]";
                            Console.WriteLine(
                                $"[observe]   -> GameEvent: type={ge.EventType} (0x{(uint)ge.EventType:X4}) " +
                                $"recv=0x{ge.ReceiverGuid:X8} seq={ge.ServerEventSequence} " +
                                $"payload={geDesc}");
                            // PlayerDescription (0x0013) login bundle — seed the
                            // bot's initial Level + experience totals so XP is
                            // perceived from login (discrete 0x02CD/0x02CF updates
                            // only fire AFTER an in-game change). Guard on the
                            // GameEvent recipient guid matching self so a bundle
                            // addressed elsewhere can never seed our own state.
                            if (ge.Payload?.PlayerDescription is { } pdesc &&
                                worldState.SelfGuid is uint pdSelf &&
                                ge.ReceiverGuid == pdSelf)
                            {
                                bool seededLvl = pdesc.Level is int pdLvl &&
                                    worldState.SeedSelfPropertyInt(25u, pdLvl);
                                bool seededTot = pdesc.TotalExperience is long pdTot &&
                                    worldState.SeedSelfPropertyInt64(
                                        PrivateUpdatePropertyInt64Message.TotalExperienceId, pdTot);
                                bool seededAvl = pdesc.AvailableExperience is long pdAvl &&
                                    worldState.SeedSelfPropertyInt64(
                                        PrivateUpdatePropertyInt64Message.AvailableExperienceId, pdAvl);
                                bool seededAttrs = pdesc.Attributes is { Count: > 0 } pdAttrs &&
                                    worldState.SeedSelfAttributes(pdAttrs);
                                bool seededSkills = pdesc.Skills is { Count: > 0 } pdSkills &&
                                    worldState.SeedSelfSkills(pdSkills);
                                Console.WriteLine(
                                    $"[playerdesc] login bundle: level={pdesc.Level?.ToString() ?? "?"}" +
                                    $"{(seededLvl ? "" : "(skip)")} " +
                                    $"totalXp={pdesc.TotalExperience?.ToString() ?? "?"}" +
                                    $"{(seededTot ? "" : "(skip)")} " +
                                    $"unspentXp={pdesc.AvailableExperience?.ToString() ?? "?"}" +
                                    $"{(seededAvl ? "" : "(skip)")} " +
                                    $"attrs={pdesc.Attributes?.Count ?? 0}" +
                                    $"{(seededAttrs ? "" : "(skip)")} " +
                                    $"skills={pdesc.Skills?.Count ?? 0}" +
                                    $"{(seededSkills ? "" : "(skip)")}");
                                MaybeEmitSelfProgress(ref lastObservedUnspentXp, worldState, eventStream);
                            }
                            // Phase 6l — pickup-ack triggers the queued
                            // equip. Send GetAndWieldItem in a fresh
                            // packet now that the server reports the
                            // item is in our inventory; rootOwner==this
                            // takes the no-MoveToChain branch
                            // (Player_Inventory.cs:1646).
                            if (ge.Payload?.InventoryPutObjInContainer is { } putAck)
                            {
                                // Surface to the EventStream so the LLM
                                // policy re-deliberates when a new item
                                // joins inventory. The ObjectCreate
                                // path above ALSO fires for fresh
                                // items, but InventoryPutObjInContainer
                                // is the canonical "you now own this"
                                // server signal (catches give-acks and
                                // pickup-acks alike).
                                if (observedInventoryAdds.Add(putAck.ItemGuid))
                                {
                                    var ackSnap = worldState.TryGet(putAck.ItemGuid);
                                    eventStream.Append(new StreamEvent
                                    {
                                        Sequence = 0,
                                        Utc = DateTimeOffset.UtcNow,
                                        Kind = EventKind.InventoryItemAdded,
                                        ItemGuid = putAck.ItemGuid,
                                        Wcid = ackSnap?.WeenieClassId,
                                        Name = ackSnap?.Name,
                                        ItemType = ackSnap?.ItemType,
                                    });
                                }
                                // Eagerly stamp ContainerGuid on the
                                // snapshot. Put-ack arrives before the
                                // (re)broadcast ObjectCreate with the
                                // new container linkage, so without
                                // this the LLM projection's inventory
                                // filter (ContainerGuid==self) misses
                                // freshly-acquired items for one or
                                // more LLM-call windows. Also clear any
                                // wielded state: an item moved into a
                                // container is no longer equipped (a
                                // dequip), so the WielderGuid/slot must
                                // not linger — otherwise the swap logic
                                // (and the LLM projection) would still
                                // see a dequipped weapon as wielded until
                                // the ObjectCreate arrives. For world
                                // pickups/loot these were already null.
                                if (worldState.TryGet(putAck.ItemGuid) is { } putSnap)
                                {
                                    putSnap.ContainerGuid = putAck.ContainerGuid;
                                    putSnap.WielderGuid = null;
                                    putSnap.CurrentWieldedLocation = 0;
                                }
                                // Phase 6n — count this pickup so the
                                // picker doesn't keep chasing identical
                                // quest-reward respawns of the same name.
                                var pickedSnap = worldState.TryGet(putAck.ItemGuid);
                                if (pickedSnap is not null && !string.IsNullOrEmpty(pickedSnap.Name))
                                {
                                    pickupCountByName.TryGetValue(pickedSnap.Name!, out var pc);
                                    pickupCountByName[pickedSnap.Name!] = pc + 1;
                                }
                                // Phase 6n — for wearables, mark the
                                // weenie class satisfied as soon as the
                                // pickup acks (don't wait for WieldObject).
                                // If the server later rejects the wield
                                // (InventoryServerSaveFailed), we've
                                // still got a copy in the bag and
                                // grabbing a second duplicate won't help.
                                // Without this, the bot loops on cap
                                // respawns when the cap fails to wield.
                                if (pendingEquipWcid.TryGetValue(putAck.ItemGuid, out var equipWcidEarly))
                                {
                                    satisfiedWeenieClasses.Add(equipWcidEarly);
                                }

                                if (pendingEquip.TryGetValue(putAck.ItemGuid, out var equipSlot))
                                {
                                    pendingEquip.Remove(putAck.ItemGuid);
                                    var equipPktSeq = nextOutboundPacketSequence++;
                                    var equipFragSeq = nextOutboundFragmentSequence++;
                                    var equipBuf = new byte[GameActionGetAndWieldItemMessage.PackedSize];
                                    var equipLen = GameActionGetAndWieldItemMessage.Pack(
                                        equipBuf,
                                        itemGuid: putAck.ItemGuid,
                                        equipLocation: (int)equipSlot);
                                    var equipMsg = new OutboundPacket();
                                    if (lastReceivedSeq != 0)
                                        equipMsg.AddAckSequence(lastReceivedSeq);
                                    equipMsg.AddBlobFragment(
                                        fragSequence: equipFragSeq,
                                        fragId: OutboundFragmentId,
                                        queue: (ushort)GameMessageGroup.UIQueue,
                                        gameMessagePayload: equipBuf.AsSpan(0, equipLen));
                                    var equipSentLen = equipMsg.Pack(sendBuf, myClientId,
                                                                     sequence: equipPktSeq, iteration: 1,
                                                                     encrypt: true, cryptoSend: cryptoSend);
                                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, equipSentLen),
                                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                                    Console.WriteLine(
                                        $"[observe]   -> PHASE6L SEND EQUIP: GetAndWieldItem(item=0x{putAck.ItemGuid:X8} slot=0x{equipSlot:X}) " +
                                        $"pktSeq={equipPktSeq} fragSeq={equipFragSeq} totalBytes={equipSentLen}");
                                }

                                // Dequip-before-wield follow-up. The put-ack
                                // for a dequipped weapon-swap BLOCKER confirms
                                // it is now in the pack (unequipped), so the
                                // intended target weapon can finally be wielded
                                // without tripping CheckWeaponCollision. Send
                                // the deferred GetAndWieldItem now (decoupled
                                // from goal state, mirroring PHASE6L).
                                if (pendingWieldAfterDequip.TryGetValue(putAck.ItemGuid, out var swap))
                                {
                                    pendingWieldAfterDequip.Remove(putAck.ItemGuid);
                                    // Suppress the startup auto-equip pass from
                                    // racing this exact wield.
                                    inventoryEquipSent.Add(swap.TargetGuid);
                                    var swapPktSeq = nextOutboundPacketSequence++;
                                    var swapFragSeq = nextOutboundFragmentSequence++;
                                    var swapBuf = new byte[GameActionGetAndWieldItemMessage.PackedSize];
                                    var swapLen = GameActionGetAndWieldItemMessage.Pack(
                                        swapBuf,
                                        itemGuid: swap.TargetGuid,
                                        equipLocation: (int)swap.TargetSlot);
                                    var swapMsg = new OutboundPacket();
                                    if (lastReceivedSeq != 0)
                                        swapMsg.AddAckSequence(lastReceivedSeq);
                                    swapMsg.AddBlobFragment(
                                        fragSequence: swapFragSeq,
                                        fragId: OutboundFragmentId,
                                        queue: (ushort)GameMessageGroup.UIQueue,
                                        gameMessagePayload: swapBuf.AsSpan(0, swapLen));
                                    var swapSentLen = swapMsg.Pack(sendBuf, myClientId,
                                                                   sequence: swapPktSeq, iteration: 1,
                                                                   encrypt: true, cryptoSend: cryptoSend);
                                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, swapSentLen),
                                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                                    Console.WriteLine(
                                        $"[strategy] SWAP-WIELD after dequip: blocker=0x{putAck.ItemGuid:X8} unequipped -> " +
                                        $"GetAndWieldItem(item=0x{swap.TargetGuid:X8} slot=0x{swap.TargetSlot:X}) " +
                                        $"pktSeq={swapPktSeq} fragSeq={swapFragSeq} totalBytes={swapSentLen}");
                                }
                            }
                            // M1.6 — PopupString is the canonical
                            // "the world is telling the player
                            // something" message (NPC reply, quest
                            // accept popup, "you cannot use that").
                            // Always surface to the LLM event stream.
                            if (ge.Payload?.PopupString is { } popup)
                            {
                                eventStream.Append(new StreamEvent
                                {
                                    Sequence = 0,
                                    Utc = DateTimeOffset.UtcNow,
                                    Kind = EventKind.PopupString,
                                    Text = popup.Message,
                                });
                            }
                            // M1.6 — Tell is per-character chat,
                            // also frequently used by NPCs to deliver
                            // dialog. Surface to event stream as
                            // NpcDialog if from a non-self guid.
                            if (ge.Payload?.Tell is { } tell &&
                                tell.SenderId != chosenCharacterGuid)
                            {
                                var sourceSnap = worldState.TryGet(tell.SenderId);
                                eventStream.Append(new StreamEvent
                                {
                                    Sequence = 0,
                                    Utc = DateTimeOffset.UtcNow,
                                    Kind = EventKind.NpcDialog,
                                    Text = tell.Message,
                                    Name = sourceSnap?.Name ?? tell.SenderName,
                                });
                            }
                            // Slice M — quest book / scroll / parchment
                            // contents. The server returns this when the
                            // bot Uses a Book itemType. Surface the full
                            // page text (concatenated) so the LLM can
                            // read quest directions, coordinates, item
                            // lists the same way a player would. We
                            // capture every BookDataResponse — the
                            // LlmGoalPolicy section dedupes by BookId
                            // newest-first so repeats don't bloat the
                            // prompt.
                            if (ge.Payload?.BookDataResponse is { } book)
                            {
                                var sb = new System.Text.StringBuilder();
                                if (!string.IsNullOrEmpty(book.Inscription))
                                    sb.AppendLine($"[Inscription] {book.Inscription}");
                                for (int pi = 0; pi < book.Pages.Count; pi++)
                                {
                                    var pg = book.Pages[pi];
                                    if (!pg.TextIncluded || pg.PageText is null) continue;
                                    if (book.Pages.Count > 1)
                                        sb.Append($"[Page {pi + 1}/{book.Pages.Count}] ");
                                    sb.AppendLine(pg.PageText.Replace("\r", ""));
                                }
                                var bookText = sb.ToString().Trim();
                                if (bookText.Length > 0)
                                {
                                    // Try to recover a human-readable
                                    // book name from the WorldState (the
                                    // ObjectCreate that delivered the
                                    // book should have populated it).
                                    var bookSnap = worldState.TryGet(book.BookId);
                                    eventStream.Append(new StreamEvent
                                    {
                                        Sequence = 0,
                                        Utc = DateTimeOffset.UtcNow,
                                        Kind = EventKind.BookText,
                                        ItemGuid = book.BookId,
                                        Name = bookSnap?.Name ?? book.AuthorName ?? $"book-0x{book.BookId:X8}",
                                        Text = bookText,
                                    });
                                }
                            }
                            // Phase 6n — wield ack: mark the slot mask
                            // and the wcid as satisfied so the picker
                            // stops chasing duplicate copies of the
                            // same armor reward.
                            if (ge.Payload?.WieldObject is { } wieldAck)
                            {
                                // cp-2273 — a successful wield ack resolves any
                                // source-autonomous auto-equip attempt for this
                                // guid: no failure is coming, so drop the marker
                                // (otherwise it would linger and could swallow a
                                // later LLM-owned inventory failure on the same
                                // guid, e.g. dequipping this weapon as a swap
                                // blocker).
                                autoEquipFailureFilter.ClearAutonomous(wieldAck.ItemGuid);
                                if (wieldAck.NewLocation != 0)
                                    satisfiedEquipSlots.Add(wieldAck.NewLocation);
                                if (pendingEquipWcid.TryGetValue(wieldAck.ItemGuid, out var wcEq))
                                {
                                    satisfiedWeenieClasses.Add(wcEq);
                                    pendingEquipWcid.Remove(wieldAck.ItemGuid);
                                }
                                else
                                {
                                    var wieldedSnap = worldState.TryGet(wieldAck.ItemGuid);
                                    if (wieldedSnap is not null && wieldedSnap.WeenieClassId is uint wc2)
                                        satisfiedWeenieClasses.Add(wc2);
                                }
                                // Slice H follow-up: WieldObject is the only
                                // notification the server sends when an item
                                // transitions from inventory to equipped. The
                                // server does NOT re-broadcast an ObjectCreate
                                // for the item, so WorldObjectSnapshot's
                                // CurrentWieldedLocation / WielderGuid stay at
                                // their last-ObjectCreate values (typically
                                // null/null for an inventory item) forever.
                                // The LLM Combat readiness section reads
                                // WieldedAt to decide if the bot is armed -
                                // without this update, `weapon: NOT wielded`
                                // is reported even right after we wield the
                                // starter Spadone, blocking any Attack.
                                //
                                // IMPORTANT: do NOT null ContainerGuid here.
                                // The WorldStateProjection inventory filter
                                // is `ContainerGuid == selfGuid`. A wielded
                                // item is still carried by the character, so
                                // it must remain in the inventory projection
                                // with WieldedAt populated. Nulling
                                // ContainerGuid drops the item from inventory
                                // entirely and `weapon: wielded` never fires
                                // (run-02 regression).
                                var equippedSnap = worldState.TryGet(wieldAck.ItemGuid);
                                if (equippedSnap is not null)
                                {
                                    equippedSnap.CurrentWieldedLocation = wieldAck.NewLocation;
                                    if (worldState.SelfGuid is uint sg)
                                        equippedSnap.WielderGuid = sg;
                                }
                                Console.WriteLine(
                                    $"[motion] satisfaction updated: slots=[{string.Join(",", satisfiedEquipSlots.Select(s => $"0x{s:X}"))}] " +
                                    $"wcids=[{string.Join(",", satisfiedWeenieClasses.Select(c => c.ToString()))}]");
                            }
                            // Phase 7f — UpdateHealth tracking. If
                            // the server reports our combat target's
                            // health <= 0, the golem is dead (corpse
                            // will spawn). Clear combat lock and
                            // mark the wcid satisfied so the picker
                            // moves on (we'll loot the corpse in a
                            // later phase via a new picker branch).
                            if (ge.Payload?.UpdateHealth is { } updHealth)
                            {
                                if (combatTargetGuid is uint ctgHealth && updHealth.ObjectId == ctgHealth)
                                {
                                    // Track damage progress. If
                                    // health dropped vs last seen
                                    // value, bump lastDamageAt so
                                    // the no-progress timeout
                                    // doesn't fire while we're
                                    // actively dpsing.
                                    if (lastObservedTargetHealthFraction is float prevHF &&
                                        updHealth.HealthFraction < prevHF - 0.0001f)
                                    {
                                        lastDamageAt = DateTime.UtcNow;
                                        // Damage landed → the server loop is
                                        // alive and connecting. Refresh the
                                        // quiescence clock (see
                                        // CombatActivityQuiescenceSec).
                                        lastServerCombatActivityAt = DateTime.UtcNow;
                                    }
                                    lastObservedTargetHealthFraction = updHealth.HealthFraction;
                                    if (updHealth.HealthFraction <= 0.0001f)
                                    {
                                        Console.WriteLine(
                                            $"[combat] target 0x{ctgHealth:X8} DEAD (health={updHealth.HealthFraction:F3}); " +
                                            $"clearing combat lock.");
                                        visitedTargetGuids.Add(ctgHealth);
                                        // combat-feel: record a KILL against
                                        // the kind we were fighting. Prefer the
                                        // live snapshot's identity; fall back to
                                        // the foe we locked (name/wcid survive a
                                        // notification-driven name update).
                                        var killSnap = worldState.TryGet(ctgHealth);
                                        var killIdentity = new CombatFeelLedger.MobIdentity(
                                            killSnap?.WeenieClassId ?? lastCombatFoe?.Wcid,
                                            killSnap?.Name ?? combatTargetName ?? lastCombatFoe?.Name);
                                        combatFeel.RecordKill(killIdentity);
                                        // cold-start egress: remember this KIND was killed in
                                        // the current landblock so the egress override can treat
                                        // it as already-farmed-here (bot's own outcome; no label).
                                        if (CombatFeelLedger.KeyOf(killIdentity) is string killKindKey)
                                            killedKindsThisLandblock.Add(killKindKey);
                                        PublishCombatHistory();
                                        // The kill resolves the engagement —
                                        // a later self-death is NOT this foe.
                                        lastCombatFoe = null;
                                        // Phase 7f.5 — DO NOT add the wcid to
                                        // satisfiedWeenieClasses. Each Sparring
                                        // Golem is an independent target; killing
                                        // one does not mean the bot should skip
                                        // the other 9 in the room. visitedTargetGuids
                                        // (per-guid) is the right granularity.
                                        // (Phase 7f originally added the wcid
                                        // satisfaction to break a 1-golem-only
                                        // bare-handed-stuck loop; now that the
                                        // bot has a Spadone, that defense is
                                        // counter-productive.)
                                        combatTargetGuid = null;
                                        combatStartedAt = null;
                                        lastCombatAttackAt = null;
                                        lastDamageAt = null;
                                        lastObservedTargetHealthFraction = null;
                                        combatFastRetryRequested = false;
                                        ClearCombatFightStats();
                                    }
                                    else
                                    {
                                        Console.WriteLine(
                                            $"[combat] target 0x{ctgHealth:X8} health={updHealth.HealthFraction:F3} ({(int)(updHealth.HealthFraction*100)}%)");
                                    }
                                }
                            }
                            // Phase 7f — AttackDone visibility. A
                            // non-zero errCode means the server
                            // refused the swing. Common reasons:
                            // OutOfRange, NotMeleeWeapon (we have
                            // none — unarmed should still work),
                            // YouCanNotAttackThisCreature, SkillTooLow.
                            //
                            // Slice H — surface non-zero AttackDone as an
                            // ActionRejected event so the LLM can pivot
                            // (e.g. wrong target classification → switch
                            // verb / target). Without this the bot can
                            // sit in the 60s no-damage stall after a bad
                            // Attack goal with no learning signal.
                            if (ge.Payload?.AttackDone is { } atkDone)
                            {
                                if (atkDone.ErrorCode != 0)
                                {
                                    var attackLabel = WeenieErrorLabels.Label(atkDone.ErrorCode);
                                    Console.WriteLine(
                                        $"[combat] AttackDone error=0x{atkDone.ErrorCode:X4} " +
                                        $"({attackLabel})");

                                    // ActionCancelled (0x0036) means the
                                    // server's auto-repeat swing loop
                                    // dropped. Request a fast re-send of the
                                    // bare melee attack so the loop restarts
                                    // well before the 5s safety net — but
                                    // only while a combat target is active
                                    // AND the server is not currently
                                    // sticking us into range (a cancel that
                                    // is just the byproduct of the server's
                                    // in-progress move-to must NOT arm a
                                    // re-send, or it perpetuates the
                                    // self-cancel loop against a mobile
                                    // target). This is mechanical
                                    // loop-keeping, NOT target choice. Other
                                    // non-zero codes are semantic refusals
                                    // surfaced below for the LLM to pivot on,
                                    // not retried here.
                                    var ctgStickActive =
                                        combatServerStickTarget == combatTargetGuid &&
                                        combatServerStickAt is DateTime acSat &&
                                        (DateTime.UtcNow - acSat).TotalSeconds is double acAge &&
                                        acAge >= 0 &&
                                        acAge < CombatStickSettleSec;
                                    if (atkDone.ErrorCode == 0x0036u &&
                                        combatTargetGuid is not null &&
                                        !ctgStickActive)
                                        combatFastRetryRequested = true;

                                    // Surface the refusal as an LLM learning
                                    // signal — EXCEPT a trailing
                                    // ActionCancelled with no active combat
                                    // target, which is the benign post-kill /
                                    // post-disengage teardown of the server's
                                    // swing loop. Surfacing that would append a
                                    // misleading "Attack rejected" for a target
                                    // the bot just killed AND discard the
                                    // establishment LLM call fired right after
                                    // the kill (a non-transport ActionRejected
                                    // is plan-invalidating). Mechanical gate on
                                    // the wire code + combat-lock state.
                                    if (CombatRetry.ShouldSurfaceAttackDoneRejection(
                                            atkDone.ErrorCode, combatTargetGuid is not null))
                                    {
                                        eventStream.Append(new StreamEvent
                                        {
                                            Sequence = 0,
                                            Utc = DateTimeOffset.UtcNow,
                                            Kind = EventKind.ActionRejected,
                                            Text = $"Attack rejected: {attackLabel}",
                                            ErrorCode = atkDone.ErrorCode,
                                            ErrorLabel = attackLabel,
                                        });
                                    }
                                }
                                else if (combatTargetGuid is not null)
                                {
                                    // errCode == 0 (None) is the normal
                                    // between-swings AttackDone the server
                                    // sends while its auto-repeat loop is
                                    // running (power meter refilling for the
                                    // next swing). It is a positive signal the
                                    // server loop is ALIVE — refresh the
                                    // quiescence clock so the loop-keeper stays
                                    // silent and lets the server keep swinging.
                                    // ActionCancelled (handled above) is the
                                    // OPPOSITE (loop dropped) and must NOT
                                    // refresh this.
                                    lastServerCombatActivityAt = DateTime.UtcNow;
                                }
                            }
                            // combat-damage-output — swing-outcome tracking.
                            // AttackerNotification (0x01B1) = a swing LANDED
                            // (carries damage); EvasionAttackerNotification
                            // (0x01B3) = a swing was EVADED (target avoided
                            // it). The server only sends these to the
                            // attacker, and the bot swings one locked target
                            // at a time, so any such event during an active
                            // combat lock is attributable to combatTargetGuid.
                            // We count landed vs evaded and surface the RAW
                            // outcome to the LLM. Source NEVER decides to stop
                            // attacking on these — disengage + target choice
                            // stay the LLM's call (the existing 60s no-damage
                            // timeout remains the mechanical liveness net).
                            if ((ge.Payload?.AttackerNotification is not null ||
                                 ge.Payload?.EvasionAttackerNotification is not null) &&
                                combatTargetGuid is uint cnTarget)
                            {
                                // Lazily reset counters when the locked target
                                // changed (handles a target switch without
                                // touching every clear site).
                                if (combatStatsForGuid != cnTarget)
                                {
                                    combatSwingsLanded = 0;
                                    combatSwingsEvaded = 0;
                                    combatDamageDealt = 0;
                                    combatFeedbackSent = false;
                                    combatTargetName = null;
                                    combatStatsForGuid = cnTarget;
                                }

                                // A landed/evaded swing notification means the
                                // server's auto-repeat loop just RESOLVED a swing
                                // against our target — the loop is alive. Refresh
                                // the quiescence clock (mirrors the AttackDone(None)
                                // keep-alive) so the loop-keeper defers to the
                                // server loop.
                                if (cnTarget == combatTargetGuid)
                                    lastServerCombatActivityAt = DateTime.UtcNow;

                                if (ge.Payload?.AttackerNotification is { } atkHit)
                                {
                                    combatSwingsLanded++;
                                    combatDamageDealt += atkHit.Damage;
                                    if (!string.IsNullOrEmpty(atkHit.DefenderName))
                                        combatTargetName = atkHit.DefenderName;
                                    Console.WriteLine(
                                        $"[combat] hit \"{atkHit.DefenderName}\" for {atkHit.Damage} " +
                                        $"(landed={combatSwingsLanded} evaded={combatSwingsEvaded} dmg={combatDamageDealt})");
                                }
                                else if (ge.Payload?.EvasionAttackerNotification is { } atkEvade)
                                {
                                    combatSwingsEvaded++;
                                    if (!string.IsNullOrEmpty(atkEvade.DefenderName))
                                        combatTargetName = atkEvade.DefenderName;
                                    Console.WriteLine(
                                        $"[combat] swing EVADED by \"{atkEvade.DefenderName}\" " +
                                        $"(landed={combatSwingsLanded} evaded={combatSwingsEvaded})");
                                }

                                // Surface the live fight outcome to the LLM
                                // prompt (## Combat readiness reads this).
                                worldState.CurrentFight = new CombatFightStatus(
                                    cnTarget, combatTargetName,
                                    combatSwingsLanded, combatSwingsEvaded, combatDamageDealt);

                                // Wake the LLM ONCE per fight when this target
                                // first produces swing-outcome telemetry, so it
                                // re-reads the current-fight line early enough to
                                // assess the engagement. Structural one-shot
                                // (deduped per target), NOT a win/lose judgment:
                                // source surfaces RAW landed/evaded/damage and the
                                // LLM decides whether to keep fighting or disengage
                                // (COMBAT SAFETY rule + the persistent prompt line).
                                // The motor independently breaks a mechanically
                                // non-progressing lock (all swings evaded, 0 damage)
                                // via the early "cannot damage" abandon watchdog —
                                // a liveness/tempo reflex on the bot's OWN swing
                                // outcomes, NOT a target-value judgment; WHICH mob
                                // to fight remains the LLM's choice.
                                if (!combatFeedbackSent)
                                {
                                    combatFeedbackSent = true;
                                    var feedbackName = string.IsNullOrEmpty(combatTargetName)
                                        ? "this target" : combatTargetName;
                                    eventStream.Append(new StreamEvent
                                    {
                                        Sequence = 0,
                                        Utc = DateTimeOffset.UtcNow,
                                        Kind = EventKind.CombatFeedback,
                                        Name = combatTargetName,
                                        Text = $"Fight with \"{feedbackName}\": " +
                                               $"{combatSwingsLanded} swings landed, {combatSwingsEvaded} evaded, " +
                                               $"{combatDamageDealt} damage dealt so far.",
                                    });
                                    Console.WriteLine(
                                        $"[combat] CombatFeedback: fight underway vs \"{feedbackName}\" " +
                                        $"(landed={combatSwingsLanded} evaded={combatSwingsEvaded} dmg={combatDamageDealt}) — waking LLM.");
                                }
                            }
                            // observed-hostile perception. DefenderNotification
                            // (0x01B2) = a creature's swing LANDED on the bot;
                            // EvasionDefenderNotification (0x01B4) = the bot
                            // EVADED an incoming swing. Either way the named
                            // attacker is actively hostile to the bot RIGHT NOW.
                            // Record the attacker's normalized name with the
                            // current time; the set published to
                            // worldState.RecentHostileNames (pruned by TTL just
                            // before each projection build) drives the
                            // ObservedHostile projection flag. The wire carries
                            // only the attacker NAME (no guid), so this is
                            // name-keyed. RAW perception — the LLM owns the
                            // fight-vs-flee decision.
                            {
                                string? hostileName =
                                    ge.Payload?.DefenderNotification?.AttackerName
                                    ?? ge.Payload?.EvasionDefenderNotification?.AttackerName;
                                if (WorldStateProjection.NormalizeHostileName(hostileName) is string hk)
                                {
                                    recentHostileAt[hk] = DateTime.UtcNow;
                                    Console.WriteLine($"[hostile] attacked by \"{hostileName}\" (tracking {recentHostileAt.Count})");
                                }
                            }
                            // M1.5 — surface WeenieErrorWithString
                            // to the EventStream as an ActionRejected
                            // event so the LLM can see the rejection
                            // and pivot. Otherwise the LLM keeps
                            // re-emitting the same Give/Use goal
                            // forever (Society Greeter refusing the
                            // Calling Stone with TradeAiDoesntWant
                            // observed in stalefix-run-01). The
                            // rubber-duck pass said skip the
                            // deterministic anti-repeat gate for
                            // now; the prompt + currentGoal drop in
                            // LlmGoalPolicy is the minimal mechanical
                            // repair.
                            if (ge.Payload?.WeenieErrorWithString is { } wewe &&
                                wewe.ErrorCode != 0)
                            {
                                var label = WeenieErrorLabels.Label(wewe.ErrorCode);
                                eventStream.Append(new StreamEvent
                                {
                                    Sequence = 0,
                                    Utc = DateTimeOffset.UtcNow,
                                    Kind = EventKind.ActionRejected,
                                    Text = wewe.Message,
                                    ErrorCode = wewe.ErrorCode,
                                    ErrorLabel = label,
                                });
                            }
                            // Slice J — InventoryServerSaveFailed
                            // surfaces as ActionRejected so the LLM
                            // and fallback policy can both avoid the
                            // failing item (e.g. pickup of an apple
                            // the bot can't physically reach). The
                            // ItemGuid identifies the target item so
                            // the policy can dedupe by guid, not by
                            // verb/name.
                            if (ge.Payload?.InventoryServerSaveFailed is { } isf &&
                                isf.ErrorType != 0)
                            {
                                var invLabel = WeenieErrorLabels.Label(isf.ErrorType);
                                // cp-2273 — a failure of a SOURCE-AUTONOMOUS
                                // auto-equip (PHASE7F.4 chose to wield this item;
                                // the LLM never asked) must not reach the Strategy
                                // layer: surfaced as a semantic ActionRejected it
                                // invalidates the in-flight plan AND the LLM
                                // mis-attributes it to its own current goal (live:
                                // a level-gated starter cloak's 0x420 LevelTooLow
                                // made the LLM abandon its weapon-wield goal). Drop
                                // it (one-shot, by guid); log for diagnostics. Any
                                // LLM-requested wield/pickup failure surfaces below.
                                if (autoEquipFailureFilter.TryConsumeAutonomous(isf.ItemGuid))
                                {
                                    Console.WriteLine(
                                        $"[auto-equip] suppressed autonomous auto-equip rejection: " +
                                        $"item=0x{isf.ItemGuid:X8} err=0x{isf.ErrorType:X} [{invLabel}] " +
                                        $"(source-autonomous wield; not an LLM goal)");
                                    break;
                                }
                                // Look up the item's name from the
                                // visible-world snapshot so the LLM
                                // sees a human-readable rejection.
                                string? isfName = null;
                                if (worldState.Self is WorldObjectSnapshot isfSelf)
                                {
                                    isfName = worldState.WithinRadius(isfSelf, 999f)
                                        .FirstOrDefault(s => s.Guid == isf.ItemGuid)?.Name;
                                }
                                isfName ??= "(unknown)";
                                eventStream.Append(new StreamEvent
                                {
                                    Sequence = 0,
                                    Utc = DateTimeOffset.UtcNow,
                                    Kind = EventKind.ActionRejected,
                                    Text = $"Inventory action failed on '{isfName}': {invLabel}",
                                    ItemGuid = isf.ItemGuid,
                                    Name = isfName,
                                    ErrorCode = isf.ErrorType,
                                    ErrorLabel = invLabel,
                                });
                            }
                            break;
                        case PrivateUpdatePropertyIntMessage pup:
                            Console.WriteLine(
                                $"[observe]   -> PrivateUpdatePropertyInt: {pup.PropertyName} = {pup.Value} (seq={pup.Sequence})");
                            break;
                        case PrivateUpdatePropertyInt64Message pup64:
                            Console.WriteLine(
                                $"[observe]   -> PrivateUpdatePropertyInt64: {pup64.PropertyName} = {pup64.Value} (seq={pup64.Sequence})");
                            MaybeEmitSelfProgress(ref lastObservedUnspentXp, worldState, eventStream);
                            break;
                        case PrivateUpdateVitalMessage puv:
                            // Surface the bot's own health changes. Only
                            // the health vital feeds self-health state;
                            // stamina/mana are logged at low value too.
                            var vitName = puv.IsHealth ? "Health"
                                : puv.Vital == (uint)VitalKind.MaxStamina ? "Stamina"
                                : puv.Vital == (uint)VitalKind.MaxMana ? "Mana"
                                : $"0x{puv.Vital:X}";
                            if (puv.IsHealth &&
                                worldState.Self is { HealthCurrent: uint hc, HealthMax: uint hm } &&
                                hm > 0)
                            {
                                Console.WriteLine(
                                    $"[vital]   -> self Health current={hc} max={hm} " +
                                    $"frac={(float)hc / hm:F3} (seq={puv.Sequence})");
                                MaybeRecordSelfDeath(hc);
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"[vital]   -> PrivateUpdateVital: {vitName} current={puv.Current} (seq={puv.Sequence})");
                            }
                            break;
                        case PrivateUpdateAttribute2ndLevelMessage pal:
                            // Per-tick current-level vital (the combat-
                            // critical self-health source). Surface the
                            // health fraction from the just-applied state.
                            if (pal.IsHealth &&
                                worldState.Self is { HealthCurrent: uint phc, HealthMax: uint phm } &&
                                phm > 0)
                            {
                                Console.WriteLine(
                                    $"[vital]   -> self Health current={phc} max={phm} " +
                                    $"frac={(float)phc / phm:F3} (seq={pal.Sequence})");
                                MaybeRecordSelfDeath(phc);
                            }
                            else if (pal.IsHealth)
                            {
                                Console.WriteLine(
                                    $"[vital]   -> self Health current={pal.Current} (seq={pal.Sequence})");
                            }
                            break;
                        case UpdatePositionMessage upm:
                            var vel = upm.Velocity is { } v ? $" vel=({v.X:F2},{v.Y:F2},{v.Z:F2})" : "";
                            var plc = upm.PlacementId is { } pid ? $" placement=0x{pid:X}" : "";
                            Console.WriteLine(
                                $"[observe]   -> UpdatePosition: guid=0x{upm.Guid:X8} lb=0x{upm.CellId:X8} " +
                                $"xyz=({upm.Position.X:F2},{upm.Position.Y:F2},{upm.Position.Z:F2}) " +
                                $"rot=({upm.Rotation.W:F3},{upm.Rotation.X:F3},{upm.Rotation.Y:F3},{upm.Rotation.Z:F3})" +
                                $"{vel}{plc} flags=0x{(uint)upm.Flags:X2} seq=(inst={upm.InstanceSequence},pos={upm.PositionSequence},tp={upm.TeleportSequence},fp={upm.ForcePositionSequence})");
                            break;
                        case MotionMessage mm:
                            var bodyDesc = mm.Body is not null
                                ? mm.Body.ToString()
                                : $"raw[{mm.BodyBytes.Length}]";
                            Console.WriteLine(
                                $"[observe]   -> Motion: guid=0x{mm.Guid:X8} type={mm.MovementType} " +
                                $"flags={mm.MotionFlags} style=0x{mm.CurrentStyle:X4} " +
                                $"autonomous={mm.IsAutonomous} " +
                                $"seq=(inst={mm.InstanceSequence},mov={mm.MovementSequence},srv={mm.ServerControlSequence}) " +
                                $"body={bodyDesc}");
                            // Phase 7f.2 — record a server StickToObject that
                            // sticks OUR OWN object to the active combat target.
                            // The loop-keeper re-send is suppressed while this is
                            // fresh so we don't cancel the server's in-progress
                            // move-into-range (see CombatStickSettleSec). Purely
                            // mechanical: it keys only on our own guid + the guid
                            // we are already attacking, no game knowledge.
                            if (mm.Guid == worldState.SelfGuid &&
                                mm.Body?.Invalid?.StickyObjectGuid is uint stickyGuid)
                            {
                                combatServerStickTarget = stickyGuid;
                                combatServerStickAt = DateTime.UtcNow;
                                // A fresh server-driven approach supersedes any
                                // pending cancel-driven fast retry.
                                if (stickyGuid == combatTargetGuid)
                                    combatFastRetryRequested = false;
                            }
                            break;
                        case SetStateMessage ss:
                            Console.WriteLine(
                                $"[observe]   -> SetState: guid=0x{ss.Guid:X8} state=0x{ss.State:X8} " +
                                $"seq=(inst={ss.InstanceSequence},state={ss.StateSequence})");
                            break;
                        case ObjectDeleteMessage od:
                            var deleteVerdict = !preDeletePresent
                                ? "noop (unknown guid)"
                                : applied
                                    ? "removed"
                                    : "dropped (stale instSeq or self)";
                            Console.WriteLine(
                                $"[observe]   -> ObjectDelete: guid=0x{od.Guid:X8} " +
                                $"instSeq={od.InstanceSequence} [{deleteVerdict}]");
                            // Combat: if the object we are actively attacking
                            // was just removed from the world (killed + culled,
                            // or despawned), clear the combat lock NOW instead
                            // of letting the no-progress watchdog burn the full
                            // AbandonOnNoDamageSec on a guid the server no
                            // longer knows (a removed object can never report
                            // damage or health). "removed" (preDeletePresent &&
                            // applied) is the strong signal — a confirmed
                            // client-side delete, not a transient distance cull.
                            // Mechanical motor bookkeeping (object existence);
                            // no game knowledge.
                            if (preDeletePresent && applied &&
                                combatTargetGuid is uint ctgDel && ctgDel == od.Guid)
                            {
                                Console.WriteLine(
                                    $"[combat] target 0x{ctgDel:X8} removed from world — clearing combat " +
                                    $"lock (no AbandonOnNoDamageSec wait).");
                                visitedTargetGuids.Add(ctgDel);
                                combatTargetGuid = null;
                                combatStartedAt = null;
                                lastCombatAttackAt = null;
                                lastDamageAt = null;
                                lastObservedTargetHealthFraction = null;
                                combatFastRetryRequested = false;
                                ClearCombatFightStats();
                            }
                            break;
                        case HearSpeechMessage hs:
                            var hsPreview = hs.Message.Length > 80 ? hs.Message.Substring(0, 80) + "..." : hs.Message;
                            Console.WriteLine(
                                $"[observe]   -> HearSpeech: <{hs.SenderName}> (0x{hs.SenderId:X8}, chatType=0x{hs.ChatMessageType:X}): \"{hsPreview}\"");
                            break;
                        case null when opcode is not null:
                            Console.WriteLine($"[observe]   -> opcode 0x{(uint)opcode.Value:X4} (no decoder yet)");
                            break;
                    }
                }

                // Mirror NetworkSession.HandlePacket line 495: track highest
                // received sequence, skipping Seq=0 and bare AckSequence-only.
                if (pkt.Header.Sequence != 0 && pkt.Header.Flags != PacketHeaderFlags.AckSequence)
                {
                    if (pkt.Header.Sequence > lastReceivedSeq)
                        lastReceivedSeq = pkt.Header.Sequence;

                    // Ack every sequenced inbound packet. Bare AckSequence is
                    // sent unencrypted at Sequence=0 (mirrors server line 742-743
                    // "AckSequence-only doesn't advance sequence").
                    var ack = new OutboundPacket();
                    ack.AddAckSequence(lastReceivedSeq);
                    var ackLen = ack.Pack(sendBuf, myClientId, sequence: 0, iteration: 1,
                                          encrypt: false, cryptoSend: null);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, ackLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    acksSent++;
                    Console.WriteLine($"[observe]   -> sent Ack({lastReceivedSeq}) [{ackLen} bytes] to {_serverPort0}");
                }

                // Reply to TimeSync. Server ignores the inbound timestamp value
                // (NetworkSession line 472-477 "Do something with this...") so
                // echoing the server's value is sufficient. Sent unencrypted at
                // Sequence=0 to avoid burning a cryptoSend slot before we have
                // real game traffic.
                if (pkt.Header.HasFlag(PacketHeaderFlags.TimeSync))
                {
                    var ts = new OutboundPacket();
                    ts.AddTimeSync(pkt.Optional.TimeSync);
                    var tsLen = ts.Pack(sendBuf, myClientId, sequence: 0, iteration: 1,
                                        encrypt: false, cryptoSend: null);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, tsLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    timeSyncsSent++;
                    Console.WriteLine($"[observe]   -> sent TimeSync({pkt.Optional.TimeSync:R}) [{tsLen} bytes]");
                }

                // Phase 3.2: send CharacterCreate once CharacterList shows
                // the test account has zero characters. Server replies with
                // 0xF643 CharacterCreateResponse. Per CharacterHandler.cs:26
                // ([GameMessage(..., SessionState.AuthConnected)]) this is
                // dispatched in the same state CharacterList arrives in -
                // so receiving CharacterList confirms we can send it.
                if (!characterCreateSent && charList is not null && charList.Characters.Count == 0)
                {
                    characterCreateSent = true;
                    var packetSeq = nextOutboundPacketSequence++;
                    var fragSeq   = nextOutboundFragmentSequence++;

                    // Account MUST exactly match the session's account
                    // (CharacterHandler.cs:31-32 silently returns on
                    // mismatch). Take it from CharacterList (which the
                    // server itself populated via Session.Account).
                    //
                    // Train Healing+Jump+TwoHandedCombat so the server's
                    // starter-gear loop (PlayerFactory.cs:225) grants
                    // the Training Spadone (weapon), Handy Healing Kit,
                    // and the Jump bundle (Calling Stone, Pyreal, Sack,
                    // Bread, Ust, Letter From Home). Without these the
                    // bot enters the academy bare-handed and dies on
                    // first contact with a Sparring Golem.
                    var opt = new CharacterCreateMessage.Options(
                        Account: charList.Account,
                        Name:    createName,
                        TrainedSkillIds: CharacterCreateMessage.DefaultTrainedSkillIds);

                    var packedSize = CharacterCreateMessage.MeasurePackedSize(opt);
                    if (packedSize > 448)
                    {
                        Console.WriteLine($"[observe]   -> SKIP CharacterCreate: {packedSize} bytes exceeds 448-byte single-fragment cap");
                        continue;
                    }
                    var ccBuf = new byte[packedSize];
                    var actual = CharacterCreateMessage.Pack(ccBuf, opt);

                    var msg = new OutboundPacket();
                    if (lastReceivedSeq != 0)
                        msg.AddAckSequence(lastReceivedSeq);
                    msg.AddBlobFragment(
                        fragSequence: fragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: ccBuf.AsSpan(0, actual));

                    var sentLen = msg.Pack(sendBuf, myClientId,
                                           sequence: packetSeq, iteration: 1,
                                           encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, sentLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    Console.WriteLine($"[observe]   -> PHASE3.2 SEND: CharacterCreate(account=\"{opt.Account}\" name=\"{opt.Name}\" payload={actual}B) pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");
                    Console.WriteLine($"[observe]      Expect 0xF643 CharacterCreateResponse (Ok=1, NameInUse=3, Corrupt=5, etc.)");
                }

                // Phase 3.3 step 1: send CharacterEnterWorldRequest (0xF7C8,
                // payload-less) once we know a character guid to use. Sources:
                //   - just-created (createResponse.Ok with non-zero guid), or
                //   - already existing in CharacterList (charList.Characters[0])
                // Server replies with CharacterEnterWorldServerReady (0xF7DF)
                // or CharacterError(LogonServerFull) if shutting down.
                if (!enterWorldRequestSent && charList is not null)
                {
                    // Pick the guid we'll commit to in step 2. Prefer the
                    // just-created guid (Phase 3.2 path); fall back to the
                    // first character in the list (re-run path).
                    if (createResponse is { Response: CharacterCreateResponse.Ok } okCreate)
                        chosenCharacterGuid = okCreate.CharacterGuid;
                    else if (charList.Characters.Count > 0)
                        chosenCharacterGuid = charList.Characters[0].Id;

                    if (chosenCharacterGuid != 0)
                    {
                        // Pre-seed the world state with our own guid so
                        // PrivateUpdatePropertyInt (which has no guid in
                        // the wire format and implicitly targets the
                        // receiving session's player) can route to the
                        // self snapshot even though it arrives BEFORE
                        // the server's PlayerCreate for our character.
                        worldState.SetSelf(chosenCharacterGuid);

                        enterWorldRequestSent = true;
                        var packetSeq = nextOutboundPacketSequence++;
                        var fragSeq   = nextOutboundFragmentSequence++;

                        var ewrBuf = new byte[CharacterEnterWorldRequestMessage.PackedSize];
                        var ewrLen = CharacterEnterWorldRequestMessage.Pack(ewrBuf);

                        var msg = new OutboundPacket();
                        if (lastReceivedSeq != 0)
                            msg.AddAckSequence(lastReceivedSeq);
                        msg.AddBlobFragment(
                            fragSequence: fragSeq,
                            fragId: OutboundFragmentId,
                            queue: (ushort)GameMessageGroup.UIQueue,
                            gameMessagePayload: ewrBuf.AsSpan(0, ewrLen));

                        var sentLen = msg.Pack(sendBuf, myClientId,
                                               sequence: packetSeq, iteration: 1,
                                               encrypt: true, cryptoSend: cryptoSend);
                        await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, sentLen),
                                                   SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                        Console.WriteLine($"[observe]   -> PHASE3.3a SEND: CharacterEnterWorldRequest (chosenGuid=0x{chosenCharacterGuid:X8}, pktSeq={packetSeq}, fragSeq={fragSeq}, totalBytes={sentLen})");
                        Console.WriteLine($"[observe]      Expect 0xF7DF CharacterEnterWorldServerReady (or 0xF659 CharacterError on shutdown)");
                    }
                }

                // Phase 3.3 step 2: once server replies with ServerReady,
                // commit by sending CharacterEnterWorld (0xF657) with the
                // chosen guid + session account. Server validates and on
                // success transitions to WorldConnected and begins the
                // world-state firehose (PlayerCreate, PlayerDescription,
                // landblock data, etc.). Failure paths reply with
                // CharacterError (EnterGameCharacterNotOwned=6,
                // EnterGameCharacterInWorld=7, EnterGameGeneric=8, etc.).
                if (!enterWorldSent && enterWorldServerReady is not null && chosenCharacterGuid != 0 && charList is not null)
                {
                    enterWorldSent = true;
                    var packetSeq = nextOutboundPacketSequence++;
                    var fragSeq   = nextOutboundFragmentSequence++;

                    var payloadSize = CharacterEnterWorldMessage.MeasurePackedSize(charList.Account);
                    if (payloadSize > 448)
                    {
                        Console.WriteLine($"[observe]   -> SKIP CharacterEnterWorld: {payloadSize} bytes exceeds 448-byte cap");
                        continue;
                    }
                    var ewBuf = new byte[payloadSize];
                    var ewLen = CharacterEnterWorldMessage.Pack(ewBuf, chosenCharacterGuid, charList.Account);

                    var msg = new OutboundPacket();
                    if (lastReceivedSeq != 0)
                        msg.AddAckSequence(lastReceivedSeq);
                    msg.AddBlobFragment(
                        fragSequence: fragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: ewBuf.AsSpan(0, ewLen));

                    var sentLen = msg.Pack(sendBuf, myClientId,
                                           sequence: packetSeq, iteration: 1,
                                           encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, sentLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    Console.WriteLine($"[observe]   -> PHASE3.3b SEND: CharacterEnterWorld(guid=0x{chosenCharacterGuid:X8}, account=\"{charList.Account}\", payload={ewLen}B) pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");
                    Console.WriteLine($"[observe]      Expect world-state firehose to begin (PlayerCreate, PlayerDescription, landblock data, ...) - or 0xF659 CharacterError on validation failure");
                }

                // Phase 7g — mid-session teleport detector. Run BEFORE
                // the LoginComplete block so the resend trigger has
                // a chance to fire this same tick. We classify a
                // landblock change (top-16 bits of CellId differ)
                // as a mid-session teleport. First non-zero
                // observation seeds the tracker (no resend fires).
                if (worldState.Self is WorldObjectSnapshot lcTeleSelf &&
                    lcTeleSelf.CellId is uint lcTeleSelfCell &&
                    lcTeleSelfCell != 0)
                {
                    var lb = lcTeleSelfCell & 0xFFFF0000u;
                    var landblockChanged = false;
                    // Slice 8 — set when the landblock change was an on-foot
                    // seam crossing (the bot WALKED across), not a teleport.
                    var onFootSeamCross = false;
                    var lcTeleSelfPos2 = new System.Numerics.Vector3(
                        lcTeleSelf.Position.X,
                        lcTeleSelf.Position.Y,
                        lcTeleSelf.Position.Z);
                    if (lastObservedSelfLandblock is uint prevLb)
                    {
                        if (lb != prevLb && loginCompleteSent)
                        {
                            // Distinguish an organic on-foot landblock-seam
                            // crossing from a teleport. A seam step moves only
                            // a few meters in global coords (local coords jump
                            // ~191 -> ~1); a teleport moves hundreds-to-
                            // thousands of meters and/or involves an indoor
                            // cell. Purely geometric — no game knowledge.
                            onFootSeamCross = lastObservedSelfCellId != 0u &&
                                Strategy.AcCoords.IsOnFootSeamCrossing(
                                    lastObservedSelfCellId, lastObservedSelfPos,
                                    lcTeleSelfCell, lcTeleSelfPos2,
                                    OnFootSeamMaxMeters);

                            if (!onFootSeamCross)
                            {
                                Console.WriteLine(
                                    $"[teleport] mid-session landblock change " +
                                    $"0x{prevLb:X8} -> 0x{lb:X8}; queueing " +
                                    $"LoginComplete resend to clear Teleporting.");
                                // A real teleport leaves the server-side
                                // Teleporting flag set; resending LoginComplete
                                // clears it. An on-foot crossing never set it,
                                // so we must NOT resend (it would re-run
                                // OnTeleportComplete mid-walk).
                                loginCompleteResendNeeded = true;
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"[nav] on-foot seam crossing 0x{prevLb:X8} " +
                                    $"-> 0x{lb:X8} (walked); recording CrossedBoundary edge.");
                            }
                            // M1.6 — surface to EventStream so the
                            // LLM policy re-deliberates on the new
                            // landblock (clears any stale "use
                            // calling stone" goal that was tied to
                            // the previous map). Fired for BOTH a
                            // teleport and an on-foot crossing — the
                            // LLM benefits from knowing the landblock
                            // changed regardless of mechanism.
                            eventStream.Append(new StreamEvent
                            {
                                Sequence = 0,
                                Utc = DateTimeOffset.UtcNow,
                                Kind = EventKind.LandblockChanged,
                                LandblockFrom = prevLb,
                                LandblockTo = lb,
                            });
                            // observed-hostile perception: a landblock change
                            // (teleport, recall, on-foot seam, respawn) leaves
                            // the prior attackers behind. Clear the name-keyed
                            // hostile tracker so a same-named creature in the
                            // new area is not falsely flagged hostile from a
                            // stale entry (the TTL would also expire it, but an
                            // explicit clear avoids a brief post-transition
                            // false positive).
                            recentHostileAt.Clear();
                            // cold-start egress: a new landblock is a fresh hunt
                            // zone — the per-dwell killed-kind set must not carry
                            // across the seam (mirrors the dwell/level reset in
                            // LlmGoalPolicy.UpdateDwellTracking).
                            killedKindsThisLandblock.Clear();
                            // Commit B — the inter-landblock edge is
                            // recorded below (after we have created the
                            // arrival node via RecordVisit). Setting
                            // landblockChanged here drives the post-
                            // RecordVisit RecordEdge that joins the
                            // pre-transition node to the arrival node.
                            landblockChanged = true;
                        }
                    }

                    // Phase 7g (intra-landblock teleport) — the landblock-
                    // change detector above misses a teleport that lands in
                    // the SAME landblock (enter-world placement, recall, or a
                    // portal/spell that stays on the current map). Such a
                    // teleport still leaves the server-side Teleporting flag
                    // set, so the server rejects EVERY client position update
                    // until we re-send LoginComplete. Detect it from the
                    // per-object instance + teleport sequences: the teleport
                    // counter advances only on an actual teleport (never on
                    // ordinary movement, which advances SeqPosition), and a
                    // new instance epoch (which resets the per-epoch teleport
                    // counter) also implies a server-side re-placement. An
                    // on-foot landblock-seam crossing uses a different server
                    // opcode and advances neither, so it is naturally
                    // excluded. The compare is instance-aware and wrap-aware;
                    // the resend below re-captures the acked values, so this
                    // fires at most once per teleport.
                    if (loginCompleteSent &&
                        TeleportOccurredSinceLoginComplete(
                            lcTeleSelf.SeqInstance, lcTeleSelf.SeqTeleport,
                            loginCompleteAckedInstanceSeq,
                            loginCompleteAckedTeleportSeq))
                    {
                        Console.WriteLine(
                            $"[teleport] tp-advance inst {loginCompleteAckedInstanceSeq?.ToString() ?? "null"}" +
                            $"->{lcTeleSelf.SeqInstance?.ToString() ?? "null"} " +
                            $"tele {loginCompleteAckedTeleportSeq?.ToString() ?? "null"}" +
                            $"->{lcTeleSelf.SeqTeleport?.ToString() ?? "null"} " +
                            $"(landblock 0x{lb:X8}); queueing LoginComplete " +
                            $"resend to clear Teleporting.");
                        loginCompleteResendNeeded = true;
                    }
                    var prevNodeId = lastVisitNodeId;
                    var lcTeleSelfPos = lcTeleSelfPos2;
                    if (landblockChanged)
                    {
                        // Same-landblock check in NavGraph.RecordVisit
                        // already breaks the per-tick walked chain on a
                        // landblock change, but call it explicitly so
                        // the intent is visible: a cross-landblock
                        // transition is NEVER a continuation of walking.
                        navGraph.BreakWalkedChain();
                    }
                    lastObservedSelfLandblock = lb;
                    lastObservedSelfCellId = lcTeleSelfCell;
                    lastObservedSelfPos = lcTeleSelfPos;
                    // Commit B — record self-position waypoint every
                    // observed self-update. NavGraph's per-tick gate
                    // (MaxTickWalkMeters=2m + MergeRadius=4m) handles
                    // node dedup and chain continuity internally so
                    // the JSONL doesn't blow up on a stationary bot.
                    lastVisitNodeId = navGraph.RecordVisit(
                        lcTeleSelfCell,
                        lcTeleSelfPos,
                        DateTimeOffset.UtcNow);
                    if (landblockChanged &&
                        prevNodeId != Guid.Empty &&
                        prevNodeId != lastVisitNodeId)
                    {
                        // Slice 8 — an on-foot seam crossing is recorded as
                        // a CrossedBoundary edge (re-walkable on foot, so the
                        // route executor's one-crossing prefix can re-use it).
                        // A teleport is recorded as UsedPortal.
                        //
                        // For UsedPortal we don't (yet) know whether the
                        // teleport came from an item (Calling Stone), a world
                        // portal, an NPC trigger, or a spell. UsedPortal is a
                        // safe default — fixed cost, doesn't poison the A*
                        // heuristic. Future work: plumb the LLM-issued goal's
                        // selected item/portal name through and pass it as
                        // useItemName / useObjectGuid.
                        //
                        // Executor contract (see ac-ai-players#75): an
                        // edge with useItemName == null AND useObjectGuid
                        // == null is observational only. The path executor
                        // treats a UsedPortal edge as a hint that a transition
                        // happens here, then asks the LLM for an action. A
                        // CrossedBoundary edge IS directly re-walkable.
                        var crossKind = onFootSeamCross
                            ? NavEdgeKind.CrossedBoundary
                            : NavEdgeKind.UsedPortal;
                        try
                        {
                            navGraph.RecordEdge(
                                prevNodeId,
                                lastVisitNodeId,
                                crossKind,
                                useItemName: null,
                                useObjectGuid: null,
                                DateTimeOffset.UtcNow);
                        }
                        catch (Exception edgeEx)
                        {
                            Console.Error.WriteLine(
                                $"[nav] WARN cross-landblock RecordEdge failed: " +
                                $"{edgeEx.GetType().Name}: {edgeEx.Message}");
                        }
                    }
                }

                // Phase 3.4: send GameActionLoginComplete (0x00A1) once
                // the server has bound our session.Player. The trigger
                // is PlayerCreate (0xF746) for our own chosen guid —
                // that's the canonical "you are now in the world"
                // signal, and the only thing OnTeleportComplete needs
                // server-side. Without this, the character stays as a
                // purple "loading" portal-haze sprite forever, cannot
                // be targeted, and other players see it as in-portal.
                // See Source/ACE.Server/Network/GameAction/Actions/
                //   GameActionLoginComplete.cs for the handler.
                //
                // Phase 7g — also fires when `loginCompleteResendNeeded`
                // is set by the mid-session teleport detector above,
                // re-clearing Teleporting after a cross-landblock USE
                // (Calling Stone, portals, recall spells, etc.).
                if (enterWorldSent && ownPlayerSeen &&
                    (!loginCompleteSent || loginCompleteResendNeeded))
                {
                    var isResend = loginCompleteSent && loginCompleteResendNeeded;
                    loginCompleteSent = true;
                    loginCompleteResendNeeded = false;
                    // Phase 7g — capture the instance + teleport sequences we
                    // are acknowledging with THIS LoginComplete so a later
                    // teleport (which advances the teleport counter, or the
                    // instance epoch) is detected as newer and triggers a
                    // resend. Captured for both the initial send and every
                    // resend, so each teleport is answered exactly once.
                    loginCompleteAckedTeleportSeq = worldState.Self?.SeqTeleport;
                    loginCompleteAckedInstanceSeq = worldState.Self?.SeqInstance;
                    loginCompletePacketIndex = count;
                    var packetSeq = nextOutboundPacketSequence++;
                    var fragSeq   = nextOutboundFragmentSequence++;

                    var lcBuf = new byte[GameActionLoginCompleteMessage.PackedSize];
                    var lcLen = GameActionLoginCompleteMessage.Pack(lcBuf);

                    var msg = new OutboundPacket();
                    if (lastReceivedSeq != 0)
                        msg.AddAckSequence(lastReceivedSeq);
                    msg.AddBlobFragment(
                        fragSequence: fragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: lcBuf.AsSpan(0, lcLen));

                    var sentLen = msg.Pack(sendBuf, myClientId,
                                           sequence: packetSeq, iteration: 1,
                                           encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, sentLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    Console.WriteLine($"[observe]   -> PHASE3.4 SEND: GameActionLoginComplete (payload={lcLen}B) pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}{(isResend ? " [RESEND post-teleport]" : "")}");
                    Console.WriteLine($"[observe]      Expect: server clears Teleporting flag; character becomes solid (no purple portal haze).");
                }

                // Phase 7f.0 — enable AutoRepeatAttacks once, right after
                // we're in the world. A real AC client sets this option so
                // that a single TargetedMeleeAttack starts a server-side
                // swing loop that auto-repeats at weapon cadence until the
                // target dies or leaves range (Player_Melee.cs:375 gates the
                // next swing on GetCharacterOption(AutoRepeatAttacks)).
                // New characters have it OFF (PlayerFactory leaves the
                // SetCharacterOption(AutoRepeatAttacks,true) line commented),
                // so without this the server does ONE swing then OnAttackDone
                // and the bot only re-swings every CombatRetryIntervalSec —
                // far too slow to win a fight. This is mechanical client
                // configuration, not game knowledge.
                if (loginCompleteSent && !autoRepeatOptionSent)
                {
                    autoRepeatOptionSent = true;
                    var optPacketSeq = nextOutboundPacketSequence++;
                    var optFragSeq   = nextOutboundFragmentSequence++;

                    var optBuf = new byte[GameActionSetSingleCharacterOptionMessage.PackedSize];
                    var optLen = GameActionSetSingleCharacterOptionMessage.Pack(
                        optBuf, CharacterOption.AutoRepeatAttacks, value: true);

                    var optMsg = new OutboundPacket();
                    if (lastReceivedSeq != 0)
                        optMsg.AddAckSequence(lastReceivedSeq);
                    optMsg.AddBlobFragment(
                        fragSequence: optFragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: optBuf.AsSpan(0, optLen));

                    var optSent = optMsg.Pack(sendBuf, myClientId,
                                              sequence: optPacketSeq, iteration: 1,
                                              encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, optSent),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    Console.WriteLine($"[observe]   -> PHASE7F.0 SEND: SetSingleCharacterOption(AutoRepeatAttacks=on) pktSeq={optPacketSeq} fragSeq={optFragSeq} totalBytes={optSent}");
                }

                // Phase 7f.D — REACTIVE DISENGAGE (self-preservation
                // reflex). When the bot's OWN health drops critically low
                // mid-combat, break off atomically: reset combat state,
                // drop to NonCombat, and actively flee directly away from
                // the threat. A ~3s LLM round-trip cannot save a ~5-HP
                // bot, so this is a mechanical motor reflex (only the
                // bot's own health vs a threshold; NO game knowledge, NO
                // target choice). It runs BEFORE the watchdog + re-attack
                // so it pre-empts a swing re-send in the same tick. The
                // LLM is told via an ActionRejected event so it
                // re-deliberates rather than re-issuing the dead Attack.
                if (combatTargetGuid is uint dgTarget &&
                    worldState.Self is WorldObjectSnapshot dgSelf &&
                    dgSelf.HealthCurrent is uint dgHc &&
                    dgSelf.HealthMax is uint dgHm &&
                    dgSelf.CellId is uint dgCell && dgCell != 0u &&
                    CombatDisengage.ShouldDisengage(
                        dgHc, dgHm, inCombat: true,
                        CombatDisengageHealthFraction, CombatDisengageCriticalHpFloor))
                {
                    // Capture the threat position BEFORE clearing combat
                    // state. If the threat already left view, flee from our
                    // own position (degenerate → +X fallback in the helper).
                    var dgThreatPos = worldState.TryGet(dgTarget) is WorldObjectSnapshot dgThreat
                        ? dgThreat.Position
                        : dgSelf.Position;

                    Console.WriteLine(
                        $"[combat] DISENGAGE low-health reflex: self HP {dgHc}/{dgHm} " +
                        $"(frac={(double)dgHc / dgHm:F2}); breaking off 0x{dgTarget:X8}, " +
                        $"NonCombat + flee {CombatFleeDistanceUnits:F0}u");

                    // combat-feel: record a NEAR-DEATH against the kind we
                    // are breaking off from, using the threat's identity
                    // BEFORE any combat-state clear below.
                    var dgFoe = worldState.TryGet(dgTarget);
                    combatFeel.RecordNearDeath(new CombatFeelLedger.MobIdentity(
                        dgFoe?.WeenieClassId ?? lastCombatFoe?.Wcid,
                        dgFoe?.Name ?? combatTargetName ?? lastCombatFoe?.Name));
                    PublishCombatHistory();

                    // 1) Reset ALL combat state (incl. fast-retry) so the
                    //    Phase 7f.2 loop-keeper can't re-swing during flee.
                    combatTargetGuid = null;
                    combatStartedAt = null;
                    lastCombatAttackAt = null;
                    lastDamageAt = null;
                    lastObservedTargetHealthFraction = null;
                    combatFastRetryRequested = false;
                    ClearCombatFightStats();

                    // 2) Avoid cooldown for this specific threat so the
                    //    picker doesn't immediately re-walk to the mob that
                    //    nearly killed us. (Pruning of expired entries is
                    //    health-aware and happens after this block — while
                    //    still suppressed we KEEP entries so the threat stays
                    //    filtered even past the time window.)
                    combatAvoidUntil[dgTarget] = DateTime.UtcNow + combatAvoidCooldown;

                    // 3) ChangeCombatMode(NonCombat) — stop the server-side
                    //    swing loop. NonCombat = 1 (CombatMode enum).
                    {
                        var ncPacketSeq = nextOutboundPacketSequence++;
                        var ncFragSeq   = nextOutboundFragmentSequence++;
                        var ncBuf = new byte[GameActionChangeCombatModeMessage.PackedSize];
                        var ncLen = GameActionChangeCombatModeMessage.Pack(
                            ncBuf, newCombatMode: 1u /* NonCombat */);
                        var ncMsg = new OutboundPacket();
                        if (lastReceivedSeq != 0) ncMsg.AddAckSequence(lastReceivedSeq);
                        ncMsg.AddBlobFragment(
                            fragSequence: ncFragSeq,
                            fragId: OutboundFragmentId,
                            queue: (ushort)GameMessageGroup.UIQueue,
                            gameMessagePayload: ncBuf.AsSpan(0, ncLen));
                        var ncSent = ncMsg.Pack(sendBuf, myClientId,
                                                sequence: ncPacketSeq, iteration: 1,
                                                encrypt: true, cryptoSend: cryptoSend);
                        await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, ncSent),
                                                   SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    }

                    // 4) RESET motion state to a clean slate (mirror the
                    //    post-action reset cascade) so the flee lock starts
                    //    fresh and Phase 5b START re-fires cleanly.
                    autonomousPositionSent = false;
                    autonomousPositionGraceStartUtc = null;
                    moveToStateStartSent = false;
                    moveToStateStopSent = false;
                    motionTarget = null;
                    motionRememberedDest = null;
                    motionRememberedSightingId = null;
                    motionIsFrontierProbe = false;
                    motionLockedGoalId = null;
                    motionRotation = null;
                    motionInitialDistance = null;
                    motionStartedAt = null;
                    motionStoppedAt = null;
                    motionLockedCellId = 0;
                    motionDone = false;
                    useSent = false;
                    worldInteractDispatched = false;
                    useSentAt = null;
                    lastSentWaypointPos = null;
                    lastSentWaypointGlobalXY = null;
                    walkTickAps = 0;
                    prevSelfBeforeAp = null;
                    prevSelfCellBeforeAp = null;
                    prevExpectedStepLen = 0f;
                    consecutiveBlockedTicks = 0;
                    motionIndoorPath = null;
                    motionIndoorPathIndex = 0;
                    motionIndoorPathCells = null;
                    motionIndoorPathAttempted = false;
                    outdoorAvoidanceAttempt = 0;
                    motionIsOutdoorFrontierProbe = false;
                    motionOutdoorApCells.Clear();
                    pendingGiveItemGuid = null;
                    pendingUseWithItemGuid = null;
                    lockedGoalKind = null;

                    // 5) Build the synthetic flee destination + an
                    //    Explore-style motion lock so the existing walk-tick
                    //    steers the bot away. Setting motionRememberedDest
                    //    (not just motionTarget) makes the walk-tick treat
                    //    the guid=0 snapshot as a live destination instead
                    //    of stopping immediately (no world object has guid 0).
                    var dgFleePos = CombatDisengage.ComputeFleeDestination(
                        dgSelf.Position, dgThreatPos, CombatFleeDistanceUnits);
                    var dgFleeDest = new WorldObjectSnapshot(0u)
                    {
                        Name = "<flee>",
                        CellId = dgCell,
                        Position = dgFleePos,
                    };

                    Quaternion dgRot =
                        WorldHeading.TryYawToTarget(dgSelf, dgFleeDest, out var dgYaw)
                            ? WorldHeading.RotationFromYaw(dgYaw)
                            : dgSelf.Rotation;

                    motionTarget = dgFleeDest;
                    motionRememberedDest = dgFleeDest;
                    motionRotation = dgRot;
                    lockedGoalKind = GoalKind.Explore;
                    motionLockedGoalId = null; // synthetic reflex — no LLM goal owns it
                    motionInitialDistance = CombatFleeDistanceUnits;
                    autonomousPositionSent = true;
                    autonomousPositionPacketIndex = count;

                    var dgApPacketSeq = nextOutboundPacketSequence++;
                    var dgApFragSeq   = nextOutboundFragmentSequence++;
                    var dgApBuf = new byte[GameActionAutonomousPositionMessage.PackedSize];
                    var dgApLen = GameActionAutonomousPositionMessage.Pack(
                        dgApBuf,
                        cellId: dgCell,
                        pos:    dgSelf.Position,
                        rot:    dgRot,
                        instanceSequence:      dgSelf.SeqInstance      ?? 0,
                        serverControlSequence: dgSelf.SeqServerControl ?? 0,
                        teleportSequence:      dgSelf.SeqTeleport      ?? 0,
                        forcePositionSequence: dgSelf.SeqForcePosition ?? 0,
                        contact: true);
                    var dgApMsg = new OutboundPacket();
                    if (lastReceivedSeq != 0) dgApMsg.AddAckSequence(lastReceivedSeq);
                    dgApMsg.AddBlobFragment(
                        fragSequence: dgApFragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: dgApBuf.AsSpan(0, dgApLen));
                    var dgApSent = dgApMsg.Pack(sendBuf, myClientId,
                                                sequence: dgApPacketSeq, iteration: 1,
                                                encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, dgApSent),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);

                    // 6) Tell Strategy the Attack goal is dead so it
                    //    re-deliberates (don't silently blackhole). Distinct
                    //    label so dedup/anti-fixation can reason about it.
                    eventStream.Append(new StreamEvent
                    {
                        Sequence = 0,
                        Utc = DateTimeOffset.UtcNow,
                        Kind = EventKind.ActionRejected,
                        Text = $"DisengageLowHealth: broke off combat at HP {dgHc}/{dgHm} and fled",
                        ItemGuid = dgTarget,
                        ErrorCode = 0xFFFB,
                        ErrorLabel = "DisengageLowHealth",
                    });
                    tactics.Fail("combat disengage: self-health critical", eventStream);
                }

                // Phase 7f.D — per-tick self-suppression flag + health-aware
                // avoid-cooldown prune. While our health is below the
                // re-engage threshold, a recently-fled threat stays AVOIDED by
                // every target-selection path even after its 30s time window
                // lapses (passive healing usually outlasts 30s) — this stops
                // the autonomous picker walking the still-wounded bot back into
                // melee. Once health recovers we prune time-expired entries so
                // the threat becomes engageable again (no permanent blacklist).
                var selfCombatSuppressed =
                    worldState.Self is WorldObjectSnapshot scSelf &&
                    scSelf.HealthCurrent is uint scHc && scSelf.HealthMax is uint scHm &&
                    CombatDisengage.IsCombatSuppressed(scHc, scHm, CombatReengageHealthFraction);
                if (!selfCombatSuppressed && combatAvoidUntil.Count > 0)
                {
                    foreach (var expired in combatAvoidUntil
                                 .Where(kv => kv.Value <= DateTime.UtcNow)
                                 .Select(kv => kv.Key).ToList())
                        combatAvoidUntil.Remove(expired);
                }

                // Phase 7f — combat watchdog. Switched from absolute
                // 30s wall-clock to "no damage progress for N sec".
                // The bot may take 30+ seconds to land its first hit
                // (unarmed L1 + golem armor); abandoning early
                // wastes the engagement. We only give up if we've
                // landed ZERO damage in AbandonOnNoDamageSec.
                if (combatTargetGuid is uint ctgWatch && combatStartedAt is DateTime cstart)
                {
                    var sinceLastDamage = lastDamageAt is DateTime ld
                        ? (DateTime.UtcNow - ld).TotalSeconds
                        : (DateTime.UtcNow - cstart).TotalSeconds;
                    // Two trips to the SAME abandon path: the absolute 60s
                    // no-damage backstop, and an earlier "cannot damage this
                    // target" conclusion once enough all-evaded swings have
                    // accumulated (own swing outcomes only — see
                    // CombatRetry.ShouldAbandonUnbeatable).
                    string? abandonReason = null;
                    if (sinceLastDamage >= AbandonOnNoDamageSec)
                        abandonReason =
                            $"after {sinceLastDamage:F0}s with no damage";
                    else if (sinceLastDamage >= AbandonAllEvadedMinSec &&
                             CombatRetry.ShouldAbandonUnbeatable(
                                 combatSwingsLanded, combatDamageDealt,
                                 combatSwingsEvaded, AbandonAllEvadedMinSwings))
                        abandonReason =
                            $"after {combatSwingsEvaded} swings all evaded (0 landed, " +
                            $"0 damage) in {sinceLastDamage:F0}s — target out-defends bot";
                    if (abandonReason is not null)
                    {
                        Console.WriteLine(
                            $"[combat] NO-PROGRESS abandon {abandonReason} on 0x{ctgWatch:X8} " +
                            $"lastHealth={lastObservedTargetHealthFraction?.ToString("F3") ?? "<none>"}; " +
                            $"adding to visited so picker moves on (NOT poisoning wcid — other " +
                            $"individuals of the same type may still be killable, e.g. different " +
                            $"position/armor state). Phase 7f.5 changed this from wcid-satisfaction " +
                            $"to per-guid visited so multi-mob rooms aren't exited after one bad fight.");
                        visitedTargetGuids.Add(ctgWatch);
                        combatTargetGuid = null;
                        combatStartedAt = null;
                        lastCombatAttackAt = null;
                        lastDamageAt = null;
                        lastObservedTargetHealthFraction = null;
                        combatFastRetryRequested = false;
                        ClearCombatFightStats();
                    }
                }

                // Phase 7f.2 — RE-ENGAGE safety net. With AutoRepeatAttacks
                // enabled (sent once at Phase 7f.0 after LoginComplete), AC1's
                // melee is a server-side loop: one TargetedMeleeAttack starts
                // the swing loop and the server auto-repeats Attack() at weapon
                // cadence until the target dies, we leave range, or the loop is
                // cancelled by our motion. This re-send every
                // CombatRetryIntervalSec only matters if that loop dropped
                // (e.g. a brief out-of-range step); if the loop is still
                // running server-side this is silently no-op'd by the
                // `if (Attacking || MeleeTarget != null && MeleeTarget.IsAlive) return;`
                // guard in Player_Melee.cs:99 — harmless. (NOT ChangeCombatMode
                // — re-sending CombatMode triggers ActionCancelled via the
                // NextUseTime gate.)
                if (combatTargetGuid is uint ffCtg &&
                    lastCombatAttackAt is DateTime ffLast &&
                    CombatRetry.ShouldReattack(
                        (DateTime.UtcNow - ffLast).TotalSeconds,
                        combatFastRetryRequested,
                        CombatRetryIntervalSec,
                        CombatFastRetryMinIntervalSec,
                        (combatServerStickTarget == ffCtg && combatServerStickAt is DateTime ffSat)
                            ? (DateTime.UtcNow - ffSat).TotalSeconds
                            : (double?)null,
                        CombatStickSettleSec,
                        (lastServerCombatActivityAt is DateTime ffAct)
                            ? (DateTime.UtcNow - ffAct).TotalSeconds
                            : (double?)null,
                        CombatActivityQuiescenceSec) &&
                    worldState.Self is WorldObjectSnapshot ffSelf &&
                    worldState.TryGet(ffCtg) is WorldObjectSnapshot ffTarget &&
                    WorldDistance.TrySquaredDistance(ffSelf, ffTarget, out var ffD2) &&
                    ffD2 <= 16.0f /* StickyDistance^2 = 16 */)
                {
                    var ffFastRetry = combatFastRetryRequested;
                    combatFastRetryRequested = false;

                    var ffPacketSeq = nextOutboundPacketSequence++;
                    var ffFragSeq   = nextOutboundFragmentSequence++;

                    // Mirror the mode the server was last put INTO by the
                    // main PHASE7F dispatch's ChangeCombatMode — this
                    // branch deliberately does NOT re-send ChangeCombatMode
                    // (re-sending it cancels the swing loop). Recomputing
                    // from the wielded weapon could diverge if the loadout
                    // changed mid-fight, sending a missile opcode while the
                    // server still believes we are in Melee mode (silent
                    // no-op). combatAttackMode is set on every ATTACK send.
                    var ffMode = combatAttackMode;
                    byte[] ffBuf;
                    int ffLen;
                    if (ffMode == AttackMode.Missile)
                    {
                        ffBuf = new byte[GameActionTargetedMissileAttackMessage.PackedSize];
                        ffLen = GameActionTargetedMissileAttackMessage.Pack(
                            ffBuf,
                            targetGuid: ffCtg,
                            attackHeight: 2u /* Medium */,
                            accuracyLevel: 1.0f);
                    }
                    else
                    {
                        ffBuf = new byte[GameActionTargetedMeleeAttackMessage.PackedSize];
                        ffLen = GameActionTargetedMeleeAttackMessage.Pack(
                            ffBuf,
                            targetGuid: ffCtg,
                            attackHeight: 2u /* Medium */,
                            powerLevel: 1.0f);
                    }

                    var ffMsg = new OutboundPacket();
                    if (lastReceivedSeq != 0)
                        ffMsg.AddAckSequence(lastReceivedSeq);
                    ffMsg.AddBlobFragment(
                        fragSequence: ffFragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: ffBuf.AsSpan(0, ffLen));

                    var ffSent = ffMsg.Pack(sendBuf, myClientId,
                                            sequence: ffPacketSeq, iteration: 1,
                                            encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, ffSent),
                                                SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    lastCombatAttackAt = DateTime.UtcNow;
                    Console.WriteLine(
                        $"[observe]   -> PHASE7F.2 {(ffFastRetry ? "FAST-RETRY (AttackDone ActionCancelled)" : "RE-ATTACK")}: " +
                        $"cmd={(ffMode == AttackMode.Missile ? "Missile" : "Melee")} " +
                        $"target=0x{ffCtg:X8} dist={Math.Sqrt(ffD2):F2}u " +
                        $"pktSeq={ffPacketSeq} fragSeq={ffFragSeq} totalBytes={ffSent} " +
                        $"(loop-keeper; server no-ops if Attacking==true)");
                }

                // Phase 7f.4 — INVENTORY-EQUIP PASS.
                //
                // The pickup-driven equip path (Phase 6m, lines ~641-705
                // and ~1755-1760) only fires when we PICK UP a wearable
                // off the landscape: the InventoryPutObjInContainer ack
                // triggers a fresh GetAndWieldItem send. That path
                // misses items the server grants at character creation
                // via PlayerFactory's starter-gear loop (Training
                // Spadone for the Two-Handed-Combat skill, Handy
                // Healing Kit, etc.) — those items arrive as
                // ObjectCreate with ContainerGuid=self,
                // WielderGuid=null, ValidLocations!=0 and are never
                // wielded.
                //
                // Fix: on every loop iteration, scan worldState for
                // wearables-in-inventory that we haven't already
                // asked the server to wield, and send GetAndWieldItem
                // for the FIRST one we find (at most one per tick to
                // avoid burst-sending five equip packets in the same
                // millisecond — the server processes them in any
                // order and we'd rather pace the swing).
                //
                // Gates:
                //   - LoginComplete already sent (server has flushed
                //     the initial ObjectCreate firehose by then).
                //   - worldState.Self exists (we know our own guid).
                //   - No active combat lock (don't interrupt a swing
                //     loop by introducing animation cooldown jitter;
                //     equip-from-inventory triggers wield animations
                //     that share the NextUseTime budget with melee).
                //
                // Slot picking matches Phase 6m: lowest set bit of
                // ValidLocations. Multi-slot wearables (rings:
                // FingerWearLeft|Right) get the lowest, which is the
                // canonical default the AC GUI uses.
                //
                // Tracking: inventoryEquipSent records guids we've
                // already issued an equip request for; the snapshot
                // refresh on WieldObject moves WielderGuid != null
                // which excludes the item naturally from subsequent
                // scans, so this set is belt-and-suspenders against
                // re-issuing the equip while waiting for the ack.
                if (loginCompleteSent &&
                    combatTargetGuid is null &&
                    worldState.Self is WorldObjectSnapshot ieSelf &&
                    ieSelf.CellId is uint &&
                    worldState.SelfGuid is uint ieSelfGuid)
                {
                    // Snapshot the bot's own inventory (pack + worn) once so
                    // the per-candidate weapon-collision check below mirrors
                    // the server's CheckWeaponCollision precondition.
                    var ieInventory = new List<WeaponSwap.ItemFacts>();
                    foreach (var io in worldState.Objects.Values)
                    {
                        var ieOwnedBag  = io.ContainerGuid is uint icg && icg == ieSelfGuid;
                        var ieOwnedWorn = io.WielderGuid   is uint iwg && iwg == ieSelfGuid;
                        if (!ieOwnedBag && !ieOwnedWorn) continue;
                        ieInventory.Add(new WeaponSwap.ItemFacts(
                            io.Guid, io.ItemType, io.ValidLocations, io.CurrentWieldedLocation));
                    }

                    WorldObjectSnapshot? ieCandidate = null;
                    uint                 ieEquipSlot = 0;
                    foreach (var snap in worldState.Objects.Values)
                    {
                        if (snap.ContainerGuid is not uint cg || cg != ieSelfGuid) continue;
                        if (snap.WielderGuid is not null) continue;
                        if (snap.ValidLocations is not uint ivl || ivl == 0) continue;
                        if (inventoryEquipSent.Contains(snap.Guid)) continue;
                        var slot = ivl & (~ivl + 1);
                        if (satisfiedEquipSlots.Contains(slot)) continue;
                        if (snap.WeenieClassId is uint wcSat2 &&
                            satisfiedWeenieClasses.Contains(wcSat2)) continue;

                        // Don't auto-equip a primary weapon (melee/missile/
                        // caster) the server would reject because another
                        // primary weapon is already wielded — that wield
                        // silently no-ops as InventoryServerSaveFailed
                        // (err=None) and wastes a round-trip (the Royal Atlatl
                        // vs an already-wielded Training Spadone case). Skip
                        // it here; it stays a candidate for a later tick if the
                        // blocker is dequipped. An intentional weapon SWAP is
                        // the LLM Wield dispatch's job (it dequips the blocker
                        // first via WeaponSwap). This mirrors the server
                        // precondition mechanically — no weapon preference, no
                        // game knowledge; non-weapons never trigger a blocker
                        // and the first weapon (empty weapon slot) is unchanged.
                        if (WeaponSwap.FindBlockingWieldedWeapon(
                                new WeaponSwap.ItemFacts(
                                    snap.Guid, snap.ItemType,
                                    snap.ValidLocations, snap.CurrentWieldedLocation),
                                ieInventory) is not null)
                            continue;

                        ieCandidate = snap;
                        ieEquipSlot = slot;
                        break;
                    }

                    if (ieCandidate is not null)
                    {
                        inventoryEquipSent.Add(ieCandidate.Guid);
                        // cp-2273 — this wield is source-autonomous (the motor
                        // chose it, the LLM never asked). If the server rejects
                        // it, suppress the rejection rather than letting it
                        // invalidate / mislead the LLM's plan.
                        autoEquipFailureFilter.MarkAutonomous(ieCandidate.Guid);
                        if (ieCandidate.WeenieClassId is uint ieWc)
                            pendingEquipWcid[ieCandidate.Guid] = ieWc;

                        var iePacketSeq = nextOutboundPacketSequence++;
                        var ieFragSeq   = nextOutboundFragmentSequence++;
                        var ieBuf = new byte[GameActionGetAndWieldItemMessage.PackedSize];
                        var ieLen = GameActionGetAndWieldItemMessage.Pack(
                            ieBuf,
                            itemGuid: ieCandidate.Guid,
                            equipLocation: (int)ieEquipSlot);
                        var ieOut = new OutboundPacket();
                        if (lastReceivedSeq != 0)
                            ieOut.AddAckSequence(lastReceivedSeq);
                        ieOut.AddBlobFragment(
                            fragSequence: ieFragSeq,
                            fragId: OutboundFragmentId,
                            queue: (ushort)GameMessageGroup.UIQueue,
                            gameMessagePayload: ieBuf.AsSpan(0, ieLen));
                        var ieSent = ieOut.Pack(sendBuf, myClientId,
                                                sequence: iePacketSeq, iteration: 1,
                                                encrypt: true, cryptoSend: cryptoSend);
                        await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, ieSent),
                                                   SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                        Console.WriteLine(
                            $"[observe]   -> PHASE7F.4 SEND INVENTORY-EQUIP: GetAndWieldItem(item=0x{ieCandidate.Guid:X8} " +
                            $"name='{ieCandidate.Name}' wcid={ieCandidate.WeenieClassId} slot=0x{ieEquipSlot:X}) " +
                            $"pktSeq={iePacketSeq} fragSeq={ieFragSeq} totalBytes={ieSent}");
                    }
                }

                // Phase 7f — combat retry. While combat lock is
                // active and the post-action cooldown has elapsed,
                // the natural picker→AP→MoveToState→Attack cycle
                // already re-dispatches an attack every iteration
                // (since the picker re-locks to combatTargetGuid).
                // We just have to make sure CombatRetryIntervalSec
                // actually paces the swings; if 2s cooldown is too
                // tight, the server may queue-saturate. We track
                // lastCombatAttackAt and skip the reset block until
                // the retry interval elapses.
                if (combatTargetGuid is not null && lastCombatAttackAt is DateTime lca &&
                    (DateTime.UtcNow - lca).TotalSeconds < CombatRetryIntervalSec)
                {
                    // Hold off resetting use gates until the retry
                    // window opens. Otherwise the picker would
                    // re-fire ATTACK as soon as motion completes
                    // (likely <2s away from last attack).
                    // No-op: just delay the reset block below.
                }

                // Phase 6g — post-action loop reset. After USE/PICKUP
                // completes and the post-action cooldown elapses, clear
                // all per-action gates so the picker block (below) can
                // fire again with a fresh target. Cap total actions per
                // session so the bot doesn't loop forever.
                //
                // The cooldown is per-target (Portal=6s, default=2s) so
                // server-side portal windups can complete without the
                // picker dispatching a competing AP. See
                // Strategy/MotorPostActionCooldown.cs for the rationale.
                var postActionCooldown = HeadlessAcClient.Strategy.MotorPostActionCooldown.For(motionTarget);
                if (useSent && useSentAt is DateTime usat &&
                    (DateTime.UtcNow - usat) >= postActionCooldown)
                {
                    // While in combat lock: do NOT reset motion
                    // state. Each AP/MS packet OnMoveComplete would
                    // call HandleActionCancelAttack and break the
                    // server-side swing loop. Keep useSent=true and
                    // motionTarget locked so the picker doesn't
                    // re-fire AP, and rely on the Phase 7f.2
                    // RE-ATTACK block (above) to keep the swing
                    // loop alive. The combat watchdog (NO-PROGRESS
                    // abandon) clears the combat lock when the
                    // target dies or stalls, at which point the
                    // reset block can fire normally.
                    var inCombat = combatTargetGuid is not null;
                    if (inCombat)
                    {
                        // Combat lock active — suppress reset so AP
                        // doesn't re-fire and cancel the swing loop.
                    }
                    else
                    {
                    // A barren no-lock idle reset (motionTarget == null, cp-2262)
                    // is not a completed action — don't burn the per-session
                    // action budget (MaxActionsPerSession) on it. Every existing
                    // path into this cascade has a non-null motionTarget (the
                    // interact block requires it to set useSent), so this guard
                    // only affects the new null-target recovery path.
                    if (motionTarget is not null)
                        actionsCompleted++;
                    // Only flag visited for non-combat targets. The
                    // combat-state machine owns the visited add for
                    // golems (on death or timeout) so we re-target
                    // the same guid until it dies. A low-health-suppressed
                    // hostile must NOT be permanently blacklisted here —
                    // its avoid is the temporary combatAvoidUntil cooldown.
                    if (motionTarget is not null && motionTarget.Guid != combatTargetGuid &&
                        !suppressVisitedAddOnReset)
                        visitedTargetGuids.Add(motionTarget.Guid);
                    suppressVisitedAddOnReset = false;

                    // Notify Tactics that the current goal completed.
                    // Used by LlmGoalPolicy to surface a GoalCompleted
                    // event on the EventStream and re-deliberate on
                    // the next ProposeGoal call.
                    //
                    // EXCEPTION — frontier probe: a discovery step toward
                    // an unexplored cell is NOT a goal action. The original
                    // LLM goal (e.g. Talk{Agent}) is still active and its
                    // named target only just became perceivable by entering
                    // the room. Preserve it so the selector re-resolves it
                    // next tick instead of falsely reporting it complete.
                    //
                    // DELIBERATION-RACE GUARD: only signal completion for
                    // the goal THIS lock was created to execute. If a fresh
                    // LLM goal landed mid-motion (CurrentGoal.Id no longer
                    // matches the locked id) or this lock owned no goal at
                    // all (auto-loot / picker no-op arrival → motionLocked
                    // GoalId is null), do NOT Clear — clearing would wipe
                    // the just-established plan before the motor ever
                    // pursues it. That clobber is what stranded fresh L1
                    // bots in the object-rich academy: a 2s fallback lock's
                    // arrival cleared the 7s-latency LLM goal the instant it
                    // landed.
                    var lockOwnsCurrentGoal =
                        motionLockedGoalId is Guid lockedGoalId &&
                        tactics.CurrentGoal is not null &&
                        tactics.CurrentGoal.Id == lockedGoalId;
                    if (motionTarget is not null && !motionIsFrontierProbe && lockOwnsCurrentGoal)
                        tactics.Clear($"action cycle done on '{motionTarget.Name}'", eventStream);
                    else if (motionIsFrontierProbe)
                        Console.WriteLine(
                            "[strategy] frontier probe arrived; LLM goal preserved " +
                            "(room loaded -> selector re-resolves next tick)");
                    else if (motionTarget is not null && tactics.CurrentGoal is not null && !lockOwnsCurrentGoal)
                        Console.WriteLine(
                            "[strategy] motion lock arrived but a different goal is now current " +
                            $"(locked={motionLockedGoalId?.ToString() ?? "none"} current={tactics.CurrentGoal.Id}); " +
                            "preserving the fresh goal (deliberation-race guard)");

                    // visible-recent-interaction: self-emitted echo so the
                    // LLM can see "you already interacted with this world
                    // object N times" and stop re-picking the same chest/
                    // door it just used (the cp-2290 Holtburg Use{Chest} ->
                    // Use{Door} -> Use{Chest} tempo loop). Spatial mirror of
                    // the InventoryItemUsed echo. NOT salient, NOT plan-
                    // invalidating. Gated to actual interact verbs (Use /
                    // Pickup) so nav-only arrivals, frontier probes, and
                    // Explore/Talk completions don't count.
                    if (motionTarget is not null && worldInteractDispatched)
                    {
                        eventStream.Append(new StreamEvent
                        {
                            Sequence = 0,
                            Utc      = DateTimeOffset.UtcNow,
                            Kind     = EventKind.WorldObjectInteracted,
                            ItemGuid = motionTarget.Guid,
                            Wcid     = motionTarget.WeenieClassId,
                            Name     = motionTarget.Name,
                        });
                    }
                    worldInteractDispatched = false;

                    Console.WriteLine(
                        $"[motion] action cycle #{actionsCompleted} complete (visited 0x{motionTarget?.Guid:X8} '{motionTarget?.Name}') " +
                        $"after {postActionCooldown.TotalSeconds:F1}s cooldown; " +
                        $"resetting motion state to pick next target");

                    // Tempo breakdown (motor-dialog-cycle-tempo): split the
                    // per-interaction wall-clock so a live run shows where the
                    // latency lives. lock->stop = walk + AP move round-trips
                    // (paired with the existing motionStoppedAt);
                    // stop->dispatch = post-stop interact/dialog wait;
                    // dispatch->complete = post-action cooldown. Raw timing only.
                    if (motionLockStartedUtc is DateTime tLock)
                    {
                        var tNow = DateTime.UtcNow;
                        var totalMs = (tNow - tLock).TotalMilliseconds;
                        var stopStr = motionStoppedAt is DateTime tStop
                            ? $"lock->stop {(tStop - tLock).TotalMilliseconds:F0}ms " +
                              $"[lock->MSstart {(motionStartedAt is DateTime tMs1 ? (tMs1 - tLock).TotalMilliseconds : double.NaN):F0}ms, " +
                              $"MSstart->stop {(motionStartedAt is DateTime tMs2 ? (tStop - tMs2).TotalMilliseconds : double.NaN):F0}ms, " +
                              $"walkticks {walkTickAps}], " +
                              $"stop->dispatch {(useSentAt is DateTime ud ? (ud - tStop).TotalMilliseconds : double.NaN):F0}ms"
                            : "stop not stamped";
                        var dispatchStr = useSentAt is DateTime us
                            ? $"dispatch->complete {(tNow - us).TotalMilliseconds:F0}ms"
                            : "no dispatch stamp";
                        Console.WriteLine(
                            $"[tempo] action-cycle latency: total {totalMs:F0}ms ({stopStr}, {dispatchStr})");
                    }

                    // Reset every per-action gate.
                    autonomousPositionSent = false;
                    autonomousPositionGraceStartUtc = null;
                    moveToStateStartSent = false;
                    moveToStateStopSent = false;
                    motionTarget = null;
                    motionLockStartedUtc = null;
                    motionRememberedDest = null;
                    motionRememberedSightingId = null;
                    motionIsFrontierProbe = false;
                    motionLockedGoalId = null;
                    motionRotation = null;
                    motionInitialDistance = null;
                    motionStartedAt = null;
                    motionStoppedAt = null;
                    motionLockedCellId = 0;
                    motionDone = false;
                    useSent = false;
                    useSentAt = null;
                    lastSentWaypointPos = null;
                    lastSentWaypointGlobalXY = null;
                    walkTickAps = 0;
                    // Slice S — clear blocked-motion bookkeeping so
                    // a fresh lock starts with a clean slate (the
                    // previous lock may have ended in a "stuck on
                    // wall" state we don't want to inherit).
                    prevSelfBeforeAp = null;
                    prevSelfCellBeforeAp = null;
                    prevExpectedStepLen = 0f;
                    consecutiveBlockedTicks = 0;
                    // Phase 3.1 — wipe the indoor-path cache so the
                    // next motion lock plans a fresh path from the
                    // bot's new position.
                    motionIndoorPath = null;
                    motionIndoorPathIndex = 0;
                    motionIndoorPathCells = null;
                    motionIndoorPathAttempted = false;
                    outdoorAvoidanceAttempt = 0;
                    motionIsOutdoorFrontierProbe = false;
                    motionOutdoorApCells.Clear();
                    pendingGiveItemGuid = null;
                    pendingUseWithItemGuid = null;
                    lockedGoalKind = null;

                    if (actionsCompleted >= MaxActionsPerSession)
                    {
                        Console.WriteLine(
                            $"[motion] max actions per session ({MaxActionsPerSession}) reached; staying idle until observation window closes");
                    }
                    }
                }

                // Slice Q — Container loot extraction pre-emptor.
                // Runs BEFORE the LLM pre-emptor and the schema
                // picker so that loot inside a freshly-opened corpse
                // always wins over the next walking target. Slice P
                // ensures the bot walks to the corpse and USEs it;
                // the server then spawns ObjectCreate messages for
                // each contained item with ContainerGuid=corpse.Guid
                // and NO world Position. WithinRadius is position-
                // based and excludes these items — they are
                // invisible to the standard picker. This block
                // scans for items inside any recently-opened corpse
                // that the bot is still adjacent to (proximity-
                // gated to ~5u) and dispatches PUTITEMINCONTAINER
                // immediately, with NO walk (we just walked to the
                // corpse and have not moved since). After dispatch
                // we synthesize motion state so the post-action
                // reset cascade (HandshakeDriver.cs ~L1759) fires
                // after MotorPostActionCooldown.For(target), marking
                // the loot item visited and clearing motionTarget so
                // the next pre-emptor iteration picks the NEXT item.
                if (!autonomousPositionSent &&
                    !useSent &&
                    motionTarget is null &&
                    combatTargetGuid is null &&
                    actionsCompleted < MaxActionsPerSession &&
                    loginCompleteSent &&
                    !loginCompleteResendNeeded &&
                    loginCompletePacketIndex >= 0 &&
                    (count - loginCompletePacketIndex) >= PostLoginCompleteGracePackets &&
                    worldState.Self is WorldObjectSnapshot lootSelf &&
                    lootSelf.CellId is uint lootSelfCell &&
                    lootSelfCell != 0)
                {
                    // Evict TTL-stale tracked corpses.
                    var lootCutoff = DateTime.UtcNow.AddSeconds(-LootContainerTtlSec);
                    var lootToEvict = new List<uint>();
                    foreach (var lkv in recentlyOpenedContainers)
                    {
                        if (lkv.Value.OpenedAt < lootCutoff)
                            lootToEvict.Add(lkv.Key);
                    }
                    foreach (var lk in lootToEvict)
                        recentlyOpenedContainers.Remove(lk);

                    if (recentlyOpenedContainers.Count > 0)
                    {
                        // Build the set of corpse guids the bot is
                        // still standing next to. A bot that has
                        // wandered away from a corpse cannot be
                        // sending PUT against items inside it (server
                        // would reject; worse, in retail AC the
                        // container auto-closes on player movement).
                        var nearbyContainers = new HashSet<uint>();
                        foreach (var lkv in recentlyOpenedContainers)
                        {
                            var dxL = lkv.Value.OpenedAtPos.X - lootSelf.Position.X;
                            var dyL = lkv.Value.OpenedAtPos.Y - lootSelf.Position.Y;
                            var distLootXY = MathF.Sqrt(dxL * dxL + dyL * dyL);
                            if (distLootXY <= LootContainerProximityRadius)
                                nearbyContainers.Add(lkv.Key);
                        }

                        if (nearbyContainers.Count > 0)
                        {
                            // First unlooted, loot-eligible item
                            // inside any nearby container. Order by
                            // guid for deterministic per-cycle
                            // selection across multi-item loots.
                            var lootItem = worldState.Objects.Values
                                .Where(s => s.Guid != lootSelf.Guid)
                                .Where(s => s.ContainerGuid is uint c && nearbyContainers.Contains(c))
                                .Where(s => !visitedTargetGuids.Contains(s.Guid))
                                .Where(s => s.ItemType is uint t && (t & LootItemTypeMask) != 0)
                                .OrderBy(s => s.Guid)
                                .FirstOrDefault();

                            if (lootItem is not null)
                            {
                                var lootPktSeq  = nextOutboundPacketSequence++;
                                var lootFragSeq = nextOutboundFragmentSequence++;
                                var lootBuf = new byte[GameActionPutItemInContainerMessage.PackedSize];
                                var lootLen = GameActionPutItemInContainerMessage.Pack(
                                    lootBuf,
                                    itemGuid:      lootItem.Guid,
                                    containerGuid: chosenCharacterGuid,
                                    placement:     0);
                                var lootMsg = new OutboundPacket();
                                if (lastReceivedSeq != 0)
                                    lootMsg.AddAckSequence(lastReceivedSeq);
                                lootMsg.AddBlobFragment(
                                    fragSequence: lootFragSeq,
                                    fragId: OutboundFragmentId,
                                    queue: (ushort)GameMessageGroup.UIQueue,
                                    gameMessagePayload: lootBuf.AsSpan(0, lootLen));
                                var lootSent = lootMsg.Pack(sendBuf, myClientId,
                                                            sequence: lootPktSeq, iteration: 1,
                                                            encrypt: true, cryptoSend: cryptoSend);
                                await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, lootSent),
                                                           SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);

                                // Hook the existing wearable equip
                                // pipeline. If the loot is wearable
                                // (ValidLocations != 0), the inventory
                                // ack path will dispatch GetAndWieldItem
                                // and the bot auto-equips upgraded gear.
                                if (lootItem.ValidLocations is uint lvl && lvl != 0)
                                {
                                    var lEquipLoc = lvl & (~lvl + 1);
                                    pendingEquip[lootItem.Guid] = lEquipLoc;
                                    if (lootItem.WeenieClassId is uint lwc)
                                        pendingEquipWcid[lootItem.Guid] = lwc;
                                }

                                Console.WriteLine(
                                    $"[loot] Slice Q PUTITEMINCONTAINER (no walk): " +
                                    $"item=0x{lootItem.Guid:X8} name='{lootItem.Name}' " +
                                    $"itemType=0x{lootItem.ItemType ?? 0:X} " +
                                    $"sourceContainer=0x{lootItem.ContainerGuid ?? 0:X8} " +
                                    $"destContainer=0x{chosenCharacterGuid:X8} " +
                                    $"payload={lootLen}B pktSeq={lootPktSeq} fragSeq={lootFragSeq} totalBytes={lootSent}");

                                // Synthesize motion state. The post-
                                // action reset cascade owns the
                                // visitedTargetGuids.Add and gate
                                // clear — set useSent=true so it
                                // fires after
                                // MotorPostActionCooldown.For(target).
                                // Set autonomousPositionSent /
                                // moveToStateStart / moveToStateStop
                                // / motionDone so the AP-send, MS-
                                // start, walk-tick, MS-stop and
                                // dispatch blocks all skip on this
                                // tick.
                                motionTarget = lootItem;
                                autonomousPositionSent = true;
                                moveToStateStartSent = true;
                                moveToStateStopSent = true;
                                motionDone = true;
                                motionStartedAt = DateTime.UtcNow;
                                motionStoppedAt = DateTime.UtcNow;
                                motionLockedCellId = lootSelfCell;
                                useSent = true;
                                useSentAt = DateTime.UtcNow;
                            }
                            else
                            {
                                // Pre-empt couldn't loot anything
                                // in the nearby containers. Either
                                // they're empty or all items have
                                // been visited. Untrack now to keep
                                // the dict small and avoid repeated
                                // proximity scans of an empty corpse
                                // for the rest of TTL.
                                foreach (var nearbyCorpse in nearbyContainers)
                                {
                                    var anyLeftInside = worldState.Objects.Values.Any(s =>
                                        s.ContainerGuid is uint c && c == nearbyCorpse &&
                                        !visitedTargetGuids.Contains(s.Guid));
                                    if (!anyLeftInside)
                                    {
                                        recentlyOpenedContainers.Remove(nearbyCorpse);
                                        Console.WriteLine(
                                            $"[loot] corpse 0x{nearbyCorpse:X8} fully looted / empty — untracking");
                                    }
                                }
                            }
                        }
                    }
                }

                // M1.6 redo — LLM-driven goal pre-emptor (replaces
                // the Phase 7h hardcoded {Jonathan ← Exit Token,
                // Greeter ← Calling Stone} pair table). Per
                // ac-ai-players#67/#68, source MUST NOT contain
                // wcid literals or NPC name literals as decision
                // inputs. Instead:
                //   1. Build a WorldStateProjection from worldState
                //      + the WeenieRepository (which has cached the
                //      ShortDesc for every wcid we have seen so far,
                //      thanks to the ObjectCreate preload below).
                //   2. tactics.Tick asks the policy to deliberate.
                //      LlmGoalPolicy kicks off an async GitHub Models
                //      call on the first triggering tick (new event
                //      OR no current goal) and returns the prior
                //      goal until the call finishes; the next tick
                //      after completion consumes the result. The
                //      NoQuestKnowledgePolicy fallback handles LLM
                //      failures / disabled-LLM mode without any
                //      content awareness.
                //   3. If the goal is actionable (Give / Use /
                //      Attack / Pickup / Wield), we resolve its
                //      target Selector to a live snapshot via
                //      SelectorResolver, set motionTarget, and (for
                //      Give) set pendingGiveItemGuid so the action-
                //      send block dispatches GiveObjectRequest
                //      instead of Use.
                //   4. Goal kinds we do NOT pre-empt with — Explore,
                //      GoTo — fall through to the schema-only picker
                //      below, which handles wander + door / portal
                //      traversal. Talk IS pre-empted (mechanically
                //      equivalent to USE on an NPC).
                if (!autonomousPositionSent &&
                    !useSent &&
                    motionTarget is null &&
                    combatTargetGuid is null &&
                    actionsCompleted < MaxActionsPerSession &&
                    loginCompleteSent &&
                    !loginCompleteResendNeeded &&
                    loginCompletePacketIndex >= 0 &&
                    (count - loginCompletePacketIndex) >= PostLoginCompleteGracePackets &&
                    worldState.Self is WorldObjectSnapshot tacticsSelf &&
                    tacticsSelf.CellId is uint tacticsSelfCell &&
                    tacticsSelfCell != 0)
                {
                    // observed-hostile perception: prune the recent-attacker
                    // tracker by TTL and publish the fresh normalized-name set
                    // so the projection's ObservedHostile flag reflects "this
                    // creature attacked us within the last few seconds". Done
                    // here (right before every projection build) so expiry is
                    // evaluated each perception tick, NOT only when a new
                    // defender notification happens to arrive.
                    if (recentHostileAt.Count > 0)
                    {
                        var hostileCutoff = DateTime.UtcNow.AddSeconds(-ObservedHostileTtlSeconds);
                        foreach (var staleName in recentHostileAt
                                     .Where(kv => kv.Value < hostileCutoff)
                                     .Select(kv => kv.Key).ToList())
                            recentHostileAt.Remove(staleName);
                    }
                    worldState.RecentHostileNames = recentHostileAt.Count > 0
                        ? new HashSet<string>(recentHostileAt.Keys, StringComparer.Ordinal)
                        : null;
                    // cold-start egress: publish the per-landblock killed-kind
                    // set so the projection (and the hunt-egress override that
                    // reads it) sees which kinds the bot has already farmed here.
                    worldState.KilledKindsThisDwell = killedKindsThisLandblock.Count > 0
                        ? new HashSet<string>(killedKindsThisLandblock, StringComparer.Ordinal)
                        : null;

                    var projection = WorldStateProjection.FromWorldState(
                        worldState, weenies, visibleRadius: 120f, maxVisible: 48);
                    // Slice R wiring — pump lifetime stat counters and
                    // check the top of the intent stack for completion
                    // BEFORE the policy deliberates. If the predicate
                    // pops the top this tick, the next prompt will
                    // render the new top and the LLM picks up. Also
                    // synthesize a GoalCompleted event so HasNewSalient
                    // fires on the next ProposeGoal poll.
                    if (projection is not null)
                    {
                        botStats.Pump(eventStream, projection);

                        // Slice 0 (Hunt) — push operator-authorised
                        // initial intent on the first tick where a
                        // projection is available. Baseline.Capture
                        // needs real world+events data so since-push
                        // predicates (units_traveled_since_push etc.)
                        // start counting from a meaningful zero.
                        //
                        // Pushed once and only once: the flag is
                        // never reset. If the operator wants to
                        // re-arm, they restart the spike. If the
                        // LLM pops the intent, that's a deliberate
                        // strategic decision — code does NOT
                        // re-push behind the LLM's back.
                        if (!initialIntentPushed &&
                            pendingInitialIntentKind is not null &&
                            intentStack.IsEmpty)
                        {
                            var baseline = IntentBaseline.Capture(
                                projection, eventStream, DateTime.UtcNow, botStats);
                            var initial = new Strategy.Intent.Intent
                            {
                                Id = intentIds.Allocate(),
                                Kind = pendingInitialIntentKind,
                                Rationale = $"operator-supplied initial intent (AC_BOTS_INITIAL_INTENT={pendingInitialIntentKind})",
                                Completion = new AlwaysFalsePredicate(),
                                Baseline = baseline,
                                Status = IntentLifecycle.Active,
                            };
                            var pushResult = intentStack.TryPush(initial);
                            if (pushResult == StackOpResult.Ok)
                            {
                                initialIntentPushed = true;
                                Console.WriteLine(
                                    $"[intent-stack] operator push: id={initial.Id} kind={initial.Kind} " +
                                    $"revision_now={intentStack.Revision} depth_now={intentStack.Depth}");
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"[intent-stack] operator push REJECTED: kind={initial.Kind} " +
                                    $"result={pushResult}");
                                // Never retry — if the first push failed
                                // there's something structurally wrong;
                                // re-trying every tick would spam logs.
                                initialIntentPushed = true;
                            }
                        }

                        var poppedTop = intentStack.CheckTopForCompletion(
                            projection, eventStream, DateTime.UtcNow, botStats);
                        if (poppedTop is not null)
                        {
                            Console.WriteLine(
                                $"[intent-stack] auto-popped id={poppedTop.Id} kind={poppedTop.Kind} " +
                                $"status={poppedTop.Status} revision_now={intentStack.Revision} " +
                                $"depth_now={intentStack.Depth}");
                            eventStream.Append(new StreamEvent
                            {
                                Sequence = 0, // overwritten by Append
                                Utc = DateTimeOffset.UtcNow,
                                Kind = EventKind.GoalCompleted,
                                Text = $"IntentCompleted id={poppedTop.Id} kind={poppedTop.Kind} status={poppedTop.Status}",
                            });
                        }
                    }
                    var goal = projection is null ? null : tactics.Tick(projection, eventStream);
                    // Allowlist:
                    //   - Give: walk to NPC, deliver item from bag.
                    //   - Use:  branch on whether target is spatial
                    //           or already in inventory (e.g. Calling
                    //           Stone the bot is carrying).
                    //   - Attack: walk to creature, dispatch melee.
                    //   - Pickup: walk to landscape item, send Use
                    //           (PutItemInContainer pathway).
                    // NOT pre-empted:
                    //   - Wield: the existing pickup→equip handoff
                    //     pipeline (pendingEquip / GetAndWieldItem)
                    //     handles this end-to-end. The LLM cannot
                    //     improve on it AND the pre-emptor has no
                    //     motor for it. If we ever need explicit
                    //     wield-from-bag (re-equip best armor on
                    //     login), add a dedicated dispatch here.
                    //   - Explore / GoTo: fall through to the
                    //     schema-only picker below.
                    //   - Talk: PRE-EMPTED. In AC, "talking to an NPC"
                    //     is mechanically a USE message against the
                    //     NPC's guid — the action-send branch defaults
                    //     to USE for non-hostile / non-give / non-pickup
                    //     targets, so the same walk-to-target codepath
                    //     used for Use works identically for Talk. This
                    //     lets the LLM's name-targeting hint
                    //     (e.g. Talk{name="Jonathan"}) actually drive
                    //     motion instead of falling through to the
                    //     nearest-named picker.
                    //   - Explore: PRE-EMPTED. We synthesize a target
                    //     by picking the farthest visible non-self
                    //     object the bot hasn't recently visited, then
                    //     walk to it with no action-send on arrival.
                    //     This unblocks the bot when the LLM emits
                    //     `Explore{anywhere}` (a goal kind it picks
                    //     when stuck in town with no new NPCs in view).
                    //     Without this branch the goal would lock,
                    //     ResolveTarget would miss (no name match),
                    //     and the bot would sit motionless until the
                    //     LLM picked a different goal.
                    if (goal is not null && goal.Kind == GoalKind.RaiseAttribute)
                    {
                        // Self-action: spend accumulated experience to raise a
                        // primary attribute. The LLM decides WHICH attribute
                        // (goal.Target.name) and HOW MUCH (goal.Amount); the
                        // motor only maps the name to the wire enum id,
                        // validates+clamps the amount to the bot's observed
                        // unspent XP, and sends the opcode. No motion, no world
                        // target. There is NO source default amount and no
                        // attribute preference — an unparseable attribute or a
                        // missing/invalid amount is a motor error (nothing is
                        // sent) and the goal fails so the LLM re-deliberates.
                        long? availXpNow =
                            worldState.Self?.PropertyInt64s is { } selfP64 &&
                            selfP64.TryGetValue(PrivateUpdatePropertyInt64Message.AvailableExperienceId, out var axNow)
                                ? axNow : (long?)null;

                        // Reconcile any prior pending raise: a confirmed drop in
                        // AvailableExperience means the last spend landed; a
                        // stale entry past the timeout is dropped so it can never
                        // wedge the dedup window.
                        if (pendingRaise is { } pr0 &&
                            ((pr0.PreAvailableXp is long preXp && availXpNow is long nowXp && nowXp < preXp) ||
                             (DateTime.UtcNow - pr0.At) > TimeSpan.FromSeconds(12)))
                        {
                            var confirmed = pr0.PreAvailableXp is long pxp && availXpNow is long nxp && nxp < pxp;
                            Console.WriteLine(
                                $"[xp-spend] pending Raise{pr0.Kind} id={pr0.Id} amount={pr0.Amount} " +
                                (confirmed
                                    ? $"CONFIRMED (unspent {pr0.PreAvailableXp}->{availXpNow})"
                                    : "timed out (no unspent-XP confirmation)"));
                            pendingRaise = null;
                        }

                        if (!AttributeRaise.TryResolveAttributeId(goal.Target.Name, out var attrId))
                        {
                            Console.WriteLine(
                                $"[xp-spend] RaiseAttribute: unknown attribute target='{goal.Target}' " +
                                $"(expected strength/endurance/quickness/coordination/focus/self); not sending. " +
                                $"source={goal.Source}");
                            tactics.Fail("raise-attribute: unknown attribute name", eventStream);
                        }
                        else if (!AttributeRaise.TryValidateAndClampAmount(goal.Amount, availXpNow, out var spendAmount))
                        {
                            Console.WriteLine(
                                $"[xp-spend] RaiseAttribute id={attrId}: needs a positive whole `amount` and spendable XP " +
                                $"(amount={(goal.Amount?.ToString() ?? "null")}, unspent={(availXpNow?.ToString() ?? "null")}); not sending. " +
                                $"source={goal.Source}");
                            tactics.Fail("raise-attribute: invalid amount or no unspent XP", eventStream);
                        }
                        else if (pendingRaise is { } prDup)
                        {
                            // Generic pending-action dedup: ONE raise is allowed
                            // in flight at a time. Any raise (same OR a different
                            // attribute/vital) is suppressed while a prior spend
                            // is still awaiting confirmation, so an A->B->A burst
                            // can never double-spend and the single pending entry
                            // is always the one the next confirmed unspent-XP drop
                            // reconciles (never an overwritten/mis-attributed
                            // request). Defer until the pending raise confirms or
                            // times out.
                            Console.WriteLine(
                                $"[xp-spend] RaiseAttribute id={attrId} suppressed — a raise " +
                                $"(Raise{prDup.Kind} id={prDup.Id}) is still " +
                                $"pending (dispatched {(DateTime.UtcNow - prDup.At).TotalSeconds:F1}s ago); " +
                                $"awaiting unspent-XP confirmation.");
                            tactics.Fail("raise-attribute: a raise is already pending", eventStream);
                        }
                        else
                        {
                            var raisePktSeq  = nextOutboundPacketSequence++;
                            var raiseFragSeq = nextOutboundFragmentSequence++;
                            var raiseBuf = new byte[GameActionRaiseAttributeMessage.PackedSize];
                            var raiseLen = GameActionRaiseAttributeMessage.Pack(raiseBuf, attrId, spendAmount);
                            var raiseMsg = new OutboundPacket();
                            if (lastReceivedSeq != 0)
                                raiseMsg.AddAckSequence(lastReceivedSeq);
                            raiseMsg.AddBlobFragment(
                                fragSequence: raiseFragSeq,
                                fragId: OutboundFragmentId,
                                queue: (ushort)GameMessageGroup.UIQueue,
                                gameMessagePayload: raiseBuf.AsSpan(0, raiseLen));
                            var raiseSent = raiseMsg.Pack(sendBuf, myClientId,
                                                          sequence: raisePktSeq, iteration: 1,
                                                          encrypt: true, cryptoSend: cryptoSend);
                            await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, raiseSent),
                                                       SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                            pendingRaise = ("Attribute", attrId, spendAmount, DateTime.UtcNow, availXpNow);
                            Console.WriteLine(
                                $"[strategy] LLM-GOAL RaiseAttribute: id={attrId} amount={spendAmount} " +
                                $"(unspent={(availXpNow?.ToString() ?? "null")}) " +
                                $"source={goal.Source} rationale=\"{goal.Rationale}\"; " +
                                $"pktSeq={raisePktSeq} fragSeq={raiseFragSeq} bytes={raiseSent}");
                            tactics.Clear("raise-attribute dispatched", eventStream);
                        }
                    }
                    else if (goal is not null && goal.Kind == GoalKind.RaiseVital)
                    {
                        // Self-action: spend accumulated experience to raise one
                        // of the three vital MAX pools (max health / stamina /
                        // mana). The LLM decides WHICH vital (goal.Target.name)
                        // and HOW MUCH (goal.Amount); the motor only maps the
                        // name to the wire enum id, validates+clamps the amount
                        // to the bot's observed unspent XP, and sends the opcode.
                        // No motion, no world target. There is NO source default
                        // amount and no vital preference — an unparseable vital or
                        // a missing/invalid amount is a motor error (nothing is
                        // sent) and the goal fails so the LLM re-deliberates.
                        // Mirror of the RaiseAttribute branch; shares the single
                        // in-flight `pendingRaise` slot (both draw the same XP
                        // pool).
                        long? availXpNow =
                            worldState.Self?.PropertyInt64s is { } selfP64v &&
                            selfP64v.TryGetValue(PrivateUpdatePropertyInt64Message.AvailableExperienceId, out var axNowV)
                                ? axNowV : (long?)null;

                        if (pendingRaise is { } pv0 &&
                            ((pv0.PreAvailableXp is long preXpV && availXpNow is long nowXpV && nowXpV < preXpV) ||
                             (DateTime.UtcNow - pv0.At) > TimeSpan.FromSeconds(12)))
                        {
                            var confirmedV = pv0.PreAvailableXp is long pxpV && availXpNow is long nxpV && nxpV < pxpV;
                            Console.WriteLine(
                                $"[xp-spend] pending Raise{pv0.Kind} id={pv0.Id} amount={pv0.Amount} " +
                                (confirmedV
                                    ? $"CONFIRMED (unspent {pv0.PreAvailableXp}->{availXpNow})"
                                    : "timed out (no unspent-XP confirmation)"));
                            pendingRaise = null;
                        }

                        if (!VitalRaise.TryResolveVitalId(goal.Target.Name, out var vitalId))
                        {
                            Console.WriteLine(
                                $"[xp-spend] RaiseVital: unknown vital target='{goal.Target}' " +
                                $"(expected health/stamina/mana); not sending. " +
                                $"source={goal.Source}");
                            tactics.Fail("raise-vital: unknown vital name", eventStream);
                        }
                        else if (!AttributeRaise.TryValidateAndClampAmount(goal.Amount, availXpNow, out var spendAmountV))
                        {
                            Console.WriteLine(
                                $"[xp-spend] RaiseVital id={vitalId}: needs a positive whole `amount` and spendable XP " +
                                $"(amount={(goal.Amount?.ToString() ?? "null")}, unspent={(availXpNow?.ToString() ?? "null")}); not sending. " +
                                $"source={goal.Source}");
                            tactics.Fail("raise-vital: invalid amount or no unspent XP", eventStream);
                        }
                        else if (pendingRaise is { } pvDup)
                        {
                            Console.WriteLine(
                                $"[xp-spend] RaiseVital id={vitalId} suppressed — a raise " +
                                $"(Raise{pvDup.Kind} id={pvDup.Id}) is still " +
                                $"pending (dispatched {(DateTime.UtcNow - pvDup.At).TotalSeconds:F1}s ago); " +
                                $"awaiting unspent-XP confirmation.");
                            tactics.Fail("raise-vital: a raise is already pending", eventStream);
                        }
                        else
                        {
                            var raisePktSeqV  = nextOutboundPacketSequence++;
                            var raiseFragSeqV = nextOutboundFragmentSequence++;
                            var raiseBufV = new byte[GameActionRaiseVitalMessage.PackedSize];
                            var raiseLenV = GameActionRaiseVitalMessage.Pack(raiseBufV, vitalId, spendAmountV);
                            var raiseMsgV = new OutboundPacket();
                            if (lastReceivedSeq != 0)
                                raiseMsgV.AddAckSequence(lastReceivedSeq);
                            raiseMsgV.AddBlobFragment(
                                fragSequence: raiseFragSeqV,
                                fragId: OutboundFragmentId,
                                queue: (ushort)GameMessageGroup.UIQueue,
                                gameMessagePayload: raiseBufV.AsSpan(0, raiseLenV));
                            var raiseSentV = raiseMsgV.Pack(sendBuf, myClientId,
                                                            sequence: raisePktSeqV, iteration: 1,
                                                            encrypt: true, cryptoSend: cryptoSend);
                            await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, raiseSentV),
                                                       SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                            pendingRaise = ("Vital", vitalId, spendAmountV, DateTime.UtcNow, availXpNow);
                            Console.WriteLine(
                                $"[strategy] LLM-GOAL RaiseVital: id={vitalId} amount={spendAmountV} " +
                                $"(unspent={(availXpNow?.ToString() ?? "null")}) " +
                                $"source={goal.Source} rationale=\"{goal.Rationale}\"; " +
                                $"pktSeq={raisePktSeqV} fragSeq={raiseFragSeqV} bytes={raiseSentV}");
                            tactics.Clear("raise-vital dispatched", eventStream);
                        }
                    }
                    else if (goal is not null && goal.Kind == GoalKind.RaiseSkill)
                    {
                        // Self-action: spend accumulated experience to raise a
                        // trained skill. The LLM decides WHICH skill
                        // (goal.Target.name) and HOW MUCH (goal.Amount); the
                        // motor only maps the name to the wire ordinal,
                        // validates+clamps the amount to the bot's observed
                        // unspent XP, and sends the opcode. No motion, no world
                        // target. There is NO source default amount and no
                        // skill preference — an unparseable skill or a
                        // missing/invalid amount is a motor error (nothing is
                        // sent) and the goal fails so the LLM re-deliberates.
                        // The source does NOT pre-judge whether the skill is
                        // trained; the server validates and rejects untrained/
                        // retired skills with a chat message. Mirror of the
                        // RaiseAttribute/RaiseVital branches; shares the single
                        // in-flight `pendingRaise` slot (all draw the same XP
                        // pool).
                        long? availXpNow =
                            worldState.Self?.PropertyInt64s is { } selfP64s &&
                            selfP64s.TryGetValue(PrivateUpdatePropertyInt64Message.AvailableExperienceId, out var axNowS)
                                ? axNowS : (long?)null;

                        if (pendingRaise is { } ps0 &&
                            ((ps0.PreAvailableXp is long preXpS && availXpNow is long nowXpS && nowXpS < preXpS) ||
                             (DateTime.UtcNow - ps0.At) > TimeSpan.FromSeconds(12)))
                        {
                            var confirmedS = ps0.PreAvailableXp is long pxpS && availXpNow is long nxpS && nxpS < pxpS;
                            Console.WriteLine(
                                $"[xp-spend] pending Raise{ps0.Kind} id={ps0.Id} amount={ps0.Amount} " +
                                (confirmedS
                                    ? $"CONFIRMED (unspent {ps0.PreAvailableXp}->{availXpNow})"
                                    : "timed out (no unspent-XP confirmation)"));
                            pendingRaise = null;
                        }

                        if (!SkillRaise.TryResolveSkillId(goal.Target.Name, out var skillId))
                        {
                            Console.WriteLine(
                                $"[xp-spend] RaiseSkill: unknown skill target='{goal.Target}' " +
                                $"(expected a skill name, e.g. \"war magic\"/\"melee defense\"/\"healing\"); not sending. " +
                                $"source={goal.Source}");
                            tactics.Fail("raise-skill: unknown skill name", eventStream);
                        }
                        else if (!AttributeRaise.TryValidateAndClampAmount(goal.Amount, availXpNow, out var spendAmountS))
                        {
                            Console.WriteLine(
                                $"[xp-spend] RaiseSkill id={skillId}: needs a positive whole `amount` and spendable XP " +
                                $"(amount={(goal.Amount?.ToString() ?? "null")}, unspent={(availXpNow?.ToString() ?? "null")}); not sending. " +
                                $"source={goal.Source}");
                            tactics.Fail("raise-skill: invalid amount or no unspent XP", eventStream);
                        }
                        else if (pendingRaise is { } psDup)
                        {
                            Console.WriteLine(
                                $"[xp-spend] RaiseSkill id={skillId} suppressed — a raise " +
                                $"(Raise{psDup.Kind} id={psDup.Id}) is still " +
                                $"pending (dispatched {(DateTime.UtcNow - psDup.At).TotalSeconds:F1}s ago); " +
                                $"awaiting unspent-XP confirmation.");
                            tactics.Fail("raise-skill: a raise is already pending", eventStream);
                        }
                        else
                        {
                            var raisePktSeqS  = nextOutboundPacketSequence++;
                            var raiseFragSeqS = nextOutboundFragmentSequence++;
                            var raiseBufS = new byte[GameActionRaiseSkillMessage.PackedSize];
                            var raiseLenS = GameActionRaiseSkillMessage.Pack(raiseBufS, skillId, spendAmountS);
                            var raiseMsgS = new OutboundPacket();
                            if (lastReceivedSeq != 0)
                                raiseMsgS.AddAckSequence(lastReceivedSeq);
                            raiseMsgS.AddBlobFragment(
                                fragSequence: raiseFragSeqS,
                                fragId: OutboundFragmentId,
                                queue: (ushort)GameMessageGroup.UIQueue,
                                gameMessagePayload: raiseBufS.AsSpan(0, raiseLenS));
                            var raiseSentS = raiseMsgS.Pack(sendBuf, myClientId,
                                                            sequence: raisePktSeqS, iteration: 1,
                                                            encrypt: true, cryptoSend: cryptoSend);
                            await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, raiseSentS),
                                                       SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                            pendingRaise = ("Skill", skillId, spendAmountS, DateTime.UtcNow, availXpNow);
                            Console.WriteLine(
                                $"[strategy] LLM-GOAL RaiseSkill: id={skillId} amount={spendAmountS} " +
                                $"(unspent={(availXpNow?.ToString() ?? "null")}) " +
                                $"source={goal.Source} rationale=\"{goal.Rationale}\"; " +
                                $"pktSeq={raisePktSeqS} fragSeq={raiseFragSeqS} bytes={raiseSentS}");
                            tactics.Clear("raise-skill dispatched", eventStream);
                        }
                    }
                    else if (goal is not null && goal.Kind == GoalKind.Explore)
                    {
                        // `Explore{target}`, honor it: walk to that
                        // specific candidate (typically chosen from
                        // the "## Exploration candidates" prompt
                        // block). Fall back to "farthest unvisited
                        // in 200u" only when the goal has no target
                        // or the target can't be resolved.
                        WorldObjectSnapshot? exploreTarget = null;
                        float bestDist = 0f;

                        if (goal.Target is not null)
                        {
                            // Prefer GUID-addressed Explore (most
                            // reliable — names duplicate). Pull from
                            // the full known-object set so the LLM
                            // can name an off-screen candidate.
                            if (goal.Target.Guid is uint tg
                                && worldState.Objects.TryGetValue(tg, out var byGuid)
                                && byGuid.Guid != tacticsSelf.Guid
                                && byGuid.CellId is uint bgc && bgc != 0u)
                            {
                                exploreTarget = byGuid;
                                if (WorldDistance.TrySquaredDistance(tacticsSelf, byGuid, out var d2g))
                                    bestDist = (float)Math.Sqrt(d2g);
                            }
                            else
                            {
                                // Other selector kinds (Name,
                                // NameContains, Wcid, ItemTypeMask,
                                // ShortDescContains) — delegate to
                                // the canonical resolver so the
                                // landblock-clip + own-guid filters
                                // are consistent with the rest of
                                // the Tactics layer.
                                var resolved = SelectorResolver.ResolveSingleNearest(goal.Target, worldState, referencePoint: tacticsSelf);
                                if (resolved is not null
                                    && resolved.Guid != tacticsSelf.Guid
                                    && resolved.CellId is uint rc && rc != 0u)
                                {
                                    exploreTarget = resolved;
                                    if (WorldDistance.TrySquaredDistance(tacticsSelf, resolved, out var d2r))
                                        bestDist = (float)Math.Sqrt(d2r);
                                }
                            }

                            if (exploreTarget is null)
                            {
                                Console.WriteLine(
                                    $"[strategy] LLM-GOAL Explore{{target}} unresolved " +
                                    $"(guid={(goal.Target.Guid is uint gt ? $"0x{gt:X8}" : "-")} " +
                                    $"name={(goal.Target.Name ?? "-")} " +
                                    $"wcid={(goal.Target.Wcid?.ToString() ?? "-")}); " +
                                    $"falling back to farthest-unvisited in 200u");
                            }
                        }

                        // Slice 4 (FOV consumption): the live worldState
                        // could not resolve the LLM-specified target (it's
                        // out of the bot's current view). Before falling
                        // back to undirected wander, consult remembered
                        // SightedLocation memory for a same-landblock match
                        // and steer toward those coords. This is mechanical
                        // resolution of the LLM's selector against memory —
                        // not autonomous targeting. Only a real arrival
                        // re-perceives (and can later promote the spot to a
                        // visited node); here we just aim the existing walk
                        // machinery at a remembered coordinate.
                        if (exploreTarget is null && goal.Target is not null)
                        {
                            var nowWall = DateTime.UtcNow;
                            var onCooldown = new HashSet<Guid>();
                            foreach (var kv in rememberedSightedCooldownUntil)
                                if (kv.Value > nowWall) onCooldown.Add(kv.Key);

                            var remembered = SightedTargetResolver.Resolve(
                                navGraph.SnapshotSighted(), goal.Target, tacticsSelfCell, onCooldown);
                            if (remembered is not null)
                            {
                                var dest = new WorldObjectSnapshot(0u)
                                {
                                    Name = remembered.Name,
                                    CellId = remembered.CellId,
                                    Position = remembered.Position,
                                };
                                bool destResolved =
                                    WorldDistance.TrySquaredDistance(tacticsSelf, dest, out var d2mem);
                                // Guard: if the bot is already standing on the
                                // remembered coords the live worldState still did
                                // NOT surface the entity — it has moved or
                                // despawned. Selecting it would lock a zero-unit
                                // walk that "arrives" instantly and re-perceives
                                // the same empty spot. Stamp the revisit cooldown
                                // and fall through to undirected wander instead.
                                var memStopRadius = MotorStopRadius.For(dest);
                                if (destResolved && d2mem <= memStopRadius * memStopRadius)
                                {
                                    rememberedSightedCooldownUntil[remembered.Id] =
                                        nowWall + rememberedSightedRevisitCooldown;
                                    Console.WriteLine(
                                        $"[strategy] LLM-GOAL Explore{{target}} memory hit " +
                                        $"'{remembered.Name}' already at bot position but entity " +
                                        $"not in view (moved/despawned); cooling down {rememberedSightedRevisitCooldown.TotalSeconds:F0}s, wandering");
                                }
                                else
                                {
                                    exploreTarget = dest;
                                    motionRememberedDest = dest;
                                    motionRememberedSightingId = remembered.Id;
                                    rememberedSightedCooldownUntil[remembered.Id] =
                                        nowWall + rememberedSightedRevisitCooldown;
                                    if (destResolved)
                                        bestDist = (float)Math.Sqrt(d2mem);
                                    Console.WriteLine(
                                        $"[strategy] LLM-GOAL Explore{{target}} resolved from MEMORY " +
                                        $"(selector {goal.Target}) -> sighted '{remembered.Name}' " +
                                        $"cell=0x{remembered.CellId:X8} " +
                                        $"pos=({remembered.Position.X:F1},{remembered.Position.Y:F1}) " +
                                        $"lastSeen={remembered.LastSeenUtc:HH:mm:ss}; steering to remembered coords");
                                }
                            }
                        }

                        // Cross-landblock FOV consumption (Slices 5-7): the
                        // target was not found live, nor in same-landblock
                        // memory. If the bot remembers seeing it in ANOTHER
                        // landblock, use the bot's OWN explored routing graph
                        // to make safe progress toward it. The route prefix
                        // walks the bot node-to-node through its own recorded
                        // cells and may re-walk a SINGLE recorded landblock
                        // seam on foot (Slice 7), but stops before a second
                        // crossing or any door/portal/item hop — the LLM still
                        // owns those crossing decisions (it re-deliberates,
                        // e.g. to use a portal, at the limit). Still mechanical
                        // resolution of the LLM's selector: the "what" is
                        // goal.Target; pathfinding over explored connectivity
                        // (incl. the one-seam re-walk) is the "how".
                        if (exploreTarget is null && goal.Target is not null)
                        {
                            var nowWall = DateTime.UtcNow;
                            // Bound the advance-node cooldown map: drop
                            // expired entries once it grows past a small cap
                            // so a long-running bot that visits thousands of
                            // waypoints doesn't leak them.
                            if (crossLbAdvanceCooldownUntil.Count > 256)
                            {
                                var expiredAdv = new List<Guid>();
                                foreach (var kv in crossLbAdvanceCooldownUntil)
                                    if (kv.Value <= nowWall) expiredAdv.Add(kv.Key);
                                foreach (var k in expiredAdv)
                                    crossLbAdvanceCooldownUntil.Remove(k);
                            }
                            var onCooldown = new HashSet<Guid>();
                            foreach (var kv in rememberedSightedCooldownUntil)
                                if (kv.Value > nowWall) onCooldown.Add(kv.Key);

                            var farSighting = SightedTargetResolver.ResolveCrossLandblock(
                                navGraph.SnapshotSighted(), goal.Target, tacticsSelfCell, onCooldown);
                            if (farSighting is not null)
                            {
                                var plan = navGraph.PlanWaypointToward(
                                    tacticsSelfCell, tacticsSelf.Position,
                                    farSighting.CellId, farSighting.Position);
                                if (plan.Kind == RouteWaypointKind.Advance &&
                                    plan.BoundaryNode is NavNode boundary &&
                                    plan.Waypoints.Count > 0 &&
                                    (!crossLbAdvanceCooldownUntil.TryGetValue(boundary.Id, out var advCd) ||
                                     advCd <= nowWall))
                                {
                                    // Steer toward the boundary node (the
                                    // farthest on-foot route node the prefix
                                    // reaches — within the bot's landblock or
                                    // the one adjacent landblock the prefix may
                                    // re-walk into across a single recorded
                                    // seam) and pre-populate the motor's
                                    // waypoint follower with the route prefix so
                                    // the bot walks node-to-node THROUGH its own
                                    // explored cells. The PathCells set lets the
                                    // cell-crossing gate slide forward (across
                                    // the seam too) instead of stopping; setting
                                    // motionIndoorPathAttempted suppresses the
                                    // indoor-nav planner from overwriting this
                                    // route-fed path for the lock's lifetime.
                                    var dest = new WorldObjectSnapshot(0u)
                                    {
                                        Name = farSighting.Name,
                                        CellId = boundary.CellId,
                                        Position = boundary.Position,
                                    };
                                    var routeWaypoints = new List<IndoorWaypoint>(plan.Waypoints.Count);
                                    // Forced Floor kind: these are OUTDOOR
                                    // surface hops, not indoor mesh nodes.
                                    // Floor keeps them inert to the door-USE
                                    // pre-emptor (which only fires on Doorway)
                                    // so the follower just walks them as plain
                                    // waypoints. Door/portal/item edges are
                                    // already excluded from the route prefix.
                                    foreach (var wp in plan.Waypoints)
                                        routeWaypoints.Add(new IndoorWaypoint(
                                            wp.Position, wp.CellId,
                                            WalkableNodeKind.Floor, null));
                                    exploreTarget = dest;
                                    motionRememberedDest = dest;
                                    motionRememberedSightingId = farSighting.Id;
                                    motionIndoorPath = routeWaypoints;
                                    motionIndoorPathIndex = 0;
                                    motionIndoorPathCells = plan.PathCells;
                                    motionIndoorPathAttempted = true;
                                    crossLbAdvanceCooldownUntil[boundary.Id] =
                                        nowWall + crossLbAdvanceCooldown;
                                    if (WorldDistance.TrySquaredDistance(tacticsSelf, dest, out var d2adv))
                                        bestDist = (float)Math.Sqrt(d2adv);
                                    Console.WriteLine(
                                        $"[strategy] LLM-GOAL Explore{{target}} CROSS-LB route advance " +
                                        $"(selector {goal.Target}) -> '{farSighting.Name}' in lb " +
                                        $"0x{(farSighting.CellId >> 16):X4}; following {routeWaypoints.Count}-hop " +
                                        $"route prefix through {plan.PathCells.Count} cells to boundary node " +
                                        $"0x{boundary.CellId:X8} pos=({boundary.Position.X:F1},{boundary.Position.Y:F1}) " +
                                        $"routeCost={plan.RouteCostSeconds:F1}s nextLb=0x{plan.NextLandblock:X4}");
                                }
                                else
                                {
                                    // No safe on-foot progress: either the
                                    // bot is already at the prefix limit
                                    // (TransitionPending — the next hop would be
                                    // a SECOND landblock crossing, or is a
                                    // door/portal/item the LLM must decide on),
                                    // there's no explored route (NoRoute), the
                                    // route start anchor isn't in the bot's
                                    // cell, or the boundary node is on cooldown.
                                    // Cool the sighting so we don't re-plan it
                                    // every tick and fall through to undirected
                                    // wander; the LLM re-deliberates on its own
                                    // cadence.
                                    rememberedSightedCooldownUntil[farSighting.Id] =
                                        nowWall + rememberedSightedRevisitCooldown;
                                    Console.WriteLine(
                                        $"[strategy] LLM-GOAL Explore{{target}} '{farSighting.Name}' is " +
                                        $"cross-landblock (lb 0x{(farSighting.CellId >> 16):X4}); " +
                                        $"{plan.Kind} (no on-foot route prefix), boundary nextLb=" +
                                        $"0x{plan.NextLandblock:X4} nextEdge={plan.NextEdgeKind}; cooling down " +
                                        $"{rememberedSightedRevisitCooldown.TotalSeconds:F0}s, wandering");
                                }
                            }
                        }

                        // Autonomous indoor frontier exploration
                        // (road-to-endgame Phase A1, redesigned per the
                        // 3-model architecture consensus 2026-06-03). The
                        // LLM emitted Explore with no resolvable target.
                        // Before the outdoor-scenery wander fallback below,
                        // if the bot is INDOORS, steer toward the nearest
                        // unexplored reachable cell in the static navmesh.
                        // The existing indoor motor (whose K-hop planner
                        // already routes into not-yet-entered cells) walks
                        // there and opens doors en route; crossing the
                        // threshold loads the next room's content so a
                        // named-but-unseen target becomes perceivable next
                        // tick. Domain-general spatial search: reads only
                        // navmesh geometry + the bot's own visited-cell set,
                        // never object names/wcids/types/quest state.
                        if (exploreTarget is null)
                        {
                            var frontier = TryChooseFrontierDest(
                                tacticsSelfCell, frontierCellCooldownUntil);
                            if (frontier is not null)
                            {
                                exploreTarget = frontier;
                                motionRememberedDest = frontier;
                                if (WorldDistance.TrySquaredDistance(tacticsSelf, frontier, out var d2front))
                                    bestDist = (float)Math.Sqrt(d2front);
                                Console.WriteLine(
                                    $"[strategy] LLM-GOAL Explore -> autonomous frontier " +
                                    $"(no resolvable target); stepping toward unexplored cell " +
                                    $"0x{(frontier.CellId ?? 0):X8} " +
                                    $"pos=({frontier.Position.X:F1},{frontier.Position.Y:F1})");
                            }
                        }

                        // Autonomous OUTDOOR frontier exploration — the
                        // surface analogue of the indoor frontier branch
                        // above. The LLM emitted Explore with no resolvable
                        // target and the bot is OUTDOORS (the indoor branch
                        // no-oped). Steer toward the least-explored compass
                        // direction, derived from the bot's OWN recorded
                        // visited positions + pure geometry, and rasterize the
                        // straight segment into motionIndoorPathCells so the
                        // motor traverses every cell it crosses (and at most a
                        // landblock seam or two) instead of HALTING at the
                        // first cell boundary like the naive farthest-visible
                        // fallback below — that halt is why a town-bound bot
                        // only ever reached the nearest civilian NPC and never
                        // open country. Domain-general spatial search: reads no
                        // object names/wcids/types/landblock-ids/quest state,
                        // and decides nothing about INTERACTION — only WHERE to
                        // move so new ground (and whatever lives there) becomes
                        // perceivable. motionIndoorPath stays null, so the
                        // indoor-only waypoint machinery (followingIndoorPath,
                        // door-USE pre-emptor, terminal cell-claim, AP
                        // cell-advance) is all inert; only the outdoor
                        // cell-slide consumes motionIndoorPathCells.
                        if (exploreTarget is null)
                        {
                            // Hunt authorization for the optional frontier
                            // Mob-bias: the bot only steers toward remembered
                            // monster sightings when the LLM/operator has an
                            // active HUNT commitment on the IntentStack — the
                            // operator-root "Hunt" intent, or an LLM-authored
                            // hunt excursion (its own "hunt-excursion" label or
                            // a typed `visible_tag:monster` completion the LLM
                            // chose as the excursion's goalpost). Typed/intent
                            // signals only — no English rationale parsing.
                            var huntBiasAuthorized =
                                Strategy.Intent.HuntAuthorization.IsHuntCommitment(intentStack.Top);
                            var outdoorFrontier = TryChooseOutdoorFrontierDest(
                                tacticsSelfCell, tacticsSelf.Position, navGraph,
                                frontierCellCooldownUntil, huntBiasAuthorized, out var outdoorPathCells);
                            if (outdoorFrontier is not null)
                            {
                                exploreTarget = outdoorFrontier;
                                motionRememberedDest = outdoorFrontier;
                                motionIndoorPathCells = outdoorPathCells;
                                motionIndoorPathAttempted = true;
                                motionIsOutdoorFrontierProbe = true;
                                var (sgx, sgy) = Strategy.AcCoords.ToGlobalXY(tacticsSelfCell, tacticsSelf.Position);
                                var (dgx, dgy) = Strategy.AcCoords.ToGlobalXY(
                                    outdoorFrontier.CellId ?? tacticsSelfCell, outdoorFrontier.Position);
                                bestDist = MathF.Sqrt((dgx - sgx) * (dgx - sgx) + (dgy - sgy) * (dgy - sgy));
                                Console.WriteLine(
                                    $"[strategy] LLM-GOAL Explore -> autonomous OUTDOOR frontier " +
                                    $"(no resolvable target); stepping toward unexplored cell " +
                                    $"0x{(outdoorFrontier.CellId ?? 0):X8} " +
                                    $"pos=({outdoorFrontier.Position.X:F1},{outdoorFrontier.Position.Y:F1}) " +
                                    $"dist={bestDist:F1}u via {outdoorPathCells?.Count ?? 0} cells");
                            }
                        }

                        if (exploreTarget is null)
                        {
                            // Original Explore{anywhere} behaviour:
                            // farthest unvisited within 200u. The
                            // LLM picks Explore without a target
                            // when it has no specific landmark in
                            // mind; "go see something new and far
                            // away" beats wandering randomly.
                            foreach (var snap in worldState.WithinRadius(tacticsSelf, 200f))
                            {
                                if (snap.Guid == tacticsSelf.Guid) continue;
                                if (snap.CellId is not uint sc || sc == 0u) continue;
                                if (visitedTargetGuids.Contains(snap.Guid)) continue;
                                // Phase 7f.D — don't pick a just-fled threat as
                                // an exploration landmark during its avoid cooldown
                                // (or for as long as we remain too hurt to engage).
                                if (combatAvoidUntil.TryGetValue(snap.Guid, out var cauE) &&
                                    (DateTime.UtcNow < cauE || selfCombatSuppressed)) continue;
                                if (!WorldDistance.TrySquaredDistance(tacticsSelf, snap, out var dsq)) continue;
                                var d = (float)Math.Sqrt(dsq);
                                if (d > bestDist)
                                {
                                    bestDist = d;
                                    exploreTarget = snap;
                                }
                            }
                        }

                        if (exploreTarget is null)
                        {
                            Console.WriteLine(
                                $"[strategy] LLM-GOAL Explore: no fresh target in 200u; " +
                                $"clearing so picker can deliberate again");
                            tactics.Fail("explore: no fresh target", eventStream);
                        }
                        else
                        {
                            float yawX = 0f;
                            Quaternion rotX;
                            if (WorldHeading.TryYawToTarget(tacticsSelf, exploreTarget, out yawX))
                                rotX = WorldHeading.RotationFromYaw(yawX);
                            else
                                rotX = tacticsSelf.Rotation;

                            motionTarget   = exploreTarget;
                            motionRotation = rotX;
                            lockedGoalKind = GoalKind.Explore;
                            motionLockedGoalId = goal.Id;
                            motionLockStartedUtc = DateTime.UtcNow;
                            motionInitialDistance = bestDist;

                            autonomousPositionSent = true;
                            autonomousPositionPacketIndex = count;

                            var apPacketSeqX = nextOutboundPacketSequence++;
                            var apFragSeqX   = nextOutboundFragmentSequence++;
                            var apBufX = new byte[GameActionAutonomousPositionMessage.PackedSize];
                            var apLenX = GameActionAutonomousPositionMessage.Pack(
                                apBufX,
                                cellId: tacticsSelfCell,
                                pos:    tacticsSelf.Position,
                                rot:    rotX,
                                instanceSequence:      tacticsSelf.SeqInstance      ?? 0,
                                serverControlSequence: tacticsSelf.SeqServerControl ?? 0,
                                teleportSequence:      tacticsSelf.SeqTeleport      ?? 0,
                                forcePositionSequence: tacticsSelf.SeqForcePosition ?? 0,
                                contact: true);
                            var apMsgX = new OutboundPacket();
                            if (lastReceivedSeq != 0)
                                apMsgX.AddAckSequence(lastReceivedSeq);
                            apMsgX.AddBlobFragment(
                                fragSequence: apFragSeqX,
                                fragId: OutboundFragmentId,
                                queue: (ushort)GameMessageGroup.UIQueue,
                                gameMessagePayload: apBufX.AsSpan(0, apLenX));
                            var apSentX = apMsgX.Pack(sendBuf, myClientId,
                                                      sequence: apPacketSeqX, iteration: 1,
                                                      encrypt: true, cryptoSend: cryptoSend);
                            await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, apSentX),
                                                       SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);

                            Console.WriteLine(
                                $"[strategy] LLM-GOAL LOCK kind=Explore " +
                                $"target='{exploreTarget.Name}' guid=0x{exploreTarget.Guid:X8} " +
                                $"cell=0x{(exploreTarget.CellId ?? 0):X8} " +
                                $"dist={bestDist:F2}u " +
                                $"source={goal.Source} rationale=\"{goal.Rationale}\"; " +
                                $"sent AP yaw={yawX:F3}rad pktSeq={apPacketSeqX} fragSeq={apFragSeqX} bytes={apSentX}");
                        }
                    }
                    else if (goal is not null &&
                        (goal.Kind == GoalKind.Give ||
                         goal.Kind == GoalKind.Use ||
                         goal.Kind == GoalKind.Talk ||
                         goal.Kind == GoalKind.Attack ||
                         goal.Kind == GoalKind.Wield ||
                         goal.Kind == GoalKind.Pickup))
                    {
                        var targetSnap = tactics.ResolveTarget(worldState, tacticsSelf);
                        var combatDeferredAttack = false;
                        // Phase 7f.D — refuse to LOCK a walk toward a hostile
                        // while we're too hurt to safely engage (self-health
                        // below the re-engage hysteresis threshold) OR the
                        // threat is still on the post-disengage avoid cooldown.
                        // Treat the target as unresolved so the bot wanders a
                        // frontier away instead of walking back into melee. The
                        // dispatch-layer suppression guard remains the secondary
                        // net for health that drops DURING an already-accepted
                        // approach. Self-state + own bookkeeping only — no game
                        // knowledge, no target choice (the LLM still picked WHAT
                        // to fight; this only defers it while recovering).
                        if (goal.Kind == GoalKind.Attack && targetSnap is not null &&
                            ((worldState.Self is WorldObjectSnapshot accSelf &&
                              accSelf.HealthCurrent is uint accHc && accSelf.HealthMax is uint accHm &&
                              CombatDisengage.IsCombatSuppressed(accHc, accHm, CombatReengageHealthFraction)) ||
                             (combatAvoidUntil.TryGetValue(targetSnap.Guid, out var accAvoid) && DateTime.UtcNow < accAvoid)))
                        {
                            Console.WriteLine(
                                $"[combat] REFUSE Attack approach on 0x{targetSnap.Guid:X8} '{targetSnap.Name}' — " +
                                $"self-health below re-engage threshold or threat on avoid cooldown; " +
                                $"treating as unresolved (wander away, do not walk into melee while recovering)");
                            targetSnap = null;
                            combatDeferredAttack = true;
                        }
                        WorldObjectSnapshot? itemSnap = null;
                        // Give always carries an item; Use carries one only
                        // for a two-object "use item on target" (e.g. a key
                        // on a locked chest). In both cases the item must be
                        // in our inventory.
                        var goalCarriesItem =
                            goal.Kind == GoalKind.Give ||
                            ((goal.Kind == GoalKind.Use || goal.Kind == GoalKind.Wield) &&
                             goal.Item is not null && !goal.Item.IsEmpty);
                        if (goalCarriesItem)
                        {
                            itemSnap = tactics.ResolveItem(worldState);
                            // The item must be in our inventory. Resolver does
                            // not filter on container; do that here.
                            if (itemSnap is not null &&
                                !(itemSnap.ContainerGuid is uint icg && icg == tacticsSelf.Guid))
                            {
                                itemSnap = null;
                            }
                        }

                        // Self-arming — explicit Wield dispatch. The canonical
                        // Wield shape carries the weapon in goal.Item with
                        // target=self; tolerate the LLM placing the weapon in
                        // target instead. Either way the weapon must be an
                        // equippable item already in our bag (ContainerGuid==self
                        // and ValidLocations!=0). Purely mechanical: the LLM chose
                        // WHICH item; we only execute, equipping to the lowest
                        // valid slot (the canonical default the AC GUI uses,
                        // mirroring the PHASE7F.4 pickup->auto-equip path). No
                        // source-side "best item" choice. Previously Wield was
                        // unhandled — a dead schema verb; the bag weapon only got
                        // wielded by the combat-gated PHASE7F.4 auto-equip pass,
                        // so an LLM that correctly chose to arm had no effect.
                        if (goal.Kind == GoalKind.Wield)
                        {
                            // Prefer the resolved inventory item (goal.Item,
                            // already filtered to in-bag above); fall back to an
                            // in-bag target selector if the LLM emitted the weapon
                            // as the target.
                            var wieldItem = itemSnap ??
                                (targetSnap is not null &&
                                 targetSnap.ContainerGuid is uint wtcg && wtcg == tacticsSelf.Guid
                                    ? targetSnap : null);
                            if (wieldItem is not null &&
                                wieldItem.ValidLocations is uint wieldVl && wieldVl != 0)
                            {
                                // cp-2273 — the LLM has explicitly taken ownership
                                // of this guid. Drop any source-autonomous marker so
                                // a subsequent InventoryServerSaveFailed for it
                                // surfaces normally (the LLM asked for this wield).
                                autoEquipFailureFilter.ClearAutonomous(wieldItem.Guid);
                                var wieldSlot = wieldVl & (~wieldVl + 1);

                                // Dequip-before-wield (weapon swap). The ACE
                                // server's CheckWeaponCollision refuses to wield
                                // a weapon while another weapon is equipped (it
                                // silently no-ops as InventoryServerSaveFailed
                                // err=None). If a currently-wielded weapon blocks
                                // this one, move the blocker into the pack FIRST;
                                // its PutItemInContainer ack drives the deferred
                                // wield (pendingWieldAfterDequip). Pure mechanical
                                // prerequisite the server requires — the LLM still
                                // chose WHICH weapon. Non-weapon wields
                                // (armor/cloak/hat) and wields into an empty weapon
                                // slot find no blocker and dispatch directly,
                                // byte-identical to before.
                                var swapInventory = new List<WeaponSwap.ItemFacts>();
                                foreach (var so in worldState.Objects.Values)
                                {
                                    var ownedBag  = so.ContainerGuid is uint scg && scg == tacticsSelf.Guid;
                                    var ownedWorn = so.WielderGuid is uint swg && swg == tacticsSelf.Guid;
                                    if (!ownedBag && !ownedWorn) continue;
                                    swapInventory.Add(new WeaponSwap.ItemFacts(
                                        so.Guid, so.ItemType, so.ValidLocations, so.CurrentWieldedLocation));
                                }
                                var blockerGuid = WeaponSwap.FindBlockingWieldedWeapon(
                                    new WeaponSwap.ItemFacts(
                                        wieldItem.Guid, wieldItem.ItemType,
                                        wieldItem.ValidLocations, wieldItem.CurrentWieldedLocation),
                                    swapInventory);

                                if (blockerGuid is uint blocker)
                                {
                                    // A stale pending entry means the dequip
                                    // never acked (e.g. the pack was full) — do
                                    // not soft-lock this blocker forever; let it
                                    // re-dispatch after a bounded wait.
                                    var pendingFresh =
                                        pendingWieldAfterDequip.TryGetValue(blocker, out var pend) &&
                                        (DateTime.UtcNow - pend.StartedUtc) < TimeSpan.FromSeconds(10);
                                    if (pendingFresh)
                                    {
                                        // Dequip already in flight for this
                                        // blocker; the put-ack will wield. Don't
                                        // spam a duplicate dequip or a colliding
                                        // direct wield. But if the LLM has since
                                        // re-targeted a DIFFERENT weapon behind the
                                        // same blocker, retarget the in-flight swap
                                        // so the put-ack wields the newest choice
                                        // (no second dequip — the blocker is the
                                        // same and already moving).
                                        if (pend.TargetGuid != wieldItem.Guid ||
                                            pend.TargetSlot != wieldSlot)
                                        {
                                            pendingWieldAfterDequip[blocker] =
                                                (wieldItem.Guid, wieldSlot, pend.StartedUtc);
                                            inventoryEquipSent.Add(wieldItem.Guid);
                                            Console.WriteLine(
                                                $"[strategy] Wield swap retargeted: blocker=0x{blocker:X8} " +
                                                $"now wields item='{wieldItem.Name}' guid=0x{wieldItem.Guid:X8} " +
                                                $"slot=0x{wieldSlot:X}; awaiting in-flight dequip ack.");
                                        }
                                        else
                                        {
                                            Console.WriteLine(
                                                $"[strategy] Wield swap already pending: blocker=0x{blocker:X8}; awaiting dequip ack.");
                                        }
                                    }
                                    else
                                    {
                                        // Suppress the startup auto-equip pass from
                                        // re-wielding the weapon we are dequipping,
                                        // AND from racing a (doomed) wield of the
                                        // target while the blocker is still equipped
                                        // — the deferred put-ack wield owns the
                                        // target.
                                        pendingWieldAfterDequip[blocker] = (wieldItem.Guid, wieldSlot, DateTime.UtcNow);
                                        inventoryEquipSent.Add(blocker);
                                        inventoryEquipSent.Add(wieldItem.Guid);
                                        var dqPktSeq  = nextOutboundPacketSequence++;
                                        var dqFragSeq = nextOutboundFragmentSequence++;
                                        var dqBuf = new byte[GameActionPutItemInContainerMessage.PackedSize];
                                        var dqLen = GameActionPutItemInContainerMessage.Pack(
                                            dqBuf,
                                            itemGuid:      blocker,
                                            containerGuid: chosenCharacterGuid,
                                            placement:     0);
                                        var dqMsg = new OutboundPacket();
                                        if (lastReceivedSeq != 0)
                                            dqMsg.AddAckSequence(lastReceivedSeq);
                                        dqMsg.AddBlobFragment(
                                            fragSequence: dqFragSeq,
                                            fragId: OutboundFragmentId,
                                            queue: (ushort)GameMessageGroup.UIQueue,
                                            gameMessagePayload: dqBuf.AsSpan(0, dqLen));
                                        var dqSent = dqMsg.Pack(sendBuf, myClientId,
                                                                sequence: dqPktSeq, iteration: 1,
                                                                encrypt: true, cryptoSend: cryptoSend);
                                        await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, dqSent),
                                                                   SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                                        Console.WriteLine(
                                            $"[strategy] LLM-GOAL Wield needs swap: dequip blocker=0x{blocker:X8} then " +
                                            $"wield item='{wieldItem.Name}' guid=0x{wieldItem.Guid:X8} slot=0x{wieldSlot:X} " +
                                            $"source={goal.Source} rationale=\"{goal.Rationale}\"; " +
                                            $"sent PutItemInContainer pktSeq={dqPktSeq} fragSeq={dqFragSeq} bytes={dqSent}");
                                    }
                                    tactics.Clear("dequip-before-wield dispatched", eventStream);
                                }
                                else
                                {
                                var wieldPktSeq  = nextOutboundPacketSequence++;
                                var wieldFragSeq = nextOutboundFragmentSequence++;
                                var wieldBuf = new byte[GameActionGetAndWieldItemMessage.PackedSize];
                                var wieldLen = GameActionGetAndWieldItemMessage.Pack(
                                    wieldBuf, itemGuid: wieldItem.Guid, equipLocation: (int)wieldSlot);
                                var wieldMsg = new OutboundPacket();
                                if (lastReceivedSeq != 0)
                                    wieldMsg.AddAckSequence(lastReceivedSeq);
                                wieldMsg.AddBlobFragment(
                                    fragSequence: wieldFragSeq,
                                    fragId: OutboundFragmentId,
                                    queue: (ushort)GameMessageGroup.UIQueue,
                                    gameMessagePayload: wieldBuf.AsSpan(0, wieldLen));
                                var wieldSent = wieldMsg.Pack(sendBuf, myClientId,
                                                              sequence: wieldPktSeq, iteration: 1,
                                                              encrypt: true, cryptoSend: cryptoSend);
                                await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, wieldSent),
                                                           SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                                // Suppress a duplicate auto-equip for the same item
                                // while we await the WieldObject ack (mirrors PHASE7F.4).
                                inventoryEquipSent.Add(wieldItem.Guid);
                                Console.WriteLine(
                                    $"[strategy] LLM-GOAL Wield direct: " +
                                    $"item='{wieldItem.Name}' guid=0x{wieldItem.Guid:X8} slot=0x{wieldSlot:X} " +
                                    $"source={goal.Source} rationale=\"{goal.Rationale}\"; " +
                                    $"pktSeq={wieldPktSeq} fragSeq={wieldFragSeq} bytes={wieldSent}");
                                tactics.Clear("wield dispatched", eventStream);
                                }
                            }
                            else
                            {
                                // Cannot arm with this selector (not an equippable
                                // in-bag weapon). FAIL explicitly so the goal is
                                // rejected — NOT silently locked-to-self and then
                                // completed as a no-op by the motor, which has no
                                // Wield arrival handler.
                                Console.WriteLine(
                                    $"[strategy] LLM-GOAL Wield unresolved -- " +
                                    $"item={(itemSnap is null ? "MISS" : "ok")} " +
                                    $"target={(targetSnap is null ? "MISS" : "ok")}; " +
                                    $"selector target={goal.Target} item={goal.Item}; failing.");
                                tactics.Fail("wield: no equippable inventory weapon", eventStream);
                            }
                        }
                        // M1.6 — inventory-Use direct dispatch. If
                        // the LLM resolved a Use to an item already
                        // in our bag (e.g. Calling Stone we are
                        // carrying), walking to its spatial position
                        // is meaningless (it has none). Send the
                        // GameActionUse straight to the server here
                        // and clear the goal — no motor, no AP, no
                        // motion lock. Pickup is excluded because
                        // an item already in bag cannot be picked up.
                        else if (goal.Kind == GoalKind.Use &&
                            targetSnap is not null &&
                            targetSnap.ContainerGuid is uint useContainer &&
                            useContainer == tacticsSelf.Guid)
                        {
                            var invUsePktSeq  = nextOutboundPacketSequence++;
                            var invUseFragSeq = nextOutboundFragmentSequence++;
                            var invUseBuf = new byte[GameActionUseMessage.PackedSize];
                            var invUseLen = GameActionUseMessage.Pack(invUseBuf, targetSnap.Guid);
                            var invUseMsg = new OutboundPacket();
                            if (lastReceivedSeq != 0)
                                invUseMsg.AddAckSequence(lastReceivedSeq);
                            invUseMsg.AddBlobFragment(
                                fragSequence: invUseFragSeq,
                                fragId: OutboundFragmentId,
                                queue: (ushort)GameMessageGroup.UIQueue,
                                gameMessagePayload: invUseBuf.AsSpan(0, invUseLen));
                            var invUseSent = invUseMsg.Pack(sendBuf, myClientId,
                                                            sequence: invUsePktSeq, iteration: 1,
                                                            encrypt: true, cryptoSend: cryptoSend);
                            await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, invUseSent),
                                                       SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                            Console.WriteLine(
                                $"[strategy] LLM-GOAL inventory-USE direct: " +
                                $"item='{targetSnap.Name}' guid=0x{targetSnap.Guid:X8} " +
                                $"source={goal.Source} rationale=\"{goal.Rationale}\"; " +
                                $"pktSeq={invUsePktSeq} fragSeq={invUseFragSeq} bytes={invUseSent}");
                            // Inventory-USE dedup (2026-05-30): record the
                            // dispatch so LlmGoalPolicy.IsInventoryUseRecentlyDispatched
                            // can drop repeat goals against the same item.
                            // Non-consumable tutorial letters in spike
                            // bot_stalenarrow01 caused 5 Use{Letter From
                            // Home} goals in 3 min and crowded out Attack
                            // emission. NOT salient, NOT plan-invalidating
                            // — purely a self-emitted echo for dedup.
                            eventStream.Append(new StreamEvent
                            {
                                Sequence = 0,
                                Utc      = DateTimeOffset.UtcNow,
                                Kind     = EventKind.InventoryItemUsed,
                                ItemGuid = targetSnap.Guid,
                                Wcid     = targetSnap.WeenieClassId,
                                Name     = targetSnap.Name,
                            });
                            tactics.Clear("inventory-use dispatched", eventStream);
                        }
                        else
                        {
                            var actionable =
                                targetSnap is not null &&
                                // A goal that carries an inventory item (Give
                                // always; Use only for a two-object use-item-
                                // on-target) requires that item to have
                                // resolved to our inventory. If it did not,
                                // this is NOT actionable — fail rather than
                                // silently degrade (e.g. a Use{chest,key}
                                // must not fall back to a plain Use{chest},
                                // which would re-open the same loop).
                                (!goalCarriesItem || itemSnap is not null) &&
                                // Spatial actions require the target
                                // to actually have a CellId. An NPC /
                                // creature without a position cannot
                                // be walked-to.
                                (targetSnap is null ||
                                 (targetSnap.CellId is uint tc && tc != 0u));

                            if (!actionable)
                            {
                                // Autonomous indoor frontier exploration:
                                // the LLM named a target we can't yet see
                                // (e.g. "the Agent in the next room"). Rather
                                // than fail to the picker — which re-locks an
                                // already-visited NPC and loops forever — steer
                                // toward the nearest unexplored reachable cell
                                // so the named target's room loads and it
                                // becomes perceivable. The LLM goal is PRESERVED
                                // (not failed) so its intent persists; on
                                // arrival the selector re-resolves against the
                                // newly-loaded content. Only fires when the
                                // TARGET itself is unresolved (targetSnap null);
                                // a resolved target missing only a Give item is
                                // an inventory problem, not a spatial one.
                                // Geometry-only search; reads no names/wcids/
                                // types/quest state.
                                WorldObjectSnapshot? frontier = null;

                                // cp-2271 (directed hunt-target travel): an LLM
                                // Attack whose named target is out of the bot's
                                // current view (targetSnap null) would otherwise
                                // degrade to a generic geometric frontier wander
                                // (drifting toward whatever unexplored cell is
                                // nearest — e.g. back into town). Before that,
                                // mechanically resolve the LLM's OWN target
                                // selector against the bot's per-bot sighting
                                // memory — the SAME FOV-consumption path the
                                // Explore branch already uses (SightedTargetResolver
                                // + the cross-landblock route prefix) — and steer
                                // toward where the bot last saw a matching entity.
                                // This fulfils the LLM's EXPLICIT Attack request
                                // (the "what" is still goal.Target, matched by
                                // Name/NameContains/Wcid only — no type priority,
                                // no hardcoded names/wcids/landblocks, no autonomous
                                // target choice). No verb is dispatched at the
                                // remembered coords: the approach locks as an inert
                                // frontier probe that PRESERVES the Attack goal, so
                                // when the target re-enters PVS the normal Attack
                                // lock fires. Excludes combatDeferredAttack — a
                                // low-health deferral wants to wander AWAY, not walk
                                // back toward the monster.
                                // !selfCombatSuppressed: combatDeferredAttack is
                                // only set when a LIVE target resolved (targetSnap
                                // not null); for an out-of-PVS target it stays
                                // false, so gate directly on the self-health
                                // suppression state (computed above) too — a bot
                                // recovering below the re-engage threshold must NOT
                                // be steered back toward a monster it can't yet see.
                                if (targetSnap is null && !combatDeferredAttack &&
                                    !selfCombatSuppressed &&
                                    goal.Kind == GoalKind.Attack && goal.Target is not null)
                                {
                                    var nowWallA = DateTime.UtcNow;
                                    var onCooldownA = new HashSet<Guid>();
                                    foreach (var kv in rememberedSightedCooldownUntil)
                                        if (kv.Value > nowWallA) onCooldownA.Add(kv.Key);

                                    // Same-landblock: steer straight to the
                                    // remembered coords.
                                    var rememberedA = SightedTargetResolver.Resolve(
                                        navGraph.SnapshotSighted(), goal.Target, tacticsSelfCell, onCooldownA);
                                    if (rememberedA is not null)
                                    {
                                        var destA = new WorldObjectSnapshot(0u)
                                        {
                                            Name = rememberedA.Name,
                                            CellId = rememberedA.CellId,
                                            Position = rememberedA.Position,
                                        };
                                        bool destResolvedA =
                                            WorldDistance.TrySquaredDistance(tacticsSelf, destA, out var d2memA);
                                        var memStopRadiusA = MotorStopRadius.For(destA);
                                        if (destResolvedA && d2memA <= memStopRadiusA * memStopRadiusA)
                                        {
                                            // Already standing on the remembered
                                            // coords but the live worldState still
                                            // didn't surface the entity (it moved or
                                            // despawned). Cool down + fall through to
                                            // geometric frontier.
                                            rememberedSightedCooldownUntil[rememberedA.Id] =
                                                nowWallA + rememberedSightedRevisitCooldown;
                                            Console.WriteLine(
                                                $"[strategy] LLM-GOAL Attack{{target}} memory hit " +
                                                $"'{rememberedA.Name}' already at bot position but entity " +
                                                $"not in view (moved/despawned); cooling down " +
                                                $"{rememberedSightedRevisitCooldown.TotalSeconds:F0}s");
                                        }
                                        else
                                        {
                                            motionRememberedSightingId = rememberedA.Id;
                                            rememberedSightedCooldownUntil[rememberedA.Id] =
                                                nowWallA + rememberedSightedRevisitCooldown;
                                            frontier = destA;
                                            Console.WriteLine(
                                                $"[strategy] LLM-GOAL Attack{{target}} resolved from MEMORY " +
                                                $"(selector {goal.Target}) -> sighted '{rememberedA.Name}' " +
                                                $"cell=0x{rememberedA.CellId:X8} " +
                                                $"pos=({rememberedA.Position.X:F1},{rememberedA.Position.Y:F1}) " +
                                                $"lastSeen={rememberedA.LastSeenUtc:HH:mm:ss}; steering to remembered " +
                                                $"coords (inert approach, Attack re-locks when target re-enters view)");
                                        }
                                    }

                                    // Cross-landblock: route over the bot's OWN
                                    // explored connectivity toward a sighting in
                                    // another landblock (the live evidence case —
                                    // monster seen in an adjacent landblock).
                                    if (frontier is null)
                                    {
                                        // Bound the advance-node cooldown map the
                                        // same way the Explore branch does so an
                                        // Attack-heavy session that rarely hits
                                        // Explore can't leak entries.
                                        if (crossLbAdvanceCooldownUntil.Count > 256)
                                        {
                                            var expiredAdvA = new List<Guid>();
                                            foreach (var kv in crossLbAdvanceCooldownUntil)
                                                if (kv.Value <= nowWallA) expiredAdvA.Add(kv.Key);
                                            foreach (var k in expiredAdvA)
                                                crossLbAdvanceCooldownUntil.Remove(k);
                                        }
                                        var farA = SightedTargetResolver.ResolveCrossLandblock(
                                            navGraph.SnapshotSighted(), goal.Target, tacticsSelfCell, onCooldownA);
                                        if (farA is not null)
                                        {
                                            var planA = navGraph.PlanWaypointToward(
                                                tacticsSelfCell, tacticsSelf.Position, farA.CellId, farA.Position);
                                            if (planA.Kind == RouteWaypointKind.Advance &&
                                                planA.BoundaryNode is NavNode boundaryA &&
                                                planA.Waypoints.Count > 0 &&
                                                (!crossLbAdvanceCooldownUntil.TryGetValue(boundaryA.Id, out var advCdA) ||
                                                 advCdA <= nowWallA))
                                            {
                                                var routeWaypointsA = new List<IndoorWaypoint>(planA.Waypoints.Count);
                                                foreach (var wp in planA.Waypoints)
                                                    routeWaypointsA.Add(new IndoorWaypoint(
                                                        wp.Position, wp.CellId, WalkableNodeKind.Floor, null));
                                                motionIndoorPath = routeWaypointsA;
                                                motionIndoorPathIndex = 0;
                                                motionIndoorPathCells = planA.PathCells;
                                                motionIndoorPathAttempted = true;
                                                motionRememberedSightingId = farA.Id;
                                                crossLbAdvanceCooldownUntil[boundaryA.Id] =
                                                    nowWallA + crossLbAdvanceCooldown;
                                                frontier = new WorldObjectSnapshot(0u)
                                                {
                                                    Name = farA.Name,
                                                    CellId = boundaryA.CellId,
                                                    Position = boundaryA.Position,
                                                };
                                                Console.WriteLine(
                                                    $"[strategy] LLM-GOAL Attack{{target}} CROSS-LB route advance " +
                                                    $"(selector {goal.Target}) -> '{farA.Name}' in lb " +
                                                    $"0x{(farA.CellId >> 16):X4}; following {routeWaypointsA.Count}-hop " +
                                                    $"route prefix through {planA.PathCells.Count} cells to boundary " +
                                                    $"node 0x{boundaryA.CellId:X8} (inert approach, Attack re-locks " +
                                                    $"when target re-enters view)");
                                            }
                                            else if (planA.Kind == RouteWaypointKind.NoRoute &&
                                                     CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(
                                                         tacticsSelfCell, farA.CellId))
                                            {
                                                // No explored navgraph route to the
                                                // neighbour landblock yet, but both the
                                                // bot and the sighting are OUTDOOR and in
                                                // adjacent landblocks. Outdoors a player
                                                // just heads straight toward a monster
                                                // visible across the seam — the route-prefix
                                                // requirement is an indoor portal/door
                                                // assumption that does not apply here. Steer
                                                // STRAIGHT to the remembered ABSOLUTE coords
                                                // (the motor's yaw is landblock-offset aware
                                                // via WorldHeading.DeltaXY), mirroring the
                                                // same-landblock memory tier above. Reactive
                                                // cliff/stuck detection still guards the walk.
                                                var destFarA = new WorldObjectSnapshot(0u)
                                                {
                                                    Name = farA.Name,
                                                    CellId = farA.CellId,
                                                    Position = farA.Position,
                                                };
                                                bool destFarResolvedA =
                                                    WorldDistance.TrySquaredDistance(
                                                        tacticsSelf, destFarA, out var d2farA);
                                                var farStopRadiusA = MotorStopRadius.For(destFarA);
                                                if (destFarResolvedA &&
                                                    d2farA <= farStopRadiusA * farStopRadiusA)
                                                {
                                                    // Already effectively on the remembered
                                                    // coords but the entity still isn't in
                                                    // view (moved/despawned across the seam);
                                                    // cool down + fall through to geometric
                                                    // frontier rather than spin in place.
                                                    rememberedSightedCooldownUntil[farA.Id] =
                                                        nowWallA + rememberedSightedRevisitCooldown;
                                                    Console.WriteLine(
                                                        $"[strategy] LLM-GOAL Attack{{target}} cross-landblock " +
                                                        $"outdoor memory hit '{farA.Name}' already at bot position " +
                                                        $"but entity not in view (moved/despawned); cooling down " +
                                                        $"{rememberedSightedRevisitCooldown.TotalSeconds:F0}s");
                                                }
                                                else
                                                {
                                                    motionRememberedSightingId = farA.Id;
                                                    rememberedSightedCooldownUntil[farA.Id] =
                                                        nowWallA + rememberedSightedRevisitCooldown;
                                                    frontier = destFarA;
                                                    Console.WriteLine(
                                                        $"[strategy] LLM-GOAL Attack{{target}} '{farA.Name}' is " +
                                                        $"cross-landblock (lb 0x{(farA.CellId >> 16):X4}); {planA.Kind} " +
                                                        $"(no on-foot route prefix) but both cells outdoor + adjacent; " +
                                                        $"steering STRAIGHT to remembered coords across the seam (inert " +
                                                        $"approach, Attack re-locks when target re-enters view)");
                                                }
                                            }
                                            else
                                            {
                                                rememberedSightedCooldownUntil[farA.Id] =
                                                    nowWallA + rememberedSightedRevisitCooldown;
                                                Console.WriteLine(
                                                    $"[strategy] LLM-GOAL Attack{{target}} '{farA.Name}' is " +
                                                    $"cross-landblock (lb 0x{(farA.CellId >> 16):X4}); {planA.Kind} " +
                                                    $"(no on-foot route prefix); cooling down " +
                                                    $"{rememberedSightedRevisitCooldown.TotalSeconds:F0}s");
                                            }
                                        }
                                    }
                                }

                                if (frontier is null && targetSnap is null)
                                    frontier = TryChooseFrontierDest(
                                        tacticsSelfCell, frontierCellCooldownUntil);

                                if (frontier is not null)
                                {
                                    Quaternion frot;
                                    float fyaw;
                                    if (WorldHeading.TryYawToTarget(tacticsSelf, frontier, out fyaw))
                                        frot = WorldHeading.RotationFromYaw(fyaw);
                                    else { fyaw = 0f; frot = tacticsSelf.Rotation; }

                                    motionTarget         = frontier;
                                    motionRememberedDest = frontier;
                                    motionRotation       = frot;
                                    // Inert on arrival (no Talk/Use at an empty
                                    // floor): this is a discovery step, not the
                                    // goal action. The unchanged LLM goal re-
                                    // resolves once the room's content loads.
                                    lockedGoalKind = GoalKind.Explore;
                                    // Preserve the LLM goal across arrival (the
                                    // arrival reset cascade keys off this).
                                    motionIsFrontierProbe = true;
                                    if (WorldDistance.TrySquaredDistance(tacticsSelf, frontier, out var d2fr))
                                        motionInitialDistance = (float)Math.Sqrt(d2fr);

                                    autonomousPositionSent = true;
                                    autonomousPositionPacketIndex = count;

                                    var apPacketSeqF = nextOutboundPacketSequence++;
                                    var apFragSeqF   = nextOutboundFragmentSequence++;
                                    var apBufF = new byte[GameActionAutonomousPositionMessage.PackedSize];
                                    var apLenF = GameActionAutonomousPositionMessage.Pack(
                                        apBufF,
                                        cellId: tacticsSelfCell,
                                        pos:    tacticsSelf.Position,
                                        rot:    frot,
                                        instanceSequence:      tacticsSelf.SeqInstance      ?? 0,
                                        serverControlSequence: tacticsSelf.SeqServerControl ?? 0,
                                        teleportSequence:      tacticsSelf.SeqTeleport      ?? 0,
                                        forcePositionSequence: tacticsSelf.SeqForcePosition ?? 0,
                                        contact: true);
                                    var apMsgF = new OutboundPacket();
                                    if (lastReceivedSeq != 0)
                                        apMsgF.AddAckSequence(lastReceivedSeq);
                                    apMsgF.AddBlobFragment(
                                        fragSequence: apFragSeqF,
                                        fragId: OutboundFragmentId,
                                        queue: (ushort)GameMessageGroup.UIQueue,
                                        gameMessagePayload: apBufF.AsSpan(0, apLenF));
                                    var apSentF = apMsgF.Pack(sendBuf, myClientId,
                                                              sequence: apPacketSeqF, iteration: 1,
                                                              encrypt: true, cryptoSend: cryptoSend);
                                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, apSentF),
                                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);

                                    Console.WriteLine(
                                        $"[strategy] goal {goal.Kind} target '{goal.Target?.Name ?? "-"}' " +
                                        $"not yet visible -> autonomous frontier explore toward unexplored " +
                                        $"cell 0x{(frontier.CellId ?? 0):X8} " +
                                        $"pos=({frontier.Position.X:F1},{frontier.Position.Y:F1}); goal preserved, " +
                                        $"sent AP yaw={fyaw:F3}rad pktSeq={apPacketSeqF} fragSeq={apFragSeqF} bytes={apSentF}");
                                }
                                else
                                {
                                    Console.WriteLine(
                                        $"[strategy] goal {goal.Kind} unresolved -- " +
                                        $"target={(targetSnap is null ? "MISS" : "ok")} " +
                                        $"item={(goalCarriesItem ? (itemSnap is null ? "MISS" : "ok") : "n/a")}; " +
                                        $"selector target={goal.Target} item={goal.Item}; " +
                                        $"clearing and falling through to picker");
                                    tactics.Fail(
                                        combatDeferredAttack
                                            ? "combat deferred: self-health too low to re-engage — recover before attacking"
                                            : "selector resolved to no live object",
                                        eventStream);
                                }
                            }
                            else
                            {
                                float yaw = 0f;
                                Quaternion rot;
                                if (WorldHeading.TryYawToTarget(tacticsSelf, targetSnap!, out yaw))
                                    rot = WorldHeading.RotationFromYaw(yaw);
                                else
                                    rot = tacticsSelf.Rotation;

                                motionTarget   = targetSnap;
                                motionRotation = rot;
                                // Snapshot the goal kind at lock time
                                // so the action-send branch dispatches
                                // the correct verb even if Tactics
                                // re-deliberates during motion.
                                lockedGoalKind = goal.Kind;
                                motionLockedGoalId = goal.Id;
                                motionLockStartedUtc = DateTime.UtcNow;
                                if (goal.Kind == GoalKind.Give)
                                    pendingGiveItemGuid = itemSnap!.Guid;
                                else if (goal.Kind == GoalKind.Use && itemSnap is not null)
                                    // Two-object use: a resolved inventory item
                                    // to be applied to the world target (e.g. a
                                    // key on a locked chest). Dispatched as
                                    // UseWithTarget at the action-send branch.
                                    pendingUseWithItemGuid = itemSnap.Guid;
                                if (WorldDistance.TrySquaredDistance(tacticsSelf, targetSnap!, out var d2lock))
                                    motionInitialDistance = (float)Math.Sqrt(d2lock);

                                autonomousPositionSent = true;
                                autonomousPositionPacketIndex = count;

                                var apPacketSeq = nextOutboundPacketSequence++;
                                var apFragSeq   = nextOutboundFragmentSequence++;
                                var apBuf = new byte[GameActionAutonomousPositionMessage.PackedSize];
                                var apLen = GameActionAutonomousPositionMessage.Pack(
                                    apBuf,
                                    cellId: tacticsSelfCell,
                                    pos:    tacticsSelf.Position,
                                    rot:    rot,
                                    instanceSequence:      tacticsSelf.SeqInstance      ?? 0,
                                    serverControlSequence: tacticsSelf.SeqServerControl ?? 0,
                                    teleportSequence:      tacticsSelf.SeqTeleport      ?? 0,
                                    forcePositionSequence: tacticsSelf.SeqForcePosition ?? 0,
                                    contact: true);

                                var apMsg = new OutboundPacket();
                                if (lastReceivedSeq != 0)
                                    apMsg.AddAckSequence(lastReceivedSeq);
                                apMsg.AddBlobFragment(
                                    fragSequence: apFragSeq,
                                    fragId: OutboundFragmentId,
                                    queue: (ushort)GameMessageGroup.UIQueue,
                                    gameMessagePayload: apBuf.AsSpan(0, apLen));
                                var apSent = apMsg.Pack(sendBuf, myClientId,
                                                        sequence: apPacketSeq, iteration: 1,
                                                        encrypt: true, cryptoSend: cryptoSend);
                                await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, apSent),
                                                           SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);

                                Console.WriteLine(
                                    $"[strategy] LLM-GOAL LOCK kind={goal.Kind} " +
                                    $"target='{targetSnap!.Name}' guid=0x{targetSnap.Guid:X8} " +
                                    $"cell=0x{(targetSnap.CellId ?? 0):X8} " +
                                    $"dist={motionInitialDistance ?? float.NaN:F2}u " +
                                    (goal.Kind == GoalKind.Give
                                        ? $"item='{itemSnap!.Name}' itemGuid=0x{itemSnap.Guid:X8} "
                                        : "") +
                                    $"source={goal.Source} rationale=\"{goal.Rationale}\"; " +
                                    $"SENT(cell=0x{tacticsSelfCell:X8} pos=({tacticsSelf.Position.X:F2},{tacticsSelf.Position.Y:F2},{tacticsSelf.Position.Z:F2})) " +
                                    $"sent AP yaw={yaw:F3}rad pktSeq={apPacketSeq} fragSeq={apFragSeq} bytes={apSent}");
                            }
                        }
                    }
                    else if (goal is not null)
                    {
                        // Diagnostic: goal kinds not covered by either
                        // dispatch branch above (Explore at L2303,
                        // Give/Use/Talk/Attack/Pickup at L2444) fall
                        // through silently. The portal02 spike traced
                        // a fallback step-3 Wield-loop bug to this
                        // gap — without this log it took grep
                        // archeology to identify which kind was being
                        // swallowed. Throttled to once per (kind,
                        // source) pair per session to avoid log spam
                        // when the fallback genuinely cannot make
                        // progress (e.g. step 3 repeat-firing on
                        // dedup-window-aged unwielded gear).
                        var fallthroughKey = $"{goal.Kind}|{goal.Source}";
                        if (loggedFallthroughKinds.Add(fallthroughKey))
                        {
                            var tgtDesc = goal.Target is null
                                ? "-"
                                : ($"guid={(goal.Target.Guid is uint g ? $"0x{g:X8}" : "-")} " +
                                   $"name={(goal.Target.Name ?? "-")}");
                            Console.WriteLine(
                                $"[strategy] goal kind={goal.Kind} unhandled " +
                                $"(no dispatch path - target {tgtDesc}); " +
                                $"source={goal.Source} rationale=\"{goal.Rationale}\" " +
                                $"(further occurrences this session suppressed)");
                        }
                    }
                }

                // M1.6+ — schema-vs-LLM deferral bookkeeping.
                // Update llmInflightSince every tick: capture the
                // first tick we see HasInflight true, clear it when
                // the policy goes idle. The schema-only picker
                // below consults this + MaxLlmDeferralSec to decide
                // whether to defer or to take over as fallback.
                var llmBusyNow = tactics.PolicyHasInflight;
                if (llmBusyNow && llmInflightSince is null)
                    llmInflightSince = DateTime.UtcNow;
                else if (!llmBusyNow)
                    llmInflightSince = null;

                // Phase 5a: send one GameActionAutonomousPosition
                // (0xF753) echoing our current server-asserted
                // position back at the server. First outbound
                // movement-adjacent GameAction. Gating preconditions
                // (in order):
                //   1. LoginComplete already sent (so server clears
                //      Teleporting).
                //   2. At least PostLoginCompleteGracePackets more
                //      inbound packets observed AFTER LoginComplete —
                //      gives the server time to actually process LC
                //      and clear Teleporting. Without this the AP
                //      handler short-circuits on (!Teleporting) and
                //      we see nothing in response.
                //   3. worldState.Self exists AND has a populated
                //      CellId (i.e., we've already received at least
                //      one ObjectCreate or UpdatePosition for our
                //      own guid). Echoing a stale or zeroed position
                //      back at the server is a likely snap-back
                //      trigger.
                // Success criterion: at least one inbound
                // UpdatePosition for our own guid AFTER the send,
                // proving the server's broadcast path fired. See
                // rubber-duck critique in checkpoint 033.
                if (!autonomousPositionSent &&
                    combatTargetGuid is null &&
                    actionsCompleted < MaxActionsPerSession &&
                    loginCompleteSent &&
                    loginCompletePacketIndex >= 0 &&
                    (count - loginCompletePacketIndex) >= PostLoginCompleteGracePackets &&
                    worldState.Self is WorldObjectSnapshot self &&
                    self.CellId is uint selfCell &&
                    selfCell != 0 &&
                    // M1.6+ — defer to the LLM when it is mid-call.
                    // Without this gate the schema-only picker
                    // grabs the nearest-named NPC at packet rate
                    // (~50ms) and locks motionTarget before the
                    // LLM's ~1s response can land, so the LLM
                    // result is never actuated (the LLM block's
                    // gate requires motionTarget==null). Bound by
                    // MaxLlmDeferralSec so a stuck network call
                    // doesn't park the bot indefinitely.
                    (llmInflightSince is not DateTime inflightSince ||
                     (DateTime.UtcNow - inflightSince).TotalSeconds > MaxLlmDeferralSec))
                {
                    autonomousPositionSent = true;
                    autonomousPositionPacketIndex = count;
                    var packetSeq = nextOutboundPacketSequence++;
                    var fragSeq   = nextOutboundFragmentSequence++;

                    // Phase 6c — pick the motion target.
                    // Override:    HEADLESS_MOTION_TARGET_NAME=<name>  (exact match, case-sensitive)
                    // Default:     nearest named (Name != null && != "") non-self snapshot
                    //              within MotionSearchRadius.
                    // Phase 6g: exclude guids we've already targeted this session.
                    // Phase 6i: exclude other players (their guids start with
                    //          0x5xxxxxxx — see ObjectGuid.Player range). USE'ing
                    //          another player accomplishes nothing useful and
                    //          can deadlock the loop on a stationary peer bot.
                    // De-hardcoding pass: door/portal classification is now
                    //          done from ObjectDescriptionFlag bits, not from
                    //          `Name == "Door"`. This must work on localized
                    //          servers and custom door names — the picker
                    //          must NEVER hold game-content English strings.
                    var apRot = self.Rotation;
                    var inRange = worldState.WithinRadius(self, MotionSearchRadius)
                        .Where(s => s.Guid != self.Guid && !string.IsNullOrEmpty(s.Name))
                        .Where(s => !visitedTargetGuids.Contains(s.Guid))
                        // Phase 7f.D — skip a threat still on the low-health
                        // disengage avoid cooldown (or for as long as we remain
                        // too hurt to engage) so the picker does not re-walk the
                        // bot back into the mob it just fled.
                        .Where(s => !(combatAvoidUntil.TryGetValue(s.Guid, out var cau) &&
                                      (DateTime.UtcNow < cau || selfCombatSuppressed)))
                        .Where(s => (s.Guid & 0xFF000000u) != 0x50000000u)
                        // Phase 6n — anti-starvation: skip duplicate
                        // quest reward respawns. If we've already
                        // wielded a wearable from this weenie class
                        // (same wcid), don't chase the next copy. If a
                        // non-wearable name has been picked up >=1x,
                        // deprioritize it (handled in sort below; the
                        // hard filter below only drops EXACT duplicates
                        // of a satisfied weenie class).
                        .Where(s => !(s.WeenieClassId is uint wcSat && satisfiedWeenieClasses.Contains(wcSat)))
                        .ToList();

                    WorldObjectSnapshot? candidate = null;
                    // Slice V (#86): track which picker chose, so we
                    // can publish the autonomous activity to the LLM
                    // prompt. Stays null if the LLM-driven paths
                    // (combat lock, name override) take this tick —
                    // those aren't autonomous picker activity.
                    string? pickerSourceForActivity = null;
                    string? pickerReasonForActivity = null;
                    // Phase 7f — combat lock. While we have an active
                    // melee target, refuse to pick anything else. If
                    // the target is still visible, target it again
                    // (so the picker keeps driving combat-tick re-
                    // sends). If not, the lock will be cleared by the
                    // disappearance check below before the next AP
                    // tick.
                    if (combatTargetGuid is uint ctg)
                    {
                        candidate = inRange.FirstOrDefault(s => s.Guid == ctg);
                        if (candidate is not null)
                        {
                            Console.WriteLine(
                                $"[motion] COMBAT LOCK -> existing target guid=0x{ctg:X8}");
                        }
                    }
                    if (candidate is null && !string.IsNullOrWhiteSpace(motionTargetNameOverride))
                    {
                        candidate = inRange.FirstOrDefault(s =>
                            string.Equals(s.Name, motionTargetNameOverride, StringComparison.Ordinal));
                    }
                    else if (candidate is null)
                    {
                        // Slice W.1 (#86) — schema-only picker. Replaces
                        // the type-based priority ladder (NPC > corpse >
                        // door > pickup > else) that the audit at
                        // .github/skills/audit-hardcoded-knowledge/SKILL.md
                        // flagged as hardcoded game knowledge. The picker
                        // now selects the nearest mechanically-eligible
                        // candidate; the LLM steers via the Slice V
                        // "## Autonomous picker activity" prompt block.
                        //
                        // Upstream filters already applied in `inRange`:
                        //   - within MotionSearchRadius
                        //   - not self, non-empty Name
                        //   - not visited (per-GUID)
                        //   - not a player (0x5xxxxxxx GUID range)
                        //   - not in satisfiedWeenieClasses
                        //
                        // Additional MECHANICAL filters in PickerSelection
                        // (loop-prevention only, NOT priority):
                        //   - drop ContainerGuid==self (item in our bag)
                        //   - drop pickup-eligible items whose Name has
                        //     already been picked once (anti-respawn)
                        //
                        // No type-based bumps. No corpse-loot bump. No
                        // door preference. No wearable preference. Those
                        // are strategic — owned by the LLM.
                        candidate = PickerSelection.PickNearest(
                            inRange,
                            self,
                            chosenCharacterGuid,
                            pickupCountByName,
                            PickupItemTypeMask);
                        if (candidate is not null)
                        {
                            pickerSourceForActivity = "in-range";
                            pickerReasonForActivity = "schema-only picker (nearest mechanically-eligible candidate)";
                        }
                    }

                    // Slice W.2 (ac-ai-players#87) — EXPLORATION
                    // FALLBACK. When the in-range queue is empty,
                    // pick the nearest mechanically-eligible known
                    // object in the bot's CURRENT LANDBLOCK
                    // (addressable in one motion). Pure-distance —
                    // no NPC>corpse>door>pickup ladder, no visited-
                    // door backtrack preference. The OLD fallback
                    // encoded those as game knowledge; the audit
                    // at .github/skills/audit-hardcoded-knowledge/
                    // SKILL.md flagged the entire ladder + the
                    // "backtrack via visited door" strategy as
                    // FORBIDDEN. They're now the LLM's call,
                    // surfaced via the "## Exploration candidates"
                    // prompt block.
                    //
                    // Mechanical filters (in PickerSelection.
                    // PickNearestFallback):
                    //   - drop self / empty-name / player guids
                    //   - drop visited GUIDs (LLM can request
                    //     backtrack via Explore{target=visited})
                    //   - drop satisfied weenie classes
                    //   - drop ContainerGuid==self / WielderGuid==self
                    //   - drop pickup respawns whose Name has been
                    //     picked once (anti-respawn)
                    //   - drop objects with no CellId
                    //   - drop objects in a different landblock
                    //     (addressability — the bot can't walk
                    //     directly into another landblock without
                    //     a cell hand-off; chasing a 300u
                    //     remembered object across a building
                    //     wall just wedges against geometry).
                    //
                    // The LLM sees the same candidate set via the
                    // "## Exploration candidates" prompt block
                    // (top N, sorted nearest-first) and can
                    // override the picker's nearest pick by
                    // emitting Explore{target=<guid|name>}.
                    IReadOnlyList<ExplorationCandidate>? explorationCandidatesForLlm = null;
                    if (candidate is null &&
                        string.IsNullOrWhiteSpace(motionTargetNameOverride) &&
                        combatTargetGuid is null)
                    {
                        var selfLandblock = (self.CellId ?? 0u) & 0xFFFF0000u;

                        // Pre-filter for the picker (mirrors the
                        // inRange pre-filters that aren't already
                        // inside PickerSelection): visited / wcid-
                        // satisfied / player guids. Keeps the
                        // PickerSelection method generic.
                        var fallbackPool = worldState.Objects.Values
                            .Where(s => (s.Guid & 0xFF000000u) != 0x50000000u)
                            .Where(s => !visitedTargetGuids.Contains(s.Guid))
                            // Phase 7f.D — honor the low-health disengage
                            // avoid cooldown in the fallback pool too (and keep
                            // avoiding the fled threat while still suppressed).
                            .Where(s => !(combatAvoidUntil.TryGetValue(s.Guid, out var cauF) &&
                                          (DateTime.UtcNow < cauF || selfCombatSuppressed)))
                            .Where(s => !(s.WeenieClassId is uint wcSat && satisfiedWeenieClasses.Contains(wcSat)))
                            .ToList();

                        var ranked = PickerSelection.EnumerateFallbackCandidates(
                            fallbackPool,
                            self,
                            chosenCharacterGuid,
                            selfLandblock,
                            pickupCountByName,
                            PickupItemTypeMask).ToList();

                        // gate-nearest-named-picker-fallback: BEFORE the
                        // autonomous nearest-known-object pick (the picker
                        // deciding ON ITS OWN to walk the bot to a concrete
                        // object the LLM never named — the AGENTS.md-forbidden
                        // autonomous interaction), prefer a geometry-only
                        // OUTDOOR FRONTIER probe: steer toward unexplored ground
                        // so new objects (and whatever lives there) become
                        // perceivable, WITHOUT selecting any object or verb.
                        // This is the surface analogue of the Explore-GOAL
                        // frontier actuation; it runs only in this aimless
                        // else-if(!llmBusyNow) branch — mutually exclusive with
                        // the goal-handling branch that drives the Explore-goal
                        // frontier — so it cannot double-drive motion.
                        // Domain-general spatial search: reads only navmesh
                        // geometry + the bot's OWN visited cells (huntBias OFF —
                        // no remembered-sighting bias without an LLM hunt). The
                        // synthetic destination is locked as an INERT Explore
                        // motion target (lockedGoalKind=Explore => arrival sends
                        // NO opcode, only re-perception — see the Slice L
                        // short-circuit) and is NOT published as picker activity
                        // (pickerSourceForActivity stays null). The
                        // nearest-known-object pick remains ONLY as a last resort
                        // when no frontier exists (indoors / cells exhausted), so
                        // the bot never goes inert.
                        var aimlessFrontier = TryChooseOutdoorFrontierDest(
                            self.CellId ?? 0u, self.Position, navGraph,
                            frontierCellCooldownUntil, huntBiasAuthorized: false,
                            out var aimlessFrontierCells);
                        if (aimlessFrontier is not null)
                        {
                            candidate = aimlessFrontier;
                            motionRememberedDest = aimlessFrontier;
                            motionIndoorPathCells = aimlessFrontierCells;
                            motionIndoorPathAttempted = true;
                            motionIsOutdoorFrontierProbe = true;
                            lockedGoalKind = GoalKind.Explore;
                            motionLockedGoalId = null;
                            Console.WriteLine(
                                $"[motion] AIMLESS FRONTIER — no LLM goal + no candidate in " +
                                $"{MotionSearchRadius}u; geometry-only outdoor frontier probe to " +
                                $"cell 0x{(aimlessFrontier.CellId ?? 0):X8} " +
                                $"pos=({aimlessFrontier.Position.X:F1},{aimlessFrontier.Position.Y:F1}) " +
                                $"via {aimlessFrontierCells?.Count ?? 0} cells (no object selected)");
                        }
                        else if (ranked.Count > 0)
                        {
                            candidate = ranked[0].snap;
                            pickerSourceForActivity = "fallback";
                            pickerReasonForActivity = "no candidates in radius; mechanical nearest known object in current landblock";
                            Console.WriteLine(
                                $"[motion] EXPLORATION FALLBACK — no candidates in {MotionSearchRadius}u; " +
                                $"mechanical nearest in landblock 0x{selfLandblock:X8}: " +
                                $"guid=0x{candidate.Guid:X8} name='{candidate.Name}' dist={ranked[0].distance:F2}u " +
                                $"(of {ranked.Count} candidates)");
                        }

                        // Surface candidate set to the LLM (top
                        // 10, including the picked one). Empty
                        // list = the LLM block won't render. We
                        // also surface candidates when the picker
                        // found one because the LLM may want to
                        // pick a DIFFERENT one (the picker's pick
                        // is by mechanical distance only).
                        if (ranked.Count > 0)
                        {
                            const int MaxCandidatesForLlm = 10;
                            var top = ranked.Take(MaxCandidatesForLlm).Select(t => new ExplorationCandidate
                            {
                                Guid     = t.snap.Guid,
                                Name     = t.snap.Name ?? string.Empty,
                                Distance = t.distance,
                                CellId   = t.snap.CellId ?? 0u,
                                Visited  = visitedTargetGuids.Contains(t.snap.Guid),
                                // Same wire-derived classification used for
                                // visible objects + sighting memory. Lets the
                                // LLM tell a creature candidate from inert
                                // scenery; no priority assigned here.
                                Kind     = EntityClassifier.ClassifySighting(
                                    t.snap.ItemType ?? 0u,
                                    t.snap.ObjectDescriptionFlags ?? 0u,
                                    t.snap.WeenieFlags ?? 0u),
                            }).ToList();
                            explorationCandidatesForLlm = top;
                        }
                    }
                    // Publish (or clear) the candidate surface every
                    // tick — stale lists are worse than empty ones
                    // because the LLM may emit Explore{target} for
                    // a candidate that no longer applies.
                    llmPolicyForPickerSurface?.SetCurrentExplorationCandidates(explorationCandidatesForLlm);

                    // Publish the bot's own remembered MONSTER sightings
                    // (out-of-view recall) so the LLM can choose to return
                    // to a monster that left its field of view. Project the
                    // Mob-kind SightedLocation memory into the small recall
                    // DTO, bounded to the most-recent ones; the prompt
                    // builder does visible-exclusion, dedup, TTL and cap.
                    // Mob-only here (not Mob+NPC): the builder renders only
                    // Mob, so including NPCs would let an NPC-dense town fill
                    // the bounded projection and starve out the monsters the
                    // recall is for. Pure perception.
                    if (llmPolicyForPickerSurface is not null)
                    {
                        const int MaxRecallSightings = 40;
                        var nowRecall = DateTimeOffset.UtcNow;
                        var recall = navGraph.SnapshotSighted()
                            .Where(s => s.Kind == EntityKind.Mob)
                            .OrderByDescending(s => s.LastSeenUtc)
                            .Take(MaxRecallSightings)
                            .Select(s => new SightedRecallProjection
                            {
                                Name       = s.Name,
                                Wcid       = s.Wcid,
                                Kind       = s.Kind,
                                Landblock  = s.Landblock,
                                WorldX     = s.WorldX,
                                WorldY     = s.WorldY,
                                AgeSeconds = Math.Max(0.0, (nowRecall - s.LastSeenUtc).TotalSeconds),
                            })
                            .ToList();
                        llmPolicyForPickerSurface.SetRecentSightings(recall);
                    }

                    // Phase C (picker-hunt-suppress) — while an LLM/operator
                    // HUNT commitment is active on the IntentStack, the
                    // autonomous picker must NOT walk the bot to the nearest
                    // inert object: doing so captures the bot away from the
                    // hunt (live-fire: the bot kept re-Using a town Well /
                    // Collector instead of leaving Holtburg to find monsters).
                    // Drop ONLY an autonomously-picked candidate (the in-range
                    // nearest pick or the landblock fallback — both set
                    // pickerSourceForActivity); NEVER a combat lock or an
                    // explicit LLM name override (both leave it null). Motion
                    // during the hunt comes from the outdoor frontier (the
                    // Explore actuation above); the AP keepalive below still
                    // fires every tick (candidate==null -> "no lock" AP), and
                    // the suppression self-lifts when the hunt intent
                    // completes (a monster enters view -> visible_tag:monster)
                    // or its deadline elapses (CheckTopForCompletion pops it
                    // before this point). No source-side target priority is
                    // introduced — the WHAT stays with the LLM.
                    if (candidate is not null &&
                        pickerSourceForActivity is not null &&
                        Strategy.Intent.HuntAuthorization.IsActiveHunt(intentStack.Top))
                    {
                        Console.WriteLine(
                            $"[motion] HUNT-ACTIVE picker suppression — dropping autonomous " +
                            $"{pickerSourceForActivity} pick guid=0x{candidate.Guid:X8} " +
                            $"name='{candidate.Name}' (active hunt intent '{intentStack.Top!.Kind}'); " +
                            $"deferring motion to the outdoor frontier");
                        candidate = null;
                        pickerSourceForActivity = null;
                        pickerReasonForActivity = null;
                    }

                    // Slice V (#86) — publish autonomous picker
                    // activity to the LLM prompt. The picker still
                    // dispatches the action mechanically; this just
                    // surfaces what it's doing so the LLM can see
                    // and override. See PickerActivity.cs for why
                    // we use a parallel surface instead of pushing
                    // synthetic Intents.
                    if (candidate is not null && pickerSourceForActivity is not null)
                    {
                        var prev = pickerActivityCurrent;
                        if (prev is null || prev.TargetGuid != candidate.Guid)
                        {
                            if (prev is not null)
                            {
                                eventStream.Append(new StreamEvent
                                {
                                    Sequence = 0,
                                    Utc      = DateTimeOffset.UtcNow,
                                    Kind     = EventKind.PickerActivityCompleted,
                                    ItemGuid = prev.TargetGuid,
                                    Name     = prev.TargetName,
                                });
                            }
                            pickerActivityCurrent = new PickerActivity
                            {
                                TargetGuid   = candidate.Guid,
                                TargetName   = candidate.Name ?? string.Empty,
                                Source       = pickerSourceForActivity,
                                Reason       = pickerReasonForActivity ?? "",
                                StartedAtUtc = DateTimeOffset.UtcNow,
                            };
                            eventStream.Append(new StreamEvent
                            {
                                Sequence = 0,
                                Utc      = DateTimeOffset.UtcNow,
                                Kind     = EventKind.PickerActivityStarted,
                                ItemGuid = candidate.Guid,
                                Name     = candidate.Name,
                                Text     = $"{pickerSourceForActivity}: {pickerReasonForActivity}",
                            });
                            llmPolicyForPickerSurface?.SetCurrentPickerActivity(pickerActivityCurrent);
                        }
                    }
                    else if (candidate is null && pickerActivityCurrent is not null)
                    {
                        eventStream.Append(new StreamEvent
                        {
                            Sequence = 0,
                            Utc      = DateTimeOffset.UtcNow,
                            Kind     = EventKind.PickerActivityCompleted,
                            ItemGuid = pickerActivityCurrent.TargetGuid,
                            Name     = pickerActivityCurrent.TargetName,
                        });
                        pickerActivityCurrent = null;
                        llmPolicyForPickerSurface?.SetCurrentPickerActivity(null);
                    }

                    // Log all candidates at AP time for visibility (capped to 8 so
                    // the spike output stays readable in dense rooms).
                    Console.WriteLine($"[motion] candidates inRange={inRange.Count} (showing up to 8):");
                    foreach (var (snap, d2) in inRange
                                 .Select(s =>
                                 {
                                     WorldDistance.TrySquaredDistance(self, s, out var d2);
                                     return (s, d2);
                                 })
                                 .OrderBy(t => t.d2)
                                 .Take(8))
                    {
                        var d = (float)Math.Sqrt(d2);
                        var marker = candidate is not null && snap.Guid == candidate.Guid ? " <-- PICKED" : "";
                        Console.WriteLine(
                            $"[motion]   guid=0x{snap.Guid:X8} name='{snap.Name}' " +
                            $"itemType=0x{snap.ItemType ?? 0:X} dist={d:F2}u{marker}");
                    }

                    if (candidate is not null &&
                        WorldHeading.TryYawToTarget(self, candidate, out var targetYaw))
                    {
                        apRot = WorldHeading.RotationFromYaw(targetYaw);
                        motionTarget = candidate;
                        motionRotation = apRot;
                        if (WorldDistance.TrySquaredDistance(self, candidate, out var d2lock))
                            motionInitialDistance = (float)Math.Sqrt(d2lock);
                        Console.WriteLine(
                            $"[motion] LOCK target guid=0x{candidate.Guid:X8} name='{candidate.Name}' " +
                            $"cell=0x{(candidate.CellId ?? 0):X8} dist={motionInitialDistance ?? float.NaN:F2}u " +
                            $"yaw={targetYaw:F3}rad (rot=({apRot.X:F3},{apRot.Y:F3},{apRot.Z:F3},{apRot.W:F3})) " +
                            $"source={(string.IsNullOrWhiteSpace(motionTargetNameOverride) ? "nearest-named" : $"override='{motionTargetNameOverride}'")}");
                    }
                    else
                    {
                        var why = string.IsNullOrWhiteSpace(motionTargetNameOverride)
                            ? $"no named snapshots within {MotionSearchRadius}u"
                            : $"override target name '{motionTargetNameOverride}' not within {MotionSearchRadius}u";
                        Console.WriteLine(
                            $"[motion] no lock — {why}. AP with unchanged rotation.");
                    }

                    var apBuf = new byte[GameActionAutonomousPositionMessage.PackedSize];
                    var apLen = GameActionAutonomousPositionMessage.Pack(
                        apBuf,
                        cellId: selfCell,
                        pos:    self.Position,
                        rot:    apRot,
                        instanceSequence:      self.SeqInstance       ?? 0,
                        serverControlSequence: self.SeqServerControl ?? 0,
                        teleportSequence:      self.SeqTeleport      ?? 0,
                        forcePositionSequence: self.SeqForcePosition ?? 0,
                        contact: true);

                    var msg = new OutboundPacket();
                    if (lastReceivedSeq != 0)
                        msg.AddAckSequence(lastReceivedSeq);
                    msg.AddBlobFragment(
                        fragSequence: fragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: apBuf.AsSpan(0, apLen));

                    var sentLen = msg.Pack(sendBuf, myClientId,
                                           sequence: packetSeq, iteration: 1,
                                           encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, sentLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    Console.WriteLine(
                        $"[observe]   -> PHASE5A SEND: GameActionAutonomousPosition " +
                        $"cell=0x{selfCell:X8} xyz=({self.Position.X:F2},{self.Position.Y:F2},{self.Position.Z:F2}) " +
                        $"seq=(inst={self.SeqInstance},srvCtrl={self.SeqServerControl}," +
                        $"tele={self.SeqTeleport},forcePos={self.SeqForcePosition}) " +
                        $"contact=true payload={apLen}B pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");
                    Console.WriteLine($"[observe]      Expect: inbound UpdatePosition for our own guid 0x{(worldState.SelfGuid ?? 0):X8} (server's broadcast echo).");
                }

                // Phase 5b — MoveToState START. Tell the server we're
                // running forward. Wait an additional grace window
                // after AutonomousPosition so the server has applied
                // the position update before we layer motion on top.
                //
                // Phase 7e: switched from WalkForward+HoldKey.None to
                // RunForward+HoldKey.Run for ~2x locomotion speed.
                // Predicted self-position step (WalkSpeedUnitsPerSec)
                // bumped from 2.5 to 5.0 to match. Player.OnMoveToState
                // short-circuits on !FastTick (true for our NPK headless
                // char), so we do NOT expect a continuous UpdatePosition
                // stream — only a Motion broadcast back at us. That
                // broadcast alone proves the wire format is accepted.
                if (autonomousPositionSent &&
                    !moveToStateStartSent &&
                    autonomousPositionPacketIndex >= 0 &&
                    autonomousPositionGraceStartUtc is null)
                    autonomousPositionGraceStartUtc = DateTime.UtcNow;
                var apGraceElapsedMs = autonomousPositionGraceStartUtc is DateTime apGraceTs
                    ? (DateTime.UtcNow - apGraceTs).TotalMilliseconds
                    : 0;
                if (autonomousPositionSent &&
                    !moveToStateStartSent &&
                    autonomousPositionPacketIndex >= 0 &&
                    ((count - autonomousPositionPacketIndex) >= PostAutonomousPositionGracePackets ||
                     apGraceElapsedMs >= PostAutonomousPositionGraceMaxMs) &&
                    worldState.Self is WorldObjectSnapshot moveSelf &&
                    moveSelf.CellId is uint moveCell &&
                    moveCell != 0)
                {
                    moveToStateStartSent = true;
                    // Phase 6b — capture motion-start state so walk-tick
                    // can begin stepping. nextWalkTickAt is set NOW so
                    // the first walk-tick AP fires on the very next
                    // loop iteration (no extra grace window — we already
                    // waited for AP-grace, which is enough for the
                    // server to consume the initial AP).
                    //
                    // Outdoor cell-consistency: the server-reported moveCell
                    // can be a stale cell that no longer matches the bot's
                    // current coordinates (e.g. it froze during a prior
                    // motion). Re-derive the cell from moveSelf.Position's
                    // GLOBAL coordinates so the START (cell, pos) — and the
                    // motion lock seeded from it — are internally consistent.
                    // Indoor cells are client-authoritative (gated out).
                    var startCell = moveCell;
                    var startPos = moveSelf.Position;
                    if (!Strategy.AcCoords.IsIndoor(moveCell))
                    {
                        var (startGX, startGY) = Strategy.AcCoords.ToGlobalXY(moveCell, moveSelf.Position);
                        var startSeam = Strategy.OutdoorSeamCell.TryDeriveSeamCell(
                            followingIndoorPath: false,
                            selfCellIsOutdoor:   true,
                            lockedCellId:        moveCell,
                            stepGlobalX:         startGX,
                            stepGlobalY:         startGY,
                            stepZ:               moveSelf.Position.Z);
                        if (startSeam is { } startSc)
                        {
                            Console.WriteLine(
                                $"[motion] PHASE5B START: outdoor cell-consistency override " +
                                $"0x{moveCell:X8} -> 0x{startSc.CellId:X8} " +
                                $"global=({startGX:F1},{startGY:F1}) local=({startSc.LocalPos.X:F1},{startSc.LocalPos.Y:F1})");
                            startCell = startSc.CellId;
                            startPos = startSc.LocalPos;
                        }
                    }
                    motionStartedAt = DateTime.UtcNow;
                    motionLockedCellId = startCell;
                    nextWalkTickAt = DateTime.UtcNow.AddMilliseconds(WalkTickIntervalMs);
                    var packetSeq = nextOutboundPacketSequence++;
                    var fragSeq   = nextOutboundFragmentSequence++;

                    var motion = RawMotionStatePayload.ForwardMotion(
                        holdKey: HoldKey.Run,
                        stance:  MotionStance.NonCombat,
                        command: MotionCommand.RunForward,
                        speed:   1.0f);

                    // Phase 6 — use the locked rotation if we picked
                    // a target; otherwise the snapshot's current
                    // rotation. The server may not have echoed our
                    // AP-rotation update before we send START, so
                    // we deliberately do NOT trust moveSelf.Rotation
                    // for the rot field when we have an intent.
                    var moveRot = motionRotation ?? moveSelf.Rotation;

                    var msBuf = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
                    var msLen = GameActionMoveToStateMessage.Pack(
                        msBuf,
                        motion,
                        cellId: startCell,
                        pos:    startPos,
                        rot:    moveRot,
                        instanceSequence:      moveSelf.SeqInstance      ?? 0,
                        serverControlSequence: moveSelf.SeqServerControl ?? 0,
                        teleportSequence:      moveSelf.SeqTeleport      ?? 0,
                        forcePositionSequence: moveSelf.SeqForcePosition ?? 0,
                        contact: true);

                    var msg = new OutboundPacket();
                    if (lastReceivedSeq != 0)
                        msg.AddAckSequence(lastReceivedSeq);
                    msg.AddBlobFragment(
                        fragSequence: fragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: msBuf.AsSpan(0, msLen));

                    var sentLen = msg.Pack(sendBuf, myClientId,
                                           sequence: packetSeq, iteration: 1,
                                           encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, sentLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    Console.WriteLine(
                        $"[observe]   -> PHASE5B START: GameActionMoveToState " +
                        $"flags=0x{(uint)motion.Flags:X3} (CurrentHoldKey|CurrentStyle|ForwardCommand|ForwardSpeed) " +
                        $"holdKey=Run stance=NonCombat cmd=RunForward speed=1.0 " +
                        $"cell=0x{startCell:X8} xyz=({startPos.X:F2},{startPos.Y:F2},{startPos.Z:F2}) " +
                        $"rot=({moveRot.X:F3},{moveRot.Y:F3},{moveRot.Z:F3},{moveRot.W:F3}) " +
                        $"rotSource={(motionRotation is null ? "self" : "target-lock")} " +
                        $"payload={msLen}B pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");
                    Console.WriteLine($"[observe]      Expect: inbound Motion (0xF74C) for our own guid 0x{(worldState.SelfGuid ?? 0):X8}.");
                }

                // Phase 5b — MoveToState STOP. Cancel the forward
                // motion intent. Per rubber-duck: a real client would
                // send a stop on key-release; otherwise the server's
                // recorded intent keeps drifting and the next
                // movement command may be misinterpreted.
                //
                // Phase 6b: stop fires when ANY of:
                //   (a) walk-tick set motionDone (target reached,
                //       cell-crossing detected, target lost, wall-
                //       clock timeout).
                //   (b) observed distance to target dropped below
                //       the target-aware stop radius (race
                //       condition: walk-tick hasn't picked this up
                //       yet). MotorStopRadius.For bumps the radius
                //       for IsPortal targets — see that file's
                //       header for the audit framing.
                //   (c) wall-clock since motion started > timeout
                //       (defensive; walk-tick will normally beat us
                //       here but doesn't fire if no packets arrive).
                // STOP pos uses lastSentWaypointPos (our most recent
                // walked-to intent) rather than stopSelf.Position
                // because GameActionMoveToState.Handle also calls
                // SetRequestedLocation with the packet's pos, and we
                // don't want STOP to overwrite the walk-tick's last
                // forward step with a stale snapshot.
                var stopByDistance = false;
                float stopByDistanceCurr = float.NaN;
                if (motionTarget is not null &&
                    worldState.Self is WorldObjectSnapshot stopProbe &&
                    WorldDistance.TrySquaredDistance(stopProbe, motionTarget, out var d2curr))
                {
                    var stopRadius = MotorStopRadius.For(motionTarget);
                    if (d2curr <= stopRadius * stopRadius)
                    {
                        stopByDistance = true;
                        stopByDistanceCurr = (float)Math.Sqrt(d2curr);
                    }
                }
                var stopByTimeout = motionStartedAt is DateTime stopStartTs &&
                                    Strategy.MotionTimeout.IsExpired(
                                        stopStartTs, DateTime.UtcNow,
                                        hasProductiveLock:
                                            motionTarget is not null ||
                                            motionRememberedDest is not null ||
                                            motionIndoorPath is not null ||
                                            motionIndoorPathCells is not null,
                                        lockedSec: MotionWallClockTimeoutSec,
                                        noLockSec: MotionNoLockTimeoutSec);
                if (moveToStateStartSent &&
                    !moveToStateStopSent &&
                    (motionDone || stopByDistance || stopByTimeout) &&
                    worldState.Self is WorldObjectSnapshot stopSelf &&
                    stopSelf.CellId is uint stopCell &&
                    stopCell != 0)
                {
                    moveToStateStopSent = true;
                    motionStoppedAt = DateTime.UtcNow;
                    var packetSeq = nextOutboundPacketSequence++;
                    var fragSeq   = nextOutboundFragmentSequence++;

                    var motion = RawMotionStatePayload.Stop(MotionStance.NonCombat);

                    // Prefer our most recent walked-to intent over the
                    // (possibly stale) self snapshot for STOP's pos.
                    var stopPos = lastSentWaypointPos ?? stopSelf.Position;

                    // Outdoor cell-consistency: the (cell, pos) pair STOP
                    // asserts must be internally consistent, or the server's
                    // in-combat StickToObject move errors out and cancels every
                    // melee swing (0 damage, death). Two independent hazards:
                    //   1. the server-reported stopCell can lag the cell our
                    //      walked-to coordinates fall in (the cell-claim froze
                    //      during the approach); and
                    //   2. our cached stop LOCAL pos (lastSentWaypointPos =
                    //      newPos) is expressed in the walk-tick's landblock
                    //      frame, which the server may have since slid away from
                    //      after a seam crossing — pairing it with stopCell
                    //      mis-projects by a full landblock (~192 m).
                    // Both are eliminated by deriving the canonical (cell, pos)
                    // from the position's TRUE GLOBAL coordinates and emitting
                    // THAT pair UNCONDITIONALLY (not only when the cell changed).
                    // Indoor cells are client-authoritative and handled by the
                    // indoor path logic, so this only applies outdoors.
                    //
                    // GLOBAL source: when the stop pos came from a walk-tick
                    // waypoint we use its frame-free captured global XY
                    // (lastSentWaypointGlobalXY), which is correct regardless of
                    // any seam frame slide; otherwise the pos is the stopSelf
                    // snapshot, atomically consistent with stopCell, so
                    // ToGlobalXY(stopCell, stopPos) is the right frame.
                    uint stopSendCell = stopCell;
                    var stopSendPos = stopPos;
                    if (!Strategy.AcCoords.IsIndoor(stopCell))
                    {
                        var (stopGX, stopGY) = lastSentWaypointGlobalXY is { } wg
                            ? (wg.X, wg.Y)
                            : Strategy.AcCoords.ToGlobalXY(stopCell, stopPos);
                        var stopCanon = Strategy.OutdoorSeamCell.Canonicalize(stopGX, stopGY, stopPos.Z);
                        if (stopCanon is { } stopSc)
                        {
                            stopSendCell = stopSc.CellId;
                            stopSendPos = stopSc.LocalPos;
                            if (stopSc.CellId != stopCell)
                                Console.WriteLine(
                                    $"[motion] PHASE5B STOP: outdoor cell-consistency override " +
                                    $"0x{stopCell:X8} -> 0x{stopSc.CellId:X8} " +
                                    $"global=({stopGX:F1},{stopGY:F1}) local=({stopSc.LocalPos.X:F1},{stopSc.LocalPos.Y:F1})");
                        }
                    }

                    var msBuf = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
                    var msLen = GameActionMoveToStateMessage.Pack(
                        msBuf,
                        motion,
                        cellId: stopSendCell,
                        pos:    stopSendPos,
                        rot:    stopSelf.Rotation,
                        instanceSequence:      stopSelf.SeqInstance      ?? 0,
                        serverControlSequence: stopSelf.SeqServerControl ?? 0,
                        teleportSequence:      stopSelf.SeqTeleport      ?? 0,
                        forcePositionSequence: stopSelf.SeqForcePosition ?? 0,
                        contact: true);

                    var msg = new OutboundPacket();
                    if (lastReceivedSeq != 0)
                        msg.AddAckSequence(lastReceivedSeq);
                    msg.AddBlobFragment(
                        fragSequence: fragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: msBuf.AsSpan(0, msLen));

                    var sentLen = msg.Pack(sendBuf, myClientId,
                                           sequence: packetSeq, iteration: 1,
                                           encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, sentLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    var trigger = motionDone
                        ? "walk-done"
                        : (stopByDistance ? $"distance({stopByDistanceCurr:F2}u)" : "wall-clock-timeout");
                    Console.WriteLine(
                        $"[observe]   -> PHASE5B STOP: GameActionMoveToState " +
                        $"flags=0x{(uint)motion.Flags:X3} (CurrentHoldKey|CurrentStyle|ForwardCommand) " +
                        $"cmd=Invalid (stop) " +
                        $"cell=0x{stopSendCell:X8} stopPos=({stopSendPos.X:F2},{stopSendPos.Y:F2},{stopSendPos.Z:F2}) " +
                        $"posSource={(lastSentWaypointPos is null ? "self-snap" : "last-waypoint")} " +
                        $"trigger={trigger} " +
                        $"payload={msLen}B pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");

                    // Arrival-by-distance completes the motion. stopByDistance
                    // means Self is already within MotorStopRadius.For(target),
                    // i.e. we are adjacent. Once moveToStateStopSent is set the
                    // walk-tick body (gated on !moveToStateStopSent) can no
                    // longer run, so it will never set motionDone via its own
                    // within-stop-radius branch. Without marking motionDone
                    // here the motion wedges: the interact (Use/Pickup/Talk)
                    // block below requires motionDone, so nothing fires, and the
                    // lock only clears at the 30s wall-clock timeout — which
                    // also mis-reports the (already-adjacent) target as
                    // Unreachable. Mark it done so the interact fires this tick.
                    // (motionDone || stopByTimeout already set/handle motionDone
                    // for their own paths; only the pure stopByDistance arrival
                    // needs this.)
                    if (stopByDistance && !motionDone)
                        motionDone = true;
                }

                // Phase 6e/6f — After STOP fires for a walk-done finish (i.e. we
                // arrived at the target), send the appropriate interact:
                //   - PutItemInContainer(item, self, 0) for pickup-eligible
                //     items (Armor/Weapon/Food/etc per PickupItemTypeMask)
                //   - Use(target) for everything else (NPC dialog, door
                //     toggle, portal teleport, lifestone attune).
                // We ONLY send on walk-done (not on wall-clock-timeout or
                // distance) to avoid pinging the server while we're not
                // adjacent.
                if (moveToStateStopSent &&
                    !useSent &&
                    motionDone &&
                    motionTarget is not null)
                {
                    // Slice L — Explore short-circuit. For Explore goals
                    // there is no action to perform on arrival; we just
                    // need to clear the motion lock so the next goal can
                    // be picked. Mark useSent so the cooldown/reset
                    // cascade fires below.
                    if (lockedGoalKind == GoalKind.Explore)
                    {
                        useSent = true;
                        useSentAt = DateTime.UtcNow;
                        Console.WriteLine(
                            $"[strategy] LLM-GOAL Explore arrived: " +
                            $"target='{motionTarget.Name}' guid=0x{motionTarget.Guid:X8} " +
                            $"(no action send; reset cascade will pick next goal)");
                        // Fall through to skip the rest of the block.
                        goto _slice_l_explore_done;
                    }

                    // Slice W.3 (ac-ai-players#88) — action dispatch
                    // is now GOAL-GATED. The picker is responsible
                    // for WHERE to walk; the LLM (via a typed Goal
                    // with a verb Kind) is responsible for WHAT to
                    // do on arrival. Wire-type bits no longer choose
                    // verbs. The verb table:
                    //   lockedGoalKind == Attack  -> ATTACK bundle
                    //   lockedGoalKind == Give    -> GIVE
                    //                                (pendingGiveItemGuid
                    //                                 set by Give-goal
                    //                                 pre-emptor)
                    //   lockedGoalKind == Pickup  -> PUTITEMINCONTAINER
                    //   lockedGoalKind == Use     -> USE
                    //   lockedGoalKind == Talk    -> USE (NPC talk
                    //                                opens dialog by
                    //                                Use on the NPC)
                    //   lockedGoalKind == null    -> arrival-no-op
                    //   (picker auto-lock without
                    //   an LLM goal — emit
                    //   PickerArrivedNoAction)
                    // The arrival-no-op branch keeps the bot parked
                    // for the post-action cooldown (~2s) while the
                    // salient event wakes the LLM. If the LLM emits
                    // a verb goal naming the same target before the
                    // cooldown elapses, the next motion-lock cycle
                    // walks distance ~0u and dispatches the verb.
                    if (lockedGoalKind is null)
                    {
                        useSent = true;
                        useSentAt = DateTime.UtcNow;
                        var arrivedActivity = pickerActivityCurrent;
                        var arrivedSource   = arrivedActivity?.Source ?? "unknown";
                        var arrivedReason   = arrivedActivity?.Reason ?? "picker auto-lock without LLM verb goal";
                        Console.WriteLine(
                            $"[strategy] PICKER ARRIVED no-action: " +
                            $"target='{motionTarget.Name}' guid=0x{motionTarget.Guid:X8} " +
                            $"source={arrivedSource} " +
                            $"(no opcode sent; awaiting LLM verb goal)");
                        eventStream.Append(new StreamEvent
                        {
                            Sequence = 0,
                            Utc      = DateTimeOffset.UtcNow,
                            Kind     = EventKind.PickerArrivedNoAction,
                            ItemGuid = motionTarget.Guid,
                            Name     = motionTarget.Name,
                            Text     = $"{arrivedSource}: {arrivedReason}",
                        });
                        if (arrivedActivity is not null &&
                            arrivedActivity.TargetGuid == motionTarget.Guid)
                        {
                            // Flag the live activity surface so the
                            // LLM prompt's "## Autonomous picker
                            // activity" block renders ARRIVED state
                            // directly (not just inferred from the
                            // event ring). Cleared automatically on
                            // the next picker selection cycle when
                            // a new PickerActivity replaces this one.
                            pickerActivityCurrent = arrivedActivity with { Arrived = true };
                            llmPolicyForPickerSurface?.SetCurrentPickerActivity(pickerActivityCurrent);
                        }
                        goto _slice_l_explore_done;
                    }

                    // Deliberation-race guard (motor side): if a DIFFERENT
                    // goal became current while this verb lock was walking
                    // (or the locked goal is gone entirely), do NOT dispatch
                    // the now-stale verb. Park for the post-action cooldown
                    // so the reset cascade picks the fresh goal on the next
                    // lock cycle. Without this, a fallback lock (or an old
                    // LLM lock) that arrives just after a new goal landed
                    // would fire the wrong opcode against motionTarget, and
                    // a semantically-identical-but-regenerated goal could be
                    // dispatched twice (old lock + re-locked fresh goal).
                    var dispatchLockOwnsGoal =
                        motionLockedGoalId is Guid dispatchGoalId &&
                        tactics.CurrentGoal is not null &&
                        tactics.CurrentGoal.Id == dispatchGoalId;
                    if (!dispatchLockOwnsGoal)
                    {
                        useSent = true;
                        useSentAt = DateTime.UtcNow;
                        Console.WriteLine(
                            $"[strategy] motion lock arrived but its goal is no longer current " +
                            $"(locked={motionLockedGoalId?.ToString() ?? "none"} " +
                            $"current={tactics.CurrentGoal?.Id.ToString() ?? "none"}); " +
                            $"skipping stale {lockedGoalKind} dispatch on '{motionTarget.Name}' " +
                            "(deliberation-race guard)");
                        goto _slice_l_explore_done;
                    }

                    useSent = true;
                    useSentAt = DateTime.UtcNow;
                    var itemType = motionTarget.ItemType ?? 0u;
                    // Slice W.3 — wire-type bits no longer choose the
                    // verb. itemType is still computed because the
                    // observe-log line below references it. isPickup
                    // is now derived from the LOCKED GOAL KIND, not
                    // from the item's wire type — verb ownership
                    // moved to the LLM.
                    var isHostile = lockedGoalKind == GoalKind.Attack;
                    var isPickup  = lockedGoalKind == GoalKind.Pickup;

                    // Target-vanished guard. If a hostile Attack target was
                    // removed from the world snapshot (e.g. killed +
                    // ObjectDelete'd, or culled) between target selection and
                    // this dispatch, do NOT arm a fresh combat lock + no-
                    // progress watchdog on a guid the server no longer knows —
                    // the attack silently no-ops and the watchdog burns the
                    // full AbandonOnNoDamageSec (~60s) before giving up.
                    // motionTarget is a snapshot captured when the lock was
                    // set; it can be stale, so re-check the LIVE world snapshot.
                    // Mirrors the walk-tick "disappeared from world snapshot —
                    // stopping" guard. useSent is already true above, so
                    // falling through parks the bot + re-deliberates. Snapshot
                    // absence may be a transient cull, so do NOT permanently
                    // blacklist the guid here — the ObjectDelete handler owns
                    // confirmed-removal visited-add. Mechanical motor
                    // bookkeeping (object existence); no game knowledge.
                    if (isHostile && worldState.TryGet(motionTarget.Guid) is null)
                    {
                        Console.WriteLine(
                            $"[combat] target 0x{motionTarget.Guid:X8} '{motionTarget.Name}' absent from " +
                            $"world snapshot at attack dispatch — skipping (re-deliberate).");
                        suppressVisitedAddOnReset = true;
                        goto _slice_l_explore_done;
                    }

                    // Self-preservation gate (paired with the Phase 7f.D
                    // disengage reflex): refuse to dispatch a melee Attack
                    // while our own health is below the re-engage threshold,
                    // or while this specific threat is still on the
                    // post-disengage avoid cooldown. Pure self-state + our
                    // own bookkeeping — no target choice, no game knowledge.
                    // useSent is already true above, so falling through to
                    // the reset cascade parks the bot and re-deliberates.
                    if (isHostile &&
                        ((worldState.Self is WorldObjectSnapshot suSelf &&
                          suSelf.HealthCurrent is uint suHc &&
                          suSelf.HealthMax is uint suHm &&
                          CombatDisengage.IsCombatSuppressed(suHc, suHm, CombatReengageHealthFraction)) ||
                         (combatAvoidUntil.TryGetValue(motionTarget.Guid, out var suAvoidUntil) &&
                          DateTime.UtcNow < suAvoidUntil)))
                    {
                        Console.WriteLine(
                            $"[combat] SUPPRESS attack on 0x{motionTarget.Guid:X8} '{motionTarget.Name}' — " +
                            $"self-health below re-engage threshold or threat on avoid cooldown; " +
                            $"parking (reset cascade re-deliberates)");
                        // TEMPORARY suppression — do NOT let the reset
                        // cascade permanently blacklist this hostile via
                        // visitedTargetGuids; re-engagement is owned by the
                        // avoid cooldown + re-engage health hysteresis.
                        suppressVisitedAddOnReset = true;
                        eventStream.Append(new StreamEvent
                        {
                            Sequence = 0,
                            Utc = DateTimeOffset.UtcNow,
                            Kind = EventKind.ActionRejected,
                            Text = $"DisengageLowHealth: refused to engage '{motionTarget.Name}' while recovering",
                            ItemGuid = motionTarget.Guid,
                            Name = motionTarget.Name,
                            ErrorCode = 0xFFFB,
                            ErrorLabel = "DisengageLowHealth",
                        });
                        goto _slice_l_explore_done;
                    }

                    var packetSeq = nextOutboundPacketSequence++;

                    int payloadLen;
                    string actionName;
                    byte[] actionBuf;
                    uint fragSeq;
                    // For combat we emit TWO fragments in one packet:
                    // (A) ChangeCombatMode(Melee) and (B) Targeted-
                    // MeleeAttack(guid, MediumHeight, 1.0f). Rubber-
                    // duck flagged that splitting them risks the
                    // NextUseTime race in Player_Combat.cs:737 — a
                    // bundled send avoids that because the server
                    // dispatches both messages in the same tick.
                    //
                    // Phase 7f.1 — on the FIRST swing of a NEW target
                    // we additionally bundle (C) QueryHealth(guid).
                    // QueryHealth sets selectedTarget server-side so
                    // Player_Vitals.HandleTargetVitals() starts emitting
                    // UpdateHealth on each tick — without it the bot
                    // is blind to damage progress (Player_Vitals.cs:166
                    // early-returns if selectedTarget == null). One
                    // QueryHealth is sufficient: selectedTarget persists
                    // until the player picks a different target.
                    uint fragSeqB = 0;
                    int combatPayloadLenB = 0;
                    byte[]? combatBufB = null;
                    uint fragSeqC = 0;
                    int combatPayloadLenC = 0;
                    byte[]? combatBufC = null;
                    var sendQueryHealth = false;
                    if (isHostile)
                    {
                        actionName = "ATTACK";
                        var attackMode = SelectCurrentAttackMode();
                        combatAttackMode = attackMode;
                        actionBuf  = new byte[GameActionChangeCombatModeMessage.PackedSize];
                        payloadLen = GameActionChangeCombatModeMessage.Pack(
                            actionBuf,
                            newCombatMode: CombatWeaponSelection.CombatModeValue(attackMode));
                        fragSeq    = nextOutboundFragmentSequence++;
                        if (attackMode == AttackMode.Missile)
                        {
                            combatBufB = new byte[GameActionTargetedMissileAttackMessage.PackedSize];
                            combatPayloadLenB = GameActionTargetedMissileAttackMessage.Pack(
                                combatBufB,
                                targetGuid: motionTarget.Guid,
                                attackHeight: 2u /* Medium */,
                                accuracyLevel: 1.0f);
                        }
                        else
                        {
                            combatBufB = new byte[GameActionTargetedMeleeAttackMessage.PackedSize];
                            combatPayloadLenB = GameActionTargetedMeleeAttackMessage.Pack(
                                combatBufB,
                                targetGuid: motionTarget.Guid,
                                attackHeight: 2u /* Medium */,
                                powerLevel: 1.0f);
                        }
                        fragSeqB   = nextOutboundFragmentSequence++;
                        // First swing on this target → also send
                        // QueryHealth so the server registers it as
                        // our selectedTarget and starts emitting
                        // UpdateHealth heartbeats.
                        sendQueryHealth = (combatTargetGuid != motionTarget.Guid);
                        if (sendQueryHealth)
                        {
                            combatBufC = new byte[GameActionQueryHealthMessage.PackedSize];
                            combatPayloadLenC = GameActionQueryHealthMessage.Pack(
                                combatBufC,
                                objectGuid: motionTarget.Guid);
                            fragSeqC = nextOutboundFragmentSequence++;
                        }
                    }
                    else if (lockedGoalKind == GoalKind.Give && pendingGiveItemGuid is uint giveItemGuid)
                    {
                        // Phase 7h — GIVE branch. The pre-emptor set
                        // motionTarget=NPC and pendingGiveItemGuid=item
                        // earlier. Now that walk-and-stop completed,
                        // send the GiveObjectRequest. Server-side:
                        //   Player_Inventory.HandleActionGiveObjectRequest
                        //   → CreateMoveToChain → GiveObjectToNPC
                        //   → NPC.EmoteManager fires the cat=6 Give
                        //     chain for the item's wcid (Jonathan's
                        //     emote 50507 for Exit Token → Goto
                        //     pick_coat_color → finalize_exit →
                        //     CastSpellInstant 3815 → recall).
                        actionName = "GIVE";
                        actionBuf  = new byte[GameActionGiveObjectRequestMessage.PackedSize];
                        payloadLen = GameActionGiveObjectRequestMessage.Pack(
                            actionBuf,
                            targetGuid: motionTarget.Guid,
                            itemGuid:   giveItemGuid,
                            amount:     1);
                        fragSeq    = nextOutboundFragmentSequence++;
                    }
                    else if (isPickup)
                    {
                        actionName = "PUTITEMINCONTAINER";
                        actionBuf  = new byte[GameActionPutItemInContainerMessage.PackedSize];
                        payloadLen = GameActionPutItemInContainerMessage.Pack(
                            actionBuf,
                            itemGuid: motionTarget.Guid,
                            containerGuid: chosenCharacterGuid,
                            placement: 0);
                        fragSeq    = nextOutboundFragmentSequence++;
                        worldInteractDispatched = true;
                    }
                    else if (lockedGoalKind == GoalKind.Use && pendingUseWithItemGuid is uint useWithSrc)
                    {
                        // Two-object "use item on target": apply a held
                        // inventory item (source, e.g. a key) to the world
                        // target (e.g. a locked chest). The pre-emptor set
                        // motionTarget=chest and pendingUseWithItemGuid=key
                        // earlier; the walk-and-stop is now complete.
                        // Server-side: Player.HandleActionUseWithTarget(
                        //   source, target). For a locked chest + matching
                        //   key this UNLOCKS the chest (Locked=false). It
                        //   does NOT open it — opening requires a follow-up
                        //   plain Use, which the LLM must emit (the motor
                        //   never auto-acts on a target the LLM did not ask
                        //   for).
                        actionName = "USEWITHTARGET";
                        actionBuf  = new byte[GameActionUseWithTargetMessage.PackedSize];
                        payloadLen = GameActionUseWithTargetMessage.Pack(
                            actionBuf,
                            sourceGuid: useWithSrc,
                            targetGuid: motionTarget.Guid);
                        fragSeq    = nextOutboundFragmentSequence++;
                    }
                    else if (lockedGoalKind == GoalKind.Use || lockedGoalKind == GoalKind.Talk)
                    {
                        actionName = "USE";
                        actionBuf  = new byte[GameActionUseMessage.PackedSize];
                        payloadLen = GameActionUseMessage.Pack(actionBuf, motionTarget.Guid);
                        fragSeq    = nextOutboundFragmentSequence++;
                        worldInteractDispatched = lockedGoalKind == GoalKind.Use;

                        // Slice Q + Slice U — track USE on any openable
                        // loot container (corpse, treasure chest,
                        // bookshelf, coffer) so the loot pre-emptor can
                        // dispatch PUTITEMINCONTAINER for items the
                        // server spawns inside. Pure wire-protocol bits
                        // (Corpse=0x2000, Openable=0x1, Container itemType
                        // 0x200) — no English-name matching. Slice Q
                        // bumps the kill-stat; Slice U non-corpse
                        // containers don't (no analog stat yet).
                        var useDescFlags  = motionTarget.ObjectDescriptionFlags ?? 0u;
                        var useIsCorpse   = (useDescFlags & (uint)ObjectDescriptionFlag.Corpse)   != 0;
                        var useIsOpenable = (useDescFlags & (uint)ObjectDescriptionFlag.Openable) != 0;
                        var useIsContainer = motionTarget.ItemType is uint uit && (uit & 0x00000200u) != 0;
                        var useIsSelfBag  = motionTarget.ContainerGuid is uint ucg && ucg == chosenCharacterGuid;
                        var trackAsLootContainer = !useIsSelfBag &&
                            (useIsCorpse || (useIsContainer && useIsOpenable));
                        if (trackAsLootContainer &&
                            worldState.Self is WorldObjectSnapshot useCorpseSelf)
                        {
                            recentlyOpenedContainers[motionTarget.Guid] =
                                (DateTime.UtcNow, useCorpseSelf.Position);
                            if (useIsCorpse) botStats.IncrementCorpsesOpened();
                            var sliceTag = useIsCorpse ? "Q" : "U";
                            var kindTag  = useIsCorpse ? "corpse" : "container";
                            Console.WriteLine(
                                $"[loot] Slice {sliceTag} tracking opened {kindTag} guid=0x{motionTarget.Guid:X8} " +
                                $"name='{motionTarget.Name}' at selfPos=" +
                                $"({useCorpseSelf.Position.X:F2},{useCorpseSelf.Position.Y:F2}) " +
                                $"cell=0x{useCorpseSelf.CellId ?? 0:X8}" +
                                (useIsCorpse ? $" stats.corpses_opened={botStats.CorpsesOpened}" : ""));
                        }
                    }
                    else
                    {
                        // Slice W.3 defensive fallthrough — should be
                        // unreachable. Explore + null are handled by
                        // the short-circuits above; Attack/Give/Pickup
                        // are explicit branches above; Use/Talk match
                        // this if-chain. If a future GoalKind is added
                        // without a dispatch branch, log and treat as
                        // arrival-no-op rather than silently sending USE.
                        Console.WriteLine(
                            $"[strategy] PICKER ARRIVED unknown verb: " +
                            $"lockedGoalKind={lockedGoalKind} target='{motionTarget.Name}' " +
                            $"guid=0x{motionTarget.Guid:X8} (no opcode sent)");
                        goto _slice_l_explore_done;
                    }

                    var msg = new OutboundPacket();
                    if (lastReceivedSeq != 0)
                        msg.AddAckSequence(lastReceivedSeq);
                    msg.AddBlobFragment(
                        fragSequence: fragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: actionBuf.AsSpan(0, payloadLen));
                    if (isHostile && combatBufB is not null)
                    {
                        msg.AddBlobFragment(
                            fragSequence: fragSeqB,
                            fragId: OutboundFragmentId,
                            queue: (ushort)GameMessageGroup.UIQueue,
                            gameMessagePayload: combatBufB.AsSpan(0, combatPayloadLenB));
                    }
                    if (isHostile && combatBufC is not null)
                    {
                        msg.AddBlobFragment(
                            fragSequence: fragSeqC,
                            fragId: OutboundFragmentId,
                            queue: (ushort)GameMessageGroup.UIQueue,
                            gameMessagePayload: combatBufC.AsSpan(0, combatPayloadLenC));
                    }

                    // Phase 6l (revised) — equip-after-pickup HANDOFF.
                    // We DON'T bundle GetAndWieldItem in the same packet
                    // as PutItemInContainer; the server's HandleAction-
                    // GetAndWieldItem races against its own pickup chain
                    // and emits InventoryServerSaveFailed(ActionCancelled).
                    // Instead we stash (itemGuid → slot bitmask) and
                    // dispatch the equip from the inbound message-handler
                    // when InventoryPutObjInContainer arrives for the
                    // same guid. This mirrors what a retail client does:
                    // the AC GUI sends GetAndWieldItem only AFTER the
                    // server acknowledges the inventory transfer.
                    //
                    // Slot selection: lowest set bit of ValidLocations.
                    // - Single-slot items (gauntlets=HandWear=0x20, etc.)
                    //   have one bit set.
                    // - Multi-slot items (rings=FingerWearLeft|Right)
                    //   pick the lowest, which is the canonical default.
                    // - Items with ValidLocations==0 or null aren't
                    //   wearable (food/keys/currency) — skip equip.
                    uint? equipLoc = null;
                    if (isPickup && motionTarget.ValidLocations is uint vl && vl != 0)
                    {
                        equipLoc = vl & (~vl + 1);
                        pendingEquip[motionTarget.Guid] = equipLoc.Value;
                        if (motionTarget.WeenieClassId is uint wcid)
                            pendingEquipWcid[motionTarget.Guid] = wcid;
                    }

                    // Phase 7c — USE-side wcid satisfaction. After we
                    // USE a Creature (NPC) or Writable (sign, book),
                    // mark its wcid as satisfied so the picker won't
                    // repeatedly walk to the next instance of the
                    // same wcid. Hostile creatures (golems) are
                    // handled separately by combatTargetGuid + the
                    // post-combat clear logic — they should NOT be
                    // marked satisfied here.
                    if (!isPickup && !isHostile && motionTarget.WeenieClassId is uint useWcid)
                    {
                        var useType = motionTarget.ItemType ?? 0u;
                        var isCreature = (useType & 0x00000010u) != 0;
                        var isWritableUse = (useType & 0x00002000u) != 0;
                        if (isCreature || isWritableUse)
                        {
                            satisfiedWeenieClasses.Add(useWcid);
                        }
                    }

                    // Phase 7f — engage combat lock.
                    if (isHostile)
                    {
                        // Only set combatStartedAt on the FIRST swing of
                        // a target. Retry swings reuse the existing
                        // timeout window (otherwise we'd never time out).
                        if (combatTargetGuid != motionTarget.Guid)
                        {
                            combatTargetGuid = motionTarget.Guid;
                            combatStartedAt  = DateTime.UtcNow;
                            lastDamageAt     = DateTime.UtcNow;
                            lastObservedTargetHealthFraction = null;
                            // Fresh target — no server combat-loop activity has
                            // been observed yet; clear any prior fight's clock so
                            // it cannot suppress this fight's loop-keeper.
                            lastServerCombatActivityAt = null;
                            // Fresh target — drop any stale cancel request
                            // carried over from a previous engagement.
                            combatFastRetryRequested = false;
                            // Fresh target — clear the surfaced fight telemetry
                            // immediately so the LLM never reads the previous
                            // target's landed/evaded counts during a switch.
                            ClearCombatFightStats();
                            // combat-feel: remember the KIND we are now
                            // fighting (with a timestamp) for later death
                            // attribution, and count the engagement.
                            lastCombatFoe = (motionTarget.WeenieClassId, motionTarget.Name, DateTime.UtcNow);
                            combatFeel.RecordFightStart(
                                new CombatFeelLedger.MobIdentity(
                                    motionTarget.WeenieClassId, motionTarget.Name));
                            PublishCombatHistory();
                        }
                        lastCombatAttackAt = DateTime.UtcNow;
                        // combat-feel: slide the death-attribution freshness
                        // window with ongoing engagement. The TTL is anchored
                        // to the LAST swing, not the fight's start, so a fight
                        // lasting longer than the TTL still attributes a death
                        // that immediately follows it. Refresh only the
                        // timestamp of the foe we are actively swinging at.
                        if (lastCombatFoe is { } lcf)
                        {
                            lastCombatFoe = (lcf.Wcid, lcf.Name, DateTime.UtcNow);
                        }
                    }

                    var sentLen = msg.Pack(sendBuf, myClientId,
                                           sequence: packetSeq, iteration: 1,
                                           encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, sentLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    var equipNote = equipLoc is uint el
                        ? $" (queued EQUIP loc=0x{el:X} for after pickup-ack)"
                        : (isPickup ? " (not wearable; ValidLocations=null/0)" : "");
                    if (isHostile)
                    {
                        var qhNote = sendQueryHealth ? "+QueryHealth" : "";
                        var cmdNote = combatAttackMode == AttackMode.Missile
                            ? "Missile+TargetedMissileAttack"
                            : "Melee+TargetedMeleeAttack";
                        Console.WriteLine(
                            $"[observe]   -> PHASE7F ATTACK: cmd={cmdNote}{qhNote} target=0x{motionTarget.Guid:X8} " +
                            $"name='{motionTarget.Name}' wcid={motionTarget.WeenieClassId} height=Medium power=1.0 " +
                            $"pktSeq={packetSeq} fragSeqA={fragSeq} fragSeqB={fragSeqB}{(sendQueryHealth ? $" fragSeqC={fragSeqC}" : "")} totalBytes={sentLen}");
                    }
                    else
                    {
                        Console.WriteLine(
                            $"[observe]   -> PHASE6E/6F {actionName}{equipNote}: target=0x{motionTarget.Guid:X8} name='{motionTarget.Name}' " +
                            $"itemType=0x{itemType:X} container=0x{chosenCharacterGuid:X8} " +
                            $"payload={payloadLen}B pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");
                    }
                    _slice_l_explore_done: ;
                }

                // Periodic world-state heartbeat — once every 100
                // packets the bot prints what it currently knows.
                // Useful for spotting whether the accumulator is
                // actually accumulating (vs. silently dropping
                // everything due to a sequence-gating bug).
                if (count % 100 == 0)
                    Console.WriteLine($"[world] {worldState.FormatSummary()}");
            }
            catch (OperationCanceledException)
            {
                // Either the outer cancellation token fired, the
                // observation deadline passed, or the walk-tick timer
                // expired. Distinguish so we don't exit on tick-wake.
                if (ct.IsCancellationRequested)
                    break;
                if (DateTime.UtcNow >= deadline)
                {
                    Console.WriteLine("[observe] observation window elapsed");
                    break;
                }
                // Otherwise: walk-tick wake. Fall through to the
                // walk-tick block below and continue the loop.
            }

            // Phase 6b — walk-tick. Runs both on packet wake AND on
            // tick-only wake. Reschedules the next tick, then (if a
            // target is locked and the motion is still in progress)
            // sends one stepped AP packet toward the target. Server-
            // side, this hits Player.SetRequestedLocation, which
            // Player_Tick.UpdatePlayerPosition picks up regardless
            // of FastTick — so this is what actually moves the bot.
            if (DateTime.UtcNow >= nextWalkTickAt)
            {
                nextWalkTickAt = DateTime.UtcNow.AddMilliseconds(WalkTickIntervalMs);

                // Wall-clock safety stop — independent of all other
                // gates, prevents a runaway walk if something else
                // wedges (lost echoes, server stall, etc.). cp-2272:
                // a no-lock standstill (nothing to walk toward) times
                // out fast so the bot re-deliberates instead of
                // drifting blindly for the full safety window.
                var motionHasProductiveLock =
                    motionTarget is not null ||
                    motionRememberedDest is not null ||
                    motionIndoorPath is not null ||
                    motionIndoorPathCells is not null;
                if (!motionDone &&
                    motionStartedAt is DateTime startedAt &&
                    Strategy.MotionTimeout.IsExpired(
                        startedAt, DateTime.UtcNow,
                        hasProductiveLock: motionHasProductiveLock,
                        lockedSec: MotionWallClockTimeoutSec,
                        noLockSec: MotionNoLockTimeoutSec))
                {
                    motionDone = true;
                    var motionTimeoutSec = Strategy.MotionTimeout.EffectiveSeconds(
                        motionHasProductiveLock, MotionWallClockTimeoutSec, MotionNoLockTimeoutSec);
                    Console.WriteLine($"[motion] walk-tick: wall-clock timeout {motionTimeoutSec}s elapsed — stopping");
                    // Slice J — surface unreachable target as
                    // ActionRejected so the LLM and the fallback
                    // policy can both avoid retargeting the same
                    // guid (otherwise we loop on geometry-blocked
                    // pickups like the academy Bruised Apple at
                    // d=4.9u behind a wall). Carries ItemGuid +
                    // Name so policies can dedupe by guid.
                    if (motionTarget is not null)
                    {
                        eventStream.Append(new StreamEvent
                        {
                            Sequence = 0,
                            Utc = DateTimeOffset.UtcNow,
                            Kind = EventKind.ActionRejected,
                            Text = $"Unreachable: '{motionTarget.Name}' (walk timeout {MotionWallClockTimeoutSec}s)",
                            ItemGuid = motionTarget.Guid,
                            Name = motionTarget.Name,
                            ErrorCode = 0xFFFE,
                            ErrorLabel = "Unreachable",
                        });
                    }
                    else
                    {
                        // Barren no-lock idle motion (cp-2262). The schema
                        // picker found no named target within range, so
                        // motionTarget is null. The interact block below — the
                        // ONLY place useSent is set — requires motionTarget !=
                        // null, so for a null target useSent is never set and the
                        // useSent-gated post-action reset cascade never runs. That
                        // leaves autonomousPositionSent stuck true; both the LLM
                        // deliberation gate and the schema picker require
                        // !autonomousPositionSent, so the bot wedges permanently
                        // (observed: 1 establishment call -> 1 no-lock probe ->
                        // 30s timeout -> zero further activity for the whole run).
                        // Mark useSent here so the reset cascade reopens the gates
                        // on the next packet, exactly like a completed action.
                        // This is the walk-tick (tick-path) site, so recovery does
                        // NOT depend on the packet-path PHASE5B STOP block having
                        // fired. Pure motor-state bookkeeping — no target is
                        // interacted with and no game knowledge is used.
                        useSent = true;
                        useSentAt = DateTime.UtcNow;
                        Console.WriteLine(
                            "[motion] walk-tick: no-lock idle motion timed out (no target) — " +
                            "resetting motion state to re-deliberate");
                    }
                }

                if (!motionDone &&
                    moveToStateStartSent && !moveToStateStopSent &&
                    motionTarget is not null &&
                    motionRotation is Quaternion lockedRot &&
                    worldState.Self is WorldObjectSnapshot walkSelf &&
                    walkSelf.CellId is uint walkCell)
                {
                    // Phase 3.1 — refresh fog-of-war. Add every indoor
                    // cell containing an observed object (including
                    // our own) to the seen-cell set. We pay this
                    // small O(N) cost per walk-tick (~30 objects in
                    // the academy) rather than per-packet so it
                    // doesn't dominate the receive loop.
                    if (_indoorNav.IsEnabled)
                    {
                        foreach (var snap in worldState.Objects.Values)
                        {
                            if (snap.CellId is uint cid &&
                                Strategy.IndoorNavService.IsIndoorCell(cid))
                                _seenIndoorCells.Add(cid);
                        }
                    }

                    // Indoor multi-cell traversal (academy escape fix).
                    //
                    // AC autonomous-position is CLIENT-AUTHORITATIVE for
                    // INDOOR cells: ACE's GameActionAutonomousPosition.Handle
                    // adopts the cellId the client claims and does NOT
                    // re-derive a new structural cell on its own (unlike the
                    // OUTDOOR seam case, where update_object_server recomputes
                    // the landblock cell from the AP coords — that's why the
                    // Slice 6/7 cross-landblock route below works via the
                    // server-reported-cell slide). So an indoor bot whose
                    // walk-tick keeps sending the OLD cellId never advances:
                    // its POSITION dead-reckons across the landblock-shared
                    // coordinate frame but its server CELL stays frozen at the
                    // start cell, the next room never streams, and any
                    // named-but-unseen target there is never perceivable.
                    //
                    // The fix: when following a planned MULTI-CELL INDOOR path,
                    // proactively advance the cell we claim in the AP packet to
                    // the cell of the most-recently-REACHED waypoint (see the
                    // AP-send site below). All indoor cells in a landblock share
                    // ONE continuous coordinate frame (verified live: adjacent
                    // cells report numerically-adjacent positions, and the
                    // navmesh world coords match wire positions to <1u), so the
                    // AP position needs NO conversion — only the cellId field
                    // changes. We gate strictly on indoor + >1 distinct cell +
                    // the server-reported cell being ON the planned path so the
                    // OUTDOOR cross-landblock route (motionIndoorPath is reused
                    // for it) and SAME-CELL indoor walks are untouched, AND an
                    // UNEXPECTED indoor cell (server adopted a cell not on our
                    // path) still falls through to the reconciliation/stop below
                    // instead of blindly stepping on.
                    bool followingIndoorPath =
                        motionIndoorPath is not null &&
                        motionIndoorPathCells is not null &&
                        motionIndoorPathCells.Count > 1 &&
                        Strategy.IndoorNavService.IsIndoorCell(walkCell) &&
                        motionIndoorPathCells.Contains(walkCell);

                    if (!followingIndoorPath && walkCell != motionLockedCellId)
                    {
                        // Phase 3.1 — if we're following a multi-cell
                        // indoor path AND the new cell is one the
                        // planner expected to traverse, slide the
                        // motion-lock forward instead of stopping.
                        // The bot is exactly where we wanted it; the
                        // remaining waypoints + the final approach
                        // logic stay valid. The same slide applies to an
                        // OUTDOOR on-foot seam crossing: motionOutdoorApCells
                        // records every cross-landblock cell this motion
                        // claimed via the OutdoorSeamCell AP override, so a
                        // server walkCell report matching one of them is the
                        // expected result of our own dead-reckoned step (an
                        // outdoor approach has no rasterized path-cell set to
                        // vouch for it) — slide, don't stop.
                        if ((motionIndoorPathCells is not null &&
                             motionIndoorPathCells.Contains(walkCell)) ||
                            motionOutdoorApCells.Contains(walkCell))
                        {
                            Console.WriteLine(
                                $"[motion] walk-tick: seam cell crossing " +
                                $"0x{motionLockedCellId:X8} -> 0x{walkCell:X8} (expected; continuing)");
                            motionLockedCellId = walkCell;
                        }
                        else
                        {
                            // Cell crossings out of scope for Phase 6b
                            // (target apple is in same cell as bot).
                            motionDone = true;
                            Console.WriteLine($"[motion] walk-tick: self cell changed (was 0x{motionLockedCellId:X8} now 0x{walkCell:X8}) — stopping (cell crossings are Phase 6c work)");
                        }
                    }

                    if (!motionDone && (walkCell == motionLockedCellId || followingIndoorPath))
                    {
                        // Slice S — blocked-motion detection. Before
                        // we compute and send another AP, check what
                        // happened to the LAST AP we sent. We snapshot
                        // walkSelf.Position INTO prevSelfBeforeAp just
                        // before sending each AP. If the server-reported
                        // position on this tick has barely moved from
                        // that snapshot — well under the step length
                        // we requested — server physics is holding us
                        // against geometry (wall, closed door, mob,
                        // obstacle). We tolerate a short run of these
                        // because the FIRST tick after lock-on can be
                        // a no-op (server hasn't started the motion
                        // yet) and a single network blip can hide one
                        // tick's worth of movement.
                        if (prevSelfBeforeAp is Vector3 prevSelf &&
                            prevExpectedStepLen >= BlockedMinExpectedStep)
                        {
                            // Slice 7 — if the bot crossed a landblock seam
                            // since the last AP, the local-coord delta wraps
                            // (e.g. 191 -> 1) and fabricates a ~190 m move.
                            // Measure in GLOBAL coords when the cell changed
                            // so a genuine block at a seam still registers.
                            float moveDx, moveDy;
                            if (prevSelfCellBeforeAp is uint prevCell && prevCell != walkCell)
                            {
                                var (pgx, pgy) = Strategy.AcCoords.ToGlobalXY(prevCell, prevSelf);
                                var (cgx, cgy) = Strategy.AcCoords.ToGlobalXY(walkCell, walkSelf.Position);
                                moveDx = cgx - pgx;
                                moveDy = cgy - pgy;
                            }
                            else
                            {
                                moveDx = walkSelf.Position.X - prevSelf.X;
                                moveDy = walkSelf.Position.Y - prevSelf.Y;
                            }
                            var actualMove = MathF.Sqrt(moveDx * moveDx + moveDy * moveDy);
                            if (actualMove < prevExpectedStepLen * BlockedMoveRatioThreshold)
                            {
                                consecutiveBlockedTicks++;
                                Console.WriteLine(
                                    $"[motion] walk-tick: BLOCKED (tick {consecutiveBlockedTicks}/{BlockedConsecutiveTicks}) " +
                                    $"actualMoveXY={actualMove:F3}u expectedStep={prevExpectedStepLen:F2}u " +
                                    $"ratio={(actualMove / prevExpectedStepLen):P0} " +
                                    $"self=({walkSelf.Position.X:F2},{walkSelf.Position.Y:F2}) " +
                                    $"prevSelf=({prevSelf.X:F2},{prevSelf.Y:F2})");
                                if (consecutiveBlockedTicks >= BlockedConsecutiveTicks)
                                {
                                    // Before fast-failing, try bounded reactive
                                    // LOCAL AVOIDANCE: a straight outdoor
                                    // frontier/remembered-coord walk that clips
                                    // an obstacle can often slip past it with a
                                    // short lateral detour. Only for OUTDOOR
                                    // remembered-coord motion (the indoor
                                    // pathfinder already routes around geometry);
                                    // this never changes WHAT the LLM asked for,
                                    // only HOW the motor reaches the same coords.
                                    bool detoured = false;
                                    if (motionIsOutdoorFrontierProbe &&
                                        motionRememberedDest is not null &&
                                        motionIndoorPath is null &&
                                        !Strategy.AcCoords.IsIndoor(walkCell) &&
                                        outdoorAvoidanceAttempt < Strategy.OutdoorLocalAvoidance.MaxAttempts &&
                                        motionRememberedDest.CellId is uint remDestCell)
                                    {
                                        var (avSgx, avSgy) = Strategy.AcCoords.ToGlobalXY(walkCell, walkSelf.Position);
                                        var (avTgx, avTgy) = Strategy.AcCoords.ToGlobalXY(remDestCell, motionRememberedDest.Position);
                                        if (Strategy.OutdoorLocalAvoidance.TryChooseDetour(
                                                avSgx, avSgy, avTgx, avTgy,
                                                outdoorAvoidanceAttempt,
                                                Strategy.OutdoorLocalAvoidance.DefaultDetourDistance,
                                                out var avDgx, out var avDgy) &&
                                            Strategy.OutdoorLocalAvoidance.IsForwardProgress(
                                                avSgx, avSgy, avTgx, avTgy, avDgx, avDgy))
                                        {
                                            var detourCell = Strategy.AcCoords.OutdoorCellIdFromGlobal(avDgx, avDgy);
                                            if (detourCell != 0u)
                                            {
                                                var ddlbx = (int)((detourCell >> 24) & 0xFFu);
                                                var ddlby = (int)((detourCell >> 16) & 0xFFu);
                                                var detourLocal = new Vector3(
                                                    avDgx - ddlbx * Strategy.AcCoords.BlockLength,
                                                    avDgy - ddlby * Strategy.AcCoords.BlockLength,
                                                    walkSelf.Position.Z);

                                                // Augment the cell-slide set with
                                                // the detour's cells (+ the current
                                                // cell): a sidestep can enter a cell
                                                // outside the original rasterized
                                                // straight-line set, which the
                                                // cell-slide gate would otherwise
                                                // refuse, stopping the detour dead.
                                                var augmented = motionIndoorPathCells is not null
                                                    ? new HashSet<uint>(motionIndoorPathCells)
                                                    : new HashSet<uint>();
                                                var rdx = avDgx - avSgx;
                                                var rdy = avDgy - avSgy;
                                                var rdist = MathF.Sqrt(rdx * rdx + rdy * rdy);
                                                var rsteps = Math.Max(1, (int)Math.Ceiling(rdist / 4f));
                                                for (int rs = 0; rs <= rsteps; rs++)
                                                {
                                                    var rt = (float)rs / rsteps;
                                                    var rc = Strategy.AcCoords.OutdoorCellIdFromGlobal(
                                                        avSgx + rdx * rt, avSgy + rdy * rt);
                                                    if (rc != 0u) augmented.Add(rc);
                                                }
                                                augmented.Add(walkCell);
                                                motionIndoorPathCells = augmented;

                                                // Re-steer toward the short detour
                                                // waypoint. On arrival the normal
                                                // Explore-arrival path re-perceives
                                                // and Strategy re-plans a fresh
                                                // frontier from the new, past-the-
                                                // obstacle position — no separate
                                                // "restore original target" step is
                                                // needed.
                                                motionRememberedDest = new WorldObjectSnapshot(0u)
                                                {
                                                    Name = motionRememberedDest.Name,
                                                    CellId = detourCell,
                                                    Position = detourLocal,
                                                };
                                                outdoorAvoidanceAttempt++;
                                                consecutiveBlockedTicks = 0;
                                                detoured = true;
                                                Console.WriteLine(
                                                    $"[motion] walk-tick: BLOCKED — local-avoidance detour " +
                                                    $"{outdoorAvoidanceAttempt}/{Strategy.OutdoorLocalAvoidance.MaxAttempts} " +
                                                    $"to 0x{detourCell:X8}@({detourLocal.X:F1},{detourLocal.Y:F1}) " +
                                                    $"global=({avDgx:F1},{avDgy:F1})");
                                            }
                                        }
                                    }

                                    if (!detoured)
                                    {
                                    // Bot is clipping into an obstacle. Stop
                                    // sending APs (the server WILL keep
                                    // clamping and we MUST NOT trust any
                                    // future accepted-far-away AP — that's
                                    // the "teleports past obstacle" bug).
                                    // Surface ActionRejected "Blocked" with
                                    // the target's guid+name so dedup
                                    // (LlmGoalPolicy + NoQuestKnowledgePolicy)
                                    // can avoid retargeting the same guid.
                                    motionDone = true;
                                    Console.WriteLine(
                                        $"[motion] walk-tick: BLOCKED for {consecutiveBlockedTicks} consecutive ticks — " +
                                        $"target 0x{motionTarget.Guid:X8} '{motionTarget.Name}' is unreachable from current position; stopping motion");
                                    eventStream.Append(new StreamEvent
                                    {
                                        Sequence = 0,
                                        Utc = DateTimeOffset.UtcNow,
                                        Kind = EventKind.ActionRejected,
                                        Text = $"Blocked: '{motionTarget.Name}' — server physics held bot in place ({consecutiveBlockedTicks} ticks, actualMove<{BlockedMoveRatioThreshold:P0} of expected)",
                                        ItemGuid = motionTarget.Guid,
                                        Name = motionTarget.Name,
                                        ErrorCode = 0xFFFD,
                                        ErrorLabel = "Blocked",
                                    });
                                    }
                                }
                            }
                            else
                            {
                                // Healthy progress — reset the counter.
                                consecutiveBlockedTicks = 0;
                            }
                        }

                        WorldObjectSnapshot? liveTarget;
                        if (motionDone)
                        {
                            liveTarget = null;
                        }
                        else if (motionRememberedDest is not null)
                        {
                            // Remembered-coordinate motion: the target is a
                            // recalled sighted location, not a live object in
                            // the current snapshot. Keep steering toward the
                            // fixed remembered coords; on arrival the Explore
                            // short-circuit re-perceives, and the next goal can
                            // resolve the entity live if it is really there.
                            liveTarget = motionRememberedDest;
                        }
                        else
                        {
                            liveTarget = worldState.WithinRadius(walkSelf, 999f)
                                .FirstOrDefault(s => s.Guid == motionTarget.Guid);
                        }
                        if (!motionDone && liveTarget is null)
                        {
                            motionDone = true;
                            Console.WriteLine($"[motion] walk-tick: target 0x{motionTarget.Guid:X8} disappeared from world snapshot — stopping");
                        }
                        else if (!motionDone && liveTarget is not null)
                        {
                            // Phase 3.1 — plan an indoor path once per
                            // motion lock. If the planner returns a
                            // collision-aware sequence of waypoints,
                            // the step computation below aims at the
                            // current waypoint instead of the target
                            // itself; this is what lets the bot walk
                            // AROUND furniture/walls and use doorways
                            // instead of clipping through geometry.
                            if (_indoorNav.IsEnabled &&
                                !motionIndoorPathAttempted &&
                                liveTarget.CellId is uint liveTargetCellId)
                            {
                                motionIndoorPathAttempted = true;
                                var pathResult = _indoorNav.TryFindPath(
                                    walkCell, walkSelf.Position,
                                    liveTargetCellId, liveTarget.Position,
                                    _seenIndoorCells);
                                Console.WriteLine(
                                    $"[motion] indoor-nav: {pathResult.Status} " +
                                    $"waypoints={pathResult.Waypoints.Count} " +
                                    $"cells={pathResult.PathCells.Count} " +
                                    $"seen-cells={_seenIndoorCells.Count} " +
                                    $"from=0x{walkCell:X8}@({walkSelf.Position.X:F1},{walkSelf.Position.Y:F1}) " +
                                    $"to=0x{liveTargetCellId:X8}@({liveTarget.Position.X:F1},{liveTarget.Position.Y:F1}) " +
                                    $"reason={pathResult.Reason ?? "(none)"}");
                                if (pathResult.Status == Strategy.IndoorPathStatus.Success)
                                {
                                    motionIndoorPath = pathResult.Waypoints;
                                    motionIndoorPathIndex = 0;
                                    motionIndoorPathCells = pathResult.PathCells;
                                }
                                else if (pathResult.Status == Strategy.IndoorPathStatus.NoPath)
                                {
                                    // Per Phase 3 rubber-duck: do NOT
                                    // silently straight-line on a real
                                    // pathfinder failure — that's the
                                    // wall-walking bug we're here to
                                    // fix. Stop motion and emit an
                                    // ActionRejected so dedup (LLM +
                                    // NoQuestKnowledgePolicy) avoids
                                    // retargeting the same unreachable
                                    // guid.
                                    motionDone = true;
                                    Console.WriteLine(
                                        $"[motion] indoor-nav: NO INDOOR PATH to 0x{motionTarget.Guid:X8} '{motionTarget.Name}' — stopping motion");
                                    eventStream.Append(new StreamEvent
                                    {
                                        Sequence = 0,
                                        Utc = DateTimeOffset.UtcNow,
                                        Kind = EventKind.ActionRejected,
                                        Text = $"NoIndoorPath: '{motionTarget.Name}' — indoor pathfinder found no walkable route ({pathResult.Reason ?? "unknown"})",
                                        ItemGuid = motionTarget.Guid,
                                        Name = motionTarget.Name,
                                        ErrorCode = 0xFFFC,
                                        ErrorLabel = "NoIndoorPath",
                                    });
                                }
                                // Other statuses (Disabled, NotIndoor,
                                // CrossLandblock, NoGraph) fall through
                                // to straight-line motion below — those
                                // are legitimate "indoor-nav doesn't
                                // apply here" cases.
                            }

                            // Decide whether to aim at an intermediate
                            // waypoint or the actual target. The path's
                            // last waypoint is a snap-to-graph
                            // approximation of the target, so once we
                            // arrive at it, switch to aiming at the
                            // real target position for the final
                            // close-in (the existing stop-radius /
                            // asymptote rules then govern arrival).
                            Vector3 stepTargetPos = liveTarget.Position;
                            uint stepTargetCell = liveTarget.CellId ?? walkCell;
                            bool aimingAtWaypoint = false;
                            if (!motionDone &&
                                motionIndoorPath is not null &&
                                motionIndoorPathIndex < motionIndoorPath.Count - 1)
                            {
                                stepTargetPos = motionIndoorPath[motionIndoorPathIndex].Position;
                                stepTargetCell = motionIndoorPath[motionIndoorPathIndex].CellId;
                                aimingAtWaypoint = true;
                            }

                            // Door-USE motor pre-emptor — part of the "walk"
                            // mechanical action, NOT a strategic interaction.
                            //
                            // Architecture justification (audit override):
                            // The hardcoded-knowledge audit on this commit
                            // flagged this block as FORBIDDEN ("Motor
                            // autonomously deciding to USE a target the
                            // LLM did not request"). We push it anyway with
                            // documented rationale per the audit skill's
                            // documented escape-hatch:
                            //
                            //   1. Doors are nav INFRASTRUCTURE, not game
                            //      content. They physically gate cell
                            //      connections. The pathfinder explicitly
                            //      routes through them.
                            //   2. The LLM already pushed an Intent to walk
                            //      to a target. Walking through a doorway
                            //      requires opening the door first. USE-on-
                            //      doorway is the mechanical "how" of
                            //      executing the LLM's "what" — same role
                            //      as steering, animation, or accepting
                            //      portal teleport.
                            //   3. The user explicitly directed this:
                            //      "all doors should be their own node so
                            //       there will be implicit instructions to
                            //       open the door node" (verbatim quote
                            //       in the original task message).
                            //   4. The alternatives (multi-second LLM
                            //      round-trip per door, or letting the bot
                            //      bounce off doors) fail the litmus tests
                            //      (academy traversal requires multiple
                            //      door openings within seconds).
                            //
                            // The two constants below are MECHANICAL:
                            //   - DoorMatchRadiusUnits = 3.0u: tolerance
                            //     for matching a Doorway navigation node
                            //     (placed at the cell-connection centroid)
                            //     to the live Door entity (placed at the
                            //     door's physics centroid). Sometimes the
                            //     pathfinder centroid and the door
                            //     entity's authoritative position differ
                            //     by a meter or two depending on cell
                            //     mesh resolution. 3u is the empirical
                            //     match tolerance.
                            //   - DoorUseCooldownSeconds = 30.0: a USE on
                            //     an already-open door toggles it CLOSED
                            //     (Door.cs:97-98). Without a cooldown,
                            //     repeated walk-tick attempts in the same
                            //     waypoint would close-then-reopen-then-
                            //     close in a tight loop. The cooldown is
                            //     a per-door "send USE at most once per
                            //     30s" mechanical rate-limit, not a
                            //     statement about door behavior.
                            //
                            // If we add other "nav infrastructure that
                            // requires interaction to traverse" (lever-
                            // gates, pressure plates, traversal portals)
                            // they belong in the same motor-extension
                            // pattern, NOT in autonomous picker logic.
                            const float DoorMatchRadiusUnits = 3.0f;
                            const double DoorUseCooldownSeconds = 30.0;
                            if (aimingAtWaypoint &&
                                motionIndoorPath is not null &&
                                motionIndoorPathIndex < motionIndoorPath.Count &&
                                motionIndoorPath[motionIndoorPathIndex].Kind == WalkableNodeKind.Doorway)
                            {
                                var doorWp = motionIndoorPath[motionIndoorPathIndex];
                                WorldObjectSnapshot? nearDoor = null;
                                float nearDoorDistSq = DoorMatchRadiusUnits * DoorMatchRadiusUnits;
                                foreach (var snap in worldState.Objects.Values)
                                {
                                    var dflags = snap.ObjectDescriptionFlags ?? 0u;
                                    if ((dflags & (uint)ObjectDescriptionFlag.Door) == 0) continue;
                                    var ddx = snap.Position.X - doorWp.Position.X;
                                    var ddy = snap.Position.Y - doorWp.Position.Y;
                                    var ddSq = ddx * ddx + ddy * ddy;
                                    if (ddSq <= nearDoorDistSq)
                                    {
                                        nearDoorDistSq = ddSq;
                                        nearDoor = snap;
                                    }
                                }
                                if (nearDoor is not null)
                                {
                                    bool onCooldown = doorUseDispatchedAt.TryGetValue(nearDoor.Guid, out var lastT)
                                        && (DateTime.UtcNow - lastT).TotalSeconds < DoorUseCooldownSeconds;
                                    if (!onCooldown)
                                    {
                                        doorUseDispatchedAt[nearDoor.Guid] = DateTime.UtcNow;
                                        var doorPktSeq  = nextOutboundPacketSequence++;
                                        var doorFragSeq = nextOutboundFragmentSequence++;
                                        var doorBuf = new byte[GameActionUseMessage.PackedSize];
                                        var doorLen = GameActionUseMessage.Pack(doorBuf, nearDoor.Guid);
                                        var doorMsg = new OutboundPacket();
                                        if (lastReceivedSeq != 0)
                                            doorMsg.AddAckSequence(lastReceivedSeq);
                                        doorMsg.AddBlobFragment(
                                            fragSequence: doorFragSeq,
                                            fragId: OutboundFragmentId,
                                            queue: (ushort)GameMessageGroup.UIQueue,
                                            gameMessagePayload: doorBuf.AsSpan(0, doorLen));
                                        var doorSent = doorMsg.Pack(sendBuf, myClientId,
                                                                    sequence: doorPktSeq, iteration: 1,
                                                                    encrypt: true, cryptoSend: cryptoSend);
                                        await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, doorSent),
                                                                   SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                                        var doorDist = MathF.Sqrt(nearDoorDistSq);
                                        Console.WriteLine(
                                            $"[motion] door-USE: opening door '{nearDoor.Name}' guid=0x{nearDoor.Guid:X8} " +
                                            $"at ({nearDoor.Position.X:F1},{nearDoor.Position.Y:F1}) " +
                                            $"distFromDoorway={doorDist:F2}u " +
                                            $"waypoint={motionIndoorPathIndex + 1}/{motionIndoorPath.Count} " +
                                            $"pktSeq={doorPktSeq} bytes={doorSent}");
                                    }
                                }
                            }

                            // Step in XY only; preserve self Z so we don't
                            // get flagged by Player_Tick's z-jump hacking
                            // check, and don't try to chase the apple's
                            // floor Z (~0.9) when we're at 0.0.
                            //
                            // Slice 7 — compute dx/dy in GLOBAL coords so a
                            // step target in a DIFFERENT landblock walks the
                            // correct physical direction. Landblock-local
                            // subtraction would be wrong across a seam
                            // (self x~191 in lbA, target x~1 in lbB is +2 m
                            // physically, not -190 m). Within one landblock
                            // the landblock offset cancels, so same-landblock
                            // behavior is byte-identical. The step is a
                            // frame-invariant delta, so newPos stays
                            // walkSelf.Position (local to walkCell) + step.
                            var (selfGX, selfGY) =
                                Strategy.AcCoords.ToGlobalXY(walkCell, walkSelf.Position);
                            var (tgtGX, tgtGY) =
                                Strategy.AcCoords.ToGlobalXY(stepTargetCell, stepTargetPos);
                            var dx = tgtGX - selfGX;
                            var dy = tgtGY - selfGY;
                            var lenXY = MathF.Sqrt(dx * dx + dy * dy);

                            // Target-aware terminal stop radius.
                            // Default 1.0u for items/NPCs/signs;
                            // 4.0u for Portals (whose stab obstacle
                            // makes the default unreachable — see
                            // MotorStopRadius for the audit framing).
                            var terminalStopRadius = MotorStopRadius.For(motionTarget);

                            // Outdoor melee-approach Z convergence.
                            //
                            // Outdoor self Z is effectively client-
                            // authoritative: the walk-tick below steps in XY
                            // and PRESERVES the bot's Z (it samples no terrain
                            // height), and the server adopts whatever Z the
                            // AutonomousPosition claims outdoors (a full live
                            // run showed self Z frozen at its spawn value across
                            // 2 landblocks / 353 samples with 0 ForcePosition
                            // corrections). On flat indoor cells self and target
                            // share a Z so this never mattered (the proven
                            // academy combat). Outdoors, a target on elevated
                            // terrain reports its TRUE surface Z while the bot
                            // stays ~20u below. The melee approach stops on a 2D
                            // XY radius, so the bot "arrives" ~1u away in XY yet
                            // ~20u away in 3D: every swing whiffs and the
                            // 3D-gated re-attack loop (StickyDistance^2 = 16,
                            // i.e. 4u) never fires — the bot deals 0 damage and
                            // dies without a kill.
                            //
                            // Fix (mechanical locomotion only): while walking to
                            // a live Attack target with BOTH self and target in
                            // outdoor cells, converge the claimed Z toward the
                            // target's reported surface Z at walk speed. This is
                            // the 3D form of "reach the coordinate the LLM's
                            // Attack goal already selected" — it encodes no game
                            // knowledge (no names / wcids / landblocks / per-type
                            // rules); the target and its Z are whatever Strategy
                            // chose. Convergence is clamped to <= walk speed per
                            // tick so it never trips the server z-jump anti-cheat
                            // that the XY-only stepping guards against. Scoped to
                            // Attack so flat-indoor academy traversal and the
                            // outdoor frontier/seam probes (which legitimately
                            // preserve Z) stay byte-identical.
                            const float MeleeZConvergeToleranceUnits = 1.0f;
                            bool outdoorMeleeZConverge =
                                Strategy.MeleeApproachZ.ShouldConverge(
                                    aimingAtWaypoint,
                                    lockedGoalKind == GoalKind.Attack,
                                    walkCell,
                                    stepTargetCell,
                                    walkSelf.Position.Z,
                                    stepTargetPos.Z,
                                    MeleeZConvergeToleranceUnits);

                            // Indoor terminal cell-claim (off-by-one fix).
                            //
                            // The per-tick AP cell-claim below advances
                            // motionLockedCellId to motionIndoorPath[index-1]'s
                            // cell. But the motor only AIMS at (and advances
                            // past) waypoints with index < Count-1 — the LAST
                            // waypoint is the terminal target, governed by the
                            // stop-radius branches below, so motionIndoorPathIndex
                            // caps at Count-1 and the claim caps at the
                            // SECOND-TO-LAST waypoint's cell. For a path whose
                            // final waypoint lives in the DESTINATION cell, that
                            // cell is therefore NEVER claimed: the bot's position
                            // dead-reckons into the destination cell but its
                            // server cell stays frozen at the prior cell, so the
                            // next room never streams (live: 45 waypoint-advances,
                            // 0 cell-advances, self frozen the whole run).
                            //
                            // Additionally the terminal stop-radius branches set
                            // motionDone and BREAK without sending an AP, so even
                            // setting the cell on that tick would never reach the
                            // wire.
                            //
                            // Fix: on the terminal-arrival tick of a MULTI-CELL
                            // indoor path whose final waypoint is in an
                            // as-yet-unclaimed destination cell, claim that cell
                            // and send ONE AP at the current position BEFORE the
                            // stop-radius branch sets motionDone. The bot has
                            // dead-reckoned to within the stop radius of the
                            // destination-cell waypoint, so it has demonstrably
                            // reached that cell; the landblock-shared coordinate
                            // frame means only the cellId field changes (no coord
                            // conversion). Strictly gated on followingIndoorPath +
                            // >1 distinct cell + the destination cell being indoor
                            // AND on the planned path, so same-cell indoor walks
                            // and the OUTDOOR cross-landblock route (which reuses
                            // motionIndoorPath) are untouched. Self-limiting: once
                            // motionLockedCellId == the destination cell the guard
                            // is false, so it fires at most once per arrival.
                            bool terminalArrivalThisTick =
                                !aimingAtWaypoint && lenXY < terminalStopRadius + 0.1f;
                            if (terminalArrivalThisTick &&
                                followingIndoorPath &&
                                motionIndoorPath is not null &&
                                motionIndoorPath.Count > 0)
                            {
                                var finalWp = motionIndoorPath[^1];
                                if (finalWp.CellId != motionLockedCellId &&
                                    Strategy.IndoorNavService.IsIndoorCell(finalWp.CellId) &&
                                    motionIndoorPathCells is not null &&
                                    motionIndoorPathCells.Contains(finalWp.CellId))
                                {
                                    Console.WriteLine(
                                        $"[motion] walk-tick: indoor terminal cell-claim " +
                                        $"0x{motionLockedCellId:X8} -> 0x{finalWp.CellId:X8} " +
                                        $"(reached destination-cell waypoint {motionIndoorPath.Count}/{motionIndoorPath.Count}; " +
                                        $"sending final AP before stop)");
                                    motionLockedCellId = finalWp.CellId;

                                    var claimBuf = new byte[GameActionAutonomousPositionMessage.PackedSize];
                                    var claimLen = GameActionAutonomousPositionMessage.Pack(
                                        claimBuf,
                                        cellId: motionLockedCellId,
                                        pos:    walkSelf.Position,
                                        rot:    lockedRot,
                                        instanceSequence:      walkSelf.SeqInstance      ?? 0,
                                        serverControlSequence: walkSelf.SeqServerControl ?? 0,
                                        teleportSequence:      walkSelf.SeqTeleport      ?? 0,
                                        forcePositionSequence: walkSelf.SeqForcePosition ?? 0,
                                        contact: true);
                                    var claimMsg = new OutboundPacket();
                                    if (lastReceivedSeq != 0)
                                        claimMsg.AddAckSequence(lastReceivedSeq);
                                    claimMsg.AddBlobFragment(
                                        fragSequence: nextOutboundFragmentSequence++,
                                        fragId: OutboundFragmentId,
                                        queue: (ushort)GameMessageGroup.UIQueue,
                                        gameMessagePayload: claimBuf.AsSpan(0, claimLen));
                                    var claimSent = claimMsg.Pack(sendBuf, myClientId,
                                                                  sequence: nextOutboundPacketSequence++, iteration: 1,
                                                                  encrypt: true, cryptoSend: cryptoSend);
                                    try
                                    {
                                        await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, claimSent),
                                                                   SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine(
                                            $"[motion] walk-tick: terminal cell-claim SendToAsync FAILED ({ex.GetType().Name}: {ex.Message})");
                                    }
                                }
                            }

                            // Phase 3.1 — intermediate-waypoint advance.
                            // When chasing a waypoint (not the final
                            // target), use a looser 1.5u XY threshold;
                            // we don't need to be on top of it, just
                            // close enough that the next waypoint is a
                            // reasonable continuation. The terminal
                            // stop radius (target-aware via
                            // MotorStopRadius) only applies to the
                            // real target.
                            if (!motionDone && aimingAtWaypoint && lenXY <= 1.5f)
                            {
                                Console.WriteLine(
                                    $"[motion] walk-tick: indoor-path waypoint {motionIndoorPathIndex + 1}/{motionIndoorPath!.Count} reached (distXY={lenXY:F2}u <= 1.50u) — advancing");
                                motionIndoorPathIndex++;
                                // Fall through to next walk-tick rather
                                // than re-evaluate now; keeps the
                                // existing once-per-tick pacing.
                            }
                            else if (motionDone)
                            {
                                // NoIndoorPath case already handled
                                // above; skip the rest.
                            }
                            else if (!aimingAtWaypoint && !outdoorMeleeZConverge && lenXY <= terminalStopRadius)
                            {
                                motionDone = true;
                                Console.WriteLine($"[motion] walk-tick: within stop radius (distXY={lenXY:F2}u <= {terminalStopRadius:F2}u) — stopping");
                            }
                            else if (!aimingAtWaypoint && !outdoorMeleeZConverge && lenXY < 1e-4f)
                            {
                                motionDone = true;
                                Console.WriteLine($"[motion] walk-tick: target overlaps self in XY (lenXY={lenXY:F4}) — stopping");
                            }
                            else if (!aimingAtWaypoint && !outdoorMeleeZConverge && lenXY - terminalStopRadius < 0.1f)
                            {
                                // Phase 6n — asymptote failsafe: when
                                // the remaining gap is < 0.1u, server
                                // physics clamps our step so the bot
                                // sits forever sending 0-step APs. Just
                                // call it good and let the USE/PUT fire.
                                // Server's actual UseRadius plus the
                                // target's physics radius is the real
                                // arrival bound; the terminal stop
                                // radius (target-aware via
                                // MotorStopRadius) provides sufficient
                                // margin for the server-side
                                // IsWithinUseRadiusOf cylinder check.
                                motionDone = true;
                                Console.WriteLine($"[motion] walk-tick: asymptote reached (distXY={lenXY:F3}u, gap={lenXY - terminalStopRadius:F3}u) — stopping");
                            }
                            else
                            {
                                var dt = WalkTickIntervalMs / 1000f;
                                // When aiming at an intermediate
                                // waypoint, we don't reserve the
                                // terminal stop radius — the
                                // waypoint itself is a valid floor
                                // sample to stand on.
                                var maxStep = aimingAtWaypoint
                                    ? lenXY
                                    : lenXY - terminalStopRadius;
                                // Clamp >= 0: when outdoorMeleeZConverge holds,
                                // the bot can reach this branch already inside
                                // the XY stop radius (maxStep <= 0) to keep
                                // correcting Z — never step backward in XY, and
                                // guard the dx/lenXY direction against a zero-
                                // length XY vector.
                                var stepLen = MathF.Max(0f, MathF.Min(WalkSpeedUnitsPerSec * dt, maxStep));
                                var stepX = lenXY > 1e-4f ? dx / lenXY * stepLen : 0f;
                                var stepY = lenXY > 1e-4f ? dy / lenXY * stepLen : 0f;

                                // Outdoor melee Z convergence: step the claimed
                                // Z toward the target's surface Z by at most one
                                // walk-speed step (same cap as XY) so the bot
                                // arrives at the target's elevation and 3D melee
                                // range closes. Preserves Z otherwise.
                                var newZ = walkSelf.Position.Z;
                                if (outdoorMeleeZConverge)
                                {
                                    newZ = Strategy.MeleeApproachZ.StepToward(
                                        walkSelf.Position.Z,
                                        stepTargetPos.Z,
                                        WalkSpeedUnitsPerSec * dt);
                                }
                                var newPos = new Vector3(
                                    walkSelf.Position.X + stepX,
                                    walkSelf.Position.Y + stepY,
                                    newZ);

                                var packetSeq = nextOutboundPacketSequence++;
                                var fragSeq   = nextOutboundFragmentSequence++;

                                // Indoor multi-cell traversal — advance the
                                // cell we CLAIM in the AP packet. The AP cell is
                                // the cell of the most-recently-REACHED waypoint
                                // (or the bot's current cell before any waypoint
                                // is reached). This keeps us claiming cell A
                                // while we walk toward the A-side doorway node,
                                // then switches to B exactly when we reach the
                                // B-side doorway node (just inside B) — i.e. we
                                // only ever claim a cell we have demonstrably
                                // stepped into. The position is unchanged
                                // (landblock-shared frame), so no coord
                                // conversion is needed; only cellId advances.
                                // This breaks the client-authoritative cellId
                                // freeze deadlock for indoor rooms. Resent every
                                // tick until the server adopts the new cell
                                // (followingIndoorPath keeps the send path live
                                // even while walkCell still lags motionLockedCellId).
                                if (followingIndoorPath)
                                {
                                    // Claim the cell of the most-recently-REACHED
                                    // waypoint only — never a cell we have not yet
                                    // demonstrably stepped into. For a normal
                                    // doorway path [.., B-door(B), B-floor(B),
                                    // target-snap(B)] the last-reached node at
                                    // final approach is already in the
                                    // destination cell, so this still advances us
                                    // into the next room; for a degenerate short
                                    // path it conservatively stays in the cell we
                                    // last reached rather than claiming ahead.
                                    uint apCell = motionIndoorPathIndex > 0 &&
                                                  motionIndoorPathIndex - 1 < motionIndoorPath!.Count
                                        ? motionIndoorPath[motionIndoorPathIndex - 1].CellId
                                        : walkCell;
                                    if (apCell != motionLockedCellId)
                                    {
                                        Console.WriteLine(
                                            $"[motion] walk-tick: indoor AP cell advance " +
                                            $"0x{motionLockedCellId:X8} -> 0x{apCell:X8} " +
                                            $"(reached waypoint {motionIndoorPathIndex}/{motionIndoorPath!.Count}; claiming last-reached planned cell)");
                                        motionLockedCellId = apCell;
                                    }
                                }

                                var apBuf = new byte[GameActionAutonomousPositionMessage.PackedSize];
                                // Outdoor seam-cell AP override.
                                //
                                // Outdoor self positions are LANDBLOCK-relative.
                                // An outdoor walk motion (frontier probe OR an
                                // Attack/Pickup interaction approach) packs a fixed
                                // source cell (motionLockedCellId) while newPos
                                // dead-reckons across the locked landblock's frame.
                                // When the step crosses an outdoor LANDBLOCK seam the
                                // packet keeps claiming the SOURCE cell while newPos
                                // overshoots into the neighbor landblock's coordinate
                                // range — the (cell, pos) pair is inconsistent, the
                                // server's PhysicsObj transition to the coords' real
                                // (often 2-landblock-distant) cell FAILS, and the
                                // server broadcasts the bot at cell origin (0,0,0),
                                // which the client adopts + re-sends in a
                                // self-reinforcing collapse — the bot freezes at the
                                // seam (actualMoveXY=0) and can never close on an
                                // outdoor target.
                                //
                                // Fix (packet-only, pure geometry): derive the AP
                                // cell from newPos's GLOBAL coordinates and, when that
                                // cell is in a DIFFERENT outdoor landblock, send the
                                // (neighbor cell, neighbor-local pos) pair so the
                                // packet is internally consistent and each per-tick
                                // transition is at most one adjacent cell. The derived
                                // cell is recorded in motionOutdoorApCells so the
                                // cell-reconciliation above slides the motion lock
                                // forward (instead of stopping) when the server then
                                // reports it. motionLockedCellId is NOT advanced here;
                                // if the server rejects, the local lock stays
                                // uncorrupted. Same-landblock outdoor walks (derived
                                // cell shares motionLockedCellId's landblock) and
                                // indoor walks (gated out) are byte-identical.
                                // Default: claim the locked cell at the
                                // dead-reckoned local position. The seam
                                // helper returns a non-null SeamCell ONLY when
                                // the step crosses into a different outdoor
                                // landblock — there is no out-parameter to
                                // clobber, so a non-seam tick can never
                                // collapse apPos to the cell origin (0,0,0).
                                uint apCellId = motionLockedCellId;
                                var apPos = newPos;
                                var seam = Strategy.OutdoorSeamCell.TryDeriveSeamCell(
                                    followingIndoorPath: followingIndoorPath,
                                    selfCellIsOutdoor:   !Strategy.AcCoords.IsIndoor(walkCell),
                                    lockedCellId:        motionLockedCellId,
                                    stepGlobalX:         selfGX + stepX,
                                    stepGlobalY:         selfGY + stepY,
                                    stepZ:               newPos.Z);
                                if (seam is { } seamCell)
                                {
                                    apCellId = seamCell.CellId;
                                    apPos = seamCell.LocalPos;
                                    motionOutdoorApCells.Add(apCellId);
                                    Console.WriteLine(
                                        $"[motion] walk-tick: outdoor seam-cell override " +
                                        $"0x{motionLockedCellId:X8} -> 0x{apCellId:X8} " +
                                        $"global=({selfGX + stepX:F1},{selfGY + stepY:F1}) " +
                                        $"local=({apPos.X:F1},{apPos.Y:F1})");
                                }
                                var apLen = GameActionAutonomousPositionMessage.Pack(
                                    apBuf,
                                    cellId: apCellId,
                                    pos:    apPos,
                                    rot:    lockedRot,
                                    instanceSequence:      walkSelf.SeqInstance      ?? 0,
                                    serverControlSequence: walkSelf.SeqServerControl ?? 0,
                                    teleportSequence:      walkSelf.SeqTeleport      ?? 0,
                                    forcePositionSequence: walkSelf.SeqForcePosition ?? 0,
                                    contact: true);

                                var msg = new OutboundPacket();
                                if (lastReceivedSeq != 0)
                                    msg.AddAckSequence(lastReceivedSeq);
                                msg.AddBlobFragment(
                                    fragSequence: fragSeq,
                                    fragId: OutboundFragmentId,
                                    queue: (ushort)GameMessageGroup.UIQueue,
                                    gameMessagePayload: apBuf.AsSpan(0, apLen));

                                var sentLen = msg.Pack(sendBuf, myClientId,
                                                       sequence: packetSeq, iteration: 1,
                                                       encrypt: true, cryptoSend: cryptoSend);
                                // Walk-tick send: wrap so a socket-level
                                // failure (server closed UDP "session",
                                // ICMP port-unreachable, NIC blip) is
                                // logged + handled gracefully instead of
                                // escaping the outer try/catch and ending
                                // the run silently. Cancellation should
                                // still propagate so the deadline path
                                // works as designed.
                                try
                                {
                                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, sentLen),
                                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine(
                                        $"[motion] walk-tick: SendToAsync FAILED ({ex.GetType().Name}: {ex.Message}) — stopping motion");
                                    motionDone = true;
                                    break;
                                }
                                walkTickAps++;
                                lastSentWaypointPos = newPos;
                                // Capture the waypoint's TRUE global coords
                                // (frame-free) so STOP can derive the canonical
                                // cell without depending on a possibly-slid
                                // motionLockedCellId frame. selfGX/selfGY are
                                // ToGlobalXY(walkCell, walkSelf.Position), so
                                // selfG + step is newPos in global coords.
                                lastSentWaypointGlobalXY = (selfGX + stepX, selfGY + stepY);
                                // Slice S — remember what we were BEFORE
                                // this AP, and how big a step we asked
                                // for. Next tick's blocked-detector
                                // compares (current walkSelf - prevSelf)
                                // against prevExpectedStepLen.
                                prevSelfBeforeAp    = walkSelf.Position;
                                prevSelfCellBeforeAp = walkCell;
                                prevExpectedStepLen = stepLen;
                                Console.WriteLine(
                                    $"[motion] walk-tick #{walkTickAps}: AP " +
                                    $"self=({walkSelf.Position.X:F2},{walkSelf.Position.Y:F2},{walkSelf.Position.Z:F2}) " +
                                    $"intent=({newPos.X:F2},{newPos.Y:F2},{newPos.Z:F2}) " +
                                    $"step=({stepX:F2},{stepY:F2}) stepLen={stepLen:F2}u " +
                                    $"distToTargetXY={lenXY:F2}u distToTargetZ={(stepTargetPos.Z - walkSelf.Position.Z):F2}u " +
                                    $"SENT(cell=0x{apCellId:X8} pos=({apPos.X:F2},{apPos.Y:F2},{apPos.Z:F2})) " +
                                    $"pktSeq={packetSeq} fragSeq={fragSeq}");
                            }
                        }
                    }
                }
            }
        }
        Console.WriteLine($"[observe] total packets observed: {count} (CRC pass={crcPass}, fail={crcFail})");
        Console.WriteLine($"[world]   final: {worldState.FormatSummary()}");

        // Spatial query smoke check — exercise the new
        // EnumerateNearby / WithinRadius / NearestN API against
        // the live world snapshot so we get real-world numbers
        // in the run log (academy populates ~30 objects).
        if (worldState.Self is { CellId: not null } spatialSelf)
        {
            var top5 = worldState.NearestN(spatialSelf, 5);
            Console.WriteLine($"[spatial] nearestN(5) for self 0x{spatialSelf.Guid:X8}:");
            foreach (var snap in top5)
            {
                WorldDistance.TrySquaredDistance(spatialSelf, snap, out var d2);
                var d = (float)Math.Sqrt(d2);
                var name = snap.Name ?? "<no name>";
                Console.WriteLine($"[spatial]   guid=0x{snap.Guid:X8} d={d,7:F2} cell=0x{snap.CellId ?? 0:X8} name='{name}'");
            }
            var within30 = worldState.WithinRadius(spatialSelf, 30f);
            Console.WriteLine($"[spatial] within 30 units: {within30.Count} objects");

            // Phase 6 — motion outcome summary. If we locked a target,
            // re-fetch its current snapshot (the world dictionary
            // returns the live mutable reference, so this just reads
            // the latest accumulated position) and report the closure.
            if (motionTarget is not null)
            {
                var liveTarget = worldState.WithinRadius(spatialSelf, 999f)
                    .FirstOrDefault(s => s.Guid == motionTarget.Guid);
                if (liveTarget is not null &&
                    WorldDistance.TrySquaredDistance(spatialSelf, liveTarget, out var dfin2))
                {
                    var dfin = (float)Math.Sqrt(dfin2);
                    var delta = motionInitialDistance.HasValue ? motionInitialDistance.Value - dfin : float.NaN;
                    // Prefer the START->STOP-send interval if STOP fired; else
                    // fall back to START->now (motion never stopped within window).
                    double elapsedSec = double.NaN;
                    if (motionStartedAt is DateTime mts)
                        elapsedSec = ((motionStoppedAt ?? DateTime.UtcNow) - mts).TotalSeconds;
                    var elapsedSource = motionStoppedAt is null ? "to-window-end" : "to-stop-send";
                    Console.WriteLine(
                        $"[motion] outcome target=0x{motionTarget.Guid:X8} name='{motionTarget.Name}' " +
                        $"initialDist={motionInitialDistance ?? float.NaN:F2}u finalDist={dfin:F2}u " +
                        $"closed={delta:F2}u (positive = bot moved toward target) " +
                        $"walkTickAps={walkTickAps} motionElapsed={elapsedSec:F1}s({elapsedSource}) " +
                        $"lastWaypoint={(lastSentWaypointPos is Vector3 lwp ? $"({lwp.X:F2},{lwp.Y:F2},{lwp.Z:F2})" : "-")}");
                }
                else
                {
                    Console.WriteLine(
                        $"[motion] outcome target=0x{motionTarget.Guid:X8} disappeared from world snapshot before window end");
                }
            }
            else
            {
                Console.WriteLine($"[motion] outcome no target was locked");
            }
        }
        else
        {
            Console.WriteLine("[spatial] self snapshot has no CellId — skipping spatial queries");
        }
        Console.WriteLine($"[observe] sent: {acksSent} acks, {timeSyncsSent} timesync echoes, characterCreate={characterCreateSent}, enterWorldRequest={enterWorldRequestSent}, enterWorld={enterWorldSent}, loginComplete={loginCompleteSent}, autonomousPosition={autonomousPositionSent}, moveToStateStart={moveToStateStartSent}, moveToStateStop={moveToStateStopSent}, walkTickAps={walkTickAps}");
        if (createResponse is not null)
            Console.WriteLine($"[observe] CharacterCreateResponse received: {createResponse.Response} (code={(uint)createResponse.Response})");
        else if (characterCreateSent)
            Console.WriteLine($"[observe] WARNING: CharacterCreate sent but no 0xF643 response received within window");
        if (enterWorldServerReady is not null)
            Console.WriteLine($"[observe] CharacterEnterWorldServerReady received");
        else if (enterWorldRequestSent)
            Console.WriteLine($"[observe] WARNING: EnterWorldRequest sent but no 0xF7DF received within window");
        if (lastCharacterError is not null)
            Console.WriteLine($"[observe] LAST CharacterError observed: code=0x{lastCharacterError.ErrorCode:X4}");
        // Slice D — flush nav graph + close training sink before
        // returning. The bot may be killed by Ctrl-C between session
        // end and the next run, so we want everything on disk.
        try
        {
            navGraph.Flush();
            Console.WriteLine($"[nav] graph snapshot: regions={navGraph.RegionCount} places={navGraph.PlaceCount} areas={navGraph.AreaCount} nodes={navGraph.NodeCount} edges={navGraph.EdgeCount} dir={navGraph.Directory}");
            navGraph.Dispose();
        }
        catch (Exception navEx)
        {
            Console.Error.WriteLine($"[nav] WARN final flush failed: {navEx.Message}");
        }
        try
        {
            trainingSink.Dispose();
        }
        catch (Exception tsEx)
        {
            Console.Error.WriteLine($"[training] WARN dispose failed: {tsEx.Message}");
        }
        return new ObserveResult(count, charList, serverName, ddd, characterCreateSent, createResponse,
            enterWorldRequestSent, enterWorldServerReady, enterWorldSent, lastCharacterError, chosenCharacterGuid,
            reconnectRequested);
    }

    private readonly record struct ObserveResult(
        int PacketCount,
        CharacterListMessage? CharacterList,
        ServerNameMessage? ServerName,
        DDDInterrogationMessage? DDDInterrogation,
        bool CharacterCreateSent,
        CharacterCreateResponseMessage? CharacterCreateResponse,
        bool EnterWorldRequestSent,
        CharacterEnterWorldServerReadyMessage? EnterWorldServerReady,
        bool EnterWorldSent,
        CharacterErrorMessage? LastCharacterError,
        uint ChosenCharacterGuid,
        bool ReconnectRequested);

    private async Task<ConnectRequestData> ReceiveConnectRequestAsync(byte[] recvBuf, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var ep = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
        var result = await _socket!.ReceiveFromAsync(new ArraySegment<byte>(recvBuf), SocketFlags.None, ep, cts.Token).ConfigureAwait(false);
        var len = result.ReceivedBytes;
        Console.WriteLine($"[handshake] received {len} bytes from {result.RemoteEndPoint}");

        if (len < PacketHeader.HeaderSize)
            throw new InvalidOperationException($"reply too short: {len} bytes");

        var hdr = new PacketHeader();
        hdr.Unpack(recvBuf.AsSpan(0, PacketHeader.HeaderSize));
        Console.WriteLine($"[handshake]   header: {hdr}");

        if (!hdr.HasFlag(PacketHeaderFlags.ConnectRequest))
            throw new InvalidOperationException(
                $"expected ConnectRequest flag, got {hdr.Flags}. Server may have rejected the LoginRequest.");

        if (hdr.Size < 32)
            throw new InvalidOperationException($"ConnectRequest body too short: {hdr.Size} bytes (need 32)");

        var body = recvBuf.AsSpan(PacketHeader.HeaderSize, hdr.Size);
        var serverTime = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(body));
        var cookie     = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8));
        var clientId   = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(16));
        var serverSeed = body.Slice(20, 4).ToArray();
        var clientSeed = body.Slice(24, 4).ToArray();

        return new ConnectRequestData(serverTime, cookie, clientId, serverSeed, clientSeed);
    }

    private static string Hex(byte[] bytes) => Hex(bytes.AsSpan());

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        Span<char> chars = stackalloc char[bytes.Length * 3];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i * 3] = ToHex((byte)(b >> 4));
            chars[i * 3 + 1] = ToHex((byte)(b & 0xF));
            chars[i * 3 + 2] = ' ';
        }
        return new string(chars[..^1]);
    }

    private static char ToHex(byte nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));

    // Phase 6n — compute the OR of all per-slot EquipMasks that the
    // server has confirmed we're already wielding into. Used by the
    // picker to skip duplicate quest-reward armor that targets a
    // slot we've already filled. Server-side, the wield's NewLocation
    // can OR multiple adjacent slots (e.g. pants occupy
    // UpperLegArmor|LowerLegArmor=0x6000); we therefore OR every
    // satisfied slot into one combined mask.
    private static uint SatisfiedSlotMask(IEnumerable<uint> slots)
    {
        uint mask = 0;
        foreach (var s in slots) mask |= s;
        return mask;
    }

    /// <summary>
    /// Autonomous indoor frontier exploration (road-to-endgame Phase A1).
    /// When the active goal's target can't be resolved against current
    /// perception and the bot is indoors, pick the nearest unexplored
    /// reachable cell from the static navmesh and return a synthetic
    /// destination at a stand-able floor point inside it. The existing
    /// indoor motor routes there (its K-hop planner already plans into
    /// not-yet-entered cells) and opens doors en route; crossing the
    /// threshold loads the next room's server-side visibility so its
    /// occupants become perceivable. Returns null when not indoors, the
    /// landblock has no indoor graph, or every reachable cell is already
    /// seen / on cooldown.
    ///
    /// This is domain-general spatial search: it reads ONLY navmesh
    /// geometry (DAT — treated as "what a human player's client renders
    /// and collides against") plus the bot's own visited-cell set. It
    /// never inspects object names, wcids, item types, or quest state —
    /// the DECISION of WHAT to find is the LLM's goal; this only supplies
    /// WHERE to look next. <paramref name="frontierCooldownUntil"/> is the
    /// per-session per-cell revisit throttle (pruned in place).
    /// </summary>
    private WorldObjectSnapshot? TryChooseFrontierDest(
        uint currentCellId,
        Dictionary<uint, DateTime> frontierCooldownUntil)
    {
        if (!_indoorNav.IsEnabled ||
            !Strategy.IndoorNavService.IsIndoorCell(currentCellId))
            return null;

        var graph = _indoorNav.GetOrLoad(
            Strategy.IndoorNavService.GetLandblockId(currentCellId));
        if (graph is null) return null;

        var now = DateTime.UtcNow;
        var cooled = new HashSet<uint>();
        if (frontierCooldownUntil.Count > 0)
        {
            var expired = new List<uint>();
            foreach (var kv in frontierCooldownUntil)
            {
                if (kv.Value > now) cooled.Add(kv.Key);
                else expired.Add(kv.Key);
            }
            foreach (var k in expired) frontierCooldownUntil.Remove(k);
        }

        var ft = Strategy.IndoorFrontierExplorer.ChooseFrontier(
            graph, currentCellId, _seenIndoorCells, cooled);
        if (ft is not Strategy.IndoorFrontierExplorer.FrontierTarget target)
            return null;

        frontierCooldownUntil[target.CellId] = now + _frontierRevisitCooldown;
        return new WorldObjectSnapshot(0u)
        {
            Name = "(unexplored area)",
            CellId = target.CellId,
            Position = target.Position,
        };
    }

    /// <summary>How long a frontier cell the bot targeted but couldn't
    /// reach (or reached without the goal resolving) is suppressed before
    /// it can be re-selected. Prevents tight re-target loops; coverage is
    /// otherwise bounded because entered cells join the seen set.</summary>
    private readonly TimeSpan _frontierRevisitCooldown = TimeSpan.FromSeconds(45);

    /// <summary>How far out (meters) each outdoor frontier pick probes.
    /// ~3 surface cells (24 m each): far enough to clear the current cell
    /// and reach fresh ground, short enough that one straight blind walk
    /// over unvalidated terrain stays bounded (the motor re-deliberates at
    /// the 30 s motion cap / on a block).</summary>
    private const float OutdoorFrontierStepMeters = 72f;

    /// <summary>Only the bot's visited samples within this radius inform an
    /// outdoor frontier pick, so the "away from where I've been" pull stays
    /// local to the current area instead of averaging the whole world.</summary>
    private const float OutdoorFrontierLocalityMeters = 250f;

    /// <summary>Prefer visited samples seen within this window when choosing
    /// an outdoor frontier; older local samples are used only if none are
    /// recent.</summary>
    private static readonly TimeSpan OutdoorFrontierRecency = TimeSpan.FromMinutes(20);

    /// <summary>Only remembered Mob sightings seen within this window bias an
    /// outdoor frontier pick. Aligned to the LLM prompt's sighting-recall TTL
    /// (180 s) so motor memory never outlives what the LLM can also see.</summary>
    private static readonly TimeSpan OutdoorFrontierMobBiasTtl = TimeSpan.FromSeconds(180);

    /// <summary>Half-width (m) of the geometric-score band within which a
    /// remembered-Mob bearing breaks a near-tie. Half an outdoor frontier
    /// step (72 m): monster memory can nudge a toss-up toward a known hunt
    /// zone but can NEVER pull the bot to a sector that is meaningfully more
    /// town-ward (more explored) or cooled.</summary>
    private const float OutdoorFrontierMobBiasTieWindowMeters = 36f;

    /// <summary>A remembered Mob sighting closer than this to the bot is
    /// ignored for biasing — the bot is effectively there already, so the
    /// stale coord would only cause an orbit. One frontier step (72 m).</summary>
    private const float OutdoorFrontierMobBiasMinDistanceMeters = 72f;

    /// <summary>
    /// Autonomous OUTDOOR frontier exploration — the surface analogue of
    /// <see cref="TryChooseFrontierDest"/>. When the active Explore goal has
    /// no resolvable target and the bot is OUTDOORS, choose the least-explored
    /// compass direction from the bot's OWN recorded visited positions + pure
    /// geometry (<see cref="Strategy.OutdoorFrontierExplorer"/>), and return a
    /// synthetic destination one step out, in the destination cell's
    /// landblock-local frame. Also emits, via <paramref name="pathCells"/>,
    /// the set of outdoor cells the straight segment crosses so the motor
    /// slides its cell-crossing lock across them (and any landblock seam)
    /// instead of halting at the first boundary.
    ///
    /// Returns null when the bot is indoors or every candidate cell is on
    /// cooldown. Domain-general spatial search: reads only the bot's visited
    /// geometry; never object names/wcids/types/landblock-ids/quest state, and
    /// decides nothing about interaction. <paramref name="frontierCooldownUntil"/>
    /// is the shared per-cell revisit throttle (pruned in place; the chosen
    /// cell is cooled so a blocked bearing naturally rotates next pick).
    ///
    /// When <paramref name="huntBiasAuthorized"/> is true (the active Explore
    /// belongs to an LLM/operator-authorized HUNT excursion — see the call
    /// site), the bot's OWN remembered recent Mob sightings break a near-tie
    /// in the geometric direction score toward a known hunt bearing. This is
    /// mechanical execution of an already-authorized hunt (the LLM still owns
    /// WHETHER to hunt and emits the Attack when a Mob enters view); it never
    /// fires for a non-hunt Explore, and it cannot override a clearly-more-
    /// unexplored or cooled direction.
    /// </summary>
    private WorldObjectSnapshot? TryChooseOutdoorFrontierDest(
        uint currentCellId,
        Vector3 currentPos,
        NavGraph navGraph,
        Dictionary<uint, DateTime> frontierCooldownUntil,
        bool huntBiasAuthorized,
        out IReadOnlySet<uint>? pathCells)
    {
        pathCells = null;
        if (Strategy.AcCoords.IsIndoor(currentCellId))
            return null;

        var now = DateTime.UtcNow;
        var cooled = new HashSet<uint>();
        if (frontierCooldownUntil.Count > 0)
        {
            var expired = new List<uint>();
            foreach (var kv in frontierCooldownUntil)
            {
                if (kv.Value > now) cooled.Add(kv.Key);
                else expired.Add(kv.Key);
            }
            foreach (var k in expired) frontierCooldownUntil.Remove(k);
        }

        // Project the bot's own visited OUTDOOR nodes to global samples.
        var nodes = navGraph.SnapshotNodes();
        var samples = new List<Strategy.OutdoorFrontierExplorer.VisitedSample>(nodes.Count);
        foreach (var n in nodes)
        {
            if (Strategy.AcCoords.IsIndoor(n.CellId)) continue;
            samples.Add(new Strategy.OutdoorFrontierExplorer.VisitedSample(
                n.WorldX, n.WorldY, n.LastSeenUtc));
        }

        var (selfGX, selfGY) = Strategy.AcCoords.ToGlobalXY(currentCellId, currentPos);

        // Hunt-bias input: the bot's OWN remembered recent Mob sightings, in
        // global meters (same frame as the visited samples). Only built for a
        // hunt-authorized Explore; Mob-kind + TTL filtered. Wire-derived Kind
        // is perception, not a hardcoded identity; this only refines WHERE an
        // authorized hunt walks, never WHETHER to hunt or WHAT to attack.
        List<Strategy.OutdoorFrontierExplorer.MonsterSighting>? mobSightings = null;
        if (huntBiasAuthorized)
        {
            var ttlCutoff = DateTimeOffset.UtcNow - OutdoorFrontierMobBiasTtl;
            foreach (var s in navGraph.SnapshotSighted())
            {
                if (s.Kind != EntityKind.Mob) continue;
                if (s.LastSeenUtc < ttlCutoff) continue;
                (mobSightings ??= new()).Add(
                    new Strategy.OutdoorFrontierExplorer.MonsterSighting(s.WorldX, s.WorldY));
            }
        }

        var choice = Strategy.OutdoorFrontierExplorer.ChooseFrontier(
            selfGX, selfGY, samples, cooled, DateTimeOffset.UtcNow,
            OutdoorFrontierStepMeters, OutdoorFrontierLocalityMeters, OutdoorFrontierRecency,
            mobSightings,
            mobSightings is { Count: > 0 } ? OutdoorFrontierMobBiasTieWindowMeters : 0f,
            OutdoorFrontierMobBiasMinDistanceMeters);
        if (choice is not Strategy.OutdoorFrontierExplorer.FrontierResult ft)
            return null;

        // Cool the chosen cell so the next pick rotates away if this bearing
        // turns out to be blocked (the bot's position won't have advanced, so
        // the same candidates recompute and this one is now skipped).
        frontierCooldownUntil[ft.DestCellId] = now + _frontierRevisitCooldown;

        // Convert the global destination back into the destination cell's
        // landblock-local frame. Preserve current Z — outdoor walks step in
        // XY and the server owns terrain height.
        var dlbx = (int)((ft.DestCellId >> 24) & 0xFFu);
        var dlby = (int)((ft.DestCellId >> 16) & 0xFFu);
        var destLocal = new Vector3(
            ft.GlobalX - dlbx * Strategy.AcCoords.BlockLength,
            ft.GlobalY - dlby * Strategy.AcCoords.BlockLength,
            currentPos.Z);

        // Rasterize the straight global segment into the motor's cell-slide
        // set (sample every ~4 m, mirroring NavGraph.AddOutdoorSegmentCells)
        // so the walk traverses every cell it crosses, including a landblock
        // seam, rather than stopping at the first cell boundary.
        var cells = new HashSet<uint>();
        var dx = ft.GlobalX - selfGX;
        var dy = ft.GlobalY - selfGY;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        var steps = Math.Max(1, (int)Math.Ceiling(dist / 4f));
        for (int s = 0; s <= steps; s++)
        {
            var t = (float)s / steps;
            var c = Strategy.AcCoords.OutdoorCellIdFromGlobal(selfGX + dx * t, selfGY + dy * t);
            if (c != 0u) cells.Add(c);
        }
        pathCells = cells;

        return new WorldObjectSnapshot(0u)
        {
            Name = "(unexplored area)",
            CellId = ft.DestCellId,
            Position = destLocal,
        };
    }

    /// <summary>
    /// Decides whether a teleport (one that leaves the server-side
    /// Teleporting flag set) has occurred since the LoginComplete whose
    /// acked sequences are <paramref name="ackInstance"/> /
    /// <paramref name="ackTeleport"/>. The server clears Teleporting only
    /// when the client (re)sends LoginComplete, so detecting this is what
    /// keeps a teleported bot from being frozen (every client position
    /// update is rejected while the flag is set).
    ///
    /// Instance epoch dominates: a strictly-newer instance sequence means
    /// the server re-created our object in a new epoch, which resets the
    /// per-epoch teleport counter to a low value — so a naive teleport-only
    /// compare would wrongly see the new low value as "older" and miss the
    /// resend. Within the same epoch, a strictly-newer teleport sequence
    /// means an intra-epoch teleport. A null acked value (we sent
    /// LoginComplete before observing any self sequence) means any later
    /// non-null observation is treated as new. Ordinary movement advances
    /// only the position sequence, so a normally moving or parked bot never
    /// trips this. All comparisons are wrap-aware.
    /// </summary>
    internal static bool TeleportOccurredSinceLoginComplete(
        ushort? currentInstance, ushort? currentTeleport,
        ushort? ackInstance, ushort? ackTeleport)
    {
        if (currentInstance is ushort ci)
        {
            if (ackInstance is ushort ai)
            {
                if (SequenceCompare.IsStrictlyNewer(ci, ai))
                    return true;   // new instance epoch → object re-created
                if (ci != ai)
                    return false;  // stale / out-of-order older epoch → ignore
                // same epoch → fall through to the teleport-counter compare
            }
            else
            {
                // Acked with no instance info; the epoch is now known, so a
                // teleport we did not account for has happened → resend.
                return true;
            }
        }

        if (currentTeleport is ushort ct)
        {
            if (ackTeleport is ushort at)
                return SequenceCompare.IsStrictlyNewer(ct, at);
            return true;           // teleport sequence appeared since the ack
        }

        return false;
    }

    public void Dispose() => _socket?.Dispose();

    // ---- self-progress wake (cp-2280, generalized to a value-edge) -------
    // Structural salience nudge: whenever the bot's unspent experience (the
    // spendable self-progress resource decoded from PropertyInt64
    // AvailableExperience) takes a NEW value, append ONE SelfProgressChanged
    // event so the LLM re-reads `## Self`. Direct analogue of the
    // CombatFeedback wake. Dedup is a consecutive value-edge: emit on the
    // first known value, then again only when the decoded value DIFFERS from
    // the last one observed (no magnitude/materiality band — that judgement
    // was deliberately removed in cp-2280 after audit). This wakes the LLM
    // after an instant XP-spend (RaiseAttribute/RaiseVital/RaiseSkill), which
    // otherwise emits no external salient event and left the bot idle until
    // the 30s stuck-timeout. Source surfaces RAW self facts only (unspent XP,
    // lifetime total, current/peak HP, level); it assigns NO urgency, names
    // NO attribute/skill, applies NO magnitude/materiality judgment, and says
    // nothing about spending — WHAT to do with the XP is owned entirely by
    // the prompt RULES. Pure bookkeeping + wire-fact rendering, no
    // game-knowledge.

    /// <summary>
    /// Build a SelfProgressChanged event from raw self facts whenever unspent
    /// XP takes a NEW value. <paramref name="lastUnspentXp"/> carries the last
    /// observed unspent-XP value across calls (null = never observed); it is
    /// updated on emit. Returns false (and emits nothing) when unspent XP is
    /// unknown or unchanged from the last observed value. The event Text is
    /// raw facts only — no directive, no attribute name, no urgency, no
    /// magnitude judgment.
    /// </summary>
    internal static bool TryBuildSelfProgressEvent(
        long? unspentXp, long? totalXp, int? level, uint? hpCurrent, uint? hpMax,
        ref long? lastUnspentXp, out StreamEvent ev, out string logLine)
    {
        ev = null!;
        logLine = string.Empty;
        if (unspentXp is not long unspent)
            return false;
        if (lastUnspentXp is long last && last == unspent)
            return false;
        lastUnspentXp = unspent;

        var hp = hpCurrent is uint hc && hpMax is uint hm && hm > 0
            ? $"{hc}/{hm} HP"
            : "unknown HP";
        var lvlTxt = level is int l ? $"level {l}, " : string.Empty;
        var totTxt = totalXp is long t ? $"{t} total" : "unknown total";

        ev = new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.SelfProgressChanged,
            Text = $"Self progress: {lvlTxt}{unspent} unspent experience, {totTxt}, {hp}.",
        };
        logLine =
            $"[self-progress] SelfProgressChanged: unspent={unspent} " +
            $"total={totalXp?.ToString() ?? "?"} level={level?.ToString() ?? "?"} " +
            $"hp={hp} — waking LLM (unspent-XP value-edge).";
        return true;
    }

    /// <summary>
    /// Read the bot's current self facts from <paramref name="worldState"/>
    /// and, whenever unspent XP takes a new value, append a SelfProgressChanged
    /// event to <paramref name="eventStream"/>. No-op when self/XP is unknown
    /// or unspent XP is unchanged from the last observed value.
    /// </summary>
    private static void MaybeEmitSelfProgress(
        ref long? lastUnspentXp, WorldState worldState, EventStream eventStream)
    {
        var self = worldState.Self;
        if (self is null)
            return;

        long? unspent = self.PropertyInt64s is { } p64 &&
            p64.TryGetValue(PrivateUpdatePropertyInt64Message.AvailableExperienceId, out var ax)
                ? ax : (long?)null;
        long? total = self.PropertyInt64s is { } p64t &&
            p64t.TryGetValue(PrivateUpdatePropertyInt64Message.TotalExperienceId, out var tx)
                ? tx : (long?)null;
        // PropertyInt id 25 = Level (see WorldStateProjection / ACE-bots
        // Source/ACE.Entity/Enum/Properties/PropertyInt.cs).
        int? level = self.PropertyInts is { } pi &&
            pi.TryGetValue(25u, out var lv) ? lv : (int?)null;

        if (TryBuildSelfProgressEvent(
                unspent, total, level, self.HealthCurrent, self.HealthMax,
                ref lastUnspentXp, out var ev, out var logLine))
        {
            eventStream.Append(ev);
            Console.WriteLine(logLine);
        }
    }

    private readonly record struct ConnectRequestData(
        double ServerTime,
        ulong Cookie,
        uint ClientId,
        byte[] ServerSeed,
        byte[] ClientSeed);
}

internal readonly record struct HandshakeResult(
    double ServerTime,
    ulong Cookie,
    uint ClientId,
    byte[] ServerSeed,
    byte[] ClientSeed,
    bool PostHandshakePacketSeen,
    CharacterListMessage? CharacterList,
    ServerNameMessage? ServerName,
    DDDInterrogationMessage? DDDInterrogation,
    CharacterCreateResponseMessage? CharacterCreateResponse,
    bool EnterWorldRequestSent,
    CharacterEnterWorldServerReadyMessage? EnterWorldServerReady,
    bool EnterWorldSent,
    CharacterErrorMessage? LastCharacterError,
    uint ChosenCharacterGuid);
