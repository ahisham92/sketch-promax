using System.IO;
using System.Text.Json;

namespace SketchProMax.Bridge;

/// <summary>
/// The tool list the Sketch ProMax connector offers, the same both ways Claude reaches Revit: the connector program
/// (SketchProMax.Mcp.exe, which the desktop app starts) lists Revit's catalogue from tools.json, and the chat's own
/// connection inside Revit (ChatMcp) lists the running registry. Both add the connection and job tools first,
/// read_pdf_part, and the learned tools saved on this PC. No Revit and no MCP library here: the connector, the add-in and
/// the unit tests compile this file.
/// </summary>
internal static class ConnectorTools
{
    /// <summary>One tool as the MCP client sees it.</summary>
    internal sealed record Def(string Name, string? Description, JsonElement InputSchema);

    public const string StatusTool = "sketch_promax_status";

    /// <summary>Listed alone when Revit has never written its catalogue (Sketch ProMax hasn't run in Revit yet).</summary>
    public static readonly Def Status = new(StatusTool, "Sketch ProMax has not run in Revit yet. Call this to get setup help.",
        Schema("""{"type":"object","properties":{}}"""));

    public static readonly Def[] ConnectionTools =
    [
        new("list_revit_models",
            "Lists every Revit window connected to Sketch ProMax (Revit version and open models), and which one Claude is working on. Call this when the user mentions a model by name or has several Revit windows open.",
            Schema("""{"type":"object","properties":{}}""")),
        new("use_revit_model",
            "Chooses which open Revit model the other tools act on, by its name (part of the name is enough). Optionally narrow by Revit version when the same model is open in two versions.",
            Schema("""
                {"type":"object","properties":{
                  "model":{"type":"string","description":"Model name as shown in Revit's title bar, or part of it"},
                  "revitVersion":{"type":"string","description":"Optional, e.g. 2024"}},
                 "required":["model"]}
                """)),
    ];

    /// <summary>
    /// The job tools that tool results refer to ("still running as job N, call wait_for_job"). Always listed, first, even
    /// when the catalogue Revit wrote is missing them or damaged: a result must never name a tool Claude can't see.
    /// Revit's own definitions replace these when present.
    /// </summary>
    public static readonly Def[] JobTools =
    [
        new("wait_for_job",
            "Get the result of a long tool call that was still running when its call returned (it said 'still running as job N'), " +
            "or see what Revit is busy with. Waits up to wait_seconds for the job to finish, then returns its result, or its " +
            "progress so far if it is still running. Call again to keep waiting.",
            Schema("""
                {"type":"object","properties":{
                  "job_id":{"type":"integer","description":"The job number. Default: the latest job."},
                  "wait_seconds":{"type":"integer","description":"How long to wait for it to finish, 0-50 (default 40)."}}}
                """)),
        new("cancel_job",
            "Ask a long-running tool call to stop. A job still waiting for Revit is dropped; a running one stops at its next check.",
            Schema("""
                {"type":"object","properties":{"job_id":{"type":"integer","description":"Default: the running job."}}}
                """)),
    ];

    /// <summary>read_pdf_part (PdfPartReader): runs in the connector, without Revit.</summary>
    public static readonly Def PdfPart = new(PdfPartReader.ToolName,
        "Reads one part of a large PDF the user attached (the chat split it into parts that can be read whole) and answers a question " +
        "about it: the part is read in full, text and drawings, by a helper that holds only that part, and its answer gives the page " +
        "numbers of the original PDF. Call it with a part file the attachment note lists and the user's question in your own clear " +
        "words; ask several parts at once (parallel calls) when the question spans them. A follow-up about the same part is quicker.",
        Schema("""
            {"type":"object","properties":{
              "file":{"type":"string","description":"The part's full path, as the attachment note lists it."},
              "question":{"type":"string","description":"What to find or answer from this part, in full (the helper doesn't see the chat)."},
              "description":{"type":"string","description":"One short line the user sees, such as 'Part 2, pages 41-80: pile sizes'."}},
             "required":["file","question"]}
            """));

    /// <summary>
    /// The catalogue Revit wrote (tools.json): null when there is none yet, empty when it can't be read right now (Revit
    /// may be rewriting it, or it is damaged; Revit writes it again on its next start).
    /// </summary>
    public static List<Def>? ReadCatalogue(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.EnumerateArray().Select(t => new Def(
                t.GetProperty("name").GetString()!,
                t.GetProperty("description").GetString(),
                t.GetProperty("inputSchema").Clone())).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The full list from Revit's tools: connection and job tools first, then Revit's, read_pdf_part and the learned tools.</summary>
    public static List<Def> Compose(IEnumerable<Def> revitTools, string learnedFolder)
    {
        var tools = revitTools.ToList();
        // The tools other results point to come first, so a client that shows only part of a long list still has them.
        var first = JobTools.Select(j => tools.FirstOrDefault(t => t.Name == j.Name) ?? j).ToList();
        tools.RemoveAll(t => JobTools.Any(j => j.Name == t.Name));
        tools.InsertRange(0, first);
        tools.InsertRange(0, ConnectionTools);
        if (!tools.Any(t => t.Name == PdfPart.Name)) tools.Add(PdfPart);
        tools.AddRange(Learned(learnedFolder, new HashSet<string>(tools.Select(t => t.Name))));
        return tools;
    }

    /// <summary>Tools Claude saved with save_as_tool, read from the files the add-in writes.</summary>
    private static IEnumerable<Def> Learned(string folder, HashSet<string> taken)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f))
        {
            Def? tool = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var name = r.GetProperty("name").GetString()!;
                if (taken.Add(name)) tool = new Def(name, r.GetProperty("description").GetString(), r.GetProperty("inputSchema").Clone());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (tool is not null) yield return tool;
        }
    }

    /// <summary>A learned tool was saved or deleted by this call: the client is told to read the list again.</summary>
    public static bool ChangesList(string tool) => tool is "save_as_tool" or "delete_learned_tool";

    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
