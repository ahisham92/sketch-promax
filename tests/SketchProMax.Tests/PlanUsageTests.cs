using SketchProMax.Chat;

/// <summary>The plan's usage meter: Claude Code's rate_limit_event read, merged, worded, warned about and kept.</summary>
internal static class PlanUsageTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1792600000);
        var utc = TimeZoneInfo.Utc;
        // The line exactly as Claude Code 2.1.306 wrote it with an answer on a subscription login.
        var line = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792614600,"rateLimitType":"five_hour","overageStatus":"allowed","overageResetsAt":1793491200,"isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.43,"resetsAt":1792614600},"seven_day":{"utilization":0.05,"resetsAt":1793192400}}},"uuid":"290ea18c","session_id":"s"}""";
        var events = new StreamParser().ParseLine(line);
        var usage = events.OfType<UsageEvent>().SingleOrDefault()?.Usage;
        check("usage: Claude Code's rate_limit_event is read", usage is { Status: "allowed", Binding: "five_hour", UsingExtra: false } && usage.Windows.Count == 2
            && usage.Windows[0] is { Key: PlanUsage.FiveHour, Used: 0.43 } && usage.Windows[0].ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1792614600)
            && usage.Windows[1] is { Key: PlanUsage.Weekly, Used: 0.05 }, events.Count.ToString());
        check("usage: the meter", usage!.Meter(now) == "5h 43%, 4h 3m left · Week 5%" && usage.Highest(now) == 0.43, usage.Meter(now));
        check("usage: the header meter leaves the time left to its hover, shorter when narrow",
            usage.Meter(now, timeLeft: false, narrow: false) == "5h 43% · Week 5%" && usage.Meter(now, timeLeft: false, narrow: true) == "5h 43% · Wk 5%",
            usage.Meter(now, timeLeft: false, narrow: true));
        check("usage: the hover gives each limit's time left and reset",
            usage.Hover(now, utc) == "Current session (5-hour limit): 43% used, 4h 3m left (resets 20:30)\nWeekly limit, all models: 5% used, resets Wed 28 Oct 13:00"
            && usage.Hover(now.AddHours(5), utc).StartsWith("Current session (5-hour limit): reset;"), usage.Hover(now, utc));
        check("usage: reset words", PlanUsage.ResetText(usage.Windows[0], now, utc) == "Resets in 4 h 3 min (20:30)"
            && PlanUsage.ResetText(usage.Windows[0], now.AddSeconds(14000), utc) == "Resets in 10 min (20:30)"
            && PlanUsage.ResetText(usage.Windows[1], now, utc) == "Resets Wed 28 Oct 13:00"
            && PlanUsage.ResetText(usage.Windows[0], now.AddHours(5), utc).StartsWith("Reset at 20:30")
            && PlanUsage.ResetText(new UsageWindow("x", 0.1, null, now), now, utc) == "Reset time not reported", PlanUsage.ResetText(usage.Windows[1], now, utc));
        check("usage: the session counts down to its reset", usage.Meter(now.AddSeconds(14000)) == "5h 43%, 10m left · Week 5%"
            && PlanUsage.Left(TimeSpan.FromSeconds(20)) == "1m" && PlanUsage.Left(TimeSpan.FromMinutes(130)) == "2h 10m", usage.Meter(now.AddSeconds(14000)));
        check("usage: percent rounds down", PlanUsage.Percent(0.796) == "79%" && PlanUsage.Percent(0.8) == "80%" && PlanUsage.Percent(1) == "100%" && PlanUsage.Percent(0) == "0%", PlanUsage.Percent(0.8));
        check("usage: a limit whose reset passed shows no figure", PlanUsage.Current(usage.Windows[0], now.AddHours(5)) is null && usage.Meter(now.AddHours(5)) == "5h reset · Week 5%", usage.Meter(now.AddHours(5)));

        var opus = PlanUsage.ParseLine("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1793192400,"rateLimitType":"seven_day_opus","utilization":0.81}}""", now.AddMinutes(5));
        check("usage: the limit in force adds its own window", opus is { Status: "allowed_warning" } && opus.Windows.Single() is { Key: PlanUsage.WeeklyOpus, Used: 0.81 }, "");
        var merged = PlanUsage.Merge(usage, opus!);
        check("usage: later reports merge, ordered session first", merged.Windows.Select(w => w.Key).SequenceEqual([PlanUsage.FiveHour, PlanUsage.Weekly, PlanUsage.WeeklyOpus])
            && merged.Windows[0].ReadAt == usage.Windows[0].ReadAt && merged.Windows[2].ReadAt == now.AddMinutes(5) && merged.Status == "allowed_warning" && merged.Meter(now) == "5h 43%, 4h 3m left · Week 5%",
            string.Join(",", merged.Windows.Select(w => w.Key)));
        var newer = PlanUsage.Merge(merged, PlanUsage.ParseLine(line.Replace("0.43", "0.5"), now.AddMinutes(9))!);
        check("usage: a newer figure replaces the older", newer.Windows.Count == 3 && newer.Windows[0].Used == 0.5 && newer.Windows[2].Used == 0.81, "");

        check("usage: a report without a figure gets none", PlanUsage.ParseLine("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"five_hour","resetsAt":1792614600}}""", now) is { } bare
            && bare.Windows.Single().Used is null && bare.Meter(now) == "5h —, 4h 3m left" && bare.Highest(now) is null, "");
        check("usage: other lines and junk aren't usage", PlanUsage.ParseLine("""{"type":"result"}""", now) is null && PlanUsage.ParseLine("nope", now) is null
            && PlanUsage.ParseLine("""{"type":"rate_limit_event","rate_limit_info":{}}""", now) is null
            && new StreamParser().ParseLine("""{"type":"rate_limit_event"}""").Count == 0, "");

        var warned = new HashSet<string>();
        check("usage: no warning under 80 %", usage.NewWarnings(warned, now).Count == 0 && warned.Count == 0, "");
        var first = merged.NewWarnings(warned, now);
        check("usage: the warning comes once per limit and reset", first.Count == 1 && first[0] is { Reached: false, Window.Key: PlanUsage.WeeklyOpus }
            && merged.NewWarnings(warned, now).Count == 0, first.Count.ToString());
        var reached = PlanUsage.Merge(merged, PlanUsage.ParseLine("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"seven_day_opus","utilization":1.02,"resetsAt":1793192400}}""", now)!);
        check("usage: and again when the limit is reached", reached.NewWarnings(warned, now) is [{ Reached: true }] && reached.NewWarnings(warned, now).Count == 0, "");
        var nextWeek = PlanUsage.ParseLine("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"seven_day_opus","utilization":0.85,"resetsAt":1793797200}}""", now)!;
        check("usage: after a reset it can warn again", nextWeek.NewWarnings(warned, now).Count == 1, "");
        var text = PlanUsage.Warning(first, ["Job 1 · Sheets for levels 1-5", "Job 3 · Wall check"], now, utc);
        check("usage: the warning names the limit, its reset and the running jobs", text == "Your plan is near its usage limit. Weekly limit, Opus: 81% used, resets Wed 28 Oct 13:00. Running now: Job 1 · Sheets for levels 1-5, Job 3 · Wall check. Stop the ones that can wait to save what is left.", text);
        check("usage: the reached warning, with no jobs running", PlanUsage.Warning([(reached.Windows[2], true)], [], now, utc).StartsWith("Your plan's usage limit is reached. Weekly limit, Opus: reached")
            && PlanUsage.Warning([(reached.Windows[2], true)], [], now, utc).EndsWith("No jobs are running."), "");
        check("usage: labels never say Claude", new[] { PlanUsage.FiveHour, PlanUsage.Weekly, PlanUsage.WeeklyOpus, PlanUsage.WeeklySonnet, PlanUsage.WeeklyIncluded, PlanUsage.Extra }
            .All(k => !PlanUsage.Label(k).Contains("Claude") && PlanUsage.Short(k).Length <= 9), "");

        var folder = Path.Combine(Path.GetTempPath(), "spm-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            check("usage: nothing kept at first", PlanUsage.Load(folder) is null, "");
            merged.Save(folder);
            var back = PlanUsage.Load(folder);
            check("usage: the last report is kept on disk", back is not null && back.Windows.Count == 3 && back.Windows[2].ResetsAt == merged.Windows[2].ResetsAt
                && back.ReadAt == merged.ReadAt && back.Meter(now) == merged.Meter(now), back?.Meter(now) ?? "null");
            File.WriteAllText(Path.Combine(folder, "usage.json"), "{ broken");
            check("usage: a damaged file reads as nothing", PlanUsage.Load(folder) is null, "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
