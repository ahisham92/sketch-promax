using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Tools;

namespace SketchProMax.UI;

/// <summary>
/// The by-hand rates behind "time saved", from Settings: minutes a person would take per run and per item, for each
/// tool. Built-in rates are labelled "estimate"; a changed one becomes "yours". Save (the one primary action) hands the
/// changed rates back to Settings, which stores them with its own Save. Reset to estimates (red) drops all of yours.
/// </summary>
internal sealed class TimeRatesWindow : Window
{
    private readonly List<(TimeRate Rate, TextBox Run, TextBox Item, TextBlock Source)> _rows = [];
    private readonly StackPanel _list = new();
    private readonly TextBox _search = new() { MinHeight = 28 };
    private readonly TextBox _newTool = new() { MinHeight = 28 };
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText);

    /// <summary>The rates that differ from the built-in estimates, once Save was pressed.</summary>
    public List<TimeRate>? Result { get; private set; }

    public TimeRatesWindow(IEnumerable<TimeRate>? current)
    {
        Title = "By-hand rates";
        Width = 600;
        Height = 620;
        MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);

        var top = new StackPanel { Margin = new Thickness(18, 14, 18, 6) };
        top.Children.Add(Brand.Text("Minutes a person would take for each run of a tool, plus for each item it handles (a sheet, a tag, a bar). " +
                                    "Time saved is this estimate minus the time the task took. Built-in rates are estimates: replace them with your office's figures.", Brand.K.Muted));
        var searchLabel = Brand.Text("Find a tool", bold: true);
        searchLabel.Margin = new Thickness(0, 8, 0, 2);
        top.Children.Add(searchLabel);
        AutomationProperties.SetName(_search, "Find a tool");
        _search.TextChanged += (_, _) => Filter();
        top.Children.Add(_search);

        foreach (var rate in TimeMath.RateTable(current)) AddRow(rate);
        var scroll = new ScrollViewer
        {
            Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(18, 0, 18, 0),
        };

        var foot = new StackPanel { Margin = new Thickness(18, 8, 18, 14) };
        var addRow = new DockPanel();
        var add = Brand.Button("Add tool");
        add.Click += (_, _) => AddTool();
        DockPanel.SetDock(add, Dock.Right);
        addRow.Children.Add(add);
        AutomationProperties.SetName(_newTool, "Tool name to add");
        AutomationProperties.SetHelpText(_newTool, "The name of a tool without a rate, such as a learned tool. It is added with zero minutes to fill in.");
        addRow.Children.Add(_newTool);
        foot.Children.Add(Brand.Text("Add a tool by its name (a learned tool, say):", Brand.K.Muted, size: 11));
        foot.Children.Add(addRow);
        _error.Visibility = Visibility.Collapsed;
        _error.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        foot.Children.Add(_error);
        var reset = Brand.Button("Reset to estimates", Brand.Weight.Danger);
        AutomationProperties.SetHelpText(reset, "Drops all your own rates and goes back to the built-in estimates. Takes effect when you save.");
        reset.Click += (_, _) => Reset();
        var cancel = Brand.Button("Cancel");
        cancel.IsCancel = true;
        var save = Brand.Button("Save", Brand.Weight.Primary);
        save.IsDefault = true;
        save.Click += (_, _) => Save();
        foot.Children.Add(Brand.ButtonRow(reset, cancel, save));

        var root = new DockPanel();
        var header = Brand.Header("By-hand rates", "How long the work would take without Sketch ProMax.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(scroll);
        Content = root;
        Loaded += (_, _) => _search.Focus();
    }

    private void AddRow(TimeRate rate)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Tag = rate.Tool };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        var name = Brand.Text(rate.Tool);
        name.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);
        var run = Box(rate.PerRun, $"{rate.Tool}: minutes per run");
        var item = Box(rate.PerItem, $"{rate.Tool}: minutes per item");
        var source = Brand.Text(rate.Estimate ? "estimate" : "yours", rate.Estimate ? Brand.K.Muted : Brand.K.Text, size: 11);
        source.VerticalAlignment = VerticalAlignment.Center;
        source.Margin = new Thickness(8, 0, 0, 0);
        void Changed() => source.Text = Same(rate, run, item) ? (rate.Estimate ? "estimate" : "yours") : "yours";
        run.TextChanged += (_, _) => Changed();
        item.TextChanged += (_, _) => Changed();
        Grid.SetColumn(run, 1);
        Grid.SetColumn(item, 2);
        Grid.SetColumn(source, 3);
        grid.Children.Add(run);
        grid.Children.Add(item);
        grid.Children.Add(source);
        if (_list.Children.Count == 0) _list.Children.Add(HeaderRow());
        _list.Children.Add(grid);
        _rows.Add((rate, run, item, source));
    }

    private static Grid HeaderRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(78), new GridLength(78), new GridLength(70) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        string[] titles = ["Tool", "Min / run", "Min / item", ""];
        for (var i = 0; i < titles.Length; i++)
        {
            var t = Brand.Text(titles[i], Brand.K.Muted, size: 11, bold: true);
            if (i > 0) t.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(t, i);
            grid.Children.Add(t);
        }
        return grid;
    }

    private static TextBox Box(double value, string name)
    {
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.CurrentCulture), MinHeight = 26, Margin = new Thickness(4, 0, 0, 0), TextAlignment = TextAlignment.Right };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static bool Same(TimeRate rate, TextBox run, TextBox item) =>
        Parse(run.Text) is { } r && Parse(item.Text) is { } i && Math.Abs(r - rate.PerRun) < 1e-9 && Math.Abs(i - rate.PerItem) < 1e-9;

    private static double? Parse(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return 0;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return v >= 0 && v <= 100_000 ? v : null;
        return null;
    }

    private void Filter()
    {
        var q = _search.Text.Trim();
        foreach (var child in _list.Children.OfType<Grid>())
            if (child.Tag is string tool)
                child.Visibility = q.Length == 0 || tool.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddTool()
    {
        var name = _newTool.Text.Trim();
        if (name.Length == 0) { ShowError("Type the tool's name first."); _newTool.Focus(); return; }
        if (_rows.FirstOrDefault(r => string.Equals(r.Rate.Tool, name, StringComparison.OrdinalIgnoreCase)) is { Rate: not null } existing)
        {
            _search.Text = existing.Rate.Tool;
            existing.Run.Focus();
            return;
        }
        AddRow(new TimeRate(name, 0, 0, Estimate: false));
        _newTool.Clear();
        _search.Text = name;
        _rows[_rows.Count - 1].Run.Focus();
    }

    private void Reset()
    {
        foreach (var (rate, run, item, _) in _rows)
        {
            var d = TimeMath.Defaults.TryGetValue(rate.Tool, out var v) ? v : (0, 0);
            run.Text = d.Item1.ToString("0.##", CultureInfo.CurrentCulture);
            item.Text = d.Item2.ToString("0.##", CultureInfo.CurrentCulture);
        }
        ShowError("");
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Save()
    {
        var table = new List<TimeRate>();
        foreach (var (rate, run, item, _) in _rows)
        {
            if (Parse(run.Text) is not { } r || Parse(item.Text) is not { } i)
            {
                _search.Text = rate.Tool;
                ShowError($"{rate.Tool}: type minutes as a number of 0 or more (e.g. 1.5), then save again.");
                (Parse(run.Text) is null ? run : item).Focus();
                return;
            }
            table.Add(new TimeRate(rate.Tool, r, i));
        }
        Result = TimeMath.Overrides(table);
        DialogResult = true;
    }
}
