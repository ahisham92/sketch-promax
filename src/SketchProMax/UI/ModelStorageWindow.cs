using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Commands;
using SketchProMax.Revit;
using SketchProMax.Settings;
using M = SketchProMax.Tools.ModelStorageMath;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Control = System.Windows.Controls.Control;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Visibility = System.Windows.Visibility;

namespace SketchProMax.UI;

/// <summary>
/// The Storage & cleanup button's window (Check panel), laid out like a phone's storage page: the model's size against the limit
/// (Settings > Model size, 200 MB by default) on a bar coloured by kind, recommendations with the cleanup that fits
/// each, the four cleanups (the old Purge & cleanup button's), the linked files, then everything the model holds, heaviest first, with a search box. A row opens to say what
/// it is and offers Select in Revit or the cleanup that fits it.
///
/// The window is modal, opened from the ribbon command, so it reads the model in short steps on Revit's thread while
/// the command runs (Revit/ModelStorageReader) and stays responsive; the first sizes show within a second or two and
/// each family's measured size replaces its estimate as it comes. A cleanup or a selection closes it and the command
/// does it (RibbonTools.Run, with its preview first), then opens it again with the new sizes.
/// </summary>
internal sealed class ModelStorageWindow : Window
{
    private static readonly TimeSpan StepBudget = TimeSpan.FromMilliseconds(150);
    private const int RowsShown = 150;

    private readonly Document _doc;
    private ModelStorageReader _reader;
    private int _limitMb = M.ClampLimit(AgentSettings.Load().ModelSizeLimitMb);
    private bool _closed, _stop, _reading;
    private int _shown = RowsShown;
    private M.Report? _report;
    private string? _openRow;

    private readonly StackPanel _summary = new();
    private readonly StackPanel _tips = new();
    private readonly StackPanel _links = new();
    private readonly StackPanel _rows = new();
    private readonly TextBox _search = new() { MinHeight = 28, Padding = new Thickness(26, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted, size: 11.5);
    private readonly Button _again = Brand.Button("Measure again");
    private readonly Button _stopButton = Brand.Button("Stop measuring");
    private bool _showAllTips;

    /// <summary>What the command does after the window closes: run a cleanup tool, or select elements.</summary>
    public string? Tool { get; private set; }
    public List<long>? Select { get; private set; }

    public static Result Command(UIApplication app)
    {
        var ui = app.ActiveUIDocument;
        if (ui?.Document is not { IsFamilyDocument: false } doc)
        {
            TaskDialog.Show("Sketch ProMax", "Open a project model first: Storage & cleanup shows what is inside it.");
            return Result.Cancelled;
        }
        while (true)
        {
            var window = new ModelStorageWindow(doc, fresh: false);
            window.ShowDialog();
            if (window.Select is { Count: > 0 } ids)
            {
                try
                {
                    var list = ids.Select(i => new ElementId(i)).Where(i => doc.GetElement(i) is not null).ToList();
                    ui.Selection.SetElementIds(list);
                    if (list.Count > 0) ui.ShowElements(list);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException)
                {
                    TaskDialog.Show("Sketch ProMax", "Revit couldn't show them here (" + ex.Message + "). They are selected: open a view that shows them.");
                }
                return Result.Succeeded;
            }
            if (window.Tool is not { } tool) return Result.Succeeded;
            // The cleanup's options, its preview and Delete them as from the ribbon; then the window again, with the new sizes.
            if (RibbonTools.Run(app, tool) == Result.Failed) return Result.Failed;
        }
    }

    private ModelStorageWindow(Document doc, bool fresh)
    {
        _doc = doc;
        _reader = ModelStorageReader.Plan(doc, fresh);
        Title = "Storage & cleanup";
        Width = 600;
        Height = 780;
        MinWidth = 440;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };
        body.Children.Add(Card(_summary));
        body.Children.Add(_tips);
        body.Children.Add(Heading("Clean up", new Thickness(4, 18, 4, 6)));
        body.Children.Add(Card(Cleanups(), padding: new Thickness(0, 4, 0, 4)));
        body.Children.Add(_links);

        var listHead = new DockPanel { Margin = new Thickness(4, 18, 4, 6) };
        var sort = Brand.Text("Size, heaviest first", Brand.K.Muted, size: 12.5);
        DockPanel.SetDock(sort, Dock.Right);
        listHead.Children.Add(sort);
        listHead.Children.Add(Heading("Inside this model"));
        body.Children.Add(listHead);
        body.Children.Add(SearchBox());
        body.Children.Add(Card(_rows, padding: new Thickness(0, 4, 0, 4)));

        var scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true, IsTabStop = true,
        };
        AutomationProperties.SetName(scroll, "Model storage of " + doc.Title);

        var close = Brand.Button("Close", Brand.Weight.Primary);
        close.IsCancel = true;
        close.IsDefault = true;
        close.Click += (_, _) => Close();
        _again.Click += async (_, _) => await Restart();
        AutomationProperties.SetHelpText(_again, "Measures every family again instead of reusing this week's measures. Takes a few minutes on a big model.");
        _stopButton.Click += (_, _) => { _stop = true; Brand.SetBusy(_stopButton, true, "Stopping…"); };
        AutomationProperties.SetHelpText(_stopButton, "Stops measuring families: the rest keep their estimates. What was measured is kept for next time.");
        var foot = new StackPanel { Margin = new Thickness(18, 0, 18, 14) };
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        foot.Children.Add(_status);
        foot.Children.Add(Brand.ButtonRow(_again, _stopButton, close));

        var root = new DockPanel();
        var header = Brand.Header("Storage & cleanup", doc.Title);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(scroll);
        Content = root;

        Closed += (_, _) => { _closed = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); e.Handled = true; }
        };
        Loaded += async (_, _) => { close.Focus(); await Read(); };
        Fill();
    }

    // ---- Reading -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads in steps of <see cref="StepBudget"/> on Revit's thread (the command is still running, so the Revit API may be
    /// used here), giving the window its turn between steps. The list is redrawn as parts finish.
    /// </summary>
    private async Task Read()
    {
        if (_reading) return;
        _reading = true;
        _stop = false;
        _again.IsEnabled = false;
        _stopButton.Visibility = Visibility.Visible;
        Brand.SetBusy(_stopButton, false);
        var lastDraw = DateTime.MinValue;
        var shownQuick = false;
        try
        {
            while (!_closed)
            {
                bool more;
                try { more = _reader.RunFor(StepBudget); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _status.Text = "Part of the model couldn't be read (" + ex.Message + "). What was read is shown.";
                    break;
                }
                if (_stop) { _reader.StopMeasuring(); more = false; }
                var quick = _reader.QuickDone;
                if (quick && (!shownQuick || DateTime.Now - lastDraw > TimeSpan.FromSeconds(1.5)) || !more)
                {
                    shownQuick = shownQuick || quick;
                    lastDraw = DateTime.Now;
                    Fill();
                }
                Progress();
                if (!more) break;
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        finally
        {
            _reading = false;
            if (!_closed)
            {
                _again.IsEnabled = true;
                _stopButton.Visibility = Visibility.Collapsed;
                Progress();
            }
        }
    }

    private async Task Restart()
    {
        if (_reading) return;
        _reader = ModelStorageReader.Plan(_doc, fresh: true);
        _report = null;
        Fill();
        await Read();
    }

    private void Progress()
    {
        if (_closed) return;
        if (!_reader.QuickDone) { _status.Text = "Reading the model… You can't use Revit until this window closes."; return; }
        if (_reading && _reader.FamiliesLeft > 0)
        {
            var done = _reader.FamiliesTotal - _reader.FamiliesLeft;
            _status.Text = $"Measuring families: {done:N0} of {_reader.FamiliesTotal:N0} ({_reader.Next}). Each measured size replaces its estimate. Stop to keep the estimates for the rest.";
            return;
        }
        _status.Text = _stop
            ? "Stopped. Families not measured show an estimate; what was measured is kept for a week."
            : "Sizes inside a model are measured where Revit allows (families, image files, Sketch ProMax data) and estimated elsewhere. Use them to find the heaviest things.";
    }

    // ---- Drawing -------------------------------------------------------------------------------------------------------

    private void Fill()
    {
        if (_reader.QuickDone || _report is null)
            _report = _reader.QuickDone ? M.Balance(_reader.FileBytes, _reader.Rows()) : null;
        FillSummary();
        FillTips();
        FillLinks();
        FillRows();
    }

    private void FillSummary()
    {
        _summary.Children.Clear();
        var top = new DockPanel();
        var used = Brand.Text(_report is null ? "Calculating…" : M.UsedLine(_report.Total, _limitMb), Brand.K.Muted, size: 13);
        used.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(used, Dock.Right);
        top.Children.Add(used);
        top.Children.Add(Brand.Text("Model", size: 15, bold: true));
        _summary.Children.Add(top);

        var bar = new Grid { Height = 16, Margin = new Thickness(0, 10, 0, 8), ClipToBounds = true, SnapsToDevicePixels = true };
        var clip = new Border { CornerRadius = new CornerRadius(4), Child = bar };
        clip.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        // Rounded ends: the bar is clipped to its border's rounded rectangle.
        clip.SizeChanged += (_, e) => bar.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 4, 4);
        var names = new List<string>();
        if (_report is not null)
            foreach (var (kind, fraction) in M.Bar(_report, _limitMb))
            {
                if (fraction <= 0) continue;
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
                if (kind is null) continue; // the free part shows the track
                var piece = new Rectangle { Fill = Brand.Brush(Rgb(kind.Colour)), Margin = new Thickness(0, 0, 1, 0), ToolTip = kind.Name };
                Grid.SetColumn(piece, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(piece);
                names.Add(kind.Name);
            }
        AutomationProperties.SetName(clip, _report is null ? "Size bar: calculating" : "Size bar: " + string.Join(", ",
            _report.ByKind.Select(s => s.Kind.Name + " " + M.Size(s.Bytes))));
        _summary.Children.Add(clip);

        if (_report is not null)
        {
            var legend = new WrapPanel();
            foreach (var share in _report.ByKind)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 14, 2), ToolTip = M.Size(share.Bytes) };
                item.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Brand.Brush(Rgb(share.Kind.Colour)), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(Brand.Text(share.Kind.Name, size: 12));
                legend.Children.Add(item);
            }
            _summary.Children.Add(legend);
            if (M.OverLine(_report.Total, _limitMb) is { } over)
            {
                var line = Brand.Text(over + $". The limit is {M.Size(M.Bytes(_limitMb))}; it can be changed in Settings.", Brand.K.DangerText, size: 12, bold: true);
                line.Margin = new Thickness(0, 8, 0, 0);
                line.TextWrapping = TextWrapping.Wrap;
                _summary.Children.Add(line);
            }
            var file = _report.OnDisk
                ? $"The file on disk{(_reader.Saved is { } at ? ", saved " + at.ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture) : "")}. A cleanup shows in the total after you save."
                : "This model has no file on this PC yet (not saved, or a cloud model): the total adds up the estimates.";
            if (_reader.Extras.CentralBytes is { } central) file += $" Central model: {M.Size(central)}.";
            var note = Brand.Text(file, Brand.K.Muted, size: 11.5);
            note.Margin = new Thickness(0, 6, 0, 0);
            note.TextWrapping = TextWrapping.Wrap;
            _summary.Children.Add(note);
        }
        var limit = new TextBlock { Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        var change = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Change the limit"));
        change.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Brand.K.Link);
        change.Click += (_, _) => ChangeLimit();
        limit.Inlines.Add(change);
        _summary.Children.Add(limit);
    }

    private void ChangeLimit()
    {
        var settings = new SettingsWindow(SettingsLayout.Storage) { Owner = this };
        settings.ShowDialog();
        _limitMb = M.ClampLimit(AgentSettings.Load().ModelSizeLimitMb);
        FillSummary();
    }

    private void FillTips()
    {
        _tips.Children.Clear();
        if (_report is null) return;
        var tips = M.Recommendations(_report, _reader.Extras);
        if (tips.Count == 0) return;
        var head = new DockPanel { Margin = new Thickness(4, 18, 4, 6) };
        if (tips.Count > 2)
        {
            var all = new TextBlock { FontSize = 12.5 };
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(_showAllTips ? "Show fewer" : "Show all"));
            link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Brand.K.Link);
            link.Click += (_, _) => { _showAllTips = !_showAllTips; FillTips(); };
            all.Inlines.Add(link);
            DockPanel.SetDock(all, Dock.Right);
            head.Children.Add(all);
        }
        head.Children.Add(Heading("Recommendations"));
        _tips.Children.Add(head);
        foreach (var tip in _showAllTips ? tips : tips.Take(2))
        {
            var card = new StackPanel();
            var top = new DockPanel();
            if (tip.Tool.Length > 0)
            {
                var run = Brand.Button(tip.Button);
                run.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetHelpText(run, "Closes this window and runs " + tip.Button + ": its preview comes first and nothing is deleted until you say so. This window opens again after.");
                var tool = tip.Tool;
                run.Click += (_, _) => { Tool = tool; Close(); };
                DockPanel.SetDock(run, Dock.Right);
                top.Children.Add(run);
            }
            var title = Brand.Text(tip.Title, size: 13.5, bold: true);
            title.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(title);
            card.Children.Add(top);
            var text = Brand.Text(tip.Text, Brand.K.Muted, size: 12);
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(0, 6, 0, 0);
            card.Children.Add(text);
            var border = Card(card);
            border.Margin = new Thickness(0, 0, 0, 8);
            _tips.Children.Add(border);
        }
    }

    private void FillLinks()
    {
        _links.Children.Clear();
        if (!_reader.QuickDone || _reader.Links.Count == 0) return;
        _links.Children.Add(Heading("Links (outside this file)", new Thickness(4, 18, 4, 2)));
        var hint = Brand.Text("Links aren't stored in the model, but every loaded link is read when the model opens. Links on a network drive are the slowest.", Brand.K.Muted, size: 11.5);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(4, 0, 4, 6);
        _links.Children.Add(hint);
        var list = new StackPanel();
        var first = true;
        foreach (var link in _reader.Links.OrderByDescending(l => l.Bytes ?? 0))
        {
            if (!first) list.Children.Add(Separator());
            first = false;
            var detail = (link.Cad ? "CAD link" : "Revit link") + (link.Loaded ? "" : " · not loaded") + (link.Path.Length > 0 ? " · " + link.Path : "");
            list.Children.Add(RowView(Brand.Brush(Rgb(link.Cad ? M.KindOf(M.Cad).Colour : M.KindOf(M.Model).Colour)), Glyph(link.Cad ? M.Cad : M.Model),
                link.Name, detail, link.Bytes is { } b ? M.Size(b) : "not found", null));
        }
        _links.Children.Add(Card(list, padding: new Thickness(0, 4, 0, 4)));
    }

    private void FillRows()
    {
        _rows.Children.Clear();
        if (_report is null)
        {
            var wait = Brand.Text("Calculating…", Brand.K.Muted);
            wait.Margin = new Thickness(14, 10, 14, 10);
            _rows.Children.Add(wait);
            return;
        }
        var find = _search.Text.Trim();
        var rows = _report.Items.Where(i => find.Length == 0
            || i.Name.IndexOf(find, StringComparison.CurrentCultureIgnoreCase) >= 0
            || i.Detail.IndexOf(find, StringComparison.CurrentCultureIgnoreCase) >= 0
            || M.KindOf(i.Kind).Name.IndexOf(find, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        if (rows.Count == 0)
        {
            var none = Brand.Text("Nothing matches \"" + find + "\".", Brand.K.Muted);
            none.Margin = new Thickness(14, 10, 14, 10);
            _rows.Children.Add(none);
            return;
        }
        var first = true;
        foreach (var item in rows.Take(_shown))
        {
            if (!first) _rows.Children.Add(Separator());
            first = false;
            var kind = M.KindOf(item.Kind);
            var key = item.Kind + "|" + item.Name;
            var size = (item.Measured ? "" : "about ") + M.Size(item.Bytes);
            _rows.Children.Add(RowView(Brand.Brush(Rgb(kind.Colour)), Glyph(item.Kind), item.Name, item.Detail, size,
                () => { _openRow = _openRow == key ? null : key; FillRows(); }, _openRow == key));
            if (_openRow == key) _rows.Children.Add(Details(item, kind));
        }
        if (rows.Count > _shown)
        {
            var more = Brand.Button($"Show {Math.Min(RowsShown, rows.Count - _shown):N0} more");
            more.HorizontalAlignment = HorizontalAlignment.Left;
            more.Margin = new Thickness(14, 8, 14, 8);
            more.Click += (_, _) => { _shown += RowsShown; FillRows(); };
            _rows.Children.Add(more);
        }
    }

    /// <summary>An opened row: what the kind holds, whether the size is measured, and what can be done about it.</summary>
    private FrameworkElement Details(M.Item item, M.Kind kind)
    {
        var panel = new StackPanel { Margin = new Thickness(58, 0, 14, 10) };
        var what = Brand.Text(kind.What, Brand.K.Muted, size: 12);
        what.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(what);
        var how = Brand.Text(item.Measured ? "Measured." : item.Kind == M.Revit ? "What the file holds besides everything listed." : "Estimated from what it contains.", Brand.K.Muted, size: 11.5);
        how.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(how);
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (item.Ids.Count > 0)
        {
            var select = Brand.Button(item.Ids.Count == 1 ? "Select in Revit" : $"Select {item.Ids.Count:N0} in Revit");
            select.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetHelpText(select, "Closes this window and selects them in Revit, zoomed to where they are.");
            var ids = item.Ids;
            select.Click += (_, _) => { Select = ids; Close(); };
            buttons.Children.Add(select);
        }
        var (tool, label) = item.Kind switch
        {
            M.Families when item.Unused => ("purge_unused", "Purge unused"),
            M.Cad => ("cleanup_imports", "Clean up imports"),
            M.Views when item.Unused => ("delete_unused_views", "Delete unused views"),
            M.Groups or M.Images when item.Unused => ("cleanup_model", "Model health clean"),
            _ => ("", ""),
        };
        if (tool.Length > 0)
        {
            var run = Brand.Button(label);
            run.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetHelpText(run, "Closes this window and runs " + label + ": its preview comes first and nothing is deleted until you say so.");
            run.Click += (_, _) => { Tool = tool; Close(); };
            buttons.Children.Add(run);
        }
        if (buttons.Children.Count > 0) panel.Children.Add(buttons);
        return panel;
    }

    /// <summary>The four cleanups, as the Purge & cleanup button offered them: each closes the window and runs with its preview first.</summary>
    private StackPanel Cleanups()
    {
        (string Tool, string Name, string Text)[] cleanups =
        [
            ("purge_unused", "Purge unused", "Families, types, materials, patterns, unused view templates and filters, repeated until nothing new is unused."),
            ("delete_unused_views", "Delete unused views", "Views on no sheet. Keeps WORKING and _keep views, open views and views others depend on."),
            ("cleanup_imports", "Clean up imports", "Imported CAD (or CAD links when asked), their leftover types and unused IMPORT- line styles."),
            ("cleanup_model", "Model health clean", "Unused view templates, unplaced rooms and areas, unused groups and filters, and the warnings by type."),
        ];
        var list = new StackPanel();
        var tile = Brand.Brush(Rgb(M.KindOf(M.Types).Colour));
        foreach (var (tool, name, text) in cleanups)
        {
            if (list.Children.Count > 0) list.Children.Add(Separator());
            var row = RowView(tile, "\uE74D", name, text, "", () => { Tool = tool; Close(); });
            AutomationProperties.SetHelpText(row, "Closes this window and runs " + name + ": its options, then the preview. Nothing is deleted until you press Delete them, and Ctrl+Z undoes it in one step. This window opens again after.");
            list.Children.Add(row);
        }
        return list;
    }

    // ---- Pieces --------------------------------------------------------------------------------------------------------

    /// <summary>A list row: a coloured tile, the name over its grey line, the size and a chevron when it opens.</summary>
    private static FrameworkElement RowView(Brush tile, string glyph, string name, string detail, string size, Action? open, bool opened = false)
    {
        var grid = new Grid { Margin = new Thickness(14, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = tile, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Brand.Text(name, size: 13.5);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title);
        if (detail.Length > 0)
        {
            var sub = Brand.Text(detail, Brand.K.Muted, size: 11.5);
            // A row without a size (the cleanups) is a description: it wraps; a list row's line is trimmed.
            if (size.Length == 0) sub.TextWrapping = TextWrapping.Wrap;
            else sub.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(sub);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var amount = Brand.Text(size, Brand.K.Muted, size: 13);
        amount.VerticalAlignment = VerticalAlignment.Center;
        amount.Margin = new Thickness(10, 0, 6, 0);
        Grid.SetColumn(amount, 2);
        grid.Children.Add(amount);

        if (open is null)
        {
            AutomationProperties.SetName(grid, $"{name}, {size}. {detail}");
            return grid;
        }
        var chevron = Brand.Text(opened ? "" : "", Brand.K.Muted, size: 11);
        chevron.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(chevron);

        // The whole row is one button (Enter or Space opens it), drawn flat.
        var button = new Button
        {
            Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Cursor = Cursors.Hand, Template = FlatTemplate(),
        };
        AutomationProperties.SetName(button, $"{name}, {size}. {detail}");
        AutomationProperties.SetHelpText(button, opened ? "Closes the details." : "Opens what it is and what can be done about it.");
        button.Click += (_, _) => open();
        return button;
    }

    private static ControlTemplate? _flat;

    /// <summary>A button drawn as its content only, with a hover tint and a focus ring from the palette.</summary>
    private static ControlTemplate FlatTemplate() => _flat ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Border x:Name="b" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent" BorderThickness="1" BorderBrush="Transparent">
            <ContentPresenter HorizontalAlignment="Stretch"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private FrameworkElement SearchBox()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        _search.SetResourceReference(Control.BackgroundProperty, Brand.K.Field);
        _search.SetResourceReference(Control.ForegroundProperty, Brand.K.Text);
        _search.SetResourceReference(Control.BorderBrushProperty, Brand.K.InputBorder);
        AutomationProperties.SetName(_search, "Search what is inside the model");
        AutomationProperties.SetHelpText(_search, "Type part of a name, a category or a kind such as Families. Ctrl+F comes here.");
        _search.TextChanged += (_, _) => { _shown = RowsShown; FillRows(); };
        grid.Children.Add(_search);
        var glass = Brand.Text("", Brand.K.Muted, size: 12);
        glass.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        glass.IsHitTestVisible = false;
        glass.VerticalAlignment = VerticalAlignment.Center;
        glass.Margin = new Thickness(9, 0, 0, 0);
        grid.Children.Add(glass);
        var hint = Brand.Text("Search", Brand.K.Muted, size: 12.5);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(28, 0, 0, 0);
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        grid.Children.Add(hint);
        return grid;
    }

    private static Border Card(UIElement content, Thickness? padding = null)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = padding ?? new Thickness(16, 14, 16, 14), BorderThickness = new Thickness(1), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        return card;
    }

    private static FrameworkElement Separator()
    {
        var line = new Border { Height = 1, Margin = new Thickness(58, 0, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, Brand.K.BorderSubtle);
        return line;
    }

    private static TextBlock Heading(string text, Thickness? margin = null)
    {
        var heading = Brand.Text(text, Brand.K.Muted, size: 13, bold: true);
        if (margin is { } m) heading.Margin = m;
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
        return heading;
    }

    /// <summary>The tile's icon per kind (Segoe Fluent Icons / MDL2 Assets).</summary>
    private static string Glyph(string kind) => kind switch
    {
        M.Families => "", // package
        M.Cad => "", // map layers
        M.Images => "", // photo
        M.Shapes => "", // edit (modelled in place)
        M.Model => "", // building
        M.Views => "", // document
        M.Annotation => "", // tag
        M.Groups => "", // group
        M.Types => "", // settings
        M.Ours => "", // save
        _ => "", // folder
    };

    private static Color Rgb(uint v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
}
