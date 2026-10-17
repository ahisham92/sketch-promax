using System.IO.Compression;
using SketchProMax.Tools;
using static SketchProMax.Tools.Carbon;

/// <summary>Embodied carbon arithmetic behind embodied_carbon, carbon_compare and carbon_view, which needs no Revit.</summary>
internal static class CarbonTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // The built-in table
        var t = BuiltIn();
        check("carbon: built-in table has a version", t.Version == "2026.2", t.Version);
        check("carbon: built-in table reads without problems", t.Problems.Count == 0, string.Join("; ", t.Problems));
        check("carbon: built-in table has materials, modes, distances and waste rates",
            t.Materials.Count > 40 && t.Modes.Count == 3 && t.Distances.Count == 4 && t.Waste.Count >= 5, $"{t.Materials.Count} {t.Modes.Count} {t.Distances.Count} {t.Waste.Count}");
        var unsourced = t.Materials.Concat(t.Modes).Concat(t.Distances).Concat(t.Waste).Where(f => f.Source.Length == 0).Select(f => f.Id).ToList();
        check("carbon: every built-in row names its source", unsourced.Count == 0, string.Join(", ", unsourced));
        var unmarked = t.Materials.Where(f => f.Class != "air" && f.Note.IndexOf("placeholder", StringComparison.OrdinalIgnoreCase) < 0).Select(f => f.Id).ToList();
        check("carbon: every built-in material factor is marked a placeholder", unmarked.Count == 0, string.Join(", ", unmarked));
        check("carbon: built-in text says generic placeholders", BuiltInText().Contains("GENERIC PLACEHOLDERS"), "");
        var grades = t.Materials.Where(f => f.Class == "concrete").GroupBy(f => f.Replacement).ToDictionary(g => g.Key, g => g.Count());
        check("carbon: concrete grades in all three replacement variants", grades.Count == 3 && grades.Values.Distinct().Count() == 1, string.Join(", ", grades.Select(kv => $"{kv.Key}={kv.Value}")));
        var lower = t.Materials.Where(f => f.Class == "concrete" && f.Replacement == "none")
            .All(c => t.Materials.Where(f => f.Class == "concrete" && f.Grade == c.Grade && f.Replacement != "none").All(f => f.Value < c.Value));
        check("carbon: cement replacement lowers every concrete factor", lower, "");

        // Material classes
        check("carbon: concrete by name", ClassOf("Concrete, Cast-in-Place gray", null) == "concrete" && ClassOf("RC Slab 250", null) == "concrete" && ClassOf("Steel fibre concrete", null) == "concrete", "");
        check("carbon: steel with MPa is steel, not concrete", ClassOf("Metal - Steel 345 MPa", null) == "steel", ClassOf("Metal - Steel 345 MPa", null) ?? "null");
        check("carbon: grade-only name is concrete", ClassOf("CL_40MPa", null) == "concrete" && ClassOf("Mix C32/40", null) == "concrete", "");
        check("carbon: masonry before concrete", ClassOf("Concrete Masonry Units", null) == "masonry" && ClassOf("Blockwork 200", null) == "masonry", "");
        check("carbon: glass wool is insulation, aluminium before metal", ClassOf("Glass wool 100", null) == "insulation" && ClassOf("Metal - Aluminum", null) == "aluminium", "");
        check("carbon: Revit material class fallback", ClassOf("CL_Mat_01", "Concrete") == "concrete" && ClassOf("XYZ", "Wood") == "timber" && ClassOf("Default", "Generic") is null, "");

        // Concrete strength and cement replacement from names
        check("carbon: strength from C32/40, MPa, fc', psi, C40", CylinderStrength("C32/40") == 32 && CylinderStrength("FL_40MPa_Raft") == 40 && CylinderStrength("fc'=35") == 35
                                                                && CylinderStrength("5000 psi") == 34.5 && CylinderStrength("Concrete C40") == 40, "");
        check("carbon: N/mm² read as cube strength", CylinderStrength("Concrete 40N/mm2") == 32, CylinderStrength("Concrete 40N/mm2")?.ToString() ?? "null");
        check("carbon: no strength in a plain name", CylinderStrength("Concrete - Cast in place") is null && CylinderStrength("SC200 block") is null, "");
        check("carbon: cement replacement from names", ReplacementOf("C32/40 50% GGBS") == "ggbs50" && ReplacementOf("25% PFA") == "fa25" && ReplacementOf("CEM I") == "none"
                                                         && ReplacementOf("CEM II/A") is null && ReplacementOf("Concrete") is null, "");

        // Factor matching
        var o = new Options();
        Pick P(string material, string type = "", Options? opt = null, IReadOnlyList<Factor>? custom = null, string? cls = null) =>
            Resolve(material, type, cls, custom ?? [], t, opt ?? o);
        var c40 = P("Concrete C40/50");
        check("carbon: concrete grade and default replacement", c40.Factor?.Id == "conc-c40-fa25" && c40.Note?.Contains("25% fly ash") == true, $"{c40.Factor?.Id} {c40.Note}");
        check("carbon: grade from the type name", P("Concrete", "FL_35MPa_Slab").Factor?.Id == "conc-c35-fa25", P("Concrete", "FL_35MPa_Slab").Factor?.Id ?? "");
        var c37 = P("Concrete 37MPa");
        check("carbon: between classes takes the next one up", c37.Factor?.Grade == "C40/50" && c37.Note?.Contains("next class up") == true, $"{c37.Factor?.Grade} {c37.Note}");
        var plain = P("Concrete - Cast in place");
        check("carbon: unknown grade assumed C32/40 and said so", plain.Factor?.Id == "conc-c32-fa25" && plain.Note?.Contains("assumed C32/40") == true, plain.Note ?? "");
        check("carbon: stated replacement wins unless forced", P("C32/40 CEM I concrete").Factor?.Id == "conc-c32-cem1"
            && P("C32/40 CEM I concrete", opt: new Options { Replacement = "ggbs50", ReplacementAll = true }).Factor?.Id == "conc-c32-ggbs50", "");
        check("carbon: grade swap", P("Concrete C40/50", opt: new Options { GradeSwap = ParseGradeSwap("C40/50>C32/40") }).Factor?.Grade == "C32/40", "");
        check("carbon: above the table's range", P("Concrete C90/105").Note?.Contains("above the table") == true, P("Concrete C90/105").Note ?? "");
        check("carbon: steel sections, hollow, stainless", P("Metal - Steel 345 MPa", "UB 305x165x40").Factor?.Id == "steel-section"
            && P("Steel S355", "SHS 100x100x5").Factor?.Id == "steel-hollow" && P("Stainless Steel").Factor?.Id == "steel-stainless", "");
        check("carbon: timber products", P("Wood - Glulam").Factor?.Id == "timber-glulam" && P("CLT panel").Factor?.Id == "timber-clt" && P("Softwood, sawn").Factor?.Id == "timber-sawn", "");
        check("carbon: rebar forced class", Resolve("Reinforcement", "T16", null, [], t, o, "rebar").Factor?.Id == "rebar", "");
        var none = P("Default", "Basic Wall");
        check("carbon: unknown material is unmapped with a reason", none.Factor is null && none.Note?.Contains("no factor") == true, none.Note ?? "");
        check("carbon: short codes match whole words only", NameMatches("CLT panel 200", "CLT") && !NameMatches("Cycltx", "CLT") && NameMatches("Glulam beam", "*glulam*"), "");

        // A user's table: parsing, then it wins over the built-in rows
        var custom = Parse("""
            Material,Class,Unit,kgCO2e per unit,Stage,Source,Density
            *C40/50*,,kgCO2e/m³,350,A1-A3,Supplier EPD 123,2400
            rebar|reinforcement,rebar,per tonne,760,,Mill EPD,
            Bad row,,furlong,1,A1-A3,x,
            Glass,,kg,abc,,,
            Brick,,kg,0.2,B1,,
            ,steel,kg,1.1,A1-A3,Section EPD,
            """, custom: true);
        check("carbon: custom table rows read", custom.Materials.Count == 3, string.Join(" | ", custom.Materials.Select(f => f.Match + f.Class)));
        check("carbon: custom table problems listed", custom.Problems.Count == 3 && custom.Problems.Any(p => p.Contains("furlong")) && custom.Problems.Any(p => p.Contains("no kgCO2e"))
                                                      && custom.Problems.Any(p => p.Contains("stage 'B1'")), string.Join("; ", custom.Problems));
        check("carbon: custom units normalised", custom.Materials[0].Unit == "m3" && custom.Materials[1].Unit == "t" && custom.Materials[1].Stage == "A1-A3", $"{custom.Materials[0].Unit} {custom.Materials[1].Unit}");
        var mine = Resolve("Concrete C40/50", "", null, custom.Materials, t, o);
        check("carbon: custom row used before the built-in one", mine.Factor?.Source == "Supplier EPD 123" && mine.Factor.Custom, mine.Factor?.Id ?? "");
        check("carbon: custom row by class only", Resolve("Steel S355", "", null, custom.Materials, t, o).Factor?.Source == "Section EPD", "");
        check("carbon: custom row doesn't catch other materials", Resolve("Concrete C32/40", "", null, custom.Materials, t, o).Factor?.Custom == false, "");
        var semi = Parse("Material;Unit;kgCO2e/unit;Source\nBrick;kg;0,2;Supplier\n", custom: true);
        check("carbon: ;-separated table with decimal commas", semi.Materials.Count == 1 && Math.Abs(semi.Materials[0].Value - 0.2) < 1e-9, string.Join("; ", semi.Problems));
        var withSource = Parse("material,unit,factor\nTimber,kg,0.3\n", custom: true);
        check("carbon: custom row without a source says so", withSource.Materials[0].Source.Contains("no source"), withSource.Materials[0].Source);

        // Units
        check("carbon: units from their many spellings", NormalizeUnit("kgCO2e/m³") == "m3" && NormalizeUnit("per tonne") == "t" && NormalizeUnit("kg CO2e / kg") == "kg"
                                                       && NormalizeUnit("m²") == "m2" && NormalizeUnit("t.km") == "t.km" && NormalizeUnit("furlong") is null, "");
        check("carbon: numbers with decimal commas", Number("0,138") == 0.138 && Number("1,234.5") == 1234.5 && Number("1.234,5") == 1234.5 && Number("x") is null, "");
        check("carbon: quantity in the factor's unit", InUnit("kg", 2, 0, null, 2400) == 4800 && InUnit("t", 2, 0, null, 2400) == 4.8 && InUnit("m3", 0, 0, 7850, 7850) == 1
                                                        && InUnit("m2", 1, 0, null, 2400) is null && InUnit("kg", 0, 0, 500, 7850) == 500, "");

        // Stages
        var c32 = t.Materials.First(f => f.Id == "conc-c32-fa25");
        var a13Only = Emissions(c32, "concrete", 1, 0, null, new Options(), t, null, null, null, out var q);
        check("carbon: A1-A3 = quantity x factor", Math.Abs(a13Only.A1A3 - 2400 * 0.133) < 1e-9 && a13Only.A4 == 0 && a13Only.A5 == 0 && q == 2400, $"{a13Only}");
        var all = Emissions(c32, "concrete", 1, 0, null, new Options { A4 = true, A5 = true }, t, null, null, null, out _);
        var a4 = 2.4 * 20 * 0.107;
        check("carbon: A4 = t x km x mode factor (concrete 20 km by road)", Math.Abs(all.A4 - a4) < 1e-9, $"{all.A4}");
        check("carbon: A5 = w/(1-w) x (A1-A3 + A4)", Math.Abs(all.A5 - 5.0 / 95 * (2400 * 0.133 + a4)) < 1e-9 && Math.Abs(all.Total - (all.A1A3 + all.A4 + all.A5)) < 1e-12, $"{all.A5}");
        var steel = t.Materials.First(f => f.Id == "steel-section");
        var rail = Emissions(steel, "steel", 0, 0, 1000, new Options { A4 = true, A4Mode = "rail", A4DistanceKm = 100 }, t, null, null, null, out _);
        check("carbon: A4 with given distance and mode", Math.Abs(rail.A4 - 1 * 100 * 0.028) < 1e-9 && Math.Abs(rail.A1A3 - 1550) < 1e-9, $"{rail}");
        check("carbon: steel uses the national distance", Distance("steel", new Options(), t, null) == 120 && Distance("masonry", new Options(), t, null) == 80 && Distance("concrete", new Options(), t, null) == 20, "");
        check("carbon: waste rates per class", WasteRate("masonry", new Options(), t, null) == 20 && WasteRate("glass", new Options(), t, null) == 5 && WasteRate("steel", new Options { WastePercent = 3 }, t, null) == 3, "");
        check("carbon: waste factor and its limits", Math.Abs(WasteFactor(20) - 0.25) < 1e-12 && Throws(() => WasteFactor(100)) && Throws(() => ModeFactor("air freight", t, null)), "");
        var userA4 = new Factor { Unit = "t", Value = 5, Stage = "A4" };
        check("carbon: a user's A4 row replaces the computed transport", Math.Abs(Emissions(c32, "concrete", 1, 0, null, new Options { A4 = true }, t, null, userA4, null, out _).A4 - 12) < 1e-9, "");

        // Rebar rates, intensity
        check("carbon: rebar rates default and override", RebarRate("Floors", null) == 110 && RebarRate("Floors", new Dictionary<string, double> { ["floors"] = 90 }) == 90 && RebarRate("Other", null) == 100, "");
        check("carbon: intensity per m²", Intensity(50000, 1000) == 50 && Intensity(50000, 0) is null && Intensity(1, null) is null, "");
        check("carbon: grade swaps parsed", ParseGradeSwap("C40/50>C32/40, 50MPa -> 40MPa") is var gs && gs[40] == 32 && gs[50] == 40 && Throws(() => ParseGradeSwap("C40 to nothing")), "");

        // Breakdowns and comparison
        Line L(string level, string cls, string material, double a13, double a4v = 0, int n = 1) =>
            new() { Level = level, Class = cls, Material = material, Type = "T", Category = "Floors", A1A3 = a13, A4 = a4v, Elements = n };
        var lines = new Dictionary<string, Line>();
        Add(lines, L("L1", "concrete", "C32", 100));
        Add(lines, L("L1", "concrete", "C32", 50, 10));
        Add(lines, L("L2", "rebar", "Rebar", 40));
        check("carbon: lines with the same keys add up", lines.Count == 2 && lines.Values.First().A1A3 == 150 && lines.Values.First().Elements == 2, "");
        var byLevel = By(lines.Values, l => l.Level);
        check("carbon: breakdown sorted with shares", byLevel[0].Key == "L1" && byLevel[0].Total == 160 && Math.Abs(byLevel.Sum(r => r.SharePercent) - 100) < 1e-9, "");
        check("carbon: top contributors", Top(lines.Values, 1).Single().Total == 160, "");
        var after = new[] { L("L1", "concrete", "C32", 80), L("L2", "rebar", "Rebar", 40), L("L3", "timber", "CLT", 5) };
        var diff = Compare(lines.Values, after, l => l.Class);
        check("carbon: comparison shows savings first", diff[0].Key == "concrete" && diff[0].Change == -80 && diff[0].ChangePercent == -50
                                                         && diff.Single(d => d.Key == "timber").ChangePercent is null && diff.Single(d => d.Key == "rebar").Change == 0, "");
        var result = new Result { GiaM2 = 10, Lines = lines.Values.ToList() };
        check("carbon: result totals and intensity", result.Total == 200 && result.A4 == 10 && result.Intensity == 20, "");
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var back = System.Text.Json.JsonSerializer.Deserialize<Result>(json)!;
        check("carbon: a saved result reads back", back.Total == 200 && back.Lines.Count == 2 && back.GiaM2 == 10, "");

        // Colour bands for the 3D view
        var bands = Bands(Enumerable.Range(1, 10).Select(i => (double)i), 5);
        check("carbon: equal-count bands", bands.SequenceEqual([2.0, 4, 6, 8, 10]), string.Join(",", bands));
        check("carbon: band of a value", BandOf(5, bands) == 2 && BandOf(1, bands) == 0 && BandOf(99, bands) == 4, "");
        check("carbon: bands with few distinct values", Bands([3, 3, 3, 3], 5).Count == 1 && Bands([], 5).Count == 0, "");
        var pal = Palette(5);
        check("carbon: palette green to red", pal[0] == (26, 150, 65) && pal[4] == (215, 25, 28) && Palette(2).Count == 2, "");

        // Excel
        check("xlsx: column letters", Xlsx.Column(0) == "A" && Xlsx.Column(25) == "Z" && Xlsx.Column(26) == "AA" && Xlsx.Column(27) == "AB" && Xlsx.Column(701) == "ZZ", "");
        var file = Path.Combine(Path.GetTempPath(), $"spm-carbon-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            Xlsx.Write(file, [("Summary", [new object?[] { "Item", "Value" }, ["Total & <more>", 12.5]]), ("By level", [new object?[] { "Level" }, ["L1"]]), ("Summary", [new object?[] { "x" }])]);
            using var zip = ZipFile.OpenRead(file);
            string Text(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
            var sheet = Text("xl/worksheets/sheet1.xml");
            var book = Text("xl/workbook.xml");
            check("xlsx: workbook with its sheets, text escaped, numbers as numbers",
                book.Contains("name=\"Summary\"") && book.Contains("name=\"By level\"") && book.Contains("name=\"Summary 2\"") && zip.GetEntry("xl/worksheets/sheet3.xml") != null
                && sheet.Contains("Total &amp; &lt;more&gt;") && sheet.Contains("<v>12.5</v>") && sheet.Contains("state=\"frozen\""), book);
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (ArgumentException) { return true; }
    }
}
