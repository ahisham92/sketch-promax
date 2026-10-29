using System.Globalization;
using System.Text;

namespace SketchProMax.Chat;

/// <summary>One line of an autopilot run's log: a popup answered, a step skipped, a pause, a decision.</summary>
internal sealed class AutopilotEntry
{
    public const string Popup = "popup", Allowed = "allowed", Paused = "paused", Resumed = "resumed", Note = "note";

    public DateTime Time { get; set; }
    public string Kind { get; set; } = Note;
    public string Text { get; set; } = "";
}

/// <summary>
/// An autopilot chat's run, saved with the chat (Conversation.Autopilot): the goal, the plan the user approved, where it
/// is (planning, waiting for approval, running, paused at a usage limit, done or stopped) and its log.
/// </summary>
internal sealed class AutopilotRun
{
    public const string Planning = "planning", Approval = "approval", Running = "running", Paused = "paused", Done = "done", Stopped = "stopped";

    public string Goal { get; set; } = "";
    public string Plan { get; set; } = "";
    public string State { get; set; } = Planning;
    public DateTime Started { get; set; }
    public DateTime Ended { get; set; }
    /// <summary>While paused at a usage limit: when the limit resets and the run carries on (null: it waits for Resume).</summary>
    public DateTimeOffset? PausedUntil { get; set; }
    public string PauseWhy { get; set; } = "";
    /// <summary>Jobs numbered above this were started by the run (the chat's jobs before it are not its own).</summary>
    public int JobsBefore { get; set; }
    /// <summary>Turns in a row that ended with no job open and no final report: the run stops after a few.</summary>
    public int Idle { get; set; }
    public string Backup { get; set; } = "";
    public List<AutopilotEntry> Log { get; set; } = [];

    public bool Active => State is Running or Paused;
}

/// <summary>
/// Autopilot: the user gives a goal and approves the coordinator's plan once; from then on the coordinator hands out
/// jobs, reviews their reports, sends fixes and next parts, and writes a report when the goal is met, without asking
/// anyone. Nothing waits on a click: Revit's warnings are dismissed, errors roll the step back, dialogs get their safe
/// button, risky approvals are refused and listed. It pauses near a usage limit and carries on when it resets. The
/// rules here are plain functions, tested; the pane (ChatPane.Autopilot) runs them.
/// </summary>
internal static class Autopilot
{
    /// <summary>What the coordinator's final report ends with once the goal is met.</summary>
    public const string DoneMark = "[AUTOPILOT DONE]";

    /// <summary>The share of a usage limit at which the run pauses (90 %).</summary>
    public const double PauseAt = 0.9;

    /// <summary>Turns in a row with nothing open and no final report before the run stops (it isn't getting anywhere).</summary>
    public const int MaxIdle = 3;

    /// <summary>True while an approved run works in the chat shown in the pane: popups and approvals are answered for the user.</summary>
    public static volatile bool Unattended;

    /// <summary>Raised (any thread) when something was answered or skipped for the user: kind (AutopilotEntry), text.</summary>
    public static event Action<string, string>? Logged;

    public static void Log(string kind, string text)
    {
        if (!Unattended) return;
        try { Logged?.Invoke(kind, text); } catch (Exception) { /* a log line must never break the call */ }
    }

    // ---- What the coordinator is told ----------------------------------------------------------------------------------

    /// <summary>The user's goal, sent to the coordinator to plan (nothing starts until the plan is approved).</summary>
    public static string PlanRequest(string goal) =>
        "[Autopilot goal from the user]\n" + goal.Trim() + "\n\n" +
        "Plan it, and don't start any job yet. The user will approve the plan once and then be away: after that nobody " +
        "answers questions. Read the model only as much as planning needs. Answer with a short plan: the parts you will " +
        "hand to workers (in order, side by side where they can be), how you will check each part, and how you will know " +
        "the goal is met. Work out the best way to reach the goal, as a senior engineer would; where something is unclear, " +
        "say which choice you will make and why, keeping assumptions to the few the goal really needs.";

    /// <summary>The run starts: the coordinator works on its own from here.</summary>
    public static string Approved(AutopilotRun run) =>
        "[Autopilot approved by the user, who is now away]\n" + Rules(run) + "\nStart now: hand out the first parts.";

    /// <summary>Jobs reported back: review them against the goal and carry on (replaces the usual "tell the user").</summary>
    public static string Review(AutopilotRun run) =>
        "Autopilot: review these reports against the goal. Check the model yourself where it matters (a quick read, not the " +
        "whole job again). Send a job back with action=message when its work is wrong or missing something, start the next " +
        "parts, and keep the workers busy. When the goal is met and no job is open, write the final report (below) and end " +
        "it with " + DoneMark + ". Goal: " + OneLine(run.Goal, 400);

    /// <summary>The coordinator ended a turn with nothing open and no final report.</summary>
    public static string Nudge(AutopilotRun run) =>
        "[Autopilot, not typed by the user] No job is open. Carry on toward the goal: start the next parts, or send a job " +
        "back to fix its work. If the goal is met, write the final report and end it with " + DoneMark + ". Don't ask " +
        "the user anything: they are away. Goal: " + OneLine(run.Goal, 400);

    /// <summary>The run carries on after a pause.</summary>
    public static string Resume(AutopilotRun run, string why) =>
        "[Autopilot resumed, not typed by the user] " + why + " Carry on toward the goal from where the work stands: " +
        "check which jobs finished, then start the next parts.\n" + Rules(run);

    private static string Rules(AutopilotRun run) =>
        "Goal: " + run.Goal.Trim() + "\n" +
        "Rules while the user is away:\n" +
        "- Work as a senior engineer who owns the result: choose the best way to reach the goal, not merely the safest. " +
        "Never ask the user anything and never wait for an answer; make the call a senior would, keep assumptions to the " +
        "few the goal needs, and list them under \"Decisions I made\" in the final report.\n" +
        "- You may change and delete whatever the goal needs; a backup copy of the model was saved before you started.\n" +
        "- Hand the work to jobs, divided across the free workers; review each report like a senior reviewer and send back " +
        "what is wrong or not good enough.\n" +
        "- A step Revit refuses is undone; find another way, and skip it only when there is none (list it).\n" +
        "- Final report, when the goal is met: Done (what was done, with counts), Checked (what you verified and how), " +
        "Unsure (what the user should look at), Skipped (what needs the user, and why), Decisions I made. Then " + DoneMark + ".";

    /// <summary>What a job is told when it starts during a run (added to its instructions).</summary>
    public const string JobRules = """


        - Autopilot: nobody is at the computer. Never end with a question and never wait for an answer. Work as a senior
          engineer: where you would ask, make the best call for the goal (not merely the safest), change or delete what
          the task needs, and say in your report what you decided and why. A backup of the model was saved first.
        """;

    // ---- Answering for the user ----------------------------------------------------------------------------------------

    /// <summary>
    /// An approval Sketch ProMax would ask the user, given while they are away: the user approved the goal, and a backup
    /// copy of the model was saved first, so every change is allowed (deletes too) and logged.
    /// </summary>
    public static (bool Allow, string Why) Allows(string reason) =>
        (true, reason.Trim().Length > 0 ? "allowed while you were away: " + Trim(reason) : "allowed while you were away");

    /// <summary>
    /// The answer to a Revit dialog nobody is there to click: Cancel where there is one (the step is undone and the
    /// worker finds another way), else No, else OK
    /// (Win32 IDOK 1, IDCANCEL 2, IDABORT 3, IDNO 7). <paramref name="messageBoxType"/>: a message box's MB_ type, or -1 for
    /// a task dialog or another Revit dialog (both cancelled).
    /// </summary>
    public static int SafeResult(int messageBoxType) => (messageBoxType & 0xF) switch
    {
        _ when messageBoxType < 0 => 2,
        0 => 1, // OK only
        2 => 3, // Abort, Retry, Ignore: abort
        4 => 7, // Yes, No: no
        _ => 2, // OK/Cancel, Yes/No/Cancel, Retry/Cancel: cancel
    };

    /// <summary>What the safe answer reads as, for the log.</summary>
    public static string ResultName(int result) => result switch { 1 => "OK", 3 => "Abort", 7 => "No", _ => "Cancel" };

    // ---- Usage limits --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the run pauses now: a usage limit at <see cref="PauseAt"/> or over (or Claude Code refusing for a limit).
    /// Until: when that limit resets (the run then carries on), or null when it waits for Resume (a weekly limit with
    /// <paramref name="resumeWeekly"/> off, or no reset time reported).
    /// </summary>
    public static (bool Pause, DateTimeOffset? Until, string Why) ShouldPause(PlanUsage? usage, DateTimeOffset now, bool resumeWeekly)
    {
        if (usage is null) return (false, null, "");
        var full = usage.Windows
            .Where(w => w.Key != PlanUsage.Extra && w.Key != PlanUsage.WeeklyIncluded && PlanUsage.Current(w, now) is { } used && used >= PauseAt)
            .ToList();
        if (full.Count == 0 && usage.Status == "rejected" && usage.Windows.FirstOrDefault(w => w.Key == usage.Binding) is { } binding && !PlanUsage.HasReset(binding, now))
            full.Add(binding);
        if (full.Count == 0) return (false, null, "");
        var weekly = full.Any(w => w.Key != PlanUsage.FiveHour);
        var resets = full.Select(w => w.ResetsAt).ToList();
        DateTimeOffset? until = resets.All(r => r is not null) ? resets.Max() : null;
        if (weekly && !resumeWeekly) until = null;
        var names = string.Join(" and ", full.Select(w => PlanUsage.Label(w.Key).ToLowerInvariant()));
        return (true, until, $"The {names} is nearly used up.");
    }

    // ---- The report ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The report the user finds when back: how the run ended, how long it took, its jobs, then every popup answered and
    /// step skipped for them. The coordinator's own final report (done / checked / unsure / skipped / decisions) is the
    /// chat's last answer, just above.
    /// </summary>
    public static string Report(AutopilotRun run, IEnumerable<ChatJob> jobs, DateTime now)
    {
        var own = jobs.Where(j => j.Number > run.JobsBefore).OrderBy(j => j.Number).ToList();
        var sb = new StringBuilder();
        sb.Append("**Autopilot report** · ").Append(run.State switch
        {
            AutopilotRun.Done => "the goal is met",
            AutopilotRun.Paused => "paused: " + run.PauseWhy,
            _ => "stopped" + (run.PauseWhy.Length > 0 ? ": " + run.PauseWhy : ""),
        });
        var end = run.Ended > run.Started ? run.Ended : now;
        sb.Append("\n\nWorked ").Append(Duration(end - run.Started));
        sb.Append(", from ").Append(run.Started.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))
          .Append(" to ").Append(end.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)).Append('.');
        if (run.Backup.Length > 0) sb.Append(" Backup of the model before it started: ").Append(run.Backup).Append('.');
        sb.Append("\n\n**Jobs** (").Append(own.Count).Append(')');
        foreach (var j in own)
            sb.Append("\n- Job ").Append(j.Number).Append(" \"").Append(j.Title).Append("\": ").Append(j.State switch
            {
                JobBoard.Done => "done",
                JobBoard.Failed => "ended with a problem",
                JobBoard.Stopped => "stopped",
                _ => "not finished",
            });
        void Section(string title, string kind)
        {
            var lines = run.Log.Where(e => e.Kind == kind).ToList();
            if (lines.Count == 0) return;
            sb.Append("\n\n**").Append(title).Append("** (").Append(lines.Count).Append(')');
            foreach (var e in lines.Take(40))
                sb.Append("\n- ").Append(e.Time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)).Append(' ').Append(e.Text);
            if (lines.Count > 40) sb.Append("\n- …and ").Append(lines.Count - 40).Append(" more");
        }
        Section("Risky steps allowed for you", AutopilotEntry.Allowed);
        Section("Popups answered for you", AutopilotEntry.Popup);
        Section("Pauses", AutopilotEntry.Paused);
        sb.Append("\n\nEvery change is in Lifeline, to replay or undo.");
        return sb.ToString();
    }

    /// <summary>"2 h 5 min", "45 min".</summary>
    public static string Duration(TimeSpan span)
    {
        var minutes = Math.Max(0, (int)Math.Round(span.TotalMinutes));
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }

    /// <summary>The banner's line: "Autopilot · working since 23:10 · 4 jobs done", "Paused until 02:40", …</summary>
    public static string Banner(AutopilotRun run, int jobsDone, DateTimeOffset now) => run.State switch
    {
        AutopilotRun.Planning => "Autopilot · type the goal; you approve the plan once, then it works on its own",
        AutopilotRun.Approval => "Autopilot · approve the plan below to start",
        AutopilotRun.Running => $"Autopilot · working since {run.Started.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} · {jobsDone} job(s) done",
        AutopilotRun.Paused => run.PausedUntil is { } until
            ? $"Autopilot · paused until {until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} ({run.PauseWhy.TrimEnd('.')}), then carries on"
            : $"Autopilot · paused: {run.PauseWhy}",
        AutopilotRun.Done => "Autopilot · the goal is met; the report is below",
        _ => "Autopilot · stopped; the report is below",
    };

    private static string Trim(string text) => OneLine(text, 160).TrimEnd('.');

    private static string OneLine(string text, int max) => JobBoard.OneLine(text, max);
}
