using SketchProMax.Chat;

/// <summary>The prompt enhancer's rules: when to brief, reading the planner's JSON, the text a choice runs, learning from edits.</summary>
internal static class PromptBriefTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // When to make a brief
        check("brief: Off never plans", !PromptBriefs.ShouldPlan(BriefMode.Off, "Create walls on every level"), "");
        check("brief: model changes plan by default", PromptBriefs.ShouldPlan(BriefMode.ModelChanges, "Tag all the beams on level 2"), "");
        check("brief: a plain question skips it", !PromptBriefs.ShouldPlan(BriefMode.ModelChanges, "How many doors are on Level 1?"), "");
        check("brief: Always plans questions too", PromptBriefs.ShouldPlan(BriefMode.Always, "How many doors are on Level 1?"), "");
        check("brief: a short reply to Claude skips it", !PromptBriefs.ShouldPlan(BriefMode.Always, "yes, go ahead") && !PromptBriefs.ShouldPlan(BriefMode.ModelChanges, "Continue."), "");
        check("brief: unsure counts as a change", PromptBriefs.ShouldPlan(BriefMode.ModelChanges, "the sheets for the east wing") && PromptBriefs.LooksReadOnly("the sheets for the east wing") is null, "");
        check("brief: a question that acts is planned", PromptBriefs.ShouldPlan(BriefMode.ModelChanges, "Can you tell me the levels and then add a grid?"), "");
        check("brief: multi-step question is planned", PromptBriefs.ShouldPlan(BriefMode.ModelChanges, "List the sheets, then the views on each"), "");
        var read = new PromptBrief { Kind = "read_only" };
        var change = new PromptBrief { Kind = "change" };
        check("brief: card skipped for read-only with Model changes only", !PromptBriefs.ShowCard(BriefMode.ModelChanges, read) && PromptBriefs.ShowCard(BriefMode.ModelChanges, change), "");
        check("brief: card shown for read-only with Always", PromptBriefs.ShowCard(BriefMode.Always, read), "");
        check("brief: unsure kind shows the card", PromptBriefs.ShowCard(BriefMode.ModelChanges, new PromptBrief { Kind = "unsure" }), "");

        // The setting
        check("brief: setting defaults to Model changes only", PromptBriefs.ParseMode(null) == BriefMode.ModelChanges && PromptBriefs.ParseMode("junk") == BriefMode.ModelChanges, "");
        check("brief: setting round-trips", Enum.GetValues(typeof(BriefMode)).Cast<BriefMode>().All(m => PromptBriefs.ParseMode(PromptBriefs.ModeText(m)) == m), "");
        var folder = Path.Combine(Path.GetTempPath(), "spm-brief-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = PromptBriefs.LoadMode(folder);
            PromptBriefs.SaveMode(folder, BriefMode.Off);
            check("brief: setting saved to the chat folder", first == BriefMode.ModelChanges && PromptBriefs.LoadMode(folder) == BriefMode.Off, "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }

        // Reading the planner's answer
        const string answer = """
            Here is the brief:
            ```json
            {"kind":"change","brief":"Create {q1} walls of type \"Generic - 200mm\" between grids A-D on Level 1 (0 mm) and Level 2 (3500 mm).",
             "scope":[{"label":"Levels","value":"Level 1 (0 mm), Level 2 (3500 mm)"},{"label":"Walls now","value":"42"}],
             "units":"mm","tools":["mcp__sketch-promax__create_walls","get_model_context"],
             "assumptions":["Walls go full height, level to level","Grids A-D are the outer frame"],
             "questions":[{"id":"q1","question":"Which walls?","options":["external","internal","both"],"recommended":2},
                          {"id":"bad","question":"Only one option","options":["x"]}]}
            ```
            """;
        var brief = PromptBriefs.Parse(answer, "  add walls ");
        check("brief: JSON read from a fenced answer", brief is { Kind: "change", Units: "mm" } && brief.Request == "add walls" && brief.Scope.Count == 2, brief is null ? "null" : brief.Kind);
        check("brief: tool names lose the mcp prefix", brief?.Tools.SequenceEqual(new[] { "create_walls", "get_model_context" }) == true, string.Join(",", brief?.Tools ?? []));
        check("brief: assumptions kept", brief?.Assumptions.Count == 2, "");
        check("brief: a question needs two options", brief?.Questions.Count == 1 && brief.Questions[0] is { Id: "q1", Recommended: 2, Chosen: -1 }, "");
        check("brief: unknown kind is unsure", PromptBriefs.Parse("""{"kind":"maybe","brief":"x"}""", "x")?.Kind == "unsure", "");
        check("brief: no brief text is no brief", PromptBriefs.Parse("""{"kind":"change"}""", "x") is null && PromptBriefs.Parse("no json here", "x") is null && PromptBriefs.Parse("{broken", "x") is null, "");
        var mapped = PromptBriefs.Parse("""{"type":"read-only","prompt":"List sheets","scope":{"Sheets":"12"},"assumptions":"Only issued sheets"}""", "sheets");
        check("brief: lenient field names and shapes", mapped is { Kind: "read_only", Prompt: "List sheets" } && mapped.Scope.Single().Label == "Sheets" && mapped.Assumptions.Single() == "Only issued sheets", "");

        // The text a choice runs; answering updates the brief
        var composed = PromptBriefs.Compose(brief!);
        check("brief: compose fills the suggested answer", composed.StartsWith("Create both walls") && composed.Contains("Which walls? → both") && composed.Contains("- Walls go full height"), composed);
        check("brief: answering updates the brief", PromptBriefs.Answer(brief!, "q1", 0) && PromptBriefs.Compose(brief!).StartsWith("Create external walls"), "");
        check("brief: a bad answer is refused", !PromptBriefs.Answer(brief!, "q1", 9) && !PromptBriefs.Answer(brief!, "nope", 0), "");
        check("brief: Run as I typed bypasses the brief", PromptBriefs.TextFor(BriefChoice.RunAsTyped, brief!) == "add walls", "");
        check("brief: Run this runs the brief", PromptBriefs.TextFor(BriefChoice.RunBrief, brief!) == PromptBriefs.Compose(brief!), "");
        check("brief: Run edited runs the edit", PromptBriefs.TextFor(BriefChoice.RunEdited, brief!, " my text ") == "my text" && PromptBriefs.TextFor(BriefChoice.RunEdited, brief!, " ") == PromptBriefs.Compose(brief!), "");
        var stored = PromptBriefs.FromStored(PromptBriefs.ToJson(brief!));
        check("brief: kept in history with its answers", stored?.Questions.Single().Chosen == 0 && PromptBriefs.Compose(stored) == PromptBriefs.Compose(brief!) && PromptBriefs.FromStored("{bad") is null, "");

        // The one-shot run's output
        var (text, isError) = PromptBriefs.ResultOf("noise\n{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"{\\\"brief\\\":\\\"x\\\"}\"}\n");
        check("brief: result read from json output", !isError && text == "{\"brief\":\"x\"}", text);
        check("brief: error and missing results", PromptBriefs.ResultOf("""{"type":"result","is_error":true,"errors":["boom"]}""") == ("boom", true) && PromptBriefs.ResultOf("") == ("", true), "");

        // Learning from edits
        var before = "Create 200 mm walls on Level 1.\n\n**Assumptions**\n- Full height";
        var after = "Create 250 mm walls on Level 1.\n\n**Assumptions**\n-   Full height";
        var (removed, added) = PromptBriefs.Diff(before, after);
        check("brief: diff ignores spacing and bullets", removed.SequenceEqual(new[] { "Create 200 mm walls on Level 1." }) && added.SequenceEqual(new[] { "Create 250 mm walls on Level 1." }), string.Join("|", removed) + " / " + string.Join("|", added));
        check("brief: only a real change is an edit", PromptBriefs.IsEdit(before, after) && !PromptBriefs.IsEdit(before, before.Replace("\n", "\r\n") + "\n\n") && !PromptBriefs.IsEdit(before, "  "), "");
        var ask = PromptBriefs.DistilMessage("add walls", before, after);
        check("brief: distil message shows both sides", ask.Contains("- Create 200 mm walls") && ask.Contains("- Create 250 mm walls") && ask.Contains("NONE"), "");
        check("brief: preference note formatted", PromptBriefs.PreferenceNote("\"use 250 mm walls unless told otherwise.\"\nmore") == "Brief preference: Use 250 mm walls unless told otherwise.", PromptBriefs.PreferenceNote("\"use 250 mm walls unless told otherwise.\""));
        check("brief: preference prefix not doubled", PromptBriefs.PreferenceNote("Brief preference: list sheets by number") == "Brief preference: List sheets by number", "");
        check("brief: NONE keeps nothing", PromptBriefs.PreferenceNote("NONE") == "" && PromptBriefs.PreferenceNote("None.") == "" && PromptBriefs.PreferenceNote("  \"\" ") == "" && PromptBriefs.PreferenceNote(null) == "", "");
        check("brief: long note is cut", PromptBriefs.PreferenceNote(new string('a', 1000)).Length == PromptBriefs.MaxNote, "");
        var fallback = PromptBriefs.FallbackNote("add walls", before, after);
        check("brief: fallback note says what changed", fallback.StartsWith(PromptBriefs.NotePrefix) && fallback.Contains("250 mm") && fallback.Contains("200 mm"), fallback);

        // The planner's message carries the conversation's end
        var plan = PromptBriefs.PlanMessage("do the same on level 3", ["plan.pdf"], [("user", "add walls on level 2"), ("assistant", "Done: 12 walls.")]);
        check("brief: plan message has context, request and files", plan.IndexOf("User: add walls", StringComparison.Ordinal) < plan.IndexOf("Assistant: Done", StringComparison.Ordinal) && plan.Contains("do the same on level 3") && plan.Contains("plan.pdf"), plan);
        check("brief: planner tools read only by name", PromptBriefs.PlannerTools.All(t => t.StartsWith("get_") || t.StartsWith("list_") || t.StartsWith("query_") || t.StartsWith("read_")), "");
    }
}
