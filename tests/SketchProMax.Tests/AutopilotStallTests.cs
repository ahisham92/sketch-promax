using SketchProMax.Chat;

/// <summary>
/// An Autopilot run whose answer got stuck after the connection dropped (bug-log row 67): the stuck answer and jobs are
/// found, a typed "stop" stops the run, and a run whose model was closed pauses. Bug 68: every chat's answer is watched
/// the same way, and messages typed meanwhile always go next.
/// </summary>
internal static class AutopilotStallTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var t0 = new DateTime(2026, 11, 1, 18, 38, 0, DateTimeKind.Utc);

        // A stuck answer: quiet too long, or quiet since the connection came back.
        check("stall: an answer that spoke a minute ago is fine", !Autopilot.Stalled(t0, t0.AddMinutes(1), false, default), "");
        check("stall: no word for the stuck time while writing is stuck", Autopilot.Stalled(t0, t0 + Autopilot.TurnStuck, false, default), "");
        check("stall: a step running in Revit gets longer", !Autopilot.Stalled(t0, t0 + Autopilot.TurnStuck, true, default)
                                                       && Autopilot.Stalled(t0, t0 + Autopilot.ToolStuck, true, default), "");
        var back = t0.AddMinutes(2);
        check("stall: nothing since the connection came back, a few minutes on, is stuck", Autopilot.Stalled(t0, back + Autopilot.AfterReconnect, false, back), "");
        check("stall: right after the connection came back it still gets its moment", !Autopilot.Stalled(t0, back.AddMinutes(1), false, back), "");
        check("stall: an answer that spoke after the connection came back is fine", !Autopilot.Stalled(back.AddMinutes(1), back + Autopilot.AfterReconnect, false, back), "");
        check("stall: the stuck times are minutes, not hours", Autopilot.TurnStuck <= TimeSpan.FromMinutes(15) && Autopilot.AfterReconnect <= TimeSpan.FromMinutes(5) && Autopilot.MaxStalls >= 2, "");
        var note = Autopilot.StalledNote(TimeSpan.FromMinutes(12));
        check("stall: the note says how long and that it carries on", note.Contains("12 minutes") && note.Contains("carries on") && !note.Contains("Claude"), note);

        // Jobs silent since the connection came back.
        ChatJob J(int n, string state, DateTime last, int goalCheck = 0, string activity = "", string kind = "assistant", string itemState = "") => new()
        {
            Number = n, State = state, Started = t0, GoalCheck = goalCheck, Activity = activity,
            Items = [new ChatItem { Kind = kind, State = itemState, Time = last }],
        };
        var now = back + Autopilot.AfterReconnect;
        check("stall: a running job silent since the connection came back is restarted", Autopilot.JobStalled(J(5, JobBoard.Running, t0.AddMinutes(1)), 2, back, now), "");
        check("stall: one that spoke since is left alone", !Autopilot.JobStalled(J(5, JobBoard.Running, back.AddMinutes(1)), 2, back, now), "");
        check("stall: no drop, no restart", !Autopilot.JobStalled(J(5, JobBoard.Running, t0), 2, default, now), "");
        check("stall: not a reviewer, nor a job from before the run", !Autopilot.JobStalled(J(5, JobBoard.Running, t0, goalCheck: 1), 2, back, now)
                                                                     && !Autopilot.JobStalled(J(2, JobBoard.Running, t0), 2, back, now), "");
        check("stall: not one whose step runs in Revit or waits its turn there", !Autopilot.JobStalled(J(5, JobBoard.Running, t0, kind: "tool", itemState: "running"), 2, back, now)
                                                                               && !Autopilot.JobStalled(J(5, JobBoard.Running, t0, activity: JobBoard.Applying), 2, back, now), "");
        check("stall: not before the few minutes are up", !Autopilot.JobStalled(J(5, JobBoard.Running, t0), 2, back, back.AddMinutes(1)), "");

        // "stop" typed to a running run.
        foreach (var said in new[] { "stop", "Stop!", "stop for now this chat", "please stop the autopilot", "STOP ALL JOBS NOW", "ok stop everything", "cancel the run", "stop working and wait" })
            check($"stop: \"{said}\" stops the run", Autopilot.SaysStop(said), said);
        foreach (var said in new[] { "stop adding doors to level 2", "don't stop", "", "carry on", "please continue, stop asking me", "why did it stop?", "stop using the old title block for the sheets" })
            check($"stop: \"{said}\" is not a stop", !Autopilot.SaysStop(said), said);

        // The run's model open in this Revit.
        check("model: open among the open models (any case)", Autopilot.ModelOpen(@"\\server\models\tower.rvt", [@"C:\other.rvt", @"\\SERVER\models\Tower.rvt"]), "");
        check("model: not open when only others are", !Autopilot.ModelOpen(@"\\server\models\tower.rvt", [@"C:\other.rvt"]), "");
        check("model: a run with no model counts as open", Autopilot.ModelOpen("", []), "");
        // Bug 68: every chat's answer is watched, not only a running Autopilot's.
        var ended = Autopilot.AnswerStalledNote(TimeSpan.FromMinutes(10.2));
        check("any chat: the stuck answer's note says how long and why", ended == "The answer showed nothing for 10 minutes (the connection may have dropped), so it was ended.", ended);
        check("any chat: a running Autopilot watches its own coordinator", Autopilot.RunWatchesAnswer(new AutopilotRun { State = AutopilotRun.Running }), "");
        check("any chat: no run, or one planning, waiting for approval or paused, gets the plain watch",
            !Autopilot.RunWatchesAnswer(null) && !Autopilot.RunWatchesAnswer(new AutopilotRun { State = AutopilotRun.Planning })
            && !Autopilot.RunWatchesAnswer(new AutopilotRun { State = AutopilotRun.Approval }) && !Autopilot.RunWatchesAnswer(new AutopilotRun { State = AutopilotRun.Paused }), "");
        check("queued: messages typed meanwhile go next however the answer ended", ChatWork.LeftoverGoes(hold: false, closing: false, ready: true), "");
        check("queued: kept back only for a model switch, a closing pane or a chat not ready",
            !ChatWork.LeftoverGoes(true, false, true) && !ChatWork.LeftoverGoes(false, true, true) && !ChatWork.LeftoverGoes(false, false, false), "");

        var gone = Autopilot.ModelGoneNote("Tower");
        check("model: the pause note names the model and says how to carry on", gone.Contains("Tower") && gone.Contains("Resume") && gone.Contains("free") && !gone.Contains("Claude"), gone);
    }

    /// <summary>The pane wires them in: Stop ends the coordinator's answer too, the round button and typed "stop" stop the run.</summary>
    public static void RunSources(string root, Action<string, bool, string> check)
    {
        string Read(string file) => File.ReadAllText(Path.Combine(root, "src", "SketchProMax", "Chat", file)).Replace("\r\n", "\n");
        var auto = Read("ChatPane.Autopilot.cs");
        var pane = Read("ChatPane.cs");
        var stop = auto.Substring(auto.IndexOf("private void AutopilotStop(", StringComparison.Ordinal));
        stop = stop.Substring(0, stop.IndexOf("\n    }", StringComparison.Ordinal));
        check("stop source: the banner's Stop ends the coordinator's answer too", stop.Contains("AbortTurn(") && stop.Contains("StopJob("), stop);
        check("stop source: the round stop button stops a running autopilot", pane.Contains("_stop.Click += (_, _) => StopPressed();"), "");
        check("stop source: \"stop\" typed is checked before it is queued after this item", pane.IndexOf("Autopilot.SaysStop(", StringComparison.Ordinal) is var at and > 0
            && at < pane.IndexOf("QueueNote(note);", StringComparison.Ordinal), "");
        check("stall source: the 30 s tick checks for a stuck answer and the run's model", auto.Contains("AutopilotStallCheck(run);") && auto.Contains("AutopilotModelCheck(run);"), "");
        check("stall source: the connection coming back is remembered for the stuck check", auto.Contains("_reconnectedAt = DateTime.UtcNow;"), "");
        var job = Read("ChatPane.Job.cs");
        check("stall source (bug 68): every chat's answer is watched, shown or in the background", pane.Contains("_idle.Tick += (_, _) => StallCheckAll();")
            && auto.Contains("foreach (var s in Sessions.ToList()) In(s, AnswerStallCheck);"), "");
        check("stall source (bug 68): a stuck answer is ended and its process killed", auto.Contains("AbortTurn(text);") && auto.Contains("_engine?.Kill();"), "");
        check("stall source (bug 68): a stop no longer holds back the messages typed meanwhile", pane.Contains("SendLeftover(left, hold: !Hidden && _pendingModel is not null, stopped);")
            && job.Contains("ChatWork.LeftoverGoes(hold, _stopped, _setup == Setup.Ready)"), "");
        var exited = pane.Substring(pane.IndexOf("private void OnExited(", StringComparison.Ordinal));
        exited = exited.Substring(0, exited.IndexOf("\n    }", StringComparison.Ordinal));
        check("stall source (bug 68): a crashed answer is signed-out-checked before the messages typed meanwhile go", exited.IndexOf("_setup = Setup.SignedOut;", StringComparison.Ordinal) < exited.LastIndexOf("EndTurn();", StringComparison.Ordinal), "");
        var bridge = File.ReadAllText(Path.Combine(root, "src", "SketchProMax", "Bridge", "BridgeServer.cs")).Replace("\r\n", "\n");
        check("review source (bug 68): a job's model change is marked, so only changed work is checked", bridge.Contains("if (tool.ModifiesModel && fromChat && jobKey is not null) Chat.ChatJobs.ChangesModel(jobKey);"), "");
    }
}
