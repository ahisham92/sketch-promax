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
/// Revit, restart Revit on the same model, or close it (Lifeline keeps the unsaved changes; the model's Lifeline window
/// offers Replay when it opens). The tray icon (UI/RescueTray) opens the window at any time. Every second it samples
/// Revit's processor time, memory, file traffic and journal length, so the window can say working or stuck.
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
    private static volatile string[] _models = [];
    private static readonly object SampleGate = new();
    private static readonly List<FreezeCause.Sample> Samples = [];
    private static readonly Process Own = Process.GetCurrentProcess();

    /// <summary>Revit's state now, and since when (read by the Rescue window and the tray icon). Any thread.</summary>
    public static (Kind Kind, DateTime Since) Current { get; private set; } = (Kind.Responding, DateTime.Now);

    /// <summary>Revit's theme as last read on Revit's thread.</summary>
    public static bool Dark => _dark;

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
        NoteModels(app);
        var stop = _stop = new CancellationTokenSource();
        try { UI.RescueTray.Start(); } catch (Exception) { /* the window still opens by itself */ }
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
        UI.RescueTray.Stop();
    }

    private static int _themeRead;

    /// <summary>
    /// Revit's theme and the open models (active first, for Restart Revit), read again on Revit's thread every 10 s:
    /// the watch's and the window's threads can't read them.
    /// </summary>
    private static void OnIdle(UIApplication app)
    {
        if (unchecked(Environment.TickCount - _themeRead) < 10_000) return;
        _themeRead = Environment.TickCount;
        try { _dark = UI.Brand.IsDark; } catch (Exception) { }
        NoteModels(app);
    }

    private static void NoteModels(UIApplication app)
    {
        try
        {
            var list = new List<string>();
            if (app.ActiveUIDocument?.Document is { IsFamilyDocument: false, PathName: { Length: > 0 } active }) list.Add(active);
            foreach (Autodesk.Revit.DB.Document doc in app.Application.Documents)
                if (!doc.IsFamilyDocument && !doc.IsLinked && doc.PathName is { Length: > 0 } path && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                    list.Add(path);
            _models = list.ToArray();
        }
        catch (Exception) { }
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
                Sample();
                CloseForAutopilot();
                var now = Read(_main);
                if (now != kind)
                {
                    if (kind != Kind.Responding && shown) Log(kind, DateTime.Now - since, opened: true);
                    if (kind != Kind.Responding) UI.RescueWindow.Responding(DateTime.Now - since);
                    if (kind != Kind.Responding && !shown && DateTime.Now - since >= TimeSpan.FromSeconds(10)) Log(kind, DateTime.Now - since, opened: false);
                    kind = now;
                    since = DateTime.Now;
                    shown = false;
                    Current = (kind, since);
                }
                else if (kind == Kind.NotResponding && Chat.Autopilot.Unattended && DateTime.Now - _hangChecked >= TimeSpan.FromSeconds(30))
                    RestartIfHung(DateTime.Now - since);
                if (kind != Kind.Responding && !shown && DateTime.Now - since >= OpenAfter
                         && (kind == Kind.HiddenDialog || DateTime.Now - since >= FreezeCause.OpenAfter(Cause())))
                {
                    shown = true;
                    UI.RescueWindow.Open(automatic: true);
                }
            }
            catch (Exception) { /* the watch must never take Revit down */ }
            if (stop.WaitHandle.WaitOne(1000)) break;
        }
    }

    private static DateTime _blockedSince = DateTime.MaxValue, _hangChecked;

    /// <summary>
    /// An autopilot at work and Revit not responding (Autopilot.RestartAfterHang: 10 minutes doing nothing at all, 45 while
    /// busy): Revit is restarted on the same model, as the Rescue window's Restart would; Lifeline replays the unsaved
    /// changes, the backup was saved at the start, and the new Revit carries the run on (ChatPane.AutopilotRecover).
    /// </summary>
    private static void RestartIfHung(TimeSpan lasted)
    {
        _hangChecked = DateTime.Now;
        var activity = Activity();
        var cause = Cause();
        if (!Chat.Autopilot.RestartAfterHang(Chat.Autopilot.Unattended, true, lasted, activity.Progress == FreezeCause.Progress.Stuck, FreezeCause.IsLongWork(cause))) return;
        var why = $"Revit stopped responding for {FreezeCause.Duration(lasted)} (busy with {cause.What}), so it was restarted on the same model";
        // Written to the shared files: Revit's own thread is stuck and won't save anything now.
        if (Chat.AutopilotShare.Any(DateTime.UtcNow) is { } run) Chat.AutopilotShare.AppendLog(run, Chat.AutopilotEntry.Note, why + ".", DateTime.UtcNow);
        Chat.AutopilotShare.MarkRestart(why);
        Tools.ActivityLog.NoteNow("autopilot_restart", why);
        RestartRevit("autopilot: " + why);
    }

    /// <summary>
    /// An autopilot at work and a dialog Revit's own events didn't reach (another add-in's, Windows') holding Revit for a
    /// minute: it is closed, as Esc or its X would, and the report says so. Outside autopilot nothing is touched.
    /// </summary>
    private static void CloseForAutopilot()
    {
        if (!Chat.Autopilot.Unattended || _main == IntPtr.Zero || IsWindowEnabled(_main))
        {
            _blockedSince = DateTime.MaxValue;
            return;
        }
        if (_blockedSince == DateTime.MaxValue) _blockedSince = DateTime.Now;
        if (DateTime.Now - _blockedSince < TimeSpan.FromMinutes(1)) return;
        var dialog = DialogWindow(_main);
        if (dialog == IntPtr.Zero) return;
        PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
        _blockedSince = DateTime.Now;
        Chat.Autopilot.Log(Chat.AutopilotEntry.Popup, "A window held Revit for a minute; it was closed.");
        Tools.ActivityLog.Note("autopilot_closed_dialog", "A dialog held Revit for a minute during autopilot; it was closed");
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

    /// <summary>Revit's processor time, memory, file traffic and journal length now, kept for the last 3 minutes.</summary>
    private static void Sample()
    {
        try
        {
            Own.Refresh();
            long files = GetProcessIoCounters(Own.Handle, out var io) ? (long)(io.ReadBytes + io.WriteBytes + io.OtherBytes) : 0;
            long journal = 0;
            try { if (_journal is { } j) journal = new FileInfo(j).Length; } catch (Exception) { }
            var sample = new FreezeCause.Sample(DateTime.Now, Own.TotalProcessorTime, Own.PrivateMemorySize64, files, journal);
            lock (SampleGate)
            {
                Samples.Add(sample);
                if (Samples.Count > 180) Samples.RemoveAt(0);
            }
        }
        catch (Exception) { }
    }

    /// <summary>Working or stuck, from the last samples. Any thread.</summary>
    public static FreezeCause.Activity Activity()
    {
        List<FreezeCause.Sample> copy;
        lock (SampleGate) copy = Samples.ToList();
        return FreezeCause.Judge(copy, Environment.ProcessorCount, DateTime.Now);
    }

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

    /// <summary>
    /// A Sketch ProMax task, tool call, self-test or any work of ours on Revit's thread (a Lifeline replay) is running now.
    /// </summary>
    public static bool OursRunning => OursCancellable || App.Dispatcher is { IsRunning: true };

    /// <summary>A task, tool call or self-test the Stop button can stop.</summary>
    public static bool OursCancellable => Jobs.Running is { } job && job.Task is not { IsCompleted: true } || JobControl.Current is { Over: false }
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

    /// <summary>The model Restart Revit opens again (the active one when Revit last answered), or null.</summary>
    public static string? ModelToReopen => _models.FirstOrDefault(File.Exists);

    /// <summary>
    /// Ends this Revit and starts it again on the same model (the active one), without Task Manager: Lifeline's lines are
    /// written out first, then a new Revit is started (it opens the model and Lifeline's window offers Replay), then this
    /// one is ended. Without a model to open, Revit starts empty.
    /// </summary>
    public static void RestartRevit(string why)
    {
        try { ChangeFile.Flush(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        var model = ModelToReopen;
        Tools.ActivityLog.NoteNow("rescue_restart", $"Revit restarted from the Rescue window ({why})" + (model is null ? "" : $", reopening {Path.GetFileName(model)}"));
        try
        {
            var exe = Own.MainModule?.FileName;
            if (exe is { Length: > 0 })
                Process.Start(new ProcessStartInfo(exe, model is null ? "" : "\"" + model + "\"")
                {
                    UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                });
        }
        catch (Exception) { /* Revit is still ended: the user starts it from the Start menu */ }
        try { Own.Kill(); } catch (Exception) { }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
