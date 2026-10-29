using System.IO;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// The export settings last used for a model (PDF and DWG export setups, the file name pattern, DWG units and
/// coordinates), used again when a later export leaves them out. A submission is exported many times while its
/// drawings are fixed, and each time the setup, the "-00" naming and the units were worked out again (2026-10-15 to
/// 10-28: "use the printer settings I just did for A0 and colour", "keep it always as default for exporting to PDF").
/// Kept per model in %APPDATA%\SketchProMax\export-defaults.json; the folder is never reused (each package has its own).
/// No Revit here, so the tests can run it.
/// </summary>
internal static class ExportDefaults
{
    /// <summary>The settings remembered, with the export they belong to.</summary>
    public static readonly string[] Keys = ["pdf_setup", "dwg_setup", "name_pattern", "dwg_units", "dwg_coordinates"];

    public static string File => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "export-defaults.json");

    private static readonly object Gate = new();

    /// <summary>All models' settings from the file's text; empty on anything unreadable.</summary>
    public static Dictionary<string, Dictionary<string, string>> Parse(string? json)
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return all;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return all;
            foreach (var model in doc.RootElement.EnumerateObject())
            {
                if (model.Value.ValueKind != JsonValueKind.Object) continue;
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in model.Value.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && Keys.Contains(p.Name) && p.Value.GetString() is { Length: > 0 } v) values[p.Name] = v;
                if (values.Count > 0) all[model.Name] = values;
            }
        }
        catch (JsonException) { }
        return all;
    }

    public static string Write(Dictionary<string, Dictionary<string, string>> all) =>
        JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The settings remembered for <paramref name="model"/> (empty when none).</summary>
    public static Dictionary<string, string> For(string model)
    {
        lock (Gate)
        {
            try { return Parse(SafeFile.Read(File)).TryGetValue(model, out var v) ? v : new Dictionary<string, string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new Dictionary<string, string>(); }
        }
    }

    /// <summary>Remembers the settings this export was given (only the ones given; the others stay).</summary>
    public static void Remember(string model, IReadOnlyDictionary<string, string?> given)
    {
        var used = given.Where(kv => Keys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)).ToList();
        if (used.Count == 0 || string.IsNullOrEmpty(model)) return;
        lock (Gate)
        {
            try
            {
                var all = Parse(SafeFile.Read(File));
                if (!all.TryGetValue(model, out var mine)) all[model] = mine = new Dictionary<string, string>(StringComparer.Ordinal);
                var changed = false;
                foreach (var kv in used)
                    if (!mine.TryGetValue(kv.Key, out var old) || old != kv.Value!.Trim()) { mine[kv.Key] = kv.Value!.Trim(); changed = true; }
                if (changed) SafeFile.Write(File, Write(all));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* remembered next time */ }
        }
    }

    /// <summary>
    /// The value to use: the one given, else the one remembered (and noted in <paramref name="used"/>), else null.
    /// <paramref name="exists"/> says whether a remembered value still exists in the model (a deleted setup is skipped).
    /// </summary>
    public static string? Pick(string key, string? given, Dictionary<string, string> remembered, List<string> used, Func<string, bool>? exists = null)
    {
        if (!string.IsNullOrWhiteSpace(given)) return given;
        if (!remembered.TryGetValue(key, out var value) || exists?.Invoke(value) == false) return null;
        used.Add($"{key} '{value}'");
        return value;
    }
}
