using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

// Looks at the model the jobs wrote as new code all day (bug 130, 22 Nov 2026: of 507 read-only code calls, about 200
// asked which views or sheets show an element, 130 read a slab or strip back, and 100 looked for text, tags, clouds or
// dimensions in a view). One call each, nothing written.

internal static class Lookup
{
    public const double Mm = 304.8;
    public static long R(double feet) => (long)Math.Round(feet * Mm);

    public static object? Overrides(View view, Element e)
    {
        try
        {
            var o = view.GetElementOverrides(e.Id);
            string? C(Color c) => c is { IsValid: true } ? $"{c.Red},{c.Green},{c.Blue}" : null;
            var lines = C(o.ProjectionLineColor);
            var fill = C(o.SurfaceForegroundPatternColor);
            var cutLines = C(o.CutLineColor);
            var halftone = o.Halftone;
            return lines is null && fill is null && cutLines is null && !halftone ? null : new { lines, fill, cut_lines = cutLines, halftone };
        }
        catch (Exception) { return null; }
    }

    public static Dictionary<long, string> SheetOf(Document doc)
    {
        var map = new Dictionary<long, string>();
        foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            foreach (var id in sheet.GetAllPlacedViews())
                map[id.Value] = sheet.SheetNumber;
        return map;
    }
}

public sealed class ViewsShowingTool : IRevitTool
{
    public string Name => "views_showing";
    public string Description =>
        "Read-only: which views and sheets show the given elements, with the sheet number, and whether each element is hidden " +
        "or has graphic overrides there. Use it instead of writing code to find where an element is drawn. By default only views " +
        "placed on sheets; on_sheets_only=false checks every plan, section, elevation and 3D view (slower: continue_from pages on).";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Up to 50 elements"},
      "on_sheets_only": {"type": "boolean", "description": "Default true"},
      "continue_from": {"type": "integer", "description": "From an earlier answer, when it stopped part way"}
    }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var ids = args.LongList("element_ids").Take(50).Select(i => new ElementId(i)).Where(i => doc.GetElement(i) is not null).ToList();
        if (ids.Count == 0) return ToolResult.Error("None of the element ids is in this model.");
        var sheets = Lookup.SheetOf(doc);
        var onSheets = args.OptBool("on_sheets_only", true);
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType is not (ViewType.DrawingSheet or ViewType.Schedule or ViewType.Legend or ViewType.ProjectBrowser
                or ViewType.SystemBrowser or ViewType.Internal or ViewType.Report or ViewType.Undefined or ViewType.DraftingView or ViewType.ColumnSchedule or ViewType.PanelSchedule))
            .Where(v => !onSheets || sheets.ContainsKey(v.Id.Value))
            .OrderBy(v => v.Id.Value).ToList();
        var start = (int)(args.OptLong("continue_from") ?? 0);
        var watch = Stopwatch.StartNew();
        var found = ids.ToDictionary(i => i.Value, _ => new List<object>());
        var set = new HashSet<ElementId>(ids);
        int at = start;
        for (; at < views.Count && watch.Elapsed.TotalSeconds < 40; at++)
        {
            var view = views[at];
            List<ElementId> shown;
            try { shown = new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(set)).ToElementIds().ToList(); }
            catch (Exception) { continue; }
            var hiddenToo = ids.Where(i => !shown.Contains(i) && doc.GetElement(i) is { } e && e.IsHidden(view)).ToList();
            foreach (var id in shown.Concat(hiddenToo))
            {
                var e = doc.GetElement(id);
                found[id.Value].Add(new
                {
                    view = view.Id.Value, name = view.Name, type = view.ViewType.ToString(),
                    sheet = sheets.TryGetValue(view.Id.Value, out var s) ? s : null,
                    hidden = hiddenToo.Contains(id) ? true : (bool?)null,
                    overrides = Lookup.Overrides(view, e),
                });
            }
        }
        return ToolResult.Ok(new
        {
            views_checked = at - start, of = views.Count,
            continue_from = at < views.Count ? at : (int?)null,
            elements = found.Select(f => new { id = f.Key, shown_in = f.Value.Count, views = f.Value }),
        });
    }
}

public sealed class ElementReadbackTool : IRevitTool
{
    public string Name => "element_readback";
    public string Description =>
        "Read-only: everything about a few elements in one call, instead of writing code to read them back after a change: " +
        "category, family and type, level and offsets, workset, phase, top and bottom (internal mm), thickness, sketch outline " +
        "points, slab shape points, joined elements (and which one cuts), host, the filled-in instance parameters, and when " +
        "view_id is given whether it is hidden or overridden there. Lengths in mm.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Up to 20 elements"},
      "view_id": {"type": "integer", "description": "Optional: also hidden / overrides in this view"},
      "points": {"type": "boolean", "description": "Include sketch and shape points (default true, at most 60 per element)"}
    }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = args.OptLong("view_id") is { } vid ? doc.GetElement(new ElementId(vid)) as View : null;
        var points = args.OptBool("points", true);
        var list = new List<object>();
        foreach (var id in args.LongList("element_ids").Take(20))
        {
            var e = doc.GetElement(new ElementId(id));
            if (e is null) { list.Add(new { id, missing = true }); continue; }
            list.Add(Read(doc, e, view, points));
        }
        return ToolResult.Ok(new { elements = list });
    }

    private static object Read(Document doc, Element e, View? view, bool points)
    {
        var type = doc.GetElement(e.GetTypeId()) as ElementType;
        var box = e.get_BoundingBox(null);
        object? joins = null;
        try
        {
            joins = JoinGeometryUtils.GetJoinedElements(doc, e).Select(j => new
            {
                id = j.Value, category = doc.GetElement(j)?.Category?.Name,
                this_cuts = Safe(() => JoinGeometryUtils.IsCuttingElementInJoin(doc, e, doc.GetElement(j))),
            }).ToList();
        }
        catch (Exception) { }
        return new
        {
            id = e.Id.Value, category = e.Category?.Name, family = type?.FamilyName, type = type?.Name,
            level = doc.GetElement(e.LevelId)?.Name,
            workset = doc.IsWorkshared ? doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name : null,
            phase_created = doc.GetElement(e.CreatedPhaseId)?.Name, phase_demolished = doc.GetElement(e.DemolishedPhaseId)?.Name,
            top_mm = box is null ? (long?)null : Lookup.R(box.Max.Z), bottom_mm = box is null ? (long?)null : Lookup.R(box.Min.Z),
            plan_min_mm = box is null ? null : new[] { Lookup.R(box.Min.X), Lookup.R(box.Min.Y) },
            plan_max_mm = box is null ? null : new[] { Lookup.R(box.Max.X), Lookup.R(box.Max.Y) },
            thickness_mm = e is Floor f ? Safe(() => (long?)Lookup.R(f.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)?.AsDouble() ?? 0)) : null,
            host = e is FamilyInstance fi ? fi.Host?.Id.Value : null,
            joined = joins,
            sketch = points ? Sketch(doc, e) : null,
            shape_points = points ? Shape(e) : null,
            in_view = view is null ? null : new { view = view.Id.Value, hidden = Safe(() => e.IsHidden(view)), overrides = Lookup.Overrides(view, e) },
            parameters = e.Parameters.Cast<Parameter>()
                .Where(p => p.HasValue && p.Definition is not null)
                .Select(p => (p.Definition.Name, Value: Safe(() => RevitHelpers.ParamValue(p)?.ToString())))
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .GroupBy(p => p.Name).Select(g => g.First()).OrderBy(p => p.Name).Take(80)
                .ToDictionary(p => p.Name, p => p.Value),
        };
    }

    private static object? Sketch(Document doc, Element e)
    {
        try
        {
            if (e is not Floor && e is not RoofBase && e is not Ceiling && e is not Wall) return null;
            var sketch = doc.GetElement((e as Floor)?.SketchId ?? (e as Ceiling)?.SketchId ?? (e as Wall)?.SketchId ?? ElementId.InvalidElementId) as Sketch;
            if (sketch is null) return null;
            int count = 0;
            return sketch.Profile.Cast<CurveArray>().Select(loop => loop.Cast<Curve>().Select(c => c.GetEndPoint(0))
                .TakeWhile(_ => count++ < 60).Select(p => new[] { Lookup.R(p.X), Lookup.R(p.Y), Lookup.R(p.Z) }).ToList()).ToList();
        }
        catch (Exception) { return null; }
    }

    private static object? Shape(Element e)
    {
        try
        {
            var editor = (e as Floor)?.GetSlabShapeEditor();
            if (editor is null || !editor.IsEnabled) return null;
            var vertices = editor.SlabShapeVertices.Cast<SlabShapeVertex>().ToList();
            return new
            {
                count = vertices.Count,
                points = vertices.Take(60).Select(v => new[] { Lookup.R(v.Position.X), Lookup.R(v.Position.Y), Lookup.R(v.Position.Z) }),
            };
        }
        catch (Exception) { return null; }
    }

    private static T? Safe<T>(Func<T> read) { try { return read(); } catch (Exception) { return default; } }
}

public sealed class FindInViewsTool : IRevitTool
{
    public string Name => "find_in_views";
    public string Description =>
        "Read-only: the text notes, tags, revision clouds and dimensions drawn in views (or on sheets and every view placed on " +
        "them), optionally only those whose text contains any of the given words, with their id, view, text and position " +
        "(model mm). Dimensions give their value, any override, prefix and suffix. Use it instead of writing code to look for " +
        "labels, notes, clouds or dimension texts.";
    public string PropertiesJson => """
    {
      "view_ids": {"type": "array", "items": {"type": "integer"}, "description": "Views, or sheets (a sheet also checks its placed views)"},
      "words": {"type": "array", "items": {"type": "string"}, "description": "Optional: keep only items whose text has one of these (any case)"},
      "kinds": {"type": "array", "items": {"type": "string"}, "description": "Optional: text, tag, cloud, dimension (default all)"},
      "limit": {"type": "integer", "description": "Default 300"}
    }
    """;
    public string[] Required => ["view_ids"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var words = args.Has("words") ? args.StringList("words").Where(w => w.Length > 0).ToList() : [];
        var kinds = args.Has("kinds") ? args.StringList("kinds").Select(k => k.ToLowerInvariant()).ToHashSet() : ["text", "tag", "cloud", "dimension"];
        var limit = (int)(args.OptLong("limit") ?? 300);
        var views = new List<View>();
        foreach (var id in args.LongList("view_ids"))
        {
            if (doc.GetElement(new ElementId(id)) is not View v) continue;
            views.Add(v);
            if (v is ViewSheet sheet) views.AddRange(sheet.GetAllPlacedViews().Select(p => doc.GetElement(p)).OfType<View>());
        }
        if (views.Count == 0) return ToolResult.Error("None of view_ids is a view or sheet in this model.");
        var items = new List<object>();
        int total = 0;
        foreach (var view in views.GroupBy(v => v.Id.Value).Select(g => g.First()))
        {
            foreach (var e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                if (e.OwnerViewId != view.Id) continue;
                var (kind, text, extra) = Describe(e);
                if (kind is null || !kinds.Contains(kind)) continue;
                if (words.Count > 0 && !words.Any(w => (text ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                total++;
                if (items.Count >= limit) continue;
                var box = e.get_BoundingBox(view);
                items.Add(new
                {
                    id = e.Id.Value, kind, view = view.Id.Value, view_name = view.Name, text, extra,
                    at_mm = box is null ? null : new[] { Lookup.R((box.Min.X + box.Max.X) / 2), Lookup.R((box.Min.Y + box.Max.Y) / 2), Lookup.R((box.Min.Z + box.Max.Z) / 2) },
                });
            }
        }
        return ToolResult.Ok(new { views = views.Count, found = total, shown = items.Count, items });
    }

    private static (string? Kind, string? Text, object? Extra) Describe(Element e)
    {
        try
        {
            switch (e)
            {
                case TextNote t: return ("text", t.Text.TrimEnd('\r'), null);
                case IndependentTag tag: return ("tag", tag.TagText, new { tagged = tag.GetTaggedLocalElementIds().Select(i => i.Value) });
                case RevisionCloud c:
                    var comments = c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    var mark = c.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    return ("cloud", string.Join(" ", new[] { mark, comments }.Where(s => !string.IsNullOrEmpty(s))), new { revision = c.Document.GetElement(c.RevisionId)?.Name });
                case Dimension d:
                    var segments = d.Segments.Size > 0 ? d.Segments.Cast<DimensionSegment>().ToList() : null;
                    var value = segments is null ? d.ValueString : string.Join(" / ", segments.Select(s => s.ValueString));
                    var over = segments is null ? d.ValueOverride : string.Join(" / ", segments.Select(s => s.ValueOverride ?? ""));
                    var text = string.Join(" ", new[] { d.Prefix, string.IsNullOrEmpty(over?.Replace(" / ", "")) ? value : over, d.Suffix, d.Above, d.Below }.Where(s => !string.IsNullOrEmpty(s)));
                    return ("dimension", text, new { value, value_override = string.IsNullOrEmpty(over?.Replace(" / ", "")) ? null : over, prefix = d.Prefix, suffix = d.Suffix, above = d.Above, below = d.Below });
                default: return (null, null, null);
            }
        }
        catch (Exception) { return (null, null, null); }
    }
}
