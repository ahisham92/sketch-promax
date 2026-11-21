using System.Threading;
using SketchProMax.Phone;

namespace SketchProMax.Chat;

/// <summary>
/// The autopilot's questions to the user on their phone (ask_user; rules in AskRules; the phone link in Phone.PhoneLink,
/// through Iris on the user's own website): a job asks, the question waits a moment for others to join it, then each goes
/// to the phone with a one-tap button for the assumed answer. While a run is on, the phone is read every 30 seconds while a
/// job may be waiting on an answer or the user is writing from the phone, else every minute, for answers, messages and
/// Stop/Resume; with or without a run, so the user can start a conversation from the phone too. A job waiting on the
/// answer gets it at once; an answer that comes after the job stopped waiting is passed on by the chat
/// (<see cref="Answered"/>). Runs on a background loop, never on Revit's thread. With no phone connected, nothing is asked:
/// the job makes the call itself.
/// </summary>
internal static class AskUser
{
    private static readonly object Gate = new();
    private static readonly List<UserQuestion> Questions = [];
    private static int _next;
    private static Task? _loop;
    private static DateTime _readAt, _chatUntil;

    /// <summary>The goal of the run (set by the chat when a run starts).</summary>
    public static string Goal { get; set; } = "";

    /// <summary>Raised (on the loop's thread) when an answer came that the asking job hasn't taken itself.</summary>
    public static event Action<UserQuestion>? Answered;

    /// <summary>Raised (on the loop's thread) when the user wrote from the phone: what they wrote, and in which project's chat.</summary>
    public static event Action<string, string>? UserWrote;

    /// <summary>Raised (on the loop's thread) when the user pressed one of PhoneLink.Commands on the phone, in which project's chat.</summary>
    public static event Action<string, string>? Command;

    /// <summary>Raised when a question or update went to the phone, or couldn't (text for the run's log).</summary>
    public static event Action<string>? Logged;

    /// <summary>Raised (on the loop's thread) when a Questions tab question went to the phone (error null), or couldn't (why).</summary>
    public static event Action<UserQuestion, string?>? TabSent;

    /// <summary>A phone is connected (Settings › Chat › Your phone) and switched on in the chat: questions and progress go there.</summary>
    public static bool ByPhone => PhoneLink.Active;

    /// <summary>The wait for an answer, in minutes (Settings › Chat › Autopilot; 0, or no phone connected: nothing is asked).</summary>
    public static int WaitMinutes
    {
        get
        {
            if (!ByPhone) return 0;
            try { return Math.Max(0, Settings.AgentSettings.Load().AutopilotAskWaitMinutes); }
            catch (Exception) { return AskRules.DefaultWaitMinutes; }
        }
    }

    /// <summary>The progress interval, in minutes (Settings › Chat › Autopilot; 0, or no phone connected: no updates).</summary>
    public static int UpdateMinutes
    {
        get
        {
            if (!ByPhone) return 0;
            try { return Math.Max(0, Settings.AgentSettings.Load().PhoneUpdateMinutes); }
            catch (Exception) { return AskRules.DefaultUpdateMinutes; }
        }
    }

    /// <summary>
    /// While a phone is connected it is read for messages, answers and Stop/Resume, with or without a run. Cheap to call
    /// again (the chat calls it when it opens, when a phone is connected and every 30 s during a run).
    /// </summary>
    public static void Listen()
    {
        lock (Gate)
        {
            if (ByPhone) _loop ??= Task.Run(Loop);
        }
    }

    /// <summary>
    /// Sends a progress update or reply to <paramref name="topic"/>'s chat on the phone (the project, PhoneProjects; on a
    /// background thread). Null when sent, else why not.
    /// </summary>
    public static async Task<string?> SendUpdateAsync(string body, string topic = "")
    {
        var error = await PhoneLink.UpdateAsync(body, topic).ConfigureAwait(false);
        if (error is null) Remember("You sent", body, topic);
        return error;
    }

    // ---- The conversation on the phone, for the coordinator ----------------------------------------------------------

    private static readonly List<(string Topic, string Line)> Said = [];

    /// <summary>
    /// A line of the phone conversation (questions, updates, replies, what the user wrote and answered), kept for the
    /// coordinator: on 31 Oct 2026 the user asked "what do you mean by squaring off?" about the coordinator's own question
    /// and got "that didn't come from me", because the turn answering the phone didn't see what had been sent there.
    /// </summary>
    internal static void Remember(string who, string text, string topic = "")
    {
        lock (Gate)
        {
            Said.Add((topic.Trim(), $"[{DateTime.Now:HH:mm}] {who}: {AskRules.Clip(text, 600)}"));
            // Kept per project's chat: each keeps its last few lines.
            var mine = Said.Where(s => PhoneProjects.Same(s.Topic, topic)).ToList();
            foreach (var old in mine.Take(Math.Max(0, mine.Count - AskRules.PhoneHistoryMax))) Said.Remove(old);
        }
    }

    /// <summary>The conversation in <paramref name="topic"/>'s chat on the phone so far (oldest first), the message being answered last.</summary>
    public static string History(string topic = "")
    {
        lock (Gate) return string.Join("\n", Said.Where(s => PhoneProjects.Same(s.Topic, topic)).Select(s => s.Line));
    }

    /// <summary>A job (or the coordinator) asks: the question waits to go out with any others asked meanwhile.</summary>
    public static UserQuestion Ask(string jobKey, string asker, string question, string assumed, string topic = "")
    {
        UserQuestion q;
        lock (Gate)
        {
            q = new UserQuestion { Id = ++_next, JobKey = jobKey, Asker = asker, Question = question.Trim(), Assumed = assumed.Trim(), Asked = DateTime.UtcNow, Topic = topic.Trim(), Cid = "ask-" + Guid.NewGuid().ToString("N") };
            Questions.Add(q);
            _loop ??= Task.Run(Loop);
        }
        return q;
    }

    /// <summary>
    /// A question of a chat's Questions tab (chat_questions, or one caught in a reply) while the phone is on: it goes to the
    /// phone at once, with its answers as buttons, and the answer comes back to that chat's tab (<paramref name="chatKey"/>,
    /// JobBoard.ChatKey; through <see cref="Answered"/>). Asked again (written again): the one sent before is closed on the phone.
    /// <paramref name="version"/>: when it was asked or last written again; the same version sent again is one message on the phone.
    /// </summary>
    public static UserQuestion AskTab(string chatKey, int number, string asker, string question, IReadOnlyList<string> options, int recommended, string topic, DateTime version = default)
    {
        UserQuestion q;
        List<UserQuestion> old;
        var cid = AskRules.TabCid(chatKey, number, version);
        lock (Gate)
        {
            // The same version already waiting to go, or on the phone: nothing to send again.
            if (Questions.FirstOrDefault(x => x.TabNumber == number && x.JobKey == chatKey && x.Answer is null && x.Cid == cid) is { } same) return same;
            old = Questions.Where(x => x.TabNumber == number && x.JobKey == chatKey && x.Answer is null).ToList();
            foreach (var o in old) Questions.Remove(o);
            q = new UserQuestion
            {
                Id = ++_next, JobKey = chatKey, TabNumber = number, Asker = asker, Question = question.Trim(), Options = options.ToList(),
                Recommended = recommended, Topic = topic.Trim(), Cid = cid,
                // Not held to gather others: the coordinator asked them together already.
                Asked = DateTime.UtcNow - AskRules.Gather,
            };
            Questions.Add(q);
            _loop ??= Task.Run(Loop);
        }
        foreach (var o in old.Where(o => o.PhoneId != 0 && o.Cid != cid)) _ = PhoneLink.SettleAsync(o.PhoneId, $"{AskRules.Label(o)} was written again; see the new one.");
        return q;
    }

    /// <summary>The tab questions of chat <paramref name="chatKey"/> on the phone and not answered there yet (their numbers).</summary>
    public static List<int> TabWaiting(string chatKey)
    {
        lock (Gate) return Questions.Where(q => q.TabNumber > 0 && q.JobKey == chatKey && q.Answer is null).Select(q => q.TabNumber).Distinct().ToList();
    }

    /// <summary>Tab question <paramref name="number"/> was answered on the PC or withdrawn: it is closed on the phone, with <paramref name="why"/>.</summary>
    public static void SettleTab(string chatKey, int number, string why)
    {
        List<UserQuestion> done;
        lock (Gate)
        {
            done = Questions.Where(q => q.TabNumber == number && q.JobKey == chatKey && q.Answer is null).ToList();
            foreach (var q in done) Questions.Remove(q);
        }
        foreach (var q in done.Where(q => q.PhoneId != 0)) _ = PhoneLink.SettleAsync(q.PhoneId, why);
    }

    /// <summary>The questions sent and not answered yet (for the progress update).</summary>
    public static List<UserQuestion> Waiting()
    {
        lock (Gate) return Questions.Where(q => q.Sent != default && q.Answer is null && DateTime.UtcNow - q.Sent < TimeSpan.FromDays(1)).ToList();
    }

    public static UserQuestion? Find(int id)
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

    /// <summary>The run ended: questions still open are dropped (the phone is still read for messages).</summary>
    public static void Clear()
    {
        // A Questions tab question stays: the chat still waits for its answer after the run.
        lock (Gate) Questions.RemoveAll(q => q.TabNumber == 0 && (q.Answer is null || q.Delivered || q.Taken) || q.TabNumber > 0 && (q.Delivered || q.Taken));
    }

    /// <summary>Every 15 s while questions are open or a phone is connected: send what is due, read the phone, pass on answers nobody took.</summary>
    private static async Task Loop()
    {
        while (true)
        {
            try { await TickAsync(DateTime.UtcNow).ConfigureAwait(false); }
            catch (Exception) { /* tried again next time */ }
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            lock (Gate)
            {
                // Open for a day at most: after that an answer isn't looked for.
                Questions.RemoveAll(q => (q.Answer is not null && (q.Taken || q.Delivered)) || DateTime.UtcNow - q.Asked > TimeSpan.FromDays(1));
                if (Questions.Count == 0 && !ByPhone)
                {
                    _loop = null;
                    return;
                }
            }
        }
    }

    private static async Task TickAsync(DateTime now)
    {
        List<UserQuestion> due;
        lock (Gate) due = AskRules.Due(Questions, now);
        if (due.Count > 0)
        {
            var wait = Math.Max(1, WaitMinutes);
            var failed = new List<string>();
            foreach (var q in due)
            {
                // A tab question has no deadline: the chat waits for its answer, from the phone or the PC.
                var (id, error) = ByPhone
                    ? await PhoneLink.AskAsync(AskRules.PhoneText(q), q.Assumed, q.TabNumber > 0 ? null : now.AddMinutes(wait), q.Topic, AskRules.PhoneOptions(q), q.Cid).ConfigureAwait(false)
                    : (0, "no phone is connected");
                lock (Gate)
                {
                    q.PhoneId = id;
                    q.Sent = error is null ? now : now.AddYears(-1); // not sent: its wait is over at once
                    if (error is not null && q.TabNumber > 0) Questions.Remove(q); // still in the tab on the PC; sent again when the phone works
                }
                if (error is null) Remember("You asked", AskRules.PhoneText(q) + (q.Assumed.Length > 0 ? " (if no answer: " + q.Assumed + ")" : ""), q.Topic);
                if (q.TabNumber > 0) TabSent?.Invoke(q, error);
                else if (error is not null) failed.Add($"Q{q.Id} ({error})");
            }
            var asked = due.Where(q => q.TabNumber == 0).ToList();
            if (asked.Count > 0)
                Logged?.Invoke(failed.Count == 0
                    ? $"Sent {(asked.Count == 1 ? "Q" + asked[0].Id : asked.Count + " questions")} to your phone ({string.Join("; ", asked.Select(q => Short(q.Question)))})."
                    : $"Couldn't send {string.Join(", ", failed)} to your phone; the work carried on with its assumptions.");
        }

        List<UserQuestion> open;
        lock (Gate) open = Questions.Where(q => q.PhoneId != 0 && q.Answer is null && now - q.Sent < TimeSpan.FromDays(1)).ToList();
        var every = now < _chatUntil ? AskRules.ChatReadEvery : AskRules.ReadEvery(open, now, WaitMinutes);
        if (ByPhone && now - _readAt >= every)
        {
            _readAt = now;
            // With two Revit windows open, both read the phone (each its own answers), but only one takes the user's
            // messages and Stop/Resume: on 31 Oct 2026 both answered one message, differently.
            var takes = PhoneLink.TakesMessages(now);
            foreach (var m in await PhoneLink.InboxAsync().ConfigureAwait(false) ?? [])
            {
                var text = m.Text.Trim();
                if (m.Error.Length > 0 || text.Length == 0) continue;
                // Each project has its chat on the phone: the Revit with a chat for that project takes what is written there.
                if (m.Kind is "command" or "message" && !PhoneLink.TakesTopic(m.Topic, takes, now)) continue;
                if (m.Kind == "command") Command?.Invoke(text, m.Topic);
                else if (m.Kind == "message")
                {
                    Remember("They wrote", text, m.Topic);
                    // Writing from the phone: read every 30 s for a while, so a conversation doesn't wait minutes per message.
                    _chatUntil = now + AskRules.ChatFor;
                    UserWrote?.Invoke(text, m.Topic);
                }
                else if (m.Kind == "answer")
                    lock (Gate)
                        if (Questions.FirstOrDefault(q => q.PhoneId != 0 && q.PhoneId == m.ReplyTo) is { Answer: null } q)
                        {
                            Said.Add((q.Topic, $"[{DateTime.Now:HH:mm}] They answered {AskRules.Label(q)}: {AskRules.Clip(text, 600)}"));
                            q.Answer = AskRules.Keep(text, q);
                            q.Answered = now;
                        }
            }
        }

        // Answers the asking job didn't take itself (it stopped waiting, or never waited): passed on by the chat.
        List<UserQuestion> late;
        lock (Gate) late = Questions.Where(q => q.Answer is not null && !q.Taken && !q.Delivered && now - q.Answered > TimeSpan.FromSeconds(10)).ToList();
        foreach (var q in late)
        {
            q.Delivered = true;
            Answered?.Invoke(q);
        }
    }

    private static string Short(string text) => text.Length <= 80 ? text : text.Substring(0, 79) + "…";
}
