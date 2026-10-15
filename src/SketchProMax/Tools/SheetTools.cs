using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class CreateSheetsTool : IRevitTool
{
    public string Name => "create_sheets";
    public string Description =>
        "Create one or many sheets in one go (a full 'cartoon set'), each with a title block and optional views " +
        "placed in a grid. Set placeholder=true for index-only sheets of other disciplines (no title block, not printed). " +
        "A view can sit on only one sheet (schedules excepted); views already placed are reported as skipped.";
    public string PropertiesJson => """
    {
      "sheets": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "number": {"type": "string"},
            "name": {"type": "string"},
            "title_block": {"type": "string", "description": "Title block type name (default: first loaded)."},
            "view_ids": {"type": "array", "items": {"type": "integer"}},
            "placeholder": {"type": "boolean"}
          },
          "required": ["number", "name"]
        }
      }
    }
    """;
    public string[] Required => ["sheets"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsElementType().Cast<FamilySymbol>().ToList();
        var existingNumbers = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Select(s => s.SheetNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Create sheets");
        foreach (var spec in args.Array("sheets"))
        {
            var number = spec.GetProperty("number").GetString()!;
            var name = spec.GetProperty("name").GetString()!;
            if (existingNumbers.Contains(number)) { results.Add(new { number, error = "sheet number already exists" }); continue; }

            ViewSheet sheet;
            if (spec.TryGetProperty("placeholder", out var ph) && ph.GetBoolean())
            {
                sheet = ViewSheet.CreatePlaceholder(doc);
            }
            else
            {
                var tbName = spec.TryGetProperty("title_block", out var tbp) ? tbp.GetString() : null;
                var tb = tbName is null ? titleBlocks.FirstOrDefault()
                    : titleBlocks.FirstOrDefault(s => string.Equals(s.Name, tbName, StringComparison.OrdinalIgnoreCase));
                if (tb is null) { results.Add(new { number, error = "no matching title block loaded" }); continue; }
                sheet = ViewSheet.Create(doc, tb.Id);
            }
            try
            {
                sheet.SheetNumber = number;
                sheet.Name = name;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
            {
                doc.Delete(sheet.Id);
                results.Add(new { number, error = $"could not set number/name ({ex.Message}); check for characters Revit refuses such as \\ : {{ }} [ ] | ; < > ? ` ~" });
                continue;
            }
            existingNumbers.Add(number);

            var ids = spec.TryGetProperty("view_ids", out var v) ? v.EnumerateArray().Select(e => e.GetInt64()).ToList() : [];
            var (placed, skipped) = ids.Count > 0 && !sheet.IsPlaceholder ? PlaceViews(doc, sheet, ids) : ([], []);
            results.Add(new { number, sheetId = sheet.Id.Value, placed, skipped });
        }
        t.Commit();
        return ToolResult.Ok(results);
    }

    internal static (List<long> placed, List<object> skipped) PlaceViews(Document doc, ViewSheet sheet, List<long> ids)
    {
        doc.Regenerate(); // the sheet outline is only valid after regeneration
        var outline = sheet.Outline;
        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(ids.Count)));
        int rows = Math.Max(1, (int)Math.Ceiling(ids.Count / (double)cols));
        double w = (outline.Max.U - outline.Min.U) / cols, h = (outline.Max.V - outline.Min.V) / rows;

        var placed = new List<long>();
        var skipped = new List<object>();
        for (int i = 0; i < ids.Count; i++)
        {
            var viewId = new ElementId(ids[i]);
            var center = new XYZ(outline.Min.U + w * (i % cols + 0.5), outline.Max.V - h * (i / cols + 0.5), 0);
            if (doc.GetElement(viewId) is ViewSchedule)
            {
                ScheduleSheetInstance.Create(doc, sheet.Id, viewId, center);
                placed.Add(ids[i]);
            }
            else if (Viewport.CanAddViewToSheet(doc, sheet.Id, viewId))
            {
                Viewport.Create(doc, sheet.Id, viewId, center);
                placed.Add(ids[i]);
            }
            else skipped.Add(new { id = ids[i], reason = "already on a sheet or not placeable" });
        }
        return (placed, skipped);
    }
}

public sealed class PlaceViewsOnSheetTool : IRevitTool
{
    public string Name => "place_views_on_sheet";
    public string Description => "Place existing views or schedules on an existing sheet, laid out in a grid.";
    public string PropertiesJson => """
    { "sheet_id": {"type": "integer"}, "view_ids": {"type": "array", "items": {"type": "integer"}} }
    """;
    public string[] Required => ["sheet_id", "view_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (doc.GetElement(new ElementId(args.OptLong("sheet_id") ?? -1)) is not ViewSheet sheet)
            return ToolResult.Error("sheet_id is not a sheet.");
        using var t = RevitHelpers.StartTransaction(doc, $"Place views on {sheet.SheetNumber}");
        var (placed, skipped) = CreateSheetsTool.PlaceViews(doc, sheet, args.LongList("view_ids"));
        t.Commit();
        return ToolResult.Ok(new { placed, skipped });
    }
}

public sealed class RenameElementsTool : IRevitTool
{
    public string Name => "rename_elements";
    public string Description =>
        "Rename or renumber many elements at once: views, sheets (name and/or number), levels, grids, rooms " +
        "(name and/or number), family types. Use this to apply firm naming conventions across the model.";
    public string PropertiesJson => """
    {
      "items": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "element_id": {"type": "integer"},
            "name": {"type": "string"},
            "number": {"type": "string", "description": "Sheet number or room number."}
          },
          "required": ["element_id"]
        }
      }
    }
    """;
    public string[] Required => ["items"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var items = args.Array("items").ToList();
        var results = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Rename elements");

        // Sheet and room numbers must be unique at every step, so park changing numbers on temporary values first.
        // The originals are kept so an element whose final number fails gets its old number back.
        var renumber = items.Where(i => i.TryGetProperty("number", out _)).ToList();
        var original = new Dictionary<long, string>();
        foreach (var i in renumber)
        {
            var e = doc.GetElement(new ElementId(i.GetProperty("element_id").GetInt64()));
            if (e is not (ViewSheet or SpatialElement)) continue; // reported in the loop below
            var old = GetNumber(e);
            // Letters and digits only: sheet numbers refuse characters such as ~ { } [ ] | ; < > ? \ :
            try { SetNumber(e, "TMP" + Guid.NewGuid().ToString("N").Substring(0, 10)); original[e.Id.Value] = old; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }

        foreach (var i in items)
        {
            var id = i.GetProperty("element_id").GetInt64();
            var e = doc.GetElement(new ElementId(id));
            if (e is null) { results.Add(new { element_id = id, status = "not found" }); continue; }
            try
            {
                if (i.TryGetProperty("number", out var n))
                {
                    try { SetNumber(e, n.GetString()!); }
                    catch (Exception) when (original.TryGetValue(id, out var old))
                    {
                        try { SetNumber(e, old); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                        original.Remove(id);
                        throw;
                    }
                    original.Remove(id);
                }
                if (i.TryGetProperty("name", out var nm)) e.Name = nm.GetString()!;
                results.Add(new { element_id = id, status = "ok" });
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
            {
                results.Add(new { element_id = id, status = ex.Message });
            }
        }
        t.Commit();
        return ToolResult.Ok(results);
    }

    private static string GetNumber(Element e) => e switch
    {
        ViewSheet s => s.SheetNumber,
        SpatialElement r => r.Number,
        _ => "",
    };

    private static void SetNumber(Element? e, string number)
    {
        switch (e)
        {
            case ViewSheet s: s.SheetNumber = number; break;
            case SpatialElement r: r.Number = number; break;
            default: throw new ArgumentException("Only sheets and rooms/spaces have numbers.");
        }
    }
}

public sealed class ExportPdfTool : IRevitTool
{
    public string Name => "export_pdf";
    public string Description =>
        "Print sheets or views to PDF (a print/issue set). Defaults to one combined file in Documents\\SketchProMax.";
    public string PropertiesJson => """
    {
      "view_ids": {"type": "array", "items": {"type": "integer"}, "description": "Sheets or views, in order."},
      "file_name": {"type": "string", "description": "Name for the combined PDF (no extension)."},
      "combine": {"type": "boolean", "description": "One PDF (default true) or one file per sheet."},
      "folder": {"type": "string"},
      "name_pattern": {"type": "string", "description": "One file per sheet named from the pattern, e.g. '{number} - {name}' or 'PRJ-{number}-{revision}'. Placeholders: {number} {name} {revision} {view} {date} {project}. The result checks each file name."}
    }
    """;
    public string[] Required => ["view_ids"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var folder = args.OptString("folder")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var options = new PDFExportOptions
        {
            Combine = args.OptBool("combine", true),
            FileName = args.OptString("file_name") ?? $"{doc.Title} - {DateTime.Now:yyyy-MM-dd HHmm}",
        };
        var ids = args.LongList("view_ids").Select(i => new ElementId(i)).ToList();
        if (args.OptString("name_pattern") is { } pattern)
        {
            ExportNames.RequireUnique(doc, ids, pattern);
            var naming = new List<object>();
            options.Combine = true;
            foreach (var id in ids)
            {
                var view = doc.GetElement(id) as View ?? throw new ArgumentException($"{id.Value} is not a view or sheet.");
                var expected = ExportNames.For(doc, view, pattern);
                options.FileName = expected;
                var before = new HashSet<string>(Directory.GetFiles(folder), StringComparer.OrdinalIgnoreCase);
                var started = DateTime.UtcNow;
                if (!doc.Export(folder, [id], options)) { naming.Add(new { view = view.Name, expected = expected + ".pdf", found = (string?)null, ok = false }); continue; }
                naming.Add(ExportNames.Settle(folder, expected, ".pdf", before, started, view.Name));
            }
            return ToolResult.Ok(new { folder, exported = ids.Count, naming });
        }
        var ok = doc.Export(folder, ids, options);
        return ok ? ToolResult.Ok(new { folder, exported = ids.Count }) : ToolResult.Error("Revit reported the PDF export failed.");
    }
}
