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
        // The ribbon's own name and one-line tip (0.35: short; the tool's full description is for the chat).
        var ribbon = CoreRibbonManifest.Tools().FirstOrDefault(t => t.Tool == name);
        var title = ribbon.Tool is null ? ToolForm.Label(name) : ribbon.Text;
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
        form = RibbonChoices.Add(doc, tool.Name, form);
        string selection;
        try { selection = string.Join(", ", app.ActiveUIDocument!.Selection.GetElementIds().Take(1000).Select(id => id.Value)); }
        catch (Exception) { selection = ""; }

        var window = new InputsWindow(title,
            (tool.ModifiesModel ? "Changes the model, Ctrl+Z undoes it" : "Reads the model only") + $" · {doc.Title}",
            ribbon.Tool is null ? FirstSentence(tool.Description) : ribbon.Tip,
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
        // Redlines whose pages Claude Code reads: the reading runs off Revit's thread as a job; only the apply comes back here.
        if (RibbonRedlines.ReadsPages(tool.Name, args, doc)) return RibbonRedlines.Start(app, tool, args, title);

        ToolResult result;
        try
        {
            using (PackProgress.Show(title, $"{title} on '{doc.Title}'"))
                result = QuickTools.Execute(app, tool.Name, args);
        }
        // A check cleared for this run is for this run only, used or not (the tool takes it when it starts).
        finally { if (IssueGateWindow.Gated(tool.Name)) Tools.IssueGate.Discard(); }
        // Purge and cleanup: the preview comes first; deleting what it lists is asked here, with Close as the default.
        if (!result.IsError && ApprovalPolicy.IsPreview(tool.Name, args) && AskApply(title, result))
        {
            args["apply"] = System.Text.Json.JsonSerializer.SerializeToElement(true);
            using (PackProgress.Show(title, $"{title} on '{doc.Title}'"))
                result = QuickTools.Execute(app, tool.Name, args);
        }
        ShowResult(title, result);
        return result.IsError ? Result.Failed : Result.Succeeded;
    }

    /// <summary>A ribbon tool's result in the result window, with its one-line headline.</summary>
    internal static void ShowResult(string title, ToolResult result)
    {
        var headline = result.IsError
            ? result.Text.StartsWith("Stopped on request", StringComparison.Ordinal) ? "Stopped; what it finished is kept" : "It did not work"
            : Revit.TimeLog.LastLine ?? "Done"; // "Done in X. By hand: about Y. Saved: Z."
        QuickTools.Show(title, result, headline);
    }

    /// <summary>
    /// The ribbon's Purge &amp; cleanup button: which of the four cleanup tools to run. The chosen one runs as any ribbon
    /// tool (its options, then the preview, then <see cref="AskApply"/>). Close is the default; nothing is deleted here.
    /// </summary>
    public static Result Cleanup(UIApplication app)
    {
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = "Purge and cleanup",
            MainContent = "Pick what to clean up. Each shows a preview first: nothing changes until you press Delete them, and Ctrl+Z undoes it in one step.",
            CommonButtons = TaskDialogCommonButtons.Close,
            DefaultButton = TaskDialogResult.Close,
            AllowCancellation = true,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Purge unused", "Families, types, materials, patterns, unused view templates and filters, repeated until nothing new is unused.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Delete unused views", "Views on no sheet. Keeps WORKING and _keep views, open views and views others depend on.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Clean up imports", "Imported CAD (or CAD links when asked), their leftover types and unused IMPORT- line styles.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Model health clean", "Unused view templates, unplaced rooms and areas, unused groups and filters, and the warnings by type.");
        var tool = dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 => "purge_unused",
            TaskDialogResult.CommandLink2 => "delete_unused_views",
            TaskDialogResult.CommandLink3 => "cleanup_imports",
            TaskDialogResult.CommandLink4 => "cleanup_model",
            _ => null,
        };
        return tool is null ? Result.Cancelled : Run(app, tool);
    }

    /// <summary>After a cleanup preview that found something: shows its summary and asks whether to delete. True: delete.</summary>
    private static bool AskApply(string title, ToolResult preview)
    {
        string summary;
        int total;
        string? also = null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(preview.Text);
            var root = json.RootElement;
            summary = root.GetProperty("summary").GetString() ?? "";
            total = root.GetProperty("total").GetInt32();
            if (root.TryGetProperty("alsoDeletes", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Object)
                also = "It also deletes " + string.Join(", ", a.EnumerateObject().Select(p => $"{p.Value.GetInt32()} {p.Name}")) + ".";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
        if (total == 0) return false;
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = $"{title}: delete {total} item(s)?",
            MainContent = summary + (also is null ? "" : "\n\n" + also),
            ExpandedContent = preview.Text.Length > 6000 ? preview.Text.Substring(0, 6000) + "\n…" : preview.Text,
            AllowCancellation = true,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Delete them", "One Undo step: Ctrl+Z brings everything back.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Close without deleting", "Nothing changes. The preview's list is in the result window.");
        dialog.DefaultButton = TaskDialogResult.CommandLink2;
        return dialog.Show() == TaskDialogResult.CommandLink1;
    }

    /// <summary>The first sentence of a tool's description (for a tool without a ribbon tip), 200 characters at most.</summary>
    internal static string FirstSentence(string description)
    {
        var text = Summary(description);
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        if (end > 0) text = text.Substring(0, end + 1);
        return text.Length <= 200 ? text : text.Substring(0, 200).TrimEnd() + "…";
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
