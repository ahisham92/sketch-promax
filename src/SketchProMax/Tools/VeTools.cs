using System.Globalization;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Measures the model for value engineering: each structural element's class, size, concrete grade, concrete and steel
/// quantities and reinforcement, then the screening checks in <see cref="Ve"/>. Reads the model only; the last scan and
/// the VE register are kept per model under %APPDATA%\SketchProMax\ve.
/// </summary>
internal static class VeRun
{
    private const double Ft = 0.3048, M2 = Ft * Ft, M3 = Ft * Ft * Ft;

    public sealed class Fact
    {
        public long Id;
        public string Class = "", Category = "", Type = "", Family = "", Level = "", Material = "", Grade = "", Option = "";
        public long TypeId;
        public double? Cyl;
        public bool Steel;
        public double ConcreteM3, SteelKg, RebarKg, ThickMm, WidthMm, DepthMm, SpanM, HeightM, AreaM2;
        public bool RebarModelled;
        public double Cost, CarbonKg;
    }

    public static readonly BuiltInCategory[] Categories =
    [
        BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFoundation,
    ];

    /// <summary>Everything a run needs besides the model: rules of thumb, rates and the carbon factors.</summary>
    public sealed class Inputs
    {
        public Ve.Settings Rules = new();
        public Ve.Rates Rates = Ve.PlaceholderRates();
        public Carbon.Table Factors = new();
        public Carbon.Options CarbonOptions = new();
        /// <summary>The model's remembered carbon factor table (as embodied_carbon uses it), its material rows first; empty without one.</summary>
        public List<Carbon.Factor> Custom = [];
        public string? DesignOption;
        /// <summary>Where the rates and the carbon factors came from, for the assumptions.</summary>
        public List<string> Notes = [];

        /// <summary>The carbon factors used, for the assumptions.</summary>
        public string CarbonSource => "built-in generic factors " + Factors.Version + " (placeholders to check)" +
                                      (Custom.Count > 0 ? $", with {Custom.Count} row(s) of the model's own factor table first" : "");
    }

    public const string InputsJson = """
      "rules": {"type": "object", "description": "Rules of thumb to replace, e.g. {\"ratios\": {\"flat_slab\": 30, \"rc_beam\": {\"ratio\": 16, \"min_mm\": 350}}, \"rebar\": {\"beam\": 160}, \"grades\": {\"column\": \"C50/60\"}, \"steel_kg_per_m2\": 50, \"depth_tolerance\": 0.2}. Defaults: span/depth flat_slab 28, slab_on_beams 30, pt_slab 40, rc_beam 15, steel_beam 20, wall height/thickness 25; rebar kg/m³ slab 100, beam 180, column 200, wall 90, foundation 100; grades C32/40 (columns C40/50); steel 55 kg/m²."},
      "rates_file": {"type": "string", "description": "A cost_estimate rate table (CSV/.xlsx: Description, Unit, Rate, optional Category); remembered for this model, so later runs use it when neither rates_file nor rates is given. Concrete per m³ (by element where named), rebar per kg/t, steel per t, formwork per m², precast per m³, PT strand per kg, piling per m."},
      "rates": {"type": "array", "items": {"type": "object", "properties": {"description": {"type": "string"}, "unit": {"type": "string"}, "rate": {"type": "number"}, "category": {"type": "string"}}}, "description": "Rate rows given directly, as cost_estimate takes them."},
      "unit_rates": {"type": "object", "description": "Direct rates laid over the table: concrete_m3, rebar_kg, steel_t, formwork_m2, precast_m3, strand_kg, pile_m."},
      "currency": {"type": "string", "description": "Label only."},
      "cement_replacement": {"type": "string", "enum": ["none", "fa25", "ggbs50"], "description": "For carbon: concrete whose name gives no replacement (default none: CEM I, as embodied_carbon)."}
    """;

    public static Inputs Read(Document doc, ToolArgs args)
    {
        var i = new Inputs
        {
            Rules = Ve.ReadSettings(args.Has("rules") ? args.Raw["rules"] : null),
            Factors = Carbon.BuiltIn(),
            DesignOption = args.OptString("design_option"),
        };
        i.CarbonOptions.Replacement = args.OptString("cement_replacement") ?? "none";
        if (i.CarbonOptions.Replacement is not ("none" or "fa25" or "ggbs50")) throw new ArgumentException("cement_replacement is none, fa25 or ggbs50.");
        // The factor table remembered for this model by embodied_carbon, so both tools give the same carbon.
        if (CarbonRun.SavedFactorFile(doc) is { } factorFile)
        {
            if (!File.Exists(factorFile)) i.Notes.Add($"The carbon factor table remembered for this model is missing ({factorFile}); the built-in factors were used.");
            else
                try
                {
                    var table = Carbon.FromRows(Table.Read(factorFile, out _), custom: true);
                    i.Custom = table.Materials.Where(f => f.Stage == "A1-A3").ToList();
                    i.Notes.Add($"Carbon factors: the table remembered for this model ({factorFile}), {i.Custom.Count} material row(s), before the built-in ones.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException)
                { i.Notes.Add($"The carbon factor table remembered for this model ({factorFile}) can't be read ({ex.Message}); the built-in factors were used."); }
        }
        // The rates file: given (and remembered for this model), else the one remembered.
        var path = args.OptString("rates_file");
        if (path != null) RememberRatesFile(doc, path);
        else if (!args.Has("rates") && SavedRatesFile(doc) is { } savedRates)
        {
            if (File.Exists(savedRates)) { path = savedRates; i.Notes.Add($"Rates: the rate table remembered for this model ({savedRates})."); }
            else i.Notes.Add($"The rate table remembered for this model is missing ({savedRates}); placeholder rates were used.");
        }
        var rows = new List<Ve.RateRow>();
        var items = path != null ? Measure.ReadBoq(path) : args.Array("rates").Select(Measure.FromJson).ToList();
        foreach (var it in items.Where(r => r.Rate is not null)) rows.Add(new Ve.RateRow(it.Description, it.Unit, it.Rate!.Value, it.Category));
        i.Rates = Ve.RatesFrom(rows, args.OptString("currency") ?? "");
        Ve.Override(i.Rates, args.Has("unit_rates") ? args.Raw["unit_rates"] : null);
        return i;
    }

    // ---------------------------------------------------------------- measuring

    private static double D(Element e, BuiltInParameter bip) => e.get_Parameter(bip) is { StorageType: StorageType.Double } p ? p.AsDouble() : 0;

    /// <summary>A length parameter on the instance or its type, in mm, by the first name found.</summary>
    private static double Dim(Element e, params string[] names)
    {
        var type = e.Document.GetElement(e.GetTypeId());
        foreach (var n in names)
            if ((e.LookupParameter(n) ?? type?.LookupParameter(n)) is { StorageType: StorageType.Double } p && p.AsDouble() > 0) return p.AsDouble() * 304.8;
        return 0;
    }

    /// <summary>Modelled reinforcement in kg by host element.</summary>
    public static Dictionary<long, double> BarsByHost(Document d)
    {
        var map = new Dictionary<long, double>();
        foreach (var bar in new FilteredElementCollector(d).OfClass(typeof(Rebar)).Cast<Element>().Concat(new FilteredElementCollector(d).OfClass(typeof(RebarInSystem))))
        {
            double kg;
            try { kg = Measure.Of(d, bar, "kg", 0).Qty; }
            catch (Exception ex) when (ex is ArgumentException or Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            if (kg <= 0) continue;
            var host = bar is Rebar rb ? rb.GetHostId() : ((RebarInSystem)bar).GetHostId();
            map[host.Value] = (map.TryGetValue(host.Value, out var k) ? k : 0) + kg;
        }
        return map;
    }

    /// <summary>One element's facts, or null when it is not a concrete or steel structural element this measures.</summary>
    public static Fact? FactOf(Document d, Element e, Dictionary<long, double> bars, Inputs inp)
    {
        if (e is Wall { IsStackedWallMember: true }) return null;
        if (e is Wall w0 && w0.WallType.Kind != WallKind.Basic) return null;
        var cat = e.Category?.Id.Value ?? 0;
        var cls = cat switch
        {
            (long)BuiltInCategory.OST_Floors => e is Floor f && f.FloorType.IsFoundationSlab ? "foundation" : "slab",
            (long)BuiltInCategory.OST_StructuralFraming => "beam",
            (long)BuiltInCategory.OST_StructuralColumns => "column",
            (long)BuiltInCategory.OST_Walls => "wall",
            (long)BuiltInCategory.OST_StructuralFoundation => "foundation",
            _ => "",
        };
        if (cls.Length == 0) return null;
        var x = new Fact
        {
            Id = e.Id.Value, Class = cls, Category = e.Category?.Name ?? "", Type = Measure.TypeName(e), TypeId = e.GetTypeId().Value,
            Family = (d.GetElement(e.GetTypeId()) as FamilySymbol)?.FamilyName ?? "", Level = Measure.LevelName(d, e), Material = Measure.MaterialName(d, e),
            Option = e.DesignOption?.Name ?? "",
        };
        foreach (var id in e.GetMaterialIds(false))
        {
            double v;
            try { v = e.GetMaterialVolume(id) * M3; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            if (v <= 1e-9 || d.GetElement(id) is not Material m) continue;
            switch (Carbon.ClassOf(m.Name, m.MaterialClass))
            {
                case "concrete": x.ConcreteM3 += v; break;
                case "steel": x.SteelKg += v * 7850; break;
            }
        }
        if (x.ConcreteM3 == 0 && x.SteelKg == 0)
        {
            var vol = Math.Max(D(e, BuiltInParameter.HOST_VOLUME_COMPUTED), e.LookupParameter("Volume") is { StorageType: StorageType.Double } p ? p.AsDouble() : 0) * M3;
            var steel = e is FamilyInstance { StructuralMaterialType: StructuralMaterialType.Steel } || Carbon.ClassOf(x.Material, null) == "steel";
            if (steel) x.SteelKg = vol * 7850;
            else if (e is FamilyInstance { StructuralMaterialType: StructuralMaterialType.Concrete or StructuralMaterialType.PrecastConcrete } || Carbon.ClassOf(x.Material, null) == "concrete"
                     || Carbon.ClassOf(x.Type, null) == "concrete") x.ConcreteM3 = vol;
        }
        x.Steel = x.SteelKg > 0 && x.ConcreteM3 == 0;
        if (x.ConcreteM3 == 0 && x.SteelKg == 0) return null;
        // Concrete walls only (masonry, partitions and finishes are not structure).
        if (cls == "wall" && x.ConcreteM3 == 0) return null;
        x.Cyl = Carbon.CylinderStrength(x.Material) ?? Carbon.CylinderStrength(x.Type);
        x.Grade = x.Cyl is { } c ? GradeName(c) : "";

        switch (e)
        {
            case Floor fl:
                x.ThickMm = (fl.FloorType.GetCompoundStructure()?.GetWidth() ?? D(fl, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)) * 304.8;
                x.AreaM2 = D(fl, BuiltInParameter.HOST_AREA_COMPUTED) * M2;
                break;
            case Wall wl:
                x.ThickMm = wl.Width * 304.8;
                x.HeightM = e.get_BoundingBox(null) is { } bb ? (bb.Max.Z - bb.Min.Z) * Ft : D(e, BuiltInParameter.WALL_USER_HEIGHT_PARAM) * Ft;
                x.SpanM = D(e, BuiltInParameter.CURVE_ELEM_LENGTH) * Ft;
                break;
            default:
                x.WidthMm = Dim(e, "b", "Width", "B", "bf");
                x.DepthMm = Dim(e, "h", "Depth", "H", "d", "Height");
                x.SpanM = (D(e, BuiltInParameter.INSTANCE_LENGTH_PARAM) is > 0 and var cut ? cut : D(e, BuiltInParameter.CURVE_ELEM_LENGTH)) * Ft;
                if (cls == "foundation") x.ThickMm = Dim(e, "Foundation Thickness", "Thickness", "h", "Depth");
                break;
        }

        // Reinforcement: modelled bars, else the typical rate for the class (assumed).
        if (bars.TryGetValue(x.Id, out var kg)) { x.RebarKg = kg; x.RebarModelled = true; }
        else if (x.ConcreteM3 > 0) x.RebarKg = x.ConcreteM3 * (inp.Rules.Rebar.TryGetValue(cls, out var r) ? r : 100);
        Price(x, inp);
        return x;
    }

    public static string GradeName(double cyl) => cyl switch
    {
        16 => "C16/20", 20 => "C20/25", 25 => "C25/30", 28 => "C28/35", 30 => "C30/37", 32 => "C32/40", 35 => "C35/45", 40 => "C40/50",
        45 => "C45/55", 50 => "C50/60", 55 => "C55/67", 60 => "C60/75", _ => $"{cyl:0.#} MPa",
    };

    /// <summary>Cost and A1-A3 carbon of an element: concrete (with a premium per class step above typical), reinforcement and steel.</summary>
    public static void Price(Fact x, Inputs inp)
    {
        var typical = inp.Rules.Grades.TryGetValue(x.Class, out var tg) ? tg : "C32/40";
        var grade = x.Grade.Length > 0 ? x.Grade : typical;
        var steps = x.Cyl is { } c && Carbon.CylinderStrength(typical) is { } tc ? Ve.GradeSteps(c, tc) : 0;
        x.Cost = x.ConcreteM3 * inp.Rates.Concrete(x.Class) * (1 + inp.Rules.GradeStepPremium * steps) + x.RebarKg * inp.Rates.RebarKg + x.SteelKg / 1000 * inp.Rates.SteelT;
        x.CarbonKg = x.ConcreteM3 * Ve.ConcreteCarbonPerM3(grade, inp.Factors, inp.CarbonOptions, inp.Custom) + x.RebarKg * Ve.SteelCarbonPerKg("rebar", inp.Factors, inp.Custom)
                     + x.SteelKg * Ve.SteelCarbonPerKg("section", inp.Factors, inp.Custom);
    }

    /// <summary>
    /// The model's structural elements. Without a design option: the main model and each option set's primary option
    /// (as Revit shows by default); with one: only the elements in the design option(s) whose name contains it.
    /// </summary>
    public static List<Fact> Facts(Document d, Inputs inp, IEnumerable<long>? only = null)
    {
        var bars = BarsByHost(d);
        var list = new List<Fact>();
        IEnumerable<Element> elements = only is null
            ? Categories.SelectMany(c => new FilteredElementCollector(d).OfCategory(c).WhereElementIsNotElementType())
            : only.Select(id => d.GetElement(new ElementId(id))).OfType<Element>();
        foreach (var e in elements)
        {
            if (only is null && !InOption(e, inp.DesignOption)) continue;
            if (FactOf(d, e, bars, inp) is { } f) list.Add(f);
        }
        return list;
    }

    public static bool InOption(Element e, string? option)
    {
        var o = e.DesignOption;
        if (option is null) return o is null || o.IsPrimary;
        return o != null && o.Name.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Plan positions (m) of the structural columns, for the grid spans.</summary>
    public static List<(double X, double Y)> ColumnPoints(Document d) =>
        new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType()
            .Select(e => e.Location is LocationPoint lp ? (lp.Point.X * Ft, lp.Point.Y * Ft) : ((double, double)?)null)
            .Where(p => p != null).Select(p => p!.Value).ToList();

    // ---------------------------------------------------------------- the screening

    public sealed class Scan
    {
        public List<Ve.Opportunity> Items = [];
        public List<Ve.Group> Groups = [];
        public List<(string Class, Ve.Group Group)> GroupClasses = [];
        public List<string> NotChecked = [];
        public List<string> Assumptions = [];
        public List<(string Level, int Steps, int Drops, int Openings, int BeamDepths, int Score)> Formwork = [];
        public object? SpecSummary;
        public int Elements;
        public double FloorAreaM2;
    }

    public static Scan Screen(UIApplication app, Document doc, ToolArgs args, Inputs inp)
    {
        var s = new Scan();
        var rules = inp.Rules;
        var facts = Facts(doc, inp);
        s.Elements = facts.Count;
        var concrete = facts.Where(f => f.ConcreteM3 > 0).ToList();
        var checks = new HashSet<string>(args.StringList("checks").Select(c => c.ToLowerInvariant()));
        bool On(string c) => checks.Count == 0 || checks.Contains(c);
        string Money(double v) => Ve.F(v, 0) + (inp.Rates.Currency.Length > 0 ? " " + inp.Rates.Currency : "");
        double Intensity(IEnumerable<Fact> fs) { var l = fs.ToList(); var m3 = l.Sum(f => f.ConcreteM3); return m3 > 0 ? l.Sum(f => f.CarbonKg) / m3 : 0; }
        double CostPerM3(IEnumerable<Fact> fs) { var l = fs.ToList(); var m3 = l.Sum(f => f.ConcreteM3); return m3 > 0 ? l.Sum(f => f.Cost) / m3 : 0; }

        // Floor area: given, else the suspended slabs' area.
        s.FloorAreaM2 = args.OptDouble("gia_m2") is { } g && g > 0 ? g : facts.Where(f => f.Class == "slab").Sum(f => f.AreaM2);

        // Grid spans for the slabs.
        var spanGiven = args.OptDouble("span_m");
        var grid = Ve.GridSpans(ColumnPoints(doc));
        var concreteBeams = concrete.Count(f => f.Class == "beam");
        var system = args.OptString("slab_system") is { } ss && ss != "auto" ? ss : concreteBeams > 0 ? "slab_on_beams" : "flat_slab";
        if (!rules.Ratios.ContainsKey(system)) throw new ArgumentException($"slab_system '{system}' has no ratio; use flat_slab, slab_on_beams or pt_slab, or add it under rules.ratios.");

        // ---- 1. Thicker than typical: slabs, beams, walls (by type)
        if (On("depth"))
        {
            double? slabSpan = spanGiven ?? (grid is { } gs ? (system == "slab_on_beams" ? gs.Short : gs.Long) : null);
            if (slabSpan is null) s.NotChecked.Add("Slab thickness: no span (fewer than three columns on a grid); give span_m.");
            else s.Assumptions.Add(spanGiven is not null ? $"Slab span {Ve.F(slabSpan.Value)} m (given), slab system {system}."
                : $"Slab span {Ve.F(slabSpan.Value)} m: the median {(system == "slab_on_beams" ? "shorter" : "longer")} column spacing (grid {Ve.F(grid!.Value.Short)} x {Ve.F(grid.Value.Long)} m); slab system {system} ({(concreteBeams > 0 ? "concrete beams found" : "no concrete beams")}). Give span_m and slab_system to set them.");
            foreach (var t in concrete.Where(f => f.Class == "slab" && f.ThickMm > 0).GroupBy(f => f.Type))
            {
                if (slabSpan is null) break;
                var rule = rules.Ratios[system];
                var th = t.First().ThickMm;
                if (Ve.Thicker(th, slabSpan.Value, rule, rules.DepthTolerance) is not { } over) continue;
                var excess = t.Sum(f => f.ConcreteM3) * over.ExcessFraction;
                var to = Smaller(doc, BuiltInCategory.OST_Floors, t.First(), over.TypicalMm, th);
                s.Items.Add(new Ve.Opportunity
                {
                    Key = "depth|slab|" + t.Key, Check = "Thicker than typical", Item = $"Slab type {t.Key} ({t.Count()} slab(s))",
                    Current = $"{Ve.F(th, 0)} mm", Proposed = $"about {Ve.F(Math.Ceiling(over.TypicalMm / 25) * 25, 0)} mm ({system} span {Ve.F(slabSpan.Value)} m / {Ve.F(rule.Ratio)})" + (to.Length > 0 ? $"; existing type {to}" : ""),
                    SavingCost = excess * CostPerM3(t), SavingCarbonKg = excess * Intensity(t), Confidence = spanGiven is null ? "low" : "medium",
                    Basis = $"{Ve.F(excess, 1)} m³ of concrete (with its reinforcement) over a {Ve.F(over.TypicalMm, 0)} mm rule-of-thumb depth",
                    Risk = "deflection, punching, fire, acoustic and vibration needs may justify the depth; transfer or heavily loaded areas", Ids = t.Select(f => f.Id).ToList(),
                    ApplyKind = to.Length > 0 ? "change_type" : "", FromTypes = [t.Key], ToType = to,
                });
            }
            foreach (var t in concrete.Where(f => f.Class == "beam" && f.DepthMm > 0 && f.SpanM > 1).GroupBy(f => f.Type))
            {
                var rule = rules.Ratios["rc_beam"];
                var flagged = t.Select(f => (f, over: Ve.Thicker(f.DepthMm, f.SpanM, rule, rules.DepthTolerance))).Where(x => x.over != null).ToList();
                if (flagged.Count == 0) continue;
                var excess = flagged.Sum(x => x.f.ConcreteM3 * x.over!.Value.ExcessFraction);
                var typical = flagged.Max(x => x.over!.Value.TypicalMm);
                var to = Smaller(doc, BuiltInCategory.OST_StructuralFraming, t.First(), typical, t.First().DepthMm);
                s.Items.Add(new Ve.Opportunity
                {
                    Key = "depth|beam|" + t.Key, Check = "Thicker than typical", Item = $"Beam type {t.Key} ({flagged.Count} of {t.Count()} beam(s))",
                    Current = $"{Ve.F(t.First().WidthMm, 0)} x {Ve.F(t.First().DepthMm, 0)} mm, spans up to {Ve.F(flagged.Max(x => x.f.SpanM))} m",
                    Proposed = $"depth about {Ve.F(Math.Ceiling(typical / 50) * 50, 0)} mm (span / {Ve.F(rule.Ratio)})" + (to.Length > 0 ? $"; existing type {to}" : ""),
                    SavingCost = excess * CostPerM3(flagged.Select(x => x.f)), SavingCarbonKg = excess * Intensity(flagged.Select(x => x.f)), Confidence = "medium",
                    Basis = $"{Ve.F(excess, 2)} m³ over the span/depth rule of thumb, each beam on its own span", Risk = "loads, deflection, openings for services and matching depths on a line of beams",
                    Ids = flagged.Select(x => x.f.Id).ToList(), ApplyKind = to.Length > 0 ? "change_type" : "", FromTypes = [t.Key], ToType = to,
                });
            }
            foreach (var t in concrete.Where(f => f.Class == "wall" && f.ThickMm > 0 && f.HeightM > 1).GroupBy(f => f.Type))
            {
                var rule = rules.Ratios["wall"];
                var flagged = t.Select(f => (f, over: Ve.Thicker(f.ThickMm, f.HeightM, rule, rules.DepthTolerance))).Where(x => x.over != null).ToList();
                if (flagged.Count == 0) continue;
                var excess = flagged.Sum(x => x.f.ConcreteM3 * x.over!.Value.ExcessFraction);
                var typical = flagged.Max(x => x.over!.Value.TypicalMm);
                var to = Smaller(doc, BuiltInCategory.OST_Walls, t.First(), typical, t.First().ThickMm);
                s.Items.Add(new Ve.Opportunity
                {
                    Key = "depth|wall|" + t.Key, Check = "Thicker than typical", Item = $"Wall type {t.Key} ({flagged.Count} of {t.Count()} wall(s))",
                    Current = $"{Ve.F(t.First().ThickMm, 0)} mm", Proposed = $"about {Ve.F(Math.Ceiling(typical / 25) * 25, 0)} mm (height / {Ve.F(rule.Ratio)}, {Ve.F(rule.MinMm, 0)} mm minimum)" + (to.Length > 0 ? $"; existing type {to}" : ""),
                    SavingCost = excess * CostPerM3(flagged.Select(x => x.f)), SavingCarbonKg = excess * Intensity(flagged.Select(x => x.f)), Confidence = "low",
                    Basis = $"{Ve.F(excess, 1)} m³ over the height/thickness rule of thumb", Risk = "cores, shear walls, retaining and fire walls are often sized by lateral load, earth pressure or fire, not height",
                    Ids = flagged.Select(x => x.f.Id).ToList(), ApplyKind = to.Length > 0 ? "change_type" : "", FromTypes = [t.Key], ToType = to,
                });
            }
        }

        // ---- 2. Many slightly different sizes
        if (On("rationalise"))
        {
            foreach (var cls in new[] { "beam", "column", "slab", "wall" })
            {
                var sizes = concrete.Where(f => f.Class == cls).GroupBy(f => f.Type).Select(gp =>
                {
                    var f0 = gp.First();
                    double[] mm = cls is "slab" or "wall" ? [f0.ThickMm] : [f0.WidthMm, f0.DepthMm];
                    return new Ve.Size(gp.Key, mm, gp.Count(), gp.Sum(f => f.ConcreteM3));
                }).ToList();
                foreach (var grp in Ve.Rationalise(sizes, rules.GroupMm, rules.GroupFraction))
                {
                    var members = concrete.Where(f => f.Class == cls && grp.Members.Any(m => m.Type == f.Type)).ToList();
                    var removed = grp.Members.Count - 1;
                    s.Groups.Add(grp); s.GroupClasses.Add((cls, grp));
                    s.Items.Add(new Ve.Opportunity
                    {
                        Key = "rationalise|" + cls + "|" + string.Join("+", grp.Members.Select(m => m.Type).OrderBy(n => n, StringComparer.Ordinal)),
                        Check = "Rationalise sizes", Item = $"{grp.Members.Count} {cls} sizes within {Ve.F(rules.GroupMm, 0)} mm ({members.Count} element(s))",
                        Current = string.Join("; ", grp.Members.Select(m => $"{m.Type} {Ve.SizeText(m.Mm)} x{m.Count}")),
                        Proposed = $"one size {Ve.SizeText(grp.Target)}" + (grp.TargetType.Length > 0 ? $" ({grp.TargetType})" : " (make the type first)"),
                        SavingCost = rules.PerTypeSaving * removed - grp.ExtraM3 * CostPerM3(members), SavingCarbonKg = -grp.ExtraM3 * Intensity(members), Confidence = "low",
                        Basis = $"{removed} type(s) fewer; {Ve.F(grp.ExtraM3, 2)} m³ more concrete as every member goes up to the largest size" +
                                (rules.PerTypeSaving > 0 ? $"; {Money(rules.PerTypeSaving)} allowed per type removed" : "; the formwork and buildability benefit is not costed (rules.per_type_saving)"),
                        Risk = "heavier elements; check headroom and the loads on what supports them", Ids = members.Select(f => f.Id).ToList(),
                        ApplyKind = grp.TargetType.Length > 0 ? "change_type" : "", FromTypes = grp.Members.Select(m => m.Type).Where(n => n != grp.TargetType).ToList(), ToType = grp.TargetType,
                    });
                }
            }
        }

        // ---- 3. Concrete grades
        if (On("grades"))
        {
            var grades = concrete.Where(f => f.Grade.Length > 0).GroupBy(f => f.Grade).OrderBy(gp => gp.First().Cyl).ToList();
            var unknown = concrete.Count(f => f.Grade.Length == 0);
            if (unknown > 0) s.NotChecked.Add($"Concrete grade: {unknown} concrete element(s) have no strength class in their material or type name.");
            if (grades.Count > rules.MaxGrades)
                s.Items.Add(new Ve.Opportunity
                {
                    Key = "grades|mix", Check = "Concrete grade mix", Item = $"{grades.Count} concrete strength classes in use",
                    Current = string.Join(", ", grades.Select(gp => $"{gp.Key} {Ve.F(gp.Sum(f => f.ConcreteM3), 0)} m³")),
                    Proposed = $"{rules.MaxGrades} or fewer mixes", Confidence = "low", Basis = "fewer mixes simplify supply, testing and site control; not costed",
                    Risk = "durability and strength needs per element", Ids = grades.SelectMany(gp => gp).Select(f => f.Id).ToList(),
                });
            foreach (var gp in concrete.Where(f => f.Cyl != null).GroupBy(f => (f.Class, f.Grade)))
            {
                var typical = rules.Grades.TryGetValue(gp.Key.Class, out var tg) ? tg : "C32/40";
                var steps = Ve.GradeSteps(gp.First().Cyl!.Value, Carbon.CylinderStrength(typical) ?? 32);
                if (steps == 0) continue;
                var m3 = gp.Sum(f => f.ConcreteM3);
                var concreteRate = inp.Rates.Concrete(gp.Key.Class);
                s.Items.Add(new Ve.Opportunity
                {
                    Key = $"grade|{gp.Key.Class}|{gp.Key.Grade}", Check = "Concrete grade above typical", Item = $"{gp.Key.Class} in {gp.Key.Grade} ({gp.Count()} element(s), {Ve.F(m3, 1)} m³)",
                    Current = gp.Key.Grade, Proposed = typical,
                    SavingCost = m3 * concreteRate * rules.GradeStepPremium * steps,
                    SavingCarbonKg = m3 * (Ve.ConcreteCarbonPerM3(gp.Key.Grade, inp.Factors, inp.CarbonOptions, inp.Custom) - Ve.ConcreteCarbonPerM3(typical, inp.Factors, inp.CarbonOptions, inp.Custom)),
                    Confidence = "low", Basis = $"{steps} strength class step(s) at {Ve.F(rules.GradeStepPremium * 100, 0)}% of the concrete rate each; carbon from {inp.CarbonSource}",
                    Risk = "strength, exposure class, early-age strength for the programme and fire may need the higher grade", Ids = gp.Select(f => f.Id).ToList(),
                    ApplyKind = "grade", ToGrade = typical,
                });
            }
        }

        // ---- 4. Reinforcement density (modelled bars only)
        if (On("rebar"))
        {
            var modelled = concrete.Where(f => f.RebarModelled).ToList();
            if (modelled.Count == 0) s.NotChecked.Add("Reinforcement density: no modelled reinforcement (assumed rates are not compared).");
            foreach (var t in modelled.GroupBy(f => (f.Class, f.Type)))
            {
                var typical = rules.Rebar.TryGetValue(t.Key.Class, out var r) ? r : 100;
                var kg = t.Sum(f => f.RebarKg); var m3 = t.Sum(f => f.ConcreteM3);
                var excess = Ve.ExcessRebarKg(kg, m3, typical, rules.RebarTolerance);
                if (excess <= 0) continue;
                s.Items.Add(new Ve.Opportunity
                {
                    Key = $"rebar|{t.Key.Class}|{t.Key.Type}", Check = "Reinforcement above typical", Item = $"{t.Key.Class} type {t.Key.Type} ({t.Count()} element(s))",
                    Current = $"{Ve.F(kg / m3, 0)} kg/m³", Proposed = $"about {Ve.F(typical, 0)} kg/m³ (typical {t.Key.Class})",
                    SavingCost = excess * inp.Rates.RebarKg, SavingCarbonKg = excess * Ve.SteelCarbonPerKg("rebar", inp.Factors, inp.Custom), Confidence = "medium",
                    Basis = $"{Ve.F(excess / 1000, 2)} t of modelled reinforcement above the typical density", Risk = "high density may follow from loads, crack control, seismic detailing or laps",
                    Ids = t.Select(f => f.Id).ToList(),
                });
            }
        }

        // ---- 5. Steel tonnage per m²
        if (On("steel"))
        {
            var steel = facts.Where(f => f.Steel && f.Class is "beam" or "column").ToList();
            var kg = steel.Sum(f => f.SteelKg);
            if (steel.Count > 0 && s.FloorAreaM2 <= 0) s.NotChecked.Add("Steel tonnage: no floor area; give gia_m2.");
            else if (steel.Count > 0)
            {
                var perM2 = kg / s.FloorAreaM2;
                if (perM2 > rules.SteelKgPerM2 * (1 + rules.SteelTolerance))
                {
                    var excess = (perM2 - rules.SteelKgPerM2) * s.FloorAreaM2;
                    s.Items.Add(new Ve.Opportunity
                    {
                        Key = "steel|frame", Check = "Steel tonnage above typical", Item = $"Steel frame: {Ve.F(kg / 1000, 1)} t over {Ve.F(s.FloorAreaM2, 0)} m²",
                        Current = $"{Ve.F(perM2, 0)} kg/m²", Proposed = $"about {Ve.F(rules.SteelKgPerM2, 0)} kg/m²",
                        SavingCost = excess / 1000 * inp.Rates.SteelT, SavingCarbonKg = excess * Ve.SteelCarbonPerKg("section", inp.Factors, inp.Custom), Confidence = "low",
                        Basis = $"{Ve.F(excess / 1000, 1)} t above a typical frame; floor area {(args.OptDouble("gia_m2") is null ? "from the slabs (approximate)" : "given")}",
                        Risk = "long spans, transfer, vibration, plant loads or lateral bracing can need more steel", Ids = steel.Select(f => f.Id).ToList(),
                    });
                }
                else s.Assumptions.Add($"Steel frame {Ve.F(perM2, 0)} kg/m² is within the typical {Ve.F(rules.SteelKgPerM2, 0)} kg/m².");
            }
        }

        // ---- 6. Formwork complexity per level
        if (On("formwork"))
        {
            var openings = new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>()
                .Where(o => o.Host is Floor).Select(o => (level: Measure.LevelName(doc, o.Host), id: o.Id.Value))
                .Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ShaftOpening).WhereElementIsNotElementType().Select(o => (level: Measure.LevelName(doc, o), id: o.Id.Value)))
                .ToList();
            var levels = concrete.Where(f => f.Class == "slab").GroupBy(f => f.Level).Select(gp =>
            {
                var steps = Math.Max(0, gp.Select(f => Math.Round(f.ThickMm)).Distinct().Count() - 1);
                var drops = gp.Count(f => System.Text.RegularExpressions.Regex.IsMatch(f.Type, "drop|thicken|capital|haunch", System.Text.RegularExpressions.RegexOptions.IgnoreCase) || (f.AreaM2 > 0 && f.AreaM2 < 12));
                var ops = openings.Count(o => o.level == gp.Key);
                var depths = Math.Max(0, concrete.Where(f => f.Class == "beam" && f.Level == gp.Key && f.DepthMm > 0).Select(f => Math.Round(f.DepthMm)).Distinct().Count() - 1);
                var ids = gp.Select(f => f.Id).Concat(openings.Where(o => o.level == gp.Key).Select(o => o.id)).ToList();
                return (level: gp.Key, steps, drops, ops, depths, score: steps + drops + ops + depths, ids);
            }).OrderByDescending(x => x.score).ToList();
            s.Formwork = levels.Select(l => (l.level, l.steps, l.drops, l.ops, l.depths, l.score)).ToList();
            var median = Ve.Median(levels.Select(l => (double)l.score));
            foreach (var l in levels.Where(l => l.score >= 5 && l.score > median * 1.5 + 1e-9))
            {
                var extra = l.score - median;
                s.Items.Add(new Ve.Opportunity
                {
                    Key = "formwork|" + l.level, Check = "Formwork complexity", Item = $"Level {l.level}: {l.score} soffit features",
                    Current = $"{l.steps} thickness step(s), {l.drops} drop(s), {l.ops} slab opening(s), {l.depths + (l.depths > 0 ? 1 : 0)} beam depth(s)",
                    Proposed = $"simplify towards the typical level ({Ve.F(median, 0)} features): one slab thickness, fewer drops, openings grouped",
                    SavingCost = extra * rules.FeatureFormworkM2 * inp.Rates.FormworkM2, Confidence = "low",
                    Basis = $"{Ve.F(extra, 0)} feature(s) above the median level at {Ve.F(rules.FeatureFormworkM2, 0)} m² of formwork each", Risk = "openings and steps usually follow services and architecture",
                    Ids = l.ids,
                });
            }
        }

        // ---- 7. Over-specified against the spec
        if (args.Has("spec") || args.OptString("spec_file") != null) Spec(app, doc, args, inp, concrete, s);

        s.Items = Ve.Rank(s.Items, args.OptDouble("cost_weight") ?? 0.5, args.OptDouble("carbon_weight") ?? 0.5);
        s.Assumptions.InsertRange(0, Ve.Describe(rules));
        s.Assumptions.Add("Rates: " + string.Join(", ", inp.Rates.From.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")) +
                          (inp.Rates.AnyPlaceholder ? ". Placeholder rates are generic round numbers in no particular currency: give rates_file (cost_estimate's rate table) for real costs." : "."));
        s.Assumptions.AddRange(inp.Notes);
        s.Assumptions.Add($"Carbon: A1-A3 from the {inp.CarbonSource}, cement replacement {Carbon.ReplacementLabel(inp.CarbonOptions.Replacement)} where the name gives none; reinforcement modelled where present, else the typical rates above.");
        s.Assumptions.Add(inp.DesignOption is null ? "Scope: the main model and the primary design options." : $"Scope: design option(s) named like '{inp.DesignOption}'.");
        return s;
    }

    /// <summary>Over-specified against the spec: elements above a requirement's minimum by more than the margin, and concrete above a grade the spec names.</summary>
    private static void Spec(UIApplication app, Document doc, ToolArgs args, Inputs inp, List<Fact> concrete, Scan s)
    {
        var list = new List<JsonElement>();
        if (args.OptString("spec_file") is { } file)
        {
            if (!File.Exists(file)) throw new ArgumentException($"File not found: {file}");
            using var d = JsonDocument.Parse(File.ReadAllText(file));
            var root = d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement : d.RootElement.GetProperty("requirements");
            list.AddRange(root.EnumerateArray().Select(r => r.Clone()));
        }
        list.AddRange(args.Array("spec"));
        // The compliance side is spec_check's job: its counts go in the summary.
        var res = Drafting.Call(app, new SpecCheckTool(), new { requirements = list, folder = Checks.Folder(args) });
        if (!res.IsError)
            using (var j = JsonDocument.Parse(res.Text))
                s.SpecSummary = new { met = j.RootElement.GetProperty("met").GetInt32(), notMet = j.RootElement.GetProperty("notMet").GetInt32(), file = j.RootElement.GetProperty("file").GetString() };
        else s.SpecSummary = new { error = res.Text };

        var n = 0;
        foreach (var r in list)
        {
            n++;
            string S(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = S(r, "id").Length > 0 ? S(r, "id") : $"R{n}";
            var text = S(r, "text");
            // A grade the spec names for a class of element.
            if (Carbon.CylinderStrength(text) is { } specCyl && Ve.ClassFromWords(text) is { } cls)
            {
                var over = concrete.Where(f => f.Class == cls && f.Cyl is { } c && c > specCyl + 0.5).ToList();
                if (over.Count > 0)
                {
                    var spec = GradeName(specCyl);
                    var m3 = over.Sum(f => f.ConcreteM3);
                    s.Items.Add(new Ve.Opportunity
                    {
                        Key = "spec|" + id, Check = "Over-specified against the spec", Item = $"{id}: {cls} above the spec's {spec} ({over.Count} element(s))",
                        Current = string.Join(", ", over.Select(f => f.Grade).Distinct()), Proposed = spec + " as the spec requires",
                        SavingCost = over.Sum(f => f.ConcreteM3 * inp.Rates.Concrete(cls) * inp.Rules.GradeStepPremium * Ve.GradeSteps(f.Cyl!.Value, specCyl)),
                        SavingCarbonKg = over.Sum(f => f.ConcreteM3 * (Ve.ConcreteCarbonPerM3(f.Grade, inp.Factors, inp.CarbonOptions, inp.Custom) - Ve.ConcreteCarbonPerM3(spec, inp.Factors, inp.CarbonOptions, inp.Custom))),
                        Confidence = "medium", Basis = $"spec '{Short(text)}'; {Ve.F(m3, 1)} m³ at a higher class", Risk = "the spec may set a minimum only; check why the higher grade was chosen",
                        Ids = over.Select(f => f.Id).ToList(), ApplyKind = "grade", ToGrade = spec,
                    });
                }
            }
            // A numeric minimum on a parameter.
            if (r.TryGetProperty("rule", out var rule) && rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number
                && S(rule, "parameter") is { Length: > 0 } param && S(rule, "category") is { Length: > 0 } catName)
            {
                BuiltInCategory bic;
                try { bic = RevitHelpers.ParseCategory(doc, catName); } catch (ArgumentException) { continue; }
                var min = mn.GetDouble();
                var over = new List<(Element e, double v)>();
                foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                    if (Number(doc, e, param, S(rule, "unit")) is { } v && v > min * (1 + inp.Rules.SpecMargin)) over.Add((e, v));
                if (over.Count == 0) continue;
                // A size parameter on concrete: the volume over the minimum is the saving.
                var size = System.Text.RegularExpressions.Regex.IsMatch(param, @"thick|depth|width|^h$|^b$|^d$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var byId = concrete.ToDictionary(f => f.Id);
                var excess = size ? over.Where(x => byId.ContainsKey(x.e.Id.Value)).Sum(x => byId[x.e.Id.Value].ConcreteM3 * (x.v - min) / x.v) : 0;
                var hit = over.Select(x => byId.TryGetValue(x.e.Id.Value, out var f) ? f : null).Where(f => f != null).ToList();
                double PerM3(Func<Fact, double> v) { var m3 = hit.Sum(f => f!.ConcreteM3); return m3 > 0 ? hit.Sum(f => v(f!)) / m3 : 0; }
                s.Items.Add(new Ve.Opportunity
                {
                    Key = "spec|" + id + "|" + param, Check = "Over-specified against the spec", Item = $"{id}: {catName} {param} more than {Ve.F(inp.Rules.SpecMargin * 100, 0)}% above the minimum {Ve.F(min)} ({over.Count} element(s))",
                    Current = $"{Ve.F(over.Min(x => x.v))} to {Ve.F(over.Max(x => x.v))}", Proposed = $"nearer {Ve.F(min)}",
                    SavingCost = excess * PerM3(f => f.Cost), SavingCarbonKg = excess * PerM3(f => f.CarbonKg), Confidence = "low",
                    Basis = size && excess > 0 ? $"{Ve.F(excess, 2)} m³ of concrete above the minimum; spec '{Short(text)}'" : $"spec '{Short(text)}'; not costed",
                    Risk = "a minimum is not a target: other requirements may set the value", Ids = over.Select(x => x.e.Id.Value).ToList(),
                });
            }
        }
    }

    private static string Short(string s) => s.Length > 80 ? s.Substring(0, 80) + "..." : s;

    /// <summary>A parameter's number on the instance or type: lengths in mm (m with unit m), areas in m².</summary>
    private static double? Number(Document doc, Element e, string name, string unit)
    {
        var p = e.LookupParameter(name) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(name);
        if (p is null) return null;
        if (p.StorageType == StorageType.Integer) return p.AsInteger();
        if (p.StorageType != StorageType.Double) return p.StorageType == StorageType.String && double.TryParse(p.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sv) ? sv : null;
        var spec = p.Definition.GetDataType();
        var raw = p.AsDouble();
        return spec == SpecTypeId.Length ? UnitUtils.ConvertFromInternalUnits(raw, unit == "m" ? UnitTypeId.Meters : UnitTypeId.Millimeters)
            : spec == SpecTypeId.Area ? UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.SquareMeters) : raw;
    }

    /// <summary>An existing type of the same kind (same family, same width for beams) at or above the typical size and below the current one.</summary>
    private static string Smaller(Document doc, BuiltInCategory cat, Fact f, double typicalMm, double currentMm)
    {
        var best = ("", double.MaxValue);
        foreach (var t in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsElementType().Cast<ElementType>())
        {
            double size, width = 0;
            switch (t)
            {
                case FloorType ft when cat == BuiltInCategory.OST_Floors: if (ft.IsFoundationSlab) continue; size = (ft.GetCompoundStructure()?.GetWidth() ?? 0) * 304.8; break;
                case WallType wt when cat == BuiltInCategory.OST_Walls: if (wt.Kind != WallKind.Basic) continue; size = wt.Width * 304.8; break;
                case FamilySymbol fs when fs.FamilyName == f.Family:
                    size = (fs.LookupParameter("h") ?? fs.LookupParameter("Depth") ?? fs.LookupParameter("H"))?.AsDouble() * 304.8 ?? 0;
                    width = (fs.LookupParameter("b") ?? fs.LookupParameter("Width") ?? fs.LookupParameter("B"))?.AsDouble() * 304.8 ?? 0;
                    if (Math.Abs(width - f.WidthMm) > 1) continue;
                    break;
                default: continue;
            }
            if (size >= typicalMm - 1 && size < currentMm - 1 && size < best.Item2) best = (t.Name, size);
        }
        return best.Item1;
    }

    // ---------------------------------------------------------------- per-model store

    public static string Store(Document doc) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "ve", Checks.ModelKey(doc));

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private sealed class Settings { public string? RatesFile { get; set; } }

    /// <summary>The rate table (rates_file) remembered for this model, or null.</summary>
    public static string? SavedRatesFile(Document doc)
    {
        try
        {
            var file = Path.Combine(Store(doc), "settings.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file))?.RatesFile : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void RememberRatesFile(Document doc, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(Store(doc));
            File.WriteAllText(Path.Combine(Store(doc), "settings.json"), JsonSerializer.Serialize(new Settings { RatesFile = Path.GetFullPath(path) }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    /// <summary>Whether two file paths name the same file (full paths, case-insensitive); false when either is empty or bad.</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            string N(string x) => Path.GetFullPath(x.Trim().Trim('"')).TrimEnd('\\', '/');
            return string.Equals(N(a!), N(b!), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static Ve.Register LoadRegister(Document doc)
    {
        var file = Path.Combine(Store(doc), "register.json");
        if (!File.Exists(file)) return new Ve.Register { Model = doc.Title };
        try { return JsonSerializer.Deserialize<Ve.Register>(File.ReadAllText(file)) ?? new Ve.Register { Model = doc.Title }; }
        catch (JsonException ex) { throw new InvalidOperationException($"The VE register {file} can't be read ({ex.Message}); fix or move it, and it starts again empty."); }
    }

    public static string SaveRegister(Document doc, Ve.Register reg)
    {
        Directory.CreateDirectory(Store(doc));
        var file = Path.Combine(Store(doc), "register.json");
        reg.Model = doc.Title;
        // Write then swap, so a failed write never leaves half a register.
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(reg, Indented));
        if (File.Exists(file)) File.Replace(file + ".tmp", file, null);
        else File.Move(file + ".tmp", file);
        return file;
    }

    public static void SaveLast(Document doc, List<Ve.Opportunity> items)
    {
        Directory.CreateDirectory(Store(doc));
        File.WriteAllText(Path.Combine(Store(doc), "last.json"), JsonSerializer.Serialize(items, Indented));
    }

    public static List<Ve.Opportunity>? LoadLast(Document doc)
    {
        var file = Path.Combine(Store(doc), "last.json");
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<Ve.Opportunity>>(File.ReadAllText(file)) : null; }
        catch (JsonException) { return null; }
    }

    public static (string csv, string? xlsx) WriteRegister(Document doc, ToolArgs args, Ve.Register reg) =>
        CarbonRun.Write(doc, args, "VE register", [("VE register", Ve.RegisterRows(reg))]);

    /// <summary>Totals for a set of elements, for before and after.</summary>
    public sealed record Totals(int Elements, double ConcreteM3, double RebarKg, double SteelKg, double Cost, double CarbonKg);

    public static Totals Sum(IEnumerable<Fact> facts)
    {
        var l = facts.ToList();
        return new Totals(l.Count, l.Sum(f => f.ConcreteM3), l.Sum(f => f.RebarKg), l.Sum(f => f.SteelKg), l.Sum(f => f.Cost), l.Sum(f => f.CarbonKg));
    }

    public static object Show(Totals t) => new
    {
        elements = t.Elements, concreteM3 = Math.Round(t.ConcreteM3, 2), rebarT = Math.Round(t.RebarKg / 1000, 3), steelT = Math.Round(t.SteelKg / 1000, 3),
        cost = Math.Round(t.Cost, 0), tCO2e = Math.Round(t.CarbonKg / 1000, 2),
    };
}

public sealed class VeOpportunitiesTool : IRevitTool
{
    public string Name => "ve_opportunities";
    public string Description =>
        "Value engineering screening (read-only): scans the structure for where cost or material could be saved and ranks it " +
        "by estimated cost and embodied carbon saving and confidence. Checks slabs, beams and walls thicker than typical " +
        "span/depth ratios (configurable rules of thumb), many slightly different sizes that could be rationalised (with " +
        "suggested groups), the concrete grade mix and grades above typical, modelled reinforcement density against typical " +
        "kg/m³, steel tonnage per m², formwork complexity per level (steps, drops, openings), and over-specification against a " +
        "spec if given (as spec_check takes it). Rates from cost_estimate's rate table, carbon from the embodied carbon factors. " +
        "Each item lists its element ids; results to Excel and CSV and into the VE register (ve_register), ids kept across runs. " +
        "A screening aid, not design: every item must be confirmed by an engineer.";
    public string PropertiesJson => "{" + VeRun.InputsJson + """
      ,
      "checks": {"type": "array", "items": {"type": "string", "enum": ["depth", "rationalise", "grades", "rebar", "steel", "formwork"]}, "description": "Checks to run (default all; the spec check runs when spec or spec_file is given)."},
      "span_m": {"type": "number", "description": "Typical slab span in m. Default: from the column grid."},
      "slab_system": {"type": "string", "enum": ["auto", "flat_slab", "slab_on_beams", "pt_slab"], "description": "Which slab ratio applies (default auto: slab_on_beams when there are concrete beams, else flat_slab)."},
      "gia_m2": {"type": "number", "description": "Floor area for steel kg/m² (default: the suspended slabs' area)."},
      "spec": {"type": "array", "items": {"type": "object"}, "description": "Requirements as spec_check takes them ({id, text, rule: {category, parameter, min}}); a text naming a grade and an element (e.g. 'Slabs C32/40') flags higher grades."},
      "spec_file": {"type": "string", "description": "A saved spec_check checklist (JSON)."},
      "design_option": {"type": "string", "description": "Scan only the elements in the design option(s) whose name contains this. Default: main model and primary options."},
      "cost_weight": {"type": "number", "description": "Weight of cost in the ranking (default 0.5)."},
      "carbon_weight": {"type": "number", "description": "Weight of carbon in the ranking (default 0.5)."},
      "update_register": {"type": "boolean", "description": "Merge the results into this model's VE register (default true)."},
      "top": {"type": "integer", "description": "Items in the reply (default 25; the files have all)."},
      "excel": {"type": "boolean", "description": "Also write an .xlsx workbook (default true)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var inp = VeRun.Read(doc, args);
        var scan = VeRun.Screen(app, doc, args, inp);
        var date = Checks.Now;
        string? registerFile = null;
        (int Added, int Updated, int NotFound)? merged = null;
        if (args.OptBool("update_register", true))
        {
            var reg = VeRun.LoadRegister(doc);
            merged = Ve.Merge(reg, scan.Items, date);
            registerFile = VeRun.SaveRegister(doc, reg);
        }
        VeRun.SaveLast(doc, scan.Items);

        var cur = inp.Rates.Currency;
        var rows = new List<object?[]> { new object?[] { "Rank", "VE id", "Check", "Item", "Current", "Proposed", $"Saving (cost{(cur.Length > 0 ? " " + cur : "")})", "Saving tCO2e", "Confidence", "Elements", "Basis", "Risk", "Score" } };
        rows.AddRange(scan.Items.Select(o => new object?[] { o.Rank, o.RegisterId, o.Check, o.Item, o.Current, o.Proposed, Math.Round(o.SavingCost, 0), Math.Round(o.SavingCarbonKg / 1000, 3), o.Confidence, o.Ids.Count, o.Basis, o.Risk, o.Score }));
        var ids = new List<object?[]> { new object?[] { "Rank", "VE id", "Check", "Element id" } };
        foreach (var o in scan.Items) ids.AddRange(o.Ids.Select(id => new object?[] { o.Rank, o.RegisterId, o.Check, id }));
        var groups = new List<object?[]> { new object?[] { "Class", "Target size", "Target type", "Member type", "Member size", "Elements", "Volume m3", "Extra m3 for the group" } };
        foreach (var (cls, gp) in scan.GroupClasses)
            groups.AddRange(gp.Members.Select(m => new object?[] { cls, Ve.SizeText(gp.Target), gp.TargetType, m.Type, Ve.SizeText(m.Mm), m.Count, Math.Round(m.M3, 2), Math.Round(gp.ExtraM3, 2) }));
        var formwork = new List<object?[]> { new object?[] { "Level", "Thickness steps", "Drops", "Slab openings", "Extra beam depths", "Features" } };
        formwork.AddRange(scan.Formwork.Select(f => new object?[] { f.Level, f.Steps, f.Drops, f.Openings, f.BeamDepths, f.Score }));
        var notes = new List<object?[]> { new object?[] { "Assumptions and caution" }, new object?[] { Ve.Caution } };
        notes.AddRange(scan.Assumptions.Select(a => new object?[] { a }));
        if (scan.NotChecked.Count > 0) { notes.Add([""]); notes.Add(["Not checked"]); notes.AddRange(scan.NotChecked.Select(a => new object?[] { a })); }
        var (csv, xlsx) = CarbonRun.Write(doc, args, "VE opportunities",
            [("Opportunities", rows), ("Element ids", ids), ("Rationalise groups", groups), ("Formwork by level", formwork), ("Assumptions", notes)]);

        var top = (int)Math.Max(1, Math.Min(200, args.OptLong("top") ?? 25));
        return ToolResult.Ok(new
        {
            caution = Ve.Caution,
            elementsScanned = scan.Elements, opportunities = scan.Items.Count,
            totalPotentialSaving = new
            {
                cost = Math.Round(scan.Items.Where(o => o.SavingCost > 0).Sum(o => o.SavingCost), 0), currency = cur,
                tCO2e = Math.Round(scan.Items.Where(o => o.SavingCarbonKg > 0).Sum(o => o.SavingCarbonKg) / 1000, 2),
                note = "Items overlap (a slab can be both thick and high grade): the sum is an upper bound, not a total to report.",
            },
            ranked = scan.Items.Take(top).Select(o => new
            {
                o.Rank, veId = o.RegisterId.Length > 0 ? o.RegisterId : null, o.Check, o.Item, o.Current, o.Proposed,
                savingCost = Math.Round(o.SavingCost, 0), savingTCO2e = Math.Round(o.SavingCarbonKg / 1000, 2), o.Confidence, elements = o.Ids.Count,
                elementIds = o.Ids.Take(50), o.Basis, o.Risk, canApply = o.ApplyKind.Length > 0 ? o.ApplyKind : null,
            }),
            notChecked = scan.NotChecked, assumptions = scan.Assumptions, spec = scan.SpecSummary,
            register = merged is { } m ? new { file = registerFile, m.Added, m.Updated, notInThisScan = m.NotFound } : null,
            files = new { csv, xlsx },
            next = "Show them with ve_view (a new coloured 3D view), compare systems with ve_compare_options, track decisions with ve_register, and try one on a saved copy or design option with ve_apply.",
        });
    }
}

public sealed class VeCompareOptionsTool : IRevitTool
{
    public string Name => "ve_compare_options";
    public string Description =>
        "Value engineering (read-only): compares structural alternatives side by side for a grid, span and load: RC flat slab, " +
        "RC beam and slab, PT flat slab, precast hollow-core, steel frame with composite deck, and for foundations raft vs piles. " +
        "Each is sized by documented, configurable rules of thumb, or measured from the model ('model') or a Revit design option. " +
        "Columns: sizes, quantities per m² (concrete, precast, rebar, steel, formwork, piling), cost (rate table), embodied " +
        "carbon (A1-A3), a rough programme index and risks. Excel and CSV. A screening comparison, not design.";
    public string PropertiesJson => "{" + VeRun.InputsJson + """
      ,
      "options": {"type": "array", "items": {"type": "string"}, "description": "Options: flat_slab, beam_and_slab, pt_flat_slab, hollow_core, steel_composite, raft, piles, model (the model's floors and beams), or 'option:<design option name>'. Default: the five floor systems, plus model when it has slabs."},
      "span_x": {"type": "number", "description": "Grid span in m (default from the column grid, else 8)."},
      "span_y": {"type": "number", "description": "Grid span the other way in m (default from the column grid, else span_x)."},
      "imposed_kpa": {"type": "number", "description": "Imposed load kPa (default 3)."},
      "superimposed_kpa": {"type": "number", "description": "Finishes, services and partitions kPa (default 1.5)."},
      "floors": {"type": "integer", "description": "Number of floors (for totals and foundations; default 1)."},
      "floor_area_m2": {"type": "number", "description": "Area of one floor in m² (for totals; default the model's slab area per level, else one bay)."},
      "assumptions": {"type": "object", "description": "Rule-of-thumb sizing values to replace, e.g. {\"flat_slab_ratio\": 30, \"pile_capacity_kn\": 2000, \"steel_kg_m2_per_m_span\": 3}."},
      "excel": {"type": "boolean", "description": "Also write an .xlsx workbook (default true)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var inp = VeRun.Read(doc, args);
        var a = Ve.ReadAssumptions(args.Has("assumptions") ? args.Raw["assumptions"] : null);
        var grid = Ve.GridSpans(VeRun.ColumnPoints(doc));
        var g = new Ve.Grid
        {
            SpanX = args.OptDouble("span_x") ?? grid?.Long ?? 8,
            ImposedKpa = args.OptDouble("imposed_kpa") ?? 3, SuperimposedKpa = args.OptDouble("superimposed_kpa") ?? 1.5,
            Floors = (int)Math.Max(1, args.OptLong("floors") ?? 1),
        };
        g.SpanY = args.OptDouble("span_y") ?? (args.Has("span_x") ? g.SpanX : grid?.Short ?? g.SpanX);
        var main = VeRun.Facts(doc, inp).Where(f => f.Class is "slab" or "beam").ToList();
        var slabArea = main.Where(f => f.Class == "slab").Sum(f => f.AreaM2);
        var levels = Math.Max(1, main.Where(f => f.Class == "slab").Select(f => f.Level).Distinct().Count());
        g.FloorAreaM2 = args.OptDouble("floor_area_m2") ?? (slabArea > 0 ? slabArea / levels : null);
        var keys = args.StringList("options");
        if (keys.Count == 0) keys = [.. Ve.OptionKeys.Take(5), .. slabArea > 0 ? new[] { "model" } : []];

        var options = new List<Ve.Option>();
        var notes = new List<string>();
        foreach (var key in keys)
        {
            var k = key.Trim();
            if (k.Equals("model", StringComparison.OrdinalIgnoreCase) || k.StartsWith("option:", StringComparison.OrdinalIgnoreCase))
            {
                var name = k.StartsWith("option:", StringComparison.OrdinalIgnoreCase) ? k.Substring(7).Trim() : null;
                var facts = name is null ? main : VeRun.Facts(doc, new VeRun.Inputs { Rules = inp.Rules, Rates = inp.Rates, Factors = inp.Factors, CarbonOptions = inp.CarbonOptions, Custom = inp.Custom, DesignOption = name })
                    .Where(f => f.Class is "slab" or "beam").ToList();
                var area = facts.Where(f => f.Class == "slab").Sum(f => f.AreaM2);
                if (area <= 0) { notes.Add(name is null ? "model: no slabs to measure." : $"option:{name}: no slabs in a design option named like that."); continue; }
                var grades = facts.Where(f => f.Grade.Length > 0).GroupBy(f => f.Grade).OrderByDescending(x => x.Sum(f => f.ConcreteM3)).Select(x => x.Key).FirstOrDefault();
                options.Add(new Ve.Option
                {
                    Key = name is null ? "model" : "option:" + name, Name = name is null ? "This model (slabs and beams)" : "Design option " + name,
                    Source = name is null ? "model" : "design option", Sizes = $"{facts.Count(f => f.Class == "slab")} slab(s), {facts.Count(f => f.Class == "beam")} beam(s) over {Ve.F(area, 0)} m²",
                    ConcreteM3 = facts.Sum(f => f.ConcreteM3) / area, RebarKg = facts.Sum(f => f.RebarKg) / area, SteelKg = facts.Sum(f => f.SteelKg) / area,
                    FormworkM2 = 1, Grade = grades ?? "C32/40", Programme = double.NaN,
                    Risks = [facts.Any(f => !f.RebarModelled && f.ConcreteM3 > 0) ? "reinforcement assumed at typical rates where not modelled" : "reinforcement as modelled"],
                });
                continue;
            }
            options.Add(Ve.SizeOption(k, g, a));
        }
        if (options.Count == 0) throw new ArgumentException("Nothing to compare: " + string.Join(" ", notes));

        var floorArea = g.TotalAreaM2;
        var footprint = g.FloorAreaM2 ?? g.SpanX * g.SpanY;
        var rows = options.Select(o =>
        {
            var cost = Ve.Cost(o, inp.Rates);
            var carbon = Ve.CarbonKg(o, inp.Factors, inp.CarbonOptions, inp.Custom);
            var area = o.Foundation ? footprint : floorArea;
            return (o, cost, carbon, area);
        }).ToList();
        double Min(Func<(Ve.Option o, double cost, double carbon, double area), double> f, bool foundation) =>
            rows.Where(r => r.o.Foundation == foundation).Select(f).DefaultIfEmpty(0).Min();

        notes.InsertRange(0, inp.Notes);
        var cur = inp.Rates.Currency;
        var sheet = new List<object?[]> { new object?[] { "Option", "Source", "Sizes", "Per", "Concrete m3", "Grade", "Precast m3", "Rebar kg", "Steel kg", "Deck kg", "PT strand kg", "Formwork m2", "Piles m",
            $"Cost{(cur.Length > 0 ? " " + cur : "")}/m2", "kgCO2e/m2", "Area m2", "Total cost", "Total tCO2e", "Cost vs cheapest %", "Carbon vs lowest %", "Programme (flat slab = 1)", "Risks and notes" } };
        foreach (var r in rows)
        {
            var o = r.o;
            var minCost = Min(x => x.cost, o.Foundation); var minCarbon = Min(x => x.carbon, o.Foundation);
            sheet.Add([o.Name, o.Source, o.Sizes, o.Per, Math.Round(o.ConcreteM3, 3), o.Grade, Math.Round(o.PrecastM3, 3), Math.Round(o.RebarKg, 1), Math.Round(o.SteelKg, 1), Math.Round(o.DeckKg, 1),
                Math.Round(o.StrandKg, 1), Math.Round(o.FormworkM2, 2), Math.Round(o.PileM, 3), Math.Round(r.cost, 1), Math.Round(r.carbon, 1), Math.Round(r.area, 0),
                Math.Round(r.cost * r.area, 0), Math.Round(r.carbon * r.area / 1000, 1), minCost > 0 ? Math.Round((r.cost / minCost - 1) * 100, 0) : null,
                minCarbon > 0 ? Math.Round((r.carbon / minCarbon - 1) * 100, 0) : null, double.IsNaN(o.Programme) ? "as built" : Ve.F(o.Programme, 1), string.Join("; ", o.Risks)]);
        }
        var assumptions = new List<object?[]> { new object?[] { "Assumptions and caution" }, new object?[] { Ve.Caution },
            new object?[] { $"Grid {Ve.F(g.SpanX)} x {Ve.F(g.SpanY)} m{(grid != null && !args.Has("span_x") ? " (from the column grid)" : "")}, imposed {Ve.F(g.ImposedKpa)} kPa, superimposed {Ve.F(g.SuperimposedKpa)} kPa, {g.Floors} floor(s), floor area {Ve.F(floorArea, 0)} m²." },
            new object?[] { "Columns, cores, stairs, fire protection, finishes and temporary works are not counted in the floor systems. Programme index: frame time per floor relative to an RC flat slab (lower is faster), a rough indicator only." },
            new object?[] { "Rule-of-thumb sizing values: " + string.Join(", ", a.Select(kv => $"{kv.Key} {Ve.F(kv.Value, 3)}")) },
            new object?[] { "Rates: " + string.Join(", ", inp.Rates.From.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")) + (inp.Rates.AnyPlaceholder ? " (placeholders are generic round numbers: give rates_file)" : "") },
            new object?[] { $"Carbon: A1-A3, {inp.CarbonSource}; precast at {options.FirstOrDefault()?.PrecastGrade ?? "C40/50"}." } };
        assumptions.AddRange(notes.Select(n => new object?[] { n }));
        var (csv, xlsx) = CarbonRun.Write(doc, args, "VE options", [("Options", sheet), ("Assumptions", assumptions)]);

        return ToolResult.Ok(new
        {
            caution = Ve.Caution,
            grid = new { spanX = g.SpanX, spanY = g.SpanY, g.ImposedKpa, g.SuperimposedKpa, g.Floors, floorAreaM2 = Math.Round(floorArea, 0), fromColumnGrid = grid != null && !args.Has("span_x") },
            options = rows.Select(r => new
            {
                option = r.o.Name, r.o.Source, r.o.Sizes, per = r.o.Per,
                quantities = new { concreteM3 = Math.Round(r.o.ConcreteM3, 3), r.o.Grade, precastM3 = r.o.PrecastM3 > 0 ? Math.Round(r.o.PrecastM3, 3) : (double?)null, rebarKg = Math.Round(r.o.RebarKg, 1),
                    steelKg = r.o.SteelKg > 0 ? Math.Round(r.o.SteelKg + r.o.DeckKg, 1) : (double?)null, strandKg = r.o.StrandKg > 0 ? r.o.StrandKg : (double?)null, pilesM = r.o.PileM > 0 ? Math.Round(r.o.PileM, 3) : (double?)null },
                costPerM2 = Math.Round(r.cost, 1), kgCO2ePerM2 = Math.Round(r.carbon, 1), totalCost = Math.Round(r.cost * r.area, 0), totalTCO2e = Math.Round(r.carbon * r.area / 1000, 1),
                programme = double.IsNaN(r.o.Programme) ? null : (double?)r.o.Programme, risks = r.o.Risks,
            }),
            cheapest = rows.Where(r => !r.o.Foundation).OrderBy(r => r.cost).FirstOrDefault().o?.Name,
            lowestCarbon = rows.Where(r => !r.o.Foundation).OrderBy(r => r.carbon).FirstOrDefault().o?.Name,
            currency = cur, ratesFrom = inp.Rates.From, notes, files = new { csv, xlsx },
        });
    }
}

public sealed class VeApplyTool : IRevitTool
{
    public string Name => "ve_apply";
    public string Description =>
        "Applies a value engineering change to a SAVED COPY of the model (default; not for workshared models: run it in a " +
        "detached copy or use design_option) or to the elements in a Revit design " +
        "option, never silently to the main model: switch element types (e.g. a thinner slab or beam type), rationalise " +
        "sizes (many types to one), or change the concrete grade (material; type-level materials get a duplicated type so " +
        "other elements are not touched). Changes come from ve_opportunities items (rank or VE id) or are given directly. " +
        "Reports before and after quantities, cost and carbon. Each element is its own step; elements others own are skipped. " +
        "dry_run lists what would change. An engineer must confirm the result.";
    public string PropertiesJson => "{" + VeRun.InputsJson + """
      ,
      "target": {"type": "string", "enum": ["copy", "design_option", "main"], "description": "copy (default): a copy of the saved model, opened, changed, saved and closed (refused for workshared models: detach a copy and run it there, or use design_option); design_option: only elements in design_option; main: the open model, only with confirm_main_model."},
      "design_option": {"type": "string", "description": "With target design_option: the option's name (or part of it). Make the option and copy the elements into it in Revit first."},
      "confirm_main_model": {"type": "boolean", "description": "Required with target main: the user asked for the change in the main model itself."},
      "copy_path": {"type": "string", "description": "Where to save the copy (default: '<model> - VE <date>.rvt' in the output folder)."},
      "items": {"type": "array", "items": {"type": "string"}, "description": "ve_opportunities items to apply, by VE id (e.g. VE-004) or rank in the last scan."},
      "changes": {"type": "array", "items": {"type": "object", "properties": {
        "kind": {"type": "string", "enum": ["change_type", "grade"]},
        "element_ids": {"type": "array", "items": {"type": "integer"}},
        "from_type": {"type": "string", "description": "Instead of ids: every element of this type (with category)."},
        "category": {"type": "string"},
        "to_type": {"type": "string", "description": "change_type: the type to switch to (same category)."},
        "to_material": {"type": "string", "description": "grade: the concrete material to use (default: an existing concrete material of to_grade)."},
        "to_grade": {"type": "string", "description": "grade: the strength class, e.g. C32/40."}
      }, "required": ["kind"]}, "description": "Changes given directly."},
      "dry_run": {"type": "boolean", "description": "List the changes and the before quantities without changing anything (default false)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    private sealed record Change(long Id, string Kind, ElementId To, string What);

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var active = app.ActiveUIDocument.Document;
        var inp = VeRun.Read(active, args);
        var target = args.OptString("target") ?? "copy";
        var dry = args.OptBool("dry_run");
        if (target is not ("copy" or "design_option" or "main")) throw new ArgumentException("target is copy, design_option or main.");
        if (target == "main" && !args.OptBool("confirm_main_model"))
            throw new ArgumentException("target main changes the open model itself. Use target copy (a saved copy) or design_option; only when the user asked for the main model, set confirm_main_model true.");
        string? option = null;
        if (target == "design_option")
        {
            option = args.OptString("design_option") ?? throw new ArgumentException("Give design_option (its name or part of it). Make the option in Revit (Manage > Design Options) and copy the elements into it first.");
            var names = new FilteredElementCollector(active).OfClass(typeof(DesignOption)).Cast<DesignOption>().Select(o => o.Name).ToList();
            if (!names.Any(n => n.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0))
                throw new ArgumentException($"No design option named like '{option}'." + (names.Count > 0 ? " Options: " + string.Join(", ", names) + "." : " This model has no design options; use target copy."));
        }

        // What to do, in the open model's terms (element ids are the same in a copy of the file).
        var requests = new List<(string kind, List<long> ids, string? toType, string? toMaterial, string? toGrade, string from)>();
        if (args.StringList("items") is { Count: > 0 } picks)
        {
            var last = VeRun.LoadLast(active) ?? throw new ArgumentException("No ve_opportunities scan saved for this model: run ve_opportunities first.");
            foreach (var p in picks)
            {
                var o = last.FirstOrDefault(x => x.RegisterId.Equals(p.Trim(), StringComparison.OrdinalIgnoreCase) || x.Rank.ToString(CultureInfo.InvariantCulture) == p.Trim())
                        ?? throw new ArgumentException($"'{p}' is not a VE id or rank in the last scan.");
                if (o.ApplyKind.Length == 0) throw new ArgumentException($"{p} ({o.Item}) can't be applied automatically ({o.Check}): change it by hand or give changes.");
                requests.Add((o.ApplyKind, o.Ids, o.ToType, null, o.ToGrade, $"{(o.RegisterId.Length > 0 ? o.RegisterId : "#" + o.Rank)} {o.Item}"));
            }
        }
        foreach (var c in args.Array("changes"))
        {
            string? S(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var kind = S("kind") ?? throw new ArgumentException("Each change needs kind (change_type or grade).");
            var ids = c.TryGetProperty("element_ids", out var ei) && ei.ValueKind == JsonValueKind.Array ? ei.EnumerateArray().Select(x => x.GetInt64()).ToList() : [];
            if (ids.Count == 0 && S("from_type") is { } ft)
            {
                var cats = S("category") is { } cn ? new[] { RevitHelpers.ParseCategory(active, cn) } : VeRun.Categories;
                ids = cats.SelectMany(bc => new FilteredElementCollector(active).OfCategory(bc).WhereElementIsNotElementType()).Where(e => Measure.TypeName(e).Equals(ft, StringComparison.OrdinalIgnoreCase)).Select(e => e.Id.Value).ToList();
                if (ids.Count == 0) throw new ArgumentException($"No element of type '{ft}'.");
            }
            if (ids.Count == 0) throw new ArgumentException("Each change needs element_ids or from_type.");
            requests.Add((kind, ids, S("to_type"), S("to_material"), S("to_grade"), kind));
        }
        if (requests.Count == 0) throw new ArgumentException("Nothing to apply: give items (VE ids or ranks from ve_opportunities) or changes.");

        var warnings = new List<string>();
        Document doc = active;
        string? copyPath = null;
        var opened = false;
        // A dry run opens nothing, so it is allowed (it says the real run is refused).
        if (target == "copy" && active.IsWorkshared && !dry)
            throw new ArgumentException("This model is workshared, so ve_apply won't open or copy it (opening it again could act on the model you have open or on the central file). " +
                                        "Make a detached copy (e.g. a _detached file), open it and run ve_apply there, or use target=design_option.");
        if (target == "copy" && !dry)
        {
            if (string.IsNullOrEmpty(active.PathName)) throw new InvalidOperationException("Save the model first: ve_apply makes its copy from the saved file.");
            if (active.IsModified) warnings.Add("The open model has unsaved changes; the copy is made from the last saved file and does not have them.");
            if (args.OptString("copy_path") is { } given && !Path.IsPathRooted(given.Trim().Trim('"')))
                throw new ArgumentException("copy_path must be a full path (e.g. D:\\Models\\Tower - VE.rvt), not a relative name.");
            copyPath = args.OptString("copy_path") is { } cp ? OutputPaths.Checked(cp.Trim().Trim('"')) ?? cp : Path.Combine(Checks.Folder(args), $"{San(active.Title)} - VE {DateTime.Now:yyyy-MM-dd HHmm}.rvt");
            if (!copyPath.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) copyPath += ".rvt";
            copyPath = Path.GetFullPath(copyPath);
            if (VeRun.SamePath(copyPath, active.PathName)) throw new ArgumentException("copy_path is the open model's own file; give another copy_path.");
            Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
            if (File.Exists(copyPath)) throw new ArgumentException($"{copyPath} exists already; give another copy_path.");
            File.Copy(active.PathName, copyPath);
            doc = app.Application.OpenDocumentFile(copyPath);
            // Revit hands back the open document when the file is already open: never change or close that one.
            if (doc.Equals(active) || VeRun.SamePath(doc.PathName, active.PathName))
            {
                if (!doc.Equals(active)) try { doc.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                doc = active;
                throw new InvalidOperationException("Revit returned the open model instead of the copy, so nothing was changed. The copy file is at " + copyPath + "; open it and run ve_apply there.");
            }
            opened = true;
        }

        try
        {
            // Resolve each request into one change per element.
            var changes = new List<Change>();
            var skipped = new List<object>();
            var dupes = new Dictionary<(long type, long mat), ElementId>();
            foreach (var r in requests)
                foreach (var id in r.ids.Distinct())
                {
                    var e = doc.GetElement(new ElementId(id));
                    if (e is null) { skipped.Add(new { id, reason = "not in the model" }); continue; }
                    if (option != null && !VeRun.InOption(e, option)) { skipped.Add(new { id, reason = $"in the main model, not in design option '{option}'" }); continue; }
                    if (r.kind == "change_type")
                    {
                        var to = TypeNamed(doc, e, r.toType ?? throw new ArgumentException($"{r.from}: change_type needs to_type."));
                        if (to is null) { skipped.Add(new { id, reason = $"no {e.Category?.Name} type '{r.toType}'" }); continue; }
                        if (to.Id == e.GetTypeId()) { skipped.Add(new { id, reason = "already that type" }); continue; }
                        changes.Add(new Change(id, "change_type", to.Id, $"{Measure.TypeName(e)} -> {to.Name}"));
                    }
                    else if (r.kind == "grade")
                    {
                        var mat = MaterialFor(doc, r.toMaterial, r.toGrade);
                        if (mat is null) { skipped.Add(new { id, reason = $"no concrete material {(r.toMaterial ?? r.toGrade)} in the model; give to_material" }); continue; }
                        changes.Add(new Change(id, "grade", mat.Id, $"{Measure.MaterialName(doc, e)} -> {mat.Name}"));
                    }
                    else throw new ArgumentException($"Unknown change kind '{r.kind}' (change_type or grade).");
                }
            var ids = changes.Select(c => c.Id).Distinct().ToList();
            var before = VeRun.Sum(VeRun.Facts(doc, inp, ids));
            if (dry || changes.Count == 0)
                return ToolResult.Ok(new
                {
                    dryRun = dry, target, changes = changes.Count, list = changes.Take(100).Select(c => new { id = c.Id, c.Kind, c.What }), skipped = skipped.Take(50),
                    before = VeRun.Show(before), note = changes.Count == 0 ? "Nothing to change." : "Nothing was changed (dry run).", caution = Ve.Caution,
                    workshared = target == "copy" && active.IsWorkshared
                        ? "This model is workshared: the real run won't make a copy. Make a detached copy (e.g. a _detached file) and run ve_apply there, or use target=design_option." : null,
                });

            var batch = ItemBatch.Run(doc, "VE apply", changes, c => $"{c.Id}: {c.What}", c => [new ElementId(c.Id)], c =>
            {
                var e = doc.GetElement(new ElementId(c.Id)) ?? throw new InvalidOperationException("element gone");
                if (c.Kind == "change_type") { e.ChangeTypeId(c.To); return null; }
                // Grade: the instance's own material when it has one; else a copy of its type with the new material.
                if (e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM) is { IsReadOnly: false } ip) { ip.Set(c.To); return null; }
                var key = (e.GetTypeId().Value, c.To.Value);
                if (!dupes.TryGetValue(key, out var dupId) || doc.GetElement(dupId) is null)
                {
                    var type = doc.GetElement(e.GetTypeId()) as ElementType ?? throw new InvalidOperationException("no type to change");
                    var name = $"{type.Name} {((Material)doc.GetElement(c.To)).Name}";
                    var existing = new FilteredElementCollector(doc).OfClass(type.GetType()).Cast<ElementType>().FirstOrDefault(t => t.Name == name && t.Category?.Id == type.Category?.Id);
                    var dup = existing ?? type.Duplicate(name);
                    if (existing is null) SetTypeMaterial(dup, c.To);
                    dupes[key] = dupId = dup.Id;
                }
                e.ChangeTypeId(dupId);
                return null;
            }, borrow: target != "copy");

            var after = VeRun.Sum(VeRun.Facts(doc, inp, ids));
            if (opened) doc.Save();
            // Record it against the register items it came from.
            var veIds = args.StringList("items").Where(p => p.StartsWith("VE-", StringComparison.OrdinalIgnoreCase)).ToList();
            if (veIds.Count > 0 && batch.Done > 0)
            {
                var reg = VeRun.LoadRegister(active);
                foreach (var v in veIds)
                    try { var it = reg.Items.First(i => i.Id.Equals(v, StringComparison.OrdinalIgnoreCase)); Ve.Set(reg, it.Id, null, null, (it.Note.Length > 0 ? it.Note + "; " : "") + $"applied to {(copyPath ?? target)} {Checks.Now}", Checks.Now); }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
                VeRun.SaveRegister(active, reg);
            }
            double Pct(double b, double a) => b != 0 ? Math.Round((a - b) / b * 100, 1) : 0;
            return ToolResult.Ok(new
            {
                target, copy = copyPath, designOption = option, applied = batch.Summary(), skipped = skipped.Take(50),
                before = VeRun.Show(before), after = VeRun.Show(after),
                change = new { concreteM3 = Math.Round(after.ConcreteM3 - before.ConcreteM3, 2), cost = Math.Round(after.Cost - before.Cost, 0), costPercent = Pct(before.Cost, after.Cost),
                    tCO2e = Math.Round((after.CarbonKg - before.CarbonKg) / 1000, 2), carbonPercent = Pct(before.CarbonKg, after.CarbonKg) },
                warnings, caution = Ve.Caution,
                note = target == "copy" ? "The open model was not changed. Open the copy to review it." :
                       target == "design_option" ? "Only elements in the design option were changed (new types, if any, are added to the project). Ctrl+Z undoes it." : "The open model was changed; Ctrl+Z undoes it.",
            });
        }
        finally
        {
            if (opened)
                try { doc.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
    }

    private static string San(string s) => new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    /// <summary>A type of the element's category by name ("Type" or "Family: Type").</summary>
    private static ElementType? TypeNamed(Document doc, Element e, string name)
    {
        var cat = e.Category?.Id;
        return new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
            .Where(t => t.Category?.Id == cat && (e is not FamilyInstance || t is FamilySymbol))
            .FirstOrDefault(t => t.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) || $"{t.FamilyName}: {t.Name}".Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The material named, else an existing concrete material of the grade (the shortest name, most likely the plain one).</summary>
    private static Material? MaterialFor(Document doc, string? name, string? grade)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
        if (name != null) return all.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (grade is null || Carbon.CylinderStrength(grade) is not { } cyl) return null;
        return all.Where(m => Carbon.ClassOf(m.Name, m.MaterialClass) == "concrete" && Carbon.CylinderStrength(m.Name) is { } c && Math.Abs(c - cyl) < 0.5)
            .OrderBy(m => m.Name.Length).FirstOrDefault();
    }

    private static void SetTypeMaterial(ElementType type, ElementId material)
    {
        if (type.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM) is { IsReadOnly: false } p) { p.Set(material); return; }
        if (type is HostObjAttributes host && host.GetCompoundStructure() is { } cs)
        {
            var i = cs.StructuralMaterialIndex;
            if (i < 0) i = Enumerable.Range(0, cs.LayerCount).FirstOrDefault(k => cs.GetLayerFunction(k) == MaterialFunctionAssignment.Structure);
            cs.SetMaterialId(i, material);
            host.SetCompoundStructure(cs);
            return;
        }
        throw new InvalidOperationException($"type {type.Name} has no structural material to set");
    }
}

public sealed class VeRegisterTool : IRevitTool
{
    public string Name => "ve_register";
    public string Description =>
        "The model's value engineering register: exports it to Excel and CSV (ID, item, current, proposed, saving, carbon, risk, " +
        "status, owner, date), sets items' status (proposed, accepted, rejected), owner and note, adds items by hand, or merges " +
        "the last ve_opportunities scan. Kept per model, so ids and statuses carry over between runs and sessions. Reads the model only.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["export", "set", "add", "merge_last"], "description": "export (default) writes the register; set changes items; add adds items by hand; merge_last merges the last scan. Every action writes the files."},
      "updates": {"type": "array", "items": {"type": "object", "properties": {"id": {"type": "string"}, "status": {"type": "string", "enum": ["proposed", "accepted", "rejected"]}, "owner": {"type": "string"}, "note": {"type": "string"}}, "required": ["id"]}, "description": "set: the items to change."},
      "items": {"type": "array", "items": {"type": "object", "properties": {"item": {"type": "string"}, "current": {"type": "string"}, "proposed": {"type": "string"}, "saving": {"type": "number"}, "carbon_t": {"type": "number"}, "risk": {"type": "string"}, "owner": {"type": "string"}}, "required": ["item"]}, "description": "add: items by hand."},
      "status": {"type": "string", "enum": ["proposed", "accepted", "rejected"], "description": "Only list items with this status in the reply."},
      "excel": {"type": "boolean", "description": "Also write an .xlsx workbook (default true)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var reg = VeRun.LoadRegister(doc);
        var action = args.OptString("action") ?? "export";
        var date = Checks.Now;
        var changed = new List<string>();
        switch (action)
        {
            case "export": break;
            case "set":
                var updates = args.Array("updates").ToList();
                if (updates.Count == 0) throw new ArgumentException("set needs updates: [{id, status, owner, note}].");
                foreach (var u in updates)
                {
                    string? S(string k) => u.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    changed.Add(Ve.Set(reg, S("id") ?? throw new ArgumentException("Each update needs id."), S("status"), S("owner"), S("note"), date).Id);
                }
                break;
            case "add":
                foreach (var it in args.Array("items"))
                {
                    string S(string k) => it.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    double N(string k) => it.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                    changed.Add(Ve.AddManual(reg, S("item"), S("current"), S("proposed"), N("saving"), N("carbon_t") * 1000, S("risk"), S("owner"), date).Id);
                }
                if (changed.Count == 0) throw new ArgumentException("add needs items: [{item, current, proposed, saving, carbon_t, risk, owner}].");
                break;
            case "merge_last":
                var last = VeRun.LoadLast(doc) ?? throw new ArgumentException("No ve_opportunities scan saved for this model: run ve_opportunities first.");
                var m = Ve.Merge(reg, last, date);
                VeRun.SaveLast(doc, last);
                changed.Add($"{m.Added} added, {m.Updated} updated, {m.NotFound} not in the scan");
                break;
            default: throw new ArgumentException("action is export, set, add or merge_last.");
        }
        string? file = null;
        if (action != "export" || File.Exists(Path.Combine(VeRun.Store(doc), "register.json"))) file = VeRun.SaveRegister(doc, reg);
        var (csv, xlsx) = VeRun.WriteRegister(doc, args, reg);
        var filter = args.OptString("status");
        return ToolResult.Ok(new
        {
            action, changed, register = file, files = new { csv, xlsx },
            counts = Ve.Statuses.ToDictionary(s => s, s => reg.Items.Count(i => i.Status == s)),
            accepted = new { cost = Math.Round(reg.Items.Where(i => i.Status == "accepted").Sum(i => i.SavingCost), 0), tCO2e = Math.Round(reg.Items.Where(i => i.Status == "accepted").Sum(i => i.SavingCarbonKg) / 1000, 2) },
            items = reg.Items.Where(i => filter is null || i.Status == filter).Take(100).Select(i => new
            {
                i.Id, i.Item, i.Current, i.Proposed, saving = Math.Round(i.SavingCost, 0), tCO2e = Math.Round(i.SavingCarbonKg / 1000, 2), i.Risk, i.Status,
                owner = i.Owner.Length > 0 ? i.Owner : null, i.Date, inLastScan = i.Key.Length == 0 ? (bool?)null : i.InLastScan, note = i.Note.Length > 0 ? i.Note : null,
            }),
            caution = Ve.Caution,
        });
    }
}

public sealed class VeViewTool : IRevitTool
{
    public string Name => "ve_view";
    public string Description =>
        "Colours the value engineering opportunities in a NEW 3D view (existing views are not changed): elements of the top " +
        "ranked items red, the next amber, the rest yellow, from the last ve_opportunities scan (or a new scan when there is " +
        "none or rescan is true, with ve_opportunities' options).";
    public string PropertiesJson => "{" + VeRun.InputsJson + """
      ,
      "rescan": {"type": "boolean", "description": "Scan again first (default false: use the last scan)."},
      "top": {"type": "integer", "description": "Ranks coloured red (default 5); the next twice as many amber; the rest yellow."},
      "span_m": {"type": "number"},
      "name": {"type": "string", "description": "Name of the new 3D view (default 'Value engineering <date>')."},
      "open": {"type": "boolean", "description": "Open the new view when done (default true)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc.Document;
        var items = args.OptBool("rescan") ? null : VeRun.LoadLast(doc);
        if (items is null)
        {
            var inp = VeRun.Read(doc, args);
            items = VeRun.Screen(app, doc, args, inp).Items;
            VeRun.SaveLast(doc, items);
        }
        if (items.Count == 0) return ToolResult.Error("The scan found no value engineering opportunities, so there is nothing to colour.");
        var top = (int)Math.Max(1, args.OptLong("top") ?? 5);
        // Each element takes the best rank of the items it is in.
        var best = new Dictionary<long, int>();
        foreach (var o in items) foreach (var id in o.Ids) if (!best.TryGetValue(id, out var r) || o.Rank < r) best[id] = o.Rank;
        var palette = Carbon.Palette(5);
        var bands = new[] { palette[4], palette[3], palette[2] };
        int Band(int rank) => rank <= top ? 0 : rank <= top * 3 ? 1 : 2;

        using var t = RevitHelpers.StartTransaction(doc, "Value engineering view");
        var view = View3D.CreateIsometric(doc, ViewKit.TypeFor(doc, ViewFamily.ThreeDimensional).Id);
        if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
        view.DetailLevel = ViewDetailLevel.Fine;
        view.DisplayStyle = DisplayStyle.HLR;
        var name = ViewKit.Finish(doc, view, args.OptString("name") ?? $"Value engineering {DateTime.Now:yyyy-MM-dd HHmm}", null, null);
        var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill && f.GetFillPattern().Target == FillPatternTarget.Drafting);
        var counts = new int[3];
        var missing = 0;
        foreach (var kv in best)
        {
            if (doc.GetElement(new ElementId(kv.Key)) is null) { missing++; continue; }
            var band = Band(kv.Value);
            var (r, g, b) = bands[band];
            var colour = new Color(r, g, b);
            var ogs = new OverrideGraphicSettings();
            if (solid != null) { ogs.SetSurfaceForegroundPatternId(solid.Id); ogs.SetSurfaceForegroundPatternColor(colour); ogs.SetCutForegroundPatternId(solid.Id); ogs.SetCutForegroundPatternColor(colour); }
            ogs.SetProjectionLineColor(colour);
            try { view.SetElementOverrides(new ElementId(kv.Key), ogs); counts[band]++; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { missing++; }
        }
        t.Commit();
        if (args.OptBool("open", true))
            try { uidoc.RequestViewChange(view); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        string Hex((byte r, byte g, byte b) c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";
        return ToolResult.Ok(new
        {
            viewId = view.Id.Value, name,
            legend = new[]
            {
                new { colour = Hex(bands[0]), ranks = $"1 to {top}", elements = counts[0] },
                new { colour = Hex(bands[1]), ranks = $"{top + 1} to {top * 3}", elements = counts[1] },
                new { colour = Hex(bands[2]), ranks = $"over {top * 3}", elements = counts[2] },
            },
            notFound = missing > 0 ? missing : (int?)null,
            note = "A new 3D view; no existing view was changed. Elements in several items take the best rank. " + Ve.Caution,
        });
    }
}
