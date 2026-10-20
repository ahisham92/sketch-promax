using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

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

        /// <summary>A slow check not run yet: automatic, but it waits for Run the slow checks.</summary>
        public bool Pending { get; init; }
    }

    /// <summary>
    /// The checks that take longer (a full model check, a clash run, opening the trade's standard template): they run only
    /// when the user asks (Run the slow checks), not each time the window reads the model.
    /// </summary>
    public static readonly HashSet<string> Slow = new(StringComparer.Ordinal) { "duplicates", "floating", "selfclash", "modelscore", "standards", "etransmit", "packageopens" };

    public sealed class Result
    {
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Hint> Hints { get; } = new(StringComparer.Ordinal);
        public string Model { get; set; } = "";
        public string Title { get; set; } = "";
    }

    /// <summary>
    /// Reads the model. Must run in a Revit API context (a command, or the dispatcher). With <paramref name="slow"/> the
    /// slow checks run too (the model must be the active one); without it they are marked not run yet. Everything runs in
    /// this one call: the checklist window reads in steps instead (<see cref="Plan"/>), so Revit never freezes on a big model.
    /// </summary>
    public static Result Read(Document doc, ChecklistMath.Definition? def, UIApplication? slow = null)
    {
        var reading = Plan(doc, def, slow);
        while (reading.Next()) { }
        return reading.Result;
    }

    /// <summary>
    /// A model reading cut into steps, one hint each: <see cref="Next"/> reads the next hint. Each step is a short Revit
    /// API call of its own, so the caller can give Revit back between steps (it repaints and stays responsive).
    /// </summary>
    public sealed class Reading
    {
        private readonly Document _doc;
        private readonly Queue<Action> _steps = new();
        internal Reading(Document doc, Result result) { _doc = doc; Result = result; }

        /// <summary>The header fields at once; the hints as the steps run.</summary>
        public Result Result { get; }

        /// <summary>Steps left.</summary>
        public int Left => _steps.Count;

        internal void Add(Action step) => _steps.Enqueue(step);

        /// <summary>Reads the next hint (Revit API context). False when none is left, or the model was closed.</summary>
        public bool Next()
        {
            if (_steps.Count == 0) return false;
            if (!_doc.IsValidObject) { _steps.Clear(); return false; }
            _steps.Dequeue()();
            return true;
        }

        /// <summary>Reads hints for about <paramref name="budget"/>, at least one (Revit API context). False when done.</summary>
        public bool RunFor(TimeSpan budget)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do { if (!Next()) return false; } while (clock.Elapsed < budget);
            return _steps.Count > 0;
        }
    }

    /// <summary>
    /// The header fields now (quick), and the hints as steps to run with <see cref="Reading.Next"/>. Revit API context for
    /// this call and for each step; the model must stay open between them (a closed model ends the reading).
    /// </summary>
    public static Reading Plan(Document doc, ChecklistMath.Definition? def, UIApplication? slow = null)
    {
        var r = new Result { Title = doc.Title };
        var reading = new Reading(doc, r);
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
        bool SlowOff(string key) => key switch
        {
            "selfclash" => Rule("clashcategories").Length == 0,
            "modelscore" => Rule("modelscore").Length == 0,
            "standards" => Rule("standardtemplate").Length == 0,
            "etransmit" or "packageopens" => Rule("packagefolder").Length == 0,
            _ => false,
        };
        // The slow checks that run a Sketch ProMax tool need the model in front (the tools work on the active model).
        var inFront = slow?.ActiveUIDocument?.Document.Equals(doc) == true;
        Hint NotInFront() => new("Bring this model to the front in Revit, then run the slow checks.", Level.Info) { Pending = true };
        (ToolResult Result, List<IssueGateMath.Finding> Found)? modelCheck = null;
        (ToolResult Result, List<IssueGateMath.Finding> Found) ModelCheck()
        {
            if (modelCheck is { } done) return done;
            var tool = new CheckModelTool();
            var res = Drafting.Call(slow!, tool, new { });
            if (res.IsError) throw new InvalidOperationException(res.Text);
            modelCheck = (res, tool.Found ?? []);
            return modelCheck.Value;
        }
        var stamp = " Checked at " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + ".";
        // The e-Transmit package (folder or the newest ZIP in it), and its model opened from a clean folder for the slow checks.
        (Package? Package, string Why)? package = null;
        (Package? Package, string Why) Pkg() => package ??= Package.Find(Rule("packagefolder"), Regex.Replace(Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(file)), @"_S\d{2}_R\d{2}$", "", RegexOptions.IgnoreCase));
        Package.OpenResult? opened = null;
        Package.OpenResult? Opened() => opened ??= Pkg().Package?.Open(doc.Application);
        void CloseOpened() { opened?.Close(); opened = null; }

        void Add(string key, Func<Hint?> read)
        {
            if (!wanted.Contains(key)) return;
            if (slow is null && Slow.Contains(key))
            {
                if (!SlowOff(key)) r.Hints[key] = new Hint("A slower automatic check, not run yet: press Run the slow checks.", Level.Info) { Pending = true };
                return;
            }
            reading.Add(() =>
            {
                try { if (read() is { } h) r.Hints[key] = h; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { r.Hints[key] = new Hint("Couldn't read this from the model (" + ex.Message + ").", Level.Info); }
            });
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
        // Project parameters: the not-allowed names, and the empty ones that can go (ParameterCleanup), one Revit search
        // per parameter so a big model keeps Revit responsive between them.
        if (wanted.Contains("parameters"))
        {
            var all = new List<ParameterCleanup.Param>();
            try { all = ParameterCleanup.List(doc); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            foreach (var p in all)
                reading.Add(() =>
                {
                    try { ParameterCleanup.Fill(doc, p); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { p.Filled = true; } // can't tell: never offered for deleting
                });
            Add("parameters", () =>
            {
                var found = new List<string>();
                var bad = Rule("badparameters");
                var off = bad.Length == 0 ? [] : all.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Where(n => ChecklistMath.LikeAny(n, bad)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                found.AddRange(off.Select(n => n + " (matches " + bad + ")"));
                var empty = ParameterCleanup.Deletable(all, ParameterCleanup.InUse(doc), ParameterCleanup.KeepNames(def));
                found.AddRange(empty.Select(e => e.Line));
                if (found.Count == 0)
                    return bad.Length == 0
                        ? new Hint($"{all.Count} project parameters; none is empty or an empty duplicate. No not-allowed names are set" + Rules + ".", Level.Info)
                        : new Hint($"{all.Count} project parameters; none matches {bad}, none is empty or an empty duplicate.", Level.Ok);
                var parts = new List<string>();
                if (off.Count > 0) parts.Add($"{off.Count} match {bad}");
                if (empty.Count > 0) parts.Add($"{empty.Count} are empty on every element or an empty copy of a duplicate (Mark, Comments, the parameters your rules name and those schedules or filters use are kept)");
                return new Hint($"Of {all.Count} project parameters, " + string.Join("; ", parts) + ".", Level.Warn, found);
            });
        }
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

        // The submitted copy and the exports, found next to the model.
        Add("submission", () =>
        {
            var suffix = Rule("submission");
            if (suffix.Length == 0) return null;
            var name = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(file));
            var endings = ChecklistMath.RuleList(suffix);
            if (endings.Any(e => ChecklistMath.Like(name, "*" + e))) return new Hint($"This model is the submitted copy {name}.", Level.Ok);
            var folder = Folder(doc);
            if (folder.Length == 0) return new Hint("The model isn't saved on this PC, so the submitted copy can't be looked for.", Level.Info);
            var copies = Directory.EnumerateFiles(folder, name + "*.rvt").Select(f => Path.GetFileNameWithoutExtension(f) ?? "")
                .Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase) && n.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)).ToList();
            var right = copies.Where(n => endings.Any(e => ChecklistMath.Like(n, name + e))).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var wrong = copies.Except(right).Where(n => !Regex.IsMatch(n, @"\.\d{4}$")).ToList();
            if (right.Count == 0)
                return new Hint($"No copy named {name}{endings[0]} next to the model. Save the submitted copy as {ChecklistMath.SubmissionName(file)}.", Level.Warn,
                    wrong.Select(n => n + " (not named " + name + endings[0] + ")").ToList());
            return wrong.Count == 0 ? new Hint($"Submitted copy found: {List(right)}.", Level.Ok, right)
                : new Hint($"Submitted copy found ({List(right)}), but {wrong.Count} other copies aren't named {name}{endings[0]}.", Level.Warn, wrong);
        });
        // The NWC and IFC folders: the checking rule's, else the model's folder and the folders in it.
        var modelName = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(file));
        var stem = Regex.Replace(modelName, @"_S\d{2}_R\d{2}$", "", RegexOptions.IgnoreCase);
        List<string>? exportFolders = null;
        List<string> ExportFolders()
        {
            if (exportFolders is not null) return exportFolders;
            exportFolders = ChecklistMath.RuleList(Rule("exportfolder")).Select(f => f.Trim('"')).ToList();
            var home = Folder(doc);
            if (exportFolders.Count == 0 && home.Length > 0) exportFolders = [home, .. Directory.EnumerateDirectories(home).Take(40)];
            return exportFolders;
        }
        var newest = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? NewestExport(string ext) => newest.TryGetValue(ext, out var found) ? found
            : newest[ext] = ExportFolders().Where(Directory.Exists).SelectMany(f => Directory.EnumerateFiles(f, stem + "*" + ext)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

        Add("exportnames", () =>
        {
            var name = modelName;
            var home = Folder(doc);
            var folders = ExportFolders();
            if (folders.Count == 0) return new Hint("The model isn't saved on this PC, so its NWC and IFC files can't be looked for.", Level.Info);
            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            foreach (var f in folders.Append(home).Where(f => f.Length > 0 && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var rvt in Directory.EnumerateFiles(f, stem + "*.rvt")) models.Add(Path.GetFileNameWithoutExtension(rvt));
            var exports = folders.Where(Directory.Exists).SelectMany(f => Directory.EnumerateFiles(f, stem + "*.nwc").Concat(Directory.EnumerateFiles(f, stem + "*.ifc"))).ToList();
            if (exports.Count == 0) return new Hint($"No NWC or IFC file named {stem}… in {List(folders)}" + (Rule("exportfolder").Length == 0 ? " (set their folder in Checking rules)" : "") + ".", Level.Warn);
            var off = exports.Where(e => !models.Contains(Path.GetFileNameWithoutExtension(e))).Select(e => Path.GetFileName(e) + " (no model with this name)").ToList();
            foreach (var ext in new[] { ".nwc", ".ifc" })
                if (!exports.Any(e => e.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) off.Add($"no {ext.Substring(1).ToUpperInvariant()} file found");
            return off.Count == 0 ? new Hint($"{exports.Count} NWC and IFC files, each named exactly like its model.", Level.Ok, exports.Select(Path.GetFileName).Select(n => n ?? "").ToList())
                : new Hint($"{off.Count} problems with the NWC and IFC names.", Level.Warn, off);
        });

        // Model content.
        Add("familynames", () =>
        {
            var pattern = Rule("typenames");
            if (pattern.Length == 0) return null;
            var types = TypesInUse(doc);
            var off = types.Where(t => !ChecklistMath.LikeAny(t.Name, pattern)).Select(TypeLabel).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return off.Count == 0 ? new Hint($"All {types.Count} model types in use are named {pattern}.", Level.Ok)
                : new Hint($"{off.Count} of {types.Count} model types in use aren't named {pattern}.", Level.Warn, off);
        });
        Add("othertrades", () =>
        {
            var rule = Rule("othertrades");
            if (rule.Length == 0) return null;
            var found = new List<string>();
            var total = 0;
            void Count(string? label, IEnumerable<Element> elements)
            {
                var n = elements.Count();
                if (n == 0 || label is null) return;
                total += n;
                found.Add($"{label} (×{n})");
            }
            string what;
            if (rule.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var trade = Trade(parts.Discipline);
                if (trade.Length == 0) return null; // the discipline isn't known: you check it (or list the categories)
                foreach (var bic in OtherTrades(trade))
                    Count(Category.GetCategory(doc, bic)?.Name, new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements());
                if (trade == "STR")
                    Count((Category.GetCategory(doc, BuiltInCategory.OST_Walls)?.Name ?? "Walls") + " not marked Structural", new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                        .Where(w => w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 0));
                what = $"other trades' categories for a {parts.Discipline} model";
            }
            else
            {
                foreach (Category c in doc.Settings.Categories)
                    if (c.CategoryType == CategoryType.Model && ChecklistMath.LikeAny(c.Name, rule))
                        Count(c.Name, new FilteredElementCollector(doc).OfCategory((BuiltInCategory)c.Id.Value).WhereElementIsNotElementType().ToElements());
                what = rule;
            }
            return total == 0 ? new Hint($"No elements of {what}.", Level.Ok) : new Hint($"{total} elements of {what}.", Level.Warn, found);
        });

        // Drawings.
        Add("detaillines", () =>
        {
            if (!int.TryParse(Rule("detaillines"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit < 0) return null;
            var views = V().OnSheets.Where(v => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.Section or ViewType.Elevation).ToList();
            if (views.Count == 0) return new Hint("No plans, sections or elevations on sheets.", Level.Info);
            var busy = new List<string>();
            foreach (var v in views)
            {
                var lines = new FilteredElementCollector(doc).OwnedByView(v.Id).OfClass(typeof(CurveElement)).Count(e => e is DetailCurve);
                var regions = new FilteredElementCollector(doc).OwnedByView(v.Id).OfClass(typeof(FilledRegion)).GetElementCount();
                if (lines + regions > limit) busy.Add($"{v.Name}: {lines} detail lines, {regions} filled regions");
            }
            return busy.Count == 0 ? new Hint($"None of the {views.Count} plans, sections and elevations on sheets has more than {limit} detail lines and filled regions.", Level.Ok)
                : new Hint($"{busy.Count} views on sheets have more than {limit} detail lines and filled regions: look that none stands in for a real element.", Level.Warn, busy);
        });
        Add("legend", () =>
        {
            var rule = Rule("legendcategories");
            if (rule.Length == 0) return null;
            var cats = new HashSet<long>();
            foreach (Category c in doc.Settings.Categories) if (c.CategoryType == CategoryType.Model && ChecklistMath.LikeAny(c.Name, rule)) cats.Add(c.Id.Value);
            var types = TypesInUse(doc).Where(t => t.Category is { } c && cats.Contains(c.Id.Value)).ToList();
            if (types.Count == 0) return new Hint($"No types in use in {rule}.", Level.Info);
            var legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => v.ViewType == ViewType.Legend && !v.IsTemplate);
            var shown = new HashSet<long>(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LegendComponents).WhereElementIsNotElementType()
                .Select(e => e.get_Parameter(BuiltInParameter.LEGEND_COMPONENT)?.AsElementId()?.Value ?? -1));
            var missing = types.Where(t => !shown.Contains(t.Id.Value)).Select(TypeLabel).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (legends == 0) return new Hint($"The model has no legend; {types.Count} types in use need one.", Level.Warn, missing);
            return missing.Count == 0 ? new Hint($"All {types.Count} types in use in {rule} are shown in a legend.", Level.Ok)
                : new Hint($"{missing.Count} of {types.Count} types in use aren't shown in any legend.", Level.Warn, missing);
        });
        Add("schedules", () =>
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule && !v.IsInternalKeynoteSchedule && !v.Name.StartsWith("<", StringComparison.Ordinal)).ToList();
            var required = ChecklistMath.RuleList(Rule("schedules"));
            var problems = required.Where(p => !all.Any(v => ChecklistMath.Like(v.Name, p))).Select(p => $"no schedule named {p}").ToList();
            if (all.Count == 0) return new Hint("The model has no schedules.", Level.Warn, problems);
            var placed = new HashSet<long>(V().SchedulesOnSheets.Select(v => v.Id.Value));
            var loose = all.Where(v => !placed.Contains(v.Id.Value)).Select(v => v.Name + " (on no sheet)").OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            problems.AddRange(loose);
            return problems.Count == 0 ? new Hint($"All {all.Count} schedules are on sheets" + (required.Count > 0 ? ", and the ones you use are there." : "."), Level.Ok)
                : new Hint((required.Count > 0 && problems.Count > loose.Count ? "A schedule you use is missing. " : "") +
                           (loose.Count > 0 ? $"{loose.Count} of {all.Count} schedules are on no sheet: tick Schedules in e-Transmit so they stay, or place them on a sheet." : ""), Level.Warn, problems);
        });

        // Files from outside the model: the MIDP, the stage's drawing list, the clash report, the checklist, the exports.
        Add("midp", () =>
        {
            if (Rule("midp").Length == 0) return null;
            var midp = ChecklistMath.NewestFile(Rule("midp"), ".xlsx", ".xlsm", ".csv", ".txt");
            if (midp.Length == 0) return new Hint($"The MIDP {Rule("midp")} can't be found" + Rules + ".", Level.Warn);
            var cells = ChecklistMath.FileCells(midp);
            if (cells.Any(c => c.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0))
                return new Hint($"{stem} is in the MIDP {Path.GetFileName(midp)} ({File.GetLastWriteTime(midp):dd MMM yyyy}).", Level.Ok);
            var shape = ChecklistMath.Shape(stem);
            var near = cells.Where(c => ChecklistMath.Shape(c) == shape).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
            return new Hint($"{stem} isn't in the MIDP {Path.GetFileName(midp)}." + (near.Count > 0 ? " Model numbers shaped like it there:" : ""), Level.Warn, near);
        });
        Add("stagelist", () =>
        {
            if (Rule("stagelist").Length == 0) return null;
            var list = ChecklistMath.NewestFile(Rule("stagelist"), ".xlsx", ".xlsm", ".csv", ".txt");
            if (list.Length == 0) return new Hint($"The drawing list {Rule("stagelist")} can't be found" + Rules + ".", Level.Warn);
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(x => !x.IsPlaceholder).Select(x => x.SheetNumber).ToList();
            if (sheets.Count == 0) return new Hint("The model has no sheets.", Level.Warn);
            var (missing, extra) = ChecklistMath.CompareDrawingList(ChecklistMath.FileCells(list), sheets);
            var listed = missing.Count + sheets.Count - extra.Count;
            if (listed == 0) return new Hint($"No drawing number in {Path.GetFileName(list)} is shaped like this model's sheet numbers (e.g. {sheets[0]}).", Level.Warn);
            return missing.Count == 0
                ? new Hint($"All {listed} drawings in {Path.GetFileName(list)} are sheets in the model. Check the drawings it relies on (e.g. ARC backgrounds) are in the set too." +
                           (extra.Count > 0 ? $" {extra.Count} sheets aren't in the list:" : ""), Level.Ok, extra)
                : new Hint($"{missing.Count} of the {listed} drawings in {Path.GetFileName(list)} aren't sheets in the model.", Level.Warn, missing.Select(n => n + " (in the list, no sheet)").Concat(extra.Select(n => n + " (sheet, not in the list)")).ToList());
        });
        Add("federated", () =>
        {
            if (Rule("clashreport").Length == 0) return null;
            var report = ChecklistMath.NewestFile(Rule("clashreport"), ".xml", ".html", ".htm");
            if (report.Length == 0) return new Hint($"The clash report {Rule("clashreport")} can't be found" + Rules + ".", Level.Warn);
            var tests = ChecklistMath.ClashReport(File.ReadAllText(report));
            if (tests.Count == 0) return new Hint($"No clash results found in {Path.GetFileName(report)}. Export the report from Navisworks as XML or HTML (tabular).", Level.Warn);
            var ours = ChecklistMath.OurClashTests(tests, parts.Discipline);
            var open = ours.Sum(t => t.Open);
            var when = $" (report of {File.GetLastWriteTime(report):dd MMM yyyy HH:mm})";
            return open == 0 ? new Hint($"No open clashes in your {ours.Count} clash tests in {Path.GetFileName(report)}{when}.", Level.Ok, ours.Select(t => $"{t.Name}: {t.Closed} closed").ToList())
                : new Hint($"{open} clashes are still New, Active or Reviewed in your tests{when}.", Level.Warn, ours.Where(t => t.Open > 0).Select(t => $"{t.Name}: {t.Open} open").ToList());
        });
        Add("signed", () =>
        {
            var problems = new List<string>();
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Checklists");
            var made = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, ChecklistMath.SafeName(modelName) + " pre-submission checklist *").Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            var ready = made is not null && Path.GetFileNameWithoutExtension(made).EndsWith(" - READY", StringComparison.Ordinal);
            if (made is null) problems.Add("no signed checklist yet: answer every check, then Create the PDF on Review and sign");
            else if (!ready) problems.Add($"the latest checklist file, {Path.GetFileName(made)}, isn't ready (it says NOT READY)");
            var pattern = Rule("healthcheck");
            if (pattern.Length > 0)
            {
                var (pkg, _) = Pkg();
                var names = pkg?.Files.Select(f => Path.GetFileName(f)).ToList()
                            ?? (Folder(doc) is { Length: > 0 } home ? Directory.EnumerateFiles(home).Select(Path.GetFileName).Select(n => n ?? "").ToList() : []);
                var report = names.FirstOrDefault(n => ChecklistMath.LikeAny(n, pattern));
                if (report is null) problems.Add($"no Model Health Check report named {pattern} in {(pkg is null ? "the model's folder" : "the package")}");
            }
            return problems.Count == 0 ? new Hint($"Signed checklist: {Path.GetFileName(made)}." + (pattern.Length > 0 ? " The Health Check report is there." : ""), Level.Ok)
                : new Hint("Not signed off yet.", Level.Warn, problems);
        });
        Add("nwcsettings", () =>
        {
            if (NewestExport(".nwc") is not { } nwc) return new Hint($"No NWC named {stem}… yet. Use Export the NWC and IFC below.", Level.Warn);
            return ChecklistExports.MadeWith(nwc, ChecklistExports.NwcSettings)
                ? new Hint($"{Path.GetFileName(nwc)} was exported by Sketch ProMax with the office settings: {ChecklistExports.NwcSettings.Substring(ChecklistExports.NwcSettings.IndexOf(':') + 2)}.", Level.Ok)
                : new Hint($"{Path.GetFileName(nwc)} wasn't exported by Sketch ProMax with the office settings (or was written again since), and an NWC doesn't record its settings. Export it again with the button below.", Level.Warn);
        });
        Add("ifcsettings", () =>
        {
            if (Rule("ifcsetup").Trim('"') is { Length: > 0 } setup && !File.Exists(setup)) return new Hint($"The IFC setup file {setup} can't be found" + Rules + ".", Level.Warn);
            if (NewestExport(".ifc") is not { } ifc) return new Hint($"No IFC named {stem}… yet. Use Export the NWC and IFC below.", Level.Warn);
            var settings = ChecklistExports.IfcSettings(def);
            return ChecklistExports.MadeWith(ifc, settings)
                ? new Hint($"{Path.GetFileName(ifc)} was exported by Sketch ProMax with the office settings: {settings.Substring(settings.IndexOf(':') + 2)}.", Level.Ok)
                : new Hint($"{Path.GetFileName(ifc)} wasn't exported by Sketch ProMax with the office settings (or was written again since). Export it again with the button below.", Level.Warn);
        });
        Add("nwcdate", () =>
        {
            if (NewestExport(".nwc") is not { } nwc) return new Hint($"No NWC named {stem}… yet. Use Export the NWC and IFC below.", Level.Warn);
            var at = File.GetLastWriteTime(nwc);
            var problems = new List<string>();
            if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName) && File.GetLastWriteTime(doc.PathName) > at.AddMinutes(5))
                problems.Add($"the model was saved after it ({File.GetLastWriteTime(doc.PathName):ddd dd MMM HH:mm}): publish it again after the fixes");
            if (Enum.TryParse<DayOfWeek>(Rule("nwcday"), true, out var day) && at < ChecklistMath.LastDay(DateTime.Now, day))
                problems.Add($"it is from before this {day} ({ChecklistMath.LastDay(DateTime.Now, day):dd MMM})");
            return problems.Count == 0 ? new Hint($"{Path.GetFileName(nwc)} was published {at:ddd dd MMM HH:mm}, after the model's last save.", Level.Ok)
                : new Hint($"{Path.GetFileName(nwc)} was published {at:ddd dd MMM HH:mm}.", Level.Warn, problems);
        });

        // Sheets, Civil 3D and the package.
        Add("backgrounds", () =>
        {
            var shows = new List<string>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                if (doc.GetElement(vp.ViewId) is not View v || v.ViewType is ViewType.Legend or ViewType.DraftingView or ViewType.Schedule) continue;
                var links = new FilteredElementCollector(doc, v.Id).OfClass(typeof(RevitLinkInstance)).Select(l => l.Name.Split(':')[0].Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (links.Count > 0 && doc.GetElement(vp.SheetId) is ViewSheet sheet) shows.Add($"{sheet.SheetNumber} {sheet.Name}: {v.Name} shows {List(links)}");
            }
            return shows.Count == 0 ? new Hint("No view on a sheet shows a linked model, so unloading the links blanks nothing.", Level.Ok)
                : new Hint($"{shows.Count} views on sheets show a linked model as background: with the links unloaded it will be blank. Handle them as agreed.", Level.Warn, shows);
        });
        Add("civil3d", () => new Hint("This is a Revit model: this check is for Civil 3D only, so answer N/A.", Level.Ok));
        Add("packagecontents", () =>
        {
            var (pkg, why) = Pkg();
            if (pkg is null) return why.Length == 0 ? null : new Hint(why, Level.Warn);
            var missing = ChecklistMath.RuleList(Rule("packagecontents")).Where(e => !pkg.Files.Any(f => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))).Select(e => "no " + e + " file").ToList();
            return missing.Count == 0 ? new Hint($"{pkg.Name} has {pkg.Files.Count} files, with every type needed ({Rule("packagecontents")}). Check the federated NWD and clash report from the BIM team are in the same transmittal.", Level.Ok, pkg.Files.Take(60).ToList())
                : new Hint($"{pkg.Name} is missing {missing.Count} file types.", Level.Warn, missing);
        });

        // The slow checks: run on request (Run the slow checks).
        Add("duplicates", () =>
        {
            if (!inFront) return NotInFront();
            var (res, found) = ModelCheck();
            var dupes = found.Where(f => f.Check == "duplicates").Select(f => $"{f.What} (id {f.ElementId}): {f.Issue}").ToList();
            var overlaps = Counted(doc.GetWarnings().Select(w => w.GetDescriptionText())
                .Where(t => t.IndexOf("identical instances", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("overlap", StringComparison.OrdinalIgnoreCase) >= 0));
            var all = dupes.Concat(overlaps).ToList();
            return all.Count == 0 ? new Hint("No duplicate elements and no overlap warnings." + stamp, Level.Ok)
                : new Hint($"{dupes.Count} duplicate elements; {overlaps.Count} kinds of overlap warning." + stamp + ReportOf(res), Level.Warn, all);
        });
        Add("floating", () =>
        {
            if (!inFront) return NotInFront();
            var (res, found) = ModelCheck();
            var beams = found.Where(f => f.Check == "unsupported_beams").Select(f => $"{f.What} (id {f.ElementId}): {f.Issue}").ToList();
            var columns = FloatingColumns(doc);
            var all = beams.Concat(columns).ToList();
            return all.Count == 0 ? new Hint("Every beam end and column base sits on something." + stamp, Level.Ok)
                : new Hint($"{beams.Count} beam ends and {columns.Count} column bases sit on nothing in this model (supports in linked models aren't seen)." + stamp, Level.Warn, all);
        });
        Add("modelscore", () =>
        {
            if (!double.TryParse(Rule("modelscore").TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var least)) return null;
            if (!inFront) return NotInFront();
            var (res, _) = ModelCheck();
            using var json = JsonDocument.Parse(res.Text);
            var score = json.RootElement.GetProperty("overallScore").GetInt32();
            var low = json.RootElement.GetProperty("scores").EnumerateArray().Where(x => x.GetProperty("score").GetInt32() < 100)
                .Select(x => $"{x.GetProperty("check").GetString()}: {x.GetProperty("score").GetInt32()}% ({x.GetProperty("findings").GetInt32()} found)").ToList();
            return new Hint($"Sketch ProMax's model check scores {score}% (at least {least:0}% needed). Keep the Model Checker template's score in the QA/QC record too." + stamp + ReportOf(res),
                score >= least ? Level.Ok : Level.Warn, low);
        });
        Add("selfclash", () =>
        {
            var rule = Rule("clashcategories");
            List<string> cats;
            if (rule.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var trade = Trade(parts.Discipline);
                if (trade.Length == 0) return null;
                cats = ClashCategories(trade);
            }
            else cats = ChecklistMath.RuleList(rule);
            if (cats.Count == 0) return null;
            if (!inFront) return NotInFront();
            var res = Drafting.Call(slow!, new FindClashesTool(), new { set_a = new { source = "model", categories = cats }, limit = 500 });
            if (res.IsError) throw new InvalidOperationException(res.Text);
            using var json = JsonDocument.Parse(res.Text);
            var root = json.RootElement;
            var count = root.GetProperty("clashes").GetInt32();
            var note = root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? " " + n.GetString() : "";
            var pairs = root.GetProperty("byCategories").EnumerateArray().Select(x => $"{x.GetProperty("pair").GetString()}: {x.GetProperty("count").GetInt32()}").ToList();
            var sample = root.GetProperty("sample").EnumerateArray().Select(x => $"{x.GetProperty("a").GetString()} / {x.GetProperty("b").GetString()} ({x.GetProperty("kind").GetString()})").ToList();
            return count == 0 ? new Hint($"No clashes between {string.Join(", ", cats.Select(Pretty))}." + note + stamp, Level.Ok)
                : new Hint($"{count} clashes inside your trade." + note + stamp + ReportOf(res), Level.Warn, pairs.Concat(sample).ToList());
        });
        Add("standards", () =>
        {
            var path = Rule("standardtemplate").Trim('"');
            if (path.Length == 0) return null;
            if (!File.Exists(path)) return new Hint($"The standard template {path} can't be found" + Rules + ".", Level.Warn);
            var std = Standard(doc.Application, path);
            var templates = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
            var filters = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var missing = std.Templates.Where(t => !templates.Contains(t)).Select(t => "view template " + t + " is missing")
                .Concat(std.Filters.Where(f => !filters.Contains(f)).Select(f => "filter " + f + " is missing")).ToList();
            var browser = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc)?.Name ?? "";
            if (std.Browser.Length > 0 && !browser.Equals(std.Browser, StringComparison.OrdinalIgnoreCase)) missing.Insert(0, $"views are organised by {browser}, the standard uses {std.Browser}");
            return missing.Count == 0 ? new Hint($"Every view template, filter and the browser organisation of {Path.GetFileName(path)} are in the model." + stamp, Level.Ok)
                : new Hint($"{missing.Count} differences from {Path.GetFileName(path)}." + stamp, Level.Warn, missing);
        });
        Add("etransmit", () =>
        {
            var (pkg, why) = Pkg();
            if (pkg is null) return why.Length == 0 ? null : new Hint(why, Level.Warn);
            var problems = new List<string>();
            var rvts = pkg.Files.Where(f => f.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var other in rvts.Where(f => !f.Equals(pkg.Model, StringComparison.OrdinalIgnoreCase))) problems.Add($"{other}: a linked Revit model is in the package (untick linked Revit models)");
            var names = new HashSet<string>(pkg.Files.Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>())
                if (LinkFile(t) is { Length: > 0 } cad && !names.Contains(cad)) problems.Add($"{cad}: a linked CAD file isn't in the package");
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>())
                if (t.Source == ImageTypeSource.Link && Path.GetFileName(t.Path ?? "") is { Length: > 0 } img && !names.Contains(img)) problems.Add($"{img}: a linked image isn't in the package");
            if (Opened() is { Doc: { } packed })
            {
                var loose = UnusedViews(packed).Count;
                if (loose > 0) problems.Add($"the packaged model keeps {loose} views on no sheet (keep only views on sheets and the Navisworks view)");
                if (Unused(packed) is { } unused && unused > 0) problems.Add($"the packaged model has {unused} unused items (purge unused)");
            }
            else if (Opened() is { Error: { } error }) problems.Add("the packaged model couldn't be opened: " + error);
            return problems.Count == 0 ? new Hint($"{pkg.Name}: only your model, its CAD links and images, views on sheets and purged." + stamp, Level.Ok)
                : new Hint($"{pkg.Name} doesn't match the e-Transmit settings." + stamp, Level.Warn, problems);
        });
        Add("packageopens", () =>
        {
            var (pkg, why) = Pkg();
            if (pkg is null) return why.Length == 0 ? null : new Hint(why, Level.Warn);
            if (Opened() is not { Doc: { } packed }) return new Hint($"{pkg.Model} didn't open from a clean folder: {Opened()?.Error ?? "it isn't in the package"}." + stamp, Level.Warn);
            var problems = new List<string>();
            var startId = StartingViewSettings.GetStartingViewSettings(packed).ViewId;
            var start = startId == ElementId.InvalidElementId ? null : packed.GetElement(startId) as View;
            var family = Rule("landing").Length > 0 ? Rule("landing") : "LandingPage";
            if (start is null) problems.Add("no starting view is set");
            else if (!new FilteredElementCollector(packed, start.Id).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Any(f => ChecklistMath.Like(f.Symbol?.FamilyName ?? "", "*" + family + "*")))
                problems.Add($"the starting view {start.Name} doesn't show the {family} family");
            if (packed.IsWorkshared)
            {
                var have = new HashSet<string>(new FilteredWorksetCollector(packed).OfKind(WorksetKind.UserWorkset).Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var w in ChecklistMath.RuleList(Rule("linkworksets")).Where(w => !have.Contains(w))) problems.Add("missing workset " + w);
            }
            var views = new FilteredElementCollector(packed).OfClass(typeof(ViewSheet)).GetElementCount();
            return problems.Count == 0 ? new Hint($"{pkg.Model} opens from a clean folder on {start!.Name}, with {views} sheets and the worksets." + stamp, Level.Ok)
                : new Hint($"{pkg.Model} opens from a clean folder, but:" + stamp, Level.Warn, problems);
        });
        // The packaged model opened by the slow checks is closed as the last step.
        if (slow is not null) reading.Add(CloseOpened);
        return reading;
    }

    /// <summary>A linked CAD file's name, from its path.</summary>
    private static string LinkFile(CADLinkType t)
    {
        try
        {
            var reference = t.GetExternalFileReference();
            return reference is null ? "" : Path.GetFileName(ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath())) ?? "";
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { return ""; }
    }

    /// <summary>How many elements Purge Unused would remove (null where Revit can't tell: before Revit 2024).</summary>
    private static int? Unused(Document doc)
    {
        var method = typeof(Document).GetMethod("GetUnusedElements", [typeof(ISet<ElementId>)]);
        return method?.Invoke(doc, [new HashSet<ElementId>()]) is ICollection<ElementId> ids ? ids.Count : null;
    }

    /// <summary>The e-Transmit package: a folder, or the newest ZIP in it; its files (relative paths) and the model in it.</summary>
    private sealed class Package
    {
        public string Name { get; private init; } = "";
        public string Source { get; private init; } = "";
        public bool Zip { get; private init; }
        public List<string> Files { get; private init; } = [];
        public string Model { get; private init; } = "";

        /// <summary>The package from the rule (null with "" when the rule is off, null with why when it can't be read).</summary>
        public static (Package?, string) Find(string rule, string stem)
        {
            var path = rule.Trim().Trim('"');
            if (path.Length == 0) return (null, "");
            string? zip = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path
                : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.zip").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            List<string> files;
            if (zip is not null)
            {
                using var archive = ZipFile.OpenRead(zip);
                files = archive.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName.Replace('\\', '/')).ToList();
            }
            else if (Directory.Exists(path)) files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(f => f.Substring(path.TrimEnd('\\', '/').Length + 1).Replace('\\', '/')).ToList();
            else return (null, $"The package folder {path} can't be found (set in Checking rules).");
            var rvts = files.Where(f => f.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)).ToList();
            var model = rvts.FirstOrDefault(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase)) ?? (rvts.Count == 1 ? rvts[0] : "");
            return (new Package { Name = Path.GetFileName(zip ?? path.TrimEnd('\\', '/')), Source = zip ?? path, Zip = zip is not null, Files = files, Model = model }, "");
        }

        public sealed class OpenResult
        {
            public Document? Doc { get; init; }
            public string? Error { get; init; }
            public string? Temp { get; init; }

            public void Close()
            {
                try { if (Doc is { IsValidObject: true }) Doc.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                try { if (Temp is not null && Directory.Exists(Temp)) Directory.Delete(Temp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>Opens the packaged model from a clean folder (a ZIP is unpacked into a new temporary folder first), detached, worksets closed.</summary>
        public OpenResult Open(Autodesk.Revit.ApplicationServices.Application app)
        {
            if (Model.Length == 0) return new OpenResult { Error = "no model of this name is in the package" };
            string? temp = null;
            try
            {
                string path;
                if (Zip)
                {
                    temp = Path.Combine(Path.GetTempPath(), "SketchProMax-package-" + Guid.NewGuid().ToString("N"));
                    ZipFile.ExtractToDirectory(Source, temp);
                    path = Path.Combine(temp, Model.Replace('/', Path.DirectorySeparatorChar));
                }
                else path = Path.Combine(Source, Model.Replace('/', Path.DirectorySeparatorChar));
                var options = new OpenOptions { Audit = false };
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
                try { if (BasicFileInfo.Extract(path).IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                var doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), options);
                return new OpenResult { Doc = doc, Temp = temp };
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                var result = new OpenResult { Error = ex.Message, Temp = temp };
                result.Close();
                return result;
            }
        }
    }

    private static string Folder(Document doc)
    {
        try { return string.IsNullOrEmpty(doc.PathName) ? "" : Path.GetDirectoryName(doc.PathName) ?? ""; }
        catch (ArgumentException) { return ""; }
    }

    private static string ReportOf(ToolResult res)
    {
        try
        {
            using var json = JsonDocument.Parse(res.Text);
            return json.RootElement.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? " Full list: " + f.GetString() : "";
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>The trade a file name's discipline code belongs to: STR, ARC, MEP, or "" when it isn't known.</summary>
    internal static string Trade(string discipline) => (discipline ?? "").Trim().ToUpperInvariant() switch
    {
        "STR" or "ST" or "SS" or "SC" or "STRU" or "S" => "STR",
        "ARC" or "AR" or "ARCH" or "A" or "ID" => "ARC",
        "MEP" or "ME" or "MECH" or "M" or "EL" or "ELE" or "ELEC" or "E" or "PL" or "PLU" or "P" or "FP" or "FF" or "HVAC" => "MEP",
        _ => "",
    };

    private static readonly BuiltInCategory[] Arch =
    [
        BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureSystems, BuiltInCategory.OST_Casework,
        BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_CurtainWallPanels, BuiltInCategory.OST_CurtainWallMullions,
    ];
    private static readonly BuiltInCategory[] Mep =
    [
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
        BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingFixtures,
        BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers,
    ];
    private static readonly BuiltInCategory[] Str =
        [BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Rebar];

    /// <summary>The categories that belong to other trades in a model of this trade (an STR model's non-structural walls are added by the hint).</summary>
    internal static IEnumerable<BuiltInCategory> OtherTrades(string trade) => trade switch
    {
        "STR" => Arch.Concat(Mep),
        "ARC" => Mep.Concat(Str),
        "MEP" => Arch.Concat(Str).Concat([BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs]),
        _ => [],
    };

    /// <summary>The categories a trade's self-clash test runs between.</summary>
    internal static List<string> ClashCategories(string trade) => trade switch
    {
        "STR" => ["OST_StructuralColumns", "OST_StructuralFraming", "OST_StructuralFoundation", "OST_Floors", "OST_Walls"],
        "ARC" => ["OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_Stairs"],
        "MEP" => ["OST_DuctCurves", "OST_PipeCurves", "OST_CableTray", "OST_Conduit", "OST_MechanicalEquipment"],
        _ => [],
    };

    private static string Pretty(string category) => Regex.Replace(category.StartsWith("OST_", StringComparison.Ordinal) ? category.Substring(4) : category, "(?<=[a-z])(?=[A-Z])", " ");

    /// <summary>Structural columns whose base sits on nothing in this model: no foundation, floor, beam, wall or column under it.</summary>
    private static List<string> FloatingColumns(Document doc)
    {
        var reach = 50 / 304.8;
        var supports = new BoxIndex<Element>(10);
        var cats = new List<BuiltInCategory> { BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralColumns };
        foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
            if (e.get_BoundingBox(null) is { } b) supports.Add(e, b.Min, b.Max);
        var off = new List<string>();
        foreach (var c in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().OfType<FamilyInstance>())
        {
            if (c.get_BoundingBox(null) is not { } b) continue;
            var at = c.Location switch
            {
                LocationPoint lp => new XYZ(lp.Point.X, lp.Point.Y, b.Min.Z),
                LocationCurve lc => lc.Curve.GetEndPoint(0).Z <= lc.Curve.GetEndPoint(1).Z ? lc.Curve.GetEndPoint(0) : lc.Curve.GetEndPoint(1),
                _ => new XYZ((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2, b.Min.Z),
            };
            if (!supports.Overlapping(at, at, reach).Any(s => s.Id != c.Id)) off.Add($"{TypeName(c)} (id {c.Id.Value}): its base sits on nothing");
        }
        return off;
    }

    private sealed record StandardParts(List<string> Templates, List<string> Filters, string Browser);
    private static readonly Dictionary<string, (DateTime Stamp, StandardParts Parts)> Standards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The view templates, filters and browser organisation of the trade's standard file, opened in the background once per change of the file.</summary>
    private static StandardParts Standard(Autodesk.Revit.ApplicationServices.Application app, string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (Standards.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Parts;
        var open = app.Documents.Cast<Document>().FirstOrDefault(d => string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));
        var std = open;
        if (std is null)
        {
            var shared = false;
            try { shared = BasicFileInfo.Extract(path).IsWorkshared; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            std = shared
                ? app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets })
                : app.OpenDocumentFile(path);
        }
        try
        {
            var parts = new StandardParts(
                new FilteredElementCollector(std).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                new FilteredElementCollector(std).OfClass(typeof(ParameterFilterElement)).Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                BrowserOrganization.GetCurrentBrowserOrganizationForViews(std)?.Name ?? "");
            Standards[path] = (stamp, parts);
            return parts;
        }
        finally { if (open is null) std.Close(false); }
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
