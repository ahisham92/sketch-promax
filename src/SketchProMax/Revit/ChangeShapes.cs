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
        _ => null,
    };

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
        approx = true;
        var points = curve.Tessellate();
        var segs = new List<M.Seg>();
        for (var i = 1; i < points.Count; i++) segs.Add(new M.Seg(P(points[i - 1]), P(points[i]), null, slope));
        return segs;
    }

    public static Curve ToCurve(M.Seg s) => s.M is { } m ? Arc.Create(X(s.P), X(s.Q), X(m)) : Line.CreateBound(X(s.P), X(s.Q));

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
            var id = e switch { Floor f => f.SketchId, Ceiling c => c.SketchId, Toposolid t => t.SketchId, _ => ElementId.InvalidElementId };
            if (id != ElementId.InvalidElementId && doc.GetElement(id) is Sketch direct) return direct;
            return e.GetDependentElements(new ElementClassFilter(typeof(Sketch))).Select(doc.GetElement).OfType<Sketch>().FirstOrDefault();
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
                if (curve is ModelCurve && curve.SketchPlane?.GetPlane() is { } plane) { line.PlaneOrigin = P(plane.Origin); line.PlaneNormal = P(plane.Normal); }
                if (curve is DetailCurve) line.View = doc.GetElement(curve.OwnerViewId)?.UniqueId;
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
                    // A slope arrow (or other extra sketch element) isn't a boundary edge.
                    try { line.SlopeArrow = sketch.GetAllElements().Count > count; } catch (Exception) { }
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
        "modelcurve" when line.P is not null && line.Q is not null && line.PlaneNormal is not null => null,
        "detailcurve" when line.P is not null && line.Q is not null && line.View is not null => null,
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
                return Floor.Create(doc, CurveLoops(line.Loops!), TypeId(), Need().Id, structural, null, 0);
            case "ceiling":
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
                var made = doc.Create.NewModelCurve(ToCurve(new M.Seg(line.P!, line.Q!, line.M)), SketchPlane.Create(doc, plane));
                Style(doc, made, line);
                return made;
            }
            case "detailcurve":
            {
                if (find(line.View) is not View view2) throw new InvalidOperationException("the view it was drawn in isn't in this model");
                var made = doc.Create.NewDetailCurve(view2, ToCurve(new M.Seg(line.P!, line.Q!, line.M)));
                Style(doc, made, line);
                return made;
            }
            default:
                return null;
        }
    }

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
        Read(doc, e, now);
        if (line.Loops is not null && !M.SameLoops(now.Loops, line.Loops))
        {
            if (e is Railing railing) { railing.SetPath(CurveLoops(line.Loops).First()); changed = true; }
            // Only a roof's slopes changed: no sketch edit needed.
            else if (e is FootPrintRoof roof && now.Loops is not null && M.SameLoops(Unsloped(now.Loops), Unsloped(line.Loops))) { Slopes(roof, line); changed = true; }
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
        editor.ResetSlabShape();
        if (points.Count == 0) return;
        if (e is Toposolid) { editor.AddPoints(points.Select(X).ToList()); return; }
        if (!editor.IsEnabled) editor.Enable();
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
    private sealed class Quiet : IFailuresPreprocessor
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
