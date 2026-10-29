using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Does again, exactly, model work the chat already did (RedoLog): after Revit closed before the model was saved, on a
/// new local copy, or on a fresh copy of the model. action=list shows the work as tasks with each step's state (still
/// in the model, lost, or can't tell); action=replay runs the chosen steps again with their own arguments and code,
/// one Revit call per step, carrying the ids of elements made again into the steps after them.
/// </summary>
public sealed class RedoStepsTool : IRevitTool
{
    public string Name => "redo_steps";
    public string Description =>
        "Do again exactly what the chat already did to this model (every change that worked is kept with its full arguments " +
        "and code). Use it when the user wants a task redone: Revit closed or the model crashed before it was saved, the " +
        "work was lost, or the same work is wanted on a fresh copy. action=list (default) shows the recent work as tasks, " +
        "each step with what it did and whether its result is still in the model, lost, or can't be told; since/until " +
        "narrow it, and model reads another model's work (e.g. the copy it was done on). action=replay with steps (the " +
        "numbers from list), or with from and to, runs them again in order, one at a time; by default steps whose result " +
        "is still in the model are skipped. Elements made again get new ids, which are passed on to the later steps. For " +
        "work the user did by hand, Lifeline (Recover) replays it instead.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["list", "replay"], "description": "Default list."},
      "model": {"type": "string", "description": "Whose work: a model name or words of it. Default the open model."},
      "since": {"type": "string", "description": "list: from this time, 'HH:mm' today or 'yyyy-MM-dd HH:mm'."},
      "until": {"type": "string", "description": "list: up to this time."},
      "steps": {"type": "array", "items": {"type": "integer"}, "description": "replay: the step numbers to run again, in order."},
      "from": {"type": "integer", "description": "replay: the first step number (with to)."},
      "to": {"type": "integer", "description": "replay: the last step number."},
      "include_done": {"type": "boolean", "description": "replay: also run steps whose result is still in the model (default false)."},
      "stop_on_error": {"type": "boolean", "description": "replay: stop at the first step that fails (default false: carry on and report)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    private readonly ToolRegistry _registry;
    public RedoStepsTool(ToolRegistry registry) => _registry = registry;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var (model, why) = Source(doc, args.OptString("model"));
        if (model is null) return ToolResult.Error(why!);
        var steps = RedoLog.Read(model);
        var action = (args.OptString("action") ?? "list").Trim().ToLowerInvariant();
        if (action == "list") return ListWork(doc, model, steps, args);
        if (action != "replay") throw new ArgumentException("action is list or replay.");

        var wanted = args.LongList("steps").Select(n => (int)n).ToList();
        if (wanted.Count == 0 && args.OptLong("from") is { } from)
        {
            var to = args.OptLong("to") ?? from;
            wanted = steps.Where(s => s.Number >= from && s.Number <= to).Select(s => s.Number).ToList();
        }
        if (wanted.Count == 0) return ToolResult.Error("Say which steps to run again: steps (numbers from action=list), or from and to.");
        var chosen = wanted.Distinct().Select(n => steps.FirstOrDefault(s => s.Number == n)).OfType<RedoLog.Step>().OrderBy(s => s.Number).ToList();
        var missing = wanted.Where(n => chosen.All(s => s.Number != n)).ToList();
        if (chosen.Count == 0) return ToolResult.Error($"No step with those numbers in {model}'s work. Call action=list first.");
        var includeDone = args.OptBool("include_done");
        var stopOnError = args.OptBool("stop_on_error");

        if (Bridge.Jobs.CanContinue(Name) is { } job)
        {
            job.Continuation = ReplayAsync(job, chosen, includeDone, stopOnError, model, missing);
            Bridge.Jobs.Report($"Redoing {chosen.Count} step(s)");
            return ToolResult.Ok(new
            {
                status = "redoing", job = job.Id, steps = chosen.Count,
                next = $"Each step runs as its own Revit call. Call wait_for_job (job_id {job.Id}) for the result.",
            });
        }
        // From a recipe or the tests: in this call.
        var map = new Dictionary<long, long>();
        var report = chosen.Select(s => RunOne(app, s, map, includeDone)).ToList();
        return Finish(report, map, model, missing);
    }

    private async System.Threading.Tasks.Task<ToolResult> ReplayAsync(Bridge.Job job, List<RedoLog.Step> chosen, bool includeDone, bool stopOnError, string model, List<int> missing)
    {
        var map = new Dictionary<long, long>();
        var report = new List<StepReport>();
        try
        {
            for (var i = 0; i < chosen.Count; i++)
            {
                if (job.CancelRequested)
                {
                    report.AddRange(chosen.Skip(i).Select(s => new StepReport(s.Number, s.Tool, RedoLog.Describe(s), "not done: stopped on request", null)));
                    break;
                }
                var step = chosen[i];
                Bridge.Jobs.Report($"Redo step {i + 1} of {chosen.Count}: {RedoLog.Describe(step)}");
                var one = await App.Dispatcher.InvokeAsync(app => RunOne(app, step, map, includeDone)).ConfigureAwait(false);
                report.Add(one);
                if (stopOnError && one.Outcome.StartsWith("failed", StringComparison.Ordinal))
                {
                    report.AddRange(chosen.Skip(i + 1).Select(s => new StepReport(s.Number, s.Tool, RedoLog.Describe(s), "not done: stopped after a failed step", null)));
                    break;
                }
            }
            return Finish(report, map, model, missing);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"The redo stopped ({ex.GetType().Name}: {ex.Message}) after {report.Count} step(s); the steps done are kept. " +
                                    "Call action=list to see what is still lost.");
        }
        finally
        {
            job.Finished = DateTime.UtcNow;
            if (Bridge.Jobs.Running == job) Bridge.Jobs.Running = null;
        }
    }

    private sealed record StepReport(int Step, string Tool, string What, string Outcome, string? Detail);

    private StepReport RunOne(UIApplication app, RedoLog.Step step, Dictionary<long, long> map, bool includeDone)
    {
        var doc = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No model is open in Revit any more.");
        var what = RedoLog.Describe(step);
        var state = State(doc, step);
        if (!includeDone && state == "in the model") return new(step.Number, step.Tool, what, "skipped: its result is still in the model", null);
        var tool = _registry.Find(step.Tool);
        if (tool is null) return new(step.Number, step.Tool, what, "failed: that tool isn't there any more", null);

        var lost = Created(step).Where(id => doc.GetElement(new ElementId(id)) is null).ToList();
        var text = RedoLog.Remap(step.Arguments, map);
        Dictionary<string, JsonElement> args;
        using (var parsed = JsonDocument.Parse(text))
            args = parsed.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        ToolResult result;
        RunningCall.Begin("a redo", tool.Name, doc.Title, args);
        try
        {
            result = FailureGuard.Run(() =>
            {
                try { return tool.Execute(app, new ToolArgs(args)); }
                catch (Exception ex) { return ToolResult.Error($"{ex.GetType().Name}: {ex.Message}"); }
            });
        }
        finally { RunningCall.End(); }
        ActivityLog.Write(doc.Title, tool, args, result);
        if (!result.IsError) RedoLog.Write(doc.Title, tool.Name, text, result.Text);

        // The elements made again, in the order the step reports them, stand in for the lost ones in later steps.
        var inputs = RedoLog.Ids(text).ToHashSet();
        var made = RedoLog.Ids(result.Text).Where(id => !inputs.Contains(id) && doc.GetElement(new ElementId(id)) is not null).ToList();
        var mapped = 0;
        if (!result.IsError && lost.Count > 0 && made.Count == lost.Count)
            for (var k = 0; k < lost.Count; k++) { map[lost[k]] = made[k]; mapped++; }
        var detail = result.Text.Length > 300 ? result.Text.Substring(0, 300) + "…" : result.Text;
        return new(step.Number, step.Tool, what, result.IsError ? "failed" : mapped > 0 ? $"done ({mapped} new element ids passed on)" : "done", detail);
    }

    private static ToolResult Finish(List<StepReport> report, Dictionary<long, long> map, string model, List<int> missing) => ToolResult.Ok(new
    {
        from = model,
        done = report.Count(r => r.Outcome.StartsWith("done", StringComparison.Ordinal)),
        skipped = report.Count(r => r.Outcome.StartsWith("skipped", StringComparison.Ordinal)),
        failed = report.Count(r => r.Outcome.StartsWith("failed", StringComparison.Ordinal)),
        steps = report.Select(r => new { step = r.Step, tool = r.Tool, what = r.What, outcome = r.Outcome, detail = r.Outcome == "done" ? null : r.Detail }),
        newIds = map.Count > 0 ? map.Count : (int?)null,
        unknownSteps = missing.Count > 0 ? missing : null,
        next = "Tell the user in a few lines what was redone and what failed, then check the result (read the elements back or capture the view) before saying it matches.",
    });

    // ---- Listing ----------------------------------------------------------------------------------------------------

    private static ToolResult ListWork(Document doc, string model, List<RedoLog.Step> steps, ToolArgs args)
    {
        var since = Time(args.OptString("since"));
        var until = Time(args.OptString("until"));
        var shown = steps.Where(s => (since is null || s.Time >= since) && (until is null || s.Time <= until)).ToList();
        if (shown.Count == 0)
            return ToolResult.Ok(new
            {
                model, steps = 0,
                note = steps.Count == 0 ? $"No chat work is recorded for {model}." : "No recorded work in that time.",
                otherModels = RedoLog.Models().Take(10).Select(m => new { model = m.Model, last = m.Last.ToString("yyyy-MM-dd HH:mm") }),
            });
        var tasks = RedoLog.Tasks(shown);
        var latest = tasks.Skip(Math.Max(0, tasks.Count - 8)).ToList();
        return ToolResult.Ok(new
        {
            model,
            tasks = latest.Select(t =>
            {
                var rows = t.Steps.Select(s => new { step = s.Number, time = s.Time.ToString("HH:mm"), tool = s.Tool, what = RedoLog.Describe(s), state = State(doc, s) }).ToList();
                return new
                {
                    from = t.Start.ToString("yyyy-MM-dd HH:mm"), to = t.End.ToString("HH:mm"), first = t.First, last = t.Last,
                    lost = rows.Count(r => r.state != "in the model"), steps = rows.Count > 60 ? rows.Skip(rows.Count - 60).ToList() : rows,
                };
            }),
            earlierTasks = tasks.Count > latest.Count ? tasks.Count - latest.Count : (int?)null,
            next = "Show the user the task(s) they mean in a line or two (time, what it did, how many steps are lost), then replay " +
                   "with from/to (or steps). They approve it once in Revit.",
        });
    }

    /// <summary>"in the model" when every element the step reported making is there, "lost" when none is, else "partly lost" or "can't tell".</summary>
    private static string State(Document doc, RedoLog.Step s)
    {
        var made = Created(s);
        if (made.Count == 0) return "can't tell";
        var there = made.Count(id => doc.GetElement(new ElementId(id)) is not null);
        return there == made.Count ? "in the model" : there == 0 ? "lost" : "partly lost";
    }

    /// <summary>Ids in the step's result that were not among its inputs: what it made (or changed and reported).</summary>
    private static List<long> Created(RedoLog.Step s)
    {
        var inputs = RedoLog.Ids(s.Arguments).ToHashSet();
        return RedoLog.Ids(s.Result).Where(id => !inputs.Contains(id)).Take(500).ToList();
    }

    private static DateTime? Time(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text!.Trim();
        if (DateTime.TryParseExact(t, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var full)) return full;
        if (TimeSpan.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var clock)) return DateTime.Today + clock;
        throw new ArgumentException($"'{text}' is not a time: use HH:mm or yyyy-MM-dd HH:mm.");
    }

    /// <summary>Whose work to read: the named model's, else the open model's, else (nothing recorded for it) a model whose name it starts with.</summary>
    private static (string? Model, string? Why) Source(Document doc, string? wanted)
    {
        var models = RedoLog.Models();
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var w = wanted!.Trim();
            var hit = models.FirstOrDefault(m => string.Equals(m.Model, w, StringComparison.OrdinalIgnoreCase)).Model
                      ?? models.FirstOrDefault(m => m.Model.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0).Model;
            return hit is null
                ? (null, $"No recorded work for a model like '{w}'. Models with work: {string.Join(", ", models.Take(10).Select(m => m.Model))}.")
                : (hit, null);
        }
        if (RedoLog.Read(doc.Title).Count > 0) return (doc.Title, null);
        // A new local copy or a "_detached" copy of the model the work was done on.
        var stem = doc.Title;
        foreach (var cut in new[] { "_detached", "_" })
        {
            var at = stem.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
            if (at > 0) { stem = stem.Substring(0, at); break; }
        }
        var near = models.FirstOrDefault(m => m.Model.StartsWith(stem, StringComparison.OrdinalIgnoreCase)).Model;
        return (near ?? doc.Title, null);
    }
}
