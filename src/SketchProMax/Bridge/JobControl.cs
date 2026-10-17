using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SketchProMax.Bridge;

// The live job card's core, shared by the chat pane, the bridge and the batch tools. No Revit and no WPF here (the unit
// tests compile this file on its own): the control state machine, the progress feed, the progress and time-left
// arithmetic and the messages the user sends while a task runs.

/// <summary>Where a chat task stands for Pause, Resume, Skip step and Stop.</summary>
internal enum JobState { Running, PauseRequested, Paused, Skipping, Stopping, Finished }

internal enum JobAction { Pause, Resume, Skip, Stop }

/// <summary>
/// The user's control over the chat task running now: Pause (at the next item or step boundary), Resume, Skip step (the
/// current tool call ends with "skipped by user" and Claude moves on) and Stop (the item in progress is rolled back,
/// finished items are kept, no more steps). Thread-safe: the card and the job window set it on their UI threads, the batch
/// tools read it on Revit's thread and the bridge on its own. Checked only at boundaries:
/// <list type="bullet">
/// <item>between items of an <c>ItemBatch</c> (no transaction open there; its transaction group stays open, so the batch
/// is still one Undo step) and at each <c>CancelPoint</c> of a one-transaction loop (Skip and Stop only: a pause can't wait
/// inside an open transaction, so it waits at the next step instead);</item>
/// <item>at the bridge, before the next tool call starts: a pause there waits without holding Revit at all.</item>
/// </list>
/// </summary>
internal sealed class JobControl
{
    private readonly object _gate = new();
    private JobState _state = JobState.Running;

    /// <summary>The chat task's control (null when no chat task runs): set by the chat pane when a turn starts.</summary>
    public static JobControl? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
    private static JobControl? _current;

    /// <summary>
    /// The control of the chat tool call running on Revit's thread now (null while none, or while a call from elsewhere
    /// runs): set by the bridge for the length of the call. Item boundaries and Jobs.CancelRequested read this, not
    /// <see cref="Current"/>, so a Pause, Skip or Stop pressed on the chat's card never reaches another client's job, and
    /// a call that outlives its chat task keeps the control it started with.
    /// </summary>
    public static JobControl? Tool
    {
        get => Volatile.Read(ref _tool);
        set => Volatile.Write(ref _tool, value);
    }
    private static JobControl? _tool;

    private bool _over;

    /// <summary>The task ended (<see cref="Finish"/>): nothing more is queued for it.</summary>
    public bool Over { get { lock (_gate) return _over; } }

    public JobState State { get { lock (_gate) return _state; } }

    /// <summary>Skip or Stop was asked for: the current step should end at its next boundary.</summary>
    public bool Halting => State is JobState.Skipping or JobState.Stopping;

    /// <summary>Raised (on the caller's thread, outside the lock) after every change.</summary>
    public event Action<JobState>? Changed;

    /// <summary>The state machine: whether <paramref name="action"/> applies in <paramref name="state"/>.</summary>
    public static bool Can(JobState state, JobAction action) => action switch
    {
        JobAction.Pause => state == JobState.Running,
        JobAction.Resume => state is JobState.PauseRequested or JobState.Paused,
        JobAction.Skip => state is JobState.Running or JobState.PauseRequested or JobState.Paused,
        JobAction.Stop => state is JobState.Running or JobState.PauseRequested or JobState.Paused or JobState.Skipping,
        _ => false,
    };

    /// <summary>The state after <paramref name="action"/> (the same state when it doesn't apply).</summary>
    public static JobState Next(JobState state, JobAction action) => !Can(state, action) ? state : action switch
    {
        JobAction.Pause => JobState.PauseRequested,
        JobAction.Resume => JobState.Running,
        JobAction.Skip => JobState.Skipping,
        _ => JobState.Stopping,
    };

    /// <summary>The user pressed a button: true when it changed anything.</summary>
    public bool Apply(JobAction action) => Move(s => Next(s, action));

    /// <summary>At a boundary: a requested pause takes effect now. True when it did (the caller then waits).</summary>
    public bool EnterPause() => Move(s => s == JobState.PauseRequested ? JobState.Paused : s);

    /// <summary>A step (tool call) ended: a skip is used up, so the next step runs. Returns the state the step ended in.</summary>
    public JobState StepEnded()
    {
        var was = State;
        Move(s => s == JobState.Skipping ? JobState.Running : s);
        return was;
    }

    /// <summary>
    /// The task ended: nothing waits any more and later buttons do nothing. A Stop already pressed stands, so a tool
    /// call still running in Revit (one that outlived its turn) stops at its next item instead of carrying on.
    /// </summary>
    public void Finish()
    {
        lock (_gate) _over = true;
        Move(s => s == JobState.Stopping ? s : JobState.Finished);
    }

    private bool Move(Func<JobState, JobState> next)
    {
        JobState now;
        lock (_gate)
        {
            now = next(_state);
            if (now == _state) return false;
            _state = now;
            Monitor.PulseAll(_gate);
        }
        try { Changed?.Invoke(now); } catch (Exception) { /* a listener's problem never stops the task */ }
        return true;
    }

    /// <summary>Blocks up to <paramref name="ms"/> while paused; returns the state then. Not on a UI thread.</summary>
    public JobState WaitWhilePaused(int ms)
    {
        var until = Environment.TickCount + ms;
        lock (_gate)
        {
            while (_state == JobState.Paused)
            {
                var left = until - Environment.TickCount;
                if (left <= 0 || !Monitor.Wait(_gate, left)) break;
            }
            return _state;
        }
    }

    /// <summary>Waits (without a thread) while paused, at most <paramref name="ms"/>; returns the state then.</summary>
    public async Task<JobState> WaitWhilePausedAsync(int ms, CancellationToken ct)
    {
        var until = Environment.TickCount + ms;
        while (State == JobState.Paused && until - Environment.TickCount > 0 && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(150, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return State;
    }

    // ---- At an item boundary (ItemBatch, CancelPoint) -----------------------------------------------------------------

    /// <summary>
    /// Waits on Revit's thread while paused at an item boundary (the chat sets it: a small modal Paused window, so Revit
    /// keeps painting but takes no edits while the batch's transaction group is open). Returns once the state is no
    /// longer Paused. Null: nothing can wait, so the pause is let go at once.
    /// </summary>
    internal static Action<JobControl, string>? WaitAtBoundary;

    /// <summary>The field test's stand-in for <see cref="WaitAtBoundary"/> (it presses Resume, Skip or Stop itself).</summary>
    internal static Action<JobControl, string>? TestWait;

    /// <summary>The field test's hook: told each item boundary (what, 0-based index) before the item runs.</summary>
    internal static Action<string, int>? TestBoundary;

    /// <summary>The field test's hook: told after an item's edit, before it is committed (a Stop here rolls the item back).</summary>
    internal static Action<string, int>? TestAfterItem;

    /// <summary>
    /// At the boundary before item <paramref name="index"/> of a batch, with no transaction open: a requested pause
    /// takes effect and waits here. Returns the state to act on (Skipping or Stopping: do no more items).
    /// </summary>
    public static JobState AtBoundary(string what, int index)
    {
        TestBoundary?.Invoke(what, index);
        if (Tool is not { } control) return JobState.Running;
        if (control.EnterPause())
        {
            JobFeed.SetPaused(true);
            try
            {
                var wait = TestWait ?? WaitAtBoundary;
                if (wait is null) control.Apply(JobAction.Resume);
                else wait(control, what);
                if (control.State == JobState.Paused) control.Apply(JobAction.Resume); // the wait gave up: carry on
            }
            finally { JobFeed.SetPaused(false); }
        }
        return control.State;
    }

    /// <summary>After an item's edit, before its commit: true when Stop was pressed meanwhile (roll this item back).</summary>
    public static bool StopNow(string what, int index)
    {
        TestAfterItem?.Invoke(what, index);
        return Tool?.State == JobState.Stopping;
    }

    /// <summary>A batch item's reason when Skip or Stop ended the batch before it.</summary>
    public static string NotDone(JobState state) =>
        state == JobState.Skipping ? "not done: the user skipped this step (Skip step)" : "not done: the user stopped the task (Stop)";

    public const string RolledBack = "rolled back: the user pressed Stop while this item ran; it was undone, finished items are kept";

    // ---- At a step boundary (the bridge, once a tool call returns) ----------------------------------------------------

    /// <summary>
    /// What Claude gets back from a chat tool call that just ended: Skip and Stop say so first (a skip is then used up),
    /// and (with <paramref name="notes"/>) the user's messages sent meanwhile are added at the end, so Claude sees them
    /// before its next step. A JSON result stays JSON (see <see cref="JobText"/>).
    /// A step that couldn't stop part-way (its result says <see cref="KeptNothing"/>) is told as kept nothing, never as
    /// "finished items are kept".
    /// <paramref name="state"/>: done, failed, skipped or stopped (for the card).
    /// </summary>
    public static string AfterStep(JobControl? control, string tool, string text, bool isError, out string state, bool notes = true)
    {
        state = isError ? "failed" : "done";
        if (control is not null)
        {
            var whole = text.Contains(KeptNothing);
            switch (control.StepEnded())
            {
                case JobState.Skipping:
                    state = "skipped";
                    text = JobText.Add(text, whole ? SkippedNothingKeptText : SkippedText, null);
                    break;
                case JobState.Stopping:
                    state = "stopped";
                    text = JobText.Add(text, whole ? StoppedNothingKeptText : StoppedText, null);
                    break;
            }
        }
        return notes ? JobNotes.Attach(tool, text) : text;
    }

    /// <summary>
    /// The result of a call that ended on a stop request before it finished (it threw at a cancel point): whatever it
    /// had open was rolled back and no report or file was written. The bridge says it, the card and Claude read it.
    /// </summary>
    public const string KeptNothing =
        "This call ended before it finished and kept nothing of its own: the change it had open was rolled back, and a report or file it was making was not written.";

    /// <summary>What the bridge returns for such a call (<paramref name="detail"/>: the tool's own line, "No report was written.").</summary>
    public static string EndedEarly(string detail) =>
        (detail.Trim().Length > 0 ? detail.Trim() + " " : "") + KeptNothing + " Check the model before continuing.";

    public const string SkippedNothingKeptText =
        "Skipped by the user (Skip step): this step can only finish whole, so it ended and nothing of it was kept (rolled " +
        "back, or no report written). Move on to the next step of the plan and don't run this step again unless the user asks.";

    public const string StoppedNothingKeptText =
        "Stopped by the user (Stop): the step in progress can only finish whole, so it was rolled back and nothing of it " +
        "was kept (earlier steps are kept). Don't start more steps: say briefly what was done and what wasn't.";

    public const string SkippedText =
        "Skipped by the user (Skip step): this step ended early; what it finished is kept. Move on to the next step of the " +
        "plan and don't run this step again unless the user asks.";

    public const string StoppedText =
        "Stopped by the user (Stop): the item in progress was rolled back and finished items are kept. Don't start more " +
        "steps: say briefly what was done and what wasn't.";

    public const string PausedText =
        "Paused by the user before this step started; nothing was run. Send this same call again to continue: it waits " +
        "until the user resumes.";
}

/// <summary>
/// Progress of the chat task's current step, for the card and the job window: which tool, item n of N with a label, and
/// the steps that started and ended. Written from any thread (Revit's, during a batch), read by polling, so a long batch
/// never floods a UI thread with events. Thread-safe.
/// </summary>
internal static class JobFeed
{
    private static readonly object Gate = new();
    private static readonly List<StepEvent> Pending = [];
    private static Snapshot _now = new();
    private static bool _longSignalled;

    /// <summary>One step started or ended (State: running, done, failed, skipped, stopped).</summary>
    internal sealed record StepEvent(string Tool, string State, string Reason, DateTime At);

    /// <summary>Where the current step is. A copy: safe to keep.</summary>
    internal sealed record Snapshot
    {
        public string Tool { get; init; } = "";
        /// <summary>The batch's name ("Fill title blocks"); a new name starts a new count.</summary>
        public string What { get; init; } = "";
        /// <summary>Items finished (done or failed) and the batch's size; Total 0: no item count for this step.</summary>
        public int Done { get; init; }
        public int Total { get; init; }
        /// <summary>The item being worked on ("A-101").</summary>
        public string Label { get; init; } = "";
        public DateTime StepStarted { get; init; }
        /// <summary>When the first item of this count was reported, and how many were done then (for the rate).</summary>
        public DateTime FirstItem { get; init; }
        public int DoneAtFirst { get; init; }
        public bool Paused { get; init; }
        public bool Running { get; init; }
        /// <summary>Changes on every update, so a poller can tell nothing moved.</summary>
        public long Version { get; init; }
    }

    /// <summary>Called once per step, on the reporting thread, when a step that reports items has run for a while (the
    /// chat opens its job window then: Revit's thread is busy, so the pane can't answer clicks).</summary>
    internal static Action? LongStep;

    /// <summary>How long a step runs before <see cref="LongStep"/>.</summary>
    internal static TimeSpan LongAfter = TimeSpan.FromSeconds(1.5);

    public static Snapshot Now { get { lock (Gate) return _now; } }

    /// <summary>The step events since the last call (oldest first).</summary>
    public static List<StepEvent> Drain()
    {
        lock (Gate)
        {
            var list = Pending.ToList();
            Pending.Clear();
            return list;
        }
    }

    /// <summary>Forgets everything (a new chat task starts).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Pending.Clear();
            _now = new Snapshot { Version = _now.Version + 1 };
            _longSignalled = false;
        }
    }

    public static void StepStarted(string tool) => StepStarted(tool, DateTime.UtcNow);

    internal static void StepStarted(string tool, DateTime at)
    {
        lock (Gate)
        {
            _now = new Snapshot { Tool = tool, StepStarted = at, Running = true, Version = _now.Version + 1 };
            _longSignalled = false;
            Add(new StepEvent(tool, "running", "", at));
        }
    }

    public static void StepEnded(string tool, string state, string reason) => StepEnded(tool, state, reason, DateTime.UtcNow);

    internal static void StepEnded(string tool, string state, string reason, DateTime at)
    {
        lock (Gate)
        {
            _now = _now with { Running = false, Paused = false, Version = _now.Version + 1 };
            Add(new StepEvent(tool, state, reason, at));
        }
    }

    /// <summary>
    /// Item progress: <paramref name="done"/> of <paramref name="total"/> finished, <paramref name="label"/> next (or ""
    /// when the batch is through). A different <paramref name="what"/> starts a new count.
    /// </summary>
    public static void Item(string what, int done, int total, string label) => Item(what, done, total, label, DateTime.UtcNow);

    internal static void Item(string what, int done, int total, string label, DateTime at)
    {
        Action? signal = null;
        lock (Gate)
        {
            var fresh = _now.What != what || _now.Total != total || done < _now.Done;
            _now = _now with
            {
                What = what, Done = Math.Max(0, Math.Min(done, total)), Total = Math.Max(0, total), Label = label,
                FirstItem = fresh || _now.FirstItem == default ? at : _now.FirstItem,
                DoneAtFirst = fresh || _now.FirstItem == default ? done : _now.DoneAtFirst,
                StepStarted = _now.StepStarted == default ? at : _now.StepStarted,
                Version = _now.Version + 1,
            };
            if (!_longSignalled && _now.Running && at - _now.StepStarted >= LongAfter)
            {
                _longSignalled = true;
                signal = LongStep;
            }
        }
        try { signal?.Invoke(); } catch (Exception) { /* the window is a convenience: the batch goes on */ }
    }

    public static void SetPaused(bool paused)
    {
        lock (Gate) _now = _now with { Paused = paused, Version = _now.Version + 1 };
    }

    private static void Add(StepEvent e)
    {
        Pending.Add(e);
        if (Pending.Count > 200) Pending.RemoveAt(0);
    }
}

/// <summary>Progress and time-left arithmetic and wording for the card ("37% (148 of 400 items) · about 2 min left").</summary>
internal static class JobMath
{
    public static int Percent(int done, int total) =>
        total <= 0 ? 0 : Math.Max(0, Math.Min(100, (int)Math.Floor(100.0 * done / total)));

    /// <summary>"37% (148 of 400 items)".</summary>
    public static string Count(int done, int total, string unit = "items") =>
        $"{Percent(done, total)}% ({done} of {total}{(unit.Length > 0 ? " " + unit : "")})";

    /// <summary>
    /// Time left from the item rate since the first item: null until two items are done and a second has passed (too
    /// early to say), or when nothing is left.
    /// </summary>
    public static TimeSpan? Remaining(TimeSpan sinceFirst, int doneSinceFirst, int left)
    {
        if (left <= 0 || doneSinceFirst < 2 || sinceFirst < TimeSpan.FromSeconds(1)) return null;
        var perItem = sinceFirst.TotalSeconds / doneSinceFirst;
        return TimeSpan.FromSeconds(Math.Min(perItem * left, TimeSpan.FromDays(1).TotalSeconds));
    }

    /// <summary>Time left from a snapshot at <paramref name="now"/>.</summary>
    public static TimeSpan? Remaining(JobFeed.Snapshot s, DateTime now) =>
        s.FirstItem == default ? null : Remaining(now - s.FirstItem, s.Done - s.DoneAtFirst, s.Total - s.Done);

    /// <summary>"45 s", "2 min 10 s", "1 h 5 min".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return t.Seconds == 0 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalMinutes} min {t.Seconds} s";
        return $"{(int)t.TotalSeconds} s";
    }

    /// <summary>"a few seconds left", "about 40 s left", "about 3 min left", "about 1 h 20 min left"; "" when unknown.</summary>
    public static string Left(TimeSpan? remaining)
    {
        if (remaining is not { } r) return "";
        if (r.TotalSeconds < 10) return "a few seconds left";
        if (r.TotalSeconds < 60) return $"about {(int)(Math.Round(r.TotalSeconds / 5) * 5)} s left";
        if (r.TotalMinutes < 60) return $"about {(int)Math.Round(r.TotalMinutes)} min left";
        var minutes = (int)Math.Round(r.TotalMinutes);
        return $"about {minutes / 60} h {minutes % 60} min left";
    }

    /// <summary>The card's progress line for a running step: "37% (148 of 400 items) · 1 min 2 s · about 2 min left".</summary>
    public static string Line(JobFeed.Snapshot s, DateTime now)
    {
        var parts = new List<string>();
        if (s.Total > 0) parts.Add(Count(s.Done, s.Total));
        if (s.StepStarted != default) parts.Add(Duration(now - s.StepStarted));
        if (s.Paused) parts.Add("paused");
        else if (Left(Remaining(s, now)) is { Length: > 0 } left) parts.Add(left);
        return string.Join(" · ", parts);
    }

    /// <summary>"tag_elements_in_view" → "Tag elements in view".</summary>
    public static string Title(string tool)
    {
        var words = tool.Replace('_', ' ').Trim();
        return words.Length == 0 ? "" : char.ToUpper(words[0], CultureInfo.InvariantCulture) + words.Substring(1);
    }
}

/// <summary>
/// Messages the user sends while a chat task runs. Claude Code's stream-json input has no way to reach a turn that is
/// already running (a user line written to stdin is only read as the next turn), so a note waits here and the bridge adds
/// it to the result of the next chat tool call (<see cref="JobControl.AfterStep"/>): Claude reads it before deciding its
/// next step. A note no step picked up by the end of the turn is sent as the next message instead. Thread-safe.
/// </summary>
internal static class JobNotes
{
    private static readonly object Gate = new();
    private static readonly List<(string Id, string Text)> Waiting = [];
    private static int _next;

    /// <summary>Raised on the delivering thread with the tool whose result carried the notes, and their ids.</summary>
    public static event Action<string, IReadOnlyList<string>>? Delivered;

    public static int Count { get { lock (Gate) return Waiting.Count; } }

    /// <summary>Queues a note; returns its id.</summary>
    public static string Add(string text)
    {
        lock (Gate)
        {
            // Unique across sessions too: a saved chat keeps the ids of its "will apply" lines.
            var id = "n" + (++_next).ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Waiting.Add((id, text.Trim()));
            return id;
        }
    }

    /// <summary>Takes every waiting note (they are then no longer added to a result).</summary>
    public static List<(string Id, string Text)> Take()
    {
        lock (Gate)
        {
            var list = Waiting.ToList();
            Waiting.Clear();
            return list;
        }
    }

    /// <summary>Adds the waiting notes to a tool result's text (and says they were delivered); the text as is when none.</summary>
    public static string Attach(string tool, string text)
    {
        var notes = Take();
        if (notes.Count == 0) return text;
        try { Delivered?.Invoke(tool, notes.Select(n => n.Id).ToList()); } catch (Exception) { }
        return JobText.Add(text, null, Block(notes.Select(n => n.Text)));
    }

    /// <summary>How the notes read to Claude.</summary>
    public static string Block(IEnumerable<string> notes)
    {
        var sb = new StringBuilder("[Message from the user, sent while this step ran. Apply it from here on; it may change what you do next:]");
        foreach (var n in notes) sb.Append("\n- ").Append(n.Replace("\r", "").Replace("\n", "\n  "));
        return sb.ToString();
    }
}

/// <summary>
/// Adds the job card's own lines to a tool result: the user's Skip or Stop before it, the user's messages after it. A
/// result that is JSON stays valid JSON: an object gets the lines as its first and last fields
/// (<see cref="ActionField"/>, <see cref="NoteField"/>), anything else is wrapped as <c>result</c>. Other text gets them as
/// paragraphs before and after.
/// </summary>
internal static class JobText
{
    public const string ActionField = "user_action", NoteField = "message_from_user", ResultField = "result";

    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Add(string text, string? before, string? after)
    {
        if (before is null && after is null) return text;
        if (Json(text, before, after) is { } json) return json;
        if (before is not null) text = before + "\n\n" + text;
        if (after is not null) text = text + "\n\n" + after;
        return text;
    }

    private static string? Json(string text, string? before, string? after)
    {
        var start = text.TrimStart();
        if (start.Length == 0 || start[0] is not ('{' or '[')) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            // Lines added earlier (a skip, then messages) are kept: ours go before or after them.
            string? Earlier(string name) =>
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var action = Join(before, Earlier(ActionField));
            var note = Join(Earlier(NoteField), after);
            using var stream = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(stream, Options))
            {
                w.WriteStartObject();
                if (action is not null) w.WriteString(ActionField, action);
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in root.EnumerateObject())
                        if (!p.NameEquals(ActionField) && !p.NameEquals(NoteField)) p.WriteTo(w);
                }
                else
                {
                    w.WritePropertyName(ResultField);
                    root.WriteTo(w);
                }
                if (note is not null) w.WriteString(NoteField, note);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException) { return null; }
    }

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + "\n\n" + b;
}
