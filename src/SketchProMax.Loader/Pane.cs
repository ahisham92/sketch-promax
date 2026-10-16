using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;
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
        // Follow Revit's light/dark UI theme live (Revit 2024+); the page also rechecks whenever it is shown.
        try { app.ThemeChanged += (_, _) => _page?.ApplyTheme(); }
        catch (Exception) { /* no theme event: the page still picks the theme up when it is next shown */ }
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

    /// <summary>Shows the pane with the Chat tab in front (the ribbon's Chat button). Without a chat, just shows the pane.</summary>
    public static int ShowChat(RevitUI.UIApplication app)
    {
        RevitUI.DockablePane? pane = null;
        try { pane = app.GetDockablePane(Id); }
        catch (Exception) { }
        if (pane is null)
        {
            RevitUI.TaskDialog.Show(Title, "The Sketch ProMax pane could not be set up in this Revit session. Restart Revit to get it.");
            return -1;
        }
        pane.Show();
        Ribbon.SetPaneShown(true);
        Page.SelectChat();
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

/// <summary>
/// The pane's content. Built in code (no XAML) and themed with <see cref="Theme"/>: it follows Revit's light/dark UI
/// theme live. Lists are rebuilt only when their text changed; the current job's controls are built once and updated
/// in place, so keyboard focus on Cancel survives the once-a-second refresh. Nothing scrolls sideways: text wraps,
/// long names end in an ellipsis with the full text in a tooltip.
///
/// When the running core provides a chat (CoreEntry.PaneContent("chat"), cores from 0.29 on), the page shows two tabs
/// under the header: Chat (the core's element) and Activity (the view below). After a version switch the old core's
/// element is removed and the new core is asked again; an older core, or none, gets the Activity view alone.
/// </summary>
internal sealed class PanePage : Page
{
    private static readonly Regex Digits = new(@"\d{2,}", RegexOptions.CultureInvariant);
    private static readonly FontFamily Mono = new("Consolas, Courier New");
    private const string K_Text = Theme.K.Text, K_Muted = Theme.K.Muted;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Ellipse _light = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _connection = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _version = new() { FontSize = 11 };
    private readonly StackPanel _notice = new();
    private readonly StackPanel _job = new();
    private readonly TextBlock _jobIdle = Text("", K_Muted);
    private readonly StackPanel _jobActive = new();
    private readonly TextBlock _jobName = Text("", K_Text, bold: true, size: 13);
    private readonly TextBlock _jobWhen = Text("", K_Muted, size: 11);
    private readonly TextBlock _jobProgress = Text("");
    private readonly Button _cancel;
    private string _jobKey = "", _cancelFor = "";
    private readonly StackPanel _log = new();
    private readonly ScrollViewer _logScroll;
    private readonly StackPanel _last = new();
    private readonly StackPanel _quick = new();
    private readonly Border _logBox, _lastBox;
    private readonly Dictionary<FrameworkElement, string> _shown = new();
    private int _ticks;

    // Tabs: shown only while the running core provides a chat.
    private readonly DockPanel _root = new();
    private readonly ScrollViewer _activity;
    private readonly ContentControl _single = new() { Focusable = false };
    private readonly TabControl _tabs = new();
    private readonly TabItem _chatTab = new() { Header = "Chat" };
    private readonly TabItem _activityTab = new() { Header = "Activity" };
    private readonly TabItem _notesTab = new() { Header = "Notes" };
    private CoreHandle? _chatCore;
    private bool _chatAsked, _wantChat;

    public PanePage()
    {
        Title = PaneHost.Title;
        Theme.Use(this);
        SetResourceReference(BackgroundProperty, Theme.K.Bg);
        SetResourceReference(TextElement.ForegroundProperty, Theme.K.Text);
        TextElement.SetFontSize(this, 12);

        // Current job: built once, updated in place.
        _cancel = Theme.Button("Cancel job", "SpmDangerButton");
        _cancel.VerticalAlignment = VerticalAlignment.Top;
        _cancel.Margin = new Thickness(8, 0, 0, 0);
        _cancel.ToolTip = "Stops the running job at its next check (like cancel_job). Batch tools keep what they finished as one Undo step.";
        AutomationProperties.SetName(_cancel, "Cancel the running job");
        _cancel.Click += (_, _) =>
        {
            if (Theme.IsBusy(_cancel)) return;
            _cancelFor = _jobKey;
            Theme.SetBusy(_cancel, true, "Stopping…");
            Host.PaneCancel();
            RefreshSoon();
        };
        var head = new DockPanel();
        DockPanel.SetDock(_cancel, Dock.Right);
        head.Children.Add(_cancel);
        var title = new StackPanel();
        title.Children.Add(_jobName);
        title.Children.Add(_jobWhen);
        head.Children.Add(title);
        _jobActive.Children.Add(head);
        _jobProgress.Margin = new Thickness(0, 4, 0, 0);
        _jobActive.Children.Add(_jobProgress);
        var bar = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(bar, "Job running");
        _jobActive.Children.Add(bar);
        _job.Children.Add(_jobIdle);
        _job.Children.Add(_jobActive);
        _jobActive.Visibility = Visibility.Collapsed;

        var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        body.Children.Add(_notice);
        body.Children.Add(Section("Current job", _job));
        _logScroll = new ScrollViewer
        {
            Content = _log, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        _logBox = Section("Steps", Box(_logScroll));
        body.Children.Add(_logBox);
        _lastBox = Section("Last job", _last);
        body.Children.Add(_lastBox);
        body.Children.Add(Section("Quick tools", _quick));
        // One small zip of the whole project, to upload to a Claude chat for a report (cores from 0.29 on).
        var export = Theme.Button(new TextBlock { Text = "Export for Claude", TextWrapping = TextWrapping.Wrap });
        export.Margin = new Thickness(0, 6, 0, 0);
        export.MinHeight = 28;
        export.HorizontalAlignment = HorizontalAlignment.Stretch;
        export.ToolTip = "Writes one small zip describing this project (quantities, sheets, revisions, schedules, latest checks) " +
                         "to upload to a Claude chat. The folder opens with the zip selected.";
        AutomationProperties.SetName(export, "Export for Claude");
        export.Click += (_, _) => PaneHost.Request("export-pack");
        body.Children.Add(Section("Share", export));
        // Bug reports (cores from 0.30 on): one small zip each, in Documents\Sketch ProMax\Bugs.
        var bugs = new StackPanel();
        var report = Theme.Button(new TextBlock { Text = "Report bug", TextWrapping = TextWrapping.Wrap });
        report.MinHeight = 28;
        report.HorizontalAlignment = HorizontalAlignment.Stretch;
        report.ToolTip = "Asks for one line on what went wrong and saves a small zip with a picture of the Revit window, the versions, " +
                         "the model's name and the last tool calls. Nothing is sent. In the chat, just say \"that's a bug\".";
        AutomationProperties.SetName(report, "Report bug");
        report.Click += (_, _) => PaneHost.Request("report-bug");
        bugs.Children.Add(report);
        var folder = Theme.Button(new TextBlock { Text = "Open bug folder", TextWrapping = TextWrapping.Wrap });
        folder.Margin = new Thickness(0, 6, 0, 0);
        folder.MinHeight = 28;
        folder.HorizontalAlignment = HorizontalAlignment.Stretch;
        folder.ToolTip = "Opens Documents\\Sketch ProMax\\Bugs: your reports, and Bug log.md with every known bug, its status and your reports not sent yet.";
        AutomationProperties.SetName(folder, "Open bug folder");
        folder.Click += (_, _) => PaneHost.Request("bug-folder");
        bugs.Children.Add(folder);
        body.Children.Add(Section("Bugs", bugs));
        _activity = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        var header = Header();
        DockPanel.SetDock(header, Dock.Top);
        _root.Children.Add(header);
        _single.Content = _activity;
        _root.Children.Add(_single);
        _tabs.Resources.MergedDictionaries.Add(TabStyles());
        _tabs.Items.Add(_chatTab);
        _tabs.Items.Add(_activityTab);
        AutomationProperties.SetName(_tabs, "Sketch ProMax pane");
        AutomationProperties.SetName(_chatTab, "Chat with Claude");
        AutomationProperties.SetName(_activityTab, "Activity: connection, running job and quick tools");
        AutomationProperties.SetName(_notesTab, "Notes Claude remembers for you");
        // The adorner layer inside the page keeps keyboard focus rings in reach of the page's theme resources.
        Content = new AdornerDecorator { Child = _root };

        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                ApplyTheme();
                Ribbon.SetPaneShown(true);
                Refresh();
                _timer.Start();
            }
            else _timer.Stop();
        };
    }

    /// <summary>Switches the palette to Revit's current theme (from the ThemeChanged event, and whenever the pane is shown).</summary>
    public void ApplyTheme()
    {
        if (Dispatcher.CheckAccess()) Theme.Use(this);
        else Dispatcher.BeginInvoke(new Action(() => Theme.Use(this)));
    }

    /// <summary>After a click ran: show its effect without waiting for the next tick.</summary>
    public void RefreshSoon()
    {
        if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
    }

    private UIElement Header()
    {
        // The brand band: the same blue in both themes, white text on it.
        var gradient = new LinearGradientBrush(Theme.Blue, Theme.Navy, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        var row = new DockPanel { LastChildFill = true };
        var logo = new Image { Source = Icons.Logo(32), Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(logo, "Sketch ProMax");
        DockPanel.SetDock(logo, Dock.Left);
        row.Children.Add(logo);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new DockPanel();
        var version = Theme.Fg(_version, Theme.K.HeaderSub);
        version.VerticalAlignment = VerticalAlignment.Bottom;
        version.Margin = new Thickness(6, 0, 0, 1);
        DockPanel.SetDock(version, Dock.Right);
        title.Children.Add(version);
        title.Children.Add(Theme.Fg(new TextBlock
        {
            Text = "Sketch ProMax", FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        }, Theme.K.HeaderText));
        text.Children.Add(title);
        var status = new DockPanel();
        DockPanel.SetDock(_light, Dock.Left);
        status.Children.Add(_light);
        status.Children.Add(Theme.Fg(_connection, Theme.K.HeaderText));
        text.Children.Add(status);
        row.Children.Add(text);
        return new Border { Background = gradient, Padding = new Thickness(10, 8, 10, 8), Child = row };
    }

    private static Border Section(string title, UIElement content)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var heading = Text(title.ToUpperInvariant(), K_Muted, bold: true, size: 10.5);
        heading.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(heading);
        panel.Children.Add(content);
        return new Border { Child = panel };
    }

    private static Border Box(UIElement content)
    {
        var box = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 4, 6, 4), Child = content,
        };
        box.SetResourceReference(Border.BackgroundProperty, Theme.K.Surface);
        box.SetResourceReference(Border.BorderBrushProperty, Theme.K.Border);
        return box;
    }

    private static TextBlock Text(string text, string key = K_Text, bool bold = false, double size = 12) => Theme.Fg(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    }, key);

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
            Notice("The pane could not refresh: " + ex.Message + "\nIt tries again every second. If this stays, hide the pane " +
                   "and show it again (Show pane on the Sketch ProMax tab).", Theme.K.DangerText);
        }
    }

    /// <summary>Brings the Chat tab to the front (now, or as soon as the core provides it).</summary>
    public void SelectChat()
    {
        _wantChat = true;
        if (_tabs.Parent is not null) _tabs.SelectedItem = _chatTab;
        RefreshSoon();
    }

    /// <summary>
    /// Asks the running core for its chat element when the core changed (first start, version switch): with one, the
    /// page shows the Chat and Activity tabs; without one (an older core, or none yet), the Activity view alone.
    /// </summary>
    private void UpdateChat(CoreHandle? core)
    {
        if (_chatAsked && ReferenceEquals(core, _chatCore)) return;
        _chatAsked = true;
        _chatCore = core;
        _chatTab.Content = null; // the old core's element, if any: it was stopped with its core
        _notesTab.Content = null;
        var chat = core?.PaneContent("chat");
        if (chat is not null && chat.Parent is ContentControl { } holder && !ReferenceEquals(holder, _chatTab)) holder.Content = null;
        // Notes (cores from 0.30 on): what Claude remembers for the user. An older core has none: no Notes tab.
        var notes = chat is null ? null : core?.PaneContent("notes");
        if (notes is not null && notes.Parent is ContentControl { } notesHolder && !ReferenceEquals(notesHolder, _notesTab)) notesHolder.Content = null;
        _notesTab.Content = notes;
        if (notes is not null && !_tabs.Items.Contains(_notesTab)) _tabs.Items.Add(_notesTab);
        else if (notes is null && _tabs.Items.Contains(_notesTab)) _tabs.Items.Remove(_notesTab);
        var tabs = chat is not null;
        if (tabs)
        {
            _chatTab.Content = chat;
            if (_tabs.Parent is null)
            {
                _single.Content = null;
                _activityTab.Content = _activity;
                _root.Children.Remove(_single);
                _root.Children.Add(_tabs);
                _tabs.SelectedItem = _chatTab; // the chat is what the pane is for once it exists
            }
            if (_wantChat) _tabs.SelectedItem = _chatTab;
        }
        else if (_tabs.Parent is not null)
        {
            _activityTab.Content = null;
            _root.Children.Remove(_tabs);
            _single.Content = _activity;
            _root.Children.Add(_single);
        }
        _wantChat = false;
    }

    /// <summary>Tab styles in the palette (the stock ones paint fixed light colours). Never fails: plain tabs then.</summary>
    private static ResourceDictionary TabStyles()
    {
        const string xaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="TabControl">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="Padding" Value="0"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabControl">
                    <DockPanel KeyboardNavigation.TabNavigation="Local">
                      <Border DockPanel.Dock="Top" BorderThickness="0,0,0,1" BorderBrush="{DynamicResource SpmBorder}" Padding="6,4,6,0">
                        <TabPanel IsItemsHost="True" KeyboardNavigation.TabIndex="1"/>
                      </Border>
                      <ContentPresenter x:Name="PART_SelectedContentHost" ContentSource="SelectedContent" KeyboardNavigation.TabIndex="2"/>
                    </DockPanel>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
              <Style TargetType="TabItem">
                <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
                <Setter Property="FocusVisualStyle" Value="{DynamicResource SpmFocusVisual}"/>
                <Setter Property="MinHeight" Value="30"/>
                <Setter Property="MinWidth" Value="64"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabItem">
                    <Border x:Name="bd" Background="Transparent" BorderThickness="0,0,0,2" BorderBrush="Transparent" Padding="12,5,12,4" Margin="0,0,2,0">
                      <ContentPresenter ContentSource="Header" HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/>
                        <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                      </Trigger>
                      <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmAccent}"/>
                        <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                        <Setter Property="FontWeight" Value="SemiBold"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
            </ResourceDictionary>
            """;
        try { return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml); }
        catch (Exception) { return new ResourceDictionary(); }
    }

    private void Render()
    {
        _ticks++;
        UpdateChat(Host.Core);
        if (Host.Core is not { } core)
        {
            var failed = Host.BootError;
            SetConnection(failed is null ? "starting" : "error", failed is null ? "Starting…" : "Not started", "");
            Notice(failed is null
                    ? "Sketch ProMax starts as soon as Revit is ready. Nothing to do; this pane fills in by itself."
                    : failed + (failed.Contains("Install.cmd") ? "" : "\nRestart Revit. If it still doesn't start, run Install.cmd again."),
                failed is null ? K_Text : Theme.K.DangerText);
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
                   "or element links to this pane yet. Use Check now in the Updates panel.", K_Text);
            ShowJob(new Dictionary<string, List<string[]>>(), new HashSet<string>(), hasPane: false);
        }
        else
        {
            Notice(null, K_Text);
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
        _connection.Text = "Claude: " + text;
        // The line ends in an ellipsis in a narrow pane: the tooltip always has all of it.
        _connection.ToolTip = detail.Length > 0 ? "Claude: " + text + "\n" + detail : "Claude: " + text;
        AutomationProperties.SetName(_connection, "Claude: " + text + (detail.Length > 0 ? ". " + detail : ""));
        if (!Changed(_light, state)) return;
        var color = state switch
        {
            "connected" => Icons.Green, "ready" => Icons.Amber, "error" => Icons.Red, _ => Icons.Gray,
        };
        _light.Fill = Theme.Brush(color);
        _light.Stroke = Brushes.White;
        _light.StrokeThickness = 1;
    }

    private void Notice(string? text, string key)
    {
        if (!Changed(_notice, (text ?? "") + "\u0001" + key)) return;
        _notice.Children.Clear();
        if (text is null) return;
        var box = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6, 8, 6), Child = Text(text, key),
        };
        box.SetResourceReference(Border.BackgroundProperty, Theme.K.NoticeBg);
        box.SetResourceReference(Border.BorderBrushProperty, Theme.K.NoticeBorder);
        AutomationProperties.SetLiveSetting(box, AutomationLiveSetting.Polite);
        _notice.Children.Add(box);
    }

    private static string Field(string[] row, int i) => i < row.Length ? row[i] : "";

    private void ShowJob(Dictionary<string, List<string[]>> records, HashSet<string> ids, bool hasPane)
    {
        // Current job: update the controls in place.
        var job = records.TryGetValue("job", out var j) && j.Count > 0 ? j[0] : new string[0];
        var state = Field(job, 0);
        var active = hasPane && state is "running" or "waiting" or "selftest";
        _jobActive.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _jobIdle.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (active)
        {
            _jobKey = state == "selftest" ? "selftest" : Field(job, 1);
            _jobName.Text = state == "selftest" ? "Self-test" : $"{Field(job, 2)}  (job {Field(job, 1)})";
            _jobWhen.Text = state == "waiting" ? "waiting for Revit" : Field(job, 3).Length > 0 ? "running for " + Field(job, 3) : "running";
            _jobProgress.Text = Field(job, 4);
            _jobProgress.Visibility = Field(job, 4).Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            // A stop asked for this job keeps its "Stopping…" spinner until the job ends.
            if (_cancelFor != _jobKey) { _cancelFor = ""; Theme.SetBusy(_cancel, false); }
        }
        else
        {
            _jobKey = _cancelFor = "";
            Theme.SetBusy(_cancel, false);
            _jobIdle.Text = hasPane
                ? "Nothing is running. Ask Claude something about this model " + (_tabs.Parent is null ? "in the Claude app" : "in the Chat tab or the Claude app") +
                  " (or run a quick tool below); its progress and a Cancel job button show here."
                : "The running job shows here once Sketch ProMax has started.";
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
            var (label, badge) = outcome switch
            {
                "ok" => ("worked", Theme.K.BadgeOk),
                "stopped" => ("stopped on request", Theme.K.BadgeWarn),
                "cancelled" => ("cancelled", Theme.K.BadgeMuted),
                _ => ("did not work", Theme.K.BadgeError),
            };
            var head = new WrapPanel();
            head.Children.Add(Text($"{Field(last, 1)}  ", K_Text, bold: true, size: 13));
            var pill = new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center,
                Child = Theme.Fg(new TextBlock { Text = label, FontSize = 11 }, Theme.K.OnAccent),
            };
            pill.SetResourceReference(Border.BackgroundProperty, badge);
            head.Children.Add(pill);
            _last.Children.Add(head);
            var when = $"job {Field(last, 0)}" + (Field(last, 3).Length > 0 ? $", finished {Field(last, 3)}" : "") +
                       (Field(last, 4).Length > 0 ? $" after {Field(last, 4)}" : "");
            _last.Children.Add(Text(when, K_Muted, size: 11));
            if (outcome is "error" or "failed")
            {
                var next = Text("Ask Claude in the chat what went wrong; it can read this result and try another way. Ctrl+Z in " +
                                "Revit undoes anything the job changed.", K_Muted, size: 11);
                next.Margin = new Thickness(0, 2, 0, 0);
                _last.Children.Add(next);
            }
            if (result.Count > 0)
            {
                var lines = new StackPanel();
                foreach (var line in result) lines.Children.Add(Linked(line, ids, mono: true));
                var box = Box(new ScrollViewer
                {
                    Content = lines, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
                });
                box.Margin = new Thickness(0, 4, 0, 0);
                _last.Children.Add(box);
            }
            // Select all the element ids found: a real button, so it is a full-size click target.
            if (ids.Count > 1)
            {
                var list = string.Join(",", ids);
                var all = Theme.Button($"Select all {ids.Count} elements");
                all.HorizontalAlignment = HorizontalAlignment.Left;
                all.Margin = new Thickness(0, 6, 0, 0);
                all.ToolTip = "Selects every element id found in the steps and the result, and zooms to them.";
                all.Click += (_, _) => PaneHost.Request("pane-select\t" + list);
                _last.Children.Add(all);
            }
        }
        else if (last is null) _shown.Remove(_last);
    }

    /// <summary>
    /// A wrapping line with every known element id in it as a link that selects and zooms to that element. Ids are
    /// inline links in running text (exempt from the 24 px target size); "Select all" below is the full-size target.
    /// </summary>
    private static TextBlock Linked(string line, HashSet<string> ids, bool mono)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = mono ? 11 : 12 };
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
                var link = new Hyperlink(new Run(id)) { ToolTip = $"Select element {id} and zoom to it" };
                AutomationProperties.SetName(link, $"Select element {id}");
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
                  "code that worked, say \"save it as a tool\". It then appears here and runs in Revit without the AI.", K_Muted));
            return;
        }
        foreach (var p in items)
        {
            var (kind, name, summary) = (p[0], p[1], p[2]);
            var kindText = kind == "recipe" ? "Recipe" : "Learned tool";
            var row = new DockPanel();
            var icon = new Image { Source = Icons.Get(kind == "recipe" ? "recipe" : "learned", 16), Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = Pretty(name), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var button = Theme.Button(row);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Padding = new Thickness(6, 3, 6, 3);
            button.Margin = new Thickness(0, 0, 0, 4);
            button.ToolTip = $"{Pretty(name)}\n{kindText}. {summary}";
            AutomationProperties.SetName(button, $"Run {kindText.ToLowerInvariant()} {Pretty(name)}");
            AutomationProperties.SetHelpText(button, summary);
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
