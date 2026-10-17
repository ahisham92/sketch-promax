using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Solids for marine and civil structures, kept as shapes in a fitting category with their data in Comments.</summary>
internal static class Shapes
{
    public static double Mm(double mm) => mm / 304.8;

    public static CurveLoop Loop(IList<XYZ> pts)
    {
        var loop = new CurveLoop();
        for (var i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
        return loop;
    }

    /// <summary>A box from plan corners (x0,y0)-(x1,y1) along a local frame, between two heights.</summary>
    public static Solid Block(XYZ origin, XYZ d, XYZ w, double d0, double d1, double w0, double w1, double z0, double z1)
    {
        var o = new XYZ(origin.X, origin.Y, z0);
        var loop = Loop([o + d * d0 + w * w0, o + d * d1 + w * w0, o + d * d1 + w * w1, o + d * d0 + w * w1]);
        return GeometryCreationUtilities.CreateExtrusionGeometry([loop], XYZ.BasisZ, z1 - z0);
    }

    public static Solid Cylinder(XYZ bottom, XYZ axis, double radius, double length)
    {
        var plane = Plane.CreateByNormalAndOrigin(axis, bottom);
        var loop = new CurveLoop();
        loop.Append(Arc.Create(plane, radius, 0, Math.PI));
        loop.Append(Arc.Create(plane, radius, Math.PI, 2 * Math.PI));
        return GeometryCreationUtilities.CreateExtrusionGeometry([loop], axis, length);
    }

    /// <summary>
    /// A cross-section swept along a plan alignment. Profile points are (offset to the right of the alignment, elevation);
    /// each straight leg is one extrusion. Elevations are absolute unless relative is set (then added to the alignment's z).
    /// </summary>
    public static List<Solid> Sweep(IList<XYZ> alignment, IList<(double u, double v)> profile, bool relative = false, double from = 0, double to = double.MaxValue)
    {
        var solids = new List<Solid>();
        var s = 0.0;
        for (var i = 0; i + 1 < alignment.Count; i++)
        {
            var a = alignment[i]; var b = alignment[i + 1];
            var flat = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            var len = flat.GetLength();
            if (len < 1e-3) continue;
            var d = flat / len;
            var right = d.CrossProduct(XYZ.BasisZ);
            double s0 = Math.Max(s, from), s1 = Math.Min(s + len, to);
            if (s1 - s0 > 1e-3)
            {
                var start = new XYZ(a.X, a.Y, 0) + d * (s0 - s);
                if (relative && Math.Abs(b.Z - a.Z) > 1e-6)
                {
                    // Sloped leg: blend between the profile at each end so the top follows the alignment's z.
                    double z0 = a.Z + (b.Z - a.Z) * (s0 - s) / len, z1 = a.Z + (b.Z - a.Z) * (s1 - s) / len;
                    var end = start + d * (s1 - s0);
                    solids.Add(GeometryCreationUtilities.CreateBlendGeometry(
                        Loop(profile.Select(p => start + right * p.u + XYZ.BasisZ * (p.v + z0)).ToList()),
                        Loop(profile.Select(p => end + right * p.u + XYZ.BasisZ * (p.v + z1)).ToList()), null));
                }
                else
                {
                    var baseZ = relative ? a.Z : 0;
                    var pts = profile.Select(p => start + right * p.u + XYZ.BasisZ * (p.v + baseZ)).ToList();
                    solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([Loop(pts)], d, s1 - s0));
                }
            }
            s += len;
        }
        return solids;
    }

    public static double Length(IList<XYZ> alignment) => alignment.Zip(alignment.Skip(1), (p, q) => new XYZ(q.X - p.X, q.Y - p.Y, 0).GetLength()).Where(l => l >= 1e-3).Sum();

    /// <summary>Point and direction at a chainage along the alignment.</summary>
    public static (XYZ at, XYZ dir) At(IList<XYZ> alignment, double chainage)
    {
        var s = 0.0;
        (XYZ b, XYZ dir)? last = null;
        for (var i = 0; i + 1 < alignment.Count; i++)
        {
            var a = alignment[i]; var b = alignment[i + 1];
            var flat = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            var len = flat.GetLength();
            if (len < 1e-3) continue;
            if (chainage <= s + len)
            {
                var t = Math.Max(0, Math.Min(1, (chainage - s) / len));
                return (a + (b - a) * t, flat / len);
            }
            last = (b, flat / len);
            s += len;
        }
        // Past the end: the end of the last real segment.
        return last is { } l ? (l.b, l.dir) : throw new ArgumentException("The alignment has no length.");
    }

    public static ElementId Category(Document doc, BuiltInCategory wanted)
    {
        var id = new ElementId(wanted);
        return DirectShape.IsValidCategoryId(id, doc) ? id : new ElementId(BuiltInCategory.OST_GenericModel);
    }

    public static DirectShape Make(Document doc, BuiltInCategory category, string name, IEnumerable<Solid> solids, string comments, string? mark = null)
    {
        var list = solids.Where(x => x.Volume > 1e-9).Cast<GeometryObject>().ToList();
        if (list.Count == 0) throw new ArgumentException($"{name}: the dimensions give no solid.");
        var shape = DirectShape.CreateElement(doc, Category(doc, category));
        try { shape.SetShape(list); }
        catch (Exception) { doc.Delete(shape.Id); throw; }
        shape.SetName(name);
        shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comments);
        if (mark != null) shape.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
        return shape;
    }

    public static double Volume(IEnumerable<Solid> solids) => UnitUtils.ConvertFromInternalUnits(solids.Sum(s => s.Volume), UnitTypeId.CubicMeters);
}

public sealed class CreatePilesTool : IRevitTool
{
    public string Name => "create_piles";
    public string Description =>
        "Model piles for jetties, quays, dolphins, bridges and buildings: at given points, or a grid over an area, or rows " +
        "along an alignment at a spacing; round (tubular or bored) or square, vertical or raked, from a cut-off level down " +
        "to a toe level, marked P-001... Elevations are project elevations (as level heights), in project units.";
    public string PropertiesJson => """
    {
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "[x, y] pile positions."},
      "grid": {"type": "object", "properties": {"origin": {"type": "array", "items": {"type": "number"}}, "nx": {"type": "integer"}, "ny": {"type": "integer"}, "sx": {"type": "number"}, "sy": {"type": "number"}, "angle": {"type": "number", "description": "Degrees."}}},
      "alignment": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "With row_offsets and spacing: rows of piles along this plan line."},
      "row_offsets": {"type": "array", "items": {"type": "number"}, "description": "Offsets to the right of the alignment."},
      "spacing": {"type": "number"},
      "shape": {"type": "string", "enum": ["round", "square", "tubular"]},
      "size": {"type": "number", "description": "Diameter or side, project units."},
      "wall_thickness": {"type": "number", "description": "Tubular steel piles."},
      "cutoff_level": {"type": "number"}, "toe_level": {"type": "number"},
      "rake": {"type": "number", "description": "Rake as 1 horizontal in n vertical (e.g. 5 for 1:5); 0 = vertical."},
      "rake_direction": {"type": "array", "items": {"type": "number"}, "description": "[dx, dy] plan direction the toe moves (default away from the alignment, to the right)."},
      "mark_prefix": {"type": "string", "description": "Default 'P-'."}
    }
    """;
    public string[] Required => ["size", "cutoff_level", "toe_level"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        double L(string k) => RevitHelpers.ToInternal(doc, args.Double(k));
        // Levels are elevations as Revit displays them (relative to the level elevation base), not internal Z.
        var size = L("size"); var top = Elevations.ToInternalZ(doc, args.Double("cutoff_level")); var toe = Elevations.ToInternalZ(doc, args.Double("toe_level"));
        if (toe >= top) throw new ArgumentException("toe_level must be below cutoff_level.");
        var shape = args.OptString("shape") ?? "round";
        var wall = args.OptDouble("wall_thickness") is { } wt ? RevitHelpers.ToInternal(doc, wt) : 0;
        var rake = args.OptDouble("rake") ?? 0;
        XYZ? rakeDir = args.Has("rake_direction") ? Modeling.Point(doc, args.Raw["rake_direction"]) : null;
        var positions = new List<(XYZ p, XYZ outward)>();
        foreach (var p in args.Array("points")) positions.Add((Modeling.Point(doc, p), rakeDir ?? XYZ.BasisX));
        if (args.Has("grid"))
        {
            var g = args.Raw["grid"];
            JsonElement G(string k) => g.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v : throw new ArgumentException($"grid needs {k} (origin, nx, ny, sx, sy).");
            var o = Modeling.Point(doc, G("origin"));
            int nx = G("nx").GetInt32(), ny = G("ny").GetInt32();
            double sx = RevitHelpers.ToInternal(doc, G("sx").GetDouble()), sy = RevitHelpers.ToInternal(doc, G("sy").GetDouble());
            if (nx < 1 || ny < 1 || (long)nx * ny > 2000) throw new ArgumentException("grid nx and ny must be at least 1 (max 2000 piles).");
            var ang = g.TryGetProperty("angle", out var a) ? a.GetDouble() * Math.PI / 180 : 0;
            XYZ ux = new(Math.Cos(ang), Math.Sin(ang), 0), uy = new(-Math.Sin(ang), Math.Cos(ang), 0);
            for (var i = 0; i < nx; i++) for (var j = 0; j < ny; j++) positions.Add((o + ux * (i * sx) + uy * (j * sy), rakeDir ?? ux));
        }
        if (args.Has("alignment"))
        {
            var al = args.Raw["alignment"].EnumerateArray().Select(p => Modeling.Point(doc, p)).ToList();
            var spacing = RevitHelpers.ToInternal(doc, args.OptDouble("spacing") ?? throw new ArgumentException("Give spacing with alignment."));
            if (spacing < Shapes.Mm(100)) throw new ArgumentException("spacing must be at least 100 mm.");
            var offsets = args.Has("row_offsets") ? args.Raw["row_offsets"].EnumerateArray().Select(x => RevitHelpers.ToInternal(doc, x.GetDouble())).ToList() : [0.0];
            var total = Shapes.Length(al);
            for (var s = 0.0; s <= total + 1e-6; s += spacing)
            {
                var (at, dir) = Shapes.At(al, s);
                var right = dir.CrossProduct(XYZ.BasisZ);
                foreach (var off in offsets) positions.Add((new XYZ(at.X, at.Y, 0) + right * off, rakeDir ?? right));
            }
        }
        if (positions.Count == 0) throw new ArgumentException("Give points, grid or alignment.");
        if (positions.Count > 2000) throw new ArgumentException($"That is {positions.Count} piles; do it in parts (max 2000).");

        var prefix = args.OptString("mark_prefix") ?? "P-";
        var made = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Piles");
        var n = 0;
        var stopped = false;
        foreach (var (p, outward) in positions)
        {
            if (stopped = CancelPoint.Stop("Piles", n, positions.Count)) break;
            var head = new XYZ(p.X, p.Y, top);
            var flatOut = new XYZ(outward.X, outward.Y, 0);
            var axis = rake > 0 && flatOut.GetLength() > 1e-9 ? (flatOut.Normalize() / rake - XYZ.BasisZ).Normalize() : -XYZ.BasisZ;
            var length = (top - toe) / Math.Abs(axis.Z);
            var toePoint = head + axis * length;
            var solids = new List<Solid>();
            if (shape == "square")
            {
                // Square piles: a prism along the axis, faces square to the plan axes.
                var ux = XYZ.BasisY.CrossProduct(axis).Normalize();
                var u = ux * (size / 2); var v = axis.CrossProduct(ux).Normalize() * (size / 2);
                solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([Shapes.Loop([toePoint - u - v, toePoint + u - v, toePoint + u + v, toePoint - u + v])], -axis, length));
            }
            else
            {
                var outer = Shapes.Cylinder(toePoint, -axis, size / 2, length);
                if (shape == "tubular" && wall > 0 && wall < size / 2)
                    outer = BooleanOperationsUtils.ExecuteBooleanOperation(outer, Shapes.Cylinder(toePoint + axis * Shapes.Mm(1), -axis, size / 2 - wall, length + Shapes.Mm(2)), BooleanOperationsType.Difference);
                solids.Add(outer);
            }
            n++;
            var mark = $"{prefix}{n:000}";
            var comments = string.Format(CultureInfo.InvariantCulture, "pile; shape={0}; size={1:0}; length={2:0.###}; cutoff={3:0.###}; toe={4:0.###}; rake={5}",
                shape, size * 304.8, UnitUtils.ConvertFromInternalUnits(length, UnitTypeId.Meters), RevitHelpers.FromInternal(doc, top), RevitHelpers.FromInternal(doc, toe), rake);
            made.Add(Shapes.Make(doc, BuiltInCategory.OST_StructuralFoundation, $"Pile {mark}", solids, comments, mark).Id.Value);
        }
        if (stopped && made.Count == 0) { t.RollBack(); CancelPoint.ThrowIfRequested("No piles were made."); }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the piles when saving them.");
        return ToolResult.Ok(new { piles = made.Count, ids = made.Take(200), firstMark = $"{prefix}001", lengthEach = RevitHelpers.FromInternal(doc, (top - toe) / (rake > 0 ? Math.Abs((new XYZ(1 / rake, 0, -1)).Normalize().Z) : 1)), stopped = CancelPoint.Note(stopped, n, positions.Count) });
    }
}

public sealed class MarineStructureTool : IRevitTool
{
    public string Name => "marine_structure";
    public string Description =>
        "Model marine and civil structures from a few dimensions, along a plan alignment (offsets to its right, levels as " +
        "project elevations, all in project units): revetment or rubble breakwater (core, filter and armour layers on a " +
        "1:n slope), crown wall, gravity block quay wall (stepped block courses), cantilever retaining wall, masonry wall, " +
        "gravity dam, open-piled jetty or quay deck (deck, beams, pile bents), bridge (deck, piers, abutments), and single " +
        "items: floating pontoon, manhole chamber, planter, hopper. Each part is its own element named and marked so it " +
        "schedules. Geometry for drawings and quantities, not a design.";
    public string PropertiesJson => """
    {
      "kind": {"type": "string", "enum": ["revetment", "breakwater", "crown_wall", "gravity_block_wall", "retaining_wall", "masonry_wall", "dam", "jetty", "quay_deck", "bridge", "pontoon", "manhole", "planter", "hopper"]},
      "alignment": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Plan points [x, y] (bridge: [x, y, deck level])."},
      "position": {"type": "array", "items": {"type": "number"}, "description": "Single items: [x, y] centre."},
      "rotation": {"type": "number", "description": "Single items: degrees."},
      "params": {"type": "object", "description": "Dimensions by kind (project units; levels as elevations). revetment/breakwater: crest_level, toe_level, crest_width, slope (n of 1:n seaward), back_slope, layers [{name, thickness}] outermost first (default armour 1500, underlayer 600, filter 300). crown_wall: base_level, height, base_width, stem_thickness, base_thickness, offset. gravity_block_wall: base_level, courses [{height, width}] bottom up, setback side 'right'|'left', block_length (joints along). retaining_wall: base_level, height, stem_top, stem_bottom, base_width, toe, base_thickness. masonry_wall: base_level, height, thickness. dam: base_level, height, crest_width, upstream_slope, downstream_slope. jetty/quay_deck: deck_level, width, deck_thickness, span (bent spacing), piles_per_bent, pile_size, toe_level, beam_depth. bridge: deck_width, deck_depth, spans [lengths] (default equal 30 m), pier_size, pier_base_level, abutment_length. pontoon: length, width, depth, water_level, freeboard. manhole: inner_length, inner_width (or diameter), cover_level, invert_level, wall, base, cover_slab, opening. planter: length, width, height, wall, base, base_level, soil_depth. hopper: top_size, outlet_size, height, wall, outlet_level, legs_height."}
    }
    """;
    public string[] Required => ["kind"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var kind = args.String("kind");
        var p = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (args.Has("params")) foreach (var x in args.Raw["params"].EnumerateObject()) p[x.Name] = x.Value.Clone();
        // "*_level" params are elevations as Revit displays them (relative to the level elevation base), not internal Z.
        var baseZ = Elevations.BaseZ(doc);
        double L(string k, double? fallbackMm = null) => (k.EndsWith("_level", StringComparison.Ordinal) ? baseZ : 0)
            + (p.TryGetValue(k, out var v) ? RevitHelpers.ToInternal(doc, v.GetDouble())
            : fallbackMm is { } f ? Shapes.Mm(f) : throw new ArgumentException($"{kind} needs params.{k}."));
        double N(string k, double fallback) => p.TryGetValue(k, out var v) ? v.GetDouble() : fallback;
        var alignment = args.Has("alignment") ? args.Raw["alignment"].EnumerateArray().Select(x => Modeling.Point(doc, x)).ToList() : [];
        var made = new List<object>();
        string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        void Part(BuiltInCategory cat, string name, List<Solid> solids, string mark)
        {
            var e = Shapes.Make(doc, cat, name, solids, $"{kind}; part={name}; volume={Fmt(Shapes.Volume(solids))}", mark);
            made.Add(new { id = e.Id.Value, part = name, mark, volumeM3 = Math.Round(Shapes.Volume(solids), 2) });
        }
        void NeedAlignment() { if (alignment.Count < 2) throw new ArgumentException($"{kind} needs an alignment of at least two points."); }
        (XYZ c, XYZ d, XYZ w) Placement()
        {
            var c = args.Has("position") ? Modeling.Point(doc, args.Raw["position"]) : throw new ArgumentException($"{kind} needs a position.");
            var a = (args.OptDouble("rotation") ?? 0) * Math.PI / 180;
            var d = new XYZ(Math.Cos(a), Math.Sin(a), 0);
            return (new XYZ(c.X, c.Y, 0), d, XYZ.BasisZ.CrossProduct(d));
        }

        using var t = RevitHelpers.StartTransaction(doc, $"Marine structure ({kind})");
        switch (kind)
        {
            case "revetment":
            case "breakwater":
            {
                NeedAlignment();
                double crest = L("crest_level"), toe = L("toe_level"), cw = L("crest_width", 4000), n = N("slope", 2), nb = N("back_slope", kind == "breakwater" ? n : 1.5);
                if (crest <= toe) throw new ArgumentException("crest_level must be above toe_level.");
                var layers = p.TryGetValue("layers", out var lj) ? lj.EnumerateArray().Select(x => (
                        name: x.TryGetProperty("name", out var nm) ? nm.GetString() ?? "Layer" : "Layer",
                        t: x.TryGetProperty("thickness", out var th) && th.ValueKind == JsonValueKind.Number ? RevitHelpers.ToInternal(doc, th.GetDouble()) : throw new ArgumentException("Each layer needs a thickness."))).ToList()
                    : [("Armour", Shapes.Mm(1500)), ("Underlayer", Shapes.Mm(600)), ("Filter", Shapes.Mm(300))];
                var h = crest - toe;
                // Outer surface: seaward toe at u=0, up the 1:n slope to the crest, across it, and (breakwater) down the back.
                List<(double u, double v)> Surface(double inset)
                {
                    var k = Math.Sqrt(1 + n * n); var kb = Math.Sqrt(1 + nb * nb);
                    var crestZ = crest - inset;
                    var seaToe = (u: inset * k, v: toe);
                    var seaTop = (u: inset * k + (crestZ - toe) * n, v: crestZ);
                    var backTop = (u: n * h + cw + (kind == "breakwater" ? inset * nb - inset * kb : 0), v: crestZ);
                    var backToe = kind == "breakwater" ? (u: n * h + cw + h * nb - inset * kb, v: toe) : (u: n * h + cw, v: toe);
                    return [seaToe, seaTop, backTop, backToe];
                }
                var inner = 0.0;
                var outerFirst = layers.ToList();
                var surfaces = new List<List<(double u, double v)>> { Surface(0) };
                foreach (var layer in outerFirst) { inner += layer.t; surfaces.Add(Surface(inner)); }
                if (surfaces.Any(x => x[1].v <= toe + 1e-3 || x[1].u >= x[2].u - 1e-3 || (kind == "breakwater" && x[2].u >= x[3].u)))
                    throw new ArgumentException("The layers are too thick for this crest width and height; reduce layer thicknesses or widen the crest.");
                for (var i = 0; i < outerFirst.Count; i++)
                {
                    var o = surfaces[i]; var ins = surfaces[i + 1];
                    // Band between two surfaces, closed along the seabed and at the back.
                    var poly = new List<(double u, double v)> { o[0], o[1], o[2], o[3], ins[3], ins[2], ins[1], ins[0] };
                    if (kind != "breakwater") poly = [o[0], o[1], o[2], (ins[2].u, ins[2].v), ins[1], ins[0]];
                    Part(BuiltInCategory.OST_StructuralFoundation, outerFirst[i].name, Shapes.Sweep(alignment, Clean(poly)), $"{kind.ToUpperInvariant()}-{outerFirst[i].name.ToUpperInvariant()}");
                }
                var core = surfaces[surfaces.Count - 1];
                if (core[1].v - core[0].v > 1e-3 && core[2].u - core[1].u > 1e-3)
                    Part(BuiltInCategory.OST_StructuralFoundation, "Core", Shapes.Sweep(alignment, Clean(core)), $"{kind.ToUpperInvariant()}-CORE");
                break;
            }
            case "crown_wall":
            {
                NeedAlignment();
                double b = L("base_level"), hgt = L("height"), bw = L("base_width", 3000), st = L("stem_thickness", 600), bt = L("base_thickness", 800), off = L("offset", 0);
                if (hgt <= bt || st >= bw) throw new ArgumentException("crown_wall: height must exceed base_thickness and stem_thickness must be less than base_width.");
                // L-shape: base slab with the stem on its seaward (left, u=off) edge.
                var poly = new List<(double, double)> { (off, b), (off + bw, b), (off + bw, b + bt), (off + st, b + bt), (off + st, b + hgt), (off, b + hgt) };
                Part(BuiltInCategory.OST_Walls, "Crown wall", Shapes.Sweep(alignment, poly), "CROWN-WALL");
                break;
            }
            case "gravity_block_wall":
            {
                NeedAlignment();
                var b = L("base_level");
                var courses = p.TryGetValue("courses", out var cj) ? cj.EnumerateArray().Select(x => (h: RevitHelpers.ToInternal(doc, x.GetProperty("height").GetDouble()), w: RevitHelpers.ToInternal(doc, x.GetProperty("width").GetDouble()))).ToList()
                    : throw new ArgumentException("gravity_block_wall needs params.courses [{height, width}] from the bottom.");
                var blockLength = p.ContainsKey("block_length") ? L("block_length") : 0;
                var total = Shapes.Length(alignment);
                var z = b; var k = 0;
                foreach (var (h, w) in courses)
                {
                    k++;
                    // Face line at u=0 (front), blocks extend back to the right by their width.
                    var poly = new List<(double, double)> { (0, z), (w, z), (w, z + h), (0, z + h) };
                    if (blockLength > Shapes.Mm(100))
                    {
                        var stagger = k % 2 == 0 ? blockLength / 2 : 0;
                        var solids = new List<Solid>();
                        for (var s = -stagger; s < total; s += blockLength)
                            solids.AddRange(Shapes.Sweep(alignment, poly, false, Math.Max(0, s), Math.Min(total, s + blockLength - Shapes.Mm(20))));
                        Part(BuiltInCategory.OST_Walls, $"Block course {k}", solids, $"GBW-C{k:00}");
                    }
                    else Part(BuiltInCategory.OST_Walls, $"Block course {k}", Shapes.Sweep(alignment, poly), $"GBW-C{k:00}");
                    z += h;
                }
                break;
            }
            case "retaining_wall":
            {
                NeedAlignment();
                double b = L("base_level"), hgt = L("height"), stt = L("stem_top", 300), stb = L("stem_bottom", 500), bw = L("base_width"), toe = L("toe", 800), bt = L("base_thickness", 600);
                if (toe + stb > bw) throw new ArgumentException("toe + stem_bottom is wider than base_width.");
                if (hgt <= bt) throw new ArgumentException("retaining_wall: height must exceed base_thickness.");
                // Toe on the left (u=0), stem front face vertical, heel to the right under the retained soil.
                var poly = new List<(double, double)> { (0, b), (bw, b), (bw, b + bt), (toe + stb, b + bt), (toe + stt, b + hgt), (toe, b + hgt), (toe, b + bt), (0, b + bt) };
                Part(BuiltInCategory.OST_Walls, "Retaining wall", Shapes.Sweep(alignment, Clean(poly)), "RW");
                break;
            }
            case "masonry_wall":
            {
                NeedAlignment();
                double b = L("base_level"), hgt = L("height"), th = L("thickness", 200);
                var level = ConcreteTypes.LevelAt(doc, b);
                var type = ConcreteTypes.Wall(doc, th, "Masonry Wall");
                for (var i = 0; i + 1 < alignment.Count; i++)
                {
                    var w = Wall.Create(doc, Line.CreateBound(new XYZ(alignment[i].X, alignment[i].Y, level.ProjectElevation), new XYZ(alignment[i + 1].X, alignment[i + 1].Y, level.ProjectElevation)),
                        type.Id, level.Id, hgt, b - level.ProjectElevation, false, false);
                    w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("masonry wall");
                    made.Add(new { id = w.Id.Value, part = "Masonry wall", mark = "MW" });
                }
                break;
            }
            case "dam":
            {
                NeedAlignment();
                double b = L("base_level"), hgt = L("height"), cw = L("crest_width", 6000), nu = N("upstream_slope", 0.05), nd = N("downstream_slope", 0.8);
                var poly = new List<(double, double)> { (0, b), (hgt * nu + cw + hgt * nd, b), (hgt * nu + cw, b + hgt), (hgt * nu, b + hgt) };
                Part(BuiltInCategory.OST_Walls, "Gravity dam", Shapes.Sweep(alignment, poly), "DAM");
                break;
            }
            case "jetty":
            case "quay_deck":
            {
                NeedAlignment();
                double deck = L("deck_level"), w = L("width", kind == "jetty" ? 12000 : 30000), dt = L("deck_thickness", 400), span = L("span", 6000), bd = L("beam_depth", 1200);
                var perBent = (int)N("piles_per_bent", kind == "jetty" ? 3 : 5);
                double pile = L("pile_size", 1000), toe = L("toe_level", -20000);
                Part(BuiltInCategory.OST_Floors, "Deck slab", Shapes.Sweep(alignment, [(0, deck - dt), (w, deck - dt), (w, deck), (0, deck)]), $"{kind.ToUpperInvariant()}-DECK");
                var total = Shapes.Length(alignment);
                if (span < Shapes.Mm(1000)) throw new ArgumentException("span must be at least 1 m.");
                if (toe >= deck - dt - bd) throw new ArgumentException("toe_level must be below the pile cap beams.");
                var bents = 0; var piles = new List<Solid>(); var beams = new List<Solid>();
                for (var s = 0.0; s <= total + 1e-6; s += span)
                {
                    bents++;
                    var (at, dir) = Shapes.At(alignment, s);
                    var right = dir.CrossProduct(XYZ.BasisZ);
                    var o = new XYZ(at.X, at.Y, 0);
                    beams.Add(Shapes.Block(o, dir, right, -pile * 0.6, pile * 0.6, 0, w, deck - dt - bd, deck - dt));
                    for (var i = 0; i < perBent; i++)
                    {
                        var u = perBent == 1 ? w / 2 : pile + (w - 2 * pile) * i / (perBent - 1);
                        piles.Add(Shapes.Cylinder(new XYZ(o.X, o.Y, toe) + right * u, XYZ.BasisZ, pile / 2, deck - dt - bd - toe));
                    }
                }
                Part(BuiltInCategory.OST_StructuralFraming, "Pile cap beams", beams, $"{kind.ToUpperInvariant()}-BEAMS");
                Part(BuiltInCategory.OST_StructuralFoundation, "Piles", piles, $"{kind.ToUpperInvariant()}-PILES");
                break;
            }
            case "bridge":
            {
                if (alignment.Count < 2) throw new ArgumentException("bridge needs an alignment [x, y, deck level] of at least two points.");
                double w = L("deck_width", 12000), dd = L("deck_depth", 2000), pier = L("pier_size", 2000), pierBase = L("pier_base_level", 0), ab = L("abutment_length", 6000);
                if (p.ContainsKey("deck_level") && alignment.All(x => Math.Abs(x.Z) < 1e-9)) { var dl = L("deck_level"); alignment = alignment.Select(x => new XYZ(x.X, x.Y, dl)).ToList(); }
                if (alignment.Any(x => x.Z - dd - Shapes.Mm(1500) <= pierBase))
                    throw new ArgumentException("The deck must be more than deck_depth + 1.5 m above pier_base_level; give each alignment point a third value (deck level) or params.deck_level.");
                var total = Shapes.Length(alignment);
                var spans = p.TryGetValue("spans", out var sj) ? sj.EnumerateArray().Select(x => RevitHelpers.ToInternal(doc, x.GetDouble())).ToList() : [];
                if (spans.Count == 0) { var count = Math.Max(1, (int)Math.Round(total / Shapes.Mm(30000))); spans = Enumerable.Repeat(total / count, count).ToList(); }
                // Deck follows the alignment's z (its top), centred on the alignment.
                Part(BuiltInCategory.OST_StructuralFraming, "Deck", Shapes.Sweep(alignment, [(-w / 2, -dd), (w / 2, -dd), (w / 2, 0), (-w / 2, 0)], true), "BRIDGE-DECK");
                var piers = new List<Solid>(); var s = 0.0;
                for (var i = 0; i + 1 < spans.Count; i++)
                {
                    s += spans[i];
                    var (at, dir) = Shapes.At(alignment, s);
                    var right = dir.CrossProduct(XYZ.BasisZ);
                    var soffit = at.Z - dd;
                    piers.Add(Shapes.Block(new XYZ(at.X, at.Y, 0), dir, right, -pier / 2, pier / 2, -w * 0.3, w * 0.3, pierBase, soffit - Shapes.Mm(1500)));
                    piers.Add(Shapes.Block(new XYZ(at.X, at.Y, 0), dir, right, -pier / 2, pier / 2, -w / 2, w / 2, soffit - Shapes.Mm(1500), soffit));
                }
                if (piers.Count > 0) Part(BuiltInCategory.OST_StructuralColumns, "Piers", piers, "BRIDGE-PIERS");
                var abutments = new List<Solid>();
                foreach (var (ch, sign) in new[] { (0.0, -1.0), (total, 1.0) })
                {
                    var (at, dir) = Shapes.At(alignment, ch);
                    var right = dir.CrossProduct(XYZ.BasisZ);
                    abutments.Add(Shapes.Block(new XYZ(at.X, at.Y, 0), dir, right, sign < 0 ? -ab : 0, sign < 0 ? 0 : ab, -w / 2 - Shapes.Mm(500), w / 2 + Shapes.Mm(500), pierBase, at.Z - dd));
                }
                Part(BuiltInCategory.OST_StructuralFoundation, "Abutments", abutments, "BRIDGE-ABUTMENTS");
                break;
            }
            case "pontoon":
            {
                var (c, d, w) = Placement();
                double len = L("length"), wid = L("width"), dep = L("depth", 1200), water = L("water_level", 0), fb = L("freeboard", 500);
                Part(BuiltInCategory.OST_GenericModel, "Floating pontoon", [Shapes.Block(c, d, w, -len / 2, len / 2, -wid / 2, wid / 2, water + fb - dep, water + fb)], "PONTOON");
                break;
            }
            case "manhole":
            case "planter":
            {
                var (c, d, w) = Placement();
                bool mh = kind == "manhole";
                double wall = L("wall", mh ? 200 : 150), bas = L("base", mh ? 250 : 150);
                double topZ = mh ? L("cover_level") : L("base_level") + L("height", 900);
                double botZ = mh ? L("invert_level") - Shapes.Mm(100) : L("base_level");
                var round = p.ContainsKey("diameter");
                var solids = new List<Solid>();
                Solid Shell(double inset, double z0, double z1)
                {
                    if (round) return Shapes.Cylinder(new XYZ(c.X, c.Y, z0), XYZ.BasisZ, L("diameter") / 2 + wall - inset, z1 - z0);
                    double il = L(mh ? "inner_length" : "length") / 2 + (mh ? wall : 0) - inset, iw = L(mh ? "inner_width" : "width") / 2 + (mh ? wall : 0) - inset;
                    return Shapes.Block(c, d, w, -il, il, -iw, iw, z0, z1);
                }
                var cover = mh ? L("cover_slab", 200) : 0;
                var outer = Shell(0, botZ - bas, topZ - cover);
                var cavity = Shell(wall, botZ, topZ - cover + Shapes.Mm(1));
                solids.Add(BooleanOperationsUtils.ExecuteBooleanOperation(outer, cavity, BooleanOperationsType.Difference));
                Part(BuiltInCategory.OST_StructuralFoundation, mh ? "Manhole chamber" : "Planter", solids, mh ? "MH" : "PLANTER");
                if (mh)
                {
                    var slab = Shell(0, topZ - cover, topZ);
                    var opening = L("opening", 600);
                    var hole = Shapes.Block(c, d, w, -opening / 2, opening / 2, -opening / 2, opening / 2, topZ - cover - Shapes.Mm(1), topZ + Shapes.Mm(1));
                    Part(BuiltInCategory.OST_StructuralFoundation, "Manhole cover slab", [BooleanOperationsUtils.ExecuteBooleanOperation(slab, hole, BooleanOperationsType.Difference)], "MH-COVER");
                }
                else if (p.ContainsKey("soil_depth"))
                    Part(BuiltInCategory.OST_Planting, "Planter soil", [Shell(wall, botZ, botZ + L("soil_depth"))], "PLANTER-SOIL");
                break;
            }
            case "hopper":
            {
                var (c, d, w) = Placement();
                double top = L("top_size"), outlet = L("outlet_size", 600), hgt = L("height"), wall = L("wall", 200), z0 = L("outlet_level"), legs = L("legs_height", 0);
                if (top <= 0 || outlet <= 0 || hgt <= 0) throw new ArgumentException("hopper needs positive top_size, outlet_size and height.");
                Solid Frustum(double inset, double ext)
                {
                    // ext extends the inner frustum past both caps (along its slope) so the cut is clean.
                    var slope = (top - outlet) / 2 / hgt;
                    var bottom = Shapes.Loop(Square(c, d, w, outlet / 2 + wall - inset - slope * ext, z0 - ext));
                    var upper = Shapes.Loop(Square(c, d, w, top / 2 + wall - inset + slope * ext, z0 + hgt + ext));
                    return GeometryCreationUtilities.CreateBlendGeometry(bottom, upper, null);
                }
                var body = BooleanOperationsUtils.ExecuteBooleanOperation(Frustum(0, 0), Frustum(wall, Shapes.Mm(5)), BooleanOperationsType.Difference);
                Part(BuiltInCategory.OST_GenericModel, "Hopper", [body], "HOPPER");
                if (legs > 0)
                {
                    var leg = Shapes.Mm(300); var reach = top / 2 + wall - leg;
                    var solids = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }
                        .Select(s => Shapes.Block(c + d * (s.Item1 * reach) + w * (s.Item2 * reach), d, w, -leg / 2, leg / 2, -leg / 2, leg / 2, z0 + hgt - legs - hgt, z0 + hgt)).ToList();
                    Part(BuiltInCategory.OST_StructuralColumns, "Hopper legs", solids, "HOPPER-LEGS");
                }
                break;
            }
            default: throw new ArgumentException($"Unknown kind '{kind}'.");
        }
        if (made.Count == 0) { t.RollBack(); return ToolResult.Error("Nothing was made."); }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the structure when saving it.");
        return ToolResult.Ok(new { kind, parts = made, alignmentLength = alignment.Count > 1 ? RevitHelpers.FromInternal(doc, Shapes.Length(alignment)) : (double?)null, note = "Geometry for drawings and quantities, not a design." });
    }

    private static List<XYZ> Square(XYZ c, XYZ d, XYZ w, double half, double z)
    {
        var o = new XYZ(c.X, c.Y, z);
        return [o - d * half - w * half, o + d * half - w * half, o + d * half + w * half, o - d * half + w * half];
    }

    /// <summary>Drops repeated points so the profile is a valid loop.</summary>
    private static List<(double u, double v)> Clean(List<(double u, double v)> poly)
    {
        var result = new List<(double u, double v)>();
        foreach (var q in poly)
            if (result.Count == 0 || Math.Abs(result[result.Count - 1].u - q.u) + Math.Abs(result[result.Count - 1].v - q.v) > 1e-4) result.Add(q);
        if (result.Count > 1 && Math.Abs(result[0].u - result[result.Count - 1].u) + Math.Abs(result[0].v - result[result.Count - 1].v) < 1e-4) result.RemoveAt(result.Count - 1);
        return result;
    }
}

public sealed class PortFurnitureTool : IRevitTool
{
    public string Name => "port_furniture";
    public string Description =>
        "Place port and marina furniture along a quay or pontoon edge: bollards, fenders, ladders, cleats and crane rails " +
        "(a pair at the crane gauge), at a spacing along the edge line, at the cope level. Uses your family type when given " +
        "(family_type_id); otherwise simple shapes sized from the parameters, marked BL-01, FD-01...";
    public string PropertiesJson => """
    {
      "item": {"type": "string", "enum": ["bollard", "fender", "ladder", "cleat", "crane_rail"]},
      "edge": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Quay edge line [x, y] points; the water is on the left of the direction."},
      "cope_level": {"type": "number", "description": "Top of quay, project elevation in project units."},
      "spacing": {"type": "number", "description": "Along the edge (default bollards 20 m, fenders 15 m, ladders 30 m, cleats 5 m)."},
      "setback": {"type": "number", "description": "Distance back from the edge (default bollards 600 mm)."},
      "size": {"type": "number", "description": "Bollard/cleat size, fender diameter (default by item)."},
      "low_level": {"type": "number", "description": "Fenders and ladders: bottom elevation (default cope - 3 m)."},
      "gauge": {"type": "number", "description": "Crane rail centres (default 30.48 m)."},
      "rail_offset": {"type": "number", "description": "Waterside rail back from the edge (default 2.5 m)."},
      "family_type_id": {"type": "integer", "description": "Place this family type instead of a shape (bollards, fenders, ladders, cleats)."}
    }
    """;
    public string[] Required => ["item", "edge", "cope_level"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var item = args.String("item");
        var edge = args.Raw["edge"].EnumerateArray().Select(x => Modeling.Point(doc, x)).ToList();
        if (edge.Count < 2) throw new ArgumentException("edge needs at least two points.");
        double Opt(string k, double mm) => args.OptDouble(k) is { } v ? RevitHelpers.ToInternal(doc, v) : Shapes.Mm(mm);
        var cope = Elevations.ToInternalZ(doc, args.Double("cope_level"));
        var total = Shapes.Length(edge);
        var made = new List<long>();
        var prefix = item switch { "bollard" => "BL", "fender" => "FD", "ladder" => "LD", "cleat" => "CL", _ => "CR" };
        using var t = RevitHelpers.StartTransaction(doc, $"Port furniture ({item})");
        if (item == "crane_rail")
        {
            double gauge = Opt("gauge", 30480), off = Opt("rail_offset", 2500), head = Shapes.Mm(150), height = Shapes.Mm(200);
            foreach (var (u, k) in new[] { (off, 1), (off + gauge, 2) })
            {
                // Edge direction has water on the left, so the land side is to the right: positive offsets.
                var solids = Shapes.Sweep(edge, [(u - head / 2, cope - height), (u + head / 2, cope - height), (u + head / 2, cope), (u - head / 2, cope)]);
                made.Add(Shapes.Make(doc, BuiltInCategory.OST_StructuralFraming, $"Crane rail {k}", solids, $"crane rail; gauge={RevitHelpers.FromInternal(doc, gauge)}", $"CR-{k:00}").Id.Value);
            }
        }
        else
        {
            var spacing = Opt("spacing", item switch { "bollard" => 20000, "fender" => 15000, "ladder" => 30000, _ => 5000 });
            if (spacing < Shapes.Mm(500)) throw new ArgumentException("spacing must be at least 500 mm.");
            var symbol = args.OptLong("family_type_id") is { } ft ? Modeling.Symbol(doc, ft) : null;
            var n = 0;
            for (var s = spacing / 2; s < total; s += spacing)
            {
                var (at, dir) = Shapes.At(edge, s);
                var land = dir.CrossProduct(XYZ.BasisZ); // right of the direction: away from the water
                var o = new XYZ(at.X, at.Y, 0);
                n++;
                var mark = $"{prefix}-{n:00}";
                if (symbol != null)
                {
                    var setbackF = item is "bollard" or "cleat" ? Opt("setback", item == "bollard" ? 600 : 300) : 0;
                    var level = ConcreteTypes.LevelAt(doc, cope);
                    var pt = new XYZ(o.X, o.Y, cope) + land * setbackF;
                    var fi = doc.Create.NewFamilyInstance(pt, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    var ang = Math.Atan2(-land.Y, -land.X);
                    ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(new XYZ(pt.X, pt.Y, 0), new XYZ(pt.X, pt.Y, 1)), ang);
                    fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
                    made.Add(fi.Id.Value);
                    continue;
                }
                var solids = new List<Solid>();
                switch (item)
                {
                    case "bollard":
                    {
                        var size = Opt("size", 450); var c = new XYZ(o.X, o.Y, cope) + land * Opt("setback", 600);
                        solids.Add(Shapes.Cylinder(c, XYZ.BasisZ, size / 2, size * 1.2));
                        solids.Add(Shapes.Cylinder(c + XYZ.BasisZ * size * 1.2, XYZ.BasisZ, size * 0.75, size * 0.2));
                        break;
                    }
                    case "cleat":
                    {
                        var size = Opt("size", 300); var c = o + land * Opt("setback", 300);
                        solids.Add(Shapes.Block(c, dir, land, -size, size, -size / 6, size / 6, cope, cope + size / 2));
                        break;
                    }
                    case "fender":
                    {
                        var dia = Opt("size", 1000); var low = args.OptDouble("low_level") is { } lw ? Elevations.ToInternalZ(doc, lw) : cope - Shapes.Mm(3000);
                        solids.Add(Shapes.Cylinder(new XYZ(o.X, o.Y, low) - land * (dia / 2), XYZ.BasisZ, dia / 2, cope - low));
                        break;
                    }
                    case "ladder":
                    {
                        var low = args.OptDouble("low_level") is { } lw ? Elevations.ToInternalZ(doc, lw) : cope - Shapes.Mm(3000);
                        var bar = Shapes.Mm(40); var half = Shapes.Mm(225); var c = o - land * Shapes.Mm(150);
                        solids.Add(Shapes.Block(c, dir, land, -half - bar, -half, -bar / 2, bar / 2, low, cope + Shapes.Mm(1000)));
                        solids.Add(Shapes.Block(c, dir, land, half, half + bar, -bar / 2, bar / 2, low, cope + Shapes.Mm(1000)));
                        for (var z = low + Shapes.Mm(300); z < cope; z += Shapes.Mm(300))
                            solids.Add(Shapes.Block(c, dir, land, -half, half, -bar / 2, bar / 2, z, z + Shapes.Mm(25)));
                        break;
                    }
                }
                made.Add(Shapes.Make(doc, BuiltInCategory.OST_GenericModel, $"{char.ToUpperInvariant(item[0])}{item.Substring(1)} {mark}", solids, $"port furniture; item={item}", mark).Id.Value);
            }
        }
        if (made.Count == 0) { t.RollBack(); return ToolResult.Error("The edge is shorter than one spacing; nothing placed."); }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the furniture when saving it.");
        return ToolResult.Ok(new { item, placed = made.Count, ids = made.Take(200), edgeLength = RevitHelpers.FromInternal(doc, total) });
    }
}

public sealed class DesignVesselTool : IRevitTool
{
    // Typical design vessels: length overall, beam, moulded depth, laden draft (m), displacement (t), DWT (t).
    private static readonly Dictionary<string, (double loa, double b, double d, double t, double disp, double dwt)> Presets = new()
    {
        ["tug"] = (32, 12, 5, 4.5, 700, 300), ["barge"] = (60, 18, 4, 3, 3500, 2800), ["hopper_barge"] = (70, 15, 5.5, 4.5, 4000, 3000),
        ["hopper_dredger"] = (130, 26, 10, 8.5, 22000, 15000), ["feeder"] = (150, 25, 13, 9, 18000, 13000),
        ["panamax"] = (294, 32.3, 21, 12, 70000, 52000), ["post_panamax"] = (335, 43, 25, 14, 125000, 95000),
        ["ulcv"] = (400, 61, 33, 16, 260000, 200000), ["bulk_carrier"] = (225, 32, 20, 14, 90000, 75000),
        ["tanker"] = (250, 44, 22, 15, 135000, 115000), ["ro_ro"] = (200, 32, 22, 8, 40000, 20000), ["yacht"] = (40, 8, 4, 2.5, 300, 50),
    };

    public string Name => "design_vessel";
    public string Description =>
        "Model a design vessel at a berth or in a channel for layout and clearance checks: a hull (with bow and stern " +
        "taper), deck and superstructure from a preset (tug, barge, hopper barge, hopper dredger with its open hopper, " +
        "feeder, panamax, post-panamax, ULCV, bulk carrier, tanker, ro-ro, yacht) or your own dimensions, floating at a " +
        "water level with its draft. Data (LOA, beam, draft, displacement, DWT) goes in Comments for vessel_collision_check.";
    public string PropertiesJson => """
    {
      "preset": {"type": "string", "enum": ["tug", "barge", "hopper_barge", "hopper_dredger", "feeder", "panamax", "post_panamax", "ulcv", "bulk_carrier", "tanker", "ro_ro", "yacht"]},
      "length": {"type": "number", "description": "LOA in metres (overrides preset)."}, "beam": {"type": "number"}, "depth": {"type": "number"}, "draft": {"type": "number"},
      "displacement": {"type": "number", "description": "Tonnes."}, "dwt": {"type": "number", "description": "Tonnes."},
      "position": {"type": "array", "items": {"type": "number"}, "description": "[x, y] midship, project units."},
      "heading": {"type": "number", "description": "Degrees from +X to the bow."},
      "water_level": {"type": "number", "description": "Project elevation, project units (default 0)."},
      "loaded": {"type": "boolean", "description": "Default true (laden draft); false = 60% draft."},
      "name": {"type": "string"}
    }
    """;
    public string[] Required => ["position"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var preset = args.OptString("preset") ?? "feeder";
        if (!Presets.TryGetValue(preset, out var v)) throw new ArgumentException($"Unknown preset '{preset}'.");
        double M(string k, double fallback) => (args.OptDouble(k) ?? fallback) / 0.3048;
        double loa = M("length", v.loa), beam = M("beam", v.b), depth = M("depth", v.d), draft = M("draft", v.t) * (args.OptBool("loaded", true) ? 1 : 0.6);
        var disp = args.OptDouble("displacement") ?? v.disp; var dwt = args.OptDouble("dwt") ?? v.dwt;
        var c = Modeling.Point(doc, args.Raw["position"]);
        var heading = (args.OptDouble("heading") ?? 0) * Math.PI / 180;
        XYZ d = new(Math.Cos(heading), Math.Sin(heading), 0), w = XYZ.BasisZ.CrossProduct(d);
        var water = args.OptDouble("water_level") is { } wl ? Elevations.ToInternalZ(doc, wl) : Elevations.BaseZ(doc);
        double keel = water - draft, deck = keel + depth;
        var o = new XYZ(c.X, c.Y, 0);
        // Hull in plan: parallel midbody, bow taper to a point, square transom; extruded keel to deck.
        double half = beam / 2, bowStart = loa * 0.3, stern = -loa / 2, bow = loa / 2;
        var plan = new List<XYZ>
        {
            o + d * stern - w * half, o + d * bowStart - w * half, o + d * (bow - loa * 0.05) - w * (half * 0.35), o + d * bow,
            o + d * (bow - loa * 0.05) + w * (half * 0.35), o + d * bowStart + w * half, o + d * stern + w * half,
        }.Select(p => new XYZ(p.X, p.Y, keel)).ToList();
        var hull = GeometryCreationUtilities.CreateExtrusionGeometry([Shapes.Loop(plan)], XYZ.BasisZ, depth);
        var solids = new List<Solid>();
        if (preset is "hopper_barge" or "hopper_dredger")
        {
            // Open hopper well amidships.
            var well = Shapes.Block(o, d, w, -loa * 0.2, loa * 0.2, -half * 0.7, half * 0.7, keel + Shapes.Mm(1000), deck + Shapes.Mm(10));
            hull = BooleanOperationsUtils.ExecuteBooleanOperation(hull, well, BooleanOperationsType.Difference);
        }
        solids.Add(hull);
        if (preset is not ("barge" or "hopper_barge"))
        {
            // Superstructure aft (forward for tugs and yachts).
            var aft = preset is "tug" or "yacht" ? -loa * 0.1 : stern + loa * 0.05;
            solids.Add(Shapes.Block(o, d, w, aft, aft + loa * 0.12, -half * 0.8, half * 0.8, deck, deck + Math.Min(Math.Max(Shapes.Mm(2500), depth * 0.8), Shapes.Mm(12500))));
        }
        var name = args.OptString("name") ?? $"Design vessel {preset}";
        var comments = string.Format(CultureInfo.InvariantCulture, "vessel; preset={0}; loa={1}; beam={2}; draft={3:0.##}; displacement={4}; dwt={5}",
            preset, loa * 0.3048, beam * 0.3048, draft * 0.3048, disp, dwt);
        using var t = RevitHelpers.StartTransaction(doc, "Design vessel");
        var e = Shapes.Make(doc, BuiltInCategory.OST_GenericModel, name, solids, comments, "VESSEL");
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the vessel when saving it.");
        return ToolResult.Ok(new { id = e.Id.Value, preset, loaM = loa * 0.3048, beamM = beam * 0.3048, draftM = Math.Round(draft * 0.3048, 2), displacementT = disp, dwtT = dwt });
    }
}

public sealed class VesselCollisionCheckTool : IRevitTool
{
    public string Name => "vessel_collision_check";
    public string Description =>
        "Screen structures for vessel impact: finds piles, piers, columns and dolphins within a navigation zone next to a " +
        "channel or berth line (or near a design_vessel) at water level, and gives the berthing energy (PIANC: " +
        "0.5·M·V²·Ce·Cm·Cs·Cc) and a ship collision force (AASHTO: 1.2e5·V·√DWT) for the vessel, with the exposed elements " +
        "and their distance. A screening to decide where fenders, dolphins or pier protection are needed, not a design.";
    public string PropertiesJson => """
    {
      "vessel_id": {"type": "integer", "description": "A design_vessel element (its data and position are used)."},
      "dwt": {"type": "number"}, "displacement": {"type": "number", "description": "Tonnes."},
      "channel": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Channel centre line or berth line [x, y]."},
      "zone_width": {"type": "number", "description": "Half-width of the zone each side of the channel, project units (default 3 x beam or 50 m)."},
      "water_level": {"type": "number", "description": "Project elevation (default 0)."},
      "velocity": {"type": "number", "description": "Approach/impact speed m/s (default berthing 0.15; use transit speed e.g. 2.5 for channel collision)."},
      "ce": {"type": "number", "description": "Eccentricity factor (default 0.5)."},
      "cm": {"type": "number", "description": "Added mass factor (default 1.5 side berthing)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XYZ? vesselAt = null;
        if (args.OptLong("vessel_id") is { } vid)
        {
            var vessel = doc.GetElement(new ElementId(vid)) ?? throw new ArgumentException($"No element {vid}.");
            data = Tendons.Data(vessel);
            if (vessel.get_BoundingBox(null) is { } vb) vesselAt = (vb.Min + vb.Max) / 2;
        }
        double D(string key, double? arg, double fallback) => arg ?? (data.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : fallback);
        var dwt = D("dwt", args.OptDouble("dwt"), 13000);
        var disp = D("displacement", args.OptDouble("displacement"), dwt * 1.35);
        var beamM = D("beam", null, 25);
        var v = args.OptDouble("velocity") ?? 0.15;
        double ce = args.OptDouble("ce") ?? 0.5, cm = args.OptDouble("cm") ?? 1.5;
        var energy = 0.5 * disp * v * v * ce * cm; // kJ with t and m/s, Cs = Cc = 1
        var collisionKn = 1.2e5 * v * Math.Sqrt(dwt) / 1000; // AASHTO (SI): N with V in m/s and DWT in tonnes
        var water = args.OptDouble("water_level") is { } wl ? Elevations.ToInternalZ(doc, wl) : Elevations.BaseZ(doc);
        var zone = args.OptDouble("zone_width") is { } zw ? RevitHelpers.ToInternal(doc, zw) : Math.Max(3 * beamM, 50) / 0.3048;
        List<XYZ> line = args.Has("channel") ? args.Raw["channel"].EnumerateArray().Select(x => Modeling.Point(doc, x)).ToList()
            : vesselAt is { } va ? [va] : throw new ArgumentException("Give channel or vessel_id.");

        double Distance(XYZ p)
        {
            if (line.Count == 1) return new XYZ(p.X - line[0].X, p.Y - line[0].Y, 0).GetLength();
            var best = double.MaxValue;
            for (var i = 0; i + 1 < line.Count; i++)
            {
                var a = new XYZ(line[i].X, line[i].Y, 0); var b = new XYZ(line[i + 1].X, line[i + 1].Y, 0);
                if (a.DistanceTo(b) < 1e-6) continue;
                var seg = Line.CreateBound(a, b);
                best = Math.Min(best, seg.Project(new XYZ(p.X, p.Y, 0))?.Distance ?? double.MaxValue);
            }
            return best;
        }
        var exposed = new List<(Element e, double dist)>();
        var filter = new LogicalOrFilter(new List<ElementFilter> { new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns), new ElementCategoryFilter(BuiltInCategory.OST_StructuralFoundation), new ElementCategoryFilter(BuiltInCategory.OST_Walls) });
        foreach (var e in new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType())
        {
            if (e.get_BoundingBox(null) is not { } bb || bb.Min.Z > water + Shapes.Mm(2000) || bb.Max.Z < water - Shapes.Mm(15000)) continue;
            var dist = Distance((bb.Min + bb.Max) / 2) - Math.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y) / 2;
            if (dist <= zone) exposed.Add((e, Math.Max(0, dist)));
        }
        return ToolResult.Ok(new
        {
            vessel = new { dwtT = dwt, displacementT = disp, velocityMs = v },
            berthingEnergyKj = Math.Round(energy), collisionForceKn = Math.Round(collisionKn),
            exposedElements = exposed.Count,
            nearest = exposed.OrderBy(x => x.dist).Take(60).Select(x => new { id = x.e.Id.Value, element = $"{x.e.Category?.Name} {x.e.Name}", distance = Math.Round(RevitHelpers.FromInternal(doc, x.dist)) }),
            note = "Screening only: confirm vessel data, velocities and factors with PIANC WG33 / BS 6349 / AASHTO for the project, and design fenders or protection accordingly.",
        });
    }
}

public sealed class ProgressiveCollapseCheckTool : IRevitTool
{
    public string Name => "progressive_collapse_check";
    public string Description =>
        "Screen a framed building for progressive collapse: for each column (or only perimeter and corner columns), what " +
        "its notional removal does (the beams that lose a support and the doubled span they would need to bridge), " +
        "transfer columns that stand on beams, and the tie forces to provide (EN 1991-1-7 / UFC 4-023-03 style: internal " +
        "and peripheral ties from the floor loads). Lists the key elements to check; a screening, not an analysis.";
    public string PropertiesJson => """
    {
      "level": {"type": "string", "description": "Only columns on this level (default all)."},
      "scope": {"type": "string", "enum": ["all", "perimeter"], "description": "Default perimeter (corners and edges, as the codes ask first)."},
      "dead_load": {"type": "number", "description": "gk kPa (default 6.0)."}, "live_load": {"type": "number", "description": "qk kPa (default 3.0)."},
      "psi": {"type": "number", "description": "Combination factor on live load (default 0.5)."},
      "max_span": {"type": "number", "description": "Flag doubled spans above this, project units (default 12 m)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var columns = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().OfType<Autodesk.Revit.DB.FamilyInstance>().ToList();
        if (args.OptString("level") is { } ln)
        {
            var level = RevitHelpers.FindLevel(doc, ln);
            columns = columns.Where(c => c.LevelId == level.Id || c.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() == level.Id).ToList();
        }
        if (columns.Count == 0) return ToolResult.Error("No structural columns found.");
        var beams = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().OfType<Autodesk.Revit.DB.FamilyInstance>()
            .Where(b => b.Location is LocationCurve { Curve: Line }).ToList();
        var tol = Shapes.Mm(500);
        var maxSpan = args.OptDouble("max_span") is { } ms ? RevitHelpers.ToInternal(doc, ms) : Shapes.Mm(12000);
        double gk = args.OptDouble("dead_load") ?? 6.0, qk = args.OptDouble("live_load") ?? 3.0, psi = args.OptDouble("psi") ?? 0.5;

        // Perimeter: columns on the outline of all column positions (convex hull in plan, within tolerance).
        var pts = columns.Select(c => (c, p: Steel.ColumnAxis(c) is { } ax ? ax.GetEndPoint(0) : (c.Location as LocationPoint)?.Point)).Where(x => x.p != null).ToList();
        var hull = Hull(pts.Select(x => new XYZ(x.p!.X, x.p.Y, 0)).Distinct(new XyComparer()).ToList());
        bool OnHull(XYZ p) => hull.Count < 3 || hull.Zip(hull.Skip(1).Concat([hull[0]]), (a, b) => Line.CreateBound(a, b)).Any(l => l.Project(new XYZ(p.X, p.Y, 0))?.Distance < tol);
        var perimeterOnly = (args.OptString("scope") ?? "perimeter") == "perimeter";
        var findings = new List<object>();
        var transfer = 0; var longSpans = 0; var screened = 0;
        foreach (var (c, p) in pts)
        {
            var onEdge = OnHull(p!);
            if (perimeterOnly && !onEdge) continue;
            var axis = Steel.ColumnAxis(c);
            if (axis is null) continue;
            screened++;
            var topZ = Math.Max(axis.GetEndPoint(0).Z, axis.GetEndPoint(1).Z);
            var botZ = Math.Min(axis.GetEndPoint(0).Z, axis.GetEndPoint(1).Z);
            var top = axis.GetEndPoint(0).Z > axis.GetEndPoint(1).Z ? axis.GetEndPoint(0) : axis.GetEndPoint(1);
            // Beams that this column supports at its top.
            var supported = new List<(Autodesk.Revit.DB.FamilyInstance beam, double other)>();
            foreach (var b in beams)
            {
                var line = (Line)((LocationCurve)b.Location).Curve;
                foreach (var (end, far) in new[] { (line.GetEndPoint(0), line.GetEndPoint(1)), (line.GetEndPoint(1), line.GetEndPoint(0)) })
                    if (new XYZ(end.X - top.X, end.Y - top.Y, 0).GetLength() < tol && Math.Abs(end.Z - topZ) < Shapes.Mm(1500))
                        supported.Add((b, line.Length));
            }
            // Transfer column: its base lands on a beam span rather than a column or foundation.
            var onBeam = beams.Any(b => ((LocationCurve)b.Location).Curve is Line l && l.Project(new XYZ(p!.X, p.Y, botZ))?.Distance < tol
                                        && l.GetEndPoint(0).DistanceTo(new XYZ(p.X, p.Y, l.GetEndPoint(0).Z)) > tol && l.GetEndPoint(1).DistanceTo(new XYZ(p.X, p.Y, l.GetEndPoint(1).Z)) > tol);
            if (onBeam) transfer++;
            // Removing the column roughly doubles the span of beams running through it.
            var doubled = supported.GroupBy(s => { var dir = ((Line)((LocationCurve)s.beam.Location).Curve).Direction; var a = Math.Atan2(dir.Y, dir.X); if (a < 0) a += Math.PI; return Math.Round(a % Math.PI, 2) % Math.Round(Math.PI, 2); })
                .Select(g => g.Sum(s => s.other)).DefaultIfEmpty(0).Max();
            var flag = doubled > maxSpan || supported.Count == 0 || onBeam;
            if (doubled > maxSpan) longSpans++;
            if (flag || findings.Count < 40)
                findings.Add(new
                {
                    column = c.Id.Value, mark = c.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(), level = doc.GetElement(c.LevelId)?.Name, perimeter = onEdge,
                    beamsLosingSupport = supported.Count, spanIfRemoved = Math.Round(RevitHelpers.FromInternal(doc, doubled)), transferColumn = onBeam,
                    issue = onBeam ? "Transfer column on a beam: treat the beam as a key element." : supported.Count == 0 ? "No beams found at its head (flat slab?): check slab punching/alternate path." : doubled > maxSpan ? "Span after removal exceeds the limit: needs alternate path design or key-element design." : "OK on screening.",
                });
        }
        // Tie forces per EN 1991-1-7 Annex A style (framed structures), with a typical 7.5 m tie span and 3 m storey.
        var load = gk + psi * qk;
        var internalTie = Math.Max(0.8 * load * 7.5 * 7.5, 75);
        var peripheralTie = Math.Max(0.4 * load * 7.5 * 7.5, 75);
        return ToolResult.Ok(new
        {
            columnsScreened = screened, findingsListed = findings.Count, transferColumns = transfer, longSpansIfRemoved = longSpans,
            ties = new { loadKpa = Math.Round(load, 2), internalTieKn = Math.Round(internalTie), peripheralTieKn = Math.Round(peripheralTie), basis = "0.8(gk+ψqk)·s·L and 0.4(gk+ψqk)·s·L with s = L = 7.5 m, min 75 kN; recompute with actual tie spacing and span." },
            findings = findings.Take(150),
            note = "Screening only (notional column removal, tie forces). Use EN 1991-1-7 / UFC 4-023-03 / GSA with the building's consequence class for the real assessment.",
        });
    }

    private sealed class XyComparer : IEqualityComparer<XYZ>
    {
        public bool Equals(XYZ? a, XYZ? b) => a != null && b != null && Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;
        public int GetHashCode(XYZ p) => (Math.Round(p.X, 1), Math.Round(p.Y, 1)).GetHashCode();
    }

    /// <summary>Convex hull in plan (monotone chain).</summary>
    private static List<XYZ> Hull(List<XYZ> pts)
    {
        if (pts.Count < 3) return pts;
        var s = pts.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        double Cross(XYZ o, XYZ a, XYZ b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var lower = new List<XYZ>();
        foreach (var p in s) { while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(p); }
        var upper = new List<XYZ>();
        for (var i = s.Count - 1; i >= 0; i--) { var p = s[i]; while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(p); }
        lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
        return lower.Concat(upper).ToList();
    }
}
