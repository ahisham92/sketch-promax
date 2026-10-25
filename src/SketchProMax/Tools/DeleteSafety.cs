using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// Deleting without crashing Revit. A preview never deletes: what a delete would take along is read with
/// Element.GetDependentElements, not found by deleting in a transaction that is rolled back. Revit 2024 closed by itself
/// (no dump, no clean exit) inside the field test's purge_unused preview, while that preview deleted families in
/// sub-transactions of a rolled-back transaction (the journal ends in ContentADoc__UpdateForDeletion). Families and
/// family types are also deleted one at a time when applying, never in chunks.
/// </summary>
internal static class DeleteSafety
{
    /// <summary>What deleting <paramref name="ids"/> would also delete, without deleting anything (the ids themselves left out).</summary>
    public static List<ElementId> Dependents(Document doc, ICollection<ElementId> ids)
    {
        var asked = new HashSet<long>(ids.Select(i => i.Value));
        var found = new HashSet<long>();
        var result = new List<ElementId>();
        foreach (var id in ids)
        {
            if (doc.GetElement(id) is not { } e) continue;
            IList<ElementId> deps;
            try { deps = e.GetDependentElements(null); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException) { continue; }
            foreach (var d in deps)
                if (!asked.Contains(d.Value) && found.Add(d.Value)) result.Add(d);
        }
        return result;
    }

    /// <summary>A family or a family type: deleted one at a time, each in its own transaction.</summary>
    public static bool IsFamilyContent(Document doc, ElementId id) => doc.GetElement(id) is Family or FamilySymbol;
}
