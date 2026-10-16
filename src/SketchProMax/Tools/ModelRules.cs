using System.Globalization;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Rules behind arch_to_structure, create_raft, link_model and check_model that need no Revit API, so the tests
/// check them directly: which architectural floors become slabs, type names by thickness, saved Revit versions,
/// and columns off the grid.
/// </summary>
internal static class ModelRules
{
    /// <summary>Type name words that mean a finish or build-up floor, not a slab.</summary>
    public static readonly string[] FloorFinishWords =
    [
        "Finish", "Screed", "Tile", "Tiling", "Topping", "Roof Layer", "Build-up", "Buildup", "Insulation", "Membrane",
        "Waterproof", "Paving", "Carpet", "Raised", "Vinyl", "Marble", "Granite", "Terrazzo", "Ceramic", "Parquet",
        "Laminate", "Gravel", "Cladding", "Render", "Plaster",
    ];

    /// <summary>Type name words that mean a structural slab.</summary>
    public static readonly string[] FloorStructureWords = ["Concrete", "RC", "Slab", "Structural", "SOG", "Hollowcore", "Hollow Core", "PT", "Post-Tension"];

    /// <summary>What arch_to_structure knows about an architectural floor.</summary>
    public sealed record FloorFacts(string TypeName, bool Structural, string? Material, double ThicknessMm);

    /// <summary>
    /// Why an architectural floor is not turned into a slab, or null to take it. Finish and build-up names are
    /// skipped first (even when flagged structural), then the Structural flag takes it, then floors thinner than
    /// the minimum are skipped, and the rest need a structural word in the type name or a concrete material.
    /// </summary>
    public static string? FloorSkipReason(FloorFacts f, IReadOnlyCollection<string> structureWords, IReadOnlyCollection<string> finishWords, double minThicknessMm)
    {
        if (FirstWord(f.TypeName, finishWords) is { } finish) return $"finish or build-up floor (type name has '{finish}')";
        if (f.Structural) return null;
        if (f.ThicknessMm > 0 && f.ThicknessMm < minThicknessMm - 0.5)
            return $"thin non-structural floor (under {minThicknessMm.ToString("0", CultureInfo.InvariantCulture)} mm)";
        if (FirstWord(f.TypeName, structureWords) is not null) return null;
        if (f.Material is { } m && m.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        return "not structural (no Structural flag, concrete material or structural word in the type name)";
    }

    /// <summary>
    /// The first word found in a type name. A word starts at a non-letter (so 'RC' matches 'FL_RC_250' but not
    /// 'Source'); words of three letters or fewer must also end at one, longer ones may run on ('Roof Layer' matches
    /// 'Roof Layers_510'). Spaces in a word also match '_' and '-'.
    /// </summary>
    public static string? FirstWord(string name, IEnumerable<string> words)
    {
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w)) continue;
            var body = string.Join(@"[\s_\-]*", w.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            var end = w.Trim().Length <= 3 ? "([^A-Za-z]|$)" : "";
            if (Regex.IsMatch(name, $@"(^|[^A-Za-z]){body}{end}", RegexOptions.IgnoreCase)) return w.Trim();
        }
        return null;
    }

    /// <summary>'*' and '?' wildcards, ignoring case; a pattern without wildcards matches names that contain it.</summary>
    public static bool Wildcard(string name, string pattern)
    {
        if (pattern.IndexOfAny(['*', '?']) < 0) return name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// A type name like another but for a new thickness: the last number that is a size (followed by nothing, a
    /// separator or 'mm', so '40MPa' is kept) is replaced, else the thickness is appended in the name's own style.
    /// </summary>
    public static string WithThickness(string name, int mm)
    {
        var sizes = Regex.Matches(name, @"(?<!\d)\d+(?!\d)(?=mm|[^A-Za-z]|$)", RegexOptions.IgnoreCase).Cast<Match>().ToList();
        if (sizes.Count > 0)
        {
            var last = sizes[sizes.Count - 1];
            return name.Substring(0, last.Index) + mm.ToString(CultureInfo.InvariantCulture) + name.Substring(last.Index + last.Length);
        }
        return name.IndexOf('_') >= 0 && name.IndexOf(' ') < 0 ? $"{name}_{mm}" : $"{name} {mm}mm";
    }

    /// <summary>True when a file saved in 'saved' (e.g. "2025", "Autodesk Revit 2025") is newer than the running release.</summary>
    public static bool IsNewerRelease(string saved, string running) =>
        Year(saved) is { } s && Year(running) is { } r && s > r;

    private static int? Year(string text) =>
        Regex.Match(text ?? "", @"(19|20)\d\d") is { Success: true } m ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;

    /// <summary>A grid line as seen from one column: its name, direction angle in plan (radians) and distance in mm.</summary>
    public sealed record GridDistance(string Name, double Angle, double DistanceMm);

    private static bool Parallel(GridDistance a, GridDistance b) => Math.Abs(Math.Cos(a.Angle - b.Angle)) > 0.99;

    /// <summary>
    /// Null when the column is on a grid intersection within the tolerance, else the finding with its offsets in mm
    /// from the nearest intersection (or the nearest grid when no two grids cross).
    /// </summary>
    public static string? OffGridIssue(IReadOnlyList<GridDistance> grids, double toleranceMm)
    {
        var on = grids.Where(g => g.DistanceMm <= toleranceMm).ToList();
        if (on.Any(a => on.Any(b => !Parallel(a, b)))) return null;
        string Mm(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        var tol = $"(tolerance {Mm(toleranceMm)} mm)";
        var best = grids.SelectMany((a, i) => grids.Skip(i + 1).Where(b => !Parallel(a, b)).Select(b => (a, b)))
            .OrderBy(p => p.a.DistanceMm * p.a.DistanceMm + p.b.DistanceMm * p.b.DistanceMm).FirstOrDefault();
        if (best.a is null)
        {
            var near = grids.OrderBy(g => g.DistanceMm).FirstOrDefault();
            return near is null ? "no grids to check against" : $"not at a grid intersection; {Mm(near.DistanceMm)} mm off grid {near.Name} {tol}";
        }
        var (x, y) = best.a.DistanceMm <= best.b.DistanceMm ? (best.a, best.b) : (best.b, best.a);
        return x.DistanceMm <= toleranceMm
            ? $"on grid {x.Name} but {Mm(y.DistanceMm)} mm off grid {y.Name} {tol}"
            : $"{Mm(x.DistanceMm)} mm off grid {x.Name} and {Mm(y.DistanceMm)} mm off grid {y.Name} {tol}";
    }

    /// <summary>
    /// True when an exceptions entry names this element: its id, Mark or Comments (ignoring case and spaces at the
    /// ends). An entry 'check_name:entry' (e.g. 'columns_off_grid:C12') applies to that check only.
    /// </summary>
    public static bool IsException(string check, long id, string? mark, string? comments, IEnumerable<string> exceptions)
    {
        foreach (var raw in exceptions)
        {
            var x = (raw ?? "").Trim();
            if (Regex.Match(x, @"^([a-z_]+):(.*)$") is { Success: true } scoped)
            {
                if (!string.Equals(scoped.Groups[1].Value, check, StringComparison.OrdinalIgnoreCase)) continue;
                x = scoped.Groups[2].Value.Trim();
            }
            if (x.Length == 0) continue;
            if (x == id.ToString(CultureInfo.InvariantCulture)) return true;
            if (mark is { } m && string.Equals(m.Trim(), x, StringComparison.OrdinalIgnoreCase)) return true;
            if (comments is { } c && string.Equals(c.Trim(), x, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
