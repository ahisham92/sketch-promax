using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>A question an autopilot job asked the user on their phone (AskUser): what it assumes until they answer, and the answer.</summary>
internal sealed class UserQuestion
{
    /// <summary>Its number in this Revit session (Q1, Q2, …): how the phone and the run log name it.</summary>
    public int Id { get; set; }
    /// <summary>The job that asked (its key), or "" for the coordinator.</summary>
    public string JobKey { get; set; } = "";
    /// <summary>Who asked ("Job 7: Existing model").</summary>
    public string Asker { get; set; } = "";
    public string Question { get; set; } = "";
    /// <summary>What the job does if no answer comes in time.</summary>
    public string Assumed { get; set; } = "";
    public DateTime Asked { get; set; }
    /// <summary>When it went to the phone (default: not sent yet).</summary>
    public DateTime Sent { get; set; }
    /// <summary>Its message id on the phone (Iris): the answer names it.</summary>
    public long PhoneId { get; set; }
    /// <summary>The user's answer (null: none yet), and when it came.</summary>
    public string? Answer { get; set; }
    public DateTime Answered { get; set; }
    /// <summary>The job got the answer itself (ask_user wait): it isn't sent to it again.</summary>
    public bool Taken { get; set; }
    /// <summary>The answer was passed on to the job or the coordinator.</summary>
    public bool Delivered { get; set; }
}

/// <summary>
/// Asking the user on their phone while an autopilot runs (Settings › Chat › Autopilot and Your phone): questions go
/// out after a short wait for others asked meanwhile, each with a one-tap button for the answer assumed if none comes in
/// time; progress updates are as short as can be. The rules here need no network, WPF or Revit (the unit tests compile
/// this file); Phone.PhoneLink sends and reads, AskUser runs it.
/// </summary>
internal static class AskRules
{
    /// <summary>A question waits this long for others to go out with it.</summary>
    public static readonly TimeSpan Gather = TimeSpan.FromSeconds(30);

    /// <summary>The default wait for an answer before the job carries on with its assumption (Settings: 0 asks nothing).</summary>
    public const int DefaultWaitMinutes = 10;
    public static readonly int[] WaitChoices = [0, 5, 10, 15, 20, 30];

    /// <summary>The longest one ask_user wait call holds (a job calls again to keep waiting).</summary>
    public const int WaitCallSeconds = 45;

    /// <summary>
    /// How often the phone is read: every 30 s while a job may be waiting on an answer (within the wait after its question
    /// went), else every minute (a message started on the phone, Stop/Resume, or an answer coming late): the phone says a
    /// message is taken in "within a minute".
    /// </summary>
    public static TimeSpan ReadEvery(IEnumerable<UserQuestion> open, DateTime nowUtc, int waitMinutes) =>
        open.Any(q => q.Sent != default && nowUtc < q.Sent.AddMinutes(Math.Max(1, waitMinutes))) ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(1);

    /// <summary>After the user wrote from the phone, it is read this often for this long (their next message, or a reply to the chat's answer).</summary>
    public static readonly TimeSpan ChatReadEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ChatFor = TimeSpan.FromMinutes(10);

    /// <summary>The longest chat answer sent back to the phone; the rest is in the chat on the PC.</summary>
    public const int ReplyMax = 1500;

    /// <summary>
    /// The chat's answer to a message written from the phone, as the phone shows it: the answer's text, cut at
    /// <see cref="ReplyMax"/> with a pointer to the chat on the PC; a stop or failure in one line.
    /// </summary>
    public static string PhoneReply(string answer, string outcome)
    {
        var text = Regex.Replace(answer ?? "", @"\n{3,}", "\n\n").Trim();
        if (outcome != "done")
            return text.Length > 0 ? Cut(text) + "\n\n(Stopped before it finished. The rest is in the chat on the PC.)" : "It stopped before it finished. The details are in the chat on the PC.";
        return text.Length > 0 ? Cut(text) : "Done. The details are in the chat on the PC.";

        static string Cut(string t) => t.Length <= ReplyMax ? t : t.Substring(0, ReplyMax - 1).TrimEnd() + "… (the rest is in the chat on the PC)";
    }

    /// <summary>The questions to send now: every one not sent yet, once the oldest has waited <see cref="Gather"/> for others to join.</summary>
    public static List<UserQuestion> Due(IEnumerable<UserQuestion> questions, DateTime now)
    {
        var waiting = questions.Where(q => q.Sent == default && q.Answer is null).OrderBy(q => q.Id).ToList();
        return waiting.Count > 0 && now - waiting.Min(q => q.Asked) >= Gather ? waiting : [];
    }

    /// <summary>The question as the phone shows it: its number, who asks, the question.</summary>
    public static string PhoneText(UserQuestion q) =>
        q.Asker.Length > 0 ? $"Q{q.Id} ({q.Asker}): {q.Question.Trim()}" : $"Q{q.Id}: {q.Question.Trim()}";

    /// <summary>"ok", "yes", "fine", "go ahead" typed as the answer: the assumption stands, said in full so the job can't mistake it.</summary>
    public static string Keep(string answer, UserQuestion q) =>
        Regex.IsMatch(answer.Trim(), @"^(ok(ay)?|yes|yep|fine|agreed|go ahead|correct|good|as assumed|proceed)[.! ]*$", RegexOptions.IgnoreCase)
            ? $"Keep your assumption ({q.Assumed.Trim()})." : answer.Trim();

    // ---- Progress updates ---------------------------------------------------------------------------------------------

    /// <summary>The progress update's interval by default, and the choices (Settings: 0 sends none).</summary>
    public const int DefaultUpdateMinutes = 60;
    public static readonly int[] UpdateChoices = [0, 30, 60, 120, 180];

    /// <summary>True when the next progress update is due.</summary>
    public static bool UpdateDue(DateTime last, DateTime now, int everyMinutes) => everyMinutes > 0 && now - last >= TimeSpan.FromMinutes(everyMinutes);

    /// <summary>
    /// The progress update, short (the user asked, 2026-10-30): how many jobs finished in the interval, how many are left
    /// and how many work now, then one line per worker on what they are doing now; the questions waiting for the user's
    /// answer, when there are some; a pause or what the review team found still open, in one line each.
    /// </summary>
    public static string UpdateBody(AutopilotRun run, IReadOnlyList<ChatJob> jobs, DateTime since, IReadOnlyList<UserQuestion> waiting, DateTime nowUtc,
        Func<int, string>? worker = null)
    {
        var own = jobs.Where(j => j.Number > run.JobsBefore && j.GoalCheck == 0).ToList();
        var done = own.Count(j => j.State == JobBoard.Done && j.Ended >= since);
        var working = own.Count(j => j.Active);
        var queued = own.Count(j => j.State == JobBoard.Queued);
        var sb = new StringBuilder();
        sb.Append(done).Append(done == 1 ? " job" : " jobs").Append(" finished in the last ").Append(Span(nowUtc - since)).Append(". ");
        sb.Append(working + queued).Append(" left: ").Append(working).Append(" working now");
        if (queued > 0) sb.Append(", ").Append(queued).Append(" waiting");
        sb.Append(".\n");
        // What each worker is on now (the user asked, 31 Oct 2026: "I need updates from what the guys are doing").
        foreach (var job in own.Where(j => j.Active).OrderBy(j => j.Number).Take(6))
        {
            var who = worker is not null && job.Worker >= 0 ? worker(job.Worker) : "Job " + job.Number;
            var doing = job.Now.Trim().Length > 0 ? job.Now : job.Title;
            sb.Append("• ").Append(who).Append(": ").Append(Short(doing, 90).TrimEnd('.')).Append('\n');
        }
        if (run.State == AutopilotRun.Paused) sb.Append("Paused: ").Append(Short(run.PauseWhy, 120).TrimEnd('.')).Append(".\n");
        var gaps = run.Gaps.Split('\n').Count(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal));
        if (gaps > 0) sb.Append("The review team found ").Append(gaps).Append(gaps == 1 ? " item" : " items").Append(" not done yet.\n");
        if (waiting.Count > 0)
        {
            sb.Append("\nWe need your input on:\n");
            foreach (var q in waiting.OrderBy(q => q.Id)) sb.Append("Q").Append(q.Id).Append(": ").Append(Short(q.Question, 200)).Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>"hour", "2 hours", "30 min".</summary>
    internal static string Span(TimeSpan span)
    {
        var minutes = Math.Max(1, (int)Math.Round(span.TotalMinutes));
        if (minutes % 60 == 0) return minutes == 60 ? "hour" : minutes / 60 + " hours";
        return minutes + " min";
    }

    /// <summary>What the coordinator gets when the user wrote from the phone.</summary>
    public static string FromPhone(string text, string coordinator = "") =>
        "[The user, from their phone, while the autopilot runs] " + text.Trim() + "\n\nTake this in as the user's message: fit it into the " +
        "work (hand it out, or change what is planned) and carry on. " + PhoneVoice(coordinator);

    /// <summary>
    /// How an answer that goes to the phone reads: the coordinator by name, as a friendly colleague texting back (the user
    /// asked, 31 Oct 2026: "I am talking to Pitso, so it should be way more friendly"). The name is the user's own, from
    /// Settings › Chat (crew names).
    /// </summary>
    public static string PhoneVoice(string coordinator) =>
        "Your answer is sent to their phone as a chat message" + (coordinator.Length > 0 ? $" from you, {coordinator}" : "") + ": write like a " +
        "friendly colleague texting back. Warm and short (a few sentences), first person, plain words, no headings or tables, at most one " +
        "emoji. Say what you did or are doing and what the workers are on; the autopilot's jobs and workers are yours, never \"another chat\".";

    /// <summary>A message from the phone when no autopilot runs: done as if typed here, answered for the phone.</summary>
    public static string PhoneTurn(string text, string coordinator = "") =>
        "[The user wrote this from their phone, away from the PC: do it as if typed here. " + PhoneVoice(coordinator) + "] " + text;

    /// <summary>What ask_user answers once an answer came.</summary>
    public static string AnswerLine(UserQuestion q) => $"The user answered Q{q.Id} from their phone: \"{q.Answer}\". Go by it (you assumed: {q.Assumed}).";

    /// <summary>What ask_user says once the wait is over without an answer.</summary>
    public static string NoReplyLine(UserQuestion q, int waitMinutes) =>
        $"No reply to Q{q.Id} in {waitMinutes} minutes: carry on with your assumption ({q.Assumed}) and list it under the decisions you made. " +
        "If the user answers later, you'll be told, to put the work right.";

    /// <summary>The message a job (or the coordinator) gets when an answer comes after it stopped waiting.</summary>
    public static string LateLine(UserQuestion q) =>
        $"The user answered your question Q{q.Id} from their phone: \"{q.Answer}\". You went on assuming: {q.Assumed}. If the answer differs, put right " +
        "what you did on the assumption (check the model first), then carry on.";

    private static string Short(string text, int max)
    {
        var line = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return line.Length <= max ? line : line.Substring(0, max - 1).TrimEnd() + "…";
    }
}
