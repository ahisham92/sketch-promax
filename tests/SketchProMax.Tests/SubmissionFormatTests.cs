using SketchProMax.Tools;

/// <summary>
/// The built-in format of a berth's drawing submission (SubmissionFormat): what a berth's set holds, and what the check
/// of a model's sheets against it finds. Generic numbers only (PRJ1-FD-AAA-...). No Revit here.
/// </summary>
internal static class SubmissionFormatTests
{
    private const string P = "PRJ1-FD-AAA";

    private static SubmissionFormat.Sheet Sheet(string number, string name, string date, params (string, int)[] views) =>
        new() { Number = number, Name = name, IssueDate = date, Views = views.Select(v => (SubmissionFormat.PlacedView)v).ToList() };

    /// <summary>Berth 9 as issued: two parts, one design section, its pile details.</summary>
    private static List<SubmissionFormat.Sheet> Good()
    {
        const string d = "15 OCT 2026";
        var layout = Sheet($"{P}-09-SC-100", "OVERALL LAYOUT", d, ("BERTH 9 OVERALL PLAN", 300));
        layout.Schedules.Add("BERTH 9 - FURNITURE SCHEDULE");
        var piles1 = Sheet($"{P}-09-SC-101", "PILES PLAN - PART 1", d, ("BERTH 9 - PART 1 PILES PLAN", 100));
        piles1.Schedules.Add("BERTH 9 - PILES SCHEDULE");
        var piles2 = Sheet($"{P}-09-SC-102", "PILES PLAN - PART 2", d, ("BERTH 9 - PART 2 PILES PLAN", 100));
        piles2.Schedules.Add("Berth 9 - Piles Schedule");
        return
        [
            layout, piles1, piles2,
            Sheet($"{P}-09-SC-111", "FRAMING PLAN - PART 1", d, ("BERTH 9 - PART 1 FRAMING PLAN", 100), ("BERTH 9 - PART 1 ELEVATION", 100)),
            Sheet($"{P}-09-SC-112", "FRAMING PLAN - PART 2", d, ("BERTH 9 - PART 2 FRAMING PLAN", 100), ("BERTH 9 - PART 2 ELEVATION", 100)),
            Sheet($"{P}-09-SC-301", "DESIGN SECTION 1a CROSS SECTION", d, ("BERTH 9 - DESIGN SECTION 1a CROSS SECTION", 75)),
            Sheet($"{P}-09-SC-401", "PILES DETAILS - 1", d, ("BERTH 9 - PILE P1 - DESIGN SECTION 1a", 50), ("BERTH 9 - PILE P1 - DS01a - SECTION A", 25)),
            Sheet($"{P}-00-SC-203", "SLAB REINFORCEMENT DETAILS", d, ("TYPICAL SLAB DETAIL", 25)),
            Sheet("S-001", "WORKING SHEET", ""),
        ];
    }

    public static void Run(Action<string, bool, string> check)
    {
        var at = SubmissionFormat.Parse($"{P}-09-SC-101-0");
        check("submission format: a sheet number gives its berth and series, with or without the revision",
            at is { Berth: 9, Series: 101, Prefix: P, Discipline: "SC" } && SubmissionFormat.Parse($"{P}-00-SC-203") is { Berth: 0, Series: 203 }
            && SubmissionFormat.Parse("S-001") is null && SubmissionFormat.Bare($"{P}-09-sc-101-0") == $"{P}-09-SC-101", at + "");
        check("submission format: the kind of a sheet by its series and name",
            SubmissionFormat.KindOf(100, "OVERALL LAYOUT")?.Key == "layout" && SubmissionFormat.KindOf(103, "PILES PLAN - PART 3")?.Key == "piles"
            && SubmissionFormat.KindOf(112, "anything")?.Key == "framing" && SubmissionFormat.KindOf(302, "DESIGN SECTION 2 CROSS SECTION")?.Key == "section"
            && SubmissionFormat.KindOf(305, "LONGITUDINAL SLOPE SECTIONS 1")?.Key == "slope" && SubmissionFormat.KindOf(303, "CROSS SECTIONS - 1")?.Key == "cross" && SubmissionFormat.KindOf(305, "SOMETHING ELSE") is null
            && SubmissionFormat.KindOf(203, "SLAB REINFORCEMENT DETAILS") is null, "");

        var show = SubmissionFormat.Show(9);
        var text = string.Join("\n", show);
        check("submission format: show lists every kind of sheet with its views, scales and schedules, for the berth asked",
            SubmissionFormat.Kinds.All(k => text.Contains(k.Title)) && text.Contains("BERTH 9 - PART <k> PILES PLAN  usually 1:100") && text.Contains("schedule: BERTH 9 - PILES SCHEDULE")
            && text.Contains("<k> is the part's number") && text.Contains("nearest standard scale that fits") && text.Contains("(when needed)") && text.Contains("Every sheet:") && text.Contains("Files:") && !text.Contains("<n>")
            && string.Join("\n", SubmissionFormat.Show()).Contains("BERTH <n> OVERALL PLAN"), text);
        check("submission format: no project, firm or person is named in it",
            !System.Text.RegularExpressions.Regex.IsMatch(text, @"[A-Z]\d{5}|\bTIN\b|\bDAR\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "");

        var good = SubmissionFormat.Check(Good());
        check("submission format: a berth issued in the format has nothing open", good.Count == 0, string.Join(" | ", good.Select(i => i.Sheet + ": " + i.Problem)));

        // Sheets missing from the set.
        var missing = SubmissionFormat.Check(Good().Where(s => !s.Number.EndsWith("-112") && !s.Number.EndsWith("-401")).ToList());
        check("submission format: a part with a piles plan and no framing plan, and a set with no pile details, are listed",
            missing.Any(i => i.Sheet == "Berth 9" && i.Problem.Contains("Part 2 has a piles plan and no framing plan") && i.Fix.Contains("112"))
            && missing.Any(i => i.Sheet == "Berth 9" && i.Problem.Contains("PILES DETAILS")), string.Join(" | ", missing.Select(i => i.Problem)));
        var none = SubmissionFormat.Check(Good(), [9, 10]);
        check("submission format: a berth asked for that has no sheets is listed with the set to make",
            none.Count == 1 && none[0].Sheet == "Berth 10" && none[0].Fix.Contains("OVERALL LAYOUT") && none[0].Fix.Contains("PILES DETAILS"), string.Join(" | ", none.Select(i => i.Problem)));

        // What differs on a sheet.
        var bad = Good();
        bad[1].Name = "PILE LAYOUT PART 1";
        bad[1].Schedules.Clear();
        bad[2].Views[0] = ("BERTH 9 - PART 2 PILES PLAN", 200);
        bad[3].Views.RemoveAt(1);
        bad[4].Views[0] = ("BERTH 10 - PART 2 FRAMING PLAN", 100);
        bad[5].Views.Clear();
        bad[6].IssueDate = "16 OCT 2026";
        bad[6].Cited.Add($"{P}-09-SC-305");
        bad[6].Cited.Add($"{P}-09-SC-301-0");
        bad[7].TypedDimensions = 2;
        var found = SubmissionFormat.Check(bad);
        bool Has(string sheet, string checkName, string says) => found.Any(i => i.Sheet == $"{P}-09-SC-{sheet}" && i.Check == checkName && (i.Problem + i.Fix).Contains(says));
        check("submission format: a wrong sheet name and a missing schedule", Has("101", "Names", "PILES PLAN - PART <k>") && Has("101", "Schedules", "PILES SCHEDULE"), string.Join(" | ", found.Select(i => i.Sheet + " " + i.Check)));
        check("submission format: a part at another scale than the berth's other parts", Has("102", "Scale", "1:200") && Has("102", "Scale", "are at 1:100") && !Has("101", "Scale", ""), "");
        check("submission format: a framing sheet without its elevation; a view titled for another berth", Has("111", "Views", "PART <k> ELEVATION") && Has("112", "Names", "names another berth"), "");
        check("submission format: an empty sheet is one line, with the views to place", found.Count(i => i.Sheet == $"{P}-09-SC-301") == 1 && Has("301", "Views", "has no view"), "");
        check("submission format: a different issue date; a drawing cited that is no sheet (a cited sheet with its revision is fine)",
            Has("401", "Title block", "15 OCT 2026") && Has("401", "Cross-references", "SC-305") && !found.Any(i => i.Check == "Cross-references" && i.Problem.Contains("SC-301")), "");
        check("submission format: typed-over dimensions on a general sheet count too",
            found.Any(i => i.Sheet == $"{P}-00-SC-203" && i.Check == "Dimensions" && i.Problem.StartsWith("2 ")), "");
        check("submission format: sheets outside the format's numbering are left alone", !found.Any(i => i.Sheet.Contains("S-001")), "");

        // Scales are usual ones, not fixed: a long berth takes another standard scale, the same for all its parts.
        var longBerth = Good();
        longBerth[0].Views[0] = ("BERTH 9 OVERALL PLAN", 500);
        longBerth[1].Views[0] = ("BERTH 9 - PART 1 PILES PLAN", 150);
        longBerth[2].Views[0] = ("BERTH 9 - PART 2 PILES PLAN", 150);
        longBerth[5].Views[0] = ("BERTH 9 - DESIGN SECTION 1a CROSS SECTION", 100);
        longBerth[5].Views.Add(("BERTH 9 - TYPICAL CROSS SECTION AT FENDER AND BOLLARD LOCATION", 100));
        longBerth.Add(Sheet($"{P}-09-SC-302", "CROSS SECTIONS - 1", "15 OCT 2026", ("BERTH 9 - SECTION 1", 75), ("BERTH 9 - SECTION 2", 75)));
        var scaled = SubmissionFormat.Check(longBerth);
        check("submission format: another standard scale is right when all the berth's views of the kind share it; extra cross sections are part of the format",
            scaled.Count == 0, string.Join(" | ", scaled.Select(i => i.Sheet + ": " + i.Problem)));
        longBerth[5].Views[0] = ("BERTH 9 - DESIGN SECTION 1a CROSS SECTION", 80);
        var odd = SubmissionFormat.Check(longBerth);
        check("submission format: a scale that is not a standard one is listed, with the usual scale",
            odd.Count == 1 && odd[0].Check == "Scale" && odd[0].Problem.Contains("1:80") && odd[0].Fix.Contains("1:100"), string.Join(" | ", odd.Select(i => i.Problem)));

        // What is read in the views: tags, matchlines, fit on the sheet, key plan.
        var drawn = Good();
        drawn[1].Views[0].EmptyTags = 3;
        drawn[1].Views[0].OverlappingTags = 2;
        drawn[1].Views[0].MatchlinesPastCrop = 1;
        drawn[1].Views[0].Taggable = 120;
        drawn[1].Views[0].Untagged = 14;
        drawn[3].Views[1].OffSheet = true;
        drawn[1].KeyPlanFound = true;
        drawn[1].KeyPlanBerths = [9];
        drawn[2].KeyPlanFound = true;
        drawn[2].KeyPlanBerths = [9, 10];
        var seen = SubmissionFormat.Check(drawn);
        bool Seen(string sheet, string checkName, string says) => seen.Any(i => i.Sheet == $"{P}-09-SC-{sheet}" && i.Check == checkName && (i.Problem + i.Fix).Contains(says));
        check("submission format: empty tags, tags on top of each other and matchlines past the crop are listed with the tool that fixes them",
            Seen("101", "Tags", "3 empty tag") && Seen("101", "Tags", "arrange_tags") && Seen("101", "Matchlines", "trim_matchlines"), string.Join(" | ", seen.Select(i => i.Sheet + " " + i.Problem)));
        check("submission format: a view running off the sheet is listed", Seen("111", "Fit", "PART 1 ELEVATION") && Seen("111", "Fit", "next standard scale"), "");
        check("submission format: a key plan highlighting another berth too is listed; one highlighting only this berth is not",
            Seen("102", "Key plan", "9, 10") && !seen.Any(i => i.Check == "Key plan" && i.Sheet.EndsWith("-101")) && seen.Count == 5, seen.Count + "");
        var look = SubmissionFormat.Look(drawn);
        check("submission format: untagged elements and a key plan the model doesn't describe are to look at, not failures",
            look.Any(l => l.Contains("14 of 120") && l.Contains("SC-101")) && look.Any(l => l.StartsWith("Key plan:") && l.Contains("SC-100") && !l.Contains("SC-101"))
            && !look.Any(l => l.Contains("00-SC-203")) && !seen.Any(i => i.Problem.Contains("14 of")), string.Join(" | ", look));

        var section = Good();
        section.Add(Sheet($"{P}-09-SC-302", "DESIGN SECTION 2 CROSS SECTION", "15 OCT 2026", ("BERTH 9 - DESIGN SECTION 2 CROSS SECTION", 75)));
        var details = SubmissionFormat.Check(section);
        check("submission format: a design section drawn with no pile details is not complete",
            details.Count == 1 && details[0].Check == "Completeness" && details[0].Problem.Contains("Design section 2"), string.Join(" | ", details.Select(i => i.Problem)));

        check("submission format: the drawing numbers a note cites",
            SubmissionFormat.CitedIn($"FOR PILES DETAILS REFER TO DWG {P}-09-SC-401-0 AND {P}-09-SC-402. SEE NOTE 3.").SequenceEqual([$"{P}-09-SC-401", $"{P}-09-SC-402"])
            && SubmissionFormat.CitedIn("NO NUMBER HERE, LEVEL +2.50").Count == 0, "");
        var matrix = SubmissionFormat.Matrix(found);
        check("submission format: the QC matrix names, per sheet, the checks with something open",
            matrix[$"{P}-09-SC-101"].SequenceEqual(["Names", "Schedules"]) && !matrix.ContainsKey($"{P}-09-SC-100"), "");
    }

    /// <summary>The tool is there, reads only, and the chat and the job check are told to use it.</summary>
    public static void RunSources(string root, Action<string, bool, string> check)
    {
        string Read(string path) => File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n");
        var tool = Read("src/SketchProMax/Tools/SubmissionFormatTool.cs");
        check("submission format: the tool reads only and has show and check",
            tool.Contains("ModifiesModel => false") && tool.Contains("case \"show\"") && tool.Contains("case \"check\"") && !tool.Contains("new Transaction"), "");
        var prompt = Read("src/SketchProMax/Agent/SystemPrompt.cs");
        check("submission format: the chat plans a berth's sheets from it and checks against it before saying done",
            prompt.Contains("submission_format (action=show, berth)") && prompt.Contains("submission_format (action=check, berths)") && prompt.Contains("Done is when it lists nothing"), "");
        check("submission format: a job's check runs it too",
            Read("src/SketchProMax/Chat/JobChecks.cs").Contains("submission_format (action=check, berths)"), "");
        var math = Read("src/SketchProMax/Tools/SubmissionFormatMath.cs");
        check("submission format: the code names no project, firm or person",
            !System.Text.RegularExpressions.Regex.IsMatch(math + tool, @"N\d{5}|-TIN-|\bDar\b"), "");
    }
}
