using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Settings;
using SketchProMax.Tools;

namespace SketchProMax.Revit;

/// <summary>
/// Keeps a model BIM-ready while it is modelled, when its project's pre-submission checklist says so (Checking rules:
/// Keep the model BIM-ready while you model = yes): each element placed, or switched to another type, gets its type's
/// Assembly Code from the UniFormat rules when the type has none, the fixed LOI values the rules give (loivalues) where
/// they are empty, and is moved off Workset1 (or Shared Levels and Grids) to its category's workset when that workset
/// exists. A Revit updater, inside the user's own transaction: one Ctrl+Z undoes it with the edit. Then, when Revit is
/// idle a few seconds later, the LOI matrix's information the model gives (material, sizes, length, levels...) is filled
/// on the types placed, retyped or changed, a few types at a time, in its own transaction ("Sketch ProMax: fill the LOI
/// data"). Never overwrites a value. A "slightly off axis" warning raised by an edit is not kept: nothing is moved, the
/// warning just isn't recorded (Ahmed: submitted geometry must not change, inclined elements stay as drawn).
/// </summary>
internal static class BimReady
{
    private static readonly Guid UpdaterGuid = new("5b0c3f8e-9a51-4d7e-8f0b-2a6c4d1e7b93");
    private static Updater? _updater;
    private static readonly Dictionary<string, (DateTime At, ChecklistMath.Definition? Def)> Defs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per open model (its title): the types whose LOI data is filled at the next quiet moment.</summary>
    private static readonly Dictionary<string, HashSet<long>> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static Autodesk.Revit.ApplicationServices.Application? _app;
    private static DateTime _lastChange;
    private const int TypesPerIdle = 8;
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    public static void Start(UIApplication app)
    {
        if (_updater is not null || app.ActiveAddInId is not { } addin) return;
        var updater = new Updater(addin);
        var id = updater.GetUpdaterId();
        if (UpdaterRegistry.IsUpdaterRegistered(id)) UpdaterRegistry.UnregisterUpdater(id);
        // Optional: a model opened where Sketch ProMax isn't installed gives no warning.
        UpdaterRegistry.RegisterUpdater(updater, true);
        _updater = updater;
        var instances = new ElementIsElementTypeFilter(true);
        UpdaterRegistry.AddTrigger(id, instances, Element.GetChangeTypeElementAddition());
        try { UpdaterRegistry.AddTrigger(id, instances, Element.GetChangeTypeParameter(new ElementId(BuiltInParameter.ELEM_TYPE_PARAM))); } catch (Exception) { }
        try { UpdaterRegistry.AddTrigger(id, instances, Element.GetChangeTypeGeometry()); } catch (Exception) { }
        RevitDispatcher.Idle += OnIdle;
        _app = app.Application;
        _app.FailuresProcessing += OnFailures;
    }

    public static void Stop()
    {
        RevitDispatcher.Idle -= OnIdle;
        if (_app is not null) { try { _app.FailuresProcessing -= OnFailures; } catch (Exception) { } _app = null; }
        lock (Pending) Pending.Clear();
        if (_updater is not { } updater) return;
        try { UpdaterRegistry.UnregisterUpdater(updater.GetUpdaterId()); } catch (Exception) { }
        _updater = null;
    }

    /// <summary>The project's checklist for this model, read again at most once a minute.</summary>
    private static ChecklistMath.Definition? DefFor(Document doc)
    {
        string name;
        try { name = string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileName(doc.PathName); }
        catch (Exception) { name = doc.Title; }
        var project = ChecklistProjects.ProjectKey(name);
        if (project.Length == 0) return null;
        lock (Defs)
        {
            if (Defs.TryGetValue(project, out var had) && DateTime.UtcNow - had.At < TimeSpan.FromMinutes(1)) return had.Def;
            var def = ChecklistStore.Load(AgentSettings.Load(), project).Def;
            Defs[project] = (DateTime.UtcNow, def);
            return def;
        }
    }

    /// <summary>Whether the rule turns it on: yes, on or true.</summary>
    internal static bool On(string rule) => rule.Trim().ToLowerInvariant() is "yes" or "on" or "true" or "y";

    private static void Run(UpdaterData data)
    {
        var doc = data.GetDocument();
        if (doc.IsFamilyDocument) return;
        var def = DefFor(doc);
        if (!On(ChecklistMath.Rule(def, "autoready"))) return;
        var codeNames = ChecklistMath.RuleList(ChecklistMath.Rule(def, "classification")) is { Count: > 0 } n ? n : ["Assembly Code"];
        var codes = ChecklistFixes.UniFormatMap(def);
        var values = ChecklistMath.ParseLoiValues(ChecklistMath.Rule(def, "loivalues")).Where(v => !v.Value.Contains('{')).ToList();
        var worksets = doc.IsWorkshared ? ChecklistFixes.WorksetMap(def) : [];
        var loi = ChecklistMath.ParseLoi(ChecklistMath.Rule(def, "loimatrix")).Count > 0;
        var seenTypes = new HashSet<long>();
        foreach (var id in data.GetAddedElementIds().Concat(data.GetModifiedElementIds()))
        {
            if (doc.GetElement(id) is not { Category: { CategoryType: CategoryType.Model } cat } e) continue;
            if (worksets.Count > 0) ToWorkset(doc, e, cat.Name, worksets);
            if (doc.GetElement(e.GetTypeId()) is not ElementType type || !seenTypes.Add(type.Id.Value)) continue;
            if (loi) lock (Pending) { (Pending.TryGetValue(doc.Title, out var set) ? set : Pending[doc.Title] = []).Add(type.Id.Value); _lastChange = DateTime.UtcNow; }
            var codeParam = codeNames.Select(x => type.LookupParameter(x)).FirstOrDefault(x => x is not null);
            if (codeParam is { IsReadOnly: false, StorageType: StorageType.String } && !ChecklistFacts.Filled(codeParam)
                && ChecklistFixes.UniFormatFor(cat.Name, type.FamilyName, type.Name, codes) is { } code)
                Try(() => codeParam.Set(code));
            var typeCode = codeParam?.AsString() ?? "";
            foreach (var (key, param, value) in values)
            {
                if (!(typeCode.Length > 0 && ChecklistMath.Like(typeCode, key) || cat.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || key == "*")) continue;
                if (type.LookupParameter(param) is { IsReadOnly: false, StorageType: StorageType.String } p && !ChecklistFacts.Filled(p)) Try(() => p.Set(value));
            }
        }
    }

    /// <summary>
    /// Hides Revit's "slightly off axis" warning when the model is kept BIM-ready: the warning is dropped as the edit is
    /// committed, so it never pops up or stays in Manage > Warnings. Nothing in the model changes.
    /// </summary>
    private static void OnFailures(object? sender, Autodesk.Revit.DB.Events.FailuresProcessingEventArgs e)
    {
        try
        {
            var fa = e.GetFailuresAccessor();
            var doc = fa.GetDocument();
            if (doc.IsFamilyDocument) return;
            var off = fa.GetFailureMessages().Where(m => m.GetSeverity() == FailureSeverity.Warning
                && (m.GetDescriptionText() ?? "").IndexOf(WarningFixes.OffAxisText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (off.Count == 0 || !On(ChecklistMath.Rule(DefFor(doc), "autoready"))) return;
            foreach (var m in off) fa.DeleteWarning(m);
        }
        catch (Exception) { /* best effort: the warning just stays */ }
    }

    /// <summary>At a quiet moment: the LOI data of a few pending types of each open model, each model in its own transaction.</summary>
    private static void OnIdle(UIApplication app)
    {
        lock (Pending) if (Pending.Count == 0 || DateTime.UtcNow - _lastChange < Quiet) return;
        if (App.Dispatcher.IsRunning) return;
        foreach (Document doc in app.Application.Documents)
        {
            List<long> batch;
            lock (Pending)
            {
                if (!Pending.TryGetValue(doc.Title, out var set)) continue;
                batch = set.Take(TypesPerIdle).ToList();
                set.ExceptWith(batch);
                if (set.Count == 0) Pending.Remove(doc.Title);
            }
            if (doc.IsFamilyDocument || doc.IsReadOnly || doc.IsModifiable || doc.IsLinked) continue;
            try
            {
                var spec = LoiFill.SpecOf(DefFor(doc));
                var answer = LoiFill.AutoAnswer(doc, spec, batch);
                if (answer.Length > 0) LoiFill.Apply(doc, spec, answer);
            }
            catch (Exception) { /* best effort: the checklist's Make it BIM-ready fills what is left */ }
        }
        // Forget models that were closed.
        lock (Pending)
            foreach (var title in Pending.Keys.ToList())
                if (!app.Application.Documents.Cast<Document>().Any(d => d.Title == title)) Pending.Remove(title);
    }

    private static void ToWorkset(Document doc, Element e, string category, List<(string Category, string Workset)> map)
    {
        var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
        if (p is null || p.IsReadOnly) return;
        var now = doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name ?? "";
        if (!now.Equals("Workset1", StringComparison.OrdinalIgnoreCase) && !now.Equals("Shared Levels and Grids", StringComparison.OrdinalIgnoreCase)) return;
        if (ChecklistFixes.WorksetFor(category, map) is not { } target) return;
        var ws = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).FirstOrDefault(w => w.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (ws is not null) Try(() => p.Set(ws.Id.IntegerValue));
    }

    private static void Try(Func<bool> set)
    {
        try { set(); }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException) { }
    }

    private sealed class Updater(AddInId addin) : IUpdater
    {
        private readonly UpdaterId _id = new(addin, UpdaterGuid);

        public void Execute(UpdaterData data)
        {
            try { Run(data); }
            catch (Exception) { /* never fail the user's own edit */ }
        }

        public UpdaterId GetUpdaterId() => _id;
        public ChangePriority GetChangePriority() => ChangePriority.FloorsRoofsStructuralWalls;
        public string GetUpdaterName() => "Sketch ProMax BIM-ready";
        public string GetAdditionalInformation() => "Fills the Assembly Code, fixed LOI values and workset of elements as they are placed, from the project's checklist rules. Optional.";
    }
}
