using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// "Fix it for me" on the pre-submission checks whose fix is mechanical and safe to undo: unload the Revit links, delete
/// the imported CAD, delete the views on no sheet, ungroup groups used once, make the 3D Navisworks Export view, and set
/// a landing view as the starting view. Each is one Revit transaction (Ctrl+Z undoes it), except unloading links,
/// which Manage Links reloads. The window asks first, saying exactly what changes. Revit API context only.
/// </summary>
internal static class ChecklistFixes
{
    /// <summary>What the fix does, for its button and the question asked before it runs; null when a check has none.</summary>
    public sealed record Offer(string Button, string Question);

    public static Offer? For(string key) => key switch
    {
        "links" => new("Unload the Revit links", "Unload every Revit link in this model? Manage Links can reload them."),
        "urs" => null,
        "imports" => new("Delete the imported CAD", "Delete every imported CAD file from the project (linked CAD and CAD inside families are kept)?"),
        "unusedviews" => new("Delete the views on no sheet",
            "Delete every view that isn't on a sheet? The Navisworks view, the starting view, the view open in front and views with dependent views are kept."),
        "groups" => new("Ungroup groups used once", "Ungroup every model group whose type is used only once? The elements stay where they are."),
        "navisview" => new("Make the Navisworks view",
            "Make a 3D view named \"3D Navisworks Export\": wireframe, with links, levels, grids and rooms hidden? Apply your trade's view template to it afterwards."),
        "landing" => new("Set the landing view as starting view", "Set the view with \"Landing\" in its name as the model's starting view?"),
        _ => null,
    };

    /// <summary>Runs the fix for <paramref name="key"/> on the active model and opens what it made; a sentence saying what changed.</summary>
    public static string Run(UIDocument uidoc, string key) => Run(uidoc.Document, key, uidoc.ActiveView?.Id, uidoc);

    /// <summary>Runs the fix on <paramref name="doc"/> (the field test passes no UIDocument, so nothing is opened).</summary>
    public static string Run(Document doc, string key, ElementId? active, UIDocument? uidoc = null)
    {
        switch (key)
        {
            case "links":
            {
                var loaded = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .Where(t => RevitLinkType.IsLoaded(doc, t.Id) && !t.IsNestedLink).ToList();
                if (loaded.Count == 0) return "No Revit links are loaded: nothing to unload.";
                var done = 0;
                var failed = new List<string>();
                foreach (var t in loaded)
                {
                    try { t.Unload(null); done++; }
                    catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException) { failed.Add(t.Name); }
                }
                return $"Unloaded {done} Revit link{(done == 1 ? "" : "s")}." + (failed.Count > 0 ? " Couldn't unload " + string.Join(", ", failed) + "." : "");
            }
            case "imports":
            {
                var ids = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(i => !i.IsLinked).Select(i => i.Id).ToList();
                if (ids.Count == 0) return "There is no imported CAD to delete.";
                var (deleted, failed) = Delete(doc, ids, "delete imported CAD");
                return $"Deleted {deleted} imported CAD file{(deleted == 1 ? "" : "s")}." + Failed(failed);
            }
            case "unusedviews":
            {
                var ids = ChecklistFacts.UnusedViews(doc, active).Select(v => v.Id).ToList();
                if (ids.Count == 0) return "There are no views on no sheet to delete.";
                var (deleted, failed) = Delete(doc, ids, "delete views on no sheet");
                return $"Deleted {deleted} view{(deleted == 1 ? "" : "s")} on no sheet." + Failed(failed);
            }
            case "groups":
            {
                var once = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_IOSModelGroups).WhereElementIsNotElementType().Cast<Autodesk.Revit.DB.Group>()
                    .GroupBy(g => g.GroupType?.Id.Value ?? -1).Where(g => g.Count() == 1).Select(g => g.First()).ToList();
                if (once.Count == 0) return "No group type is used only once: nothing to ungroup.";
                var done = 0;
                using (var t = new Transaction(doc, "Sketch ProMax: ungroup groups used once"))
                {
                    t.Start();
                    foreach (var g in once)
                    {
                        try { g.UngroupMembers(); done++; }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException) { }
                    }
                    t.Commit();
                }
                return $"Ungrouped {done} group{(done == 1 ? "" : "s")} used only once." + Failed(once.Count - done);
            }
            case "navisview":
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                if (type is null) return "The model has no 3D view type to make the view with.";
                View3D view;
                using (var t = new Transaction(doc, "Sketch ProMax: make the 3D Navisworks Export view"))
                {
                    t.Start();
                    view = View3D.CreateIsometric(doc, type.Id);
                    view.ViewTemplateId = ElementId.InvalidElementId;
                    view.Name = Unique(doc, "3D Navisworks Export");
                    view.DisplayStyle = DisplayStyle.Wireframe;
                    foreach (var c in new[] { BuiltInCategory.OST_RvtLinks, BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_Rooms })
                    {
                        var id = new ElementId(c);
                        if (view.CanCategoryBeHidden(id)) view.SetCategoryHidden(id, true);
                    }
                    t.Commit();
                }
                uidoc?.RequestViewChange(view);
                return $"Made {view.Name} (wireframe; links, levels, grids and rooms hidden){(uidoc is null ? "" : " and opened it")}. Apply your trade's view template to it.";
            }
            case "landing":
            {
                var landing = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name.IndexOf("Landing", StringComparison.OrdinalIgnoreCase) >= 0);
                if (landing is null) return "No view has \"Landing\" in its name. Place LandingPage.rfa on a view first, then set it in Manage > Starting View.";
                using (var t = new Transaction(doc, "Sketch ProMax: set the starting view"))
                {
                    t.Start();
                    StartingViewSettings.GetStartingViewSettings(doc).ViewId = landing.Id;
                    t.Commit();
                }
                return $"{landing.Name} is now the starting view.";
            }
            default:
                return "There is no automatic fix for this check.";
        }
    }

    private static (int Deleted, int Failed) Delete(Document doc, List<ElementId> ids, string what)
    {
        var deleted = 0;
        using var t = new Transaction(doc, "Sketch ProMax: " + what);
        t.Start();
        foreach (var id in ids)
        {
            try { if (doc.GetElement(id) is not null && doc.Delete(id).Count > 0) deleted++; }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException) { }
        }
        t.Commit();
        return (deleted, ids.Count - deleted);
    }

    private static string Failed(int failed) => failed > 0 ? $" {failed} couldn't be changed (someone else may own them, or they are pinned or in use)." : "";

    private static string Unique(Document doc, string name)
    {
        var taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }
}
