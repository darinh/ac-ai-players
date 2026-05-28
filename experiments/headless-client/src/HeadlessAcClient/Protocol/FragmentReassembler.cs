// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FragmentReassembler — buffers BlobFragments by message-sequence
// and emits the assembled payload once all `Count` fragments have
// arrived (possibly out of order, possibly spread across multiple
// UDP packets).
//
// Mirrors the server's reassembly in
// Source/ACE.Server/Network/NetworkSession.cs:510-547 and
// MessageBuffer.cs (TryGetMessage sorts by Index, concatenates Data).
//
// Wire-protocol facts that drive this design (verified via
// docs/research/headless-client/phase4-objectcreate-run-08.log):
//   1. A single logical game message can be split across N fragments
//      when its serialized size exceeds the MTU budget for one
//      BlobFragments packet (Pilot-01 ObjectCreate with velocity
//      goes from ~444B single-fragment to 448B + 36B two-fragment).
//   2. Fragments share `(Sequence, Id, Count)`. Index runs 0..Count-1.
//      Sequence is the per-message sequence; Id appears to be a
//      group identifier (0x80000000 = GameMessageGroup.WorldBroadcast).
//   3. Fragments may arrive in any order. In run-08 the Idx=1 frag
//      for Seq=24 arrived before Idx=0 (different UDP packets,
//      different receive instants). Reassembly MUST tolerate this.
//   4. The opcode (first u32 of the assembled payload) lives in
//      fragment Idx=0 only. Decoding any other fragment as a
//      standalone game message yields garbage (or null when
//      PeekOpcode returns an unknown value).
//
// This class is NOT thread-safe; it is intended for use from the
// single-threaded handshake receive loop. If we ever move to a
// multi-threaded receive path, wrap _buffers access in a lock.

using System;
using System.Collections.Generic;

namespace HeadlessAcClient.Protocol;

internal sealed class FragmentReassembler
{
    // Sanity caps. ACE's MaxPacketSize is ~464B and the largest game
    // messages we've observed (full character description bursts) split
    // into a handful of fragments — never tens, let alone hundreds.
    // These caps bound the worst-case memory exposure if a packet with
    // a corrupted Count somehow gets past CRC, or if the network drops
    // many fragments over a long session and incomplete buffers pile up.
    internal const int MaxFragmentsPerMessage = 64;
    internal const int MaxInFlightMessages = 64;

    private readonly Dictionary<uint, MessageBuffer> _buffers = new();

    /// <summary>
    /// Accept one fragment. Returns the assembled payload if this
    /// fragment completes its message (always for Count==1, or for
    /// the final-arriving fragment of a Count&gt;1 message).
    /// Returns null when more fragments are still expected.
    /// Returns null and logs to stderr when the fragment is rejected
    /// for exceeding a sanity cap.
    /// </summary>
    /// <remarks>
    /// The returned array is owned by the caller; the reassembler
    /// retains no reference to it.
    /// </remarks>
    public byte[]? Add(in PacketFragmentHeader header, ReadOnlyMemory<byte> data)
    {
        if (header.Count == 0)
        {
            Console.Error.WriteLine(
                $"[reassembler] dropping fragment with Count=0 for Sequence={header.Sequence}");
            return null;
        }

        if (header.Count > MaxFragmentsPerMessage)
        {
            Console.Error.WriteLine(
                $"[reassembler] dropping fragment with Count={header.Count} > cap " +
                $"({MaxFragmentsPerMessage}) for Sequence={header.Sequence}");
            return null;
        }

        if (header.Count == 1)
        {
            // Single-fragment fast path — no need to allocate a buffer.
            var single = new byte[data.Length];
            data.CopyTo(single);
            return single;
        }

        if (!_buffers.TryGetValue(header.Sequence, out var buffer))
        {
            if (_buffers.Count >= MaxInFlightMessages)
            {
                // Cap reached. Evict the oldest in-flight message by
                // Sequence (server sequences are monotonically
                // increasing per message, so the lowest-Sequence
                // buffer is necessarily the staleest). Without this,
                // a single missing fragment early in the session
                // would permanently consume a slot and 64 such events
                // would lock out all future multi-fragment messages.
                uint stale = uint.MaxValue;
                foreach (var k in _buffers.Keys)
                    if (k < stale) stale = k;
                _buffers.Remove(stale);
                Console.Error.WriteLine(
                    $"[reassembler] evicting stale in-flight Seq={stale} to admit Seq={header.Sequence} " +
                    $"(in-flight cap {MaxInFlightMessages} reached; likely a fragment was dropped on the wire).");
            }
            buffer = new MessageBuffer(header.Sequence, header.Count);
            _buffers[header.Sequence] = buffer;
        }

        if (!buffer.AddFragment(header.Index, data))
            return null;

        if (!buffer.IsComplete)
            return null;

        _buffers.Remove(header.Sequence);
        return buffer.Assemble();
    }

    /// <summary>
    /// Count of in-flight multi-fragment messages awaiting completion.
    /// Exposed for diagnostics / leak detection. A long-lived session
    /// with non-zero here probably means a dropped fragment.
    /// </summary>
    public int InFlightCount => _buffers.Count;

    private sealed class MessageBuffer
    {
        private readonly ReadOnlyMemory<byte>[] _slots;

        public uint Sequence { get; }
        public ushort TotalCount { get; }
        public int Received { get; private set; }

        public MessageBuffer(uint sequence, ushort totalCount)
        {
            Sequence = sequence;
            TotalCount = totalCount;
            _slots = new ReadOnlyMemory<byte>[totalCount];
        }

        public bool IsComplete => Received == TotalCount;

        /// <summary>
        /// Returns true if the fragment was stored, false if it was
        /// rejected (out-of-range index or duplicate). Never throws —
        /// the receive loop is the boundary for malformed-but-CRC-
        /// valid packets and one bad packet must not kill the loop.
        /// </summary>
        public bool AddFragment(ushort index, ReadOnlyMemory<byte> data)
        {
            if (index >= TotalCount)
            {
                Console.Error.WriteLine(
                    $"[reassembler] dropping fragment with Index={index} >= Count={TotalCount} " +
                    $"for Sequence={Sequence}");
                return false;
            }

            // Duplicate fragment — server resent. Drop silently
            // (mirrors MessageBuffer.cs:26 `!fragments.All(...)`).
            if (!_slots[index].IsEmpty)
                return false;

            // Copy out: caller's backing buffer is the recv buffer
            // which is reused on the next UDP read.
            var copy = new byte[data.Length];
            data.CopyTo(copy);
            _slots[index] = copy;
            Received++;
            return true;
        }

        public byte[] Assemble()
        {
            var totalLength = 0;
            for (var i = 0; i < TotalCount; i++)
                totalLength += _slots[i].Length;

            var assembled = new byte[totalLength];
            var offset = 0;
            for (var i = 0; i < TotalCount; i++)
            {
                _slots[i].CopyTo(assembled.AsMemory(offset));
                offset += _slots[i].Length;
            }
            return assembled;
        }
    }
}
