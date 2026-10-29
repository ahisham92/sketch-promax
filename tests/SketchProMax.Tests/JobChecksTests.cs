using SketchProMax.Chat;

/// <summary>Each job's budget, the check of a finished job by a teammate, and scheduled routines with catch-up.</summary>
internal static class JobChecksTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 10, 29, 22, 0, 0, DateTimeKind.Utc);

        // ---- Budget ----
        double? seen = null;
        check("budget: the first report counts nothing", JobBudget.Rise(ref seen, 0.30) == 0 && seen == 0.30, "");
        check("budget: a rise counts", Math.Abs(JobBudget.Rise(ref seen, 0.34) - 0.04) < 1e-9, "");
        check("budget: a report without a figure counts nothing and keeps the last", JobBudget.Rise(ref seen, null) == 0 && seen == 0.34, "");
        check("budget: a lower figure is a reset window, what it shows was used since", Math.Abs(JobBudget.Rise(ref seen, 0.02) - 0.02) < 1e-9, "");
        check("budget: 15 % by default, 0 is off", JobBudget.DefaultPercent == 15 && JobBudget.Share(0) == 0 && JobBudget.Share(15) == 0.15, "");

        var jobs = new List<ChatJob>();
        var board = new JobBoard(jobs);
        var job = board.Start("Tag walls", "Tag every wall on levels 1-6", now, force: true, out _)!;
        board.ToStart(3, now);
        check("budget: under budget it keeps going", board.Charge(job.Key, 0.10, 0.15, now) is null && job.State == JobBoard.Running, job.State);
        board.AddItem(job, new ChatItem { Kind = "assistant", Text = "Tagged levels 1-3, now level 4." }, now);
        var paused = board.Charge(job.Key, 0.06, 0.15, now);
        check("budget: over budget the job pauses itself", paused == job && job.State == JobBoard.Stopped && job.OverBudget, job.State);
        check("budget: its view says why and how to carry on", job.Items.Any(i => i.Kind == "error" && i.Text.Contains("16%") && i.Text.Contains("Send it a message")), "");
        check("budget: its card says paused at its budget", JobBoard.StateLabel(job) == "Paused at its budget", JobBoard.StateLabel(job));
        var report = JobBoard.Report([job], 0);
        check("budget: the coordinator is told it was paused and to judge its progress", report.Contains("was paused at its budget") && report.Contains("making real progress") && report.Contains("Tagged levels 1-3"), report);
        check("budget: a message lets it carry on with a new budget", board.Message(job.Number, "carry on", now) == JobBoard.Delivery.Reopened && !job.OverBudget
            && Math.Abs(job.BudgetFrom - 0.16) < 1e-9 && !JobBudget.Over(job, 0.15), "");
        check("budget: off never pauses", board.Charge(job.Key, 0.5, 0, now) is null, "");

        // ---- Review ----
        var jobs2 = new List<ChatJob>();
        var b2 = new JobBoard(jobs2);
        var work = b2.Start("Sheets L1-3", "Make sheets for levels 1-3", now, force: true, out _)!;
        b2.ToStart(3, now);
        b2.RunEnded(work, true, "Made 3 sheets: A101-A103.", now);
        check("review: wanted for a finished job when on", JobReview.Wanted(JobReview.Always, false, work), "");
        check("review: only in an autopilot when set so", !JobReview.Wanted(JobReview.AutopilotOnly, false, work) && JobReview.Wanted(JobReview.AutopilotOnly, true, work), "");
        check("review: never when off", !JobReview.Wanted(JobReview.Off, true, work), "");
        var check1 = b2.StartReview(work.Number, now)!;
        check("review: a check job opens, waiting to start, and the job waits for it", check1.ReviewOf == work.Number && check1.State == JobBoard.Queued
            && work.Review == JobReview.Pending && work.ReviewedBy == check1.Number, "");
        check("review: the check gets the task and report and must not change the model", check1.Task.Contains("Make sheets for levels 1-3") && check1.Task.Contains("A101-A103")
            && check1.Task.Contains("Don't change") && check1.Task.Contains(JobReview.PassedWord), check1.Task);
        check("review: a check isn't checked", !JobReview.Wanted(JobReview.Always, true, check1), "");
        check("review: the job being checked isn't checked twice at once", !JobReview.Wanted(JobReview.Always, true, work), "");
        b2.ToStart(3, now);
        b2.RunEnded(check1, true, "All three sheets exist with their views.\n**PASSED**", now);
        check("review: a passed check goes on the job", b2.ReviewEnded(check1, now) == work && JobReview.Passed(work.Review), work.Review);
        check("review: the coordinator hears it passed", JobBoard.Report([work], 0).Contains("checked the work in the model: it passed"), "");
        // Found issues: the job is sent back, fixes, and is checked once more at most.
        b2.Message(work.Number, "add A104", now);
        check("review: a job carrying on is checked again later", work.Review == "", work.Review);
        b2.ToStart(3, now);
        b2.RunEnded(work, true, "Made A104 too.", now);
        var check2 = b2.StartReview(work.Number, now)!;
        b2.ToStart(3, now);
        b2.RunEnded(check2, true, "A101-A103 fine.\nISSUES: A104 has no view placed.\n- place the level 4 plan on A104", now);
        b2.ReviewEnded(check2, now);
        check("review: issues are kept with what to fix", work.Review.StartsWith("ISSUES: A104 has no view placed.") && work.Review.Contains("place the level 4 plan"), work.Review);
        var r2 = JobBoard.Report([work], 0);
        check("review: the coordinator is told what was found and to send the job back", r2.Contains("found: ISSUES") && r2.Contains($"Send job {work.Number} a message"), r2);
        b2.Message(work.Number, "fix it", now);
        b2.ToStart(3, now);
        b2.RunEnded(work, true, "Placed.", now);
        check("review: at most two checks of one job", !JobReview.Wanted(JobReview.Always, true, work), "");
        var failed = new ChatJob { ReviewOf = 1, State = JobBoard.Failed, Summary = "usage limit" };
        check("review: a check that failed says so", JobReview.Verdict(failed).StartsWith("The check couldn't finish"), JobReview.Verdict(failed));
        var crew = new CrewPlan();
        var seats = new List<ChatJob> { new() { Number = 1, Title = "a", State = JobBoard.Done, Worker = 0 }, new() { Number = 2, Title = "check", State = JobBoard.Running, ReviewOf = 1 } };
        crew.Update(seats, now, workers: 3);
        crew.Update(seats, now.AddSeconds(1), workers: 3);
        check("review: the check sits at another worker's desk", crew.Seats.FirstOrDefault(s => s.Number == 2) is { Slot: not 0 }, string.Join(",", crew.Seats.Select(s => s.Number + ":" + s.Slot)));

        // ---- Routines ----
        var routine = new Routine { Id = "r1", Name = "Nightly model check", Task = "Check the model.", Time = "02:00", Created = new DateTime(2026, 10, 20, 9, 0, 0) };
        var night = new DateTime(2026, 10, 29, 2, 0, 30); // a Thursday
        check("routines: due at its time", Routines.Due(routine, night) == new DateTime(2026, 10, 29, 2, 0, 0), "");
        check("routines: not before its time", Routines.Due(routine, new DateTime(2026, 10, 29, 1, 59, 0)) == new DateTime(2026, 10, 28, 2, 0, 0), "(yesterday's, never run)");
        Routines.Started(routine, new DateTime(2026, 10, 29, 2, 0, 0), now, night, "c1", "Routine · Nightly", 4, "Tower");
        check("routines: once run, not due again that day", Routines.Due(routine, new DateTime(2026, 10, 29, 23, 0, 0)) is null, "");
        // Revit closed for three nights: one catch-up run at the next start, not three.
        var monday = new DateTime(2026, 11, 2, 8, 15, 0);
        check("routines: missed nights catch up once, for the latest", Routines.Due(routine, monday) == new DateTime(2026, 11, 2, 2, 0, 0), "");
        var run = Routines.Started(routine, new DateTime(2026, 11, 2, 2, 0, 0), now, monday, "c2", "Routine · Nightly", 1, "Tower");
        check("routines: a late run is marked as caught up", run.CatchUp && routine.Runs[0] == run && Routines.Due(routine, monday.AddMinutes(1)) is null, "");
        check("routines: the history line says so", Routines.Line(run).Contains("caught up") && Routines.Line(run).Contains("Running"), Routines.Line(run));
        var list = new List<Routine> { routine };
        check("routines: the run's end is kept", Routines.Ended(list, "r1", "c2", 1, JobBoard.Done, "3 warnings found.", now) && run.State == JobBoard.Done && run.Summary == "3 warnings found.", "");
        check("routines: a new routine doesn't run for times before it existed", Routines.Due(new Routine { Time = "02:00", Created = monday }, monday.AddHours(1)) is null, "");
        var weekdays = new Routine { Time = "02:00", Days = [1, 2, 3, 4, 5], Created = new DateTime(2026, 10, 1) };
        check("routines: weekdays skip the weekend", Routines.Due(weekdays, new DateTime(2026, 11, 1, 12, 0, 0)) == new DateTime(2026, 10, 30, 2, 0, 0), ""); // Sunday: Friday's
        check("routines: schedule in words", Routines.Schedule(weekdays) == "Weekdays at 02:00" && Routines.Schedule(routine) == "Every day at 02:00"
            && Routines.Schedule(new Routine { Time = "18:30", Days = [4, 1], Model = "Tower" }) == "Mon, Thu at 18:30, on Tower", Routines.Schedule(weekdays));
        check("routines: next run", Routines.Next(weekdays, new DateTime(2026, 10, 31, 9, 0, 0)) == new DateTime(2026, 11, 2, 2, 0, 0), "");
        check("routines: off is never due", Routines.Due(new Routine { Time = "02:00", On = false }, monday) is null, "");
        check("routines: times read", Routines.ParseTime("2:05") == new TimeSpan(2, 5, 0) && Routines.ParseTime("25:00") is null && Routines.ParseTime("x") is null, "");
        var task = Routines.JobTask(routine, new DateTime(2026, 11, 2, 2, 0, 0), catchUp: true);
        check("routines: the job knows it runs alone and is a catch-up", task.Contains("don't ask questions") && task.Contains("catching up") && task.Contains("Check the model."), task);
        check("routines: ready-made nightly check and BIM-ready audit", Routines.Presets.Any(p => p.Name == "Nightly model check") && Routines.Presets.Any(p => p.Name == "BIM-ready audit")
            && Routines.Presets.All(p => p.Task.Contains("Don't change the model")), "");
        var folder = Path.Combine(Path.GetTempPath(), "spm-routines-" + Guid.NewGuid().ToString("N"));
        try
        {
            Routines.Save(folder, list);
            Routines.Save(folder, list);
            var back = Routines.Load(folder);
            check("routines: saved and read back", back.Count == 1 && back[0].Runs.Count == 2 && back[0].Runs[0].CatchUp && back[0].LastDue == routine.LastDue, "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        check("routines: a routine's job keeps its id", new ChatJob { Routine = "r1" }.Routine == "r1", "");
    }
}
