using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>How often Sketch ProMax asks in Revit before Claude changes something (Settings, and the chat's model menu).</summary>
public enum ApprovalLevel
{
    /// <summary>Normal model changes run without a dialog (they are one Undo away); risky ones are still asked. The default.</summary>
    RiskyOnly,
    /// <summary>The first change of a chat task asks once; the rest of that task runs. Risky ones are still asked.</summary>
    OncePerTask,
    /// <summary>Every model change is asked (how it worked before the levels).</summary>
    EveryChange,
    /// <summary>
    /// Nothing is asked, risky changes included (code, deletes that take others, overwrites, what-ifs, value engineering
    /// on the open model). Only the issue check's accept and issue-past-open-items still ask: they are a QA sign-off.
    /// </summary>
    AlwaysAllow,
}

/// <summary>Why a call is asked about: a normal change (can be silenced), risky (asked each time), or always asked (whatever the setting).</summary>
internal enum AskKind { None, Change, Risky, Always }

/// <summary>
/// What the bridge found out about a call before deciding, that needs Revit or the disk: code the user has not approved
/// yet (a learned tool or a recipe), what a delete takes with it, and a file it would overwrite.
/// </summary>
internal sealed record ApprovalFacts
{
    /// <summary>A learned tool or recipe whose code (or steps) has not been approved on this PC, or changed since.</summary>
    public bool NewCode { get; init; }
    /// <summary>"It also deletes 2 Doors, 3 Door Tags." when a delete takes other elements with it.</summary>
    public string? DeleteTakes { get; init; }
    /// <summary>A file the call replaces that the path checks can't see (e.g. a recipe saved under an existing name).</summary>
    public string? Overwrites { get; init; }
}

/// <summary>The answer: ask or not, why (one plain line), and whether the dialog may offer "don't ask again" and "allow the rest of this task".</summary>
internal sealed record ApprovalDecision(bool Ask, AskKind Kind, string Reason)
{
    public static readonly ApprovalDecision No = new(false, AskKind.None, "");
    /// <summary>Only a normal change can be silenced; risky and always-asked calls ask every time.</summary>
    public bool CanSilence => Kind == AskKind.Change;
}

/// <summary>Whether a path a tool writes to exists, and what it is.</summary>
internal enum PathState { Missing, EmptyFolder, Folder, File }

/// <summary>
/// The one place that decides whether a tool call is asked about in Revit. Pure (no Revit, no settings file): the bridge
/// passes the level, the tools the user said not to ask about again, whether the chat task is already approved (its
/// brief was run, or "Allow the rest of this task" was pressed) and a probe for paths, so the tests check every rule.
/// </summary>
internal static class ApprovalPolicy
{
    /// <summary>Arguments that name a file or folder a tool writes to (template_path is read, so it is not here).</summary>
    private static readonly HashSet<string> WriteKeys = new(StringComparer.Ordinal)
    {
        "folder", "output_folder", "report_folder", "file_name", "copy_path",
    };

    /// <summary>Tools whose "path" argument is where they write.</summary>
    private static readonly HashSet<string> WritesToPath = new(StringComparer.Ordinal) { "save_legend_seed" };

    /// <summary>Tools that run code Claude wrote in this call: from the chat, always asked (the chat has no prompts of its own).</summary>
    private static readonly HashSet<string> RunsNewCode = new(StringComparer.Ordinal) { "run_revit_query_code", "run_revit_code" };

    /// <summary>Tools that run a saved recipe: asked when its steps are new to this PC (<see cref="ApprovalFacts.NewCode"/>).</summary>
    private static readonly HashSet<string> RunsRecipe = new(StringComparer.Ordinal) { "run_recipe", "batch_run_recipe" };

    /// <summary>
    /// Ask or not, and why. <paramref name="remembered"/>: tools the user said not to ask about again.
    /// <paramref name="taskApproved"/>: the chat task this call belongs to is approved (only for calls from the chat).
    /// <paramref name="outputRoots"/>: Sketch ProMax's own folders; writing inside them never asks.
    /// </summary>
    public static ApprovalDecision Decide(
        string tool, bool modifiesModel, bool isLearned, IReadOnlyDictionary<string, JsonElement> args, bool fromChat, ApprovalFacts facts,
        ApprovalLevel level, ICollection<string> remembered, bool taskApproved, Func<string, PathState> probe, IReadOnlyList<string> outputRoots)
    {
        // Whatever the level, wherever it comes from: what_if with confirm, accepting issue items, issuing with open items.
        // At "Always allow" only the issue gate's (a QA sign-off, not an approval) is left.
        if (OutputPaths.AlwaysAskReason(tool, args) is { } always)
            return level != ApprovalLevel.AlwaysAllow || IsIssueGate(tool) ? new(true, AskKind.Always, always) : ApprovalDecision.No;
        if (level == ApprovalLevel.AlwaysAllow) return ApprovalDecision.No;

        var risks = new List<string>();
        if (fromChat && RunsNewCode.Contains(tool))
            risks.Add("The chat wrote code to run in Revit. Read it in the details before you allow it.");
        if (facts.NewCode && isLearned)
            risks.Add("This learned tool's code has not been allowed on this PC yet, or it changed since. Read it in the details.");
        if (facts.NewCode && RunsRecipe.Contains(tool))
            risks.Add("This recipe's steps have not been allowed on this PC yet, or they changed since. Read them in the details.");
        if (tool == "ve_apply" && Str(args, "target") == "main")
            risks.Add("It applies value engineering changes to the open model itself.");
        if (facts.DeleteTakes is { Length: > 0 } takes)
            risks.Add("It deletes elements, and other elements go with them. " + takes);
        if (facts.Overwrites is { Length: > 0 } over)
            risks.Add($"It replaces {over}, which can't be undone.");
        foreach (var path in WritePaths(tool, args))
        {
            if (Inside(path, outputRoots)) continue;
            switch (probe(path))
            {
                case PathState.File: risks.Add($"It overwrites the file {path}, which can't be undone."); break;
                case PathState.Folder: risks.Add($"It writes into {path}, which already has files that may be overwritten."); break;
            }
        }
        if (risks.Count > 0) return new(true, AskKind.Risky, string.Join(" ", risks));

        // Reads never ask, and neither does a cleanup tool's preview (it changes nothing). Normal changes are one Undo away.
        if (!modifiesModel || IsPreview(tool, args)) return ApprovalDecision.No;
        if (level == ApprovalLevel.RiskyOnly || remembered.Contains(tool) || fromChat && taskApproved) return ApprovalDecision.No;
        return new(true, AskKind.Change, "It changes the model. Ctrl+Z undoes it.");
    }

    /// <summary>A purge or cleanup tool run without apply true: a preview made in a rolled-back transaction.</summary>
    public static bool IsPreview(string tool, IReadOnlyDictionary<string, JsonElement> args) =>
        CleanupMath.Tools.Contains(tool) && !(args.TryGetValue("apply", out var a) && a.ValueKind == JsonValueKind.True);

    /// <summary>The issue check's ways past an open item, which ask at every level (see OutputPaths.AlwaysAskReason).</summary>
    public static bool IsIssueGate(string tool) => tool is "issue_check" or "issue_drawings";

    /// <summary>The full paths a call writes to (empty values are the tool's default folder, which is ours).</summary>
    public static List<string> WritePaths(string tool, IReadOnlyDictionary<string, JsonElement> args) =>
        args.Where(a => (WriteKeys.Contains(a.Key) || a.Key == "path" && WritesToPath.Contains(tool))
                        && a.Value.ValueKind == JsonValueKind.String && a.Value.GetString() is { Length: > 0 })
            .Select(a => OutputPaths.FullPath(a.Value.GetString()!)).ToList();

    /// <summary>True when <paramref name="path"/> is one of <paramref name="roots"/> or inside one (case-insensitive, as Windows).</summary>
    public static bool Inside(string path, IReadOnlyList<string> roots)
    {
        var p = Normal(path);
        foreach (var root in roots)
        {
            var r = Normal(root);
            if (r.Length == 0) continue;
            if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
            if (p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Normal(string path)
    {
        // Resolved as Windows will ("..\" included), so a path that lands outside the root is outside.
        return OutputPaths.FullPath(path).Replace('/', '\\').TrimEnd('\\');
    }

    private static string? Str(IReadOnlyDictionary<string, JsonElement> args, string key) =>
        args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// How many dialogs a chat task of these calls shows when the user answers Allow each time (the field test and the
    /// tests use it; at "Ask once per task" the first Allow approves the rest). <paramref name="briefRun"/>: the task
    /// started from a brief the user ran. Paths are taken as new (nothing is overwritten).
    /// </summary>
    public static int CountAsks(IEnumerable<(string Tool, bool Modifies, IReadOnlyDictionary<string, JsonElement> Args)> calls, ApprovalLevel level, bool briefRun)
    {
        var approved = briefRun;
        var asks = 0;
        foreach (var (tool, modifies, args) in calls)
        {
            var d = Decide(tool, modifies, false, args, true, new ApprovalFacts(), level, Array.Empty<string>(), approved, _ => PathState.Missing, []);
            if (!d.Ask) continue;
            asks++;
            if (d.CanSilence && level == ApprovalLevel.OncePerTask) approved = true;
        }
        return asks;
    }

    // ---- Names and the setting -----------------------------------------------------------------------------------------

    /// <summary>"create_floor_plans" → "Create floor plans": how a tool is named in the dialog and in Settings.</summary>
    public static string Friendly(string tool)
    {
        var text = tool.Replace('_', ' ').Trim();
        return text.Length == 0 ? tool : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    /// <summary>
    /// The level from settings.json: the stored one, or for settings written before levels existed the new default
    /// (whatever the old on/off switch said: on was the default nobody chose, and off asked about less than this).
    /// </summary>
    public static ApprovalLevel Parse(string? stored) => stored?.Trim().ToLowerInvariant() switch
    {
        "once_per_task" or "oncepertask" => ApprovalLevel.OncePerTask,
        "every_change" or "everychange" => ApprovalLevel.EveryChange,
        "always_allow" or "alwaysallow" => ApprovalLevel.AlwaysAllow,
        _ => ApprovalLevel.RiskyOnly,
    };

    public static string Store(ApprovalLevel level) => level switch
    {
        ApprovalLevel.OncePerTask => "once_per_task",
        ApprovalLevel.EveryChange => "every_change",
        ApprovalLevel.AlwaysAllow => "always_allow",
        _ => "risky_only",
    };

    public static string Label(ApprovalLevel level) => level switch
    {
        ApprovalLevel.OncePerTask => "Ask once per task",
        ApprovalLevel.EveryChange => "Ask for every change",
        ApprovalLevel.AlwaysAllow => "Always allow (never ask)",
        _ => "Ask only for risky changes",
    };

    public static string Hint(ApprovalLevel level) => level switch
    {
        ApprovalLevel.OncePerTask => "The first change of a chat task asks; the rest run",
        ApprovalLevel.EveryChange => "A dialog before every change",
        ApprovalLevel.AlwaysAllow => "Nothing asks, risky changes and code included; only the issue check's sign-off still asks",
        _ => "Changes just run (Ctrl+Z undoes them); risky ones still ask",
    };

    public static readonly ApprovalLevel[] Levels = [ApprovalLevel.RiskyOnly, ApprovalLevel.OncePerTask, ApprovalLevel.EveryChange, ApprovalLevel.AlwaysAllow];
}
