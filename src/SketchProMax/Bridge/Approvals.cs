using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Settings;
using SketchProMax.Tools;

namespace SketchProMax.Bridge;

/// <summary>
/// The approval dialog and what it remembers. <see cref="ApprovalPolicy"/> decides whether a call asks; this gathers
/// what that needs from Revit and the disk, shows the one dialog every approval uses (one plain line, the details
/// collapsed, and for a normal change "Allow the rest of this task" and "Don't ask again"), and keeps the answers.
/// Call on Revit's main thread.
/// </summary>
internal static class Approvals
{
    /// <summary>The field test's override of the level (null: the user's setting).</summary>
    internal static ApprovalLevel? TestLevel;

    public static ApprovalLevel Level => TestLevel ?? AgentSettings.Load().Approvals;

    public static void SetLevel(ApprovalLevel level)
    {
        var settings = AgentSettings.Load();
        settings.Approvals = level;
        settings.Save();
        LevelChanged?.Invoke();
    }

    /// <summary>The level was changed (the chat's menu, the dialog's "Always allow", Settings): the chat shows it.</summary>
    public static event Action? LevelChanged;

    internal static void RaiseLevelChanged() => LevelChanged?.Invoke();

    /// <summary>Tools the user said not to ask about again.</summary>
    public static List<string> Remembered() => AgentSettings.Load().NoAskTools ?? [];

    private static void Remember(string tool)
    {
        try
        {
            var settings = AgentSettings.Load();
            var list = settings.NoAskTools ?? [];
            if (list.Contains(tool, StringComparer.Ordinal)) return;
            list.Add(tool);
            settings.NoAskTools = list;
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* asked again next time; nothing worse */ }
    }

    /// <summary>Sketch ProMax's own folders: files written inside them never ask.</summary>
    public static IReadOnlyList<string> OutputRoots
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return [Path.Combine(docs, "SketchProMax"), Path.Combine(docs, "Sketch ProMax"), Path.Combine(appData, "SketchProMax")];
        }
    }

    /// <summary>What is at a path a tool would write to. Unreadable counts as a folder with files, so it asks.</summary>
    public static PathState Probe(string path)
    {
        try
        {
            if (File.Exists(path)) return PathState.File;
            if (!Directory.Exists(path)) return PathState.Missing;
            return Directory.EnumerateFileSystemEntries(path).Any() ? PathState.Folder : PathState.EmptyFolder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return PathState.Folder; }
    }

    // ---- The call running now -------------------------------------------------------------------------------------

    /// <summary>
    /// The bridge call running on Revit's thread now: its decision, its chat task (null outside the chat) and whether the
    /// bridge already asked (then a tool's own dialog doesn't ask again). Null outside a bridge call (the ribbon).
    /// </summary>
    internal sealed record CallState(ApprovalDecision Decision, JobControl? Chat, bool Asked);

    internal static CallState? Call;

    /// <summary>Tools whose own dialog (the package plan, each redline) is the approval: the bridge leaves a normal change's ask to it.</summary>
    public static bool OwnDialog(string tool) => tool is "drawing_package" or "redline_apply";

    /// <summary>
    /// For a tool with its own approval dialog, or a read that makes one change on the side: whether a normal change
    /// should be asked about now. From the ribbon (the user started it), only at "Ask for every change".
    /// </summary>
    public static bool AskOwn(string tool)
    {
        var call = Call;
        if (call is { Asked: true }) return false;
        if (call?.Decision.Kind == AskKind.Change) return true;
        var level = Level;
        if (level is ApprovalLevel.RiskyOnly or ApprovalLevel.AlwaysAllow || Remembered().Contains(tool, StringComparer.Ordinal)) return false;
        if (call is null) return level == ApprovalLevel.EveryChange;
        return call.Chat is not { ApprovedForTask: true };
    }

    /// <summary>The decision for a bridge call, with what Revit and the disk say about it.</summary>
    public static ApprovalDecision Decide(UIApplication app, IRevitTool tool, IReadOnlyDictionary<string, JsonElement> args, bool fromChat, JobControl? chat)
    {
        var facts = new ApprovalFacts
        {
            NewCode = tool is LearnedTool learned ? !LearnedTools.IsApproved(learned.Code)
                : RecipeText(tool, args) is { } recipe ? !LearnedTools.IsApproved(recipe) : false,
            DeleteTakes = tool.Name == "delete_elements" && app.ActiveUIDocument?.Document is { } doc ? DeleteTakes(doc, args)
                : CleanupMath.Tools.Contains(tool.Name) ? Cleanup.Takes(app, tool.Name, args) : null,
            Overwrites = tool.Name == "save_recipe" && fromChat && Str(args, "name") is { Length: > 0 } name && Exists(Recipes.PathFor(name))
                ? $"the saved recipe '{name}'" : null,
        };
        return ApprovalPolicy.Decide(tool.Name, tool.ModifiesModel, tool is LearnedTool, args, fromChat, facts,
            Level, Remembered(), chat?.ApprovedForTask == true, Probe, OutputRoots);
    }

    private static string? DeleteTakes(Document doc, IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("element_ids", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var ids = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _))
            .Select(e => new ElementId(e.GetInt64())).Where(id => doc.GetElement(id) is not null).ToList();
        return ids.Count == 0 ? null : Redlines.AlsoDeleted(doc, ids);
    }

    private static bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static string? Str(IReadOnlyDictionary<string, JsonElement> args, string key) =>
        args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>For run_recipe and batch_run_recipe, the recipe file's text (approved like a learned tool's code); null otherwise.</summary>
    internal static string? RecipeText(IRevitTool tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        var key = tool.Name switch { "run_recipe" => "name", "batch_run_recipe" => "recipe", _ => null };
        if (key is null || Str(args, key) is not { Length: > 0 } name) return null;
        try
        {
            var path = Recipes.PathFor(name);
            return File.Exists(path) ? "recipe\n" + File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // ---- The dialog ------------------------------------------------------------------------------------------------

    internal enum Answer { No, Yes, RestOfTask, NeverAsk, AlwaysAllow }

    /// <summary>Test hook (the field test): answers instead of the dialog, and sees what would have been asked.</summary>
    internal static Func<string, ApprovalDecision, Answer>? TestAnswer;

    /// <summary>Asks about a tool call (the bridge, the ribbon, recipes): true when allowed. Approving new code approves it for later.</summary>
    public static bool Ask(IRevitTool tool, IReadOnlyDictionary<string, JsonElement> args, ApprovalDecision decision, JobControl? chat)
    {
        var yes = Show(tool.Name, decision, Details(tool, args), chat);
        if (yes && tool is LearnedTool learned) LearnedTools.MarkApproved(learned.Code);
        if (yes && RecipeText(tool, args) is { } recipe) LearnedTools.MarkApproved(recipe);
        return yes;
    }

    /// <summary>
    /// The approval dialog: "Allow '<tool>'?", the reason in one line, the details collapsed. A normal change also offers
    /// "Allow the rest of this task" (in a chat task) and "Don't ask again"; risky and always-asked calls only Allow or
    /// Don't allow, with Don't allow the default. Keeps the answer (the task approved, the tool remembered). True: allowed.
    /// </summary>
    public static bool Show(string tool, ApprovalDecision decision, string details, JobControl? chat, string? what = null)
    {
        var friendly = ApprovalPolicy.Friendly(tool);
        var level = Level;
        // "Always allow": nothing asks (the bridge already decided so); this catches the dialogs tools and the ribbon
        // show themselves. Only the issue gate's sign-off (AskKind.Always at this level) is still shown.
        if (level == ApprovalLevel.AlwaysAllow && decision.Kind != AskKind.Always) return true;
        var silence = decision.CanSilence;
        var onceTask = silence && chat is not null && level == ApprovalLevel.OncePerTask;
        Answer answer;
        if (TestAnswer is { } hook) answer = hook(tool, decision);
        else
        {
            if (details.Length > 6000) details = details.Substring(0, 6000) + "\n…";
            var dialog = new TaskDialog("Sketch ProMax")
            {
                MainInstruction = what ?? $"Allow \"{friendly}\"?",
                MainContent = decision.Reason,
                ExpandedContent = details,
                AllowCancellation = true,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, onceTask ? "Allow this task" : "Allow",
                onceTask ? "The rest of this chat task runs without asking; risky changes still ask." : "");
            if (silence && chat is not null && !onceTask)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Allow the rest of this task",
                    "This chat task's other changes run without asking; risky changes still ask.");
            if (silence)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, $"Allow, and don't ask again for {friendly}",
                    "Remembered for you. Settings lists it, to clear.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Don't allow", "Nothing changes. Claude is told, and asks you what to do.");
            dialog.DefaultButton = silence ? TaskDialogResult.CommandLink1 : TaskDialogResult.CommandLink4;
            // One click to stop being asked at all (the dialog has room for four links, so it is the tick box under them).
            var gate = decision.Kind == AskKind.Always && ApprovalPolicy.IsIssueGate(tool);
            if (!gate) dialog.VerificationText = "Always allow: don't ask me again for any change (risky ones included; change it in Settings)";
            var shown = dialog.Show();
            answer = shown switch
            {
                TaskDialogResult.CommandLink1 => onceTask ? Answer.RestOfTask : Answer.Yes,
                TaskDialogResult.CommandLink2 => Answer.RestOfTask,
                TaskDialogResult.CommandLink3 => Answer.NeverAsk,
                _ => Answer.No,
            };
            if (!gate && answer != Answer.No && dialog.WasVerificationChecked()) answer = Answer.AlwaysAllow;
        }
        if (answer == Answer.No) return false;
        if (answer == Answer.AlwaysAllow)
        {
            try { SetLevel(ApprovalLevel.AlwaysAllow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* this one is allowed; the next asks again */ }
            return true;
        }
        if (!silence) return true;
        if (answer == Answer.RestOfTask && chat is not null) chat.ApprovedForTask = true;
        if (answer == Answer.NeverAsk) Remember(tool);
        return true;
    }

    /// <summary>What the dialog's details show: code as code, a learned tool's code, a recipe's steps, otherwise the inputs.</summary>
    internal static string Details(IRevitTool tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        if (tool is LearnedTool learned) return $"Learned tool: {learned.Summary}\n\nInputs: {JsonSerializer.Serialize(args)}\n\n{learned.Code}";
        if (args.TryGetValue("code", out var code) && code.ValueKind == JsonValueKind.String)
            return (args.TryGetValue("description", out var d) ? d.GetString() + "\n\n" : "") + code.GetString();
        var text = JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
        if (RecipeText(tool, args) is { } recipe) text += "\n\nRecipe steps:\n" + recipe.Substring("recipe\n".Length);
        return text;
    }
}
