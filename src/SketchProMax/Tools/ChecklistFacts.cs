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

    public sealed record Hint(string Text, Level Level);

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

        void Add(string key, Func<Hint?> read)
        {
            if (!wanted.Contains(key)) return;
            try { if (read() is { } h) r.Hints[key] = h; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { r.Hints[key] = new Hint("Couldn't read this from the model (" + ex.Message + ").", Level.Info); }
        }

        Add("name002", () => parts.Has002
            ? new Hint($"The file name {file} has 002 after ABC.", Level.Ok)
            : new Hint($"The file name {file} has no 002 after ABC.", Level.Warn));
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
            return new Hint($"This file is {mb:0} MB on disk now (check the e-Transmit output after purge).", mb > 300 ? Level.Warn : Level.Ok);
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
            var names = new FilteredElementCollector(doc).OfClass(typeof(Level)).Select(l => l.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var off = names.Where(n => !Regex.IsMatch(n, @"^BLDG\s*\d+\s+LVL\s+", RegexOptions.IgnoreCase)).ToList();
            return off.Count == 0
                ? new Hint($"{names.Count} levels, all named BLDG n LVL ….", Level.Ok)
                : new Hint($"{names.Count} levels; {off.Count} not named BLDG n LVL …: {List(off)}.", Level.Warn);
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
            var missing = Regex.Matches(Ex("worksets"), @"\bZ_Link_\w+").Cast<Match>().Select(m => m.Value).Distinct().Where(n => !names.Contains(n)).ToList();
            if (missing.Count > 0) problems.Add("missing " + List(missing));
            return problems.Count == 0
                ? new Hint($"{worksets.Count} worksets; nothing on Workset1, only levels and grids on Shared Levels and Grids, the link worksets are there.", Level.Ok)
                : new Hint(string.Join("; ", problems) + ".", Level.Warn);
        });
        Add("scopeboxes", () =>
        {
            var names = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().Select(e => e.Name).ToList();
            if (names.Count == 0) return new Hint("The model has no scope boxes.", Level.Warn);
            var off = names.Where(n => !Regex.IsMatch(n, @"^BLDG-\d{2}-\d{3,4}$")).ToList();
            return off.Count == 0 ? new Hint($"{names.Count} scope boxes, all named BLDG-NN-NNNN.", Level.Ok) : new Hint($"{off.Count} of {names.Count} scope boxes not named BLDG-NN-NNNN: {List(off)}.", Level.Warn);
        });
        Add("categories", () =>
        {
            var n = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType().GetElementCount();
            return n == 0 ? new Hint("No Generic Model elements.", Level.Ok) : new Hint($"{n} Generic Model elements.", Level.Warn);
        });
        Add("imports", () =>
        {
            var n = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Count(i => !i.IsLinked);
            return n == 0 ? new Hint("No imported CAD in the project (inside families isn't counted).", Level.Ok) : new Hint($"{n} imported CAD files in the project.", Level.Warn);
        });
        Add("groups", () =>
        {
            var groups = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_IOSModelGroups).WhereElementIsNotElementType().Cast<Autodesk.Revit.DB.Group>().ToList();
            var once = groups.GroupBy(g => g.GroupType?.Id.Value ?? -1).Count(g => g.Count() == 1);
            return groups.Count == 0 ? new Hint("No model groups.", Level.Ok) : new Hint($"{groups.Count} model groups; {once} group types are used only once.", once > 0 ? Level.Warn : Level.Info);
        });
        Add("warnings", () =>
        {
            var n = doc.GetWarnings().Count;
            return n == 0 ? new Hint("No warnings.", Level.Ok) : new Hint($"{n} warnings in the model.", Level.Warn);
        });
        Add("landing", () =>
        {
            var start = StartingViewSettings.GetStartingViewSettings(doc).ViewId;
            var view = start == ElementId.InvalidElementId ? null : doc.GetElement(start) as View;
            var landing = new FilteredElementCollector(doc).OfClass(typeof(Family)).Any(f => f.Name.IndexOf("LandingPage", StringComparison.OrdinalIgnoreCase) >= 0);
            var text = (view is null ? "No starting view is set." : $"Starting view: {view.Name}.") + (landing ? " The LandingPage family is loaded." : " No LandingPage family is loaded.");
            return new Hint(text, view is not null && landing ? Level.Ok : Level.Warn);
        });
        Add("titleblock", () =>
        {
            var expected = Regex.Match(Ex("titleblock"), @"([\w\-.]+)\.rfa", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "";
            var used = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
                .OfType<FamilyInstance>().Select(f => f.Symbol?.FamilyName ?? "").Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (used.Count == 0) return new Hint("No title blocks on sheets.", Level.Warn);
            var off = expected.Length == 0 ? new List<string>() : used.Where(n => !n.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToList();
            return off.Count == 0 ? new Hint("Title block on sheets: " + List(used) + ".", expected.Length > 0 ? Level.Ok : Level.Info)
                : new Hint("Other title blocks on sheets: " + List(off) + ".", Level.Warn);
        });
        Add("scales", () =>
        {
            var scales = V().OnSheets.Where(v => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.Section or ViewType.Elevation or ViewType.Detail or ViewType.AreaPlan)
                .GroupBy(v => v.Scale).OrderBy(g => g.Key).Select(g => (Scale: g.Key, Count: g.Count())).ToList();
            if (scales.Count == 0) return new Hint("No plans, sections or details on sheets.", Level.Info);
            var text = "Scales on sheets: " + string.Join(", ", scales.Select(s => $"1:{s.Scale} ({s.Count})")) + ".";
            return new Hint(text, scales.Any(s => s.Scale == 300) ? Level.Warn : Level.Info);
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
            var none = graphic.Count(v => v.ViewTemplateId == ElementId.InvalidElementId);
            var templates = graphic.Where(v => v.ViewTemplateId != ElementId.InvalidElementId).Select(v => v.ViewTemplateId.Value).Distinct().Count();
            return new Hint($"{none} of {graphic.Count} views on sheets have no view template; {templates} templates are used.", none > 0 || templates == 1 && graphic.Count > 3 ? Level.Warn : Level.Ok);
        });
        Add("unusedviews", () =>
        {
            var on = new HashSet<long>(V().OnSheets.Select(v => v.Id.Value));
            var unused = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted && v.ViewType is not (ViewType.DrawingSheet or ViewType.Schedule or ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined)
                            && !on.Contains(v.Id.Value) && v.Name.IndexOf("Navisworks", StringComparison.OrdinalIgnoreCase) < 0)
                .Count();
            return unused == 0 ? new Hint("Every view is on a sheet (or is the Navisworks view).", Level.Ok) : new Hint($"{unused} views are on no sheet. Check > Purge & cleanup can delete them.", Level.Warn);
        });
        Add("navisview", () =>
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate && v.Name.IndexOf("Navisworks", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (views.Count == 0) return new Hint("No 3D view named …Navisworks Export.", Level.Warn);
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
                : new Hint(detached + $"{loaded.Count} Revit links still loaded: {List(loaded)}.", Level.Warn);
        });
        return r;
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
