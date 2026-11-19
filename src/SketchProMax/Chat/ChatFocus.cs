using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// Keeps the chat to the point (the 2026-11-17 review of a month of chats: four replies per message, an hour for a
/// one-element trim, the same corrections typed again and again). Three things, all plain functions (the unit tests
/// compile this file):
///  - a small ask (about what is selected or shown, a line or two) runs at once: no brief, no job, no list of options;
///  - a correction ("no", "still there", "why is it...") is fixed and checked in the view the user looks at;
///  - and that correction is distilled into one lesson kept in the memory notes, which every later chat and job reads;
///  - "see what I did for ..." is read as an example to reproduce exactly, not a hint to interpret;
///  - "are you working?" is answered at once from the job list, without waiting for the chat.
/// </summary>
internal static class ChatFocus
{
    /// <summary>The longest message (in words) that still counts as a small ask, and the most elements selected.</summary>
    public const int MaxWords = 40, MaxSelected = 60;

    private static readonly Regex Here = new(
        @"\b(selected|selection|selecting|this (view|one|beam|slab|floor|pile|column|wall|section|plan|sheet|tag|dimension|line|element|family|frame|framing)|the one|current view|opened view|open view|active view)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Words that make an ask wide whatever is selected ("for all berths", "the whole model").</summary>
    private static readonly Regex Wide = new(
        @"\b(all (the )?(berths|sheets|views|drawings|levels|models)|every (berth|sheet|view|drawing|level)|whole (model|project)|entire (model|project)|all over|allover|submission|package)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// True for a small ask: a short message about what is selected (<paramref name="selected"/> elements; -1 when not
    /// known) or about "this view" or "the selected ones", with no files, not several tasks, and nothing that makes it wide.
    /// </summary>
    public static bool IsSmall(string text, int selected, bool attachments = false)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || attachments || Words(t) > MaxWords) return false;
        if (Wide.IsMatch(t) || DirectAdvice.IsSeveral(t)) return false;
        return selected is >= 1 and <= MaxSelected || Here.IsMatch(t);
    }

    private static readonly Regex Fine = new(@"^no[\s,]+(problem|need|worries|issue)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Correcting = new(
        // A plain "why ...?" is a question (answered, nothing changed); a why about work that didn't hold is a correction.
        @"^(no+\b|nope\b|wrong\b|incorrect\b|still\b|again\b)" +
        @"|^why\b.*\b(still|again|removed?|deleted?|missing|not (done|fixed|working|moved|changed|updated|applied))\b" +
        @"|\bstill (there|not|outside|inside|the same|showing|shown|missing|clashing|wrong|poor|bad)\b" +
        @"|\b(same|these|those) mistakes?\b|\brepeating\b|\bnot what i\b|\bi (told|said|asked)\b|\bas i (told|said|asked)\b|\bi meant\b" +
        @"|\b(did not|didn't|don't|do not) understand\b|\bi (did not|didn't|don't|do not) (want|need|mean|ask)\b" +
        @"|\b(is|are|was|were) (so |very |extremely |totally )?(wrong|poor|bad|incorrect)\b|\bnot correct\b|\bwas not done\b|\bwasn't done\b|\bnot done\b" +
        @"|\b(should|shall) not have\b|\bshouldn't have\b|\bwhy did you\b|\bdoes not make sense\b|\bdoesn't make sense\b" +
        // "this is the fourth time" (19 Nov 2026: read as a new request, and the brief guessed what it was about).
        @"|\b(second|third|fourth|fifth|sixth|tenth|\d+(st|nd|rd|th)) time\b|\bhow many times\b|\bagain and again\b|\bonce again\b|\b(we are|we're|i am|i'm|keep) repeating\b|\b(already|have already) (told|said|asked)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the message reads as a correction of the work before it ("no", "still there", "why is it...").</summary>
    public static bool IsCorrection(string text)
    {
        var t = (text ?? "").Trim();
        return t.Length > 0 && !Fine.IsMatch(t) && Correcting.IsMatch(t);
    }

    /// <summary>
    /// What goes to the chat with the user's message (never shown in the pane): how to take a correction or a small ask.
    /// "" for anything else. <paramref name="answered"/>: the chat answered before in this conversation.
    /// </summary>
    public static string Hint(string text, int selected, bool attachments, bool answered)
    {
        const string start = "\n\n[Sketch ProMax, not typed by the user: ";
        if (answered && IsRepeat(text))
            return start + "the user says they have raised this before. Find their earlier messages about it in this chat (and the " +
                   "summary of earlier messages), say in one line what it is and why it came back, and fix the cause so it holds from " +
                   "now on (a setting, a rule kept with memory_note, the whole scope), not only this once. Don't explain the earlier " +
                   "attempts and don't offer options. Answer in two or three lines.]";
        if (answered && IsCorrection(text))
            return start + "this reads as a correction of your last work. Don't explain the earlier attempt and don't offer options. " +
                   "When what they want is clear, fix exactly that now, yourself; when it isn't, ask one short question. Before you answer, " +
                   "look at the result in the view they are looking at (capture_view_image), not only at numbers. Answer in one or two lines.]";
        if (IsQuestion(text))
            return start + QuestionRule + "]";
        if (IsExample(text))
            return start + "the user made an example by hand and wants the same. First read the example exactly: which elements or " +
                   "view they changed (the model note lists what the user changed), its types, parameter values, offsets, joins and " +
                   "graphics, and look at it (capture_view_image). For annotation in a view they set up, learn it first " +
                   "(drawing_standard action=learn, add=true, that view). Say in one line what the example has, then reproduce exactly " +
                   "that: don't improve on it, and what differs from the example in your result is a mistake to fix before you answer.]";
        if (IsSmall(text, selected, attachments))
            return start + "this is a small ask about what is selected or shown. Do it yourself now: no job, and no list of ways to choose from. " +
                   "Take the way that changes least (the element keeps its id, type and values) and say in one line which you took. Before you " +
                   "answer, look at the result in the active view (capture_view_image): what the user sees must be right, not only the numbers. " +
                   "Answer in at most three lines.]";
        return "";
    }

    // ---- Repeats and questions ---------------------------------------------------------------------------------------

    private static readonly Regex Repeat = new(
        @"\b(second|third|fourth|fifth|sixth|tenth|\d+(st|nd|rd|th)) time\b|\bhow many times\b|\bagain and again\b|\b(we are|we're|i am|i'm|keep) repeating\b|\b(i )?(already|have already) (told|said|asked)\b|\bas i (told|said) (you )?(before|already)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the user says they are raising something again ("this is the fourth time", "I already told you").</summary>
    public static bool IsRepeat(string text) => Repeat.IsMatch((text ?? "").Trim());

    /// <summary>
    /// True for a plain question ("did you extend the piles...?", "is the model neat now?", "why ...?"): it is answered at
    /// once, also while the chat or its jobs are busy, and never handed to a job. A status question has its own answer.
    /// </summary>
    public static bool IsQuestion(string text)
    {
        var t = (text ?? "").Trim();
        return t.Length > 0 && !IsStatusAsk(t) && PromptBriefs.LooksReadOnly(t) == true;
    }

    /// <summary>How the chat takes a question (19 Nov 2026: "did you extend the piles...?" got "a job will check").</summary>
    public const string QuestionRule =
        "this is a question: answer it yourself in this turn, the answer first (yes or no when it asks yes or no), in one to " +
        "three lines. Don't start a job and don't hand the question to one, and don't say a job will check: if a fact is " +
        "missing, read it now with a quick read-only tool (get_activity_log, query_elements, get_element_parameters, chat_jobs " +
        "action=ask), then answer. When part of it truly isn't known yet, say what is known and what isn't. Change nothing";

    /// <summary>The instructions of the quick run that answers a question while the chat is busy with other work.</summary>
    public const string QuickInstructions =
        "You answer the user's question in the Sketch ProMax chat inside Revit while the main chat and its jobs are busy. " +
        "The user is a structural engineer. You get the summary of the chat, its last messages and the jobs with their " +
        "reports. Answer from those first; use a read-only Revit tool only when the answer is not there and one quick read " +
        "settles it. Start with the answer (yes or no when it asks yes or no), then at most two short lines. Plain words, " +
        "no tables, no headings, no element ids. Never change the model and never promise work: when something isn't done " +
        "yet, say so plainly. When it truly can't be known yet, say what is known and which job is still on it.";

    /// <summary>The message of that quick run: the question, then the chat's state.</summary>
    public static string QuickMessage(string question, string recall, IEnumerable<(string Kind, string Text)> recent, IEnumerable<ChatJob> jobs)
    {
        var b = new System.Text.StringBuilder();
        b.Append("The user's question:\n").Append(Cut(question, 1500)).Append("\n\n");
        if ((recall ?? "").Trim().Length > 0) b.Append("Summary of the earlier chat:\n").Append(Cut(recall!, 4000)).Append("\n\n");
        var last = recent.Where(r => (r.Text ?? "").Trim().Length > 0).ToList();
        if (last.Count > 0)
        {
            b.Append("The last messages (oldest first):\n");
            foreach (var (kind, text) in last) b.Append(kind == "user" ? "User: " : "Sketch ProMax: ").Append(Cut(text, 1200)).Append('\n');
            b.Append('\n');
        }
        var all = jobs.OrderBy(j => j.Number).ToList();
        if (all.Count > 0)
        {
            b.Append("The jobs (newest last):\n");
            foreach (var j in all.Skip(Math.Max(0, all.Count - 15)))
            {
                b.Append("Job ").Append(j.Number).Append(" (").Append(j.State).Append("): ").Append(JobBoard.OneLine(j.Title, 80));
                var now = JobBoard.OneLine(j.Now, 120);
                if (j.Active && now.Length > 0) b.Append(". Now: ").Append(now);
                if (j.Summary.Trim().Length > 0) b.Append(". Reported: ").Append(Cut(j.Summary, 1200));
                b.Append('\n');
            }
        }
        return b.ToString();
    }

    /// <summary>
    /// What the main chat hears with the user's next message about questions answered while it was busy, so it knows the
    /// user asked and what they were told (and doesn't answer again). "" when there were none.
    /// </summary>
    public static string Answered(IEnumerable<(string Question, string Answer)> asides)
    {
        var list = asides.ToList();
        if (list.Count == 0) return "";
        var b = new System.Text.StringBuilder("\n\n[Sketch ProMax, not typed by the user: while you were busy the user asked:");
        foreach (var (q, a) in list)
            b.Append("\n- ").Append(Cut(q, 300)).Append(a.Trim().Length > 0 ? " They were answered at once: " + Cut(a, 500) : " Not answered yet: answer it first, in a line.");
        return b.Append("\nDon't answer the answered ones again; act on anything they show is still to do.]").ToString();
    }

    // ---- Their example -------------------------------------------------------------------------------------------------

    private static readonly Regex Example = new(
        @"\b(see|check|look at|follow|copy|repeat|same as|as|like|similar to) (what|how|the one|the ones|the way) i(('ve| have))? (just )?(did|made|done|fixed|adjusted|modified|changed|placed|drew|modelled|modeled|annotated|tagged|dimensioned)\b" +
        @"|\b(like|as|same as|similar to) (i|what i) (did|made|have done|fixed|adjusted|modified|annotated) (for|in|on|at|to)\b" +
        @"|\bi (did|made|fixed|adjusted|modified|annotated|tagged|dimensioned) (one|it|this|the first|an example|a sample)\b.*\b(same|rest|others?|all|remaining|similarly|likewise)\b" +
        @"|\b(do|apply|make) (the )?(same|similar)\b.*\b(i (did|made|have done|fixed|adjusted))\b|\bas per my (example|sample|manual)\b|\bmy (example|sample)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the user points at something they did by hand as the example to follow ("see what I did for berth 12").</summary>
    public static bool IsExample(string text) => Example.IsMatch((text ?? "").Trim());

    // ---- "Are you working?" --------------------------------------------------------------------------------------------

    private static readonly Regex StatusAsk = new(
        @"^(status|progress|update|updates|any update|any updates|any progress|hello|still there)\W*$" +
        @"|\b(are|r) (you|u) (still )?(working|there|alive|stuck|running|on it|done|finished|hanging|hung|frozen)\b" +
        @"|\b(is|are) (it|this|that|they|the jobs?|the task) (still )?(working|running|stuck|done|finished|hanging|hung|frozen)\b" +
        @"|\bstill (working|running)\b|\bwhy (is it |are you )?(taking )?so (long|slow)\b|\bwhat is taking\b" +
        @"|\bhow (many|much) (jobs?|tasks?|items?|time|more|longer)?\s*(are |is )?(left|remaining|pending|running|done|finished)\b|\bhow (long|much longer)\b" +
        @"|\bwhat('s| is) (left|remaining|pending|the status|the progress|running)\b|\bdid (you|it) (stop|finish|hang|freeze|crash)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The longest message (in words) that can be a status question, and when a running job counts as long (minutes).</summary>
    public const int StatusWords = 12, LongMinutes = 30;

    /// <summary>True for "are you working?", "how many jobs left?", "status?": answered from the job list, with no wait.</summary>
    public static bool IsStatusAsk(string text)
    {
        var t = (text ?? "").Trim();
        return t.Length > 0 && Words(t) <= StatusWords && StatusAsk.IsMatch(t);
    }

    /// <summary>
    /// The answer to a status question, from the job list alone: what runs, what waits, what ended since
    /// <paramref name="since"/> (the user's last request). "" when nothing is running and the chat isn't answering:
    /// then the question goes to the chat as any other.
    /// </summary>
    public static string StatusLine(IEnumerable<ChatJob> jobs, bool answering, DateTime since, DateTime now)
    {
        var all = jobs.ToList();
        var running = all.Where(j => j.Active).OrderBy(j => j.Started).ToList();
        var queued = all.Count(j => j.State == JobBoard.Queued);
        if (running.Count == 0 && queued == 0)
            return answering ? "Yes, working on your last message; no jobs are running. Your message reaches it at its next step." : "";
        var ended = all.Where(j => !j.Open && j.Ended >= since).ToList();
        var done = ended.Count(j => j.State == JobBoard.Done);
        var failed = ended.Count(j => j.State is JobBoard.Failed or JobBoard.Stopped);
        var text = new System.Text.StringBuilder("Working: ").Append(Count(running.Count, "job")).Append(" running");
        if (queued > 0) text.Append(", ").Append(queued).Append(" waiting to start");
        if (done > 0) text.Append(", ").Append(done).Append(" done");
        if (failed > 0) text.Append(", ").Append(failed).Append(" failed or stopped");
        text.Append('.');
        foreach (var job in running.Take(3))
        {
            var minutes = job.Started == default ? 0 : (int)Math.Max(0, (now - job.Started).TotalMinutes);
            var doing = JobBoard.OneLine(job.Now, 80);
            text.Append("\nJob ").Append(job.Number).Append(", ").Append(JobBoard.OneLine(job.Title, 60))
                .Append(doing.Length > 0 ? ": " + doing : "").Append(" (").Append(minutes).Append(" min")
                .Append(minutes >= LongMinutes ? ", long: open its card to see it, or stop it there" : "").Append(')');
        }
        if (running.Count > 3) text.Append("\nAnd ").Append(running.Count - 3).Append(" more.");
        return text.ToString();
    }

    private static string Count(int n, string what) => n + " " + what + (n == 1 ? "" : "s");

    // ---- Lessons -------------------------------------------------------------------------------------------------------

    /// <summary>How a lesson starts in the memory notes, so the Notes tab shows which notes were learned from corrections.</summary>
    public const string LessonPrefix = "Lesson: ";

    public const string LessonInstructions =
        "You turn a user's correction into one lesson for next time. The user is a structural engineer working in Revit with an " +
        "assistant. You get the request, the assistant's answer, and the user's correction of it. Use no tools.";

    /// <summary>The message for the one-shot run that distils a correction.</summary>
    public static string LessonMessage(string request, string answer, string correction, IReadOnlyList<string>? notes = null) =>
        "The request:\n" + Cut(request, 1500) + "\n\nThe assistant's answer:\n" + Cut(answer, 3000) + "\n\nThe user's correction:\n" + Cut(correction, 1500) +
        NotesList(notes) +
        "\n\nWrite the one lesson that would have avoided this correction, as an instruction for next time: what to do, in one sentence of " +
        "plain words (at most 40 words), general enough to hold for the next similar task, with no element ids and no names of people. " +
        "Keep to what the user said: don't widen it to things they didn't name, and where their words are unclear, keep only the clear part. " +
        "Start the line with GLOBAL: when it holds for any model (how to work, check or answer), or MODEL: when it is a fact about this " +
        "project only (its berths, levels, names, standards). When the message is not a correction, or teaches nothing for next time " +
        "(a one-off choice, a question, new work), answer exactly NONE." +
        (notes is { Count: > 0 } ? " When your lesson changes or contradicts a note kept so far, add a line REPLACES: <its number> for each such note." : "");

    /// <summary>The notes kept so far, numbered, so a lesson that changes one replaces it (19 Nov 2026: "AZ piles 100 mm in" stayed beside "AZ piles at the soffit").</summary>
    private static string NotesList(IReadOnlyList<string>? notes)
    {
        if (notes is not { Count: > 0 }) return "";
        var b = new System.Text.StringBuilder("\n\nNotes kept so far:");
        for (var i = 0; i < notes.Count && i < 60; i++) b.Append('\n').Append(i + 1).Append(". ").Append(Cut(notes[i], 300));
        return b.ToString();
    }

    /// <summary>The numbers (from 1) of the kept notes the lesson replaces ("REPLACES: 3"), within <paramref name="count"/>.</summary>
    public static List<int> ParseReplaces(string? answer, int count)
    {
        var found = new List<int>();
        foreach (Match m in Regex.Matches(answer ?? "", @"REPLACES:\s*([\d,\s]+)", RegexOptions.IgnoreCase))
            foreach (var part in m.Groups[1].Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out var n) && n >= 1 && n <= count && !found.Contains(n)) found.Add(n);
        return found;
    }

    private static string Cut(string text, int max)
    {
        var t = (text ?? "").Trim();
        return t.Length > max ? t.Substring(0, max) + "…" : t;
    }

    /// <summary>The lesson in the run's answer: whether it holds for every model, and the note to keep. Null when there is none.</summary>
    public static (bool Global, string Note)? ParseLesson(string? answer)
    {
        foreach (var raw in (answer ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().Trim('*', '`', '-', ' ');
            var global = line.StartsWith("GLOBAL:", StringComparison.OrdinalIgnoreCase);
            if (!global && !line.StartsWith("MODEL:", StringComparison.OrdinalIgnoreCase)) continue;
            var note = MemoryNotes.Clean(line.Substring(line.IndexOf(':') + 1));
            if (note.Length < 8) return null;
            return (global, LessonPrefix + note);
        }
        return null;
    }

    /// <summary>The line the chat shows once a lesson is kept.</summary>
    public static string Saved(string note) =>
        "Lesson kept for next time: " + (note.StartsWith(LessonPrefix, StringComparison.Ordinal) ? note.Substring(LessonPrefix.Length) : note) +
        " (edit or delete it in the Notes tab).";
}
