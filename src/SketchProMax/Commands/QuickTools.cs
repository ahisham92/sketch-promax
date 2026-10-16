using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Revit;
using SketchProMax.Tools;
using SketchProMax.UI;

namespace SketchProMax.Commands;

/// <summary>
/// Recipes and learned tools on the ribbon: listed for the Quick tools pulldown, and run straight in Revit without the AI
/// or internet. A run goes the way a call from Claude goes: the same tool (run_recipe, or the learned tool itself), as a
/// job (so Claude sees it and cancel_job stops it), under the failure guard, and into the activity log. The inputs window
/// is the user's go-ahead; a learned tool whose code has not been approved on this PC still asks about its code.
/// </summary>
internal static class QuickTools
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_ ]*)\}");

    // What was typed last time per item, so rerunning is one click.
    private static readonly Dictionary<string, Dictionary<string, string>> LastValues = new();

    private static string _listed = "";
    private static string _signature = "";

    /// <summary>One "kind \t name \t summary" line per recipe and learned tool, recipes first.</summary>
    /// <remarks>Asked by the ribbon every half minute on Revit's thread, so it rereads the files only when a folder changed.</remarks>
    public static string List(bool force = false)
    {
        var signature = Signature(Recipes.Folder) + "|" + Signature(LearnedTools.Folder);
        if (!force && signature == _signature) return _listed;
        _listed = Read();
        _signature = signature;
        return _listed;
    }

    private static string Signature(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder).Ticks.ToString(CultureInfo.InvariantCulture) : "-"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "?"; }
    }

    private static string Read()
    {
        var lines = new List<string>();
        try
        {
            if (Directory.Exists(Recipes.Folder))
                foreach (var file in Directory.GetFiles(Recipes.Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    // Keyed by file name, which is what run_recipe looks the recipe up by.
                    var key = Path.GetFileNameWithoutExtension(file);
                    var recipe = ReadRecipe(key);
                    if (recipe is null || key.IndexOf('\t') >= 0) continue;
                    var summary = recipe.Description ?? $"{recipe.Steps.Count} step(s): {string.Join(", ", recipe.Steps.Take(4))}{(recipe.Steps.Count > 4 ? ", …" : "")}";
                    lines.Add($"recipe\t{key}\t{Clean(summary)}");
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var tool in LearnedTools.All())
            lines.Add($"learned\t{tool.Name}\t{Clean(tool.Summary)}{(tool.ModifiesModel ? " (changes the model)" : "")}");
        return string.Join("\n", lines);
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private sealed record Recipe(string Name, string? Description, List<string> Steps, List<string> Placeholders);

    private static Recipe? ReadRecipe(string name)
    {
        try
        {
            var path = Recipes.PathFor(name);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var steps = root.GetProperty("steps");
            var stepNames = steps.EnumerateArray()
                .Select(s => s.TryGetProperty("tool", out var t) ? t.GetString() ?? "?" : "?").ToList();
            var placeholders = Placeholder.Matches(steps.GetRawText()).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            var display = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : name;
            var description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return new Recipe(display, description, stepNames, placeholders);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static Result Run(UIApplication app, string kind, string name)
    {
        if (app.ActiveUIDocument?.Document is null)
        {
            TaskDialog.Show("Sketch ProMax", "Open a model first.");
            return Result.Cancelled;
        }
        return kind == "recipe" ? RunRecipe(app, name) : RunLearned(app, name);
    }

    private static Result RunRecipe(UIApplication app, string name)
    {
        var recipe = ReadRecipe(name);
        if (recipe is null)
        {
            TaskDialog.Show("Sketch ProMax", $"The recipe '{name}' is gone or can't be read. The list has been refreshed.");
            return Result.Failed;
        }
        var key = "recipe:" + name;
        LastValues.TryGetValue(key, out var last);
        var fields = recipe.Placeholders.Select(p => new InputsWindow.Field(p, Pretty(p), null, "string", true, null,
            last is not null && last.TryGetValue(p, out var v) ? v : null)).ToList();
        var steps = recipe.Steps.Select((s, i) => $"{i + 1}. {s.Replace('_', ' ')}").ToList();
        var window = new InputsWindow($"Run recipe: {recipe.Name}", "A saved workflow, run here without the AI.",
            (recipe.Description is null ? "" : recipe.Description + "\n\n") +
            $"It runs these {recipe.Steps.Count} step(s) on '{app.ActiveUIDocument!.Document.Title}'. Each step is its own Undo entry.",
            steps, fields, "Run", "Keep going after a step fails");
        if (window.ShowDialog() != true) return Result.Cancelled;
        LastValues[key] = new Dictionary<string, string>(window.Values);

        var values = Element(w =>
        {
            w.WriteStartObject();
            foreach (var kv in window.Values) w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
        });
        var args = new Dictionary<string, JsonElement>
        {
            ["name"] = Element(w => w.WriteStringValue(name)),
            ["values"] = values,
            ["continue_on_error"] = Element(w => w.WriteBooleanValue(window.OptionChecked)),
        };
        var result = Execute(app, "run_recipe", args);
        Show($"Recipe: {recipe.Name}", result, RecipeSummary(result));
        return result.IsError ? Result.Failed : Result.Succeeded;
    }

    private static Result RunLearned(UIApplication app, string name)
    {
        var tool = LearnedTools.Load(name);
        if (tool is null)
        {
            TaskDialog.Show("Sketch ProMax", $"The learned tool '{name}' is gone or can't be read. The list has been refreshed.");
            return Result.Failed;
        }
        var key = "learned:" + name;
        LastValues.TryGetValue(key, out var last);
        var fields = new List<InputsWindow.Field>();
        var types = new Dictionary<string, (string Type, string? ItemType)>();
        try
        {
            using var props = JsonDocument.Parse(tool.PropertiesJson);
            foreach (var p in props.RootElement.EnumerateObject())
            {
                var s = p.Value;
                var type = s.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "string";
                var itemType = s.TryGetProperty("items", out var it) && it.TryGetProperty("type", out var itt) && itt.ValueKind == JsonValueKind.String ? itt.GetString() : null;
                var help = s.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (type == "array") help = (help is null ? "" : help + " ") + "Separate several values with commas.";
                if (type == "object") help = (help is null ? "" : help + " ") + "As JSON, e.g. {\"key\": \"value\"}.";
                var choices = s.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).ToArray() : null;
                types[p.Name] = (type, itemType);
                fields.Add(new InputsWindow.Field(p.Name, Pretty(p.Name), help, type, tool.Required.Contains(p.Name), choices,
                    last is not null && last.TryGetValue(p.Name, out var v) ? v : null));
            }
        }
        catch (JsonException) { }

        var window = new InputsWindow($"Run tool: {Pretty(tool.Name)}", "A tool Claude learned, run here without the AI.",
            tool.Summary + "\n\n" + (tool.ModifiesModel
                ? $"It changes '{app.ActiveUIDocument!.Document.Title}'. Ctrl+Z undoes it."
                : "It only reads the model."),
            [], fields, "Run");
        if (window.ShowDialog() != true) return Result.Cancelled;
        LastValues[key] = new Dictionary<string, string>(window.Values);

        var args = new Dictionary<string, JsonElement>();
        var problems = new List<string>();
        foreach (var kv in window.Values)
        {
            var (type, itemType) = types.TryGetValue(kv.Key, out var t) ? t : ("string", null);
            try { args[kv.Key] = Convert(kv.Value, type, itemType); }
            catch (Exception ex) when (ex is FormatException or JsonException or OverflowException) { problems.Add($"{Pretty(kv.Key)}: {ex.Message}"); }
        }
        if (problems.Count > 0)
        {
            TaskDialog.Show("Sketch ProMax", "Some inputs could not be read:\n" + string.Join("\n", problems));
            return Result.Failed;
        }
        var result = Execute(app, tool.Name, args);
        Show($"Tool: {Pretty(tool.Name)}", result, result.IsError ? "It did not work" : "Done");
        return result.IsError ? Result.Failed : Result.Succeeded;
    }

    /// <summary>The model check, read-only, with its defaults.</summary>
    public static Result ModelCheck(UIApplication app)
    {
        if (app.ActiveUIDocument?.Document is null)
        {
            TaskDialog.Show("Sketch ProMax", "Open a model first.");
            return Result.Cancelled;
        }
        var result = Execute(app, "check_model", new Dictionary<string, JsonElement>());
        Show("Model check", result, result.IsError ? "The check did not finish" : $"Checked '{app.ActiveUIDocument.Document.Title}'");
        return result.IsError ? Result.Failed : Result.Succeeded;
    }

    /// <summary>
    /// Export for Claude (ribbon button, or the pane through CoreEntry.Command("export-pack")): writes the project pack
    /// with its defaults as a job, with a progress window that can stop it, then opens its folder with the zip selected.
    /// </summary>
    public static Result ExportPack(UIApplication app)
    {
        if (app.ActiveUIDocument?.Document is not { IsFamilyDocument: false })
        {
            TaskDialog.Show("Sketch ProMax", "Open a project model first.");
            return Result.Cancelled;
        }
        ToolResult result;
        using (PackProgress.Show("Export for Claude", $"Packing '{ProjectPack.ModelName(app.ActiveUIDocument.Document)}' for Claude"))
            result = Execute(app, "export_project_pack", new Dictionary<string, JsonElement>());
        if (result.IsError)
        {
            Show("Export for Claude", result, result.Text.StartsWith("Stopped on request", StringComparison.Ordinal) ? "Stopped; no pack was written" : "The pack was not written");
            return Result.Failed;
        }
        string? file = null;
        try
        {
            using var parsed = JsonDocument.Parse(result.Text);
            if (parsed.RootElement.TryGetProperty("file", out var f)) file = f.GetString();
        }
        catch (JsonException) { }
        if (file is null || !File.Exists(file))
        {
            Show("Export for Claude", result, "Pack written");
            return Result.Succeeded;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Show("Export for Claude", result, "Pack written");
        }
        return Result.Succeeded;
    }

    /// <summary>Runs a tool on Revit's thread, now, as a job: the ribbon's counterpart of the bridge's call path.</summary>
    internal static ToolResult Execute(UIApplication app, string name, Dictionary<string, JsonElement> args)
    {
        var registry = App.Bridge?.Registry ?? ToolRegistry.CreateDefault();
        var tool = registry.Find(name);
        if (tool is null) return ToolResult.Error($"Unknown tool '{name}'.");
        if (Jobs.Running is { } running) return Jobs.Busy(running);
        if (App.Dispatcher.IsRunning || SelfTest.IsRunning) return Jobs.Busy(null);

        var job = Jobs.TryCreate(tool.Name, out var inTheWay);
        if (job is null) return Jobs.Busy(inTheWay);
        var done = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Task = done.Task;
        job.TryStart();
        Jobs.Running = job;
        job.StartedSignal.TrySetResult(true);
        ToolResult result;
        try
        {
            result = FailureGuard.Run(() =>
            {
                try { return tool.Execute(app, new ToolArgs(args)); }
                catch (OperationCanceledException) when (job.CancelRequested) { return ToolResult.Error("Stopped on request. Steps already finished are kept; check the model."); }
                catch (Exception ex) { return ToolResult.Error(BridgeServer.Describe(tool, ex)); }
            });
            ActivityLog.Write(app.ActiveUIDocument?.Document?.Title ?? "", tool, args, result);
        }
        finally
        {
            job.Finished = DateTime.UtcNow;
            Jobs.Running = null;
        }
        done.TrySetResult(result);
        return result;
    }

    private static void Show(string title, ToolResult result, string headline)
    {
        var window = new ResultWindow(title, !result.IsError, headline, Readable(result.Text));
        window.Show();
    }

    private static string RecipeSummary(ToolResult result)
    {
        if (result.IsError) return "The recipe did not run";
        try
        {
            using var doc = JsonDocument.Parse(result.Text);
            var steps = doc.RootElement.GetProperty("steps").EnumerateArray().ToList();
            var ok = steps.Count(s => s.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True);
            return ok == steps.Count ? $"All {ok} step(s) worked" : $"{ok} of {steps.Count} step(s) worked";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return "Finished"; }
    }

    /// <summary>JSON results indented for reading; anything else as it is.</summary>
    private static string Readable(string text)
    {
        var trimmed = text.TrimStart();
        if (!(trimmed.StartsWith("{") || trimmed.StartsWith("["))) return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (JsonException) { return text; }
    }

    private static JsonElement Convert(string text, string type, string? itemType)
    {
        switch (type)
        {
            case "integer":
                return Element(w => w.WriteNumberValue(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)));
            case "number":
                return Element(w => w.WriteNumberValue(ParseNumber(text)));
            case "boolean":
                return Element(w => w.WriteBooleanValue(text is "true" or "True" or "yes" or "1"));
            case "object":
                return Parsed(text);
            case "array":
                if (text.TrimStart().StartsWith("[")) return Parsed(text);
                var parts = text.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                return Element(w =>
                {
                    w.WriteStartArray();
                    foreach (var p in parts)
                    {
                        if (itemType == "integer") w.WriteNumberValue(long.Parse(p, NumberStyles.Integer, CultureInfo.InvariantCulture));
                        else if (itemType == "number") w.WriteNumberValue(ParseNumber(p));
                        else w.WriteStringValue(p);
                    }
                    w.WriteEndArray();
                });
            default:
                return Element(w => w.WriteStringValue(text));
        }
    }

    /// <summary>Accepts 2.5 and, on a comma-decimal PC, 2,5.</summary>
    private static double ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
        : double.Parse(text, NumberStyles.Float, CultureInfo.CurrentCulture);

    private static JsonElement Parsed(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement Element(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) write(w);
        // Parsed from a string: JsonDocument.Parse(byte[]) binds the ReadOnlyMemory<byte> overload, which names
        // System.Memory in this DLL and fails when Revit 2024 hands this DLL and System.Text.Json different copies of it.
        using var parsed = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        return parsed.RootElement.Clone();
    }

    private static string Pretty(string name)
    {
        var text = name.Replace('_', ' ').Trim();
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
