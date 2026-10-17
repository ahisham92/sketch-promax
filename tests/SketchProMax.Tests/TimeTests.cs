using SketchProMax.Tools;

/// <summary>Working time and time saved (Revit/TimeLog's arithmetic): rates, minute bucketing, idle cutoff, the AI and by-hand split, the queue merge.</summary>
internal static class TimeTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Rates and estimates
        var sheets = TimeMath.RateFor("create_sheets", true, null);
        check("time: built-in rate is an estimate", sheets.PerRun == 2 && sheets.PerItem == 2 && sheets.Estimate, sheets.ToString());
        var mine = new List<TimeRate> { new("create_sheets", 5, 3) };
        var own = TimeMath.RateFor("CREATE_SHEETS", true, mine);
        check("time: the user's rate replaces the estimate", own.PerRun == 5 && own.PerItem == 3 && !own.Estimate, own.ToString());
        var other = TimeMath.RateFor("some_new_tool", true, null);
        check("time: an unknown tool that changes the model gets the fallback, under its own name",
            other.Tool == "some_new_tool" && other.PerRun == TimeMath.Defaults[TimeMath.OtherChanges].Run, other.ToString());
        check("time: an unknown read-only tool saves nothing", TimeMath.RateFor("some_query", false, null) is { PerRun: 0, PerItem: 0 }, "");
        check("time: estimate is per run plus per item", TimeMath.EstimateMinutes(new TimeRate("t", 2, 1.5), 4) == 8, "");
        check("time: a failed run counts only finished items", TimeMath.EstimateMinutes(new TimeRate("t", 2, 1.5), 0, failed: true) == 0
                                                              && TimeMath.EstimateMinutes(new TimeRate("t", 2, 1.5), 2, failed: true) == 3, "");
        check("time: estimate is capped", TimeMath.EstimateMinutes(new TimeRate("t", 0, 1), 1_000_000) == TimeMath.MaxEstimateMinutes, "");
        check("time: items are batch items, else added, else changed", TimeMath.Units(3, 9, 9) == 3 && TimeMath.Units(0, 9, 4) == 9 && TimeMath.Units(0, 0, 4) == 4, "");
        var table = TimeMath.RateTable(mine.Append(new TimeRate("my_learned_tool", 1, 0)));
        check("time: rate table lists estimates, yours and extra tools",
            table.Count == TimeMath.Defaults.Count + 1 && table.Single(r => r.Tool == "create_sheets") is { Estimate: false, PerRun: 5 }
            && table.Single(r => r.Tool == "tag_elements_in_view").Estimate && table.Any(r => r.Tool == "my_learned_tool" && !r.Estimate)
            && table.Last().Tool.StartsWith("("), "");
        var kept = TimeMath.Overrides([new TimeRate("create_sheets", 2, 2), new TimeRate("create_walls", 9, 9), new TimeRate("my_tool", 0, 0)]);
        check("time: only rates that differ from the estimates are kept", kept.Select(r => r.Tool).SequenceEqual(["create_walls", "my_tool"]), string.Join(",", kept.Select(r => r.Tool)));

        // ---- Saved and the line
        check("time: saved never below zero", TimeMath.Saved(60, 90) == 0 && TimeMath.Saved(600, 90) == 510, "");
        check("time: durations", TimeMath.Duration(40) == "40 s" && TimeMath.Duration(1500) == "25 min" && TimeMath.Duration(3900) == "1 h 05 min", TimeMath.Duration(3900));
        var line = TimeMath.Line(40, 25 * 60);
        check("time: the done line", line == "Done in 40 s. By hand: about 25 min. Saved: 24 min.", line);
        var split = TimeMath.SplitChat(600, [1200, 600]);
        check("time: a chat's saving is shared over its tools and its time taken off once", split.SequenceEqual([800.0, 400.0]), string.Join(",", split));
        check("time: a chat longer than its tools' estimates saves nothing", TimeMath.SplitChat(4000, [1200, 600]).All(s => s == 0) && TimeMath.SplitChat(10, []).Count == 0, "");

        // ---- Minute bucketing and idle cutoff
        var t0 = new DateTime(2026, 10, 17, 9, 0, 0);
        double Hand(ActivityClock c, DateTime now, string doc = "A") => c.Drain(now).Where(d => d.Doc == doc).Sum(d => d.Seconds);

        var clock = new ActivityClock();
        for (var i = 0; i < 12; i++) clock.Activity("A", t0.AddSeconds(30 * i));
        check("time: activity every 30 s for 6 minutes is 6 minutes", Hand(clock, t0.AddMinutes(30)) == 360, "");

        clock = new ActivityClock();
        clock.Activity("A", t0);
        clock.Activity("A", t0.AddMinutes(4));
        check("time: a gap under the idle threshold is bridged", Hand(clock, t0.AddMinutes(30)) == 300, "");
        clock = new ActivityClock();
        clock.Activity("A", t0);
        clock.Activity("A", t0.AddMinutes(7));
        check("time: a gap over the idle threshold counts nothing", Hand(clock, t0.AddMinutes(30)) == 120, "");
        clock = new ActivityClock { Idle = TimeSpan.FromMinutes(10) };
        clock.Activity("A", t0);
        clock.Activity("A", t0.AddMinutes(7));
        check("time: the idle threshold is a setting", Hand(clock, t0.AddMinutes(30)) == 480, "");

        clock = new ActivityClock();
        clock.Activity("A", t0);
        clock.Activity("B", t0.AddMinutes(1));
        clock.Activity("A", t0.AddMinutes(3));
        var two = clock.Drain(t0.AddMinutes(30));
        check("time: another model's activity ends the bridge, no minute counted for two models",
            two.Where(d => d.Doc == "A").Sum(d => d.Seconds) == 120 && two.Where(d => d.Doc == "B").Sum(d => d.Seconds) == 60, string.Join(";", two));

        clock = new ActivityClock();
        clock.Activity("A", t0);
        var early = Hand(clock, t0.AddMinutes(3));
        clock.Activity("A", t0.AddMinutes(4));
        check("time: a minute is not final until no bridge can reach it", early == 0 && Hand(clock, t0.AddMinutes(30)) == 300, early.ToString());
        clock = new ActivityClock();
        clock.Activity("A", t0);
        Hand(clock, t0.AddMinutes(10));
        clock.Activity("A", t0.AddMinutes(10));
        check("time: a drained minute is never counted again", Hand(clock, t0.AddMinutes(30)) == 60, "");

        // ---- AI versus by hand
        clock = new ActivityClock();
        clock.Activity("A", t0.AddSeconds(10));
        var task = clock.TaskStart(t0.AddSeconds(30));
        clock.Activity("A", t0.AddSeconds(70)); // the tool's own changes: not by hand
        clock.TaskEnd(task, "A", t0.AddSeconds(135));
        clock.Activity("A", t0.AddSeconds(165));
        var hand = Hand(clock, t0.AddMinutes(30));
        check("time: minutes shared with a task give only their other seconds to by hand", hand == 30 + 45, hand.ToString());
        check("time: AI plus by hand is the active time, nothing twice", hand + 105 == 180, "");

        clock = new ActivityClock();
        var outer = clock.TaskStart(t0);
        var inner = clock.TaskStart(t0.AddSeconds(20));
        clock.TaskEnd(inner, "A", t0.AddSeconds(40));
        clock.Activity("A", t0.AddSeconds(50)); // the chat still runs
        clock.TaskEnd(outer, "A", t0.AddSeconds(90));
        clock.Activity("A", t0.AddSeconds(100));
        hand = Hand(clock, t0.AddMinutes(30));
        check("time: a tool inside a chat is not counted twice", hand == 30, hand.ToString());

        clock = new ActivityClock();
        clock.Activity("A", t0);
        var running = clock.TaskStart(t0.AddSeconds(30));
        var during = Hand(clock, t0.AddMinutes(30));
        clock.TaskEnd(running, "A", t0.AddMinutes(31));
        check("time: a running task holds its minutes until it ends", during == 0 && Hand(clock, t0.AddMinutes(60)) == 30, during.ToString());

        clock = new ActivityClock();
        var long1 = clock.TaskStart(t0);
        clock.TaskEnd(long1, "A", t0.AddMinutes(20));
        check("time: after a long task nothing is by hand until the user is back", Hand(clock, t0.AddMinutes(40)) == 0, "");

        // ---- Sheets: storage format and the queue merge
        var day = "2026-10-17";
        var pc = new TimeSheet("alex", "PC1");
        pc.Add(day, TimeMath.HandTool, byHand: 600);
        pc.Add(day, "create_sheets", ai: 40, estimate: 1500, saved: 1460, runs: 1, units: 4);
        var parsed = TimeSheet.Parse("alex", "PC1", pc.Serialize());
        check("time: rows survive the model's text", parsed.Serialize() == pc.Serialize() && parsed.Rows.Count == 2, parsed.Serialize());
        check("time: unreadable lines are skipped", TimeSheet.Parse("a", "b", "junk\n2026-10-17\tx\t1\t2\n" + pc.Serialize()).Rows.Count == 2, "");

        var model = pc.Copy();
        pc.Add(day, "create_sheets", ai: 20, estimate: 300, saved: 280, runs: 1, units: 1);
        pc.Add(day, TimeMath.HandTool, byHand: 120);
        check("time: the PC is ahead of the model while an entry is queued", model.IsBehind(pc) && !pc.IsBehind(model), "");
        var merged = model.Copy();
        merged.MergeMax(pc);
        check("time: the queue merges into the model", merged.Serialize() == pc.Serialize(), merged.Serialize());
        var again = merged.Copy();
        check("time: merging twice changes nothing", !again.MergeMax(pc) && again.Serialize() == merged.Serialize(), "");
        var older = pc.Copy();
        older.MergeMax(model);
        check("time: an older copy never takes time away", older.Serialize() == pc.Serialize(), "");
        var lost = new TimeSheet("alex", "PC1");
        lost.MergeMax(merged);
        check("time: a lost PC file is filled back from the model", lost.Serialize() == merged.Serialize(), "");

        var bob = new TimeSheet("bob", "PC2");
        bob.Add(day, "tag_elements_in_view", ai: 60, estimate: 900, saved: 840, runs: 1, units: 20);
        bob.Add("2026-10-16", TimeMath.HandTool, byHand: 1800);
        var combined = TimeSheet.Combine([model, pc, bob, model]);
        check("time: copies of one user and PC become one, other users stay apart", combined.Count == 2, combined.Count.ToString());

        // ---- The report
        var report = new TimeReport { Model = "Test model", Sheets = combined };
        var tt = report.Totals;
        check("time: totals cover all users", tt.Ai == 120 && tt.ByHand == 2520 && tt.Working == 2640 && tt.Saved == 2580 && tt.Runs == 3, tt.ToString());
        check("time: fully by hand is working time plus time saved", tt.FullyByHand == 2640 + 2580, "");
        var byTool = report.ByTool();
        check("time: per tool, largest saving first, by-hand row left out",
            byTool.Select(x => x.Tool).SequenceEqual(["create_sheets", "tag_elements_in_view"]) && byTool[0].Units == 5 && byTool[0].Totals.Runs == 2, string.Join(",", byTool.Select(x => x.Tool)));
        var byDay = report.ByDay();
        check("time: per day, newest first", byDay.Select(x => x.Day).SequenceEqual([day, "2026-10-16"]) && byDay[1].Totals.ByHand == 1800, "");
        var excel = report.ExcelSheets();
        check("time: Excel has summary, per tool, per day and all rows, with a user column",
            excel.Select(s => s.Name).SequenceEqual(["Summary", "By tool", "By day", "All rows"]) && (string)excel[1].Rows[0][0]! == "User"
            && (string)excel[2].Rows[0][0]! == "User" && excel[3].Rows.Count == 5, "");
        var csv = report.Csv();
        check("time: CSV for Export for Claude", csv.Contains("By tool") && csv.Contains("bob,tag_elements_in_view,1,20"), csv);
        var file = Path.Combine(Path.GetTempPath(), $"spm-time-{Guid.NewGuid():N}.xlsx");
        try
        {
            Xlsx.Write(file, excel);
            using var zip = System.IO.Compression.ZipFile.OpenRead(file);
            check("time: the Excel file is written", zip.GetEntry("xl/worksheets/sheet4.xml") is not null, "");
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }
}
