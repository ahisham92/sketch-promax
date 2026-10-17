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
        (@"\brailings?\b|\bbalustrades?\b|\bhandrails?\b|\bguard ?rails?\b", [BuiltInCategory.OST_StairsRailing], null),
        (@"\bdoors?\b", [BuiltInCategory.OST_Doors], null),
        (@"\bwindows?\b", [BuiltInCategory.OST_Windows], null),
    ];

    internal static bool IsPile(Element e) => TypeName(e).IndexOf("pile", StringComparison.OrdinalIgnoreCase) >= 0
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
        if (len == 0 && (e.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns || IsPile(e)) && e.get_BoundingBox(null) is { } cb) len = cb.Max.Z - cb.Min.Z;
        if (len == 0 && e is Autodesk.Revit.DB.Architecture.Railing rail) len = rail.GetPath().Sum(c => c.Length);
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

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
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
                compared.Add(new { item.Ref, item.Description, unit, boq = item.Quantity, model = Math.Round(model.Value, 3), difference = diff is { } d ? Math.Round(d, 3) : (double?)null, percent = pct is { } pp ? Math.Round(pp, 1) : (double?)null, elements = count, flag });
                sb.AppendLine(string.Join(",", Measure.Csv(item.Ref), Measure.Csv(item.Description), unit, item.Quantity?.ToString(CultureInfo.InvariantCulture), Math.Round(model.Value, 3).ToString(CultureInfo.InvariantCulture),
                    diff is { } d2 ? Math.Round(d2, 3).ToString(CultureInfo.InvariantCulture) : "", pct is { } p2 ? Math.Round(p2, 1).ToString(CultureInfo.InvariantCulture) : "", count, Measure.Csv(flag)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
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
        "Cost plan from the model and the firm's Excel rate table (region and currency chosen; template true writes a " +
        "documented template). Each rate is measured in the model as boq_check measures a BOQ item, times the rate; then " +
        "editable add-ons: preliminaries %, contingency %, escalation at an annual % from a base date to a target date, " +
        "VAT %. Reports the total; rates per m² GFA, by level and by zone (rooms, areas or scope boxes, said which); per " +
        "linear m of walls, beams, piles, railings and quay or berth length; per m³ of concrete, per tonne of steel and " +
        "rebar, and per item. options compares 2 or 3 options side by side on cost, carbon and a rough programme: the " +
        "model, a design option, a what_if result, or a floor system from ve_compare_options swapped in. Excel cost plan " +
        "with the basis of every line linked to its rows on the Basis sheet (the BOQ basis of calculation), CSV, and a " +
        "short summaryCard for the chat. The table, region, currency and add-ons are remembered for the model. " +
        "compare_to an earlier estimate shows the change.";
    public string PropertiesJson => "{" + CostRun.RatesJson + "," + CostRun.AddOnsJson + "," + CostRun.OutputsJson + """
      ,
      "template": {"type": "boolean", "description": "Write the documented rate table template (.xlsx: Rates and Read me sheets) and stop; fill it in and pass it as rates_file."},
      "template_path": {"type": "string", "description": "Where to write the template (default 'Rate table template.xlsx' in the output folder)."},
      "method": {"type": "string", "enum": ["NRM2", "POMI", "CESMM4", "net"], "description": "Void rule for volumes (default net: Revit's net volume)."},
      "gfa_m2": {"type": "number", "description": "Gross floor area in m² (default: gross building areas, else rooms, else slab areas; the source is reported)."},
      "zone_source": {"type": "string", "enum": ["auto", "areas", "rooms", "scope_boxes", "none"], "description": "Zones for the rates by zone (default auto: areas, else rooms by department or name, else scope boxes)."},
      "quay_length_m": {"type": "number", "description": "Quay or berth length in m (default: model lines in a quay/berth/wharf line style, else elements so named, else the 'Quay Length' project parameter)."},
      "design_option": {"type": "string", "description": "Cost the building with this design option (by name or part of it) instead of the primary ones."},
      "options": {"type": "array", "items": {"type": "string"}, "description": "2 or 3 options side by side (cost with the add-ons, carbon, frame weeks), the first is the baseline: model, option:<design option>, what_if (the last what_if result) or what_if:<name>, or a floor system swapped in for the model's slabs and beams: flat_slab, beam_and_slab, pt_flat_slab, hollow_core, steel_composite (raft, piles for foundations)."},
      "span_x": {"type": "number", "description": "For floor-system options: grid span in m (default from the column grid, else 8)."},
      "span_y": {"type": "number", "description": "For floor-system options: the other span in m (default from the column grid, else span_x)."},
      "cement_replacement": {"type": "string", "enum": ["none", "fa25", "ggbs50"], "description": "For the options' carbon: concrete whose name gives no replacement (default none)."},
      "compare_to": {"type": "string", "description": "An earlier cost_estimate CSV or Excel file: each line's change is shown."},
      "excel": {"type": "boolean", "description": "Also write the Excel cost plan (default true)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var folder = Checks.Folder(args);
        if (args.OptBool("template"))
        {
            var path = args.OptString("template_path") is { } tp ? OutputPaths.Checked(tp.Trim().Trim('"'))! : Path.Combine(folder, "Rate table template.xlsx");
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var sheets = Cost.Template();
            Xlsx.Write(path, sheets);
            return ToolResult.Ok(new
            {
                template = Path.GetFullPath(path), columns = Cost.Columns, exampleRows = sheets[0].Rows.Count - 1,
                howTo = "Fill the Rates sheet with the firm's rates (keep it the first sheet), then run cost_estimate with rates_file set to it, and region and currency if the table has several.",
            });
        }

        var saved = CostRun.Load(doc);
        var rates = CostRun.ReadRates(doc, args, saved, required: true)!;
        var addOns = CostRun.ReadAddOns(doc, args, saved);
        var method = args.OptString("method") ?? "net";
        var threshold = Measure.VoidThreshold.TryGetValue(method, out var th) ? th : 0;
        var zoneSource = args.OptString("zone_source") ?? "auto";
        if (zoneSource is not ("auto" or "areas" or "rooms" or "scope_boxes" or "none")) throw new ArgumentException("zone_source is auto, areas, rooms, scope_boxes or none.");
        var option = args.OptString("design_option");
        var plan = CostRun.MeasurePlan(doc, rates.Selection.Rows, threshold, zoneSource, args.OptDouble("gfa_m2"), args.OptDouble("quay_length_m"), option);
        var lines = plan.Lines;
        var measured = lines.Where(l => l.Error is null).Sum(l => l.Cost);
        var b = Cost.Apply(measured, addOns);
        var perUnit = Cost.UnitRates(lines, b, plan.Measures);
        var cur = rates.Selection.Currency;
        var notes = new List<string>(rates.Selection.Notes);
        notes.AddRange(plan.Notes);

        // ---- Options side by side
        List<Cost.OptionRow>? compared = null;
        var keys = args.StringList("options");
        if (keys.Count > 0)
        {
            if (keys.Count is < 2 or > 3) throw new ArgumentException("options takes 2 or 3 options (the first is the baseline).");
            compared = Cost.Compare(Options(doc, args, keys, rates, plan, threshold, option, notes), addOns);
        }

        // ---- Earlier estimate
        Dictionary<string, double>? before = null;
        if (args.OptString("compare_to") is { } prev)
        {
            if (!File.Exists(prev)) throw new ArgumentException($"File not found: {prev}");
            before = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Table.Read(prev, out _))
                if (r.TryGetValue("description", out var d) && !d.StartsWith("Total", StringComparison.OrdinalIgnoreCase) && d != "Measured works"
                    && (r.TryGetValue("cost", out var c) || r.TryGetValue("amount", out c)) && double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    before[(r.TryGetValue("ref", out var rf) ? rf : "") + "|" + d] = v;
        }

        Directory.CreateDirectory(folder);
        var stem = Path.Combine(folder, $"{new string(doc.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray())} - Cost plan {DateTime.Now:yyyy-MM-dd HHmm}");

        // ---- CSV (the earlier format, so compare_to reads it): lines, then the add-ons, levels and rates per unit.
        var sb = new StringBuilder("Ref,Description,Unit,Quantity,Rate,Cost,Previous cost,Change,Note,Rate basis,Basis rows\n");
        foreach (var l in lines)
        {
            var old = before != null && before.TryGetValue(l.Ref + "|" + l.Description, out var o) ? o : (double?)null;
            sb.AppendLine(string.Join(",", Measure.Csv(l.Ref), Measure.Csv(l.Description), l.Unit, Inv(Math.Round(l.Qty, 3)), Inv(l.Rate), Inv(Math.Round(l.Cost, 2)),
                old is { } ov ? Inv(ov) : "", old is { } ov2 ? Inv(Math.Round(l.Cost - ov2, 2)) : "", Measure.Csv(l.Error ?? l.Notes), Measure.Csv(l.RateSource),
                l.BasisFirst > 0 ? $"Basis {l.BasisFirst}-{l.BasisLast}" : ""));
        }
        sb.AppendLine($",Total measured works {cur},,,,{Inv(Math.Round(measured, 2))},{(before is null ? "" : Inv(Math.Round(before.Values.Sum(), 2)))},,");
        sb.AppendLine().AppendLine("Add-on,Amount");
        foreach (var (label, amount, _) in Cost.AddOnRows(b, addOns, cur)) sb.AppendLine($"{Measure.Csv(label)},{Inv(Math.Round(amount, 2))}");
        sb.AppendLine().AppendLine("Rate per unit,Rate");
        foreach (var u in perUnit) sb.AppendLine($"{Measure.Csv($"{u.Section}: {u.What} per {u.Per}")},{Inv(Math.Round(u.Rate, 2))}");
        File.WriteAllText(stem + ".csv", sb.ToString(), new UTF8Encoding(true));

        // ---- Excel cost plan: each line's quantity sums its rows on the Basis sheet; the add-ons' % are editable cells.
        string? xlsx = null;
        if (args.OptBool("excel", true))
        {
            var planRows = new List<object?[]> { new object?[] { "Ref", "Description", "Unit", "Quantity", "Rate", "Amount", "Share %", "Elements", "Rate basis", "BOQ basis (link)", "Basis rows", "Note" } };
            foreach (var l in lines)
            {
                var r = planRows.Count + 1;
                var hasBasis = l.BasisFirst > 0;
                object? qty = hasBasis ? new Xlsx.Formula($"SUM(Basis!H{l.BasisFirst}:H{l.BasisLast})", Math.Round(l.Qty, 4)) : Math.Round(l.Qty, 4);
                planRows.Add([l.Ref, l.Description, l.Unit, qty, l.Rate, new Xlsx.Formula($"D{r}*E{r}", Math.Round(l.Cost, 2)), measured > 0 ? Math.Round(l.Cost / measured * 100, 2) : null, l.Elements,
                    l.RateSource, hasBasis ? new Xlsx.Formula($"HYPERLINK(\"#'Basis'!A{l.BasisFirst}\",\"Basis rows {l.BasisFirst}-{l.BasisLast}\")", $"Basis rows {l.BasisFirst}-{l.BasisLast}") : "",
                    hasBasis ? $"Basis!H{l.BasisFirst}:H{l.BasisLast}" : "", l.Error ?? l.Notes]);
            }
            var last = planRows.Count;
            planRows.Add([]);
            int w = planRows.Count + 1, p = w + 1, c = w + 2, e = w + 3, bv = w + 4, v = w + 5, t = w + 6;
            planRows.Add(["", "Measured works", "", "", "", new Xlsx.Formula($"SUM(F2:F{last})", Math.Round(b.Measured, 2)), "", "", "the lines above"]);
            planRows.Add(["", "Preliminaries", "%", "", addOns.PrelimsPct, new Xlsx.Formula($"F{w}*E{p}/100", Math.Round(b.Prelims, 2)), "", "", "% of the measured works (edit the % in column E)"]);
            planRows.Add(["", "Contingency", "%", "", addOns.ContingencyPct, new Xlsx.Formula($"(F{w}+F{p})*E{c}/100", Math.Round(b.Contingency, 2)), "", "", "% of works and preliminaries"]);
            planRows.Add(["", "Escalation", "% a year; D = years", Math.Round(b.Years, 4), addOns.EscalationPctPerYear, new Xlsx.Formula($"(F{w}+F{p}+F{c})*((1+E{e}/100)^D{e}-1)", Math.Round(b.Escalation, 2)), "", "",
                addOns.TargetDate is { } td ? $"compounded from {addOns.BaseDate:yyyy-MM-dd} to {td:yyyy-MM-dd}" : "no target date"]);
            planRows.Add(["", "Total before VAT", "", "", "", new Xlsx.Formula($"F{w}+F{p}+F{c}+F{e}", Math.Round(b.BeforeVat, 2))]);
            planRows.Add(["", "VAT", "%", "", addOns.VatPct, new Xlsx.Formula($"F{bv}*E{v}/100", Math.Round(b.Vat, 2)), "", "", "% of the total before VAT"]);
            planRows.Add(["", $"Total {cur}".Trim(), "", "", "", new Xlsx.Formula($"F{bv}+F{v}", Math.Round(b.Total, 2))]);

            var summary = new List<object?[]>
            {
                new object?[] { "Cost plan", doc.Title }, new object?[] { "Date", Checks.Now }, new object?[] { "Currency", cur }, new object?[] { "Region", rates.Selection.Region.Length > 0 ? rates.Selection.Region : "general rates" },
                new object?[] { "Rates", rates.From }, new object?[] { "Method", method }, new object?[] { "Scope", option is null ? "main model and primary design options" : $"design option '{option}'" }, new object?[] { "" },
            };
            summary.AddRange(Cost.AddOnRows(b, addOns, cur).Select(x => new object?[] { x.Label, Math.Round(x.Amount, 2), x.Basis }));
            summary.Add([""]);
            summary.Add(["GFA m²", Math.Round(plan.Measures.GfaM2, 1), plan.Measures.GfaSource]);
            summary.Add(["Zones", plan.Measures.ZoneAreaM2.Count, plan.Measures.ZoneSource]);
            summary.Add(["Quay or berth m", Math.Round(plan.Measures.QuayM, 1), plan.Measures.QuaySource]);
            var unitSheet = new List<object?[]> { new object?[] { "Section", "What", "Per", $"Cost {cur}".Trim(), "Quantity", "Rate", "Basis" } };
            unitSheet.AddRange(perUnit.Select(u => new object?[] { u.Section, u.What, u.Per, Math.Round(u.Cost, 2), Math.Round(u.Quantity, 3), Math.Round(u.Rate, 2), u.Basis }));
            var sheets = new List<(string, IList<object?[]>)> { ("Cost plan", planRows), ("Summary", summary), ("Rates per unit", unitSheet) };
            if (compared is not null) sheets.Add(("Options", OptionSheet(compared, cur)));
            sheets.Add(("Basis", plan.BasisRows));
            var assumptions = new List<object?[]> { new object?[] { "Assumptions" },
                new object?[] { "Quantities are measured from the model as boq_check measures a BOQ item; items not modelled (finishes, services, external works) are not included unless the table has a measurable line for them." },
                new object?[] { "Rates per unit use the measured works (before add-ons) unless marked all-in; per linear m divides each kind's lines by the length of the elements they measured." },
                new object?[] { CostRun.OutputsText(CostRun.Outputs(args)) } };
            assumptions.AddRange(notes.Select(n => new object?[] { n }));
            sheets.Add(("Assumptions", assumptions));
            try { Xlsx.Write(stem + ".xlsx", sheets); xlsx = stem + ".xlsx"; }
            catch (IOException ex) { notes.Add($"The Excel file could not be written ({ex.Message}); the CSV has the cost plan."); }
        }

        var optionLine = compared is null ? null : "- Options: " + string.Join(" · ", compared.Select(o =>
            $"{Cost.Short(o.Option.Name, 28)} {Cost.Money(o.Total)}" + (o.CostPercent is { } cp && o != compared[0] ? $" ({(cp > 0 ? "+" : "")}{Cost.F(cp, 1)}%)" : "") + $", {Cost.F(o.Option.CarbonKg / 1000, 0)} tCO2e, {Cost.F(o.Option.Weeks, 0)} wk"));
        return ToolResult.Ok(new
        {
            total = Math.Round(b.Total, 2), currency = cur, region = rates.Selection.Region.Length > 0 ? rates.Selection.Region : null, ratesFrom = rates.From,
            addOns = Cost.AddOnRows(b, addOns, cur).Select(x => new { x.Label, amount = Math.Round(x.Amount, 2), basis = x.Basis.Length > 0 ? x.Basis : null }),
            gfa = new { m2 = Math.Round(plan.Measures.GfaM2, 1), source = plan.Measures.GfaSource }, zones = plan.Measures.ZoneSource, quay = new { m = Math.Round(plan.Measures.QuayM, 1), source = plan.Measures.QuaySource },
            perUnit = perUnit.Take(60).Select(u => new { u.Section, u.What, u.Per, rate = Math.Round(u.Rate, 2), cost = Math.Round(u.Cost, 0), quantity = Math.Round(u.Quantity, 2) }),
            items = lines.Take(100).Select(l => new
            {
                l.Ref, l.Description, l.Unit, quantity = Math.Round(l.Qty, 3), l.Rate, cost = Math.Round(l.Cost, 2), elements = l.Elements, rateBasis = l.RateSource,
                basisRows = l.BasisFirst > 0 ? $"Basis {l.BasisFirst}-{l.BasisLast}" : null, error = l.Error, note = l.Error is null && l.Notes.Length > 0 ? l.Notes : null,
            }),
            options = compared?.Select(o => new
            {
                option = o.Option.Name, o.Option.Source, works = Math.Round(o.Option.Cost, 0), total = Math.Round(o.Total, 0), costChange = Math.Round(o.CostChange, 0), costPercent = o.CostPercent is { } cp ? Math.Round(cp, 1) : (double?)null,
                tCO2e = Math.Round(o.Option.CarbonKg / 1000, 1), carbonChangeT = Math.Round(o.CarbonChangeKg / 1000, 1), frameWeeks = Math.Round(o.Option.Weeks, 1), weeksChange = Math.Round(o.WeeksChange, 1),
                best = string.Join(", ", new[] { o.Cheapest ? "cheapest" : null, o.LowestCarbon ? "lowest carbon" : null, o.Fastest ? "fastest" : null }.Where(x => x != null)), notes = o.Option.Notes,
            }),
            previousTotal = before is null ? (double?)null : Math.Round(before.Values.Sum(), 2),
            files = new { csv = stem + ".csv", xlsx }, notes,
            summaryCard = Cost.Card($"Cost plan: {doc.Title}", cur, b, addOns, perUnit, lines, optionLine),
            note = "An estimate from modelled quantities and your rates; check the Basis sheet for each line's elements. Try a change with what_if, then compare it here with options [\"model\", \"what_if\"].",
        });
    }

    private static List<object?[]> OptionSheet(List<Cost.OptionRow> rows, string cur)
    {
        var sheet = new List<object?[]> { new object?[] { "Option", "Source", $"Measured works {cur}".Trim(), "Total with add-ons", "Cost change", "Cost change %", "tCO2e (A1-A3)", "Carbon change t", "Carbon change %",
            "Frame weeks (rough)", "Weeks change", "Concrete m3", "Rebar t", "Steel t", "Best", "Notes" } };
        sheet.AddRange(rows.Select(o => new object?[]
        {
            o.Option.Name, o.Option.Source, Math.Round(o.Option.Cost, 0), Math.Round(o.Total, 0), Math.Round(o.CostChange, 0), o.CostPercent is { } cp ? Math.Round(cp, 1) : null,
            Math.Round(o.Option.CarbonKg / 1000, 2), Math.Round(o.CarbonChangeKg / 1000, 2), o.CarbonPercent is { } kp ? Math.Round(kp, 1) : null, Math.Round(o.Option.Weeks, 1), Math.Round(o.WeeksChange, 1),
            Math.Round(o.Option.ConcreteM3, 1), Math.Round(o.Option.RebarT, 2), Math.Round(o.Option.SteelT, 2),
            string.Join(", ", new[] { o.Cheapest ? "cheapest" : null, o.LowestCarbon ? "lowest carbon" : null, o.Fastest ? "fastest" : null }.Where(x => x != null)), o.Option.Notes,
        }));
        return sheet;
    }

    /// <summary>
    /// The options' figures: the model (this plan), a design option (measured again in its scope), a saved what-if, or a
    /// ve_compare_options floor system (or foundation) sized by rule of thumb and swapped in for the model's own.
    /// </summary>
    private static List<Cost.Figures> Options(Document doc, ToolArgs args, List<string> keys, CostRun.Rates rates, CostRun.Plan plan, double threshold, string? option, List<string> notes)
    {
        var inp = CostRun.VeInputs(doc, args, rates);
        var outputs = CostRun.Outputs(args);
        List<VeRun.Fact>? facts = null;
        Cost.Figures? model = null;
        Cost.Figures Model()
        {
            facts ??= CostRun.Facts(doc, inp, option);
            return model ??= CostRun.Figures(option is null ? "This model" : $"This model with option {option}", "model", plan, facts, outputs);
        }
        var list = new List<Cost.Figures>();
        foreach (var raw in keys)
        {
            var k = raw.Trim();
            if (k.Equals("model", StringComparison.OrdinalIgnoreCase)) { list.Add(Model()); continue; }
            if (k.StartsWith("option:", StringComparison.OrdinalIgnoreCase))
            {
                var name = k.Substring(7).Trim();
                var p = CostRun.MeasurePlan(doc, rates.Selection.Rows, threshold, "none", null, null, name, basis: false);
                list.Add(CostRun.Figures($"Design option {name}", "design option", p, CostRun.Facts(doc, inp, name), outputs));
                continue;
            }
            if (k.StartsWith("what_if", StringComparison.OrdinalIgnoreCase))
            {
                var name = k.Contains(':') ? k.Substring(k.IndexOf(':') + 1).Trim() : null;
                var w = CostRun.LoadWhatIf(doc, name);
                var f = w.WhatIf;
                f.Name = "What if: " + Cost.Short(string.Join("; ", w.Changes), 60);
                f.Source = $"what_if {w.Name} ({w.Date})";
                if (w.Currency.Length > 0 && w.Currency != rates.Selection.Currency) notes.Add($"The what-if '{w.Name}' was costed in {w.Currency}, this plan in {rates.Selection.Currency}: run what_if again with the same currency.");
                list.Add(f);
                continue;
            }
            // A ve_compare_options system swapped in for the model's floors (slabs and beams) or foundations.
            var m = Model();
            var grid = Ve.GridSpans(VeRun.ColumnPoints(doc));
            var g = new Ve.Grid { SpanX = args.OptDouble("span_x") ?? grid?.Long ?? 8 };
            g.SpanY = args.OptDouble("span_y") ?? (args.Has("span_x") ? g.SpanX : grid?.Short ?? g.SpanX);
            var sys = Ve.SizeOption(k, g);
            var slabs = facts!.Where(x => x.Class == "slab").ToList();
            var levels = Math.Max(1, slabs.Select(x => x.Level).Distinct().Count());
            g.Floors = levels;
            if (sys.Foundation) sys = Ve.SizeOption(k, g);
            var replaced = facts!.Where(x => sys.Foundation ? x.Class == "foundation" : x.Class is "slab" or "beam").ToList();
            var area = sys.Foundation ? slabs.Sum(x => x.AreaM2) / levels : slabs.Sum(x => x.AreaM2);
            if (area <= 0) { notes.Add($"{k}: the model has no slabs to size it over, so it was left out."); continue; }
            var kinds = sys.Foundation ? new[] { "foundation", "pile" } : ["slab", "beam"];
            var removedCost = plan.Lines.Where(l => l.Error is null).Sum(l => kinds.Sum(kd => l.ByKind.TryGetValue(kd, out var v) ? v : 0));
            var conc = m.ConcreteM3 - replaced.Sum(x => x.ConcreteM3) + (sys.ConcreteM3 + sys.PrecastM3) * area;
            var rebar = m.RebarT - replaced.Sum(x => x.RebarKg) / 1000 + (sys.RebarKg + sys.StrandKg) * area / 1000;
            var steel = m.SteelT - replaced.Sum(x => x.SteelKg) / 1000 + (sys.SteelKg + sys.DeckKg) * area / 1000;
            list.Add(new Cost.Figures
            {
                Name = sys.Name + (sys.Foundation ? " (foundations swapped)" : " (floors swapped)"), Source = "ve_compare_options rule of thumb",
                Cost = m.Cost - removedCost + Ve.Cost(sys, inp.Rates) * area,
                CarbonKg = m.CarbonKg - replaced.Sum(x => x.CarbonKg) + Ve.CarbonKg(sys, inp.Factors, inp.CarbonOptions, inp.Custom) * area,
                ConcreteM3 = conc, RebarT = rebar, SteelT = steel,
                Weeks = Cost.FrameWeeks(conc - sys.PrecastM3 * area, rebar, steel, sys.PrecastM3 * area, outputs),
                Notes = $"{sys.Sizes} over {Ve.F(area, 0)} m² (grid {Ve.F(g.SpanX)} x {Ve.F(g.SpanY)} m) in place of the model's {(sys.Foundation ? "foundations" : "slabs and beams")}; rates {(inp.Rates.AnyPlaceholder ? "partly placeholders (the table lacks some)" : "from the rate table")}",
            });
        }
        if (list.Count < 2) throw new ArgumentException("Fewer than 2 options could be costed: " + string.Join(" ", notes));
        return list;
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
            .Select(t => (text: Regex.Replace(t.Text, @"\s+", " "), view: (doc.GetElement(t.OwnerViewId) as View)?.Name ?? "")).ToList();
        (bool found, string where) Find(string s)
        {
            var hit = notes.FirstOrDefault(n => n.text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
            return (hit.text != null, hit.text != null ? $"'{(hit.text.Length > 80 ? hit.text.Substring(0, 80) + "..." : hit.text)}' in {hit.view}" : "");
        }

        var results = new List<(string id, string source, string text, string status, string evidence)>();
        var n = 0;
        foreach (var r in list.EnumerateArray())
        {
            n++;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = S("id").Length > 0 ? S("id") : $"R{n}";
            var evidence = new List<string>(); var failed = false; var checkedAny = false; var continue_ = false;
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
                    if (found) failed = true;
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
            results.Add((id, S("source"), S("text"), !checkedAny ? "not checked (no rule or text given)" : failed ? "NOT MET" : "met", string.Join("; ", evidence)));
        }

        var folder = Checks.Folder(args);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{doc.Title} - Spec check {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        var sb = new StringBuilder("Id,Source,Requirement,Status,Evidence\n");
        foreach (var x in results) sb.AppendLine(string.Join(",", Checks.Csv(x.id), Checks.Csv(x.source), Checks.Csv(x.text), Checks.Csv(x.status), Checks.Csv(x.evidence)));
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return ToolResult.Ok(new
        {
            requirements = results.Count, met = results.Count(x => x.status == "met"), notMet = results.Count(x => x.status == "NOT MET"), notChecked = results.Count(x => x.status.StartsWith("not checked", StringComparison.Ordinal)),
            file, notesSearched = notes.Count, saved = args.OptString("save_as"),
            discrepancies = results.Where(x => x.status == "NOT MET").Take(60).Select(x => new { x.id, x.source, x.text, x.evidence }),
        });
    }
}
