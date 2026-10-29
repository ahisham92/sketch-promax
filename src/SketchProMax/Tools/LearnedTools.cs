using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// Learned tools: when no dedicated tool fits and run_revit_code solves a request, Claude saves that code as a new
/// named tool with its own parameters. Saved tools are listed to Claude as ordinary tools in every later chat, in every
/// Revit window, so the add-in grows with use. Stored as JSON in %APPDATA%\SketchProMax\learned-tools, one file per
/// tool, so they can be shared with colleagues by copying the files.
/// </summary>
internal static class LearnedTools
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "learned-tools");

    public static readonly Regex ValidName = new("^[a-z][a-z0-9_]{2,47}$");

    public static string PathFor(string name) => Path.Combine(Folder, name + ".json");

    public static LearnedTool? Load(string name)
    {
        if (!ValidName.IsMatch(name)) return null;
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            // A copied or renamed file must not run under a name other than its own, or replace a built-in tool's name.
            var tool = LearnedTool.FromJson(File.ReadAllText(path));
            return tool.Name == name ? tool : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or FormatException) { return null; }
    }

    // Learned tools are plain files anyone can drop into the folder, so the first run of any code asks the user.
    // Approved code is remembered by its SHA-256, so editing a tool asks again.
    private static readonly object ApprovalGate = new();

    // Kept beside the learned-tools folder, not in it: copying that folder to a colleague must not carry approvals,
    // and no learned tool can be saved over it.
    public static string ApprovedFile => Path.Combine(Path.GetDirectoryName(Folder)!, "learned-tools-approved.json");

    public static string Hash(string code)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(code))).Replace("-", "").ToLowerInvariant();
    }

    public static bool IsApproved(string code)
    {
        lock (ApprovalGate) return ReadApproved().Contains(Hash(code));
    }

    public static void MarkApproved(string code)
    {
        lock (ApprovalGate)
        {
            var approved = ReadApproved();
            if (!approved.Add(Hash(code))) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ApprovedFile)!);
                var temp = ApprovedFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(approved.OrderBy(h => h, StringComparer.Ordinal)));
                if (File.Exists(ApprovedFile)) File.Replace(temp, ApprovedFile, null);
                else File.Move(temp, ApprovedFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* asked again next time */ }
        }
    }

    private static HashSet<string> ReadApproved()
    {
        try
        {
            if (File.Exists(ApprovedFile))
                return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(ApprovedFile)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    public static List<LearnedTool> All()
    {
        var list = new List<LearnedTool>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var file in Directory.GetFiles(Folder, "*.json"))
        {
            var tool = Load(Path.GetFileNameWithoutExtension(file));
            if (tool is not null) list.Add(tool);
        }
        return list.OrderBy(t => t.Name).ToList();
    }

    /// <summary>Converts JSON arguments to plain .NET values a snippet can use without System.Text.Json.</summary>
    public static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
        _ => null,
    };

    /// <summary>
    /// True when the code opens its own transactions (or an edit scope), so it must run without the one the runner
    /// would open around it: inside an open transaction Revit refuses "Starting a new transaction is not permitted"
    /// (2026-10-28: two saved join tools failed six times this way).
    /// </summary>
    public static bool OpensOwnTransactions(string code) => OwnTransaction.IsMatch(code);

    private static readonly Regex OwnTransaction = new(@"new\s+(Autodesk\.Revit\.DB\.)?(Transaction|TransactionGroup)\s*\(|EditScope\b");
}

/// <summary>One saved tool. The MCP server reads the same file to list it, so the JSON shape is shared.</summary>
public sealed class LearnedTool : IRevitTool
{
    public string Name { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public string Code { get; private set; } = "";
    public string PropertiesJson { get; private set; } = "{}";
    public string[] Required { get; private set; } = [];
    public bool ModifiesModel { get; private set; }
    /// <summary>The code starts and commits its own transactions; the run is still one Undo step (a group).</summary>
    public bool OwnTransactions { get; private set; }
    public int Uses { get; private set; }
    public DateTime Saved { get; private set; }

    public string Description => "[Learned tool] " + Summary;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        // The bridge asks before calling; this covers every other way in (recipes, for one).
        if (!LearnedTools.IsApproved(Code) && !Bridge.BridgeServer.Approve(this, args.Raw))
            return ToolResult.Error($"The user did not approve running the learned tool '{Name}'. Ask them how to proceed.");
        var values = args.Raw.ToDictionary(p => p.Key, p => LearnedTools.Plain(p.Value));
        // The same time cap as run_revit_code, so a saved tool can't keep Revit frozen either.
        var result = RevitCodeToolBase.RunScript(app, Code, Name.Replace('_', ' '), ModifiesModel, values, OwnTransactions,
            RevitCodeToolBase.CapSeconds);
        if (!result.IsError) CountUse();
        else result = result with
        {
            Text = result.Text + $"\nThis is the learned tool '{Name}'. Fix it: write corrected code with run_revit_code, " +
                   "and once it works, save it again with save_as_tool under the same name.",
        };
        return result;
    }

    private void CountUse()
    {
        try
        {
            if (!LearnedTools.ValidName.IsMatch(Name)) return;
            Uses++;
            File.WriteAllText(LearnedTools.PathFor(Name), ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the count is only informative */ }
    }

    public static LearnedTool Create(string name, string summary, string code, string propertiesJson, string[] required,
        bool modifiesModel, int uses, bool? ownTransactions = null) => new()
    {
        Name = name, Summary = summary, Code = code, PropertiesJson = propertiesJson, Required = required,
        ModifiesModel = modifiesModel, Uses = uses, Saved = DateTime.Now,
        OwnTransactions = ownTransactions ?? LearnedTools.OpensOwnTransactions(code),
    };

    public string ToJson()
    {
        using var props = JsonDocument.Parse(PropertiesJson);
        return JsonSerializer.Serialize(new
        {
            name = Name,
            description = ToolSchema.Description(this),
            summary = Summary,
            modifiesModel = ModifiesModel,
            ownTransactions = OwnTransactions,
            inputSchema = new { type = "object", properties = props.RootElement, required = Required },
            code = Code,
            uses = Uses,
            saved = Saved,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static LearnedTool FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var schema = r.GetProperty("inputSchema");
        var name = r.GetProperty("name").GetString() ?? "";
        if (!LearnedTools.ValidName.IsMatch(name)) throw new FormatException($"'{name}' is not a valid learned tool name.");
        return new LearnedTool
        {
            Name = name,
            Summary = r.GetProperty("summary").GetString() ?? "",
            Code = r.GetProperty("code").GetString()!,
            ModifiesModel = r.GetProperty("modifiesModel").GetBoolean(),
            // Tools saved before 0.35.43 have no flag: tell from the code.
            OwnTransactions = r.TryGetProperty("ownTransactions", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? o.GetBoolean() : LearnedTools.OpensOwnTransactions(r.GetProperty("code").GetString() ?? ""),
            PropertiesJson = schema.TryGetProperty("properties", out var p) ? p.GetRawText() : "{}",
            Required = schema.TryGetProperty("required", out var q) && q.ValueKind == JsonValueKind.Array
                ? q.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
            Uses = r.TryGetProperty("uses", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0,
            Saved = r.TryGetProperty("saved", out var s) && s.TryGetDateTime(out var d) ? d : DateTime.MinValue,
        };
    }
}

public sealed class SaveAsToolTool : IRevitTool
{
    public string Name => "save_as_tool";
    public string Description =>
        "Save working Revit API code as a new reusable tool, so next time the request has a dedicated tool. Use it after " +
        "run_revit_code or run_revit_query_code solved something no existing tool covers and that is likely to come up " +
        "again: generalise the code so whatever changes between uses (names, levels, sizes, ids) comes from 'args', a " +
        "dictionary of the tool's inputs (args[\"width_mm\"] is a long, double, string, bool, List<object> or " +
        "Dictionary<string, object>; use Convert.ToDouble etc.). Saving under an existing learned tool's name replaces it, " +
        "which is how a learned tool is fixed or improved. The code is compiled before saving.";
    public string PropertiesJson => """
    {
      "name": {"type": "string", "description": "snake_case, 3 to 48 characters, verb first, e.g. renumber_parking_spaces"},
      "description": {"type": "string", "description": "What the tool does and when to use it, written for Claude choosing tools in a later chat. Mention units."},
      "code": {"type": "string", "description": "C# method body, as for run_revit_code, reading its inputs from args. Must end with a return statement."},
      "parameters": {"type": "object", "description": "JSON Schema 'properties' for the inputs, e.g. {\"level\": {\"type\": \"string\", \"description\": \"Level name\"}}"},
      "required": {"type": "array", "items": {"type": "string"}},
      "changes_model": {"type": "boolean", "description": "True when the code changes the model. It then runs in one transaction behind the same approval as run_revit_code."},
      "own_transactions": {"type": "boolean", "description": "True when the code starts and commits its own transactions (as with own_transactions in run_revit_code: one transaction per item, so one refused item doesn't undo the rest). Default: worked out from the code."}
    }
    """;
    public string[] Required => ["name", "description", "code", "changes_model"];
    public bool ModifiesModel => false;

    private readonly ToolRegistry _registry;
    public SaveAsToolTool(ToolRegistry registry) => _registry = registry;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var name = args.String("name").Trim();
        if (!LearnedTools.ValidName.IsMatch(name))
            return ToolResult.Error("The name must be snake_case: lowercase letters, digits and underscores, 3 to 48 characters, starting with a letter.");
        if (_registry.IsBuiltIn(name))
            return ToolResult.Error($"'{name}' is a built-in tool. Choose another name.");

        var code = args.String("code");
        var (run, errors) = ScriptCompiler.Compile(code);
        if (run is null) return ToolResult.Error("The code did not compile, so it was not saved:\n" + errors);

        var properties = args.Has("parameters") ? args.Raw["parameters"] : default;
        if (properties.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            return ToolResult.Error("'parameters' must be a JSON Schema properties object.");
        var propertiesJson = properties.ValueKind == JsonValueKind.Object ? properties.GetRawText() : "{}";
        var names = properties.ValueKind == JsonValueKind.Object ? properties.EnumerateObject().Select(p => p.Name).ToHashSet() : [];
        var required = args.StringList("required");
        var missing = required.Where(r => !names.Contains(r)).ToList();
        if (missing.Count > 0) return ToolResult.Error($"Required inputs not described in 'parameters': {string.Join(", ", missing)}");

        var previous = LearnedTools.Load(name);
        var tool = LearnedTool.Create(name, args.String("description"), code, propertiesJson, required.ToArray(),
            args.OptBool("changes_model"), previous?.Uses ?? 0, args.Has("own_transactions") ? args.OptBool("own_transactions") : (bool?)null);
        Directory.CreateDirectory(LearnedTools.Folder);
        File.WriteAllText(LearnedTools.PathFor(name), tool.ToJson());
        return ToolResult.Ok(new
        {
            saved = name,
            replaced = previous is not null,
            file = LearnedTools.PathFor(name),
            note = "The tool is available now and in every later chat. Tell the user in one line that you saved it.",
        });
    }
}

public sealed class ListLearnedToolsTool : IRevitTool
{
    public string Name => "list_learned_tools";
    public string Description =>
        "List the tools Claude has learned (saved with save_as_tool): name, description, inputs, how often each was used, " +
        "and optionally the code. Use it when the user asks what has been learned, before editing one, or when a learned " +
        "tool does not appear in the tool list yet (call it through run_learned_tool).";
    public string PropertiesJson => """
    {
      "include_code": {"type": "boolean"},
      "name": {"type": "string", "description": "Only this tool"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var withCode = args.OptBool("include_code");
        var only = args.OptString("name");
        var tools = LearnedTools.All().Where(t => only is null || t.Name == only).Select(t => new
        {
            name = t.Name,
            description = t.Summary,
            changesModel = t.ModifiesModel,
            inputs = JsonDocument.Parse(t.PropertiesJson).RootElement,
            required = t.Required,
            uses = t.Uses,
            saved = t.Saved,
            code = withCode ? t.Code : null,
        }).ToList();
        return ToolResult.Ok(new { tools, folder = LearnedTools.Folder });
    }
}

public sealed class RunLearnedToolTool : IRevitTool
{
    public string Name => "run_learned_tool";
    public string Description =>
        "Run a learned tool by name with its inputs. Normally learned tools appear as their own tools; use this when one " +
        "was saved in this chat but the Claude app has not refreshed its tool list yet.";
    public string PropertiesJson => """
    {
      "name": {"type": "string"},
      "arguments": {"type": "object", "description": "The tool's inputs"}
    }
    """;
    public string[] Required => ["name"];
    // Approval for model changes is asked by the bridge, which resolves this call to the learned tool itself.
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args) =>
        ToolResult.Error($"No learned tool named '{args.String("name")}'. Call list_learned_tools.");
}

public sealed class DeleteLearnedToolTool : IRevitTool
{
    public string Name => "delete_learned_tool";
    public string Description => "Delete a learned tool the user no longer wants. Only learned tools can be deleted, never built-in ones.";
    public string PropertiesJson => """{"name": {"type": "string"}}""";
    public string[] Required => ["name"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var name = args.String("name");
        if (LearnedTools.Load(name) is null) return ToolResult.Error($"No learned tool named '{name}'. Call list_learned_tools.");
        File.Delete(LearnedTools.PathFor(name));
        return ToolResult.Ok(new { deleted = name });
    }
}
