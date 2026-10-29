using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SketchProMax.Chat;

namespace SketchProMax.Revit;

/// <summary>
/// For the chat's model note (Chat.ModelNote): per open model, what changed since the chat's last answer (counted by
/// category from Revit's DocumentChanged), the active view and the selection (from Revit's own events, so nothing is read
/// outside an API call). The chat marks each answer's end; the next message says what changed since. Best effort: when
/// an event can't be read the note leaves that part out.
/// </summary>
internal static class ChatModelWatch
{
    /// <summary>Elements looked up per change for their category; the rest of a large change counts as "other".</summary>
    private const int Lookups = 300;

    private static readonly Dictionary<string, ModelNote.Facts> Models = new(StringComparer.OrdinalIgnoreCase);
    private static UIApplication? _ui;

    public static void Start(UIApplication app)
    {
        if (_ui is not null) return;
        _ui = app;
        try
        {
            app.Application.DocumentChanged += OnChanged;
            app.Application.DocumentClosing += OnClosing;
            app.SelectionChanged += OnSelection;
            app.ViewActivated += OnView;
        }
        catch (Exception) { /* the note then says only what it knows */ }
    }

    public static void Stop()
    {
        if (_ui is not { } app) return;
        try
        {
            app.Application.DocumentChanged -= OnChanged;
            app.Application.DocumentClosing -= OnClosing;
            app.SelectionChanged -= OnSelection;
            app.ViewActivated -= OnView;
        }
        catch (Exception) { }
        _ui = null;
        lock (Models) Models.Clear();
    }

    private static ModelNote.Facts For(Document doc)
    {
        var key = ChatPane.ModelKey(doc);
        return Models.TryGetValue(key, out var facts) ? facts : Models[key] = new ModelNote.Facts();
    }

    /// <summary>The chat answered (its turn ended): changes are counted afresh from now.</summary>
    public static void Mark(string modelKey)
    {
        if (modelKey.Length == 0) return;
        lock (Models)
        {
            if (!Models.TryGetValue(modelKey, out var facts)) Models[modelKey] = facts = new ModelNote.Facts();
            facts.Known = _ui is not null;
            facts.Changed.Clear();
            facts.Deleted = 0;
        }
    }

    /// <summary>The note for the next message about <paramref name="modelKey"/> ("" when nothing is known).</summary>
    public static string Note(string modelKey, bool changes = true)
    {
        lock (Models)
        {
            if (!Models.TryGetValue(modelKey, out var facts)) return "";
            try { return ModelNote.Text(facts, changes); }
            catch (Exception) { return ""; }
        }
    }

    private static void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        var start = UiCost.Start();
        try
        {
            var doc = e.GetDocument();
            if (doc.IsFamilyDocument || doc.IsLinked) return;
            var changed = e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).ToList();
            var deleted = e.GetDeletedElementIds().Count;
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var looked = 0;
            foreach (var id in changed)
            {
                var name = looked++ < Lookups ? doc.GetElement(id)?.Category?.Name : null;
                name = string.IsNullOrEmpty(name) ? "other" : name!;
                kinds[name] = kinds.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            lock (Models)
            {
                var facts = For(doc);
                foreach (var pair in kinds) facts.Changed[pair.Key] = (facts.Changed.TryGetValue(pair.Key, out var n) ? n : 0) + pair.Value;
                facts.Deleted += deleted;
            }
        }
        catch (Exception) { /* best effort */ }
        finally { UiCost.End("chat noting a change", start); }
    }

    private static void OnSelection(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            if (doc.IsFamilyDocument) return;
            var ids = e.GetSelectedElements();
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var looked = 0;
            foreach (var id in ids)
            {
                if (looked++ >= Lookups) break;
                var name = doc.GetElement(id)?.Category?.Name;
                name = string.IsNullOrEmpty(name) ? "other" : name!;
                kinds[name] = kinds.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            if (ids.Count > Lookups) kinds["other"] = (kinds.TryGetValue("other", out var o) ? o : 0) + ids.Count - Lookups;
            var first = ids.Take(ModelNote.MaxIds).Select(id => id.Value).ToList();
            lock (Models)
            {
                var facts = For(doc);
                facts.SelectionKnown = true;
                facts.SelectedCount = ids.Count;
                facts.SelectedIds = first;
                facts.SelectedKinds = kinds;
            }
        }
        catch (Exception) { /* best effort */ }
    }

    private static void OnView(object? sender, ViewActivatedEventArgs e)
    {
        try
        {
            if (e.Document is not { IsFamilyDocument: false } doc || e.CurrentActiveView is not { } view) return;
            lock (Models)
            {
                var facts = For(doc);
                facts.View = view.Name;
                facts.ViewId = view.Id.Value;
            }
        }
        catch (Exception) { /* best effort */ }
    }

    private static void OnClosing(object? sender, DocumentClosingEventArgs e)
    {
        try { lock (Models) Models.Remove(ChatPane.ModelKey(e.Document)); } catch (Exception) { }
    }
}
