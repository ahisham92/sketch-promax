namespace SketchProMax.Chat;

/// <summary>
/// How a worker in the crew strip looks: thinking or reading the model at its own desk (several at once), applying a
/// change at the one Revit station (one at a time), waiting its turn for it, or just finished.
/// </summary>
internal enum CrewMood { Thinking, Reading, Applying, Waiting, Resting, Done, Failed }

/// <summary>Something the strip acts out once: a worker sits down, gets a new sheet from the coordinator, or leaves.</summary>
internal enum CrewCue { Arrive, Handover, Leave }

/// <summary>
/// A worker at work: the job, its mood, which worker has it (<paramref name="Slot"/>: the n-th name, its own desk) and
/// its place in the line at the applier (<paramref name="Line"/>: 0 applying now, 1 next, …; -1 at the desk).
/// </summary>
internal sealed record CrewSeat(int Number, string Title, CrewMood Mood, int Slot = 0, int Line = -1);

/// <summary>
/// The names in the crew strip, from Settings > Chat: you, the coordinator, the workers (one per job that runs at once,
/// in order; more jobs than names get "Worker 4", …) and the one who applies changes in Revit.
/// </summary>
internal sealed record CrewNames(string You, string Coordinator, IReadOnlyList<string> Workers, string Applier)
{
    public const string DefaultCoordinator = "Pitso", DefaultWorkers = "Ali, Carmen, Carla", DefaultApplier = "Dieng";

    /// <summary>The settings' names, each blank one replaced by its default (you: the first part of the Windows account name).</summary>
    public static CrewNames From(string? you, string? coordinator, string? workers, string? applier, string? account)
    {
        static string Clean(string? name, string fallback)
        {
            var t = (name ?? "").Trim();
            return t.Length == 0 ? fallback : t.Length > 24 ? t.Substring(0, 24).TrimEnd() : t;
        }
        var list = (string.IsNullOrWhiteSpace(workers) ? DefaultWorkers : workers!)
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(w => Clean(w, "")).Where(w => w.Length > 0).Take(12).ToList();
        if (list.Count == 0) list = DefaultWorkers.Split(',').Select(w => w.Trim()).ToList();
        return new CrewNames(Clean(you, FromAccount(account)), Clean(coordinator, DefaultCoordinator), list, Clean(applier, DefaultApplier));
    }

    /// <summary>"jane.doe" or "DOMAIN\\jdoe" → "Jane" / "Jdoe"; "You" when there is none.</summary>
    public static string FromAccount(string? account)
    {
        var name = (account ?? "").Split('\\').Last().Split(['.', '_', '-', ' ', '@'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (name.Length == 0 || !name.Any(char.IsLetter)) return "You";
        return char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();
    }

    /// <summary>The worker at desk <paramref name="slot"/> (from 0).</summary>
    public string Worker(int slot) => slot >= 0 && slot < Workers.Count ? Workers[slot] : $"Worker {slot + 1}";

    public static CrewNames Defaults { get; } = From(null, null, null, null, null);
}

internal sealed record CrewEvent(CrewCue Cue, int Number);

/// <summary>
/// The crew strip's rules, apart from its drawing (CrewStrip): which jobs have a worker at a desk and what just happened
/// to them. A job that runs or waits its turn has a worker; a job waiting to start has none yet (it is hidden). A
/// worker arrives when its job starts, gets a sheet each time the job is sent a new message (staying seated while a
/// finished job it belongs to waits to start again), and stays a few seconds
/// after the job is done (or ended with a problem) before leaving; a stopped job's worker leaves at once.
/// </summary>
internal sealed class CrewPlan
{
    /// <summary>How long a finished job's worker stays at the desk, its screen showing the result, before leaving.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(4);

    private readonly Dictionary<int, int> _asked = new();
    private readonly Dictionary<int, DateTime> _leaving = new();
    private readonly Dictionary<int, int> _slots = new();
    private List<CrewSeat> _seats = [];
    private bool _started;

    /// <summary>The workers now, in job order.</summary>
    public IReadOnlyList<CrewSeat> Seats => _seats;

    /// <summary>True while a finished job's worker is still at the desk (the pane checks back after <see cref="Linger"/>).</summary>
    public bool Lingering => _leaving.Count > 0;

    /// <summary>
    /// Takes the jobs as they are now and returns what changed. The first call only seats who is already working (a
    /// chat opened, or the large window shown), without acting anything out. <paramref name="activity"/> says what a
    /// running job does now (JobBoard.ActivityOf: thinking, reading, waiting or applying; empty reads as thinking).
    /// </summary>
    public List<CrewEvent> Update(IEnumerable<ChatJob> jobs, DateTime now, Func<ChatJob, string>? activity = null)
    {
        var events = new List<CrewEvent>();
        var quiet = !_started;
        _started = true;
        var seated = _seats.ToDictionary(s => s.Number);
        var next = new List<CrewSeat>();
        var ahead = new Dictionary<int, int>();
        foreach (var job in jobs.OrderBy(j => j.Number))
        {
            var asked = Asked(job);
            var was = seated.TryGetValue(job.Number, out var seat);
            CrewMood? mood = job.State switch
            {
                JobBoard.Running or JobBoard.Waiting => Mood(job, activity),
                // Sent a message after it finished: the worker stays at the desk until a place is free to start again.
                JobBoard.Queued when was => CrewMood.Resting,
                JobBoard.Done when was => CrewMood.Done,
                JobBoard.Failed when was => CrewMood.Failed,
                _ => null,
            };
            if (mood is CrewMood.Done or CrewMood.Failed)
            {
                if (!_leaving.TryGetValue(job.Number, out var since)) _leaving[job.Number] = since = now;
                if (now - since >= Linger) mood = null;
            }
            else _leaving.Remove(job.Number);
            if (mood is not { } m)
            {
                if (was && !quiet) events.Add(new CrewEvent(CrewCue.Leave, job.Number));
                seated.Remove(job.Number);
                _leaving.Remove(job.Number);
                _asked.Remove(job.Number);
                _slots.Remove(job.Number);
                continue;
            }
            if (!quiet)
            {
                if (!was) events.Add(new CrewEvent(CrewCue.Arrive, job.Number));
                else if (_asked.TryGetValue(job.Number, out var before) && asked > before && m is not (CrewMood.Done or CrewMood.Failed))
                    events.Add(new CrewEvent(CrewCue.Handover, job.Number));
            }
            _asked[job.Number] = asked;
            if (!_slots.TryGetValue(job.Number, out var slot))
            {
                // The first worker without a job takes it, and keeps it (and the desk) until the job's worker leaves.
                slot = 0;
                while (_slots.ContainsValue(slot)) slot++;
                _slots[job.Number] = slot;
            }
            next.Add(new CrewSeat(job.Number, job.Title, m, slot));
            ahead[job.Number] = job.Ahead;
            seated.Remove(job.Number);
        }
        // A job gone from the board altogether (a chat cleared): its worker leaves too.
        foreach (var gone in seated.Keys)
        {
            if (!quiet) events.Add(new CrewEvent(CrewCue.Leave, gone));
            _leaving.Remove(gone);
            _asked.Remove(gone);
            _slots.Remove(gone);
        }
        // The line at the applier: the change being applied first, then the others in the order they asked.
        var line = next.Where(s => s.Mood == CrewMood.Applying)
            .Concat(next.Where(s => s.Mood == CrewMood.Waiting).OrderBy(s => ahead[s.Number] <= 0 ? int.MaxValue : ahead[s.Number]).ThenBy(s => s.Number))
            .Select(s => s.Number).ToList();
        _seats = next.Select(s => line.IndexOf(s.Number) is var at and >= 0 ? s with { Line = at } : s).ToList();
        return events;
    }

    /// <summary>Starts over: the next update seats who is working without acting anything out.</summary>
    public void Reset()
    {
        _asked.Clear();
        _leaving.Clear();
        _slots.Clear();
        _seats = [];
        _started = false;
    }

    /// <summary>A running job's mood from what it does now.</summary>
    internal static CrewMood Mood(ChatJob job, Func<ChatJob, string>? activity) => (activity?.Invoke(job) ?? "") switch
    {
        "applying" => CrewMood.Applying,
        "waiting" => CrewMood.Waiting,
        "reading" => CrewMood.Reading,
        "thinking" => CrewMood.Thinking,
        _ => job.State == JobBoard.Waiting ? CrewMood.Waiting : CrewMood.Thinking,
    };

    /// <summary>How many messages the job has been given (its task and every message after it).</summary>
    private static int Asked(ChatJob job) => job.Items.Count(i => i.Kind == "user");

    /// <summary>The line over the strip: who is doing what, in a few words, by name.</summary>
    public static string Caption(IReadOnlyList<CrewSeat> seats, CrewNames names)
    {
        if (seats.Count == 0) return $"{names.Coordinator} is ready";
        var applying = seats.FirstOrDefault(s => s.Mood == CrewMood.Applying);
        var thinking = seats.Count(s => s.Mood == CrewMood.Thinking);
        var reading = seats.Count(s => s.Mood == CrewMood.Reading);
        var waiting = seats.Count(s => s.Mood == CrewMood.Waiting);
        var done = seats.Count(s => s.Mood is CrewMood.Done or CrewMood.Failed);
        var parts = new List<string>();
        if (applying is not null) parts.Add($"{names.Applier} applies {names.Worker(applying.Slot)}'s change");
        if (thinking > 0) parts.Add($"{thinking} thinking");
        if (reading > 0) parts.Add($"{reading} reading the model");
        if (waiting > 0) parts.Add($"{waiting} in line");
        if (done > 0) parts.Add(done == 1 ? "1 just finished" : $"{done} just finished");
        return string.Join(" · ", parts);
    }

    /// <summary>The worker who has job <paramref name="number"/> now, or null when none has it.</summary>
    public string? WorkerOf(int number, CrewNames names) => _slots.TryGetValue(number, out var slot) ? names.Worker(slot) : null;
}
