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
/// exists. A Revit updater, inside the user's own transaction: one Ctrl+Z undoes it with the edit. Never overwrites a value.
/// </summary>
internal static class BimReady
{
    private static readonly Guid UpdaterGuid = new("5b0c3f8e-9a51-4d7e-8f0b-2a6c4d1e7b93");
    private static Updater? _updater;
    private static readonly Dictionary<string, (DateTime At, ChecklistMath.Definition? Def)> Defs = new(StringComparer.OrdinalIgnoreCase);

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
    }

    public static void Stop()
    {
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
        var seenTypes = new HashSet<long>();
        foreach (var id in data.GetAddedElementIds().Concat(data.GetModifiedElementIds()))
        {
            if (doc.GetElement(id) is not { Category: { CategoryType: CategoryType.Model } cat } e) continue;
            if (worksets.Count > 0) ToWorkset(doc, e, cat.Name, worksets);
            if (doc.GetElement(e.GetTypeId()) is not ElementType type || !seenTypes.Add(type.Id.Value)) continue;
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
