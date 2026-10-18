using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SketchProMax.Chat;

/// <summary>
/// Shrink chat and chats that recall each other (ChatRecall): the Shrink button and the automatic shrink once a chat
/// sends a lot with every message (Claude Code's /compact), and the short summary each chat keeps of itself, written in
/// the background when the chat is shrunk, left or idle, which later chats about the same model start with.
/// </summary>
internal sealed partial class ChatPane
{
    /// <summary>How much the last request of this chat sent (its context), from the stream; 0 when unknown.</summary>
    private long _contextTokens;
    private Button _shrink = null!;
    private static Task? _remembering;
    private static readonly HashSet<string> RememberingIds = [];

    /// <summary>Raised on the UI thread when a chat's summary was written or changed (the Notes tab shows them).</summary>
    internal static event Action? RecallChanged;

    /// <summary>The open chat's key in the chat store ("(no model)" when none is open) and id, for the engine's instructions.</summary>
    internal static string CurrentChatKey => ConversationKey;

    internal static string CurrentChatId => _instance?._conv.Id ?? "";

    /// <summary>The "Shrink chat" icon button of the header.</summary>
    private Button ShrinkButton()
    {
        var button = ChatUi.IconButton(ChatUi.Paths.Shrink, "Shrink chat",
            "Shrink this chat: Claude replaces the earlier messages with a short summary and carries on, so each new message uses less of your plan. " +
            "It also happens by itself once a chat gets long.");
        button.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        button.Click += (_, _) => _ = Shrink(auto: false);
        return button;
    }

    /// <summary>Shrink is offered when nothing runs and the chat has a Claude Code conversation to shrink.</summary>
    private void ShowShrink()
    {
        var can = !_stopped && _turn is null && _setup == Setup.Ready && _conv.SessionId.Length > 0;
        _shrink.IsEnabled = can;
        AutomationProperties.SetHelpText(_shrink, can ? (string)_shrink.ToolTip : "Shrink chat works once the chat has a conversation and Claude isn't answering.");
    }

    /// <summary>
    /// Runs /compact as its own turn: the conversation Claude Code keeps becomes a summary of the earlier messages (what
    /// <see cref="ChatRecall.CompactInstructions"/> names), the pane keeps showing every message, and this chat's own
    /// summary for later chats is brought up to date.
    /// </summary>
    private async Task Shrink(bool auto)
    {
        if (_stopped || _sample) return;
        if (_turn is not null)
        {
            if (!auto) Status("Claude is still answering. Shrink the chat when it is done.", spin: false);
            return;
        }
        if (_setup != Setup.Ready || _conv.SessionId.Length == 0)
        {
            if (!auto) Status(_conv.SessionId.Length == 0 ? "There is nothing to shrink yet: this chat has no conversation with Claude." : "Finish the step above first.", spin: false);
            return;
        }
        var turn = _turn = new Turn { Shrinking = true, AutoShrink = auto, Line = StreamInput.Compact(ChatRecall.CompactInstructions) };
        _lastActivity = DateTime.UtcNow;
        ShowStatus(auto ? "This chat got long, so it is being shrunk to save usage…" : "Shrinking this chat…");
        try
        {
            await Deliver(turn);
        }
        catch (Exception ex)
        {
            if (_turn == turn) EndTurn();
            Error($"The chat could not be shrunk ({ex.Message}). It carries on as it was.");
        }
    }

    /// <summary>The end of a shrink turn: say what happened. True when <paramref name="end"/> was a shrink's.</summary>
    private bool ShrinkEnded(Turn turn, ResultEvent end)
    {
        if (!turn.Shrinking) return false;
        var message = end.Text.Trim().Length > 0 ? end.Text.Trim() : string.Join(" ", end.Errors);
        if (turn.Stopping) Note("Shrinking was stopped. The chat carries on as it was.");
        else if (end.IsError && message.IndexOf("No conversation found", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _conv.SessionId = "";
            _engine?.Kill();
            _engine = null;
            Note("This chat couldn't be shrunk because its earlier conversation couldn't be reopened. Your next message starts fresh.");
        }
        else if (end.IsError) Note("The chat couldn't be shrunk" + (message.Length > 0 ? $" ({Last(message, 200)})" : "") + ". It carries on as it was.");
        else if (!turn.Compacted) Note(message.Length > 0 ? message : "There wasn't enough in this chat to shrink yet.");
        EndTurn();
        if (turn.Compacted) Remember(_conv);
        return true;
    }

    /// <summary>The compaction itself (from the stream): the conversation sent with each message is now a summary.</summary>
    private void Compacted(CompactedEvent e, Turn? turn)
    {
        _contextTokens = 0;
        if (turn is not null) turn.Compacted = true;
        if (turn is { Shrinking: true })
        {
            // The shrink's result normally follows at once; should it never come, the pane doesn't stay busy.
            var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                if (_turn != turn) return;
                EndTurn();
                Remember(_conv);
            };
            wait.Start();
        }
        var before = e.PreTokens > 0 ? $" (it was about {Math.Round(e.PreTokens / 1000.0):0}k tokens per message)" : "";
        if (turn is { Shrinking: true, AutoShrink: true })
            Note("This chat got long, so it was shrunk to save usage" + before + ". Claude keeps a summary of the earlier messages; they stay on screen.");
        else if (turn is { Shrinking: true })
            Note("Chat shrunk" + before + ". Claude keeps a summary of the earlier messages, so each new message uses less; they stay on screen.");
        else
            Note("The conversation was long, so Claude summarized the earlier part to keep going.");
    }

    /// <summary>After a finished turn: shrink by itself when the chat now sends ChatRecall.AutoShrinkTokens more than its floor.</summary>
    private void ShrinkIfLong(Turn? ended, bool stopped)
    {
        if (ended is null || ended.Shrinking || stopped || ended.Outcome != "done" || _pendingModel is not null) return;
        if (!ChatRecall.ShouldAutoShrink(_contextTokens, _conv.ContextFloor)) return;
        // After what the turn's end still does (leftover messages are sent first; a turn they start comes first).
        Dispatcher.BeginInvoke(new Action(() => { if (_turn is null && !_stopped) _ = Shrink(auto: true); }));
    }

    // ---- What each chat remembers -----------------------------------------------------------------------------------

    /// <summary>
    /// Brings a chat's summary up to date in the background when it has new messages: a short Claude run (the quickest
    /// model) over its text. The summary is saved with the chat, without moving it up the history.
    /// </summary>
    private void Remember(Conversation conv)
    {
        if (_sample || _install is not { } install || conv.Id.Length == 0 || !ChatRecall.NeedsSummary(conv)) return;
        lock (RememberingIds)
            if (!RememberingIds.Add(conv.Id)) return;
        var id = conv.Id;
        var file = conv.File;
        var count = conv.Items.Count;
        var transcript = ChatRecall.Transcript(conv);
        var previous = _remembering;
        var task = Task.Run(async () =>
        {
            try
            {
                // One at a time: two summaries of the same chat must not race.
                if (previous is not null) await previous.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                var (text, error) = await BriefRunner.Run(install, ChatRecall.SummaryInstructions, transcript, readTools: false,
                    ChatRecall.SummaryModel, BriefRunner.DistilTimeoutMs, CancellationToken.None).ConfigureAwait(false);
                // A plan without the quick model: the plan's default.
                if (error.Length > 0 && ChatModels.IsRejection(error))
                    (text, error) = await BriefRunner.Run(install, ChatRecall.SummaryInstructions, transcript, readTools: false,
                        null, BriefRunner.DistilTimeoutMs, CancellationToken.None).ConfigureAwait(false);
                if (error.Length > 0) return; // tried again when the chat next changes
                var summary = ChatRecall.CleanSummary(text);
                await Dispatcher.BeginInvoke(new Action(() => SetRecall(id, file, summary, count)));
            }
            catch (Exception) { /* the chat works without its summary */ }
            finally
            {
                lock (RememberingIds) RememberingIds.Remove(id);
            }
        });
        _remembering = task;
    }

    /// <summary>Saves a chat's new summary: the open chat in place, any other in its file. An empty one only marks it done.</summary>
    private void SetRecall(string id, string file, string summary, int count)
    {
        void Apply(Conversation c)
        {
            if (summary.Length > 0) c.Recall = summary;
            c.RecallCount = Math.Min(count, c.Items.Count);
        }
        if (_conv.Id == id && !_handedOver)
        {
            Apply(_conv);
            Save(); // the pane's own save: its messages haven't changed, so the chat keeps its place in the history
        }
        else if (file.Length > 0 && ChatStore.Read(file) is { } saved && saved.Id == id)
        {
            Apply(saved);
            ChatStore.Save(saved, touch: false);
        }
        if (summary.Length > 0) RecallChanged?.Invoke();
    }

    /// <summary>
    /// Once Claude Code is ready: this model's chats left with new messages since their summary (Revit closed while one
    /// was open, say) are summarised now, so the next new chat starts knowing them.
    /// </summary>
    private void RememberLeftOver()
    {
        if (_sample || _stopped) return;
        var key = ConversationKey;
        var current = _conv.Id;
        Task.Run(() => ChatStore.ForModel(key).Where(c => c.Id != current && ChatRecall.NeedsSummary(c)).Take(3).ToList())
            .ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_stopped) return;
                    foreach (var c in t.Result) Remember(c);
                }));
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// A new chat's first message waits (a little) for the summary of the chat just left, so the new chat starts with it.
    /// </summary>
    private async Task WaitForMemory(Turn turn)
    {
        if (_remembering is not { IsCompleted: false } pending) return;
        ShowStatus("Remembering your last chat for this one…");
        await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(30)));
        if (_turn == turn) ShowStatus(turn.Shrinking ? "Shrinking this chat…" : null);
    }

    /// <summary>Changes a saved chat (the Notes tab edits or deletes a summary): the open chat in place, others in their file.</summary>
    internal static bool ChangeSaved(string file, Action<Conversation> change)
    {
        if (_instance is { } pane && string.Equals(pane._conv.File, file, StringComparison.OrdinalIgnoreCase))
        {
            change(pane._conv);
            pane.Save();
            return true;
        }
        if (ChatStore.Read(file) is not { } conv) return false;
        change(conv);
        ChatStore.Save(conv, touch: false);
        return true;
    }
}
