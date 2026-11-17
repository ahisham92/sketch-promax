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
        @"|\b(should|shall) not have\b|\bshouldn't have\b|\bwhy did you\b|\bdoes not make sense\b|\bdoesn't make sense\b",
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
        if (answered && IsCorrection(text))
            return start + "this reads as a correction of your last work. Don't explain the earlier attempt and don't offer options. " +
                   "When what they want is clear, fix exactly that now, yourself; when it isn't, ask one short question. Before you answer, " +
                   "look at the result in the view they are looking at (capture_view_image), not only at numbers. Answer in one or two lines.]";
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
    public static string LessonMessage(string request, string answer, string correction) =>
        "The request:\n" + Cut(request, 1500) + "\n\nThe assistant's answer:\n" + Cut(answer, 3000) + "\n\nThe user's correction:\n" + Cut(correction, 1500) +
        "\n\nWrite the one lesson that would have avoided this correction, as an instruction for next time: what to do, in one sentence of " +
        "plain words (at most 40 words), general enough to hold for the next similar task, with no element ids and no names of people. " +
        "Start the line with GLOBAL: when it holds for any model (how to work, check or answer), or MODEL: when it is a fact about this " +
        "project only (its berths, levels, names, standards). When the message is not a correction, or teaches nothing for next time " +
        "(a one-off choice, a question, new work), answer exactly NONE.";

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
