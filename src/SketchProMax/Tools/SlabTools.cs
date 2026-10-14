using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>2D boolean work on floor outlines, done with thin extrusions and Revit's solid booleans.</summary>
internal static class Outlines
{
    private const double Thickness = 1.0;

    public static List<CurveLoop> Flat(IEnumerable<CurveLoop> loops) =>
        loops.Select(l => CurveLoop.CreateViaTransform(l, Transform.CreateTranslation(new XYZ(0, 0, -l.First().GetEndPoint(0).Z)))).ToList();

    public static Solid Extrude(IList<CurveLoop> loops) => GeometryCreationUtilities.CreateExtrusionGeometry(Flat(loops), XYZ.BasisZ, Thickness);

    /// <summary>Each separate piece of a solid's footprint: its outer boundary plus any holes.</summary>
    public static List<List<CurveLoop>> Pieces(Solid solid)
    {
        var pieces = new List<List<CurveLoop>>();
        if (solid.Volume <= 1e-9) return pieces;
        foreach (Face f in solid.Faces)
            if (f is PlanarFace pf && pf.FaceNormal.Z < -0.99)
                pieces.Add(pf.GetEdgesAsCurveLoops().ToList());
        return pieces;
    }

    public static List<CurveLoop> Polygon(Document doc, System.Text.Json.JsonElement points)
    {
        var pts = points.EnumerateArray().Select(p => ViewKit.Point(doc, p)).ToList();
        var loop = new CurveLoop();
        for (var i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
        return [loop];
    }

    public static List<CurveLoop> FloorProfile(Document doc, Floor floor)
    {
        var sketch = (Sketch)doc.GetElement(floor.SketchId);
        var loops = new List<CurveLoop>();
        foreach (CurveArray array in sketch.Profile)
        {
            var loop = new CurveLoop();
            foreach (Curve c in array) loop.Append(c);
            loops.Add(loop);
        }
        return loops;
    }

    public static Floor ParseFloor(Document doc, long id) =>
        doc.GetElement(new ElementId(id)) as Floor ?? throw new ArgumentException($"{id} is not a floor.");
}

public sealed class CutSlabTool : IRevitTool
{
    public string Name => "cut_slab";
    public string Description =>
        "Change a floor's outline by cutting a region away (an edge cut, notch, chamfered corner or recess at the slab " +
        "edge) or adding a region to it. Give the region as plan points. Edits the floor's own sketch, so hosted " +
        "elements, tags and schedules stay connected. For a hole fully inside the slab, create_mep_openings or run_revit_code can make an opening instead.";
    public string PropertiesJson => """
    {
      "floor_id": {"type": "integer"},
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "[[x, y], ...] closed polygon, project units."},
      "operation": {"type": "string", "enum": ["cut", "add"], "description": "Default cut."}
    }
    """;
    public string[] Required => ["floor_id", "points"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var floor = Outlines.ParseFloor(doc, args.OptLong("floor_id")!.Value);
        var current = Outlines.FloorProfile(doc, floor);
        var region = Outlines.Polygon(doc, args.Raw["points"]);
        var op = args.OptString("operation") == "add" ? BooleanOperationsType.Union : BooleanOperationsType.Difference;
        var result = BooleanOperationsUtils.ExecuteBooleanOperation(Outlines.Extrude(current), Outlines.Extrude(region), op);
        var pieces = Outlines.Pieces(result);
        if (pieces.Count == 0) return ToolResult.Error("Nothing would be left of the floor.");
        if (pieces.Count > 1 && op == BooleanOperationsType.Difference)
            return ToolResult.Error($"That cut would split the floor into {pieces.Count} pieces. Cut it in two steps, or split it with run_revit_code.");

        var sketch = (Sketch)doc.GetElement(floor.SketchId);
        var z = sketch.SketchPlane.GetPlane().Origin.Z;
        var lift = Transform.CreateTranslation(new XYZ(0, 0, z));

        var scope = new SketchEditScope(doc, "Sketch ProMax: Cut slab");
        scope.Start(floor.SketchId);
        using (var t = new Transaction(doc, "Sketch ProMax: slab outline"))
        {
            t.Start();
            foreach (var id in sketch.GetAllElements())
                if (doc.GetElement(id) is CurveElement) doc.Delete(id);
            foreach (var loop in pieces[0])
                foreach (var curve in loop)
                    doc.Create.NewModelCurve(curve.CreateTransformed(lift), sketch.SketchPlane);
            t.Commit();
        }
        scope.Commit(new TypeKit.SwallowWarnings());
        return ToolResult.Ok(new { floor = floor.Id.Value, operation = op == BooleanOperationsType.Union ? "add" : "cut", loops = pieces[0].Count });
    }
}

public sealed class AddSlabEdgesTool : IRevitTool
{
    public string Name => "add_slab_edges";
    public string Description =>
        "Add slab edges (upstands, downstands, edge beams, balcony nosings) along a floor's outer edges, or along " +
        "edges facing given directions, using a slab edge type.";
    public string PropertiesJson => """
    {
      "floor_ids": {"type": "array", "items": {"type": "integer"}},
      "slab_edge_type": {"type": "string"},
      "facing": {"type": "array", "items": {"type": "string", "enum": ["north", "south", "east", "west"]}, "description": "Only edges facing these directions. Default: all outer edges."}
    }
    """;
    public string[] Required => ["floor_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var type = TypeKit.Pick<SlabEdgeType>(doc, args.OptString("slab_edge_type"), "slab edge");
        var facing = args.StringList("facing").Select(f => f.ToLowerInvariant()).ToList();
        var made = new List<long>();
        var problems = new List<string>();
        using var t = RevitHelpers.StartTransaction(doc, "Slab edges");
        foreach (var id in args.LongList("floor_ids"))
        {
            var floor = Outlines.ParseFloor(doc, id);
            var top = HostObjectUtils.GetTopFaces(floor).Select(r => (Ref: r, Face: floor.GetGeometryObjectFromReference(r) as PlanarFace)).FirstOrDefault(x => x.Face != null);
            if (top.Face is null) { problems.Add($"{id}: no flat top face"); continue; }
            var refs = new ReferenceArray();
            var outer = top.Face.EdgeLoops.Cast<EdgeArray>().OrderByDescending(loop => loop.Cast<Edge>().Sum(e => e.ApproximateLength)).First();
            var centre = (top.Face.GetBoundingBox().Min + top.Face.GetBoundingBox().Max) / 2;
            var mid = top.Face.Evaluate(centre);
            foreach (Edge edge in outer)
            {
                var curve = edge.AsCurve();
                var p = curve.Evaluate(0.5, true);
                var outward = new XYZ(p.X - mid.X, p.Y - mid.Y, 0);
                if (facing.Count > 0 && !facing.Contains(ViewKit.Compass(outward).ToLowerInvariant())) continue;
                refs.Append(edge.Reference);
            }
            if (refs.Size == 0) continue;
            try { made.Add(doc.Create.NewSlabEdge(type, refs).Id.Value); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { problems.Add($"{id}: {ex.Message}"); }
        }
        t.Commit();
        return ToolResult.Ok(new { slabEdges = made.Count, ids = made, type = type.Name, problems });
    }
}

public sealed class HollowCoreLayoutTool : IRevitTool
{
    public string Name => "hollow_core_layout";
    public string Description =>
        "Lay out precast hollow core planks over a floor, room, or polygon: planks of a standard width spanning in a " +
        "chosen direction, with joints, the last plank cut to fit, each plank its own floor element (type created at " +
        "the plank depth) marked HC-01, HC-02... so they schedule by length and width. Optionally hide or delete the original slab.";
    public string PropertiesJson => """
    {
      "floor_id": {"type": "integer", "description": "Area to cover (use its outline). Or give points or room_id."},
      "room_id": {"type": "integer"},
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}},
      "level": {"type": "string", "description": "When using points or a room. Default: the floor's or room's level."},
      "span_direction": {"type": "string", "enum": ["x", "y", "auto"], "description": "Direction the planks span (their length). auto = shorter side of the area."},
      "plank_width": {"type": "number", "description": "Project units, default 1200 mm."},
      "depth": {"type": "number", "description": "Plank depth, default 200 mm (150, 200, 265, 320, 400 are common)."},
      "joint": {"type": "number", "description": "Gap between planks, default 10 mm."},
      "start_from": {"type": "string", "enum": ["min", "max", "center"], "description": "Side where full planks start; the cut plank lands on the other side (center: cuts split both sides)."},
      "original": {"type": "string", "enum": ["keep", "delete"], "description": "What to do with floor_id afterwards. Default keep."},
      "mark_prefix": {"type": "string", "description": "Default 'HC-'."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        double Mm(double v) => UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Millimeters);
        var width = args.OptDouble("plank_width") is { } pw ? RevitHelpers.ToInternal(doc, pw) : Mm(1200);
        var depth = args.OptDouble("depth") is { } d ? RevitHelpers.ToInternal(doc, d) : Mm(200);
        var joint = args.OptDouble("joint") is { } j ? RevitHelpers.ToInternal(doc, j) : Mm(10);

        List<CurveLoop> area;
        Level level;
        Floor? original = null;
        if (args.OptLong("floor_id") is { } fid)
        {
            original = Outlines.ParseFloor(doc, fid);
            area = Outlines.FloorProfile(doc, original);
            level = (Level)doc.GetElement(original.LevelId);
        }
        else if (args.OptLong("room_id") is { } rid)
        {
            var room = doc.GetElement(new ElementId(rid)) as Autodesk.Revit.DB.Architecture.Room ?? throw new ArgumentException($"{rid} is not a room.");
            area = room.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Center })
                .Select(segs => CurveLoop.Create(segs.Select(s => s.GetCurve()).ToList())).ToList();
            level = room.Level;
        }
        else if (args.Has("points"))
        {
            area = Outlines.Polygon(doc, args.Raw["points"]);
            level = RevitHelpers.FindLevel(doc, args.OptString("level"));
        }
        else return ToolResult.Error("Give floor_id, room_id or points.");
        if (args.OptString("level") is { } ln) level = RevitHelpers.FindLevel(doc, ln);

        var areaSolid = Outlines.Extrude(area);
        var box = areaSolid.GetBoundingBox();
        var min = box.Transform.OfPoint(box.Min);
        var max = box.Transform.OfPoint(box.Max);
        var spanAlongX = args.OptString("span_direction") switch
        {
            "x" => true,
            "y" => false,
            _ => max.X - min.X <= max.Y - min.Y,
        };
        // Planks span along one axis and are laid side by side along the other.
        double acrossMin = spanAlongX ? min.Y : min.X, acrossMax = spanAlongX ? max.Y : max.X;
        double spanMin = (spanAlongX ? min.X : min.Y) - 1, spanMax = (spanAlongX ? max.X : max.Y) + 1;
        var pitch = width + joint;
        var total = acrossMax - acrossMin;
        var full = (int)Math.Floor((total + joint) / pitch);
        var remainder = total - full * pitch;
        var start = args.OptString("start_from") switch
        {
            "max" => acrossMin + remainder,
            "center" => acrossMin + remainder / 2,
            _ => acrossMin,
        };
        var strips = new List<(double From, double To)>();
        if (start - acrossMin > Mm(20)) strips.Add((acrossMin, start - joint));
        for (var i = 0; i < full; i++) strips.Add((start + i * pitch, start + i * pitch + width));
        var last = start + full * pitch;
        if (acrossMax - last > Mm(20)) strips.Add((last, acrossMax));

        using var t = new Transaction(doc, "Sketch ProMax: Hollow core layout");
        t.Start();
        var failure = t.GetFailureHandlingOptions();
        failure.SetFailuresPreprocessor(new TypeKit.SwallowWarnings());
        t.SetFailureHandlingOptions(failure);

        var type = PlankType(doc, depth);
        var prefix = args.OptString("mark_prefix") ?? "HC-";
        var planks = new List<object>();
        var n = 0;
        foreach (var (from, to) in strips)
        {
            XYZ P(double span, double across) => spanAlongX ? new XYZ(span, across, 0) : new XYZ(across, span, 0);
            var rect = new CurveLoop();
            var corners = new[] { P(spanMin, from), P(spanMax, from), P(spanMax, to), P(spanMin, to) };
            for (var i = 0; i < 4; i++) rect.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
            var clipped = BooleanOperationsUtils.ExecuteBooleanOperation(areaSolid, Outlines.Extrude([rect]), BooleanOperationsType.Intersect);
            foreach (var piece in Outlines.Pieces(clipped))
            {
                var loops = piece.Select(l => CurveLoop.CreateViaTransform(l, Transform.CreateTranslation(new XYZ(0, 0, level.Elevation)))).ToList();
                Floor plank;
                try { plank = Floor.Create(doc, loops, type.Id, level.Id); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
                n++;
                plank.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set($"{prefix}{n:00}");
                var pieceBox = Outlines.Extrude(piece).GetBoundingBox();
                var length = spanAlongX ? pieceBox.Max.X - pieceBox.Min.X : pieceBox.Max.Y - pieceBox.Min.Y;
                var plankWidth = to - from;
                planks.Add(new
                {
                    mark = $"{prefix}{n:00}", id = plank.Id.Value,
                    length = RevitHelpers.FromInternal(doc, length), width = RevitHelpers.FromInternal(doc, plankWidth),
                    cut = plankWidth < width - Mm(1),
                });
            }
        }
        if (original != null && args.OptString("original") == "delete") doc.Delete(original.Id);
        t.Commit();
        return ToolResult.Ok(new
        {
            planks = planks.Count, type = type.Name, spanDirection = spanAlongX ? "x" : "y",
            cutPlanks = planks.Count(p => (bool)p.GetType().GetProperty("cut")!.GetValue(p)!), list = planks,
        });
    }

    /// <summary>A floor type named for the plank depth, made from the first floor type with its structure set to that depth.</summary>
    private static FloorType PlankType(Document doc, double depth)
    {
        var mm = Math.Round(UnitUtils.ConvertFromInternalUnits(depth, UnitTypeId.Millimeters));
        var name = $"Hollow Core Plank {mm}mm";
        var types = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
        if (types.FirstOrDefault(x => x.Name == name) is { } existing) return existing;
        var basis = types.FirstOrDefault(x => x.Name.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) >= 0) ?? types.First();
        var type = (FloorType)basis.Duplicate(name);
        var cs = type.GetCompoundStructure();
        if (cs != null)
        {
            var layers = new List<CompoundStructureLayer> { new(depth, MaterialFunctionAssignment.Structure, cs.GetLayers().FirstOrDefault()?.MaterialId ?? ElementId.InvalidElementId) };
            cs.SetLayers(layers);
            cs.StructuralMaterialIndex = 0;
            type.SetCompoundStructure(cs);
        }
        return type;
    }
}
