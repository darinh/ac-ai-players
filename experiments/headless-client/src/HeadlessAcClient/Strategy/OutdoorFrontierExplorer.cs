// SPDX-License-Identifier: AGPL-3.0-or-later
//
// OutdoorFrontierExplorer — autonomous OUTDOOR spatial search, the
// surface-world analogue of IndoorFrontierExplorer.
//
// Problem it solves: a bot standing in an open-air town with no monsters
// or fresh quests left emits the targetless goal Explore{anywhere} (a
// "go discover new areas" directive from the LLM). The HandshakeDriver
// Explore pre-emptor has an INDOOR frontier explorer (walk to the nearest
// unentered navmesh cell) but OUTDOORS it had only a naive fallback:
// "walk to the farthest visible object within 200u". In a town the
// farthest visible object is just another civilian NPC, so the bot mills
// among townsfolk and never heads for the landblock edge / open country
// where huntable monsters live.
//
// This class answers the content-free question "which outdoor direction
// is least-explored?" using ONLY:
//   * the bot's own recorded visited positions (global coords), and
//   * pure 2-D geometry (compass-sector candidates around the bot).
// It never inspects object names, wcids, item types, quest state, or
// landblock identities, and never decides to INTERACT with anything — it
// only proposes WHERE to move so new ground (and whatever the world puts
// there) can be perceived. The decision of WHAT to seek stays with the
// LLM goal; crossing-into-new-territory is mechanical "how".
//
// All math is in GLOBAL (Dereth-frame) meters so candidates that fall in
// an adjacent landblock are scored and addressed correctly (mixing a
// new-landblock cell id with an old-landblock-local coordinate is the
// classic seam bug). The caller converts the chosen global point back to
// the destination cell's local frame and rasterizes the straight segment
// into the motor's cell-slide set.

namespace HeadlessAcClient.Strategy;

internal static class OutdoorFrontierExplorer
{
    /// <summary>One recorded place the bot has stood, in global meters,
    /// with the wall-clock time it was last seen (for recency weighting).
    /// A lightweight projection of a NavNode so this stays unit-testable
    /// without constructing a graph.</summary>
    internal readonly record struct VisitedSample(
        float GlobalX, float GlobalY, DateTimeOffset LastSeenUtc);

    /// <summary>The chosen outward destination: a global point, its
    /// outdoor surface cell, and which of the 8 compass sectors it lies
    /// in (0 = +X/east, increasing counter-clockwise by 45°).</summary>
    internal readonly record struct FrontierResult(
        float GlobalX, float GlobalY, uint DestCellId, int Sector);

    internal const int SectorCount = 8;

    /// <summary>
    /// Pick the least-explored outward direction from <paramref
    /// name="selfGlobalX"/>/<paramref name="selfGlobalY"/>.
    ///
    /// Generates <see cref="SectorCount"/> candidate points one
    /// <paramref name="stepMeters"/> away (one per 45° compass sector),
    /// drops any whose destination cell is on cooldown, and scores the
    /// rest by distance to the NEAREST recent/local visited sample
    /// (farther from where the bot has been = more unexplored = better).
    /// Ties break toward the bearing pointing away from the visited
    /// centroid, so even with no usable reference samples the choice is
    /// deterministic and sensible.
    ///
    /// Returns null only when every candidate cell is on cooldown.
    /// </summary>
    /// <param name="visited">The bot's own recorded positions (global m).</param>
    /// <param name="cooledCells">Destination cells suppressed (recently
    /// targeted or known-blocked) — skipped.</param>
    /// <param name="nowUtc">Wall clock for recency filtering.</param>
    /// <param name="stepMeters">How far out to probe per pick.</param>
    /// <param name="localityRadius">Only visited samples within this many
    /// meters of the bot inform the choice (keeps the centroid local, not
    /// a stale all-world average).</param>
    /// <param name="recencyWindow">Prefer samples seen within this window;
    /// if none qualify, fall back to any in-locality sample regardless of
    /// age.</param>
    internal static FrontierResult? ChooseFrontier(
        float selfGlobalX,
        float selfGlobalY,
        IReadOnlyList<VisitedSample> visited,
        IReadOnlySet<uint> cooledCells,
        DateTimeOffset nowUtc,
        float stepMeters,
        float localityRadius,
        TimeSpan recencyWindow)
    {
        // Build the reference set: visited samples that are both LOCAL
        // (near the bot) and RECENT. Relax to any-age local samples if
        // recency leaves nothing, so a bot returning to an old town still
        // gets a sensible "away from where I've been" pull.
        var localitySq = localityRadius * localityRadius;
        var recentLocal = new List<VisitedSample>();
        var anyLocal = new List<VisitedSample>();
        foreach (var v in visited)
        {
            var dx = v.GlobalX - selfGlobalX;
            var dy = v.GlobalY - selfGlobalY;
            if (dx * dx + dy * dy > localitySq) continue;
            anyLocal.Add(v);
            if (nowUtc - v.LastSeenUtc <= recencyWindow)
                recentLocal.Add(v);
        }
        var refs = recentLocal.Count > 0 ? recentLocal : anyLocal;

        // Centroid of the reference set (for the tie-break "away" bearing).
        float cx = 0f, cy = 0f;
        if (refs.Count > 0)
        {
            foreach (var v in refs) { cx += v.GlobalX; cy += v.GlobalY; }
            cx /= refs.Count; cy /= refs.Count;
        }
        var awayDx = selfGlobalX - cx;
        var awayDy = selfGlobalY - cy;
        var awayLen = MathF.Sqrt(awayDx * awayDx + awayDy * awayDy);
        bool haveAway = refs.Count > 0 && awayLen > 1e-3f;
        if (haveAway) { awayDx /= awayLen; awayDy /= awayLen; }

        FrontierResult? best = null;
        float bestScore = float.NegativeInfinity;
        float bestTie = float.NegativeInfinity;

        for (int k = 0; k < SectorCount; k++)
        {
            var angle = k * (2f * MathF.PI / SectorCount);
            var dirX = MathF.Cos(angle);
            var dirY = MathF.Sin(angle);
            var gx = selfGlobalX + dirX * stepMeters;
            var gy = selfGlobalY + dirY * stepMeters;
            if (gx < 0f) gx = 0f;
            if (gy < 0f) gy = 0f;

            var destCell = AcCoords.OutdoorCellIdFromGlobal(gx, gy);
            if (destCell == 0u) continue;            // indoor / invalid — skip
            if (cooledCells.Contains(destCell)) continue;

            // Score: distance from this candidate to the nearest reference
            // sample. Higher = farther from explored ground = better. With
            // no references every candidate scores the same large value, so
            // the tie-break alone decides (deterministic).
            float score;
            if (refs.Count == 0)
            {
                score = float.MaxValue;
            }
            else
            {
                var nearestSq = float.MaxValue;
                foreach (var v in refs)
                {
                    var rdx = v.GlobalX - gx;
                    var rdy = v.GlobalY - gy;
                    var sq = rdx * rdx + rdy * rdy;
                    if (sq < nearestSq) nearestSq = sq;
                }
                score = MathF.Sqrt(nearestSq);
            }

            // Tie-break: prefer the bearing pointing away from the
            // centroid. Without a centroid, prefer the lowest sector index
            // for full determinism.
            var tie = haveAway ? (dirX * awayDx + dirY * awayDy) : (-k);

            if (score > bestScore + 1e-3f ||
                (MathF.Abs(score - bestScore) <= 1e-3f && tie > bestTie))
            {
                bestScore = score;
                bestTie = tie;
                best = new FrontierResult(gx, gy, destCell, k);
            }
        }

        return best;
    }
}
