using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>What the history drawer asks of the chat pane.</summary>
internal interface IHistoryHost
{
    /// <summary>The open model's key: its chats are listed first (or only).</summary>
    string ModelKey { get; }
    /// <summary>The file of the chat shown in the pane ("" before it is saved).</summary>
    string CurrentFile { get; }
    void OpenChat(ChatSummary chat);
    void NewChat();
    /// <summary>Renames or pins a chat; the pane applies it to the chat it shows, so its next save keeps the change.</summary>
    void ChangeChat(string file, Action<Conversation> change);
    /// <summary>Deletes a chat (the pane starts a new one when it was the one shown). False when it couldn't.</summary>
    bool DeleteChat(string file);
    void CloseDrawer();
}

/// <summary>
/// The chat history drawer: search, the chats grouped as Pinned, Last 7 days, Last 30 days and Older, the open model's
/// chats only (or all models, the open model's first), and per chat a ⋯ that shows Rename, Pin, Export and Delete.
/// Reading the saved chats happens off the UI thread.
/// </summary>
internal sealed class HistoryDrawer : Border
{
    private readonly IHistoryHost _host;
    private readonly TextBox _search = new() { MinHeight = 28, Padding = new Thickness(ChatUi.S2, 4, ChatUi.S2, 4) };
    private readonly CheckBox _all = new() { Content = "All models", Margin = new Thickness(0, ChatUi.S2, 0, 0) };
    private readonly StackPanel _list = new();
    private readonly TextBlock _state = ChatUi.Small("");
    private List<ChatSummary> _chats = [];
    private string? _openActions;
    private int _loads;

    public HistoryDrawer(IHistoryHost host)
    {
        _host = host;
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        SetResourceReference(BorderBrushProperty, Brand.K.Border);
        BorderThickness = new Thickness(0, 0, 1, 0);
        AutomationProperties.SetName(this, "Chat history");

        var top = new DockPanel { Margin = new Thickness(ChatUi.S2, ChatUi.S2, ChatUi.S2, ChatUi.S2) };
        var close = ChatUi.Ghost(ChatUi.GlyphCross, null, "Close chat history", "Close (Esc)");
        close.Click += (_, _) => _host.CloseDrawer();
        DockPanel.SetDock(close, Dock.Left);
        top.Children.Add(close);
        var add = ChatUi.Ghost(ChatUi.GlyphAdd, "New", "New chat", "Starts a new chat; this one stays in the history.");
        add.Click += (_, _) => _host.NewChat();
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        var refresh = ChatUi.Ghost(ChatUi.GlyphRefresh, null, "Refresh the list", "Reads the saved chats again.");
        refresh.Click += (_, _) => Refresh();
        DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(refresh);
        var title = Brand.Text("Chats", Brand.K.Text, 13, bold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        top.Children.Add(title);

        var filters = new StackPanel { Margin = new Thickness(ChatUi.S3, 0, ChatUi.S3, ChatUi.S2) };
        var hint = Brand.Text("Search chats", Brand.K.Muted, 12);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(ChatUi.S2 + 3, 6, 0, 0);
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Draw();
        };
        AutomationProperties.SetName(_search, "Search chats");
        AutomationProperties.SetHelpText(_search, "Finds chats by name, model or what was said. Esc closes the history.");
        var field = new Grid();
        field.Children.Add(_search);
        field.Children.Add(hint);
        filters.Children.Add(field);
        _all.ToolTip = "Lists the chats of every model on this PC, the open model's first.";
        AutomationProperties.SetHelpText(_all, (string)_all.ToolTip);
        _all.Checked += (_, _) => Draw();
        _all.Unchecked += (_, _) => Draw();
        filters.Children.Add(_all);
        _state.Margin = new Thickness(0, ChatUi.S1, 0, 0);
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);
        filters.Children.Add(_state);

        var scroll = new ScrollViewer
        {
            Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(ChatUi.S2, 0, ChatUi.S2, ChatUi.S2), Focusable = false,
        };
        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(filters, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(filters);
        panel.Children.Add(scroll);
        Child = panel;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.OriginalSource is TextBox { Tag: "rename" }) return;
            e.Handled = true;
            _host.CloseDrawer();
        };
    }

    /// <summary>Puts the keyboard in the search box.</summary>
    public void FocusSearch() => Dispatcher.BeginInvoke(new Action(() => _search.Focus()), System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>Reads the saved chats again (off the UI thread) and redraws the list.</summary>
    public void Refresh()
    {
        var load = ++_loads;
        _state.Text = "Reading the saved chats…";
        Task.Run(ChatStore.Summaries).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (load != _loads) return;
            _chats = t.Status == TaskStatus.RanToCompletion ? t.Result : [];
            Draw();
        })), TaskScheduler.Default);
    }

    private void Draw()
    {
        _list.Children.Clear();
        var sections = ChatHistory.Arrange(_chats, _host.ModelKey, _all.IsChecked == true, _search.Text, DateTime.Now);
        var count = sections.Sum(s => s.Chats.Count);
        _state.Text = count == 0
            ? _search.Text.Trim().Length > 0 ? "No chat matches. Try other words" + (_all.IsChecked == true ? "." : ", or tick All models.")
            : _all.IsChecked == true ? "No saved chats yet." : "No saved chats for this model yet. Tick All models to see the others."
            : count == 1 ? "1 chat" : $"{count} chats";
        foreach (var (group, chats) in sections)
        {
            var header = ChatUi.Small(group, Brand.K.Muted, bold: true);
            header.Margin = new Thickness(ChatUi.S1, ChatUi.S3, 0, ChatUi.S1);
            _list.Children.Add(header);
            foreach (var chat in chats) _list.Children.Add(Row(chat));
        }
    }

    private UIElement Row(ChatSummary chat)
    {
        var current = string.Equals(chat.File, _host.CurrentFile, StringComparison.OrdinalIgnoreCase);
        var mine = string.Equals(chat.Key, _host.ModelKey, StringComparison.OrdinalIgnoreCase);
        var text = new StackPanel();
        var name = ChatUi.Small((chat.Pinned ? "★ " : "") + chat.Name, Brand.K.Text, bold: current);
        name.FontSize = 12;
        text.Children.Add(name);
        var sub = StreamRender.Stamp(chat.Updated.ToLocalTime(), DateTime.Now) + (!mine || _all.IsChecked == true ? " · " + chat.ModelTitle : "");
        text.Children.Add(ChatUi.Small(sub));
        var open = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(ChatUi.S2, 4, ChatUi.S2, 4) };
        open.SetResourceReference(StyleProperty, "SpmChatGhost");
        open.ToolTip = chat.Name + "\n" + chat.ModelTitle + "\n" + $"{chat.Count} messages and steps";
        AutomationProperties.SetName(open, $"{chat.Name}, {sub}{(current ? ", open now" : "")}{(chat.Pinned ? ", pinned" : "")}");
        AutomationProperties.SetHelpText(open, "Opens this chat; Claude resumes it with what was said.");
        open.Click += (_, _) => _host.OpenChat(chat);

        var more = ChatUi.Ghost(ChatUi.GlyphMore, null, "More actions for " + chat.Name, "Rename, pin, export or delete");
        more.VerticalAlignment = VerticalAlignment.Center;
        var line = new DockPanel();
        DockPanel.SetDock(more, Dock.Right);
        line.Children.Add(more);
        line.Children.Add(open);

        var actions = new WrapPanel { Margin = new Thickness(ChatUi.S2, 0, 0, ChatUi.S1), Visibility = _openActions == chat.File ? Visibility.Visible : Visibility.Collapsed };
        more.Click += (_, _) =>
        {
            _openActions = actions.Visibility == Visibility.Visible ? null : chat.File;
            Draw();
        };
        AutomationProperties.SetItemStatus(more, actions.Visibility == Visibility.Visible ? "Expanded" : "Collapsed");
        Button Act(string glyph, string label, string tip, Action click)
        {
            var b = ChatUi.Ghost(glyph, label, label + " " + chat.Name, tip);
            b.Click += (_, _) => click();
            actions.Children.Add(b);
            return b;
        }
        Act(ChatUi.GlyphRename, "Rename", "Gives the chat another name.", () => Rename(chat, actions));
        Act(ChatUi.GlyphPin, chat.Pinned ? "Unpin" : "Pin", chat.Pinned ? "Takes the chat out of Pinned." : "Keeps the chat at the top, under Pinned.", () =>
        {
            _host.ChangeChat(chat.File, c => c.Pinned = !chat.Pinned);
            Refresh();
        });
        Act(ChatUi.GlyphSave, "Export", "Saves the chat as a Markdown file.", () => Export(chat));
        var delete = Act(ChatUi.GlyphDelete, "Delete", "Deletes the chat from this PC. It can't be undone.", () => Delete(chat));
        Brand.Fg(delete, Brand.K.DangerText);

        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        row.Children.Add(line);
        row.Children.Add(actions);
        var frame = new Border { Child = row, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(3, 0, 0, 0) };
        if (current)
        {
            frame.SetResourceReference(BackgroundProperty, Brand.K.Surface);
            frame.SetResourceReference(BorderBrushProperty, Brand.K.Accent);
        }
        else frame.BorderBrush = System.Windows.Media.Brushes.Transparent;
        return frame;
    }

    private void Rename(ChatSummary chat, WrapPanel actions)
    {
        actions.Children.Clear();
        var box = new TextBox { Text = chat.Name, MinWidth = 160, Tag = "rename", Margin = new Thickness(0, 0, ChatUi.S1, 0) };
        AutomationProperties.SetName(box, "New name for the chat");
        AutomationProperties.SetHelpText(box, "Enter saves, Esc cancels.");
        void Commit()
        {
            var name = box.Text.Trim();
            if (name.Length > 0 && name != chat.Name) _host.ChangeChat(chat.File, c => c.Name = name.Length > 80 ? name.Substring(0, 80) : name);
            _openActions = null;
            Refresh();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _openActions = null; Draw(); }
        };
        actions.Children.Add(box);
        var save = Brand.Button("Save", Brand.Weight.Primary);
        save.MinWidth = 0;
        save.Margin = new Thickness(0);
        save.Click += (_, _) => Commit();
        actions.Children.Add(save);
        box.Focus();
        box.SelectAll();
    }

    private void Export(ChatSummary chat)
    {
        if (ChatStore.Read(chat.File) is not { } conv)
        {
            _state.Text = "That chat couldn't be read. Press Refresh and try again.";
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the chat", Filter = "Markdown|*.md|Text|*.txt", DefaultExt = ".md",
            FileName = string.Concat(chat.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('…', '.', ' ') + ".md",
        };
        var owner = Window.GetWindow(this);
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, Tools.ProjectPack.RenderChat(conv), new UTF8Encoding(false));
            _state.Text = "Exported to " + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _state.Text = "The chat couldn't be saved there: " + ex.Message;
        }
    }

    private void Delete(ChatSummary chat)
    {
        var dialog = new Autodesk.Revit.UI.TaskDialog("Sketch ProMax")
        {
            MainInstruction = $"Delete the chat \"{chat.Name}\"?",
            MainContent = "It is removed from this PC and can't be opened again. Your model is not changed.",
            CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Yes | Autodesk.Revit.UI.TaskDialogCommonButtons.No,
            DefaultButton = Autodesk.Revit.UI.TaskDialogResult.No,
        };
        if (dialog.Show() != Autodesk.Revit.UI.TaskDialogResult.Yes) return;
        _state.Text = _host.DeleteChat(chat.File) ? "Deleted." : "The chat couldn't be deleted (in use?). Try again.";
        _openActions = null;
        Refresh();
    }
}
