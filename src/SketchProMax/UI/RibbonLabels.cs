using System.Reflection;
using Autodesk.Revit.UI;

namespace SketchProMax.UI;

/// <summary>
/// Keeps the labels of the Sketch ProMax panel's buttons when the ribbon is narrow. Revit shrinks a crowded panel in
/// steps, and one step hides the text of small stacked buttons, starting with the widest label of a stack and the
/// stacks at the right of the panel (so "Export chats &amp; bugs", "Settings" and "Help" went icon-only while "Report bug"
/// and "Bug folder" kept theirs). Its ribbon control (AdWindows) lets a button opt out of that step: this clears the
/// "hide text" resize style on every button of the panel. Revit then shrinks the panel by other means, collapsing it
/// into its title as a last resort. The API object is reached by reflection, so a Revit that has changed it just keeps
/// its own behaviour. Runs after every manifest the loader applies; the buttons are the loader's, found by panel name.
/// </summary>
internal static class RibbonLabels
{
    private const string Tab = "Sketch ProMax", Panel = "Sketch ProMax";
    private const int HideText = 2; // Autodesk.Windows.RibbonItemResizeStyles.HideText

    private static UIApplication? _app;

    public static void Attach(UIApplication app) => _app = app;

    /// <summary>Clears "hide text" on the panel's buttons. Cosmetic; never throws.</summary>
    public static void Keep()
    {
        if (_app is null) return;
        try
        {
            foreach (var panel in _app.GetRibbonPanels(Tab))
            {
                if (panel is null || panel.Name != Panel) continue;
                foreach (var item in panel.GetItems())
                    if (item is not null) KeepText(item);
            }
        }
        catch (Exception) { /* Revit's own resizing */ }
    }

    /// <summary>The "hide text" flag removed from a resize style value; the other ways of shrinking stay.</summary>
    internal static int WithoutHideText(int style) => style & ~HideText;

    private static MethodInfo? _inner;
    private static bool _looked;

    private static void KeepText(RibbonItem item)
    {
        if (!_looked)
        {
            _looked = true;
            // Revit's RibbonItem wraps an AdWindows item and hands it out through a non-public method.
            _inner = typeof(RibbonItem).GetMethod("getRibbonItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                     ?? typeof(RibbonItem).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                         .FirstOrDefault(m => m.GetParameters().Length == 0 && m.ReturnType.FullName == "Autodesk.Windows.RibbonItem");
        }
        if (_inner is null) return;
        try
        {
            var inner = _inner.Invoke(item, null);
            var property = inner?.GetType().GetProperty("ResizeStyle");
            if (inner is null || property is null || !property.CanWrite || !property.PropertyType.IsEnum) return;
            var now = Convert.ToInt32(property.GetValue(inner));
            var keep = WithoutHideText(now);
            if (keep != now) property.SetValue(inner, Enum.ToObject(property.PropertyType, keep));
        }
        catch (Exception) { /* this button may still lose its label */ }
    }
}
