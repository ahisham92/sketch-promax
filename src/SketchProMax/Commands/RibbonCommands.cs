using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.UI;
using SketchProMax.Update;

namespace SketchProMax.Commands;

/// <summary>Ribbon buttons stay available with no document open.</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

[Transaction(TransactionMode.Manual)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        new SettingsWindow().ShowDialog();
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class StatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        ClaudeRegistration.Ensure();
        var registered = ClaudeRegistration.Registered();
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = App.Bridge?.IsListening == true ? "Sketch ProMax is ready" : "Sketch ProMax is not connected",
            MainContent =
                (doc is null ? "Open a model, then " : $"Active model: {doc.Title}\n\n") +
                "chat in the Claude desktop app, for example: \"What's in my Revit model?\"\n\n" +
                "If the Revit tools don't appear in Claude, quit Claude completely (system tray > Quit) and start it again.",
            FooterText = Updater.Describe(),
            ExpandedContent = $"Revit {commandData.Application.Application.VersionNumber}, session {BridgeServer.SessionId}\n\n" +
                              "Registered with the Claude app in:\n" +
                              (registered.Count > 0 ? string.Join("\n", registered) : "(nowhere yet - is the Claude desktop app installed?)"),
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Check for updates now");
        if (dialog.Show() != TaskDialogResult.CommandLink1) return Result.Succeeded;

        // Downloading a new version can take a minute on slow connections, so it runs in the background and the
        // result is shown once Revit is idle again.
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                var staged = await Updater.CheckAsync(force: true).ConfigureAwait(false);
                message = staged is null ? Updater.Describe() : App.UpdateReadyMessage(staged);
            }
            catch (Exception ex) { message = $"The update check failed: {ex.Message}"; }
            try
            {
                await App.Dispatcher.InvokeAsync(_ =>
                {
                    TaskDialog.Show("Sketch ProMax", message);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception) { /* Revit is closing; Status shows the result next time */ }
        });
        TaskDialog.Show("Sketch ProMax", "Checking for updates in the background. You can keep working; the result appears when it is done.");
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class SelfTestCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var app = commandData.Application;
        if (Tools.SelfTest.IsRunning)
        {
            var running = new TaskDialog("Sketch ProMax self-test")
            {
                MainInstruction = "A self-test is running",
                MainContent = Tools.SelfTest.Progress(),
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            running.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Stop it after the current step");
            if (running.Show() == TaskDialogResult.CommandLink1) Tools.SelfTest.Stop();
            return Result.Succeeded;
        }
        if (app.ActiveUIDocument is null)
        {
            TaskDialog.Show("Sketch ProMax", "Open a model first. A copy of a real project gives the most useful result.");
            return Result.Cancelled;
        }
        var ask = new TaskDialog("Sketch ProMax self-test")
        {
            MainInstruction = "Test the Sketch ProMax tools on this model",
            MainContent = "Tools run one at a time in the background, so you can keep working, but stay in this model until it " +
                          "finishes. Slow tools are skipped on large models. Click Self-test again to see progress or stop it.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
        };
        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Quick check (recommended)", "Read-only tools only. Nothing in the model changes.");
        // This choice is the confirmation for the full check, so it names the model and says to use a copy.
        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Full check, on a copy of a model",
            "Also runs the tools that change the model. Each change is undone straight after it is checked. " +
            Tools.SelfTest.FullWarning(app.ActiveUIDocument.Document).Replace(" Continue?", ""));
        var choice = ask.Show();
        if (choice is not (TaskDialogResult.CommandLink1 or TaskDialogResult.CommandLink2)) return Result.Cancelled;

        var registry = App.Bridge?.Registry ?? Tools.ToolRegistry.CreateDefault();
        var mode = choice == TaskDialogResult.CommandLink2 ? Tools.SelfTest.Mode.Full : Tools.SelfTest.Mode.Safe;
        Tools.SelfTest.Start(app, registry, mode, report => new TaskDialog("Sketch ProMax self-test")
        {
            MainInstruction = report.Failed == 0 ? "Every tested tool works" : $"{report.Failed} tool(s) failed",
            MainContent = Tools.SelfTest.Progress(),
            FooterText = report.File is null ? "" : "Send the report file to Claude (attach it in the chat) to get any failures fixed.",
        }.Show());
        return Result.Succeeded;
    }
}
