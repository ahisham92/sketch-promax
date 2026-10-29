using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Chat;

namespace SketchProMax.UI;

/// <summary>
/// The Routines window (chat: New chat › Routines…): the routines the crew runs by itself at set times, each with its
/// schedule, when it runs next, an On switch, Run now, Edit, Delete and its runs (open one to see its job). The ready-made
/// ones are added in one click; Your own… asks for a name, what to do, the time and the days. Kept in routines.json.
/// </summary>
internal sealed class RoutinesWindow : Window
{
    private readonly string _folder, _model;
    private readonly StackPanel _list = new();
    private readonly StackPanel _editor = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted, size: 12);
    private readonly TextBox _name = new() { MinHeight = 26 }, _task = new() { MinHeight = 80, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap }, _time = new() { MinHeight = 26, Width = 70 };
    private readonly CheckBox[] _days = new CheckBox[7];
    private readonly CheckBox _onlyThis = new();
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText, size: 12);
    private string? _editing;

    /// <summary>Run now: the pane starts the routine; the reason it couldn't, or null.</summary>
    public event Func<string, string?>? RunNow;

    /// <summary>A run's Open: its chat and job number.</summary>
    public event Action<string, int>? OpenRun;

    public RoutinesWindow(string folder, string model)
    {
        _folder = folder;
        _model = model;
        Title = "Routines";
        Width = 640;
        Height = 640;
        MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        body.Children.Add(Brand.Text("The crew runs these by itself at their time, as a job in a chat With coordinator. A run missed while Revit or the PC was off runs once at the next Revit start. Each run is listed with what it found.", Brand.K.Muted, size: 12));
        _list.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(_list);
        var add = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var addLabel = Brand.Text("Add:", Brand.K.Muted);
        addLabel.VerticalAlignment = VerticalAlignment.Center;
        add.Children.Add(addLabel);
        foreach (var (name, task, time) in Routines.Presets)
        {
            var preset = Brand.Button(name);
            AutomationProperties.SetHelpText(preset, $"Adds \"{name}\", every day at {time}. Edit it after to change the time.");
            preset.Click += (_, _) => Add(name, task, time);
            add.Children.Add(preset);
        }
        var own = Brand.Button("Your own…");
        own.Click += (_, _) => Edit(null);
        add.Children.Add(own);
        body.Children.Add(add);
        BuildEditor();
        body.Children.Add(_editor);
        _status.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        body.Children.Add(_status);

        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var close = Brand.Button("Close", Brand.Weight.Primary);
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var foot = new StackPanel { Margin = new Thickness(18, 0, 18, 14) };
        foot.Children.Add(Brand.ButtonRow(close));
        var root = new DockPanel();
        var header = Brand.Header("Routines", "Work the crew does by itself at set times");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(scroll);
        Content = root;
        Fill();
    }

    private List<Routine> Load() => Routines.Load(_folder);

    private bool Save(List<Routine> list)
    {
        try
        {
            Routines.Save(_folder, list);
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = "Couldn't save the routines: " + ex.Message;
            return false;
        }
    }

    private void Fill()
    {
        _list.Children.Clear();
        var list = Load();
        if (list.Count == 0)
        {
            _list.Children.Add(Brand.Text("No routines yet. Add a ready-made one below, or your own.", Brand.K.Muted));
            return;
        }
        foreach (var routine in list) _list.Children.Add(Card(routine));
    }

    private FrameworkElement Card(Routine routine)
    {
        var stack = new StackPanel();
        var top = new DockPanel();
        var on = new CheckBox { IsChecked = routine.On, Content = "On", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(on, routine.Name + " on");
        on.Click += (_, _) => Change(routine.Id, r => r.On = on.IsChecked == true);
        DockPanel.SetDock(on, Dock.Right);
        top.Children.Add(on);
        top.Children.Add(Brand.Text(routine.Name, bold: true, size: 13));
        stack.Children.Add(top);
        var next = routine.On && Routines.Next(routine, DateTime.Now) is { } at ? " · next " + at.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture) : routine.On ? "" : " · off";
        stack.Children.Add(Brand.Text(Routines.Schedule(routine) + next, Brand.K.Muted, size: 12));
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var run = Brand.Button("Run now");
        run.Margin = new Thickness(0, 0, 8, 0);
        run.Click += (_, _) =>
        {
            var why = RunNow?.Invoke(routine.Id);
            _status.Text = why ?? $"\"{routine.Name}\" started: its job shows in the chat.";
            Fill();
        };
        var edit = Brand.Button("Edit");
        edit.Margin = new Thickness(0, 0, 8, 0);
        edit.Click += (_, _) => Edit(routine);
        var delete = Brand.Button("Delete", Brand.Weight.Danger);
        delete.Margin = new Thickness(0, 0, 8, 0);
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Delete the routine \"{routine.Name}\" and its history?", "Routines", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            var list = Load();
            list.RemoveAll(r => r.Id == routine.Id);
            if (Save(list)) _status.Text = $"\"{routine.Name}\" deleted.";
            Fill();
        };
        foreach (var b in new[] { run, edit, delete }) AutomationProperties.SetName(b, $"{b.Content} {routine.Name}");
        buttons.Children.Add(run);
        buttons.Children.Add(edit);
        buttons.Children.Add(delete);
        stack.Children.Add(buttons);
        if (routine.Runs.Count > 0)
        {
            var runs = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var r in routine.Runs.Take(10))
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                var open = new Button { Content = "Open", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
                open.SetResourceReference(StyleProperty, "SpmSecondaryButton");
                AutomationProperties.SetName(open, "Open the run of " + Routines.Line(r));
                var chat = r.ChatId;
                var job = r.Job;
                open.Click += (_, _) => OpenRun?.Invoke(chat, job);
                DockPanel.SetDock(open, Dock.Right);
                row.Children.Add(open);
                var line = new StackPanel();
                line.Children.Add(Brand.Text(Routines.Line(r), size: 12));
                if (r.Summary.Length > 0)
                {
                    var said = Brand.Text(JobBoard.OneLine(r.Summary, 220), Brand.K.Muted, size: 11);
                    said.TextWrapping = TextWrapping.Wrap;
                    said.ToolTip = r.Summary;
                    line.Children.Add(said);
                }
                row.Children.Add(line);
                runs.Children.Add(row);
            }
            var history = new Expander { Header = $"Runs ({routine.Runs.Count})", Content = runs, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(history, $"{routine.Name} runs");
            stack.Children.Add(history);
        }
        var card = new Border { Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        return card;
    }

    private void Change(string id, Action<Routine> change)
    {
        var list = Load();
        if (list.FirstOrDefault(r => r.Id == id) is not { } routine) return;
        change(routine);
        Save(list);
        Fill();
    }

    private void Add(string name, string task, string time)
    {
        var list = Load();
        var routine = new Routine { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Name = name, Task = task, Time = time, Created = DateTime.Now };
        list.Add(routine);
        if (Save(list)) _status.Text = $"\"{name}\" added: {Routines.Schedule(routine).ToLowerInvariant()}.";
        Fill();
    }

    private void BuildEditor()
    {
        _editor.Children.Add(Brand.Text("Routine", bold: true, size: 13));
        _editor.Children.Add(Label("Name"));
        AutomationProperties.SetName(_name, "Routine name");
        _editor.Children.Add(_name);
        _editor.Children.Add(Label("What to do (as you would ask in the chat)"));
        AutomationProperties.SetName(_task, "What the routine does");
        _editor.Children.Add(_task);
        var when = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var at = Brand.Text("At");
        at.VerticalAlignment = VerticalAlignment.Center;
        at.Margin = new Thickness(0, 0, 6, 0);
        when.Children.Add(at);
        AutomationProperties.SetName(_time, "Time of day, HH:mm");
        when.Children.Add(_time);
        var on = Brand.Text("on");
        on.VerticalAlignment = VerticalAlignment.Center;
        on.Margin = new Thickness(8, 0, 6, 0);
        when.Children.Add(on);
        // Monday first; DayOfWeek numbers (Sunday 0).
        for (var k = 0; k < 7; k++)
        {
            var day = (k + 1) % 7;
            _days[day] = new CheckBox { Content = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)day), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            when.Children.Add(_days[day]);
        }
        _editor.Children.Add(when);
        _editor.Children.Add(Brand.Text("No day ticked: every day.", Brand.K.Muted, size: 11));
        _onlyThis.Margin = new Thickness(0, 8, 0, 0);
        _onlyThis.Content = _model.Length > 0 ? $"Only on {_model}" : "Only on the model open now (open one first)";
        _onlyThis.IsEnabled = _model.Length > 0;
        _editor.Children.Add(_onlyThis);
        _error.TextWrapping = TextWrapping.Wrap;
        _error.Visibility = Visibility.Collapsed;
        _error.Margin = new Thickness(0, 6, 0, 0);
        _editor.Children.Add(_error);
        var save = Brand.Button("Save routine", Brand.Weight.Primary);
        save.Click += (_, _) => SaveEditor();
        var cancel = Brand.Button("Cancel");
        cancel.Click += (_, _) => _editor.Visibility = Visibility.Collapsed;
        _editor.Children.Add(Brand.ButtonRow(cancel, save));
    }

    private static TextBlock Label(string text)
    {
        var label = Brand.Text(text, Brand.K.Muted, size: 12);
        label.Margin = new Thickness(0, 8, 0, 2);
        return label;
    }

    private void Edit(Routine? routine)
    {
        _editing = routine?.Id;
        _name.Text = routine?.Name ?? "";
        _task.Text = routine?.Task ?? "";
        _time.Text = routine?.Time ?? "02:00";
        for (var d = 0; d < 7; d++) _days[d].IsChecked = routine?.Days.Contains(d) == true;
        _onlyThis.IsChecked = routine is { Model.Length: > 0 };
        if (routine is { Model.Length: > 0 } && !string.Equals(routine.Model, _model, StringComparison.OrdinalIgnoreCase))
        {
            _onlyThis.Content = $"Only on {routine.Model}";
            _onlyThis.IsEnabled = true;
        }
        _error.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Visible;
        _name.Focus();
        _editor.BringIntoView();
    }

    private void SaveEditor()
    {
        string? problem = _name.Text.Trim().Length == 0 ? "Give the routine a name."
            : _task.Text.Trim().Length == 0 ? "Say what the routine does."
            : Routines.ParseTime(_time.Text) is null ? "Type the time as hours and minutes, for example 02:00 or 18:30."
            : null;
        if (problem is not null)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
            return;
        }
        var list = Load();
        var routine = _editing is null ? null : list.FirstOrDefault(r => r.Id == _editing);
        if (routine is null)
        {
            routine = new Routine { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Created = DateTime.Now };
            list.Add(routine);
        }
        var model = _onlyThis.IsChecked == true ? (_onlyThis.Content as string ?? "").Replace("Only on ", "") : "";
        routine.Name = _name.Text.Trim();
        routine.Task = _task.Text.Trim();
        routine.Time = Routines.ParseTime(_time.Text)!.Value.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        routine.Days = Enumerable.Range(0, 7).Where(d => _days[d].IsChecked == true).ToList();
        routine.Model = model;
        // A time already gone today waits for its next turn, rather than running the moment it is saved.
        if (Routines.LastScheduled(routine, DateTime.Now) is { } gone && gone > routine.LastDue) routine.LastDue = gone;
        if (!Save(list)) return;
        _editor.Visibility = Visibility.Collapsed;
        _status.Text = $"\"{routine.Name}\" saved: {Routines.Schedule(routine).ToLowerInvariant()}.";
        Fill();
    }
}
