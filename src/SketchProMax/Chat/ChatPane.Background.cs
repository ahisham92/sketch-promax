using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// A chat that works in the background: leaving a chat while it answers (New chat, or opening another chat) no longer
/// stops it. Its Claude Code carries on, its answer and steps go on into its saved conversation, and its calls to Revit
/// take turns with the shown chat's like a parallel job's (JobBoard.Caller). The Chats switcher in the header and the
/// history list say which chats are working and which finished since you left them; opening one shows it as it is, and
/// one still working carries on in the pane. A chat whose jobs are open can't be left (they report to it).
/// </summary>
internal sealed class BackgroundChat(Conversation conv, ChatEngine engine)
{
    public const string Working = "working", Done = "done", Failed = "failed", Stopped = "stopped";

    /// <summary>At most this many chats work in the background at once (each is a Claude Code on your plan).</summary>
    public const int Limit = 3;

    public Conversation Conv { get; } = conv;
    public ChatEngine Engine { get; } = engine;
    public string State { get; set; } = Working;
    /// <summary>What it does now (the running step), while it works.</summary>
    public string Now { get; set; } = "";
    public bool SawText, SawTool;
    public DateTime Started, Ended;
    public string Brief = "";
    /// <summary>The streamed answers and the steps of the running turn, by the stream's keys and tool ids.</summary>
    public readonly Dictionary<string, ChatItem> Texts = new(), Tools = new();
    public Action<ChatEvent>? OnEvent;
    public Action<string>? OnExited;

    /// <summary>"Working…", "Finished", "Stopped with an error": as the switcher and the history say it.</summary>
    public string Label => State switch
    {
        Working => Now.Length > 0 ? "Working · " + Now : "Working…",
        Done => "Finished, not opened yet",
        Failed => "Stopped with an error, not opened yet",
        _ => "Stopped",
    };
}

internal sealed partial class ChatPane
{
    private readonly List<BackgroundChat> _background = [];
    private Button _chatsPill = null!;
    private readonly TextBlock _chatsLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>True while a chat left in the background still works.</summary>
    private bool BackgroundWorking => _background.Any(b => b.State == BackgroundChat.Working);

    private BackgroundChat? BackgroundOf(string file) =>
        file.Length == 0 ? null : _background.FirstOrDefault(b => string.Equals(b.Conv.File, file, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The shown chat answers and is being left: it carries on in the background (true), unless it can't: its jobs are
    /// open, it is planning a brief, shrinking or stopping, the connection can't tell its calls apart, or enough chats
    /// already work in the background.
    /// </summary>
    private bool Detach(out string? why)
    {
        why = null;
        if (_turn is not { Planning: false, Shrinking: false, Stopping: false } turn || _engine is not { IsAlive: true, ChatKey: { Length: > 0 } } engine
            || JobsOpen || _sample || _viewJob is not null || !ClaudeCli.JobsAvailable)
            return false;
        if (_background.Count(b => b.State == BackgroundChat.Working) >= BackgroundChat.Limit)
        {
            why = $"{BackgroundChat.Limit} chats already work in the background. Wait for one to finish (Chats, at the top), or press Stop here first.";
            return false;
        }
        RenderDirty();
        var bg = new BackgroundChat(_conv, engine) { SawText = turn.SawText, SawTool = turn.SawTool, Started = turn.Started, Brief = turn.Brief };
        foreach (var pair in _texts) bg.Texts[pair.Key] = pair.Value.Item;
        foreach (var pair in _tools) bg.Tools[pair.Key] = pair.Value.Item;
        if (_tools.Values.LastOrDefault(r => r.Item.State == "running") is { } running) bg.Now = StepNames.Title(running.Item.Name);
        bg.OnEvent = e => BackgroundEvent(bg, e);
        bg.OnExited = tail => BackgroundExited(bg, tail);
        engine.Event += bg.OnEvent;
        engine.Exited += bg.OnExited;
        // The task's control, card and time end here without stopping it; its calls now take turns like a job's.
        turn.KillTimer?.Stop();
        try { Revit.TimeLog.EndChat(); } catch (Exception) { }
        // Its live card goes (its steps are in the chat as well): a card that stopped following them would mislead.
        if (_jobItem is { } card) _conv.Items.Remove(card);
        _job = null;
        _jobItem = null;
        var left = _control is not null ? EndJob("done", null) : [];
        if (left.Count > 0 && _input.Text.Trim().Length == 0) _input.Text = string.Join("\n\n", left.Select(n => n.Text));
        _turn = null;
        _engine = null;
        CrewCoordinatorDoes(null);
        _texts.Clear();
        _tools.Clear();
        // A kind picked while it answered: its next message starts with it.
        if (_pendingType is { } kind) _conv.Type = kind;
        _pendingType = null;
        Save();
        _background.Add(bg);
        ShowChats();
        return true;
    }

    /// <summary>Opens a chat left in the background: as it is now, and still answering in the pane when it works.</summary>
    private void Reattach(BackgroundChat bg)
    {
        _background.Remove(bg);
        if (bg.OnEvent is not null) bg.Engine.Event -= bg.OnEvent;
        if (bg.OnExited is not null) bg.Engine.Exited -= bg.OnExited;
        Save();
        Remember(_conv);
        _contextTokens = 0;
        _engine?.Dispose();
        _engine = null;
        _conv = bg.Conv;
        var working = bg.State == BackgroundChat.Working && bg.Engine.IsAlive;
        if (working)
        {
            // The pane's own handlers on this engine (Deliver) answer again once it is the pane's engine.
            _engine = bg.Engine;
            _turn = new Turn { SawText = bg.SawText, SawTool = bg.SawTool, Started = bg.Started, Brief = bg.Brief };
            BeginJob(null);
            _lastActivity = DateTime.UtcNow;
        }
        else bg.Engine.Dispose();
        ShowConversation();
        if (working)
        {
            foreach (var pair in bg.Texts) if (_views.TryGetValue(pair.Value, out var view)) _texts[pair.Key] = (pair.Value, view);
            foreach (var pair in bg.Tools) if (_rows.TryGetValue(pair.Value, out var row)) _tools[pair.Key] = row;
        }
        ShowStatus();
        ShowChats();
    }

    /// <summary>What the background chat's Claude Code says: into its conversation (saved), without drawing anything.</summary>
    private void BackgroundEvent(BackgroundChat bg, ChatEvent e)
    {
        if (bg.State != BackgroundChat.Working) return;
        var conv = bg.Conv;
        void Add(ChatItem item)
        {
            if (item.Time == default) item.Time = DateTime.UtcNow;
            conv.Items.Add(item);
        }
        switch (e)
        {
            case InitEvent init:
                if (init.SessionId.Length > 0) conv.SessionId = init.SessionId;
                break;
            case TextStartEvent start:
            {
                var item = new ChatItem { Kind = "assistant" };
                Add(item);
                bg.Texts[start.Key] = item;
                bg.SawText = true;
                bg.Now = "Writing";
                SaveBackground(bg);
                break;
            }
            case TextDeltaEvent delta:
                if (bg.Texts.TryGetValue(delta.Key, out var target)) target.Text += delta.Text;
                break;
            case AssistantTextEvent text:
                if (text.Text.Trim().Length == 0) break;
                bg.SawText = true;
                Add(new ChatItem { Kind = "assistant", Text = text.Text });
                break;
            case ToolUseEvent tool:
            {
                var name = StreamParser.ShortName(tool.Name);
                var summary = StepNames.Describe(name, tool.InputJson);
                if (!bg.Tools.TryGetValue(tool.Id, out var item))
                {
                    item = new ChatItem { Kind = "tool", Name = name, State = "running", Text = summary };
                    Add(item);
                    bg.Tools[tool.Id] = item;
                }
                if (tool.InputJson.Length > 0)
                {
                    item.Input = Pretty(tool.InputJson);
                    item.Text = summary;
                }
                bg.SawTool = true;
                bg.Now = StepNames.Title(name);
                SaveBackground(bg);
                break;
            }
            case ToolResultEvent result:
                if (bg.Tools.TryGetValue(result.ToolUseId, out var step))
                {
                    step.State = result.IsError ? "failed" : "done";
                    if (result.Text.Length > 0) step.Result = result.Text.Length > MaxResult ? result.Text.Substring(0, MaxResult) + "…" : result.Text;
                    if (!result.IsError && FilesItem(step.Name, result.Text) is { } files) Add(files);
                }
                bg.Now = "Thinking";
                SaveBackground(bg);
                break;
            case NoticeEvent notice:
                if (notice.Text.Length > 0) Add(new ChatItem { Kind = "note", Text = notice.Text });
                break;
            case UsageEvent usage:
                OnUsage(usage.Usage);
                break;
            case ContextEvent context:
                conv.ContextFloor = ChatRecall.Floor(conv.ContextFloor, context.Tokens);
                break;
            case ResultEvent end:
            {
                if (end.SessionId.Length > 0) conv.SessionId = end.SessionId;
                var message = end.Text.Trim().Length > 0 ? end.Text.Trim() : string.Join(" ", end.Errors);
                if (end.IsError)
                    Add(new ChatItem { Kind = "error", Text = (message.Length > 0 ? message : "The chat reported an error.") + " Send your message again; the conversation is kept." });
                else if (!bg.SawText && message.Length > 0)
                    Add(new ChatItem { Kind = "assistant", Text = message });
                EndBackground(bg, end.IsError ? BackgroundChat.Failed : BackgroundChat.Done);
                break;
            }
        }
    }

    private void BackgroundExited(BackgroundChat bg, string tail)
    {
        if (bg.State != BackgroundChat.Working) return;
        var detail = Last(tail, 500);
        bg.Conv.Items.Add(new ChatItem
        {
            Kind = "error", Time = DateTime.UtcNow,
            Text = "The chat stopped unexpectedly while it worked in the background. Send your message again; the conversation is kept." + (detail.Length > 0 ? "\n\n" + detail : ""),
        });
        EndBackground(bg, BackgroundChat.Failed);
    }

    /// <summary>A background chat's turn ended: its steps end, it is saved, and the switcher (and a status line) say so.</summary>
    private void EndBackground(BackgroundChat bg, string state)
    {
        bg.State = state;
        bg.Ended = DateTime.UtcNow;
        bg.Now = "";
        foreach (var item in bg.Tools.Values.Where(i => i.State == "running")) item.State = "stopped";
        bg.Texts.Clear();
        bg.Tools.Clear();
        if (bg.OnEvent is not null) bg.Engine.Event -= bg.OnEvent;
        if (bg.OnExited is not null) bg.Engine.Exited -= bg.OnExited;
        bg.Engine.Dispose(); // opening it again resumes its session
        try { Revit.ChatModelWatch.Mark(bg.Conv.Key); } catch (Exception) { }
        SaveBackground(bg);
        ShowChats();
        if (_stopped) return;
        var name = ChatHistory.NameFor(bg.Conv) is { Length: > 0 } n ? n : ChatHistory.Untitled;
        if (_turn is null)
            Status(state == BackgroundChat.Done ? $"“{name}” finished in the background. Open it from Chats at the top."
                : $"“{name}” stopped with an error in the background. Open it from Chats at the top.", spin: false);
        ChatUi.FlashIfAway(this);
    }

    private void SaveBackground(BackgroundChat bg)
    {
        if (_sample || _handedOver) return;
        ChatStore.Save(bg.Conv);
    }

    /// <summary>Revit closes or Sketch ProMax updates: chats still working in the background stop, saying where.</summary>
    private void StopBackground(string note, bool now)
    {
        foreach (var bg in _background.ToList())
        {
            if (bg.State == BackgroundChat.Working)
            {
                if (bg.OnEvent is not null) bg.Engine.Event -= bg.OnEvent;
                if (bg.OnExited is not null) bg.Engine.Exited -= bg.OnExited;
                foreach (var item in bg.Tools.Values.Where(i => i.State == "running")) item.State = "stopped";
                bg.Conv.Items.Add(new ChatItem { Kind = "note", Text = note, Time = DateTime.UtcNow });
                bg.State = BackgroundChat.Stopped;
                if (now) bg.Engine.KillNow();
                else bg.Engine.Kill();
            }
            SaveBackground(bg);
        }
        _background.Clear();
    }

    /// <summary>A model never saved was saved: its chats in the background belong to the file now.</summary>
    private void RekeyBackground(string from, string to)
    {
        foreach (var bg in _background.Where(b => string.Equals(b.Conv.Key, from, StringComparison.OrdinalIgnoreCase)))
        {
            var old = bg.Conv.File;
            bg.Conv.Key = to;
            SaveBackground(bg);
            if (old.Length > 0 && !string.Equals(old, bg.Conv.File, StringComparison.OrdinalIgnoreCase)) ChatStore.Delete(old);
        }
    }

    /// <summary>What the history says next to a chat: working or finished in the background, or answering in the pane.</summary>
    private string? StateOf(string file)
    {
        if (BackgroundOf(file) is { } bg) return bg.Label;
        if (_turn is not null && string.Equals(file, _conv.File, StringComparison.OrdinalIgnoreCase)) return "Working now";
        return null;
    }

    // ---- The Chats switcher ---------------------------------------------------------------------------------------

    /// <summary>The Chats pill in the header: it says how many chats work or finished in the background, and opens the switcher.</summary>
    private void BuildChatsPill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_chatsLabel);
        var chevron = ChatUi.Icon(ChatUi.Paths.ChevronDown, 12);
        chevron.Margin = new Thickness(2, 0, 0, 0);
        row.Children.Add(chevron);
        _chatsPill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(ChatUi.S1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _chatsPill.SetResourceReference(StyleProperty, "SpmChatGhost");
        _chatsPill.Click += (_, _) => OpenSwitcher();
        AutomationProperties.SetItemStatus(_chatsPill, "Collapsed");
        ShowChats();
    }

    private void ShowChats()
    {
        if (_chatsPill is null) return;
        var working = _background.Count(b => b.State == BackgroundChat.Working);
        var finished = _background.Count - working;
        var parts = new List<string>();
        if (_narrow && working + finished > 0) parts.Add((working + finished).ToString(System.Globalization.CultureInfo.InvariantCulture));
        else
        {
            if (working > 0) parts.Add($"{working} working");
            if (finished > 0) parts.Add($"{finished} finished");
        }
        _chatsLabel.Text = "Chats" + (parts.Count > 0 ? " · " + string.Join(", ", parts) : "");
        Brand.Fg(_chatsLabel, parts.Count > 0 ? Brand.K.Accent : Brand.K.Muted);
        _chatsPill.ToolTip = parts.Count > 0
            ? "Switch chat. Chats you left keep working in the background; finished ones are marked until you open them."
            : "Switch chat: this model's recent chats. A chat you leave while it works keeps working in the background.";
        AutomationProperties.SetName(_chatsPill, _chatsLabel.Text);
        AutomationProperties.SetHelpText(_chatsPill, (string)_chatsPill.ToolTip);
    }

    /// <summary>The switcher: the chats in the background first, then this model's recent chats (read off the UI thread).</summary>
    private async void OpenSwitcher()
    {
        if (_choiceMenu.IsOpen || DateTime.UtcNow - _choiceClosed < TimeSpan.FromMilliseconds(250))
        {
            ToggleChoice(_chatsPill, new Choice("", []));
            return;
        }
        var key = ConversationKey;
        List<ChatSummary> recent;
        try { recent = await Task.Run(() => ChatStore.Summaries()); }
        catch (Exception) { recent = []; }
        if (_stopped) return;
        var rows = new List<(string Label, string Hint, bool Chosen, Action Pick)>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Row(string file, string name, string hint)
        {
            if (!listed.Add(file)) return;
            var current = string.Equals(file, _conv.File, StringComparison.OrdinalIgnoreCase) && _conv.File.Length > 0;
            rows.Add((name, hint, current, () => SwitchToChat(file)));
        }
        foreach (var bg in _background.OrderBy(b => b.State == BackgroundChat.Working ? 1 : 0).ThenByDescending(b => b.Ended))
            Row(bg.Conv.File, ChatHistory.NameFor(bg.Conv) is { Length: > 0 } n ? n : ChatHistory.Untitled, bg.Label + " · " + ChatTypes.Label(ChatTypes.Of(bg.Conv)));
        if (_conv.File.Length > 0)
            Row(_conv.File, ChatHistory.NameFor(_conv) is { Length: > 0 } name ? name : ChatHistory.Untitled,
                (_turn is not null ? "Open now · working" : "Open now") + " · " + ChatTypes.Label(ChatTypes.Of(_conv)));
        foreach (var chat in recent.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.Updated).Take(8))
            Row(chat.File, chat.Name, StreamRender.Stamp(chat.Updated.ToLocalTime(), DateTime.Now) + " · " + ChatTypes.Label(chat.Type));
        if (rows.Count == 0) rows.Add(("No other chats yet", "New chat (+) starts one; this one stays here", true, () => { }));
        ToggleChoice(_chatsPill, new Choice("Switch chat", rows));
    }

    /// <summary>Opens a chat by its file (from the switcher), as the history does.</summary>
    private void SwitchToChat(string file)
    {
        if (string.Equals(file, _conv.File, StringComparison.OrdinalIgnoreCase)) return;
        var summary = new ChatSummary(file, "", "", "", false, default, 0, "");
        ((IHistoryHost)this).OpenChat(summary);
    }
}
