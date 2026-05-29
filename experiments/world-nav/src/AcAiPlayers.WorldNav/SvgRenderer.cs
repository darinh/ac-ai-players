// SPDX-License-Identifier: AGPL-3.0-or-later
//
// SvgRenderer - render an IndoorNavGraph as a self-contained SVG
// file. Top-down projection (X horizontal, Y vertical in world
// space). Each cell becomes a coloured rectangle (its XY bounds),
// each connection becomes a circle, each connection->neighbour
// link is a line segment from cell-centroid through connection
// point to the neighbouring cell-centroid.
//
// Multi-floor buildings: the default Floors mode partitions cells
// by centroid Z into floor groups (1D clustering by gap > 1.5u)
// and renders each floor in its own panel, stacked top-down with
// the highest floor at the top of the SVG. Inter-floor connections
// (stairs, ramps, ladders, drops) get a colored "up"/"down" marker
// at the source cell with the destination floor + cell-id label.
//
// CAVEAT: This view shows the CELL graph plus the cell's WALKABLE
// FLOOR POLYGONS (light green, the actual surface the bot can step
// on) plus its STATIC-OBSTACLE FOOTPRINTS (red/orange circles for
// signs, columns, lifestones, training dummies, etc.). The
// pathfinder currently still routes via cell centroids — a bot
// following pure cell-centroid waypoints could clip the obstacles
// drawn here. Phase 2 will sample walkable nodes inside each floor
// polygon, carve out obstacle footprints, and refactor the
// pathfinder to use those. Dynamic obstacles (NPCs, mobs, players,
// items, closed doors) are runtime-sensed and never appear here.
//
// This is diagnostic output, not a runtime artifact. Used to
// eyeball-verify that the loader produced sensible geometry
// before we hook this up to the headless client's NavGraph.

using System.Globalization;
using System.Numerics;
using System.Text;

namespace AcAiPlayers.WorldNav;

public sealed class SvgRenderer
{
    private const float DefaultPixelsPerWorldUnit = 6f;
    private const float Margin = 24f;
    private const float FloorGapThreshold = 1.5f;
    private const float InterFloorMinDz = 1.5f;

    public enum RenderMode
    {
        /// <summary>Single panel, all floors overlaid (2D top-down).</summary>
        Combined,

        /// <summary>One panel per floor, stacked vertically.</summary>
        Floors,
    }

    public sealed class Options
    {
        public float PixelsPerWorldUnit { get; init; } = DefaultPixelsPerWorldUnit;
        public bool ShowCellIds { get; init; } = true;
        public bool ShowConnectionIds { get; init; } = false;
        public bool ShowWalkableEdges { get; init; } = false;
        public RenderMode Mode { get; init; } = RenderMode.Floors;
    }

    private sealed class FloorGroup
    {
        public int Index;
        public float MinZ;
        public float MaxZ;
        public List<IndoorCell> Cells = new();
        public NavBounds XyBounds; // XY extent of cells in this floor

        public string Label =>
            $"Floor {Index + 1}  (Z {MinZ:0.#}..{MaxZ:0.#}u, {Cells.Count} cells)";
    }

    public string Render(IndoorNavGraph graph, Options? options = null)
    {
        options ??= new Options();
        return options.Mode == RenderMode.Combined
            ? RenderCombined(graph, options)
            : RenderFloors(graph, options);
    }

    private static List<FloorGroup> ClusterFloors(IndoorNavGraph graph)
    {
        var sorted = graph.Cells.Values
            .OrderBy(c => c.CentroidWorld.Z)
            .ToList();
        if (sorted.Count == 0) return new();

        var floors = new List<FloorGroup>();
        var current = new FloorGroup { MinZ = sorted[0].CentroidWorld.Z };
        float lastZ = sorted[0].CentroidWorld.Z;
        foreach (var c in sorted)
        {
            float z = c.CentroidWorld.Z;
            if (z - lastZ > FloorGapThreshold)
            {
                current.MaxZ = lastZ;
                floors.Add(current);
                current = new FloorGroup { MinZ = z };
            }
            current.Cells.Add(c);
            lastZ = z;
        }
        current.MaxZ = lastZ;
        floors.Add(current);

        // Compute XY bounds per floor and re-index from bottom (lowest Z) up.
        floors = floors.OrderBy(f => f.MinZ).ToList();
        for (int i = 0; i < floors.Count; i++)
        {
            floors[i].Index = i;
            var f = floors[i];
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            foreach (var c in f.Cells)
            {
                if (c.BoundsWorld.MinX < minX) minX = c.BoundsWorld.MinX;
                if (c.BoundsWorld.MinY < minY) minY = c.BoundsWorld.MinY;
                if (c.BoundsWorld.MaxX > maxX) maxX = c.BoundsWorld.MaxX;
                if (c.BoundsWorld.MaxY > maxY) maxY = c.BoundsWorld.MaxY;
            }
            f.XyBounds = new NavBounds(minX, minY, maxX, maxY, 0, 0);
        }
        return floors;
    }

    private static string CssBlock()
    {
        var sb = new StringBuilder();
        sb.Append("<style>\n");
        sb.Append("  .cell{fill:#dbe9ff;stroke:#3a6fb5;stroke-width:1}\n");
        sb.Append("  .cell-nogeom{fill:#fff5d1;stroke:#cc9933;stroke-dasharray:3 2;stroke-width:1}\n");
        sb.Append("  .cell-label{font:9px sans-serif;fill:#0a2a4a;text-anchor:middle;dominant-baseline:central}\n");
        sb.Append("  .cell-centroid{fill:#0a3a8a}\n");
        sb.Append("  .conn-link{stroke:#888;stroke-width:0.8;fill:none}\n");
        sb.Append("  .conn-link-dangling{stroke:#cc3333;stroke-width:0.8;stroke-dasharray:3 2;fill:none}\n");
        sb.Append("  .conn{fill:#22aa44;stroke:#0a5522;stroke-width:0.5}\n");
        sb.Append("  .conn-unresolved{fill:#aa4422;stroke:#552200;stroke-width:0.5}\n");
        sb.Append("  .obstacle-cyl{fill:#cc3333;fill-opacity:0.55;stroke:#660000;stroke-width:0.4}\n");
        sb.Append("  .obstacle-sphere{fill:#cc6633;fill-opacity:0.45;stroke:#663300;stroke-width:0.4}\n");
        sb.Append("  .obstacle-bound{fill:none;stroke:#660000;stroke-width:0.5;stroke-dasharray:2 2;opacity:0.7}\n");
        sb.Append("  .floor-poly{fill:#88dd88;fill-opacity:0.35;stroke:#226622;stroke-width:0.15}\n");
        sb.Append("  .walkable-node{fill:#0a4a0a;fill-opacity:0.85;stroke:none}\n");
        sb.Append("  .walkable-edge{stroke:#226622;stroke-width:0.15;stroke-opacity:0.4;fill:none}\n");
        sb.Append("  .stair-up{fill:#ff8a1a;stroke:#7a3a00;stroke-width:0.6}\n");
        sb.Append("  .stair-dn{fill:#7a3aff;stroke:#2a0066;stroke-width:0.6}\n");
        sb.Append("  .stair-label{font:8px sans-serif;fill:#2a0033;text-anchor:middle;dominant-baseline:central}\n");
        sb.Append("  .stair-connect{stroke:#ff8a1a;stroke-width:1.2;stroke-dasharray:5 3;fill:none;opacity:0.55}\n");
        sb.Append("  .stair-connect-skip{stroke:#cc3333;stroke-width:1.2;stroke-dasharray:5 3;fill:none;opacity:0.55}\n");
        sb.Append("  .floor-bg{fill:#fafafa;stroke:#bbb;stroke-width:1}\n");
        sb.Append("  .floor-title{font:bold 13px sans-serif;fill:#000}\n");
        sb.Append("  .legend{font:11px sans-serif;fill:#222}\n");
        sb.Append("  .title{font:bold 14px sans-serif;fill:#000}\n");
        sb.Append("</style>\n");
        return sb.ToString();
    }

    private static void AppendFloorPanel(
        StringBuilder sb,
        IndoorNavGraph graph,
        FloorGroup floor,
        Dictionary<uint, int> cellToFloor,
        float panelOriginX,
        float panelOriginY,
        float scale,
        Options options)
    {
        var b = floor.XyBounds;
        float panelW = Margin * 2 + b.Width * scale;
        float panelH = Margin * 2 + b.Height * scale + 28f; // +28 for title bar

        float Wx(float x) => panelOriginX + Margin + (x - b.MinX) * scale;
        float Wy(float y) => panelOriginY + 28f + Margin + (b.MaxY - y) * scale;

        sb.Append(CultureInfo.InvariantCulture,
            $"<rect class=\"floor-bg\" x=\"{panelOriginX:0.##}\" y=\"{panelOriginY:0.##}\" width=\"{panelW:0.##}\" height=\"{panelH:0.##}\"/>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text class=\"floor-title\" x=\"{panelOriginX + Margin:0.##}\" y=\"{panelOriginY + 18:0.##}\">{floor.Label}</text>\n");

        var cellSet = new HashSet<uint>(floor.Cells.Select(c => c.CellId));

        // Cell rectangles
        foreach (var cell in floor.Cells)
        {
            var cls = cell.HasGeometry ? "cell" : "cell-nogeom";
            var x = Wx(cell.BoundsWorld.MinX);
            var y = Wy(cell.BoundsWorld.MaxY);
            var w = cell.BoundsWorld.Width * scale;
            var h = cell.BoundsWorld.Height * scale;
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect class=\"{cls}\" x=\"{x:0.##}\" y=\"{y:0.##}\" width=\"{w:0.##}\" height=\"{h:0.##}\"/>\n");
        }

        // Walkable floor polygons (per-cell PhysicsPolygons with
        // upward-facing normals, projected to world). Drawn over the
        // cell bounding rect so the actual walkable shape -- usually
        // smaller than the bounding box -- is visible. Drawn BEFORE
        // obstacles so red obstacle circles sit on top of green floor.
        foreach (var cell in floor.Cells)
        {
            foreach (var fp in cell.FloorPolygons)
            {
                var pts = new StringBuilder(fp.VerticesWorld.Count * 12);
                for (int i = 0; i < fp.VerticesWorld.Count; i++)
                {
                    if (i > 0) pts.Append(' ');
                    var vw = fp.VerticesWorld[i];
                    pts.Append(CultureInfo.InvariantCulture, $"{Wx(vw.X):0.##},{Wy(vw.Y):0.##}");
                }
                sb.Append(CultureInfo.InvariantCulture,
                    $"<polygon class=\"floor-poly\" points=\"{pts}\"/>\n");
            }
        }

        // Static obstacles (signs, columns, lifestones, dummies, ...).
        // Drawn BEFORE connection links so the link lines + diamonds
        // sit on top — the obstacles are background context for the
        // graph, not the graph itself.
        foreach (var cell in floor.Cells)
        {
            foreach (var ob in cell.StaticObstacles)
            {
                var cls = ob.Shape switch
                {
                    ObstacleShape.Cylinder => "obstacle-cyl",
                    ObstacleShape.Sphere => "obstacle-sphere",
                    ObstacleShape.BoundingCylinder => "obstacle-bound",
                    _ => "obstacle-cyl",
                };
                var cx = Wx(ob.CenterWorld.X);
                var cy = Wy(ob.CenterWorld.Y);
                var r = ob.Radius * scale;
                if (r < 0.6f) r = 0.6f; // keep tiny props visible at default zoom
                sb.Append(CultureInfo.InvariantCulture,
                    $"<circle class=\"{cls}\" cx=\"{cx:0.##}\" cy=\"{cy:0.##}\" r=\"{r:0.##}\"/>\n");
            }
        }

        // Walkable nodes (grid-sampled stand-here points). Drawn AFTER
        // obstacles so the dark green dots overlay any obstacle that
        // visually overlaps -- though in practice they were carved out
        // at sample time so there should be no overlap. Each dot is
        // one Vector3 the per-cell micro-pathfinder will eventually
        // walk along.
        foreach (var cell in floor.Cells)
        {
            // Edges first (under the dots) so dots remain visible.
            if (options.ShowWalkableEdges)
            {
                foreach (var e in cell.WalkableEdges)
                {
                    var na = cell.WalkableNodes[e.NodeA].PositionWorld;
                    var nb = cell.WalkableNodes[e.NodeB].PositionWorld;
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<line class=\"walkable-edge\" x1=\"{Wx(na.X):0.##}\" y1=\"{Wy(na.Y):0.##}\" x2=\"{Wx(nb.X):0.##}\" y2=\"{Wy(nb.Y):0.##}\"/>\n");
                }
            }
            foreach (var wn in cell.WalkableNodes)
            {
                var nx = Wx(wn.PositionWorld.X);
                var ny = Wy(wn.PositionWorld.Y);
                sb.Append(CultureInfo.InvariantCulture,
                    $"<circle class=\"walkable-node\" cx=\"{nx:0.##}\" cy=\"{ny:0.##}\" r=\"0.6\"/>\n");
            }
        }

        // Connection links and markers.
        // Classification (orthogonal axes):
        //   bothInPanel = the other cell is on THIS floor's panel
        //                 (cells are physically rendered side-by-side).
        //   isStair     = |dz| of cell centroids >= InterFloorMinDz
        //                 (the connection traverses a notable vertical
        //                 distance — stair, ramp, ladder, drop).
        // Same-panel + flat = green connection dot + full link (cc→pc→oc).
        // Same-panel + stair = orange/purple diamond + full link.
        //                      Stair labelled with "dz=Nu" since the
        //                      other endpoint IS visible on this panel.
        // Cross-panel + stair (the common case) = diamond + half-link
        //                      (cc→pc only) + "↑F{n}" / "↓F{n}" label.
        // Dangling = red dashed half-link.
        foreach (var cell in floor.Cells)
        {
            var cc = cell.CentroidWorld;
            foreach (var connection in cell.Connections)
            {
                if (connection.CentroidWorld is not Vector3 pc) continue;

                bool resolved = connection.OtherCellLoaded;
                bool bothInPanel = resolved && cellSet.Contains(connection.OtherCellId);

                // First leg: cell-centroid -> connection-point (always).
                var linkClass = resolved ? "conn-link" : "conn-link-dangling";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<line class=\"{linkClass}\" x1=\"{Wx(cc.X):0.##}\" y1=\"{Wy(cc.Y):0.##}\" x2=\"{Wx(pc.X):0.##}\" y2=\"{Wy(pc.Y):0.##}\"/>\n");

                // Second leg only if other cell is also in this panel.
                if (bothInPanel && graph.Cells.TryGetValue(connection.OtherCellId, out var otherSame))
                {
                    var oc = otherSame.CentroidWorld;
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<line class=\"conn-link\" x1=\"{Wx(pc.X):0.##}\" y1=\"{Wy(pc.Y):0.##}\" x2=\"{Wx(oc.X):0.##}\" y2=\"{Wy(oc.Y):0.##}\"/>\n");
                }
            }
        }

        // Cell centroids
        foreach (var cell in floor.Cells)
        {
            var cc = cell.CentroidWorld;
            sb.Append(CultureInfo.InvariantCulture,
                $"<circle class=\"cell-centroid\" cx=\"{Wx(cc.X):0.##}\" cy=\"{Wy(cc.Y):0.##}\" r=\"2\"/>\n");
        }

        // Connection markers + stair markers (drawn last so they sit on top of links).
        foreach (var cell in floor.Cells)
        {
            var cc = cell.CentroidWorld;
            foreach (var connection in cell.Connections)
            {
                if (connection.CentroidWorld is not Vector3 pc) continue;

                bool resolved = connection.OtherCellLoaded;
                if (!resolved)
                {
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<circle class=\"conn-unresolved\" cx=\"{Wx(pc.X):0.##}\" cy=\"{Wy(pc.Y):0.##}\" r=\"2.4\"/>\n");
                    continue;
                }

                var otherCell = graph.Cells[connection.OtherCellId];
                float dz = otherCell.CentroidWorld.Z - cc.Z;
                bool isStair = Math.Abs(dz) >= InterFloorMinDz;

                if (!isStair)
                {
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<circle class=\"conn\" cx=\"{Wx(pc.X):0.##}\" cy=\"{Wy(pc.Y):0.##}\" r=\"2.4\"/>\n");
                    continue;
                }

                bool goesUp = dz >= 0;
                var stairCls = goesUp ? "stair-up" : "stair-dn";
                var arrow = goesUp ? "\u2191" : "\u2193";
                float px = Wx(pc.X);
                float py = Wy(pc.Y);
                sb.Append(CultureInfo.InvariantCulture,
                    $"<polygon class=\"{stairCls}\" points=\"{px:0.##},{py - 4:0.##} {px + 4:0.##},{py:0.##} {px:0.##},{py + 4:0.##} {px - 4:0.##},{py:0.##}\"/>\n");

                int otherFloor = cellToFloor.TryGetValue(otherCell.CellId, out var f) ? f : -1;
                bool bothInPanel = cellSet.Contains(connection.OtherCellId);
                string label = bothInPanel
                    ? $"{arrow}{Math.Abs(dz):0.0}u"
                    : (otherFloor >= 0 ? $"{arrow}F{otherFloor + 1}" : $"{arrow}?");
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text class=\"stair-label\" x=\"{px:0.##}\" y=\"{py - 8:0.##}\">{label}</text>\n");
            }
        }

        // Cell labels last so they sit on top
        if (options.ShowCellIds)
        {
            foreach (var cell in floor.Cells)
            {
                var cc = cell.CentroidWorld;
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text class=\"cell-label\" x=\"{Wx(cc.X):0.##}\" y=\"{Wy(cc.Y) - 8:0.##}\">{cell.CellWithinLandblock:X4}</text>\n");
            }
        }
    }

    private static void AppendInterPanelConnectors(
        StringBuilder sb,
        IndoorNavGraph graph,
        Dictionary<uint, int> cellToFloor,
        NavBounds commonBounds,
        float scale,
        Dictionary<int, float> panelOriginYByFloor)
    {
        float panelX = Margin;
        float Wx(float x) => panelX + Margin + (x - commonBounds.MinX) * scale;
        float Wy(float y, float panelOriginY) => panelOriginY + 28f + Margin + (commonBounds.MaxY - y) * scale;

        var seen = new HashSet<(uint, uint)>();
        int connectorCount = 0;
        foreach (var cell in graph.Cells.Values)
        {
            if (!cellToFloor.TryGetValue(cell.CellId, out var fromFloor)) continue;
            foreach (var connection in cell.Connections)
            {
                if (connection.CentroidWorld is not Vector3 pc) continue;
                if (!connection.OtherCellLoaded) continue;
                if (!cellToFloor.TryGetValue(connection.OtherCellId, out var toFloor)) continue;
                if (fromFloor == toFloor) continue;

                // Canonical ordering so we draw each connector once.
                var lo = Math.Min(cell.CellId, connection.OtherCellId);
                var hi = Math.Max(cell.CellId, connection.OtherCellId);
                if (!seen.Add((lo, hi))) continue;

                if (!panelOriginYByFloor.TryGetValue(fromFloor, out var fromOriginY)) continue;
                if (!panelOriginYByFloor.TryGetValue(toFloor, out var toOriginY)) continue;

                float x = Wx(pc.X);
                float y1 = Wy(pc.Y, fromOriginY);
                float y2 = Wy(pc.Y, toOriginY);

                // Skip-floor stairs (e.g., a connection that joins Floor 1
                // to Floor 3 over the top of Floor 2) get a different color
                // because the line is physically passing through an
                // intermediate panel.
                bool skipsFloor = Math.Abs(fromFloor - toFloor) > 1;
                var cls = skipsFloor ? "stair-connect-skip" : "stair-connect";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<line class=\"{cls}\" x1=\"{x:0.##}\" y1=\"{y1:0.##}\" x2=\"{x:0.##}\" y2=\"{y2:0.##}\"/>\n");
                connectorCount++;
            }
        }

        // Legend tucked at the top-right of the SVG, just under the title.
        sb.Append(CultureInfo.InvariantCulture,
            $"<text class=\"legend\" x=\"{Margin}\" y=\"38\">{connectorCount} inter-floor connectors (dashed orange = adjacent floors; dashed red = skip-floor)</text>\n");
    }

    private string RenderFloors(IndoorNavGraph graph, Options options)
    {
        var scale = options.PixelsPerWorldUnit;
        var floors = ClusterFloors(graph);
        var cellToFloor = new Dictionary<uint, int>(graph.Cells.Count);
        foreach (var f in floors)
            foreach (var c in f.Cells)
                cellToFloor[c.CellId] = f.Index;

        // Use a SINGLE shared bounds so panels line up vertically — easier to
        // read inter-floor connections as "this XY position connects to that
        // XY position one panel down".
        var commonBounds = graph.BoundsWorld;
        float panelW = Margin * 2 + commonBounds.Width * scale;
        float panelH = Margin * 2 + commonBounds.Height * scale + 28f;
        const float panelGap = 16f;

        // Highest floor at top of SVG -> render in descending Index order
        var stacked = floors.OrderByDescending(f => f.Index).ToList();
        float totalH = 40f + stacked.Count * panelH + (stacked.Count - 1) * panelGap + Margin;
        float totalW = panelW + Margin * 2;

        var sb = new StringBuilder(128 * 1024);
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {totalW:0.##} {totalH:0.##}\" width=\"{totalW:0.##}\" height=\"{totalH:0.##}\">\n");
        sb.Append(CssBlock());
        sb.Append("<rect x=\"0\" y=\"0\" width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text class=\"title\" x=\"{Margin}\" y=\"22\">Landblock {graph.LandblockId:X4} — {graph.CellCount} cells, {graph.ConnectionCount} connections, {graph.StaticObstacleCount} static obstacles, {graph.FloorPolygonCount} floor polys, {graph.WalkableNodeCount} walk nodes ({graph.WalkableEdgeCount} edges), {floors.Count} floors (highest at top)</text>\n");

        float y = 40f;
        var panelOriginYByFloor = new Dictionary<int, float>(stacked.Count);
        foreach (var floor in stacked)
        {
            // Use commonBounds for the panel so cells stay XY-aligned across floors.
            // We render the floor's cells only; cells from other floors are
            // skipped. We still build the panel with commonBounds so connection
            // points at the same XY land at the same panel X/Y on every floor.
            var virtualFloor = new FloorGroup
            {
                Index = floor.Index,
                MinZ = floor.MinZ,
                MaxZ = floor.MaxZ,
                Cells = floor.Cells,
                XyBounds = commonBounds,
            };
            panelOriginYByFloor[floor.Index] = y;
            AppendFloorPanel(sb, graph, virtualFloor, cellToFloor, Margin, y, scale, options);
            y += panelH + panelGap;
        }

        // Inter-panel stair connectors: vertical dashed lines that visibly
        // tie the same connection point on Floor A's panel to its match on
        // Floor B's panel, so the reader can see "this stair connects these
        // two floors" at a glance. Drawn AFTER all panels so they sit on top.
        AppendInterPanelConnectors(sb, graph, cellToFloor, commonBounds, scale, panelOriginYByFloor);

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private string RenderCombined(IndoorNavGraph graph, Options options)
    {
        var b = graph.BoundsWorld;
        var scale = options.PixelsPerWorldUnit;

        // SVG Y grows down; world Y grows north. Flip Y so north
        // points up in the rendered image.
        float Wx(float x) => Margin + (x - b.MinX) * scale;
        float Wy(float y) => Margin + (b.MaxY - y) * scale;

        var width = Margin * 2 + b.Width * scale;
        var height = Margin * 2 + b.Height * scale;

        var sb = new StringBuilder(64 * 1024);
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width:0.##} {height:0.##}\" width=\"{width:0.##}\" height=\"{height:0.##}\">\n");
        sb.Append(CssBlock());
        sb.Append("<rect x=\"0\" y=\"0\" width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>\n");

        // Cells
        foreach (var cell in graph.Cells.Values)
        {
            var cls = cell.HasGeometry ? "cell" : "cell-nogeom";
            var x = Wx(cell.BoundsWorld.MinX);
            var y = Wy(cell.BoundsWorld.MaxY);
            var w = cell.BoundsWorld.Width * scale;
            var h = cell.BoundsWorld.Height * scale;
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect class=\"{cls}\" x=\"{x:0.##}\" y=\"{y:0.##}\" width=\"{w:0.##}\" height=\"{h:0.##}\"/>\n");
        }

        // Walkable floor polygons — drawn over cell rects, under obstacles.
        foreach (var cell in graph.Cells.Values)
        {
            foreach (var fp in cell.FloorPolygons)
            {
                var pts = new StringBuilder(fp.VerticesWorld.Count * 12);
                for (int i = 0; i < fp.VerticesWorld.Count; i++)
                {
                    if (i > 0) pts.Append(' ');
                    var vw = fp.VerticesWorld[i];
                    pts.Append(CultureInfo.InvariantCulture, $"{Wx(vw.X):0.##},{Wy(vw.Y):0.##}");
                }
                sb.Append(CultureInfo.InvariantCulture,
                    $"<polygon class=\"floor-poly\" points=\"{pts}\"/>\n");
            }
        }

        // Static obstacles — drawn before connection links/circles
        // so the graph stays the visual foreground.
        foreach (var cell in graph.Cells.Values)
        {
            foreach (var ob in cell.StaticObstacles)
            {
                var cls = ob.Shape switch
                {
                    ObstacleShape.Cylinder => "obstacle-cyl",
                    ObstacleShape.Sphere => "obstacle-sphere",
                    ObstacleShape.BoundingCylinder => "obstacle-bound",
                    _ => "obstacle-cyl",
                };
                var cx = Wx(ob.CenterWorld.X);
                var cy = Wy(ob.CenterWorld.Y);
                var r = ob.Radius * scale;
                if (r < 0.6f) r = 0.6f;
                sb.Append(CultureInfo.InvariantCulture,
                    $"<circle class=\"{cls}\" cx=\"{cx:0.##}\" cy=\"{cy:0.##}\" r=\"{r:0.##}\"/>\n");
            }
        }

        // Walkable nodes (grid-sampled stand-here points).
        foreach (var cell in graph.Cells.Values)
        {
            if (options.ShowWalkableEdges)
            {
                foreach (var e in cell.WalkableEdges)
                {
                    var na = cell.WalkableNodes[e.NodeA].PositionWorld;
                    var nb = cell.WalkableNodes[e.NodeB].PositionWorld;
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<line class=\"walkable-edge\" x1=\"{Wx(na.X):0.##}\" y1=\"{Wy(na.Y):0.##}\" x2=\"{Wx(nb.X):0.##}\" y2=\"{Wy(nb.Y):0.##}\"/>\n");
                }
            }
            foreach (var wn in cell.WalkableNodes)
            {
                var nx = Wx(wn.PositionWorld.X);
                var ny = Wy(wn.PositionWorld.Y);
                sb.Append(CultureInfo.InvariantCulture,
                    $"<circle class=\"walkable-node\" cx=\"{nx:0.##}\" cy=\"{ny:0.##}\" r=\"0.6\"/>\n");
            }
        }

        foreach (var cell in graph.Cells.Values)
        {
            var cc = cell.CentroidWorld;
            foreach (var connection in cell.Connections)
            {
                if (connection.CentroidWorld is not Vector3 pc) continue;
                var linkClass = connection.OtherCellLoaded ? "conn-link" : "conn-link-dangling";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<line class=\"{linkClass}\" x1=\"{Wx(cc.X):0.##}\" y1=\"{Wy(cc.Y):0.##}\" x2=\"{Wx(pc.X):0.##}\" y2=\"{Wy(pc.Y):0.##}\"/>\n");
                if (connection.OtherCellLoaded
                    && graph.Cells.TryGetValue(connection.OtherCellId, out var other))
                {
                    var oc = other.CentroidWorld;
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<line class=\"conn-link\" x1=\"{Wx(pc.X):0.##}\" y1=\"{Wy(pc.Y):0.##}\" x2=\"{Wx(oc.X):0.##}\" y2=\"{Wy(oc.Y):0.##}\"/>\n");
                }
            }
        }

        foreach (var cell in graph.Cells.Values)
        {
            var cc = cell.CentroidWorld;
            sb.Append(CultureInfo.InvariantCulture,
                $"<circle class=\"cell-centroid\" cx=\"{Wx(cc.X):0.##}\" cy=\"{Wy(cc.Y):0.##}\" r=\"2\"/>\n");
        }

        foreach (var cell in graph.Cells.Values)
        {
            foreach (var connection in cell.Connections)
            {
                if (connection.CentroidWorld is not Vector3 pc) continue;
                var cls = connection.OtherCellLoaded ? "conn" : "conn-unresolved";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<circle class=\"{cls}\" cx=\"{Wx(pc.X):0.##}\" cy=\"{Wy(pc.Y):0.##}\" r=\"2.4\"/>\n");
            }
        }

        if (options.ShowCellIds)
        {
            foreach (var cell in graph.Cells.Values)
            {
                var cc = cell.CentroidWorld;
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text class=\"cell-label\" x=\"{Wx(cc.X):0.##}\" y=\"{Wy(cc.Y) - 8:0.##}\">{cell.CellWithinLandblock:X4}</text>\n");
            }
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"<text class=\"title\" x=\"{Margin}\" y=\"16\">Landblock {graph.LandblockId:X4} — {graph.CellCount} cells, {graph.ConnectionCount} connections, {graph.StaticObstacleCount} static obstacles, {graph.FloorPolygonCount} floor polys, {graph.WalkableNodeCount} walk nodes ({graph.WalkableEdgeCount} edges) (combined view)</text>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }
}
