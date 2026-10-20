using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// Project parameters nobody uses (pre-submission check "Project parameters are clean"): a project parameter that is
/// empty on every element it is bound to can go, and so can the empty copy of a parameter whose name is there twice
/// (two Folder parameters on views, one filled and one not). Kept whatever they hold: the names in the Keep rule
/// (Mark, Comments by default), the parameters the other checking rules name (view folders, classification, IFC, the
/// model name on sheets), and any parameter a schedule or a view filter uses. Built-in parameters (Mark, Comments and
/// the rest of Revit's own) are never project parameters, so they are never touched. Revit API context only.
/// </summary>
internal static class ParameterCleanup
{
    /// <summary>A project parameter: its element, name, binding and whether any element has a value for it.</summary>
    public sealed class Param
    {
        public ElementId Id = ElementId.InvalidElementId;
        public string Name = "";
        public bool Instance;
        public List<ElementId> Categories = [];
        public string Bound = "";
        public bool Filled;
        public bool Read;
    }

    /// <summary>The project parameters with their bindings (quick: no element is read).</summary>
    public static List<Param> List(Document doc)
    {
        var all = new List<Param>();
        var it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
        {
            if (it.Key is not InternalDefinition def || it.Current is not ElementBinding binding) continue;
            var cats = new List<ElementId>();
            var names = new List<string>();
            foreach (Category c in binding.Categories) { cats.Add(c.Id); names.Add(c.Name); }
            all.Add(new Param
            {
                Id = def.Id, Name = def.Name, Instance = binding is InstanceBinding, Categories = cats,
                Bound = names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more",
            });
        }
        return all;
    }

    /// <summary>Whether any element of the parameter's categories has a value for it (one search inside Revit).</summary>
    public static void Fill(Document doc, Param p)
    {
        p.Read = true;
        if (p.Categories.Count == 0 || p.Id == ElementId.InvalidElementId) return;
        var collector = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(p.Categories));
        collector = p.Instance ? collector.WhereElementIsNotElementType() : collector.WhereElementIsElementType();
        p.Filled = collector.WherePasses(new ElementParameterFilter(ParameterFilterRuleFactory.CreateHasValueParameterRule(p.Id))).FirstElementId() != ElementId.InvalidElementId;
    }

    /// <summary>The parameters schedules and view filters use: kept even when empty.</summary>
    public static HashSet<long> InUse(Document doc)
    {
        var used = new HashSet<long>();
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            try
            {
                var def = s.Definition;
                for (var i = 0; i < def.GetFieldCount(); i++) used.Add(def.GetField(i).ParameterId.Value);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
        {
            try { foreach (var id in f.GetElementFilterParameters()) used.Add(id.Value); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        return used;
    }

    /// <summary>The names never deleted: the Keep rule and the parameters the other checking rules name.</summary>
    public static List<string> KeepNames(ChecklistMath.Definition? def)
    {
        var names = new List<string>();
        foreach (var key in new[] { "keepparameters", "viewfolders", "classification", "ifc", "tbmodelname" })
            names.AddRange(ChecklistMath.Rule(def, key).Split(',').Select(n => n.Trim()).Where(n => n.Length > 0));
        return names;
    }

    /// <summary>What can be deleted, and a line for each (empty everywhere, or the empty copy of a duplicate).</summary>
    public static List<(Param Param, string Line)> Deletable(List<Param> all, HashSet<long> used, List<string> keep)
    {
        var copies = all.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        // A name that is there more than once keeps one copy: the filled one, else the oldest.
        var kept = new HashSet<long>(all.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 && !g.Any(p => p.Filled))
            .Select(g => g.OrderBy(p => p.Id.Value).First().Id.Value));
        var list = new List<(Param, string)>();
        foreach (var p in all.Where(p => p.Read && !p.Filled).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (kept.Contains(p.Id.Value) || used.Contains(p.Id.Value) || keep.Any(k => ChecklistMath.Like(p.Name, k))) continue;
            var twice = copies.TryGetValue(p.Name, out var n) && n > 1;
            list.Add((p, $"{p.Name} ({(p.Instance ? "instance" : "type")}, {p.Bound}): " + (twice ? $"one of {n} parameters with this name, and this one is empty" : "empty on every element")));
        }
        return list;
    }

    /// <summary>Reads every parameter at once (the fix; the checklist reads them one step each).</summary>
    public static List<(Param Param, string Line)> Find(Document doc, ChecklistMath.Definition? def)
    {
        var all = List(doc);
        foreach (var p in all) Fill(doc, p);
        return Deletable(all, InUse(doc), KeepNames(def));
    }
}
