using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using SketchProMax.Revit;

namespace SketchProMax.UI;

/// <summary>
/// The Sketch ProMax icon in the Windows tray while Revit runs: a click opens the Rescue window at any time, also while
/// Revit doesn't respond (the icon lives on a thread of its own with its own message window, so Revit's stuck thread
/// can't block it). Its tooltip says whether Revit is responding. One icon per Revit; it goes when the core stops.
/// </summary>
internal static class RescueTray
{
    private const int Callback = 0x8000 + 41; // WM_APP + 41
    private const uint Id = 1;
    private static Dispatcher? _dispatcher;
    private static readonly object Gate = new();

    /// <summary>The icon's thread is running (for the field test).</summary>
    internal static bool Running { get { lock (Gate) return _dispatcher is not null; } }

    public static void Start()
    {
        lock (Gate) if (_dispatcher is not null) return;
        var thread = new Thread(Run) { IsBackground = true, Name = "Sketch ProMax tray icon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Stop()
    {
        Dispatcher? dispatcher;
        lock (Gate) { dispatcher = _dispatcher; _dispatcher = null; }
        try { dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal); } catch (Exception) { }
    }

    private static void Run()
    {
        HwndSource? source = null;
        var icon = IntPtr.Zero;
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, e) => { e.Handled = true; };
            lock (Gate) _dispatcher = dispatcher;
            // A message-only window to receive the icon's clicks.
            source = new HwndSource(new HwndSourceParameters("Sketch ProMax tray") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
            icon = LoadIcon();
            var hwnd = source.Handle;
            var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            var data = Data(hwnd, icon, Tip(FreezeWatch.Kind.Responding));
            Shell_NotifyIcon(NimAdd, ref data);
            source.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == Callback)
                {
                    var ev = (int)((long)l & 0xFFFF);
                    if (ev is WmLButtonUp or WmRButtonUp or NinSelect or NinKeySelect) RescueWindow.Open(automatic: false);
                    handled = true;
                }
                else if (taskbarCreated != 0 && msg == (int)taskbarCreated)
                {
                    // Explorer restarted: the tray was emptied.
                    var again = Data(hwnd, icon, Tip(FreezeWatch.Current.Kind));
                    Shell_NotifyIcon(NimAdd, ref again);
                }
                return IntPtr.Zero;
            });
            // The tooltip follows Revit's state.
            var last = FreezeWatch.Kind.Responding;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                var kind = FreezeWatch.Current.Kind;
                if (kind == last) return;
                last = kind;
                var tip = Data(hwnd, icon, Tip(kind));
                tip.Flags = NifTip;
                Shell_NotifyIcon(NimModify, ref tip);
            };
            timer.Start();
            Dispatcher.Run();
            timer.Stop();
            var gone = Data(hwnd, icon, "");
            Shell_NotifyIcon(NimDelete, ref gone);
        }
        catch (Exception) { /* no tray icon: the window still opens by itself */ }
        finally
        {
            try { source?.Dispose(); } catch (Exception) { }
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            lock (Gate) if (_dispatcher == Dispatcher.CurrentDispatcher) _dispatcher = null;
        }
    }

    /// <summary>The tooltip: what a click opens, and Revit's state.</summary>
    internal static string Tip(FreezeWatch.Kind kind) => "Sketch ProMax: " + kind switch
    {
        FreezeWatch.Kind.NotResponding => "Revit isn't responding. Click for help.",
        FreezeWatch.Kind.HiddenDialog => "a Revit dialog is waiting. Click to show it.",
        _ => "Revit is responding. Click if it seems stuck.",
    };

    /// <summary>The product icon, from the copy embedded in this DLL, written next to the settings once.</summary>
    private static IntPtr LoadIcon()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "rescue.ico");
            if (!File.Exists(file))
            {
                using var stream = typeof(RescueTray).Assembly.GetManifestResourceStream("SketchProMax.sketch-promax.ico");
                if (stream is null) return IntPtr.Zero;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using var output = File.Create(file);
                stream.CopyTo(output);
            }
            return LoadImage(IntPtr.Zero, file, 1 /* IMAGE_ICON */, 16, 16, 0x10 /* LR_LOADFROMFILE */);
        }
        catch (Exception) { return IntPtr.Zero; }
    }

    private static NotifyIconData Data(IntPtr hwnd, IntPtr icon, string tip) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = Id, Flags = NifMessage | NifTip | (icon != IntPtr.Zero ? NifIcon : 0),
        CallbackMessage = Callback, Icon = icon, Tip = tip.Length > 63 ? tip.Substring(0, 63) : tip,
    };

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, NinSelect = 0x0400, NinKeySelect = 0x0401;

    // The first (Windows 2000) layout: every Windows takes it, and its 64-character tip is enough.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id, Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Tip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
