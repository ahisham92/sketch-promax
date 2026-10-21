using System.Text.Json;
using SketchProMax.Bridge;

namespace SketchProMax.Chat;

/// <summary>One step of a job card: a planned tool from the brief, or a tool Claude added while it worked.</summary>
internal sealed class JobStep
{
    public string Tool { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>waiting, running, done, failed, skipped, stopped or notrun (planned, never reached).</summary>
    public string State { get; set; } = "waiting";
    /// <summary>Why it failed, was skipped or stopped; empty otherwise.</summary>
    public string Reason { get; set; } = "";
    /// <summary>The step's last item count (Total 0: it reported none).</summary>
    public int Done { get; set; }
    public int Total { get; set; }
    /// <summary>From the approved brief (false: Claude added it on the way).</summary>
    public bool Planned { get; set; }
}

/// <summary>
/// The live job card's content, saved in the chat as a "job" item (<see cref="ChatItem.Job"/>) and drawn again after a
/// reload: the steps and how each ended, the thinking shown, and the summary once the task ended. The rules that need no
/// WPF live here: which step a tool call is, what its result means, the thinking's one-line form.
/// </summary>
internal sealed class JobCardModel
{
    public List<JobStep> Steps { get; set; } = [];
    /// <summary>running, done, stopped or failed.</summary>
    public string State { get; set; } = "running";
    public DateTime Started { get; set; }
    public DateTime Ended { get; set; }
    /// <summary>The latest thinking block's text (capped).</summary>
    public string Thinking { get; set; } = "";
    /// <summary>The "Done in X. By hand: about Y. Saved: Z." line, or how long it took.</summary>
    public string Summary { get; set; } = "";
    /// <summary>Elements the task added, changed or deleted.</summary>
    public int Changed { get; set; }

    public const int MaxThinking = 6000;

    /// <summary>Read-only helpers and job plumbing: not steps of their own on the card.</summary>
    public static bool Quiet(string tool) =>
        tool is "wait_for_job" or "cancel_job" or "chat_jobs" or "get_model_context" or "Read" or "Glob" or "Grep" or "help_guide" ||
        tool.StartsWith("get_", StringComparison.Ordinal) || tool.StartsWith("list_", StringComparison.Ordinal) ||
        tool.StartsWith("find_", StringComparison.Ordinal);

    /// <summary>A card for a brief's planned tools (in order, each once).</summary>
    public static JobCardModel FromPlan(IEnumerable<string> tools, DateTime started) => new()
    {
        Started = started,
        Steps = tools.Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)
            .Select(t => new JobStep { Tool = t, Title = JobMath.Title(t), Planned = true }).ToList(),
    };

    /// <summary>True when a task should get a card from the start: a brief that plans two or more steps.</summary>
    public static bool Plans(IReadOnlyCollection<string> tools, string kind) =>
        tools.Count(t => !Quiet(t)) >= 2 || kind == "multi_step" && tools.Count >= 1;

    public JobStep? Running => Steps.LastOrDefault(s => s.State == "running");

    /// <summary>
    /// A tool call started: the first planned step of that tool still waiting, else a new step (null for a quiet tool,
    /// which is not a step of its own).
    /// </summary>
    public JobStep? Begin(string tool)
    {
        var step = Steps.FirstOrDefault(s => s.Planned && s.State == "waiting" && s.Tool == tool);
        if (step is null)
        {
            if (Quiet(tool)) return null;
            step = new JobStep { Tool = tool, Title = JobMath.Title(tool) };
            Steps.Add(step);
        }
        step.State = "running";
        step.Reason = "";
        return step;
    }

    /// <summary>What a tool call's result means for its step: running (still running in Revit), done, failed, skipped or stopped, with the reason.</summary>
    public static (string State, string Reason) Outcome(string result, bool isError)
    {
        if (result.Contains("\"status\":\"still running in Revit\"")) return ("running", "");
        if (result.Contains(JobControl.PausedText)) return ("running", "");
        // A step that can only finish whole says it kept nothing: the card says so, not "finished items kept".
        if (result.Contains(JobControl.SkippedNothingKeptText.Substring(0, 60))) return ("skipped", "skipped by you; this step can only finish whole, so nothing of it was kept");
        if (result.Contains(JobControl.StoppedNothingKeptText.Substring(0, 60))) return ("stopped", "stopped by you; this step can only finish whole, so it was rolled back and nothing of it kept");
        if (result.Contains(JobControl.SkippedText.Substring(0, 30))) return ("skipped", "skipped by you (Skip step)");
        if (result.Contains(JobControl.StoppedText.Substring(0, 30))) return ("stopped", "stopped by you; the item in progress was rolled back, finished items kept");
        if (!isError) return ("done", "");
        var first = result.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return ("failed", first.Length > 200 ? first.Substring(0, 200) + "…" : first);
    }

    /// <summary>A tool call ended with <paramref name="state"/> (see <see cref="Outcome"/>).</summary>
    public void End(JobStep step, string state, string reason)
    {
        step.State = state;
        step.Reason = reason;
        if (state == "done" && step.Total > 0) step.Done = step.Total;
    }

    /// <summary>
    /// The task ended (<paramref name="state"/>: done, stopped or failed): steps still running end with it, planned steps
    /// never reached say so.
    /// </summary>
    public void Finish(string state, DateTime ended, string summary, int changed)
    {
        State = state;
        Ended = ended;
        Summary = summary;
        Changed = changed;
        foreach (var s in Steps)
        {
            if (s.State == "running") { s.State = state == "done" ? "done" : "stopped"; if (state != "done") s.Reason = "stopped when the task ended"; }
            else if (s.State == "waiting") { s.State = "notrun"; s.Reason = state == "done" ? "not needed: finished without it" : "not reached"; }
        }
    }

    /// <summary>Thinking text arrived: a new block replaces the last one, deltas add to it.</summary>
    public void Think(string text, bool newBlock)
    {
        Thinking = newBlock ? text : Thinking + text;
        if (Thinking.Length > MaxThinking) Thinking = "…" + Thinking.Substring(Thinking.Length - MaxThinking);
    }

    /// <summary>The thinking as one line: its latest non-empty line, cut to <paramref name="max"/> characters.</summary>
    public static string ThinkingLine(string thinking, int max = 140)
    {
        var line = thinking.Replace("\r", "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        line = line.TrimStart('#', '*', '-', ' ').Replace("**", "");
        return line.Length <= max ? line : line.Substring(0, max - 1) + "…";
    }

    /// <summary>"3 of 5 steps done" (failed, skipped and stopped steps count as finished).</summary>
    public string Headline()
    {
        var counted = Steps.Where(s => s.State != "notrun").ToList();
        var finished = counted.Count(s => s.State is "done" or "failed" or "skipped" or "stopped");
        return counted.Count == 0 ? "Starting" : $"{finished} of {counted.Count} step{(counted.Count == 1 ? "" : "s")} done";
    }

    /// <summary>The finished card's summary line: the time line, then elements changed.</summary>
    public string FinalLine()
    {
        var took = Ended > Started && Started != default ? JobMath.Duration(Ended - Started) : "";
        var head = State switch
        {
            "stopped" => Summary.Length > 0 || took.Length == 0 ? "Stopped." : $"Stopped after {took}.",
            "failed" => Summary.Length > 0 || took.Length == 0 ? "Ended with a problem." : $"Ended with a problem after {took}.",
            _ => "",
        };
        var time = Summary.Length > 0 ? Summary : State == "done" && took.Length > 0 ? $"Done in {took}." : "";
        var changed = Changed > 0 ? $"{Changed} element{(Changed == 1 ? "" : "s")} changed." : "Nothing in the model changed.";
        return string.Join(" ", new[] { head, time, changed }.Where(s => s.Length > 0));
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>The saved card, or null when unreadable.</summary>
    public static JobCardModel? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var model = JsonSerializer.Deserialize<JobCardModel>(json!);
            if (model is null) return null;
            model.Steps ??= [];
            model.Thinking ??= "";
            model.Summary ??= "";
            return model;
        }
        catch (JsonException) { return null; }
    }
}
