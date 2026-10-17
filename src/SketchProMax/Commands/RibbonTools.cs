using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Tools;
using SketchProMax.UI;

namespace SketchProMax.Commands;

/// <summary>
/// A tool run straight from the ribbon's Model, Documents, Check and Issue panels, without the chat: a small options
/// window built from the tool's input schema (<see cref="ToolForm"/>), filled with the values used last time, then the
/// tool runs as a job the way a call from Claude does (one job at a time, under the failure guard, into the activity
/// log, its progress in a window that can stop it and its result in the pane), and the result is shown. Clicking Run
/// is the user's go-ahead for that change; a learned tool's code is still asked about the first time, as the bridge does.
/// </summary>
internal static class RibbonTools
{
    public static Result Run(UIApplication app, string name)
    {
        var registry = App.Bridge?.Registry ?? ToolRegistry.CreateDefault();
        var tool = registry.Find(name);
        var title = ToolForm.Label(name);
        if (tool is null)
        {
            TaskDialog.Show("Sketch ProMax", $"This button runs '{name}', which this version of Sketch ProMax doesn't have. " +
                                              "Check for updates from the Update button, or restart Revit to tidy up the ribbon.");
            return Result.Cancelled;
        }
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            TaskDialog.Show("Sketch ProMax", $"Open a project model first, then click {title} again.");
            return Result.Cancelled;
        }
        if (Jobs.Running is { } running)
        {
            TaskDialog.Show("Sketch ProMax", $"Revit is still running {running.Tool}. Wait for it to finish, or click the Job button to stop it, then try again.");
            return Result.Cancelled;
        }
        if (SelfTest.IsRunning)
        {
            TaskDialog.Show("Sketch ProMax", "The self-test is running. Wait for it to finish, or click the Job button to stop it, then try again.");
            return Result.Cancelled;
        }

        var fields = ToolForm.Fields(tool.PropertiesJson, tool.Required);
        var defaults = ToolDefaults.Load();
        var remembered = defaults.For(tool.Name);
        var form = fields.Select(f =>
        {
            var initial = ToolForm.Initial(f, remembered);
            // A yes/no whose default the schema doesn't say: a list with an empty "tool's default" entry, so leaving it
            // alone never sends "no" by accident.
            return ToolForm.AsYesNoList(f, initial)
                ? new InputsWindow.Field(f.Key, f.Label, (f.Help is null ? "" : f.Help + " ") + "Leave empty for the tool's default.", "string", false, ["yes", "no"], null)
                : new InputsWindow.Field(f.Key, f.Label, f.Help, f.Type, f.Required, f.Choices, initial, f.ElementIds);
        }).ToList();
        string selection;
        try { selection = string.Join(", ", app.ActiveUIDocument!.Selection.GetElementIds().Take(1000).Select(id => id.Value)); }
        catch (Exception) { selection = ""; }

        var window = new InputsWindow(title,
            tool.ModifiesModel ? "Changes the model. Ctrl+Z undoes it." : "Reads the model. Nothing in it changes.",
            Summary(tool.Description) + $"\n\nRuns on '{doc.Title}'." + (fields.Count == 0 ? " It needs no options." : " Fields marked * are needed; leave the others empty for the tool's defaults."),
            [], form, "Run", selection: selection,
            favourite: FavouritesRibbon.CanPin(Favourites.Key("tool", tool.Name)) ? Favourites.Key("tool", tool.Name) : null)
        {
            Validate = values =>
            {
                var problems = new List<string>();
                ToolForm.Arguments(fields, values, problems);
                return problems;
            },
        };
        if (window.ShowDialog() != true) return Result.Cancelled;
        defaults.Remember(tool.Name, fields, window.Values);
        defaults.Save();

        var problems = new List<string>();
        var json = ToolForm.Arguments(fields, window.Values, problems);
        if (json is null)
        {
            TaskDialog.Show("Sketch ProMax", "Some options could not be read:\n" + string.Join("\n", problems));
            return Result.Failed;
        }
        var args = ToolForm.Parse(json);
        if (tool is LearnedTool learned && !LearnedTools.IsApproved(learned.Code)
            && !BridgeServer.Approve(tool, args))
            return Result.Cancelled;
        // Issuing or exporting sheets runs the issue check first; open items stop it until fixed or accepted.
        if (IssueGateWindow.Gated(tool.Name) && !IssueGateWindow.Before(app, tool.Name, args)) return Result.Cancelled;

        ToolResult result;
        try
        {
            using (PackProgress.Show(title, $"{title} on '{doc.Title}'"))
                result = QuickTools.Execute(app, tool.Name, args);
        }
        // A check cleared for this run is for this run only, used or not (the tool takes it when it starts).
        finally { if (IssueGateWindow.Gated(tool.Name)) Tools.IssueGate.Discard(); }
        var headline = result.IsError
            ? result.Text.StartsWith("Stopped on request", StringComparison.Ordinal) ? "Stopped; what it finished is kept" : "It did not work"
            : Revit.TimeLog.LastLine ?? "Done"; // "Done in X. By hand: about Y. Saved: Z."
        QuickTools.Show(title, result, headline);
        return result.IsError ? Result.Failed : Result.Succeeded;
    }

    /// <summary>The first paragraph of a tool's description, cut at a sentence end near 400 characters.</summary>
    internal static string Summary(string description)
    {
        var text = (description ?? "").Replace("\r", "");
        var paragraph = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Replace('\n', ' ').Trim() ?? "";
        if (paragraph.Length <= 400) return paragraph;
        var cut = paragraph.LastIndexOf(". ", 400, StringComparison.Ordinal);
        return cut > 120 ? paragraph.Substring(0, cut + 1) : paragraph.Substring(0, 400).TrimEnd() + "…";
    }
}
