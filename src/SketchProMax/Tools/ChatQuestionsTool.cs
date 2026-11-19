using System.Text.Json;
using Autodesk.Revit.UI;
using SketchProMax.Chat;

namespace SketchProMax.Tools;

/// <summary>
/// chat_questions: the chat's Questions tab (Chat.ChatQuestions). The coordinator and the jobs put their questions to the
/// user there, numbered, with answers to tap; the user answers on the tab or by number in the message box, and the answers
/// come back to the coordinator together. Answered by the bridge, not Revit's thread.
/// </summary>
public sealed class ChatQuestionsTool : IRevitTool
{
    public const string ToolName = "chat_questions";

    public string Name => ToolName;
    public string Description =>
        "The Questions tab of the Revit chat: every question for the user goes here, numbered, not in your answer's text. action=ask " +
        "with questions (one {question, options, recommended} each; all your open questions in one call): short and plain, with two to " +
        "four answers to tap where you can (the tab adds Other to type) and the index of the one you suggest. A job's questions show " +
        "as from that job. Then say in one line that it is in the Questions tab (\"Q4 in Questions\"), never the question again. The " +
        "answers come back to the coordinator as a message. action=update with number rewrites a question the user didn't understand " +
        "(plain words, what each option means); action=close with number withdraws one that is no longer needed; action=list shows the " +
        "open ones. The practice and memory notes are not questions: apply them. Not a model change.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["ask", "update", "close", "list"], "description": "Default ask."},
      "questions": {"type": "array", "description": "ask: the questions, one per item.", "items": {"type": "object", "properties": {"question": {"type": "string"}, "options": {"type": "array", "items": {"type": "string"}}, "recommended": {"type": "integer", "description": "0-based index of the suggested option."}}, "required": ["question"]}},
      "question": {"type": "string", "description": "ask (one question) or update: the question's text."},
      "options": {"type": "array", "items": {"type": "string"}, "description": "ask (one question) or update: the answers to tap."},
      "recommended": {"type": "integer", "description": "ask (one question) or update: 0-based index of the suggested option."},
      "number": {"type": "integer", "description": "update and close: the question's number (Q4: 4)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    /// <summary>Run from a recipe or the self-test (not the chat): nobody to ask.</summary>
    public ToolResult Execute(UIApplication app, ToolArgs args) =>
        ToolResult.Error("chat_questions works only in the chat inside Revit: there the Questions tab shows the questions to the user.");

    /// <summary>The bridge's call, from the chat (<paramref name="jobKey"/>: the caller, null or a chat key for the main chat).</summary>
    internal static ToolResult Answer(ToolArgs args, bool fromChat, string? jobKey)
    {
        var board = fromChat ? ChatJobs.BoardFor(jobKey) : null;
        if (board is null) return ToolResult.Error("No chat is open in the Sketch ProMax pane: ask the user in your answer instead.");
        var questions = board.Questions;
        var me = jobKey is not null && !JobBoard.IsChatKey(jobKey) ? board.FindKey(jobKey) : null;
        var action = (Str(args, "action") ?? "ask").Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;
        switch (action)
        {
            case "list":
                return new ToolResult(questions.Describe());
            case "ask":
            {
                var asked = new List<UserAsk>();
                var from = me is null ? "Coordinator" : $"Job {me.Number}: {me.Title}";
                if (args.Raw.TryGetValue("questions", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var q in list.EnumerateArray())
                    {
                        if (q.ValueKind == JsonValueKind.String) { if ((q.GetString() ?? "").Trim() is { Length: > 0 } t) asked.Add(questions.Ask(me?.Number ?? 0, from, t, null, -1, now)); continue; }
                        if (q.ValueKind != JsonValueKind.Object) continue;
                        var text = q.TryGetProperty("question", out var tq) && tq.ValueKind == JsonValueKind.String ? tq.GetString() ?? "" : "";
                        if (text.Trim().Length == 0) continue;
                        asked.Add(questions.Ask(me?.Number ?? 0, from, text, Strings(q, "options"), Int(q, "recommended") ?? -1, now));
                    }
                else if ((Str(args, "question") ?? "").Trim() is { Length: > 0 } one)
                    asked.Add(questions.Ask(me?.Number ?? 0, from, one, Strings(args.Raw, "options"), Int(args.Raw, "recommended") ?? -1, now));
                if (asked.Count == 0) return ToolResult.Error("Give the questions in 'questions' (one {question, options, recommended} each).");
                ChatJobs.ChangedFor(jobKey);
                var numbers = string.Join(", ", asked.Select(q => "Q" + q.Number));
                return ToolResult.Ok(new
                {
                    asked = asked.Select(q => new { number = q.Number, question = q.Text }).ToList(),
                    next = me is null
                        ? $"{numbers} are in the Questions tab. Say so in one line (\"{numbers} in Questions\"); don't write the questions again. The answers come to you as a message."
                        : $"{numbers} are in the user's Questions tab. Carry on with the work that doesn't depend on them, on the answer you'd assume; " +
                          "say in your report what waits on them. The answer reaches you as a message.",
                });
            }
            case "update":
            {
                if (Int(args.Raw, "number") is not { } n) return ToolResult.Error("update needs the question's number.");
                var options = args.Raw.TryGetValue("options", out _) ? Strings(args.Raw, "options") : null;
                if (questions.Rewrite(n, Str(args, "question") ?? "", options, Int(args.Raw, "recommended") ?? -1, now) is not { } q)
                    return ToolResult.Error($"There is no question {n}. " + questions.Describe());
                ChatJobs.ChangedFor(jobKey);
                return ToolResult.Ok(new { number = q.Number, question = q.Text, next = $"Q{q.Number} shows again in the Questions tab, written anew. Say so in one line." });
            }
            case "close":
            {
                if (Int(args.Raw, "number") is not { } n) return ToolResult.Error("close needs the question's number.");
                if (!questions.Close(n)) return ToolResult.Error($"Question {n} isn't open. " + questions.Describe());
                ChatJobs.ChangedFor(jobKey);
                return ToolResult.Ok(new { number = n, closed = true });
            }
            default:
                return ToolResult.Error("action is ask, update, close or list.");
        }
    }

    private static string? Str(ToolArgs args, string key) =>
        args.Raw.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(IReadOnlyDictionary<string, JsonElement> raw, string key) =>
        raw.TryGetValue(key, out var v) ? Int(v) : null;

    private static int? Int(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? Int(v) : null;

    private static int? Int(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
        : v.ValueKind == JsonValueKind.String && int.TryParse((v.GetString() ?? "").Trim().TrimStart('Q', 'q'), out var p) ? p : null;

    private static List<string> Strings(IReadOnlyDictionary<string, JsonElement> raw, string key) =>
        raw.TryGetValue(key, out var v) ? Strings(v) : [];

    private static List<string> Strings(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? Strings(v) : [];

    private static List<string> Strings(JsonElement v) =>
        v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList() : [];
}
