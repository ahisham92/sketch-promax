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
            !ChatFocus.Hint("why is the slab not joined", 0, false, answered: false).Contains("correction")
            && ChatFocus.Hint("still the selected one", 1, false, answered: false) == small, "");
        check("hint: nothing for ordinary work", ChatFocus.Hint("prepare the submission sheets for berths 7 to 11", 0, false, answered: true).Length == 0, "");
        check("hint: starts on its own paragraph, so the message reads as typed", small.StartsWith("\n\n[Sketch ProMax"), "");

        // ---- Their example
        foreach (var said in new[]
                 {
                     "see what I did for berth 12 and do the same for berth 13", "do the same for the others like the one I made",
                     "I fixed one of them, apply the same to the rest", "tag them as per my example", "follow what I did in this view",
                 })
            check("example: \"" + said + "\"", ChatFocus.IsExample(said), "");
        check("example: ordinary asks are not one",
            !ChatFocus.IsExample("tag the piles in this view") && !ChatFocus.IsExample("what did you do for berth 12") && !ChatFocus.IsExample(""), "");
        var example = ChatFocus.Hint("see what I did for this one and do the same for the selected ones", 4, false, answered: true);
        check("example: read exactly, said in one line, reproduced without improving, before the small-ask hint",
            example.Contains("example by hand") && example.Contains("don't improve on it") && example.Contains("drawing_standard action=learn"), example);

        // ---- "Are you working?"
        foreach (var said in new[] { "are you working?", "Are you still working", "how many jobs left", "status?", "any update", "why is it taking so long", "is it stuck?", "what is left" })
            check("status: \"" + said + "\"", ChatFocus.IsStatusAsk(said), "");
        check("status: work and long messages are not status questions",
            !ChatFocus.IsStatusAsk("update the status parameter of the selected piles") && !ChatFocus.IsStatusAsk("continue") && !ChatFocus.IsStatusAsk("")
            && !ChatFocus.IsStatusAsk("are you working on berth 12 or 13 because I need the sections of berth 13 first and then the plans of the others"), "");
        var t0 = new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);
        var jobs = new List<ChatJob>
        {
            new() { Number = 1, Title = "Old job", State = JobBoard.Done, Ended = t0.AddHours(-3) },
            new() { Number = 2, Title = "Tag berth 12 plans", State = JobBoard.Done, Ended = t0.AddMinutes(5) },
            new() { Number = 3, Title = "Dimension berth 13 sections", State = JobBoard.Running, Started = t0.AddMinutes(2), Now = "Placing dimensions\nin view 4" },
            new() { Number = 4, Title = "Check berth 14", State = JobBoard.Running, Started = t0.AddMinutes(-40) },
            new() { Number = 5, Title = "Sheets", State = JobBoard.Queued },
            new() { Number = 6, Title = "Broken", State = JobBoard.Failed, Ended = t0.AddMinutes(6) },
        };
        var line = ChatFocus.StatusLine(jobs, true, t0, t0.AddMinutes(12));
        check("status: counts what runs, waits and ended since the request",
            line.StartsWith("Working: 2 jobs running, 1 waiting to start, 1 done, 1 failed or stopped."), line);
        check("status: each running job with what it does and for how long; a long one is flagged",
            line.Contains("Job 3, Dimension berth 13 sections: Placing dimensions in view 4 (10 min)") && line.Contains("Job 4, Check berth 14 (52 min, long"), line);
        check("status: nothing running and not answering goes to the chat; answering without jobs says so",
            ChatFocus.StatusLine(jobs.Take(2), false, t0, t0).Length == 0 && ChatFocus.StatusLine([], true, t0, t0).StartsWith("Yes, working"), "");

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
        string Read(string path) => File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n");
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
        var basePrompt = Read("src/SketchProMax/Agent/SystemPrompt.cs");
        check("prompt: kept code is looked up before code is written; show one then do all; their example; finish what was started",
            basePrompt.Contains("Once coded, not written again") && basePrompt.Contains("worked_code") && basePrompt.Contains("a ready-made tool first")
            && !File.Exists(Path.Combine(root, "src/SketchProMax/Sharing/TeamShare.cs")) && basePrompt.Contains("Show one, then do all")
            && basePrompt.Contains("Follow their example") && basePrompt.Contains("Finish what was started"), "");
        var code = Read("src/SketchProMax/Tools/CodeTools.cs");
        check("code tools: code that worked is kept, and a failed call points at kept code",
            code.Contains("WorkedCode.Note(code, description, Name") && code.Contains("WorkedCode.PointerFor(description, code)"), "");
        var jobsPane = Read("src/SketchProMax/Chat/ChatPane.Jobs.cs");
        check("jobs: a check that finds parts not done sends the job back by itself, and the check carries the user's words",
            jobsPane.Contains("if (JobReview.SendBack(checkedJob))") && jobsPane.Contains("JobReview.FixMessage(checkedJob)") && jobsPane.Contains("StartReview(job.Number, now, AskedWords(job.Created), _conv.Recall)")
            && chat.Contains("each part of your task as done or not"), "");
        var joins = Read("src/SketchProMax/Tools/JoinTools.cs");
        var batch = Read("src/SketchProMax/Tools/ItemBatch.cs");
        check("joins: one call holds Revit (and the chat window) about 20 s at most, the rest waits for the next call, joined pairs skipped (bug 110)",
            joins.Contains("budget: Budget") && joins.Contains("TimeSpan.FromSeconds(20)") && joins.Contains("pairs already joined are skipped")
            && batch.Contains("clock.Elapsed >= most") && batch.Contains("result.Items.Add(new(what, false, TimeLeft, null))"), "");
        var jobsPane111 = Read("src/SketchProMax/Chat/ChatPane.Jobs.cs");
        check("exact (bug 118): jobs read 'exactly/follow/match/flush' as 1 mm and never borrow the clash tolerance",
            Read("src/SketchProMax/Chat/ClaudeCli.cs").Contains("never makes an element that is\n          a few mm off \"already matching\""), "");
        check("notes (bug 116): a note is saved while Revit is busy, by the bridge, not refused",
            Read("src/SketchProMax/Bridge/BridgeServer.cs").Contains("if (name == \"memory_note\" && (Jobs.Running is not null || _dispatcher.IsRunning)) return MemoryNoteTool.Answer(")
            && Read("src/SketchProMax/Tools/MemoryTools.cs").Contains("internal static ToolResult Answer(ToolArgs args)"), "");
        check("job (bug 115): a new job session gets the chat's rules with its task (not a check, which gets them already)",
            jobsPane111.Contains("if (job.ReviewOf == 0 && JobReview.RulesForJob(_conv.Recall) is { Length: > 0 } rules) message = message with { Text = message.Text + rules };"), "");
        check("pause (bug 111): a job that ended its turn because the pause refused its call is paused, not done, and carries on if Continue came first",
            jobsPane111.Contains("JobPause.TookRefusal(run.Job.Key) && board.Pause(run.Job.Number, now)")
            && jobsPane111.IndexOf("JobPause.TookRefusal(run.Job.Key) && board.Pause") < jobsPane111.IndexOf("if (board.RunEnded(run.Job, !end.IsError, summary, now)")
            && jobsPane111.Contains("board.Message(run.Job.Number, JobPause.CarryOn, now)")
            && Read("src/SketchProMax/Chat/ChatPane.Pause.cs").Contains("JobPause.TookRefusal(run.Job.Key); // set aside here"), "");
        var pane = Read("src/SketchProMax/Chat/ChatPane.cs");
        check("pane: a status question is answered from the job list at once, and kept code is named with the message",
            pane.Contains("StatusAnswer(_input.Text)") && pane.Contains("WorkedCode.HintFor(text)"), "");
        var brief = Read("src/SketchProMax/Chat/ChatPane.Brief.cs");
        check("pane: a typed message carries the hint and a correction is learned from",
            pane.Contains("LearnFrom(text);") && pane.Contains("hint: FocusHint(text, attachments) + ") && pane.Contains("var sent = text + hint + "), "");
        check("pane: a small ask or a correction gets no brief card", brief.Contains("if (FocusHint(text, attachments).Length > 0) return false;"), "");
        // ---- 19 Nov 2026: repeats and questions in a real chat ("this is the fourth time", "did you extend the piles...?")
        foreach (var said in new[]
                 {
                     "this is the fourth time", "This is the 3rd time I say this", "how many times do I need to say it",
                     "I feel we are repeating the same things", "I already told you piles are never joined",
                 })
            check("repeat: \"" + said + "\"", ChatFocus.IsRepeat(said) && ChatFocus.IsCorrection(said), "");
        check("repeat: ordinary messages are not one",
            !ChatFocus.IsRepeat("do it at the same time as the tags") && !ChatFocus.IsRepeat("the first time you open it, check the levels") && !ChatFocus.IsRepeat(""), "");
        var repeat = ChatFocus.Hint("this is the fourth time", 0, true, answered: true);
        check("repeat: the hint finds the earlier messages and fixes the cause so it holds",
            repeat.Contains("raised this before") && repeat.Contains("fix the cause") && repeat.Contains("not only this once"), repeat);
        foreach (var said in new[]
                 {
                     "did you extend the piles just upwards to catch the slab and beams but you kept the tip level correct ?",
                     "did you move the piles and made all updates of the grading in berth 9 and approach slabs ?",
                     "why clara is not working are all tasks finalized ?", "I extended the void is that okay now ?",
                     "is the model neat now?", "are all the joints joined?",
                 })
            check("question: \"" + said.Substring(0, Math.Min(40, said.Length)) + "\"", ChatFocus.IsQuestion(said), "");
        foreach (var said in new[]
                 {
                     "extend the piles 100 mm into the slab", "move the 0.1m is fine as long as it will fix things", "are you working?",
                     "yes and also follow the levels as it was 100mm into the beam", "did you extend the piles? then join the slabs and tag them", "",
                 })
            check("not a question: \"" + said + "\"", !ChatFocus.IsQuestion(said), "");
        var question = ChatFocus.Hint("did you extend the piles and keep the tips?", 0, false, answered: true);
        check("question: answered now, answer first, never handed to a job, nothing changed",
            question.Contains("answer it yourself in this turn") && question.Contains("yes or no") && question.Contains("Don't start a job")
            && question.Contains("get_activity_log") && question.Contains("Change nothing"), question);
        check("question: gets no brief", !PromptBriefs.ShouldPlan(BriefMode.ModelChanges, "did you extend the piles just upwards to catch the slab and beams ?"), "");
        var quick = ChatFocus.QuickMessage("did you extend the piles?", "Rules: piles never joined.",
            [("user", "raise the pile heads"), ("assistant", "Job 26 has the pile heads.")],
            [new ChatJob { Number = 26, Title = "Berth 10 pile heads", State = JobBoard.Done, Summary = "257 piles raised, tips unchanged." },
             new ChatJob { Number = 27, Title = "Approach slabs", State = JobBoard.Running, Now = "Shaping voids" }]);
        check("quick answer: the question, the chat's summary, its last messages and each job's report go with it",
            quick.StartsWith("The user's question:\ndid you extend the piles?") && quick.Contains("piles never joined") && quick.Contains("User: raise the pile heads")
            && quick.Contains("Job 26 (done): Berth 10 pile heads. Reported: 257 piles raised") && quick.Contains("Now: Shaping voids"), quick);
        check("quick answer: answers from what is known, answer first, never changes the model or promises work",
            ChatFocus.QuickInstructions.Contains("Start with the answer") && ChatFocus.QuickInstructions.Contains("Never change the model")
            && ChatFocus.QuickInstructions.Contains("never promise work"), "");
        var told = ChatFocus.Answered([("is the pit joined?", "Yes, all three are joined."), ("did you do berth 9?", "")]);
        check("quick answer: the main chat hears what was asked and answered, and what still needs an answer",
            told.Contains("is the pit joined? They were answered at once: Yes") && told.Contains("did you do berth 9? Not answered yet: answer it first")
            && told.Contains("Don't answer the answered ones again") && ChatFocus.Answered([]).Length == 0, told);
        var quickPane = Read("src/SketchProMax/Chat/ChatPane.Quick.cs");
        check("pane: a question while the chat is busy is answered at once by a read-only run, and the chat hears it next",
            pane.Contains("if (QuickAnswer(_input.Text)) return;") && pane.IndexOf("if (QuickAnswer(_input.Text)) return;", StringComparison.Ordinal) < pane.IndexOf("if (_turn is not null)\n", StringComparison.Ordinal)
            && quickPane.Contains("readTools: true") && pane.Contains("TakeAsides()") && brief.Contains("hint: TakeAsides()"), "");
        check("prompt: questions answered by the chat itself, put-off items come back, practice settles a job's question, sharing work keeps every worker, a 'don't show' holds",
            chat.Contains("Never hand a question to a job") && chat.Contains("stays an open\n          item") && chat.Contains("tell the\n          job the answer")
            && chat.Contains("the named one too") && chat.Contains("holds for the whole chat") && chat.Contains("are not questions: apply them"), "");
        var sync = Read("src/SketchProMax/Revit/AutoSync.cs");
        var prompt = Read("src/SketchProMax/Revit/PositionPrompt.cs");
        check("sync (bug 114): a failing auto sync waits longer each time (2, 4, 8, 16, 30 min) and asks for a sync by hand after 3",
            sync.Contains("Math.Min(30, RetryAfter.TotalMinutes * Math.Pow(2,") && sync.Contains("var later = WaitAfter(_failures);")
            && sync.Contains("_failures >= 3") && sync.Contains("_failures = 0;"), "");
        check("sync: Revit's Location Position Changed prompt is answered Do not save only during Sketch ProMax's own sync or save, and said",
            sync.Contains("PositionPrompt.During(() => { doc.SynchronizeWithCentral(") && Read("src/SketchProMax/Tools/LinkedModelTools.cs").Contains("PositionPrompt.During(() => { doc.Save();")
            && prompt.Contains("if (_depth == 0 ||") && prompt.Contains("DoNotSave = 1002") && Read("src/SketchProMax/App.cs").Contains("PositionPrompt.Watch(app);")
            && sync.Contains("+ Prompted()"), "");
        // ---- 19 Nov 2026 afternoon: "supposedly the work is done and when you check it was not done"
        var notesAsk = ChatFocus.LessonMessage("raise the pile heads", "All 595 piles go 100 mm in, AZ included.", "az piles should stay at soffit",
            ["Lesson: Extend every pile head, including AZ sheet piles, 100 mm into the soffit.", "Clash tolerance 10 mm."]);
        check("lesson: the notes kept so far go with it, numbered, and a changed one is replaced, keeping to the user's words",
            notesAsk.Contains("Notes kept so far:\n1. Lesson: Extend every pile head") && notesAsk.Contains("2. Clash tolerance") && notesAsk.Contains("REPLACES: <its number>")
            && notesAsk.Contains("don't widen it to things they didn't name") && !ChatFocus.LessonMessage("a", "b", "c").Contains("REPLACES"), notesAsk);
        check("lesson: the notes it replaces are read from its answer, in range, once each",
            ChatFocus.ParseReplaces("GLOBAL: AZ sheet piles stop flush with the soffit.\nREPLACES: 1", 2).SequenceEqual([1])
            && ChatFocus.ParseReplaces("GLOBAL: x\nREPLACES: 2, 5, 2", 2).SequenceEqual([2]) && ChatFocus.ParseReplaces("GLOBAL: x", 2).Count == 0, "");
        var reviewTask = JobReview.Task(new ChatJob { Number = 27, Title = "Approach slabs grading", Task = "Grade the approach slabs.", Summary = "Done." },
            "grade them", "- Method agreed: full-length generic model voids, as neat as the reference strip.");
        check("check job: the look and the agreed method count, not only the numbers, and the chat's rules go with it",
            reviewTask.Contains("Check how it was done, not only the numbers") && reviewTask.Contains("a mesh of creases") && reviewTask.Contains("NOT DONE")
            && reviewTask.Contains("The rules, methods and decisions agreed earlier in the chat") && reviewTask.Contains("full-length generic model voids, as neat")
            && reviewTask.Contains("7. Don't change the model") && reviewTask.Contains("A tolerance belongs only to the rule that gives it") && reviewTask.Contains("never ask for two things that can't both hold") && !JobReview.Task(new ChatJob { Number = 1, Title = "t", Task = "x" }).Contains("agreed earlier in the chat"), reviewTask);
        check("practice: AZ sheet piles flush with the soffit, grading with full-length voids not shape points, limits follow the drawing",
            basePrompt.Contains("AZ sheet piles stop flush with the soffit") && basePrompt.Contains("never with dense slab shape points") && basePrompt.Contains("bottom follows its top at the same thickness")
            && basePrompt.Contains("is about positions, not about") && basePrompt.Contains("reaching the limits the drawing gives") && !basePrompt.Contains("sheet piles, combi-wall pipes and their"), "");
        check("prompt: an agreed method is a must in every task with the look in done_when; done only when checked; a corrected note replaces the old",
            chat.Contains("never add \"or another way where neater\"") && chat.Contains("looks as clean as the reference")
            && chat.Contains("Never tell the user a part is done unless the job's check passed") && chat.Contains("memory_note replaces="), "");
        check("memory_note: replaces takes the earlier note out", Read("src/SketchProMax/Tools/MemoryTools.cs").Contains("\"replaces\": {\"type\": \"string\"")
            && Read("src/SketchProMax/Chat/BriefRunner.cs").Contains("foreach (var (k, n) in replaced) MemoryNotes.Remove(sections, k, n);"), "");
        // ---- Task times (19 Nov 2026: "Done in 25 s. By hand: about 12 min. Saved: 12 min. Nothing in the model changed.")
        check("time: a task that changed nothing claims no time saved",
            SketchProMax.Tools.TimeMath.ChatLine(25, 720, 0, 0) == "Answered in 25 s.", SketchProMax.Tools.TimeMath.ChatLine(25, 720, 0, 0) ?? "null");
        check("time: a task that only started jobs says so, their time is on their cards",
            SketchProMax.Tools.TimeMath.ChatLine(25, 720, 0, 2) == "Handed to 2 jobs in 25 s; the work and its time are on their cards.", SketchProMax.Tools.TimeMath.ChatLine(25, 720, 0, 2) ?? "null");
        check("time: a task that changed the model still says what it saved",
            SketchProMax.Tools.TimeMath.ChatLine(180, 2400, 12, 0) == "Done in 3 min. By hand: about 40 min. Saved: 37 min." && SketchProMax.Tools.TimeMath.ChatLine(30, 0, 3, 0) is null, "");
        var answered = new JobCardModel { State = "done", Started = new DateTime(2026, 11, 19, 12, 0, 0), Ended = new DateTime(2026, 11, 19, 12, 2, 0) };
        check("time: a card that changed nothing reads 'Answered in', not 'Done in'", answered.FinalLine() == "Answered in 2 min. Nothing in the model changed.", answered.FinalLine());
        var timeLog = Read("src/SketchProMax/Revit/TimeLog.cs");
        check("time: the task's time runs from the user's message, and nothing is counted saved when nothing changed",
            timeLog.Contains("var shares = changed == 0 ?") && timeLog.Contains("took = (DateTime.UtcNow - asked).TotalSeconds")
            && pane.Contains("EndChat(Revit.JobUndo.ElementsChanged, _askedAt, jobs)") && pane.Contains("_askedAt = DateTime.UtcNow;"), "");
        check("pane: lessons go to the memory notes, global or this model's",
            Read("src/SketchProMax/Chat/BriefRunner.cs").Contains("MemoryNotes.Add(sections, key, lesson.Note)"), "");
    }
}
