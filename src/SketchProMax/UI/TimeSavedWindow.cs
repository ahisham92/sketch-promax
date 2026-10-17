using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using SketchProMax.Revit;
using SketchProMax.Tools;

namespace SketchProMax.UI;

/// <summary>
/// The Time saved button's window, for the active model: working time, AI time versus by hand, time saved and an
/// estimate of the work fully by hand, then totals per tool, per day and per user. Export to Excel (the one primary
/// action) writes the same with a user column. The data is read when the window opens.
/// </summary>
internal sealed class TimeSavedWindow : Window
{
    private readonly TimeReport _report;
    private readonly TextBlock _status = Brand.Text("");
    private readonly Button _export = Brand.Button("Export to Excel", Brand.Weight.Primary);

    public static Result Command(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            TaskDialog.Show("Sketch ProMax", "Open a project model first: time is counted per model.");
            return Result.Cancelled;
        }
        TimeReport report;
        try { report = TimeLog.Report(doc); }
        catch (Exception ex) // whatever it is, a message here rather than Revit's error dialog
        {
            TaskDialog.Show("Sketch ProMax", "The time record of this model couldn't be read: " + ex.Message);
            return Result.Failed;
        }
        new TimeSavedWindow(report).Show();
        return Result.Succeeded;
    }

    public TimeSavedWindow(TimeReport report)
    {
        _report = report;
        Title = "Time saved";
        Width = 640;
        Height = 640;
        MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        var t = report.Totals;
        var empty = report.Sheets.All(s => s.Rows.Count == 0);
        if (!TimeLog.On)
            body.Children.Add(Notice("Counting is off (Settings). What is shown was counted before it was turned off."));
        if (empty)
            body.Children.Add(Brand.Text("Nothing is counted in this model yet. Run a Sketch ProMax tool or a chat task, or work in the model for a few minutes, then open this again.", Brand.K.Muted));
        else
        {
            var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            cards.Children.Add(Card("Working time", t.Working, "Active time: stops after the idle threshold."));
            cards.Children.Add(Card("AI time", t.Ai, "Tool and chat tasks from start to end, approval waits included."));
            cards.Children.Add(Card("By-hand time", t.ByHand, "Every other active minute."));
            cards.Children.Add(Card("Time saved", t.Saved, "By-hand estimate minus AI time, per task."));
            cards.Children.Add(Card("Fully by hand (estimate)", t.FullyByHand, "Working time plus time saved."));
            body.Children.Add(cards);
        }
        if (report.Queued is { } queued) body.Children.Add(Notice(queued));

        if (!empty)
        {
            body.Children.Add(Heading("Per tool"));
            body.Children.Add(Table("Per tool", ["Tool", "Runs", "Items", "AI", "By hand (est.)", "Saved"],
                report.ByTool().Take(60).Select(x => new[]
                {
                    TimeReport.ToolLabel(x.Tool), x.Totals.Runs.ToString(), x.Units.ToString(), TimeMath.Duration(x.Totals.Ai),
                    TimeMath.Duration(x.Totals.Estimate), TimeMath.Duration(x.Totals.Saved),
                })));
            body.Children.Add(Heading("Per day"));
            body.Children.Add(Table("Per day", ["Day", "Working", "AI", "By hand", "Saved"],
                report.ByDay().Take(60).Select(x => new[]
                {
                    x.Day, TimeMath.Duration(x.Totals.Working), TimeMath.Duration(x.Totals.Ai), TimeMath.Duration(x.Totals.ByHand), TimeMath.Duration(x.Totals.Saved),
                })));
            var users = report.ByUser();
            if (users.Count > 1)
            {
                body.Children.Add(Heading("Per user"));
                body.Children.Add(Table("Per user", ["User", "Working", "AI", "By hand", "Saved"],
                    users.Select(x => new[] { x.User, TimeMath.Duration(x.Totals.Working), TimeMath.Duration(x.Totals.Ai), TimeMath.Duration(x.Totals.ByHand), TimeMath.Duration(x.Totals.Saved) })));
            }
        }
        var note = Brand.Text("By-hand rates are estimates unless you set your own (Settings > Edit by-hand rates). The totals are kept in the model " +
                              "and in Project Information, so anyone who opens a shared model can see them, per user.", Brand.K.Muted, size: 11);
        note.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(note);

        var scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true, IsTabStop = true,
        };
        AutomationProperties.SetName(scroll, "Time saved in " + report.Model);

        var close = Brand.Button("Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        _export.IsDefault = true;
        _export.IsEnabled = !empty;
        _export.Click += (_, _) => Export();
        AutomationProperties.SetHelpText(_export, "Writes the summary, per tool and per day with a user column, to an Excel file in Documents\\SketchProMax.");
        var foot = new StackPanel { Margin = new Thickness(18, 0, 18, 14) };
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        foot.Children.Add(_status);
        foot.Children.Add(Brand.ButtonRow(close, _export));

        var root = new DockPanel();
        var header = Brand.Header("Time saved", report.Model);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(scroll);
        Content = root;
        Loaded += (_, _) => (_export.IsEnabled ? _export : close).Focus();
    }

    private static FrameworkElement Card(string label, double seconds, string help)
    {
        var stack = new StackPanel();
        stack.Children.Add(Brand.Text(label, Brand.K.Muted, size: 11));
        stack.Children.Add(Brand.Text(TimeMath.Duration(seconds), size: 18, bold: true));
        var card = new Border { Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8), MinWidth = 150, CornerRadius = new CornerRadius(6), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        card.ToolTip = help;
        AutomationProperties.SetName(card, $"{label}: {TimeMath.Duration(seconds)}. {help}");
        return card;
    }

    private static FrameworkElement Notice(string text)
    {
        var border = new Border { Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        border.SetResourceReference(Border.BackgroundProperty, Brand.K.NoticeBg);
        border.SetResourceReference(Border.BorderBrushProperty, Brand.K.NoticeBorder);
        border.Child = Brand.Text(text);
        return border;
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

    /// <summary>A read-only table: the first column takes the spare width and wraps, the others fit their text.</summary>
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

    private void Export()
    {
        if (Brand.IsBusy(_export)) return;
        Brand.SetBusy(_export, true, "Exporting…");
        string? file = null;
        string? problem = null;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, $"{ProjectPack.Safe(_report.Model)} - Time saved {DateTime.Now:yyyy-MM-dd HHmm}.xlsx");
            Xlsx.Write(file, _report.ExcelSheets());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            problem = ex.Message;
        }
        Brand.SetBusy(_export, false);
        _status.Visibility = Visibility.Visible;
        if (problem is not null || file is null)
        {
            Brand.Fg(_status, Brand.K.DangerText);
            _status.Text = $"The Excel file wasn't written: {problem}. Close it if it is open in Excel, then press Export again.";
            return;
        }
        Brand.Fg(_status, Brand.K.SuccessText);
        _status.Text = $"Saved: {file}";
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { /* the path is shown above */ }
    }
}
