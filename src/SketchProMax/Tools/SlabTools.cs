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
        if (pieces.Count > 1)
            return ToolResult.Error(op == BooleanOperationsType.Difference
                ? $"That cut would split the floor into {pieces.Count} pieces. Cut it in two steps, or split it with run_revit_code."
                : $"That region doesn't overlap the floor, so the result would be {pieces.Count} separate pieces. Make the region overlap the slab edge.");

        var sketch = (Sketch)doc.GetElement(floor.SketchId);
        var z = sketch.SketchPlane.GetPlane().Origin.Z;
        var lift = Transform.CreateTranslation(new XYZ(0, 0, z));

        using var scope = new SketchEditScope(doc, "Sketch ProMax: Cut slab");
        scope.Start(floor.SketchId);
        try
        {
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
        }
        catch
        {
            if (scope.IsActive) scope.Cancel();
            throw;
        }
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
            // Edges of a face only carry references when the geometry is read with ComputeReferences; the faces from
            // HostObjectUtils.GetTopFaces / GetGeometryObjectFromReference have null edge references.
            var top = TopFace(floor);
            if (top is null) { problems.Add($"{id}: no flat top face"); continue; }
            var refs = new ReferenceArray();
            var outer = top.EdgeLoops.Cast<EdgeArray>().OrderByDescending(loop => loop.Cast<Edge>().Sum(e => e.ApproximateLength)).FirstOrDefault();
            if (outer is null) { problems.Add($"{id}: the top face has no edges"); continue; }
            foreach (Edge edge in outer)
            {
                if (edge.Reference is null) continue;
                // Following the face, the face lies to the left of the edge, so direction x normal points out of the slab.
                var tangent = edge.AsCurveFollowingFace(top).ComputeDerivatives(0.5, true).BasisX;
                var side = tangent.CrossProduct(top.FaceNormal);
                var outward = new XYZ(side.X, side.Y, 0);
                if (facing.Count > 0 && (outward.GetLength() < 1e-9 || !facing.Contains(ViewKit.Compass(outward).ToLowerInvariant()))) continue;
                refs.Append(edge.Reference);
            }
            if (refs.Size == 0 && facing.Count > 0) { problems.Add($"{id}: no outer edge faces {string.Join("/", facing)}"); continue; }
            if (refs.Size == 0) { problems.Add($"{id}: no edge references on the top face"); continue; }
            try { made.Add(doc.Create.NewSlabEdge(type, refs).Id.Value); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { problems.Add($"{id}: {ex.Message}"); }
        }
        t.Commit();
        made = made.Where(i => doc.GetElement(new ElementId(i)) != null).ToList();
        return ToolResult.Ok(new { slabEdges = made.Count, ids = made, type = type.Name, problems });
    }

    /// <summary>The floor's highest upward-facing planar face, read with references so its edges can host slab edges.</summary>
    private static PlanarFace? TopFace(Floor floor)
    {
        var geometry = floor.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
        if (geometry is null) return null;
        var faces = new List<PlanarFace>();
        void Collect(GeometryElement ge)
        {
            foreach (var obj in ge)
            {
                if (obj is Solid solid && solid.Faces.Size > 0)
                {
                    foreach (Face f in solid.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.Z > 0.99 && pf.Reference != null) faces.Add(pf);
                }
                else if (obj is GeometryInstance gi) Collect(gi.GetInstanceGeometry());
            }
        }
        Collect(geometry);
        return faces.OrderByDescending(f => f.Origin.Z).ThenByDescending(f => f.Area).FirstOrDefault();
    }
}

public sealed class HollowCoreLayoutTool : IRevitTool
{
    public string Name => "hollow_core_layout";
    public string Description =>
        "Lay out precast slab units over a floor, room, or polygon: hollow core planks, solid planks, filigree (lattice " +
        "girder) planks or double tees, of a standard width spanning in a chosen direction, with joints, the last unit cut " +
        "to fit, each unit its own floor element (type created at the unit depth) marked HC-01, SP-01, FP-01 or DT-01... so " +
        "they schedule by length and width. Optionally a cast-in-place structural topping over them, and delete the original slab. " +
        "export_precast_schedule lists the units with weights.";
    public string PropertiesJson => """
    {
      "floor_id": {"type": "integer", "description": "Area to cover (use its outline). Or give points or room_id."},
      "room_id": {"type": "integer"},
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}},
      "level": {"type": "string", "description": "When using points or a room. Default: the floor's or room's level."},
      "kind": {"type": "string", "enum": ["hollow_core", "solid_plank", "filigree", "double_tee"], "description": "Default hollow_core. Sets the default width, depth and marks."},
      "topping": {"type": "number", "description": "Cast-in-place topping thickness, project units (e.g. 75). The units drop by this much and a topping slab covers them."},
      "span_direction": {"type": "string", "enum": ["x", "y", "auto"], "description": "Direction the planks span (their length). auto = shorter side of the area."},
      "plank_width": {"type": "number", "description": "Project units. Default 1200 mm (hollow core, solid plank) or 2400 mm (filigree, double tee)."},
      "depth": {"type": "number", "description": "Unit depth. Default 200 mm hollow core (150-400 common), 150 solid plank, 60 filigree, 500 double tee."},
      "joint": {"type": "number", "description": "Gap between planks, default 10 mm."},
      "start_from": {"type": "string", "enum": ["min", "max", "center"], "description": "Side where full planks start; the cut plank lands on the other side (center: cuts split both sides)."},
      "original": {"type": "string", "enum": ["keep", "delete"], "description": "What to do with floor_id afterwards. Default keep."},
      "mark_prefix": {"type": "string", "description": "Default 'HC-', 'SP-', 'FP-' or 'DT-' by kind."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        double Mm(double v) => UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Millimeters);
        var kind = args.OptString("kind") ?? "hollow_core";
        var (defWidth, defDepth, defPrefix, label) = kind switch
        {
            "solid_plank" => (1200.0, 150.0, "SP-", "Solid Precast Plank"),
            "filigree" => (2400.0, 60.0, "FP-", "Filigree Plank"),
            "double_tee" => (2400.0, 500.0, "DT-", "Double Tee"),
            "hollow_core" => (1200.0, 200.0, "HC-", "Hollow Core Plank"),
            _ => throw new ArgumentException($"Unknown kind '{kind}'."),
        };
        var width = args.OptDouble("plank_width") is { } pw ? RevitHelpers.ToInternal(doc, pw) : Mm(defWidth);
        var depth = args.OptDouble("depth") is { } d ? RevitHelpers.ToInternal(doc, d) : Mm(defDepth);
        var topping = args.OptDouble("topping") is { } tp ? RevitHelpers.ToInternal(doc, tp) : 0;
        if (topping < 0) throw new ArgumentException("topping must be a positive thickness.");
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
        var swallow = new TypeKit.SwallowWarnings(doc);
        failure.SetFailuresPreprocessor(swallow);
        t.SetFailureHandlingOptions(failure);

        var type = PlankType(doc, depth, label);
        var prefix = args.OptString("mark_prefix") ?? defPrefix;
        var levelOffset = original?.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;
        // Floors hang down from their top; with a topping the units sit one topping lower.
        double? heightOffset = topping > 0 || original != null ? levelOffset - topping : null;
        var planks = new List<(string Mark, long Id, double Length, double Width, bool Cut)>();
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
                var loops = piece.Select(l => CurveLoop.CreateViaTransform(l, Transform.CreateTranslation(new XYZ(0, 0, level.ProjectElevation)))).ToList();
                Floor plank;
                try { plank = Floor.Create(doc, loops, type.Id, level.Id, true, null, 0); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
                n++;
                plank.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set($"{prefix}{n:00}");
                if (heightOffset is { } ho) plank.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(ho);
                var pieceBox = Outlines.Extrude(piece).GetBoundingBox();
                var length = spanAlongX ? pieceBox.Max.X - pieceBox.Min.X : pieceBox.Max.Y - pieceBox.Min.Y;
                var plankWidth = to - from;
                plank.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "precast unit; length={0:0.###}; width={1:0.###}", UnitUtils.ConvertFromInternalUnits(length, UnitTypeId.Meters), UnitUtils.ConvertFromInternalUnits(plankWidth, UnitTypeId.Meters)));
                planks.Add(($"{prefix}{n:00}", plank.Id.Value, length, plankWidth, plankWidth < width - Mm(1)));
            }
        }
        long? toppingId = null;
        string? toppingError = null;
        if (topping > 0 && planks.Count > 0)
        {
            try
            {
                var slab = Floor.Create(doc, area.Select(l => CurveLoop.CreateViaTransform(l, Transform.CreateTranslation(new XYZ(0, 0, level.ProjectElevation - l.First().GetEndPoint(0).Z)))).ToList(),
                    PlankType(doc, topping, "Structural Topping").Id, level.Id, true, null, 0);
                slab.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(levelOffset);
                slab.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set($"{prefix}TOPPING");
                toppingId = slab.Id.Value;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { toppingError = "The topping slab couldn't be made: " + ex.Message.Trim(); }
        }
        if (original != null && args.OptString("original") == "delete") doc.Delete(original.Id);
        t.Commit();
        var kept = planks.Where(p => !swallow.Deleted.Contains(p.Id)).ToList();
        return ToolResult.Ok(new
        {
            kind, planks = kept.Count, type = type.Name, toppingId, toppingError,
            warning = topping > 0 && original != null && args.OptString("original") != "delete"
                ? "The original slab was kept under the new units and topping, so concrete is doubled there. Run again with original 'delete', or delete it." : null, spanDirection = spanAlongX ? "x" : "y", cutPlanks = kept.Count(p => p.Cut),
            list = kept.Select(p => new { mark = p.Mark, id = p.Id, length = RevitHelpers.FromInternal(doc, p.Length), width = RevitHelpers.FromInternal(doc, p.Width), cut = p.Cut }),
            removedByRevit = swallow.Deleted,
        });
    }

    /// <summary>A floor type named for the plank depth, made from the first non-foundation floor type with its structure set to that depth.</summary>
    internal static FloorType PlankType(Document doc, double depth, string label)
    {
        var mm = Math.Round(UnitUtils.ConvertFromInternalUnits(depth, UnitTypeId.Millimeters));
        var name = $"{label} {mm}mm";
        var types = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
        // Precast units and toppings are floors, not structural foundations: never reuse or copy a foundation slab type.
        if (types.FirstOrDefault(x => x.Name == name && !x.IsFoundationSlab) is { } existing) return existing;
        if (types.Any(x => x.Name == name))
        {
            name += " (Floor)";
            if (types.FirstOrDefault(x => x.Name == name && !x.IsFoundationSlab) is { } renamed) return renamed;
        }
        var floorTypes = types.Where(x => !x.IsFoundationSlab).ToList();
        if (floorTypes.Count == 0) throw new InvalidOperationException("This model has no floor types that aren't foundation slabs. Load or create one first.");
        var basis = floorTypes.FirstOrDefault(x => x.Name.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) >= 0) ?? floorTypes[0];
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
