// SPDX-License-Identifier: AGPL-3.0-or-later
//
// SvgRenderer - render an IndoorNavGraph as a self-contained SVG
// file. Top-down projection (X horizontal, Y vertical in world
// space). Each cell becomes a coloured rectangle (its XY bounds),
// each portal becomes a circle, each portal->neighbour link is a
// line segment from cell-centroid through portal-centroid to the
// neighbouring cell-centroid.
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

    public sealed class Options
    {
        public float PixelsPerWorldUnit { get; init; } = DefaultPixelsPerWorldUnit;
        public bool ShowCellIds { get; init; } = true;
        public bool ShowPortalIds { get; init; } = false;
    }

    public string Render(IndoorNavGraph graph, Options? options = null)
    {
        options ??= new Options();
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
        sb.Append("<style>\n");
        sb.Append("  .cell{fill:#dbe9ff;stroke:#3a6fb5;stroke-width:1}\n");
        sb.Append("  .cell-nogeom{fill:#fff5d1;stroke:#cc9933;stroke-dasharray:3 2;stroke-width:1}\n");
        sb.Append("  .cell-label{font:9px sans-serif;fill:#0a2a4a;text-anchor:middle;dominant-baseline:central}\n");
        sb.Append("  .cell-centroid{fill:#0a3a8a}\n");
        sb.Append("  .portal-link{stroke:#888;stroke-width:0.8;fill:none}\n");
        sb.Append("  .portal-link-dangling{stroke:#cc3333;stroke-width:0.8;stroke-dasharray:3 2;fill:none}\n");
        sb.Append("  .portal{fill:#22aa44;stroke:#0a5522;stroke-width:0.5}\n");
        sb.Append("  .portal-unresolved{fill:#aa4422;stroke:#552200;stroke-width:0.5}\n");
        sb.Append("  .legend{font:11px sans-serif;fill:#222}\n");
        sb.Append("  .title{font:bold 14px sans-serif;fill:#000}\n");
        sb.Append("</style>\n");

        sb.Append("<rect x=\"0\" y=\"0\" width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>\n");

        // Cells
        foreach (var cell in graph.Cells.Values)
        {
            var cls = cell.HasGeometry ? "cell" : "cell-nogeom";
            var x = Wx(cell.BoundsWorld.MinX);
            var y = Wy(cell.BoundsWorld.MaxY); // flipped: MaxY world = top of SVG rect
            var w = cell.BoundsWorld.Width * scale;
            var h = cell.BoundsWorld.Height * scale;
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect class=\"{cls}\" x=\"{x:0.##}\" y=\"{y:0.##}\" width=\"{w:0.##}\" height=\"{h:0.##}\"/>\n");
        }

        // Portal links: cell-centroid -> portal-centroid -> other-cell-centroid
        foreach (var cell in graph.Cells.Values)
        {
            var cc = cell.CentroidWorld;
            foreach (var portal in cell.Portals)
            {
                if (portal.CentroidWorld is not Vector3 pc)
                    continue;

                var linkClass = portal.OtherCellLoaded ? "portal-link" : "portal-link-dangling";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<line class=\"{linkClass}\" x1=\"{Wx(cc.X):0.##}\" y1=\"{Wy(cc.Y):0.##}\" x2=\"{Wx(pc.X):0.##}\" y2=\"{Wy(pc.Y):0.##}\"/>\n");

                if (portal.OtherCellLoaded
                    && graph.Cells.TryGetValue(portal.OtherCellId, out var other))
                {
                    var oc = other.CentroidWorld;
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<line class=\"portal-link\" x1=\"{Wx(pc.X):0.##}\" y1=\"{Wy(pc.Y):0.##}\" x2=\"{Wx(oc.X):0.##}\" y2=\"{Wy(oc.Y):0.##}\"/>\n");
                }
            }
        }

        // Cell centroids
        foreach (var cell in graph.Cells.Values)
        {
            var cc = cell.CentroidWorld;
            sb.Append(CultureInfo.InvariantCulture,
                $"<circle class=\"cell-centroid\" cx=\"{Wx(cc.X):0.##}\" cy=\"{Wy(cc.Y):0.##}\" r=\"2\"/>\n");
        }

        // Portal centroids
        foreach (var cell in graph.Cells.Values)
        {
            foreach (var portal in cell.Portals)
            {
                if (portal.CentroidWorld is not Vector3 pc)
                    continue;
                var cls = portal.OtherCellLoaded ? "portal" : "portal-unresolved";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<circle class=\"{cls}\" cx=\"{Wx(pc.X):0.##}\" cy=\"{Wy(pc.Y):0.##}\" r=\"2.4\"/>\n");
            }
        }

        // Cell labels (last so they sit on top)
        if (options.ShowCellIds)
        {
            foreach (var cell in graph.Cells.Values)
            {
                var cc = cell.CentroidWorld;
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text class=\"cell-label\" x=\"{Wx(cc.X):0.##}\" y=\"{Wy(cc.Y) - 8:0.##}\">{cell.CellWithinLandblock:X4}</text>\n");
            }
        }

        // Title + legend
        sb.Append(CultureInfo.InvariantCulture,
            $"<text class=\"title\" x=\"{Margin}\" y=\"16\">Landblock {graph.LandblockId:X4} — {graph.CellCount} cells, {graph.PortalCount} portals</text>\n");

        sb.Append("</svg>\n");
        return sb.ToString();
    }
}
