namespace SketchProMax.Tools;

/// <summary>The BEP data Excel's cell rules (no Revit API): its fixed columns, the markers, and the rows read back.</summary>
internal static class BepSheetText
{
    public static readonly string[] Fixed = ["Type id", "Category", "Family", "Type", "Elements"];
    public const string SheetName = "BEP data";
    public const string NotNeeded = "n/a";
    public const string Varies = "(varies)";
    private static readonly string[] ReadOnly = ["Assembly Description", "Suggested code", "Still missing"];

    /// <summary>Whether a filled cell is written back: not a fixed or information column, not empty, not a marker.</summary>
    public static bool Writable(string column, string text) =>
        !Fixed.Contains(column, StringComparer.OrdinalIgnoreCase) && !ReadOnly.Contains(column, StringComparer.OrdinalIgnoreCase)
        && text.Trim().Length > 0 && text.Trim() != NotNeeded && text.Trim() != Varies;

    /// <summary>The BEP data sheet's rows as column name to text, from the row naming Type id on.</summary>
    public static List<Dictionary<string, string>> Table(Dictionary<string, List<(int Row, Dictionary<int, string> Cells)>> sheets)
    {
        var list = new List<Dictionary<string, string>>();
        if (!sheets.TryGetValue(SheetName, out var rows)) return list;
        Dictionary<int, string>? head = null;
        foreach (var (_, cells) in rows)
        {
            if (head is null)
            {
                if (cells.Values.Any(v => string.Equals(v.Trim(), Fixed[0], StringComparison.OrdinalIgnoreCase))) head = cells.ToDictionary(c => c.Key, c => c.Value.Trim());
                continue;
            }
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in head)
                if (h.Value.Length > 0) row[h.Value] = cells.TryGetValue(h.Key, out var v) ? v : "";
            if (row.TryGetValue(Fixed[0], out var id) && id.Trim().Length > 0) list.Add(row);
        }
        return list;
    }
}
