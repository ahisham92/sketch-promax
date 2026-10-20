using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// "Fix it for me" on the pre-submission checks whose fix is mechanical and safe to undo: unload the Revit links, delete
/// the imported CAD, delete the views on no sheet, ungroup groups used once, make the 3D Navisworks Export view, set
/// a landing view as the starting view, delete the empty project parameters, put the office title block on every sheet,
/// delete unused levels, sort elements onto their worksets, hide linked backgrounds on sheets, fill the view folder
/// parameters, write the model name on the sheets, delete identical instances, save the submitted copy and export the
/// NWC and IFC with the office settings (the last two change no model). Each is one Revit transaction (Ctrl+Z undoes
/// it), except unloading links, which Manage Links reloads. The window asks first, saying exactly what changes. Revit
/// API context only.
/// </summary>
internal static class ChecklistFixes
{
    /// <summary>What the fix does, for its button and the question asked before it runs; null when a check has none.</summary>
    /// <summary><paramref name="Ask"/>: a value the window asks for before running (the answer is passed to Run; empty is allowed).</summary>
    public sealed record Offer(string Button, string Question, bool ChangesModel = true, string? Ask = null);

    public static Offer? For(string key, ChecklistMath.Definition? def = null) => key switch
    {
        "titleblock" => new("Put the title block on every sheet",
            $"Change every sheet's title block to {Named(ChecklistFacts.TitleBlockWanted(def), "the Title block family in Checking rules")} (a type with the same name when it has one), and add it to sheets that have none? " +
            "The landing page's title block is left alone."),
        "levels" => new("Delete the unused levels",
            "Delete every level nothing is on (no element sits on it or is constrained to it)? A level with a plan on a sheet is kept; its plans on no sheet are deleted with it."),
        "worksets" => new("Sort the elements onto their worksets",
            "Move the elements on Workset1, and those on Shared Levels and Grids that aren't levels or grids, to the workset for their category " +
            $"({Named(ChecklistMath.Rule(def, "worksetmap"), "set Workset for each category in Checking rules first")}), and make the missing worksets " +
            $"({Named(ChecklistMath.Rule(def, "linkworksets"), "none set")})? Elements of a category with no workset set stay where they are."),
        "backgrounds" => new("Hide the linked models on sheets",
            "Hide the Revit links in every view on a sheet that shows one (Visibility/Graphics)? Where the view's template controls it, the template is changed instead, so every view using that template hides its links too."),
        "viewfolders" => new("Fill the view folders",
            "Fill every empty view folder parameter " + Named(string.Join(", ", ChecklistMath.RuleList(ChecklistMath.Rule(def, "viewfolders"))), "the Project Browser sorts by") +
            " from the view's template or view type, so no view lands in ???. Views whose template and type give nothing get the value you type next (leave it empty to skip them).",
            Ask: "The value for views whose template and type give none (empty: leave them):"),
        "tbmodelname" => new("Write the model name on the sheets",
            $"Write the model's file name into {Named(ChecklistMath.Rule(def, "tbmodelname"), "the sheet parameter that shows the model name")} on every sheet where it differs?"),
        "duplicates" => new("Delete the identical instances",
            "Delete the copies Revit warns about as identical instances in the same place, keeping one of each?"),
        "submission" => new("Save the submitted copy",
            $"Copy the saved model next to it, named with {Named(ChecklistMath.Rule(def, "submission"), "_S03_R00")} added (the first number not taken)? The model itself isn't changed; save it first.", ChangesModel: false),
        "links" => new("Unload the Revit links", "Unload every Revit link in this model? Manage Links can reload them."),
        "urs" => null,
        "imports" => new("Delete the imported CAD", "Delete every imported CAD file from the project (linked CAD and CAD inside families are kept)?"),
        "unusedviews" => new("Delete the views on no sheet",
            "Delete every view that isn't on a sheet? The Navisworks view, the starting view, the view open in front and views with dependent views are kept."),
        "groups" => new("Ungroup groups used once", "Ungroup every model group whose type is used only once? The elements stay where they are."),
        "navisview" => new("Make the Navisworks view",
            "Make a 3D view named \"3D Navisworks Export\": wireframe, with links, levels, grids and rooms hidden? Apply your trade's view template to it afterwards."),
        "parameters" => new("Delete the empty parameters",
            "Delete every project parameter that is empty on all its elements, and the empty copy of a parameter that is there twice? " +
            "Kept whatever they hold: Mark, Comments, the names in Checking rules (always kept, view folders, classification, IFC) and parameters used by schedules or view filters. Ctrl+Z undoes it."),
        "landing" => new("Set the landing view as starting view", "Set the view with \"Landing\" in its name as the model's starting view?"),
        "nwcsettings" or "nwcdate" when !ChecklistExports.WantsIfc(def) => new("Export the NWC with the office settings",
            "Export the NWC from the 3D Navisworks Export view, named exactly like the model, into the NWC and IFC folder (Checking rules), else next to the model? " +
            "NWC: shared coordinates, divided into levels, element IDs, all parameters, no links. No IFC: the package needs none (Files the package must have). A file with the same name is replaced.", ChangesModel: false),
        "nwcsettings" or "ifcsettings" or "nwcdate" => new("Export the NWC and IFC with the office settings",
            "Export the NWC from the 3D Navisworks Export view and the IFC, named exactly like the model, into the NWC and IFC folder (Checking rules), else next to the model? " +
            "NWC: shared coordinates, divided into levels, element IDs, all parameters, no links. IFC: IFC 4x3 (or your IFC setup file), shared coordinates, no links. Files with the same names are replaced.", ChangesModel: false),
        _ => null,
    };

    /// <summary>Runs the fix for <paramref name="key"/> on the active model and opens what it made; a sentence saying what changed.</summary>
    public static string Run(UIDocument uidoc, string key, ChecklistMath.Definition? def = null, string? answer = null) => Run(uidoc.Document, key, uidoc.ActiveView?.Id, uidoc, def, answer);

    /// <summary>Runs the fix on <paramref name="doc"/> (the field test passes no UIDocument, so nothing is opened).</summary>
    public static string Run(Document doc, string key, ElementId? active, UIDocument? uidoc = null, ChecklistMath.Definition? def = null, string? answer = null)
    {
        switch (key)
        {
            case "titleblock": return TitleBlocks(doc, def);
            case "levels": return Levels(doc, active);
            case "worksets": return Worksets(doc, def);
            case "backgrounds": return Backgrounds(doc);
            case "viewfolders": return ViewFolders(doc, def, answer);
            case "tbmodelname": return ModelName(doc, def);
            case "duplicates": return Duplicates(doc);
            case "submission": return Submission(doc, def);
            case "nwcsettings" or "ifcsettings" or "nwcdate":
                return ChecklistExports.Export(doc, def);
            case "parameters":
            {
                var empty = ParameterCleanup.Find(doc, def);
                if (empty.Count == 0) return "No project parameter is empty or an empty duplicate: nothing to delete.";
                var (deleted, failed) = Delete(doc, empty.Select(e => e.Param.Id).ToList(), "delete empty project parameters");
                return $"Deleted {deleted} empty project parameter{(deleted == 1 ? "" : "s")}: " + string.Join(", ", empty.Select(e => e.Param.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(12)) +
                       (empty.Count > 12 ? " and more" : "") + "." + Failed(failed);
            }
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

    private static string Named(string value, string otherwise) => value.Trim().Length > 0 ? value.Trim() : otherwise;

    private static bool Revit(Exception ex) => ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException;

    private static string Plural(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    /// <summary>Every sheet's title block becomes the office's; sheets with none get it. The landing page's is left alone.</summary>
    private static string TitleBlocks(Document doc, ChecklistMath.Definition? def)
    {
        var wanted = ChecklistFacts.TitleBlockWanted(def);
        if (wanted.Length == 0) return "No title block is set: set Title block family in Checking rules, then press Fix it again.";
        var symbols = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().OfType<FamilySymbol>().ToList();
        bool Wanted(string family) => family.Equals(wanted, StringComparison.OrdinalIgnoreCase) || ChecklistMath.LikeAny(family, wanted);
        var types = symbols.Where(t => Wanted(t.FamilyName)).ToList();
        if (types.Count == 0) return $"The {wanted} title block isn't loaded in this model: load it (Insert > Load Family), then press Fix it again.";
        var blocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
        var withBlock = new HashSet<long>(blocks.Select(b => b.OwnerViewId.Value));
        var bare = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder && !withBlock.Contains(s.Id.Value)).ToList();
        var swap = blocks.Where(b => b.Symbol is { } sym && !ChecklistFacts.IsLanding(sym.FamilyName, def) && !Wanted(sym.FamilyName)).ToList();
        if (swap.Count == 0 && bare.Count == 0) return $"Every sheet already has the {wanted} title block.";
        int swapped = 0, added = 0, failed = 0;
        using (var t = new Transaction(doc, "Sketch ProMax: put the title block on every sheet"))
        {
            t.Start();
            foreach (var b in swap)
            {
                // The same size when the office family has a type of that name (A1 stays A1), else its first type.
                var type = types.FirstOrDefault(x => x.Name.Equals(b.Symbol.Name, StringComparison.OrdinalIgnoreCase)) ?? types[0];
                try { b.ChangeTypeId(type.Id); swapped++; }
                catch (Exception ex) when (Revit(ex)) { failed++; }
            }
            if (bare.Count > 0 && !types[0].IsActive) types[0].Activate();
            foreach (var sheet in bare)
            {
                try { doc.Create.NewFamilyInstance(XYZ.Zero, types[0], sheet); added++; }
                catch (Exception ex) when (Revit(ex)) { failed++; }
            }
            t.Commit();
        }
        return $"Changed {Plural(swapped, "title block")} to {wanted}" + (added > 0 ? $" and added it to {Plural(added, "sheet")} that had none" : "") + "." +
               (added > 0 ? " Check where it sits on those sheets." : "") + Failed(failed);
    }

    private static string Levels(Document doc, ElementId? active)
    {
        var unused = ChecklistFacts.UnusedLevels(doc, active);
        if (unused.Count == 0) return "Every level has something on it: nothing to delete.";
        var names = unused.Select(l => l.Name).ToList();
        var (deleted, failed) = Delete(doc, unused.Select(l => l.Id).ToList(), "delete unused levels");
        return $"Deleted {Plural(deleted, "unused level")}: {string.Join(", ", names.Take(12))}{(names.Count > 12 ? " and more" : "")}." + Failed(failed);
    }

    /// <summary>The category to workset pairs of the worksetmap rule ("Floors>ST_Concrete").</summary>
    internal static List<(string Category, string Workset)> WorksetMap(ChecklistMath.Definition? def) =>
        ChecklistMath.RuleList(ChecklistMath.Rule(def, "worksetmap")).Select(p => p.Split('>'))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0).Select(p => (p[0].Trim(), p[1].Trim())).ToList();

    /// <summary>The workset a category goes on: its name, the start of its name (Structural Foundation) or a pattern.</summary>
    internal static string? WorksetFor(string category, List<(string Category, string Workset)> map) =>
        map.FirstOrDefault(m => category.Equals(m.Category, StringComparison.OrdinalIgnoreCase) || category.StartsWith(m.Category, StringComparison.OrdinalIgnoreCase) ||
                                ChecklistMath.Like(category, m.Category)).Workset;

    private static string Worksets(Document doc, ChecklistMath.Definition? def)
    {
        if (!doc.IsWorkshared) return "The model isn't workshared, so it has no worksets to sort.";
        var map = WorksetMap(def);
        var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToList();
        var made = new List<string>();
        var left = new List<string>();
        int moved = 0, failed = 0;
        using (var t = new Transaction(doc, "Sketch ProMax: sort elements onto worksets"))
        {
            t.Start();
            WorksetId Get(string name)
            {
                if (worksets.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } w) return w.Id;
                var created = Workset.Create(doc, name);
                worksets.Add(created);
                made.Add(name);
                return created.Id;
            }
            foreach (var name in ChecklistMath.RuleList(ChecklistMath.Rule(def, "linkworksets")))
            {
                try { Get(name); }
                catch (Exception ex) when (Revit(ex)) { failed++; }
            }
            var off = new List<Element>();
            if (worksets.FirstOrDefault(w => w.Name.Equals("Workset1", StringComparison.OrdinalIgnoreCase)) is { } one)
                off.AddRange(new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementWorksetFilter(one.Id, false)));
            if (worksets.FirstOrDefault(w => w.Name.Equals("Shared Levels and Grids", StringComparison.OrdinalIgnoreCase)) is { } shared)
                off.AddRange(new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementWorksetFilter(shared.Id, false))
                    .Where(e => e.Category is { } c && c.CategoryType == CategoryType.Model && c.BuiltInCategory is not (BuiltInCategory.OST_Levels or BuiltInCategory.OST_Grids)));
            foreach (var e in off)
            {
                var category = e.Category?.Name ?? "";
                var target = category.Length == 0 ? null : WorksetFor(category, map);
                var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (target is null || p is null || p.IsReadOnly) { left.Add(category.Length > 0 ? category : "(no category)"); continue; }
                try { if (p.Set(Get(target).IntegerValue)) moved++; else failed++; }
                catch (Exception ex) when (Revit(ex)) { failed++; }
            }
            t.Commit();
        }
        var said = $"Moved {Plural(moved, "element")} to their category's workset." + (made.Count > 0 ? " Made the worksets " + string.Join(", ", made) + "." : "");
        if (left.Count > 0)
            said += $" {Plural(left.Count, "element")} stayed: no workset is set for " + string.Join(", ", left.Distinct(StringComparer.OrdinalIgnoreCase).Take(8)) +
                    (map.Count == 0 ? " (set Workset for each category in Checking rules)." : " (add them to Workset for each category in Checking rules).");
        return said + Failed(failed);
    }

    private static string Backgrounds(Document doc)
    {
        var links = new ElementId(BuiltInCategory.OST_RvtLinks);
        var linkParam = new ElementId(BuiltInParameter.VIS_GRAPHICS_RVT_LINKS);
        var views = new List<View>();
        foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            if (doc.GetElement(vp.ViewId) is View v && v.ViewType is not (ViewType.Legend or ViewType.DraftingView or ViewType.Schedule) &&
                new FilteredElementCollector(doc, v.Id).OfClass(typeof(RevitLinkInstance)).GetElementCount() > 0 && !views.Any(x => x.Id == v.Id))
                views.Add(v);
        if (views.Count == 0) return "No view on a sheet shows a linked model: nothing to hide.";
        int hidden = 0, failed = 0;
        var templates = new List<string>();
        using (var t = new Transaction(doc, "Sketch ProMax: hide linked models on sheets"))
        {
            t.Start();
            foreach (var v in views)
            {
                // A template that controls Visibility/Graphics overrides the view's own setting, so the template is changed.
                var target = doc.GetElement(v.ViewTemplateId) is View tmpl && !tmpl.GetNonControlledTemplateParameterIds().Contains(linkParam) ? tmpl : v;
                try
                {
                    if (!target.CanCategoryBeHidden(links)) { failed++; continue; }
                    if (!target.GetCategoryHidden(links))
                    {
                        target.SetCategoryHidden(links, true);
                        if (target.IsTemplate) templates.Add(target.Name);
                    }
                    hidden++;
                }
                catch (Exception ex) when (Revit(ex)) { failed++; }
            }
            t.Commit();
        }
        return $"Hid the Revit links in {Plural(hidden, "view")} on sheets." + (templates.Count > 0 ? " Changed the view templates " + string.Join(", ", templates.Distinct()) + " (every view using them hides links too)." : "") + Failed(failed);
    }

    private static string ViewFolders(Document doc, ChecklistMath.Definition? def, string? answer)
    {
        var names = ChecklistFacts.ViewFolderParameters(doc, def);
        if (names.Count == 0) return "The Project Browser doesn't sort views by a parameter of yours: nothing to fill.";
        var typed = (answer ?? "").Trim();
        int filled = 0, failed = 0;
        var left = new List<string>();
        string? Text(Element? e, string n) => e?.LookupParameter(n) is { StorageType: StorageType.String } p && (p.AsString() ?? "").Trim() is { Length: > 0 } x ? x : null;
        using (var t = new Transaction(doc, "Sketch ProMax: fill the view folders"))
        {
            t.Start();
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                         .Where(v => !v.IsTemplate && v.ViewType is not (ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined or ViewType.DrawingSheet)).ToList())
                foreach (var n in names)
                {
                    var p = v.LookupParameter(n);
                    if (p is null || ChecklistFacts.Filled(p)) continue;
                    var value = Text(doc.GetElement(v.ViewTemplateId), n) ?? Text(doc.GetElement(v.GetTypeId()), n) ?? (typed.Length > 0 ? typed : null);
                    if (value is null || p.IsReadOnly || p.StorageType != StorageType.String) { left.Add(v.Name); continue; }
                    try { if (p.Set(value)) filled++; else failed++; }
                    catch (Exception ex) when (Revit(ex)) { failed++; }
                }
            t.Commit();
        }
        return $"Filled {Plural(filled, "empty view folder parameter")} ({string.Join(", ", names)})." +
               (left.Count > 0 ? $" {Plural(left.Distinct().Count(), "view")} still have one empty (no value from the template or type{(typed.Length > 0 ? ", or it can't be set there" : "; type a value to fill them")}): " + string.Join(", ", left.Distinct().Take(8)) + (left.Distinct().Count() > 8 ? " and more" : "") + "." : "") +
               Failed(failed);
    }

    private static string ModelName(Document doc, ChecklistMath.Definition? def)
    {
        var param = ChecklistFacts.ModelNameParameter(doc, def);
        if (param.Length == 0) return "No sheet or title block parameter shows the model name: name it in Checking rules (Sheet parameter with the model name).";
        var name = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(FileName(doc)));
        var blocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().ToList();
        int written = 0, failed = 0, same = 0;
        using (var t = new Transaction(doc, "Sketch ProMax: write the model name on the sheets"))
        {
            t.Start();
            foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(x => !x.IsPlaceholder))
            {
                var p = sheet.LookupParameter(param) ?? blocks.FirstOrDefault(b => b.OwnerViewId == sheet.Id)?.LookupParameter(param);
                if (p is null) continue;
                if ((p.AsString() ?? "").Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) { same++; continue; }
                if (p.IsReadOnly || p.StorageType != StorageType.String) { failed++; continue; }
                try { if (p.Set(name)) written++; else failed++; }
                catch (Exception ex) when (Revit(ex)) { failed++; }
            }
            t.Commit();
        }
        if (written + same + failed == 0) return $"No sheet or title block has a parameter named {param}.";
        return $"Wrote {name} into {param} on {Plural(written, "sheet")}" + (same > 0 ? $" ({same} already had it)" : "") + "." + Failed(failed);
    }

    /// <summary>Revit's "identical instances in the same place" warnings: one of each set is kept (the oldest), the rest deleted.</summary>
    internal static List<ElementId> IdenticalCopies(Document doc)
    {
        var keep = new HashSet<long>();
        var delete = new Dictionary<long, ElementId>();
        foreach (var w in doc.GetWarnings().Where(w => w.GetFailureDefinitionId() == BuiltInFailures.OverlapFailures.DuplicateInstances))
        {
            var ids = w.GetFailingElements().Concat(w.GetAdditionalElements()).GroupBy(i => i.Value).Select(g => g.First()).OrderBy(i => i.Value).ToList();
            if (ids.Count < 2) continue;
            if (!ids.Any(i => keep.Contains(i.Value))) keep.Add(ids[0].Value);
            foreach (var id in ids.Where(i => !keep.Contains(i.Value))) delete[id.Value] = id;
        }
        return delete.Values.ToList();
    }

    private static string Duplicates(Document doc)
    {
        var copies = IdenticalCopies(doc);
        if (copies.Count == 0) return "Revit warns of no identical instances in the same place: nothing to delete.";
        var (deleted, failed) = Delete(doc, copies, "delete identical instances");
        return $"Deleted {Plural(deleted, "identical copy")}, keeping one of each." + Failed(failed);
    }

    /// <summary>The submitted copy's file name: the model's working name with the submission ending, # as the first free number.</summary>
    internal static string? SubmissionFile(string folder, string model, string ending)
    {
        var name = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(model));
        var digits = ending.Count(c => c == '#');
        for (var n = 0; n < Math.Pow(10, Math.Min(digits, 3)); n++)
        {
            var number = n.ToString(new string('0', Math.Max(digits, 1)), System.Globalization.CultureInfo.InvariantCulture);
            var built = digits == 0 ? ending : System.Text.RegularExpressions.Regex.Replace(ending, "#+", number);
            var path = Path.Combine(folder, name + built + ".rvt");
            if (!File.Exists(path)) return path;
            if (digits == 0) return null;
        }
        return null;
    }

    private static string Submission(Document doc, ChecklistMath.Definition? def)
    {
        var ending = ChecklistMath.RuleList(ChecklistMath.Rule(def, "submission")).FirstOrDefault() ?? "_S03_R##";
        if (ending.IndexOfAny(['*', '?']) >= 0) return $"The Submitted copy ending in Checking rules ({ending}) is a pattern; give it as _S03_R## so the copy can be named.";
        var path = "";
        try { path = doc.PathName ?? ""; } catch (ArgumentException) { }
        if (path.Length == 0 || !File.Exists(path)) return "The model isn't saved on this PC, so there's no file to copy. Save it first.";
        var name = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(Path.GetFileName(path)));
        if (ChecklistMath.Like(name, "*" + ending)) return $"This model is already the submitted copy ({name}).";
        if (doc.IsModified) return "The model has changes that aren't saved: save it first, so the copy has them.";
        var folder = Path.GetDirectoryName(path) ?? "";
        if (SubmissionFile(folder, Path.GetFileName(path), ending) is not { } target) return $"Every number for {name}{ending} is taken next to the model.";
        try { File.Copy(path, target, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"Couldn't save {Path.GetFileName(target)} ({ex.Message})."; }
        return $"Saved {Path.GetFileName(target)} next to the model." + (doc.IsWorkshared ? " It's a copy of a workshared file: open it with Detach from Central and save it once before you submit it." : "");
    }

    private static string FileName(Document doc)
    {
        var file = "";
        try { file = Path.GetFileName(doc.PathName ?? ""); } catch (ArgumentException) { }
        return file.Length > 0 ? file : doc.Title;
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
