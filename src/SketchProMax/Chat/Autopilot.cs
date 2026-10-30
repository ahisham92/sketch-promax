using System.Globalization;
using System.Text;

namespace SketchProMax.Chat;

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
    /// <summary>
    /// A newer Sketch ProMax waits to go in (its version): no new job starts, the running ones finish their step, then
    /// the update switches in and the run carries on (ChatPane.AutopilotUpdated). "" when none waits. Since when.
    /// </summary>
    public string Updating { get; set; } = "";
    public DateTime HeldSince { get; set; }
    /// <summary>How much of the weekly limit the run has used so far (0 to 1): the rises of the weekly figure while it ran.</summary>
    public double WeekUsed { get; set; }
    /// <summary>The run is paused because it used its share of the week (Settings): a new week starts it again from 0.</summary>
    public bool WeekCap { get; set; }
    public List<AutopilotEntry> Log { get; set; } = [];
    /// <summary>Review rounds so far: the review team checks the goal each time the coordinator says it is met (GoalReview).</summary>
    public int ReviewRound { get; set; }
    /// <summary>What the review team last found not done ("" when nothing): sent back to the coordinator, and in the report.</summary>
    public string Gaps { get; set; } = "";
    /// <summary>When the last progress update went (Settings › Chat › Autopilot; the run's start before the first).</summary>
    public DateTime LastUpdate { get; set; }
    /// <summary>Paused because the connection dropped (ConnectionWatch.Internet or .Network; "" otherwise), and since when.</summary>
    public string Offline { get; set; } = "";
    public DateTime OfflineSince { get; set; }

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

    /// <summary>True while an approved run works in the chat shown in this window's pane.</summary>
    public static volatile bool Here;

    /// <summary>
    /// True while popups and approvals are answered for the user: an approved run works in this window's chat, or in
    /// another Revit window's chat that reaches this one (AutopilotShare: a documentation model linking the working model).
    /// </summary>
    public static bool Unattended => Here || AutopilotShare.Remote(DateTime.UtcNow) is not null;

    /// <summary>This window's model, for the lines it logs for another window's run (set by the add-in).</summary>
    public static Func<string>? ModelTitle;

    /// <summary>Raised (any thread) when something was answered or skipped for the user: kind (AutopilotEntry), text.</summary>
    public static event Action<string, string>? Logged;

    public static void Log(string kind, string text)
    {
        try
        {
            if (Here) Logged?.Invoke(kind, text);
            // For another window's run: written for its report, saying which model it was in.
            else if (AutopilotShare.Remote(DateTime.UtcNow) is { } run)
                AutopilotShare.AppendLog(run, kind, (ModelTitle?.Invoke() is { Length: > 0 } model ? $"[{model}] " : "[other Revit window] ") + text, DateTime.UtcNow);
        }
        catch (Exception) { /* a log line must never break the call */ }
    }

    /// <summary>A run's id across the Revit windows: its chat and when it started.</summary>
    public static string RunId(string chatId, AutopilotRun run) => chatId + ":" + run.Started.Ticks.ToString(CultureInfo.InvariantCulture);

    // ---- What the coordinator is told ----------------------------------------------------------------------------------

    /// <summary>The heading that ends the plan: everything the coordinator needs from the user, asked once.</summary>
    public const string QuestionsHeading = "Questions before you leave";

    /// <summary>
    /// The user's goal, sent to the coordinator to plan (nothing changes until the plan is approved). It reads what the
    /// goal depends on first, with read-only jobs, so every question is asked now, in one list, while the user is there.
    /// </summary>
    public static string PlanRequest(string goal) =>
        "[Autopilot goal from the user]\n" + goal.Trim() + "\n\n" +
        "Plan it. The user will approve the plan once and then be away: after that nobody answers questions. So read first: " +
        "start read-only jobs side by side (chat_jobs start; they can't change anything before the plan is approved) to read " +
        "what the goal depends on: the attached files, the drawings and folders it names, CAD files and the models, so that " +
        "every question shows up now and not hours later. Keep what they find: the work after approval builds on it. When " +
        "they have all reported, answer with a short plan: the parts you will hand to workers (in order, side by side where " +
        "they can be), how you will check each part, and how you will know the goal is met. Work out the best way to reach " +
        "the goal, as a senior engineer would. End with \"" + QuestionsHeading + "\": everything you need from the user, in " +
        "one numbered list, each with the answer you will assume if they don't reply. Ask nothing later.";

    /// <summary>The reading jobs reported while the plan is made: plan once they all have.</summary>
    public static string PlanReports(int stillOpen) =>
        stillOpen > 0
            ? $"Autopilot planning: {stillOpen} reading job(s) are still working. Note what these found; end your turn without the plan, and plan when the rest report."
            : "Autopilot planning: all reading jobs have reported. Now answer with the plan, ending with \"" + QuestionsHeading + "\" (each question with the answer you will assume).";

    /// <summary>The run starts: the coordinator works on its own from here.</summary>
    public static string Approved(AutopilotRun run) =>
        "[Autopilot approved by the user, who is now away]\n" + Rules(run) + "\nStart now: hand out the first parts.";

    /// <summary>Jobs reported back: review them against the goal and carry on (replaces the usual "tell the user").</summary>
    public static string Review(AutopilotRun run) =>
        "Autopilot: review these reports against the goal. Check the model yourself where it matters (a quick read, not the " +
        "whole job again); where a teammate's check found issues, send the job back to fix them. A job paused at its budget: " +
        "let it carry on only if it was making progress, else give that work another way. Send a job back with action=message " +
        "when its work is wrong or missing something, start the next " +
        "parts, and keep the workers busy (start every part that doesn't wait on another). A job stopped for no progress: " +
        "give its work another way. Don't wait for the user's go-ahead: they are away. When the goal is met and no job is " +
        "open, write the final report (below) and end it with " + DoneMark + ". Goal: " + OneLine(run.Goal, 400);

    /// <summary>The coordinator ended a turn with nothing open and no final report.</summary>
    public static string Nudge(AutopilotRun run) =>
        "[Autopilot, not typed by the user] No job is open. Carry on toward the goal: start the next parts, or send a job " +
        "back to fix its work. If the goal is met, write the final report and end it with " + DoneMark + ". Don't ask " +
        "the user anything and don't wait for their go-ahead: they are away. Goal: " + OneLine(run.Goal, 400);

    /// <summary>The run carries on after a pause.</summary>
    public static string Resume(AutopilotRun run, string why) =>
        "[Autopilot resumed, not typed by the user] " + why + " Carry on toward the goal from where the work stands: " +
        "check which jobs finished, then start the next parts.\n" + Rules(run);

    private static string Rules(AutopilotRun run) =>
        "Goal: " + run.Goal.Trim() + "\n" +
        "Rules while the user is away:\n" +
        "- Work as a senior engineer who owns the result: choose the best way to reach the goal, not merely the safest. " +
        "Never ask the user anything and never wait for an answer; make the call a senior would, keep assumptions to the " +
        "few the goal needs, and list them under \"Decisions I made\" in the final report. Only a choice that would change the " +
        "result a lot and that the goal doesn't settle may go to the user's phone (ask_user, with the answer you assume): " +
        "carry on meanwhile; their reply, even a late one, reaches the job to put the work right.\n" +
        "- You may change and delete whatever the goal needs; a backup copy of the model was saved before you started.\n" +
        "- Hand the work to jobs, divided across the free workers; review each report like a senior reviewer and send back " +
        "what is wrong or not good enough.\n" +
        "- A step Revit refuses is undone; find another way, and skip it only when there is none (list it).\n" +
        "- Never stop to wait for the user's confirmation or go-ahead, even where an earlier message or a brief says to wait: " +
        "the run only stops when the goal is met, a limit pauses it or the user presses Stop. A choice you'd ask about: make it, " +
        "carry on, and list it under Decisions I made.\n" +
        "- Keep every worker busy: never hold parts back for a slow job. Start every part that doesn't depend on it; a big part " +
        "with several steps is split across the free workers (changes still go into Revit one at a time, by themselves).\n" +
        "- When you say the goal is met, a review team checks every item the user asked for in the models and files. What " +
        "they find not done comes back to you, so finish everything the user asked for, not only the easy parts.\n" +
        "- Final report, when the goal is met: Done (what was done, with counts), Checked (what you verified and how), " +
        "Unsure (what the user should look at), Skipped (what needs the user, and why), Decisions I made. Then " + DoneMark + ".";

    /// <summary>What a job is told when it starts during a run (added to its instructions).</summary>
    public const string JobRules = """


        - Autopilot: nobody is at the computer. Never end with a question and never wait for an answer. Work as a senior
          engineer: where you would ask, make the best call for the goal (not merely the safest), change or delete what
          the task needs, and say in your report what you decided and why. A backup of the model was saved first.
          Only a choice that would change the result a lot and that the task doesn't settle may go to the user's phone:
          ask_user with the question and the answer you assume, then carry on with work that doesn't depend on it (or
          ask_user action=wait); a reply that comes later reaches you as a message, to put the work right.
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

    // ---- Updates of Sketch ProMax ----------------------------------------------------------------------------------------

    /// <summary>How long new jobs wait for an update to go in before the run carries on without it (and tries again later).</summary>
    public static readonly TimeSpan UpdateHoldMax = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether a run waiting for an update (<see cref="AutopilotRun.Updating"/>) now runs <paramref name="current"/>, the
    /// version it waited for or newer: it carries on in it.
    /// </summary>
    public static bool UpdateIn(AutopilotRun run, Version current) =>
        run.Updating.Length > 0 && Version.TryParse(run.Updating, out var wanted)
        && new Version(current.Major, current.Minor, Math.Max(current.Build, 0)) >= new Version(wanted.Major, wanted.Minor, Math.Max(wanted.Build, 0));

    // ---- Revit stuck --------------------------------------------------------------------------------------------------

    /// <summary>Revit "Not responding" this long with no activity at all during a run: restarted on the same model.</summary>
    public static readonly TimeSpan RestartWhenStuck = TimeSpan.FromMinutes(10);

    /// <summary>Revit busy (the processor, memory or files moving) but not responding this long: restarted too.</summary>
    public static readonly TimeSpan RestartWhenBusy = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Whether an autopilot restarts a hung Revit by itself: Revit not responding for <see cref="RestartWhenStuck"/> while
    /// doing nothing at all, or for <see cref="RestartWhenBusy"/> while working; long work Revit is known to do
    /// (<paramref name="longWork"/>: synchronizing, opening, saving, loading links) gets the longer time either way.
    /// </summary>
    public static bool RestartAfterHang(bool unattended, bool notResponding, TimeSpan lasted, bool idle, bool longWork) =>
        unattended && notResponding && lasted >= (idle && !longWork ? RestartWhenStuck : RestartWhenBusy);

    /// <summary>What the coordinator is told when the run carries on after Revit was started again.</summary>
    public static string Recovered(AutopilotRun run, string why) =>
        "[Autopilot, not typed by the user] " + why + " The jobs that were working were told to carry on; a change that was " +
        "being applied may not be in the model. Check the model where it matters, then carry on toward the goal. Goal: " + OneLine(run.Goal, 400);

    // ---- Usage limits --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the run pauses now: a usage limit at <see cref="PauseAt"/> or over (or Claude Code refusing for a limit).
    /// Until: when that limit resets (the run then carries on), or null when it waits for Resume (a weekly limit with
    /// <paramref name="resumeWeekly"/> off, or no reset time reported).
    /// </summary>
    public static (bool Pause, DateTimeOffset? Until, string Why) ShouldPause(PlanUsage? usage, DateTimeOffset now, bool resumeWeekly)
    {
        var (pause, until, why, _) = ShouldPause(usage, now, resumeWeekly, PauseAt, 0, 0);
        return (pause, until, why);
    }

    /// <summary>
    /// The autopilot's limits (Settings > Chat > Autopilot): the 5-hour limit at <paramref name="fiveHourMax"/> (it carries
    /// on when it resets), any weekly limit near full (<see cref="PauseAt"/>), and the run's own share of the week:
    /// <paramref name="weekUsed"/> at <paramref name="weekShare"/> or over (0: no share of its own). A weekly pause carries
    /// on when the week resets only with <paramref name="resumeWeekly"/>; otherwise it waits for Resume (or "continue").
    /// WeekCap: the pause is the run's share of the week.
    /// </summary>
    public static (bool Pause, DateTimeOffset? Until, string Why, bool WeekCap) ShouldPause(PlanUsage? usage, DateTimeOffset now, bool resumeWeekly,
        double fiveHourMax, double weekShare, double weekUsed)
    {
        if (usage is null) return (false, null, "", false);
        var full = usage.Windows
            .Where(w => w.Key != PlanUsage.Extra && w.Key != PlanUsage.WeeklyIncluded && PlanUsage.Current(w, now) is { } used
                        && used >= (w.Key == PlanUsage.FiveHour ? fiveHourMax : PauseAt) - 1e-9)
            .ToList();
        if (full.Count == 0 && usage.Status == "rejected" && usage.Windows.FirstOrDefault(w => w.Key == usage.Binding) is { } binding && !PlanUsage.HasReset(binding, now))
            full.Add(binding);
        if (full.Count == 0)
        {
            if (weekShare <= 0 || weekUsed < weekShare - 1e-9) return (false, null, "", false);
            var week = usage.Windows.FirstOrDefault(w => w.Key == PlanUsage.Weekly);
            return (true, resumeWeekly ? week?.ResetsAt : null,
                $"This run used {Math.Round(weekUsed * 100):0}% of your weekly limit, its share ({Math.Round(weekShare * 100):0}%, Settings > Chat > Autopilot).", true);
        }
        var weekly = full.Any(w => w.Key != PlanUsage.FiveHour);
        var resets = full.Select(w => w.ResetsAt).ToList();
        DateTimeOffset? until = resets.All(r => r is not null) ? resets.Max() : null;
        if (weekly && !resumeWeekly) until = null;
        var names = string.Join(" and ", full.Select(w => PlanUsage.Label(w.Key).ToLowerInvariant()));
        var fiveOnly = !weekly && full.Count == 1 && Math.Abs(fiveHourMax - PauseAt) > 1e-9;
        return (true, until, fiveOnly ? $"The {names} reached {Math.Round(fiveHourMax * 100):0}%, the autopilot's limit (Settings > Chat > Autopilot)." : $"The {names} is nearly used up.", false);
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
            AutopilotRun.Done => run.ReviewRound > 0 ? "the goal is met, checked by the review team" : "the goal is met",
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
        if (run.State != AutopilotRun.Done && run.Gaps.Trim().Length > 0)
            sb.Append("\n\n**Still not done** (what the review team found)\n").Append(run.Gaps.Trim());
        Section("Questions sent to your phone", AutopilotEntry.Asked);
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

/// <summary>
/// The review team (Autopilot): when the coordinator says the goal is met, reviewers (read-only jobs) check every item the
/// user asked for in the models and files, not in the reports. All met: the run ends. Anything not done goes back to the
/// coordinator, who carries on, and the team checks again, up to <see cref="MaxRounds"/> times. Needs no WPF or Revit
/// (the unit tests compile this file).
/// </summary>
internal static class GoalReview
{
    /// <summary>Review rounds before the run stops with what is still not done listed.</summary>
    public const int MaxRounds = 5;

    /// <summary>The verdict lines that end a reviewer's report.</summary>
    public const string MetWord = "GOAL MET", NotMetWord = "GOAL NOT MET";

    /// <summary>The team: each reviewer takes one side of the goal, side by side.</summary>
    public static readonly (string Title, string Scope)[] Parts =
    [
        ("Review: modelling", "the modelling: elements, geometry, levels, voids and openings, parameters and marks, bills " +
                              "and quantities, links, clashes. Check the model in this window and any other model the goal names."),
        ("Review: drawings and files", "the drawings and deliverables: sheets and their grouping, views, tags, dimensions, " +
                                       "annotation, revisions and dates, schedules, and exported files (PDF sets, lists, " +
                                       "bills). The sheets may be in a documentation model open in another Revit window " +
                                       "(list_revit_models), so check there too."),
    ];

    /// <summary>A reviewer's task: the goal in full, the plan's checks, the coordinator's claims, and how to answer.</summary>
    public static string Task(AutopilotRun run, int part, string claims) =>
        $"You are on the review team of an autopilot run. The coordinator says the user's goal is met. Check it, for {Parts[part].Scope}\n\n" +
        "The user's goal, with their answers to the plan's questions:\n" + run.Goal.Trim() + "\n\n" +
        (run.Plan.Trim().Length > 0 ? "The plan the user approved (its checks say how each part is verified):\n" + Clip(run.Plan, 6000) + "\n\n" : "") +
        (run.Gaps.Trim().Length > 0 ? "Last round, the team found these not done; check them first:\n" + run.Gaps.Trim() + "\n\n" : "") +
        "The coordinator's final report (claims to check, not proof):\n" + (claims.Trim().Length > 0 ? Clip(claims, 6000) : "(none)") + "\n\n" +
        "How to review:\n" +
        "1. List every thing the user asked for on your side, one line each, from the goal and their answers (not from the plan or the report).\n" +
        "2. Check each one in the models and files with Sketch ProMax's tools: count it, read it, look at it. A report saying it is done is not proof.\n" +
        "3. An item is NOT DONE when it is missing, partly done, done differently from what the user said, or left for later, " +
        "\"to confirm\" or \"flagged\" when the user didn't allow that. An assumption is fine only where the goal left the choice open.\n" +
        "4. Don't change anything (your changes are refused): you only check.\n\n" +
        $"End with one line: \"{MetWord}\" when every item on your side is done, or \"{NotMetWord}:\" followed by the items not done, " +
        "one per line starting with \"- \", each saying what is missing (with counts) and what to do.";

    /// <summary>A reviewer's verdict: met, and else what is not done (its own words after the NOT MET line, or why it couldn't tell).</summary>
    public static (bool Met, string Gaps) Verdict(ChatJob reviewer)
    {
        var text = reviewer.Summary.Trim();
        if (reviewer.State != JobBoard.Done)
            return (false, $"- {reviewer.Title}: the review couldn't finish" + (text.Length > 0 ? " (" + JobBoard.OneLine(text, 300) + ")" : "") + ". Check this side again.");
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        int Find(string word) => lines.FindLastIndex(l => l.Trim('*', '_', '`', ' ').StartsWith(word, StringComparison.OrdinalIgnoreCase));
        var notMet = Find(NotMetWord);
        if (notMet >= 0)
        {
            var first = lines[notMet].Trim('*', '_', '`', ' ').Substring(NotMetWord.Length).TrimStart(':', ' ', '-');
            var items = lines.Skip(notMet + 1).Where(l => l.Length > 0).ToList();
            if (first.Length > 0) items.Insert(0, "- " + first);
            return (false, items.Count > 0 ? string.Join("\n", items) : $"- {reviewer.Title}: not met, with no list given. Check this side again.");
        }
        if (Find(MetWord) >= 0) return (true, "");
        return (false, $"- {reviewer.Title}: gave no verdict. Its report: " + JobBoard.OneLine(text, 600));
    }

    /// <summary>The team's verdict: met only when every reviewer says so; else everything not done, by reviewer.</summary>
    public static (bool Met, string Gaps) Combine(IEnumerable<ChatJob> reviewers)
    {
        var all = reviewers.OrderBy(j => j.Number).Select(j => (j.Title, Verdict: Verdict(j))).ToList();
        if (all.Count > 0 && all.All(v => v.Verdict.Met)) return (true, "");
        return (false, string.Join("\n\n", all.Where(v => !v.Verdict.Met).Select(v => $"{v.Title.Replace("Review: ", "")}:\n{v.Verdict.Gaps}")));
    }

    /// <summary>What the coordinator is told when the team found work not done: carry on, without stopping or asking.</summary>
    public static string SendBack(AutopilotRun run, string gaps) =>
        "[Autopilot, not typed by the user] The review team checked the goal in the models and files: it is NOT met yet. " +
        $"Still to do (round {run.ReviewRound} of {MaxRounds}):\n{gaps.Trim()}\n\n" +
        "Hand all of this out to the workers now, side by side where it can be, and check it as it comes back. Don't stop, don't " +
        "wait for the user's go-ahead and don't ask them anything: they are away. When it is all done, write the final report " +
        "again and end it with " + Autopilot.DoneMark + "; the team checks again. Goal: " + JobBoard.OneLine(run.Goal, 400);

    /// <summary>Whether the coordinator can't move it on: a turn that ends with nothing open after a send-back still gets checked.</summary>
    public static bool MoreRounds(AutopilotRun run) => run.ReviewRound < MaxRounds;

    private static string Clip(string text, int max) => text.Length <= max ? text.Trim() : text.Substring(0, max).TrimEnd() + "…";
}
