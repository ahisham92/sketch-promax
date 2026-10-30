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
        "title (a few words), task (everything it needs, in full: it doesn't see this chat) and done_when (how the job can check in the model that it " +
        "is finished; it checks that before it reports). To divide work across the free workers, " +
        "give parts instead (one {title, task, kind} per part, which don't overlap) and facts (what you read that every part needs): they " +
        "all start at once. action=list shows every job with its " +
        "state and report. action=message sends a follow-up to job (its number): a running job gets it when its current run ends; a " +
        "finished or stopped one carries on with it. action=stop stops job. Before starting a job, list the jobs: a follow-up that " +
        "belongs to an existing job goes there with action=message. start refuses a request that reads as a follow-up of an existing " +
        "job; call it again with new=true when it really is separate work. A job may call it too, to work with its teammates: action=list " +
        "shows them, action=ask with job and question gets what that job has found so far (forward=true also passes the question on, and it " +
        "messages you back), action=message with job and text tells it something it needs. Reads run in parallel; model changes take turns. " +
        "Work on two models open in two Revit windows (list_revit_models), for example a working model and a documentation model " +
        "that links it: give each job its model (model=its title); jobs in different windows really work at once. Not a model change.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["start", "list", "message", "stop", "ask"], "description": "Default list. A job (not the main chat) may use list, ask and message."},
      "question": {"type": "string", "description": "ask (from a job): what you need from that job, e.g. 'the ids of the grids you read'."},
      "forward": {"type": "boolean", "description": "ask: true to also pass the question on when the job is still working; it messages you the answer when its current run ends."},
      "title": {"type": "string", "description": "start: a few words the user sees on the card, e.g. 'Sheets for levels 1-5'."},
      "task": {"type": "string", "description": "start: the whole task for the job, with every detail it needs (names, levels, types, settings)."},
      "done_when": {"type": "string", "description": "start (and each part): the finish line, one to three conditions the job can check in the model, e.g. 'every sheet A-101 to A-120 has a filled title block and a plan view; issue_check passes'. The job checks them before it reports and says which held."},
      "job": {"type": "integer", "description": "message, stop and ask: the job's number."},
      "text": {"type": "string", "description": "message: what to tell the job, in full."},
      "parts": {"type": "array", "description": "start: divide the work, one job per part, all started at once (each part's task in full; the parts must not overlap, e.g. levels 1-3 and 4-6).", "items": {"type": "object", "properties": {"title": {"type": "string"}, "task": {"type": "string"}, "done_when": {"type": "string"}, "kind": {"type": "string", "enum": ["simple", "everyday", "hard"]}, "model": {"type": "string"}}, "required": ["title", "task"]}},
      "model": {"type": "string", "description": "start (and each part): the open model the job works on, its title as list_revit_models gives it, when several are open (also in another Revit window). Left out: the model active in this window."},
      "facts": {"type": "string", "description": "start with parts: what you already read from the model that every part needs (levels, grids, types, names, ids); each job gets it with its task."},
      "single": {"type": "string", "description": "start without parts while two or more workers are free: why this work can't be divided (one element, one sheet, one short answer). Without it such a start is sent back to be divided."},
      "new": {"type": "boolean", "description": "start: true when it is separate work even though it resembles an existing job."},
      "kind": {"type": "string", "enum": ["simple", "everyday", "hard"], "description": "start: the kind of work, which picks the job's model and effort when the chat is on Automatic. simple: mechanical work (rename, tag, list, fill parameters, short summaries); everyday: most Revit work (sheets, views, dimensions, checks, normal modelling); hard: hard reasoning (a spec or BEP against the model, redlines, tricky modelling logic, costing decisions). Left out, it is judged from the task's words."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    /// <summary>A job's own calls: ask a teammate, or message one; it doesn't start or stop jobs.</summary>
    private static ToolResult Teamwork(JobBoard board, ChatJob me, string action, ToolArgs args)
    {
        var number = Number(args);
        if (action is not ("ask" or "message"))
            return ToolResult.Error("A job doesn't start or stop jobs: say in your report what else is needed, and the main chat hands it out. You may list, ask and message.");
        if (number is null || number == me.Number) return ToolResult.Error("Give another job's number in 'job' (action=list shows your teammates).");
        var now = DateTime.UtcNow;
        if (action == "ask")
        {
            var answer = board.Ask(me.Key, number.Value, (Str(args, "question") ?? Str(args, "text") ?? "").Trim(), Bool(args, "forward"), now);
            if (answer is null) return ToolResult.Error($"There is no job {number}. " + board.Describe(ChatJobs.Limit));
            ChatJobs.Changed?.Invoke();
            return ToolResult.Ok(new { job = number, answer, next = "Use what fits and carry on with your task; read the model only for what is still missing." });
        }
        var text = (Str(args, "text") ?? "").Trim();
        if (text.Length == 0) return ToolResult.Error("Give what to tell the job in 'text'.");
        var sent = board.Tell(me.Key, number.Value, text, now);
        if (sent == JobBoard.Delivery.NotFound) return ToolResult.Error($"There is no job {number}. " + board.Describe(ChatJobs.Limit));
        ChatJobs.Changed?.Invoke();
        return ToolResult.Ok(new { job = number, sent = true, state = sent == JobBoard.Delivery.Reopened ? "it carries on with this message" : sent == JobBoard.Delivery.Soon ? "it gets this with its next step" : "it gets this when its current run ends" });
    }

    /// <summary>Run from a recipe or the self-test (not the chat): lists, never starts or changes a job.</summary>
    public ToolResult Execute(UIApplication app, ToolArgs args) => Answer(args, coordinator: false);

    /// <summary>
    /// The call's answer. <paramref name="coordinator"/>: the main chat of the pane called it (the bridge says so); only
    /// then may it start, message or stop jobs.
    /// </summary>
    internal static ToolResult Answer(ToolArgs args, bool coordinator, string? jobKey = null)
    {
        var action = (Str(args, "action") ?? "list").Trim().ToLowerInvariant();
        var board = ChatJobs.Board;
        var limit = ChatJobs.Limit;
        if (action == "list")
            return board is null ? ToolResult.Ok(new { jobs = "No chat is open in the Sketch ProMax pane, so there are no jobs." }) : new ToolResult(board.Describe(limit));
        if (action is not ("start" or "message" or "stop" or "ask")) return ToolResult.Error("action is start, list, message, stop or ask.");
        // A job working with its teammates: it asks one for what it found, or tells one something.
        if (jobKey is not null && board?.FindKey(jobKey) is { } me) return Teamwork(board, me, action, args);
        if (action == "ask") return ToolResult.Error("ask is for a job asking another job; the main chat reads a job's report with action=list.");
        if (!coordinator || board is null)
            return ToolResult.Error("Jobs run only from the main chat of the Sketch ProMax pane in Revit. Do the work here yourself instead.");
        if (!ChatJobs.Coordinator)
            return ToolResult.Error(SimpleChatRefusal);
        if (!ChatJobs.Available)
            return ToolResult.Error("Jobs aren't available right now (the Sketch ProMax connector is older than the add-in, or the chat is switching). Do the work here yourself.");
        var now = DateTime.UtcNow;
        switch (action)
        {
            case "start" when args.Raw.TryGetValue("parts", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0:
                return StartParts(board, parts, (Str(args, "facts") ?? "").Trim(), Str(args, "model"), limit, now);
            case "start":
            {
                var task = (Str(args, "task") ?? "").Trim();
                var title = (Str(args, "title") ?? "").Trim();
                if (task.Length == 0) return ToolResult.Error("Give the job's whole task in 'task' (and a short 'title').");
                // Free workers: the work goes out divided, not to one worker (unless it can't be divided, and the coordinator says why).
                var idle = Math.Max(0, JobBoard.ClampLimit(limit) - board.OpenCount);
                if (JobBoard.MustDivide(idle, Str(args, "single")))
                    return ToolResult.Ok(new
                    {
                        started = false, free_workers = idle,
                        next = $"Not started: {idle} workers are free, so divide this work between them. Call start again with parts (one per worker, " +
                               "not overlapping: by level, zone, category, sheet range, or reading / checking / changing) and facts (what you already read " +
                               "that every part needs). Only if it truly can't be divided, call start again with the same task and single = why.",
                    });
                var job = board.Start(title, JobBoard.WithDoneWhen(task, Str(args, "done_when")), now, Bool(args, "new"), out var similar);
                if (job is null)
                    return ToolResult.Ok(new
                    {
                        started = false,
                        similar_job = similar!.Number, similar_title = similar.Title, state = JobBoard.StateLabel(similar),
                        next = $"Not started: this reads as a follow-up of job {similar.Number} (\"{similar.Title}\"). Send it there with " +
                               $"action=message, job={similar.Number}. If it really is separate work, call start again with new=true.",
                    });
                var kind = (Str(args, "kind") ?? "").Trim().ToLowerInvariant();
                lock (board.Gate)
                {
                    ChatJobs.Assign(job, kind is ChatAuto.Simple or ChatAuto.Everyday or ChatAuto.Hard ? kind : null);
                    JobBoard.SetRevitModel(job, Str(args, "model"));
                }
                ChatJobs.Changed?.Invoke();
                var waiting = board.OpenCount > JobBoard.ClampLimit(limit);
                var free = Math.Max(0, JobBoard.ClampLimit(limit) - board.OpenCount);
                return ToolResult.Ok(new
                {
                    started = true, job = job.Number, title = job.Title, model = ChatAuto.ModelLabel(job.Model), effort = job.Effort,
                    state = waiting ? "waiting to start (others are running)" : "starting",
                    free_workers = free,
                    next = free > 0
                        ? $"{free} worker(s) are still free. If this job's work can be divided (by level, zone, category, sheet range), divide it now: " +
                          $"message job {job.Number} to keep only its share, and start the other shares with parts. Then tell the user in one line which jobs started and end your turn."
                        : "The user sees its card. Tell them in one line which jobs started, then end your turn: each job reports back when it finishes.",
                });
            }
            case "message":
            {
                var number = Number(args);
                var text = (Str(args, "text") ?? "").Trim();
                if (number is null || text.Length == 0) return ToolResult.Error("Give the job's number in 'job' and what to tell it in 'text'.");
                var sent = board.Message(number.Value, text, now, fromUser: false);
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

    /// <summary>start with parts: one job per part, all at once (they are separate work, so none is refused as a follow-up).</summary>
    private static ToolResult StartParts(JobBoard board, JsonElement parts, string facts, string? model, int limit, DateTime now)
    {
        var list = parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(p => (
            Title: Prop(p, "title"), Task: JobBoard.WithDoneWhen(Prop(p, "task"), Prop(p, "done_when")), Kind: Prop(p, "kind").ToLowerInvariant(), Model: Prop(p, "model") is { Length: > 0 } m ? m : model ?? ""))
            .Where(p => p.Task.Length > 0).Take(12).ToList();
        if (list.Count == 0) return ToolResult.Error("Each part needs its whole task in 'task' (and a short 'title').");
        var titles = list.Select(p => p.Title.Length > 0 ? p.Title : JobBoard.OneLine(p.Task, 40)).ToList();
        var started = new List<object>();
        for (var i = 0; i < list.Count; i++)
        {
            var (title, task, kind, partModel) = list[i];
            // Each part knows what was read for all of them and what the other parts are, so it leaves them alone.
            var others = string.Join("; ", titles.Where((_, k) => k != i));
            var full = task + (facts.Length > 0 ? "\n\nFacts the coordinator already read (use them; don't read them again):\n" + facts : "")
                + (others.Length > 0 ? "\n\nThe other parts, done by other jobs at the same time (leave them alone): " + others + "." : "");
            var job = board.Start(titles[i], full, now, force: true, out _);
            if (job is null) continue;
            lock (board.Gate)
            {
                ChatJobs.Assign(job, kind is ChatAuto.Simple or ChatAuto.Everyday or ChatAuto.Hard ? kind : null);
                JobBoard.SetRevitModel(job, partModel);
            }
            started.Add(new { job = job.Number, title = job.Title, model = ChatAuto.ModelLabel(job.Model), revit_model = job.RevitModel.Length > 0 ? job.RevitModel : null });
        }
        ChatJobs.Changed?.Invoke();
        var waiting = Math.Max(0, board.OpenCount - JobBoard.ClampLimit(limit));
        return ToolResult.Ok(new
        {
            started,
            state = waiting > 0 ? $"{waiting} wait to start until a worker is free" : "all starting",
            next = "Tell the user in one line how you divided the work, then end your turn: each job reports back when it finishes.",
        });
    }

    private static string Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    /// <summary>What start, message and stop say in a Simple chat (ChatTypes).</summary>
    internal const string SimpleChatRefusal =
        "This is a simple chat, so it has no jobs: do the work here yourself. (The user can switch the chat to With coordinator in the chat's header.)";

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
