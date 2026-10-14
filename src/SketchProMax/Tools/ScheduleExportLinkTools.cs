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
                else
                {
                    try { def.AddFilter(new ScheduleFilter(field.FieldId, op, value)); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) when (double.TryParse(value, out var number))
                    {
                        // Numeric field: lengths are compared in internal units.
                        var spec2 = field.GetSpecTypeId();
                        var internalValue = spec2 == SpecTypeId.Length ? RevitHelpers.ToInternal(doc, number) : number;
                        def.AddFilter(new ScheduleFilter(field.FieldId, op, internalValue));
                    }
                }
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
        "(needs the Navisworks exporter installed) or PNG/JPG images of views and sheets. Files go to Documents\\SketchProMax unless a folder is given.";
    public string PropertiesJson => """
    {
      "format": {"type": "string", "enum": ["dwg", "ifc", "nwc", "png", "jpg"]},
      "view_ids": {"type": "array", "items": {"type": "integer"}, "description": "dwg/png/jpg: views or sheets. ifc/nwc: optional single 3D view to limit the export."},
      "folder": {"type": "string"},
      "file_name": {"type": "string", "description": "Base name. dwg/images add the view name per file."},
      "dwg_setup": {"type": "string", "description": "Name of a saved DWG export setup in the model."},
      "ifc_version": {"type": "string", "enum": ["IFC2x3", "IFC4"], "description": "Default IFC2x3 Coordination View 2.0."},
      "image_pixels": {"type": "integer", "description": "Image width in pixels, default 3000."}
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
        var before = new HashSet<string>(Directory.GetFiles(folder), StringComparer.OrdinalIgnoreCase);
        var format = args.String("format");

        switch (format)
        {
            case "dwg":
            {
                if (ids.Count == 0) ids.Add(doc.ActiveView.Id);
                var options = args.OptString("dwg_setup") is { } setup
                    ? DWGExportOptions.GetPredefinedOptions(doc, setup) ?? throw new ArgumentException($"No DWG export setup named '{setup}'.")
                    : new DWGExportOptions();
                options.MergedViews = true;
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

        var files = Directory.GetFiles(folder).Where(f => !before.Contains(f)).Select(Path.GetFileName).ToList();
        return ToolResult.Ok(new { folder, files });
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

        var link = links.FirstOrDefault(l => l.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? throw new ArgumentException($"No link matching '{wanted}'. Links: {string.Join(", ", links.Select(l => l.Name))}");
        var linkDoc = link.GetLinkDocument() ?? throw new InvalidOperationException($"'{link.Name}' is not loaded.");

        if (args.OptString("category") is not { } category)
        {
            var counts = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
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
