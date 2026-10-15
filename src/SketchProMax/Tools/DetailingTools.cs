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
        return TypeMatch.Find(symbols, name, what);
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
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "element_ids": {"type": "array", "items": {"type": "integer"}},
      "category": {"type": "string", "description": "Used when element_ids is not given, e.g. Walls, Doors, Detail Items."},
      "tag_type": {"type": "string", "description": "Keynote tag 'Family: Type' (default: first loaded keynote tag)."},
      "leader": {"type": "boolean", "description": "Default true."},
      "offset": {"type": "array", "items": {"type": "number"}, "description": "[dx, dy] from the element centre to the tag head, project units (default ~1 m / 3 ft right and up)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view.IsTemplate || view is ViewSchedule || view.ViewType == ViewType.DrawingSheet)
            return ToolResult.Error($"'{view.Name}' can't hold keynote tags. Use a plan, section, elevation, detail or 3D view.");
        var tagType = ViewPoints.Symbol(doc, BuiltInCategory.OST_KeynoteTags, args.OptString("tag_type"), "keynote tag");

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
            try
            {
                var tag = IndependentTag.Create(doc, tagType.Id, view.Id, new Reference(e), leader, TagOrientation.Horizontal, center);
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
        "measured from the view origin in other views; [x, y, z] is a model point. Use list_types category 'Detail Items' to find types.";
    public string PropertiesJson => """
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
      }}}
    }
    """;
    public string[] Required => ["placements"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view.IsTemplate || view is View3D || view is ViewSchedule || view.ViewType == ViewType.DrawingSheet)
            return ToolResult.Error($"Detail components go in plan, section, elevation, drafting or detail views, not '{view.Name}' ({view.ViewType}).");
        var defaultName = args.OptString("family");
        var placements = args.Array("placements").ToList();
        if (placements.Count == 0) return ToolResult.Error("Give at least one placement.");

        var created = new List<long>();
        var errors = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Place detail components");
        var cache = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < placements.Count; i++)
        {
            var p = placements[i];
            try
            {
                var name = p.TryGetProperty("family", out var f) ? f.GetString() : defaultName;
                var key = name ?? "";
                if (!cache.TryGetValue(key, out var symbol))
                {
                    symbol = cache[key] = ViewPoints.Symbol(doc, BuiltInCategory.OST_DetailComponents, name, "detail item");
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                }
                FamilyInstance inst;
                var lineBased = symbol.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail;
                if (lineBased)
                {
                    if (!p.TryGetProperty("start", out var s) || !p.TryGetProperty("end", out var e))
                        throw new ArgumentException($"'{symbol.FamilyName}' is line-based: give start and end.");
                    var a = ViewPoints.ToModel(doc, view, s);
                    var b = ViewPoints.ToModel(doc, view, e);
                    if (a.DistanceTo(b) < doc.Application.ShortCurveTolerance) throw new ArgumentException("start and end are the same point.");
                    inst = doc.Create.NewFamilyInstance(Line.CreateBound(a, b), symbol, view);
                }
                else
                {
                    var atSpec = p.TryGetProperty("at", out var at) ? at : p.TryGetProperty("start", out var st) ? st
                        : throw new ArgumentException("Give 'at' for a point-based detail item.");
                    var point = ViewPoints.ToModel(doc, view, atSpec);
                    inst = doc.Create.NewFamilyInstance(point, symbol, view);
                    if (p.TryGetProperty("rotation_degrees", out var r) && Math.Abs(r.GetDouble()) > 1e-9)
                        ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateUnbound(point, view.ViewDirection), r.GetDouble() * Math.PI / 180);
                }
                if (p.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Object)
                    foreach (var kv in ps.EnumerateObject())
                    {
                        var prm = inst.LookupParameter(kv.Name);
                        if (prm is null || prm.IsReadOnly) { errors.Add(new { index = i, warning = $"no editable parameter '{kv.Name}'" }); continue; }
                        if (!SetParametersTool.Write(prm, kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.GetRawText())) errors.Add(new { index = i, warning = $"'{kv.Name}' rejected the value" });
                    }
                created.Add(inst.Id.Value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                errors.Add(new { index = i, error = ex.Message.Trim() });
            }
        }
        if (created.Count == 0)
        {
            t.RollBack();
            return ToolResult.Error($"No detail components were placed. {JsonSerializer.Serialize(errors.Take(10))}");
        }
        t.Commit();
        return ToolResult.Ok(new { view = view.Name, placed = created.Count, ids = created, errors });
    }
}

public sealed class DimensionElementsTool : IRevitTool
{
    public string Name => "dimension_elements";
    public string Description =>
        "Add one dimension string through the given elements: grids, levels, reference planes, walls (faces or centre " +
        "references) and family instances such as columns, doors or furniture (their centre reference planes). Works in " +
        "plans, sections and elevations. The string measures along 'direction' (x, y or vertical); by default it is " +
        "worked out from the elements (across parallel grids/walls/planes, vertical for levels, else along their widest spread).";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "At least two elements."},
      "direction": {"type": "string", "enum": ["auto", "x", "y", "vertical"]},
      "walls": {"type": "string", "enum": ["exterior_face", "interior_face", "both_faces"], "description": "Wall references to use (default exterior_face)."},
      "offset": {"type": "number", "description": "Distance from the elements to the dimension line, project units (default ~1 m / 3 ft)."},
      "line": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Optional [[x,y],[x,y]] to place the dimension line exactly (view points, see place_detail_components)."}
    }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view is View3D || view is ViewSchedule || view.ViewType == ViewType.DrawingSheet || view.IsTemplate)
            return ToolResult.Error($"dimension_elements works in plan, section, elevation and detail views, not '{view.Name}'.");
        var elements = args.LongList("element_ids").Select(i => doc.GetElement(new ElementId(i)) ?? throw new ArgumentException($"No element with id {i}.")).ToList();
        if (elements.Count < 2) return ToolResult.Error("Give at least two element_ids.");
        var wallMode = args.OptString("walls") ?? "exterior_face";
        var offset = args.OptDouble("offset") is { } o ? RevitHelpers.ToInternal(doc, o) : 3.0;
        var vd = view.ViewDirection.Normalize();

        // Measuring direction, in the view plane.
        XYZ? m = (args.OptString("direction") ?? "auto") switch
        {
            "x" => XYZ.BasisX,
            "y" => XYZ.BasisY,
            "vertical" => XYZ.BasisZ,
            _ => null,
        };
        m ??= AutoDirection(elements, view);
        m = (m - vd * m.DotProduct(vd));
        if (m.GetLength() < 1e-6)
            return ToolResult.Error($"That direction points straight out of '{view.Name}'. Pick another direction or a view that shows it.");
        m = m.Normalize();

        var refs = new ReferenceArray();
        var points = new List<XYZ>();
        var skipped = new List<object>();
        foreach (var e in elements)
        {
            var found = References(e, m, wallMode);
            if (found.Count == 0) { skipped.Add(new { id = e.Id.Value, reason = $"no reference across this direction ({e.Category?.Name ?? e.GetType().Name})" }); continue; }
            foreach (var r in found) refs.Append(r);
            var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
            if (bb != null) { points.Add(bb.Min); points.Add(bb.Max); }
        }
        if (refs.Size < 2) return ToolResult.Error($"Only {refs.Size} usable reference(s) found; a dimension needs two. Skipped: {JsonSerializer.Serialize(skipped)}");

        Line line;
        if (args.Has("line"))
        {
            var pts = args.Array("line").Select(p => ViewPoints.ToModel(doc, view, p)).ToList();
            if (pts.Count != 2) return ToolResult.Error("line needs exactly two points.");
            line = Line.CreateBound(pts[0], pts[1]);
        }
        else
        {
            if (points.Count == 0) return ToolResult.Error("Couldn't find where the elements are in this view. Give 'line'.");
            var perp = vd.CrossProduct(m).Normalize();
            var along = points.Select(p => p.DotProduct(m)).ToList();
            var position = points.Max(p => p.DotProduct(perp)) + offset;
            var depth = view is ViewPlan plan ? (plan.GenLevel?.ProjectElevation ?? 0) : view.Origin.DotProduct(vd);
            XYZ At(double s) => m * s + perp * position + vd * depth;
            // On plans, vd is up, so "depth" is the level elevation.
            line = Line.CreateBound(At(along.Min() - 1), At(along.Max() + 1));
        }

        using var t = RevitHelpers.StartTransaction(doc, "Dimension elements");
        try
        {
            var dim = doc.Create.NewDimension(view, line, refs);
            t.Commit();
            return ToolResult.Ok(new { view = view.Name, dimensionId = dim.Id.Value, references = refs.Size, segments = Math.Max(1, dim.NumberOfSegments), skipped });
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            t.RollBack();
            return ToolResult.Error($"Revit could not make the dimension: {ex.Message.Trim()} The references may not be parallel to each other or not visible in '{view.Name}'. Try direction x/y/vertical, fewer elements, or walls: exterior_face.");
        }
    }

    private static XYZ AutoDirection(List<Element> elements, View view)
    {
        foreach (var e in elements)
        {
            switch (e)
            {
                case Grid { Curve: Line gl }: return new XYZ(-gl.Direction.Y, gl.Direction.X, 0);
                case Wall { Location: LocationCurve { Curve: Line wl } }: return new XYZ(-wl.Direction.Y, wl.Direction.X, 0);
                case ReferencePlane rp: return rp.Normal;
                case Level: return XYZ.BasisZ;
            }
        }
        var centers = elements.Select(e => e.get_BoundingBox(view) ?? e.get_BoundingBox(null)).Where(b => b != null).Select(b => (b!.Min + b.Max) / 2).ToList();
        if (centers.Count < 2) return view.RightDirection;
        var right = view.RightDirection; var up = view.UpDirection;
        double spread(XYZ d) => centers.Max(c => c.DotProduct(d)) - centers.Min(c => c.DotProduct(d));
        return spread(right) >= spread(up) ? right : up;
    }

    private static List<Reference> References(Element e, XYZ m, string wallMode)
    {
        bool Across(XYZ normal) => Math.Abs(normal.Normalize().DotProduct(m)) > 0.99;
        switch (e)
        {
            case Grid g when g.Curve is Line l:
                return Across(new XYZ(-l.Direction.Y, l.Direction.X, 0)) ? [new Reference(g)] : [];
            case Level lv:
                return Across(XYZ.BasisZ) ? [new Reference(lv)] : [];
            case ReferencePlane rp:
                return Across(rp.Normal) ? [rp.GetReference()] : [];
            case Wall w when w.Location is LocationCurve { Curve: Line wl }:
                if (!Across(new XYZ(-wl.Direction.Y, wl.Direction.X, 0))) return [];
                var list = new List<Reference>();
                if (wallMode is "exterior_face" or "both_faces" && DimensionWallsTool.WallReference(w, "exterior_face") is { } ext) list.Add(ext);
                if (wallMode is "interior_face" or "both_faces" && DimensionWallsTool.WallReference(w, "interior_face") is { } inn) list.Add(inn);
                return list;
            case FamilyInstance fi:
                // Centre (left/right) plane is perpendicular to the hand direction; (front/back) to the facing direction.
                var hand = fi.HandOrientation; var facing = fi.FacingOrientation;
                FamilyInstanceReferenceType type =
                    Across(XYZ.BasisZ) && Math.Abs(m.Z) > 0.99 ? FamilyInstanceReferenceType.CenterElevation
                    : Math.Abs(hand.DotProduct(m)) >= Math.Abs(facing.DotProduct(m)) ? FamilyInstanceReferenceType.CenterLeftRight
                    : FamilyInstanceReferenceType.CenterFrontBack;
                var refs = fi.GetReferences(type);
                return refs.Count > 0 ? [refs[0]] : [];
            default:
                return [];
        }
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
