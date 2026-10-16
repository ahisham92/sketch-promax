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
///                      loader's dockable pane ("chat"), or null. Asked again after each version switch.
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
    /// Content the core puts into the loader's dockable pane: "chat" is the Claude chat. Returns a WPF FrameworkElement
    /// (typed object so the signature stays primitive), or null for a name this core doesn't provide. Called on Revit's
    /// UI thread, after Start.
    /// </summary>
    public static object? PaneContent(string name) => name switch
    {
        "chat" => Chat.ChatPane.Get(),
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
            "self-test" => CoreCommands.SelfTest(app),
            "field-test" => Tools.FieldTest.RibbonCommand(app),
            "update-info" => CoreCommands.UpdateInfo(),
            "update-check" => CoreCommands.CheckForUpdates(),
            "whats-new" => CoreCommands.OpenUrl(CoreCommands.ReleasesUrl),
            "settings" => CoreCommands.Settings(),
            "help" => CoreCommands.OpenUrl(CoreCommands.HelpUrl),
            // The loader's dockable pane (0.27+): select and zoom to element ids, stop the running job quietly.
            "pane-cancel" => Pane.Cancel(),
            _ when name.StartsWith("pane-select\t", StringComparison.Ordinal) => Pane.Select(app, name.Substring("pane-select\t".Length)),
            _ when name.StartsWith("quick\t", StringComparison.Ordinal) && name.Split('\t') is { Length: 3 } q => QuickTools.Run(app, q[1], q[2]),
            _ => Unknown(name),
        };
        return result switch { Result.Succeeded => 0, Result.Cancelled => 1, _ => -1 };
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
            case "swapped":
            {
                // The loader switched from this core to version <arg>; this core is stopped. Finish its update window.
                var progress = UpdateProgress.Current;
                if (progress is null) return "";
                progress.Report(UpdateStage.Updated, null, arg);
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
                // Started by the loader after an update this window followed from another Revit window, or whose window
                // was hidden: say so briefly.
                UpdateWindow.Announce(new UpdateSnapshot(UpdateStage.Updated, Updater.CurrentText, 0, null, null, DateTime.UtcNow));
                return "shown";
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
        return $"Version\n{Updater.CurrentText}\t{Updater.Describe()}\tversion";
    }
}
