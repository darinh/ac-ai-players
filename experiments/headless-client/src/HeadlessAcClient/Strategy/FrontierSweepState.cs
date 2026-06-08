// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Anti-tunnel sweep state for the UNDIRECTED outdoor frontier.
//
// The geometry-only outdoor frontier (OutdoorFrontierExplorer.ChooseFrontier
// with no caller heading) prefers a single bearing: its tie-break favors the
// direction pointing away from the visited centroid, which equals the bot's
// current travel direction, so an undirected Explore advances one straight
// line. This holds the small bookkeeping the Motor uses to break that: cycle a
// compass heading through the eight sectors, advancing to the next sector once
// the bot has crossed more than a fixed span of DISTINCT landblocks under the
// current heading. The heading is fed to ChooseFrontier's LOW-precedence
// fallback heading bias (it can never override an explicit heading, a
// remembered-monster steer, or a clearly-more-unexplored or cooled cell), so
// the frontier fans across sectors instead of advancing one bearing.
//
// Pure bookkeeping over the bot's own observed landblock progress and generic
// compass geometry — no game knowledge, no object types, no map data.

using System.Collections.Generic;

namespace HeadlessAcClient.Strategy;

internal sealed class FrontierSweepState
{
    // ~90-degrees-apart fan order so consecutive sweeps probe spread-out
    // directions (not adjacent sectors), covering the compass quickly. Each
    // entry is a heading string TryHeadingVector understands.
    private static readonly string[] Order =
        { "east", "north", "west", "south", "northeast", "northwest", "southwest", "southeast" };

    private readonly HashSet<uint> _landblocksOnHeading = new();
    private readonly int _landblockSpan;
    private int _sector;

    /// <param name="landblockSpan">
    /// Distinct landblocks the bot may cross on one heading before the sweep
    /// rotates to the next sector. Clamped to a minimum of 1.
    /// </param>
    internal FrontierSweepState(int landblockSpan = 4)
    {
        _landblockSpan = landblockSpan < 1 ? 1 : landblockSpan;
    }

    /// <summary>The compass heading the sweep is currently steering toward.</summary>
    internal string CurrentHeading => Order[_sector];

    /// <summary>Index into the fan order (for tests/diagnostics).</summary>
    internal int Sector => _sector;

    /// <summary>
    /// Record the bot's current landblock and return the heading to steer the
    /// aimless frontier this tick. Advances to the next sector once the bot has
    /// crossed more than <c>landblockSpan</c> DISTINCT landblocks under the
    /// current heading, then resets the per-heading landblock set. A zero
    /// landblock (indoor / unknown cell) is ignored so it neither counts toward
    /// the span nor resets progress.
    /// </summary>
    internal string Advance(uint currentLandblock)
    {
        if (currentLandblock != 0u) _landblocksOnHeading.Add(currentLandblock);
        if (_landblocksOnHeading.Count > _landblockSpan)
        {
            _sector = (_sector + 1) % Order.Length;
            _landblocksOnHeading.Clear();
            if (currentLandblock != 0u) _landblocksOnHeading.Add(currentLandblock);
        }
        return Order[_sector];
    }
}
