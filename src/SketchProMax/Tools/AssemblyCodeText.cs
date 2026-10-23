using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// The model's Assembly Code table from the project's LOI matrix (a UniFormat 2010 Excel: codes such as A1020.15 with
/// their names, wherever they sit in a row) or from a Revit assembly code file, written in Revit's table format
/// (code, description, level, tab separated) so it can be loaded into the model. No Revit API here.
/// </summary>
internal static class AssemblyCodeText
{
    private static readonly Regex Code = new(@"^([A-Z](?:\d{2}(?:\d{2}(?:\.\d{2,3})?)?)?)(?:\s+(\S.*))?$", RegexOptions.Compiled);

    /// <summary>The codes and names found in the rows' cells, in table order, with any missing parent added.</summary>
    public static List<(string Code, string Name)> FromRows(IEnumerable<IReadOnlyList<string>> rows)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var cells = row.Select(c => Regex.Replace(c ?? "", @"\s+", " ").Trim()).ToList();
            for (var i = 0; i < cells.Count; i++)
            {
                var m = Code.Match(cells[i]);
                if (!m.Success) continue;
                var code = m.Groups[1].Value;
                var name = m.Groups[2].Value.Trim();
                // A lone letter is only a code with its name in the cell ("A SUBSTRUCTURE"), not a stray letter.
                if (code.Length == 1 && name.Length == 0) continue;
                if (name.Length == 0 && i + 1 < cells.Count && IsName(cells[i + 1])) name = cells[i + 1];
                if (!found.TryGetValue(code, out var had) || had.Length == 0) found[code] = name;
            }
        }
        foreach (var code in found.Keys.ToList())
            for (var parent = Parent(code); parent is not null; parent = Parent(parent))
                if (!found.ContainsKey(parent)) found[parent] = "";
        return found.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Length > 0 ? p.Value : p.Key)).ToList();
    }

    /// <summary>The codes of a Revit assembly code file (code, description, level, categories).</summary>
    public static List<(string Code, string Name)> FromTable(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r').Split('\t')).Where(p => p.Length >= 2 && p[0].Trim().Length > 0)
            .Select(p => (p[0].Trim(), p[1].Trim())).ToList();

    private static bool IsName(string cell) => cell.Length > 2 && cell.Any(char.IsLetter) && !Code.IsMatch(cell);

    /// <summary>The code one level up: A1020.15 > A1020 > A10 > A; null for a letter.</summary>
    public static string? Parent(string code) =>
        code.IndexOf('.') is var dot and > 0 ? code.Substring(0, dot) : code.Length == 5 ? code.Substring(0, 3) : code.Length == 3 ? code.Substring(0, 1) : null;

    /// <summary>The level in the tree: A 1, A10 2, A1010 3, A1010.10 4.</summary>
    public static int Level(string code) => code.Contains('.') ? 4 : code.Length switch { 1 => 1, 3 => 2, _ => 3 };

    /// <summary>Revit's assembly code file text: one "code, name, level, (no category)" line each.</summary>
    public static string TableText(IEnumerable<(string Code, string Name)> codes)
    {
        var sb = new StringBuilder();
        foreach (var (code, name) in codes) sb.Append(code).Append('\t').Append(name.Replace('\t', ' ')).Append('\t').Append(Level(code)).Append("\t\r\n");
        return sb.ToString();
    }

    /// <summary>The codes in an LOI Excel (every sheet) or a Revit assembly code .txt.</summary>
    public static List<(string Code, string Name)> Read(string path)
    {
        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return FromTable(File.ReadAllText(path));
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var sheets = ChecklistMath.ReadSheets(stream);
        return FromRows(sheets.Values.SelectMany(rows => rows.Select(r =>
            (IReadOnlyList<string>)(r.Cells.Count == 0 ? [] : Enumerable.Range(0, r.Cells.Keys.Max() + 1).Select(i => r.Cells.TryGetValue(i, out var v) ? v : "").ToList()))));
    }

    /// <summary>The "key, code" lines (tab separated) of the Assembly Codes list the user kept: a type id or "cat:" and a category name, codes not empty.</summary>
    public static List<(string Key, string Code)> Picked(string? answer) =>
        (answer ?? "").Split('\n').Select(l => l.TrimEnd('\r').Split('\t'))
            .Where(p => p.Length == 2 && p[1].Trim().Length > 0 && (p[0].Trim().StartsWith("cat:", StringComparison.Ordinal) && p[0].Trim().Length > 4
                || long.TryParse(p[0].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)))
            .Select(p => (p[0].Trim(), p[1].Trim())).ToList();
}
