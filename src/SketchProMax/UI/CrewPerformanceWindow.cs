using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Chat;

namespace SketchProMax.UI;

/// <summary>
/// The Team button's window (crew strip): how the chat's team performs across every chat, for today, the last 7 days or
/// all time. Cards for the time the workers used, the time saved by working side by side, the tokens and the dollars
/// (what the same work would cost on the API; the plan covers it); then each worker (jobs, done, minutes per job, tokens,
/// dollars), the coordinator (turns, jobs handed out, tokens, dollars) and the applier (changes into Revit). Read when
/// shown and when the period changes.
/// </summary>
internal sealed class CrewPerformanceWindow : Window
{
    private readonly StackPanel _body = new() { Margin = new Thickness(18, 14, 18, 14) };
    private readonly ComboBox _period = new() { MinWidth = 140 };

    public CrewPerformanceWindow()
    {
        Title = "Team performance";
        Width = 700;
        Height = 620;
        MinWidth = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        foreach (var label in new[] { "Today", "Last 7 days", "All time" }) _period.Items.Add(new ComboBoxItem { Content = label });
        _period.SelectedIndex = 0;
        AutomationProperties.SetName(_period, "Period");
        _period.SelectionChanged += (_, _) => Fill();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 10, 18, 0) };
        var label2 = Brand.Text("Period", Brand.K.Muted);
        label2.VerticalAlignment = VerticalAlignment.Center;
        label2.Margin = new Thickness(0, 0, 8, 0);
        top.Children.Add(label2);
        top.Children.Add(_period);

        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true, IsTabStop = true };
        AutomationProperties.SetName(scroll, "Team performance");
        var close = Brand.Button("Close", Brand.Weight.Primary);
        close.IsCancel = true;
        close.IsDefault = true;
        close.Click += (_, _) => Close();
        var foot = new StackPanel { Margin = new Thickness(18, 0, 18, 14) };
        foot.Children.Add(Brand.ButtonRow(close));

        var root = new DockPanel();
        var header = Brand.Header("Team performance", "Your chat's workers, coordinator and applier, across every chat");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(scroll);
        Content = root;
        Fill();
        CrewStats.Changed += Refresh;
        Closed += (_, _) => CrewStats.Changed -= Refresh;
        Loaded += (_, _) => _period.Focus();
    }

    private void Refresh() => Dispatcher.BeginInvoke(new Action(Fill));

    private void Fill()
    {
        DateTime? from = _period.SelectedIndex switch { 0 => DateTime.Today.ToUniversalTime(), 1 => DateTime.Today.AddDays(-6).ToUniversalTime(), _ => null };
        var records = CrewStats.Since(CrewStats.All(), from);
        var names = CrewStrip.Names;
        _body.Children.Clear();
        if (records.Count == 0)
        {
            _body.Children.Add(Brand.Text("Nothing counted for this period yet. Work in a chat With coordinator and the team's numbers show here.", Brand.K.Muted));
            return;
        }
        var (used, saved) = CrewStats.Time(records);
        var workers = CrewStats.Workers(records);
        var coordinator = CrewStats.CoordinatorKpi(records);
        var applier = CrewStats.ApplierKpi(records);
        var tokens = records.Sum(r => r.In + r.Out);
        var usd = records.Sum(r => r.Usd);
        var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        cards.Children.Add(Card("Workers' time", CrewStats.Duration(used), "All the workers' working time added up."));
        cards.Children.Add(Card("Saved by working together", CrewStats.Duration(saved), "Time the workers' parallel work saved over one worker doing it all in turn."));
        cards.Children.Add(Card("Tokens", CrewStats.Tokens(tokens), "Tokens read and written by the whole team."));
        cards.Children.Add(Card("API-equivalent cost", CrewStats.Dollars(usd), "What the same work would cost on Anthropic's API, as Claude Code reports it. Your plan covers it."));
        _body.Children.Add(cards);

        _body.Children.Add(Heading("Workers"));
        _body.Children.Add(Table("Workers", ["Worker", "Jobs", "Done", "Min / job", "Tokens in", "Tokens out", "Cost"],
            workers.Select(k => new[]
            {
                (k.Slot == CrewStats.Leader(records) ? "★ " : "") + names.Worker(k.Slot), k.Jobs.ToString(), k.Done.ToString(), k.MinutesPerJob.ToString("0.0"),
                CrewStats.Tokens(k.In), CrewStats.Tokens(k.Out), CrewStats.Dollars(k.Usd),
            })));
        _body.Children.Add(Heading($"{names.Coordinator} (coordinator)"));
        var together = used <= 0 ? 0 : used / Math.Max(1, used - saved);
        _body.Children.Add(Table("Coordinator", ["", "Turns", "Jobs handed out", "Workers at once", "Tokens", "Cost"],
            [[names.Coordinator, coordinator.Turns.ToString(), coordinator.JobsHandedOut.ToString(), together.ToString("0.0"), CrewStats.Tokens(coordinator.In + coordinator.Out), CrewStats.Dollars(coordinator.Usd)]]));
        _body.Children.Add(Heading($"{names.Applier} (applies changes in Revit)"));
        _body.Children.Add(Table("Applier", ["", "Changes applied", "Time in Revit", "Per change"],
            [[names.Applier, applier.Changes.ToString(), CrewStats.Duration(applier.Seconds), applier.Changes == 0 ? "-" : CrewStats.Duration(applier.Seconds / applier.Changes)]]));
        var note = Brand.Text("Workers at once: how many worked side by side on average while any of them worked. Costs are the API equivalent Claude Code reports; on your plan you pay the plan, not these.", Brand.K.Muted, size: 11);
        note.Margin = new Thickness(0, 12, 0, 0);
        _body.Children.Add(note);
    }

    private static FrameworkElement Card(string label, string value, string help)
    {
        var stack = new StackPanel();
        stack.Children.Add(Brand.Text(label, Brand.K.Muted, size: 11));
        stack.Children.Add(Brand.Text(value, size: 18, bold: true));
        var card = new Border { Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8), MinWidth = 150, CornerRadius = new CornerRadius(6), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        card.ToolTip = help;
        AutomationProperties.SetName(card, $"{label}: {value}. {help}");
        return card;
    }

    private static TextBlock Heading(string text)
    {
        var heading = Brand.Text(text, bold: true, size: 13);
        heading.Margin = new Thickness(0, 10, 0, 4);
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
        return heading;
    }

    /// <summary>A read-only table: the first column takes the spare width, the others fit their text.</summary>
    private static FrameworkElement Table(string name, string[] headers, IEnumerable<string[]> rows)
    {
        var grid = new Grid();
        AutomationProperties.SetName(grid, name);
        for (var c = 0; c < headers.Length; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void Row(string[] cells, bool header)
        {
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < cells.Length && c < headers.Length; c++)
            {
                var text = Brand.Text(cells[c], header ? Brand.K.Muted : Brand.K.Text, size: 12, bold: header);
                text.Margin = new Thickness(c == 0 ? 0 : 12, 2, 0, 2);
                if (c > 0) { text.TextAlignment = TextAlignment.Right; text.TextWrapping = TextWrapping.NoWrap; }
                Grid.SetRow(text, r);
                Grid.SetColumn(text, c);
                grid.Children.Add(text);
            }
        }
        Row(headers, true);
        foreach (var row in rows) Row(row, false);
        return grid;
    }
}
