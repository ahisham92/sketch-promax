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
        "elements, tags and schedules stay connected (unchanged edges keep their sketch lines). If Revit refuses the sketch edit, a cut " +
        "falls back to an opening of the same shape and the result says so. For a hole fully inside the slab, create_mep_openings can make an opening instead.";
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
        var target = pieces[0].SelectMany(l => l).Select(c => c.CreateTransformed(lift)).ToList();
        var opName = op == BooleanOperationsType.Union ? "add" : "cut";

        string? refused = null;
        int kept = 0, changed = 0, added = 0, removed = 0;
        // A structural floor's span direction, to put back if the edit moved it (it throws on a non-structural floor).
        double? span = null;
        try { if (floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1) span = floor.SpanDirectionAngle; }
        catch (Exception) { span = null; }
        // This attempt reports its own outcome (and falls back to an opening), so the failures Revit raises here must
        // not also mark the whole call as refused.
        FailureGuard.Muted++;
        try
        {
        using (var scope = new SketchEditScope(doc, "Sketch ProMax: Cut slab"))
        {
            try
            {
                scope.Start(floor.SketchId);
                using (var t = new Transaction(doc, "Sketch ProMax: slab outline"))
                {
                    t.Start();
                    (kept, changed, added, removed) = Reshape(doc, sketch, target);
                    t.Commit();
                }
                // Warnings are dismissed; an error the edit can't settle (e.g. an element hosted on a removed edge)
                // makes Revit refuse the commit, and the cut falls back to an opening below.
                scope.Commit(new SketchCommitFailures());
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                refused = ex.Message.Trim();
                if (scope.IsActive) scope.Cancel();
            }
        }
        }
        finally { FailureGuard.Muted--; }
        if (refused is null)
        {
            if (span is { } angle) KeepSpan(doc, floor, angle);
            return ToolResult.Ok(new { floor = floor.Id.Value, operation = opName, loops = pieces[0].Count, method = "sketch edited",
                edges = new { kept, changed, added, removed } });
        }

        if (op == BooleanOperationsType.Union)
            return ToolResult.Error($"Revit refused to edit the floor's sketch ({refused}), so nothing was added. Check elements hosted on the slab " +
                                    "edges (slab edges, railings, rebar) and edit the boundary in Revit (Edit Boundary) if it keeps refusing.");

        // Fallback for a cut: an opening shaped like the part of the region that overlaps the floor.
        var overlap = Outlines.Pieces(BooleanOperationsUtils.ExecuteBooleanOperation(Outlines.Extrude(current), Outlines.Extrude(region), BooleanOperationsType.Intersect));
        if (overlap.Count == 0) return ToolResult.Error($"Revit refused to edit the floor's sketch ({refused}) and the region doesn't overlap the floor.");
        var openings = new List<long>();
        using (var t = RevitHelpers.StartTransaction(doc, "Cut slab (opening)"))
        {
            try
            {
                foreach (var piece in overlap)
                {
                    var profile = new CurveArray();
                    foreach (var c in piece.OrderByDescending(l => l.GetExactLength()).First()) profile.Append(c.CreateTransformed(lift));
                    openings.Add(doc.Create.NewOpening(floor, profile, true).Id.Value);
                }
                t.Commit();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                return ToolResult.Error($"Revit refused both the sketch edit ({refused}) and an opening in its place ({ex.Message.Trim()}), so the floor is unchanged.");
            }
        }
        openings = openings.Where(i => doc.GetElement(new ElementId(i)) is not null).ToList();
        if (openings.Count == 0)
            return ToolResult.Error($"Revit refused both the sketch edit ({refused}) and an opening in its place, so the floor is unchanged.");
        return ToolResult.Ok(new
        {
            floor = floor.Id.Value, operation = opName, method = "opening", openings,
            note = $"Revit refused to edit the floor's own sketch ({refused}), so the region was cut with an opening instead. " +
                   "The floor's outline (and its area in schedules) is unchanged; edit the boundary in Revit if the outline itself must change.",
        });
    }

    /// <summary>Sets a structural floor's span direction back when the sketch edit changed it. Best effort.</summary>
    private static void KeepSpan(Document doc, Floor floor, double angle)
    {
        try
        {
            if (Math.Abs(Math.IEEERemainder(floor.SpanDirectionAngle - angle, Math.PI)) < 1e-6) return;
            using var t = new Transaction(doc, "Sketch ProMax: keep span direction");
            var options = t.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new TypeKit.SwallowWarnings());
            t.SetFailureHandlingOptions(options);
            t.Start();
            floor.SpanDirectionAngle = angle;
            if (t.Commit() != TransactionStatus.Committed && t.HasStarted() && !t.HasEnded()) t.RollBack();
        }
        catch (Exception) { /* the outline is what was asked for; the span direction stays where Revit put it */ }
    }

    /// <summary>
    /// Turns the sketch's boundary lines into the target outline while keeping as many of them as possible: a line that
    /// is already right is kept, a line on the same straight edge is shortened or lengthened in place, and a line whose
    /// edge is gone is moved onto the nearest new parallel edge before anything is deleted, so slab edges and other
    /// things hosted on a floor edge stay attached. Only the remaining curves are deleted or drawn new.
    /// A floor's sketch also holds curves that are not boundary lines (Revit 2024 field test: a 4-sided floor's sketch
    /// has 5 curve elements; the extra one carries the span direction of a structural floor). Those are never deleted:
    /// deleting it made Revit refuse the edit ("Structural floor span direction not specified"). When the edge it lies
    /// on changes, it follows that edge.
    /// </summary>
    private static (int kept, int changed, int added, int removed) Reshape(Document doc, Sketch sketch, List<Curve> target)
    {
        const double Tol = 1e-4;
        var all = sketch.GetAllElements().Select(doc.GetElement).OfType<CurveElement>().ToList();
        var boundary = BoundaryLines(sketch, all);
        var others = all.Where(e => !boundary.Contains(e)).ToList();
        // Where each non-boundary line sits before the edit: the boundary line it lies on, if any.
        var sitsOn = new List<(CurveElement e, Line g, CurveElement? on)>();
        foreach (var e in others)
            if (e.GeometryCurve is Line g)
                sitsOn.Add((e, g, boundary.FirstOrDefault(b => b.GeometryCurve is Line bl && Collinear(bl, g, Tol) && Overlaps(bl, g))));

        var unused = new List<CurveElement>(boundary);
        var todo = new List<Curve>();
        bool Same(XYZ a, XYZ b) => a.DistanceTo(b) < Tol;
        int kept = 0, changed = 0, added = 0;
        foreach (var c in target)
        {
            var match = unused.FirstOrDefault(e => e.GeometryCurve is { } g && g.GetType() == c.GetType() &&
                ((Same(g.GetEndPoint(0), c.GetEndPoint(0)) && Same(g.GetEndPoint(1), c.GetEndPoint(1))) ||
                 (Same(g.GetEndPoint(0), c.GetEndPoint(1)) && Same(g.GetEndPoint(1), c.GetEndPoint(0)))) &&
                (c is Line || Same(g.Evaluate(0.5, true), c.Evaluate(0.5, true))));
            if (match is null) { todo.Add(c); continue; }
            unused.Remove(match);
            kept++;
        }
        var fresh = new List<Curve>();
        var reshaped = new List<(CurveElement e, Curve c)>();
        // First the lines that stay on their own edge (shortened or lengthened)...
        foreach (var c in todo)
        {
            CurveElement? on = null;
            if (c is Line l)
                on = unused.FirstOrDefault(e => e.GeometryCurve is Line g && Collinear(g, l, Tol) && Overlaps(g, l));
            if (on is null) { fresh.Add(c); continue; }
            unused.Remove(on);
            reshaped.Add((on, c));
        }
        // ...then a line whose edge is gone moves to the nearest new parallel edge (a set-back keeps its slab edge).
        foreach (var c in fresh.ToList())
        {
            if (c is not Line l) continue;
            var move = unused.Where(e => e.GeometryCurve is Line g && DimensionGeometry.IsParallel(g.Direction, l.Direction))
                .OrderBy(e => Distance((Line)e.GeometryCurve, l)).FirstOrDefault();
            if (move is null) continue;
            unused.Remove(move);
            fresh.Remove(c);
            reshaped.Add((move, c));
        }

        var removed = unused.Count;
        if (unused.Count > 0) doc.Delete(unused.Select(e => e.Id).ToList());
        foreach (var (e, c) in reshaped) { e.SetGeometryCurve(c, true); changed++; }
        foreach (var c in fresh) { doc.Create.NewModelCurve(c, sketch.SketchPlane); added++; }

        // The span direction (and any other non-boundary line on an edge) follows its edge.
        var targetLines = target.OfType<Line>().ToList();
        foreach (var (e, g, on) in sitsOn)
        {
            if (on is null || !e.IsValidObject) continue;
            Line? edge = null;
            if (on.IsValidObject && on.GeometryCurve is Line now) edge = now;
            else
                edge = targetLines.Where(t => DimensionGeometry.IsParallel(t.Direction, g.Direction)).OrderBy(t => Distance(t, g)).FirstOrDefault()
                       ?? targetLines.OrderByDescending(t => t.Length).FirstOrDefault();
            if (edge is null || Within(g, edge, Tol)) continue;
            try { e.SetGeometryCurve(Line.CreateBound(edge.GetEndPoint(0), edge.GetEndPoint(1)), true); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* Revit settles it at commit, or refuses the edit */ }
        }
        return (kept, changed, added, removed);
    }

    /// <summary>
    /// The sketch's boundary lines: the curve elements behind Sketch.Profile (each profile curve's Reference names its
    /// ModelCurve). When those references are missing, every curve element except span direction or slope arrow lines.
    /// </summary>
    private static HashSet<CurveElement> BoundaryLines(Sketch sketch, List<CurveElement> all)
    {
        var ids = new HashSet<long>();
        var curves = 0;
        try
        {
            foreach (CurveArray array in sketch.Profile)
                foreach (Curve c in array)
                {
                    curves++;
                    if (c.Reference?.ElementId is { } id && id != ElementId.InvalidElementId) ids.Add(id.Value);
                }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { ids.Clear(); }
        var byRef = all.Where(e => ids.Contains(e.Id.Value)).ToList();
        if (byRef.Count > 0 && byRef.Count >= curves) return new HashSet<CurveElement>(byRef);
        return new HashSet<CurveElement>(all.Where(e => !NotBoundaryByName(e)));
    }

    private static bool NotBoundaryByName(CurveElement e)
    {
        string name;
        try { name = (e.LineStyle?.Name ?? "") + " " + (e.Category?.Name ?? ""); }
        catch (Exception) { return false; }
        return name.IndexOf("span", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("slope", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool Collinear(Line a, Line b, double tol) =>
        DimensionGeometry.IsParallel(a.Direction, b.Direction) &&
        (b.GetEndPoint(0) - a.GetEndPoint(0)).CrossProduct(a.Direction).GetLength() < tol;

    /// <summary>Distance between two parallel lines (from b's start to a's infinite line).</summary>
    private static double Distance(Line a, Line b) => (b.GetEndPoint(0) - a.GetEndPoint(0)).CrossProduct(a.Direction).GetLength();

    /// <summary>Line g lies on line edge, within its ends.</summary>
    private static bool Within(Line g, Line edge, double tol)
    {
        if (!Collinear(edge, g, tol)) return false;
        var d = edge.Direction;
        double e0 = edge.GetEndPoint(0).DotProduct(d), e1 = edge.GetEndPoint(1).DotProduct(d);
        double lo = Math.Min(e0, e1) - tol, hi = Math.Max(e0, e1) + tol;
        double g0 = g.GetEndPoint(0).DotProduct(d), g1 = g.GetEndPoint(1).DotProduct(d);
        return g0 >= lo && g0 <= hi && g1 >= lo && g1 <= hi;
    }

    /// <summary>Two collinear lines share some length.</summary>
    private static bool Overlaps(Line a, Line b)
    {
        var d = a.Direction;
        double a0 = a.GetEndPoint(0).DotProduct(d), a1 = a.GetEndPoint(1).DotProduct(d);
        double b0 = b.GetEndPoint(0).DotProduct(d), b1 = b.GetEndPoint(1).DotProduct(d);
        return Math.Min(Math.Max(a0, a1), Math.Max(b0, b1)) - Math.Max(Math.Min(a0, a1), Math.Min(b0, b1)) > 1e-4;
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

/// <summary>
/// Commits a floor sketch edit: dismisses warnings, and settles "Structural floor span direction not specified" (raised
/// when the edge that carried the span direction was redrawn) with Revit's own resolution, which puts the span
/// direction back on an edge. Any other error still makes Revit refuse the edit.
/// The resolution is tried once: Revit runs this again after it, and a failure that is still there is refused at once.
/// (0.28.1 resolved it on every pass; in the Revit 2024 field test the edit went round for 14 s before Revit gave up.)
/// </summary>
internal sealed class SketchCommitFailures : IFailuresPreprocessor
{
    private int _passes;

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        _passes++;
        bool resolved = false, unresolved = false;
        foreach (var f in accessor.GetFailureMessages())
        {
            if (f.GetSeverity() == FailureSeverity.Warning) { accessor.DeleteWarning(f); continue; }
            var text = f.GetDescriptionText() ?? "";
            if (_passes == 1 && text.IndexOf("span direction", StringComparison.OrdinalIgnoreCase) >= 0 && f.HasResolutions())
            {
                accessor.ResolveFailure(f);
                resolved = true;
            }
            else unresolved = true;
        }
        if (unresolved)
        {
            // Refuse quietly (no dialog): the commit throws and the tool falls back to an opening.
            var options = accessor.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(false);
            accessor.SetFailureHandlingOptions(options);
            return FailureProcessingResult.ProceedWithRollBack;
        }
        return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
    }
}
