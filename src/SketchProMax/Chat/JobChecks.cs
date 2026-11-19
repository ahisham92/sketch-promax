using System.Globalization;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// Each job's budget (Settings > Chat): a job that uses more than its share of the plan's 5-hour limit pauses itself
/// with a warning, so a job going round in circles can't use up the limit. A job's share is the rise of the 5-hour
/// figure reported with its own requests (<see cref="Rise"/>). Needs no WPF or Revit (the unit tests compile this file).
/// </summary>
internal static class JobBudget
{
    /// <summary>The share of the 5-hour limit one job may use before it pauses (15 %), and the choices in Settings (0: off).</summary>
    public const int DefaultPercent = 15;
    public static readonly int[] Choices = [0, 5, 10, 15, 20, 25, 30, 40, 50];

    /// <summary>The Settings' percent as a share (0 to 1); 0 when off or out of range.</summary>
    public static double Share(int percent) => percent is > 0 and <= 100 ? percent / 100.0 : 0;

    /// <summary>True once the job used <paramref name="budget"/> (a share; 0 is off) since it was last let go on.</summary>
    public static bool Over(ChatJob job, double budget) => budget > 0 && job.Spent - job.BudgetFrom >= budget - 1e-9;

    /// <summary>
    /// How much the 5-hour figure rose with a new report (<paramref name="used"/>, 0 to 1) over the highest one seen from
    /// any chat or job (<paramref name="last"/>, updated). Parallel jobs report the same figure a moment apart, so a lower
    /// figure is an older reading and counts nothing; only <paramref name="reset"/> (the window's reset time moved on)
    /// starts it again, and then what it shows was used since. The first report, or one without a figure, counts nothing.
    /// </summary>
    public static double Rise(ref double? last, double? used, bool reset = false)
    {
        if (used is not { } now) return 0;
        var before = last;
        if (before is not { } was)
        {
            last = now;
            return 0;
        }
        if (reset)
        {
            last = now;
            return now;
        }
        if (now <= was) return 0;
        last = now;
        return now - was;
    }

    /// <summary>
    /// The rise counted to the job whose answer reported it: the whole plan's figure rose with every chat and job working
    /// since the last report, so it is shared out between them (<paramref name="working"/>, at least 1).
    /// </summary>
    public static double ShareOf(double rise, int working) => rise / Math.Max(1, working);

    /// <summary>True when the window's reset time moved on since the last report (a new 5-hour window began).</summary>
    public static bool NewWindow(ref DateTimeOffset? seen, DateTimeOffset? resets)
    {
        if (resets is not { } now) return false;
        var before = seen;
        seen = now;
        return before is { } was && now - was > TimeSpan.FromMinutes(5);
    }

    public static string Percent(double share) => Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The job's line when it pauses itself.</summary>
    public static string Paused(ChatJob job, double budget) =>
        $"Paused: this job used {Percent(job.Spent - job.BudgetFrom)} of your 5-hour limit, its budget ({Percent(budget)}, Settings > Chat), " +
        "so it stopped before using more. Send it a message to let it carry on with another budget.";

    /// <summary>What the coordinator is told about a job paused at its budget.</summary>
    public static string ReportLine(ChatJob job) =>
        $"It used {Percent(job.Spent - job.BudgetFrom)} of the 5-hour limit, its budget, so it was paused before finishing. Look at what it did " +
        "(chat_jobs list, or its card): send it a message to carry on only if it was making real progress; if it went round in circles, " +
        "give the work another way (smaller parts, clearer steps) instead.";
}

/// <summary>
/// The check before a job is done (Settings > Chat): when a job finishes, a teammate (a new job, on another worker's desk)
/// reads the model to check the work against the task, without changing anything, and its verdict goes with the job's
/// report to the coordinator. Needs no WPF or Revit (the unit tests compile this file).
/// </summary>
internal static class JobReview
{
    /// <summary>The Settings' choices: check every finished job, only in an autopilot (nobody watching), or never.</summary>
    public const string Always = "always", AutopilotOnly = "autopilot", Off = "off";

    /// <summary>A job's <see cref="ChatJob.Review"/> while its check runs.</summary>
    public const string Pending = "pending";

    /// <summary>
    /// Checks of one job at most. A job whose check found parts not done is sent back to finish exactly those by itself
    /// and is checked again (<see cref="SendBack"/>); only after the last check does unfinished work reach the user, named
    /// as not done (the owner, 2026-11-17: "when checks are done I see things not done at the end").
    /// </summary>
    public const int MaxReviews = 3;

    /// <summary>The verdict's first word when the work is right.</summary>
    public const string PassedWord = "PASSED", IssuesWord = "ISSUES";

    public static string Normal(string? mode) => mode is Always or AutopilotOnly or Off ? mode : Always;

    /// <summary>
    /// True when job <paramref name="job"/>, just finished, gets a check before it is reported: a job that read is checked
    /// too (the user wants every job's work confirmed), but only once per piece of work (<see cref="ChatJob.Unchecked"/>: a
    /// job reported after its check is not checked again), and at most <see cref="MaxReviews"/> times.
    /// </summary>
    public static bool Wanted(string? mode, bool autopilot, ChatJob job) =>
        Normal(mode) switch { Off => false, AutopilotOnly => autopilot, _ => true }
        && job.State == JobBoard.Done && job.ReviewOf == 0 && job.GoalCheck == 0 && job.Unchecked
        && job.Review != Pending && job.Reviews < MaxReviews;

    /// <summary>The words that mark a part in a check's list.</summary>
    public const string DoneWord = "DONE", NotDoneWord = "NOT DONE";

    /// <summary>
    /// The checking job's task: the job's task and report, what the user asked in their own words (<paramref name="asked"/>,
    /// so a part the task left out is still caught), and how to answer: every part listed and read back, one by one.
    /// </summary>
    public static string Task(ChatJob done, string asked = "", string rules = "") =>
        $"Check the work of job {done.Number} (\"{done.Title}\"), which says it is finished, against what it was asked to do.\n\n" +
        $"Its task:\n{done.Task.Trim()}\n\nIts report:\n{(done.Summary.Trim().Length > 0 ? done.Summary.Trim() : "(it gave none)")}\n\n" +
        (asked.Trim().Length > 0
            ? "What the user asked, in their own words (the task above was written from this; a part of it that belongs to this job and is " +
              $"missing from the task still counts):\n{asked.Trim()}\n\n"
            : "") +
        (rules.Trim().Length > 0
            ? $"The rules, methods and decisions agreed earlier in the chat (they hold for this job too):\n{Cut(rules.Trim(), 3000)}\n\n"
            : "") +
        "How to check, part by part:\n" +
        "1. Write the task as a numbered list of its parts: every thing it had to make, change, place, name or leave alone. Leave nothing " +
        "out because it is small, and don't take the report's word for any of it.\n" +
        "2. For each part, read the model with Sketch ProMax's tools and see that it is really there: the elements, names, values, views " +
        "and sheets. Where the task says all or gives a number (all piles, every sheet, berths 7 to 11), count what was done against what " +
        "was asked. For anything drawn (geometry, lines, tags, dimensions, text), also look at it in its view (capture_view_image): " +
        "overlapping tags, a missing dimension or a hidden element show there and not in the numbers.\n" +
        "3. Check how it was done, not only the numbers: where the user agreed a method or pointed at a reference (\"the same as the " +
        "reference strip\", \"neater like the decks\", generic model voids), the work must use that method, and it must look like that " +
        "reference in the view: extra lines, a mesh of creases, steps or gaps that the reference doesn't show make the part NOT DONE " +
        "even when every level matches. A method the task allows \"or another way\" doesn't excuse a result that looks worse than the " +
        "reference.\n" +
        "4. When the work was a berth's submission drawings or sheets, also call submission_format (action=check, berths): every line " +
        "it lists for those berths is a part that is NOT DONE.\n" +
        "5. Don't change the model: changes are refused for a check.\n\n" +
        $"Answer with the list, one line per part: \"<n>. <the part>: {DoneWord}\" with what you read, or \"<n>. <the part>: {NotDoneWord} - " +
        "<what is missing or wrong, and where>\". A part you could not check is NOT DONE with the reason.\n" +
        $"End with one line: \"{PassedWord}\" only when every part is {DoneWord}, or \"{IssuesWord}: \" followed by the {NotDoneWord} parts, " +
        "each with what to fix.";

    /// <summary>
    /// True when a job whose check found parts not done goes back to finish them by itself, without the user or the main
    /// chat in between: it finished, a check gave a verdict that isn't a pass, and it has checks left.
    /// </summary>
    public static bool SendBack(ChatJob job) =>
        job.State == JobBoard.Done && job.Review.Length > 0 && job.Review != Pending && !Passed(job.Review)
        && job.Review.StartsWith(IssuesWord, StringComparison.OrdinalIgnoreCase) && job.Reviews < MaxReviews;

    /// <summary>What a job sent back is told: exactly the parts the check found not done.</summary>
    public static string FixMessage(ChatJob job) =>
        $"A teammate checked your work in the model, part by part, and it is not finished. {job.Review}\n\n" +
        "Finish exactly these parts now; leave what was found done as it is. After each one, read it back in the model (and look at " +
        "drawn work in its view) before you go on. A part that truly can't be done: say why in one line, don't call it done. " +
        "Then report each of these parts as done or not done. Your work is checked again.";

    /// <summary>The checking job's verdict, from its report: its last line when that says PASSED or ISSUES, else the whole report.</summary>
    public static string Verdict(ChatJob check)
    {
        if (check.State != JobBoard.Done) return "The check couldn't finish" + (check.Summary.Length > 0 ? ": " + JobBoard.OneLine(check.Summary, 300) : ".");
        var text = check.Summary.Trim();
        var lines = text.Split('\n').Select(l => l.Trim().Trim('*', '_', '`').Trim()).Where(l => l.Length > 0).ToList();
        var marked = lines.LastOrDefault(l => l.StartsWith(PassedWord, StringComparison.OrdinalIgnoreCase) || l.StartsWith(IssuesWord, StringComparison.OrdinalIgnoreCase));
        if (marked is null) return text.Length > 0 ? JobBoard.OneLine(text, 1500) : "The check gave no verdict.";
        if (marked.StartsWith(PassedWord, StringComparison.OrdinalIgnoreCase)) return PassedWord;
        // The issues: the marked line and anything listed after it.
        var at = lines.LastIndexOf(marked);
        return JobBoard.OneLine(string.Join(" ", lines.Skip(at)), 1500);
    }

    private static string Cut(string text, int max) => text.Length > max ? text.Substring(0, max) + "…" : text;

    public static bool Passed(string verdict) => string.Equals(verdict, PassedWord, StringComparison.Ordinal);

    /// <summary>What the coordinator is told about the check of a job's work; "" when it wasn't checked.</summary>
    public static string ReportLine(ChatJob job) =>
        job.Review.Length == 0 || job.Review == Pending ? ""
        : Passed(job.Review) ? $"A teammate (job {job.ReviewedBy}) checked the work in the model: it passed."
        : $"A teammate (job {job.ReviewedBy}) checked the work in the model, part by part" + (job.Reviews > 1 ? $" ({job.Reviews} checks, the job was sent back to finish in between)" : "") +
          $", and this is still not done: {job.Review} Tell the user these parts first, exactly as not done; don't say the work is finished. " +
          $"Then send job {job.Number} a message to finish them another way, or ask the user the one thing that blocks them.";
}

/// <summary>
/// Jobs that stop moving (JobWatch): one that wrote its report but whose run never ended is finished with that report,
/// and one with no step for <see cref="Stuck"/> is stopped and handed back to the coordinator, so the rest of the work
/// never waits on it (the 29-30 Oct overnight run lost 12 hours to two of each). Needs no WPF or Revit (the unit tests
/// compile this file).
/// </summary>
internal static class JobWatch
{
    /// <summary>A report written, then nothing for this long: the run is taken as finished.</summary>
    public static readonly TimeSpan Hung = TimeSpan.FromMinutes(5);

    /// <summary>No step and no words for this long: the job is stopped.</summary>
    public static readonly TimeSpan Stuck = TimeSpan.FromMinutes(20);

    /// <summary>The shortest answer that reads as a report, not a line between steps.</summary>
    public const int ReportLength = 400;

    public const string Fine = "", Finished = "finished", Stopped = "stuck";

    /// <summary>When the job last did anything: its latest step or words, else when it started.</summary>
    public static DateTime LastMove(ChatJob job)
    {
        var last = job.Items.Count > 0 ? job.Items.Max(i => i.Time) : default;
        return last > job.Started ? last : job.Started;
    }

    /// <summary>
    /// What to do with a running job now: <see cref="Finished"/> (its last item is a long answer, no step runs, and
    /// nothing came for <see cref="Hung"/>), <see cref="Stopped"/> (nothing for <see cref="Stuck"/>), else <see cref="Fine"/>.
    /// A job waiting its turn to change the model, or while the model synchronizes, is never judged: it isn't its doing.
    /// </summary>
    public static string Judge(ChatJob job, DateTime now)
    {
        if (job.State != JobBoard.Running || job.Activity is JobBoard.Syncing or JobBoard.Applying or JobBoard.Reading) return Fine;
        var quiet = now - LastMove(job);
        var last = job.Items.LastOrDefault(i => i.Kind != "note");
        if (last is { Kind: "assistant" } && last.Text.Trim().Length >= ReportLength && quiet >= Hung) return Finished;
        return quiet >= Stuck ? Stopped : Fine;
    }

    /// <summary>The note on a job finished here.</summary>
    public const string FinishedNote = "Its report was written but its run didn't end, so it was finished here with that report.";

    /// <summary>What a stuck job reports to the coordinator.</summary>
    public static string StuckReport(ChatJob job)
    {
        var said = job.Items.Where(i => i.Kind == "assistant" && i.Text.Trim().Length > 0).Select(i => i.Text.Trim()).ToList();
        return $"It showed no progress for {Stuck.TotalMinutes:0} minutes, so it was stopped." +
               (said.Count > 0 ? " What it had found so far: " + JobBoard.OneLine(string.Join(" ", said.Skip(Math.Max(0, said.Count - 3))), 3000) : " It hadn't reported anything.") +
               " Give the rest of its work another way (smaller parts), and don't hold other parts back for it.";
    }
}

/// <summary>
/// No worker waits while there is work (FreeWorkers): when a worker is free and a running job still has several steps
/// it hasn't started, the coordinator is told to hand some of them to the free worker. Only changes go into Revit one
/// at a time; reading and thinking run side by side. Needs no WPF or Revit (the unit tests compile this file).
/// </summary>
internal static class FreeWorkers
{
    /// <summary>Steps not started yet that make a job worth sharing.</summary>
    public const int MinSteps = 2;

    /// <summary>Workers free now: the Settings' number less the jobs running or waiting to start.</summary>
    public static int Free(IEnumerable<ChatJob> jobs, int limit) => Math.Max(0, limit - jobs.Count(j => j.Open));

    /// <summary>The job to share with a free worker (the most steps not started), or null; each job is offered once (<paramref name="offered"/>).</summary>
    public static ChatJob? ToShare(IReadOnlyList<ChatJob> jobs, int limit, ICollection<int> offered)
    {
        if (Free(jobs, limit) < 1) return null;
        return jobs.Where(j => j.Active && j.GoalCheck == 0 && j.ReviewOf == 0 && !offered.Contains(j.Number))
            .Select(j => (Job: j, Left: j.Plan.Values.Count(v => v == JobPlan.ToDo)))
            .Where(x => x.Left >= MinSteps).OrderByDescending(x => x.Left).Select(x => x.Job).FirstOrDefault();
    }

    /// <summary>What the coordinator is told (a turn the user didn't type).</summary>
    public static string Nudge(ChatJob job, int free) =>
        $"[Sketch ProMax, not typed by the user] {free} worker(s) are free while job {job.Number} (\"{job.Title}\") still has " +
        $"{job.Plan.Values.Count(v => v == JobPlan.ToDo)} steps it hasn't started. Nobody should wait while there is work: hand the " +
        "steps that don't depend on its current one to new jobs now (chat_jobs start, with the facts it found), and tell job " +
        $"{job.Number} (chat_jobs message) which steps moved so it leaves them. Changes still go into Revit one at a time by " +
        "themselves. If nothing can be split off, answer only \"No split.\" and carry on: the user doesn't need the reason " +
        "(this answer is never shown to them).";

    /// <summary>
    /// The coordinator's answer to <see cref="Nudge"/> when nothing could be split off ("No split.", perhaps with a short
    /// reason): an internal decision, so the chat drops it instead of showing it as Sketch ProMax's answer.
    /// </summary>
    public static bool IsNothing(string text) => Strip(text).Length == 0;

    /// <summary>
    /// The text without the coordinator's "No split." (a line of its own, or the end of the last line), wherever it
    /// appears: a reply that did real work can still end with it (bug 94). Text that is only "No split." becomes "".
    /// </summary>
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("split", StringComparison.OrdinalIgnoreCase) < 0) return (text ?? "").Trim().Length == 0 ? "" : text ?? "";
        var kept = OwnLine.Replace(text, "");
        kept = AtEnd.Replace(kept, "");
        return kept.Trim().Length == 0 ? "" : kept.TrimEnd();
    }

    private static readonly Regex OwnLine = new(@"^[ \t>*_`]*no split(?:\b[^\n]{0,200})?$\r?\n?", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex AtEnd = new(@"[ \t]+[*_`]*no split[.!]?[*_`]*\s*$", RegexOptions.IgnoreCase);
}
