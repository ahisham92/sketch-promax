using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Revit;

/// <summary>
/// What Revit is busy with while it doesn't respond, read from the end of Revit's own journal (the journal is written
/// as Revit works, so its last lines name the command that started the long work and the step it is in), and the
/// commands the user asked to be warned about before they run (<see cref="Warned"/>). Pure: the Rescue window
/// (UI/RescueWindow) reads the journal's tail off Revit's thread and hands the lines here; the tests compile this file.
/// </summary>
internal static class FreezeCause
{
    /// <param name="What">The command, as Revit names it ("Run systems analysis"), or the long work ("Synchronizing with central").</param>
    /// <param name="CommandId">Revit's id for the command ("ID_RUN_SYSTEMS_ANALYSIS"), or null when no command started it.</param>
    /// <param name="Step">The transaction it is in ("Create Energy Analysis Model"), or null.</param>
    /// <param name="Since">When the command started (journal time), or null.</param>
    /// <param name="Ours">The step is a Sketch ProMax transaction.</param>
    /// <param name="Dialog">The text of a Revit message still waiting for an answer, or null.</param>
    public sealed record Busy(string What, string? CommandId, string? Step, DateTime? Since, bool Ours, string? Dialog);

    private static readonly Regex Command = new("^\\s*Jrn\\.Command\\s+\"(?<src>[^\"]*)\"\\s*,\\s*\"(?<text>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex Stamp = new("^\\s*'[CEH] (?<t>\\d{2}-\\w{3}-\\d{4} \\d{2}:\\d{2}:\\d{2})", RegexOptions.Compiled);
    private static readonly Regex Transaction = new("^\\s*'inTransaction \\S+ (?<name>.+?)\\s*$", RegexOptions.Compiled);
    private static readonly Regex Done = new("\"Transaction (Successful|Failed|Cancelled|RolledBack)\"\\s*,\\s*\"(?<name>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex Slog = new("SLOG \\$\\w+ \\S+ \\S+ (?<dir>[<>])(?<tag>[A-Za-z]+)(?=[: ]|$)", RegexOptions.Compiled);
    private static readonly Regex Dialog = new("^\\s*'\\s*\\d+:< TaskDialog \"(?<text>[^\"]*)", RegexOptions.Compiled);

    /// <summary>Long work the journal brackets with SLOG lines, in the words the user knows.</summary>
    private static readonly Dictionary<string, string> Brackets = new(StringComparer.Ordinal)
    {
        ["STC"] = "Synchronizing with central", ["Open"] = "Opening a model", ["OpenLink"] = "Loading a linked model",
        ["ReloadLatest"] = "Reloading latest", ["Save"] = "Saving",
    };

    /// <summary>Commands that end an earlier one (Esc, Undo) and say nothing about what is running.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal) { "ID_CANCEL_EDITOR", "ID_EDIT_UNDO", "ID_EDIT_REDO" };

    /// <summary>What the journal's last lines say Revit is busy with. Never throws; "something of its own" when nothing can be told.</summary>
    public static Busy Read(IReadOnlyList<string> tail)
    {
        try { return ReadCore(tail); }
        catch (Exception) { return new Busy("something of its own", null, null, null, false, null); }
    }

    private static Busy ReadCore(IReadOnlyList<string> tail)
    {
        int command = -1;
        string? what = null, id = null;
        for (var i = tail.Count - 1; i >= 0 && command < 0; i--)
        {
            var m = Command.Match(tail[i]);
            if (!m.Success) continue;
            var (name, cid) = SplitCommand(m.Groups["text"].Value);
            if (cid is not null && Noise.Contains(cid)) continue;
            command = i;
            what = name;
            id = cid;
        }

        // The transaction in progress after that command: the last one opened that the journal hasn't closed.
        string? step = null;
        var closed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = tail.Count - 1; i > command; i--)
        {
            var done = Done.Match(tail[i]);
            if (done.Success) { closed.Add(Trimmed(done.Groups["name"].Value)); continue; }
            var t = Transaction.Match(tail[i]);
            if (!t.Success) continue;
            var name = Trimmed(t.Groups["name"].Value);
            if (closed.Contains(name)) continue;
            step = name;
            break;
        }

        // Long work bracketed by SLOG ">Tag" … "<Tag" that hasn't closed (newer than the command).
        string? bracket = null;
        var shut = new HashSet<string>(StringComparer.Ordinal);
        for (var i = tail.Count - 1; i > command; i--)
        {
            var s = Slog.Match(tail[i]);
            if (!s.Success) continue;
            var tag = s.Groups["tag"].Value;
            if (s.Groups["dir"].Value == "<") { shut.Add(tag); continue; }
            if (shut.Contains(tag) || !Brackets.TryGetValue(tag, out var words)) continue;
            bracket = words;
            break;
        }

        // A Revit message after the command that has no answer yet (its "TaskDialogResult" comes after it).
        string? dialog = null;
        for (var i = tail.Count - 1; i > command; i--)
        {
            if (tail[i].Contains("\"TaskDialogResult\"")) break;
            var d = Dialog.Match(tail[i]);
            if (d.Success) { dialog = d.Groups["text"].Value.Trim(); break; }
        }

        var since = command >= 0 ? TimeNear(tail, command) : null;
        var ours = step is not null && step.StartsWith("Sketch ProMax", StringComparison.Ordinal);
        if (bracket is not null && (what is null || id is "ID_FILE_SAVE_TO_CENTRAL" or "ID_REVIT_FILE_OPEN" or "ID_FILE_SAVE")) what = bracket;
        return new Busy(what ?? bracket ?? "something of its own", id, step, since, ours, dialog);
    }

    /// <summary>"Run systems analysis. , ID_RUN_SYSTEMS_ANALYSIS" → ("Run systems analysis", "ID_RUN_SYSTEMS_ANALYSIS").</summary>
    public static (string Name, string? Id) SplitCommand(string text)
    {
        var comma = text.LastIndexOf(" , ", StringComparison.Ordinal);
        var name = (comma >= 0 ? text.Substring(0, comma) : text).Trim().TrimEnd('.').Trim();
        var id = comma >= 0 ? text.Substring(comma + 3).Trim() : null;
        if (id is { Length: 0 }) id = null;
        if (name.Length == 0 && id is not null) name = Humanize(id);
        return (name.Length == 0 ? "a Revit command" : name, id);
    }

    /// <summary>"ID_FILE_CADFORMAT_LINK" → "File cadformat link", for a command the journal doesn't describe.</summary>
    public static string Humanize(string id)
    {
        var words = (id.StartsWith("ID_", StringComparison.Ordinal) ? id.Substring(3) : id).Replace('_', ' ').Trim().ToLowerInvariant();
        return words.Length == 0 ? "a Revit command" : char.ToUpperInvariant(words[0]) + words.Substring(1);
    }

    private static string Trimmed(string name) => name.TrimEnd('.', ' ');

    /// <summary>The journal time nearest before line <paramref name="index"/> (else just after it).</summary>
    private static DateTime? TimeNear(IReadOnlyList<string> tail, int index)
    {
        for (var i = index; i >= 0 && i > index - 400; i--)
            if (Parse(tail[i]) is { } t) return t;
        for (var i = index + 1; i < tail.Count && i < index + 50; i++)
            if (Parse(tail[i]) is { } t) return t;
        return null;
    }

    private static DateTime? Parse(string line)
    {
        var m = Stamp.Match(line);
        return m.Success && DateTime.TryParseExact(m.Groups["t"].Value, "dd-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : null;
    }

    // ---- Words for the Rescue window ------------------------------------------------------------------------------

    /// <summary>"6 min", "45 s", "1 h 12 min".</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)} s"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min"
        : $"{(int)t.TotalHours} h {t.Minutes} min";

    /// <summary>The window's main line: what Revit is busy with, and for how long.</summary>
    /// <param name="ours">A Sketch ProMax call is running on Revit's thread now (a task, a tool, a Lifeline replay): that, not
    /// the journal's last command, is what keeps Revit busy (calls of ours don't show as commands in the journal).</param>
    public static string Line(Busy busy, TimeSpan frozen, bool ours = false)
    {
        ours |= busy.Ours;
        var step = busy.Step is { Length: > 0 } s && !string.Equals(s, busy.What, StringComparison.OrdinalIgnoreCase) && !ours ? $" ({s})" : "";
        var what = ours ? "a Sketch ProMax task" : busy.What + step;
        return $"Revit is busy with {what}. It hasn't responded for {Duration(frozen)}.";
    }

    /// <summary>Whose work it is, in one short line.</summary>
    public static string Whose(Busy busy, bool jobRunning) =>
        busy.Ours || jobRunning
            ? "This is a Sketch ProMax task, not Revit's own: a task, a tool or a Lifeline replay. Stop it below or in its own window, or let it finish."
            : busy.CommandId is null && busy.What == "something of its own"
                ? "This is Revit's own work, not Sketch ProMax."
                : "This is Revit's own command, not Sketch ProMax.";

    /// <summary>
    /// Offer "Warn me before this command next time" once Revit has been frozen a while by a command of its own that
    /// isn't one people need every day (saving, syncing, opening, undo). Never while Sketch ProMax work runs on Revit's
    /// thread (<paramref name="ours"/>): then the journal's last command is only what was open, not what froze Revit
    /// (27 Oct 2026: a chat task froze Revit during Create Spot Elevation, and the warning was offered for spot elevations).
    /// </summary>
    public static bool OfferWarning(Busy busy, TimeSpan frozen, bool ours = false) =>
        busy.CommandId is { Length: > 0 } id && !busy.Ours && !ours && frozen >= WarnAfter && !NeverWarn.Contains(id);

    /// <summary>
    /// How long Revit may be stuck before the Rescue window opens: 20 s, but 1 min for everyday long work (opening a model
    /// or a link, syncing, saving), so the window doesn't open at every short sync of a big model.
    /// </summary>
    public static TimeSpan OpenAfter(Busy busy) => Routine.Contains(busy.What) ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> Routine = new(Brackets.Values, StringComparer.Ordinal);

    /// <summary>Long work Revit is known to do (synchronizing, opening, saving, loading links): worth waiting longer for.</summary>
    public static bool IsLongWork(Busy busy) => Routine.Contains(busy.What);

    /// <summary>How long a freeze must last before its command is worth a warning.</summary>
    public static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(60);

    /// <summary>Commands too common to warn about, however long they take.</summary>
    public static readonly HashSet<string> NeverWarn = new(StringComparer.Ordinal)
    {
        "ID_FILE_SAVE_TO_CENTRAL", "ID_FILE_SAVE", "ID_REVIT_FILE_SAVE", "ID_REVIT_FILE_OPEN", "ID_FILE_OPEN", "ID_EDIT_UNDO",
        "ID_EDIT_REDO", "ID_CANCEL_EDITOR", "ID_RELOAD_LATEST", "ID_FILE_SYNCHRONIZE_NOW",
    };

    // ---- Working or stuck: Revit's processor, memory, file and journal activity -------------------------------------

    /// <param name="Cpu">Revit's processor time so far (all threads).</param>
    /// <param name="Memory">Revit's private memory, bytes.</param>
    /// <param name="Files">Bytes Revit has read and written so far (models, links, central, the network).</param>
    /// <param name="Journal">The journal's length, bytes (Revit writes it as it works).</param>
    public sealed record Sample(DateTime At, TimeSpan Cpu, long Memory, long Files, long Journal);

    public enum Progress { Working, Files, Quiet, Stuck }

    /// <param name="CpuPercent">As Task Manager shows it (share of the whole PC), over the last 30 s.</param>
    /// <param name="MemoryChange">Change over the last 30 s, bytes.</param>
    /// <param name="FilesBytes">Read and written over the last 30 s.</param>
    /// <param name="Quiet">Since the last activity of any kind.</param>
    public sealed record Activity(Progress Progress, double CpuPercent, long Memory, long MemoryChange, long FilesBytes, TimeSpan Quiet);

    /// <summary>No activity for this long: Revit is most likely stuck, not working.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Working (the processor busy on a quarter of one core or more: real work keeps a core busy, a stuck Revit that
    /// only polls uses a few percent; memory moving; or the journal written), reading or
    /// writing files, quiet for a while, or stuck (nothing at all for <see cref="StuckAfter"/>), from samples a second
    /// apart, oldest first. Fewer than two samples: Working with no figures (nothing to judge yet).
    /// </summary>
    public static Activity Judge(IReadOnlyList<Sample> samples, int cores, DateTime now)
    {
        if (samples.Count < 2) return new Activity(Progress.Working, 0, samples.Count == 1 ? samples[0].Memory : 0, 0, 0, TimeSpan.Zero);
        cores = Math.Max(1, cores);
        DateTime? busy = null, files = null;
        for (var i = 1; i < samples.Count; i++)
        {
            var a = samples[i - 1];
            var b = samples[i];
            var wall = (b.At - a.At).TotalSeconds;
            if (wall <= 0) continue;
            var core = (b.Cpu - a.Cpu).TotalSeconds / wall;
            if (core >= 0.25 || Math.Abs(b.Memory - a.Memory) >= 2L * 1024 * 1024 || b.Journal > a.Journal) busy = b.At;
            if (b.Files - a.Files >= 64L * 1024) files = b.At;
        }
        var last = samples[samples.Count - 1];
        var from = samples.LastOrDefault(x => last.At - x.At >= TimeSpan.FromSeconds(30)) ?? samples[0];
        var span = Math.Max(1e-6, (last.At - from.At).TotalSeconds);
        var cpu = (last.Cpu - from.Cpu).TotalSeconds / span / cores * 100;
        var any = new[] { busy, files }.Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(samples[0].At).Max();
        var quiet = now - any;
        var progress = busy is { } w && now - w <= TimeSpan.FromSeconds(10) ? Progress.Working
            : files is { } f && now - f <= TimeSpan.FromSeconds(10) ? Progress.Files
            : quiet >= StuckAfter ? Progress.Stuck : Progress.Quiet;
        return new Activity(progress, Math.Round(Math.Max(0, cpu), 1), last.Memory, last.Memory - from.Memory, Math.Max(0, last.Files - from.Files), quiet);
    }

    /// <summary>The verdict in a few words, for the window's coloured line.</summary>
    public static string Verdict(Activity a) => a.Progress switch
    {
        Progress.Working => "Working",
        Progress.Files => "Working on files",
        Progress.Quiet => "No activity for " + Duration(a.Quiet),
        _ => "Looks stuck",
    };

    /// <summary>What to do, for the verdict (and what Revit is busy with).</summary>
    public static string Advice(Activity a, Busy busy) => a.Progress switch
    {
        Progress.Working => "Revit is using the processor and memory is changing, so it is still working. Waiting usually pays off.",
        Progress.Files => "Revit is reading or writing files (the model, links or the central model). Wait while this goes on.",
        Progress.Quiet => $"If nothing changes for {Duration(StuckAfter)}, Revit is most likely stuck.",
        _ when busy.What == "Synchronizing with central" =>
            $"Nothing has changed for {Duration(a.Quiet)}. Revit may be waiting for the central model on the network. Give it a few more minutes before closing it.",
        _ => $"Nothing has changed for {Duration(a.Quiet)}, so waiting is unlikely to help. Try Press Esc in Revit; if that does nothing, close Revit (Lifeline keeps your changes).",
    };

    /// <summary>"CPU 6% · memory 9.1 GB (+420 MB in 30 s) · files 4 MB in 30 s".</summary>
    public static string Figures(Activity a)
    {
        var parts = new List<string> { $"CPU {a.CpuPercent.ToString("0.#", CultureInfo.InvariantCulture)}%" };
        if (a.Memory > 0)
            parts.Add($"memory {Size(a.Memory)}" + (Math.Abs(a.MemoryChange) >= 1024 * 1024 ? $" ({(a.MemoryChange > 0 ? "+" : "−")}{Size(Math.Abs(a.MemoryChange))} in 30 s)" : ""));
        if (a.FilesBytes >= 64 * 1024) parts.Add($"files {Size(a.FilesBytes)} in 30 s");
        return string.Join(" · ", parts);
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : bytes >= 1L << 20 ? (bytes >> 20).ToString(CultureInfo.InvariantCulture) + " MB"
        : (bytes >> 10).ToString(CultureInfo.InvariantCulture) + " KB";

    // ---- Commands to warn about before they run -------------------------------------------------------------------

    /// <param name="Id">Revit's command id.</param>
    /// <param name="Name">The command as Revit names it.</param>
    /// <param name="Seconds">The longest freeze seen for it on this PC (0 = none seen yet, a built-in entry).</param>
    /// <param name="On">Date of that freeze ("27 Oct"), or "".</param>
    public sealed record Warned(string Id, string Name, int Seconds, string On);

    /// <summary>Warned about from the start: Revit itself says it won't respond while these run on a big model.</summary>
    public static readonly Warned[] BuiltIn =
    [
        // Seen freezing a port model with links on 27 Oct 2026 (it builds an energy analytical model first).
        new("ID_RUN_SYSTEMS_ANALYSIS", "Run systems analysis", 0, ""),
    ];

    public static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "freeze-warnings.json");

    internal static string? FileForTest;

    /// <summary>2: commands learned before it may have been learned from Sketch ProMax's own freezes, and are dropped.</summary>
    private const int Version = 2;

    private sealed class Saved
    {
        public int Version { get; set; }
        public List<Warned> Warn { get; set; } = [];
        public List<string> Off { get; set; } = [];
    }

    private static readonly object Gate = new();

    /// <summary>The commands to warn about: the built-in ones (unless turned off) and the ones learned from freezes.</summary>
    public static List<Warned> Load()
    {
        var saved = Read();
        var list = new List<Warned>();
        foreach (var w in saved.Warn) if (!list.Any(x => x.Id == w.Id)) list.Add(w);
        foreach (var b in BuiltIn) if (!saved.Off.Contains(b.Id) && !list.Any(x => x.Id == b.Id)) list.Add(b);
        return list;
    }

    /// <summary>Adds (or updates, keeping the longest freeze) a command to warn about. Any thread.</summary>
    public static void Learn(string id, string name, TimeSpan frozen, DateTime when)
    {
        lock (Gate)
        {
            var saved = Read();
            saved.Off.Remove(id);
            var old = saved.Warn.FirstOrDefault(w => w.Id == id);
            var seconds = (int)frozen.TotalSeconds;
            if (old is not null)
            {
                if (old.Seconds >= seconds) { Write(saved); return; }
                saved.Warn.Remove(old);
            }
            saved.Warn.Add(new Warned(id, name, seconds, when.ToString("d MMM", CultureInfo.InvariantCulture)));
            Write(saved);
        }
    }

    /// <summary>Stops warning about a command (the warning's "Don't warn me again").</summary>
    public static void Forget(string id)
    {
        lock (Gate)
        {
            var saved = Read();
            saved.Warn.RemoveAll(w => w.Id == id);
            if (BuiltIn.Any(b => b.Id == id) && !saved.Off.Contains(id)) saved.Off.Add(id);
            Write(saved);
        }
    }

    /// <summary>The warning shown before the command runs: (main line, details).</summary>
    public static (string Main, string Content) Warning(Warned w) =>
        ($"{w.Name} can freeze Revit for a long time",
         (w.Seconds > 0
             ? $"On {w.On} it kept Revit frozen for {Duration(TimeSpan.FromSeconds(w.Seconds))}."
             : "Revit doesn't respond while it runs, and on a big model with links that can take a long time.") +
         " Save your work first. If Revit freezes, the Sketch ProMax Rescue window lets you stop or close it safely.");

    private static Saved Read()
    {
        try
        {
            var text = SafeFile.Read(FileForTest ?? File);
            var saved = text is null ? null : JsonSerializer.Deserialize<Saved>(text);
            if (saved is null) return new Saved { Version = Version };
            if (saved.Version < Version) { saved.Warn.Clear(); saved.Version = Version; }
            return saved;
        }
        catch (Exception) { return new Saved { Version = Version }; }
    }

    private static void Write(Saved saved)
    {
        try { SafeFile.Write(FileForTest ?? File, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception) { /* the warning is a convenience */ }
    }
}
