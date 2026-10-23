using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace SketchProMax.Revit;

/// <summary>
/// What changed in each open model since Sketch ProMax started watching it (opened, or open when Sketch ProMax started):
/// the ids added or changed, and the ids deleted, from Revit's DocumentChanged (every transaction, undo, redo and sync).
/// Submissions status takes them at Update status to read only the berths that changed (BerthStatusFacts.Touched).
/// </summary>
internal static class ChangeTracker
{
    /// <param name="Since">Since when changes are known: a reading from before it can't be trusted to be current.</param>
    /// <param name="Overflow">Too many changes to keep: read everything.</param>
    public sealed record Changes(DateTime Since, HashSet<long> Changed, HashSet<long> Deleted, bool Overflow);

    private sealed class Log
    {
        public DateTime Since;
        public HashSet<long> Changed = [];
        public HashSet<long> Deleted = [];
        public bool Overflow;
    }

    private const int Cap = 100_000;
    private static readonly Dictionary<string, Log> Logs = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _started = DateTime.MaxValue;
    private static Autodesk.Revit.ApplicationServices.Application? _app;

    public static void Start(UIApplication app)
    {
        if (_app is not null) return;
        _app = app.Application;
        _started = DateTime.Now;
        _app.DocumentChanged += OnChanged;
        _app.DocumentOpened += OnOpened;
        _app.DocumentClosing += OnClosing;
    }

    public static void Stop()
    {
        if (_app is null) return;
        try { _app.DocumentChanged -= OnChanged; _app.DocumentOpened -= OnOpened; _app.DocumentClosing -= OnClosing; } catch (Exception) { }
        _app = null;
        lock (Logs) Logs.Clear();
    }

    private static string Key(Document doc) => doc.Title;

    private static Log For(Document doc) => Logs.TryGetValue(Key(doc), out var log) ? log : Logs[Key(doc)] = new Log { Since = _started };

    private static void OnOpened(object? sender, DocumentOpenedEventArgs e)
    {
        try { if (e.Document is { IsFamilyDocument: false } doc) lock (Logs) Logs[Key(doc)] = new Log { Since = DateTime.Now }; } catch (Exception) { }
    }

    private static void OnClosing(object? sender, DocumentClosingEventArgs e)
    {
        try { lock (Logs) Logs.Remove(Key(e.Document)); } catch (Exception) { }
    }

    private static void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            if (doc.IsFamilyDocument || doc.IsLinked) return;
            lock (Logs)
            {
                var log = For(doc);
                if (log.Overflow) return;
                foreach (var id in e.GetAddedElementIds().Concat(e.GetModifiedElementIds())) log.Changed.Add(id.Value);
                foreach (var id in e.GetDeletedElementIds()) log.Deleted.Add(id.Value);
                if (log.Changed.Count + log.Deleted.Count > Cap) { log.Overflow = true; log.Changed.Clear(); log.Deleted.Clear(); }
            }
        }
        catch (Exception) { /* best effort: the next Update status reads everything it can't trust */ }
    }

    /// <summary>The changes so far, forgotten here (a reading takes them; <see cref="GiveBack"/> if it doesn't finish).</summary>
    public static Changes Take(Document doc)
    {
        lock (Logs)
        {
            var log = For(doc);
            var taken = new Changes(log.Since, log.Changed, log.Deleted, log.Overflow || _app is null);
            log.Changed = [];
            log.Deleted = [];
            log.Overflow = false;
            return taken;
        }
    }

    /// <summary>Puts back changes a reading took but didn't finish.</summary>
    public static void GiveBack(Document doc, Changes changes)
    {
        lock (Logs)
        {
            var log = For(doc);
            log.Changed.UnionWith(changes.Changed);
            log.Deleted.UnionWith(changes.Deleted);
            log.Overflow |= changes.Overflow;
        }
    }
}
