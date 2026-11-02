using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace SketchProMax.Chat;

/// <summary>
/// Chats that keep working while another is shown. Each chat the pane shows or has left working is a session: its
/// conversation, its coordinator's (or the chat's) Claude Code and running answer, its jobs (board, runs, reports and
/// their clocks) and its Autopilot's clock. The pane draws only the shown session. The others carry on in the background:
/// their engines' events, their jobs and their timers run "in" their session (<see cref="In"/>), which points the pane's
/// per-chat state at it and turns drawing off (<see cref="Hidden"/>), so the same code works on a chat that isn't shown
/// and only saves what it does. Opening the chat again shows it as it is now, still working. At most
/// <see cref="ChatWork.Limit"/> chats work in the background; leaving one more says so instead of stopping anything.
/// </summary>
internal sealed partial class ChatPane
{
    /// <summary>One chat's working state (see the class summary). The pane's per-chat fields read and write the current one.</summary>
    private sealed class ChatSession(Conversation conv)
    {
        public Conversation Conv = conv;
        public ChatEngine? Engine;
        public Turn? Turn;
        public DateTime LastActivity = DateTime.UtcNow;
        public string Model = ChatModels.DefaultId, Effort = "";
        public AutoPick? LastPick;
        public string? PendingType;
        public long ContextTokens;
        public object? CostEngine;
        public double CostSeen;

        // The main answer's task card (only while shown).
        public Bridge.JobControl? Control;
        public JobCardView? Job;
        public ChatItem? JobItem;

        // Its jobs.
        public JobBoard? Board;
        public List<ChatJob>? BoardJobs;
        public string BoardChat = "";
        public readonly Dictionary<string, JobRun> Runs = new();
        public readonly List<int> Reports = [];
        public DispatcherTimer? ReportTimer, JobClock;
        public bool JobsQueued;
        public int? ViewJob;
        public readonly Dictionary<string, ParkedEngine> Parked = new();
        public DateTime WatchedAt;
        public readonly HashSet<int> ShareOffered = [];

        // Its Autopilot's clock.
        public DateTime AutoIdleSince, ReconnectedAt, UpdateRetryAt;
        public bool ReviewAgain, ModelChecking, Probing, GapsWaiting, BackfillWaiting;
        public int Stalls;
        public readonly ConnectionWatch Connection = new();
        public readonly List<UserQuestion> LateAnswers = [];
        public readonly List<string> FromPhone = [];
        public Turn? PhoneTurn;

        // While in the background: the running answer's streamed texts and steps (by the stream's keys and tool ids), what
        // it does now, how its work ended (ChatWork.Done or .Failed until opened), and a save waiting.
        public readonly Dictionary<string, ChatItem> Texts = new(), Tools = new();
        public string Now = "", Ended = "";
        public DateTime EndedAt;
        public bool SaveDue;
    }

    private ChatSession _s = null!, _shown = null!;

    /// <summary>The chats left in the background: working, or finished and not opened since.</summary>
    private readonly List<ChatSession> _away = [];

    /// <summary>True while the code runs for a chat that isn't shown: nothing is drawn, what it does is only saved.</summary>
    private bool Hidden => !ReferenceEquals(_s, _shown);

    /// <summary>Runs <paramref name="work"/> for session <paramref name="s"/> (shown or not), then goes back to the one before.</summary>
    private void In(ChatSession s, Action work)
    {
        if (ReferenceEquals(s, _s))
        {
            work();
            return;
        }
        var was = _s;
        _s = s;
        try { work(); }
        finally { _s = was; }
    }

    /// <summary>Runs <paramref name="work"/> soon on the UI thread, for the session it was asked for.</summary>
    private void Later(Action work, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        var s = _s;
        Dispatcher.BeginInvoke(priority, new Action(() => In(s, work)));
    }

    /// <summary>A timer of the current session: each tick runs in it, shown or not.</summary>
    private DispatcherTimer SessionTimer(TimeSpan every, Action tick)
    {
        var s = _s;
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = every };
        timer.Tick += (_, _) =>
        {
            try { In(s, tick); }
            catch (Exception) { /* the next tick tries again */ }
        };
        return timer;
    }

    /// <summary>
    /// Awaits <paramref name="task"/> and carries on in the session it was awaited in, whichever chat is shown by then:
    /// every await in a chat's own work goes through this.
    /// </summary>
    private SessionAwait On(Task task) => new(this, _s, task);

    private SessionAwait<T> On<T>(Task<T> task) => new(this, _s, task);

    private readonly struct SessionAwait : INotifyCompletion
    {
        private readonly ChatPane _pane;
        private readonly ChatSession _session;
        private readonly Task _task;

        public SessionAwait(ChatPane pane, ChatSession session, Task task) => (_pane, _session, _task) = (pane, session, task);

        public SessionAwait GetAwaiter() => this;
        public bool IsCompleted => _task.IsCompleted && ReferenceEquals(_pane._s, _session);
        public void GetResult() => _task.GetAwaiter().GetResult();

        public void OnCompleted(Action next)
        {
            var (pane, s) = (_pane, _session);
            _task.ContinueWith(_ => pane.Dispatcher.BeginInvoke(new Action(() => pane.In(s, next))), TaskScheduler.Default);
        }
    }

    private readonly struct SessionAwait<T> : INotifyCompletion
    {
        private readonly ChatPane _pane;
        private readonly ChatSession _session;
        private readonly Task<T> _task;

        public SessionAwait(ChatPane pane, ChatSession session, Task<T> task) => (_pane, _session, _task) = (pane, session, task);

        public SessionAwait<T> GetAwaiter() => this;
        public bool IsCompleted => _task.IsCompleted && ReferenceEquals(_pane._s, _session);
        public T GetResult() => _task.GetAwaiter().GetResult();

        public void OnCompleted(Action next)
        {
            var (pane, s) = (_pane, _session);
            _task.ContinueWith(_ => pane.Dispatcher.BeginInvoke(new Action(() => pane.In(s, next))), TaskScheduler.Default);
        }
    }

    /// <summary>The session has work going: an answer, jobs running, waiting or reporting, or an Autopilot run on.</summary>
    private static bool Working(ChatSession s) =>
        s.Turn is not null || s.Runs.Count > 0 || (s.Board?.OpenCount ?? 0) > 0 || s.Reports.Count > 0 || s.Conv.Autopilot is { Active: true };

    /// <summary>Every session: the shown one first, then those in the background.</summary>
    private IEnumerable<ChatSession> Sessions => new[] { _shown }.Concat(_away);

    /// <summary>
    /// The session a bridge call came from: the shown chat for no key, else the chat with that chat key, or whose board
    /// has that job. Null when none (a chat let go).
    /// </summary>
    private ChatSession? SessionOf(string? key)
    {
        if (string.IsNullOrEmpty(key) || key == JobBoard.ChatKey(_shown.Conv.Id)) return _shown;
        return Sessions.FirstOrDefault(s => JobBoard.IsChatKey(key) ? JobBoard.ChatKey(s.Conv.Id) == key : s.Board?.FindKey(key) is not null);
    }

    private ChatSession? AwayOf(string file) =>
        file.Length == 0 ? null : _away.FirstOrDefault(s => string.Equals(s.Conv.File, file, StringComparison.OrdinalIgnoreCase));

    /// <summary>The main engine of the current session: its events and its end are handled in this session, shown or not.</summary>
    private void Hook(ChatEngine engine)
    {
        var s = _s;
        engine.Event += e => In(s, () =>
        {
            if (_engine != engine) return;
            if (Hidden) HiddenEvent(e);
            else OnEvent(e);
        });
        engine.Exited += tail => In(s, () => { if (_engine == engine) OnExited(engine, tail); });
    }

    /// <summary>
    /// The shown chat is being left (New chat, or another chat opened): it carries on in the background when it has work
    /// (its answer, its jobs, its Autopilot), else it is let go. False when it can't be left now; <paramref name="why"/>
    /// then says why in one line. Nothing is ever stopped here.
    /// </summary>
    private bool LeaveShown(out string? why)
    {
        why = null;
        var s = _shown;
        // Only the brief's planner: nothing in the model changes, and the request is kept in the chat.
        if (_turn is { Planning: true } planning) StopBrief(planning);
        if (_turn is { Shrinking: true })
        {
            why = "Sketch ProMax is shrinking this chat. Wait a moment for it, then open the other chat.";
            return false;
        }
        var working = Working(s);
        if (working && ChatWork.WhyNotLeave(_away.Count(Working), _turn is not null && !CanAnswerAway()) is { } refusal)
        {
            why = refusal;
            return false;
        }
        if (_viewJob is not null) ShowCoordinator(focus: false);
        if (_turn is { } turn)
        {
            // The answer's card and time end here without stopping it: its steps are in the chat as well.
            RenderDirty();
            if (!turn.Stopping) turn.KillTimer?.Stop(); // one being stopped is still ended (StopTurn's timer runs in its chat)
            try { Revit.TimeLog.EndChat(); } catch (Exception) { }
            if (_jobItem is { } card) _conv.Items.Remove(card);
            _job = null;
            _jobItem = null;
            var left = _control is not null ? EndJob("done", null) : [];
            if (left.Count > 0 && _input.Text.Trim().Length == 0) _input.Text = string.Join("\n\n", left.Select(n => n.Text));
            foreach (var pair in _texts) s.Texts[pair.Key] = pair.Value.Item;
            foreach (var pair in _tools) s.Tools[pair.Key] = pair.Value.Item;
            s.Now = _tools.Values.LastOrDefault(r => r.Item.State == "running") is { } running ? StepNames.Title(running.Item.Name) : "Thinking";
        }
        _texts.Clear();
        _tools.Clear();
        _dirty.Clear();
        CrewCoordinatorDoes(null);
        Save();
        if (working)
        {
            s.Ended = "";
            _away.Add(s);
        }
        else LetGo(s);
        return true;
    }

    /// <summary>
    /// A chat's answer can carry on in the background only when its calls can be told from the shown chat's: its Claude
    /// Code passes the chat's key (a connector as new as the add-in).
    /// </summary>
    private bool CanAnswerAway() => _engine is { IsAlive: true, ChatKey.Length: > 0 } && ClaudeCli.JobsAvailable;

    /// <summary>A chat with no work left: its engines end (opening it again resumes its session) and its board is no longer reached.</summary>
    private void LetGo(ChatSession s)
    {
        In(s, () =>
        {
            _engine?.Dispose();
            _engine = null;
            foreach (var key in _parked.Keys.ToList()) DropParked(key);
            _reportTimer?.Stop();
            _jobClock?.Stop();
            if (_boardChat.Length > 0) ChatJobs.Boards.Remove(_boardChat);
            _boardChat = "";
            Remember(_conv);
        });
    }

    /// <summary>Shows session <paramref name="next"/> in the pane: as it is now, still answering when it answers.</summary>
    private void ShowSession(ChatSession next)
    {
        _away.Remove(next);
        _s = _shown = next;
        var answering = _turn is { Planning: false } && _engine is { IsAlive: true };
        if (answering && _control is null) BeginJob(null);
        _lastActivity = DateTime.UtcNow;
        ShowConversation();
        if (answering)
        {
            foreach (var pair in next.Texts) if (_views.TryGetValue(pair.Value, out var view)) _texts[pair.Key] = (pair.Value, view);
            foreach (var pair in next.Tools) if (_rows.TryGetValue(pair.Value, out var row)) _tools[pair.Key] = row;
        }
        next.Texts.Clear();
        next.Tools.Clear();
        next.Now = "";
        next.Ended = "";
        ShowStatus();
        ShowChats();
    }

    /// <summary>Shows conversation <paramref name="conv"/> as a new session (a chat opened from disk, or a new one).</summary>
    private void ShowNew(Conversation conv) => ShowSession(new ChatSession(conv) { Model = _model, Effort = _effort });

    /// <summary>
    /// What a background chat's Claude Code says while its chat isn't shown: into its conversation (saved), without
    /// drawing anything; its result ends the answer as the shown chat's would (OnResult).
    /// </summary>
    private void HiddenEvent(ChatEvent e)
    {
        var s = _s;
        _lastActivity = DateTime.UtcNow;
        var turn = _turn;
        void Put(ChatItem item)
        {
            if (item.Time == default) item.Time = DateTime.UtcNow;
            _conv.Items.Add(item);
        }
        switch (e)
        {
            case InitEvent init:
                if (init.SessionId.Length > 0) _conv.SessionId = init.SessionId;
                break;
            case TextStartEvent start:
            {
                var item = new ChatItem { Kind = "assistant" };
                Put(item);
                s.Texts[start.Key] = item;
                if (turn is not null) turn.SawText = true;
                s.Now = "Writing";
                SaveSoon();
                break;
            }
            case TextDeltaEvent delta:
                if (s.Texts.TryGetValue(delta.Key, out var target)) target.Text += delta.Text;
                break;
            case AssistantTextEvent text:
                if (text.Text.Trim().Length == 0) break;
                if (turn is not null) turn.SawText = true;
                Put(new ChatItem { Kind = "assistant", Text = text.Text });
                break;
            case ToolUseEvent tool:
            {
                var name = StreamParser.ShortName(tool.Name);
                var summary = StepNames.Describe(name, tool.InputJson);
                if (!s.Tools.TryGetValue(tool.Id, out var item))
                {
                    item = new ChatItem { Kind = "tool", Name = name, State = "running", Text = summary };
                    Put(item);
                    s.Tools[tool.Id] = item;
                }
                if (tool.InputJson.Length > 0)
                {
                    item.Input = Pretty(tool.InputJson);
                    item.Text = summary;
                }
                if (turn is not null) turn.SawTool = true;
                s.Now = StepNames.Title(name);
                SaveSoon();
                ShowChats();
                break;
            }
            case ToolResultEvent result:
                if (s.Tools.TryGetValue(result.ToolUseId, out var step))
                {
                    step.State = result.IsError ? "failed" : "done";
                    if (result.Text.Length > 0) step.Result = result.Text.Length > MaxResult ? result.Text.Substring(0, MaxResult) + "…" : result.Text;
                    if (!result.IsError && FilesItem(step.Name, result.Text) is { } files) Put(files);
                }
                s.Now = "Thinking";
                SaveSoon();
                break;
            case NoticeEvent notice:
                if (notice.Text.Length > 0) Put(new ChatItem { Kind = "note", Text = notice.Text });
                break;
            case CompactedEvent compacted:
                Compacted(compacted, turn);
                break;
            case UsageEvent usage:
                OnUsage(usage.Usage);
                break;
            case ContextEvent context:
                if (turn is not { Shrinking: true })
                {
                    _contextTokens = context.Tokens;
                    _conv.ContextFloor = ChatRecall.Floor(_conv.ContextFloor, context.Tokens);
                }
                break;
            case ResultEvent end:
                // The answer streamed is complete: it ends as the shown chat's would (retries, errors, the Autopilot's next step).
                foreach (var item in s.Texts.Values) item.Text = item.Text.TrimEnd();
                OnResult(end);
                break;
        }
    }

    /// <summary>A step still runs in the current session's answer (drawn rows when shown, its own items when not).</summary>
    private bool StepRunning => Hidden ? _s.Tools.Values.Any(i => i.State == "running") : _tools.Values.Any(r => r.Item.State == "running");

    /// <summary>
    /// After a background chat's answer or job ended: when it has no work left it is marked finished (the Chats switcher
    /// says so until it is opened) and its engines end.
    /// </summary>
    private void AwayCheck()
    {
        if (!Hidden || _stopped) return;
        var s = _s;
        if (Working(s) || !_away.Contains(s)) return;
        if (s.Ended.Length == 0)
        {
            s.Ended = s.Conv.Items.LastOrDefault(i => i.Kind is "assistant" or "error")?.Kind == "error" ? ChatWork.Failed : ChatWork.Done;
            s.EndedAt = DateTime.UtcNow;
            var name = ChatHistory.NameFor(s.Conv) is { Length: > 0 } n ? n : ChatHistory.Untitled;
            In(_shown, () =>
            {
                if (_turn is null) Status(ChatWork.FinishedLine(name, s.Ended), spin: false);
                ChatUi.FlashIfAway(this);
            });
        }
        foreach (var item in s.Tools.Values.Where(i => i.State == "running")) item.State = "stopped";
        s.Texts.Clear();
        s.Tools.Clear();
        s.Now = "";
        Save();
        LetGo(s);
        ShowChats();
    }

    /// <summary>The background chats' saves waiting (SaveSoon while not shown): written with the shown chat's.</summary>
    private void SaveAway()
    {
        foreach (var s in _away.Where(a => a.SaveDue).ToList())
        {
            s.SaveDue = false;
            In(s, Save);
        }
    }
}
