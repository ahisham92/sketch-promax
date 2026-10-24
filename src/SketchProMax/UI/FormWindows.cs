using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SketchProMax.UI;

/// <summary>
/// Asks for the inputs of a recipe or learned tool run from the ribbon, and doubles as the confirmation to run it.
/// Built from the tool's input schema (or the recipe's {placeholders}), so it needs no per-tool code. Since 0.35 the
/// fields sit in two columns (label, then the box), each field's help is its tooltip (the ⓘ after a label says there
/// is one), lists from the model replace typed ids and level names, and the optional fields nobody has filled fold
/// under More options when there are more than a few.
/// </summary>
internal sealed class InputsWindow : Window
{
    /// <param name="ElementIds">Takes element ids: a "Use current selection" button fills in the ids selected in Revit when the window opened.</param>
    /// <param name="Options">Picked from a list of names that stand for values (a type's "Family: Type" for its id).</param>
    /// <param name="Suggestions">Offered in a list that can still be typed over (the model's level names).</param>
    public sealed record Field(string Key, string Label, string? Help, string Type, bool Required, string[]? Choices, string? Value,
        bool ElementIds = false, IReadOnlyList<Option>? Options = null, IReadOnlyList<string>? Suggestions = null);

    /// <summary>One entry of <see cref="Field.Options"/>: what the list shows, and the value it stands for.</summary>
    public sealed record Option(string Text, string Value)
    {
        public override string ToString() => Text;
    }

    /// <summary>More fields than this and the empty optional ones fold under More options.</summary>
    internal const int ShownAlways = 4;

    private readonly List<(Field Field, FrameworkElement Control)> _controls = [];
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText);
    private readonly CheckBox? _option;
    private Expander? _more;

    public Dictionary<string, string> Values { get; } = new();
    public bool OptionChecked => _option?.IsChecked == true;

    /// <summary>Checks the values before the window closes; the lines it returns are shown and the window stays open.</summary>
    public Func<Dictionary<string, string>, IReadOnlyList<string>>? Validate { get; init; }

    public InputsWindow(string title, string subtitle, string? intro, IReadOnlyList<string> details, IReadOnlyList<Field> fields,
        string runLabel, string? optionLabel = null, string selection = "", string? favourite = null)
    {
        // selection: the ids selected in Revit when the window opened ("1, 2, 3"), offered to element-id fields.
        // favourite: its key on the My tools panel ("tool:name", see Favourites), for the Pin to My tools box; null for none.
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        _error.Visibility = Visibility.Collapsed;
        _error.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        if (!string.IsNullOrWhiteSpace(intro))
        {
            var introText = Brand.Text(intro!);
            introText.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(introText);
        }
        if (details.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var d in details)
                list.Children.Add(Brand.Text("•  " + d, Brand.K.Muted));
            body.Children.Add(list);
        }

        // Required fields and the ones with a value show; the rest fold away when there are many.
        var fold = fields.Count > ShownAlways;
        var shown = fields.Where(f => !fold || f.Required || !string.IsNullOrEmpty(f.Value)).ToList();
        var folded = fields.Where(f => !shown.Contains(f)).ToList();
        body.Children.Add(FieldGrid(shown, selection));
        if (folded.Count > 0)
        {
            var more = new Expander
            {
                Header = $"More options ({folded.Count})", Content = FieldGrid(folded, selection), Margin = new Thickness(0, 10, 0, 0),
                ToolTip = string.Join(", ", folded.Select(f => f.Label)),
            };
            AutomationProperties.SetName(more, $"More options: {string.Join(", ", folded.Select(f => f.Label))}");
            body.Children.Add(more);
            _more = more;
        }

        if (optionLabel is not null)
        {
            _option = new CheckBox { Content = optionLabel, Margin = new Thickness(0, 12, 0, 0) };
            body.Children.Add(_option);
        }
        if (favourite is not null) body.Children.Add(PinBox(favourite));
        body.Children.Add(_error);

        // One primary action (Enter); Cancel (Esc) only closes this window, nothing runs.
        var run = Brand.Button(runLabel, Brand.Weight.Primary);
        run.IsDefault = true;
        run.Click += (_, _) => Submit();
        var cancel = Brand.Button("Cancel");
        cancel.IsCancel = true;
        body.Children.Add(Brand.ButtonRow(cancel, run));

        var root = new DockPanel();
        var header = Brand.Header(title, subtitle);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
        Loaded += (_, _) => (_controls.FirstOrDefault().Control as Control ?? run).Focus();
    }

    /// <summary>Label on the left, its box on the right; the help is the tooltip of both.</summary>
    private Grid FieldGrid(IReadOnlyList<Field> fields, string selection)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var field in fields)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var help = string.IsNullOrWhiteSpace(field.Help) ? null : field.Help;
            FrameworkElement control;
            if (field.Type == "boolean")
            {
                control = new CheckBox { Content = field.Label, IsChecked = field.Value is "true" or "True" or "yes", VerticalAlignment = VerticalAlignment.Center };
            }
            else if (field.Options is { Count: > 0 } options)
            {
                var combo = new ComboBox { IsEditable = false, MinHeight = 26 };
                if (!field.Required) combo.Items.Add(new Option("", ""));
                foreach (var o in options) combo.Items.Add(o);
                combo.SelectedItem = options.FirstOrDefault(o => o.Value == field.Value) ?? (field.Required ? options[0] : combo.Items[0]);
                control = combo;
            }
            else if (field.Choices is { Length: > 0 } choices)
            {
                var combo = new ComboBox { IsEditable = false, MinHeight = 26 };
                if (!field.Required) combo.Items.Add("");
                foreach (var c in choices) combo.Items.Add(c);
                combo.SelectedItem = field.Value is { } v && choices.Contains(v) ? v : field.Required ? choices[0] : "";
                control = combo;
            }
            else if (field.Suggestions is { Count: > 0 } suggestions)
            {
                var combo = new ComboBox { IsEditable = true, MinHeight = 26 };
                foreach (var name in suggestions) combo.Items.Add(name);
                combo.Text = field.Value ?? (field.Required ? suggestions[0] : "");
                control = combo;
            }
            else
            {
                control = new TextBox { Text = field.Value ?? "", MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center };
            }
            AutomationProperties.SetName(control, field.Label + (field.Required ? " (required)" : ""));
            if (help is not null)
            {
                AutomationProperties.SetHelpText(control, help);
                control.ToolTip = help;
            }
            FrameworkElement cell = control;
            if (field.ElementIds && control is TextBox ids)
            {
                // Element ids: the selection made in Revit before opening this window, one click away.
                var count = selection.Length == 0 ? 0 : selection.Split(',').Length;
                var use = Brand.Button(count == 0 ? "Use selection" : $"Use selection ({count})");
                use.Margin = new Thickness(8, 0, 0, 0);
                use.IsEnabled = count > 0;
                use.ToolTip = count > 0
                    ? $"Fills in the {count} element(s) selected in Revit."
                    : "Nothing is selected in Revit. Close this window, select elements, then run the tool again.";
                ToolTipService.SetShowOnDisabled(use, true);
                AutomationProperties.SetName(use, $"Use current selection for {field.Label}");
                use.Click += (_, _) => { ids.Text = selection; ids.Focus(); };
                var dock = new DockPanel();
                DockPanel.SetDock(use, Dock.Right);
                dock.Children.Add(use);
                dock.Children.Add(ids);
                cell = dock;
            }
            cell.Margin = new Thickness(0, 4, 0, 4);
            if (field.Type != "boolean")
            {
                var label = Brand.Text(field.Label + (field.Required ? " *" : "") + (help is null ? "" : "  ⓘ"), bold: field.Required);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.TextWrapping = TextWrapping.Wrap;
                label.Margin = new Thickness(0, 4, 10, 4);
                if (help is not null) label.ToolTip = help;
                Grid.SetRow(label, row);
                grid.Children.Add(label);
            }
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, 1);
            grid.Children.Add(cell);
            _controls.Add((field, control));
        }
        return grid;
    }

    /// <summary>
    /// "Pin to My tools": a button for this tool, recipe or learned tool next to My tools, saved as soon as it is ticked
    /// (running or cancelling this window doesn't change it).
    /// </summary>
    private static FrameworkElement PinBox(string key)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var pinned = FavouritesRibbon.IsPinned(key);
        var full = !pinned && FavouritesRibbon.Pinned.Count >= Favourites.Max;
        var box = new CheckBox { Content = "Pin to My tools", IsChecked = pinned, IsEnabled = !full };
        var note = Brand.Text("", Brand.K.Muted, size: 11);
        AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
        void Say(string text, string colour = Brand.K.Muted)
        {
            note.Text = text;
            note.SetResourceReference(TextBlock.ForegroundProperty, colour);
            AutomationProperties.SetHelpText(box, text);
        }
        string Plain() => box.IsChecked == true
            ? "Pinned: it has its own button on the My tools panel. Untick to remove it."
            : "Gives it its own button on the My tools panel, next to My tools. Order your pins with Favourites there.";
        if (full) Say($"{Favourites.Max} are pinned already, the most My tools holds. Unpin one with Favourites on the My tools panel first.", Brand.K.WarningText);
        else Say(Plain());
        var busy = false;
        void Toggle()
        {
            if (busy) return;
            var problem = FavouritesRibbon.SetPinned(key, box.IsChecked == true);
            if (problem is null) { Say(Plain()); return; }
            busy = true;
            box.IsChecked = !box.IsChecked; // it didn't happen: show what is true
            busy = false;
            Say(problem, Brand.K.DangerText);
        }
        box.Checked += (_, _) => Toggle();
        box.Unchecked += (_, _) => Toggle();
        AutomationProperties.SetName(box, "Pin to My tools");
        panel.Children.Add(box);
        panel.Children.Add(note);
        return panel;
    }

    private void Submit()
    {
        Values.Clear();
        var missing = new List<string>();
        foreach (var (field, control) in _controls)
        {
            var value = control switch
            {
                CheckBox box => box.IsChecked == true ? "true" : "false",
                ComboBox { SelectedItem: Option option } => option.Value,
                ComboBox { IsEditable: true } typed => typed.Text.Trim(),
                ComboBox combo => combo.SelectedItem as string ?? "",
                TextBox text => text.Text.Trim(),
                _ => "",
            };
            if (value.Length == 0)
            {
                if (field.Required) missing.Add(field.Label);
                continue;
            }
            Values[field.Key] = value;
        }
        if (missing.Count > 0)
        {
            _error.Text = $"Fill in {string.Join(", ", missing)}, then press Enter to run (Esc closes without running).";
            _error.Visibility = Visibility.Visible;
            var first = _controls.FirstOrDefault(c => missing.Contains(c.Field.Label)).Control;
            Reveal(first);
            return;
        }
        if (Validate?.Invoke(Values) is { Count: > 0 } problems)
        {
            _error.Text = string.Join("\n", problems) + "\nCorrect it, then press Enter to run (Esc closes without running).";
            _error.Visibility = Visibility.Visible;
            var first = _controls.FirstOrDefault(c => problems.Any(p => p.StartsWith(c.Field.Label + ":", StringComparison.Ordinal))).Control;
            Reveal(first);
            return;
        }
        DialogResult = true;
    }

    /// <summary>Focuses a field, opening More options first when it is folded there.</summary>
    private void Reveal(FrameworkElement? control)
    {
        if (control is null) return;
        if (_more is not null && !_more.IsExpanded && IsInside(control, _more)) _more.IsExpanded = true;
        control.Dispatcher.BeginInvoke(new Action(() => control.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var at = element; at is not null; at = LogicalTreeHelper.GetParent(at))
            if (ReferenceEquals(at, container)) return true;
        return false;
    }
}

/// <summary>
/// Shows what a quick tool or check did, without blocking Revit: a status line, then (0.35) the result as a page: its
/// summary, figures across the top, a bar chart of its breakdown and lists whose rows open for details and select
/// their elements in Revit (see <see cref="ResultView"/>). The full text, to read or copy for a bug report, is folded
/// under Full details (open when the result isn't JSON or the tool failed), with the folder of any file it wrote.
/// </summary>
internal sealed class ResultWindow : Window
{
    public ResultWindow(string title, bool ok, string headline, string text)
    {
        Title = title;
        Width = 720;
        Height = 620;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var view = ok ? ResultView.From(text) : null;
        var status = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        status.Children.Add(Brand.Text((ok ? "✓  " : "✕  ") + headline, ok ? Brand.K.SuccessText : Brand.K.DangerText, 14, bold: true));
        if (!ok)
        {
            var next = Brand.Text("Read the details below. To get it fixed, press Copy and paste them into the chat; " +
                                  "Ctrl+Z in Revit undoes anything it changed.", Brand.K.Muted);
            next.Margin = new Thickness(0, 4, 0, 0);
            status.Children.Add(next);
        }
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(box, "Result");

        var buttons = Brand.ButtonRow();
        buttons.Margin = new Thickness(0, 12, 0, 0);
        var file = FindFile(text);
        if (file is not null)
        {
            var open = Brand.Button("Open folder");
            open.ToolTip = file;
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
                catch (Exception) { }
            };
            buttons.Children.Add(open);
        }
        var copy = Brand.Button("Copy");
        copy.ToolTip = "Copies the full result, to paste into the chat or a bug report.";
        copy.Click += (_, _) =>
        {
            copy.Content = ClipboardText.TrySet(text) ? "Copied" : "Copy failed, try again";
            AutomationProperties.SetItemStatus(copy, copy.Content as string ?? "");
        };
        // The one primary action: Enter and Esc both close.
        var close = Brand.Button("Close", Brand.Weight.Primary);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var body = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(status);
        body.Children.Add(buttons);
        if (view is null) body.Children.Add(box);
        else
        {
            var page = Page(view);
            box.Height = 220;
            var raw = new Expander { Header = "Full details (for a bug report)", Content = box, Margin = new Thickness(0, 14, 0, 0) };
            AutomationProperties.SetName(raw, "Full details");
            page.Children.Add(raw);
            body.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }

        var root = new DockPanel();
        var header = Brand.Header(title, "Ran in Revit without the AI. Ctrl+Z undoes any change.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;
        Loaded += (_, _) => close.Focus();
    }

    /// <summary>The summary, the figures, the chart and the lists, top to bottom.</summary>
    private static StackPanel Page(ResultView view)
    {
        var page = new StackPanel();
        if (!string.IsNullOrWhiteSpace(view.Summary))
        {
            var summary = Brand.Text(view.Summary!, size: 13);
            summary.Margin = new Thickness(0, 0, 0, 12);
            page.Children.Add(summary);
        }
        if (view.Figures.Count > 0)
        {
            var tiles = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var f in view.Figures)
            {
                var stack = new StackPanel();
                stack.Children.Add(Brand.Text(f.Value, size: 22, bold: true));
                stack.Children.Add(Brand.Text(f.Label, Brand.K.Muted, 11.5));
                var tile = Card(stack, new Thickness(12, 8, 12, 8));
                tile.MinWidth = 120;
                tile.Margin = new Thickness(0, 0, 10, 10);
                AutomationProperties.SetName(tile, $"{f.Label}: {f.Value}");
                tiles.Children.Add(tile);
            }
            page.Children.Add(tiles);
        }
        if (view.Bars is { Bars.Count: > 0 } chart)
        {
            page.Children.Add(Heading(chart.Title));
            var max = Math.Max(1e-9, chart.Bars.Max(b => Math.Abs(b.Value)));
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MaxWidth = 300 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (var i = 0; i < chart.Bars.Count; i++)
            {
                var (label, value) = chart.Bars[i];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = Brand.Text(label);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.ToolTip = label;
                name.Margin = new Thickness(0, 3, 10, 3);
                var track = new Border { Height = 12, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(2) };
                track.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
                var bar = new Border { Height = 12, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
                bar.SetResourceReference(Border.BackgroundProperty, Brand.K.Accent);
                var share = Math.Abs(value) / max;
                track.SizeChanged += (_, e) => bar.Width = Math.Max(2, e.NewSize.Width * share);
                track.Child = bar;
                var number = Brand.Text(ResultView.Number(value));
                number.Margin = new Thickness(10, 3, 0, 3);
                number.HorizontalAlignment = HorizontalAlignment.Right;
                AutomationProperties.SetName(track, $"{label}: {ResultView.Number(value)}");
                Grid.SetRow(name, i); Grid.SetRow(track, i); Grid.SetRow(number, i);
                Grid.SetColumn(track, 1); Grid.SetColumn(number, 2);
                grid.Children.Add(name); grid.Children.Add(track); grid.Children.Add(number);
            }
            page.Children.Add(grid);
        }
        foreach (var section in view.Sections)
        {
            page.Children.Add(Heading(section.Total > section.Rows.Count ? $"{section.Title} ({section.Rows.Count} of {section.Total})" : $"{section.Title} ({section.Total})"));
            var list = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var row in section.Rows) list.Children.Add(RowLine(row));
            page.Children.Add(list);
        }
        return page;
    }

    private static TextBlock Heading(string text)
    {
        var heading = Brand.Text(text, size: 13, bold: true);
        heading.Margin = new Thickness(0, 2, 0, 6);
        return heading;
    }

    /// <summary>One row: its text and figure, Select in Revit when it names elements, its details folded under it.</summary>
    private static FrameworkElement RowLine(ResultView.Row row)
    {
        var line = new DockPanel { LastChildFill = true };
        if (row.ElementIds.Count > 0)
        {
            var select = Brand.Button(row.ElementIds.Count == 1 ? "Select in Revit" : $"Select {row.ElementIds.Count} in Revit");
            select.Margin = new Thickness(8, 0, 0, 0);
            select.Padding = new Thickness(8, 2, 8, 2);
            select.ToolTip = "Selects and zooms to them in the active view.";
            AutomationProperties.SetName(select, $"Select in Revit: {row.Text}");
            var ids = string.Join(",", row.ElementIds.Take(500));
            select.Click += async (_, _) =>
            {
                try { await App.Dispatcher.InvokeAsync(a => Pane.Select(a, ids)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { select.Content = "Revit is busy, try again"; }
            };
            DockPanel.SetDock(select, Dock.Right);
            line.Children.Add(select);
        }
        if (row.Figure is not null)
        {
            var figure = Brand.Text(row.Figure, Brand.K.Muted);
            figure.Margin = new Thickness(10, 0, 0, 0);
            figure.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(figure, Dock.Right);
            line.Children.Add(figure);
        }
        var text = Brand.Text(row.Text);
        text.TextWrapping = TextWrapping.Wrap;
        text.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(text);

        FrameworkElement content = line;
        if (row.Details.Count > 0)
        {
            var details = new StackPanel { Margin = new Thickness(18, 4, 0, 2) };
            foreach (var (label, value) in row.Details)
            {
                var d = Brand.Text($"{label}: {value}", Brand.K.Muted, 11.5);
                d.TextWrapping = TextWrapping.Wrap;
                details.Children.Add(d);
            }
            line.MinWidth = 0;
            var expander = new Expander { Header = line, Content = details };
            AutomationProperties.SetName(expander, row.Text);
            // The header stretches, so the figure and Select sit at the right edge.
            expander.Loaded += (_, _) => { if (expander.Template?.FindName("HeaderSite", expander) is Control site) site.HorizontalContentAlignment = HorizontalAlignment.Stretch; };
            content = expander;
        }
        var card = Card(content, new Thickness(10, 6, 10, 6));
        card.Margin = new Thickness(0, 0, 0, 4);
        return card;
    }

    private static Border Card(UIElement child, Thickness padding)
    {
        var card = new Border { Child = child, Padding = padding, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        return card;
    }

    /// <summary>The first existing file path in a result (a CSV, a BCF, a report), if any.</summary>
    private static string? FindFile(string text)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z]:\\\\?[^""\r\n<>|*?]+?\.(csv|bcf|bcfzip|xlsx|txt|json|pdf|html)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var path = m.Value.Replace(@"\\", @"\");
            try { if (File.Exists(path)) return path; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return null;
    }
}

internal static class ClipboardText
{
    /// <summary>Sets the clipboard, retrying briefly when another program holds it.</summary>
    public static bool TrySet(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(60); }
        }
        return false;
    }
}
