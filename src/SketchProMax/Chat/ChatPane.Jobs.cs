using System.IO;
using System.Windows;
using System.Windows.Threading;
using SketchProMax.Settings;

namespace SketchProMax.Chat;

/// <summary>
/// Where the bridge reaches the open chat's parallel jobs (chat_jobs calls and a job's change waiting its turn), from
/// its own threads. The pane sets it when a chat is shown.
/// </summary>
internal static class ChatJobs
{
    private static volatile JobBoard? _board;
    private static volatile Action? _changed;

    /// <summary>The open chat's jobs (null before the pane has a chat).</summary>
    public static JobBoard? Board { get => _board; set => _board = value; }

    /// <summary>Called after the bridge changed a job (any thread): the pane starts, stops and redraws on its own thread.</summary>
    public static Action? Changed { get => _changed; set => _changed = value; }

    /// <summary>True when jobs can run: a chat is open and the connector is as new as this add-in.</summary>
    public static bool Available => _board is not null && ClaudeCli.JobsAvailable;

    /// <summary>How many jobs run at once (Settings, Chat tab).</summary>
    public static int Limit => JobBoard.ClampLimit(AgentSettings.Load().ChatJobLimit);

    /// <summary>The bridge: job <paramref name="key"/>'s call waits its turn (<paramref name="ahead"/>: its place, 1 = next) or got it (null).</summary>
    public static void SetWaiting(string key, int? ahead)
    {
        if (_board?.SetWaiting(key, ahead) == true) _changed?.Invoke();
    }
}

/// <summary>
/// The chat as coordinator of parallel jobs (JobBoard): the main chat starts jobs with chat_jobs, each runs as its own
/// hidden Claude Code session (ClaudeCli.JobStart, its session in the chat's jobs folder, sessions.json) with a card in
/// the conversation ("pjob" item) that opens to its detail and a message box, plus Stop. Up to the Settings' number run at
/// once; the rest wait to start. A job that finishes reports back: its summary goes to the main chat as its next turn.
/// </summary>
internal sealed partial class ChatPane
{
    /// <summary>One job's run: its Claude Code process and what the run has said so far.</summary>
    private sealed class JobRun
    {
        public ChatJob Job = null!;
        public ChatEngine Engine = null!;
        /// <summary>The message the run started with (sent again from the task when its session is gone).</summary>
        public string Message = "";
        public bool Resumed, Retried;
        public string LastText = "";
        public readonly Dictionary<string, JobLine> Steps = new();
    }

    private JobBoard? _board;
    private List<ChatJob>? _boardJobs;
    private readonly Dictionary<string, JobRun> _runs = new();
    private readonly Dictionary<int, ParallelJobCard> _jobCards = new();
    private readonly List<int> _reports = [];
    private DispatcherTimer? _reportTimer, _jobClock;
    private bool _jobsQueued;

    /// <summary>True while any of the chat's jobs runs or waits to start.</summary>
    private bool JobsOpen => _board is { } board && (board.OpenCount > 0 || _runs.Count > 0);

    private static string JobsFolder(Conversation conv) => Path.Combine(ClaudeCli.Folder, "jobs", conv.Id.Length > 0 ? conv.Id : "chat");

    /// <summary>The shown chat's jobs: a chat opened from disk has none running (those saved running are stopped).</summary>
    private void AttachJobs()
    {
        if (_board is not null && ReferenceEquals(_boardJobs, _conv.Jobs)) return;
        EndRuns("The chat was left, so this job stopped. Open the chat again and send it a message to carry on.");
        _conv.Jobs ??= [];
        _boardJobs = _conv.Jobs;
        _board = new JobBoard(_conv.Jobs);
        _board.AfterReload(DateTime.UtcNow);
        _jobCards.Clear();
        _reports.Clear();
        HookJobs();
    }

    /// <summary>The bridge reaches this chat's jobs (again, when a failed version switch revived this pane).</summary>
    private void HookJobs()
    {
        if (_sample || _board is null) return;
        ChatJobs.Board = _board;
        ChatJobs.Changed = () => Dispatcher.BeginInvoke(new Action(JobsChanged));
    }

    /// <summary>Ends every running job's process (the chat is left, Revit closes, the core switches versions).</summary>
    private void EndRuns(string note, bool now = false)
    {
        foreach (var run in _runs.Values.ToList())
        {
            _board?.Stop(run.Job.Number, DateTime.UtcNow);
            if (note.Length > 0) _board?.AddLine(run.Job, "note", note, "", DateTime.UtcNow);
            if (now) run.Engine.KillNow();
            else run.Engine.Kill();
        }
        _runs.Clear();
        _jobClock?.Stop();
    }

    /// <summary>Something changed (a chat_jobs call, a turn waiting in Revit, Stop): start, stop and redraw, once per moment.</summary>
    private void JobsChanged()
    {
        if (_jobsQueued) return;
        _jobsQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _jobsQueued = false;
            if (_board is null || _stopped) return;
            // Stopped from the main chat or a card: its process ends now.
            foreach (var run in _runs.Values.Where(r => r.Job.State == JobBoard.Stopped).ToList()) EndRun(run, kill: true);
            PumpJobs();
            ShowJobCards();
            SaveSoon();
        }));
    }

    /// <summary>Starts the jobs waiting to start, while fewer than the Settings' number run.</summary>
    private void PumpJobs()
    {
        if (_board is null || _sample || _stopped || _setup != Setup.Ready || _install is null) return;
        foreach (var (job, message) in _board.ToStart(ChatJobs.Limit, DateTime.UtcNow)) _ = StartRun(job, message);
        if (_runs.Count > 0) StartJobClock();
    }

    private async Task StartRun(ChatJob job, string message, bool fresh = false)
    {
        var folder = JobsFolder(_conv);
        var resume = job.HasSession && !fresh ? JobSessions.Get(folder, job.Key) : null;
        if (job.HasSession && resume is null) message = _board!.Restart(job, message);
        string? session = null;
        if (resume is null)
        {
            session = Guid.NewGuid().ToString();
            JobSessions.Set(folder, job.Key, session);
            job.HasSession = true;
        }
        var install = _install!;
        var model = _model;
        var key = job.Key;
        var engine = new ChatEngine(Dispatcher, install, resume, model, () => ClaudeCli.JobStart(install, key, resume, session, model));
        var run = new JobRun { Job = job, Engine = engine, Message = message, Resumed = resume is not null };
        _runs[key] = run;
        engine.Event += e => { if (_runs.TryGetValue(key, out var r) && r == run) OnJobEvent(run, e); };
        engine.Exited += tail => { if (_runs.TryGetValue(key, out var r) && r == run) OnJobExited(run, tail); };
        ShowJobCards();
        try { await engine.StartAsync(StreamInput.UserMessage(message, [])); }
        catch (Exception ex)
        {
            if (!_runs.TryGetValue(key, out var r) || r != run) return;
            if (ex is IOException && await Task.Run(() => engine.WaitForExit(500))) return; // Exited says why
            EndRun(run, kill: true);
            _board?.RunEnded(job, false, $"It couldn't start ({ex.Message}). Send it a message to try again.", DateTime.UtcNow);
            ShowJobCards();
        }
    }

    private void OnJobEvent(JobRun run, ChatEvent e)
    {
        var board = _board;
        if (board is null) return;
        var now = DateTime.UtcNow;
        switch (e)
        {
            case InitEvent init:
                if (init.SessionId.Length > 0) JobSessions.Set(JobsFolder(_conv), run.Job.Key, init.SessionId);
                if (init.Servers.All(s => s.Name != ClaudeCli.ServerName))
                    board.AddLine(run.Job, "note", "The Revit tools didn't load for this job. Run Install.cmd from the Sketch ProMax download again.", "", now);
                break;
            case AssistantTextEvent text when text.Text.Trim().Length > 0:
                run.LastText = text.Text.Trim();
                board.AddLine(run.Job, "text", run.LastText, "", now);
                break;
            case ToolUseEvent tool when !run.Steps.ContainsKey(tool.Id):
            {
                var name = StreamParser.ShortName(tool.Name);
                run.Steps[tool.Id] = board.AddLine(run.Job, "step", StepNames.Line(name, StepNames.Describe(name, tool.InputJson)), "running", now);
                break;
            }
            case ToolResultEvent result when run.Steps.TryGetValue(result.ToolUseId, out var step):
                step.State = result.IsError ? "failed" : "done";
                break;
            case ResultEvent end:
                OnJobResult(run, end);
                return;
            default:
                return;
        }
        ShowJobCard(run.Job.Number);
        SaveSoon();
    }

    private void OnJobResult(JobRun run, ResultEvent end)
    {
        var board = _board!;
        var now = DateTime.UtcNow;
        if (end.SessionId.Length > 0) JobSessions.Set(JobsFolder(_conv), run.Job.Key, end.SessionId);
        var message = end.Text.Trim().Length > 0 ? end.Text.Trim() : string.Join(" ", end.Errors);
        if (end.IsError && run.Resumed && !run.Retried && message.IndexOf("No conversation found", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Restart(run);
            return;
        }
        string summary;
        if (!end.IsError) summary = message.Length > 0 ? message : run.LastText;
        else if (System.Text.RegularExpressions.Regex.IsMatch(message, @"usage limit|rate limit|limit reached|out of (extra )?usage", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            summary = $"Your plan's usage limit is reached: {message}. Send it a message to carry on when the limit resets.";
        else if (LooksSignedOut(message)) summary = "You're not signed in. Press Sign in at the top of the chat, then send this job a message to carry on.";
        else summary = (message.Length > 0 ? message : "It reported an error.") + " Send it a message to try again.";
        // Messages sent while it ran: it carries on with them in the same process.
        if (board.RunEnded(run.Job, !end.IsError, summary, now) is { } next)
        {
            run.Steps.Clear();
            _ = run.Engine.SendAsync(StreamInput.UserMessage(next, [])).ContinueWith(_ => { }, TaskScheduler.Default);
            ShowJobCard(run.Job.Number);
            SaveSoon();
            return;
        }
        if (end.IsError) board.AddLine(run.Job, "note", summary, "", now);
        EndRun(run, kill: false);
        Report(run.Job);
    }

    private void OnJobExited(JobRun run, string tail)
    {
        if (_board is not { } board) return;
        if (!run.Engine.Initialized && run.Resumed && !run.Retried)
        {
            Restart(run); // its session couldn't be reopened (deleted, or from another PC)
            return;
        }
        EndRun(run, kill: false);
        if (run.Job.State == JobBoard.Stopped) return;
        var detail = Last(tail, 300);
        var why = LooksSignedOut(tail) ? "You're not signed in. Press Sign in at the top of the chat, then send this job a message to carry on."
            : "It stopped unexpectedly." + (detail.Length > 0 ? " " + detail : "") + " Send it a message to try again.";
        board.RunEnded(run.Job, false, why, DateTime.UtcNow);
        board.AddLine(run.Job, "note", why, "", DateTime.UtcNow);
        Report(run.Job);
    }

    /// <summary>The job's session is gone: the same run starts again from its task in a new session.</summary>
    private void Restart(JobRun run)
    {
        EndRun(run, kill: true);
        _board!.AddLine(run.Job, "note", "Its earlier session couldn't be reopened, so it starts again from its task.", "", DateTime.UtcNow);
        _ = StartRun(run.Job, run.Message, fresh: true).ContinueWith(_ => { }, TaskScheduler.Default);
        if (_runs.TryGetValue(run.Job.Key, out var again)) again.Retried = true;
    }

    /// <summary>The run is over (its process ends): the next waiting job may start.</summary>
    private void EndRun(JobRun run, bool kill)
    {
        if (_runs.TryGetValue(run.Job.Key, out var r) && r == run) _runs.Remove(run.Job.Key);
        if (kill) run.Engine.Kill();
        else run.Engine.Dispose();
        ShowJobCard(run.Job.Number);
        SaveSoon();
        if (_runs.Count == 0) _jobClock?.Stop();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            PumpJobs();
            // A model switch held while jobs ran happens once they all ended.
            if (!JobsOpen && _turn is null && _pendingModel is { } next)
            {
                _pendingModel = null;
                SwitchTo(next.Key, next.Title);
            }
        }));
    }

    // ---- Reporting back to the main chat ----------------------------------------------------------------------------

    /// <summary>A job finished (or failed): its summary goes to the main chat soon, several at once when they end together.</summary>
    private void Report(ChatJob job)
    {
        if (job.State is not (JobBoard.Done or JobBoard.Failed)) return;
        if (!_reports.Contains(job.Number)) _reports.Add(job.Number);
        _reportTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1.5) };
        _reportTimer.Tick -= OnReportTick;
        _reportTimer.Tick += OnReportTick;
        _reportTimer.Stop();
        _reportTimer.Start();
    }

    private void OnReportTick(object? sender, EventArgs e)
    {
        _reportTimer?.Stop();
        FlushReports();
    }

    /// <summary>Sends the finished jobs' reports as the main chat's next turn, once it is free (EndTurn calls this again).</summary>
    private void FlushReports()
    {
        if (_reports.Count == 0 || _board is null || _turn is not null || _stopped || _setup != Setup.Ready || _sample) return;
        var jobs = _board.Snapshot();
        var done = _reports.Select(n => jobs.FirstOrDefault(j => j.Number == n)).OfType<ChatJob>().Where(j => j.State is JobBoard.Done or JobBoard.Failed).ToList();
        _reports.Clear();
        if (done.Count == 0) return;
        var open = jobs.Count(j => j.Active || j.State == JobBoard.Queued);
        Note(done.Count == 1 ? $"Job {done[0].Number} finished and reported back." : $"Jobs {string.Join(", ", done.Select(j => j.Number))} finished and reported back.");
        _ = RunTurn(JobBoard.Report(done, open), [], "");
    }

    // ---- Cards ------------------------------------------------------------------------------------------------------

    /// <summary>The element for a "pjob" item: the job's card, drawn from the board.</summary>
    private UIElement ParallelJobCardFor(ChatItem item)
    {
        int.TryParse(item.Name, out var number);
        var card = new ParallelJobCard(number);
        card.StopPressed += StopJob;
        card.MessageSent = MessageJob;
        card.Opened += ShowJobCard;
        _jobCards[number] = card;
        if (_board?.Snapshot().FirstOrDefault(j => j.Number == number) is { } job) card.Render(job, DateTime.UtcNow, ChatJobs.Limit);
        return card;
    }

    /// <summary>
    /// A new job gets its card in the chat (where it was started), and every card shows its job as it is now. Only open
    /// jobs get one: a finished job's card that a long chat let go of doesn't come back at the end.
    /// </summary>
    private void ShowJobCards()
    {
        if (_board is null) return;
        var jobs = _board.Snapshot();
        foreach (var job in jobs.Where(j => j.Active || j.State == JobBoard.Queued))
            if (!_conv.Items.Any(i => i.Kind == "pjob" && i.Name == job.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                Add(new ChatItem { Kind = "pjob", Name = job.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), Text = job.Title });
        var limit = ChatJobs.Limit;
        foreach (var job in jobs)
            if (_jobCards.TryGetValue(job.Number, out var card)) card.Render(job, DateTime.UtcNow, limit);
    }

    private void ShowJobCard(int number)
    {
        if (_board?.Find(number) is null || !_jobCards.TryGetValue(number, out var card)) return;
        if (_board.Snapshot().FirstOrDefault(j => j.Number == number) is { } job) card.Render(job, DateTime.UtcNow, ChatJobs.Limit);
    }

    /// <summary>While jobs run, their cards' clocks move.</summary>
    private void StartJobClock()
    {
        _jobClock ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _jobClock.Tick -= OnJobClock;
        _jobClock.Tick += OnJobClock;
        _jobClock.Start();
    }

    private void OnJobClock(object? sender, EventArgs e)
    {
        if (_runs.Count == 0) { _jobClock?.Stop(); return; }
        foreach (var run in _runs.Values) ShowJobCard(run.Job.Number);
    }

    /// <summary>Stop on a card.</summary>
    private void StopJob(int number)
    {
        if (_board?.Stop(number, DateTime.UtcNow) is null) return;
        if (_runs.Values.FirstOrDefault(r => r.Job.Number == number) is { } run) EndRun(run, kill: true);
        ShowJobCard(number);
        Status($"Job {number} stopped. A change it had already started in Revit finishes on its own.", spin: false);
        SaveSoon();
    }

    /// <summary>A message typed on a card: to the job (it carries on with it), and how that went.</summary>
    private (bool Ok, string Said) MessageJob(int number, string text)
    {
        if (_board is null || _stopped) return (false, "The chat isn't running. Restart Revit, then try again.");
        if (_setup != Setup.Ready) return (false, "The chat isn't set up yet: finish the step at the top of the chat, then send again.");
        if (!ClaudeCli.JobsAvailable) return (false, "Jobs need the Sketch ProMax connector as new as the add-in. Run Install.cmd again, then send again.");
        var sent = _board.Message(number, text, DateTime.UtcNow);
        if (sent == JobBoard.Delivery.NotFound) return (false, "This job is no longer in the chat.");
        JobsChanged();
        return (true, sent == JobBoard.Delivery.Reopened ? "Sent. The job carries on with it." : "Sent. The job gets it when its current run ends.");
    }
}
