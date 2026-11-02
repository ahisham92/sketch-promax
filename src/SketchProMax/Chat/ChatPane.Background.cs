using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The Chats switcher and the history's marks for chats left working in the background (ChatPane.Session): leaving a
/// chat while it answers, its jobs work or its Autopilot runs (New chat, or opening another chat) never stops it. Its
/// Claude Code and jobs carry on, what they do goes into its saved conversation, and its calls to Revit take turns with
/// the shown chat's like a parallel job's (JobBoard.Caller). The Chats switcher in the header and the history list say
/// which chats are working and which finished since you left them; opening one shows it as it is, still working.
/// </summary>
internal sealed partial class ChatPane
{
    private Button _chatsPill = null!;
    private readonly TextBlock _chatsLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>True while a chat left in the background still works.</summary>
    private bool BackgroundWorking => _away.Any(Working);

    /// <summary>Revit closes or Sketch ProMax updates: chats still working in the background stop, saying where.</summary>
    private void StopBackground(string note, string jobNote, bool now)
    {
        foreach (var s in _away.ToList())
        {
            In(s, () =>
            {
                if (_turn is { } turn)
                {
                    turn.KillTimer?.Stop();
                    turn.Cancel?.Cancel();
                    foreach (var item in s.Tools.Values.Where(i => i.State == "running")) item.State = "stopped";
                    _conv.Items.Add(new ChatItem { Kind = "note", Text = note, Time = DateTime.UtcNow });
                    _turn = null;
                }
                if (now) _engine?.KillNow();
                else _engine?.Kill();
                _engine = null;
                _reportTimer?.Stop();
                _jobClock?.Stop();
                EndRuns(jobNote, now);
                if (_boardChat.Length > 0) ChatJobs.Boards.Remove(_boardChat);
                Save();
            });
        }
        _away.Clear();
    }

    /// <summary>A model never saved was saved: its chats in the background belong to the file now.</summary>
    private void RekeyBackground(string from, string to)
    {
        foreach (var s in _away.Where(a => string.Equals(a.Conv.Key, from, StringComparison.OrdinalIgnoreCase)).ToList())
            In(s, () =>
            {
                var old = _conv.File;
                _conv.Key = to;
                Save();
                if (old.Length > 0 && !string.Equals(old, _conv.File, StringComparison.OrdinalIgnoreCase)) ChatStore.Delete(old);
            });
    }

    /// <summary>What the history says next to a chat: working or finished in the background, or answering in the pane.</summary>
    private string? StateOf(string file)
    {
        if (AwayOf(file) is { } away) return AwayLabel(away);
        if (_turn is not null && string.Equals(file, _conv.File, StringComparison.OrdinalIgnoreCase)) return "Working now";
        return null;
    }

    private static string AwayLabel(ChatSession s) => ChatWork.Label(Working(s), s.Now, s.Ended, s.Conv.Autopilot is { Active: true });

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
        var working = _away.Count(Working);
        var finished = _away.Count - working;
        _chatsLabel.Text = ChatWork.PillText(working, finished, _narrow);
        Brand.Fg(_chatsLabel, working + finished > 0 ? Brand.K.Accent : Brand.K.Muted);
        _chatsPill.ToolTip = working + finished > 0
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
        foreach (var away in _away.OrderBy(a => Working(a) ? 1 : 0).ThenByDescending(a => a.EndedAt))
            Row(away.Conv.File, ChatHistory.NameFor(away.Conv) is { Length: > 0 } n ? n : ChatHistory.Untitled, AwayLabel(away) + " · " + ChatTypes.Label(ChatTypes.Of(away.Conv)));
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
