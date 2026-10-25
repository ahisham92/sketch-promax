using static SketchProMax.Tools.ModelRules;

/// <summary>Rules behind arch_to_structure, create_raft, link_model and check_model, which need no Revit.</summary>
internal static class ModelRulesTests
{
    public static void Run(Action<string, bool, string> check)
    {
        string? Floor(string type, bool structural = false, string? material = null, double mm = 200) =>
            FloorSkipReason(new FloorFacts(type, structural, material, mm), FloorStructureWords, FloorFinishWords, 100);

        check("floors: concrete type name is taken", Floor("CL_Concrete_250") is null, Floor("CL_Concrete_250") ?? "");
        check("floors: Structural flag is taken", Floor("Generic 300mm", structural: true) is null, "");
        check("floors: concrete material is taken", Floor("Generic 300mm", material: "Concrete, Cast-in-Place Concrete") is null, "");
        check("floors: finish type skipped", Floor("FL_Finish_100")?.Contains("finish") == true, Floor("FL_Finish_100") ?? "");
        check("floors: finish word doesn't override concrete structure", Floor("FL_Finish_100", material: "Concrete") is null, Floor("FL_Finish_100", material: "Concrete") ?? "");
        check("floors: Structural flag beats finish words", Floor("Roof Layers_510 mm", structural: true) is null, Floor("Roof Layers_510 mm", true) ?? "");
        check("floors: structural word beats finish words", Floor("RC Slab with Screed 250") is null, Floor("RC Slab with Screed 250") ?? "");
        check("floors: screed and tile skipped", Floor("Screed 50") != null && Floor("FL_Tiles_20") != null && Floor("Ceramic Tile 10mm") != null, "");
        check("floors: thin non-structural floor skipped", Floor("Generic 50mm", material: "Concrete", mm: 50)?.Contains("thin") == true, "");
        check("floors: thin structural floor kept", Floor("Thin RC", structural: true, mm: 60) is null, "");
        check("floors: plain generic floor skipped", Floor("Generic 300mm", material: "Default")?.StartsWith("not structural") == true, "");
        check("floors: short words need word ends", FirstWord("FL_RC_250", ["RC"]) == "RC" && FirstWord("Source Floor", ["RC"]) is null && FirstWord("PTFE 10", ["PT"]) is null, "");
        check("floors: no exclude words still skips non-structural", FloorSkipReason(new FloorFacts("FL_Finish_100", false, null, 100), FloorStructureWords, [], 100)?.StartsWith("not structural") == true, "");

        // arch_to_structure never says a bare "Nothing was made.": counts, reasons per group and the argument to change.
        var skips = new List<ArchSkip>
        {
            new("columns: architectural Columns category (usually cladding; set include_architectural_columns to convert)", 1, ["Rectangular Column: 18 x 18"]),
            new("floors: not structural (no Structural flag, concrete structural material or structural word in the type name)", 2, ["Generic - 12\""]),
        };
        var found = new Dictionary<string, int> { ["walls"] = 0, ["architectural columns"] = 1, ["structural columns"] = 0, ["floors"] = 2 };
        var nothing = NothingMadeMessage("FieldTest-Arch.rvt", null, ["columns", "floors"], found, skips, [], 0);
        check("arch: nothing-made message counts what it found", nothing.Contains("Found 1 architectural columns, 2 floors"), nothing);
        check("arch: nothing-made message names each reason with its count and types", nothing.Contains("architectural Columns category") && nothing.Contains(": 2 [Generic - 12\"]"), nothing);
        check("arch: hint for architectural columns", nothing.Contains("include_architectural_columns=true"), nothing);
        check("arch: hint for floors names floor_type_words with a word from the types", nothing.Contains("floor_type_words") && nothing.Contains("'Generic'"), nothing);
        check("arch: never a bare message", nothing != "Nothing was made." && nothing.Length > 60, nothing);
        var empty = NothingMadeMessage("this model", "L9", ["walls", "columns", "floors", "openings"], new Dictionary<string, int> { ["walls"] = 0 }, [], [], 0);
        check("arch: empty source says so and points at the level", empty.Contains("no walls, columns, floors") && empty.Contains("level name"), empty);
        var refused = NothingMadeMessage("this model", null, ["floors"], new Dictionary<string, int> { ["floors"] = 1 }, [], ["floor 12: boundary is open"], 3);
        check("arch: Revit's refusals and earlier runs are reported", refused.Contains("Revit refused: floor 12") && refused.Contains("3 element(s) were already converted"), refused);
        check("arch: thin floors hint at min_floor_thickness", ArchSkipHints([new("floors: thin non-structural floor (under 100 mm)", 1, [])], 0).Single().Contains("min_floor_thickness"), "");
        check("arch: wall hint", ArchSkipHints([new("walls: not load-bearing (no Structural flag, load-bearing word or thickness)", 4, ["Basic Wall: Generic - 8\""])], 0).Single().Contains("wall_type_words"), "");

        check("raft: thickness replaces the size, not the grade", WithThickness("FL_40MPa_Raft_1200", 1500) == "FL_40MPa_Raft_1500", WithThickness("FL_40MPa_Raft_1200", 1500));
        check("raft: mm suffix kept", WithThickness("Raft 1000mm", 1500) == "Raft 1500mm", WithThickness("Raft 1000mm", 1500));
        check("raft: thickness appended when absent", WithThickness("FL_40MPa_Raft", 1500) == "FL_40MPa_Raft_1500" && WithThickness("Mat Foundation", 900) == "Mat Foundation 900mm", WithThickness("FL_40MPa_Raft", 1500));
        check("raft: wildcard match", Wildcard("FL_40MPa_Raft_1500", "fl_*raft*") && !Wildcard("FL_40MPa_SOG_400", "FL_*Raft*") && Wildcard("FL_40MPa_SOG_400", "40MPa"), "");

        var lineBars = new[] { ("Bar Tag", false), ("Additional Bars RFT", true), ("Rebar Mesh 2", false), ("Bottom Rebar", true), ("Column RFT Section", false), ("Generic Line", true) };
        var bars = RankDetailFamilies(AdditionalBarsRule, lineBars);
        check("rft families: additional bars are line-based bar/rft names, best first", bars.SequenceEqual(["Additional Bars RFT", "Bottom Rebar"]), string.Join(", ", bars));
        var mesh = RankDetailFamilies(MeshRule, lineBars);
        check("rft families: mesh needs 'mesh' and a point family", mesh.SequenceEqual(["Rebar Mesh 2"]), string.Join(", ", mesh));
        var col = RankDetailFamilies(ColumnSectionRule, [("Column RFT Section", false), ("Column Tag", false), ("Column Rebar Ties", false)]);
        check("rft families: column section needs column + rft/rebar, ties ranked down", col.SequenceEqual(["Column RFT Section", "Column Rebar Ties"]), string.Join(", ", col));
        var ties = RankDetailFamilies(TiesRule, [("Transversal Rebar", false), ("Stirrup", true), ("Mesh Tie Tag", false)]);
        check("rft families: ties by tie/stirrup/transversal", ties.Count == 3 && ties[0] == "Transversal Rebar" && ties[2] == "Mesh Tie Tag", string.Join(", ", ties));
        check("rft families: nothing fits", RankDetailFamilies(MeshRule, [("Section Head", false)]).Count == 0, "");

        check("link: newer release detected", IsNewerRelease("2025", "2024") && IsNewerRelease("Autodesk Revit 2026", "2025"), "");
        check("link: same or older release fine", !IsNewerRelease("2025", "2025") && !IsNewerRelease("2023", "2025") && !IsNewerRelease("", "2025"), "");

        const double X = 0, Y = Math.PI / 2;
        var onCrossing = OffGridIssue([new("A", Y, 10), new("1", X, 20), new("B", Y, 6000)], 25);
        check("grid: within tolerance at a crossing", onCrossing is null, onCrossing ?? "");
        var offBoth = OffGridIssue([new("A", Y, 120), new("1", X, 60), new("B", Y, 6000)], 25);
        check("grid: offsets reported in mm", offBoth == "60 mm off grid 1 and 120 mm off grid A (tolerance 25 mm)", offBoth ?? "");
        var oneLine = OffGridIssue([new("A", Y, 5), new("1", X, 160)], 50);
        check("grid: on one line, off the other", oneLine == "on grid A but 160 mm off grid 1 (tolerance 50 mm)", oneLine ?? "");
        check("grid: larger tolerance accepts it", OffGridIssue([new("A", Y, 5), new("1", X, 160)], 200) is null, "");
        var parallel = OffGridIssue([new("A", Y, 30), new("B", Y, 7000)], 25);
        check("grid: only parallel grids", parallel == "not at a grid intersection; 30 mm off grid A (tolerance 25 mm)", parallel ?? "");

        check("exceptions: by id, mark or comment", IsException("columns_off_grid", 123, null, null, ["123"]) && IsException("columns_off_grid", 1, "c12", null, [" C12 "])
                                                     && IsException("columns_off_grid", 1, null, "facade offset", ["Facade offset"]), "");
        check("exceptions: scoped to a check", IsException("columns_off_grid", 1, "C12", null, ["columns_off_grid:C12"]) && !IsException("duplicates", 1, "C12", null, ["columns_off_grid:C12"]), "");
        check("exceptions: no match", !IsException("columns_off_grid", 5, "C1", null, ["C12", "", "50"]), "");
    }
}
