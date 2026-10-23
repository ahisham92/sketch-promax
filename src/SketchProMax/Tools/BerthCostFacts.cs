using System.Globalization;
using Autodesk.Revit.DB;
using static SketchProMax.Tools.BerthCostMath;
using Line = SketchProMax.Tools.BerthCostMath.Line;

namespace SketchProMax.Tools;

/// <summary>
/// Measures the berths of the open model for the berth costing (read only, nothing changes): which elements are the
/// berth's (its parameter value or scope box, less the scope boxes it leaves out), then each item's rule over them, as
/// quantities and the lines of the back-up tables (levels as Revit shows them, in metres). It runs in short steps
/// (<see cref="Reading.RunFor"/>) so the window gives Revit back between them and a big model never freezes.
/// </summary>
internal static class BerthCostFacts
{
    private const double M = 0.3048, M2 = M * M, M3 = M * M * M;

    /// <summary>An element a rule may count, read once.</summary>
    private sealed class El
    {
        public Element E = null!;
        public long Id;
        public string Category = "", Type = "", Family = "";
        public ElementType? T;
        public double X, Y, MinX, MinY, MaxX, MaxY, Top, Bottom; // feet; Top/Bottom as Revit shows them (feet from the base)
    }

    public sealed class Reading
    {
        private readonly Document _doc;
        private readonly Queue<(string Name, Func<bool> Step)> _steps = new();
        internal Reading(Document doc) => _doc = doc;

        /// <summary>Per berth key: what was measured.</summary>
        public Dictionary<string, Measurement> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Per berth key: a design parameter read from the model (pile diameters), for berths without one.</summary>
        public Dictionary<string, Dictionary<string, double>> Params { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Problems { get; } = [];
        public int Total { get; private set; }
        public int Left => _steps.Count;
        public string NextName => _steps.Count > 0 ? _steps.Peek().Name : "";

        internal void Add(string name, Func<bool> step) { _steps.Enqueue((name, step)); Total++; }
        internal void Once(string name, Action step) => Add(name, () => { step(); return false; });

        public bool Next()
        {
            if (_steps.Count == 0) return false;
            if (!_doc.IsValidObject) { _steps.Clear(); Problems.Add("The model was closed."); return false; }
            var (name, step) = _steps.Peek();
            bool more;
            try { more = step(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Problems.Add($"{name}: {ex.Message}"); more = false; }
            if (!more) _steps.Dequeue();
            return true;
        }

        /// <summary>Runs steps for about <paramref name="budget"/>, at least one. False when done.</summary>
        public bool RunFor(TimeSpan budget)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do { if (!Next()) return false; } while (clock.Elapsed < budget);
            return _steps.Count > 0;
        }
    }

    /// <summary>Measures in one call (field test and the chat tool; the window uses <see cref="Plan"/> and steps).</summary>
    public static Reading Read(Document doc, Project p, IReadOnlyList<Berth> berths)
    {
        var r = Plan(doc, p, berths);
        while (r.Next()) { }
        return r;
    }

    /// <summary>
    /// The measuring of <paramref name="berths"/> in <paramref name="doc"/>, as steps. Revit API context for this call
    /// and each step. Rules that can't be read are said in Problems and skipped.
    /// </summary>
    public static Reading Plan(Document doc, Project p, IReadOnlyList<Berth> berths)
    {
        var reading = new Reading(doc);
        var rules = new Dictionary<int, List<Rule>>();
        foreach (var item in p.Items.Where(i => i.Rule.Trim().Length > 0))
        {
            try { rules[item.No] = ParseRule(item.Rule); }
            catch (FormatException ex) { reading.Problems.Add($"Item {item.No} {item.Name}: {ex.Message}"); }
        }
        var cats = new HashSet<string>(rules.Values.SelectMany(l => l).SelectMany(r => r.Categories), StringComparer.OrdinalIgnoreCase);
        var baseZ = Elevations.BaseZ(doc);
        var all = new List<El>();

        // 1. The elements of the categories the rules use, a slice at a time.
        IEnumerator<Element>? walk = null;
        reading.Add("Reading the model's elements", () =>
        {
            walk ??= new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent().GetEnumerator();
            for (var n = 0; n < 2000; n++)
            {
                if (!walk.MoveNext()) return false;
                var e = walk.Current;
                if (e.Category is not { } cat || !cats.Contains(cat.Name)) continue;
                var box = e.get_BoundingBox(null);
                if (box is null) continue;
                var type = doc.GetElement(e.GetTypeId()) as ElementType;
                all.Add(new El
                {
                    E = e, Id = e.Id.Value, Category = cat.Name, T = type, Type = type?.Name ?? "", Family = type?.FamilyName ?? "",
                    X = (box.Min.X + box.Max.X) / 2, Y = (box.Min.Y + box.Max.Y) / 2, MinX = box.Min.X, MinY = box.Min.Y, MaxX = box.Max.X, MaxY = box.Max.Y,
                    Top = box.Max.Z - baseZ, Bottom = box.Min.Z - baseZ,
                });
            }
            return true;
        });

        // 2. Each berth: its elements, then each item's rules over them.
        foreach (var b in berths)
        {
            List<El> mine = [];
            var m = new Measurement { At = DateTime.Now, Model = doc.Title };
            reading.Once($"{b.Title}: finding its elements", () =>
            {
                var member = Membership(doc, b, out var why);
                if (member is null) { reading.Problems.Add($"{b.Title}: {why}"); return; }
                var leave = Shapes(doc, b.Exclude, out var missing);
                if (missing.Count > 0) reading.Problems.Add($"{b.Title}: no scope box named \"{string.Join("\", \"", missing)}\" to leave out.");
                mine = all.Where(e => member(e) && !leave.Any(s => BerthStatusFacts.Inside(s, e.X, e.Y))).ToList();
                reading.Results[b.Key] = m;
                if (mine.Count == 0) reading.Problems.Add($"{b.Title}: no elements found ({(b.FindBy == BerthStatusMath.FindBy.ScopeBox ? "scope box " : b.Parameter + " = ")}{b.Value}).");
            });
            foreach (var item in p.Items.Where(i => rules.ContainsKey(i.No)))
                reading.Once($"{b.Title}: {item.Name}", () =>
                {
                    if (!reading.Results.ContainsKey(b.Key)) return;
                    Measure(p, item, rules[item.No], mine, m);
                });
            reading.Once($"{b.Title}: pile sizes", () =>
            {
                if (!reading.Results.ContainsKey(b.Key)) return;
                var found = new Dictionary<string, double>(StringComparer.Ordinal);
                void Size(int no, string key)
                {
                    var lines = m.Lines.Where(l => l.No == no && l.Diameter > 0).ToList();
                    if (lines.Count > 0) found[key] = lines.GroupBy(l => Math.Round(l.Diameter, 3)).OrderByDescending(g => g.Sum(l => l.Count)).First().Key;
                }
                Size(5, "MiddleD");
                Size(6, "RearD");
                reading.Params[b.Key] = found;
            });
        }
        return reading;
    }

    /// <summary>Puts a reading into the project: each berth's measurement, and pile sizes for berths that have none set.</summary>
    public static void Apply(Project p, Reading r)
    {
        foreach (var result in r.Results)
            if (p.Berths.TryGetValue(result.Key, out var b))
            {
                b.Measured = result.Value;
                if (r.Params.TryGetValue(result.Key, out var found))
                    foreach (var kv in found) if (!b.Params.ContainsKey(kv.Key)) b.Params[kv.Key] = kv.Value;
            }
    }

    // ---- Membership ------------------------------------------------------------------------------------------------------

    private static Func<El, bool>? Membership(Document doc, Berth b, out string why)
    {
        why = "";
        var value = b.Value.Trim();
        switch (b.FindBy)
        {
            case BerthStatusMath.FindBy.ScopeBox:
                var shapes = Shapes(doc, value, out var missing);
                if (missing.Count > 0 || shapes.Count == 0) { why = $"no scope box named \"{string.Join("\", \"", missing.DefaultIfEmpty(value))}\" in this model."; return null; }
                return e => shapes.Any(s => BerthStatusFacts.Inside(s, e.X, e.Y));
            case BerthStatusMath.FindBy.Parameter:
                if (b.Parameter.Trim().Length == 0) { why = "set the parameter that names the berth (Berth status setup, Berths sheet)."; return null; }
                var values = BerthStatusMath.Values(value);
                var name = b.Parameter.Trim();
                return e => BerthStatusMath.ValueMatches(Text(e, name), values);
            case BerthStatusMath.FindBy.Workset:
                if (value.Length == 0) { why = "set the text in its workset name."; return null; }
                var worksets = doc.IsWorkshared ? doc.GetWorksetTable() : null;
                return e => worksets is not null && (worksets.GetWorkset(e.E.WorksetId)?.Name ?? "").IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
            default:
                return _ => true;
        }
    }

    /// <summary>The plan outlines of the scope boxes named in <paramref name="names"/> ("A; B"), and the names not found.</summary>
    private static List<List<(double X, double Y)>> Shapes(Document doc, string names, out List<string> missing)
    {
        missing = [];
        var list = new List<List<(double X, double Y)>>();
        var wanted = BerthStatusMath.Values(names);
        if (wanted.Count == 0) return list;
        var boxes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().ToList();
        foreach (var n in wanted)
        {
            var box = boxes.FirstOrDefault(x => string.Equals(x.Name.Trim(), n, StringComparison.OrdinalIgnoreCase));
            if (box is null) { missing.Add(n); continue; }
            var points = new List<(double X, double Y)>();
            if (box.get_Geometry(new Options()) is { } geo)
                foreach (var g in geo)
                    if (g is Curve c) { points.Add((c.GetEndPoint(0).X, c.GetEndPoint(0).Y)); points.Add((c.GetEndPoint(1).X, c.GetEndPoint(1).Y)); }
            if (points.Count == 0 && box.get_BoundingBox(null) is { } bb) points.AddRange([(bb.Min.X, bb.Min.Y), (bb.Max.X, bb.Min.Y), (bb.Max.X, bb.Max.Y), (bb.Min.X, bb.Max.Y)]);
            var hull = BerthStatusFacts.Hull(points);
            if (hull.Count >= 3) list.Add(hull); else missing.Add(n);
        }
        return list;
    }

    // ---- Reading values --------------------------------------------------------------------------------------------------

    private static Parameter? Param(El e, string name) => e.E.LookupParameter(name) ?? e.T?.LookupParameter(name);

    private static string Text(El e, string name)
    {
        if (name == "@type") return e.Type;
        if (name == "@family") return e.Family;
        var p = Param(e, name);
        if (p is null || !p.HasValue) return "";
        return p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
    }

    /// <summary>A length parameter in metres (the first of the names with a value), or 0.</summary>
    private static double Length(El e, params string[] names)
    {
        foreach (var n in names)
            if (Param(e, n) is { StorageType: StorageType.Double, HasValue: true } p && p.AsDouble() > 0) return p.AsDouble() * M;
        return 0;
    }

    private static double Builtin(Element e, BuiltInParameter bip) =>
        e.get_Parameter(bip) is { StorageType: StorageType.Double, HasValue: true } p ? p.AsDouble() : 0;

    /// <summary>A floor's thickness (m): its own, else its type's.</summary>
    private static double Thickness(El e)
    {
        var t = Builtin(e.E, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
        if (t <= 0 && e.T is not null) t = Builtin(e.T, BuiltInParameter.FLOOR_ATTR_DEFAULT_THICKNESS_PARAM);
        return t * M;
    }

    /// <summary>The element's solid volume (m³): Revit's computed volume, else its solids.</summary>
    private static double Volume(El e)
    {
        var v = Builtin(e.E, BuiltInParameter.HOST_VOLUME_COMPUTED);
        if (v > 0) return v * M3;
        try
        {
            if (e.E.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }) is { } geo)
                return Solids(geo).Sum(s => s.Volume) * M3;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        return 0;
    }

    private static IEnumerable<Solid> Solids(GeometryElement geo)
    {
        foreach (var g in geo)
        {
            if (g is Solid s && s.Volume > 0) yield return s;
            else if (g is GeometryInstance gi && gi.GetInstanceGeometry() is { } inner)
                foreach (var x in Solids(inner)) yield return x;
        }
    }

    // ---- Measuring an item -----------------------------------------------------------------------------------------------

    /// <summary>One element as measured: its row values before grouping.</summary>
    private sealed record One(string Label, double Top, double Bottom, double D, double Inner, double Area, double Thick, double Volume, double Weight, double Length, long Id);

    private static void Measure(Project p, Item item, List<Rule> rules, List<El> mine, Measurement m)
    {
        double? total = null;
        var lines = new List<Line>();
        var ids = new List<long>();
        foreach (var r in rules)
        {
            var hits = mine.Where(e => r.Categories.Any(c => string.Equals(c, e.Category, StringComparison.OrdinalIgnoreCase))
                && r.Filters.All(f => Like(Text(e, f.Name), f.Pattern) != f.Not)).ToList();
            if (r.Thick.Count > 0) hits = hits.Where(e => Thickness(e) is var t && r.Thick.Any(mm => Math.Abs(t * 1000 - mm) < 1.5)).ToList();
            if (r.Over is { } over)
            {
                var under = mine.Where(e => Like(Text(e, over.Name), over.Pattern)).ToList();
                hits = hits.Where(f => under.Any(u => u.X >= f.MinX && u.X <= f.MaxX && u.Y >= f.MinY && u.Y <= f.MaxY)).ToList();
            }
            if (hits.Count == 0) continue;
            var ones = hits.Select(e => Read1(p, r, e)).ToList();
            var q = r.Measure switch
            {
                "count" => ones.Count,
                "length" => ones.Sum(o => o.Length),
                "volume" => ones.Sum(o => o.Volume),
                "area" => ones.Sum(o => o.Area),
                _ => ones.Sum(o => o.Weight),
            };
            total = (total ?? 0) + Finish(r, q);
            ids.AddRange(ones.Select(o => o.Id));
            if (r.Table.Length > 0 && r.Table != "none") lines.AddRange(Group(item, r, ones));
        }
        NumberGroups(lines);
        if (total is { } t) m.Quantities[item.No] = Math.Round(t, 3);
        else m.Notes[item.No] = "nothing matched its rule";
        m.Lines.RemoveAll(l => l.No == item.No);
        m.Lines.AddRange(lines);
        if (ids.Count > 0) m.Ids[item.No] = ids;
    }

    private static One Read1(Project p, Rule r, El e)
    {
        var top = Math.Round(e.Top * M, 3);
        var bottom = Math.Round(e.Bottom * M, 3);
        var outer = Length(e, "OD", "Outer Diameter", "b", "d", "Diameter", "D");
        var inner = Length(e, "ID", "Inner Diameter");
        var thick = e.Category.Equals("Floors", StringComparison.OrdinalIgnoreCase) ? Thickness(e) : 0;
        double area, volume;
        if (thick > 0)
        {
            area = Builtin(e.E, BuiltInParameter.HOST_AREA_COMPUTED) * M2;
            volume = Volume(e);
            if (volume <= 0) volume = area * thick;
        }
        else
        {
            area = outer > 0 ? Math.PI / 4 * (outer * outer - (inner > 0 && inner < outer ? inner * inner : 0)) : 0;
            volume = area > 0 ? area * (top - bottom) : Volume(e);
        }
        var label = r.Label.Length > 0 ? r.Label
            : r.Group.Equals("thickness", StringComparison.OrdinalIgnoreCase) ? ThickLabel(thick)
            : r.Group.Equals("number", StringComparison.OrdinalIgnoreCase) ? "#"
            : r.Group.Equals("type", StringComparison.OrdinalIgnoreCase) || r.Group.Length == 0 ? e.Type
            : Text(e, r.Group) is { Length: > 0 } g ? g : e.Type;
        var density = r.Density > 0 ? r.Density : p.SteelDensity;
        return new One(label, top, bottom, outer, inner, area, thick, volume, volume * density, top - bottom, e.Id);
    }

    /// <summary>The table lines of a rule's elements: one per label and size (slabs per label and thickness).</summary>
    private static IEnumerable<Line> Group(Item item, Rule r, List<One> ones)
    {
        if (r.Table == "slab")
            return ones.GroupBy(o => (o.Label, Math.Round(o.Thick, 3))).Select(g => new Line
            {
                No = item.No, Label = g.Key.Label, Count = g.Count(), Thickness = g.Key.Item2, Area = g.Sum(o => o.Area), Volume = g.Sum(o => o.Volume),
                Quantity = g.Sum(o => o.Volume), Table = r.Table,
            });
        return ones.GroupBy(o => (o.Label, Math.Round(o.D, 3), Math.Round(o.Inner, 3), Math.Round(o.Top, 2), Math.Round(o.Bottom, 2)))
            .OrderBy(g => g.Key.Label, StringComparer.OrdinalIgnoreCase).ThenByDescending(g => g.Key.Item4)
            .Select(g => new Line
            {
                No = item.No, Label = g.Key.Label, Count = g.Count(), Top = g.Key.Item4, Bottom = g.Key.Item5, Height = Math.Round(g.Key.Item4 - g.Key.Item5, 3),
                Diameter = g.Key.Item2, Inner = g.Key.Item3, Area = g.First().Area, Volume = g.Sum(o => o.Volume), Weight = g.Sum(o => o.Weight),
                Quantity = r.Measure switch { "length" => g.Sum(o => o.Length), "volume" => g.Sum(o => o.Volume), "count" => g.Count(), "area" => g.Sum(o => o.Area), _ => g.Sum(o => o.Weight) },
                Table = r.Table,
            });
    }
}
