using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;

namespace SketchProMax.Revit;

/// <summary>
/// Watches Revit's main window from a thread of its own and opens the Rescue window (UI/RescueWindow) when Revit stops
/// responding for <see cref="OpenAfter"/>, or when a dialog keeps Revit waiting while it can't be seen (Revit's window is
/// disabled and in front, the dialog behind it or on another screen). The window runs on its own thread too, so its
/// buttons work while Revit's thread is stuck: stop Sketch ProMax's task, bring the dialog to the front, press Esc in
/// Revit, or close Revit (Lifeline keeps the unsaved changes; the model's Lifeline window offers Replay when it opens).
/// Only Win32 calls that send nothing to Revit's thread are used here, so the watch can't hang with Revit.
/// Every freeze is written to the activity log with what Revit was busy with, read from the end of Revit's journal.
/// </summary>
internal static class FreezeWatch
{
    /// <summary>How long Revit must stay stuck before the Rescue window opens (longer for everyday long work, see FreezeCause.OpenAfter).</summary>
    public static readonly TimeSpan OpenAfter = TimeSpan.FromSeconds(20);

    public enum Kind { Responding, NotResponding, HiddenDialog }

    private static Thread? _thread;
    private static CancellationTokenSource? _stop;
    private static IntPtr _main;
    private static string? _journal;
    private static bool _dark;

    /// <summary>The journal Revit is writing (for the field test).</summary>
    internal static string? Journal => _journal;

    /// <summary>Starts the watch. Revit's thread (reads the main window, the journal's path and the theme).</summary>
    public static void Start(UIApplication app)
    {
        Stop();
        _main = App.MainWindow;
        try { _journal = app.Application.RecordingJournalFilename; } catch (Exception) { _journal = null; }
        _dark = UI.Brand.IsDark;
        if (_main == IntPtr.Zero) return;
        RevitDispatcher.Idle += OnIdle;
        var stop = _stop = new CancellationTokenSource();
        _thread = new Thread(() => Run(stop.Token)) { IsBackground = true, Name = "Sketch ProMax freeze watch", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public static void Stop()
    {
        RevitDispatcher.Idle -= OnIdle;
        try { _stop?.Cancel(); } catch (Exception) { }
        _stop = null;
        _thread = null;
        UI.RescueWindow.Close();
    }

    private static int _themeRead;

    /// <summary>Revit's theme, read again on Revit's thread every 30 s (the window's thread can't read it).</summary>
    private static void OnIdle(UIApplication app)
    {
        if (unchecked(Environment.TickCount - _themeRead) < 30_000) return;
        _themeRead = Environment.TickCount;
        try { _dark = UI.Brand.IsDark; } catch (Exception) { }
    }

    private static void Run(CancellationToken stop)
    {
        var since = DateTime.MinValue;
        var kind = Kind.Responding;
        var shown = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = Read(_main);
                if (now != kind)
                {
                    if (kind != Kind.Responding && shown) Ended(kind, DateTime.Now - since);
                    if (kind != Kind.Responding && !shown && DateTime.Now - since >= TimeSpan.FromSeconds(10)) Log(kind, DateTime.Now - since, opened: false);
                    kind = now;
                    since = DateTime.Now;
                    shown = false;
                }
                else if (kind != Kind.Responding && !shown && DateTime.Now - since >= OpenAfter
                         && (kind == Kind.HiddenDialog || DateTime.Now - since >= FreezeCause.OpenAfter(Cause())))
                {
                    shown = true;
                    UI.RescueWindow.Open(kind, since, _dark);
                }
            }
            catch (Exception) { /* the watch must never take Revit down */ }
            if (stop.WaitHandle.WaitOne(1000)) break;
        }
    }

    private static void Ended(Kind kind, TimeSpan lasted)
    {
        Log(kind, lasted, opened: true);
        UI.RescueWindow.Responding(lasted);
    }

    private static void Log(Kind kind, TimeSpan lasted, bool opened)
    {
        var busy = kind == Kind.HiddenDialog ? "a dialog that was out of sight" : Cause().What;
        Tools.ActivityLog.Note("revit_froze", $"Revit {(kind == Kind.HiddenDialog ? "waited for" : "didn't respond, busy with")} {busy} for {FreezeCause.Duration(lasted)}" +
                                              (opened ? "; the Rescue window opened" : ""));
    }

    /// <summary>Whether Revit is stuck, from outside its thread.</summary>
    public static Kind Read(IntPtr main)
    {
        if (main == IntPtr.Zero) return Kind.Responding;
        if (IsHungAppWindow(main)) return Kind.NotResponding;
        // A modal dialog disables Revit's window. Revit's window in front while disabled: the dialog is out of sight.
        if (!IsWindowEnabled(main) && GetForegroundWindow() == main && DialogWindow(main) != IntPtr.Zero) return Kind.HiddenDialog;
        return Kind.Responding;
    }

    // ---- What Revit is busy with -----------------------------------------------------------------------------------

    /// <summary>What Revit is busy with, from the end of its journal. Any thread; never throws.</summary>
    public static FreezeCause.Busy Cause() => FreezeCause.Read(JournalTail(_journal));

    /// <summary>The journal's last lines (up to 512 KB), read while Revit keeps it open.</summary>
    internal static List<string> JournalTail(string? file, int bytes = 512 * 1024)
    {
        var lines = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return lines;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > bytes) stream.Seek(-bytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (stream.Position > 0) reader.ReadLine(); // the first line is cut
            while (reader.ReadLine() is { } line) lines.Add(line);
        }
        catch (Exception) { }
        return lines;
    }

    // ---- What the Rescue window's buttons do ------------------------------------------------------------------------

    /// <summary>A Sketch ProMax task, tool call or self-test is running now.</summary>
    public static bool OursRunning => Jobs.Running is { } job && job.Task is not { IsCompleted: true } || JobControl.Current is { Over: false }
                                      || Tools.SelfTest.IsRunning;

    /// <summary>Asks every Sketch ProMax task to stop at its next check (the item in progress is rolled back).</summary>
    public static void StopOurs()
    {
        try { CancelJobTool.Cancel(new Tools.ToolArgs(new Dictionary<string, System.Text.Json.JsonElement>())); } catch (Exception) { }
        try { JobControl.Current?.Apply(JobAction.Stop); } catch (Exception) { }
        try { if (Tools.SelfTest.IsRunning) Tools.SelfTest.Stop(); } catch (Exception) { }
        Tools.ActivityLog.Note("rescue", "Stop Sketch ProMax task pressed in the Rescue window");
    }

    /// <summary>Brings Revit's dialog to the front, over the middle of Revit's window. False when there is none.</summary>
    public static bool ShowDialog()
    {
        var dialog = DialogWindow(_main);
        if (dialog == IntPtr.Zero) return false;
        try
        {
            if (GetWindowRect(_main, out var main) && GetWindowRect(dialog, out var d))
            {
                int w = d.Right - d.Left, h = d.Bottom - d.Top;
                var x = main.Left + (main.Right - main.Left - w) / 2;
                var y = main.Top + (main.Bottom - main.Top - h) / 2;
                SetWindowPos(dialog, HwndTop, x, y, 0, 0, SwpNoSize | SwpShowWindow);
            }
            ShowWindow(dialog, SwRestore);
            SetForegroundWindow(dialog);
            BringWindowToTop(dialog);
        }
        catch (Exception) { }
        Tools.ActivityLog.Note("rescue", "Show the dialog pressed in the Rescue window");
        return true;
    }

    /// <summary>
    /// Presses Esc in Revit (some long Revit operations stop on it): Revit's window is put in front, then the key is
    /// typed, as a person would. Allowed because the click on the Rescue window made this process the foreground one.
    /// </summary>
    public static void PressEsc()
    {
        try
        {
            SetForegroundWindow(_main);
            var inputs = new[] { Key(VkEscape, up: false), Key(VkEscape, up: true) };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            PostMessage(_main, WmKeyDown, (IntPtr)VkEscape, IntPtr.Zero);
            PostMessage(_main, WmKeyUp, (IntPtr)VkEscape, IntPtr.Zero);
        }
        catch (Exception) { }
        Tools.ActivityLog.Note("rescue", "Press Esc pressed in the Rescue window");
    }

    /// <summary>
    /// Closes Revit at once, without saving (Revit can't save while it is stuck). Lifeline's lines are written out first,
    /// so the changes since the last save are in the model's change file, offered for Replay when the model opens again.
    /// </summary>
    public static void CloseRevit(string why)
    {
        try { ChangeFile.Flush(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        Tools.ActivityLog.NoteNow("rescue_close", "Revit closed from the Rescue window: " + why);
        try { Process.GetCurrentProcess().Kill(); } catch (Exception) { }
    }

    /// <summary>Lifeline's files for the open models: the model's name, the changes kept since its last save, and since when.</summary>
    public static List<(string Model, int Changes, DateTime? Since)> Lifeline()
    {
        var list = new List<(string, int, DateTime?)>();
        try
        {
            foreach (var (model, file) in ChangeFile.CurrentFiles())
            {
                if (!File.Exists(file)) continue;
                var parsed = ChangeFileMath.Read(ChangeFile.ReadLines(file));
                if (parsed.Elements.Count > 0) list.Add((Path.GetFileNameWithoutExtension(model), parsed.Elements.Count, parsed.First));
            }
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>The enabled, visible window of this process owned by Revit's main window (the modal dialog), or zero.</summary>
    private static IntPtr DialogWindow(IntPtr main)
    {
        IntPtr found = IntPtr.Zero;
        var pid = (uint)Process.GetCurrentProcess().Id;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == main || !IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid) return true;
            for (var (h, depth) = (GetWindow(hwnd, GwOwner), 0); h != IntPtr.Zero && depth < 8; h = GetWindow(h, GwOwner), depth++)
                if (h == main) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static Input Key(ushort vk, bool up) => new() { Type = 1, Ki = new KeybdInput { Vk = vk, Flags = up ? 2u : 0u } };

    private const ushort VkEscape = 0x1B;
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, GwOwner = 4, SwpNoSize = 0x0001, SwpShowWindow = 0x0040;
    private const int SwRestore = 9;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }

    // INPUT's union is as large as MOUSEINPUT: pad so SendInput gets the size it expects.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public KeybdInput Ki; public long Pad; }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
