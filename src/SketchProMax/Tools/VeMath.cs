using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Value engineering arithmetic, with no Revit: the rule-of-thumb tables (span/depth ratios, typical reinforcement
/// densities, steel tonnage, concrete grades), the checks that compare an element with them, grouping slightly
/// different sizes, rates taken from a cost_estimate rate table, ranking, the VE register and the rule-of-thumb sizing
/// of structural options. ve_opportunities, ve_compare_options, ve_apply, ve_register and ve_view measure the model and
/// call these; the tests run them directly. Every figure here is a screening rule of thumb, never a design value.
/// </summary>
internal static class Ve
{
    public const string Caution = "Value engineering screening aid, not design: every item must be checked and confirmed by the responsible engineer " +
                                  "(strength, deflection, punching, vibration, fire, durability, buildability) before it is proposed to anyone.";

    // ---------------------------------------------------------------- rules of thumb

    /// <summary>
    /// Typical span/overall depth ratios (wall: height/thickness) and practical minimum depths in mm. Rules of thumb for
    /// economic RC and steel frames of ordinary loading (offices, residential, imposed load up to about 5 kPa), of the
    /// kind in the Concrete Centre's "Economic concrete frame elements" and common practice; not code limits. Every
    /// value can be replaced through the tools' ratios argument.
    /// </summary>
    public static Dictionary<string, (double Ratio, double MinMm)> DefaultRatios() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["flat_slab"] = (28, 200),       // RC flat slab, longer span / overall depth
        ["slab_on_beams"] = (30, 150),   // RC one- or two-way slab spanning between beams, shorter span / depth
        ["pt_slab"] = (40, 180),         // post-tensioned flat slab
        ["rc_beam"] = (15, 300),         // RC beam, span / overall depth (continuous)
        ["steel_beam"] = (20, 150),      // steel beam, span / section depth
        ["wall"] = (25, 200),            // RC wall, storey height / thickness, 200 mm practical minimum
    };

    /// <summary>Typical reinforcement in kg per m³ of concrete by element class (rules of thumb for ordinary RC frames).</summary>
    public static Dictionary<string, double> DefaultRebar() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["slab"] = 100, ["beam"] = 180, ["column"] = 200, ["wall"] = 90, ["foundation"] = 100, ["stair"] = 120,
    };

    /// <summary>Typical concrete strength class by element class (rule of thumb; exposure, fire and strength may need more).</summary>
    public static Dictionary<string, string> DefaultGrades() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["slab"] = "C32/40", ["beam"] = "C32/40", ["wall"] = "C32/40", ["foundation"] = "C32/40", ["stair"] = "C32/40", ["column"] = "C40/50",
    };

    /// <summary>Typical steel frame tonnage (beams and columns, kg per m² of floor) for a medium-rise office frame.</summary>
    public const double DefaultSteelKgPerM2 = 55;

    /// <summary>The screening settings: tolerances before an item is flagged and the tables above (each replaceable).</summary>
    public sealed class Settings
    {
        public Dictionary<string, (double Ratio, double MinMm)> Ratios = DefaultRatios();
        public Dictionary<string, double> Rebar = DefaultRebar();
        public Dictionary<string, string> Grades = DefaultGrades();
        public double SteelKgPerM2 = DefaultSteelKgPerM2;
        /// <summary>Thicker than typical by more than this fraction before it is flagged (default 15%).</summary>
        public double DepthTolerance = 0.15;
        /// <summary>Reinforcement above typical by more than this fraction (default 25%).</summary>
        public double RebarTolerance = 0.25;
        public double SteelTolerance = 0.15;
        /// <summary>Sizes within this many mm (and this fraction) of the smallest in a group are grouped (default 50 mm, 15%).</summary>
        public double GroupMm = 50, GroupFraction = 0.15;
        /// <summary>More concrete mixes than this in the model is flagged (default 3).</summary>
        public int MaxGrades = 3;
        /// <summary>Cost premium per strength class step (about 5 MPa) as a fraction of the concrete rate (default 4%).</summary>
        public double GradeStepPremium = 0.04;
        /// <summary>Extra formwork (m²) one step, drop or opening costs compared with a plain soffit (default 3 m²).</summary>
        public double FeatureFormworkM2 = 3;
        /// <summary>Saving allowed for each type removed by rationalising sizes (default 0: not costed).</summary>
        public double PerTypeSaving;
        /// <summary>Over-specified: more than this fraction above a spec minimum (default 15%).</summary>
        public double SpecMargin = 0.15;
    }

    /// <summary>Settings with the user's overrides, e.g. {"ratios": {"flat_slab": 30}, "rebar": {"beam": 160}, "depth_tolerance": 0.2}.</summary>
    public static Settings ReadSettings(JsonElement? o)
    {
        var s = new Settings();
        if (o is not { ValueKind: JsonValueKind.Object } j) return s;
        double Num(JsonElement v, string what) => v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new ArgumentException($"{what} is not a number.");
        foreach (var p in j.EnumerateObject())
            switch (p.Name.ToLowerInvariant())
            {
                case "ratios":
                    foreach (var r in p.Value.EnumerateObject())
                    {
                        var min = s.Ratios.TryGetValue(r.Name, out var had) ? had.MinMm : 150;
                        if (r.Value.ValueKind == JsonValueKind.Object)
                        {
                            var ratio = r.Value.TryGetProperty("ratio", out var rv) ? Num(rv, "ratio") : had.Ratio;
                            if (r.Value.TryGetProperty("min_mm", out var mv)) min = Num(mv, "min_mm");
                            s.Ratios[r.Name] = (ratio, min);
                        }
                        else s.Ratios[r.Name] = (Num(r.Value, "ratio " + r.Name), min);
                        if (s.Ratios[r.Name].Ratio <= 0) throw new ArgumentException($"ratio {r.Name} must be above 0.");
                    }
                    break;
                case "rebar": foreach (var r in p.Value.EnumerateObject()) s.Rebar[r.Name] = Num(r.Value, "rebar " + r.Name); break;
                case "grades":
                    foreach (var r in p.Value.EnumerateObject())
                    {
                        var g = r.Value.ToString();
                        if (Carbon.CylinderStrength(g) is null) throw new ArgumentException($"grades {r.Name}: '{g}' isn't a strength class like C32/40.");
                        s.Grades[r.Name] = g;
                    }
                    break;
                case "steel_kg_per_m2": s.SteelKgPerM2 = Num(p.Value, p.Name); break;
                case "depth_tolerance": s.DepthTolerance = Num(p.Value, p.Name); break;
                case "rebar_tolerance": s.RebarTolerance = Num(p.Value, p.Name); break;
                case "steel_tolerance": s.SteelTolerance = Num(p.Value, p.Name); break;
                case "group_mm": s.GroupMm = Num(p.Value, p.Name); break;
                case "group_fraction": s.GroupFraction = Num(p.Value, p.Name); break;
                case "max_grades": s.MaxGrades = (int)Num(p.Value, p.Name); break;
                case "grade_step_premium": s.GradeStepPremium = Num(p.Value, p.Name); break;
                case "feature_formwork_m2": s.FeatureFormworkM2 = Num(p.Value, p.Name); break;
                case "per_type_saving": s.PerTypeSaving = Num(p.Value, p.Name); break;
                case "spec_margin": s.SpecMargin = Num(p.Value, p.Name); break;
                default: throw new ArgumentException($"rules: unknown setting '{p.Name}'. Known: ratios, rebar, grades, steel_kg_per_m2, depth_tolerance, rebar_tolerance, steel_tolerance, group_mm, group_fraction, max_grades, grade_step_premium, feature_formwork_m2, per_type_saving, spec_margin.");
            }
        return s;
    }

    /// <summary>The rules of thumb in use, as lines for the report's assumptions.</summary>
    public static List<string> Describe(Settings s) =>
    [
        "Span/depth (wall: height/thickness) rules of thumb, minimum mm: " + string.Join(", ", s.Ratios.Select(kv => $"{kv.Key} {F(kv.Value.Ratio)} (min {F(kv.Value.MinMm, 0)})")) +
        $"; flagged when more than {F(s.DepthTolerance * 100, 0)}% thicker than typical.",
        "Typical reinforcement kg/m³: " + string.Join(", ", s.Rebar.Select(kv => $"{kv.Key} {F(kv.Value, 0)}")) + $"; flagged when more than {F(s.RebarTolerance * 100, 0)}% above.",
        "Typical concrete grades: " + string.Join(", ", s.Grades.Select(kv => $"{kv.Key} {kv.Value}")) + $"; more than {s.MaxGrades} mixes flagged; {F(s.GradeStepPremium * 100, 0)}% of the concrete rate per strength class step.",
        $"Steel frame: typical {F(s.SteelKgPerM2, 0)} kg/m² of floor (beams and columns); flagged when more than {F(s.SteelTolerance * 100, 0)}% above.",
        $"Sizes grouped when within {F(s.GroupMm, 0)} mm and {F(s.GroupFraction * 100, 0)}% of the smallest in the group; each group goes up to its largest size.",
        $"Formwork: each soffit step, drop or slab opening counted as {F(s.FeatureFormworkM2, 0)} m² of extra formwork.",
        "These are screening rules of thumb for ordinary loading, not code checks: transfer structures, heavy loads, long cantilevers, vibration, fire, durability and punching can justify more.",
    ];

    // ---------------------------------------------------------------- checks

    /// <summary>The typical overall depth (mm) for a span (m) and a ratio row: span/ratio, not less than the minimum.</summary>
    public static double TypicalDepthMm(double spanM, (double Ratio, double MinMm) rule) => Math.Max(rule.MinMm, spanM * 1000 / rule.Ratio);

    /// <summary>
    /// How much thicker than typical an element is: null when within tolerance; otherwise the typical depth and the
    /// fraction of its volume that is over (for a constant width, volume follows depth).
    /// </summary>
    public static (double TypicalMm, double ExcessFraction)? Thicker(double actualMm, double spanM, (double Ratio, double MinMm) rule, double tolerance)
    {
        if (actualMm <= 0 || spanM <= 0) return null;
        var typical = TypicalDepthMm(spanM, rule);
        if (actualMm <= typical * (1 + tolerance)) return null;
        return (typical, (actualMm - typical) / actualMm);
    }

    /// <summary>kg of reinforcement above typical in a volume of concrete, or 0 when within tolerance.</summary>
    public static double ExcessRebarKg(double rebarKg, double concreteM3, double typicalKgPerM3, double tolerance)
    {
        if (concreteM3 <= 0 || rebarKg <= 0) return 0;
        var density = rebarKg / concreteM3;
        return density > typicalKgPerM3 * (1 + tolerance) ? (density - typicalKgPerM3) * concreteM3 : 0;
    }

    /// <summary>Strength class steps (about 5 MPa each) from the typical grade up to this one; 0 when not above.</summary>
    public static int GradeSteps(double cylinderMpa, double typicalMpa) => cylinderMpa > typicalMpa + 0.5 ? (int)Math.Max(1, Math.Round((cylinderMpa - typicalMpa) / 5)) : 0;

    /// <summary>
    /// Plan spans of a column grid: for each column the nearest neighbour, and the nearest one roughly at right angles
    /// to that; the medians of the shorter and the longer of the two. Points in m; stacked columns count once.
    /// </summary>
    public static (double Short, double Long)? GridSpans(IEnumerable<(double X, double Y)> points, double minM = 2, double maxM = 20)
    {
        var p = new List<(double X, double Y)>();
        foreach (var q in points)
            if (!p.Any(x => Math.Abs(x.X - q.X) < 0.2 && Math.Abs(x.Y - q.Y) < 0.2)) p.Add(q);
        if (p.Count < 3) return null;
        var shorts = new List<double>();
        var longs = new List<double>();
        foreach (var a in p)
        {
            var near = p.Where(b => b != a).Select(b => (b, d: Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y))))
                .Where(x => x.d >= minM && x.d <= maxM).OrderBy(x => x.d).ToList();
            if (near.Count < 2) continue;
            var first = near[0];
            var ux = (first.b.X - a.X) / first.d; var uy = (first.b.Y - a.Y) / first.d;
            var cross = near.Skip(1).FirstOrDefault(x => Math.Abs(((x.b.X - a.X) * ux + (x.b.Y - a.Y) * uy) / x.d) < 0.5);
            if (cross.d <= 0) continue;
            shorts.Add(Math.Min(first.d, cross.d));
            longs.Add(Math.Max(first.d, cross.d));
        }
        if (shorts.Count == 0) return null;
        return (Median(shorts), Median(longs));
    }

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    // ---------------------------------------------------------------- rationalising sizes

    /// <summary>One size in use: its type, its dimensions in mm (thickness, or width × depth), elements and volume.</summary>
    public sealed record Size(string Type, double[] Mm, int Count, double M3);

    /// <summary>Sizes that could become one: the members and the size they all go up to (the largest in each dimension).</summary>
    public sealed record Group(List<Size> Members, double[] Target, string TargetType, double ExtraM3);

    /// <summary>
    /// Groups sizes that differ only a little: sorted from the smallest, a size joins the group when every dimension is
    /// within <paramref name="mm"/> and <paramref name="fraction"/> of the group's smallest. Each group goes up to its
    /// largest dimensions (never down, so no element loses size) and to the member type that already has them, if any.
    /// Only groups of two or more sizes are returned, the most elements first.
    /// </summary>
    public static List<Group> Rationalise(IEnumerable<Size> sizes, double mm, double fraction)
    {
        var list = sizes.Where(s => s.Mm.Length > 0 && s.Mm.All(d => d > 0)).OrderBy(s => s.Mm.Sum()).ThenBy(s => s.Type, StringComparer.Ordinal).ToList();
        var used = new HashSet<Size>();
        var groups = new List<Group>();
        foreach (var start in list)
        {
            if (used.Contains(start)) continue;
            var members = new List<Size> { start };
            used.Add(start);
            foreach (var s in list)
            {
                if (used.Contains(s) || s.Mm.Length != start.Mm.Length) continue;
                var ok = true;
                for (var i = 0; i < s.Mm.Length && ok; i++)
                {
                    var d = Math.Abs(s.Mm[i] - start.Mm[i]);
                    ok = d <= mm + 1e-6 && d <= start.Mm[i] * fraction + 1e-6;
                }
                if (ok) { members.Add(s); used.Add(s); }
            }
            if (members.Count < 2) continue;
            var target = Enumerable.Range(0, start.Mm.Length).Select(i => members.Max(m => m.Mm[i])).ToArray();
            var holder = members.Where(m => m.Mm.SequenceEqual(target)).OrderByDescending(m => m.Count).FirstOrDefault();
            // Extra concrete when every member goes up to the target: volume scales with the product of the dimensions.
            var extra = members.Sum(m => m.M3 * (Product(target) / Product(m.Mm) - 1));
            groups.Add(new Group(members, target, holder?.Type ?? "", extra));
        }
        return groups.OrderByDescending(g => g.Members.Sum(m => m.Count)).ToList();
    }

    private static double Product(double[] v) => v.Aggregate(1.0, (a, b) => a * b);

    public static string SizeText(double[] mm) => string.Join(" x ", mm.Select(d => F(d, 0))) + " mm";

    // ---------------------------------------------------------------- rates

    /// <summary>Unit rates used for savings and options, in the rate table's currency.</summary>
    public sealed class Rates
    {
        /// <summary>Concrete per m³ (supply and place), by element class where the table gives one, else "*".</summary>
        public Dictionary<string, double> ConcreteM3 = new(StringComparer.OrdinalIgnoreCase);
        public double RebarKg, SteelT, FormworkM2, PrecastM3, StrandKg, PileM;
        public string Currency = "";
        /// <summary>Where each rate came from: "rate table" or "placeholder".</summary>
        public Dictionary<string, string> From = new(StringComparer.OrdinalIgnoreCase);
        public bool AnyPlaceholder => From.Values.Any(v => v == "placeholder");

        public double Concrete(string elementClass) =>
            ConcreteM3.TryGetValue(elementClass, out var r) ? r : ConcreteM3.TryGetValue("*", out var all) ? all : 0;
    }

    /// <summary>Placeholder rates in a generic currency unit, used only where the rate table has no matching row.</summary>
    public static Rates PlaceholderRates()
    {
        var r = new Rates { RebarKg = 1.3, SteelT = 2800, FormworkM2 = 40, PrecastM3 = 450, StrandKg = 6, PileM = 120 };
        r.ConcreteM3["*"] = 160;
        foreach (var k in new[] { "concrete", "rebar", "steel", "formwork", "precast", "strand", "pile" }) r.From[k] = "placeholder";
        return r;
    }

    /// <summary>One row of a rate table, as cost_estimate reads it.</summary>
    public sealed record RateRow(string Description, string Unit, double Rate, string Category = "");

    private static readonly (string cls, string words)[] ClassWords =
    [
        ("foundation", @"\bfoundations?\b|\bfootings?\b|\braft\b|\bpile ?caps?\b|\bbases?\b"),
        ("column", @"\bcolumns?\b|\bpiers?\b"),
        ("beam", @"\bbeams?\b|\bframing\b|\bgirders?\b"),
        ("wall", @"\bwalls?\b|\bcores?\b"),
        ("slab", @"\bslabs?\b|\bfloors?\b|\bdecks?\b"),
        ("stair", @"\bstairs?\b|\bflights?\b"),
    ];

    /// <summary>The element class a description or category names (slab, beam, column, wall, foundation, stair), or null.</summary>
    public static string? ClassFromWords(string text)
    {
        foreach (var (cls, words) in ClassWords) if (Regex.IsMatch(text ?? "", words, RegexOptions.IgnoreCase)) return cls;
        return null;
    }

    /// <summary>
    /// Rates from a cost_estimate rate table: concrete per m³ (by element class where the description or category names
    /// one), reinforcement per kg (or t), structural steel per t (or kg), formwork per m², precast per m³, PT strand per
    /// kg, piling per m. Whatever the table lacks comes from <see cref="PlaceholderRates"/>, marked as such.
    /// </summary>
    public static Rates RatesFrom(IEnumerable<RateRow> rows, string currency = "")
    {
        var r = PlaceholderRates();
        r.Currency = currency;
        static string U(string u) => Regex.Replace((u ?? "").Trim().ToLowerInvariant().Replace("³", "3").Replace("²", "2"), @"[.\s]", "") switch
        {
            "m3" or "cum" or "cbm" => "m3", "m2" or "sqm" => "m2", "m" or "lm" or "rm" => "m", "kg" or "kgs" => "kg",
            "t" or "mt" or "ton" or "tons" or "tonne" or "tonnes" => "t", var other => other,
        };
        var concrete = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Rate <= 0) continue;
            var text = row.Description + " " + row.Category;
            var unit = U(row.Unit);
            bool Has(string p) => Regex.IsMatch(text, p, RegexOptions.IgnoreCase);
            if (Has(@"\brebars?\b|reinforce|\bmesh\b|\bfabric\b|\bbars?\b") && unit is "kg" or "t")
            { r.RebarKg = unit == "t" ? row.Rate / 1000 : row.Rate; r.From["rebar"] = "rate table"; }
            else if (Has(@"\bstrands?\b|\btendons?\b|post.?tension|\bPT\b") && unit is "kg" or "t")
            { r.StrandKg = unit == "t" ? row.Rate / 1000 : row.Rate; r.From["strand"] = "rate table"; }
            else if (Has(@"\bsteel\b|\bsections?\b|\bUB\b|\bUC\b") && unit is "kg" or "t")
            { r.SteelT = unit == "kg" ? row.Rate * 1000 : row.Rate; r.From["steel"] = "rate table"; }
            else if (Has(@"formwork|shuttering|\bforms?\b") && unit == "m2")
            { r.FormworkM2 = row.Rate; r.From["formwork"] = "rate table"; }
            else if (Has(@"\bpiles?\b|\bpiling\b") && unit == "m" && !Has(@"\bcaps?\b"))
            { r.PileM = row.Rate; r.From["pile"] = "rate table"; }
            else if (Has(@"precast|hollow.?core|\bplanks?\b") && unit == "m3")
            { r.PrecastM3 = row.Rate; r.From["precast"] = "rate table"; }
            else if (unit == "m3" && (Has(@"concrete|\bRC\b|\bC\d{2}") || ClassFromWords(text) is not null))
            {
                var cls = ClassFromWords(text) ?? "*";
                if (!concrete.TryGetValue(cls, out var l)) concrete[cls] = l = [];
                l.Add(row.Rate);
            }
        }
        if (concrete.Count > 0)
        {
            r.ConcreteM3.Clear();
            foreach (var kv in concrete) r.ConcreteM3[kv.Key] = kv.Value.Average();
            if (!r.ConcreteM3.ContainsKey("*")) r.ConcreteM3["*"] = concrete.Values.SelectMany(v => v).Average();
            r.From["concrete"] = "rate table";
        }
        return r;
    }

    /// <summary>Lays the user's direct rates over: {"concrete_m3": 150, "rebar_kg": 1.1, "steel_t": 2500, "formwork_m2": 35, "precast_m3": 400, "strand_kg": 5, "pile_m": 100}.</summary>
    public static void Override(Rates r, JsonElement? o)
    {
        if (o is not { ValueKind: JsonValueKind.Object } j) return;
        foreach (var p in j.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Number) throw new ArgumentException($"unit_rates {p.Name} is not a number.");
            var v = p.Value.GetDouble();
            switch (p.Name.ToLowerInvariant())
            {
                case "concrete_m3": r.ConcreteM3.Clear(); r.ConcreteM3["*"] = v; r.From["concrete"] = "given"; break;
                case "rebar_kg": r.RebarKg = v; r.From["rebar"] = "given"; break;
                case "rebar_t": r.RebarKg = v / 1000; r.From["rebar"] = "given"; break;
                case "steel_t": r.SteelT = v; r.From["steel"] = "given"; break;
                case "steel_kg": r.SteelT = v * 1000; r.From["steel"] = "given"; break;
                case "formwork_m2": r.FormworkM2 = v; r.From["formwork"] = "given"; break;
                case "precast_m3": r.PrecastM3 = v; r.From["precast"] = "given"; break;
                case "strand_kg": r.StrandKg = v; r.From["strand"] = "given"; break;
                case "pile_m": r.PileM = v; r.From["pile"] = "given"; break;
                default: throw new ArgumentException($"unit_rates: unknown rate '{p.Name}' (concrete_m3, rebar_kg, steel_t, formwork_m2, precast_m3, strand_kg, pile_m).");
            }
        }
    }

    // ---------------------------------------------------------------- carbon per unit (from the carbon factor table)

    /// <summary>A1-A3 kgCO2e per m³ of concrete of a strength class (cement replacement as the carbon options say).</summary>
    public static double ConcreteCarbonPerM3(string grade, Carbon.Table table, Carbon.Options? o = null)
    {
        var pick = Carbon.Resolve("Concrete " + grade, "", null, [], table, o ?? new Carbon.Options(), forcedClass: "concrete");
        return pick.Factor is { } f ? Carbon.Emissions(f, "concrete", 1, 0, null, new Carbon.Options(), table, null, null, null, out _).A1A3 : 0;
    }

    /// <summary>A1-A3 kgCO2e per kg of a steel product: rebar, sections, decking/sheet (galvanised).</summary>
    public static double SteelCarbonPerKg(string kind, Carbon.Table table)
    {
        var pick = kind switch
        {
            "rebar" => Carbon.Resolve("Reinforcement", "", null, [], table, new Carbon.Options(), forcedClass: "rebar"),
            "deck" => Carbon.Resolve("Galvanised steel deck", "", null, [], table, new Carbon.Options(), forcedClass: "steel"),
            _ => Carbon.Resolve("Steel", "UB", null, [], table, new Carbon.Options(), forcedClass: "steel"),
        };
        return pick.Factor is { } f ? Carbon.Emissions(f, kind == "rebar" ? "rebar" : "steel", 0, 0, 1, new Carbon.Options(), table, null, null, null, out _).A1A3 : 0;
    }

    // ---------------------------------------------------------------- opportunities and ranking

    /// <summary>One VE opportunity: what could change, what it saves, how sure the screening is and which elements.</summary>
    public sealed class Opportunity
    {
        /// <summary>Stable key, so a register keeps its id across runs: check + subject (type, level, class...).</summary>
        public string Key { get; set; } = "";
        public string Check { get; set; } = "";
        public string Item { get; set; } = "";
        public string Current { get; set; } = "";
        public string Proposed { get; set; } = "";
        public double SavingCost { get; set; }
        public double SavingCarbonKg { get; set; }
        /// <summary>high, medium or low.</summary>
        public string Confidence { get; set; } = "low";
        public string Basis { get; set; } = "";
        public string Risk { get; set; } = "";
        public List<long> Ids { get; set; } = [];
        public double Score { get; set; }
        public int Rank { get; set; }
        public string RegisterId { get; set; } = "";
        /// <summary>What ve_apply can do for it: change_type (FromTypes → ToType), grade (ToGrade) or none.</summary>
        public string ApplyKind { get; set; } = "";
        public List<string> FromTypes { get; set; } = [];
        public string ToType { get; set; } = "";
        public string ToGrade { get; set; } = "";
    }

    public static double ConfidenceWeight(string c) => c switch { "high" => 1.0, "medium" => 0.7, _ => 0.4 };

    /// <summary>
    /// Ranks opportunities: score = (cost weight × saving / largest saving + carbon weight × carbon / largest carbon) ×
    /// confidence weight (high 1, medium 0.7, low 0.4). Negative savings score below zero; ties go to the larger cost
    /// saving, then the key. Sets Score and Rank (1 = best) and returns them in rank order.
    /// </summary>
    public static List<Opportunity> Rank(IEnumerable<Opportunity> items, double costWeight = 0.5, double carbonWeight = 0.5)
    {
        var list = items.ToList();
        var maxCost = Math.Max(1e-9, list.Select(o => Math.Abs(o.SavingCost)).DefaultIfEmpty(0).Max());
        var maxCarbon = Math.Max(1e-9, list.Select(o => Math.Abs(o.SavingCarbonKg)).DefaultIfEmpty(0).Max());
        foreach (var o in list)
            o.Score = Math.Round((costWeight * o.SavingCost / maxCost + carbonWeight * o.SavingCarbonKg / maxCarbon) * ConfidenceWeight(o.Confidence), 4);
        var ranked = list.OrderByDescending(o => o.Score).ThenByDescending(o => o.SavingCost).ThenBy(o => o.Key, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;
        return ranked;
    }

    // ---------------------------------------------------------------- register

    public static readonly string[] Statuses = ["proposed", "accepted", "rejected"];

    public sealed class RegisterItem
    {
        public string Id { get; set; } = "";
        public string Key { get; set; } = "";
        public string Check { get; set; } = "";
        public string Item { get; set; } = "";
        public string Current { get; set; } = "";
        public string Proposed { get; set; } = "";
        public double SavingCost { get; set; }
        public double SavingCarbonKg { get; set; }
        public string Confidence { get; set; } = "";
        public string Risk { get; set; } = "";
        public string Status { get; set; } = "proposed";
        public string Owner { get; set; } = "";
        /// <summary>When it first entered the register.</summary>
        public string Date { get; set; } = "";
        public string Updated { get; set; } = "";
        public string Note { get; set; } = "";
        public int Elements { get; set; }
        /// <summary>Found by the latest scan merged in (false: it was not found that time; kept with its status).</summary>
        public bool InLastScan { get; set; } = true;
    }

    public sealed class Register
    {
        public string Model { get; set; } = "";
        public int Next { get; set; } = 1;
        public List<RegisterItem> Items { get; set; } = [];
    }

    public static string IdOf(int n) => "VE-" + n.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Merges a scan into the register: an opportunity already there (same key) keeps its id, status, owner and note and
    /// gets the new figures; a new one gets the next id with status proposed; items the scan did not find stay, marked
    /// not in the last scan. Manual items (no key) are never touched. Sets each opportunity's RegisterId.
    /// </summary>
    public static (int Added, int Updated, int NotFound) Merge(Register reg, IEnumerable<Opportunity> scan, string date)
    {
        int added = 0, updated = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in scan)
        {
            if (o.Key.Length == 0 || !seen.Add(o.Key)) continue;
            var item = reg.Items.FirstOrDefault(i => i.Key == o.Key);
            if (item is null)
            {
                while (reg.Items.Any(i => i.Id == IdOf(reg.Next))) reg.Next++;
                item = new RegisterItem { Id = IdOf(reg.Next++), Key = o.Key, Status = "proposed", Date = date };
                reg.Items.Add(item);
                added++;
            }
            else updated++;
            item.Check = o.Check; item.Item = o.Item; item.Current = o.Current; item.Proposed = o.Proposed;
            item.SavingCost = Math.Round(o.SavingCost, 2); item.SavingCarbonKg = Math.Round(o.SavingCarbonKg, 1);
            item.Confidence = o.Confidence; item.Risk = o.Risk; item.Elements = o.Ids.Count; item.Updated = date; item.InLastScan = true;
            o.RegisterId = item.Id;
        }
        var notFound = 0;
        foreach (var i in reg.Items.Where(i => i.Key.Length > 0 && !seen.Contains(i.Key)))
            if (i.InLastScan) { i.InLastScan = false; i.Updated = date; notFound++; }
        return (added, updated, notFound);
    }

    /// <summary>Adds an item by hand (no key, so scans never change it).</summary>
    public static RegisterItem AddManual(Register reg, string item, string current, string proposed, double cost, double carbonKg, string risk, string owner, string date)
    {
        if (string.IsNullOrWhiteSpace(item)) throw new ArgumentException("A register item needs a description (item).");
        while (reg.Items.Any(i => i.Id == IdOf(reg.Next))) reg.Next++;
        var r = new RegisterItem
        {
            Id = IdOf(reg.Next++), Check = "manual", Item = item.Trim(), Current = current, Proposed = proposed, SavingCost = cost, SavingCarbonKg = carbonKg,
            Risk = risk, Owner = owner, Date = date, Updated = date, Confidence = "",
        };
        reg.Items.Add(r);
        return r;
    }

    /// <summary>Sets an item's status (proposed, accepted, rejected), owner and note; throws on an unknown id or status.</summary>
    public static RegisterItem Set(Register reg, string id, string? status, string? owner, string? note, string date)
    {
        var item = reg.Items.FirstOrDefault(i => i.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No register item '{id}'." + (reg.Items.Count > 0 ? $" Ids run {reg.Items.First().Id} to {reg.Items.Last().Id}." : " The register is empty: run ve_opportunities first."));
        if (status != null)
        {
            var s = status.Trim().ToLowerInvariant();
            if (!Statuses.Contains(s)) throw new ArgumentException($"Status '{status}' is not one of proposed, accepted, rejected.");
            item.Status = s;
        }
        if (owner != null) item.Owner = owner.Trim();
        if (note != null) item.Note = note.Trim();
        item.Updated = date;
        return item;
    }

    public static readonly string[] RegisterHeader =
        ["ID", "Item", "Current", "Proposed", "Saving (cost)", "Saving tCO2e", "Risk", "Status", "Owner", "Date", "Updated", "Confidence", "Check", "Elements", "In last scan", "Note"];

    public static List<object?[]> RegisterRows(Register reg) =>
    [
        RegisterHeader,
        .. reg.Items.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => new object?[]
        {
            i.Id, i.Item, i.Current, i.Proposed, Math.Round(i.SavingCost, 2), Math.Round(i.SavingCarbonKg / 1000, 3), i.Risk, i.Status, i.Owner, i.Date, i.Updated,
            i.Confidence, i.Check, i.Elements, i.Key.Length == 0 ? "manual" : i.InLastScan ? "yes" : "no", i.Note,
        }),
    ];

    // ---------------------------------------------------------------- structural options by rule of thumb

    /// <summary>The grid and loading the options are sized for.</summary>
    public sealed class Grid
    {
        public double SpanX = 8, SpanY = 8;
        /// <summary>Imposed load, kPa (default 3: offices without partitions allowance beyond 1 kPa).</summary>
        public double ImposedKpa = 3;
        public double SuperimposedKpa = 1.5;
        public int Floors = 1;
        /// <summary>Floor area of one floor, m² (default: one bay).</summary>
        public double? FloorAreaM2;
        public double Long => Math.Max(SpanX, SpanY);
        public double Short => Math.Min(SpanX, SpanY);
        public double TotalAreaM2 => (FloorAreaM2 ?? SpanX * SpanY) * Math.Max(1, Floors);
    }

    /// <summary>Quantities of one option per m² of floor (or of footprint, for foundations), with its notes.</summary>
    public sealed class Option
    {
        public string Key = "", Name = "", Source = "rule of thumb", Sizes = "";
        public double ConcreteM3, RebarKg, SteelKg, DeckKg, StrandKg, PrecastM3, FormworkM2, PileM;
        public string Grade = "C32/40", PrecastGrade = "C40/50";
        /// <summary>Relative programme: frame time per floor against an RC flat slab = 1.0 (lower is faster).</summary>
        public double Programme = 1;
        public List<string> Risks = [];
        /// <summary>Per what: "m² of floor" (frames) or "m² of footprint" (foundations).</summary>
        public string Per = "m² of floor";
        public bool Foundation;
    }

    public static readonly string[] OptionKeys = ["flat_slab", "beam_and_slab", "pt_flat_slab", "hollow_core", "steel_composite", "raft", "piles"];

    /// <summary>
    /// Assumptions used by the rule-of-thumb sizing; each can be replaced through the tool's assumptions argument (same
    /// names). Values are typical for ordinary buildings and documented in the report.
    /// </summary>
    public static Dictionary<string, double> DefaultAssumptions() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["flat_slab_ratio"] = 28, ["flat_slab_rebar"] = 120, ["slab_ratio"] = 30, ["slab_rebar"] = 100,
        ["beam_ratio"] = 15, ["beam_rebar"] = 180, ["beam_width_fraction"] = 0.6, ["pt_ratio"] = 40, ["pt_rebar"] = 50, ["pt_strand_kg_m2"] = 5,
        ["hollow_core_solid_fraction"] = 0.55, ["topping_mm"] = 75, ["topping_mesh_kg_m2"] = 3,
        ["steel_base_kg_m2"] = 30, ["steel_kg_m2_per_m_span"] = 2.5, ["deck_slab_m3_m2"] = 0.095, ["deck_kg_m2"] = 10, ["deck_mesh_kg_m2"] = 3,
        ["load_depth_factor"] = 0.03, ["raft_base_m"] = 0.3, ["raft_m_per_floor"] = 0.075, ["raft_min_m"] = 0.45, ["raft_rebar"] = 110,
        ["pile_capacity_kn"] = 1500, ["pile_diameter_m"] = 0.6, ["pile_length_m"] = 20, ["pile_rebar"] = 50, ["pile_cap_m3_per_pile"] = 1.2, ["pile_cap_rebar"] = 120,
        ["dead_load_kpa_per_floor"] = 7,
    };

    public static Dictionary<string, double> ReadAssumptions(JsonElement? o)
    {
        var a = DefaultAssumptions();
        if (o is not { ValueKind: JsonValueKind.Object } j) return a;
        foreach (var p in j.EnumerateObject())
        {
            if (!a.ContainsKey(p.Name)) throw new ArgumentException($"assumptions: unknown '{p.Name}'. Known: {string.Join(", ", a.Keys)}.");
            if (p.Value.ValueKind != JsonValueKind.Number || p.Value.GetDouble() < 0) throw new ArgumentException($"assumptions {p.Name} must be a number of 0 or more.");
            a[p.Name] = p.Value.GetDouble();
        }
        return a;
    }

    /// <summary>Depth multiplier for imposed load above 5 kPa (3% per kPa by default).</summary>
    private static double LoadFactor(Grid g, Dictionary<string, double> a) => 1 + a["load_depth_factor"] * Math.Max(0, g.ImposedKpa + g.SuperimposedKpa - 6.5);

    private static double RoundUp(double mm, double step = 25) => Math.Ceiling(mm / step - 1e-9) * step;

    /// <summary>
    /// Sizes an option for the grid by rule of thumb (quantities per m²). flat_slab: RC flat slab, depth = long span /
    /// ratio; beam_and_slab: RC slab spanning the short way onto RC beams along the long way; pt_flat_slab: PT flat slab;
    /// hollow_core: precast hollow-core units spanning the long way with a structural topping on RC beams; steel_composite:
    /// steel beams and columns with a composite deck; raft and piles: foundations for the floors given.
    /// </summary>
    public static Option SizeOption(string key, Grid g, Dictionary<string, double>? assumptions = null)
    {
        var a = assumptions ?? DefaultAssumptions();
        if (g.SpanX <= 0 || g.SpanY <= 0) throw new ArgumentException("Spans must be above 0.");
        var lf = LoadFactor(g, a);
        var o = new Option { Key = key };
        switch (key)
        {
            case "flat_slab":
            {
                var d = RoundUp(Math.Max(200, g.Long * 1000 / a["flat_slab_ratio"] * lf));
                o.Name = "RC flat slab"; o.Sizes = $"{F(d, 0)} mm slab";
                o.ConcreteM3 = d / 1000; o.RebarKg = o.ConcreteM3 * a["flat_slab_rebar"]; o.FormworkM2 = 1; o.Programme = 1.0;
                o.Risks.AddRange(["punching shear at columns (may need studs or drops)", "long-term deflection on spans over about 9 m", "flat soffit: simple services and formwork"]);
                break;
            }
            case "beam_and_slab":
            {
                var d = RoundUp(Math.Max(150, g.Short * 1000 / a["slab_ratio"] * lf));
                var h = RoundUp(Math.Max(300, g.Long * 1000 / a["beam_ratio"] * lf), 50);
                var b = RoundUp(Math.Max(300, h * a["beam_width_fraction"]), 50);
                var beamM3PerM2 = b / 1000 * (h - d) / 1000 / g.Short;
                o.Name = "RC beam and slab"; o.Sizes = $"{F(d, 0)} mm slab, {F(b, 0)} x {F(h, 0)} mm beams at {F(g.Short)} m";
                o.ConcreteM3 = d / 1000 + beamM3PerM2;
                o.RebarKg = d / 1000 * a["slab_rebar"] + beamM3PerM2 * a["beam_rebar"];
                o.FormworkM2 = 1 + 2 * (h - d) / 1000 / g.Short; o.Programme = 1.3;
                o.Risks.AddRange(["downstand beams reduce ceiling void or raise storey height", "more formwork and slower cycle than a flat slab", "robust and familiar; easy to form openings"]);
                break;
            }
            case "pt_flat_slab":
            {
                var d = RoundUp(Math.Max(180, g.Long * 1000 / a["pt_ratio"] * lf));
                o.Name = "Post-tensioned flat slab"; o.Sizes = $"{F(d, 0)} mm PT slab";
                o.ConcreteM3 = d / 1000; o.RebarKg = o.ConcreteM3 * a["pt_rebar"]; o.StrandKg = a["pt_strand_kg_m2"]; o.FormworkM2 = 1; o.Programme = 0.9;
                o.Grade = "C40/50";
                o.Risks.AddRange(["specialist PT designer and contractor needed", "later openings must avoid tendons (coring needs scanning)", "early stressing on the critical path; restraint cracking at stiff cores"]);
                break;
            }
            case "hollow_core":
            {
                var unit = g.Long <= 6 ? 150 : g.Long <= 7.5 ? 200 : g.Long <= 10 ? 250 : g.Long <= 12 ? 320 : g.Long <= 15 ? 400 : 500;
                var h = RoundUp(Math.Max(300, g.Short * 1000 / a["beam_ratio"] * lf), 50);
                var b = RoundUp(Math.Max(300, h * a["beam_width_fraction"]), 50);
                var beamM3PerM2 = b / 1000 * h / 1000 / g.Long;
                o.Name = "Precast hollow-core with topping"; o.Sizes = $"{unit} mm hollow-core units spanning {F(g.Long)} m, {F(a["topping_mm"], 0)} mm topping, {F(b, 0)} x {F(h, 0)} mm beams";
                o.PrecastM3 = unit / 1000.0 * a["hollow_core_solid_fraction"];
                o.ConcreteM3 = a["topping_mm"] / 1000 + beamM3PerM2;
                o.RebarKg = a["topping_mesh_kg_m2"] + beamM3PerM2 * a["beam_rebar"];
                o.FormworkM2 = 2 * h / 1000 / g.Long + b / 1000 / g.Long; o.Programme = 0.7;
                if (g.Long > 15) o.Risks.Add("span beyond the usual hollow-core range: check with a supplier");
                o.Risks.AddRange(["crane capacity and access for unit lifts", "camber differences between units; topping thickness varies", "openings and services limited to the cores of the units"]);
                break;
            }
            case "steel_composite":
            {
                var kg = (a["steel_base_kg_m2"] + a["steel_kg_m2_per_m_span"] * g.Long) * lf;
                o.Name = "Steel frame with composite deck"; o.Sizes = $"about {F(kg, 0)} kg/m² of steel, 130 mm composite slab on metal deck";
                o.SteelKg = kg; o.DeckKg = a["deck_kg_m2"]; o.ConcreteM3 = a["deck_slab_m3_m2"]; o.RebarKg = a["deck_mesh_kg_m2"]; o.Programme = 0.7;
                o.Risks.AddRange(["fire protection to the steel (not counted)", "floor vibration on long spans", "steel price volatility and lead time; connections design"]);
                break;
            }
            case "raft":
            {
                var t = Math.Max(a["raft_min_m"], a["raft_base_m"] + a["raft_m_per_floor"] * Math.Max(1, g.Floors) + (g.Long - 8) * 0.025);
                t = RoundUp(t * 1000, 50) / 1000;
                o.Name = "RC raft"; o.Sizes = $"{F(t * 1000, 0)} mm raft"; o.Foundation = true; o.Per = "m² of footprint";
                o.ConcreteM3 = t; o.RebarKg = t * a["raft_rebar"]; o.FormworkM2 = 0.05; o.Programme = 1.0;
                o.Risks.AddRange(["needs adequate bearing capacity and tolerable settlement (site investigation)", "large pours: heat of hydration and early-age cracking", "waterproofing and uplift where groundwater is high"]);
                break;
            }
            case "piles":
            {
                var loadKn = g.SpanX * g.SpanY * Math.Max(1, g.Floors) * (a["dead_load_kpa_per_floor"] + g.SuperimposedKpa + g.ImposedKpa);
                var n = Math.Max(1, Math.Ceiling(loadKn / Math.Max(1, a["pile_capacity_kn"])));
                var pileM3 = Math.PI * a["pile_diameter_m"] * a["pile_diameter_m"] / 4 * a["pile_length_m"] * n;
                var capM3 = a["pile_cap_m3_per_pile"] * n;
                var bay = g.SpanX * g.SpanY;
                o.Name = "Bored piles with pile caps"; o.Foundation = true; o.Per = "m² of footprint";
                o.Sizes = $"{n} x {F(a["pile_diameter_m"] * 1000, 0)} mm piles {F(a["pile_length_m"], 0)} m long per column ({F(loadKn, 0)} kN), caps";
                o.ConcreteM3 = (pileM3 + capM3) / bay; o.RebarKg = (pileM3 * a["pile_rebar"] + capM3 * a["pile_cap_rebar"]) / bay;
                o.PileM = n * a["pile_length_m"] / bay; o.FormworkM2 = capM3 / bay * 2; o.Programme = 1.2;
                o.Risks.AddRange(["pile capacity depends on the ground: needs a site investigation and pile tests", "ground slab or suspended ground floor not counted", "piling rig access, spoil disposal and noise"]);
                break;
            }
            default: throw new ArgumentException($"Unknown option '{key}'. Options: {string.Join(", ", OptionKeys)}.");
        }
        return o;
    }

    /// <summary>Cost of one m² of an option at these rates.</summary>
    public static double Cost(Option o, Rates r) =>
        o.ConcreteM3 * r.Concrete(o.Foundation ? "foundation" : "slab") + o.RebarKg * r.RebarKg + (o.SteelKg + o.DeckKg) / 1000 * r.SteelT + o.StrandKg * r.StrandKg
        + o.PrecastM3 * r.PrecastM3 + o.FormworkM2 * r.FormworkM2 + o.PileM * r.PileM;

    /// <summary>A1-A3 kgCO2e of one m² of an option from the carbon factor table.</summary>
    public static double CarbonKg(Option o, Carbon.Table t, Carbon.Options? co = null) =>
        o.ConcreteM3 * ConcreteCarbonPerM3(o.Grade, t, co) + o.PrecastM3 * ConcreteCarbonPerM3(o.PrecastGrade, t, co)
        + (o.RebarKg + o.StrandKg) * SteelCarbonPerKg("rebar", t) + o.SteelKg * SteelCarbonPerKg("section", t) + o.DeckKg * SteelCarbonPerKg("deck", t);

    // ---------------------------------------------------------------- text

    public static string F(double v, int dp = 1) => Carbon.F(v, dp);
}
