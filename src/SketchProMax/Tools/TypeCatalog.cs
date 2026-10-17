namespace SketchProMax.Tools;

/// <summary>
/// Revit type catalogs (.txt beside an .rfa): a header naming each parameter as name##spec##units, then one line per type.
/// Revit parses a catalog inside LoadFamilySymbol and shows a modal dialog ("Could not parse column header") when the
/// header is wrong, which blocks everything until someone clicks it. So a catalog is checked here first. No Revit here.
/// </summary>
public static class TypeCatalog
{
    /// <summary>Why this header line can't be a type catalog header, or null when it can.</summary>
    public static string? HeaderProblem(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "the first line is empty";
        var cells = Split(header!);
        if (cells.Count < 2 || cells.Skip(1).All(c => c.Length == 0))
            return "the first line names no parameter (it needs at least one column like 'Width##length##millimeters')";
        for (var i = 1; i < cells.Count; i++)
        {
            var parts = cells[i].Split(["##"], StringSplitOptions.None);
            if (parts.Length != 3 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
                return $"column {i + 1} header '{cells[i]}' is not name##spec##units";
        }
        return null;
    }

    /// <summary>Why these catalog lines can't be loaded, or null when they can: the header, and no row with more cells than it.</summary>
    public static string? Problem(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return "the file is empty";
        if (HeaderProblem(lines[0]) is { } header) return header;
        var columns = Split(lines[0]).Count;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (Split(lines[i]).Count > columns) return $"line {i + 1} has more values than the header has columns";
        }
        return null;
    }

    /// <summary>The type names (first cell of each line after the header).</summary>
    public static List<string> Types(IEnumerable<string> lines) =>
        lines.Skip(1).Select(l => Split(l)[0]).Where(n => n.Length > 0).ToList();

    /// <summary>A catalog line's cells, with quoted cells (which may hold commas) kept whole.</summary>
    public static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }
}
