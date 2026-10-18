using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Commands;
using SketchProMax.Settings;
using SketchProMax.UI;
using SketchProMax.Update;

namespace SketchProMax;

/// <summary>
/// How the loader (SketchProMax.Loader) drives this core. Called by reflection, so the loader works with any core
/// version: only these signatures (static, primitive and Revit API types) are the contract between them.
///   Start / Stop       start or let go of everything this core hooks into Revit; Stop leaves it restartable.
///   Shutdown           Revit is closing.
///   Command(name, app) a ribbon button; returns a Revit Result as int.
///   Query(key)         a short text answer for the ribbon or the dockable pane, or a notice about switching versions.
///   PaneContent(name)  optional (0.29+; loaders that don't know it never call it): a WPF element for a tab of the
///                      loader's dockable pane ("chat", "notes" from 0.30, or a tab the manifest declares), or null. Asked again after each
///                      version switch.
///   Query("ribbon")    (0.30+ loaders) the buttons, pane buttons and pane tabs this core declares (UI/CoreRibbonManifest),
///                      added by the loader while Revit runs.
/// All are called on Revit's main thread; Start, Stop and Command in a valid Revit API context.
/// </summary>
public static class CoreEntry
{
    /// <summary>The oldest loader contract this core works with (see CoreHandle.Contract in the loader).</summary>
    public static readonly int MinimumLoaderContract = 1;

    public static void Start(UIApplication app) => App.StartCore(app);

    public static void Stop(UIApplication app) => App.StopCore(app);

    public static void Shutdown() => App.ShutdownCore();

    /// <summary>
    /// Content the core puts into the loader's dockable pane: "chat" is the Claude chat, "notes" its memory notes. Returns a WPF FrameworkElement
    /// (typed object so the signature stays primitive), or null for a name this core doesn't provide. Called on Revit's
    /// UI thread, after Start.
    /// The real element is made at Revit's next idle moment (<see cref="LazyPane"/>): the loader asks while Revit starts,
    /// often while a model is opening, and the chat reads and lays out its saved conversation.
    /// </summary>
    public static object? PaneContent(string name) => name switch
    {
        "chat" => LazyPane.For(name, Chat.ChatPane.Get),
        "notes" => LazyPane.For(name, Chat.NotesView.Get),
        _ => null,
    };

    public static int Command(string name, UIApplication app)
    {
        var result = name switch
        {
            "status" => CoreCommands.Status(app),
            "copy-prompt" => CoreCommands.CopyPrompt(app),
            "open-claude" => CoreCommands.OpenClaude(),
            "job" => CoreCommands.Job(),
            "cancel-job" => CoreCommands.CancelJob(),
            "quick-folder" => CoreCommands.QuickFolder(),
            "model-check" => QuickTools.ModelCheck(app),
            "export-pack" => QuickTools.ExportPack(app),
            "self-test" => CoreCommands.SelfTest(app),
            "field-test" => Tools.FieldTest.RibbonCommand(app),
            "update-info" => CoreCommands.UpdateInfo(),
            "update-check" => CoreCommands.CheckForUpdates(),
            "whats-new" => CoreCommands.OpenUrl(CoreCommands.ReleasesUrl),
            "settings" => CoreCommands.Settings(),
            "help" => CoreCommands.OpenUrl(CoreCommands.HelpUrl),
            // The 0.30 ribbon: tools run through their options window, the live Job and Update buttons, Send view.
            _ when name.StartsWith("tool:", StringComparison.Ordinal) => RibbonTools.Run(app, name.Substring("tool:".Length)),
            "job-button" => CoreCommands.JobButton(),
            "update-button" => CoreCommands.UpdateButton(),
            "send-view" => SendView(),
            // Bug reports (0.30+): one small zip per report, in Documents\Sketch ProMax\Bugs.
            "report-bug" => Bugs.BugReport.Command(app),
            "bug-folder" => Bugs.BugReport.OpenFolder(),
            "export-chats-bugs" => QuickTools.ExportChatsAndBugs(app),
            // Time saved and working time for the active model (0.32+).
            "time-saved" => TimeSavedWindow.Command(app),
            // The zero-mistake issue check (0.33+): the pass/fail window for the issue set.
            "issue-check" => IssueGateWindow.Command(app),
            // Purge and cleanup (0.34+): pick one of the four cleanup tools; each previews, then asks before deleting.
            "cleanup" => RibbonTools.Cleanup(app),
            // The guided test checklist (0.32+): the manual items of the field test's coverage, saved to the Bugs folder.
            "test-checklist" => TestChecklistWindow.Open(app),
            // Favourites on the My tools panel (0.30.1+): the manager, and the slot buttons ("fav:1"...).
            Favourites.ManagerCommand => FavouritesRibbon.Manage(app),
            _ when Favourites.Slot(name, "fav:") > 0 => FavouritesRibbon.Run(app, Favourites.Slot(name, "fav:")),
            // The loader's dockable pane (0.27+): select and zoom to element ids, stop the running job quietly.
            "pane-cancel" => Pane.Cancel(),
            _ when name.StartsWith("pane-select\t", StringComparison.Ordinal) => Pane.Select(app, name.Substring("pane-select\t".Length)),
            _ when name.StartsWith("quick\t", StringComparison.Ordinal) && name.Split('\t') is { Length: 3 } q => QuickTools.Run(app, q[1], q[2]),
            _ => Unknown(name),
        };
        return result switch { Result.Succeeded => 0, Result.Cancelled => 1, _ => -1 };
    }

    private static Result SendView()
    {
        // The loader has shown the pane with its Chat tab; on the first Send view of a session the chat may not be made
        // yet: make it now (the pane adopts this same element), so the view is attached instead of refused.
        LazyPane.FillNow("chat");
        Chat.ChatPane.Get();
        if (Chat.ChatPane.AttachCurrentView()) return Result.Succeeded;
        TaskDialog.Show("Sketch ProMax", "The chat isn't open in this Revit session. Click Chat first, then Send view.");
        return Result.Cancelled;
    }

    private static Result Unknown(string name)
    {
        // A newer loader may have a button this core doesn't know yet.
        TaskDialog.Show("Sketch ProMax", $"This button ({name}) needs a newer Sketch ProMax. Check for updates from the Updates panel.");
        return Result.Cancelled;
    }

    public static string Query(string key)
    {
        var bar = key.IndexOf('|');
        var arg = bar >= 0 ? key.Substring(bar + 1) : "";
        switch (bar >= 0 ? key.Substring(0, bar) : key)
        {
            case "version": return Updater.CurrentText;
            case "busy": return App.IsBusy ? "1" : "0";
            case "autoupdate": return AgentSettings.Load().AutoUpdate ? "1" : "0";
            case "claude":
            {
                var (state, text, detail) = Connection.Describe();
                return $"{state}\t{text}\t{detail}";
            }
            case "job": return JobLine();
            case "update": return UpdateLine();
            case "quick": return QuickTools.List();
            case "quick-reload": return QuickTools.List(force: true);
            case "pane": return Pane.Snapshot(); // the dockable pane's content, see UI/Pane.cs for the format
            case "ribbon":
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var text = RibbonText();
                Revit.StartupTimings.Phase("ribbon manifest", sw.ElapsedMilliseconds);
                return text;
            }
            case "live": return LiveLabel(arg);
            case "restartnote":
                // From a 0.30+ loader once it added this core's buttons: what can only appear after a restart ("" = nothing).
                RestartNote.FromLoader(arg);
                // It follows every manifest the loader applies: the favourite slots it made or showed are put right.
                var synced = System.Diagnostics.Stopwatch.StartNew();
                try { FavouritesRibbon.AfterSync(); } catch (Exception) { /* the live labels still say "Empty" */ }
                Revit.StartupTimings.Phase("favourite slots", synced.ElapsedMilliseconds);
                // Small buttons keep their labels on a narrow ribbon.
                try { RibbonLabels.Keep(); } catch (Exception) { /* cosmetic */ }
                return "ok";
            case "swapnote":
                // Sent to the outgoing core just before "swapped": the new version's restart note, for its update window.
                _swapNote = arg.Trim().Length == 0 ? null : arg.Trim();
                return "ok";
            case "swapped":
            {
                // The loader switched from this core to version <arg>; this core is stopped. Finish its update window.
                var progress = UpdateProgress.Current;
                var note = _swapNote;
                _swapNote = null;
                if (progress is null) return "";
                progress.Report(UpdateStage.Updated, note, arg);
                return UpdateWindow.IsShowing ? "shown" : "";
            }
            case "swapfailed":
            {
                var detail = $"The new version could not be switched to while Revit runs ({arg}). Restart Revit to finish (your work is not interrupted until then).";
                var progress = UpdateProgress.Current;
                if (progress is null)
                {
                    progress = new UpdateProgress();
                    UpdateProgress.Current = progress;
                }
                progress.Surfaced = true;
                progress.Report(UpdateStage.RestartNeeded, detail);
                UpdateWindow.ShowFor(progress);
                return "shown";
            }
            case "announce":
                // Started by the loader after an automatic update, one this window followed from another Revit window, or
                // one whose window was hidden: a toast that doesn't take the focus, and the band at the top of the chat.
                UpdateNotice.Offer(new UpdateNotice.Notice(UpdateNotice.Kind.Updated, Updater.CurrentText,
                    UpdateNotice.TopLines(Updater.NotesFor(Updater.CurrentText)), RestartNote.Text));
                return "shown";
            default: return "";
        }
    }

    private static string? _swapNote;

    /// <summary>
    /// The manifest with the tools this core has; each tool's own summary is its button's long tooltip. The favourite
    /// slots start with what is pinned now (asked when the loader starts this core, so it is not cached).
    /// </summary>
    private static string RibbonText()
    {
        var registry = App.Bridge?.Registry ?? Tools.ToolRegistry.CreateDefault();
        IReadOnlyList<Favourites.Item> favourites;
        try { favourites = FavouritesRibbon.Shown; }
        catch (Exception) { favourites = []; }
        return CoreRibbonManifest.Build(name => registry.IsBuiltIn(name) && registry.Find(name) is { } tool ? RibbonTools.Summary(tool.Description) : null,
            favourites);
    }

    /// <summary>
    /// Live ribbon labels a manifest button can follow (flag live:key): "status" (the Claude connection, logo with a
    /// coloured dot), "job" (the running job and its progress), "update" (the update badge) and "fav1"... (the favourite
    /// slots on My tools). "" for an unknown key.
    /// </summary>
    private static string LiveLabel(string key)
    {
        switch (key)
        {
            case "status":
            {
                var (state, text, detail) = Connection.Describe();
                var dot = state switch { "connected" => "green", "ready" => "amber", "error" => "red", _ => "gray" };
                // A small button: one line, the coloured dot on the logo says the rest.
                var label = text.Length == 0 ? "Claude" : char.ToUpperInvariant(text[0]) + text.Substring(1);
                return CoreRibbonManifest.LiveLine(label, detail, "logo:" + dot);
            }
            case "job":
            {
                var f = JobLine().Split('\t');
                var running = f[0] is "running" or "selftest";
                // While something runs, the button stops it: the red stop tile, and the tooltip says so.
                // A large button: the tool's name and its running time on two lines.
                return f.Length < 3 ? "" : CoreRibbonManifest.LiveLine(f[1],
                    running ? f[2] + " Click to ask it to stop." : f[2] + " Click for details of the last job.", running ? "cancel" : "job");
            }
            case "update":
            {
                var f = UpdateLine().Split('\t');
                return f.Length < 3 ? "" : CoreRibbonManifest.LiveLine(f[0], f[1], f[2]);
            }
            case "update-first":
            {
                // The Claude panel's copy of the badge; the Sketch ProMax panel's one also pumps the notice.
                var ((text, _), tip, glyph) = CoreCommands.BadgeWithTip();
                return CoreRibbonManifest.LiveLine(text, tip, glyph);
            }
            case "update-badge":
            {
                var ((text, _), tip, glyph) = CoreCommands.BadgeWithTip();
                // Asked every two seconds on Revit's thread: also the moment a waiting update notice may be shown.
                try { UpdateNotice.Pump(); } catch (Exception) { /* the badge still says it */ }
                return CoreRibbonManifest.LiveLine(text, tip, glyph);
            }
            case var _ when Favourites.Slot(key, "fav") > 0:
                return FavouritesRibbon.Live(Favourites.Slot(key, "fav"));
            default: return "";
        }
    }

    private static string JobLine()
    {
        if (Jobs.Running is { } job)
        {
            var progress = job.Progress();
            var name = job.Tool.Length > 16 ? job.Tool.Substring(0, 15) + "…" : job.Tool;
            return $"running\t{name}\n{Jobs.Duration(job.Elapsed)}\t" +
                   $"Running {job.Tool} for {Jobs.Duration(job.Elapsed)}." + (progress is null ? "" : $" Progress: {progress}") +
                   " Cancel job asks it to stop at its next check.";
        }
        if (Tools.SelfTest.IsRunning) return $"selftest\tSelf-test\nrunning\t{Tools.SelfTest.Progress()}";
        if (Jobs.Find(null) is { } last)
        {
            var ok = last.Task is { IsCompleted: true } t && t.Status == TaskStatus.RanToCompletion && !t.Result.IsError;
            var finished = last.Finished?.ToLocalTime().ToString("HH:mm") ?? "";
            return $"idle\tNo job\tNothing is running. Last: {last.Tool}" + (finished.Length > 0 ? $" at {finished}" : "") +
                   (last.Task is { IsCompleted: true } ? ok ? ", worked." : ", did not work." : ".");
        }
        return "idle\tNo job\tNothing has run in this Revit window yet. Tools Claude runs show here with their progress.";
    }

    private static string UpdateLine()
    {
        if (UpdateProgress.Current is { Surfaced: true } progress)
        {
            var s = progress.Snapshot;
            var recent = !s.IsFinal || DateTime.UtcNow - s.Changed < TimeSpan.FromMinutes(10) || s.Stage == UpdateStage.RestartNeeded;
            if (recent && s.Stage != UpdateStage.Idle)
            {
                var glyph = s.Stage == UpdateStage.Downloading ? "download" : s.IsFinal ? "version" : "update";
                var tip = s.IsFinal ? (s.Detail ?? UpdateProgress.Short(s).Replace('\n', ' ')) : (s.Detail ?? "") + " Click to see the update window.";
                return $"{UpdateProgress.Short(s)}\t{tip}\t{glyph}";
            }
        }
        // New buttons or pane parts this Revit session can't show yet: the Version button says so (every loader asks).
        if (RestartNote.Text is { } restart) return $"Restart\nRevit\tSketch ProMax {Updater.CurrentText}. {restart}\tupdate";
        return $"Version\n{Updater.CurrentText}\t{Updater.Describe()}\tversion";
    }
}
