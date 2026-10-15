using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RevitUI = Autodesk.Revit.UI;

namespace SketchProMax.Loader;

/// <summary>
/// The "Sketch ProMax" dockable pane. Revit only accepts dockable panes while it starts, and the core is switched at
/// runtime, so the pane belongs to the loader: a WPF page that asks whichever core runs for its content as text
/// (Query("pane"), Query("claude"), Query("quick")) once a second while it is visible, and sends clicks back as core
/// commands through an external event, so they run in a valid Revit API context.
/// A core older than 0.27 answers "" to "pane": the pane then shows connection, version and quick tools and asks to update.
/// </summary>
internal static class PaneHost
{
    public static readonly RevitUI.DockablePaneId Id = new(new Guid("448734fe-1b1a-47e2-94a5-1fd138cb74e7"));
    public const string Title = "Sketch ProMax";

    private static PanePage? _page;
    private static RevitUI.ExternalEvent? _event;
    private static readonly PaneRequests Handler = new();

    public static bool Registered { get; private set; }

    /// <summary>The page, created on first use (Revit asks for it when the pane is first shown).</summary>
    public static PanePage Page => _page ??= new PanePage();

    /// <summary>True while the pane can be seen (shown and, if tabbed, the front tab).</summary>
    public static bool Visible => _page?.IsVisible == true;

    /// <summary>Registers the pane. Only works during OnStartup.</summary>
    public static void Register(RevitUI.UIControlledApplication app)
    {
        try
        {
            app.RegisterDockablePane(Id, Title, new Provider());
            Registered = true;
        }
        catch (Exception) { Registered = false; /* already registered by another copy, or too late: the button says so */ }
        try { app.DockableFrameVisibilityChanged += (_, e) => { if (e.PaneId == Id) Ribbon.SetPaneShown(e.DockableFrameShown); }; }
        catch (Exception) { }
        EnsureEvent();
    }

    /// <summary>Creates the external event the pane's clicks go through. Retried from Idling if Revit refused at startup.</summary>
    public static void EnsureEvent()
    {
        if (_event is not null) return;
        try { _event = RevitUI.ExternalEvent.Create(Handler); }
        catch (Exception) { _event = null; }
    }

    /// <summary>Runs a core command (or a loader one such as a quick tool) on Revit's thread, when Revit is ready.</summary>
    public static void Request(string name)
    {
        EnsureEvent();
        if (_event is null)
        {
            RevitUI.TaskDialog.Show(Title, "Revit is not ready for this yet. Try again in a moment.");
            return;
        }
        Handler.Enqueue(name);
        try { _event.Raise(); }
        catch (Exception) { /* Revit is closing */ }
    }

    /// <summary>Shows or hides the pane (the ribbon's Pane button). A pane tabbed behind another is brought to the front.</summary>
    public static int Toggle(RevitUI.UIApplication app)
    {
        RevitUI.DockablePane? pane = null;
        try { pane = app.GetDockablePane(Id); }
        catch (Exception) { }
        if (pane is null)
        {
            RevitUI.TaskDialog.Show(Title, "The Sketch ProMax pane could not be set up in this Revit session. Restart Revit to get it.");
            return -1;
        }
        var hide = pane.IsShown() && Visible;
        if (hide) pane.Hide();
        else pane.Show();
        Ribbon.SetPaneShown(!hide);
        return 0;
    }

    private sealed class Provider : RevitUI.IDockablePaneProvider
    {
        public void SetupDockablePane(RevitUI.DockablePaneProviderData data)
        {
            data.FrameworkElement = Page;
            data.VisibleByDefault = false;
            data.InitialState = new RevitUI.DockablePaneState
            {
                DockPosition = RevitUI.DockPosition.Tabbed,
                TabBehind = RevitUI.DockablePanes.BuiltInDockablePanes.ProjectBrowser,
            };
        }
    }

    /// <summary>The pane's queued clicks, run by Revit through the external event.</summary>
    private sealed class PaneRequests : RevitUI.IExternalEventHandler
    {
        private readonly Queue<string> _queue = new();

        public void Enqueue(string name)
        {
            lock (_queue)
            {
                if (_queue.Count < 20 && !_queue.Contains(name)) _queue.Enqueue(name);
            }
        }

        public void Execute(RevitUI.UIApplication app)
        {
            while (true)
            {
                string name;
                lock (_queue)
                {
                    if (_queue.Count == 0) break;
                    name = _queue.Dequeue();
                }
                try { Host.Dispatch(name, app); }
                catch (Exception ex)
                {
                    if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
                    RevitUI.TaskDialog.Show(Title, $"That didn't work: {ex.Message}\n\nIf it keeps happening, send this to Claude: {ex.GetType().Name} in {name}.");
                }
            }
            Page.RefreshSoon();
        }

        public string GetName() => "Sketch ProMax pane";
    }
}

/// <summary>Brand colours (the same as the core's UI.Brand; the loader can't reference the core).</summary>
internal static class PaneBrand
{
    public static readonly Color Blue = Color.FromRgb(0x1D, 0x4E, 0xD8);
    public static readonly Color Navy = Color.FromRgb(0x0B, 0x1E, 0x4F);
    public static readonly Color Amber = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color Green = Color.FromRgb(0x16, 0xA3, 0x4A);
    public static readonly Color Red = Color.FromRgb(0xDC, 0x26, 0x26);
    public static readonly Color Muted = Color.FromRgb(0x64, 0x74, 0x8B);
    public static readonly Color Ink = Color.FromRgb(0x0F, 0x17, 0x2A);
    public static readonly Color Paper = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static readonly Color Soft = Color.FromRgb(0xF1, 0xF5, 0xF9);
    public static readonly Color Line = Color.FromRgb(0xE2, 0xE8, 0xF0);

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>The pane's content. Built in code (no XAML), rebuilt section by section only when that section's text changed.</summary>
internal sealed class PanePage : Page
{
    private static readonly Regex Digits = new(@"\d{2,}", RegexOptions.CultureInvariant);
    private static readonly FontFamily Mono = new("Consolas, Courier New");

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Ellipse _light = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _connection = new() { Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _version = new() { Foreground = PaneBrand.Brush(Color.FromRgb(0xC7, 0xD2, 0xFE)), FontSize = 11 };
    private readonly StackPanel _notice = new();
    private readonly StackPanel _job = new();
    private readonly StackPanel _log = new();
    private readonly ScrollViewer _logScroll;
    private readonly StackPanel _last = new();
    private readonly StackPanel _quick = new();
    private readonly Border _logBox, _lastBox;
    private readonly Dictionary<FrameworkElement, string> _shown = new();
    private int _ticks;

    public PanePage()
    {
        Title = PaneHost.Title;
        Background = PaneBrand.Brush(PaneBrand.Paper);
        TextElement.SetForeground(this, PaneBrand.Brush(PaneBrand.Ink));
        TextElement.SetFontSize(this, 12);

        var root = new StackPanel();
        root.Children.Add(Header());
        var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        body.Children.Add(_notice);
        body.Children.Add(Section("Current job", _job));
        _logScroll = new ScrollViewer { Content = _log, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        _logBox = Section("Steps", Box(_logScroll));
        body.Children.Add(_logBox);
        _lastBox = Section("Last job", _last);
        body.Children.Add(_lastBox);
        body.Children.Add(Section("Quick tools", _quick));
        root.Children.Add(body);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Ribbon.SetPaneShown(true);
                Refresh();
                _timer.Start();
            }
            else _timer.Stop();
        };
    }

    /// <summary>After a click ran: show its effect without waiting for the next tick.</summary>
    public void RefreshSoon()
    {
        if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
    }

    private UIElement Header()
    {
        var gradient = new LinearGradientBrush(PaneBrand.Blue, PaneBrand.Navy, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        var row = new DockPanel { LastChildFill = true };
        var logo = new Image { Source = Icons.Logo(32), Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(logo, Dock.Left);
        row.Children.Add(logo);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new DockPanel();
        var version = _version;
        version.VerticalAlignment = VerticalAlignment.Bottom;
        version.Margin = new Thickness(6, 0, 0, 1);
        DockPanel.SetDock(version, Dock.Right);
        title.Children.Add(version);
        title.Children.Add(new TextBlock { Text = "Sketch ProMax", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold });
        text.Children.Add(title);
        var status = new DockPanel();
        DockPanel.SetDock(_light, Dock.Left);
        status.Children.Add(_light);
        status.Children.Add(_connection);
        text.Children.Add(status);
        row.Children.Add(text);
        return new Border { Background = gradient, Padding = new Thickness(10, 8, 10, 8), Child = row };
    }

    private static Border Section(string title, UIElement content)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            Foreground = PaneBrand.Brush(PaneBrand.Muted), Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(content);
        return new Border { Child = panel };
    }

    private static Border Box(UIElement content) => new()
    {
        Background = PaneBrand.Brush(PaneBrand.Soft), BorderBrush = PaneBrand.Brush(PaneBrand.Line), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 4, 6, 4), Child = content,
    };

    private static TextBlock Text(string text, Color? color = null, bool bold = false, double size = 12) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size,
        Foreground = PaneBrand.Brush(color ?? PaneBrand.Ink), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };

    /// <summary>Rebuilds <paramref name="panel"/> only when <paramref name="key"/> differs from what it shows.</summary>
    private bool Changed(FrameworkElement panel, string key)
    {
        if (_shown.TryGetValue(panel, out var was) && was == key) return false;
        _shown[panel] = key;
        return true;
    }

    private void Refresh()
    {
        try { Render(); }
        catch (Exception ex)
        {
            // Never let a pane refresh reach Revit; a core being switched answers again on the next tick.
            if (Changed(_notice, "error" + ex.Message))
            {
                _notice.Children.Clear();
                _notice.Children.Add(Text("The pane could not refresh: " + ex.Message, PaneBrand.Red));
            }
        }
    }

    private void Render()
    {
        _ticks++;
        if (Host.Core is not { } core)
        {
            var failed = Host.BootError;
            SetConnection(failed is null ? "starting" : "error", failed is null ? "Starting…" : "Not started", "");
            Notice(failed ?? "Sketch ProMax starts as soon as Revit is ready.", failed is null ? PaneBrand.Muted : PaneBrand.Red);
            ShowJob(new Dictionary<string, List<string[]>>(), new HashSet<string>(), hasPane: false);
            ShowQuick("");
            return;
        }

        var snapshot = Host.Query("pane");
        var records = Parse(snapshot);
        var claude = records.TryGetValue("claude", out var c) && c.Count > 0 ? c[0] : Host.Query("claude").Split('\t');
        var version = records.TryGetValue("version", out var v) && v.Count > 0 && v[0].Length > 0 ? v[0][0] : core.VersionText;
        _version.Text = version;
        SetConnection(claude.Length > 0 ? claude[0] : "off", claude.Length > 1 ? claude[1].Replace('\n', ' ') : "", claude.Length > 2 ? claude[2] : "");

        if (snapshot.Length == 0)
        {
            Notice($"Update Sketch ProMax to see details here: version {core.VersionText} doesn't send the running job, its steps " +
                   "or element links to this pane yet. Use Check now in the Updates panel.", PaneBrand.Muted);
            ShowJob(new Dictionary<string, List<string[]>>(), new HashSet<string>(), hasPane: false);
        }
        else
        {
            Notice(null, PaneBrand.Muted);
            ShowJob(records, Ids(records), hasPane: true);
        }
        // The quick tools list rarely changes; the core rereads its folders only when they changed.
        if (_ticks % 5 == 1 || !_shown.ContainsKey(_quick)) ShowQuick(Host.Query("quick"));
    }

    private static Dictionary<string, List<string[]>> Parse(string snapshot)
    {
        var records = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        foreach (var line in snapshot.Split('\n'))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (!records.TryGetValue(parts[0], out var list)) records[parts[0]] = list = new List<string[]>();
            list.Add(parts.Skip(1).ToArray());
        }
        return records;
    }

    private static HashSet<string> Ids(Dictionary<string, List<string[]>> records)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (records.TryGetValue("ids", out var ids))
            foreach (var row in ids)
                foreach (var id in (row.Length > 0 ? row[0] : "").Split(','))
                    if (id.Length > 0) set.Add(id);
        return set;
    }

    private void SetConnection(string state, string text, string detail)
    {
        var color = state switch
        {
            "connected" => Icons.Green, "ready" => Icons.Amber, "error" => Icons.Red, _ => Icons.Gray,
        };
        if (!Changed(_light, state)) { _connection.Text = "Claude: " + text; _connection.ToolTip = detail.Length > 0 ? detail : null; return; }
        _light.Fill = PaneBrand.Brush(color);
        _light.Stroke = Brushes.White;
        _light.StrokeThickness = 1;
        _connection.Text = "Claude: " + text;
        _connection.ToolTip = detail.Length > 0 ? detail : null;
    }

    private void Notice(string? text, Color color)
    {
        if (!Changed(_notice, text ?? "")) return;
        _notice.Children.Clear();
        if (text is null) return;
        _notice.Children.Add(new Border
        {
            Background = PaneBrand.Brush(Color.FromRgb(0xFF, 0xF7, 0xE0)), BorderBrush = PaneBrand.Brush(PaneBrand.Amber),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6, 8, 6),
            Child = Text(text, color == PaneBrand.Muted ? PaneBrand.Ink : color),
        });
    }

    private static string Field(string[] row, int i) => i < row.Length ? row[i] : "";

    private void ShowJob(Dictionary<string, List<string[]>> records, HashSet<string> ids, bool hasPane)
    {
        // Current job.
        var job = records.TryGetValue("job", out var j) && j.Count > 0 ? j[0] : new string[0];
        var state = Field(job, 0);
        if (Changed(_job, hasPane + "\u0001" + string.Join("\u0001", job)))
        {
            _job.Children.Clear();
            if (!hasPane) _job.Children.Add(Text("—", PaneBrand.Muted));
            else if (state is "running" or "waiting" or "selftest")
            {
                var head = new DockPanel();
                var cancel = new Button
                {
                    Content = "Cancel", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0),
                    Background = PaneBrand.Brush(PaneBrand.Red), Foreground = Brushes.White, BorderBrush = PaneBrand.Brush(PaneBrand.Red),
                    ToolTip = "Ask it to stop at its next check (like cancel_job). Batch tools keep what they finished as one Undo step.",
                    VerticalAlignment = VerticalAlignment.Top,
                };
                cancel.Click += (_, _) => { cancel.IsEnabled = false; Host.PaneCancel(); RefreshSoon(); };
                DockPanel.SetDock(cancel, Dock.Right);
                head.Children.Add(cancel);
                var title = new StackPanel();
                var name = state == "selftest" ? "Self-test" : $"{Field(job, 2)}  (job {Field(job, 1)})";
                title.Children.Add(Text(name, PaneBrand.Navy, bold: true, size: 13));
                var when = state == "waiting" ? "waiting for Revit" : Field(job, 3).Length > 0 ? "running for " + Field(job, 3) : "running";
                title.Children.Add(Text(when, PaneBrand.Muted, size: 11));
                head.Children.Add(title);
                _job.Children.Add(head);
                if (Field(job, 4).Length > 0)
                {
                    var progress = Text(Field(job, 4));
                    progress.Margin = new Thickness(0, 4, 0, 0);
                    _job.Children.Add(progress);
                }
                _job.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 6, 0, 0), Foreground = PaneBrand.Brush(PaneBrand.Blue), BorderThickness = new Thickness(0) });
            }
            else _job.Children.Add(Text("Nothing is running. Tools Claude runs in Revit show here with their progress.", PaneBrand.Muted));
        }

        // Step log.
        var log = records.TryGetValue("log", out var l) ? l.Select(r => Field(r, 0)).ToList() : new List<string>();
        _logBox.Visibility = log.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Changed(_log, string.Join("\n", log) + "\u0001" + string.Join(",", ids)))
        {
            var atEnd = _logScroll.VerticalOffset >= _logScroll.ScrollableHeight - 4;
            _log.Children.Clear();
            foreach (var line in log) _log.Children.Add(Linked(line, ids, mono: true));
            if (atEnd) _logScroll.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(_logScroll.ScrollToEnd));
        }

        // Last finished job.
        var last = records.TryGetValue("last", out var la) && la.Count > 0 ? la[0] : null;
        var result = records.TryGetValue("result", out var re) ? re.Select(r => Field(r, 0)).ToList() : new List<string>();
        _lastBox.Visibility = last is not null ? Visibility.Visible : Visibility.Collapsed;
        if (last is not null && Changed(_last, string.Join("\u0001", last) + "\n" + string.Join("\n", result) + "\u0001" + string.Join(",", ids)))
        {
            _last.Children.Clear();
            var outcome = Field(last, 2);
            var (label, color) = outcome switch
            {
                "ok" => ("worked", PaneBrand.Green),
                "stopped" => ("stopped on request", PaneBrand.Amber),
                "cancelled" => ("cancelled", PaneBrand.Muted),
                _ => ("did not work", PaneBrand.Red),
            };
            var head = new WrapPanel();
            head.Children.Add(Text($"{Field(last, 1)}  ", PaneBrand.Navy, bold: true, size: 13));
            head.Children.Add(new Border
            {
                Background = PaneBrand.Brush(color), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 11 },
            });
            _last.Children.Add(head);
            var when = $"job {Field(last, 0)}" + (Field(last, 3).Length > 0 ? $", finished {Field(last, 3)}" : "") +
                       (Field(last, 4).Length > 0 ? $" after {Field(last, 4)}" : "");
            _last.Children.Add(Text(when, PaneBrand.Muted, size: 11));
            if (result.Count > 0)
            {
                var lines = new StackPanel();
                foreach (var line in result) lines.Children.Add(Linked(line, ids, mono: true));
                var box = Box(new ScrollViewer { Content = lines, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
                box.Margin = new Thickness(0, 4, 0, 0);
                _last.Children.Add(box);
            }
        }
        else if (last is null) _shown.Remove(_last);

        // Select all the element ids found.
        if (ids.Count > 1 && last is not null && _last.Children.Count > 0 && _last.Children[_last.Children.Count - 1] is not TextBlock { Tag: "all" })
        {
            var all = new TextBlock { Tag = "all", Margin = new Thickness(0, 6, 0, 0) };
            var link = new Hyperlink(new Run($"Select all {ids.Count} elements")) { Foreground = PaneBrand.Brush(PaneBrand.Blue) };
            var list = string.Join(",", ids);
            link.Click += (_, _) => PaneHost.Request("pane-select\t" + list);
            all.Inlines.Add(link);
            all.Inlines.Add(new Run("  (found in the steps and result)") { Foreground = PaneBrand.Brush(PaneBrand.Muted) });
            _last.Children.Add(all);
        }
    }

    /// <summary>A line with every known element id in it as a link that selects and zooms to that element.</summary>
    private static TextBlock Linked(string line, HashSet<string> ids, bool mono)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.NoWrap, FontSize = mono ? 11 : 12 };
        if (mono) block.FontFamily = Mono;
        var at = 0;
        if (ids.Count > 0)
            foreach (Match m in Digits.Matches(line))
            {
                if (!ids.Contains(m.Value)) continue;
                // Whole numbers only: not the tail of 12.345 or of a longer token.
                if (m.Index > 0 && (char.IsLetterOrDigit(line[m.Index - 1]) || line[m.Index - 1] == '.')) continue;
                if (m.Index > at) block.Inlines.Add(new Run(line.Substring(at, m.Index - at)));
                var id = m.Value;
                var link = new Hyperlink(new Run(id)) { Foreground = PaneBrand.Brush(PaneBrand.Blue), ToolTip = $"Select element {id} and zoom to it" };
                link.Click += (_, _) => PaneHost.Request("pane-select\t" + id);
                block.Inlines.Add(link);
                at = m.Index + m.Length;
            }
        if (at < line.Length) block.Inlines.Add(new Run(line.Substring(at)));
        return block;
    }

    private void ShowQuick(string list)
    {
        if (!Changed(_quick, list)) return;
        _quick.Children.Clear();
        var items = list.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('\t')).Where(p => p.Length >= 3).ToList();
        if (items.Count == 0)
        {
            _quick.Children.Add(Text(Host.Core is null
                ? "Your saved recipes and learned tools show here once Sketch ProMax has started."
                : "Nothing saved yet. In the Claude app, after a workflow you liked, say \"save that as a recipe\"; when Claude wrote " +
                  "code that worked, say \"save it as a tool\". It then appears here and runs in Revit without the AI.", PaneBrand.Muted));
            return;
        }
        foreach (var p in items)
        {
            var (kind, name, summary) = (p[0], p[1], p[2]);
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 0, 4),
                Background = PaneBrand.Brush(PaneBrand.Soft), BorderBrush = PaneBrand.Brush(PaneBrand.Line),
                ToolTip = (kind == "recipe" ? "Recipe. " : "Learned tool. ") + summary,
            };
            var row = new DockPanel();
            var icon = new Image { Source = Icons.Get(kind == "recipe" ? "recipe" : "learned", 16), Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = Pretty(name), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            button.Content = row;
            var key = $"quick\t{kind}\t{name}";
            button.Click += (_, _) => PaneHost.Request(key);
            _quick.Children.Add(button);
        }
    }

    private static string Pretty(string name)
    {
        var text = name.Replace('_', ' ').Trim();
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
