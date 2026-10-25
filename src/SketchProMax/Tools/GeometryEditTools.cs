using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Solids, volumes and names used by the geometry edit tools.</summary>
internal static class Geo
{
    private const double CubicFeetToM3 = 0.3048 * 0.3048 * 0.3048;

    public static Element Get(Document doc, long id) =>
        doc.GetElement(new ElementId(id)) ?? throw new ArgumentException($"No element with id {id}.");

    /// <summary>The element's solids (positive volume only; void solids and inside-out solids are left out), in model coordinates.</summary>
    public static List<Solid> Solids(Element e, bool includeNonVisible = false)
    {
        var list = new List<Solid>();
        Collect(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = includeNonVisible }), list);
        return list;
    }

    private static void Collect(GeometryElement? g, List<Solid> into)
    {
        if (g is null) return;
        foreach (var o in g)
            if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), into);
            else if (o is Solid { Volume: > 1e-9 } s) into.Add(s);
    }

    /// <summary>Total solid volume (internal units), or null when Revit exposes no solid for the element.</summary>
    public static double? Volume(Element e)
    {
        var solids = Solids(e);
        return solids.Count == 0 ? null : solids.Sum(s => s.Volume);
    }

    public static double M3(double cubicFeet) => Math.Round(cubicFeet * CubicFeetToM3, 4);

    public static string Describe(Element e) =>
        $"{e.Category?.Name ?? "Element"} {e.Id.Value}" +
        (e is FamilyInstance fi ? $" ({fi.Symbol.FamilyName}: {fi.Symbol.Name})" : e.Name is { Length: > 0 } n ? $" ({n})" : "");

    public static bool BoxesOverlap(Element a, Element b, double tol = 0.01)
    {
        var x = a.get_BoundingBox(null); var y = b.get_BoundingBox(null);
        if (x is null || y is null) return true; // can't tell: let Revit decide
        return x.Min.X <= y.Max.X + tol && x.Max.X >= y.Min.X - tol && x.Min.Y <= y.Max.Y + tol && x.Max.Y >= y.Min.Y - tol
               && x.Min.Z <= y.Max.Z + tol && x.Max.Z >= y.Min.Z - tol;
    }

    public static string Reason(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
}

/// <summary>
/// In-place families: Revit does not hand add-ins their geometry reliably (often none at all), cannot cut with their
/// voids through the API, and cannot host on them. Tools check for them up front and say so, instead of returning
/// empty results.
/// </summary>
internal static class InPlace
{
    public static bool Is(Element? e) => e is FamilyInstance fi && fi.Symbol?.Family?.IsInPlace == true;

    /// <summary>True when the element is in-place and Revit gives no solid for it (so geometry checks can't see it).</summary>
    public static bool Hidden(Element e) => Is(e) && Geo.Solids(e, includeNonVisible: true).Count == 0;

    public static string Why(Element e) =>
        $"{Geo.Describe(e)} is an in-place family. Revit does not expose an in-place family's geometry (solids, faces, " +
        "voids) to add-ins, so it can't be measured, clash-checked, hosted on or cut with through the API. Work on it " +
        "in Revit (select it > Edit In-Place, or Modify > Cut/Join Geometry), or remake it as a loadable family " +
        "(for a void: a face-based void family with 'Cut with Voids When Loaded' on) and run this again. " + GuideHint;

    /// <summary>Where the step-by-step conversion guide is, for results that meet in-place families.</summary>
    public const string GuideHint = "Step-by-step guide to make it loadable: in_place_guide with action 'convert'.";

    public const string Short = "in-place family: Revit does not expose its geometry to add-ins";
}

/// <summary>
/// Positive solids for the unattached voids of a loadable void family instance, which Revit doesn't return from
/// get_Geometry: the family is opened (EditFamily), set to the instance's type and parameter values, and its void forms'
/// geometry is read and placed with the instance's transform. Nested families' voids are not included. Needs no open
/// transaction in the project. In-place families and families in linked models can't be opened this way.
/// </summary>
internal static class VoidSolids
{
    /// <summary>Opening families is slow (each EditFamily loads a document), so a run opens at most this many.</summary>
    public const int MaxOpens = 20;

    /// <summary>What one run has learned: rebuilt solids by key, families that failed, the parameters that drive each family.</summary>
    public sealed class Session
    {
        internal readonly Dictionary<string, List<Solid>?> Cache = new();
        internal readonly Dictionary<long, string> Failed = new();
        internal readonly Dictionary<long, List<string>> Driving = new();
        public int Opens { get; internal set; }
    }

    /// <param name="capped">True when the instance wasn't rebuilt because the run already opened <see cref="MaxOpens"/> families.</param>
    public static List<Solid>? Rebuild(Document doc, FamilyInstance fi, Session session, out string? why, out bool capped)
    {
        why = null; capped = false;
        var family = fi.Symbol.Family;
        if (family.IsInPlace) { why = InPlace.Short; return null; }
        if (fi.Document.IsLinked) { why = "void family in a linked model: open the link to check it"; return null; }
        if (!family.IsEditable) { why = "the family can't be opened for editing"; return null; }
        var familyId = family.Id.Value;
        if (session.Failed.TryGetValue(familyId, out var failed)) { why = failed; return null; }

        List<Solid>? local = null;
        var known = session.Driving.TryGetValue(familyId, out var driving);
        var key = known ? Key(fi, driving!) : null;
        if (key is null || !session.Cache.TryGetValue(key, out local))
        {
            if (session.Opens >= MaxOpens) { capped = true; why = $"not rebuilt: at most {MaxOpens} families are opened per run"; return null; }
            session.Opens++;
            local = null;
            string? failure = null;
            Document? famDoc = null;
            try
            {
                famDoc = doc.EditFamily(family);
                var mgr = famDoc.FamilyManager;
                if (!known) { driving = DrivingParameters(famDoc); session.Driving[familyId] = driving; key = Key(fi, driving); }
                using var t = new Transaction(famDoc, "match instance");
                t.Start();
                var ft = mgr.Types.Cast<FamilyType>().FirstOrDefault(x => x.Name == fi.Symbol.Name);
                if (ft != null) mgr.CurrentType = ft;
                foreach (FamilyParameter fp in mgr.Parameters)
                {
                    if (fp.IsDeterminedByFormula || fp.IsReporting || fp.IsReadOnly) continue;
                    var p = fp.IsInstance ? fi.LookupParameter(fp.Definition.Name) : fi.Symbol.LookupParameter(fp.Definition.Name);
                    if (p is null || p.StorageType != fp.StorageType) continue;
                    try
                    {
                        if (p.StorageType == StorageType.Double) mgr.Set(fp, p.AsDouble());
                        else if (p.StorageType == StorageType.Integer) mgr.Set(fp, p.AsInteger());
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { /* keep the family's value */ }
                }
                famDoc.Regenerate();
                local = [];
                var voids = new FilteredElementCollector(famDoc).OfClass(typeof(GenericForm)).Cast<GenericForm>().Where(f => !f.IsSolid).ToList();
                foreach (var form in voids)
                {
                    var g = form.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                    if (g is null) continue;
                    foreach (var o in g)
                        if (o is Solid s && Math.Abs(s.Volume) > 1e-9) local.Add(Outward(form, s) is { } solid ? solid : SolidUtils.Clone(s));
                }
                t.RollBack();
                if (local.Count == 0) { failure = "no void forms found in the family (voids in nested families aren't read)"; local = null; }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or ArgumentException)
            {
                failure = "couldn't open the family: " + Geo.Reason(ex);
                local = null;
            }
            finally { famDoc?.Close(false); }
            // A family that failed fails the same way for every instance: never open it again this run.
            if (failure != null) { session.Failed[familyId] = failure; why = failure; return null; }
            if (key != null) session.Cache[key] = local;
        }
        if (local is null) { why ??= "not rebuilt"; return null; }
        var tr = fi.GetTotalTransform();
        var placed = local.Select(s => SolidUtils.CreateTransformed(s, tr)).Where(s => s.Volume > 1e-9).ToList();
        if (placed.Count == 0) { why = "rebuilt solids came out inside-out (mirrored instance?)"; return null; }
        return placed;
    }

    /// <summary>
    /// A void's own geometry comes inside out (a negative volume), so it came back as "inside-out" and was never used
    /// (the field test's void family, bug log row 27). An extruded void is built again from its profile as an ordinary
    /// solid of the same shape; other forms keep their geometry (null).
    /// </summary>
    private static Solid? Outward(GenericForm form, Solid solid)
    {
        if (solid.Volume > 0 || form is not Extrusion extrusion) return null;
        try
        {
            var normal = extrusion.Sketch.SketchPlane.GetPlane().Normal;
            double start = extrusion.StartOffset, end = extrusion.EndOffset;
            var depth = Math.Abs(end - start);
            if (depth < 1e-9) return null;
            var shift = Transform.CreateTranslation(normal * Math.Min(start, end));
            var loops = new List<CurveLoop>();
            foreach (CurveArray array in extrusion.Sketch.Profile)
            {
                var loop = new CurveLoop();
                foreach (Curve curve in array) loop.Append(curve.CreateTransformed(shift));
                loops.Add(loop);
            }
            Solid built;
            try { built = GeometryCreationUtilities.CreateExtrusionGeometry(loops, normal, depth); }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // The profile drawn the other way round: the loops reversed.
                foreach (var loop in loops) loop.Flip();
                built = GeometryCreationUtilities.CreateExtrusionGeometry(loops, normal, depth);
            }
            return built.Volume > 1e-9 ? built : null;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>The type plus the values of the family's geometry-driving parameters, so instances that differ only in
    /// other values (offsets, marks, project parameters) share one rebuild.</summary>
    private static string Key(FamilyInstance fi, List<string> driving)
    {
        var parts = new List<string> { fi.Symbol.Id.Value.ToString() };
        foreach (var name in driving)
        {
            var p = fi.LookupParameter(name) ?? fi.Symbol.LookupParameter(name);
            if (p is null || !p.HasValue) { parts.Add(name + "=?"); continue; }
            parts.Add(name + "=" + (p.StorageType == StorageType.Double ? Math.Round(p.AsDouble(), 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : p.StorageType == StorageType.Integer ? p.AsInteger().ToString() : p.AsValueString() ?? ""));
        }
        return string.Join("|", parts);
    }

    /// <summary>Settable family parameters that drive geometry: dimension labels, parameters associated to element
    /// parameters (extrusion ends, visibility, nested family parameters), and the inputs of formulas feeding those.</summary>
    private static List<string> DrivingParameters(Document famDoc)
    {
        var mgr = famDoc.FamilyManager;
        var all = mgr.Parameters.Cast<FamilyParameter>().ToList();
        var drive = new HashSet<string>();
        foreach (var fp in all)
            if (fp.AssociatedParameters is { Size: > 0 }) drive.Add(fp.Definition.Name);
        foreach (var d in new FilteredElementCollector(famDoc).OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            try { if (d.FamilyLabel is { } label) drive.Add(label.Definition.Name); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { /* not a labelable dimension */ }
        }
        // Formula inputs: a driving parameter computed by a formula is driven by the parameters the formula names.
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var fp in all)
            {
                if (!drive.Contains(fp.Definition.Name) || !fp.IsDeterminedByFormula || fp.Formula is not { } formula) continue;
                foreach (var other in all)
                    if (formula.Contains(other.Definition.Name) && drive.Add(other.Definition.Name)) grew = true;
            }
        }
        return all.Where(fp => drive.Contains(fp.Definition.Name) && !fp.IsDeterminedByFormula && !fp.IsReporting
                               && fp.StorageType is StorageType.Double or StorageType.Integer)
            .Select(fp => fp.Definition.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// Finds a face of an element that a face-based family can be hosted on, trying in turn: HostObjectUtils
/// (top/bottom/side faces of floors, walls, roofs), the element's geometry with ComputeReferences (also with
/// non-visible objects, then in the active 3D view), the family symbol's geometry of a family instance; and when none
/// of those give a face reference (joined or cut elements often don't), a reference plane laid on the face.
/// Call inside a transaction (the fallback creates a plane).
/// </summary>
internal static class FaceFinder
{
    public sealed record Hit(Reference Reference, XYZ Point, XYZ Normal, string How, bool OnPlane);

    private sealed record Candidate(PlanarFace Face, Reference? Reference, Transform Transform, string How)
    {
        public XYZ Normal => Transform.OfVector(Face.FaceNormal).Normalize();
    }

    /// <param name="want">Which faces count: "top", "bottom", "side", "any", or an outward normal.</param>
    public static Hit Find(Document doc, Element host, string want, XYZ? normal, XYZ? near, XYZ? refDirection, View? view)
    {
        if (InPlace.Is(host)) throw new ArgumentException(InPlace.Why(host));
        var all = Candidates(host, view);
        bool Fits(Candidate c)
        {
            var n = c.Normal;
            if (normal != null) return n.DotProduct(normal.Normalize()) > 0.95;
            return want switch
            {
                "top" => n.Z > 0.95,
                "bottom" => n.Z < -0.95,
                "side" => Math.Abs(n.Z) < 0.2,
                _ => true,
            };
        }
        var fitting = all.Where(Fits).ToList();
        if (fitting.Count == 0)
            throw new ArgumentException($"{Geo.Describe(host)} has no planar {(normal != null ? "face with that normal" : want + " face")} " +
                                        $"({all.Count} planar faces found). Try face 'any' with a point on the face.");

        Candidate Best(IEnumerable<Candidate> list)
        {
            if (near is null) return list.OrderByDescending(c => c.Face.Area).First();
            return list.Select(c =>
            {
                var local = c.Transform.Inverse.OfPoint(near);
                var hit = c.Face.Project(local);
                var plane = Math.Abs((local - c.Face.Origin).DotProduct(c.Face.FaceNormal));
                return (c, inside: hit != null, d: hit?.Distance ?? plane);
            }).OrderBy(x => x.inside ? 0 : 1).ThenBy(x => x.d).First().c;
        }

        var withRef = fitting.Where(c => c.Reference != null).ToList();
        var best = Best(withRef.Count > 0 ? withRef : fitting);
        var n = best.Normal;
        var origin = best.Transform.OfPoint(best.Face.Origin);
        var target = near ?? best.Transform.OfPoint(Center(best.Face));
        var onFace = target - n * (target - origin).DotProduct(n);
        if (best.Reference != null) return new Hit(best.Reference, onFace, n, best.How, false);

        // No face reference: host on a plane laid on the face (a reference plane, else a sketch plane).
        var u = InPlane(n, refDirection);
        var v = n.CrossProduct(u).Normalize();
        Reference? planeRef = null; string how;
        try
        {
            var rp = doc.Create.NewReferencePlane(onFace - u, onFace + u, v, view ?? doc.ActiveView);
            rp.Name = $"SPM face {host.Id.Value} {DateTime.Now:HHmmssfff}";
            planeRef = rp.GetReference();
            how = "reference plane on the face";
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
        {
            var sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(n, onFace));
            planeRef = sp.GetPlaneReference();
            how = "work plane on the face";
        }
        return new Hit(planeRef, onFace, n, how + $" (Revit gave no reference for this face of {Geo.Describe(host)}; " +
                                               "the family is hosted on a plane at the face, so it won't follow the face if the element moves)", true);
    }

    /// <summary>A unit vector in the plane with this normal: the given direction projected, else X (horizontal faces) or along the face.</summary>
    public static XYZ InPlane(XYZ n, XYZ? wanted)
    {
        if (wanted != null)
        {
            var p = wanted - n * wanted.DotProduct(n);
            if (p.GetLength() > 1e-6) return p.Normalize();
        }
        return Math.Abs(n.Z) > 0.9 ? XYZ.BasisX : XYZ.BasisZ.CrossProduct(n).Normalize();
    }

    private static XYZ Center(PlanarFace f)
    {
        var box = f.GetBoundingBox();
        return f.Evaluate((box.Min + box.Max) / 2);
    }

    private static List<Candidate> Candidates(Element host, View? view)
    {
        var list = new List<Candidate>();
        if (host is HostObject ho)
        {
            void FromRefs(Func<IList<Reference>> get, string how)
            {
                IList<Reference> refs;
                try { refs = get(); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { return; }
                foreach (var r in refs)
                    if (ho.GetGeometryObjectFromReference(r) is PlanarFace pf) list.Add(new Candidate(pf, r, Transform.Identity, how));
            }
            FromRefs(() => HostObjectUtils.GetTopFaces(ho), "top face (HostObjectUtils)");
            FromRefs(() => HostObjectUtils.GetBottomFaces(ho), "bottom face (HostObjectUtils)");
            if (ho is Wall)
            {
                FromRefs(() => HostObjectUtils.GetSideFaces(ho, ShellLayerType.Exterior), "exterior face (HostObjectUtils)");
                FromRefs(() => HostObjectUtils.GetSideFaces(ho, ShellLayerType.Interior), "interior face (HostObjectUtils)");
            }
        }
        var variants = new List<(Options o, string how)>
        {
            (new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine }, "element geometry"),
            (new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, DetailLevel = ViewDetailLevel.Fine }, "element geometry incl. non-visible"),
        };
        if (view is View3D { IsTemplate: false }) variants.Add((new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view }, "element geometry in the 3D view"));
        foreach (var (o, how) in variants)
        {
            GeometryElement? g;
            try { g = host.get_Geometry(o); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            Walk(g, Transform.Identity, how, list);
            if (list.Any(c => c.Reference != null)) break; // stop at the first way that gives references
        }
        return list;
    }

    private static void Walk(GeometryElement? g, Transform tr, string how, List<Candidate> list)
    {
        if (g is null) return;
        foreach (var o in g)
        {
            if (o is Solid { Volume: > 1e-9 } s)
            {
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf) list.Add(new Candidate(pf, pf.Reference, tr, how));
            }
            else if (o is GeometryInstance gi)
            {
                // Symbol geometry carries references a family can be hosted on; the instance copy often does not.
                Walk(gi.GetSymbolGeometry(), tr.Multiply(gi.Transform), how + " (family symbol)", list);
                Walk(gi.GetInstanceGeometry(), tr, how + " (instance)", list);
            }
        }
    }
}

/// <summary>Plan loops: cleaning (tiny edges, collinear points, spikes, self-touching points) and grouping into regions.</summary>
internal static class LoopClean
{
    public static double Area(IList<XYZ> p)
    {
        var a = 0.0;
        for (var i = 0; i < p.Count; i++) { var q = p[(i + 1) % p.Count]; a += p[i].X * q.Y - q.X * p[i].Y; }
        return a / 2;
    }

    public static bool Inside(IList<XYZ> poly, XYZ pt)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > pt.Y) != (poly[j].Y > pt.Y) &&
                pt.X < (poly[j].X - poly[i].X) * (pt.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>Points of a curve loop, in order, curves tessellated.</summary>
    public static List<XYZ> Points(CurveLoop loop)
    {
        var pts = new List<XYZ>();
        foreach (var c in loop)
        {
            var t = c.Tessellate();
            for (var i = 0; i + 1 < t.Count; i++) pts.Add(t[i]);
        }
        return pts;
    }

    /// <summary>
    /// Cleans one loop in plan: drops repeated points and edges shorter than tol, removes collinear points and spikes,
    /// then splits the loop where it touches itself (a vertex on another vertex or edge), so each piece is simple.
    /// </summary>
    public static List<List<XYZ>> Clean(IList<XYZ> input, double tol, double angleTolRad = 0.5 * Math.PI / 180)
    {
        var pts = new List<XYZ>();
        foreach (var p in input)
        {
            var q = new XYZ(p.X, p.Y, 0);
            if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(q) > tol) pts.Add(q);
        }
        while (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= tol) pts.RemoveAt(pts.Count - 1);
        Simplify(pts, tol, angleTolRad);
        var result = new List<List<XYZ>>();
        Split(pts, tol, angleTolRad, result, 0);
        return result;
    }

    private static void Simplify(List<XYZ> pts, double tol, double angleTol)
    {
        var sinTol = Math.Sin(angleTol);
        var changed = true; var guard = 0;
        while (changed && pts.Count >= 3 && guard++ < 100000)
        {
            changed = false;
            for (var i = 0; i < pts.Count && pts.Count >= 3; i++)
            {
                var prev = pts[(i - 1 + pts.Count) % pts.Count]; var cur = pts[i]; var next = pts[(i + 1) % pts.Count];
                var a = cur - prev; var b = next - cur;
                double la = a.GetLength(), lb = b.GetLength();
                var drop = la <= tol || lb <= tol;
                if (!drop)
                {
                    var sin = Math.Abs(a.X * b.Y - a.Y * b.X) / (la * lb);
                    // Collinear (or a spike folding back on itself), or within tol of the line through its neighbours.
                    var chord = next - prev;
                    var off = chord.GetLength() > 1e-9 ? Math.Abs(chord.X * (cur.Y - prev.Y) - chord.Y * (cur.X - prev.X)) / chord.GetLength() : 0;
                    drop = sin < sinTol || off < tol / 2;
                }
                if (drop) { pts.RemoveAt(i); i--; changed = true; }
            }
        }
    }

    private static void Split(List<XYZ> pts, double tol, double angleTol, List<List<XYZ>> into, int depth)
    {
        if (pts.Count < 3) return;
        if (depth < 50)
        {
            var n = pts.Count;
            // A vertex lying on a non-adjacent edge: add it to that edge so the touch becomes a shared vertex.
            for (var k = 0; k < n; k++)
                for (var m = 0; m < n; m++)
                {
                    var m1 = (m + 1) % n;
                    if (m == k || m1 == k) continue;
                    var a = pts[m]; var b = pts[m1]; var p = pts[k];
                    var ab = b - a; var len = ab.GetLength();
                    if (len < 1e-9) continue;
                    var t = (p - a).DotProduct(ab) / (len * len);
                    if (t <= 0 || t >= 1) continue;
                    var foot = a + ab * t;
                    if (foot.DistanceTo(p) > tol || foot.DistanceTo(a) <= tol || foot.DistanceTo(b) <= tol) continue;
                    pts.Insert(m + 1, p);
                    Split(pts, tol, angleTol, into, depth + 1);
                    return;
                }
            // Two vertices on the same spot: split the loop there into two loops.
            for (var i = 0; i < n; i++)
                for (var j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;
                    if (pts[i].DistanceTo(pts[j]) > tol) continue;
                    var first = pts.GetRange(i, j - i);
                    var second = pts.GetRange(j, n - j).Concat(pts.GetRange(0, i)).ToList();
                    foreach (var piece in new[] { first, second })
                    {
                        Simplify(piece, tol, angleTol);
                        Split(piece, tol, angleTol, into, depth + 1);
                    }
                    return;
                }
        }
        if (pts.Count >= 3) into.Add(pts);
    }

    /// <summary>Groups loops into regions: each outer loop with the holes directly inside it. Outer loops CCW, holes CW.</summary>
    public static List<List<List<XYZ>>> Regions(List<List<XYZ>> loops, double minArea)
    {
        var sorted = loops.Where(l => l.Count >= 3 && Math.Abs(Area(l)) >= minArea).OrderByDescending(l => Math.Abs(Area(l))).ToList();
        var parent = new int[sorted.Count]; var depth = new int[sorted.Count];
        for (var i = 0; i < sorted.Count; i++)
        {
            parent[i] = -1;
            var probes = Probes(sorted[i]);
            for (var j = i - 1; j >= 0; j--) // smallest larger loop that contains it
            {
                if (probes.Count(p => Inside(sorted[j], p)) * 2 > probes.Count) { parent[i] = j; break; }
            }
            depth[i] = parent[i] < 0 ? 0 : depth[parent[i]] + 1;
        }
        var regions = new List<List<List<XYZ>>>();
        var index = new Dictionary<int, List<List<XYZ>>>();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (depth[i] % 2 == 0)
            {
                var outer = Area(sorted[i]) < 0 ? Enumerable.Reverse(sorted[i]).ToList() : sorted[i];
                var region = new List<List<XYZ>> { outer };
                regions.Add(region); index[i] = region;
            }
            else if (index.TryGetValue(parent[i], out var region))
                region.Add(Area(sorted[i]) > 0 ? Enumerable.Reverse(sorted[i]).ToList() : sorted[i]);
        }
        return regions;
    }

    /// <summary>Points just inside each edge's midpoint (on the loop's left for CCW): robust inside tests for touching loops.</summary>
    private static List<XYZ> Probes(List<XYZ> loop)
    {
        var ccw = Area(loop) > 0 ? 1 : -1;
        var probes = new List<XYZ>();
        for (var i = 0; i < loop.Count; i++)
        {
            var a = loop[i]; var b = loop[(i + 1) % loop.Count];
            var d = b - a; var len = d.GetLength();
            if (len < 1e-9) continue;
            var left = new XYZ(-d.Y, d.X, 0) / len * ccw;
            probes.Add((a + b) / 2 + left * Math.Min(len * 0.01, 0.001));
        }
        return probes;
    }

    public static CurveLoop ToCurveLoop(IList<XYZ> pts, double z)
    {
        var loop = new CurveLoop();
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            loop.Append(Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z)));
        }
        return loop;
    }
}

public sealed class PlaceVoidCutsTool : IRevitTool
{
    public string Name => "place_void_cuts";
    public string Description =>
        "Place cutting void families and cut elements with them, then check the cut geometrically: each element's solid " +
        "volume is measured before and after (after the change is committed), so a cut Revit registered but that removed " +
        "nothing is reported, and by default undone. Face-based void families are hosted on a face of host_id (top, " +
        "bottom, side, or the face nearest the point; joined/cut elements whose faces give no reference get a plane at the " +
        "face). Level-based void families are placed at a point and elevation; Revit often won't cut with those, so use " +
        "face-based voids. The family must have unattached voids and 'Cut with Voids When Loaded' on; in-place voids can't " +
        "be used through the API (see cut_geometry). Each void is its own step: one Revit refuses doesn't undo the others.";
    public string PropertiesJson => """
    {
      "family": {"type": "string", "description": "'Family: Type' of the void family."},
      "voids": {"type": "array", "items": {"type": "object", "properties": {
        "host_id": {"type": "integer", "description": "Face-based families: the element whose face hosts the void (it is also cut)."},
        "face": {"type": "string", "enum": ["top", "bottom", "side", "any"], "description": "Which face of host_id (default: top; 'any' = the face nearest point)."},
        "normal": {"type": "array", "items": {"type": "number"}, "description": "Or the face's outward normal [x, y, z]."},
        "point": {"type": "array", "items": {"type": "number"}, "description": "[x, y] in project units; picks the face and the spot on it. Default: face centre."},
        "elevation": {"type": "number", "description": "Elevation of the point as Revit displays it (project units). Level-based voids need it or level."},
        "level": {"type": "string", "description": "Level-based voids: the level (default: the highest level at or below the elevation)."},
        "direction": {"type": "array", "items": {"type": "number"}, "description": "Orientation of the family's X on the face [x, y, z] (default X, or along the face for side faces)."},
        "rotation_degrees": {"type": "number", "description": "Extra rotation about the face normal."},
        "cut_ids": {"type": "array", "items": {"type": "integer"}, "description": "Elements to cut (default: host_id and every element the void's box overlaps that a void can cut)."},
        "parameters": {"type": "object", "description": "Instance parameters to set, e.g. {\"Width\": 400}; lengths in project units, angles in degrees."}
      }}},
      "remove_ineffective": {"type": "boolean", "description": "Undo cuts that removed no volume, and delete voids that cut nothing (default true)."}
    }
    """;
    public string[] Required => ["family", "voids"];
    public bool ModifiesModel => true;

    private sealed class Spec
    {
        public int Index; public Element? Host; public string Face = "top"; public XYZ? Normal, Near, Direction; public double? Z;
        public Level? Level; public double Rotation; public List<Element> Targets = []; public JsonElement? Parameters;
        public ElementId? VoidId; public string? How; public List<Element> Cut = [];
        public string Label => $"void {Index + 1}" + (Host != null ? $" on {Host.Id.Value}" : "");
    }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var symbol = TypeMatch.Find(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>(), args.String("family"), "family");
        var family = symbol.Family;
        if (family.IsInPlace)
            throw ManualSteps.InPlace(TypeMatch.Label(symbol), "In-place voids can't be placed or used to cut through the Revit API (API limit); a loadable void family can.");
        if (family.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS) is { } cutParam && cutParam.AsInteger() == 0)
            throw new ManualStepException(new HandoffStep
            {
                Kind = "family_setting", What = $"Turn on 'Cut with Voids When Loaded' in the family '{family.Name}' and load it again",
                Where = "Select an instance > Edit Family, then Create tab > Family Category and Parameters; tick it, then Load into Project (overwrite)",
                Why = "With it off, the family's voids can't cut anything in the project, and the API can't change it from here.",
                Recheck = () => doc.GetElement(family.Id) is Family f && f.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS)?.AsInteger() != 0,
            });
        var placement = family.FamilyPlacementType;
        var faceBased = placement == FamilyPlacementType.WorkPlaneBased;
        if (!faceBased && placement != FamilyPlacementType.OneLevelBased)
            return ToolResult.Error($"'{family.Name}' is {placement}; use a face-based (work-plane-based) void family, or a level-based one.");
        var view = doc.ActiveView;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();

        var specs = new List<Spec>();
        var refused = new List<object>();
        var notCuttable = new List<object>();
        var i = 0;
        foreach (var v in args.Array("voids"))
        {
            var s = new Spec { Index = i++ };
            if (v.TryGetProperty("host_id", out var h) && h.ValueKind == JsonValueKind.Number) s.Host = Geo.Get(doc, h.GetInt64());
            if (v.TryGetProperty("face", out var f) && f.ValueKind == JsonValueKind.String) s.Face = f.GetString() ?? "top";
            if (v.TryGetProperty("normal", out var n) && n.ValueKind == JsonValueKind.Array) s.Normal = new XYZ(n[0].GetDouble(), n[1].GetDouble(), n[2].GetDouble());
            if (v.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.Array) s.Direction = new XYZ(d[0].GetDouble(), d[1].GetDouble(), d.GetArrayLength() > 2 ? d[2].GetDouble() : 0);
            if (v.TryGetProperty("elevation", out var el) && el.ValueKind == JsonValueKind.Number) s.Z = Elevations.ToInternalZ(doc, el.GetDouble());
            if (v.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.String) s.Level = RevitHelpers.FindLevel(doc, lv.GetString());
            if (v.TryGetProperty("rotation_degrees", out var r) && r.ValueKind == JsonValueKind.Number) s.Rotation = r.GetDouble() * Math.PI / 180;
            if (v.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Object) s.Parameters = ps.Clone();
            if (v.TryGetProperty("point", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                var hb = s.Host?.get_BoundingBox(null);
                var z = s.Z ?? (p.GetArrayLength() > 2 ? Elevations.ToInternalZ(doc, p[2].GetDouble()) : hb != null ? (hb.Min.Z + hb.Max.Z) / 2 : s.Level?.ProjectElevation ?? 0);
                s.Near = new XYZ(RevitHelpers.ToInternal(doc, p[0].GetDouble()), RevitHelpers.ToInternal(doc, p[1].GetDouble()), z);
            }
            if (v.TryGetProperty("cut_ids", out var c) && c.ValueKind == JsonValueKind.Array)
                s.Targets = c.EnumerateArray().Select(x => Geo.Get(doc, x.GetInt64())).ToList();
            if (s.Host != null && !s.Targets.Any(t => t.Id == s.Host.Id)) s.Targets.Insert(0, s.Host);

            // Up-front refusals: things the API can't do, said plainly.
            if (faceBased && s.Host is null && s.Z is null && s.Level is null)
            { refused.Add(new { item = s.Label, reason = "Face-based void: give host_id (the element whose face hosts it), or an elevation/level to place it on a horizontal work plane." }); continue; }
            if (!faceBased && s.Near is null)
            { refused.Add(new { item = s.Label, reason = "Level-based void: give point and elevation (or level)." }); continue; }
            if (s.Host != null && InPlace.Is(s.Host)) { refused.Add(new { item = s.Label, reason = InPlace.Why(s.Host) }); continue; }
            var bad = s.Targets.Where(t => !InstanceVoidCutUtils.CanBeCutWithVoid(t)).ToList();
            if (bad.Count > 0)
            {
                var why = $"Revit can't cut these with a void family: {string.Join(", ", bad.Select(x => InPlace.Is(x) ? InPlace.Why(x) : Geo.Describe(x)))} (only host elements such as floors and walls, and Generic Model or structural family instances, can be cut).";
                if (bad.Count == s.Targets.Count) { refused.Add(new { item = s.Label, reason = why }); continue; }
                notCuttable.Add(new { item = s.Label, reason = why });
            }
            s.Targets = s.Targets.Except(bad).ToList();
            specs.Add(s);
        }
        if (specs.Count == 0) return ToolResult.Error("Nothing to place: " + JsonSerializer.Serialize(refused));

        // Volumes before, on the committed model.
        var before = new Dictionary<long, double?>();
        foreach (var t in specs.SelectMany(s => s.Targets)) if (!before.ContainsKey(t.Id.Value)) before[t.Id.Value] = Geo.Volume(t);

        // The placements and the removal of ineffective cuts below are one Undo step. Disposing the group unfinished
        // (an exception) rolls it back.
        if (doc.IsModifiable) throw new InvalidOperationException("A transaction is already open; this tool must run on its own.");
        using var group = new TransactionGroup(doc, "Sketch ProMax: Place void cuts");
        group.Start();
        var batch = ItemBatch.Run(doc, "Place void cuts", specs, s => s.Label,
            s => s.Targets.Select(t => t.Id),
            s =>
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                FamilyInstance inst;
                if (faceBased)
                {
                    if (s.Host != null)
                    {
                        var hit = FaceFinder.Find(doc, s.Host, s.Face, s.Normal, s.Near, s.Direction, view);
                        var dir = Rotate(FaceFinder.InPlane(hit.Normal, s.Direction), hit.Normal, s.Rotation);
                        inst = doc.Create.NewFamilyInstance(hit.Reference, hit.Point, dir, symbol);
                        s.How = hit.How;
                    }
                    else
                    {
                        var z = s.Z ?? s.Level!.ProjectElevation;
                        var at = new XYZ(s.Near?.X ?? 0, s.Near?.Y ?? 0, z);
                        var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, at));
                        inst = doc.Create.NewFamilyInstance(plane.GetPlaneReference(), at, Rotate(FaceFinder.InPlane(XYZ.BasisZ, s.Direction), XYZ.BasisZ, s.Rotation), symbol);
                        s.How = "horizontal work plane";
                    }
                }
                else
                {
                    var z = s.Z ?? s.Level?.ProjectElevation ?? s.Near!.Z;
                    var level = s.Level ?? levels.LastOrDefault(l => l.ProjectElevation <= z + 1e-6) ?? levels.First();
                    inst = doc.Create.NewFamilyInstance(new XYZ(s.Near!.X, s.Near.Y, z), symbol, level, StructuralType.NonStructural);
                    var off = inst.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM) ?? inst.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                    if (off is { IsReadOnly: false }) off.Set(z - level.ProjectElevation);
                    var dir = s.Direction is { } dv ? Math.Atan2(dv.Y, dv.X) : 0;
                    if (Math.Abs(dir + s.Rotation) > 1e-9)
                        ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateUnbound(new XYZ(s.Near.X, s.Near.Y, z), XYZ.BasisZ), dir + s.Rotation);
                    s.How = $"level-based on {level.Name}";
                }
                SetParameters(doc, inst, s.Parameters);
                doc.Regenerate();
                if (!InstanceVoidCutUtils.IsVoidInstanceCuttingElement(inst))
                    throw new InvalidOperationException($"'{TypeMatch.Label(symbol)}' can't cut: it has no unattached void, or 'Cut with Voids When Loaded' is off in the family. Turn it on (Family Category and Parameters) and reload the family.");
                s.VoidId = inst.Id;

                var targets = s.Targets.ToList();
                if (targets.Count == 0 && inst.get_BoundingBox(null) is { } bb)
                {
                    var outline = new Outline(bb.Min, bb.Max);
                    targets = new FilteredElementCollector(doc).WherePasses(new BoundingBoxIntersectsFilter(outline)).WhereElementIsNotElementType()
                        .Where(e => e.Id != inst.Id && e.Category is { CategoryType: CategoryType.Model } && !(e is FamilyInstance fi && fi.SuperComponent != null)
                                    && InstanceVoidCutUtils.CanBeCutWithVoid(e) && !InPlace.Is(e)).Take(50).ToList();
                    foreach (var t in targets) if (!before.ContainsKey(t.Id.Value)) before[t.Id.Value] = Geo.Volume(t);
                }
                var cut = new List<Element>();
                foreach (var t in targets)
                {
                    if (!InstanceVoidCutUtils.InstanceVoidCutExists(t, inst)) InstanceVoidCutUtils.AddInstanceVoidCut(doc, t, inst);
                    cut.Add(t);
                }
                s.Cut = cut;
                return new { voidId = inst.Id.Value, hosted = s.How, cuts = cut.Select(t => t.Id.Value) };
            });

        // A rolled-back item's ids may since have been reused by a later item's new element: forget them.
        foreach (var (o, s) in batch.Items.Zip(specs, (o, s) => (o, s))) if (!o.Ok) { s.VoidId = null; s.Cut = []; }

        // Volumes after: the batch has committed, so this is the regenerated model (a rolled-back trial would not show cuts).
        var results = new List<object>();
        var ineffective = new List<(Spec s, Element t)>();
        foreach (var s in specs)
        {
            if (s.VoidId is null || doc.GetElement(s.VoidId) is not FamilyInstance) continue;
            foreach (var t in s.Cut)
            {
                if (!t.IsValidObject) continue;
                var b = before.TryGetValue(t.Id.Value, out var bv) ? bv : null;
                var a = Geo.Volume(t);
                var removed = b is { } x && a is { } y ? x - y : (double?)null;
                var effective = removed is { } rm ? Geo.M3(rm) > 1e-6 : (bool?)null;
                if (effective == false) ineffective.Add((s, t));
                results.Add(new
                {
                    item = s.Label, voidId = s.VoidId.Value, element = Geo.Describe(t),
                    volumeBeforeM3 = b is { } b1 ? Geo.M3(b1) : (double?)null, volumeAfterM3 = a is { } a1 ? Geo.M3(a1) : (double?)null,
                    removedM3 = removed is { } r1 ? Geo.M3(r1) : (double?)null,
                    check = effective switch
                    {
                        true => "cut: volume removed",
                        false => "NO VOLUME REMOVED: " + Hint(doc, t, faceBased, s),
                        _ => InPlace.Is(t) ? "can't verify: " + InPlace.Short : "can't verify: Revit gives no solid for this element",
                    },
                });
            }
        }

        var undone = new List<string>();
        string? removalNote = null;
        if (args.OptBool("remove_ineffective", true) && ineffective.Count > 0)
        {
            using var t = new Transaction(doc, "Sketch ProMax: undo void cuts that removed nothing");
            t.Start();
            foreach (var (s, e) in ineffective)
            {
                var inst = doc.GetElement(s.VoidId!);
                try { if (InstanceVoidCutUtils.InstanceVoidCutExists(e, inst)) InstanceVoidCutUtils.RemoveInstanceVoidCut(doc, e, inst); undone.Add($"cut of {e.Id.Value} by void {inst.Id.Value}"); }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { undone.Add($"could not remove the cut of {e.Id.Value}: {ex.Message}"); }
            }
            foreach (var s in specs.Where(s => s.VoidId != null && doc.GetElement(s.VoidId) is FamilyInstance).Distinct())
            {
                var inst = doc.GetElement(s.VoidId!);
                if (ineffective.Any(x => x.s == s) && InstanceVoidCutUtils.GetElementsBeingCut(inst).Count == 0)
                {
                    doc.Delete(inst.Id);
                    undone.Add($"deleted void {s.VoidId!.Value} (it cut nothing)");
                }
            }
            var status = t.Commit();
            if (status != TransactionStatus.Committed)
            {
                undone.Clear();
                removalNote = $"Revit did not accept removing the cuts that removed nothing ({status}); they are still in the model. Remove them by hand (Modify > Cut > Uncut Geometry, or delete the void).";
            }
        }
        if (batch.Done > 0) group.Assimilate(); else group.RollBack();

        var failures = batch.Failed.Select(f => new { item = f.Item, reason = f.Error + (!faceBased && (f.Error ?? "").IndexOf("cut", StringComparison.OrdinalIgnoreCase) >= 0 ? " " + LevelBasedHint : "") }).ToList();
        return ToolResult.Ok(new
        {
            placed = specs.Count(s => s.VoidId != null && doc.GetElement(s.VoidId) != null),
            failed = failures.Count + refused.Count,
            failures = failures.Count > 0 ? failures : null,
            refused = refused.Count > 0 ? refused : null,
            notCuttable = notCuttable.Count > 0 ? notCuttable : null,
            checks = results.Take(200),
            undone = undone.Count > 0 ? undone : null,
            removalNote,
            warning = !faceBased ? LevelBasedHint : null,
            note = "Volumes are the elements' solid volumes read after the change was committed. Run verify_void_cut later to re-check.",
        });
    }

    private const string LevelBasedHint =
        "Level-based (unhosted) voids are unreliable: Revit can register the cut and remove nothing, or refuse it with " +
        "\"Can't cut instance(s) out of its host\". Use a face-based void family (Generic Model face based, voids " +
        "unattached, 'Cut with Voids When Loaded' on) hosted on the element's face with host_id.";

    private static string Hint(Document doc, Element t, bool faceBased, Spec s)
    {
        var why = new List<string>();
        var inst = doc.GetElement(s.VoidId!);
        if (inst != null && !Geo.BoxesOverlap(inst, t)) why.Add("the void doesn't reach the element (their boxes don't overlap)");
        var joined = JoinGeometryUtils.GetJoinedElements(doc, t).Count;
        if (joined > 0) why.Add($"the element is joined to {joined} element(s): a void across a join may not cut; try cutting the joined elements too (cut_ids) or unjoin them");
        if (!faceBased) why.Add(LevelBasedHint);
        return why.Count > 0 ? string.Join("; ", why) : "Revit kept the cut but the solid is unchanged; check the void's size and position.";
    }

    private static XYZ Rotate(XYZ dir, XYZ axis, double angle) =>
        Math.Abs(angle) < 1e-12 ? dir : Transform.CreateRotation(axis, angle).OfVector(dir);

    internal static void SetParameters(Document doc, Element inst, JsonElement? parameters)
    {
        if (parameters is not { } ps) return;
        foreach (var prop in ps.EnumerateObject())
        {
            var p = inst.LookupParameter(prop.Name);
            if (p is null)
            {
                var type = doc.GetElement(inst.GetTypeId());
                throw new ArgumentException(type?.LookupParameter(prop.Name) != null
                    ? $"'{prop.Name}' is a type parameter; set it on a duplicated type (duplicate_type) and place that type."
                    : $"The family has no parameter '{prop.Name}'.");
            }
            if (p.IsReadOnly) throw new ArgumentException($"'{prop.Name}' is read-only (driven by a formula or the host).");
            var v = prop.Value;
            switch (p.StorageType)
            {
                case StorageType.Double:
                    var spec = p.Definition.GetDataType();
                    var x = v.GetDouble();
                    p.Set(spec == SpecTypeId.Length ? RevitHelpers.ToInternal(doc, x) : spec == SpecTypeId.Angle ? x * Math.PI / 180 : x);
                    break;
                case StorageType.Integer:
                    p.Set(v.ValueKind is JsonValueKind.True or JsonValueKind.False ? (v.GetBoolean() ? 1 : 0) : v.GetInt32());
                    break;
                case StorageType.String:
                    p.Set(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText());
                    break;
                default:
                    throw new ArgumentException($"'{prop.Name}' can't be set from here.");
            }
        }
    }
}

public sealed class VerifyVoidCutTool : IRevitTool
{
    public string Name => "verify_void_cut";
    public string Description =>
        "Check, on the committed model, whether elements are really cut: each element's solid volume now (and against a " +
        "volume you give from before), Revit's own volume parameter, the void instances and solids cutting it, what it is " +
        "joined to, and for each void whether it can cut, cuts this element and reaches it. Use it after a cut instead of " +
        "a rolled-back trial (Revit doesn't show cuts there). In-place families are reported as not checkable.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Elements that should be cut."},
      "void_ids": {"type": "array", "items": {"type": "integer"}, "description": "Optional void instances to check against them (default: the voids Revit says cut them)."},
      "volumes_before": {"type": "object", "description": "Optional {\"element id\": volume in m3 before the cut}, e.g. from place_void_cuts."}
    }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var ids = args.LongList("element_ids");
        if (ids.Count == 0) throw new ArgumentException("Give element_ids.");
        if (ids.Count > 200) throw new ArgumentException("At most 200 elements at a time.");
        var voids = args.LongList("void_ids").Select(id => Geo.Get(doc, id)).ToList();
        var baseline = new Dictionary<long, double>();
        if (args.Has("volumes_before"))
            foreach (var p in args.Raw["volumes_before"].EnumerateObject())
                if (long.TryParse(p.Name, out var k) && p.Value.ValueKind == JsonValueKind.Number) baseline[k] = p.Value.GetDouble();

        var list = new List<object>();
        foreach (var id in ids)
        {
            var e = Geo.Get(doc, id);
            if (InPlace.Hidden(e)) { list.Add(new { element = Geo.Describe(e), verdict = "not checkable: " + InPlace.Short }); continue; }
            var vol = Geo.Volume(e);
            var revit = e.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED) is { StorageType: StorageType.Double } hp ? Geo.M3(hp.AsDouble()) : (double?)null;
            var cutting = Safe(() => InstanceVoidCutUtils.GetCuttingVoidInstances(e)) ?? [];
            var solidsCutting = Safe(() => SolidSolidCutUtils.GetCuttingSolids(e)) ?? [];
            var joined = Safe(() => JoinGeometryUtils.GetJoinedElements(doc, e)) ?? [];
            var check = voids.Count > 0 ? voids : cutting.Select(x => doc.GetElement(x)).OfType<Element>().ToList();
            double? removed = vol is { } v && baseline.TryGetValue(id, out var b) ? Math.Round(b - Geo.M3(v), 4) : null;
            var voidInfo = check.Select(x => new
            {
                voidId = x.Id.Value,
                family = (x as FamilyInstance)?.Symbol.FamilyName,
                canCut = InPlace.Is(x) ? "no: " + InPlace.Short : InstanceVoidCutUtils.IsVoidInstanceCuttingElement(x) ? "yes" : "no: no unattached void, or 'Cut with Voids When Loaded' is off",
                cutsThisElement = InstanceVoidCutUtils.InstanceVoidCutExists(e, x),
                reaches = Geo.BoxesOverlap(x, e),
            }).ToList();
            string verdict;
            if (vol is null) verdict = "no solid geometry exposed; can't verify";
            else if (removed is { } r) verdict = r > 1e-6 ? $"cut: {r} m3 removed since the volume given" : "NOT CUT: the volume is the same as before";
            else if (check.Count == 0 && solidsCutting.Count == 0) verdict = "no void or solid cuts this element";
            else if (voidInfo.Any(x => x.cutsThisElement && !x.reaches)) verdict = "a void is registered as cutting it but doesn't reach it: likely nothing is removed";
            else verdict = "cut registered; give volumes_before to confirm volume was removed";
            list.Add(new
            {
                element = Geo.Describe(e), solidVolumeM3 = vol is { } vv ? Geo.M3(vv) : (double?)null, revitVolumeM3 = revit,
                removedM3 = removed, voids = voidInfo, cutBySolids = solidsCutting.Select(x => x.Value),
                joinedTo = joined.Take(20).Select(x => x.Value), joinedCount = joined.Count, verdict,
            });
        }
        return ToolResult.Ok(new { elements = list, note = "Read from the committed model. A cut made inside a transaction that was rolled back never shows here." });
    }

    private static ICollection<ElementId>? Safe(Func<ICollection<ElementId>> f)
    {
        try { return f(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }
}

public sealed class CutOpeningsInJoinedTool : IRevitTool
{
    public string Name => "cut_openings_in_joined";
    public string Description =>
        "Cut openings (plan outlines) in floors, roofs, ceilings, beams or columns that are joined to other elements, " +
        "where a plain opening fails with \"Can't cut joined element\". Each opening is its own step: it unjoins the joins " +
        "in the opening's way, cuts, then rejoins them in their original order (switching the order if Revit wants it), " +
        "each rejoin its own step. If the cut still fails it retries with all joins off, then as one opening of the merged " +
        "outline when the opening overlaps an existing void (e.g. rebate strips around a pit). Reports only failures and " +
        "the rejoins Revit refused.";
    public string PropertiesJson => """
    {
      "openings": {"type": "array", "items": {"type": "object", "properties": {
        "host_id": {"type": "integer"},
        "outline": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "[[x, y], ...] closed plan polygon, project units."},
        "label": {"type": "string"}
      }, "required": ["host_id", "outline"]}},
      "unjoin": {"type": "string", "enum": ["overlapping", "all", "none"], "description": "Which joins to undo first: those whose element overlaps the opening (default), all, or none."},
      "merge_with_existing_voids": {"type": "boolean", "description": "Last try: one opening of the outline merged with the existing openings/holes it overlaps (default true)."},
      "merge_overlapping": {"type": "boolean", "description": "Merge requested openings on the same host that overlap or touch into one outline first (default false)."},
      "perpendicular": {"type": "boolean", "description": "Floors/roofs: cut square to the face instead of vertically (default false)."}
    }
    """;
    public string[] Required => ["openings"];
    public bool ModifiesModel => true;

    private sealed class Op
    {
        public string Label = ""; public Element Host = null!; public List<CurveLoop> Outline = []; public double Z;
        public List<(Element other, bool otherCutsHost)> Joins = []; public List<Element> Overlapping = [];
        public List<Element> Unjoined = []; public ElementId? Created; public string? How; public List<string> Attempts = [];
        public bool HasExistingVoid; public List<string> Rejoins = [];
    }

    private sealed record Step(Op Op, string Kind, Element? Other, bool OtherCutsHost);

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var mode = args.OptString("unjoin") ?? "overlapping";
        var perpendicular = args.OptBool("perpendicular");
        var merge = args.OptBool("merge_with_existing_voids", true);
        var ops = new List<Op>();
        var n = 0;
        foreach (var o in args.Array("openings"))
        {
            n++;
            var host = Geo.Get(doc, o.GetProperty("host_id").GetInt64());
            if (InPlace.Is(host)) throw new ArgumentException(InPlace.Why(host));
            if (host is Wall) throw new ArgumentException($"Opening {n}: {Geo.Describe(host)} is a wall; this tool cuts plan openings in floors, roofs, ceilings, beams and columns.");
            if (host is not (Floor or RoofBase or Ceiling) && !(host is FamilyInstance fi0 && fi0.StructuralType is StructuralType.Beam or StructuralType.Brace or StructuralType.Column))
                throw new ArgumentException($"Opening {n}: {Geo.Describe(host)} can't take an opening (floors, roofs, ceilings, structural beams, braces and columns can).");
            var bb = host.get_BoundingBox(null);
            ops.Add(new Op
            {
                Label = o.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : $"opening {n} in {host.Id.Value}",
                Host = host, Outline = Outlines.Polygon(doc, o.GetProperty("outline")), Z = bb != null ? (bb.Min.Z + bb.Max.Z) / 2 : 0,
            });
        }
        if (ops.Count == 0) throw new ArgumentException("Give openings.");

        if (args.OptBool("merge_overlapping"))
            ops = ops.GroupBy(o => o.Host.Id.Value).SelectMany(g => MergeTouching(g.ToList())).ToList();

        // Joins and existing voids, read once on the committed model.
        foreach (var op in ops)
        {
            var prism = Prism(op);
            foreach (var id in JoinGeometryUtils.GetJoinedElements(doc, op.Host))
            {
                var other = doc.GetElement(id);
                if (other is null) continue;
                op.Joins.Add((other, JoinGeometryUtils.IsCuttingElementInJoin(doc, other, op.Host)));
                if (mode == "all" || (mode == "overlapping" && Overlaps(other, prism))) op.Overlapping.Add(other);
            }
            op.HasExistingVoid = merge && ExistingVoids(doc, op).Any(v => Intersects(Outlines.Extrude(v.loops), Outlines.Extrude(op.Outline)));
        }

        var steps = new List<Step>();
        foreach (var op in ops)
        {
            steps.Add(new Step(op, "cut", null, false));
            if (mode == "overlapping" && op.Joins.Count > op.Overlapping.Count) steps.Add(new Step(op, "cut with all joins off", null, false));
            if (op.HasExistingVoid) steps.Add(new Step(op, "cut merged outline", null, false));
            foreach (var (other, cuts) in op.Joins)
            {
                steps.Add(new Step(op, "rejoin", other, cuts));
                steps.Add(new Step(op, "rejoin, other order", other, !cuts));
            }
        }

        var batch = ItemBatch.Run(doc, "Openings in joined elements", steps,
            s => s.Other is null ? $"{s.Op.Label}: {s.Kind}" : $"{s.Op.Label}: {s.Kind} {Geo.Describe(s.Other)}",
            s => s.Other is null ? s.Op.Joins.Select(j => j.other.Id).Prepend(s.Op.Host.Id) : [s.Op.Host.Id, s.Other.Id],
            s => s.Other is null ? Cut(doc, s, perpendicular) : Rejoin(doc, s));

        // Report: per opening whether it is cut and how; earlier attempts that a later one rescued are not failures.
        // ItemBatch reports one outcome per step, in order.
        var cutOk = new HashSet<Op>();
        foreach (var (o, step) in batch.Items.Zip(steps, (o, step) => (o, step)))
        {
            if (o.Ok && step.Other is null) cutOk.Add(step.Op);
            if (!o.Ok) step.Op.Attempts.Add((step.Other is null ? step.Kind : $"{step.Kind} {step.Other.Id.Value}") + ": " + o.Error);
            else if (step.Other != null && o.Value is string v && v == "rejoin, other order") step.Op.Rejoins.Add($"{Geo.Describe(step.Op.Host)} / {Geo.Describe(step.Other)}: rejoined with the join order switched");
        }
        // A rolled-back attempt's opening id may have been reused by another opening since: only a committed cut counts.
        foreach (var op in ops) if (!cutOk.Contains(op)) op.Created = null;
        var failed = new List<object>(); var refusedRejoins = new List<object>(); var done = new List<object>();
        foreach (var op in ops)
        {
            var cut = op.Created != null && doc.GetElement(op.Created) is Opening;
            if (!cut) { failed.Add(new { opening = op.Label, host = op.Host.Id.Value, attempts = op.Attempts.Where(a => !a.StartsWith("rejoin")) }); continue; }
            done.Add(new { opening = op.Label, id = op.Created!.Value, how = op.How });
            foreach (var other in op.Unjoined)
                if (other.IsValidObject && !JoinGeometryUtils.AreElementsJoined(doc, op.Host, other))
                    refusedRejoins.Add(new
                    {
                        opening = op.Label, host = Geo.Describe(op.Host), other = Geo.Describe(other),
                        reason = string.Join("; ", op.Attempts.Where(a => a.StartsWith("rejoin") && a.Contains(" " + other.Id.Value + ":")).Select(a => a.Substring(a.IndexOf(':') + 1).Trim()).Distinct()),
                        fix = "Revit won't keep these joined with the opening cut. Leave them unjoined (check the overlap is not counted twice), or rejoin by hand in Revit (Modify > Join).",
                    });
        }
        return ToolResult.Ok(new
        {
            openings = ops.Count, cut = done.Count, failed = failed.Count > 0 ? failed : null,
            rejoinRefused = refusedRejoins.Count > 0 ? refusedRejoins : null,
            created = done.Take(100),
            switchedOrder = ops.SelectMany(o => o.Rejoins).Take(50).ToList() is { Count: > 0 } sw ? sw : null,
        });
    }

    private static object? Cut(Document doc, Step s, bool perpendicular)
    {
        var op = s.Op;
        if (op.Created != null && doc.GetElement(op.Created) is Opening) return "already cut";
        List<Element> off = s.Kind == "cut" ? op.Overlapping : op.Joins.Select(j => j.other).ToList();
        var outline = op.Outline;
        if (s.Kind == "cut merged outline")
        {
            var voids = ExistingVoids(doc, op).Where(v => Intersects(Outlines.Extrude(v.loops), Outlines.Extrude(op.Outline))).ToList();
            var union = Outlines.Extrude(op.Outline);
            foreach (var v in voids) union = BooleanOperationsUtils.ExecuteBooleanOperation(union, Outlines.Extrude(v.loops), BooleanOperationsType.Union);
            var piece = Outlines.Pieces(union).OrderByDescending(p => p.Max(l => Math.Abs(LoopClean.Area(LoopClean.Points(l))))).FirstOrDefault()
                        ?? throw new InvalidOperationException("Could not merge the outline with the existing void.");
            // Outer boundary only: the area inside is already void.
            outline = [piece.OrderByDescending(l => Math.Abs(LoopClean.Area(LoopClean.Points(l)))).First()];
            foreach (var v in voids) if (v.opening != null) doc.Delete(v.opening.Id);
        }
        var unjoined = new List<Element>();
        foreach (var other in off)
            if (JoinGeometryUtils.AreElementsJoined(doc, op.Host, other)) { JoinGeometryUtils.UnjoinGeometry(doc, op.Host, other); unjoined.Add(other); }
        var profile = new CurveArray();
        foreach (var loop in outline)
            foreach (var c in loop) profile.Append(c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, op.Z - c.GetEndPoint(0).Z))));
        Opening opening = op.Host is FamilyInstance fi
            ? doc.Create.NewOpening(fi, profile, Autodesk.Revit.Creation.eRefFace.CenterZ)
            : doc.Create.NewOpening(op.Host, profile, perpendicular);
        doc.Regenerate();
        op.Created = opening.Id;
        op.Unjoined = unjoined;
        op.How = s.Kind == "cut" ? (unjoined.Count > 0 ? $"cut after unjoining {unjoined.Count} element(s)" : "cut directly")
            : s.Kind == "cut with all joins off" ? $"cut after unjoining all {unjoined.Count} joined element(s)"
            : "one opening of the outline merged with the existing void it overlaps";
        return new { opening = opening.Id.Value, unjoined = unjoined.Select(u => u.Id.Value) };
    }

    private static object? Rejoin(Document doc, Step s)
    {
        var op = s.Op; var other = s.Other!;
        if (op.Created is null || doc.GetElement(op.Created) is not Opening || !op.Unjoined.Any(u => u.Id == other.Id)) return "not needed";
        if (JoinGeometryUtils.AreElementsJoined(doc, op.Host, other)) return "already joined";
        JoinGeometryUtils.JoinGeometry(doc, op.Host, other);
        if (JoinGeometryUtils.IsCuttingElementInJoin(doc, other, op.Host) != s.OtherCutsHost) JoinGeometryUtils.SwitchJoinOrder(doc, op.Host, other);
        return s.Kind;
    }

    private static Solid Prism(Op op)
    {
        var bb = op.Host.get_BoundingBox(null);
        var z0 = (bb?.Min.Z ?? op.Z - 10) - 0.1; var h = ((bb?.Max.Z ?? op.Z + 10) + 0.1) - z0;
        var loops = Outlines.Flat(op.Outline).Select(l => CurveLoop.CreateViaTransform(l, Transform.CreateTranslation(new XYZ(0, 0, z0)))).ToList();
        return GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, h);
    }

    private static bool Overlaps(Element other, Solid prism)
    {
        foreach (var s in Geo.Solids(other))
            if (Intersects(s, prism)) return true;
        return InPlace.Is(other); // geometry hidden: assume it is in the way
    }

    private static bool Intersects(Solid a, Solid b)
    {
        try { return (BooleanOperationsUtils.ExecuteBooleanOperation(a, b, BooleanOperationsType.Intersect)?.Volume ?? 0) > 1e-6; }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return true; }
    }

    /// <summary>Openings hosted by the element (and shafts through it), and the holes in a floor's own sketch.</summary>
    private static List<(List<CurveLoop> loops, Opening? opening)> ExistingVoids(Document doc, Op op)
    {
        var found = new List<(List<CurveLoop>, Opening?)>();
        var bb = op.Host.get_BoundingBox(null);
        if (bb != null)
            foreach (var o in new FilteredElementCollector(doc).OfClass(typeof(Opening)).WherePasses(new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max))).Cast<Opening>())
            {
                if (o.IsRectBoundary || o.BoundaryCurves is null || o.BoundaryCurves.Size == 0) continue;
                if (o.Host != null && o.Host.Id != op.Host.Id) continue;
                var loop = new CurveLoop();
                try { foreach (Curve c in o.BoundaryCurves) loop.Append(c); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }
                found.Add(([loop], o.Host?.Id == op.Host.Id ? o : null));
            }
        if (op.Host is Floor f)
        {
            var loops = Outlines.FloorProfile(doc, f);
            if (loops.Count > 1)
                foreach (var hole in loops.OrderByDescending(l => Math.Abs(LoopClean.Area(LoopClean.Points(l)))).Skip(1))
                    found.Add(([hole], null));
        }
        return found;
    }

    private static IEnumerable<Op> MergeTouching(List<Op> ops)
    {
        var groups = ops.Select(o => new List<Op> { o }).ToList();
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < groups.Count && !merged; i++)
                for (var j = i + 1; j < groups.Count && !merged; j++)
                {
                    var a = Grow(groups[i]); var b = Grow(groups[j]);
                    if (!Intersects(a, b)) continue;
                    groups[i].AddRange(groups[j]); groups.RemoveAt(j); merged = true;
                }
        }
        foreach (var g in groups)
        {
            if (g.Count == 1) { yield return g[0]; continue; }
            var union = Outlines.Extrude(g[0].Outline);
            foreach (var o in g.Skip(1)) union = BooleanOperationsUtils.ExecuteBooleanOperation(union, Outlines.Extrude(o.Outline), BooleanOperationsType.Union);
            var pieces = Outlines.Pieces(union);
            foreach (var piece in pieces)
                yield return new Op { Label = string.Join(" + ", g.Select(o => o.Label)), Host = g[0].Host, Z = g[0].Z, Outline = piece };
        }

        // Touching counts as overlapping: grow each outline by a hair before testing.
        static Solid Grow(List<Op> group)
        {
            Solid? u = null;
            foreach (var o in group)
            {
                var loops = Outlines.Flat(o.Outline).Select(l => { try { return CurveLoop.CreateViaOffset(l, 0.003, XYZ.BasisZ); } catch (Autodesk.Revit.Exceptions.ApplicationException) { return l; } }).ToList();
                var s = GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, 1.0);
                u = u is null ? s : BooleanOperationsUtils.ExecuteBooleanOperation(u, s, BooleanOperationsType.Union);
            }
            return u!;
        }
    }
}

public sealed class CreateFloorsFromFacesTool : IRevitTool
{
    public string Name => "create_floors_from_faces";
    public string Description =>
        "Create floors (e.g. blinding, toppings) from the top or bottom faces of elements' solids, optionally after " +
        "subtracting other elements (a boolean difference), or from outlines you give. Outlines are cleaned first: tiny " +
        "edges dropped, collinear points merged, spikes and self-touching points split, and faces with several separate " +
        "regions split into one outline each; each floor is then created in its own step with retries at a coarser " +
        "tolerance, so one bad outline doesn't stop the others.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Elements whose faces to use."},
      "face": {"type": "string", "enum": ["top", "bottom"], "description": "Default top."},
      "subtract_ids": {"type": "array", "items": {"type": "integer"}, "description": "Elements to subtract from those solids first."},
      "outlines": {"type": "array", "items": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}}, "description": "Or loops [[x, y], ...] in project units (holes are found by containment)."},
      "elevation": {"type": "number", "description": "With outlines: floor top elevation as Revit displays it."},
      "floor_type": {"type": "string"},
      "level": {"type": "string", "description": "Default: the highest level at or below each floor's top."},
      "offset": {"type": "number", "description": "Added to the face elevation for the floor top, project units (default 0; e.g. a topping's thickness on a top face)."},
      "structural": {"type": "boolean", "description": "Default false."},
      "tolerance": {"type": "number", "description": "Edges shorter than this are dropped, project units (default 5 mm)."},
      "min_area": {"type": "number", "description": "Skip pieces smaller than this, m2 (default 0.01)."},
      "merge_coplanar": {"type": "boolean", "description": "Merge touching faces at the same height into one outline (default false)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    private sealed record Region(string Label, List<List<XYZ>> Loops, double Top, string? Note);

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var tol = args.OptDouble("tolerance") is { } t ? RevitHelpers.ToInternal(doc, t) : 5 / 304.8;
        if (tol <= 0) throw new ArgumentException("tolerance must be above 0.");
        var minArea = (args.OptDouble("min_area") ?? 0.01) / (0.3048 * 0.3048);
        var offset = args.OptDouble("offset") is { } o ? RevitHelpers.ToInternal(doc, o) : 0;
        var bottom = args.OptString("face") == "bottom";
        var floorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
        var type = args.OptString("floor_type") is { } ft ? TypeMatch.Find(floorTypes, ft, "floor")
                   : doc.GetElement(Floor.GetDefaultFloorType(doc, false)) as FloorType ?? floorTypes.FirstOrDefault() ?? throw new InvalidOperationException("No floor types are loaded.");
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
        var fixedLevel = args.OptString("level") is { } ln ? RevitHelpers.FindLevel(doc, ln) : null;
        var structural = args.OptBool("structural");

        var regions = new List<Region>();
        var skipped = new List<string>();
        if (args.Has("outlines"))
        {
            var top = args.OptDouble("elevation") is { } el ? Elevations.ToInternalZ(doc, el) : throw new ArgumentException("With outlines, give elevation (the floor top).");
            var loops = new List<List<XYZ>>();
            foreach (var loop in args.Array("outlines"))
                loops.AddRange(LoopClean.Clean(loop.EnumerateArray().Select(p => ViewKit.Point(doc, p)).ToList(), tol));
            var k = 0;
            foreach (var r in LoopClean.Regions(loops, minArea)) regions.Add(new Region($"outline region {++k}", r, top + offset, null));
        }
        var ids = args.LongList("element_ids");
        if (ids.Count > 0)
        {
            var cutters = args.LongList("subtract_ids").Select(id => Geo.Get(doc, id)).ToList();
            var cutterSolids = new List<Solid>();
            foreach (var c in cutters)
            {
                if (InPlace.Hidden(c)) { skipped.Add($"subtract {c.Id.Value}: {InPlace.Short}"); continue; }
                cutterSolids.AddRange(Geo.Solids(c));
            }
            var faces = new List<(string label, List<List<XYZ>> loops, double z, double zMin, double zMax)>();
            foreach (var id in ids)
            {
                var e = Geo.Get(doc, id);
                if (InPlace.Hidden(e)) { skipped.Add(InPlace.Why(e)); continue; }
                var solids = Geo.Solids(e);
                if (solids.Count == 0) { skipped.Add($"{Geo.Describe(e)}: Revit gives no solid for it."); continue; }
                var fi = 0;
                foreach (var s0 in solids)
                {
                    var s = s0;
                    foreach (var c in cutterSolids)
                    {
                        try { s = BooleanOperationsUtils.ExecuteBooleanOperation(s, c, BooleanOperationsType.Difference) ?? s; }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { skipped.Add($"{Geo.Describe(e)}: Revit couldn't subtract one of the subtract_ids solids; used it uncut."); }
                    }
                    if (s.Volume < 1e-9) continue;
                    foreach (Face f in s.Faces)
                    {
                        if (f is not PlanarFace pf) continue;
                        var nz = pf.FaceNormal.Z;
                        if (bottom ? nz > -0.999 : nz < 0.999) continue; // horizontal within ~2.5 degrees
                        var loops = pf.GetEdgesAsCurveLoops().Select(LoopClean.Points).ToList();
                        var zs = loops.SelectMany(l => l).Select(p => p.Z).ToList();
                        faces.Add(($"{Geo.Describe(e)} {(bottom ? "bottom" : "top")} face {++fi}", loops, zs.Average(), zs.Min(), zs.Max()));
                    }
                }
            }
            if (args.OptBool("merge_coplanar"))
                faces = faces.GroupBy(f => Math.Round(f.z / Math.Max(tol, 1e-3))).Select(g => g.Count() == 1 ? g.First() : MergeGroup(g.ToList(), tol)).ToList();
            foreach (var f in faces)
            {
                var cleaned = f.loops.SelectMany(l => LoopClean.Clean(l, tol)).ToList();
                var rs = LoopClean.Regions(cleaned, minArea);
                var sloped = f.zMax - f.zMin > tol ? $"face is sloped ({RevitHelpers.FromInternal(doc, f.zMax - f.zMin)} {RevitHelpers.LengthUnitLabel(doc)} fall); floor made flat at its average height" : null;
                for (var r = 0; r < rs.Count; r++)
                    regions.Add(new Region(rs.Count > 1 ? $"{f.label}, region {r + 1} of {rs.Count}" : f.label, rs[r], f.z + offset, sloped));
                if (rs.Count == 0) skipped.Add($"{f.label}: nothing left after cleaning (smaller than min_area).");
            }
        }
        if (regions.Count == 0) return ToolResult.Error("No outlines to make floors from." + (skipped.Count > 0 ? " " + string.Join(" ", skipped) : " Give element_ids or outlines."));
        if (regions.Count > 500) throw new ArgumentException($"{regions.Count} outlines; narrow the elements (500 at most per call).");

        var batch = ItemBatch.Run(doc, "Floors from faces", regions, r => r.Label, _ => [], r =>
        {
            var level = fixedLevel ?? levels.LastOrDefault(l => l.ProjectElevation <= r.Top + 1e-6) ?? levels.First();
            string? lastError = null;
            foreach (var factor in new[] { 1.0, 3.0, 10.0 })
            {
                var loops = new List<CurveLoop>();
                var pieces = r.Loops.Select(l => factor == 1.0 ? new List<List<XYZ>> { l } : LoopClean.Clean(l, tol * factor)).ToList();
                if (pieces[0].Count == 0) { lastError = "outline vanished when simplified"; break; }
                loops.Add(LoopClean.ToCurveLoop(pieces[0].OrderByDescending(p => Math.Abs(LoopClean.Area(p))).First(), level.ProjectElevation));
                foreach (var hole in pieces.Skip(1).SelectMany(p => p)) loops.Add(LoopClean.ToCurveLoop(hole, level.ProjectElevation));
                if (!BoundaryValidation.IsValidHorizontalBoundary(loops)) { lastError = "the cleaned loops still don't form a valid boundary"; continue; }
                Floor floor;
                try { floor = Floor.Create(doc, loops, type.Id, level.Id, structural, null, 0); }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { lastError = Geo.Reason(ex); continue; }
                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(r.Top - level.ProjectElevation);
                return new
                {
                    floor = floor.Id.Value, level = level.Name, top = Elevations.FromInternalZ(doc, r.Top),
                    simplified = factor > 1 ? $"needed a {factor:0}x coarser tolerance" : null, note = r.Note,
                };
            }
            throw new InvalidOperationException((lastError ?? "invalid outline") + ". Check the face, or give its outline cleaned by hand in outlines.");
        });
        return ToolResult.Ok(new
        {
            created = batch.Done, failed = batch.Items.Count - batch.Done, type = TypeMatch.Label(type),
            floors = batch.Items.Where(i => i.Ok).Take(100).Select(i => new { item = i.Item, result = i.Value }),
            failures = batch.Failed.Select(f => new { item = f.Item, reason = f.Error }).Take(50),
            skipped = skipped.Count > 0 ? skipped.Take(30) : null,
        });
    }

    /// <summary>Faces at one height merged by a solid union of thin extrusions; falls back to the faces as they are.</summary>
    private static (string, List<List<XYZ>>, double, double, double) MergeGroup(List<(string label, List<List<XYZ>> loops, double z, double zMin, double zMax)> g, double tol)
    {
        try
        {
            Solid? u = null;
            foreach (var f in g)
                foreach (var region in LoopClean.Regions(f.loops.SelectMany(l => LoopClean.Clean(l, tol)).ToList(), 0))
                {
                    var s = GeometryCreationUtilities.CreateExtrusionGeometry(region.Select(l => LoopClean.ToCurveLoop(l, 0)).ToList(), XYZ.BasisZ, 1.0);
                    u = u is null ? s : BooleanOperationsUtils.ExecuteBooleanOperation(u, s, BooleanOperationsType.Union);
                }
            if (u is null) throw new InvalidOperationException();
            var loops = Outlines.Pieces(u).SelectMany(p => p).Select(LoopClean.Points).ToList();
            return ($"{g.Count} merged faces at {g[0].label}", loops, g.Average(f => f.z), g.Min(f => f.zMin), g.Max(f => f.zMax));
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or ArgumentException)
        {
            return (g[0].label + $" (+{g.Count - 1} faces, not merged)", g.SelectMany(f => f.loops).ToList(), g.Average(f => f.z), g.Min(f => f.zMin), g.Max(f => f.zMax));
        }
    }
}

public sealed class CutGeometryTool : IRevitTool
{
    public string Name => "cut_geometry";
    public string Description =>
        "Revit's Cut/Join Geometry for pairs of elements, one step per pair with a clear result each: cut (a void family " +
        "instance cuts with its unattached voids; solid family instances cut each other; elements Revit can't solid-cut, " +
        "such as floor by floor, are joined with the cutting element winning, unless join_fallback is false), uncut, join, " +
        "unjoin or switch_join_order. Volumes of the cut elements are compared before and after. In-place voids can't cut " +
        "through the API: they are detected up front, and the elements are selected in Revit for you to finish with " +
        "Modify > Cut Geometry.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["cut", "uncut", "join", "unjoin", "switch_join_order"], "description": "Default cut."},
      "cutting_ids": {"type": "array", "items": {"type": "integer"}, "description": "The cutting elements (voids, or the element that should win a join)."},
      "cut_ids": {"type": "array", "items": {"type": "integer"}, "description": "The elements to cut; every cutting x cut pair is done."},
      "pairs": {"type": "array", "items": {"type": "object", "properties": {"cutting": {"type": "integer"}, "cut": {"type": "integer"}}, "required": ["cutting", "cut"]}, "description": "Or explicit pairs."},
      "only_overlapping": {"type": "boolean", "description": "Skip pairs whose boxes don't touch (default true)."},
      "join_fallback": {"type": "boolean", "description": "cut: when Revit can't solid-cut the pair, join them with the cutting element winning (default true)."},
      "select_for_manual": {"type": "boolean", "description": "Select the elements of pairs the API can't do (in-place voids) in Revit (default true)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc.Document;
        var action = args.OptString("action") ?? "cut";
        var pairs = new List<(Element cutting, Element cut)>();
        foreach (var p in args.Array("pairs"))
            pairs.Add((Geo.Get(doc, p.GetProperty("cutting").GetInt64()), Geo.Get(doc, p.GetProperty("cut").GetInt64())));
        var cutting = args.LongList("cutting_ids").Select(id => Geo.Get(doc, id)).ToList();
        var cut = args.LongList("cut_ids").Select(id => Geo.Get(doc, id)).ToList();
        var skippedApart = 0;
        foreach (var a in cutting)
            foreach (var b in cut)
            {
                if (a.Id == b.Id) continue;
                if (args.OptBool("only_overlapping", true) && !Geo.BoxesOverlap(a, b)) { skippedApart++; continue; }
                pairs.Add((a, b));
            }
        if (pairs.Count == 0) return ToolResult.Error(skippedApart > 0 ? $"None of the {skippedApart} pairs touch (by bounding box); set only_overlapping false to try anyway." : "Give pairs, or cutting_ids and cut_ids.");
        if (pairs.Count > 1000) throw new ArgumentException($"{pairs.Count} pairs; do at most 1000 per call.");

        // API limits, found up front: in-place voids can't cut through the API.
        var manual = action is "cut" or "uncut" ? pairs.Where(p => InPlace.Is(p.cutting)).ToList() : [];
        var todo = pairs.Except(manual).ToList();
        string? selected = null;
        if (manual.Count > 0 && args.OptBool("select_for_manual", true))
        {
            var ids = manual.SelectMany(p => new[] { p.cutting.Id, p.cut.Id }).Distinct().ToList();
            uidoc.Selection.SetElementIds(ids);
            selected = $"Selected {ids.Count} elements in Revit.";
        }

        var verify = action is "cut" or "uncut" or "join" or "unjoin" or "switch_join_order";
        var before = new Dictionary<long, double?>();
        if (verify) foreach (var (_, b) in todo) if (!before.ContainsKey(b.Id.Value)) before[b.Id.Value] = Geo.Volume(b);

        var joinFallback = args.OptBool("join_fallback", true);
        var batch = ItemBatch.Run(doc, ActionName(action), todo, p => $"{Geo.Describe(p.cutting)} / {Geo.Describe(p.cut)}",
            p => [p.cutting.Id, p.cut.Id], p => Apply(doc, action, p.cutting, p.cut, joinFallback));

        var results = new List<object>();
        foreach (var (item, (a, b)) in batch.Items.Zip(todo, (i, p) => (i, p)))
        {
            if (!item.Ok) continue;
            var after = b.IsValidObject ? Geo.Volume(b) : null;
            var vb = before.TryGetValue(b.Id.Value, out var x) ? x : null;
            results.Add(new
            {
                cutting = a.Id.Value, cut = b.Id.Value, done = item.Value,
                volumeChangeM3 = vb is { } v0 && after is { } v1 ? Geo.M3(v1 - v0) : (double?)null,
                warning = item.Error,
            });
        }
        return ToolResult.Ok(new
        {
            action, pairs = pairs.Count, done = batch.Done,
            failures = batch.Failed.Count > 0 ? batch.Failed.Select(f => new { pair = f.Item, reason = f.Error }).Take(100) : null,
            results = results.Take(200),
            skippedNotTouching = skippedApart > 0 ? skippedApart : (int?)null,
            manual = manual.Count == 0 ? null : new
            {
                pairs = manual.Select(p => new { cutting = Geo.Describe(p.cutting), cut = Geo.Describe(p.cut) }).Take(100),
                why = "API limit: an in-place void can't cut through the Revit API (InstanceVoidCutUtils takes only loadable families " +
                      "with unattached voids; SolidSolidCutUtils refuses in-place forms with CutNotAppropriateForElements), and its geometry isn't exposed.",
                todo = (selected ?? "") + " In Revit: Modify > Geometry > Cut > Cut Geometry, pick an element to cut, then the in-place void (repeat per element; " +
                       "tick 'Multiple Cuts' on the Options Bar to pick several). Or remake the void as a face-based loadable void family and use place_void_cuts.",
                editMode = "While an in-place family is open (Edit In-Place), Revit doesn't answer add-in calls at all; click Finish Model or Cancel Model before calling Sketch ProMax.",
            },
        });
    }

    private static string ActionName(string a) => a switch { "cut" => "Cut geometry", "uncut" => "Uncut geometry", "join" => "Join geometry", "unjoin" => "Unjoin geometry", _ => "Switch join order" };

    private static object Apply(Document doc, string action, Element a, Element b, bool joinFallback)
    {
        switch (action)
        {
            case "cut":
                if (InstanceVoidCutUtils.IsVoidInstanceCuttingElement(a))
                {
                    if (!InstanceVoidCutUtils.CanBeCutWithVoid(b)) throw new ArgumentException($"{Geo.Describe(b)} can't be cut by a void family (only hosts, Generic Models and structural families can).");
                    if (InstanceVoidCutUtils.InstanceVoidCutExists(b, a)) return "already cut by this void";
                    InstanceVoidCutUtils.AddInstanceVoidCut(doc, b, a);
                    return "cut with the void";
                }
                if (SolidSolidCutUtils.IsAllowedForSolidCut(a) && SolidSolidCutUtils.IsAllowedForSolidCut(b))
                {
                    SolidSolidCutUtils.CanElementCutElement(a, b, out var reason);
                    switch (reason)
                    {
                        case CutFailureReason.CutAllowed: SolidSolidCutUtils.AddCutBetweenSolids(doc, b, a); return "solid cut";
                        case CutFailureReason.CutAlreadyExists: return "already cut";
                        case CutFailureReason.OppositeCutExists: throw new InvalidOperationException("The other element already cuts this one; uncut them first (action uncut with the roles swapped).");
                    }
                }
                if (!joinFallback) throw new InvalidOperationException("Revit can't solid-cut these two (CutNotAppropriateForElements). Allow join_fallback to join them with the cutting element winning.");
                return JoinWinning(doc, a, b) + " (Revit can't solid-cut these, so they were joined with the cutting element winning)";
            case "uncut":
                if (InstanceVoidCutUtils.InstanceVoidCutExists(b, a)) { InstanceVoidCutUtils.RemoveInstanceVoidCut(doc, b, a); return "void cut removed"; }
                if (SolidSolidCutUtils.CutExistsBetweenElements(a, b, out _)) { SolidSolidCutUtils.RemoveCutBetweenSolids(doc, a, b); return "solid cut removed"; }
                throw new InvalidOperationException("There is no cut between these two.");
            case "join":
                return JoinWinning(doc, a, b);
            case "unjoin":
                if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) return "not joined";
                JoinGeometryUtils.UnjoinGeometry(doc, a, b);
                return "unjoined";
            default:
                if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) throw new InvalidOperationException("They aren't joined.");
                JoinGeometryUtils.SwitchJoinOrder(doc, a, b);
                return JoinGeometryUtils.IsCuttingElementInJoin(doc, a, b) ? "first now cuts second" : "second now cuts first";
        }
    }

    private static string JoinWinning(Document doc, Element a, Element b)
    {
        var was = JoinGeometryUtils.AreElementsJoined(doc, a, b);
        if (!was) JoinGeometryUtils.JoinGeometry(doc, a, b);
        if (!JoinGeometryUtils.IsCuttingElementInJoin(doc, a, b)) JoinGeometryUtils.SwitchJoinOrder(doc, a, b);
        return was ? "already joined; cutting element set to win" : "joined, cutting element wins";
    }
}
