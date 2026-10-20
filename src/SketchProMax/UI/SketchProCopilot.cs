using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Autodesk.Revit.UI;

namespace SketchProMax.UI;

/// <summary>
/// The checklist's helper when it is SketchPro (another company's add-in, with no way for other programs to talk to it):
/// clicks its ribbon button Show Copilot (SketchPro tab, found by its label, so its internal name doesn't matter), waits
/// for its chat to appear, clicks into the chat's message box and pastes the question that is on the clipboard (Ctrl+V).
/// It never presses Enter: the user reads the question and sends it. Anything not found leaves the question on the
/// clipboard to paste by hand. The ribbon is read by reflection (AdWindows); the chat is found with UI Automation on a
/// background thread (UI Automation must not query this process's own windows from Revit's UI thread).
/// </summary>
internal static class SketchProCopilot
{
    private const string Tab = "SketchPro", Button = "Show Copilot";

    /// <summary>Posts SketchPro's Show Copilot command. Revit API context. Returns "" when posted, else why not.</summary>
    public static string Open(UIApplication app)
    {
        var id = ButtonId();
        if (id is null) return "SketchPro's Show Copilot button isn't on the ribbon (is SketchPro installed and loaded?).";
        try
        {
            var command = RevitCommandId.LookupCommandId(id);
            if (command is null || !app.CanPostCommand(command)) return "Revit won't run SketchPro's Show Copilot right now.";
            app.PostCommand(command);
            return "";
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException or InvalidOperationException)
        {
            return "Revit couldn't run SketchPro's Show Copilot (" + ex.Message + ").";
        }
    }

    /// <summary>The command id of SketchPro's Show Copilot button, read from Revit's ribbon; null when it isn't there.</summary>
    internal static string? ButtonId()
    {
        try
        {
            var manager = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AdWindows")?.GetType("Autodesk.Windows.ComponentManager");
            var ribbon = manager?.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (Get(ribbon, "Tabs") is not IEnumerable tabs) return null;
            foreach (var tab in tabs)
            {
                if (tab is null || !Same(Get(tab, "Title") as string ?? Get(tab, "Name") as string, Tab)) continue;
                if (Get(tab, "Panels") is not IEnumerable panels) continue;
                foreach (var panel in panels)
                    if (Get(Get(panel, "Source"), "Items") is IEnumerable items && Find(items, 0) is { } id) return id;
            }
        }
        catch (Exception) { /* the ribbon changed: paste by hand */ }
        return null;
    }

    private static string? Find(IEnumerable items, int depth)
    {
        if (depth > 4) return null;
        foreach (var item in items)
        {
            if (item is null) continue;
            var text = (Get(item, "Text") as string ?? "").Replace("\r", " ").Replace("\n", " ");
            if (Same(text, Button) && Get(item, "Id") is string { Length: > 0 } id) return id;
            if (Get(item, "Items") is IEnumerable inner && inner is not string && Find(inner, depth + 1) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for SketchPro's chat (a window or pane of this process named SketchPro…), clicks
    /// into its message box and presses Ctrl+V. Call off Revit's UI thread. True when pasted.
    /// While waiting it only looks inside windows titled SketchPro… or …Copilot (cheap). A UI Automation search of all of
    /// Revit's windows makes Revit's UI thread answer for every ribbon button and pane item, which held Revit for seconds
    /// per search; it runs at most once, as the last try.
    /// </summary>
    public static bool Paste(IntPtr revit, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            if (TryPaste(() => MessageBox(Titled(revit)))) return true;
            Thread.Sleep(400);
        }
        return TryPaste(() => MessageBox(Everything(revit)));
    }

    private static bool TryPaste(Func<AutomationElement?> find)
    {
        try
        {
            if (find() is not { } box) return false;
            box.SetFocus();
            Thread.Sleep(150);
            PressCtrlV();
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return false; }
    }

    /// <summary>This process's windows (Revit's child windows and its own top-level ones) titled like SketchPro's chat.</summary>
    private static List<AutomationElement> Titled(IntPtr revit)
    {
        var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        var found = new List<IntPtr>();
        bool Look(IntPtr hwnd, IntPtr _)
        {
            var length = GetWindowTextLength(hwnd);
            if (length > 0 && length < 200)
            {
                var text = new System.Text.StringBuilder(length + 1);
                GetWindowText(hwnd, text, text.Capacity);
                if (IsSketchProName(text.ToString())) found.Add(hwnd);
            }
            return true;
        }
        if (revit != IntPtr.Zero) EnumChildWindows(revit, Look, IntPtr.Zero);
        EnumWindows((hwnd, l) => { GetWindowThreadProcessId(hwnd, out var owner); if (owner == pid) { Look(hwnd, l); EnumChildWindows(hwnd, Look, IntPtr.Zero); } return true; }, IntPtr.Zero);
        var roots = new List<AutomationElement>();
        foreach (var hwnd in found.Distinct())
        {
            try { roots.Add(AutomationElement.FromHandle(hwnd)); }
            catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException or COMException) { }
        }
        return roots;
    }

    /// <summary>All of Revit's windows (the slow search).</summary>
    private static List<AutomationElement> Everything(IntPtr revit)
    {
        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var roots = new List<AutomationElement>();
        if (revit != IntPtr.Zero) roots.Add(AutomationElement.FromHandle(revit));
        // A floating pane is its own top-level window.
        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
            if (!roots.Contains(top)) roots.Add(top);
        return roots;
    }

    /// <summary>
    /// The chat's message box: an editable box under <paramref name="roots"/> that sits inside something named SketchPro…
    /// or …Copilot (the dockable pane, or its window when floating); the lowest one on screen when there are several.
    /// </summary>
    private static AutomationElement? MessageBox(List<AutomationElement> roots)
    {
        var edits = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true));
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? lowest = null;
        foreach (var root in roots)
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, edits))
            {
                if (e.Current.IsOffscreen || !InSketchPro(e, walker)) continue;
                if (lowest is null || e.Current.BoundingRectangle.Bottom > lowest.Current.BoundingRectangle.Bottom) lowest = e;
            }
        return lowest;
    }

    private static bool IsSketchProName(string name) =>
        name.StartsWith(Tab, StringComparison.OrdinalIgnoreCase) && name.IndexOf("ProMax", StringComparison.OrdinalIgnoreCase) < 0
        || name.IndexOf("Copilot", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool InSketchPro(AutomationElement e, TreeWalker walker)
    {
        var node = e;
        for (var i = 0; i < 30 && node is not null; i++)
        {
            if (IsSketchProName(node.Current.Name ?? "")) return true;
            node = walker.GetParent(node);
        }
        return false;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    private static void PressCtrlV()
    {
        const byte Ctrl = 0x11, V = 0x56;
        const uint Up = 0x2;
        keybd_event(Ctrl, 0, 0, UIntPtr.Zero);
        keybd_event(V, 0, 0, UIntPtr.Zero);
        keybd_event(V, 0, Up, UIntPtr.Zero);
        keybd_event(Ctrl, 0, Up, UIntPtr.Zero);
    }

    private static bool Same(string? a, string b) => string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase);

    private static object? Get(object? target, string property)
    {
        if (target is null) return null;
        try { return target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target); }
        catch (Exception) { return null; }
    }
}
