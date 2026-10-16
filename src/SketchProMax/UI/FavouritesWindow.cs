using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SketchProMax.UI;

/// <summary>
/// The Favourites button's window: what is pinned to the My tools panel (on top, in ribbon order, with up, down and
/// unpin), then every ribbon tool, recipe and learned tool that can be pinned, with a search box. Each change is saved
/// and reaches the ribbon at once; Close (Enter or Esc) is the only primary action.
/// </summary>
internal sealed class FavouritesWindow : Window
{
    private readonly TextBox _search = new() { MinHeight = 28 };
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = Brand.Text("");
    private readonly TextBlock _problem = Brand.Text("", Brand.K.DangerText);

    public FavouritesWindow()
    {
        Title = "Favourites";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var body = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        var top = new StackPanel();
        _status.Margin = new Thickness(0, 0, 0, 4);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        top.Children.Add(_status);
        _problem.Visibility = Visibility.Collapsed;
        _problem.Margin = new Thickness(0, 0, 0, 4);
        AutomationProperties.SetLiveSetting(_problem, AutomationLiveSetting.Assertive);
        top.Children.Add(_problem);
        var searchLabel = Brand.Text("Search", bold: true);
        searchLabel.Margin = new Thickness(0, 8, 0, 2);
        top.Children.Add(searchLabel);
        AutomationProperties.SetName(_search, "Search tools, recipes and learned tools");
        AutomationProperties.SetHelpText(_search, "Type part of a name. Down arrow or Enter moves to the first result.");
        _search.TextChanged += (_, _) => Rebuild(null);
        _search.PreviewKeyDown += (_, e) =>
        {
            // Enter and Down go to the results (Enter would otherwise close the window).
            if (e.Key is not (Key.Enter or Key.Down)) return;
            e.Handled = true;
            var first = Buttons().FirstOrDefault(b => b.IsEnabled && (b.Tag as string)?.EndsWith("|pin", StringComparison.Ordinal) == true)
                        ?? Buttons().FirstOrDefault(b => b.IsEnabled);
            first?.Focus();
        };
        top.Children.Add(_search);
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);

        var close = Brand.Button("Close", Brand.Weight.Primary);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var buttons = Brand.ButtonRow(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);

        AutomationProperties.SetName(_list, "Favourites and everything you can pin");
        var scroll = new ScrollViewer
        {
            Content = _list, MaxHeight = 440, Margin = new Thickness(0, 10, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        body.Children.Add(scroll);

        var root = new DockPanel();
        var header = Brand.Header("Favourites", "Pin tools, recipes and learned tools to the My tools panel.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;
        Rebuild(null);
        Loaded += (_, _) => _search.Focus();
    }

    private IEnumerable<Button> Buttons() =>
        _list.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<StackPanel>()).SelectMany(p => p.Children.OfType<Button>());

    /// <summary>Redraws the lists; then focuses the button tagged <paramref name="focus"/> ("key|action"), or a close one.</summary>
    private void Rebuild(string? focus)
    {
        _list.Children.Clear();
        var pinned = FavouritesRibbon.Pinned;
        var catalog = FavouritesRibbon.Catalog;
        var full = pinned.Count >= Favourites.Max;

        _status.Text = (pinned.Count == 0 ? "Nothing is pinned yet." : $"{pinned.Count} of {Favourites.Max} pinned.") +
                       (FavouritesRibbon.IsLive
                           ? " Changes show on the My tools panel within a few seconds."
                           : " Your pins are saved. Restart Revit to see changes on the My tools panel.");
        _status.SetResourceReference(TextBlock.ForegroundProperty, FavouritesRibbon.IsLive ? Brand.K.Muted : Brand.K.WarningText);

        _list.Children.Add(Heading("Pinned, in ribbon order"));
        if (pinned.Count == 0)
            _list.Children.Add(Note("Pin something from the list below and it gets a button next to My tools."));
        for (var i = 0; i < pinned.Count; i++)
        {
            var key = pinned[i];
            var exists = catalog.TryGetValue(key, out var item);
            var (kind, name) = Favourites.Split(key);
            var title = exists ? item!.Text : name.Replace('_', ' ');
            var detail = exists ? Favourites.KindLabel(kind) : Favourites.KindLabel(kind) + " not found: skipped on the ribbon. It shows again if it comes back.";
            var up = RowButton("Up", $"Move {title} up", key + "|up", i > 0, () => FavouritesRibbon.Move(key, -1));
            var down = RowButton("Down", $"Move {title} down", key + "|down", i < pinned.Count - 1, () => FavouritesRibbon.Move(key, 1));
            // A pin whose item is gone can only be removed: red, never the default.
            var remove = exists
                ? RowButton("Unpin", $"Unpin {title}", key + "|unpin", true, () => FavouritesRibbon.SetPinned(key, false), refocus: key + "|pin")
                : RowButton("Remove", $"Remove {title} from favourites", key + "|unpin", true, () => FavouritesRibbon.SetPinned(key, false), Brand.Weight.Danger);
            _list.Children.Add(Row(title, detail, exists ? null : Brand.K.WarningText, up, down, remove));
        }

        var query = _search.Text.Trim();
        var matches = catalog.Values.Where(i => !pinned.Contains(i.Key) && Matches(i, query)).ToList();
        var heading = Heading(query.Length == 0 ? "Everything you can pin" : $"Matching \"{query}\"");
        heading.Margin = new Thickness(0, 14, 0, 4);
        _list.Children.Add(heading);
        if (full)
            _list.Children.Add(Note($"{Favourites.Max} are pinned, the most My tools holds. Unpin one above to pin another.", Brand.K.WarningText));
        if (matches.Count == 0)
            _list.Children.Add(Note(query.Length == 0
                ? "Everything is pinned already."
                : $"Nothing matches \"{query}\". Try another word, or clear the search."));
        foreach (var item in matches)
        {
            var pin = RowButton("Pin", $"Pin {item.Text}", item.Key + "|pin", !full, () => FavouritesRibbon.SetPinned(item.Key, true), refocus: item.Key + "|unpin");
            if (full)
            {
                pin.ToolTip = $"{Favourites.Max} are pinned already. Unpin one first.";
                ToolTipService.SetShowOnDisabled(pin, true);
            }
            _list.Children.Add(Row(item.Text, Favourites.KindLabel(item.Kind) + (item.Tip.Length > 0 ? ". " + Trim(item.Tip) : ""), null, pin));
        }

        if (focus is null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var target = Buttons().FirstOrDefault(b => b.IsEnabled && Equals(b.Tag, focus))
                         ?? Buttons().FirstOrDefault(b => b.IsEnabled && (b.Tag as string)?.StartsWith(focus.Split('|')[0] + "|", StringComparison.Ordinal) == true);
            if (target is not null) target.Focus();
            else _search.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static bool Matches(Favourites.Item item, string query) =>
        query.Length == 0 || item.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                          || item.Name.Replace('_', ' ').IndexOf(query.Replace('_', ' '), StringComparison.OrdinalIgnoreCase) >= 0
                          || Favourites.KindLabel(item.Kind).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Trim(string text) => text.Length <= 140 ? text : text.Substring(0, 139).TrimEnd() + "…";

    private Button RowButton(string text, string name, string tag, bool enabled, Func<string?> act, Brand.Weight weight = Brand.Weight.Secondary,
        string? refocus = null)
    {
        var button = Brand.Button(text, weight);
        button.MinWidth = 64;
        button.Tag = tag;
        button.IsEnabled = enabled;
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) =>
        {
            var problem = act();
            _problem.Text = problem ?? "";
            _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            Rebuild(refocus ?? tag);
        };
        return button;
    }

    private static Grid Row(string title, string detail, string? detailKey, params Button[] buttons)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        text.Children.Add(Brand.Text(title, bold: true));
        text.Children.Add(Brand.Text(detail, detailKey ?? Brand.K.Muted, size: 11));
        row.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in buttons) actions.Children.Add(b);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        AutomationProperties.SetName(row, title);
        return row;
    }

    private static TextBlock Heading(string text)
    {
        var heading = Brand.Text(text, bold: true, size: 13);
        heading.Margin = new Thickness(0, 0, 0, 4);
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
        return heading;
    }

    private static TextBlock Note(string text, string key = Brand.K.Muted)
    {
        var note = Brand.Text(text, key);
        note.Margin = new Thickness(0, 2, 0, 6);
        return note;
    }
}
