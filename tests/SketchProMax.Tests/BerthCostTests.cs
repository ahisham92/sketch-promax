using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using SketchProMax.Tools;
using static SketchProMax.Tools.BerthCostMath;

/// <summary>
/// Berth costing (Tools/BerthCostMath, XlsxBook, BerthCostExports): the rules, quantities by hand / model / worked
/// out, the rebuilt pile rate, totals and completeness, saving, deliverables in the berth status, and the three files
/// made from templates built here (a Quantities sheet, a BOQ sheet, a back-up .docx). Generic names only.
/// Set SKETCHPROMAX_BERTH_TEMPLATES to a folder with comparison.xlsx, boq.xlsx and backup.docx to also fill real ones
/// (the files land in its "out" folder).
/// </summary>
internal static class BerthCostTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "spm-berth-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        FolderForTests = Path.Combine(dir, "store");
        try
        {
            Rules(check);
            Money(check);
            Storage(check);
            Deliverables(check);
            Comparison(check, dir);
            Boq(check, dir);
            Backup(check, dir);
            Base(check, dir);
            Real(check);
        }
        finally
        {
            FolderForTests = null;
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void Rules(Action<string, bool, string> check)
    {
        var r = ParseRule("cat=Structural Columns; Legend=FKS*; measure=weight; table=tube; group=Legend");
        check("berth costing: a rule reads its category, filters and measure", r.Count == 1 && r[0].Categories.SequenceEqual(["Structural Columns"])
            && r[0].Filters.Single() == ("Legend", "FKS*", false) && r[0].Measure == "weight" && r[0].Table == "tube" && r[0].Group == "Legend", "");
        var two = ParseRule("cat=Floors; thick=150,300; Comments!=*PIT*; measure=volume OR cat=Floors; Comments=SLAB THICK*; label=Thickening");
        check("berth costing: OR gives a second rule, != a negation, thick a list in mm", two.Count == 2 && two[0].Thick.SequenceEqual([150.0, 300.0])
            && two[0].Filters.Single() == ("Comments", "*PIT*", true) && two[1].Label == "Thickening", two.Count.ToString());
        var over = ParseRule("cat=Floors; over=Legend=FKS*; factor=0.1; round=up")[0];
        check("berth costing: over, factor and round", over.Over == ("Legend", "FKS*") && over.Factor == 0.1 && over.RoundUp && Finish(over, 92) == 10, "");
        check("berth costing: wildcards and lists", Like("FKS-DS01a", "FKS*") && Like("RP1-DS01a", "MP*,RP*") && !Like("SP-DS01a", "MP*,RP*") && Like("abc", "ABC") && !Like("abcd", "abc"), "");
        check("berth costing: numbers read either way", Num("1,234.5") == 1234.5 && Num(" 12 ") == 12 && Num("x") is null, "");
    }

    private static Project Sample(out Berth b)
    {
        var p = new Project();
        foreach (var (code, v) in new[] { ("R_PIPE", 2000.0), ("R_SHEET", 1800.0), ("R_CONC_COMBI", 200.0), ("R_REBAR", 1200.0), ("R_PILE_GIVEN", 1500.0), ("R_CONC_BEAM", 250.0), ("R_CONC_SLAB", 240.0), ("R_CASING", 2100.0) })
            p.RateOf(code)!.Value = v;
        b = AddBerth(p, "Port A", "Berth Number", "9");
        b.Length = 200;
        b.Exclude = "BERTH 9 - PART 1";
        b.ExcludeNote = "excl. RoRo";
        b.Params["MiddleD"] = 1.2; b.Params["MiddleKg"] = 165; b.Params["InfillKg"] = 120; b.Params["DeckKg"] = 150; b.Params["FrontBeamKg"] = 140;
        b.Measured = new Measurement
        {
            At = new DateTime(2026, 10, 23, 9, 0, 0),
            Model = "Sample",
            Quantities = new() { [1] = 866.4, [2] = 331, [3] = 4000, [5] = 1000, [9] = 2000, [12] = 1500 },
            Lines =
            [
                new Line { No = 1, Label = "FKS-01", Count = 40, Top = 3.5, Bottom = -25, Height = 28.5, Diameter = 1.626, Inner = 1.59, Area = 0.0909, Volume = 103.6, Weight = 813.4, Quantity = 813.4, Table = "tube" },
                new Line { No = 1, Label = "FKS-02", Count = 2, Top = 3.5, Bottom = -26, Height = 29.5, Diameter = 1.626, Inner = 1.59, Area = 0.0909, Volume = 5.4, Weight = 53, Quantity = 53, Table = "tube" },
                new Line { No = 5, Label = "MP1", Count = 25, Top = 0.5, Bottom = -39.3, Height = 39.8, Diameter = 1.2, Area = 1.131, Quantity = 995, Table = "pile" },
                new Line { No = 9, Label = "2.00m THICK.", Count = 1, Area = 1000, Thickness = 2, Volume = 2000, Quantity = 2000, Table = "slab" },
            ],
        };
        return p;
    }

    private static void Money(Action<string, bool, string> check)
    {
        var p = Sample(out var b);
        check("berth costing: a berth is Berth 09 with its exclusion", b.Number == "9" && b.Title == "Berth 09 (excl. RoRo)", b.Title);
        var (infill, from) = Quantity(p, b, InfillRebar);
        check("berth costing: infill reinforcement is worked out from m³ × kg/m³", from == Source.Worked && Math.Abs(infill!.Value - 480) < 1e-9, infill.ToString());
        var (super, _) = Quantity(p, b, SuperRebar);
        check("berth costing: superstructure reinforcement sums the concrete items", Math.Abs(super!.Value - (2000 * 140 + 1500 * 150) / 1000.0) < 1e-9, super.ToString());
        b.Hand[2] = 300;
        check("berth costing: a hand quantity wins over the model", Quantity(p, b, 2) == (300, Source.Hand), "");
        // The reference pile (1.2 m, 165 kg/m³) comes back at the given rate.
        check("berth costing: the pile rate rebuilds the given rate at the reference pile", Math.Abs(PileRate(p, 1.2, 165) - 1500) < 1e-6, PileRate(p, 1.2, 165).ToString(CultureInfo.InvariantCulture));
        check("berth costing: a bigger pile costs more", PileRate(p, 1.5, 165) > 1500 && PileRate(p, 0, 165) == 0, "");
        p.Pile.ByVolume = false;
        check("berth costing: diameter scaling also rebuilds the reference", Math.Abs(PileRate(p, 1.2, 165) - 1500) < 1e-6, "");
        p.Pile.ByVolume = true;
        var t = Total(p, b);
        var expected = Costs(p, b).Where(c => c.Item.Group != "C10").Sum(c => c.Cost);
        check("berth costing: structural works total and per lm", Math.Abs(t.Structural - expected) < 1e-6 && Math.Abs(t.PerLm - expected / 200) < 1e-6 && t.ByGroup.Count == Groups.Length, t.Structural.ToString(CultureInfo.InvariantCulture));
        var done = Completeness(p, b);
        check("berth costing: completeness is a percent, less than full with items not found", done is > 0 and < 100, done.ToString());
        check("berth costing: what is missing is said", Missing(p, b).Any(m => m.Contains("not found in the model")), string.Join("; ", Missing(p, b)));
        var empty = AddBerth(p, "Port A", "Berth Number", "10");
        check("berth costing: a berth not measured has no completeness", Completeness(p, empty) is null && Missing(p, empty).Contains("the quay length"), "");
        check("berth costing: money reads short", BerthCostMath.Money(1234567) == "US$ 1.23 M" && BerthCostMath.Money(950) == "US$ 950" && Qty(null) == "–" && Qty(12.345) == "12.35", BerthCostMath.Money(1234567));
        var rebar = Rebar(p, b);
        check("berth costing: reinforcement rows per concrete element", rebar.Any(r => r.Name == "CombiWall" && Math.Abs(r.Weight - 480) < 1e-9) && rebar.Any(r => r.No == 5), string.Join(",", rebar.Select(r => r.Name)));
    }

    private static void Storage(Action<string, bool, string> check)
    {
        var p = Sample(out var b);
        b.Hand[7] = 12;
        p.Items.RemoveAll(i => i.No == 42);
        Save("PRJ1", p);
        var back = Load("PRJ1");
        var bb = back.Berths.Values.Single();
        check("berth costing: a project saves and loads", bb.Length == 200 && bb.Hand[7] == 12 && bb.Measured!.Lines.Count == 4 && back.RateValue("R_PIPE") == 2000 && SavedText("PRJ1").Length > 0, "");
        check("berth costing: items added in an update reach a saved project", back.ItemOf(42) is not null && back.Items.Select(i => i.No).SequenceEqual(back.Items.Select(i => i.No).OrderBy(n => n)), "");
        check("berth costing: an unknown project starts from the defaults", Load("PRJ-none").Berths.Count == 0 && Load("PRJ-none").Items.Count == 42, "");

        var setup = new BerthStatusMath.Setup
        {
            Berths =
            [
                new BerthStatusMath.Berth("Port A", "P1 Berths 9, 10", BerthStatusMath.FindBy.Parameter, "9; 10", "Berth Number", ""),
                new BerthStatusMath.Berth("Port A", "P2", BerthStatusMath.FindBy.WholeModel, "", "", ""),
            ],
        };
        var fresh = new Project();
        var synced = SyncBerths(fresh, setup);
        check("berth costing: berths come from the berth status (each berth of a package)", synced.Count == 2 && synced.Select(s => s.Number).SequenceEqual(["9", "10"])
            && synced[0].Package == "P1 Berths 9, 10" && synced[0].Parameter == "Berth Number", string.Join("|", synced.Select(s => s.Key)));
        synced[0].Length = 150;
        var again = SyncBerths(fresh, setup);
        check("berth costing: syncing again keeps what was set", again.Count == 2 && again[0].Length == 150, "");
    }

    private static void Deliverables(Action<string, bool, string> check)
    {
        var p = Sample(out var b);
        var setup = new BerthStatusMath.Setup
        {
            Deliverables = [new("Design of sections"), new("Bills of quantities"), new("Back-up calculations of the bills"), new("Costing")],
        };
        check("berth costing: each file is its deliverable", DeliverableFor(setup, Made.Comparison) == "Costing" && DeliverableFor(setup, Made.Boq) == "Bills of quantities"
            && DeliverableFor(setup, Made.Backup) == "Back-up calculations of the bills", "");
        var state = new BerthStatusMath.State();
        var name = MarkDelivered(setup, state, [b], Made.Boq, @"C:\out\BOQ Berth 9.xlsx", new DateTime(2026, 10, 23, 10, 0, 0));
        var entry = state.For(b.Key).DeliveredFor("Bills of quantities");
        check("berth costing: making a file marks its deliverable done", name == "Bills of quantities" && entry.Percent == 100 && entry.Comment.Contains("BOQ Berth 9.xlsx"), entry.Comment);
        check("berth costing: no deliverable, nothing marked", MarkDelivered(new BerthStatusMath.Setup(), state, [b], Made.Boq, "x.xlsx", DateTime.Now) is null, "");
    }

    // ---- The comparison workbook ---------------------------------------------------------------------------------------

    private static OfficeXlsx.Row R(params object?[] cells) => new(cells);

    private static void Comparison(Action<string, bool, string> check, string dir)
    {
        var template = Path.Combine(dir, "comparison.xlsx");
        var rows = new List<OfficeXlsx.Row>
        {
            R("", "Bill No.", "", "", "", "", "END"),                 // 1
            R("", "Stage", "", "", "Detailed Design", "", ""),           // 2
            R("", "Site", "", "", "Port A", "", ""),                     // 3
            R("", "Bill / Berth", "", "", "Berth 10", "Spare - new berth", ""), // 4
            R("", "Source document", "", "", "BOQ 10", "", ""),          // 5
            R("", "Quay length (m)", "", "", 292, "", ""),              // 6
            R("", "Remarks", "", "", "", "", ""),                        // 7
            R("", "Middle pile diameter", "", "", 1.2, "", ""),          // 8
            R("", "Deck slab reinforcement", "", "", 150, "", ""),       // 9
            R(1, "Steel tube piles", "ton", "", 1500, 7, ""),           // 10
            R(2, "Sheet piles", "ton", "", 500, 7, ""),                  // 11
            R(3, "RC infill", "m³", "", 6000, 7, ""),                    // 12
            R(4, "Infill rebar", "ton", "", new OfficeXlsx.Formula("E12*0.12"), "", ""), // 13
            R("", "Bill-stated reinforcement excl. piles", "", "", 900, "", ""), // 14
        };
        OfficeXlsx.Write(template, "Comparison", [new OfficeXlsx.Sheet("Quantities", "", [], rows, [], [], false)]);
        var p = Sample(out var b);
        var ten = AddBerth(p, "Port A", "Berth Number", "10");
        ten.Measured = new Measurement { Quantities = new() { [1] = 1600 } };
        var output = Path.Combine(dir, "out", "comparison.xlsx");
        var notes = BerthCostExports.WriteComparison(p, [b, ten], template, output, x => "Model " + x.Number);
        using var book = XlsxBook.Read(output);
        const string S = "Quantities";
        check("berth costing: a known berth keeps its column, a new one takes the spare", book.Number(S, 4, 10) == 1600 && book.Text(S, 5, 4) == "Berth 09 (excl. RoRo)"
            && book.Text(S, 5, 2) == "Detailed Design" && book.Text(S, 5, 3) == "Port A" && book.Text(S, 5, 5) == "Model 9", string.Join(" | ", notes));
        check("berth costing: quantities, length, parameters and remarks are written", book.Number(S, 5, 10) == 866.4 && book.Number(S, 5, 11) == 331 && book.Number(S, 5, 12) == 4000
            && book.Number(S, 5, 6) == 200 && book.Number(S, 5, 8) == 1.2 && book.Text(S, 5, 7).Contains("BERTH 9 - PART 1"), book.Text(S, 5, 7));
        check("berth costing: worked-out rows keep their formulas", book.Formula(S, 4, 13).Length > 0, book.Formula(S, 4, 13));
        check("berth costing: the bill-stated reinforcement is the berth's", book.Number(S, 5, 14) is > 0, "");
        check("berth costing: the workbook recalculates when opened", Entry(output, "xl/workbook.xml").Contains("fullCalcOnLoad=\"1\""), "");

        // No spare column left: said, nothing breaks.
        var eleven = AddBerth(p, "Port A", "Berth Number", "11");
        var notes2 = BerthCostExports.WriteComparison(p, [b, ten, eleven], template, Path.Combine(dir, "out", "comparison2.xlsx"), _ => "");
        check("berth costing: no spare column is said", notes2.Any(n => n.Contains("no spare column")), string.Join(" | ", notes2));
    }

    private static string Entry(string zip, string name)
    {
        using var z = ZipFile.OpenRead(zip);
        using var r = new StreamReader(z.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }

    // ---- The BOQ ---------------------------------------------------------------------------------------------------------

    private static void Boq(Action<string, bool, string> check, string dir)
    {
        check("berth costing: BOQ patterns", BerthCostExports.BoqMatches("Supply of steel tube piles of diameter 1.626 m", "steel tube piles; diameter 1.6")
            && BerthCostExports.BoqMatches("Static pile load test", "^Static pile load") && !BerthCostExports.BoqMatches("Compression static pile load test", "^Static pile load")
            && BerthCostExports.BoqMatches("Slab", "=Slab") && !BerthCostExports.BoqMatches("Slab edge", "=Slab") && BerthCostExports.BoqMatches("Polystrene sheets", "Polystrene|Polystyrene"), "");
        check("berth costing: a text for another berth", BerthCostExports.ForBerth("BERTH 10 - LENGTH 292 METER", "10", "9", 169) == "BERTH 9 - LENGTH 169 METER"
            && BerthCostExports.ForBerth("PRJ1-FD-10-BOQ", "10", "9") == "PRJ1-FD-09-BOQ" && BerthCostExports.ForBerth("Berth (10) Combi wall", "10", "9") == "Berth (9) Combi wall", BerthCostExports.ForBerth("BERTH 10 - LENGTH 292 METER", "10", "9", 169));

        var template = Path.Combine(dir, "BOQ-Berth 10-ST.xlsx");
        var rows = new List<OfficeXlsx.Row>
        {
            R("", "BILL OF QUANTITIES - BERTH 10 - LENGTH 292 METER", "", "", "", ""),
            R("Item", "Description", "Qty", "Unit", "Rate", "Amount"),
            R("A", "Supply and install steel tube piles diameter 1.626 m for 92 piles of height 39.8 m to Tip Level (-25.00) CD", 1500, "ton", "", ""),
            R("B", "Combi-wall reinforced concrete filling", 6000, "m³", "", ""),
            R("C", "Middle Piles", 1100, "lm", "", ""),
            R("D", "Reinforcement Steel (866 Ton)", 900, "ton", "", ""),
            R("E", "Something else", 3, "Nr", "", ""),
        };
        OfficeXlsx.Write(template, "BOQ", [new OfficeXlsx.Sheet("ST", "", [], rows, [], [], false)]);
        var p = Sample(out var b);
        check("berth costing: the BOQ file is named for the berth", BerthCostExports.BoqName(template, b) == "BOQ-Berth 9-ST", BerthCostExports.BoqName(template, b));
        var output = Path.Combine(dir, "out", "boq.xlsx");
        var notes = BerthCostExports.WriteBoq(p, b, template, output);
        using var book = XlsxBook.Read(output);
        check("berth costing: BOQ quantities go in their rows", book.Number("ST", 2, 3) == 866 && book.Number("ST", 2, 4) == 4000 && book.Number("ST", 2, 5) == 1000, string.Join(" | ", notes));
        check("berth costing: BOQ descriptions take the berth's numbers", book.Text("ST", 1, 3).Contains("for 42 piles of height 28.5 m") && book.Text("ST", 1, 1).Contains("BERTH 9 - LENGTH 200 METER"), book.Text("ST", 1, 3) + " / " + book.Text("ST", 1, 1));
        check("berth costing: two tip levels are not guessed, they are noted", book.Text("ST", 1, 3).Contains("(-25.00)") && notes.Any(n => n.Contains("tip levels")), string.Join(" | ", notes));
        check("berth costing: the reinforcement tonnage is the berth's", book.Text("ST", 1, 6).Contains("Ton)") && !book.Text("ST", 1, 6).Contains("866 Ton"), book.Text("ST", 1, 6));
        check("berth costing: a row that is no item is noted and kept", book.Number("ST", 2, 7) == 3 && notes.Any(n => n.Contains("Something else")), "");
    }

    // ---- The Word back-up ------------------------------------------------------------------------------------------------

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static XElement P(string text, string? numId = null, bool tab = false) =>
        new(W + "p", numId is null ? null : new XElement(W + "pPr", new XElement(W + "numPr", new XElement(W + "ilvl", new XAttribute(W + "val", "0")), new XElement(W + "numId", new XAttribute(W + "val", numId)))),
            new XElement(W + "r", new XElement(W + "t", text), tab ? new XElement(W + "tab") : null, tab ? new XElement(W + "t", "x") : null));

    private static XElement Tbl(params string[][] rows) =>
        new(W + "tbl", rows.Select(r => new XElement(W + "tr", r.Select(c => new XElement(W + "tc", new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", c))))))));

    private static void Backup(Action<string, bool, string> check, string dir)
    {
        var template = Path.Combine(dir, "backup.docx");
        var body = new XElement(W + "body",
            P("BILL OF QUANTITIES BACKUP CALCULATION"),
            P("This document provides the backup calculation supporting every Structural Item quantity in the Bill of Quantities for Berth 01 and Berth 02. More."),
            P("Note:"), P("Old note"),
            P("Material Properties & Basis"),
            P("Steel unit weight", tab: true),
            P(""),
            P("Front combi-wall reinforced concrete filling", "5"),
            P("How it is billed."),
            P("Berth 1"),
            Tbl(["Item", "Pile Diameter (m)", "Area (m²)", "Top Level (mCD)", "Bottom Level (mCD)", "Height (m)", "Count", "Volume (m³)"], ["Calculation", "", "", "", "", "", "", ""], ["X", "1", "1", "1", "1", "1", "1", "1"], ["Total", "", "", "", "", "", "1", "1"]),
            Tbl(["Item", "Pile Diameter (m)", "Inner Diameter (m)", "Area (m²)", "Top Level (mCD)", "Bottom Level (mCD)", "Height (m)", "Count", "Volume (m³)", "Weight (Ton)"], ["X", "", "", "", "", "", "", "", "", ""]),
            Tbl(["Item", "Pile Diameter (m)", "Top Level (mCD)", "Bottom Level (mCD)", "Height (m)", "Count", "Quantity (m)"], ["X", "", "", "", "", "", ""], ["Total", "", "", "", "", "", ""]),
            Tbl(["Item", "Area (m²)", "Thickness (m)", "Volume (m³)"], ["X", "", "", ""]),
            P("Combi wall", "1"),
            Tbl(["Item", "Rate (kg/m³)", "Volume (m³)", "Reinforcement Weight (Ton)"], ["X", "", "", ""], ["Total", "", "", ""]),
            new XElement(W + "sectPr"));
        using (var z = ZipFile.Open(template, ZipArchiveMode.Create))
        {
            using var s = z.CreateEntry("word/document.xml").Open();
            new XDocument(new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), body)).Save(s);
        }
        var p = Sample(out var b);
        var output = Path.Combine(dir, "out", "backup.docx");
        var notes = BerthCostExports.WriteBackup(p, [b], template, output);
        var xml = XDocument.Parse(Entry(output, "word/document.xml"));
        var texts = xml.Descendants(W + "p").Where(x => x.Parent?.Name == W + "body").Select(x => string.Concat(x.Descendants(W + "t").Select(t => t.Value))).ToList();
        var tables = xml.Root!.Element(W + "body")!.Elements(W + "tbl").ToList();
        check("berth costing: the back-up names the berths and says what is left out", texts.Any(t => t.Contains("for Berth 09 (excl. RoRo).")) && texts.Any(t => t.Contains("BERTH 9 - PART 1")), string.Join(" / ", texts.Take(6)));
        var order = texts.FindIndex(t => t == "Front combi-wall reinforced concrete filling") is var a && texts.FindIndex(t => t.EndsWith("Steel Combi Wall")) is var c && a >= 0 && c > a;
        check("berth costing: the back-up follows the template's order (infill, then the steel wall)", order, string.Join(" / ", texts));
        var tube = tables.FirstOrDefault(t => string.Concat(t.Descendants(W + "t").Select(x => x.Value)).Contains("FKS-02"));
        check("berth costing: a table row per line, with its total", tube is not null && tube.Elements(W + "tr").Count() == 1 + 2 + 1
            && string.Concat(tube.Elements(W + "tr").Last().Descendants(W + "t").Select(x => x.Value)).StartsWith("Total"), tube?.Elements(W + "tr").Count().ToString() ?? "none");
        check("berth costing: the reinforcement section is built", texts.Contains("Reinforcement") && tables.Any(t => string.Concat(t.Descendants(W + "t").Select(x => x.Value)).Contains("CombiWall")), "");
        check("berth costing: items not measured are noted, not invented", notes.Any(n => n.Contains("no Rear")), string.Join(" | ", notes));
        check("berth costing: the section settings stay last", xml.Root!.Element(W + "body")!.Elements().Last().Name == W + "sectPr", "");
    }

    // ---- Built-in formats (no files needed) --------------------------------------------------------------------------

    private static void Base(Action<string, bool, string> check, string dir)
    {
        var p = Sample(out var b);
        p.Details.HeaderLeft = "PROJECT LINE 1\nPROJECT LINE 2";
        p.Details.DocNumber = "PRJ1-B{nn}-BOQ";
        var unknown = new List<string>();
        var pipe = Describe(p, b, p.ItemOf(1)!, unknown);
        check("berth costing: a BOQ description fills from the model (diameter, count, length)", pipe.Contains("1.626") && pipe.Contains("42 piles") && !pipe.Contains("{"), pipe);
        var mp = Describe(p, b, p.ItemOf(5)!, unknown);
        check("berth costing: the middle piles' description has their tip and diameter", mp.Contains("1200") && mp.Contains("-39.3") && !mp.Contains("{"), mp);
        check("berth costing: {nn} and {length} follow the berth", ForBerth("B{nn} {n} {length}", b) == "B09 9 200", ForBerth("B{nn} {n} {length}", b));

        var boq = Path.Combine(dir, "base-boq.xlsx");
        var notes = BerthCostDocs.WriteBoq(p, b, boq);
        var sheet = Entry(boq, "xl/worksheets/sheet1.xml");
        var strings = sheet;
        check("berth costing: built-in BOQ has its sections, collection and summary", strings.Contains("STRUCTURAL ITEMS") && strings.Contains("COLLECTION") && strings.Contains("To Summary")
            && sheet.Contains("SUM(") && strings.Contains("PROJECT LINE 1"), string.Join(" | ", notes));
        check("berth costing: built-in BOQ has its footer with the berth's number", Entry(boq, "xl/worksheets/sheet1.xml").Contains("PRJ1-B09-BOQ"), "");
        p.Details.HeaderLeft = "";
        p.ItemOf(1)!.InFiles = false;
        BerthCostDocs.WriteBoq(p, b, boq);
        check("berth costing: an item ticked off stays out of the BOQ", !Entry(boq, "xl/worksheets/sheet1.xml").Contains("1.626"), "");
        p.ItemOf(1)!.InFiles = true;

        var costing = Path.Combine(dir, "base-costing.xlsx");
        BerthCostDocs.WriteCosting(p, [b], costing);
        var book = Entry(costing, "xl/workbook.xml");
        check("berth costing: built-in costing has a summary, a sheet per berth and the unit costs", book.Contains("Summary") && book.Contains("Unit Costs") && book.Contains("Berth"), book.Length.ToString());

        var docx = Path.Combine(dir, "base-backup.docx");
        notes = BerthCostDocs.WriteBackup(p, [b], docx);
        var doc = Entry(docx, "word/document.xml");
        var xml = XDocument.Parse(doc);
        check("berth costing: built-in back-up has the berth's tables with totals", doc.Contains("Total") && xml.Descendants(W + "tbl").Any() && doc.Contains("Berth 9"), string.Join(" | ", notes));
        check("berth costing: built-in back-up is a valid Word body", xml.Root!.Element(W + "body")!.Elements().Last().Name == W + "sectPr", "");
        var example = Path.Combine(dir, "base-example.docx");
        BerthCostDocs.WriteBackup(p, [b], example, example: true);
        var again = Path.Combine(dir, "base-again.docx");
        var refill = BerthCostExports.WriteBackup(p, [b], example, again);
        check("berth costing: the exported back-up template fills again as a project template", File.Exists(again) && Entry(again, "word/document.xml").Contains("Total"), string.Join(" | ", refill));
        var boqAgain = Path.Combine(dir, "base-boq-again.xlsx");
        var refillBoq = BerthCostExports.WriteBoq(p, b, boq, boqAgain);
        check("berth costing: the exported BOQ fills again as a project template", File.Exists(boqAgain), string.Join(" | ", refillBoq));

        var keep = Environment.GetEnvironmentVariable("SKETCHPROMAX_BERTH_BASE_OUT");
        if (!string.IsNullOrEmpty(keep)) { Directory.CreateDirectory(keep); foreach (var x in new[] { boq, costing, docx, example, again, boqAgain }) File.Copy(x, Path.Combine(keep, Path.GetFileName(x)), true); }
    }

    // ---- Real files (only on a PC that has them) -------------------------------------------------------------------------

    private static void Real(Action<string, bool, string> check)
    {
        var folder = Environment.GetEnvironmentVariable("SKETCHPROMAX_BERTH_TEMPLATES");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var outDir = Path.Combine(folder, "out");
        Directory.CreateDirectory(outDir);
        var p = Sample(out var b);
        b.Site = Environment.GetEnvironmentVariable("SKETCHPROMAX_BERTH_SITE") ?? b.Site;
        var comparison = Path.Combine(folder, "comparison.xlsx");
        var import = BerthCostExports.Import(p, comparison, [b]);
        Console.WriteLine("  import: " + import);
        var notes = BerthCostExports.WriteComparison(p, [b], comparison, Path.Combine(outDir, "comparison.xlsx"), _ => "Sketch ProMax test");
        Console.WriteLine("  comparison: " + string.Join(" | ", notes));
        notes = BerthCostExports.WriteBoq(p, b, Path.Combine(folder, "boq.xlsx"), Path.Combine(outDir, "boq.xlsx"));
        Console.WriteLine("  boq: " + string.Join(" | ", notes));
        notes = BerthCostExports.WriteBackup(p, [b], Path.Combine(folder, "backup.docx"), Path.Combine(outDir, "backup.docx"));
        Console.WriteLine("  backup: " + string.Join(" | ", notes));
        check("berth costing: real templates filled", File.Exists(Path.Combine(outDir, "backup.docx")), import);
    }
}
