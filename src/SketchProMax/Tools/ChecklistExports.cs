using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// "Export the NWC and IFC" on the pre-submission checklist: the NWC from the 3D Navisworks Export view and the IFC,
/// each with the office's settings (checks 49 and 50), named exactly like the model, into the export folder (Checking
/// rules), else next to the model. Revit doesn't keep export settings in the files, so each export Sketch ProMax makes is
/// recorded with its settings; the checks then pass for files it exported with the current settings. Revit API context.
/// </summary>
internal static class ChecklistExports
{
    /// <summary>The office's NWC settings (check 49). A change here must change the text, so older exports stop passing.</summary>
    public const string NwcSettings = "NWC v1: shared coordinates, the 3D Navisworks Export view, divided into levels, element IDs, all parameters, element and type properties, no linked files or linked CAD";

    /// <summary>The office's IFC settings (check 50): the setup file (by name and date) when one is set, else the defaults.</summary>
    public static string IfcSettings(ChecklistMath.Definition? def)
    {
        var setup = ChecklistMath.Rule(def, "ifcsetup").Trim('"');
        return setup.Length > 0
            ? $"IFC v1: setup {Path.GetFileName(setup)} ({ChecklistMath.Stamp(setup)}), shared coordinates, no links"
            : "IFC v1: IFC 4x3, shared coordinates, visible elements of the 3D Navisworks Export view, no links";
    }

    private static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "checklist-exports.json");

    private sealed class Entry
    {
        public string Settings { get; set; } = "";
        public long Written { get; set; }
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            return File.Exists(LogPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(LogPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private static void Record(string file, string settings)
    {
        var log = Load();
        log[Path.GetFullPath(file).ToLowerInvariant()] = new Entry { Settings = settings, Written = File.GetLastWriteTimeUtc(file).Ticks };
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, JsonSerializer.Serialize(log));
    }

    /// <summary>Whether Sketch ProMax exported this file, as it is now, with these settings.</summary>
    public static bool MadeWith(string file, string settings)
    {
        if (!Load().TryGetValue(Path.GetFullPath(file).ToLowerInvariant(), out var e) || e.Settings != settings) return false;
        return Math.Abs(File.GetLastWriteTimeUtc(file).Ticks - e.Written) < TimeSpan.FromSeconds(2).Ticks;
    }

    /// <summary>Where the exports go: the first export folder in the checking rules, else the model's folder.</summary>
    public static string Folder(Document doc, ChecklistMath.Definition? def)
    {
        if (ChecklistMath.RuleList(ChecklistMath.Rule(def, "exportfolder")).FirstOrDefault() is { } set) return set.Trim('"');
        try { return string.IsNullOrEmpty(doc.PathName) ? "" : Path.GetDirectoryName(doc.PathName) ?? ""; }
        catch (ArgumentException) { return ""; }
    }

    /// <summary>The 3D view the NWC is exported from (check 47's view).</summary>
    public static View3D? NavisView(Document doc, ChecklistMath.Definition? def)
    {
        var pattern = ChecklistMath.Rule(def, "navisview") is { Length: > 0 } p ? p : "*Navisworks*";
        return new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate && ChecklistMath.LikeAny(v.Name, pattern));
    }

    /// <summary>Whether the project needs an IFC: not when Files the package must have is set without .ifc (the NWC alone is exported).</summary>
    public static bool WantsIfc(ChecklistMath.Definition? def)
    {
        var contents = ChecklistMath.RuleList(ChecklistMath.Rule(def, "packagecontents"));
        return contents.Count == 0 || contents.Any(c => c.TrimStart('.', '*').Equals("ifc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Exports the NWC and the IFC with the office's settings (the NWC alone when the project needs no IFC); a sentence saying what was written or why not.</summary>
    public static string Export(Document doc, ChecklistMath.Definition? def)
    {
        var folder = Folder(doc, def);
        if (folder.Length == 0) return "Save the model first, or set the NWC and IFC folder in Checking rules.";
        Directory.CreateDirectory(folder);
        if (NavisView(doc, def) is not { } view) return "There's no 3D Navisworks Export view to export from: make it first (check 47 has a Fix button).";
        var name = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(doc.Title));
        var said = new List<string>();

        if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
            said.Add("The NWC wasn't exported: the Navisworks exporter isn't installed for this Revit (Autodesk's free Navisworks NWC Export Utility).");
        else
        {
            var nwc = new NavisworksExportOptions
            {
                ExportScope = NavisworksExportScope.View,
                ViewId = view.Id,
                Coordinates = NavisworksCoordinates.Shared,
                DivideFileIntoLevels = true,
                ExportElementIds = true,
                Parameters = NavisworksParameters.All,
                ConvertElementProperties = true,
                ExportLinks = false,
                ConvertLinkedCADFormats = false,
            };
            doc.Export(folder, name + ".nwc", nwc);
            var file = Path.Combine(folder, name + ".nwc");
            if (File.Exists(file)) { Record(file, NwcSettings); said.Add($"Exported {name}.nwc."); }
            else said.Add("Revit didn't write the NWC.");
        }

        if (!WantsIfc(def)) return string.Join(" ", said) + $" In {folder}. No IFC: the package needs none (Files the package must have).";

        var ifc = new IFCExportOptions { FileVersion = Enum.TryParse<IFCVersion>("IFC4x3", out var v4x3) ? v4x3 : IFCVersion.IFC4RV, FilterViewId = view.Id };
        var setup = ChecklistMath.Rule(def, "ifcsetup").Trim('"');
        if (setup.Length > 0)
        {
            if (!File.Exists(setup)) return string.Join(" ", said.Append($"The IFC wasn't exported: the setup file {setup} can't be found."));
            // The exporter's saved setup: its settings are the exporter's own option names.
            using var json = JsonDocument.Parse(File.ReadAllText(setup));
            foreach (var p in json.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("IFCVersion", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && Enum.IsDefined(typeof(IFCVersion), p.Value.GetInt32()))
                    ifc.FileVersion = (IFCVersion)p.Value.GetInt32();
                else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) ifc.AddOption(p.Name, p.Value.GetBoolean() ? "true" : "false");
                else if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) ifc.AddOption(p.Name, p.Value.ToString());
            }
        }
        else ifc.AddOption("VisibleElementsOfCurrentView", "true");
        ifc.AddOption("SitePlacement", "0"); // shared coordinates
        ifc.AddOption("ExportLinkedFiles", "false");
        // IFC export needs an open transaction; nothing is kept.
        using (var t = new Transaction(doc, "Sketch ProMax: IFC export"))
        {
            t.Start();
            var ok = doc.Export(folder, name + ".ifc", ifc);
            t.RollBack();
            var file = Path.Combine(folder, name + ".ifc");
            if (ok && File.Exists(file)) { Record(file, IfcSettings(def)); said.Add($"Exported {name}.ifc ({ifc.FileVersion})."); }
            else said.Add("Revit reported the IFC export failed.");
        }
        return string.Join(" ", said) + $" In {folder}.";
    }
}
