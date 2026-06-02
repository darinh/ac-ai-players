// SPDX-License-Identifier: AGPL-3.0-or-later
// Copied verbatim from ACEmulator (AGPL3). Upstream:
//   Source/ACE.Common/Cryptography/ISAAC.cs
// Original copyright: ACEmulator Contributors. Preserved here for
// license-inheritance purposes. If this spike is ever extracted
// for separate distribution, replace this file with a clean-room
// implementation derived from public ISAAC documentation.

using System;

namespace HeadlessAcClient.Crypto;

internal class Isaac
{
    private uint _offset;
    private uint _a, _b, _c;
    private readonly uint[] _mm;
    private readonly uint[] _randRsl;

    public Isaac(byte[] seed)
    {
        _mm = new uint[256];
        _randRsl = new uint[256];
        _offset = 255u;
        Initialize(seed);
    }

    public uint Next()
    {
        var value = _randRsl[_offset];
        if (_offset > 0)
            _offset--;
        else
        {
            Scramble();
            _offset = 255u;
        }
        return value;
    }

    private void Initialize(byte[] keyBytes)
    {
        for (var i = 0; i < 256; i++)
            _mm[i] = _randRsl[i] = 0;

        var abcdefgh = new uint[8];
        for (var i = 0; i < 8; i++)
            abcdefgh[i] = 0x9E3779B9;

        for (var i = 0; i < 4; i++)
            Shuffle(abcdefgh);

        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < 256; j += 8)
            {
                for (var k = 0; k < 8; k++)
                    abcdefgh[k] += (i < 1) ? _randRsl[j + k] : _mm[j + k];

                Shuffle(abcdefgh);

                for (var k = 0; k < 8; k++)
                    _mm[j + k] = abcdefgh[k];
            }
        }

        _a = BitConverter.ToUInt32(keyBytes, 0);
        _c = _b = _a;

        Scramble();
    }

    private void Scramble()
    {
        _b += ++_c;
        for (var i = 0; i < 256; i++)
        {
            var x = _mm[i];
            switch (i & 3)
            {
                case 0: _a ^= (_a << 0x0D); break;
                case 1: _a ^= (_a >> 0x06); break;
                case 2: _a ^= (_a << 0x02); break;
                case 3: _a ^= (_a >> 0x10); break;
            }
            _a += _mm[(i + 128) & 0xFF];
            uint y;
            _mm[i] = y = _mm[(int)(x >> 2) & 0xFF] + _a + _b;
            _randRsl[i] = _b = _mm[(int)(y >> 10) & 0xFF] + x;
        }
    }

    private static void Shuffle(uint[] x)
    {
        x[0] ^= x[1] << 0x0B; x[3] += x[0]; x[1] += x[2];
        x[1] ^= x[2] >> 0x02; x[4] += x[1]; x[2] += x[3];
        x[2] ^= x[3] << 0x08; x[5] += x[2]; x[3] += x[4];
        x[3] ^= x[4] >> 0x10; x[6] += x[3]; x[4] += x[5];
        x[4] ^= x[5] << 0x0A; x[7] += x[4]; x[5] += x[6];
        x[5] ^= x[6] >> 0x04; x[0] += x[5]; x[6] += x[7];
        x[6] ^= x[7] << 0x08; x[1] += x[6]; x[7] += x[0];
        x[7] ^= x[0] >> 0x09; x[2] += x[7]; x[0] += x[1];
    }
}
