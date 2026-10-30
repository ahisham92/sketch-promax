using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// How a tool call is shown to the user in the chat's step rows, the job card, the job window and the status line: a
/// friendly step name ("Read model data") and the plain description the call carries ("Read 79 selected piles: …"),
/// never the tool's id or "key=value" arguments. The full input stays in the row's details. No WPF: unit-tested.
/// </summary>
internal static class StepNames
{
    // Tools whose id doesn't read well as words. Anything else is its id in sentence case ("Tag elements in view").
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["run_revit_query_code"] = "Read model data",
        ["run_revit_code"] = "Change the model",
        ["query_elements"] = "Find elements",
        ["query_linked_models"] = "Read linked models",
        ["get_model_context"] = "Look at the model",
        ["get_element_parameters"] = "Read parameters",
        ["get_coordinates"] = "Read coordinates",
        ["get_activity_log"] = "Read the activity log",
        ["get_drawing_package_plan"] = "Plan the drawing package",
        ["capture_view_image"] = "Take a picture of the view",
        ["select_elements"] = "Select elements",
        ["set_parameters"] = "Set parameters",
        ["delete_elements"] = "Delete elements",
        ["move_copy_rotate"] = "Move, copy or rotate",
        ["wait_for_job"] = "Wait for the job",
        ["cancel_job"] = "Stop the job",
        ["memory_note"] = "Save a note",
        ["read_document_text"] = "Read a document",
        ["berth_status"] = "Submissions status",
        ["berth_costing"] = "Berth costing",
        ["help"] = "Read the guide",
        ["features"] = "List what it can do",
        ["diagnostics"] = "Check the setup",
        ["ve"] = "Value engineering",
        ["ve_apply"] = "Apply value engineering",
        ["ve_view"] = "Value engineering view",
        ["ve_register"] = "Value engineering register",
        ["ve_opportunities"] = "Find value engineering ideas",
        ["ve_compare_options"] = "Compare value engineering options",
        ["what_if"] = "What if",
        ["read_pdf_part"] = "Read a part of the PDF",
        ["chat_jobs"] = "Jobs",
        ["ask_user"] = "Ask you on your phone",
        ["make_document"] = "Make a file",
        // Built-in steps of the chat engine.
        ["Read"] = "Read a file",
        ["Write"] = "Write a file",
        ["Edit"] = "Edit a file",
        ["MultiEdit"] = "Edit a file",
        ["Glob"] = "Find files",
        ["Grep"] = "Search files",
        ["LS"] = "List a folder",
        ["Bash"] = "Run a command",
        ["TodoWrite"] = "Update the plan",
        ["TaskCreate"] = "Add a step to the plan",
        ["TaskUpdate"] = "Update the plan",
        ["WebFetch"] = "Read a web page",
        ["WebSearch"] = "Search the web",
        ["ToolSearch"] = "Look up tools",
        ["Task"] = "Hand off a part of the task",
        ["Agent"] = "Hand off a part of the task",
    };

    // Short words written in capitals when an id is turned into words.
    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal) { "bcf", "boq", "cad", "mep", "pdf", "pt", "rft", "ve", "id", "ids" };

    // The input keys that carry a plain sentence about the call, best first.
    private static readonly string[] DescriptionKeys = ["description", "summary", "title", "what", "task"];

    private static readonly Regex Legacy = new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant);

    /// <summary>"run_revit_query_code" → "Read model data"; "mcp__x__tag_elements_in_view" → "Tag elements in view".</summary>
    public static string Title(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return "";
        tool = Short(tool.Trim());
        if (Names.TryGetValue(tool, out var name)) return name;
        var words = tool.Replace('_', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Acronyms.Contains(w) ? w.ToUpperInvariant() : w).ToList();
        if (words.Count == 0) return "";
        var text = string.Join(" ", words);
        return char.ToUpper(text[0], CultureInfo.InvariantCulture) + text.Substring(1);
    }

    /// <summary>
    /// The call's own plain description from its input JSON ("description", "summary", "title"...; for file steps the
    /// file's name), on one line and cut to <paramref name="max"/> characters. "" when it carries none.
    /// </summary>
    public static string Describe(string tool, string json, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";
            // The main chat handing work to its parallel jobs: what it did with which job.
            if (Short(tool) == "chat_jobs") return Cut(JobsLine(root), max);
            // A file for the user: what kind, and its title.
            if (Short(tool) == "make_document") return Cut(DocumentLine(root), max);
            foreach (var key in DescriptionKeys)
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && OneLine(v.GetString()) is { Length: > 0 } text)
                    return Cut(text, max);
            // A part of a large PDF: the part's name says which pages.
            if (Short(tool) == "read_pdf_part" && root.TryGetProperty("file", out var part) && part.ValueKind == JsonValueKind.String && OneLine(part.GetString()) is { Length: > 0 } partPath)
                return Cut(FileName(partPath), max);
            // File steps of the chat engine: the file's name says enough.
            foreach (var key in new[] { "file_path", "path" })
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && OneLine(v.GetString()) is { Length: > 0 } path)
                    return Cut(EngineFile(path) ?? FileName(path), max);
            if (root.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String && OneLine(p.GetString()) is { Length: > 0 } pattern)
                return Cut(pattern, max);
        }
        catch (JsonException) { }
        return "";
    }

    /// <summary>
    /// What a step row says: its description, or the friendly name when it has none. A chat saved before 0.34 kept
    /// "key=value, ..." there: its description is taken out of it, or the friendly name shown instead.
    /// </summary>
    public static string Line(string tool, string text)
    {
        text = OneLine(text);
        if (EngineFile(text) is { } engine) return engine; // a row saved before 0.34.2 shows the file's name
        if (Legacy.IsMatch(text))
        {
            const string lead = "description=";
            if (!text.StartsWith(lead, StringComparison.Ordinal)) return Title(tool);
            var rest = text.Substring(lead.Length);
            var next = Regex.Match(rest, @", [A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant);
            text = (next.Success ? rest.Substring(0, next.Index) : rest).Trim();
        }
        return text.Length > 0 ? text : Title(tool);
    }

    /// <summary>
    /// The status line while a step runs: "Running: Set 79 selected pile tops…". A step that may change the model adds
    /// "If Revit asks, answer it there." unless nothing ever asks (the Always allow approval level).
    /// </summary>
    public static string Running(string tool, string text, bool mayAsk)
    {
        var what = Line(tool, text).TrimEnd('…', '.', ' ');
        return "Running: " + what + "…" + (mayAsk ? " If Revit asks, answer it there." : "");
    }

    /// <summary>True for steps that only read (no approval is ever asked for them).</summary>
    public static bool ReadOnly(string tool)
    {
        tool = Short(tool);
        return tool is "run_revit_query_code" or "query_elements" or "query_linked_models" or "capture_view_image" or "select_elements"
                   or "wait_for_job" or "cancel_job" or "chat_jobs" or "ask_user" or "help" or "features" or "memory_note" or "diagnostics" or "make_document"
                   or "Read" or "Glob" or "Grep" or "LS" or "TodoWrite" or "TaskCreate" or "TaskUpdate" or "WebFetch" or "WebSearch" or "ToolSearch"
               || tool.StartsWith("get_", StringComparison.Ordinal) || tool.StartsWith("list_", StringComparison.Ordinal)
               || tool.StartsWith("find_", StringComparison.Ordinal) || tool.StartsWith("read_", StringComparison.Ordinal);
    }

    /// <summary>"Start a job: Sheets for levels 1-5", "Message job 2", "Stop job 2", "List the jobs".</summary>
    private static string JobsLine(JsonElement root)
    {
        string Str(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? OneLine(v.GetString()) : "";
        var job = root.TryGetProperty("job", out var j) ? OneLine(j.ValueKind == JsonValueKind.String ? j.GetString() : j.GetRawText()) : "";
        return Str("action").ToLowerInvariant() switch
        {
            "start" => Str("title") is { Length: > 0 } title ? "Start a job: " + title : "Start a job",
            "message" => job.Length > 0 ? "Message job " + job : "Message a job",
            "stop" => job.Length > 0 ? "Stop job " + job : "Stop a job",
            _ => "List the jobs",
        };
    }

    /// <summary>"Make an Excel workbook: Bill of quantities", "Make a PDF".</summary>
    private static string DocumentLine(JsonElement root)
    {
        string Str(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? OneLine(v.GetString()) : "";
        var kind = Str("format").ToLowerInvariant() switch
        {
            "excel" or "xlsx" => "an Excel workbook",
            "word" or "docx" => "a Word document",
            "powerpoint" or "pptx" => "a PowerPoint presentation",
            "pdf" => "a PDF",
            _ => "a file",
        };
        return "Make " + kind + (Str("title") is { Length: > 0 } title ? ": " + title : "");
    }

    private static string Short(string tool)
    {
        if (!tool.StartsWith("mcp__", StringComparison.Ordinal)) return tool;
        var at = tool.IndexOf("__", 5, StringComparison.Ordinal);
        return at > 0 ? tool.Substring(at + 2) : tool.Substring(5);
    }

    private static string OneLine(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    private static string Cut(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…";

    /// <summary>The chat engine's own conversation files (Claude Code's .jsonl transcripts) in plain words.</summary>
    public const string EarlierMessages = "Look back at earlier messages";

    /// <summary>A large step result the engine saved to a file (Claude Code's tool-results folder).</summary>
    public const string SavedResult = "Read the full result of an earlier step";

    /// <summary>
    /// What a file of the chat engine itself is, in words: its conversation transcript ("…/&lt;id&gt;.jsonl") or a step
    /// result it saved aside; null for the user's own files.
    /// </summary>
    private static string? EngineFile(string path)
    {
        if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return EarlierMessages;
        var p = path.Replace('\\', '/');
        return p.IndexOf("/tool-results/", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("/.claude/projects/", StringComparison.OrdinalIgnoreCase) >= 0 ? SavedResult : null;
    }

    private static string FileName(string path)
    {
        var at = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return at >= 0 && at < path.Length - 1 ? path.Substring(at + 1) : path;
    }
}
