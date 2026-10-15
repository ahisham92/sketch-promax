using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SketchProMax.Update;

/// <summary>
/// Makes sure the Claude desktop app knows about the Sketch ProMax connector, every time Revit starts.
/// This repairs installs where the app was installed after Sketch ProMax, or keeps its config somewhere else
/// (the Microsoft Store / MSIX build reads %LOCALAPPDATA%\Packages\<package family>\LocalCache\Roaming\Claude).
/// </summary>
public static class ClaudeRegistration
{
    public static string ConnectorPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "mcp", "SketchProMax.Mcp.exe");

    /// <summary>Config folders the Claude app may read.</summary>
    public static IEnumerable<string> ConfigFolders()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) yield break;
        // Named after the package family, e.g. Claude_pzs8sxrjxfjjc or AnthropicPBC.Claude_fnn82j28hfe8t.
        var family = new System.Text.RegularExpressions.Regex(@"(^|\.)Claude_[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var package in Directory.GetDirectories(packages).Where(d => family.IsMatch(Path.GetFileName(d))))
            yield return Path.Combine(package, "LocalCache", "Roaming", "Claude");
    }

    /// <summary>Config files that currently list the connector, for the Status button.</summary>
    public static List<string> Registered() =>
        ConfigFolders().Select(f => Path.Combine(f, "claude_desktop_config.json"))
            .Where(f => { try { return File.Exists(f) && File.ReadAllText(f).Contains("sketch-promax"); } catch (IOException) { return false; } })
            .ToList();

    /// <summary>Adds or repairs the connector entry. Returns the config files that were changed.</summary>
    public static List<string> Ensure()
    {
        var changed = new List<string>();
        if (!File.Exists(ConnectorPath)) return changed;
        // Several Revit windows start at once; only one reads and rewrites the Claude config at a time.
        using var mutex = new Mutex(false, @"Local\SketchProMax.ClaudeConfig");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) return changed;
        }
        catch (AbandonedMutexException) { /* the previous holder exited mid-write; we own it now */ }
        try
        {
            EnsureLocked(changed);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
        return changed;
    }

    private static void EnsureLocked(List<string> changed)
    {
        foreach (var folder in ConfigFolders())
        {
            try
            {
                var file = Path.Combine(folder, "claude_desktop_config.json");
                JsonObject config;
                if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } text)
                {
                    // Leave a file we can't read alone rather than risk losing the user's other settings.
                    if (JsonNode.Parse(text) is not JsonObject parsed) continue;
                    config = parsed;
                }
                else config = new JsonObject();

                if (config["mcpServers"] is not JsonObject servers)
                {
                    servers = new JsonObject();
                    config["mcpServers"] = servers;
                }
                if (servers["sketch-promax"] is JsonObject entry && entry["command"]?.GetValue<string>() == ConnectorPath) continue;

                servers["sketch-promax"] = new JsonObject { ["command"] = ConnectorPath };
                Directory.CreateDirectory(folder);
                WriteAtomically(file, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                changed.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
    }

    /// <summary>Writes a temp file and swaps it in, so the Claude app never reads a half-written config.</summary>
    private static void WriteAtomically(string file, string text)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        try
        {
            if (File.Exists(file))
            {
                try { File.Replace(temp, file, file + ".bak", true); }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                {
                    // Some file systems can't replace in one step; keep a backup and copy over.
                    File.Copy(file, file + ".bak", true);
                    File.Copy(temp, file, true);
                }
            }
            else File.Move(temp, file);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}
