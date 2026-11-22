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
        // ---- A check after a pass checks only the later asks (bug 113) ----
        var passed = new ChatJob { Number = 112, Title = "Grading fix", Task = "Fix the slab.", Summary = "Done.", PassedAt = now };
        passed.Items.Add(new ChatItem { Kind = "user", Text = "Fix the slab.", Time = now.AddMinutes(-40) });
        passed.Items.Add(new ChatItem { Kind = "user", Text = "Don't change the working plan's crop.", Time = now.AddMinutes(2) });
        var after = JobReview.Task(passed);
        check("check (bug 113): after a pass it checks only what was asked since, and raises nothing new",
            after.Contains("already PASSED") && after.Contains("Don't change the working plan's crop.")
            && JobReview.AsksSince(passed, now) == "Don't change the working plan's crop." && !JobReview.Task(new ChatJob { Number = 1, Task = "x" }).Contains("already PASSED"), after);
        check("job (bug 115): a job starts with the chat's agreed rules, the ones its check judges by",
            JobReview.RulesForJob("- Modelling rules: no floor cuts.") is { } jr && jr.Contains("no floor cuts") && jr.Contains("judges it by them")
            && JobReview.RulesForJob("  ") == "", "");
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
        slow.Activity = JobBoard.Blocked;
        check("watch: a job whose call waits for Revit to take work (a command or dialog of Revit's own) is never judged; its card says so",
            JobWatch.Judge(slow, t0.AddHours(1)) == JobWatch.Fine && JobBoard.ActivityOf(slow) == JobBoard.Blocked && JobBoard.StateLabel(slow).Contains("Waiting for Revit"), JobBoard.StateLabel(slow));
        slow.Activity = "";
        slow.Moved = t0.AddMinutes(30);
        check("watch: a Revit call that just ended after a long wait is a move: the job isn't stopped the moment its call ends",
            JobWatch.Judge(slow, t0.AddMinutes(31)) == JobWatch.Fine && JobWatch.Judge(slow, t0.AddMinutes(51)) == JobWatch.Stopped, "");
        slow.Moved = default;
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

        check("free workers: the coordinator's \"No split.\" is an internal answer the chat never shows",
            FreeWorkers.IsNothing("No split.") && FreeWorkers.IsNothing("  no split\n") && FreeWorkers.IsNothing("**No split.**")
            && FreeWorkers.IsNothing("No split: every step needs the one before.") && FreeWorkers.IsNothing("")
            && share.Contains("never shown"), "");
        var mixed = "Job 27 is reshaping the 6 Berth 10 approach slabs. Any slab joined to a rear beam gets unjoined.\n\nNo split.";
        check("free workers: \"No split.\" at the end of a real answer is taken out, the answer stays (bug 94)",
            FreeWorkers.Strip(mixed) == "Job 27 is reshaping the 6 Berth 10 approach slabs. Any slab joined to a rear beam gets unjoined."
            && FreeWorkers.Strip("Handed steps 3-4 to job 26. No split.") == "Handed steps 3-4 to job 26."
            && FreeWorkers.Strip("No split.\r\nJob 27 carries on.") == "Job 27 carries on." && !FreeWorkers.IsNothing(mixed), FreeWorkers.Strip(mixed));
        check("free workers: text that only mentions splitting is left alone",
            FreeWorkers.Strip("Split the wall at grid C.") == "Split the wall at grid C." && FreeWorkers.Strip("There is no split joint here.") == "There is no split joint here."
            && FreeWorkers.Strip("Plain answer.") == "Plain answer.", "");
        check("free workers: a real answer after a nudge is still shown",
            !FreeWorkers.IsNothing("Started job 26 for the fenders and told job 25 to leave them.") && !FreeWorkers.IsNothing("Split off steps 3 and 4 into job 26."), "");

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
        b2.MarkChanged(work.Key);
        b2.RunEnded(work, true, "Made 3 sheets: A101-A103.", now);
        var reader = b2.Start("Read the title block", "Read the title block's parameters", now, force: true, out _)!;
        b2.ToStart(3, now);
        b2.RunEnded(reader, true, "It has 12 parameters.", now);
        check("review: a job that only read is checked too", JobReview.Wanted(JobReview.Always, true, reader), "");
        check("review (bug 131): a read-only job sent back and finished again without changing the model isn't checked again; one that changed it is",
            !JobReview.Wanted(JobReview.Always, true, new ChatJob { State = JobBoard.Done, Unchecked = true, Reviews = 1 })
            && JobReview.Wanted(JobReview.Always, true, new ChatJob { State = JobBoard.Done, Unchecked = true, Reviews = 1, ChangedModel = true }), "");
        check("coordinator (bug 131): known work starts at once, long lists split across free workers",
            System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../src/SketchProMax/Chat/ClaudeCli.cs")).Contains("Never hold known work back until a whole list is checked"), "");
        check("review: a job not marked as finished work isn't checked", !JobReview.Wanted(JobReview.Always, true, new ChatJob { State = JobBoard.Done }), "");
        b2.MarkChanged("nope");
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
        check("review: a job reported after its check is not checked again (bug 68: two checks per job)", !JobReview.Wanted(JobReview.Always, true, work) && !work.Unchecked, "");
        // Found issues: the job is sent back, fixes, and is checked once more at most.
        b2.Message(work.Number, "add A104", now);
        check("review: a job carrying on is checked again later", work.Review == "", work.Review);
        b2.ToStart(3, now);
        b2.MarkChanged(work.Key); // making A104 changes the model
        b2.RunEnded(work, true, "Made A104 too.", now);
        check("review: a job sent back is checked again once it finishes", JobReview.Wanted(JobReview.Always, true, work), "");
        var check2 = b2.StartReview(work.Number, now)!;
        b2.ToStart(3, now);
        b2.RunEnded(check2, true, "A101-A103 fine.\nISSUES: A104 has no view placed.\n- place the level 4 plan on A104", now);
        b2.ReviewEnded(check2, now);
        check("review: issues are kept with what to fix", work.Review.StartsWith("ISSUES: A104 has no view placed.") && work.Review.Contains("place the level 4 plan"), work.Review);
        check("review: a job with parts not done and checks left goes back by itself, told exactly those parts",
            JobReview.SendBack(work) && JobReview.FixMessage(work).Contains("A104 has no view placed") && JobReview.FixMessage(work).Contains("Finish exactly these parts")
            && JobReview.FixMessage(work).Contains("checked again"), JobReview.FixMessage(work));
        b2.Message(work.Number, JobReview.FixMessage(work), now);
        b2.MarkChanged(work.Key);
        b2.ToStart(3, now);
        b2.RunEnded(work, true, "Placed.", now);
        check("review: the job sent back is checked a third time", JobReview.Wanted(JobReview.Always, true, work), "");
        var check3 = b2.StartReview(work.Number, now, "make sheets for levels 1 to 4 with their plans")!;
        check("review: the check lists every part, reads each back, counts, looks at drawn work, and has the user's own words",
            check3.Task.Contains("numbered list of its parts") && check3.Task.Contains("count what was done against what") && check3.Task.Contains("capture_view_image")
            && check3.Task.Contains(JobReview.NotDoneWord) && check3.Task.Contains("make sheets for levels 1 to 4 with their plans") && !check3.Task.Contains("Be quick"), check3.Task);
        b2.ToStart(3, now);
        b2.RunEnded(check3, true, "1. A101-A103: DONE\n2. A104 plan: NOT DONE - the plan is on A104 but cropped empty\nISSUES: 2. A104 plan is cropped empty: reset its crop", now);
        b2.ReviewEnded(check3, now);
        check("review: after the last check nothing goes back by itself; unfinished parts reach the user named as not done",
            !JobReview.SendBack(work) && work.Reviews == JobReview.MaxReviews, work.Reviews + "");
        var r2 = JobBoard.Report([work], 0);
        check("review: the coordinator is told what is still not done, to say it first, and to send the job back or ask",
            r2.Contains("still not done: ISSUES") && r2.Contains("3 checks") && r2.Contains("don't say the work is finished") && r2.Contains($"send job {work.Number} a message")
            && r2.Contains("never call work finished while a part is open"), r2);
        b2.Message(work.Number, "fix it", now);
        b2.MarkChanged(work.Key);
        b2.ToStart(3, now);
        b2.RunEnded(work, true, "Fixed.", now);
        check("review: at most three checks of one job", !JobReview.Wanted(JobReview.Always, true, work), "");
        check("review: a passed job, a failed check or a job with no verdict is not sent back",
            !JobReview.SendBack(new ChatJob { State = JobBoard.Done, Review = JobReview.PassedWord }) && !JobReview.SendBack(new ChatJob { State = JobBoard.Done, Review = "The check couldn't finish: usage limit" })
            && !JobReview.SendBack(new ChatJob { State = JobBoard.Done }) && !JobReview.SendBack(new ChatJob { State = JobBoard.Done, Review = JobReview.Pending }), "");
        check("review: the third check reads the last check's not-done parts again first (bug 106)",
            check3.Task.Contains("This is check 3") && check3.Task.Contains("A104 has no view placed") && check3.Task.Contains("don't check everything again"), check3.Task);

        // Bug 106: parts waiting on the user or on another job pass, are told to the user, and never send the job back.
        var jobs3 = new List<ChatJob>();
        var b3 = new JobBoard(jobs3);
        var beam = b3.Start("1800 mm beam", "Make the front beam 1800 mm deep", now, force: true, out _)!;
        var pipes = b3.Start("Pipes", "Place the pipes through the deck", now, force: true, out _)!;
        b3.ToStart(3, now);
        b3.RunEnded(beam, true, "Beam 1800 mm; corner beam added. The +1.30 to +1.40 step needs your yes.", now.AddMinutes(1));
        if (beam.Items.Count(i => i.Kind == "user") == 0) beam.Items.Add(new ChatItem { Kind = "user", Text = "Make the front beam 1800 mm deep" });
        beam.Items.Add(new ChatItem { Kind = "user", Text = "also add the corner beam" });
        var c1 = b3.StartReview(beam.Number, now.AddMinutes(2), "make the front beam 1800 mm")!;
        check("waiting: the check knows which jobs worked at the same time and that their changes aren't this job's",
            c1.Task.Contains($"job {pipes.Number} \"Pipes\"") && c1.Task.Contains(JobReview.WaitingWord) && c1.Task.Contains("never NOT DONE"), c1.Task);
        check("waiting: the check has what the job was asked later", c1.Task.Contains("also add the corner beam"), c1.Task);
        check("waiting: a part needing the user's yes is WAITING, not NOT DONE", c1.Task.Contains("needs the user's decision or yes"), c1.Task);
        b3.ToStart(3, now);
        b3.RunEnded(c1, true, "1. Beam 1800 mm: DONE\n2. Corner beam: DONE\n3. +1.30 to +1.40 step: WAITING - needs the user's yes\n4. Pile joins: WAITING - job 2 made them\n" +
            "WAITING: 3. the +1.30 to +1.40 step needs the user's yes; 4. pile joins made by job 2\nPASSED", now.AddMinutes(3));
        b3.ReviewEnded(c1, now.AddMinutes(3));
        check("waiting: it passes and keeps what waits", JobReview.Passed(beam.Review) && beam.WaitingOn.Contains("+1.30 to +1.40") && !JobReview.SendBack(beam), beam.Review + " | " + beam.WaitingOn);
        var r3 = JobBoard.Report([beam], 0);
        check("waiting: the coordinator asks the user, doesn't send the job back", r3.Contains("it passed") && r3.Contains("+1.30 to +1.40") && r3.Contains("don't send the job back"), r3);
        check("waiting: a check without a WAITING line has none", JobReview.WaitingOf(new ChatJob { State = JobBoard.Done, Summary = "PASSED" }) == ""
            && JobReview.WaitingOf(new ChatJob { State = JobBoard.Done, Summary = "WAITING: none\nPASSED" }) == "", "");
        var sentBack = new ChatJob { State = JobBoard.Done, Review = "ISSUES: 2. corner beam missing", WaitingOn = "3. the step needs the user's yes", Reviews = 1 };
        check("waiting: a job sent back for real parts is told to leave the waiting ones", JobReview.SendBack(sentBack) && JobReview.FixMessage(sentBack).Contains("Leave the parts that wait"), JobReview.FixMessage(sentBack));
        check("waiting: the check's send-back and Continue aren't counted as later asks",
            JobReview.LaterAsks(new ChatJob { Items = [new() { Kind = "user", Text = "task" }, new() { Kind = "user", Text = "A teammate checked your work in the model, part by part" }, new() { Kind = "user", Text = JobPause.CarryOn }] }) == "", "");

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
