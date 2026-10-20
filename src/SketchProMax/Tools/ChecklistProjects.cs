using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// Checklists per project, kept for this Windows user only in %APPDATA%\SketchProMax\project checklists: named
/// checklists (each starts as a copy of the Default, or is imported from Excel), which one each project uses (by the
/// project code at the start of the model's file name), and the password that protects the Default checklist. Add-in
/// updates and shared packs never write here, so nothing in it is overwritten. No Revit API.
/// </summary>
internal static class ChecklistProjects
{
    /// <summary>The tests point this at a temporary folder (per thread, so a test run never moves the user's own files).</summary>
    [ThreadStatic] internal static string? FolderForTests;

    private static string Folder => FolderForTests ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "project checklists");
    private static string MapPath => Path.Combine(Folder, "projects.json");
    private static string LockPath => Path.Combine(Folder, "editing.json");
    private const string Extension = ".checklist.json";
    private const string Prefix = "project-checklist:";

    /// <summary>The password until someone changes it.</summary>
    public const string DefaultPassword = "0000";

    /// <summary>The SourcePath a project checklist carries, so a save goes back to it.</summary>
    public static string Source(string name) => Prefix + name;

    /// <summary>The project checklist's name from a SourcePath, or null for the office checklist.</summary>
    public static string? NameOf(string? sourcePath) =>
        sourcePath is not null && sourcePath.StartsWith(Prefix, StringComparison.Ordinal) ? sourcePath.Substring(Prefix.Length) : null;

    /// <summary>
    /// The project a model belongs to: the code before the first dash of its file name (PRJ1 for
    /// PRJ1-070-00-ABC-STR-3DM-0010.rvt), or the whole name when it isn't dashed that way.
    /// </summary>
    public static string ProjectKey(string modelFile)
    {
        var file = modelFile ?? "";
        file = file.Substring(file.LastIndexOfAny(['\\', '/']) + 1);
        var name = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(file)).Trim();
        var parts = name.Split('-');
        return parts.Length >= 3 && parts[0].Trim().Length >= 2 ? parts[0].Trim().ToUpperInvariant() : name;
    }

    /// <summary>A name that can be a file name: what Windows refuses becomes _, and at most 60 characters.</summary>
    public static string CleanName(string name)
    {
        var clean = ChecklistMath.SafeName(System.Text.RegularExpressions.Regex.Replace(name ?? "", @"\s+", " ").Trim());
        return clean.Length > 60 ? clean.Substring(0, 60).Trim() : clean;
    }

    private static string PathOf(string name) => Path.Combine(Folder, CleanName(name) + Extension);

    /// <summary>The project checklists on this PC, by name.</summary>
    public static List<string> Names()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetFiles(Folder, "*" + Extension).Select(f => Path.GetFileName(f)).Select(f => f.Substring(0, f.Length - Extension.Length))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Exists(string name) => CleanName(name).Length > 0 && File.Exists(PathOf(name));

    /// <summary>A project checklist, or null when it isn't there or can't be read.</summary>
    public static ChecklistMath.Definition? Read(string name)
    {
        try
        {
            if (!Exists(name)) return null;
            var def = ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(PathOf(name)));
            if (def is null || !def.Items.Any()) return null;
            def.SourcePath = Source(CleanName(name));
            return def;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Saves a project checklist. Throws IOException / UnauthorizedAccessException when it can't.</summary>
    public static void Save(string name, ChecklistMath.Definition def)
    {
        var clean = CleanName(name);
        if (clean.Length == 0) throw new ArgumentException("A project checklist needs a name.", nameof(name));
        def.SourcePath = Source(clean);
        Directory.CreateDirectory(Folder);
        var path = PathOf(clean);
        var temp = path + ".new";
        File.WriteAllText(temp, ChecklistMath.ToJson(def));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    /// <summary>Deletes a project checklist; the projects that used it go back to the office checklist.</summary>
    public static void Delete(string name)
    {
        var clean = CleanName(name);
        if (File.Exists(PathOf(clean))) File.Delete(PathOf(clean));
        var map = ReadMap();
        foreach (var project in map.Where(p => string.Equals(p.Value, clean, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList()) map.Remove(project);
        WriteMap(map);
    }

    /// <summary>The project checklist a project uses, or "" for the office checklist.</summary>
    public static string Assigned(string project) =>
        project.Length > 0 && ReadMap().TryGetValue(project, out var name) && Exists(name) ? name : "";

    /// <summary>Sets the checklist a project uses ("" for the office checklist).</summary>
    public static void Assign(string project, string name)
    {
        if (project.Length == 0) return;
        var map = ReadMap();
        if (CleanName(name).Length == 0) map.Remove(project);
        else map[project] = CleanName(name);
        WriteMap(map);
    }

    private static Dictionary<string, string> ReadMap()
    {
        try
        {
            return File.Exists(MapPath)
                ? new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MapPath)) ?? new(), StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void WriteMap(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(MapPath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>A new checklist that starts as a copy of <paramref name="from"/> (the Default), under its own title.</summary>
    public static ChecklistMath.Definition Copy(ChecklistMath.Definition from, string title)
    {
        var def = ChecklistMath.FromJson<ChecklistMath.Definition>(ChecklistMath.ToJson(from)) ?? throw new InvalidOperationException("The checklist couldn't be copied.");
        def.Title = title.Trim();
        def.SourceStamp = DateTime.Now.ToString("dd-MMM-yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return ChecklistMath.Renumber(def);
    }

    /// <summary>Where Make this the Default for everyone saves the next Default, for the next update to carry.</summary>
    public static string NextDefaultPath => Path.Combine(DocumentsForTests ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Default checklist for the next update.json");

    /// <summary>The tests point this at a temporary folder (per thread).</summary>
    [ThreadStatic] internal static string? DocumentsForTests;

    /// <summary>Saves <paramref name="def"/> as the next Default (the built-in checklist's layout); returns where.</summary>
    public static string SaveNextDefault(ChecklistMath.Definition def)
    {
        var copy = ChecklistMath.FromJson<ChecklistMath.Definition>(ChecklistMath.ToJson(def)) ?? throw new InvalidOperationException("The checklist couldn't be copied.");
        copy.SourcePath = "";
        copy.SourceStamp = "";
        var path = NextDefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ChecklistMath.ToJson(ChecklistMath.Renumber(copy)));
        return path;
    }

    // ---- A person's own checking rules on the Default --------------------------------------------------------------

    /// <summary>
    /// The rules <paramref name="mine"/> sets differently from <paramref name="theDefault"/> (a rule not set counts as
    /// its built-in default): kept apart, so the Default can still be updated under them.
    /// </summary>
    public static Dictionary<string, string> OwnRules(ChecklistMath.Definition mine, ChecklistMath.Definition theDefault)
    {
        var own = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in ChecklistMath.RuleDefs)
        {
            var value = mine.Rules.TryGetValue(rule.Key, out var m) ? m : rule.Default;
            var given = theDefault.Rules.TryGetValue(rule.Key, out var d) ? d : rule.Default;
            if (value != given) own[rule.Key] = value;
        }
        return own;
    }

    /// <summary>The Default with a person's own rules laid over it.</summary>
    public static ChecklistMath.Definition WithRules(ChecklistMath.Definition theDefault, IReadOnlyDictionary<string, string> own)
    {
        foreach (var pair in own)
            if (ChecklistMath.RuleDefs.Any(r => r.Key == pair.Key)) theDefault.Rules[pair.Key] = pair.Value;
        return theDefault;
    }

    // ---- The editing password ---------------------------------------------------------------------------------------

    /// <summary>Whether <paramref name="entered"/> is the password for changing checks (0000 until it is changed).</summary>
    public static bool PasswordIs(string entered) => Hash(entered ?? "") == StoredHash();

    /// <summary>Sets a new password. Throws IOException / UnauthorizedAccessException when it can't be saved.</summary>
    public static void SetPassword(string password)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(LockPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["hash"] = Hash(password) }));
    }

    private static string StoredHash()
    {
        try
        {
            if (File.Exists(LockPath)
                && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LockPath)) is { } saved
                && saved.TryGetValue("hash", out var hash) && hash.Length > 0)
                return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return Hash(DefaultPassword);
    }

    private static string Hash(string password)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes("Sketch ProMax checklist editing:" + password)).Select(b => b.ToString("x2")));
    }
}
