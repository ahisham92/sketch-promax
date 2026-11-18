using System.Globalization;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// The parts of modelling from as-built (or any section) drawings that need no Revit: where a zoom of a drawing page
/// falls in pixels, the labelled grid laid over a page, which section each stretch of the quay takes (and the gaps), the
/// shapes of the parts of a section (gravity block courses with their setbacks included), where a point sits along the
/// alignment, and the comparison of levels with a reference model. Lengths and levels are in project units throughout.
/// </summary>
internal static class AsBuiltMath
{
    // ---- Reading a drawing page --------------------------------------------------------------------------------

    /// <summary>A part of a page as fractions of its width and height, from the top left corner.</summary>
    public readonly record struct Region(double Left, double Top, double Right, double Bottom)
    {
        public double W => Right - Left;
        public double H => Bottom - Top;
        public static Region Whole => new(0, 0, 1, 1);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "[{0:0.###}, {1:0.###}, {2:0.###}, {3:0.###}]", Left, Top, Right, Bottom);
    }

    /// <summary>Columns and rows of the grid laid over a whole page, chosen so the cells are about square.</summary>
    public static (int Cols, int Rows) GridFor(double pageW, double pageH)
    {
        if (pageW <= 0 || pageH <= 0) return (8, 6);
        return pageW >= pageH ? (8, Math.Max(3, (int)Math.Round(8 * pageH / pageW))) : (Math.Max(3, (int)Math.Round(8 * pageW / pageH)), 8);
    }

    /// <summary>Column letters: A..Z, then AA...</summary>
    public static string Column(int i) => i < 26 ? ((char)('A' + i)).ToString() : Column(i / 26 - 1) + (char)('A' + i % 26);

    private static readonly Regex Cell = new(@"^\s*([A-Za-z]{1,2})\s*(\d{1,2})\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The region for "C3" (one grid cell) or "B2:D4" (a block of cells), on a grid of cols x rows; null with the reason
    /// when it isn't a cell name on that grid.
    /// </summary>
    public static Region? CellRegion(string text, int cols, int rows, out string error)
    {
        error = "";
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 2) { error = $"\"{text}\" is not a cell (C3) or a block of cells (B2:D4)."; return null; }
        var cells = new List<(int c, int r)>();
        foreach (var p in parts)
        {
            var m = Cell.Match(p);
            if (!m.Success) { error = $"\"{p.Trim()}\" is not a cell name like C3."; return null; }
            var letters = m.Groups[1].Value.ToUpperInvariant();
            var c = letters.Length == 1 ? letters[0] - 'A' : (letters[0] - 'A' + 1) * 26 + letters[1] - 'A';
            var r = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1;
            if (c < 0 || c >= cols || r < 0 || r >= rows) { error = $"{p.Trim().ToUpperInvariant()} is off the grid (A1 to {Column(cols - 1)}{rows})."; return null; }
            cells.Add((c, r));
        }
        int c0 = cells.Min(x => x.c), c1 = cells.Max(x => x.c), r0 = cells.Min(x => x.r), r1 = cells.Max(x => x.r);
        return new Region(c0 / (double)cols, r0 / (double)rows, (c1 + 1) / (double)cols, (r1 + 1) / (double)rows);
    }

    /// <summary>A region from [left, top, right, bottom] fractions, put in order and kept on the page; null with the reason.</summary>
    public static Region? Fractions(IReadOnlyList<double> v, out string error)
    {
        error = "";
        if (v.Count != 4) { error = "region takes four numbers: [left, top, right, bottom], as fractions of the page from its top left."; return null; }
        double l = Clamp(Math.Min(v[0], v[2])), r = Clamp(Math.Max(v[0], v[2])), t = Clamp(Math.Min(v[1], v[3])), b = Clamp(Math.Max(v[1], v[3]));
        if (r - l < 0.005 || b - t < 0.005) { error = "The region is too small: give at least half a percent of the page each way."; return null; }
        return new Region(l, t, r, b);
        static double Clamp(double x) => Math.Max(0, Math.Min(1, x));
    }

    /// <summary>
    /// The picture for a region of a page of pageW x pageH points: its size in pixels (longest side maxPixels), and the
    /// full page size and the offset to draw the whole page at so that the region fills the picture. Dpi is the scan
    /// resolution the picture has on the printed page.
    /// </summary>
    public static (int Width, int Height, int FullWidth, int FullHeight, int OffsetX, int OffsetY, double Dpi) Zoom(double pageW, double pageH, Region region, int maxPixels)
    {
        double rw = pageW * region.W, rh = pageH * region.H;
        var scale = maxPixels / Math.Max(rw, rh);
        // A picture past ~25 000 pixels across the whole page is far past any scan's own resolution; cap it so the page fits in memory maths.
        scale = Math.Min(scale, 30000 / Math.Max(pageW, pageH));
        int w = Math.Max(1, (int)Math.Round(rw * scale)), h = Math.Max(1, (int)Math.Round(rh * scale));
        int fw = Math.Max(1, (int)Math.Round(pageW * scale)), fh = Math.Max(1, (int)Math.Round(pageH * scale));
        return (w, h, fw, fh, (int)Math.Round(region.Left * fw), (int)Math.Round(region.Top * fh), scale * 72);
    }

    /// <summary>A region of the whole page from a region of a zoomed picture (both as fractions), so a zoom can be zoomed again.</summary>
    public static Region Within(Region outer, Region inner) =>
        new(outer.Left + inner.Left * outer.W, outer.Top + inner.Top * outer.H, outer.Left + inner.Right * outer.W, outer.Top + inner.Bottom * outer.H);

    // ---- Sections along the quay ------------------------------------------------------------------------------

    /// <summary>A stretch of the alignment, from and to as chainages from its first point, that takes one section.</summary>
    public sealed record Zone(string Section, double From, double To, string? Name = null)
    {
        public string Label => Name ?? $"{Section} {From.ToString("0.###", CultureInfo.InvariantCulture)}-{To.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// What is wrong with the zones over an alignment of the given length: a missing section, a zone running backwards,
    /// overlaps, and stretches no zone covers (each named, so nothing is quietly left out). Errors stop the work;
    /// gaps are reported, since a stretch can be without the structure.
    /// </summary>
    public static (List<string> Errors, List<string> Gaps) CheckZones(IReadOnlyList<Zone> zones, ISet<string> sections, double length, double tolerance)
    {
        var errors = new List<string>(); var gaps = new List<string>();
        if (zones.Count == 0) errors.Add("Give zones: which section each stretch of the alignment takes ([{section, from, to}]).");
        foreach (var z in zones)
        {
            if (!sections.Contains(z.Section)) errors.Add($"Zone {z.Label}: there is no section named \"{z.Section}\".");
            if (z.To - z.From <= tolerance) errors.Add($"Zone {z.Label}: \"to\" must be past \"from\".");
            if (z.From < -tolerance || z.To > length + tolerance)
                errors.Add($"Zone {z.Label} runs past the alignment (0 to {F(length)}).");
        }
        var sorted = zones.OrderBy(z => z.From).ToList();
        for (var i = 0; i + 1 < sorted.Count; i++)
            if (sorted[i + 1].From < sorted[i].To - tolerance) errors.Add($"Zones {sorted[i].Label} and {sorted[i + 1].Label} overlap.");
        var at = 0.0;
        foreach (var z in sorted)
        {
            if (z.From > at + tolerance) gaps.Add($"{F(at)} to {F(z.From)}");
            at = Math.Max(at, z.To);
        }
        if (length > at + tolerance && zones.Count > 0) gaps.Add($"{F(at)} to {F(length)}");
        return (errors, gaps);
    }

    /// <summary>One gravity block course: its height, its width front to back, and how far its front sits behind the face line.</summary>
    public readonly record struct Course(double Height, double Width, double Setback);

    /// <summary>
    /// The outline (offset, level) of each block course stacked from baseLevel, the first course at the bottom. A course's
    /// front is at its setback behind the face line (offset 0), towards the land, so a battered or stepped wall keeps its
    /// shape; the land side runs back by the width.
    /// </summary>
    public static List<List<(double u, double v)>> Courses(double baseLevel, IReadOnlyList<Course> courses, bool landToRight = true)
    {
        var s = landToRight ? 1 : -1;
        var list = new List<List<(double u, double v)>>();
        var z = baseLevel;
        foreach (var c in courses)
        {
            if (c.Height <= 0 || c.Width <= 0) throw new ArgumentException("Each block course needs a height and a width above 0.");
            double f = s * c.Setback, b = s * (c.Setback + c.Width);
            list.Add(Ordered([(f, z), (b, z), (b, z + c.Height), (f, z + c.Height)]));
            z += c.Height;
        }
        return list;
    }

    /// <summary>The profile anticlockwise (so extrusions face out); repeated end points dropped.</summary>
    public static List<(double u, double v)> Ordered(IReadOnlyList<(double u, double v)> pts)
    {
        var list = new List<(double u, double v)>();
        foreach (var p in pts)
            if (list.Count == 0 || Math.Abs(list[list.Count - 1].u - p.u) + Math.Abs(list[list.Count - 1].v - p.v) > 1e-9) list.Add(p);
        if (list.Count > 1 && Math.Abs(list[0].u - list[list.Count - 1].u) + Math.Abs(list[0].v - list[list.Count - 1].v) <= 1e-9) list.RemoveAt(list.Count - 1);
        if (Area(list) < 0) list.Reverse();
        return list;
    }

    /// <summary>Signed area of a profile (positive anticlockwise).</summary>
    public static double Area(IReadOnlyList<(double u, double v)> p)
    {
        var a = 0.0;
        for (var i = 0; i < p.Count; i++) { var q = p[(i + 1) % p.Count]; a += p[i].u * q.v - q.u * p[i].v; }
        return a / 2;
    }

    /// <summary>What is wrong with a part's outline: too few points, no area, or edges crossing each other.</summary>
    public static string? ProfileProblem(IReadOnlyList<(double u, double v)> p)
    {
        if (p.Count < 3) return "needs at least 3 points";
        if (Math.Abs(Area(p)) < 1e-9) return "has no area (its points are in a line)";
        for (var i = 0; i < p.Count; i++)
            for (var j = i + 1; j < p.Count; j++)
            {
                if (j == i + 1 || (i == 0 && j == p.Count - 1)) continue;
                if (Cross(p[i], p[(i + 1) % p.Count], p[j], p[(j + 1) % p.Count]))
                    return $"crosses itself (edge {i + 1}-{(i + 1) % p.Count + 1} and edge {j + 1}-{(j + 1) % p.Count + 1}): list the points in order around the outline";
            }
        return null;
    }

    private static bool Cross((double u, double v) a, (double u, double v) b, (double u, double v) c, (double u, double v) d)
    {
        static double O((double u, double v) p, (double u, double v) q, (double u, double v) r) => (q.u - p.u) * (r.v - p.v) - (q.v - p.v) * (r.u - p.u);
        double d1 = O(c, d, a), d2 = O(c, d, b), d3 = O(a, b, c), d4 = O(a, b, d);
        return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) && ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
    }

    public static (double Bottom, double Top) Levels(IReadOnlyList<(double u, double v)> p) => (p.Min(x => x.v), p.Max(x => x.v));

    /// <summary>The chainages along a zone where piles go: from the zone start plus start, every spacing, to the zone end.</summary>
    public static List<double> Stations(double from, double to, double start, double spacing)
    {
        if (spacing <= 0) throw new ArgumentException("Pile spacing must be above 0.");
        var list = new List<double>();
        for (var s = from + start; s <= to + 1e-6; s += spacing) list.Add(s);
        return list;
    }

    /// <summary>Where a plan point sits: its chainage along the alignment and its offset to the right (negative left), with the distance to the nearest leg.</summary>
    public static (double Chainage, double Offset) Station(IReadOnlyList<(double x, double y)> alignment, (double x, double y) p)
    {
        double best = double.MaxValue, ch = 0, off = 0, s = 0;
        for (var i = 0; i + 1 < alignment.Count; i++)
        {
            var (ax, ay) = alignment[i]; var (bx, by) = alignment[i + 1];
            double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;
            double ux = dx / len, uy = dy / len;
            var t = Math.Max(0, Math.Min(len, (p.x - ax) * ux + (p.y - ay) * uy));
            double cx = ax + ux * t, cy = ay + uy * t, dist = Math.Sqrt((p.x - cx) * (p.x - cx) + (p.y - cy) * (p.y - cy));
            if (dist < best - 1e-9)
            {
                best = dist; ch = s + t;
                // Right of the direction of travel: (uy, -ux).
                off = (p.x - ax) * uy - (p.y - ay) * ux;
            }
            s += len;
        }
        return (ch, off);
    }

    // ---- Levels against the reference model ---------------------------------------------------------------------

    /// <summary>A modelled part's top and bottom in one zone, and the level it must match in the reference (null: none asked).</summary>
    public sealed record PartLevels(string Zone, string Part, double Top, double Bottom, string? Match);

    /// <summary>A reference element's top and bottom where it overlaps the zone.</summary>
    public sealed record RefLevels(string Zone, string Category, double Top, double Bottom);

    /// <summary>
    /// For each part asked to match ("top", "bottom" or "both"), the nearest reference level in the same zone and the
    /// difference; a difference past the tolerance, or no reference element in the zone, is a line to fix.
    /// </summary>
    public static List<string> LevelMismatches(IReadOnlyList<PartLevels> parts, IReadOnlyList<RefLevels> refs, double tolerance, Func<double, string> show)
    {
        var lines = new List<string>();
        foreach (var p in parts.Where(x => !string.IsNullOrEmpty(x.Match)))
        {
            var inZone = refs.Where(r => r.Zone == p.Zone).ToList();
            if (inZone.Count == 0) { lines.Add($"{p.Zone} {p.Part}: no reference element found in this zone to compare its {p.Match} with."); continue; }
            void One(string which, double mine, IEnumerable<double> theirs)
            {
                var nearest = theirs.OrderBy(t => Math.Abs(t - mine)).First();
                if (Math.Abs(nearest - mine) > tolerance)
                    lines.Add($"{p.Zone} {p.Part}: {which} {show(mine)}, reference {which} {show(nearest)} (differs by {show(Math.Abs(nearest - mine))}).");
            }
            var m = p.Match!.ToLowerInvariant();
            if (m is "top" or "both") One("top", p.Top, inZone.Select(r => r.Top));
            if (m is "bottom" or "both") One("bottom", p.Bottom, inZone.Select(r => r.Bottom));
        }
        return lines;
    }

    /// <summary>The distinct levels in a list, rounded to the step, highest first.</summary>
    public static List<double> Distinct(IEnumerable<double> levels, double step) =>
        levels.Select(l => Math.Round(l / step) * step).Distinct().OrderByDescending(l => l).ToList();

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
