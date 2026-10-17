using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.UI;
using SketchProMax.Update;

namespace SketchProMax.Commands;

/// <summary>Ribbon buttons stay available with no document open.</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

// The command classes of the ribbon before the loader. Kept so a ribbon built without the loader (it is missing next to
// this DLL) still has Status, Settings and Self-test; the loader's buttons reach the same code through CoreEntry.

[Transaction(TransactionMode.Manual)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => CoreCommands.Settings();
}

[Transaction(TransactionMode.Manual)]
public sealed class StatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => CoreCommands.Status(commandData.Application);
}

[Transaction(TransactionMode.Manual)]
public sealed class SelfTestCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => CoreCommands.SelfTest(commandData.Application);
}

/// <summary>What each ribbon button does. Runs on Revit's thread, inside the button's command.</summary>
internal static class CoreCommands
{
    public const string ReleasesUrl = "https://github.com/" + Updater.Repository + "/releases/latest";
    public const string HelpUrl = "https://github.com/" + Updater.Repository + "#readme";

    public static Result Settings()
    {
        var window = new SettingsWindow();
        Brand.Prepare(window);
        window.ShowDialog();
        return Result.Succeeded;
    }

    public static Result Status(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        ClaudeRegistration.Ensure();
        var registered = ClaudeRegistration.Registered();
        var (state, _, detail) = Connection.Describe();
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = state switch
            {
                "connected" => "Claude is connected to this Revit window",
                "ready" => "Ready: waiting for Claude",
                "error" => "Sketch ProMax can't accept connections",
                _ => "The Claude desktop app is not connected",
            },
            MainContent = detail + "\n\n" +
                (doc is null ? "Open a model, then " : $"Active model: {doc.Title}\n\n") +
                "chat in the Claude desktop app, for example: \"What's in my Revit model?\"\n\n" +
                "If the Revit tools don't appear in Claude, quit Claude completely (system tray > Quit) and start it again.",
            FooterText = Updater.Describe(),
            ExpandedContent = $"Revit {app.Application.VersionNumber}, session {BridgeServer.SessionId}, Sketch ProMax {Updater.CurrentText}" +
                              (Updater.CanSwitchLive ? " (updates without restarting)" : "") + "\n" +
                              SketchProMax.Revit.AssemblyReport.Summary() + "\n\n" +
                              "Registered with the Claude app in:\n" +
                              (registered.Count > 0 ? string.Join("\n", registered) : "(nowhere yet - is the Claude desktop app installed?)"),
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Copy a starter prompt", "A good first message for Claude, on the clipboard.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open Claude");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Check for updates now");
        switch (dialog.Show())
        {
            case TaskDialogResult.CommandLink1: return CopyPrompt(app);
            case TaskDialogResult.CommandLink2: return OpenClaude();
            case TaskDialogResult.CommandLink3: return CheckForUpdates();
            default: return Result.Succeeded;
        }
    }

    public static string StarterPrompt(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        var model = doc is null ? "the model I have open in Revit" : $"the Revit model \"{doc.Title}\"";
        return $"I'm working in Revit {app.Application.VersionNumber} on {model}, with Sketch ProMax connected.\n" +
               "1. Look at the model first (get_model_context) and give me a short overview: levels, main element counts by category, " +
               "views and sheets, and anything that looks wrong or unfinished.\n" +
               "2. Then suggest five useful things you could do for me in this model (documentation, checks, quantities or modeling), " +
               "and wait for me to choose.\n" +
               "Ask me before changing anything, and keep each change undoable.";
    }

    public static Result CopyPrompt(UIApplication app)
    {
        var copied = ClipboardText.TrySet(StarterPrompt(app));
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = copied ? "Starter prompt copied" : "The clipboard is busy",
            MainContent = copied
                ? "Paste it (Ctrl+V) into a new chat in the Claude desktop app."
                : "Another program is holding the clipboard. Try again in a moment.",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        if (copied) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open Claude now");
        if (dialog.Show() == TaskDialogResult.CommandLink1) return OpenClaude();
        return copied ? Result.Succeeded : Result.Failed;
    }

    public static Result OpenClaude()
    {
        if (ClaudeLauncher.Start(out var how)) return Result.Succeeded;
        TaskDialog.Show("Sketch ProMax", "The Claude desktop app wasn't found on this PC. Install it from claude.ai/download " +
                                          $"(or the Microsoft Store), sign in, then click Open Claude again.\n\n{how}");
        return Result.Failed;
    }

    public static Result Job()
    {
        var running = Jobs.Running;
        if (running is null && Tools.SelfTest.IsRunning) return SelfTest(null);
        var job = running ?? Jobs.Find(null);
        if (job is null)
        {
            TaskDialog.Show("Sketch ProMax", "No job has run in this Revit window yet. When Claude runs a tool, it shows here with its progress.");
            return Result.Succeeded;
        }
        var dialog = new TaskDialog("Sketch ProMax job")
        {
            MainInstruction = running is null ? $"Last job: {job.Tool}" : $"Running: {job.Tool}",
            MainContent = Jobs.Status(job).Text,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        if (running is not null) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Ask it to stop");
        if (dialog.Show() == TaskDialogResult.CommandLink1) return CancelJob();
        return Result.Succeeded;
    }

    /// <summary>The Sketch ProMax panel's Job button: stops what is running, else shows the last job.</summary>
    public static Result JobButton() => Jobs.Running is not null || Tools.SelfTest.IsRunning ? CancelJob() : Job();

    /// <summary>
    /// The Update button: "Update ready" installs the newer version now (in this Revit session), "What's new" opens the
    /// release notes (and the badge goes back to normal), "Couldn't check" and "Check for updates" look for a new version
    /// now, anything else shows the version with its update window or choices.
    /// </summary>
    public static Result UpdateButton()
    {
        switch (Badge().Kind)
        {
            case UpdateBadge.Kind.WhatsNew:
                return WhatsNew();
            case UpdateBadge.Kind.Ready:
            case UpdateBadge.Kind.Failed:
            case UpdateBadge.Kind.Check:
                _checkState = null;
                return CheckForUpdates();
            default:
                return UpdateInfo();
        }
    }

    /// <summary>Opens the release notes; the "What's new" badge goes back to normal.</summary>
    public static Result WhatsNew()
    {
        MarkSeen();
        return OpenUrl(ReleasesUrl);
    }

    private static (DateTime? Last, string? Error, string? Latest, string? Staged)? _checkState;
    private static int _checkStateAt;
    private static Version? _seen;

    private static string SeenFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "whats-new-seen.txt");

    /// <summary>
    /// The Update button's badge, asked every two seconds: the update-state file is read at most every half minute, and
    /// at once after a run of this window changed it.
    /// </summary>
    internal static (string Text, UpdateBadge.Kind Kind) Badge() => BadgeWithTip().Badge;

    internal static ((string Text, UpdateBadge.Kind Kind) Badge, string Tip, string Glyph) BadgeWithTip()
    {
        string? working = null, ready = null;
        if (UpdateProgress.Current is { } progress)
        {
            var snapshot = progress.Snapshot;
            // Applying: downloaded, waiting for Revit to be idle with no job running.
            if (snapshot.Stage == UpdateStage.Applying) ready = snapshot.Version;
            else if (!snapshot.IsFinal && snapshot.Stage != UpdateStage.Idle) working = UpdateProgress.Short(snapshot);
            if (snapshot.IsFinal && snapshot.Changed > _checkStateTime) _checkState = null;
        }
        if (_checkState is null || unchecked(Environment.TickCount - _checkStateAt) > 30000)
        {
            var (last, error) = Updater.LastCheckState();
            _checkState = (last, error, Updater.Latest, Updater.Staged);
            _checkStateAt = Environment.TickCount;
            _checkStateTime = DateTime.UtcNow;
        }
        var (lastCheck, lastError, latest, staged) = _checkState.Value;
        ready ??= staged ?? latest;
        var badge = UpdateBadge.For(working, ready, RestartNote.Text is not null, Updater.Current, SeenVersion(), lastCheck, lastError, DateTime.UtcNow);
        var tip = UpdateBadge.Tip(badge.Kind, Updater.CurrentText, lastError, lastCheck, Updater.Describe(), RestartNote.Text,
            working is null ? null : UpdateProgress.Current?.Snapshot.Detail is { Length: > 0 } d ? d + " Click to see the update window." : null);
        var glyph = badge.Kind switch
        {
            UpdateBadge.Kind.WhatsNew => "news",
            UpdateBadge.Kind.Ready => "download",
            UpdateBadge.Kind.Working => UpdateProgress.Current?.Snapshot.Stage == UpdateStage.Downloading ? "download" : "update",
            UpdateBadge.Kind.Check or UpdateBadge.Kind.Restart or UpdateBadge.Kind.Failed => "update",
            _ => "version",
        };
        return (badge, tip, glyph);
    }

    private static DateTime _checkStateTime;

    /// <summary>
    /// The version whose release notes were last opened. The first time (nothing stored) it is this version on a fresh
    /// install, so that doesn't start with a "What's new" badge; after an update from a version that recorded nothing,
    /// the older version (<see cref="UpdateBadge.FirstRunSeen"/>), so it does.
    /// </summary>
    private static Version SeenVersion()
    {
        if (_seen is not null) return _seen;
        try
        {
            if (File.Exists(SeenFile) && Version.TryParse(File.ReadAllText(SeenFile).Trim(), out var v)) return _seen = v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Record(UpdateBadge.FirstRunSeen(Updater.Current, VersionFolders(), EarlierUpdateState()));
        return _seen ?? Updater.Current;
    }

    private static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax");

    private static IEnumerable<string> VersionFolders()
    {
        try
        {
            var folder = Path.Combine(LocalRoot, "versions");
            return Directory.Exists(folder) ? Directory.GetDirectories(folder).Select(Path.GetFileName).OfType<string>().ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>True when an update check wrote its state before this Revit session started (so an older version ran here).</summary>
    private static bool EarlierUpdateState()
    {
        try
        {
            var state = Path.Combine(LocalRoot, "update-state.json");
            return File.Exists(state) && File.GetCreationTime(state) < Process.GetCurrentProcess().StartTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static void MarkSeen() => Record(Updater.Current);

    private static void Record(Version seen)
    {
        _seen = seen;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SeenFile)!);
            File.WriteAllText(SeenFile, $"{seen.Major}.{seen.Minor}.{Math.Max(seen.Build, 0)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static Result CancelJob()
    {
        if (Jobs.Running is null && Tools.SelfTest.IsRunning)
        {
            Tools.SelfTest.Stop();
            TaskDialog.Show("Sketch ProMax", "The self-test stops after its current step.");
            return Result.Succeeded;
        }
        var answer = CancelJobTool.Cancel(new Tools.ToolArgs(new Dictionary<string, System.Text.Json.JsonElement>()));
        TaskDialog.Show("Sketch ProMax", answer.Text.Contains("nothing to cancel")
            ? "Nothing is running."
            : "Asked the job to stop. It stops at its next check; what it finished so far is kept as one Undo step.");
        return Result.Succeeded;
    }

    public static Result QuickFolder()
    {
        var folder = Tools.Recipes.Folder;
        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Tools.LearnedTools.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(folder)}\"") { UseShellExecute = true })?.Dispose();
            return Result.Succeeded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TaskDialog.Show("Sketch ProMax", $"Couldn't open {folder}: {ex.Message}");
            return Result.Failed;
        }
    }

    public static Result SelfTest(UIApplication? app)
    {
        if (Tools.SelfTest.IsRunning)
        {
            var running = new TaskDialog("Sketch ProMax self-test")
            {
                MainInstruction = "A self-test is running",
                MainContent = Tools.SelfTest.Progress(),
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            running.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Stop it after the current step");
            if (running.Show() == TaskDialogResult.CommandLink1) Tools.SelfTest.Stop();
            return Result.Succeeded;
        }
        // A job (the field test runs one between its steps, with Revit free in between) owns the model until it ends.
        if (Jobs.Running is { } busy)
        {
            TaskDialog.Show("Sketch ProMax self-test", Jobs.Busy(busy).Text);
            return Result.Cancelled;
        }
        if (app?.ActiveUIDocument is null)
        {
            TaskDialog.Show("Sketch ProMax", "Open a model first. A copy of a real project gives the most useful result.");
            return Result.Cancelled;
        }
        var ask = new TaskDialog("Sketch ProMax self-test")
        {
            MainInstruction = "Test the Sketch ProMax tools on this model",
            MainContent = "Tools run one at a time in the background, so you can keep working, but stay in this model until it " +
                          "finishes. Slow tools are skipped on large models. Click Self-test again to see progress or stop it.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
        };
        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Quick check (recommended)", "Read-only tools only. Nothing in the model changes.");
        // This choice is the confirmation for the full check, so it names the model and says to use a copy.
        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Full check, on a copy of a model",
            "Also runs the tools that change the model. Each change is undone straight after it is checked. " +
            Tools.SelfTest.FullWarning(app.ActiveUIDocument.Document).Replace(" Continue?", ""));
        var choice = ask.Show();
        if (choice is not (TaskDialogResult.CommandLink1 or TaskDialogResult.CommandLink2)) return Result.Cancelled;

        var registry = App.Bridge?.Registry ?? Tools.ToolRegistry.CreateDefault();
        var mode = choice == TaskDialogResult.CommandLink2 ? Tools.SelfTest.Mode.Full : Tools.SelfTest.Mode.Safe;
        Tools.SelfTest.Start(app, registry, mode, report => new TaskDialog("Sketch ProMax self-test")
        {
            MainInstruction = report.Failed == 0 ? "Every tested tool works" : $"{report.Failed} tool(s) failed",
            MainContent = Tools.SelfTest.Progress(),
            FooterText = report.File is null ? "" : "Send the report file to Claude (attach it in the chat) to get any failures fixed.",
        }.Show());
        return Result.Succeeded;
    }

    public static Result UpdateInfo()
    {
        if (UpdateWindow.ShowCurrent()) return Result.Succeeded;
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = $"Sketch ProMax {Updater.CurrentText}",
            MainContent = Updater.Describe() + (Updater.CanSwitchLive
                ? "\n\nUpdates install while Revit stays open and switch over as soon as no job is running. No restart needed."
                : ""),
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Check for updates now");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "What's new");
        return dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 => CheckForUpdates(),
            TaskDialogResult.CommandLink2 => OpenUrl(ReleasesUrl),
            _ => Result.Succeeded,
        };
    }

    /// <summary>Checks now, in the background, with the progress window open from the start.</summary>
    public static Result CheckForUpdates()
    {
        App.CheckForUpdatesInteractive();
        return Result.Succeeded;
    }

    public static Result OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return Result.Succeeded;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskDialog.Show("Sketch ProMax", $"Open this address in your browser:\n{url}");
            return Result.Failed;
        }
    }
}

/// <summary>Is the Claude desktop app talking to this Revit window? Answered from memory for the ribbon.</summary>
internal static class Connection
{
    private static volatile bool _claudeRunning;
    private static DateTime _checked = DateTime.MinValue;
    private static int _checking;

    /// <summary>(state, short text, detail). State: connected, ready, offline or error.</summary>
    public static (string State, string Short, string Detail) Describe()
    {
        RefreshProcessState();
        var bridge = App.Bridge;
        if (bridge is null) return ("offline", "starting", "Sketch ProMax is starting in this Revit window.");
        if (!bridge.IsListening)
            return ("error", "no link", "Sketch ProMax could not open its connection point in this Revit window. It keeps retrying; " +
                                        "if this stays, restart Revit.");
        var last = BridgeServer.LastContact;
        var lastJob = Jobs.Find(null);
        var jobText = lastJob is null ? "" : $" Last tool call: {lastJob.Tool} at {lastJob.Queued.ToLocalTime():HH:mm}.";
        if (last is { } t && DateTime.UtcNow - t < TimeSpan.FromMinutes(10))
            return ("connected", "connected", $"Claude last talked to this Revit window at {t.ToLocalTime():HH:mm} ({Ago(t)}).{jobText}");
        if (_claudeRunning)
            return ("ready", "ready", "The Claude desktop app is running and can reach this Revit window. Ask it something about the model." +
                                      (last is { } older ? $" Last contact {Ago(older)}." : "") + jobText);
        return ("offline", "not running", "The Claude desktop app is not running. Click Open Claude, or start it from the Start menu.");
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 60) return $"{Math.Max(1, (int)span.TotalSeconds)} s ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        return $"{(int)span.TotalHours} h ago";
    }

    /// <summary>Looks for the Claude app's process at most every 10 s, off Revit's thread.</summary>
    private static void RefreshProcessState()
    {
        if (DateTime.UtcNow - _checked < TimeSpan.FromSeconds(10)) return;
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        _checked = DateTime.UtcNow;
        Task.Run(() =>
        {
            try
            {
                var processes = Process.GetProcessesByName("claude");
                _claudeRunning = processes.Length > 0;
                foreach (var p in processes) p.Dispose();
            }
            catch (Exception) { /* keep the last answer */ }
            finally { Interlocked.Exchange(ref _checking, 0); }
        });
    }
}

/// <summary>Starts the Claude desktop app: the normal installer's exe, else the Microsoft Store build, else its link handler.</summary>
internal static class ClaudeLauncher
{
    public static bool Start(out string tried)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var files = new[]
        {
            Path.Combine(local, "AnthropicClaude", "claude.exe"),
            Path.Combine(local, "Programs", "Claude", "Claude.exe"),
            Path.Combine(local, "Programs", "claude-desktop", "Claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Claude", "Claude.exe"),
        };
        var log = new List<string>();
        tried = "";
        foreach (var file in files)
            if (File.Exists(file) && Launch(file, null, log)) return true;

        // Microsoft Store (MSIX) build: started through its package family name.
        var packages = Path.Combine(local, "Packages");
        try
        {
            if (Directory.Exists(packages))
            {
                var family = new System.Text.RegularExpressions.Regex(@"(^|\.)Claude_[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var package in Directory.GetDirectories(packages).Select(Path.GetFileName))
                    if (package is not null && family.IsMatch(package) && Launch("explorer.exe", $"shell:AppsFolder\\{package}!Claude", log)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (Launch("claude://", null, log)) return true;
        tried = log.Count == 0 ? "" : "Tried: " + string.Join("; ", log);
        return false;
    }

    private static bool Launch(string file, string? arguments, List<string> log)
    {
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = true };
            if (arguments is not null) info.Arguments = arguments;
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            log.Add($"{file} {arguments}: {ex.Message}".Trim());
            return false;
        }
    }
}
