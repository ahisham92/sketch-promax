using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Every model change the chat made that worked, with its full arguments (the code too), kept per model so the same
/// work can be done again exactly: after Revit closed before the model was saved, or on a fresh copy (2026-10-29: "I
/// finalize a task, the model crashes, and I want it redone exactly"). The activity log cuts arguments at 4,000
/// characters, which loses most code; this keeps them whole. One JSON line per call in
/// %APPDATA%\SketchProMax\redo\&lt;model&gt;.jsonl, the newest 3,000 kept. No Revit here, so the tests can run it.
/// </summary>
internal static class RedoLog
{
    public sealed record Step(int Number, DateTime Time, string Tool, string Arguments, string Result);

    /// <summary>A run of steps with no gap over <see cref="TaskGap"/>: one piece of work the user asked for.</summary>
    public sealed record Chunk(int First, int Last, DateTime Start, DateTime End, List<Step> Steps);

    public static readonly TimeSpan TaskGap = TimeSpan.FromMinutes(15);
    private const int Keep = 3000, MaxArguments = 400_000, MaxResult = 2000;
    private static readonly object Gate = new();

    /// <summary>Calls that are not model work to redo (bookkeeping, sync, the redo itself).</summary>
    public static readonly HashSet<string> NotRedone = new(StringComparer.Ordinal)
    {
        "redo_steps", "auto_sync", "save_model", "reload_links", "relinquish_mine", "borrow_elements", "make_worksets_editable",
        "memory_note", "chat_jobs", "work_progress", "berth_status", "berth_costing", "report_bug", "save_recipe", "save_as_tool",
        "delete_learned_tool", "model_snapshot", "coordination_progress", "use_revit_model", "cancel_job", "wait_for_job",
    };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "redo");

    /// <summary>The file for a model title ("Tower_jsmith" and "Tower" each have their own).</summary>
    public static string FileFor(string model)
    {
        var safe = new string(model.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return Path.Combine(Folder, (safe.Length == 0 ? "unnamed" : safe) + ".jsonl");
    }

    public static string Line(DateTime time, string tool, string arguments, string result) => JsonSerializer.Serialize(new
    {
        time = time.ToString("s"),
        tool,
        arguments = arguments.Length > MaxArguments ? "" : arguments,
        result = result.Length > MaxResult ? result.Substring(0, MaxResult) : result,
    });

    /// <summary>Adds a change that worked. Never throws: the log must not break a tool call.</summary>
    public static void Write(string model, string tool, string arguments, string result)
    {
        if (NotRedone.Contains(tool) || arguments.Length > MaxArguments) return;
        try
        {
            var file = FileFor(model);
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(file, Line(DateTime.Now, tool, arguments, result) + Environment.NewLine, new UTF8Encoding(false));
                // Trimmed now and then, not on every write.
                if (new FileInfo(file).Length > 4_000_000)
                {
                    var lines = File.ReadAllLines(file);
                    if (lines.Length > Keep) SafeFile.Write(file, string.Join(Environment.NewLine, lines.Skip(lines.Length - Keep)) + Environment.NewLine);
                }
            }
        }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>The steps in a file's text, numbered from 1 in file order; unreadable lines are skipped but keep their number.</summary>
    public static List<Step> Parse(string text)
    {
        var steps = new List<Step>();
        var n = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            n++;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;
                var args = r.GetProperty("arguments").GetString() ?? "";
                if (args.Length == 0) continue;
                steps.Add(new Step(n, DateTime.Parse(r.GetProperty("time").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    r.GetProperty("tool").GetString() ?? "", args, r.TryGetProperty("result", out var res) ? res.GetString() ?? "" : ""));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { }
        }
        return steps;
    }

    public static List<Step> Read(string model)
    {
        lock (Gate)
        {
            try { return File.Exists(FileFor(model)) ? Parse(File.ReadAllText(FileFor(model))) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>The models with a redo log, newest first.</summary>
    public static List<(string Model, DateTime Last)> Models()
    {
        try
        {
            if (!Directory.Exists(Folder)) return [];
            return Directory.GetFiles(Folder, "*.jsonl").Select(f => (Path.GetFileNameWithoutExtension(f), File.GetLastWriteTime(f)))
                .OrderByDescending(x => x.Item2).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Steps grouped into tasks by the gaps between them.</summary>
    public static List<Chunk> Tasks(List<Step> steps)
    {
        var tasks = new List<Chunk>();
        List<Step>? current = null;
        foreach (var s in steps.OrderBy(s => s.Number))
        {
            if (current is null || s.Time - current[current.Count - 1].Time > TaskGap)
            {
                current = [];
                tasks.Add(new Chunk(0, 0, s.Time, s.Time, current));
            }
            current.Add(s);
        }
        return tasks.Select(t => t with { First = t.Steps[0].Number, Last = t.Steps[t.Steps.Count - 1].Number, End = t.Steps[t.Steps.Count - 1].Time }).ToList();
    }

    /// <summary>A short line for a step: the code's own description, else the tool and its first arguments.</summary>
    public static string Describe(Step s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s.Arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString()!;
                var parts = doc.RootElement.EnumerateObject().Where(p => p.Name != "code").Take(3)
                    .Select(p => $"{p.Name}={Short(p.Value.ToString())}");
                return string.Join(", ", parts);
            }
        }
        catch (JsonException) { }
        return Short(s.Arguments);
    }

    private static string Short(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;

    private static readonly Regex Number = new(@"(?<![\d.])\d{4,}(?![\d.])");

    /// <summary>Whole numbers of 4 digits or more in a text, in order, each once: candidate element ids.</summary>
    public static List<long> Ids(string text) =>
        Number.Matches(text).Cast<Match>().Select(m => long.TryParse(m.Value, out var v) ? v : -1).Where(v => v > 0).Distinct().ToList();

    /// <summary>
    /// Writes each old id in <paramref name="map"/> as its new id: element ids the lost work made, which the redo made
    /// again under new ids, so a later step (code included, "new ElementId(5475707L)") acts on the new elements.
    /// </summary>
    public static string Remap(string arguments, IReadOnlyDictionary<long, long> map)
    {
        if (map.Count == 0) return arguments;
        return Number.Replace(arguments, m => long.TryParse(m.Value, out var v) && map.TryGetValue(v, out var to) ? to.ToString() : m.Value);
    }
}
