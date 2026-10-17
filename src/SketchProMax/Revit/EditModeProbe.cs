using System.Collections;
using System.Reflection;
using System.Windows.Threading;

namespace SketchProMax.Revit;

/// <summary>
/// Tells an edit mode (a sketch, a group, an in-place family) from other reasons Revit won't run add-in work: in an edit
/// mode Revit's ribbon shows a Finish button (Finish Edit Mode, Finish, Finish Model) on the active or a contextual tab.
/// The ribbon (AdWindows) is read by reflection on Revit's UI thread through WPF's dispatcher, which keeps running while
/// Revit refuses API work. Bounded and best effort: null when it can't be told, and then nothing is claimed.
/// </summary>
internal static class EditModeProbe
{
    /// <summary>True: a Finish button is on the ribbon now; false: none; null: the ribbon couldn't be read in time.</summary>
    public static async Task<bool?> ShownAsync(int timeoutMs = 1500)
    {
        var ui = App.Ui;
        if (ui is null) return null;
        try
        {
            var op = ui.InvokeAsync(Look, DispatcherPriority.Send);
            if (await Task.WhenAny(op.Task, Task.Delay(timeoutMs)).ConfigureAwait(false) != op.Task)
            {
                op.Abort();
                return null;
            }
            return await op.Task.ConfigureAwait(false);
        }
        catch (Exception) { return null; }
    }

    private static bool? Look()
    {
        try
        {
            var manager = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AdWindows")?.GetType("Autodesk.Windows.ComponentManager");
            var ribbon = manager?.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (Get(ribbon, "Tabs") is not IEnumerable tabs) return null;
            foreach (var tab in tabs)
            {
                // The edit mode's Mode panel is on the active tab (Modify | ...) or a contextual one.
                if (tab is null || Get(tab, "IsVisible") is false || (Get(tab, "IsActive") is not true && Get(tab, "IsContextualTab") is not true)) continue;
                if (Get(tab, "Panels") is not IEnumerable panels) continue;
                foreach (var panel in panels)
                {
                    if (panel is null || Get(panel, "IsVisible") is false) continue;
                    if (Get(Get(panel, "Source"), "Items") is IEnumerable items && Any(items, 0)) return true;
                }
            }
            return false;
        }
        catch (Exception) { return null; }
    }

    private static bool Any(IEnumerable items, int depth)
    {
        if (depth > 4) return false;
        foreach (var item in items)
        {
            if (item is null || Get(item, "IsVisible") is false) continue;
            if (RevitBlock.IsFinishButton(Get(item, "Id") as string, Get(item, "Text") as string)) return true;
            // Rows, split buttons and drop-downs hold more items.
            if (Get(item, "Items") is IEnumerable inner && !(inner is string) && Any(inner, depth + 1)) return true;
        }
        return false;
    }

    private static object? Get(object? target, string property)
    {
        if (target is null) return null;
        try { return target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target); }
        catch (Exception) { return null; }
    }
}
