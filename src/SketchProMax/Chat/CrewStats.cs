using System.IO;
using System.Text.Json;

namespace SketchProMax.Chat;

/// <summary>
/// One piece of work the crew did, kept for its numbers (CrewStats): a worker's run of a job, a turn of the coordinator
/// (the main chat With coordinator) or a change the applier put into Revit; when, which chat and job, the tokens in and
/// out, the API-equivalent dollars (what Claude Code reports; the plan covers it) and whether it went well.
/// </summary>
internal sealed class CrewRecord
{
    public const string Worker = "worker", Coordinator = "coordinator", Applier = "applier";

    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Role { get; set; } = Worker;
    /// <summary>The worker's desk (0: the first name in Settings > Chat); -1 for the coordinator and the applier.</summary>
    public int Slot { get; set; } = -1;
    public string Chat { get; set; } = "";
    public int Job { get; set; }
    public long In { get; set; }
    public long Out { get; set; }
    public double Usd { get; set; }
    public bool Ok { get; set; } = true;
    /// <summary>A request in a Simple chat, answered by the worker the chat talks to directly (no coordinator handed it out).</summary>
    public bool Direct { get; set; }

    public double Seconds => Math.Max(0, (End - Start).TotalSeconds);
}

/// <summary>A worker's numbers for a period: jobs, how many went well, time, tokens and dollars.</summary>
internal sealed record WorkerKpi(int Slot, int Jobs, int Done, double Seconds, long In, long Out, double Usd)
{
    public double MinutesPerJob => Jobs == 0 ? 0 : Seconds / 60 / Jobs;
}

/// <summary>
/// The crew's numbers across every chat (crew-stats.json next to the settings): what each worker, the coordinator and
/// the applier did, with tokens, dollars and time, and the time saved by working side by side. Read once, kept in memory,
/// written after each record. The sums here are plain functions, tested.
/// </summary>
internal static class CrewStats
{
    private static readonly object Gate = new();
    private static List<CrewRecord>? _records;
    private const int Keep = 5000;

    /// <summary>For tests: another file.</summary>
    internal static string? PathOverride;

    private static string FilePath => PathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "crew-stats.json");

    /// <summary>Raised after a record is added (any thread).</summary>
    public static event Action? Changed;

    /// <summary>Every record (a copy).</summary>
    public static List<CrewRecord> All()
    {
        lock (Gate) return Loaded().ToList();
    }

    private static List<CrewRecord> Loaded()
    {
        if (_records is not null) return _records;
        try
        {
            _records = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<CrewRecord>>(File.ReadAllText(FilePath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _records = []; }
        return _records;
    }

    /// <summary>Adds a record and writes the file (a failed write keeps it in memory for this session).</summary>
    public static void Add(CrewRecord record)
    {
        string json;
        lock (Gate)
        {
            var list = Loaded();
            list.Add(record);
            if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
            json = JsonSerializer.Serialize(list);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    /// <summary>For tests: forget what was read.</summary>
    internal static void Reset()
    {
        lock (Gate) _records = null;
    }

    /// <summary>
    /// The dollars of one answer: Claude Code reports the total of its process so far, so this is the rise since
    /// <paramref name="seen"/> (or all of it in a new process, whose total starts again).
    /// </summary>
    public static double Rise(ref double seen, double total)
    {
        var rise = total >= seen ? total - seen : total;
        seen = total;
        return Math.Max(0, rise);
    }

    /// <summary>The records since <paramref name="from"/> (null: all).</summary>
    public static List<CrewRecord> Since(IEnumerable<CrewRecord> records, DateTime? from) => records.Where(r => from is null || r.End >= from).ToList();

    /// <summary>Each worker's numbers, the busiest first. A job counts once however many runs it took.</summary>
    public static List<WorkerKpi> Workers(IEnumerable<CrewRecord> records) =>
        records.Where(r => r.Role == CrewRecord.Worker && r.Slot >= 0).GroupBy(r => r.Slot).Select(g =>
        {
            var jobs = g.GroupBy(r => (r.Chat, r.Job)).ToList();
            return new WorkerKpi(g.Key, jobs.Count, jobs.Count(j => j.OrderBy(r => r.End).Last().Ok), g.Sum(r => r.Seconds), g.Sum(r => r.In), g.Sum(r => r.Out), g.Sum(r => r.Usd));
        }).OrderByDescending(k => k.Done).ThenBy(k => k.Slot).ToList();

    /// <summary>
    /// The workers' time: all their working time added up, and the time saved because they worked side by side (that
    /// sum less the time at least one of them was working).
    /// </summary>
    public static (double Used, double Saved) Time(IEnumerable<CrewRecord> records)
    {
        var runs = records.Where(r => r.Role == CrewRecord.Worker && r.End > r.Start).OrderBy(r => r.Start).ToList();
        var used = runs.Sum(r => r.Seconds);
        double wall = 0;
        DateTime? from = null, to = null;
        foreach (var r in runs)
        {
            if (from is null || r.Start > to)
            {
                if (from is not null) wall += (to!.Value - from.Value).TotalSeconds;
                from = r.Start;
                to = r.End;
            }
            else if (r.End > to) to = r.End;
        }
        if (from is not null) wall += (to!.Value - from.Value).TotalSeconds;
        return (used, Math.Max(0, used - wall));
    }

    /// <summary>The coordinator's numbers: turns, time, tokens, dollars, and the jobs it handed out in the same period.</summary>
    public static (int Turns, double Seconds, long In, long Out, double Usd, int JobsHandedOut) CoordinatorKpi(IEnumerable<CrewRecord> records)
    {
        var list = records.ToList();
        var turns = list.Where(r => r.Role == CrewRecord.Coordinator).ToList();
        var jobs = list.Where(r => r.Role == CrewRecord.Worker && !r.Direct).Select(r => (r.Chat, r.Job)).Distinct().Count();
        return (turns.Count, turns.Sum(r => r.Seconds), turns.Sum(r => r.In), turns.Sum(r => r.Out), turns.Sum(r => r.Usd), jobs);
    }

    /// <summary>The applier's numbers: changes put into Revit and the time they took.</summary>
    public static (int Changes, double Seconds) ApplierKpi(IEnumerable<CrewRecord> records)
    {
        var changes = records.Where(r => r.Role == CrewRecord.Applier).ToList();
        return (changes.Count, changes.Sum(r => r.Seconds));
    }

    /// <summary>The worker with the most jobs done well in <paramref name="records"/> (-1 when none, or a tie at the top).</summary>
    public static int Leader(IEnumerable<CrewRecord> records)
    {
        var kpis = Workers(records);
        if (kpis.Count == 0 || kpis[0].Done == 0) return -1;
        return kpis.Count > 1 && kpis[1].Done == kpis[0].Done ? -1 : kpis[0].Slot;
    }

    /// <summary>
    /// The worker a new Simple chat talks to: of the first <paramref name="workers"/>, the one who worked least recently
    /// (never first, then the first desk), so the team takes turns.
    /// </summary>
    public static int NextWorker(IEnumerable<CrewRecord> records, int workers)
    {
        workers = Math.Max(1, workers);
        var last = records.Where(r => r.Role == CrewRecord.Worker && r.Slot >= 0 && r.Slot < workers)
            .GroupBy(r => r.Slot).ToDictionary(g => g.Key, g => g.Max(r => r.End));
        return Enumerable.Range(0, workers).OrderBy(s => last.TryGetValue(s, out var t) ? t : DateTime.MinValue).ThenBy(s => s).First();
    }

    /// <summary>"1.2k", "34k", "2.1M" tokens.</summary>
    public static string Tokens(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"$0.42".</summary>
    public static string Dollars(double usd) => "$" + usd.ToString(usd >= 100 ? "0" : "0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"45 s", "12 min", "2 h 5 min".</summary>
    public static string Duration(double seconds)
    {
        if (seconds < 60) return $"{Math.Round(seconds)} s";
        var minutes = (int)Math.Round(seconds / 60);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }
}
