using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// Finds the code that worked before (WorkedCode): every run_revit_code and run_revit_query_code that worked is kept
/// with what it did, so work done once is adapted next time, not written again. Reads only.
/// </summary>
public sealed class WorkedCodeTool : IRevitTool
{
    public const string ToolName = "worked_code";

    public string Name => ToolName;
    public string Description =>
        "The code that worked before on this PC: every run_revit_code and run_revit_query_code that worked is kept with what it " +
        "did, from every chat and job. Call it BEFORE writing code (action=find with a few words on the work, for example " +
        "'trim beam to slab edge' or 'dimension pile centres'): the best match comes back with its code, ready to adapt (other " +
        "ids, levels, sizes), so nothing that worked once is written again or fails the same way twice. action=get with an id " +
        "gives one entry's code; action=list the newest. Changes nothing.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["find", "get", "list"], "description": "Default find."},
      "query": {"type": "string", "description": "find: what the code should do, in a few words (the words of its description)."},
      "id": {"type": "string", "description": "get: the entry's id, from find, list, or the note on a failed code call."},
      "max": {"type": "integer", "description": "find and list: how many entries. Default 5 (find), 20 (list)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var action = (args.OptString("action") ?? "find").Trim().ToLowerInvariant();
        var entries = WorkedCode.Load();
        var max = (int?)args.OptLong("max");
        switch (action)
        {
            case "get":
            {
                var id = (args.OptString("id") ?? "").Trim();
                var entry = entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
                return entry is null ? ToolResult.Error($"No kept code with id '{id}'. Call worked_code action=find.") : ToolResult.Ok(Full(entry));
            }
            case "list":
                return ToolResult.Ok(new
                {
                    kept = entries.Count,
                    entries = entries.OrderByDescending(e => e.Last).Take(max is > 0 ? max.Value : 20).Select(Header).ToList(),
                });
            case "find":
            {
                var query = (args.OptString("query") ?? "").Trim();
                if (query.Length == 0) return ToolResult.Error("find needs a query: what the code should do, in a few words.");
                var found = WorkedCode.Find(entries, query, max is > 0 ? max.Value : 5, 1);
                if (found.Count == 0)
                    return ToolResult.Ok(new { kept = entries.Count, found = 0, note = "Nothing like it is kept yet. Write it; when it works it is kept by itself." });
                return ToolResult.Ok(new
                {
                    kept = entries.Count,
                    found = found.Count,
                    best = Full(found[0].Entry),
                    others = found.Skip(1).Select(m => Header(m.Entry)).ToList(),
                    note = "Adapt the best one (ids, names, levels, sizes) rather than writing new code. It worked as it is; when it fails now, the model differs: read the error, change only that.",
                });
            }
            default:
                return ToolResult.Error("action is find, get or list.");
        }
    }

    private static object Header(WorkedCode.Entry e) => new
    {
        id = e.Id, description = e.Description, tool = e.Tool, worked = e.Uses, last = e.Last.ToString("yyyy-MM-dd"), model = e.Model,
    };

    private static object Full(WorkedCode.Entry e) => new
    {
        id = e.Id, description = e.Description, tool = e.Tool, own_transactions = e.OwnTransactions, worked = e.Uses,
        last = e.Last.ToString("yyyy-MM-dd"), model = e.Model, code = e.Code,
    };
}
