using System.Globalization;
using System.Text;
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
    /// Why an architectural floor is not turned into a slab, or null to take it. The Structural flag always takes it;
    /// otherwise floors thinner than the minimum are skipped, and a structural word in the type name or a concrete
    /// structural material takes it. Finish and build-up words never override those: they only name why a floor with
    /// none of them is skipped.
    /// </summary>
    public static string? FloorSkipReason(FloorFacts f, IReadOnlyCollection<string> structureWords, IReadOnlyCollection<string> finishWords, double minThicknessMm)
    {
        if (f.Structural) return null;
        if (f.ThicknessMm > 0 && f.ThicknessMm < minThicknessMm - 0.5)
            return $"thin non-structural floor (under {minThicknessMm.ToString("0", CultureInfo.InvariantCulture)} mm)";
        if (FirstWord(f.TypeName, structureWords) is not null) return null;
        if (f.Material is { } m && m.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        if (FirstWord(f.TypeName, finishWords) is { } finish) return $"finish or build-up floor (type name has '{finish}')";
        return "not structural (no Structural flag, concrete structural material or structural word in the type name)";
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

    /// <summary>
    /// How a 2D reinforcement role's family is recognised among the loaded detail and annotation families when none is
    /// given or remembered: every group in Need must have a word in the family name (anywhere, ignoring case), Bonus
    /// words rank it up, Avoid words down, and LineBased (when set) must match how the family is placed.
    /// </summary>
    public sealed record DetailRoleRule(string Key, string[][] Need, string[] Bonus, string[] Avoid, bool? LineBased);

    public static readonly DetailRoleRule AdditionalBarsRule = new("additional_bars",
        [["rft", "rebar", "reinf", "bar"]], ["add", "additional", "extra", "top", "bottom"], ["mesh", "tie", "stirrup", "transvers", "column", "section", "tag"], true);
    public static readonly DetailRoleRule MeshRule = new("mesh",
        [["mesh"]], ["rebar", "rft", "reinf"], ["tag"], false);
    public static readonly DetailRoleRule ColumnSectionRule = new("column_section",
        [["column"], ["rft", "rebar", "reinf"]], ["section", "beam", "rect"], ["tie", "stirrup", "transvers", "tag"], false);
    public static readonly DetailRoleRule TiesRule = new("ties",
        [["tie", "stirrup", "transvers", "link"]], ["rebar", "rft", "bar"], ["tag", "mesh"], null);

    /// <summary>How well a family fits a role (higher is better), or null when it doesn't fit at all.</summary>
    public static int? DetailRoleScore(DetailRoleRule rule, string familyName, bool lineBased)
    {
        if (rule.LineBased is { } lb && lb != lineBased) return null;
        bool Has(string w) => familyName.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;
        if (!rule.Need.All(group => group.Any(Has))) return null;
        return 10 + rule.Need.Sum(group => group.Count(Has)) + 3 * rule.Bonus.Count(Has) - 5 * rule.Avoid.Count(Has);
    }

    /// <summary>The families that fit a role, best first (ties by name), each once.</summary>
    public static List<string> RankDetailFamilies(DetailRoleRule rule, IEnumerable<(string family, bool lineBased)> loaded) =>
        loaded.Select(f => (f.family, score: DetailRoleScore(rule, f.family, f.lineBased)))
            .Where(f => f.score is not null)
            .GroupBy(f => f.family, StringComparer.OrdinalIgnoreCase).Select(g => (family: g.Key, score: g.Max(f => f.score!.Value)))
            .OrderByDescending(f => f.score).ThenBy(f => f.family, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.family).ToList();

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
    /// <summary>One group of elements arch_to_structure left out: "kind: reason", how many, and a few of their type names.</summary>
    public sealed record ArchSkip(string Reason, int Count, IReadOnlyCollection<string> Types);

    /// <summary>
    /// What to change so arch_to_structure converts the groups it skipped: one hint per kind of skip reason, naming
    /// the argument (include_architectural_columns, floor_type_words, ...) and, where it helps, a word from the types.
    /// </summary>
    public static List<string> ArchSkipHints(IEnumerable<ArchSkip> skipped, int alreadyMade)
    {
        var hints = new List<string>();
        void Add(string hint) { if (!hints.Contains(hint)) hints.Add(hint); }
        static string? Word(IEnumerable<string> types) =>
            types.Select(t => Regex.Match(t, "[A-Za-z]{2,}")).Where(m => m.Success).Select(m => m.Value).FirstOrDefault();
        foreach (var s in skipped)
        {
            var r = s.Reason;
            var example = Word(s.Types) is { } w ? $" (e.g. '{w}')" : "";
            if (r.StartsWith("columns:", StringComparison.Ordinal) && r.Contains("architectural Columns"))
                Add("Architectural columns were left out: pass include_architectural_columns=true to convert them too.");
            else if (r.Contains("structural_columns is false"))
                Add("Pass structural_columns=true to take the source's Structural Columns.");
            else if (r.Contains("not cast-in-place concrete"))
                Add("Steel, timber and precast columns are not rebuilt in concrete: copy them with copy_monitor.");
            else if (r.Contains("already structural in this model"))
                Add("Elements already structural in this model are not converted again: to rebuild from the architect's model, give its link name as source.");
            else if (r.Contains("no point location"))
                Add("Slanted and in-place columns are not converted: model them by hand.");
            else if (r.StartsWith("floors:", StringComparison.Ordinal) && r.Contains("thin"))
                Add("Thin floors were left out: lower min_floor_thickness, or set those floors' Structural flag.");
            else if (r.StartsWith("floors:", StringComparison.Ordinal))
                Add($"Floors were left out: pass floor_type_words with a word from their type names{example}, or set those floors' Structural flag.");
            else if (r.StartsWith("walls:", StringComparison.Ordinal))
                Add($"Walls were left out: pass wall_type_words with a word from their type names{example}, or min_wall_thickness, or set those walls' Structural flag.");
        }
        if (alreadyMade > 0)
            Add($"{alreadyMade} element(s) were already converted by an earlier run and are skipped; delete what that run made to remake them.");
        return hints;
    }

    /// <summary>
    /// The error when arch_to_structure made nothing: what it found in the source per kind, every skip reason with its
    /// count and types, what Revit refused, and the hints. Never just "Nothing was made."
    /// </summary>
    public static string NothingMadeMessage(string source, string? level, IReadOnlyCollection<string> what, IReadOnlyDictionary<string, int> found,
        IReadOnlyCollection<ArchSkip> skipped, IReadOnlyCollection<string> errors, int alreadyMade)
    {
        var on = level is null ? "" : $" on level '{level}'";
        var sb = new StringBuilder($"Nothing was made from {source}{on}.");
        var parts = found.Where(f => f.Value > 0).Select(f => $"{f.Value} {f.Key}").ToList();
        sb.Append(parts.Count == 0
            ? $" It has no {string.Join(", ", what.Where(w => w != "openings"))} to convert{on}" + (level is null ? "." : " (check the level name: it is the source's level).")
            : $" Found {string.Join(", ", parts)}.");
        if (skipped.Count > 0)
            sb.Append(" Skipped: " + string.Join("; ", skipped.Select(s => $"{s.Reason}: {s.Count}" + (s.Types.Count > 0 ? $" [{string.Join(", ", s.Types.Take(4))}]" : ""))) + ".");
        if (errors.Count > 0)
            sb.Append(" Revit refused: " + string.Join("; ", errors.Take(10)) + (errors.Count > 10 ? $"; and {errors.Count - 10} more" : "") + ".");
        var hints = ArchSkipHints(skipped, alreadyMade);
        if (hints.Count > 0) sb.Append(" " + string.Join(" ", hints));
        return sb.ToString();
    }
}
