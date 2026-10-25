using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using static SketchProMax.Tools.BerthStatusMath;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// The submissions schedule, changed in Revit (Submissions status: Setup > Edit the schedule): one row per submission
/// with its model, berth (empty: every berth of the model; a package of berths 9 and 10 is its own berth row), name,
/// start, due date and time, and Submitted. Resubmit adds the next revision after comments (T3 Rev 1) under the row.
/// Nothing is kept until Save; Submissions status then writes the setup Excel too, so both stay the same.
/// </summary>
internal sealed class SubmissionScheduleWindow : Window
{
    private sealed class Line
    {
        public ComboBox Model { get; } = Editable(150);
        public ComboBox Berth { get; } = Editable(150);
        public TextBox Name { get; } = new() { Width = 190, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        public DatePicker Start { get; } = new() { Width = 128 };
        public DatePicker Due { get; } = new() { Width = 128 };
        public TextBox Time { get; } = new() { Width = 56, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        public CheckBox Submitted { get; } = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    }

    private const string EveryBerth = "(every berth)";
    private readonly Setup _setup;
    private readonly List<Line> _lines = [];
    private readonly StackPanel _rows = new();
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText, 12);
    private bool _changed;

    /// <summary>The schedule after Save; null when cancelled.</summary>
    public List<Submission>? Saved { get; private set; }

    public static List<Submission>? Edit(Window owner, Setup setup)
    {
        var window = new SubmissionScheduleWindow(setup) { Owner = owner };
        window.ShowDialog();
        return window.Saved;
    }

    private static ComboBox Editable(double width) => new() { Width = width, IsEditable = true, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };

    private SubmissionScheduleWindow(Setup setup)
    {
        _setup = setup;
        Title = "Sketch ProMax: submissions schedule";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1180, Math.Floor(area.Width * 0.9));
        Height = Math.Min(720, Math.Floor(area.Height * 0.9));
        MinWidth = Math.Min(900, Width);
        MinHeight = Math.Min(420, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));
        foreach (var s in setup.Submissions.OrderBy(s => s.Date == NoDate ? DateTime.MinValue : DueAt(s, setup.Week))) AddLine(s, -1);
        Content = Layout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { AddLine(null, -1, focus: true); e.Handled = true; }
        };
        Closing += (_, e) =>
        {
            if (Saved is not null || !_changed) return;
            if (MessageBox.Show(this, "Close without saving the schedule changes?", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }

    private FrameworkElement Layout()
    {
        var header = Brand.Header("Submissions schedule",
            "Add, re-date or remove submissions. A resubmission after comments is its own row (Resubmit adds T3 Rev 1). Start: when work on it begins (empty: when the one before was due).");
        DockPanel.SetDock(header, Dock.Top);

        var heads = new Grid { Margin = new Thickness(18, 12, 18, 4) };
        string[] names = ["MODEL", "BERTH", "SUBMISSION", "START", "DUE", "TIME", "SUBMITTED", ""];
        double[] widths = [158, 158, 198, 136, 136, 64, 80, 220];
        for (var i = 0; i < names.Length; i++)
        {
            heads.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i]) });
            var t = Brand.Text(names[i], Brand.K.Muted, 10.5, bold: true);
            Grid.SetColumn(t, i);
            heads.Children.Add(t);
        }
        DockPanel.SetDock(heads, Dock.Top);

        _error.TextWrapping = TextWrapping.Wrap;
        _error.Visibility = Visibility.Collapsed;
        _error.Margin = new Thickness(18, 8, 18, 0);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        var add = RichUi.Button(RichUi.Icon.Add, "Add a submission");
        add.Margin = new Thickness(0);
        AutomationProperties.SetHelpText(add, "Adds an empty row at the end (Ctrl+N).");
        add.Click += (_, _) => AddLine(null, -1, focus: true);
        var cancel = RichUi.Button(RichUi.Icon.Close, "Cancel");
        cancel.IsCancel = true;
        var save = RichUi.Button(RichUi.Icon.Save, "Save", Brand.Weight.Primary);
        AutomationProperties.SetHelpText(save, "Keeps the schedule (Ctrl+S) and writes it into the setup Excel too.");
        save.Click += (_, _) => Save();
        var right = Brand.ButtonRow(cancel, save);
        right.Margin = new Thickness(0);
        var footGrid = new Grid();
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footGrid.Children.Add(add);
        Grid.SetColumn(right, 1);
        footGrid.Children.Add(right);
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 12), Margin = new Thickness(0, 8, 0, 0), Child = footGrid };
        foot.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        var bottom = new StackPanel { Children = { _error, foot } };
        DockPanel.SetDock(bottom, Dock.Bottom);

        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 0, 18, 0), Focusable = false };
        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(bottom);
        root.Children.Add(heads);
        root.Children.Add(scroll);
        root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
        return root;
    }

    /// <summary>Adds a row for <paramref name="s"/> (null: an empty one) at <paramref name="at"/> (-1: the end).</summary>
    private void AddLine(Submission? s, int at, bool focus = false)
    {
        var line = new Line();
        foreach (var m in _setup.Models) line.Model.Items.Add(m.Name);
        line.Model.Text = s?.Model ?? (_setup.Models.Count == 1 ? _setup.Models[0].Name : "");
        void Berths()
        {
            var keep = line.Berth.Text;
            line.Berth.Items.Clear();
            line.Berth.Items.Add(EveryBerth);
            foreach (var b in _setup.Berths.Where(b => line.Model.Text.Trim().Length == 0 || Same(b.Model, line.Model.Text))) line.Berth.Items.Add(b.Name);
            line.Berth.Text = keep;
        }
        Berths();
        line.Model.LostFocus += (_, _) => Berths();
        line.Model.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Berths));
        line.Berth.Text = s is null ? "" : s.Berth.Trim().Length == 0 ? EveryBerth : s.Berth;
        line.Name.Text = s?.Name ?? "";
        line.Start.SelectedDate = s?.Start;
        line.Due.SelectedDate = s is null || s.Date == NoDate ? null : s.Date;
        line.Time.Text = s?.Time is { } t ? t.ToString(@"hh\:mm", CultureInfo.InvariantCulture) : "";
        line.Submitted.IsChecked = s?.Submitted == true;
        AutomationProperties.SetName(line.Model, "Model");
        AutomationProperties.SetName(line.Berth, "Berth or package (every berth: empty)");
        AutomationProperties.SetName(line.Name, "Submission name");
        AutomationProperties.SetName(line.Start, "Start date");
        AutomationProperties.SetName(line.Due, "Due date");
        AutomationProperties.SetName(line.Time, "Due time (empty: the usual due time)");
        AutomationProperties.SetName(line.Submitted, "Submitted");
        line.Time.ToolTip = $"Due time, e.g. 08:00 (empty: {_setup.Week.Due:hh\\:mm})";
        void Touched() => _changed = true;
        line.Name.TextChanged += (_, _) => Touched();
        line.Start.SelectedDateChanged += (_, _) => Touched();
        line.Due.SelectedDateChanged += (_, _) => Touched();
        line.Time.TextChanged += (_, _) => Touched();
        line.Submitted.Click += (_, _) => Touched();
        line.Berth.LostFocus += (_, _) => Touched();

        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        double[] widths = [158, 158, 198, 136, 136, 64, 80, 220];
        foreach (var w in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        UIElement[] cells = [line.Model, line.Berth, line.Name, line.Start, line.Due, line.Time, line.Submitted];
        for (var i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); grid.Children.Add(cells[i]); }
        var resubmit = new Button { Content = "Resubmit", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetHelpText(resubmit, "Adds the next revision of this submission after comments under it (same berth, T3 Rev 1), starting today: set its due date.");
        resubmit.Click += (_, _) =>
        {
            if (Read(line) is not { } current) return;
            var index = _lines.IndexOf(line);
            var next = Resubmission(current, DateTime.Today, line.Due.SelectedDate ?? DateTime.Today);
            AddLine(next with { Date = NoDate }, index + 1, focus: true);
            var added = _lines[index + 1];
            added.Start.SelectedDate = DateTime.Today;
            added.Due.Focus();
            _changed = true;
        };
        var delete = new Button { Content = "Delete", Padding = new Thickness(8, 2, 8, 2) };
        AutomationProperties.SetHelpText(delete, "Removes this submission from the schedule (its checklist, if any, is kept).");
        delete.Click += (_, _) =>
        {
            var index = _lines.IndexOf(line);
            _lines.RemoveAt(index);
            _rows.Children.RemoveAt(index);
            _changed = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { resubmit, delete } };
        Grid.SetColumn(buttons, 7);
        grid.Children.Add(buttons);
        foreach (var c in cells.OfType<FrameworkElement>()) { c.Margin = new Thickness(0, 0, 8, 0); c.VerticalAlignment = VerticalAlignment.Center; }

        if (at < 0 || at >= _lines.Count) { _lines.Add(line); _rows.Children.Add(grid); }
        else { _lines.Insert(at, line); _rows.Children.Insert(at, grid); }
        if (focus) Dispatcher.BeginInvoke(new Action(() => line.Name.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>A row as a submission (null when its time can't be read: the error says so).</summary>
    private Submission? Read(Line line)
    {
        TimeSpan? time = null;
        if (line.Time.Text.Trim().Length > 0)
        {
            if (ParseTime(line.Time.Text) is not { } t) { Error($"{line.Name.Text.Trim()}: \"{line.Time.Text.Trim()}\" isn't a time (e.g. 08:00)."); line.Time.Focus(); return null; }
            time = t;
        }
        var berth = line.Berth.Text.Trim();
        return new Submission(line.Model.Text.Trim(), berth == EveryBerth ? "" : berth, line.Name.Text.Trim(), line.Due.SelectedDate?.Date ?? NoDate, time, line.Submitted.IsChecked == true, line.Start.SelectedDate?.Date);
    }

    private void Error(string text)
    {
        _error.Text = text;
        _error.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save()
    {
        var rows = new List<Submission>();
        foreach (var line in _lines)
        {
            if (Read(line) is not { } s) return;
            if (s.Name.Length == 0 && s.Date == NoDate && s.Berth.Length == 0 && !s.Submitted) continue;
            rows.Add(s);
        }
        if (ScheduleProblems(rows) is { Count: > 0 } problems) { Error(string.Join(" ", problems.Take(4)) + (problems.Count > 4 ? $" (and {problems.Count - 4} more)" : "")); return; }
        Saved = rows;
        Close();
    }
}
