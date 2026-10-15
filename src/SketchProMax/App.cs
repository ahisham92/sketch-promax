using System.IO;
using System.Runtime.CompilerServices;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SketchProMax.Bridge;
using SketchProMax.Commands;
using SketchProMax.Revit;
using SketchProMax.Settings;
using SketchProMax.Tools;
using SketchProMax.UI;
using SketchProMax.Update;

namespace SketchProMax;

/// <summary>
/// The core of Sketch ProMax: the bridge the Claude desktop app talks to (through SketchProMax.Mcp.exe), the dispatcher
/// onto Revit's thread, the failure guard and the update loop. There is no chat inside Revit: conversations happen in
/// the Claude desktop app on the user's Claude plan, so there is no API key and no per-use cost.
///
/// Normally the loader (SketchProMax.Loader, which the add-in manifest names) starts and stops this core through
/// <see cref="CoreEntry"/>, and can switch to a newer core while Revit runs. This class is also still an
/// IExternalApplication, because manifests written by 0.25.x name SketchProMax.App directly: then it hands over to the
/// loader shipped next to it, and points the manifests at the loader for the next start.
/// </summary>
public sealed class App : IExternalApplication
{
    public static RevitDispatcher Dispatcher { get; private set; } = new();

    public static BridgeServer? Bridge { get; private set; }

    /// <summary>The Revit release this core runs in ("2025"), once started.</summary>
    internal static string RevitYear { get; private set; } = "";

    /// <summary>Revit's main window, to own Sketch ProMax windows.</summary>
    internal static IntPtr MainWindow { get; private set; }

    /// <summary>Revit's UI thread, for windows opened from background work (the update window).</summary>
    internal static System.Windows.Threading.Dispatcher? Ui { get; private set; }

    private static CancellationTokenSource? _life;
    private static bool _started;
    private static bool _viaLoader;

    /// <summary>True while anything is running or queued, so switching to a new core must wait.</summary>
    internal static bool IsBusy => Jobs.Running is not null || Dispatcher.HasWork || SelfTest.IsRunning;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            var result = BootThroughLoader(application);
            _viaLoader = true;
            return result;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException or BadImageFormatException)
        {
            // The loader is missing next to this DLL: run as before, with the old three buttons.
            return StartWithoutLoader(application);
        }
    }

    // Separate and not inlined, so a missing loader DLL fails here (catchable) rather than when OnStartup is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Result BootThroughLoader(UIControlledApplication application) =>
        SketchProMax.Loader.LoaderApp.BootFromCore(application, typeof(App).Assembly);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ShutdownLoader() => SketchProMax.Loader.LoaderApp.ShutdownFromCore();

    private static Result StartWithoutLoader(UIControlledApplication application)
    {
        // The API objects needed to start are only handed out on Idling; start on the first one.
        void Once(object? sender, IdlingEventArgs e)
        {
            if (sender is not UIApplication app) return;
            application.Idling -= Once;
            StartCore(app);
        }
        application.Idling += Once;

        const string tab = "Sketch ProMax";
        application.CreateRibbonTab(tab);
        var panel = application.CreateRibbonPanel(tab, "AI Agent");
        var assembly = typeof(App).Assembly.Location;
        panel.AddItem(new PushButtonData("SketchProMaxStatus", "Status", assembly, typeof(StatusCommand).FullName)
        {
            ToolTip = "Check the connection to the Claude desktop app and how to start.",
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
        });
        panel.AddItem(new PushButtonData("SketchProMaxSettings", "Settings", assembly, typeof(SettingsCommand).FullName)
        {
            ToolTip = "Firm standards and whether Revit asks before Claude changes the model.",
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
        });
        panel.AddItem(new PushButtonData("SketchProMaxSelfTest", "Self-test", assembly, typeof(SelfTestCommand).FullName)
        {
            ToolTip = "Run every Sketch ProMax tool once on the open model and report what works. Changes are rolled back, so the model is left as it was.",
        });
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        if (_viaLoader) ShutdownLoader();
        else ShutdownCore();
        return Result.Succeeded;
    }

    /// <summary>Starts everything this core hooks into Revit. Call in a valid API context (Idling or a command).</summary>
    internal static void StartCore(UIApplication app)
    {
        if (_started) return;
#if NETFRAMEWORK
        DependencyResolver.Register();
#endif
        _life = new CancellationTokenSource();
        RevitYear = app.Application.VersionNumber;
        Ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        try { MainWindow = app.MainWindowHandle; } catch (Exception) { MainWindow = IntPtr.Zero; }

        try
        {
            Dispatcher = new RevitDispatcher();
            Dispatcher.Initialize(app);
            app.Application.FailuresProcessing += FailureGuard.Handle;
            // A new server on the same pipe name: a connector that was talking to the previous core simply reconnects.
            // Writing tools.json also makes the connector tell the Claude app that the tool list changed.
            Bridge = new BridgeServer(ToolRegistry.CreateDefault(), Dispatcher, RevitYear);
            Bridge.Start();
        }
        catch (Exception)
        {
            // Half started: let go of what was hooked, so the loader can fall back to another version cleanly.
            _started = true;
            StopCore(app);
            throw;
        }
        _started = true;

        _ = Task.Run(() =>
        {
            try { ClaudeRegistration.Ensure(); } catch (Exception) { }
            try { if (Updater.CanSwitchLive) Updater.MoveManifestsToLoader(); } catch (Exception) { }
        });
        // Check at every start, then hourly while Revit stays open.
        var token = _life.Token;
        if (AgentSettings.Load().AutoUpdate) _ = Task.Run(() => UpdateLoopAsync(token));
    }

    /// <summary>
    /// Lets go of Revit so a newer core can take over: stops the bridge (keeping the session file the new core takes
    /// over), the update loop, the dispatcher and the failure handler. Deletes nothing. Call in a valid API context.
    /// </summary>
    internal static void StopCore(UIApplication app)
    {
        if (!_started) return;
        _started = false;
        _life?.Cancel();
        Bridge?.Stop(keepSession: true);
        Bridge = null;
        try { app.Application.FailuresProcessing -= FailureGuard.Handle; } catch (Exception) { }
        Dispatcher.Shutdown(app);
#if NETFRAMEWORK
        DependencyResolver.Unregister();
#endif
    }

    /// <summary>Revit is closing.</summary>
    internal static void ShutdownCore()
    {
        _life?.Cancel();
        Bridge?.Stop();
#if NETFRAMEWORK
        DependencyResolver.Unregister();
#endif
    }

    private static async Task UpdateLoopAsync(CancellationToken stop)
    {
        var force = true;
        while (!stop.IsCancellationRequested)
        {
            try { await RunUpdateAsync(force, interactive: false, stop).ConfigureAwait(false); }
            catch (Exception) { /* a failed check is retried next round; Status shows the last error */ }
            force = false;
            try { await Task.Delay(TimeSpan.FromHours(1), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!AgentSettings.Load().AutoUpdate) return;
        }
    }

    /// <summary>The ribbon's Check now: runs in the background with the update window open from the start.</summary>
    internal static void CheckForUpdatesInteractive()
    {
        if (UpdateProgress.Current is { } running && !running.Snapshot.IsFinal && running.Snapshot.Stage != UpdateStage.Idle)
        {
            running.Surfaced = true;
            UpdateWindow.ShowFor(running);
            return;
        }
        var token = _life?.Token ?? CancellationToken.None;
        _ = Task.Run(() => RunUpdateAsync(force: true, interactive: true, token));
    }

    /// <summary>
    /// One update run. Automatic runs stay silent unless a new version is found; then the update window opens. The
    /// window and the ribbon follow the run through its <see cref="UpdateProgress"/>, never blocking Revit.
    /// </summary>
    private static async Task RunUpdateAsync(bool force, bool interactive, CancellationToken stop)
    {
        var progress = new UpdateProgress { Interactive = interactive };
        if (interactive) Surface(progress);
        else
            progress.Changed += s =>
            {
                if (!progress.Surfaced && s.Stage is UpdateStage.Downloading or UpdateStage.Applying or UpdateStage.RestartNeeded) Surface(progress);
            };
        await Updater.CheckAsync(force, progress, stop).ConfigureAwait(false);
    }

    private static void Surface(UpdateProgress progress)
    {
        progress.Surfaced = true;
        UpdateProgress.Current = progress;
        var ui = Ui;
        if (ui is null) return;
        ui.BeginInvoke(new Action(() =>
        {
            try { UpdateWindow.ShowFor(progress); }
            catch (Exception) { /* the ribbon still shows the state */ }
        }));
    }
}
