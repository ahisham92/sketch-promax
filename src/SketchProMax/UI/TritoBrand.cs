using Autodesk.Revit.UI;
using SketchProMax.Sharing;

namespace SketchProMax.UI;

/// <summary>
/// The running version on the TritoRevit tab: its first panel is titled "TritoRevit 0.35.80" and its Update button reads
/// "Update" over the version, so a colleague can say which version they have without opening anything (2026-11-19).
/// Done by the core, which runs the tab with either add-in, so the byte-stable TritoRevit add-in doesn't change; a core
/// switched to with Revit open writes its own version. Cosmetic: never throws, and a PC with no TritoRevit tab is left alone.
/// </summary>
internal static class TritoBrand
{
    private static UIApplication? _app;
    private static bool _done;

    /// <summary>Call on Revit's thread when the core starts.</summary>
    public static void Attach(UIApplication app)
    {
        _app = app;
        _done = false;
        Apply();
    }

    /// <summary>Writes the version when the tab is there. Revit's thread; asked again while the tab may still be coming.</summary>
    public static void Apply()
    {
        if (_done || _app is null) return;
        try
        {
            var version = TritoTabMath.Short(typeof(TritoBrand).Assembly.GetName().Version);
            foreach (var panel in _app.GetRibbonPanels(TritoTabMath.Trito))
            {
                if (panel is null || panel.Name != TritoTabMath.Panel) continue;
                var title = TritoTabMath.PanelTitle(version);
                if (panel.Title != title) panel.Title = title;
                foreach (var item in panel.GetItems())
                {
                    if (item is null || item.Name != TritoTabMath.UpdateButton) continue;
                    var text = TritoTabMath.UpdateText(version);
                    if (item.ItemText != text) item.ItemText = text;
                    var tip = TritoTabMath.UpdateTip(version);
                    if ((item.ToolTip ?? "") != tip) item.ToolTip = tip;
                    _done = true;
                }
            }
        }
        catch (Exception) { /* no TritoRevit tab here (yet), or Revit's ribbon said no: the tab keeps its own words */ }
    }
}
