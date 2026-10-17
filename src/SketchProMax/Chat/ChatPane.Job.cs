using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SketchProMax.Bridge;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The live job card: every chat task gets a <see cref="JobControl"/> (Pause, Resume, Skip step, Stop) and, when it has
/// several steps, a card in the conversation. The card starts from the approved brief's planned tools (or appears at the
/// second step of a task run as typed), follows each tool call in place, shows the step's item progress from the job
/// feed, the thinking and "Now: …", and ends as a summary saved with the chat (a "job" item). Messages typed while the
/// task runs are queued (<see cref="JobNotes"/>) and reach Claude with the next step's result.
/// </summary>
internal sealed partial class ChatPane
{
    private JobControl? _control;
    private JobCardView? _job;
    /// <summary>The finished card that offers Undo all (only one does: the latest task's).</summary>
    private JobCardView? _undoView;
    private ChatItem? _jobItem;
    private DispatcherTimer? _jobTimer, _undoTimer;
    private readonly Dictionary<string, JobStep> _jobSteps = new();
    private readonly Dictionary<string, string> _jobInputs = new();
    private readonly List<(string Id, string Tool)> _turnTools = [];
    private readonly Dictionary<string, (ChatItem Item, TextBlock Text)> _acks = new();
    private JobStep? _waitingStep;
    private string _thought = "";
    private bool _thinking;
    private DateTime _jobStarted;

    /// <summary>
    /// A chat task starts: its control, a fresh feed, and the card when the brief plans several steps. <paramref name="plan"/>:
    /// the brief the user ran (null when run as typed).
    /// </summary>
    private void BeginJob(PromptBrief? plan)
    {
        EndJobHooks();
        DropUndo(); // a new task: the last one's Undo all would no longer be the top of Revit's Undo list
        // A brief the user ran is their approval of the task: its steps don't ask again, except the risky ones.
        _control = new JobControl { ApprovedForTask = plan is not null };
        JobControl.Current = _control;
        JobControl.WaitAtBoundary ??= JobWindow.WaitPaused;
        JobFeed.Reset();
        JobNotes.Take(); // nothing left over from an earlier task
        Revit.JobUndo.BeginJob();
        _jobSteps.Clear();
        _jobInputs.Clear();
        _turnTools.Clear();
        _waitingStep = null;
        _thought = "";
        _thinking = false;
        _jobStarted = DateTime.UtcNow;
        var control = _control;
        var dark = Brand.IsDark;
        JobFeed.LongStep = () => JobWindow.Open(control, dark, (id, text) =>
            Dispatcher.BeginInvoke(new Action(() => { if (_control == control) ShowSentNote(id, text, fromWindow: true); })));
        JobNotes.Delivered += OnNotesDelivered;
        _control.Changed += OnControlChanged;
        if (plan is not null && JobCardModel.Plans(plan.Tools, plan.Kind)) AddJobCard(JobCardModel.FromPlan(plan.Tools, _jobStarted));
        _jobTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _jobTimer.Tick -= OnJobTick;
        _jobTimer.Tick += OnJobTick;
        _jobTimer.Start();
    }

    private void AddJobCard(JobCardModel model)
    {
        _jobItem = new ChatItem { Kind = "job", Job = model.ToJson() };
        _job = NewCardView(model, live: true);
        Add(_jobItem);
        if (_thought.Length > 0) _job.ThinkingText(_thought, newBlock: true);
        if (!_thinking) _job.ThinkingDone();
    }

    private JobCardView NewCardView(JobCardModel model, bool live)
    {
        var view = new JobCardView(model, live);
        view.Pressed += action =>
        {
            if (_control is not { } control) return;
            if (action == JobAction.Stop) StopTurn(); // Stop also ends Claude's turn
            else control.Apply(action);
            RenderJob();
        };
        return view;
    }

    /// <summary>The element for a saved "job" item: the live card while its task runs, else the finished summary.</summary>
    private UIElement JobCard(ChatItem item)
    {
        if (item == _jobItem && _job is not null) return _job;
        var model = JobCardModel.FromJson(item.Job) ?? new JobCardModel { State = "done" };
        if (model.State == "running") model.Finish("stopped", model.Started, "", model.Changed); // Revit closed or the chat moved on mid-task
        return NewCardView(model, live: false);
    }

    private void OnControlChanged(JobState state) => Dispatcher.BeginInvoke(new Action(RenderJob));

    private void OnJobTick(object? sender, EventArgs e) => RenderJob();

    private void RenderJob()
    {
        if (_job is not { Live: true } view || _control is not { } control) return;
        JobFeed.Drain(); // step events: the stream says the same, in order; keep the queue short
        var snap = JobFeed.Now;
        var step = view.Model.Running;
        if (step is not null && snap.Running && snap.Tool == step.Tool && snap.Total > 0)
        {
            step.Done = snap.Done;
            step.Total = snap.Total;
        }
        view.Render(snap.Running ? snap : null, DateTime.UtcNow, NowLine(snap, step), control.State);
        if (control.State is JobState.Paused or JobState.PauseRequested && _turn is { Stopping: false })
            Status(control.State == JobState.Paused ? "Paused. Press Resume on the card to carry on." : "Pausing at the next item or step…", spin: true);
    }

    /// <summary>"Fill title blocks · A-101": the running step, and its item or input.</summary>
    private string NowLine(JobFeed.Snapshot snap, JobStep? step)
    {
        if (snap.Running)
            return JobMath.Title(snap.Tool) + (snap.Label.Length > 0 ? " · " + snap.Label : snap.What.Length > 0 && snap.What != JobMath.Title(snap.Tool) ? " · " + snap.What : "");
        if (step is null) return "";
        var input = _jobSteps.Where(p => p.Value == step).Select(p => _jobInputs.TryGetValue(p.Key, out var s) ? s : "").FirstOrDefault(s => s.Length > 0) ?? "";
        return step.Title + (input.Length > 0 ? " · " + input : "");
    }

    // ---- From the stream ------------------------------------------------------------------------------------------

    /// <summary>A tool call started (once per call id).</summary>
    private void JobToolStarted(string id, string tool, string summary)
    {
        if (_control is null) return;
        _jobInputs[id] = summary;
        _job?.ThinkingDone();
        _thinking = false;
        if (JobCardModel.Quiet(tool)) return;
        _turnTools.Add((id, tool));
        if (_job is null && _turnTools.Count >= 2)
        {
            // A task run as typed turns out to have several steps: the card starts here, with the steps so far.
            var model = new JobCardModel { Started = _jobStarted };
            foreach (var (earlierId, earlierTool) in _turnTools.Take(_turnTools.Count - 1))
            {
                if (model.Begin(earlierTool) is not { } earlier) continue;
                _jobSteps[earlierId] = earlier;
                var row = _tools.TryGetValue(earlierId, out var r) ? r.Item : null;
                if (row is { State: not "running" }) model.End(earlier, row.State, row.State == "failed" ? JobCardModel.Outcome(row.Result, true).Reason : "");
            }
            AddJobCard(model);
        }
        if (_job?.Model.Begin(tool) is { } step) _jobSteps[id] = step;
        SaveJob();
    }

    /// <summary>A tool call returned (its result, or "still running" for a long one that wait_for_job collects later).</summary>
    private void JobToolEnded(string id, string result, bool isError)
    {
        if (_job is not { } view) return;
        var (state, reason) = JobCardModel.Outcome(result, isError);
        if (_jobSteps.TryGetValue(id, out var step))
        {
            if (state == "running") _waitingStep = step;
            else view.Model.End(step, state, reason);
        }
        else if (_waitingStep is { } waiting && state != "running" && _jobInputs.ContainsKey(id))
        {
            // wait_for_job brought the long step's result.
            view.Model.End(waiting, state, reason);
            _waitingStep = null;
        }
        SaveJob();
        RenderJob();
    }

    private void JobThinking(string text, bool newBlock)
    {
        _thinking = true;
        _thought = newBlock ? text : _thought + text;
        if (_thought.Length > JobCardModel.MaxThinking) _thought = _thought.Substring(_thought.Length - JobCardModel.MaxThinking);
        _job?.ThinkingText(text, newBlock);
    }

    private void JobWriting()
    {
        _thinking = false;
        _job?.ThinkingDone();
    }

    private void SaveJob()
    {
        if (_jobItem is { } item && _job is { } view) item.Job = view.Model.ToJson();
    }

    // ---- The end of the task ----------------------------------------------------------------------------------------

    /// <summary>
    /// The task ended (<paramref name="state"/>: done, stopped or failed): the card collapses to its summary with
    /// <paramref name="time"/> (the time log's "Done in…" line, or null), Undo all when it is safe, and messages no step
    /// picked up are returned: the caller sends them as the next message (or, after a stop, puts them back in the box).
    /// </summary>
    private List<(string Id, string Text)> EndJob(string state, string? time)
    {
        var control = _control;
        control?.Finish();
        EndJobHooks();
        var left = JobNotes.Take();
        Revit.JobUndo.EndJob();
        if (_job is { } view && _jobItem is { } item)
        {
            view.Model.Finish(state, DateTime.UtcNow, time ?? "", Revit.JobUndo.ElementsChanged);
            item.Job = view.Model.ToJson();
            if (Revit.JobUndo.CanUndo(out _) && !_sample)
            {
                var task = Revit.JobUndo.TaskNumber;
                view.CanUndo = true;
                view.UndoPressed += () => UndoJob(view, item, task);
                WatchUndo(view);
            }
            view.End();
        }
        else if (time is not null) Note(time);
        _job = null;
        _jobItem = null;
        _control = null;
        _jobSteps.Clear();
        _turnTools.Clear();
        _waitingStep = null;
        return left;
    }

    /// <summary>Lets go of everything static that points at this task (and this pane).</summary>
    private void EndJobHooks()
    {
        _jobTimer?.Stop();
        if (_control is { } control)
        {
            control.Changed -= OnControlChanged;
            if (JobControl.Current == control) JobControl.Current = null;
        }
        JobNotes.Delivered -= OnNotesDelivered;
        JobFeed.LongStep = null;
        JobWindow.Close();
    }

    /// <summary>Undo all stays only while it is safe: once the model changes, the button goes.</summary>
    private void WatchUndo(JobCardView view)
    {
        DropUndo();
        _undoView = view;
        var timer = _undoTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (view.CanUndo && _undoView == view && Revit.JobUndo.CanUndo(out _) && !_stopped) return;
            timer.Stop();
            if (!view.CanUndo) return;
            view.CanUndo = false;
            view.End();
        };
        timer.Start();
    }

    /// <summary>Takes Undo all off the card that offers it.</summary>
    private void DropUndo()
    {
        _undoTimer?.Stop();
        _undoTimer = null;
        if (_undoView is { } old && old.CanUndo)
        {
            old.CanUndo = false;
            old.End();
        }
        _undoView = null;
    }

    private async void UndoJob(JobCardView view, ChatItem item, int task)
    {
        if (!view.CanUndo || _undoView != view) return;
        DropUndo();
        string? problem;
        try { problem = await App.Dispatcher.InvokeFromEventAsync(app => Revit.JobUndo.Undo(app, task)); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or Autodesk.Revit.Exceptions.ApplicationException) { problem = ex.Message; }
        if (problem is not null)
        {
            Status(problem, spin: false);
            return;
        }
        view.Model.Summary = (view.Model.Summary + " Undone with Undo all.").Trim();
        item.Job = view.Model.ToJson();
        view.End();
        Save();
        Status("Undo all: Revit undid the task's changes (Redo brings them back).", spin: false);
    }

    // ---- Messages while the task runs -------------------------------------------------------------------------------

    /// <summary>
    /// While a task runs, Send shows next to Stop as soon as there is a message to send (it steers the task); the
    /// tooltip says when Claude gets it.
    /// </summary>
    private void ShowSteer()
    {
        var steer = _turn is { Planning: false, Stopping: false } && _control is not null && _input.Text.Trim().Length > 0;
        if (_turn is not null) _send.Visibility = steer ? Visibility.Visible : Visibility.Collapsed;
        var tip = steer ? "Send to Claude now: it is applied after the current item (Enter)." : "Send (Enter)";
        if (_send.ToolTip as string != tip)
        {
            _send.ToolTip = tip;
            AutomationProperties.SetHelpText(_send, steer ? tip : "Sends your message to Claude (Enter).");
        }
    }

    /// <summary>A message typed while the task runs: queued for the next step boundary.</summary>
    private void QueueNote(string text)
    {
        var id = JobNotes.Add(text);
        ShowSentNote(id, text, fromWindow: false);
    }

    /// <summary>Shows a queued message and its "will apply" line (typed in the pane, or in the job window while Revit was busy).</summary>
    private void ShowSentNote(string id, string text, bool fromWindow)
    {
        Add(new ChatItem { Kind = "user", Text = text });
        Add(new ChatItem { Kind = "note", Name = "ack:" + id, Text = "Got it, will apply after this item." + (fromWindow ? " (Sent from the task window.)" : "") });
        Save();
    }

    /// <summary>A "will apply" line, kept so it can say when Claude got the message.</summary>
    private UIElement AckLine(ChatItem item)
    {
        var row = (DockPanel)NoteLine(item);
        if (row.Children.OfType<TextBlock>().LastOrDefault() is { } text) _acks[item.Name.Substring(4)] = (item, text);
        return row;
    }

    private void OnNotesDelivered(string tool, IReadOnlyList<string> ids) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var id in ids) SetAck(id, $"Applied: Claude got it with the result of {JobMath.Title(tool)}.");
            Save();
        }));

    private void SetAck(string id, string text)
    {
        if (!_acks.TryGetValue(id, out var ack)) return;
        ack.Item.Text = text;
        ack.Text.Text = text;
        if (ack.Text.Parent is FrameworkElement row) AutomationProperties.SetName(row, text);
    }

    /// <summary>Messages no step picked up: sent as the next message, or (after a stop) put back in the message box.</summary>
    private void SendLeftover(List<(string Id, string Text)> left, bool stopped)
    {
        if (left.Count == 0) return;
        var text = string.Join("\n\n", left.Select(n => n.Text));
        if (stopped || _stopped || _setup != Setup.Ready)
        {
            foreach (var (id, _) in left) SetAck(id, "Not sent: the task was stopped. It is back in the message box.");
            if (_input.Text.Trim().Length == 0) _input.Text = text;
            else _input.Text = text + "\n\n" + _input.Text;
            _input.CaretIndex = _input.Text.Length;
            return;
        }
        foreach (var (id, _) in left) SetAck(id, "Claude finished before another step, so this was sent as your next message.");
        _ = RunTurn(text, [], "");
    }

    // ---- Field-test hooks -------------------------------------------------------------------------------------------

    /// <summary>
    /// For the field test: a job card built from a sample stream-json transcript (no pane needed), with the thinking
    /// it carried. Returns the card and the thinking events parsed.
    /// </summary>
    internal static (JobCardView Card, int ThinkingEvents) CardFromSample(IEnumerable<string> lines)
    {
        var parser = new StreamParser();
        var model = JobCardModel.FromPlan(["create_sheets", "fill_title_blocks"], DateTime.UtcNow);
        var view = new JobCardView(model, live: true);
        var count = 0;
        foreach (var line in lines)
            foreach (var e in parser.ParseLine(line))
            {
                switch (e)
                {
                    case ThinkingEvent: view.ThinkingStarted(); count++; break;
                    case ThinkingDeltaEvent d: view.ThinkingText(d.Text, false); count++; break;
                    case ThinkingTextEvent t: view.ThinkingText(t.Text, true); count++; break;
                }
            }
        view.Render(null, DateTime.UtcNow, "Create sheets · A-101");
        return (view, count);
    }
}
