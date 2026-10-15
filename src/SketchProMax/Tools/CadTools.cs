using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

internal static class Cad
{
    public static ImportInstance Find(Document doc, long? id)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        if (id is { } v) return all.FirstOrDefault(i => i.Id.Value == v) ?? throw new ArgumentException($"{v} is not a CAD import or link.");
        return all.Count switch
        {
            0 => throw new InvalidOperationException("No CAD file is linked or imported. Use import_cad first."),
            1 => all[0],
            _ => throw new ArgumentException("Several CAD files are in the model; give import_id (see list_cad_layers)."),
        };
    }

    /// <summary>All curves of the CAD instance in model coordinates, with their layer names.</summary>
    public static List<(string Layer, Curve Curve)> Curves(Document doc, ImportInstance cad)
    {
        var result = new List<(string, Curve)>();
        void Walk(GeometryElement? geometry)
        {
            if (geometry is null) return;
            foreach (var obj in geometry)
            {
                var layer = (doc.GetElement(obj.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "(none)";
                switch (obj)
                {
                    case GeometryInstance gi: Walk(gi.GetInstanceGeometry()); break;
                    case Curve c: result.Add((layer, c)); break;
                    case PolyLine pl:
                        var pts = pl.GetCoordinates();
                        for (var i = 0; i + 1 < pts.Count; i++)
                            if (pts[i].DistanceTo(pts[i + 1]) > doc.Application.ShortCurveTolerance)
                                result.Add((layer, Line.CreateBound(pts[i], pts[i + 1])));
                        break;
                }
            }
        }
        // A CAD file placed in one view only has its geometry in that view.
        var options = new Options();
        if (cad.ViewSpecific && doc.GetElement(cad.OwnerViewId) is View owner) options.View = owner;
        Walk(cad.get_Geometry(options));
        return result;
    }

    /// <summary>The curve moved onto elevation z; null when it is shorter than <paramref name="shortest"/> in plan (e.g. a vertical line).</summary>
    public static Curve? Flatten(Curve c, double z, double shortest)
    {
        if (c is Line { IsBound: true } l)
        {
            XYZ a = l.GetEndPoint(0), b = l.GetEndPoint(1);
            a = new XYZ(a.X, a.Y, z);
            b = new XYZ(b.X, b.Y, z);
            return a.DistanceTo(b) < shortest ? null : Line.CreateBound(a, b);
        }
        var start = c.IsBound ? c.GetEndPoint(0) : c.Evaluate(0, false);
        var moved = c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, z - start.Z)));
        return moved.IsBound && moved.Length < shortest ? null : moved;
    }

    /// <summary>Groups curves whose ends touch into connected pieces (rectangles, circles, closed outlines).</summary>
    public static List<List<Curve>> Pieces(List<Curve> curves, double tolerance)
    {
        var parent = Enumerable.Range(0, curves.Count).ToArray();
        int Root(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        // Endpoints go into a grid of tolerance-sized cells; touching ends are in the same or a neighbouring cell.
        var cells = new Dictionary<(long, long), List<(int Curve, XYZ Point)>>();
        for (var i = 0; i < curves.Count; i++)
        {
            if (!curves[i].IsBound) continue;
            for (var end = 0; end < 2; end++)
            {
                var p = curves[i].GetEndPoint(end);
                long cx = (long)Math.Floor(p.X / tolerance), cy = (long)Math.Floor(p.Y / tolerance);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        if (cells.TryGetValue((cx + dx, cy + dy), out var near))
                            foreach (var (j, q) in near)
                                if (q.DistanceTo(p) < tolerance) parent[Root(i)] = Root(j);
                if (!cells.TryGetValue((cx, cy), out var cell)) cells[(cx, cy)] = cell = [];
                cell.Add((i, p));
            }
        }
        return Enumerable.Range(0, curves.Count).GroupBy(Root).Select(g => g.Select(i => curves[i]).ToList()).ToList();
    }

    /// <summary>Orders touching curves into a closed loop, or returns null when they don't close.</summary>
    public static CurveLoop? Loop(List<Curve> piece, double tolerance)
    {
        if (piece.Count == 1 && !piece[0].IsBound) return CurveLoop.Create([piece[0]]);
        var rest = piece.Where(c => c.IsBound).ToList();
        if (rest.Count < 2) return null;
        var ordered = new List<Curve> { rest[0] };
        rest.RemoveAt(0);
        while (rest.Count > 0)
        {
            var end = ordered[ordered.Count - 1].GetEndPoint(1);
            var next = rest.FindIndex(c => c.GetEndPoint(0).DistanceTo(end) < tolerance || c.GetEndPoint(1).DistanceTo(end) < tolerance);
            if (next < 0) return null;
            var c = rest[next];
            rest.RemoveAt(next);
            ordered.Add(c.GetEndPoint(0).DistanceTo(end) < tolerance ? c : c.CreateReversed());
        }
        if (ordered[0].GetEndPoint(0).DistanceTo(ordered[ordered.Count - 1].GetEndPoint(1)) > tolerance) return null;
        try { return CurveLoop.Create(ordered); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { return null; }
    }

    public static BoundingBoxUV Box(IEnumerable<Curve> curves)
    {
        var pts = curves.SelectMany(c => c.Tessellate()).ToList();
        return new BoundingBoxUV(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
    }
}

public sealed class ImportCadTool : IRevitTool
{
    public string Name => "import_cad";
    public string Description =>
        "Link (default) or import a DWG/DXF into a plan view, so it can be traced or converted with convert_cad. " +
        "Placement: origin to origin by default, or by shared coordinates.";
    public string PropertiesJson => """
    {
      "path": {"type": "string"},
      "view_id": {"type": "integer", "description": "Plan view to place it in (current view only). Default: active view."},
      "link": {"type": "boolean", "description": "Link (default true) or import."},
      "placement": {"type": "string", "enum": ["origin", "shared", "center"]},
      "units": {"type": "string", "enum": ["auto", "millimeter", "centimeter", "meter", "inch", "foot"]}
    }
    """;
    public string[] Required => ["path"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var path = args.String("path");
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {path}");
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        var options = new DWGImportOptions
        {
            ThisViewOnly = view is ViewPlan,
            Placement = args.OptString("placement") switch { "shared" => ImportPlacement.Shared, "center" => ImportPlacement.Centered, _ => ImportPlacement.Origin },
            Unit = args.OptString("units") switch
            {
                "millimeter" => ImportUnit.Millimeter, "centimeter" => ImportUnit.Centimeter, "meter" => ImportUnit.Meter,
                "inch" => ImportUnit.Inch, "foot" => ImportUnit.Foot, _ => ImportUnit.Default,
            },
        };
        using var t = RevitHelpers.StartTransaction(doc, "Import CAD");
        ElementId id;
        var ok = args.OptBool("link", true) ? doc.Link(path, options, view, out id) : doc.Import(path, options, view, out id);
        t.Commit();
        return ok ? ToolResult.Ok(new { importId = id.Value, view = view.Name }) : ToolResult.Error("Revit could not bring in that file.");
    }
}

public sealed class ListCadLayersTool : IRevitTool
{
    public string Name => "list_cad_layers";
    public string Description =>
        "List linked/imported CAD files and, for one of them, its layers with counts of lines, arcs and closed shapes " +
        "and their typical lengths. Use it to decide which layers are walls, grids, columns or slabs before convert_cad.";
    public string PropertiesJson => """{"import_id": {"type": "integer"}}""";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
            .Select(i => new { id = i.Id.Value, name = i.Category?.Name, linked = i.IsLinked, view = i.ViewSpecific ? doc.GetElement(i.OwnerViewId)?.Name : "all views" }).ToList();
        if (imports.Count == 0) return ToolResult.Ok(new { imports, message = "No CAD files in the model." });
        if (imports.Count > 1 && !args.Has("import_id")) return ToolResult.Ok(new { imports });

        var cad = Cad.Find(doc, args.OptLong("import_id"));
        var layers = Cad.Curves(doc, cad).GroupBy(x => x.Layer).Select(g => new
        {
            layer = g.Key,
            lines = g.Count(x => x.Curve is Line),
            arcs = g.Count(x => x.Curve is Arc),
            other = g.Count(x => x.Curve is not Line and not Arc),
            medianLength = RevitHelpers.FromInternal(doc, g.Select(x => x.Curve.Length).OrderBy(l => l).ElementAt(g.Count() / 2)),
        }).OrderByDescending(x => x.lines + x.arcs).ToList();
        return ToolResult.Ok(new { imports, importId = cad.Id.Value, layers });
    }
}

public sealed class ConvertCadTool : IRevitTool
{
    public string Name => "convert_cad";
    public string Description =>
        "Convert a linked/imported DWG into Revit elements, layer by layer: 'walls' (pairs of parallel lines become " +
        "walls of the matching thickness, creating wall types for new thicknesses), 'wall_centerlines' (each line is a " +
        "wall), 'grids' (each line a grid, optionally renamed 1,2,3 / A,B,C), 'columns' (each small closed shape a column " +
        "at its center), 'floors' (each closed outline a floor), 'room_separation', 'model_lines'.";
    public string PropertiesJson => """
    {
      "import_id": {"type": "integer"},
      "level": {"type": "string"},
      "conversions": {"type": "array", "items": {"type": "object", "properties": {
        "layer": {"type": "string", "description": "Layer name (exact, or * as a wildcard e.g. 'A-WALL*')."},
        "as": {"type": "string", "enum": ["walls", "wall_centerlines", "grids", "columns", "floors", "room_separation", "model_lines"]},
        "type": {"type": "string", "description": "Wall/column/floor type name (default: closest or first)."},
        "min_thickness": {"type": "number", "description": "walls: smallest wall thickness to look for, project units (default 50 mm)."},
        "max_thickness": {"type": "number", "description": "walls: largest (default 600 mm)."},
        "height": {"type": "number", "description": "Wall height, project units (default: up to the next level, or 3 m)."},
        "max_size": {"type": "number", "description": "columns: biggest shape counted as a column (default 1.5 m)."},
        "auto_name": {"type": "boolean", "description": "grids: name vertical grids 1,2,3 left to right and horizontal A,B,C bottom up."}}, "required": ["layer", "as"]}}
    }
    """;
    public string[] Required => ["conversions"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var cad = Cad.Find(doc, args.OptLong("import_id"));
        var level = RevitHelpers.FindLevel(doc, args.OptString("level"));
        var curves = Cad.Curves(doc, cad);
        double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        var tolerance = Mm(5);
        var report = new List<object>();

        var shortest = doc.Application.ShortCurveTolerance;

        using var t = new Transaction(doc, "Sketch ProMax: Convert CAD");
        t.Start();
        var failure = t.GetFailureHandlingOptions();
        var swallow = new TypeKit.SwallowWarnings(doc);
        failure.SetFailuresPreprocessor(swallow);
        t.SetFailureHandlingOptions(failure);

        foreach (var spec in args.Array("conversions"))
        {
            var pattern = spec.GetProperty("layer").GetString() ?? "";
            var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var matching = curves.Where(c => regex.IsMatch(c.Layer)).ToList();
            var layerCurves = matching.Select(c => Cad.Flatten(c.Curve, level.ProjectElevation, shortest)).OfType<Curve>().ToList();
            var kind = spec.GetProperty("as").GetString();
            string? typeName = spec.TryGetProperty("type", out var tn) ? tn.GetString() : null;
            double? Opt(string key) => spec.TryGetProperty(key, out var v) ? RevitHelpers.ToInternal(doc, v.GetDouble()) : null;
            var created = 0;
            var notes = new List<string>();
            if (matching.Count > layerCurves.Count) notes.Add($"{matching.Count - layerCurves.Count} curves too short in plan were skipped");
            var failed = new Dictionary<string, int>();
            // One bad item is noted and skipped instead of stopping the whole conversion.
            void Make(Action create)
            {
                try { create(); created++; }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
                {
                    failed[ex.Message] = failed.TryGetValue(ex.Message, out var n) ? n + 1 : 1;
                }
            }

            switch (kind)
            {
                case "walls":
                case "wall_centerlines":
                {
                    var height = Opt("height") ?? DefaultHeight(doc, level);
                    var centerlines = kind == "walls"
                        ? PairWalls(layerCurves.OfType<Line>().ToList(), Opt("min_thickness") ?? Mm(50), Opt("max_thickness") ?? Mm(600))
                        : layerCurves.Select(c => (c, (double?)null)).ToList();
                    var types = new Dictionary<long, WallType>();
                    foreach (var (line, width) in centerlines)
                    {
                        if (line.Length < shortest) continue;
                        Make(() => Wall.Create(doc, line, WallTypeFor(doc, typeName, width, types).Id, level.Id, height, 0, false, false));
                    }
                    if (kind == "walls") notes.Add($"{layerCurves.Count} lines paired into {created} walls");
                    break;
                }
                case "grids":
                {
                    var grids = new List<Grid>();
                    foreach (var l in layerCurves.OfType<Line>().Where(l => l.Length > Mm(500)))
                        Make(() => grids.Add(Grid.Create(doc, l)));
                    if (spec.TryGetProperty("auto_name", out var an) && an.GetBoolean()) NameGrids(grids);
                    break;
                }
                case "columns":
                {
                    var symbol = Symbol(doc, typeName, BuiltInCategory.OST_StructuralColumns) ?? Symbol(doc, typeName, BuiltInCategory.OST_Columns)
                                 ?? throw new InvalidOperationException("No column family is loaded. Use load_family first.");
                    if (!symbol.IsActive) symbol.Activate();
                    var maxSize = Opt("max_size") ?? Mm(1500);
                    var open = 0;
                    foreach (var piece in Cad.Pieces(layerCurves, tolerance))
                    {
                        if (piece.Count > 1 && Cad.Loop(piece, tolerance) is null) { open++; continue; }
                        var box = Cad.Box(piece);
                        if (box.Max.U - box.Min.U > maxSize || box.Max.V - box.Min.V > maxSize) continue;
                        var centre = new XYZ((box.Min.U + box.Max.U) / 2, (box.Min.V + box.Max.V) / 2, level.ProjectElevation);
                        Make(() => doc.Create.NewFamilyInstance(centre, symbol, level, symbol.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns ? StructuralType.Column : StructuralType.NonStructural));
                    }
                    if (open > 0) notes.Add($"{open} open shapes (lines that don't close) were skipped");
                    break;
                }
                case "floors":
                {
                    var type = TypeKit.Pick<FloorType>(doc, typeName, "floor");
                    foreach (var piece in Cad.Pieces(layerCurves, tolerance))
                    {
                        if (Cad.Loop(piece, tolerance) is not { } loop) { notes.Add("an outline that doesn't close was skipped"); continue; }
                        Make(() => Floor.Create(doc, [loop], type.Id, level.Id));
                    }
                    break;
                }
                case "room_separation":
                case "model_lines":
                {
                    var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.ProjectElevation)));
                    if (kind == "model_lines")
                        foreach (var c in layerCurves) Make(() => doc.Create.NewModelCurve(c, plane));
                    else
                    {
                        var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                            .FirstOrDefault(p => !p.IsTemplate && p.ViewType == ViewType.FloorPlan && p.GenLevel?.Id == level.Id)
                            ?? throw new InvalidOperationException($"No floor plan for {level.Name}. Create one first (create_levels makes plans).");
                        foreach (var c in layerCurves)
                        {
                            var one = new CurveArray();
                            one.Append(c);
                            Make(() => doc.Create.NewRoomBoundaryLines(plane, one, plan));
                        }
                    }
                    break;
                }
                default:
                    notes.Add($"unknown conversion '{kind}'");
                    break;
            }
            foreach (var f in failed.Take(10)) notes.Add(f.Value > 1 ? $"{f.Value} failed: {f.Key}" : $"1 failed: {f.Key}");
            report.Add(new { layer = pattern, @as = kind, curves = layerCurves.Count, created, notes });
        }
        t.Commit();
        return ToolResult.Ok(new { level = level.Name, conversions = report, removedByRevit = swallow.Deleted.Take(50) });
    }

    private static double DefaultHeight(Document doc, Level level)
    {
        var above = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Where(l => l.Elevation > level.ProjectElevation + 0.1).OrderBy(l => l.Elevation).FirstOrDefault();
        return above != null ? above.ProjectElevation - level.ProjectElevation : UnitUtils.ConvertToInternalUnits(3, UnitTypeId.Meters);
    }

    /// <summary>Finds pairs of parallel, overlapping lines a wall-thickness apart and returns their centerlines.</summary>
    private static List<(Curve Line, double? Width)> PairWalls(List<Line> lines, double min, double max)
    {
        var used = new bool[lines.Count];
        var result = new List<(Curve, double?)>();
        if (lines.Count == 0) return result;

        // Parallel lines (within ~0.6 degrees) sit in the same or a neighbouring 1-degree direction bucket. Inside a
        // bucket, lines are sorted by their offset along the bucket's normal, so only lines about a wall-thickness
        // away are compared. The slack covers the small angle between a line's own normal and the bucket's.
        const int buckets = 180;
        const double step = Math.PI / buckets;
        var centre = new XYZ(lines.Average(l => l.GetEndPoint(0).X), lines.Average(l => l.GetEndPoint(0).Y), 0);
        var reach = lines.Max(l => Math.Max(Flat(l.GetEndPoint(0) - centre).GetLength(), Flat(l.GetEndPoint(1) - centre).GetLength()));
        var slack = 2 * reach * 2 * step;
        XYZ Normal(int bucket) => new XYZ(-Math.Sin((bucket + 0.5) * step), Math.Cos((bucket + 0.5) * step), 0);
        int Bucket(XYZ d)
        {
            var angle = Math.Atan2(d.Y, d.X);
            if (angle < 0) angle += Math.PI;
            return Math.Min(buckets - 1, Math.Max(0, (int)(angle / step)));
        }
        double Offset(Line l, int bucket) => (l.GetEndPoint(0) - centre).DotProduct(Normal(bucket));
        var sorted = lines.Select((l, i) => (Index: i, Bucket: Bucket(l.Direction))).GroupBy(x => x.Bucket).ToDictionary(
            g => g.Key,
            g => g.Select(x => (Offset: Offset(lines[x.Index], g.Key), x.Index)).OrderBy(x => x.Offset).ToArray());
        IEnumerable<int> Near(Line a)
        {
            var own = Bucket(a.Direction);
            foreach (var k in new[] { own, (own + 1) % buckets, (own + buckets - 1) % buckets }.Distinct())
            {
                if (!sorted.TryGetValue(k, out var list)) continue;
                var at = Offset(a, k);
                int lo = 0, hi = list.Length;
                while (lo < hi) { var m = (lo + hi) / 2; if (list[m].Offset < at - max - slack) lo = m + 1; else hi = m; }
                for (var n = lo; n < list.Length && list[n].Offset <= at + max + slack; n++) yield return list[n].Index;
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (used[i]) continue;
            var a = lines[i];
            var dir = a.Direction;
            var normal = new XYZ(-dir.Y, dir.X, 0);
            var best = -1;
            double bestGap = double.MaxValue, bestFrom = 0, bestTo = 0;
            foreach (var j in Near(a))
            {
                if (j == i || used[j]) continue;
                var b = lines[j];
                if (Math.Abs(dir.CrossProduct(b.Direction).Z) > 0.01) continue;
                var gap = Math.Abs((b.GetEndPoint(0) - a.GetEndPoint(0)).DotProduct(normal));
                if (gap < min || gap > max || gap >= bestGap) continue;
                double Along(XYZ p) => (p - a.GetEndPoint(0)).DotProduct(dir);
                var from = Math.Max(0, Math.Min(Along(b.GetEndPoint(0)), Along(b.GetEndPoint(1))));
                var to = Math.Min(a.Length, Math.Max(Along(b.GetEndPoint(0)), Along(b.GetEndPoint(1))));
                if (to - from < Math.Max(gap, 0.5)) continue;
                best = j; bestGap = gap; bestFrom = from; bestTo = to;
            }
            if (best < 0) continue;
            used[i] = used[best] = true;
            var side = (lines[best].GetEndPoint(0) - a.GetEndPoint(0)).DotProduct(normal) > 0 ? 1 : -1;
            var shift = normal * side * bestGap / 2;
            var start = a.GetEndPoint(0) + dir * bestFrom + shift;
            var end = a.GetEndPoint(0) + dir * bestTo + shift;
            result.Add((Line.CreateBound(start, end), bestGap));
        }
        return result;
    }

    private static XYZ Flat(XYZ v) => new(v.X, v.Y, 0);

    /// <summary>A basic wall type of the requested name, or the closest width (duplicating one when no width is close).</summary>
    private static WallType WallTypeFor(Document doc, string? name, double? width, Dictionary<long, WallType> cache)
    {
        var basics = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(w => w.Kind == WallKind.Basic).ToList();
        if (name != null) return TypeKit.Pick<WallType>(doc, name, "wall");
        if (width is null) return basics.First();
        var mm = (long)Math.Round(UnitUtils.ConvertFromInternalUnits(width.Value, UnitTypeId.Millimeters) / 5) * 5;
        if (cache.TryGetValue(mm, out var cached)) return cached;
        var closest = basics.OrderBy(w => Math.Abs(w.Width - width.Value)).First();
        if (Math.Abs(closest.Width - width.Value) < UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters)) return cache[mm] = closest;

        var newName = $"Generic - {mm}mm (from CAD)";
        var existing = basics.FirstOrDefault(w => w.Name == newName);
        if (existing != null) return cache[mm] = existing;
        var type = (WallType)closest.Duplicate(newName);
        var cs = type.GetCompoundStructure();
        if (cs != null)
        {
            var layers = cs.GetLayers();
            var thickest = Enumerable.Range(0, layers.Count).OrderByDescending(i => layers[i].Width).First();
            cs.SetLayerWidth(thickest, layers[thickest].Width + (width.Value - cs.GetWidth()));
            type.SetCompoundStructure(cs);
        }
        return cache[mm] = type;
    }

    private static FamilySymbol? Symbol(Document doc, string? name, BuiltInCategory category)
    {
        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>().ToList();
        return name is null ? symbols.FirstOrDefault()
            : symbols.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals($"{s.FamilyName}: {s.Name}", name, StringComparison.OrdinalIgnoreCase));
    }

    private static void NameGrids(List<Grid> grids)
    {
        // Temporary names first, so renaming never collides with an existing grid name.
        foreach (var g in grids) g.Name = "tmp-" + g.Id.Value;
        var vertical = grids.Where(g => Math.Abs(((Line)g.Curve).Direction.Y) > 0.7).OrderBy(g => g.Curve.Evaluate(0.5, true).X).ToList();
        var horizontal = grids.Except(vertical).OrderBy(g => g.Curve.Evaluate(0.5, true).Y).ToList();
        for (var i = 0; i < vertical.Count; i++) TryName(vertical[i], (i + 1).ToString());
        for (var i = 0; i < horizontal.Count; i++) TryName(horizontal[i], Modeling.Letters(i));
    }

    private static void TryName(Grid g, string name)
    {
        try { g.Name = name; }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { g.Name = name + "'"; }
    }
}
