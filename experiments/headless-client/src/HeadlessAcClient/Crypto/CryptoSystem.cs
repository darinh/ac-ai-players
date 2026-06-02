// SPDX-License-Identifier: AGPL-3.0-or-later
// Copied verbatim from ACEmulator (AGPL3). Upstream:
//   Source/ACE.Common/Cryptography/CryptoSystem.cs
//
// AC's UDP packet "encrypted checksum" scheme isn't really encryption;
// it's an ISAAC keystream XORed with the unencrypted Hash32 checksum.
// The sender derives `xor = isaac.Next()` and writes
// `header.Checksum = (headerChecksum + payloadChecksum) ^ xor`.
//
// Because UDP packets can arrive out of order (or be lost and never
// arrive at all), the receiver maintains a sliding window of pending
// keys — up to MaximumEffortLevel = 256 lookahead — and tries to match
// the recovered `xor` against any key in that window.
//
// On match: ConsumeKey() removes that key (advancing the window if
// it was the head). On mismatch: packet is rejected as a checksum
// failure; the keystream is NOT advanced (the receiver must wait for
// a packet whose key is already in the window).

using System;
using System.Collections.Generic;

namespace HeadlessAcClient.Crypto;

internal sealed class CryptoSystem
{
    public const int MaximumEffortLevel = 256;

    private readonly Isaac _isaac;
    private readonly HashSet<uint> _xors = new();
    private uint _currentKey;

    public CryptoSystem(byte[] seed)
    {
        _isaac = new Isaac(seed);
        _currentKey = _isaac.Next();
    }

    /// <summary>
    /// Drop a key from the window after a successful match. If the
    /// matched key was the head of the stream, advance to the next
    /// ISAAC output. Otherwise just remove it from the lookahead set.
    /// </summary>
    public void ConsumeKey(uint x)
    {
        if (_currentKey == x)
        {
            _currentKey = _isaac.Next();
        }
        else
        {
            _xors.Remove(x);
        }
    }

    /// <summary>
    /// Returns true if `x` is currently in the keystream window
    /// (either at the head or already cached in the lookahead set).
    /// Walks the keystream forward up to MaximumEffortLevel - cached
    /// count slots, caching every passed-over key into the lookahead
    /// set so future out-of-order arrivals can still find them.
    /// </summary>
    public bool Search(uint x)
    {
        if (_currentKey == x)
            return true;
        if (_xors.Contains(x))
            return true;

        var cached = _xors.Count;
        for (var i = 0; i < MaximumEffortLevel - cached; i++)
        {
            _xors.Add(_currentKey);
            ConsumeKey(_currentKey);
            if (_currentKey == x)
                return true;
        }
        return false;
    }

    public uint PeekCurrentKey() => _currentKey;
    public int CachedCount => _xors.Count;
}
