using SketchProMax.Chat;

/// <summary>
/// What the 2026-11-17 review of a month of chats turned into: small asks run at once, corrections are fixed and kept
/// as lessons, and the chat answers to the point. The messages below are the kind the engineers typed. No Revit here.
/// </summary>
internal static class ChatFocusTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Small asks
        check("small ask: a line about the selected element", ChatFocus.IsSmall("For the framing selected can it be trimmed with the slab limit", 1), "");
        check("small ask: something selected, a short message", ChatFocus.IsSmall("move them to the middle between the tie rods", 27), "");
        check("small ask: 'this view' with nothing selected", ChatFocus.IsSmall("Can you hide in this view the lines which is in the approach slab", 0), "");
        check("small ask: selection not known and no word for it is not small", !ChatFocus.IsSmall("add dimensions between grids", -1), "");
        check("small ask: a wide ask is never small, whatever is selected",
            !ChatFocus.IsSmall("review all sheets of the submission for the selected berth", 1) && !ChatFocus.IsSmall("unify all steel to 345 all over the project", 3), "");
        check("small ask: too many selected, a long message, files or several tasks are not small",
            !ChatFocus.IsSmall("join them", ChatFocus.MaxSelected + 1)
            && !ChatFocus.IsSmall(string.Join(" ", Enumerable.Repeat("selected", ChatFocus.MaxWords + 1)), 1)
            && !ChatFocus.IsSmall("check the selected against this", 1, attachments: true)
            && !ChatFocus.IsSmall("1. tag the selected\n2. dimension them\n3. print the sheet", 1), "");

        // ---- Corrections
        foreach (var said in new[]
                 {
                     "why is it still there", "check the selected frame it is still outside the berth", "no no no that is so wrong",
                     "we are repeating the same mistakes this is not what I need", "the pile edit was not done see these selected columns",
                     "I see drawings are extremely poor what is that please and all tags are so bad", "No I just need a schedule for Berth 9",
                     "why did you remove them ?", "no I meant they are only from 1 to 4", "I did what is needed as you did not understand me",
                 })
            check("correction: \"" + said.Substring(0, Math.Min(40, said.Length)) + "\"", ChatFocus.IsCorrection(said), "");
        foreach (var said in new[]
                 {
                     "no problem for now keep the piles levels tag", "proceed with 2", "yes please", "continue",
                     "Can you rename this section into G6 and detail number to G6", "Move the selected piles between tie rods", "",
                     "why is the selected beam showing 1995 although it is supposed to be 2000", "why is the selected grid not 2800 and 4200 ?",
                 })
            check("not a correction: \"" + said + "\"", !ChatFocus.IsCorrection(said), "");

        // ---- What goes with the message
        var fix = ChatFocus.Hint("why is it still there", 1, false, answered: true);
        var small = ChatFocus.Hint("trim the selected beam to the slab", 1, false, answered: false);
        check("hint: a correction is fixed, not explained, and looked at in the view",
            fix.Contains("correction") && fix.Contains("Don't explain") && fix.Contains("capture_view_image") && fix.Contains("not typed by the user"), fix);
        check("hint: a small ask runs without a job or options, checked in the active view, three lines",
            small.Contains("no job") && small.Contains("changes least") && small.Contains("capture_view_image") && small.Contains("three lines"), small);
        check("hint: a correction needs an answer to correct; else it is judged as an ask",
            ChatFocus.Hint("why is the slab not joined", 0, false, answered: false).Length == 0
            && ChatFocus.Hint("still the selected one", 1, false, answered: false) == small, "");
        check("hint: nothing for ordinary work", ChatFocus.Hint("prepare the submission sheets for berths 7 to 11", 0, false, answered: true).Length == 0, "");
        check("hint: starts on its own paragraph, so the message reads as typed", small.StartsWith("\n\n[Sketch ProMax"), "");

        // ---- Lessons
        var global = ChatFocus.ParseLesson("GLOBAL: After trimming a family's solids, trim its plan-only lines too and look at the plan view.");
        var model = ChatFocus.ParseLesson("Here it is:\n**MODEL: Trapezoid slab shapes exist only in berths 1 to 4.**");
        check("lesson: a global one is kept as a note that starts with Lesson",
            global is { Global: true } g && g.Note.StartsWith(ChatFocus.LessonPrefix + "After trimming") && g.Note.EndsWith("plan view."), global?.Note ?? "");
        check("lesson: a model one, with Markdown around it", model is { Global: false } m && m.Note == ChatFocus.LessonPrefix + "Trapezoid slab shapes exist only in berths 1 to 4.", model?.Note ?? "");
        check("lesson: NONE, nothing, or a bare label is no lesson",
            ChatFocus.ParseLesson("NONE") is null && ChatFocus.ParseLesson("") is null && ChatFocus.ParseLesson(null) is null && ChatFocus.ParseLesson("GLOBAL: ok") is null, "");
        var ask = ChatFocus.LessonMessage("trim the beam", new string('a', 5000), "why is it still there");
        check("lesson: the run gets the request, a cut answer and the correction, and may answer NONE",
            ask.Contains("trim the beam") && ask.Contains("why is it still there") && ask.Contains("NONE") && ask.Contains("GLOBAL:") && ask.Contains("MODEL:") && ask.Length < 7000, ask.Length.ToString());
        check("lesson: the chat says what it kept, without the label", ChatFocus.Saved(global!.Value.Note).StartsWith("Lesson kept for next time: After trimming") && ChatFocus.Saved(global.Value.Note).Contains("Notes tab"), "");

        // Kept where every chat and job reads it: the memory notes' prompt block.
        var sections = new List<MemoryNotes.Section>();
        MemoryNotes.Add(sections, null, global.Value.Note);
        MemoryNotes.Add(sections, @"C:\P\Quay.rvt", model!.Value.Note);
        var block = MemoryNotes.PromptBlock(sections, @"C:\P\Quay.rvt");
        check("lesson: global and this model's lessons reach the prompt, another model's don't",
            block.Contains("plan-only lines") && block.Contains("berths 1 to 4") && !MemoryNotes.PromptBlock(sections, @"C:\P\Other.rvt").Contains("berths 1 to 4"), block);
        check("lesson: the same lesson isn't kept twice", !MemoryNotes.Add(sections, null, global.Value.Note), "");

        check("free workers: nothing to split is two words, not a reason",
            FreeWorkers.Nudge(new ChatJob { Number = 3, Title = "Sheets" }, 1).Contains("answer only \"No split.\""), "");
    }

    /// <summary>Checks on the sources: the prompts and the pane carry the rules.</summary>
    public static void RunSources(string root, Action<string, bool, string> check)
    {
        string Read(string path) => File.ReadAllText(Path.Combine(root, path));
        var chat = Read("src/SketchProMax/Chat/ClaudeCli.cs");
        check("prompt: one reply, the result first, three lines", chat.Contains("To the point: one reply per request, the result first, three lines at most"), "");
        check("prompt: tables only when asked for", chat.Contains("a table only when") && !chat.Contains("short paragraphs, lists and tables"), "");
        check("prompt: small asks are done by the chat itself, the way that changes least, without a list of ways",
            chat.Contains("Small asks are done at once, by you") && chat.Contains("Several ways to do it: don't list them"), "");
        check("prompt: drawn work is looked at in the user's view, by the chat and by jobs",
            chat.Contains("Check it as they see it") && chat.Split(["plan-only lines"], StringSplitOptions.None).Length >= 3, "");
        check("prompt: a job's report reaches the user in three lines, and nothing is written while jobs work",
            chat.Contains("don't write\n          about them") && chat.Contains("the result in three lines at most"), "");
        check("prompt: the selection is the scope, the user's words are used, a why is answered without a change, open items are one list",
            chat.Contains("The selection is the scope") && chat.Contains("Their words: use the names the user uses") && chat.Contains("is never used again")
            && chat.Contains("\"Why ...?\" is a question") && chat.Contains("<where>: <problem> - <what to do>"), "");
        var pane = Read("src/SketchProMax/Chat/ChatPane.cs");
        var brief = Read("src/SketchProMax/Chat/ChatPane.Brief.cs");
        check("pane: a typed message carries the hint and a correction is learned from",
            pane.Contains("LearnFrom(text);") && pane.Contains("hint: FocusHint(text, attachments)") && pane.Contains("var sent = text + hint + "), "");
        check("pane: a small ask or a correction gets no brief card", brief.Contains("if (FocusHint(text, attachments).Length > 0) return false;"), "");
        check("pane: lessons go to the memory notes, global or this model's",
            Read("src/SketchProMax/Chat/BriefRunner.cs").Contains("MemoryNotes.Add(sections, key, lesson.Note)"), "");
    }
}
