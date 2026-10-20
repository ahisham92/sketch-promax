using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// What the open model says about the pre-submission checks (read only, nothing changes): the header fields it can fill
/// (file names, building and discipline, stage) and a short hint per check that ChecklistMath.AutoRules gives a key,
/// such as "3 elements on Workset1" or "No imported CAD". A hint never answers a check: the user still picks Y, N or NA.
/// Each hint is read on its own, so one that fails says so and the others still come.
/// </summary>
internal static class ChecklistFacts
{
    /// <summary>A hint: its text and whether it looks fine (Ok), needs a look (Warn) or is just information.</summary>
    public enum Level { Info, Ok, Warn }

    /// <summary>A hint: the sentence shown, its level, and what it found by name (shown in full in the details panel).</summary>
    public sealed record Hint(string Text, Level Level, IReadOnlyList<string>? Found = null)
    {
        public IReadOnlyList<string> Items => Found ?? [];
    }

    public sealed class Result
    {
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Hint> Hints { get; } = new(StringComparer.Ordinal);
        public string Model { get; set; } = "";
        public string Title { get; set; } = "";
    }

    /// <summary>Reads the model. Must run in a Revit API context (a command, or the dispatcher).</summary>
    public static Result Read(Document doc, ChecklistMath.Definition? def)
    {
        var r = new Result { Title = doc.Title };
        var file = "";
        try { file = Path.GetFileName(doc.PathName ?? ""); } catch (ArgumentException) { }
        if (file.Length == 0) file = doc.Title;
        r.Model = file;
        var parts = ChecklistMath.NameParts(file);

        // Header fields.
        r.Fields["model_file"] = ChecklistMath.WorkingName(file);
        r.Fields["submission_file"] = ChecklistMath.SubmissionName(ChecklistMath.WorkingName(file));
        var building = ChecklistMath.BuildingName(parts.Building);
        r.Fields["building"] = building.Length > 0 && parts.Discipline.Length > 0 ? building + " / " + parts.Discipline : building + parts.Discipline;
        try
        {
            var status = doc.ProjectInformation?.Status ?? "";
            if (status.Trim().Length > 0) r.Fields["stage"] = status.Trim();
        }
        catch (Exception) { }
        r.Fields["date"] = DateTime.Now.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);

        var wanted = new HashSet<string>(def?.Items.Select(i => i.Auto).Where(a => a.Length > 0) ?? ChecklistMath.AutoRules.Select(a => a.Key), StringComparer.Ordinal);
        // Each hint's check text and example (the expected names: link worksets, the title block file).
        var example = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in def?.Items ?? [])
            if (item.Auto.Length > 0 && !example.ContainsKey(item.Auto)) example[item.Auto] = item.Example + "\n" + item.Check;
        string Ex(string key) => example.TryGetValue(key, out var e) ? e : "";

        Views? views = null;
        Views V() => views ??= new Views(doc);
        string Rule(string key) => ChecklistMath.Rule(def, key);
        const string Rules = " (set in Checking rules)";

        void Add(string key, Func<Hint?> read)
        {
            if (!wanted.Contains(key)) return;
            try { if (read() is { } h) r.Hints[key] = h; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { r.Hints[key] = new Hint("Couldn't read this from the model (" + ex.Message + ").", Level.Info); }
        }

        Add("name002", () =>
        {
            var pattern = Rule("filename");
            var name = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(file));
            if (pattern.Length == 0) return new Hint($"The file name is {file}. No file name pattern is set" + Rules + ".", Level.Info);
            return ChecklistMath.LikeAny(name, pattern)
                ? new Hint($"The file name {name} matches {pattern}.", Level.Ok)
                : new Hint($"The file name {name} doesn't match {pattern}" + Rules + ".", Level.Warn, [name + " (expected " + pattern + ")"]);
        });
        Add("doctype", () =>
        {
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Count(s => !s.IsPlaceholder);
            return parts.DocType.Length == 0
                ? new Hint($"The file name doesn't follow the naming pattern, so the doc type can't be read. The model has {sheets} sheets.", Level.Warn)
                : new Hint($"Doc type in the file name: {parts.DocType}. The model has {sheets} sheets.", Level.Info);
        });
        Add("levelfield", () => parts.Level.Length == 0 ? null : new Hint($"Level field in the file name: {parts.Level}.", Level.Info));
        Add("filesize", () =>
        {
            if (string.IsNullOrEmpty(doc.PathName) || !File.Exists(doc.PathName)) return new Hint("The model isn't saved on this PC, so its size can't be read.", Level.Info);
            var mb = new FileInfo(doc.PathName).Length / 1048576.0;
            var limit = double.TryParse(Rule("filesize"), NumberStyles.Float, CultureInfo.InvariantCulture, out var l) && l > 0 ? l : 300;
            return new Hint($"This file is {mb:0} MB on disk now; the limit is {limit:0} MB (check the e-Transmit output after purge).", mb > limit ? Level.Warn : Level.Ok);
        });
        Add("urs", () =>
        {
            var urs = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                .Where(t => t.Name.IndexOf("URS", StringComparison.OrdinalIgnoreCase) >= 0).Select(t => t.Name).ToList();
            return urs.Count == 0 ? new Hint("No linked model with URS in its name.", Level.Warn) : new Hint("URS link: " + string.Join(", ", urs) + ".", Level.Ok);
        });
        Add("grids", () =>
        {
            var n = new FilteredElementCollector(doc).OfClass(typeof(Grid)).GetElementCount();
            return n == 0 ? new Hint("The model has no grids.", Level.Warn) : new Hint($"The model has {n} grids.", Level.Ok);
        });
        Add("levels", () =>
        {
            var names = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level)).Select(l => l.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var pattern = Rule("levels");
            if (pattern.Length == 0) return new Hint($"{names.Count} levels. No level name pattern is set" + Rules + ".", Level.Info, names);
            var off = names.Where(n => !ChecklistMath.LikeAny(n, pattern)).ToList();
            return off.Count == 0
                ? new Hint($"{names.Count} levels, all named {pattern}.", Level.Ok)
                : new Hint($"{names.Count} levels; {off.Count} not named {pattern}: {List(off)}.", Level.Warn, off);
        });
        Add("worksets", () =>
        {
            if (!doc.IsWorkshared) return new Hint("The model isn't workshared, so it has no worksets.", Level.Warn);
            var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToList();
            var problems = new List<string>();
            var one = worksets.FirstOrDefault(w => w.Name.Equals("Workset1", StringComparison.OrdinalIgnoreCase));
            if (one is not null)
            {
                var n = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementWorksetFilter(one.Id, false)).GetElementCount();
                if (n > 0) problems.Add($"{n} elements on Workset1");
            }
            var shared = worksets.FirstOrDefault(w => w.Name.Equals("Shared Levels and Grids", StringComparison.OrdinalIgnoreCase));
            if (shared is not null)
            {
                var others = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementWorksetFilter(shared.Id, false))
                    .Count(e => e.Category is { } c && c.CategoryType == CategoryType.Model && c.BuiltInCategory is not (BuiltInCategory.OST_Levels or BuiltInCategory.OST_Grids));
                if (others > 0) problems.Add($"{others} elements other than levels and grids on Shared Levels and Grids");
            }
            var names = new HashSet<string>(worksets.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
            var missing = ChecklistMath.RuleList(Rule("linkworksets")).Where(n => !names.Contains(n)).ToList();
            foreach (var m in missing) problems.Add("missing workset " + m);
            return problems.Count == 0
                ? new Hint($"{worksets.Count} worksets; nothing on Workset1, only levels and grids on Shared Levels and Grids, the link worksets are there.", Level.Ok)
                : new Hint(string.Join("; ", problems) + ".", Level.Warn, problems);
        });
        Add("scopeboxes", () =>
        {
            var names = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().Select(e => e.Name).ToList();
            if (names.Count == 0) return new Hint("The model has no scope boxes.", Level.Warn);
            var pattern = Rule("scopeboxes");
            if (pattern.Length == 0) return new Hint($"{names.Count} scope boxes. No scope box name pattern is set" + Rules + ".", Level.Info, names);
            var off = names.Where(n => !ChecklistMath.LikeAny(n, pattern)).ToList();
            return off.Count == 0 ? new Hint($"{names.Count} scope boxes, all named {pattern}.", Level.Ok) : new Hint($"{off.Count} of {names.Count} scope boxes not named {pattern}: {List(off)}.", Level.Warn, off);
        });
        Add("categories", () =>
        {
            var generic = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType().ToList();
            return generic.Count == 0 ? new Hint("No Generic Model elements.", Level.Ok)
                : new Hint($"{generic.Count} Generic Model elements.", Level.Warn, Counted(generic.Select(TypeName)));
        });
        Add("imports", () =>
        {
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(i => !i.IsLinked).ToList();
            return imports.Count == 0 ? new Hint("No imported CAD in the project (inside families isn't counted).", Level.Ok)
                : new Hint($"{imports.Count} imported CAD files in the project.", Level.Warn, Counted(imports.Select(TypeName)));
        });
        Add("groups", () =>
        {
            var groups = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_IOSModelGroups).WhereElementIsNotElementType().Cast<Autodesk.Revit.DB.Group>().ToList();
            var once = groups.GroupBy(g => g.GroupType?.Id.Value ?? -1).Where(g => g.Count() == 1).Select(g => g.First().GroupType?.Name ?? g.First().Name).ToList();
            return groups.Count == 0 ? new Hint("No model groups.", Level.Ok)
                : new Hint($"{groups.Count} model groups; {once.Count} group types are used only once.", once.Count > 0 ? Level.Warn : Level.Info, once.Select(n => n + " (used once)").ToList());
        });
        Add("warnings", () =>
        {
            var warnings = doc.GetWarnings();
            return warnings.Count == 0 ? new Hint("No warnings.", Level.Ok)
                : new Hint($"{warnings.Count} warnings in the model.", Level.Warn, Counted(warnings.Select(w => w.GetDescriptionText())));
        });
        Add("landing", () =>
        {
            var start = StartingViewSettings.GetStartingViewSettings(doc).ViewId;
            var view = start == ElementId.InvalidElementId ? null : doc.GetElement(start) as View;
            var family = Rule("landing").Length > 0 ? Rule("landing") : "LandingPage";
            var landing = new FilteredElementCollector(doc).OfClass(typeof(Family)).Any(f => f.Name.IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0 || ChecklistMath.Like(f.Name, family));
            var text = (view is null ? "No starting view is set." : $"Starting view: {view.Name}.") + (landing ? $" The {family} family is loaded." : $" No {family} family is loaded.");
            return new Hint(text, view is not null && landing ? Level.Ok : Level.Warn);
        });
        Add("titleblock", () =>
        {
            var expected = Rule("titleblock").Length > 0 ? Rule("titleblock") : Regex.Match(Ex("titleblock"), @"([\w\-.]+)\.rfa", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "";
            var used = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
                .OfType<FamilyInstance>().Select(f => f.Symbol?.FamilyName ?? "").Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (used.Count == 0) return new Hint("No title blocks on sheets.", Level.Warn);
            var off = expected.Length == 0 ? new List<string>() : used.Where(n => !n.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToList();
            return off.Count == 0 ? new Hint("Title block on sheets: " + List(used) + ".", expected.Length > 0 ? Level.Ok : Level.Info)
                : new Hint("Other title blocks on sheets: " + List(off) + ".", Level.Warn, off.Select(n => n + $" (expected {expected})").ToList());
        });
        Add("scales", () =>
        {
            var scales = V().OnSheets.Where(v => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.Section or ViewType.Elevation or ViewType.Detail or ViewType.AreaPlan)
                .GroupBy(v => v.Scale).OrderBy(g => g.Key).Select(g => (Scale: g.Key, Count: g.Count())).ToList();
            if (scales.Count == 0) return new Hint("No plans, sections or details on sheets.", Level.Info);
            var text = "Scales on sheets: " + string.Join(", ", scales.Select(s => $"1:{s.Scale} ({s.Count})")) + ".";
            var approved = ChecklistMath.RuleList(Rule("scales")).Select(a => int.TryParse(Regex.Replace(a, @"^\s*1\s*:\s*", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n > 0).ToHashSet();
            if (approved.Count == 0) return new Hint(text + " No approved scales are set" + Rules + ".", Level.Info);
            var rejected = V().OnSheets.Where(v => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.Section or ViewType.Elevation or ViewType.Detail or ViewType.AreaPlan && !approved.Contains(v.Scale))
                .Select(v => $"{v.Name} at 1:{v.Scale}").ToList();
            return new Hint(text, rejected.Count > 0 ? Level.Warn : Level.Ok, rejected);
        });
        Add("drawinglist", () =>
        {
            var lists = V().SchedulesOnSheets.Count(s => s.Definition?.CategoryId.Value == (long)BuiltInCategory.OST_Sheets);
            var notes = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Any(s => s.Name.IndexOf("General Notes", StringComparison.OrdinalIgnoreCase) >= 0);
            var text = (lists > 0 ? "A sheet list schedule is on a sheet." : "No sheet list schedule is on a sheet.") + (notes ? " A General Notes sheet is there." : " No sheet is named General Notes.");
            return new Hint(text, lists > 0 && notes ? Level.Ok : Level.Warn);
        });
        Add("viewtemplates", () =>
        {
            var graphic = V().OnSheets.Where(v => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.Section or ViewType.Elevation or ViewType.ThreeD).ToList();
            if (graphic.Count == 0) return new Hint("No plans, sections or 3D views on sheets.", Level.Info);
            var bare = graphic.Where(v => v.ViewTemplateId == ElementId.InvalidElementId).Select(v => v.Name + " (no view template)").ToList();
            var none = bare.Count;
            var templates = graphic.Where(v => v.ViewTemplateId != ElementId.InvalidElementId).Select(v => v.ViewTemplateId.Value).Distinct().Count();
            return new Hint($"{none} of {graphic.Count} views on sheets have no view template; {templates} templates are used.", none > 0 || templates == 1 && graphic.Count > 3 ? Level.Warn : Level.Ok, bare);
        });
        Add("unusedviews", () =>
        {
            var unused = UnusedViews(doc);
            return unused.Count == 0 ? new Hint("Every view is on a sheet (or is the Navisworks view or the starting view).", Level.Ok)
                : new Hint($"{unused.Count} views are on no sheet.", Level.Warn, unused.Select(v => v.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
        });
        Add("navisview", () =>
        {
            var pattern = Rule("navisview").Length > 0 ? Rule("navisview") : "*Navisworks*";
            var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate && ChecklistMath.LikeAny(v.Name, pattern)).ToList();
            if (views.Count == 0) return new Hint($"No 3D view named {pattern}.", Level.Warn);
            var v = views[0];
            return new Hint($"3D view {v.Name}{(v.ViewTemplateId == ElementId.InvalidElementId ? ", with no view template" : "")}.", v.ViewTemplateId == ElementId.InvalidElementId ? Level.Warn : Level.Ok);
        });
        Add("central", () => doc.IsDetached ? new Hint("This copy is detached: export the NWC from the synced central model, not from here.", Level.Info)
            : doc.IsWorkshared ? new Hint("This is a local copy of a central model: sync before the NWC export.", Level.Info)
            : new Hint("The model isn't workshared.", Level.Info));
        Add("links", () =>
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            var loaded = types.Where(t => RevitLinkType.IsLoaded(doc, t.Id)).Select(t => t.Name).ToList();
            var detached = doc.IsDetached ? "This copy is detached. " : doc.IsWorkshared ? "This isn't a detached copy. " : "";
            return loaded.Count == 0
                ? new Hint(detached + $"No Revit links are loaded ({types.Count} unloaded).", doc.IsDetached || !doc.IsWorkshared ? Level.Ok : Level.Warn)
                : new Hint(detached + $"{loaded.Count} Revit links still loaded: {List(loaded)}.", Level.Warn, loaded);
        });
        // Checks made automatic by the checking rules.
        Add("newworksets", () =>
        {
            var approved = Rule("approvedworksets");
            if (approved.Length == 0 || !doc.IsWorkshared) return null; // not tested: you check it
            var off = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => w.Name)
                .Where(n => !n.Equals("Workset1", StringComparison.OrdinalIgnoreCase) && !n.Equals("Shared Levels and Grids", StringComparison.OrdinalIgnoreCase) && !ChecklistMath.LikeAny(n, approved))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return off.Count == 0 ? new Hint("Every workset is on the approved list.", Level.Ok) : new Hint($"{off.Count} worksets aren't on the approved list: {List(off)}.", Level.Warn, off);
        });
        Add("columns", () =>
        {
            if (!Rule("columns").StartsWith("y", StringComparison.OrdinalIgnoreCase)) return null;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level)).Cast<Autodesk.Revit.DB.Level>().Select(l => l.Elevation).OrderBy(e => e).ToList();
            var tall = new List<string>();
            var count = 0;
            foreach (var c in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                count++;
                var baseId = c.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId();
                var topId = c.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId();
                if (baseId is null || topId is null || doc.GetElement(baseId) is not Autodesk.Revit.DB.Level b || doc.GetElement(topId) is not Autodesk.Revit.DB.Level t) continue;
                var between = levels.Count(e => e > b.Elevation + 1e-6 && e < t.Elevation - 1e-6);
                if (between > 0) tall.Add($"{TypeName(c)} (id {c.Id.Value}): {b.Name} to {t.Name}");
            }
            if (count == 0) return new Hint("The model has no structural columns.", Level.Info);
            return tall.Count == 0 ? new Hint($"All {count} columns stop at the next level up.", Level.Ok)
                : new Hint($"{tall.Count} of {count} columns run past the next level up. Split them, or list the double-height ones in the comment.", Level.Warn, tall);
        });
        Add("uniformat", () =>
        {
            var names = ChecklistMath.RuleList(Rule("classification"));
            if (names.Count == 0) return null;
            var missing = TypesInUse(doc).Where(t => !names.Any(n => Filled(t.LookupParameter(n))))
                .Select(t => TypeLabel(t)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return missing.Count == 0 ? new Hint($"Every model type in use has {string.Join(" or ", names)} filled.", Level.Ok)
                : new Hint($"{missing.Count} model types in use have no {string.Join(" or ", names)}.", Level.Warn, missing);
        });
        Add("ifc", () =>
        {
            var names = ChecklistMath.RuleList(Rule("ifc"));
            if (names.Count == 0) return null;
            bool Has(Element e) => names.Any(n => Filled(e.LookupParameter(n)))
                                    || Filled(e.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS)) || Filled(e.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS));
            var byType = Instances(doc).GroupBy(e => e.GetTypeId().Value).ToList();
            var missing = new List<string>();
            foreach (var g in byType)
            {
                if (doc.GetElement(new ElementId(g.Key)) is not ElementType t || Has(t) || g.All(Has)) continue;
                missing.Add(TypeLabel(t));
            }
            missing.Sort(StringComparer.OrdinalIgnoreCase);
            return missing.Count == 0 ? new Hint("Every model type in use has its IFC entity set (by type or on every instance).", Level.Ok)
                : new Hint($"{missing.Count} model types in use have no IFC entity set.", Level.Warn, missing);
        });
        Add("parameters", () =>
        {
            var bad = Rule("badparameters");
            if (bad.Length == 0) return null;
            var names = new List<string>();
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext()) if (it.Key is Definition d) names.Add(d.Name);
            var off = names.Where(n => ChecklistMath.LikeAny(n, bad)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return off.Count == 0 ? new Hint($"{names.Count} project parameters; none matches {bad}.", Level.Ok)
                : new Hint($"{off.Count} project parameters match {bad}: {List(off)}.", Level.Warn, off);
        });
        Add("tbmodelname", () =>
        {
            var param = Rule("tbmodelname");
            if (param.Length == 0) return null;
            var name = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(file));
            var blocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().ToList();
            var off = new List<string>();
            var found = 0;
            foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(x => !x.IsPlaceholder))
            {
                var p = sheet.LookupParameter(param) ?? blocks.FirstOrDefault(b => b.OwnerViewId == sheet.Id)?.LookupParameter(param);
                if (p is null) continue;
                found++;
                var value = (p.AsString() ?? p.AsValueString() ?? "").Trim();
                if (value.Length == 0 || value.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf(Path.GetFileNameWithoutExtension(value), StringComparison.OrdinalIgnoreCase) < 0)
                    off.Add($"{sheet.SheetNumber} {sheet.Name}: \"{value}\"");
            }
            if (found == 0) return new Hint($"No sheet or title block has a parameter named {param}" + Rules + ".", Level.Warn);
            return off.Count == 0 ? new Hint($"{param} matches the file name {name} on all {found} sheets.", Level.Ok)
                : new Hint($"{off.Count} of {found} sheets show a different model name in {param} (file: {name}).", Level.Warn, off);
        });
        Add("viewfolders", () =>
        {
            var names = ChecklistMath.RuleList(Rule("viewfolders"));
            if (names.Count == 0) return null;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType is not (ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined or ViewType.DrawingSheet)).ToList();
            if (!views.Any(v => names.Any(n => v.LookupParameter(n) is not null))) return new Hint($"No view has a parameter named {string.Join(" or ", names)}" + Rules + ".", Level.Warn);
            var off = views.Where(v => names.Any(n => !Filled(v.LookupParameter(n)))).Select(v => v.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return off.Count == 0 ? new Hint($"Every view has {string.Join(" and ", names)} filled.", Level.Ok)
                : new Hint($"{off.Count} views have {string.Join(" or ", names)} empty, so they land in ???.", Level.Warn, off);
        });
        return r;
    }

    /// <summary>Elements of model categories that have material quantities (the physical model), not in links.</summary>
    private static IEnumerable<Element> Instances(Document doc) =>
        new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent()
            .Where(e => e.Category is { CategoryType: CategoryType.Model, HasMaterialQuantities: true } && e.GetTypeId() != ElementId.InvalidElementId);

    /// <summary>The types those elements use.</summary>
    private static List<ElementType> TypesInUse(Document doc) =>
        Instances(doc).Select(e => e.GetTypeId().Value).Distinct().Select(id => doc.GetElement(new ElementId(id)) as ElementType).Where(t => t is not null).Select(t => t!).ToList();

    private static string TypeLabel(ElementType t) => (t.Category?.Name is { Length: > 0 } c ? c + ": " : "") + (t.FamilyName.Length > 0 ? t.FamilyName + ": " : "") + t.Name;

    private static bool Filled(Parameter? p)
    {
        if (p is null || !p.HasValue) return false;
        return p.StorageType switch
        {
            StorageType.String => (p.AsString() ?? "").Trim().Length > 0,
            StorageType.ElementId => p.AsElementId() != ElementId.InvalidElementId,
            _ => (p.AsValueString() ?? "").Trim().Length > 0,
        };
    }

    /// <summary>
    /// Views on no sheet that can go: not the Navisworks view, not the starting view, not a view whose dependent views
    /// would go with it, not the view open in front. The unusedviews hint counts these and ChecklistFixes deletes them.
    /// </summary>
    public static List<View> UnusedViews(Document doc, ElementId? active = null)
    {
        var on = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(v => v.ViewId.Value));
        var start = StartingViewSettings.GetStartingViewSettings(doc).ViewId;
        return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.CanBePrinted && v.ViewType is not (ViewType.DrawingSheet or ViewType.Schedule or ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined)
                        && !on.Contains(v.Id.Value) && v.Id != start && v.Id != active && v.Name.IndexOf("Navisworks", StringComparison.OrdinalIgnoreCase) < 0
                        && v.GetDependentViewIds().Count == 0)
            .ToList();
    }

    /// <summary>"Name (×3)" lines, most frequent first.</summary>
    private static List<string> Counted(IEnumerable<string> names) =>
        names.GroupBy(n => n.Length > 0 ? n : "(no name)").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} (×{g.Count()})" : g.Key).ToList();

    private static string TypeName(Element e)
    {
        var type = e.Document.GetElement(e.GetTypeId()) as ElementType;
        return type is null ? e.Name : type.FamilyName.Length > 0 ? type.FamilyName + ": " + type.Name : type.Name;
    }

    private static string List(IReadOnlyCollection<string> names) =>
        names.Count <= 4 ? string.Join(", ", names) : string.Join(", ", names.Take(4)) + $" and {names.Count - 4} more";

    /// <summary>The views and schedules placed on sheets, read once for the hints that need them.</summary>
    private sealed class Views
    {
        public readonly List<View> OnSheets;
        public readonly List<ViewSchedule> SchedulesOnSheets;

        public Views(Document doc)
        {
            OnSheets = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                .Select(v => doc.GetElement(v.ViewId) as View).Where(v => v is not null).Select(v => v!)
                .GroupBy(v => v.Id.Value).Select(g => g.First()).ToList();
            SchedulesOnSheets = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .Where(s => !s.IsTitleblockRevisionSchedule).Select(s => doc.GetElement(s.ScheduleId) as ViewSchedule).Where(s => s is not null).Select(s => s!).ToList();
        }
    }
}
