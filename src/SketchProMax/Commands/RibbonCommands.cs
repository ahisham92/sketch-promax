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
