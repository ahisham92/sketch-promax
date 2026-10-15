using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Points given in a view: [x, y] on a plan's level, or along the view's right/up axes elsewhere; [x, y, z] is model space.</summary>
internal static class ViewPoints
{
    public static XYZ ToModel(Document doc, View view, JsonElement p)
    {
        var v = p.EnumerateArray().Select(e => RevitHelpers.ToInternal(doc, e.GetDouble())).ToArray();
        if (v.Length < 2) throw new ArgumentException("A point needs at least [x, y].");
        if (v.Length > 2) return new XYZ(v[0], v[1], v[2]);
        if (view is ViewPlan plan) return new XYZ(v[0], v[1], plan.GenLevel?.ProjectElevation ?? 0);
        return view.Origin + view.RightDirection * v[0] + view.UpDirection * v[1];
    }

    public static FamilySymbol Symbol(Document doc, BuiltInCategory category, string? name, string what)
    {
        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>()
            .OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
        if (symbols.Count == 0) throw new InvalidOperationException($"No {what} family is loaded. Load one with load_family first.");
        if (name is null) return symbols[0];
        return DetailTypes.Find(doc, category, name, what, out _);
    }
}

public sealed class AddKeynoteTagsTool : IRevitTool
{
    public string Name => "add_keynote_tags";
    public string Description =>
        "Place keynote tags on elements in a view: the given element_ids, or every element of a category in the view. " +
        "Whether the tag shows the element keynote, material keynote or user keynote is set by the keynote tag family/type " +
        "(e.g. 'Keynote Tag: Keynote Number'). Elements already keynoted in the view are skipped; elements with an empty " +
        "Keynote parameter are reported (set it with set_parameters, parameter 'Keynote').";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "element_ids": {"type": "array", "items": {"type": "integer"}},
      "category": {"type": "string", "description": "Used when element_ids is not given, e.g. Walls, Doors, Detail Items."},
      "tag_type": {"type": "string", "description": "Keynote tag 'Family: Type' (default: first loaded keynote tag)."},
      "leader": {"type": "boolean", "description": "Default true."},
      "offset": {"type": "array", "items": {"type": "number"}, "description": "[dx, dy] from the element centre to the tag head, project units (default ~1 m / 3 ft right and up)."}
    }
    """);
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view.IsTemplate || view is ViewSchedule || view.ViewType == ViewType.DrawingSheet)
            return ToolResult.Error($"'{view.Name}' can't hold keynote tags. Use a plan, section, elevation, detail or 3D view.");
        var tagType = ViewPoints.Symbol(doc, BuiltInCategory.OST_KeynoteTags, args.OptString("tag_type"), "keynote tag");
        var guard = DependentGuard.For(doc, view, args.OptString("dependent_view"));

        List<Element> targets;
        var ids = args.LongList("element_ids");
        if (ids.Count > 0)
            targets = ids.Select(i => doc.GetElement(new ElementId(i)) ?? throw new ArgumentException($"No element with id {i}.")).ToList();
        else if (args.OptString("category") is { } cat)
            targets = new FilteredElementCollector(doc, view.Id).OfCategory(RevitHelpers.ParseCategory(doc, cat))
                .WhereElementIsNotElementType().ToList();
        else return ToolResult.Error("Give element_ids or a category.");

        var already = new HashSet<long>();
        foreach (var tag in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_KeynoteTags)
                     .WhereElementIsNotElementType().OfType<IndependentTag>())
            foreach (var id in tag.GetTaggedLocalElementIds()) already.Add(id.Value);

        double dx = 3, dy = 3;
        if (args.Has("offset"))
        {
            var o = args.Array("offset").Select(e => RevitHelpers.ToInternal(doc, e.GetDouble())).ToList();
            if (o.Count >= 2) { dx = o[0]; dy = o[1]; }
        }
        var leader = args.OptBool("leader", true);

        var placed = new List<long>();
        var noKeynote = new List<long>();
        var reasons = new Dictionary<string, int>();
        void Skip(string why) => reasons[why] = reasons.TryGetValue(why, out var n) ? n + 1 : 1;
        var skippedExisting = 0;

        using var t = RevitHelpers.StartTransaction(doc, "Add keynote tags");
        if (!tagType.IsActive) { tagType.Activate(); doc.Regenerate(); }
        foreach (var e in targets)
        {
            if (already.Contains(e.Id.Value)) { skippedExisting++; continue; }
            var keynote = e.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString();
            if (string.IsNullOrWhiteSpace(keynote) && doc.GetElement(e.GetTypeId()) is { } type)
                keynote = type.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString();
            if (string.IsNullOrWhiteSpace(keynote)) noKeynote.Add(e.Id.Value);

            var bb = e.get_BoundingBox(view);
            if (bb is null) { Skip("not visible in the view"); continue; }
            var center = (bb.Min + bb.Max) / 2;
            var head = view is ViewPlan ? center + new XYZ(dx, dy, 0) : center + view.RightDirection * dx + view.UpDirection * dy;
            if (!guard.Keep(head)) { Skip("tag head outside this dependent view's crop"); continue; }
            try
            {
                var tag = IndependentTag.Create(doc, tagType.Id, guard.Target.Id, new Reference(e), leader, TagOrientation.Horizontal, center);
                if (leader) { tag.LeaderEndCondition = LeaderEndCondition.Free; }
                tag.TagHeadPosition = head;
                placed.Add(tag.Id.Value);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { Skip(ex.Message.Trim()); }
        }
        if (placed.Count == 0)
        {
            t.RollBack();
            if (targets.Count == 0) return ToolResult.Error($"No elements to keynote in '{view.Name}'.");
            if (skippedExisting == targets.Count) return ToolResult.Ok(new { placed = 0, note = "Every element already has a keynote tag in this view." });
            return ToolResult.Error($"No keynote tags were placed in '{view.Name}'. Revit said: {string.Join("; ", reasons.Select(r => $"{r.Value}× {r.Key}"))}");
        }
        t.Commit();
        return ToolResult.Ok(new
        {
            view = view.Name, tagType = $"{tagType.FamilyName}: {tagType.Name}", placed = placed.Count, ids = placed.Take(200),
            alreadyTagged = skippedExisting,
            withoutKeynoteValue = noKeynote.Count, withoutKeynoteIds = noKeynote.Take(50),
            skippedBecause = reasons.Select(r => $"{r.Value}: {r.Key}"),
            dependentView = guard.Report(),
            note = noKeynote.Count > 0 ? "Some elements have no Keynote value, so their tags show '?' or blank. Set 'Keynote' with set_parameters (on the type for element keynotes)." : null,
        });
    }
}

public sealed class PlaceDetailComponentsTool : IRevitTool
{
    public string Name => "place_detail_components";
    public string Description =>
        "Place detail components (detail items, e.g. 'Break Line', 'Wood Blocking', 'Insulation', 'Concrete Masonry Units') " +
        "in a plan, section, elevation, drafting or detail view. Point-based components take 'at'; line-based ones take " +
        "'start' and 'end'. Points are [x, y] in project units: model x,y on plans, or along the view's right/up axes " +
        "measured from the view origin in other views; [x, y, z] is a model point. Use list_types category 'Detail Items' to find types. " +
        "Each placement is its own step (one refused item undoes nothing else). Parameters are set after placing, read back after " +
        "regeneration and re-applied once if they did not stick; what still differs is reported with the reason (formula, read-only...). " +
        "If Revit refuses a new instance ('Can't make type', constraint errors), an existing instance of the type is copied instead " +
        "(method 'auto', the default); see also place_detail_item_copy.";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "family": {"type": "string", "description": "Detail item 'Family: Type' used when a placement doesn't name one."},
      "placements": {"type": "array", "items": {"type": "object", "properties": {
        "family": {"type": "string"},
        "at": {"type": "array", "items": {"type": "number"}},
        "start": {"type": "array", "items": {"type": "number"}},
        "end": {"type": "array", "items": {"type": "number"}},
        "rotation_degrees": {"type": "number"},
        "parameters": {"type": "object", "description": "Instance parameters to set, name -> value."}
      }}},
      "method": {"type": "string", "enum": ["auto", "new", "copy"], "description": "auto (default): new instance, falling back to copying an existing one when Revit refuses. new: never copy. copy: always copy an existing instance."}
    }
    """);
    public string[] Required => ["placements"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view.IsTemplate || view is View3D || view is ViewSchedule || view.ViewType == ViewType.DrawingSheet)
            return ToolResult.Error($"Detail components go in plan, section, elevation, drafting or detail views, not '{view.Name}' ({view.ViewType}).");
        var method = args.OptString("method") ?? "auto";
        if (method is not ("auto" or "new" or "copy")) throw new ArgumentException("method must be auto, new or copy.");
        var guard = DependentGuard.For(doc, view, args.OptString("dependent_view"));
        var placements = args.Array("placements").ToList();
        if (placements.Count == 0) return ToolResult.Error("Give at least one placement.");

        var cache = new Dictionary<string, (FamilySymbol, string?)>(StringComparer.OrdinalIgnoreCase);
        var all = placements.Select((p, i) => DetailPlacement.Parse(doc, view, p, i, args.OptString("family"), cache)).ToList();
        // Dependent views: with clip_to_crop, placements outside this view's crop are left out (they would show in other dependents).
        var outside = all.Where(x => x.Error is null && (x.At ?? (x.Start is { } st && x.End is { } en ? (st + en) / 2 : null)) is { } pt && !guard.Keep(pt))
            .Select(x => x.Index).ToList();
        var specs = all.Where(x => !outside.Contains(x.Index)).ToList();
        var target = guard.Target;
        Batch.InGroup(doc, "Place detail components", () => { DetailPlacement.Run(doc, target, specs, method, null); return specs; },
            s => s.Any(x => x.Created is not null));

        var placed = specs.Where(s => s.Created is not null).ToList();
        var errors = new List<object>();
        foreach (var s in specs)
        {
            if (s.Created is null) errors.Add(new { index = s.Index, error = s.Error ?? s.FallbackReason ?? "not placed" });
            foreach (var l in ParamEdits.Problems(s.Params).Where(l => !l.Ok))
                errors.Add(new { index = s.Index, warning = $"'{l.parameter}' {l.status}" + (l.now is null ? "" : $" (now {l.now})") + (l.note is null ? "" : $": {l.note}") });
        }
        if (placed.Count == 0)
            return ToolResult.Error($"No detail components were placed. {JsonSerializer.Serialize(errors.Take(10))}");
        return ToolResult.Ok(new
        {
            view = view.Name,
            placed = placed.Count,
            ids = placed.Select(s => s.Created!.Value),
            errors,
            copied = placed.Count(s => s.Method == "copy") is var c && c > 0 ? c : (int?)null,
            placements = specs.Any(s => s.Method == "copy" || s.Params.Count > 0 || s.TypeNote != null) ? specs.Select(DetailPlacement.Row) : null,
            dependentView = guard.Report(),
            outsideCrop = outside.Count > 0 ? outside : null,
        });
    }
}

public sealed class DimensionElementsTool : IRevitTool
{
    public string Name => "dimension_elements";
    public string Description =>
        "Add one dimension string through the given elements: grids, levels, reference planes, walls (faces or centre " +
        "references), floors/slabs (top and bottom faces for vertical strings) and family instances such as piles, beams, " +
        "columns, doors or furniture (centre reference planes, or with family_refs 'faces' their side planes / outer faces, " +
        "resolved from the family geometry with the instance's position). Works in plans, sections and elevations. The string " +
        "measures along 'direction' (x, y or vertical); by default it is worked out from the elements. For several strings " +
        "at once use add_dimensions.";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "At least two elements (or one, with family_refs faces, for its width)."},
      "direction": {"type": "string", "enum": ["auto", "x", "y", "vertical"]},
      "walls": {"type": "string", "enum": ["exterior_face", "interior_face", "both_faces"], "description": "Wall references to use (default exterior_face)."},
      "family_refs": {"type": "string", "enum": ["center", "faces", "center_and_faces"], "description": "Family instances: centre plane (default), the two outer faces/side planes, or both."},
      "offset": {"type": "number", "description": "Distance from the elements to the dimension line, project units (default ~1 m / 3 ft)."},
      "line": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Optional [[x,y],[x,y]] to place the dimension line exactly (view points, see place_detail_components)."}
    }
    """);
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view is View3D || view is ViewSchedule || view.ViewType == ViewType.DrawingSheet || view.IsTemplate)
            return ToolResult.Error($"dimension_elements works in plan, section, elevation and detail views, not '{view.Name}'.");
        var guard = DependentGuard.For(doc, view, args.OptString("dependent_view"));
        var target = guard.Target;
        var elements = args.LongList("element_ids").Select(i => doc.GetElement(new ElementId(i)) ?? throw new ArgumentException($"No element with id {i}.")).ToList();
        if (elements.Count < 1) return ToolResult.Error("Give at least two element_ids.");
        var offset = args.OptDouble("offset") is { } o ? RevitHelpers.ToInternal(doc, o) : 3.0;
        Line? line = null;
        if (args.Has("line"))
        {
            var pts = args.Array("line").Select(p => ViewPoints.ToModel(doc, target, p)).ToList();
            if (pts.Count != 2) return ToolResult.Error("line needs exactly two points.");
            line = Line.CreateBound(pts[0], pts[1]);
        }
        DimensionPlanner.Plan plan;
        try
        {
            plan = DimensionPlanner.Make(doc, target, elements, args.OptString("direction"), args.OptString("walls") ?? "exterior_face",
                args.OptString("family_refs") ?? "center", offset, line);
        }
        catch (ArgumentException ex) { return ToolResult.Error(ex.Message); }
        if (!guard.Keep(plan.Line.Evaluate(0.5, true)))
            return ToolResult.Error($"The dimension line is outside the crop of dependent view '{view.Name}' (dependent_view: clip_to_crop).");

        var (ids, result) = DimensionBatch.Create(doc, "Dimension elements", [new DimensionBatch.Spec("dimension", target, plan.Line, plan.Refs)]);
        if (ids.Count == 0)
            return ToolResult.Error($"Revit could not make the dimension: {result.Failed.FirstOrDefault()?.Error} The references may not be parallel to each other (a sloped face), " +
                                    $"or not visible in '{target.Name}'. Try direction x/y/vertical, fewer elements, family_refs faces, or walls: exterior_face. Skipped: {JsonSerializer.Serialize(plan.Skipped)}");
        var dim = (Dimension)doc.GetElement(new ElementId(ids[0]));
        return ToolResult.Ok(new
        {
            view = target.Name, dimensionId = dim.Id.Value, references = plan.Refs.Size, segments = Math.Max(1, dim.NumberOfSegments),
            skipped = plan.Skipped, dependentView = guard.Report(),
        });
    }
}

public sealed class CreateToposolidTool : IRevitTool
{
    public string Name => "create_toposolid";
    public string Description =>
        "Create a toposolid (site terrain) from survey points [x, y, z] (z = elevation, project units, measured like level " +
        "elevations) and/or a boundary polygon of [x, y] points. With only a boundary it makes a flat pad at the level " +
        "(plus height_offset). Also converts an old TopographySurface with from_topography_id. Revit 2024 and later.";
    public string PropertiesJson => """
    {
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Survey points [x, y, z]."},
      "boundary": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Outline [x, y] points (closed automatically). Default: the hull of the points' extent."},
      "level": {"type": "string", "description": "Level name (default: the lowest level)."},
      "type_id": {"type": "integer", "description": "Toposolid type (default: the project's default)."},
      "height_offset": {"type": "number", "description": "Flat pad only: height above the level, project units."},
      "from_topography_id": {"type": "integer", "description": "Convert this TopographySurface instead."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var typeId = args.OptLong("type_id") is { } tid ? new ElementId(tid)
            : new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).FirstElementId();
        if (typeId == ElementId.InvalidElementId || doc.GetElement(typeId) is not ToposolidType)
            return ToolResult.Error("No toposolid type found. Pass type_id from list_types (category Toposolid).");
        var level = args.OptString("level") is { } ln ? RevitHelpers.FindLevel(doc, ln)
            : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault()
              ?? throw new InvalidOperationException("The model has no levels.");

        using var t = RevitHelpers.StartTransaction(doc, "Create toposolid");
        Toposolid topo;
        if (args.OptLong("from_topography_id") is { } fromId)
        {
            if (doc.GetElement(new ElementId(fromId)) is not Autodesk.Revit.DB.Architecture.TopographySurface)
                return ToolResult.Error($"{fromId} is not a topography surface.");
            topo = Toposolid.CreateFromTopographySurface(doc, new ElementId(fromId), typeId, level.Id);
        }
        else
        {
            var pts = args.Array("points").Select(p =>
            {
                var v = p.EnumerateArray().Select(e => RevitHelpers.ToInternal(doc, e.GetDouble())).ToArray();
                if (v.Length < 3) throw new ArgumentException("Survey points need [x, y, z].");
                // z is read like a level elevation (Revit's elevation base); geometry needs internal coordinates.
                return new XYZ(v[0], v[1], v[2] - (level.Elevation - level.ProjectElevation));
            }).ToList();
            var boundary = args.Array("boundary").Select(p => Modeling.Point(doc, p, level.ProjectElevation)).ToList();
            if (boundary.Count > 1 && boundary[0].IsAlmostEqualTo(boundary[boundary.Count - 1])) boundary.RemoveAt(boundary.Count - 1);

            if (boundary.Count == 0 && pts.Count < 3) return ToolResult.Error("Give at least 3 survey points, or a boundary.");
            if (boundary.Count is > 0 and < 3) return ToolResult.Error("A boundary needs at least 3 points.");

            if (boundary.Count == 0)
                topo = Toposolid.Create(doc, pts, typeId, level.Id);
            else
            {
                var loop = new CurveLoop();
                for (var i = 0; i < boundary.Count; i++)
                    loop.Append(Line.CreateBound(boundary[i], boundary[(i + 1) % boundary.Count]));
                topo = pts.Count > 0
                    ? Toposolid.Create(doc, [loop], pts, typeId, level.Id)
                    : Toposolid.Create(doc, [loop], typeId, level.Id);
                if (pts.Count == 0 && args.OptDouble("height_offset") is { } h)
                    topo.get_Parameter(BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM)?.Set(RevitHelpers.ToInternal(doc, h));
            }
        }
        t.Commit();
        var bb = topo.get_BoundingBox(null);
        return ToolResult.Ok(new
        {
            id = topo.Id.Value, level = level.Name, type = doc.GetElement(typeId).Name,
            lowest = bb is null ? (double?)null : Math.Round(RevitHelpers.FromInternal(doc, bb.Min.Z), 3),
            highest = bb is null ? (double?)null : Math.Round(RevitHelpers.FromInternal(doc, bb.Max.Z), 3),
            units = RevitHelpers.LengthUnitLabel(doc),
        });
    }
}
