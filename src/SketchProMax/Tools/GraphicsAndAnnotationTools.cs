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
                    var boxes = cropTo.Select(doc.GetElement).Where(e => e != null).Select(e => e.get_BoundingBox(view)).OfType<BoundingBoxXYZ>().ToList();
                    if (boxes.Count == 0) throw new ArgumentException("elements not visible in this view");
                    var crop = view.CropBox;
                    // The crop box lives in its own coordinates: bring every corner of every element box into them.
                    var inverse = crop.Transform.Inverse;
                    var corners = boxes.SelectMany(ViewKit.Corners).Select(inverse.OfPoint).ToList();
                    crop.Min = new XYZ(corners.Min(c => c.X) - margin, corners.Min(c => c.Y) - margin, crop.Min.Z);
                    crop.Max = new XYZ(corners.Max(c => c.X) + margin, corners.Max(c => c.Y) + margin, crop.Max.Z);
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
        XYZ ToModel(JsonElement p) => ViewKit.ToModel(doc, view, p);
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
        "center lines), or turn leaders on/off. Works on element tags and room/space/area tags. Pick tags by id, or all tags of a tag category in the view (e.g. 'Door Tags', 'Room Tags').";
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

    /// <summary>Element tags and spatial (room/space/area) tags expose the same head and leader, but share no interface.</summary>
    private sealed class TagRef(Element element)
    {
        public Element Element { get; } = element;
        public XYZ Head
        {
            get => Element is IndependentTag t ? t.TagHeadPosition : ((SpatialElementTag)Element).TagHeadPosition;
            set { if (Element is IndependentTag t) t.TagHeadPosition = value; else ((SpatialElementTag)Element).TagHeadPosition = value; }
        }
        public bool HasLeader
        {
            get => Element is IndependentTag t ? t.HasLeader : ((SpatialElementTag)Element).HasLeader;
            set { if (Element is IndependentTag t) t.HasLeader = value; else ((SpatialElementTag)Element).HasLeader = value; }
        }
        public string Text => Element switch
        {
            IndependentTag t => t.TagText,
            Autodesk.Revit.DB.Architecture.RoomTag r => r.TagText,
            SpatialElementTag st => st.TagText,
            _ => Element.Name,
        };
    }

    /// <summary>A tag's extent in view coordinates (u along RightDirection, v along UpDirection), cached as doubles.</summary>
    private sealed class Rect
    {
        public TagRef Tag = null!;
        public double U0, V0, U1, V1, Shift;
        public bool Overlaps(Rect o) => U0 < o.U1 && o.U0 < U1 && V0 < o.V1 && o.V0 < V1;
    }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        var elements = args.LongList("tag_ids").Select(i => doc.GetElement(new ElementId(i)))
            .Where(e => e is IndependentTag or SpatialElementTag).ToList();
        if (elements.Count == 0)
        {
            // Room, space and area tags are SpatialElementTag; they are also collected by category so none is missed.
            var found = new Dictionary<long, Element>();
            foreach (var e in new FilteredElementCollector(doc, view.Id)
                         .WherePasses(new ElementMulticlassFilter([typeof(IndependentTag), typeof(SpatialElementTag)])))
                found[e.Id.Value] = e;
            foreach (var tagCat in new[] { BuiltInCategory.OST_RoomTags, BuiltInCategory.OST_MEPSpaceTags, BuiltInCategory.OST_AreaTags })
                foreach (var e in new FilteredElementCollector(doc, view.Id).OfCategory(tagCat).WhereElementIsNotElementType())
                    if (e is IndependentTag or SpatialElementTag) found[e.Id.Value] = e;
            IEnumerable<Element> collector = found.Values;
            if (args.OptString("tag_category") is { } cat)
            {
                var bic = RevitHelpers.ParseCategory(doc, cat);
                collector = collector.Where(x => x.Category?.Id.Value == (long)bic);
            }
            elements = collector.ToList();
        }
        if (elements.Count == 0) return ToolResult.Error("No tags found in that view. Check the view and tag_category (e.g. 'Door Tags', 'Room Tags').");
        var tags = elements.Select(e => new TagRef(e)).ToList();

        XYZ right = view.RightDirection, up = view.UpDirection;
        Rect? ToRect(TagRef tag)
        {
            if (tag.Element.get_BoundingBox(view) is not { } box)
            {
                // Some spatial tags report no view bounding box; estimate one from the head and the text length
                // (about 2.5 mm text on paper), so room tags still take part in overlap checks.
                XYZ head;
                try { head = tag.Head; } catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
                var ch = 2.5 / 304.8 * Math.Max(1, view.Scale);
                var halfW = Math.Max(3, (tag.Text ?? "").Length) * ch * 0.35;
                var halfH = ch * 0.75;
                double hu = head.DotProduct(right), hv = head.DotProduct(up);
                return new Rect { Tag = tag, U0 = hu - halfW, V0 = hv - halfH, U1 = hu + halfW, V1 = hv + halfH };
            }
            double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
            foreach (var c in ViewKit.Corners(box))
            {
                double u = c.DotProduct(right), v = c.DotProduct(up);
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
            return new Rect { Tag = tag, U0 = u0, V0 = v0, U1 = u1, V1 = v1 };
        }

        var action = args.String("action");
        if (action is "report_overlaps" or "fix_overlaps")
        {
            // Bounding boxes read once; then sort by bottom edge and sweep, so only vertically close tags are compared.
            var rects = tags.Select(ToRect).OfType<Rect>().OrderBy(r => r.V0).ThenBy(r => r.U0).ToList();
            if (action == "report_overlaps")
            {
                var pairs = new List<(Rect a, Rect b)>();
                for (var i = 0; i < rects.Count; i++)
                    for (var j = i + 1; j < rects.Count && rects[j].V0 < rects[i].V1; j++)
                        if (rects[i].Overlaps(rects[j])) pairs.Add((rects[i], rects[j]));
                return ToolResult.Ok(new
                {
                    tags = tags.Count,
                    overlapping = pairs.Count,
                    pairs = pairs.Take(100).Select(x => new { a = x.a.Tag.Element.Id.Value, b = x.b.Tag.Element.Id.Value, aText = x.a.Tag.Text, bText = x.b.Tag.Text }),
                });
            }

            // Bottom-up: each tag is pushed up just clear of any already-settled tag it overlaps, with a small gap;
            // leaders keep it tied to its element. Settled tags stay sorted by top edge so the scan can stop early.
            var settled = new List<Rect>();
            var moved = new List<Rect>();
            foreach (var r in rects)
            {
                for (var guard = 0; guard <= settled.Count; guard++)
                {
                    Rect? hit = null;
                    for (var k = settled.Count - 1; k >= 0 && settled[k].V1 > r.V0; k--)
                        if (settled[k].Overlaps(r)) { hit = settled[k]; break; }
                    if (hit is null) break;
                    var shift = hit.V1 - r.V0 + (hit.V1 - hit.V0) * 0.15;
                    r.V0 += shift; r.V1 += shift; r.Shift += shift;
                }
                if (r.Shift > 0) moved.Add(r);
                var at = settled.FindLastIndex(s => s.V1 <= r.V1) + 1;
                settled.Insert(at, r);
            }

            using var tx = RevitHelpers.StartTransaction(doc, "Arrange tags");
            foreach (var r in moved)
            {
                if (!r.Tag.HasLeader) r.Tag.HasLeader = true;
                r.Tag.Head += up * r.Shift;
            }
            tx.Commit();
            return ToolResult.Ok(new { moved = moved.Count, ids = moved.Select(r => r.Tag.Element.Id.Value) });
        }

        using var t = RevitHelpers.StartTransaction(doc, "Arrange tags");
        if (action is "leaders_on" or "leaders_off")
        {
            foreach (var tag in tags) tag.HasLeader = action == "leaders_on";
        }
        else
        {
            // Heads in view coordinates, so alignment follows the view's own left/right/up even in rotated or section views.
            var heads = tags.Select(x => (tag: x, p: x.Head)).Select(x => (x.tag, x.p, u: x.p.DotProduct(right), v: x.p.DotProduct(up))).ToList();
            double target = action switch
            {
                "align_left" => heads.Min(h => h.u),
                "align_right" => heads.Max(h => h.u),
                "align_top" => heads.Max(h => h.v),
                "align_bottom" => heads.Min(h => h.v),
                "align_vertical_center" => heads.Average(h => h.u),
                _ => heads.Average(h => h.v),
            };
            var moveU = action is "align_left" or "align_right" or "align_vertical_center";
            foreach (var h in heads)
                h.tag.Head = moveU ? h.p + right * (target - h.u) : h.p + up * (target - h.v);
        }
        t.Commit();
        return ToolResult.Ok(new { arranged = tags.Count, action });
    }
}
