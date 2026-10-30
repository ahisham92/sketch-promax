using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SketchProMax.Settings;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// Where the bridge reaches the open chat's parallel jobs (chat_jobs calls and a job's change waiting its turn), from
/// its own threads. The pane sets it when a chat is shown.
/// </summary>
internal static class ChatJobs
{
    private static volatile JobBoard? _board;
    private static volatile Action? _changed;
    private static volatile bool _coordinator = true;

    /// <summary>The key (JobBoard.ChatKey) the shown chat's Claude Code passes with its calls: those are the main chat's.</summary>
    public static volatile string Shown = "";

    /// <summary>The open chat is With coordinator (ChatTypes): only then may its main chat start or message jobs.</summary>
    public static bool Coordinator { get => _coordinator; set => _coordinator = value; }

    /// <summary>The open chat's jobs (null before the pane has a chat).</summary>
    public static JobBoard? Board { get => _board; set => _board = value; }

    /// <summary>Called after the bridge changed a job (any thread): the pane starts, stops and redraws on its own thread.</summary>
    public static Action? Changed { get => _changed; set => _changed = value; }

    /// <summary>True when jobs can run: a chat is open and the connector is as new as this add-in.</summary>
    public static bool Available => _board is not null && ClaudeCli.JobsAvailable;

    /// <summary>How many jobs run at once (Settings, Chat tab).</summary>
    public static int Limit => JobBoard.ClampLimit(AgentSettings.Load().ChatJobLimit);

    /// <summary>The open chat's own model and effort choice (ChatAuto: "auto" or what was picked by hand), for new jobs.</summary>
    public static volatile string ChatModel = ChatAuto.Automatic, ChatEffort = ChatAuto.Automatic;

    /// <summary>Automatic's model and effort for each kind of work, from Settings > Chat.</summary>
    public static (Dictionary<string, string> Models, Dictionary<string, string> Efforts) AutoMaps()
    {
        var s = AgentSettings.Load();
        return (new() { [ChatAuto.Simple] = s.ChatModelSimple, [ChatAuto.Everyday] = s.ChatModelEveryday, [ChatAuto.Hard] = s.ChatModelHard, [ChatAuto.Coordinating] = s.ChatModelCoordinator },
                new() { [ChatAuto.Simple] = s.ChatEffortSimple, [ChatAuto.Everyday] = s.ChatEffortEveryday, [ChatAuto.Hard] = s.ChatEffortHard, [ChatAuto.Coordinating] = s.ChatEffortCoordinator });
    }

    /// <summary>The model and effort for a request or job (ChatAuto.Pick with the Settings' maps).</summary>
    public static AutoPick Pick(string text, string? chatModel, string? chatEffort, string? kind = null, bool documents = false, ICollection<string>? refused = null)
    {
        var (models, efforts) = AutoMaps();
        return ChatAuto.Pick(text, chatModel, chatEffort, models, efforts, kind, documents, refused);
    }

    /// <summary>A new job gets its kind, model and effort: the chat's own choice, or Automatic's for its task.</summary>
    public static void Assign(ChatJob job, string? kind)
    {
        var pick = Pick(job.Title + "\n" + job.Task, ChatModel, ChatEffort, kind);
        job.Kind = pick.Kind;
        job.Model = pick.Model;
        job.Effort = pick.Effort;
    }

    /// <summary>The bridge: job <paramref name="key"/>'s call waits its turn (<paramref name="ahead"/>: its place, 1 = next) or got it (null).</summary>
    public static void SetWaiting(string key, int? ahead)
    {
        if (_board?.SetWaiting(key, ahead) == true) _changed?.Invoke();
    }

    /// <summary>The bridge, after a job's call: the messages sent to it while it worked, to add to the result (null when none).</summary>
    public static string? TakeAsides(string key, string tool)
    {
        var block = _board?.TakeAsides(key, tool, DateTime.UtcNow);
        if (block is not null) _changed?.Invoke();
        return block;
    }

    private static volatile Action<string?, string>? _touched;

    /// <summary>The pane: told when the chat or one of its jobs works on a model (by title), to name it in the header.</summary>
    public static Action<string?, string>? TouchedModel { get => _touched; set => _touched = value; }

    /// <summary>The connection: the chat (<paramref name="key"/>: its key or a job's) works on model <paramref name="title"/> now.</summary>
    public static void Touched(string? key, string title) => _touched?.Invoke(key, title);

    /// <summary>The connection: the model job <paramref name="key"/> was given (chat_jobs start model=…), or null.</summary>
    public static string? ModelOf(string key) => _board?.FindKey(key) is { RevitModel.Length: > 0 } job ? job.RevitModel : null;

    /// <summary>
    /// The bridge: true when job <paramref name="key"/> may only read: it checks another job's work (JobReview), reviews an
    /// autopilot's goal (GoalReview) or reads before an autopilot's plan is approved. Its changes are refused.
    /// </summary>
    public static bool IsCheck(string key) => _board?.FindKey(key) is { ReviewOf: > 0 } or { ReadOnly: true };

    /// <summary>The bridge: what job <paramref name="key"/> does in Revit now (JobBoard.SetActivity), for its card and animation.</summary>
    public static void SetActivity(string key, string activity, int ahead = 0)
    {
        if (_board?.SetActivity(key, activity, ahead) == true) _changed?.Invoke();
    }
}

/// <summary>
/// The chat as coordinator of parallel jobs (JobBoard): the main chat starts jobs with chat_jobs, each runs as its own
/// hidden Claude Code session (ClaudeCli.JobStart, its session in the chat's jobs folder, sessions.json) with a card in
/// the conversation ("pjob" item) and its own view: selecting a job (its card's Open job, or the switcher above the
/// conversation) shows the job's whole conversation in place of the main chat's, and the message box then talks to that
/// job, attachments included; Back to coordinator (or Esc) returns. Up to the Settings' number run at once; the rest wait
/// to start. A job that finishes reports back: its summary goes to the main chat as its next turn.
/// </summary>
internal sealed partial class ChatPane
{
    /// <summary>One job's run: its Claude Code process and what the run has said so far.</summary>
    private sealed class JobRun
    {
        public ChatJob Job = null!;
        public ChatEngine Engine = null!;
        /// <summary>The message the run started with (sent again from the task when its session is gone).</summary>
        public JobMessage Message = new("", []);
        public bool Resumed, Retried;
        public string LastText = "";
        public readonly Dictionary<string, ChatItem> Steps = new();
        /// <summary>When this run (or its carrying on with new messages) started, and the dollars its process reported so far: for CrewStats.</summary>
        public DateTime RunStart = DateTime.UtcNow;
        public double CostSeen;
        /// <summary>The whole input of each plan step under way (the item keeps a shortened copy), for JobPlan.</summary>
        public readonly Dictionary<string, string> PlanInputs = new();
    }

    private JobBoard? _board;
    private List<ChatJob>? _boardJobs;
    private readonly Dictionary<string, JobRun> _runs = new();
    private readonly Dictionary<int, ParallelJobCard> _jobCards = new();
    private readonly List<int> _reports = [];
    private DispatcherTimer? _reportTimer, _jobClock;
    private bool _jobsQueued;

    // The job view: which job is shown (null: the main chat), its drawn items, the switcher and the job's bar.
    private int? _viewJob;
    private readonly StackPanel _jobMessages = new();
    private readonly List<ChatItem> _jobDrawn = [];
    private ScrollViewer? _jobScroll;
    private readonly Border _switcher = new() { Visibility = Visibility.Collapsed };
    private readonly WrapPanel _switchList = new();
    private string _switchShown = "";
    private readonly Border _jobBar = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _jobBarTitle = new() { FontSize = ChatUi.SmallSize, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _jobBarState = new() { FontSize = ChatUi.SmallSize, TextWrapping = TextWrapping.Wrap };
    private Button? _jobBack, _jobStop;

    /// <summary>True while any of the chat's jobs runs or waits to start.</summary>
    private bool JobsOpen => _board is { } board && (board.OpenCount > 0 || _runs.Count > 0);

    /// <summary>The shown chat is With coordinator (ChatTypes).</summary>
    private bool IsCoordinator => ChatTypes.Of(_conv) == ChatTypes.Coordinator;

    private static string JobsFolder(Conversation conv) => Path.Combine(ClaudeCli.Folder, "jobs", conv.Id.Length > 0 ? conv.Id : "chat");

    /// <summary>The shown chat's jobs: a chat opened from disk has none running (those saved running are stopped).</summary>
    private void AttachJobs()
    {
        // A chat saved before kinds existed gets its kind now (ChatTypes.Of), so it keeps it.
        _conv.Jobs ??= [];
        _conv.Type = ChatTypes.Of(_conv);
        ChatJobs.Coordinator = IsCoordinator;
        if (_board is not null && ReferenceEquals(_boardJobs, _conv.Jobs)) return;
        EndRuns("The chat was left, so this job stopped. Open the chat again and send it a message to carry on.");
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
        ChatJobs.Coordinator = IsCoordinator;
        ChatJobs.Changed = () => Dispatcher.BeginInvoke(new Action(JobsChanged));
        ChatJobs.TouchedModel = (key, title) => Dispatcher.BeginInvoke(new Action(() => ModelTouched(key, title)));
    }

    /// <summary>The chat or one of its jobs works on another model: the header names it with the chat's own.</summary>
    private void ModelTouched(string? key, string title)
    {
        if (_sample || title.Trim().Length == 0 || string.Equals(title, _conv.Title, StringComparison.OrdinalIgnoreCase)) return;
        // Only this chat's own calls: its main chat or one of its jobs (a chat left in the background keeps its own).
        if (key is not null && key != JobBoard.ChatKey(_conv.Id) && _board?.FindKey(key) is null) return;
        _conv.Models ??= [];
        if (_conv.Models.Any(m => string.Equals(m, title, StringComparison.OrdinalIgnoreCase))) return;
        _conv.Models.Add(title);
        ShowTitle();
        SaveSoon();
    }

    /// <summary>Ends every running job's process (the chat is left, Revit closes, the core switches versions).</summary>
    private void EndRuns(string note, bool now = false)
    {
        foreach (var run in _runs.Values.ToList())
        {
            _board?.Stop(run.Job.Number, DateTime.UtcNow);
            if (note.Length > 0) _board?.AddNote(run.Job, note, DateTime.UtcNow);
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
            RefreshJobView();
            SaveSoon();
        }));
    }

    /// <summary>Starts the jobs waiting to start, while fewer than the Settings' number run (never in a Simple chat).</summary>
    private void PumpJobs()
    {
        if (_board is null || _sample || _stopped || _setup != Setup.Ready || _install is null || !IsCoordinator) return;
        if (Run is { State: AutopilotRun.Paused }) return; // an autopilot paused at a usage limit starts nothing new
        if (Run is { Updating.Length: > 0 }) return; // nor one waiting for an update to go in
        if (Run is { State: AutopilotRun.Planning or AutopilotRun.Approval }) _board.ReadOnlyUntilStarted(); // reading before the plan is approved
        foreach (var (job, message) in _board.ToStart(ChatJobs.Limit, DateTime.UtcNow)) _ = StartRun(job, message);
        if (_runs.Count > 0) StartJobClock();
    }

    /// <summary>The stream-json line for a job's message: its text, and its attached files the way the main chat sends them.</summary>
    private static string JobLine(JobMessage message)
    {
        var files = message.Files.Where(File.Exists).Select(f => new Attachment { Path = f, Name = Path.GetFileName(f) }).ToList();
        // The active view and the selection (a job keeps no count of changes since an answer: its task says what is known).
        var text = message.Text + Revit.ChatModelWatch.Note(CurrentModelKey, changes: false);
        if (files.Count == 0) return StreamInput.UserMessage(text, []);
        try { return BuildMessage(text, files, new AttachedSoFar()); }
        catch (Exception ex)
        {
            return StreamInput.UserMessage(message.Text + $"\n\n(The attached files couldn't be read here: {ex.Message} Their paths: " + string.Join("; ", message.Files) + ")", []);
        }
    }

    private async Task StartRun(ChatJob job, JobMessage message, bool fresh = false)
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
        if (job.Model.Length == 0) ChatJobs.Assign(job, null); // a job saved before the pickers
        var install = _install!;
        var model = job.Model;
        var effort = ClaudeCli.EffortRefused ? "" : job.Effort;
        var key = job.Key;
        var engine = new ChatEngine(Dispatcher, install, resume, model, () => ClaudeCli.JobStart(install, key, resume, session, model, effort), effort: effort, job: key);
        var run = new JobRun { Job = job, Engine = engine, Message = message, Resumed = resume is not null };
        _runs[key] = run;
        engine.Event += e => { if (_runs.TryGetValue(key, out var r) && r == run) OnJobEvent(run, e); };
        engine.Exited += tail => { if (_runs.TryGetValue(key, out var r) && r == run) OnJobExited(run, tail); };
        ShowJobCards();
        ShowSwitcher();
        try
        {
            var line = await Task.Run(() => JobLine(message));
            if (!_runs.TryGetValue(key, out var still) || still != run) return; // stopped meanwhile
            await engine.StartAsync(line);
        }
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
                var address = run.Engine.McpAddress;
                var server = init.Servers.FirstOrDefault(s => s.Name == ClaudeCli.ServerName);
                if (server.Name is null)
                    board.AddNote(run.Job, ToolsNote.Missing(inside: address.Length > 0, job: true), now);
                else if (server.Status is not ("connected" or "pending" or ""))
                {
                    var status = server.Status;
                    _ = Task.Run(() => ClaudeCli.ToolsFailed(status, address, job: true)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (_board == board) board.AddNote(run.Job, t.Result, DateTime.UtcNow); }
                        catch (Exception) { /* on Revit's UI thread: a note that can't be added must not escape */ }
                    })), TaskContinuationOptions.OnlyOnRanToCompletion);
                }
                break;
            case AssistantTextEvent text when text.Text.Trim().Length > 0:
                run.LastText = text.Text.Trim();
                board.AddItem(run.Job, new ChatItem { Kind = "assistant", Text = run.LastText }, now);
                break;
            case ToolUseEvent tool when !run.Steps.ContainsKey(tool.Id):
            {
                var name = StreamParser.ShortName(tool.Name);
                var summary = StepNames.Describe(name, tool.InputJson);
                run.Steps[tool.Id] = board.AddItem(run.Job, new ChatItem
                {
                    Kind = "tool", Name = name, State = "running", Text = summary, Input = tool.InputJson.Length > 0 ? Pretty(tool.InputJson) : "",
                }, now);
                run.Job.Now = JobBoard.OneLine(StepNames.Line(name, summary), 120);
                if (JobPlan.IsPlanTool(name)) run.PlanInputs[tool.Id] = tool.InputJson;
                break;
            }
            case ToolResultEvent result when run.Steps.TryGetValue(result.ToolUseId, out var step):
            {
                var state = result.IsError ? "failed" : "done";
                if (_viewJob == run.Job.Number && _rows.TryGetValue(step, out var row)) SetTool(row, state, step.Text, result.Text);
                else
                {
                    step.State = state;
                    if (result.Text.Length > 0) step.Result = result.Text.Length > MaxResult ? result.Text.Substring(0, MaxResult) + "…" : result.Text;
                }
                if (!result.IsError && FilesItem(step.Name, result.Text) is { } files) board.AddItem(run.Job, files, now);
                // The plan moved on: the worker's progress in the crew strip follows.
                if (run.PlanInputs.TryGetValue(result.ToolUseId, out var planInput))
                {
                    run.PlanInputs.Remove(result.ToolUseId);
                    if (!result.IsError && board.UpdatePlan(run.Job, step.Name, planInput, result.Text)) ShowSwitcher();
                }
                break;
            }
            case ResultEvent end:
                OnJobResult(run, end);
                return;
            case UsageEvent usage:
                OnUsage(usage.Usage, run.Job.Key); // a job's answers report the plan's usage too, and count to its budget
                return;
            default:
                return;
        }
        ShowJobCard(run.Job.Number);
        RefreshJobView();
        SaveSoon();
    }

    private void OnJobResult(JobRun run, ResultEvent end)
    {
        var board = _board!;
        var now = DateTime.UtcNow;
        if (end.SessionId.Length > 0) JobSessions.Set(JobsFolder(_conv), run.Job.Key, end.SessionId);
        // The worker's numbers: this run's time, tokens and dollars.
        CrewStats.Add(new CrewRecord
        {
            Start = run.RunStart, End = now, Role = CrewRecord.Worker, Slot = run.Job.Worker, Chat = _conv.Id, Job = run.Job.Number,
            In = end.InputTokens, Out = end.OutputTokens, Usd = CrewStats.Rise(ref run.CostSeen, end.CostUsd), Ok = !end.IsError,
        });
        run.RunStart = now;
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
            run.Message = next;
            _ = Task.Run(() => JobLine(next)).ContinueWith(t => run.Engine.SendAsync(t.Result), TaskScheduler.Default).Unwrap().ContinueWith(_ => { }, TaskScheduler.Default);
            ShowJobCard(run.Job.Number);
            RefreshJobView();
            SaveSoon();
            return;
        }
        if (end.IsError) board.AddItem(run.Job, new ChatItem { Kind = "error", Text = summary }, now);
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
        if (!run.Engine.Initialized && run.Engine.Effort.Length > 0 && ChatAuto.IsEffortRefusal(tail))
        {
            // An older Claude Code without --effort: the same run again without it.
            ClaudeCli.EffortRefused = true;
            board.AddNote(run.Job, "This Claude Code doesn't take an effort setting, so the job runs at its default effort.", DateTime.UtcNow);
            _ = StartRun(run.Job, run.Message, fresh: !run.Resumed).ContinueWith(_ => { }, TaskScheduler.Default);
            return;
        }
        var detail = Last(tail, 300);
        var why = LooksSignedOut(tail) ? "You're not signed in. Press Sign in at the top of the chat, then send this job a message to carry on."
            : "It stopped unexpectedly." + (detail.Length > 0 ? " " + detail : "") + " Send it a message to try again.";
        board.RunEnded(run.Job, false, why, DateTime.UtcNow);
        board.AddItem(run.Job, new ChatItem { Kind = "error", Text = why }, DateTime.UtcNow);
        RefreshJobView();
        Report(run.Job);
    }

    /// <summary>The job's session is gone: the same run starts again from its task in a new session.</summary>
    private void Restart(JobRun run)
    {
        EndRun(run, kill: true);
        _board!.AddNote(run.Job, "Its earlier session couldn't be reopened, so it starts again from its task.", DateTime.UtcNow);
        _ = StartRun(run.Job, run.Message, fresh: true).ContinueWith(_ => { }, TaskScheduler.Default);
        if (_runs.TryGetValue(run.Job.Key, out var again)) again.Retried = true;
    }

    /// <summary>The run is over (its process ends): the next waiting job may start.</summary>
    private void EndRun(JobRun run, bool kill)
    {
        if (_runs.TryGetValue(run.Job.Key, out var r) && r == run) _runs.Remove(run.Job.Key);
        if (kill) run.Engine.Kill();
        else run.Engine.Dispose();
        // Steps still turning when the run ended: they won't finish now.
        foreach (var step in run.Steps.Values.Where(s => s.State == "running"))
        {
            if (_viewJob == run.Job.Number && _rows.TryGetValue(step, out var row)) SetTool(row, "stopped", step.Text, "");
            else step.State = "stopped";
        }
        ShowJobCard(run.Job.Number);
        RefreshJobView();
        ShowSwitcher();
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

    /// <summary>The 5-hour limit rose with job <paramref name="key"/>'s request: counted to it, and over its budget it pauses (JobBudget).</summary>
    private void ChargeJob(string key, double rise)
    {
        double budget;
        // An autopilot's jobs have their own budget (Settings > Chat > Autopilot).
        try { budget = JobBudget.Share(Run is { Active: true } ? AgentSettings.Load().AutopilotJobBudget : AgentSettings.Load().ChatJobBudget); }
        catch (Exception) { budget = JobBudget.Share(JobBudget.DefaultPercent); }
        if (_board?.Charge(key, rise, budget, DateTime.UtcNow) is not { } job) return;
        if (_runs.TryGetValue(key, out var run)) EndRun(run, kill: true);
        var text = $"Job {job.Number} paused: it used {JobBudget.Percent(job.Spent - job.BudgetFrom)} of your 5-hour limit, its budget (Settings > Chat). " +
                   "Send it a message to let it carry on.";
        Note(text);
        Status(text, spin: false);
        Report(job);
    }

    // ---- Reporting back to the main chat ----------------------------------------------------------------------------

    /// <summary>A job finished (or failed): its summary goes to the main chat soon, several at once when they end together.</summary>
    private void Report(ChatJob job)
    {
        var now = DateTime.UtcNow;
        // A goal reviewer's report goes to the run (GoalReview), not to the coordinator's chat.
        if (job.GoalCheck > 0)
        {
            if (!job.Open) GoalCheckEnded(job);
            return;
        }
        // A teammate's check ended: the job it checked is reported now, with the verdict.
        if (job.ReviewOf > 0)
        {
            if (_board?.ReviewEnded(job, now) is { } checkedJob) Report(checkedJob);
            return;
        }
        if (job.State is not (JobBoard.Done or JobBoard.Failed) && !job.OverBudget || job.Review == JobReview.Pending) return;
        // Finished: a second worker checks the work in the model first (Settings > Chat).
        if (job.State == JobBoard.Done && _board is { } board && JobReview.Wanted(AgentSettings.Load().ChatJobReview, Run is { Active: true }, job)
            && board.StartReview(job.Number, now) is { } check)
        {
            ChatJobs.Assign(check, ChatAuto.Everyday);
            ShowJobCards();
            PumpJobs();
            SaveSoon();
            return;
        }
        RoutineEnded(job);
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
        if (Run is { State: AutopilotRun.Paused }) return; // reviewed when the run carries on
        var jobs = _board.Snapshot();
        var done = _reports.Select(n => jobs.FirstOrDefault(j => j.Number == n)).OfType<ChatJob>().Where(j => j.State is JobBoard.Done or JobBoard.Failed || j.OverBudget).ToList();
        _reports.Clear();
        if (done.Count == 0) return;
        var open = jobs.Count(j => j.Active || j.State == JobBoard.Queued);
        Note(done.Count == 1 ? $"Job {done[0].Number} finished and reported back." : $"Jobs {string.Join(", ", done.Select(j => j.Number))} finished and reported back.");
        var report = JobBoard.Report(done, open);
        // An autopilot reviews the reports and carries on instead of telling the user.
        if (Run is { State: AutopilotRun.Running } run && report.LastIndexOf("Tell the user", StringComparison.Ordinal) is var cut and > 0)
        {
            report = report.Substring(0, cut) + Autopilot.Review(run);
            run.Idle = 0;
        }
        // Reading before the plan: the plan comes once every reading job has reported.
        else if (Run is { State: AutopilotRun.Planning } && report.LastIndexOf("Tell the user", StringComparison.Ordinal) is var at and > 0)
            report = report.Substring(0, at) + Autopilot.PlanReports(open);
        _ = RunTurn(report, [], "");
    }

    private DateTime _watchedAt;

    /// <summary>
    /// Every 30 s while jobs run (JobWatch): a job that wrote its report but never ended is finished with it, and one with
    /// no step for 20 minutes is stopped and reported, so the coordinator hands its work out again and nothing waits on it.
    /// </summary>
    private void WatchJobs()
    {
        _watchedAt = DateTime.UtcNow;
        if (_board is not { } board) return;
        ShareWithFreeWorkers(board);
        foreach (var run in _runs.Values.ToList())
        {
            var now = DateTime.UtcNow;
            var verdict = JobWatch.Judge(run.Job, now);
            if (verdict == JobWatch.Fine) continue;
            EndRun(run, kill: true);
            if (verdict == JobWatch.Finished)
            {
                var report = run.Job.Items.Last(i => i.Kind == "assistant").Text.Trim();
                board.AddNote(run.Job, JobWatch.FinishedNote, now);
                board.RunEnded(run.Job, true, report, now);
            }
            else
            {
                var why = JobWatch.StuckReport(run.Job);
                board.RunEnded(run.Job, false, why, now);
                board.AddItem(run.Job, new ChatItem { Kind = "error", Text = $"No progress for {JobWatch.Stuck.TotalMinutes:0} minutes, so it was stopped." }, now);
            }
            Tools.ActivityLog.Note("job_watch", $"job {run.Job.Number} {verdict}");
            Report(run.Job);
        }
    }

    private readonly HashSet<int> _shareOffered = [];

    /// <summary>
    /// A worker is free while a running job has steps it hasn't started (FreeWorkers): the coordinator is told to hand
    /// some to the free worker, once per job. Not while the coordinator is answering, nor in a paused or planning run.
    /// </summary>
    private void ShareWithFreeWorkers(JobBoard board)
    {
        if (_turn is not null || _reports.Count > 0 || _stopped || _sample || !IsCoordinator) return;
        if (Run is { State: not AutopilotRun.Running and not AutopilotRun.Done and not AutopilotRun.Stopped }) return;
        var jobs = board.Snapshot();
        if (FreeWorkers.ToShare(jobs, ChatJobs.Limit, _shareOffered) is not { } job) return;
        _shareOffered.Add(job.Number);
        _ = RunTurn(FreeWorkers.Nudge(job, FreeWorkers.Free(jobs, ChatJobs.Limit)), [], "");
    }

    // ---- Cards ------------------------------------------------------------------------------------------------------

    /// <summary>The element for a "pjob" item: the job's card, drawn from the board.</summary>
    private UIElement ParallelJobCardFor(ChatItem item)
    {
        int.TryParse(item.Name, out var number);
        var card = new ParallelJobCard(number);
        card.StopPressed += StopJob;
        card.Opened += OpenJobView;
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
        ShowSwitcher();
        ShowJobBar();
    }

    private void ShowJobCard(int number)
    {
        if (_board is null) return;
        if (_jobCards.TryGetValue(number, out var card) && _board.Snapshot().FirstOrDefault(j => j.Number == number) is { } job)
            card.Render(job, DateTime.UtcNow, ChatJobs.Limit);
        if (_viewJob == number) ShowJobBar();
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
        if (DateTime.UtcNow - _watchedAt > TimeSpan.FromSeconds(30)) WatchJobs();
        foreach (var run in _runs.Values) ShowJobCard(run.Job.Number);
        ShowSwitcher();
    }

    /// <summary>Stop on a card or in the job's view.</summary>
    private void StopJob(int number)
    {
        if (_board?.Stop(number, DateTime.UtcNow) is null) return;
        if (_runs.Values.FirstOrDefault(r => r.Job.Number == number) is { } run) EndRun(run, kill: true);
        if (_board.Find(number) is { } stopped)
        {
            // A stopped check: the job it checked is reported without it. A stopped routine run: its history says so.
            if (stopped.ReviewOf > 0 && _board.ReviewEnded(stopped, DateTime.UtcNow) is { } checkedJob) Report(checkedJob);
            // A stopped goal reviewer: its round is judged without it (it counts as not able to tell).
            if (stopped.GoalCheck > 0) GoalCheckEnded(stopped);
            RoutineEnded(stopped);
        }
        ShowJobCard(number);
        RefreshJobView();
        ShowSwitcher();
        Status($"Job {number} stopped. A change it had already started in Revit finishes on its own.", spin: false);
        SaveSoon();
    }

    // ---- The job view and the switcher ------------------------------------------------------------------------------

    /// <summary>
    /// Builds the switcher (Coordinator and one button per job, with its state) and the job's bar (Back to coordinator, the
    /// job's name and state, Stop), docked over the conversation, and the job view's own scrolling list in the centre.
    /// </summary>
    private UIElement BuildJobView()
    {
        var top = new StackPanel { Margin = new Thickness(ChatUi.S3, 0, ChatUi.S3, ChatUi.S1) };
        KeyboardNavigation.SetTabNavigation(top, KeyboardNavigationMode.Local);

        // The switcher: one row of pills that wraps.
        _switchList.Margin = new Thickness(0);
        AutomationProperties.SetName(_switchList, "Chats of this chat: the coordinator and its jobs");
        _switcher.Child = _switchList;
        _switcher.Padding = new Thickness(0, 0, 0, ChatUi.S1);
        top.Children.Add(_switcher);
        _switcherHome = top; // the large window moves it into its jobs column (ShowBig)

        // The job's bar: Back to coordinator, the job's name and state, Stop.
        var bar = new DockPanel();
        _jobBack = new Button { Content = ChatUiRow(ChatUi.Icon(ChatUi.Paths.Back, 14), "Back to coordinator"), Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        _jobBack.SetResourceReference(StyleProperty, "SpmChatPill");
        _jobBack.ToolTip = "Back to the main chat that hands out the jobs (Esc). The job keeps working.";
        AutomationProperties.SetName(_jobBack, "Back to coordinator");
        AutomationProperties.SetHelpText(_jobBack, (string)_jobBack.ToolTip);
        _jobBack.Click += (_, _) => ShowCoordinator(focus: true);
        DockPanel.SetDock(_jobBack, Dock.Left);
        bar.Children.Add(_jobBack);
        _jobStop = new Button { Content = "Stop job", MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ChatUi.S2, 0, 0, 0) };
        _jobStop.SetResourceReference(StyleProperty, "SpmChatDanger");
        _jobStop.ToolTip = "Stops this job. A change it already started in Revit finishes on its own; send it a message later to carry on.";
        AutomationProperties.SetHelpText(_jobStop, (string)_jobStop.ToolTip);
        _jobStop.Click += (_, _) => { if (_viewJob is { } n) StopJob(n); };
        DockPanel.SetDock(_jobStop, Dock.Right);
        bar.Children.Add(_jobStop);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Brand.Fg(_jobBarTitle, Brand.K.Text);
        names.Children.Add(_jobBarTitle);
        AutomationProperties.SetLiveSetting(_jobBarState, AutomationLiveSetting.Polite);
        names.Children.Add(_jobBarState);
        bar.Children.Add(names);
        var card = new Border { Child = bar, Padding = new Thickness(ChatUi.S2), CornerRadius = new CornerRadius(ChatUi.RadiusSmall), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.AccentSoft);
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        _jobBar.Child = card;
        top.Children.Add(_jobBar);

        // The job's conversation scrolls on its own, in the conversation's place.
        _jobMessages.Margin = new Thickness(ChatUi.S4, 0, ChatUi.S4, ChatUi.S8);
        _jobScroll = new ScrollViewer
        {
            Content = _jobMessages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true, IsTabStop = true, Visibility = Visibility.Collapsed,
        };
        _jobScroll.SetResourceReference(FocusVisualStyleProperty, "SpmChatFocus");
        AutomationProperties.SetHelpText(_jobScroll, "Arrow keys and Page Up/Down scroll. Escape goes back to the coordinator.");
        return top;
    }

    private static StackPanel ChatUiRow(UIElement icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is FrameworkElement f) f.Margin = new Thickness(0, 0, ChatUi.S1, 0);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>The colour of a job's state, as its card shows it.</summary>
    private static string StateColor(ChatJob job) => job.State switch
    {
        JobBoard.Done => Brand.K.SuccessText,
        JobBoard.Failed => Brand.K.DangerText,
        JobBoard.Waiting => Brand.K.WarningText,
        JobBoard.Running => Brand.K.Text,
        _ => Brand.K.Muted,
    };

    /// <summary>
    /// The switcher shows while the chat has jobs: Coordinator first, then each job with its state; the one shown is
    /// marked. Rebuilt only when something on it changed, so the keyboard stays where it was.
    /// </summary>
    private void ShowSwitcher()
    {
        var jobs = _board?.Snapshot() ?? [];
        if (_viewJob is { } shownJob && jobs.All(j => j.Number != shownJob)) ShowCoordinator(focus: false);
        // The side pane lists only the open jobs; the large window's column lists the finished ones too, under them.
        var (listed, open) = JobBoard.ForSwitcher(jobs, _jobsSide, _viewJob);
        _switcher.Visibility = listed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSideEmpty(jobs.Count);
        ShowCrew(jobs);
        var key = (_viewJob?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "c") + "|" + string.Join("|", listed.Select(j => j.Number + j.Title + JobBoard.StateLabel(j) + j.Ahead + CrewWorker(j)));
        if (key == _switchShown) return;
        _switchShown = key;
        var focused = _switchList.Children.OfType<Button>().FirstOrDefault(b => b.IsKeyboardFocused) is { } had ? AutomationProperties.GetAutomationId(had) : null;
        _switchList.Children.Clear();
        if (listed.Count == 0) return;
        Button Pill(string id, UIElement content, string name, string help, bool selected, Action click)
        {
            var b = new Button { Content = content, Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1), Padding = new Thickness(ChatUi.S2, 2, ChatUi.S2, 2), ToolTip = help };
            // In the large window's jobs column: one full-width row each.
            if (_jobsSide)
            {
                b.Width = SideWidth - 2 * ChatUi.S3 - ChatUi.S1;
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
                b.Padding = new Thickness(ChatUi.S2, ChatUi.S1, ChatUi.S2, ChatUi.S1);
            }
            b.SetResourceReference(StyleProperty, "SpmChatPill");
            b.SetResourceReference(BackgroundProperty, selected ? Brand.K.AccentSoft : Brand.K.Raised);
            AutomationProperties.SetAutomationId(b, id);
            AutomationProperties.SetName(b, name + (selected ? ", shown" : ""));
            AutomationProperties.SetHelpText(b, help);
            b.Click += (_, _) => click();
            _switchList.Children.Add(b);
            return b;
        }
        var coordinator = new TextBlock { Text = CrewShown ? CrewStrip.Names.Coordinator + " · Coordinator" : "Coordinator", FontSize = ChatUi.SmallSize, FontWeight = _viewJob is null ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        Brand.Fg(coordinator, Brand.K.Text);
        Pill("switch-coordinator", coordinator, "Coordinator", "The main chat: it hands out the jobs and hears back from them.", _viewJob is null, () => ShowCoordinator(focus: true));
        for (var i = 0; i < listed.Count; i++)
        {
            var job = listed[i];
            if (i == open && _jobsSide)
            {
                var finished = Brand.Text("Finished", Brand.K.Muted, ChatUi.SmallSize);
                finished.Width = SideWidth - 2 * ChatUi.S3 - ChatUi.S1;
                finished.Margin = new Thickness(ChatUi.S1, ChatUi.S2, 0, ChatUi.S1);
                _switchList.Children.Add(finished);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = ChatUi.Glyph(job.State switch { JobBoard.Done => ChatUi.GlyphCheck, JobBoard.Failed => ChatUi.GlyphError, JobBoard.Stopped => ChatUi.GlyphStop, _ => "●" }, StateColor(job), 10);
            dot.Margin = new Thickness(0, 0, ChatUi.S1, 0);
            if (!job.Active && job.State != JobBoard.Done && job.State != JobBoard.Failed && job.State != JobBoard.Stopped) dot.Text = "○";
            row.Children.Add(dot);
            var worker = CrewWorker(job) is { } who ? who + " · " : "";
            var title = new TextBlock { Text = $"{worker}Job {job.Number} · {JobBoard.OneLine(job.Title, _jobsSide ? 60 : 24)}", FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center, FontWeight = _viewJob == job.Number ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
            Brand.Fg(title, Brand.K.Text);
            row.Children.Add(title);
            var state = JobBoard.StateLabel(job);
            UIElement content = row;
            if (_jobsSide)
            {
                // The column has room for the state under the title.
                var lines = new StackPanel();
                lines.Children.Add(row);
                var said = Brand.Text(state, StateColor(job), ChatUi.SmallSize);
                said.Margin = new Thickness(14, 1, 0, 0);
                said.TextTrimming = TextTrimming.CharacterEllipsis;
                said.TextWrapping = TextWrapping.NoWrap;
                lines.Children.Add(said);
                content = lines;
            }
            var number = job.Number;
            Pill("switch-job-" + number, content, $"Job {number}: {job.Title}, {state}", $"Job {number}, {state.ToLowerInvariant()}. Opens its own chat: everything it did, and what you send goes to it.",
                _viewJob == number, () => OpenJobView(number));
        }
        if (focused is not null && _switchList.Children.OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == focused) is { } again) again.Focus();
    }

    /// <summary>Shows job <paramref name="number"/>'s own chat in the conversation's place; the message box talks to it.</summary>
    private void OpenJobView(int number)
    {
        if (_board?.Find(number) is null || _jobScroll is null) return;
        CloseDrawer();
        if (_viewJob != number)
        {
            LeaveJobItems();
            _viewJob = number;
        }
        RefreshJobView(rebuild: true);
        _scroll.Visibility = Visibility.Collapsed;
        _jump.Visibility = Visibility.Collapsed;
        _jobScroll.Visibility = Visibility.Visible;
        AutomationProperties.SetName(_jobScroll, $"Job {number}'s conversation");
        _hint.Text = $"Message job {number}…";
        AutomationProperties.SetName(_input, $"Message to job {number}");
        AutomationProperties.SetHelpText(_input, $"What you type goes to job {number}. Enter sends, Shift+Enter starts a new line, Escape goes back to the coordinator.");
        ShowSwitcher();
        ShowJobBar();
        ShowStatus();
        Status(IsCoordinator ? $"You're in job {number}. What you send goes to this job; Back to coordinator (or Esc) returns."
            : $"You're in job {number}. This is a simple chat now, so its jobs don't run; switch it to With coordinator to carry on with this job.", spin: false);
        _jobScroll.ScrollToEnd();
        _input.Focus();
    }

    /// <summary>Back to the main chat (the coordinator) from a job's view.</summary>
    private void ShowCoordinator(bool focus)
    {
        if (_viewJob is null) return;
        LeaveJobItems();
        _viewJob = null;
        if (_jobScroll is not null) _jobScroll.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;
        _jobBar.Visibility = Visibility.Collapsed;
        _hint.Text = ChatStyles.ComposerHint;
        AutomationProperties.SetName(_input, "Message to Sketch ProMax");
        AutomationProperties.SetHelpText(_input, "Type your question or request here. Enter sends, Shift+Enter starts a new line, Escape stops the answer. Paste a picture or a file to attach it.");
        ShowSwitcher();
        ShowStatus();
        UpdateJump();
        ScrollToEnd(force: true, instant: true);
        if (focus) _input.Focus();
    }

    /// <summary>The job view's drawn items are let go (their rows and text views).</summary>
    private void LeaveJobItems()
    {
        foreach (var item in _jobDrawn)
        {
            _rows.Remove(item);
            _views.Remove(item);
        }
        _jobDrawn.Clear();
        _jobMessages.Children.Clear();
    }

    /// <summary>Draws what the shown job added since (or all of it, when items were let go at the start or it is new).</summary>
    private void RefreshJobView(bool rebuild = false)
    {
        if (_viewJob is not { } number || _board is null || _jobScroll is null) return;
        var items = _board.ItemsOf(number);
        var appendable = !rebuild && _jobDrawn.Count <= items.Count && (_jobDrawn.Count == 0 || ReferenceEquals(items[0], _jobDrawn[0]))
            && (_jobDrawn.Count == 0 || ReferenceEquals(items[_jobDrawn.Count - 1], _jobDrawn[^1]));
        if (!appendable || _jobDrawn.Count == 0) LeaveJobItems(); // also clears "Nothing here yet."
        var atEnd = _jobScroll.VerticalOffset >= _jobScroll.ScrollableHeight - 8;
        for (var k = _jobDrawn.Count; k < items.Count; k++)
        {
            var element = BuildJobItem(items, k);
            _jobMessages.Children.Add(element);
            _jobDrawn.Add(items[k]);
            if (appendable && _jobDrawn.Count > 1) ChatUi.Arrive(element, ChatUi.S2);
        }
        // A message on its way that has since arrived (or was kept for after): its line says so, without the buttons.
        foreach (var (item, text, actions) in _asideLines)
            if (text.Text != item.Text)
            {
                text.Text = item.Text;
                actions.Visibility = Visibility.Collapsed;
            }
        if (items.Count == 0 && _jobMessages.Children.Count == 0)
            _jobMessages.Children.Add(NoteLine(new ChatItem { Kind = "note", Text = "Nothing here yet." }));
        ShowJobBar();
        if (atEnd || rebuild) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _jobScroll.ScrollToEnd()));
    }

    /// <summary>One item of a job's conversation, drawn like the main chat's.</summary>
    private UIElement BuildJobItem(List<ChatItem> items, int index)
    {
        var item = items[index];
        var first = index == 0 || items[index - 1].Kind is not ("assistant" or "tool" or "file");
        return item.Kind switch
        {
            "user" => UserMessage(item),
            "assistant" => AssistantMessage(item, first),
            "tool" => ToolCard(item, first),
            "file" => FileCard(item),
            "error" => ErrorBox(item),
            "note" when item.Name.StartsWith(JobBoard.AsidePrefix, StringComparison.Ordinal) => AsideLine(item),
            _ => NoteLine(item),
        };
    }

    /// <summary>
    /// A message on its way to a busy job: the line says how it will be taken, with Apply now and After this job while
    /// it hasn't arrived (the worker's own call otherwise).
    /// </summary>
    private readonly List<(ChatItem Item, TextBlock Text, WrapPanel Actions)> _asideLines = [];

    private UIElement AsideLine(ChatItem item)
    {
        var panel = new StackPanel();
        var line = (DockPanel)NoteLine(item);
        panel.Children.Add(line);
        if (item.Text != JobBoard.AsideWaits || _viewJob is not { } number || line.Children.OfType<TextBlock>().LastOrDefault() is not { } said) return panel;
        var id = item.Name.Substring(JobBoard.AsidePrefix.Length);
        var actions = new WrapPanel { Margin = new Thickness(ChatUi.S4, ChatUi.S1, 0, 0) };
        _asideLines.RemoveAll(a => !a.Actions.IsVisible && a.Actions.Visibility == Visibility.Collapsed);
        _asideLines.Add((item, said, actions));
        void Pick(bool now)
        {
            if (_board?.SetAside(number, id, now, DateTime.UtcNow) != true)
            {
                Status("It already has this message.", spin: false);
                return;
            }
            actions.Visibility = Visibility.Collapsed;
            said.Text = item.Text;
            Status(now ? "It applies this with its next step." : "Kept for after its current work.", spin: false);
            JobsChanged();
        }
        var apply = ChatUi.Ghost(ChatUi.GlyphRight, "Apply now", "Apply now", "It changes the work in hand: the job fixes what it affects with its next step.");
        apply.Click += (_, _) => Pick(true);
        var after = ChatUi.Ghost(ChatUi.GlyphDown, "After this job", "After this job", "New work: the job does it once its current work is finished.");
        after.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        after.Click += (_, _) => Pick(false);
        actions.Children.Add(apply);
        actions.Children.Add(after);
        panel.Children.Add(actions);
        return panel;
    }

    /// <summary>The job's bar: its number, title and state (what it does now), and Stop while it is open.</summary>
    private void ShowJobBar()
    {
        if (_viewJob is not { } number || _board?.Snapshot().FirstOrDefault(j => j.Number == number) is not { } job)
        {
            _jobBar.Visibility = Visibility.Collapsed;
            return;
        }
        _jobBar.Visibility = Visibility.Visible;
        var title = $"Job {job.Number} · {job.Title}";
        if (_jobBarTitle.Text != title) _jobBarTitle.Text = title;
        _jobBarTitle.ToolTip = job.Title;
        var state = JobBoard.StateLabel(job);
        var line = job.State switch
        {
            JobBoard.Running when job.Now.Length > 0 => state + ". Now: " + job.Now,
            JobBoard.Waiting => state + ": its change waits for Revit to finish another job's change.",
            JobBoard.Queued => state + $" (up to {ChatJobs.Limit} jobs run at once).",
            JobBoard.Stopped => state + ". Send it a message to carry on.",
            JobBoard.Done or JobBoard.Failed => state + ". Send it a message to carry on.",
            _ => state,
        };
        if (job.Model.Length > 0) line += $" Runs on {ParallelJobCard.RunsOn(job)}.";
        if (_jobBarState.Text != line) _jobBarState.Text = line;
        Brand.Fg(_jobBarState, StateColor(job) == Brand.K.Text ? Brand.K.Muted : StateColor(job));
        AutomationProperties.SetName(_jobBar, $"Job {job.Number}: {job.Title}, {state}");
        if (_jobStop is not null)
        {
            _jobStop.Visibility = job.Active || job.State == JobBoard.Queued ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(_jobStop, $"Stop job {job.Number}");
        }
    }

    /// <summary>Send in a job's view: the message (and attachments) go to that job, which carries on with them.</summary>
    private void SendToJob(int number)
    {
        if (_board is null) return;
        if (_setup != Setup.Ready)
        {
            ShowSetup();
            Status(_setup == Setup.Checking ? "Still checking the chat setup; try again in a moment." : "Finish the step above first, then send.", spin: false);
            return;
        }
        if (!IsCoordinator)
        {
            Status("This is a simple chat now, so its jobs don't run. Switch it to With coordinator (under the chat's name) to carry on with this job.", spin: false);
            return;
        }
        if (!ClaudeCli.JobsAvailable)
        {
            Status("Jobs need the Sketch ProMax connector as new as the add-in. Run Install.cmd again, then send again.", spin: false);
            return;
        }
        var text = _input.Text.Trim();
        var files = _pending.Select(a => a.Path).ToList();
        if (text.Length == 0 && files.Count == 0)
        {
            Status("Type a message first.", spin: false);
            _input.Focus();
            return;
        }
        var sent = _board.Message(number, text, DateTime.UtcNow, fromUser: true, files);
        if (sent == JobBoard.Delivery.NotFound)
        {
            Status("This job is no longer in the chat.", spin: false);
            return;
        }
        _input.Clear();
        _pending.Clear();
        ShowChips();
        RefreshJobView();
        _jobScroll?.ScrollToEnd();
        JobsChanged();
        Status(sent == JobBoard.Delivery.Soon ? $"Sent to job {number}. It gets it with its next step: a correction is fixed now, new work is done after."
            : sent == JobBoard.Delivery.Reopened ? $"Sent to job {number}. It carries on with it."
            : $"Sent to job {number}. It reads it as soon as it finishes what it is doing now.", spin: false);
    }
}
