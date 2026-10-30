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
        check("budget: a lower figure from another job's older reading counts nothing and keeps the highest", JobBudget.Rise(ref seen, 0.02) == 0 && seen == 0.34, "");
        check("budget: a 55% reading after a 54% one never charges 55% (the 0.35.44 overnight bug)", JobBudget.Rise(ref seen, 0.55) > 0.2 && JobBudget.Rise(ref seen, 0.54) == 0 && Math.Abs(JobBudget.Rise(ref seen, 0.55)) < 1e-9, "");
        check("budget: a new window starts again, what it shows was used since", Math.Abs(JobBudget.Rise(ref seen, 0.02, reset: true) - 0.02) < 1e-9 && seen == 0.02, "");
        DateTimeOffset? resets = null;
        var at = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
        check("budget: the first reset time is no new window", !JobBudget.NewWindow(ref resets, at), "");
        check("budget: the same reset time (seconds apart) is no new window", !JobBudget.NewWindow(ref resets, at.AddSeconds(30)), "");
        check("budget: a reset time hours later is a new window", JobBudget.NewWindow(ref resets, at.AddHours(5)), "");
        check("budget: a rise is shared between the jobs and chat working", Math.Abs(JobBudget.ShareOf(0.09, 3) - 0.03) < 1e-9 && JobBudget.ShareOf(0.09, 0) == 0.09, "");
        // ---- Jobs that stop moving (the 29-30 Oct run: two reports written, runs never ended, 12 hours lost) ----
        var t0 = new DateTime(2026, 10, 29, 23, 0, 0, DateTimeKind.Utc);
        var hung = new ChatJob { Number = 2, Title = "Read as-built", State = JobBoard.Running, Started = t0 };
        hung.Items.Add(new ChatItem { Kind = "tool", Name = "get_coordinates", State = "done", Time = t0.AddMinutes(5) });
        hung.Items.Add(new ChatItem { Kind = "assistant", Text = "# Existing quay spec\n" + new string('x', 600), Time = t0.AddMinutes(8) });
        check("watch: a report written, then quiet 4 minutes, is fine", JobWatch.Judge(hung, t0.AddMinutes(12)) == JobWatch.Fine, "");
        check("watch: a report written, then quiet 5 minutes, is finished with it", JobWatch.Judge(hung, t0.AddMinutes(13)) == JobWatch.Finished, "");
        var slow = new ChatJob { Number = 3, Title = "Survey", State = JobBoard.Running, Started = t0 };
        slow.Items.Add(new ChatItem { Kind = "assistant", Text = "Nearly done, checking grids.", Time = t0.AddMinutes(1) });
        check("watch: a short line between steps is not a report", JobWatch.Judge(slow, t0.AddMinutes(10)) == JobWatch.Fine, "");
        check("watch: nothing for 20 minutes is stuck", JobWatch.Judge(slow, t0.AddMinutes(21)) == JobWatch.Stopped, "");
        slow.Activity = JobBoard.Applying;
        check("watch: a job whose change is being applied is never judged", JobWatch.Judge(slow, t0.AddHours(2)) == JobWatch.Fine, "");
        slow.Activity = "";
        check("watch: a job waiting its turn or finished is never judged", JobWatch.Judge(new ChatJob { State = JobBoard.Waiting, Started = t0 }, t0.AddHours(2)) == JobWatch.Fine
            && JobWatch.Judge(new ChatJob { State = JobBoard.Done, Started = t0 }, t0.AddHours(2)) == JobWatch.Fine, "");
        var stuckReport = JobWatch.StuckReport(slow);
        check("watch: a stuck job hands back what it found, and says not to hold others back", stuckReport.Contains("no progress for 20 minutes") && stuckReport.Contains("checking grids")
            && stuckReport.Contains("don't hold other parts back"), stuckReport);
        var stopBoard = new JobBoard([]);
        var toStop = stopBoard.Start("Read CAD", "Read the CAD", t0, force: true, out _)!;
        stopBoard.ToStart(3, t0);
        stopBoard.AddItem(toStop, new ChatItem { Kind = "assistant", Text = "## Survey report\nPits: 20, none in capping beams." }, t0);
        stopBoard.Stop(toStop.Number, t0.AddHours(1));
        check("watch: a job stopped by the coordinator keeps what it wrote as its report", stopBoard.Find(toStop.Number)!.Summary.Contains("Pits: 20")
            && JobBoard.Findings(stopBoard.Find(toStop.Number)!).Contains("Pits: 20"), "");

        // ---- No worker waits while there is work ----
        var busy = new ChatJob { Number = 25, Title = "PVC, voids, pits, fenders, grids", State = JobBoard.Running };
        foreach (var (id, st) in new[] { ("1", JobPlan.Finished), ("2", JobPlan.Doing), ("3", JobPlan.ToDo), ("4", JobPlan.ToDo), ("5", JobPlan.ToDo) }) busy.Plan[id] = st;
        var other = new ChatJob { Number = 24, Title = "Existing model", State = JobBoard.Running };
        other.Plan["1"] = JobPlan.ToDo;
        var offered = new HashSet<int>();
        check("free workers: one free of three, the job with the most steps not started is shared", FreeWorkers.ToShare([busy, other], 3, offered) == busy && FreeWorkers.Free([busy, other], 3) == 1, "");
        check("free workers: nobody free, nothing to share", FreeWorkers.ToShare([busy, other], 2, offered) is null, "");
        check("free workers: a job with one step left isn't split", FreeWorkers.ToShare([other], 3, offered) is null, "");
        offered.Add(busy.Number);
        check("free workers: each job is offered once", FreeWorkers.ToShare([busy, other], 3, offered) is null, "");
        check("free workers: reviewers and checks are never split", FreeWorkers.ToShare([new ChatJob { Number = 40, State = JobBoard.Running, GoalCheck = 1, Plan = busy.Plan }], 3, new HashSet<int>()) is null, "");
        var share = FreeWorkers.Nudge(busy, 1);
        check("free workers: the coordinator is told to hand steps to new jobs and tell the job", share.Contains("job 25") && share.Contains("3 steps") && share.Contains("chat_jobs start")
            && share.Contains("chat_jobs message") && share.Contains("not typed by the user"), share);

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
