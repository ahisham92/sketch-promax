using Autodesk.Revit.DB;
using SketchProMax.Bridge;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Runs a batch of edits one item at a time, so one item Revit refuses (a borrowed element, a cut it can't make, a
/// dimension whose references stopped being parallel) does not undo the whole batch. Each item gets its own
/// transaction with a failure handler that rolls back just that item and records why; all of them sit in one
/// transaction group, so the batch is still a single Undo step. Call it with no transaction open.
///
/// In a workshared model, elements another user owns are skipped up front (and named with their owner); with
/// borrow set, the batch first tries to borrow the elements it needs.
/// </summary>
internal static class ItemBatch
{
    public sealed record Outcome(string Item, bool Ok, string? Error, object? Value);

    public sealed class Result
    {
        public List<Outcome> Items { get; } = [];
        public int Done => Items.Count(i => i.Ok);
        public List<Outcome> Failed => Items.Where(i => !i.Ok).ToList();

        /// <summary>Failed items only the user can unblock (see the manual argument): pass to <see cref="Handoff.Finish"/>.</summary>
        public List<HandoffItem> Handoffs { get; } = [];

        /// <summary>A compact summary for a tool's result: counts, then each failure with its reason.</summary>
        public object Summary(int maxFailures = 50) => new
        {
            done = Done,
            failed = Items.Count - Done,
            failures = Failed.Take(maxFailures).Select(f => new { item = f.Item, reason = f.Error }).ToList(),
            failuresTruncated = Items.Count - Done > maxFailures ? true : (bool?)null,
        };
    }

    /// <param name="items">What to do, one entry per item.</param>
    /// <param name="label">How the item is named in the result (an id, a name...).</param>
    /// <param name="elements">The existing elements the item edits, for the ownership check (may be empty).</param>
    /// <param name="apply">Does the edit inside the item's transaction; return a value to report, or throw to fail the item.</param>
    /// <param name="manual">For a failed item and its reason: the step only the user can do to unblock it, or null.
    /// Such items are listed in <see cref="Result.Handoffs"/>; Continue on one runs that item again.</param>
    public static Result Run<T>(Document doc, string name, IEnumerable<T> items, Func<T, string> label,
        Func<T, IEnumerable<ElementId>> elements, Func<T, object?> apply, bool borrow = true, bool cancellable = true,
        Func<T, string, HandoffStep?>? manual = null)
    {
        var list = items.ToList();
        var result = new Result();
        if (doc.IsModifiable)
            throw new InvalidOperationException("ItemBatch needs to start its own transactions; a transaction is already open.");

        if (doc.IsWorkshared)
        {
            var wanted = list.SelectMany(elements).Where(id => id != ElementId.InvalidElementId).Distinct().ToList();
            if (borrow && wanted.Count > 0)
            {
                try { WorksharingUtils.CheckoutElements(doc, wanted); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { /* what can't be borrowed is reported per item below */ }
            }
        }

        using var group = new TransactionGroup(doc, $"Sketch ProMax: {name}");
        group.Start();
        Revit.JobUndo.GroupOpened();
        var index = 0;
        // Skip step or Stop from the chat's job card ends the batch at an item boundary, like cancel_job.
        var halted = JobState.Running;
        foreach (var item in list)
        {
            var what = label(item);
            index++;
            if (cancellable && halted == JobState.Running)
            {
                // A boundary with no transaction open: a pause waits here (the group stays open, so still one Undo step).
                JobFeed.Item(name, index - 1, list.Count, what);
                var state = JobControl.AtBoundary(name, index - 1);
                if (state is JobState.Skipping or JobState.Stopping) halted = state;
            }
            if (halted != JobState.Running) { result.Items.Add(new(what, false, JobControl.NotDone(halted), null)); continue; }
            if (cancellable && Jobs.CancelRequested) { result.Items.Add(new(what, false, "not done: stopped on request (cancel_job)", null)); continue; }
            if (list.Count > 20 && index % 10 == 0) Jobs.Report($"{name}: {index} of {list.Count}");
            var locked = doc.IsWorkshared ? Locked(doc, elements(item)) : null;
            if (locked is not null) { result.Items.Add(new(what, false, locked, null)); continue; }

            var errors = new List<string>();
            using var t = new Transaction(doc, name);
            var options = t.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new Collector(errors));
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(false);
            t.SetFailureHandlingOptions(options);
            FailureGuard.Muted++;
            try
            {
                t.Start();
                object? value;
                try { value = apply(item); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    result.Items.Add(new(what, false, Message(ex), null));
                    continue;
                }
                if (cancellable && JobControl.StopNow(name, index - 1))
                {
                    // Stop pressed while this item ran: undo just this item; the finished ones are kept.
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    result.Items.Add(new(what, false, JobControl.RolledBack, null));
                    halted = JobState.Stopping;
                    continue;
                }
                var status = t.Commit();
                result.Items.Add(status == TransactionStatus.Committed
                    ? new(what, true, errors.Count > 0 ? "warnings: " + string.Join("; ", errors) : null, value)
                    : new(what, false, errors.Count > 0 ? string.Join("; ", errors) : $"Revit did not accept it ({status}).", null));
            }
            finally { FailureGuard.Muted--; }
        }

        if (cancellable && list.Count > 0) JobFeed.Item(name, result.Items.Count(i => !IsNotDone(i)), list.Count, "");

        // The time log writes in one more transaction of the group, so the batch stays one undo step.
        if (result.Done > 0) TimeLog.InBatch(doc, name, result.Done);
        if (result.Done > 0) group.Assimilate();
        else group.RollBack();
        Revit.JobUndo.GroupClosed(result.Done > 0);
        if (manual is not null)
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (result.Items[i] is not { Ok: false, Error: { } why } || manual(item, why) is not { } step) continue;
                step.Item ??= result.Items[i].Item;
                result.Handoffs.Add(new HandoffItem(step, !step.RunAgain ? null : () =>
                {
                    var again = Run(doc, name, [item], label, elements, apply, borrow, cancellable: false);
                    return again.Items[0].Ok ? again.Items[0].Value : throw new InvalidOperationException(again.Items[0].Error);
                }));
            }
        return result;
    }

    /// <summary>
    /// Like <see cref="Run"/>, for items that each run a whole tool (code that starts its own transactions): each item gets
    /// a transaction group instead of a transaction, rolled back when the item throws or Stop is pressed while it runs, so
    /// a failed or stopped item leaves nothing behind. Progress, Pause, Skip step and Stop work at the item boundaries as
    /// in <see cref="Run"/>, and the batch is one transaction group (one Undo step, or part of the caller's).
    /// No ownership check or borrowing: the tools each item runs do their own. Call it with no transaction open.
    /// </summary>
    public static Result RunGrouped<T>(Document doc, string name, IEnumerable<T> items, Func<T, string> label, Func<T, object?> apply, bool cancellable = true)
    {
        var list = items.ToList();
        var result = new Result();
        if (doc.IsModifiable)
            throw new InvalidOperationException("ItemBatch needs to start its own transactions; a transaction is already open.");

        using var group = new TransactionGroup(doc, $"Sketch ProMax: {name}");
        group.Start();
        Revit.JobUndo.GroupOpened();
        var kept = false;
        try
        {
            var index = 0;
            var halted = JobState.Running;
            foreach (var item in list)
            {
                var what = label(item);
                index++;
                if (cancellable && halted == JobState.Running)
                {
                    JobFeed.Item(name, index - 1, list.Count, what);
                    var state = JobControl.AtBoundary(name, index - 1);
                    if (state is JobState.Skipping or JobState.Stopping) halted = state;
                }
                if (halted != JobState.Running) { result.Items.Add(new(what, false, JobControl.NotDone(halted), null)); continue; }
                if (cancellable && Jobs.CancelRequested) { result.Items.Add(new(what, false, "not done: stopped on request (cancel_job)", null)); continue; }
                if (list.Count > 20 && index % 10 == 0) Jobs.Report($"{name}: {index} of {list.Count}");

                using var one = new TransactionGroup(doc, name);
                one.Start();
                object? value;
                try { value = apply(item); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (one.HasStarted() && !one.HasEnded()) one.RollBack();
                    result.Items.Add(new(what, false, Message(ex), null));
                    continue;
                }
                if (cancellable && JobControl.StopNow(name, index - 1))
                {
                    // Stop pressed while this item ran: undo just this item; the finished ones are kept.
                    one.RollBack();
                    result.Items.Add(new(what, false, JobControl.RolledBack, null));
                    halted = JobState.Stopping;
                    continue;
                }
                one.Assimilate();
                result.Items.Add(new(what, true, null, value));
            }
            if (cancellable && list.Count > 0) JobFeed.Item(name, result.Items.Count(i => !IsNotDone(i)), list.Count, "");
            kept = result.Done > 0;
            if (kept) group.Assimilate();
            else group.RollBack();
        }
        finally
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            Revit.JobUndo.GroupClosed(kept);
        }
        return result;
    }

    /// <summary>An item a stop or skip ended the batch before (it never ran): not counted as finished in the progress.</summary>
    private static bool IsNotDone(Outcome o) => !o.Ok && o.Error is { } e && e.StartsWith("not done:", StringComparison.Ordinal);

    /// <summary>Why the user can't edit one of these elements right now, or null when all are editable.</summary>
    public static string? Locked(Document doc, IEnumerable<ElementId> ids)
    {
        foreach (var id in ids)
        {
            if (id == ElementId.InvalidElementId || doc.GetElement(id) is null) continue;
            var status = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
            if (status == CheckoutStatus.OwnedByOtherUser)
                return $"{id.Value} is borrowed by {owner}; ask them to synchronize and relinquish it.";
            var update = WorksharingUtils.GetModelUpdatesStatus(doc, id);
            if (update == ModelUpdatesStatus.UpdatedInCentral)
                return $"{id.Value} was changed in the central model since your last sync; synchronize first.";
        }
        return null;
    }

    private static string Message(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;

    /// <summary>Records each failure's text; warnings are dismissed, errors roll back the item's transaction.</summary>
    private sealed class Collector(List<string> errors) : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var error = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                var text = f.GetDescriptionText();
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
                else error = true;
                if (!errors.Contains(text)) errors.Add(text);
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
