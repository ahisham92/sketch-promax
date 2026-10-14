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

        // Checking blocks Revit briefly; downloading a new version can take a minute on slow connections.
        var staged = Task.Run(() => Updater.CheckAsync(force: true)).GetAwaiter().GetResult();
        TaskDialog.Show("Sketch ProMax", staged is null ? Updater.Describe() : App.UpdateReadyMessage(staged));
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class SelfTestCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var app = commandData.Application;
        if (app.ActiveUIDocument is null)
        {
            TaskDialog.Show("Sketch ProMax", "Open a model first. A copy of a real project gives the most useful result.");
            return Result.Cancelled;
        }
        var ask = new TaskDialog("Sketch ProMax self-test")
        {
            MainInstruction = "Test every Sketch ProMax tool on this model?",
            MainContent = "Each tool runs once with inputs taken from this model. Everything it creates or changes is rolled back " +
                          "at the end, so the model is left exactly as it was. It can take a few minutes on a large model.",
            CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
        };
        if (ask.Show() != TaskDialogResult.Ok) return Result.Cancelled;

        var registry = App.Bridge?.Registry ?? Tools.ToolRegistry.CreateDefault();
        var report = Tools.SelfTest.Run(app, registry, includeChanges: true);
        new TaskDialog("Sketch ProMax self-test")
        {
            MainInstruction = report.Failed == 0 ? "Every tested tool works" : $"{report.Failed} tool(s) failed",
            MainContent = report.Summary(),
            FooterText = report.File is null ? "" : "Full report: " + report.File +
                         "\nSend this file to Claude (attach it in the chat) to get the failures fixed.",
        }.Show();
        return Result.Succeeded;
    }
}
