using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Measures the model for embodied carbon: each element's material volumes (as Revit reports them per material, the
/// same quantities the BOQ tools use), reinforcement (modelled bars, else an assumed rate per m³ of concrete), floor
/// area, then the factors and stages in <see cref="Carbon"/>. Reads the model only; results and the user's factor
/// table path are kept per model under %APPDATA%\SketchProMax\carbon.
/// </summary>
internal static class CarbonRun
{
    private const double M = 0.3048, M2 = M * M, M3 = M * M * M;

    /// <summary>Categories measured by default: structure, envelope and the main architectural elements.</summary>
    public static readonly BuiltInCategory[] DefaultCategories =
    [
        BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_StairsRuns, BuiltInCategory.OST_StairsLandings, BuiltInCategory.OST_Ramps, BuiltInCategory.OST_StructConnections,
        BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors, BuiltInCategory.OST_CurtainWallPanels,
        BuiltInCategory.OST_CurtainWallMullions, BuiltInCategory.OST_GenericModel,
    ];

    /// <summary>English category names for the assumed rebar rates (category names are translated in other Revit languages).</summary>
    private static readonly Dictionary<BuiltInCategory, string> RateKey = new()
    {
        [BuiltInCategory.OST_Floors] = "Floors", [BuiltInCategory.OST_StructuralFraming] = "Structural Framing",
        [BuiltInCategory.OST_StructuralColumns] = "Structural Columns", [BuiltInCategory.OST_Columns] = "Columns", [BuiltInCategory.OST_Walls] = "Walls",
        [BuiltInCategory.OST_StructuralFoundation] = "Structural Foundations", [BuiltInCategory.OST_StairsRuns] = "Stairs",
        [BuiltInCategory.OST_StairsLandings] = "Stairs", [BuiltInCategory.OST_Roofs] = "Roofs",
    };

    public static readonly string[] ChangeKeys = ["cement_replacement", "cement_replacement_all", "grade_swap"];

    /// <summary>The assessment's inputs, read from a tool's arguments.</summary>
    public sealed class Inputs
    {
        public Carbon.Options Options = new();
        public Carbon.Table BuiltIn = new();
        public Carbon.Table? Custom;
        public string? CustomPath;
        public string? CustomNote;
        public bool Links;
        public List<BuiltInCategory> Categories = [];
        public string RebarSource = "auto";
        public Dictionary<string, double>? RebarRates;
        public double? Gia;
    }

    /// <summary>The schema properties every carbon tool shares (inserted into each tool's PropertiesJson).</summary>
    public const string OptionsJson = """
      "stages": {"type": "array", "items": {"type": "string", "enum": ["A1-A3", "A4", "A5"]}, "description": "Life cycle modules to count (EN 15978). Default A1-A3 only; A4 transport and A5 site waste are optional."},
      "a4_distance_km": {"type": "number", "description": "A4: one transport distance for every material. Default: 50 km for concrete, masonry and aggregates, 300 km for the rest (RICS default scenarios)."},
      "a4_mode": {"type": "string", "enum": ["road", "rail", "sea"], "description": "A4 transport mode (default road)."},
      "waste_percent": {"type": "number", "description": "A5: one waste rate for every material. Default: per material (concrete 5%, steel 1%, masonry 20%...)."},
      "cement_replacement": {"type": "string", "enum": ["none", "fa25", "ggbs50"], "description": "Concrete whose name gives no replacement: none (CEM I), fa25 (25% fly ash, default) or ggbs50 (50% GGBS)."},
      "cement_replacement_all": {"type": "boolean", "description": "Apply cement_replacement to all concrete, even where the material name gives another (default false)."},
      "default_grade": {"type": "string", "description": "Strength class for concrete whose name gives none (default C32/40)."},
      "grade_swap": {"type": "string", "description": "Strength class swaps for an option, e.g. 'C40/50>C32/40, C50/60>C40/50'."},
      "factor_file": {"type": "string", "description": "Your factor table (CSV or .xlsx): columns material (name pattern), class, unit, kgCO2e per unit, stage, source, optional density. Its rows are used before the built-in ones and it is remembered for this model."},
      "use_saved_factor_file": {"type": "boolean", "description": "Use the factor table remembered for this model when factor_file is not given (default true)."},
      "factors": {"type": "array", "items": {"type": "object", "properties": {"material": {"type": "string"}, "class": {"type": "string"}, "unit": {"type": "string"}, "kgco2e_per_unit": {"type": "number"}, "stage": {"type": "string"}, "source": {"type": "string"}, "density": {"type": "number"}}}, "description": "Factor rows given directly (used before factor_file and the built-in table)."},
      "rebar_source": {"type": "string", "enum": ["auto", "model", "rates"], "description": "auto (default): modelled rebar where an element has any, else the assumed rate; model: modelled rebar only; rates: assumed rates only."},
      "rebar_rates": {"type": "object", "description": "kg of rebar per m³ of concrete by category, e.g. {\"Floors\": 120, \"Walls\": 90}. Defaults: slabs 110, beams 200, columns 200, walls 100, foundations 110, stairs 120."},
      "include_links": {"type": "boolean", "description": "Also measure loaded linked Revit models (default false)."},
      "categories": {"type": "array", "items": {"type": "string"}, "description": "Categories to measure. Default: structure, walls, floors, roofs, ceilings, stairs, doors, windows, curtain panels and mullions, generic models."},
      "gia_m2": {"type": "number", "description": "Gross internal area (m²) for kgCO2e/m². Default: a gross area plan if the model has one, else the structural floor slab areas (approximate)."}
    """;

    public static Inputs Read(Document doc, ToolArgs args)
    {
        var i = new Inputs { BuiltIn = Carbon.BuiltIn() };
        var o = i.Options;
        var stages = args.StringList("stages");
        if (stages.Any(s => s.Equals("A1-A5", StringComparison.OrdinalIgnoreCase))) { o.A4 = true; o.A5 = true; }
        foreach (var s in stages)
        {
            if (s.Equals("A1-A5", StringComparison.OrdinalIgnoreCase)) continue;
            switch (Carbon.NormalizeStage(s))
            {
                case "A1-A3": break;
                case "A4": o.A4 = true; break;
                case "A5": o.A5 = true; break;
                default: throw new ArgumentException($"Stage '{s}' isn't A1-A3, A4 or A5. Other modules (B, C, D) are not assessed.");
            }
        }
        o.A4DistanceKm = args.OptDouble("a4_distance_km");
        o.A4Mode = args.OptString("a4_mode") ?? "road";
        o.WastePercent = args.OptDouble("waste_percent");
        if (o.WastePercent is { } w) Carbon.WasteFactor(w);
        o.Replacement = args.OptString("cement_replacement") ?? "fa25";
        if (o.Replacement is not ("none" or "fa25" or "ggbs50")) throw new ArgumentException("cement_replacement is none, fa25 or ggbs50.");
        o.ReplacementAll = args.OptBool("cement_replacement_all");
        o.DefaultGrade = args.OptString("default_grade") ?? "C32/40";
        if (Carbon.CylinderStrength(o.DefaultGrade) is null) throw new ArgumentException($"default_grade '{o.DefaultGrade}' isn't a strength class like C32/40.");
        o.GradeSwap = Carbon.ParseGradeSwap(args.OptString("grade_swap"));
        i.RebarSource = args.OptString("rebar_source") ?? "auto";
        if (i.RebarSource is not ("auto" or "model" or "rates")) throw new ArgumentException("rebar_source is auto, model or rates.");
        if (args.Has("rebar_rates"))
        {
            i.RebarRates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in args.Raw["rebar_rates"].EnumerateObject())
                i.RebarRates[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble() : Carbon.Number(p.Value.ToString()) ?? throw new ArgumentException($"rebar_rates '{p.Name}' is not a number.");
        }
        i.Links = args.OptBool("include_links");
        i.Gia = args.OptDouble("gia_m2") is { } g && g > 0 ? g : null;
        i.Categories = args.StringList("categories") is { Count: > 0 } cats ? cats.Select(c => RevitHelpers.ParseCategory(doc, c)).Distinct().ToList() : DefaultCategories.ToList();

        // The user's table: given now (and remembered), else the one remembered for this model.
        var rows = new List<Dictionary<string, string>>();
        foreach (var f in args.Array("factors"))
            rows.Add(f.EnumerateObject().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText(), StringComparer.OrdinalIgnoreCase));
        var path = args.OptString("factor_file");
        if (path is null && args.OptBool("use_saved_factor_file", true) && SavedFactorFile(doc) is { } saved)
        {
            if (File.Exists(saved)) { path = saved; i.CustomNote = "remembered for this model"; }
            else i.CustomNote = $"the factor table remembered for this model is missing ({saved}); the built-in factors were used";
        }
        if (path != null)
        {
            if (!File.Exists(path)) throw new ArgumentException($"Factor table not found: {path}");
            rows.AddRange(Table.Read(path, out _));
            i.CustomPath = path;
            if (args.OptString("factor_file") != null) RememberFactorFile(doc, path);
        }
        if (rows.Count > 0)
        {
            i.Custom = Carbon.FromRows(rows, custom: true);
            if (i.Custom.Materials.Count + i.Custom.Modes.Count + i.Custom.Distances.Count + i.Custom.Waste.Count == 0)
                throw new ArgumentException("The factor table has no usable rows: " + string.Join("; ", i.Custom.Problems.Take(5)) +
                                            ". Columns: material (name pattern), class, unit, kgCO2e per unit, stage, source.");
        }
        return i;
    }

    /// <summary>Arguments with some keys left out and others laid over (carbon_compare's baseline and option).</summary>
    public static ToolArgs With(ToolArgs args, IEnumerable<string> drop, JsonElement? over)
    {
        var d = args.Raw.Where(kv => !drop.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (over is { ValueKind: JsonValueKind.Object } o)
            foreach (var p in o.EnumerateObject()) d[p.Name] = p.Value.Clone();
        return new ToolArgs(d);
    }

    public sealed class Outcome
    {
        public Carbon.Result Result = new();
        /// <summary>This model's elements: kgCO2e (with their reinforcement) and m³, for the colour view.</summary>
        public Dictionary<long, (double kg, double m3)> Elements = [];
        /// <summary>This model's elements measured but not assessed (unmapped material or no volume).</summary>
        public HashSet<long> NotAssessed = [];
    }

    public static Outcome Assess(Document doc, Inputs i)
    {
        var o = i.Options;
        var custom = i.Custom?.Materials ?? [];
        var lines = new Dictionary<string, Carbon.Line>();
        var unmapped = new Dictionary<string, Carbon.Unmapped>();
        var used = new Dictionary<string, Carbon.FactorUse>();
        var picks = new Dictionary<string, int>();
        var rateUse = new Dictionary<string, (double rate, double m3)>();
        var outcome = new Outcome();
        int modelledBars = 0, assumedHosts = 0, curtainWalls = 0;
        double modelledKg = 0;

        void AddLine(Document d, bool isHost, long hostId, string source, string level, string category, string type, string material, Carbon.Pick pick,
            double vol, double area, double? mass, bool assumed, int count)
        {
            var f = pick.Factor!;
            var a4Row = i.Custom is null ? null : Carbon.StageRow(custom, "A4", material, type, pick.Class);
            var a5Row = i.Custom is null ? null : Carbon.StageRow(custom, "A5", material, type, pick.Class);
            var st = Carbon.Emissions(f, pick.Class, vol, area, mass, o, i.BuiltIn, i.Custom, a4Row, a5Row, out var qty);
            var density = f.Density ?? Carbon.DefaultDensity(pick.Class);
            Carbon.Add(lines, new Carbon.Line
            {
                Source = source, Level = level.Length > 0 ? level : "(no level)", Category = category, Type = type, Material = material, Class = pick.Class ?? "",
                Factor = f.Id, Unit = f.Unit, Quantity = qty, VolumeM3 = vol, MassKg = Carbon.Mass(vol, mass, density),
                A1A3 = st.A1A3, A4 = st.A4, A5 = st.A5, Elements = count, Assumed = assumed, Note = pick.Note ?? "",
            });
            if (!used.TryGetValue(f.Id, out var u))
                used[f.Id] = u = new Carbon.FactorUse
                {
                    Id = f.Id, Class = f.Class, Match = f.Match, Grade = f.Grade, Replacement = f.Replacement, Unit = f.Unit, Value = f.Value, Stage = f.Stage,
                    Source = f.Source, Note = f.Note, Custom = f.Custom,
                };
            u.KgCO2e += st.Total;
            if (pick.Note is { Length: > 0 } n) picks[n] = (picks.TryGetValue(n, out var c) ? c : 0) + count;
            if (isHost)
            {
                var had = outcome.Elements.TryGetValue(hostId, out var e) ? e : (kg: 0.0, m3: 0.0);
                outcome.Elements[hostId] = (had.kg + st.Total, had.m3 + (assumed || pick.Class == "rebar" ? 0 : vol));
            }
        }

        void AddUnmapped(string source, string category, string type, string material, string revitClass, double vol, string reason)
        {
            var key = string.Join("\u0001", source, category, type, material);
            if (!unmapped.TryGetValue(key, out var u))
                unmapped[key] = u = new Carbon.Unmapped { Source = source, Category = category, Type = type, Material = material, RevitClass = revitClass, Reason = reason };
            u.VolumeM3 += vol;
            u.Elements++;
        }

        var docs = new List<(Document d, string source, bool host)> { (doc, "This model", true) };
        if (i.Links)
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                if (link.GetLinkDocument() is { } ld) docs.Add((ld, "Link: " + link.Name, false));

        var rebarPick = Carbon.Resolve("Reinforcement", "", null, custom, i.BuiltIn, o, forcedClass: "rebar");
        foreach (var (d, source, isHost) in docs)
        {
            // Modelled reinforcement, by host.
            var barsByHost = new Dictionary<long, double>();
            if (i.RebarSource != "rates")
                foreach (var bar in new FilteredElementCollector(d).OfClass(typeof(Rebar)).Cast<Element>().Concat(new FilteredElementCollector(d).OfClass(typeof(RebarInSystem))))
                {
                    double kg;
                    try { kg = Measure.Of(d, bar, "kg", 0).Qty; }
                    catch (Exception ex) when (ex is ArgumentException or Autodesk.Revit.Exceptions.ApplicationException) { continue; }
                    if (kg <= 0) continue;
                    var hostId = bar is Rebar rb ? rb.GetHostId() : ((RebarInSystem)bar).GetHostId();
                    var host = d.GetElement(hostId);
                    barsByHost[hostId.Value] = (barsByHost.TryGetValue(hostId.Value, out var k) ? k : 0) + kg;
                    modelledBars++; modelledKg += kg;
                    var barType = (d.GetElement(bar.GetTypeId()) as ElementType)?.Name ?? "";
                    var pick = Carbon.Resolve("Reinforcement", barType, null, custom, i.BuiltIn, o, forcedClass: "rebar");
                    if (pick.Factor is null) { AddUnmapped(source, "Structural Rebar", barType, "Reinforcement", "", 0, pick.Note ?? "no rebar factor"); continue; }
                    AddLine(d, isHost, hostId.Value, source, Measure.LevelName(d, bar), host?.Category?.Name ?? "Structural Rebar", host is null ? barType : Measure.TypeName(host),
                        "Reinforcement (modelled)", pick, 0, 0, kg, false, 1);
                }

            foreach (var cat in i.Categories)
                foreach (var e in new FilteredElementCollector(d).OfCategory(cat).WhereElementIsNotElementType())
                {
                    if (e is Wall { IsStackedWallMember: true }) continue;
                    if (e is Wall cw && cw.WallType.Kind == WallKind.Curtain) { curtainWalls++; continue; }
                    var category = e.Category?.Name ?? cat.ToString();
                    var type = Measure.TypeName(e);
                    var level = Measure.LevelName(d, e);
                    var parts = Parts(d, e);
                    if (parts.Count == 0)
                    {
                        AddUnmapped(source, category, type, "(no measurable volume)", "", 0, InPlace.Is(e) ? InPlace.Short : "Revit reports no material volume for it");
                        if (isHost) outcome.NotAssessed.Add(e.Id.Value);
                        continue;
                    }
                    var concreteM3 = 0.0;
                    foreach (var (name, revitClass, vol, area) in parts)
                    {
                        var pick = Carbon.Resolve(name, type, revitClass, custom, i.BuiltIn, o);
                        if (pick.Factor is null)
                        {
                            AddUnmapped(source, category, type, name, revitClass, vol, pick.Note ?? "no factor");
                            if (isHost) outcome.NotAssessed.Add(e.Id.Value);
                            continue;
                        }
                        AddLine(d, isHost, e.Id.Value, source, level, category, type, name, pick, vol, area, null, false, 1);
                        if (pick.Class == "concrete") concreteM3 += vol;
                    }
                    // Assumed reinforcement in concrete without modelled bars.
                    if (concreteM3 > 0 && i.RebarSource != "model" && RateKey.TryGetValue(cat, out var key) && (i.RebarSource == "rates" || !barsByHost.ContainsKey(e.Id.Value)))
                    {
                        var rate = Carbon.RebarRate(key, i.RebarRates);
                        if (rate <= 0) continue;
                        if (rebarPick.Factor is null) { AddUnmapped(source, category, type, "Reinforcement (assumed)", "", 0, rebarPick.Note ?? "no rebar factor"); continue; }
                        AddLine(d, isHost, e.Id.Value, source, level, category, type, $"Reinforcement (assumed {Carbon.F(rate, 0)} kg/m³)", rebarPick, 0, 0, concreteM3 * rate, true, 1);
                        assumedHosts++;
                        rateUse[key] = (rate, (rateUse.TryGetValue(key, out var ru) ? ru.m3 : 0) + concreteM3);
                    }
                }
        }

        // Floor area for the intensity.
        double? gia = i.Gia;
        string giaSource;
        if (gia is not null) giaSource = "given (gia_m2)";
        else
        {
            var areas = docs.SelectMany(x => new FilteredElementCollector(x.d).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType().OfType<Area>())
                .Where(a => a.Area > 0 && (a.AreaScheme?.Name ?? "").IndexOf("gross", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (areas.Count > 0) { gia = areas.Sum(a => a.Area) * M2; giaSource = $"{areas.Count} area(s) in the gross area plan(s)"; }
            else
            {
                var floors = docs.SelectMany(x => new FilteredElementCollector(x.d).OfClass(typeof(Floor)).Cast<Floor>()).Where(f =>
                {
                    if (f.FloorType.IsFoundationSlab) return false;
                    var structural = f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;
                    var material = f.Document.GetElement(f.FloorType.GetCompoundStructure() is { } cs && cs.StructuralMaterialIndex >= 0 ? cs.GetMaterialId(cs.StructuralMaterialIndex) : ElementId.InvalidElementId) as Material;
                    return ModelRules.FloorSkipReason(new ModelRules.FloorFacts(f.FloorType.Name, structural, material?.Name, (f.FloorType.GetCompoundStructure()?.GetWidth() ?? 0) * 304.8),
                        ModelRules.FloorStructureWords, ModelRules.FloorFinishWords, 100) is null;
                }).ToList();
                var sum = floors.Sum(f => f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0) * M2;
                gia = sum > 0 ? sum : null;
                giaSource = sum > 0 ? $"approximate: area of {floors.Count} structural floor slab(s), not a measured GIA; give gia_m2 for a reported figure"
                    : "no floor area found; give gia_m2 for kgCO2e/m²";
            }
        }

        var r = outcome.Result;
        r.Model = doc.Title;
        r.Date = Checks.Now;
        r.FactorTable = "built-in " + (i.BuiltIn.Version.Length > 0 ? i.BuiltIn.Version : "(unversioned)");
        r.CustomTable = i.CustomPath ?? (i.Custom != null ? "factors given in the request" : null);
        r.GiaM2 = gia;
        r.GiaSource = giaSource;
        r.Stages = ["A1-A3"];
        if (o.A4) r.Stages.Add("A4");
        if (o.A5) r.Stages.Add("A5 (site waste)");
        r.Lines = lines.Values.OrderByDescending(l => l.Total).ToList();
        r.Unmapped = unmapped.Values.OrderByDescending(u => u.VolumeM3).ThenByDescending(u => u.Elements).ToList();
        r.Factors = used.Values.OrderByDescending(u => u.KgCO2e).ToList();
        r.NotIncluded = Carbon.NotIncluded(o);

        var a = r.Assumptions;
        a.Add($"Factors: built-in generic table {i.BuiltIn.Version}. These are GENERIC PLACEHOLDERS (ICE v3.0-style values, IStructE/RICS defaults) to be checked against the current database or replaced by product EPDs before reporting.");
        if (i.Custom != null) a.Add($"Your factors ({r.CustomTable}{(i.CustomNote is { } cn ? ", " + cn : "")}): {i.Custom.Materials.Count} material row(s) used before the built-in ones" +
                                    (i.Custom.Problems.Count > 0 ? $"; {i.Custom.Problems.Count} row(s) skipped: {string.Join("; ", i.Custom.Problems.Take(5))}" : "") + ".");
        else if (i.CustomNote != null) a.Add("Factor table: " + i.CustomNote + ".");
        a.Add("Quantities: Revit's volume per material of each element (compound layers separately), as modelled; densities from the factor rows (concrete 2400, steel 7850 kg/m³).");
        foreach (var kv in picks.OrderByDescending(kv => kv.Value).Take(15)) a.Add($"Concrete ({kv.Value} element part(s)): {kv.Key}.");
        a.Add(i.RebarSource switch
        {
            "model" => $"Reinforcement: modelled bars only ({modelledBars} bar set(s), {Carbon.F(modelledKg / 1000, 2)} t); none assumed.",
            "rates" => "Reinforcement: assumed rates only (modelled bars ignored).",
            _ => $"Reinforcement: modelled bars where an element has any ({modelledBars} bar set(s), {Carbon.F(modelledKg / 1000, 2)} t); otherwise assumed rates ({assumedHosts} element(s)).",
        });
        foreach (var kv in rateUse.OrderBy(kv => kv.Key)) a.Add($"Assumed reinforcement: {kv.Key} {Carbon.F(kv.Value.rate, 0)} kg/m³ of concrete over {Carbon.F(kv.Value.m3, 1)} m³ (typical value; replace with the design's through rebar_rates).");
        if (o.A4)
            a.Add(o.A4DistanceKm is { } km ? $"A4: {Carbon.F(km, 0)} km by {o.A4Mode} for every material, {Carbon.F(Carbon.ModeFactor(o.A4Mode, i.BuiltIn, i.Custom), 3)} kgCO2e/t·km."
                : $"A4: RICS default distances (50 km for concrete, masonry and aggregates, 300 km for the rest) by {o.A4Mode}, {Carbon.F(Carbon.ModeFactor(o.A4Mode, i.BuiltIn, i.Custom), 3)} kgCO2e/t·km.");
        if (o.A5)
            a.Add(o.WastePercent is { } wp ? $"A5: site waste {Carbon.F(wp, 1)}% for every material (waste factor w/(1-w) × (A1-A3 + A4))."
                : "A5: site waste at RICS default rates per material (concrete 5%, rebar 5%, steel 1%, timber 10%, masonry 20%, plasterboard 22.5%, others 5%), waste factor w/(1-w) × (A1-A3 + A4).");
        a.Add("Floor area: " + giaSource + ".");
        if (i.Links) a.Add($"Linked models: {docs.Count - 1} loaded link instance(s) measured; a model linked twice is counted twice.");
        if (curtainWalls > 0) a.Add($"{curtainWalls} curtain wall(s) are counted through their panels and mullions.");
        if (r.Unmapped.Count > 0) a.Add($"Unmapped: {r.Unmapped.Sum(u => u.Elements)} element part(s) in {r.Unmapped.Count} group(s), {Carbon.F(r.Unmapped.Sum(u => u.VolumeM3), 1)} m³, are NOT in the totals (see the Unmapped list); add factor rows for them.");
        return outcome;
    }

    /// <summary>An element's materials with their volume (m³) and area (m²); its total volume under its main material when Revit gives none per material.</summary>
    private static List<(string name, string revitClass, double vol, double area)> Parts(Document d, Element e)
    {
        var parts = new List<(string, string, double, double)>();
        foreach (var id in e.GetMaterialIds(false))
        {
            double v, a;
            try { v = e.GetMaterialVolume(id) * M3; a = e.GetMaterialArea(id, false) * M2; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            if (v <= 1e-9) continue;
            var m = d.GetElement(id) as Material;
            parts.Add((m?.Name ?? "(material)", m?.MaterialClass ?? "", v, a));
        }
        if (parts.Count > 0) return parts;
        var vol = Math.Max(e.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ?? 0, e.LookupParameter("Volume") is { StorageType: StorageType.Double } p ? p.AsDouble() : 0) * M3;
        if (vol > 1e-9)
        {
            var name = Measure.MaterialName(d, e);
            parts.Add((name.Length > 0 ? name : "(no material)", "", vol, (e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0) * M2));
        }
        return parts;
    }

    // ---------------------------------------------------------------- per-model store

    public static string Store(Document doc) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "carbon", Checks.ModelKey(doc));

    private sealed class Settings { public string? FactorFile { get; set; } }

    public static string? SavedFactorFile(Document doc)
    {
        try
        {
            var file = Path.Combine(Store(doc), "settings.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file))?.FactorFile : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void RememberFactorFile(Document doc, string path)
    {
        try
        {
            Directory.CreateDirectory(Store(doc));
            File.WriteAllText(Path.Combine(Store(doc), "settings.json"), JsonSerializer.Serialize(new Settings { FactorFile = Path.GetFullPath(path) }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Safe(string name) => new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    /// <summary>Saves the result as "last" and, when named, under its name too. Returns the named file.</summary>
    public static string? Save(Document doc, Carbon.Result r, string? name)
    {
        Directory.CreateDirectory(Store(doc));
        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(Store(doc), "last.json"), json);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var file = Path.Combine(Store(doc), Safe(name!.Trim()) + ".json");
        File.WriteAllText(file, json);
        return file;
    }

    /// <summary>A saved result: "last", a name saved with save_as, or a .json path.</summary>
    public static Carbon.Result Load(Document doc, string nameOrPath)
    {
        var file = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(nameOrPath) ? nameOrPath : Path.Combine(Store(doc), Safe(nameOrPath.Trim()) + ".json");
        if (!File.Exists(file))
        {
            var saved = Directory.Exists(Store(doc)) ? Directory.GetFiles(Store(doc), "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => n != "settings").ToList() : [];
            throw new ArgumentException($"No saved carbon result '{nameOrPath}' for this model." +
                                        (saved.Count > 0 ? " Saved: " + string.Join(", ", saved) + "." : " Run embodied_carbon first (with save_as to name it)."));
        }
        var r = JsonSerializer.Deserialize<Carbon.Result>(File.ReadAllText(file)) ?? throw new ArgumentException($"{file} is not a carbon result.");
        if (r.Name.Length == 0) r.Name = Path.GetFileNameWithoutExtension(file);
        return r;
    }

    // ---------------------------------------------------------------- reports

    private static string N(double v, int dp = 1) => Math.Round(v, dp).ToString(CultureInfo.InvariantCulture);

    private static readonly string[] BreakdownHeader = ["Group", "A1-A3 kgCO2e", "A4 kgCO2e", "A5 kgCO2e", "Total kgCO2e", "Total tCO2e", "Share %", "Volume m3", "Mass kg", "Elements"];

    private static object?[] BreakdownRow(Carbon.Row x) =>
        [x.Key, Math.Round(x.A1A3, 1), Math.Round(x.A4, 1), Math.Round(x.A5, 1), Math.Round(x.Total, 1), Math.Round(x.Total / 1000, 3), Math.Round(x.SharePercent, 1), Math.Round(x.VolumeM3, 3), Math.Round(x.MassKg, 1), x.Elements];

    /// <summary>The report's sheets: summary, breakdowns, top contributors, lines, factors, assumptions, unmapped.</summary>
    public static List<(string Name, IList<object?[]> Rows)> Sheets(Carbon.Result r)
    {
        List<object?[]> Breakdown(Func<Carbon.Line, string> key) => [BreakdownHeader, .. Carbon.By(r.Lines, key).Select(BreakdownRow)];
        var summary = new List<object?[]>
        {
            new object?[] { "Item", "Value" },
            new object?[] { "Model", r.Model }, new object?[] { "Date", r.Date },
            new object?[] { "Modules counted (EN 15978)", string.Join(", ", r.Stages) },
            new object?[] { "Total tCO2e", Math.Round(r.Total / 1000, 2) },
            new object?[] { "A1-A3 tCO2e", Math.Round(r.A1A3 / 1000, 2) }, new object?[] { "A4 tCO2e", Math.Round(r.A4 / 1000, 2) }, new object?[] { "A5 tCO2e", Math.Round(r.A5 / 1000, 2) },
            new object?[] { "Floor area m2", r.GiaM2 is { } g ? Math.Round(g, 1) : "not known" }, new object?[] { "Floor area from", r.GiaSource },
            new object?[] { "kgCO2e/m2", r.Intensity is { } ii ? Math.Round(ii, 1) : "give gia_m2" },
            new object?[] { "Factor table", r.FactorTable + (r.CustomTable is { } ct ? " + " + ct : "") },
            new object?[] { "Unmapped (not in totals)", $"{r.Unmapped.Sum(u => u.Elements)} element part(s), {N(r.Unmapped.Sum(u => u.VolumeM3))} m3" },
            new object?[] { "Caution", "Generic placeholder factors: check them against the current ICE database or product EPDs before reporting." },
        };
        var top = new List<object?[]> { BreakdownHeader };
        top.AddRange(Carbon.Top(r.Lines, 20).Select(BreakdownRow));
        var lines = new List<object?[]> { new object?[] { "Source", "Level", "Category", "Type", "Material", "Class", "Factor", "Unit", "Quantity", "Volume m3", "Mass kg", "A1-A3 kgCO2e", "A4 kgCO2e", "A5 kgCO2e", "Total kgCO2e", "Elements", "Assumed", "Note" } };
        lines.AddRange(r.Lines.Select(l => new object?[] { l.Source, l.Level, l.Category, l.Type, l.Material, l.Class, l.Factor, l.Unit, Math.Round(l.Quantity, 3), Math.Round(l.VolumeM3, 3), Math.Round(l.MassKg, 1),
            Math.Round(l.A1A3, 1), Math.Round(l.A4, 1), Math.Round(l.A5, 1), Math.Round(l.Total, 1), l.Elements, l.Assumed ? "assumed" : "", l.Note }));
        var factors = new List<object?[]> { new object?[] { "Id", "Class", "Match", "Grade", "Replacement", "Unit", "kgCO2e per unit", "Stage", "Source", "Note", "Your table", "kgCO2e in this result" } };
        factors.AddRange(r.Factors.Select(f => new object?[] { f.Id, f.Class, f.Match, f.Grade, f.Replacement, f.Unit, f.Value, f.Stage, f.Source, f.Note, f.Custom ? "yes" : "", Math.Round(f.KgCO2e, 1) }));
        var assumptions = new List<object?[]> { new object?[] { "Assumptions" } };
        assumptions.AddRange(r.Assumptions.Select(x => new object?[] { x }));
        assumptions.Add([""]);
        assumptions.Add(["Not included"]);
        assumptions.AddRange(r.NotIncluded.Select(x => new object?[] { x }));
        var unmapped = new List<object?[]> { new object?[] { "Source", "Category", "Type", "Material", "Revit material class", "Volume m3", "Element parts", "Why" } };
        unmapped.AddRange(r.Unmapped.Select(u => new object?[] { u.Source, u.Category, u.Type, u.Material, u.RevitClass, Math.Round(u.VolumeM3, 3), u.Elements, u.Reason }));
        return
        [
            ("Summary", summary), ("By material class", Breakdown(l => l.Class)), ("By level", Breakdown(l => l.Level)), ("By category", Breakdown(l => l.Category)),
            ("By type", Breakdown(l => l.Type)), ("By material", Breakdown(l => l.Material)), ("Top contributors", top), ("Lines", lines),
            ("Factors used", factors), ("Assumptions", assumptions), ("Unmapped", unmapped),
        ];
    }

    /// <summary>Writes the sheets as one CSV (sections one after another) and, unless excel is false, an .xlsx.</summary>
    public static (string csv, string? xlsx) Write(Document doc, ToolArgs args, string title, List<(string Name, IList<object?[]> Rows)> sheets)
    {
        var folder = Checks.Folder(args);
        Directory.CreateDirectory(folder);
        var stem = Path.Combine(folder, $"{Safe(doc.Title)} - {title} {DateTime.Now:yyyy-MM-dd HHmm}");
        var sb = new StringBuilder();
        foreach (var (name, rows) in sheets)
        {
            sb.AppendLine(Carbon.Csv(name));
            foreach (var row in rows)
                sb.AppendLine(string.Join(",", row.Select(c => c is null ? "" : Carbon.Csv(Convert.ToString(c, CultureInfo.InvariantCulture) ?? ""))));
            sb.AppendLine();
        }
        File.WriteAllText(stem + ".csv", sb.ToString(), new UTF8Encoding(true));
        string? xlsx = null;
        if (args.OptBool("excel", true))
        {
            try { Xlsx.Write(stem + ".xlsx", sheets); xlsx = stem + ".xlsx"; }
            catch (IOException) { xlsx = null; }
        }
        return (stem + ".csv", xlsx);
    }

    private static object Rows(IEnumerable<Carbon.Row> rows, int take) => rows.Take(take).Select(x => new
    {
        group = x.Key, tCO2e = Math.Round(x.Total / 1000, 2), share = Math.Round(x.SharePercent, 1),
        a1a3 = Math.Round(x.A1A3 / 1000, 2), a4 = x.A4 > 0 ? Math.Round(x.A4 / 1000, 2) : (double?)null, a5 = x.A5 > 0 ? Math.Round(x.A5 / 1000, 2) : (double?)null,
    });

    /// <summary>The tool's reply: totals, intensity, breakdowns, top contributors, unmapped items and assumptions.</summary>
    public static object Summary(Carbon.Result r, int top = 10) => new
    {
        model = r.Model, modules = r.Stages,
        totalTCO2e = Math.Round(r.Total / 1000, 2),
        byModule = new { a1a3 = Math.Round(r.A1A3 / 1000, 2), a4 = Math.Round(r.A4 / 1000, 2), a5 = Math.Round(r.A5 / 1000, 2) },
        floorAreaM2 = r.GiaM2 is { } g ? Math.Round(g, 1) : (double?)null, floorAreaFrom = r.GiaSource,
        kgCO2ePerM2 = r.Intensity is { } ii ? Math.Round(ii, 1) : (double?)null,
        byClass = Rows(Carbon.By(r.Lines, l => l.Class), 20),
        byLevel = Rows(Carbon.By(r.Lines, l => l.Level), 25),
        byCategory = Rows(Carbon.By(r.Lines, l => l.Category), 20),
        topContributors = Rows(Carbon.Top(r.Lines, top), top),
        unmapped = r.Unmapped.Count == 0 ? null : new
        {
            groups = r.Unmapped.Count, elementParts = r.Unmapped.Sum(u => u.Elements), volumeM3 = Math.Round(r.Unmapped.Sum(u => u.VolumeM3), 2),
            items = r.Unmapped.Take(20).Select(u => new { u.Source, u.Category, u.Type, u.Material, volumeM3 = Math.Round(u.VolumeM3, 2), parts = u.Elements, why = u.Reason }),
        },
        assumptions = r.Assumptions, notIncluded = r.NotIncluded,
        factorsUsed = r.Factors.Take(25).Select(f => new { f.Id, f.Unit, kgCO2ePerUnit = f.Value, f.Source, yours = f.Custom ? true : (bool?)null, tCO2e = Math.Round(f.KgCO2e / 1000, 2) }),
        caution = "The built-in factors are generic placeholders, not verified for this project: check them against the current ICE database or the suppliers' EPDs (factor_file) before reporting.",
    };
}

public sealed class EmbodiedCarbonTool : IRevitTool
{
    public string Name => "embodied_carbon";
    public string Description =>
        "Embodied carbon of the model (EN 15978 / RICS whole life carbon conventions): material quantities from the model " +
        "(concrete by strength class, reinforcement from modelled bars or an assumed kg/m³ per category, structural steel, " +
        "timber, masonry, glass, aluminium and others), times kgCO2e factors, for modules A1-A3 (default) plus optional A4 " +
        "transport and A5 site waste. Total tCO2e and kgCO2e/m² of floor area (gross area plan, slab areas, or gia_m2), " +
        "by material class, level, category, type and material, top contributors, unmapped materials (listed, not dropped), " +
        "and an assumptions list; Excel and CSV. The built-in factors are generic placeholders to check; factor_file adds " +
        "your own (remembered per model). Each run is saved for carbon_compare (save_as names it).";
    public string PropertiesJson => "{" + CarbonRun.OptionsJson + """
      ,
      "save_as": {"type": "string", "description": "Name to save this result under, for carbon_compare (it is always saved as 'last' too)."},
      "write_factor_template": {"type": "boolean", "description": "Also write the built-in factor table as a CSV next to the report, to copy and edit as your own (default false)."},
      "excel": {"type": "boolean", "description": "Also write an .xlsx workbook (default true)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var inputs = CarbonRun.Read(doc, args);
        var outcome = CarbonRun.Assess(doc, inputs);
        var r = outcome.Result;
        r.Name = args.OptString("save_as") ?? "";
        var saved = CarbonRun.Save(doc, r, args.OptString("save_as"));
        var (csv, xlsx) = CarbonRun.Write(doc, args, "Embodied carbon", CarbonRun.Sheets(r));
        string? template = null;
        if (args.OptBool("write_factor_template"))
        {
            template = Path.Combine(Checks.Folder(args), $"Carbon factors (built-in {inputs.BuiltIn.Version}).csv");
            File.WriteAllText(template, Carbon.BuiltInText(), new UTF8Encoding(true));
        }
        if (r.Lines.Count == 0)
            return ToolResult.Ok(new
            {
                totalTCO2e = 0, files = new { csv, xlsx },
                note = "Nothing was assessed: no element in the measured categories has a material with a factor. See the Unmapped sheet; add rows in a factor_file, or widen categories.",
                unmapped = r.Unmapped.Take(20).Select(u => new { u.Category, u.Type, u.Material, why = u.Reason }),
            });
        return ToolResult.Ok(new
        {
            summary = CarbonRun.Summary(r),
            files = new { csv, xlsx, factorTemplate = template },
            saved = saved ?? "as 'last' (compare with carbon_compare)",
            factorTable = new { builtIn = inputs.BuiltIn.Version, yours = r.CustomTable, skippedRows = inputs.Custom?.Problems.Take(10) },
        });
    }
}

public sealed class CarbonCompareTool : IRevitTool
{
    public string Name => "carbon_compare";
    public string Description =>
        "Compare embodied carbon between two options or snapshots and show the saving: the current model against a saved " +
        "result ('last' or a save_as name from embodied_carbon), two saved results, or the current model against itself " +
        "with changes (cement_replacement such as ggbs50 with cement_replacement_all, grade_swap such as 'C40/50>C32/40', " +
        "or any embodied_carbon option in changes). Totals and kgCO2e/m² before and after, by module, material class, " +
        "material and level; Excel and CSV.";
    public string PropertiesJson => "{" + CarbonRun.OptionsJson + """
      ,
      "baseline": {"type": "string", "description": "'current' (the model now), 'last', a saved name, or a result .json. Default: 'current' when changes are given, else 'last'."},
      "option": {"type": "string", "description": "'current' (default: the model now, with the changes), a saved name, or a result .json."},
      "changes": {"type": "object", "description": "embodied_carbon options for the option only, e.g. {\"cement_replacement\": \"ggbs50\", \"cement_replacement_all\": true}. cement_replacement, cement_replacement_all and grade_swap given at the top level apply to the option only too."},
      "save_as": {"type": "string", "description": "Save the option's result under this name."},
      "excel": {"type": "boolean", "description": "Also write an .xlsx workbook (default true)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        JsonElement? changes = args.Has("changes") ? args.Raw["changes"] : null;
        if (changes is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("changes is an object of embodied_carbon options.");
        var topChanges = CarbonRun.ChangeKeys.Where(args.Has).ToList();
        var hasChanges = topChanges.Count > 0 || changes is { } c && c.EnumerateObject().Any();
        var baselineName = args.OptString("baseline") ?? (hasChanges ? "current" : "last");
        var optionName = args.OptString("option") ?? "current";
        if (baselineName == optionName && baselineName != "current") throw new ArgumentException("baseline and option are the same saved result.");
        if (baselineName == "current" && optionName == "current" && !hasChanges)
            throw new ArgumentException("Nothing to compare: give changes (e.g. cement_replacement ggbs50 with cement_replacement_all, or grade_swap), or compare with a saved result (baseline 'last').");

        var baseArgs = CarbonRun.With(args, CarbonRun.ChangeKeys.Concat(["changes", "save_as"]), null);
        Carbon.Result Get(string name, bool option)
        {
            if (!name.Equals("current", StringComparison.OrdinalIgnoreCase)) return CarbonRun.Load(doc, name);
            ToolArgs a = baseArgs;
            if (option)
            {
                var over = args.Raw.Where(kv => topChanges.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                a = CarbonRun.With(new ToolArgs(baseArgs.Raw.Concat(over).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value)), [], changes);
            }
            var r = CarbonRun.Assess(doc, CarbonRun.Read(doc, a)).Result;
            r.Name = option ? (hasChanges ? "current, with changes" : "current") : "current";
            return r;
        }
        var before = Get(baselineName, false);
        var after = Get(optionName, true);
        if (args.OptString("save_as") is { } saveAs && optionName == "current") { after.Name = saveAs; CarbonRun.Save(doc, after, saveAs); }

        var change = after.Total - before.Total;
        var pct = before.Total > 0 ? change / before.Total * 100 : (double?)null;
        var byClass = Carbon.Compare(before.Lines, after.Lines, l => l.Class);
        var byMaterial = Carbon.Compare(before.Lines, after.Lines, l => l.Material);
        var byLevel = Carbon.Compare(before.Lines, after.Lines, l => l.Level);
        var notes = new List<string>();
        if (before.FactorTable != after.FactorTable || before.CustomTable != after.CustomTable) notes.Add($"The two use different factor tables ({before.FactorTable}{(before.CustomTable is { } b1 ? " + " + b1 : "")} vs {after.FactorTable}{(after.CustomTable is { } a1 ? " + " + a1 : "")}).");
        if (!before.Stages.SequenceEqual(after.Stages)) notes.Add($"The two count different modules ({string.Join(", ", before.Stages)} vs {string.Join(", ", after.Stages)}).");
        if (hasChanges && optionName != "current") notes.Add("changes apply only when the option is 'current'; they were not used.");
        if (after.Unmapped.Count > 0 || before.Unmapped.Count > 0) notes.Add("Unmapped materials are in neither total; see each result's Unmapped list.");

        IList<object?[]> DiffSheet(List<Carbon.Diff> d) =>
            [new object?[] { "Group", $"{before.Name} kgCO2e", $"{after.Name} kgCO2e", "Change kgCO2e", "Change %" },
             .. d.Select(x => new object?[] { x.Key, Math.Round(x.Before, 1), Math.Round(x.After, 1), Math.Round(x.Change, 1), x.ChangePercent is { } p ? Math.Round(p, 1) : null })];
        var summary = new List<object?[]>
        {
            new object?[] { "Item", before.Name.Length > 0 ? before.Name : "baseline", after.Name.Length > 0 ? after.Name : "option", "Change" },
            new object?[] { "Date", before.Date, after.Date, "" },
            new object?[] { "Total tCO2e", Math.Round(before.Total / 1000, 2), Math.Round(after.Total / 1000, 2), Math.Round(change / 1000, 2) },
            new object?[] { "A1-A3 tCO2e", Math.Round(before.A1A3 / 1000, 2), Math.Round(after.A1A3 / 1000, 2), Math.Round((after.A1A3 - before.A1A3) / 1000, 2) },
            new object?[] { "A4 tCO2e", Math.Round(before.A4 / 1000, 2), Math.Round(after.A4 / 1000, 2), Math.Round((after.A4 - before.A4) / 1000, 2) },
            new object?[] { "A5 tCO2e", Math.Round(before.A5 / 1000, 2), Math.Round(after.A5 / 1000, 2), Math.Round((after.A5 - before.A5) / 1000, 2) },
            new object?[] { "kgCO2e/m2", before.Intensity is { } bi ? Math.Round(bi, 1) : null, after.Intensity is { } ai ? Math.Round(ai, 1) : null,
                before.Intensity is { } bi2 && after.Intensity is { } ai2 ? Math.Round(ai2 - bi2, 1) : null },
            new object?[] { "Change %", "", "", pct is { } p0 ? Math.Round(p0, 1) : null },
        };
        foreach (var n in notes) summary.Add(["Note", n, "", ""]);
        var (csv, xlsx) = CarbonRun.Write(doc, args, "Carbon comparison",
            [("Summary", summary), ("By material class", DiffSheet(byClass)), ("By material", DiffSheet(byMaterial)), ("By level", DiffSheet(byLevel)),
             ("Option assumptions", [new object?[] { "Assumptions" }, .. after.Assumptions.Select(x => new object?[] { x })])]);

        object Top(List<Carbon.Diff> d, int n) => d.Where(x => Math.Abs(x.Change) > 0.05).Take(n).Select(x => new
        {
            group = x.Key, beforeT = Math.Round(x.Before / 1000, 2), afterT = Math.Round(x.After / 1000, 2), changeT = Math.Round(x.Change / 1000, 2),
            changePercent = x.ChangePercent is { } p ? Math.Round(p, 1) : (double?)null,
        });
        return ToolResult.Ok(new
        {
            baseline = new { name = before.Name.Length > 0 ? before.Name : baselineName, before.Date, totalTCO2e = Math.Round(before.Total / 1000, 2), kgCO2ePerM2 = before.Intensity is { } x1 ? Math.Round(x1, 1) : (double?)null },
            option = new { name = after.Name.Length > 0 ? after.Name : optionName, after.Date, totalTCO2e = Math.Round(after.Total / 1000, 2), kgCO2ePerM2 = after.Intensity is { } x2 ? Math.Round(x2, 1) : (double?)null },
            changeTCO2e = Math.Round(change / 1000, 2), changePercent = pct is { } pp ? Math.Round(pp, 1) : (double?)null,
            saving = change < 0 ? $"{Carbon.F(-change / 1000, 1)} tCO2e less ({Carbon.F(-(pct ?? 0), 1)}%)" : change > 0 ? $"{Carbon.F(change / 1000, 1)} tCO2e more ({Carbon.F(pct ?? 0, 1)}%)" : "no change",
            byClass = Top(byClass, 15), byMaterial = Top(byMaterial, 15), byLevel = Top(byLevel, 15),
            notes, files = new { csv, xlsx },
            caution = "Generic placeholder factors: the difference shows the direction and rough size of a saving; check factors before reporting.",
        });
    }
}

public sealed class CarbonViewTool : IRevitTool
{
    public string Name => "carbon_view";
    public string Description =>
        "Colour-codes the model by embodied carbon in a NEW 3D view (existing views are not changed): each element of this " +
        "model coloured green to red by carbon intensity (kgCO2e per m³ of the element, reinforcement included) or by its " +
        "total kgCO2e, in bands holding about equal numbers of elements; elements with unmapped materials are halftone. " +
        "Takes the same options as embodied_carbon. Linked models are not coloured.";
    public string PropertiesJson => "{" + CarbonRun.OptionsJson + """
      ,
      "metric": {"type": "string", "enum": ["intensity", "total"], "description": "intensity (default): kgCO2e per m³ of the element; total: kgCO2e of the element."},
      "bands": {"type": "integer", "description": "Number of colour bands, 2 to 7 (default 5)."},
      "name": {"type": "string", "description": "Name of the new 3D view (default 'Embodied carbon - <metric> <date>')."},
      "open": {"type": "boolean", "description": "Open the new view when done (default true)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc.Document;
        var inputs = CarbonRun.Read(doc, CarbonRun.With(args, ["include_links"], null));
        var outcome = CarbonRun.Assess(doc, inputs);
        var total = args.OptString("metric") == "total";
        var values = outcome.Elements.Where(kv => kv.Value.kg > 0 && (total || kv.Value.m3 > 1e-6))
            .ToDictionary(kv => kv.Key, kv => total ? kv.Value.kg : kv.Value.kg / kv.Value.m3);
        if (values.Count == 0) return ToolResult.Error("No element of this model could be assessed (see embodied_carbon's Unmapped list), so there is nothing to colour.");
        var n = (int)Math.Max(2, Math.Min(7, args.OptLong("bands") ?? 5));
        var limits = Carbon.Bands(values.Values, n);
        var colours = Carbon.Palette(limits.Count);
        var unit = total ? "kgCO2e" : "kgCO2e/m³";

        using var t = RevitHelpers.StartTransaction(doc, "Embodied carbon view");
        var view = View3D.CreateIsometric(doc, ViewKit.TypeFor(doc, ViewFamily.ThreeDimensional).Id);
        if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
        try { view.AreModelCategoriesHidden = false; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        view.DetailLevel = ViewDetailLevel.Fine;
        view.DisplayStyle = DisplayStyle.HLR;
        var name = ViewKit.Finish(doc, view, args.OptString("name") ?? $"Embodied carbon - {(total ? "total" : "intensity")} {DateTime.Now:yyyy-MM-dd HHmm}", null, null);
        var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill && f.GetFillPattern().Target == FillPatternTarget.Drafting);
        var counts = new int[limits.Count];
        var failed = 0;
        foreach (var kv in values)
        {
            var band = Carbon.BandOf(kv.Value, limits);
            var (r, g, b) = colours[band];
            var colour = new Color(r, g, b);
            var ogs = new OverrideGraphicSettings();
            if (solid != null)
            {
                ogs.SetSurfaceForegroundPatternId(solid.Id); ogs.SetSurfaceForegroundPatternColor(colour);
                ogs.SetCutForegroundPatternId(solid.Id); ogs.SetCutForegroundPatternColor(colour);
            }
            ogs.SetProjectionLineColor(colour);
            try { view.SetElementOverrides(new ElementId(kv.Key), ogs); counts[band]++; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { failed++; }
        }
        var halftone = 0;
        foreach (var id in outcome.NotAssessed.Where(id => !values.ContainsKey(id)))
        {
            try { view.SetElementOverrides(new ElementId(id), new OverrideGraphicSettings().SetHalftone(true)); halftone++; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        t.Commit();
        if (args.OptBool("open", true))
            try { uidoc.RequestViewChange(view); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }

        string Hex((byte r, byte g, byte b) c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";
        return ToolResult.Ok(new
        {
            viewId = view.Id.Value, name, metric = total ? "total kgCO2e per element" : "kgCO2e per m³ of element",
            legend = limits.Select((limit, k) => new
            {
                band = k + 1, from = k == 0 ? 0 : Math.Round(limits[k - 1], 1), to = Math.Round(limit, 1), unit, colour = Hex(colours[k]),
                label = k == 0 ? "lowest" : k == limits.Count - 1 ? "highest" : "", elements = counts[k],
            }),
            coloured = counts.Sum(), halftone, notColoured = failed > 0 ? failed : (int?)null,
            totalTCO2e = Math.Round(outcome.Result.Total / 1000, 2),
            note = "A new 3D view; no existing view was changed. Colours follow the legend above (green lowest, red highest). Linked models are not coloured. " +
                   "Factors are generic placeholders: see embodied_carbon for the assumptions.",
        });
    }
}
