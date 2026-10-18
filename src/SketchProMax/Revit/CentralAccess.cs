using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SketchProMax.Bridge;
using SketchProMax.Tools;

namespace SketchProMax.Revit;

/// <summary>
/// The one place Sketch ProMax asks the central model (file server or cloud) for something on its own: borrowing the
/// elements an edit needs. Ownership and "changed in central" checks read Revit's local cache and reach nothing.
///
/// When the central model doesn't answer (someone is synchronizing, the network or the cloud has a hiccup), Revit shows
/// "Unable to Access the Model" and waits. So that a Sketch ProMax task costs at most one such dialog:
/// - only elements that need it are asked for, all in one request; when none do, nothing is asked at all;
/// - a busy central model (another user synchronizing) is asked once more after a short wait;
/// - after a failed request, or after that dialog appeared, nothing more is asked of that central model for
///   <see cref="Pause"/>: edits that would need it stop with a clear message instead of one dialog per item
///   (<see cref="ItemBatch"/> also stops a batch whose item failed while the dialog showed).
/// </summary>
internal static class CentralAccess
{
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(4);
    private const string Dialog = "\"Unable to Access the Model\"";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Since, DateTime Until, string Why)> Paused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times the dialog was seen this session: a batch compares it before and after an item.</summary>
    public static int Seen { get; private set; }

    /// <summary>For the field test: elements to treat as in central and not this user's (as on a real model after a sync).</summary>
    internal static Func<ElementId, bool>? NeedsBorrowForTest;

    /// <summary>For the field test: makes a request fail as if the central model didn't answer (Busy: as if it were synchronizing).</summary>
    internal static Func<(string Why, bool Busy)?>? FailForTest;

    /// <summary>Requests sent to the central model this session (the field test checks there is one per task).</summary>
    internal static int Requests;

    private static bool Shared(Document doc)
    {
        try { return doc.IsWorkshared && !doc.IsDetached && !doc.IsLinked; }
        catch (Exception) { return false; }
    }

    /// <summary>The elements that must be borrowed before they can be edited: in central and not this user's (local cache only).</summary>
    public static List<ElementId> NeedsBorrow(Document doc, IEnumerable<ElementId> ids)
    {
        var list = new List<ElementId>();
        var test = NeedsBorrowForTest;
        if (test is null && !Shared(doc)) return list;
        foreach (var id in ids.Distinct())
        {
            if (id == ElementId.InvalidElementId || doc.GetElement(id) is null) continue;
            if (test is not null) { if (test(id)) list.Add(id); continue; }
            try
            {
                // Ours already, or new since the last sync (ours too). Someone else's stays in: the cache may be out of
                // date, and the one request refreshes it for the ownership check that names the owner.
                if (WorksharingUtils.GetCheckoutStatus(doc, id) == CheckoutStatus.OwnedByCurrentUser) continue;
                if (WorksharingUtils.GetModelUpdatesStatus(doc, id) == ModelUpdatesStatus.NotYetInCentral) continue;
                list.Add(id);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        return list;
    }

    /// <summary>
    /// Borrows what the edit needs in one request. Null when nothing stands in the way (what another user owns is left to
    /// the caller's ownership check); else why the central model couldn't be asked, for the task's result.
    /// Call with no transaction open.
    /// </summary>
    public static string? Borrow(Document doc, IEnumerable<ElementId> ids)
    {
        var need = NeedsBorrow(doc, ids);
        if (need.Count == 0) return null;
        if (Waiting(doc) is { } wait) return wait;
        for (var attempt = 0; ; attempt++)
        {
            var (failure, busy) = Ask(doc, need);
            if (failure is null) return null;
            if (busy && attempt == 0)
            {
                Jobs.Report($"The central model is busy (someone is synchronizing): asking again in {RetryAfter.TotalSeconds:0} s");
                Thread.Sleep(RetryAfter);
                continue;
            }
            var until = Hold(doc, failure);
            return $"couldn't borrow {need.Count} element(s) from the central model: {failure} Sketch ProMax won't ask it again until " +
                   $"{until:HH:mm:ss}, so Revit doesn't keep showing {Dialog}. Run it again after that.";
        }
    }

    private static (string? Why, bool Busy) Ask(Document doc, List<ElementId> need)
    {
        Requests++;
        try
        {
            if (FailForTest?.Invoke() is { } test) return (test.Why, test.Busy);
            WorksharingUtils.CheckoutElements(doc, need);
            return (null, false);
        }
        catch (Autodesk.Revit.Exceptions.CentralModelContentionException ex) { return (Workshare.Explain(ex), true); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return ("it didn't answer, and the wait was cancelled.", false); }
        catch (Autodesk.Revit.Exceptions.CentralFileCommunicationException ex) { return (Workshare.Explain(ex), false); }
        catch (Autodesk.Revit.Exceptions.CentralModelException ex) { return (Workshare.Explain(ex), false); }
        // Anything else (an element Revit won't lend): not the central model's fault; the ownership check reports it per item.
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return (null, false); }
    }

    /// <summary>Why nothing is asked of this model's central model right now, or null.</summary>
    public static string? Waiting(Document doc)
    {
        if (NeedsBorrowForTest is null && !Shared(doc)) return null;
        var key = TimeLog.Key(doc);
        lock (Gate)
        {
            if (!Paused.TryGetValue(key, out var p)) return null;
            if (DateTime.Now >= p.Until) { Paused.Remove(key); return null; }
            return $"the central model didn't answer at {p.Since:HH:mm:ss} ({p.Why.TrimEnd('.')}). Sketch ProMax waits until {p.Until:HH:mm:ss} " +
                   $"before asking it again, so Revit doesn't keep showing {Dialog}. Run it again after that.";
        }
    }

    private static DateTime Hold(Document doc, string why)
    {
        var now = DateTime.Now;
        var until = now + Pause;
        lock (Gate) Paused[TimeLog.Key(doc)] = (now, until, why);
        try { ActivityLog.Note("central_unavailable", $"{doc.Title}: {why}"); } catch (Exception) { }
        return until;
    }

    /// <summary>For the field test: forget every pause.</summary>
    internal static void Reset()
    {
        lock (Gate) Paused.Clear();
    }

    // ---- Revit's own dialog ------------------------------------------------------------------------------------------

    public static void Watch(UIApplication app)
    {
        try { app.DialogBoxShowing += OnDialog; } catch (Exception) { }
    }

    public static void Unwatch(UIApplication app)
    {
        try { app.DialogBoxShowing -= OnDialog; } catch (Exception) { }
    }

    /// <summary>
    /// Revit is showing "Unable to Access the Model" (for any reason: our request, a colleague's sync, the user's own edit).
    /// It is left to show (Revit retries while it waits); it only stops Sketch ProMax asking that central model for a while.
    /// </summary>
    private static void OnDialog(object? sender, DialogBoxShowingEventArgs e)
    {
        try
        {
            if (e is not TaskDialogShowingEventArgs task || !IsUnavailable(task.Message)) return;
            if ((sender as UIApplication)?.ActiveUIDocument?.Document is not { } doc) return;
            Noticed(doc, $"Revit showed {Dialog}" + (Jobs.Running is { } job ? $" during {job.Tool}." : "."));
        }
        catch (Exception) { /* a dialog must never fail because of us */ }
    }

    /// <summary>The dialog's text, in English Revit ("...the model X.rvt is not currently available").</summary>
    internal static bool IsUnavailable(string? message) =>
        message is { Length: > 0 } m && m.IndexOf("is not currently available", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The dialog appeared for this model (also called by the field test).</summary>
    internal static void Noticed(Document doc, string why)
    {
        Seen++;
        Hold(doc, why);
    }
}
