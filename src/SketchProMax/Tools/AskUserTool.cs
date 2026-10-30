using System.Globalization;
using System.Threading;
using Autodesk.Revit.UI;
using SketchProMax.Chat;

namespace SketchProMax.Tools;

/// <summary>
/// ask_user: during an autopilot, a question that truly needs the user goes to their phone (Iris, Chat.AskUser),
/// with the answer the job assumes meanwhile. The job carries on with its assumption; action=wait holds for the reply up
/// to the wait set in Settings › Chat › Autopilot (10 minutes by default), a short call at a time. Answered by the bridge,
/// not Revit's thread.
/// </summary>
public sealed class AskUserTool : IRevitTool
{
    public const string ToolName = "ask_user";

    public string Name => ToolName;
    public string Description =>
        "Only during an autopilot, while the user is away: ask them a question on their phone when a choice truly needs them (it would " +
        "change the result a lot and the goal doesn't say). action=ask with the question and the answer you will assume; it goes " +
        "out within 30 seconds, together with others asked meanwhile. Carry on with work that doesn't depend on it, on your " +
        "assumption. action=wait with its id holds up to 45 s for the reply; call again until the wait it names is over, then go " +
        "on with your assumption and list it under your decisions. A later reply reaches you as a message, to put the work right. " +
        "Ask rarely: one call with all your open questions beats several. Not a model change.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["ask", "wait"], "description": "Default ask."},
      "question": {"type": "string", "description": "ask: the question, complete and short (the user reads it on a phone), with the options if there are some."},
      "assumed": {"type": "string", "description": "ask: the answer you go on with if no reply comes in time."},
      "id": {"type": "integer", "description": "wait: the question's id, from ask."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    /// <summary>Run from a recipe or the self-test (not through the bridge): nobody to ask.</summary>
    public ToolResult Execute(UIApplication app, ToolArgs args) => Refused(fromChat: false);

    private static ToolResult Refused(bool fromChat) => ToolResult.Error(fromChat
        ? "ask_user works only during an autopilot, while the user is away. The user is here: ask in your answer instead."
        : "ask_user works only in the chat inside Revit, during an autopilot.");

    internal static async Task<ToolResult> AnswerAsync(ToolArgs args, bool fromChat, string? jobKey, CancellationToken ct)
    {
        if (!fromChat || !Autopilot.Unattended) return Refused(fromChat);
        var wait = AskUser.WaitMinutes;
        if (wait <= 0)
            return ToolResult.Error("Questions to the user are off (no phone connected in Settings › Chat › Your phone, or the wait is set to never). Make the call a senior would, and list it under your decisions.");
        var action = (args.OptString("action") ?? "ask").Trim().ToLowerInvariant();
        if (action == "ask")
        {
            var question = (args.OptString("question") ?? "").Trim();
            var assumed = (args.OptString("assumed") ?? "").Trim();
            if (question.Length == 0 || assumed.Length == 0) return ToolResult.Error("ask needs the question and the answer you will assume.");
            var q = AskUser.Ask(jobKey ?? "", ChatJobs.AskerName(jobKey), question, assumed);
            var until = DateTime.Now.Add(AskRules.Gather).AddMinutes(wait);
            return ToolResult.Ok(new
            {
                id = q.Id,
                wait_until = until.ToString("HH:mm", CultureInfo.InvariantCulture),
                next = $"Q{q.Id} goes to the user's phone within 30 seconds. Carry on with work that doesn't depend on it, on your assumption. " +
                       $"To wait for the answer, call ask_user action=wait id={q.Id} (up to {AskRules.WaitCallSeconds} s a call) until {until:HH:mm}; then go on with your assumption.",
            });
        }
        if (action != "wait") return ToolResult.Error("action is ask or wait.");
        if (args.OptLong("id") is not { } id || AskUser.Find((int)id) is not { } asked) return ToolResult.Error("wait needs the id ask gave.");
        var answer = await AskUser.WaitAsync(asked.Id, AskRules.WaitCallSeconds, ct).ConfigureAwait(false);
        if (answer is not null) return ToolResult.Ok(new { id = asked.Id, answered = true, answer, next = AskRules.AnswerLine(asked) });
        var start = asked.Sent != default ? asked.Sent : asked.Asked.Add(AskRules.Gather);
        var over = DateTime.UtcNow >= start.AddMinutes(wait);
        return ToolResult.Ok(new
        {
            id = asked.Id,
            answered = false,
            next = over ? AskRules.NoReplyLine(asked, wait)
                : $"No reply yet. Call ask_user action=wait id={asked.Id} again, or carry on with other work, until {start.AddMinutes(wait).ToLocalTime():HH:mm}.",
        });
    }
}
