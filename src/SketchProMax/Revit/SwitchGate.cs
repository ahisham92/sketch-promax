using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SketchProMax.Revit;

/// <summary>
/// Whether a newer core may be switched to now, beyond our own work (App.IsBusy). The loader switches on Idling, and
/// Revit raises Idling inside placement tools too: on 27 Oct 2026 a switch in the middle of Create Spot Elevation closed
/// Revit. Revit has no public "a command is running" flag, so the switch waits while either is true:
/// someone used the mouse or keyboard in the last <see cref="Quiet"/> (they are working), or the ribbon shows a
/// contextual tab (Modify | ..., shown while a tool runs, in an edit mode, or with something selected). The loader asks
/// again at the next Idling, so the switch happens once Revit is quiet; Revit's next start uses the newer core anyway.
/// </summary>
internal static class SwitchGate
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(20);

    /// <summary>True when switching cores now could land inside a running Revit command. Revit's UI thread.</summary>
    public static bool Wait() => RecentInput() || ContextualTab() != false;

    private static bool RecentInput()
    {
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return false;
            return unchecked((uint)Environment.TickCount - info.Time) < Quiet.TotalMilliseconds;
        }
        catch (Exception) { return false; }
    }

    /// <summary>True: a contextual tab shows; false: none; null: the ribbon couldn't be read (then the switch waits).</summary>
    internal static bool? ContextualTab()
    {
        try
        {
            if (App.Ui is { } ui && !ui.CheckAccess()) return null;
            var manager = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AdWindows")?.GetType("Autodesk.Windows.ComponentManager");
            var ribbon = manager?.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (Get(ribbon, "Tabs") is not IEnumerable tabs) return null;
            foreach (var tab in tabs)
                if (tab is not null && Get(tab, "IsContextualTab") is true && Get(tab, "IsVisible") is true) return true;
            return false;
        }
        catch (Exception) { return null; }
    }

    private static object? Get(object? target, string property)
    {
        if (target is null) return null;
        try { return target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target); }
        catch (Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
