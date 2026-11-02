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
            q = new UserQuestion { Id = ++_next, JobKey = jobKey, Asker = asker, Question = question.Trim(), Assumed = assumed.Trim(), Asked = DateTime.UtcNow, Topic = topic.Trim() };
            Questions.Add(q);
            _loop ??= Task.Run(Loop);
        }
        return q;
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
        lock (Gate) Questions.RemoveAll(q => q.Answer is null || q.Delivered || q.Taken);
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
                var (id, error) = ByPhone
                    ? await PhoneLink.AskAsync(AskRules.PhoneText(q), q.Assumed, now.AddMinutes(wait), q.Topic).ConfigureAwait(false)
                    : (0, "no phone is connected");
                lock (Gate)
                {
                    q.PhoneId = id;
                    q.Sent = error is null ? now : now.AddYears(-1); // not sent: its wait is over at once
                }
                if (error is null) Remember("You asked", AskRules.PhoneText(q) + (q.Assumed.Length > 0 ? " (if no answer: " + q.Assumed + ")" : ""), q.Topic);
                if (error is not null) failed.Add($"Q{q.Id} ({error})");
            }
            Logged?.Invoke(failed.Count == 0
                ? $"Sent {(due.Count == 1 ? "Q" + due[0].Id : due.Count + " questions")} to your phone ({string.Join("; ", due.Select(q => Short(q.Question)))})."
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
                            Said.Add((q.Topic, $"[{DateTime.Now:HH:mm}] They answered Q{q.Id}: {AskRules.Clip(text, 600)}"));
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
