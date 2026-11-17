using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Sketches and shapes in the change file: reading the boundary of floors, ceilings, roofs (with each edge's slope),
/// openings, filled regions, toposolids and walls with an edited profile, a railing's path, model and detail lines, and
/// the points of a shape-edited floor, roof or toposolid; and putting them back (making the element again from its
/// sketch, editing an existing sketch in a sketch edit scope, setting points and paths).
/// </summary>
internal static class ChangeShapes
{
    /// <summary>Points kept of a shape-edited slab (a toposolid can have thousands; the file stays small).</summary>
    public const int MaxPoints = 2000;

    /// <summary>What the change file calls the element, sketch-based kinds first.</summary>
    public static string? Kind(Element e) => e switch
    {
        FootPrintRoof => "roof",
        RoofBase => "roof-other",
        Floor => "floor",
        Ceiling => "ceiling",
        Toposolid => "topo",
        Opening => "opening",
        FilledRegion => "region",
        Railing => "railing",
        ModelCurve => "modelcurve",
        DetailCurve => "detailcurve",
        // A spline through points drawn in a view has no class of its own in Revit: it is a plain curve element.
        CurveElement { ViewSpecific: true } when IsPlainLine(e) => "detailcurve",
        CurveElement when IsPlainLine(e) => "modelcurve",
        _ => null,
    };

    private static bool IsPlainLine(Element e) =>
        e.GetType() == typeof(CurveElement) && e.Category?.Id.Value == (long)BuiltInCategory.OST_Lines;

    /// <summary>True for a line element whose curve is a line or an arc with two ends (kept as P, Q and M).</summary>
    private static bool Simple(Curve? c) => c is Line or Arc { IsBound: true };

    /// <summary>Lines that belong to a sketch are part of their element's line, never lines of their own.</summary>
    public static bool IsSketchLine(Element e) =>
        e is CurveElement && e.Category?.Id.Value == (long)BuiltInCategory.OST_SketchLines;

    // ---- Curves ------------------------------------------------------------------------------------------------------

    public static M.Xyz P(XYZ p) => new(M.Round(p.X), M.Round(p.Y), M.Round(p.Z));

    public static XYZ X(M.Xyz p) => new(p.X, p.Y, p.Z);

    /// <summary>A curve as edges: a line or an arc as one, anything else as short lines along it (approx set).</summary>
    public static IEnumerable<M.Seg> Segs(Curve curve, ref bool approx, double? slope = null)
    {
        if (curve is Line line) return [new M.Seg(P(line.GetEndPoint(0)), P(line.GetEndPoint(1)), null, slope)];
        if (curve is Arc { IsBound: true } arc) return [new M.Seg(P(arc.GetEndPoint(0)), P(arc.GetEndPoint(1)), P(arc.Evaluate(0.5, true)), slope)];
        // Splines, ellipses and whole circles exactly, by their own numbers.
        if (Exact(curve) is { } exact)
        {
            var (from, to) = curve.IsBound ? (curve.GetEndPoint(0), curve.GetEndPoint(1)) : (curve.Evaluate(0, false), curve.Evaluate(0, false));
            return [new M.Seg(P(from), P(to), null, slope, exact)];
        }
        approx = true;
        var points = curve.Tessellate();
        var segs = new List<M.Seg>();
        for (var i = 1; i < points.Count; i++) segs.Add(new M.Seg(P(points[i - 1]), P(points[i]), null, slope));
        return segs;
    }

    public static Curve ToCurve(M.Seg s)
    {
        if (s.C is { } exact)
            try { if (FromExact(exact) is { } curve) return curve; } catch (Exception) { /* numbers Revit won't take: a straight line between its ends */ }
        return s.M is { } m ? Arc.Create(X(s.P), X(s.Q), X(m)) : Line.CreateBound(X(s.P), X(s.Q));
    }

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static string N(double d) => d.ToString("G12", Inv);

    private static string V(XYZ p) => $"{N(p.X)},{N(p.Y)},{N(p.Z)}";

    private static double D(string text) => double.Parse(text, System.Globalization.NumberStyles.Float, Inv);

    private static XYZ Xyz(string text)
    {
        var parts = text.Split(',');
        return new XYZ(D(parts[0]), D(parts[1]), D(parts[2]));
    }

    /// <summary>The most control points of a spline that are written (longer ones come back as short straight lines).</summary>
    private const int MaxSplinePoints = 400;

    /// <summary>
    /// A curve that is neither a line nor a bound arc, as text: A a whole circle (centre, radius, x and y directions);
    /// E an ellipse (centre, the two radii, directions, where it starts and ends); H a spline through points (closed or
    /// not, the points, its tangents at the ends); N a NURBS spline (degree, knots, weights, control points).
    /// </summary>
    private static string? Exact(Curve curve)
    {
        try
        {
            switch (curve)
            {
                case Arc { IsBound: false } circle:
                    return $"A|{V(circle.Center)}|{N(circle.Radius)}|{V(circle.XDirection)}|{V(circle.YDirection)}";
                case Ellipse e:
                    var (t0, t1) = e.IsBound ? (e.GetEndParameter(0), e.GetEndParameter(1)) : (0.0, 2 * Math.PI);
                    return $"E|{V(e.Center)}|{N(e.RadiusX)}|{N(e.RadiusY)}|{V(e.XDirection)}|{V(e.YDirection)}|{N(t0)}|{N(t1)}";
                case HermiteSpline h when h.ControlPoints.Count is >= 2 and <= MaxSplinePoints:
                {
                    // Written the way that draws the same curve again (checked here, in Revit 2025 the end tangents
                    // alone drew a spline up to 50 mm off): through its points only, else with its end tangents, else
                    // as the NURBS spline Revit says is the same curve.
                    var points = string.Join(";", h.ControlPoints.Select(V));
                    var tangents = h.Tangents;
                    foreach (var text in new[] { $"H|{(h.IsPeriodic ? 1 : 0)}|{points}||", $"H|0|{points}|{V(tangents[0])}|{V(tangents[tangents.Count - 1])}" })
                        try { if (FromExact(text) is { } again && SameCurve(h, again)) return text; } catch (Exception) { }
                    return NurbSpline.Create(h) is { } same && SameCurve(h, same) ? Exact(same) : null;
                }
                case NurbSpline n when n.CtrlPoints.Count is >= 2 and <= MaxSplinePoints:
                    return $"N|{n.Degree}|{string.Join(",", n.Knots.Cast<double>().Select(N))}|{(n.isRational ? string.Join(",", n.Weights.Cast<double>().Select(N)) : "")}|{string.Join(";", n.CtrlPoints.Select(V))}";
                default:
                    return null;
            }
        }
        catch (Exception) { return null; }
    }

    /// <summary>True when the two bound curves run through the same points (sampled along their length).</summary>
    private static bool SameCurve(Curve a, Curve b)
    {
        if (!a.IsBound || !b.IsBound || Math.Abs(a.Length - b.Length) > 1e-6) return false;
        for (var i = 0; i <= 16; i++)
            if (a.Evaluate(i / 16.0, true).DistanceTo(b.Evaluate(i / 16.0, true)) > 1e-6) return false;
        return true;
    }

    private static Curve? FromExact(string text)
    {
        var p = text.Split('|');
        switch (p[0])
        {
            case "A":
                return Arc.Create(Xyz(p[1]), D(p[2]), 0, 2 * Math.PI, Xyz(p[3]).Normalize(), Xyz(p[4]).Normalize());
            case "E":
                return Ellipse.CreateCurve(Xyz(p[1]), D(p[2]), D(p[3]), Xyz(p[4]).Normalize(), Xyz(p[5]).Normalize(), D(p[6]), D(p[7]));
            case "H":
            {
                var points = p[2].Split(';').Select(Xyz).ToList();
                if (p[1] == "1") return HermiteSpline.Create(points, true);
                if (p.Length < 5 || p[3].Length == 0) return HermiteSpline.Create(points, false);
                var tangents = new HermiteSplineTangents { StartTangent = Xyz(p[3]).Normalize(), EndTangent = Xyz(p[4]).Normalize() };
                return HermiteSpline.Create(points, false, tangents);
            }
            case "N":
            {
                var knots = p[2].Split(',').Select(D).ToList();
                var points = p[4].Split(';').Select(Xyz).ToList();
                return p[3].Length > 0 ? NurbSpline.CreateCurve(int.Parse(p[1], Inv), knots, points, p[3].Split(',').Select(D).ToList()) : NurbSpline.CreateCurve(int.Parse(p[1], Inv), knots, points);
            }
            default:
                return null;
        }
    }

    private static List<M.Seg> Loop(IEnumerable<Curve> curves, ref bool approx)
    {
        var loop = new List<M.Seg>();
        foreach (var c in curves) loop.AddRange(Segs(c, ref approx));
        return loop;
    }

    public static List<CurveLoop> CurveLoops(List<List<M.Seg>> loops) =>
        loops.Select(l => CurveLoop.Create(l.Select(ToCurve).ToList())).ToList();

    private static CurveArray Array(IEnumerable<M.Seg> segs)
    {
        var array = new CurveArray();
        foreach (var s in segs) array.Append(ToCurve(s));
        return array;
    }

    /// <summary>The sketch an element is drawn from, if it has one.</summary>
    public static Sketch? SketchOf(Document doc, Element e)
    {
        try
        {
            var id = e switch { Floor f => f.SketchId, Ceiling c => c.SketchId, Toposolid t => t.SketchId, Wall w => w.SketchId, _ => ElementId.InvalidElementId };
            if (id != ElementId.InvalidElementId && doc.GetElement(id) is Sketch direct) return direct;
            // Only elements drawn from a sketch of their own. A type or a view also "depends" on the sketches of the
            // elements of that type or in that view: those are never its sketch (they gave types and views an outline
            // that a replay then tried to redraw).
            if (e is not (RoofBase or RevisionCloud or Autodesk.Revit.DB.Structure.AreaReinforcement or Autodesk.Revit.DB.Structure.PathReinforcement)) return null;
            return e.GetDependentElements(new ElementClassFilter(typeof(Sketch))).Select(doc.GetElement).OfType<Sketch>().OrderByDescending(s => s.OwnerId == e.Id).FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    private static SlabShapeEditor? Editor(Element e) => e switch
    {
        Floor f => f.GetSlabShapeEditor(),
        RoofBase r => r.GetSlabShapeEditor(),
        Toposolid t => t.GetSlabShapeEditor(),
        _ => null,
    };

    // ---- Reading -------------------------------------------------------------------------------------------------------

    /// <summary>Adds the element's sketch, path, lines and shape points to its line.</summary>
    public static void Read(Document doc, Element e, M.Line line)
    {
        var approx = false;
        switch (e)
        {
            case FootPrintRoof roof:
                var loops = new List<List<M.Seg>>();
                foreach (ModelCurveArray profile in roof.GetProfiles())
                {
                    var loop = new List<M.Seg>();
                    foreach (ModelCurve mc in profile)
                        loop.AddRange(Segs(mc.GeometryCurve, ref approx, roof.get_DefinesSlope(mc) ? M.Round(roof.get_SlopeAngle(mc)) : null));
                    loops.Add(loop);
                }
                line.Loops = loops;
                break;
            case Railing railing:
                line.Loops = [Loop(railing.GetPath(), ref approx)];
                if (railing.HostId != ElementId.InvalidElementId) line.Host = doc.GetElement(railing.HostId)?.UniqueId;
                break;
            case FilledRegion region:
                line.Loops = region.GetBoundaries().Select(l => Loop(l, ref approx)).ToList();
                line.View = doc.GetElement(region.OwnerViewId)?.UniqueId;
                break;
            case Opening opening:
                line.Host = opening.Host?.UniqueId;
                if (opening.IsRectBoundary && opening.BoundaryRect is { Count: 2 } rect) { line.P = P(rect[0]); line.Q = P(rect[1]); }
                else line.Loops = [Loop(opening.BoundaryCurves.Cast<Curve>(), ref approx)];
                if (opening.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() is { } top && doc.GetElement(top) is Level topLevel) line.TopLevel = topLevel.UniqueId;
                break;
            case CurveElement curve:
                line.LineStyle = (curve.LineStyle as GraphicsStyle)?.Name;
                if (!curve.ViewSpecific && curve.SketchPlane?.GetPlane() is { } plane) { line.PlaneOrigin = P(plane.Origin); line.PlaneNormal = P(plane.Normal); }
                if (curve.ViewSpecific) line.View = doc.GetElement(curve.OwnerViewId)?.UniqueId;
                // A spline, an ellipse or a whole circle: its own numbers (a line or an arc is P, Q and M already).
                if (Kind(curve) is not null && curve.GeometryCurve is { } own && !Simple(own))
                {
                    var whole = Segs(own, ref approx).ToList();
                    line.Loops = [whole];
                    (line.P, line.Q, line.M) = (whole[0].P, whole[^1].Q, null);
                }
                break;
            default:
                if (SketchOf(doc, e) is { } sketch)
                {
                    var profileLoops = new List<List<M.Seg>>();
                    var count = 0;
                    foreach (CurveArray array in sketch.Profile)
                    {
                        profileLoops.Add(Loop(array.Cast<Curve>(), ref approx));
                        count += array.Size;
                    }
                    line.Loops = profileLoops;
                    // A slope arrow isn't a boundary edge: its line (tail to head) and slope are kept so it is drawn again.
                    try { ReadArrow(doc, sketch, line); } catch (Exception) { }
                }
                break;
        }
        try
        {
            if (Editor(e) is { IsEnabled: true } editor && editor.SlabShapeVertices.Size > 0)
                line.Points = editor.SlabShapeVertices.Cast<SlabShapeVertex>().Take(MaxPoints).Select(v => P(v.Position)).ToList();
        }
        catch (Exception) { }
        if (approx) line.Approx = true;
    }

    // ---- Making again -----------------------------------------------------------------------------------------------------

    /// <summary>Null when the element can be made again from its line, else why not.</summary>
    public static string? CanMake(M.Line line) => line.What switch
    {
        "floor" or "ceiling" or "topo" or "region" or "railing" or "roof" when line.Loops is { Count: > 0 } && line.Type is not null => null,
        "opening" when line.Loops is { Count: > 0 } || (line.P is not null && line.Q is not null) => null,
        "modelcurve" when (line.Loops is { Count: > 0 } || (line.P is not null && line.Q is not null)) && line.PlaneNormal is not null => null,
        "detailcurve" when (line.Loops is { Count: > 0 } || (line.P is not null && line.Q is not null)) && line.View is not null => null,
        "roof-other" => "an extrusion or other non-footprint roof made after the last save: make it again by hand",
        _ => "made after the last save, but the file has no sketch for it: make it again by hand",
    };

    /// <summary>Makes a sketch-based element again; null when <paramref name="line"/> isn't one of these kinds.</summary>
    public static Element? Make(Document doc, M.Line line, ElementType? type, Level? level, Func<string?, Element?> find)
    {
        Level Need() => level ?? throw new InvalidOperationException("its level isn't in this model");
        ElementId TypeId() => type?.Id ?? throw new InvalidOperationException($"the type {line.Type} isn't in this model");
        switch (line.What)
        {
            case "floor":
                var structural = line.Values?.FirstOrDefault(v => v.Bip == (int)BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Whole == 1;
                if (ArrowLine(line) is { } floorArrow)
                    try { return Floor.Create(doc, CurveLoops(line.Loops!), TypeId(), Need().Id, structural, floorArrow, line.ArrowSlope ?? 0); }
                    catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { line.Arrow = null; /* made flat below, and listed */ }
                return Floor.Create(doc, CurveLoops(line.Loops!), TypeId(), Need().Id, structural, null, 0);
            case "ceiling":
                if (ArrowLine(line) is { } ceilingArrow)
                    try { return Ceiling.Create(doc, CurveLoops(line.Loops!), TypeId(), Need().Id, ceilingArrow, line.ArrowSlope ?? 0); }
                    catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { line.Arrow = null; }
                return Ceiling.Create(doc, CurveLoops(line.Loops!), TypeId(), Need().Id);
            case "topo":
                return line.Points is { Count: > 0 } points
                    ? Toposolid.Create(doc, CurveLoops(line.Loops!), points.Select(X).ToList(), TypeId(), Need().Id)
                    : Toposolid.Create(doc, CurveLoops(line.Loops!), TypeId(), Need().Id);
            case "region":
                var view = find(line.View) ?? throw new InvalidOperationException("the view it was drawn in isn't in this model");
                return FilledRegion.Create(doc, TypeId(), view.Id, CurveLoops(line.Loops!));
            case "railing":
                return Railing.Create(doc, CurveLoops(line.Loops!).First(), TypeId(), Need().Id);
            case "roof":
            {
                if (type is not RoofType roofType) throw new InvalidOperationException($"the roof type {line.Type} isn't in this model");
                var roof = doc.Create.NewFootPrintRoof(Array(line.Loops!.SelectMany(l => l)), Need(), roofType, out _);
                Slopes(roof, line);
                return roof;
            }
            case "opening":
            {
                var host = find(line.Host);
                if (line.Loops is null && host is Wall wall) return doc.Create.NewOpening(wall, X(line.P!), X(line.Q!));
                var curves = Array(line.Loops?.First() ?? []);
                if (host is not null) return doc.Create.NewOpening(host, curves, true);
                if (line.Host is not null) throw new InvalidOperationException("its host isn't in this model");
                var top = find(line.TopLevel) as Level ?? Need();
                return doc.Create.NewOpening(Need(), top, curves);
            }
            case "modelcurve":
            {
                var plane = Plane.CreateByNormalAndOrigin(X(line.PlaneNormal!), X(line.PlaneOrigin ?? line.P!));
                var sketchPlane = SketchPlane.Create(doc, plane);
                var made = NewLines(doc, null, LineSegs(line), c => doc.Create.NewModelCurve(c, sketchPlane));
                Style(doc, made, line);
                return made;
            }
            case "detailcurve":
            {
                if (find(line.View) is not View view2) throw new InvalidOperationException("the view it was drawn in isn't in this model");
                var made = NewLines(doc, view2, LineSegs(line), c => doc.Create.NewDetailCurve(view2, c));
                Style(doc, made, line);
                return made;
            }
            default:
                return null;
        }
    }

    /// <summary>The curve(s) of a model or detail line: its own numbers when it is a spline, an ellipse or a circle.</summary>
    private static List<M.Seg> LineSegs(M.Line line) =>
        line.Loops is { Count: > 0 } loops && loops[0].Count > 0 ? loops[0] : [new M.Seg(line.P!, line.Q!, line.M)];

    /// <summary>
    /// Draws a model or detail line. Revit gives nothing back for some curves it does draw (a spline through points in
    /// a view), so the new line is then the curve element that wasn't there before. A curve written as short straight
    /// lines (too many points to keep) comes back as those lines; the first is the element.
    /// </summary>
    private static CurveElement NewLines(Document doc, View? view, List<M.Seg> segs, Func<Curve, CurveElement?> draw)
    {
        CurveElement? first = null;
        foreach (var seg in segs)
        {
            var before = first is null ? LineIds(doc, view) : null;
            var made = draw(ToCurve(seg));
            if (made is null && before is not null)
            {
                doc.Regenerate();
                made = LineIds(doc, view).Where(id => !before.Contains(id)).Select(doc.GetElement).OfType<CurveElement>().FirstOrDefault();
            }
            first ??= made;
        }
        return first ?? throw new InvalidOperationException("Revit didn't draw the line");
    }

    private static HashSet<ElementId> LineIds(Document doc, View? view) =>
        [.. (view is null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id)).OfClass(typeof(CurveElement)).ToElementIds()];

    /// <summary>
    /// The sketch's slope arrow, when it has one: a line of the sketch that isn't a boundary edge and has the slope
    /// arrow's "Specify" value. Its slope (rise over run) is its Slope value, else worked out from its two heights.
    /// </summary>
    private static void ReadArrow(Document doc, Sketch sketch, M.Line line)
    {
        foreach (var id in sketch.GetAllElements())
        {
            if (doc.GetElement(id) is not CurveElement { GeometryCurve: Line arrow } ce) continue;
            if (ce.get_Parameter(BuiltInParameter.SPECIFY_SLOPE_OR_OFFSET) is null) continue;
            line.SlopeArrow = true;
            line.Arrow = new M.Seg(P(arrow.GetEndPoint(0)), P(arrow.GetEndPoint(1)));
            double? slope = null;
            if (ce.get_Parameter(BuiltInParameter.ROOF_SLOPE) is { HasValue: true } s) slope = s.AsDouble();
            if (slope is null or 0 && ce.get_Parameter(BuiltInParameter.SLOPE_START_HEIGHT) is { HasValue: true } tail
                && ce.get_Parameter(BuiltInParameter.SLOPE_END_HEIGHT) is { HasValue: true } head && arrow.Length > 1e-6)
            {
                double Level(BuiltInParameter p) => doc.GetElement(ce.get_Parameter(p)?.AsElementId() ?? ElementId.InvalidElementId) is Level l ? l.Elevation : 0;
                slope = (Level(BuiltInParameter.SLOPE_ARROW_LEVEL_END) + head.AsDouble() - Level(BuiltInParameter.SLOPE_ARROW_LEVEL_START) - tail.AsDouble()) / arrow.Length;
            }
            line.ArrowSlope = slope is { } value ? M.Round(value) : null;
            return;
        }
    }

    /// <summary>The slope arrow to make the element with, when the file has one with a slope.</summary>
    private static Line? ArrowLine(M.Line line) =>
        line.Arrow is { } a && line.ArrowSlope is not null && !M.Same(a.P, a.Q) ? Line.CreateBound(X(a.P), X(a.Q)) : null;

    private static void Style(Document doc, CurveElement curve, M.Line line)
    {
        if (line.LineStyle is not { } name) return;
        try
        {
            var style = curve.GetLineStyleIds().Select(doc.GetElement).OfType<GraphicsStyle>().FirstOrDefault(g => g.Name == name);
            if (style is not null) curve.LineStyle = style;
        }
        catch (Exception) { }
    }

    /// <summary>Each footprint edge's slope as in the file, matched by its ends.</summary>
    public static void Slopes(FootPrintRoof roof, M.Line line)
    {
        var segs = line.Loops?.SelectMany(l => l).ToList() ?? [];
        foreach (ModelCurveArray profile in roof.GetProfiles())
            foreach (ModelCurve mc in profile)
            {
                var c = mc.GeometryCurve;
                var seg = segs.FirstOrDefault(s => (M.Same(s.P, P(c.GetEndPoint(0))) && M.Same(s.Q, P(c.GetEndPoint(1)))) || (M.Same(s.P, P(c.GetEndPoint(1))) && M.Same(s.Q, P(c.GetEndPoint(0)))));
                if (seg is null) continue;
                roof.set_DefinesSlope(mc, seg.Slope is not null);
                if (seg.Slope is { } angle) roof.set_SlopeAngle(mc, angle);
            }
    }

    // ---- Setting back ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets the element's path and shape points back inside the item's transaction; true when anything changed. A changed
    /// sketch is returned in <paramref name="sketch"/>: it is edited afterwards, in a sketch edit scope of its own
    /// (<see cref="EditSketch"/>), which Revit doesn't allow inside a transaction.
    /// </summary>
    public static bool Set(Document doc, Element e, M.Line line, out ElementId? sketch)
    {
        sketch = null;
        var changed = false;
        var now = new M.Line();
        // An element made a moment ago has no sketch to read yet: it was made from the file, so it is as the file has it.
        try { Read(doc, e, now); } catch (Autodesk.Revit.Exceptions.ApplicationException) { now.Loops = line.Loops; }
        if (line.Loops is not null && !M.SameLoops(now.Loops, line.Loops))
        {
            if (e is Railing railing) { railing.SetPath(CurveLoops(line.Loops).First()); changed = true; }
            // Only a roof's slopes changed: no sketch edit needed.
            else if (e is FootPrintRoof roof && now.Loops is not null && M.SameLoops(Unsloped(now.Loops), Unsloped(line.Loops))) { Slopes(roof, line); changed = true; }
            else if (e is CurveElement ce && Kind(e) is not null)
            {
                // A spline, an ellipse or a circle drawn as a line: its curve set back as a whole.
                if (line.Loops is [[var only]]) { ce.SetGeometryCurve(ToCurve(only), true); changed = true; }
            }
            else if (SketchOf(doc, e) is { } s) sketch = s.Id;
        }
        if (line.Points is not null && !M.SamePoints(now.Points, line.Points)) { Points(e, line.Points); changed = true; }
        return changed;
    }

    private static List<List<M.Seg>> Unsloped(List<List<M.Seg>> loops) => loops.Select(l => l.Select(s => s with { Slope = null }).ToList()).ToList();

    /// <summary>Puts a shape-edited slab's points back: reset, then each point at its height.</summary>
    public static void Points(Element e, List<M.Xyz> points)
    {
        if (Editor(e) is not { } editor) return;
        if (editor.IsEnabled) editor.ResetSlabShape();
        if (points.Count == 0) return;
        if (!editor.IsEnabled) editor.Enable();
        if (e is Toposolid) { editor.AddPoints(points.Select(X).ToList()); return; }
        foreach (var p in points)
        {
            SlabShapeVertex? At() => editor.SlabShapeVertices.Cast<SlabShapeVertex>().FirstOrDefault(v => M.Same(v.Position.X, p.X) && M.Same(v.Position.Y, p.Y));
            var vertex = At();
            if (vertex is null)
            {
#if REVIT2024 || REVIT2025
                vertex = editor.DrawPoint(new XYZ(p.X, p.Y, 0));
#else
                editor.AddPoint(new XYZ(p.X, p.Y, 0));
                vertex = At();
#endif
            }
            if (vertex is null) continue;
            var offset = p.Z - vertex.Position.Z;
            if (Math.Abs(offset) > 3.3e-5) editor.ModifySubElement(vertex, offset);
        }
    }

    /// <summary>
    /// Redraws a sketch's boundary as in the file, in a sketch edit scope (one undo step), keeping anything in the sketch
    /// that isn't a boundary edge (a slope arrow). Call with no transaction open. Null when done, else why not.
    /// </summary>
    public static string? EditSketch(Document doc, ElementId sketchId, Element owner, M.Line line)
    {
        if (doc.GetElement(sketchId) is not Sketch sketch) return "its sketch isn't there any more";
        var profile = sketch.Profile.Cast<CurveArray>().SelectMany(a => a.Cast<Curve>()).ToList();
        var scope = new SketchEditScope(doc, "Sketch ProMax: Replay change file (sketch)");
        try
        {
            if (!scope.IsSketchEditingSupported(sketchId)) { scope.Dispose(); return "Revit doesn't let add-ins edit this kind of sketch: edit it by hand"; }
            scope.Start(sketchId);
            using (var t = new Transaction(doc, "Replay sketch"))
            {
                t.Start();
                foreach (var id in sketch.GetAllElements())
                {
                    if (doc.GetElement(id) is not CurveElement ce) continue;
                    var g = ce.GeometryCurve;
                    // Boundary edges only: an edge of the profile has the same ends.
                    if (profile.Any(c => (c.GetEndPoint(0).IsAlmostEqualTo(g.GetEndPoint(0)) && c.GetEndPoint(1).IsAlmostEqualTo(g.GetEndPoint(1)))
                                         || (c.GetEndPoint(0).IsAlmostEqualTo(g.GetEndPoint(1)) && c.GetEndPoint(1).IsAlmostEqualTo(g.GetEndPoint(0)))))
                        doc.Delete(id);
                }
                foreach (var seg in line.Loops!.SelectMany(l => l)) doc.Create.NewModelCurve(ToCurve(seg), sketch.SketchPlane);
                t.Commit();
            }
            scope.Commit(new Quiet());
        }
        catch (Exception ex)
        {
            try { if (scope.IsActive) scope.Cancel(); } catch (Exception) { }
            return "Revit refused the new sketch (" + ex.Message + "): edit it by hand";
        }
        finally { scope.Dispose(); }
        if (owner is FootPrintRoof roof && line.Loops!.Any(l => l.Any(s => s.Slope is not null)))
        {
            using var t = new Transaction(doc, "Sketch ProMax: Replay change file (roof slopes)");
            t.Start();
            try { Slopes(roof, line); t.Commit(); }
            catch (Exception) { t.RollBack(); return "the roof's outline came back but not its slopes: set them by hand"; }
        }
        return null;
    }

    /// <summary>Warnings are dismissed; an error rolls the sketch edit back.</summary>
    internal sealed class Quiet : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
                else return FailureProcessingResult.ProceedWithRollBack;
            }
            return FailureProcessingResult.Continue;
        }
    }
}
