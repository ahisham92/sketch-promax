using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The Notes tab of the Sketch ProMax pane (CoreEntry.PaneContent("notes")): what the user asked Claude to remember,
/// for every model and for the open model, to read, edit, delete or add by hand. Edits are saved when a note's box
/// loses focus or on Enter. The list is re-read every 2 s while visible, unless a box is being edited, so notes Claude
/// saves from the chat show up by themselves.
/// </summary>
internal sealed class NotesView : DockPanel
{
    private static NotesView? _instance;

    public static FrameworkElement Get() => _instance ??= new NotesView();

    /// <summary>The core is stopping: let go of the theme and the timer; the next core makes its own view.</summary>
    public static void Stop()
    {
        if (_instance is not { } view) return;
        view._timer.Stop();
        ChatPane.RecallChanged -= view.OnRecallChanged;
        Brand.ReleaseTheme(view);
        _instance = null;
    }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly StackPanel _global = new(), _model = new(), _recall = new();
    private readonly TextBlock _modelHeading, _recallHeading, _status;
    private int _recallVersion;
    private readonly TextBox _new;
    private readonly ComboBox _scope;
    private string _shown = "";

    private NotesView()
    {
        Brand.UseTheme(this);
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        SetResourceReference(TextElement.ForegroundProperty, Brand.K.Text);
        TextElement.SetFontSize(this, 12);

        var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        body.Children.Add(Brand.Text("What you asked Sketch ProMax to remember in the chat (\"remember that …\"). It gets these at the start " +
                                     "of each chat: the ones for all models, and the ones for the model it is about.", Brand.K.Muted, 11));
        body.Children.Add(Heading("For all models"));
        body.Children.Add(_global);
        _modelHeading = Heading("For this model");
        body.Children.Add(_modelHeading);
        body.Children.Add(_model);
        _recallHeading = Heading("From earlier chats about this model");
        body.Children.Add(_recallHeading);
        body.Children.Add(Brand.Text("What each chat remembered of itself. A new chat about this model starts with the latest three, and " +
                                     "Sketch ProMax can search all of them, for every model, when you say \"the same way as in …\".", Brand.K.Muted, 11));
        body.Children.Add(_recall);

        // Add a note by hand.
        body.Children.Add(Heading("Add a note"));
        _new = new TextBox { TextWrapping = TextWrapping.Wrap, MinHeight = 28, AcceptsReturn = false, SpellCheck = { IsEnabled = true } };
        AutomationProperties.SetName(_new, "New note");
        AutomationProperties.SetHelpText(_new, "Type a note and press Enter or Add note.");
        _new.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AddNote();
        };
        body.Children.Add(_new);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var add = Brand.Button("Add note", Brand.Weight.Primary);
        add.MinHeight = 28;
        add.Click += (_, _) => AddNote();
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        _scope = new ComboBox { MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        _scope.Items.Add("For this model");
        _scope.Items.Add("For all models");
        _scope.SelectedIndex = 0;
        AutomationProperties.SetName(_scope, "Who the new note is for");
        row.Children.Add(_scope);
        body.Children.Add(row);

        _status = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
        _status.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        body.Children.Add(_status);

        var open = Brand.Button("Open the notes file");
        open.Margin = new Thickness(0, 10, 0, 0);
        open.MinHeight = 28;
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.ToolTip = MemoryNotes.File + "\nEvery model's notes, as plain Markdown.";
        open.Click += (_, _) => OpenFile();
        body.Children.Add(open);

        Children.Add(new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        });

        _timer.Tick += (_, _) => Refresh();
        ChatPane.RecallChanged += OnRecallChanged;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) { Refresh(force: true); _timer.Start(); }
            else _timer.Stop();
        };
    }

    private static string ModelKey => ChatPane.CurrentModelKey;

    private static TextBlock Heading(string text)
    {
        var heading = Brand.Text(text.ToUpperInvariant(), Brand.K.Muted, ChatUi.SmallSize, bold: true);
        heading.Margin = new Thickness(0, 12, 0, 4);
        return heading;
    }

    private void Refresh(bool force = false)
    {
        // Never pull a note out from under the user's typing.
        if (!force && IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox box && !ReferenceEquals(box, _new)) return;
        DateTime stamp;
        try { stamp = File.Exists(MemoryNotes.File) ? File.GetLastWriteTimeUtc(MemoryNotes.File) : default; }
        catch (Exception) { stamp = default; }
        var key = ModelKey + "\u0001" + stamp.Ticks + "\u0001" + _recallVersion;
        if (!force && key == _shown) return;
        _shown = key;
        var (global, model) = MemoryNotes.For(MemoryNotes.Load(), ModelKey);
        Fill(_global, null, global, "Nothing yet. Say \"remember that …\" in the chat, or add one below.");
        var hasModel = ModelKey.Length > 0;
        _modelHeading.Text = (hasModel ? "For this model: " + ChatPane.CurrentModelTitle : "For this model (none open)").ToUpperInvariant();
        Fill(_model, hasModel ? ModelKey : null, hasModel ? model : [], hasModel ? "Nothing for this model yet." : "Open a model to see its notes.", hasModel);
        if (!hasModel) _scope.SelectedIndex = 1;
        FillRecall();
    }

    private void OnRecallChanged()
    {
        _recallVersion++;
        if (IsVisible) Refresh();
    }

    /// <summary>The open model's chats that remembered something, newest first, to read, edit or delete.</summary>
    private void FillRecall()
    {
        _recall.Children.Clear();
        List<Conversation> chats;
        try { chats = ChatStore.ForModel(ChatPane.CurrentChatKey).Where(c => c.Recall.Length > 0).Take(10).ToList(); }
        catch (Exception) { chats = []; }
        if (chats.Count == 0)
        {
            _recall.Children.Add(Brand.Text("Nothing yet. A chat writes its summary when you shrink it, leave it for another chat, or leave it idle.", Brand.K.Muted));
            return;
        }
        foreach (var chat in chats)
        {
            var file = chat.File;
            var title = Brand.Text(chat.Name + " · " + chat.Updated.ToLocalTime().ToString("d MMM yyyy"), Brand.K.Text, ChatUi.SmallSize, bold: true);
            title.Margin = new Thickness(0, 4, 0, 2);
            _recall.Children.Add(title);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var delete = Brand.Button("Delete", Brand.Weight.Danger);
            delete.MinWidth = 0;
            delete.MinHeight = 28;
            delete.VerticalAlignment = VerticalAlignment.Top;
            delete.ToolTip = "Deletes what this chat remembered. New chats stop getting it; the chat itself stays in the history.";
            AutomationProperties.SetName(delete, $"Delete what the chat {chat.Name} remembered");
            var text = new TextBox { Text = chat.Recall, TextWrapping = TextWrapping.Wrap, MinHeight = 28, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(text, $"What the chat {chat.Name} remembered");
            AutomationProperties.SetHelpText(text, "Edit it; it is saved when you leave the box.");
            var original = chat.Recall;
            text.LostKeyboardFocus += (_, _) =>
            {
                var now = text.Text.Replace("\r", "").Trim();
                if (now == original) return;
                SaveRecall(file, now, now.Length == 0 ? "Deleted." : "Saved.");
                original = now;
            };
            text.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                text.Text = original;
            };
            delete.Click += (_, _) => SaveRecall(file, "", "Deleted.");
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            row.Children.Add(text);
            _recall.Children.Add(row);
        }
    }

    private void SaveRecall(string file, string recall, string done)
    {
        try
        {
            _status.Text = ChatPane.ChangeSaved(file, c => c.Recall = recall) ? done : "That chat couldn't be found any more.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"It could not be saved: {ex.Message}";
        }
        _recallVersion++;
        Refresh(force: true);
    }

    private void Fill(StackPanel panel, string? key, List<string> notes, string empty, bool enabled = true)
    {
        panel.Children.Clear();
        if (notes.Count == 0)
        {
            panel.Children.Add(Brand.Text(empty, Brand.K.Muted));
            return;
        }
        if (!enabled) return;
        var n = 0;
        foreach (var note in notes)
        {
            n++;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var delete = Brand.Button("Delete", Brand.Weight.Danger);
            delete.MinWidth = 0;
            delete.MinHeight = 28;
            delete.VerticalAlignment = VerticalAlignment.Top;
            delete.ToolTip = "Deletes this note. Sketch ProMax stops getting it from its next chat.";
            AutomationProperties.SetName(delete, $"Delete note {n}: {note}");
            var text = new TextBox { Text = note, TextWrapping = TextWrapping.Wrap, MinHeight = 28, AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(text, $"Note {n}");
            AutomationProperties.SetHelpText(text, "Edit the note; it is saved when you press Enter or leave the box.");
            var original = note;
            void Commit()
            {
                var now = MemoryNotes.Clean(text.Text);
                if (now == original) return;
                Save(s => MemoryNotes.Replace(s, key, original, now), now.Length == 0 ? "Note deleted." : "Note saved.");
                original = now;
            }
            text.LostKeyboardFocus += (_, _) => Commit();
            text.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
                else if (e.Key == Key.Escape) { e.Handled = true; text.Text = original; }
            };
            delete.Click += (_, _) => Save(s => MemoryNotes.Remove(s, key, original), "Note deleted.", force: true);
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
    }

    private void AddNote()
    {
        var note = MemoryNotes.Clean(_new.Text);
        if (note.Length == 0)
        {
            _status.Text = "Type the note first, then press Add note.";
            _new.Focus();
            return;
        }
        var global = _scope.SelectedIndex == 1 || ModelKey.Length == 0;
        if (Save(s => MemoryNotes.Add(s, global ? null : ModelKey, note), global ? "Added for all models." : "Added for this model.", force: true))
            _new.Text = "";
    }

    private bool Save(Func<List<MemoryNotes.Section>, bool> change, string done, bool force = false)
    {
        try
        {
            var changed = MemoryNotes.Change(change);
            _status.Text = changed ? done : "Nothing changed (the note was already there, or was changed elsewhere).";
            Refresh(force: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = $"The notes could not be saved: {ex.Message} Try again; if it keeps failing, open the notes file.";
            if (force) Refresh(force: true);
            return false;
        }
    }

    private void OpenFile()
    {
        try
        {
            if (!File.Exists(MemoryNotes.File)) MemoryNotes.Change(_ => true);
            Process.Start(new ProcessStartInfo(MemoryNotes.File) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status.Text = $"Couldn't open {MemoryNotes.File}: {ex.Message}";
        }
    }
}
