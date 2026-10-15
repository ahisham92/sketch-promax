using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class ExportPrecastScheduleTool : IRevitTool
{
    // Type labels hollow_core_layout gives its units, and how much of the modelled solid is concrete.
    private static readonly (string label, double solid)[] Kinds =
        [("Hollow Core Plank", 0.55), ("Solid Precast Plank", 1.0), ("Filigree Plank", 1.0), ("Double Tee", 0.3)];

    public string Name => "export_precast_schedule";
    public string Description =>
        "Precast slab unit schedule: every unit laid out by hollow_core_layout (or floors whose mark starts with a prefix) " +
        "with mark, type, length, width, depth, area, volume and lifting weight, plus totals per type, to a CSV that opens in " +
        "Excel. Hollow core and double tee weights allow for their voids (modelled as solid).";
    public string PropertiesJson => """
    {
      "mark_prefix": {"type": "string", "description": "Only floors whose mark starts with this (e.g. 'HC-'). Default: all precast unit types."},
      "density": {"type": "number", "description": "Concrete density kg/m³ (default 2500)."},
      "folder": {"type": "string"},
      "file_name": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var prefix = args.OptString("mark_prefix");
        var density = args.OptDouble("density") ?? 2500;
        var rows = new List<(string mark, string type, double l, double w, double d, double area, double vol, double kg)>();
        foreach (var floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
        {
            var typeName = floor.FloorType?.Name ?? "";
            var mark = floor.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
            var kind = Kinds.FirstOrDefault(k => typeName.StartsWith(k.label, StringComparison.OrdinalIgnoreCase));
            if (prefix != null ? !mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) : kind.label is null) continue;
            if (mark.EndsWith("TOPPING", StringComparison.OrdinalIgnoreCase)) continue;
            var bb = floor.get_BoundingBox(null);
            if (bb is null) continue;
            double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
            double a = M(bb.Max.X - bb.Min.X), b = M(bb.Max.Y - bb.Min.Y);
            // hollow_core_layout records each unit's length and width; the plan box is only a fallback.
            var data = Tendons.Data(floor);
            if (data.TryGetValue("length", out var ls) && data.TryGetValue("width", out var ws)
                && double.TryParse(ls, NumberStyles.Float, CultureInfo.InvariantCulture, out var lv) && double.TryParse(ws, NumberStyles.Float, CultureInfo.InvariantCulture, out var wv))
            { a = lv; b = wv; }
            var area = UnitUtils.ConvertFromInternalUnits(floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0, UnitTypeId.SquareMeters);
            var vol = UnitUtils.ConvertFromInternalUnits(floor.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ?? 0, UnitTypeId.CubicMeters);
            var depth = M(floor.FloorType?.GetCompoundStructure()?.GetWidth() ?? 0);
            rows.Add((mark, typeName, data.ContainsKey("length") ? a : Math.Max(a, b), data.ContainsKey("width") ? b : Math.Min(a, b), depth, area, vol, vol * density * (kind.label is null ? 1 : kind.solid)));
        }
        if (rows.Count == 0) return ToolResult.Error("No precast units found. Lay them out with hollow_core_layout, or give mark_prefix.");

        string N(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
        string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine("Mark,Type,Length (m),Width (m),Depth (m),Area (m²),Volume (m³),Weight (kg)");
        foreach (var r in rows.OrderBy(r => System.Text.RegularExpressions.Regex.Replace(r.mark, @"\d+", m => m.Value.PadLeft(8, '0')), StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(string.Join(",", Csv(r.mark), Csv(r.type), N(r.l), N(r.w), N(r.d), N(r.area), N(r.vol), N(r.kg)));
        sb.AppendLine();
        sb.AppendLine("Type,Units,Area (m²),Weight (t),Heaviest unit (kg)");
        var byType = rows.GroupBy(r => r.type).OrderBy(g => g.Key).ToList();
        foreach (var g in byType)
            sb.AppendLine($"{Csv(g.Key)},{g.Count()},{N(g.Sum(r => r.area))},{N(g.Sum(r => r.kg) / 1000)},{N(g.Max(r => r.kg))}");

        var folder = args.OptString("folder") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, (args.OptString("file_name") ?? $"{doc.Title} - Precast schedule {DateTime.Now:yyyy-MM-dd}") + ".csv");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return ToolResult.Ok(new
        {
            file, units = rows.Count,
            byType = byType.Select(g => new { type = g.Key, units = g.Count(), tonnes = Math.Round(g.Sum(r => r.kg) / 1000, 2), heaviestKg = Math.Round(g.Max(r => r.kg)) }),
            note = "Lengths and widths are the units' plan extents; weights use the density given and, for hollow core and double tees, a typical solid fraction.",
        });
    }
}

/// <summary>Post-tensioning tendons, modelled as generic model shapes that carry their data in Comments.</summary>
internal static class Tendons
{
    public const string Prefix = "PT tendon";

    /// <summary>Strand area in mm² for common strand sizes.</summary>
    public static double StrandArea(double mm) => mm switch { <= 9.6 => 55, <= 11.2 => 75, <= 12.8 => 100, <= 13.0 => 100, <= 15.3 => 140, _ => 150 };

    /// <summary>Strand mass in kg/m for common strand sizes (7850 kg/m³ on the strand area).</summary>
    public static double StrandKgPerM(double mm) => StrandArea(mm) * 7.85e-3;

    /// <summary>A tendon path: high over supports, low at mid-span, at mid-depth at the anchorages; two half-parabolas per span.</summary>
    public static List<XYZ> Profile(XYZ a, XYZ b, List<double> supports, double anchorZ, double highZ, double lowZ, int perSpan = 12)
    {
        var length = new XYZ(b.X - a.X, b.Y - a.Y, 0).GetLength();
        var stations = new List<double> { 0 };
        stations.AddRange(supports.Where(s => s > 0.05 * length && s < 0.95 * length).OrderBy(s => s));
        stations.Add(length);
        double Z(int i) => i == 0 || i == stations.Count - 1 ? anchorZ : highZ;
        var points = new List<XYZ>();
        var dir = new XYZ(b.X - a.X, b.Y - a.Y, 0).Normalize();
        for (var i = 0; i + 1 < stations.Count; i++)
        {
            double s0 = stations[i], s1 = stations[i + 1], z0 = Z(i), z1 = Z(i + 1);
            for (var k = i == 0 ? 0 : 1; k <= perSpan; k++)
            {
                var t = (double)k / perSpan;
                var z = t <= 0.5 ? lowZ + (z0 - lowZ) * Math.Pow(1 - 2 * t, 2) : lowZ + (z1 - lowZ) * Math.Pow(2 * t - 1, 2);
                var p = new XYZ(a.X, a.Y, 0) + dir * (s0 + (s1 - s0) * t);
                points.Add(new XYZ(p.X, p.Y, z));
            }
        }
        return points;
    }

    /// <summary>A round duct along the path, one short cylinder per segment.</summary>
    public static List<GeometryObject> Duct(List<XYZ> path, double radius)
    {
        var solids = new List<GeometryObject>();
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var axis = path[i + 1] - path[i];
            var length = axis.GetLength();
            if (length < 1e-3) continue;
            var dir = axis / length;
            var plane = Plane.CreateByNormalAndOrigin(dir, path[i]);
            var circle = new CurveLoop();
            circle.Append(Arc.Create(plane, radius, 0, Math.PI));
            circle.Append(Arc.Create(plane, radius, Math.PI, 2 * Math.PI));
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([circle], dir, length));
        }
        return solids;
    }

    public static double PathLength(List<XYZ> path) => path.Zip(path.Skip(1), (p, q) => p.DistanceTo(q)).Sum();

    public static Dictionary<string, string> Data(Element e)
    {
        var text = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in text.Split(';').Select(p => p.Split(['='], 2)).Where(p => p.Length == 2)) data[p[0].Trim()] = p[1].Trim();
        return data;
    }

    public static IEnumerable<DirectShape> All(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
            .Where(d => (d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
}

public sealed class PlacePtTendonsTool : IRevitTool
{
    public string Name => "place_pt_tendons";
    public string Description =>
        "Model post-tensioning tendons in a concrete slab or beams with a draped profile: high over supports, low at " +
        "mid-span, at mid-depth at the anchorages (parabolic). Slabs: tendons along given lines, or spread uniformly at a " +
        "spacing in x or y across the slab, or banded along the column lines; supports are the columns the tendon passes. " +
        "Beams: tendons along each beam. Each tendon is a duct shape marked T-01... carrying strands and force for " +
        "export_tendon_schedule. Section views show the drape.";
    public string PropertiesJson => """
    {
      "floor_id": {"type": "integer", "description": "Slab to post-tension."},
      "beam_ids": {"type": "array", "items": {"type": "integer"}, "description": "Or beams to post-tension."},
      "lines": {"type": "array", "items": {"type": "object", "properties": {"start": {"type": "array", "items": {"type": "number"}}, "end": {"type": "array", "items": {"type": "number"}}}, "required": ["start", "end"]}, "description": "Slab: tendon lines in plan [x, y]."},
      "uniform": {"type": "object", "properties": {"direction": {"type": "string", "enum": ["x", "y"]}, "spacing": {"type": "number"}}, "description": "Slab: tendons every 'spacing' running in x or y across the slab."},
      "banded": {"type": "object", "properties": {"direction": {"type": "string", "enum": ["x", "y"]}, "count": {"type": "integer"}, "spacing": {"type": "number"}}, "description": "Slab: a band of 'count' tendons at 'spacing' on every column line running in x or y."},
      "tendons_per_beam": {"type": "integer", "description": "Beams: tendons side by side in each beam (default 1)."},
      "strands": {"type": "integer", "description": "Strands per tendon (default 4 in slabs, 7 in beams)."},
      "strand_size": {"type": "number", "description": "Strand diameter mm (default 15.2; 12.7 also common)."},
      "jacking_force": {"type": "number", "description": "kN per tendon (default 0.75 x 1860 MPa x strand area x strands)."},
      "cover_top": {"type": "number", "description": "Project units to the duct, default 30 mm."},
      "cover_bottom": {"type": "number", "description": "Project units to the duct, default 30 mm."},
      "duct_diameter": {"type": "number", "description": "Project units, default 50 mm slabs, 80 mm beams."},
      "support_tolerance": {"type": "number", "description": "How close a column must be to a tendon line to count as a support, project units (default 600 mm)."},
      "mark_prefix": {"type": "string", "description": "Default 'T-'."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        double Mm(double v) => v / 304.8;
        double Len(string key, double mm) => args.OptDouble(key) is { } v ? RevitHelpers.ToInternal(doc, v) : Mm(mm);
        var beams = args.LongList("beam_ids");
        var floor = args.OptLong("floor_id") is { } fid ? doc.GetElement(new ElementId(fid)) as Floor ?? throw new ArgumentException($"{fid} is not a floor.") : null;
        if (floor is null && beams.Count == 0) return ToolResult.Error("Give floor_id (with lines, uniform or banded) or beam_ids.");
        var inBeams = floor is null;
        var strands = (int)(args.OptLong("strands") ?? (inBeams ? 7 : 4));
        var strandMm = args.OptDouble("strand_size") ?? 15.2;
        var force = args.OptDouble("jacking_force") ?? Math.Round(0.75 * 1860 * Tendons.StrandArea(strandMm) * strands / 1000);
        var coverTop = Len("cover_top", 30);
        var coverBottom = Len("cover_bottom", 30);
        var radius = Len("duct_diameter", inBeams ? 80 : 50) / 2;
        var tol = Len("support_tolerance", 600);
        var prefix = args.OptString("mark_prefix") ?? "T-";
        // Continue after the highest number already used with this prefix, so marks don't repeat after deletions.
        var start = Tendons.All(doc).Select(d => d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
            .Where(m => m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(m => int.TryParse(m.Substring(prefix.Length), out var n) ? n : 0).DefaultIfEmpty(0).Max();

        // Each tendon: plan start/end, supports along it, and the heights to drape between.
        var plans = new List<(XYZ a, XYZ b, List<double> supports, double anchor, double high, double low, long host)>();
        var errors = new List<object>();
        if (floor != null)
        {
            var bb = floor.get_BoundingBox(null) ?? throw new ArgumentException("The floor has no geometry.");
            double top = bb.Max.Z, bottom = bb.Min.Z;
            if (top - bottom < coverTop + coverBottom + 2 * radius) throw new ArgumentException("The slab is too thin for these covers and duct.");
            var outline = Outlines.Extrude(Outlines.FloorProfile(doc, floor));
            // Columns meeting this slab only (not those of other storeys at the same spot), taken at slab height.
            var columns = new List<XYZ>();
            foreach (var c in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType())
            {
                if (c.get_BoundingBox(null) is not { } cb || cb.Min.Z > top + tol || cb.Max.Z < bottom - tol) continue;
                var axis = c is FamilyInstance fi ? Steel.ColumnAxis(fi) : null;
                if (axis is null) continue;
                var (p0, p1) = (axis.GetEndPoint(0), axis.GetEndPoint(1));
                var z = (top + bottom) / 2;
                var p = Math.Abs(p1.Z - p0.Z) < 1e-6 ? p0 : p0 + (p1 - p0) * Math.Max(0, Math.Min(1, (z - p0.Z) / (p1.Z - p0.Z)));
                if (p.X >= bb.Min.X - tol && p.X <= bb.Max.X + tol && p.Y >= bb.Min.Y - tol && p.Y <= bb.Max.Y + tol) columns.Add(p);
            }
            var lines = new List<(XYZ a, XYZ b)>();
            foreach (var l in args.Array("lines"))
            {
                var (la, lb) = (Modeling.Point(doc, l.GetProperty("start")), Modeling.Point(doc, l.GetProperty("end")));
                if (new XYZ(lb.X - la.X, lb.Y - la.Y, 0).GetLength() < Mm(100)) { errors.Add(new { error = "a tendon line's start and end are the same point" }); continue; }
                lines.Add((la, lb));
            }
            if (args.Has("uniform"))
            {
                var u = args.Raw["uniform"];
                var alongX = !u.TryGetProperty("direction", out var dv) || dv.GetString() != "y";
                var spacing = u.TryGetProperty("spacing", out var sv) ? RevitHelpers.ToInternal(doc, sv.GetDouble()) : Mm(1000);
                if (spacing < Mm(150)) throw new ArgumentException("uniform spacing is too small.");
                double from = alongX ? bb.Min.Y : bb.Min.X, to = alongX ? bb.Max.Y : bb.Max.X;
                for (var c = from + spacing / 2; c < to; c += spacing) lines.Add(Across(alongX, c, bb));
            }
            if (args.Has("banded"))
            {
                var bnd = args.Raw["banded"];
                var alongX = !bnd.TryGetProperty("direction", out var dv) || dv.GetString() != "y";
                var count = bnd.TryGetProperty("count", out var cv) ? cv.GetInt32() : 4;
                var spacing = bnd.TryGetProperty("spacing", out var sv) ? RevitHelpers.ToInternal(doc, sv.GetDouble()) : Mm(300);
                if (count < 1 || count > 20 || spacing < Mm(100)) throw new ArgumentException("banded needs count 1-20 and spacing of at least 100 mm.");
                // Bands are wider than a column; let their outer tendons still find the columns they pass.
                tol = Math.Max(tol, (count - 1) / 2.0 * spacing + Mm(300));
                // Column lines: column positions across the band direction, merged within the tolerance.
                var bands = new List<double>();
                foreach (var c in columns.Select(p => alongX ? p.Y : p.X).OrderBy(v => v))
                    if (bands.Count == 0 || c - bands[bands.Count - 1] > tol) bands.Add(c);
                if (bands.Count == 0) errors.Add(new { error = "banded: no structural columns under this slab to band on" });
                foreach (var centre in bands)
                    for (var i = 0; i < count; i++) lines.Add(Across(alongX, centre + (i - (count - 1) / 2.0) * spacing, bb));
            }
            if (lines.Count == 0) throw new ArgumentException("Give lines, uniform or banded for the slab.");
            foreach (var (a, b) in lines)
                foreach (var (sa, sb) in Inside(outline, a, b, bottom))
                {
                    var dir = (sb - sa).Normalize();
                    var length = sa.DistanceTo(sb);
                    if (length < Mm(1000)) continue;
                    var supports = new List<double>();
                    foreach (var c in columns)
                    {
                        var v = new XYZ(c.X - sa.X, c.Y - sa.Y, 0);
                        var along = v.DotProduct(dir);
                        if (along > 0 && along < length && (v - dir * along).GetLength() <= tol) supports.Add(along);
                    }
                    supports = supports.OrderBy(s => s).Aggregate(new List<double>(), (acc, s) => { if (acc.Count == 0 || s - acc[acc.Count - 1] > Mm(1000)) acc.Add(s); return acc; });
                    plans.Add((sa, sb, supports, (top + bottom) / 2, top - coverTop - radius, bottom + coverBottom + radius, floor.Id.Value));
                }
        }
        foreach (var id in beams)
        {
            try
            {
                var beam = doc.GetElement(new ElementId(id)) as FamilyInstance ?? throw new ArgumentException("not a beam");
                var f = Rebars.BeamFrame(beam);
                if (Math.Abs(f.D.Z) > 1e-3) throw new ArgumentException("sloped beams aren't supported");
                var n = (int)(args.OptLong("tendons_per_beam") ?? 1);
                var side = Math.Max(coverTop, coverBottom);
                var gap = n > 1 ? (f.Width - 2 * (side + radius)) / (n - 1) : 0;
                if (n < 1 || (n > 1 && gap < 2 * radius)) throw new ArgumentException($"{n} tendons don't fit side by side in this beam");
                if (f.Height < coverTop + coverBottom + 2 * radius) throw new ArgumentException("the beam is too shallow for these covers and duct");
                for (var i = 0; i < n; i++)
                {
                    var w = n == 1 ? (f.W0 + f.W1) / 2 : f.W0 + side + radius + gap * i;
                    var a = f.At(f.D0, w, 0);
                    var b = f.At(f.D1, w, 0);
                    double zTop = f.At(0, 0, f.U1).Z, zBottom = f.At(0, 0, f.U0).Z;
                    plans.Add((a, b, [], (zTop + zBottom) / 2, zTop - coverTop - radius, zBottom + coverBottom + radius, id));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { errors.Add(new { beam = id, error = ex.Message }); }
        }
        if (plans.Count == 0) return ToolResult.Error($"No tendons to place. {JsonSerializer.Serialize(errors)}");
        if (plans.Count > 400) return ToolResult.Error($"That makes {plans.Count} tendons; use a larger spacing or fewer lines (max 400 per call).");

        var category = new ElementId(BuiltInCategory.OST_GenericModel);
        var made = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "PT tendons");
        var k = start;
        foreach (var p in plans)
        {
            try
            {
                var path = Tendons.Profile(p.a, p.b, p.supports, p.anchor, p.high, p.low);
                var duct = Tendons.Duct(path, radius); // before the element, so a failure leaves nothing behind
                var shape = DirectShape.CreateElement(doc, category);
                try { shape.SetShape(duct); }
                catch (Exception) { doc.Delete(shape.Id); throw; }
                k++;
                var mark = $"{prefix}{k:00}";
                var lengthM = UnitUtils.ConvertFromInternalUnits(Tendons.PathLength(path), UnitTypeId.Meters);
                shape.SetName($"{Tendons.Prefix} {mark}");
                shape.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
                shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(string.Join("; ", Tendons.Prefix,
                    $"host={p.host}", $"strands={strands}", $"strand={strandMm.ToString(CultureInfo.InvariantCulture)}",
                    $"force={force.ToString(CultureInfo.InvariantCulture)}", $"length={Math.Round(lengthM, 3).ToString(CultureInfo.InvariantCulture)}",
                    $"spans={p.supports.Count + 1}"));
                made.Add(new { mark, id = shape.Id.Value, host = p.host, lengthM = Math.Round(lengthM, 2), spans = p.supports.Count + 1 });
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { errors.Add(new { host = p.host, error = ex.Message.Trim() }); }
        }
        if (made.Count == 0) { t.RollBack(); return ToolResult.Error($"No tendons were placed: {JsonSerializer.Serialize(errors)}"); }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the tendons when saving them.");
        return ToolResult.Ok(new
        {
            tendons = made.Count, strands, strandSize = strandMm, jackingForceKn = force, list = made.Take(100), errors,
            note = "Tendons are generic model shapes (Revit has no tendon category). Profiles are simple parabolic drapes peaking sharply over supports, for drawings and quantities, not a PT design.",
        });
    }

    private static (XYZ, XYZ) Across(bool alongX, double c, BoundingBoxXYZ bb) =>
        alongX ? (new XYZ(bb.Min.X - 1, c, 0), new XYZ(bb.Max.X + 1, c, 0)) : (new XYZ(c, bb.Min.Y - 1, 0), new XYZ(c, bb.Max.Y + 1, 0));

    /// <summary>The parts of a plan line inside the slab outline (so tendons stop at edges and openings).</summary>
    private static IEnumerable<(XYZ, XYZ)> Inside(Solid outline, XYZ a, XYZ b, double z)
    {
        // Outlines.Extrude puts the outline between z = 0 and 1 ft; cut through its middle.
        var line = Line.CreateBound(new XYZ(a.X, a.Y, 0.5), new XYZ(b.X, b.Y, 0.5));
        var result = outline.IntersectWithCurve(line, new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside });
        for (var i = 0; i < result.SegmentCount; i++)
        {
            var seg = result.GetCurveSegment(i);
            XYZ p = seg.GetEndPoint(0), q = seg.GetEndPoint(1);
            yield return (new XYZ(p.X, p.Y, z), new XYZ(q.X, q.Y, z));
        }
    }
}

public sealed class ExportTendonScheduleTool : IRevitTool
{
    public string Name => "export_tendon_schedule";
    public string Description =>
        "Post-tensioning tendon schedule from place_pt_tendons: mark, host, spans, strands, strand size, length, jacking " +
        "force and strand weight, plus totals, to a CSV that opens in Excel.";
    public string PropertiesJson => """
    {
      "folder": {"type": "string"},
      "file_name": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var tendons = Tendons.All(doc).ToList();
        if (tendons.Count == 0) return ToolResult.Error("No tendons found (place them with place_pt_tendons).");
        double D(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
        string N(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("Mark,Host id,Spans,Strands,Strand (mm),Length (m),Jacking force (kN),Strand length (m),Strand weight (kg)");
        double totalKg = 0, totalStrandM = 0;
        string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        string Natural(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\d+", m => m.Value.PadLeft(8, '0'));
        foreach (var tnd in tendons.OrderBy(x => Natural(x.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""), StringComparer.OrdinalIgnoreCase))
        {
            var d = Tendons.Data(tnd);
            var strands = D(d, "strands");
            var size = D(d, "strand");
            var length = D(d, "length");
            var strandM = strands * length;
            var kg = strandM * Tendons.StrandKgPerM(size);
            totalKg += kg; totalStrandM += strandM;
            sb.AppendLine(string.Join(",", Csv(tnd.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""), (d.TryGetValue("host", out var h) ? h : ""),
                (d.TryGetValue("spans", out var sp) ? sp : ""), strands, N(size), N(length), N(D(d, "force")), N(strandM), N(kg)));
        }
        sb.AppendLine();
        sb.AppendLine($"Total,,,,,,,{N(totalStrandM)},{N(totalKg)}");
        var folder = args.OptString("folder") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, (args.OptString("file_name") ?? $"{doc.Title} - Tendon schedule {DateTime.Now:yyyy-MM-dd}") + ".csv");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return ToolResult.Ok(new
        {
            file, tendons = tendons.Count, strandLengthM = Math.Round(totalStrandM, 1), strandTonnes = Math.Round(totalKg / 1000, 3),
            note = "Strand lengths exclude stressing tails at the anchorages; add an allowance (often 0.6-1 m per live end).",
        });
    }
}
