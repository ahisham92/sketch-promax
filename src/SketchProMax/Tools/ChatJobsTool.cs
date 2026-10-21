using System.Text.Json;
using Autodesk.Revit.UI;
using SketchProMax.Chat;

namespace SketchProMax.Tools;

/// <summary>
/// chat_jobs: the in-Revit chat as coordinator. It splits a request into jobs that work side by side, each its own hidden
/// session with a card in the pane (Chat.JobBoard, ChatPane.Jobs), sends a follow-up to the job it belongs to, lists and
/// stops them. Answered by the bridge, not Revit's thread, so it works while a job's change runs; only the main chat in
/// the pane may call it (not a job, not the Claude desktop app).
/// </summary>
public sealed class ChatJobsTool : IRevitTool
{
    public const string ToolName = "chat_jobs";

    public string Name => ToolName;
    public string Description =>
        "Jobs that work side by side in the Revit chat pane, each its own helper with a card the user sees. action=start opens a job: " +
        "title (a few words) and task (everything it needs, in full: it doesn't see this chat). action=list shows every job with its " +
        "state and report. action=message sends a follow-up to job (its number): a running job gets it when its current run ends; a " +
        "finished or stopped one carries on with it. action=stop stops job. Before starting a job, list the jobs: a follow-up that " +
        "belongs to an existing job goes there with action=message. start refuses a request that reads as a follow-up of an existing " +
        "job; call it again with new=true when it really is separate work. Reads run in parallel; model changes take turns. Not a model change.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["start", "list", "message", "stop"], "description": "Default list."},
      "title": {"type": "string", "description": "start: a few words the user sees on the card, e.g. 'Sheets for levels 1-5'."},
      "task": {"type": "string", "description": "start: the whole task for the job, with every detail it needs (names, levels, types, settings)."},
      "job": {"type": "integer", "description": "message and stop: the job's number."},
      "text": {"type": "string", "description": "message: what to tell the job, in full."},
      "new": {"type": "boolean", "description": "start: true when it is separate work even though it resembles an existing job."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    /// <summary>Run from a recipe or the self-test (not the chat): lists, never starts or changes a job.</summary>
    public ToolResult Execute(UIApplication app, ToolArgs args) => Answer(args, coordinator: false);

    /// <summary>
    /// The call's answer. <paramref name="coordinator"/>: the main chat of the pane called it (the bridge says so); only
    /// then may it start, message or stop jobs.
    /// </summary>
    internal static ToolResult Answer(ToolArgs args, bool coordinator)
    {
        var action = (Str(args, "action") ?? "list").Trim().ToLowerInvariant();
        var board = ChatJobs.Board;
        var limit = ChatJobs.Limit;
        if (action == "list")
            return board is null ? ToolResult.Ok(new { jobs = "No chat is open in the Sketch ProMax pane, so there are no jobs." }) : new ToolResult(board.Describe(limit));
        if (action is not ("start" or "message" or "stop")) return ToolResult.Error("action is start, list, message or stop.");
        if (!coordinator || board is null)
            return ToolResult.Error("Jobs run only from the main chat of the Sketch ProMax pane in Revit. Do the work here yourself instead.");
        if (!ChatJobs.Available)
            return ToolResult.Error("Jobs aren't available right now (the Sketch ProMax connector is older than the add-in, or the chat is switching). Do the work here yourself.");
        var now = DateTime.UtcNow;
        switch (action)
        {
            case "start":
            {
                var task = (Str(args, "task") ?? "").Trim();
                var title = (Str(args, "title") ?? "").Trim();
                if (task.Length == 0) return ToolResult.Error("Give the job's whole task in 'task' (and a short 'title').");
                var job = board.Start(title, task, now, Bool(args, "new"), out var similar);
                if (job is null)
                    return ToolResult.Ok(new
                    {
                        started = false,
                        similar_job = similar!.Number, similar_title = similar.Title, state = JobBoard.StateLabel(similar),
                        next = $"Not started: this reads as a follow-up of job {similar.Number} (\"{similar.Title}\"). Send it there with " +
                               $"action=message, job={similar.Number}. If it really is separate work, call start again with new=true.",
                    });
                ChatJobs.Changed?.Invoke();
                var waiting = board.OpenCount > JobBoard.ClampLimit(limit);
                return ToolResult.Ok(new
                {
                    started = true, job = job.Number, title = job.Title,
                    state = waiting ? "waiting to start (others are running)" : "starting",
                    next = "The user sees its card. Tell them in one line which jobs started, then end your turn: each job reports back when it finishes.",
                });
            }
            case "message":
            {
                var number = Number(args);
                var text = (Str(args, "text") ?? "").Trim();
                if (number is null || text.Length == 0) return ToolResult.Error("Give the job's number in 'job' and what to tell it in 'text'.");
                var sent = board.Message(number.Value, text, now);
                if (sent == JobBoard.Delivery.NotFound) return ToolResult.Error($"There is no job {number}. " + board.Describe(limit));
                ChatJobs.Changed?.Invoke();
                return ToolResult.Ok(new
                {
                    job = number, sent = true,
                    state = sent == JobBoard.Delivery.Reopened ? "it carries on with this message" : "it gets this when its current run ends",
                    next = "Tell the user in one line that the job has it. It reports back when it finishes.",
                });
            }
            default:
            {
                var number = Number(args);
                if (number is null) return ToolResult.Error("Give the job's number in 'job'.");
                var was = board.Stop(number.Value, now);
                if (was is null) return ToolResult.Error($"There is no job {number}. " + board.Describe(limit));
                ChatJobs.Changed?.Invoke();
                return ToolResult.Ok(new
                {
                    job = number,
                    stopped = was is JobBoard.Running or JobBoard.Waiting or JobBoard.Queued,
                    note = was is JobBoard.Running or JobBoard.Waiting
                        ? "Stopped. A Revit change it had already started finishes on its own."
                        : was == JobBoard.Queued ? "Stopped before it started." : "It wasn't running.",
                });
            }
        }
    }

    private static string? Str(ToolArgs args, string key) =>
        args.Raw.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(ToolArgs args, string key) =>
        args.Raw.TryGetValue(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>The job's number, given as a number or as text ("2", "job 2").</summary>
    private static int? Number(ToolArgs args)
    {
        if (!args.Raw.TryGetValue("job", out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && System.Text.RegularExpressions.Regex.Match(v.GetString() ?? "", @"\d+") is { Success: true } m
            && int.TryParse(m.Value, out var parsed)) return parsed;
        return null;
    }
}
