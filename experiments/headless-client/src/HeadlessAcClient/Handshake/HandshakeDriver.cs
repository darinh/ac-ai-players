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
        // nav/, replacing the per-landblock NavGraphRecorder JSON
        // files). The graph is global across sessions, characters
        // and accounts; the LLM/planner can query it for routes.
        var navGraph = new NavGraph();
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
        Quaternion?          motionRotation = null;
        float?               motionInitialDistance = null;
        DateTime?            motionStartedAt = null;
        DateTime?            motionStoppedAt = null;
        DateTime             nextWalkTickAt = DateTime.UtcNow;
        Vector3?             lastSentWaypointPos = null;
        int                  walkTickAps = 0;
        // Slice S — blocked-motion detection state. Tracked across
        // walk ticks within a single motion lock; reset whenever the
        // lock is released (see ResetMotion block).
        Vector3?             prevSelfBeforeAp = null;
        float                prevExpectedStepLen = 0f;
        int                  consecutiveBlockedTicks = 0;
        uint                 motionLockedCellId = 0;
        bool                 motionDone = false;
        bool                 useSent = false;
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
        // Door-USE dispatch tracking: per-door cooldown so we don't
        // spam USE on the same door every walk-tick while waiting for
        // it to open. Keyed by door object guid; value is the wall-
        // clock tick we last dispatched USE.
        var doorUseDispatchedAt = new Dictionary<uint, DateTime>();
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
                            // EntityKind is left Unknown for now —
                            // wcid + name is enough for FindRouteToEntity
                            // lookup; future work can infer the kind
                            // from the weenie ObjectType bits.
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
                                lastObservedSelfLandblock is uint selfLb &&
                                (lmPos.LandblockId & 0xFFFF0000u) == selfLb)
                            {
                                navGraph.RecordObservation(
                                    lastVisitNodeId,
                                    oc.Weenie.WeenieClassId,
                                    oc.Weenie.Name!,
                                    new System.Numerics.Vector3(lmPos.X, lmPos.Y, lmPos.Z),
                                    EntityKind.Unknown,
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
                                // more LLM-call windows.
                                if (worldState.TryGet(putAck.ItemGuid) is { } putSnap)
                                {
                                    putSnap.ContainerGuid = putAck.ContainerGuid;
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
                    var landblockChanged = false;
                    if (lastObservedSelfLandblock is uint prevLb)
                    {
                        if (lb != prevLb && loginCompleteSent)
                        {
                            Console.WriteLine(
                                $"[teleport] mid-session landblock change " +
                                $"0x{prevLb:X8} -> 0x{lb:X8}; queueing " +
                                $"LoginComplete resend to clear Teleporting.");
                            loginCompleteResendNeeded = true;
                            // M1.6 — surface to EventStream so the
                            // LLM policy re-deliberates on the new
                            // landblock (clears any stale "use
                            // calling stone" goal that was tied to
                            // the previous map).
                            eventStream.Append(new StreamEvent
                            {
                                Sequence = 0,
                                Utc = DateTimeOffset.UtcNow,
                                Kind = EventKind.LandblockChanged,
                                LandblockFrom = prevLb,
                                LandblockTo = lb,
                            });
                            // Commit B — the inter-landblock edge is
                            // recorded below (after we have created the
                            // arrival node via RecordVisit). Setting
                            // landblockChanged here drives the post-
                            // RecordVisit RecordEdge that joins the
                            // pre-teleport node to the arrival node.
                            landblockChanged = true;
                        }
                    }
                    var prevNodeId = lastVisitNodeId;
                    var lcTeleSelfPos = new System.Numerics.Vector3(
                        lcTeleSelf.Position.X,
                        lcTeleSelf.Position.Y,
                        lcTeleSelf.Position.Z);
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
                        // Best-effort kind: we don't (yet) know whether
                        // the teleport came from an item (Calling Stone),
                        // a world portal, an NPC trigger, or a spell.
                        // UsedPortal is a safe default — fixed cost, same
                        // semantics as a portal, doesn't poison the A*
                        // heuristic. Future work: plumb the LLM-issued
                        // goal's selected item/portal name through and
                        // pass it as useItemName / useObjectGuid.
                        //
                        // Executor contract (see ac-ai-players#75): an
                        // edge with useItemName == null AND useObjectGuid
                        // == null is observational only. The future path
                        // executor must treat such edges as a hint that
                        // a transition happens here, then ask the LLM
                        // for an action to dispatch. A* may include the
                        // edge in a route; the executor stops at the
                        // edge boundary and re-deliberates.
                        try
                        {
                            navGraph.RecordEdge(
                                prevNodeId,
                                lastVisitNodeId,
                                NavEdgeKind.UsedPortal,
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

                    // Notify Tactics that the current goal completed.
                    // Used by LlmGoalPolicy to surface a GoalCompleted
                    // event on the EventStream and re-deliberate on
                    // the next ProposeGoal call.
                    if (motionTarget is not null)
                        tactics.Clear($"action cycle done on '{motionTarget.Name}'", eventStream);

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
                    // Slice S — clear blocked-motion bookkeeping so
                    // a fresh lock starts with a clean slate (the
                    // previous lock may have ended in a "stuck on
                    // wall" state we don't want to inherit).
                    prevSelfBeforeAp = null;
                    prevExpectedStepLen = 0f;
                    consecutiveBlockedTicks = 0;
                    // Phase 3.1 — wipe the indoor-path cache so the
                    // next motion lock plans a fresh path from the
                    // bot's new position.
                    motionIndoorPath = null;
                    motionIndoorPathIndex = 0;
                    motionIndoorPathCells = null;
                    motionIndoorPathAttempted = false;
                    pendingGiveItemGuid = null;
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
                // after PostActionCooldownSec, marking the loot
                // item visited and clearing motionTarget so the
                // next pre-emptor iteration picks the NEXT item.
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
                                // fires after PostActionCooldownSec.
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
                    var projection = WorldStateProjection.FromWorldState(
                        worldState, weenies, visibleRadius: 60f, maxVisible: 32);
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
                    if (goal is not null && goal.Kind == GoalKind.Explore)
                    {
                        // Slice W.2 (#87) — if the LLM emitted
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
                         goal.Kind == GoalKind.Pickup))
                    {
                        var targetSnap = tactics.ResolveTarget(worldState, tacticsSelf);
                        WorldObjectSnapshot? itemSnap = null;
                        if (goal.Kind == GoalKind.Give)
                        {
                            itemSnap = tactics.ResolveItem(worldState);
                            // Give requires the item to be in our
                            // inventory. Resolver does not filter on
                            // container; do that here.
                            if (itemSnap is not null &&
                                !(itemSnap.ContainerGuid is uint icg && icg == tacticsSelf.Guid))
                            {
                                itemSnap = null;
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
                        if (goal.Kind == GoalKind.Use &&
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
                                (goal.Kind != GoalKind.Give || itemSnap is not null) &&
                                // Spatial actions require the target
                                // to actually have a CellId. An NPC /
                                // creature without a position cannot
                                // be walked-to.
                                (targetSnap is null ||
                                 (targetSnap.CellId is uint tc && tc != 0u));

                            if (!actionable)
                            {
                                Console.WriteLine(
                                    $"[strategy] goal {goal.Kind} unresolved -- " +
                                    $"target={(targetSnap is null ? "MISS" : "ok")} " +
                                    $"item={(goal.Kind == GoalKind.Give ? (itemSnap is null ? "MISS" : "ok") : "n/a")}; " +
                                    $"selector target={goal.Target} item={goal.Item}; " +
                                    $"clearing and falling through to picker");
                                tactics.Fail("selector resolved to no live object", eventStream);
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
                                if (goal.Kind == GoalKind.Give)
                                    pendingGiveItemGuid = itemSnap!.Guid;
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
                                    $"sent AP yaw={yaw:F3}rad pktSeq={apPacketSeq} fragSeq={apFragSeq} bytes={apSent}");
                            }
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
                            .Where(s => !(s.WeenieClassId is uint wcSat && satisfiedWeenieClasses.Contains(wcSat)))
                            .ToList();

                        var ranked = PickerSelection.EnumerateFallbackCandidates(
                            fallbackPool,
                            self,
                            chosenCharacterGuid,
                            selfLandblock,
                            pickupCountByName,
                            PickupItemTypeMask).ToList();

                        if (ranked.Count > 0)
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
                            }).ToList();
                            explorationCandidatesForLlm = top;
                        }
                    }
                    // Publish (or clear) the candidate surface every
                    // tick — stale lists are worse than empty ones
                    // because the LLM may emit Explore{target} for
                    // a candidate that no longer applies.
                    llmPolicyForPickerSurface?.SetCurrentExplorationCandidates(explorationCandidatesForLlm);

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
                    else if (lockedGoalKind == GoalKind.Use || lockedGoalKind == GoalKind.Talk)
                    {
                        actionName = "USE";
                        actionBuf  = new byte[GameActionUseMessage.PackedSize];
                        payloadLen = GameActionUseMessage.Pack(actionBuf, motionTarget.Guid);
                        fragSeq    = nextOutboundFragmentSequence++;

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
                // wedges (lost echoes, server stall, etc.).
                if (!motionDone &&
                    motionStartedAt is DateTime startedAt &&
                    (DateTime.UtcNow - startedAt).TotalSeconds > MotionWallClockTimeoutSec)
                {
                    motionDone = true;
                    Console.WriteLine($"[motion] walk-tick: wall-clock timeout {MotionWallClockTimeoutSec}s elapsed — stopping");
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

                    if (walkCell != motionLockedCellId)
                    {
                        // Phase 3.1 — if we're following a multi-cell
                        // indoor path AND the new cell is one the
                        // planner expected to traverse, slide the
                        // motion-lock forward instead of stopping.
                        // The bot is exactly where we wanted it; the
                        // remaining waypoints + the final approach
                        // logic stay valid.
                        if (motionIndoorPathCells is not null &&
                            motionIndoorPathCells.Contains(walkCell))
                        {
                            Console.WriteLine(
                                $"[motion] walk-tick: indoor-path cell crossing " +
                                $"0x{motionLockedCellId:X8} -> 0x{walkCell:X8} (expected by planner; continuing)");
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

                    if (!motionDone && walkCell == motionLockedCellId)
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
                            var moveDx = walkSelf.Position.X - prevSelf.X;
                            var moveDy = walkSelf.Position.Y - prevSelf.Y;
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
                            else
                            {
                                // Healthy progress — reset the counter.
                                consecutiveBlockedTicks = 0;
                            }
                        }

                        var liveTarget = motionDone
                            ? null
                            : worldState.WithinRadius(walkSelf, 999f)
                                .FirstOrDefault(s => s.Guid == motionTarget.Guid);
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
                            bool aimingAtWaypoint = false;
                            if (!motionDone &&
                                motionIndoorPath is not null &&
                                motionIndoorPathIndex < motionIndoorPath.Count - 1)
                            {
                                stepTargetPos = motionIndoorPath[motionIndoorPathIndex].Position;
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
                            var dx = stepTargetPos.X - walkSelf.Position.X;
                            var dy = stepTargetPos.Y - walkSelf.Position.Y;
                            var lenXY = MathF.Sqrt(dx * dx + dy * dy);

                            // Phase 3.1 — intermediate-waypoint advance.
                            // When chasing a waypoint (not the final
                            // target), use a looser 1.5u XY threshold;
                            // we don't need to be on top of it, just
                            // close enough that the next waypoint is a
                            // reasonable continuation. The terminal
                            // stop radius (1.0u) only applies to the
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
                            else if (!aimingAtWaypoint && lenXY <= MotionStopRadius)
                            {
                                motionDone = true;
                                Console.WriteLine($"[motion] walk-tick: within stop radius (distXY={lenXY:F2}u <= {MotionStopRadius:F2}u) — stopping");
                            }
                            else if (!aimingAtWaypoint && lenXY < 1e-4f)
                            {
                                motionDone = true;
                                Console.WriteLine($"[motion] walk-tick: target overlaps self in XY (lenXY={lenXY:F4}) — stopping");
                            }
                            else if (!aimingAtWaypoint && lenXY - MotionStopRadius < 0.1f)
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
                                // When aiming at an intermediate
                                // waypoint, we don't reserve the
                                // MotionStopRadius — the waypoint
                                // itself is a valid floor sample to
                                // stand on.
                                var maxStep = aimingAtWaypoint
                                    ? lenXY
                                    : lenXY - MotionStopRadius;
                                var stepLen = MathF.Min(WalkSpeedUnitsPerSec * dt, maxStep);
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
                                // Slice S — remember what we were BEFORE
                                // this AP, and how big a step we asked
                                // for. Next tick's blocked-detector
                                // compares (current walkSelf - prevSelf)
                                // against prevExpectedStepLen.
                                prevSelfBeforeAp    = walkSelf.Position;
                                prevExpectedStepLen = stepLen;
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
