using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Hands a step over to the user when only they can do it, and resumes after it. A task never ends on just a message:
/// the user sees what to do, where in Revit and why, with Continue (re-check and resume where it stopped), Skip this
/// item and Stop; in a window, or as a card in the chat when the chat is running the task.
///
/// Three ways in:
///   - <see cref="Finish"/>: a tool ends with steps for the user (one per item, e.g. from <see cref="ItemBatch"/>). Called
///     by Claude, the call stays open as the job's continuation (Revit is free meanwhile, so the user can work in it) and
///     its result, with how each step ended, arrives through wait_for_job. From the ribbon or a recipe the window still
///     opens and resumes; its result is shown when done.
///   - <see cref="ManualStepException"/>: a tool (or a helper) throws it; the bridge hands it over and Continue runs the
///     whole call again.
///   - <see cref="Ask"/>: a modal question for a step done outside Revit (copy a file into place), answered at once.
/// <see cref="TestResponder"/> answers every hand-off without UI, for the field test.
/// </summary>
public static class Handoff
{
    /// <summary>
    /// Answers every hand-off without UI (the field test): given the step and why it is asked again (null the first time),
    /// return Continue, Skip or Stop. Continue still re-checks and resumes; after <see cref="HandoffLoop.MaxTries"/> tries
    /// the item counts as not done. Set it back to null afterwards.
    /// </summary>
    public static Func<HandoffStep, string?, HandoffChoice>? TestResponder { get; set; }

    /// <summary>How the most recent hand-offs ended, oldest first (at most 50), for the field test and diagnostics.</summary>
    public static IReadOnlyList<HandoffOutcome> Recent { get { lock (Gate) return RecentList.ToList(); } }

    /// <summary>
    /// Set by the chat: shows a request as a card and returns true, or false when the chat isn't the place (no turn is
    /// running, or the pane is hidden), and the window opens instead. Called on Revit's UI thread.
    /// </summary>
    internal static Func<HandoffRequest, bool>? ChatPresenter;

    /// <summary>A request still on screen, by id (a chat card redrawn after scrolling finds its request again).</summary>
    public static HandoffRequest? Find(string id)
    {
        lock (Gate) return Open.TryGetValue(id, out var r) ? r : null;
    }

    private static readonly object Gate = new();
    private static readonly List<HandoffOutcome> RecentList = [];
    private static readonly Dictionary<string, HandoffRequest> Open = new();

    /// <summary>The UIApplication of the Revit call running a resume step (null outside one).</summary>
    [ThreadStatic] private static UIApplication? _current;

    /// <summary>More than 0 while a whole call runs again after a hand-off: a hand-off inside it fails it instead, and the outer one asks again.</summary>
    [ThreadStatic] private static int _nested;

    // ---- From tools ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The result of a tool call that leaves <paramref name="items"/> for the user. No items: <paramref name="result"/>
    /// unchanged. Otherwise the steps are handed over; the call's final result is <paramref name="result"/> (or, for one
    /// whole-call step, the call run again) with a "handoff" entry saying how each step ended.
    /// Call it last in Execute, with no transaction open.
    /// </summary>
    public static ToolResult Finish(string tool, ToolResult result, IReadOnlyList<HandoffItem> items)
    {
        if (items.Count == 0) return result;
        if (_nested > 0)
        {
            // Run again from an outer hand-off and still stuck: fail, so the outer window asks again with this reason.
            return new ToolResult(items[0].Step.Text + "\n" + result.Text, IsError: true, result.PngImage);
        }

        if (TestResponder is { } respond)
        {
            var outcomes = HandoffLoop.RunAsync(items, null, respond, work => Task.FromResult(work()), () => Jobs.CancelRequested, Failure)
                .GetAwaiter().GetResult();
            Remember(outcomes);
            return Final(result, items, outcomes);
        }

        // The same step for the same tool and item already open (a ribbon run, or a call that got here twice): don't open
        // a second card or window; the open one carries on, and its Continue runs the work once.
        var keys = items.Select(i => HandoffKeys.Step(tool, i.Step)).Distinct().ToList();
        lock (Gate)
        {
            if (keys.Any(Holding.Contains))
                return With(result, new
                {
                    status = "already with the user",
                    steps = items.Select(i => i.Step.Text).Take(20).ToList(),
                    next = "The same step is already open in Revit (a window or a card in the chat) from an earlier run; it carries on there when the user answers. " +
                           "Don't ask again; tell the user to finish it there.",
                });
            foreach (var key in keys) Holding.Add(key);
        }

        if (Jobs.CanContinue(tool) is { } job)
        {
            // The call stays open until the user answers; Revit is free meanwhile. The result comes through wait_for_job.
            job.Continuation = Hold(job, result, items, keys);
            Jobs.Report($"waiting for the user: {items[0].Step.What}");
            return With(result, new
            {
                status = "waiting for the user",
                steps = items.Select(i => i.Step.Text).Take(20).ToList(),
                next = $"The user is doing {(items.Count == 1 ? "a step" : $"{items.Count} steps")} in Revit that only they can do (a window or a card in the chat). " +
                       $"Call wait_for_job (job_id {job.Id}) for the result; don't repeat this request or send other model work meanwhile.",
            });
        }

        // From the ribbon, a quick tool or a recipe step: there is no call to hold, but the user still gets the steps.
        _ = Detached(tool, result, items, keys);
        return With(result, new
        {
            status = "handed to the user",
            steps = items.Select(i => i.Step.Text).Take(20).ToList(),
            next = "A window in Revit shows each step with Continue, Skip this item and Stop; it shows the result when done.",
        });
    }

    /// <summary>From the bridge: the tool threw <see cref="ManualStepException"/>. Continue runs the whole call again.</summary>
    internal static ToolResult FromException(IRevitTool tool, UIApplication app, ToolArgs args, ManualStepException ex)
    {
        if (_nested > 0) return ToolResult.Error(ex.Step.Text); // run again from an outer hand-off: it asks again
        return Finish(tool.Name, ToolResult.Error(ex.Step.Text), [new HandoffItem(ex.Step, () => RunAgain(tool, _current ?? app, args))]);
    }

    /// <summary>
    /// A modal question for a step done outside Revit (a file to copy into place, a network drive to connect): Continue
    /// runs <see cref="HandoffStep.Recheck"/> and asks again until it passes. Call on Revit's thread, from a tool.
    /// </summary>
    public static HandoffChoice Ask(HandoffStep step)
    {
        var respond = TestResponder;
        List<HandoffOutcome> outcomes;
        if (respond is not null || App.Ui is null)
            outcomes = HandoffLoop.RunAsync([new HandoffItem(step, null)], null, respond ?? ((_, _) => HandoffChoice.Stop),
                work => Task.FromResult(work()), () => Jobs.CancelRequested).GetAwaiter().GetResult();
        else
        {
            var choice = UI.HandoffWindow.AskModal(step);
            outcomes = [new(step.Item ?? step.What, step.What, choice switch { HandoffChoice.Continue => "continued", HandoffChoice.Skip => "skipped", _ => "stopped" },
                choice == HandoffChoice.Continue, null, null)];
        }
        Remember(outcomes);
        return outcomes[0] switch { { Done: true } => HandoffChoice.Continue, { Choice: "skipped" } => HandoffChoice.Skip, _ => HandoffChoice.Stop };
    }

    /// <summary>
    /// From the bridge, off Revit's thread: a call Revit didn't take (an edit mode, a dialog). The user finishes it and
    /// presses Continue (true: the caller sends the call again) or Skip/Stop (false).
    /// </summary>
    internal static async Task<bool> AskBlocked(HandoffStep step, CancellationToken ct)
    {
        if (TestResponder is { } respond) return respond(step, null) == HandoffChoice.Continue;
        if (App.Ui is null) return false;
        var outcomes = await HandoffLoop.RunAsync([new HandoffItem(step, null)], Show, null,
            work => Task.FromResult(work()), () => ct.IsCancellationRequested).ConfigureAwait(false);
        Remember(outcomes);
        return outcomes[0].Done;
    }

    // ---- The loop ----------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> Hold(Job job, ToolResult result, IReadOnlyList<HandoffItem> items, List<string> keys)
    {
        ToolResult? final = null;
        try
        {
            var outcomes = await HandoffLoop.RunAsync(items, Show, null, OnRevit, () => job.CancelRequested, Failure).ConfigureAwait(false);
            Remember(outcomes);
            final = Final(result, items, outcomes);
        }
        catch (Exception ex)
        {
            final = ToolResult.Error($"The hand-off stopped unexpectedly ({ex.GetType().Name}: {ex.Message}). What the call did before it is kept.\n{result.Text}");
        }
        finally
        {
            Release(keys);
            job.Finished = DateTime.UtcNow;
            if (Jobs.Running == job) Jobs.Running = null;
        }
        return final!;
    }

    private static async Task Detached(string tool, ToolResult result, IReadOnlyList<HandoffItem> items, List<string> keys)
    {
        try
        {
            var outcomes = await HandoffLoop.RunAsync(items, Show, null, OnRevit, () => false, Failure).ConfigureAwait(false);
            Remember(outcomes);
            var final = Final(result, items, outcomes);
            App.Ui?.BeginInvoke(new Action(() =>
            {
                try { Commands.QuickTools.Show(tool, final, final.IsError ? "Not finished: " + HandoffLoop.Summary(outcomes) : "Done: " + HandoffLoop.Summary(outcomes)); }
                catch (Exception) { /* the outcome is still in Recent */ }
            }));
        }
        catch (Exception) { /* nothing is waiting for this result */ }
        finally { Release(keys); }
    }

    /// <summary>Hand-offs open now, by tool, step and item (see <see cref="HandoffKeys.Step"/>).</summary>
    private static readonly HashSet<string> Holding = new(StringComparer.OrdinalIgnoreCase);

    private static void Release(IEnumerable<string> keys)
    {
        lock (Gate) foreach (var key in keys) Holding.Remove(key);
    }

    private static Task<object?> OnRevit(Func<object?> work) =>
        App.Dispatcher.InvokeAsync(app =>
        {
            var before = _current;
            _current = app;
            try { return work(); }
            finally { _current = before; }
        });

    private static string? Failure(object? value) => value is ToolResult { IsError: true } r ? r.Text : null;

    private static object RunAgain(IRevitTool tool, UIApplication app, ToolArgs args)
    {
        _nested++;
        try
        {
            return FailureGuard.Run(() =>
            {
                try { return tool.Execute(app, args); }
                catch (ManualStepException again) { return ToolResult.Error(again.Step.Text); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return ToolResult.Error(BridgeServer.Describe(tool, ex)); }
            });
        }
        finally { _nested--; }
    }

    /// <summary>Shows a request: as a chat card when the chat takes it, else in a window. Any thread.</summary>
    private static void Show(HandoffRequest request)
    {
        lock (Gate) Open[request.Id] = request;
        request.Changed += r => { if (r.State == HandoffState.Closed) lock (Gate) Open.Remove(r.Id); };
        var ui = App.Ui;
        if (ui is null) { request.Answer(HandoffChoice.Stop); return; }
        ui.BeginInvoke(new Action(() =>
        {
            try
            {
                if (ChatPresenter?.Invoke(request) == true) return;
                UI.HandoffWindow.Open(request);
            }
            catch (Exception) { request.Answer(HandoffChoice.Stop); }
        }));
    }

    private static void Remember(IEnumerable<HandoffOutcome> outcomes)
    {
        lock (Gate)
        {
            RecentList.AddRange(outcomes);
            if (RecentList.Count > 50) RecentList.RemoveRange(0, RecentList.Count - 50);
        }
    }

    // ---- Results -----------------------------------------------------------------------------------------------------

    private static ToolResult Final(ToolResult result, IReadOnlyList<HandoffItem> items, List<HandoffOutcome> outcomes)
    {
        // One whole-call step that worked: the call's own result is the one run again.
        var basis = items.Count == 1 && outcomes is [{ Done: true, Value: ToolResult again }] ? again : result;
        var report = new
        {
            summary = HandoffLoop.Summary(outcomes),
            steps = outcomes.Select(o => new
            {
                item = o.Item, what = o.What, choice = o.Choice, done = o.Done, error = o.Error,
                value = o.Value is ToolResult or null ? null : o.Value,
            }).ToList(),
            next = outcomes.All(o => o.Done) ? null
                : outcomes.Any(o => o.Choice == "stopped") ? "The user stopped here. Ask them how to go on before doing more."
                : "Some steps were not done; tell the user which, and that they can ask again once they are.",
        };
        return With(basis, report);
    }

    /// <summary>The result with a "handoff" entry added (to its JSON object, or after its text).</summary>
    internal static ToolResult With(ToolResult result, object handoff)
    {
        var node = JsonSerializer.SerializeToNode(handoff);
        try
        {
            if (JsonNode.Parse(result.Text) is JsonObject json)
            {
                json["handoff"] = node;
                return result with { Text = json.ToJsonString() };
            }
        }
        catch (JsonException) { }
        return result with { Text = result.Text + "\nhandoff: " + node?.ToJsonString() };
    }
}
