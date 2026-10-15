using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>An element from this model or a link, with its box in this model's coordinates.</summary>
internal sealed class Placed(Element element, Transform transform, string source, bool inHost)
{
    public Element Element { get; } = element;
    public Transform Transform { get; } = transform;
    public string Source { get; } = source;
    public bool InHost { get; } = inHost;
    public XYZ Min = XYZ.Zero, Max = XYZ.Zero;
    private List<Solid>? _solids;

    /// <summary>The unattached voids of a void family instance, rebuilt as positive solids (Revit returns no void geometry).</summary>
    public bool IsVoid { get; private set; }
    private List<Solid>? _voidSolids;

    /// <summary>Solids with negative volume Revit returned (void or inside-out geometry), left out of the boolean checks.</summary>
    public int NegativeSolids { get; private set; }

    public string Key => $"{Source}:{Element.Id.Value}" + (IsVoid ? "#void" : "");

    /// <summary>A second entry for a void family instance whose voids were rebuilt as positive solids (model coordinates).</summary>
    public static Placed ForVoid(Placed of, List<Solid> solids)
    {
        var p = new Placed(of.Element, Transform.Identity, of.Source, of.InHost) { IsVoid = true, _voidSolids = solids };
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var s in solids)
        {
            var bb = s.GetBoundingBox();
            foreach (var c in new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z) })
            {
                var w = bb.Transform.OfPoint(c);
                x0 = Math.Min(x0, w.X); y0 = Math.Min(y0, w.Y); z0 = Math.Min(z0, w.Z);
                x1 = Math.Max(x1, w.X); y1 = Math.Max(y1, w.Y); z1 = Math.Max(z1, w.Z);
            }
        }
        p.Min = new XYZ(x0, y0, z0); p.Max = new XYZ(x1, y1, z1);
        return p;
    }

    /// <summary>Drops the cached solids (for elements that won't be compared again).</summary>
    public void Release() { if (!IsVoid) _solids = null; }

    public List<Solid> Solids()
    {
        if (IsVoid) return _voidSolids!;
        if (_solids != null) return _solids;
        _solids = [];
        Collect(Element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
        if (!Transform.IsIdentity) _solids = _solids.Select(s => SolidUtils.CreateTransformed(s, Transform)).ToList();
        return _solids;

        void Collect(GeometryElement? g)
        {
            if (g is null) return;
            foreach (var o in g)
                if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry());
                else if (o is Solid { Volume: > 1e-6 } s) _solids!.Add(s);
                // A negative volume is void (or inside-out) geometry: intersecting it gives nonsense such as whole-floor volumes.
                else if (o is Solid { Volume: < -1e-6 }) NegativeSolids++;
        }
    }

    public string Describe() => (IsVoid ? "void of " : "") + $"{Element.Category?.Name} {Element.Id.Value}" + (InHost ? "" : $" in {Source}") +
                                (Element is FamilyInstance fi ? $" ({fi.Symbol.FamilyName}: {fi.Symbol.Name})" : Element.Name is { Length: > 0 } n ? $" ({n})" : "");
}

internal static class Sources
{
    /// <summary>Elements of these categories in this model ('model') or in the link named.</summary>
    public static List<Placed> Collect(Document doc, string source, IList<string> categories)
    {
        Document from = doc; var tr = Transform.Identity; var name = "model"; var inHost = true;
        if (!string.Equals(source, "model", StringComparison.OrdinalIgnoreCase) && !string.Equals(source, "host", StringComparison.OrdinalIgnoreCase))
        {
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            var link = LinkMatch.Find(doc, links, source);
            from = link.GetLinkDocument() ?? throw new ArgumentException($"Link '{link.Name}' is not loaded.");
            tr = link.GetTotalTransform();
            name = link.Name; inHost = false;
        }
        if (categories.Count == 0) throw new ArgumentException("Give the categories to check, e.g. ['Structural Framing', 'Ducts'].");
        var filter = new LogicalOrFilter(categories.Select(c => (ElementFilter)new ElementCategoryFilter(RevitHelpers.ParseCategory(doc, c))).ToList());
        var list = new List<Placed>();
        foreach (var e in new FilteredElementCollector(from).WherePasses(filter).WhereElementIsNotElementType())
        {
            if (e.get_BoundingBox(null) is not { } bb) continue;
            var p = new Placed(e, tr, name, inHost);
            if (tr.IsIdentity) { p.Min = bb.Min; p.Max = bb.Max; }
            else
            {
                var corners = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                    new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z), new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z) }
                    .Select(tr.OfPoint).ToList();
                p.Min = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
                p.Max = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
            }
            list.Add(p);
        }
        return list;
    }
}

/// <summary>A plan grid over boxes, queried by box.</summary>
internal sealed class BoxIndex<T>(double cell) where T : class
{
    private readonly Dictionary<(long, long), List<(T item, XYZ min, XYZ max)>> _cells = new();
    private readonly List<(T item, XYZ min, XYZ max)> _big = [];

    public void Add(T item, XYZ min, XYZ max)
    {
        // Huge boxes (toposolids, stray far-off geometry) would fill millions of cells; keep them aside and always test them.
        if ((Math.Floor(max.X / cell) - Math.Floor(min.X / cell) + 1) * (Math.Floor(max.Y / cell) - Math.Floor(min.Y / cell) + 1) > 2500) { _big.Add((item, min, max)); return; }
        for (var x = (long)Math.Floor(min.X / cell); x <= (long)Math.Floor(max.X / cell); x++)
            for (var y = (long)Math.Floor(min.Y / cell); y <= (long)Math.Floor(max.Y / cell); y++)
            {
                if (!_cells.TryGetValue((x, y), out var list)) _cells[(x, y)] = list = [];
                list.Add((item, min, max));
            }
    }

    public IEnumerable<T> Overlapping(XYZ min, XYZ max, double tol)
    {
        var seen = new HashSet<T>();
        foreach (var (item, a, b) in _big)
            if (a.X <= max.X + tol && b.X >= min.X - tol && a.Y <= max.Y + tol && b.Y >= min.Y - tol && a.Z <= max.Z + tol && b.Z >= min.Z - tol && seen.Add(item))
                yield return item;
        for (var x = (long)Math.Floor((min.X - tol) / cell); x <= (long)Math.Floor((max.X + tol) / cell); x++)
            for (var y = (long)Math.Floor((min.Y - tol) / cell); y <= (long)Math.Floor((max.Y + tol) / cell); y++)
            {
                if (!_cells.TryGetValue((x, y), out var list)) continue;
                foreach (var (item, a, b) in list)
                    if (a.X <= max.X + tol && b.X >= min.X - tol && a.Y <= max.Y + tol && b.Y >= min.Y - tol && a.Z <= max.Z + tol && b.Z >= min.Z - tol
                        && seen.Add(item))
                        yield return item;
            }
    }
}

public sealed class FindClashesTool : IRevitTool
{
    public string Name => "find_clashes";
    public string Description =>
        "Clash detection between two sets of elements, each from this model or a linked model (other trades, or one link " +
        "against another), or inside one set: hard clashes from real solid overlap, or clearance clashes within a distance. " +
        "Skips elements hosted in or joined to each other. Writes a CSV of every clash and, with bcf, a BCF file for " +
        "ACC/Navisworks. Void families are checked with their voids rebuilt (an element running into a void that " +
        "doesn't cut it is a 'void' clash); in-place families whose geometry Revit doesn't expose are listed as not " +
        "checkable. Use on a level or a few categories at a time on big models.";
    public string PropertiesJson => """
    {
      "set_a": {"type": "object", "properties": {"source": {"type": "string", "description": "'model' or a link name."}, "categories": {"type": "array", "items": {"type": "string"}}}, "required": ["categories"]},
      "set_b": {"type": "object", "properties": {"source": {"type": "string"}, "categories": {"type": "array", "items": {"type": "string"}}}, "description": "Default: same as set_a (clashes inside one set)."},
      "clearance": {"type": "number", "description": "Also report elements closer than this (by bounding box), project units. Default 0 = hard clashes only."},
      "min_overlap": {"type": "number", "description": "Ignore overlaps thinner than this, project units (default 10 mm)."},
      "level": {"type": "string", "description": "Only elements whose box spans this level's height band (this level up to the next)."},
      "limit": {"type": "integer", "description": "Max clashes to report (default 1000)."},
      "bcf": {"type": "boolean", "description": "Also write a BCF file with one issue per clash (first 200)."},
      "folder": {"type": "string"},
      "voids": {"type": "string", "enum": ["rebuild", "skip"], "description": "Void family instances (unattached voids): 'rebuild' (default) rebuilds their voids as solids from the family and reports elements running into a void that doesn't cut them (kind 'void'); 'skip' leaves voids out. In-place voids can't be read and are listed as not checkable."}
    }
    """;
    public string[] Required => ["set_a"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        (string source, List<string> cats) Set(JsonElement e) => (
            e.TryGetProperty("source", out var s) ? s.GetString() ?? "model" : "model",
            e.TryGetProperty("categories", out var c) ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : []);
        var a = Set(args.Raw["set_a"]);
        var b = args.Has("set_b") ? Set(args.Raw["set_b"]) : a;
        if (b.cats.Count == 0) b.cats = a.cats;
        var same = string.Equals(a.source, b.source, StringComparison.OrdinalIgnoreCase) && a.cats.OrderBy(x => x).SequenceEqual(b.cats.OrderBy(x => x));
        var clearance = args.OptDouble("clearance") is { } cl ? RevitHelpers.ToInternal(doc, cl) : 0;
        var minOverlap = args.OptDouble("min_overlap") is { } mo ? RevitHelpers.ToInternal(doc, mo) : 10 / 304.8;
        var limit = (int)(args.OptLong("limit") ?? 1000);
        if (limit < 1) throw new ArgumentException("limit must be at least 1.");

        var setA = Sources.Collect(doc, a.source, a.cats);
        var meshOnly = 0;
        var setB = same ? setA : Sources.Collect(doc, b.source, b.cats);
        if (args.OptString("level") is { } levelName)
        {
            var level = RevitHelpers.FindLevel(doc, levelName);
            var above = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Where(l => l.ProjectElevation > level.ProjectElevation + 0.1)
                .Select(l => l.ProjectElevation).DefaultIfEmpty(level.ProjectElevation + 30).Min();
            bool In(Placed p) => p.Max.Z > level.ProjectElevation && p.Min.Z < above;
            setA = setA.Where(In).ToList();
            setB = same ? setA : setB.Where(In).ToList();
        }
        // Voids and in-place families, handled up front instead of giving wrong or empty results.
        var voidMode = args.OptString("voids") ?? "rebuild";
        var notCheckable = new List<string>(); var voidsRebuilt = 0; var voidProblems = new List<string>();
        var voidSession = new VoidSolids.Session(); var voidsCapped = new HashSet<long>();
        List<Placed> Prepare(List<Placed> set)
        {
            var kept = new List<Placed>();
            foreach (var p in set)
            {
                if (InPlace.Is(p.Element) && p.Solids().Count == 0)
                {
                    if (notCheckable.Count < 100) notCheckable.Add($"{p.Describe()}: {InPlace.Short} (an in-place void or solid can't be clash-checked; check it in Revit or Navisworks)");
                    continue;
                }
                kept.Add(p);
                if (p.Element is FamilyInstance fi && !InPlace.Is(fi) && InstanceVoidCutUtils.IsVoidInstanceCuttingElement(fi))
                {
                    if (voidMode == "skip") continue;
                    if (Bridge.Jobs.CancelRequested) throw new OperationCanceledException("Stopped on request (cancel_job).");
                    var solids = VoidSolids.Rebuild(doc, fi, voidSession, out var why, out var capped);
                    if (capped) voidsCapped.Add(fi.Id.Value);
                    if (solids is null) { if (!capped && voidProblems.Count < 30) voidProblems.Add($"{p.Describe()}: {why}"); continue; }
                    if (!p.Transform.IsIdentity) solids = solids.Select(x => SolidUtils.CreateTransformed(x, p.Transform)).ToList();
                    if (p.Solids().Count == 0) kept.Remove(p); // a void-only family: only its rebuilt void takes part
                    kept.Add(Placed.ForVoid(p, solids));
                    voidsRebuilt++;
                }
            }
            return kept;
        }
        setA = Prepare(setA);
        setB = same ? setA : Prepare(setB);

        var index = new BoxIndex<Placed>(10);
        foreach (var p in setB) index.Add(p, p.Min, p.Max);

        var clashes = new List<(Placed a, Placed b, string kind, double depth, XYZ at)>();
        var seen = new HashSet<string>();
        var checkedPairs = 0; var solidFailures = 0; var truncated = false;
        foreach (var pa in setA)
        {
            foreach (var pb in index.Overlapping(pa.Min, pa.Max, clearance))
            {
                if (pa.Key == pb.Key) continue;
                var pair = string.CompareOrdinal(pa.Key, pb.Key) < 0 ? pa.Key + "|" + pb.Key : pb.Key + "|" + pa.Key;
                if (!seen.Add(pair) || Related(pa, pb)) continue;
                if (++checkedPairs > 200000) { truncated = true; break; }
                // Overlap of the boxes, to skip touching faces and to place the clash.
                var lo = new XYZ(Math.Max(pa.Min.X, pb.Min.X), Math.Max(pa.Min.Y, pb.Min.Y), Math.Max(pa.Min.Z, pb.Min.Z));
                var hi = new XYZ(Math.Min(pa.Max.X, pb.Max.X), Math.Min(pa.Max.Y, pb.Max.Y), Math.Min(pa.Max.Z, pb.Max.Z));
                var boxDepth = Math.Min(hi.X - lo.X, Math.Min(hi.Y - lo.Y, hi.Z - lo.Z));
                if (boxDepth >= minOverlap)
                {
                    if (pa.Solids().Count == 0 || pb.Solids().Count == 0) meshOnly++;
                    var (hit, depth, at, failed) = Hard(pa, pb, minOverlap);
                    if (failed) solidFailures++;
                    if (hit) { clashes.Add((pa, pb, pa.IsVoid || pb.IsVoid ? "void" : "hard", depth, at)); if (clashes.Count >= limit) break; continue; }
                }
                // Clearance: boxes apart but closer than the clearance (touching faces like a wall on a slab don't count).
                if (clearance > 0 && boxDepth < -1 / 304.8 && !pa.IsVoid && !pb.IsVoid)
                    clashes.Add((pa, pb, "clearance", 0, (lo + hi) / 2));
                if (clashes.Count >= limit) break;
            }
            if (!same) pa.Release();
            if (clashes.Count >= limit || truncated) break;
        }


        var folder = args.OptString("folder") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{doc.Title} - Clashes {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        string N(double v) => Math.Round(RevitHelpers.FromInternal(doc, v), 1).ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder("No,Kind,Element A,Source A,Id A,Element B,Source B,Id B,Overlap,X,Y,Z\n");
        var i = 0;
        foreach (var c in clashes)
            sb.AppendLine(string.Join(",", ++i, c.kind, Csv(c.a.Describe()), Csv(c.a.Source), c.a.Element.Id.Value, Csv(c.b.Describe()), Csv(c.b.Source), c.b.Element.Id.Value,
                N(c.depth), N(c.at.X), N(c.at.Y), N(c.at.Z)));
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        string? bcf = null;
        if (args.OptBool("bcf") && clashes.Count > 0)
        {
            var issues = clashes.Take(200).Select((c, k) => new
            {
                title = $"Clash {k + 1}: {c.a.Element.Category?.Name} / {c.b.Element.Category?.Name}",
                description = $"{c.kind} clash between {c.a.Describe()} and {c.b.Describe()}.",
                element_ids = new[] { c.a, c.b }.Where(p => p.InHost).Select(p => p.Element.Id.Value).ToArray(),
            });
            var raw = JsonSerializer.SerializeToElement(new { issues, folder, file_name = $"{doc.Title} - Clashes {DateTime.Now:yyyy-MM-dd HHmm}" });
            var result = new ExportBcfTool().Execute(app, new ToolArgs(raw.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));
            bcf = result.IsError ? "BCF not written: " + result.Text : result.Text;
        }
        return ToolResult.Ok(new
        {
            clashes = clashes.Count, hard = clashes.Count(c => c.kind == "hard"), clearance = clashes.Count(c => c.kind == "clearance"),
            setA = setA.Count, setB = setB.Count, file, bcf,
            byCategories = clashes.GroupBy(c => $"{c.a.Element.Category?.Name} / {c.b.Element.Category?.Name}").Select(g => new { pair = g.Key, count = g.Count() }).OrderByDescending(g => g.count),
            sample = clashes.Take(40).Select(c => new { kind = c.kind, a = c.a.Describe(), aId = c.a.InHost ? c.a.Element.Id.Value : (long?)null, b = c.b.Describe(), bId = c.b.InHost ? c.b.Element.Id.Value : (long?)null, overlap = N(c.depth) }),
            note = truncated ? "Stopped after 200,000 candidate pairs; narrow by level or categories." : clashes.Count >= limit ? $"Stopped at the limit of {limit} clashes." : null,
            geometryFallbacks = solidFailures > 0 ? $"{solidFailures} pairs were judged by their boxes because Revit couldn't intersect their solids." : null,
            meshOnly = meshOnly > 0 ? $"{meshOnly} candidate pairs involved elements with no solids (meshes, e.g. from IFC) and can only clash by clearance." : null,
            voids = clashes.Any(c => c.kind == "void") || voidsRebuilt > 0 || voidProblems.Count > 0 || voidsCapped.Count > 0 ? new
            {
                rebuilt = voidsRebuilt,
                familiesOpened = voidSession.Opens,
                notRebuiltOverCap = voidsCapped.Count > 0
                    ? $"{voidsCapped.Count} void instances were not rebuilt (and not checked) because a run opens at most {VoidSolids.MaxOpens} void families. Narrow the run by level or categories, or run again with voids 'skip' to leave them out."
                    : null,
                meaning = "A 'void' clash is an element running into a void family's void that does not cut it (a void that cuts an element is by design and skipped).",
                notRebuilt = voidProblems.Count > 0 ? voidProblems : null,
            } : null,
            negativeSolidsSkipped = setA.Concat(setB).Distinct().Sum(p => p.NegativeSolids) is var neg and > 0 ? $"{neg} void/inside-out solids (negative volume) were left out of the boolean checks." : null,
            notCheckable = notCheckable.Count > 0 ? notCheckable : null,
        });
    }

    /// <summary>Hosted in, joined to, or the same family nest as each other: overlap there is by design.</summary>
    private static bool Related(Placed a, Placed b)
    {
        if (a.Element.Document != b.Element.Document) return false;
        if (a.Element.Id == b.Element.Id) return true; // an element and its own rebuilt void
        if (a.IsVoid || b.IsVoid)
        {
            var (v, other) = a.IsVoid ? (a, b) : (b, a);
            try { if (InstanceVoidCutUtils.InstanceVoidCutExists(other.Element, v.Element)) return true; }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            // A face-based void hosted on the element cuts it by design too.
            if (v.Element is FamilyInstance hv && hv.Host?.Id == other.Element.Id) return true;
            return false;
        }
        if (a.Element is FamilyInstance fa && (fa.Host?.Id == b.Element.Id || fa.SuperComponent?.Id == b.Element.Id)) return true;
        if (b.Element is FamilyInstance fb && (fb.Host?.Id == a.Element.Id || fb.SuperComponent?.Id == a.Element.Id)) return true;
        try { return JoinGeometryUtils.AreElementsJoined(a.Element.Document, a.Element, b.Element); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { return false; }
    }

    private static (bool hit, double depth, XYZ at, bool failed) Hard(Placed a, Placed b, double minOverlap)
    {
        var minVolume = Math.Pow(minOverlap, 3);
        int tried = 0, failed = 0;
        foreach (var sa in a.Solids())
            foreach (var sb in b.Solids())
            {
                tried++;
                Solid? x;
                try { x = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { failed++; continue; }
                if (x is null || x.Volume < minVolume) continue;
                // An intersection bigger than either solid is a broken boolean (typically void or inside-out geometry): don't trust it.
                if (x.Volume > Math.Min(sa.Volume, sb.Volume) * 1.001 + 1e-9) { failed++; continue; }
                var bb = x.GetBoundingBox();
                var lo = bb.Transform.OfPoint(bb.Min); var hi = bb.Transform.OfPoint(bb.Max);
                var depth = Math.Min(Math.Abs(hi.X - lo.X), Math.Min(Math.Abs(hi.Y - lo.Y), Math.Abs(hi.Z - lo.Z)));
                if (depth < minOverlap) continue;
                return (true, depth, x.ComputeCentroid(), false);
            }
        if (tried == 0 || failed < tried) return (false, 0, XYZ.Zero, false);
        // Revit couldn't intersect any of their solids; the boxes already overlap by more than min_overlap.
        var blo = new XYZ(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z));
        var bhi = new XYZ(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z));
        return (true, Math.Min(bhi.X - blo.X, Math.Min(bhi.Y - blo.Y, bhi.Z - blo.Z)), (blo + bhi) / 2, true);
    }
}

public sealed class CheckDrawingsTool : IRevitTool
{
    public string Name => "check_drawings";
    public string Description =>
        "Look for discrepancies and mistakes across the drawings: grids and levels that differ from the linked models " +
        "(name or position), dimensions whose text was overridden with a different value, duplicate door/window marks, " +
        "sections and callouts not placed on any sheet, empty sheets, and sheets missing title block fields (drawn by, " +
        "checked by, issue date...). Lists each finding with the element or sheet, plus a CSV.";
    public string PropertiesJson => """
    {
      "checks": {"type": "array", "items": {"type": "string", "enum": ["grids_vs_links", "levels_vs_links", "dimension_overrides", "duplicate_marks", "views_not_on_sheets", "empty_sheets", "sheet_fields"]}, "description": "Default all."},
      "sheet_fields": {"type": "array", "items": {"type": "string"}, "description": "Sheet parameters that must be filled (default Drawn By, Checked By, Approved By, Sheet Issue Date)."},
      "tolerance": {"type": "number", "description": "Position tolerance for grids/levels, project units (default 5 mm)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var checks = args.StringList("checks");
        bool On(string c) => checks.Count == 0 || checks.Contains(c);
        var tol = args.OptDouble("tolerance") is { } tv ? RevitHelpers.ToInternal(doc, tv) : 5 / 304.8;
        var findings = new List<(string check, string where, long? id, string issue)>();
        string L(double ft) => $"{Math.Round(RevitHelpers.FromInternal(doc, ft), 1)} {RevitHelpers.LengthUnitLabel(doc)}";
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Where(l => l.GetLinkDocument() != null).ToList();

        if (On("grids_vs_links") && links.Count > 0)
        {
            var mine = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line).ToList();
            foreach (var link in links)
            {
                var tr = link.GetTotalTransform();
                var theirs = new FilteredElementCollector(link.GetLinkDocument()).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line).ToList();
                foreach (var g in theirs)
                {
                    var line = (Line)g.Curve.CreateTransformed(tr);
                    var match = mine.FirstOrDefault(m => string.Equals(m.Name, g.Name, StringComparison.OrdinalIgnoreCase));
                    double Off(Grid m) => Flat(m.Curve as Line)!.Project(Flat(line)!.Origin) is { } r && Parallel(Flat(m.Curve as Line)!, Flat(line)!) ? r.Distance : double.MaxValue;
                    if (match is null)
                    {
                        var near = mine.FirstOrDefault(m => Off(m) < tol);
                        findings.Add(("grids_vs_links", link.Name, near?.Id.Value, near is null
                            ? $"Grid {g.Name} in the link has no grid of that name here."
                            : $"Grid {g.Name} in the link is grid {near.Name} here."));
                    }
                    else if (Off(match) > tol)
                        findings.Add(("grids_vs_links", link.Name, match.Id.Value,
                            Off(match) == double.MaxValue ? $"Grid {g.Name} runs in a different direction from the link's." : $"Grid {g.Name} is {L(Off(match))} away from the link's grid {g.Name}."));
                }
            }
        }
        if (On("levels_vs_links") && links.Count > 0)
        {
            var mine = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            foreach (var link in links)
            {
                var dz = link.GetTotalTransform().Origin.Z;
                foreach (var lv in new FilteredElementCollector(link.GetLinkDocument()).OfClass(typeof(Level)).Cast<Level>())
                {
                    var z = lv.ProjectElevation + dz;
                    var match = mine.FirstOrDefault(m => string.Equals(m.Name, lv.Name, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        if (mine.FirstOrDefault(m => Math.Abs(m.ProjectElevation - z) < tol) is { } same)
                            findings.Add(("levels_vs_links", link.Name, same.Id.Value, $"Level '{lv.Name}' in the link is called '{same.Name}' here."));
                        else if (lv.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() == 1)
                            findings.Add(("levels_vs_links", link.Name, null, $"Level '{lv.Name}' in the link has no level here (by name or height)."));
                    }
                    else if (Math.Abs(match.ProjectElevation - z) > tol)
                        findings.Add(("levels_vs_links", link.Name, match.Id.Value, $"Level '{lv.Name}' is {L(match.ProjectElevation - z)} off the link's."));
                }
            }
        }
        if (On("dimension_overrides"))
        {
            foreach (var d in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                var where = (doc.GetElement(d.OwnerViewId) as View)?.Name ?? "";
                IEnumerable<(string? over, double? value, string? shown)> Parts() => d.NumberOfSegments > 1
                    ? d.Segments.Cast<DimensionSegment>().Select(s => (s.ValueOverride, s.Value, s.ValueString))
                    : [(d.ValueOverride, d.Value, d.ValueString)];
                foreach (var (over, value, shown) in Parts())
                {
                    if (string.IsNullOrWhiteSpace(over) || value is null || shown is null) continue;
                    // Text like 'EQ', 'VARIES' or 'TYP' is a deliberate label, not a wrong number.
                    var number = System.Text.RegularExpressions.Regex.Match(over!, @"-?\d+(\.\d+)?");
                    var actual = System.Text.RegularExpressions.Regex.Match(shown.Replace(",", ""), @"-?\d+(\.\d+)?");
                    if (!number.Success || !actual.Success) continue;
                    var a = double.Parse(number.Value, CultureInfo.InvariantCulture);
                    var m = double.Parse(actual.Value, CultureInfo.InvariantCulture);
                    var differs = Math.Abs(a - m) > Math.Max(0.5, Math.Abs(m) * 0.005);
                    if (differs) findings.Add(("dimension_overrides", where, d.Id.Value, $"Dimension shows '{over}' but measures {shown}."));
                }
            }
        }
        if (On("duplicate_marks"))
        {
            foreach (var bic in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
                foreach (var g in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()
                             .Select(e => (e, mark: e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""))
                             .Where(x => x.mark.Length > 0).GroupBy(x => x.mark).Where(g => g.Count() > 1))
                    findings.Add(("duplicate_marks", g.First().e.Category?.Name ?? "", g.First().e.Id.Value,
                        $"Mark '{g.Key}' is used by {g.Count()} elements: {string.Join(", ", g.Take(8).Select(x => x.e.Id.Value))}."));
        }
        if (On("views_not_on_sheets"))
        {
            var placed = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(v => v.ViewId.Value).ToHashSet();
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                         .Where(v => !v.IsTemplate && v.ViewType is ViewType.Section or ViewType.Detail && !placed.Contains(v.Id.Value)))
                findings.Add(("views_not_on_sheets", v.ViewType.ToString(), v.Id.Value, $"{v.ViewType} '{v.Name}' isn't on any sheet, so its marker has no sheet reference."));
        }
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
        if (On("empty_sheets"))
        {
            var schedulesOn = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Select(s => s.OwnerViewId.Value).ToHashSet();
            foreach (var s in sheets.Where(s => s.GetAllPlacedViews().Count == 0 && !schedulesOn.Contains(s.Id.Value)))
                findings.Add(("empty_sheets", $"{s.SheetNumber} {s.Name}", s.Id.Value, "Sheet has no views or schedules."));
        }
        if (On("sheet_fields"))
        {
            var fields = args.StringList("sheet_fields");
            var builtIns = fields.Count == 0;
            foreach (var s in sheets)
            {
                var missing = new List<string>();
                if (builtIns)
                {
                    foreach (var (bip, label) in new[] { (BuiltInParameter.SHEET_DRAWN_BY, "Drawn By"), (BuiltInParameter.SHEET_CHECKED_BY, "Checked By"),
                                 (BuiltInParameter.SHEET_APPROVED_BY, "Approved By"), (BuiltInParameter.SHEET_ISSUE_DATE, "Sheet Issue Date") })
                    {
                        var v = s.get_Parameter(bip)?.AsString();
                        if (string.IsNullOrWhiteSpace(v) || v is "Author" or "Checker" or "Approver" or "Designer" or "Date") missing.Add(label);
                    }
                }
                else
                    foreach (var f in fields)
                        if (string.IsNullOrWhiteSpace(s.LookupParameter(f) is { } p ? p.AsString() ?? p.AsValueString() : null)) missing.Add(f);
                if (missing.Count > 0) findings.Add(("sheet_fields", $"{s.SheetNumber} {s.Name}", s.Id.Value, $"Missing or placeholder: {string.Join(", ", missing)}."));
            }
        }

        var folder = args.OptString("folder") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{doc.Title} - Drawing check {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        File.WriteAllText(file, "Check,Where,Element id,Issue\n" + string.Join("\n", findings.Select(f => $"{f.check},{Csv(f.where)},{f.id},{Csv(f.issue)}")), new UTF8Encoding(true));
        return ToolResult.Ok(new
        {
            findings = findings.Count, file,
            byCheck = findings.GroupBy(f => f.check).Select(g => new { check = g.Key, count = g.Count() }),
            list = findings.Take(150).Select(f => new { f.check, f.where, f.id, f.issue }),
            linksChecked = links.Select(l => l.Name),
        });
    }

    private static Line? Flat(Line? l) => l is null ? null
        : Line.CreateBound(new XYZ(l.GetEndPoint(0).X, l.GetEndPoint(0).Y, 0), new XYZ(l.GetEndPoint(1).X, l.GetEndPoint(1).Y, 0)) is var f ? Line.CreateUnbound(f.Origin, f.Direction) : null;

    private static bool Parallel(Line a, Line b) => Math.Abs(Math.Abs(a.Direction.DotProduct(b.Direction)) - 1) < 1e-4;
}

public sealed class CheckCodeRulesTool : IRevitTool
{
    public string Name => "check_code_rules";
    public string Description =>
        "Check the model against code and standard rules: each rule names a category, an optional filter, and a value to " +
        "check (a parameter, or a measure: stair riser, stair tread, door width, room area, room height) against min/max/" +
        "equals/not empty. Rules come from the request, a JSON rule file, or the example pack 'example_residential' " +
        "(typical values to replace with your code's). Lists passes and failures and writes a CSV; show failures " +
        "with select_elements or set_view_graphics on the failing ids. This is a rules check, not code certification.";
    public string PropertiesJson => """
    {
      "rules": {"type": "array", "items": {"type": "object", "properties": {
        "name": {"type": "string"}, "reference": {"type": "string", "description": "Code clause, e.g. 'IBC 1011.5.2'."},
        "category": {"type": "string"},
        "where": {"type": "object", "properties": {"parameter": {"type": "string"}, "contains": {"type": "string"}}, "description": "Only elements whose parameter contains this text (e.g. room Name contains 'Corridor')."},
        "measure": {"type": "string", "enum": ["riser", "tread", "door_width", "room_area", "room_height"]},
        "parameter": {"type": "string", "description": "Or a parameter name to check."},
        "unit": {"type": "string", "enum": ["mm", "m", "m2", "deg", "number", "text"], "description": "Unit of min/max (default mm for lengths, m2 for areas)."},
        "min": {"type": "number"}, "max": {"type": "number"}, "equals": {"type": "string"}, "not_empty": {"type": "boolean"}
      }, "required": ["name", "category"]}},
      "rule_file": {"type": "string", "description": "JSON file with a 'rules' array in the same form."},
      "pack": {"type": "string", "enum": ["example_residential"]},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    private const string ExamplePack = """
    [
      {"name": "Stair riser height", "category": "Stairs", "measure": "riser", "max": 180, "reference": "example value"},
      {"name": "Stair tread depth", "category": "Stairs", "measure": "tread", "min": 280, "reference": "example value"},
      {"name": "Door width", "category": "Doors", "measure": "door_width", "min": 850, "reference": "example value"},
      {"name": "Bedroom area", "category": "Rooms", "where": {"parameter": "Name", "contains": "Bed"}, "measure": "room_area", "min": 9, "reference": "example value"},
      {"name": "Room height", "category": "Rooms", "measure": "room_height", "min": 2400, "reference": "example value"},
      {"name": "Fire rating on walls marked fire", "category": "Walls", "where": {"parameter": "Type Name", "contains": "Fire"}, "parameter": "Fire Rating", "not_empty": true, "reference": "example value"}
    ]
    """;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var rules = new List<JsonElement>();
        rules.AddRange(args.Array("rules"));
        if (args.OptString("rule_file") is { } path)
        {
            using var file = JsonDocument.Parse(File.ReadAllText(path));
            var root = file.RootElement.ValueKind == JsonValueKind.Array ? file.RootElement : file.RootElement.GetProperty("rules");
            rules.AddRange(root.EnumerateArray().Select(r => r.Clone()));
        }
        if (args.OptString("pack") == "example_residential")
            rules.AddRange(JsonDocument.Parse(ExamplePack).RootElement.EnumerateArray().Select(r => r.Clone()));
        if (rules.Count == 0) return ToolResult.Error("Give rules, rule_file or pack.");

        var results = new List<object>();
        var rows = new StringBuilder("Rule,Reference,Element id,Element,Value,Result,Requirement\n");
        string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var failing = new HashSet<long>();
        foreach (var rule in rules)
        {
            var name = rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("name", out var nm) ? nm.ToString() : "rule";
            var reference = rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("reference", out var rf) ? rf.ToString() : "";
            try
            {
                var bic = RevitHelpers.ParseCategory(doc, rule.GetProperty("category").GetString() ?? "");
                var elements = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();
                if (rule.TryGetProperty("where", out var where) && where.TryGetProperty("parameter", out var wp) && where.TryGetProperty("contains", out var wc))
                    elements = elements.Where(e => (Text(doc, e, wp.GetString() ?? "") ?? "").IndexOf(wc.GetString() ?? "", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (bic == BuiltInCategory.OST_Rooms) elements = elements.Where(e => e is Room { Area: > 0 }).ToList();
                var unit = rule.TryGetProperty("unit", out var u) ? u.GetString() : null;
                double? min = rule.TryGetProperty("min", out var mn) ? mn.GetDouble() : null;
                double? max = rule.TryGetProperty("max", out var mx) ? mx.GetDouble() : null;
                var equals = rule.TryGetProperty("equals", out var eq) ? eq.ToString() : null;
                var notEmpty = rule.TryGetProperty("not_empty", out var ne) && ne.GetBoolean();
                var requirement = string.Join(" ", new[] { min is { } a ? $">= {a}" : null, max is { } b ? $"<= {b}" : null, equals is { } c ? $"= {c}" : null, notEmpty ? "filled" : null }.Where(x => x != null));
                int pass = 0, fail = 0, unknown = 0;
                var fails = new List<object>();
                foreach (var e in elements)
                {
                    var (number, text) = Value(doc, e, rule, unit);
                    bool? ok = null;
                    if (notEmpty) ok = !string.IsNullOrWhiteSpace(text);
                    if (equals != null) ok = (ok ?? true) && string.Equals(text?.Trim(), equals.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (min != null || max != null)
                        ok = number is { } n ? (ok ?? true) && (min is null || n >= min - 1e-6) && (max is null || n <= max + 1e-6) : null;
                    var shown = number is { } nv ? Math.Round(nv, 2).ToString(CultureInfo.InvariantCulture) : text ?? "";
                    if (ok is null) { unknown++; continue; }
                    if (ok == true) pass++;
                    else
                    {
                        fail++; failing.Add(e.Id.Value);
                        if (fails.Count < 25) fails.Add(new { id = e.Id.Value, element = e.Name, value = shown });
                    }
                    rows.AppendLine(string.Join(",", Csv(name), Csv(reference), e.Id.Value, Csv(e.Name), Csv(shown), ok == true ? "pass" : "FAIL", Csv(requirement)));
                }
                results.Add(new { rule = name, reference, requirement, checkedCount = pass + fail, pass, fail, couldNotRead = unknown, fails });
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                results.Add(new { rule = name, error = ex.Message });
            }
        }

        var folder = args.OptString("folder") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var csv = Path.Combine(folder, $"{doc.Title} - Rules check {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        File.WriteAllText(csv, rows.ToString(), new UTF8Encoding(true));
        return ToolResult.Ok(new { rules = results, failingElements = failing.Count, failingIds = failing.Take(500), file = csv, note = "A rules check against the values given, not code certification." });
    }

    /// <summary>A parameter's text, looked up on the instance then its type ('Type Name' and 'Family' work too).</summary>
    private static string? Text(Document doc, Element e, string name)
    {
        if (name.Equals("Type Name", StringComparison.OrdinalIgnoreCase)) return doc.GetElement(e.GetTypeId())?.Name;
        if (name.Equals("Name", StringComparison.OrdinalIgnoreCase) && e is Room room) return room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
        var p = e.LookupParameter(name) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(name);
        return p is null ? null : p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
    }

    private static (double? number, string? text) Value(Document doc, Element e, JsonElement rule, string? unit)
    {
        double Mm(double ft) => ft * 304.8;
        if (rule.TryGetProperty("measure", out var m))
        {
            switch (m.GetString())
            {
                case "riser" when e is Stairs s: return (Mm(s.ActualRiserHeight), null);
                case "tread" when e is Stairs s: return (Mm(s.ActualTreadDepth), null);
                case "door_width":
                    var w = e.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? doc.GetElement(e.GetTypeId())?.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                            ?? doc.GetElement(e.GetTypeId())?.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
                    return (w is null ? null : Mm(w.AsDouble()) / (unit == "m" ? 1000 : 1), null);
                case "room_area" when e is Room r: return (UnitUtils.ConvertFromInternalUnits(r.Area, UnitTypeId.SquareMeters), null);
                case "room_height" when e is Room r: return (Mm(r.UnboundedHeight) / (unit == "m" ? 1000 : 1), null);
                default: return (null, null);
            }
        }
        var name = rule.TryGetProperty("parameter", out var pn) ? pn.GetString() ?? "" : "";
        var p = e.LookupParameter(name) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(name);
        if (p is null) return name.Equals("Type Name", StringComparison.OrdinalIgnoreCase) ? (null, doc.GetElement(e.GetTypeId())?.Name) : (null, null);
        if (p.StorageType != StorageType.Double) return (p.StorageType == StorageType.Integer ? p.AsInteger() : null, p.StorageType == StorageType.String ? p.AsString() : p.AsValueString());
        var spec = p.Definition.GetDataType();
        var raw = p.AsDouble();
        double value =
            spec == SpecTypeId.Length ? UnitUtils.ConvertFromInternalUnits(raw, unit == "m" ? UnitTypeId.Meters : UnitTypeId.Millimeters)
            : spec == SpecTypeId.Area ? UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.SquareMeters)
            : spec == SpecTypeId.Angle ? UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Degrees)
            : raw;
        return (value, p.AsValueString());
    }
}
