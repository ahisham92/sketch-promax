using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>A question an autopilot job asked the user by email (AskUser): what it assumes until they answer, and the answer.</summary>
internal sealed class EmailQuestion
{
    /// <summary>Its number in this Revit session (Q1, Q2, …): how the email and the reply name it.</summary>
    public int Id { get; set; }
    /// <summary>The job that asked (its key), or "" for the coordinator.</summary>
    public string JobKey { get; set; } = "";
    /// <summary>Who asked, for the email ("Job 7: Existing model").</summary>
    public string Asker { get; set; } = "";
    public string Question { get; set; } = "";
    /// <summary>What the job does if no answer comes in time.</summary>
    public string Assumed { get; set; } = "";
    public DateTime Asked { get; set; }
    /// <summary>When its email went (default: not sent yet), and the email's token (its subject carries it).</summary>
    public DateTime Sent { get; set; }
    public string Token { get; set; } = "";
    /// <summary>The user's answer (null: none yet), and when it came.</summary>
    public string? Answer { get; set; }
    public DateTime Answered { get; set; }
    /// <summary>The job got the answer itself (ask_user wait): it isn't sent to it again.</summary>
    public bool Taken { get; set; }
    /// <summary>The answer was passed on to the job or the coordinator.</summary>
    public bool Delivered { get; set; }
}

/// <summary>
/// Asking the user by email while an autopilot runs (Settings › Chat › Autopilot): questions asked close together go in
/// one email to the user's own address, each with the answer assumed if no reply comes in time; the reply is read back
/// and each answer goes to the job that asked. The rules here need no Outlook, WPF or Revit (the unit tests compile this
/// file); OutlookMail sends and reads, AskUser runs it.
/// </summary>
internal static class AskByEmail
{
    /// <summary>Every email's subject starts with this, so the user tells them apart from mail from themselves.</summary>
    public const string SubjectTag = "[Sketch ProMax]";

    /// <summary>The Outlook folder the questions and replies are moved to once read.</summary>
    public const string Folder = "Sketch ProMax";

    /// <summary>Questions asked within this time of the first one waiting go in the same email.</summary>
    public static readonly TimeSpan Gather = TimeSpan.FromSeconds(30);

    /// <summary>The default wait for a reply before the job carries on with its assumption (Settings: 0 asks nothing).</summary>
    public const int DefaultWaitMinutes = 10;
    public static readonly int[] WaitChoices = [0, 5, 10, 15, 20, 30];

    /// <summary>The longest one ask_user wait call holds (a job calls again to keep waiting).</summary>
    public const int WaitCallSeconds = 45;

    /// <summary>
    /// How often the Inbox is read for replies: every 30 s while a job may be waiting on an answer (within the wait after
    /// its email went), else every 2 minutes (a progress email's reply, or an answer coming late). Reading more often made
    /// Outlook heavy.
    /// </summary>
    public static TimeSpan ReadEvery(IEnumerable<EmailQuestion> open, DateTime nowUtc, int waitMinutes) =>
        open.Any(q => q.Sent != default && nowUtc < q.Sent.AddMinutes(Math.Max(1, waitMinutes))) ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2);

    /// <summary>The oldest open email's time, less 2 minutes: only mail received since then is looked at (UTC).</summary>
    public static DateTime ReadSince(IEnumerable<DateTime> sentUtc, DateTime nowUtc)
    {
        var list = sentUtc.Where(t => t != default).ToList();
        return (list.Count > 0 ? list.Min() : nowUtc).AddMinutes(-2);
    }

    /// <summary>A short token for one email ("A7F2"): the reply's subject carries it back.</summary>
    public static string NewToken(Random random) => random.Next(0x1000, 0x10000).ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>The questions to send now: every one not sent yet, once the oldest has waited <see cref="Gather"/> for others to join.</summary>
    public static List<EmailQuestion> Due(IEnumerable<EmailQuestion> questions, DateTime now)
    {
        var waiting = questions.Where(q => q.Sent == default && q.Answer is null).OrderBy(q => q.Id).ToList();
        return waiting.Count > 0 && now - waiting.Min(q => q.Asked) >= Gather ? waiting : [];
    }

    /// <summary>The email's subject: the tag, the question (or how many), and the token the reply carries back.</summary>
    public static string Subject(IReadOnlyList<EmailQuestion> batch, string token)
    {
        var what = batch.Count == 1 ? $"Q{batch[0].Id}: {Short(batch[0].Question, 60)}" : $"{batch.Count} questions (Q{string.Join(", Q", batch.Select(q => q.Id))})";
        return $"{SubjectTag} {what} #{token}";
    }

    /// <summary>The email's text: each question, who asks, what it assumes and until when, and how to reply.</summary>
    public static string Body(IReadOnlyList<EmailQuestion> batch, string goal, DateTime sentLocal, int waitMinutes)
    {
        var sb = new StringBuilder();
        sb.Append("The autopilot needs your answer").Append(batch.Count == 1 ? "" : "s").Append(". Goal: ").Append(Short(goal, 200)).Append("\r\n\r\n");
        foreach (var q in batch)
        {
            sb.Append('Q').Append(q.Id).Append(" (").Append(q.Asker).Append("): ").Append(q.Question.Trim()).Append("\r\n");
            sb.Append("   If you don't reply by ").Append(sentLocal.AddMinutes(waitMinutes).ToString("HH:mm", CultureInfo.InvariantCulture))
              .Append(", it carries on assuming: ").Append(q.Assumed.Trim()).Append("\r\n\r\n");
        }
        sb.Append(batch.Count == 1
            ? "Reply to this email with your answer. \"ok\" keeps the assumption. A later reply still counts: the work is put right."
            : $"Reply to this email with one line per question, such as \"Q{batch[0].Id}: your answer\". \"ok\" keeps the assumption. A later reply still counts: the work is put right.");
        sb.Append("\r\n\r\nSketch ProMax");
        return sb.ToString();
    }

    /// <summary>True for a reply's subject (RE:, AW:, …) carrying <paramref name="token"/>; the question email itself isn't one.</summary>
    public static bool IsReply(string subject, string token) =>
        Regex.IsMatch(subject ?? "", @"^\s*(re|aw|sv|antw|r|rv|ref)\s*:", RegexOptions.IgnoreCase) && (subject ?? "").IndexOf("#" + token, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The token a subject carries ("#A7F2"), or null.</summary>
    public static string? TokenOf(string subject) => Regex.Match(subject ?? "", @"#([0-9A-F]{4})\b", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;

    /// <summary>What the user wrote, without the quoted email under it (Outlook, Gmail, iPhone and Android mail).</summary>
    public static string NewText(string body)
    {
        var lines = (body ?? "").Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith(">", StringComparison.Ordinal)
                || Regex.IsMatch(line, @"^(From|Von|De|Sent|Gesendet):\s", RegexOptions.IgnoreCase)
                || Regex.IsMatch(line, @"^-{2,}\s*Original Message", RegexOptions.IgnoreCase)
                || Regex.IsMatch(line, @"^_{10,}$")
                || Regex.IsMatch(line, @"^On .+ wrote:$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(line, @"^Get Outlook for", RegexOptions.IgnoreCase)
                || line.StartsWith("The autopilot needs your answer", StringComparison.Ordinal))
                break;
            kept.Add(raw.TrimEnd());
        }
        return string.Join("\n", kept).Trim();
    }

    /// <summary>
    /// Each question's answer from the reply's own text: "Q3: …" (or "3: …", "3 - …") lines answer those questions; with one
    /// question, the whole text answers it. "ok" (and the like) keeps the assumption. Unnumbered text with several questions
    /// answers each one the same way.
    /// </summary>
    public static Dictionary<int, string> Answers(string text, IReadOnlyList<EmailQuestion> batch)
    {
        var result = new Dictionary<int, string>();
        var reply = text.Trim();
        if (reply.Length == 0 || batch.Count == 0) return result;
        var ids = batch.Select(q => q.Id).ToHashSet();
        var current = -1;
        var parts = new Dictionary<int, StringBuilder>();
        foreach (var line in reply.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(?:Q\s*)?(\d+)\s*(?:[:)\-]|\.(?=\s))\s*(.*)$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && ids.Contains(id))
            {
                current = id;
                parts[id] = new StringBuilder(m.Groups[2].Value.Trim());
            }
            else if (current > 0 && line.Trim().Length > 0) parts[current].Append(' ').Append(line.Trim());
        }
        if (parts.Count > 0)
        {
            foreach (var part in parts) result[part.Key] = Keep(part.Value.ToString().Trim(), batch.First(q => q.Id == part.Key));
            return result;
        }
        foreach (var q in batch) result[q.Id] = Keep(reply, q);
        return result;
    }

    /// <summary>"ok", "yes", "fine", "go ahead": the assumption stands, said in full so the job can't mistake it.</summary>
    private static string Keep(string answer, EmailQuestion q) =>
        Regex.IsMatch(answer, @"^(ok(ay)?|yes|yep|fine|agreed|go ahead|correct|good|as assumed|proceed)[.! ]*$", RegexOptions.IgnoreCase)
            ? $"Keep your assumption ({q.Assumed.Trim()})." : answer;

    // ---- Progress updates ---------------------------------------------------------------------------------------------

    /// <summary>The progress email's interval by default, and the choices (Settings: 0 sends none).</summary>
    public const int DefaultUpdateMinutes = 60;
    public static readonly int[] UpdateChoices = [0, 30, 60, 120, 180];

    /// <summary>True when the next progress email is due.</summary>
    public static bool UpdateDue(DateTime last, DateTime now, int everyMinutes) => everyMinutes > 0 && now - last >= TimeSpan.FromMinutes(everyMinutes);

    public static string UpdateSubject(DateTime local, string token) => $"{SubjectTag} Progress at {local.ToString("HH:mm", CultureInfo.InvariantCulture)} #{token}";

    /// <summary>
    /// The progress email, as short as can be (the user asked, 2026-10-30): how many jobs finished in the interval, how many
    /// are left and how many work now; the questions waiting for the user's answer, when there are some; a pause or what
    /// the review team found still open, in one line each. Nothing else.
    /// </summary>
    public static string UpdateBody(AutopilotRun run, IReadOnlyList<ChatJob> jobs, DateTime since, IReadOnlyList<EmailQuestion> waiting, DateTime nowUtc)
    {
        var own = jobs.Where(j => j.Number > run.JobsBefore && j.GoalCheck == 0).ToList();
        var done = own.Count(j => j.State == JobBoard.Done && j.Ended >= since);
        var working = own.Count(j => j.Active);
        var queued = own.Count(j => j.State == JobBoard.Queued);
        var sb = new StringBuilder();
        sb.Append(done).Append(done == 1 ? " job" : " jobs").Append(" finished in the last ").Append(Span(nowUtc - since)).Append(". ");
        sb.Append(working + queued).Append(" left: ").Append(working).Append(" working now");
        if (queued > 0) sb.Append(", ").Append(queued).Append(" waiting");
        sb.Append(".\r\n");
        if (run.State == AutopilotRun.Paused) sb.Append("Paused: ").Append(Short(run.PauseWhy, 120).TrimEnd('.')).Append(".\r\n");
        var gaps = run.Gaps.Split('\n').Count(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal));
        if (gaps > 0) sb.Append("The review team found ").Append(gaps).Append(gaps == 1 ? " item" : " items").Append(" not done yet.\r\n");
        if (waiting.Count > 0)
        {
            sb.Append("\r\nWe need your input on:\r\n");
            foreach (var q in waiting.OrderBy(q => q.Id)) sb.Append("Q").Append(q.Id).Append(": ").Append(Short(q.Question, 200)).Append("\r\n");
            sb.Append("\r\nReply \"Q").Append(waiting.Min(q => q.Id)).Append(": your answer\" to answer in the email you got for it, or here with anything new.\r\n");
        }
        else sb.Append("\r\nReply to add or change anything.\r\n");
        return sb.ToString();
    }

    /// <summary>"hour", "2 hours", "30 min".</summary>
    internal static string Span(TimeSpan span)
    {
        var minutes = Math.Max(1, (int)Math.Round(span.TotalMinutes));
        if (minutes % 60 == 0) return minutes == 60 ? "hour" : minutes / 60 + " hours";
        return minutes + " min";
    }

    /// <summary>What the coordinator gets when the user replied to a progress email.</summary>
    public static string FromEmail(string text) =>
        "[The user, by email from their phone, while the autopilot runs] " + text.Trim() + "\n\nTake this in as the user's message: fit it into the " +
        "work (hand it out, or change what is planned) and carry on.";

    /// <summary>What ask_user answers once a reply came.</summary>
    public static string AnswerLine(EmailQuestion q) => $"The user answered Q{q.Id} by email: \"{q.Answer}\". Go by it (you assumed: {q.Assumed}).";

    /// <summary>What ask_user says once the wait is over without a reply.</summary>
    public static string NoReplyLine(EmailQuestion q, int waitMinutes) =>
        $"No reply to Q{q.Id} in {waitMinutes} minutes: carry on with your assumption ({q.Assumed}) and list it under the decisions you made. " +
        "If the user answers later, you'll be told, to put the work right.";

    /// <summary>The message a job (or the coordinator) gets when an answer comes after it stopped waiting.</summary>
    public static string LateLine(EmailQuestion q) =>
        $"The user answered your question Q{q.Id} by email: \"{q.Answer}\". You went on assuming: {q.Assumed}. If the answer differs, put right " +
        "what you did on the assumption (check the model first), then carry on.";

    private static string Short(string text, int max)
    {
        var line = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return line.Length <= max ? line : line.Substring(0, max - 1).TrimEnd() + "…";
    }
}
