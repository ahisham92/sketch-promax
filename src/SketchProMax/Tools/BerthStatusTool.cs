using System.Globalization;
using System.IO;
using Autodesk.Revit.UI;
using static SketchProMax.Tools.BerthStatusMath;

namespace SketchProMax.Tools;

/// <summary>
/// The berth status tracker for the chat: the status of every berth (or one, with what to fix), and setting a
/// percentage or comment on an area or deliverable (design sections, calculations, bills, costing) or the expected
/// finish, as the user reports them. It never reads the model: the Berth status window's Update status does, in steps.
/// </summary>
public sealed class BerthStatusTool : IRevitTool
{
    public string Name => "berth_status";
    public string Description =>
        "The berth status tracker (Berth status button): 'list' gives every berth's status %, verdict, next submission (due at its time, 08:00 by default, " +
        "working hours left) and expected finish; 'details' gives one berth's areas, deliverables, comments and what to fix; 'set' records what the user " +
        "reports (a percentage and/or comment on an area such as Drawings or a deliverable such as Calculations or Design of sections, or the berth's " +
        "comment with what 'berth'); 'finish' sets the expected finish by hand. It doesn't read the model: tell the user to press Update status in the " +
        "Berth status window for fresh model results. Berths are named as in the setup, or by their code (T3, A1).";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["list", "details", "set", "finish"]},
      "berth": {"type": "string", "description": "details, set, finish: the berth's name or code."},
      "what": {"type": "string", "description": "set: an area (Modelling, Categories and data, Services and openings, Structural clashes, Drawings, Details, Annotation, Schedules and bills, Deliverables), a deliverable name, or 'berth' for the berth's comment."},
      "percent": {"type": "number", "description": "set: 0-100, replaces what the model or files say."},
      "clear": {"type": "boolean", "description": "set: drop the hand percentage (back to the model or files)."},
      "comment": {"type": "string", "description": "set: the comment to keep (shown in the window and the Excel)."},
      "finish": {"type": "string", "description": "finish: yyyy-MM-dd HH:mm, or 'clear' to go back to the forecast from the readings."},
      "project": {"type": "string", "description": "The project code; default: the open model's."}
    }
    """;
    public string[] Required => ["action"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var project = args.OptString("project")?.Trim() ?? "";
        if (project.Length == 0 && app.ActiveUIDocument?.Document is { IsFamilyDocument: false } doc)
        {
            var file = "";
            try { file = Path.GetFileName(doc.PathName ?? ""); } catch (ArgumentException) { }
            project = ChecklistProjects.ProjectKey(file.Length > 0 ? file : doc.Title);
        }
        if (project.Length == 0 || LoadSetup(project) is null)
        {
            var all = Projects();
            if (all.Count == 1) project = all[0];
            else if (LoadSetup(project) is null)
                return ToolResult.Error(all.Count == 0
                    ? "The berth status isn't set up on this PC: open Berth status (next to the pre-submission checklist) and import the setup Excel."
                    : $"No berth status setup for '{project}'. Set up: {string.Join(", ", all)} (give project).");
        }
        var setup = LoadSetup(project)!;
        var state = LoadState(project);
        var now = DateTime.Now;

        Berth Pick()
        {
            var text = args.OptString("berth") ?? throw new ArgumentException("Give the berth.");
            return FindBerth(setup, text) ?? throw new ArgumentException($"No single berth matches '{text}'. Berths: {string.Join("; ", setup.Berths.Select(b => b.Name + " (" + b.Model + ")"))}.");
        }

        switch (args.String("action"))
        {
            case "list":
                return ToolResult.Ok(new
                {
                    project,
                    berths = setup.Berths.Select(b => Line(setup, Summarize(setup, b, state, now), detail: false)),
                    note = "Model results are from each berth's last Update status (readAt). Press Update status in the Berth status window to read the model again.",
                });
            case "details":
                return ToolResult.Ok(Line(setup, Summarize(setup, Pick(), state, now), detail: true));
            case "set":
            {
                var berth = Pick();
                var said = SetByHand(setup, state, berth, args.OptString("what") ?? "", args.OptDouble("percent"), args.OptString("comment"), args.OptBool("clear"));
                SaveState(project, state);
                var s = Summarize(setup, berth, state, now);
                return ToolResult.Ok(new { set = said, berth = berth.Name, status = Pct(s.Percent), verdict = Text(s.Verdict) });
            }
            case "finish":
            {
                var berth = Pick();
                var text = (args.OptString("finish") ?? "").Trim();
                var b = state.For(berth.Key);
                if (text.Equals("clear", StringComparison.OrdinalIgnoreCase)) b.FinishByHand = null;
                else
                {
                    var parts = text.Split([' ', 'T'], 2, StringSplitOptions.RemoveEmptyEntries);
                    var date = parts.Length > 0 ? ParseDate(parts[0]) : null;
                    if (date is null) throw new ArgumentException("Give finish as yyyy-MM-dd HH:mm (or 'clear').");
                    b.FinishByHand = date.Value + (parts.Length > 1 ? ParseTime(parts[1]) ?? TimeSpan.FromHours(17) : TimeSpan.FromHours(17));
                }
                SaveState(project, state);
                var s = Summarize(setup, berth, state, now);
                return ToolResult.Ok(new { berth = berth.Name, finish = s.Finish?.ToString("ddd dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture), byHand = s.FinishByHand, verdict = Text(s.Verdict) });
            }
            default:
                throw new ArgumentException("action is list, details, set or finish.");
        }
    }

    private static object Line(Setup setup, Summary s, bool detail) => new
    {
        berth = s.Berth.Name,
        model = s.Berth.Model,
        status = Pct(s.Percent),
        verdict = Text(s.Verdict),
        next = s.Next is null ? null : new { s.Next.Name, due = s.Due?.ToString("ddd dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture), workingHoursLeft = Math.Round(s.HoursLeft, 1) },
        submitted = s.Last is { } last ? last.Name + When(last, "dd-MMM-yyyy", ", ") : null,
        expectedFinish = s.Finish?.ToString("ddd dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture),
        finishByHand = s.FinishByHand,
        readAt = s.State.ReadAt?.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture),
        areas = s.Scores.Select(a => new
        {
            area = a.Area.Name, status = Pct(a.Percent), weight = a.Weight, from = a.From.ToString(), summary = a.Result?.Summary, comment = a.Hand?.Comment is { Length: > 0 } c ? c : null,
            toFix = detail ? a.Result?.Findings.Take(50).Select(f => $"{f.Item}: {f.Problem}" + (f.Id is { } id ? $" (id {id})" : "")) : null,
            toFixCount = a.Result?.Findings.Count ?? 0,
        }),
        deliverables = s.Deliverables.Select(d => new { d.Deliverable.Name, status = Pct(d.Percent), from = d.From.ToString(), file = d.File.Length > 0 ? d.File : null, comment = d.Comment.Length > 0 ? d.Comment : null }),
        comment = s.State.Comment.Length > 0 ? s.State.Comment : null,
    };
}
