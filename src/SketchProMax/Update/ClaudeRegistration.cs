using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SketchProMax.Update;

/// <summary>
/// Makes sure the Claude desktop app knows about the Sketch ProMax connector, every time Revit starts.
/// This repairs installs where the app was installed after Sketch ProMax, or keeps its config somewhere else
/// (the Microsoft Store / MSIX build uses %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude).
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
        foreach (var package in Directory.GetDirectories(packages, "Claude*"))
            yield return Path.Combine(package, "LocalCache", "Roaming", "Claude");
    }

    /// <summary>Adds or repairs the connector entry. Returns the config files that were changed.</summary>
    public static List<string> Ensure()
    {
        var changed = new List<string>();
        if (!File.Exists(ConnectorPath)) return changed;
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
                if (File.Exists(file)) File.Copy(file, file + ".bak", true);
                File.WriteAllText(file, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                changed.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        return changed;
    }
}
