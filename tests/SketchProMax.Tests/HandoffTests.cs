using SketchProMax.Tools;

/// <summary>The hand-off loop (Continue, Skip this item, Stop), the static dimension wording and the shipped guides, which need no Revit.</summary>
internal static class HandoffTests
{
    public static void Run(Action<string, bool, string> check)
    {
        static Task<object?> Inline(Func<object?> work) => Task.FromResult(work());
        static HandoffItem Item(string what, Func<bool>? recheck = null, Func<object?>? resume = null) =>
            new(new HandoffStep { What = what, Where = "here", Why = "because", Recheck = recheck }, resume);
        List<HandoffOutcome> Loop(IReadOnlyList<HandoffItem> items, Func<HandoffStep, string?, HandoffChoice> respond, Func<bool>? stop = null) =>
            HandoffLoop.RunAsync(items, null, respond, Inline, stop ?? (() => false), v => v as string == "error" ? "it failed" : null).GetAwaiter().GetResult();

        // ---- Continue re-checks, and asks again with what is missing until it passes
        var done = false;
        var notes = new List<string?>();
        var resumed = 0;
        var outcomes = Loop([Item("place it", () => done, () => ++resumed)], (_, note) =>
        {
            notes.Add(note);
            if (notes.Count == 2) done = true; // the user does it before the second Continue
            return HandoffChoice.Continue;
        });
        check("handoff: continue re-checks and resumes once done", outcomes is [{ Done: true, Choice: "continued" }] && resumed == 1 && Equals(outcomes[0].Value, 1),
            string.Join(" | ", outcomes));
        check("handoff: asked again saying it is not done yet", notes.Count == 2 && notes[0] is null && notes[1]?.StartsWith("Not done yet: place it") == true, string.Join(" | ", notes));

        // ---- Skip moves on to the next item; Stop ends the rest
        var asked = new List<string>();
        outcomes = Loop([Item("one"), Item("two"), Item("three"), Item("four")], (step, _) =>
        {
            asked.Add(step.What);
            return step.What switch { "one" => HandoffChoice.Skip, "two" => HandoffChoice.Continue, _ => HandoffChoice.Stop };
        });
        check("handoff: skip, continue, stop and the rest stopped",
            outcomes.Select(o => o.Choice).SequenceEqual(["skipped", "continued", "stopped", "stopped"]) && outcomes[1].Done && !outcomes[3].Done,
            string.Join(" | ", outcomes.Select(o => o.Choice)));
        check("handoff: nothing is asked after stop", asked.SequenceEqual(["one", "two", "three"]), string.Join(",", asked));
        check("handoff: summary counts each ending", HandoffLoop.Summary(outcomes) == "1 done, 1 skipped, 2 stopped", HandoffLoop.Summary(outcomes));

        // ---- A resume that fails (throws or returns an error) is asked again, and the user can't get stuck
        var tries = 0;
        outcomes = Loop([Item("fix it", null, () => { tries++; throw new InvalidOperationException("Revit refused"); })], (_, _) => HandoffChoice.Continue);
        check("handoff: a failing resume is retried at most MaxTries times", tries == HandoffLoop.MaxTries && outcomes is [{ Done: false }] && outcomes[0].Error!.Contains("Revit refused"),
            $"{tries} {outcomes[0].Error}");
        outcomes = Loop([Item("fix it", null, () => "error")], (_, note) => note is null ? HandoffChoice.Continue : HandoffChoice.Skip);
        check("handoff: an error result counts as not done", outcomes is [{ Choice: "skipped" }], string.Join(" | ", outcomes));

        // ---- Cancelling the task (cancel_job) stops before asking
        outcomes = Loop([Item("a"), Item("b")], (_, _) => HandoffChoice.Continue, () => true);
        check("handoff: a cancelled task stops every item", outcomes.All(o => o.Choice == "stopped"), string.Join(" | ", outcomes));

        // ---- The UI path: a request waits for an answer from the window or the card, and closes
        HandoffRequest? shown = null;
        var run = HandoffLoop.RunAsync([Item("wait for me")], r => shown = r, null, Inline, () => false);
        var spin = 0;
        while (shown is null && spin++ < 100) Thread.Sleep(10);
        var waiting = shown?.State == HandoffState.Waiting;
        var answered = shown?.Answer(HandoffChoice.Skip) == true;
        var twice = shown?.Answer(HandoffChoice.Continue) == true;
        var finished = run.Wait(5000);
        check("handoff: a shown request waits, takes one answer and closes", waiting && answered && !twice && finished && shown!.State == HandoffState.Closed && shown.Outcome == "Skipped",
            $"{waiting} {answered} {twice} {finished} {shown?.State} {shown?.Outcome}");

        var thrown = new ManualStepException(new HandoffStep { What = "Load the family", Where = "Insert tab", Why = "It is not loaded." });
        check("handoff: a manual step is an argument problem with the whole step as its message",
            thrown is ArgumentException && thrown.Message == "Load the family. Where: Insert tab. Why: It is not loaded.", thrown.Message);

        // ---- Static dimension stand-ins say they are static
        check("static dimension: segment text says static", StaticDimensionText.Segment(" 1250 ") == "1250 (static)", StaticDimensionText.Segment(" 1250 "));
        var note = StaticDimensionText.Note(42, "Section 1");
        check("static dimension: the note says it is not live and won't update", note.Contains("STATIC") && note.Contains("not a live dimension") && note.Contains("does not update") && note.Contains("42"), note);
        var spans = StaticDimensionText.Spans([(0, 2), (2.5, 3)]);
        check("static dimension: segments span from their centres", spans.SequenceEqual([(-1.0, 1.0), (1.0, 4.0)]), string.Join(",", spans));
        check("static dimension: paper mm at 1:50 in feet", Math.Abs(StaticDimensionText.PaperToModel(3.048, 50) - 0.5) < 1e-12, StaticDimensionText.PaperToModel(3.048, 50).ToString());

        // ---- The in-place guide ships inside the add-in and reads as steps
        var guide = HelpGuides.Read(HelpGuides.InPlaceToLoadable);
        check("guide: in-place to loadable is embedded", guide is not null && HelpGuides.Names.Contains(HelpGuides.InPlaceToLoadable), string.Join(",", HelpGuides.Names));
        var steps = HelpGuides.Steps(guide ?? "");
        check("guide: numbered steps, in order, with no Markdown marks",
            steps.Count >= 10 && steps[0].StartsWith("1. ") && steps[1].StartsWith("2. ") && steps.All(s => !s.Contains("**") && !s.Contains('`')),
            $"{steps.Count}: {string.Join(" | ", steps.Take(3))}");
        check("guide: indented lines fold into their step", steps.Any(s => s.StartsWith("3. ") && s.Contains("Metric Generic Model.rft")), steps.ElementAtOrDefault(2) ?? "");
        check("guide: title", HelpGuides.Title(guide ?? "") == "Convert an in-place family to a loadable family", HelpGuides.Title(guide ?? ""));
        check("guide: unknown name is null", HelpGuides.Read("no-such-guide") is null, "");
    }
}
