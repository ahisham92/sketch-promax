using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class SetViewGraphicsTool : IRevitTool
{
    public string Name => "set_view_graphics";
    public string Description =>
        "Visibility/graphics for one or more views: hide or show categories, hide/unhide/isolate elements, override " +
        "graphics of categories or elements (color, halftone, transparency, line weight), set detail level, crop " +
        "the view to elements, turn the crop on/off, or assign a scope box. Views controlled by a view template " +
        "may refuse some changes; change the template instead (its id works here too).";
    public string PropertiesJson => """
    {
      "view_ids": {"type": "array", "items": {"type": "integer"}, "description": "Default: active view."},
      "hide_categories": {"type": "array", "items": {"type": "string"}},
      "show_categories": {"type": "array", "items": {"type": "string"}},
      "hide_element_ids": {"type": "array", "items": {"type": "integer"}},
      "unhide_element_ids": {"type": "array", "items": {"type": "integer"}},
      "isolate_element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Hide everything else in the view (permanent, unlike Revit's temporary isolate)."},
      "category_overrides": {"type": "array", "items": {"type": "object", "properties": {"category": {"type": "string"}, "color": {"type": "string"}, "halftone": {"type": "boolean"}, "transparency": {"type": "integer"}, "line_weight": {"type": "integer"}}}},
      "element_overrides": {"type": "array", "items": {"type": "object", "properties": {"element_ids": {"type": "array", "items": {"type": "integer"}}, "color": {"type": "string"}, "halftone": {"type": "boolean"}, "transparency": {"type": "integer"}, "line_weight": {"type": "integer"}}}},
      "clear_element_overrides": {"type": "array", "items": {"type": "integer"}},
      "detail_level": {"type": "string", "enum": ["coarse", "medium", "fine"]},
      "crop_to_element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Plan views: crop around these elements."},
      "crop_margin": {"type": "number", "description": "Project units, default 1 m."},
      "crop_active": {"type": "boolean"},
      "crop_visible": {"type": "boolean"},
      "scope_box": {"type": "string", "description": "Scope box name, or empty string to remove."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var views = args.LongList("view_ids").Select(i => doc.GetElement(new ElementId(i))).OfType<View>().ToList();
        if (views.Count == 0) views.Add(doc.ActiveView);
        var ids = (string key) => args.LongList(key).Select(i => new ElementId(i)).ToList();
        var report = new List<object>();

        using var t = RevitHelpers.StartTransaction(doc, "View graphics");
        foreach (var view in views)
        {
            var done = new List<string>();
            var problems = new List<string>();
            void Try(string what, Action action)
            {
                try { action(); done.Add(what); }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
                { problems.Add($"{what}: {ex.Message}"); }
            }

            foreach (var (key, hidden) in new[] { ("hide_categories", true), ("show_categories", false) })
                foreach (var name in args.StringList(key))
                    Try($"{(hidden ? "hide" : "show")} {name}", () =>
                    {
                        var cat = Category.GetCategory(doc, RevitHelpers.ParseCategory(doc, name))
                                  ?? throw new ArgumentException("category not in this model");
                        if (!view.CanCategoryBeHidden(cat.Id)) throw new InvalidOperationException("can't be hidden in this view");
                        view.SetCategoryHidden(cat.Id, hidden);
                    });

            if (ids("hide_element_ids") is { Count: > 0 } hide) Try($"hide {hide.Count} elements", () => view.HideElements(hide));
            if (ids("unhide_element_ids") is { Count: > 0 } unhide) Try($"unhide {unhide.Count} elements", () => view.UnhideElements(unhide));
            if (ids("isolate_element_ids") is { Count: > 0 } keep)
            {
                var keepSet = new HashSet<ElementId>(keep);
                var others = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                    .Where(e => !keepSet.Contains(e.Id) && e.CanBeHidden(view)).Select(e => e.Id).ToList();
                Try($"isolate {keep.Count} elements (hid {others.Count})", () => view.HideElements(others));
            }

            foreach (var spec in args.Array("category_overrides"))
            {
                var name = spec.GetProperty("category").GetString() ?? "";
                Try($"override {name}", () =>
                {
                    var cat = Category.GetCategory(doc, RevitHelpers.ParseCategory(doc, name))
                              ?? throw new ArgumentException("category not in this model");
                    view.SetCategoryOverrides(cat.Id, Filters.Overrides(doc, spec));
                });
            }
            foreach (var spec in args.Array("element_overrides"))
            {
                var targets = spec.GetProperty("element_ids").EnumerateArray().Select(e => new ElementId(e.GetInt64())).ToList();
                var ogs = Filters.Overrides(doc, spec);
                Try($"override {targets.Count} elements", () => targets.ForEach(id => view.SetElementOverrides(id, ogs)));
            }
            if (ids("clear_element_overrides") is { Count: > 0 } clear)
                Try($"clear overrides on {clear.Count} elements", () => clear.ForEach(id => view.SetElementOverrides(id, new OverrideGraphicSettings())));

            if (args.OptString("detail_level") is { } level)
                Try($"detail level {level}", () => view.DetailLevel = level switch
                {
                    "coarse" => ViewDetailLevel.Coarse,
                    "medium" => ViewDetailLevel.Medium,
                    _ => ViewDetailLevel.Fine,
                });

            if (args.OptString("scope_box") is { } scopeName)
                Try(scopeName.Length == 0 ? "remove scope box" : $"scope box {scopeName}", () =>
                {
                    var p = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)
                            ?? throw new InvalidOperationException("this view can't use a scope box");
                    var id = scopeName.Length == 0 ? ElementId.InvalidElementId
                        : new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                              .FirstOrDefault(e => string.Equals(e.Name, scopeName, StringComparison.OrdinalIgnoreCase))?.Id
                          ?? throw new ArgumentException("no scope box with that name");
                    p.Set(id);
                });

            if (ids("crop_to_element_ids") is { Count: > 0 } cropTo)
                Try("crop to elements", () =>
                {
                    if (view is not ViewPlan) throw new InvalidOperationException("only plan views");
                    var margin = args.OptDouble("crop_margin") is { } m ? RevitHelpers.ToInternal(doc, m) : UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Meters);
                    var box = ViewKit.Extents(doc, cropTo.Select(doc.GetElement).Where(e => e != null), view)
                              ?? throw new ArgumentException("elements not visible in this view");
                    var crop = view.CropBox;
                    crop.Min = new XYZ(box.Min.X - margin, box.Min.Y - margin, crop.Min.Z);
                    crop.Max = new XYZ(box.Max.X + margin, box.Max.Y + margin, crop.Max.Z);
                    view.CropBoxActive = true;
                    view.CropBox = crop;
                });
            if (args.Has("crop_active")) Try("crop on/off", () => view.CropBoxActive = args.OptBool("crop_active"));
            if (args.Has("crop_visible")) Try("crop visible", () => view.CropBoxVisible = args.OptBool("crop_visible"));

            report.Add(new { view = view.Name, id = view.Id.Value, done, problems });
        }
        t.Commit();
        return ToolResult.Ok(new { views = report });
    }
}

public sealed class AddAnnotationsTool : IRevitTool
{
    public string Name => "add_annotations";
    public string Description =>
        "Draw annotation in a view: text notes, detail lines (any line style, open or closed) and filled regions. " +
        "Coordinates: in plans, model x, y in project units; in sections, elevations and drafting views, x along the " +
        "view's right direction and y up from the view origin. Use list_types with category 'Text Notes' or " +
        "'Filled region' for type names.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "text_notes": {"type": "array", "items": {"type": "object", "properties": {"text": {"type": "string"}, "at": {"type": "array", "items": {"type": "number"}}, "type": {"type": "string"}, "width": {"type": "number", "description": "Wrap width on paper, mm. Optional."}}, "required": ["text", "at"]}},
      "detail_lines": {"type": "array", "items": {"type": "object", "properties": {"points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}}, "closed": {"type": "boolean"}, "line_style": {"type": "string"}}, "required": ["points"]}},
      "filled_regions": {"type": "array", "items": {"type": "object", "properties": {"points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}}, "type": {"type": "string"}}, "required": ["points"]}}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        XYZ ToModel(JsonElement p)
        {
            double x = RevitHelpers.ToInternal(doc, p[0].GetDouble()), y = RevitHelpers.ToInternal(doc, p[1].GetDouble());
            if (view is ViewPlan plan) return new XYZ(x, y, plan.GenLevel?.Elevation ?? 0);
            return view.Origin + view.RightDirection * x + view.UpDirection * y;
        }
        List<Curve> Polyline(JsonElement spec, bool closed)
        {
            var pts = spec.GetProperty("points").EnumerateArray().Select(ToModel).ToList();
            if (closed && pts.Count > 2 && !pts[0].IsAlmostEqualTo(pts[pts.Count - 1])) pts.Add(pts[0]);
            var curves = new List<Curve>();
            for (var i = 0; i + 1 < pts.Count; i++)
                if (pts[i].DistanceTo(pts[i + 1]) > doc.Application.ShortCurveTolerance) curves.Add(Line.CreateBound(pts[i], pts[i + 1]));
            return curves;
        }

        var created = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Add annotations");

        var textTypes = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
        foreach (var spec in args.Array("text_notes"))
        {
            var typeName = spec.TryGetProperty("type", out var tn) ? tn.GetString() : null;
            var type = textTypes.FirstOrDefault(x => string.Equals(x.Name, typeName, StringComparison.OrdinalIgnoreCase))
                       ?? doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType
                       ?? textTypes.First();
            var options = new TextNoteOptions(type.Id) { HorizontalAlignment = HorizontalTextAlignment.Left };
            var at = ToModel(spec.GetProperty("at"));
            var text = spec.GetProperty("text").GetString() ?? "";
            var note = spec.TryGetProperty("width", out var w)
                ? TextNote.Create(doc, view.Id, at, UnitUtils.ConvertToInternalUnits(w.GetDouble(), UnitTypeId.Millimeters), text, options)
                : TextNote.Create(doc, view.Id, at, text, options);
            created.Add(note.Id.Value);
        }

        var lineStyles = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines).SubCategories.Cast<Category>().ToList();
        foreach (var spec in args.Array("detail_lines"))
        {
            var closed = spec.TryGetProperty("closed", out var c) && c.GetBoolean();
            var styleName = spec.TryGetProperty("line_style", out var ls) ? ls.GetString() : null;
            var style = styleName is null ? null
                : lineStyles.FirstOrDefault(x => string.Equals(x.Name, styleName, StringComparison.OrdinalIgnoreCase))?.GetGraphicsStyle(GraphicsStyleType.Projection)
                  ?? throw new ArgumentException($"No line style '{styleName}'. Available: {string.Join(", ", lineStyles.Select(x => x.Name))}");
            foreach (var curve in Polyline(spec, closed))
            {
                var line = doc.Create.NewDetailCurve(view, curve);
                if (style != null) line.LineStyle = style;
                created.Add(line.Id.Value);
            }
        }

        var regionTypes = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
        foreach (var spec in args.Array("filled_regions"))
        {
            var typeName = spec.TryGetProperty("type", out var rt) ? rt.GetString() : null;
            var type = regionTypes.FirstOrDefault(x => string.Equals(x.Name, typeName, StringComparison.OrdinalIgnoreCase)) ?? regionTypes.FirstOrDefault()
                       ?? throw new InvalidOperationException("This model has no filled region types.");
            var loop = CurveLoop.Create(Polyline(spec, closed: true));
            created.Add(FilledRegion.Create(doc, type.Id, view.Id, [loop]).Id.Value);
        }

        t.Commit();
        return ToolResult.Ok(new { view = view.Name, created = created.Count, ids = created });
    }
}

public sealed class ArrangeTagsTool : IRevitTool
{
    public string Name => "arrange_tags";
    public string Description =>
        "Tidy tags in a view: report overlapping tags, move overlapping tags apart (fix_overlaps), align tag heads in a row or column (left/right/top/bottom or " +
        "center lines), or turn leaders on/off. Pick tags by id, or all tags of a tag category in the view (e.g. 'Door Tags', 'Room Tags').";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer"},
      "tag_ids": {"type": "array", "items": {"type": "integer"}},
      "tag_category": {"type": "string"},
      "action": {"type": "string", "enum": ["report_overlaps", "fix_overlaps", "align_left", "align_right", "align_top", "align_bottom", "align_horizontal_center", "align_vertical_center", "leaders_on", "leaders_off"]}
    }
    """;
    public string[] Required => ["action"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        var tags = args.LongList("tag_ids").Select(i => doc.GetElement(new ElementId(i))).OfType<IndependentTag>().ToList();
        if (tags.Count == 0)
        {
            var collector = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>();
            if (args.OptString("tag_category") is { } cat)
            {
                var bic = RevitHelpers.ParseCategory(doc, cat);
                collector = collector.Where(x => x.Category?.Id.Value == (long)bic);
            }
            tags = collector.ToList();
        }
        if (tags.Count == 0) return ToolResult.Error("No tags found in that view.");

        var action = args.String("action");
        if (action == "report_overlaps")
        {
            var boxes = tags.Select(x => (Tag: x, Box: x.get_BoundingBox(view))).Where(x => x.Box != null).ToList();
            var overlaps = new List<object>();
            for (var i = 0; i < boxes.Count; i++)
                for (var j = i + 1; j < boxes.Count; j++)
                {
                    BoundingBoxXYZ a = boxes[i].Box, b = boxes[j].Box;
                    if (a.Min.X < b.Max.X && b.Min.X < a.Max.X && a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y)
                        overlaps.Add(new { a = boxes[i].Tag.Id.Value, b = boxes[j].Tag.Id.Value, aText = boxes[i].Tag.TagText, bText = boxes[j].Tag.TagText });
                }
            return ToolResult.Ok(new { tags = tags.Count, overlapping = overlaps.Count, pairs = overlaps.Take(100) });
        }

        using var t = RevitHelpers.StartTransaction(doc, "Arrange tags");
        if (action == "fix_overlaps")
        {
            var moved = new HashSet<long>();
            for (var pass = 0; pass < 6; pass++)
            {
                var boxes = tags.Select(x => (Tag: x, Box: x.get_BoundingBox(view))).Where(x => x.Box != null)
                    .OrderBy(x => x.Box.Min.Y).ThenBy(x => x.Box.Min.X).ToList();
                var any = false;
                for (var i = 0; i < boxes.Count; i++)
                    for (var j = i + 1; j < boxes.Count; j++)
                    {
                        BoundingBoxXYZ a = boxes[i].Box, b = boxes[j].Box;
                        if (!(a.Min.X < b.Max.X && b.Min.X < a.Max.X && a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y)) continue;
                        // Push the upper tag up just clear of the lower one, with a small gap; leaders keep it tied to its element.
                        var shift = a.Max.Y - b.Min.Y + (a.Max.Y - a.Min.Y) * 0.15;
                        var tag = boxes[j].Tag;
                        if (!tag.HasLeader) tag.HasLeader = true;
                        tag.TagHeadPosition += view.UpDirection * shift;
                        boxes[j] = (tag, new BoundingBoxXYZ { Min = b.Min + XYZ.BasisY * shift, Max = b.Max + XYZ.BasisY * shift });
                        moved.Add(tag.Id.Value);
                        any = true;
                    }
                if (!any) break;
                doc.Regenerate();
            }
            t.Commit();
            return ToolResult.Ok(new { moved = moved.Count, ids = moved });
        }
        if (action is "leaders_on" or "leaders_off")
        {
            foreach (var tag in tags) tag.HasLeader = action == "leaders_on";
        }
        else
        {
            var heads = tags.Select(x => x.TagHeadPosition).ToList();
            double target = action switch
            {
                "align_left" => heads.Min(p => p.X),
                "align_right" => heads.Max(p => p.X),
                "align_top" => heads.Max(p => p.Y),
                "align_bottom" => heads.Min(p => p.Y),
                "align_vertical_center" => heads.Average(p => p.X),
                _ => heads.Average(p => p.Y),
            };
            var moveX = action is "align_left" or "align_right" or "align_vertical_center";
            foreach (var tag in tags)
            {
                var p = tag.TagHeadPosition;
                tag.TagHeadPosition = moveX ? new XYZ(target, p.Y, p.Z) : new XYZ(p.X, target, p.Z);
            }
        }
        t.Commit();
        return ToolResult.Ok(new { arranged = tags.Count, action });
    }
}
