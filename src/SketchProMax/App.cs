using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Commands;
using SketchProMax.Revit;
using SketchProMax.Tools;

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
        return Result.Succeeded;
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
