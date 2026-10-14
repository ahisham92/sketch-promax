using Autodesk.Revit.UI;
using SketchProMax.Commands;
using SketchProMax.Revit;
using SketchProMax.UI;

namespace SketchProMax;

/// <summary>Revit entry point: registers the ribbon button, the dockable chat pane and the API dispatcher.</summary>
public sealed class App : IExternalApplication
{
    public static readonly DockablePaneId PaneId = new(new Guid("8b0f7e52-6d1a-4c3e-9f27-5a4b1c9e2d70"));

    public static RevitDispatcher Dispatcher { get; } = new();

    public Result OnStartup(UIControlledApplication application)
    {
#if NETFRAMEWORK
        DependencyResolver.Register();
#endif
        Dispatcher.Initialize();
        application.RegisterDockablePane(PaneId, "Sketch ProMax", new ChatPane());

        const string tab = "Sketch ProMax";
        application.CreateRibbonTab(tab);
        var panel = application.CreateRibbonPanel(tab, "AI Agent");
        var button = new PushButtonData("SketchProMaxShowPane", "Sketch\nProMax",
            typeof(App).Assembly.Location, typeof(ShowPaneCommand).FullName)
        {
            ToolTip = "Open the Sketch ProMax AI agent pane.",
            AvailabilityClassName = typeof(ShowPaneCommand).FullName,
        };
        panel.AddItem(button);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
#if NETFRAMEWORK
        DependencyResolver.Unregister();
#endif
        return Result.Succeeded;
    }
}
