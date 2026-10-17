using System.IO.Compression;
using System.Text.Json;
using SketchProMax.Tools;

/// <summary>Cost plan and what-if arithmetic behind cost_estimate and what_if, which needs no Revit.</summary>
internal static class CostTests
{
    public static void Run(Action<string, bool, string> check)
    {
        static bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol * Math.Max(1, Math.Abs(b));

        // ---- Numbers in table cells
        check("cost: numbers with thousands and decimal commas", Cost.Num("1,234.5") == 1234.5 && Cost.Num("1.234,5") == 1234.5 && Cost.Num("12,5") == 12.5
            && Cost.Num("USD 150") == 150 && Cost.Num("") is null && Cost.Num("-") is null && Cost.Num("2,5", ';') == 2.5, "");

        // ---- The rate table: region, currency, exchange
        var table = Dicts(Cost.SampleTable());
        var north = Cost.SelectRates(table, "north", "USD", new Dictionary<string, double> { ["EUR"] = 1.1 });
        Cost.RateRow R(Cost.Selection s, string r) => s.Rows.Single(x => x.Ref == r);
        check("rates: a region's own row replaces the general one", north.Region == "North" && R(north, "T-01").Rate == 120 && R(north, "T-01").SourceRow == 3, R(north, "T-01").Rate.ToString());
        check("rates: one row per item", north.Rows.Count == 7 && north.Rows.Select(x => x.Ref).Distinct().Count() == 7, north.Rows.Count.ToString());
        check("rates: another currency converted at the exchange rate", Near(R(north, "T-02").Rate, 220) && R(north, "T-02").Currency == "USD" && R(north, "T-02").Source.Contains("EUR"), R(north, "T-02").Source);
        var noFx = Cost.SelectRates(table, "South", "USD");
        check("rates: without an exchange rate the other currency is left out and said", noFx.Rows.All(x => x.Ref != "T-02") && noFx.Notes.Any(n => n.Contains("EUR") && n.Contains("exchange_rates")) && R(noFx, "T-01").Rate == 90,
            string.Join(" | ", noFx.Notes));
        var general = Cost.SelectRates(table, null, "USD");
        check("rates: no region chosen uses the general rows and lists the regions", R(general, "T-01").Rate == 100 && general.Notes.Any(n => n.Contains("North") && n.Contains("South")), string.Join(" | ", general.Notes));
        check("rates: an unknown region is refused with the regions", Throws(() => Cost.SelectRates(table, "East", "USD"))?.Contains("North, South") == true, "");
        var regional = Dicts([["Description", "Unit", "Rate", "Region"], ["Slabs", "m3", "100", "A"], ["Slabs", "m3", "110", "B"]]);
        check("rates: every row regional and several regions: one must be chosen", Throws(() => Cost.SelectRates(regional, null, null))?.Contains("A, B") == true, "");
        var common = Cost.SelectRates(table, "North", null, new Dictionary<string, double> { ["EUR"] = 1.1 });
        check("rates: default currency is the most common one", common.Currency == "USD" && common.Notes.Any(n => n.Contains("most common")), common.Currency);
        check("rates: an empty table is refused with the columns", Throws(() => Cost.SelectRates(Dicts([["Description", "Unit"]]), null, null))?.Contains("template") == true, "");

        // ---- The template, and an .xlsx made here and read back
        var template = Cost.Template();
        check("template: Rates first with the documented columns, then Read me", template[0].Name == "Rates" && template[1].Name == "Read me"
            && template[0].Rows[0].Select(c => c?.ToString()).SequenceEqual(Cost.Columns) && template[1].Rows.Count > 10, "");
        var dir = Path.Combine(Path.GetTempPath(), "spm-cost-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var tfile = Path.Combine(dir, "template.xlsx");
            Xlsx.Write(tfile, template);
            var fromTemplate = Cost.SelectRates(Cells(Xlsx.Read(tfile)), null, null);
            var regionB = Cost.SelectRates(Cells(Xlsx.Read(tfile)), "Region B", null);
            check("template: written, read back and chosen by region", R(fromTemplate, "C-01").Rate == 160 && R(regionB, "C-01").Rate == 175 && fromTemplate.Currency == "USD"
                && fromTemplate.Rows.Count == 12, $"{fromTemplate.Rows.Count} rows");
            var sample = Path.Combine(dir, "rates.xlsx");
            Xlsx.Write(sample, [("Rates", Cost.SampleTable())]);
            var back = Cost.SelectRates(Cells(Xlsx.Read(sample)), "North", "USD", new Dictionary<string, double> { ["EUR"] = 1.1 });
            check("rate-table xlsx: a small table written and read back gives the same rates", back.Rows.Count == north.Rows.Count && back.Rows.Zip(north.Rows).All(p => p.First.Ref == p.Second.Ref && Near(p.First.Rate, p.Second.Rate)), "");

            // Formula cells keep their value for readers that don't calculate, and the workbook recalculates on open.
            var f = Path.Combine(dir, "formula.xlsx");
            Xlsx.Write(f, [("Plan", new List<object?[]> { new object?[] { "Qty", "Rate", "Amount", "Link" }, new object?[] { 2.0, 3.0, new Xlsx.Formula("A2*B2", 6.0), new Xlsx.Formula("HYPERLINK(\"#'Basis'!A2\",\"Basis rows 2-3\")", "Basis rows 2-3") } })]);
            var cells = Xlsx.Read(f);
            string Text(string entry) { using var z = ZipFile.OpenRead(f); using var r = new StreamReader(z.GetEntry(entry)!.Open()); return r.ReadToEnd(); }
            check("xlsx: formula cells with cached values", cells[1][2] == "6" && cells[1][3] == "Basis rows 2-3" && Text("xl/worksheets/sheet1.xml").Contains("<f>A2*B2</f>")
                && Text("xl/workbook.xml").Contains("fullCalcOnLoad"), string.Join(",", cells[1]));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }

        // ---- Add-ons and escalation
        var a = new Cost.AddOns { PrelimsPct = 10, ContingencyPct = 5, VatPct = 5 };
        var b = Cost.Apply(1_000_000, a);
        check("add-ons: prelims on works, contingency on both, VAT on all", Near(b.Prelims, 100_000) && Near(b.Contingency, 55_000) && b.Escalation == 0 && Near(b.Vat, 57_750) && Near(b.Total, 1_212_750),
            $"{b.Prelims} {b.Contingency} {b.Vat} {b.Total}");
        var from = new DateTime(2024, 1, 1); var to = new DateTime(2026, 1, 1);
        var years = Cost.Years(from, to);
        check("escalation: years by 365.25 days", Near(years, 731 / 365.25), years.ToString());
        check("escalation: compounded annual rate", Near(Cost.EscalationFactor(4, from, to), Math.Pow(1.04, 731 / 365.25)) && Near(Cost.EscalationFactor(4, from, from.AddYears(1).AddDays(-0.75)), 1.04, 1e-4), "");
        check("escalation: none without a rate, a date, or a later target", Cost.EscalationFactor(0, from, to) == 1 && Cost.EscalationFactor(4, from, null) == 1 && Cost.EscalationFactor(4, to, from) == 1, "");
        var e = Cost.Apply(1_000_000, new Cost.AddOns { PrelimsPct = 10, ContingencyPct = 5, EscalationPctPerYear = 4, BaseDate = from, TargetDate = to, VatPct = 5 });
        var esc = 1_155_000 * (Math.Pow(1.04, 731 / 365.25) - 1);
        check("escalation: on works, prelims and contingency, before VAT", Near(e.Escalation, esc) && Near(e.Vat, (1_155_000 + esc) * 0.05) && Near(e.Total, (1_155_000 + esc) * 1.05) && Near(e.Years, years),
            $"{e.Escalation} vs {esc}");
        check("add-ons: rows in order ending with the total", Cost.AddOnRows(e, a, "USD") is var rows && rows.Count == 7 && rows[0].Label == "Measured works" && rows[^1].Label == "Total USD" && Near(rows[^1].Amount, e.Total), "");
        check("add-ons: negative or over 100% refused", Throws(() => Cost.Validate(new Cost.AddOns { PrelimsPct = -1 })) != null && Throws(() => Cost.Validate(new Cost.AddOns { VatPct = 120 })) != null, "");
        check("add-ons: escalation needs a target date", Throws(() => Cost.Validate(new Cost.AddOns { EscalationPctPerYear = 3 }))?.Contains("escalation_to") == true
            && Throws(() => Cost.Validate(new Cost.AddOns { EscalationPctPerYear = 3, TargetDate = to })) is null, "");

        // ---- Rates per unit
        var lines = new List<Cost.Line>
        {
            Line("Concrete in slabs", "m3", 100, 12_000, "concrete", ("L1", 8_000), ("L2", 4_000), zones: [("A", 5_000), ("B", 7_000)], kind: "slab"),
            Line("Concrete in walls", "m3", 50, 10_000, "concrete", ("L1", 10_000), kind: "wall"),
            Line("Reinforcement", "t", 10, 10_000, "rebar", ("L1", 10_000), kind: "slab"),
            Line("Steel sections", "kg", 5_000, 15_000, "steel", ("L2", 15_000), kind: "beam"),
            Line("Railings", "m", 40, 10_000, "other", ("L2", 10_000), kind: "railing"),
            new() { Description = "Lump sum", Unit = "", Error = "not measurable" },
        };
        var basis = new Cost.Basis { GfaM2 = 1000, GfaSource = "test", QuayM = 200, QuaySource = "given", ZoneSource = "rooms" };
        basis.GfaByLevel["L1"] = 600; basis.GfaByLevel["L2"] = 400; basis.ZoneAreaM2["A"] = 100; basis.ZoneAreaM2["B"] = 200;
        basis.LengthM["wall"] = 100; basis.LengthM["railing"] = 40;
        var measured = lines.Where(l => l.Error is null).Sum(l => l.Cost);
        var br = Cost.Apply(measured, new Cost.AddOns { PrelimsPct = 10 });
        var u = Cost.UnitRates(lines, br, basis);
        double U(string section, string what) => u.Single(x => x.Section == section && x.What == what).Rate;
        check("per m² GFA: all-in and works", Near(U("Per m² GFA", "All-in (with add-ons)"), 62.7) && Near(U("Per m² GFA", "Measured works"), 57), "");
        check("per m² GFA by level: each level's cost over its area", Near(U("Per m² GFA by level", "L1"), 28_000 / 600.0) && Near(U("Per m² GFA by level", "L2"), 29_000 / 400.0), "");
        check("per m² by zone", Near(U("Per m² by zone", "A"), 50) && Near(U("Per m² by zone", "B"), 35), "");
        check("per linear m: walls and railings over their length; none without a length", Near(U("Per linear m", "Walls"), 100) && Near(U("Per linear m", "Railings"), 250)
            && !u.Any(x => x.What is "Beams" or "Piles"), string.Join(", ", u.Where(x => x.Section == "Per linear m").Select(x => x.What)));
        check("per linear m of quay: all-in and works", Near(U("Per linear m", "Quay or berth, all-in"), 313.5) && Near(U("Per linear m", "Quay or berth, measured works"), 285), "");
        check("per m³ of concrete: concrete items, and all works over the concrete", Near(U("Per m³ of concrete", "Concrete items"), 22_000 / 150.0) && Near(U("Per m³ of concrete", "All measured works over the concrete"), 57_000 / 150.0), "");
        check("per tonne: steel (kg lines in t) and rebar", Near(U("Per tonne", "Structural steel"), 3000) && Near(U("Per tonne", "Reinforcement"), 1000) && Near(Cost.Tonnes(lines, "steel"), 5), "");
        check("per unit: nothing measured, no rate", Cost.UnitRates([], Cost.Apply(0, new Cost.AddOns()), new Cost.Basis()).Count == 0, "");
        check("groups by unit and words", Cost.GroupOf("Concrete in slabs", "m3") == "concrete" && Cost.GroupOf("Blockwork walls", "m3") == "other" && Cost.GroupOf("Rebar to beams", "t") == "rebar"
            && Cost.GroupOf("Steel sections", "kg") == "steel" && Cost.GroupOf("Formwork to soffits", "m2") == "formwork" && Cost.GroupOf("Tiling", "m2") == "other", "");
        check("kinds by category", Cost.KindOf("Walls") == "wall" && Cost.KindOf("Structural Framing") == "beam" && Cost.KindOf("Structural Foundations", pile: true) == "pile"
            && Cost.KindOf("Railings") == "railing" && Cost.KindOf("Floors") == "slab" && Cost.KindOf("Furniture") == "other", "");
        var square = new List<(double, double)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        check("zones: point in polygon and area", Cost.Inside(5, 5, square) && !Cost.Inside(15, 5, square) && Near(Cost.Area(square), 100), "");

        // ---- Programme, options, the what-if delta
        check("programme: one crew per trade in sequence", Near(Cost.FrameWeeks(300, 50, 0, 0), 4) && Near(Cost.FrameWeeks(0, 0, 80, 500, new Cost.Outputs { SteelTPerWeek = 40 }), 4), "");
        var opts = new List<Cost.Figures>
        {
            new() { Name = "Model", Cost = 1_000_000, CarbonKg = 500_000, Weeks = 20 },
            new() { Name = "Flat slab", Cost = 900_000, CarbonKg = 550_000, Weeks = 18 },
            new() { Name = "Steel", Cost = 1_100_000, CarbonKg = 450_000, Weeks = 15 },
        };
        var cmp = Cost.Compare(opts, new Cost.AddOns { PrelimsPct = 10 });
        check("options: totals with add-ons and changes against the first", Near(cmp[1].Total, 990_000) && Near(cmp[1].CostChange, -110_000) && Near(cmp[1].CostPercent!.Value, -10)
            && Near(cmp[2].CarbonChangeKg, -50_000) && Near(cmp[2].WeeksChange, -5), "");
        check("options: cheapest, lowest carbon and fastest marked", cmp[1].Cheapest && cmp[2].LowestCarbon && cmp[2].Fastest && !cmp[0].Cheapest, "");
        check("options: 2 or 3 only", Throws(() => Cost.Compare(opts.Take(1).ToList(), new Cost.AddOns())) != null && Throws(() => Cost.Compare([.. opts, opts[0]], new Cost.AddOns())) != null, "");
        var d = Cost.Delta(new Cost.Figures { Cost = 1000, ConcreteM3 = 100, RebarT = 10, CarbonKg = 50_000, Weeks = 10 },
            new Cost.Figures { Cost = 900, ConcreteM3 = 90, RebarT = 9.5, SteelT = 2, CarbonKg = 45_000, Weeks = 9 }, "USD");
        check("what-if delta: change and change % per metric", d.Count == 6 && d[0].Metric == "Concrete" && Near(d[0].Change, -10) && Near(d[0].Percent!.Value, -10)
            && d[3].Unit == "USD" && Near(d[3].Change, -100) && Near(d[4].Current, 50) && Near(d[4].Change, -5), "");
        check("what-if delta: no % from zero", d[2].Metric == "Structural steel" && d[2].Percent is null && Near(d[2].Change, 2), "");
        check("grid spacing: the first stays, then the spacing", Cost.GridPositions([0, 7.5, 15.2], 8, null).SequenceEqual([0.0, 8, 16]), "");
        check("grid spacing: scale from the first, in the input order", Cost.GridPositions([15, 0, 7], null, 2).SequenceEqual([30.0, 0, 14]), "");
        check("grid spacing: one of spacing or scale, two or more grids", Throws(() => Cost.GridPositions([0, 5], null, null)) != null && Throws(() => Cost.GridPositions([0, 5], 6, 2)) != null
            && Throws(() => Cost.GridPositions([0], 6, null)) != null && Throws(() => Cost.GridPositions([0, 5], -1, null)) != null, "");

        // ---- The chat card and the approval
        var card = Cost.Card("Cost plan: Test", "USD", br, new Cost.AddOns { PrelimsPct = 10 }, u, lines, "- Options: A · B");
        check("summary card: total with the add-ons, key rates, largest lines, short", card.Contains("62,700 USD") && card.Contains("prelims 10%") && card.Contains("Largest: Steel sections 15,000")
            && card.Contains("/m² GFA all-in") && card.Contains("1 line(s) measured nothing") && card.EndsWith("- Options: A · B") && card.Split('\n').Length <= 10, card);
        static IReadOnlyDictionary<string, JsonElement> Args(string json) => JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        check("approval: what_if with confirm is always asked; its preview is not", OutputPaths.AlwaysAskReason("what_if", Args("""{"confirm":true}"""))?.Contains("copy") == true
            && OutputPaths.AlwaysAskReason("what_if", Args("""{"confirm":true,"target":"design_option"}"""))?.Contains("design option") == true
            && OutputPaths.AlwaysAskReason("what_if", Args("""{"confirm":false}""")) is null && OutputPaths.AlwaysAskReason("cost_estimate", Args("""{"confirm":true}""")) is null, "");

        // ---- what_if on a copy: only a saved, non-workshared model (a copy detached with its worksets discarded, then saved)
        check("what_if copy: a saved plain model (e.g. a saved _detached copy without worksets) is allowed", Cost.CopyRefusal(false, false, true) is null, "");
        check("what_if copy: workshared, or detached with its worksets (saved or not), is refused and points to discarding worksets",
            new[] { Cost.CopyRefusal(true, false, true), Cost.CopyRefusal(true, true, false), Cost.CopyRefusal(true, true, true) }.All(r => r?.Contains("Detach and discard worksets") == true), "");
        check("what_if copy: an unsaved model is asked to be saved first", Cost.CopyRefusal(false, false, false)?.Contains("Save the model first") == true, "");

        // ---- A retried drawing_package or redline_apply gets the first run's result
        var a1 = Args("""{"request":"GA plans","levels":["1","2"],"approved":true,"sections":2}""");
        var a2 = Args("""{"sections":2.0,"levels":["1","2"],"request":"GA plans","force":true}""");
        var a3 = Args("""{"request":"GA plans","levels":["2","1"],"sections":2}""");
        var key = RecentRuns.Key("drawing_package", "model-a", a1);
        check("repeat guard: the key ignores order, approved and force; it changes with the inputs and the model",
            key == RecentRuns.Key("drawing_package", "model-a", a2) && key != RecentRuns.Key("drawing_package", "model-a", a3)
            && key != RecentRuns.Key("drawing_package", "model-b", a1) && key != RecentRuns.Key("redline_apply", "model-a", a1), "");
        var t0 = new DateTime(2026, 10, 17, 9, 0, 0);
        check("repeat guard: nothing remembered, nothing returned", RecentRuns.Repeat("drawing_package", key, t0) is null, "");
        RecentRuns.Remember(key, """{"status":"done","counts":{"views":4}}""", t0);
        var again = RecentRuns.Repeat("drawing_package", key, t0.AddMinutes(3));
        check("repeat guard: within 10 minutes the first run's result comes back, said as such",
            again is not null && again.Contains("alreadyDone") && again.Contains("3 minutes") && again.Contains("force true") && again.Contains("\"views\":4"), again ?? "");
        check("repeat guard: after 10 minutes it runs again", RecentRuns.Repeat("drawing_package", key, t0.AddMinutes(11)) is null, "");
        RecentRuns.Forget(key);
        check("repeat guard: force forgets the earlier run", RecentRuns.Repeat("drawing_package", key, t0.AddMinutes(1)) is null, "");

        // ---- Text that looks like a formula stays text (model and table text in the Excel and CSV files)
        check("excel: formula-like text is spotted; plain numbers and text are not",
            Xlsx.FormulaLike("=HYPERLINK(\"x\")") && Xlsx.FormulaLike("+A1") && !Xlsx.FormulaLike("+1") && Xlsx.FormulaLike("@SUM(A1)") && Xlsx.FormulaLike("-2+3") && Xlsx.FormulaLike(" =1")
            && !Xlsx.FormulaLike("-12.5") && !Xlsx.FormulaLike("Concrete") && !Xlsx.FormulaLike("") && !Xlsx.FormulaLike("a=b"), "");
        check("csv: formula-like text gets a leading quote; numbers don't", Carbon.Csv("=1+1") == "'=1+1" && Carbon.Csv("-5") == "-5" && Carbon.Csv("=A1,B1") == "\"'=A1,B1\"", Carbon.Csv("=A1,B1"));
        var fdir = Path.Combine(Path.GetTempPath(), "spm-xlsx-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(fdir);
        try
        {
            var fx = Path.Combine(fdir, "f.xlsx");
            Xlsx.Write(fx, [("S", new List<object?[]> { new object?[] { "Name", "Qty" }, new object?[] { "=cmd|' /C calc'!A0", new Xlsx.Formula("1+1", 2.0) } })]);
            string sheetXml;
            using (var z = ZipFile.OpenRead(fx))
            using (var sr = new StreamReader(z.GetEntry("xl/worksheets/sheet1.xml")!.Open())) sheetXml = sr.ReadToEnd();
            check("excel: formula-like text is an inline string with the quote-prefix style; real formulas stay formulas",
                sheetXml.Contains("<c r=\"A2\" s=\"2\" t=\"inlineStr\">") && !sheetXml.Contains("<f>=cmd") && sheetXml.Contains("<f>1+1</f>")
                && Xlsx.Read(fx)[1][0] == "=cmd|' /C calc'!A0", sheetXml.Length > 400 ? sheetXml.Substring(sheetXml.Length - 400) : sheetXml);

            // A workbook whose first tab is not sheet1.xml (sheets moved in Excel), with a column far out and a phonetic run.
            var odd = Path.Combine(fdir, "odd.xlsx");
            using (var z = ZipFile.Open(odd, ZipArchiveMode.Create))
            {
                void E(string n, string t) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(t); }
                const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                E("xl/workbook.xml", $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Rates\" sheetId=\"2\" r:id=\"rId7\"/><sheet name=\"Old\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                E("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId7\" Target=\"/xl/worksheets/sheet2.xml\"/></Relationships>");
                E("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>wrong sheet</t></is></c></row></sheetData></worksheet>");
                E("xl/worksheets/sheet2.xml", $"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"C1\"><v>5</v></c><c r=\"ZZZZZ1\"><v>9</v></c><c r=\"B1\" t=\"s\"><v>99</v></c></row></sheetData></worksheet>");
                E("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\"><si><t>Rate</t><rPh><t>reto</t></rPh></si></sst>");
            }
            var oddRows = Xlsx.Read(odd);
            check("excel: reads the first tab (from the workbook), skips an impossible column and phonetic text",
                oddRows.Count == 1 && oddRows[0].SequenceEqual(["Rate", "", "5"]), string.Join("|", oddRows.SelectMany(r => r)));
            File.WriteAllText(Path.Combine(fdir, "not.xlsx"), "Description,Rate");
            check("excel: a file that isn't a workbook is refused with a readable error", Throws(() => Xlsx.Read(Path.Combine(fdir, "not.xlsx"))) is { Length: > 0 }, "");
        }
        finally { try { Directory.Delete(fdir, true); } catch (IOException) { } }
    }

    private static Cost.Line Line(string description, string unit, double qty, double cost, string group, (string, double) level, (string, double)? level2 = null,
        (string, double)[]? zones = null, string kind = "other")
    {
        var l = new Cost.Line { Description = description, Unit = unit, Qty = qty, Cost = cost, Rate = cost / qty, Group = group, Elements = 1 };
        l.ByLevel[level.Item1] = level.Item2;
        if (level2 is { } l2) l.ByLevel[l2.Item1] = l2.Item2;
        foreach (var z in zones ?? []) l.ByZone[z.Item1] = z.Item2;
        l.ByKind[kind] = cost;
        return l;
    }

    /// <summary>Rows as Table.Read gives them: a dictionary per row, header names lower case.</summary>
    private static List<Dictionary<string, string>> Dicts(IList<object?[]> rows)
    {
        var header = rows[0].Select(h => (h?.ToString() ?? "").Trim().ToLowerInvariant()).ToList();
        return rows.Skip(1).Select(r =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < r.Length; i++) d[header[i]] = Convert.ToString(r[i], System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return d;
        }).ToList();
    }

    private static List<Dictionary<string, string>> Cells(List<List<string>> rows) => Dicts(rows.Select(r => r.Cast<object?>().ToArray()).ToList());

    private static string? Throws(Action a)
    {
        try { a(); return null; }
        catch (Exception ex) { return ex.Message; }
    }
}
