using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SketchProMax.Chat;

/// <summary>One line of an autopilot run's log: a popup answered, a step skipped, a pause, a decision.</summary>
internal sealed class AutopilotEntry
{
    public const string Popup = "popup", Allowed = "allowed", Paused = "paused", Resumed = "resumed", Note = "note", Asked = "asked";

    public DateTime Time { get; set; }
    public string Kind { get; set; } = Note;
    public string Text { get; set; } = "";
}

/// <summary>The running autopilot as every Revit window on this PC sees it (autopilot.json).</summary>
internal sealed class AutopilotShareState
{
    /// <summary>The Revit process whose chat runs the autopilot.</summary>
    public int Owner { get; set; }
    public string Run { get; set; } = "";
    /// <summary>The owner's last sign of life (UTC): it writes every half minute while the run is on.</summary>
    public DateTime Beat { get; set; }
    /// <summary>The models (their files) already backed up for this run: each gets one backup, before its first change.</summary>
    public List<string> Backups { get; set; } = [];
    /// <summary>Revit was closed the normal way during the run: nothing restarts it.</summary>
    public bool Closed { get; set; }
    /// <summary>The owner restarted Revit itself (a hang): the new Revit is on its way, the watchdog starts none.</summary>
    public bool Relaunched { get; set; }
    /// <summary>Why the owner's Revit went away (a hang it restarted from), for the report; "" for a crash.</summary>
    public string Why { get; set; } = "";
    /// <summary>Times Revit was started again for this run (the watchdog gives up after <see cref="AutopilotShare.MaxRestarts"/>).</summary>
    public int Restarts { get; set; }
}

/// <summary>
/// An autopilot that works on models in two Revit windows (a working model, and a documentation model that links it):
/// the chat that runs it lives in one window, its jobs' calls reach the other over its pipe. This file, in
/// %APPDATA%\SketchProMax, tells the other window that an autopilot is on, so it too answers its dialogs and approvals
/// for the user, backs up each model before its first change, and logs what it answered (autopilot-log.jsonl), which
/// the owner reads into the run's report. A run whose owner stopped writing (closed, crashed) is over for the others.
/// No WPF or Revit (the unit tests compile this file).
/// </summary>
internal static class AutopilotShare
{
    /// <summary>A run not written for this long (or whose Revit is gone) no longer counts.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    /// <summary>The folder of the shared files (tests point it elsewhere).</summary>
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");

    /// <summary>This Revit's process id (tests change it to play the other window).</summary>
    public static int Me { get; set; } = Process.GetCurrentProcess().Id;

    /// <summary>Whether a process still runs (tests replace it).</summary>
    public static Func<int, bool> IsAlive { get; set; } = pid =>
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    };

    private static string StateFile => Path.Combine(Folder, "autopilot.json");
    private static string LogFile => Path.Combine(Folder, "autopilot-log.jsonl");
    private static readonly object Lock = new();
    private static (DateTime At, string? Run) _cached;

    public static AutopilotShareState? Read()
    {
        lock (Lock)
        {
            try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<AutopilotShareState>(File.ReadAllText(StateFile)) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    private static void Write(AutopilotShareState state)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        _cached = default;
    }

    /// <summary>The owner starts run <paramref name="run"/>; <paramref name="backedUp"/>: the model it already backed up ("" none).</summary>
    public static void Begin(string run, string backedUp, DateTime now)
    {
        lock (Lock)
        {
            try
            {
                Write(new AutopilotShareState { Owner = Me, Run = run, Beat = now, Backups = backedUp.Length > 0 ? [backedUp] : [] });
                if (File.Exists(LogFile)) File.Delete(LogFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the other window then asks as usual */ }
        }
    }

    /// <summary>
    /// The owner's sign of life while run <paramref name="run"/> is on (it starts the run again when the file is gone). A
    /// Revit that took the run over after a restart becomes its owner, keeping the backups and the count of restarts.
    /// </summary>
    public static void Beat(string run, DateTime now)
    {
        lock (Lock)
        {
            try
            {
                var state = Read();
                if (state is null || state.Run != run || state.Owner != Me)
                    state = new AutopilotShareState { Owner = Me, Run = run, Backups = state?.Run == run ? state.Backups : [], Restarts = state?.Run == run ? state.Restarts : 0 };
                state.Beat = now;
                Write(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Restarts of Revit for one run before the watchdog gives up (Revit keeps crashing).</summary>
    public const int MaxRestarts = 3;

    /// <summary>A run whose Revit went away (a crash, or a restart after a hang) this long ago or less can be taken over.</summary>
    public static readonly TimeSpan Orphaned = TimeSpan.FromMinutes(30);

    private static void Change(Action<AutopilotShareState> change)
    {
        lock (Lock)
        {
            try
            {
                if (Read() is not { } state || state.Owner != Me) return;
                change(state);
                Write(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Revit closes the normal way: the run isn't carried on by a restart.</summary>
    public static void MarkClosed() => Change(s => s.Closed = true);

    /// <summary>The owner restarts Revit after a hang (<paramref name="why"/>): the new one carries the run on.</summary>
    public static void MarkRestart(string why) => Change(s =>
    {
        s.Relaunched = true;
        s.Why = why;
        s.Restarts++;
    });

    /// <summary>
    /// A run left behind by a Revit that went away without closing (a crash, or a restart after a hang): a new Revit
    /// takes it over. Null when there is none (no run, closed the normal way, its Revit still runs, or too long ago).
    /// </summary>
    public static AutopilotShareState? Orphan(DateTime now)
    {
        var state = Read();
        if (state is null || state.Closed || state.Owner == Me || now - state.Beat > Orphaned || IsAlive(state.Owner)) return null;
        return state;
    }

    /// <summary>
    /// The watchdog (SketchProMax.Mcp.exe --watch-revit): Revit <paramref name="pid"/> has ended; true when it should be
    /// started again (it owned a run that was on, didn't close the normal way, didn't restart itself, and hasn't been
    /// restarted too often), and the restart is counted.
    /// </summary>
    public static bool ShouldRelaunch(int pid, DateTime now)
    {
        lock (Lock)
        {
            try
            {
                if (Read() is not { } state || state.Owner != pid || state.Closed || state.Relaunched || now - state.Beat > Fresh + Fresh
                    || state.Restarts >= MaxRestarts) return false;
                state.Restarts++;
                state.Relaunched = true; // one Revit, even if a second watcher (after an update in session) asks too
                Write(state);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>The watchdog asked to open Revit again and the user said no: the run counts as closed (no Revit takes it over).</summary>
    public static void Decline(int pid)
    {
        lock (Lock)
        {
            try
            {
                if (Read() is not { } state || state.Owner != pid) return;
                state.Closed = true;
                Write(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The run ended: the other windows go back to asking.</summary>
    public static void End(string run)
    {
        lock (Lock)
        {
            try
            {
                if (Read() is { } state && state.Run == run) File.Delete(StateFile);
                _cached = default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The run another Revit window's autopilot has on now, or null (read again every few seconds).</summary>
    public static string? Remote(DateTime now)
    {
        if (now - _cached.At < TimeSpan.FromSeconds(5) && now >= _cached.At) return _cached.Run;
        var state = Read();
        var run = state is not null && state.Owner != Me && now - state.Beat < Fresh && state.Beat <= now + TimeSpan.FromMinutes(1) && IsAlive(state.Owner) ? state.Run : null;
        _cached = (now, run);
        return run;
    }

    /// <summary>The run on now in any window, this one's included (null: none).</summary>
    public static string? Any(DateTime now)
    {
        var state = Read();
        return state is not null && now - state.Beat < Fresh && (state.Owner == Me || IsAlive(state.Owner)) ? state.Run : null;
    }

    /// <summary>
    /// The model at <paramref name="path"/> is about to change while a run is on: true the first time (it is backed up
    /// now), false when it already was or no run is on.
    /// </summary>
    public static bool Claim(string path, DateTime now)
    {
        if (path.Length == 0) return false;
        lock (Lock)
        {
            try
            {
                var state = Read();
                if (state is null || now - state.Beat >= Fresh) return false;
                if (state.Backups.Any(b => string.Equals(b, path, StringComparison.OrdinalIgnoreCase))) return false;
                state.Backups.Add(path);
                Write(state);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>Another window answered something for the user: kept for the owner's report.</summary>
    public static void AppendLog(string run, string kind, string text, DateTime now)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(LogFile, JsonSerializer.Serialize(new AutopilotEntry { Time = now, Kind = kind, Text = text }) + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The owner: the lines logged outside its chat (another window, the watcher, a Revit that was restarted) since it
    /// last looked. They are taken off the file, so a Revit that takes the run over never reads them twice.
    /// </summary>
    public static List<AutopilotEntry> TakeLog()
    {
        lock (Lock)
        {
            var taken = new List<AutopilotEntry>();
            try
            {
                if (!File.Exists(LogFile)) return taken;
                var lines = File.ReadAllLines(LogFile);
                File.Delete(LogFile);
                foreach (var line in lines)
                {
                    try { if (JsonSerializer.Deserialize<AutopilotEntry>(line) is { } entry) taken.Add(entry); }
                    catch (JsonException) { /* a line cut short: skipped */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return taken;
        }
    }
}
