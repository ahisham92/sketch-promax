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
    public static void Run(Action<string, bool, string> check)
    {
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
            var xlsx = Path.Combine(temp, "setup.xlsx");
            OfficeXlsx.Write(xlsx, "setup", SetupSheets(example));
            var back = ReadSetup(xlsx);
            check("berth status: the setup Excel reads back",
                back.Models.Count == example.Models.Count && back.Models[1].Links == "PORTB"
                && back.Berths.Select(b => b.Key).SequenceEqual(example.Berths.Select(b => b.Key)) && back.Berths[0].FindBy == FindBy.ScopeBox && back.Berths[1].Parameter == "Zone"
                && back.Submissions.Count == 2 && back.Submissions[0].Date == example.Submissions[0].Date && back.Submissions[1].Berth == ""
                && back.Planned.Count == 5 && back.Planned[0].What == "Structural Columns / Pile" && back.Planned[0].Count == 120
                && back.Deliverables.Count == 4 && back.WeightOf(Drawings) == 30 && back.Week.Due == TimeSpan.FromHours(9) && back.Week.Days.Count == 5
                && back.SourcePath == xlsx && back.SourceStamp.Length > 0,
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
