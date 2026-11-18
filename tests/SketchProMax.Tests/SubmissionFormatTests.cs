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
        bad[6].IssueDate = "16/10/26";
        bad[6].Cited.Add($"{P}-09-SC-305");
        bad[6].Cited.Add($"{P}-09-SC-301-0");
        bad[7].TypedDimensions = 2;
        var found = SubmissionFormat.Check(bad);
        bool Has(string sheet, string checkName, string says) => found.Any(i => i.Sheet == $"{P}-09-SC-{sheet}" && i.Check == checkName && (i.Problem + i.Fix).Contains(says));
        check("submission format: a wrong sheet name and a missing schedule", Has("101", "Names", "PILES PLAN - PART <k>") && Has("101", "Schedules", "PILES SCHEDULE"), string.Join(" | ", found.Select(i => i.Sheet + " " + i.Check)));
        check("submission format: a part at another scale than the berth's other parts", Has("102", "Scale", "1:200") && Has("102", "Scale", "are at 1:100") && !Has("101", "Scale", ""), "");
        check("submission format: a framing sheet without its elevation; a view titled for another berth", Has("111", "Views", "PART <k> ELEVATION") && Has("112", "Names", "names another berth"), "");
        check("submission format: an empty sheet is one line, with the views to place", found.Count(i => i.Sheet == $"{P}-09-SC-301") == 1 && Has("301", "Views", "has no view"), "");
        check("submission format: an issue date written another way; a drawing cited that is no sheet (a cited sheet with its revision is fine)",
            Has("401", "Title block", "15 OCT 2026") && Has("401", "Cross-references", "SC-305") && !found.Any(i => i.Check == "Cross-references" && i.Problem.Contains("SC-301")), "");
        var later = Good();
        later[6].IssueDate = "30 NOV 2026";
        check("submission format: a later date on a sheet added in a later revision is listed to look at, not a failure",
            !SubmissionFormat.Check(later).Any(i => i.Check == "Title block") && SubmissionFormat.Look(later).Any(l => l.StartsWith("Issue dates:") && l.Contains("30 NOV 2026 on 1 sheet")),
            string.Join(" | ", SubmissionFormat.Look(later)));
        var box = SubmissionFormat.WithoutLeader(0, 0, 10, 4, 8, 3);
        check("submission format: a tag's leader is not part of the tag when looking for tags on top of each other",
            box == (6, 2, 10, 4) && SubmissionFormat.WithoutLeader(0, 0, 10, 4, 12, 3) == (0, 0, 10, 4), box.ToString());
        // A 10 x 2 tag in a plan turned 5 degrees: its box along the model's axes is bigger, and is worked back.
        double c5 = Math.Cos(5 * Math.PI / 180), s5 = Math.Sin(5 * Math.PI / 180);
        var back = SubmissionFormat.Unturned(10 * c5 + 2 * s5, 10 * s5 + 2 * c5, c5, s5);
        var quarter = SubmissionFormat.Unturned(2, 10, 0, 1);
        check("submission format: a tag's size in a turned plan is worked back from its box (not turned, a quarter turn and 45 degrees too)",
            Math.Abs(back.W - 10) < 1e-9 && Math.Abs(back.H - 2) < 1e-9 && SubmissionFormat.Unturned(10, 2, 1, 0) == (10, 2)
            && Math.Abs(quarter.W - 10) < 1e-9 && Math.Abs(quarter.H - 2) < 1e-9 && SubmissionFormat.Unturned(8, 8, Math.Sqrt(0.5), Math.Sqrt(0.5)) == (8, 8), $"{back} {quarter}");
        var allOn = Good();
        allOn[1].KeyPlanFound = allOn[1].KeyPlanSwitches = true;
        for (var b = 1; b <= 10; b++) allOn[1].KeyPlanOn.Add((b, 0));
        check("submission format: a key plan with every berth on is one short line",
            SubmissionFormat.Check(allOn).Single(i => i.Check == "Key plan").Problem is { Length: < 90 } all && all.Contains("10 berths and parts"), SubmissionFormat.Check(allOn).FirstOrDefault(i => i.Check == "Key plan")?.Problem ?? "");
        var mixed = Good();
        mixed[6].IssueDate = "16/10/26";
        check("submission format: a date written another way is a failure only, not listed as a later revision",
            SubmissionFormat.Check(mixed).Any(i => i.Check == "Title block") && !SubmissionFormat.Look(mixed).Any(l => l.StartsWith("Issue dates:")), string.Join(" | ", SubmissionFormat.Look(mixed)));
        var slow = Good();
        slow[1].Views[0].NotJudged = true;
        check("submission format: a view whose tags were not read yet is said first, to run the check again",
            SubmissionFormat.Look(slow).FirstOrDefault() is { } first && first.StartsWith("Not read yet:") && first.Contains("1 sheet") && !SubmissionFormat.Look(Good()).Any(l => l.StartsWith("Not read yet")), "");
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
        drawn[1].Views[0].UntaggedInside = 14;
        drawn[1].Views[0].TaggedOutside = 6;
        drawn[3].Views[0].Taggable = 40;
        drawn[3].Views[0].UntaggedInside = 5;
        drawn[3].Views[1].OffSheet = true;
        drawn[1].KeyPlanFound = true;
        drawn[1].KeyPlanOn = [(9, 1)];
        drawn[1].KeyPlanParts = true;
        drawn[2].KeyPlanFound = true;
        drawn[2].KeyPlanOn = [(9, 0), (10, 0)];
        var seen = SubmissionFormat.Check(drawn);
        bool Seen(string sheet, string checkName, string says) => seen.Any(i => i.Sheet == $"{P}-09-SC-{sheet}" && i.Check == checkName && (i.Problem + i.Fix).Contains(says));
        check("submission format: empty tags, tags on top of each other and matchlines past the crop are listed with the tool that fixes them",
            Seen("101", "Tags", "3 empty tag") && Seen("101", "Tags", "arrange_tags") && Seen("101", "Matchlines", "trim_matchlines"), string.Join(" | ", seen.Select(i => i.Sheet + " " + i.Problem)));
        check("submission format: a view running off the sheet is listed", Seen("111", "Fit", "PART 1 ELEVATION") && Seen("111", "Fit", "next standard scale"), "");
        drawn[3].Views[1].OffSheetAroundOnly = true;
        var around = SubmissionFormat.Check(drawn).Where(i => i.Check == "Fit").ToList();
        check("submission format: when only a level head or a note around a view runs past the paper, the line says the view itself is inside",
            around.Count == 1 && around[0].Problem.Contains("PART 1 ELEVATION") && around[0].Problem.Contains("the view itself is inside") && !around[0].Fix.Contains("standard scale"), string.Join(" | ", around.Select(i => i.Problem)));
        drawn[3].Views[1].OffSheetAroundOnly = false;
        check("submission format: a key plan highlighting another berth too is listed; one highlighting only this berth is not",
            Seen("102", "Key plan", "Berth 9, Berth 10") && Seen("102", "Key plan", "only Berth 9 - Part 2") && !seen.Any(i => i.Check == "Key plan" && i.Sheet.EndsWith("-101")) && seen.Count == 7, seen.Count + "");
        check("submission format: piles inside the matchlines with no tag, and tags on piles beyond the matchline, are failures on a piles plan",
            Seen("101", "Tags", "14 of 120 piles inside the matchlines have no tag") && Seen("101", "Tags", "6 tag(s) are on piles beyond the matchline") && Seen("101", "Tags", "Hide those tags")
            && !seen.Any(i => i.Sheet.EndsWith("-111") && i.Check == "Tags"), "");
        check("submission format: a title block's berth switch is read by its name, whatever the wording; other switches name no berth",
            SubmissionFormat.SwitchBerths("Berth 9").SequenceEqual([9]) && SubmissionFormat.SwitchBerths("BERTH 09").SequenceEqual([9]) && SubmissionFormat.SwitchBerths("B10").SequenceEqual([10])
            && SubmissionFormat.SwitchBerths("B-05").SequenceEqual([5]) && SubmissionFormat.SwitchBerths("KP 7").SequenceEqual([7]) && SubmissionFormat.SwitchBerths("Key Plan - Berth 5&6").SequenceEqual([5, 6])
            && SubmissionFormat.SwitchBerths("12").SequenceEqual([12]) && SubmissionFormat.SwitchBerths("Show Logo").Count == 0 && SubmissionFormat.SwitchBerths("A1").Count == 0
            && SubmissionFormat.SwitchBerths("Stamp 2026").Count == 0, string.Join(",", SubmissionFormat.SwitchBerths("Key Plan - Berth 5&6")));
        var off = Good();
        off[0].KeyPlanFound = off[0].KeyPlanSwitches = true;
        off[1].OtherSwitches = ["Show Logo", "North Arrow"];
        var allOff = SubmissionFormat.Check(off);
        check("submission format: every berth switch off is a key plan with no berth; switches that name no berth are named for the user",
            allOff.Count == 1 && allOff[0].Check == "Key plan" && allOff[0].Fix.Contains("Switch on Berth 9")
            && SubmissionFormat.Look(off).Any(l => l.Contains("North Arrow, Show Logo") && !l.Contains("SC-100,")), string.Join(" | ", SubmissionFormat.Look(off)));
        var parts = Good();
        foreach (var sh in parts) { sh.KeyPlanFound = sh.KeyPlanSwitches = sh.KeyPlanParts = true; sh.KeyPlanOn = [(9, 0)]; }
        parts[1].KeyPlanOn = [(9, 1)];
        parts[2].KeyPlanOn = [(9, 1)];
        parts[3].KeyPlanOn = [(9, 1)];
        parts[5].KeyPlanOn = [(9, 2)];
        var keyed = SubmissionFormat.Check(parts);
        check("submission format: a part's sheet highlights its own part in the key plan, other sheets the whole berth",
            keyed.Count == 3 && keyed.All(i => i.Check == "Key plan")
            && keyed.Any(i => i.Sheet.EndsWith("-102") && i.Problem.Contains("Berth 9 - Part 1") && i.Fix.Contains("only Berth 9 - Part 2"))
            && keyed.Any(i => i.Sheet.EndsWith("-112") && i.Problem.Contains("highlights Berth 9.") && i.Fix.Contains("Berth 9 - Part 2"))
            && keyed.Any(i => i.Sheet.EndsWith("-301") && i.Fix.Contains("only Berth 9.")), string.Join(" | ", keyed.Select(i => i.Sheet + ": " + i.Problem + " " + i.Fix)));
        check("submission format: the part a switch is for", SubmissionFormat.SwitchPart("Berth 6 - Part 2") == 2 && SubmissionFormat.SwitchPart("Berth 6") == 0
            && SubmissionFormat.SwitchBerths("Berth 10 - Part 4").SequenceEqual([10]), "");
        var look = SubmissionFormat.Look(drawn);
        check("submission format: untagged elements on other sheets and a key plan the model doesn't describe are to look at, not failures",
            look.Any(l => l.Contains("5 of 40") && l.Contains("SC-111")) && !look.Any(l => l.Contains("14 of 120")) && look.Any(l => l.StartsWith("Key plan:") && l.Contains("SC-100") && !l.Contains("SC-101"))
            && !look.Any(l => l.Contains("00-SC-203")) && !seen.Any(i => i.Problem.Contains("5 of 40")), string.Join(" | ", look));

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
