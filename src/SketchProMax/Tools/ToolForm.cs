using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// The options window of a tool run straight from the ribbon, worked out from the tool's input schema: one field per
/// property (text, number, yes/no, a list to pick from for an enum, comma-separated values for an array, JSON for an
/// object), required ones marked, element-id inputs able to take the current selection. Turns what was typed back into
/// the tool's arguments, with a plain message per field that can't be read. Pure: no Revit, no WPF, so the tests run it.
/// </summary>
internal static class ToolForm
{
    /// <param name="Type">string, integer, number, boolean, array or object.</param>
    /// <param name="ItemType">For an array: the type of its items.</param>
    /// <param name="Default">The schema's default as text, or null.</param>
    /// <param name="ElementIds">An element id (or a list of them): the window offers "Use current selection".</param>
    internal sealed record Field(string Key, string Label, string? Help, string Type, string? ItemType, bool Required,
        string[]? Choices, string? Default, bool ElementIds);

    /// <summary>The fields of a tool, in schema order. A schema that can't be read gives no fields.</summary>
    public static List<Field> Fields(string propertiesJson, IEnumerable<string> required)
    {
        var must = new HashSet<string>(required, StringComparer.Ordinal);
        var fields = new List<Field>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(propertiesJson) ? "{}" : propertiesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return fields;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var s = p.Value;
                if (s.ValueKind != JsonValueKind.Object) continue;
                var type = TypeOf(s) ?? "string";
                string? itemType = null;
                string[]? itemChoices = null;
                if (s.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                {
                    itemType = TypeOf(items);
                    itemChoices = Choices(items);
                }
                var help = s.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (type == "array" && itemType is "object" or "array")
                    help = Join(help, "As a JSON list, for example [{\"x\": 0, \"y\": 0}].");
                else if (type == "array")
                    help = Join(help, itemChoices is { Length: > 0 }
                        ? $"Separate several values with commas. Options: {string.Join(", ", itemChoices)}."
                        : "Separate several values with commas.");
                else if (type == "object") help = Join(help, "As JSON, for example {\"key\": \"value\"}.");
                var choices = type is "array" or "object" or "boolean" ? null : Choices(s);
                fields.Add(new Field(p.Name, Label(p.Name), help, type, itemType, must.Contains(p.Name), choices,
                    s.TryGetProperty("default", out var def) ? Text(def) : null, IsElementIds(p.Name, type, itemType)));
            }
        }
        catch (JsonException) { }
        return fields;
    }

    /// <summary>
    /// True for an input that holds element ids: "element_ids", "ids", "wall_id", "host_ids"... with whole numbers (or
    /// strings, which some tools accept as ids). Not job numbers.
    /// </summary>
    public static bool IsElementIds(string key, string type, string? itemType)
    {
        var k = key.ToLowerInvariant();
        if (k is "job_id" or "job_ids") return false;
        var named = k is "id" or "ids" || k.EndsWith("_id", StringComparison.Ordinal) || k.EndsWith("_ids", StringComparison.Ordinal);
        if (!named) return false;
        return type is "integer" or "string" || type == "array" && itemType is null or "integer" or "string";
    }

    /// <summary>
    /// What a field starts with: the value used last time, else the schema's default, else (for a required list) its
    /// first choice. Element ids never come back from last time: they belong to the model and selection of that run.
    /// </summary>
    public static string? Initial(Field field, IReadOnlyDictionary<string, string>? remembered)
    {
        if (!field.ElementIds && remembered is not null && remembered.TryGetValue(field.Key, out var last))
        {
            if (field.Choices is not { Length: > 0 } choices || choices.Contains(last)) return last;
        }
        if (field.Default is { } d) return d;
        return field.Required && field.Choices is { Length: > 0 } c ? c[0] : null;
    }

    /// <summary>
    /// True for an optional yes/no with nothing to start from (no default in the schema, nothing remembered): shown as
    /// a list with an empty entry, so the tool's own default applies unless the user picks yes or no.
    /// </summary>
    public static bool AsYesNoList(Field field, string? initial) => field.Type == "boolean" && !field.Required && initial is null;

    /// <summary>
    /// The tool's arguments as a JSON object, from what was typed (empty values are left out, so the tool's own
    /// defaults apply). Null when something is missing or can't be read; <paramref name="problems"/> then says what,
    /// one line per field, in words the user can act on.
    /// </summary>
    public static string? Arguments(IReadOnlyList<Field> fields, IReadOnlyDictionary<string, string> values, List<string> problems)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var f in fields)
        {
            var text = values.TryGetValue(f.Key, out var v) ? v.Trim() : "";
            if (text.Length == 0)
            {
                if (f.Required) problems.Add($"{f.Label}: fill this in.");
                continue;
            }
            string json;
            try { json = Value(f, text); }
            catch (FormatException ex) { problems.Add($"{f.Label}: {ex.Message}"); continue; }
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(f.Key)).Append(':').Append(json);
        }
        sb.Append('}');
        return problems.Count == 0 ? sb.ToString() : null;
    }

    /// <summary>The arguments as the tools take them.</summary>
    public static Dictionary<string, JsonElement> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>"element_ids" → "Element ids".</summary>
    /// <summary>base_level, top_level, level, levels... but not a level offset or a level count.</summary>
    public static bool IsLevel(string key) =>
        key.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0
        && !(key.IndexOf("offset", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("count", StringComparison.OrdinalIgnoreCase) >= 0
             || key.IndexOf("elevation", StringComparison.OrdinalIgnoreCase) >= 0);

    public static string Label(string key)
    {
        var text = key.Replace('_', ' ').Trim();
        return text.Length == 0 ? key : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    private static string Value(Field f, string text)
    {
        switch (f.Type)
        {
            case "integer": return Integer(text);
            case "number": return Number(text);
            case "boolean": return Boolean(text);
            case "object":
                if (!IsJson(text, JsonValueKind.Object)) throw new FormatException("type it as JSON, for example {\"key\": \"value\"}.");
                return text;
            case "array":
                if (f.ItemType is "object" or "array" || text.StartsWith("[", StringComparison.Ordinal))
                {
                    if (!IsJson(text, JsonValueKind.Array))
                        throw new FormatException(f.ItemType is "object" or "array"
                            ? "type it as a JSON list, for example [{\"x\": 0, \"y\": 0}]."
                            : "type the values separated by commas, for example 1, 2, 3.");
                    return text;
                }
                var parts = text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0);
                return "[" + string.Join(",", parts.Select(p => f.ItemType switch
                {
                    "integer" => Integer(p),
                    "number" => Number(p),
                    "boolean" => Boolean(p),
                    _ => JsonSerializer.Serialize(p),
                })) + "]";
            default:
                if (f.Choices is { Length: > 0 } choices && !choices.Contains(text))
                    throw new FormatException($"pick one of {string.Join(", ", choices)}.");
                return JsonSerializer.Serialize(text);
        }
    }

    private static string Integer(string text)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n.ToString(CultureInfo.InvariantCulture);
        throw new FormatException($"'{text}' is not a whole number; type one such as 3.");
    }

    /// <summary>Accepts 2.5 and, on a comma-decimal PC, 2,5.</summary>
    private static string Number(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            if (!double.IsNaN(v) && !double.IsInfinity(v)) return v.ToString("R", CultureInfo.InvariantCulture);
        throw new FormatException($"'{text}' is not a number; type one such as 2.5.");
    }

    private static string Boolean(string text) => text.ToLowerInvariant() switch
    {
        "true" or "yes" or "1" => "true",
        "false" or "no" or "0" => "false",
        _ => throw new FormatException($"'{text}' is not yes or no."),
    };

    private static bool IsJson(string text, JsonValueKind kind)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == kind;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The schema type; for a union such as ["string", "null"], the first that isn't null.</summary>
    private static string? TypeOf(JsonElement s)
    {
        if (!s.TryGetProperty("type", out var t)) return null;
        if (t.ValueKind == JsonValueKind.String) return t.GetString();
        if (t.ValueKind == JsonValueKind.Array)
            return t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).FirstOrDefault(x => x != "null");
        return null;
    }

    private static string[]? Choices(JsonElement s) =>
        s.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(Text).Where(x => x is not null).Select(x => x!).ToArray()
            : null;

    /// <summary>A schema value as the text a field shows: arrays as a comma list.</summary>
    private static string? Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Text).Where(x => x is not null)),
        JsonValueKind.Object => v.GetRawText(),
        _ => null,
    };

    private static string Join(string? a, string b) => string.IsNullOrWhiteSpace(a) ? b : a!.TrimEnd() + " " + b;
}

/// <summary>
/// What was typed last time for each tool run from the ribbon (%APPDATA%\SketchProMax\tool-defaults.json), so running
/// it again is one click. A missing or damaged file is simply an empty memory.
/// </summary>
internal sealed class ToolDefaults
{
    private const int MaxTools = 300, MaxValue = 4000;
    private readonly string _path;
    private readonly Dictionary<string, Dictionary<string, string>> _byTool;

    private ToolDefaults(string path, Dictionary<string, Dictionary<string, string>> byTool)
    {
        _path = path;
        _byTool = byTool;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "tool-defaults.json");

    public static ToolDefaults Load(string? path = null)
    {
        path ??= DefaultPath;
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var tool in doc.RootElement.EnumerateObject())
                    {
                        if (tool.Value.ValueKind != JsonValueKind.Object) continue;
                        var values = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var v in tool.Value.EnumerateObject())
                            if (v.Value.ValueKind == JsonValueKind.String) values[v.Name] = v.Value.GetString()!;
                        map[tool.Name] = values;
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new ToolDefaults(path, map);
    }

    public IReadOnlyDictionary<string, string>? For(string tool) => _byTool.TryGetValue(tool, out var v) ? v : null;

    /// <summary>Keeps <paramref name="values"/> as the tool's last values (replacing the earlier ones), leaving out element ids.</summary>
    public void Remember(string tool, IReadOnlyList<ToolForm.Field> fields, IReadOnlyDictionary<string, string> values)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in fields)
            if (!f.ElementIds && values.TryGetValue(f.Key, out var v) && v.Length <= MaxValue) kept[f.Key] = v;
        _byTool[tool] = kept;
        while (_byTool.Count > MaxTools) _byTool.Remove(_byTool.Keys.First(k => k != tool));
    }

    /// <summary>Writes the file (through a temporary file, so a crash never leaves half of it). False when it couldn't.</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_byTool, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(temp, _path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
