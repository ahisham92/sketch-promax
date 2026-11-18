using SketchProMax.Tools;
using M = SketchProMax.Tools.AsBuiltMath;

/// <summary>
/// Modelling from as-built drawings without Revit (AsBuiltMath): zooming a drawing page, the lettered grid, zones of
/// sections along the quay and their gaps, gravity block courses with setbacks, outlines, stations along the alignment,
/// and levels compared with the reference model.
/// </summary>
internal static class AsBuiltTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // Grid and cells.
        var (cols, rows) = M.GridFor(3370, 2384); // A0 landscape in points
        check("as-built: an A0 landscape page gets an 8-column grid of about square cells", cols == 8 && rows == 6, $"{cols}x{rows}");
        var c3 = M.CellRegion("c3", 8, 6, out _);
        check("as-built: a cell name is the cell's part of the page", c3 is { } r && Math.Abs(r.Left - 0.25) < 1e-9 && Math.Abs(r.Top - 2 / 6.0) < 1e-9 && Math.Abs(r.W - 0.125) < 1e-9, c3?.ToString() ?? "null");
        var block = M.CellRegion("B2:D4", 8, 6, out _);
        check("as-built: a block of cells spans them all", block is { } b && Math.Abs(b.Left - 0.125) < 1e-9 && Math.Abs(b.Right - 0.5) < 1e-9 && Math.Abs(b.Bottom - 4 / 6.0) < 1e-9, block?.ToString() ?? "null");
        check("as-built: a cell off the grid is refused with the grid's range", M.CellRegion("J9", 8, 6, out var why) is null && why.Contains("H6"), why);
        check("as-built: text that is not a cell is refused", M.CellRegion("left half", 8, 6, out _) is null, "");
        check("as-built: column letters go past Z", M.Column(26) == "AA" && M.Column(0) == "A", M.Column(26));
        var f = M.Fractions([0.6, 0.5, 0.2, 0.1], out _);
        check("as-built: a region is put in order", f is { } fr && fr.Left == 0.2 && fr.Top == 0.1 && fr.Right == 0.6 && fr.Bottom == 0.5, f?.ToString() ?? "null");
        check("as-built: a tiny region is refused", M.Fractions([0.5, 0.5, 0.501, 0.6], out _) is null, "");

        // Zoom maths: a cell of an A0 page at 2000 px is far sharper than the whole page.
        var whole = M.Zoom(3370, 2384, M.Region.Whole, 2000);
        var cell = M.Zoom(3370, 2384, c3!.Value, 2000);
        check("as-built: the whole page fits the picture", whole.Width == 2000 && whole.OffsetX == 0, $"{whole.Width}x{whole.Height}");
        check("as-built: a zoomed cell is drawn at about 8 times the resolution", cell.Dpi > whole.Dpi * 7.5 && cell.OffsetX == (int)Math.Round(0.25 * cell.FullWidth), $"{whole.Dpi:0} -> {cell.Dpi:0} dpi");
        var inner = M.Within(c3.Value, new M.Region(0.5, 0.5, 1, 1));
        check("as-built: a zoom inside a zoom is a part of the page", Math.Abs(inner.Left - 0.3125) < 1e-9 && Math.Abs(inner.Right - 0.375) < 1e-9, inner.ToString());

        // Zones.
        var secs = new HashSet<string>(["S1", "S2"]);
        var ok = M.CheckZones([new("S1", 0, 100), new("S2", 100, 250)], secs, 250, 0.001);
        check("as-built: zones covering the quay end to end have no errors and no gaps", ok.Errors.Count == 0 && ok.Gaps.Count == 0, string.Join("; ", ok.Errors.Concat(ok.Gaps)));
        var gap = M.CheckZones([new("S1", 0, 100), new("S2", 120, 200)], secs, 250, 0.001);
        check("as-built: stretches with no section are listed, the end too", gap.Gaps.Count == 2 && gap.Gaps[0] == "100 to 120" && gap.Gaps[1] == "200 to 250", string.Join("; ", gap.Gaps));
        var bad = M.CheckZones([new("S1", 0, 120), new("S3", 100, 200), new("S2", 300, 290)], secs, 250, 0.001);
        check("as-built: overlaps, unknown sections and backwards zones are errors", bad.Errors.Count >= 3 && bad.Errors.Any(e => e.Contains("overlap")) && bad.Errors.Any(e => e.Contains("\"S3\"")), string.Join("; ", bad.Errors));

        // Gravity block courses with setbacks.
        var courses = M.Courses(-12, [new(1.5, 6, 0), new(1.5, 5, 0.3), new(1.5, 4, 0.6)]);
        check("as-built: each course sits on the one below", courses.Count == 3 && Math.Abs(M.Levels(courses[2]).Top - (-7.5)) < 1e-9 && Math.Abs(M.Levels(courses[1]).Bottom - (-10.5)) < 1e-9, "");
        check("as-built: a course's front is at its setback, its back at setback plus width", Math.Abs(courses[2].Min(p => p.u) - 0.6) < 1e-9 && Math.Abs(courses[2].Max(p => p.u) - 4.6) < 1e-9, "");
        check("as-built: courses are anticlockwise outlines", courses.All(x => M.Area(x) > 0), "");
        var threw = false; try { M.Courses(0, [new(0, 1, 0)]); } catch (ArgumentException) { threw = true; }
        check("as-built: a course with no height is refused", threw, "");

        // Outlines.
        check("as-built: a clockwise outline is turned round", M.Area(M.Ordered([(0, 0), (0, 1), (1, 1), (1, 0), (0, 0)])) > 0, "");
        check("as-built: a crossing outline is named", M.ProfileProblem([(0, 0), (2, 2), (2, 0), (0, 1)]) is { } p && p.Contains("crosses"), M.ProfileProblem([(0, 0), (2, 2), (2, 0), (0, 1)]) ?? "");
        check("as-built: a coping with a chamfer is a good outline", M.ProfileProblem(M.Ordered([(0, 0), (2, 0), (2, 1), (0.3, 1), (0, 0.7)])) is null, "");
        check("as-built: points in a line have no area", M.ProfileProblem([(0, 0), (1, 0), (2, 0)]) is { } q && q.Contains("no area"), "");

        // Stations and piles.
        var line = new List<(double, double)> { (0, 0), (100, 0), (100, 50) };
        var st = M.Station(line, (40, -3));
        check("as-built: a point's chainage and its offset to the right", Math.Abs(st.Chainage - 40) < 1e-9 && Math.Abs(st.Offset - 3) < 1e-9, $"{st.Chainage} {st.Offset}");
        var st2 = M.Station(line, (103, 20));
        check("as-built: chainage carries round a bend", Math.Abs(st2.Chainage - 120) < 1e-9 && Math.Abs(st2.Offset - 3) < 1e-9, $"{st2.Chainage} {st2.Offset}");
        var piles = M.Stations(10, 30, 2, 5);
        check("as-built: piles from the zone start plus start, every spacing, to the end", piles.SequenceEqual([12.0, 17, 22, 27]), string.Join(",", piles));

        // Levels against the reference.
        var parts = new List<M.PartLevels> { new("B12", "Deck", 3.35, 2.75, "top"), new("B12", "Coping", 2.91, 1.5, null), new("B13", "Deck", 3.00, 2.4, "both") };
        var refs = new List<M.RefLevels> { new("B12", "Floors", 3.35, 1.95), new("B13", "Floors", 3.55, 2.15) };
        var diff = M.LevelMismatches(parts, refs, 0.01, v => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        check("as-built: a matching top is not reported, a different one is with both levels", diff.Count == 2 && diff.All(d => d.StartsWith("B13 Deck")) && diff[0].Contains("3.000") && diff[0].Contains("3.550"), string.Join(" | ", diff));
        var none = M.LevelMismatches([new("B14", "Deck", 3, 2, "top")], refs, 0.01, v => v.ToString());
        check("as-built: no reference element in a zone is said, not passed", none.Count == 1 && none[0].Contains("no reference"), string.Join(" | ", none));
        check("as-built: distinct levels are rounded and highest first", M.Distinct([3.3501, 3.35, 2.75], 0.005).SequenceEqual([3.35, 2.75]), "");
    }

    /// <summary>The chat is told to read the drawings and model each section, never a copied typical one.</summary>
    public static void RunSources(string root, Action<string, bool, string> check)
    {
        var prompt = File.ReadAllText(Path.Combine(root, "src", "SketchProMax", "Agent", "SystemPrompt.cs"));
        check("as-built: the prompt sends drawings to read_drawing and sections to model_from_sections",
            prompt.Contains("read_drawing") && prompt.Contains("model_from_sections") && prompt.Contains("never a typical section copied"), "");
        var registry = File.ReadAllText(Path.Combine(root, "src", "SketchProMax", "Tools", "ToolRegistry.cs"));
        check("as-built: both tools are registered", registry.Contains("new ReadDrawingTool()") && registry.Contains("new ModelFromSectionsTool()"), "");
    }
}
