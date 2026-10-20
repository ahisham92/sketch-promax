using System.IO;
using SketchProMax.Settings;

namespace SketchProMax.Tools;

/// <summary>
/// Which pre-submission checklist is in use, kept on this PC. In order: the Excel file imported (Settings.ChecklistPath)
/// when it changed since it was read; else the copy saved in %APPDATA%\SketchProMax (imported, or edited in the add-in);
/// else the built-in checklist. Editing in the add-in saves the copy and stops following the Excel file; importing
/// follows it again; going back to the built-in one deletes the copy. A project can use one of its own checklists
/// instead (ChecklistProjects), kept beside it.
/// </summary>
internal static class ChecklistStore
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");

    /// <summary>The checklist as imported or edited.</summary>
    public static string SavedPath => Path.Combine(Folder, "pre-submission checklist.json");

    /// <summary>0.34.6 kept a copy of the picked Excel file here; it becomes the saved checklist the first time it is read.</summary>
    private static string OldCopy => Path.Combine(Folder, "pre-submission checklist.xlsx");

    /// <summary>The checklist a project uses: its own when one is set and can be read, else the office checklist.</summary>
    public static (ChecklistMath.Definition? Def, string Note) Load(AgentSettings settings, string project)
    {
        var own = ChecklistProjects.Assigned(project);
        if (own.Length > 0)
        {
            if (ChecklistProjects.Read(own) is { } def) return (def, "");
            var (office, note) = Load(settings);
            return (office, $"The checklist {own} picked for this project can't be read, so the Default checklist is shown. " + note);
        }
        return Load(settings);
    }

    /// <summary>
    /// Saves a checklist where it came from: a project checklist to its own file; the Default's checking rules alone
    /// (they name a person's own files and folders) beside it, so Default updates still reach them; else as the
    /// office checklist.
    /// </summary>
    public static void SaveInUse(ChecklistMath.Definition def)
    {
        if (ChecklistProjects.NameOf(def.SourcePath) is { } name) ChecklistProjects.Save(name, def);
        else if (def.SourcePath == ChecklistMath.BuiltInSource && ChecklistMath.BuiltIn() is { } builtIn)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(RulesPath, System.Text.Json.JsonSerializer.Serialize(ChecklistProjects.OwnRules(def, builtIn)));
        }
        else Save(def);
    }

    /// <summary>Your own checking rules on the Default checklist.</summary>
    private static string RulesPath => Path.Combine(Folder, "checklist rules.json");

    private static ChecklistMath.Definition WithOwnRules(ChecklistMath.Definition builtIn)
    {
        try
        {
            if (File.Exists(RulesPath) && System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RulesPath)) is { } own)
                return ChecklistProjects.WithRules(builtIn, own);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return builtIn;
    }

    /// <summary>The office checklist to use, and a note to show when something couldn't be read (empty when all is well).</summary>
    public static (ChecklistMath.Definition? Def, string Note) Load(AgentSettings settings)
    {
        var note = "";
        var saved = ReadSaved();
        var path = (settings.ChecklistPath ?? "").Trim().Trim('"');
        if (path.Length > 0)
        {
            try
            {
                if (File.Exists(path))
                {
                    if (saved is not null && string.Equals(saved.SourcePath, path, StringComparison.OrdinalIgnoreCase) && saved.SourceStamp == ChecklistMath.Stamp(path))
                        return (saved, "");
                    var def = ChecklistMath.ReadXlsx(path);
                    TrySave(def);
                    return (def, "");
                }
                if (saved is null) note = $"The checklist Excel file picked in Settings isn't there any more ({path}), so the built-in checklist is shown.";
            }
            catch (InvalidDataException ex) { note = $"Couldn't read the checklist Excel file: {ex.Message}"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                note = $"Couldn't open the checklist Excel file ({ex.Message}). Check the file is on a drive you can reach.";
            }
            if (note.Length > 0 && saved is not null) note += " The copy kept in Sketch ProMax is used.";
        }
        if (saved is not null) return (saved, note);
        try
        {
            if (File.Exists(OldCopy))
            {
                var def = ChecklistMath.ReadXlsx(OldCopy);
                def.SourcePath = path.Length > 0 ? path : ChecklistMath.EditedSource;
                if (TrySave(def)) File.Delete(OldCopy);
                return (def, note);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
        var builtIn = ChecklistMath.BuiltIn();
        if (builtIn is null && note.Length == 0) note = "The built-in checklist couldn't be read. Pick your office's checklist Excel file.";
        return (builtIn is null ? null : WithOwnRules(builtIn), note);
    }

    /// <summary>Saves the checklist in use (imported or edited). Throws IOException / UnauthorizedAccessException when it can't.</summary>
    public static void Save(ChecklistMath.Definition def)
    {
        Directory.CreateDirectory(Folder);
        var temp = SavedPath + ".new";
        File.WriteAllText(temp, ChecklistMath.ToJson(def));
        if (File.Exists(SavedPath)) File.Delete(SavedPath);
        File.Move(temp, SavedPath);
    }

    /// <summary>Back to the built-in checklist: the saved copy goes (Settings.ChecklistPath is cleared by the caller).</summary>
    public static void Reset()
    {
        if (File.Exists(SavedPath)) File.Delete(SavedPath);
        if (File.Exists(OldCopy)) File.Delete(OldCopy);
    }

    private static bool TrySave(ChecklistMath.Definition def)
    {
        try { Save(def); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static ChecklistMath.Definition? ReadSaved()
    {
        try
        {
            if (!File.Exists(SavedPath)) return null;
            var def = ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(SavedPath));
            return def is not null && def.Items.Any() ? def : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
