using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>One line of a job's detail as saved before 0.34.33 (read only: <see cref="JobBoard"/> turns it into chat items).</summary>
internal sealed class JobLine
{
    /// <summary>you (a message to the job), text (the job's answer), step (a tool call) or note.</summary>
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Steps: running, done or failed.</summary>
    public string State { get; set; } = "";
    public DateTime Time { get; set; }
}

/// <summary>
/// One parallel job of a chat: its own hidden Claude Code session (kept in the jobs folder's sessions.json under
/// <see cref="Key"/>), shown as a card in the pane and as its own chat (the job view), saved with the chat
/// (<see cref="Conversation.Jobs"/>).
/// </summary>
internal sealed class ChatJob
{
    /// <summary>The job's number in its chat (1, 2, …): how the main chat and the user name it.</summary>
    public int Number { get; set; }
    /// <summary>Unique for good: the session's key in sessions.json and what the connector passes on (SKETCHPROMAX_JOB).</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>What the job was asked to do, in full (it doesn't see the main chat).</summary>
    public string Task { get; set; } = "";
    /// <summary>queued (waiting to start), running, waiting (its change waits its turn in Revit), done, failed or stopped.</summary>
    public string State { get; set; } = JobBoard.Queued;
    /// <summary>What it reported when its last run ended (its final answer), or why it failed.</summary>
    public string Summary { get; set; } = "";
    /// <summary>What it is doing now (the latest step or line), while it runs.</summary>
    public string Now { get; set; } = "";
    /// <summary>How many changes are ahead of it while it waits its turn (0 when none).</summary>
    public int Ahead { get; set; }
    /// <summary>
    /// What it does right now in Revit, set by the bridge: reading (a read runs), waiting (its change waits its turn to be
    /// applied), applying (its change runs); "" between calls, when it is thinking. Not saved. <see cref="JobBoard.ActivityOf"/>
    /// gives the whole picture, for the card and its animation.
    /// </summary>
    [JsonIgnore] public string Activity { get; set; } = "";
    /// <summary>Which worker of the crew strip took it (0: the first name in Settings > Chat; -1: none yet). Saved.</summary>
    public int Worker { get; set; } = -1;
    /// <summary>Its plan's steps by id, each pending, in_progress or completed (JobPlan), as its Claude Code keeps it. Saved.</summary>
    public Dictionary<string, string> Plan { get; set; } = new();
    /// <summary>The other jobs whose reports it was given (JobBoard.Team), so each one reaches it once. Saved.</summary>
    public List<int> Heard { get; set; } = [];
    /// <summary>The other jobs it went to for information or told something (chat_jobs ask / message from a job), oldest first. Saved.</summary>
    public List<int> Visited { get; set; } = [];
    public DateTime Created { get; set; }
    public DateTime Started { get; set; }
    public DateTime Ended { get; set; }
    /// <summary>The kind of work (ChatAuto: simple, everyday, hard), the model alias and the effort it runs with, set when it opens.</summary>
    public string Kind { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    /// <summary>True once it has a Claude Code session (the next run resumes it).</summary>
    public bool HasSession { get; set; }
    /// <summary>
    /// Messages for it not sent yet, each saying who sent it ("The user says: …", "The main chat says: …"): they go as
    /// its next run (or right after the current one).
    /// </summary>
    public List<string> Inbox { get; set; } = [];
    /// <summary>Files attached to the messages waiting in <see cref="Inbox"/> (full paths), sent with them.</summary>
    public List<string> InboxFiles { get; set; } = [];
    /// <summary>
    /// The job's own conversation, as the job view shows it: what it was asked (user items; State "coordinator" when the
    /// main chat sent it), its answers (assistant), its steps (tool) and notes. At most <see cref="JobBoard.MaxItems"/>.
    /// </summary>
    public List<ChatItem> Items { get; set; } = [];
    /// <summary>The detail lines of a job saved before 0.34.33; turned into <see cref="Items"/> when the chat is opened.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<JobLine>? Log { get; set; }

    /// <summary>True while it has (or is about to have) a Claude Code process: running or waiting its turn.</summary>
    [JsonIgnore] public bool Active => State is JobBoard.Running or JobBoard.Waiting;

    /// <summary>True until it is finished: running, waiting its turn or waiting to start (a finished job sent a message is open again).</summary>
    [JsonIgnore] public bool Open => Active || State == JobBoard.Queued;
}

/// <summary>What a job's run is sent: the text (its task, or the messages waiting for it) and the files attached to them.</summary>
internal sealed record JobMessage(string Text, List<string> Files);

/// <summary>
/// A chat's parallel jobs and the rules that need no WPF or Revit (the unit tests compile this file): starting one,
/// routing a follow-up to the job that fits, messages to a job, Stop, how many run at once, which start next, what the
/// card says, and the report the main chat gets when one finishes. Thread-safe: the bridge (the main chat's chat_jobs
/// calls, a job's change waiting its turn) and the pane change it from different threads; <see cref="Gate"/> is also
/// held while the chat is saved.
/// </summary>
internal sealed class JobBoard
{
    public const string Queued = "queued", Running = "running", Waiting = "waiting", Done = "done", Failed = "failed", Stopped = "stopped";

    /// <summary>
    /// What an active job is doing (<see cref="ActivityOf"/>): thinking (its Claude works on its own, in parallel with the
    /// others), reading the model (reads run as soon as Revit is free, between other jobs' changes), waiting to apply (its
    /// change waits its turn: changes are applied one at a time), applying (its change runs in Revit) and synchronizing (auto
    /// sync, Revit.AutoSyncRule: every job's Revit work waits while the model is synchronized with central).
    /// </summary>
    public const string Thinking = "thinking", Reading = "reading", WaitingToApply = "waiting", Applying = "applying", Syncing = "syncing";

    /// <summary>How many jobs may run at once (Settings, Chat tab): the default, and the range allowed.</summary>
    public const int DefaultLimit = 3, MinLimit = 1, MaxLimit = 6;

    /// <summary>Items kept in a job's conversation, and the longest text kept in one.</summary>
    public const int MaxItems = 300, MaxLine = 20000;

    /// <summary>A user item's State when the main chat (the coordinator) sent it, not the user.</summary>
    public const string FromCoordinator = "coordinator";

    /// <summary>How the messages waiting for a job start, by who sent them.</summary>
    public const string UserSays = "The user says: ", MainSays = "The main chat says: ", TeamSays = "A teammate, job ";

    /// <summary>A job's view marks a message another job sent it with this State and the sender's number ("team:2").</summary>
    public const string FromTeam = "team:";

    /// <summary>What a resumed job is sent when it has no message waiting.</summary>
    public const string CarryOn = "Carry on with your task.";

    /// <summary>
    /// What a main chat's own Claude Code passes with its calls ("chat-" and the chat's id), so the bridge tells the chat
    /// shown in the pane from one left working in the background (<see cref="Caller"/>). "" for a chat without an id.
    /// </summary>
    public static string ChatKey(string chatId) => chatId.Length > 0 ? ChatPrefix + chatId : "";

    public const string ChatPrefix = "chat-";

    public static bool IsChatKey(string? key) => key is not null && key.StartsWith(ChatPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Who made a call, for the bridge: null for the main chat shown in the pane (its task's Pause, Stop, approvals and
    /// queued messages apply), else the key the call takes turns under like a parallel job: a job's own key, or the key
    /// of a chat working in the background (one that is not <paramref name="shown"/>, the shown chat's key).
    /// </summary>
    public static string? Caller(string? key, string shown) =>
        string.IsNullOrEmpty(key) || (IsChatKey(key) && string.Equals(key, shown, StringComparison.Ordinal)) ? null : key;

    public readonly object Gate = new();
    private readonly List<ChatJob> _jobs;

    /// <summary>The board of a chat's saved jobs (the same list: changes are saved with the chat).</summary>
    public JobBoard(List<ChatJob> jobs)
    {
        _jobs = jobs;
        foreach (var job in _jobs) Migrate(job);
    }

    /// <summary>A job saved before 0.34.33 kept its detail as lines: they become its conversation's items.</summary>
    internal static void Migrate(ChatJob job)
    {
        job.Items ??= [];
        job.Inbox ??= [];
        job.InboxFiles ??= [];
        if (job.Log is not { } log) return;
        if (job.Items.Count == 0)
            for (var k = 0; k < log.Count; k++)
            {
                var line = log[k];
                job.Items.Add(line.Kind switch
                {
                    "you" => new ChatItem { Kind = "user", Text = line.Text, State = k == 0 ? FromCoordinator : "", Time = line.Time },
                    "text" => new ChatItem { Kind = "assistant", Text = line.Text, Time = line.Time },
                    "step" => new ChatItem { Kind = "note", Text = "Step: " + line.Text + (line.State == "failed" ? " (failed)" : ""), Time = line.Time },
                    _ => new ChatItem { Kind = "note", Text = line.Text, Time = line.Time },
                });
            }
        job.Log = null;
    }

    public static int ClampLimit(int limit) => limit < MinLimit ? DefaultLimit : Math.Min(MaxLimit, limit);

    /// <summary>A copy of every job (safe to read on any thread).</summary>
    public List<ChatJob> Snapshot()
    {
        lock (Gate) return _jobs.Select(Copy).ToList();
    }

    public ChatJob? Find(int number)
    {
        lock (Gate) return _jobs.FirstOrDefault(j => j.Number == number);
    }

    /// <summary>The job whose session is <paramref name="key"/> (what the connector passes on for a job's calls).</summary>
    public ChatJob? FindKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (Gate) return _jobs.FirstOrDefault(j => j.Key == key);
    }

    /// <summary>
    /// One job asks another for what it knows (chat_jobs action=ask from a job): the answer is what <paramref name="target"/>
    /// has found so far (its report, or its latest words and step), at once and without a run of its own. Both views note
    /// it, and the asker walks over on the crew strip. With <paramref name="forward"/>, an open target also gets the
    /// question, and answers with a message when its current run ends. Null when either job isn't found.
    /// </summary>
    public string? Ask(string askerKey, int target, string question, bool forward, DateTime now)
    {
        lock (Gate)
        {
            var asker = _jobs.FirstOrDefault(j => j.Key == askerKey);
            var other = _jobs.FirstOrDefault(j => j.Number == target);
            if (asker is null || other is null || asker == other) return null;
            question = question.Trim();
            asker.Visited.Add(other.Number);
            AddItem(asker, new ChatItem { Kind = "note", Text = $"Asked job {other.Number} (\"{other.Title}\"): {OneLine(question, 200)}" }, now);
            var answer = Findings(other);
            if (forward && question.Length > 0 && (other.Active || other.State == Queued))
            {
                other.Inbox.Add($"{TeamSays}{asker.Number} (\"{asker.Title}\"), asks: {question}\nAnswer it with chat_jobs action=message, job={asker.Number}, when you know.");
                AddItem(other, new ChatItem { Kind = "user", Text = question, State = FromTeam + asker.Number }, now);
                answer += $"\nJob {other.Number} also got your question and will message you the answer when its current run ends; carry on meanwhile.";
            }
            return answer;
        }
    }

    /// <summary>
    /// One job tells another something (chat_jobs action=message from a job): it goes to that job like the main chat's
    /// messages, marked as from the teammate, and the sender walks over on the crew strip.
    /// </summary>
    public Delivery Tell(string senderKey, int target, string text, DateTime now)
    {
        lock (Gate)
        {
            var sender = _jobs.FirstOrDefault(j => j.Key == senderKey);
            var other = _jobs.FirstOrDefault(j => j.Number == target);
            text = text.Trim();
            if (sender is null || other is null || sender == other || text.Length == 0) return Delivery.NotFound;
            sender.Visited.Add(other.Number);
            other.Inbox.Add($"{TeamSays}{sender.Number} (\"{sender.Title}\"), says: {text}");
            AddItem(other, new ChatItem { Kind = "user", Text = text, State = FromTeam + sender.Number }, now);
            if (other.Active || other.State == Queued) return Delivery.Queued;
            other.State = Queued;
            other.Summary = "";
            return Delivery.Reopened;
        }
    }

    /// <summary>What a job has found so far, for a teammate who asks: its task, state, report or latest words, and its step now.</summary>
    public static string Findings(ChatJob job)
    {
        var lines = new List<string> { $"Job {job.Number} \"{job.Title}\" ({StateLabel(job)}). Its task: {OneLine(job.Task, 300)}" };
        if (job.Summary.Length > 0) lines.Add("Its report: " + OneLine(job.Summary, 1500));
        else
        {
            var said = job.Items.Where(i => i.Kind == "assistant" && i.Text.Trim().Length > 0).Select(i => i.Text.Trim()).ToList();
            if (said.Count > 0) lines.Add("What it has found so far: " + OneLine(string.Join(" ", said.Skip(Math.Max(0, said.Count - 2))), 1500));
            if (job.Now.Length > 0) lines.Add("Doing now: " + OneLine(job.Now, 150));
            if (said.Count == 0) lines.Add("It hasn't reported anything yet.");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Jobs running or waiting their turn now.</summary>
    public int ActiveCount
    {
        get { lock (Gate) return _jobs.Count(j => j.Active); }
    }

    /// <summary>Jobs running, waiting their turn or waiting to start.</summary>
    public int OpenCount
    {
        get { lock (Gate) return _jobs.Count(j => j.Open); }
    }

    /// <summary>
    /// The jobs the switcher lists, and how many of them are open (they come first, in job order). The side pane
    /// (<paramref name="all"/> false) lists only open jobs, plus job <paramref name="shown"/> while its chat is open; the
    /// large window's Jobs column lists the finished ones too, under the open ones, the latest first.
    /// </summary>
    public static (List<ChatJob> Jobs, int Open) ForSwitcher(IEnumerable<ChatJob> jobs, bool all, int? shown)
    {
        var open = jobs.Where(j => j.Open).OrderBy(j => j.Number).ToList();
        var count = open.Count;
        var finished = jobs.Where(j => !j.Open && (all || j.Number == shown)).OrderByDescending(j => j.Number);
        return (open.Concat(finished).ToList(), count);
    }

    /// <summary>
    /// Opens a job (it waits to start until a place is free), unless the request reads as a follow-up of an existing job
    /// and <paramref name="force"/> is off: then nothing is opened and <paramref name="similar"/> is that job.
    /// </summary>
    public ChatJob? Start(string title, string task, DateTime now, bool force, out ChatJob? similar)
    {
        lock (Gate)
        {
            similar = force ? null : JobRouting.Best(_jobs, title + "\n" + task);
            if (similar is not null) return null;
            var job = new ChatJob
            {
                Number = _jobs.Count == 0 ? 1 : _jobs.Max(j => j.Number) + 1,
                Key = Guid.NewGuid().ToString("N").Substring(0, 12),
                Title = Clean(title, 60), Task = task.Trim(), Created = now, State = Queued,
            };
            if (job.Title.Length == 0) job.Title = ChatHistory.AutoTitle(task, 60);
            _jobs.Add(job);
            AddItem(job, new ChatItem { Kind = "user", Text = job.Task, State = FromCoordinator }, now);
            return job;
        }
    }

    /// <summary>How a message to a job went.</summary>
    internal enum Delivery { NotFound, Queued, Reopened }

    /// <summary>
    /// A message for job <paramref name="number"/>, from the user (typed in the job view, with any
    /// <paramref name="files"/>) or from the main chat: a running job gets it as soon as its current run ends; a finished,
    /// failed or stopped one opens again with it (it waits to start like a new job).
    /// </summary>
    public Delivery Message(int number, string text, DateTime now, bool fromUser = false, IReadOnlyList<string>? files = null)
    {
        lock (Gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Number == number);
            text = text.Trim();
            files ??= [];
            if (job is null || text.Length == 0 && files.Count == 0) return Delivery.NotFound;
            var said = text.Length > 0 ? text : "(see the attached files)";
            job.Inbox.Add((fromUser ? UserSays : MainSays) + said);
            job.InboxFiles.AddRange(files);
            AddItem(job, new ChatItem
            {
                Kind = "user", Text = text, State = fromUser ? "" : FromCoordinator,
                Files = files.ToList(), Name = string.Join(", ", files.Select(f => f.Split('\\', '/').Last())),
            }, now);
            if (job.Active || job.State == Queued) return Delivery.Queued;
            job.State = Queued;
            job.Summary = "";
            return Delivery.Reopened;
        }
    }

    /// <summary>Stops a job: it won't start, or its run ends now (the pane ends its process). Its state before; null when not found.</summary>
    public string? Stop(int number, DateTime now)
    {
        lock (Gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Number == number);
            if (job is null) return null;
            var was = job.State;
            if (job.Active || job.State == Queued)
            {
                job.State = Stopped;
                job.Ended = now;
                job.Now = "";
                job.Ahead = 0;
                job.Activity = "";
                job.Inbox.Clear();
                job.InboxFiles.Clear();
                AddItem(job, new ChatItem { Kind = "note", Text = "Stopped. Send it a message to carry on." }, now);
            }
            return was;
        }
    }

    /// <summary>
    /// The jobs to start now, oldest first, while fewer than <paramref name="limit"/> run: each is marked running and
    /// comes with the message its run starts with (the whole task for a new job, else the messages waiting for it).
    /// </summary>
    public List<(ChatJob Job, JobMessage Message)> ToStart(int limit, DateTime now)
    {
        lock (Gate)
        {
            var list = new List<(ChatJob, JobMessage)>();
            var free = ClampLimit(limit) - _jobs.Count(j => j.Active);
            foreach (var job in _jobs.Where(j => j.State == Queued).OrderBy(j => j.Created).ThenBy(j => j.Number).ToList())
            {
                if (free-- <= 0) break;
                job.State = Running;
                job.Started = now;
                job.Ended = default;
                job.Now = "Starting…";
                if (JobPlan.AllDone(job.Plan)) job.Plan.Clear(); // a new run after a finished plan makes its own
                var message = NextMessage(job);
                // The team: who else works on what, and what the jobs that finished found, so it builds on them.
                var team = Team(job, _jobs, out var shared);
                if (team.Length > 0) message = message with { Text = message.Text + "\n\n" + team };
                job.Heard.AddRange(shared);
                list.Add((job, message));
            }
            return list;
        }
    }

    /// <summary>
    /// The message a run starts with: the task first time, the waiting messages after, each saying who sent it, with
    /// their files (the inbox is emptied).
    /// </summary>
    public static JobMessage NextMessage(ChatJob job)
    {
        var (waiting, files) = TakeWaiting(job);
        if (job.HasSession) return new JobMessage(waiting.Length > 0 ? waiting : CarryOn, files);
        var first = "Your job (number " + job.Number + ", \"" + job.Title + "\"):\n\n" + job.Task;
        return new JobMessage(waiting.Length > 0 ? first + "\n\nSince then:\n\n" + waiting : first, files);
    }

    /// <summary>
    /// What a job starting a run hears from the team: on its first run, the other open jobs and their parts (so nobody
    /// does the same work twice); every run, the reports of jobs that finished since it last heard (facts it can use
    /// instead of reading the model again). <paramref name="shared"/>: the jobs whose reports it now has. "" for nothing.
    /// </summary>
    public static string Team(ChatJob job, IEnumerable<ChatJob> jobs, out List<int> shared)
    {
        var others = jobs.Where(j => j.Number != job.Number).OrderBy(j => j.Number).ToList();
        var working = job.HasSession ? new List<ChatJob>() : others.Where(j => j.Open).ToList();
        var reports = others.Where(j => j.State == Done && j.Summary.Length > 0 && !job.Heard.Contains(j.Number)).ToList();
        reports = reports.Skip(Math.Max(0, reports.Count - 6)).ToList();
        shared = reports.Select(j => j.Number).ToList();
        if (working.Count == 0 && reports.Count == 0) return "";
        var sb = new StringBuilder("From the team (the other jobs of this chat):");
        foreach (var j in working)
            sb.Append($"\n- Job {j.Number} \"{j.Title}\" works alongside you on: {OneLine(j.Task, 200)} Leave that part to it.");
        foreach (var j in reports)
            sb.Append($"\n- Job {j.Number} \"{j.Title}\" finished and found: {OneLine(j.Summary, 500)} Use what helps rather than reading the model again.");
        return sb.ToString();
    }

    /// <summary>The messages waiting for the job, joined, and their files (the inbox is emptied).</summary>
    private static (string Text, List<string> Files) TakeWaiting(ChatJob job)
    {
        var waiting = string.Join("\n\n", job.Inbox);
        var files = job.InboxFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        job.Inbox.Clear();
        job.InboxFiles.Clear();
        return (waiting, files);
    }

    /// <summary>
    /// The job's session is gone (made on another PC, deleted): its run starts from the task again, with
    /// <paramref name="pending"/> (the message that run was to start with) after it. The new first message.
    /// </summary>
    public JobMessage Restart(ChatJob job, JobMessage pending)
    {
        lock (Gate)
        {
            job.HasSession = false;
            if (pending.Text.StartsWith(UserSays, StringComparison.Ordinal) || pending.Text.StartsWith(MainSays, StringComparison.Ordinal)
                || pending.Text.StartsWith(TeamSays, StringComparison.Ordinal))
            {
                job.Inbox.Insert(0, pending.Text);
                job.InboxFiles.InsertRange(0, pending.Files);
            }
            return NextMessage(job);
        }
    }

    /// <summary>
    /// A run ended. With messages waiting, the job carries on with them (returned; it stays running). Otherwise it is
    /// done (or failed) with <paramref name="summary"/>; null is returned. A job stopped meanwhile stays stopped.
    /// </summary>
    public JobMessage? RunEnded(ChatJob job, bool ok, string summary, DateTime now)
    {
        lock (Gate)
        {
            if (job.State == Stopped) return null;
            if (ok && job.Inbox.Count > 0)
            {
                job.Summary = summary.Trim();
                job.State = Running;
                var (waiting, files) = TakeWaiting(job); // the same process carries on: only the new messages
                return new JobMessage(waiting, files);
            }
            job.State = ok ? Done : Failed;
            job.Summary = summary.Trim();
            job.Ended = now;
            job.Now = "";
            job.Ahead = 0;
            job.Activity = "";
            return null;
        }
    }

    /// <summary>The bridge: the job's change waits its turn (<paramref name="ahead"/> changes before it), or got it (null).</summary>
    public bool SetWaiting(string key, int? ahead)
    {
        lock (Gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Key == key);
            if (job is null || !job.Active) return false;
            job.State = ahead is null ? Running : Waiting;
            job.Ahead = ahead ?? 0;
            return true;
        }
    }

    /// <summary>
    /// The bridge: what job <paramref name="key"/> does in Revit now (<see cref="Reading"/>, <see cref="WaitingToApply"/>
    /// with <paramref name="ahead"/> changes before it counting itself, <see cref="Applying"/>, or "" when its call ended and
    /// it thinks again). False when nothing changed or it isn't active.
    /// </summary>
    public bool SetActivity(string key, string activity, int ahead = 0)
    {
        lock (Gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Key == key);
            if (job is null || !job.Active) return false;
            var state = activity == WaitingToApply ? Waiting : Running;
            var count = activity == WaitingToApply ? Math.Max(1, ahead) : 0;
            if (job.Activity == activity && job.State == state && job.Ahead == count) return false;
            job.Activity = activity;
            job.State = state;
            job.Ahead = count;
            return true;
        }
    }

    /// <summary>The crew strip gave job <paramref name="number"/> to worker <paramref name="worker"/> (kept with the chat).</summary>
    public void SetWorker(int number, int worker)
    {
        lock (Gate)
            if (_jobs.FirstOrDefault(j => j.Number == number) is { } job) job.Worker = worker;
    }

    /// <summary>A plan tool call of the job's (JobPlan) finished: its plan follows it. True when the plan changed.</summary>
    public bool UpdatePlan(ChatJob job, string tool, string input, string result)
    {
        lock (Gate) return JobPlan.Apply(job.Plan, tool, input, result);
    }

    /// <summary>What an active job is doing: thinking, reading, waiting (to apply) or applying; "" when it isn't active.</summary>
    public static string ActivityOf(ChatJob job) =>
        !job.Active ? "" : job.State == Waiting ? WaitingToApply : job.Activity is Reading or Applying or Syncing ? job.Activity : Thinking;

    /// <summary>Jobs saved while running (Revit closed, the chat was left): stopped, so a message carries them on.</summary>
    public void AfterReload(DateTime now)
    {
        lock (Gate)
            foreach (var job in _jobs.Where(j => j.Active || j.State == Queued))
            {
                job.State = Stopped;
                job.Ended = now;
                job.Now = "";
                job.Ahead = 0;
                job.Activity = "";
                AddItem(job, new ChatItem { Kind = "note", Text = "It stopped when the chat closed. Send it a message to carry on." }, now);
            }
    }

    /// <summary>
    /// Adds an item to the job's conversation (the oldest go first past <see cref="MaxItems"/>); its answers and steps are
    /// what it is doing now. The item, to update a step when it ends.
    /// </summary>
    public ChatItem AddItem(ChatJob job, ChatItem item, DateTime now)
    {
        lock (Gate)
        {
            if (item.Time == default) item.Time = now;
            if (item.Text.Length > MaxLine) item.Text = item.Text.Substring(0, MaxLine) + "…";
            job.Items.Add(item);
            if (job.Items.Count > MaxItems) job.Items.RemoveRange(0, job.Items.Count - MaxItems);
            if (item.Kind == "assistant") job.Now = OneLine(item.Text, 120);
            return item;
        }
    }

    /// <summary>A note in the job's conversation (it stopped, its session was gone, a problem).</summary>
    public ChatItem AddNote(ChatJob job, string text, DateTime now) => AddItem(job, new ChatItem { Kind = "note", Text = text }, now);

    /// <summary>The job's conversation as it is now (a copy of the list; the items are the board's own), for the job view.</summary>
    public List<ChatItem> ItemsOf(int number)
    {
        lock (Gate) return _jobs.FirstOrDefault(j => j.Number == number)?.Items.ToList() ?? [];
    }

    // ---- Words ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A coordinator's start of one whole job while workers are free is sent back to be divided (chat_jobs): with two
    /// or more free workers, the work goes out in parts, unless the coordinator says why it can't be divided
    /// (<paramref name="single"/>). The workers then work at once instead of one doing everything.
    /// </summary>
    public static bool MustDivide(int free, string? single) => free >= 2 && string.IsNullOrWhiteSpace(single);

    /// <summary>What the card says about the job's state, with how far along its plan is while it works ("Thinking · 40% · 3 left").</summary>
    public static string StateLabel(ChatJob job) => JobPlan.Progress(job) is { Length: > 0 } far ? Bare(job) + " · " + far : Bare(job);

    private static string Bare(ChatJob job) => job.State switch
    {
        Queued => "Waiting to start",
        Waiting => job.Ahead > 1 ? $"Waiting to apply ({job.Ahead - 1} ahead)" : "Waiting to apply",
        Running => ActivityOf(job) switch { Reading => "Reading the model", Applying => "Applying changes", Syncing => "Synchronizing with central", _ => "Thinking" },
        Done => "Done",
        Failed => "Ended with a problem",
        Stopped => "Stopped",
        _ => job.State,
    };

    /// <summary>The jobs as list_jobs gives them to the main chat.</summary>
    public string Describe(int limit)
    {
        lock (Gate)
        {
            if (_jobs.Count == 0) return $"No jobs in this chat yet. Up to {ClampLimit(limit)} run at once.";
            var free = Math.Max(0, ClampLimit(limit) - _jobs.Count(j => j.Open));
            var sb = new StringBuilder($"Jobs in this chat (up to {ClampLimit(limit)} run at once, {free} worker(s) free now; changes to the model are applied one at a time; thinking and reading run in parallel):\n");
            foreach (var j in _jobs.OrderBy(j => j.Number))
            {
                sb.Append($"- Job {j.Number} \"{j.Title}\": {StateLabel(j).ToLowerInvariant()}");
                if (j.Active && j.Now.Length > 0) sb.Append($". Now: {OneLine(j.Now, 120)}");
                if (j.Inbox.Count > 0) sb.Append($". {j.Inbox.Count} message(s) waiting for it");
                sb.Append($". Task: {OneLine(j.Task, 200)}");
                if (j.Summary.Length > 0) sb.Append($". Reported: {OneLine(j.Summary, 300)}");
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>The message the main chat gets when jobs finish (sent as its next turn; the user never typed it).</summary>
    public static string Report(IEnumerable<ChatJob> finished, int stillOpen)
    {
        var sb = new StringBuilder("[Job report from Sketch ProMax, not typed by the user]\n");
        foreach (var j in finished)
            sb.Append($"\nJob {j.Number} \"{j.Title}\" {(j.State == Failed ? "ended with a problem" : "finished")}:\n{(j.Summary.Length > 0 ? j.Summary.Trim() : "(it gave no summary)")}\n");
        sb.Append(stillOpen > 0 ? $"\n{stillOpen} other job(s) are still open. " : "\nNo other jobs are open. ");
        sb.Append("Tell the user in a line or two what it did and anything they need to check; the job's card opens its full conversation. Don't redo its work.");
        return sb.ToString();
    }

    private static string Clean(string text, int max) => OneLine(text, max);

    internal static string OneLine(string text, int max)
    {
        var line = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return line.Length <= max ? line : line.Substring(0, max - 1).TrimEnd() + "…";
    }

    private static ChatJob Copy(ChatJob j) => new()
    {
        Number = j.Number, Key = j.Key, Title = j.Title, Task = j.Task, State = j.State, Kind = j.Kind, Model = j.Model, Effort = j.Effort, Summary = j.Summary, Now = j.Now, Ahead = j.Ahead,
        Created = j.Created, Started = j.Started, Ended = j.Ended, HasSession = j.HasSession, Inbox = j.Inbox.ToList(),
        InboxFiles = j.InboxFiles.ToList(), Items = j.Items.ToList(), Activity = j.Activity, Worker = j.Worker,
        Plan = new Dictionary<string, string>(j.Plan), Heard = j.Heard.ToList(), Visited = j.Visited.ToList(),
    };
}

/// <summary>
/// Which existing job a follow-up belongs to: "also add level 6" after "Sheets for levels 1-5" goes to that job, not a
/// new one. Words are compared without case, plural s or filler words; numbers count only alongside a shared word.
/// </summary>
internal static class JobRouting
{
    /// <summary>Share of the follow-up's words a job must have to take it.</summary>
    public const double Threshold = 0.34;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "the", "and", "also", "add", "for", "with", "this", "that", "then", "too", "all", "any", "please", "can", "you", "make",
        "into", "from", "same", "more", "one", "now", "again", "job", "new", "its", "our", "are", "was", "but", "not", "just", "them",
        "they", "what", "should", "would", "could", "need", "want", "like", "use", "using", "each", "every", "other", "some", "check",
    };

    /// <summary>The words that count: lower case, without plural s, filler left out.</summary>
    public static HashSet<string> Words(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches((text ?? "").ToLowerInvariant(), @"[a-z]+|\d+"))
        {
            var w = m.Value;
            if (char.IsDigit(w[0])) { set.Add(w); continue; }
            if (w.Length < 3 || Filler.Contains(w)) continue;
            if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) w = w.Substring(0, w.Length - 3) + "y";
            else if (w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal)) w = w.Substring(0, w.Length - 1);
            set.Add(w);
        }
        return set;
    }

    /// <summary>How well <paramref name="text"/> fits <paramref name="job"/>: 0 (nothing in common) to 1.</summary>
    public static double Score(string text, ChatJob job)
    {
        var mine = Words(text);
        var theirs = Words(job.Title + " " + job.Task);
        var shared = mine.Where(theirs.Contains).ToList();
        if (!shared.Any(w => !char.IsDigit(w[0]))) return 0;
        var counted = mine.Count(w => !char.IsDigit(w[0])) + Math.Min(1, mine.Count(w => char.IsDigit(w[0])));
        return counted == 0 ? 0 : (double)shared.Count(w => !char.IsDigit(w[0])) / counted;
    }

    /// <summary>The one job that clearly fits <paramref name="text"/> (above the threshold and ahead of the rest), or null.</summary>
    public static ChatJob? Best(IEnumerable<ChatJob> jobs, string text)
    {
        var scored = jobs.Select(j => (Job: j, Score: Score(text, j))).Where(s => s.Score >= Threshold).OrderByDescending(s => s.Score).ToList();
        if (scored.Count == 0) return null;
        return scored.Count > 1 && scored[1].Score >= scored[0].Score ? null : scored[0].Job;
    }
}

/// <summary>
/// Each job's Claude Code session, kept in its chat's jobs folder as sessions.json (job key → session id), the way
/// read_pdf_part keeps a PDF part's helper chat: the next run resumes it.
/// </summary>
internal static class JobSessions
{
    private static readonly object Lock = new();

    public static string? Get(string folder, string key)
    {
        lock (Lock) return Read(folder)[key]?.GetValue<string>();
    }

    public static void Set(string folder, string key, string session)
    {
        lock (Lock)
        {
            var map = Read(folder);
            if (map[key]?.GetValue<string>() == session) return;
            map[key] = session;
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "sessions.json"), map.ToJsonString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next run starts the job's session afresh */ }
        }
    }

    private static JsonObject Read(string folder)
    {
        var file = Path.Combine(folder, "sessions.json");
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? [] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
