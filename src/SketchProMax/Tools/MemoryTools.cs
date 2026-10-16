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
