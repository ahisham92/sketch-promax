using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// "Show in Revit" for the pre-submission checks: takes you to what a check is about. Some open an existing view or
/// Revit's own dialog (warnings, links, the starting view); some select the elements and zoom to them (imported CAD);
/// the rest make a checking view named "Sketch ProMax check - …" (a plan with grids or scope boxes, a 3D view with only
/// the elements to look at, a schedule of sheets, title blocks or views). Those views are the only change to the model,
/// and <see cref="Cleanup"/> deletes them all at the end. Revit API context only (a command or the dispatcher).
/// </summary>
internal static class ChecklistViews
{
    public const string Prefix = "Sketch ProMax check - ";

    /// <summary>The button text for a check's model rule (ChecklistMath.AutoRules key), or null when there is nothing to show.</summary>
    public static string? ButtonFor(string key) => key switch
    {
        "grids" => "Show the grids",
        "levels" => "Open an elevation",
        "worksets" => "Show what's on the wrong workset",
        "scopeboxes" => "Show the scope boxes",
        "categories" => "Show the Generic Models",
        "imports" => "Select the imported CAD",
        "groups" => "Show the model groups",
        "warnings" => "Open the warnings",
        "landing" => "Open the starting view",
        "titleblock" => "List the title blocks",
        "scales" or "viewtemplates" or "unusedviews" => "List the views",
        "drawinglist" => "List the sheets",
        "navisview" => "Open the Navisworks view",
        "links" or "urs" => "Open Manage Links",
        _ => null,
    };

    /// <summary>Shows what <paramref name="key"/> is about in the active model; returns a sentence saying what happened.</summary>
    public static string Show(UIDocument uidoc, string key)
    {
        var doc = uidoc.Document;
        switch (key)
        {
            case "warnings":
                uidoc.Application.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ReviewWarnings));
                return "Opened Revit's list of warnings.";
            case "links" or "urs":
                uidoc.Application.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ManageLinks));
                return "Opened Manage Links.";
            case "landing":
            {
                var id = StartingViewSettings.GetStartingViewSettings(doc).ViewId;
                if (id == ElementId.InvalidElementId || doc.GetElement(id) is not View start) return "No starting view is set (Manage > Starting View).";
                uidoc.RequestViewChange(start);
                return $"Opened the starting view {start.Name}.";
            }
            case "navisview":
            {
                var navis = Views(doc).OfType<View3D>().FirstOrDefault(v => v.Name.IndexOf("Navisworks", StringComparison.OrdinalIgnoreCase) >= 0);
                if (navis is null) return "There is no 3D view named …Navisworks Export to open.";
                uidoc.RequestViewChange(navis);
                return $"Opened {navis.Name}.";
            }
            case "levels":
            {
                var elevation = Views(doc).FirstOrDefault(v => v.ViewType == ViewType.Elevation && !v.Name.StartsWith(Prefix, StringComparison.Ordinal));
                if (elevation is null) return "The model has no elevation view to show the levels in.";
                uidoc.RequestViewChange(elevation);
                return $"Opened {elevation.Name} to look at the level names.";
            }
            case "imports":
            {
                var ids = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(i => !i.IsLinked).Select(i => i.Id).ToList();
                if (ids.Count == 0) return "There is no imported CAD in the project to show.";
                uidoc.Selection.SetElementIds(ids);
                uidoc.ShowElements(ids);
                return $"Selected the {ids.Count} imported CAD files and zoomed to them. Nothing was deleted.";
            }
        }
        var (view, message) = Make(doc, key);
        if (view is not null) uidoc.RequestViewChange(view);
        return message;
    }

    /// <summary>Makes the checking view for <paramref name="key"/> (null with the reason when there is nothing to show).</summary>
    public static (View? View, string Message) Make(Document doc, string key)
    {
        switch (key)
        {
            case "grids":
                return Plan(doc, "Grids", BuiltInCategory.OST_Grids, new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Grid)).GetElementCount(), "grids");
            case "scopeboxes":
                return Plan(doc, "Scope boxes", BuiltInCategory.OST_VolumeOfInterest,
                    new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().GetElementCount(), "scope boxes");
            case "categories":
                return Isolated(doc, "Generic Models", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType().ToElementIds(), "Generic Model elements");
            case "groups":
                return Isolated(doc, "Model groups", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_IOSModelGroups).WhereElementIsNotElementType().ToElementIds(), "model groups");
            case "worksets":
                return Isolated(doc, "Worksets", WrongWorkset(doc), "elements on Workset1 or non-grid elements on Shared Levels and Grids");
            case "titleblock":
                return Schedule(doc, "Title blocks", () => ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_TitleBlocks)), "Family and Type", "Sheet Number", "Sheet Name");
            case "drawinglist":
                return Schedule(doc, "Sheets", () => ViewSchedule.CreateSheetList(doc), "Sheet Number", "Sheet Name", "Current Revision");
            case "scales" or "viewtemplates" or "unusedviews":
                return Schedule(doc, "Views", () => ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Views)),
                    "View Name", "Family and Type", "View Scale", "Scale Value    1:", "View Template", "Sheet Number", "Sheet Name");
            default:
                return (null, "There is nothing in the model to show for this check.");
        }
    }

    /// <summary>The checking views in the model (by their name).</summary>
    public static List<View> Find(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.Name.StartsWith(Prefix, StringComparison.Ordinal)).ToList();

    /// <summary>Deletes every checking view but the one open in front (Revit can't delete the active view); says what it did.</summary>
    public static string Cleanup(UIDocument uidoc)
    {
        var doc = uidoc.Document;
        var views = Find(doc);
        if (views.Count == 0) return "There are no checking views to delete.";
        var active = uidoc.ActiveView?.Id ?? ElementId.InvalidElementId;
        var (deleted, failed) = Delete(doc, active);
        var text = deleted == 1 ? "Deleted 1 checking view." : $"Deleted {deleted} checking views.";
        if (failed > 0) text += $" {failed} couldn't be deleted (someone else may own them).";
        if (views.FirstOrDefault(v => v.Id == active) is { } kept) text += $" {kept.Name} is the view in front, so it was kept: switch to another view and delete again.";
        return text;
    }

    /// <summary>Deletes the checking views except <paramref name="keep"/>, in one transaction: how many went and how many couldn't.</summary>
    public static (int Deleted, int Failed) Delete(Document doc, ElementId keep)
    {
        var ids = Find(doc).Where(v => v.Id != keep).Select(v => v.Id).ToList();
        if (ids.Count == 0) return (0, 0);
        var deleted = 0;
        using var t = new Transaction(doc, "Sketch ProMax: delete checking views");
        t.Start();
        foreach (var id in ids)
        {
            try { if (doc.Delete(id).Count > 0) deleted++; }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        }
        t.Commit();
        return (deleted, ids.Count - deleted);
    }

    // ---- Making the views -----------------------------------------------------------------------------------------

    private static IEnumerable<View> Views(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.CanBePrinted);

    /// <summary>A plan on the lowest level with <paramref name="show"/> visible and no view template or crop.</summary>
    private static (View?, string) Plan(Document doc, string name, BuiltInCategory show, int count, string what)
    {
        if (count == 0) return (null, $"The model has no {what} to show.");
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
        var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan) ?? new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.StructuralPlan);
        if (level is null || type is null) return (null, "The model has no level or plan view type to make the checking plan with.");
        return Made(doc, name, () =>
        {
            var plan = ViewPlan.Create(doc, type.Id, level.Id);
            plan.ViewTemplateId = ElementId.InvalidElementId;
            plan.CropBoxActive = false;
            var category = new ElementId(show);
            if (plan.CanCategoryBeHidden(category)) plan.SetCategoryHidden(category, false);
            return plan;
        }, $"Made the plan {Prefix}{name} on {level.Name} with the {count} {what} showing.");
    }

    /// <summary>A 3D view with only <paramref name="ids"/> showing.</summary>
    private static (View?, string) Isolated(Document doc, string name, ICollection<ElementId> ids, string what)
    {
        // Only elements that can show in 3D (not those drawn in one view).
        ids = ids.Where(id => doc.GetElement(id) is { } e && e.OwnerViewId == ElementId.InvalidElementId).ToList();
        if (ids.Count == 0) return (null, $"There are no {what} to show.");
        var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
        if (type is null) return (null, "The model has no 3D view type to make the checking view with.");
        return Made(doc, name, () =>
        {
            var view = View3D.CreateIsometric(doc, type.Id);
            view.ViewTemplateId = ElementId.InvalidElementId;
            view.IsolateElementsTemporary(ids);
            view.ConvertTemporaryHideIsolateToPermanent();
            return view;
        }, $"Made the 3D view {Prefix}{name} with only the {ids.Count} {what} showing.");
    }

    /// <summary>A schedule with the columns of <paramref name="fields"/> that this Revit has (matched by name).</summary>
    private static (View?, string) Schedule(Document doc, string name, Func<ViewSchedule> create, params string[] fields)
    {
        return Made(doc, name, () =>
        {
            var schedule = create();
            var definition = schedule.Definition;
            var available = definition.GetSchedulableFields();
            foreach (var wanted in fields)
                if (available.FirstOrDefault(f => Same(f.GetName(doc), wanted)) is { } field) definition.AddField(field);
            if (definition.GetFieldCount() == 0)
                foreach (var field in available.Take(4)) definition.AddField(field);
            return schedule;
        }, $"Made the schedule {Prefix}{name}.");
    }

    private static bool Same(string a, string b) =>
        string.Equals(Regex.Replace(a, @"\s+", " ").Trim(), Regex.Replace(b, @"\s+", " ").Trim(), StringComparison.OrdinalIgnoreCase);

    private static (View?, string) Made(Document doc, string name, Func<View> make, string message)
    {
        using var t = new Transaction(doc, "Sketch ProMax: checking view");
        t.Start();
        var view = make();
        view.Name = Unique(doc, Prefix + name);
        t.Commit();
        return (view, message + " Delete the checking views on Review and sign when you're done.");
    }

    private static string Unique(Document doc, string name)
    {
        var taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    /// <summary>Elements on Workset1, and model elements other than levels and grids on Shared Levels and Grids.</summary>
    private static List<ElementId> WrongWorkset(Document doc)
    {
        if (!doc.IsWorkshared) return [];
        var ids = new List<ElementId>();
        var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToList();
        if (worksets.FirstOrDefault(w => w.Name.Equals("Workset1", StringComparison.OrdinalIgnoreCase)) is { } one)
            ids.AddRange(new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementWorksetFilter(one.Id, false))
                .Where(e => e.Category is { CategoryType: CategoryType.Model }).Select(e => e.Id));
        if (worksets.FirstOrDefault(w => w.Name.Equals("Shared Levels and Grids", StringComparison.OrdinalIgnoreCase)) is { } shared)
            ids.AddRange(new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementWorksetFilter(shared.Id, false))
                .Where(e => e.Category is { CategoryType: CategoryType.Model } c && c.BuiltInCategory is not (BuiltInCategory.OST_Levels or BuiltInCategory.OST_Grids)).Select(e => e.Id));
        return ids;
    }
}
