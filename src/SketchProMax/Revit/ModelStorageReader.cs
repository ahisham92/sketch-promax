using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using SketchProMax.Tools;
using M = SketchProMax.Tools.ModelStorageMath;

namespace SketchProMax.Revit;

/// <summary>
/// Reads what a model holds for the Model storage window, in short steps (<see cref="RunFor"/>), so a big model never
/// holds Revit in one call: the file and the central model's file, one pass over the elements, the types, the geometry
/// of imports, in-place families and DirectShapes, images, links and Sketch ProMax's own data; then each loaded family
/// is measured by saving it to a temporary file (EditFamily, SaveAs, Close), one at a time. Measures are kept for a week
/// per model (%APPDATA%\SketchProMax\ModelStorage) so opening the window again is quick. Reads only: nothing in the
/// model changes. Call everything on Revit's thread, outside a transaction.
/// </summary>
internal sealed class ModelStorageReader
{
    /// <summary>A linked file: not inside the model, shown apart because links on a network drive slow opening.</summary>
    public sealed record Link(string Name, string Path, long? Bytes, bool Loaded, bool Cad);

    // Sketch ProMax's own schemas (ProjectStore, SubmissionChecklistStore, TimeLog): their stored text is measured.
    private static readonly HashSet<Guid> OurSchemas =
    [
        new("9d2e4f61-3b7a-4c58-8e19-a6c40b5d7f23"), new("4c7b2e90-6d15-4f3a-9b8e-2a61d0f5c3e7"), new("4b7f2c19-6d3e-4a85-9e21-c3a0f58d71b6"),
    ];

    private const int MaxIds = 10_000;
    private const long MaxGeometryObjects = 2_000_000;

    private readonly Document _doc;
    private readonly Queue<Action> _steps = new();
    private readonly Queue<M.Item> _toMeasure = new();
    private IEnumerator<Element>? _elements;
    private readonly Dictionary<string, M.Measure> _measures;
    private readonly string _measuresFile;
    private bool _measuresChanged;

    // What the passes gather before they become rows.
    private readonly Dictionary<(string Kind, string Name), M.Item> _rows = new();
    private readonly Dictionary<long, int> _placedPerType = new(), _placedPerFamily = new();
    private readonly Dictionary<long, List<long>> _instancesPerFamily = new(), _instancesPerType = new();
    private readonly HashSet<long> _onSheets = [];
    private readonly List<(View View, string Group)> _views = [];
    private readonly List<(Element Element, string Name, bool InPlace)> _shapes = [];
    private readonly List<M.Item> _families = [];

    public string Model { get; }
    public string FilePath { get; }
    public long? FileBytes { get; private set; }
    public DateTime? Saved { get; private set; }
    public M.Extras Extras { get; private set; } = new(0, null, "");
    public List<Link> Links { get; } = [];

    /// <summary>True once everything but the family measures is read: a first report can be shown.</summary>
    public bool QuickDone => _steps.Count == 0 && _elements is null;
    public int FamiliesTotal { get; private set; }
    public int FamiliesLeft => _toMeasure.Count;
    /// <summary>The family being measured next, for the progress line.</summary>
    public string Next => _toMeasure.Count > 0 ? _toMeasure.Peek().Name : "";
    public int StepsLeft => _steps.Count + (_elements is null ? 0 : 1);

    private ModelStorageReader(Document doc, bool fresh)
    {
        _doc = doc;
        Model = doc.Title;
        string path;
        try { path = doc.PathName ?? ""; }
        catch (Exception) { path = ""; }
        FilePath = path;
        _measuresFile = Path.Combine(Folder, M.MeasuresFile(path.Length > 0 ? path : doc.Title));
        string? json = null;
        if (!fresh)
            try { if (File.Exists(_measuresFile)) json = File.ReadAllText(_measuresFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _measures = M.ParseMeasures(json, DateTime.Now);
    }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "ModelStorage");

    /// <summary>Plans the reading. <paramref name="fresh"/>: measure every family again instead of reusing last week's measures.</summary>
    public static ModelStorageReader Plan(Document doc, bool fresh = false)
    {
        var r = new ModelStorageReader(doc, fresh);
        r._steps.Enqueue(r.ReadFiles);
        r._steps.Enqueue(() => r._elements = new FilteredElementCollector(r._doc).WhereElementIsNotElementType().GetEnumerator());
        r._steps.Enqueue(r.ReadTypes);
        r._steps.Enqueue(r.ReadFamilies);
        r._steps.Enqueue(r.ReadViews);
        r._steps.Enqueue(r.QueueShapes);
        r._steps.Enqueue(r.ReadImages);
        r._steps.Enqueue(r.ReadLinks);
        r._steps.Enqueue(r.ReadOurs);
        r._steps.Enqueue(r.Describe);
        return r;
    }

    /// <summary>
    /// Reads for about <paramref name="budget"/>, then returns: true while there is more to read or measure. One family
    /// measure or one big import may take longer than the budget on its own.
    /// </summary>
    public bool RunFor(TimeSpan budget)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < budget)
        {
            if (_elements is { } elements)
            {
                var n = 0;
                while (n++ < 2_000 && elements.MoveNext())
                {
                    try { Classify(elements.Current); }
                    catch (Exception) { /* an element Revit can't describe counts as Revit's own data */ }
                }
                if (n <= 2_000) { elements.Dispose(); _elements = null; }
                continue;
            }
            if (_steps.Count > 0)
            {
                var step = _steps.Dequeue();
                try { step(); }
                catch (Exception) { /* one part that can't be read leaves the rest */ }
                continue;
            }
            if (_toMeasure.Count == 0) { SaveMeasures(); return false; }
            MeasureFamily(_toMeasure.Dequeue());
        }
        return _steps.Count > 0 || _elements is not null || _toMeasure.Count > 0;
    }

    /// <summary>Stops measuring: the families not measured yet keep their estimates, and what was measured is kept.</summary>
    public void StopMeasuring()
    {
        _toMeasure.Clear();
        SaveMeasures();
    }

    /// <summary>Fresh copies of the rows so far (the window fits them to the file, which changes their sizes).</summary>
    public List<M.Item> Rows() =>
        _rows.Values.Concat(_families).Select(i => new M.Item
        {
            Kind = i.Kind, Name = i.Name, Detail = i.Detail, Bytes = i.Bytes, Measured = i.Measured, Count = i.Count, Unused = i.Unused,
            Ids = i.Ids, FamilyId = i.FamilyId,
        }).ToList();

    // ---- The passes ----------------------------------------------------------------------------------------------------

    private void ReadFiles()
    {
        if (FilePath.Length > 0)
            try
            {
                var file = new FileInfo(FilePath);
                if (file.Exists) { FileBytes = file.Length; Saved = file.LastWriteTime; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        long? central = null;
        var centralPath = "";
        try
        {
            if (_doc.IsWorkshared && _doc.GetWorksharingCentralModelPath() is { } model && !model.ServerPath)
            {
                centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(model);
                if (!string.Equals(centralPath, FilePath, StringComparison.OrdinalIgnoreCase) && File.Exists(centralPath))
                    central = new FileInfo(centralPath).Length;
            }
        }
        catch (Exception) { }
        int warnings;
        try { warnings = _doc.GetWarnings().Count; }
        catch (Exception) { warnings = 0; }
        Extras = new M.Extras(warnings, central, centralPath);
    }

    private M.Item Row(string kind, string name, long bytes, long id = 0)
    {
        if (!_rows.TryGetValue((kind, name), out var row)) _rows[(kind, name)] = row = new M.Item { Kind = kind, Name = name };
        row.Bytes += bytes;
        row.Count++;
        if (id != 0 && row.Ids.Count < MaxIds) row.Ids.Add(id);
        return row;
    }

    private static void Add<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull =>
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

    private static void Add(Dictionary<long, List<long>> lists, long key, long id)
    {
        if (!lists.TryGetValue(key, out var list)) lists[key] = list = [];
        if (list.Count < MaxIds) list.Add(id);
    }

    private void Classify(Element e)
    {
        var id = e.Id.Value;
        var typeId = e.GetTypeId();
        if (typeId != ElementId.InvalidElementId)
        {
            Add(_placedPerType, typeId.Value);
            Add(_instancesPerType, typeId.Value, id);
            if (_doc.GetElement(typeId) is FamilySymbol symbol)
            {
                Add(_placedPerFamily, symbol.Family.Id.Value);
                Add(_instancesPerFamily, symbol.Family.Id.Value, id);
            }
        }
        switch (e)
        {
            case DataStorage:
                return; // ReadOurs measures ours; others count as Revit's own data
            case Material or FillPatternElement or LinePatternElement or AppearanceAssetElement or PropertySetElement:
                Row(M.Types, Plural(e.GetType().Name), M.TypeOrSetting);
                return;
            case Viewport viewport:
                _onSheets.Add(viewport.ViewId.Value);
                return;
            case ScheduleSheetInstance placed:
                _onSheets.Add(placed.ScheduleId.Value);
                return;
            case View view:
                if (view.IsTemplate) Row(M.Types, "View templates", M.View);
                else if (ViewGroup(view) is { } group) _views.Add((view, group));
                return;
            case ImportInstance import:
                if (import.IsLinked) Row(M.Model, "CAD links", M.LinkInstance, id);
                else _shapes.Add((import, Name(e.Document.GetElement(typeId)) ?? "Imported CAD", false));
                return;
            case RevitLinkInstance:
                Row(M.Model, "Revit links", M.LinkInstance, id);
                return;
            case ImageInstance:
                return; // counted per picture (ReadImages)
            case Group:
                return; // its type is a row of its own (ReadTypes); its members are counted where they belong
            case DirectShape:
                _shapes.Add((e, "DirectShape: " + (e.Category?.Name ?? "no category"), false));
                return;
            case FamilyInstance fi when fi.Symbol?.Family is { IsInPlace: true } family:
                _shapes.Add((e, "In-place: " + family.Name, true));
                return;
        }
        var category = e.Category;
        if (category is null) return;
        if (e.OwnerViewId != ElementId.InvalidElementId || category.CategoryType == CategoryType.Annotation)
        {
            Row(M.Annotation, category.Name, M.Annotate, id);
            return;
        }
        if (category.CategoryType != CategoryType.Model) return;
        Row(M.Model, category.Name, e is SpatialElement ? M.Room : M.ModelElement, id);
    }

    /// <summary>A view's row name ("Floor plans"), or null for views Revit keeps for itself.</summary>
    private static string? ViewGroup(View view)
    {
        if (view is ViewSheet) return "Sheets";
        if (view is ViewSchedule schedule)
            return schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule ? null : "Schedules";
        return view.ViewType switch
        {
            ViewType.FloorPlan => "Floor plans",
            ViewType.CeilingPlan => "Ceiling plans",
            ViewType.EngineeringPlan => "Structural plans",
            ViewType.AreaPlan => "Area plans",
            ViewType.Section => "Sections",
            ViewType.Elevation => "Elevations",
            ViewType.Detail => "Detail views",
            ViewType.ThreeD => "3D views",
            ViewType.DraftingView => "Drafting views",
            ViewType.Legend => "Legends",
            ViewType.Walkthrough or ViewType.Rendering => "Renderings",
            _ => null,
        };
    }

    private void ReadViews()
    {
        foreach (var (view, group) in _views)
        {
            var sheet = group == "Sheets";
            var weight = sheet ? M.Sheet : group == "Schedules" ? M.Schedule : M.View;
            // Sheets are never "on a sheet"; legends can be on many; the rest is left over when it is on none.
            var unused = !sheet && !_onSheets.Contains(view.Id.Value);
            var row = Row(M.Views, unused ? group + " not on sheets" : group, weight, view.Id.Value);
            row.Unused = unused;
        }
        foreach (var row in _rows.Values.Where(r => r.Kind == M.Views))
            row.Detail = $"{N(row.Count)} {(row.Count == 1 ? "view" : "views")}{(row.Unused ? " · not placed on any sheet" : "")}";
        _views.Clear();
    }

    private void ReadTypes()
    {
        foreach (var e in new FilteredElementCollector(_doc).WhereElementIsElementType())
        {
            try
            {
                switch (e)
                {
                    case FamilySymbol or ImageType:
                        continue; // families and images are rows of their own
                    case CADLinkType:
                        continue; // an import is a row of its own (its geometry is read from its instance)
                    case GroupType group:
                        var placed = _placedPerType.TryGetValue(group.Id.Value, out var n) ? n : 0;
                        var row = Row(M.Groups, group.Name, M.GroupType + placed * M.GroupInstance);
                        row.Count = placed;
                        row.Unused = placed == 0;
                        row.Detail = (group.Category?.Name ?? "Group") + " · " + (placed == 0 ? "not placed" : $"{N(placed)} placed");
                        if (_instancesPerType.TryGetValue(group.Id.Value, out var ids)) row.Ids.AddRange(ids);
                        continue;
                }
                Row(M.Types, e.Category?.Name is { Length: > 0 } c ? c + " types" : "Other types", M.TypeOrSetting);
            }
            catch (Exception) { }
        }
        foreach (var row in _rows.Values.Where(r => r.Kind == M.Types)) row.Detail = $"{N(row.Count)} {(row.Count == 1 ? "item" : "items")}";
    }

    private void ReadFamilies()
    {
        foreach (var family in new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>())
        {
            try
            {
                if (family.IsInPlace) continue; // its instance is read with the shapes
                var key = family.Id.Value;
                var types = family.GetFamilySymbolIds().Count;
                var placed = _placedPerFamily.TryGetValue(key, out var n) ? n : 0;
                var category = family.FamilyCategory?.Name ?? "";
                var row = new M.Item
                {
                    Kind = M.Families, Name = family.Name, FamilyId = key, Count = placed,
                    // Profiles, balusters and nested parts are used through types, never placed: not called unused.
                    Unused = placed == 0 && !UsedThroughTypes(category),
                    Detail = (category.Length > 0 ? category + " · " : "") + $"{N(types)} {(types == 1 ? "type" : "types")} · "
                             + (placed == 0 ? "nothing placed" : $"{N(placed)} placed"),
                };
                if (_instancesPerFamily.TryGetValue(key, out var ids)) row.Ids.AddRange(ids);
                if (_measures.TryGetValue(M.FamilyKey(family.Name, category), out var known))
                {
                    row.Bytes = known.Bytes;
                    row.Measured = true;
                }
                else
                {
                    row.Bytes = M.FamilyEstimate(types);
                    if (family.IsEditable) _toMeasure.Enqueue(row);
                }
                _families.Add(row);
            }
            catch (Exception) { }
        }
        FamiliesTotal = _toMeasure.Count;
    }

    private static bool UsedThroughTypes(string category) =>
        category.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) >= 0 || category.IndexOf("Baluster", StringComparison.OrdinalIgnoreCase) >= 0
        || category.IndexOf("Rail", StringComparison.OrdinalIgnoreCase) >= 0 || category.IndexOf("Mullion", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The geometry of imports, in-place families and DirectShapes: one element per step, as an import can be big.</summary>
    private void QueueShapes()
    {
        foreach (var shape in _shapes)
            _steps.Enqueue(() => ReadShape(shape.Element, shape.Name, shape.InPlace));
        _shapes.Clear();
    }

    private void ReadShape(Element element, string name, bool inPlace)
    {
        long curves = 0, faces = 0, points = 0;
        var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
        if (element.OwnerViewId != ElementId.InvalidElementId && _doc.GetElement(element.OwnerViewId) is View owner) options.View = owner;
        else options.DetailLevel = ViewDetailLevel.Fine;
        void Walk(GeometryElement? geometry, int depth)
        {
            if (geometry is null || depth > 8) return;
            foreach (var g in geometry)
            {
                if (curves + faces + points > MaxGeometryObjects) return;
                switch (g)
                {
                    case Solid solid: faces += solid.Faces.Size; break;
                    case Mesh mesh: faces += Math.Max(1, mesh.NumTriangles / 2); break;
                    case PolyLine line: points += line.NumberOfCoordinates; break;
                    case Curve: curves++; break;
                    case Point: points++; break;
                    case GeometryInstance instance: Walk(instance.GetSymbolGeometry(), depth + 1); break;
                }
            }
        }
        try { Walk(element.get_Geometry(options), 0); }
        catch (Exception) { }
        var import = element is ImportInstance;
        var bytes = import ? M.CadBytes(curves, faces, points) : M.ShapeBytes(faces, inPlace);
        var row = Row(import ? M.Cad : M.Shapes, name, bytes, element.Id.Value);
        var lines = curves + points;
        row.Detail = import
            ? $"{(row.Count == 1 ? "1 import" : N(row.Count) + " imports")} · {N(lines)} lines and points{(faces > 0 ? $" · {N(faces)} faces" : "")}"
            : $"{(row.Count == 1 ? "1 element" : N(row.Count) + " elements")} · {N(faces)} faces";
        if (import && element.OwnerViewId != ElementId.InvalidElementId && _doc.GetElement(element.OwnerViewId) is View view)
            row.Detail += " · in " + view.Name;
    }

    private void ReadImages()
    {
        foreach (var e in new FilteredElementCollector(_doc).OfClass(typeof(ImageType)))
        {
            try
            {
                var image = (ImageType)e;
                long? file = null;
                try
                {
                    var path = image.Path;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) file = new FileInfo(path).Length;
                }
                catch (Exception) { }
                var placed = _placedPerType.TryGetValue(image.Id.Value, out var n) ? n : 0;
                var row = Row(M.Images, image.Name, M.ImageBytes(file, image.WidthInPixels, image.HeightInPixels));
                row.Measured = file is not null;
                row.Count = placed;
                row.Unused = placed == 0;
                row.Detail = $"{N(image.WidthInPixels)} × {N(image.HeightInPixels)} pixels · " + (placed == 0 ? "not placed" : $"{N(placed)} placed");
                if (_instancesPerType.TryGetValue(image.Id.Value, out var ids)) row.Ids.AddRange(ids);
            }
            catch (Exception) { }
        }
    }

    private void ReadLinks()
    {
        foreach (var e in new FilteredElementCollector(_doc).OfClass(typeof(RevitLinkType)))
            _steps.Enqueue(() => ReadLink(e, cad: false));
        foreach (var e in new FilteredElementCollector(_doc).OfClass(typeof(CADLinkType)))
            _steps.Enqueue(() => ReadLink(e, cad: true));
    }

    private void ReadLink(Element type, bool cad)
    {
        if (!ExternalFileUtils.IsExternalFileReference(_doc, type.Id)) return;
        var reference = ExternalFileUtils.GetExternalFileReference(_doc, type.Id);
        var path = "";
        long? bytes = null;
        try
        {
            path = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            if (path.Length > 0 && File.Exists(path)) bytes = new FileInfo(path).Length;
        }
        catch (Exception) { }
        var loaded = cad ? reference.GetLinkedFileStatus() == LinkedFileStatus.Loaded : RevitLinkType.IsLoaded(_doc, type.Id);
        Links.Add(new Link(type.Name, path, bytes, loaded, cad));
    }

    private void ReadOurs()
    {
        long bytes = 0;
        var count = 0;
        foreach (var e in new FilteredElementCollector(_doc).OfClass(typeof(DataStorage)))
        {
            try
            {
                foreach (var guid in e.GetEntitySchemaGuids())
                {
                    if (!OurSchemas.Contains(guid) || Schema.Lookup(guid) is not { } schema) continue;
                    var entity = e.GetEntity(schema);
                    if (!entity.IsValid()) continue;
                    count++;
                    bytes += 500;
                    foreach (var field in schema.ListFields())
                    {
                        if (field.ValueType != typeof(string)) continue;
                        if (field.ContainerType == ContainerType.Simple) bytes += (entity.Get<string>(field) ?? "").Length;
                        else if (field.ContainerType == ContainerType.Map && field.KeyType == typeof(string))
                            bytes += entity.Get<IDictionary<string, string>>(field)?.Sum(p => p.Key.Length + (p.Value?.Length ?? 0)) ?? 0;
                        else if (field.ContainerType == ContainerType.Array)
                            bytes += entity.Get<IList<string>>(field)?.Sum(s => s?.Length ?? 0) ?? 0;
                    }
                }
            }
            catch (Exception) { }
        }
        if (count == 0) return;
        var row = Row(M.Ours, "Sketch ProMax data", bytes);
        row.Count = count;
        row.Measured = true;
        row.Detail = $"{N(count)} {(count == 1 ? "record" : "records")}: settings, checklist answers and time saved";
    }

    /// <summary>The grey line of the rows counted by category: "2,400 elements".</summary>
    private void Describe()
    {
        foreach (var row in _rows.Values.Where(r => r.Kind is M.Model or M.Annotation && r.Detail.Length == 0))
            row.Detail = $"{N(row.Count)} {(row.Count == 1 ? "element" : "elements")}";
    }

    // ---- Measuring a family --------------------------------------------------------------------------------------------

    private void MeasureFamily(M.Item row)
    {
        if (_doc.GetElement(new ElementId(row.FamilyId)) is not Family family) return;
        Document? fdoc = null;
        var folder = Path.Combine(Path.GetTempPath(), "SketchProMax storage");
        var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".rfa");
        try
        {
            Directory.CreateDirectory(folder);
            fdoc = _doc.EditFamily(family);
            fdoc.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
            var bytes = new FileInfo(file).Length;
            if (bytes > 0)
            {
                row.Bytes = bytes;
                row.Measured = true;
                _measures[M.FamilyKey(family.Name, family.FamilyCategory?.Name ?? "")] = new M.Measure { Bytes = bytes, At = DateTime.Now };
                _measuresChanged = true;
            }
        }
        catch (Exception) { /* not measurable (locked, damaged): it keeps its estimate */ }
        finally
        {
            try { fdoc?.Close(false); } catch (Exception) { }
            try
            {
                foreach (var left in Directory.GetFiles(folder, Path.GetFileNameWithoutExtension(file) + "*")) File.Delete(left);
            }
            catch (Exception) { }
        }
    }

    private void SaveMeasures()
    {
        if (!_measuresChanged) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(_measuresFile, M.WriteMeasures(_measures));
            _measuresChanged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- Words ---------------------------------------------------------------------------------------------------------

    private static string N(long n) => n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    private static string? Name(Element? e)
    {
        try { return e?.Name; }
        catch (Exception) { return null; }
    }

    /// <summary>"FillPatternElement" → "Fill patterns", for the rows of settings.</summary>
    private static string Plural(string className) => className switch
    {
        nameof(Material) => "Materials",
        nameof(FillPatternElement) => "Fill patterns",
        nameof(LinePatternElement) => "Line patterns",
        nameof(AppearanceAssetElement) => "Material appearances",
        nameof(PropertySetElement) => "Material properties",
        _ => "Other settings",
    };
}
