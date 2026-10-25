namespace SketchProMax.Tools;

/// <summary>
/// Plain 2D geometry behind sheet layout, tag placement and image capture. No Revit API here, so the tests check it
/// directly. Rectangles are (X0, Y0, X1, Y1) with X0 &lt;= X1 and Y0 &lt;= Y1; segments are (X0, Y0, X1, Y1) end points.
/// </summary>
internal static class LayoutMath
{
    public readonly record struct Rect(double X0, double Y0, double X1, double Y1)
    {
        public double W => X1 - X0;
        public double H => Y1 - Y0;
        public double Area => Math.Max(0, W) * Math.Max(0, H);
        public double Cx => (X0 + X1) / 2;
        public double Cy => (Y0 + Y1) / 2;
        public Rect Move(double dx, double dy) => new(X0 + dx, Y0 + dy, X1 + dx, Y1 + dy);
        public Rect Grow(double d) => new(X0 - d, Y0 - d, X1 + d, Y1 + d);
        public bool Overlaps(Rect o) => X0 < o.X1 && X1 > o.X0 && Y0 < o.Y1 && Y1 > o.Y0;
        public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    }

    public readonly record struct Seg(double X0, double Y0, double X1, double Y1)
    {
        public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
    }

    // ---------------------------------------------------------------- title block drawing area

    /// <summary>
    /// The drawing area of a title block from its lines: the inner frame (the innermost of the long lines on each side
    /// of the middle; a title strip's long edge counts as one of them), then the largest rectangle inside that frame
    /// with no lines in it, so a title panel boxed into a corner is left out as well. Null when no frame is found.
    /// </summary>
    public static Rect? DrawingArea(IReadOnlyList<Seg> segments, out Rect? frame)
    {
        frame = null;
        const double tol = 1e-4;
        var vertical = segments.Where(s => Math.Abs(s.X0 - s.X1) < tol && Math.Abs(s.Y0 - s.Y1) > tol).ToList();
        var horizontal = segments.Where(s => Math.Abs(s.Y0 - s.Y1) < tol && Math.Abs(s.X0 - s.X1) > tol).ToList();
        var vCover = vertical.GroupBy(s => Math.Round(s.X0, 3))
            .Select(g => (at: g.Key, cover: Coverage(g.Select(s => (Math.Min(s.Y0, s.Y1), Math.Max(s.Y0, s.Y1)))))).ToList();
        var hCover = horizontal.GroupBy(s => Math.Round(s.Y0, 3))
            .Select(g => (at: g.Key, cover: Coverage(g.Select(s => (Math.Min(s.X0, s.X1), Math.Max(s.X0, s.X1)))))).ToList();
        if (vCover.Count < 2 || hCover.Count < 2) return null;
        double lv = vCover.Max(v => v.cover), lh = hCover.Max(h => h.cover);
        var longV = vCover.Where(v => v.cover >= 0.5 * lv).Select(v => v.at).ToList();
        var longH = hCover.Where(h => h.cover >= 0.5 * lh).Select(h => h.at).ToList();
        if (longV.Count < 2 || longH.Count < 2) return null;
        var outer = new Rect(longV.Min(), longH.Min(), longV.Max(), longH.Max());
        if (outer.W < 0.2 * lh || outer.H < 0.2 * lv) return null;
        var inner = new Rect(
            longV.Where(x => x < outer.Cx).DefaultIfEmpty(outer.X0).Max(),
            longH.Where(y => y < outer.Cy).DefaultIfEmpty(outer.Y0).Max(),
            longV.Where(x => x > outer.Cx).DefaultIfEmpty(outer.X1).Min(),
            longH.Where(y => y > outer.Cy).DefaultIfEmpty(outer.Y1).Min());
        if (inner.W < 0.3 * outer.W || inner.H < 0.3 * outer.H) inner = outer;
        frame = inner;
        var free = LargestEmpty(inner, segments);
        return free is { } f && f.Area >= 0.3 * inner.Area ? f : inner;
    }

    private static double Coverage(IEnumerable<(double lo, double hi)> spans)
    {
        double total = 0, end = double.MinValue;
        foreach (var (lo, hi) in spans.OrderBy(s => s.lo))
        {
            if (hi <= end) continue;
            total += hi - Math.Max(lo, end);
            end = hi;
        }
        return total;
    }

    /// <summary>
    /// The largest rectangle inside <paramref name="frame"/>, on a grid made of the frame and the longer lines inside it,
    /// that no line crosses. Short ticks standing on the frame (zone marks) do not count.
    /// </summary>
    public static Rect? LargestEmpty(Rect frame, IReadOnlyList<Seg> segments)
    {
        const double tol = 1e-4;
        var eps = Math.Min(frame.W, frame.H) * 1e-4;
        var inside = segments.Where(s => ClipLength(s, new Rect(frame.X0 + eps, frame.Y0 + eps, frame.X1 - eps, frame.Y1 - eps)) > eps).ToList();
        bool OnFrame(double x, double y) =>
            Math.Abs(x - frame.X0) < 10 * eps || Math.Abs(x - frame.X1) < 10 * eps || Math.Abs(y - frame.Y0) < 10 * eps || Math.Abs(y - frame.Y1) < 10 * eps;
        inside = inside.Where(s => !(s.Length < 0.03 * Math.Min(frame.W, frame.H) && (OnFrame(s.X0, s.Y0) || OnFrame(s.X1, s.Y1)))).ToList();

        List<double> Grid(IEnumerable<(double at, double len)> lines, double lo, double hi)
        {
            var picked = lines.Where(l => l.at > lo + eps && l.at < hi - eps).OrderByDescending(l => l.len).Take(150).Select(l => l.at);
            var all = new List<double> { lo, hi };
            foreach (var v in picked.OrderBy(v => v))
                if (all.All(a => Math.Abs(a - v) > eps)) all.Add(v);
            all.Sort();
            return all;
        }
        var xs = Grid(inside.Where(s => Math.Abs(s.X0 - s.X1) < tol && Math.Abs(s.Y1 - s.Y0) >= 0.03 * frame.H).Select(s => (s.X0, Math.Abs(s.Y1 - s.Y0))), frame.X0, frame.X1);
        var ys = Grid(inside.Where(s => Math.Abs(s.Y0 - s.Y1) < tol && Math.Abs(s.X1 - s.X0) >= 0.03 * frame.W).Select(s => (s.Y0, Math.Abs(s.X1 - s.X0))), frame.Y0, frame.Y1);
        int nx = xs.Count - 1, ny = ys.Count - 1;
        // A cell is blocked when a line passes through it; a grid line between two cells is blocked where a line lies on it.
        var blocked = new bool[nx, ny];
        var wallX = new bool[nx + 1, ny];   // vertical grid line xs[i], in row j
        var wallY = new bool[nx, ny + 1];   // horizontal grid line ys[j], in column i
        foreach (var s in inside)
        {
            int i0 = Cell(xs, Math.Min(s.X0, s.X1)), i1 = Cell(xs, Math.Max(s.X0, s.X1));
            int j0 = Cell(ys, Math.Min(s.Y0, s.Y1)), j1 = Cell(ys, Math.Max(s.Y0, s.Y1));
            for (var i = i0; i <= i1; i++)
                for (var j = j0; j <= j1; j++)
                    if (!blocked[i, j] && ClipLength(s, new Rect(xs[i] + eps, ys[j] + eps, xs[i + 1] - eps, ys[j + 1] - eps)) > eps) blocked[i, j] = true;
            if (Math.Abs(s.X0 - s.X1) < tol && xs.FindIndex(x => Math.Abs(x - s.X0) <= eps) is var gi and >= 0)
            {
                double lo = Math.Min(s.Y0, s.Y1), hi = Math.Max(s.Y0, s.Y1);
                for (var j = 0; j < ny; j++) if (hi > ys[j] + eps && lo < ys[j + 1] - eps) wallX[gi, j] = true;
            }
            if (Math.Abs(s.Y0 - s.Y1) < tol && ys.FindIndex(y => Math.Abs(y - s.Y0) <= eps) is var gj and >= 0)
            {
                double lo = Math.Min(s.X0, s.X1), hi = Math.Max(s.X0, s.X1);
                for (var i = 0; i < nx; i++) if (hi > xs[i] + eps && lo < xs[i + 1] - eps) wallY[i, gj] = true;
            }
        }

        // Every column range [i, k]: rows that are clear across it, joined into runs where no line separates them.
        Rect? best = null;
        var ok = new bool[ny];
        var join = new bool[ny];
        for (var i = 0; i < nx; i++)
        {
            for (var j = 0; j < ny; j++) { ok[j] = true; join[j] = true; }
            for (var k = i; k < nx; k++)
            {
                var runStart = -1;
                for (var j = 0; j <= ny; j++)
                {
                    if (j < ny)
                    {
                        ok[j] &= !blocked[k, j] && (k == i || !wallX[k, j]);
                        if (j > 0) join[j] &= !wallY[k, j];
                    }
                    var continues = j < ny && ok[j] && (runStart < 0 || join[j]);
                    if (continues) { if (runStart < 0) runStart = j; continue; }
                    if (runStart >= 0)
                    {
                        var r = new Rect(xs[i], ys[runStart], xs[k + 1], ys[j]);
                        if (best is null || r.Area > best.Value.Area + 1e-12) best = r;
                        runStart = j < ny && ok[j] ? j : -1;
                    }
                }
            }
        }
        return best;
    }

    private static int Cell(List<double> grid, double v)
    {
        for (var i = 0; i < grid.Count - 1; i++)
            if (v < grid[i + 1]) return i;
        return grid.Count - 2;
    }

    /// <summary>Length of the part of a segment inside a rectangle (Liang-Barsky clipping).</summary>
    public static double ClipLength(Seg s, Rect r)
    {
        double dx = s.X1 - s.X0, dy = s.Y1 - s.Y0, t0 = 0, t1 = 1;
        bool Edge(double p, double q)
        {
            if (Math.Abs(p) < 1e-15) return q >= 0;
            var t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        if (!Edge(-dx, s.X0 - r.X0) || !Edge(dx, r.X1 - s.X0) || !Edge(-dy, s.Y0 - r.Y0) || !Edge(dy, r.Y1 - s.Y0)) return 0;
        // A segment lying along the rectangle's edge counts as inside it only if it is strictly within (the caller shrinks the rectangle).
        return Math.Max(0, t1 - t0) * s.Length;
    }

    // ---------------------------------------------------------------- tag placement

    /// <summary>
    /// Where a tag of size w x h goes beside a host's box: just outside it, trying the corners (upper right first),
    /// then the sides, then the same ring further out; the first spot that is clear of every obstacle wins. Returns the
    /// tag's centre; when nothing is clear, the least crowded spot.
    /// </summary>
    public static (double x, double y) TagBeside(Rect host, double w, double h, double gap, IReadOnlyList<Rect> obstacles)
    {
        (double x, double y)? bestFallback = null;
        var fewest = int.MaxValue;
        for (var ring = 0; ring < 3; ring++)
        {
            var g = gap * (1 + 2 * ring);
            double right = host.X1 + g + w / 2, left = host.X0 - g - w / 2, top = host.Y1 + g + h / 2, bottom = host.Y0 - g - h / 2;
            var spots = new (double x, double y)[]
            {
                (right, top), (left, top), (right, bottom), (left, bottom),
                (right, host.Cy), (left, host.Cy), (host.Cx, top), (host.Cx, bottom),
            };
            foreach (var (x, y) in spots)
            {
                var box = new Rect(x - w / 2, y - h / 2, x + w / 2, y + h / 2);
                var hits = obstacles.Count(o => o.Overlaps(box));
                if (hits == 0) return (x, y);
                if (hits < fewest) { fewest = hits; bestFallback = (x, y); }
            }
        }
        return bestFallback ?? (host.X1 + gap + w / 2, host.Y1 + gap + h / 2);
    }

    /// <summary>
    /// Points well inside a polygon (with holes), best first: grid samples ordered by their distance to the nearest
    /// edge, so the first is close to the middle of the widest part. Used to put a slab's tag on the slab itself.
    /// </summary>
    public static List<(double x, double y, double clearance)> InteriorPoints(IReadOnlyList<(double x, double y)> outer,
        IReadOnlyList<IReadOnlyList<(double x, double y)>> holes, int samples = 24)
    {
        var result = new List<(double x, double y, double clearance)>();
        if (outer.Count < 3) return result;
        double x0 = outer.Min(p => p.x), x1 = outer.Max(p => p.x), y0 = outer.Min(p => p.y), y1 = outer.Max(p => p.y);
        var rings = new List<IReadOnlyList<(double x, double y)>> { outer };
        rings.AddRange(holes);
        for (var i = 0; i < samples; i++)
            for (var j = 0; j < samples; j++)
            {
                double x = x0 + (x1 - x0) * (i + 0.5) / samples, y = y0 + (y1 - y0) * (j + 0.5) / samples;
                if (!InPolygon(outer, x, y) || holes.Any(h => InPolygon(h, x, y))) continue;
                result.Add((x, y, rings.Min(r => DistanceToRing(r, x, y))));
            }
        return result.OrderByDescending(p => p.clearance).ToList();
    }

    public static bool InPolygon(IReadOnlyList<(double x, double y)> poly, double x, double y) => Cost.Inside(x, y, poly);

    private static double DistanceToRing(IReadOnlyList<(double x, double y)> ring, double x, double y)
    {
        var best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (ax, ay) = ring[j];
            var (bx, by) = ring[i];
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            var t = len2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len2));
            double px = ax + t * dx - x, py = ay + t * dy - y;
            best = Math.Min(best, Math.Sqrt(px * px + py * py));
        }
        return best;
    }

    /// <summary>
    /// Groups points that sit within <paramref name="tolerance"/> of each other in plan (a column stack seen from
    /// above and below). Returns the groups as lists of indexes into <paramref name="points"/>.
    /// </summary>
    public static List<List<int>> Stacks(IReadOnlyList<(double x, double y)> points, double tolerance)
    {
        var groups = new List<List<int>>();
        var centres = new List<(double x, double y)>();
        for (var i = 0; i < points.Count; i++)
        {
            var (x, y) = points[i];
            var k = centres.FindIndex(c => Math.Abs(c.x - x) <= tolerance && Math.Abs(c.y - y) <= tolerance);
            if (k < 0) { groups.Add([i]); centres.Add((x, y)); }
            else groups[k].Add(i);
        }
        return groups;
    }

    /// <summary>
    /// Where a straight line across a polygon with holes lies inside it: horizontal (y = at) or vertical (x = at).
    /// Returns the inside spans as (from, to) along the line, longest first. Openings split a span.
    /// </summary>
    public static List<(double from, double to)> LineSpans(IReadOnlyList<IReadOnlyList<(double x, double y)>> rings, bool horizontal, double at)
    {
        var hits = new List<double>();
        foreach (var ring in rings)
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var (ax, ay) = horizontal ? ring[j] : (ring[j].y, ring[j].x);
                var (bx, by) = horizontal ? ring[i] : (ring[i].y, ring[i].x);
                if ((ay > at) != (by > at)) hits.Add(ax + (at - ay) * (bx - ax) / (by - ay));
            }
        hits.Sort();
        var spans = new List<(double from, double to)>();
        for (var k = 0; k + 1 < hits.Count; k += 2)
            if (hits[k + 1] - hits[k] > 1e-9) spans.Add((hits[k], hits[k + 1]));
        return spans.OrderByDescending(sp => sp.to - sp.from).ToList();
    }

    // ---------------------------------------------------------------- image capture

    /// <summary>
    /// The export resolution and pixel width for printing a sheet of <paramref name="paperWidthMm"/> so that a region
    /// <paramref name="regionWidthMm"/> wide comes out about <paramref name="wantedRegionPixels"/> wide, keeping the
    /// whole image at most <paramref name="maxPixels"/> wide. The pixel width always matches the resolution (paper
    /// width x dpi), so Revit draws text where it draws the lines.
    /// </summary>
    public static (int dpi, int pixels) ExportSize(double paperWidthMm, double regionWidthMm, int wantedRegionPixels, int maxPixels)
    {
        var neededDpi = wantedRegionPixels / Math.Max(regionWidthMm, 1) * 25.4;
        int[] steps = [72, 150, 300, 600];
        var dpi = steps.FirstOrDefault(d => d >= neededDpi * 0.9);
        if (dpi == 0) dpi = 600;
        while (dpi > 72 && paperWidthMm / 25.4 * dpi > maxPixels) dpi = steps[Array.IndexOf(steps, dpi) - 1];
        var pixels = (int)Math.Round(paperWidthMm / 25.4 * dpi);
        return (dpi, Math.Max(64, Math.Min(maxPixels, pixels)));
    }

    /// <summary>The pixel rectangle (x, y from the top-left, w, h) of a region of a page, clipped to the image.</summary>
    public static (int x, int y, int w, int h) PixelRegion(Rect page, Rect region, int imageW, int imageH)
    {
        double sx = imageW / page.W, sy = imageH / page.H;
        var x0 = (int)Math.Floor((Math.Max(region.X0, page.X0) - page.X0) * sx);
        var x1 = (int)Math.Ceiling((Math.Min(region.X1, page.X1) - page.X0) * sx);
        var y0 = (int)Math.Floor((page.Y1 - Math.Min(region.Y1, page.Y1)) * sy);
        var y1 = (int)Math.Ceiling((page.Y1 - Math.Max(region.Y0, page.Y0)) * sy);
        x0 = Math.Max(0, Math.Min(imageW - 1, x0)); y0 = Math.Max(0, Math.Min(imageH - 1, y0));
        x1 = Math.Max(x0 + 1, Math.Min(imageW, x1)); y1 = Math.Max(y0 + 1, Math.Min(imageH, y1));
        return (x0, y0, x1 - x0, y1 - y0);
    }
}
