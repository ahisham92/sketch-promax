using System.Globalization;
using SketchProMax.Tools;
using static SketchProMax.Tools.BerthStatusMath;

/// <summary>
/// The berth status tracker (Tools/BerthStatusMath): the working calendar and the 08:00 due time, the setup Excel both
/// ways, scores with hand entries and deliverables, the forecast and the verdict, the berth's Excel, and saving. Runs in
/// a temporary folder; no project names (generic ones only).
/// </summary>
internal static class BerthStatusTests
{
    /// <summary>The window rebuilds itself (after Import): every control it keeps in a field must leave its old parent first.</summary>
    public static void RunSource(string repoRoot, Action<string, bool, string> check)
    {
        var source = File.ReadAllText(Path.Combine(repoRoot, "src/SketchProMax/UI/BerthStatusWindow.cs"));
        var kept = System.Text.RegularExpressions.Regex.Matches(source, @"private readonly (?:TextBlock|StackPanel|ScrollViewer|Border|ProgressBar|Button|ScoreRing) (_\w+)")
            .Select(m => m.Groups[1].Value).ToList();
        var reuse = System.Text.RegularExpressions.Regex.Match(source, @"foreach \(var reused in new FrameworkElement\[\] \{([^}]*)\}").Groups[1].Value;
        var listed = reuse.Split(',').Select(x => x.Trim()).ToHashSet();
        var missing = kept.Where(k => !listed.Contains(k)).ToList();
        check("berth status: the window detaches every kept control before rebuilding", kept.Count >= 8 && missing.Count == 0, "missing: " + string.Join(", ", missing));
    }

    public static void Run(Action<string, bool, string> check)
    {
        // A package of several berths: its cells list them, and "9" matches a value written "BERTH 9" (and back).
        check("berth status: a cell lists several values", Values(" 9; 10 ,BERTH 11;9").SequenceEqual(["9", "10", "BERTH 11"]), string.Join("|", Values(" 9; 10 ,BERTH 11;9")));
        check("berth status: a parameter value matches any of the berth's", ValueMatches("10", ["9", "10"]) && ValueMatches("9", ["BERTH 9"]) && ValueMatches("Berth 9", ["9"])
            && !ValueMatches("1", ["10"]) && !ValueMatches("", ["9"]) && !ValueMatches("19", ["BERTH 9"]), "");

        // A package of several berths: each berth read and shown on its own, the package puts them together.
        var package = new Berth("Port A", "P1 Berths 5, 6", FindBy.Parameter, "5; 6", "Berth Number", "-A-05-; -A-06-");
        var kids = ChildrenOf(package);
        check("berth status: a package of two berths has two berths", kids.Count == 2 && kids[0].Name == "Berth 5" && kids[1].Value == "6" && kids[1].Sheets == "-A-06-"
            && kids[0].Parent == package.Name && kids[0].ScheduleTexts.SequenceEqual(["Berth 5"]) && kids[0].Key != package.Key, string.Join("|", kids.Select(k => k.Key + "/" + k.Sheets)));
        check("berth status: a package of one, a whole model and a workset have no berths under them",
            ChildrenOf(package with { Value = "5" }).Count == 0 && ChildrenOf(package with { FindBy = FindBy.WholeModel }).Count == 0 && ChildrenOf(package with { FindBy = FindBy.Workset }).Count == 0, "");
        check("berth status: a package of one berth finds its schedules by its number", (package with { Value = "4" }).ScheduleTexts.SequenceEqual(["Berth 4"])
            && (package with { FindBy = FindBy.WholeModel, Value = "" }).ScheduleTexts.SequenceEqual([package.Name]), "");
        check("berth status: what is read is each berth of a package", Units([package, package with { Name = "P2", Value = "7" }]).Select(u => u.Name).SequenceEqual(["Berth 5", "Berth 6", "P2"]), "");
        var pSetup = new Setup { Berths = [package], Submissions = [new("Port A", package.Name, "P1", new DateTime(2026, 11, 5))] };
        check("berth status: a berth has its package's submission", SubmissionsFor(pSetup, kids[0]).Count == 1 && PackageOf(pSetup, kids[1]) == package, "");
        check("berth status: a berth of a package is found by its name or the package's code", FindBerth(pSetup, "Berth 6")?.Key == kids[1].Key && FindBerth(pSetup, "P1 Berth 5")?.Key == kids[0].Key, "");
        var pState = new State();
        pState.For(kids[0].Key).Results[Drawings] = new AreaResult { Percent = 80, Done = 4, Total = 5, Findings = [new Finding(Drawings, "Sheet 1", "No views")] };
        pState.For(kids[1].Key).Results[Drawings] = new AreaResult { Percent = 40, Done = 2, Total = 5 };
        pState.For(package.Key).Comment = "package note";
        var merged = Merged(package, pState);
        check("berth status: a package's area is the average of its berths", merged.Results[Drawings].Percent == 60 && merged.Results[Drawings].Total == 10
            && merged.Results[Drawings].Findings.Single().Item == "Berth 5 · Sheet 1" && merged.Comment == "package note", merged.Results[Drawings].Summary);
        RecordPackages(pSetup, pState, new DateTime(2026, 10, 23, 12, 0, 0));
        check("berth status: the package gets a history point from its berths", pState.For(package.Key).History.Count == 1, "");

        // The overview: a ring per package coloured red, yellow, green; its short status; engineers share the daily need.
        check("berth status: rings run red at 0, yellow at 50, green at 90 and over, grey when not measured",
            RingColor(0) is { R: > 180, G: < 40 } && RingColor(50) is { R: > 180, G: > 140, B: < 40 } && RingColor(90) == RingColor(100) && RingColor(95) is { G: > 150, R: < 80 }
            && RingColor(null) == (0x9C, 0xA3, 0xAF) && RingColor(-5) == RingColor(0), RingColor(50).ToString());
        pState.For(package.Key).Engineers = 3;
        check("berth status: the engineers belong to the package and share its daily need", Merged(package, pState).Engineers == 3 && PerEngineer(9, 3) == 3 && PerEngineer(9, null) == 9 && PerEngineer(null, 2) is null, "");
        var due = new DateTime(2026, 11, 5, 8, 0, 0);
        var dueSub = new Submission("Port A", package.Name, "P1", due);
        Summary Sum(double? p, Submission? next, Submission? last, Verdict v) => new(package, p, next, next is null ? null : due, 0, null, false, v, [], [], new BerthState(), last);
        check("berth status: a package is Submitted, Overdue, In progress or Not started",
            StageOf(Sum(70, null, dueSub, Verdict.Submitted)) == Stage.Submitted && StageOf(Sum(70, dueSub, null, Verdict.Overdue)) == Stage.Overdue
            && StageOf(Sum(40, dueSub, null, Verdict.OnTrack)) == Stage.InProgress && StageOf(Sum(null, dueSub, null, Verdict.NotKnown)) == Stage.NotStarted
            && StageOf(Sum(0, dueSub, null, Verdict.NotKnown)) == Stage.NotStarted && Text(Stage.InProgress) == "In progress", "");

        // Deliverables are set per berth: the package shows their average (a berth not set counts 0).
        var dState = new State();
        dState.For(kids[0].Key).DeliveredFor("Calculations").Percent = 100;
        dState.For(package.Key).DeliveredFor("Costing").Percent = 40;
        var dSetup = new Setup { Berths = [package], Deliverables = [new("Calculations"), new("Costing")] };
        var dPackage = Summarize(dSetup, package, dState, new DateTime(2026, 10, 23, 12, 0, 0), (_, _) => "");
        var dBerth = Summarize(dSetup, kids[1], dState, new DateTime(2026, 10, 23, 12, 0, 0), (_, _) => "");
        check("berth status: deliverables are per berth and the package averages them",
            dPackage.Deliverables.First(d => d.Deliverable.Name == "Calculations").Percent == 50 && dPackage.Deliverables.First(d => d.Deliverable.Name == "Costing").Percent == 40
            && dBerth.Deliverables.First(d => d.Deliverable.Name == "Calculations").Percent is null, string.Join(", ", dPackage.Deliverables.Select(d => d.Deliverable.Name + " " + d.Percent)));

        // Modelling against the submitted berths: what they have is expected here at their average count.
        var refA = new Dictionary<string, int> { ["Structural Columns: Concrete Round"] = 400, ["Specialty Equipment: Fender"] = 10, ["Floors: Floor"] = 30 };
        var refB = new Dictionary<string, int> { ["Structural Columns: Concrete Round"] = 420, ["Specialty Equipment: Fender"] = 12, ["Floors: Floor"] = 34 };
        var full = CompareModelling(new Dictionary<string, int>(refA, StringComparer.OrdinalIgnoreCase) { ["Structural Columns: Concrete Round"] = 500 }, [refA, refB]);
        var half = CompareModelling(new Dictionary<string, int> { ["Structural Columns: Concrete Round"] = 200, ["Floors: Floor"] = 32 }, [refA, refB]);
        check("berth status: a berth with what the submitted berths have is fully modelled", full.Percent >= 99 && full.Short.Count == 0, full.Percent?.ToString() ?? "");
        check("berth status: what the submitted berths have and this berth lacks is listed", half.Percent is > 50 and < 60 && half.Short.Count == 2 && half.Short[0].Kind == "Specialty Equipment: Fender"
            && half.Short[0].Have == 0 && half.Short[0].Expected == 11 && CompareModelling([], []).Percent is null, $"{half.Percent} {string.Join("|", half.Short)}");
        // Modelling after a reading: against the submitted berths' saved counts; the only submitted berth is the example; the BIM model follows its berths.
        var mSetup = new Setup
        {
            Models = [new("Port A", "PORTA")],
            Berths = [new("Port A", "Berth 1", FindBy.WholeModel), new("Port A", "Berth 2", FindBy.WholeModel), new("Port A", "BIM Model", FindBy.WholeModel)],
            Submissions = [new("Port A", "Berth 1", "S1", new DateTime(2026, 9, 1), null, true), new("Port A", "Berth 2", "S2", new DateTime(2026, 12, 1))],
        };
        mSetup.Berths[2] = mSetup.Berths[2] with { FindBy = "Whole model" };
        var mBerths = mSetup.Berths.Take(2).Select(b => b with { FindBy = FindBy.Parameter }).Concat([mSetup.Berths[2]]).ToList();
        mSetup.Berths = mBerths;
        var mState = new State();
        mState.For(mBerths[0].Key).Results[Modelling] = new AreaResult { Kinds = new(StringComparer.OrdinalIgnoreCase) { ["Structural Columns: Pile"] = 100, ["Floors: Slab"] = 10 } };
        mState.For(mBerths[1].Key).Results[Modelling] = new AreaResult { Kinds = new(StringComparer.OrdinalIgnoreCase) { ["Structural Columns: Pile"] = 50 } };
        mState.For(mBerths[2].Key).Results[Modelling] = new AreaResult { Kinds = new(StringComparer.OrdinalIgnoreCase) { ["Structural Columns: Pile"] = 150, ["Floors: Slab"] = 10 } };
        CompareModelling(mSetup, mState);
        var m1 = mState.For(mBerths[0].Key).Results[Modelling];
        var m2 = mState.For(mBerths[1].Key).Results[Modelling];
        var mw = mState.For(mBerths[2].Key).Results[Modelling];
        check("modelling: the only submitted berth is the example, the next one is held against it, the BIM model is their average",
            m1.Percent == 100 && m2.Percent is { } p2 && Math.Abs(p2 - 100.0 * 50 / 110) < 0.5 && m2.Findings.Any(f => f.Item == "Floors: Slab")
            && mw.Percent is { } pw && Math.Abs(pw - (100 + p2) / 2) < 0.5 && mw.Summary.Contains("average"), $"{m1.Percent} {m2.Percent} {mw.Percent}");


        // The Assembly Code table from an LOI matrix: codes wherever they sit in a row, their names, parents added.
        var codes = SketchProMax.Tools.AssemblyCodeText.FromRows([
            ["A   SUBSTRUCTURE", "Uniformat 2010 Assemble Code reference"],
            ["A10   Foundations", "A1010", "Standard foundations", "A1010.10", "Wall Foundation", "G1", "200", "200", "G2", "300"],
            ["", "A1020.15", "Bored Piles", "G1", "200", "SE"],
            ["GEO"], ["A"], ["B1010.20", "G1"],
        ]);
        check("assembly codes: an LOI matrix gives each code its name and level", codes.Select(c => c.Code).SequenceEqual(["A", "A10", "A1010", "A1010.10", "A1020", "A1020.15", "B", "B10", "B1010", "B1010.20"])
            && codes.First(c => c.Code == "A1020.15").Name == "Bored Piles" && codes.First(c => c.Code == "A").Name == "SUBSTRUCTURE" && codes.First(c => c.Code == "A1020").Name == "A1020"
            && codes.First(c => c.Code == "B1010.20").Name == "B1010.20" && SketchProMax.Tools.AssemblyCodeText.Level("A1020.15") == 4 && SketchProMax.Tools.AssemblyCodeText.Level("A10") == 2,
            string.Join("|", codes));
        check("UniFormat rules: a key with a thickness range is read as its category and range in mm, a plain key isn't",
            ChecklistMath.ThickRange("Floors 1450-3200mm") == ("Floors", 1450, 3200) && ChecklistMath.ThickRange(" *Slab* 3200 - 1450 MM") == ("*Slab*", 1450, 3200)
            && ChecklistMath.ThickRange("Floors") is null && ChecklistMath.ThickRange("*Pile Cap*") is null, "");
        var picked = SketchProMax.Tools.AssemblyCodeText.Picked("cat:Floors\tB1010.20\n12\tA1020.15\r\n13\t \nx\tB1010\ncat:\tB10\n14\tB1010.20 \n15");
        check("Assembly Codes: the kept lines are read as a category or type id and a code, empty codes and bad keys skipped",
            picked.Count == 3 && picked[0] == ("cat:Floors", "B1010.20") && picked[1] == ("12", "A1020.15") && picked[2] == ("14", "B1010.20"), string.Join(",", picked));
        var table = SketchProMax.Tools.AssemblyCodeText.TableText(codes);
        check("assembly codes: written in Revit's table format and read back", table.StartsWith("A\tSUBSTRUCTURE\t1\t\r\n", StringComparison.Ordinal)
            && SketchProMax.Tools.AssemblyCodeText.FromTable(table).SequenceEqual(codes), table.Split('\n')[0]);

        // The BEP data Excel read back: rows from the Type id header on; only filled, writable cells go back to the model.
        var bepSheets = new Dictionary<string, List<(int Row, Dictionary<int, string> Cells)>>
        {
            ["BEP data"] = [(1, new() { [0] = "BEP data of M" }), (3, new() { [0] = "Type id", [1] = "Category", [5] = "Assembly Code", [6] = "Suggested code", [7] = "Material" }),
                (4, new() { [0] = "123", [1] = "Floors", [5] = "B1010.20", [6] = "B1010.20", [7] = "n/a" }), (5, new() { [1] = "no id" })],
        };
        var bep = SketchProMax.Tools.BepSheetText.Table(bepSheets);
        check("BEP data: the sheet reads back by column name", bep.Count == 1 && bep[0]["Type id"] == "123" && bep[0]["Assembly Code"] == "B1010.20" && bep[0]["Material"] == "n/a", "");
        check("BEP data: only filled cells of parameter columns are written", SketchProMax.Tools.BepSheetText.Writable("Assembly Code", "B1010.20")
            && !SketchProMax.Tools.BepSheetText.Writable("Suggested code", "B1010") && !SketchProMax.Tools.BepSheetText.Writable("Material", "n/a")
            && !SketchProMax.Tools.BepSheetText.Writable("Material", "(varies)") && !SketchProMax.Tools.BepSheetText.Writable("Material", " ") && !SketchProMax.Tools.BepSheetText.Writable("Type", "X"), "");

        // Make it BIM-ready: a step runs when its rule is set and the check doesn't pass; one with no rule only when flagged.
        var readyRules = new Dictionary<string, string> { ["units"] = "Length mm 0", ["uniformatmap"] = "Floors=B1010", ["loimatrix"] = "B1010: Material" };
        var readyFlags = new Dictionary<string, bool?> { ["units"] = false, ["uniformat"] = true, ["navisview"] = true, ["landing"] = false };
        var ready = SketchProMax.Tools.BimReadyPlan.ToRun(r => readyRules.TryGetValue(r, out var v) ? v : "", k => readyFlags.TryGetValue(k, out var f) ? f : null).Select(st => st.Key).ToList();
        check("BIM-ready: runs set rules that don't pass, and flagged rule-free fixes", ready.SequenceEqual(["uniformat", "loi", "navisview"]), string.Join(",", ready));
        check("BIM-ready: only lines a hair off 0/90° are off axis; inclined ones are left", SketchProMax.Tools.BimReadyPlan.OffAxisTurn(0.05) is { } t1 && Math.Abs(t1 + 0.05) < 1e-9
            && SketchProMax.Tools.BimReadyPlan.OffAxisTurn(44.9) is null && SketchProMax.Tools.BimReadyPlan.OffAxisTurn(0.5) is null
            && SketchProMax.Tools.BimReadyPlan.OffAxisTurn(89.97) is { } t2 && Math.Abs(t2 - 0.03) < 1e-9 && SketchProMax.Tools.BimReadyPlan.OffAxisTurn(90) is null
            && SketchProMax.Tools.BimReadyPlan.OffAxisTurn(30) is null && SketchProMax.Tools.BimReadyPlan.EndMoveMm(6000, 0.01) < 2 && SketchProMax.Tools.BimReadyPlan.EndMoveMm(6000, 0.1) > 2, "");
        check("BIM-ready: warnings flagged: clears the off-axis ones and unjoins what doesn't touch, moving nothing", SketchProMax.Tools.BimReadyPlan.ToRun(_ => "", k => k == "warnings" ? true : false).Select(st => st.Key).SequenceEqual(["offaxisclear", "unjoinapart"]), "");
        check("BIM-ready: never deletes or exports", !SketchProMax.Tools.BimReadyPlan.Steps.Any(st => st.Key is "offaxis" or "levels" or "unusedviews" or "imports" or "links" or "groups" or "duplicates" or "parameters" or "nwcsettings" or "submission"), "");

        // Tagging: only the views the rules name need tags (a piles plan tags its piles); others need dimensions only.
        var tagSetup = new Setup();
        check("berth status: a piles plan must tag its piles", TagCategoriesFor(tagSetup, "BERTH 9 - PART 2 PILES PLAN", []).SequenceEqual(["Structural Columns"])
            && TagCategoriesFor(tagSetup, "Plan 1", ["BERTH 9 PILES PLAN - PART 2"]).Count == 1, "");
        check("berth status: an overall or framing plan needs no tags", TagCategoriesFor(tagSetup, "BERTH 9 OVERALL PLAN", []).Count == 0 && TagCategoriesFor(tagSetup, "BERTH 9 - PART 2 FRAMING PLAN", []).Count == 0, "");

        var week = new BerthStatusMath.Calendar();
        // Thursday 2026-10-29: due at 08:00 that day, so Wednesday's working day is the last before it.
        var thu = new DateTime(2026, 10, 29);
        var sub = new Submission("Port A", "Berth 1", "Stage 2", thu);
        check("berth status: a submission is due at 08:00 on its day", DueAt(sub, week) == thu.AddHours(8), DueAt(sub, week).ToString("s"));
        check("berth status: its own time wins", DueAt(sub with { Time = TimeSpan.FromHours(12) }, week) == thu.AddHours(12), "");
        var sunMorning = new DateTime(2026, 10, 25, 8, 0, 0);
        check("berth status: Sunday 08:00 to Thursday 08:00 is four working days", Math.Abs(WorkHours(sunMorning, thu.AddHours(8), week) - 36) < 1e-9, WorkHours(sunMorning, thu.AddHours(8), week).ToString(CultureInfo.InvariantCulture));
        var friday = new DateTime(2026, 10, 23, 10, 0, 0);
        check("berth status: Friday and Saturday don't count", WorkHours(friday, new DateTime(2026, 10, 24, 23, 0, 0), week) == 0, "");
        check("berth status: hours added skip the weekend and the night",
            AddWorkHours(new DateTime(2026, 10, 22, 15, 0, 0), 4, week) == new DateTime(2026, 10, 25, 10, 0, 0), AddWorkHours(new DateTime(2026, 10, 22, 15, 0, 0), 4, week).ToString("s"));
        check("berth status: days and times from the sheet",
            ParseDays("Sun-Thu").SequenceEqual([DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday])
            && ParseDays("Saturday, Sunday").SequenceEqual([DayOfWeek.Saturday, DayOfWeek.Sunday])
            && ParseTime("8 am") == TimeSpan.FromHours(8) && ParseTime("08:00") == TimeSpan.FromHours(8) && ParseTime("2:30 PM") == new TimeSpan(14, 30, 0)
            && ParseTime("0.375") == TimeSpan.FromHours(9) && ParseTime("noon") is null, "");
        check("berth status: dates from the sheet (text and Excel numbers)",
            ParseDate("2026-10-29") == thu && ParseDate("29/10/2026") == thu && ParseDate("29-Oct-2026") == thu && ParseDate("46324") == new DateTime(2026, 10, 29) && ParseDate("soon") is null,
            ParseDate("46324")?.ToString("s") ?? "null");

        // The setup Excel: written, read back the same.
        var temp = Path.Combine(Path.GetTempPath(), "spm-berth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        FolderForTests = Path.Combine(temp, "store");
        try
        {
            var example = Example();
            example.Weights[Drawings] = 30;
            example.Week.Due = TimeSpan.FromHours(9);
            example.ClashToleranceMm = 40;
            example.TagRules = [new("Piles plan", "Structural Columns, Structural Foundations"), new("Section", "Structural Columns")];
            var xlsx = Path.Combine(temp, "setup.xlsx");
            OfficeXlsx.Write(xlsx, "setup", SetupSheets(example));
            var back = ReadSetup(xlsx);
            check("berth status: the setup Excel reads back",
                back.Models.Count == example.Models.Count && back.Models[1].Links == "PORTB"
                && back.Berths.Select(b => b.Key).SequenceEqual(example.Berths.Select(b => b.Key)) && back.Berths[0].FindBy == FindBy.ScopeBox && back.Berths[1].Parameter == "Zone"
                && back.Submissions.Count == 2 && back.Submissions[0].Date == example.Submissions[0].Date && back.Submissions[1].Berth == ""
                && back.Planned.Count == 5 && back.Planned[0].What == "Structural Columns / Pile" && back.Planned[0].Count == 120
                && back.Deliverables.Count == 4 && back.WeightOf(Drawings) == 30 && back.Week.Due == TimeSpan.FromHours(9) && back.Week.Days.Count == 5
                && back.ClashToleranceMm == 40 && new Setup().ClashToleranceMm == 50
                && back.TagRules.Count == 2 && back.TagRules[0].Categories == "Structural Columns, Structural Foundations" && back.TagRules[1].ViewContains == "Section" && back.SourcePath == xlsx && back.SourceStamp.Length > 0,
                $"{back.Berths.Count} berths, {back.Submissions.Count} submissions, due {back.Week.Due}");

            var bad = new Setup { Models = [new("Port A", "PORTA")], Berths = [new("Port C", "Berth 9", FindBy.WholeModel)], Submissions = [new("Port A", "", "x", thu)] };
            var badFile = Path.Combine(temp, "bad.xlsx");
            var sheets = SetupSheets(bad);
            // A date that isn't one.
            sheets[2] = sheets[2] with { Rows = [new OfficeXlsx.Row(["Port A", "", "x", "next week", "", ""])] };
            OfficeXlsx.Write(badFile, "bad", sheets);
            string? error = null;
            try { ReadSetup(badFile); } catch (InvalidDataException ex) { error = ex.Message; }
            check("berth status: a wrong setup says what is wrong", error is not null && error.Contains("Port C") && error.Contains("next week"), error ?? "no error");

            // Scores: model results, a hand entry, deliverables by file and by hand.
            var setup = Example();
            setup.Deliverables = [new("Calculations", Path.Combine(temp, "calcs"), "{berth} calc"), new("Costing")];
            Directory.CreateDirectory(Path.Combine(temp, "calcs"));
            var berth = setup.Berths[0];
            var state = new State();
            var results = new Dictionary<string, AreaResult>
            {
                [Modelling] = new() { Percent = 50 },
                [Drawings] = new() { Percent = 100 },
            };
            var now = new DateTime(2026, 10, 25, 9, 0, 0);
            Record(setup, berth, state, results, "model.rvt", now);
            var s1 = Summarize(setup, berth, state, now);
            // Modelling 50 (w20), Drawings 100 (w20), Deliverables 0 (w15, calculations folder empty; costing not known).
            check("berth status: the status weighs the areas that have a percentage",
                s1.Percent is { } p1 && Math.Abs(p1 - (50 * 20 + 100 * 20 + 0 * 15) / 55.0) < 1e-6, Pct(s1.Percent));
            File.WriteAllText(Path.Combine(temp, "calcs", "Berth 1 calc rev2.pdf"), "x");
            state.For(berth.Key).HandFor(Modelling).Percent = 90;
            state.For(berth.Key).DeliveredFor("Costing").Percent = 40;
            var s2 = Summarize(setup, berth, state, now);
            var deliverables = s2.Scores.First(a => a.Area.Key == Deliverables);
            check("berth status: a hand percentage replaces the model's, and a found file counts the deliverable done",
                s2.Scores.First(a => a.Area.Key == Modelling) is { Percent: 90, From: Source.Hand } && deliverables.Percent == 70
                && s2.Deliverables[0].File.EndsWith("Berth 1 calc rev2.pdf", StringComparison.Ordinal), $"{deliverables.Percent}");
            var reread = new Dictionary<string, AreaResult> { [Drawings] = new() { Percent = 100 } };
            Record(setup, berth, state, reread, "model.rvt", now.AddHours(2));
            check("berth status: a new reading replaces the last one whole", !state.For(berth.Key).Results.ContainsKey(Modelling) && state.For(berth.Key).History.Count == 2, "");

            // Forecast: 40% on Sunday 09:00, 58% on Tuesday 09:00 (18 working hours): 1%/h, 42 h left -> Monday 15:00 (Friday and Saturday off).
            var history = new List<Snapshot> { new(new DateTime(2026, 10, 25, 9, 0, 0), 40) };
            var tue = new DateTime(2026, 10, 27, 9, 0, 0);
            var forecast = Forecast(history, 58, tue, week);
            check("berth status: the forecast follows the pace in working hours", forecast == new DateTime(2026, 11, 2, 15, 0, 0), forecast?.ToString("s") ?? "none");
            check("berth status: no forecast from one reading", Forecast([], 58, tue, week) is null && Forecast([new(tue.AddHours(-1), 60)], 58, tue, week) is null, "");
            check("berth status: verdicts",
                Judge(58, sub, forecast, tue, week) == Verdict.Behind
                && Judge(58, sub, new DateTime(2026, 10, 28, 16, 0, 0), tue, week) == Verdict.OnTrack
                && Judge(58, sub, new DateTime(2026, 10, 29, 12, 0, 0), tue, week) == Verdict.AtRisk
                && Judge(58, sub, null, tue, week) == Verdict.NotKnown
                && Judge(58, sub, null, thu.AddHours(9), week) == Verdict.Overdue
                && Judge(100, sub, null, tue, week) == Verdict.Done
                && Judge(58, null, null, tue, week) == Verdict.NoSubmission, "");
            check("berth status: needed per working day", NeededPerDay(58, DueAt(sub, week), tue, week) is { } need && Math.Abs(need - 42 / 17.0 * 9) < 1e-6, "");

            // The submissions of a berth: its own and its model's, the next one not submitted.
            setup.Submissions = [new("Port A", "Berth 1", "Old", thu.AddDays(-7), null, true), new("Port A", "", "All berths", thu.AddDays(7)), new("Port A", "Berth 1", "Mine", thu), new("Port B", "", "Other", thu)];
            check("berth status: the next submission is the berth's or its model's, not submitted yet", Next(setup, berth)?.Name == "Mine" && SubmissionsFor(setup, berth).Count == 3, Next(setup, berth)?.Name ?? "none");
            check("berth status: the open model picks its setup model", ModelFor(setup, @"C:\x\PORTB-DOC-001.rvt")?.Name == "Port B" && ModelFor(setup, "PORTA-001_detached.rvt")?.Name == "Port A" && ModelFor(setup, "other.rvt") is null, "");

            // The berth's Excel, and the AI question.
            state.For(berth.Key).Results[Annotation] = new AreaResult { Percent = 40, Summary = "3 of 10 tagged", Findings = [new(Annotation, "Plan 1", "7 of 10 elements without a tag", 5)] };
            state.For(berth.Key).Comment = "Waiting for the pile layout.";
            var s3 = Summarize(setup, berth, state, tue);
            var report = SketchProMax.Tools.BerthStatusReport.Markdown(setup, state, [s3], tue);
            check("submissions status: the Word report has the overview, the areas, what to fix and the comment",
                report.Contains("## Overview") && report.Contains("| Berth 1 |") && report.Contains("| Annotation | 40%") && report.Contains("Plan 1: 7 of 10 elements without a tag")
                && report.Contains("Waiting for the pile layout.") && report.Contains("Next submission: Mine"), report.Length > 400 ? report.Substring(0, 400) : report);
            var reportDocx = Path.Combine(temp, "report.docx");
            SketchProMax.Tools.OfficeDocx.Write(reportDocx, "Submissions status", "PRJ1", report, landscape: true);
            check("submissions status: the Word report is written", File.Exists(reportDocx) && new FileInfo(reportDocx).Length > 2000, "");
            var file = Path.Combine(temp, "berth.xlsx");
            OfficeXlsx.Write(file, "berth", BerthSheets(setup, s3, tue));
            using (var stream = File.OpenRead(file))
            {
                var read = ChecklistMath.ReadSheets(stream);
                check("berth status: the berth's Excel has its status, findings and comments",
                    read.ContainsKey("Status") && read.ContainsKey("Submissions") && read.ContainsKey("Deliverables") && read.ContainsKey("Annotation") && read.ContainsKey("Comments") && read.ContainsKey("History")
                    && read["Annotation"].Any(r => r.Cells.Values.Contains("7 of 10 elements without a tag")) && read["Comments"].Any(r => r.Cells.Values.Contains("Waiting for the pile layout.")),
                    string.Join(", ", read.Keys));
            }
            var q = Question(setup, s3, tue);
            check("berth status: the AI question carries the schedule, the areas and the rules", q.Contains("Berth 1") && q.Contains("Mine") && q.Contains("08:00") && q.Contains("Annotation: 40%") && q.Contains("Don't change the model"), q);

            // A package marked submitted with no date reads back as submitted.
            var nodate = new Setup { Models = [new("Port A", "PORTA")], Berths = [new("Port A", "T1 Berths 5, 6", FindBy.WholeModel)], Submissions = [new("Port A", "T1 Berths 5, 6", "T1", NoDate, null, true)] };
            var nodateFile = Path.Combine(temp, "nodate.xlsx");
            OfficeXlsx.Write(nodateFile, "nodate", SetupSheets(nodate));
            var nodateBack = ReadSetup(nodateFile);
            var nodateSummary = Summarize(nodateBack, nodateBack.Berths[0], new State(), tue);
            check("berth status: a submission marked submitted with no date is kept", nodateBack.Submissions.Count == 1 && nodateBack.Submissions[0].Submitted
                && nodateSummary.Last is not null && When(nodateSummary.Last, "dd-MMM") == "", "");

            // Submitted packages keep their status and what is left to fix.
            setup.Submissions = [new("Port A", "Berth 1", "Done one", thu.AddDays(-7), null, true)];
            var done = Summarize(setup, berth, state, tue);
            check("berth status: a submitted berth is still scored, with fixes open", done.Next is null && done.Last?.Name == "Done one" && done.Verdict == Verdict.Submitted && done.Percent is not null,
                Text(done.Verdict));

            // Hand entries from the chat: by area or deliverable name, by berth code.
            setup.Berths = [new("Port A", "T3 Berths 9, 10", FindBy.Parameter, "B9-10", "Zone"), new("Port A", "T4 RORO", FindBy.ScopeBox, "RORO"), new("Port B", "A2 Bull Nose", FindBy.WholeModel)];
            check("berth status: berths found by code, name or a unique part",
                FindBerth(setup, "T3")?.Name == "T3 Berths 9, 10" && FindBerth(setup, "roro")?.Name == "T4 RORO" && FindBerth(setup, "Bull")?.Name == "A2 Bull Nose" && FindBerth(setup, "r") is null && FindBerth(setup, "T9") is null, "");
            setup.Deliverables = [new("Design of sections"), new("Calculations")];
            var t3 = setup.Berths[0];
            var said = SetByHand(setup, state, t3, "design of sections", 60, "Deck slab done, beams next");
            SetByHand(setup, state, t3, "drawings", 75, null);
            SetByHand(setup, state, t3, "berth", null, "Waiting for the fender loads");
            var t3s = Summarize(setup, t3, state, tue);
            check("berth status: the chat records deliverables, areas and the berth's comment",
                said.Contains("60%") && t3s.Deliverables.First(d => d.Deliverable.Name == "Design of sections") is { Percent: 60, Comment: "Deck slab done, beams next" }
                && t3s.Scores.First(x => x.Area.Key == Drawings) is { Percent: 75, From: Source.Hand } && state.For(t3.Key).Comment == "Waiting for the fender loads", said);
            SetByHand(setup, state, t3, "Drawings", null, null, clearPercent: true);
            string? refused = null;
            try { SetByHand(setup, state, t3, "weather", 10, null); } catch (ArgumentException ex) { refused = ex.Message; }
            check("berth status: clearing goes back to the model, and an unknown area is refused with the choices",
                state.For(t3.Key).Hand[Drawings].Percent is null && refused is not null && refused.Contains("Calculations") && refused.Contains("Drawings"), refused ?? "");

            // Saved per project.
            SaveSetup("PRJ1", setup);
            SaveState("PRJ1", state);
            check("berth status: setup and results are kept per project",
                LoadSetup("PRJ1")?.Berths.Count == setup.Berths.Count && LoadState("PRJ1").For(berth.Key).Comment == "Waiting for the pile layout." && Projects().SequenceEqual(["PRJ1"]) && LoadSetup("PRJ2") is null, "");
        }
        finally
        {
            FolderForTests = null;
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }
}
