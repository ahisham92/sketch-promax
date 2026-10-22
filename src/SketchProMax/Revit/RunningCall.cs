using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SketchProMax.Revit;

/// <summary>
/// The tool call running in Revit right now, kept in a small file per Revit process (logs\running-PID.json) for as long
/// as it runs. A Revit that closes by itself mid-call (a crash leaves no activity-log line, and the chat keeps no
/// arguments) leaves the file behind: the next start finds it (<see cref="Recover"/>) and the activity log says which
/// call Revit closed during, with its arguments (the code of run_revit_code, for one). Nested calls (the field test, a
/// recipe) name the innermost one. No Revit API: App switches it on and writes what <see cref="Recover"/> returns.
/// </summary>
internal static class RunningCall
{
    /// <summary>Longest arguments text kept, as the activity log keeps.</summary>
    public const int MaxArgs = 4000;

    /// <summary>On in the Sketch ProMax core (not TritoRevit's), set at start.</summary>
    public static volatile bool Enabled;

    /// <summary>This core's version, set at start.</summary>
    public static string Version = "";

    /// <summary>Tests point it at a folder of their own.</summary>
    internal static string? FolderOverride { get; set; }

    private static readonly object Gate = new();
    private static readonly Stack<Entry> Open = new();

    internal sealed class Entry
    {
        public string Time { get; set; } = "";
        public string Where { get; set; } = "";
        public string Tool { get; set; } = "";
        public string Model { get; set; } = "";
        public string Version { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    private static string Folder => FolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "logs");

    internal static string FileOf(int pid) => Path.Combine(Folder, $"running-{pid}.json");

    private static int Pid => Process.GetCurrentProcess().Id;

    /// <summary>A call starts (<paramref name="where"/>: chat, ribbon, field test, …). Pair it with <see cref="End"/> in a finally.</summary>
    public static void Begin(string where, string tool, string? model, IReadOnlyDictionary<string, JsonElement>? args)
    {
        if (!Enabled) return;
        string arguments;
        try { arguments = args is null ? "" : JsonSerializer.Serialize(args); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { arguments = ""; }
        var entry = new Entry
        {
            Time = DateTime.Now.ToString("s"), Where = where, Tool = tool, Model = model ?? "", Version = Version,
            Arguments = arguments.Length > MaxArgs ? arguments.Substring(0, MaxArgs) + "…" : arguments,
        };
        lock (Gate)
        {
            Open.Push(entry);
            Save(entry);
        }
    }

    /// <summary>The call ended (any way): the one around it is the running call again, or none.</summary>
    public static void End()
    {
        if (!Enabled) return;
        lock (Gate)
        {
            if (Open.Count > 0) Open.Pop();
            if (Open.Count > 0) Save(Open.Peek());
            else Delete(FileOf(Pid));
        }
    }

    /// <summary>This core stops (Revit closes, a version switch): nothing of it is running.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Open.Clear();
            Delete(FileOf(Pid));
        }
    }

    /// <summary>
    /// The calls earlier Revit sessions closed during: the files of processes that are no longer a running Revit, each
    /// as one line for the activity log (their files are removed). Never this process's own.
    /// </summary>
    public static List<string> Recover() => Recover(Pid, IsRevitRunning);

    internal static List<string> Recover(int self, Func<int, bool> alive)
    {
        var lines = new List<string>();
        string[] files;
        try { files = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "running-*.json") : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return lines; }
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!int.TryParse(name.Substring("running-".Length), out var pid) || pid == self || alive(pid)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
                if (entry is not null) lines.Add(Describe(entry));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            Delete(file);
        }
        return lines;
    }

    /// <summary>The activity log's line for a call Revit closed during.</summary>
    internal static string Describe(Entry e) =>
        $"Revit closed by itself while {e.Tool} was running" + (e.Where.Length > 0 ? $" (from {e.Where})" : "") +
        (e.Model.Length > 0 ? $" on '{e.Model}'" : "") + $", started {e.Time}" + (e.Version.Length > 0 ? $", Sketch ProMax {e.Version}" : "") +
        ". Its arguments: " + (e.Arguments.Length > 0 ? e.Arguments : "(none)");

    private static bool IsRevitRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.IndexOf("Revit", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static void Save(Entry entry)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FileOf(Pid), JsonSerializer.Serialize(entry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only the diagnosis after a crash is lost */ }
    }

    private static void Delete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
