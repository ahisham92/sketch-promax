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
        "laid out inside the title block's drawing area without overlapping. Set placeholder=true for index-only sheets of other disciplines (no title block, not printed). " +
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
            "title_block": {"type": "string", "description": "Title block as 'Family: Type', family name or type name (default: first loaded). A name that fits several title blocks is refused with the list."},
            "view_ids": {"type": "array", "items": {"type": "integer"}},
            "drawing_area": {"type": "array", "items": {"type": "number"}, "description": "Optional [x_min, y_min, x_max, y_max] on the sheet in project units (paper size) that views may use. Default: the title block frame minus its title panel."},
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
            FamilySymbol? tb = null;
            if (spec.TryGetProperty("placeholder", out var ph) && ph.GetBoolean())
            {
                sheet = ViewSheet.CreatePlaceholder(doc);
            }
            else
            {
                var tbName = spec.TryGetProperty("title_block", out var tbp) ? tbp.GetString() : null;
                if (tbName is null)
                {
                    tb = titleBlocks.OrderBy(s => s.FamilyName).ThenBy(s => s.Name).FirstOrDefault();
                    if (tb is null) { results.Add(new { number, error = "no title block loaded; load one with load_family or use placeholder=true" }); continue; }
                }
                else if (!TypeMatch.TryFind(titleBlocks, tbName, "title block", out tb, out var tbError))
                {
                    results.Add(new { number, error = tbError });
                    continue;
                }
                sheet = ViewSheet.Create(doc, tb!.Id);
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
            var area = spec.TryGetProperty("drawing_area", out var da) ? da.EnumerateArray().Select(e => e.GetDouble()).ToList() : null;
            if (ids.Count > 0 && !sheet.IsPlaceholder)
            {
                var layout = SheetLayout.PlaceViews(doc, sheet, ids, area);
                results.Add(new { number, sheetId = sheet.Id.Value, titleBlock = tb is null ? null : TypeMatch.Label(tb), layout.placed, layout.skipped, layout.warnings, layout.drawingArea });
            }
            else results.Add(new { number, sheetId = sheet.Id.Value, titleBlock = tb is null ? null : TypeMatch.Label(tb), placed = new List<long>(), skipped = new List<object>() });
        }
        t.Commit();
        return ToolResult.Ok(results);
    }
}

public sealed class PlaceViewsOnSheetTool : IRevitTool
{
    public string Name => "place_views_on_sheet";
    public string Description =>
        "Place existing views or schedules on an existing sheet, packed inside the title block's drawing area (its frame " +
        "minus the title panel) without overlapping each other or what is already on the sheet.";
    public string PropertiesJson => """
    {
      "sheet_id": {"type": "integer"},
      "view_ids": {"type": "array", "items": {"type": "integer"}},
      "drawing_area": {"type": "array", "items": {"type": "number"}, "description": "Optional [x_min, y_min, x_max, y_max] on the sheet in project units (paper size) that views may use. Default: detected from the title block."}
    }
    """;
    public string[] Required => ["sheet_id", "view_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (doc.GetElement(new ElementId(args.OptLong("sheet_id") ?? -1)) is not ViewSheet sheet)
            return ToolResult.Error("sheet_id is not a sheet.");
        if (sheet.IsPlaceholder) return ToolResult.Error("This is a placeholder sheet; placeholders cannot hold views.");
        using var t = RevitHelpers.StartTransaction(doc, $"Place views on {sheet.SheetNumber}");
        var area = args.Has("drawing_area") ? args.Array("drawing_area").Select(e => e.GetDouble()).ToList() : null;
        var layout = SheetLayout.PlaceViews(doc, sheet, args.LongList("view_ids"), area);
        t.Commit();
        return ToolResult.Ok(new { layout.placed, layout.skipped, layout.warnings, layout.drawingArea });
    }
}

public sealed class RenameElementsTool : IRevitTool
{
    public string Name => "rename_elements";
    public string Description =>
        "Rename or renumber many elements at once: views, sheets (name and/or number), levels, grids, rooms " +
        "(name and/or number), family types. Use this to apply firm naming conventions across the model. Each element is its own " +
        "step: in a workshared model, elements another user owns are skipped and named with their owner, and one refusal undoes nothing else.";
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
        var items = new List<(long id, Element? e, string? name, string? number)>();
        foreach (var i in args.Array("items"))
        {
            var id = i.GetProperty("element_id").GetInt64();
            items.Add((id, doc.GetElement(new ElementId(id)),
                i.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                i.TryGetProperty("number", out var n) ? n.GetString() : null));
        }
        var status = new Dictionary<int, string>();
        // Workshared: elements someone else owns are skipped up front, each with its owner; every other item is its own step.
        var locked = Batch.Locked(doc, items.Where(i => i.e is not null).Select(i => i.e!.Id));
        var run = new List<int>();
        for (var k = 0; k < items.Count; k++)
        {
            if (items[k].e is null) status[k] = "not found";
            else if (locked.TryGetValue(items[k].id, out var why)) status[k] = "locked: " + why;
            else run.Add(k);
        }

        Batch.InGroup(doc, "Rename elements", () =>
        {
            // Sheet and room numbers must be unique at every step, so changing numbers are parked on temporary values first
            // (this is what lets two sheets swap numbers). An item that then fails gets its old number back.
            var park = run.Where(k => items[k].number is not null && items[k].e is ViewSheet or SpatialElement).ToList();
            var original = new Dictionary<int, string>();
            var parked = ItemBatch.Run(doc, "Rename elements (park numbers)", park, k => items[k].id.ToString(), k => [items[k].e!.Id], k =>
            {
                var old = GetNumber(items[k].e!);
                // Letters and digits only: sheet numbers refuse characters such as ~ { } [ ] | ; < > ? \ :
                SetNumber(items[k].e, "TMP" + Guid.NewGuid().ToString("N").Substring(0, 10));
                return old;
            });
            for (var j = 0; j < park.Count; j++)
                if (parked.Items[j].Ok) original[park[j]] = (string)parked.Items[j].Value!;

            var main = ItemBatch.Run(doc, "Rename elements", run, k => items[k].id.ToString(), k => [items[k].e!.Id], k =>
            {
                var (_, e, name, number) = items[k];
                if (number is not null) SetNumber(e, number);
                if (name is not null) e!.Name = name;
                return null;
            }, borrow: false);
            var restore = new List<int>();
            for (var j = 0; j < run.Count; j++)
            {
                var k = run[j];
                status[k] = main.Items[j].Ok ? "ok" : main.Items[j].Error ?? "failed";
                if (!main.Items[j].Ok && original.ContainsKey(k)) restore.Add(k);
            }
            if (restore.Count > 0)
            {
                var back = ItemBatch.Run(doc, "Rename elements (restore numbers)", restore, k => items[k].id.ToString(), k => [items[k].e!.Id],
                    k => { SetNumber(items[k].e, original[k]); return null; }, borrow: false, cancellable: false);
                for (var j = 0; j < restore.Count; j++)
                    if (!back.Items[j].Ok)
                        status[restore[j]] += $" (and its old number '{original[restore[j]]}' could not be put back: {back.Items[j].Error}; it now has a temporary TMP number)";
            }
            return status;
        }, _ => run.Count > 0);

        return ToolResult.Ok(items.Select((i, k) => new { element_id = i.id, status = status.TryGetValue(k, out var st) ? st : "not applied" }));
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
        var folder = OutputPaths.Checked(args.OptString("folder"))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var options = new PDFExportOptions
        {
            Combine = args.OptBool("combine", true),
            FileName = OutputPaths.Checked(args.OptString("file_name")) ?? $"{doc.Title} - {DateTime.Now:yyyy-MM-dd HHmm}",
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
                if (FileInUse.Why(Path.Combine(folder, expected + ".pdf")) is { } inUse)
                {
                    naming.Add(new { view = view.Name, expected = expected + ".pdf", found = (string?)null, ok = false, error = inUse });
                    continue;
                }
                var before = new HashSet<string>(Directory.GetFiles(folder), StringComparer.OrdinalIgnoreCase);
                var started = DateTime.UtcNow;
                if (!doc.Export(folder, [id], options)) { naming.Add(new { view = view.Name, expected = expected + ".pdf", found = (string?)null, ok = false }); continue; }
                naming.Add(ExportNames.Settle(folder, expected, ".pdf", before, started, view.Name));
            }
            return ToolResult.Ok(new { folder, exported = ids.Count, naming });
        }
        // Revit only says "failed" when it can't write the file; the usual cause is the same PDF open in a viewer.
        var combined = options.Combine ? Path.Combine(folder, options.FileName + ".pdf") : null;
        if (combined is not null && FileInUse.Why(combined) is { } locked) return ToolResult.Error(locked);
        var exportStarted = DateTime.UtcNow;
        var ok = doc.Export(folder, ids, options);
        if (ok) return ToolResult.Ok(new { folder, exported = ids.Count });
        var held = combined is not null
            ? FileInUse.Why(combined)
            : FileInUse.Describe(Directory.GetFiles(folder, "*.pdf").Where(f => File.GetLastWriteTimeUtc(f) < exportStarted && FileInUse.Why(f) is not null).ToList());
        return ToolResult.Error(held ?? "Revit reported the PDF export failed. Check that the folder can be written to and that no PDF of " +
                                         "the same name is open in a PDF viewer, or pass another file_name or folder, then try again.");
    }
}

/// <summary>Why an export can't write a file: it is open in another program (a PDF viewer) or read-only. Null when it can.</summary>
internal static class FileInUse
{
    public static string? Why(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return $"'{Path.GetFileName(path)}' is read-only (or this user may not change it), so it can't be overwritten. " +
                   "Pass another file_name or folder, or ask the user to clear the read-only flag.";
        }
        catch (IOException)
        {
            return $"'{Path.GetFileName(path)}' is open in another program, most likely a PDF viewer, so Revit can't overwrite it. " +
                   "Ask the user to close it there (or pass another file_name), then try again.";
        }
    }

    /// <summary>One message for several files held open, or null when there are none.</summary>
    public static string? Describe(List<string> held)
    {
        if (held.Count == 0) return null;
        if (held.Count == 1) return Why(held[0]);
        return $"{held.Count} PDFs in the folder are open in another program, most likely a PDF viewer, so Revit can't overwrite them (" +
               string.Join(", ", held.Take(5).Select(Path.GetFileName)) + (held.Count > 5 ? ", ..." : "") +
               "). Ask the user to close them there (or pass another folder), then try again.";
    }
}

/// <summary>
/// Places views and schedules on a sheet inside the title block's drawing area: the frame minus the title panel,
/// found from the title block's long frame lines, or a rectangle the caller gives. Each new viewport (box and label)
/// is packed top-left first next to what is already on the sheet, with a gap, so nothing overlaps.
/// </summary>
internal static class SheetLayout
{
    private const double Gap = 8 / 304.8; // 8 mm on paper, in feet

    public sealed record Result(List<long> placed, List<object> skipped, List<string> warnings, object drawingArea);

    private readonly record struct Box(double X0, double Y0, double X1, double Y1)
    {
        public double W => X1 - X0;
        public double H => Y1 - Y0;
        public Box Union(Box o) => new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
        public Box Inset(double d) => W > 4 * d && H > 4 * d ? new(X0 + d, Y0 + d, X1 - d, Y1 - d) : this;
        public bool Overlaps(Box o, double gap) =>
            X0 < o.X1 + gap - 1e-6 && X1 > o.X0 - gap + 1e-6 && Y0 < o.Y1 + gap - 1e-6 && Y1 > o.Y0 - gap + 1e-6;
    }

    public static Result PlaceViews(Document doc, ViewSheet sheet, List<long> ids, List<double>? area)
    {
        var placed = new List<long>();
        var skipped = new List<object>();
        var warnings = new List<string>();
        doc.Regenerate(); // the sheet and title block extents are only valid after regeneration

        var full = SheetExtents(doc, sheet, out var titleBlock, warnings);
        Box usable;
        string source;
        if (area is { Count: 4 })
        {
            double a0 = RevitHelpers.ToInternal(doc, area[0]), a1 = RevitHelpers.ToInternal(doc, area[1]);
            double a2 = RevitHelpers.ToInternal(doc, area[2]), a3 = RevitHelpers.ToInternal(doc, area[3]);
            usable = new Box(Math.Min(a0, a2), Math.Min(a1, a3), Math.Max(a0, a2), Math.Max(a1, a3));
            source = "given";
        }
        else
        {
            if (area is not null) warnings.Add("drawing_area needs four numbers [x_min, y_min, x_max, y_max]; used the title block's drawing area.");
            usable = titleBlock is null ? full.Inset(Gap) : DrawingArea(sheet, titleBlock, full, warnings).Inset(Gap);
            source = titleBlock is null ? "sheet extents" : "title block";
        }

        // What is already on the sheet stays where it is; new views go around it.
        var occupied = new List<Box>();
        foreach (var vpId in sheet.GetAllViewports())
            if (doc.GetElement(vpId) is Viewport vp) occupied.Add(ViewportBox(vp));
        foreach (var si in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            if (!si.IsTitleblockRevisionSchedule && si.get_BoundingBox(sheet) is { } sb) occupied.Add(new Box(sb.Min.X, sb.Min.Y, sb.Max.X, sb.Max.Y));

        // Create everything at the area centre first, regenerate once, then read the real sizes and move.
        var start = new XYZ((usable.X0 + usable.X1) / 2, (usable.Y0 + usable.Y1) / 2, 0);
        var made = new List<(long id, Element element)>();
        foreach (var id in ids)
        {
            var viewId = new ElementId(id);
            var view = doc.GetElement(viewId) as View;
            if (view is null) { skipped.Add(new { id, reason = "not a view" }); continue; }
            try
            {
                if (view is ViewSchedule) made.Add((id, ScheduleSheetInstance.Create(doc, sheet.Id, viewId, start)));
                else if (Viewport.CanAddViewToSheet(doc, sheet.Id, viewId)) made.Add((id, Viewport.Create(doc, sheet.Id, viewId, start)));
                else skipped.Add(new { id, reason = "already on a sheet or not placeable" });
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { skipped.Add(new { id, reason = ex.Message }); }
        }
        if (made.Count == 0) return new Result(placed, skipped, warnings, AreaInfo(doc, usable, source));
        doc.Regenerate();

        double offX = full.X1 + 4 * Gap, offTop = full.Y1;
        foreach (var (id, element) in made)
        {
            Box? current = element switch
            {
                Viewport vp => ViewportBox(vp),
                _ => element.get_BoundingBox(sheet) is { } bb ? new Box(bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y) : null,
            };
            if (current is not { } cur) { placed.Add(id); continue; }
            Box target;
            if (Fit(usable, occupied, cur.W, cur.H) is { } spot)
            {
                target = spot;
                occupied.Add(target);
            }
            else
            {
                // Too big for the free area at its scale: park it beside the sheet rather than over the title block or other views.
                target = new Box(offX, offTop - cur.H, offX + cur.W, offTop);
                offTop -= cur.H + Gap;
                var name = (doc.GetElement(new ElementId(id)) as View)?.Name;
                warnings.Add($"View {id} '{name}' ({Len(doc, cur.W)} x {Len(doc, cur.H)}) does not fit in the free drawing area " +
                             $"({Len(doc, usable.W)} x {Len(doc, usable.H)}); it was placed to the right of the sheet. Change its scale or crop, or use a larger title block.");
            }
            var delta = new XYZ(target.X0 - cur.X0, target.Y0 - cur.Y0, 0);
            if (!delta.IsZeroLength())
            {
                if (element is Viewport vp) vp.SetBoxCenter(vp.GetBoxCenter() + delta);
                else if (element is ScheduleSheetInstance si) si.Point = si.Point + delta;
            }
            placed.Add(id);
        }
        return new Result(placed, skipped, warnings, AreaInfo(doc, usable, source));
    }

    private static string Len(Document doc, double feet) => RevitHelpers.FromInternal(doc, feet).ToString("0.#");

    private static object AreaInfo(Document doc, Box b, string source) => new
    {
        min = new[] { RevitHelpers.FromInternal(doc, b.X0), RevitHelpers.FromInternal(doc, b.Y0) },
        max = new[] { RevitHelpers.FromInternal(doc, b.X1), RevitHelpers.FromInternal(doc, b.Y1) },
        source,
    };

    /// <summary>The highest, then leftmost, free spot of w x h inside the area, touching the area corner or another box.</summary>
    private static Box? Fit(Box area, List<Box> occupied, double w, double h)
    {
        var candidates = new List<(double x, double y)> { (area.X0, area.Y1) };
        foreach (var r in occupied)
        {
            candidates.Add((r.X1 + Gap, r.Y1));
            candidates.Add((r.X1 + Gap, area.Y1));
            candidates.Add((r.X0, r.Y0 - Gap));
            candidates.Add((area.X0, r.Y0 - Gap));
        }
        foreach (var (x, y) in candidates.OrderByDescending(c => Math.Round(c.y, 5)).ThenBy(c => c.x))
        {
            var b = new Box(x, y - h, x + w, y);
            if (b.X0 < area.X0 - 1e-6 || b.X1 > area.X1 + 1e-6 || b.Y0 < area.Y0 - 1e-6 || b.Y1 > area.Y1 + 1e-6) continue;
            if (occupied.Any(o => o.Overlaps(b, Gap))) continue;
            return b;
        }
        return null;
    }

    /// <summary>The viewport's box together with its title label.</summary>
    private static Box ViewportBox(Viewport vp)
    {
        var o = vp.GetBoxOutline();
        var box = new Box(o.MinimumPoint.X, o.MinimumPoint.Y, o.MaximumPoint.X, o.MaximumPoint.Y);
        try
        {
            var l = vp.GetLabelOutline();
            if (l is not null && !l.IsEmpty) box = box.Union(new Box(l.MinimumPoint.X, l.MinimumPoint.Y, l.MaximumPoint.X, l.MaximumPoint.Y));
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { } // no label shown
        return box;
    }

    private static Box SheetExtents(Document doc, ViewSheet sheet, out Element? titleBlock, List<string> warnings)
    {
        titleBlock = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().FirstOrDefault();
        if (titleBlock?.get_BoundingBox(sheet) is { } bb && bb.Max.X - bb.Min.X > 0.3 && bb.Max.Y - bb.Min.Y > 0.3)
            return new Box(bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y);
        var o = sheet.Outline;
        if (o is not null && o.Max.U - o.Min.U > 0.3 && o.Max.V - o.Min.V > 0.3)
            return new Box(o.Min.U, o.Min.V, o.Max.U, o.Max.V);
        warnings.Add("Could not read the sheet size; assumed A1 landscape (841 x 594 mm) from the sheet origin.");
        return new Box(0, 0, 841 / 304.8, 594 / 304.8);
    }

    /// <summary>
    /// The title block's frame minus its title panel. Long straight lines (merged along one axis) running most of the
    /// sheet's height or width are the frame and the panel edges; the innermost one on each side bounds the drawing area.
    /// </summary>
    private static Box DrawingArea(ViewSheet sheet, Element titleBlock, Box full, List<string> warnings)
    {
        var segments = new List<(XYZ a, XYZ b)>();
        try { Collect(titleBlock.get_Geometry(new Options { View = sheet }), segments); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }

        const double axisTol = 1e-4;
        // Coverage of each vertical x and horizontal y (several short segments on one line add up).
        double Coverage(IEnumerable<(double lo, double hi)> spans)
        {
            double total = 0, end = double.MinValue;
            foreach (var (lo, hi) in spans.OrderBy(s => s.lo))
            {
                if (hi <= end) continue;
                total += hi - Math.Max(lo, end);
                end = hi;
            }
            return total;
        }
        var verticals = segments.Where(s => Math.Abs(s.a.X - s.b.X) < axisTol && Math.Abs(s.a.Y - s.b.Y) > axisTol)
            .GroupBy(s => Math.Round(s.a.X, 3))
            .Where(g => Coverage(g.Select(s => (Math.Min(s.a.Y, s.b.Y), Math.Max(s.a.Y, s.b.Y)))) >= 0.6 * full.H)
            .Select(g => g.Key).ToList();
        var horizontals = segments.Where(s => Math.Abs(s.a.Y - s.b.Y) < axisTol && Math.Abs(s.a.X - s.b.X) > axisTol)
            .GroupBy(s => Math.Round(s.a.Y, 3))
            .Where(g => Coverage(g.Select(s => (Math.Min(s.a.X, s.b.X), Math.Max(s.a.X, s.b.X)))) >= 0.6 * full.W)
            .Select(g => g.Key).ToList();

        double midX = (full.X0 + full.X1) / 2, midY = (full.Y0 + full.Y1) / 2;
        var area = new Box(
            verticals.Where(x => x < midX).DefaultIfEmpty(full.X0).Max(),
            horizontals.Where(y => y < midY).DefaultIfEmpty(full.Y0).Max(),
            verticals.Where(x => x > midX).DefaultIfEmpty(full.X1).Min(),
            horizontals.Where(y => y > midY).DefaultIfEmpty(full.Y1).Min());
        if (verticals.Count + horizontals.Count == 0)
            warnings.Add("Could not find the title block's frame lines; views are laid out over the whole sheet. Pass drawing_area to keep them off the title panel.");
        if (area.W < 0.3 * full.W || area.H < 0.3 * full.H)
        {
            warnings.Add("The title block's drawing area looked implausibly small; used the whole sheet. Pass drawing_area to set it.");
            return full;
        }
        return area;
    }

    private static void Collect(GeometryElement? geometry, List<(XYZ a, XYZ b)> segments)
    {
        if (geometry is null) return;
        foreach (var g in geometry)
        {
            switch (g)
            {
                case Line l when l.IsBound: segments.Add((l.GetEndPoint(0), l.GetEndPoint(1))); break;
                case PolyLine pl:
                    var pts = pl.GetCoordinates();
                    for (int i = 0; i + 1 < pts.Count; i++) segments.Add((pts[i], pts[i + 1]));
                    break;
                case GeometryInstance gi: Collect(gi.GetInstanceGeometry(), segments); break;
            }
        }
    }
}
