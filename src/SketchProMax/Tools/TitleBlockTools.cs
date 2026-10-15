using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Finding sheets, their title blocks and the fields shown in them.</summary>
internal static class TitleBlocks
{
    public static List<FamilySymbol> Types(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .Cast<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();

    /// <summary>Title block instances per sheet id, collected once.</summary>
    public static Dictionary<long, List<FamilyInstance>> BySheet(Document doc)
    {
        var map = new Dictionary<long, List<FamilyInstance>>();
        foreach (var tb in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType().OfType<FamilyInstance>())
        {
            var owner = tb.OwnerViewId.Value;
            if (!map.TryGetValue(owner, out var list)) map[owner] = list = [];
            list.Add(tb);
        }
        return map;
    }

    /// <summary>Sheets picked by number list, number prefix, or all (placeholders excluded).</summary>
    public static List<ViewSheet> Sheets(Document doc, ToolArgs args)
    {
        var numbers = args.StringList("sheet_numbers").Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefix = args.OptString("number_starts_with");
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder)
            .Where(s => numbers.Count == 0 || numbers.Contains(s.SheetNumber))
            .Where(s => string.IsNullOrEmpty(prefix) || s.SheetNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.SheetNumber).ToList();
        var missing = numbers.Where(n => sheets.All(s => !s.SheetNumber.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0) throw new ArgumentException($"No sheet numbered {string.Join(", ", missing)}.");
        return sheets;
    }

    public static FamilySymbol FindType(Document doc, ToolArgs args)
    {
        var types = Types(doc);
        if (types.Count == 0) throw new InvalidOperationException("No title block family is loaded. Load one with load_family (e.g. search 'A1 metric').");
        if (args.OptLong("type_id") is { } id)
            return types.FirstOrDefault(t => t.Id.Value == id) ?? throw new ArgumentException($"{id} is not a title block type. Call list_title_blocks.");
        var name = args.OptString("title_block") ?? throw new ArgumentException("Give title_block ('Family: Type' or type name) or type_id.");
        return TypeMatch.Find(types, name, "title block");
    }

    /// <summary>Text-like fields a user would fill in: editable, with a definition, not an id.</summary>
    public static IEnumerable<Parameter> Fields(Element e) =>
        e.Parameters.Cast<Parameter>()
            .Where(p => p.Definition != null && !p.IsReadOnly && p.StorageType != StorageType.ElementId && p.StorageType != StorageType.None)
            .GroupBy(p => p.Definition.Name).Select(g => g.First())
            .OrderBy(p => p.Definition.Name);
}

public sealed class ListTitleBlocksTool : IRevitTool
{
    public string Name => "list_title_blocks";
    public string Description =>
        "Title block overview: loaded title block types and how many sheets use each, sheets with no title block or with " +
        "more than one, and title blocks not at the sheet origin. With sheet_number, also lists every field that can be " +
        "filled on that sheet (sheet parameters and the title block's own instance and type parameters) with current values.";
    public string PropertiesJson => """
    {
      "sheet_number": {"type": "string", "description": "Show the fillable fields of this sheet's title block."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var bySheet = TitleBlocks.BySheet(doc);
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber).ToList();
        var usage = bySheet.Values.SelectMany(l => l).GroupBy(i => i.Symbol.Id.Value).ToDictionary(g => g.Key, g => g.Count());

        var none = sheets.Where(s => !bySheet.ContainsKey(s.Id.Value)).Select(s => s.SheetNumber).ToList();
        var several = sheets.Where(s => bySheet.TryGetValue(s.Id.Value, out var l) && l.Count > 1).Select(s => s.SheetNumber).ToList();
        var offOrigin = bySheet.Values.SelectMany(l => l)
            .Where(i => i.Location is LocationPoint lp && lp.Point.GetLength() > 0.01)
            .Select(i => (doc.GetElement(i.OwnerViewId) as ViewSheet)?.SheetNumber).Where(n => n != null).Distinct().ToList();

        object? fields = null;
        if (args.OptString("sheet_number") is { } number)
        {
            var sheet = sheets.FirstOrDefault(s => s.SheetNumber.Equals(number.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No sheet numbered '{number}'.");
            var tb = bySheet.TryGetValue(sheet.Id.Value, out var l) ? l.First() : null;
            object Dump(Element e) => TitleBlocks.Fields(e).Select(p => new { name = p.Definition.Name, value = RevitHelpers.ParamValue(p) }).ToList();
            fields = new
            {
                sheet = sheet.SheetNumber,
                sheet_fields = Dump(sheet),
                title_block = tb is null ? null : $"{tb.Symbol.FamilyName}: {tb.Symbol.Name}",
                title_block_fields = tb is null ? null : Dump(tb),
                title_block_type_fields = tb is null ? null : Dump(tb.Symbol),
                project_information = Dump(doc.ProjectInformation),
            };
        }

        return ToolResult.Ok(new
        {
            types = TitleBlocks.Types(doc).Select(t => new
            {
                type_id = t.Id.Value, name = $"{t.FamilyName}: {t.Name}",
                sheets = usage.TryGetValue(t.Id.Value, out var n) ? n : 0,
            }),
            sheet_count = sheets.Count,
            sheets_without_title_block = new { count = none.Count, numbers = none.Take(50) },
            sheets_with_several = new { count = several.Count, numbers = several.Take(50) },
            not_at_origin = new { count = offOrigin.Count, numbers = offOrigin.Take(50) },
            fields,
        });
    }
}

public sealed class FillTitleBlocksTool : IRevitTool
{
    public string Name => "fill_title_blocks";
    public string Description =>
        "Fill title block fields on many sheets at once (Drawn By, Checked By, Approved By, Sheet Issue Date, or any custom " +
        "field in the title block), and project-wide fields shown on every sheet (Client Name, Project Number, Project " +
        "Address...). Each sheet field is written to the sheet if it has that parameter, else to its title block. " +
        "Values may use {number} and {name} for the sheet's own number and name. Call list_title_blocks with a sheet_number first to see the field names.";
    public string PropertiesJson => """
    {
      "values": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Field name -> value, set on each chosen sheet."},
      "project_info": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Project Information field -> value (shared by all sheets)."},
      "sheet_numbers": {"type": "array", "items": {"type": "string"}, "description": "Default: every sheet."},
      "number_starts_with": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var values = args.Has("values") ? args.Raw["values"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText()) : [];
        var project = args.Has("project_info") ? args.Raw["project_info"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText()) : [];
        if (values.Count == 0 && project.Count == 0) return ToolResult.Error("Give values (per sheet) and/or project_info.");

        var sheets = values.Count > 0 ? TitleBlocks.Sheets(doc, args) : [];
        var bySheet = TitleBlocks.BySheet(doc);
        var problems = new Dictionary<string, List<string>>();
        void Problem(string field, string sheet)
        {
            if (!problems.TryGetValue(field, out var l)) problems[field] = l = [];
            if (l.Count < 20) l.Add(sheet);
        }

        int written = 0;
        var projectResults = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Fill title blocks");
        foreach (var kv in project)
        {
            var p = doc.ProjectInformation.LookupParameter(kv.Key);
            var status = p is null ? "no such field in Project Information"
                : p.IsReadOnly ? "read-only"
                : SetParametersTool.Write(p, kv.Value) ? "ok" : "value rejected";
            projectResults.Add(new { field = kv.Key, status });
        }
        foreach (var sheet in sheets)
        {
            var tb = bySheet.TryGetValue(sheet.Id.Value, out var l) ? l.First() : null;
            foreach (var kv in values)
            {
                var value = kv.Value.Replace("{number}", sheet.SheetNumber).Replace("{name}", sheet.Name);
                var p = Writable(sheet, kv.Key) ?? (tb is null ? null : Writable(tb, kv.Key));
                if (p is null) { Problem($"{kv.Key}: no editable field with this name", sheet.SheetNumber); continue; }
                try
                {
                    if (SetParametersTool.Write(p, value)) written++;
                    else Problem($"{kv.Key}: value '{value}' rejected", sheet.SheetNumber);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { Problem($"{kv.Key}: {ex.Message}", sheet.SheetNumber); }
            }
        }
        t.Commit();
        return ToolResult.Ok(new
        {
            sheets = sheets.Count,
            fields_written = written,
            project_info = projectResults,
            problems = problems.Select(p => new { problem = p.Key, sheets = p.Value }),
        });
    }

    private static Parameter? Writable(Element e, string name)
    {
        var p = e.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Definition != null && !x.IsReadOnly &&
                    string.Equals(x.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
        return p;
    }
}

public sealed class SetSheetTitleBlockTool : IRevitTool
{
    public string Name => "set_sheet_title_block";
    public string Description =>
        "Put a title block type on sheets: swaps the existing title block's type, adds one to sheets that have none, " +
        "optionally removes duplicates (keeps one per sheet) and moves title blocks to the sheet origin.";
    public string PropertiesJson => """
    {
      "title_block": {"type": "string", "description": "'Family: Type' or type name (list_title_blocks). Omit to keep each sheet's current type."},
      "type_id": {"type": "integer"},
      "sheet_numbers": {"type": "array", "items": {"type": "string"}, "description": "Default: every sheet."},
      "number_starts_with": {"type": "string"},
      "add_missing": {"type": "boolean", "description": "Add a title block to sheets without one (default true)."},
      "remove_duplicates": {"type": "boolean", "description": "Default false."},
      "move_to_origin": {"type": "boolean", "description": "Default false."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var wantsType = args.Has("title_block") || args.Has("type_id");
        var type = wantsType ? TitleBlocks.FindType(doc, args) : null;
        var addMissing = args.OptBool("add_missing", true);
        var removeDuplicates = args.OptBool("remove_duplicates");
        var toOrigin = args.OptBool("move_to_origin");
        if (!wantsType && !removeDuplicates && !toOrigin)
            return ToolResult.Error("Nothing to do: give title_block, or set remove_duplicates or move_to_origin.");

        var sheets = TitleBlocks.Sheets(doc, args);
        var bySheet = TitleBlocks.BySheet(doc);
        int changed = 0, added = 0, removed = 0, moved = 0;
        var skipped = new List<string>();

        using var t = RevitHelpers.StartTransaction(doc, "Set sheet title blocks");
        if (type is { IsActive: false }) { type.Activate(); doc.Regenerate(); }
        foreach (var sheet in sheets)
        {
            var list = bySheet.TryGetValue(sheet.Id.Value, out var l) ? l : [];
            if (list.Count == 0)
            {
                if (type is null || !addMissing) { skipped.Add(sheet.SheetNumber); continue; }
                doc.Create.NewFamilyInstance(XYZ.Zero, type, sheet);
                added++;
                continue;
            }
            if (removeDuplicates && list.Count > 1)
            {
                // Keep the one that already has the wanted type and sits at the origin.
                var keep = list.OrderByDescending(tb => type is not null && tb.Symbol.Id == type.Id)
                    .ThenBy(tb => tb.Location is LocationPoint p ? p.Point.GetLength() : double.MaxValue).First();
                foreach (var extra in list.Where(tb => tb.Id != keep.Id)) { doc.Delete(extra.Id); removed++; }
                list = [keep];
            }
            foreach (var tb in list)
            {
                if (type is not null && tb.Symbol.Id != type.Id) { tb.ChangeTypeId(type.Id); changed++; }
                if (toOrigin && tb.Location is LocationPoint lp && lp.Point.GetLength() > 1e-6)
                {
                    ElementTransformUtils.MoveElement(doc, tb.Id, XYZ.Zero - lp.Point);
                    moved++;
                }
            }
        }
        t.Commit();
        return ToolResult.Ok(new
        {
            sheets = sheets.Count, changed, added, removed, moved,
            title_block = type is null ? null : $"{type.FamilyName}: {type.Name}",
            sheets_left_without_title_block = skipped.Take(50),
        });
    }
}
