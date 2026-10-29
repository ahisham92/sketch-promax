using System.Globalization;

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
    /// How much the 5-hour figure rose with a new report (<paramref name="used"/>, 0 to 1) over the last one seen from any
    /// chat or job (<paramref name="last"/>, updated). A lower figure means the window reset: what it shows was used since.
    /// The first report, or one without a figure, counts nothing.
    /// </summary>
    public static double Rise(ref double? last, double? used)
    {
        if (used is not { } now) return 0;
        var before = last;
        last = now;
        if (before is not { } was) return 0;
        return now >= was ? now - was : now;
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

    /// <summary>Checks of one job at most (a job sent back to fix what a check found is checked once more).</summary>
    public const int MaxReviews = 2;

    /// <summary>The verdict's first word when the work is right.</summary>
    public const string PassedWord = "PASSED", IssuesWord = "ISSUES";

    public static string Normal(string? mode) => mode is Always or AutopilotOnly or Off ? mode : Always;

    /// <summary>True when job <paramref name="job"/>, just finished, gets a check before it is reported.</summary>
    public static bool Wanted(string? mode, bool autopilot, ChatJob job) =>
        Normal(mode) switch { Off => false, AutopilotOnly => autopilot, _ => true }
        && job.State == JobBoard.Done && job.ReviewOf == 0 && job.Review != Pending && job.Reviews < MaxReviews;

    /// <summary>The checking job's task: the job's task and report, and how to answer.</summary>
    public static string Task(ChatJob done) =>
        $"Check the work of job {done.Number} (\"{done.Title}\"), which says it is finished, against what it was asked to do.\n\n" +
        $"Its task:\n{done.Task.Trim()}\n\nIts report:\n{(done.Summary.Trim().Length > 0 ? done.Summary.Trim() : "(it gave none)")}\n\n" +
        "How to check: read the model with Sketch ProMax's tools and see that each part of the task is really done in the model " +
        "(the elements, counts, names, values, views and sheets it should have made or changed), not just reported. Don't change " +
        "the model: changes are refused for a check; say what needs fixing instead. Be quick: check what matters, not everything twice.\n\n" +
        $"End your answer with one line: \"{PassedWord}\" when the work is done right, or \"{IssuesWord}: \" followed by what is wrong " +
        "or missing, each with what to fix.";

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

    public static bool Passed(string verdict) => string.Equals(verdict, PassedWord, StringComparison.Ordinal);

    /// <summary>What the coordinator is told about the check of a job's work; "" when it wasn't checked.</summary>
    public static string ReportLine(ChatJob job) =>
        job.Review.Length == 0 || job.Review == Pending ? ""
        : Passed(job.Review) ? $"A teammate (job {job.ReviewedBy}) checked the work in the model: it passed."
        : $"A teammate (job {job.ReviewedBy}) checked the work in the model and found: {job.Review} Send job {job.Number} a message to fix it (or say why it is fine).";
}
