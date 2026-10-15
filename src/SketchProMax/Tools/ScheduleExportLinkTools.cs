using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

internal static class ScheduleKit
{
    public static ViewSchedule Find(Document doc, ToolArgs args)
    {
        if (args.OptLong("schedule_id") is { } id)
            return doc.GetElement(new ElementId(id)) as ViewSchedule ?? throw new ArgumentException($"{id} is not a schedule.");
        var name = args.String("schedule");
        return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                   .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No schedule named '{name}'.");
    }

    public static ScheduleField? Field(ScheduleDefinition def, string name)
    {
        for (var i = 0; i < def.GetFieldCount(); i++)
        {
            var f = def.GetField(i);
            if (string.Equals(f.GetName(), name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.ColumnHeading, name, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }
}

public sealed class FormatScheduleTool : IRevitTool
{
    public string Name => "format_schedule";
    public string Description =>
        "Format an existing schedule: add or hide fields, rename column headings, filters, sorting and grouping " +
        "(with headers, footers and blank lines), itemize on/off, grand totals and column totals.";
    public string PropertiesJson => """
    {
      "schedule": {"type": "string", "description": "Schedule name (or use schedule_id)."},
      "schedule_id": {"type": "integer"},
      "add_fields": {"type": "array", "items": {"type": "string"}},
      "hide_fields": {"type": "array", "items": {"type": "string"}},
      "show_fields": {"type": "array", "items": {"type": "string"}},
      "headings": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Field name -> new column heading."},
      "clear_filters": {"type": "boolean"},
      "filters": {"type": "array", "items": {"type": "object", "properties": {"field": {"type": "string"}, "operator": {"type": "string", "enum": ["equal", "not_equal", "greater", "greater_or_equal", "less", "less_or_equal", "contains", "not_contains", "begins_with", "ends_with", "has_value", "has_no_value"]}, "value": {"type": "string"}}, "required": ["field", "operator"]}},
      "clear_sorting": {"type": "boolean"},
      "sort_by": {"type": "array", "items": {"type": "object", "properties": {"field": {"type": "string"}, "descending": {"type": "boolean"}, "header": {"type": "boolean"}, "footer": {"type": "boolean"}, "blank_line": {"type": "boolean"}}, "required": ["field"]}},
      "itemize": {"type": "boolean"},
      "grand_totals": {"type": "boolean"},
      "total_fields": {"type": "array", "items": {"type": "string"}, "description": "Numeric fields to total (e.g. Area, Count)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var schedule = ScheduleKit.Find(doc, args);
        var def = schedule.Definition;
        var problems = new List<string>();
        ScheduleField Need(string name) => ScheduleKit.Field(def, name) ?? throw new ArgumentException($"The schedule has no field '{name}'. Add it first.");

        using var t = RevitHelpers.StartTransaction(doc, "Format schedule");
        var schedulable = def.GetSchedulableFields();
        foreach (var name in args.StringList("add_fields"))
        {
            if (ScheduleKit.Field(def, name) != null) continue;
            var sf = schedulable.FirstOrDefault(f => string.Equals(f.GetName(doc), name, StringComparison.OrdinalIgnoreCase));
            if (sf is null) problems.Add($"no field '{name}' available. Available: {string.Join(", ", schedulable.Select(f => f.GetName(doc)).Take(60))}");
            else def.AddField(sf);
        }
        foreach (var name in args.StringList("hide_fields")) Need(name).IsHidden = true;
        foreach (var name in args.StringList("show_fields")) Need(name).IsHidden = false;
        if (args.Has("headings"))
            foreach (var p in args.Raw["headings"].EnumerateObject()) Need(p.Name).ColumnHeading = p.Value.GetString() ?? p.Name;

        if (args.OptBool("clear_filters")) def.ClearFilters();
        foreach (var spec in args.Array("filters"))
        {
            var field = Need(spec.GetProperty("field").GetString()!);
            var op = spec.GetProperty("operator").GetString() switch
            {
                "not_equal" => ScheduleFilterType.NotEqual,
                "greater" => ScheduleFilterType.GreaterThan,
                "greater_or_equal" => ScheduleFilterType.GreaterThanOrEqual,
                "less" => ScheduleFilterType.LessThan,
                "less_or_equal" => ScheduleFilterType.LessThanOrEqual,
                "contains" => ScheduleFilterType.Contains,
                "not_contains" => ScheduleFilterType.NotContains,
                "begins_with" => ScheduleFilterType.BeginsWith,
                "ends_with" => ScheduleFilterType.EndsWith,
                "has_value" => ScheduleFilterType.HasValue,
                "has_no_value" => ScheduleFilterType.HasNoValue,
                _ => ScheduleFilterType.Equal,
            };
            var value = spec.TryGetProperty("value", out var v) ? v.ToString() : null;
            try
            {
                if (value is null) def.AddFilter(new ScheduleFilter(field.FieldId, op));
                else if (SpecOf(field) is { } fieldSpec && UnitUtils.IsMeasurableSpec(fieldSpec))
                {
                    // Measurable fields (length, area, volume, angle, ...) compare in internal units; parse as Revit would ('2100 mm', '3.5').
                    if (!UnitFormatUtils.TryParse(doc.GetUnits(), fieldSpec, value, out double number))
                    {
                        problems.Add($"filter on {field.GetName()}: '{value}' is not a valid value; type a number, with units if needed (e.g. '2100 mm').");
                        continue;
                    }
                    def.AddFilter(new ScheduleFilter(field.FieldId, op, number));
                }
                else if (SpecOf(field) == SpecTypeId.Int.Integer
                         && int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var whole))
                    def.AddFilter(new ScheduleFilter(field.FieldId, op, whole));
                else def.AddFilter(new ScheduleFilter(field.FieldId, op, value));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { problems.Add($"filter on {field.GetName()}: {ex.Message}"); }
        }

        if (args.OptBool("clear_sorting")) def.ClearSortGroupFields();
        foreach (var spec in args.Array("sort_by"))
        {
            var field = Need(spec.GetProperty("field").GetString()!);
            var sort = new ScheduleSortGroupField(field.FieldId,
                spec.TryGetProperty("descending", out var d) && d.GetBoolean() ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending)
            {
                ShowHeader = spec.TryGetProperty("header", out var h) && h.GetBoolean(),
                ShowFooter = spec.TryGetProperty("footer", out var f) && f.GetBoolean(),
                ShowBlankLine = spec.TryGetProperty("blank_line", out var b) && b.GetBoolean(),
            };
            def.AddSortGroupField(sort);
        }
        if (args.Has("itemize")) def.IsItemized = args.OptBool("itemize");
        if (args.Has("grand_totals"))
        {
            def.ShowGrandTotal = args.OptBool("grand_totals");
            if (def.ShowGrandTotal) { def.ShowGrandTotalTitle = true; def.ShowGrandTotalCount = true; }
        }
        foreach (var name in args.StringList("total_fields"))
        {
            var field = Need(name);
            if (field.CanTotal()) field.DisplayType = ScheduleFieldDisplayType.Totals;
            else problems.Add($"'{name}' can't be totaled");
        }
        t.Commit();
        return ToolResult.Ok(new { schedule = schedule.Name, fields = Enumerable.Range(0, def.GetFieldCount()).Select(i => def.GetField(i).ColumnHeading), problems });
    }

    /// <summary>The field's data type, or null for fields that have none (e.g. formulas Revit can't type, count).</summary>
    private static ForgeTypeId? SpecOf(ScheduleField field)
    {
        try { return field.GetSpecTypeId() is { } id && !id.Empty() ? id : null; }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }
}

public sealed class CreateSheetListTool : IRevitTool
{
    public string Name => "create_sheet_list";
    public string Description =>
        "Create a sheet index (drawing list) schedule: sheet number and name plus any extra sheet fields (e.g. " +
        "'Current Revision', 'Current Revision Date', 'Drawn By'), sorted by number, optionally placed on a sheet.";
    public string PropertiesJson => """
    {
      "name": {"type": "string", "description": "Default 'Drawing List'."},
      "extra_fields": {"type": "array", "items": {"type": "string"}},
      "number_starts_with": {"type": "string", "description": "Only list sheets whose number starts with this (e.g. 'A-')."},
      "place_on_sheet": {"type": "string", "description": "Sheet number to place the list on (top-left area)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        using var t = RevitHelpers.StartTransaction(doc, "Create drawing list");
        var schedule = ViewSchedule.CreateSheetList(doc);
        try { schedule.Name = args.OptString("name") ?? "Drawing List"; }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        var def = schedule.Definition;
        var available = def.GetSchedulableFields();
        var missing = new List<string>();
        ScheduleField? Add(string name)
        {
            var sf = available.FirstOrDefault(f => string.Equals(f.GetName(doc), name, StringComparison.OrdinalIgnoreCase));
            if (sf is null) { missing.Add(name); return null; }
            return def.AddField(sf);
        }
        var number = Add("Sheet Number");
        Add("Sheet Name");
        foreach (var extra in args.StringList("extra_fields")) Add(extra);
        if (number != null)
        {
            def.AddSortGroupField(new ScheduleSortGroupField(number.FieldId, ScheduleSortOrder.Ascending));
            if (args.OptString("number_starts_with") is { Length: > 0 } prefix)
                def.AddFilter(new ScheduleFilter(number.FieldId, ScheduleFilterType.BeginsWith, prefix));
        }

        string? placed = null;
        if (args.OptString("place_on_sheet") is { } sheetNumber)
        {
            var sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, sheetNumber, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No sheet numbered '{sheetNumber}'.");
            var outline = sheet.Outline;
            var at = new XYZ(outline.Min.U + 0.1, outline.Max.V - 0.1, 0);
            ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, at);
            placed = sheet.SheetNumber;
        }
        t.Commit();
        return ToolResult.Ok(new { id = schedule.Id.Value, name = schedule.Name, placedOn = placed, missingFields = missing,
            availableFields = missing.Count > 0 ? available.Select(f => f.GetName(doc)).Take(60) : null });
    }
}

public sealed class ExportFilesTool : IRevitTool
{
    public string Name => "export_files";
    public string Description =>
        "Export to DWG (views or sheets), IFC (whole model, or what's visible in one 3D view), Navisworks NWC " +
        "(needs the Navisworks exporter installed) or PNG/JPG images of views and sheets. Files go to Documents\\SketchProMax unless a folder is given. " +
        "coordinates picks internal, project base point or shared coordinates; units sets DWG units. With name_pattern each " +
        "view or sheet becomes its own file named from the pattern, and the result checks every expected file exists with that exact name.";
    public string PropertiesJson => """
    {
      "format": {"type": "string", "enum": ["dwg", "ifc", "nwc", "png", "jpg"]},
      "view_ids": {"type": "array", "items": {"type": "integer"}, "description": "dwg/png/jpg: views or sheets. ifc/nwc: optional single 3D view to limit the export."},
      "folder": {"type": "string"},
      "file_name": {"type": "string", "description": "Base name. dwg/images add the view name per file."},
      "dwg_setup": {"type": "string", "description": "Name of a saved DWG export setup in the model."},
      "ifc_version": {"type": "string", "enum": ["IFC2x3", "IFC4"], "description": "Default IFC2x3 Coordination View 2.0."},
      "image_pixels": {"type": "integer", "description": "Image width in pixels, default 3000."},
      "coordinates": {"type": "string", "enum": ["internal", "project_base", "shared"], "description": "dwg: internal or shared. ifc: all three. nwc: internal or shared. Default: the exporter's own default."},
      "units": {"type": "string", "enum": ["mm", "cm", "m", "ft", "in"], "description": "dwg only (ifc and nwc use the model's units)."},
      "name_pattern": {"type": "string", "description": "dwg: one file per view/sheet, e.g. '{number} - {name}' or 'PRJ-{number}-{revision}'. Placeholders: {number} {name} {revision} {view} {date}."}
    }
    """;
    public string[] Required => ["format"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var folder = args.OptString("folder")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var name = args.OptString("file_name") ?? $"{doc.Title} - {DateTime.Now:yyyy-MM-dd HHmm}";
        var ids = args.LongList("view_ids").Select(i => new ElementId(i)).ToList();
        // Exports overwrite same-named files, so a file counts as produced if it is new or was written after we started.
        var before = new HashSet<string>(Directory.GetFiles(folder), StringComparer.OrdinalIgnoreCase);
        var started = DateTime.UtcNow.AddSeconds(-2);
        var format = args.String("format");
        var notes = new List<string>();
        var naming = new List<object>();

        switch (format)
        {
            case "dwg":
            {
                if (ids.Count == 0) ids.Add(doc.ActiveView.Id);
                var options = args.OptString("dwg_setup") is { } setup
                    ? DWGExportOptions.GetPredefinedOptions(doc, setup) ?? throw new ArgumentException($"No DWG export setup named '{setup}'.")
                    : new DWGExportOptions();
                options.MergedViews = true;
                switch (args.OptString("coordinates"))
                {
                    case "shared": options.SharedCoords = true; break;
                    case "internal": options.SharedCoords = false; break;
                    case "project_base": notes.Add("DWG export has no project base point option; used internal coordinates. Use shared coordinates, or IFC, for a project-base origin."); options.SharedCoords = false; break;
                }
                if (args.OptString("units") is { } u)
                    options.TargetUnit = u switch { "mm" => ExportUnit.Millimeter, "cm" => ExportUnit.Centimeter, "m" => ExportUnit.Meter, "in" => ExportUnit.Inch, _ => ExportUnit.Foot };
                if (args.OptString("name_pattern") is { } pattern)
                {
                    ExportNames.RequireUnique(doc, ids, pattern);
                    foreach (var id in ids)
                    {
                        var view = doc.GetElement(id) as View ?? throw new ArgumentException($"{id.Value} is not a view or sheet.");
                        var expected = ExportNames.For(doc, view, pattern);
                        var existing = new HashSet<string>(Directory.GetFiles(folder), StringComparer.OrdinalIgnoreCase);
                        var t0 = DateTime.UtcNow;
                        if (!doc.Export(folder, expected, [id], options)) { naming.Add(new { view = view.Name, expected = expected + ".dwg", found = (string?)null, ok = false }); continue; }
                        naming.Add(ExportNames.Settle(folder, expected, ".dwg", existing, t0, view.Name));
                    }
                    break;
                }
                if (!doc.Export(folder, name, ids, options)) return ToolResult.Error("Revit reported the DWG export failed.");
                break;
            }
            case "ifc":
            {
                var options = new IFCExportOptions
                {
                    FileVersion = args.OptString("ifc_version") == "IFC4" ? IFCVersion.IFC4 : IFCVersion.IFC2x3CV2,
                };
                if (ids.Count > 0) options.FilterViewId = ids[0];
                // The IFC exporter's site placement: 0 shared, 2 project base point, 3 internal.
                if (args.OptString("coordinates") is { } coords)
                    options.AddOption("SitePlacement", coords switch { "shared" => "0", "project_base" => "2", _ => "3" });
                if (args.Has("units")) notes.Add("IFC uses the model's project units; 'units' was ignored.");
                // IFC export needs an open transaction; nothing is kept.
                using var t = new Transaction(doc, "Sketch ProMax: IFC export");
                t.Start();
                var ok = doc.Export(folder, name + ".ifc", options);
                t.RollBack();
                if (!ok) return ToolResult.Error("Revit reported the IFC export failed.");
                break;
            }
            case "nwc":
            {
                if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
                    return ToolResult.Error("The Navisworks exporter isn't installed for this Revit version (free 'Navisworks NWC Export Utility' from Autodesk).");
                var options = new NavisworksExportOptions();
                if (ids.Count > 0) { options.ExportScope = NavisworksExportScope.View; options.ViewId = ids[0]; }
                switch (args.OptString("coordinates"))
                {
                    case "shared": options.Coordinates = NavisworksCoordinates.Shared; break;
                    case "internal": options.Coordinates = NavisworksCoordinates.Internal; break;
                    case "project_base": notes.Add("NWC export has no project base point option; used internal coordinates."); options.Coordinates = NavisworksCoordinates.Internal; break;
                }
                doc.Export(folder, name + ".nwc", options);
                break;
            }
            default:
            {
                if (ids.Count == 0) ids.Add(doc.ActiveView.Id);
                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(folder, name),
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = (int)(args.OptLong("image_pixels") ?? 3000),
                    ImageResolution = ImageResolution.DPI_300,
                    HLRandWFViewsFileType = format == "jpg" ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                    ShadowViewsFileType = format == "jpg" ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                };
                options.SetViewsAndSheets(ids);
                doc.ExportImage(options);
                break;
            }
        }

        var files = Directory.GetFiles(folder).Where(f => !before.Contains(f) || File.GetLastWriteTimeUtc(f) >= started)
            .Select(Path.GetFileName).ToList();
        return ToolResult.Ok(new
        {
            folder, files = files.Take(300), fileCount = files.Count,
            naming = naming.Count > 0 ? naming : null, notes = notes.Count > 0 ? notes : null,
        });
    }
}

/// <summary>File names built from a pattern per view or sheet, and a check that Revit wrote exactly that name.</summary>
internal static class ExportNames
{
    public static string For(Document doc, View view, string pattern)
    {
        var sheet = view as ViewSheet;
        var revision = sheet is null ? "" : sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "";
        var text = pattern
            .Replace("{number}", sheet?.SheetNumber ?? "")
            .Replace("{name}", sheet?.Name ?? view.Name)
            .Replace("{view}", view.Name)
            .Replace("{revision}", revision)
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
            .Replace("{project}", doc.ProjectInformation?.Number ?? "");
        var safe = new string(text.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        return safe.Length == 0 ? "export" : safe;
    }

    /// <summary>Stops before exporting when two views would get the same file name and overwrite each other.</summary>
    public static void RequireUnique(Document doc, IEnumerable<ElementId> ids, string pattern)
    {
        var clashes = ids.Select(doc.GetElement).OfType<View>().GroupBy(v => For(doc, v, pattern), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => $"'{g.Key}' ({string.Join(", ", g.Select(v => v.Name))})").ToList();
        if (clashes.Count > 0)
            throw new ArgumentException($"name_pattern gives the same file name to several views: {string.Join("; ", clashes.Take(5))}. Add {{view}} or {{number}} to the pattern.");
    }

    /// <summary>Finds what Revit wrote and renames it to the expected name when Revit added its own suffix.</summary>
    public static object Settle(string folder, string expected, string extension, HashSet<string> before, DateTime started, string view)
    {
        var target = Path.Combine(folder, expected + extension);
        var written = Directory.GetFiles(folder, "*" + extension)
            .Where(f => !before.Contains(f) || File.GetLastWriteTimeUtc(f) >= started).ToList();
        if (written.Any(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase)))
            return new { view, expected = expected + extension, found = expected + extension, ok = true };
        if (written.Count == 1)
        {
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(written[0], target);
                return new { view, expected = expected + extension, found = expected + extension, ok = true, renamedFrom = Path.GetFileName(written[0]) };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new { view, expected = expected + extension, found = Path.GetFileName(written[0]), ok = false, error = ex.Message };
            }
        }
        return new { view, expected = expected + extension, found = written.Count == 0 ? null : string.Join(", ", written.Select(Path.GetFileName)), ok = false };
    }
}

public sealed class QueryLinkedModelsTool : IRevitTool
{
    public string Name => "query_linked_models";
    public string Description =>
        "List linked Revit models (name, path, loaded or not, position), or query elements inside a link by " +
        "category, optionally with a parameter value filter. Without a category it returns element counts per category.";
    public string PropertiesJson => """
    {
      "link": {"type": "string", "description": "Part of the link's name. Omit to just list links."},
      "category": {"type": "string"},
      "parameter": {"type": "string"},
      "value_contains": {"type": "string"},
      "limit": {"type": "integer", "description": "Default 200."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
        if (args.OptString("link") is not { } wanted)
        {
            return ToolResult.Ok(new
            {
                links = links.Select(l =>
                {
                    var type = doc.GetElement(l.GetTypeId()) as RevitLinkType;
                    var ext = type?.GetExternalFileReference();
                    var origin = l.GetTotalTransform().Origin;
                    return new
                    {
                        id = l.Id.Value, name = l.Name, loaded = l.GetLinkDocument() != null,
                        path = ext is null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath()),
                        origin = new[] { RevitHelpers.FromInternal(doc, origin.X), RevitHelpers.FromInternal(doc, origin.Y), RevitHelpers.FromInternal(doc, origin.Z) },
                    };
                }),
            });
        }

        var link = LinkMatch.Find(doc, links, wanted);
        var linkDoc = link.GetLinkDocument() ?? throw new InvalidOperationException($"'{link.Name}' is not loaded.");

        if (args.OptString("category") is not { } category)
        {
            var counts = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !e.ViewSpecific
                            && !e.Category.Name.StartsWith("<", StringComparison.Ordinal)
                            && e.Category.Name.IndexOf("Analytical", StringComparison.OrdinalIgnoreCase) < 0)
                .GroupBy(e => e.Category.Name).Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).ToList();
            return ToolResult.Ok(new { link = link.Name, categories = counts });
        }

        var bic = RevitHelpers.ParseCategory(linkDoc, category);
        IEnumerable<Element> elements = new FilteredElementCollector(linkDoc).OfCategory(bic).WhereElementIsNotElementType();
        var param = args.OptString("parameter");
        var contains = args.OptString("value_contains");
        if (param != null)
            elements = elements.Where(e => e.LookupParameter(param) is { } p &&
                                           (contains is null || (RevitHelpers.ParamValue(p) ?? "").IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0));
        var list = elements.ToList();
        var limit = (int)(args.OptLong("limit") ?? 200);
        return ToolResult.Ok(new
        {
            link = link.Name,
            total = list.Count,
            elements = list.Take(limit).Select(e => new
            {
                summary = RevitHelpers.ElementSummary(linkDoc, e),
                value = param is null ? null : RevitHelpers.ParamValue(e.LookupParameter(param)),
            }),
        });
    }
}

public sealed class LoadFamilyTool : IRevitTool
{
    public string Name => "load_family";
    public string Description =>
        "Load a family (.rfa) into the model from a file path, or search Revit's content libraries (and an optional " +
        "folder) by name. If a search finds several matches it lists them instead of loading; call again with the path.";
    public string PropertiesJson => """
    {
      "path": {"type": "string", "description": "Full path to an .rfa file."},
      "search": {"type": "string", "description": "Words in the family file name, e.g. 'single flush door'."},
      "folder": {"type": "string", "description": "Extra folder to search (with subfolders)."},
      "overwrite": {"type": "boolean", "description": "Replace an already loaded family of the same name (default false)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var path = args.OptString("path");
        if (path is null)
        {
            var words = (args.OptString("search") ?? throw new ArgumentException("Give a path or search words."))
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var roots = app.Application.GetLibraryPaths().Values.ToList();
            if (args.OptString("folder") is { } extra) roots.Insert(0, extra);
            var matches = new List<string>();
            foreach (var root in roots.Where(Directory.Exists))
            {
                try
                {
                    matches.AddRange(Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories)
                        .Where(f => words.All(w => Path.GetFileNameWithoutExtension(f).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Take(30));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (matches.Count >= 30) break;
            }
            if (matches.Count == 0) return ToolResult.Error($"No family files matching '{string.Join(" ", words)}' in: {string.Join("; ", roots)}");
            if (matches.Count > 1) return ToolResult.Ok(new { message = "Several matches; call again with one path.", matches });
            path = matches[0];
        }
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {path}");

        using var t = RevitHelpers.StartTransaction(doc, "Load family");
        var loaded = doc.LoadFamily(path, new LoadOptions(args.OptBool("overwrite")), out Family family);
        t.Commit();
        if (!loaded || family is null)
            return ToolResult.Ok(new { loaded = false, message = "Already loaded (set overwrite to replace it)." });
        var types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).Select(s => new { id = s.Id.Value, name = s.Name });
        return ToolResult.Ok(new { loaded = true, family = family.Name, category = family.FamilyCategory?.Name, types });
    }

    private sealed class LoadOptions(bool overwrite) : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = overwrite;
            return overwrite;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = overwrite;
            return overwrite;
        }
    }
}

/// <summary>
/// Picks one link from what the user called it. Exact file names win over whole words ("EL" in "Tower-EL-Model"),
/// which win over parts of words; a tie between different links is reported instead of guessed.
/// </summary>
internal static class LinkMatch
{
    public static RevitLinkInstance Find(Document doc, IList<RevitLinkInstance> links, string wanted)
    {
        string FileName(RevitLinkInstance l)
        {
            var name = (doc.GetElement(l.GetTypeId()) as RevitLinkType)?.Name ?? l.Name;
            var colon = name.IndexOf(" : ", StringComparison.Ordinal);
            if (colon > 0) name = name.Substring(0, colon);
            return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }
        var w = wanted.Trim();
        if (w.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) w = w.Substring(0, w.Length - 4);
        var tokens = System.Text.RegularExpressions.Regex.Split(w, "[^A-Za-z0-9]+").Where(t => t.Length > 0).ToList();

        int Rank(RevitLinkInstance l)
        {
            var file = FileName(l);
            if (string.Equals(file, w, StringComparison.OrdinalIgnoreCase)) return 3;
            var words = System.Text.RegularExpressions.Regex.Split(file, "[^A-Za-z0-9]+")
                .Where(t => t.Length > 0).Select(t => t.ToUpperInvariant()).ToHashSet();
            if (tokens.Count > 0 && tokens.All(t => words.Contains(t.ToUpperInvariant()))) return 2;
            return file.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
        }

        var ranked = links.Select(l => (Link: l, Rank: Rank(l))).Where(x => x.Rank > 0).ToList();
        if (ranked.Count == 0)
            throw new ArgumentException($"No link matching '{wanted}'. Links: {string.Join(", ", links.Select(FileName).Distinct())}");
        var best = ranked.Max(x => x.Rank);
        var top = ranked.Where(x => x.Rank == best).Select(x => x.Link).ToList();
        var files = top.Select(FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count > 1)
            throw new ArgumentException($"'{wanted}' matches several links: {string.Join(", ", files)}. Ask the user which one, then use its full name.");
        return top[0];
    }
}
