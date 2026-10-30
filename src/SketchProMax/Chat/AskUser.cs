using System.Threading;

namespace SketchProMax.Chat;

/// <summary>
/// The autopilot's questions to the user by email (ask_user; rules in AskByEmail): a job asks, the question waits a moment
/// for others to join it, then one email goes to the user's own address through Outlook. Replies are looked for every 30
/// seconds and each answer goes to the question; a job waiting on it gets it at once, and an answer that comes after the
/// job stopped waiting is passed on by the chat (<see cref="Answered"/>). Runs on a background loop, never on Revit's thread.
/// </summary>
internal static class AskUser
{
    private static readonly object Gate = new();
    private static readonly List<EmailQuestion> Questions = [];
    private static readonly Random Tokens = new();
    private static int _next;
    private static Task? _loop;

    /// <summary>The goal the emails name (set by the chat when a run starts).</summary>
    public static string Goal { get; set; } = "";

    /// <summary>Raised (on the loop's thread) when an answer came that the asking job hasn't taken itself.</summary>
    public static event Action<EmailQuestion>? Answered;

    /// <summary>Raised (on the loop's thread) when the user replied to a progress email: what they wrote.</summary>
    public static event Action<string>? UserWrote;

    /// <summary>Progress emails whose replies are looked for (their token, and when each went).</summary>
    private static readonly Dictionary<string, DateTime> Updates = new(StringComparer.Ordinal);

    /// <summary>The progress interval, in minutes (Settings › Chat › Autopilot; 0: no progress emails).</summary>
    public static int UpdateMinutes
    {
        get
        {
            try { return Math.Max(0, Settings.AgentSettings.Load().AutopilotUpdateMinutes); }
            catch (Exception) { return AskByEmail.DefaultUpdateMinutes; }
        }
    }

    /// <summary>Sends a progress email (on a background thread) and looks for replies to it. Null when sent, else why not.</summary>
    public static async Task<string?> SendUpdateAsync(string body)
    {
        var token = AskByEmail.NewToken(Tokens);
        var refused = await Task.Run(() => OutlookMail.SendToSelf(AskByEmail.UpdateSubject(DateTime.Now, token), body)).ConfigureAwait(false);
        var error = OutlookMail.LastError.Length > 0 ? OutlookMail.LastError : refused;
        if (error is not null) return error;
        lock (Gate)
        {
            Updates[token] = DateTime.UtcNow;
            _loop ??= Task.Run(Loop);
        }
        return null;
    }

    /// <summary>Raised when an email went out, or couldn't (text for the run's log).</summary>
    public static event Action<string>? Logged;

    /// <summary>The wait for a reply, in minutes (Settings › Chat › Autopilot; 0: no questions by email).</summary>
    public static int WaitMinutes
    {
        get
        {
            try { return Math.Max(0, Settings.AgentSettings.Load().AutopilotAskWaitMinutes); }
            catch (Exception) { return AskByEmail.DefaultWaitMinutes; }
        }
    }

    /// <summary>A job (or the coordinator) asks: the question waits to go out with any others asked meanwhile.</summary>
    public static EmailQuestion Ask(string jobKey, string asker, string question, string assumed)
    {
        EmailQuestion q;
        lock (Gate)
        {
            q = new EmailQuestion { Id = ++_next, JobKey = jobKey, Asker = asker, Question = question.Trim(), Assumed = assumed.Trim(), Asked = DateTime.UtcNow };
            Questions.Add(q);
            _loop ??= Task.Run(Loop);
        }
        return q;
    }

    /// <summary>The questions emailed and not answered yet (for the progress email).</summary>
    public static List<EmailQuestion> Waiting()
    {
        lock (Gate) return Questions.Where(q => q.Sent != default && q.Answer is null && DateTime.UtcNow - q.Sent < TimeSpan.FromDays(1)).ToList();
    }

    public static EmailQuestion? Find(int id)
    {
        lock (Gate) return Questions.FirstOrDefault(q => q.Id == id);
    }

    /// <summary>
    /// Waits up to <paramref name="seconds"/> for question <paramref name="id"/>'s answer: the answer (taken: it isn't
    /// passed on again), or null while there is none yet.
    /// </summary>
    public static async Task<string?> WaitAsync(int id, int seconds, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            lock (Gate)
            {
                if (Questions.FirstOrDefault(x => x.Id == id) is { Answer: { } answer } q)
                {
                    q.Taken = true;
                    return answer;
                }
            }
            if (DateTime.UtcNow >= until || ct.IsCancellationRequested) return null;
            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
    }

    /// <summary>The run ended: questions still open are dropped (a later reply is left in the Inbox).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Questions.RemoveAll(q => q.Answer is null || q.Delivered || q.Taken);
            Updates.Clear();
        }
    }

    /// <summary>Every 30 s while questions are open: send what is due, read replies, pass on answers nobody took.</summary>
    private static async Task Loop()
    {
        while (true)
        {
            try { Tick(DateTime.UtcNow); }
            catch (Exception) { /* tried again next time */ }
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            lock (Gate)
            {
                // Open for a day at most: after that a reply isn't looked for.
                Questions.RemoveAll(q => (q.Answer is not null && (q.Taken || q.Delivered)) || DateTime.UtcNow - q.Asked > TimeSpan.FromDays(1));
                // A progress email's reply is looked for 6 hours: the next ones carry what came later.
                foreach (var old in Updates.Where(u => DateTime.UtcNow - u.Value > TimeSpan.FromHours(6)).Select(u => u.Key).ToList()) Updates.Remove(old);
                if (Questions.Count == 0 && Updates.Count == 0)
                {
                    _loop = null;
                    return;
                }
            }
        }
    }

    private static DateTime _readAt;

    private static void Tick(DateTime now)
    {
        List<EmailQuestion> due;
        lock (Gate) due = AskByEmail.Due(Questions, now);
        if (due.Count > 0)
        {
            var token = AskByEmail.NewToken(Tokens);
            var wait = Math.Max(1, WaitMinutes);
            var refused = OutlookMail.SendToSelf(AskByEmail.Subject(due, token), AskByEmail.Body(due, Goal, DateTime.Now, wait));
            var error = OutlookMail.LastError.Length > 0 ? OutlookMail.LastError : refused;
            lock (Gate)
                foreach (var q in due)
                {
                    q.Token = token;
                    q.Sent = error is null ? now : now.AddYears(-1); // not sent: its wait is over at once
                }
            Logged?.Invoke(error is null
                ? $"Emailed you {(due.Count == 1 ? "Q" + due[0].Id : due.Count + " questions")} ({string.Join("; ", due.Select(q => Short(q.Question)))})."
                : $"Couldn't email you Q{string.Join(", Q", due.Select(q => q.Id))} ({error}); the work carried on with its assumptions.");
        }
        // Replies: every 30 s while a job may be waiting on an answer, else every 2 minutes (AskByEmail.ReadEvery).
        List<EmailQuestion> open;
        List<string> updates;
        List<DateTime> sent;
        lock (Gate)
        {
            open = Questions.Where(q => q.Sent != default && q.Answer is null && q.Token.Length > 0 && now - q.Sent < TimeSpan.FromDays(1)).ToList();
            updates = Updates.Keys.ToList();
            sent = open.Select(q => q.Sent).Concat(Updates.Values).ToList();
        }
        if ((open.Count > 0 || updates.Count > 0) && now - _readAt >= AskByEmail.ReadEvery(open, now, WaitMinutes))
        {
            _readAt = now;
            var since = AskByEmail.ReadSince(sent, now).ToLocalTime();
            var replies = OutlookMail.TakeReplies(open.Select(q => q.Token).Concat(updates).Distinct().ToList(), since) ?? [];
            foreach (var reply in replies.OrderBy(r => r.Received))
            {
                if (AskByEmail.TokenOf(reply.Subject) is not { } token) continue;
                // A reply to a progress email: something new from the user, for the coordinator.
                if (updates.Contains(token))
                {
                    var text = AskByEmail.NewText(reply.Body);
                    if (text.Length > 0) UserWrote?.Invoke(text);
                    continue;
                }
                var batch = open.Where(q => q.Token == token).OrderBy(q => q.Id).ToList();
                foreach (var pair in AskByEmail.Answers(AskByEmail.NewText(reply.Body), batch))
                    lock (Gate)
                        if (batch.FirstOrDefault(q => q.Id == pair.Key) is { Answer: null } q)
                        {
                            q.Answer = pair.Value;
                            q.Answered = now;
                        }
            }
        }
        // Answers the asking job didn't take itself (it stopped waiting, or never waited): passed on by the chat.
        List<EmailQuestion> late;
        lock (Gate) late = Questions.Where(q => q.Answer is not null && !q.Taken && !q.Delivered && now - q.Answered > TimeSpan.FromSeconds(10)).ToList();
        foreach (var q in late)
        {
            q.Delivered = true;
            Answered?.Invoke(q);
        }
    }

    private static string Short(string text) => text.Length <= 80 ? text : text.Substring(0, 79) + "…";
}
