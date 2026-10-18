using Autodesk.Revit.UI;
using SketchProMax.Chat;

namespace SketchProMax.Tools;

/// <summary>Notes the user asks Claude to remember, kept in %APPDATA%\SketchProMax\memory.md (see MemoryNotes).</summary>
public sealed class MemoryNoteTool : IRevitTool
{
    public string Name => "memory_note";
    public string Description =>
        "Remember or forget a note for the user. When the user says \"remember that ...\" use action=remember with the note " +
        "in their words, short and self-contained; when they say \"forget ...\" use action=forget with the note or a few " +
        "words of it. scope=model (default) keeps it for the open model only; scope=global for every model (their habits, " +
        "standards, preferences). Notes are given to every in-Revit chat at its start (global plus that model's) and can be " +
        "seen and edited in the Notes tab of the Sketch ProMax pane. action=list shows them. Not a model change.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["remember", "forget", "list"], "description": "Default list."},
      "note": {"type": "string", "description": "remember: the note. forget: the note, or words that appear in it."},
      "scope": {"type": "string", "enum": ["model", "global"], "description": "Default model (the open model); global for every model."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument?.Document;
        var modelKey = doc is null ? null : ChatPane.ModelKey(doc);
        var action = (args.OptString("action") ?? "list").Trim().ToLowerInvariant();
        var global = string.Equals(args.OptString("scope"), "global", StringComparison.OrdinalIgnoreCase);
        if (!global && modelKey is null && action != "list") return ToolResult.Error("No model is open: use scope=global, or open the model first.");
        var key = global ? null : modelKey;
        var where = global ? "for every model" : $"for this model ({doc!.Title})";

        switch (action)
        {
            case "list":
            {
                var (g, m) = MemoryNotes.For(MemoryNotes.Load(), modelKey);
                return ToolResult.Ok(new { global = g, this_model = m, model = doc?.Title, file = MemoryNotes.File });
            }
            case "remember":
            {
                var note = MemoryNotes.Clean(args.OptString("note") ?? "");
                if (note.Length == 0) return ToolResult.Error("Give the note to remember in 'note'.");
                var added = MemoryNotes.Change(s => MemoryNotes.Add(s, key, note));
                return ToolResult.Ok(new
                {
                    remembered = note, scope = global ? "global" : "model", added,
                    next = added ? $"Tell the user it is remembered {where}; it is in the Notes tab of the pane." : "It was already remembered.",
                });
            }
            case "forget":
            {
                var text = args.OptString("note") ?? "";
                if (MemoryNotes.Clean(text).Length == 0) return ToolResult.Error("Say which note to forget in 'note' (the note or words from it).");
                var result = MemoryNotes.Change(s =>
                {
                    var matches = MemoryNotes.Matches(s, key, text);
                    if (matches.Count == 1) MemoryNotes.Remove(s, key, matches[0]);
                    return matches;
                });
                if (result.Count == 1) return ToolResult.Ok(new { forgotten = result[0], scope = global ? "global" : "model" });
                if (result.Count == 0)
                {
                    var (g, m) = MemoryNotes.For(MemoryNotes.Load(), modelKey);
                    return ToolResult.Error($"No note {where} matches '{text}'. Notes for every model: {Join(g)}. Notes for this model: {Join(m)}. " +
                                            "Try the other scope, or the note's exact words.");
                }
                return ToolResult.Error($"{result.Count} notes {where} match '{text}': {Join(result)}. Ask the user which one, then forget it by its exact words.");
            }
            default:
                return ToolResult.Error("action is remember, forget or list.");
        }
    }

    private static string Join(List<string> notes) => notes.Count == 0 ? "(none)" : string.Join(" | ", notes.Select(n => "\"" + n + "\""));
}

/// <summary>
/// Searches the user's earlier chats in the Revit pane, for every model (ChatRecall): "do it the same way as in the
/// Tower project" finds that chat, and action=open gives its summary, requests and steps to follow again.
/// </summary>
public sealed class RecallChatsTool : IRevitTool
{
    public string Name => "recall_chats";
    public string Description =>
        "Search the user's earlier chats in the Revit pane, across every model and project. Use it when the user refers to " +
        "something done before (\"the same way as in the Tower project\", \"like last time\", \"what did we decide about ...\"). " +
        "action=search (default) with query = the words that name the project, model or task: lists the best matching chats " +
        "with their model, date, summary and matching lines. action=open with chat = a chat's id from search: its summary, " +
        "the user's requests and the steps taken with their settings, to repeat on this model. action=list: this model's " +
        "chats with what each remembered. Read-only, not a model change.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["search", "open", "list"], "description": "Default search."},
      "query": {"type": "string", "description": "search: words naming the project, model or task, e.g. 'Tower pile caps'."},
      "chat": {"type": "string", "description": "open: the chat id from search or list."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument?.Document;
        var action = (args.OptString("action") ?? "search").Trim().ToLowerInvariant();
        var all = ChatStore.All();
        switch (action)
        {
            case "search":
            {
                var query = args.OptString("query") ?? "";
                var hits = ChatRecall.Search(all, query);
                return ToolResult.Ok(new
                {
                    query, searched = all.Count,
                    chats = hits.Select(h => Row(h.Chat, h.Lines)).ToList(),
                    next = hits.Count == 0
                        ? "No earlier chat matches. Ask the user which project or chat they mean, or search with other words."
                        : "Open the best match with action=open and chat=<id> to see its steps, then follow them on this model.",
                });
            }
            case "open":
            {
                var id = (args.OptString("chat") ?? "").Trim();
                if (id.Length == 0) return ToolResult.Error("Give the chat's id in 'chat' (from action=search or list).");
                var chat = all.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                if (chat is null) return ToolResult.Error($"No chat has the id '{id}'. Search again with action=search.");
                var (summary, requests, steps) = ChatRecall.Details(chat);
                return ToolResult.Ok(new
                {
                    chat = chat.Id, name = chat.Name, model = chat.Title, date = chat.Updated.ToLocalTime().ToString("yyyy-MM-dd"),
                    summary, requests, steps,
                    next = "Repeat what applies on this model with the same settings (check its levels, types and units first). " +
                           "Tell the user in one line which chat you followed.",
                });
            }
            case "list":
            {
                var key = doc is null ? "(no model)" : ChatPane.ModelKey(doc);
                var mine = all.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase) && c.Items.Count > 0)
                    .OrderByDescending(c => c.Updated).Take(20).ToList();
                return ToolResult.Ok(new { model = doc?.Title, chats = mine.Select(c => Row(c, [])).ToList() });
            }
            default:
                return ToolResult.Error("action is search, open or list.");
        }
    }

    private static object Row(Conversation c, List<string> lines) => new
    {
        chat = c.Id, name = c.Name, model = c.Title, date = c.Updated.ToLocalTime().ToString("yyyy-MM-dd"),
        summary = c.Recall.Length > 0 ? c.Recall : "(not summarised yet)", matches = lines,
    };
}
