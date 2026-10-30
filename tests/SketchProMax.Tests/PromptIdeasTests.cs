using System.Text.RegularExpressions;
using SketchProMax.Chat;

/// <summary>
/// What the 2026-10-30 review of the Claude Code prompt library added: jobs with a finish line they check, prompt blanks
/// filled from the open model, pictures compared with the result, "too much" undone in part, documents like past ones.
/// </summary>
internal static class PromptIdeasTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Blanks in the home screen's prompts
        const string prompt = "Columns on {level} along grid {grid}, then tag {selection} and keep {placeholder}; again on {level}.";
        var slots = PromptSlots.In(prompt).Select(s => s.Name).ToList();
        check("slots: known blanks in order, once each, unknown braces ignored", slots.SequenceEqual(["level", "grid", "selection"]), string.Join(",", slots));
        check("slots: the selection is never asked", PromptSlots.ToPick(prompt).Select(s => s.Name).SequenceEqual(["level", "grid"]), "");
        var filled = PromptSlots.Fill(prompt, new Dictionary<string, string> { ["level"] = "Level 2" });
        check("slots: a picked blank takes the model's name everywhere, others read as words to type over, other braces stay",
            filled == "Columns on Level 2 along grid [grid], then tag the elements selected in Revit and keep {placeholder}; again on Level 2.", filled);
        var tidy = PromptSlots.Tidy(["Level 10", "Level 2", " ", "level 2", "B", "A"]);
        check("slots: names sorted with numbers as numbers, blanks and repeats dropped", tidy.SequenceEqual(["A", "B", "Level 2", "Level 10"]), string.Join(",", tidy));
        check("slots: levels keep their elevation order", PromptSlots.Tidy(["Roof", "Ground"], keepOrder: true).SequenceEqual(["Roof", "Ground"]), "");
        check("slots: at most the cap", PromptSlots.Tidy(Enumerable.Range(1, 100).Select(i => "G" + i)).Count == PromptSlots.MaxChoices, "");

        // ---- A job's finish line
        var task = JobBoard.WithDoneWhen("Make sheets for levels 1-5.", "each sheet has a plan view and a filled title block");
        check("done when: added after the task under its heading", task.StartsWith("Make sheets for levels 1-5.\n\n" + JobBoard.DoneWhenHeading) && task.EndsWith("filled title block"), task);
        check("done when: none given, or already there, leaves the task as it is",
            JobBoard.WithDoneWhen("x", " ") == "x" && JobBoard.WithDoneWhen(task, "again") == task && JobBoard.WithDoneWhen("", "y") == "", "");
    }

    public static void RunSources(string root, Action<string, bool, string> check)
    {
        string Read(string path) => File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n");
        var cli = Read("src/SketchProMax/Chat/ClaudeCli.cs");
        var system = Read("src/SketchProMax/Agent/SystemPrompt.cs");
        var jobs = Read("src/SketchProMax/Tools/ChatJobsTool.cs");
        check("done when: the coordinator gives each job one, the tool takes it for a job and each part",
            cli.Contains("its done_when") && Regex.Matches(jobs, "\"done_when\"").Count >= 2 && Regex.Matches(jobs, @"JobBoard\.WithDoneWhen\(").Count == 2, "");
        check("done when: a job checks its finish line before it reports, writing its own when it has none",
            cli.Contains("Finish line:") && cli.Contains("write your own") && cli.Contains("each\n          finish condition and whether it held"), "");
        check("prompt: work from a picture is compared with the result", system.Contains("capture_view_image of the result and compare it with"), "");
        check("prompt: \"too much\" puts back only the extra changes", cli.Contains("you did too much") && cli.Contains("put only those\n          back"), "");
        check("prompt: documents follow the office's past ones, read with read_document_text",
            cli.Contains("\"Like our past ones\"") && Regex.Matches(cli, "read_document_text").Count >= 2, "");

        var prompts = Read("src/SketchProMax/Chat/chat-prompts.json");
        var blanks = Regex.Matches(prompts, @"\{([a-z_]+)\}").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
        check("slots: the home prompts use blanks, and only known ones", blanks.Contains("level") && blanks.Contains("berth") && blanks.Contains("sheet_set")
            && blanks.All(b => PromptSlots.Find(b) is not null), string.Join(",", blanks));
        var home = Read("src/SketchProMax/Chat/HomeView.cs");
        check("slots: the home screen fills them from the model through Revit's dispatcher, with a time limit",
            home.Contains("PromptSlots.Fill(") && home.Contains("Task.Delay(ChoicesWaitMs)") && Read("src/SketchProMax/Chat/ChatPane.cs").Contains("Revit.PromptChoices.Read"), "");
    }
}
