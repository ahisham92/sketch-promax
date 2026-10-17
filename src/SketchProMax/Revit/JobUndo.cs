using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace SketchProMax.Revit;

/// <summary>
/// Whether a finished chat task can be undone in one go ("Undo all" on its job card), and doing it. Offered only when
/// it is certain to undo exactly the task and nothing of the user's: one tool call of the task changed the model, its
/// changes were one Undo step (one transaction, or one item batch's transaction group), nothing else changed the model
/// while the task ran, and nothing has changed it since. Undo then posts Revit's own Undo (the top of the Undo list),
/// after checking the same model is still active. Anything uncertain hides the button instead.
///
/// Fed by the bridge (tool start and end, on Revit's thread), ItemBatch (its group opening and closing) and the time
/// log's DocumentChanged handler (no subscription of its own). Not thread-safe beyond that: Revit's thread only, apart
/// from <see cref="BeginJob"/>/<see cref="EndJob"/> on the pane's (the same thread in Revit).
/// </summary>
internal static class JobUndo
{
    private static bool _job, _tool, _spoilt, _changedSince, _undoPosted;
    private static int _steps, _groupDepth, _inGroup, _outside, _groups;
    private static string? _doc;
    // The model itself, not just its key: two open copies of one central model share a key, and a model closed and
    // opened again is another Document whose Undo list no longer holds the task.
    private static Document? _model;
    private static readonly HashSet<long> Elements = [];

    /// <summary>Counts chat tasks: Undo all is for the latest one only (an older card's button must never undo a newer task).</summary>
    public static int TaskNumber { get; private set; }

    /// <summary>Raised on Revit's thread when the posted Undo has run.</summary>
    public static event Action? Undone;

    /// <summary>Elements the task added, changed or deleted (its own records left out).</summary>
    public static int ElementsChanged => Elements.Count;

    /// <summary>A chat task starts.</summary>
    public static void BeginJob()
    {
        _job = true;
        _tool = _spoilt = _changedSince = _undoPosted = false;
        _steps = _groupDepth = _inGroup = _outside = _groups = 0;
        _doc = null;
        _model = null;
        TaskNumber++;
        Elements.Clear();
    }

    /// <summary>The chat task ended (what happens to the model from now on is the user's).</summary>
    public static void EndJob() => _job = false;

    /// <summary>A chat tool call starts on Revit's thread.</summary>
    public static void BeginTool()
    {
        if (!_job) return;
        _tool = true;
        _groupDepth = _inGroup = _outside = _groups = 0;
    }

    /// <summary>A chat tool call ended: counts it when it changed the model, and whether that was one Undo step.</summary>
    public static void EndTool()
    {
        if (!_job || !_tool) return;
        _tool = false;
        if (_inGroup + _outside == 0) return;
        _steps++;
        var oneStep = _inGroup == 0 && _outside == 1 || _outside == 0 && _groups == 1;
        if (!oneStep) _spoilt = true;
    }

    public static void GroupOpened()
    {
        if (_tool) _groupDepth++;
    }

    public static void GroupClosed(bool kept)
    {
        if (!_tool) return;
        _groupDepth = Math.Max(0, _groupDepth - 1);
        // Only an outermost group is an Undo step of its own.
        if (kept && _groupDepth == 0) _groups++;
    }

    /// <summary>From the time log's DocumentChanged handler: every committed, undone or redone transaction.</summary>
    public static void Changed(DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            var key = TimeLog.Key(doc);
            if (_undoPosted && e.Operation == UndoOperation.TransactionUndone && string.Equals(key, _doc, StringComparison.OrdinalIgnoreCase))
            {
                _undoPosted = false;
                _changedSince = true; // done: not offered again
                Undone?.Invoke();
                return;
            }
            if (!_job)
            {
                if (_doc is not null) _changedSince = true;
                return;
            }
            if (!_tool)
            {
                // The model changed during the task, but not by its tools (the user, or another add-in): not ours to undo.
                _spoilt = true;
                return;
            }
            if (_doc is null)
            {
                _doc = key;
                _model = doc;
            }
            else if (!string.Equals(_doc, key, StringComparison.OrdinalIgnoreCase) || !Same(_model, doc)) _spoilt = true;
            if (_groupDepth > 0) _inGroup++;
            else _outside++;
            var info = doc.ProjectInformation?.Id;
            foreach (var id in e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).Concat(e.GetDeletedElementIds()))
            {
                if (id == info) continue;
                if (doc.GetElement(id) is DataStorage) continue;
                Elements.Add(id.Value);
                if (Elements.Count > 100_000) break;
            }
        }
        catch (Exception) { _spoilt = true; }
    }

    /// <summary>True when "Undo all" may be offered for the task that just ended; otherwise why not.</summary>
    public static bool CanUndo(out string why)
    {
        why = _job ? "the task is still running"
            : _doc is null ? "the task didn't change the model"
            : _steps != 1 || _spoilt ? "the task changed the model in more than one Undo step, or something else changed it meanwhile"
            : _changedSince ? "the model has changed since the task ended"
            : "";
        return why.Length == 0;
    }

    /// <summary>
    /// Posts Revit's Undo for the task, after checking again: null when posted, else why not. Call from the external
    /// event (App.Dispatcher.InvokeFromEventAsync).
    /// </summary>
    public static string? Undo(UIApplication app, int task)
    {
        if (task != TaskNumber) return "Undo all is only for the latest task: another task has run since. Use Revit's Undo list to choose what to undo.";
        if (!CanUndo(out var why)) return "Undo all isn't safe now: " + why + ". Use Revit's Undo list to choose what to undo.";
        if (_model is not { IsValidObject: true }) return "The model the task changed was closed, so its Undo list is gone.";
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || !string.Equals(TimeLog.Key(doc), _doc, StringComparison.OrdinalIgnoreCase) || !Same(_model, doc))
            return "Switch back to the model the task changed, then press Undo all again.";
        if (doc.IsModifiable) return "Revit is in the middle of a change; try again in a moment.";
        var id = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
        if (id is null || !app.CanPostCommand(id)) return "Revit can't undo right now (an edit mode or a dialog may be open).";
        app.PostCommand(id);
        _undoPosted = true;
        return null;
    }

    private static bool Same(Document? a, Document b)
    {
        try { return a is { IsValidObject: true } && a.Equals(b); }
        catch (Exception) { return false; }
    }
}
