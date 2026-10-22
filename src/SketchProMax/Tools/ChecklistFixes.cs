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
    /// <summary>
    /// <paramref name="Ask"/>: a value the window asks for before running (the answer is passed to Run; empty is allowed).
    /// <paramref name="Preview"/>: the window first shows what would change (Suggest) for the user to edit, and passes the
    /// edited list to Run. <paramref name="Fix"/>: the fix Run is called with, when not the check's own key.
    /// </summary>
    public sealed record Offer(string Button, string Question, bool ChangesModel = true, string? Ask = null, bool Preview = false, string? Fix = null);

    /// <summary>Every fix a check offers: its own, then any other (levels can also be named like the URS).</summary>
    public static List<Offer> Offers(string key, ChecklistMath.Definition? def = null)
    {
        var list = new List<Offer>();
        if (For(key, def) is { } own) list.Add(own);
        if (key == "levels" && For("scopeboxes", def) is { } urs) list.Add(urs);
        return list;
    }

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
        "units" => new("Set the project units",
            $"Set the project units to {Named(ChecklistMath.Rule(def, "units"), "the Project units in Checking rules")} (Manage > Project Units)? Other units are left as they are."),
        "uniformat" => new("Fill the UniFormat codes",
            $"Write the UniFormat code into {Named(ChecklistMath.RuleList(ChecklistMath.Rule(def, "classification")).FirstOrDefault() ?? "", "the classification parameter")} of every model type in use that has none, " +
            $"from {Named(ChecklistMath.Rule(def, "uniformatmap"), "UniFormat code for each category or type in Checking rules (set it first)")}? Types already filled are left alone."),
        "loi" => new("Fill the LOI data",
            $"Fill the parameters the LOI matrix asks for ({Named(ChecklistMath.Rule(def, "loimatrix"), "set LOI matrix in Checking rules first")}) on every model type in use, and its elements, that has them empty? " +
            "Values come from LOI values the model can't give (Checking rules), else from the model: material, sizes, length, levels, grade, cut-off and toe. A parameter the model doesn't have is added as a project parameter " +
            "(from your shared parameter file when it has one of that name). Values already filled are left alone. Fill the UniFormat codes first: the matrix goes by code. You see each value first and can change or untick it.", Preview: true),
        "scopeboxes" => new("Name levels and scope boxes like the URS", 
            "Rename each level to the name of the URS link's level at the same height, and each scope box to the name of the URS link's scope box in the same place? " +
            "The URS link must be loaded. Ones with no match in the URS keep their names.", Fix: "scopeboxes"),
        "viewtemplates" => new("Apply the view templates by view type",
            $"Apply the view template for each view type ({Named(ChecklistMath.Rule(def, "templatemap"), "set View template for each view type in Checking rules first")}) to every view on a sheet that has another one or none?"),
        "familynames" => new("Rename the types",
            $"Rename the model types in use that aren't named {Named(ChecklistMath.Rule(def, "typenames"), "the Type name pattern")}? You see each suggested name first and can change or untick it.", Preview: true),
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
            case "units": return Units(doc, def);
            case "uniformat": return UniFormat(doc, def);
            case "scopeboxes": return UrsNames(doc);
            case "viewtemplates": return Templates(doc, def);
            case "familynames": return RenameTypes(doc, answer);
            case "loi": return LoiFill.Apply(doc, LoiFill.SpecOf(def), answer);
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

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

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

    private static string Units(Document doc, ChecklistMath.Definition? def)
    {
        var wanted = ChecklistMath.ParseUnits(ChecklistMath.Rule(def, "units"));
        if (wanted.Count == 0) return "No Project units are set in Checking rules (e.g. Length mm 0, Angle degrees 2).";
        var units = doc.GetUnits();
        var done = new List<string>();
        foreach (var u in wanted)
        {
            var (spec, unit, accuracy) = ChecklistFacts.UnitTarget(u);
            var old = units.GetFormatOptions(spec);
            var fo = new FormatOptions(unit);
            if (fo.IsValidAccuracy(accuracy)) fo.Accuracy = accuracy;
            // Keep the symbol shown when the new unit has it, else the unit's own (° for degrees) or none.
            var symbols = FormatOptions.GetValidSymbols(unit);
            var symbol = symbols.FirstOrDefault(x => x.Equals(old.GetSymbolTypeId())) ?? (u.Kind == "angle" ? symbols.FirstOrDefault(x => !x.Empty()) : null);
            if (symbol is not null && fo.CanHaveSymbol()) fo.SetSymbolTypeId(symbol);
            units.SetFormatOptions(spec, fo);
            done.Add($"{u.Kind} {u.Unit}, {u.Decimals} decimals");
        }
        using (var t = new Transaction(doc, "Sketch ProMax: set the project units"))
        {
            t.Start();
            doc.SetUnits(units);
            t.Commit();
        }
        return "Set the project units: " + string.Join("; ", done) + ".";
    }

    /// <summary>The Category>code (or type name pattern>code) pairs of the uniformatmap rule.</summary>
    internal static List<(string Key, string Code)> UniFormatMap(ChecklistMath.Definition? def) =>
        ChecklistMath.RuleList(ChecklistMath.Rule(def, "uniformatmap")).Select(p => p.Split('>'))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0).Select(p => (p[0].Trim(), p[1].Trim())).ToList();

    /// <summary>The code for a type: a pair naming its category (or the start of it), else one whose pattern matches its family or type name.</summary>
    internal static string? UniFormatFor(string category, string family, string type, List<(string Key, string Code)> map) =>
        map.Where(m => category.Equals(m.Key, StringComparison.OrdinalIgnoreCase) || category.StartsWith(m.Key, StringComparison.OrdinalIgnoreCase))
            .Concat(map.Where(m => ChecklistMath.Like(type, m.Key) || ChecklistMath.Like(family, m.Key) || ChecklistMath.Like(family + ": " + type, m.Key)))
            .Select(m => m.Code).FirstOrDefault();

    private static string UniFormat(Document doc, ChecklistMath.Definition? def)
    {
        var names = ChecklistMath.RuleList(ChecklistMath.Rule(def, "classification"));
        if (names.Count == 0) return "No classification parameter is set in Checking rules.";
        var map = UniFormatMap(def);
        if (map.Count == 0) return "Set UniFormat code for each category or type in Checking rules first (e.g. Structural Foundations>A1020, Floors>B1010).";
        var types = ChecklistFacts.TypesOf(doc, ChecklistFacts.Instances(doc)).Where(t => !names.Any(n => ChecklistFacts.Filled(t.LookupParameter(n)))).ToList();
        if (types.Count == 0) return $"Every model type in use has {names[0]} filled: nothing to fill.";
        int filled = 0, failed = 0;
        var left = new List<string>();
        using (var t = new Transaction(doc, "Sketch ProMax: fill the UniFormat codes"))
        {
            t.Start();
            foreach (var type in types)
            {
                var code = UniFormatFor(type.Category?.Name ?? "", type.FamilyName, type.Name, map);
                var p = names.Select(n => type.LookupParameter(n)).FirstOrDefault(x => x is not null);
                if (code is null || p is null || p.IsReadOnly || p.StorageType != StorageType.String) { left.Add(ChecklistFacts.TypeLabel(type)); continue; }
                try { if (p.Set(code)) filled++; else failed++; }
                catch (Exception ex) when (Revit(ex)) { failed++; }
            }
            t.Commit();
        }
        return $"Filled the UniFormat code of {Plural(filled, "type")}." +
               (left.Count > 0 ? $" {Plural(left.Count, "type")} got none (no code set for them in Checking rules): {string.Join(", ", left.Take(8))}{(left.Count > 8 ? " and more" : "")}." : "") + Failed(failed);
    }

    /// <summary>Levels and scope boxes renamed like the URS link's: levels at the same height, scope boxes in the same place.</summary>
    private static string UrsNames(Document doc)
    {
        var link = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .FirstOrDefault(l => l.GetLinkDocument() is not null && (doc.GetElement(l.GetTypeId())?.Name ?? l.Name).IndexOf("URS", StringComparison.OrdinalIgnoreCase) >= 0);
        if (link?.GetLinkDocument() is not { } urs) return "No loaded linked model has URS in its name: load the URS link (Manage Links), then press Fix it again.";
        var tf = link.GetTotalTransform();
        const double sameHeight = 0.01; // feet, about 3 mm
        const double samePlace = 0.5;   // feet, about 150 mm
        var renames = new List<(Element E, string Name)>();
        var hostLevels = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level)).Cast<Autodesk.Revit.DB.Level>().ToList();
        foreach (var l in new FilteredElementCollector(urs).OfClass(typeof(Autodesk.Revit.DB.Level)).Cast<Autodesk.Revit.DB.Level>())
        {
            var z = tf.OfPoint(new XYZ(0, 0, l.ProjectElevation)).Z;
            if (hostLevels.FirstOrDefault(h => Math.Abs(h.ProjectElevation - z) < sameHeight) is { } match && match.Name != l.Name) renames.Add((match, l.Name));
        }
        XYZ? Centre(Element e) => e.get_BoundingBox(null) is { } b ? (b.Min + b.Max) / 2 : null;
        var hostBoxes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().ToList();
        foreach (var b in new FilteredElementCollector(urs).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType())
        {
            if (Centre(b) is not { } c) continue;
            var at = tf.OfPoint(c);
            if (hostBoxes.FirstOrDefault(h => Centre(h) is { } hc && hc.DistanceTo(at) < samePlace) is { } match && match.Name != b.Name) renames.Add((match, b.Name));
        }
        if (renames.Count == 0) return "Every level and scope box with a match in the URS already has its name (or none matches by height or place).";
        int done = 0;
        var taken = new List<string>();
        using (var t = new Transaction(doc, "Sketch ProMax: name levels and scope boxes like the URS"))
        {
            t.Start();
            foreach (var (e, name) in renames)
            {
                try { e.Name = name; done++; }
                catch (Exception ex) when (Revit(ex)) { taken.Add($"{e.Name} (the name {name} is taken)"); }
            }
            t.Commit();
        }
        return $"Renamed {Plural(done, "level or scope box", "levels and scope boxes")} like the URS ({link.Name.Split(':')[0].Trim()})." + (taken.Count > 0 ? " Not renamed: " + string.Join(", ", taken.Take(6)) + "." : "");
    }

    /// <summary>A view type named in the templatemap rule: Floor Plan, Structural Plan, Ceiling Plan, Section, Elevation, 3D View, Detail, Drafting, Area Plan.</summary>
    internal static bool SameViewType(ViewType type, string name)
    {
        var n = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (n.EndsWith("view", StringComparison.Ordinal) && n.Length > 4) n = n.Substring(0, n.Length - 4);
        return type switch
        {
            ViewType.FloorPlan => n is "floorplan" or "plan",
            ViewType.EngineeringPlan => n is "structuralplan" or "engineeringplan",
            ViewType.CeilingPlan => n is "ceilingplan" or "reflectedceilingplan" or "rcp",
            ViewType.ThreeD => n is "3d" or "threed",
            ViewType.DraftingView => n is "drafting",
            _ => n == type.ToString().ToLowerInvariant(),
        };
    }

    private static string Templates(Document doc, ChecklistMath.Definition? def)
    {
        var map = ChecklistMath.RuleList(ChecklistMath.Rule(def, "templatemap")).Select(p => p.Split('>'))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0).Select(p => (Type: p[0].Trim(), Template: p[1].Trim())).ToList();
        if (map.Count == 0) return "Set View template for each view type in Checking rules first (e.g. Floor Plan>ST_Plan, Section>ST_Section).";
        var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
        var views = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(vp => doc.GetElement(vp.ViewId) as View)
            .Where(v => v is not null && !v.IsTemplate).Cast<View>().GroupBy(v => v.Id.Value).Select(g => g.First()).ToList();
        int applied = 0, failed = 0;
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var t = new Transaction(doc, "Sketch ProMax: apply view templates by view type"))
        {
            t.Start();
            foreach (var v in views)
            {
                if (map.FirstOrDefault(m => SameViewType(v.ViewType, m.Type)) is not { Template: { } name }) continue;
                var tpl = templates.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? templates.FirstOrDefault(x => ChecklistMath.Like(x.Name, name));
                if (tpl is null) { missing.Add(name); continue; }
                if (v.ViewTemplateId == tpl.Id) continue;
                try
                {
                    if (!v.IsValidViewTemplate(tpl.Id)) { failed++; continue; }
                    v.ViewTemplateId = tpl.Id;
                    applied++;
                }
                catch (Exception ex) when (Revit(ex)) { failed++; }
            }
            t.Commit();
        }
        return $"Applied the view template to {Plural(applied, "view")} on sheets." + (missing.Count > 0 ? " Not in the model: " + string.Join(", ", missing) + "." : "") +
               (failed > 0 ? $" {failed} views couldn't take their template (it is for another kind of view, or they are in use)." : "");
    }

    /// <summary>
    /// Suggested names for the model types in use not named like the typenames rule: one line each, "id\tfamily: old\tnew".
    /// For loi, the LOI values to fill (LoiFill.Suggest), read in one go; the window reads them in steps.
    /// A suggestion keeps the old name's words, joined with _, behind the category's and the discipline's short codes.
    /// </summary>
    public static string Suggest(Document doc, string key, ChecklistMath.Definition? def)
    {
        if (key == "loi") { var spec = LoiFill.SpecOf(def); return LoiFill.Suggest(doc, spec, LoiFill.TypeIds(doc, spec)); }
        if (key != "familynames") return "";
        var pattern = ChecklistMath.Rule(def, "typenames");
        if (pattern.Length == 0) return "";
        var file = FileName(doc);
        var discipline = ChecklistMath.NameParts(file).Discipline;
        var lines = new List<string>();
        foreach (var t in ChecklistFacts.TypesOf(doc, ChecklistFacts.Instances(doc)).Where(t => !ChecklistMath.LikeAny(t.Name, pattern)).OrderBy(t => t.Category?.Name).ThenBy(t => t.Name))
            lines.Add($"{t.Id.Value}\t{ChecklistFacts.TypeLabel(t)}\t{SuggestName(t.Name, t.Category?.Name ?? "", discipline, pattern)}");
        return string.Join("\n", lines);
    }

    internal static string SuggestName(string name, string category, string discipline, string pattern)
    {
        var words = System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"[^A-Za-z0-9.]+", "_").Trim('_');
        if (ChecklistMath.LikeAny(words, pattern)) return words;
        var parts = category.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var code = parts.Length > 1 ? string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0]))) : category.Length >= 2 ? category.Substring(0, 2).ToUpperInvariant() : "XX";
        var built = string.Join("_", new[] { code, discipline.ToUpperInvariant(), words }.Where(x => x.Length > 0));
        return built;
    }

    private static string RenameTypes(Document doc, string? answer)
    {
        var wanted = (answer ?? "").Split('\n').Select(l => l.Split('\t')).Where(p => p.Length == 2 && long.TryParse(p[0], out _) && p[1].Trim().Length > 0)
            .Select(p => (Id: new ElementId(long.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture)), Name: p[1].Trim())).ToList();
        if (wanted.Count == 0) return "No type was picked to rename.";
        int done = 0;
        var failed = new List<string>();
        using (var t = new Transaction(doc, "Sketch ProMax: rename types"))
        {
            t.Start();
            foreach (var (id, name) in wanted)
            {
                if (doc.GetElement(id) is not ElementType type || type.Name == name) continue;
                try { type.Name = name; done++; }
                catch (Exception ex) when (Revit(ex)) { failed.Add($"{type.Name} → {name}"); }
            }
            t.Commit();
        }
        return $"Renamed {Plural(done, "type")}." + (failed.Count > 0 ? " Not renamed (the name is taken or not allowed): " + string.Join(", ", failed.Take(6)) + "." : "");
    }

    /// <summary>Selects these elements in the active model and zooms to them (Select these in Revit); a sentence saying what it did.</summary>
    public static string Select(UIDocument uidoc, IReadOnlyList<long> ids)
    {
        var doc = uidoc.Document;
        var found = ids.Select(i => new ElementId(i)).Where(i => doc.GetElement(i) is not null).ToList();
        if (found.Count == 0) return "None of them is in this model any more.";
        uidoc.Selection.SetElementIds(found);
        try { uidoc.ShowElements(found); }
        catch (Exception ex) when (Revit(ex)) { return $"Selected {Plural(found.Count, "element")}; no open view shows them, so open a 3D view to see them."; }
        return $"Selected {Plural(found.Count, "element")} and zoomed to them" + (found.Count < ids.Count ? $" ({ids.Count - found.Count} are gone)" : "") + ".";
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
