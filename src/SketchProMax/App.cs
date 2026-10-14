using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Commands;
using SketchProMax.Revit;
using SketchProMax.Settings;
using SketchProMax.Tools;
using SketchProMax.Update;

namespace SketchProMax;

/// <summary>
/// Revit entry point. Starts the bridge that the Claude desktop app talks to (through SketchProMax.Mcp.exe)
/// and adds a ribbon tab with Status and Settings. There is no chat inside Revit: conversations happen in the
/// Claude desktop app on the user's Claude plan, so there is no API key and no per-use cost.
/// </summary>
public sealed class App : IExternalApplication
{
    public static RevitDispatcher Dispatcher { get; } = new();

    public static BridgeServer? Bridge { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
#if NETFRAMEWORK
        DependencyResolver.Register();
#endif
        Dispatcher.Initialize();
        Bridge = new BridgeServer(ToolRegistry.CreateDefault(), Dispatcher, application.ControlledApplication.VersionNumber);
        Bridge.Start();
        _ = Task.Run(() => ClaudeRegistration.Ensure());
        // Check at every start, then hourly while Revit stays open, so a fix is staged by the time Revit is next closed.
        if (AgentSettings.Load().AutoUpdate) _ = Task.Run(UpdateLoopAsync);

        const string tab = "Sketch ProMax";
        application.CreateRibbonTab(tab);
        var panel = application.CreateRibbonPanel(tab, "AI Agent");
        var assembly = typeof(App).Assembly.Location;
        panel.AddItem(new PushButtonData("SketchProMaxStatus", "Status", assembly, typeof(StatusCommand).FullName)
        {
            ToolTip = "Check the connection to the Claude desktop app and how to start.",
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
        });
        panel.AddItem(new PushButtonData("SketchProMaxSettings", "Settings", assembly, typeof(SettingsCommand).FullName)
        {
            ToolTip = "Firm standards and whether Revit asks before Claude changes the model.",
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
        });
        panel.AddItem(new PushButtonData("SketchProMaxSelfTest", "Self-test", assembly, typeof(SelfTestCommand).FullName)
        {
            ToolTip = "Run every Sketch ProMax tool once on the open model and report what works. Changes are rolled back, so the model is left as it was.",
        });
        return Result.Succeeded;
    }

    public static string UpdateReadyMessage(string version) =>
        $"Sketch ProMax {version} has been downloaded. It starts the next time you open Revit.\n\n" +
        "Restart the Claude desktop app too (system tray > Quit, then open it again) so it picks up the new version.";

    private static async Task UpdateLoopAsync()
    {
        var force = true;
        while (true)
        {
            try { await CheckForUpdateAsync(force).ConfigureAwait(false); }
            catch (Exception) { /* a failed check is retried next round; Status shows the last error */ }
            force = false;
            await Task.Delay(TimeSpan.FromHours(1)).ConfigureAwait(false);
            if (!AgentSettings.Load().AutoUpdate) return;
        }
    }

    private static async Task CheckForUpdateAsync(bool force)
    {
        var staged = await Updater.CheckAsync(force).ConfigureAwait(false);
        if (staged is null) return;
        // Show the notice on Revit's main thread, when Revit is idle.
        await Dispatcher.InvokeAsync(_ =>
        {
            TaskDialog.Show("Sketch ProMax update", UpdateReadyMessage(staged));
            return true;
        }).ConfigureAwait(false);
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Bridge?.Stop();
#if NETFRAMEWORK
        DependencyResolver.Unregister();
#endif
        return Result.Succeeded;
    }
}
