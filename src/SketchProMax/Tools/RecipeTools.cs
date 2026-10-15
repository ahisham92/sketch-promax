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
        var steps = Run(app, _registry, args.String("name"), args.Has("values") ? args.Raw["values"] : null, args.OptBool("continue_on_error"));
        return steps is null
            ? ToolResult.Error($"No recipe named '{args.String("name")}'. Call list_recipes.")
            : ToolResult.Ok(new { recipe = args.String("name"), steps });
    }

    /// <summary>Runs a recipe on the active model. Returns per-step results, or null when the recipe doesn't exist.</summary>
    public static List<object>? Run(UIApplication app, ToolRegistry registry, string recipe, JsonElement? values, bool keepGoing)
    {
        var path = Recipes.PathFor(recipe);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (values is { ValueKind: JsonValueKind.Object } v)
            foreach (var p in v.EnumerateObject())
                text = text.Replace("{" + p.Name + "}", JsonEncodedText.Encode(p.Value.ToString()).ToString());

        using var doc = JsonDocument.Parse(text);
        var results = new List<object>();
        var index = 0;
        foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
        {
            index++;
            var name = step.GetProperty("tool").GetString() ?? "";
            if (name is "run_recipe" or "batch_run_recipe") { results.Add(new { step = index, tool = name, error = "a recipe can't run another recipe" }); continue; }
            var stepArgs = step.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>();
            // As in the bridge: run the learned tool itself, with its own inputs.
            if (name == "run_learned_tool" && stepArgs.TryGetValue("name", out var inner) && inner.ValueKind == JsonValueKind.String
                && LearnedTools.Load(inner.GetString()!) is { } learned)
            {
                name = learned.Name;
                stepArgs = stepArgs.TryGetValue("arguments", out var la) && la.ValueKind == JsonValueKind.Object
                    ? la.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                    : new Dictionary<string, JsonElement>();
            }
            var tool = registry.Find(name);
            if (tool is null) { results.Add(new { step = index, tool = name, error = "unknown tool" }); if (!keepGoing) break; continue; }
            ToolResult result;
            try { result = tool.Execute(app, new ToolArgs(stepArgs)); }
            catch (Exception ex) { result = ToolResult.Error($"{ex.GetType().Name}: {ex.Message}"); }
            ActivityLog.Write(app.ActiveUIDocument?.Document?.Title ?? "", tool, stepArgs, result);
            results.Add(new { step = index, tool = name, ok = !result.IsError, result = result.Text.Length > 800 ? result.Text.Substring(0, 800) + "…" : result.Text });
            if (result.IsError && !keepGoing) break;
        }
        return results;
    }
}

public sealed class BatchRunRecipeTool : IRevitTool
{
    public string Name => "batch_run_recipe";
    public string Description =>
        "Run a saved recipe on many models in turn: opens each .rvt (from a list or every .rvt in a folder), runs the " +
        "recipe, optionally saves, and closes it. Workshared central models are opened detached and saved as a copy " +
        "only when output_folder is given. Long-running: tell the user Revit will be busy.";
    public string PropertiesJson => """
    {
      "recipe": {"type": "string"},
      "model_paths": {"type": "array", "items": {"type": "string"}},
      "folder": {"type": "string", "description": "Run on every .rvt in this folder (not subfolders)."},
      "values": {"type": "object", "additionalProperties": {"type": "string"}},
      "save": {"type": "boolean", "description": "Save each model after the recipe (default false). Ignored for detached central models."},
      "output_folder": {"type": "string", "description": "Save each result here as a copy instead of overwriting."},
      "continue_on_error": {"type": "boolean"}
    }
    """;
    public string[] Required => ["recipe"];
    public bool ModifiesModel => true;

    private readonly ToolRegistry _registry;
    public BatchRunRecipeTool(ToolRegistry registry) => _registry = registry;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var paths = args.StringList("model_paths");
        if (args.OptString("folder") is { } folder)
            paths.AddRange(Directory.GetFiles(folder, "*.rvt").Where(f => !System.Text.RegularExpressions.Regex.IsMatch(f, @"\.\d{4}\.rvt$")));
        if (paths.Count == 0) return ToolResult.Error("Give model_paths or a folder with .rvt files.");
        if (!File.Exists(Recipes.PathFor(args.String("recipe")))) return ToolResult.Error($"No recipe named '{args.String("recipe")}'.");

        var startDoc = app.ActiveUIDocument?.Document;
        var output = args.OptString("output_folder");
        if (output != null) Directory.CreateDirectory(output);
        var values = args.Has("values") ? args.Raw["values"] : (JsonElement?)null;
        var report = new List<object>();
        Autodesk.Revit.DB.Document? previous = null;

        foreach (var path in paths)
        {
            try
            {
                var modelPath = Autodesk.Revit.DB.ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
                var info = Autodesk.Revit.DB.BasicFileInfo.Extract(path);
                var options = new Autodesk.Revit.DB.OpenOptions();
                if (info.IsWorkshared) options.DetachFromCentralOption = Autodesk.Revit.DB.DetachFromCentralOption.DetachAndPreserveWorksets;
                var uidoc = app.OpenAndActivateDocument(modelPath, options, false);
                CloseQuietly(previous, startDoc);

                var steps = RunRecipeTool.Run(app, _registry, args.String("recipe"), values, args.OptBool("continue_on_error"));
                string saved = "not saved";
                var doc = uidoc.Document;
                if (output != null)
                {
                    var target = Path.Combine(output, Path.GetFileName(path));
                    var save = new Autodesk.Revit.DB.SaveAsOptions { OverwriteExistingFile = true };
                    if (doc.IsWorkshared) save.SetWorksharingOptions(new Autodesk.Revit.DB.WorksharingSaveAsOptions { SaveAsCentral = true });
                    doc.SaveAs(target, save);
                    saved = target;
                }
                else if (args.OptBool("save") && !info.IsWorkshared)
                {
                    doc.Save();
                    saved = "saved";
                }
                report.Add(new { model = path, saved, steps });
                previous = doc;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException or InvalidOperationException)
            {
                report.Add(new { model = path, error = ex.Message });
            }
        }
        return ToolResult.Ok(new { recipe = args.String("recipe"), models = report.Count, report, note = "The last model stays open in Revit." });
    }

    /// <summary>Closes a batch model once another one is active. The model the user started in is never closed.</summary>
    private static void CloseQuietly(Autodesk.Revit.DB.Document? doc, Autodesk.Revit.DB.Document? keep)
    {
        if (doc is null || (keep != null && doc.Equals(keep))) return;
        try { doc.Close(false); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
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
        catch (Exception) { /* logging must never break a tool call */ }
    }
}
