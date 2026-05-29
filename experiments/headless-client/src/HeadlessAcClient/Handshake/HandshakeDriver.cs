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

    private readonly IPEndPoint _serverPort0;
    private readonly IPEndPoint _serverPort1;
    private readonly string _account;
    private readonly string _password;
    private readonly string _characterName;

    private Socket? _socket;

    public HandshakeDriver(IPAddress host, int port, string account, string password, string? characterName = null)
    {
        _serverPort0 = new IPEndPoint(host, port);
        _serverPort1 = new IPEndPoint(host, port + 1);
        _account = account;
        _password = password;
        _characterName = string.IsNullOrWhiteSpace(characterName) ? "Headless01" : characterName;
    }

    public async Task<HandshakeResult> RunAsync(CancellationToken ct)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        var localPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        Console.WriteLine($"[handshake] bound UDP socket on 0.0.0.0:{localPort}");

        var loginBuf = ArrayPool<byte>.Shared.Rent(RecvBufferSize);
        var recvBuf = ArrayPool<byte>.Shared.Rent(RecvBufferSize);
        try
        {
            var loginLen = BuildLoginRequest(loginBuf);
            Console.WriteLine($"[handshake] sending LoginRequest ({loginLen} bytes) to {_serverPort0}");
            await _socket.SendToAsync(new ArraySegment<byte>(loginBuf, 0, loginLen),
                                      SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);

            var connectReq = await ReceiveConnectRequestAsync(recvBuf, ct).ConfigureAwait(false);
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
        const int            PostActionCooldownSec = 2;
        // Phase 7f.5 — bumped from 30 → 100. With the exploration
        // fallback the bot can keep finding new things to do well
        // past 30 actions (whole academy has ~50+ named objects:
        // 10 golems, 3-5 NPCs, ~20 signs, 5-6 doors, ~10 wearables).
        // The 30s damage-watchdog still protects against per-fight
        // runaways; this just lets the session-length budget breathe.
        const int            MaxActionsPerSession = 100;
        int                  actionsCompleted = 0;
        var                  visitedTargetGuids = new HashSet<uint>();
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
        // Phase 7f — combat state. Locked when bot dispatches a melee
        // attack. While locked, the picker keeps targeting the same
        // golem (so we don't walk away mid-fight) and a retry timer
        // re-sends TargetedMeleeAttack every CombatRetryIntervalSec
        // until we observe the target dying (UpdateHealth==0) or
        // disappearing, or the wall-clock CombatTimeoutSec elapses.
        // Hardcoded for now: only wcid 12698 (Sparring Golem) triggers
        // combat. Bot is unarmed (no weapon equipped) — server uses
        // bare-handed melee, falling back to UnarmedCombat skill.
        //
        // Phase 7f.1 — progress-based abandon. The 30s wall-clock
        // timeout fires too early when the bot is making progress
        // (3% damage per hit at unarmed L1). Switched to "no damage
        // for AbandonOnNoDamageSec" — abandon a golem ONLY if we've
        // failed to land ANY damage in this window. lastDamageAt is
        // bumped whenever UpdateHealth shows healthFraction dropped.
        //
        // Phase 7f.2 — server-driven swing loop. AC1 melee is a
        // CONTINUOUS server-side loop: one TargetedMeleeAttack
        // packet starts a loop that auto-repeats Attack() until the
        // target dies, AutoRepeatAttacks character option is off,
        // bot moves out of range, or OnMoveComplete fires with a
        // non-None status (Player_Move.cs:228 calls
        // HandleActionCancelAttack). The slow motion cycle we run
        // BREAKS the swing loop because each AP/MS packet causes
        // OnMoveComplete to fire. To fix:
        //   1) After first ATTACK on a target, suppress AP +
        //      walk-tick + MS-START + MS-STOP packets (the server
        //      auto-positions us via melee sticky distance).
        //   2) Re-engage attack only via a periodic re-attack timer
        //      (CombatRetryIntervalSec, set high enough to not
        //      conflict with the server's animation cooldown).
        //   3) Fast-fire packet bundles ONLY TargetedMeleeAttack
        //      (NOT ChangeCombatMode — re-sending CombatMode while
        //      already in Melee triggers ActionCancelled via the
        //      NextUseTime gate in Player_Combat.cs:744).
        const uint           HostileCreatureWcidSparringGolem = 12698u;
        // Phase 7f.5 — Academy exit mechanism. The Calling Stone
        // (wcid 5084) is the "portal" that consumes the Academy
        // Exit Token and teleports the player to the academy's
        // outdoor destination. We promote it above everything else
        // in BOTH picker passes because (a) leaving the academy is
        // the M1.6 goal once the bot has done basic training, and
        // (b) the Calling Stone is sometimes in a corner of the
        // start room and the bot's wandering can leave it for
        // last — we want it engaged ASAP after we have the token.
        // Verified in phase7f5-headless21-fullrun.log: bot saw
        // 'Calling Stone' (guid 0x800003D4 wcid=5084 itemType=0x800)
        // on first ObjectCreate but never picked it as candidate
        // in 23 cycles / 30 min.
        const uint           AcademyCallingStoneWcid = 5084u;
        // Phase 7f.5b — Exit Token wcid. The Calling Stone REQUIRES
        // an Academy Exit Token in inventory to teleport us out;
        // USE'ing it without the token fails and burns the
        // Calling Stone's guid into visitedTargetGuids forever.
        // We gate the Calling Stone's prio=-1 promotion on actual
        // ownership of an Exit Token (ObjectCreate of wcid 29335
        // with ContainerGuid == self.Guid). If we don't own one
        // yet, treat the Calling Stone like any other unvisited
        // object (default to whatever its itemType prio would be).
        // Jonathan grants the Exit Token on first USE; this is
        // observed in phase7f5-headless21-fullrun.log cycle 3.
        const uint           AcademyExitTokenWcid = 29335u;
        const double         CombatRetryIntervalSec = 5.0;
        const double         AbandonOnNoDamageSec   = 60.0;
        uint?                combatTargetGuid = null;
        DateTime?            combatStartedAt = null;
        DateTime?            lastCombatAttackAt = null;
        DateTime?            lastDamageAt = null;
        float?               lastObservedTargetHealthFraction = null;
        var ownPlayerSeen = false;
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
        bool loginCompleteResendNeeded = false;

        // Phase 7h — GIVE-to-NPC table. Maps (NPC weenie class) →
        // (item weenie class to GIVE to that NPC if owned). Filled
        // from ace_world emote chain inspection. Initially:
        //   - Jonathan (29324 academyguardexitholtburg) ← Academy
        //     Exit Token (29335). Triggers academy-exit recall.
        //   - Society Greeter (30991) ← Calling Stone (5084). Per
        //     stone's ShortDesc: "Give this item to the Society
        //     Greeter." Part of the academy quest chain.
        //
        // The pre-emptor (below) iterates this dict in declaration
        // order, so the first qualifying pair (we own the item AND
        // see the NPC in worldState) wins. To prioritize the exit
        // over the Greeter handoff, Jonathan must come first. See
        // ac-ai-players#66 for personality-driven prioritization.
        var giveItemForNpcWcid = new Dictionary<uint, uint>
        {
            { 29324u, AcademyExitTokenWcid    },  // Jonathan ← Exit Token (academy exit)
            { 30991u, AcademyCallingStoneWcid },  // Society Greeter ← Calling Stone
        };
        // (npcWcid, itemWcid) pairs that have completed a GIVE.
        // Defensive guard against re-firing on the same pair if the
        // server-side delete of the item from inventory hasn't
        // propagated to our worldState by the next tick.
        var giveCompletedPairs = new HashSet<(uint, uint)>();
        // Set by the Phase 7h pre-emptor when we lock onto an NPC to
        // GIVE. Consumed by the action-send block (replaces USE with
        // GiveObjectRequest). Cleared by the cooldown-reset block.
        uint? pendingGiveItemGuid = null;

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
        Quaternion?          motionRotation = null;
        float?               motionInitialDistance = null;
        DateTime?            motionStartedAt = null;
        DateTime?            motionStoppedAt = null;
        DateTime             nextWalkTickAt = DateTime.UtcNow;
        Vector3?             lastSentWaypointPos = null;
        int                  walkTickAps = 0;
        uint                 motionLockedCellId = 0;
        bool                 motionDone = false;
        bool                 useSent = false;
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
        const float          MotionStopRadius = 1.0f;
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
        CharacterEnterWorldServerReadyMessage? enterWorldServerReady = null;
        CharacterErrorMessage? lastCharacterError = null;
        uint chosenCharacterGuid = 0;

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

        Console.WriteLine($"[observe] listening for post-handshake packets for {seconds}s; will send acks + timesync echoes ...");
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
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
                            createResponse = ccr;
                            if (ccr.Response == CharacterCreateResponse.Ok)
                                Console.WriteLine($"[observe]   -> CharacterCreateResponse: Ok guid=0x{ccr.CharacterGuid:X8} name=\"{ccr.Name}\"");
                            else
                                Console.WriteLine($"[observe]   -> CharacterCreateResponse: {ccr.Response} (code={(uint)ccr.Response})");
                            break;
                        case CharacterEnterWorldServerReadyMessage ready:
                            enterWorldServerReady = ready;
                            Console.WriteLine($"[observe]   -> CharacterEnterWorldServerReady (server ready, send 0xF657)");
                            break;
                        case CharacterErrorMessage cerr:
                            lastCharacterError = cerr;
                            Console.WriteLine($"[observe]   -> CharacterError: code=0x{cerr.ErrorCode:X4}");
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
                            break;
                        case GameEventMessage ge:
                            var geDesc = ge.Payload is not null
                                ? ge.Payload.ToString()
                                : $"raw[{ge.PayloadBytes.Length}]";
                            Console.WriteLine(
                                $"[observe]   -> GameEvent: type={ge.EventType} (0x{(uint)ge.EventType:X4}) " +
                                $"recv=0x{ge.ReceiverGuid:X8} seq={ge.ServerEventSequence} " +
                                $"payload={geDesc}");
                            // Phase 6l — pickup-ack triggers the queued
                            // equip. Send GetAndWieldItem in a fresh
                            // packet now that the server reports the
                            // item is in our inventory; rootOwner==this
                            // takes the no-MoveToChain branch
                            // (Player_Inventory.cs:1646).
                            if (ge.Payload?.InventoryPutObjInContainer is { } putAck)
                            {
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
                            }
                            // Phase 6n — wield ack: mark the slot mask
                            // and the wcid as satisfied so the picker
                            // stops chasing duplicate copies of the
                            // same armor reward.
                            if (ge.Payload?.WieldObject is { } wieldAck)
                            {
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
                                    }
                                    lastObservedTargetHealthFraction = updHealth.HealthFraction;
                                    if (updHealth.HealthFraction <= 0.0001f)
                                    {
                                        Console.WriteLine(
                                            $"[combat] target 0x{ctgHealth:X8} DEAD (health={updHealth.HealthFraction:F3}); " +
                                            $"clearing combat lock.");
                                        visitedTargetGuids.Add(ctgHealth);
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
                            if (ge.Payload?.AttackDone is { } atkDone)
                            {
                                if (atkDone.ErrorCode != 0)
                                {
                                    Console.WriteLine(
                                        $"[combat] AttackDone error=0x{atkDone.ErrorCode:X4} " +
                                        $"({WeenieErrorLabels.Label(atkDone.ErrorCode)})");
                                }
                            }
                            break;
                        case PrivateUpdatePropertyIntMessage pup:
                            Console.WriteLine(
                                $"[observe]   -> PrivateUpdatePropertyInt: {pup.PropertyName} = {pup.Value} (seq={pup.Sequence})");
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
                        Name:    _characterName,
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
                    if (lastObservedSelfLandblock is uint prevLb)
                    {
                        if (lb != prevLb && loginCompleteSent)
                        {
                            Console.WriteLine(
                                $"[teleport] mid-session landblock change " +
                                $"0x{prevLb:X8} -> 0x{lb:X8}; queueing " +
                                $"LoginComplete resend to clear Teleporting.");
                            loginCompleteResendNeeded = true;
                        }
                    }
                    lastObservedSelfLandblock = lb;
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
                    if (sinceLastDamage >= AbandonOnNoDamageSec)
                    {
                        Console.WriteLine(
                            $"[combat] NO-PROGRESS abandon after {sinceLastDamage:F0}s with no damage on 0x{ctgWatch:X8} " +
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
                    }
                }

                // Phase 7f.2 — RE-ENGAGE attack. AC1's melee is a
                // server-side loop: one TargetedMeleeAttack starts
                // the swing loop, server auto-repeats Attack() until
                // target dies, AutoRepeatAttacks character option
                // turns off, we leave range, or the loop is
                // cancelled by our motion. We send a fresh
                // TargetedMeleeAttack every CombatRetryIntervalSec
                // (NOT ChangeCombatMode — re-sending CombatMode
                // triggers ActionCancelled via NextUseTime gate).
                // If the swing loop is still running server-side,
                // this attack is silently no-op'd by the
                // `if (Attacking || MeleeTarget != null && MeleeTarget.IsAlive) return;`
                // guard in Player_Melee.cs:99 — harmless. If the
                // loop has stopped, this re-engages it.
                if (combatTargetGuid is uint ffCtg &&
                    lastCombatAttackAt is DateTime ffLast &&
                    (DateTime.UtcNow - ffLast).TotalSeconds >= CombatRetryIntervalSec &&
                    worldState.Self is WorldObjectSnapshot ffSelf &&
                    worldState.TryGet(ffCtg) is WorldObjectSnapshot ffTarget &&
                    WorldDistance.TrySquaredDistance(ffSelf, ffTarget, out var ffD2) &&
                    ffD2 <= 16.0f /* StickyDistance^2 = 16 */)
                {
                    var ffPacketSeq = nextOutboundPacketSequence++;
                    var ffFragSeq   = nextOutboundFragmentSequence++;

                    var ffBuf = new byte[GameActionTargetedMeleeAttackMessage.PackedSize];
                    var ffLen = GameActionTargetedMeleeAttackMessage.Pack(
                        ffBuf,
                        targetGuid: ffCtg,
                        attackHeight: 2u /* Medium */,
                        powerLevel: 1.0f);

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
                        $"[observe]   -> PHASE7F.2 RE-ATTACK: target=0x{ffCtg:X8} dist={Math.Sqrt(ffD2):F2}u " +
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

                        ieCandidate = snap;
                        ieEquipSlot = slot;
                        break;
                    }

                    if (ieCandidate is not null)
                    {
                        inventoryEquipSent.Add(ieCandidate.Guid);
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
                if (useSent && useSentAt is DateTime usat &&
                    (DateTime.UtcNow - usat).TotalSeconds >= PostActionCooldownSec)
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
                    actionsCompleted++;
                    // Only flag visited for non-combat targets. The
                    // combat-state machine owns the visited add for
                    // golems (on death or timeout) so we re-target
                    // the same guid until it dies.
                    if (motionTarget is not null && motionTarget.Guid != combatTargetGuid)
                        visitedTargetGuids.Add(motionTarget.Guid);

                    // Phase 7h — record completed GIVE pair so the
                    // pre-emptor doesn't re-fire if the server-side
                    // inventory delete hasn't reached our worldState
                    // by the next pre-emptor pass. motionTarget here
                    // is the NPC we GAVE to (pre-emptor set it).
                    if (pendingGiveItemGuid is not null && motionTarget is not null &&
                        motionTarget.WeenieClassId is uint giveNpcWc)
                    {
                        foreach (var p in giveItemForNpcWcid)
                        {
                            if (p.Key == giveNpcWc)
                            {
                                giveCompletedPairs.Add((p.Key, p.Value));
                                Console.WriteLine(
                                    $"[motion] PHASE7H GIVE COMPLETE: " +
                                    $"npcWcid={p.Key} itemWcid={p.Value} added to giveCompletedPairs");
                                break;
                            }
                        }
                    }

                    Console.WriteLine(
                        $"[motion] action cycle #{actionsCompleted} complete (visited 0x{motionTarget?.Guid:X8} '{motionTarget?.Name}'); " +
                        $"resetting motion state to pick next target");

                    // Reset every per-action gate.
                    autonomousPositionSent = false;
                    moveToStateStartSent = false;
                    moveToStateStopSent = false;
                    motionTarget = null;
                    motionRotation = null;
                    motionInitialDistance = null;
                    motionStartedAt = null;
                    motionStoppedAt = null;
                    motionLockedCellId = 0;
                    motionDone = false;
                    useSent = false;
                    useSentAt = null;
                    lastSentWaypointPos = null;
                    walkTickAps = 0;
                    pendingGiveItemGuid = null;

                    if (actionsCompleted >= MaxActionsPerSession)
                    {
                        Console.WriteLine(
                            $"[motion] max actions per session ({MaxActionsPerSession}) reached; staying idle until observation window closes");
                    }
                    }
                }

                // Phase 7h — GIVE-to-NPC pre-emptor (replaces the
                // earlier Phase 7g inventory-USE-on-Calling-Stone idea,
                // which sent UseDone(ok) but produced no teleport: per
                // ace_world DB the Calling Stone has no SpellDID; its
                // intended use is GIVE to a Society Greeter / Training
                // Master, not USE).
                //
                // Mechanism per ace_world weenie 29324 (Jonathan,
                // academyguardexitholtburg):
                //   cat=6 Give wcid=29335 (Academy Exit Token) →
                //     Goto pick_coat_color (weighted GotoSet auto-picks
                //     one of 10 coat color variants) →
                //     Goto finalize_exit →
                //     InqBoolStat RecallsDisabled →
                //     TestSuccess RecallsDisabled =
                //       Give wcid=49563 (facility hub portal gem) +
                //       SetSanctuaryPosition cell=0xA9B40019 (84,7.1,94) +
                //       EraseQuest AcademeyExitTokenGiven +
                //       CastSpellInstant spell_Id=3815 (recall to fresh
                //       sanctuary). The recall is the actual teleport.
                //
                // No coat-color dialog is needed because the server
                // auto-selects via emote probabilities. We just need to
                // GIVE the token. The server-side CreateMoveToChain
                // walks our player to Jonathan automatically — but since
                // a headless player without a real client tick has a
                // history of physics-skipping, we still drive the walk
                // ourselves (set motionTarget=Jonathan, send AP, run
                // through MoveToState START / walk-tick / STOP, then
                // send GIVE). This is the same pattern as the existing
                // walk-and-USE flow used for other NPC interactions.
                //
                // Pre-emptor bypasses visitedTargetGuids (Jonathan was
                // already USE'd in cycle 3 to receive the token) and
                // ignores radius (worldState.Objects is a global view).
                // giveCompletedPairs records (npcWcid, itemWcid) pairs
                // that have completed at action-send time, defensively
                // gating re-fires (the item is normally already gone
                // from inventory after a successful GIVE, but this is
                // belt-and-suspenders).
                if (!autonomousPositionSent &&
                    !useSent &&
                    motionTarget is null &&
                    combatTargetGuid is null &&
                    actionsCompleted < MaxActionsPerSession &&
                    loginCompleteSent &&
                    !loginCompleteResendNeeded &&
                    loginCompletePacketIndex >= 0 &&
                    (count - loginCompletePacketIndex) >= PostLoginCompleteGracePackets &&
                    worldState.Self is WorldObjectSnapshot giveSelf &&
                    giveSelf.CellId is uint giveSelfCell &&
                    giveSelfCell != 0)
                {
                    foreach (var pair in giveItemForNpcWcid)
                    {
                        var npcWcid = pair.Key;
                        var itemWcid = pair.Value;
                        if (giveCompletedPairs.Contains((npcWcid, itemWcid))) continue;

                        var ownedItem = worldState.Objects.Values.FirstOrDefault(o =>
                            o.WeenieClassId == itemWcid &&
                            o.ContainerGuid is uint cg && cg == giveSelf.Guid);
                        if (ownedItem is null) continue;

                        var npc = worldState.Objects.Values.FirstOrDefault(o =>
                            o.WeenieClassId == npcWcid &&
                            o.CellId is uint nCell && nCell != 0 &&
                            (o.Guid & 0xFF000000u) != 0x50000000u);
                        if (npc is null) continue;

                        float giveYaw = 0f;
                        Quaternion giveRot;
                        if (WorldHeading.TryYawToTarget(giveSelf, npc, out giveYaw))
                            giveRot = WorldHeading.RotationFromYaw(giveYaw);
                        else
                            giveRot = giveSelf.Rotation;

                        motionTarget          = npc;
                        motionRotation        = giveRot;
                        pendingGiveItemGuid   = ownedItem.Guid;
                        if (WorldDistance.TrySquaredDistance(giveSelf, npc, out var giveD2))
                            motionInitialDistance = (float)Math.Sqrt(giveD2);

                        autonomousPositionSent = true;
                        autonomousPositionPacketIndex = count;

                        var apPacketSeq = nextOutboundPacketSequence++;
                        var apFragSeq   = nextOutboundFragmentSequence++;
                        var apBuf = new byte[GameActionAutonomousPositionMessage.PackedSize];
                        var apLen = GameActionAutonomousPositionMessage.Pack(
                            apBuf,
                            cellId: giveSelfCell,
                            pos:    giveSelf.Position,
                            rot:    giveRot,
                            instanceSequence:      giveSelf.SeqInstance      ?? 0,
                            serverControlSequence: giveSelf.SeqServerControl ?? 0,
                            teleportSequence:      giveSelf.SeqTeleport      ?? 0,
                            forcePositionSequence: giveSelf.SeqForcePosition ?? 0,
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
                            $"[motion] PHASE7H GIVE TARGET LOCK: " +
                            $"npc='{npc.Name}' wcid={npcWcid} guid=0x{npc.Guid:X8} " +
                            $"cell=0x{(npc.CellId ?? 0):X8} dist={motionInitialDistance ?? float.NaN:F2}u; " +
                            $"item='{ownedItem.Name}' wcid={itemWcid} guid=0x{ownedItem.Guid:X8}; " +
                            $"sent AP yaw={giveYaw:F3}rad pktSeq={apPacketSeq} fragSeq={apFragSeq} totalBytes={apSent}");
                        Console.WriteLine(
                            $"[motion]      Expect: walk-and-GIVE flow drives MoveToState START → walk-tick AP → STOP → " +
                            $"GameActionGiveObjectRequest(target=0x{npc.Guid:X8}, item=0x{ownedItem.Guid:X8}, amount=1). " +
                            $"Then Jonathan's emote chain (Goto pick_coat_color → finalize_exit → CastSpellInstant 3815) " +
                            $"recalls bot to landblock 0xA9B40019.");
                        break;
                    }
                }

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
                    selfCell != 0)
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
                    //          Also: weight the sort so any object named "Door"
                    //          ranks ahead of equal-distance non-doors. Doors
                    //          are the chief mechanism for progressing between
                    //          tutorial rooms; otherwise a dense room of NPCs
                    //          can starve us of door interactions until they
                    //          all join the visited set.
                    var apRot = self.Rotation;
                    // Phase 7f.5b — Do we own an Academy Exit Token?
                    // The Calling Stone consumes the token to teleport us
                    // out; USE without the token fails and burns the
                    // Calling Stone into visitedTargetGuids. Compute once
                    // per AP tick so both picker passes share the result.
                    var haveExitToken = worldState.Objects.Values.Any(o =>
                        o.WeenieClassId == AcademyExitTokenWcid &&
                        o.ContainerGuid is uint cg && cg == self.Guid);
                    var inRange = worldState.WithinRadius(self, MotionSearchRadius)
                        .Where(s => s.Guid != self.Guid && !string.IsNullOrEmpty(s.Name))
                        .Where(s => !visitedTargetGuids.Contains(s.Guid))
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
                        // Phase 7c/7f/7f.3 picker. Priorities:
                        // - prio 0: friendly NPCs (Creature itemType
                        //   0x10, not the hostile wcid). NPCs hand
                        //   out academy quests, training weapons,
                        //   and reward items. Bot MUST exhaust all
                        //   NPCs in range before engaging hostiles —
                        //   a bare-handed L1 cannot kill a Sparring
                        //   Golem (verified in phase7f2 run-01: bot
                        //   landed 2 hits over 720s, then died).
                        // - prio 1: hostile creatures (wcid 12698
                        //   Sparring Golem). Combat is gating on
                        //   Academy Token, which only drops from
                        //   golem corpses. Engage AFTER all NPCs.
                        // - prio 2: unsatisfied-slot wearables,
                        //   doors, portals, AND writables (signs).
                        // - prio 3: non-wearable pickups (apples).
                        // - prio 4: everything else.
                        candidate = inRange
                            .Select(s =>
                            {
                                WorldDistance.TrySquaredDistance(self, s, out var d2);
                                var isDoor = string.Equals(s.Name, "Door", StringComparison.OrdinalIgnoreCase);
                                var isPortal = s.ItemType is uint pt && (pt & 0x00010000u) != 0;
                                var isWritable = s.ItemType is uint wt && (wt & 0x00002000u) != 0;
                                var isPickup = s.ItemType is uint it && (it & PickupItemTypeMask) != 0 && !isPortal && !isWritable;
                                var isNpc = s.ItemType is uint nt && (nt & 0x00000010u) != 0 && !isPickup;
                                var isWearable = isPickup && s.ValidLocations is uint vl && vl != 0;
                                var hasSatisfiedSlot = isWearable && s.ValidLocations is uint vl2 &&
                                    (vl2 & SatisfiedSlotMask(satisfiedEquipSlots)) != 0;
                                var pickedBefore = pickupCountByName.TryGetValue(s.Name ?? string.Empty, out var pc) && pc > 0;
                                var isHostile = s.WeenieClassId == HostileCreatureWcidSparringGolem;
                                var isAcademyExit = s.WeenieClassId == AcademyCallingStoneWcid;
                                int prio;
                                // Phase 7f.5 — Academy Calling Stone is the
                                // M1.6 exit mechanism; promote above all else.
                                // Phase 7f.5b: ONLY when we already hold an
                                // Exit Token. Otherwise USE fails and the
                                // Calling Stone is permanently marked visited.
                                if (isAcademyExit && haveExitToken) prio = -1;
                                else if (isNpc && !isHostile) prio = 0;
                                else if (isHostile) prio = 1;
                                else if (isDoor || isPortal) prio = 2;
                                else if (isWearable && !hasSatisfiedSlot) prio = 2;
                                else if (isWritable) prio = 2;
                                else if (isPickup && !pickedBefore) prio = 3;
                                else prio = 4;
                                return (snap: s, d2, prio);
                            })
                            .OrderBy(t => t.prio)
                            .ThenBy(t => t.d2)
                            .Select(t => t.snap)
                            .FirstOrDefault();
                    }

                    // Phase 7f.5 — EXPLORATION FALLBACK. When the
                    // normal picker (within MotionSearchRadius) finds
                    // nothing, the bot would otherwise sit idle until
                    // the observation window closes. That happened in
                    // phase7f4-headless20-fullrun.log: bot killed one
                    // golem, visited the corpse + 4 signs, then
                    // reported inRange=0 from then on (every other
                    // golem was filtered by the now-removed wcid
                    // satisfaction; even with that fix, the bot can
                    // still exhaust everything within 60u in a small
                    // room). The fallback widens the search to ALL
                    // known objects (no radius limit) and ALSO admits
                    // visited doors/portals as re-traversal targets —
                    // walking back through a door we used earlier
                    // crosses cells and re-stimulates server-side
                    // visibility on whatever's on the other side
                    // (which may include the next set of unvisited
                    // signs, NPCs, hostiles, or the academy exit
                    // portal). The wearable / "pickedBefore" filters
                    // still apply so we don't farm respawned apples.
                    if (candidate is null &&
                        string.IsNullOrWhiteSpace(motionTargetNameOverride) &&
                        combatTargetGuid is null)
                    {
                        var allKnown = worldState.Objects.Values
                            .Where(s => s.Guid != self.Guid && !string.IsNullOrEmpty(s.Name))
                            .Where(s => (s.Guid & 0xFF000000u) != 0x50000000u)
                            .Where(s => !(s.WeenieClassId is uint wcSat && satisfiedWeenieClasses.Contains(wcSat)))
                            .Select(s =>
                            {
                                var hasDist = WorldDistance.TrySquaredDistance(self, s, out var d2);
                                var isDoor = string.Equals(s.Name, "Door", StringComparison.OrdinalIgnoreCase);
                                var isPortal = s.ItemType is uint pt && (pt & 0x00010000u) != 0;
                                var isWritable = s.ItemType is uint wt && (wt & 0x00002000u) != 0;
                                var isPickup = s.ItemType is uint it && (it & PickupItemTypeMask) != 0 && !isPortal && !isWritable;
                                var isNpc = s.ItemType is uint nt && (nt & 0x00000010u) != 0 && !isPickup;
                                var isHostile = s.WeenieClassId == HostileCreatureWcidSparringGolem;
                                var isAcademyExit = s.WeenieClassId == AcademyCallingStoneWcid;
                                var isVisited = visitedTargetGuids.Contains(s.Guid);
                                var pickedBefore = pickupCountByName.TryGetValue(s.Name ?? string.Empty, out var pc) && pc > 0;
                                // Exploration priorities (lower = better):
                                //  -1: unvisited Academy Calling Stone (LEAVE!)
                                //   0: unvisited hostile creature (kill for XP/loot)
                                //   1: unvisited NPC (quest giver)
                                //   2: unvisited door/portal (cross to new room)
                                //   3: visited door/portal (BACKTRACK through to re-stimulate cells)
                                //   4: unvisited pickup we haven't farmed (apple)
                                //   5: everything else (mostly filtered out below)
                                int prio;
                                if (!isVisited && isAcademyExit && haveExitToken) prio = -1;
                                else if (!isVisited && isHostile) prio = 0;
                                else if (!isVisited && isNpc) prio = 1;
                                else if (!isVisited && (isDoor || isPortal)) prio = 2;
                                else if (isDoor || isPortal) prio = 3;
                                else if (!isVisited && isPickup && !pickedBefore) prio = 4;
                                else prio = 5;
                                return (snap: s, d2, prio, hasDist);
                            })
                            // Drop the "everything else" bucket entirely to
                            // avoid re-walking to visited signs / corpses /
                            // farmed pickups in the fallback (they were the
                            // exact things that filled visitedTargetGuids).
                            .Where(t => t.prio < 5 && t.hasDist)
                            .OrderBy(t => t.prio)
                            .ThenBy(t => t.d2)
                            .ToList();
                        if (allKnown.Count > 0)
                        {
                            var picked = allKnown[0];
                            candidate = picked.snap;
                            var dist = (float)Math.Sqrt(picked.d2);
                            var visTag = visitedTargetGuids.Contains(candidate.Guid) ? " [BACKTRACK]" : "";
                            Console.WriteLine(
                                $"[motion] EXPLORATION FALLBACK — no candidates in {MotionSearchRadius}u; " +
                                $"picked from {allKnown.Count} known objects: " +
                                $"guid=0x{candidate.Guid:X8} name='{candidate.Name}' prio={picked.prio} dist={dist:F2}u{visTag}");
                        }
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
                    (count - autonomousPositionPacketIndex) >= PostAutonomousPositionGracePackets &&
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
                    motionStartedAt = DateTime.UtcNow;
                    motionLockedCellId = moveCell;
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
                        cellId: moveCell,
                        pos:    moveSelf.Position,
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
                        $"cell=0x{moveCell:X8} xyz=({moveSelf.Position.X:F2},{moveSelf.Position.Y:F2},{moveSelf.Position.Z:F2}) " +
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
                //       MotionStopRadius (race condition: walk-tick
                //       hasn't picked this up yet).
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
                    WorldDistance.TrySquaredDistance(stopProbe, motionTarget, out var d2curr) &&
                    d2curr <= MotionStopRadius * MotionStopRadius)
                {
                    stopByDistance = true;
                    stopByDistanceCurr = (float)Math.Sqrt(d2curr);
                }
                var stopByTimeout = motionStartedAt is DateTime stopStartTs &&
                                    (DateTime.UtcNow - stopStartTs).TotalSeconds > MotionWallClockTimeoutSec;
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

                    var msBuf = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
                    var msLen = GameActionMoveToStateMessage.Pack(
                        msBuf,
                        motion,
                        cellId: stopCell,
                        pos:    stopPos,
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
                        $"cell=0x{stopCell:X8} stopPos=({stopPos.X:F2},{stopPos.Y:F2},{stopPos.Z:F2}) " +
                        $"posSource={(lastSentWaypointPos is null ? "self-snap" : "last-waypoint")} " +
                        $"trigger={trigger} " +
                        $"payload={msLen}B pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");
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
                    useSent = true;
                    useSentAt = DateTime.UtcNow;
                    var itemType = motionTarget.ItemType ?? 0u;
                    var isPickup = (itemType & PickupItemTypeMask) != 0;
                    // Phase 7f — hostile-creature detection. For the
                    // spike we hardcode wcid 12698 (Sparring Golem)
                    // since the academy quest gate is "kill golem,
                    // loot Academy Token". Once combat is working we
                    // can generalize via Tolerance / PlayerKiller /
                    // hostile-faction flags.
                    var isHostile = motionTarget.WeenieClassId == HostileCreatureWcidSparringGolem;
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
                        actionBuf  = new byte[GameActionChangeCombatModeMessage.PackedSize];
                        payloadLen = GameActionChangeCombatModeMessage.Pack(
                            actionBuf,
                            newCombatMode: 2u /* Melee */);
                        fragSeq    = nextOutboundFragmentSequence++;
                        combatBufB = new byte[GameActionTargetedMeleeAttackMessage.PackedSize];
                        combatPayloadLenB = GameActionTargetedMeleeAttackMessage.Pack(
                            combatBufB,
                            targetGuid: motionTarget.Guid,
                            attackHeight: 2u /* Medium */,
                            powerLevel: 1.0f);
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
                    else if (pendingGiveItemGuid is uint giveItemGuid)
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
                    }
                    else
                    {
                        actionName = "USE";
                        actionBuf  = new byte[GameActionUseMessage.PackedSize];
                        payloadLen = GameActionUseMessage.Pack(actionBuf, motionTarget.Guid);
                        fragSeq    = nextOutboundFragmentSequence++;
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
                        }
                        lastCombatAttackAt = DateTime.UtcNow;
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
                        Console.WriteLine(
                            $"[observe]   -> PHASE7F ATTACK: cmd=Melee+TargetedMeleeAttack{qhNote} target=0x{motionTarget.Guid:X8} " +
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
                // wedges (lost echoes, server stall, etc.).
                if (!motionDone &&
                    motionStartedAt is DateTime startedAt &&
                    (DateTime.UtcNow - startedAt).TotalSeconds > MotionWallClockTimeoutSec)
                {
                    motionDone = true;
                    Console.WriteLine($"[motion] walk-tick: wall-clock timeout {MotionWallClockTimeoutSec}s elapsed — stopping");
                }

                if (!motionDone &&
                    moveToStateStartSent && !moveToStateStopSent &&
                    motionTarget is not null &&
                    motionRotation is Quaternion lockedRot &&
                    worldState.Self is WorldObjectSnapshot walkSelf &&
                    walkSelf.CellId is uint walkCell)
                {
                    if (walkCell != motionLockedCellId)
                    {
                        // Cell crossings out of scope for Phase 6b
                        // (target apple is in same cell as bot).
                        motionDone = true;
                        Console.WriteLine($"[motion] walk-tick: self cell changed (was 0x{motionLockedCellId:X8} now 0x{walkCell:X8}) — stopping (cell crossings are Phase 6c work)");
                    }
                    else
                    {
                        var liveTarget = worldState.WithinRadius(walkSelf, 999f)
                            .FirstOrDefault(s => s.Guid == motionTarget.Guid);
                        if (liveTarget is null)
                        {
                            motionDone = true;
                            Console.WriteLine($"[motion] walk-tick: target 0x{motionTarget.Guid:X8} disappeared from world snapshot — stopping");
                        }
                        else
                        {
                            // Step in XY only; preserve self Z so we don't
                            // get flagged by Player_Tick's z-jump hacking
                            // check, and don't try to chase the apple's
                            // floor Z (~0.9) when we're at 0.0.
                            var dx = liveTarget.Position.X - walkSelf.Position.X;
                            var dy = liveTarget.Position.Y - walkSelf.Position.Y;
                            var lenXY = MathF.Sqrt(dx * dx + dy * dy);
                            if (lenXY <= MotionStopRadius)
                            {
                                motionDone = true;
                                Console.WriteLine($"[motion] walk-tick: within stop radius (distXY={lenXY:F2}u <= {MotionStopRadius:F2}u) — stopping");
                            }
                            else if (lenXY < 1e-4f)
                            {
                                motionDone = true;
                                Console.WriteLine($"[motion] walk-tick: target overlaps self in XY (lenXY={lenXY:F4}) — stopping");
                            }
                            else if (lenXY - MotionStopRadius < 0.1f)
                            {
                                // Phase 6n — asymptote failsafe: when
                                // the remaining gap is < 0.1u, server
                                // physics clamps our step so the bot
                                // sits forever sending 0-step APs. Just
                                // call it good and let the USE/PUT fire.
                                // Server's actual UseRadius (0.6u) plus
                                // the target's physics radius is the
                                // real arrival bound; 1.1u is well
                                // inside that for any NPC/door.
                                motionDone = true;
                                Console.WriteLine($"[motion] walk-tick: asymptote reached (distXY={lenXY:F3}u, gap={lenXY - MotionStopRadius:F3}u) — stopping");
                            }
                            else
                            {
                                var dt = WalkTickIntervalMs / 1000f;
                                var stepLen = MathF.Min(WalkSpeedUnitsPerSec * dt, lenXY - MotionStopRadius);
                                var stepX = dx / lenXY * stepLen;
                                var stepY = dy / lenXY * stepLen;
                                var newPos = new Vector3(
                                    walkSelf.Position.X + stepX,
                                    walkSelf.Position.Y + stepY,
                                    walkSelf.Position.Z);

                                var packetSeq = nextOutboundPacketSequence++;
                                var fragSeq   = nextOutboundFragmentSequence++;

                                var apBuf = new byte[GameActionAutonomousPositionMessage.PackedSize];
                                var apLen = GameActionAutonomousPositionMessage.Pack(
                                    apBuf,
                                    cellId: motionLockedCellId,
                                    pos:    newPos,
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
                                Console.WriteLine(
                                    $"[motion] walk-tick #{walkTickAps}: AP " +
                                    $"self=({walkSelf.Position.X:F2},{walkSelf.Position.Y:F2},{walkSelf.Position.Z:F2}) " +
                                    $"intent=({newPos.X:F2},{newPos.Y:F2},{newPos.Z:F2}) " +
                                    $"step=({stepX:F2},{stepY:F2}) stepLen={stepLen:F2}u " +
                                    $"distToTargetXY={lenXY:F2}u pktSeq={packetSeq} fragSeq={fragSeq}");
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
        return new ObserveResult(count, charList, serverName, ddd, characterCreateSent, createResponse,
            enterWorldRequestSent, enterWorldServerReady, enterWorldSent, lastCharacterError, chosenCharacterGuid);
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
        uint ChosenCharacterGuid);

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

    public void Dispose() => _socket?.Dispose();

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
