using System.Text.Json;
using SketchProMax.Tools;
using static SketchProMax.Tools.DrawingPackage;

/// <summary>The drawing package from one sentence: the request reader, the structured inputs and the plan's checks against a model.</summary>
internal static class PackageTests
{
    public static void Run(Action<string, bool, string> check)
    {
        static IReadOnlyDictionary<string, JsonElement> J(string s) =>
            JsonDocument.Parse(s).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        static string? Throws(Action a) { try { a(); return null; } catch (ArgumentException ex) { return ex.Message; } }

        // ---- Reading the sentence
        var s = Parse("GA plans, sections and column schedule for levels 1–5 on A1 with our title block");
        check("package: the field-test sentence is read", s.Views.SequenceEqual(["floor_plan", "section"]) && s.Sections == 2 && s.Schedules.SequenceEqual(["Structural Columns"])
            && s.Levels.SequenceEqual(["1-5"]) && s.SheetSize == "A1" && s.TitleBlock == "our" && s.Scale == 100, string.Join(" | ", s.Read));
        check("package: what was assumed is said", s.Read.Any(r => r.Contains("assumed 1:100")) && s.Read.Any(r => r.Contains("your title block")), string.Join(" | ", s.Read));
        var st = Parse("Structural GA plans for L1 to L3 at 1:200 on A0, numbered S-201, tag columns and beams, no dimensions, transmittal");
        check("package: structural plans, scale, size, numbering, tags, no dims, transmittal",
            st.Views.SequenceEqual(["structural_plan"]) && st.Package == "structural_ga" && st.Levels.SequenceEqual(["1-3"]) && st.Scale == 200 && st.SheetSize == "A0"
            && st.SheetPrefix == "S-" && st.FirstNumber == 201 && st.Tags!.SequenceEqual(["Structural Columns", "Structural Framing"]) && !st.Dimensions && st.IssueSheet == "transmittal",
            $"{string.Join(",", st.Views)} {string.Join(",", st.Levels)} {st.Scale} {st.SheetSize} {st.SheetPrefix}{st.FirstNumber} {string.Join(",", st.Tags ?? [])} {st.Dimensions} {st.IssueSheet}");
        var three = Parse("plans, three sections and elevations for levels 1, 3 and 5, without tags, title block called Office A1");
        check("package: counted sections, elevations, a level list, no tags, a named title block",
            three.Sections == 3 && three.Views.Contains("elevation") && three.Levels.SequenceEqual(["1", "3", "5"]) && three.Tags is { Count: 0 } && three.TitleBlock == "Office A1",
            $"{three.Sections} {string.Join(",", three.Levels)} {three.TitleBlock}");
        var rcp = Parse("reflected ceiling plans for all levels");
        check("package: ceiling plans only, all levels", rcp.Views.SequenceEqual(["ceiling_plan"]) && rcp.Package == "rcp" && rcp.Levels.SequenceEqual(["all"]), string.Join(",", rcp.Views));
        var ground = Parse("GA plans ground to level 4 and a door schedule");
        check("package: ground to level n, a door schedule", ground.Levels.SequenceEqual(["0-4"]) && ground.Schedules.SequenceEqual(["Doors"]), string.Join(",", ground.Levels));
        var bare = Parse("make the drawings");
        check("package: nothing named means GA floor plans of all levels", bare.Views.SequenceEqual(["floor_plan"]) && bare.Levels.Count == 0 && bare.Read.Any(r => r.Contains("assumed GA floor plans")), "");
        check("package: a sheet number is not a sheet size", Parse("plans numbered A-101").SheetSize == "A1" && Parse("plans on a3, numbered A-101").SheetSize == "A3", "");

        // ---- Structured inputs win over the sentence
        var a = FromArgs(J("""{"request": "GA plans for levels 1-5 on A1", "levels": ["2"], "scale": 50, "sheet_size": "a2", "tags": ["doors"], "dimensions": false, "title_block_fields": {"Drawn By": "AM"}}"""));
        check("package: fields override the sentence", a.Levels.SequenceEqual(["2"]) && a.Scale == 50 && a.SheetSize == "A2" && a.Tags!.SequenceEqual(["Doors"]) && !a.Dimensions && a.TitleBlockFields["Drawn By"] == "AM", "");
        var b = FromArgs(J("""{"views": ["structural_plan", "section"], "schedules": ["Columns", "Structural Framing"]}"""));
        check("package: structured only, sections default to two, schedule words mapped", b.Sections == 2 && b.Schedules.SequenceEqual(["Structural Columns", "Structural Framing"]), $"{b.Sections} {string.Join(",", b.Schedules)}");
        check("package: sections given adds the view type", FromArgs(J("""{"sections": 1}""")).Views.Contains("section"), "");
        check("package: no views at all means the package's plan kind", FromArgs(J("""{"package": "structural_ga"}""")).Views.SequenceEqual(["structural_plan"]), "");
        check("package: a bad number is refused with the field", Throws(() => FromArgs(J("""{"scale": "big"}""")))?.Contains("scale") == true, "");

        // ---- The model
        var facts = new Facts
        {
            Levels = [new("Level 1", 0), new("Level 2", 4000), new("Level 3", 8000), new("Level 4", 12000), new("Level 5", 16000), new("Roof", 20000)],
            TitleBlocks = [new("A0 metric: A0", 1189, 841, 0), new("Office A1: Standard", 841, 594, 12), new("A1 metric: A1", 841, 594, 2)],
            Grids = 6,
        };
        facts.SheetNumbers.UnionWith(["A-101", "A-102"]);
        facts.ViewNames.UnionWith(["Level 1 - GA Plan", "Section A-A"]);

        // Levels
        var errs = new List<string>();
        var warn = new List<string>();
        var lv = ResolveLevels(["1-5"], facts.Levels, errs, warn);
        check("package: levels 1-5 by number", lv.Select(l => l.Name).SequenceEqual(["Level 1", "Level 2", "Level 3", "Level 4", "Level 5"]) && errs.Count == 0, string.Join(",", errs));
        check("package: all levels, lowest first", ResolveLevels(["all"], facts.Levels, errs, warn).Count == 6 && ResolveLevels([], facts.Levels, errs, warn)[0].Name == "Level 1", "");
        check("package: a level by name and by part of a name", ResolveLevels(["Roof", "level 2"], facts.Levels, errs, warn).Select(l => l.Name).SequenceEqual(["Level 2", "Roof"]), "");
        var missing = new List<string>();
        ResolveLevels(["9"], facts.Levels, missing, warn);
        check("package: an unknown level is an error naming the levels there", missing.Count == 1 && missing[0].Contains("Level 1, Level 2"), string.Join(",", missing));
        var gaps = new List<string>();
        ResolveLevels(["4-7"], facts.Levels, missing, gaps);
        check("package: a range with gaps warns", gaps.Count == 1 && gaps[0].Contains("only 2 of 4"), string.Join(",", gaps));
        check("package: level numbers from names", LevelNumber("Level 03") == 3 && LevelNumber("L12") == 12 && LevelNumber("B2") == -2 && LevelNumber("Ground Floor") == 0
                                                    && LevelNumber("Roof") is null && LevelNumber("02 - Second") == 2, "");

        // Title blocks
        var tw = new List<string>();
        check("package: our A1 title block is the one the sheets use most", PickTitleBlock("our", "A1", facts.TitleBlocks, tw, out _) == "Office A1: Standard" && tw.Count == 0, "");
        check("package: a named title block", PickTitleBlock("A1 metric", "A1", facts.TitleBlocks, tw, out _) == "A1 metric: A1", "");
        var none = PickTitleBlock("our", "A1", [], tw, out var why);
        check("package: no title block loaded is a step for the user", none is null && why!.Contains("Load your A1 title block"), why ?? "");
        PickTitleBlock("Missing one", "A1", facts.TitleBlocks, tw, out var why2);
        check("package: a title block that isn't loaded is named", why2?.Contains("No title block called 'Missing one'") == true, why2 ?? "");
        var w2 = new List<string>();
        check("package: no title block of the size uses the usual one and says so", PickTitleBlock("our", "A3", facts.TitleBlocks, w2, out _) == "Office A1: Standard" && w2.Count == 1 && w2[0].Contains("No A3"), string.Join(",", w2));
        check("package: size from the name when the type has no dimensions", FitsSize(new("Firm A2 border", 0, 0, 0), "A2") && !FitsSize(new("Firm A20 border", 0, 0, 0), "A2"), "");

        // The whole plan
        var plan = Resolve(Parse("GA plans, sections and column schedule for levels 1-5 on A1 with our title block"), facts);
        check("package: the plan is ok", plan.Ok, string.Join(" ", plan.Errors));
        check("package: views counted", plan.Views.Count == 8 && plan.Count("floor_plan") == 5 && plan.Count("section") == 2 && plan.Count("schedule") == 1, plan.Views.Count.ToString());
        check("package: taken view names stepped over", plan.Views[0].Name == "Level 1 - GA Plan (2)" && plan.Views.Single(v => v.Key == "section:A").Name == "Section A-A (2)", plan.Views[0].Name);
        var numbers = plan.Sheets.Select(x => x.Number).ToList();
        check("package: register first, taken sheet numbers stepped over", numbers.SequenceEqual(["A-100", "A-103", "A-104", "A-105", "A-106", "A-107", "A-108", "A-109"]) && plan.Sheets[0].Register
                                                                         && plan.Warnings.Any(x => x.Contains("taken already")), string.Join(",", numbers));
        check("package: one plan per sheet, two sections on a sheet, schedules together", plan.Sheets[1].Views.Count == 1 && plan.Sheets[6].Views.Count == 2 && plan.Sheets[7].Name == "Column Schedule", plan.Sheets[7].Name);
        check("package: tags and dimensions per plan", plan.Tags.Count == 15 && plan.DimensionViews.Count == 5 && plan.TitleBlock == "Office A1: Standard", plan.Tags.Count.ToString());
        check("package: stages counted in order", plan.StageItems().Select(x => x.Items).SequenceEqual([8, 5, 15, 7, 0, 1]), string.Join(",", plan.StageItems().Select(x => x.Items)));
        check("package: a by-hand estimate", PlannedByHandMinutes(plan, null) > 60, PlannedByHandMinutes(plan, null).ToString());

        Plan R(string json) => Resolve(FromArgs(J(json)), facts);
        check("package: an unknown view type is refused", !R("""{"views": ["axonometric"]}""").Ok, "");
        check("package: a silly scale is refused", !R("""{"scale": 0}""").Ok, "");
        check("package: an unknown sheet size is refused", !R("""{"sheet_size": "B1"}""").Ok, "");
        check("package: a prefix Revit refuses is refused", R("""{"sheet_prefix": "A:"}""").Errors.Any(e => e.Contains("refuses")), "");
        check("package: an unknown view template is refused", R("""{"view_template": "Nope"}""").Errors.Any(e => e.Contains("No view template")), "");
        check("package: no grids, no dimensions (said)", Resolve(FromArgs(J("""{"levels": ["1"]}""")), new Facts { Levels = facts.Levels, TitleBlocks = facts.TitleBlocks }) is { DimensionViews.Count: 0 } p0 && p0.Warnings.Any(x => x.Contains("grids")), "");
        var noTb = Resolve(FromArgs(J("""{"levels": ["1"]}""")), new Facts { Levels = facts.Levels, Grids = 2 });
        check("package: no title block is a warning and a hand-off, not an error", noTb.Ok && noTb.TitleBlock is null && noTb.TitleBlockMissing is not null, string.Join(" ", noTb.Errors));
        var structural = R("""{"package": "structural_ga", "levels": ["1-2"], "issue_sheet": "none"}""");
        check("package: structural defaults: S- numbers, column and framing tags, no register", structural.Sheets.All(x => x.Number.StartsWith("S-")) && structural.Register is null
            && structural.Tags.Select(t => t.Category).Distinct().SequenceEqual(["Structural Columns", "Structural Framing"]), string.Join(",", structural.Sheets.Select(x => x.Number)));
        var many = R("""{"sections": 5, "views": ["section", "elevation"], "levels": []}""");
        check("package: five sections on three sheets, four elevations on two", many.Sheets.Count(x => x.Name.StartsWith("Sections")) == 3 && many.Sheets.Count(x => x.Name.StartsWith("Elevations")) == 2, string.Join(",", many.Sheets.Select(x => x.Name)));
        check("package: unique names", Unique("X", new HashSet<string>(["X", "X (2)"])) == "X (3)", "");

        // ---- Sections, overlaps, time
        var lines = SectionLines(0, 0, 30, 10, 2, 1);
        check("package: first section runs the long way, the second across", lines.Count == 2 && lines[0] == (-1, 5, 31, 5, 6) && lines[1] == (15, 11, 15, -1, 16), string.Join(" ", lines));
        var four = SectionLines(0, 0, 10, 30, 4, 0);
        check("package: more sections at the thirds, alternating", four.Count == 4 && four[0].X0 == four[0].X1 && Math.Abs(four[0].X0 - 10.0 / 3) < 1e-9 && four[1].Y0 == four[1].Y1, string.Join(" ", four));
        var boxes = new List<(string, LayoutMath.Rect)> { ("a", new(0, 0, 2, 2)), ("b", new(2, 0, 4, 2)), ("c", new(1, 1, 3, 3)) };
        var ov = Overlaps(boxes);
        check("package: overlapping viewports found, touching ones not", ov.Count == 2 && ov.Contains(("a", "c")) && ov.Contains(("b", "c")), string.Join(";", ov));
        check("package: a view parked beside the sheet is off it", OffSheet(new(10, 0, 12, 2), new(0, 0, 8, 6)) && !OffSheet(new(1, 1, 2, 2), new(0, 0, 8, 6)), "");
        check("package: by hand per stage uses the tool's rate", ByHandMinutes("create_sheets", 5, null) == 2 + 2 * 5 && ByHandMinutes("create_section", 3, null) == 9 && ByHandMinutes("create_sheets", 0, null) == 0, "");
        check("package: the tools have time rates", TimeMath.Defaults.ContainsKey("drawing_package") && TimeMath.Defaults.ContainsKey("get_drawing_package_plan"), "");
    }
}
