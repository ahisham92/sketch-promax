using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Shared bits for the model checks: findings, CSV, folders.</summary>
internal static class Checks
{
    public sealed record Finding(string Check, long Id, string What, string Issue);

    /// <summary>A key for this model that is the same for every user and session: the central model's path when workshared.</summary>
    public static string ModelKey(Document doc)
    {
        var path = doc.IsWorkshared && doc.GetWorksharingCentralModelPath() is { } central ? ModelPathUtils.ConvertModelPathToUserVisiblePath(central) : doc.PathName;
        if (string.IsNullOrEmpty(path)) path = doc.Title;
        var name = new string(Path.GetFileNameWithoutExtension(path).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        using var md5 = MD5.Create();
        var hash = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "").Substring(0, 8).ToLowerInvariant();
        return $"{name}-{hash}";
    }

    public static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string Describe(Element e) =>
        $"{e.Category?.Name} {(e.Document.GetElement(e.GetTypeId()) as ElementType)?.Name ?? e.Name}".Trim();

    public static string Folder(ToolArgs args) =>
        OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");

    public static string Write(ToolArgs args, string name, IEnumerable<Finding> findings)
    {
        var folder = Folder(args);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{name} {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        var sb = new StringBuilder("Check,Id,Element,Issue\n");
        foreach (var f in findings) sb.AppendLine(string.Join(",", Csv(f.Check), f.Id, Csv(f.What), Csv(f.Issue)));
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return file;
    }

    /// <summary>Writes a BCF with one issue per check (its first elements), through export_bcf.</summary>
    public static string? Bcf(UIApplication app, ToolArgs args, string name, List<Finding> findings)
    {
        if (!args.OptBool("bcf") || findings.Count == 0) return null;
        var issues = findings.GroupBy(f => f.Check).Select(g => new
        {
            title = $"{g.Key}: {g.Count()} item(s)",
            description = string.Join("\n", g.Take(20).Select(f => $"{f.What} ({f.Id}): {f.Issue}")),
            element_ids = g.Where(f => f.Id > 0).Select(f => f.Id).Distinct().Take(200).ToArray(),
        }).ToList();
        var r = Drafting.Call(app, new ExportBcfTool(), new { issues, folder = Folder(args), file_name = $"{name} {DateTime.Now:yyyy-MM-dd HHmm}" });
        return r.IsError ? "BCF not written: " + r.Text : r.Text;
    }

    public static IEnumerable<Element> Of(Document doc, BuiltInCategory cat) =>
        new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType();
}

public sealed class CheckModelTool : IRevitTool
{
    public string Name => "check_model";
    public string Description =>
        "Check that the model is built correctly, scored per check: Revit warnings grouped by type, duplicate elements (same " +
        "type in the same place), wall ends that stop short of another wall, beams with an end on nothing, columns off " +
        "the grid intersections, walls/beams/grids slightly off-axis (under 1°), elements far above or below their level " +
        "(probably on the wrong level), rooms not placed or not enclosed, and empty required parameters. CSV of every " +
        "finding, optional BCF per check.";
    public string PropertiesJson => """
    {
      "checks": {"type": "array", "items": {"type": "string", "enum": ["warnings", "duplicates", "wall_gaps", "unsupported_beams", "columns_off_grid", "off_axis", "level_offsets", "rooms", "required_parameters"]}, "description": "Default all."},
      "required_parameters": {"type": "array", "items": {"type": "string"}, "description": "Parameter names that must have a value (default Mark) on structural columns, framing, foundations, walls and floors."},
      "tolerance": {"type": "number", "description": "Column-on-grid tolerance, project units (default 50 mm)."},
      "gap_tolerance": {"type": "number", "description": "Wall gap and beam support search distance, project units (default 300 mm)."},
      "max_level_offset": {"type": "number", "description": "Flag base offsets larger than this, project units (default 1500 mm)."},
      "bcf": {"type": "boolean"},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var wanted = args.StringList("checks");
        bool On(string c) => wanted.Count == 0 || wanted.Contains(c);
        var findings = new List<Checks.Finding>();
        var population = new Dictionary<string, int>();
        var tol = args.OptDouble("tolerance") is { } t ? RevitHelpers.ToInternal(doc, t) : (double?)null;
        var maxOffset = args.OptDouble("max_level_offset") is { } mo ? RevitHelpers.ToInternal(doc, mo) : 1500 / 304.8;
        void Add(string check, Element? e, string issue) => findings.Add(new Checks.Finding(check, e?.Id.Value ?? 0, e is null ? "" : Checks.Describe(e), issue));

        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.WallType.Kind == WallKind.Basic && w.Location is LocationCurve).ToList();
        var columns = Checks.Of(doc, BuiltInCategory.OST_StructuralColumns).OfType<FamilyInstance>().ToList();
        var beams = Checks.Of(doc, BuiltInCategory.OST_StructuralFraming).OfType<FamilyInstance>().Where(b => b.Location is LocationCurve { Curve: Line }).ToList();

        if (On("warnings"))
        {
            var warnings = doc.GetWarnings();
            population["warnings"] = Math.Max(1, new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount() / 20);
            foreach (var w in warnings)
            {
                var ids = w.GetFailingElements();
                var first = ids.Count > 0 ? doc.GetElement(ids.First()) : null;
                Add("warnings", first, w.GetDescriptionText());
            }
        }

        if (On("duplicates"))
        {
            var cats = new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Columns, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_GenericModel };
            var boxes = cats.SelectMany(c => Checks.Of(doc, c)).Select(e => (e, b: e.get_BoundingBox(null))).Where(x => x.b != null).ToList();
            var all = boxes.Select(x => x.e).ToList();
            var boxOf = boxes.ToDictionary(x => x.e.Id.Value, x => x.b);
            population["duplicates"] = all.Count;
            string Key(Element e)
            {
                var b = boxOf[e.Id.Value];
                long R(double v) => (long)Math.Round(v * 304.8 / 10);
                return $"{e.Category?.Id.Value}|{e.GetTypeId().Value}|{R(b.Min.X)},{R(b.Min.Y)},{R(b.Min.Z)}|{R(b.Max.X)},{R(b.Max.Y)},{R(b.Max.Z)}";
            }
            foreach (var g in all.GroupBy(Key).Where(g => g.Count() > 1))
                foreach (var e in g.Skip(1)) Add("duplicates", e, $"same type in the same place as {g.First().Id.Value}");
        }

        if (On("wall_gaps"))
        {
            var gap = args.OptDouble("gap_tolerance") is { } gt0 ? RevitHelpers.ToInternal(doc, gt0) : 300 / 304.8;
            population["wall_gaps"] = walls.Count * 2;
            var index = new BoxIndex<Wall>(10);
            foreach (var w in walls) if (w.get_BoundingBox(null) is { } b) index.Add(w, b.Min, b.Max);
            foreach (var w in walls)
            {
                var lc = (LocationCurve)w.Location;
                for (var end = 0; end < 2; end++)
                {
                    var joined = lc.get_ElementsAtJoin(end).Cast<Element>().Any(x => x.Id != w.Id);
                    if (joined) continue;
                    var p = lc.Curve.GetEndPoint(end);
                    var wb = w.get_BoundingBox(null);
                    var dirW = lc.Curve is Line wl0 ? wl0.Direction : (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0)).Normalize();
                    var near = index.Overlapping(p, p, gap + 1).Where(o => o.Id != w.Id).Select(o =>
                    {
                        // Same storey only: the walls must overlap in height.
                        var ob = o.get_BoundingBox(null);
                        if (ob is null || wb is null || ob.Max.Z <= wb.Min.Z + 0.1 || ob.Min.Z >= wb.Max.Z - 0.1) return (o, d: double.MaxValue);
                        var oc = ((LocationCurve)o.Location).Curve;
                        var flat = new XYZ(p.X, p.Y, oc.GetEndPoint(0).Z);
                        var dirO = oc is Line ol ? ol.Direction : (oc.GetEndPoint(1) - oc.GetEndPoint(0)).Normalize();
                        if (Math.Abs(dirW.DotProduct(dirO)) > 0.985)
                        {
                            // Parallel: only a gap when collinear (end to end); side-by-side walls are not gaps.
                            var off = (oc.Project(flat)?.XYZPoint ?? flat) - flat;
                            if (Math.Abs(off.DotProduct(XYZ.BasisZ.CrossProduct(dirW).Normalize())) > 0.05) return (o, d: double.MaxValue);
                            var endGap = Math.Min(new XYZ(oc.GetEndPoint(0).X - p.X, oc.GetEndPoint(0).Y - p.Y, 0).GetLength(), new XYZ(oc.GetEndPoint(1).X - p.X, oc.GetEndPoint(1).Y - p.Y, 0).GetLength());
                            return (o, d: endGap);
                        }
                        var d = oc.Project(flat)?.Distance ?? double.MaxValue;
                        return (o, d: d - o.Width / 2);
                    }).Where(x => x.d > 1 / 304.8 && x.d < gap).OrderBy(x => x.d).FirstOrDefault();
                    if (near.o != null) Add("wall_gaps", w, $"end {end + 1} stops {Math.Round(near.d * 304.8)} mm short of wall {near.o.Id.Value} without joining");
                }
            }
        }

        if (On("unsupported_beams"))
        {
            var reach = args.OptDouble("gap_tolerance") is { } gt1 ? RevitHelpers.ToInternal(doc, gt1) : 300 / 304.8;
            population["unsupported_beams"] = beams.Count;
            var supports = new BoxIndex<Element>(10);
            foreach (var e in columns.Cast<Element>().Concat(walls).Concat(beams).Concat(Checks.Of(doc, BuiltInCategory.OST_StructuralFoundation)))
                if (e.get_BoundingBox(null) is { } b) supports.Add(e, b.Min, b.Max);
            foreach (var b in beams)
            {
                var line = (Line)((LocationCurve)b.Location).Curve;
                for (var end = 0; end < 2; end++)
                {
                    var p = line.GetEndPoint(end);
                    var held = supports.Overlapping(p, p, reach).Any(s => s.Id != b.Id && (s is not FamilyInstance fb || fb.Category?.Id.Value != (long)BuiltInCategory.OST_StructuralFraming
                        || (fb.Location as LocationCurve)?.Curve is Line other && other.Distance(p) < reach && Math.Abs(other.Direction.DotProduct(line.Direction)) < 0.99));
                    if (!held) Add("unsupported_beams", b, $"end {end + 1} sits on no column, wall, beam or foundation (within {Math.Round(reach * 304.8)} mm)");
                }
            }
        }

        if (On("columns_off_grid"))
        {
            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Select(g => g.Curve).OfType<Line>().ToList();
            if (grids.Count >= 2)
            {
                var gt = tol ?? 50 / 304.8;
                population["columns_off_grid"] = columns.Count;
                foreach (var c in columns)
                {
                    if (c.Location is not LocationPoint lp) continue;
                    var on = grids.Where(g => Line.CreateUnbound(new XYZ(g.Origin.X, g.Origin.Y, 0), new XYZ(g.Direction.X, g.Direction.Y, 0)).Distance(new XYZ(lp.Point.X, lp.Point.Y, 0)) < gt).ToList();
                    var crossing = on.Any(a => on.Any(b => Math.Abs(a.Direction.DotProduct(b.Direction)) < 0.99));
                    if (!crossing) Add("columns_off_grid", c, on.Count == 0 ? "not on any grid line" : "on one grid line only, not at an intersection");
                }
            }
        }

        if (On("off_axis"))
        {
            var lines = walls.Select(w => ((Element)w, ((LocationCurve)w.Location).Curve as Line))
                .Concat(beams.Select(b => ((Element)b, ((LocationCurve)b.Location).Curve as Line)))
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Select(g => ((Element)g, g.Curve as Line)))
                .Where(x => x.Item2 != null).ToList();
            population["off_axis"] = lines.Count;
            foreach (var (e, l) in lines)
            {
                var deg = Math.Atan2(l!.Direction.Y, l.Direction.X) * 180 / Math.PI;
                var off = Math.Abs(deg - Math.Round(deg / 90) * 90);
                if (off > 0.001 && off < 1) Add("off_axis", e, $"{off.ToString("0.###", CultureInfo.InvariantCulture)}° off square");
            }
        }

        if (On("level_offsets"))
        {
            var items = new List<(Element e, double off)>();
            items.AddRange(walls.Select(w => ((Element)w, w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0)));
            items.AddRange(columns.Select(c => ((Element)c, c.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0)));
            items.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Floor)).Select(f => (f, f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0)));
            population["level_offsets"] = items.Count;
            foreach (var (e, off) in items.Where(x => Math.Abs(x.off) > maxOffset))
                Add("level_offsets", e, $"{Math.Round(off * 304.8)} mm from its level ({(doc.GetElement(e.LevelId) as Level)?.Name}); probably on the wrong level");
        }

        if (On("rooms"))
        {
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>().ToList();
            population["rooms"] = rooms.Count;
            foreach (var r in rooms.Where(r => r.Area <= 0))
                Add("rooms", r, r.Location is null ? "not placed" : "not enclosed (or redundant)");
        }

        if (On("required_parameters"))
        {
            var names = args.StringList("required_parameters");
            if (names.Count == 0) names = ["Mark"];
            var hosts = new[] { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors }
                .SelectMany(c => Checks.Of(doc, c)).ToList();
            population["required_parameters"] = hosts.Count;
            foreach (var e in hosts)
            {
                var absent = names.Where(n => e.LookupParameter(n) is null && (doc.GetElement(e.GetTypeId())?.LookupParameter(n)) is null).ToList();
                var missing = names.Except(absent).Where(n => (e.LookupParameter(n) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(n)) is { } p && (!p.HasValue || (p.StorageType == StorageType.String && string.IsNullOrWhiteSpace(p.AsString())))).ToList();
                if (missing.Count > 0 || absent.Count > 0)
                    Add("required_parameters", e, string.Join("; ", new[] { missing.Count > 0 ? $"empty: {string.Join(", ", missing)}" : null, absent.Count > 0 ? $"no such parameter: {string.Join(", ", absent)}" : null }.Where(x => x != null)));
            }
        }

        var file = Checks.Write(args, $"{doc.Title} - Model check", findings);
        var scores = population.Select(kv =>
        {
            var n = findings.Count(f => f.Check == kv.Key);
            return new { check = kv.Key, findings = n, checkedItems = kv.Value, score = (int)Math.Round(100 * Math.Max(0, 1 - (double)n / Math.Max(1, kv.Value))) };
        }).ToList();
        return ToolResult.Ok(new
        {
            overallScore = scores.Count == 0 ? 100 : (int)Math.Round(scores.Average(s => s.score)),
            scores, findings = findings.Count, file, bcf = Checks.Bcf(app, args, $"{doc.Title} - Model check", findings),
            warningTypes = findings.Where(f => f.Check == "warnings").GroupBy(f => f.Issue).Select(g => new { warning = g.Key, count = g.Count() }).OrderByDescending(g => g.count).Take(25),
            sample = findings.Where(f => f.Check != "warnings").GroupBy(f => f.Check).SelectMany(g => g.Take(15)).Select(f => new { f.Check, f.Id, f.What, f.Issue }),
        });
    }
}

public sealed class FindMissingItemsTool : IRevitTool
{
    public string Name => "find_missing_items";
    public string Description =>
        "Find what is missing: rooms with no door, columns with no footing under them, doors and windows in masonry walls " +
        "with no lintel or beam over them, structural elements never tagged in any view, sheets with no views, and (with " +
        "an architectural link as source) load-bearing walls, columns and floors that have no structural counterpart.";
    public string PropertiesJson => """
    {
      "checks": {"type": "array", "items": {"type": "string", "enum": ["rooms_without_door", "columns_without_footing", "openings_without_lintel", "untagged_structure", "sheets_without_views", "arch_without_structure"]}, "description": "Default all that apply."},
      "source": {"type": "string", "description": "Architectural link for rooms and arch_without_structure (default this model)."},
      "bcf": {"type": "boolean"},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var wanted = args.StringList("checks");
        bool On(string c) => wanted.Count == 0 || wanted.Contains(c);
        Document src = doc; var tr = Transform.Identity; var sourceName = "this model";
        if (args.OptString("source") is { } source && !source.Equals("model", StringComparison.OrdinalIgnoreCase))
        {
            var link = LinkMatch.Find(doc, new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList(), source);
            src = link.GetLinkDocument() ?? throw new ArgumentException($"Link '{link.Name}' is not loaded.");
            tr = link.GetTotalTransform(); sourceName = link.Name;
        }
        var findings = new List<Checks.Finding>();
        var counts = new Dictionary<string, int>();
        void Add(string check, Element e, string issue, string? prefix = null)
        {
            findings.Add(new Checks.Finding(check, !e.Document.IsLinked ? e.Id.Value : 0, (prefix ?? "") + Checks.Describe(e) + (!e.Document.IsLinked ? "" : $" (link id {e.Id.Value})"), issue));
            counts[check] = counts.TryGetValue(check, out var n) ? n + 1 : 1;
        }

        if (On("rooms_without_door"))
        {
            // Each room belongs to one phase: check the doors in every phase.
            var withDoor = new HashSet<long>();
            var doors = new FilteredElementCollector(src).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            foreach (Phase phase in src.Phases)
                foreach (var d in doors)
                {
                    if (d.get_FromRoom(phase) is { } a) withDoor.Add(a.Id.Value);
                    if (d.get_ToRoom(phase) is { } b) withDoor.Add(b.Id.Value);
                }
            foreach (var r in new FilteredElementCollector(src).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>().Where(r => r.Area > 0 && !withDoor.Contains(r.Id.Value)))
                Add("rooms_without_door", r, "no door opens into it (shafts and risers may be fine)", $"{r.Number} {r.Name}: ");
        }

        var foundations = Checks.Of(doc, BuiltInCategory.OST_StructuralFoundation).Concat(new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.FloorType.IsFoundationSlab))
            .Select(e => e.get_BoundingBox(null)).Where(b => b != null).ToList();
        if (On("columns_without_footing"))
        {
            var columns = Checks.Of(doc, BuiltInCategory.OST_StructuralColumns).OfType<FamilyInstance>().ToList();
            var axes = columns.Select(c => (c, a: Steel.ColumnAxis(c))).Where(x => x.a != null).ToList();
            if (axes.Count > 0)
            {
                var fIndex = new BoxIndex<BoundingBoxXYZ>(10);
                foreach (var b in foundations) fIndex.Add(b!, b!.Min, b.Max);
                var tops = new BoxIndex<FamilyInstance>(10);
                foreach (var (c, a) in axes) { var top = a!.GetEndPoint(0).Z > a.GetEndPoint(1).Z ? a.GetEndPoint(0) : a.GetEndPoint(1); tops.Add(c, top, top); }
                var floors = new BoxIndex<Element>(10);
                foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => !f.FloorType.IsFoundationSlab).Concat<Element>(Checks.Of(doc, BuiltInCategory.OST_StructuralFraming)))
                    if (f.get_BoundingBox(null) is { } fb) floors.Add(f, fb.Min, fb.Max);
                foreach (var (c, a) in axes)
                {
                    var bottom = a!.GetEndPoint(0).Z < a.GetEndPoint(1).Z ? a.GetEndPoint(0) : a.GetEndPoint(1);
                    // Standing on a column, a beam (transfer) or a suspended floor below: not a foundation case.
                    if (tops.Overlapping(bottom - new XYZ(0, 0, 1000 / 304.8), bottom, 0.3).Any(o => o.Id != c.Id)) continue;
                    var footed = fIndex.Overlapping(bottom - new XYZ(0, 0, 1000 / 304.8), bottom, 0.1).Any();
                    if (footed) continue;
                    var onSlab = floors.Overlapping(bottom - new XYZ(0, 0, 0.5), bottom, 0.05).Any();
                    Add("columns_without_footing", c, onSlab ? "stands on a slab or beam with no footing, pile cap or raft below it (check: transfer or ground-bearing slab?)" : "no footing, pile cap or raft under its base");
                }
            }
        }

        if (On("openings_without_lintel"))
        {
            var framing = new BoxIndex<BoundingBoxXYZ>(10);
            foreach (var b in Checks.Of(doc, BuiltInCategory.OST_StructuralFraming).Concat(Checks.Of(doc, BuiltInCategory.OST_GenericModel).Where(e => (e.Name + Checks.Describe(e)).IndexOf("lintel", StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(e => e.get_BoundingBox(null)).Where(b => b != null)) framing.Add(b, b.Min, b.Max);
            var masonryType = new Dictionary<long, bool>();
            bool Masonry(Wall w)
            {
                if (masonryType.TryGetValue(w.WallType.Id.Value, out var known)) return known;
                return masonryType[w.WallType.Id.Value] = w.WallType.GetCompoundStructure() is { } cs && cs.GetLayers().Any(l => doc.GetElement(l.MaterialId) is Material m && new[] { "Masonry", "Block", "Brick", "Stone", "CMU" }.Any(x => m.Name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0 || (m.MaterialClass ?? "").IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0));
            }
            foreach (var o in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows }.SelectMany(c => Checks.Of(doc, c)).OfType<FamilyInstance>())
            {
                if (o.Host is not Wall w || !Masonry(w) || o.get_BoundingBox(null) is not { } ob) continue;
                var head = ob.Max.Z;
                var covered = framing.Overlapping(new XYZ(ob.Min.X, ob.Min.Y, head), new XYZ(ob.Max.X, ob.Max.Y, head + 600 / 304.8), 0).Any(b => b.Min.Z >= head - 0.05);
                if (!covered) Add("openings_without_lintel", o, $"in masonry wall {w.Id.Value} with no lintel or beam within 600 mm above the head");
            }
        }

        if (On("untagged_structure"))
        {
            var tagged = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().SelectMany(t => t.GetTaggedLocalElementIds()).Select(id => id.Value));
            foreach (var e in new[] { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation }.SelectMany(c => Checks.Of(doc, c)))
            {
                if (e is FamilyInstance { SuperComponent: not null } || e is WallFoundation) continue;
                if (e is FamilyInstance fi && fi.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming && BeamSystem.BeamBelongsTo(fi) != null) continue;
                if (!tagged.Contains(e.Id.Value)) Add("untagged_structure", e, "not tagged in any view");
            }
        }

        if (On("sheets_without_views"))
        {
            var withSchedules = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Where(i => !i.IsTitleblockRevisionSchedule).Select(i => i.OwnerViewId.Value));
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder && s.GetAllPlacedViews().Count == 0 && !withSchedules.Contains(s.Id.Value)))
                Add("sheets_without_views", s, "no views placed", $"{s.SheetNumber} ");
        }

        if (On("arch_without_structure") && src != doc)
        {
            var made = ConcreteTypes.MadeFrom(doc);
            var structure = new BoxIndex<Element>(10);
            foreach (var e in new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralColumns }.SelectMany(c => Checks.Of(doc, c)))
                if (e.get_BoundingBox(null) is { } b) structure.Add(e, b.Min, b.Max);
            IEnumerable<Element> Bearing()
            {
                foreach (var w in new FilteredElementCollector(src).OfClass(typeof(Wall)).Cast<Wall>())
                    if (w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1) yield return w;
                foreach (var c in Checks.Of(src, BuiltInCategory.OST_Columns)) yield return c;
                foreach (var f in new FilteredElementCollector(src).OfClass(typeof(Floor)).Cast<Floor>())
                    if (f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1 || (f.FloorType.GetCompoundStructure()?.GetWidth() ?? 0) >= 150 / 304.8) yield return f;
            }
            foreach (var e in Bearing())
            {
                if (made.Contains(e.UniqueId) || e.get_BoundingBox(null) is not { } b) continue;
                var mid = tr.OfPoint((b.Min + b.Max) / 2);
                if (!structure.Overlapping(mid, mid, 0.5).Any()) Add("arch_without_structure", e, "no structural element at its position (run arch_to_structure)");
            }
        }

        var file = Checks.Write(args, $"{doc.Title} - Missing items", findings);
        return ToolResult.Ok(new
        {
            source = sourceName, missing = findings.Count, byCheck = counts, file, bcf = Checks.Bcf(app, args, $"{doc.Title} - Missing items", findings),
            sample = findings.GroupBy(f => f.Check).SelectMany(g => g.Take(15)).Select(f => new { f.Check, f.Id, f.What, f.Issue }),
        });
    }
}

public sealed class ModelSnapshotTool : IRevitTool
{
    public string Name => "model_snapshot";
    public string Description =>
        "Track changes: 'take' saves a snapshot of this model or a link (every element's category, type, level, geometry " +
        "fingerprint and key parameters); 'compare' lists what was added, deleted and modified between two snapshots (or a " +
        "snapshot and now), with what changed, to CSV, and can colour a view (green added, orange modified); 'list' shows " +
        "saved snapshots. Use before and after a design change, or on each new arch link drop.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["take", "compare", "list"]},
      "name": {"type": "string", "description": "take: snapshot name (default date and time)."},
      "source": {"type": "string", "description": "'model' (default) or a link name."},
      "from": {"type": "string", "description": "compare: older snapshot name."},
      "to": {"type": "string", "description": "compare: newer snapshot name (default: the model now)."},
      "parameters": {"type": "array", "items": {"type": "string"}, "description": "Extra parameter names to track (Mark, Comments, type and level always)."},
      "color_view_id": {"type": "integer", "description": "compare: colour added and modified elements in this view (this model only)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => ["action"];
    public bool ModifiesModel => true;

    private sealed class Item { public long Id { get; set; } public string Cat { get; set; } = ""; public string Type { get; set; } = ""; public string Level { get; set; } = ""; public string Geo { get; set; } = ""; public Dictionary<string, string> P { get; set; } = new(); }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        Document src = doc; var sourceName = "model";
        if (args.OptString("source") is { } source && !source.Equals("model", StringComparison.OrdinalIgnoreCase))
        {
            var link = LinkMatch.Find(doc, new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList(), source);
            src = link.GetLinkDocument() ?? throw new ArgumentException($"Link '{link.Name}' is not loaded.");
            sourceName = src.Title;
        }
        string Safe(string s) => new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "snapshots", Checks.ModelKey(doc), Safe(sourceName));
        var extra = args.StringList("parameters");

        // Unbound curves (a full circle wall, a cylindrical grid, an unbound model line) throw on GetEndPoint, so
        // fingerprint them by centre and radius instead; any other curve that cannot be read gives an empty key.
        static string CurveKey(LocationCurve lc, Func<double, string> R)
        {
            try
            {
                // The Curve getter itself can throw "The input curve is not bound" for some elements.
                var c = lc.Curve;
                if (c is null) return "";
                if (c.IsBound)
                {
                    XYZ a = c.GetEndPoint(0), z = c.GetEndPoint(1);
                    return $"{R(a.X)},{R(a.Y)},{R(a.Z)}>{R(z.X)},{R(z.Y)},{R(z.Z)}";
                }
                return c switch
                {
                    Arc arc => $"circle {R(arc.Center.X)},{R(arc.Center.Y)},{R(arc.Center.Z)} r{R(arc.Radius)}",
                    Ellipse el => $"ellipse {R(el.Center.X)},{R(el.Center.Y)},{R(el.Center.Z)} r{R(el.RadiusX)}x{R(el.RadiusY)}",
                    _ => "unbound " + c.GetType().Name,
                };
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return ""; }
            catch (ArgumentException) { return ""; }
        }

        Dictionary<string, Item> Take()
        {
            var items = new Dictionary<string, Item>();
            using var md5 = MD5.Create();
            foreach (var e in new FilteredElementCollector(src).WhereElementIsNotElementType())
            {
                BoundingBoxXYZ? bb;
                try { bb = e.Category is { CategoryType: CategoryType.Model } ? e.get_BoundingBox(null) : null; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { bb = null; }
                catch (ArgumentException) { bb = null; }
                if (bb is not { } b) continue;
                string R(double v) => Math.Round(v * 304.8).ToString(CultureInfo.InvariantCulture);
                var loc = e.Location switch
                {
                    LocationPoint lp => $"{R(lp.Point.X)},{R(lp.Point.Y)},{R(lp.Point.Z)}",
                    LocationCurve lc => CurveKey(lc, R),
                    _ => "",
                };
                var geo = $"{R(b.Min.X)},{R(b.Min.Y)},{R(b.Min.Z)}|{R(b.Max.X)},{R(b.Max.Y)},{R(b.Max.Z)}|{loc}";
                var item = new Item
                {
                    Id = e.Id.Value, Cat = e.Category.Name, Type = (src.GetElement(e.GetTypeId()) as ElementType)?.Name ?? "",
                    Level = (src.GetElement(e.LevelId) as Level)?.Name ?? "",
                    Geo = Convert.ToBase64String(md5.ComputeHash(Encoding.UTF8.GetBytes(geo))).Substring(0, 12),
                };
                foreach (var n in new[] { "Mark", "Comments" }.Concat(extra))
                    if (e.LookupParameter(n) is { HasValue: true } p) item.P[n] = p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
                items[e.UniqueId] = item;
            }
            return items;
        }
        Dictionary<string, Item> Load(string name)
        {
            var file = Path.Combine(folder, Safe(name) + ".json");
            if (!File.Exists(file)) throw new ArgumentException($"No snapshot '{name}' for {sourceName}. Saved: {string.Join(", ", Saved())}");
            try { return JsonSerializer.Deserialize<Dictionary<string, Item>>(File.ReadAllText(file)) ?? new(); }
            catch (JsonException) { throw new ArgumentException($"Snapshot '{name}' is damaged; take a new one."); }
        }
        IEnumerable<string> Saved() => Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").OrderBy(File.GetLastWriteTime).Select(Path.GetFileNameWithoutExtension).OfType<string>() : [];

        switch (args.String("action"))
        {
            case "list":
                return ToolResult.Ok(new { source = sourceName, snapshots = Saved() });
            case "take":
            {
                var name = args.OptString("name") ?? DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
                var items = Take();
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, Safe(name) + ".json"), JsonSerializer.Serialize(items));
                return ToolResult.Ok(new { source = sourceName, snapshot = name, elements = items.Count, byCategory = items.Values.GroupBy(i => i.Cat).Select(g => new { category = g.Key, count = g.Count() }).OrderByDescending(g => g.count).Take(20) });
            }
            case "compare":
            {
                var before = Load(args.OptString("from") ?? throw new ArgumentException("Give from (a snapshot name)."));
                var after = args.OptString("to") is { } to ? Load(to) : Take();
                var added = after.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
                var deleted = before.Where(kv => !after.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
                var modified = new List<(Item item, string what)>();
                foreach (var kv in after)
                {
                    if (!before.TryGetValue(kv.Key, out var old)) continue;
                    var n = kv.Value; var what = new List<string>();
                    if (old.Type != n.Type) what.Add($"type {old.Type} > {n.Type}");
                    if (old.Level != n.Level) what.Add($"level {old.Level} > {n.Level}");
                    if (old.Geo != n.Geo) what.Add("geometry");
                    foreach (var k in old.P.Keys.Union(n.P.Keys))
                    {
                        old.P.TryGetValue(k, out var a); n.P.TryGetValue(k, out var b);
                        if ((a ?? "") != (b ?? "")) what.Add($"{k} '{a}' > '{b}'");
                    }
                    if (what.Count > 0) modified.Add((n, string.Join("; ", what)));
                }
                var dir = Checks.Folder(args);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"{doc.Title} - Changes {Safe(sourceName)} {DateTime.Now:yyyy-MM-dd HHmm}.csv");
                var sb = new StringBuilder("Change,Id,Category,Type,Level,What changed\n");
                foreach (var i in added) sb.AppendLine(string.Join(",", "added", i.Id, Checks.Csv(i.Cat), Checks.Csv(i.Type), Checks.Csv(i.Level), ""));
                foreach (var i in deleted) sb.AppendLine(string.Join(",", "deleted", i.Id, Checks.Csv(i.Cat), Checks.Csv(i.Type), Checks.Csv(i.Level), ""));
                foreach (var (i, w) in modified) sb.AppendLine(string.Join(",", "modified", i.Id, Checks.Csv(i.Cat), Checks.Csv(i.Type), Checks.Csv(i.Level), Checks.Csv(w)));
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

                string? colored = null;
                if (args.OptLong("color_view_id") is { } vid && src == doc && args.OptString("to") is null)
                {
                    var view = doc.GetElement(new ElementId(vid)) as View ?? throw new ArgumentException($"{vid} is not a view.");
                    if (!view.AreGraphicsOverridesAllowed()) throw new ArgumentException($"'{view.Name}' doesn't allow graphic overrides; give a plan, section or 3D view.");
                    var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                    OverrideGraphicSettings Paint(Color c)
                    {
                        var o = new OverrideGraphicSettings().SetProjectionLineColor(c).SetCutLineColor(c).SetSurfaceForegroundPatternColor(c).SetCutForegroundPatternColor(c);
                        if (solid != null) o.SetSurfaceForegroundPatternId(solid.Id).SetCutForegroundPatternId(solid.Id);
                        return o;
                    }
                    using var t = RevitHelpers.StartTransaction(doc, "Colour changes");
                    foreach (var i in added) if (doc.GetElement(new ElementId(i.Id)) != null) view.SetElementOverrides(new ElementId(i.Id), Paint(new Color(0, 170, 0)));
                    foreach (var (i, _) in modified) if (doc.GetElement(new ElementId(i.Id)) != null) view.SetElementOverrides(new ElementId(i.Id), Paint(new Color(255, 140, 0)));
                    colored = t.Commit() == TransactionStatus.Committed ? $"Coloured in '{view.Name}': green added, orange modified (deleted elements are only in the CSV)." : "Revit refused the colours.";
                }
                return ToolResult.Ok(new
                {
                    source = sourceName, added = added.Count, deleted = deleted.Count, modified = modified.Count, file, colored,
                    byCategory = added.Select(i => (i.Cat, c: "added")).Concat(deleted.Select(i => (i.Cat, c: "deleted"))).Concat(modified.Select(m => (m.item.Cat, c: "modified")))
                        .GroupBy(x => x.Cat).Select(g => new { category = g.Key, added = g.Count(x => x.c == "added"), deleted = g.Count(x => x.c == "deleted"), modified = g.Count(x => x.c == "modified") }).OrderByDescending(g => g.added + g.deleted + g.modified).Take(25),
                    sampleModified = modified.Take(25).Select(m => new { m.item.Id, m.item.Cat, m.what }),
                });
            }
            default:
                throw new ArgumentException("action is take, compare or list.");
        }
    }
}

public sealed class CoordinationProgressTool : IRevitTool
{
    public string Name => "coordination_progress";
    public string Description =>
        "Track coordination over time: 'record' runs a named clash test (find_clashes arguments) and saves which clashes " +
        "exist; 'report' shows each test's runs with total, new, resolved and still-open clashes, by category pair, to a " +
        "CSV for a progress dashboard. Record the same test name after each model update.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["record", "report"]},
      "test": {"type": "string", "description": "record: the test name, e.g. 'Structure vs MEP L1'."},
      "clash_args": {"type": "object", "description": "record: the find_clashes arguments (set_a, set_b, level, clearance...)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => ["action"];
    public bool ModifiesModel => false;

    private sealed class Run { public string Date { get; set; } = ""; public List<string> Keys { get; set; } = []; public Dictionary<string, string> Pairs { get; set; } = new(); }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "coordination", Checks.ModelKey(doc) + ".json");
        Dictionary<string, List<Run>> history;
        try { history = File.Exists(store) ? JsonSerializer.Deserialize<Dictionary<string, List<Run>>>(File.ReadAllText(store)) ?? new() : new Dictionary<string, List<Run>>(); }
        catch (JsonException) { throw new ArgumentException($"The coordination history file is damaged: {store}"); }

        if (args.String("action") == "record")
        {
            var test = args.OptString("test") ?? throw new ArgumentException("Give the test name.");
            if (!args.Has("clash_args")) throw new ArgumentException("Give clash_args (the find_clashes arguments).");
            var raw = args.Raw["clash_args"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            raw["limit"] = JsonSerializer.SerializeToElement(100000);
            raw["bcf"] = JsonSerializer.SerializeToElement(false);
            var result = new FindClashesTool().Execute(app, new ToolArgs(raw));
            if (result.IsError) return result;
            using var parsed = JsonDocument.Parse(result.Text);
            var csv = parsed.RootElement.GetProperty("file").GetString()!;
            if (parsed.RootElement.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String)
                return ToolResult.Error($"Not recorded: the clash run was incomplete ({note.GetString()}). Narrow the test (level or categories) so it completes.");
            var run = new Run { Date = Checks.Now };
            foreach (var row in File.ReadAllLines(csv).Skip(1).Select(Split).Where(r => r.Count >= 8))
            {
                // Same pair in either order is the same clash.
                // Link names change with each drop (dates, versions): key links by their name without digits.
                string Src(string x) => Regex.Replace(x, @"[\d_\-. ]+", "");
                var a = $"{Src(row[3])}:{row[4]}"; var b = $"{Src(row[6])}:{row[7]}";
                var key = string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
                if (run.Pairs.ContainsKey(key)) continue;
                run.Keys.Add(key);
                run.Pairs[key] = $"{Cat(row[2])} / {Cat(row[5])}";
            }
            if (!history.TryGetValue(test, out var runs)) history[test] = runs = [];
            runs.Add(run);
            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            File.WriteAllText(store, JsonSerializer.Serialize(history));
            var prev = runs.Count > 1 ? new HashSet<string>(runs[runs.Count - 2].Keys) : [];
            var now = new HashSet<string>(run.Keys);
            return ToolResult.Ok(new { test, run = runs.Count, clashes = now.Count, @new = now.Count(k => !prev.Contains(k)), resolved = prev.Count(k => !now.Contains(k)), csv });
        }

        if (history.Count == 0) return ToolResult.Error("Nothing recorded yet; use action 'record' with a test name and clash_args.");
        var folder = Checks.Folder(args);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{doc.Title} - Coordination progress {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        var sb = new StringBuilder("Test,Run,Date,Total,New,Resolved,Open from before,Category pair,Pair count\n");
        var summary = new List<object>();
        foreach (var (test, runs) in history.Select(kv => (kv.Key, kv.Value)))
        {
            var rows = new List<object>();
            HashSet<string> prev = [];
            for (var i = 0; i < runs.Count; i++)
            {
                var now = new HashSet<string>(runs[i].Keys);
                int added = now.Count(k => !prev.Contains(k)), resolved = prev.Count(k => !now.Contains(k)), open = now.Count - added;
                sb.AppendLine(string.Join(",", Checks.Csv(test), i + 1, runs[i].Date, now.Count, added, resolved, open, "", ""));
                foreach (var g in runs[i].Pairs.Values.GroupBy(x => x).OrderByDescending(g => g.Count()))
                    sb.AppendLine(string.Join(",", Checks.Csv(test), i + 1, runs[i].Date, "", "", "", "", Checks.Csv(g.Key), g.Count()));
                rows.Add(new { run = i + 1, date = runs[i].Date, total = now.Count, @new = added, resolved, openFromBefore = open });
                prev = now;
            }
            summary.Add(new { test, runs = rows });
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return ToolResult.Ok(new { tests = summary, file });
    }

    private static string Cat(string described)
    {
        // find_clashes describes elements as "<Category> <id> ...".
        var m = System.Text.RegularExpressions.Regex.Match(described, @"^(.*?) \d+(\s|$)");
        return m.Success ? m.Groups[1].Value : described;
    }

    private static List<string> Split(string line)
    {
        var cells = new List<string>(); var sb = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
