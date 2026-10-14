using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SketchProMax.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ShowPaneCommand : IExternalCommand, IExternalCommandAvailability
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var pane = commandData.Application.GetDockablePane(App.PaneId);
        if (pane.IsShown()) pane.Hide(); else pane.Show();
        return Result.Succeeded;
    }

    // Available even with no document open (zero-doc state), so the pane can always be toggled.
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}
