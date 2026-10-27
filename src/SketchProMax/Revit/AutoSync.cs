using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Settings;
using SketchProMax.Tools;

namespace SketchProMax.Revit;

/// <summary>
/// Revit's side of the auto-sync rule (<see cref="AutoSyncRule"/>, Settings > Chat): counts each model's changes from
/// DocumentChanged, starts again when the model is synchronized (by Sketch ProMax or by hand), and synchronizes with
/// central when Claude's work reaches the limit. The bridge pauses every chat job and runs <see cref="RunDue"/> between
/// calls (BridgeServer.SyncFirstAsync); an item batch bigger than what is left synchronizes at an item boundary
/// (<see cref="AtBoundary"/>, ItemBatch) and carries on. Only a local copy of a central model is synchronized, never a
/// detached copy or a model that isn't workshared.
/// </summary>
internal static class AutoSync
{
    private static Autodesk.Revit.ApplicationServices.Application? _app;
    private static int _lastSettings = Environment.TickCount;

    /// <summary>After a sync that failed, how long before the next try (so every call doesn't knock on a busy central model).</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
    private static DateTime _retryAt;

    /// <summary>True on Revit's thread while a Claude call runs (<see cref="InCallNow"/>): only then may a batch stop to sync.</summary>
    private static bool InCall;

    /// <summary>For the field test: treats the model as a local copy of a central model (the test project isn't one).</summary>
    internal static bool SharedForTest;

    /// <summary>For the field test: stands in for Revit's synchronize (returns why it failed, or null).</summary>
    internal static Func<Document, bool, string?>? SyncForTest;

    /// <summary>Syncs Sketch ProMax made this session (the field test checks one was made).</summary>
    internal static int Syncs;

    public static AutoSyncRule Rule => AutoSyncRule.Shared;

    public static void Start(UIApplication app)
    {
        if (_app is not null) return;
        ReadSettings();
        _app = app.Application;
        _app.DocumentChanged += OnChanged;
        _app.DocumentSynchronizedWithCentral += OnSynced;
        _app.DocumentClosing += OnClosing;
    }

    public static void Stop()
    {
        if (_app is not { } app) return;
        try
        {
            app.DocumentChanged -= OnChanged;
            app.DocumentSynchronizedWithCentral -= OnSynced;
            app.DocumentClosing -= OnClosing;
        }
        catch (Exception) { }
        _app = null;
    }

    /// <summary>Settings were saved (or a minute went by): the rule's on/off and limit.</summary>
    public static void ReadSettings()
    {
        _lastSettings = Environment.TickCount;
        var s = AgentSettings.Load();
        Rule.Configure(s.AutoSync, s.AutoSyncChanges);
    }

    public static string Key(Document doc) => doc.Title;

    /// <summary>A local copy of a central model: the only kind that can be synchronized.</summary>
    public static bool Shared(Document doc)
    {
        try { return SharedForTest && !doc.IsFamilyDocument || doc.IsWorkshared && !doc.IsDetached && !doc.IsLinked && !doc.IsFamilyDocument; }
        catch (Exception) { return false; }
    }

    /// <summary>The main chat's task now: a skip holds until the user's next message there starts a new one.</summary>
    public static int Task => JobUndo.TaskNumber;

    private static void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            if (doc.IsFamilyDocument || doc.IsLinked) return;
            Rule.Changed(Key(doc), Shared(doc),
                e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).Concat(e.GetDeletedElementIds()).Select(id => id.Value));
            if (unchecked(Environment.TickCount - _lastSettings) > 60_000) ReadSettings();
        }
        catch (Exception) { /* counting is best effort: the next change counts again */ }
    }

    private static void OnSynced(object? sender, DocumentSynchronizedWithCentralEventArgs e)
    {
        try { if (e.Status == RevitAPIEventStatus.Succeeded) Rule.Synced(Key(e.Document)); } catch (Exception) { }
    }

    private static void OnClosing(object? sender, DocumentClosingEventArgs e)
    {
        try { Rule.Synced(Key(e.Document)); } catch (Exception) { }
    }

    /// <summary>The model the last Claude call worked on (set on Revit's thread), for <see cref="MaybeDue"/>.</summary>
    private static volatile string? _active;

    /// <summary>A Claude call starts on <paramref name="doc"/> (Revit's thread).</summary>
    public static void Seen(Document doc)
    {
        try
        {
            _active = Key(doc);
            Rule.Changed(_active, Shared(doc), []);
        }
        catch (Exception) { }
    }

    /// <summary>True when the model Claude works on may be due (checked again on Revit's thread before anything is done).</summary>
    public static bool MaybeDue() => DateTime.UtcNow >= _retryAt && _active is { } model && Rule.Due(model, Task, DateTime.Now);

    /// <summary>For the auto_sync tool: the rule as it stands for the model Claude works on.</summary>
    public static string Describe() => Rule.Describe(_active, Task, DateTime.Now);

    /// <summary>
    /// Between calls, on Revit's thread: synchronizes the active model when it is due. Null when nothing was due; else what
    /// happened, in a line for Claude and the log.
    /// </summary>
    public static string? RunDue(UIApplication app)
    {
        if (app.ActiveUIDocument?.Document is not { } doc) return null;
        Seen(doc);
        if (!Due(doc)) return null;
        // Named for the Rescue window if Revit hangs on the central model; Revit's warnings during it don't stop it.
        string? said = null;
        RunningCall.Begin("auto sync", "auto_sync", doc.Title, null);
        try
        {
            FailureGuard.Run(() =>
            {
                said = Sync(doc, keepBorrowed: false);
                return ToolResult.Ok(new { });
            });
        }
        finally { RunningCall.End(); }
        return said;
    }

    /// <summary>
    /// At an item boundary of a batch (no transaction open, the caller's group closed): true when the active model is due
    /// and was synchronized, so the batch starts a new group and carries on. What it borrowed is kept, so the rest of the
    /// batch doesn't have to ask the central model again; the next sync between calls gives it back.
    /// </summary>
    public static bool DueAtBoundary(Document doc) => InCall && Due(doc);

    public static string AtBoundary(Document doc)
    {
        var what = Sync(doc, keepBorrowed: true);
        CallNotes.Add(what);
        return what;
    }

    /// <summary>What auto sync did during the running call (in its batches), for the call's result. Revit's thread only.</summary>
    private static readonly List<string> CallNotes = [];

    /// <summary>A Claude call starts (true) or ends: only a running call's batches may stop to sync.</summary>
    public static void InCallNow(bool on)
    {
        InCall = on;
        if (on) CallNotes.Clear();
    }

    /// <summary>The call's result, with what auto sync did during it first (so a cap on the text never cuts it off).</summary>
    public static ToolResult Noted(ToolResult result)
    {
        if (CallNotes.Count == 0) return result;
        var notes = string.Join(" ", CallNotes);
        CallNotes.Clear();
        return result with { Text = "Auto sync: " + notes + "\n\n" + result.Text };
    }

    /// <summary>For the field test: forget a failed sync's wait.</summary>
    internal static void ResetForTest() => _retryAt = default;

    private static bool Due(Document doc) =>
        DateTime.UtcNow >= _retryAt && Shared(doc) && !doc.IsModifiable && !doc.IsReadOnly && Rule.Due(Key(doc), Task, DateTime.Now);

    private static string Sync(Document doc, bool keepBorrowed)
    {
        var count = Rule.Count(Key(doc));
        if (CentralAccess.Waiting(doc) is { } wait)
        {
            _retryAt = DateTime.UtcNow + RetryAfter;
            return Note(doc, $"Auto sync waited: {wait}");
        }
        Jobs.Report($"Synchronizing with central ({count} changes since the last sync)");
        string? failed;
        try
        {
            failed = SyncForTest is { } test ? test(doc, keepBorrowed) : RevitSync(doc, keepBorrowed, count);
        }
        catch (Autodesk.Revit.Exceptions.CentralModelContentionException ex) { failed = Workshare.Explain(ex); }
        catch (Autodesk.Revit.Exceptions.CentralFileCommunicationException ex) { failed = Workshare.Explain(ex); }
        catch (Autodesk.Revit.Exceptions.CentralModelException ex) { failed = Workshare.Explain(ex); }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { failed = ex.Message; }
        if (failed is not null)
        {
            _retryAt = DateTime.UtcNow + RetryAfter;
            return Note(doc, $"Auto sync after {count} changes didn't go through: {failed.TrimEnd('.')}. The work carries on; it tries again in " +
                             $"{RetryAfter.TotalMinutes:0} minutes, or the user can synchronize by hand.");
        }
        Syncs++;
        _retryAt = default;
        Rule.Synced(Key(doc));
        return Note(doc, $"Synchronized \"{doc.Title}\" with central after {count} changes (auto sync, Settings > Chat)" +
                         (keepBorrowed ? ", in the middle of a step; the step carried on." : "; the work carried on where it stopped."));
    }

    private static string? RevitSync(Document doc, bool keepBorrowed, int count)
    {
        // Like Synchronize Now: elements borrowed for the work are given back (unless the step is still using them), the
        // worksets the user owns are kept, and the local copy is saved before and after.
        var relinquish = new RelinquishOptions(false) { CheckedOutElements = !keepBorrowed };
        var options = new SynchronizeWithCentralOptions
        {
            Comment = $"Sketch ProMax: auto sync after {count} changes",
            SaveLocalBefore = true,
            SaveLocalAfter = true,
            Compact = false,
        };
        options.SetRelinquishOptions(relinquish);
        doc.SynchronizeWithCentral(new TransactWithCentralOptions(), options);
        return null;
    }

    private static string Note(Document doc, string text)
    {
        try { ActivityLog.Note("auto_sync", $"{doc.Title}: {text}"); } catch (Exception) { }
        return text;
    }
}
