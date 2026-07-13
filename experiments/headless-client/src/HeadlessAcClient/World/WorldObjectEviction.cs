// SPDX-License-Identifier: AGPL-3.0-or-later
// WorldObjectEviction — the pure block-distance decision for the world-snapshot
// distant-object eviction sweep (WorldState.EvictDistantObjects).
//
// Why this exists: the world snapshot prunes objects only on a server ObjectDelete.
// A fast-exploring bot crosses many landblocks, and the server does not send an
// ObjectDelete for every object that leaves view, so the snapshot grows without
// bound (observed: 127 -> 2513 objects over one exploration run) — bloating memory
// and the per-tick nearest-N scan, and retaining stale far objects the bot can no
// longer see. A generous block-distance sweep bounds the snapshot to a window
// around the bot: an object whose landblock is more than N blocks (Chebyshev) from
// the bot's own landblock is definitively out of view (the server loads only the
// 3x3 landblock group around the player) and is dropped. Pure bit-decode + integer
// distance; the WorldState method applies the preservation rules (self, owned).

using System;

namespace HeadlessAcClient.World;

internal static class WorldObjectEviction
{
    // Config: AC_BOTS_WORLDSTATE_EVICT_BLOCK_RADIUS. An object more than this many
    // landblocks from the bot is evicted. Default 3 keeps the 3x3 visible group plus
    // a two-block margin, so a still-visible object is never dropped. 0 disables the
    // sweep (byte-identical to the prior server-ObjectDelete-only prune).
    internal const int DefaultBlockRadius = 3;

    internal static int ResolveBlockRadius(string? envValue)
    {
        const int Min = 0;   // 0 = disabled
        const int Max = 64;
        if (int.TryParse(envValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= Min)
            return Math.Min(v, Max);
        return DefaultBlockRadius;
    }

    // Landblock (block) X/Y are the top two bytes of an AC CellId
    // (0xXXYYCCCC -> block X = XX, block Y = YY). Matches the driver's own decode.
    internal static int BlockX(uint cellId) => (int)((cellId >> 24) & 0xFF);
    internal static int BlockY(uint cellId) => (int)((cellId >> 16) & 0xFF);

    // Chebyshev landblock distance between two cells (the max of the X/Y block
    // deltas) — the number of landblock "rings" between them.
    internal static int BlockDistance(uint cellA, uint cellB)
        => Math.Max(Math.Abs(BlockX(cellA) - BlockX(cellB)),
                    Math.Abs(BlockY(cellA) - BlockY(cellB)));

    // True iff an object at objCell should be evicted as too-far-stale relative to
    // the bot at selfCell: its landblock is more than blockRadius blocks away. A
    // non-positive blockRadius (disabled) never evicts. The caller has already
    // excluded self, owned items, and objects without a cell.
    internal static bool ShouldEvictByBlockDistance(uint selfCell, uint objCell, int blockRadius)
        => blockRadius > 0 && BlockDistance(selfCell, objCell) > blockRadius;
}
