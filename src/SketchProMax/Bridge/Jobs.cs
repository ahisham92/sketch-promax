using System.Text;
using System.Text.Json;
using Autodesk.Revit.UI;
using SketchProMax.Tools;

namespace SketchProMax.Bridge;

/// <summary>
/// Every tool call is a job. A call that runs longer than the connector can wait for is not lost: the job keeps running
/// in Revit, the call returns its id, and wait_for_job collects the result (or reports progress) later. Revit runs one
/// job at a time; a call that arrives meanwhile is told which job is running instead of hanging until it times out.
/// </summary>
public sealed class Job
{
    public int Id { get; init; }
    public string Tool { get; init; } = "";
    public DateTime Queued { get; } = DateTime.UtcNow;
    public DateTime? Started { get; internal set; }
    public DateTime? Finished { get; internal set; }
    public Task<ToolResult> Task { get; internal set; } = null!;
    internal readonly TaskCompletionSource<bool> StartedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly CancellationTokenSource Abandon = new();
    public volatile bool CancelRequested;

    /// <summary>Where the job writes notes as it goes (run_revit_code's log); read for progress.</summary>
    internal StringBuilder? Log;

    /// <summary>A short progress line set by built-in tools (for example "120 of 400 views").</summary>
    internal volatile string? ProgressText;

    public TimeSpan Elapsed => (Finished ?? DateTime.UtcNow) - (Started ?? Queued);

    /// <summary>The latest progress: the tool's own line, else the last lines of its log. Safe to call from any thread.</summary>
    public string? Progress()
    {
        if (ProgressText is { } p) return p;
        if (Log is null) return null;
        try
        {
            var text = Log.ToString();
            return text.Length <= 600 ? text : "…" + text.Substring(text.Length - 600);
        }
        catch (Exception) { return null; } // written to on Revit's thread at this moment; ask again later
    }
}

public static class Jobs
{
    private static readonly object Gate = new();
    private static readonly List<Job> Recent = [];
    private static int _next;

    /// <summary>The job Revit is running right now, if any.</summary>
    public static Job? Running { get; internal set; }

    /// <summary>True when the user (through cancel_job) asked the running job to stop. Long loops should check it.</summary>
    public static bool CancelRequested => Running?.CancelRequested == true;

    /// <summary>Sets the running job's progress line, shown by wait_for_job.</summary>
    public static void Report(string text)
    {
        if (Running is { } job) job.ProgressText = text;
    }

    internal static Job Create(string tool)
    {
        lock (Gate)
        {
            var job = new Job { Id = ++_next, Tool = tool };
            Recent.Add(job);
            if (Recent.Count > 20) Recent.RemoveAt(0);
            return job;
        }
    }

    public static Job? Find(int? id)
    {
        lock (Gate) return id is { } n ? Recent.FirstOrDefault(j => j.Id == n) : Recent.LastOrDefault();
    }

    internal static string Describe(Job job)
    {
        var state = job.Task?.IsCompleted == true ? "finished" : job.Started is null ? "waiting for Revit" : "running";
        return $"job {job.Id} ({job.Tool}, {state}, {Duration(job.Elapsed)})";
    }

    internal static string Duration(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{Math.Max(0, (int)t.TotalSeconds)} s";

    /// <summary>The answer for a call that can't run because another job is: what is running and what to do.</summary>
    internal static ToolResult Busy(Job? running) => ToolResult.Error(
        (running is null
            ? "Revit is busy finishing an earlier request."
            : $"Revit is still running {Describe(running)}.{(running.Progress() is { } p ? $" Progress: {p}" : "")}") +
        " Call wait_for_job to wait for it and get its result, or cancel_job to ask it to stop. Don't send other model work meanwhile.");

    /// <summary>A job's state as a tool result: its own result once finished, otherwise how it is getting on.</summary>
    internal static ToolResult Status(Job job)
    {
        if (job.Task is { IsCompleted: true } done)
        {
            if (done.IsCanceled) return ToolResult.Error($"Job {job.Id} ({job.Tool}) was cancelled before Revit started it; nothing was changed.");
            if (done.IsFaulted) return ToolResult.Error($"Job {job.Id} ({job.Tool}) failed: {done.Exception?.GetBaseException().Message}");
            var r = done.Result;
            return r with { Text = $"Job {job.Id} ({job.Tool}) finished after {Duration(job.Elapsed)}{(job.CancelRequested ? " (stop was requested)" : "")}:\n{r.Text}" };
        }
        return ToolResult.Ok(new
        {
            job = job.Id, tool = job.Tool,
            status = job.Started is null ? "waiting for Revit to start it" : "running",
            elapsed = Duration(job.Elapsed),
            stopRequested = job.CancelRequested ? true : (bool?)null,
            progress = job.Progress(),
            next = "Call wait_for_job again to keep waiting. Revit can't take other model work until it finishes.",
        });
    }
}

/// <summary>Waits for a job started by an earlier call and returns its result. Answered by the bridge, not Revit's thread.</summary>
public sealed class WaitForJobTool : IRevitTool
{
    public string Name => "wait_for_job";
    public string Description =>
        "Get the result of a long tool call that was still running when its call returned (it said 'still running as job N'), " +
        "or see what Revit is busy with. Waits up to wait_seconds for the job to finish, then returns its result, or its " +
        "progress so far if it is still running. Call again to keep waiting.";
    public string PropertiesJson => """
    {
      "job_id": {"type": "integer", "description": "The job number. Default: the latest job."},
      "wait_seconds": {"type": "integer", "description": "How long to wait for it to finish, 0-50 (default 40)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args) => Answer(args, waited: true);

    internal static ToolResult Answer(ToolArgs args, bool waited)
    {
        var job = Jobs.Find(args.OptLong("job_id") is { } id ? (int)id : null);
        if (job is null) return ToolResult.Ok(new { status = "no jobs", note = "No tool call has run in this Revit session yet (or that job number is too old)." });
        return Jobs.Status(job);
    }

    internal static async Task<ToolResult> AnswerAsync(ToolArgs args, CancellationToken ct)
    {
        var job = Jobs.Find(args.OptLong("job_id") is { } id ? (int)id : null);
        if (job?.Task is { IsCompleted: false } task)
        {
            var wait = Math.Max(0, Math.Min(50, (int)(args.OptLong("wait_seconds") ?? 40)));
            try { await System.Threading.Tasks.Task.WhenAny(task, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(wait), ct)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        return Answer(args, waited: true);
    }
}

/// <summary>Asks the running (or queued) job to stop. Answered by the bridge, not Revit's thread.</summary>
public sealed class CancelJobTool : IRevitTool
{
    public string Name => "cancel_job";
    public string Description =>
        "Ask a long-running tool call to stop. A job still waiting for Revit is dropped. A running one stops at its next " +
        "check: batch tools stop between items (what is done so far is kept as one Undo step), and run_revit_code / " +
        "run_revit_query_code stop where the code calls CheckCancel(). Revit itself can't be interrupted in the middle of " +
        "one API call (a regeneration, a big boolean), so that part always runs to the end.";
    public string PropertiesJson => """
    {
      "job_id": {"type": "integer", "description": "Default: the running job."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args) => Cancel(args);

    internal static ToolResult Cancel(ToolArgs args)
    {
        var job = args.OptLong("job_id") is { } id ? Jobs.Find((int)id) : Jobs.Running ?? Jobs.Find(null);
        if (job is null || job.Task is { IsCompleted: true })
            return ToolResult.Ok(new { status = "nothing to cancel", job = job?.Id });
        job.CancelRequested = true;
        if (job.Started is null)
        {
            try { job.Abandon.Cancel(); } catch (ObjectDisposedException) { }
            return ToolResult.Ok(new { status = "dropped before it started", job = job.Id, tool = job.Tool });
        }
        return ToolResult.Ok(new
        {
            status = "stop requested", job = job.Id, tool = job.Tool, elapsed = Jobs.Duration(job.Elapsed),
            note = "It stops at its next check. Call wait_for_job to see when it has stopped and what it kept.",
        });
    }
}
