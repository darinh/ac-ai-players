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
using System.Net;
using System.Net.Sockets;
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
    private const int ObserveSeconds = 180;

    // ConnectResponse retransmit constants — see spec/04-handshake.md
    // "Race condition with server-side bcrypt password verification".
    private const int ConnectResponseRetries = 3;
    private const int ConnectResponseRetryDelayMs = 100;

    private readonly IPEndPoint _serverPort0;
    private readonly IPEndPoint _serverPort1;
    private readonly string _account;
    private readonly string _password;

    private Socket? _socket;

    public HandshakeDriver(IPAddress host, int port, string account, string password)
    {
        _serverPort0 = new IPEndPoint(host, port);
        _serverPort1 = new IPEndPoint(host, port + 1);
        _account = account;
        _password = password;
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
        var ownPlayerSeen = false;
        // Packet index at which LoginComplete was sent; we gate the
        // first AutonomousPosition probe on "saw at least this many
        // more inbound packets after LC" so the server has time to
        // process LoginComplete and clear the Teleporting flag.
        // Without this gate, the AP handler short-circuits on
        // (!Teleporting) and we observe nothing.
        int loginCompletePacketIndex = -1;
        int autonomousPositionPacketIndex = -1;
        int moveToStateStartPacketIndex = -1;
        const int PostLoginCompleteGracePackets = 30;
        const int PostAutonomousPositionGracePackets = 30;
        const int PostMoveStartGracePackets = 40;
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(remaining);
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

                    // Feed the world-state accumulator BEFORE the
                    // logging switch. Apply is a no-op for message
                    // types it doesn't recognize (CharacterList,
                    // ServerName, GameEvent envelopes, etc.) so it's
                    // safe to call unconditionally here.
                    worldState.Apply(decoded);

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
                    var opt = new CharacterCreateMessage.Options(
                        Account: charList.Account,
                        Name:    "Headless01");

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
                if (!loginCompleteSent && ownPlayerSeen && enterWorldSent)
                {
                    loginCompleteSent = true;
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
                    Console.WriteLine($"[observe]   -> PHASE3.4 SEND: GameActionLoginComplete (payload={lcLen}B) pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");
                    Console.WriteLine($"[observe]      Expect: server clears Teleporting flag; character becomes solid (no purple portal haze).");
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

                    var apBuf = new byte[GameActionAutonomousPositionMessage.PackedSize];
                    var apLen = GameActionAutonomousPositionMessage.Pack(
                        apBuf,
                        cellId: selfCell,
                        pos:    self.Position,
                        rot:    self.Rotation,
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
                // walking forward. Wait an additional grace window
                // after AutonomousPosition so the server has applied
                // the position update before we layer motion on top.
                //
                // Per rubber-duck on this phase: WalkForward +
                // CurrentHoldKey=Run gets remapped to RunForward
                // server-side, so we use HoldKey.None to actually
                // walk. Also: Player.OnMoveToState short-circuits on
                // !FastTick (true for our NPK headless char), so we
                // do NOT expect a continuous UpdatePosition stream —
                // only a Motion broadcast back at us. That broadcast
                // alone proves the wire format is accepted.
                if (autonomousPositionSent &&
                    !moveToStateStartSent &&
                    autonomousPositionPacketIndex >= 0 &&
                    (count - autonomousPositionPacketIndex) >= PostAutonomousPositionGracePackets &&
                    worldState.Self is WorldObjectSnapshot moveSelf &&
                    moveSelf.CellId is uint moveCell &&
                    moveCell != 0)
                {
                    moveToStateStartSent = true;
                    moveToStateStartPacketIndex = count;
                    var packetSeq = nextOutboundPacketSequence++;
                    var fragSeq   = nextOutboundFragmentSequence++;

                    var motion = RawMotionStatePayload.ForwardMotion(
                        holdKey: HoldKey.None,
                        stance:  MotionStance.NonCombat,
                        command: MotionCommand.WalkForward,
                        speed:   1.0f);

                    var msBuf = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
                    var msLen = GameActionMoveToStateMessage.Pack(
                        msBuf,
                        motion,
                        cellId: moveCell,
                        pos:    moveSelf.Position,
                        rot:    moveSelf.Rotation,
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
                        $"holdKey=None stance=NonCombat cmd=WalkForward speed=1.0 " +
                        $"cell=0x{moveCell:X8} xyz=({moveSelf.Position.X:F2},{moveSelf.Position.Y:F2},{moveSelf.Position.Z:F2}) " +
                        $"payload={msLen}B pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");
                    Console.WriteLine($"[observe]      Expect: inbound Motion (0xF74C) for our own guid 0x{(worldState.SelfGuid ?? 0):X8}.");
                }

                // Phase 5b — MoveToState STOP. Cancel the forward
                // motion intent. Per rubber-duck: a real client would
                // send a stop on key-release; otherwise the server's
                // recorded intent keeps drifting and the next
                // movement command may be misinterpreted. We send
                // PostMoveStartGracePackets after the START.
                if (moveToStateStartSent &&
                    !moveToStateStopSent &&
                    moveToStateStartPacketIndex >= 0 &&
                    (count - moveToStateStartPacketIndex) >= PostMoveStartGracePackets &&
                    worldState.Self is WorldObjectSnapshot stopSelf &&
                    stopSelf.CellId is uint stopCell &&
                    stopCell != 0)
                {
                    moveToStateStopSent = true;
                    var packetSeq = nextOutboundPacketSequence++;
                    var fragSeq   = nextOutboundFragmentSequence++;

                    var motion = RawMotionStatePayload.Stop(MotionStance.NonCombat);

                    var msBuf = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
                    var msLen = GameActionMoveToStateMessage.Pack(
                        msBuf,
                        motion,
                        cellId: stopCell,
                        pos:    stopSelf.Position,
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
                    Console.WriteLine(
                        $"[observe]   -> PHASE5B STOP: GameActionMoveToState " +
                        $"flags=0x{(uint)motion.Flags:X3} (CurrentHoldKey|CurrentStyle|ForwardCommand) " +
                        $"cmd=Invalid (stop) " +
                        $"cell=0x{stopCell:X8} xyz=({stopSelf.Position.X:F2},{stopSelf.Position.Y:F2},{stopSelf.Position.Z:F2}) " +
                        $"payload={msLen}B pktSeq={packetSeq} fragSeq={fragSeq} totalBytes={sentLen}");
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
                Console.WriteLine("[observe] observation window elapsed");
                break;
            }
        }
        Console.WriteLine($"[observe] total packets observed: {count} (CRC pass={crcPass}, fail={crcFail})");
        Console.WriteLine($"[world]   final: {worldState.FormatSummary()}");
        Console.WriteLine($"[observe] sent: {acksSent} acks, {timeSyncsSent} timesync echoes, characterCreate={characterCreateSent}, enterWorldRequest={enterWorldRequestSent}, enterWorld={enterWorldSent}, loginComplete={loginCompleteSent}, autonomousPosition={autonomousPositionSent}, moveToStateStart={moveToStateStartSent}, moveToStateStop={moveToStateStopSent}");
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
