using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// What the field test proves, in one table, so nothing is missed silently. Every registered tool is "tested" (a
/// self-test case the field test runs, or one of <see cref="FieldSteps"/>) or listed in <see cref="ManualTools"/>; every
/// non-tool feature is an entry in <see cref="Features"/>, either tested (by a probe in FieldTest.Features.cs, or by a
/// tool's case) or manual with the reason and the steps a person follows in the guided checklist; every ribbon and pane
/// command maps to a feature in <see cref="Ribbon"/>. The unit tests fail when a tool, a ribbon command or a feature has
/// no entry, and the report's coverage section is built from the registry against this table.
///
/// To extend: a new tool needs a self-test case (SelfTest.cs) or a <see cref="ManualTools"/> row; a new ribbon or pane
/// command needs a <see cref="Ribbon"/> row naming a feature; a new feature needs a <see cref="Features"/> row. No Revit here.
/// </summary>
internal static class FieldTestCoverage
{
    /// <summary>How a feature is covered: Probe (FieldTest.Features.cs checks it), Case (a tool's self-test case checks it), Manual (a person, in the checklist).</summary>
    internal enum How { Probe, Case, Manual }

    /// <param name="Key">Unique, e.g. "chat.history". A Probe's key appears as a string in FieldTest.Features.cs.</param>
    /// <param name="Area">The checklist and report group.</param>
    /// <param name="By">Case: the tool whose case checks it. Otherwise empty.</param>
    /// <param name="Reason">Manual: why it can't be automated. Probe/Case: what is checked.</param>
    /// <param name="Steps">Manual: what the person does, where in Revit. Empty otherwise.</param>
    internal sealed record Feature(string Key, string Area, string Title, How How, string By, string Reason, string Steps = "");

    /// <summary>Tools the field test runs itself, outside the self-test cases (they open documents or are the tests).</summary>
    public static readonly string[] FieldSteps = ["batch_run_recipe", "run_self_test", "self_test_status", "run_field_test"];

    /// <summary>Tools no test can run, with the reason. Each also becomes a checklist step. Empty: every tool runs.</summary>
    public static readonly Dictionary<string, (string Reason, string Steps)> ManualTools = new(StringComparer.Ordinal);

    public const string Chat = "Chat", RibbonArea = "Ribbon and favourites", Carbon = "Embodied carbon", Ve = "Value engineering",
        Rebar2D = "2D rebar", Bugs = "Report bug and exports", Updates = "Updates and the Claude app", Model = "Real-model checks",
        Limits = "Revit limits", BugFixes = "Bug fixes", Tasks = "Hand-off and Cancel", Time = "Time saved";

    public static readonly Feature[] Features =
    [
        // ---- Chat
        new("chat.cli", Chat, "Claude Code is found", How.Probe, "", "finds claude.exe where the chat looks for it and reads its version"),
        new("chat.signin", Chat, "Claude Code sign-in state", How.Probe, "", "runs `claude auth status` and reports signed in, signed out or unknown"),
        new("chat.roundtrip", Chat, "A short chat round-trip", How.Probe, "", "sends one short message through Claude Code when the run asks for it (chat_round_trip); it uses your Claude plan, so it is off by default and the checklist covers it"),
        new("chat.models", Chat, "Model list and saved choice", How.Probe, "", "the model picker's list has the default, and a choice is saved and read back"),
        new("chat.history", Chat, "Chat history save and load", How.Probe, "", "a chat saved to a temporary history folder is listed, summarised and deleted"),
        new("chat.prompts", Chat, "Saved prompts", How.Probe, "", "a prompt is added, saved, read back and removed in a temporary file"),
        new("chat.notes", Chat, "Notes read and write", How.Probe, "", "notes are parsed, added, written and read back in memory; your notes file is only read"),
        new("brief.plan", Chat, "Prompt enhancer: a brief before anything runs", How.Probe, "", "an ambiguous request is planned (the real planning run) into a brief with assumptions and questions, and the model's element count is the same before and after; it uses your Claude plan, so it runs only with chat_round_trip and the checklist covers it otherwise"),
        new("brief.choose", Chat, "Prompt enhancer: Run as I typed and a remembered edit", How.Probe, "", "on a brief card in the pane, Run as I typed sends exactly the typed text; an edited brief is remembered as a Global note, which is then removed; with chat_round_trip only"),
        new("brief.ui", Chat, "The brief card in the chat", How.Manual, "", "the card's look and its one-tap questions are judged on screen, and planning uses your Claude plan",
            "Open Chat and turn briefs on in the model menu. Send \"make the walls thicker\": a brief card lists the scope, assumptions and one-tap questions, and nothing in the model changes before you choose. Tap an answer, then Run this. Send it again and press Run as I typed: your words are sent unchanged. Send it once more, press Edit, change a line and run it: \"Remembered for later briefs\" appears; delete that note in the Notes tab."),
        new("chat.ui", Chat, "Chat in the pane", How.Manual, "", "streaming, scrolling and the drawer are what you see on screen; a test can't judge them",
            "Click Chat on the Sketch ProMax tab. Send \"What is in this model?\" and watch the reply stream in. Open the history drawer, search, pin and rename a chat. Switch the model in the picker. Save a prompt from the home screen and use it. Open the Notes tab, add a note and check Claude uses it."),

        // ---- Ribbon and favourites
        new("ribbon.registered", RibbonArea, "Ribbon buttons registered and enabled", How.Probe, "", "every button, drop-down and entry the core declares is on the Sketch ProMax tab and enabled"),
        new("ribbon.dialogs", RibbonArea, "Tool option windows open with saved values", How.Probe, "", "every ribbon tool's options window builds, and a value typed last time is filled in again"),
        new("favourites", RibbonArea, "A favourite added and removed", How.Probe, "", "a ribbon tool is pinned to My tools, found there, and unpinned again"),
        new("ribbon.ui", RibbonArea, "Ribbon look and favourites in use", How.Manual, "", "button labels, icons and the My tools slots are judged by eye",
            "On the Sketch ProMax tab, check every panel shows its labels and icons (narrow the Revit window too). Open Favourites, pin two tools, reorder them and run one from My tools. Unpin them. Open Settings, change nothing, and close it."),
        new("ribbon.status", RibbonArea, "Claude status, Copy prompt, Send view, pane", How.Manual, "", "these show dialogs, use the clipboard or depend on the Claude app running",
            "Click the Claude status button and read the dialog. Click Copy prompt and paste into Notepad. Open a plan, click Send view and check the picture is attached in the chat. Use More > Show or hide pane twice."),
        new("ribbon.job", RibbonArea, "Job and Update buttons", How.Manual, "", "they show live state that only exists while a job or update runs",
            "Start a long tool (e.g. Model check), watch the Job button show progress, click it and stop the job. Click Updates and read the version and last check."),
        new("help", RibbonArea, "Help page", How.Manual, "", "opens a web page in your browser", "Help > Help and getting started: the guide opens in the browser."),
        new("diagnostics", RibbonArea, "Self-test, field test and the test checklist", How.Probe, "", "the field test runs the self-test (safe mode) on its own project; this report is the field test"),

        new("checklist.build", RibbonArea, "The guided test checklist builds and saves", How.Probe, "", "the checklist lists every manual item plus the fixed bug rows, and its result file is saved (to a temporary folder)"),

        // ---- Hand-off and Cancel
        new("handoff.continue", Tasks, "Hand-off: Continue re-checks and resumes", How.Probe, "", "copy_dimensions on a cut-edge dimension hands the step over; Continue before it is done is asked again, Continue after it is done passes the re-check, and the result's handoff section and the recent hand-offs say done"),
        new("handoff.skip_stop", Tasks, "Hand-off: Skip this item and Stop", How.Probe, "", "add_legend_components with no component to copy hands over and Skip is reported; on three steps, Skip, then Stop, and the third is not asked and counts as stopped"),
        new("handoff.static", Tasks, "copy_dimensions static stand-ins", How.Probe, "", "copy_dimensions with static_fallback draws a detail line and a \"(static)\" value note for a cut-edge dimension instead of handing it over"),
        new("handoff.ui", Tasks, "The hand-off window and chat card", How.Manual, "", "the window, the chat card and doing the step by hand are what a person sees and does",
            "On a _detached copy with no legend component in any legend (and no legend seed in Settings): ask Claude in the chat to add legend components for two wall types. A card shows what to do, where in Revit and why, with Continue, Skip this item and Stop. Press Continue first: it says not done yet. Place one legend component by hand (Annotate > Component > Legend Component), press Continue: the task carries on. Run Add legend components from the ribbon too: the same step opens in a window. Press Stop there and check the result says you stopped."),
        new("cancel.fast", Tasks, "Cancel stops a heavy tool part-way", How.Probe, "", "place_many on 60 placements is asked to stop after the first chunk; it stops early, keeps what it did and says so in its result"),

        // ---- Time saved
        new("time.tracking", Time, "Working time, AI time and time saved are counted", How.Probe, "", "a scripted session (activity, idle beyond the threshold, AI minutes) counts what is expected, AI plus by hand equals working time with nothing counted twice, and it is in the Done in line, the four Project Information parameters, Extensible Storage and the PC log"),
        new("time.export", Time, "Time saved export to Excel", How.Probe, "", "the model's summary is written as the Excel file the Time saved window exports (to a temporary folder) and read back"),
        new("time.queue", Time, "Time waits when a colleague holds the record", How.Probe, "", "with Project Information and the time records owned by someone else (a test hook), the parameters are left alone and the time is kept on this PC; once free, the next write brings it in"),
        new("time.ui", Time, "Time saved window and rates", How.Manual, "", "the window, the rates and a colleague's real ownership need a person (and a second user)",
            "Run a tool from the ribbon (e.g. Tag elements) and read the \"Done in…, By hand: about…, Saved:…\" line. Click Time saved: check the cards, the per tool, per day and per user tables, and Export to Excel. In Settings change one time rate, save and reopen it. In a workshared copy where a colleague has Project Information borrowed, run a tool: the Time saved window says some time is kept on this PC until later."),

        // ---- Carbon, VE, 2D rebar
        new("carbon.mix", Carbon, "Carbon with and without the mix in the material name", How.Case, "embodied_carbon", "a slab whose material names the mix and one whose doesn't (assumed CEM I) are both assessed"),
        new("carbon.real", Carbon, "Carbon on a real model", How.Manual, "", "the factors and mapping need an engineer's eye on a real project",
            "On a _detached copy: Check > Embodied carbon > Embodied carbon. Open the Excel file; check the unmapped list and the top contributors make sense. Run Compare options with 50% GGBS and Carbon 3D view."),
        new("ve.apply_copy", Ve, "VE apply on a copy", How.Probe, "", "ve_apply target copy runs in a non-workshared copy the field test opened, and the saved copy is checked"),
        new("ve.workshared", Ve, "VE apply refuses to copy a workshared model", How.Case, "ve_apply", "on the workshared test project, target copy is refused with a clear message"),
        new("ve.design_option", Ve, "VE apply on a design option", How.Manual, "", "the Revit API can't create design options, so the test project has none",
            "On a _detached copy: Manage > Design Options, make an option set with an option and copy a slab into it. Check > Value engineering > Apply to a copy with target design_option and the option's name. Check only the option's slab changed."),
        new("ve.real", Ve, "VE on a real model", How.Manual, "", "savings need an engineer's judgement",
            "On a _detached copy: Check > Value engineering > VE opportunities, then Compare systems and VE register. Read the ranked list and the Excel register."),
        new("rebar2d", Rebar2D, "2D rebar with detail families", How.Case, "place_additional_reinforcement", "additional bars, mesh symbols and column section details are placed with families the field test makes"),
        new("rebar2d.real", Rebar2D, "2D rebar with your office families", How.Manual, "", "needs the office's own detail families from your template",
            "On a _detached copy with your template's detail families: Reinforcement > 2D rebar families, then Additional bars (2D) on a slab plan, Mesh symbols (2D) and Column sections (2D). Check the drawings look right."),

        // ---- Report bug and exports
        new("bugs.report", Bugs, "Report bug zip size and contents", How.Case, "report_bug", "the zip is written to a temporary Bugs folder, is small and holds the description, activity and versions"),
        new("bugs.export", Bugs, "Export chats & bugs zip", How.Case, "export_chats_and_bugs", "the zip holds the (test) chats, reports and versions and is small"),
        new("pack.export", Bugs, "Export for Claude zip", How.Case, "export_project_pack", "the project pack zip is written, is small and holds the project data"),
        new("bugs.sync", Bugs, "Bug log sync and \"Not sent yet\"", How.Probe, "", "the published bug log downloads, and a report it doesn't list shows under \"Not sent yet\""),
        new("bugs.ui", Bugs, "Report bug, Bug folder and Export chats from the ribbon", How.Manual, "", "the buttons ask questions and open Explorer",
            "Click Report bug, type a line and send it; the Bugs folder opens with the zip. Click Bug folder. Click Export chats, pick Today, and check the zip in Exports. Click Export for Claude and check the zip opens."),
        new("exports.ui", Bugs, "Export PDF, list of drawings and quantities", How.Manual, "", "the files are opened and read by a person",
            "On a _detached copy: Issue > Export PDF for two sheets, List of drawings and Quantities / BOQ. Open each file."),

        // ---- Updates and the Claude app
        new("update.endpoint", Updates, "Update check endpoint", How.Probe, "", "the releases page answers and names a version"),
        new("update.version", Updates, "Version compare", How.Probe, "", "this version and the latest release compare the way the Update button shows them"),
        new("update.loader", Updates, "Loader path and version", How.Probe, "", "the loader that started this core is found, with its file version and contract"),
        new("claude.msix", Updates, "Microsoft Store Claude app detection", How.Probe, "", "looks for the Store (MSIX) Claude app's package and config folder, and the normal install"),
        new("claude.open", Updates, "Open Claude launches the app", How.Manual, "", "starting the desktop app is visible only to you",
            "Close the Claude desktop app, then More > Open Claude. The app starts (also for the Microsoft Store version)."),

        // ---- Real-model checks
        new("model.joined_faces", Model, "Hosting on faces of joined elements", How.Case, "place_void_cuts", "a void is hosted on the top face of a slab joined to a beam"),
        new("model.void_rebuild", Model, "Clash check void rebuild", How.Case, "find_clashes", "a cutting void family is rebuilt as a solid in a clash run"),
        new("model.real_hosting", Model, "Hosting and voids on your model", How.Manual, "", "joined geometry on real projects varies more than a test project can show",
            "On a _detached copy: place a void cut on a slab joined to beams (Clashes > Void cuts) and run a clash check with voids on. Check the void cuts and is reported."),

        // ---- Revit limits
        new("matchlines.trim", Limits, "Trimming a real matchline", How.Manual, "", "the Revit API can't create a matchline, so the field test only checks the tool on a dependent-view pair with none",
            "On a _detached copy with dependent views and a matchline: ask Claude to trim the matchlines in a plan (trim_matchlines). The redrawn lines stop at the crop."),
    ];

    /// <summary>
    /// Every ribbon and pane command (and pane tab, as "tab:name") the core declares, with the feature it belongs to.
    /// Commands "tool:&lt;name&gt;" are covered by the tool itself and by ribbon.dialogs; "fav:N" slots by favourites.
    /// </summary>
    public static readonly Dictionary<string, string> Ribbon = new(StringComparer.Ordinal)
    {
        ["chat"] = "chat.ui", ["status"] = "ribbon.status", ["send-view"] = "ribbon.status", ["copy-prompt"] = "ribbon.status",
        ["open-claude"] = "claude.open", ["pane"] = "ribbon.status", ["export-pack"] = "pack.export", ["job-button"] = "ribbon.job",
        ["update-button"] = "ribbon.job", ["report-bug"] = "bugs.ui", ["bug-folder"] = "bugs.ui", ["export-chats-bugs"] = "bugs.export",
        ["settings"] = "ribbon.ui", ["help"] = "help", ["self-test"] = "diagnostics", ["field-test"] = "diagnostics",
        ["test-checklist"] = "diagnostics", ["time-saved"] = "time.ui", ["favourites"] = "favourites", ["fav:*"] = "favourites", ["tab:notes"] = "chat.notes",
    };

    /// <summary>The coverage key of a manifest line, or null when it runs nothing (drop-downs, hidden items, tool entries).</summary>
    internal static string? RibbonKey(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 3) return null;
        if (f[0] == "tab") return "tab:" + f[2];
        if (f[0] is not ("button" or "item" or "pane") || f.Length < 5 || f[4].Length == 0) return null;
        var command = f[4];
        if (command.StartsWith("tool:", StringComparison.Ordinal)) return null;
        return Regex.IsMatch(command, @"^fav:\d+$") ? "fav:*" : command;
    }

    /// <summary>Where a tool stands: "tested", "manual (checklist)" or null when the table has nothing for it.</summary>
    internal static string? ToolStatus(string tool, ICollection<string> caseTools) =>
        caseTools.Contains(tool) || FieldSteps.Contains(tool) ? "tested" : ManualTools.ContainsKey(tool) ? "manual (checklist)" : null;

    /// <summary>What the unit test checks: every gap, one line each (empty when coverage is complete).</summary>
    internal static List<string> Gaps(IEnumerable<string> tools, ICollection<string> caseTools, IEnumerable<string> manifestLines, string probeSource)
    {
        var gaps = new List<string>();
        foreach (var t in tools)
            if (ToolStatus(t, caseTools) is null) gaps.Add($"tool {t}: no field-test case and no ManualTools row");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Features)
        {
            if (!keys.Add(f.Key)) gaps.Add($"feature {f.Key}: listed twice");
            if (f.How == How.Probe && probeSource.IndexOf("\"" + f.Key + "\"", StringComparison.Ordinal) < 0) gaps.Add($"feature {f.Key}: no probe in FieldTest.Features.cs");
            if (f.How == How.Case && !caseTools.Contains(f.By)) gaps.Add($"feature {f.Key}: {f.By} has no self-test case");
            if (f.How == How.Manual && (f.Reason.Length == 0 || f.Steps.Length == 0)) gaps.Add($"feature {f.Key}: manual without a reason and steps");
        }
        foreach (var pair in ManualTools)
            if (pair.Value.Reason.Length == 0 || pair.Value.Steps.Length == 0) gaps.Add($"tool {pair.Key}: manual without a reason and steps");
        foreach (var line in manifestLines)
            if (RibbonKey(line) is { } key && !(Ribbon.TryGetValue(key, out var feature) && keys.Contains(feature)))
                gaps.Add($"ribbon {key}: no Ribbon row naming a feature");
        return gaps;
    }

    // ---- The guided checklist --------------------------------------------------------------------------------------------

    internal sealed record CheckItem(string Id, string Area, string Title, string Steps, string Why);

    internal sealed record BugRow(int Number, string What, string Status);

    private static readonly Regex Row = new(@"^\|\s*(\d+)\s*\|[^|]*\|([^|]*)\|[^|]*\|([^|]*)\|", RegexOptions.CultureInvariant);

    /// <summary>The rows of a bug log (docs/bug-log.md format) whose status says "Fixed in", in number order.</summary>
    internal static List<BugRow> FixedBugs(string? markdown)
    {
        var rows = new Dictionary<int, BugRow>();
        foreach (var line in (markdown ?? "").Replace("\r", "").Split('\n'))
        {
            var m = Row.Match(line.Trim());
            if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            var status = m.Groups[3].Value.Trim();
            if (status.IndexOf("Fixed in", StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows[n] = new BugRow(n, m.Groups[2].Value.Trim(), status);
        }
        return rows.Values.OrderBy(r => r.Number).ToList();
    }

    /// <summary>The checklist: every manual feature and tool, then one step per fixed bug row, in area order.</summary>
    internal static List<CheckItem> Checklist(IEnumerable<BugRow> fixedBugs)
    {
        var items = Features.Where(f => f.How == How.Manual).Select(f => new CheckItem(f.Key, f.Area, f.Title, f.Steps, f.Reason)).ToList();
        items.AddRange(ManualTools.OrderBy(t => t.Key).Select(t => new CheckItem("tool:" + t.Key, Limits, t.Key, t.Value.Steps, t.Value.Reason)));
        string[] order = [Chat, RibbonArea, Tasks, Time, Carbon, Ve, Rebar2D, Bugs, Updates, Model, Limits];
        items = items.OrderBy(i => Array.IndexOf(order, i.Area) is var x && x < 0 ? order.Length : x).ToList();
        items.AddRange(fixedBugs.Select(b => new CheckItem("bug:" + b.Number, BugFixes, $"#{b.Number}: {b.What}",
            "Do what the row describes on a _detached copy and check it no longer happens.", b.Status)));
        return items;
    }

    internal sealed record Mark(string Id, string Result, string Note);

    /// <summary>The result file: a short Markdown table, small enough to paste or attach.</summary>
    internal static string ResultMarkdown(IReadOnlyList<CheckItem> items, IReadOnlyDictionary<string, Mark> marks, string version, string revit, string model, DateTime when)
    {
        int Count(string r) => items.Count(i => marks.TryGetValue(i.Id, out var m) && m.Result == r);
        var sb = new StringBuilder();
        sb.AppendLine("# Sketch ProMax test checklist").AppendLine();
        sb.AppendLine($"- Date: {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Sketch ProMax {version}, Revit {revit}");
        if (model.Length > 0) sb.AppendLine($"- Model: {model}");
        sb.AppendLine($"- Result: {Count("pass")} pass, {Count("fail")} fail, {items.Count - Count("pass") - Count("fail")} not checked").AppendLine();
        sb.AppendLine("| Item | Area | Result | Note |").AppendLine("|---|---|---|---|");
        foreach (var i in items)
        {
            marks.TryGetValue(i.Id, out var m);
            sb.AppendLine($"| {Cell(i.Title)} | {i.Area} | {(m is null || m.Result.Length == 0 ? "not checked" : m.Result == "fail" ? "**fail**" : m.Result)} | {Cell(m?.Note ?? "")} |");
        }
        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "/").Replace("\r", "").Replace("\n", " ");
}
