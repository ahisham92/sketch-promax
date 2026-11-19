using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using SketchProMax.Settings;
using W = SketchProMax.Revit.LifelineWatchMath;

namespace SketchProMax.Revit;

/// <summary>
/// Lifeline's watch: small hints in the corner when the central model's server stops answering, when the PC shows a
/// sign that often comes before a crash (memory, disk, window handles), or when a lot is unsaved; and, when the user
/// turns it on, saving the local copy by itself every so many minutes. A hint never asks, never takes the focus and
/// never stops a command; the save runs only at one of Revit's idle moments with no tool open, and never synchronizes.
/// </summary>
internal static class LifelineWatch
{
    private sealed record Open(string Path, string Title, string Central, int Changes, bool Modified);

    private static readonly object Gate = new();
    private static Autodesk.Revit.ApplicationServices.Application? _app;
    private static System.Threading.Timer? _timer;
    private static bool _warn = true, _autoSave;
    private static int _minutes = W.DefaultMinutes;
    private static int _lastSettings, _lastLook, _sampling;
    private static List<Open> _models = [];
    private static readonly Dictionary<string, DateTime> Saved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (int Fails, bool Lost)> Servers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> Shown = [];
    private static readonly HashSet<string> Muted = [];
    private static DateTime _failed = DateTime.MinValue;
    private static string? _saving;
    private static int _savingAt;

    /// <summary>For the field test: hints are collected here instead of shown.</summary>
    internal static List<W.Warning>? HintsForTest;

    public static void Start(UIApplication app)
    {
        if (_app is not null) return;
        _app = app.Application;
        ReadSettings();
        _app.DocumentOpened += OnOpened;
        _app.DocumentSaved += OnSaved;
        _app.DocumentSavedAs += OnSavedAs;
        _app.DocumentSynchronizedWithCentral += OnSynced;
        _app.DocumentClosed += OnClosed;
        RevitDispatcher.Idle += OnIdle;
        _timer = new System.Threading.Timer(_ => Sample(), null, 45_000, 60_000);
    }

    public static void Stop()
    {
        try { _timer?.Dispose(); } catch (Exception) { }
        _timer = null;
        RevitDispatcher.Idle -= OnIdle;
        if (_app is { } app)
        {
            try
            {
                app.DocumentOpened -= OnOpened;
                app.DocumentSaved -= OnSaved;
                app.DocumentSavedAs -= OnSavedAs;
                app.DocumentSynchronizedWithCentral -= OnSynced;
                app.DocumentClosed -= OnClosed;
            }
            catch (Exception) { }
        }
        _app = null;
    }

    public static void ReadSettings()
    {
        var s = AgentSettings.Load();
        _warn = s.LifelineWarnings;
        _autoSave = s.LifelineAutoSave;
        _minutes = W.ClampMinutes(s.LifelineAutoSaveMinutes);
        _lastSettings = Environment.TickCount;
    }

    private static string PathOf(Document? doc) { try { return doc is { IsValidObject: true } ? doc.PathName ?? "" : ""; } catch (Exception) { return ""; } }

    private static void Mark(Document? doc)
    {
        if (PathOf(doc) is { Length: > 0 } path) lock (Gate) Saved[path] = DateTime.Now;
    }

    private static void OnOpened(object? sender, DocumentOpenedEventArgs e) => Mark(e.Document);
    private static void OnSaved(object? sender, DocumentSavedEventArgs e) => Mark(e.Document);
    private static void OnSavedAs(object? sender, DocumentSavedAsEventArgs e) => Mark(e.Document);
    private static void OnSynced(object? sender, DocumentSynchronizedWithCentralEventArgs e) => Mark(e.Document);
    private static void OnClosed(object? sender, DocumentClosedEventArgs e) { /* its time is dropped at the next look, when it isn't open any more */ }

    // ---- Revit's thread: what is open, and the save ---------------------------------------------------------------------

    private static void OnIdle(UIApplication app)
    {
        try
        {
            var tick = Environment.TickCount;
            if (unchecked(tick - _lastSettings) > 60_000) ReadSettings();
            if (_saving is { } path)
            {
                // The hint is on the screen by now: the save itself.
                if (unchecked(tick - _savingAt) < 1200) { RevitDispatcher.IdleAgain(); return; }
                _saving = null;
                foreach (Document doc in app.Application.Documents)
                    if (string.Equals(PathOf(doc), path, StringComparison.OrdinalIgnoreCase)) { Save(doc); break; }
                return;
            }
            if (unchecked(tick - _lastLook) < 10_000) return;
            _lastLook = tick;
            var open = new List<Open>();
            foreach (Document doc in app.Application.Documents)
            {
                if (!Watched(doc)) continue;
                var at = PathOf(doc);
                open.Add(new Open(at, doc.Title, ChangeFile.CentralOf(doc), ChangeFile.ChangedCount(doc), doc.IsModified));
                lock (Gate) if (!Saved.ContainsKey(at)) Saved[at] = DateTime.Now;
            }
            lock (Gate)
            {
                _models = open;
                foreach (var gone in Saved.Keys.Where(k => !open.Any(o => string.Equals(o.Path, k, StringComparison.OrdinalIgnoreCase))).ToList()) Saved.Remove(gone);
            }
            if (!_autoSave) return;
            foreach (Document doc in app.Application.Documents)
            {
                if (!Watched(doc) || !CanSave(doc)) continue;
                DateTime saved;
                lock (Gate) saved = Saved.TryGetValue(PathOf(doc), out var s) ? s : DateTime.Now;
                if (!W.SaveDue(doc.IsModified, DateTime.Now - saved, _minutes, SwitchGate.SinceInput(), SwitchGate.ContextualTab() != false, DateTime.Now - _failed)) continue;
                // First the hint, so the user sees why Revit is busy; the save at the next idle moment.
                Hint(W.Saving(doc.Title), force: true);
                _saving = PathOf(doc);
                _savingAt = tick;
                RevitDispatcher.IdleAgain();
                return;
            }
        }
        catch (Exception) { /* a watch is never worth an error */ }
    }

    private static bool Watched(Document doc)
    {
        try { return doc.IsValidObject && !doc.IsFamilyDocument && !doc.IsLinked && PathOf(doc).Length > 0 && !ChangeKeeper.Is(PathOf(doc)); }
        catch (Exception) { return false; }
    }

    /// <summary>A local copy or a plain project that Revit lets be saved now: not read-only, not in the middle of a change, not the central model itself.</summary>
    private static bool CanSave(Document doc)
    {
        try
        {
            if (doc.IsReadOnly || doc.IsModifiable || doc.IsDetached) return false;
            if (doc.IsWorkshared && string.Equals(ChangeFile.CentralOf(doc), PathOf(doc), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Saves the local copy (never a synchronize) and says so; a save Revit refuses is said too, and tried again later.</summary>
    internal static bool Save(Document doc)
    {
        var title = doc.Title;
        var clock = Stopwatch.StartNew();
        try
        {
            doc.Save();
            Mark(doc);
            Hint(W.Saved(title, clock.Elapsed, _minutes), force: true);
            return true;
        }
        catch (Exception ex)
        {
            _failed = DateTime.Now;
            Hint(W.SaveFailed(title, ex.Message.TrimEnd('.') + "."), force: true);
            LifelineBugs.Note("auto-save", "the local copy couldn't be saved by itself", PathOf(doc), ex);
            return false;
        }
    }

    // ---- Any thread: the signs ------------------------------------------------------------------------------------------

    private static void Sample()
    {
        if (!_warn || System.Threading.Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            List<Open> models;
            lock (Gate) models = _models;
            foreach (var warning in W.Judge(Read(models.FirstOrDefault()?.Path))) Hint(warning);
            foreach (var model in models)
            {
                if (W.IsServerPath(model.Central, model.Path))
                {
                    var reachable = Reachable(model.Central);
                    (int Fails, bool Lost) before;
                    lock (Gate) before = Servers.TryGetValue(model.Path, out var b) ? b : (0, false);
                    var (fails, lost, say) = W.Server(before.Fails, before.Lost, reachable);
                    lock (Gate) Servers[model.Path] = (fails, lost);
                    if (say == "lost") Hint(W.ServerLost(model.Title), force: true);
                    else if (say == "back") Hint(W.ServerBack(model.Title), force: true);
                    else if (lost) Hint(W.ServerLost(model.Title));
                }
                DateTime saved;
                lock (Gate) saved = Saved.TryGetValue(model.Path, out var s) ? s : DateTime.Now;
                // With saving by itself on, only when a save is long overdue (a tool was left open, or Revit refuses it).
                var since = DateTime.Now - saved;
                if (W.Unsaved(model.Title, since, model.Changes, _autoSave && since < TimeSpan.FromMinutes(_minutes * 2)) is { } unsaved) Hint(unsaved);
            }
        }
        catch (Exception) { }
        finally { System.Threading.Interlocked.Exchange(ref _sampling, 0); }
    }

    /// <summary>Whether the central model's folder answers within a few seconds (asked on another thread: a dead share can hang for a minute).</summary>
    internal static bool Reachable(string central)
    {
        try
        {
            var folder = Path.GetDirectoryName(central);
            if (string.IsNullOrEmpty(folder)) return true;
            var ask = Task.Run(() => Directory.Exists(folder));
            return ask.Wait(TimeSpan.FromSeconds(8)) && ask.Result;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Memory, the disk Revit saves to and Revit's window handles, as they are now.</summary>
    internal static W.Signs Read(string? modelPath)
    {
        long revit = -1, free = -1, total = -1, disk = -1;
        var drive = "";
        var handles = 0;
        try
        {
            using var own = Process.GetCurrentProcess();
            revit = own.PrivateMemorySize64 / (1024 * 1024);
            handles = Math.Max((int)GetGuiResources(own.Handle, 0), (int)GetGuiResources(own.Handle, 1));
        }
        catch (Exception) { }
        try
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                // What can still be given out: the smaller of free memory and what is left of memory plus page file.
                free = (long)(Math.Min(status.AvailPhys, status.AvailPageFile) / (1024 * 1024));
                total = (long)(status.TotalPhys / (1024 * 1024));
            }
        }
        catch (Exception) { }
        try
        {
            // The fuller of the two drives a save needs: the model's own, and the temp folder's.
            foreach (var path in new[] { modelPath, Path.GetTempPath() })
            {
                if (path is not { Length: > 2 } || path[1] != ':') continue;
                var info = new DriveInfo(path.Substring(0, 1));
                if (!info.IsReady || info.DriveType == DriveType.Network) continue;
                var mb = info.AvailableFreeSpace / (1024 * 1024);
                if (disk < 0 || mb < disk) { disk = mb; drive = info.Name.TrimEnd('\\'); }
            }
        }
        catch (Exception) { }
        return new W.Signs(revit, free, total, disk, drive, handles);
    }

    private static void Hint(W.Warning warning, bool force = false)
    {
        if (HintsForTest is { } collected) { lock (Gate) collected.Add(warning); return; }
        var tick = Environment.TickCount;
        lock (Gate)
        {
            if (Muted.Contains(warning.Key)) return;
            if (!force && !W.Due(tick, Shown.TryGetValue(warning.Key, out var shown) ? shown : null)) return;
            Shown[warning.Key] = tick;
        }
        try
        {
            App.Ui?.BeginInvoke(new Action(() =>
                UI.CommandHint.Show(warning.Main, warning.Detail, () => { lock (Gate) Muted.Add(warning.Key); }, seconds: 20, never: "Not again until Revit restarts")));
        }
        catch (Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
}
