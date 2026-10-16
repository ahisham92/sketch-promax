using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Commands;

namespace SketchProMax.UI;

/// <summary>
/// The core's side of the "Sketch ProMax" dockable pane. The pane itself (a WPF page) belongs to the loader, because
/// dockable panes can only be registered while Revit starts and the core is switched at runtime; the loader asks the
/// running core for <see cref="Snapshot"/> (CoreEntry.Query("pane")) about once a second while the pane is visible,
/// and sends the pane's clicks back as CoreEntry.Command("pane-select\t…") / ("pane-cancel").
///
/// The snapshot is plain text, one record per line, fields separated by tabs (values never contain tabs or newlines):
///   pane    1                                         format version
///   version 0.27.0
///   claude  state  text  detail                       state: connected / ready / error / off
///   job     state  id  tool  elapsed  progress        state: running / waiting / selftest / idle
///   log     line                                      up to 50, the step log of the running (else the last) job
///   last    id  tool  outcome  finished  elapsed      outcome: ok / error / cancelled / stopped / failed
///   result  line                                      up to 40, the last job's result (JSON pretty-printed)
///   ids     id,id,…                                   element ids found in the log and result (at most 50)
/// Quick tools come from the existing "quick" query, which older cores answer too.
/// </summary>
internal static class Pane
{
    private const int MaxLog = 50, MaxResult = 40, MaxIds = 50, MaxLine = 300;

    // JSON: "id": 123, "element_id": "123", "ids": [1, 2], "wallIds": [...], "hostId": 5; not "grid" or "solid".
    // Job numbers are not elements.
    private static readonly Regex JsonIds = new(
        @"""(?<key>(?:[A-Za-z]*_)?(?:id|ids|ID|IDs)|[A-Za-z_]*[a-z](?:Id|Ids|ID|IDs))""\s*:\s*(?<value>\[[^\]]*\]|""?-?\d+""?)", RegexOptions.CultureInvariant);

    // Prose: "id 123", "ids: 1, 2, 3", "element 123", "elements 1,2", "#123" after "element".
    private static readonly Regex TextIds = new(
        @"\b(?:ids?|element(?:\s*id)?s?|ElementId)\b\s*[:=#]?\s*(?<list>\d{2,}(?:\s*,\s*\d{2,})*)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex Number = new(@"\d+", RegexOptions.CultureInvariant);

    public static string Snapshot()
    {
        var sb = new StringBuilder();
        void Line(params object?[] fields)
        {
            sb.Append(string.Join("\t", fields.Select(f => Clean(Convert.ToString(f, CultureInfo.InvariantCulture) ?? ""))));
            sb.Append('\n');
        }

        Line("pane", 1);
        Line("version", Update.Updater.CurrentText);
        try
        {
            var (state, text, detail) = Connection.Describe();
            Line("claude", state, text.Replace('\n', ' '), detail);
        }
        catch (Exception) { Line("claude", "off", "unknown", ""); }

        var ids = new List<long>();
        var latest = Jobs.Find(null);
        var running = Jobs.Running ?? (latest is { Task: null or { IsCompleted: false } } && !IsAbandoned(latest) ? latest : null);

        if (running is not null)
        {
            var waiting = running.Started is null;
            Line("job", waiting ? "waiting" : "running", running.Id, running.Tool, Jobs.Duration(running.Elapsed),
                waiting ? "Waiting for Revit to start it." : running.ProgressText ?? (running.CancelRequested ? "Stop requested." : ""));
        }
        else if (Tools.SelfTest.IsRunning)
            Line("job", "selftest", "", "Self-test", "", Tools.SelfTest.Progress());
        else
            Line("job", "idle", "", "", "", "");

        // The last finished job: the latest one, or the one before the running one.
        var last = latest is not null && !ReferenceEquals(latest, running) ? latest : null;
        if (last is null && latest is not null)
            for (var id = latest.Id - 1; id > 0 && id > latest.Id - 25; id--)
                if (Jobs.Find(id) is { } j && j.Task is { IsCompleted: true }) { last = j; break; }

        // Step log: the running job's, else the last one's.
        var logJob = running ?? last;
        foreach (var line in LogTail(logJob))
        {
            Line("log", line);
            Collect(line, ids);
        }

        if (last is not null)
        {
            var (outcome, text) = Outcome(last);
            Line("last", last.Id, last.Tool, outcome, last.Finished?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
                Jobs.Duration(last.Elapsed));
            foreach (var line in ResultLines(text))
            {
                Line("result", line);
                Collect(line, ids);
            }
            CollectJson(text, ids);
        }

        if (ids.Count > 0) Line("ids", string.Join(",", ids.Distinct().Take(MaxIds).Select(i => i.ToString(CultureInfo.InvariantCulture))));
        return sb.ToString();
    }

    private static bool IsAbandoned(Job job) => job.Task is { IsCanceled: true };

    private static IEnumerable<string> LogTail(Job? job)
    {
        if (job?.Log is not { } log) return [];
        string text;
        try { text = log.ToString(); }
        catch (Exception) { return []; } // being written on Revit's thread right now; next poll
        if (text.Length > 20000) text = text.Substring(text.Length - 20000);
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        return lines.Skip(Math.Max(0, lines.Count - MaxLog)).Select(Short);
    }

    private static (string Outcome, string Text) Outcome(Job job)
    {
        var task = job.Task;
        if (task is null || !task.IsCompleted) return ("running", "");
        if (task.IsCanceled) return ("cancelled", "Cancelled before Revit started it; nothing was changed.");
        if (task.IsFaulted) return ("failed", task.Exception?.GetBaseException().Message ?? "failed");
        var r = task.Result;
        return (r.IsError ? "error" : job.CancelRequested ? "stopped" : "ok", r.Text ?? "");
    }

    private static IEnumerable<string> ResultLines(string text)
    {
        if (text.Length == 0) return [];
        var shown = text.Length > 40000 ? text.Substring(0, 40000) : text;
        var trimmed = shown.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(shown);
                shown = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            }
            catch (JsonException) { /* not JSON after all (or cut off): show as text */ }
        }
        var lines = shown.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var result = lines.Take(MaxResult).Select(Short).ToList();
        if (lines.Count > MaxResult) result.Add($"… {lines.Count - MaxResult} more line(s); ask Claude for the full result.");
        return result;
    }

    private static void Collect(string text, List<long> ids)
    {
        if (ids.Count >= MaxIds * 2) return;
        foreach (Match m in TextIds.Matches(text))
            foreach (Match n in Number.Matches(m.Groups["list"].Value))
                Add(n.Value, ids);
        CollectJson(text, ids);
    }

    private static void CollectJson(string text, List<long> ids)
    {
        if (ids.Count >= MaxIds * 2) return;
        foreach (Match m in JsonIds.Matches(text.Length > 200000 ? text.Substring(0, 200000) : text))
        {
            var key = m.Groups["key"].Value.ToLowerInvariant();
            if (key.Contains("job")) continue;
            foreach (Match n in Number.Matches(m.Groups["value"].Value))
                Add(n.Value, ids);
        }
    }

    private static void Add(string digits, List<long> ids)
    {
        // Element ids are positive; tiny numbers are almost always counts or indexes, not elements.
        if (digits.Length < 2 || digits.Length > 18) return;
        if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 10 && !ids.Contains(id))
            ids.Add(id);
    }

    private static string Short(string line)
    {
        line = line.TrimEnd();
        return line.Length <= MaxLine ? line : line.Substring(0, MaxLine - 1) + "…";
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// "pane-select\t1,2,3": selects the elements that exist in the active document and zooms to them. A single view id
    /// opens that view instead. Called by the loader from an external event, so in a valid API context.
    /// </summary>
    public static Result Select(UIApplication app, string list)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc is null || doc is null)
        {
            TaskDialog.Show("Sketch ProMax", "Open the model these elements are in first.");
            return Result.Cancelled;
        }
        var wanted = list.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .Where(v => v > 0).Distinct().Take(500).ToList();
        var found = new List<ElementId>();
        View? view = null;
        foreach (var value in wanted)
        {
            var element = doc.GetElement(new ElementId(value));
            switch (element)
            {
                case null: continue;
                case View v when !v.IsTemplate && wanted.Count == 1: view = v; break;
                case View or ElementType: continue; // can't be selected in a view
                default: found.Add(element.Id); break;
            }
        }
        if (view is not null)
        {
            try { uidoc.RequestViewChange(view); return Result.Succeeded; }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                TaskDialog.Show("Sketch ProMax", $"Couldn't open the view {view.Name}: {ex.Message}\n\nOpen it from the Project Browser instead.");
                return Result.Failed;
            }
        }
        if (found.Count == 0)
        {
            TaskDialog.Show("Sketch ProMax", wanted.Count == 1
                ? $"Element {wanted[0]} is not in the active model ({doc.Title}). It may have been deleted, be in another model, or be a type.\n\nSwitch to the model the job ran in and click it again."
                : $"None of these {wanted.Count} elements is in the active model ({doc.Title}).\n\nSwitch to the model the job ran in and click again.");
            return Result.Cancelled;
        }
        uidoc.Selection.SetElementIds(found);
        try { uidoc.ShowElements(found); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { /* selected, but no open view can show them: fine */ }
        return Result.Succeeded;
    }

    /// <summary>"pane-cancel": asks the running job (or self-test) to stop, quietly; the pane shows the outcome.</summary>
    public static Result Cancel()
    {
        if (Jobs.Running is null && Tools.SelfTest.IsRunning)
        {
            Tools.SelfTest.Stop();
            return Result.Succeeded;
        }
        CancelJobTool.Cancel(new Tools.ToolArgs(new Dictionary<string, JsonElement>()));
        return Result.Succeeded;
    }
}
