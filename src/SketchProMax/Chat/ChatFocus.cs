using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// Keeps the chat to the point (the 2026-11-17 review of a month of chats: four replies per message, an hour for a
/// one-element trim, the same corrections typed again and again). Three things, all plain functions (the unit tests
/// compile this file):
///  - a small ask (about what is selected or shown, a line or two) runs at once: no brief, no job, no list of options;
///  - a correction ("no", "still there", "why is it...") is fixed and checked in the view the user looks at;
///  - and that correction is distilled into one lesson kept in the memory notes, which every later chat and job reads.
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
        if (IsSmall(text, selected, attachments))
            return start + "this is a small ask about what is selected or shown. Do it yourself now: no job, and no list of ways to choose from. " +
                   "Take the way that changes least (the element keeps its id, type and values) and say in one line which you took. Before you " +
                   "answer, look at the result in the active view (capture_view_image): what the user sees must be right, not only the numbers. " +
                   "Answer in at most three lines.]";
        return "";
    }

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
