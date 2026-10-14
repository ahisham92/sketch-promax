using System.IO;
using System.Text.Json;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// Recipes: a saved list of tool calls that worked, replayed later without the AI (fast, repeatable, no plan usage).
/// Stored as JSON in %APPDATA%\SketchProMax\recipes so they can be shared with colleagues by copying the file.
/// Values written as {placeholder} in the saved arguments are filled in when the recipe runs.
/// </summary>
internal static class Recipes
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "recipes");

    public static string PathFor(string name)
    {
        var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Folder, safe + ".json");
    }
}

public sealed class SaveRecipeTool : IRevitTool
{
    public string Name => "save_recipe";
    public string Description =>
        "Save a sequence of tool calls as a named recipe the user can rerun later without the AI (run_recipe). " +
        "Use it after a run the user was happy with, or when they ask to save a workflow. Put {placeholders} in " +
        "argument values that should change between runs (e.g. {level}).";
    public string PropertiesJson => """
    {
      "name": {"type": "string"},
      "description": {"type": "string"},
      "steps": {"type": "array", "items": {"type": "object", "properties": {"tool": {"type": "string"}, "arguments": {"type": "object"}}, "required": ["tool"]}}
    }
    """;
    public string[] Required => ["name", "steps"];
    public bool ModifiesModel => false;

    private readonly ToolRegistry _registry;
    public SaveRecipeTool(ToolRegistry registry) => _registry = registry;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var steps = args.Array("steps").ToList();
        var unknown = steps.Select(s => s.GetProperty("tool").GetString() ?? "").Where(n => _registry.Find(n) is null).ToList();
        if (unknown.Count > 0) return ToolResult.Error($"Unknown tools: {string.Join(", ", unknown)}");
        Directory.CreateDirectory(Recipes.Folder);
        var path = Recipes.PathFor(args.String("name"));
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            name = args.String("name"),
            description = args.OptString("description"),
            saved = DateTime.Now,
            steps,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return ToolResult.Ok(new { saved = path, steps = steps.Count });
    }
}

public sealed class ListRecipesTool : IRevitTool
{
    public string Name => "list_recipes";
    public string Description => "List saved recipes with their descriptions, steps and placeholders.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        if (!Directory.Exists(Recipes.Folder)) return ToolResult.Ok(new { recipes = Array.Empty<object>(), folder = Recipes.Folder });
        var list = new List<object>();
        foreach (var file in Directory.GetFiles(Recipes.Folder, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var text = root.GetProperty("steps").GetRawText();
                var placeholders = System.Text.RegularExpressions.Regex.Matches(text, @"\{([A-Za-z_][A-Za-z0-9_ ]*)\}")
                    .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
                list.Add(new
                {
                    name = root.GetProperty("name").GetString(),
                    description = root.TryGetProperty("description", out var d) ? d.GetString() : null,
                    steps = root.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("tool").GetString()).ToList(),
                    placeholders,
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
        }
        return ToolResult.Ok(new { recipes = list, folder = Recipes.Folder });
    }
}

public sealed class RunRecipeTool : IRevitTool
{
    public string Name => "run_recipe";
    public string Description =>
        "Run a saved recipe step by step in the active model, filling {placeholders} from 'values'. Stops at the first " +
        "failing step unless continue_on_error is set. Each step is its own Undo entry.";
    public string PropertiesJson => """
    {
      "name": {"type": "string"},
      "values": {"type": "object", "additionalProperties": {"type": "string"}},
      "continue_on_error": {"type": "boolean"}
    }
    """;
    public string[] Required => ["name"];
    public bool ModifiesModel => true;

    private readonly ToolRegistry _registry;
    public RunRecipeTool(ToolRegistry registry) => _registry = registry;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var path = Recipes.PathFor(args.String("name"));
        if (!File.Exists(path)) return ToolResult.Error($"No recipe named '{args.String("name")}'. Call list_recipes.");
        var text = File.ReadAllText(path);
        if (args.Has("values"))
            foreach (var p in args.Raw["values"].EnumerateObject())
                text = text.Replace("{" + p.Name + "}", JsonEncodedText.Encode(p.Value.ToString()).ToString());

        using var doc = JsonDocument.Parse(text);
        var results = new List<object>();
        var keepGoing = args.OptBool("continue_on_error");
        var index = 0;
        foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
        {
            index++;
            var name = step.GetProperty("tool").GetString() ?? "";
            if (name == Name) { results.Add(new { step = index, tool = name, error = "a recipe can't run another recipe" }); continue; }
            var tool = _registry.Find(name);
            if (tool is null) { results.Add(new { step = index, tool = name, error = "unknown tool" }); if (!keepGoing) break; continue; }
            var stepArgs = step.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>();
            ToolResult result;
            try { result = tool.Execute(app, new ToolArgs(stepArgs)); }
            catch (Exception ex) { result = ToolResult.Error($"{ex.GetType().Name}: {ex.Message}"); }
            results.Add(new { step = index, tool = name, ok = !result.IsError, result = result.Text.Length > 800 ? result.Text.Substring(0, 800) + "…" : result.Text });
            if (result.IsError && !keepGoing) break;
        }
        return ToolResult.Ok(new { recipe = args.String("name"), steps = results });
    }
}

public sealed class GetActivityLogTool : IRevitTool
{
    public string Name => "get_activity_log";
    public string Description =>
        "Read Sketch ProMax's activity log: every tool Claude ran, when, on which model, and whether it worked. " +
        "Use it to report what was changed today, or to build a recipe from a successful session.";
    public string PropertiesJson => """
    {
      "date": {"type": "string", "description": "yyyy-MM-dd, default today."},
      "limit": {"type": "integer", "description": "Most recent entries, default 100."},
      "changes_only": {"type": "boolean"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var date = args.OptString("date") ?? DateTime.Now.ToString("yyyy-MM-dd");
        var file = ActivityLog.FileFor(date);
        if (!File.Exists(file)) return ToolResult.Ok(new { date, entries = Array.Empty<object>(), file });
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0);
        if (args.OptBool("changes_only")) lines = lines.Where(l => l.Contains("\"changes\":true"));
        var limit = (int)(args.OptLong("limit") ?? 100);
        var entries = lines.Reverse().Take(limit).Reverse().Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        return ToolResult.Ok(new { date, file, entries });
    }
}

/// <summary>Appends one JSON line per tool call to %APPDATA%\SketchProMax\logs\yyyy-MM-dd.jsonl.</summary>
internal static class ActivityLog
{
    private static readonly object Gate = new();

    public static string FileFor(string date) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "logs", date + ".jsonl");

    public static void Write(string model, IRevitTool tool, IReadOnlyDictionary<string, JsonElement> args, ToolResult result)
    {
        try
        {
            var argText = JsonSerializer.Serialize(args);
            var line = JsonSerializer.Serialize(new
            {
                time = DateTime.Now.ToString("s"),
                model,
                tool = tool.Name,
                changes = tool.ModifiesModel,
                ok = !result.IsError,
                arguments = argText.Length > 4000 ? argText.Substring(0, 4000) + "…" : argText,
                result = result.Text.Length > 500 ? result.Text.Substring(0, 500) + "…" : result.Text,
            });
            var file = FileFor(DateTime.Now.ToString("yyyy-MM-dd"));
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch (IOException) { /* logging must never break a tool call */ }
    }
}
