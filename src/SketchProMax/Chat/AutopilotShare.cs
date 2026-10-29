using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SketchProMax.Chat;

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

    /// <summary>The owner's sign of life while run <paramref name="run"/> is on (it starts the run again when the file is gone).</summary>
    public static void Beat(string run, DateTime now)
    {
        lock (Lock)
        {
            try
            {
                var state = Read();
                if (state is null || state.Run != run || state.Owner != Me) state = new AutopilotShareState { Owner = Me, Run = run, Backups = state?.Run == run ? state.Backups : [] };
                state.Beat = now;
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

    /// <summary>The owner: the lines the other windows logged since <paramref name="read"/> (how many it has taken; updated).</summary>
    public static List<AutopilotEntry> TakeLog(ref int read)
    {
        lock (Lock)
        {
            var taken = new List<AutopilotEntry>();
            try
            {
                if (!File.Exists(LogFile)) { read = 0; return taken; }
                var lines = File.ReadAllLines(LogFile);
                if (lines.Length < read) read = 0;
                foreach (var line in lines.Skip(read))
                {
                    try { if (JsonSerializer.Deserialize<AutopilotEntry>(line) is { } entry) taken.Add(entry); }
                    catch (JsonException) { /* a line being written: read past it */ }
                }
                read = lines.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return taken;
        }
    }
}
