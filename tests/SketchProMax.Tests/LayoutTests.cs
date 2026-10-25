using static SketchProMax.Tools.LayoutMath;

/// <summary>Sheet drawing areas, tag spots, slab points and image regions: the plain geometry, without Revit.</summary>
internal static class LayoutTests
{
    private static List<Seg> Box(double x0, double y0, double x1, double y1) =>
        [new(x0, y0, x1, y0), new(x1, y0, x1, y1), new(x1, y1, x0, y1), new(x0, y1, x0, y0)];

    private static bool Near(Rect? r, double x0, double y0, double x1, double y1) =>
        r is { } v && Math.Abs(v.X0 - x0) < 1e-6 && Math.Abs(v.Y0 - y0) < 1e-6 && Math.Abs(v.X1 - x1) < 1e-6 && Math.Abs(v.Y1 - y1) < 1e-6;

    public static void Run(Action<string, bool, string> check)
    {
        // A0 sheet: trim edge, inner frame, a title strip down the right side with rows in it.
        var strip = new List<Seg>();
        strip.AddRange(Box(0, 0, 1189, 841));
        strip.AddRange(Box(15, 20, 1174, 821));
        strip.Add(new(1070, 20, 1070, 821));
        for (var y = 60; y < 800; y += 40) strip.Add(new(1070, y, 1174, y));
        var a = DrawingArea(strip, out _);
        check("drawing area: title strip on the right is left out", Near(a, 15, 20, 1070, 821), a.ToString() ?? "null");

        // Title panel boxed into the bottom-right corner: the largest clear rectangle is the full width above it.
        var corner = new List<Seg>();
        corner.AddRange(Box(0, 0, 841, 594));
        corner.AddRange(Box(10, 10, 831, 584));
        corner.Add(new(651, 10, 651, 120));
        corner.Add(new(651, 120, 831, 120));
        corner.Add(new(651, 60, 831, 60));
        corner.Add(new(740, 10, 740, 60));
        var b = DrawingArea(corner, out var frame);
        check("drawing area: corner title box is left out", Near(b, 10, 120, 831, 584) && Near(frame, 10, 10, 831, 584), $"{b} frame {frame}");

        // Zone ticks standing on the frame don't shrink the area; stray short lines with no frame give nothing.
        var ticks = new List<Seg>(strip);
        for (var x = 100; x < 1000; x += 100) { ticks.Add(new(x, 20, x, 25)); ticks.Add(new(x, 821, x, 816)); }
        check("drawing area: frame ticks are ignored", Near(DrawingArea(ticks, out _), 15, 20, 1070, 821), DrawingArea(ticks, out _).ToString() ?? "null");
        check("drawing area: no frame gives null", DrawingArea([new(0, 0, 5, 0), new(0, 0, 0, 5)], out _) is null, "");

        // Tags: the upper-right spot is taken, so the tag goes upper left; all spots blocked still gives a spot.
        var host = new Rect(0, 0, 1, 1);
        var spot = TagBeside(host, 2, 1, 0.2, [new Rect(1, 1, 4, 3)]);
        check("tag beside host avoids an obstacle", spot.x < 0 && spot.y > 1, spot.ToString());
        var free = TagBeside(host, 2, 1, 0.2, []);
        check("tag beside host starts upper right", free.x > 1 && free.y > 1 && new Rect(free.x - 1, free.y - 0.5, free.x + 1, free.y + 0.5).Overlaps(host) == false, free.ToString());

        // Slab interior: an L shape's best point is inside it and away from the edges.
        var l = new List<(double x, double y)> { (0, 0), (10, 0), (10, 4), (4, 4), (4, 10), (0, 10) };
        var pts = InteriorPoints(l, []);
        check("interior point lies inside an L-shaped slab", pts.Count > 0 && InPolygon(l, pts[0].x, pts[0].y) && pts[0].clearance > 1.5, pts.Count > 0 ? pts[0].ToString() : "none");
        var hole = new List<(double x, double y)> { (1, 1), (3, 1), (3, 3), (1, 3) };
        var withHole = InteriorPoints([(0, 0), (4, 0), (4, 4), (0, 4)], [hole]);
        check("interior points avoid openings", withHole.All(p => !InPolygon(hole, p.x, p.y)), "");

        // Column stacks: the column below and above share a spot; a nearby one is separate.
        var stacks = Stacks([(0, 0), (5, 5), (0.01, -0.02), (5, 5.03), (9, 9)], 0.1);
        check("column stacks group by location", stacks.Count == 3 && stacks[0].SequenceEqual([0, 2]) && stacks[1].SequenceEqual([1, 3]), string.Join(" | ", stacks.Select(s => string.Join(",", s))));

        // Bars across a slab: an opening splits the line into two spans, the longer first.
        var spans = LineSpans([new List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10) }, new List<(double x, double y)> { (6, 4), (8, 4), (8, 6), (6, 6) }], true, 5);
        check("bar spans split at an opening", spans.Count == 2 && Math.Abs(spans[0].from) < 1e-9 && Math.Abs(spans[0].to - 6) < 1e-9 && Math.Abs(spans[1].from - 8) < 1e-9,
            string.Join(" ", spans));
        var vspans = LineSpans([new List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10) }], false, 3);
        check("vertical bar spans the slab", vspans.Count == 1 && Math.Abs(vspans[0].to - vspans[0].from - 10) < 1e-9, string.Join(" ", vspans));

        // Sheet region export: pixel width follows the resolution, capped; the region maps to the right pixels.
        var (dpi, px) = ExportSize(1189, 200, 1600, 8000);
        check("sheet export size keeps pixels = paper x dpi", dpi == 150 && px == (int)Math.Round(1189 / 25.4 * 150), $"{dpi} dpi {px} px");
        var (dpi2, px2) = ExportSize(46, 46, 1600, 1600);
        check("zoom export stays at or under the requested width", px2 <= 1600 && px2 == (int)Math.Round(46 / 25.4 * dpi2), $"{dpi2} dpi {px2} px");
        var region = PixelRegion(new Rect(0, 0, 100, 50), new Rect(80, 0, 100, 10), 1000, 500);
        check("sheet region maps to image pixels (top-left origin)", region == (800, 400, 200, 100), region.ToString());
    }
}
