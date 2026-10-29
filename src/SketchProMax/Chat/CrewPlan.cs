using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// How a worker in the crew strip looks: thinking or reading the model at its own desk (several at once), applying a
/// change at the one Revit station (one at a time), waiting its turn for it, or just finished.
/// </summary>
internal enum CrewMood { Thinking, Reading, Applying, Waiting, Resting, Done, Failed }

/// <summary>
/// Something the strip acts out once: a worker sits down, gets a new sheet from the coordinator, or leaves; and the
/// moments a worker says something short (a bubble): the plan is made, its turn at Revit came, the job finished.
/// </summary>
internal enum CrewCue { Arrive, Handover, Leave, Plan, Turn, Finish, Huddle, Share }

/// <summary>
/// A worker at work: the job, its mood, which worker has it (<paramref name="Slot"/>: the n-th name, its own desk) and
/// its place in the line at the applier (<paramref name="Line"/>: 0 applying now, 1 next, …; -1 at the desk), and how
/// far its plan is (<paramref name="Percent"/>, with <paramref name="Left"/> steps to go; -1 while it has no plan).
/// </summary>
internal sealed record CrewSeat(int Number, string Title, CrewMood Mood, int Slot = 0, int Line = -1, int Percent = -1, int Left = -1, int Steps = 0);

/// <summary>
/// The names in the crew strip, from Settings > Chat: you, the coordinator, the workers (one per job that runs at once,
/// in order; more jobs than names get "Worker 4", …) and the one who applies changes in Revit.
/// </summary>
internal sealed record CrewNames(string You, string Coordinator, IReadOnlyList<string> Workers, string Applier)
{
    public const string DefaultCoordinator = "Atlas", DefaultWorkers = "Nova, Echo, Pixel", DefaultApplier = "Bolt";

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

/// <summary>
/// Something to act out for job <paramref name="Number"/>, with what its worker says (<paramref name="Say"/>, "" for
/// nothing). A huddle is the coordinator's (Number 0): several jobs handed out at once. A share is a call from the worker
/// at desk <paramref name="From"/> (whose job finished) passing what it found to job <paramref name="Number"/>.
/// </summary>
internal sealed record CrewEvent(CrewCue Cue, int Number, string Say = "", int From = -1);

/// <summary>
/// A job's plan, as its Claude Code keeps it: the steps it wrote with TodoWrite (the whole list each time) or with
/// TaskCreate and TaskUpdate (one step each), by id, with each one's state (pending, in_progress or completed). The
/// crew strip shows how far along it is from this (60% · 2 left).
/// </summary>
internal static class JobPlan
{
    public const string ToDo = "pending", Doing = "in_progress", Finished = "completed";

    /// <summary>The Claude Code tools a job keeps its plan with.</summary>
    public const string Tools = "TodoWrite,TaskCreate,TaskUpdate";

    public static bool IsPlanTool(string tool) => tool is "TodoWrite" or "TaskCreate" or "TaskUpdate";

    /// <summary>Takes a plan tool's call (its input, and the result it gave) into <paramref name="plan"/>; true when it changed.</summary>
    public static bool Apply(Dictionary<string, string> plan, string tool, string input, string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(input) ? "{}" : input);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            switch (tool)
            {
                case "TodoWrite":
                    if (!root.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array) return false;
                    plan.Clear();
                    var i = 0;
                    foreach (var todo in todos.EnumerateArray())
                        plan["t" + (i++).ToString(System.Globalization.CultureInfo.InvariantCulture)] = State(todo) ?? ToDo;
                    return true;
                case "TaskCreate":
                    // "Task #3 created …": its id; else the next number.
                    var id = Regex.Match(result ?? "", @"#(\d+)") is { Success: true } m ? m.Groups[1].Value : (plan.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    plan[id] = ToDo;
                    return true;
                case "TaskUpdate":
                    var key = Text(root, "taskId") ?? Text(root, "id");
                    var state = State(root);
                    if (key is null || state is null) return false;
                    if (state == "deleted") return plan.Remove(key);
                    plan[key] = state;
                    return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static string? State(JsonElement e) => Text(e, "status");

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    /// <summary>Steps done, and in all.</summary>
    public static (int Done, int Total) Count(IReadOnlyDictionary<string, string>? plan) =>
        plan is null ? (0, 0) : (plan.Values.Count(v => v == Finished), plan.Count);

    /// <summary>
    /// How far along, 0-100 (a step under way counts half); -1 without a plan. A finished job is 100; one still at work
    /// says 99 at most, even with every step ticked.
    /// </summary>
    public static int Percent(IReadOnlyDictionary<string, string>? plan, bool finished)
    {
        if (finished) return 100;
        if (plan is null || plan.Count == 0) return -1;
        var doing = plan.Values.Count(v => v == Doing);
        var (done, total) = Count(plan);
        return Math.Min(99, (int)Math.Round(100.0 * (done + 0.5 * doing) / total));
    }

    /// <summary>"40% · 3 left" for a job at work with a plan; "" otherwise (for its card, pill and the job list).</summary>
    public static string Progress(ChatJob job)
    {
        if (!job.Active) return "";
        var percent = Percent(job.Plan, finished: false);
        if (percent < 0) return "";
        var (done, total) = Count(job.Plan);
        return total - done > 0 ? $"{percent}% · {total - done} left" : $"{percent}%";
    }

    /// <summary>True when every step is done (a new run then starts a new plan).</summary>
    public static bool AllDone(IReadOnlyDictionary<string, string>? plan) => plan is { Count: > 0 } && plan.Values.All(v => v == Finished);
}

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
    private readonly Dictionary<int, int> _planned = new();
    private readonly Dictionary<int, int> _heard = new();
    /// <summary>When each worker was last given a job: the next job goes to the free worker who waited longest.</summary>
    private readonly Dictionary<int, DateTime> _given = new();
    private List<CrewSeat> _seats = [];
    private bool _started;

    /// <summary>The workers now, in job order.</summary>
    public IReadOnlyList<CrewSeat> Seats => _seats;

    /// <summary>True while a finished job's worker is still at the desk (the pane checks back after <see cref="Linger"/>).</summary>
    public bool Lingering => _leaving.Count > 0;

    /// <summary>Jobs waiting for a free worker (they wait on the coordinator's desk).</summary>
    public int Unassigned { get; private set; }

    /// <summary>
    /// Takes the jobs as they are now and returns what changed. The first call only seats who is already working (a
    /// chat opened, or the strip shown), without acting anything out. <paramref name="activity"/> says what a running job
    /// does now (JobBoard.ActivityOf: thinking, reading, waiting or applying; empty reads as thinking).
    /// <paramref name="workers"/> is how many workers there are (Settings: jobs at once): each takes one job at a time, a
    /// job keeps the worker it had (ChatJob.Worker) when that one is free, and a new job may take the desk of a worker
    /// still showing a finished job (who leaves at once).
    /// </summary>
    public List<CrewEvent> Update(IEnumerable<ChatJob> jobs, DateTime now, Func<ChatJob, string>? activity = null, int workers = int.MaxValue)
    {
        var events = new List<CrewEvent>();
        var quiet = !_started;
        _started = true;
        var seated = _seats.ToDictionary(s => s.Number);
        var before = _seats.ToDictionary(s => s.Number, s => s.Mood);
        var all = jobs.ToDictionary(j => j.Number);
        var next = new List<(ChatJob Job, CrewMood Mood)>();
        var unassigned = 0;
        foreach (var job in jobs.OrderBy(j => j.Number))
        {
            var asked = Asked(job);
            var was = seated.TryGetValue(job.Number, out _);
            CrewMood? mood = job.State switch
            {
                JobBoard.Running or JobBoard.Waiting => Mood(job, activity),
                // Sent a message after it finished: the worker stays at the desk until a place is free to start again.
                JobBoard.Queued when was => CrewMood.Resting,
                JobBoard.Done when was => CrewMood.Done,
                JobBoard.Failed when was => CrewMood.Failed,
                _ => null,
            };
            if (job.State == JobBoard.Queued && !was) unassigned++;
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
                Forget(job.Number);
                continue;
            }
            var steps = job.Plan.Count;
            if (!quiet)
            {
                var finished = m is CrewMood.Done or CrewMood.Failed;
                if (!was) events.Add(new CrewEvent(CrewCue.Arrive, job.Number, Say(CrewCue.Arrive, job)));
                else if (_asked.TryGetValue(job.Number, out var earlier) && asked > earlier && !finished)
                    events.Add(new CrewEvent(CrewCue.Handover, job.Number, Say(CrewCue.Handover, job)));
                else if (finished && before.TryGetValue(job.Number, out var had) && had is not (CrewMood.Done or CrewMood.Failed))
                    events.Add(new CrewEvent(CrewCue.Finish, job.Number, Say(CrewCue.Finish, job)));
                else if (m == CrewMood.Applying && before.TryGetValue(job.Number, out var had2) && had2 != CrewMood.Applying)
                    events.Add(new CrewEvent(CrewCue.Turn, job.Number, Say(CrewCue.Turn, job)));
                else if (steps > 0 && _planned.TryGetValue(job.Number, out var known) && known == 0)
                    events.Add(new CrewEvent(CrewCue.Plan, job.Number, Say(CrewCue.Plan, job)));
            }
            // It was given another job's report: a call between the two desks.
            var heardBefore = _heard.TryGetValue(job.Number, out var h) ? h : 0;
            if (!quiet && job.Heard.Count > heardBefore
                && all.TryGetValue(job.Heard[job.Heard.Count - 1], out var from) && from.Worker >= 0 && from.Worker < workers)
                events.Add(new CrewEvent(CrewCue.Share, job.Number, "Thanks for the notes!", from.Worker));
            _heard[job.Number] = job.Heard.Count;
            _planned[job.Number] = steps;
            _asked[job.Number] = asked;
            next.Add((job, m));
            seated.Remove(job.Number);
        }
        // A job gone from the board altogether (a chat cleared): its worker leaves too.
        foreach (var gone in seated.Keys)
        {
            if (!quiet) events.Add(new CrewEvent(CrewCue.Leave, gone));
            Forget(gone);
        }
        // Workers for the jobs that have none yet: the one it had before when free, else the free one who has waited
        // longest for work (so the jobs go round the team, not always to the first), else the desk of someone still
        // showing a finished job, who leaves now.
        foreach (var (job, _) in next.Where(n => !_slots.ContainsKey(n.Job.Number)).ToList())
        {
            int slot;
            // (With no number of workers given, simply the first free one.)
            var free = workers > 64 ? [] : Enumerable.Range(0, workers).Where(k => !_slots.ContainsValue(k)).ToList();
            if (job.Worker >= 0 && job.Worker < workers && !_slots.ContainsValue(job.Worker)) slot = job.Worker;
            else if (free.Count > 0) slot = free.OrderBy(k => _given.TryGetValue(k, out var at) ? at : DateTime.MinValue).ThenBy(k => k).First();
            else
            {
                slot = 0;
                while (_slots.ContainsValue(slot)) slot++;
                if (slot >= workers && next.FirstOrDefault(n => n.Mood is CrewMood.Done or CrewMood.Failed && _slots.ContainsKey(n.Job.Number)) is { Job: { } done })
                {
                    slot = _slots[done.Number];
                    next.RemoveAll(n => n.Job.Number == done.Number);
                    events.RemoveAll(e => e.Number == done.Number);
                    if (!quiet) events.Insert(0, new CrewEvent(CrewCue.Leave, done.Number));
                    Forget(done.Number);
                }
            }
            _slots[job.Number] = slot;
            _given[slot] = now;
        }
        Unassigned = unassigned;
        // Several jobs handed out at once: the coordinator splits the work in a huddle.
        if (events.Count(e => e.Cue == CrewCue.Arrive) >= 2) events.Insert(0, new CrewEvent(CrewCue.Huddle, 0, "Team, let's split this!"));
        var ahead = next.ToDictionary(n => n.Job.Number, n => n.Job.Ahead);
        var placed = next.Select(n =>
        {
            var finished = n.Mood is CrewMood.Done;
            var (done, total) = JobPlan.Count(n.Job.Plan);
            return new CrewSeat(n.Job.Number, n.Job.Title, n.Mood, _slots[n.Job.Number], Percent: JobPlan.Percent(n.Job.Plan, finished), Left: total == 0 ? -1 : finished ? 0 : total - done,
                Steps: n.Job.Items.Count(i => i.Kind == "tool"));
        }).ToList();
        // The line at the applier: the change being applied first, then the others in the order they asked.
        var line = placed.Where(s => s.Mood == CrewMood.Applying)
            .Concat(placed.Where(s => s.Mood == CrewMood.Waiting).OrderBy(s => ahead[s.Number] <= 0 ? int.MaxValue : ahead[s.Number]).ThenBy(s => s.Number))
            .Select(s => s.Number).ToList();
        _seats = placed.Select(s => line.IndexOf(s.Number) is var at and >= 0 ? s with { Line = at } : s).ToList();
        return events;
    }

    private void Forget(int number)
    {
        _leaving.Remove(number);
        _asked.Remove(number);
        _slots.Remove(number);
        _planned.Remove(number);
        _heard.Remove(number);
    }

    /// <summary>Starts over: the next update seats who is working without acting anything out.</summary>
    public void Reset()
    {
        _asked.Clear();
        _leaving.Clear();
        _slots.Clear();
        _planned.Clear();
        _heard.Clear();
        _seats = [];
        Unassigned = 0;
        _started = false;
    }

    /// <summary>A running job's mood from what it does now.</summary>
    internal static CrewMood Mood(ChatJob job, Func<ChatJob, string>? activity) => (activity?.Invoke(job) ?? "") switch
    {
        "applying" => CrewMood.Applying,
        "waiting" or "syncing" => CrewMood.Waiting,
        "reading" => CrewMood.Reading,
        "thinking" => CrewMood.Thinking,
        _ => job.State == JobBoard.Waiting ? CrewMood.Waiting : CrewMood.Thinking,
    };

    /// <summary>How many messages the job has been given (its task and every message after it).</summary>
    private static int Asked(ChatJob job) => job.Items.Count(i => i.Kind == "user");

    /// <summary>The line over the strip: who is doing what, in a few words, by name.</summary>
    public static string Caption(IReadOnlyList<CrewSeat> seats, CrewNames names)
    {
        return Caption(seats, names, 0);
    }

    /// <summary>The same, with how many jobs wait on the coordinator's desk for a free worker.</summary>
    public static string Caption(IReadOnlyList<CrewSeat> seats, CrewNames names, int unassigned)
    {
        var queued = unassigned <= 0 ? "" : unassigned == 1 ? "1 job waits for a free worker" : $"{unassigned} jobs wait for a free worker";
        if (seats.Count == 0) return queued.Length > 0 ? queued : $"{names.Coordinator} is ready";
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
        if (queued.Length > 0) parts.Add(queued);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// What a worker says in a bubble, a few friendly words: a long task is a big job, the plan says how big it is, the
    /// turn at Revit is "My turn!", and a finished job says how it went (quick, hard or plain done).
    /// </summary>
    public static string Say(CrewCue cue, ChatJob job)
    {
        static string Pick(int n, params string[] said) => said[Math.Abs(n) % said.Length];
        var steps = job.Plan.Count;
        return cue switch
        {
            CrewCue.Arrive => job.Task.Length > 600 ? "Big job!" : Pick(job.Number, "On it!", "Got it!", "Let's go!"),
            CrewCue.Handover => Pick(job.Items.Count, "Noted!", "On it!"),
            CrewCue.Plan => steps >= 6 ? "Big job!" : steps <= 2 ? "Quick one!" : "Smart plan!",
            CrewCue.Turn => "My turn!",
            CrewCue.Finish when job.State == JobBoard.Failed => "Hit a snag",
            CrewCue.Finish when job.Ended > job.Started && job.Started != default && job.Ended - job.Started < TimeSpan.FromSeconds(90) => "Quick job, done!",
            CrewCue.Finish when steps >= 6 => "Hard job, done!",
            CrewCue.Finish => "Job done!",
            _ => "",
        };
    }

    /// <summary>
    /// The line under a worker's name: how far along and what is left ("60% · 2 left"), what it waits for, or, for a
    /// worker without a job (<paramref name="seat"/> null), that they are ready for one.
    /// </summary>
    public static string Status(CrewSeat? seat, int slot)
    {
        if (seat is null) return (slot % 3) switch { 0 => "Ready for work", 1 => "Free for a job", _ => "Ready when you are" };
        var far = seat.Percent < 0 ? "" : seat.Left > 0 ? $"{seat.Percent}% · {seat.Left} left" : $"{seat.Percent}%";
        return seat.Mood switch
        {
            CrewMood.Done => "Job done!",
            CrewMood.Failed => "Needs a look",
            CrewMood.Resting => "Starting again soon",
            CrewMood.Applying => far.Length > 0 ? $"{seat.Percent}% · applying" : "Applying",
            CrewMood.Waiting => far.Length > 0 ? $"{seat.Percent}% · in line" : "In line",
            CrewMood.Reading => far.Length > 0 ? far : "Reading the model",
            // No plan written (yet): how many steps it took, so there is always something moving.
            _ => far.Length > 0 ? far : seat.Steps > 0 ? $"Step {seat.Steps}" : "Planning…",
        };
    }

    /// <summary>The worker who has job <paramref name="number"/> now, or null when none has it.</summary>
    public string? WorkerOf(int number, CrewNames names) => _slots.TryGetValue(number, out var slot) ? names.Worker(slot) : null;
}
