using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SketchProMax.Tools;
using static SketchProMax.Tools.SubmissionChecklistTemplate;
using Tpl = SketchProMax.Tools.SubmissionChecklistTemplate.Template;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// The submission checklist's questions template, for its owner only (the submission checklist shows the button on the
/// owner's PC alone): every question on the left, by page, and the chosen one on the right (page, question, details,
/// structure types, teams, sketch). Add, delete, export to Excel and import it back, or go back to the built-in
/// questions. Nothing is kept until Save; the checklist then asks the new questions and carries them in the model.
/// </summary>
internal sealed class SubmissionChecklistTemplateWindow : Window
{
    private sealed class Row : INotifyPropertyChanged
    {
        private string _label = "";
        public Item Item { get; init; } = new();
        public string Label { get => _label; set { _label = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<Item> _items;
    private readonly string _user;
    private readonly ObservableCollection<Row> _rows = [];
    private readonly ListBox _list = new();
    private readonly ComboBox _filter = new() { MinHeight = 28 };
    private readonly TextBox _search = new() { MinHeight = 28, Margin = new Thickness(0, 6, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _page = new() { MinHeight = 28, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _text = Box(), _details = Box(multi: true), _teams = Box();
    private readonly WrapPanel _types = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly ComboBox _sketch = new() { MinHeight = 28, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ContentControl _preview = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _id = Brand.Text("", Brand.K.Muted, 11);
    private readonly TextBlock _count = Brand.Text("", Brand.K.Muted, 12);
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText, 12);
    private readonly StackPanel _form = new();
    private readonly TextBlock _none = Brand.Text("Pick a question on the left, or add one.", Brand.K.Muted, 12);
    private bool _loading, _changed;

    /// <summary>The template after Save (the built-in one after Use the built-in questions); null when cancelled.</summary>
    public Tpl? Saved { get; private set; }

    /// <summary>Opens the editor over <paramref name="owner"/> on <paramref name="current"/> (null: the built-in questions).</summary>
    public static Tpl? Edit(Window owner, Tpl? current, string user)
    {
        var window = new SubmissionChecklistTemplateWindow(current ?? BuiltIn(), user) { Owner = owner };
        window.ShowDialog();
        return window.Saved;
    }

    private SubmissionChecklistTemplateWindow(Tpl current, string user)
    {
        _items = Copy(current).Questions;
        _user = user;
        Title = "Sketch ProMax: submission checklist questions";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1180, Math.Floor(area.Width * 0.9));
        Height = Math.Min(780, Math.Floor(area.Height * 0.9));
        MinWidth = Math.Min(820, Width);
        MinHeight = Math.Min(520, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);
        if (ChecklistEditor.ListStyles() is { } styles) Resources.MergedDictionaries.Add(styles);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));
        Content = Layout(current);
        Fill(select: _items.FirstOrDefault());
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Loaded += (_, _) => _list.Focus();
    }

    private static TextBox Box(bool multi = false) => new()
    {
        MinHeight = multi ? 72 : 28, TextWrapping = TextWrapping.Wrap, AcceptsReturn = multi,
        VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden, MaxHeight = multi ? 160 : 60,
        VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), MaxLength = 2000,
    };

    private static TextBlock Label(string text)
    {
        var t = Brand.Text(text, Brand.K.Muted, 12);
        t.Margin = new Thickness(0, 10, 0, 0);
        return t;
    }

    // ---- Layout ---------------------------------------------------------------------------------------------------

    private FrameworkElement Layout(Tpl current)
    {
        var header = Brand.Header("Questions template",
            current.BuiltIn ? "The questions every project's submission checklist starts from (the built-in ones). Only you can change them."
                : $"The questions every project's submission checklist starts from, changed {current.At.ToLocalTime():dd MMM yyyy HH:mm}. Only you can change them.");
        DockPanel.SetDock(header, Dock.Top);

        // Left: the questions, by page, with a search.
        _filter.Items.Add(new ComboBoxItem { Content = "Every page", Tag = "" });
        foreach (var t in SubmissionChecklistMath.Topics) _filter.Items.Add(new ComboBoxItem { Content = t.Title, Tag = t.Key });
        _filter.SelectedIndex = 0;
        AutomationProperties.SetName(_filter, "Show the questions of");
        _filter.SelectionChanged += (_, _) => { if (!_loading) Fill(select: Current); };
        AutomationProperties.SetName(_search, "Search the questions");
        _search.ToolTip = "Search: type a few words of the question or its details.";
        _search.TextChanged += (_, _) => { if (!_loading) Fill(select: Current); };
        _list.ItemsSource = _rows;
        _list.DisplayMemberPath = nameof(Row.Label);
        AutomationProperties.SetName(_list, "Questions");
        _list.SelectionChanged += (_, _) => { if (!_loading) Show(); };
        var add = RichUi.Button(RichUi.Icon.Add, "Add a question");
        add.Margin = new Thickness(0, 0, 6, 6);
        add.Click += (_, _) => AddQuestion();
        var delete = RichUi.Button(RichUi.Icon.Delete, "Delete", Brand.Weight.Danger);
        delete.Margin = new Thickness(0, 0, 6, 6);
        AutomationProperties.SetHelpText(delete, "Deletes the chosen question from the template (Delete key). Answers already given to it stay in the models but are no longer shown.");
        delete.Click += (_, _) => DeleteQuestion();
        var left = new DockPanel { Margin = new Thickness(18, 14, 9, 0) };
        var leftTop = new StackPanel { Children = { _filter, _search } };
        DockPanel.SetDock(leftTop, Dock.Top);
        left.Children.Add(leftTop);
        var leftBottom = new StackPanel { Children = { new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { add, delete } }, _count } };
        DockPanel.SetDock(leftBottom, Dock.Bottom);
        left.Children.Add(leftBottom);
        _list.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(_list);

        // Right: the chosen question.
        foreach (var t in SubmissionChecklistMath.Topics) _page.Items.Add(new ComboBoxItem { Content = t.Title, Tag = t.Key });
        AutomationProperties.SetName(_page, "Page");
        _page.SelectionChanged += (_, _) => { if (_loading || Current is not { } i || _page.SelectedItem is not ComboBoxItem { Tag: string k }) return; i.Topic = k; Edited(i); };
        void Bind(TextBox box, string name, Action<Item, string> set)
        {
            AutomationProperties.SetName(box, name);
            box.TextChanged += (_, _) => { if (_loading || Current is not { } i) return; set(i, box.Text); Edited(i); };
        }
        Bind(_text, "Question", (i, v) => i.Text = v);
        Bind(_details, "Details", (i, v) => i.Details = v);
        Bind(_teams, "Agree with (teams)", (i, v) => i.Teams = v);
        foreach (var t in SubmissionChecklistMath.Types)
        {
            var box = new CheckBox { Content = t.Name, Tag = t.Key, Margin = new Thickness(0, 4, 14, 0), ToolTip = t.About };
            box.Click += (_, _) => { if (Current is { } i) { i.Types = string.Join(",", _types.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag)); Edited(i); } };
            _types.Children.Add(box);
        }
        _sketch.Items.Add(new ComboBoxItem { Content = "Its structure's own", Tag = "" });
        foreach (var d in SubmissionChecklistDiagrams.All) _sketch.Items.Add(new ComboBoxItem { Content = d.Title, Tag = d.Key });
        AutomationProperties.SetName(_sketch, "Sketch");
        _sketch.SelectionChanged += (_, _) => { if (_loading || Current is not { } i || _sketch.SelectedItem is not ComboBoxItem { Tag: string k }) return; i.Diagram = k; Edited(i); };
        _form.Children.Add(Label("Page"));
        _form.Children.Add(_page);
        _form.Children.Add(Label("Question (short, ends with ?)"));
        _form.Children.Add(_text);
        _form.Children.Add(Label("Details (shown when the engineer clicks Details)"));
        _form.Children.Add(_details);
        _form.Children.Add(Label("Structure types (none: every project; Structure checks needs one)"));
        _form.Children.Add(_types);
        _form.Children.Add(Label("Agree with (teams, comma separated, e.g. Grading, Marine)"));
        _form.Children.Add(_teams);
        _form.Children.Add(Label("Sketch"));
        _form.Children.Add(_sketch);
        _form.Children.Add(_preview);
        _id.Margin = new Thickness(0, 10, 0, 0);
        _form.Children.Add(_id);
        var right = new ScrollViewer
        {
            Content = new StackPanel { Children = { _none, _form } }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(9, 14, 18, 0), Focusable = false,
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 300 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.Children.Add(left);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        // Footer: Excel and the built-in questions on the left, Cancel / Save on the right.
        _error.TextWrapping = TextWrapping.Wrap;
        _error.Visibility = Visibility.Collapsed;
        _error.Margin = new Thickness(18, 8, 18, 0);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        var export = RichUi.Button(RichUi.Icon.Export, "Export to Excel…");
        export.Margin = new Thickness(0);
        AutomationProperties.SetHelpText(export, "Saves the questions as they are here as an Excel file, to edit in Excel and import back.");
        export.Click += (_, _) => Export();
        var import = RichUi.Button(RichUi.Icon.Import, "Import from Excel…");
        AutomationProperties.SetHelpText(import, "Replaces the questions here with an Excel file (as Export writes it). Nothing is kept until you Save.");
        import.Click += (_, _) => Import();
        var reset = RichUi.Button(RichUi.Icon.Reset, "Use the built-in questions", Brand.Weight.Danger);
        AutomationProperties.SetHelpText(reset, "Drops your changes and goes back to the questions that come with Sketch ProMax.");
        reset.Click += (_, _) => ResetToBuiltIn();
        var cancel = RichUi.Button(RichUi.Icon.Close, "Cancel");
        cancel.IsCancel = true;
        var save = RichUi.Button(RichUi.Icon.Save, "Save", Brand.Weight.Primary);
        save.Click += (_, _) => Save();
        var leftButtons = new WrapPanel { Children = { export, import, reset }, VerticalAlignment = VerticalAlignment.Center };
        var rightButtons = Brand.ButtonRow(cancel, save);
        rightButtons.Margin = new Thickness(0);
        var footGrid = new Grid();
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footGrid.Children.Add(leftButtons);
        Grid.SetColumn(rightButtons, 1);
        footGrid.Children.Add(rightButtons);
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 12), Margin = new Thickness(0, 12, 0, 0), Child = footGrid };
        foot.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        var bottom = new StackPanel { Children = { _error, foot } };
        DockPanel.SetDock(bottom, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(bottom);
        root.Children.Add(body);
        root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
        return root;
    }

    // ---- The list -------------------------------------------------------------------------------------------------

    private Item? Current => _list.SelectedItem is Row r ? r.Item : null;

    private string LabelOf(Item i)
    {
        var number = _items.IndexOf(i) + 1;
        var page = SubmissionChecklistMath.TopicOf(i.Topic).Title;
        var text = i.Text.Trim().Length > 0 ? i.Text.Trim() : "(no question yet)";
        return _filter.SelectedItem is ComboBoxItem { Tag: string { Length: > 0 } } ? $"{number}. {text}" : $"{number}. {text}   ·  {page}";
    }

    /// <summary>The rows for the page and search shown, with <paramref name="select"/> chosen when it is among them.</summary>
    private void Fill(Item? select)
    {
        _loading = true;
        var page = _filter.SelectedItem is ComboBoxItem { Tag: string k } ? k : "";
        var words = _search.Text.Trim();
        _rows.Clear();
        foreach (var i in _items)
        {
            if (page.Length > 0 && i.Topic != page) continue;
            if (words.Length > 0 && (i.Text + " " + i.Details + " " + i.Teams).IndexOf(words, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _rows.Add(new Row { Item = i, Label = LabelOf(i) });
        }
        _list.SelectedItem = _rows.FirstOrDefault(r => r.Item == select) ?? _rows.FirstOrDefault();
        _count.Text = _rows.Count == _items.Count ? $"{_items.Count} questions" : $"{_rows.Count} of {_items.Count} questions shown";
        _loading = false;
        Show();
    }

    private void Show()
    {
        _loading = true;
        var i = Current;
        _form.Visibility = i is null ? Visibility.Collapsed : Visibility.Visible;
        _none.Visibility = i is null ? Visibility.Visible : Visibility.Collapsed;
        if (i is not null)
        {
            _page.SelectedItem = _page.Items.OfType<ComboBoxItem>().FirstOrDefault(c => (string)c.Tag == SubmissionChecklistMath.TopicOf(i.Topic).Key);
            _text.Text = i.Text;
            _details.Text = i.Details;
            _teams.Text = i.Teams;
            var keys = SubmissionChecklistMath.Split(i.Types);
            foreach (var box in _types.Children.OfType<CheckBox>()) box.IsChecked = keys.Contains((string)box.Tag);
            _sketch.SelectedItem = _sketch.Items.OfType<ComboBoxItem>().FirstOrDefault(c => (string)c.Tag == i.Diagram) ?? _sketch.Items[0];
            _id.Text = "Id " + i.Id + " (answers are kept by it)";
            ShowSketch(i);
        }
        _loading = false;
    }

    private void ShowSketch(Item i)
    {
        var q = new SubmissionChecklistMath.Question(i.Id, i.Topic, i.Text, i.Details, i.Types, i.Teams, i.Diagram);
        _preview.Content = SubmissionChecklistDiagrams.For(q, SubmissionChecklistMath.Split(i.Types)) is { } d ? SubmissionChecklistDiagramView.View(d, 360) : null;
    }

    private void Edited(Item i)
    {
        _changed = true;
        if (_list.SelectedItem is Row r) r.Label = LabelOf(i);
        ShowSketch(i);
        _error.Visibility = Visibility.Collapsed;
    }

    private void AddQuestion()
    {
        var page = _filter.SelectedItem is ComboBoxItem { Tag: string { Length: > 0 } k } ? k : Current?.Topic ?? SubmissionChecklistMath.Topics[0].Key;
        var item = new Item { Id = NewId(DateTime.UtcNow, _items.Select(x => x.Id)), Topic = page };
        if (page == SubmissionChecklistMath.TopicTypes && Current is { } near) item.Types = SubmissionChecklistMath.Split(near.Types).FirstOrDefault() ?? "";
        // After the chosen question, or at the end of its page.
        var at = Current is { } c && c.Topic == page ? _items.IndexOf(c) + 1 : _items.FindLastIndex(x => x.Topic == page) + 1;
        if (at <= 0) at = _items.Count;
        _items.Insert(at, item);
        _changed = true;
        _search.Text = "";
        Fill(select: item);
        _text.Focus();
    }

    private void DeleteQuestion()
    {
        if (Current is not { } i) return;
        var index = _list.SelectedIndex;
        _items.Remove(i);
        _changed = true;
        Fill(select: null);
        if (_rows.Count > 0) _list.SelectedIndex = Math.Min(index, _rows.Count - 1);
        _list.Focus();
    }

    // ---- Save, Excel ----------------------------------------------------------------------------------------------

    /// <summary>The template as it stands, or null with the reason shown when a question isn't complete.</summary>
    private Tpl? Build()
    {
        foreach (var i in _items)
        {
            if (i.Text.Trim().Length == 0) return Fail(i, "A question is empty: type it, or delete it.");
            if (i.Topic == SubmissionChecklistMath.TopicTypes && SubmissionChecklistMath.Split(i.Types).Count == 0)
                return Fail(i, "A question on Structure checks needs at least one structure type.");
        }
        if (_items.Count == 0) { ShowError("Keep at least one question, or use the built-in questions."); return null; }
        return new Tpl
        {
            At = DateTime.UtcNow, By = _user,
            Questions = _items.Select(i => new Item
            {
                Id = i.Id, Topic = i.Topic, Text = i.Text.Trim().EndsWith("?", StringComparison.Ordinal) ? i.Text.Trim() : i.Text.Trim() + "?",
                Details = i.Details.Trim(), Types = i.Types, Teams = i.Teams.Trim(), Diagram = i.Diagram,
            }).ToList(),
        };
    }

    private Tpl? Fail(Item i, string why)
    {
        _filter.SelectedIndex = 0;
        _search.Text = "";
        Fill(select: i);
        ShowError(why);
        return null;
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.Visibility = Visibility.Visible;
    }

    private void Save()
    {
        if (Build() is not { } template) return;
        if (SavePc(template) is { } why) { ShowError($"Couldn't save the questions on this PC ({why}). Try again."); return; }
        Saved = template;
        _changed = false;
        DialogResult = true;
    }

    private void Export()
    {
        if (Build() is not { } template) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the submission checklist questions to Excel", Filter = "Excel workbook|*.xlsx",
            FileName = "Submission checklist questions.xlsx", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            OfficeDocs.WriteFile(dialog.FileName, s => WriteXlsx(s, template, DateTime.Now));
            _error.Visibility = Visibility.Collapsed;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError($"Couldn't write the Excel file ({ex.Message}). If it is open in Excel, close it and try again.");
        }
    }

    private void Import()
    {
        if (_changed && MessageBox.Show(this, "Replace the questions here with the Excel file? Your changes here that aren't saved are lost.", "Sketch ProMax",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import the submission checklist questions from Excel", Filter = "Excel workbooks|*.xlsx|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var (template, notes, error) = ReadXlsx(dialog.FileName, DateTime.UtcNow, _user);
        if (template is null) { ShowError("Couldn't read the questions: " + error + "."); return; }
        _items.Clear();
        _items.AddRange(template.Questions);
        _changed = true;
        _filter.SelectedIndex = 0;
        _search.Text = "";
        Fill(select: _items.FirstOrDefault());
        if (notes.Count > 0) ShowError($"Imported {_items.Count} questions. Set aside: " + string.Join("; ", notes.Take(6)) + (notes.Count > 6 ? $"; and {notes.Count - 6} more" : "") + ". Save to keep them.");
        else { _error.Visibility = Visibility.Collapsed; }
    }

    private void ResetToBuiltIn()
    {
        if (MessageBox.Show(this, "Go back to the questions that come with Sketch ProMax? Your questions are removed from this PC (Excel files you exported are kept). " +
                                  "Models keep the answers already given.", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        // A newer template than any in a model: the built-in questions, saved with the time, so they win over the old one everywhere.
        var builtIn = BuiltIn();
        builtIn.At = DateTime.UtcNow;
        builtIn.By = _user;
        if (SavePc(builtIn) is { } why) { ShowError($"Couldn't save on this PC ({why}). Try again."); return; }
        Saved = builtIn;
        _changed = false;
        DialogResult = true;
    }

    // ---- Keys and closing -----------------------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Save(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { AddQuestion(); e.Handled = true; }
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is ListBoxItem or ListBox) { DeleteQuestion(); e.Handled = true; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Saved is not null || !_changed) return;
        if (MessageBox.Show(this, "Close without saving your changes to the questions?", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            != MessageBoxResult.Yes) e.Cancel = true;
    }
}
