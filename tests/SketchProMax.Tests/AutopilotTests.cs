using SketchProMax.Chat;

/// <summary>Autopilot's rules: what the coordinator is told, the answers given for the user, pausing at limits, the report.</summary>
internal static class AutopilotTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTimeOffset(2026, 10, 29, 23, 0, 0, TimeSpan.Zero);
        var run = new AutopilotRun { Goal = "Tag every wall and make sheets for levels 1-6", State = AutopilotRun.Running, Started = now.UtcDateTime.AddHours(-2) };

        // What the coordinator is told: plan first, then work alone as a senior, with a final report that ends the run.
        var plan = Autopilot.PlanRequest(run.Goal);
        check("autopilot: the goal is planned first, no job yet", plan.Contains(run.Goal) && plan.Contains("don't start any job yet"), plan);
        var approved = Autopilot.Approved(run);
        check("autopilot: approved, it never asks and takes the best way, not merely the safest", approved.Contains("Never ask the user") && approved.Contains("best way")
            && approved.Contains("change and delete whatever the goal needs") && approved.Contains(Autopilot.DoneMark), approved);
        check("autopilot: reports are reviewed against the goal, not just told", Autopilot.Review(run).Contains("review these reports") && Autopilot.Review(run).Contains(run.Goal), "");
        check("autopilot: a quiet turn gets a nudge that names the goal", Autopilot.Nudge(run).Contains("No job is open") && Autopilot.Nudge(run).Contains(run.Goal), "");
        check("autopilot: the final mark ends the run", ("All done.\n" + Autopilot.DoneMark).Contains(Autopilot.DoneMark), "");

        // Answering for the user: every approval is given (a backup was saved), dialogs get Cancel, No or OK.
        check("autopilot: approvals are given while the user is away", Autopilot.Allows("It deletes elements, and other elements go with them.").Allow, "");
        check("autopilot: a dialog with Cancel is cancelled", Autopilot.SafeResult(-1) == 2 && Autopilot.SafeResult(1) == 2 && Autopilot.SafeResult(3) == 2, "");
        check("autopilot: Yes/No gets No, OK only gets OK", Autopilot.SafeResult(4) == 7 && Autopilot.SafeResult(0) == 1 && Autopilot.ResultName(7) == "No", "");

        // Usage: pause at 90 %, carry on when the limit resets; a weekly pause waits for Resume when the setting says so.
        PlanUsage Usage(string key, double used, DateTimeOffset? reset) => new() { Windows = [new UsageWindow(key, used, reset, now)] };
        check("autopilot: under 90 % it keeps going", !Autopilot.ShouldPause(Usage(PlanUsage.FiveHour, 0.85, now.AddHours(1)), now, true).Pause, "");
        var five = Autopilot.ShouldPause(Usage(PlanUsage.FiveHour, 0.92, now.AddHours(1)), now, resumeWeekly: false);
        check("autopilot: at 90 % of the 5-hour limit it pauses until the reset", five.Pause && five.Until == now.AddHours(1), five.Why);
        var week = Autopilot.ShouldPause(Usage(PlanUsage.Weekly, 0.95, now.AddDays(2)), now, resumeWeekly: true);
        check("autopilot: a weekly limit carries on at its reset by default", week.Pause && week.Until == now.AddDays(2), "");
        check("autopilot: with weekly resume off it waits for Resume", Autopilot.ShouldPause(Usage(PlanUsage.Weekly, 0.95, now.AddDays(2)), now, resumeWeekly: false).Until is null, "");
        check("autopilot: a limit that already reset doesn't pause", !Autopilot.ShouldPause(Usage(PlanUsage.FiveHour, 0.99, now.AddMinutes(-1)), now, true).Pause, "");
        check("autopilot: extra usage doesn't pause", !Autopilot.ShouldPause(Usage(PlanUsage.Extra, 0.99, now.AddHours(1)), now, true).Pause, "");

        // Settings > Chat > Autopilot: its own 5-hour maximum, and its share of the week.
        var at80 = Autopilot.ShouldPause(Usage(PlanUsage.FiveHour, 0.81, now.AddHours(2)), now, true, 0.80, 0.25, 0);
        check("autopilot: a 5-hour maximum of 80 % pauses at 81 % until the reset", at80.Pause && at80.Until == now.AddHours(2) && !at80.WeekCap && at80.Why.Contains("80%"), at80.Why);
        check("autopilot: under its 5-hour maximum it keeps going", !Autopilot.ShouldPause(Usage(PlanUsage.FiveHour, 0.92, now.AddHours(2)), now, true, 0.95, 0.25, 0.1).Pause, "");
        var share = Autopilot.ShouldPause(Usage(PlanUsage.Weekly, 0.40, now.AddDays(3)), now, true, 0.90, 0.25, 0.26);
        check("autopilot: its share of the week used pauses it until the week resets", share.Pause && share.WeekCap && share.Until == now.AddDays(3) && share.Why.Contains("25%"), share.Why);
        check("autopilot: its share of the week waits for Resume when weekly resume is off",
            Autopilot.ShouldPause(Usage(PlanUsage.Weekly, 0.40, now.AddDays(3)), now, false, 0.90, 0.25, 0.26) is { Pause: true, Until: null, WeekCap: true }, "");
        check("autopilot: under its share of the week it keeps going", !Autopilot.ShouldPause(Usage(PlanUsage.Weekly, 0.40, now.AddDays(3)), now, true, 0.90, 0.25, 0.20).Pause, "");
        check("autopilot: no share of the week (0) never pauses for it", !Autopilot.ShouldPause(Usage(PlanUsage.Weekly, 0.40, now.AddDays(3)), now, true, 0.90, 0, 0.9).Pause, "");

        // The report: how it ended, its own jobs (not the chat's earlier ones), what was answered for the user.
        run.JobsBefore = 1;
        run.State = AutopilotRun.Done;
        run.Ended = now.UtcDateTime;
        run.Backup = @"C:\Models\Tower autopilot backup 2026-10-29 2100.rvt";
        run.Log.Add(new AutopilotEntry { Time = now.UtcDateTime, Kind = AutopilotEntry.Popup, Text = "Revit asked \"Delete the level?\": answered Cancel." });
        List<ChatJob> jobs = [new() { Number = 1, Title = "Earlier", State = JobBoard.Done }, new() { Number = 2, Title = "Tag walls", State = JobBoard.Done }, new() { Number = 3, Title = "Sheets", State = JobBoard.Failed }];
        var report = Autopilot.Report(run, jobs, now.UtcDateTime);
        check("autopilot: the report says it is met, how long, and the backup", report.Contains("the goal is met") && report.Contains("2 h 0 min") && report.Contains("autopilot backup"), report);
        check("autopilot: the report lists its own jobs only", report.Contains("Job 2 \"Tag walls\": done") && report.Contains("Job 3 \"Sheets\": ended with a problem") && !report.Contains("Earlier"), report);
        check("autopilot: the report lists the popups answered", report.Contains("Popups answered for you** (1)") && report.Contains("answered Cancel"), report);

        // The banner says where it is.
        var paused = new AutopilotRun { State = AutopilotRun.Paused, PausedUntil = now.AddHours(1), PauseWhy = "The current session (5-hour limit) is nearly used up." };
        check("autopilot: paused, the banner says until when", Autopilot.Banner(paused, 0, now).Contains("paused until"), Autopilot.Banner(paused, 0, now));
        check("autopilot: waiting for approval, the banner says so", Autopilot.Banner(new AutopilotRun { State = AutopilotRun.Approval }, 0, now).Contains("approve"), "");
    }
}
