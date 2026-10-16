using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// "Export for Claude": one small zip that describes the open project (project data, quantities, drawings, schedules,
/// the latest saved check results, change tracking, worksets, links and this model's chat) so it can be uploaded to a Claude
/// chat to prepare a report. Read-only on the model. Every part is capped so the zip stays small on big models.
/// </summary>
public sealed class ExportProjectPackTool : IRevitTool
{
    public string Name => "export_project_pack";
    public string Description =>
        "Export for Claude: write ONE small zip (usually well under 3 MB, even on big models) describing the open project, " +
        "to upload to a Claude chat to prepare a report. Contains README.md (what each file is, units, model, versions), " +
        "project.json (project information, location and base points, levels, grids, phases, units), counts.csv and " +
        "counts_by_level.csv (element counts, volume, area, length by category, family, type and material), warnings.csv, " +
        "drawings.csv (sheets with current revision and issue status), views.csv, revisions.csv, schedules/ (up to 30 " +
        "schedules as CSV, sheet-placed first), checks/ (the newest saved results of the model check, clash, drawing, " +
        "rules, spec, BOQ, steel, rebar and precast tools), changes.json (snapshots and the latest change comparison), " +
        "worksets.csv, links.csv (Revit and CAD links with paths and load status), chat/ (this model's in-Revit chat) and " +
        "bugs/ (bug report zips on this PC not yet in the published bug log). " +
        "No images unless include_images. Read-only. Defaults to Documents\\SketchProMax\\packs.";
    public string PropertiesJson => """
    {
      "folder": {"type": "string", "description": "Where to write the zip (default Documents\\SketchProMax\\packs)."},
      "next_to_model": {"type": "boolean", "description": "Write the zip in the model's own folder instead (default false; ignored for cloud or unsaved models)."},
      "include_images": {"type": "boolean", "description": "Add small JPEG images of the active view and the first sheets (default false)."},
      "max_images": {"type": "integer", "description": "With include_images: how many sheet images, 0-12 (default 4)."},
      "include_chat": {"type": "boolean", "description": "Add this model's in-Revit chat, if it has one (default true)."},
      "include_bug_reports": {"type": "boolean", "description": "Add the bug reports not sent yet (Documents\\Sketch ProMax\\Bugs\\Reports; default true)."},
      "run_checks": {"type": "boolean", "description": "Run the model check first so checks/ has a fresh result (default false: only results already saved are packed)."},
      "max_schedules": {"type": "integer", "description": "How many schedules to export, 0-60 (default 30; sheet-placed schedules first)."},
      "max_schedule_rows": {"type": "integer", "description": "Rows kept per schedule, 10-5000 (default 500)."},
      "max_elements": {"type": "integer", "description": "Element cap for counts.csv, 1000-1000000 (default 300000)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args) => ToolResult.Ok(ProjectPack.Export(app, args));
}

internal static class ProjectPack
{
    private const int CheckFileCap = 300_000, ChecksTotalCap = 1_500_000;
    private const int ScheduleFileCap = 150_000, SchedulesTotalCap = 1_500_000;
    private const int ChatFileCap = 400_000;
    private const int BugReportsCap = 4_000_000, MaxBugReports = 10;

    /// <summary>Schedules with more body rows than this are not exported at all: Revit's export of them alone can take minutes.</summary>
    private const int HugeScheduleRows = 20_000;

    /// <summary>Where packs go when no folder is given.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax", "packs");

    /// <summary>The newest saved check results, by the file-name prefix the tools use ("&lt;title&gt; - Model check ...").</summary>
    private static readonly (string Kind, string[] Ext)[] CheckKinds =
    [
        ("Model check", [".csv", ".bcf"]),
        ("Clashes", [".csv", ".bcf"]),
        ("Missing items", [".csv"]),
        ("Rules check", [".csv"]),
        ("Spec check", [".csv"]),
        ("Drawing check", [".csv"]),
        ("Analytical check", [".csv"]),
        ("BOQ check", [".csv"]),
        ("Cost estimate", [".csv"]),
        ("Quantities", [".csv"]),
        ("Steel takeoff", [".csv"]),
        ("Bar schedule", [".csv"]),
        ("Precast schedule", [".csv"]),
        ("Tendon schedule", [".csv"]),
        ("Sleeve schedule", [".csv"]),
        ("Coordination progress", [".csv"]),
        ("Construction sequence", [".csv"]),
    ];

    private sealed class Pack
    {
        public readonly List<(string Name, byte[] Data, string What)> Files = [];
        public readonly List<string> Notes = [];
        public void Text(string name, string text, string what, bool bom = false) =>
            Files.Add((name, (bom ? new UTF8Encoding(true) : new UTF8Encoding(false)).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(), what));
        public void Json(string name, object value, string what) =>
            Text(name, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), what);
    }

    private static void Step(string text)
    {
        if (Jobs.CancelRequested) throw new OperationCanceledException("Stopped on request (cancel_job). No pack was written.");
        Jobs.Report("Export for Claude: " + text);
    }

    private static string Csv(string? s)
    {
        s ??= "";
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string Row(params object?[] cells) => string.Join(",", cells.Select(c => c switch
    {
        null => "",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Csv(c.ToString()),
    }));

    internal static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) || c is '#' or '%' ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length > 80 ? clean.Substring(0, 80).Trim() : clean.Length == 0 ? "unnamed" : clean;
    }

    private static string Kb(long bytes) => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";

    /// <summary>The model's name for files: the central model's for a workshared local copy (no "_username").</summary>
    public static string ModelName(Document doc)
    {
        try
        {
            if (doc.IsWorkshared && doc.GetWorksharingCentralModelPath() is { } central)
            {
                var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                var name = Path.GetFileNameWithoutExtension(visible);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        return doc.Title;
    }

    public static object Export(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument?.Document ?? throw new ArgumentException("Open a model first.");
        if (doc.IsFamilyDocument) throw new ArgumentException("This is a family; open a project model to export a pack.");
        var modelName = ModelName(doc);

        string folder;
        if (OutputPaths.Checked(args.OptString("folder")) is { Length: > 0 } f) folder = f;
        else if (args.OptBool("next_to_model") && !string.IsNullOrEmpty(doc.PathName) && Path.IsPathRooted(doc.PathName) && Path.GetDirectoryName(doc.PathName) is { Length: > 0 } dir) folder = dir;
        else folder = DefaultFolder;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { throw new ArgumentException($"Can't write to '{folder}': {ex.Message}"); }

        var maxSchedules = (int)Math.Max(0, Math.Min(60, args.OptLong("max_schedules") ?? 30));
        var maxRows = (int)Math.Max(10, Math.Min(5000, args.OptLong("max_schedule_rows") ?? 500));
        var maxElements = (int)Math.Max(1000, Math.Min(1_000_000, args.OptLong("max_elements") ?? 300_000));
        var started = DateTime.Now;
        var pack = new Pack();
        var temp = Path.Combine(Path.GetTempPath(), "spm-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string? checkRun = null;
            if (args.OptBool("run_checks"))
            {
                Step("running the model check");
                checkRun = RunModelCheck(app);
            }

            Step("project data");
            pack.Json("project.json", ProjectData(app, doc, modelName), "Project information, location and base points, levels, grids, phases and units.");
            Step("element counts");
            Counts(doc, pack, maxElements);
            Step("warnings");
            Warnings(doc, pack);
            Step("sheets and views");
            Drawings(doc, pack);
            Step("revisions");
            Revisions(doc, pack);
            Step("worksets and links");
            Worksets(doc, pack);
            Links(doc, pack);
            Schedules(doc, pack, temp, maxSchedules, maxRows);
            Step("saved check results");
            SavedChecks(doc, modelName, pack);
            if (checkRun is not null) pack.Notes.Add(checkRun);
            Step("change tracking");
            Changes(doc, modelName, pack);
            if (args.OptBool("include_chat", true)) Chat(doc, pack);
            if (args.OptBool("include_bug_reports", true)) BugReports(pack);
            if (args.OptBool("include_images")) Images(doc, app, pack, temp, (int)Math.Max(0, Math.Min(12, args.OptLong("max_images") ?? 4)));

            Step("writing the zip");
            var readme = Readme(app, doc, modelName, started, pack);
            var file = Path.Combine(folder, $"{Safe(modelName)} - Claude pack {started:yyyy-MM-dd HHmm}.zip");
            if (File.Exists(file)) file = Path.Combine(folder, $"{Safe(modelName)} - Claude pack {started:yyyy-MM-dd HHmmss}.zip");
            // Written beside the final name and moved into place, so a failed write never leaves a broken zip behind.
            var partial = file + ".partial";
            try
            {
                using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    void Add(string name, byte[] data)
                    {
                        var entry = zip.CreateEntry(name, name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                        using var s = entry.Open();
                        s.Write(data, 0, data.Length);
                    }
                    Add("README.md", Encoding.UTF8.GetBytes(readme));
                    foreach (var (name, data, _) in pack.Files) Add(name, data);
                }
                File.Move(partial, file);
            }
            catch
            {
                try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
            var size = new FileInfo(file).Length;

            // Short enough for a chat: top-level files one by one, folders summed.
            var top = pack.Files.Where(x => x.Name.IndexOf('/') < 0).Select(x => new { file = x.Name, size = Kb(x.Data.Length) }).ToList();
            var folders = pack.Files.Where(x => x.Name.IndexOf('/') >= 0).GroupBy(x => x.Name.Substring(0, x.Name.IndexOf('/') + 1))
                .Select(g => new { folder = g.Key, files = g.Count(), size = Kb(g.Sum(x => (long)x.Data.Length)) }).ToList();
            return new
            {
                file, zipSize = Kb(size), zipBytes = size, model = modelName,
                files = top, folders, notes = pack.Notes.Take(15).ToList(),
                took = $"{(int)(DateTime.Now - started).TotalSeconds} s",
                next = "Upload this zip to a Claude chat and ask for the report; README.md inside tells Claude what each file is.",
            };
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string? RunModelCheck(UIApplication app)
    {
        try
        {
            var result = new CheckModelTool().Execute(app, new ToolArgs(new Dictionary<string, JsonElement>()));
            return result.IsError ? "run_checks: the model check did not finish: " + result.Text : "run_checks: a fresh model check was run and is in checks/.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "run_checks: the model check failed: " + ex.Message; }
    }

    // ---------------------------------------------------------------- project.json

    private static string UnitLabel(Document doc, ForgeTypeId spec)
    {
        try { return LabelUtils.GetLabelForUnit(doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId()); }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { return "?"; }
    }

    private static double Conv(Document doc, ForgeTypeId spec, double internalValue) =>
        Math.Round(UnitUtils.ConvertFromInternalUnits(internalValue, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId()), 3);

    private static object ProjectData(UIApplication app, Document doc, string modelName)
    {
        double L(double feet) => RevitHelpers.FromInternal(doc, feet);
        object Point(XYZ p) => new { x = L(p.X), y = L(p.Y), z = L(p.Z) };

        var info = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.ProjectInformation is { } pi)
            foreach (Parameter p in pi.Parameters)
            {
                var name = p.Definition?.Name;
                if (string.IsNullOrEmpty(name) || !p.HasValue) continue;
                string? value;
                try { value = RevitHelpers.ParamValue(p); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { value = null; }
                if (!string.IsNullOrWhiteSpace(value)) info[name!] = value!;
            }

        object? site = null;
        try
        {
            var s = doc.SiteLocation;
            site = new { place = s.PlaceName, latitude = Math.Round(s.Latitude * 180 / Math.PI, 6), longitude = Math.Round(s.Longitude * 180 / Math.PI, 6), timeZone = s.TimeZone };
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or NullReferenceException) { }

        object? position = null;
        try
        {
            var loc = doc.ActiveProjectLocation;
            var pp = loc.GetProjectPosition(XYZ.Zero);
            position = new
            {
                location = loc.Name, eastWest = L(pp.EastWest), northSouth = L(pp.NorthSouth), elevation = L(pp.Elevation),
                angleToTrueNorthDegrees = Math.Round(pp.Angle * 180 / Math.PI, 4),
            };
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or NullReferenceException) { }

        object? BasePointData(BasePoint? bp) => bp is null ? null : new { internalPosition = Point(bp.Position), sharedPosition = Point(bp.SharedPosition) };
        object? pbp = null, sp = null;
        try { pbp = BasePointData(BasePoint.GetProjectBasePoint(doc)); sp = BasePointData(BasePoint.GetSurveyPoint(doc)); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }

        var levelBase = doc.GetElement(Elevations.DefaultLevelTypeId(doc))?.get_Parameter(BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE)?.AsInteger() == 1 ? "Survey Point" : "Project Base Point";
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).Select(l => new
        {
            id = l.Id.Value, name = l.Name,
            elevation = Elevations.Displayed(doc, l),
            internalZ = L(l.ProjectElevation),
            elevationBase = doc.GetElement(l.GetTypeId())?.get_Parameter(BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE)?.AsInteger() == 1 ? "Survey Point" : "Project Base Point",
            structural = l.get_Parameter(BuiltInParameter.LEVEL_IS_STRUCTURAL)?.AsInteger() == 1,
            buildingStory = l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() == 1,
        }).ToList();

        var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Take(1000).Select(g =>
        {
            Curve? c = null;
            try { c = g.Curve; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            return new
            {
                name = g.Name, curved = g.IsCurved,
                start = c is { IsBound: true } ? new[] { L(c.GetEndPoint(0).X), L(c.GetEndPoint(0).Y) } : null,
                end = c is { IsBound: true } ? new[] { L(c.GetEndPoint(1).X), L(c.GetEndPoint(1).Y) } : null,
                length = c is null ? (double?)null : L(c.Length),
            };
        }).ToList();

        var phases = new List<string>();
        foreach (Phase ph in doc.Phases) phases.Add(ph.Name);

        string? central = null;
        try { if (doc.IsWorkshared && doc.GetWorksharingCentralModelPath() is { } cp) central = ModelPathUtils.ConvertModelPathToUserVisiblePath(cp); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }

        return new
        {
            model = new
            {
                name = modelName, title = doc.Title, path = doc.PathName, centralModel = central, workshared = doc.IsWorkshared,
                revit = $"{app.Application.VersionName} ({app.Application.VersionBuild})",
                addIn = "Sketch ProMax " + Update.Updater.CurrentText, exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            },
            units = new
            {
                length = UnitLabel(doc, SpecTypeId.Length), area = UnitLabel(doc, SpecTypeId.Area), volume = UnitLabel(doc, SpecTypeId.Volume),
                angle = UnitLabel(doc, SpecTypeId.Angle),
                note = "Every length, coordinate and elevation in this pack is in the length unit above; areas and volumes in the units above.",
            },
            projectInformation = info,
            site, projectPosition = position,
            projectBasePoint = pbp, surveyPoint = sp,
            levelElevationBase = new
            {
                defaultLevelType = levelBase, baseInternalZ = L(Elevations.BaseZ(doc)),
                note = "elevation = what Revit displays for the level (relative to its type's elevation base); internalZ = Z in Revit's internal coordinates.",
            },
            levels, grids, phases,
        };
    }

    // ---------------------------------------------------------------- counts.csv

    private sealed class Tally { public int Count; public double Volume, Area, Length; }

    private static void Counts(Document doc, Pack pack, int maxElements)
    {
        var rows = new Dictionary<string, Tally>();
        var byLevel = new Dictionary<string, Tally>();
        var types = new Dictionary<long, (string Family, string Type)>();
        var levelNames = new FilteredElementCollector(doc).OfClass(typeof(Level)).ToDictionary(l => l.Id.Value, l => l.Name);
        var materials = new Dictionary<long, string>();
        var categories = doc.Settings.Categories.Cast<Category>().Where(c => c.CategoryType == CategoryType.Model).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int seen = 0, catIndex = 0, catsWithElements = 0;
        var capped = false;

        foreach (var cat in categories)
        {
            catIndex++;
            if (seen >= maxElements) { capped = true; break; }
            FilteredElementCollector collector;
            try { collector = new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType(); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { continue; }
            if (collector.GetElementCount() == 0) continue;
            catsWithElements++;
            Step($"counting {cat.Name} ({catIndex} of {categories.Count} categories, {seen} elements so far)");
            foreach (var e in collector)
            {
                if (++seen % 500 == 0 && Jobs.CancelRequested) throw new OperationCanceledException("Stopped on request (cancel_job). No pack was written.");
                if (seen > maxElements) { capped = true; break; }
                if (e.ViewSpecific) continue;
                var typeId = e.GetTypeId();
                if (!types.TryGetValue(typeId.Value, out var ft))
                {
                    var t = typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId) as ElementType;
                    ft = (t?.FamilyName ?? "", t?.Name ?? "");
                    types[typeId.Value] = ft;
                }
                var matId = e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsElementId()
                            ?? (typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId)?.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsElementId());
                var material = "";
                if (matId is { } mid && mid != ElementId.InvalidElementId)
                {
                    if (materials.TryGetValue(mid.Value, out var known)) material = known;
                    else materials[mid.Value] = material = doc.GetElement(mid)?.Name ?? "";
                }
                double Read(BuiltInParameter bip) => e.get_Parameter(bip) is { HasValue: true, StorageType: StorageType.Double } p ? p.AsDouble() : 0;
                var volume = Read(BuiltInParameter.HOST_VOLUME_COMPUTED);
                var area = Read(BuiltInParameter.HOST_AREA_COMPUTED);
                var length = Read(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (length == 0) length = Read(BuiltInParameter.INSTANCE_LENGTH_PARAM);

                var key = string.Join("\u0001", cat.Name, ft.Family, ft.Type, material);
                if (!rows.TryGetValue(key, out var tally)) rows[key] = tally = new Tally();
                tally.Count++; tally.Volume += volume; tally.Area += area; tally.Length += length;

                var levelId = e.LevelId;
                if (levelId == ElementId.InvalidElementId) levelId = e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                if (levelId == ElementId.InvalidElementId) levelId = e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                var level = levelNames.TryGetValue(levelId.Value, out var ln) ? ln : "(no level)";
                var lkey = cat.Name + "\u0001" + level;
                if (!byLevel.TryGetValue(lkey, out var lt)) byLevel[lkey] = lt = new Tally();
                lt.Count++; lt.Volume += volume; lt.Area += area; lt.Length += length;
            }
        }

        string vU = UnitLabel(doc, SpecTypeId.Volume), aU = UnitLabel(doc, SpecTypeId.Area), lU = UnitLabel(doc, SpecTypeId.Length);
        double V(double x) => Conv(doc, SpecTypeId.Volume, x);
        double A(double x) => Conv(doc, SpecTypeId.Area, x);
        double Len(double x) => Conv(doc, SpecTypeId.Length, x);
        var sb = new StringBuilder(Row("Category", "Family", "Type", "Structural material", "Count", $"Volume ({vU})", $"Area ({aU})", $"Length ({lU})") + "\n");
        foreach (var kv in rows.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var k = kv.Key.Split('\u0001');
            sb.AppendLine(Row(k[0], k[1], k[2], k[3], kv.Value.Count, V(kv.Value.Volume), A(kv.Value.Area), Len(kv.Value.Length)));
        }
        pack.Text("counts.csv", sb.ToString(), $"{rows.Count} rows: model elements by category, family, type and structural material ({Math.Min(seen, maxElements)} elements in {catsWithElements} categories).", bom: true);
        var sl = new StringBuilder(Row("Category", "Level", "Count", $"Volume ({vU})", $"Area ({aU})", $"Length ({lU})") + "\n");
        foreach (var kv in byLevel.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var k = kv.Key.Split('\u0001');
            sl.AppendLine(Row(k[0], k[1], kv.Value.Count, V(kv.Value.Volume), A(kv.Value.Area), Len(kv.Value.Length)));
        }
        pack.Text("counts_by_level.csv", sl.ToString(), $"{byLevel.Count} rows: the same elements by category and level.", bom: true);
        if (capped) pack.Notes.Add($"counts.csv stopped at {maxElements} elements (max_elements); categories after '{categories[Math.Min(catIndex, categories.Count) - 1].Name}' are not counted.");
    }

    private static void Warnings(Document doc, Pack pack)
    {
        IList<FailureMessage> warnings;
        try { warnings = doc.GetWarnings(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return; }
        var sb = new StringBuilder("Warning,Count,Example element ids\n");
        foreach (var g in warnings.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()))
            sb.AppendLine(Row(g.Key, g.Count(), string.Join(" ", g.SelectMany(w => w.GetFailingElements()).Take(10).Select(i => i.Value.ToString(CultureInfo.InvariantCulture)))));
        pack.Text("warnings.csv", sb.ToString(), $"Revit warnings grouped by text ({warnings.Count} in total).", bom: true);
    }

    // ---------------------------------------------------------------- drawings.csv, views.csv, revisions.csv

    private static void Drawings(Document doc, Pack pack)
    {
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
        var nameOf = views.ToDictionary(v => v.Id.Value, v => v.Name);
        var sheets = views.OfType<ViewSheet>().OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder("Id,Sheet number,Sheet name,Current revision,Revision date,Revision description,Issued,Views placed,View names,Placeholder\n");
        foreach (var s in sheets)
        {
            Revision? rev = null;
            try { rev = doc.GetElement(s.GetCurrentRevision()) as Revision; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            var placed = s.GetAllPlacedViews().ToList();
            var names = string.Join("; ", placed.Take(12).Select(id => nameOf.TryGetValue(id.Value, out var n) ? n : id.Value.ToString(CultureInfo.InvariantCulture))) + (placed.Count > 12 ? "; …" : "");
            sb.AppendLine(Row(s.Id.Value, s.SheetNumber, s.Name,
                s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? rev?.RevisionNumber,
                s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION_DATE)?.AsString() ?? rev?.RevisionDate,
                s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION_DESCRIPTION)?.AsString() ?? rev?.Description,
                rev is null ? "" : rev.Issued ? "Yes" : "No", placed.Count, names, s.IsPlaceholder ? "Yes" : ""));
        }
        pack.Text("drawings.csv", sb.ToString(), $"{sheets.Count} sheets: number, name, current revision and date, issued, views placed.", bom: true);

        var skip = new HashSet<ViewType> { ViewType.ProjectBrowser, ViewType.SystemBrowser, ViewType.Internal, ViewType.Undefined, ViewType.DrawingSheet };
        var list = views.Where(v => !v.IsTemplate && !skip.Contains(v.ViewType) && !(v is ViewSchedule vs && (vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule)))
            .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        const int cap = 6000;
        var sv = new StringBuilder("Id,View type,Name,Scale,Sheet number,Sheet name,Level,Dependency,View template\n");
        foreach (var v in list.Take(cap))
        {
            int? scale = null;
            try { if (v.ViewType is not (ViewType.Schedule or ViewType.ColumnSchedule or ViewType.PanelSchedule)) scale = v.Scale; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            string dependency;
            var primary = v.GetPrimaryViewId();
            if (primary != ElementId.InvalidElementId) dependency = "Dependent of " + (nameOf.TryGetValue(primary.Value, out var pn) ? pn : primary.Value.ToString(CultureInfo.InvariantCulture));
            else dependency = v.GetDependentViewIds().Count is var n and > 0 ? $"Primary ({n} dependent)" : "";
            var template = v.ViewTemplateId != ElementId.InvalidElementId && nameOf.TryGetValue(v.ViewTemplateId.Value, out var tn) ? tn : "";
            sv.AppendLine(Row(v.Id.Value, v.ViewType.ToString(), v.Name, scale,
                v.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER)?.AsString(), v.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NAME)?.AsString(),
                v.GenLevel?.Name, dependency, template));
        }
        pack.Text("views.csv", sv.ToString(), $"{Math.Min(cap, list.Count)} views (templates left out): type, name, scale, sheet, level, dependent or primary, template.", bom: true);
        if (list.Count > cap) pack.Notes.Add($"views.csv lists the first {cap} of {list.Count} views.");
    }

    private static void Revisions(Document doc, Pack pack)
    {
        var onSheets = new Dictionary<long, int>();
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
        {
            IList<ElementId> ids;
            try { ids = s.GetAllRevisionIds(); } catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            foreach (var id in ids) onSheets[id.Value] = (onSheets.TryGetValue(id.Value, out var n) ? n : 0) + 1;
        }
        var sb = new StringBuilder("Sequence,Number,Date,Description,Issued,Issued to,Issued by,Visibility,Sheets\n");
        var count = 0;
        foreach (var id in Revision.GetAllRevisionIds(doc))
        {
            if (doc.GetElement(id) is not Revision r) continue;
            count++;
            sb.AppendLine(Row(r.SequenceNumber, r.RevisionNumber, r.RevisionDate, r.Description, r.Issued ? "Yes" : "No", r.IssuedTo, r.IssuedBy,
                r.Visibility.ToString(), onSheets.TryGetValue(id.Value, out var n) ? n : 0));
        }
        pack.Text("revisions.csv", sb.ToString(), $"{count} revisions in sequence order, with the number of sheets each is on.", bom: true);
    }

    // ---------------------------------------------------------------- worksets.csv, links.csv

    private static void Worksets(Document doc, Pack pack)
    {
        if (!doc.IsWorkshared)
        {
            pack.Text("worksets.csv", "Note\nThis model is not workshared.\n", "Not workshared.", bom: true);
            return;
        }
        var sb = new StringBuilder("Id,Workset,Open,Editable,Owner,Default,Elements\n");
        var n = 0;
        foreach (var ws in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
        {
            n++;
            int elements;
            try { elements = new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(ws.Id)).WhereElementIsNotElementType().GetElementCount(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { elements = -1; }
            sb.AppendLine(Row(ws.Id.IntegerValue, ws.Name, ws.IsOpen ? "Yes" : "No", ws.IsEditable ? "Yes" : "No", ws.Owner, ws.IsDefaultWorkset ? "Yes" : "", elements));
        }
        pack.Text("worksets.csv", sb.ToString(), $"{n} user worksets: open, editable, owner, element count.", bom: true);
    }

    private static (string Path, string Status, string PathType) Reference(ExternalFileReference? r)
    {
        if (r is null) return ("", "", "");
        string path;
        try { path = ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetAbsolutePath()); }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
        {
            try { path = ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetPath()); }
            catch (Exception ex2) when (ex2 is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { path = ""; }
        }
        string status;
        try { status = r.GetLinkedFileStatus().ToString(); } catch (Autodesk.Revit.Exceptions.ApplicationException) { status = "?"; }
        return (path, status, r.PathType.ToString());
    }

    private static void Links(Document doc, Pack pack)
    {
        var sb = new StringBuilder("Kind,Name,Path,Load status,Path type,Attachment,Instances,View-specific instances,Nested\n");
        var revitInstances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .GroupBy(i => i.GetTypeId().Value).ToDictionary(g => g.Key, g => g.Count());
        int revit = 0, cad = 0;
        foreach (var lt in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            revit++;
            ExternalFileReference? r = null;
            try { r = lt.GetExternalFileReference(); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            var (path, status, type) = Reference(r);
            if (status is "" or "?") status = RevitLinkType.IsLoaded(doc, lt.Id) ? "Loaded" : "Not loaded";
            sb.AppendLine(Row("Revit link", lt.Name, path, status, type, lt.AttachmentType.ToString(),
                revitInstances.TryGetValue(lt.Id.Value, out var n) ? n : 0, "", lt.IsNestedLink ? "Yes" : ""));
        }
        var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var mine = imports.Where(i => i.GetTypeId() == t.Id).ToList();
            var linked = t.IsExternalFileReference();
            ExternalFileReference? r = null;
            if (linked) try { r = t.GetExternalFileReference(); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            var (path, status, type) = Reference(r);
            cad++;
            sb.AppendLine(Row(linked ? "CAD link" : "CAD import", t.Name, linked ? path : "(imported into the model)", linked ? status : "Imported", type, "",
                mine.Count, mine.Count(i => i.ViewSpecific), ""));
        }
        pack.Text("links.csv", sb.ToString(), $"{revit} Revit links and {cad} CAD links/imports with paths and load status.", bom: true);
    }

    // ---------------------------------------------------------------- schedules/

    private static void Schedules(Document doc, Pack pack, string temp, int max, int maxRows)
    {
        if (max == 0) return;
        var placed = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Select(s => s.ScheduleId.Value));
        var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
            .OrderBy(s => placed.Contains(s.Id.Value) ? 0 : 1).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var dir = Path.Combine(temp, "schedules");
        Directory.CreateDirectory(dir);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int done = 0, failed = 0;
        var huge = new List<string>();
        var index = new StringBuilder("File,Schedule,On a sheet,Rows exported,Rows in schedule,Truncated\n");
        foreach (var s in all)
        {
            if (done >= max || total >= SchedulesTotalCap) break;
            // Counted before exporting: a schedule of every element in a big model would stall the pack for minutes.
            int bodyRows;
            try { bodyRows = s.GetTableData().GetSectionData(SectionType.Body)?.NumberOfRows ?? 0; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { bodyRows = 0; }
            if (bodyRows > HugeScheduleRows)
            {
                huge.Add($"{s.Name} ({bodyRows:N0} rows)");
                index.AppendLine(Row("", s.Name, placed.Contains(s.Id.Value) ? "Yes" : "No", 0, bodyRows, "Skipped: too many rows"));
                continue;
            }
            Step($"schedule {done + 1} of {Math.Min(max, all.Count)}: {s.Name}");
            var name = Safe(s.Name);
            for (var i = 2; !used.Add(name); i++) name = Safe(s.Name) + " " + i;
            try
            {
                var file = name + ".csv";
                s.Export(dir, file, new ViewScheduleExportOptions { FieldDelimiter = ",", TextQualifier = ExportTextQualifier.DoubleQuote });
                var path = Path.Combine(dir, file);
                if (!File.Exists(path)) { failed++; continue; }
                var keep = new StringBuilder();
                var kept = 0;
                var more = false;
                // One line past the cap is read, only to know whether anything was left out.
                foreach (var line in File.ReadLines(path).Take(maxRows + 1))
                {
                    if (kept >= maxRows || keep.Length + line.Length > ScheduleFileCap) { more = true; break; }
                    keep.AppendLine(line);
                    kept++;
                }
                if (more) keep.AppendLine("\"… more rows not included (Export for Claude caps each schedule)\"");
                pack.Text($"schedules/{name}.csv", keep.ToString(), "Schedule", bom: true);
                total += keep.Length;
                done++;
                index.AppendLine(Row(name + ".csv", s.Name, placed.Contains(s.Id.Value) ? "Yes" : "No", kept, bodyRows, more ? "Yes" : ""));
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException or UnauthorizedAccessException or ArgumentException) { failed++; }
        }
        if (done > 0 || huge.Count > 0) pack.Text("schedules/_index.csv", index.ToString(), "Which schedule each file is.", bom: true);
        if (huge.Count > 0)
            pack.Notes.Add($"schedules/: {huge.Count} schedule{(huge.Count == 1 ? "" : "s")} with more than {HugeScheduleRows:N0} rows left out (too big to export quickly): " +
                           string.Join("; ", huge.Take(10)) + (huge.Count > 10 ? "; …" : "") + ". Ask the user for a filtered version if one matters.");
        if (all.Count > done) pack.Notes.Add($"schedules/: {done} of {all.Count} schedules exported (sheet-placed first; max_schedules {max})" + (failed > 0 ? $", {failed} could not be exported." : "."));
    }

    // ---------------------------------------------------------------- checks/

    private static IEnumerable<string> SearchFolders()
    {
        var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        if (Directory.Exists(docs)) yield return docs;
    }

    private static IEnumerable<string> Prefixes(Document doc, string modelName) =>
        new[] { doc.Title, modelName }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static void SavedChecks(Document doc, string modelName, Pack pack)
    {
        var files = new List<FileInfo>();
        foreach (var folder in SearchFolders())
            try { files.AddRange(new DirectoryInfo(folder).GetFiles()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var prefixes = Prefixes(doc, modelName).ToList();
        long total = 0;
        var index = new StringBuilder("Check,File,Saved,Size,Included\n");
        var found = 0;
        foreach (var (kind, exts) in CheckKinds)
        {
            foreach (var ext in exts)
            {
                var newest = files.Where(f => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase)
                                              && prefixes.Any(p => f.Name.StartsWith(p + " - " + kind + " ", StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (newest is null) continue;
                found++;
                string included;
                if (total >= ChecksTotalCap) included = "no (pack size cap)";
                else if (ext == ".bcf")
                {
                    var summary = BcfSummary(newest.FullName);
                    pack.Text($"checks/{Safe(Path.GetFileNameWithoutExtension(newest.Name))} (BCF summary).md", summary, "BCF summary");
                    total += summary.Length;
                    included = "summary of topics";
                }
                else
                {
                    var (text, cut) = ReadCapped(newest.FullName, CheckFileCap, keepTail: false);
                    pack.Text($"checks/{Safe(Path.GetFileNameWithoutExtension(newest.Name))}{ext}", text, kind, bom: true);
                    total += text.Length;
                    included = cut ? "first part (file is large)" : "yes";
                }
                index.AppendLine(Row(kind, newest.Name, newest.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Kb(newest.Length), included));
            }
        }
        if (found == 0)
            pack.Text("checks/_index.csv", "Note\nNo saved check results were found for this model in Documents\\SketchProMax. Run the Model check button (or ask Claude to run check_model / find_clashes) and export again; or export with run_checks.\n", "No saved check results.", bom: true);
        else
            pack.Text("checks/_index.csv", index.ToString(), "Which saved check result each file is, and when it was saved.", bom: true);
    }

    /// <summary>A text file's first (or last) part, cut at a line end.</summary>
    private static (string Text, bool Cut) ReadCapped(string path, int cap, bool keepTail)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ($"(could not read: {ex.Message})", false); }
        if (text.Length <= cap) return (text, false);
        if (keepTail)
        {
            var tail = text.Substring(text.Length - cap);
            var nl = tail.IndexOf('\n');
            return ("… (earlier part left out to keep the pack small)\n" + (nl >= 0 ? tail.Substring(nl + 1) : tail), true);
        }
        var head = text.Substring(0, cap);
        var last = head.LastIndexOf('\n');
        var lines = text.Count(c => c == '\n');
        return ((last > 0 ? head.Substring(0, last + 1) : head) + $"\"… file cut to keep the pack small ({lines} lines in the full file)\"\n", true);
    }

    private static string BcfSummary(string path)
    {
        var sb = new StringBuilder($"# BCF summary: {Path.GetFileName(path)}\n\n");
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var markups = zip.Entries.Where(e => e.FullName.EndsWith("markup.bcf", StringComparison.OrdinalIgnoreCase)).ToList();
            sb.AppendLine($"{markups.Count} topic(s).\n");
            foreach (var m in markups.Take(300))
            {
                using var s = m.Open();
                var xml = System.Xml.Linq.XDocument.Load(s);
                string? Val(string name) => xml.Descendants().FirstOrDefault(d => d.Name.LocalName == name)?.Value;
                var desc = Val("Description") ?? "";
                if (desc.Length > 300) desc = desc.Substring(0, 300) + "…";
                sb.AppendLine($"- **{Val("Title")}**" + (Val("TopicStatus") is { Length: > 0 } st ? $" ({st})" : "") + (desc.Length > 0 ? ": " + desc.Replace("\n", " / ") : ""));
            }
            if (markups.Count > 300) sb.AppendLine($"- … {markups.Count - 300} more");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            sb.AppendLine($"(could not read the BCF: {ex.Message})");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- changes.json

    private static void Changes(Document doc, string modelName, Pack pack)
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");
        var key = Checks.ModelKey(doc);
        var snapshots = new List<object>();
        var snapRoot = Path.Combine(appData, "snapshots", key);
        try
        {
            if (Directory.Exists(snapRoot))
                foreach (var source in Directory.GetDirectories(snapRoot))
                    foreach (var f in new DirectoryInfo(source).GetFiles("*.json").OrderBy(f => f.LastWriteTimeUtc))
                        snapshots.Add(new { source = Path.GetFileName(source), name = Path.GetFileNameWithoutExtension(f.Name), saved = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), size = Kb(f.Length) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        object? latest = null;
        var prefixes = Prefixes(doc, modelName).ToList();
        var change = SearchFolders().SelectMany(d =>
            {
                try { return new DirectoryInfo(d).GetFiles("*.csv"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new FileInfo[0]; }
            })
            .Where(f => prefixes.Any(p => f.Name.StartsWith(p + " - Changes ", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (change is not null)
        {
            var (text, _) = ReadCapped(change.FullName, 5_000_000, keepTail: false);
            var rows = text.Split('\n').Skip(1).Where(l => l.Trim().Length > 0).Select(SplitCsv).Where(r => r.Count >= 6).ToList();
            latest = new
            {
                file = change.Name, saved = change.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                added = rows.Count(r => r[0] == "added"), deleted = rows.Count(r => r[0] == "deleted"), modified = rows.Count(r => r[0] == "modified"),
                byCategory = rows.GroupBy(r => r[2]).Select(g => new { category = g.Key, added = g.Count(r => r[0] == "added"), deleted = g.Count(r => r[0] == "deleted"), modified = g.Count(r => r[0] == "modified") })
                    .OrderByDescending(g => g.added + g.deleted + g.modified).Take(30).ToList(),
                sampleModified = rows.Where(r => r[0] == "modified").Take(40).Select(r => new { id = r[1], category = r[2], type = r[3], level = r[4], what = r[5] }).ToList(),
            };
        }

        var coordination = new List<object>();
        var store = Path.Combine(appData, "coordination", key + ".json");
        try
        {
            if (File.Exists(store))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(store));
                foreach (var test in json.RootElement.EnumerateObject())
                    coordination.Add(new
                    {
                        test = test.Name,
                        runs = test.Value.EnumerateArray().Reverse().Take(10).Reverse().Select(r => new
                        {
                            date = r.TryGetProperty("Date", out var d) ? d.GetString() : null,
                            clashes = r.TryGetProperty("Keys", out var k) && k.ValueKind == JsonValueKind.Array ? k.GetArrayLength() : 0,
                        }).ToList(),
                    });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }

        pack.Json("changes.json", new
        {
            note = snapshots.Count == 0 && latest is null && coordination.Count == 0
                ? "No change tracking saved for this model yet. Ask Claude to 'take a model snapshot' (model_snapshot) now and compare later, or record clash tests with coordination_progress."
                : "Change tracking from model_snapshot (snapshots, latest comparison) and coordination_progress (clash counts per run).",
            snapshots = snapshots.Count > 40 ? snapshots.Skip(snapshots.Count - 40).ToList() : snapshots,
            latestComparison = latest,
            coordination,
        }, "Change tracking: saved snapshots, the latest comparison and clash-test history.");
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else if (c != '\r') sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    // ---------------------------------------------------------------- chat/, images/

    private static void Chat(Document doc, Pack pack)
    {
        // Only this model's conversation, as the chat pane keeps it (same key), never other projects' chats.
        Step("this model's chat");
        var conv = SketchProMax.Chat.ChatStore.Load(SketchProMax.Chat.ChatPane.ModelKey(doc), doc.Title);
        if (conv.Items.Count == 0) return;
        pack.Text("chat/conversation.md", RenderChat(conv), "This model's in-Revit chat, oldest first.");
    }

    /// <summary>A saved in-Revit chat as readable Markdown, tool calls and errors included; a long one keeps its end.</summary>
    internal static string RenderChat(SketchProMax.Chat.Conversation conv)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# In-Revit chat: {conv.Title}");
        if (conv.Updated != default) sb.AppendLine($"Last updated {conv.Updated.ToLocalTime():yyyy-MM-dd HH:mm}");
        foreach (var item in conv.Items)
        {
            sb.AppendLine();
            switch (item.Kind)
            {
                case "user":
                    sb.AppendLine("## User");
                    if (item.Name.Length > 0) sb.AppendLine($"(attached: {item.Name})");
                    sb.AppendLine(item.Text);
                    break;
                case "assistant":
                    sb.AppendLine("## Claude");
                    sb.AppendLine(item.Text);
                    break;
                case "tool":
                    sb.AppendLine($"- Tool {item.Name}: {(item.State.Length > 0 ? item.State : "run")}" + (item.Text.Length > 0 ? $" ({item.Text})" : ""));
                    break;
                default:
                    sb.AppendLine($"_{item.Kind}: {item.Text}_");
                    break;
            }
        }
        var text = sb.ToString();
        if (text.Length > ChatFileCap)
        {
            var tail = text.Substring(text.Length - ChatFileCap);
            var nl = tail.IndexOf('\n');
            text = "… (earlier part left out to keep the pack small)\n" + (nl >= 0 ? tail.Substring(nl + 1) : tail);
        }
        return text;
    }

    /// <summary>The bug report zips not sent yet (see BugLog), newest first, as they are.</summary>
    private static void BugReports(Pack pack)
    {
        Step("bug reports not sent yet");
        var unsent = SketchProMax.Bugs.BugLog.UnsentReports();
        long total = 0;
        var added = 0;
        foreach (var report in unsent)
        {
            if (added >= MaxBugReports) { pack.Notes.Add($"{unsent.Count - added} older bug report(s) left out; send them from Documents\\Sketch ProMax\\Bugs\\Reports."); break; }
            byte[] data;
            try { data = File.ReadAllBytes(report.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { pack.Notes.Add($"Bug report {Path.GetFileName(report.Path)} could not be read: {ex.Message}"); continue; }
            if (total + data.Length > BugReportsCap) { pack.Notes.Add($"Bug report {Path.GetFileName(report.Path)} left out to keep the pack small."); continue; }
            total += data.Length;
            added++;
            pack.Files.Add(($"bugs/{Path.GetFileName(report.Path)}", data, report.Description));
        }
    }

    private static void Images(Document doc, UIApplication app, Pack pack, string temp, int maxSheets)
    {
        var views = new List<View>();
        if (app.ActiveUIDocument.ActiveView is { } active && active.CanBePrinted && active is not ViewSchedule) views.Add(active);
        views.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder && s.Id != views.FirstOrDefault()?.Id)
            .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).Take(maxSheets));
        var dir = Path.Combine(temp, "images");
        Directory.CreateDirectory(dir);
        var n = 0;
        foreach (var v in views)
        {
            Step($"image {++n} of {views.Count}");
            var stem = Path.Combine(dir, "img" + n);
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews, FilePath = stem, ZoomType = ZoomFitType.FitToPage, PixelSize = 1400,
                HLRandWFViewsFileType = ImageFileType.JPEGMedium, ShadowViewsFileType = ImageFileType.JPEGMedium,
            };
            options.SetViewsAndSheets([v.Id]);
            try { doc.ExportImage(options); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { pack.Notes.Add($"Image of '{v.Name}' failed: {ex.Message}"); continue; }
            var file = Directory.GetFiles(dir, "img" + n + "*").FirstOrDefault();
            if (file is null) continue;
            var label = v is ViewSheet s ? $"{s.SheetNumber} - {s.Name}" : v.Name;
            pack.Files.Add(($"images/{Safe(label)}.jpg", File.ReadAllBytes(file), "Image"));
        }
    }

    // ---------------------------------------------------------------- README.md

    private static string Readme(UIApplication app, Document doc, string modelName, DateTime started, Pack pack)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Claude pack: {modelName}");
        sb.AppendLine();
        sb.AppendLine($"- Model: {modelName}" + (doc.Title != modelName ? $" (open as {doc.Title})" : ""));
        sb.AppendLine($"- Path: {(string.IsNullOrEmpty(doc.PathName) ? "(not saved)" : doc.PathName)}" + (doc.IsWorkshared ? " (workshared)" : ""));
        sb.AppendLine($"- Revit: {app.Application.VersionName} ({app.Application.VersionBuild})");
        sb.AppendLine($"- Add-in: Sketch ProMax {Update.Updater.CurrentText}");
        sb.AppendLine($"- Exported: {started:yyyy-MM-dd HH:mm} (local time)");
        sb.AppendLine($"- Units: length {UnitLabel(doc, SpecTypeId.Length)}, area {UnitLabel(doc, SpecTypeId.Area)}, volume {UnitLabel(doc, SpecTypeId.Volume)}, angle {UnitLabel(doc, SpecTypeId.Angle)} (the project's own units; CSV numbers use a dot as decimal separator)");
        sb.AppendLine();
        sb.AppendLine("## How to use this pack (for Claude)");
        sb.AppendLine();
        sb.AppendLine("This zip was exported from the Revit model by the Sketch ProMax add-in so you can write a report on the project without access to Revit. " +
                      "Start with project.json (project information, levels, grids, coordinates) and counts.csv (what is in the model and how much of it), then " +
                      "drawings.csv / revisions.csv for the drawing register and issue status, and checks/ for the latest quality, clash and code-check results " +
                      "(check each result's date in checks/_index.csv: they are the newest saved runs, not necessarily from today). changes.json says what changed " +
                      "between model snapshots. Quantities are as modelled (net, no waste) in the units above; empty quantity cells mean Revit has no computed value " +
                      "for that category. Files cut to keep the pack small say so on their last line. If something is missing, ask the user to run the matching " +
                      "Sketch ProMax tool in Revit and export again.");
        sb.AppendLine();
        sb.AppendLine("## Files");
        sb.AppendLine();
        sb.AppendLine("| File | What it is | Size |");
        sb.AppendLine("|---|---|---|");
        foreach (var g in pack.Files.GroupBy(f => f.Name.IndexOf('/') is var i and > 0 && !f.Name.EndsWith("_index.csv", StringComparison.Ordinal) ? f.Name.Substring(0, i + 1) : f.Name))
        {
            var what = g.Key.EndsWith("/", StringComparison.Ordinal)
                ? g.Key switch
                {
                    "schedules/" => $"{g.Count()} schedules exported from Revit as CSV (sheet-placed first; long ones cut). See schedules/_index.csv.",
                    "checks/" => $"{g.Count()} newest saved check results (CSV, BCF as topic summaries). See checks/_index.csv.",
                    "chat/" => "This model's in-Revit chat as readable text (a long one keeps its end).",
                    "images/" => $"{g.Count()} small JPEG images (active view and first sheets).",
                    "bugs/" => $"{g.Count()} Sketch ProMax bug report zips not sent yet (each: report.md, description, screenshot, last tool calls, assemblies). " +
                               "For the add-in's developer, not about the project.",
                    _ => $"{g.Count()} files",
                }
                : g.First().What;
            sb.AppendLine($"| {g.Key} | {what.Replace("|", "/")} | {Kb(g.Sum(f => (long)f.Data.Length))} |");
        }
        if (pack.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine();
            foreach (var n in pack.Notes) sb.AppendLine("- " + n);
        }
        return sb.ToString();
    }
}
