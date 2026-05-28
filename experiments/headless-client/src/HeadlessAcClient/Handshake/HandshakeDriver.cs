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

namespace HeadlessAcClient.Protocol;

internal sealed class HandshakeDriver : IDisposable
{
    private const int RecvBufferSize = 1024;
    private const int ClientVersion = 1802;
    private const int ObserveSeconds = 65;

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
                observe.DDDInterrogation);
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
        // Phase 3.1 probe: send one unknown-opcode BlobFragments packet after
        // CharacterList arrives. Server's InboundMessageManager.cs:115-118 logs
        // "Received unhandled fragment opcode: 0xFFFE" and continues. Proves the
        // encrypted-fragment path works end-to-end without committing to a real
        // game message yet.
        const uint UnknownProbeOpcode = 0xFFFE;
        var probeSent = false;

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
                foreach (var (fh, data) in pkt.Fragments)
                {
                    Console.WriteLine($"[observe]   {fh} payload[{data.Length}]: {Hex(data.AsSpan(0, Math.Min(data.Length, 32)))}{(data.Length > 32 ? " ..." : "")}");

                    var decoded = GameMessageDecoder.Decode(data);
                    var opcode = GameMessageDecoder.PeekOpcode(data);
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
                        case null when opcode is not null:
                            Console.WriteLine($"[observe]   -> opcode 0x{(uint)opcode.Value:X4} (no decoder yet)");
                            break;
                    }
                }

                if (!verified)
                    continue;

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

                // Phase 3.1 acceptance probe: once we know we're in AuthConnected
                // state (CharacterList means the server is past the AuthConnected
                // transition - see Handlers/CharacterHandler.cs:26-27), send a
                // single encrypted BlobFragments packet carrying an unknown
                // opcode. Server's InboundMessageManager.cs:117 logs a warning
                // and keeps the session alive. Confirms the outbound encryption
                // chain works end-to-end without committing to a real game
                // message yet.
                if (!probeSent && charList is not null)
                {
                    probeSent = true;
                    var packetSeq = nextOutboundPacketSequence++;
                    var fragSeq   = nextOutboundFragmentSequence++;

                    var payload = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, UnknownProbeOpcode);

                    var probe = new OutboundPacket();
                    // Piggyback the latest inbound ack so the server's
                    // retransmit pressure stays low.
                    if (lastReceivedSeq != 0)
                        probe.AddAckSequence(lastReceivedSeq);
                    probe.AddBlobFragment(
                        fragSequence: fragSeq,
                        fragId: OutboundFragmentId,
                        queue: (ushort)GameMessageGroup.UIQueue,
                        gameMessagePayload: payload);

                    var probeLen = probe.Pack(sendBuf, myClientId,
                                              sequence: packetSeq, iteration: 1,
                                              encrypt: true, cryptoSend: cryptoSend);
                    await _socket!.SendToAsync(new ArraySegment<byte>(sendBuf, 0, probeLen),
                                               SocketFlags.None, _serverPort0, ct).ConfigureAwait(false);
                    Console.WriteLine($"[observe]   -> PHASE3.1 PROBE: sent encrypted BlobFragments(opcode=0x{UnknownProbeOpcode:X4}, pktSeq={packetSeq}, fragSeq={fragSeq}) [{probeLen} bytes]");
                    Console.WriteLine($"[observe]      Check C:\\ACE\\Logs\\ACE_Log.txt for: \"Received unhandled fragment opcode: 0x{UnknownProbeOpcode:X4}\"");
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[observe] observation window elapsed");
                break;
            }
        }
        Console.WriteLine($"[observe] total packets observed: {count} (CRC pass={crcPass}, fail={crcFail})");
        Console.WriteLine($"[observe] sent: {acksSent} acks, {timeSyncsSent} timesync echoes");
        return new ObserveResult(count, charList, serverName, ddd, probeSent);
    }

    private readonly record struct ObserveResult(
        int PacketCount,
        CharacterListMessage? CharacterList,
        ServerNameMessage? ServerName,
        DDDInterrogationMessage? DDDInterrogation,
        bool ProbeSent);

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
    DDDInterrogationMessage? DDDInterrogation);
