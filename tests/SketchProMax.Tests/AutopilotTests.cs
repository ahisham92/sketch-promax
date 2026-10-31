using SketchProMax.Chat;

/// <summary>Autopilot's rules: what the coordinator is told, the answers given for the user, pausing at limits, the report.</summary>
internal static class AutopilotTests
{
    private static bool Test(Func<bool> f) => f();

    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTimeOffset(2026, 10, 29, 23, 0, 0, TimeSpan.Zero);
        var run = new AutopilotRun { Goal = "Tag every wall and make sheets for levels 1-6", State = AutopilotRun.Running, Started = now.UtcDateTime.AddHours(-2) };

        // What the coordinator is told: plan first, then work alone as a senior, with a final report that ends the run.
        var plan = Autopilot.PlanRequest(run.Goal);
        check("autopilot: the goal is planned after reading, with read-only jobs, so questions come now", plan.Contains(run.Goal) && plan.Contains("read-only jobs")
            && plan.Contains("can't change anything before the plan is approved"), plan);
        check("autopilot: the plan ends with every question at once, each with its assumed answer", plan.Contains(Autopilot.QuestionsHeading) && plan.Contains("one numbered list")
            && plan.Contains("answer you will assume") && plan.Contains("Ask nothing later"), plan);
        check("autopilot: while reading jobs are out, no plan yet", Autopilot.PlanReports(2).Contains("2 reading job(s) are still working") && Autopilot.PlanReports(2).Contains("without the plan"), "");
        check("autopilot: once they reported, the plan and the questions", Autopilot.PlanReports(0).Contains("Now answer with the plan") && Autopilot.PlanReports(0).Contains(Autopilot.QuestionsHeading), "");
        var approved = Autopilot.Approved(run);
        check("autopilot: approved, it never asks and takes the best way, not merely the safest", approved.Contains("Never ask the user") && approved.Contains("best way")
            && approved.Contains("change and delete whatever the goal needs") && approved.Contains(Autopilot.DoneMark), approved);
        check("autopilot: it never waits for a go-ahead, even where a brief says so", approved.Contains("Never stop to wait for the user's confirmation")
            && approved.Contains("even where an earlier message or a brief says to wait") && Autopilot.Review(run).Contains("Don't wait for the user's go-ahead")
            && Autopilot.Nudge(run).Contains("don't wait for their go-ahead"), approved);
        check("autopilot: every worker kept busy, nothing held back for a slow job", approved.Contains("Keep every worker busy") && approved.Contains("never hold parts back")
            && Autopilot.Review(run).Contains("stopped for no progress"), "");
        check("autopilot: the coordinator knows a review team checks the goal", approved.Contains("review team checks every item"), "");
        check("autopilot: a brief in a run never adds a wait", PromptBriefs.PlanMessage("yes please do so", [], [], autopilot: true).Contains("Never add a step, rule or assumption that makes it wait")
            && !PromptBriefs.PlanMessage("yes please do so", [], []).Contains("autopilot run"), "");
        check("autopilot: reports are reviewed against the goal, not just told", Autopilot.Review(run).Contains("review these reports") && Autopilot.Review(run).Contains(run.Goal), "");
        check("autopilot: a quiet turn gets a nudge that names the goal", Autopilot.Nudge(run).Contains("No job is open") && Autopilot.Nudge(run).Contains(run.Goal), "");
        // What the user writes after the start (how to do the plans, say) stays with the goal: every prompt, every job and
        // the review team carry it, in full (31 Oct 2026: comments were lost after their turn and the goal was cut at 400).
        var heard = new AutopilotRun { Goal = run.Goal + " " + new string('x', 600) + " END-OF-GOAL", State = AutopilotRun.Running, Started = run.Started };
        Autopilot.Heard(heard, "On the plans, put the grid bubbles at the top and left only");
        Autopilot.Heard(heard, "On the plans, put the grid bubbles at the top and left only");
        check("autopilot: a comment written mid-run is kept once", heard.Said.Count == 1, string.Join("|", heard.Said));
        check("autopilot: the full goal and the comment reach every prompt", new[] { Autopilot.Nudge(heard), Autopilot.Review(heard), Autopilot.Approved(heard),
            Autopilot.Resume(heard, "x"), GoalReview.SendBack(heard, "- sheets"), GoalReview.Task(heard, 0, "") }
            .All(t => t.Contains("END-OF-GOAL") && t.Contains("grid bubbles at the top and left only") && t.Contains("wins over the goal and the plan")), Autopilot.Nudge(heard));
        check("autopilot: the user's how-to is a requirement, copied into each job's task", Autopilot.Approved(heard).Contains("do it their way exactly")
            && Autopilot.Approved(heard).Contains("word for word"), "");
        check("autopilot: jobs get the user's words themselves", Autopilot.UserWords.Contains("grid bubbles") && Autopilot.JobWords().Contains("do it their way exactly"), Autopilot.JobWords());
        check("autopilot: the review team checks how the user said to do it", GoalReview.Task(heard, 1, "").Contains("including HOW they said to do it"), "");
        Autopilot.UserWords = "";
        check("autopilot: no run, no words added to jobs", Autopilot.JobWords() == "", "");
        check("autopilot: a run that hasn't started keeps nothing (its changes go into the goal)", Test(() => { var r = new AutopilotRun(); Autopilot.Heard(r, "x"); return r.Said.Count == 0; }), "");

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

        check("autopilot: met without a review round says so plainly", !report.Contains("review team"), report);
        run.ReviewRound = 1;
        check("autopilot: met after the review team says it was checked", Autopilot.Report(run, jobs, now.UtcDateTime).Contains("the goal is met, checked by the review team"), "");
        var short_ = new AutopilotRun { Goal = run.Goal, State = AutopilotRun.Stopped, PauseWhy = "the review team still found work not done after 5 rounds", Started = run.Started, Ended = run.Ended,
            Gaps = "Modelling:\n- PVC pipes: none modelled; place PVC-01 to PVC-04" };
        var shortReport = Autopilot.Report(short_, jobs, now.UtcDateTime);
        check("autopilot: stopped short, the report never says met and lists what is still not done", !shortReport.Contains("goal is met") && shortReport.Contains("Still not done")
            && shortReport.Contains("PVC pipes: none modelled"), shortReport);

        // The review team: two reviewers side by side, read-only, met only when both say so; else it goes back.
        var goal = new AutopilotRun { Goal = "Berths 7-11 package: existing model, PVC pipes, sheets Rev 0 dated 05-11\nChange from the user: TL is inside the Revit model", Plan = "Stage 1 ...\n## When the goal is met\nEvery sheet passes the issue check.", State = AutopilotRun.Running, ReviewRound = 1 };
        var task0 = GoalReview.Task(goal, 0, "Done: 110 sheets.");
        var task1 = GoalReview.Task(goal, 1, "Done: 110 sheets.");
        check("review team: one reviewer for the modelling, one for the drawings and files", GoalReview.Parts.Length == 2 && task0.Contains("the modelling") && task1.Contains("drawings and deliverables")
            && task1.Contains("list_revit_models"), task1);
        check("review team: each gets the user's goal with their answers, the plan's checks and the claims", task0.Contains("TL is inside the Revit model") && task0.Contains("When the goal is met")
            && task0.Contains("claims to check, not proof") && task0.Contains("Done: 110 sheets."), task0);
        check("review team: items come from the user's words, checked in the model, not the reports", task0.Contains("from the goal, their answers and what they wrote since") && task0.Contains("(not from the plan or the report)")
            && task0.Contains("A report saying it is done is not proof"), task0);
        check("review team: left for later or 'to confirm' counts as not done unless the user allowed it", task0.Contains("left for later") && task0.Contains("when the user didn't allow that"), task0);
        check("review team: they never change anything and end with the verdict line", task0.Contains("Don't change anything") && task0.Contains(GoalReview.MetWord) && task0.Contains(GoalReview.NotMetWord), task0);
        ChatJob Reviewer(int n, string title, string summary, string state = JobBoard.Done) => new() { Number = n, Title = title, State = state, Summary = summary, GoalCheck = 1, ReadOnly = true };
        var met = Reviewer(30, "Review: modelling", "Checked 12 items, all there.\n**GOAL MET**");
        var notMet = Reviewer(31, "Review: drawings and files", "Checked 9 items.\nGOAL NOT MET:\n- PDF set: not exported\n- Berths 7-11 sheets: Rev 0 missing on 6 of 30");
        check("review team: a GOAL MET line is met", GoalReview.Verdict(met).Met, "");
        var v = GoalReview.Verdict(notMet);
        check("review team: GOAL NOT MET is not met (never read as MET), with its list", !v.Met && v.Gaps.Contains("PDF set: not exported") && v.Gaps.Contains("6 of 30"), v.Gaps);
        check("review team: a reviewer with no verdict is not taken as met", !GoalReview.Verdict(Reviewer(32, "Review: modelling", "I looked around.")).Met, "");
        check("review team: a reviewer that couldn't finish is not taken as met", GoalReview.Verdict(Reviewer(33, "Review: modelling", "", JobBoard.Failed)) is { Met: false } f && f.Gaps.Contains("couldn't finish"), "");
        check("review team: met only when every reviewer says so", GoalReview.Combine([met, Reviewer(34, "Review: drawings and files", "GOAL MET")]).Met && !GoalReview.Combine([met, notMet]).Met
            && !GoalReview.Combine([]).Met, "");
        var gaps = GoalReview.Combine([met, notMet]).Gaps;
        check("review team: the gaps say which side they come from", gaps.StartsWith("drawings and files:") && gaps.Contains("PDF set"), gaps);
        var back = GoalReview.SendBack(goal, gaps);
        check("review team: the gaps go back to the coordinator to carry on, without stopping or asking", back.Contains("NOT met yet") && back.Contains("PDF set") && back.Contains("Don't stop")
            && back.Contains("round 1 of " + GoalReview.MaxRounds) && back.Contains(Autopilot.DoneMark), back);
        check("review team: rounds are limited", GoalReview.MoreRounds(new AutopilotRun { ReviewRound = GoalReview.MaxRounds - 1 }) && !GoalReview.MoreRounds(new AutopilotRun { ReviewRound = GoalReview.MaxRounds }), "");
        goal.Gaps = gaps;
        check("review team: the next round checks last round's gaps first", GoalReview.Task(goal, 0, "").Contains("check them first") && GoalReview.Task(goal, 0, "").Contains("PDF set"), "");
        var board = new JobBoard([]);
        var rev = board.StartGoalCheck("Review: modelling", task0, 1, now.UtcDateTime);
        check("review team: a reviewer is a read-only job of its round, not checked itself by a teammate", rev.GoalCheck == 1 && rev.ReadOnly && rev.State == JobBoard.Queued
            && !JobReview.Wanted(JobReview.Always, true, new ChatJob { State = JobBoard.Done, GoalCheck = 1 }), "");

        // Reading before the plan is approved: read-only; approval lets those jobs change the model, never a reviewer.
        var reading = board.Start("Read as-built drawings", "Read the as-built folder", now.UtcDateTime, force: true, out _)!;
        board.ReadOnlyUntilStarted();
        check("autopilot: jobs started before approval may only read", board.Find(reading.Number)!.ReadOnly, "");
        board.AllowChanges();
        check("autopilot: approved, the reading jobs may change the model, reviewers still can't", !board.Find(reading.Number)!.ReadOnly && board.Find(rev.Number)!.ReadOnly, "");

        // The banner says where it is.
        var paused = new AutopilotRun { State = AutopilotRun.Paused, PausedUntil = now.AddHours(1), PauseWhy = "The current session (5-hour limit) is nearly used up." };
        check("autopilot: paused, the banner says until when", Autopilot.Banner(paused, 0, now).Contains("paused until"), Autopilot.Banner(paused, 0, now));
        check("autopilot: waiting for approval, the banner says so", Autopilot.Banner(new AutopilotRun { State = AutopilotRun.Approval }, 0, now).Contains("approve"), "");
    }
}
