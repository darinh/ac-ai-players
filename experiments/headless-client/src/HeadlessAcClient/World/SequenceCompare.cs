// SPDX-License-Identifier: AGPL-3.0-or-later
// Wrap-aware u16 / u8 sequence comparison. Matches the server-
// side semantics of ACE.Server.Network.Sequence.UShortSequence
// and ByteSequence — the per-object sequence counters in
// ObjectCreate / UpdatePosition / Motion / SetState are u16 and
// wrap from 65535 to 0; the property-update family uses a u8
// sequence that wraps from 255 to 0.
//
// A naive `incoming > current` check breaks across the wrap
// boundary (drops valid updates after wrap, accepts stale ones
// after going backward). The forward-distance trick treats the
// counter space as cyclic: an update is "current or newer" if
// it's within half the range forward of the current value.

namespace HeadlessAcClient.World;

internal static class SequenceCompare
{
    /// <summary>
    /// Half the u16 range. Updates more than this far "ahead"
    /// of the current value are treated as wrap-around stale.
    /// </summary>
    public const ushort UShortWrapWindow = 32768;

    /// <summary>Half the u8 range.</summary>
    public const byte ByteWrapWindow = 128;

    /// <summary>
    /// Returns true if <paramref name="incoming"/> is at least
    /// as recent as <paramref name="current"/>. Equal values are
    /// accepted (server may redundantly re-send the same
    /// sequence and the update is idempotent).
    /// </summary>
    public static bool IsCurrentOrNewer(ushort incoming, ushort current)
    {
        var forward = (ushort)(incoming - current);
        return forward < UShortWrapWindow;
    }

    /// <summary>
    /// Nullable overload — null current means "never seen", so
    /// every incoming value is accepted.
    /// </summary>
    public static bool IsCurrentOrNewer(ushort incoming, ushort? current)
        => current is not ushort cur || IsCurrentOrNewer(incoming, cur);

    /// <summary>
    /// Returns true if <paramref name="incoming"/> is strictly
    /// newer than <paramref name="current"/>, wrap-aware.
    /// </summary>
    public static bool IsStrictlyNewer(ushort incoming, ushort current)
    {
        var forward = (ushort)(incoming - current);
        return forward != 0 && forward < UShortWrapWindow;
    }

    /// <summary>Byte-sequence variant for the property-update family.</summary>
    public static bool IsCurrentOrNewer(byte incoming, byte current)
    {
        var forward = (byte)(incoming - current);
        return forward < ByteWrapWindow;
    }

    /// <summary>Nullable byte overload.</summary>
    public static bool IsCurrentOrNewer(byte incoming, byte? current)
        => current is not byte cur || IsCurrentOrNewer(incoming, cur);
}
