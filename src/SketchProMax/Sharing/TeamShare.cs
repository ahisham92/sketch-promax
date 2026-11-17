using System.IO;
using System.Text;
using System.Text.Json;
using SketchProMax.Tools;

namespace SketchProMax.Sharing;

/// <summary>
/// What one person's chat coded, everyone's chat uses (the owner's ask, 2026-11-17: "if I did something and it is coded,
/// when someone asks for the same thing it is already stored"). A team folder on the network holds:
///   Kept code\&lt;PC&gt;.json      each PC's kept code (WorkedCode); every PC writes its own file and reads the others';
///   One-click tools\*.json   the owner's learned tools; every PC installs them (never over its own tool of that name,
///                            one changed here, or one deleted here). Only the owner's PC publishes (the 2026-10-25 rule).
/// The folder is the one set with worked_code action=share, else the update folder of a shared pack this PC published or
/// installed, so most PCs need no setup. All file work runs in the background round (App.SharedLoopAsync), never on
/// Revit's thread; the chat reads what the last round found. No folder, or it can't be reached: nothing happens.
/// </summary>
internal static class TeamShare
{
    public const string CodeFolder = "Kept code", ToolsFolder = "One-click tools";

    private static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");
    public static string SettingFile => Path.Combine(Roaming, "team-folder.txt");
    private static string StateFile => Path.Combine(Roaming, "team-tools.json");

    private static readonly object Gate = new();
    private static List<WorkedCode.Entry> _others = [];
    private static string _published = "";

    /// <summary>Called once at start: the chat's kept-code lookups then include what the last round read from the team.</summary>
    public static void Start() => WorkedCode.Team = () => { lock (Gate) return _others.ToList(); };

    /// <summary>The folder set on this PC ("" when none was set; "off" turns sharing off).</summary>
    public static string Setting()
    {
        try { return File.Exists(SettingFile) ? File.ReadAllText(SettingFile).Trim() : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public static void Set(string folder)
    {
        Directory.CreateDirectory(Roaming);
        File.WriteAllText(SettingFile, folder.Trim(), new UTF8Encoding(false));
    }

    /// <summary>The team folder in use: the one set here, else a shared pack's update folder. "" when none or sharing is off.</summary>
    public static string Folder()
    {
        var set = Setting();
        if (set.Equals("off", StringComparison.OrdinalIgnoreCase)) return "";
        if (set.Length > 0) return set;
        try
        {
            return SharedPacks.Records().Select(r => r.UpdateFrom).Concat(SharedPacks.List().Select(i => i.Pack.UpdateFrom))
                .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f)) ?? "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
    }

    /// <summary>One background round: this PC's kept code out, the colleagues' in, one-click tools out (owner) and in.</summary>
    public static List<string> Round(bool ownerPc)
    {
        var notes = new List<string>();
        var folder = Folder();
        if (folder.Length == 0 || !Directory.Exists(folder)) return notes;
        var pc = Environment.MachineName;

        // Kept code out: this PC's file, when it changed.
        try
        {
            if (File.Exists(WorkedCode.StoreFile))
            {
                var mine = File.ReadAllText(WorkedCode.StoreFile);
                if (mine != _published)
                {
                    Directory.CreateDirectory(Path.Combine(folder, CodeFolder));
                    WriteWhole(Path.Combine(folder, CodeFolder, pc + ".json"), mine);
                    _published = mine;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // Kept code in.
        try
        {
            var dir = Path.Combine(folder, CodeFolder);
            if (Directory.Exists(dir))
            {
                var files = new List<(string, string)>();
                foreach (var file in Directory.GetFiles(dir, "*.json").Take(200))
                {
                    if (new FileInfo(file).Length > 20_000_000) continue;
                    try { files.Add((Path.GetFileNameWithoutExtension(file), File.ReadAllText(file))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                var others = WorkedCode.FromTeam(files, pc);
                lock (Gate) _others = others;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // One-click tools.
        try
        {
            var dir = Path.Combine(folder, ToolsFolder);
            var state = ReadState();
            if (ownerPc && Directory.Exists(LearnedTools.Folder))
            {
                Directory.CreateDirectory(dir);
                foreach (var file in Directory.GetFiles(LearnedTools.Folder, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (LearnedTools.Load(name) is null) continue;
                    var text = File.ReadAllText(file);
                    var target = Path.Combine(dir, name + ".json");
                    if (File.Exists(target) && Stamp(File.ReadAllText(target)) == Stamp(text)) continue;
                    WriteWhole(target, text);
                    notes.Add("Shared the one-click tool " + name + " with the team");
                }
            }
            if (Directory.Exists(dir))
            {
                var changed = false;
                foreach (var file in Directory.GetFiles(dir, "*.json").Take(500))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!LearnedTools.ValidName.IsMatch(name) || new FileInfo(file).Length > 2_000_000) continue;
                    var text = File.ReadAllText(file);
                    // The file must be a learned tool that carries its own name, as for a pack's tools.
                    try { if (LearnedTool.FromJson(text).Name != name) continue; }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { continue; }
                    var local = LearnedTools.PathFor(name);
                    var localHash = File.Exists(local) ? Stamp(File.ReadAllText(local)) : null;
                    var teamHash = Stamp(text);
                    state.TryGetValue(name, out var installed);
                    var what = WorkedCode.Decide(localHash, installed, teamHash);
                    if (what == "keep")
                    {
                        // The owner's own tools are recorded as given, so a later change there is not taken for a colleague's edit.
                        if (localHash == teamHash && installed != teamHash) { state[name] = teamHash; changed = true; }
                        continue;
                    }
                    Directory.CreateDirectory(LearnedTools.Folder);
                    WriteWhole(local, text);
                    state[name] = teamHash;
                    changed = true;
                    notes.Add((what == "install" ? "Got the one-click tool " : "Updated the one-click tool ") + name + " from the team");
                }
                if (changed) WriteWhole(StateFile, JsonSerializer.Serialize(state));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return notes;
    }

    /// <summary>How sharing stands on this PC, for worked_code action=share.</summary>
    public static (string Folder, bool Reachable, int FromTeam) Status()
    {
        var folder = Folder();
        int count;
        lock (Gate) count = _others.Count;
        var reachable = false;
        try { reachable = folder.Length > 0 && Directory.Exists(folder); } catch (Exception) { }
        return (folder, reachable, count);
    }

    /// <summary>A tool file's fingerprint without its use count, which changes every time the tool runs.</summary>
    internal static string Stamp(string text) =>
        LearnedTools.Hash(System.Text.RegularExpressions.Regex.Replace(text, "\"uses\"\\s*:\\s*\\d+", "\"uses\":0"));

    private static Dictionary<string, string> ReadState()
    {
        try
        {
            if (File.Exists(StateFile)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StateFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    private static void WriteWhole(string path, string text)
    {
        var temp = path + "." + Environment.MachineName + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }
}
