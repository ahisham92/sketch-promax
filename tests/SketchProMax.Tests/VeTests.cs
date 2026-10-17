using System.Text.Json;
using SketchProMax.Tools;
using static SketchProMax.Tools.Ve;

/// <summary>Value engineering arithmetic behind the ve_* tools, which needs no Revit.</summary>
internal static class VeTests
{
    public static void Run(Action<string, bool, string> check)
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

        // ---- Span/depth checks
        var r = DefaultRatios();
        check("ve: typical depth is span/ratio with a minimum", TypicalDepthMm(8.4, r["flat_slab"]) == 300 && TypicalDepthMm(3, r["flat_slab"]) == 200, TypicalDepthMm(8.4, r["flat_slab"]).ToString());
        var thick = Thicker(400, 8.4, r["flat_slab"], 0.15);
        check("ve: 400 mm flat slab on 8.4 m is flagged with a quarter of its volume over", thick is { } t1 && t1.TypicalMm == 300 && Math.Abs(t1.ExcessFraction - 0.25) < 1e-9, thick?.ToString() ?? "null");
        check("ve: within the tolerance is not flagged", Thicker(340, 8.4, r["flat_slab"], 0.15) is null && Thicker(344, 8.4, r["flat_slab"], 0.15) is null, "");
        check("ve: no span or size, no finding", Thicker(400, 0, r["flat_slab"], 0.15) is null && Thicker(0, 8, r["flat_slab"], 0.15) is null, "");
        check("ve: beam rule", Thicker(900, 9, r["rc_beam"], 0.15) is { TypicalMm: 600 } && Thicker(650, 9, r["rc_beam"], 0.15) is null, "");
        check("ve: wall minimum governs short walls", Thicker(300, 3.5, r["wall"], 0.15) is { TypicalMm: 200 } && Thicker(220, 3.5, r["wall"], 0.15) is null, "");

        // ---- Settings
        var s = ReadSettings(J("""{"ratios": {"flat_slab": 32, "rc_beam": {"ratio": 16, "min_mm": 350}}, "rebar": {"beam": 160}, "grades": {"column": "C50/60"}, "depth_tolerance": 0.2}"""));
        check("ve: rules override ratios, minimums, rebar, grades, tolerance", s.Ratios["flat_slab"] == (32, 200) && s.Ratios["rc_beam"] == (16, 350) && s.Rebar["beam"] == 160
                                                                           && s.Grades["column"] == "C50/60" && s.DepthTolerance == 0.2 && s.Rebar["slab"] == 100, "");
        var badKey = Throws(() => ReadSettings(J("""{"ratio": {}}""")));
        check("ve: unknown rule setting refused with the known names", badKey?.Contains("Known: ratios") == true, badKey ?? "");
        check("ve: bad grade refused", Throws(() => ReadSettings(J("""{"grades": {"slab": "strong"}}"""))) != null, "");
        check("ve: rules of thumb described for the report", Describe(new Settings()).Count >= 6 && Describe(new Settings())[0].Contains("flat_slab 28"), "");

        // ---- Rebar, grades
        check("ve: rebar above typical", Math.Abs(ExcessRebarKg(1600, 10, 100, 0.25) - 600) < 1e-9 && ExcessRebarKg(1200, 10, 100, 0.25) == 0 && ExcessRebarKg(100, 0, 100, 0.25) == 0, "");
        check("ve: grade steps", GradeSteps(40, 32) == 2 && GradeSteps(35, 32) == 1 && GradeSteps(32, 32) == 0 && GradeSteps(30, 32) == 0 && GradeSteps(50, 32) == 4, "");

        // ---- Grid spans
        var pts = new List<(double, double)>();
        for (var i = 0; i < 4; i++) for (var j = 0; j < 3; j++) { pts.Add((i * 8.0, j * 6.0)); pts.Add((i * 8.0, j * 6.0)); }
        var spans = GridSpans(pts);
        check("ve: grid spans from columns (stacked count once)", spans is { } sp && sp.Short == 6 && sp.Long == 8, spans?.ToString() ?? "null");
        check("ve: too few columns for a grid", GridSpans([(0, 0), (8, 0)]) is null, "");
        check("ve: median", Median([3, 1, 2]) == 2 && Median([4, 1, 2, 3]) == 2.5 && Median([]) == 0, "");

        // ---- Rationalising sizes
        var sizes = new List<Size>
        {
            new("B300x600", [300, 600], 10, 20), new("B300x625", [300, 625], 4, 8), new("B300x650", [300, 650], 2, 4),
            new("B300x900", [300, 900], 3, 9), new("B450x600", [450, 600], 5, 12),
        };
        var groups = Rationalise(sizes, 50, 0.15);
        check("ve: close sizes grouped, distant ones not", groups.Count == 1 && groups[0].Members.Select(m => m.Type).SequenceEqual(["B300x600", "B300x625", "B300x650"]),
            string.Join(" | ", groups.Select(g => string.Join(",", g.Members.Select(m => m.Type)))));
        check("ve: group goes up to its largest size and existing type", groups[0].Target.SequenceEqual([300.0, 650.0]) && groups[0].TargetType == "B300x650", "");
        var extra = 20 * (650.0 / 600 - 1) + 8 * (650.0 / 625 - 1);
        check("ve: extra concrete when sizes go up", Math.Abs(groups[0].ExtraM3 - extra) < 1e-9, groups[0].ExtraM3.ToString());
        var thicknesses = Rationalise([new("S200", [200], 5, 50), new("S225", [225], 2, 20), new("S250", [250], 1, 10), new("S300", [300], 4, 40)], 50, 0.15);
        check("ve: slab thicknesses grouped from the smallest", thicknesses.Count == 1 && thicknesses[0].Members.Count == 2 && thicknesses[0].Target[0] == 225, string.Join(",", thicknesses.SelectMany(g => g.Members.Select(m => m.Type))));
        check("ve: no target type when no member has the size", Rationalise([new("A", [300, 600], 1, 1), new("B", [320, 580], 1, 1)], 50, 0.15) is [{ TargetType: "" } g2] && g2.Target.SequenceEqual([320.0, 600.0]), "");
        check("ve: size text", SizeText([300, 650]) == "300 x 650 mm", SizeText([300, 650]));

        // ---- Rates from a cost_estimate table
        var rates = RatesFrom(
        [
            new("Concrete C32/40 in suspended slabs", "m3", 150), new("Concrete in columns", "m3", 190), new("Reinforcement high yield bars", "t", 1200),
            new("Structural steel sections", "t", 2600), new("Formwork to soffits", "m2", 35), new("Bored piles 600 dia", "m", 110), new("Hollow core planks", "m3", 400),
        ], "AED");
        check("ve: concrete rates by element and an overall one", rates.Concrete("slab") == 150 && rates.Concrete("column") == 190 && rates.Concrete("wall") == 170, $"{rates.Concrete("slab")} {rates.Concrete("column")} {rates.Concrete("wall")}");
        check("ve: rebar per t becomes per kg, steel per t, formwork, piles, precast", rates.RebarKg == 1.2 && rates.SteelT == 2600 && rates.FormworkM2 == 35 && rates.PileM == 110 && rates.PrecastM3 == 400, "");
        check("ve: rates say where they came from", rates.From["concrete"] == "rate table" && rates.From["strand"] == "placeholder" && rates.AnyPlaceholder && rates.Currency == "AED", "");
        check("ve: no table means placeholders", RatesFrom([]).From.Values.All(v => v == "placeholder") && RatesFrom([]).Concrete("slab") == 160, "");
        Override(rates, J("""{"concrete_m3": 140, "steel_kg": 3}"""));
        check("ve: direct rates laid over", rates.Concrete("column") == 140 && rates.SteelT == 3000 && rates.From["steel"] == "given", "");
        check("ve: unknown direct rate refused", Throws(() => Override(rates, J("""{"gold_kg": 1}"""))) != null, "");
        check("ve: class from words", ClassFromWords("Concrete to pile caps") == "foundation" && ClassFromWords("RC core walls") == "wall" && ClassFromWords("Concrete") is null, "");

        // ---- Carbon per unit (from the built-in carbon factor table)
        var table = Carbon.BuiltIn();
        var c32 = ConcreteCarbonPerM3("C32/40", table);
        var c40 = ConcreteCarbonPerM3("C40/50", table);
        check("ve: concrete carbon per m³ from the factor table", Math.Abs(c32 - 0.133 * 2400) < 1e-6 && c40 > c32, $"{c32} {c40}");
        check("ve: rebar, section and deck carbon per kg", SteelCarbonPerKg("rebar", table) == 1.99 && SteelCarbonPerKg("section", table) == 1.55 && SteelCarbonPerKg("deck", table) == 2.76, "");

        // ---- Ranking
        var ranked = Rank(
        [
            new Opportunity { Key = "a", SavingCost = 1000, SavingCarbonKg = 0, Confidence = "high" },
            new Opportunity { Key = "b", SavingCost = 1000, SavingCarbonKg = 5000, Confidence = "low" },
            new Opportunity { Key = "c", SavingCost = 500, SavingCarbonKg = 5000, Confidence = "high" },
            new Opportunity { Key = "d", SavingCost = -200, SavingCarbonKg = -100, Confidence = "low" },
            new Opportunity { Key = "e", SavingCost = 0, SavingCarbonKg = 0, Confidence = "medium" },
        ]);
        check("ve: ranked by weighted saving and confidence", ranked.Select(o => o.Key).SequenceEqual(["c", "a", "b", "e", "d"]), string.Join(",", ranked.Select(o => $"{o.Key}:{o.Score}")));
        check("ve: ranks start at 1", ranked.Select(o => o.Rank).SequenceEqual([1, 2, 3, 4, 5]), "");
        check("ve: cost only weighting", Rank([new Opportunity { Key = "x", SavingCost = 10, SavingCarbonKg = 0, Confidence = "high" }, new Opportunity { Key = "y", SavingCost = 5, SavingCarbonKg = 9000, Confidence = "high" }], 1, 0)[0].Key == "x", "");
        check("ve: empty ranking", Rank([]).Count == 0, "");

        // ---- Register merge, ids and statuses kept
        var reg = new Register();
        var scan1 = new List<Opportunity>
        {
            new() { Key = "depth|slab|S400", Item = "Slab S400", SavingCost = 100, Ids = [1, 2] },
            new() { Key = "grade|beam|C40/50", Item = "Beams C40/50", SavingCost = 50 },
        };
        var m1 = Merge(reg, scan1, "2026-10-01 10:00");
        check("ve: first merge adds with ids VE-001.. and status proposed", m1 == (2, 0, 0) && reg.Items.Select(i => i.Id).SequenceEqual(["VE-001", "VE-002"]) && reg.Items.All(i => i.Status == "proposed")
                                                                          && scan1[0].RegisterId == "VE-001" && reg.Items[0].Elements == 2, m1.ToString());
        Set(reg, "ve-002", "Accepted", "Structures", "agreed at the VE workshop", "2026-10-02 09:00");
        check("ve: status, owner and note set (id and status case-insensitive)", reg.Items[1].Status == "accepted" && reg.Items[1].Owner == "Structures" && reg.Items[1].Note.Length > 0, "");
        check("ve: unknown status and id refused", Throws(() => Set(reg, "VE-001", "maybe", null, null, "d")) != null && Throws(() => Set(reg, "VE-999", "accepted", null, null, "d"))?.Contains("VE-001 to VE-002") == true, "");
        var manual = AddManual(reg, "Omit screed on level 3", "75 mm screed", "power-float finish", 2000, 500, "finish tolerance", "Architect", "2026-10-02");
        check("ve: manual item gets the next id", manual.Id == "VE-003" && manual.Key == "" && manual.Check == "manual", manual.Id);
        var scan2 = new List<Opportunity>
        {
            new() { Key = "grade|beam|C40/50", Item = "Beams C40/50", SavingCost = 80 },
            new() { Key = "rebar|beam|B1", Item = "Beam B1 rebar", SavingCost = 30 },
        };
        var m2 = Merge(reg, scan2, "2026-10-03 10:00");
        var beams = reg.Items.First(i => i.Key == "grade|beam|C40/50");
        check("ve: second merge keeps id, status and owner and updates the figures", beams.Id == "VE-002" && beams.Status == "accepted" && beams.Owner == "Structures" && beams.SavingCost == 80 && beams.Date == "2026-10-01 10:00", "");
        check("ve: new item takes the next free id", reg.Items.First(i => i.Key == "rebar|beam|B1").Id == "VE-004" && scan2[1].RegisterId == "VE-004", "");
        check("ve: item missing from the scan is kept and marked", m2 == (1, 1, 1) && reg.Items.First(i => i.Id == "VE-001") is { InLastScan: false, Status: "proposed" }, m2.ToString());
        check("ve: manual items untouched by scans", reg.Items.First(i => i.Id == "VE-003") is { InLastScan: true, SavingCost: 2000 }, "");
        var back = JsonSerializer.Deserialize<Register>(JsonSerializer.Serialize(reg))!;
        var m3 = Merge(back, scan2, "2026-10-04");
        check("ve: register round-trips through JSON and re-merges with the same ids", back.Items.Count == 4 && m3 == (0, 2, 0) && back.Next == 5 && scan2[0].RegisterId == "VE-002", m3.ToString());
        var rows = RegisterRows(reg);
        check("ve: register rows have the agreed columns", rows[0].Take(10).SequenceEqual(["ID", "Item", "Current", "Proposed", "Saving (cost)", "Saving tCO2e", "Risk", "Status", "Owner", "Date"]) && rows.Count == 5, "");
        check("ve: duplicate keys in one scan merge once", Merge(new Register(), [new() { Key = "k" }, new() { Key = "k" }], "d") == (1, 0, 0), "");

        // ---- Rule-of-thumb sizing of options
        var g = new Grid { SpanX = 8.4, SpanY = 7.2, ImposedKpa = 3, Floors = 6, FloorAreaM2 = 1000 };
        var flat = SizeOption("flat_slab", g);
        check("ve: flat slab depth = long span / 28, to 25 mm", flat.Sizes == "300 mm slab" && Math.Abs(flat.ConcreteM3 - 0.3) < 1e-9 && Math.Abs(flat.RebarKg - 36) < 1e-9, flat.Sizes);
        var bs = SizeOption("beam_and_slab", g);
        check("ve: beam and slab: thinner slab plus beams", bs.Sizes.StartsWith("250 mm slab") && bs.Sizes.Contains("x 600 mm beams") && bs.ConcreteM3 > 0.25 && bs.ConcreteM3 < flat.ConcreteM3 && bs.Programme > flat.Programme, bs.Sizes);
        var pt = SizeOption("pt_flat_slab", g);
        check("ve: PT slab thinner with strand", pt.Sizes == "225 mm PT slab" && pt.StrandKg == 5 && pt.ConcreteM3 < flat.ConcreteM3, pt.Sizes);
        var hc = SizeOption("hollow_core", g);
        check("ve: hollow-core unit from the span table", hc.Sizes.StartsWith("250 mm hollow-core") && Math.Abs(hc.PrecastM3 - 0.25 * 0.55) < 1e-9 && hc.Programme < 1, hc.Sizes);
        var steel = SizeOption("steel_composite", g);
        check("ve: steel tonnage grows with span", Math.Abs(steel.SteelKg - (30 + 2.5 * 8.4)) < 1e-9 && SizeOption("steel_composite", new Grid { SpanX = 12, SpanY = 9 }).SteelKg > steel.SteelKg, steel.SteelKg.ToString());
        var heavy = SizeOption("flat_slab", new Grid { SpanX = 8.4, SpanY = 7.2, ImposedKpa = 10 });
        check("ve: heavier load gives a deeper slab", heavy.ConcreteM3 > flat.ConcreteM3, heavy.Sizes);
        var raft = SizeOption("raft", g);
        var piles = SizeOption("piles", g);
        check("ve: raft and piles are foundations per footprint", raft.Foundation && piles.Foundation && raft.Per == "m² of footprint" && raft.Sizes == "800 mm raft", raft.Sizes);
        check("ve: piles per column from the load and capacity", piles.Sizes.StartsWith("3 x 600 mm piles") && piles.PileM > 0, piles.Sizes);
        var custom = ReadAssumptions(J("""{"flat_slab_ratio": 30, "pile_capacity_kn": 5000}"""));
        check("ve: assumptions override the sizing", SizeOption("flat_slab", g, custom).Sizes == "300 mm slab" && SizeOption("piles", g, custom).Sizes.StartsWith("1 x"), SizeOption("flat_slab", g, custom).Sizes);
        check("ve: unknown option and assumption refused", Throws(() => SizeOption("timber_frame", g)) != null && Throws(() => ReadAssumptions(J("""{"magic": 1}"""))) != null, "");
        check("ve: every option has risks", OptionKeys.All(k => SizeOption(k, g).Risks.Count >= 2), "");

        // ---- Option cost and carbon
        var placeholder = PlaceholderRates();
        check("ve: option cost from the rates", Math.Abs(Cost(flat, placeholder) - (0.3 * 160 + 36 * 1.3 + 1 * 40)) < 1e-9, Cost(flat, placeholder).ToString());
        var flatCarbon = CarbonKg(flat, table);
        check("ve: option carbon = concrete + reinforcement", Math.Abs(flatCarbon - (0.3 * c32 + 36 * 1.99)) < 1e-6, flatCarbon.ToString());
        check("ve: PT uses less concrete carbon than the flat slab here", CarbonKg(pt, table) < flatCarbon, "");
        check("ve: grid total area", g.TotalAreaM2 == 6000 && new Grid { SpanX = 8, SpanY = 6 }.TotalAreaM2 == 48, "");

        check("ve: caution says an engineer must confirm", Caution.Contains("not design") && Caution.Contains("engineer"), "");
    }

    private static string? Throws(Action a)
    {
        try { a(); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }
}
