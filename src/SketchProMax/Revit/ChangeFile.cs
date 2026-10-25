using System.Collections.Concurrent;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SketchProMax.Settings;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Writes each open model's change file (format and rules: <see cref="ChangeFileMath"/>; playing it back:
/// <see cref="ChangeReplay"/>). Nothing here saves or changes the model, and nothing waits on Revit's thread for the
/// disk: DocumentChanged only notes which elements changed; at the next idle moments their states are read, a few
/// hundred at a time, and a background thread appends the lines. Deleted elements are written at once, by the
/// UniqueId seen when they were last read or selected (Revit can't tell it once they are gone).
/// </summary>
internal static class ChangeFile
{
    private sealed class Model
    {
        public string Path = "", Folder = "", Central = "";
        public readonly HashSet<long> Pending = [];
        public bool Incoming;
        /// <summary>The values last written for each element, so a later line holds only what changed.</summary>
        public readonly Dictionary<long, Dictionary<string, string>> Written = [];
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Model> Models = new(StringComparer.OrdinalIgnoreCase);
    // The UniqueId of each element seen (read, added or selected), so a deletion can still name it.
    private static readonly Dictionary<string, Dictionary<long, (string Unique, string? Category)>> Known = new(StringComparer.OrdinalIgnoreCase);
    private const int KnownCap = 200_000;
    private static readonly BlockingCollection<Action> Disk = new();
    private static Thread? _writer;
    private static Autodesk.Revit.ApplicationServices.Application? _app;
    private static UIApplication? _ui;
    private static bool _on = true;
    private static int _lastSettings;

    /// <summary>For the field test: where the folders go instead of Documents (and models in the temp folder count).</summary>
    internal static string? RootForTest;

    /// <summary>For the field test: keep the file even when Settings turned it off.</summary>
    internal static bool ForceForTest;

    private static bool On => _on || ForceForTest;

    public static string Root => RootForTest ?? M.Root;

    public static void Start(UIApplication app)
    {
        if (_app is not null) return;
        _ui = app;
        _app = app.Application;
        ReadSettings();
        if (_writer is not { IsAlive: true })
        {
            _writer = new Thread(() => { foreach (var write in Disk.GetConsumingEnumerable()) { try { write(); } catch (Exception) { } } })
            { IsBackground = true, Name = "Sketch ProMax change file", Priority = ThreadPriority.BelowNormal };
            _writer.Start();
        }
        _app.DocumentChanged += OnChanged;
        _app.DocumentOpened += OnOpened;
        _app.DocumentClosing += OnClosing;
        _app.DocumentSaved += OnSaved;
        _app.DocumentSavedAs += OnSavedAs;
        _app.DocumentSynchronizingWithCentral += OnSyncing;
        _app.DocumentSynchronizedWithCentral += OnSynced;
        _app.DocumentReloadingLatest += OnReloading;
        _app.DocumentReloadedLatest += OnReloaded;
        try { app.SelectionChanged += OnSelection; } catch (Exception) { /* deletions of elements never read are named by id only */ }
        RevitDispatcher.Idle += OnIdle;
        // Models already open (a newer core switched to while Revit runs): their file carries on.
        try { foreach (Document doc in _app.Documents) Track(doc, opened: false); } catch (Exception) { }
    }

    public static void Stop()
    {
        if (_app is not { } app) return;
        RevitDispatcher.Idle -= OnIdle;
        try
        {
            app.DocumentChanged -= OnChanged;
            app.DocumentOpened -= OnOpened;
            app.DocumentClosing -= OnClosing;
            app.DocumentSaved -= OnSaved;
            app.DocumentSavedAs -= OnSavedAs;
            app.DocumentSynchronizingWithCentral -= OnSyncing;
            app.DocumentSynchronizedWithCentral -= OnSynced;
            app.DocumentReloadingLatest -= OnReloading;
            app.DocumentReloadedLatest -= OnReloaded;
            if (_ui is { } ui) ui.SelectionChanged -= OnSelection;
        }
        catch (Exception) { }
        // What was noted but not read yet is read now, while the models are still open.
        try { foreach (Document doc in app.Documents) ReadPending(doc, int.MaxValue); } catch (Exception) { }
        _app = null;
        _ui = null;
        lock (Gate) { Models.Clear(); Known.Clear(); }
        Flush(TimeSpan.FromSeconds(3));
    }

    public static void ReadSettings()
    {
        _on = AgentSettings.Load().ChangeFile;
        _lastSettings = Environment.TickCount;
    }

    // ---- Files ----------------------------------------------------------------------------------------------------------

    private static string PathOf(Document doc)
    {
        try { return doc.PathName ?? ""; }
        catch (Exception) { return ""; }
    }

    private static bool Watched(Document doc, out string path)
    {
        path = "";
        try
        {
            if (!doc.IsValidObject || doc.IsFamilyDocument || doc.IsLinked) return false;
            path = PathOf(doc);
            if (path.Length == 0) return false;
            // The field test's own throwaway projects stay out of Documents.
            if (RootForTest is null && path.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static Model? For(Document doc)
    {
        if (!Watched(doc, out var path)) return null;
        lock (Gate) return Models.TryGetValue(path, out var model) ? model : null;
    }

    /// <summary>For the field test: watch <paramref name="doc"/> (its folder under <see cref="RootForTest"/>), read what changed now, and stop watching.</summary>
    internal static void TrackForTest(Document doc) => Track(doc, opened: false);

    internal static void ReadNow(Document doc) => ReadPending(doc, int.MaxValue);

    internal static void ForgetForTest(Document doc)
    {
        if (For(doc) is { } model) lock (Gate) { Models.Remove(model.Path); Known.Remove(model.Path); }
    }

    public static string FolderFor(string modelPath) => System.IO.Path.Combine(Root, M.FolderName(modelPath));

    public static string CurrentFor(string modelPath) => System.IO.Path.Combine(FolderFor(modelPath), M.CurrentName);

    private static void Track(Document doc, bool opened)
    {
        if (!Watched(doc, out var path)) return;
        string central = "";
        try
        {
            if (doc.IsWorkshared && !doc.IsDetached) central = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath());
        }
        catch (Exception) { }
        var model = new Model { Path = path, Folder = FolderFor(path), Central = central };
        lock (Gate) Models[path] = model;
        // Opened: a file left from before (Revit closed by itself, or closed without saving) is kept with its time.
        if (opened) Queue(() => KeepLeftover(model));
    }

    private static void KeepLeftover(Model model)
    {
        var current = System.IO.Path.Combine(model.Folder, M.CurrentName);
        if (!File.Exists(current)) return;
        if (M.Read(ReadLines(current)).Elements.Count == 0) { File.Delete(current); return; }
        var kept = System.IO.Path.Combine(model.Folder, M.LeftoverName(File.GetLastWriteTime(current)));
        File.Move(current, kept);
        foreach (var old in M.LeftoversToDelete(Directory.GetFiles(model.Folder, "*.txt")))
            try { File.Delete(old); } catch (Exception) { }
        // Shown once the model is open: Revit closed by itself last time, or the model was closed without saving.
        App.Dispatcher.Later(() => UI.RecoverWindow.OfferLeftover(kept));
    }

    /// <summary>Saved or synchronized: the file is cleared, the one cleared kept as "before last save".</summary>
    private static void Cleared(Document doc)
    {
        if (For(doc) is not { } model) return;
        lock (Gate) { model.Pending.Clear(); model.Written.Clear(); }
        Queue(() =>
        {
            var current = System.IO.Path.Combine(model.Folder, M.CurrentName);
            if (!File.Exists(current)) return;
            var last = System.IO.Path.Combine(model.Folder, M.LastSaveName);
            if (File.Exists(last)) File.Delete(last);
            File.Move(current, last);
        });
    }

    private static void Append(Model model, IEnumerable<M.Line> lines)
    {
        var text = string.Join(Environment.NewLine, lines.Select(M.Write));
        if (text.Length == 0) return;
        var path = model.Path;
        var central = model.Central;
        Queue(() =>
        {
            Directory.CreateDirectory(model.Folder);
            var file = System.IO.Path.Combine(model.Folder, M.CurrentName);
            var fresh = !File.Exists(file) || new FileInfo(file).Length == 0;
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            if (fresh)
            {
                writer.WriteLine(M.HeaderComment);
                writer.WriteLine(M.Write(new M.Line { Kind = M.Kinds.Model, At = Now, Path = path, Central = central.Length > 0 ? central : null }));
            }
            writer.WriteLine(text);
        });
    }

    private static volatile int _busy;

    private static void Queue(Action write)
    {
        try
        {
            if (Disk.IsAddingCompleted) return;
            Interlocked.Increment(ref _busy);
            Disk.Add(() => { try { write(); } finally { Interlocked.Decrement(ref _busy); } });
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>Waits (up to <paramref name="wait"/>) for the lines queued so far to reach the disk.</summary>
    internal static void Flush(TimeSpan wait)
    {
        var until = DateTime.Now + wait;
        while (_busy > 0 && DateTime.Now < until) Thread.Sleep(20);
    }

    internal static List<string> ReadLines(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ---- Events (best effort: nothing here may stop Revit's own work) -------------------------------------------------

    private static void OnOpened(object? sender, DocumentOpenedEventArgs e)
    {
        try { if (e.Document is { } doc) Track(doc, opened: true); } catch (Exception) { }
    }

    private static void OnClosing(object? sender, DocumentClosingEventArgs e)
    {
        try
        {
            if (For(e.Document) is not { } model) return;
            ReadPending(e.Document, 5000);
            lock (Gate) { Models.Remove(model.Path); Known.Remove(model.Path); }
        }
        catch (Exception) { }
    }

    private static void OnSaved(object? sender, DocumentSavedEventArgs e)
    {
        try { if (e.Status == RevitAPIEventStatus.Succeeded) Cleared(e.Document); } catch (Exception) { }
    }

    /// <summary>Save As: the model now lives at a new path, with a file of its own that starts empty.</summary>
    private static void OnSavedAs(object? sender, DocumentSavedAsEventArgs e)
    {
        try
        {
            if (e.Status != RevitAPIEventStatus.Succeeded || e.Document is not { } doc) return;
            Track(doc, opened: false);
            Cleared(doc);
        }
        catch (Exception) { }
    }

    private static void OnSyncing(object? sender, DocumentSynchronizingWithCentralEventArgs e) => Incoming(e.Document, true);

    private static void OnSynced(object? sender, DocumentSynchronizedWithCentralEventArgs e)
    {
        Incoming(e.Document, false);
        try { if (e.Status == RevitAPIEventStatus.Succeeded) Cleared(e.Document); } catch (Exception) { }
    }

    private static void OnReloading(object? sender, DocumentReloadingLatestEventArgs e) => Incoming(e.Document, true);

    private static void OnReloaded(object? sender, DocumentReloadedLatestEventArgs e) => Incoming(e.Document, false);

    /// <summary>While central's elements come in (reload latest, sync), what changes is other people's work, not this file's.</summary>
    private static void Incoming(Document doc, bool on)
    {
        try { if (For(doc) is { } model) model.Incoming = on; } catch (Exception) { }
    }

    private static void OnSelection(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (!On) return;
            var doc = e.GetDocument();
            if (For(doc) is not { } model) return;
            var ids = e.GetSelectedElements();
            if (ids.Count > 2000) return;
            foreach (var id in ids)
                if (doc.GetElement(id) is { } element) Remember(model, id.Value, element.UniqueId, element.Category?.Name);
        }
        catch (Exception) { }
    }

    private static void Remember(Model model, long id, string unique, string? category)
    {
        lock (Gate)
        {
            if (!Known.TryGetValue(model.Path, out var map)) Known[model.Path] = map = [];
            if (map.Count < KnownCap || map.ContainsKey(id)) map[id] = (unique, category);
        }
    }

    private static void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            if (!On) return;
            var doc = e.GetDocument();
            if (For(doc) is not { } model || model.Incoming) return;
            lock (Gate)
            {
                foreach (var id in e.GetAddedElementIds()) model.Pending.Add(id.Value);
                foreach (var id in e.GetModifiedElementIds()) model.Pending.Add(id.Value);
            }
            var deleted = e.GetDeletedElementIds();
            if (deleted.Count == 0) return;
            var tx = string.Join(", ", e.GetTransactionNames().Distinct());
            var lines = new List<M.Line>();
            lock (Gate)
            {
                Known.TryGetValue(model.Path, out var map);
                foreach (var id in deleted)
                {
                    model.Pending.Remove(id.Value);
                    var known = map is not null && map.TryGetValue(id.Value, out var k) ? k : default;
                    lines.Add(new M.Line { Kind = M.Kinds.Deleted, At = Now, Id = id.Value, Unique = known.Unique, Category = known.Category, Tx = tx.Length > 0 ? tx : null });
                }
            }
            Append(model, lines);
        }
        catch (Exception) { /* this change is missing from the file; the model is not affected */ }
    }

    /// <summary>At Revit's idle moments: the elements changed since the last look are read and written.</summary>
    private static void OnIdle(UIApplication app)
    {
        try
        {
            if (unchecked(Environment.TickCount - _lastSettings) > 60_000) ReadSettings();
            if (!On) return;
            foreach (Document doc in app.Application.Documents) ReadPending(doc, M.ReadPerIdle);
        }
        catch (Exception) { }
    }

    private static void ReadPending(Document doc, int max)
    {
        if (For(doc) is not { } model) return;
        List<long> ids;
        lock (Gate)
        {
            if (model.Pending.Count == 0) return;
            ids = model.Pending.Take(max).ToList();
            foreach (var id in ids) model.Pending.Remove(id);
        }
        var lines = new List<M.Line>();
        foreach (var id in ids)
        {
            try
            {
                if (doc.GetElement(new ElementId(id)) is not { } element || Capture(doc, element) is not { } line) continue;
                Remember(model, id, element.UniqueId, element.Category?.Name);
                lock (Gate)
                {
                    model.Written.TryGetValue(id, out var written);
                    var all = line.Values ?? [];
                    var changed = M.Changed(all, written);
                    line.Values = changed.Count > 0 ? changed : null;
                    if (written is null && model.Written.Count < KnownCap) model.Written[id] = written = [];
                    if (written is not null) foreach (var v in all) written[M.KeyOf(v)] = M.Print(v);
                }
                lines.Add(line);
            }
            catch (Exception) { }
        }
        Append(model, lines);
    }

    // ---- An element's state ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Built-in values left out: the type and family (written as Type), and values Revit works out itself.
    /// </summary>
    private static readonly HashSet<BuiltInParameter> Skip =
    [
        BuiltInParameter.ELEM_TYPE_PARAM, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM, BuiltInParameter.ELEM_FAMILY_PARAM,
        BuiltInParameter.SYMBOL_ID_PARAM, BuiltInParameter.ELEM_PARTITION_PARAM,
    ];

    /// <summary>The element as it is now; null for what isn't worth a line (Revit's own bookkeeping elements).</summary>
    internal static M.Line? Capture(Document doc, Element element)
    {
        var category = element.Category;
        if (category is null && element is not ElementType) return null;
        if (element is View || element is ElementType { Category: null } || ChangeShapes.IsSketchLine(element)) return null;
        var line = new M.Line
        {
            Kind = M.Kinds.State, At = Now, Id = element.Id.Value, Unique = element.UniqueId,
            Category = category?.Name, CategoryId = category?.Id.Value ?? 0,
            What = ChangeShapes.Kind(element) ?? element switch { Level => "level", Grid => "grid", ElementType => "type", Wall => "wall", FamilyInstance => "instance", _ => "other" },
        };
        try { line.Name = element.Name; } catch (Exception) { }
        if (element is not ElementType && doc.GetElement(element.GetTypeId()) is ElementType type)
        {
            line.Type = $"{type.FamilyName}: {type.Name}";
            line.TypeUnique = type.UniqueId;
        }
        var levelId = element.LevelId;
        if (levelId == ElementId.InvalidElementId)
            levelId = element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId()
                      ?? element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
        if (doc.GetElement(levelId) is Level level) { line.Level = level.Name; line.LevelUnique = level.UniqueId; }
        if (element is FamilyInstance fi)
        {
            line.Host = fi.Host?.UniqueId;
            line.Structural = fi.StructuralType.ToString();
            try { line.FacingFlipped = fi.FacingFlipped; line.HandFlipped = fi.HandFlipped; } catch (Exception) { }
        }
        switch (element)
        {
            case Level lv:
                line.Elevation = M.Round(lv.Elevation);
                break;
            case Grid g when g.Curve is Line gl:
                line.P = P(gl.GetEndPoint(0));
                line.Q = P(gl.GetEndPoint(1));
                break;
            default:
                if (element.Location is LocationPoint lp)
                {
                    line.P = P(lp.Point);
                    try { line.Rotation = M.Round(lp.Rotation); } catch (Exception) { }
                }
                else if (element.Location is LocationCurve { Curve: Line or Arc } lc)
                {
                    var approx = false;
                    var seg = ChangeShapes.Segs(lc.Curve, ref approx).First();
                    (line.P, line.Q, line.M) = (seg.P, seg.Q, seg.M);
                }
                break;
        }
        // Sketches (floors, ceilings, roofs, openings, regions, toposolids, edited wall profiles), paths and shape points.
        try { ChangeShapes.Read(doc, element, line); } catch (Exception) { }
        var values = new List<M.Value>();
        foreach (Parameter p in element.Parameters)
        {
            if (values.Count >= M.MaxValues) break;
            try
            {
                if (p.IsReadOnly || !p.HasValue || p.StorageType == StorageType.None) continue;
                var bip = p.Definition is InternalDefinition def ? def.BuiltInParameter : BuiltInParameter.INVALID;
                if (Skip.Contains(bip)) continue;
                var v = new M.Value { Bip = bip == BuiltInParameter.INVALID ? 0 : (int)bip, Guid = p.IsShared ? p.GUID.ToString("N") : null };
                // The name only where nothing else finds the value again (project parameters that aren't shared).
                if (v.Bip == 0 && v.Guid is null) v.Name = p.Definition?.Name ?? "";
                switch (p.StorageType)
                {
                    case StorageType.Double: v.Kind = "d"; v.Number = M.Round(p.AsDouble()); break;
                    case StorageType.Integer: v.Kind = "i"; v.Whole = p.AsInteger(); break;
                    case StorageType.String: v.Kind = "s"; v.Text = p.AsString() ?? ""; break;
                    case StorageType.ElementId:
                        v.Kind = "e";
                        var target = p.AsElementId();
                        v.Ref = target == ElementId.InvalidElementId ? "" : doc.GetElement(target)?.UniqueId;
                        if (v.Ref is null) continue;
                        break;
                }
                values.Add(v);
            }
            catch (Exception) { }
        }
        if (values.Count > 0) line.Values = values;
        return line;
    }

    private static M.Xyz P(XYZ p) => new(M.Round(p.X), M.Round(p.Y), M.Round(p.Z));

    /// <summary>The time on a line, to the second (shorter lines).</summary>
    private static DateTime Now => new(DateTime.Now.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
}
