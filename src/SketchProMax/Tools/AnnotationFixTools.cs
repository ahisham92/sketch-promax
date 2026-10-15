using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

// Annotation fixes from the 0.26 field test: references on family instances (spot elevations, dimensions), dimension
// batches that keep the good ones, dimensions the API cannot recreate, re-flowing copied tags and notes, dependent-view
// ownership of detailing, and matchlines that run past the crop.

/// <summary>
/// Faces of an element with references Revit accepts for dimensions and spot elevations in the host document.
/// Faces of family instances come from the family symbol's geometry (with the instance transform applied to the
/// geometry), because faces from GetInstanceGeometry carry references to the symbol that Revit refuses
/// ("does not lie on its reference"). Where the symbol reference has no instance part, the instance is prefixed onto
/// its stable representation.
/// </summary>
internal static class RefResolver
{
    public sealed class RefFace(Element element, Reference reference, Face face, Transform transform)
    {
        public Element Element { get; } = element;
        public Reference Ref { get; } = reference;
        public Face Face { get; } = face;
        public Transform Tr { get; } = transform;

        public XYZ NormalAt(UV uv) => Tr.OfVector(Face.ComputeNormal(uv)).Normalize();

        /// <summary>A point that is on the face (inside its boundary), near its middle, in model coordinates.</summary>
        public XYZ Middle()
        {
            var box = Face.GetBoundingBox();
            var uv = (box.Min + box.Max) / 2;
            if (Face.IsInside(uv)) return Tr.OfPoint(Face.Evaluate(uv));
            var mesh = Face.Triangulate(0);
            if (mesh is { NumTriangles: > 0 })
            {
                // The centre of the largest triangle is safely inside the face.
                MeshTriangle? best = null; double area = -1;
                for (var i = 0; i < mesh.NumTriangles; i++)
                {
                    var t = mesh.get_Triangle(i);
                    var a = (t.get_Vertex(1) - t.get_Vertex(0)).CrossProduct(t.get_Vertex(2) - t.get_Vertex(0)).GetLength();
                    if (a > area) { area = a; best = t; }
                }
                if (best != null) return Tr.OfPoint((best.get_Vertex(0) + best.get_Vertex(1) + best.get_Vertex(2)) / 3);
            }
            return Tr.OfPoint(Face.Evaluate(uv));
        }

        /// <summary>The face's highest (sign +1) or lowest (sign -1) vertex, model coordinates.</summary>
        public XYZ Extreme(int sign)
        {
            var mesh = Face.Triangulate(0);
            XYZ? best = null;
            if (mesh != null)
                foreach (var v in mesh.Vertices)
                {
                    var p = Tr.OfPoint(v);
                    if (best is null || sign * p.Z > sign * best.Z) best = p;
                }
            return best ?? Middle();
        }

        /// <summary>The point of the face closest to a model point, or null when the projection misses it.</summary>
        public XYZ? Closest(XYZ model)
        {
            var hit = Face.Project(Tr.Inverse.OfPoint(model));
            return hit is null ? null : Tr.OfPoint(hit.XYZPoint);
        }

        public UV? UvOf(XYZ model) => Face.Project(Tr.Inverse.OfPoint(model))?.UVPoint;
    }

    public static Options GeometryOptions(View? view) => new()
    {
        ComputeReferences = true,
        IncludeNonVisibleObjects = false,
        DetailLevel = view is null || view.DetailLevel == ViewDetailLevel.Undefined ? ViewDetailLevel.Fine : view.DetailLevel,
    };

    /// <summary>Every face with a usable reference. Family geometry is read from the symbol (one nesting level deep).</summary>
    public static List<RefFace> Faces(Element e, View? view)
    {
        var list = new List<RefFace>();
        var geometry = e.get_Geometry(GeometryOptions(view));
        if (geometry != null) Walk(e, geometry, Transform.Identity, inSymbol: false, list, depth: 0);
        return list;
    }

    private static void Walk(Element owner, GeometryElement geometry, Transform tr, bool inSymbol, List<RefFace> list, int depth)
    {
        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case Solid { Faces.Size: > 0 } solid:
                    foreach (Face f in solid.Faces)
                    {
                        if (f.Reference is null) continue;
                        var r = inSymbol && owner is FamilyInstance fi ? ForInstance(owner.Document, fi, f.Reference) : f.Reference;
                        list.Add(new RefFace(owner, r, f, tr));
                    }
                    break;
                case GeometryInstance gi when depth < 2:
                    Walk(owner, gi.GetSymbolGeometry(), tr.Multiply(gi.Transform), inSymbol: true, list, depth + 1);
                    break;
            }
        }
    }

    /// <summary>
    /// A reference from family symbol geometry, made to point at the instance. When Revit already gave an
    /// instance-qualified reference it is returned as is.
    /// </summary>
    public static Reference ForInstance(Document doc, FamilyInstance instance, Reference symbolRef)
    {
        string rep;
        try { rep = symbolRef.ConvertToStableRepresentation(doc); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return symbolRef; }
        if (rep.Contains(":INSTANCE:") || rep.StartsWith(instance.UniqueId, StringComparison.Ordinal)) return symbolRef;
        try { return Reference.ParseFromStableRepresentation(doc, $"{instance.UniqueId}:0:INSTANCE:{rep}"); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return symbolRef; }
    }

    /// <summary>
    /// The top (sign +1) or bottom (sign -1) face of an element for a spot elevation: floors, roofs and other host
    /// objects use Revit's own top/bottom faces; everything else (piles, footings, beams, columns, pipes) uses the
    /// face holding the highest/lowest point among faces that look up/down.
    /// </summary>
    public static List<RefFace> TopOrBottom(Element e, View? view, int sign)
    {
        if (e is HostObject host and not Wall)
        {
            try
            {
                var refs = sign > 0 ? HostObjectUtils.GetTopFaces(host) : HostObjectUtils.GetBottomFaces(host);
                var hostFaces = refs.Select(r => (r, f: e.GetGeometryObjectFromReference(r) as Face))
                    .Where(x => x.f != null).Select(x => new RefFace(e, x.r, x.f!, Transform.Identity)).ToList();
                if (hostFaces.Count > 0) return hostFaces;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* fall through to the geometry */ }
        }

        RefFace? best = null; double bestZ = 0;
        foreach (var f in Faces(e, view))
        {
            var p = f.Extreme(sign);
            var uv = f.UvOf(p);
            if (uv is null || sign * f.NormalAt(uv).Z < 0.3) continue;
            if (best is null || sign * p.Z > sign * bestZ + 1e-6) { best = f; bestZ = p.Z; }
        }
        return best is null ? [] : [best];
    }

    /// <summary>
    /// Planar faces whose normal is along m, outermost first and last (e.g. the two sides of a pile or a beam's top
    /// and bottom). Faces that are only nearly parallel (a 1% fall) are reported, since Revit refuses them in a
    /// linear dimension ("references are no longer parallel").
    /// </summary>
    public static (List<Reference> Refs, string? Problem) OuterFacesAcross(Element e, XYZ m, View? view)
    {
        var parallel = new List<(double at, Reference r)>();
        var nearly = 0; double worst = 0;
        foreach (var f in Faces(e, view))
        {
            if (f.Face is not PlanarFace pf) continue;
            var n = f.Tr.OfVector(pf.FaceNormal).Normalize();
            var dot = Math.Abs(n.DotProduct(m));
            if (dot < 0.98) continue;
            var angle = Math.Acos(Math.Min(1, dot));
            if (angle > 1e-4) { nearly++; worst = Math.Max(worst, angle); continue; }
            parallel.Add((f.Tr.OfPoint(pf.Origin).DotProduct(m), f.Ref));
        }
        string? problem = nearly > 0 && parallel.Count < 2
            ? $"{nearly} face(s) slope {Math.Tan(worst) * 100:0.##}% off this direction, so Revit can't use them in a linear dimension; use a spot elevation (add_spot_elevations) or dimension to a level/reference plane"
            : null;
        if (parallel.Count == 0) return ([], problem ?? "no flat face across this direction");
        var min = parallel.OrderBy(p => p.at).First();
        var max = parallel.OrderBy(p => p.at).Last();
        return Math.Abs(max.at - min.at) < 1e-6 ? ([min.r], problem) : ([min.r, max.r], problem);
    }
}

/// <summary>A view's crop region, flattened to the view plane, for in/out tests and clipping curves.</summary>
internal sealed class CropClip
{
    private readonly XYZ _right, _up;
    private readonly List<List<UV>> _loops;
    public View View { get; }

    private CropClip(View view, List<List<UV>> loops)
    {
        View = view; _right = view.RightDirection; _up = view.UpDirection; _loops = loops;
    }

    /// <summary>The crop of a view, or null when its crop is off (it then shows everything).</summary>
    public static CropClip? For(View view)
    {
        if (!view.CropBoxActive) return null;
        var loops = new List<List<UV>>();
        try
        {
            foreach (var loop in view.GetCropRegionShapeManager().GetCropShape())
            {
                var pts = new List<UV>();
                foreach (var c in loop)
                {
                    var tess = c.Tessellate();
                    for (var i = 0; i < tess.Count - 1; i++) pts.Add(Uv(view, tess[i]));
                }
                if (pts.Count >= 3) loops.Add(pts);
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { loops.Clear(); }
        if (loops.Count == 0)
        {
            var b = view.CropBox;
            var tr = b.Transform;
            loops.Add(new[] { new XYZ(b.Min.X, b.Min.Y, b.Min.Z), new XYZ(b.Max.X, b.Min.Y, b.Min.Z), new XYZ(b.Max.X, b.Max.Y, b.Min.Z), new XYZ(b.Min.X, b.Max.Y, b.Min.Z) }
                .Select(p => Uv(view, tr.OfPoint(p))).ToList());
        }
        return new CropClip(view, loops);
    }

    private static UV Uv(View view, XYZ p) => new(p.DotProduct(view.RightDirection), p.DotProduct(view.UpDirection));
    private UV Uv(XYZ p) => new(p.DotProduct(_right), p.DotProduct(_up));

    public bool Inside(XYZ p) => Inside(Uv(p));

    private bool Inside(UV p)
    {
        var inside = false;
        foreach (var loop in _loops)
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                var a = loop[i]; var b = loop[j];
                if ((a.V > p.V) != (b.V > p.V) && p.U < (b.U - a.U) * (p.V - a.V) / (b.V - a.V) + a.U) inside = !inside;
            }
        return inside;
    }

    /// <summary>Parameters (0..1) where a straight segment crosses this crop's boundary.</summary>
    private IEnumerable<double> Crossings(XYZ start, XYZ end)
    {
        UV p = Uv(start), q = Uv(end);
        var d = q - p;
        foreach (var loop in _loops)
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                UV a = loop[j], b = loop[i];
                var e = b - a;
                var den = d.U * e.V - d.V * e.U;
                if (Math.Abs(den) < 1e-12) continue;
                var w = a - p;
                var t = (w.U * e.V - w.V * e.U) / den;
                var s = (w.U * d.V - w.V * d.U) / den;
                if (t > 1e-9 && t < 1 - 1e-9 && s >= -1e-9 && s <= 1 + 1e-9) yield return t;
            }
    }

    /// <summary>The pieces of a bound curve inside this crop (exact for lines, sampled and refined for other curves).</summary>
    public List<Curve> Clip(Curve curve, double minLength) => ClipToAny([this], curve, minLength);

    /// <summary>The pieces of a bound curve inside any of the crops (their union).</summary>
    public static List<Curve> ClipToAny(IList<CropClip> crops, Curve curve, double minLength)
    {
        if (crops.Count == 0 || !curve.IsBound) return [curve];
        bool In(double t) => crops.Any(c => c.Inside(curve.Evaluate(t, true)));
        var breaks = new List<double> { 0, 1 };
        if (curve is Line)
        {
            foreach (var c in crops) breaks.AddRange(c.Crossings(curve.GetEndPoint(0), curve.GetEndPoint(1)));
        }
        else
        {
            const int n = 128;
            var prev = In(0);
            for (var i = 1; i <= n; i++)
            {
                double t0 = (i - 1) / (double)n, t1 = i / (double)n;
                var now = In(t1);
                if (now == prev) continue;
                for (var k = 0; k < 40; k++) { var mid = (t0 + t1) / 2; if (In(mid) == prev) t0 = mid; else t1 = mid; }
                breaks.Add((t0 + t1) / 2);
                prev = now;
            }
        }
        breaks = breaks.OrderBy(t => t).ToList();
        var kept = new List<(double a, double b)>();
        for (var i = 0; i + 1 < breaks.Count; i++)
        {
            double a = breaks[i], b = breaks[i + 1];
            if (b - a < 1e-9 || !In((a + b) / 2)) continue;
            if (kept.Count > 0 && Math.Abs(kept[kept.Count - 1].b - a) < 1e-9) kept[kept.Count - 1] = (kept[kept.Count - 1].a, b);
            else kept.Add((a, b));
        }
        if (kept.Count == 1 && kept[0].a < 1e-9 && kept[0].b > 1 - 1e-9) return [curve];
        var result = new List<Curve>();
        double p0 = curve.GetEndParameter(0), p1 = curve.GetEndParameter(1);
        foreach (var (a, b) in kept)
        {
            Curve piece;
            if (curve is Line) piece = Line.CreateBound(curve.Evaluate(a, true), curve.Evaluate(b, true));
            else { piece = curve.Clone(); piece.MakeBound(p0 + a * (p1 - p0), p0 + b * (p1 - p0)); }
            if (piece.Length >= minLength) result.Add(piece);
        }
        return result;
    }

    /// <summary>A curve flattened onto the plane detail curves of this view are drawn in.</summary>
    public static List<Curve> ToViewPlane(View view, Curve c)
    {
        XYZ P(XYZ p)
        {
            if (view is ViewPlan plan) return new XYZ(p.X, p.Y, plan.GenLevel?.ProjectElevation ?? p.Z);
            var n = view.ViewDirection.Normalize();
            return p - n * (p - view.Origin).DotProduct(n);
        }
        if (c is Line) return [Line.CreateBound(P(c.GetEndPoint(0)), P(c.GetEndPoint(1)))];
        if (c is Arc && c.IsBound)
        {
            try { return [Arc.Create(P(c.GetEndPoint(0)), P(c.GetEndPoint(1)), P(c.Evaluate(0.5, true)))]; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        var pts = c.Tessellate().Select(P).ToList();
        var list = new List<Curve>();
        for (var i = 0; i + 1 < pts.Count; i++)
            if (pts[i].DistanceTo(pts[i + 1]) > 1e-4) list.Add(Line.CreateBound(pts[i], pts[i + 1]));
        return list;
    }
}

/// <summary>
/// Detail elements created in a dependent view are owned by its primary view, so they show in the primary and in
/// every other dependent view whose crop covers them. Tools that draw in a view use this to warn, and optionally to
/// keep new detailing inside this view's crop, create it in the primary on purpose, or stop.
/// </summary>
internal sealed class DependentGuard
{
    public const string Property =
        "\"dependent_view\": {\"type\": \"string\", \"enum\": [\"warn\", \"clip_to_crop\", \"primary\", \"stop\"], \"description\": \"When the view is a dependent view (detailing created there belongs to the primary view and shows in every dependent whose crop covers it): warn (default), clip_to_crop (detail lines trimmed to this view's crop, point annotations outside it skipped), primary (create in the primary view on purpose), stop (do nothing, just explain).\"}";

    /// <summary>A tool's "properties" JSON with the dependent_view property added.</summary>
    public static string AddTo(string propertiesJson)
    {
        var end = propertiesJson.LastIndexOf('}');
        return propertiesJson.Substring(0, end).TrimEnd() + ",\n  " + Property + "\n}";
    }

    public View View { get; }
    public View? Primary { get; }
    public string Mode { get; }
    private readonly CropClip? _crop;
    private readonly List<XYZ> _points = [];
    public int Dropped { get; private set; }
    public int Trimmed { get; private set; }

    private DependentGuard(View view, View? primary, string mode)
    {
        View = view; Primary = primary; Mode = mode;
        _crop = primary != null && mode == "clip_to_crop" ? CropClip.For(view) : null;
    }

    public static DependentGuard For(Document doc, View view, string? mode)
    {
        mode ??= "warn";
        if (mode is not ("warn" or "clip_to_crop" or "primary" or "stop"))
            throw new ArgumentException("dependent_view must be warn, clip_to_crop, primary or stop.");
        var primaryId = view.GetPrimaryViewId();
        var primary = primaryId == ElementId.InvalidElementId ? null : doc.GetElement(primaryId) as View;
        var guard = new DependentGuard(view, primary, mode);
        if (primary != null && mode == "stop")
            throw new ArgumentException(guard.Explanation() + " Nothing was created (dependent_view: stop).");
        return guard;
    }

    public bool IsDependent => Primary != null;

    /// <summary>The view to create elements in.</summary>
    public View Target => Mode == "primary" && Primary != null ? Primary : View;

    /// <summary>Whether to create a point annotation here; counts the ones dropped for being outside the crop.</summary>
    public bool Keep(XYZ p)
    {
        if (_crop is null || _crop.Inside(p)) { Note(p); return true; }
        Dropped++;
        return false;
    }

    /// <summary>Whether to create an outlined annotation (e.g. a filled region): all of it must be inside the crop under clip_to_crop.</summary>
    public bool KeepAll(IList<XYZ> points)
    {
        if (_crop != null && !points.All(_crop.Inside)) { Dropped++; return false; }
        foreach (var p in points) Note(p);
        return true;
    }

    /// <summary>The parts of a detail curve to draw (all of it unless clip_to_crop).</summary>
    public List<Curve> Clip(Curve c, double minLength)
    {
        if (_crop is null) { Note(c.GetEndPoint(0)); Note(c.GetEndPoint(1)); return [c]; }
        var parts = _crop.Clip(c, minLength);
        if (parts.Count == 0) Dropped++;
        else if (parts.Count > 1 || Math.Abs(parts[0].Length - c.Length) > 1e-6) Trimmed++;
        foreach (var p in parts) { Note(p.GetEndPoint(0)); Note(p.GetEndPoint(1)); }
        return parts;
    }

    public void Note(XYZ p) { if (IsDependent && _points.Count < 2000) _points.Add(p); }

    private string Explanation() =>
        $"'{View.Name}' is a dependent view of '{Primary!.Name}'. Detail lines, detail items, text, tags and dimensions created in it " +
        "belong to the primary view, so they also show in the primary and in every other dependent view whose crop covers them.";

    /// <summary>The note for a tool result, or null when the view is not dependent.</summary>
    public object? Report()
    {
        if (Primary is null) return null;
        var doc = View.Document;
        var alsoIn = new List<string>();
        if (_points.Count > 0)
        {
            foreach (var id in Primary.GetDependentViewIds())
            {
                if (id == View.Id || doc.GetElement(id) is not View other) continue;
                var crop = CropClip.For(other);
                if (crop is null || _points.Any(crop.Inside)) alsoIn.Add(other.Name);
            }
        }
        return new
        {
            warning = Explanation(),
            primaryView = Primary.Name,
            mode = Mode,
            alsoShownInDependentViews = alsoIn.Count > 0 ? alsoIn : null,
            droppedOutsideCrop = Dropped > 0 ? Dropped : (int?)null,
            trimmedToCrop = Trimmed > 0 ? Trimmed : (int?)null,
            hint = Mode == "warn"
                ? "Call again with dependent_view: 'clip_to_crop' to keep new detail lines inside this view's crop, or 'primary' to place them in the primary view deliberately."
                : null,
        };
    }
}

/// <summary>Creates dimension strings one per transaction (ItemBatch), so one Revit refuses doesn't undo the others.</summary>
internal static class DimensionBatch
{
    public sealed record Spec(string Label, View View, Line Line, ReferenceArray Refs, DimensionType? Type = null);

    public static (List<long> Ids, ItemBatch.Result Result) Create(Document doc, string name, IEnumerable<Spec> specs)
    {
        var result = ItemBatch.Run(doc, name, specs, s => s.Label, _ => [], s =>
        {
            var dim = s.Type is null ? doc.Create.NewDimension(s.View, s.Line, s.Refs) : doc.Create.NewDimension(s.View, s.Line, s.Refs, s.Type);
            if (dim is null) throw new InvalidOperationException("Revit returned no dimension for these references.");
            return dim.Id.Value;
        }, borrow: false);
        return (result.Items.Where(i => i.Ok && i.Value is long).Select(i => (long)i.Value!).ToList(), result);
    }
}

/// <summary>
/// Works out a dimension string through elements: the measuring direction, the references (grids, levels, reference
/// planes, walls, floors and other host objects, family instances by their centre planes, named side planes or outer
/// faces) and the dimension line. Shared by dimension_elements and add_dimensions.
/// </summary>
internal static class DimensionPlanner
{
    public sealed record Plan(Line Line, ReferenceArray Refs, List<object> Skipped);

    public static Plan Make(Document doc, View view, List<Element> elements, string? direction, string wallMode, string familyRefs,
        double offset, Line? line)
    {
        if (view is View3D || view is ViewSchedule || view.ViewType == ViewType.DrawingSheet || view.IsTemplate)
            throw new ArgumentException($"Dimensions go in plan, section, elevation and detail views, not '{view.Name}'.");
        if (elements.Count < 1) throw new ArgumentException("Give at least one element (two, unless family_refs gives both faces of one).");
        if (familyRefs is not ("center" or "faces" or "center_and_faces"))
            throw new ArgumentException("family_refs must be center, faces or center_and_faces.");
        var vd = view.ViewDirection.Normalize();

        XYZ? m = (direction ?? "auto") switch
        {
            "x" => XYZ.BasisX,
            "y" => XYZ.BasisY,
            "vertical" => XYZ.BasisZ,
            "auto" => null,
            _ => throw new ArgumentException("direction must be auto, x, y or vertical."),
        };
        m ??= AutoDirection(elements, view);
        m = m - vd * m.DotProduct(vd);
        if (m.GetLength() < 1e-6)
            throw new ArgumentException($"That direction points straight out of '{view.Name}'. Pick another direction or a view that shows it.");
        m = m.Normalize();

        var refs = new ReferenceArray();
        var points = new List<XYZ>();
        var skipped = new List<object>();
        foreach (var e in elements)
        {
            var (found, problem) = References(e, m, wallMode, familyRefs, view);
            if (found.Count == 0)
            {
                skipped.Add(new { id = e.Id.Value, reason = problem ?? $"no reference across this direction ({e.Category?.Name ?? e.GetType().Name})" });
                continue;
            }
            if (problem != null) skipped.Add(new { id = e.Id.Value, note = problem });
            foreach (var r in found) refs.Append(r);
            var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
            if (bb != null) points.AddRange(ViewKit.Corners(bb));
        }
        if (refs.Size < 2)
            throw new ArgumentException($"Only {refs.Size} usable reference(s) found; a dimension needs two. Skipped: {JsonSerializer.Serialize(skipped)}");

        if (line is null)
        {
            if (points.Count == 0) throw new ArgumentException("Couldn't find where the elements are in this view. Give 'line'.");
            var perp = vd.CrossProduct(m).Normalize();
            var along = points.Select(p => p.DotProduct(m)).ToList();
            var position = points.Max(p => p.DotProduct(perp)) + offset;
            var depth = view is ViewPlan plan ? (plan.GenLevel?.ProjectElevation ?? 0) : view.Origin.DotProduct(vd);
            XYZ At(double s) => m * s + perp * position + vd * depth;
            line = Line.CreateBound(At(along.Min() - 1), At(along.Max() + 1));
        }
        return new Plan(line, refs, skipped);
    }

    public static XYZ AutoDirection(List<Element> elements, View view)
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
        if (centers.Count < 2) return elements.Count == 1 && elements[0] is HostObject and not Wall ? XYZ.BasisZ : view.RightDirection;
        var right = view.RightDirection; var up = view.UpDirection;
        double Spread(XYZ d) => centers.Max(c => c.DotProduct(d)) - centers.Min(c => c.DotProduct(d));
        return Spread(right) >= Spread(up) ? right : up;
    }

    public static (List<Reference> Refs, string? Problem) References(Element e, XYZ m, string wallMode, string familyRefs, View view)
    {
        bool Across(XYZ normal) => Math.Abs(normal.Normalize().DotProduct(m)) > 0.99;
        switch (e)
        {
            case Grid g when g.Curve is Line l:
                return (Across(new XYZ(-l.Direction.Y, l.Direction.X, 0)) ? [new Reference(g)] : [], null);
            case Level lv:
                return (Across(XYZ.BasisZ) ? [new Reference(lv)] : [], null);
            case ReferencePlane rp:
                return (Across(rp.Normal) ? [rp.GetReference()] : [], null);
            case Wall w when w.Location is LocationCurve { Curve: Line wl }:
            {
                if (!Across(new XYZ(-wl.Direction.Y, wl.Direction.X, 0))) return ([], null);
                var list = new List<Reference>();
                if (wallMode is "exterior_face" or "both_faces" && DimensionWallsTool.WallReference(w, "exterior_face") is { } ext) list.Add(ext);
                if (wallMode is "interior_face" or "both_faces" && DimensionWallsTool.WallReference(w, "interior_face") is { } inn) list.Add(inn);
                return (list, null);
            }
            case HostObject host when host is not Wall:
            {
                // Floors, roofs, slabs: top and bottom faces for vertical strings, outer edges otherwise.
                if (Math.Abs(m.Z) > 0.99)
                {
                    var faces = new List<Reference>();
                    string? problem = null;
                    foreach (var sign in new[] { 1, -1 })
                        foreach (var f in RefResolver.TopOrBottom(host, view, sign))
                        {
                            if (f.Face is PlanarFace pf && Math.Acos(Math.Min(1, Math.Abs(f.Tr.OfVector(pf.FaceNormal).Normalize().Z))) <= 1e-4) faces.Add(f.Ref);
                            else problem = $"the {(sign > 0 ? "top" : "bottom")} face slopes (e.g. a fall), so Revit can't keep it parallel in a vertical dimension; " +
                                           "use add_spot_elevations on it, or dimension to a level or reference plane";
                        }
                    return (faces, problem);
                }
                return RefResolver.OuterFacesAcross(host, m, view);
            }
            case FamilyInstance fi:
                return InstanceReferences(fi, m, familyRefs, view);
            default:
                return RefResolver.OuterFacesAcross(e, m, view);
        }
    }

    private static (List<Reference>, string?) InstanceReferences(FamilyInstance fi, XYZ m, string familyRefs, View view)
    {
        var list = new List<Reference>();
        string? problem = null;
        var hand = fi.HandOrientation; var facing = fi.FacingOrientation;
        bool vertical = Math.Abs(m.Z) > 0.99;
        var alongHand = !vertical && Math.Abs(hand.DotProduct(m)) >= Math.Abs(facing.DotProduct(m));

        if (familyRefs is "center" or "center_and_faces")
        {
            // Centre (left/right) plane is perpendicular to the hand direction; (front/back) to the facing direction.
            var type = vertical ? FamilyInstanceReferenceType.CenterElevation
                : alongHand ? FamilyInstanceReferenceType.CenterLeftRight : FamilyInstanceReferenceType.CenterFrontBack;
            list.AddRange(fi.GetReferences(type).Take(1));
        }
        if (familyRefs is "faces" or "center_and_faces" || list.Count == 0)
        {
            // The family's own side planes when it has them (strong references), else the outer faces of its geometry.
            var (a, b) = vertical ? (FamilyInstanceReferenceType.Bottom, FamilyInstanceReferenceType.Top)
                : alongHand ? (FamilyInstanceReferenceType.Left, FamilyInstanceReferenceType.Right)
                : (FamilyInstanceReferenceType.Front, FamilyInstanceReferenceType.Back);
            var named = fi.GetReferences(a).Take(1).Concat(fi.GetReferences(b).Take(1)).ToList();
            if (named.Count == 2) list.AddRange(named);
            else
            {
                var (faces, why) = RefResolver.OuterFacesAcross(fi, m, view);
                list.AddRange(faces);
                problem = why;
                if (faces.Count == 0 && HasCircularEdge(fi, view))
                    problem = "round element: there is no flat face to measure its width; use a diameter dimension (add_dimensions kind 'diameter', Revit 2025 and later) or add it by hand";
            }
        }
        return (list, list.Count > 0 ? null : problem);
    }

    private static bool HasCircularEdge(Element e, View view) =>
        RefResolver.Faces(e, view).Any(f => f.Face is CylindricalFace or ConicalFace);
}

/// <summary>Arc edges (pile and pipe outlines) for radius and diameter dimensions.</summary>
internal static class ArcEdges
{
    /// <summary>The circular edge of an element that best faces the view: in plans the one nearest the cut plane.</summary>
    public static (Reference Ref, Arc Arc)? Find(Element e, View view)
    {
        var vd = view.ViewDirection.Normalize();
        double target;
        if (view is ViewPlan plan && plan.GenLevel is { } level)
        {
            double cut;
            try { cut = plan.GetViewRange().GetOffset(PlanViewPlane.CutPlane); } catch (Autodesk.Revit.Exceptions.ApplicationException) { cut = 4; }
            target = level.ProjectElevation + cut;
        }
        else target = view.Origin.DotProduct(vd);

        (Reference, Arc)? best = null; double bestScore = double.MaxValue;
        var geometry = e.get_Geometry(RefResolver.GeometryOptions(view));
        if (geometry is null) return null;
        void Walk(GeometryElement g, Transform tr, bool inSymbol, int depth)
        {
            foreach (var obj in g)
            {
                if (obj is Solid solid)
                {
                    foreach (Edge edge in solid.Edges)
                    {
                        if (edge.Reference is null || edge.AsCurve() is not Arc arc) continue;
                        var a = (Arc)arc.CreateTransformed(tr);
                        if (Math.Abs(a.Normal.Normalize().DotProduct(vd)) < 0.999) continue; // the circle must face the viewer
                        var score = Math.Abs(a.Center.DotProduct(vd) - target);
                        if (score >= bestScore) continue;
                        bestScore = score;
                        var r = inSymbol && e is FamilyInstance fi ? RefResolver.ForInstance(e.Document, fi, edge.Reference) : edge.Reference;
                        best = (r, a);
                    }
                }
                else if (obj is GeometryInstance gi && depth < 2) Walk(gi.GetSymbolGeometry(), tr.Multiply(gi.Transform), true, depth + 1);
            }
        }
        Walk(geometry, Transform.Identity, false, 0);
        return best;
    }

    /// <summary>
    /// Creates a radius or diameter dimension on an arc reference. Revit 2025 added RadialDimension.Create for projects;
    /// in Revit 2024 the API can only make these in family documents, so the caller must ask for a manual re-add.
    /// </summary>
    public static Dimension Create(Document doc, View view, Reference arcRef, bool diameter, DimensionType? type)
    {
#if REVIT2024
        throw new InvalidOperationException(
            $"Revit 2024's API cannot create {(diameter ? "diameter" : "radius")} dimensions in a project (RadialDimension.Create arrived in Revit 2025). " +
            $"Add it by hand: Annotate > {(diameter ? "Diameter" : "Radial")}, then pick the circular edge.");
#else
        var dim = RadialDimension.Create(doc, view, arcRef, diameter)
                  ?? throw new InvalidOperationException("Revit did not create the dimension on this edge.");
        if (type != null && dim.DimensionType.Id != type.Id) dim.ChangeTypeId(type.Id);
        return dim;
#endif
    }

    public static bool CanCreate =>
#if REVIT2024
        false;
#else
        true;
#endif
}

public sealed class AddDimensionsTool : IRevitTool
{
    public string Name => "add_dimensions";
    public string Description =>
        "Add several dimension strings in one view, each in its own step: one Revit refuses (e.g. 'references are no longer " +
        "parallel' on a sloped floor top) is reported with its reason and the others are kept. Each item dimensions through " +
        "elements like dimension_elements: grids, levels, reference planes, walls, floors/slabs (top/bottom faces), and family " +
        "instances such as piles, beams and columns by their centre planes, side planes or outer faces (references are taken " +
        "from the family geometry with the instance's position applied). kind 'diameter'/'radius' dimensions a round element " +
        "(Revit 2025 and later; Revit 2024's API can't, so those come back as 'add by hand').";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "items": {"type": "array", "items": {"type": "object", "properties": {
        "element_ids": {"type": "array", "items": {"type": "integer"}},
        "kind": {"type": "string", "enum": ["linear", "diameter", "radius"], "description": "Default linear. diameter/radius take one element."},
        "direction": {"type": "string", "enum": ["auto", "x", "y", "vertical"]},
        "walls": {"type": "string", "enum": ["exterior_face", "interior_face", "both_faces"]},
        "family_refs": {"type": "string", "enum": ["center", "faces", "center_and_faces"], "description": "Family instances: centre plane (default), the two outer faces (e.g. pile or beam width/depth), or both."},
        "offset": {"type": "number", "description": "Distance from the elements to the dimension line, project units."},
        "line": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Optional [[x,y],[x,y]] dimension line (view points)."},
        "label": {"type": "string", "description": "How this item is named in the result."}
      }, "required": ["element_ids"]}},
      "dimension_type": {"type": "string", "description": "Dimension type name (default: each kind's default)."}
    }
    """);
    public string[] Required => ["items"];
    public bool ModifiesModel => true;

    private sealed record Job(string Label, string Kind, DimensionPlanner.Plan? Plan, Reference? ArcRef, string? Problem);

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        var guard = DependentGuard.For(doc, view, args.OptString("dependent_view"));
        var target = guard.Target;
        DimensionType? type = null;
        if (args.OptString("dimension_type") is { } typeName)
            type = TypeMatch.Find(new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(t => t.StyleType is DimensionStyleType.Linear or DimensionStyleType.Diameter or DimensionStyleType.Radial).ToList(), typeName, "dimension type");

        var items = args.Array("items").ToList();
        if (items.Count == 0) return ToolResult.Error("Give at least one item.");
        if (items.Count > 500) return ToolResult.Error("At most 500 dimension strings per call.");

        var jobs = new List<Job>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            string S(string k) => it.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var label = S("label") is { Length: > 0 } l ? l : $"item {i + 1}";
            var kind = S("kind") is { Length: > 0 } k ? k : "linear";
            try
            {
                var elements = (it.TryGetProperty("element_ids", out var ids) ? ids.EnumerateArray().Select(x => x.GetInt64()) : [])
                    .Select(x => doc.GetElement(new ElementId(x)) ?? throw new ArgumentException($"No element with id {x}.")).ToList();
                if (kind is "diameter" or "radius")
                {
                    if (elements.Count != 1) throw new ArgumentException($"A {kind} dimension takes exactly one element.");
                    var arc = ArcEdges.Find(elements[0], target)
                              ?? throw new ArgumentException($"{elements[0].Id.Value} has no circular edge facing '{target.Name}' (round piles show one in plan).");
                    jobs.Add(new Job(label, kind, null, arc.Ref, ArcEdges.CanCreate ? null
                        : $"Revit 2024's API cannot create {kind} dimensions in a project; add it by hand: Annotate > {(kind == "diameter" ? "Diameter" : "Radial")} and pick the edge of {elements[0].Id.Value}"));
                    guard.Note(arc.Arc.Center);
                    continue;
                }
                if (kind != "linear") throw new ArgumentException("kind must be linear, diameter or radius.");
                Line? line = null;
                if (it.TryGetProperty("line", out var lp))
                {
                    var pts = lp.EnumerateArray().Select(p => ViewPoints.ToModel(doc, target, p)).ToList();
                    if (pts.Count != 2) throw new ArgumentException("line needs exactly two points.");
                    line = Line.CreateBound(pts[0], pts[1]);
                }
                var offset = it.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number ? RevitHelpers.ToInternal(doc, o.GetDouble()) : 3.0;
                var plan = DimensionPlanner.Make(doc, target, elements, S("direction") is { Length: > 0 } d ? d : null,
                    S("walls") is { Length: > 0 } w ? w : "exterior_face", S("family_refs") is { Length: > 0 } f ? f : "center", offset, line);
                if (!guard.Keep(plan.Line.Evaluate(0.5, true))) { jobs.Add(new Job(label, kind, null, null, "outside this dependent view's crop (dependent_view: clip_to_crop)")); continue; }
                jobs.Add(new Job(label, kind, plan, null, null));
            }
            catch (ArgumentException ex) { jobs.Add(new Job(label, kind, null, null, ex.Message)); }
        }

        var result = ItemBatch.Run(doc, "Add dimensions", jobs.Where(j => j.Problem is null), j => j.Label, _ => [], j =>
        {
            Dimension dim;
            if (j.Plan != null)
                dim = (type is { StyleType: DimensionStyleType.Linear } ? doc.Create.NewDimension(target, j.Plan.Line, j.Plan.Refs, type)
                          : doc.Create.NewDimension(target, j.Plan.Line, j.Plan.Refs))
                      ?? throw new InvalidOperationException("Revit returned no dimension for these references.");
            else dim = ArcEdges.Create(doc, target, j.ArcRef!, j.Kind == "diameter", type is { StyleType: DimensionStyleType.Diameter or DimensionStyleType.Radial } ? type : null);
            return dim.Id.Value;
        }, borrow: false);

        var created = result.Items.Where(x => x.Ok && x.Value is long).Select(x => (long)x.Value!).ToList();
        var notes = jobs.Where(j => j.Plan?.Skipped.Count > 0).Select(j => new { item = j.Label, skipped = j.Plan!.Skipped }).ToList();
        var payload = new
        {
            view = target.Name,
            created = created.Count,
            ids = created,
            failed = result.Failed.Select(f => new { item = f.Item, reason = f.Error })
                .Concat(jobs.Where(j => j.Problem != null).Select(j => new { item = j.Label, reason = j.Problem })).ToList(),
            manualReAdd = jobs.Where(j => j.Problem != null && j.Problem.StartsWith("Revit 2024")).Select(j => j.Label).ToList(),
            referenceNotes = notes.Count > 0 ? notes : null,
            dependentView = guard.Report(),
        };
        return created.Count == 0 && jobs.Count > 0
            ? new ToolResult("No dimensions were created. " + JsonSerializer.Serialize(payload), IsError: true)
            : ToolResult.Ok(payload);
    }
}

public sealed class CopyDimensionsTool : IRevitTool
{
    public string Name => "copy_dimensions";
    public string Description =>
        "Copy or recreate dimensions (and spot elevations) from one view into other views, each dimension in its own step. " +
        "Where the dimensioned elements also show in the target view, the dimension is copied view-to-view; otherwise it is " +
        "rebuilt on the matching elements of the target view (same category and type at the same place relative to the view, " +
        "e.g. a typical section copied from Berth 10 to Berth 9 piles). Dimensions the API cannot recreate are listed up front " +
        "under manualReAdd with where they were: ones on view-specific cut-edge references, and in Revit 2024 diameter and " +
        "radius dimensions (Revit 2025+ recreates those). Use list_only to see the plan before changing anything.";
    public string PropertiesJson => """
    {
      "source_view_id": {"type": "integer"},
      "target_view_ids": {"type": "array", "items": {"type": "integer"}},
      "dimension_ids": {"type": "array", "items": {"type": "integer"}, "description": "Default: every dimension and spot elevation drawn in the source view."},
      "method": {"type": "string", "enum": ["auto", "copy", "recreate"], "description": "auto (default): copy when the target shows the same elements, else recreate on matching elements."},
      "match_tolerance": {"type": "number", "description": "How far (project units, in the view plane) a matching element may be from the mapped position. Default 100 mm."},
      "offset": {"type": "array", "items": {"type": "number"}, "description": "Extra [right, up] shift in the target view, project units."},
      "list_only": {"type": "boolean", "description": "Only report what would be copied, recreated or left for manual re-add."}
    }
    """;
    public string[] Required => ["source_view_id", "target_view_ids"];
    public bool ModifiesModel => true;

    private sealed class Job
    {
        public Dimension Dim = null!;
        public View Target = null!;
        public string How = "";
        public List<Reference>? Mapped;
        public string? Manual;
        public string Label => $"{Dim.Id.Value} -> '{Target.Name}'";
    }

    private static Transform Frame(View v)
    {
        var t = Transform.Identity;
        t.Origin = v.Origin; t.BasisX = v.RightDirection.Normalize(); t.BasisY = v.UpDirection.Normalize(); t.BasisZ = v.ViewDirection.Normalize();
        return t;
    }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var source = doc.GetElement(new ElementId(args.OptLong("source_view_id") ?? 0)) as View ?? throw new ArgumentException("source_view_id is not a view.");
        var targets = args.LongList("target_view_ids").Select(i => doc.GetElement(new ElementId(i)) as View ?? throw new ArgumentException($"{i} is not a view.")).ToList();
        if (targets.Count == 0) return ToolResult.Error("Give at least one target view.");
        if (targets.Any(t => t.Id == source.Id)) return ToolResult.Error("A target view is the source view.");
        var method = args.OptString("method") ?? "auto";
        if (method is not ("auto" or "copy" or "recreate")) return ToolResult.Error("method must be auto, copy or recreate.");
        var tol = args.OptDouble("match_tolerance") is { } mt ? RevitHelpers.ToInternal(doc, mt) : 100 / 304.8;
        var shift = args.Array("offset").Select(e => RevitHelpers.ToInternal(doc, e.GetDouble())).ToList();

        var ids = args.LongList("dimension_ids");
        var dims = (ids.Count > 0
                ? ids.Select(i => doc.GetElement(new ElementId(i)))
                : new FilteredElementCollector(doc, source.Id).OfClass(typeof(Dimension)).Where(d => d.OwnerViewId == source.Id || d.OwnerViewId == source.GetPrimaryViewId()))
            .OfType<Dimension>().ToList();
        if (dims.Count == 0) return ToolResult.Error($"No dimensions found in '{source.Name}'.");
        if (dims.Count * targets.Count > 2000) return ToolResult.Error($"{dims.Count} dimensions x {targets.Count} views is too many for one call; pass dimension_ids or fewer target views.");

        var jobs = new List<Job>();
        var matchCache = new Dictionary<(long, long), Element?>();
        foreach (var target in targets)
        {
            var map = Frame(target).Multiply(Frame(source).Inverse);
            if (shift.Count >= 2) map = Transform.CreateTranslation(target.RightDirection * shift[0] + target.UpDirection * shift[1]).Multiply(map);
            var refIds = dims.SelectMany(d => Refs(d)).Select(r => r.ElementId).Where(i => i != ElementId.InvalidElementId).Distinct().ToList();
            var visible = refIds.Count == 0 ? new HashSet<ElementId>()
                : new HashSet<ElementId>(new FilteredElementCollector(doc, target.Id).WherePasses(new ElementIdSetFilter(refIds)).ToElementIds());

            foreach (var d in dims)
            {
                var job = new Job { Dim = d, Target = target };
                jobs.Add(job);
                var refs = Refs(d);
                var shape = d.DimensionShape;
                if (refs.Any(r => r.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_CUT_EDGE))
                { job.Manual = "it measures to a view-specific cut edge, which exists only in its own view; the API can't point a dimension at the cut edge of another view"; continue; }
                var allVisible = refs.Count > 0 && refs.All(r => visible.Contains(r.ElementId));
                if (method != "recreate" && allVisible) { job.How = "copy"; continue; }
                if (method == "copy") { job.Manual = "the dimensioned elements don't show in the target view (use method auto or recreate)"; continue; }

                var creatable = shape switch
                {
                    DimensionShape.Linear or DimensionShape.Angular => null,
                    DimensionShape.Spot => d is SpotDimension && d.Category?.Id.Value == (long)BuiltInCategory.OST_SpotElevations ? null
                        : "spot coordinates and spot slopes can only be copied, not rebuilt on other elements",
                    DimensionShape.Diameter or DimensionShape.Radial => ArcEdges.CanCreate ? null
                        : $"{(shape == DimensionShape.Diameter ? "diameter" : "radius")} dimensions can't be created in a project through Revit 2024's API (Revit 2025 added RadialDimension.Create)",
                    _ => $"{shape} dimensions are not rebuilt by this tool",
                };
                if (creatable != null) { job.Manual = creatable; continue; }

                // Rebuild on the matching elements of the target view.
                var mapped = new List<Reference>();
                string? why = null;
                foreach (var r in refs)
                {
                    if (visible.Contains(r.ElementId)) { mapped.Add(r); continue; }
                    if (r.LinkedElementId != ElementId.InvalidElementId) { why = "it refers to a linked element"; break; }
                    var src = doc.GetElement(r.ElementId);
                    if (src is null || src is Grid or Level or ReferencePlane) { why = $"{src?.Name ?? r.ElementId.Value.ToString()} (a datum) does not show in the target view"; break; }
                    var key = (src.Id.Value, target.Id.Value);
                    if (!matchCache.TryGetValue(key, out var match)) matchCache[key] = match = Match(doc, src, target, map, tol);
                    if (match is null) { why = $"no {src.Category?.Name ?? "element"} of the same type at the matching place in the target view for {src.Id.Value}"; break; }
                    var swapped = Swap(doc, r, src, match);
                    if (swapped is null) { why = $"the face/edge of {src.Id.Value} has no counterpart on {match.Id.Value}"; break; }
                    mapped.Add(swapped);
                }
                if (why != null) { job.Manual = why; continue; }
                job.How = "recreate";
                job.Mapped = mapped;
            }
        }

        object Where(Job j)
        {
            var p = j.Dim.Origin;
            return new
            {
                dimension = j.Dim.Id.Value, view = j.Target.Name, shape = j.Dim.DimensionShape.ToString(),
                value = j.Dim.NumberOfSegments > 1 ? string.Join(" / ", j.Dim.Segments.Cast<DimensionSegment>().Select(s => s.ValueString)) : j.Dim.ValueString,
                sourceViewPoint = p is null ? null : new[] { RevitHelpers.FromInternal(doc, (p - source.Origin).DotProduct(source.RightDirection)), RevitHelpers.FromInternal(doc, (p - source.Origin).DotProduct(source.UpDirection)) },
                reason = j.Manual,
            };
        }
        var manual = jobs.Where(j => j.Manual != null).Select(Where).ToList();
        if (args.OptBool("list_only"))
            return ToolResult.Ok(new
            {
                source = source.Name,
                copy = jobs.Count(j => j.How == "copy"),
                recreate = jobs.Count(j => j.How == "recreate"),
                manualReAdd = manual,
                units = RevitHelpers.LengthUnitLabel(doc),
            });

        var options = new CopyPasteOptions();
        var result = ItemBatch.Run(doc, "Copy dimensions", jobs.Where(j => j.Manual is null), j => j.Label, _ => [], j =>
        {
            var map = Frame(j.Target).Multiply(Frame(source).Inverse);
            if (shift.Count >= 2) map = Transform.CreateTranslation(j.Target.RightDirection * shift[0] + j.Target.UpDirection * shift[1]).Multiply(map);
            if (j.How == "copy")
            {
                var extra = shift.Count >= 2 ? Transform.CreateTranslation(j.Target.RightDirection * shift[0] + j.Target.UpDirection * shift[1]) : Transform.Identity;
                var copied = ElementTransformUtils.CopyElements(source, [j.Dim.Id], j.Target, extra, options);
                if (copied.Count == 0) throw new InvalidOperationException("Revit copied nothing (its references may not be dimensionable in the target view).");
                return copied.First().Value;
            }
            return Recreate(doc, j.Dim, j.Target, j.Mapped!, map).Id.Value;
        }, borrow: false);

        var ok = result.Items.Where(x => x.Ok && x.Value is long).Select(x => (long)x.Value!).ToList();
        return ToolResult.Ok(new
        {
            source = source.Name,
            created = ok.Count,
            ids = ok.Take(500),
            failed = result.Failed.Select(f => new { item = f.Item, reason = f.Error }).Take(100),
            manualReAdd = manual,
            note = manual.Count > 0 ? "Add the manualReAdd dimensions by hand in the target views (sourceViewPoint is right/up from the source view origin)." : null,
            units = RevitHelpers.LengthUnitLabel(doc),
        });
    }

    private static List<Reference> Refs(Dimension d)
    {
        var list = new List<Reference>();
        try { foreach (Reference r in d.References) list.Add(r); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        return list;
    }

    private static XYZ? Position(Element e) => e.Location switch
    {
        LocationPoint lp => lp.Point,
        LocationCurve lc => lc.Curve.Evaluate(0.5, true),
        _ => e.get_BoundingBox(null) is { } b ? (b.Min + b.Max) / 2 : null,
    };

    /// <summary>The element in the target view that matches a source element (same category and type, same place relative to the view).</summary>
    private static Element? Match(Document doc, Element src, View target, Transform map, double tol)
    {
        if (src.Category is null || Position(src) is not { } p) return null;
        var want = map.OfPoint(p);
        XYZ r = target.RightDirection, u = target.UpDirection, d = target.ViewDirection;
        Element? best = null; double bestPlane = double.MaxValue, bestDepth = double.MaxValue;
        foreach (var c in new FilteredElementCollector(doc, target.Id).OfCategoryId(src.Category.Id).WhereElementIsNotElementType())
        {
            if (c.GetTypeId() != src.GetTypeId() || Position(c) is not { } q) continue;
            var delta = q - want;
            var plane = Math.Sqrt(Math.Pow(delta.DotProduct(r), 2) + Math.Pow(delta.DotProduct(u), 2));
            var depth = Math.Abs(delta.DotProduct(d));
            if (plane > tol) continue;
            if (plane < bestPlane - 1e-6 || (Math.Abs(plane - bestPlane) <= 1e-6 && depth < bestDepth)) { best = c; bestPlane = plane; bestDepth = depth; }
        }
        return best;
    }

    /// <summary>The same face or edge on another element of the same type (its stable reference with the element swapped).</summary>
    private static Reference? Swap(Document doc, Reference r, Element from, Element to)
    {
        try
        {
            var rep = r.ConvertToStableRepresentation(doc).Replace(from.UniqueId, to.UniqueId);
            var swapped = Reference.ParseFromStableRepresentation(doc, rep);
            if (swapped.ElementId != to.Id) return null;
            // Family instance references point into the symbol and may not resolve through the instance; Revit checks them on creation.
            if (to is not FamilyInstance && swapped.ElementReferenceType != ElementReferenceType.REFERENCE_TYPE_NONE && to.GetGeometryObjectFromReference(swapped) is null) return null;
            return swapped;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }

    private static Dimension Recreate(Document doc, Dimension src, View view, List<Reference> refs, Transform map)
    {
        var type = src.DimensionType;
        Dimension dim;
        switch (src.DimensionShape)
        {
            case DimensionShape.Linear:
            {
                var line = (Line)src.Curve;
                var o = map.OfPoint(src.Origin ?? line.Origin);
                var dir = map.OfVector(line.Direction).Normalize();
                var array = new ReferenceArray();
                foreach (var r in refs) array.Append(r);
                dim = doc.Create.NewDimension(view, Line.CreateBound(o - dir, o + dir), array, type)
                      ?? throw new InvalidOperationException("Revit returned no dimension for these references.");
                break;
            }
            case DimensionShape.Angular:
                dim = AngularDimension.Create(doc, view, (Arc)src.Curve.CreateTransformed(map), refs, type);
                break;
            case DimensionShape.Diameter:
            case DimensionShape.Radial:
                dim = ArcEdges.Create(doc, view, refs[0], src.DimensionShape == DimensionShape.Diameter, type);
                break;
            case DimensionShape.Spot:
            {
                var at = map.OfPoint(src.Origin);
                var step = view.Scale * 5 / 304.8;
                var bend = at + (view.RightDirection + view.UpDirection) * step;
                dim = doc.Create.NewSpotElevation(view, refs[0], at, bend, bend + view.RightDirection * step, at, true);
                if (dim.DimensionType.Id != type.Id) dim.ChangeTypeId(type.Id);
                break;
            }
            default:
                throw new InvalidOperationException($"{src.DimensionShape} dimensions are not rebuilt by this tool.");
        }
        CopyText(src, dim);
        return dim;
    }

    /// <summary>Carries prefix, suffix, above/below text and value overrides across, segment by segment when the counts match.</summary>
    private static void CopyText(Dimension from, Dimension to)
    {
        try
        {
            if (from.NumberOfSegments <= 1 && to.NumberOfSegments <= 1)
            {
                if (!string.IsNullOrEmpty(from.Prefix)) to.Prefix = from.Prefix;
                if (!string.IsNullOrEmpty(from.Suffix)) to.Suffix = from.Suffix;
                if (!string.IsNullOrEmpty(from.Above)) to.Above = from.Above;
                if (!string.IsNullOrEmpty(from.Below)) to.Below = from.Below;
                if (!string.IsNullOrEmpty(from.ValueOverride)) to.ValueOverride = from.ValueOverride;
                return;
            }
            if (from.NumberOfSegments != to.NumberOfSegments) return;
            for (var i = 0; i < from.NumberOfSegments; i++)
            {
                var a = from.Segments.get_Item(i); var b = to.Segments.get_Item(i);
                if (!string.IsNullOrEmpty(a.Prefix)) b.Prefix = a.Prefix;
                if (!string.IsNullOrEmpty(a.Suffix)) b.Suffix = a.Suffix;
                if (!string.IsNullOrEmpty(a.Above)) b.Above = a.Above;
                if (!string.IsNullOrEmpty(a.Below)) b.Below = a.Below;
                if (!string.IsNullOrEmpty(a.ValueOverride)) b.ValueOverride = a.ValueOverride;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { /* text overrides are a courtesy; the dimension stands */ }
    }
}

public sealed class ReflowAnnotationsTool : IRevitTool
{
    public string Name => "reflow_annotations";
    public string Description =>
        "Re-flow the tags and text notes of a detail (typically one copied from another section, whose labels kept their old " +
        "heights): each leader end is pulled onto its element where it is now, the label moves to that height so the leader runs " +
        "horizontal, and labels line up in a column on the chosen side of the elements, spaced so none overlap (a leader gets a " +
        "short kink only where labels had to be spread apart). Tags follow their tagged element; text notes follow the element " +
        "under (or nearest) their leader end among element_ids. Use dry_run to see the layout first.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "tag_ids": {"type": "array", "items": {"type": "integer"}},
      "text_note_ids": {"type": "array", "items": {"type": "integer"}},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Only annotations pointing at these elements (with no tag/note ids), and the elements text-note leaders snap to."},
      "tag_category": {"type": "string", "description": "With no ids: only tags of this tag category, e.g. 'Structural Framing Tags'."},
      "include_text_notes": {"type": "boolean", "description": "With no ids: also re-flow text notes (default true)."},
      "side": {"type": "string", "enum": ["auto", "left", "right"], "description": "Column side; auto keeps each label on the side it is on now."},
      "column_at": {"type": "array", "items": {"type": "number"}, "description": "Optional view point [x, y] the label column passes through (otherwise just beyond the elements)."},
      "column_offset": {"type": "number", "description": "Gap between the outermost element and the label column, paper mm (default 8)."},
      "gap": {"type": "number", "description": "Minimum space between labels, paper mm (default 1.5)."},
      "snap_leader_ends": {"type": "boolean", "description": "Pull leader ends onto their element (default true)."},
      "dry_run": {"type": "boolean"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    private sealed class Item
    {
        public Element E = null!;
        public Reference? TagRef;
        public bool HasLeader;
        public XYZ Base = XYZ.Zero;       // a model point on the annotation, for converting view (u, v) back to model
        public double? AnchorU, AnchorV;   // leader end in view coordinates
        public (double U0, double U1, double V0, double V1)? Box;
        public double W, H, CenterU, CenterV, OffU, OffV; // label size and centre; tags: centre minus head
        public bool Right;
        public double NewV, ColU;
        public string Kind => E is TextNote ? "text" : "tag";
    }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        XYZ R = view.RightDirection.Normalize(), U = view.UpDirection.Normalize();
        double Uof(XYZ p) => p.DotProduct(R);
        double Vof(XYZ p) => p.DotProduct(U);
        var paper = view.Scale / 304.8; // one paper mm in model feet
        var columnOffset = (args.OptDouble("column_offset") ?? 8) * paper;
        var gap = (args.OptDouble("gap") ?? 1.5) * paper;
        var snap = args.OptBool("snap_leader_ends", true);
        var side = args.OptString("side") ?? "auto";
        if (side is not ("auto" or "left" or "right")) return ToolResult.Error("side must be auto, left or right.");

        var targetIds = new HashSet<long>(args.LongList("element_ids"));
        var targetBoxes = targetIds.Select(i => doc.GetElement(new ElementId(i))).Where(e => e != null)
            .Select(e => (e: e!, b: e!.get_BoundingBox(view))).Where(x => x.b != null)
            .Select(x => (x.e, box: Rect(x.b!, Uof, Vof))).ToList();

        // Collect the annotations.
        var elements = args.LongList("tag_ids").Concat(args.LongList("text_note_ids")).Select(i => doc.GetElement(new ElementId(i)))
            .Where(e => e is IndependentTag or TextNote).ToList();
        if (elements.Count == 0)
        {
            var tags = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().AsEnumerable();
            if (args.OptString("tag_category") is { } cat)
            {
                var bic = (long)RevitHelpers.ParseCategory(doc, cat);
                tags = tags.Where(t => t.Category?.Id.Value == bic);
            }
            if (targetIds.Count > 0)
                tags = tags.Where(t => t.GetTaggedLocalElementIds().Any(id => targetIds.Contains(id.Value)));
            elements.AddRange(tags);
            if (args.OptBool("include_text_notes", true))
            {
                var notes = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().AsEnumerable();
                if (targetBoxes.Count > 0)
                    notes = notes.Where(n => n.GetLeaders().Count > 0 && targetBoxes.Any(t => Near(t.box, Uof(n.GetLeaders()[0].End), Vof(n.GetLeaders()[0].End), columnOffset * 3)));
                elements.AddRange(notes);
            }
        }
        if (elements.Count == 0) return ToolResult.Error($"No tags or text notes to re-flow in '{view.Name}'.");
        if (elements.Count > 400) return ToolResult.Error($"{elements.Count} annotations; re-flow one detail at a time (pass element_ids or tag/note ids, at most 400).");

        var items = new List<Item>();
        foreach (var e in elements)
        {
            var it = new Item { E = e };
            if (e is IndependentTag tag)
            {
                it.HasLeader = tag.HasLeader;
                it.Base = tag.TagHeadPosition;
                it.TagRef = tag.GetTaggedReferences().FirstOrDefault();
                var tagged = it.TagRef is { } r && r.LinkedElementId == ElementId.InvalidElementId ? doc.GetElement(r.ElementId) : null;
                if (tagged?.get_BoundingBox(view) is { } bb) it.Box = Rect(bb, Uof, Vof);
                XYZ? end = null;
                if (tag.HasLeader && tag.LeaderEndCondition == LeaderEndCondition.Free && it.TagRef != null)
                {
                    try { end = tag.GetLeaderEnd(it.TagRef); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                }
                if (end != null) { it.AnchorU = Uof(end); it.AnchorV = Vof(end); }
                else if (it.Box is { } b) { it.AnchorU = (b.U0 + b.U1) / 2; it.AnchorV = (b.V0 + b.V1) / 2; }
            }
            else if (e is TextNote note)
            {
                it.Base = note.Coord;
                var leaders = note.GetLeaders();
                it.HasLeader = leaders.Count > 0;
                if (it.HasLeader)
                {
                    var end = leaders[0].End;
                    it.AnchorU = Uof(end); it.AnchorV = Vof(end);
                    if (targetBoxes.Count > 0)
                        it.Box = targetBoxes.OrderBy(t => Distance(t.box, it.AnchorU.Value, it.AnchorV.Value)).First().box;
                }
                it.W = note.Width * view.Scale; it.H = note.Height * view.Scale;
                var left = note.HorizontalAlignment switch
                {
                    HorizontalTextAlignment.Right => Uof(note.Coord) - it.W,
                    HorizontalTextAlignment.Center => Uof(note.Coord) - it.W / 2,
                    _ => Uof(note.Coord),
                };
                it.CenterU = left + it.W / 2; it.CenterV = Vof(note.Coord) - it.H / 2;
            }
            if (snap && it.HasLeader && it.Box is { } box && it.AnchorU is { } au && it.AnchorV is { } av)
            {
                it.AnchorU = Math.Min(Math.Max(au, box.U0), box.U1);
                it.AnchorV = Math.Min(Math.Max(av, box.V0), box.V1);
            }
            items.Add(it);
        }

        // Tag label sizes: read with leaders off (a tag's box includes its leader), in a transaction that is undone.
        var tagItems = items.Where(i => i.E is IndependentTag).ToList();
        if (tagItems.Count > 0)
        {
            using var measure = new Transaction(doc, "Measure tags");
            measure.Start();
            try
            {
                foreach (var it in tagItems.Where(i => i.HasLeader))
                {
                    try { ((IndependentTag)it.E).HasLeader = false; }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { /* measured with its leader; the box is then a little generous */ }
                }
                doc.Regenerate();
                foreach (var it in tagItems)
                {
                    var head = ((IndependentTag)it.E).TagHeadPosition;
                    if (it.E.get_BoundingBox(view) is { } bb)
                    {
                        var r = Rect(bb, Uof, Vof);
                        it.W = r.U1 - r.U0; it.H = r.V1 - r.V0;
                        it.CenterU = (r.U0 + r.U1) / 2; it.CenterV = (r.V0 + r.V1) / 2;
                    }
                    else { it.W = 10 * paper; it.H = 3 * paper; it.CenterU = Uof(head); it.CenterV = Vof(head); }
                    it.OffU = it.CenterU - Uof(head); it.OffV = it.CenterV - Vof(head);
                }
            }
            finally { measure.RollBack(); }
        }

        // Sides and columns.
        var meanAnchor = items.Where(i => i.AnchorU.HasValue).Select(i => i.AnchorU!.Value).DefaultIfEmpty(items.Average(i => i.CenterU)).Average();
        foreach (var it in items)
            it.Right = side == "right" || (side == "auto" && it.CenterU >= (it.AnchorU ?? meanAnchor));
        double? columnAt = args.Has("column_at") ? Uof(ViewPoints.ToModel(doc, view, args.Raw["column_at"])) : null;
        var columns = new List<object>();
        foreach (var right in new[] { true, false })
        {
            var group = items.Where(i => i.Right == right).ToList();
            if (group.Count == 0) continue;
            var extents = group.SelectMany(i => i.Box is { } b ? new[] { b.U0, b.U1 } : i.AnchorU is { } a ? new[] { a } : new[] { i.CenterU }).ToList();
            var col = columnAt ?? (right ? extents.Max() + columnOffset : extents.Min() - columnOffset);
            foreach (var it in group) it.ColU = col;
            Stack(group, gap);
            columns.Add(new { side = right ? "right" : "left", labels = group.Count });
        }

        var kinked = items.Count(i => i.AnchorV is { } av && Math.Abs(i.NewV - av) > 1e-6);
        if (args.OptBool("dry_run"))
            return ToolResult.Ok(new
            {
                view = view.Name, annotations = items.Count, tags = tagItems.Count, textNotes = items.Count - tagItems.Count, columns,
                horizontalLeaders = items.Count(i => i.HasLeader) - kinked, leadersWithKink = kinked,
                moves = items.Take(100).Select(i => new
                {
                    id = i.E.Id.Value, kind = i.Kind,
                    up = RevitHelpers.FromInternal(doc, i.NewV - i.CenterV),
                    across = RevitHelpers.FromInternal(doc, (i.Right ? i.ColU + i.W / 2 : i.ColU - i.W / 2) - i.CenterU),
                }),
                units = RevitHelpers.LengthUnitLabel(doc),
            });

        XYZ Model(Item it, double u, double v) => it.Base + R * (u - Uof(it.Base)) + U * (v - Vof(it.Base));
        var elbowReach = Math.Min(columnOffset * 0.5, 4 * paper);
        var batch = ItemBatch.Run(doc, "Re-flow annotations", items, i => $"{i.Kind} {i.E.Id.Value}", i => [i.E.Id], it =>
        {
            var notes = new List<string>();
            var elbowU = it.Right ? it.ColU - elbowReach : it.ColU + elbowReach;
            if (it.E is IndependentTag tag)
            {
                var cu = it.Right ? it.ColU + it.W / 2 : it.ColU - it.W / 2;
                tag.TagHeadPosition = Model(it, cu - it.OffU, it.NewV - it.OffV);
                if (it.HasLeader && it.TagRef != null && it.AnchorU is { } au && it.AnchorV is { } av)
                {
                    try
                    {
                        if (snap)
                        {
                            if (tag.LeaderEndCondition != LeaderEndCondition.Free) tag.LeaderEndCondition = LeaderEndCondition.Free;
                            tag.SetLeaderEnd(it.TagRef, Model(it, au, av));
                        }
                        tag.SetLeaderElbow(it.TagRef, Model(it, elbowU, av));
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex) { notes.Add("leader: " + ex.Message.Trim()); }
                }
            }
            else if (it.E is TextNote note)
            {
                note.HorizontalAlignment = it.Right ? HorizontalTextAlignment.Left : HorizontalTextAlignment.Right;
                note.Coord = Model(it, it.ColU, it.NewV + it.H / 2);
                var leaders = note.GetLeaders();
                if (leaders.Count > 0 && it.AnchorU is { } au && it.AnchorV is { } av)
                {
                    try
                    {
                        note.LeaderLeftAttachment = LeaderAtachement.Midpoint;
                        note.LeaderRightAttachment = LeaderAtachement.Midpoint;
                        if (snap) leaders[0].End = Model(it, au, av);
                        leaders[0].Elbow = Model(it, elbowU, av);
                        // Where the leader meets the text depends on the note's lines; measure it and lift the note so it meets at the row height.
                        doc.Regenerate();
                        var anchor = note.GetLeaders()[0].Anchor;
                        var dv = it.NewV - Vof(anchor);
                        if (Math.Abs(dv) > 1e-6) note.Coord = note.Coord + U * dv;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex) { notes.Add("leader: " + ex.Message.Trim()); }
                }
            }
            return notes.Count > 0 ? string.Join("; ", notes) : null;
        });

        return ToolResult.Ok(new
        {
            view = view.Name,
            reflowed = batch.Done,
            columns,
            horizontalLeaders = items.Count(i => i.HasLeader) - kinked,
            leadersWithKink = kinked,
            leaderNotes = batch.Items.Where(i => i.Ok && i.Value is string).Select(i => new { item = i.Item, note = i.Value }).Take(50),
            failed = batch.Failed.Select(f => new { item = f.Item, reason = f.Error }).Take(50),
            note = kinked > 0 ? $"{kinked} label(s) sat too close to a neighbour, so they were spread apart and their leaders have a short kink near the column; widen 'gap' or move elements apart for straighter leaders." : null,
        });
    }

    private static (double U0, double U1, double V0, double V1) Rect(BoundingBoxXYZ bb, Func<XYZ, double> u, Func<XYZ, double> v)
    {
        var corners = ViewKit.Corners(bb).ToList();
        return (corners.Min(u), corners.Max(u), corners.Min(v), corners.Max(v));
    }

    private static double Distance((double U0, double U1, double V0, double V1) b, double u, double v)
    {
        var du = Math.Max(0, Math.Max(b.U0 - u, u - b.U1));
        var dv = Math.Max(0, Math.Max(b.V0 - v, v - b.V1));
        return Math.Sqrt(du * du + dv * dv);
    }

    private static bool Near((double U0, double U1, double V0, double V1) b, double u, double v, double reach) => Distance(b, u, v) <= reach;

    /// <summary>
    /// Rows: each label wants its leader's height. Labels that would overlap are merged into a block that is stacked
    /// top to bottom and centred on the mean of what its labels want, so leaders stay as close to horizontal as they can.
    /// </summary>
    private static void Stack(List<Item> group, double gap)
    {
        var sorted = group.OrderByDescending(i => i.AnchorV ?? i.CenterV).ToList();
        var blocks = new List<(List<Item> items, double top)>();
        double Height(List<Item> b) => b.Sum(i => i.H) + gap * (b.Count - 1);
        double BestTop(List<Item> b)
        {
            double offset = 0, sum = 0;
            foreach (var i in b) { sum += (i.AnchorV ?? i.CenterV) + offset + i.H / 2; offset += i.H + gap; }
            return sum / b.Count;
        }
        foreach (var it in sorted)
        {
            var block = new List<Item> { it };
            blocks.Add((block, BestTop(block)));
            while (blocks.Count >= 2)
            {
                var (upper, upperTop) = blocks[blocks.Count - 2];
                var (lower, lowerTop) = blocks[blocks.Count - 1];
                if (upperTop - Height(upper) - gap >= lowerTop) break;
                var merged = upper.Concat(lower).ToList();
                blocks.RemoveRange(blocks.Count - 2, 2);
                blocks.Add((merged, BestTop(merged)));
            }
        }
        foreach (var (items, top) in blocks)
        {
            var y = top;
            foreach (var i in items) { i.NewV = y - i.H / 2; y -= i.H + gap; }
        }
    }
}

public sealed class TrimMatchlinesTool : IRevitTool
{
    public string Name => "trim_matchlines";
    public string Description =>
        "Stop matchlines running past a view's crop. Revit does not clip matchlines to the crop region, and their sketches can't " +
        "be trimmed per view (a matchline is one element shown in every plan it crosses). mode 'redraw' (default) hides each " +
        "matchline in the view and redraws the part inside the crop as detail lines in a matchline line style (re-running " +
        "replaces the earlier redraw; 'restore' undoes it). mode 'edit_sketch' instead tries to trim the matchline's own sketch " +
        "to the union of the given views' crops (affects every view); it says exactly why when Revit's API refuses. mode 'report' " +
        "lists how far each matchline overruns.";
    public string PropertiesJson => """
    {
      "view_ids": {"type": "array", "items": {"type": "integer"}, "description": "Plan views (default: active view)."},
      "matchline_ids": {"type": "array", "items": {"type": "integer"}, "description": "Default: every matchline visible in each view."},
      "mode": {"type": "string", "enum": ["redraw", "report", "restore", "edit_sketch"]},
      "line_style": {"type": "string", "description": "Line style for the redrawn lines. Default: a line style with 'match' in its name, else a new 'Matchline (Sketch ProMax)' style copied from the Matchline object style."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    private const string Marker = "Sketch ProMax matchline ";

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var mode = args.OptString("mode") ?? "redraw";
        if (mode is not ("redraw" or "report" or "restore" or "edit_sketch")) return ToolResult.Error("mode must be redraw, report, restore or edit_sketch.");
        var viewIds = args.LongList("view_ids");
        var views = viewIds.Count > 0
            ? viewIds.Select(i => doc.GetElement(new ElementId(i)) as View ?? throw new ArgumentException($"{i} is not a view.")).ToList()
            : [doc.ActiveView];
        var bad = views.Where(v => v is not ViewPlan || v.IsTemplate).Select(v => v.Name).ToList();
        if (bad.Count > 0) return ToolResult.Error($"Matchlines show in plan views; these are not plans: {string.Join(", ", bad)}.");
        var wanted = new HashSet<long>(args.LongList("matchline_ids"));
        var tol = doc.Application.ShortCurveTolerance;

        // Matchlines and their curves as each view shows them.
        var work = new List<(View view, Element ml, List<Curve> curves, CropClip? crop)>();
        foreach (var view in views)
        {
            var crop = CropClip.For(view);
            var mls = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Matchline).WhereElementIsNotElementType()
                .Where(e => wanted.Count == 0 || wanted.Contains(e.Id.Value)).ToList();
            if (mode is "restore" or "redraw")
            {
                // Hidden matchlines are not collected through the view; find them in the model.
                foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Matchline).WhereElementIsNotElementType())
                    if ((wanted.Count == 0 || wanted.Contains(e.Id.Value)) && e.IsHidden(view) && mls.All(m => m.Id != e.Id)) mls.Add(e);
            }
            foreach (var ml in mls) work.Add((view, ml, Curves(doc, ml, view), crop));
        }
        if (work.Count == 0) return ToolResult.Error("No matchlines found in the given views.");

        if (mode == "report")
            return ToolResult.Ok(new
            {
                matchlines = work.Select(w =>
                {
                    var total = w.curves.Sum(c => c.Length);
                    var inside = w.crop is null ? total : w.curves.SelectMany(c => w.crop.Clip(c, 0)).Sum(c => c.Length);
                    var sketch = SketchOf(doc, w.ml);
                    return new
                    {
                        view = w.view.Name, matchline = w.ml.Id.Value,
                        cropActive = w.crop != null,
                        length = RevitHelpers.FromInternal(doc, total),
                        beyondCrop = RevitHelpers.FromInternal(doc, total - inside),
                        hiddenInView = w.ml.IsHidden(w.view),
                        redrawnLines = Ours(doc, w.view, w.ml).Count,
                        sketchEditableByApi = sketch is not null && SketchEditable(doc, sketch),
                    };
                }),
                units = RevitHelpers.LengthUnitLabel(doc),
            });

        if (mode == "edit_sketch") return EditSketches(doc, work, tol);

        GraphicsStyle? style = null;
        string? styleNote = null;
        var styleCreated = false;
        var guards = new Dictionary<long, DependentGuard>();
        // The new line style (if one is made) and the batch are one Undo step; with nothing done, the style goes too.
        var batch = Batch.InGroup(doc, mode == "redraw" ? "Trim matchlines" : "Restore matchlines", () =>
        {
            if (mode == "redraw") (style, styleNote, styleCreated) = LineStyle(doc, args.OptString("line_style"));
            return ItemBatch.Run(doc, mode == "redraw" ? "Trim matchlines" : "Restore matchlines", work,
                w => $"matchline {w.ml.Id.Value} in '{w.view.Name}'", w => [w.view.Id], w =>
                {
                    var old = Ours(doc, w.view, w.ml);
                    if (old.Count > 0) doc.Delete(old);
                    if (mode == "restore")
                    {
                        if (w.ml.IsHidden(w.view)) w.view.UnhideElements([w.ml.Id]);
                        return new { removedLines = old.Count };
                    }
                    if (w.crop is null) throw new InvalidOperationException("the view's crop is off, so there is nothing to trim to");
                    if (!w.ml.IsHidden(w.view))
                    {
                        if (!w.ml.CanBeHidden(w.view)) throw new InvalidOperationException("Revit won't hide this matchline in this view (check the view template's Matchline visibility)");
                        w.view.HideElements([w.ml.Id]);
                    }
                    if (!guards.TryGetValue(w.view.Id.Value, out var guard)) guards[w.view.Id.Value] = guard = DependentGuard.For(doc, w.view, "warn");
                    var drawn = new List<long>();
                    foreach (var piece in w.curves.SelectMany(c => w.crop.Clip(c, tol)))
                        foreach (var flat in CropClip.ToViewPlane(w.view, piece))
                        {
                            var line = doc.Create.NewDetailCurve(w.view, flat);
                            if (style != null) line.LineStyle = style;
                            var comment = line.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                            if (comment is { IsReadOnly: false }) comment.Set(Marker + w.ml.Id.Value);
                            guard.Note(flat.GetEndPoint(0)); guard.Note(flat.GetEndPoint(1));
                            drawn.Add(line.Id.Value);
                        }
                    return new { lines = drawn.Count, replacedLines = old.Count };
                });
        }, b => b.Done > 0);
        if (batch.Done == 0 && styleCreated) { style = null; styleNote = "No matchline was redrawn, so the new matchline line style was not kept."; }

        return ToolResult.Ok(new
        {
            mode,
            done = batch.Done,
            results = batch.Items.Where(i => i.Ok).Select(i => new { item = i.Item, result = i.Value }).Take(100),
            failed = batch.Failed.Select(f => new { item = f.Item, reason = f.Error }),
            lineStyle = style?.GraphicsStyleCategory?.Name,
            styleNote,
            dependentViews = guards.Values.Select(g => g.Report()).Where(r => r != null).ToList(),
            note = mode == "redraw"
                ? "The matchlines are hidden only in these views; other plans still show them in full. Matchline visibility overrides set in a view's Visibility/Graphics don't apply to the redrawn detail lines."
                : null,
        });
    }

    /// <summary>The matchline's curves as the view draws them, or its sketch curves when the view gives none (e.g. hidden).</summary>
    private static List<Curve> Curves(Document doc, Element ml, View view)
    {
        var list = new List<Curve>();
        void Walk(GeometryElement? g)
        {
            if (g is null) return;
            foreach (var o in g)
            {
                if (o is Curve { IsBound: true } c) list.Add(c);
                else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
            }
        }
        if (!ml.IsHidden(view)) Walk(ml.get_Geometry(new Options { View = view }));
        if (list.Count == 0 && SketchOf(doc, ml) is { } sketch)
            foreach (CurveArray arr in sketch.Profile)
                foreach (Curve c in arr) list.Add(c);
        return list;
    }

    private static bool SketchEditable(Document doc, Sketch sketch)
    {
        using var scope = new SketchEditScope(doc, "Sketch ProMax: check matchline sketch");
        try { return scope.IsSketchEditingSupported(sketch.Id); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
    }

    private static Sketch? SketchOf(Document doc, Element ml) =>
        ml.GetDependentElements(new ElementClassFilter(typeof(Sketch))).Select(doc.GetElement).OfType<Sketch>().FirstOrDefault();

    /// <summary>Detail lines this tool drew for a matchline in a view: marked in Comments, or lying on the matchline in the matchline style.</summary>
    private static List<ElementId> Ours(Document doc, View view, Element ml)
    {
        var owner = view.GetPrimaryViewId() is { } p && p != ElementId.InvalidElementId ? p : view.Id;
        var mlCurves = Curves(doc, ml, view).Where(c => c is Line).Cast<Line>().ToList();
        var list = new List<ElementId>();
        foreach (var e in new FilteredElementCollector(doc).OwnedByView(owner).OfClass(typeof(CurveElement)).OfType<DetailCurve>())
        {
            var comment = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            if (comment == Marker + ml.Id.Value) { list.Add(e.Id); continue; }
            if (!string.IsNullOrEmpty(comment) || e.GeometryCurve is not Line l) continue;
            var styleName = (e.LineStyle as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
            if (styleName.IndexOf("match", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (mlCurves.Any(m => OnLine(m, l.GetEndPoint(0)) && OnLine(m, l.GetEndPoint(1)))) list.Add(e.Id);
        }
        return list;
    }

    private static bool OnLine(Line m, XYZ p)
    {
        var a = m.GetEndPoint(0); var d = m.Direction;
        var flat = new XYZ(p.X, p.Y, a.Z);
        var off = flat - a;
        return (off - d * off.DotProduct(d)).GetLength() < 1e-3;
    }

    private static (GraphicsStyle? Style, string? Note, bool Created) LineStyle(Document doc, string? name)
    {
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        var subs = lines.SubCategories.Cast<Category>().ToList();
        if (name != null)
        {
            var found = subs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No line style '{name}'. Line styles: {string.Join(", ", subs.Select(c => c.Name).OrderBy(n => n).Take(60))}");
            return (found.GetGraphicsStyle(GraphicsStyleType.Projection), null, false);
        }
        var match = subs.FirstOrDefault(c => c.Name.IndexOf("match", StringComparison.OrdinalIgnoreCase) >= 0);
        if (match != null) return (match.GetGraphicsStyle(GraphicsStyleType.Projection), null, false);

        const string newName = "Matchline (Sketch ProMax)";
        using var t = new Transaction(doc, "Sketch ProMax: matchline line style");
        try
        {
            t.Start();
            var source = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Matchline);
            var sub = doc.Settings.Categories.NewSubcategory(lines, newName);
            if (source != null)
            {
                sub.LineColor = source.LineColor;
                if (source.GetLineWeight(GraphicsStyleType.Projection) is { } w) sub.SetLineWeight(w, GraphicsStyleType.Projection);
                var pattern = source.GetLinePatternId(GraphicsStyleType.Projection);
                if (pattern != ElementId.InvalidElementId) sub.SetLinePatternId(pattern, GraphicsStyleType.Projection);
            }
            if (t.Commit() != TransactionStatus.Committed)
                return (null, "Revit did not accept a new matchline line style; the lines use the default style. Pass line_style to choose one.", false);
            return (sub.GetGraphicsStyle(GraphicsStyleType.Projection), $"Created line style '{newName}' from the Matchline object style.", true);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            return (null, $"Couldn't create a matchline line style ({ex.Message.Trim()}); the lines use the default style. Pass line_style to choose one.", false);
        }
    }

    /// <summary>Trims each matchline's sketch to the union of the crops of the views it was found in, when Revit's API allows it.</summary>
    private static ToolResult EditSketches(Document doc, List<(View view, Element ml, List<Curve> curves, CropClip? crop)> work, double tol)
    {
        var results = new List<object>();
        foreach (var group in work.GroupBy(w => w.ml.Id.Value))
        {
            var ml = group.First().ml;
            var crops = group.Select(g => g.crop).OfType<CropClip>().ToList();
            if (crops.Count == 0) { results.Add(new { matchline = ml.Id.Value, error = "none of the views has its crop on" }); continue; }
            var sketch = SketchOf(doc, ml);
            if (sketch is null)
            {
                results.Add(new { matchline = ml.Id.Value, apiLimit = true, error = "Revit's API exposes no sketch for this matchline, so it can't be trimmed by code. Use mode 'redraw', or Modify | Matchline > Edit Sketch by hand." });
                continue;
            }
            if (!SketchEditable(doc, sketch))
            {
                results.Add(new { matchline = ml.Id.Value, apiLimit = true, error = "Revit's SketchEditScope does not support matchline sketches, so the API can't trim it. Use mode 'redraw' (hide + detail lines in each view), or select the matchline > Edit Sketch and trim it by hand (it then changes in every view)." });
                continue;
            }
            if (doc.IsWorkshared && ItemBatch.Locked(doc, [ml.Id]) is { } locked) { results.Add(new { matchline = ml.Id.Value, error = locked }); continue; }

            var scope = new SketchEditScope(doc, "Sketch ProMax: trim matchline");
            var errors = new List<string>();
            try
            {
                scope.Start(sketch.Id);
                using (var t = new Transaction(doc, "Trim matchline sketch"))
                {
                    t.Start();
                    int trimmed = 0, removed = 0;
                    foreach (var id in sketch.GetAllElements())
                    {
                        if (doc.GetElement(id) is not CurveElement ce || ce.GeometryCurve is not { IsBound: true } curve) continue;
                        var pieces = CropClip.ClipToAny(crops, curve, tol);
                        if (pieces.Count == 0) { doc.Delete(id); removed++; continue; }
                        if (pieces.Count == 1 && Math.Abs(pieces[0].Length - curve.Length) < 1e-9) continue;
                        ce.SetGeometryCurve(pieces[0], true);
                        foreach (var extra in pieces.Skip(1)) doc.Create.NewModelCurve(extra, ce.SketchPlane);
                        trimmed++;
                    }
                    t.Commit();
                    scope.Commit(new Collect(errors));
                    results.Add(new { matchline = ml.Id.Value, trimmedSegments = trimmed, removedSegments = removed, errors = errors.Count > 0 ? errors : null });
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                if (scope.IsActive) scope.Cancel();
                results.Add(new { matchline = ml.Id.Value, apiLimit = true, error = $"Revit refused to edit the matchline sketch: {ex.Message.Trim()} Use mode 'redraw' instead." });
            }
        }
        return ToolResult.Ok(new { mode = "edit_sketch", results, note = "A matchline's sketch is shared by every view it shows in; it was trimmed to the union of the given views' crops." });
    }

    private sealed class Collect(List<string> errors) : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            var error = false;
            foreach (var f in a.GetFailureMessages())
            {
                errors.Add(f.GetDescriptionText());
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f); else error = true;
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
