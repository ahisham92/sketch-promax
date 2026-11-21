using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// What the coordinator says about other jobs comes from the jobs as they are, not from its memory (bug 123, 21 Nov 2026:
/// at 22:36 it told the phone "Your other worker is still drawing the existing connection detail on A-101"; that job
/// had finished two seconds earlier and was being checked, and the line was written from what it remembered of starting
/// it). <see cref="Line"/> is the live state chat_jobs hands back with each start and message; <see cref="Correction"/>
/// adds one line to a reply that calls a job busy when it finished while the coordinator was answering.
/// </summary>
internal static class JobTruth
{
    /// <summary>The main jobs (not a teammate's check of one, not a goal check).</summary>
    private static bool Main(ChatJob j) => j.ReviewOf == 0 && j.GoalCheck == 0;

    private static string Who(ChatJob j) => j.Worker >= 0 && JobBoard.WorkerName(j.Worker) is { Length: > 0 } name ? name : "";

    /// <summary>
    /// The other jobs right now, in one line for the coordinator: each open one with what it is doing, and each finished
    /// one a teammate is still checking. "No other job is working right now." when there are none.
    /// </summary>
    public static string Line(IEnumerable<ChatJob> jobs, int except = 0)
    {
        var all = jobs.ToList();
        var parts = new List<string>();
        foreach (var j in all.Where(j => Main(j) && j.Number != except).OrderBy(j => j.Number))
        {
            var checking = all.Any(c => c.ReviewOf == j.Number && c.Open);
            string? state = j.Open ? JobBoard.StateLabel(j).ToLowerInvariant()
                : checking ? "finished, a teammate is checking it"
                : null;
            if (state is null) continue;
            var who = Who(j) is { Length: > 0 } name ? $" ({name})" : "";
            parts.Add($"job {j.Number}{who} \"{j.Title}\": {state}");
        }
        return parts.Count == 0
            ? "No other job is working right now."
            : "Other jobs right now (live, use only this): " + string.Join("; ", parts) + ".";
    }

    private static readonly Regex Busy = new(
        @"\b(still|currently|right now|is (now )?(busy|working|drawing|reading|checking|modelling|modeling|doing|on (it|the|that|this))|are (busy|working|still))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OtherWorker = new(@"\b(other|second) (worker|job)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "check", "detail", "details", "sheet", "sheets", "model", "level", "levels", "views", "plans", "section", "sections", "existing",
        "about", "there", "their", "which", "where", "after", "before", "drawing", "drawings", "update", "fix",
    };

    /// <summary>True when the reply names the job: "job 254", a sheet or element mark from its title ("A-101"), two of its words, or its worker.</summary>
    internal static bool Mentions(string reply, ChatJob job, bool byWorker)
    {
        if (Regex.IsMatch(reply, $@"\bjob {job.Number}\b", RegexOptions.IgnoreCase)) return true;
        var words = Regex.Split(job.Title ?? "", @"[^\p{L}\p{Nd}\-]+").Where(w => w.Length > 0).ToList();
        // A mark with a digit ("A-101", "B10", "10-MP1") names it on its own.
        if (words.Any(w => w.Length >= 3 && w.Any(char.IsDigit) && Regex.IsMatch(reply, $@"(?<![\p{{L}}\p{{Nd}}]){Regex.Escape(w)}(?![\p{{L}}\p{{Nd}}])", RegexOptions.IgnoreCase)))
            return true;
        var plain = words.Where(w => w.Length >= 5 && !w.Any(char.IsDigit) && !Common.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (plain.Count >= 2 && plain.Count(w => Regex.IsMatch(reply, $@"\b{Regex.Escape(w)}", RegexOptions.IgnoreCase)) >= 2) return true;
        return byWorker && Who(job) is { Length: > 0 } name && Regex.IsMatch(reply, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// The line added to the coordinator's reply when it calls a job busy ("still", "is drawing") that isn't: one that was
    /// working when the turn started and finished before the reply was shown. Null when the reply is right (or says nothing
    /// of the kind). By its worker's name only when that worker has no other job going now.
    /// </summary>
    public static string? Correction(string reply, IEnumerable<ChatJob> atStart, IEnumerable<ChatJob> now)
    {
        if (string.IsNullOrWhiteSpace(reply) || !Busy.IsMatch(reply)) return null;
        var current = now.ToList();
        var finished = atStart.Where(b => Main(b) && b.Active)
            .Select(b => current.FirstOrDefault(c => c.Number == b.Number))
            .Where(c => c is not null && !c.Open).Select(c => c!).ToList();
        if (finished.Count == 0) return null;
        var named = finished.Where(j => Mentions(reply, j, byWorker: !current.Any(o => o.Open && o.Number != j.Number && o.Worker == j.Worker && Main(o)))).ToList();
        // "Your other worker is still ...": the one job that finished meanwhile, when it is the only one.
        if (named.Count == 0 && finished.Count == 1 && OtherWorker.IsMatch(reply)) named = finished;
        if (named.Count == 0) return null;
        var lines = named.Select(j =>
        {
            var who = Who(j) is { Length: > 0 } name ? name : $"Job {j.Number}";
            var checking = current.Any(c => c.ReviewOf == j.Number && c.Open);
            var how = j.State switch
            {
                JobBoard.Failed => "has just stopped with a problem on",
                JobBoard.Stopped => j.Paused ? "is paused on" : "has stopped work on",
                _ => "has just finished",
            };
            return $"{who} {how} \"{j.Title}\"{(checking ? "; a teammate is checking it now" : "")}.";
        });
        return "Update: " + string.Join(" ", lines);
    }
}
