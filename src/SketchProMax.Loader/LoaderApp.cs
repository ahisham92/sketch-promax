using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SketchProMax.Loader;

/// <summary>
/// What SketchProMax.addin loads. Builds the ribbon at startup, starts the newest installed core on Revit's first idle
/// moment, and switches to a newer core when one has been downloaded, without restarting Revit.
/// </summary>
public sealed class LoaderApp : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application) => Host.Startup(application, null);

    public Result OnShutdown(UIControlledApplication application)
    {
        Host.Shutdown();
        return Result.Succeeded;
    }

    /// <summary>
    /// For a core that Revit loaded directly (manifests written by 0.25.x point at SketchProMax.dll with SketchProMax.App):
    /// the core's App hands over to the loader, with itself as the current core.
    /// </summary>
    public static Result BootFromCore(UIControlledApplication application, Assembly core) => Host.Startup(application, core);

    public static void ShutdownFromCore() => Host.Shutdown();
}

/// <summary>The loader's state for this Revit process.</summary>
internal static class Host
{
    // AppDomain-wide keys, so a core (or a second copy of this loader) can find the host without sharing types with it.
    public const string ContractKey = "SketchProMax.Loader.Contract";
    public const string DispatchKey = "SketchProMax.Loader.Dispatch";
    public const string PendingKey = "SketchProMax.Loader.Pending";
    public const string PathKey = "SketchProMax.Loader.Path";

    private static UIControlledApplication? _application;
    private static Assembly? _preloaded;
    private static CoreHandle? _core;
    private static string _year = "";
    private static string? _bootError;
    private static bool _booting;
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
    private static System.Windows.Threading.DispatcherTimer? _timer;
    private static int _lastScan = Environment.TickCount - 120000;
    private static int _lastRefresh;
    private static IntPtr _mainWindow;
    private static UIApplication? _uiApp;

    public static CoreHandle? Core => _core;
    public static string Year => _year;
    public static string? BootError => _bootError;

    public static Result Startup(UIControlledApplication application, Assembly? preloaded)
    {
        if (_application is not null) return Result.Succeeded; // a second manifest for the same add-in
        _application = application;
        _preloaded = preloaded;
        _year = application.ControlledApplication.VersionNumber;
        try { _mainWindow = application.MainWindowHandle; } catch (Exception) { _mainWindow = IntPtr.Zero; }

        var domain = AppDomain.CurrentDomain;
        domain.SetData(ContractKey, CoreHandle.Contract);
        domain.SetData(PathKey, typeof(Host).Assembly.Location);
        domain.SetData(DispatchKey, (Func<string, UIApplication, int>)Dispatch);
#if NETFRAMEWORK
        Resolver.Register();
        if (preloaded is not null) Resolver.AddCore(preloaded);
#endif
        try { Ribbon.Build(application); }
        catch (Exception ex) { _bootError = "The ribbon could not be built: " + ex.Message; }
        // Dockable panes can only be registered now, during startup; the pane gets its content from whichever core runs.
        try { PaneHost.Register(application); }
        catch (Exception) { /* no pane this session: the Pane button says so */ }

        application.Idling += OnIdling;
        // Revit raises Idling once per idle spell, so the ribbon's live text runs on a UI timer instead.
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        return Result.Succeeded;
    }

    public static void Shutdown()
    {
        _timer?.Stop();
        try { _core?.Shutdown(); } catch (Exception) { /* Revit is closing */ }
    }

    private static void Tick()
    {
        try { Ribbon.Refresh(); } catch (Exception) { /* ribbon refresh is cosmetic */ }
        // Work that needs Revit's API (starting a core, switching to a new one) waits for Idling; give Revit a nudge.
        if (_core is null ? !_booted : Pending() is not null) Nudge();
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (sender is not UIApplication app) return;
        _uiApp = app;
        PaneHost.EnsureEvent();
        if (_core is null)
        {
            if (!_booted) Boot(app);
            return;
        }
        var target = Pending();
        if (target is not null)
        {
            // A job, a self-test or queued work is running in the old core: the timer brings Revit back here shortly.
            if (_core.Query("busy") == "1") return;
            Swap(app, target);
        }
        if (unchecked(Environment.TickCount - _lastRefresh) > 2000)
        {
            _lastRefresh = Environment.TickCount;
            try { Ribbon.Refresh(); } catch (Exception) { }
        }
    }

    private static bool _booted;

    /// <summary>Starts the newest core that starts. Called from Idling (or a button) in a valid API context.</summary>
    private static void Boot(UIApplication app)
    {
        if (_booting || _core is not null) return;
        _booting = true;
        _booted = true;
        var errors = new List<string>();
        try
        {
            foreach (var candidate in Candidates())
            {
                try
                {
                    // The core Revit already loaded (old manifest) is used as is; never load a second copy of the same version.
                    var handle = _preloaded is not null && (candidate.Path.Equals(_preloaded.Location, StringComparison.OrdinalIgnoreCase)
                                                            || candidate.Version == _preloaded.GetName().Version)
                        ? CoreHandle.FromLoaded(_preloaded)
                        : CoreHandle.Load(candidate.Path);
                    handle.Start(app);
                    _core = handle;
                    _bootError = null;
                    break;
                }
                catch (Exception ex)
                {
                    Failed.Add(candidate.Path);
                    errors.Add($"{candidate.Path}: {Inner(ex).Message}");
                }
            }
            if (_core is null)
                _bootError = errors.Count == 0
                    ? "No SketchProMax.dll was found next to the loader or in %LOCALAPPDATA%\\SketchProMax\\versions. Run Install.cmd again."
                    : "Sketch ProMax could not start:\n" + string.Join("\n", errors);
        }
        finally { _booting = false; }
        try { Ribbon.Refresh(); Ribbon.RefreshQuickTools(); } catch (Exception) { }
    }

    /// <summary>Switches from the running core to the one at <paramref name="path"/>. Called from Idling.</summary>
    private static void Swap(UIApplication app, Candidate target)
    {
        AppDomain.CurrentDomain.SetData(PendingKey, null);
        var old = _core!;
        CoreHandle next;
        try { next = CoreHandle.Load(target.Path); }
        catch (Exception ex)
        {
            Failed.Add(target.Path);
            old.Query("swapfailed|" + Inner(ex).Message);
            return;
        }

        // Stop lets go of everything it can even when one step throws; whatever is left of the old core is inert
        // (an empty dispatcher queue, a failure handler that only acts while its own tool runs), so carry on.
        try { old.Stop(app); }
        catch (Exception) { }

        try
        {
            next.Start(app);
        }
        catch (Exception ex)
        {
            Failed.Add(target.Path);
            try { next.Stop(app); } catch (Exception) { /* it may have half started; the old one takes the pipe back */ }
            try { old.Start(app); } catch (Exception) { _core = null; }
            old.Query("swapfailed|" + Inner(ex).Message);
            return;
        }

        _core = next;
        // The old core's update window, if open, shows the result; otherwise the new core announces itself.
        if (old.Query("swapped|" + next.VersionText) != "shown") next.Query("announce|" + old.VersionText);
        try { Ribbon.Refresh(); Ribbon.RefreshQuickTools(reload: true); } catch (Exception) { }
    }

    /// <summary>A newer core to switch to: one the running core announced, or (every minute) one found on disk.</summary>
    private static Candidate? Pending()
    {
        if (_core is null) return null;
        if (AppDomain.CurrentDomain.GetData(PendingKey) is string path)
        {
            if (File.Exists(path) && !Failed.Contains(path) && CoreHandle.PeekVersion(path) is { } v && v > _core.Version)
                return new Candidate(path, v);
            AppDomain.CurrentDomain.SetData(PendingKey, null);
            // Asked again for a version that already failed to start in this Revit session: say so rather than wait.
            if (Failed.Contains(path)) _core.Query("swapfailed|it did not start earlier in this Revit session");
        }
        if (unchecked(Environment.TickCount - _lastScan) < 60000) return _pendingFromScan;
        _lastScan = Environment.TickCount;
        _pendingFromScan = null;
        // Another Revit window may have downloaded the update; follow it only when automatic updates are on.
        if (_core.Query("autoupdate") != "1") return null;
        var newest = Candidates().FirstOrDefault();
        if (newest is not null && newest.Version > _core.Version) _pendingFromScan = newest;
        return _pendingFromScan;
    }

    private static Candidate? _pendingFromScan;

    internal sealed record Candidate(string Path, Version Version);

    /// <summary>Every core for this Revit version that can be started, newest first.</summary>
    private static List<Candidate> Candidates()
    {
        var list = new List<Candidate>();
        void Add(string path)
        {
            if (!File.Exists(path) || Failed.Contains(path)) return;
            if (CoreHandle.PeekVersion(path) is { } v && !list.Any(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                list.Add(new Candidate(path, v));
        }
        if (_preloaded is not null) Add(_preloaded.Location);
        var own = Path.GetDirectoryName(typeof(Host).Assembly.Location);
        if (own is not null) Add(Path.Combine(own, "SketchProMax.dll"));
        var versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "versions");
        try
        {
            if (Directory.Exists(versions))
                foreach (var folder in Directory.GetDirectories(versions))
                {
                    // Only finished folders: the updater unpacks to "<version>.partial" and renames when complete.
                    if (!Version.TryParse(Path.GetFileName(folder), out _)) continue;
                    Add(Path.Combine(folder, "Revit" + _year, "SketchProMax.dll"));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.OrderByDescending(c => c.Version).ToList();
    }

    /// <summary>Runs a ribbon command. Reached through an AppDomain delegate, so any copy of the command classes finds it.</summary>
    public static int Dispatch(string name, UIApplication app)
    {
        _uiApp = app;
        if (name == "quick-refresh")
        {
            Ribbon.RefreshQuickTools(reload: true);
            return 0;
        }
        // The dockable pane belongs to the loader, so its button works with any core, or none yet.
        if (name == "pane") return PaneHost.Toggle(app);
        if (_core is null) Boot(app);
        if (_core is null)
        {
            TaskDialog.Show("Sketch ProMax", _bootError ?? "Sketch ProMax is still starting. Try again in a moment.");
            return -1;
        }
        if (name.StartsWith("quick:", StringComparison.Ordinal))
        {
            var key = Ribbon.QuickKey(int.Parse(name.Substring(6)));
            if (key is null)
            {
                Ribbon.RefreshQuickTools();
                TaskDialog.Show("Sketch ProMax quick tools",
                    "Nothing saved yet. In the Claude app, after a workflow you liked, say \"save that as a recipe\"; when Claude wrote " +
                    "code that worked, say \"save it as a tool\". It then appears under Quick tools and runs here without the AI.");
                return 1;
            }
            name = key;
        }
        var result = _core.Command(name, app);
        try { Ribbon.Refresh(); if (name.StartsWith("quick", StringComparison.Ordinal)) Ribbon.RefreshQuickTools(reload: true); } catch (Exception) { }
        return result;
    }

    /// <summary>Asks the running core; empty while none is running.</summary>
    public static string Query(string key) => _core?.Query(key) ?? "";

    /// <summary>
    /// The pane's Cancel button. Asking a job to stop needs no Revit API, so it goes to the core straight away rather than
    /// waiting for Revit to be idle. It only sets the job's stop flag: the pane can only be clicked while Revit's UI is
    /// responsive, which is between the steps of a multi-step job (the field test, the self-test), and those stop before
    /// their next step. A single long tool call keeps Revit's thread busy until it ends, so the click is not seen until
    /// then; such a call is stopped with cancel_job from Claude, and stops only at its own checks.
    /// </summary>
    public static void PaneCancel()
    {
        if (_core is not { } core) return;
        try { core.Command("pane-cancel", _uiApp!); }
        catch (Exception) { /* the job finished or the core is switching: the next refresh shows where things are */ }
    }

    private static Exception Inner(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private static void Nudge()
    {
        if (_mainWindow == IntPtr.Zero) return;
        try { PostMessage(_mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception) { }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
