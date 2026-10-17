using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Measures the model for the cost plan and the what-if: each rate of the firm's table against the elements it covers
/// (as boq_check measures a BOQ item), with each element's level, zone and kind, the floor area (GFA) and zone areas, the
/// lengths the rates per linear metre need, the quay or berth length, and the drawings that show a set of elements. The
/// rate table, its region and currency and the add-ons are remembered per model under %APPDATA%\SketchProMax\cost, the
/// way embodied_carbon remembers its factor table; what-if results are kept there too for cost_estimate's options.
/// </summary>
internal static class CostRun
{
    private const double Ft = 0.3048, M2 = Ft * Ft;

    // ---------------------------------------------------------------- per-model store

    public static string Store(Document doc) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "cost", Checks.ModelKey(doc));

    /// <summary>What is remembered for a model: the rate table with its region and currency, the exchange rates and the add-ons.</summary>
    public sealed class Saved
    {
        public string? RatesFile { get; set; }
        public string? Region { get; set; }
        public string? Currency { get; set; }
        public Dictionary<string, double>? Exchange { get; set; }
        public double? Prelims { get; set; }
        public double? Contingency { get; set; }
        public double? Vat { get; set; }
        public double? Escalation { get; set; }
        public string? EscalationFrom { get; set; }
        public string? EscalationTo { get; set; }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static Saved Load(Document doc)
    {
        try
        {
            var file = Path.Combine(Store(doc), "settings.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) ?? new Saved() : new Saved();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new Saved(); }
    }

    public static void Remember(Document doc, Saved s)
    {
        try
        {
            Directory.CreateDirectory(Store(doc));
            File.WriteAllText(Path.Combine(Store(doc), "settings.json"), JsonSerializer.Serialize(s, Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    // ---------------------------------------------------------------- rates and add-ons

    /// <summary>The rates chosen for a run and where they came from.</summary>
    public sealed class Rates
    {
        public Cost.Selection Selection = new();
        public string From = "";
        public string? File;
    }

    public const string RatesJson = """
      "rates_file": {"type": "string", "description": "The firm's rate table (.xlsx or CSV; columns Ref, Description, Unit, Rate, Currency, Region, Category, Type, Level, Material, Notes; make one with cost_estimate template true). Remembered for this model with its region and currency, so later runs use it until another is given."},
      "rates": {"type": "array", "items": {"type": "object", "properties": {"ref": {"type": "string"}, "description": {"type": "string"}, "unit": {"type": "string"}, "rate": {"type": "number"}, "currency": {"type": "string"}, "region": {"type": "string"}, "category": {"type": "string"}, "type": {"type": "string"}, "level": {"type": "string"}, "material": {"type": "string"}}}, "description": "Or rates given directly (not remembered)."},
      "region": {"type": "string", "description": "The rate table's region to use (its rows replace the general ones). Required when every row has a region and there are several."},
      "currency": {"type": "string", "description": "The currency to cost in, e.g. USD (default the table's most common). Rows in another currency need exchange_rates."},
      "exchange_rates": {"type": "object", "description": "Units of the chosen currency per unit of another, e.g. {\"EUR\": 1.08}, to convert the table's rows in that currency."},
      "use_saved_rates": {"type": "boolean", "description": "Use the rate table remembered for this model when rates_file and rates are not given (default true)."}
    """;

    /// <summary>
    /// The rates for this run: rates_file (then remembered with its region, currency and exchange rates), rates given
    /// directly, or the table remembered for the model. Null when there are none and <paramref name="required"/> is false.
    /// </summary>
    public static Rates? ReadRates(Document doc, ToolArgs args, Saved saved, bool required)
    {
        var exchange = Exchange(args) ?? saved.Exchange;
        var region = args.OptString("region");
        var currency = args.OptString("currency");
        if (args.OptString("rates_file") is { } path)
        {
            path = path.Trim().Trim('"');
            if (!File.Exists(path)) throw new ArgumentException($"File not found: {path}");
            var rows = Table.Read(path, out var sep);
            var sel = Cost.SelectRates(rows, region, currency, exchange, sep);
            saved.RatesFile = Path.GetFullPath(path); saved.Region = sel.Region; saved.Currency = sel.Currency; saved.Exchange = exchange;
            Remember(doc, saved);
            return new Rates { Selection = sel, From = $"rate table {Path.GetFileName(path)} (now remembered for this model)", File = Path.GetFullPath(path) };
        }
        if (args.Has("rates"))
        {
            var rows = args.Array("rates").Select(j => j.EnumerateObject().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString(), StringComparer.OrdinalIgnoreCase)).ToList();
            var sel = Cost.SelectRates(rows, region, currency, exchange);
            foreach (var r in sel.Rows) r.Source = r.Source.Replace("Rates row", "given row");
            return new Rates { Selection = sel, From = "rates given in the request" };
        }
        if (args.OptBool("use_saved_rates", true) && saved.RatesFile is { } remembered)
        {
            if (!File.Exists(remembered))
            {
                if (required) throw new ArgumentException($"The rate table remembered for this model is missing ({remembered}). Give rates_file.");
                return null;
            }
            var rows = Table.Read(remembered, out var sep);
            var sel = Cost.SelectRates(rows, region ?? saved.Region, currency ?? saved.Currency, exchange, sep);
            return new Rates { Selection = sel, From = $"rate table {Path.GetFileName(remembered)} (remembered for this model)", File = remembered };
        }
        if (required) throw new ArgumentException("No rates: give rates_file (the firm's rate table; cost_estimate with template true writes a documented one) or rates.");
        return null;
    }

    private static Dictionary<string, double>? Exchange(ToolArgs args)
    {
        if (!args.Has("exchange_rates")) return null;
        var j = args.Raw["exchange_rates"];
        if (j.ValueKind != JsonValueKind.Object) throw new ArgumentException("exchange_rates is an object like {\"EUR\": 1.08}.");
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in j.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Number || p.Value.GetDouble() <= 0) throw new ArgumentException($"exchange_rates {p.Name} must be a number above 0.");
            d[p.Name.ToUpperInvariant()] = p.Value.GetDouble();
        }
        return d;
    }

    public const string AddOnsJson = """
      "prelims_percent": {"type": "number", "description": "Preliminaries, % of the measured works (default the last used for this model, else 0)."},
      "contingency_percent": {"type": "number", "description": "Contingency, % of works and preliminaries (default the last used, else 0)."},
      "escalation_percent_per_year": {"type": "number", "description": "Escalation, % a year compounded from escalation_from to escalation_to (default the last used, else 0)."},
      "escalation_from": {"type": "string", "description": "Base date of the rates, yyyy-mm-dd (default today)."},
      "escalation_to": {"type": "string", "description": "Target date, e.g. the mid-point of construction, yyyy-mm-dd."},
      "vat_percent": {"type": "number", "description": "VAT, % of the total before VAT (default the last used, else 0)."}
    """;

    /// <summary>The add-ons: given now (then remembered for the model), else the last used for it, else none.</summary>
    public static Cost.AddOns ReadAddOns(Document doc, ToolArgs args, Saved saved)
    {
        var a = new Cost.AddOns
        {
            PrelimsPct = args.OptDouble("prelims_percent") ?? saved.Prelims ?? 0,
            ContingencyPct = args.OptDouble("contingency_percent") ?? saved.Contingency ?? 0,
            VatPct = args.OptDouble("vat_percent") ?? saved.Vat ?? 0,
            EscalationPctPerYear = args.OptDouble("escalation_percent_per_year") ?? saved.Escalation ?? 0,
            BaseDate = Date(args.OptString("escalation_from") ?? saved.EscalationFrom, "escalation_from") ?? DateTime.Today,
            TargetDate = Date(args.OptString("escalation_to") ?? saved.EscalationTo, "escalation_to"),
        };
        Cost.Validate(a);
        if (new[] { "prelims_percent", "contingency_percent", "vat_percent", "escalation_percent_per_year", "escalation_from", "escalation_to" }.Any(args.Has))
        {
            saved.Prelims = a.PrelimsPct; saved.Contingency = a.ContingencyPct; saved.Vat = a.VatPct; saved.Escalation = a.EscalationPctPerYear;
            saved.EscalationFrom = args.OptString("escalation_from") ?? saved.EscalationFrom; saved.EscalationTo = a.TargetDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Remember(doc, saved);
        }
        return a;
    }

    private static DateTime? Date(string? s, string name)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = ["yyyy-MM-dd", "yyyy-MM", "yyyy/MM/dd", "dd/MM/yyyy"];
        return DateTime.TryParseExact(s!.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
            : throw new ArgumentException($"{name} '{s}' is not a date like 2027-06-30.");
    }

    // ---------------------------------------------------------------- design option scope

    /// <summary>
    /// Whether an element is in the building being costed: without an option, the main model and the primary options
    /// (as Revit shows by default); with one, the main model, the named option, and the primary options of other sets.
    /// </summary>
    public static bool InScope(Element e, string? option, ElementId? optionSet)
    {
        var o = e.DesignOption;
        if (o is null) return true;
        if (option is null) return o.IsPrimary;
        if (o.Name.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return o.IsPrimary && (optionSet is null || SetOf(o) is not { } set || set != optionSet);
    }

    public static ElementId? SetOf(DesignOption o) => o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();

    /// <summary>The option set of the design option named like <paramref name="option"/>; throws when there is none.</summary>
    public static ElementId? OptionSet(Document doc, string? option)
    {
        if (option is null) return null;
        var all = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>().ToList();
        var o = all.FirstOrDefault(x => x.Name.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException($"No design option named like '{option}'." + (all.Count > 0 ? " Options: " + string.Join(", ", all.Select(x => x.Name)) + "." : " This model has no design options."));
        return SetOf(o);
    }

    // ---------------------------------------------------------------- floor area and zones

    private sealed record Patch(string Level, double X, double Y, double Z, double M2);

    private sealed record Zone(string Name, string Level, List<(double X, double Y)>? Poly, BoundingBoxXYZ? Box, double M2);

    private static List<(double X, double Y)>? Outline(SpatialElement s)
    {
        IList<IList<BoundarySegment>>? loops;
        try { loops = s.GetBoundarySegments(new SpatialElementBoundaryOptions()); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        if (loops is null || loops.Count == 0) return null;
        List<(double X, double Y)>? best = null;
        foreach (var loop in loops)
        {
            var pts = new List<(double X, double Y)>();
            foreach (var seg in loop)
            {
                var t = seg.GetCurve().Tessellate();
                for (var i = 0; i + 1 < t.Count; i++) pts.Add((t[i].X, t[i].Y));
            }
            if (pts.Count >= 3 && (best is null || Cost.Area(pts) > Cost.Area(best))) best = pts;
        }
        return best;
    }

    private static string LevelOf(Document doc, ElementId id) => (doc.GetElement(id) as Level)?.Name ?? "";

    /// <summary>Floor area patches: gross areas (an area scheme for gross building area), else rooms, else suspended slabs.</summary>
    private static (List<Patch> patches, string source) GfaPatches(Document doc)
    {
        var areas = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType().OfType<Area>().Where(a => a.Area > 0).ToList();
        var gross = areas.Where(a => a.AreaScheme?.IsGrossBuildingArea == true).ToList();
        Patch P(Element e, string level, double sqft)
        {
            var c = e.get_BoundingBox(null) is { } b ? (b.Min + b.Max) / 2 : (e.Location as LocationPoint)?.Point ?? XYZ.Zero;
            return new Patch(level, c.X, c.Y, c.Z, sqft * M2);
        }
        if (gross.Count > 0) return (gross.Select(a => P(a, LevelOf(doc, a.LevelId), a.Area)).ToList(), $"gross building areas (area scheme {gross[0].AreaScheme.Name}, {gross.Count} area(s))");
        var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>().Where(r => r.Area > 0).ToList();
        if (rooms.Count > 0) return (rooms.Select(r => P(r, LevelOf(doc, r.LevelId), r.Area)).ToList(), $"rooms ({rooms.Count}; net of walls, so below the true GFA: give gfa_m2 or add gross areas)");
        var slabs = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType().OfType<Floor>()
            .Where(f => !f.FloorType.IsFoundationSlab).ToList();
        return (slabs.Select(f => P(f, Measure.LevelName(doc, f), f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0)).Where(p => p.M2 > 0).ToList(),
            $"suspended slab areas ({slabs.Count} slab(s); no gross areas or rooms in the model)");
    }

    /// <summary>The zones: areas (not the gross scheme), rooms (by department, else name) or scope boxes; auto takes the first the model has.</summary>
    private static (List<Zone> zones, string source) Zones(Document doc, string pick, List<Patch> patches)
    {
        if (pick == "none") return ([], "none (zone_source none)");
        if (pick is "auto" or "areas")
        {
            var areas = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType().OfType<Area>()
                .Where(a => a.Area > 0 && a.AreaScheme?.IsGrossBuildingArea != true).ToList();
            if (areas.Count == 0) areas = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType().OfType<Area>().Where(a => a.Area > 0).ToList();
            var zones = areas.Select(a => new Zone(a.Name.Length > 0 ? a.Name : a.Number, LevelOf(doc, a.LevelId), Outline(a), null, a.Area * M2)).Where(z => z.Poly != null).ToList();
            if (zones.Count > 0) return (zones, $"areas ({zones.Count}, scheme {areas[0].AreaScheme?.Name})");
            if (pick == "areas") return ([], "none: the model has no placed areas (zone_source areas)");
        }
        if (pick is "auto" or "rooms")
        {
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>().Where(r => r.Area > 0).ToList();
            var byDept = rooms.Any(r => (r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "").Trim().Length > 0);
            var zones = rooms.Select(r => new Zone(byDept ? (r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "").Trim() is { Length: > 0 } d ? d : "(no department)" : r.Name,
                LevelOf(doc, r.LevelId), Outline(r), null, r.Area * M2)).Where(z => z.Poly != null).ToList();
            if (zones.Count > 0) return (zones, $"rooms ({zones.Count}, zoned by {(byDept ? "department" : "room name")})");
            if (pick == "rooms") return ([], "none: the model has no placed rooms (zone_source rooms)");
        }
        if (pick is "auto" or "scope_boxes")
        {
            var boxes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                .Select(b => (b.Name, box: b.get_BoundingBox(null))).Where(b => b.box != null).ToList();
            var zones = boxes.Select(b => new Zone(b.Name, "", null, b.box, patches.Where(p => In(b.box!, p.X, p.Y, p.Z)).Sum(p => p.M2))).ToList();
            if (zones.Count > 0) return (zones, $"scope boxes ({zones.Count}; zone area is the floor area inside each)");
            if (pick == "scope_boxes") return ([], "none: the model has no scope boxes (zone_source scope_boxes)");
        }
        return ([], "none: the model has no areas, rooms or scope boxes");
    }

    private static bool In(BoundingBoxXYZ b, double x, double y, double z) =>
        x >= b.Min.X && x <= b.Max.X && y >= b.Min.Y && y <= b.Max.Y && z >= b.Min.Z - 1 && z <= b.Max.Z + 1;

    private static string? ZoneOf(List<Zone> zones, string level, XYZ p)
    {
        if (zones.Count == 0) return null;
        var onLevel = zones.Where(z => z.Box is null && z.Level.Equals(level, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var z in onLevel.Count > 0 ? onLevel : zones)
            if (z.Box is { } b ? In(b, p.X, p.Y, p.Z) : Cost.Inside(p.X, p.Y, z.Poly!)) return z.Name;
        return null;
    }

    // ---------------------------------------------------------------- quay or berth length

    private static readonly Regex QuayWords = new(@"\bquay|\bberth|\bwharf", RegexOptions.IgnoreCase);

    /// <summary>The quay or berth length in m: given, else model lines in a quay/berth/wharf line style, else elements so named, else a project parameter.</summary>
    private static (double m, string source) Quay(Document doc, double? given)
    {
        if (given is > 0) return (given.Value, "given (quay_length_m)");
        var lines = new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>()
            .Where(c => c.CurveElementType == CurveElementType.ModelCurve && QuayWords.IsMatch(c.LineStyle?.Name ?? "")).ToList();
        if (lines.Count > 0) return (lines.Sum(c => c.GeometryCurve?.Length ?? 0) * Ft, $"{lines.Count} model line(s) in line style '{lines[0].LineStyle?.Name}'");
        var named = new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Floors, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Site }))
            .Where(e => QuayWords.IsMatch(Measure.TypeName(e) + " " + (e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "") + " " + (e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "")))
            .ToList();
        if (named.Count > 0)
        {
            double Len(Element e) => e.Location is LocationCurve lc ? lc.Curve.Length
                : e.get_BoundingBox(null) is { } b ? Math.Max(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y) : 0;
            return (named.Sum(Len) * Ft, $"{named.Count} element(s) named quay, berth or wharf (type, mark or comments; length along each, or its longer plan side)");
        }
        foreach (var name in new[] { "Quay Length", "Berth Length", "Wharf Length" })
            if (doc.ProjectInformation?.LookupParameter(name) is { } p)
            {
                var v = p.StorageType == StorageType.Double ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Meters)
                    : p.StorageType == StorageType.String ? Cost.Num(p.AsString()) ?? 0 : p.StorageType == StorageType.Integer ? p.AsInteger() : 0;
                if (v > 0) return (v, $"project parameter '{name}'");
            }
        return (0, "none: no quay or berth lines or elements, no quay_length_m and no 'Quay Length' project parameter");
    }

    // ---------------------------------------------------------------- measuring the plan

    public sealed class Plan
    {
        public List<Cost.Line> Lines = [];
        /// <summary>The Basis sheet: header then one row per element, linked from the cost plan lines.</summary>
        public List<object?[]> BasisRows = [["Item ref", "Item", "Element id", "Level", "Zone", "Category", "Type", "Quantity", "Unit", "Working"]];
        public Cost.Basis Measures = new();
        public HashSet<long> Ids = [];
        public List<string> Notes = [];
    }

    public const int MaxBasisRows = 200000;

    /// <summary>
    /// Measures each rate in the model: the elements it covers (in scope of the design option), each one's quantity with
    /// its working, cost by level, zone and kind, and the Basis rows. Stops between rates on cancel_job.
    /// </summary>
    public static Plan MeasurePlan(Document doc, IReadOnlyList<Cost.RateRow> rates, double voidThreshold, string zoneSource, double? gfaGiven, double? quayGiven, string? option, bool basis = true)
    {
        var plan = new Plan();
        var (patches, gfaSource) = GfaPatches(doc);
        var (zones, zoneLabel) = Zones(doc, zoneSource, patches);
        var m = plan.Measures;
        m.GfaM2 = gfaGiven is > 0 ? gfaGiven.Value : patches.Sum(p => p.M2);
        m.GfaSource = gfaGiven is > 0 ? $"given (gfa_m2); by level from {gfaSource}" : gfaSource;
        foreach (var g in patches.GroupBy(p => p.Level, StringComparer.OrdinalIgnoreCase)) m.GfaByLevel[g.Key] = g.Sum(p => p.M2);
        m.ZoneSource = zoneLabel;
        foreach (var g in zones.GroupBy(z => z.Name, StringComparer.OrdinalIgnoreCase)) m.ZoneAreaM2[g.Key] = g.Sum(z => z.M2);
        (m.QuayM, m.QuaySource) = Quay(doc, quayGiven);

        var set = OptionSet(doc, option);
        var lengthById = new Dictionary<long, (string kind, double m)>();
        var failedElements = 0;
        for (var i = 0; i < rates.Count; i++)
        {
            CancelPoint.ThrowIfRequested($"The cost plan had measured {i} of {rates.Count} rates.");
            if (rates.Count > 10 && i % 5 == 0) Jobs.Report($"Cost plan: rate {i + 1} of {rates.Count}");
            var r = rates[i];
            var item = new Measure.Item { Ref = r.Ref, Description = r.Description, Unit = r.Unit, Category = r.Category, Type = r.Type, Level = r.Level, Material = r.Material, Rate = r.Rate };
            var line = new Cost.Line { Ref = r.Ref, Description = r.Description, Rate = r.Rate, RateSource = r.Source, Notes = r.Notes };
            plan.Lines.Add(line);
            try
            {
                line.Unit = Measure.Unit(item);
                line.Group = Cost.GroupOf(r.Description, line.Unit);
                var elements = Measure.Elements(doc, item).Where(e => InScope(e, option, set)).ToList();
                if (basis && plan.BasisRows.Count < MaxBasisRows) line.BasisFirst = plan.BasisRows.Count + 1;
                foreach (var e in elements)
                {
                    Measure.Line l;
                    try { l = Measure.Of(doc, e, line.Unit, voidThreshold); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException) { failedElements++; continue; }
                    var level = Measure.LevelName(doc, e);
                    var center = e.get_BoundingBox(null) is { } bb ? (bb.Min + bb.Max) / 2 : null;
                    var zone = center is null ? null : ZoneOf(zones, level, center);
                    var cat = e.Category?.Name ?? "";
                    // Bars count with the element they reinforce (a beam's rebar is part of its cost per metre).
                    var host = e is Autodesk.Revit.DB.Structure.Rebar rb ? doc.GetElement(rb.GetHostId()) : e is Autodesk.Revit.DB.Structure.RebarInSystem ris ? doc.GetElement(ris.GetHostId()) : null;
                    var kind = Cost.KindOf((host ?? e).Category?.Name ?? "", Tools.Measure.IsPile(host ?? e));
                    var cost = l.Qty * r.Rate;
                    line.Qty += l.Qty; line.Cost += cost; line.Elements++;
                    Add(line.ByLevel, level, cost);
                    if (zone != null) Add(line.ByZone, zone, cost);
                    Add(line.ByKind, kind, cost);
                    plan.Ids.Add(e.Id.Value);
                    if (host is null && kind is "wall" or "beam" or "pile" or "railing" && !lengthById.ContainsKey(e.Id.Value))
                    {
                        try { lengthById[e.Id.Value] = (kind, line.Unit == "m" ? l.Qty : Tools.Measure.Of(doc, e, "m", 0).Qty); }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException) { }
                    }
                    if (basis && plan.BasisRows.Count < MaxBasisRows)
                        plan.BasisRows.Add([r.Ref, r.Description, e.Id.Value, level, zone ?? "", cat, Tools.Measure.TypeName(e), Math.Round(l.Qty, 4), line.Unit, l.Working]);
                }
                if (line.BasisFirst > 0) line.BasisLast = plan.BasisRows.Count;
                if (line.BasisLast < line.BasisFirst) line.BasisFirst = line.BasisLast = 0;
                if (line.Elements == 0) line.Notes = (line.Notes.Length > 0 ? line.Notes + "; " : "") + "no matching elements";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                line.Error = ex.Message;
                line.BasisFirst = line.BasisLast = 0;
            }
        }
        foreach (var g in lengthById.Values.GroupBy(v => v.kind)) m.LengthM[g.Key] = g.Sum(v => v.m);
        if (failedElements > 0) plan.Notes.Add($"{failedElements} element(s) could not be measured in their line's unit (e.g. concrete weighed in t) and were left out.");
        if (plan.BasisRows.Count >= MaxBasisRows) plan.Notes.Add($"The Basis sheet stops at {MaxBasisRows} rows; lines after that have no basis rows (their quantities are still counted).");
        return plan;
    }

    private static void Add(Dictionary<string, double> d, string key, double v) => d[key] = (d.TryGetValue(key, out var x) ? x : 0) + v;

    // ---------------------------------------------------------------- quantities, carbon and programme

    /// <summary>The value engineering inputs (carbon factors as embodied_carbon remembers them), with the rate table's rates when there is one.</summary>
    public static VeRun.Inputs VeInputs(Document doc, ToolArgs args, Rates? rates)
    {
        var keep = args.Raw.Where(kv => kv.Key is "rules" or "cement_replacement").ToDictionary(kv => kv.Key, kv => kv.Value);
        var inp = VeRun.Read(doc, new ToolArgs(keep));
        if (rates is not null)
        {
            inp.Rates = Ve.RatesFrom(rates.Selection.Rows.Select(r => new Ve.RateRow(r.Description, r.Unit, r.Rate, r.Category)), rates.Selection.Currency);
            inp.Notes.Add("Rates for carbon-side quantities and options: " + rates.From + ".");
        }
        return inp;
    }

    /// <summary>The structure's facts (concrete, rebar, steel, carbon) for the building in scope.</summary>
    public static List<VeRun.Fact> Facts(Document doc, VeRun.Inputs inp, string? option)
    {
        var set = OptionSet(doc, option);
        var ids = VeRun.Categories.SelectMany(c => new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType())
            .Where(e => InScope(e, option, set)).Select(e => e.Id.Value).ToList();
        return VeRun.Facts(doc, inp, ids);
    }

    /// <summary>A building's figures: measured cost (the plan's lines, else the structure at the VE rates), carbon, quantities and frame weeks.</summary>
    public static Cost.Figures Figures(string name, string source, Plan? plan, List<VeRun.Fact> facts, Cost.Outputs outputs)
    {
        var f = new Cost.Figures
        {
            Name = name, Source = source,
            ConcreteM3 = facts.Sum(x => x.ConcreteM3), RebarT = facts.Sum(x => x.RebarKg) / 1000, SteelT = facts.Sum(x => x.SteelKg) / 1000,
            CarbonKg = facts.Sum(x => x.CarbonKg),
            Cost = plan is not null ? plan.Lines.Where(l => l.Error is null).Sum(l => l.Cost) : facts.Sum(x => x.Cost),
            Notes = plan is not null ? "cost from the rate table" : "cost of the structure at the value engineering rates (no rate table)",
        };
        f.Weeks = Cost.FrameWeeks(f.ConcreteM3, f.RebarT, f.SteelT, 0, outputs);
        return f;
    }

    public const string OutputsJson = """
      "programme_outputs": {"type": "object", "description": "Crew outputs a week for the rough frame programme: concrete_m3 (default 150), rebar_t (25), steel_t (40), precast_m3 (250)."}
    """;

    public static Cost.Outputs Outputs(ToolArgs args)
    {
        var o = new Cost.Outputs();
        if (!args.Has("programme_outputs")) return o;
        foreach (var p in args.Raw["programme_outputs"].EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Number || p.Value.GetDouble() <= 0) throw new ArgumentException($"programme_outputs {p.Name} must be a number above 0.");
            var v = p.Value.GetDouble();
            switch (p.Name.ToLowerInvariant())
            {
                case "concrete_m3": o.ConcreteM3PerWeek = v; break;
                case "rebar_t": o.RebarTPerWeek = v; break;
                case "steel_t": o.SteelTPerWeek = v; break;
                case "precast_m3": o.PrecastM3PerWeek = v; break;
                default: throw new ArgumentException($"programme_outputs: unknown '{p.Name}' (concrete_m3, rebar_t, steel_t, precast_m3).");
            }
        }
        return o;
    }

    public static string OutputsText(Cost.Outputs o) =>
        $"Frame programme (rough): one crew per trade in sequence at {Cost.F(o.ConcreteM3PerWeek, 0)} m³ of concrete, {Cost.F(o.RebarTPerWeek, 0)} t of rebar, {Cost.F(o.SteelTPerWeek, 0)} t of steel and {Cost.F(o.PrecastM3PerWeek, 0)} m³ of precast a week (programme_outputs); an index to compare options, not a programme.";

    // ---------------------------------------------------------------- what-if results, for cost_estimate's options

    public sealed class WhatIfSaved
    {
        public string Name { get; set; } = "";
        public string Date { get; set; } = "";
        public string Target { get; set; } = "";
        public string? Copy { get; set; }
        public string Currency { get; set; } = "";
        public List<string> Changes { get; set; } = [];
        public Cost.Figures Current { get; set; } = new();
        public Cost.Figures WhatIf { get; set; } = new();
    }

    private static string Safe(string s) => new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    public static string SaveWhatIf(Document doc, WhatIfSaved w)
    {
        Directory.CreateDirectory(Store(doc));
        var json = JsonSerializer.Serialize(w, Indented);
        File.WriteAllText(Path.Combine(Store(doc), "whatif-last.json"), json);
        var file = Path.Combine(Store(doc), "whatif-" + Safe(w.Name.Trim()) + ".json");
        File.WriteAllText(file, json);
        return file;
    }

    /// <summary>A saved what-if: "last" or the name it was saved under.</summary>
    public static WhatIfSaved LoadWhatIf(Document doc, string? name)
    {
        var file = Path.Combine(Store(doc), "whatif-" + Safe(string.IsNullOrWhiteSpace(name) ? "last" : name!.Trim()) + ".json");
        if (!File.Exists(file)) throw new ArgumentException(string.IsNullOrWhiteSpace(name) ? "No what_if result saved for this model: run what_if (with confirm) first." : $"No what_if result named '{name}' for this model.");
        try { return JsonSerializer.Deserialize<WhatIfSaved>(File.ReadAllText(file)) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { throw new ArgumentException($"The saved what-if {file} can't be read ({ex.Message})."); }
    }

    // ---------------------------------------------------------------- drawings that show elements

    public sealed record Drawing(string Sheet, string SheetName, string View, string ViewType, int Elements);

    /// <summary>
    /// The views that show any of the elements (plans, sections, elevations, 3D views, schedules; not templates, legends or
    /// drafting views) and the sheets they are placed on. Views not on a sheet are listed with an empty sheet.
    /// </summary>
    public static List<Drawing> Drawings(Document doc, ICollection<ElementId> ids, out int viewsChecked, int maxViews = 3000)
    {
        viewsChecked = 0;
        var list = new List<Drawing>();
        if (ids.Count == 0) return list;
        var sheetsOf = new Dictionary<long, List<ViewSheet>>();
        foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
        {
            foreach (var v in sheet.GetAllPlacedViews()) Put(sheetsOf, v.Value, sheet);
        }
        foreach (var ssi in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            if (doc.GetElement(ssi.OwnerViewId) is ViewSheet sh) Put(sheetsOf, ssi.ScheduleId.Value, sh);
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v is not ViewSheet && v.ViewType is not (ViewType.Legend or ViewType.DraftingView or ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined or ViewType.Report or ViewType.ColumnSchedule or ViewType.PanelSchedule))
            .Where(v => v is not ViewSchedule s || !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
            .OrderByDescending(v => sheetsOf.ContainsKey(v.Id.Value)).Take(maxViews).ToList();
        var filter = new ElementIdSetFilter(ids);
        for (var i = 0; i < views.Count; i++)
        {
            if (CancelPoint.Stop("Drawings affected", i, views.Count)) break;
            var v = views[i];
            int n;
            try { n = new FilteredElementCollector(doc, v.Id).WherePasses(filter).GetElementCount(); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { continue; }
            viewsChecked++;
            if (n == 0) continue;
            if (sheetsOf.TryGetValue(v.Id.Value, out var sheets))
                foreach (var s in sheets) list.Add(new Drawing(s.SheetNumber, s.Name, v.Name, v.ViewType.ToString(), n));
            else list.Add(new Drawing("", "", v.Name, v.ViewType.ToString(), n));
        }
        return list.OrderBy(d => d.Sheet.Length == 0 ? 1 : 0).ThenBy(d => d.Sheet, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.View, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Put(Dictionary<long, List<ViewSheet>> d, long key, ViewSheet s)
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = [];
        l.Add(s);
    }

    // ---------------------------------------------------------------- writing

    /// <summary>Writes an .xlsx workbook (when asked) and a CSV of its first sheet; returns both paths.</summary>
    public static (string csv, string? xlsx) Write(Document doc, ToolArgs args, string title, List<(string Name, IList<object?[]> Rows)> sheets) =>
        CarbonRun.Write(doc, args, title, sheets);
}

public sealed class WhatIfTool : IRevitTool
{
    public string Name => "what_if";
    public string Description =>
        "What if: tries a parameter change on a COPY of the saved model (or on the elements in a Revit design option), " +
        "never on the open model, and sets it side by side with the current model: quantities (concrete, rebar, steel), " +
        "cost (the firm's rate table, as cost_estimate), embodied carbon (A1-A3), a rough frame programme, and the drawings " +
        "affected (the sheets and views that show the changed elements). Changes: slab thickness, column size, material " +
        "grade, grid spacing. Without confirm it only previews the plan, the current figures and the drawings; ask the user, " +
        "then run it again with confirm true. Workshared models are refused unless the open model is a detached copy. " +
        "The result is saved for cost_estimate's options ('what_if'); Excel and CSV. A screening aid; an engineer confirms it.";
    public string PropertiesJson => "{" + CostRun.RatesJson + "," + CostRun.OutputsJson + """
      ,
      "changes": {"type": "array", "items": {"type": "object", "properties": {
        "kind": {"type": "string", "enum": ["slab_thickness", "column_size", "material_grade", "grid_spacing"]},
        "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "The elements to change (or from_type, or level)."},
        "from_type": {"type": "string", "description": "Instead of ids: every element of this type."},
        "level": {"type": "string", "description": "Instead of ids: every slab or column on this level (exact name)."},
        "category": {"type": "string", "description": "material_grade: the category with from_type or level (default the structural categories)."},
        "thickness_mm": {"type": "number", "description": "slab_thickness: the new overall thickness."},
        "width_mm": {"type": "number", "description": "column_size: the new width (or diameter for round columns)."},
        "depth_mm": {"type": "number", "description": "column_size: the new depth (default the width)."},
        "to_type": {"type": "string", "description": "column_size: an existing column type to use instead of a size."},
        "to_grade": {"type": "string", "description": "material_grade: the concrete strength class, e.g. C40/50 (an existing material of that class)."},
        "to_material": {"type": "string", "description": "material_grade: the material by name."},
        "grids": {"type": "array", "items": {"type": "string"}, "description": "grid_spacing: the grid names (default every straight grid along axis)."},
        "axis": {"type": "string", "enum": ["x", "y"], "description": "grid_spacing: x moves grids that run north-south (their x changes), y those that run east-west."},
        "spacing_m": {"type": "number", "description": "grid_spacing: the new distance between neighbouring grids; the first grid stays."},
        "scale": {"type": "number", "description": "grid_spacing: or multiply every distance from the first grid by this."}
      }, "required": ["kind"]}, "description": "The parameter changes to try. Columns that move with grids follow them; beams framing into them stretch."},
      "target": {"type": "string", "enum": ["copy", "design_option"], "description": "copy (default): a copy of the saved model is opened, changed, measured, saved and closed; design_option: only the elements in design_option are changed (grid_spacing can't be)."},
      "design_option": {"type": "string", "description": "With target design_option: the option's name (or part of it)."},
      "copy_path": {"type": "string", "description": "Where to save the copy (default '<model> - What if <date>.rvt' in the output folder)."},
      "confirm": {"type": "boolean", "description": "Run it. Only after the user agreed to the preview; without it nothing is copied or changed (default false)."},
      "name": {"type": "string", "description": "Save the result under this name for cost_estimate options ('what_if:<name>'); always saved as the last what-if too."},
      "method": {"type": "string", "enum": ["NRM2", "POMI", "CESMM4", "net"], "description": "Void rule for the measured volumes (default net)."},
      "cement_replacement": {"type": "string", "enum": ["none", "fa25", "ggbs50"], "description": "For carbon: concrete whose name gives no replacement (default none)."},
      "excel": {"type": "boolean", "description": "Also write an .xlsx workbook (default true)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => ["changes"];
    public bool ModifiesModel => true;

    private sealed record Op(string Kind, long Id, string What, double A = 0, double B = 0, string? Text = null, string? Grade = null, double[]? Move = null);

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var active = app.ActiveUIDocument.Document;
        var target = args.OptString("target") ?? "copy";
        if (target is not ("copy" or "design_option")) throw new ArgumentException("target is copy or design_option; what_if never changes the open model itself.");
        var confirm = args.OptBool("confirm");
        string? option = null;
        if (target == "design_option")
        {
            option = args.OptString("design_option") ?? throw new ArgumentException("Give design_option (its name or part of it). Make the option in Revit (Manage > Design Options) and copy the elements into it first.");
            CostRun.OptionSet(active, option);
        }
        var saved = CostRun.Load(active);
        var rates = CostRun.ReadRates(active, args, saved, required: false);
        var inp = CostRun.VeInputs(active, args, rates);
        var outputs = CostRun.Outputs(args);
        var threshold = Measure.VoidThreshold.TryGetValue(args.OptString("method") ?? "net", out var th) ? th : 0;
        var currency = rates?.Selection.Currency ?? inp.Rates.Currency;

        var (ops, skipped, described) = Plan(active, args, option);
        if (ops.Count == 0) throw new ArgumentException("Nothing to change: " + (skipped.Count > 0 ? string.Join("; ", skipped.Take(5).Select(s => s.reason)) : "the changes matched no elements."));
        var affected = Affected(active, ops);
        var drawings = CostRun.Drawings(active, affected.Select(id => new ElementId(id)).ToList(), out var viewsChecked);

        // The current model's figures (the same building the what-if changes).
        var plan0 = rates is null ? null : CostRun.MeasurePlan(active, rates.Selection.Rows, threshold, "none", null, null, option, basis: false);
        var current = CostRun.Figures("Current model", "the open model", plan0, CostRun.Facts(active, inp, option), outputs);

        // Safety: never the open model; a workshared model only when it is a detached copy.
        var detached = active.IsWorkshared && active.IsDetached;
        string? refusal = null;
        if (target == "copy" && active.IsWorkshared && !active.IsDetached)
            refusal = "This model is workshared, so what_if won't copy or open it (that could act on the central file or on the model you have open). " +
                      "Detach a copy (Open > Detach from Central, e.g. as a _detached file), save it, open it and run what_if there, or use target design_option.";
        else if (target == "copy" && string.IsNullOrEmpty(active.PathName))
            refusal = "Save the model first: what_if makes its copy from the saved file.";

        if (!confirm)
            return ToolResult.Ok(new
            {
                preview = true, target, designOption = option,
                plan = described, elementsToChange = ops.Select(o => o.Id).Distinct().Count(), skipped = skipped.Take(30).Select(s => new { s.id, s.reason }),
                current = Show(current, currency),
                drawingsAffected = new { sheets = drawings.Where(d => d.Sheet.Length > 0).Select(d => d.Sheet).Distinct().Count(), views = drawings.Select(d => d.View).Distinct().Count(), viewsChecked, list = drawings.Take(40) },
                refused = refusal,
                ask = refusal ?? $"Ask the user: \"Try {string.Join("; ", described)} on {(target == "copy" ? "a copy of the saved model" : $"design option '{option}'")} and compare cost, carbon and drawings? The open model {(target == "copy" ? "is not changed" : "changes only inside that design option")}.\" Run what_if again with confirm true only if they agree.",
                caution = Ve.Caution,
            });
        if (refusal != null) throw new ArgumentException(refusal);

        var warnings = new List<string>();
        Document doc = active;
        string? copyPath = null;
        var opened = false;
        string? source = null;
        if (target == "copy")
        {
            if (active.IsModified) warnings.Add("The open model has unsaved changes; the copy is made from the last saved file and does not have them.");
            if (args.OptString("copy_path") is { } given && !Path.IsPathRooted(given.Trim().Trim('"')))
                throw new ArgumentException("copy_path must be a full path (e.g. D:\\Models\\Tower - what if.rvt), not a relative name.");
            copyPath = args.OptString("copy_path") is { } cp ? OutputPaths.Checked(cp.Trim().Trim('"')) ?? cp : Path.Combine(Checks.Folder(args), $"{San(active.Title)} - What if {DateTime.Now:yyyy-MM-dd HHmm}.rvt");
            if (!copyPath.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) copyPath += ".rvt";
            copyPath = Path.GetFullPath(copyPath);
            if (VeRun.SamePath(copyPath, active.PathName)) throw new ArgumentException("copy_path is the open model's own file; give another copy_path.");
            Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
            if (File.Exists(copyPath)) throw new ArgumentException($"{copyPath} exists already; give another copy_path.");
            // A detached workshared model's file is a central of its own: its copy is opened detached from a temporary
            // source file and saved as copy_path, so nothing reaches the open model's file.
            source = detached ? Path.Combine(Path.GetDirectoryName(copyPath)!, Path.GetFileNameWithoutExtension(copyPath) + " (source).rvt") : copyPath;
            if (File.Exists(source)) throw new ArgumentException($"{source} exists already; give another copy_path.");
            File.Copy(active.PathName, source);
            var options = new OpenOptions();
            if (detached) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            doc = app.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(source), options);
            // Revit hands back the open document when the file is already open: never change or close that one.
            if (doc.Equals(active) || VeRun.SamePath(doc.PathName, active.PathName))
            {
                if (!doc.Equals(active)) try { doc.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                throw new InvalidOperationException("Revit returned the open model instead of the copy, so nothing was changed. The copy file is at " + copyPath + "; open it and run what_if there.");
            }
            opened = true;
        }

        try
        {
            var dupes = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            var batch = ItemBatch.Run(doc, "What if", ops, o => $"{o.Id}: {o.What}", o => [new ElementId(o.Id)], o => Apply(doc, o, dupes), borrow: target != "copy");
            if (opened && batch.Done > 0)
            {
                // A detached copy with worksets is saved as a central of its own at copy_path (never the original's).
                if (doc.IsWorkshared)
                {
                    var o = new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 };
                    o.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                    doc.SaveAs(copyPath!, o);
                }
                else doc.Save();
            }
            var plan1 = rates is null ? null : CostRun.MeasurePlan(doc, rates.Selection.Rows, threshold, "none", null, null, option, basis: false);
            var after = CostRun.Figures("What if", target == "copy" ? "copy " + copyPath : "design option " + option, plan1, CostRun.Facts(doc, inp, option), outputs);
            var delta = Cost.Delta(current, after, currency);

            var name = args.OptString("name") ?? "last";
            var record = new CostRun.WhatIfSaved
            {
                Name = name, Date = Checks.Now, Target = target, Copy = copyPath, Currency = currency, Changes = described, Current = current, WhatIf = after,
            };
            var savedFile = CostRun.SaveWhatIf(active, record);

            var side = new List<object?[]> { new object?[] { "Metric", "Unit", "Current model", "What if", "Change", "Change %" } };
            side.AddRange(delta.Select(d => new object?[] { d.Metric, d.Unit, Math.Round(d.Current, 3), Math.Round(d.WhatIf, 3), Math.Round(d.Change, 3), d.Percent is { } p ? Math.Round(p, 1) : null }));
            var changes = new List<object?[]> { new object?[] { "Element id", "Change", "Result" } };
            changes.AddRange(batch.Items.Select((it, i) => new object?[] { ops[i].Id, ops[i].What, it.Ok ? "done" : "failed: " + it.Error }));
            var sheet = new List<object?[]> { new object?[] { "Sheet", "Sheet name", "View", "View type", "Changed elements shown" } };
            sheet.AddRange(drawings.Select(d => new object?[] { d.Sheet, d.SheetName, d.View, d.ViewType, d.Elements }));
            var notes = new List<object?[]> { new object?[] { "Assumptions and caution" }, new object?[] { Ve.Caution },
                new object?[] { "Changed: " + string.Join("; ", described) },
                new object?[] { target == "copy" ? $"Applied to a copy ({copyPath}); the open model was not changed." : $"Applied to the elements in design option '{option}' of the open model (Ctrl+Z undoes it)." },
                new object?[] { rates is null ? "Cost: no rate table (give rates_file), so the structure is costed at the value engineering rates: " + string.Join(", ", inp.Rates.From.Select(kv => $"{kv.Key} {kv.Value}")) : "Cost: measured works from " + rates.From + $", in {currency}, before add-ons (cost_estimate adds them)." },
                new object?[] { $"Carbon: A1-A3 from the {inp.CarbonSource}." },
                new object?[] { CostRun.OutputsText(outputs) },
                new object?[] { $"Drawings: {viewsChecked} views checked for the changed elements (and, for grids, the columns on them and the beams framing into those)." } };
            notes.AddRange(warnings.Select(w => new object?[] { w }));
            var (csv, xlsx) = CostRun.Write(active, args, "What if", [("Side by side", side), ("Changes", changes), ("Drawings affected", sheet), ("Assumptions", notes)]);

            var cost = delta.First(d => d.Metric.StartsWith("Cost", StringComparison.Ordinal));
            var carbon = delta.First(d => d.Metric.StartsWith("Embodied", StringComparison.Ordinal));
            var card = $"**What if: {Cost.Short(string.Join("; ", described), 90)}**\n" +
                       $"Cost {Cost.Money(cost.Current)} → {Cost.Money(cost.WhatIf, currency)} ({Signed(cost.Change)}{(cost.Percent is { } cp2 ? $", {Signed(cp2, 1)}%" : "")})\n" +
                       $"Carbon {Cost.F(carbon.Current, 1)} → {Cost.F(carbon.WhatIf, 1)} tCO2e ({Signed(carbon.Change, 1)}) · concrete {Signed(delta[0].Change, 1)} m³ · frame {Signed(delta[5].Change, 1)} weeks\n" +
                       $"- Drawings affected: {drawings.Where(d => d.Sheet.Length > 0).Select(d => d.Sheet).Distinct().Count()} sheet(s), {drawings.Select(d => d.View).Distinct().Count()} view(s)\n" +
                       $"- {batch.Done} of {ops.Count} change(s) made on {(target == "copy" ? "a copy" : $"design option {option}")}; compare with cost_estimate options [\"model\", \"what_if{(name == "last" ? "" : ":" + name)}\"]";
            return ToolResult.Ok(new
            {
                target, copy = copyPath, designOption = option, applied = batch.Summary(), skipped = skipped.Take(30).Select(s => new { s.id, s.reason }),
                sideBySide = delta.Select(d => new { d.Metric, d.Unit, current = Math.Round(d.Current, 2), whatIf = Math.Round(d.WhatIf, 2), change = Math.Round(d.Change, 2), percent = d.Percent is { } p ? Math.Round(p, 1) : (double?)null }),
                currency, costFrom = rates?.From ?? "value engineering rates (no rate table)",
                drawingsAffected = new { sheets = drawings.Where(d => d.Sheet.Length > 0).Select(d => d.Sheet).Distinct().Count(), views = drawings.Select(d => d.View).Distinct().Count(), viewsChecked, list = drawings.Take(60) },
                saved = new { name, file = savedFile, useIn = $"cost_estimate options: [\"model\", \"what_if{(name == "last" ? "" : ":" + name)}\"]" },
                files = new { csv, xlsx }, warnings, summaryCard = card, caution = Ve.Caution,
                note = target == "copy" ? "The open model was not changed. Open the copy to review it." : "Only elements in the design option were changed (new types, if any, are added to the project). Ctrl+Z undoes it.",
            });
        }
        finally
        {
            if (opened)
                try { doc.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            if (source != null && source != copyPath)
                try { File.Delete(source); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Signed(double v, int dp = 0) => (v > 0 ? "+" : "") + (dp == 0 ? Cost.Money(v) : Cost.F(v, dp));

    private static object Show(Cost.Figures f, string currency) => new
    {
        cost = Math.Round(f.Cost, 0), currency, concreteM3 = Math.Round(f.ConcreteM3, 2), rebarT = Math.Round(f.RebarT, 2), steelT = Math.Round(f.SteelT, 2),
        tCO2e = Math.Round(f.CarbonKg / 1000, 2), frameWeeks = Math.Round(f.Weeks, 1), costFrom = f.Notes,
    };

    private static string San(string s) => new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    /// <summary>The changes as one operation per element (or per grid), in the open model's ids (the same in a copy).</summary>
    private static (List<Op> ops, List<(long id, string reason)> skipped, List<string> described) Plan(Document doc, ToolArgs args, string? option)
    {
        var ops = new List<Op>();
        var skipped = new List<(long, string)>();
        var described = new List<string>();
        var list = args.Array("changes").ToList();
        if (list.Count == 0) throw new ArgumentException("Give changes: [{kind: slab_thickness | column_size | material_grade | grid_spacing, ...}].");
        foreach (var c in list)
        {
            string? S(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double? N(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            var kind = S("kind") ?? throw new ArgumentException("Each change needs kind (slab_thickness, column_size, material_grade or grid_spacing).");
            if (kind == "grid_spacing")
            {
                if (option != null) throw new ArgumentException("grid_spacing can't be tried in a design option (grids are not in options); use target copy.");
                var axis = S("axis") ?? "x";
                if (axis is not ("x" or "y")) throw new ArgumentException("axis is x or y.");
                var names = c.TryGetProperty("grids", out var gv) && gv.ValueKind == JsonValueKind.Array ? gv.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => !g.IsCurved && g.Curve is Line).ToList();
                // x: grids that run along y (their x position changes); y: along x.
                var row = grids.Where(g => { var d = ((Line)g.Curve).Direction; return axis == "x" ? Math.Abs(d.Y) > 0.99 : Math.Abs(d.X) > 0.99; })
                    .Where(g => names.Count == 0 || names.Any(n => n.Equals(g.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                var missing = names.Where(n => !row.Any(g => g.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0) throw new ArgumentException($"No straight grid along {(axis == "x" ? "y (for axis x)" : "x (for axis y)")} named {string.Join(", ", missing)}.");
                double Pos(Grid g) { var o = ((Line)g.Curve).Origin; return axis == "x" ? o.X : o.Y; }
                var spacing = N("spacing_m") is { } sm ? sm / Ft : (double?)null;
                var now = row.Select(Pos).ToList();
                var next = Cost.GridPositions(now, spacing, N("scale"));
                for (var i = 0; i < row.Count; i++)
                {
                    var d = next[i] - now[i];
                    if (Math.Abs(d) < 1e-6) continue;
                    ops.Add(new Op("grid", row[i].Id.Value, $"grid {row[i].Name} {(d > 0 ? "+" : "")}{Cost.F(d * 304.8, 0)} mm along {axis}", Move: axis == "x" ? [d, 0, 0] : [0, d, 0]));
                }
                described.Add($"grid spacing along {axis} " + (spacing is not null ? $"{Cost.F(N("spacing_m")!.Value, 2)} m" : $"× {Cost.F(N("scale")!.Value, 3)}") + $" ({row.Count} grids)");
                continue;
            }
            var cats = kind switch
            {
                "slab_thickness" => new[] { BuiltInCategory.OST_Floors },
                "column_size" => [BuiltInCategory.OST_StructuralColumns],
                "material_grade" => S("category") is { } cn ? [RevitHelpers.ParseCategory(doc, cn)] : VeRun.Categories,
                _ => throw new ArgumentException($"Unknown change kind '{kind}' (slab_thickness, column_size, material_grade, grid_spacing)."),
            };
            var ids = c.TryGetProperty("element_ids", out var ei) && ei.ValueKind == JsonValueKind.Array ? ei.EnumerateArray().Select(x => x.GetInt64()).ToList() : [];
            if (ids.Count == 0)
            {
                var from = S("from_type");
                var level = S("level");
                if (from is null && level is null) throw new ArgumentException($"{kind}: give element_ids, from_type or level.");
                ids = cats.SelectMany(bc => new FilteredElementCollector(doc).OfCategory(bc).WhereElementIsNotElementType())
                    .Where(e => (from is null || Measure.TypeName(e).Equals(from, StringComparison.OrdinalIgnoreCase)) && (level is null || Measure.LevelName(doc, e).Equals(level, StringComparison.OrdinalIgnoreCase)))
                    .Select(e => e.Id.Value).ToList();
                if (ids.Count == 0) throw new ArgumentException($"{kind}: no element {(from is null ? "" : $"of type '{from}' ")}{(level is null ? "" : $"on level '{level}'")}.");
            }
            var count = 0;
            foreach (var id in ids.Distinct())
            {
                var e = doc.GetElement(new ElementId(id));
                if (e is null) { skipped.Add((id, "not in the model")); continue; }
                if (option != null && !VeRun.InOption(e, option)) { skipped.Add((id, $"in the main model, not in design option '{option}'")); continue; }
                if (!cats.Any(bc => e.Category?.Id.Value == (long)bc)) { skipped.Add((id, $"{e.Category?.Name} is not a {(kind == "slab_thickness" ? "floor" : kind == "column_size" ? "structural column" : "structural element")}")); continue; }
                switch (kind)
                {
                    case "slab_thickness":
                        var mm = N("thickness_mm") ?? throw new ArgumentException("slab_thickness needs thickness_mm.");
                        if (mm is < 50 or > 5000) throw new ArgumentException("thickness_mm is from 50 to 5000.");
                        if (e is not Floor f) { skipped.Add((id, "not a floor")); continue; }
                        ops.Add(new Op("thickness", id, $"{Measure.TypeName(e)} {Cost.F((f.FloorType.GetCompoundStructure()?.GetWidth() ?? 0) * 304.8, 0)} -> {Cost.F(mm, 0)} mm", A: mm));
                        break;
                    case "column_size":
                        if (e is not FamilyInstance) { skipped.Add((id, "not a column family instance")); continue; }
                        if (S("to_type") is { } tt) ops.Add(new Op("column", id, $"{Measure.TypeName(e)} -> {tt}", Text: tt));
                        else
                        {
                            var w = N("width_mm") ?? throw new ArgumentException("column_size needs width_mm (and depth_mm) or to_type.");
                            var d = N("depth_mm") ?? w;
                            if (w is < 100 or > 5000 || d is < 100 or > 5000) throw new ArgumentException("width_mm and depth_mm are from 100 to 5000.");
                            ops.Add(new Op("column", id, $"{Measure.TypeName(e)} -> {Cost.F(w, 0)} x {Cost.F(d, 0)} mm", A: w, B: d));
                        }
                        break;
                    case "material_grade":
                        var grade = S("to_grade");
                        var mat = S("to_material");
                        if (grade is null && mat is null) throw new ArgumentException("material_grade needs to_grade (e.g. C40/50) or to_material.");
                        if (grade != null && Carbon.CylinderStrength(grade) is null) throw new ArgumentException($"to_grade '{grade}' isn't a strength class like C40/50.");
                        ops.Add(new Op("grade", id, $"{Measure.MaterialName(doc, e)} -> {mat ?? grade}", Text: mat, Grade: grade));
                        break;
                }
                count++;
            }
            described.Add(kind switch
            {
                "slab_thickness" => $"slab thickness {Cost.F(N("thickness_mm") ?? 0, 0)} mm ({count} slab(s))",
                "column_size" => S("to_type") is { } t2 ? $"columns to {t2} ({count})" : $"columns {Cost.F(N("width_mm") ?? 0, 0)} x {Cost.F(N("depth_mm") ?? N("width_mm") ?? 0, 0)} mm ({count})",
                _ => $"material {S("to_material") ?? S("to_grade")} ({count} element(s))",
            });
        }
        return (ops, skipped, described);
    }

    private const double Ft = 0.3048;

    /// <summary>The elements a change touches, for the drawings: the changed ones, and for grids the columns on them and the beams framing into those.</summary>
    private static HashSet<long> Affected(Document doc, List<Op> ops)
    {
        var set = new HashSet<long>(ops.Select(o => o.Id));
        var grids = ops.Where(o => o.Kind == "grid").Select(o => doc.GetElement(new ElementId(o.Id))).OfType<Grid>().ToList();
        if (grids.Count == 0) return set;
        var columns = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType()
            .Where(c => c.Location is LocationPoint lp && grids.Any(g => g.Curve is Line l && PlanDistance(l, lp.Point) < 0.05)).ToList();
        foreach (var c in columns) set.Add(c.Id.Value);
        var points = columns.Select(c => ((LocationPoint)c.Location).Point).ToList();
        foreach (var b in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType())
            if (b.Location is LocationCurve { Curve: { } cv } && points.Any(p => Flat(cv.GetEndPoint(0), p) < 1.0 || Flat(cv.GetEndPoint(1), p) < 1.0)) set.Add(b.Id.Value);
        return set;
    }

    private static double Flat(XYZ a, XYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Plan distance from a point to an unbounded grid line.</summary>
    private static double PlanDistance(Line l, XYZ p)
    {
        var o = l.Origin; var d = l.Direction;
        var vx = p.X - o.X; var vy = p.Y - o.Y;
        return Math.Abs(vx * d.Y - vy * d.X) / Math.Max(1e-9, Math.Sqrt(d.X * d.X + d.Y * d.Y));
    }

    /// <summary>One change on one element, inside its own transaction (ItemBatch).</summary>
    private static object? Apply(Document doc, Op o, Dictionary<string, ElementId> dupes)
    {
        var e = doc.GetElement(new ElementId(o.Id)) ?? throw new InvalidOperationException("element gone");
        switch (o.Kind)
        {
            case "grid":
                if (e.Pinned) e.Pinned = false;
                ElementTransformUtils.MoveElement(doc, e.Id, new XYZ(o.Move![0], o.Move[1], o.Move[2]));
                return null;
            case "thickness":
            {
                var f = (Floor)e;
                var key = $"t|{f.GetTypeId().Value}|{o.A}";
                if (!dupes.TryGetValue(key, out var to) || doc.GetElement(to) is null)
                {
                    var type = f.FloorType;
                    var name = $"{type.Name} (what if {Cost.F(o.A, 0)} mm)";
                    var existing = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault(t => t.Name == name);
                    if (existing is null)
                    {
                        existing = (FloorType)type.Duplicate(name);
                        var cs = existing.GetCompoundStructure() ?? throw new InvalidOperationException($"floor type {type.Name} has no layers to make thicker or thinner");
                        var i = cs.StructuralMaterialIndex;
                        if (i < 0) i = Enumerable.Range(0, cs.LayerCount).OrderByDescending(k => cs.GetLayerWidth(k)).First();
                        var width = cs.GetLayerWidth(i) + (o.A / 304.8 - cs.GetWidth());
                        if (width <= 0.01 / 304.8) throw new InvalidOperationException($"{Cost.F(o.A, 0)} mm is thinner than the type's other layers");
                        cs.SetLayerWidth(i, width);
                        existing.SetCompoundStructure(cs);
                    }
                    dupes[key] = to = existing.Id;
                }
                f.ChangeTypeId(to);
                return null;
            }
            case "column":
            {
                if (o.Text is { } typeName)
                {
                    var t = VeApplyTool.TypeNamed(doc, e, typeName) ?? throw new InvalidOperationException($"no {e.Category?.Name} type '{typeName}'");
                    e.ChangeTypeId(t.Id);
                    return null;
                }
                var sym = (FamilySymbol)doc.GetElement(e.GetTypeId());
                var key = $"c|{sym.Id.Value}|{o.A}|{o.B}";
                if (!dupes.TryGetValue(key, out var to) || doc.GetElement(to) is null)
                {
                    var name = $"{sym.Name} (what if {Cost.F(o.A, 0)}x{Cost.F(o.B, 0)})";
                    var existing = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s => s.FamilyName == sym.FamilyName && s.Name == name);
                    if (existing is null)
                    {
                        existing = (FamilySymbol)sym.Duplicate(name);
                        Parameter? P(params string[] names) => names.Select(existing.LookupParameter).FirstOrDefault(p => p is { StorageType: StorageType.Double, IsReadOnly: false });
                        var b = P("b", "Width", "B", "bf");
                        var h = P("h", "Depth", "H", "d", "Height");
                        var dia = P("Diameter", "D", "Dia");
                        if (b != null && h != null) { b.Set(o.A / 304.8); h.Set(o.B / 304.8); }
                        else if (dia != null) dia.Set(o.A / 304.8);
                        else throw new InvalidOperationException($"type {sym.Name} has no editable b/h, Width/Depth or Diameter parameter; give to_type");
                    }
                    if (!existing.IsActive) existing.Activate();
                    dupes[key] = to = existing.Id;
                }
                e.ChangeTypeId(to);
                return null;
            }
            case "grade":
            {
                var mat = VeApplyTool.MaterialFor(doc, o.Text, o.Grade) ?? throw new InvalidOperationException($"no concrete material {o.Text ?? o.Grade} in the model; give to_material");
                if (e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM) is { IsReadOnly: false } ip) { ip.Set(mat.Id); return null; }
                var key = $"g|{e.GetTypeId().Value}|{mat.Id.Value}";
                if (!dupes.TryGetValue(key, out var to) || doc.GetElement(to) is null)
                {
                    var type = doc.GetElement(e.GetTypeId()) as ElementType ?? throw new InvalidOperationException("no type to change");
                    var name = $"{type.Name} {mat.Name}";
                    var existing = new FilteredElementCollector(doc).OfClass(type.GetType()).Cast<ElementType>().FirstOrDefault(t => t.Name == name && t.Category?.Id == type.Category?.Id);
                    var dup = existing ?? type.Duplicate(name);
                    if (existing is null) VeApplyTool.SetTypeMaterial(dup, mat.Id);
                    dupes[key] = to = dup.Id;
                }
                e.ChangeTypeId(to);
                return null;
            }
            default: throw new InvalidOperationException("unknown change " + o.Kind);
        }
    }
}
