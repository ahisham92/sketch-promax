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
    }
}
