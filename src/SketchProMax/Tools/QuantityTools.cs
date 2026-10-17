using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Measuring the model for BOQ items: which elements an item covers (by category, type, level, material or the words
/// of its description) and each element's quantity in the item's unit (m3, m2, m, nr, kg, t), with its working.
/// </summary>
internal static class Measure
{
    public sealed class Item
    {
        public string Ref = "", Description = "", Unit = "", Category = "", Type = "", Level = "", Material = "";
        public double? Quantity, Rate;
    }

    public sealed record Line(Element E, double Qty, string Working, double Gross, double Deducted);

    /// <summary>Void thresholds (m3) below which openings are not deducted, by method. Typical values; confirm against the method used.</summary>
    public static readonly Dictionary<string, double> VoidThreshold = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NRM2"] = 0.05, ["POMI"] = 0.05, ["CESMM4"] = 0.1, ["net"] = 0,
    };

    private const string RebarWords = @"\breinforcement\b|\brebars?\b|\bbars?\b|\bmesh\b|\bfabric\b";

    /// <summary>Category words, most specific first; the filter narrows elements within the categories.</summary>
    private static readonly (string pattern, BuiltInCategory[] cats, Func<Element, bool>? keep)[] Words =
    [
        (@"\bpile ?caps?\b", [BuiltInCategory.OST_StructuralFoundation], e => !IsPile(e) || TypeName(e).IndexOf("cap", StringComparison.OrdinalIgnoreCase) >= 0),
        (@"\bpiles?\b|\bpiling\b", [BuiltInCategory.OST_StructuralFoundation], e => IsPile(e) && TypeName(e).IndexOf("cap", StringComparison.OrdinalIgnoreCase) < 0),
        (@"\braft|\bmat foundation", [BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Floors], e => e is not Floor f || f.FloorType.IsFoundationSlab || TypeName(e).IndexOf("raft", StringComparison.OrdinalIgnoreCase) >= 0),
        (@"\bfootings?\b|\bfoundations?\b|\bpad (base|footing)s?\b|\bstrip\b|\bbases?\b", [BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Floors], e => (e is not Floor f || f.FloorType.IsFoundationSlab) && !IsPile(e)),
        (@"\bcolumns?\b|\bpiers?\b", [BuiltInCategory.OST_StructuralColumns], null),
        (@"\bbeams?\b|\blintels?\b|\bgirders?\b|\bjoists?\b|\bframing\b", [BuiltInCategory.OST_StructuralFraming], null),
        (@"\bwalls?\b|\bcores?\b|\bblockwork\b|\bmasonry\b", [BuiltInCategory.OST_Walls], null),
        (@"\bslabs?\b|\bfloors?\b|\btopping\b|\bdecks?\b|\bscreed\b", [BuiltInCategory.OST_Floors], e => e is not Floor f || !f.FloorType.IsFoundationSlab),
        (@"\bstairs?\b|\bflights?\b|\blandings?\b", [BuiltInCategory.OST_Stairs], null),
        (@"\bdoors?\b", [BuiltInCategory.OST_Doors], null),
        (@"\bwindows?\b", [BuiltInCategory.OST_Windows], null),
    ];

    private static bool IsPile(Element e) => TypeName(e).IndexOf("pile", StringComparison.OrdinalIgnoreCase) >= 0
                                             || (e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith("pile", StringComparison.OrdinalIgnoreCase);

    public static string Unit(Item item)
    {
        var u = Regex.Replace(item.Unit.Trim().ToLowerInvariant().Replace("³", "3").Replace("²", "2"), @"[.\s]", "");
        return u switch
        {
            "m3" or "cum" or "cbm" => "m3",
            "m2" or "sqm" => "m2",
            "m" or "lm" or "rm" or "linm" or "lnm" or "mtr" => "m",
            "nr" or "no" or "nos" or "ea" or "each" or "pcs" or "pc" or "set" => "nr",
            "kg" or "kgs" => "kg",
            "t" or "mt" or "ton" or "tons" or "tonne" or "tonnes" => "t",
            "" => Regex.IsMatch(item.Description, RebarWords + @"|\bsteel\b", RegexOptions.IgnoreCase) ? "t" : "m3",
            _ => throw new ArgumentException($"Item '{item.Ref}': unit '{item.Unit}' isn't a measured unit (m3, m2, m, nr, kg, t); lump sums can't be measured from the model."),
        };
    }

    public static bool IsRebar(Item item) => item.Category.Length == 0 ? Regex.IsMatch(item.Description, RebarWords, RegexOptions.IgnoreCase)
        : Regex.IsMatch(item.Category, "rebar|reinforcement", RegexOptions.IgnoreCase);

    private static (BuiltInCategory[] cats, Func<Element, bool>? keep)? FromWords(string text)
    {
        foreach (var w in Words) if (Regex.IsMatch(text, w.pattern, RegexOptions.IgnoreCase)) return (w.cats, w.keep);
        return null;
    }

    /// <summary>The elements the item covers.</summary>
    public static List<Element> Elements(Document doc, Item item)
    {
        IEnumerable<Element> found;
        if (IsRebar(item))
        {
            found = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Element>().Concat(new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)));
            // "Reinforcement to slabs": only bars in hosts of that kind.
            if (FromWords(Regex.Replace(item.Description, RebarWords, "", RegexOptions.IgnoreCase)) is { } host)
                found = found.Where(e => (e is Rebar r ? doc.GetElement(r.GetHostId()) : e is RebarInSystem ris ? doc.GetElement(ris.GetHostId()) : null) is { } h
                                         && host.cats.Any(c => h.Category?.Id.Value == (long)c) && (host.keep is null || host.keep(h)));
        }
        else
        {
            Func<Element, bool>? keep = null;
            BuiltInCategory[] cats;
            if (item.Category.Length > 0)
                cats = item.Category.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(c => RevitHelpers.ParseCategory(doc, c.Trim())).ToArray();
            else
            {
                var w = FromWords(item.Description) ?? throw new ArgumentException($"Item '{item.Ref} {item.Description}': give its category (no category word in the description).");
                cats = w.cats; keep = w.keep;
            }
            found = cats.SelectMany(c => new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType());
            if (keep != null) found = found.Where(keep);
            // Material words in the description narrow it when no material column is given.
            if (item.Material.Length == 0)
            {
                if (Regex.IsMatch(item.Description, @"\bconcrete\b|\bRC\b", RegexOptions.IgnoreCase)) found = found.Where(e => e.Category?.Id.Value is (long)BuiltInCategory.OST_Doors or (long)BuiltInCategory.OST_Windows || MaterialName(doc, e).IndexOf("concrete", StringComparison.OrdinalIgnoreCase) >= 0);
                else if (Regex.IsMatch(item.Description, @"\bsteel\b", RegexOptions.IgnoreCase)) found = found.Where(e => MaterialName(doc, e).IndexOf("steel", StringComparison.OrdinalIgnoreCase) >= 0);
                else if (Regex.IsMatch(item.Description, @"\bblockwork\b|\bmasonry\b|\bbrick", RegexOptions.IgnoreCase)) found = found.Where(e => Regex.IsMatch(MaterialName(doc, e) + " " + TypeName(e), "block|masonry|brick|cmu", RegexOptions.IgnoreCase));
            }
        }
        if (item.Type.Length > 0) found = found.Where(e => Like(TypeName(e), item.Type));
        if (item.Level.Length > 0) found = found.Where(e => Like(LevelName(doc, e), item.Level, exact: true));
        if (item.Material.Length > 0) found = found.Where(e => Like(MaterialName(doc, e), item.Material));
        return found.Where(e => e is not Wall { IsStackedWallMember: true }).ToList();
    }

    private static bool Like(string value, string pattern, bool exact = false) =>
        pattern.Contains('*') ? Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase)
            : exact ? value.Trim().Equals(pattern.Trim(), StringComparison.OrdinalIgnoreCase) : value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;

    public static string TypeName(Element e) => (e.Document.GetElement(e.GetTypeId()) as ElementType)?.Name ?? e.Name;

    public static string LevelName(Document doc, Element e) =>
        (doc.GetElement(e.LevelId) as Level)?.Name ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)?.AsValueString()
        ?? e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsValueString() ?? (e is Rebar rb && doc.GetElement(rb.GetHostId()) is { } h1 ? LevelName(doc, h1) : e is RebarInSystem ris && doc.GetElement(ris.GetHostId()) is { } h2 ? LevelName(doc, h2) : "");

    public static string MaterialName(Document doc, Element e)
    {
        if (e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsElementId() is { } mid && doc.GetElement(mid) is Material m) return m.Name;
        var type = doc.GetElement(e.GetTypeId()) as HostObjAttributes;
        if (type?.GetCompoundStructure() is { } cs && cs.StructuralMaterialIndex >= 0 && doc.GetElement(cs.GetMaterialId(cs.StructuralMaterialIndex)) is Material sm) return sm.Name;
        return e.GetMaterialIds(false).Select(id => (doc.GetElement(id) as Material)?.Name).FirstOrDefault(n => n != null) ?? "";
    }

    private static double D(Element e, BuiltInParameter bip) => e.get_Parameter(bip) is { StorageType: StorageType.Double } p ? p.AsDouble() : 0;

    private static double? Size(Element e, params string[] names)
    {
        var type = e.Document.GetElement(e.GetTypeId());
        foreach (var n in names)
            if ((e.LookupParameter(n) ?? type?.LookupParameter(n)) is { StorageType: StorageType.Double } p && p.AsDouble() > 0) return p.AsDouble();
        return null;
    }

    private static string F(double v, int dp = 3) => v.ToString("0." + new string('0', dp), CultureInfo.InvariantCulture);

    /// <summary>One element's quantity in the unit, with the working shown, gross and deducted (voids) in that unit.</summary>
    public static Line Of(Document doc, Element e, string unit, double voidThreshold)
    {
        const double M = 0.3048, M2 = M * M, M3 = M * M * M;
        // Framing: the cut length (between supports) so beams don't run into columns; others: their curve length.
        double len = D(e, BuiltInParameter.INSTANCE_LENGTH_PARAM) is > 0 and var cut && e.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming
            ? cut : Math.Max(D(e, BuiltInParameter.CURVE_ELEM_LENGTH), D(e, BuiltInParameter.INSTANCE_LENGTH_PARAM));
        if (len == 0 && e.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns && e.get_BoundingBox(null) is { } cb) len = cb.Max.Z - cb.Min.Z;
        switch (unit)
        {
            case "nr":
                return new Line(e, 1, "1 nr", 1, 0);
            case "kg":
            case "t":
            {
                double kg; string working;
                if (e is Rebar or RebarInSystem)
                {
                    var bar = doc.GetElement(e.GetTypeId()) as RebarBarType;
                    var dmm = (bar?.BarNominalDiameter ?? 0) * 304.8;
                    var total = D(e, BuiltInParameter.REBAR_ELEM_TOTAL_LENGTH) * M;
                    kg = total * dmm * dmm / 162.2;
                    working = $"total bar length {F(total, 2)} m × {F(dmm, 0)}²/162.2 kg/m = {F(kg, 1)} kg";
                }
                else
                {
                    var vol = Math.Max(D(e, BuiltInParameter.HOST_VOLUME_COMPUTED), e.LookupParameter("Volume") is { StorageType: StorageType.Double } vp ? vp.AsDouble() : 0) * M3;
                    if (MaterialName(doc, e).IndexOf("steel", StringComparison.OrdinalIgnoreCase) < 0 && MaterialName(doc, e).IndexOf("metal", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new ArgumentException($"{e.Category?.Name} {e.Id.Value} isn't steel; weigh only rebar or steel members (measure concrete in m3).");
                    kg = vol * 7850;
                    working = $"volume {F(vol, 4)} m³ × 7850 kg/m³ = {F(kg, 1)} kg";
                }
                var q = unit == "t" ? kg / 1000 : kg;
                return new Line(e, q, working, q, 0);
            }
            case "m":
                return new Line(e, len * M, $"length {F(len * M)} m", len * M, 0);
            case "m2":
            {
                var area = D(e, BuiltInParameter.HOST_AREA_COMPUTED) * M2;
                if (area == 0 && InPlace.Is(e)) return new Line(e, 0, "0: " + InPlace.Short + "; measure it by hand", 0, 0);
                if (area == 0 && len > 0 && Size(e, "b", "Width", "B") is { } w) { area = len * w * M2; return new Line(e, area, $"L {F(len * M)} × b {F(w * M)} = {F(area)} m²", area, 0); }
                return new Line(e, area, $"area {F(area)} m² (Revit, net of openings)", area, 0);
            }
            default:
            {
                var net = Math.Max(D(e, BuiltInParameter.HOST_VOLUME_COMPUTED), e.LookupParameter("Volume") is { StorageType: StorageType.Double } vp ? vp.AsDouble() : 0) * M3;
                if (net == 0 && InPlace.Is(e)) return new Line(e, 0, "0: " + InPlace.Short + "; measure it by hand", 0, 0);
                // Revit's net volume already deducts joins and openings. Openings at or under the method's threshold go
                // back in, one by one (joins are always deducted, so nothing is counted in two items).
                if (voidThreshold > 0 && e is HostObject host && (e is Wall || e is Floor))
                {
                    var t = e is Wall w ? w.Width : ((Floor)e).FloorType.GetCompoundStructure()?.GetWidth() ?? 0;
                    var small = new List<double>(); var large = 0;
                    foreach (var id in host.FindInserts(true, false, false, false))
                    {
                        if (doc.GetElement(id) is not { } ins || ins.get_BoundingBox(null) is not { } ib) continue;
                        double v;
                        if (e is Wall wl && wl.Location is LocationCurve { Curve: Autodesk.Revit.DB.Line along })
                        {
                            var corners = new[] { ib.Min, ib.Max };
                            var width = Math.Abs((corners[1] - corners[0]).DotProduct(along.Direction));
                            v = width * (ib.Max.Z - ib.Min.Z) * t * M3;
                        }
                        else v = (ib.Max.X - ib.Min.X) * (ib.Max.Y - ib.Min.Y) * t * M3;
                        if (v <= voidThreshold) small.Add(v); else large++;
                    }
                    if (small.Count > 0)
                    {
                        var back = small.Sum();
                        return new Line(e, net + back, $"Revit net {F(net)} m³ + {small.Count} void(s) of {F(voidThreshold, 2)} m³ or less not deducted ({F(back)} m³){(large > 0 ? $"; {large} larger opening(s) deducted" : "")} = {F(net + back)} m³", net + back, 0);
                    }
                }
                string? dims = null;
                try
                {
                    if (e is Wall ww && D(e, BuiltInParameter.WALL_USER_HEIGHT_PARAM) is var h && len > 0 && h > 0) dims = $"L {F(len * M)} × H {F(h * M)} × t {F(ww.Width * M)}";
                    else if (e is Floor f && f.FloorType.GetCompoundStructure() is { } cs)
                    {
                        var outer = Outlines.FloorProfile(doc, f).Select(l => Math.Abs(ExporterIFCUtilsArea(l))).DefaultIfEmpty(0).Max();
                        if (outer > 0) dims = $"outline {F(outer * M2)} m² × t {F(cs.GetWidth() * M)}";
                    }
                    else if (len > 0 && Size(e, "Diameter", "D") is { } dia) dims = $"L {F(len * M)} × π × {F(dia * M)}²/4";
                    else if (len > 0 && Size(e, "b", "Width", "B") is { } b && Size(e, "h", "Depth", "H", "Height") is { } hh) dims = $"L {F(len * M)} × b {F(b * M)} × h {F(hh * M)}";
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException or Autodesk.Revit.Exceptions.ApplicationException) { dims = null; }
                return new Line(e, net, dims is null ? $"volume {F(net)} m³ (Revit, net of joins and openings)" : $"{dims}; Revit net (after joins and openings) = {F(net)} m³", net, 0);
            }
        }
    }

    /// <summary>Plan area of a loop (shoelace over tessellated points).</summary>
    private static double ExporterIFCUtilsArea(CurveLoop loop)
    {
        var pts = new List<XYZ>();
        foreach (var c in loop) { var t = c.Tessellate(); for (var i = 0; i + 1 < t.Count; i++) pts.Add(t[i]); }
        var a = 0.0;
        for (var i = 0; i < pts.Count; i++) { var p = pts[i]; var q = pts[(i + 1) % pts.Count]; a += p.X * q.Y - q.X * p.Y; }
        return a / 2;
    }

    /// <summary>BOQ rows from a CSV/Excel file: columns found by name (ref/item, description, unit, qty/quantity, rate, category, type, level, material).</summary>
    public static List<Item> ReadBoq(string path)
    {
        if (!File.Exists(path)) throw new ArgumentException($"File not found: {path}");
        var rows = Table.Read(path, out var separator);
        string Col(Dictionary<string, string> r, params string[] names)
        {
            // Exact header first, then "name (unit)" or "name ..." forms.
            foreach (var n in names) if (r.TryGetValue(n, out var exact)) return exact;
            foreach (var n in names)
                foreach (var kv in r)
                    if (kv.Key.StartsWith(n + " (", StringComparison.Ordinal) || kv.Key.StartsWith(n + "(", StringComparison.Ordinal)) return kv.Value;
            return "";
        }
        double? Num(string s)
        {
            var t = Regex.Replace(s, @"[^\d.,\-]", "");
            if (t.Length == 0) return null;
            // Decimal comma: '12,5', '1.234,56', or any comma in a ';'-separated CSV.
            if (Regex.IsMatch(t, @"^-?\d{1,3}(\.\d{3})*,\d+$") || Regex.IsMatch(t, @"^-?\d+,\d{1,2}$") || (separator == ';' && t.Contains(',')))
                t = t.Replace(".", "").Replace(",", ".");
            else t = t.Replace(",", "");
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        return rows.Select(r => new Item
        {
            Ref = Col(r, "ref", "item", "item no", "no", "code"), Description = Col(r, "description", "item description", "desc"),
            Unit = Col(r, "unit", "uom"), Quantity = Num(Col(r, "qty", "quantity", "quantities")), Rate = Num(Col(r, "rate", "unit rate", "unit price")),
            Category = Col(r, "category", "revit category"), Type = Col(r, "type", "revit type"), Level = Col(r, "level", "floor"), Material = Col(r, "material"),
        }).Where(i => i.Description.Length > 0 && (i.Quantity is not null || i.Rate is not null || i.Category.Length > 0)).ToList();
    }

    public static Item FromJson(JsonElement j)
    {
        string S(string k) => j.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        double? N(string k) => j.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        return new Item { Ref = S("ref"), Description = S("description"), Unit = S("unit"), Category = S("category"), Type = S("type"), Level = S("level"), Material = S("material"), Quantity = N("quantity"), Rate = N("rate") };
    }

    public static string Csv(string s) => Checks.Csv(s);
}

public sealed class BoqCheckTool : IRevitTool
{
    public string Name => "boq_check";
    public string Description =>
        "Check a bill of quantities against the model. (1) A hand-made BOQ (CSV or Excel; for a PDF BOQ, read it and pass " +
        "the rows as items): each item is matched to model elements by its category/type/level/material columns or the " +
        "words of its description, measured in its unit (m3, m2, m, nr, kg, t) and compared, flagging differences over " +
        "the tolerance. (2) Double counting: concrete elements that overlap without being joined (beam inside slab, " +
        "column through beam), whose volume is counted twice. Use boq_item_breakdown for any item's working.";
    public string PropertiesJson => """
    {
      "boq_file": {"type": "string", "description": "CSV or .xlsx with columns like Ref, Description, Unit, Qty (optional Category, Type, Level, Material)."},
      "items": {"type": "array", "items": {"type": "object", "properties": {"ref": {"type": "string"}, "description": {"type": "string"}, "unit": {"type": "string"}, "quantity": {"type": "number"}, "category": {"type": "string"}, "type": {"type": "string"}, "level": {"type": "string"}, "material": {"type": "string"}}}, "description": "Or the BOQ rows directly (e.g. read from a PDF)."},
      "tolerance_percent": {"type": "number", "description": "Flag differences above this (default 5)."},
      "method": {"type": "string", "enum": ["NRM2", "POMI", "CESMM4", "net"], "description": "Void rule for volumes (default net: Revit's net volume)."},
      "double_counting": {"type": "boolean", "description": "Also look for overlapping concrete (default true)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    /// <summary>Each flagged item and overlap of the last run, for the issue gate.</summary>
    internal List<IssueGateMath.Finding>? Found { get; private set; }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var found = new List<IssueGateMath.Finding>();
        string Item(Measure.Item i) => (i.Ref.Length > 0 ? i.Ref + " " : "") + i.Description;
        var items = args.OptString("boq_file") is { } path ? Measure.ReadBoq(path) : args.Array("items").Select(Measure.FromJson).ToList();
        var tol = args.OptDouble("tolerance_percent") ?? 5;
        var method = args.OptString("method") ?? "net";
        var threshold = Measure.VoidThreshold.TryGetValue(method, out var th) ? th : 0;
        var compared = new List<object>();
        var usedBy = new Dictionary<long, List<string>>();
        var sb = new StringBuilder("Ref,Description,Unit,BOQ quantity,Model quantity,Difference,Difference %,Elements,Flag\n");
        foreach (var item in items)
        {
            var unit = Measure.Unit(item);
            string flag; double? model = null; int count = 0;
            try
            {
                var elements = Measure.Elements(doc, item);
                count = elements.Count;
                foreach (var e in elements) { if (!usedBy.TryGetValue(e.Id.Value, out var l)) usedBy[e.Id.Value] = l = []; l.Add(item.Ref.Length > 0 ? item.Ref : item.Description); }
                var measured = elements.Select(e => Measure.Of(doc, e, unit, threshold)).ToList();
                model = measured.Sum(l => l.Qty);
                var inPlace = unit is "m3" or "m2" ? measured.Count(l => l.Qty == 0 && InPlace.Is(l.E)) : 0;
                var diff = item.Quantity is { } q ? model.Value - q : (double?)null;
                var pct = item.Quantity is { } q2 && Math.Abs(q2) > 1e-9 ? diff!.Value / q2 * 100 : (double?)null;
                flag = count == 0 ? "no matching elements" : item.Quantity is null ? "no BOQ quantity" : pct is { } p && Math.Abs(p) > tol ? (p > 0 ? "model more than BOQ" : "BOQ more than model") : "ok";
                if (inPlace > 0) flag += $"; {inPlace} in-place element(s) measured as 0 ({InPlace.Short})";
                if (flag != "ok") found.Add(new(Name, "boq_item", 0, Item(item), $"{flag}: BOQ {item.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "-"} {unit}, model {Math.Round(model.Value, 3).ToString(CultureInfo.InvariantCulture)} {unit}"));
                compared.Add(new { item.Ref, item.Description, unit, boq = item.Quantity, model = Math.Round(model.Value, 3), difference = diff is { } d ? Math.Round(d, 3) : (double?)null, percent = pct is { } pp ? Math.Round(pp, 1) : (double?)null, elements = count, flag });
                sb.AppendLine(string.Join(",", Measure.Csv(item.Ref), Measure.Csv(item.Description), unit, item.Quantity?.ToString(CultureInfo.InvariantCulture), Math.Round(model.Value, 3).ToString(CultureInfo.InvariantCulture),
                    diff is { } d2 ? Math.Round(d2, 3).ToString(CultureInfo.InvariantCulture) : "", pct is { } p2 ? Math.Round(p2, 1).ToString(CultureInfo.InvariantCulture) : "", count, Measure.Csv(flag)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                found.Add(new(Name, "boq_item", 0, Item(item), ex.Message));
                compared.Add(new { item.Ref, item.Description, unit, boq = item.Quantity, flag = ex.Message });
                sb.AppendLine(string.Join(",", Measure.Csv(item.Ref), Measure.Csv(item.Description), unit, item.Quantity?.ToString(CultureInfo.InvariantCulture), "", "", "", 0, Measure.Csv(ex.Message)));
            }
        }

        // Double counting: unjoined overlapping concrete.
        var doubles = new List<(long a, string aType, long b, string bType, double m3)>();
        var pairsCapped = false; var doubleTotal = 0.0; var doubleSkippedInPlace = 0; var doubleByPair = new Dictionary<string, double>();
        if (args.OptBool("double_counting", true))
        {
            var cats = new[] { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFoundation };
            var concrete = cats.SelectMany(c => new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType())
                .Where(e => e.get_BoundingBox(null) != null && Measure.MaterialName(doc, e).IndexOf("concrete", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var index = new BoxIndex<Element>(10);
            foreach (var e in concrete) { var b = e.get_BoundingBox(null); index.Add(e, b.Min, b.Max); }
            var solids = new Dictionary<long, Solid?>();
            Solid? SolidOf(Element e)
            {
                if (solids.TryGetValue(e.Id.Value, out var s)) return s;
                var all = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })?.SelectMany(g => g is GeometryInstance gi ? gi.GetInstanceGeometry().Cast<GeometryObject>() : [g]).OfType<Solid>().Where(x => x.Volume > 1e-9).ToList() ?? [];
                Solid? u = null;
                foreach (var x in all) { try { u = u is null ? x : BooleanOperationsUtils.ExecuteBooleanOperation(u, x, BooleanOperationsType.Union); } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { } }
                return solids[e.Id.Value] = u;
            }
            var pairs = 0;
            doubleSkippedInPlace = concrete.Count(InPlace.Is);
            concrete = concrete.Where(e => !InPlace.Is(e)).ToList();
            foreach (var a in concrete)
            {
                var ab = a.get_BoundingBox(null);
                foreach (var b in index.Overlapping(ab.Min, ab.Max, -0.01))
                {
                    if (b.Id.Value <= a.Id.Value || JoinGeometryUtils.AreElementsJoined(doc, a, b)) continue;
                    if (++pairs > 200000) { pairsCapped = true; goto capped; }
                    if (SolidOf(a) is not { } sa || SolidOf(b) is not { } sb2) continue;
                    double v;
                    try { v = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb2, BooleanOperationsType.Intersect)?.Volume ?? 0; }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException) { continue; }
                    var m3 = v * Math.Pow(0.3048, 3);
                    if (m3 < 0.001) continue;
                    doubleTotal += m3;
                    var key = $"{a.Category?.Name} / {b.Category?.Name}";
                    doubleByPair[key] = (doubleByPair.TryGetValue(key, out var acc) ? acc : 0) + m3;
                    if (doubles.Count < 200) doubles.Add((a.Id.Value, Measure.TypeName(a), b.Id.Value, Measure.TypeName(b), Math.Round(m3, 3)));
                }
            }
        }

        capped:
        var folder = Checks.Folder(args);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{doc.Title} - BOQ check {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        if (doubles.Count > 0)
        {
            sb.AppendLine().AppendLine("Double counting (overlapping concrete not joined)").AppendLine("Element A,Type A,Element B,Type B,Overlap m3");
            foreach (var d in doubles) sb.AppendLine(string.Join(",", d.a, Measure.Csv(d.aType), d.b, Measure.Csv(d.bType), d.m3.ToString(CultureInfo.InvariantCulture)));
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        found.AddRange(doubles.Select(d => new IssueGateMath.Finding(Name, "double_counting", d.a, $"{d.aType} / {d.bType}",
            $"overlaps element {d.b} by {d.m3.ToString(CultureInfo.InvariantCulture)} m3 without being joined, so the overlap is counted twice")));
        if (pairsCapped) found.Add(new(Name, "double_counting", 0, "overlap search", "stopped at 200,000 pairs; the rest of the model was not checked for double counting"));
        Found = found;
        return ToolResult.Ok(new
        {
            items = compared.Count, method, file, compared = compared.Take(150),
            inSeveralItems = usedBy.Where(kv => kv.Value.Count > 1).GroupBy(kv => string.Join(" + ", kv.Value)).Select(g => new { items = g.Key, elements = g.Count() }).Take(20),
            pairsCapped = pairsCapped ? "Stopped the overlap search at 200,000 pairs; run per level for the rest." : null,
            doubleCounting = new { totalM3 = Math.Round(doubleTotal, 2), byPair = doubleByPair.Select(kv => new { pair = kv.Key, m3 = Math.Round(kv.Value, 2) }).OrderByDescending(x => x.m3), sample = doubles.Take(30).Select(d => new { d.a, d.aType, d.b, d.bType, overlapM3 = d.m3 }), fix = "Join the overlapping elements (cut_geometry with action join) so Revit deducts the overlap, or decide which item carries it.",
                notChecked = doubleSkippedInPlace > 0 ? $"{doubleSkippedInPlace} in-place element(s) not checked for overlap: {InPlace.Short}." : null },
            note = "Model quantities are as modelled; check the item mapping with boq_item_breakdown before trusting a difference.",
        });
    }
}

public sealed class BoqItemBreakdownTool : IRevitTool
{
    public string Name => "boq_item_breakdown";
    public string Description =>
        "Basis of calculation for one BOQ item: every element it includes (id, level, type), the dimensions and formula used, " +
        "deductions for openings, the waste factor and the measurement method, with the total, to a CSV sheet for that " +
        "item; select: true selects those elements in Revit. The item is given by its fields, or by ref from a BOQ file.";
    public string PropertiesJson => """
    {
      "item": {"type": "object", "properties": {"ref": {"type": "string"}, "description": {"type": "string"}, "unit": {"type": "string"}, "category": {"type": "string"}, "type": {"type": "string"}, "level": {"type": "string"}, "material": {"type": "string"}}},
      "boq_file": {"type": "string"}, "ref": {"type": "string", "description": "With boq_file: the item's ref."},
      "method": {"type": "string", "enum": ["NRM2", "POMI", "CESMM4", "net"], "description": "Default net."},
      "waste_percent": {"type": "number", "description": "Added to the measured total (default 0; a BOQ is usually net)."},
      "select": {"type": "boolean"},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        Measure.Item item;
        if (args.Has("item")) item = Measure.FromJson(args.Raw["item"]);
        else if (args.OptString("boq_file") is { } path)
        {
            var r = args.OptString("ref") ?? throw new ArgumentException("Give ref with boq_file.");
            item = Measure.ReadBoq(path).FirstOrDefault(i => i.Ref.Equals(r, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"No item '{r}' in the BOQ.");
        }
        else throw new ArgumentException("Give item, or boq_file and ref.");
        var unit = Measure.Unit(item);
        var method = args.OptString("method") ?? "net";
        var threshold = Measure.VoidThreshold.TryGetValue(method, out var th) ? th : 0;
        var waste = args.OptDouble("waste_percent") ?? 0;
        var elements = Measure.Elements(doc, item);
        if (elements.Count == 0) return ToolResult.Error($"No elements match '{item.Description}'. Give category/type/level/material.");
        var lines = elements.Select(e => Measure.Of(doc, e, unit, threshold)).OrderBy(l => Measure.LevelName(doc, l.E)).ThenBy(l => Measure.TypeName(l.E)).ToList();
        var net = lines.Sum(l => l.Qty);
        var total = net * (1 + waste / 100);

        var folder = Checks.Folder(args);
        Directory.CreateDirectory(folder);
        var safe = new string((item.Ref.Length > 0 ? item.Ref : item.Description).Take(40).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var file = Path.Combine(folder, $"{doc.Title} - Basis of calculation {safe} {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        var sb = new StringBuilder();
        sb.AppendLine($"Item,{Measure.Csv(item.Ref)},{Measure.Csv(item.Description)}");
        sb.AppendLine($"Unit,{unit}");
        sb.AppendLine($"Method,{method},{Measure.Csv(threshold > 0 ? $"voids up to {threshold.ToString(CultureInfo.InvariantCulture)} m3 not deducted (typical; confirm against the method)" : "net as modelled (Revit volumes after joins and openings)")}");
        sb.AppendLine($"Selection,{Measure.Csv(item.Category.Length > 0 ? $"category {item.Category}" : "by description words")},{Measure.Csv($"type {item.Type} level {item.Level} material {item.Material}".Trim())}");
        sb.AppendLine().AppendLine("Id,Level,Category,Type,Quantity,Working,Gross,Deducted");
        foreach (var l in lines)
            sb.AppendLine(string.Join(",", l.E.Id.Value, Measure.Csv(Measure.LevelName(doc, l.E)), Measure.Csv(l.E.Category?.Name ?? ""), Measure.Csv(Measure.TypeName(l.E)),
                Math.Round(l.Qty, 4).ToString(CultureInfo.InvariantCulture), Measure.Csv(l.Working), Math.Round(l.Gross, 4).ToString(CultureInfo.InvariantCulture), Math.Round(l.Deducted, 4).ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine().AppendLine($"Net,{Math.Round(net, 3).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Waste %,{waste.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Total,{Math.Round(total, 3).ToString(CultureInfo.InvariantCulture)}");
        if (item.Quantity is { } q) sb.AppendLine($"BOQ quantity,{q.ToString(CultureInfo.InvariantCulture)},difference,{Math.Round(total - q, 3).ToString(CultureInfo.InvariantCulture)}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        if (args.OptBool("select"))
            app.ActiveUIDocument.Selection.SetElementIds(lines.Where(l => l.E.Document == doc).Select(l => l.E.Id).ToList());
        return ToolResult.Ok(new
        {
            item = new { item.Ref, item.Description, unit }, method, elements = lines.Count, net = Math.Round(net, 3), wastePercent = waste, total = Math.Round(total, 3), boq = item.Quantity, file,
            byLevel = lines.GroupBy(l => Measure.LevelName(doc, l.E)).Select(g => new { level = g.Key, quantity = Math.Round(g.Sum(l => l.Qty), 3), elements = g.Count() }),
            byType = lines.GroupBy(l => Measure.TypeName(l.E)).Select(g => new { type = g.Key, quantity = Math.Round(g.Sum(l => l.Qty), 3), elements = g.Count() }),
            sample = lines.Take(20).Select(l => new { id = l.E.Id.Value, level = Measure.LevelName(doc, l.E), type = Measure.TypeName(l.E), quantity = Math.Round(l.Qty, 4), working = l.Working }),
            selected = args.OptBool("select") ? lines.Count : (int?)null,
            inPlace = lines.Where(l => l.Qty == 0 && InPlace.Is(l.E)).Select(l => l.E.Id.Value).ToList() is { Count: > 0 } ip
                ? new { ids = ip.Take(50), note = $"{ip.Count} in-place element(s) measured as 0: {InPlace.Short}. Measure them by hand. " + InPlace.GuideHint } : null,
        });
    }
}

public sealed class CostEstimateTool : IRevitTool
{
    public string Name => "cost_estimate";
    public string Description =>
        "Cost estimate from the model: a rate table (CSV/Excel or rows) of items with category/type/level/material or " +
        "description words, unit and rate; each is measured in the model (as boq_check does), times the rate, totalled " +
        "by item, level and category, to CSV. compare_to an earlier estimate CSV to show the cost change between versions.";
    public string PropertiesJson => """
    {
      "rates_file": {"type": "string", "description": "CSV or .xlsx with Description, Unit, Rate (optional Ref, Category, Type, Level, Material)."},
      "rates": {"type": "array", "items": {"type": "object", "properties": {"ref": {"type": "string"}, "description": {"type": "string"}, "unit": {"type": "string"}, "rate": {"type": "number"}, "category": {"type": "string"}, "type": {"type": "string"}, "level": {"type": "string"}, "material": {"type": "string"}}}},
      "currency": {"type": "string", "description": "Label only (default blank)."},
      "method": {"type": "string", "enum": ["NRM2", "POMI", "CESMM4", "net"]},
      "compare_to": {"type": "string", "description": "An earlier cost_estimate CSV."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var rates = args.OptString("rates_file") is { } path ? Measure.ReadBoq(path) : args.Array("rates").Select(Measure.FromJson).ToList();
        rates = rates.Where(r => r.Rate is not null).ToList();
        if (rates.Count == 0) throw new ArgumentException("No rates: give rates_file (with a Rate column) or rates.");
        var threshold = Measure.VoidThreshold.TryGetValue(args.OptString("method") ?? "net", out var th) ? th : 0;
        var currency = args.OptString("currency") ?? "";
        var rows = new List<(Measure.Item item, string unit, double qty, double cost, Dictionary<string, double> byLevel, string? error)>();
        var byCategory = new Dictionary<string, double>();
        foreach (var r in rates)
        {
            var unit = Measure.Unit(r);
            try
            {
                var lines = Measure.Elements(doc, r).Select(e => Measure.Of(doc, e, unit, threshold)).ToList();
                var qty = lines.Sum(l => l.Qty);
                var byLevel = lines.GroupBy(l => Measure.LevelName(doc, l.E)).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty) * r.Rate!.Value);
                foreach (var g in lines.GroupBy(l => l.E.Category?.Name ?? "Other")) byCategory[g.Key] = (byCategory.TryGetValue(g.Key, out var c) ? c : 0) + g.Sum(l => l.Qty) * r.Rate!.Value;
                rows.Add((r, unit, qty, qty * r.Rate!.Value, byLevel, null));
            }
            catch (ArgumentException ex) { rows.Add((r, unit, 0, 0, new Dictionary<string, double>(), ex.Message)); }
        }
        var total = rows.Sum(x => x.cost);

        Dictionary<string, double>? before = null;
        if (args.OptString("compare_to") is { } prev)
        {
            if (!File.Exists(prev)) throw new ArgumentException($"File not found: {prev}");
            before = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Table.Read(prev, out _))
                if (r.TryGetValue("description", out var d) && !d.StartsWith("Total", StringComparison.OrdinalIgnoreCase) && r.TryGetValue("cost", out var c) && double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    before[(r.TryGetValue("ref", out var rf) ? rf : "") + "|" + d] = v;
        }
        var folder = Checks.Folder(args);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{doc.Title} - Cost estimate {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        var sb = new StringBuilder("Ref,Description,Unit,Quantity,Rate,Cost,Previous cost,Change,Note\n");
        foreach (var x in rows)
        {
            var key = x.item.Ref + "|" + x.item.Description;
            var old = before != null && before.TryGetValue(key, out var o) ? o : (double?)null;
            sb.AppendLine(string.Join(",", Measure.Csv(x.item.Ref), Measure.Csv(x.item.Description), x.unit, Math.Round(x.qty, 3).ToString(CultureInfo.InvariantCulture),
                x.item.Rate!.Value.ToString(CultureInfo.InvariantCulture), Math.Round(x.cost, 2).ToString(CultureInfo.InvariantCulture),
                old?.ToString(CultureInfo.InvariantCulture) ?? "", old is { } ov ? Math.Round(x.cost - ov, 2).ToString(CultureInfo.InvariantCulture) : "", Measure.Csv(x.error ?? "")));
        }
        sb.AppendLine($",Total {currency},,,,{Math.Round(total, 2).ToString(CultureInfo.InvariantCulture)},{(before is null ? "" : Math.Round(before.Values.Sum(), 2).ToString(CultureInfo.InvariantCulture))},,");
        sb.AppendLine().AppendLine("Level,Cost");
        foreach (var g in rows.SelectMany(x => x.byLevel).GroupBy(kv => kv.Key).OrderBy(g => g.Key)) sb.AppendLine($"{Measure.Csv(g.Key)},{Math.Round(g.Sum(kv => kv.Value), 2).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine().AppendLine("Category,Cost");
        foreach (var kv in byCategory.OrderByDescending(kv => kv.Value)) sb.AppendLine($"{Measure.Csv(kv.Key)},{Math.Round(kv.Value, 2).ToString(CultureInfo.InvariantCulture)}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return ToolResult.Ok(new
        {
            total = Math.Round(total, 2), currency, file, previousTotal = before is null ? (double?)null : Math.Round(before.Values.Sum(), 2),
            items = rows.Select(x => new { x.item.Ref, x.item.Description, x.unit, quantity = Math.Round(x.qty, 3), rate = x.item.Rate, cost = Math.Round(x.cost, 2), error = x.error }).Take(100),
            byCategory = byCategory.OrderByDescending(kv => kv.Value).Select(kv => new { category = kv.Key, cost = Math.Round(kv.Value, 2) }),
            note = "An estimate from modelled quantities and your rates; items not modelled (preliminaries, finishes not in the model) are not included.",
        });
    }
}

public sealed class SpecCheckTool : IRevitTool
{
    public string Name => "spec_check";
    public string Description =>
        "Check the model and drawings against requirements taken from specifications, minutes of meetings, the basis of " +
        "design or design reports (read the document, then pass its requirements): each requirement can carry a model rule " +
        "(as check_code_rules: category, parameter, min/max/equals) and/or text the drawings must show (e.g. 'C40/50', " +
        "'cover 50') or must not show. Text is searched in text notes on sheets and drafting views. Saves the checklist as " +
        "JSON (save_as) so it can be re-run with checklist_file. Reports each requirement as met, not met or not checked.";
    public string PropertiesJson => """
    {
      "requirements": {"type": "array", "items": {"type": "object", "properties": {
        "id": {"type": "string"}, "source": {"type": "string", "description": "Document and clause, e.g. 'Spec 03 30 00 §2.3' or 'MOM 12 item 4'."},
        "text": {"type": "string", "description": "The requirement in words."},
        "rule": {"type": "object", "description": "A check_code_rules rule (name, category, parameter or measure, min/max/equals/not_empty)."},
        "expect_text": {"type": "array", "items": {"type": "string"}, "description": "Text that must appear in the drawing notes."},
        "forbid_text": {"type": "array", "items": {"type": "string"}, "description": "Text that must not appear (a superseded value)."}
      }, "required": ["text"]}},
      "checklist_file": {"type": "string", "description": "Re-run a saved checklist (JSON)."},
      "save_as": {"type": "string", "description": "Save the requirements to this JSON file."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    /// <summary>Each requirement not met (or not checked) in the last run, on the view that shows it when there is one, for the issue gate.</summary>
    internal List<IssueGateMath.Finding>? Found { get; private set; }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        JsonElement list;
        if (args.OptString("checklist_file") is { } cf)
        {
            if (!File.Exists(cf)) throw new ArgumentException($"File not found: {cf}");
            using var d = JsonDocument.Parse(File.ReadAllText(cf));
            list = (d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement : d.RootElement.GetProperty("requirements")).Clone();
        }
        else if (args.Has("requirements")) list = args.Raw["requirements"];
        else throw new ArgumentException("Give requirements or checklist_file.");
        if (args.OptString("save_as") is { } saveAs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(saveAs))!);
            File.WriteAllText(saveAs, JsonSerializer.Serialize(new { requirements = list }, new JsonSerializerOptions { WriteIndented = true }));
        }

        // Drawing text: notes on sheets and on views placed on sheets, and drafting views.
        var placed = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().SelectMany(s => s.GetAllPlacedViews().Append(s.Id)).Select(id => id.Value));
        var notes = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>()
            .Where(t => placed.Contains(t.OwnerViewId.Value) || doc.GetElement(t.OwnerViewId) is ViewDrafting)
            .Select(t => (text: Regex.Replace(t.Text, @"\s+", " "), view: (doc.GetElement(t.OwnerViewId) as View)?.Name ?? "", viewId: t.OwnerViewId.Value)).ToList();
        long hitView = 0;
        (bool found, string where) Find(string s)
        {
            var hit = notes.FirstOrDefault(n => n.text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
            return (hit.text != null, hit.text != null ? $"'{(hit.text.Length > 80 ? hit.text.Substring(0, 80) + "..." : hit.text)}' in {hit.view}" : "");
        }

        var results = new List<(string id, string source, string text, string status, string evidence, long view)>();
        var n = 0;
        foreach (var r in list.EnumerateArray())
        {
            n++;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = S("id").Length > 0 ? S("id") : $"R{n}";
            var evidence = new List<string>(); var failed = false; var checkedAny = false; var continue_ = false;
            hitView = 0;
            if (r.TryGetProperty("expect_text", out var et))
                foreach (var s in et.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0))
                {
                    checkedAny = true;
                    var (found, where) = Find(s);
                    if (!found) failed = true;
                    evidence.Add(found ? $"shows '{s}': {where}" : $"'{s}' not found in the drawing notes");
                }
            if (r.TryGetProperty("forbid_text", out var ft))
                foreach (var s in ft.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0))
                {
                    checkedAny = true;
                    var (found, where) = Find(s);
                    if (found) { failed = true; hitView = notes.First(x => x.text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).viewId; }
                    evidence.Add(found ? $"still shows '{s}': {where}" : $"no '{s}'");
                }
            if (r.TryGetProperty("rule", out var rule) && rule.ValueKind == JsonValueKind.Object)
            {
                checkedAny = true;
                var res = Drafting.Call(app, new CheckCodeRulesTool(), new { rules = new[] { rule }, folder = Checks.Folder(args) });
                if (res.IsError) { evidence.Add("rule not checked: " + res.Text); failed = true; }
                else
                {
                    using var parsed = JsonDocument.Parse(res.Text);
                    var first = parsed.RootElement.GetProperty("rules").EnumerateArray().FirstOrDefault();
                    var text = first.ValueKind == JsonValueKind.Undefined ? "" : first.ToString();
                    var fails = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("fail", out var fe) && fe.ValueKind == JsonValueKind.Number ? fe.GetInt32() : (int?)null;
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("error", out var er)) { failed = true; evidence.Add("rule error: " + er.GetString()); }
                    var checkedCount = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("checkedCount", out var cc) && cc.ValueKind == JsonValueKind.Number ? cc.GetInt32() : (int?)null;
                    if (fails is > 0) failed = true;
                    if (checkedCount == 0) { evidence.Add("model: no element could be checked (parameter missing or nothing matched)"); checkedAny = evidence.Count > 1; continue_ = true; }
                    if (!continue_) evidence.Add(fails is { } f ? $"model: {f} element(s) fail" : $"model: {(text.Length > 200 ? text.Substring(0, 200) : text)}");
                }
            }
            results.Add((id, S("source"), S("text"), !checkedAny ? "not checked (no rule or text given)" : failed ? "NOT MET" : "met", string.Join("; ", evidence), hitView));
        }

        var folder = Checks.Folder(args);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{doc.Title} - Spec check {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        var sb = new StringBuilder("Id,Source,Requirement,Status,Evidence\n");
        foreach (var x in results) sb.AppendLine(string.Join(",", Checks.Csv(x.id), Checks.Csv(x.source), Checks.Csv(x.text), Checks.Csv(x.status), Checks.Csv(x.evidence)));
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        Found = results.Where(x => x.status != "met").Select(x => new IssueGateMath.Finding(Name, x.status == "NOT MET" ? "requirement" : "not_checked", x.view,
            $"{x.id} {x.source}".Trim(), $"{x.text}: {(x.evidence.Length > 0 ? x.evidence : x.status)}")).ToList();
        return ToolResult.Ok(new
        {
            requirements = results.Count, met = results.Count(x => x.status == "met"), notMet = results.Count(x => x.status == "NOT MET"), notChecked = results.Count(x => x.status.StartsWith("not checked", StringComparison.Ordinal)),
            file, notesSearched = notes.Count, saved = args.OptString("save_as"),
            discrepancies = results.Where(x => x.status == "NOT MET").Take(60).Select(x => new { x.id, x.source, x.text, x.evidence }),
        });
    }
}
