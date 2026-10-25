using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// The BEP data Excel: one row per model type in use with its Assembly Code (and the code the checklist's UniFormat rules
/// suggest) and every parameter the LOI matrix asks for, with what is still missing. The user fills it in Excel and
/// imports it: each changed cell is written to the type, or to its elements when the parameter is on the elements.
/// Revit API context only; the cell rules are in <see cref="BepSheetText"/>.
/// </summary>
internal static class BepSheet
{
    public const string SheetName = BepSheetText.SheetName;

    /// <summary>Writes the BEP data Excel of the model and opens it; a sentence saying where.</summary>
    public static string Export(Document doc, ChecklistMath.Definition? def)
    {
        var matrix = ChecklistMath.ParseLoi(ChecklistMath.Rule(def, "loimatrix"));
        var codeParam = ChecklistMath.RuleList(ChecklistMath.Rule(def, "classification")).FirstOrDefault() ?? "Assembly Code";
        var map = ChecklistFixes.UniFormatMap(def);
        var byType = ChecklistFacts.Instances(doc).GroupBy(e => e.GetTypeId().Value).ToDictionary(g => g.Key, g => g.ToList());
        var types = byType.Keys.Select(id => doc.GetElement(new ElementId(id)) as ElementType).OfType<ElementType>()
            .OrderBy(t => t.Category?.Name ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (types.Count == 0) return "The model has no model elements: nothing to export.";
        var loiParams = matrix.SelectMany(m => m.Params).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var columns = new List<string>(BepSheetText.Fixed) { codeParam, "Assembly Description", "Suggested code" };
        columns.AddRange(loiParams);
        columns.Add("Still missing");
        var rows = new List<OfficeXlsx.Row>();
        var missingTotal = 0;
        foreach (var t in types)
        {
            var elements = byType[t.Id.Value];
            var category = t.Category?.Name ?? "";
            var code = Value(doc, t, elements, codeParam) ?? "";
            var entry = matrix.FirstOrDefault(m => code.Length > 0 && ChecklistMath.Like(code, m.Key) || category.Equals(m.Key, StringComparison.OrdinalIgnoreCase));
            var needed = entry.Params ?? [];
            var missing = new List<string>();
            if (code.Length == 0) missing.Add(codeParam);
            var cells = new List<object?> { t.Id.Value.ToString(CultureInfo.InvariantCulture), category, t.FamilyName, t.Name, elements.Count, code,
                t.LookupParameter("Assembly Description")?.AsString() ?? "",
                code.Length == 0 ? ChecklistFixes.UniFormatFor(category, t.FamilyName, t.Name, map, ChecklistFixes.ThickMm(t), valuesOf: ChecklistFixes.ValuesOf(t, elements)) ?? "" : "" };
            foreach (var p in loiParams)
            {
                var v = Value(doc, t, elements, p);
                var wanted = needed.Contains(p, StringComparer.OrdinalIgnoreCase);
                if (wanted && string.IsNullOrWhiteSpace(v)) missing.Add(p);
                cells.Add(v ?? (wanted ? "" : BepSheetText.NotNeeded));
            }
            missingTotal += missing.Count;
            cells.Add(string.Join(", ", missing));
            rows.Add(new OfficeXlsx.Row(cells));
        }
        var how = new List<OfficeXlsx.Row>
        {
            new(["Fill the empty cells and those listed under Still missing, save, then Pre-submission checklist > Import the BEP data Excel."]),
            new(["Suggested code: what the checklist's UniFormat rules give; copy it into the code column to use it."]),
            new([$"{BepSheetText.NotNeeded}: the LOI matrix doesn't ask this of the type's code. {BepSheetText.Varies}: its elements differ; type one value to give them all."]),
            new(["Lengths and sizes in the project's units (e.g. 1200 or 1200 mm); materials by their name in the model. Keep the Type id column."]),
        };
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "BEP data");
        Directory.CreateDirectory(folder);
        var path = OfficeDocs.FreePath(folder, OfficeDocs.SafeName(Path.GetFileNameWithoutExtension(doc.Title) + " BEP data"), ".xlsx");
        var widths = columns.Select((c, i) => i == 0 ? 10.0 : i < 4 ? 26 : c == "Still missing" ? 40 : 16).ToList();
        OfficeXlsx.Write(path, "BEP data", [
            new(SheetName, $"BEP data of {doc.Title}: {types.Count} types, {missingTotal} values to fill", columns, rows, columns.Select(_ => "text").ToList(), widths, true),
            new("How to fill it", "How to fill it", ["Notes"], how, ["text"], [120], false),
        ]);
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return $"Saved {Path.GetFileName(path)} ({types.Count} types, {missingTotal} values to fill) in {folder}. Fill it in, save it, then Import the BEP data Excel.";
    }

    /// <summary>Writes the filled cells back: to the type's parameter, else to its elements'. A sentence saying what changed.</summary>
    public static string Import(Document doc, string? path)
    {
        if (path is null || !File.Exists(path)) return "Pick the BEP data Excel to import.";
        List<Dictionary<string, string>> table;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            table = BepSheetText.Table(ChecklistMath.ReadSheets(stream));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        { return $"Couldn't read {Path.GetFileName(path)} ({ex.Message}). Close it in Excel and try again."; }
        if (table.Count == 0) return $"{Path.GetFileName(path)} has no \"{SheetName}\" sheet with a Type id column: export it from here first.";
        int changed = 0, types = 0;
        var problems = new List<string>();
        var byType = ChecklistFacts.Instances(doc).GroupBy(e => e.GetTypeId().Value).ToDictionary(g => g.Key, g => g.ToList());
        using (var t = new Transaction(doc, "Sketch ProMax: import the BEP data"))
        {
            t.Start();
            foreach (var row in table)
            {
                if (!long.TryParse(row.TryGetValue(BepSheetText.Fixed[0], out var idText) ? idText : "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                    || doc.GetElement(new ElementId(id)) is not ElementType type) continue;
                var elements = byType.TryGetValue(id, out var list) ? list : [];
                var before = changed;
                foreach (var cell in row)
                {
                    var (name, text) = (cell.Key, cell.Value);
                    if (!BepSheetText.Writable(name, text) || Value(doc, type, elements, name) == text.Trim()) continue;
                    var said = Write(doc, type, elements, name, text.Trim());
                    if (said.Length == 0) changed++;
                    else if (problems.Count < 40) problems.Add($"{ChecklistFacts.TypeLabel(type)}: {name}: {said}");
                }
                if (changed > before) types++;
            }
            t.Commit();
        }
        return $"Wrote {changed} value{(changed == 1 ? "" : "s")} on {types} type{(types == 1 ? "" : "s")} from {Path.GetFileName(path)}." +
               (problems.Count > 0 ? $" Not written ({problems.Count}): " + string.Join("; ", problems.Take(8)) + (problems.Count > 8 ? " and more." : ".") : "");
    }

    /// <summary>The value shown for a parameter: the type's, else the one its elements share, else "(varies)"; null when none has it.</summary>
    private static string? Value(Document doc, ElementType type, List<Element> elements, string name)
    {
        if (type.LookupParameter(name) is { } tp) return Show(doc, tp);
        var values = elements.Select(e => e.LookupParameter(name)).Where(p => p is not null).Select(p => Show(doc, p!)).Distinct().Take(2).ToList();
        return values.Count == 0 ? null : values.Count == 1 ? values[0] : BepSheetText.Varies;
    }

    private static string Show(Document doc, Parameter p) => p.StorageType switch
    {
        StorageType.String => (p.AsString() ?? "").Trim(),
        StorageType.ElementId => p.AsElementId() is { } id && id != ElementId.InvalidElementId ? doc.GetElement(id)?.Name ?? "" : "",
        _ => p.HasValue ? (p.AsValueString() ?? "").Trim() : "",
    };

    /// <summary>Sets the type's parameter, else each element's; "" when written, else why not.</summary>
    private static string Write(Document doc, ElementType type, List<Element> elements, string name, string text)
    {
        var targets = type.LookupParameter(name) is { } tp ? [tp] : elements.Select(e => e.LookupParameter(name)).Where(p => p is not null).Select(p => p!).ToList();
        if (targets.Count == 0) return "no such parameter on the type or its elements (Fill the LOI data adds it)";
        foreach (var p in targets)
        {
            if (p.IsReadOnly) return "read-only";
            try
            {
                var ok = p.StorageType switch
                {
                    StorageType.String => p.Set(text),
                    StorageType.Integer when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => p.Set(n),
                    StorageType.ElementId => Material(doc, text) is { } m && p.Set(m),
                    _ => p.SetValueString(text),
                };
                if (!ok) return p.StorageType == StorageType.ElementId ? $"no material named \"{text}\" in the model" : $"Revit didn't take \"{text}\"";
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException)
            { return ex.Message; }
        }
        return "";
    }

    private static ElementId? Material(Document doc, string name) =>
        new FilteredElementCollector(doc).OfClass(typeof(Material)).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
}
