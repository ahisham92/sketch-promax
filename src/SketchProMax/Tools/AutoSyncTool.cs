using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// auto_sync: the auto-sync rule (Revit.AutoSyncRule, Settings > Chat) for Claude: how many changes are left before the
/// next sync, and skipping the rule for a task when the user says so (one big step applied in one go, no pause and no
/// sync until their next message). Answered by the bridge, not Revit's thread, so it works while a sync or a job runs.
/// </summary>
public sealed class AutoSyncTool : IRevitTool
{
    public const string ToolName = "auto_sync";

    public string Name => ToolName;
    public string Description =>
        "Auto sync (Settings > Chat): after every N changes (250 by default) to a local copy of a central model, Sketch ProMax " +
        "pauses every job, synchronizes with central and carries on; an item batch bigger than what is left is split across syncs. " +
        "One code call can't be split: keep each run_revit_code call under the changes left. action=status says the rule and the " +
        "changes left. action=skip when the user says to do a big step in one go or not to sync for this task (no pause, no sync, " +
        "no split until their next message); action=resume puts the rule back. Not a model change.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["status", "skip", "resume"], "description": "Default status."},
      "reason": {"type": "string", "description": "skip: what the user said, in a few words."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    /// <summary>Run from a recipe or the self-test (not through the bridge): the status only.</summary>
    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        if (app.ActiveUIDocument?.Document is { } doc) AutoSync.Seen(doc);
        return Answer(new ToolArgs(new Dictionary<string, System.Text.Json.JsonElement>()));
    }

    internal static ToolResult Answer(ToolArgs args)
    {
        var action = (args.OptString("action") ?? "status").Trim().ToLowerInvariant();
        var rule = AutoSync.Rule;
        switch (action)
        {
            case "status":
                return ToolResult.Ok(new { on = rule.On, limit = rule.Limit, rule = AutoSync.Describe() });
            case "skip":
                if (!rule.On) return ToolResult.Ok(new { skipped = false, rule = AutoSync.Describe() });
                rule.Skip(AutoSync.Task, DateTime.Now, (args.OptString("reason") ?? "").Trim());
                return ToolResult.Ok(new
                {
                    skipped = true,
                    rule = AutoSync.Describe(),
                    next = "Do the step in one go. Tell the user the model isn't synchronized until they do it, or until the next task reaches the limit.",
                });
            case "resume":
                rule.Resume();
                return ToolResult.Ok(new { skipped = false, rule = AutoSync.Describe() });
            default:
                return ToolResult.Error("action is status, skip or resume.");
        }
    }
}
