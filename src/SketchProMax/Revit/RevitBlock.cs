using System.Runtime.InteropServices;
using System.Text;
using SketchProMax.Tools;

namespace SketchProMax.Revit;

/// <summary>
/// Why Revit isn't taking a request, worked out from outside Revit's UI thread: a modal dialog (Revit's main window is
/// disabled while one is open; its title is read too), a UI thread that has stopped answering (a long operation of
/// Revit's own), an edit mode (only when its Finish button is seen on the ribbon, see EditModeProbe), or none of those
/// that can be told (a command of Revit's own still running, most often; Esc ends it).
/// Only Win32 calls that never send a message to Revit's thread are used (no GetWindowText), so this can't hang when
/// Revit is the thing that is stuck. The wording (<see cref="Explain"/>) is pure, so the tests compile it.
/// </summary>
internal static class RevitBlock
{
    public enum Kind { None, Dialog, NotResponding, Unknown, EditMode }

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
    /// The message for a request Revit didn't start, for Claude. <paramref name="what"/> names the request ("The request",
    /// "Switching models"); <paramref name="seconds"/> is how long it waited.
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
            Kind.EditMode => lead + "Revit is in an edit mode (its Finish button is on the ribbon: a sketch, a group or an in-place family " +
                             "is being edited). Ask the user to finish or cancel it in Revit, then try again.",
            // Nothing that can be seen from outside: say so plainly rather than guess at an edit mode.
            _ => lead + "Revit is busy with something of its own, most often a command that is still running. Ask the user, in one " +
                 "short sentence, to press Esc in Revit once or twice and say when done, then try again.",
        };
    }

    /// <summary>
    /// The tool, as the user would say it ("reading your model"), for the card: never the tool's id. Reads that aren't
    /// named here are "reading your model"; changes are "the change you asked for".
    /// </summary>
    public static string Friendly(string tool, bool changes) => tool switch
    {
        "get_model_context" or "get_elements" or "get_element_parameters" or "list_types" or "list_levels" or "list_views" or "list_sheets"
            or "run_revit_query_code" or "check_model" => "reading your model",
        "capture_view_image" => "taking a picture of the view",
        "list_revit_models" or "use_revit_model" => "finding the open models",
        "export_pdf" or "export_files" => "exporting",
        "run_revit_code" => "running the code",
        _ => changes ? "the change you asked for" : "reading your model",
    };

    /// <summary>
    /// The card the user sees for a call Revit didn't start: one short line each for what to do, where, and why. Says
    /// "edit mode" only when one was seen (<see cref="Kind.EditMode"/>); otherwise it doesn't guess.
    /// </summary>
    public static HandoffStep Card(State state, string friendly)
    {
        var nothing = " Nothing was changed.";
        return state.Kind switch
        {
            Kind.Dialog => new HandoffStep
            {
                Kind = "dialog",
                What = string.IsNullOrWhiteSpace(state.Title) ? "Close the dialog in Revit" : $"Close the dialog '{state.Title!.Trim()}' in Revit",
                Where = "Revit's window (the dialog may be behind another window), then click Continue",
                Why = $"Revit can't start {friendly} while a dialog is open." + nothing,
            },
            Kind.NotResponding => new HandoffStep
            {
                Kind = "busy", What = "Revit is busy with a long task",
                Where = "Wait until Revit responds, then click Continue",
                Why = $"Revit can't start {friendly} until it's done." + nothing,
            },
            Kind.EditMode => new HandoffStep
            {
                Kind = "edit_mode", What = "Finish or cancel the edit in Revit",
                Where = "Modify tab: the green tick (Finish) or the red cross (Cancel), then click Continue",
                Why = $"Revit can't start {friendly} while you edit." + nothing,
            },
            _ => new HandoffStep
            {
                Kind = "busy", What = "Revit is busy",
                Where = "Press Esc in Revit once or twice, then click Continue",
                Why = $"Revit didn't start {friendly} yet." + nothing,
            },
        };
    }

    /// <summary>True for a ribbon button that ends an edit mode: Finish Edit Mode, Finish (a group), Finish Model (in place).</summary>
    public static bool IsFinishButton(string? id, string? text)
    {
        if (id is { Length: > 0 } && id.IndexOf("FINISH", StringComparison.OrdinalIgnoreCase) >= 0
            && id.IndexOf("FINISHES", StringComparison.OrdinalIgnoreCase) < 0) return true;
        var t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Equals("Finish", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Finish Edit", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Finish Model", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// The window class that has the keyboard on Revit's UI thread ("HwndWrapper..." is a WPF pane such as the chat;
    /// "AfxFrameOrView..." a Revit view), for the log of a request Revit didn't take. Sends Revit no message.
    /// </summary>
    public static string FocusClass(IntPtr mainWindow)
    {
        try
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (mainWindow == IntPtr.Zero || !GetGUIThreadInfo(GetWindowThreadProcessId(mainWindow, out _), ref info)) return "?";
            if (info.Focus == IntPtr.Zero) return "nothing";
            var sb = new StringBuilder(128);
            return GetClassName(info.Focus, sb, sb.Capacity) > 0 ? sb.ToString() : "?";
        }
        catch (Exception) { return "?"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int max);
}
