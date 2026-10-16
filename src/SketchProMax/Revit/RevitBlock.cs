using System.Runtime.InteropServices;
using System.Text;

namespace SketchProMax.Revit;

/// <summary>
/// Why Revit isn't taking a request, worked out from outside Revit's UI thread: a modal dialog (Revit's main window is
/// disabled while one is open; its title is read too), a UI thread that has stopped answering (a long operation of
/// Revit's own), or neither (an edit mode, or a command that keeps Revit from running add-in work).
/// Only Win32 calls that never send a message to Revit's thread are used (no GetWindowText), so this can't hang when
/// Revit is the thing that is stuck. The wording (<see cref="Explain"/>) is pure, so the tests compile it.
/// </summary>
internal static class RevitBlock
{
    public enum Kind { None, Dialog, NotResponding, Unknown }

    public readonly record struct State(Kind Kind, string? Title);

    /// <summary>Looks at Revit's main window. Safe on any thread; never throws.</summary>
    public static State Read(IntPtr mainWindow)
    {
        if (mainWindow == IntPtr.Zero) return new State(Kind.Unknown, null);
        try
        {
            if (IsHungAppWindow(mainWindow)) return new State(Kind.NotResponding, null);
            if (IsWindowEnabled(mainWindow)) return new State(Kind.None, null);
            return new State(Kind.Dialog, DialogTitle(mainWindow));
        }
        catch (Exception) { return new State(Kind.Unknown, null); }
    }

    /// <summary>
    /// The message for a request Revit didn't start. <paramref name="what"/> names the request ("The request", "Switching
    /// models"); <paramref name="seconds"/> is how long it waited.
    /// </summary>
    public static string Explain(State state, string what, int seconds)
    {
        var lead = $"{what} could not start: Revit didn't take it within {seconds} s, so it was dropped and nothing was changed. ";
        return state.Kind switch
        {
            Kind.Dialog => lead + (string.IsNullOrWhiteSpace(state.Title)
                               ? "Revit has a dialog or message box open. "
                               : $"Revit has a dialog open: '{state.Title!.Trim()}'. ") +
                           "Ask the user to answer or close it in Revit (it may be behind another window), then try again.",
            Kind.NotResponding => lead + "Revit is busy with a long operation of its own and isn't responding right now (for example a " +
                                  "regeneration, sync with central, loading a model or printing). Ask the user to wait until Revit " +
                                  "responds again, or to cancel that operation in Revit, then try again. Don't send other work meanwhile.",
            _ => lead + "Revit is probably in an edit mode (editing a family in place, a group, a sketch such as a floor boundary, or " +
                 "Edit Profile) or running a command of its own. Ask the user to finish or cancel it in Revit (Finish or Cancel Edit " +
                 "Mode, press Esc), then try again.",
        };
    }

    /// <summary>The title of the topmost visible window of this process owned by <paramref name="main"/> (the modal dialog).</summary>
    private static string? DialogTitle(IntPtr main)
    {
        string? found = null, fallback = null;
        var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        // EnumWindows goes top of the Z order first, so the first match is the dialog in front.
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == main || !IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid) return true;
            var title = Title(hwnd);
            if (OwnedBy(hwnd, main))
            {
                if (IsWindowEnabled(hwnd) && title.Length > 0) { found = title; return false; }
                fallback ??= title.Length > 0 ? title : null;
            }
            return true;
        }, IntPtr.Zero);
        return found ?? fallback;
    }

    private static bool OwnedBy(IntPtr hwnd, IntPtr main)
    {
        var h = GetWindow(hwnd, 4 /* GW_OWNER */);
        for (var depth = 0; h != IntPtr.Zero && depth < 8; depth++)
        {
            if (h == main) return true;
            h = GetWindow(h, 4);
        }
        return false;
    }

    private static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        // InternalGetWindowText reads the title the window manager keeps: no message to the (possibly busy) owner thread.
        return InternalGetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int max);
}
