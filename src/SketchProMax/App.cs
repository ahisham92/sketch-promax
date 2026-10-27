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
/// onto Revit's thread, the failure guard and the update loop. Conversations happen in the Claude desktop app, or in
/// the pane's Chat tab (Chat/ChatPane.cs), which runs Claude Code signed in with the user's Claude account: both use
/// the user's Claude plan, so there is no API key and no per-use cost.
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

    /// <summary>
    /// True when the TritoRevit add-in runs this core for shared tools (CoreEntry.StartShared): no connection to the
    /// Claude app, no chat, no Sketch ProMax ribbon, and updates go to TritoRevit's own folder (Sharing/TritoRuntime).
    /// </summary>
    internal static bool Shared { get; private set; }

    /// <summary>True while anything is running or queued, so switching to a new core must wait.</summary>
    internal static bool IsBusy => Jobs.Running is not null || Dispatcher.HasWork || SelfTest.IsRunning || Chat.ChatPane.IsBusy;

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
            ToolTip = "Firm standards and whether Revit asks before Sketch ProMax changes the model.",
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

    /// <summary>
    /// Starts everything this core hooks into Revit. Call in a valid API context (Idling or a command).
    /// Revit is often opening a model meanwhile (bug #18), so only what must be in place before the first tool call runs
    /// here: the dispatcher, the failure guard, the tool list and the pipe. File writes go to a background thread; pane
    /// content waits for an idle moment (<see cref="UI.LazyPane"/>); the update check, bug-log download, install
    /// settling and Claude registration wait <see cref="BackgroundDelay"/> and for no model to be opening. Each phase is
    /// timed (<see cref="StartupTimings"/>) and logged at the first idle moment.
    /// </summary>
    internal static void StartCore(UIApplication app, bool shared = false)
    {
        if (_started) return;
        Shared = shared;
        var total = System.Diagnostics.Stopwatch.StartNew();
#if NETFRAMEWORK
        DependencyResolver.Register();
#endif
        _life = new CancellationTokenSource();
        RevitYear = app.Application.VersionNumber;
        Ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        try { MainWindow = app.MainWindowHandle; } catch (Exception) { MainWindow = IntPtr.Zero; }
        // The Sketch ProMax tab's own parts; TritoRevit has its own tab and no favourites.
        if (!shared)
        {
            UI.FavouritesRibbon.Attach(app);
            UI.RibbonLabels.Attach(app);
            UI.LoaderBrand.Attach(app);
        }
        else
        {
            TimeLog.Off = true; // nothing is written into the models for TritoRevit's tools
            Bugs.BugLog.SharedRoot = () => Sharing.UserFolder.Bugs; // Documents\TritoRevit - <pack>\Bugs
            Bugs.BugReport.Watch(Ui, app); // a failure in a shared tool's window is saved as a problem report
        }
        // The loader asks for the ribbon (favourites included) right after this returns: read the recipe and learned-tool
        // folders on a background thread meanwhile, not on Revit's.
        Commands.QuickTools.Warm();

        try
        {
            StartupTimings.Time("dispatcher", () =>
            {
                Dispatcher = new RevitDispatcher();
                Dispatcher.Initialize(app);
                app.Application.FailuresProcessing += FailureGuard.Handle;
                app.Application.DocumentOpening += OnDocumentOpening;
                app.Application.DocumentOpened += OnDocumentOpened;
            });
            ToolRegistry? registry = null;
            StartupTimings.Time("tool registry", () => registry = ToolRegistry.CreateDefault());
            // A new server on the same pipe name: a connector that was talking to the previous core simply reconnects.
            // Writing tools.json (in the background) also makes the connector tell the Claude app that the tool list changed.
            StartupTimings.Time("bridge", () =>
            {
                Bridge = new BridgeServer(registry!, Dispatcher, RevitYear);
                // TritoRevit's tools run from its ribbon only: no connection point for the Claude app.
                if (!shared) Bridge.Start();
            });
        }
        catch (Exception)
        {
            // Half started: let go of what was hooked, so the loader can fall back to another version cleanly.
            _started = true;
            StopCore(app);
            throw;
        }
        _started = true;
        // The TritoRevit tab next to Sketch ProMax's (else SketchPro's, else first of the add-ins), with either add-in.
        try { UI.TritoTabOrder.Start(); } catch (Exception) { /* cosmetic: the tab stays where Revit put it */ }
        if (!shared)
        {
            // The chat's own connection, inside Revit (no helper program to start): a new port and token for each core.
            try { StartupTimings.Time("chat connection", () => ChatMcp.Start(Bridge!)); } catch (Exception) { /* the chat uses the connector program */ }
            try { StartupTimings.Time("chat hooks", () => Chat.ChatPane.Start(app)); } catch (Exception) { /* the chat tab says what is wrong when it is opened */ }
            try { StartupTimings.Time("restart note", () => UI.RestartNote.AfterStart(Ui)); } catch (Exception) { /* the chat and the Version button still say it */ }
            try { StartupTimings.Time("time log", () => TimeLog.Start(app)); } catch (Exception) { /* tasks still run; time is not counted */ }
            try { StartupTimings.Time("bim ready", () => BimReady.Start(app)); } catch (Exception) { /* models are only kept ready by the checklist's Fix buttons */ }
            try { StartupTimings.Time("change tracker", () => ChangeTracker.Start(app)); } catch (Exception) { /* Update status then reads every berth */ }
            try { StartupTimings.Time("chat model note", () => ChatModelWatch.Start(app)); } catch (Exception) { /* chat messages then carry no model note */ }
            // The Rescue window when Revit freezes, and a warning before Revit commands known to freeze it.
            try { StartupTimings.Time("freeze watch", () => FreezeWatch.Start(app)); } catch (Exception) { /* no Rescue window this session */ }
            try { StartupTimings.Time("heavy commands", () => HeavyCommands.Start(app)); } catch (Exception) { /* those commands run without the warning */ }
            // This PC's license (Licensing/LicenseState): without a valid key the License window opens at the first idle moment.
            try
            {
                StartupTimings.Time("license", () =>
                {
                    if (!Licensing.LicenseState.Allowed) Dispatcher.Later(() => UI.LicenseWindow.Open());
                });
            }
            catch (Exception) { /* the License button still opens it */ }
        }
        // Lifeline's change file, for everyone: with Sketch ProMax and with TritoRevit alone (shared packs), licensed or not.
        try { StartupTimings.Time("change file", () => ChangeFile.Start(app)); } catch (Exception ex) { Revit.LifelineBugs.Note("start", "Lifeline didn't start: no change file is kept", null, ex); }
        CentralAccess.Watch(app);
        // Our exceptions that reach Revit's UI thread are caught and logged; one on a background thread is logged before Revit closes.
        if (!shared) CrashGuard.Start(Ui);

        var token = _life.Token;
        _ = Task.Run(() => shared ? SharedBackgroundAsync(token) : BackgroundStartAsync(token));
        StartupTimings.StartDone(total.ElapsedMilliseconds);
        // The loader's ribbon queries follow in this same Idling; the line goes to the log at the next one.
        Dispatcher.Later(() => ActivityLog.Note("startup_timing",
            $"Sketch ProMax {Updater.CurrentText} in Revit {RevitYear}: {StartupTimings.StartLine()}; first Idling {StartupTimings.SinceStart} ms after start"));
        if (!shared)
        {
            // A Revit that closed by itself mid-call left that call behind: the log says which, with its arguments.
            RunningCall.Version = Updater.CurrentText;
            RunningCall.Enabled = true;
            _ = Task.Run(() => { foreach (var line in RunningCall.Recover()) ActivityLog.Note("revit_closed_during", line); });
        }
    }

    /// <summary>How long after start the network and install work waits, so it doesn't compete with Revit opening a model.</summary>
    internal static readonly TimeSpan BackgroundDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Start work that isn't needed in the first seconds, on a background thread: Claude registration, settling the
    /// install, then the update loop (or the bug-log download alone when automatic updates are off).
    /// </summary>
    private static async Task BackgroundStartAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(BackgroundDelay, stop).ConfigureAwait(false);
            // Still opening a model (a big one can take minutes): wait for it, up to ten minutes more.
            for (var i = 0; i < 300 && StartupTimings.IsOpening; i++) await Task.Delay(2000, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        // Revit closed during a field test last time: its report of what ran, then the user is told once.
        try { Tools.FieldTest.AfterStart(); } catch (Exception) { }
        try { ClaudeRegistration.Ensure(); } catch (Exception) { }
        // The owner's changes to keys given out (a shorter date, a cancelled key), kept for offline days.
        try { await Licensing.LicenseState.RefreshChangesAsync(stop).ConfigureAwait(false); } catch (Exception) { }
        // Moves a manifest from an older release onto the one fixed loader path, once; writes nothing when settled.
        try { Updater.SettleInstall(); } catch (Exception) { }
        if (stop.IsCancellationRequested) return;
        // Shared tools: packs this PC shared are republished when they change, packs it received follow their updates.
        _ = Task.Run(() => SharedLoopAsync(stop));
        // Check at every start, then hourly while Revit stays open; the bug log is fetched with each check. With
        // automatic updates off a newer version is only announced (UI/UpdateNotice), never downloaded.
        await UpdateLoopAsync(stop).ConfigureAwait(false);
    }

    private static void OnDocumentOpening(object? sender, Autodesk.Revit.DB.Events.DocumentOpeningEventArgs e) => StartupTimings.Opening();

    /// <summary>Only timing: nothing of ours runs on a model open (the chat follows the model at the next idle moment).</summary>
    private static void OnDocumentOpened(object? sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string title;
        try { title = e.Document?.Title ?? ""; } catch (Exception) { title = ""; }
        StartupTimings.Opened(title, sw.ElapsedMilliseconds);
    }

    /// <summary>How often shared packs are compared with their update folders (and this PC's own packs with what it published).</summary>
    internal static readonly TimeSpan SharedEvery = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Sketch ProMax's side of shared tools, every <see cref="SharedEvery"/>: republishes the packs this PC shared when a
    /// tool in them changed, and, when the TritoRevit tab runs through this core, updates the packs installed here and
    /// TritoRevit.dll. Only file work; nothing runs when nothing was shared or installed.
    /// </summary>
    private static async Task SharedLoopAsync(CancellationToken stop)
    {
        var settled = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                Sharing.SharedTools.AutoPublish();
                if (Sharing.SharedPacks.Any()) Sharing.SharedPacks.Sync(stop);
                if (!settled && Sharing.TritoRuntime.Installed()) { Sharing.TritoRuntime.SettleFromRunning(); settled = true; }
            }
            catch (Exception) { /* tried again at the next round */ }
            try { await Task.Delay(SharedEvery, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// TritoRevit's background work: the installed packs applied and followed every <see cref="SharedEvery"/>, and this
    /// core's own updates (at start, then hourly), switched to while Revit runs. Silent: a failure is retried later.
    /// </summary>
    private static async Task SharedBackgroundAsync(CancellationToken stop)
    {
        // Applied straight away, so a pack the installer just put here has its learned tools before a button is clicked.
        try { Sharing.SharedPacks.Sync(stop); } catch (Exception) { }
        Sharing.UserFolder.Ensure(); // Documents\TritoRevit - <pack>, with Bugs and the how-to
        try
        {
            await Task.Delay(BackgroundDelay, stop).ConfigureAwait(false);
            for (var i = 0; i < 300 && StartupTimings.IsOpening; i++) await Task.Delay(2000, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        var lastCheck = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try { Sharing.SharedPacks.Sync(stop); } catch (Exception) { }
            if (DateTime.UtcNow - lastCheck > CheckEvery)
            {
                try
                {
                    await Sharing.TritoRuntime.CheckAsync(stop).ConfigureAwait(false);
                    lastCheck = DateTime.UtcNow;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception) { lastCheck = DateTime.UtcNow - CheckEvery + TimeSpan.FromMinutes(10); /* again in ten minutes */ }
            }
            try { await Task.Delay(SharedEvery, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
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
        try { Chat.ChatPane.Stop(app); } catch (Exception) { }
        try { Chat.NotesView.Stop(); } catch (Exception) { }
        try { TimeLog.Stop(app); } catch (Exception) { }
        try { BimReady.Stop(); } catch (Exception) { }
        try { ChangeTracker.Stop(); } catch (Exception) { }
        try { ChatModelWatch.Stop(); } catch (Exception) { }
        try { FreezeWatch.Stop(); } catch (Exception) { }
        try { HeavyCommands.Stop(); } catch (Exception) { }
        try { ChangeFile.Stop(); } catch (Exception) { }
        CentralAccess.Unwatch(app);
        ChatMcp.Stop();
        // A shared-mode core never opened the connection point, and the session file named after this Revit is Sketch ProMax's.
        if (!Shared) Bridge?.Stop(keepSession: true);
        Bridge = null;
        try { app.Application.FailuresProcessing -= FailureGuard.Handle; } catch (Exception) { }
        try { app.Application.DocumentOpening -= OnDocumentOpening; } catch (Exception) { }
        try { app.Application.DocumentOpened -= OnDocumentOpened; } catch (Exception) { }
        try { UI.UpdateNotice.Stop(); } catch (Exception) { }
        Bugs.BugReport.Unwatch();
        CrashGuard.Stop();
        UI.LoaderBrand.Stop();
        UI.LazyPane.Forget();
        Dispatcher.Shutdown(app);
#if NETFRAMEWORK
        DependencyResolver.Unregister();
#endif
    }

    /// <summary>Revit is closing.</summary>
    internal static void ShutdownCore()
    {
        _life?.Cancel();
        // Revit closing can take a while (saving, relinquishing): that is not a freeze.
        try { FreezeWatch.Stop(); } catch (Exception) { }
        if (Shared) return;
        RunningCall.Clear();
        try { Chat.ChatPane.Shutdown(); } catch (Exception) { }
        try { TimeLog.Shutdown(); } catch (Exception) { }
        ChatMcp.Stop();
        Bridge?.Stop();
#if NETFRAMEWORK
        DependencyResolver.Unregister();
#endif
    }

    /// <summary>How often updates are looked for while Revit stays open, by any Revit window (they share the last check).</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// How often the loop looks at the clock. Not one long hourly wait: a timer stands still while Windows sleeps, so a
    /// Revit left open overnight would go on for most of an hour after waking without checking.
    /// </summary>
    private static readonly TimeSpan LoopTick = TimeSpan.FromMinutes(5);

    /// <summary>The newer version this core last announced as available.</summary>
    private static string? _offered;

    private static async Task UpdateLoopAsync(CancellationToken stop)
    {
        var first = true;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                bool auto;
                try { auto = AgentSettings.Load().AutoUpdate; } catch (Exception) { auto = true; }
                if (first || Updater.IsDue(CheckEvery))
                {
                    await RunUpdateAsync(force: first, interactive: false, stop, install: auto).ConfigureAwait(false);
                    first = false;
                }
                else if (!auto && Updater.Latest is { } latest && latest != _offered && Updater.Staged is null)
                {
                    // Another Revit window made the check that found it: say so here too (with automatic updates on, this
                    // window follows that window's download instead, and announces "Updated to").
                    _offered = latest;
                    UI.UpdateNotice.Offer(new UI.UpdateNotice.Notice(UI.UpdateNotice.Kind.Available, latest, UI.UpdateNotice.TopLines(Updater.NotesFor(latest))));
                }
            }
            catch (Exception) { first = false; /* a failed check is retried at the next round; the badge shows the last error */ }
            try { await Task.Delay(LoopTick, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
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
    /// One update run. The ribbon's Update button follows every run through its <see cref="UpdateProgress"/>. A run the
    /// user asked for opens the update window from the start. An automatic run never opens a window: a newer version
    /// is announced by a small toast that doesn't take the focus (<see cref="UI.UpdateNotice"/>), "available" when
    /// automatic updates are off, "installed" when Revit must restart; a live switch is announced by the new version.
    /// </summary>
    private static async Task RunUpdateAsync(bool force, bool interactive, CancellationToken stop, bool install = true)
    {
        var progress = new UpdateProgress { Interactive = interactive };
        UpdateProgress.Current = progress;
        if (interactive) Surface(progress);
        else
            progress.Changed += s =>
            {
                // The Version button of older loaders shows a run once it is surfaced.
                if (s.Stage is UpdateStage.Downloading or UpdateStage.Applying or UpdateStage.RestartNeeded or UpdateStage.Available) progress.Surfaced = true;
                if (s.Stage == UpdateStage.Available && s.Version is { } available)
                {
                    _offered = available;
                    UI.UpdateNotice.Offer(new UI.UpdateNotice.Notice(UI.UpdateNotice.Kind.Available, available,
                        UI.UpdateNotice.TopLines(Updater.NotesFor(available))));
                }
                else if (s.Stage == UpdateStage.RestartNeeded && s.Version is { } installed)
                    UI.UpdateNotice.Offer(new UI.UpdateNotice.Notice(UI.UpdateNotice.Kind.Installed, installed,
                        UI.UpdateNotice.TopLines(Updater.NotesFor(installed)), s.Detail));
            };
        try { await Updater.CheckAsync(force, progress, stop, install).ConfigureAwait(false); }
        finally { await SyncBugLogAsync(stop).ConfigureAwait(false); }
    }

    /// <summary>Downloads the published bug log into Documents\Sketch ProMax\Bugs. Silent; never throws.</summary>
    private static async Task SyncBugLogAsync(CancellationToken stop)
    {
        try { await Bugs.BugLog.SyncAsync($"SketchProMax/{Updater.CurrentText}", stop).ConfigureAwait(false); }
        catch (Exception) { }
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
