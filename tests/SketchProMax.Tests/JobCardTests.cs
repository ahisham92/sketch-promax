using SketchProMax.Bridge;
using SketchProMax.Chat;

/// <summary>The live job card's pure rules: progress and time-left arithmetic, the control state machine, the feed, mid-task messages, thinking in stream-json, and the card model.</summary>
internal static class JobCardTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Progress and time left
        check("job: percent", JobMath.Percent(148, 400) == 37 && JobMath.Percent(0, 0) == 0 && JobMath.Percent(401, 400) == 100 && JobMath.Percent(399, 400) == 99, "");
        check("job: count line", JobMath.Count(148, 400) == "37% (148 of 400 items)" && JobMath.Count(3, 4, "sheets") == "75% (3 of 4 sheets)", JobMath.Count(148, 400));
        check("job: remaining from the item rate", JobMath.Remaining(TimeSpan.FromSeconds(10), 5, 15) == TimeSpan.FromSeconds(30), "");
        check("job: remaining unknown too early or when done",
            JobMath.Remaining(TimeSpan.FromSeconds(10), 1, 15) is null && JobMath.Remaining(TimeSpan.FromMilliseconds(500), 5, 15) is null
            && JobMath.Remaining(TimeSpan.FromSeconds(10), 5, 0) is null, "");
        check("job: time left wording",
            JobMath.Left(null) == "" && JobMath.Left(TimeSpan.FromSeconds(4)) == "a few seconds left" && JobMath.Left(TimeSpan.FromSeconds(42)) == "about 40 s left"
            && JobMath.Left(TimeSpan.FromMinutes(2.6)) == "about 3 min left" && JobMath.Left(TimeSpan.FromMinutes(80)) == "about 1 h 20 min left",
            JobMath.Left(TimeSpan.FromSeconds(42)));
        check("job: durations", JobMath.Duration(TimeSpan.FromSeconds(45)) == "45 s" && JobMath.Duration(TimeSpan.FromSeconds(130)) == "2 min 10 s"
                                && JobMath.Duration(TimeSpan.FromMinutes(2)) == "2 min" && JobMath.Duration(TimeSpan.FromMinutes(65)) == "1 h 5 min"
                                && JobMath.Duration(TimeSpan.FromSeconds(-3)) == "0 s", JobMath.Duration(TimeSpan.FromSeconds(130)));
        check("job: tool titles", JobMath.Title("tag_elements_in_view") == "Tag elements in view" && JobMath.Title("") == "", "");

        // ---- The feed
        var t0 = new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
        var longs = 0;
        JobFeed.Reset();
        JobFeed.LongStep = () => longs++;
        try
        {
            JobFeed.StepStarted("fill_title_blocks", t0);
            JobFeed.Item("Fill title blocks", 0, 10, "A-101", t0);
            JobFeed.Item("Fill title blocks", 4, 10, "A-105", t0.AddSeconds(4));
            var snap = JobFeed.Now;
            var line = JobMath.Line(snap, t0.AddSeconds(4));
            check("job: feed counts items and times the step", snap is { Done: 4, Total: 10, Label: "A-105", Running: true } && line == "40% (4 of 10 items) · 4 s · a few seconds left", line);
            check("job: rate from the first item", JobMath.Remaining(snap, t0.AddSeconds(4)) == TimeSpan.FromSeconds(6), "");
            check("job: a long step opens the job window once", longs == 1, longs.ToString());
            JobFeed.Item("Fill title blocks", 5, 10, "A-106", t0.AddSeconds(5));
            check("job: ...and only once per step", longs == 1, longs.ToString());
            JobFeed.Item("Restore numbers", 0, 3, "A-101", t0.AddSeconds(6));
            check("job: a new batch starts a new count", JobFeed.Now is { What: "Restore numbers", Done: 0, Total: 3, DoneAtFirst: 0 } && JobFeed.Now.FirstItem == t0.AddSeconds(6), "");
            JobFeed.SetPaused(true);
            check("job: paused shows in the line", JobMath.Line(JobFeed.Now, t0.AddSeconds(7)).EndsWith("paused"), JobMath.Line(JobFeed.Now, t0.AddSeconds(7)));
            JobFeed.StepEnded("fill_title_blocks", "done", "", t0.AddSeconds(8));
            var events = JobFeed.Drain();
            check("job: step events in order", events.Select(e => e.State).SequenceEqual(["running", "done"]) && !JobFeed.Now.Running && !JobFeed.Now.Paused && JobFeed.Drain().Count == 0,
                string.Join(",", events.Select(e => e.State)));
        }
        finally { JobFeed.LongStep = null; JobFeed.Reset(); }

        // ---- The control state machine
        var all = Enum.GetValues(typeof(JobState)).Cast<JobState>().ToList();
        var actions = Enum.GetValues(typeof(JobAction)).Cast<JobAction>().ToList();
        check("job: nothing applies once finished", actions.All(a => !JobControl.Can(JobState.Finished, a)), "");
        check("job: transitions",
            JobControl.Next(JobState.Running, JobAction.Pause) == JobState.PauseRequested && JobControl.Next(JobState.Paused, JobAction.Resume) == JobState.Running
            && JobControl.Next(JobState.PauseRequested, JobAction.Resume) == JobState.Running && JobControl.Next(JobState.Paused, JobAction.Skip) == JobState.Skipping
            && JobControl.Next(JobState.Skipping, JobAction.Stop) == JobState.Stopping && JobControl.Next(JobState.Stopping, JobAction.Resume) == JobState.Stopping
            && JobControl.Next(JobState.Stopping, JobAction.Skip) == JobState.Stopping && JobControl.Next(JobState.Running, JobAction.Resume) == JobState.Running, "");
        check("job: every pair is consistent (Next changes the state exactly when Can)",
            all.All(s => actions.All(a => JobControl.Can(s, a) == (JobControl.Next(s, a) != s))), "");
        var c = new JobControl();
        var changes = new List<JobState>();
        c.Changed += changes.Add;
        check("job: pause waits for a boundary", c.Apply(JobAction.Pause) && c.State == JobState.PauseRequested && !c.Apply(JobAction.Pause), c.State.ToString());
        check("job: the boundary enters the pause", c.EnterPause() && c.State == JobState.Paused && !c.EnterPause(), c.State.ToString());
        var waiter = Task.Run(() => c.WaitWhilePaused(5000));
        Thread.Sleep(50);
        c.Apply(JobAction.Resume);
        check("job: Resume wakes a waiting batch", waiter.Wait(2000) && waiter.Result == JobState.Running, waiter.IsCompleted ? waiter.Result.ToString() : "still waiting");
        c.Apply(JobAction.Skip);
        check("job: a skip is used up by the step it ended", c.Halting && c.StepEnded() == JobState.Skipping && c.State == JobState.Running, c.State.ToString());
        c.Apply(JobAction.Stop);
        check("job: a stop is not used up", c.StepEnded() == JobState.Stopping && c.State == JobState.Stopping && !c.Apply(JobAction.Pause), c.State.ToString());
        c.Finish();
        check("job: finishing a stopped task keeps the stop (buttons do nothing)", c.State == JobState.Stopping && c.Over && !c.Apply(JobAction.Stop) && !c.Apply(JobAction.Resume) && c.Halting, c.State.ToString());
        var f = new JobControl();
        f.Changed += changes.Add;
        f.Finish();
        check("job: finished ends everything", f.State == JobState.Finished && f.Over && !f.Apply(JobAction.Stop) && !f.Halting, "");
        check("job: Changed is raised for each change", changes.SequenceEqual([JobState.PauseRequested, JobState.Paused, JobState.Running, JobState.Skipping, JobState.Running, JobState.Stopping, JobState.Finished]),
            string.Join(",", changes));
        var p = new JobControl();
        p.Apply(JobAction.Pause);
        p.EnterPause();
        check("job: a wait that times out returns still paused", p.WaitWhilePaused(30) == JobState.Paused, "");
        var asyncWait = p.WaitWhilePausedAsync(5000, CancellationToken.None);
        p.Apply(JobAction.Stop);
        check("job: the bridge's wait ends on Stop", asyncWait.Wait(2000) && asyncWait.Result == JobState.Stopping, "");

        // ---- At a boundary
        var keep = JobControl.Tool;
        try
        {
            JobControl.Tool = null;
            JobControl.Current = new JobControl();
            JobControl.Current.Apply(JobAction.Stop);
            check("job: the chat's control never reaches a call from elsewhere", JobControl.AtBoundary("x", 0) == JobState.Running && !JobControl.StopNow("x", 0), "");
            JobControl.Current = null;
            var b = JobControl.Tool = new JobControl();
            check("job: no control, nothing to do", JobControl.AtBoundary("x", 0) == JobState.Running, "");
            b.Apply(JobAction.Pause);
            JobState? during = null;
            JobControl.TestWait = (ctl, _) => { during = ctl.State; ctl.Apply(JobAction.Skip); };
            check("job: a pause waits at the boundary, and Skip from the wait halts the batch", JobControl.AtBoundary("x", 3) == JobState.Skipping && during == JobState.Paused, during?.ToString() ?? "no wait");
            JobControl.TestWait = (_, _) => { };
            b.StepEnded();
            b.Apply(JobAction.Pause);
            check("job: a wait that gives up resumes", JobControl.AtBoundary("x", 4) == JobState.Running, b.State.ToString());
            JobControl.TestWait = null;
            JobControl.WaitAtBoundary = null;
            b.Apply(JobAction.Pause);
            check("job: with nothing to wait on, a pause is let go", JobControl.AtBoundary("x", 5) == JobState.Running, b.State.ToString());
            b.Apply(JobAction.Stop);
            check("job: Stop after an item's edit rolls it back", JobControl.StopNow("x", 6), "");
            check("job: not-done reasons", JobControl.NotDone(JobState.Skipping).Contains("skipped") && JobControl.NotDone(JobState.Stopping).Contains("stopped"), "");
        }
        finally
        {
            JobControl.Tool = keep;
            JobControl.Current = null;
            JobControl.TestWait = null;
        }

        // ---- The end of a task
        var ended = new JobControl();
        ended.Apply(JobAction.Stop);
        ended.Finish();
        check("job: a Stop pressed before the task ended still stops a call that outlived it", ended.State == JobState.Stopping && ended.Halting && ended.Over, ended.State.ToString());
        var quiet = new JobControl();
        quiet.Apply(JobAction.Pause);
        quiet.Finish();
        check("job: otherwise the end finishes it and buttons do nothing", quiet.State == JobState.Finished && !quiet.Apply(JobAction.Resume) && !quiet.Apply(JobAction.Stop), quiet.State.ToString());

        // ---- After a step: Skip, Stop and the user's messages
        JobNotes.Take();
        var skipping = new JobControl();
        skipping.Apply(JobAction.Skip);
        var skipped = JobControl.AfterStep(skipping, "create_sheets", "{\"done\":5}", isError: true, out var skipState);
        check("job: a skipped step says so first, as JSON when the result is, and is used up",
            skipState == "skipped" && skipped == "{\"user_action\":\"" + JobControl.SkippedText + "\",\"done\":5}" && skipping.State == JobState.Running, skipped);
        var stopping = new JobControl();
        stopping.Apply(JobAction.Stop);
        var stopped = JobControl.AfterStep(stopping, "create_sheets", "ok", false, out var stopState);
        check("job: a stopped step says so first", stopState == "stopped" && stopped == JobControl.StoppedText + "\n\nok", stopped);
        var scan = new JobControl();
        scan.Apply(JobAction.Stop);
        var scanText = JobControl.AfterStep(scan, "check_model", JobControl.EndedEarly("Stopped on request (cancel_job). No report was written."), true, out var scanState);
        check("job: a step that can only finish whole says it kept nothing, never 'finished items are kept'",
            scanState == "stopped" && scanText.StartsWith(JobControl.StoppedNothingKeptText) && !scanText.Contains("finished items are kept") && scanText.Contains("No report was written."), scanText);
        var scanSkip = new JobControl();
        scanSkip.Apply(JobAction.Skip);
        var skipScan = JobControl.AfterStep(scanSkip, "check_model", JobControl.EndedEarly(""), true, out _);
        check("card: whole-or-nothing steps show as rolled back / nothing kept",
            JobCardModel.Outcome(scanText, true) is ("stopped", var r1) && r1.Contains("nothing of it kept")
            && JobCardModel.Outcome(skipScan, false) is ("skipped", var r2) && r2.Contains("nothing of it was kept")
            && JobCardModel.Outcome(JobText.Add("{}", JobControl.SkippedText, null), false) == ("skipped", "skipped by you (Skip step)"), skipScan);
        var list = JobText.Add("[{\"id\":1}]", JobControl.StoppedText, "[Message from the user]\n- \"quoted\" and \\ slash");
        using (var parsed = System.Text.Json.JsonDocument.Parse(list))
            check("job: an array result is wrapped and stays JSON, the note readable", parsed.RootElement.GetProperty("result")[0].GetProperty("id").GetInt32() == 1
                && parsed.RootElement.GetProperty("message_from_user").GetString() == "[Message from the user]\n- \"quoted\" and \\ slash"
                && list.StartsWith("{\"user_action\":"), list);
        var twice = JobText.Add(JobText.Add("{\"a\":1}", null, "first"), null, "second");
        check("job: notes added twice keep one field", twice == "{\"a\":1,\"message_from_user\":\"first\\n\\nsecond\"}", twice);
        check("job: text that only looks like JSON is left as text", JobText.Add("{cut off…", "A", "B") == "A\n\n{cut off…\n\nB", "");
        JobControl.AfterStep(null, "t", "x", true, out var failState);
        JobControl.AfterStep(new JobControl(), "t", "x", false, out var doneState);
        check("job: plain steps are done or failed", failState == "failed" && doneState == "done", failState + doneState);
        var delivered = new List<(string, IReadOnlyList<string>)>();
        void Got(string tool, IReadOnlyList<string> ids) => delivered.Add((tool, ids));
        JobNotes.Delivered += Got;
        try
        {
            var id1 = JobNotes.Add("  use B- for the new sheets ");
            var id2 = JobNotes.Add("and skip the\nlegends");
            var text = JobControl.AfterStep(new JobControl(), "create_sheets", "{\"created\":3}", false, out _);
            using var json = System.Text.Json.JsonDocument.Parse(text);
            var note = json.RootElement.GetProperty("message_from_user").GetString() ?? "";
            check("job: messages reach the next step's result, once, as a marked field of its JSON",
                json.RootElement.GetProperty("created").GetInt32() == 3 && note.StartsWith("[Message from the user") && note.Contains("\n- use B- for the new sheets") && note.Contains("\n- and skip the\n  legends")
                && JobNotes.Count == 0 && JobControl.AfterStep(null, "x", "y", false, out _) == "y", text);
            JobNotes.Add("plain");
            var plain = JobControl.AfterStep(null, "x", "Done.", false, out _, notes: false);
            check("job: the bridge's step end leaves the messages for the reply", plain == "Done." && JobNotes.Count == 1 && JobNotes.Attach("x", "Done.").StartsWith("Done.\n\n[Message from the user"), plain);
            check("job: delivery is reported with the tool and ids", delivered.Count == 2 && delivered[0].Item1 == "create_sheets" && delivered[0].Item2.SequenceEqual([id1, id2]), "");
            check("job: note ids are unique", id1 != id2, "");
        }
        finally { JobNotes.Delivered -= Got; JobNotes.Take(); }

        // ---- Thinking in stream-json
        var parser = new StreamParser();
        var events2 = new List<ChatEvent>();
        foreach (var line in new[]
        {
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Check the levels."}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"abc"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":" Then tag."}}}""",
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"thinking","thinking":"Check the levels. Then tag.","signature":"abc"},{"type":"text","text":"Done"}]}}""",
        }) events2.AddRange(parser.ParseLine(line));
        var deltas = events2.OfType<ThinkingDeltaEvent>().Select(d => d.Text).ToList();
        check("thinking: streamed deltas, signatures ignored, the complete message not repeated",
            events2.OfType<ThinkingEvent>().Count() == 1 && deltas.SequenceEqual(["Check the levels.", " Then tag."]) && !events2.OfType<ThinkingTextEvent>().Any(),
            string.Join("|", events2.Select(e => e.GetType().Name)));
        var whole = new StreamParser().ParseLine("""{"type":"assistant","message":{"id":"m2","content":[{"type":"thinking","thinking":"Plan: sheets first."},{"type":"thinking","thinking":""},{"type":"redacted_thinking","data":"x"}]}}""");
        check("thinking: a complete message without streaming gives its text; empty and redacted blocks give none",
            whole.OfType<ThinkingTextEvent>().Select(t => t.Text).SequenceEqual(["Plan: sheets first."]), string.Join("|", whole.Select(e => e.GetType().Name)));
        var sub = new StreamParser().ParseLine("""{"type":"stream_event","parent_tool_use_id":"t1","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"sub"}}}""");
        check("thinking: a subagent's thinking is left out", sub.Count == 0, "");

        // ---- The card model
        var model = JobCardModel.FromPlan(["create_sheets", "fill_title_blocks", "create_sheets", ""], t0);
        check("card: planned steps in order, each once", model.Steps.Select(s => s.Tool).SequenceEqual(["create_sheets", "fill_title_blocks"]) && model.Steps.All(s => s.Planned && s.State == "waiting")
                                                         && model.Steps[1].Title == "Fill title blocks", "");
        check("card: when a brief gets a card", JobCardModel.Plans(["a_tool", "b_tool"], "change") && !JobCardModel.Plans(["a_tool", "get_x"], "change")
                                                && JobCardModel.Plans(["a_tool"], "multi_step") && !JobCardModel.Plans([], "multi_step"), "");
        check("card: quiet tools are not steps", model.Begin("wait_for_job") is null && model.Begin("get_model_context") is null && model.Steps.Count == 2, "");
        var first = model.Begin("create_sheets")!;
        check("card: a planned tool takes its step", first == model.Steps[0] && first.State == "running" && model.Running == first, "");
        var extra = model.Begin("tag_elements_in_view")!;
        check("card: an unplanned tool is added", !extra.Planned && model.Steps.Count == 3 && model.Steps[2] == extra, "");
        check("card: outcomes",
            JobCardModel.Outcome("{\"status\":\"still running in Revit\",\"job\":3}", false).State == "running"
            && JobCardModel.Outcome(JobControl.SkippedText + "\n\n{}", false).State == "skipped"
            && JobCardModel.Outcome("Job 3 (x) finished after 2 s:\n" + JobControl.StoppedText, true).State == "stopped"
            && JobCardModel.Outcome(JobControl.PausedText, true).State == "running"
            && JobCardModel.Outcome("\n  Revit refused it.\nmore", true) == ("failed", "Revit refused it.")
            && JobCardModel.Outcome("{}", false) == ("done", ""), JobCardModel.Outcome("\n  Revit refused it.\nmore", true).ToString());
        first.Total = 40;
        first.Done = 12;
        model.End(first, "done", "");
        model.End(extra, "failed", "Revit refused it.");
        check("card: headline counts finished steps", model.Headline() == "2 of 3 steps done" && first.Done == 40, model.Headline());
        model.Think("Step one.\n", newBlock: true);
        model.Think("**Next:** the title blocks", newBlock: false);
        check("card: thinking's one line is its latest line, without marks", JobCardModel.ThinkingLine(model.Thinking) == "Next: the title blocks", JobCardModel.ThinkingLine(model.Thinking));
        check("card: long thinking is cut to one line", JobCardModel.ThinkingLine(new string('x', 300), 50).Length == 50, "");
        model.Finish("done", t0.AddMinutes(3), "Done in 3 min. By hand: about 40 min. Saved: 37 min.", 12);
        check("card: unreached planned steps say why", model.Steps[1] is { State: "notrun" } && model.Steps[1].Reason.Contains("not needed"), model.Steps[1].Reason);
        check("card: summary line", model.FinalLine() == "Done in 3 min. By hand: about 40 min. Saved: 37 min. 12 elements changed.", model.FinalLine());
        var stoppedModel = new JobCardModel { Started = t0, Steps = [new JobStep { Tool = "a", State = "running" }] };
        stoppedModel.Finish("stopped", t0.AddSeconds(70), "", 0);
        check("card: a stopped task", stoppedModel.Steps[0].State == "stopped" && stoppedModel.FinalLine() == "Stopped after 1 min 10 s. Nothing in the model changed.", stoppedModel.FinalLine());
        var back = JobCardModel.FromJson(model.ToJson());
        check("card: saved and read back", back is not null && back.Steps.Count == 3 && back.Steps[2].Reason == "Revit refused it." && back.Summary == model.Summary
                                           && back.Changed == 12 && back.Thinking == model.Thinking && back.State == "done", "");
        check("card: unreadable saved card", JobCardModel.FromJson("{nope") is null && JobCardModel.FromJson("") is null, "");
    }
}
