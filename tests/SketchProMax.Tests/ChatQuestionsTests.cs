using SketchProMax.Chat;

/// <summary>The chat's Questions tab (Chat.ChatQuestions): numbered, answered by tap or "4 yes", answered ones leave. No Revit here.</summary>
internal static class ChatQuestionsTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 11, 19, 19, 10, 0, DateTimeKind.Utc);
        var saved = new List<UserAsk>();
        var qs = new ChatQuestions(saved);
        var q1 = qs.Ask(0, "Coordinator", "Shift the deck +1.30 to +1.40 by 100 mm landward?", ["Yes", "No, keep it", "Yes"], 0, now);
        var q2 = qs.Ask(51, "Job 51: 1800 mm beam", "Join the corner beam to the piles?", ["Join", "Leave"], 5, now);
        var q3 = qs.Ask(54, "Job 54: Pipes", "Which pipe size at SP1-SP4?", null, -1, now);
        check("questions: numbered 1, 2, 3 in the order asked, kept in the chat", q1.Number == 1 && q2.Number == 2 && q3.Number == 3 && saved.Count == 3, "");
        check("questions: repeated options dropped, a suggestion out of range ignored", q1.Options.Count == 2 && q1.Recommended == 0 && q2.Recommended == -1, string.Join("|", q1.Options));
        check("questions: the same open question asked again isn't shown twice", qs.Ask(0, "Coordinator", "shift the deck +1.30 to +1.40 by 100 mm landward", null, -1, now) == q1 && qs.OpenCount == 3, "");
        var longOption = "Keep the channel opening where it is: walls and base become 250, and the 400 mm gap to the existing slab is filled with mass concrete, joined flush";
        var q9 = new ChatQuestions(new List<UserAsk>()).Ask(0, "Coordinator", "How should the channel change?", [longOption, "Move it"], 0, now);
        check("questions: a long option is kept whole, never cut with an ellipsis (bug 124)", q9.Options[0] == longOption && ChatQuestions.MaxOption >= 400, q9.Options[0]);
        check("questions: the tab shows the open ones by number", string.Join(",", qs.Open().Select(q => q.Number)) == "1,2,3", "");

        qs.Answer(1, "Yes", now);
        check("questions: an answered question leaves the tab (the owner: 'once answered it is removed')", qs.OpenCount == 2 && !qs.Open().Contains(q1) && saved.Contains(q1), "");
        check("questions: an answered one can't be answered again from the tab", qs.Answer(1, "No", now) is null && q1.Answer == "Yes", "");

        // Typed in the message box.
        var typed = qs.Typed("2 b\nQ3: 150 mm");
        check("typed: '2 b' picks option B of Q2, 'Q3: 150 mm' answers Q3", typed.Count == 2 && typed[0].Ask == q2 && typed[0].Answer == "Leave" && typed[1].Ask == q3 && typed[1].Answer == "150 mm",
            string.Join(" | ", typed.Select(t => t.Ask.Number + "=" + t.Answer)));
        check("typed: '2 2' picks option 2, '2 join' matches the option", qs.Typed("2 2")[0].Answer == "Leave" && qs.Typed("2 join").Count == 1, "");
        check("typed: '2 yes' counts", qs.Typed("2 yes").Count == 1, "");
        check("typed: '2 piles are missing' is a message, not an answer", qs.Typed("2 piles are missing on berth 9").Count == 0, "");
        check("typed: '2: piles are missing' or 'Q2 piles…' counts", qs.Typed("2: piles are missing").Count == 1 && qs.Typed("Q2 piles are missing").Count == 1, "");
        check("typed: an answered or unknown number is a message", qs.Typed("1 no").Count == 0 && qs.Typed("9 yes").Count == 0, "");
        check("typed: a line that isn't an answer makes it all a message", qs.Typed("2 yes\nalso check the slabs").Count == 0, "");
        check("typed: plain text is a message", qs.Typed("make the beam 1800 mm").Count == 0, "");

        // Not understood: it stays, goes back to be written again, and shows anew.
        var unclear = qs.Answer(3, "I don't understand this", now)!;
        check("unclear: 'I don't understand' keeps it in the tab", unclear.Unclear && qs.Open().Contains(q3), "");
        check("unclear: '4 what do you mean' typed by number counts", qs.Typed("3 what do you mean").Count == 1, "");
        qs.Answer(2, "Join", now);
        check("answers: waiting to go", qs.AnswersWaiting, "");
        var sent = qs.TakeAnswers();
        var message = ChatQuestions.Message(sent);
        check("answers: go together, each with its question and who asked", sent.Count == 3 && message.Contains("Q1 (Coordinator") && message.Contains("Q2 (Job 51: 1800 mm beam") && message.Contains(": Join"), message);
        check("answers: a job's answer is passed to that job", message.Contains("pass it to job 51"), message);
        check("answers: a question not understood is rewritten with update", message.Contains("doesn't understand") && message.Contains("action=update number=3"), message);
        check("answers: taken once", !qs.AnswersWaiting && qs.TakeAnswers().Count == 0, "");
        var again = qs.Rewrite(3, "Pipe diameter at sheet piles SP1 to SP4: 150 or 200 mm?", ["150 mm", "200 mm"], 0, now.AddMinutes(1))!;
        check("rewrite: shows again, plain, with options, waiting for an answer", again.Open && !again.Unclear && again.Answer == "" && again.Options.Count == 2 && again.Rewritten != default && qs.OpenCount == 1, "");
        check("close: withdrawn questions leave the tab", qs.Close(3) && qs.OpenCount == 0 && !qs.Close(3), "");
        check("list: says when nothing is open", qs.Describe().StartsWith("No questions"), qs.Describe());
        check("numbers: never used again", qs.Ask(0, "Coordinator", "New one?", null, -1, now).Number == 4, "");
        check("unclear words", ChatQuestions.SaysUnclear("not clear") && ChatQuestions.SaysUnclear("?") && !ChatQuestions.SaysUnclear("yes, as you suggested"), "");

        // Questions left in a reply's text go in the tab too (19 Nov 2026: no tab showed for "(a) ... or (b) ...").
        var real = "I've restarted the four paused jobs:\n- **Job 55:** rebuilds the channels.\n- **Job 51:** sets the beam to 1850 mm.\n\n" +
                   "Only 3 jobs run at a time, so one of these starts when another finishes.\n\n" +
                   "The thickenings are still on hold until you choose: (a) lift all 15 whole by 500 mm, which moves their bottoms, or (b) keep the bottoms and fill the gap to the deck.";
        var caught = ChatQuestions.FromReply(real);
        check("reply: lettered choices become a question with its options", caught.Count == 1 && caught[0].Options.Count == 2
            && caught[0].Options[0] == "lift all 15 whole by 500 mm, which moves their bottoms" && caught[0].Options[1] == "keep the bottoms and fill the gap to the deck"
            && caught[0].Text.StartsWith("The thickenings are still on hold until you choose"), string.Join(" | ", caught.Select(c => c.Text + " => " + string.Join(" / ", c.Options))));
        var yn = ChatQuestions.FromReply("Done: 12 piles moved.\n\nShould I also tag them on the plan?");
        check("reply: a yes-or-no question gets Yes and No", yn.Count == 1 && yn[0].Text == "Should I also tag them on the plan?" && yn[0].Options.SequenceEqual(new[] { "Yes", "No" }), yn.Count + "");
        check("reply: an open question gets no options", ChatQuestions.FromReply("Which level should the slab sit on?")[0].Options.Count == 0, "");
        check("reply: no question, nothing", ChatQuestions.FromReply("All 15 thickenings are lifted and joined. Checked.").Count == 0, "");
        check("reply: a reply that points at the tab adds nothing", ChatQuestions.FromReply("Q4 in Questions. Should I go on?").Count == 0, "");
        check("reply: code is not read for questions", ChatQuestions.FromReply("Ran `x ? y : z` fine.").Count == 0, "");

        // The phone (21 Nov 2026: the owner switched the phone on and the coordinator's question never reached it).
        var pq = new ChatQuestions([]);
        var t = new DateTime(2026, 11, 21, 1, 30, 0, DateTimeKind.Utc);
        var p1 = pq.Ask(0, "Coordinator", "Lift the 15 thickenings whole?", ["Lift whole", "Keep bottoms"], 0, t);
        var p2 = pq.Ask(7, "Job 7: Pipes", "Which pipe size?", null, -1, t);
        check("phone: open questions the phone hasn't had go there, by number", pq.NotOnPhone().Select(q => q.Number).SequenceEqual(new[] { 1, 2 }), "");
        pq.MarkOnPhone(p1, t.AddSeconds(5));
        check("phone: a question sent isn't sent again", pq.NotOnPhone().Select(q => q.Number).SequenceEqual(new[] { 2 }), "");
        pq.Answer(2, "150 mm", t.AddSeconds(6));
        check("phone: an answered question doesn't go", pq.NotOnPhone().Count == 0, "");
        pq.Answer(1, "what do you mean?", t.AddSeconds(7));
        check("phone: one not understood waits to be written again", pq.NotOnPhone().Count == 0, "");
        pq.Rewrite(1, "Lift all 15 by 500 mm (bottoms move), or keep the bottoms?", null, -1, t.AddSeconds(30));
        check("phone: written again, it goes again", pq.NotOnPhone().Select(q => q.Number).SequenceEqual(new[] { 1 }), "");
        pq.MarkOnPhone(p1, default);
        check("phone: switched on again, a question marked unsent goes again", pq.NotOnPhone().Count == 1, "");
        check("phone: when it went is not saved with the chat (only this Revit can take the answer)",
            !System.Text.Json.JsonSerializer.Serialize(p1).Contains("PhoneAt"), "");

        var tab = new UserQuestion { Id = 9, TabNumber = 4, Asker = "Coordinator", Question = "Lift whole?", Options = ["Lift whole", "Keep bottoms"], Recommended = 1 };
        check("phone: a tab question is named by its tab number", AskRules.Label(tab) == "Q4" && AskRules.PhoneText(tab).StartsWith("Q4 (Coordinator): Lift whole?"), AskRules.PhoneText(tab));
        check("phone: a tab question shows its answers as buttons and the one suggested",
            AskRules.PhoneOptions(tab).SequenceEqual(new[] { "Lift whole", "Keep bottoms" }) && AskRules.PhoneText(tab).EndsWith("Suggested: Keep bottoms"), AskRules.PhoneText(tab));
        check("phone: 'yes' to a tab question stays 'yes' (it has no assumption)", AskRules.Keep("yes", tab) == "yes", "");
        var job = new UserQuestion { Id = 3, Asker = "Job 2: x", Question = "q?", Assumed = "copy it" };
        check("phone: an ask_user question keeps its one button and its Q number", AskRules.PhoneOptions(job).SequenceEqual(new[] { "copy it" }) && AskRules.Label(job) == "Q3"
            && AskRules.Keep("ok", job).StartsWith("Keep your assumption"), "");

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        if (!File.Exists(Path.Combine(root, "README.md"))) root = Directory.GetCurrentDirectory();
        string Src(string f) => File.ReadAllText(Path.Combine(root, "src/SketchProMax/Chat", f)).Replace("\r\n", "\n");
        var pane = Src("ChatPane.Questions.cs");
        check("phone: every change to the Questions tab syncs it with the phone", pane.Contains("try { SyncPhoneQuestions(); }") && pane.Contains("AskUser.AskTab(") && pane.Contains("AskUser.SettleTab("), "");
        check("phone: switching the phone on sends the questions waiting", Src("ChatPane.Phone.cs").Contains("SyncPhoneQuestions(again: true)"), "");
        check("phone: an answer from the phone reaches the tab, and the reply goes back to the phone",
            Src("ChatPane.Autopilot.cs").Contains("PhoneTabAnswered(q)") && pane.Contains("if (fromPhone) _phoneTurn = _turn;"), "");
    }
}
