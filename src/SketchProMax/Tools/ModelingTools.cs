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
        return (Polygon(doc, pts, level.ProjectElevation), level);
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

    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Grid-style letters for a 0-based index: A..Z, then AA, AB... (from the given alphabet).</summary>
    public static string Letters(int i, string alphabet = Alphabet) =>
        i < alphabet.Length ? alphabet[i].ToString() : Letters(i / alphabet.Length - 1, alphabet) + alphabet[i % alphabet.Length];

    /// <summary>2D intersections of all pairs of straight grids (every grid in the model unless a set is given).</summary>
    public static List<(XYZ point, string a, string b)> GridIntersections(Document doc, IEnumerable<Grid>? only = null)
    {
        var grids = (only ?? new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()).Where(g => g.Curve is Line).ToList();
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
        "Create levels at given elevations (project units), optionally with a floor plan and ceiling plan for each. " +
        "Elevations are as Revit displays them for the level type (relative to its Elevation Base: Project Base Point " +
        "or Survey Point), the same values get_model_context reports.";
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

        // Elevations are read as Revit displays them for the level type (Elevation Base = Project Base Point or
        // Survey Point), not as internal Z. Bases are taken before any level is added.
        var defaultType = Elevations.DefaultLevelTypeId(doc);
        var bases = new Dictionary<ElementId, double>();
        double Base(ElementId typeId)
        {
            if (!bases.TryGetValue(typeId, out var z)) bases[typeId] = z = Elevations.BaseZ(doc, typeId);
            return z;
        }
        Base(defaultType);

        var created = new List<(Level level, long? floorPlan, long? ceilingPlan)>();
        using var t = RevitHelpers.StartTransaction(doc, "Create levels");
        foreach (var spec in args.Array("levels"))
        {
            var wanted = RevitHelpers.ToInternal(doc, spec.GetProperty("elevation").GetDouble());
            var level = Level.Create(doc, wanted + Base(defaultType));
            var actualType = level.GetTypeId();
            if (actualType != defaultType && Math.Abs(Base(actualType) - Base(defaultType)) > 1e-9)
                ElementTransformUtils.MoveElement(doc, level.Id, new XYZ(0, 0, Base(actualType) - Base(defaultType)));
            try { level.Name = spec.GetProperty("name").GetString()!; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            long? floorPlan = null, ceilingPlan = null;
            if (plans)
            {
                if (floorVft != null) floorPlan = ViewPlan.Create(doc, floorVft.Id, level.Id).Id.Value;
                if (ceilingVft != null) ceilingPlan = ViewPlan.Create(doc, ceilingVft.Id, level.Id).Id.Value;
            }
            created.Add((level, floorPlan, ceilingPlan));
        }
        t.Commit();
        // Report the elevation Revit shows for each level (same basis as get_model_context).
        return ToolResult.Ok(new
        {
            created = created.Select(c => new
            {
                id = c.level.Id.Value, name = c.level.Name, elevation = Elevations.Displayed(doc, c.level),
                floorPlanId = c.floorPlan, ceilingPlanId = c.ceilingPlan,
            }).ToList(),
            elevationBaseInternalZ = RevitHelpers.FromInternal(doc, Base(defaultType)),
        });
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
        var elevations = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.ProjectElevation).DefaultIfEmpty(0).ToList();
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
            // Letters skip I and O (easily confused with 1 and 0), and run on to AA, AB... after Z.
            const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            var letter = r.TryGetProperty("first_letter", out var fl) && fl.GetString() is { Length: > 0 } s
                ? Math.Max(0, letters.IndexOf(char.ToUpperInvariant(s[0])))
                : 0;
            double xMax = xs.Last(), yMax = ys.Last();

            foreach (var x in xs)
                Make(origin + new XYZ(x, -ext, 0), origin + new XYZ(x, yMax + ext, 0), (number++).ToString());
            foreach (var y in ys)
            {
                Make(origin + new XYZ(-ext, y, 0), origin + new XYZ(xMax + ext, y, 0), Modeling.Letters(letter++, letters));
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
        "otherwise every edge slopes (hip roof) except gable_edges (gable ends). Edge i runs from point i to point i+1. " +
        "Optional overhang: the outline is offset outwards by that distance.";
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
        var loop = Modeling.Polygon(doc, args.Raw["points"], level.ProjectElevation);

        var slope = args.OptDouble("slope_degrees") ?? 0;
        if (slope < 0 || slope >= 89) return ToolResult.Error("slope_degrees must be between 0 (flat) and 89.");
        var overhang = args.OptDouble("overhang") is { } o ? RevitHelpers.ToInternal(doc, o) : 0;
        var gables = args.LongList("gable_edges").Select(i => (int)i).ToHashSet();
        var edgeCount = loop.Count();
        var outOfRange = gables.Where(i => i < 0 || i >= edgeCount).ToList();
        if (outOfRange.Count > 0)
            return ToolResult.Error($"gable_edges {string.Join(", ", outOfRange)} out of range: the outline has {edgeCount} edges (0 to {edgeCount - 1}; edge i runs from point i to point i+1).");

        // Overhang: offset the sketch outwards (set_Overhang only applies to edges picked from walls).
        if (overhang > 0)
        {
            CurveLoop? grown = null;
            foreach (var sign in new[] { 1.0, -1.0 })
            {
                try
                {
                    var candidate = CurveLoop.CreateViaOffset(loop, sign * overhang, XYZ.BasisZ);
                    if (candidate.Count() == edgeCount && PlanArea(candidate) > PlanArea(loop)) { grown = candidate; break; }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }
            loop = grown ?? throw new ArgumentException("Could not offset the outline by the overhang (too large for this shape?). Try a smaller overhang.");
        }
        var outline = loop.ToList();
        var footprint = new CurveArray();
        foreach (var c in outline) footprint.Append(c);

        using var t = RevitHelpers.StartTransaction(doc, "Create roof");
        // The mapping array must exist before the call: Revit fills it, and throws "Value cannot be null" otherwise.
        ModelCurveArray edges = new ModelCurveArray();
        var roof = doc.Create.NewFootPrintRoof(footprint, level, roofType, out edges)
                   ?? throw new InvalidOperationException("Revit did not create the roof. Check that the outline is closed and does not cross itself.");
        var slopeValue = Math.Tan(slope * Math.PI / 180); // Revit stores roof slopes as rise/run
        var sloped = new List<int>();
        foreach (ModelCurve edge in edges ?? new ModelCurveArray())
        {
            // Match each sketch line to its outline edge by midpoint, so gable_edges follow the input order.
            var mid = edge.GeometryCurve.Evaluate(0.5, true);
            var index = Enumerable.Range(0, outline.Count).OrderBy(i => outline[i].Evaluate(0.5, true).DistanceTo(mid)).First();
            var slopes = slope > 0 && !gables.Contains(index);
            roof.set_DefinesSlope(edge, slopes);
            if (slopes) { roof.set_SlopeAngle(edge, slopeValue); sloped.Add(index); }
        }
        t.Commit();
        var kind = slope <= 0 ? "flat" : gables.Count == 0 ? "hip" : "gable";
        return ToolResult.Ok(new { roofId = roof.Id.Value, type = roofType.Name, kind, slopeDegrees = slope, slopedEdges = sloped.OrderBy(i => i), gableEdges = gables.OrderBy(i => i) });
    }

    private static double PlanArea(CurveLoop loop)
    {
        double a = 0;
        foreach (var c in loop)
        {
            XYZ p = c.GetEndPoint(0), q = c.GetEndPoint(1);
            a += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(a) / 2;
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
        "at points on a level, with optional rotation in degrees and instance parameter values. Each placement is its own step; " +
        "parameters are set after placing, read back after regeneration, re-applied once if they did not stick, and anything " +
        "still different is reported with the reason (formula, read-only, type parameter).";
    public string PropertiesJson => """
    {
      "type_id": {"type": "integer"},
      "level": {"type": "string"},
      "placements": {"type": "array", "items": {"type": "object", "properties": {
        "point": {"type": "array", "items": {"type": "number"}, "description": "[x, y] or [x, y, z offset]"},
        "rotation_degrees": {"type": "number"},
        "parameters": {"type": "object", "description": "Instance parameters to set, name -> value as typed in Revit."}}, "required": ["point"]}}
    }
    """;
    public string[] Required => ["type_id", "placements"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var level = RevitHelpers.FindLevel(doc, args.OptString("level"));
        var symbol = doc.GetElement(new ElementId(args.OptLong("type_id") ?? -1)) as FamilySymbol
                     ?? throw new ArgumentException($"{args.OptLong("type_id")} is not a loadable family type. Use list_types to find one.");
        var errors = new List<string>();
        var items = new List<(int index, XYZ point, double rotation, List<ParamEdits.Want> wants)>();
        var index = 0;
        foreach (var p in args.Array("placements"))
        {
            index++;
            try
            {
                var pt = Modeling.Point(doc, p.GetProperty("point"));
                var rot = p.TryGetProperty("rotation_degrees", out var r) ? r.GetDouble() : 0;
                var wants = p.TryGetProperty("parameters", out var ps) ? ParamEdits.FromJson(ps) : [];
                items.Add((index, pt, rot, wants));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or FormatException)
            {
                errors.Add($"placement {index}: {ex.Message}");
            }
        }

        var created = new List<long>();
        var parameterIssues = new List<object>();
        Batch.InGroup(doc, "Place family instances", () =>
        {
            var batch = ItemBatch.Run(doc, "Place family instances", items, i => $"placement {i.index}", _ => [], i =>
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                var pt = i.point;
                var fi = doc.Create.NewFamilyInstance(new XYZ(pt.X, pt.Y, level.ProjectElevation + pt.Z), symbol, level, StructuralType.NonStructural);
                if (i.rotation != 0)
                {
                    var axis = Line.CreateBound(new XYZ(pt.X, pt.Y, 0), new XYZ(pt.X, pt.Y, 1));
                    ElementTransformUtils.RotateElement(doc, fi.Id, axis, i.rotation * Math.PI / 180);
                }
                return fi.Id;
            }, borrow: false);
            var targets = new List<ParamEdits.Target>();
            for (var k = 0; k < items.Count; k++)
            {
                var o = batch.Items[k];
                if (!o.Ok) { errors.Add($"{o.Item}: {o.Error}"); continue; }
                var id = (ElementId)o.Value!;
                created.Add(id.Value);
                if (items[k].wants.Count > 0) targets.Add(new ParamEdits.Target { Id = id, Label = o.Item, Wants = items[k].wants });
            }
            foreach (var l in ParamEdits.Problems(ParamEdits.Apply(doc, "Instance parameters", targets, borrow: false)))
                parameterIssues.Add(new { element_id = l.element_id, l.parameter, l.value, l.status, l.now, l.note });
            return created.Count;
        }, n => n > 0);
        return ToolResult.Ok(new { family = symbol.FamilyName, created, errors, parameters = parameterIssues.Count > 0 ? parameterIssues : null });
    }
}

public sealed class PlaceColumnsTool : IRevitTool
{
    public const int ConfirmAbove = 100;
    public const int MaxIds = 100;

    public string Name => "place_columns";
    public string Description =>
        "Place structural (or architectural) columns from a base level to a top level, at given points or at grid " +
        "intersections. at_grid_intersections uses the grids named in 'grids' (both grids of a crossing must be listed), " +
        "else the grids visible in the active plan view, else every grid; 'area' limits it to a plan rectangle. " +
        $"More than {ConfirmAbove} columns needs confirm_count equal to the number to place (call once without it to " +
        "learn the count). Use list_types 'Structural Columns' or 'Columns' for type ids.";
    public string PropertiesJson => """
    {
      "type_id": {"type": "integer"},
      "base_level": {"type": "string"},
      "top_level": {"type": "string"},
      "at_grid_intersections": {"type": "boolean"},
      "grids": {"type": "array", "items": {"type": "string"}, "description": "Grid names to use for at_grid_intersections (default: grids visible in the active plan view)."},
      "area": {"type": "object", "properties": {"min": {"type": "array", "items": {"type": "number"}}, "max": {"type": "array", "items": {"type": "number"}}},
        "description": "Only grid intersections inside this plan rectangle [x, y] (project units)."},
      "confirm_count": {"type": "integer", "description": "Required when more than 100 columns would be placed: the exact number expected."},
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
        var kind = args.OptBool("architectural") ? StructuralType.NonStructural : StructuralType.Column;

        var points = args.Array("points").Select(p => Modeling.Point(doc, p)).ToList();
        string? gridSource = null;
        if (args.OptBool("at_grid_intersections"))
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            List<Grid> grids;
            var names = args.StringList("grids");
            if (names.Count > 0)
            {
                var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                grids = all.Where(g => wanted.Contains(g.Name)).ToList();
                var missing = names.Where(n => !grids.Any(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0) return ToolResult.Error($"No grid named {string.Join(", ", missing.Select(m => $"'{m}'"))}.");
                gridSource = "named grids";
            }
            else if (doc.ActiveView is ViewPlan plan
                     && new FilteredElementCollector(doc, plan.Id).OfClass(typeof(Grid)).Cast<Grid>().ToList() is { Count: > 1 } visible)
            {
                grids = visible;
                gridSource = $"grids visible in '{plan.Name}'";
            }
            else
            {
                grids = all;
                gridSource = "all grids in the model";
            }

            var hits = Modeling.GridIntersections(doc, grids).Select(h => h.point);
            if (args.Has("area"))
            {
                var area = args.Raw["area"];
                if (!area.TryGetProperty("min", out var mn) || !area.TryGetProperty("max", out var mx))
                    return ToolResult.Error("area needs min [x, y] and max [x, y].");
                XYZ a = Modeling.Point(doc, mn), b = Modeling.Point(doc, mx);
                double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                const double eps = 1e-6;
                hits = hits.Where(p => p.X >= x0 - eps && p.X <= x1 + eps && p.Y >= y0 - eps && p.Y <= y1 + eps);
            }
            points.AddRange(hits);
        }
        // Several grids crossing at one point (or a point given twice) must not stack columns.
        var unique = new List<XYZ>();
        foreach (var p in points)
            if (!unique.Any(u => Math.Abs(u.X - p.X) < 1e-4 && Math.Abs(u.Y - p.Y) < 1e-4)) unique.Add(p);
        points = unique;
        if (points.Count == 0) return ToolResult.Error("No points given and no grid intersections found" + (gridSource is null ? "." : $" ({gridSource})."));

        var confirm = args.OptLong("confirm_count");
        if ((points.Count > ConfirmAbove && confirm is null) || (confirm is { } c && c != points.Count))
            return ToolResult.Error($"This would place {points.Count} columns" + (gridSource is null ? "" : $" ({gridSource})") +
                $". Pass confirm_count: {points.Count} to go ahead, or narrow it with 'grids' or 'area'.");

        var created = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Place columns");
        var symbol = Modeling.Symbol(doc, args.OptLong("type_id") ?? -1);
        foreach (var p in points)
        {
            var fi = doc.Create.NewFamilyInstance(new XYZ(p.X, p.Y, baseLevel.ProjectElevation), symbol, baseLevel, kind);
            fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(topLevel.Id);
            fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(0.0);
            created.Add(fi.Id.Value);
        }
        t.Commit();
        return ToolResult.Ok(new
        {
            created = created.Count,
            ids = created.Take(MaxIds),
            idsTruncated = created.Count > MaxIds,
            gridSource,
        });
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
        var z = level.ProjectElevation;

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
        var symbol = Modeling.Symbol(doc, args.OptLong("type_id") ?? -1);
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
        "through a point. With copy=true the originals stay and the copies are returned. Workshared: elements another user owns are " +
        "left in place and named with their owner; if Revit refuses the set as a whole, each element is tried on its own.";
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
        if (ids.Count == 0) return ToolResult.Error("None of those element ids exist in the model. Check them with query_elements.");
        var offset = args.Has("offset") ? Modeling.Point(doc, args.Raw["offset"]) : XYZ.Zero;
        var copy = args.OptBool("copy");
        var deg = args.OptDouble("rotation_degrees") ?? 0;

        // Moving or rotating needs ownership; copying only reads the originals. Locked ones are left and named.
        var locked = copy ? new Dictionary<long, string>() : Batch.Locked(doc, ids);
        var work = ids.Where(id => !locked.ContainsKey(id.Value)).ToList();
        if (work.Count == 0) return ToolResult.Error($"Nothing moved: {string.Join("; ", locked.Values.Take(10))}");

        // The rotation centre is fixed before anything moves, so a per-element retry turns everything about the same point.
        XYZ? center = null;
        if (deg != 0)
        {
            if (args.Has("rotation_center")) center = Modeling.Point(doc, args.Raw["rotation_center"]);
            else
            {
                var boxes = work.Select(id => doc.GetElement(id).get_BoundingBox(null)).Where(b => b != null).ToList();
                center = boxes.Count == 0 ? XYZ.Zero
                    : new XYZ(boxes.Average(b => (b.Min.X + b.Max.X) / 2), boxes.Average(b => (b.Min.Y + b.Max.Y) / 2), 0) + offset;
            }
        }
        ICollection<ElementId> Apply(ICollection<ElementId> set)
        {
            var result = set;
            if (copy) result = ElementTransformUtils.CopyElements(doc, set, offset);
            else if (!offset.IsZeroLength()) ElementTransformUtils.MoveElements(doc, set, offset);
            if (center is not null)
            {
                var axis = Line.CreateBound(new XYZ(center.X, center.Y, 0), new XYZ(center.X, center.Y, 1));
                ElementTransformUtils.RotateElements(doc, result, axis, deg * Math.PI / 180);
            }
            return result;
        }

        // All together first; if Revit refuses that, each element on its own (hosted elements travel with a host in the set).
        var method = "together";
        var failures = new List<object>();
        var done = new List<long>();
        var withHost = new List<long>();
        Batch.InGroup(doc, copy ? "Copy elements" : "Move elements", () =>
        {
            var all = ItemBatch.Run(doc, copy ? "Copy elements" : "Move elements", [work], _ => "all", x => copy ? [] : x, x => Apply(x));
            if (all.Done == 1) { done.AddRange(((ICollection<ElementId>)all.Items[0].Value!).Select(i => i.Value)); return done.Count; }
            method = "one by one";
            var set = new HashSet<long>(work.Select(i => i.Value));
            var single = work.Where(id => !(doc.GetElement(id) is FamilyInstance { Host: { } h } && set.Contains(h.Id.Value))).ToList();
            withHost.AddRange(work.Where(id => !single.Contains(id)).Select(i => i.Value));
            var each = ItemBatch.Run(doc, copy ? "Copy elements" : "Move elements", single, id => id.Value.ToString(), id => copy ? [] : [id],
                id => Apply([id]), borrow: false);
            foreach (var o in each.Items.Where(o => o.Ok)) done.AddRange(((ICollection<ElementId>)o.Value!).Select(i => i.Value));
            failures.AddRange(Batch.Failures(each));
            if (each.Done == 0 && all.Failed.FirstOrDefault()?.Error is { } why) failures.Insert(0, new { item = "all together", reason = why });
            return each.Done;
        }, n => n > 0);

        if (done.Count == 0)
            return ToolResult.Error($"Revit refused the {(copy ? "copy" : "move")}; nothing changed. {System.Text.Json.JsonSerializer.Serialize(failures.Take(10))}");
        return ToolResult.Ok(new
        {
            elements = done,
            method,
            movedWithHost = withHost.Count > 0 ? withHost : null,
            locked = locked.Count > 0 ? locked.Select(kv => new { id = kv.Key, reason = kv.Value }) : null,
            failures = failures.Count > 0 ? failures : null,
        });
    }
}
