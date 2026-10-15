using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;
using SketchProMax.Settings;

namespace SketchProMax.Tools;

/// <summary>
/// Where legends to start from are found. Revit's API has no way to create a legend view from nothing (there is no
/// ViewLegend.Create); it can only duplicate an existing legend, or copy one in from another document.
/// </summary>
internal static class LegendSeeds
{
    /// <summary>The seed written by save_legend_seed: a tiny project holding one empty legend.</summary>
    public static string SavedSeedPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "legend-seed.rvt");

    private const string MarkerSuffix = ".sketchpromax-seed";

    /// <summary>True when save_legend_seed wrote <paramref name="path"/>, so it may be replaced without asking.</summary>
    public static bool IsOurSeed(string path)
    {
        if (File.Exists(path + MarkerSuffix)) return true;
        var ourFolder = Path.GetDirectoryName(SavedSeedPath)!;
        var name = Path.GetFileName(path);
        return string.Equals(Path.GetFullPath(Path.GetDirectoryName(path) ?? ""), Path.GetFullPath(ourFolder), StringComparison.OrdinalIgnoreCase)
               && name.StartsWith("legend-seed", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Leaves a small marker next to a seed this tool wrote, so a later save may replace it.</summary>
    public static void MarkOurSeed(string path)
    {
        try { File.WriteAllText(path + MarkerSuffix, "Written by Sketch ProMax save_legend_seed; it may replace the .rvt next to this file.\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static List<View> Legends(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend).ToList();

    /// <summary>
    /// Opens a seed file in the background (detached and with worksets closed when it is workshared), or returns the
    /// already-open copy. <paramref name="opened"/> says whether the caller must close it. Null with a reason on failure.
    /// </summary>
    public static Document? Open(Autodesk.Revit.ApplicationServices.Application app, string path, out bool opened, out string? why)
    {
        opened = false;
        why = null;
        if (!File.Exists(path)) { why = "file not found"; return null; }
        var open = app.Documents.Cast<Document>()
            .FirstOrDefault(d => !d.IsLinked && string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));
        if (open != null) return open;

        var options = new OpenOptions { Audit = false };
        try
        {
            var info = BasicFileInfo.Extract(path);
            if (int.TryParse(info.Format, out var fileYear) && int.TryParse(app.VersionNumber, out var revitYear) && fileYear > revitYear)
            {
                why = $"saved in Revit {fileYear}; Revit {revitYear} can't open it";
                return null;
            }
            if (info.IsWorkshared)
            {
                options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        try
        {
            var doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), options);
            opened = true;
            return doc;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException or UnauthorizedAccessException)
        {
            why = ex.Message.Trim();
            return null;
        }
    }

    public sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }

    public static CopyPasteOptions CopyOptions()
    {
        var options = new CopyPasteOptions();
        options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
        return options;
    }

    /// <summary>Copies a legend view from another document into <paramref name="target"/>; open transaction required.</summary>
    public static View CopyAcross(View legend, Document target, bool withContents)
    {
        var options = CopyOptions();
        var ids = ElementTransformUtils.CopyElements(legend.Document, [legend.Id], target, Transform.Identity, options);
        var copy = ids.Select(target.GetElement).OfType<View>().FirstOrDefault()
                   ?? throw new InvalidOperationException("Revit copied the legend but returned no view.");
        if (withContents)
        {
            // The view comes across empty; its legend components, text and lines follow with a view-to-view copy.
            var contents = new FilteredElementCollector(legend.Document, legend.Id).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.OwnerViewId == legend.Id).Select(e => e.Id).ToList();
            if (contents.Count > 0) ElementTransformUtils.CopyElements(legend, contents, copy, Transform.Identity, options);
        }
        return copy;
    }
}

public sealed class CreateLegendTool : IRevitTool
{
    public string Name => "create_legend";
    public string Description =>
        "Create a new, empty legend view (or a copy of an existing legend with its contents). Revit's API can't make a legend " +
        "from nothing, only duplicate one, so this looks for a legend to start from, in order: this model; any other open " +
        "model; the firm seed file set in Settings (a .rvt/.rte with a legend, opened in the background and closed again); the " +
        "seed saved by save_legend_seed. When none exists it says the one manual step needed (View > Legends > Legend once). " +
        "Legend components themselves can't be placed through the API; add text, lines and filled regions with the other " +
        "tools, or copy a legend that already has the components (with_contents).";
    public string PropertiesJson => """
    {
      "name": {"type": "string", "description": "New legend name (default 'Legend'; made unique)."},
      "scale": {"type": "integer", "description": "View scale denominator, e.g. 50 for 1:50 (default: the source legend's)."},
      "source_legend": {"type": "string", "description": "Name of the legend to start from (in this model, or another open one). Default: any."},
      "with_contents": {"type": "boolean", "description": "Copy the source legend's contents too (default false: an empty legend)."},
      "seed_path": {"type": "string", "description": "A .rvt or .rte file with a legend to use when this model has none (overrides the Settings seed for this call)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (doc.IsFamilyDocument) return ToolResult.Error("Legends live in projects; switch to a project model.");
        var wanted = args.OptString("source_legend")?.Trim();
        var withContents = args.OptBool("with_contents");
        var name = string.IsNullOrWhiteSpace(args.OptString("name")) ? "Legend" : args.OptString("name")!.Trim();
        var scale = args.OptLong("scale");
        if (scale is < 1 or > 24000) throw new ArgumentException("scale must be between 1 and 24000.");
        var tried = new List<string>();

        View? Pick(List<View> legends) => string.IsNullOrEmpty(wanted)
            ? legends.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : legends.FirstOrDefault(v => string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase))
              ?? (legends.Where(v => v.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).Take(2).ToList() is { Count: 1 } one ? one[0] : null);

        // 1. This model.
        var local = Pick(LegendSeeds.Legends(doc));
        if (local != null)
        {
            if (TryFinish(doc, () => Duplicate(doc, local, withContents), $"duplicated legend '{local.Name}' in this model", name, scale, tried) is { } done) return done;
        }
        else tried.Add(LegendSeeds.Legends(doc).Count == 0 ? "this model: no legends" : $"this model: no legend matches '{wanted}'");

        // 2. Other open models.
        foreach (var other in app.Application.Documents.Cast<Document>().Where(d => !d.Equals(doc) && !d.IsFamilyDocument && !d.IsLinked))
        {
            var legend = Pick(LegendSeeds.Legends(other));
            if (legend is null) { tried.Add($"open model '{other.Title}': no {(string.IsNullOrEmpty(wanted) ? "legends" : $"legend matching '{wanted}'")}"); continue; }
            var result = TryFinish(doc, () => LegendSeeds.CopyAcross(legend, doc, withContents), $"copied legend '{legend.Name}' from the open model '{other.Title}'", name, scale, tried);
            if (result != null) return result;
        }

        // 3. The firm seed (argument, else Settings), then 4. the seed saved by save_legend_seed.
        var seeds = new List<(string Path, string Label)>();
        var firm = args.OptString("seed_path") ?? AgentSettings.Load().LegendSeedPath;
        if (!string.IsNullOrWhiteSpace(firm)) seeds.Add((Environment.ExpandEnvironmentVariables(firm!.Trim().Trim('"')), "firm seed"));
        else tried.Add("firm seed: none set (Settings > legend seed file)");
        seeds.Add((LegendSeeds.SavedSeedPath, "saved seed"));
        foreach (var (path, label) in seeds)
        {
            var seed = LegendSeeds.Open(app.Application, path, out var opened, out var why);
            if (seed is null) { tried.Add($"{label} {path}: {why}"); continue; }
            try
            {
                var legend = Pick(LegendSeeds.Legends(seed)) ?? LegendSeeds.Legends(seed).FirstOrDefault();
                if (legend is null) { tried.Add($"{label} {path}: has no legend"); continue; }
                var result = TryFinish(doc, () => LegendSeeds.CopyAcross(legend, doc, withContents), $"copied legend '{legend.Name}' from the {label} {path}", name, scale, tried);
                if (result != null) return result;
            }
            finally { if (opened) seed.Close(false); }
        }

        return ToolResult.Error(
            "No legend to start from. Revit's API can't create a legend from nothing (API limit), only duplicate one. One manual step fixes it for good: " +
            "in Revit click View > Legends > Legend once (any name and scale), then ask again; or open any project that has a legend and run " +
            "save_legend_seed there, so every later project can start from it. Looked in: " + string.Join("; ", tried) + ".");
    }

    private static View Duplicate(Document doc, View legend, bool withContents)
    {
        var option = withContents ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate;
        if (!legend.CanViewBeDuplicated(option)) option = ViewDuplicateOption.WithDetailing;
        var view = doc.GetElement(legend.Duplicate(option)) as View ?? throw new InvalidOperationException("Revit did not duplicate the legend.");
        if (!withContents && option == ViewDuplicateOption.WithDetailing)
        {
            var contents = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.OwnerViewId == view.Id).Select(e => e.Id).ToList();
            if (contents.Count > 0) doc.Delete(contents);
        }
        return view;
    }

    private static ToolResult Finish(Document doc, Func<View> make, string how, string name, long? scale)
    {
        using var t = RevitHelpers.StartTransaction(doc, "Create legend");
        var view = make();
        var finalName = ViewKit.Finish(doc, view, name, scale, null);
        t.Commit();
        return ToolResult.Ok(new
        {
            id = view.Id.Value, name = finalName, scale = view.Scale, source = how,
            next = "Place it with place_legend_on_sheets. Legend components can't be placed through the API; add them by hand (Annotate > Component > Legend Component) or fill it with text, detail lines and filled regions.",
        });
    }

    private static ToolResult? TryFinish(Document doc, Func<View> make, string how, string name, long? scale, List<string> tried)
    {
        try { return Finish(doc, make, how, name, scale); }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            tried.Add($"{how}: failed ({ex.Message.Trim()})");
            return null;
        }
    }
}

public sealed class SaveLegendSeedTool : IRevitTool
{
    public string Name => "save_legend_seed";
    public string Description =>
        "Save a legend seed so create_legend works in projects that have no legend: writes a tiny project holding one empty " +
        "legend to %LOCALAPPDATA%\\SketchProMax\\legend-seed.rvt, copied from a legend in this model (or another open one). " +
        "Run once in any project that has a legend. The open model is not changed. The seed opens in this Revit version and newer. " +
        "An existing file at path that this tool did not write is left alone unless overwrite is true.";
    public string PropertiesJson => """
    {
      "legend": {"type": "string", "description": "Legend to copy (only its view, not its contents). Default: any legend."},
      "path": {"type": "string", "description": "Where to write the seed (default %LOCALAPPDATA%\\SketchProMax\\legend-seed.rvt)."},
      "overwrite": {"type": "boolean", "description": "Replace an existing .rvt at path that this tool did not write. Default false: such a file is left alone and the call fails."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var wanted = args.OptString("legend")?.Trim();
        var candidates = new[] { doc }.Concat(app.Application.Documents.Cast<Document>().Where(d => !d.Equals(doc) && !d.IsFamilyDocument && !d.IsLinked))
            .SelectMany(LegendSeeds.Legends).ToList();
        var legend = string.IsNullOrEmpty(wanted)
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(v => string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"No legend named '{wanted}'. Legends: {string.Join(", ", candidates.Select(v => v.Name).Distinct().Take(50))}");
        if (legend is null)
            return ToolResult.Error("No open model has a legend. Make one by hand once (View > Legends > Legend), then run save_legend_seed again.");

        var path = Environment.ExpandEnvironmentVariables((args.OptString("path") ?? LegendSeeds.SavedSeedPath).Trim().Trim('"'));
        if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("path must end in .rvt.");
        if (app.Application.Documents.Cast<Document>().Any(d => string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase)))
            return ToolResult.Error($"{path} is open in Revit; close it first.");
        // Never replace someone's model by accident: an existing file is only overwritten when this tool wrote it (its
        // marker file is next to it, or it is a legend-seed*.rvt in the Sketch ProMax folder) or overwrite is passed.
        if (File.Exists(path) && !LegendSeeds.IsOurSeed(path) && !args.OptBool("overwrite"))
            return ToolResult.Error($"{path} already exists and was not written by save_legend_seed, so it was left alone. " +
                                    "Pick another path, or pass overwrite: true to replace it.");
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var backupPattern = Path.GetFileNameWithoutExtension(path) + ".0*.rvt";
        var backupsBefore = new HashSet<string>(Directory.GetFiles(folder, backupPattern), StringComparer.OrdinalIgnoreCase);

        var seed = app.Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            using (var t = new Transaction(seed, "Legend seed"))
            {
                t.Start();
                var copy = LegendSeeds.CopyAcross(legend, seed, withContents: false);
                try { copy.Name = "Legend Seed"; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                t.Commit();
            }
            seed.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
        }
        finally { seed.Close(false); }
        LegendSeeds.MarkOurSeed(path);
        // SaveAs leaves a backup next to it (legend-seed.0001.rvt); it is not needed. Backups that were there before
        // this call belong to someone else and are kept.
        foreach (var backup in Directory.GetFiles(folder, backupPattern).Where(b => !backupsBefore.Contains(b)))
            try { File.Delete(backup); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return ToolResult.Ok(new
        {
            saved = path, from = $"'{legend.Name}' in '{legend.Document.Title}'", revit = app.Application.VersionNumber,
            note = $"create_legend now works in any project without legends. The seed opens in Revit {app.Application.VersionNumber} and newer; " +
                   "to share it with the firm, copy it to a shared folder and set that path as the legend seed in Settings.",
        });
    }
}

/// <summary>
/// In-place families have no loadable family behind them, so tools that read or rebuild family geometry, or swap types,
/// skip them. This lists them, shows them, and gives the manual steps for what the API can't do on them.
/// </summary>
public sealed class InPlaceGuideTool : IRevitTool
{
    public string Name => "in_place_guide";
    public string Description =>
        "Find in-place elements (in-place families) by category and level, optionally select them or isolate them in a view, " +
        "and get step-by-step manual instructions for what the API can't do with them: cut, join, quantity (volumes/areas), " +
        "or convert to a loadable family. Read-only unless select/isolate is asked for (a new 3D view asks for approval).";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["list", "cut", "join", "quantity", "convert"], "description": "Which manual steps to return (default list: just the elements)."},
      "category": {"type": "string", "description": "Only this category, e.g. 'Generic Models', 'Walls', 'Floors'."},
      "level": {"type": "string", "description": "Only elements on this level (name)."},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Only these elements."},
      "select": {"type": "boolean", "description": "Select the found elements in Revit."},
      "isolate": {"type": "string", "enum": ["none", "temporary", "new_3d_view"], "description": "temporary: Temporary Isolate in the active view (Reset with the sunglasses icon). new_3d_view: a new 3D view with a section box around them."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc.Document;
        var action = args.OptString("action") ?? "list";
        if (action is not ("list" or "cut" or "join" or "quantity" or "convert")) throw new ArgumentException("action must be list, cut, join, quantity or convert.");
        var isolate = args.OptString("isolate") ?? "none";
        if (isolate is not ("none" or "temporary" or "new_3d_view")) throw new ArgumentException("isolate must be none, temporary or new_3d_view.");

        var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance));
        if (args.OptString("category") is { Length: > 0 } cat) collector = collector.OfCategory(RevitHelpers.ParseCategory(doc, cat));
        var ids = args.LongList("element_ids");
        if (ids.Count > 0)
        {
            var idSet = ids.Select(i => new ElementId(i)).Where(i => doc.GetElement(i) != null).ToList();
            if (idSet.Count == 0) throw new ArgumentException("None of element_ids exist in this model.");
            collector = collector.WherePasses(new ElementIdSetFilter(idSet));
        }
        var levelWanted = args.OptString("level")?.Trim();
        var found = collector.Cast<FamilyInstance>().Where(fi => fi.Symbol?.Family?.IsInPlace == true)
            .Select(fi => (E: fi, Level: LevelName(doc, fi)))
            .Where(x => string.IsNullOrEmpty(levelWanted) || string.Equals(x.Level, levelWanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var notInPlace = ids.Where(i => !found.Any(f => f.E.Id.Value == i)).Take(50).ToList();

        string? shown = null;
        var foundIds = found.Select(f => f.E.Id).ToList();
        if (found.Count > 0 && args.OptBool("select")) uidoc.Selection.SetElementIds(foundIds);
        if (found.Count > 0 && isolate == "temporary") shown = IsolateTemporary(doc, doc.ActiveView, foundIds);
        if (found.Count > 0 && isolate == "new_3d_view")
        {
            var approved = !AgentSettings.Load().RequireApprovalForChanges || new TaskDialog("Sketch ProMax")
            {
                MainInstruction = "Claude wants to create a 3D view",
                MainContent = $"A new 3D view with a section box around {found.Count} in-place element(s). This changes the model; Ctrl+Z undoes it.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No,
            }.Show() == TaskDialogResult.Yes;
            shown = approved ? New3DView(uidoc, found.Select(f => (Element)f.E).ToList(), foundIds) : "the user declined the new 3D view";
        }

        var list = found
            .GroupBy(f => f.E.Category?.Name ?? "(no category)").OrderBy(g => g.Key)
            .Select(g => new
            {
                category = g.Key,
                count = g.Count(),
                levels = g.GroupBy(f => f.Level ?? "(no level)").OrderBy(l => l.Key).Select(l => new
                {
                    level = l.Key,
                    elements = l.Take(200).Select(f => new { id = f.E.Id.Value, family = f.E.Symbol.Family.Name, volume = Value(f.E, BuiltInParameter.HOST_VOLUME_COMPUTED), area = Value(f.E, BuiltInParameter.HOST_AREA_COMPUTED) }),
                }),
            }).Take(50).ToList();

        return ToolResult.Ok(new
        {
            found = found.Count,
            inPlace = list,
            notInPlace = notInPlace.Count > 0 ? notInPlace : null,
            selected = args.OptBool("select") ? found.Count : (int?)null,
            isolated = shown,
            why = "In-place elements have no loadable family behind them: tools that read or rebuild family geometry, swap types, or " +
                  "copy families skip them, and their geometry can only be edited in Revit's in-place editor.",
            steps = Steps(action),
        });
    }

    private static string? LevelName(Document doc, Element e)
    {
        var id = e.LevelId;
        if (id == ElementId.InvalidElementId)
            foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM })
                if (e.get_Parameter(bip) is { StorageType: StorageType.ElementId } p && p.AsElementId() != ElementId.InvalidElementId) { id = p.AsElementId(); break; }
        return id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
    }

    private static string? Value(Element e, BuiltInParameter bip) =>
        e.get_Parameter(bip) is { HasValue: true } p && p.AsDouble() > 0 ? p.AsValueString() : null;

    private static string IsolateTemporary(Document doc, View view, List<ElementId> ids)
    {
        try
        {
            using var t = RevitHelpers.StartTransaction(doc, "Isolate in-place elements");
            view.IsolateElementsTemporary(ids);
            t.Commit();
            return $"temporarily isolated in '{view.Name}' (Reset Temporary Hide/Isolate to undo)";
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { return $"could not isolate in '{view.Name}': {ex.Message.Trim()}"; }
    }

    private static string New3DView(UIDocument uidoc, List<Element> elements, List<ElementId> ids)
    {
        var doc = uidoc.Document;
        var box = ViewKit.Extents(doc, elements);
        if (box is null) return "the elements have no extents to box";
        View3D view;
        string name;
        using (var t = RevitHelpers.StartTransaction(doc, "3D view of in-place elements"))
        {
            view = View3D.CreateIsometric(doc, ViewKit.TypeFor(doc, ViewFamily.ThreeDimensional).Id);
            if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
            var grown = ViewKit.Grow(box, 1000 / 304.8);
            view.SetSectionBox(grown);
            var centre = (grown.Min + grown.Max) / 2;
            var o = view.GetOrientation();
            var forward = o.ForwardDirection.Normalize();
            view.SetOrientation(new ViewOrientation3D(centre - forward * Math.Max(grown.Min.DistanceTo(grown.Max), 1.0), o.UpDirection, forward));
            name = ViewKit.Finish(doc, view, "In-place elements", null, null);
            try { view.IsolateElementsTemporary(ids); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            t.Commit();
        }
        try { uidoc.RequestViewChange(view); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        return $"new 3D view '{name}' (id {view.Id.Value}) with a section box around them, opened in Revit";
    }

    private static string[] Steps(string action) => action switch
    {
        "cut" =>
        [
            "Cut another element WITH the in-place element: Modify tab > Geometry > Cut > Cut Geometry. Pick the element to be cut first, then the in-place element.",
            "If Revit says the elements can't cut each other, make the cutter a void instead: select the in-place element > Edit In-Place, Create > Void Forms (Extrusion/Blend/Sweep), draw the void, then Modify > Cut Geometry inside the editor and pick the project element to cut; Finish Model.",
            "Cut the in-place element itself with a void: select it > Edit In-Place, add a void form, Cut Geometry on the in-place solid, Finish Model.",
            "To undo a cut: Modify > Geometry > Cut > Uncut Geometry, pick both.",
        ],
        "join" =>
        [
            "Modify tab > Geometry > Join > Join Geometry. Tick 'Multiple Join' on the Options Bar to join one element to many.",
            "Pick the in-place element first, then each element to join to it (walls, floors, columns, beams, generic models).",
            "If the wrong one wins, Join > Switch Join Order and pick the two elements.",
            "If Revit refuses ('elements are not intersecting' or 'cannot be joined'), check they overlap; some categories can't join in-place solids. Use Cut Geometry instead (action 'cut').",
        ],
        "quantity" =>
        [
            "Quick check: select the element; Properties shows Volume and Area where its category computes them (walls, floors, roofs, structural categories). The list above includes those values when Revit has them.",
            "Schedule: View tab > Schedules > Material Takeoff, pick the category (e.g. Generic Models), add Material: Volume and Material: Area, plus Family and Level. Material takeoffs measure the solid geometry, so in-place elements are counted.",
            "If a solid has no material, it shows as <By Category>: Edit In-Place, select the solid, set its Material in Properties, Finish Model.",
            "Export the schedule (File > Export > Reports > Schedule) or ask me to read it with the schedule tools.",
        ],
        "convert" =>
        [
            "Revit has no command (and no API) to turn an in-place family into a loadable one; it is rebuilt by copy and paste.",
            "Select the in-place element > Edit In-Place. Select all its forms and voids, Ctrl+C, then Finish Model.",
            "File > New > Family, choose a template of the same category (e.g. Metric Generic Model.rft); or set Create > Family Category and Parameters afterwards.",
            "Modify > Paste > Aligned to Same Place. Add reference planes and parameters as needed, set materials, then Save As an .rfa.",
            "Load into Project, place an instance where the in-place element is (use its id from the list above to find it), compare, then delete the in-place element.",
            "Repeat for each in-place family; identical copies only need converting once, then swap the others to the new type.",
        ],
        _ =>
        [
            "Ask again with action 'cut', 'join', 'quantity' or 'convert' for the manual steps.",
            "select: true highlights them in Revit; isolate 'temporary' shows only them in the active view; isolate 'new_3d_view' makes a 3D view boxed around them.",
        ],
    };
}
