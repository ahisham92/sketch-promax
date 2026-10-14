using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Shared geometry/type helpers for modeling tools. Inputs are in project length units.</summary>
internal static class Modeling
{
    public static XYZ Point(Document doc, JsonElement arr, double z = 0)
    {
        var v = arr.EnumerateArray().Select(e => RevitHelpers.ToInternal(doc, e.GetDouble())).ToArray();
        return new XYZ(v[0], v[1], v.Length > 2 ? v[2] : z);
    }

    /// <summary>Closed loop from a polygon of [x,y] points (last point need not repeat the first).</summary>
    public static CurveLoop Polygon(Document doc, JsonElement points, double z)
    {
        var pts = points.EnumerateArray().Select(p => Point(doc, p, z)).ToList();
        if (pts.Count < 3) throw new ArgumentException("A boundary needs at least 3 points.");
        var loop = new CurveLoop();
        for (int i = 0; i < pts.Count; i++)
            loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
        return loop;
    }

    /// <summary>Outer boundary of a placed room, at its finish faces.</summary>
    public static CurveLoop RoomLoop(Room room)
    {
        var segments = room.GetBoundarySegments(new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        });
        if (segments is null || segments.Count == 0) throw new ArgumentException($"Room {room.Number} has no boundary (not placed or not enclosed).");
        var loop = new CurveLoop();
        foreach (var s in segments[0]) loop.Append(s.GetCurve());
        return loop;
    }

    /// <summary>
    /// Boundary from either "room_id" or "points" in a spec object. Returns the level to use
    /// (room's level, the spec's level, or the lowest level).
    /// </summary>
    public static (CurveLoop loop, Level level) Boundary(Document doc, JsonElement spec)
    {
        if (spec.TryGetProperty("room_id", out var rid))
        {
            var room = doc.GetElement(new ElementId(rid.GetInt64())) as Room ?? throw new ArgumentException("room_id is not a room.");
            return (RoomLoop(room), room.Level);
        }
        var level = RevitHelpers.FindLevel(doc, spec.TryGetProperty("level", out var l) ? l.GetString() : null);
        if (!spec.TryGetProperty("points", out var pts)) throw new ArgumentException("Give either room_id or points.");
        return (Polygon(doc, pts, level.Elevation), level);
    }

    public static ElementId TypeId(Document doc, JsonElement spec, ElementTypeGroup fallback)
    {
        if (spec.TryGetProperty("type_id", out var t)) return new ElementId(t.GetInt64());
        return doc.GetDefaultElementTypeId(fallback);
    }

    public static FamilySymbol Symbol(Document doc, long typeId)
    {
        var symbol = doc.GetElement(new ElementId(typeId)) as FamilySymbol
                     ?? throw new ArgumentException($"{typeId} is not a loadable family type. Use list_types to find one.");
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        return symbol;
    }

    /// <summary>2D intersections of all pairs of straight grids.</summary>
    public static List<(XYZ point, string a, string b)> GridIntersections(Document doc)
    {
        var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line).ToList();
        var result = new List<(XYZ, string, string)>();
        for (int i = 0; i < grids.Count; i++)
            for (int j = i + 1; j < grids.Count; j++)
            {
                if (Intersect2D((Line)grids[i].Curve, (Line)grids[j].Curve) is { } p)
                    result.Add((p, grids[i].Name, grids[j].Name));
            }
        return result;
    }

    /// <summary>Plan intersection of two bounded lines (ignores Z); null if parallel or not crossing.</summary>
    private static XYZ? Intersect2D(Line l1, Line l2)
    {
        XYZ p = l1.GetEndPoint(0), r = l1.GetEndPoint(1) - p, q = l2.GetEndPoint(0), s = l2.GetEndPoint(1) - q;
        double cross = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(cross) < 1e-9) return null;
        double t = ((q.X - p.X) * s.Y - (q.Y - p.Y) * s.X) / cross;
        double u = ((q.X - p.X) * r.Y - (q.Y - p.Y) * r.X) / cross;
        const double eps = 1e-6;
        if (t < -eps || t > 1 + eps || u < -eps || u > 1 + eps) return null;
        return new XYZ(p.X + t * r.X, p.Y + t * r.Y, 0);
    }
}

public sealed class CreateLevelsTool : IRevitTool
{
    public string Name => "create_levels";
    public string Description =>
        "Create levels at given elevations (project units), optionally with a floor plan and ceiling plan for each.";
    public string PropertiesJson => """
    {
      "levels": {"type": "array", "items": {"type": "object",
        "properties": {"name": {"type": "string"}, "elevation": {"type": "number"}}, "required": ["name", "elevation"]}},
      "create_plans": {"type": "boolean", "description": "Also create floor and ceiling plans (default true)."}
    }
    """;
    public string[] Required => ["levels"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var plans = args.OptBool("create_plans", true);
        ViewFamilyType? Vft(ViewFamily f) => new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == f);
        var floorVft = Vft(ViewFamily.FloorPlan);
        var ceilingVft = Vft(ViewFamily.CeilingPlan);

        var created = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Create levels");
        foreach (var spec in args.Array("levels"))
        {
            var level = Level.Create(doc, RevitHelpers.ToInternal(doc, spec.GetProperty("elevation").GetDouble()));
            try { level.Name = spec.GetProperty("name").GetString()!; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            long? floorPlan = null, ceilingPlan = null;
            if (plans)
            {
                if (floorVft != null) floorPlan = ViewPlan.Create(doc, floorVft.Id, level.Id).Id.Value;
                if (ceilingVft != null) ceilingPlan = ViewPlan.Create(doc, ceilingVft.Id, level.Id).Id.Value;
            }
            created.Add(new { id = level.Id.Value, name = level.Name, floorPlanId = floorPlan, ceilingPlanId = ceilingPlan });
        }
        t.Commit();
        return ToolResult.Ok(new { created });
    }
}

public sealed class CreateGridsTool : IRevitTool
{
    public string Name => "create_grids";
    public string Description =>
        "Create grids. Either give explicit lines, or a rectangular layout: spacings along X (numbered 1,2,3...) " +
        "and along Y (lettered A,B,C...), measured from an origin. Distances in project units.";
    public string PropertiesJson => """
    {
      "lines": {"type": "array", "items": {"type": "object", "properties": {
        "name": {"type": "string"}, "start": {"type": "array", "items": {"type": "number"}}, "end": {"type": "array", "items": {"type": "number"}}},
        "required": ["start", "end"]}},
      "rectangular": {"type": "object", "properties": {
        "origin": {"type": "array", "items": {"type": "number"}},
        "x_spacings": {"type": "array", "items": {"type": "number"}, "description": "Distances between consecutive vertical grids."},
        "y_spacings": {"type": "array", "items": {"type": "number"}, "description": "Distances between consecutive horizontal grids."},
        "extension": {"type": "number", "description": "How far grids run past the outer grids."},
        "first_number": {"type": "integer"}, "first_letter": {"type": "string"}}}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var created = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Create grids");
        // Grids should show in the plans of every level, so stretch them from below the lowest to above the highest.
        var elevations = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation).DefaultIfEmpty(0).ToList();
        double bottom = elevations.Min() - 10, top = elevations.Max() + 30;

        void Make(XYZ a, XYZ b, string? name)
        {
            var g = Grid.Create(doc, Line.CreateBound(new XYZ(a.X, a.Y, 0), new XYZ(b.X, b.Y, 0)));
            try { g.SetVerticalExtents(bottom, top); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            if (name != null) { try { g.Name = name; } catch (Autodesk.Revit.Exceptions.ArgumentException) { } }
            created.Add(new { id = g.Id.Value, name = g.Name });
        }

        foreach (var l in args.Array("lines"))
            Make(Modeling.Point(doc, l.GetProperty("start")), Modeling.Point(doc, l.GetProperty("end")),
                 l.TryGetProperty("name", out var n) ? n.GetString() : null);

        if (args.Has("rectangular"))
        {
            var r = args.Raw["rectangular"];
            var origin = r.TryGetProperty("origin", out var o) ? Modeling.Point(doc, o) : XYZ.Zero;
            double Conv(JsonElement e) => RevitHelpers.ToInternal(doc, e.GetDouble());
            var xs = Cumulative(r.TryGetProperty("x_spacings", out var xsp) ? xsp.EnumerateArray().Select(Conv) : []);
            var ys = Cumulative(r.TryGetProperty("y_spacings", out var ysp) ? ysp.EnumerateArray().Select(Conv) : []);
            var ext = r.TryGetProperty("extension", out var e) ? Conv(e) : 5.0;
            int number = r.TryGetProperty("first_number", out var fn) ? fn.GetInt32() : 1;
            char letter = r.TryGetProperty("first_letter", out var fl) && fl.GetString() is { Length: > 0 } s ? s[0] : 'A';
            double xMax = xs.Last(), yMax = ys.Last();

            foreach (var x in xs)
                Make(origin + new XYZ(x, -ext, 0), origin + new XYZ(x, yMax + ext, 0), (number++).ToString());
            foreach (var y in ys)
            {
                Make(origin + new XYZ(-ext, y, 0), origin + new XYZ(xMax + ext, y, 0), letter.ToString());
                letter++;
                if (letter is 'I' or 'O') letter++; // skip letters easily confused with 1 and 0
            }
        }
        t.Commit();
        return ToolResult.Ok(new { created });
    }

    private static List<double> Cumulative(IEnumerable<double> spacings)
    {
        var list = new List<double> { 0 };
        foreach (var s in spacings) list.Add(list[list.Count - 1] + s);
        return list;
    }
}

public sealed class CreateFloorsTool : IRevitTool
{
    public string Name => "create_floors";
    public string Description =>
        "Create floors (slabs) from room boundaries or from polygons of [x,y] points. One floor per item.";
    public string PropertiesJson => """
    {
      "floors": {"type": "array", "items": {"type": "object", "properties": {
        "room_id": {"type": "integer", "description": "Use this room's boundary and level."},
        "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}},
        "level": {"type": "string"},
        "type_id": {"type": "integer", "description": "Floor type (list_types 'Floors'). Default: model default."},
        "offset": {"type": "number", "description": "Height offset from level, project units."},
        "structural": {"type": "boolean"}}}}
    }
    """;
    public string[] Required => ["floors"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var created = new List<long>();
        var errors = new List<string>();
        using var t = RevitHelpers.StartTransaction(doc, "Create floors");
        foreach (var spec in args.Array("floors"))
        {
            try
            {
                var (loop, level) = Modeling.Boundary(doc, spec);
                var floor = Floor.Create(doc, [loop], Modeling.TypeId(doc, spec, ElementTypeGroup.FloorType), level.Id);
                if (spec.TryGetProperty("offset", out var off))
                    floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(RevitHelpers.ToInternal(doc, off.GetDouble()));
                if (spec.TryGetProperty("structural", out var st))
                    floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(st.GetBoolean() ? 1 : 0);
                created.Add(floor.Id.Value);
            }
            catch (Exception ex) when (ex is ArgumentException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                errors.Add(ex.Message);
            }
        }
        t.Commit();
        return ToolResult.Ok(new { created, errors });
    }
}

public sealed class CreateCeilingsTool : IRevitTool
{
    public string Name => "create_ceilings";
    public string Description => "Create ceilings from room boundaries or polygons, at a height above the level.";
    public string PropertiesJson => """
    {
      "ceilings": {"type": "array", "items": {"type": "object", "properties": {
        "room_id": {"type": "integer"},
        "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}},
        "level": {"type": "string"},
        "type_id": {"type": "integer"},
        "height": {"type": "number", "description": "Height above level, project units."}}}}
    }
    """;
    public string[] Required => ["ceilings"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var defaultType = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).FirstElementId();
        var created = new List<long>();
        var errors = new List<string>();
        using var t = RevitHelpers.StartTransaction(doc, "Create ceilings");
        foreach (var spec in args.Array("ceilings"))
        {
            try
            {
                var (loop, level) = Modeling.Boundary(doc, spec);
                var typeId = spec.TryGetProperty("type_id", out var tid) ? new ElementId(tid.GetInt64()) : defaultType;
                var ceiling = Ceiling.Create(doc, [loop], typeId, level.Id);
                if (spec.TryGetProperty("height", out var h))
                    ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(RevitHelpers.ToInternal(doc, h.GetDouble()));
                created.Add(ceiling.Id.Value);
            }
            catch (Exception ex) when (ex is ArgumentException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                errors.Add(ex.Message);
            }
        }
        t.Commit();
        return ToolResult.Ok(new { created, errors });
    }
}

public sealed class CreateRoofTool : IRevitTool
{
    public string Name => "create_roof";
    public string Description =>
        "Create a footprint roof from a polygon of [x,y] points on a level. slope_degrees 0 gives a flat roof; " +
        "otherwise every edge slopes (hip roof). Optional overhang beyond the footprint.";
    public string PropertiesJson => """
    {
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}},
      "level": {"type": "string"},
      "type_id": {"type": "integer"},
      "slope_degrees": {"type": "number"},
      "overhang": {"type": "number", "description": "Project units."},
      "gable_edges": {"type": "array", "items": {"type": "integer"}, "description": "0-based edge indexes that should NOT slope (gable ends)."}
    }
    """;
    public string[] Required => ["points", "level"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var level = RevitHelpers.FindLevel(doc, args.String("level"));
        var roofType = args.OptLong("type_id") is { } tid
            ? doc.GetElement(new ElementId(tid)) as RoofType ?? throw new ArgumentException($"{tid} is not a roof type. Use list_types 'Roofs'.")
            : DefaultRoofType(doc);
        var loop = Modeling.Polygon(doc, args.Raw["points"], level.Elevation);
        var footprint = new CurveArray();
        foreach (var c in loop) footprint.Append(c);

        var slope = args.OptDouble("slope_degrees") ?? 0;
        var overhang = args.OptDouble("overhang") is { } o ? RevitHelpers.ToInternal(doc, o) : 0;
        var gables = args.LongList("gable_edges").Select(i => (int)i).ToHashSet();

        using var t = RevitHelpers.StartTransaction(doc, "Create roof");
        var roof = doc.Create.NewFootPrintRoof(footprint, level, roofType, out var edges)
                   ?? throw new InvalidOperationException("Revit did not create the roof. Check that the outline is closed and does not cross itself.");
        int index = 0;
        foreach (ModelCurve edge in edges ?? new ModelCurveArray())
        {
            var sloped = slope > 0 && !gables.Contains(index++);
            roof.set_DefinesSlope(edge, sloped);
            if (sloped) roof.set_SlopeAngle(edge, Math.Tan(slope * Math.PI / 180));
            if (overhang > 0) roof.set_Overhang(edge, overhang);
        }
        t.Commit();
        return ToolResult.Ok(new { roofId = roof.Id.Value, type = roofType.Name });
    }

    /// <summary>The model's default roof type, else the first basic roof (not sloped glazing).</summary>
    private static RoofType DefaultRoofType(Document doc)
    {
        if (doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.RoofType)) is RoofType byDefault) return byDefault;
        var types = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().ToList();
        return types.FirstOrDefault(t => t.GetCompoundStructure() is not null) ?? types.FirstOrDefault()
               ?? throw new InvalidOperationException("The model has no roof types. Load or transfer one first.");
    }
}

public sealed class PlaceHostedFamiliesTool : IRevitTool
{
    public string Name => "place_doors_windows";
    public string Description =>
        "Insert doors or windows (or any wall-hosted family) into walls. Position is the distance from the wall's " +
        "start point along its length, in project units (use query_elements/get_element_parameters to read wall length). " +
        "Windows take an optional sill height.";
    public string PropertiesJson => """
    {
      "items": {"type": "array", "items": {"type": "object", "properties": {
        "wall_id": {"type": "integer"},
        "type_id": {"type": "integer", "description": "Door/window family type id (list_types 'Doors' or 'Windows')."},
        "distance_along_wall": {"type": "number"},
        "sill_height": {"type": "number"},
        "flip_facing": {"type": "boolean"}, "flip_hand": {"type": "boolean"}},
        "required": ["wall_id", "type_id", "distance_along_wall"]}}
    }
    """;
    public string[] Required => ["items"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var created = new List<long>();
        var errors = new List<string>();
        using var t = RevitHelpers.StartTransaction(doc, "Place doors and windows");
        foreach (var spec in args.Array("items"))
        {
            try
            {
                var wall = doc.GetElement(new ElementId(spec.GetProperty("wall_id").GetInt64())) as Wall
                           ?? throw new ArgumentException("wall_id is not a wall.");
                var curve = ((LocationCurve)wall.Location).Curve;
                var d = RevitHelpers.ToInternal(doc, spec.GetProperty("distance_along_wall").GetDouble());
                if (d < 0 || d > curve.Length) throw new ArgumentException($"Distance is outside wall {wall.Id.Value} (length {RevitHelpers.FromInternal(doc, curve.Length)}).");
                var point = curve.Evaluate(d / curve.Length, true);
                var level = (Level)doc.GetElement(wall.LevelId);
                var symbol = Modeling.Symbol(doc, spec.GetProperty("type_id").GetInt64());

                var fi = doc.Create.NewFamilyInstance(point, symbol, wall, level, StructuralType.NonStructural);
                if (spec.TryGetProperty("sill_height", out var sill))
                    fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(RevitHelpers.ToInternal(doc, sill.GetDouble()));
                if (spec.TryGetProperty("flip_facing", out var ff) && ff.GetBoolean()) fi.flipFacing();
                if (spec.TryGetProperty("flip_hand", out var fh) && fh.GetBoolean()) fi.flipHand();
                created.Add(fi.Id.Value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidCastException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                errors.Add(ex.Message);
            }
        }
        t.Commit();
        return ToolResult.Ok(new { created, errors });
    }
}

public sealed class PlaceFamilyInstancesTool : IRevitTool
{
    public string Name => "place_family_instances";
    public string Description =>
        "Place any loaded family type (furniture, equipment, fixtures, lighting, casework, site, generic models...) " +
        "at points on a level, with optional rotation in degrees.";
    public string PropertiesJson => """
    {
      "type_id": {"type": "integer"},
      "level": {"type": "string"},
      "placements": {"type": "array", "items": {"type": "object", "properties": {
        "point": {"type": "array", "items": {"type": "number"}, "description": "[x, y] or [x, y, z offset]"},
        "rotation_degrees": {"type": "number"}}, "required": ["point"]}}
    }
    """;
    public string[] Required => ["type_id", "placements"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var level = RevitHelpers.FindLevel(doc, args.OptString("level"));
        var symbol = Modeling.Symbol(doc, args.OptLong("type_id") ?? -1);
        var created = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, $"Place {symbol.FamilyName}");
        foreach (var p in args.Array("placements"))
        {
            var pt = Modeling.Point(doc, p.GetProperty("point"));
            var fi = doc.Create.NewFamilyInstance(new XYZ(pt.X, pt.Y, level.Elevation + pt.Z), symbol, level, StructuralType.NonStructural);
            if (p.TryGetProperty("rotation_degrees", out var r) && r.GetDouble() != 0)
            {
                var axis = Line.CreateBound(new XYZ(pt.X, pt.Y, 0), new XYZ(pt.X, pt.Y, 1));
                ElementTransformUtils.RotateElement(doc, fi.Id, axis, r.GetDouble() * Math.PI / 180);
            }
            created.Add(fi.Id.Value);
        }
        t.Commit();
        return ToolResult.Ok(new { created });
    }
}

public sealed class PlaceColumnsTool : IRevitTool
{
    public string Name => "place_columns";
    public string Description =>
        "Place structural (or architectural) columns from a base level to a top level, at given points or at every " +
        "grid intersection. Use list_types 'Structural Columns' or 'Columns' for type ids.";
    public string PropertiesJson => """
    {
      "type_id": {"type": "integer"},
      "base_level": {"type": "string"},
      "top_level": {"type": "string"},
      "at_grid_intersections": {"type": "boolean"},
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}},
      "architectural": {"type": "boolean", "description": "Place as non-structural columns."}
    }
    """;
    public string[] Required => ["type_id", "base_level", "top_level"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var baseLevel = RevitHelpers.FindLevel(doc, args.String("base_level"));
        var topLevel = RevitHelpers.FindLevel(doc, args.String("top_level"));
        var symbol = Modeling.Symbol(doc, args.OptLong("type_id") ?? -1);
        var kind = args.OptBool("architectural") ? StructuralType.NonStructural : StructuralType.Column;

        var points = args.Array("points").Select(p => Modeling.Point(doc, p)).ToList();
        if (args.OptBool("at_grid_intersections"))
            points.AddRange(Modeling.GridIntersections(doc).Select(i => i.point));
        if (points.Count == 0) return ToolResult.Error("No points given and no grid intersections found.");

        var created = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Place columns");
        foreach (var p in points)
        {
            var fi = doc.Create.NewFamilyInstance(new XYZ(p.X, p.Y, baseLevel.Elevation), symbol, baseLevel, kind);
            fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(topLevel.Id);
            fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(0.0);
            created.Add(fi.Id.Value);
        }
        t.Commit();
        return ToolResult.Ok(new { created = created.Count, ids = created });
    }
}

public sealed class PlaceBeamsTool : IRevitTool
{
    public string Name => "place_beams";
    public string Description =>
        "Place structural beams (framing) on a level, either along given lines or along every grid line between " +
        "consecutive grid intersections. Use list_types 'Structural Framing' for type ids.";
    public string PropertiesJson => """
    {
      "type_id": {"type": "integer"},
      "level": {"type": "string"},
      "along_grids": {"type": "boolean"},
      "lines": {"type": "array", "items": {"type": "object", "properties": {
        "start": {"type": "array", "items": {"type": "number"}}, "end": {"type": "array", "items": {"type": "number"}}},
        "required": ["start", "end"]}}
    }
    """;
    public string[] Required => ["type_id", "level"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var level = RevitHelpers.FindLevel(doc, args.String("level"));
        var symbol = Modeling.Symbol(doc, args.OptLong("type_id") ?? -1);
        var z = level.Elevation;

        var segments = args.Array("lines")
            .Select(l => (Modeling.Point(doc, l.GetProperty("start"), z), Modeling.Point(doc, l.GetProperty("end"), z)))
            .ToList();

        if (args.OptBool("along_grids"))
        {
            // For each grid, connect consecutive intersections along it.
            var hits = Modeling.GridIntersections(doc);
            foreach (var gridName in hits.SelectMany(h => new[] { h.a, h.b }).Distinct())
            {
                var onGrid = hits.Where(h => h.a == gridName || h.b == gridName).Select(h => h.point).ToList();
                if (onGrid.Count < 2) continue;
                var dir = (onGrid.Last() - onGrid.First()).Normalize();
                var ordered = onGrid.OrderBy(p => p.DotProduct(dir)).ToList();
                for (int i = 0; i + 1 < ordered.Count; i++)
                    segments.Add((new XYZ(ordered[i].X, ordered[i].Y, z), new XYZ(ordered[i + 1].X, ordered[i + 1].Y, z)));
            }
        }
        if (segments.Count == 0) return ToolResult.Error("No lines given and no grid intersections found.");

        var created = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Place beams");
        foreach (var (a, b) in segments)
        {
            if (a.DistanceTo(b) < doc.Application.ShortCurveTolerance) continue;
            created.Add(doc.Create.NewFamilyInstance(Line.CreateBound(a, b), symbol, level, StructuralType.Beam).Id.Value);
        }
        t.Commit();
        return ToolResult.Ok(new { created = created.Count, ids = created });
    }
}

public sealed class TransformElementsTool : IRevitTool
{
    public string Name => "move_copy_rotate";
    public string Description =>
        "Move or copy elements by an offset [dx, dy, dz] (project units), and/or rotate them about a vertical axis " +
        "through a point. With copy=true the originals stay and the copies are returned.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}},
      "offset": {"type": "array", "items": {"type": "number"}},
      "copy": {"type": "boolean"},
      "rotation_degrees": {"type": "number"},
      "rotation_center": {"type": "array", "items": {"type": "number"}, "description": "[x, y]; default: center of the elements."}
    }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        ICollection<ElementId> ids = args.LongList("element_ids").Select(i => new ElementId(i)).Where(id => doc.GetElement(id) != null).ToList();
        var offset = args.Has("offset") ? Modeling.Point(doc, args.Raw["offset"]) : XYZ.Zero;

        using var t = RevitHelpers.StartTransaction(doc, args.OptBool("copy") ? "Copy elements" : "Move elements");
        if (args.OptBool("copy"))
            ids = ElementTransformUtils.CopyElements(doc, ids, offset);
        else if (!offset.IsZeroLength())
            ElementTransformUtils.MoveElements(doc, ids, offset);

        if (args.OptDouble("rotation_degrees") is { } deg && deg != 0)
        {
            XYZ center;
            if (args.Has("rotation_center")) center = Modeling.Point(doc, args.Raw["rotation_center"]);
            else
            {
                var boxes = ids.Select(id => doc.GetElement(id).get_BoundingBox(null)).Where(b => b != null).ToList();
                center = boxes.Count == 0 ? XYZ.Zero
                    : new XYZ(boxes.Average(b => (b.Min.X + b.Max.X) / 2), boxes.Average(b => (b.Min.Y + b.Max.Y) / 2), 0);
            }
            var axis = Line.CreateBound(new XYZ(center.X, center.Y, 0), new XYZ(center.X, center.Y, 1));
            ElementTransformUtils.RotateElements(doc, ids, axis, deg * Math.PI / 180);
        }
        t.Commit();
        return ToolResult.Ok(new { elements = ids.Select(i => i.Value) });
    }
}
