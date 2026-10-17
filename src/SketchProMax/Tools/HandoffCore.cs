namespace SketchProMax.Tools;

/// <summary>What the user chose on a hand-off: re-check and resume, skip this item, or stop the task.</summary>
public enum HandoffChoice { Continue, Skip, Stop }

/// <summary>
/// One step only the user can do (a Revit API limit, a missing family or DWG, an edit mode, a legend component, a
/// cut-edge dimension, a missing schedule): what to do, where in Revit, and why the tool can't do it itself.
/// </summary>
public sealed class HandoffStep
{
    /// <summary>What to do, as an instruction ("Place one legend component in any legend").</summary>
    public string What { get; set; } = "";
    /// <summary>Where in Revit (ribbon path, view, dialog).</summary>
    public string Where { get; set; } = "";
    /// <summary>Why the tool can't do it (the API limit, the missing file...).</summary>
    public string Why { get; set; } = "";
    /// <summary>A short machine-readable kind: api_limit, missing_family, missing_file, edit_mode, dialog, legend_component, cut_edge_dimension, missing_schedule, in_place.</summary>
    public string Kind { get; set; } = "manual";
    /// <summary>The item it is about, when the task works through several (a dimension id, a type name).</summary>
    public string? Item { get; set; }
    /// <summary>Optional: true once the user has done it. Runs on Revit's thread when the user presses Continue.</summary>
    public Func<bool>? Recheck { get; set; }
    /// <summary>
    /// For a step inside a batch: true (default) when Continue runs the item again after the check; false when the user
    /// does the item themselves, so Continue only checks it is there.
    /// </summary>
    public bool RunAgain { get; set; } = true;
    /// <summary>Optional: the name of a help guide to offer next to the step (see HelpGuides).</summary>
    public string? Guide { get; set; }

    /// <summary>The step as one paragraph, for logs and tool results.</summary>
    public string Text => $"{What}. Where: {Where}. Why: {Why}".Replace("..", ".");
}

/// <summary>
/// Thrown by a tool (or a helper it calls) when it can't go on until the user does something in Revit. The bridge
/// turns it into a hand-off: Continue runs the whole call again. It is an ArgumentException with the step as its
/// message, so code that catches argument problems per item still reports it as before.
/// </summary>
public sealed class ManualStepException(HandoffStep step) : ArgumentException(step.Text)
{
    public HandoffStep Step { get; } = step;
}

/// <summary>A step to hand over, and how to resume after it: run on Revit's thread; throw (or return an error) when it still can't.</summary>
public sealed record HandoffItem(HandoffStep Step, Func<object?>? Resume);

/// <summary>How one hand-off ended.</summary>
public sealed record HandoffOutcome(string Item, string What, string Choice, bool Done, string? Error, object? Value);

/// <summary>The state of one hand-off shown to the user, for the window and the chat card.</summary>
public enum HandoffState { Waiting, Checking, Closed }

/// <summary>
/// One hand-off on screen (a window or a chat card; both answer the same request). No Revit or WPF here: the UI
/// subscribes to <see cref="Changed"/> (raised on any thread) and calls <see cref="Answer"/>.
/// </summary>
public sealed class HandoffRequest
{
    private static int _next;
    private readonly object _gate = new();
    private TaskCompletionSource<HandoffChoice>? _answer;

    public HandoffRequest(HandoffStep step, int index, int count)
    {
        Step = step;
        Index = index;
        Count = count;
        Id = "h" + Interlocked.Increment(ref _next);
    }

    public string Id { get; }
    public HandoffStep Step { get; }
    /// <summary>1-based position among the task's hand-offs, and how many there are.</summary>
    public int Index { get; }
    public int Count { get; }
    /// <summary>Why it is asked again ("Not done yet: ..."), or null the first time.</summary>
    public string? Note { get; private set; }
    public HandoffState State { get; private set; } = HandoffState.Checking;
    /// <summary>The last line once closed ("Done", "Skipped", "Stopped").</summary>
    public string? Outcome { get; private set; }

    /// <summary>Raised on any thread whenever the state, note or outcome changes.</summary>
    public event Action<HandoffRequest>? Changed;

    /// <summary>From the UI: false when it is not waiting for an answer (already checking or closed).</summary>
    public bool Answer(HandoffChoice choice)
    {
        TaskCompletionSource<HandoffChoice>? answer;
        lock (_gate)
        {
            if (State != HandoffState.Waiting || _answer is null) return false;
            answer = _answer;
            _answer = null;
            State = choice == HandoffChoice.Continue ? HandoffState.Checking : HandoffState.Closed;
            if (choice != HandoffChoice.Continue) Outcome = choice == HandoffChoice.Skip ? "Skipped" : "Stopped";
        }
        Changed?.Invoke(this);
        answer.TrySetResult(choice);
        return true;
    }

    internal Task<HandoffChoice> Wait(string? note)
    {
        Task<HandoffChoice> task;
        lock (_gate)
        {
            Note = note;
            State = HandoffState.Waiting;
            _answer = new TaskCompletionSource<HandoffChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _answer.Task;
        }
        Changed?.Invoke(this);
        return task;
    }

    internal void Close(string outcome)
    {
        TaskCompletionSource<HandoffChoice>? answer;
        lock (_gate)
        {
            if (State == HandoffState.Closed && Outcome == outcome) return;
            answer = _answer;
            _answer = null;
            State = HandoffState.Closed;
            Outcome = outcome;
        }
        answer?.TrySetResult(HandoffChoice.Stop);
        Changed?.Invoke(this);
    }
}

/// <summary>
/// The hand-off loop, with no Revit or WPF: for each item, ask the user; on Continue re-check (and resume) on Revit's
/// thread, and ask again with what is still missing until it works or the user skips or stops. The Revit side
/// (<c>Handoff</c>) supplies how to show a request and how to run work on Revit's thread; tests supply plain functions.
/// </summary>
public static class HandoffLoop
{
    /// <summary>Continue is tried at most this often per item; then the item counts as not done (the user can't get stuck).</summary>
    public const int MaxTries = 10;

    /// <param name="show">Shows a new request (window or chat card); null when <paramref name="respond"/> answers instead.</param>
    /// <param name="respond">Answers without UI (the field test's TestResponder); null to ask the user.</param>
    /// <param name="onRevit">Runs work on Revit's thread (inline when already there).</param>
    /// <param name="stopRequested">True when the task was cancelled (cancel_job, Cancel in Revit): the open request closes as Stop.</param>
    /// <param name="failed">The error in a value Resume returned (a tool's error result), or null when it worked.</param>
    public static async Task<List<HandoffOutcome>> RunAsync(IReadOnlyList<HandoffItem> items, Action<HandoffRequest>? show,
        Func<HandoffStep, string?, HandoffChoice>? respond, Func<Func<object?>, Task<object?>> onRevit, Func<bool> stopRequested,
        Func<object?, string?>? failed = null)
    {
        var outcomes = new List<HandoffOutcome>();
        var stopped = false;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var label = item.Step.Item ?? item.Step.What;
            if (stopped || stopRequested())
            {
                stopped = true;
                outcomes.Add(new(label, item.Step.What, "stopped", false, "not done: the task was stopped", null));
                continue;
            }
            var request = new HandoffRequest(item.Step, i + 1, items.Count);
            var shown = false;
            string? note = null;
            HandoffOutcome? outcome = null;
            for (var attempt = 1; outcome is null; attempt++)
            {
                HandoffChoice choice;
                if (respond is not null) choice = respond(item.Step, note);
                else
                {
                    var wait = request.Wait(note);
                    if (!shown) { show?.Invoke(request); shown = true; }
                    choice = await WaitOrStop(wait, stopRequested).ConfigureAwait(false);
                }
                if (choice == HandoffChoice.Skip) { outcome = new(label, item.Step.What, "skipped", false, "skipped by the user", null); break; }
                if (choice == HandoffChoice.Stop) { stopped = true; outcome = new(label, item.Step.What, "stopped", false, "the user stopped the task here", null); break; }

                string? problem = null;
                object? value = null;
                try
                {
                    value = await onRevit(() =>
                    {
                        if (item.Step.Recheck is { } check && !check()) return new NotYet("Not done yet: " + item.Step.What + ".");
                        return item.Resume?.Invoke();
                    }).ConfigureAwait(false);
                    if (value is NotYet notYet) problem = notYet.Why;
                    else if (failed?.Invoke(value) is { } error) problem = "It still didn't work: " + FirstLine(error);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    problem = "It still didn't work: " + FirstLine((ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex).Message);
                }
                if (problem is null) { outcome = new(label, item.Step.What, "continued", true, null, value); break; }
                if (attempt >= MaxTries) { outcome = new(label, item.Step.What, "continued", false, problem + $" (gave up after {MaxTries} tries)", null); break; }
                note = problem;
            }
            request.Close(outcome!.Done ? "Done" : outcome.Choice switch { "skipped" => "Skipped", "stopped" => "Stopped", _ => "Not done" });
            outcomes.Add(outcome);
        }
        return outcomes;
    }

    private static async Task<HandoffChoice> WaitOrStop(Task<HandoffChoice> answer, Func<bool> stopRequested)
    {
        while (!answer.IsCompleted)
        {
            if (stopRequested()) return HandoffChoice.Stop;
            await Task.WhenAny(answer, Task.Delay(400)).ConfigureAwait(false);
        }
        return await answer.ConfigureAwait(false);
    }

    /// <summary>A one-line summary of how the hand-offs ended ("2 done, 1 skipped").</summary>
    public static string Summary(IReadOnlyList<HandoffOutcome> outcomes)
    {
        if (outcomes.Count == 0) return "nothing to hand over";
        var parts = new List<string>();
        void Add(int n, string what) { if (n > 0) parts.Add($"{n} {what}"); }
        Add(outcomes.Count(o => o.Done), "done");
        Add(outcomes.Count(o => !o.Done && o.Choice == "continued"), "still not done");
        Add(outcomes.Count(o => o.Choice == "skipped"), "skipped");
        Add(outcomes.Count(o => o.Choice == "stopped"), "stopped");
        return string.Join(", ", parts);
    }

    internal static string FirstLine(string text)
    {
        var line = (text ?? "").Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 300 ? line.Substring(0, 300) + "…" : line;
    }

    private sealed record NotYet(string Why);
}
