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
        "gives one entry's code; action=list the newest. Kept code is shared with the team: with a team folder (action=share shows " +
        "it; folder sets it, 'off' stops it; by default the update folder of the shared tools) every PC's kept code is found by " +
        "everyone, and the owner's one-click tools (save_as_tool) are installed on every PC. Changes nothing in the model.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["find", "get", "list", "share"], "description": "Default find."},
      "query": {"type": "string", "description": "find: what the code should do, in a few words (the words of its description)."},
      "id": {"type": "string", "description": "get: the entry's id, from find, list, or the note on a failed code call."},
      "folder": {"type": "string", "description": "share: the team folder on the network to set (everyone uses the same one), or 'off'. Leave out to see how sharing stands."},
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
            case "share":
            {
                if (args.OptString("folder") is { } folder && folder.Trim().Length > 0)
                {
                    folder = folder.Trim().Trim('"');
                    if (!folder.Equals("off", StringComparison.OrdinalIgnoreCase) && !System.IO.Directory.Exists(folder))
                        return ToolResult.Error($"This PC can't reach the folder '{folder}'. Give a network folder everyone can open.");
                    Sharing.TeamShare.Set(folder);
                }
                var (team, reachable, fromTeam) = Sharing.TeamShare.Status();
                return ToolResult.Ok(new
                {
                    teamFolder = team.Length > 0 ? team : null,
                    reachable,
                    keptHere = entries.Count(e => e.By.Length == 0),
                    keptByColleagues = fromTeam,
                    note = team.Length == 0
                        ? "No team folder: kept code stays on this PC. Set one with action=share and folder (a network folder everyone can open); the others set the same folder once, or get it with the shared tools."
                        : "Kept code goes to and comes from this folder every two minutes; the owner's one-click tools are installed on every PC from it.",
                });
            }
            default:
                return ToolResult.Error("action is find, get, list or share.");
        }
    }

    private static object Header(WorkedCode.Entry e) => new
    {
        id = e.Id, description = e.Description, tool = e.Tool, worked = e.Uses, last = e.Last.ToString("yyyy-MM-dd"), model = e.Model, keptBy = e.By.Length > 0 ? e.By : null,
    };

    private static object Full(WorkedCode.Entry e) => new
    {
        id = e.Id, description = e.Description, tool = e.Tool, own_transactions = e.OwnTransactions, worked = e.Uses,
        last = e.Last.ToString("yyyy-MM-dd"), model = e.Model, keptBy = e.By.Length > 0 ? e.By : null, code = e.Code,
    };
}
