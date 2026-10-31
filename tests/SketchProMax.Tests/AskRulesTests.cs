using SketchProMax.Chat;

/// <summary>The autopilot's questions on the user's phone: gathering, what the phone shows, answers, and progress updates.</summary>
internal static class AskRulesTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var t0 = new DateTime(2026, 10, 30, 13, 0, 0, DateTimeKind.Utc);
        UserQuestion Q(int id, string asker, string question, string assumed, int secondsAgo = 40) =>
            new() { Id = id, Asker = asker, Question = question, Assumed = assumed, Asked = t0.AddSeconds(-secondsAgo) };
        var q1 = Q(1, "Job 24: Existing model", "No as-built for Berths 7-10. Copy the Berth 11 section, or wait for drawings?", "copy the Berth 11 section");
        var q2 = Q(2, "Job 25: PVC pipes", "PVC pipes: 4 lengths or 3?", "4 lengths", secondsAgo: 5);

        // Gathering: a question waits 30 s for others, then all waiting go together.
        check("ask: a question waits a moment for others", AskRules.Due([Q(3, "x", "q", "a", secondsAgo: 10)], t0).Count == 0, "");
        var due = AskRules.Due([q1, q2], t0);
        check("ask: once the oldest waited, every one waiting goes", due.Count == 2, due.Count.ToString());
        check("ask: a sent or answered question isn't sent again", AskRules.Due([new UserQuestion { Id = 4, Asked = t0.AddMinutes(-5), Sent = t0 }], t0).Count == 0, "");

        // What the phone shows.
        check("ask: the phone shows the number, who asks and the question", AskRules.PhoneText(q1) == "Q1 (Job 24: Existing model): No as-built for Berths 7-10. Copy the Berth 11 section, or wait for drawings?", AskRules.PhoneText(q1));
        check("ask: the coordinator's question has no asker", AskRules.PhoneText(Q(5, "", " Go on? ", "yes")) == "Q5: Go on?", "");

        // Answers typed on the phone: ok keeps the assumption, said in full.
        check("ask: ok keeps the assumption, said in full", AskRules.Keep("ok", q1).Contains("Keep your assumption (copy the Berth 11 section)"), "");
        check("ask: a real answer is kept as typed", AskRules.Keep(" use 1400 mm piles ", q1) == "use 1400 mm piles", "");

        // What the job is told.
        q1.Answer = "copy berth 11";
        check("ask: the answer line says go by it", AskRules.AnswerLine(q1).Contains("\"copy berth 11\"") && AskRules.AnswerLine(q1).Contains("Go by it"), "");
        check("ask: no reply in time: carry on with the assumption and list it", AskRules.NoReplyLine(q2, 10).Contains("No reply to Q2 in 10 minutes") && AskRules.NoReplyLine(q2, 10).Contains("4 lengths"), "");
        check("ask: a late answer says to put right what was done on the assumption", AskRules.LateLine(q1).Contains("put right") && AskRules.LateLine(q1).Contains("copy the Berth 11 section"), "");
        check("ask: 10 minutes by default, and never is a choice", AskRules.DefaultWaitMinutes == 10 && AskRules.WaitChoices.Contains(0), "");

        // Progress updates: every hour by default, as short as can be.
        check("progress: due after the interval, never when off", AskRules.UpdateDue(t0.AddMinutes(-61), t0, 60) && !AskRules.UpdateDue(t0.AddMinutes(-30), t0, 60)
            && !AskRules.UpdateDue(t0.AddDays(-1), t0, 0) && AskRules.DefaultUpdateMinutes == 60, "");
        var prun = new AutopilotRun { Goal = "Berths 7-11 package", State = AutopilotRun.Running, Started = t0.AddHours(-3), Gaps = "Modelling:\n- PVC pipes: none" };
        List<ChatJob> pjobs =
        [
            new() { Number = 1, Title = "Read PDF", State = JobBoard.Done, Ended = t0.AddHours(-2), Summary = "Template read." },
            new() { Number = 2, Title = "Existing model", State = JobBoard.Done, Ended = t0.AddMinutes(-20), Summary = "Gravity wall built for Berths 7-10." },
            new() { Number = 3, Title = "PVC pipes", State = JobBoard.Running },
            new() { Number = 4, Title = "Sheets", State = JobBoard.Queued },
        ];
        var pbody = AskRules.UpdateBody(prun, pjobs, t0.AddHours(-1), [], t0);
        check("progress: short: finished in the last hour, left, working now", pbody.StartsWith("1 job finished in the last hour. 2 left: 1 working now, 1 waiting.\n"), pbody);
        check("progress: no finished jobs' titles, summaries or coordinator's words", !pbody.Contains("Gravity wall") && !pbody.Contains("Existing model") && !pbody.Contains("Berths 7-11"), pbody);
        check("progress: what each worker is on now", pbody.Contains("• Job 3: PVC pipes\n") && !pbody.Contains("Sheets"), pbody);
        pjobs[2].Worker = 0;
        pjobs[2].Now = "Placing pipes on Berth 8";
        var named = AskRules.UpdateBody(prun, pjobs, t0.AddHours(-1), [], t0, slot => slot == 0 ? "Ali" : "?");
        check("progress: the worker by name, with what they are doing now", named.Contains("• Ali: Placing pipes on Berth 8\n"), named);
        check("progress: one line for what the review team found open", pbody.EndsWith("The review team found 1 item not done yet."), pbody);
        check("progress: no questions, no question section", !pbody.Contains("input"), pbody);
        var asked = new List<UserQuestion> { new() { Id = 5, Question = "PVC pipes: 4 lengths or 3?", Sent = t0.AddMinutes(-5) }, new() { Id = 3, Question = "Copy the Berth 11 section?", Sent = t0.AddMinutes(-9) } };
        var qbody = AskRules.UpdateBody(prun, pjobs, t0.AddHours(-1), asked, t0);
        check("progress: the questions waiting for you, listed", qbody.EndsWith("We need your input on:\nQ3: Copy the Berth 11 section?\nQ5: PVC pipes: 4 lengths or 3?"), qbody);
        check("progress: the span reads plainly", AskRules.Span(TimeSpan.FromMinutes(30)) == "30 min" && AskRules.Span(TimeSpan.FromHours(2)) == "2 hours", "");
        var paused = new AutopilotRun { State = AutopilotRun.Paused, PauseWhy = "The 5-hour limit is nearly used up." };
        check("progress: a pause in one line", AskRules.UpdateBody(paused, [], t0.AddHours(-1), [], t0).Contains("Paused: The 5-hour limit is nearly used up."), "");
        check("phone voice: friendly, by the coordinator's own name, never \"another chat\"", AskRules.PhoneTurn("hi", "Pitso").Contains("from you, Pitso")
            && AskRules.PhoneTurn("hi", "Pitso").Contains("friendly") && AskRules.FromPhone("x", "Nova").Contains("Nova") && AskRules.PhoneVoice("").Contains("never \"another chat\""), "");
        check("progress: what the user writes reaches the coordinator as their message", AskRules.FromPhone("also add the fender schedule").Contains("from their phone")
            && AskRules.FromPhone("also add the fender schedule").Contains("also add the fender schedule"), "");

        // The rules tell the coordinator and the jobs when they may ask the user.
        var run = new AutopilotRun { Goal = "g", State = AutopilotRun.Running };
        check("ask: the coordinator may ask only a choice that changes the result a lot", Autopilot.Approved(run).Contains("ask_user") && Autopilot.Approved(run).Contains("change the result a lot"), "");
        check("ask: a job is told to ask, carry on, and fix after a late answer", Autopilot.JobRules.Contains("ask_user") && Autopilot.JobRules.Contains("put the work right"), "");
        check("ask: the report lists the questions sent", Autopilot.Report(new AutopilotRun { State = AutopilotRun.Done, Log = [new AutopilotEntry { Kind = AutopilotEntry.Asked, Text = "Sent Q1 to your phone (x)." }] }, [], t0)
            .Contains("Questions sent to your phone** (1)"), "");

        // Messages started from the phone: the chat's answer goes back, short.
        check("phone chat: the answer goes back as it is", AskRules.PhoneReply("Added 12 fenders to Berth 7.", "done") == "Added 12 fenders to Berth 7.", "");
        var longReply = AskRules.PhoneReply(new string('x', 4000), "done");
        check("phone chat: a long answer is cut, pointing to the PC", longReply.Length < AskRules.ReplyMax + 60 && longReply.EndsWith("(the rest is in the chat on the PC)"), longReply.Length.ToString());
        check("phone chat: a stop says so", AskRules.PhoneReply("", "stopped").Contains("stopped before it finished") && AskRules.PhoneReply("Half done.", "failed").Contains("Stopped before it finished"), "");
        check("phone chat: read every 30 s for 10 minutes after the user writes", AskRules.ChatReadEvery == TimeSpan.FromSeconds(30) && AskRules.ChatFor == TimeSpan.FromMinutes(10), "");

        // Reading the phone: often only while a job may be waiting.
        var waiting = new UserQuestion { Id = 7, Sent = t0.AddMinutes(-3) };
        var late = new UserQuestion { Id = 8, Sent = t0.AddMinutes(-30) };
        check("ask: the phone is read every 30 s while a job may wait on an answer", AskRules.ReadEvery([waiting, late], t0, 10) == TimeSpan.FromSeconds(30), "");
        check("ask: otherwise every minute (a message from the phone is taken in within a minute)", AskRules.ReadEvery([late], t0, 10) == TimeSpan.FromMinutes(1) && AskRules.ReadEvery([], t0, 10) == TimeSpan.FromMinutes(1), "");
    }
}
