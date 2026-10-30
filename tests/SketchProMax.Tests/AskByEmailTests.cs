using SketchProMax.Chat;

/// <summary>The autopilot's questions by email: gathering, the email, telling replies apart, and reading answers.</summary>
internal static class AskByEmailTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var t0 = new DateTime(2026, 10, 30, 13, 0, 0, DateTimeKind.Utc);
        EmailQuestion Q(int id, string asker, string question, string assumed, int secondsAgo = 40) =>
            new() { Id = id, Asker = asker, Question = question, Assumed = assumed, Asked = t0.AddSeconds(-secondsAgo) };
        var q1 = Q(1, "Job 24: Existing model", "No as-built for Berths 7-10. Copy the Berth 11 section, or wait for drawings?", "copy the Berth 11 section");
        var q2 = Q(2, "Job 25: PVC pipes", "PVC pipes: 4 lengths or 3?", "4 lengths", secondsAgo: 5);

        // Gathering: a question waits 30 s for others, then all waiting go together.
        check("email: a question waits a moment for others", AskByEmail.Due([Q(3, "x", "q", "a", secondsAgo: 10)], t0).Count == 0, "");
        var due = AskByEmail.Due([q1, q2], t0);
        check("email: once the oldest waited, every one waiting goes in one email", due.Count == 2, due.Count.ToString());
        check("email: a sent or answered question isn't sent again", AskByEmail.Due([new EmailQuestion { Id = 4, Asked = t0.AddMinutes(-5), Sent = t0 }], t0).Count == 0, "");

        // The email: tagged subject with a token; the body has each question, who asks, the assumption and until when.
        var token = AskByEmail.NewToken(new Random(1));
        var subject = AskByEmail.Subject(due, token);
        check("email: the subject starts with the tag and carries the token", subject.StartsWith(AskByEmail.SubjectTag) && subject.EndsWith("#" + token) && subject.Contains("Q1, Q2"), subject);
        check("email: one question names it in the subject", AskByEmail.Subject([q1], token).Contains("Q1: No as-built"), "");
        var body = AskByEmail.Body(due, "Berths 7-11 package", new DateTime(2026, 10, 30, 15, 0, 0), 10);
        check("email: the body asks each question with who asks and what it assumes until when", body.Contains("Q1 (Job 24: Existing model)") && body.Contains("by 15:10")
            && body.Contains("copy the Berth 11 section") && body.Contains("Q2 (Job 25"), body);
        check("email: the body says how to reply, that ok keeps it and a late reply counts", body.Contains("\"Q1: your answer\"") && body.Contains("\"ok\" keeps") && body.Contains("later reply still counts"), body);

        // Replies: told apart from the question email (it reaches the same Inbox), by the token.
        check("email: a reply carries the token back", AskByEmail.IsReply("RE: " + subject, token) && AskByEmail.IsReply("Re: " + subject, token) && AskByEmail.IsReply("AW: " + subject, token), "");
        check("email: the question itself is not a reply", !AskByEmail.IsReply(subject, token), "");
        check("email: another email's token is not this reply", !AskByEmail.IsReply("RE: [Sketch ProMax] Q9: x #0ABC", token == "0ABC" ? "0ABD" : token), "");
        check("email: the token is read from the subject", AskByEmail.TokenOf("RE: " + subject) == token && AskByEmail.TokenOf("RE: hello") is null, "");

        // Answers: the reply's own text, without the quoted email.
        var outlook = "Q1: copy berth 11\r\nQ2: 3 lengths only\r\n\r\n________________________________\r\nFrom: Me\r\nSent: Thursday\r\nThe autopilot needs your answers.";
        var text = AskByEmail.NewText(outlook);
        check("email: the quoted email under the reply is cut", text == "Q1: copy berth 11\nQ2: 3 lengths only", text);
        check("email: a phone's 'On … wrote:' and > lines are cut", AskByEmail.NewText("use 1400\n\nOn Thu, 30 Oct 2026 at 15:00, Me <me@x.com> wrote:\n> Q1") == "use 1400", "");
        var answers = AskByEmail.Answers(text, due);
        check("email: numbered lines answer their questions", answers[1] == "copy berth 11" && answers[2] == "3 lengths only", string.Join("; ", answers));
        check("email: '2: …' and '2 - …' work too", AskByEmail.Answers("1 - wait\n2: four", due) is var a2 && a2[1] == "wait" && a2[2] == "four", "");
        check("email: one question, the whole reply answers it", AskByEmail.Answers("use 1400 mm piles", [q1])[1] == "use 1400 mm piles", "");
        check("email: a number in the answer isn't read as a question's", AskByEmail.Answers("3.5 m spacing", [Q(3, "x", "spacing?", "4 m")])[3] == "3.5 m spacing", "");
        check("email: ok keeps the assumption, said in full", AskByEmail.Answers("ok", [q1])[1].Contains("Keep your assumption (copy the Berth 11 section)"), "");
        check("email: unnumbered text with several questions answers each", AskByEmail.Answers("do whatever you assumed, thanks", due).Count == 2, "");
        check("email: an empty reply answers nothing", AskByEmail.Answers("  ", due).Count == 0, "");

        // What the job is told.
        q1.Answer = "copy berth 11";
        check("email: the answer line says go by it", AskByEmail.AnswerLine(q1).Contains("\"copy berth 11\"") && AskByEmail.AnswerLine(q1).Contains("Go by it"), "");
        check("email: no reply in time: carry on with the assumption and list it", AskByEmail.NoReplyLine(q2, 10).Contains("No reply to Q2 in 10 minutes") && AskByEmail.NoReplyLine(q2, 10).Contains("4 lengths"), "");
        check("email: a late answer says to put right what was done on the assumption", AskByEmail.LateLine(q1).Contains("put right") && AskByEmail.LateLine(q1).Contains("copy the Berth 11 section"), "");
        check("email: 10 minutes by default, and never is a choice", AskByEmail.DefaultWaitMinutes == 10 && AskByEmail.WaitChoices.Contains(0), "");

        // Progress emails: every hour by default; what finished, what works, and a reply goes to the coordinator.
        check("progress: due after the interval, never when off", AskByEmail.UpdateDue(t0.AddMinutes(-61), t0, 60) && !AskByEmail.UpdateDue(t0.AddMinutes(-30), t0, 60)
            && !AskByEmail.UpdateDue(t0.AddDays(-1), t0, 0) && AskByEmail.DefaultUpdateMinutes == 60, "");
        var prun = new AutopilotRun { Goal = "Berths 7-11 package", State = AutopilotRun.Running, Started = t0.AddHours(-3), Gaps = "Modelling:\n- PVC pipes: none" };
        List<ChatJob> pjobs =
        [
            new() { Number = 1, Title = "Read PDF", State = JobBoard.Done, Ended = t0.AddHours(-2), Summary = "Template read." },
            new() { Number = 2, Title = "Existing model", State = JobBoard.Done, Ended = t0.AddMinutes(-20), Summary = "Gravity wall built for Berths 7-10." },
            new() { Number = 3, Title = "PVC pipes", State = JobBoard.Running },
            new() { Number = 4, Title = "Sheets", State = JobBoard.Queued },
        ];
        var pbody = AskByEmail.UpdateBody(prun, pjobs, t0.AddHours(-1), "Handing out the PVC pipes next.", t0);
        check("progress: says how long and what finished since the last one only", pbody.Contains("Working for 3 h 0 min") && pbody.Contains("Job 2 Existing model: Gravity wall")
            && !pbody.Contains("Template read"), pbody);
        check("progress: says what works now and waits", pbody.Contains("Job 3 PVC pipes") && pbody.Contains("1 more waiting to start"), pbody);
        check("progress: says what isn't done yet and the coordinator's latest words", pbody.Contains("PVC pipes: none") && pbody.Contains("Handing out the PVC pipes next."), pbody);
        check("progress: says a reply goes to the coordinator", pbody.Contains("Reply to this email with anything new"), pbody);
        var sub = AskByEmail.UpdateSubject(new DateTime(2026, 10, 30, 16, 0, 0), "BEEF");
        check("progress: tagged subject with the time and a token for the reply", sub == "[Sketch ProMax] Progress at 16:00 #BEEF" && AskByEmail.IsReply("RE: " + sub, "BEEF"), sub);
        check("progress: a reply reaches the coordinator as the user's message", AskByEmail.FromEmail("also add the fender schedule").Contains("by email from their phone")
            && AskByEmail.FromEmail("also add the fender schedule").Contains("also add the fender schedule"), "");

        // The rules tell the coordinator and the jobs when they may email the user.
        var run = new AutopilotRun { Goal = "g", State = AutopilotRun.Running };
        check("email: the coordinator may email only a choice that changes the result a lot", Autopilot.Approved(run).Contains("ask_user") && Autopilot.Approved(run).Contains("change the result a lot"), "");
        check("email: a job is told to ask by email, carry on, and fix after a late reply", Autopilot.JobRules.Contains("ask_user") && Autopilot.JobRules.Contains("put the work right"), "");
        check("email: the report lists the questions emailed", Autopilot.Report(new AutopilotRun { State = AutopilotRun.Done, Log = [new AutopilotEntry { Kind = AutopilotEntry.Asked, Text = "Emailed you Q1 (x)." }] }, [], t0)
            .Contains("Questions emailed to you** (1)"), "");
    }
}
