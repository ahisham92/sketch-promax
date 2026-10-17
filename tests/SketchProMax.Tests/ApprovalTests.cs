using System.Text.Json;
using SketchProMax.Bridge;
using SketchProMax.Tools;

/// <summary>When Revit asks before a call (ApprovalPolicy.Decide), which needs no Revit.</summary>
internal static class ApprovalTests
{
    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    /// <summary>"Make plans and sheets for levels 1-5 and tag the columns", as the chat runs it.</summary>
    internal static List<(string, bool, IReadOnlyDictionary<string, JsonElement>)> TypicalTask()
    {
        var calls = new List<(string, bool, IReadOnlyDictionary<string, JsonElement>)>
        {
            ("get_model_context", false, Args("{}")),
            ("list_view_templates", false, Args("{}")),
            ("create_plan_views", true, Args("""{"level_names":["Level 1","Level 2","Level 3","Level 4","Level 5"]}""")),
            ("apply_view_template", true, Args("""{"view_ids":[1,2,3,4,5],"template_id":9}""")),
            ("create_sheets", true, Args("""{"count":5}""")),
            ("list_types", false, Args("""{"category":"Structural Column Tags"}""")),
        };
        for (var i = 1; i <= 5; i++) calls.Add(("place_views_on_sheet", true, Args($$"""{"sheet_id":{{100 + i}},"view_ids":[{{i}}]}""")));
        for (var i = 1; i <= 5; i++) calls.Add(("tag_elements_in_view", true, Args($$"""{"view_id":{{i}},"category":"Structural Columns"}""")));
        return calls;
    }

    public static void Run(Action<string, bool, string> check)
    {
        var docs = @"C:\Users\me\Documents\SketchProMax";
        string[] roots = [docs, @"C:\Users\me\AppData\Roaming\SketchProMax"];
        var files = new Dictionary<string, PathState>(StringComparer.OrdinalIgnoreCase);
        PathState Probe(string p) => files.TryGetValue(OutputPaths.FullPath(p), out var s) ? s : PathState.Missing;
        files[OutputPaths.FullPath(@"D:\Jobs\report.xlsx")] = PathState.File;
        files[OutputPaths.FullPath(@"D:\Jobs")] = PathState.Folder;
        files[OutputPaths.FullPath(@"D:\Empty")] = PathState.EmptyFolder;

        ApprovalDecision D(string tool, bool modifies, string json, ApprovalLevel level = ApprovalLevel.RiskyOnly, bool chat = true,
            bool task = false, string[]? remembered = null, ApprovalFacts? facts = null, bool learned = false) =>
            ApprovalPolicy.Decide(tool, modifies, learned, Args(json), chat, facts ?? new ApprovalFacts(), level, remembered ?? [], task, Probe, roots);

        // ---- Levels: a normal change
        check("approval: risky only, a normal change runs", !D("create_sheets", true, "{}").Ask, "");
        var every = D("create_sheets", true, "{}", ApprovalLevel.EveryChange);
        check("approval: every change asks a normal change, and it can be silenced", every.Ask && every.Kind == AskKind.Change && every.CanSilence, every.Reason);
        check("approval: once per task asks until the task is approved",
            D("create_sheets", true, "{}", ApprovalLevel.OncePerTask).Ask && !D("create_sheets", true, "{}", ApprovalLevel.OncePerTask, task: true).Ask, "");
        check("approval: an approved task (brief run) silences every change too", !D("create_sheets", true, "{}", ApprovalLevel.EveryChange, task: true).Ask, "");
        check("approval: task approval counts only for chat calls", D("create_sheets", true, "{}", ApprovalLevel.EveryChange, chat: false, task: true).Ask, "");
        check("approval: don't ask again silences that tool only",
            !D("create_sheets", true, "{}", ApprovalLevel.EveryChange, remembered: ["create_sheets"]).Ask
            && D("tag_elements_in_view", true, "{}", ApprovalLevel.EveryChange, remembered: ["create_sheets"]).Ask, "");

        // ---- Reads never ask, at any level
        var readsAsk = ApprovalPolicy.Levels.Where(l => D("get_elements", false, "{}", l).Ask || D("export_pdf", false, """{"folder":""}""", l).Ask).ToList();
        check("approval: reads never ask", readsAsk.Count == 0, string.Join(", ", readsAsk));

        // ---- Always asked, whatever the level, the task or don't ask again
        foreach (var (tool, json) in new[] { ("what_if", """{"confirm":true}"""), ("issue_check", """{"accept":[{"key":"k","reason":"r"}]}"""), ("issue_drawings", """{"allow_open_items":true}""") })
        {
            var d = D(tool, true, json, ApprovalLevel.RiskyOnly, task: true, remembered: [tool]);
            check($"approval: {tool} is always asked and can't be silenced", d.Ask && d.Kind == AskKind.Always && !d.CanSilence, d.Reason);
        }
        check("approval: what_if without confirm is a plain read", !D("what_if", false, """{"confirm":false}""", ApprovalLevel.EveryChange).Ask, "");

        // ---- Risky: asked at every level, even in an approved task or after don't ask again
        ApprovalDecision R(string tool, bool modifies, string json, ApprovalFacts? facts = null, bool chat = true, bool learned = false) =>
            D(tool, modifies, json, ApprovalLevel.RiskyOnly, chat: chat, task: true, remembered: [tool], facts: facts, learned: learned);
        bool Risky(ApprovalDecision d) => d.Ask && d.Kind == AskKind.Risky && !d.CanSilence;
        check("approval: chat code is risky (query and change)", Risky(R("run_revit_query_code", false, """{"code":"return 1;"}""")) && Risky(R("run_revit_code", true, """{"code":"x"}""")), "");
        check("approval: code from the Claude app follows the level (it has its own prompts)", !R("run_revit_code", true, """{"code":"x"}""", chat: false).Ask, "");
        check("approval: a learned tool with new code is risky, approved code is not",
            Risky(R("my_tool", false, "{}", new ApprovalFacts { NewCode = true }, learned: true)) && !R("my_tool", false, "{}", learned: true).Ask, "");
        check("approval: a recipe with new steps is risky, a known one is not",
            Risky(R("run_recipe", true, """{"name":"x"}""", new ApprovalFacts { NewCode = true })) && !R("run_recipe", true, """{"name":"x"}""").Ask, "");
        check("approval: a delete that takes other elements is risky, a plain delete is not",
            Risky(R("delete_elements", true, """{"element_ids":[1]}""", new ApprovalFacts { DeleteTakes = "It also deletes 2 Doors." }))
            && !R("delete_elements", true, """{"element_ids":[1]}""").Ask, "");
        var ve = R("ve_apply", true, """{"target":"main","confirm_main_model":true}""");
        check("approval: ve_apply on the open model is risky, from anywhere", Risky(ve) && Risky(R("ve_apply", true, """{"target":"main"}""", chat: false)), ve.Reason);
        check("approval: ve_apply on a design option or a copy is a normal change",
            !R("ve_apply", true, """{"target":"design_option","design_option":"VE"}""").Ask && !R("ve_apply", true, """{"target":"copy"}""").Ask, "");
        check("approval: a recipe saved over an existing one is risky", Risky(R("save_recipe", false, """{"name":"x"}""", new ApprovalFacts { Overwrites = "the saved recipe 'x'" })), "");
        check("approval: a new recipe saves without asking", !R("save_recipe", false, """{"name":"x"}""").Ask, "");

        // ---- Files: Sketch ProMax's folders and new files never ask; overwriting outside them does
        check("approval: the default folder (empty) never asks", !R("export_project_pack", false, """{"folder":""}""").Ask, "");
        check("approval: a file in Documents\\SketchProMax never asks, even over an old one",
            !R("export_schedule", false, $$"""{"file_name":"{{docs.Replace("\\", "\\\\")}}\\report.xlsx"}""").Ask, "");
        check("approval: a new file outside our folders doesn't ask", !R("export_schedule", false, """{"file_name":"D:\\Jobs\\new.xlsx"}""").Ask, "");
        check("approval: a new or empty folder doesn't ask",
            !R("export_pdf", false, """{"folder":"D:\\New"}""").Ask && !R("export_pdf", false, """{"folder":"D:\\Empty"}""").Ask, "");
        var over = R("export_schedule", false, """{"file_name":"D:\\Jobs\\report.xlsx"}""");
        check("approval: overwriting a file outside our folders is risky, and names it", Risky(over) && over.Reason.Contains("report.xlsx"), over.Reason);
        check("approval: a folder with files in it outside ours is risky", Risky(R("export_pdf", false, """{"folder":"D:\\Jobs"}""")), "");
        check("approval: save_legend_seed's path is where it writes", Risky(R("save_legend_seed", false, """{"path":"D:\\Jobs\\report.xlsx"}""")), "");
        check("approval: a template path is read, not written", !R("create_sheets", true, """{"template_path":"D:\\Jobs\\report.xlsx"}""").Ask, "");
        check("approval: a folder that only starts like ours is outside", !ApprovalPolicy.Inside(docs + "Old\\a.pdf", roots) && ApprovalPolicy.Inside(docs + "\\sub\\a.pdf", roots)
            && ApprovalPolicy.Inside(docs.ToUpperInvariant(), roots), "");

        // ---- How many dialogs the typical task shows
        var task = TypicalTask();
        var before = ApprovalPolicy.CountAsks(task, ApprovalLevel.EveryChange, false);
        var once = ApprovalPolicy.CountAsks(task, ApprovalLevel.OncePerTask, false);
        var risky = ApprovalPolicy.CountAsks(task, ApprovalLevel.RiskyOnly, false);
        var brief = ApprovalPolicy.CountAsks(task, ApprovalLevel.EveryChange, true);
        check($"approval: typical task asks {before} times at every change, {once} once per task, {risky} risky only, {brief} after a brief",
            before == 13 && once == 1 && risky == 0 && brief == 0, $"{before} {once} {risky} {brief}");
        var withCode = task.Prepend(("run_revit_query_code", false, (IReadOnlyDictionary<string, JsonElement>)Args("""{"code":"x"}"""))).ToList();
        check("approval: chat code still asks after a brief", ApprovalPolicy.CountAsks(withCode, ApprovalLevel.RiskyOnly, true) == 1, "");

        // ---- The setting
        check("approval: settings written before the levels read as risky only", ApprovalPolicy.Parse(null) == ApprovalLevel.RiskyOnly && ApprovalPolicy.Parse("") == ApprovalLevel.RiskyOnly, "");
        check("approval: every level stores and reads back", ApprovalPolicy.Levels.All(l => ApprovalPolicy.Parse(ApprovalPolicy.Store(l)) == l), "");
        check("approval: friendly tool names", ApprovalPolicy.Friendly("create_plan_views") == "Create plan views", ApprovalPolicy.Friendly("create_plan_views"));
        var control = new JobControl();
        check("approval: a new task starts unapproved and can be approved", !control.ApprovedForTask && (control.ApprovedForTask = true) && control.ApprovedForTask, "");
    }
}
