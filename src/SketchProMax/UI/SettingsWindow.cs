using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using SketchProMax.Settings;

namespace SketchProMax.UI;

/// <summary>
/// Settings dialog opened from the ribbon, in tabs (<see cref="SettingsLayout"/>): Approvals, Chat, Firm standards, Time
/// tracking, Issue &amp; drawings, Sign-off and Updates. The window fits the monitor Revit is on, each tab's page scrolls on its own
/// and the Save / Cancel bar stays pinned at the bottom. The tab opened last is opened again.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AgentSettings _settings = AgentSettings.Load();
    private readonly List<(RadioButton Button, Tools.ApprovalLevel Level)> _levels = [];
    private readonly List<(RadioButton Button, Chat.Motion Level)> _motion = [];
    private readonly List<(RadioButton Button, string Type)> _chatTypes = [];
    private readonly Dictionary<string, ComboBox> _picks = new(StringComparer.Ordinal);
    private readonly List<string> _noAsk;
    private readonly StackPanel _noAskList = new();
    private readonly CheckBox _autoUpdate = Check("Update Sketch ProMax automatically");
    private readonly CheckBox _openItems = Check("Allow issue with open items (each such issue says so, with the reason, in its issue record)");
    private readonly TextBox _standards = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private readonly TextBox _legendSeed = new();
    private readonly TextBlock _seedHint = Brand.Text("", Brand.K.WarningText, size: 11);
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText);
    private readonly CheckBox _timeOn = Check("Count working time and time saved in each model");
    private readonly TextBox _idle = new() { Width = 56, MinHeight = 26, Margin = new Thickness(6, 0, 6, 0) };
    private readonly CheckBox _changeFile = Check("Keep a change file for each model (cleared at every save)");
    private readonly TextBox _sigName = new() { MinHeight = 26 };
    private readonly ComboBox _sigRole = new() { IsEditable = true, MinHeight = 26 };
    private readonly System.Windows.Controls.Image _sigPreview = new() { MaxHeight = 70, MaxWidth = 260, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _sigHint = Brand.Text("", Brand.K.Muted, size: 11);
    private readonly TextBox _checklistPath = new();
    private readonly ComboBox _helper = new() { MinHeight = 26 };
    private readonly TextBox _crewYou = new() { MinHeight = 26 }, _crewCoordinator = new() { MinHeight = 26 }, _crewWorkers = new() { MinHeight = 26 }, _crewApplier = new() { MinHeight = 26 };
    private readonly ComboBox _jobLimit = new() { MinHeight = 26, MinWidth = 56, Margin = new Thickness(6, 0, 6, 0) };
    private string _sigImage = "";
    private readonly TabControl _tabs = new();
    private readonly List<(string Setting, FrameworkElement Control)> _placed = [];
    private List<Tools.TimeRate>? _rates;

    /// <param name="openTab">The tab to open on (a SettingsLayout key); null: the one used last.</param>
    public SettingsWindow(string? openTab = null)
    {
        Title = "Sketch ProMax settings";
        var area = RevitWorkArea();
        (Width, Height, MinWidth, MinHeight) = SettingsLayout.Fit(area.Width, area.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Brand.UseTheme(this);

        _noAsk = [.. _settings.NoAskTools ?? []];
        _autoUpdate.IsChecked = _settings.AutoUpdate;
        _openItems.IsChecked = _settings.AllowIssueWithOpenItems;
        _standards.Text = _settings.FirmStandards;
        _legendSeed.Text = _settings.LegendSeedPath ?? "";
        _timeOn.IsChecked = _settings.TimeTracking;
        _idle.Text = _settings.IdleMinutes.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        _rates = _settings.TimeRates;
        _changeFile.IsChecked = _settings.ChangeFile;
        _sigName.Text = _settings.SignatureName ?? "";
        _sigRole.Text = _settings.SignatureRole ?? "";
        _sigImage = _settings.SignatureImage ?? "";
        _checklistPath.Text = _settings.ChecklistPath ?? "";
        foreach (var (tag, text) in Tools.ChecklistHelper.Choices) _helper.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
        _helper.SelectedIndex = Math.Max(0, Array.FindIndex(Tools.ChecklistHelper.Choices, c => c.Tag == Tools.ChecklistHelper.Parse(_settings.ChecklistHelper)));
        for (var n = Chat.JobBoard.MinLimit; n <= Chat.JobBoard.MaxLimit; n++) _jobLimit.Items.Add(new ComboBoxItem { Content = n.ToString(System.Globalization.CultureInfo.CurrentCulture), Tag = n });
        _jobLimit.SelectedIndex = Chat.JobBoard.ClampLimit(_settings.ChatJobLimit) - Chat.JobBoard.MinLimit;

        // One primary action: Save (Enter, except inside the standards box where Enter starts a new line). Esc cancels.
        var save = Brand.Button("Save", Brand.Weight.Primary);
        save.IsDefault = true;
        save.Click += (_, _) => Save();
        var cancel = Brand.Button("Cancel");
        cancel.IsCancel = true;

        TabStyles(_tabs);
        AutomationProperties.SetName(_tabs, "Settings sections");
        foreach (var tab in SettingsLayout.Tabs)
        {
            var page = tab.Key switch
            {
                SettingsLayout.Approvals => ApprovalPage(),
                SettingsLayout.Chat => ChatPage(),
                SettingsLayout.Standards => StandardsPage(),
                SettingsLayout.Time => TimePage(),
                SettingsLayout.Issue => IssuePage(),
                SettingsLayout.SignOff => SignOffPage(),
                SettingsLayout.Recovery => RecoveryPage(),
                _ => UpdatesPage(),
            };
            page.Children.Insert(0, Heading(tab.Name));
            var intro = Brand.Text(tab.Intro, Brand.K.Muted);
            intro.Margin = new Thickness(0, 2, 0, 4);
            page.Children.Insert(1, intro);
            page.Margin = new Thickness(20, 14, 20, 16);
            var item = new TabItem
            {
                Header = tab.Name, Tag = tab.Key,
                // Each page scrolls on its own; long lines wrap instead of scrolling sideways.
                Content = new ScrollViewer
                {
                    Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
                },
            };
            AutomationProperties.SetName(item, tab.Name);
            AutomationProperties.SetHelpText(item, tab.Intro);
            _tabs.Items.Add(item);
        }
        _tabs.SelectedIndex = SettingsLayout.StartIndex(openTab ?? ReadLastTab());
        Closed += (_, _) => WriteLastTab((_tabs.SelectedItem as TabItem)?.Tag as string);

        // The bar at the foot: always visible, whatever the size of the window or the page.
        var note = Brand.Text("Changes apply to new chats; chat animations and the number of jobs change straight away.", Brand.K.Muted, size: 11);
        _error.Margin = new Thickness(0, 6, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        var buttons = Brand.ButtonRow(cancel, save);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        var foot = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 12),
            Child = new StackPanel { Children = { note, _error, buttons } },
        };
        foot.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);

        var root = new DockPanel();
        var header = Brand.Header("Settings", "How Sketch ProMax works in this Revit.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(_tabs);
        Content = root;

        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => KeepOnScreen();
        Loaded += (_, _) => FocusPage();
    }

    /// <summary>Every setting control with the AgentSettings property it edits, for the field test's check that each is on its tab.</summary>
    internal IReadOnlyList<(string Setting, FrameworkElement Control)> Placed => _placed;

    /// <summary>The tab list, for the field test.</summary>
    internal TabControl Tabs => _tabs;

    private T Place<T>(string setting, T control) where T : FrameworkElement
    {
        _placed.Add((setting, control));
        return control;
    }

    private static CheckBox Check(string label)
    {
        // A wrapping text block, so a long label wraps inside the page instead of being cut off on the right.
        var box = new CheckBox { Content = Brand.Text(label), Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(box, label);
        return box;
    }

    private static TextBlock Heading(string text)
    {
        var heading = Brand.Text(text, size: 15, bold: true);
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
        return heading;
    }

    private static TextBlock SubHeading(string text)
    {
        var heading = Brand.Text(text, bold: true);
        heading.Margin = new Thickness(0, 14, 0, 2);
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
#endif
        return heading;
    }

    private static TextBlock Hint(string text, double top = 6)
    {
        var hint = Brand.Text(text, Brand.K.Muted, size: 11);
        hint.Margin = new Thickness(0, top, 0, 0);
        return hint;
    }

    // ---- Pages ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// When Revit asks before Sketch ProMax changes something: the levels, what is always asked (code, deletes that take
    /// other elements, overwriting files, what-ifs, the issue gate), and the tools the user said not to ask about again,
    /// each with "Ask again".
    /// </summary>
    private StackPanel ApprovalPage()
    {
        var page = new StackPanel();
        page.Children.Add(SubHeading("Ask before changes"));
        var current = _settings.Approvals;
        foreach (var level in Tools.ApprovalPolicy.Levels)
        {
            var radio = new RadioButton
            {
                GroupName = "approvals", IsChecked = level == current, Margin = new Thickness(0, 6, 0, 0), MinHeight = 24,
                Content = new StackPanel
                {
                    Children =
                    {
                        Brand.Text(Tools.ApprovalPolicy.Label(level) + (level == Tools.ApprovalLevel.RiskyOnly ? " (recommended)" : "")),
                        Brand.Text(Tools.ApprovalPolicy.Hint(level) + ".", Brand.K.Muted, size: 11),
                    },
                },
            };
            AutomationProperties.SetName(radio, Tools.ApprovalPolicy.Label(level));
            AutomationProperties.SetHelpText(radio, Tools.ApprovalPolicy.Hint(level));
            _levels.Add((radio, level));
            page.Children.Add(Place("ApprovalLevel", radio));
        }
        page.Children.Add(Hint(
            "Asked at the first three levels, whatever you choose: code Sketch ProMax wrote, deletes that take other elements with them, " +
            "overwriting a file outside Documents\\SketchProMax, what-ifs, and value engineering on the open model. Always allow " +
            "runs those too, without asking. Accepting or issuing past open issue check items asks at every level. A brief you " +
            "run counts as approving its task."));
        page.Children.Add(SubHeading("Not asked again"));
        page.Children.Add(Place("NoAskTools", _noAskList));
        FillNoAsk();
        return page;
    }

    /// <summary>The tools picked with "Don't ask again", each with Ask again, or a line saying there are none.</summary>
    private void FillNoAsk()
    {
        _noAskList.Children.Clear();
        if (_noAsk.Count == 0)
        {
            _noAskList.Children.Add(Brand.Text("None. In an approval dialog, \"Don't ask again\" adds a tool here.", Brand.K.Muted, size: 11));
            return;
        }
        foreach (var tool in _noAsk.OrderBy(t => t, StringComparer.Ordinal).ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var again = Brand.Button("Ask again");
            again.MinHeight = 24;
            AutomationProperties.SetName(again, "Ask again for " + Tools.ApprovalPolicy.Friendly(tool));
            again.Click += (_, _) =>
            {
                _noAsk.Remove(tool);
                FillNoAsk();
                (_noAskList.Children.OfType<DockPanel>().FirstOrDefault()?.Children.OfType<Button>().FirstOrDefault() as UIElement ?? _levels[0].Button).Focus();
            };
            DockPanel.SetDock(again, Dock.Right);
            row.Children.Add(again);
            var name = Brand.Text(Tools.ApprovalPolicy.Friendly(tool));
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            _noAskList.Children.Add(row);
        }
        if (_noAsk.Count > 1)
        {
            var all = Brand.Button("Ask again for all");
            all.HorizontalAlignment = HorizontalAlignment.Left;
            all.Margin = new Thickness(0, 4, 0, 0);
            all.MinHeight = 24;
            all.Click += (_, _) => { _noAsk.Clear(); FillNoAsk(); _levels[0].Button.Focus(); };
            _noAskList.Children.Add(all);
        }
    }

    /// <summary>How much the chat pane moves: Full, Reduced or Off, each with what it means.</summary>
    private StackPanel ChatPage()
    {
        var page = new StackPanel();
        page.Children.Add(SubHeading("Chat animations"));
        var current = Chat.MotionLevels.Parse(_settings.ChatAnimations);
        foreach (var level in Chat.MotionLevels.All)
        {
            var radio = new RadioButton
            {
                GroupName = "motion", IsChecked = level == current, Margin = new Thickness(0, 6, 0, 0), MinHeight = 24,
                Content = new StackPanel
                {
                    Children =
                    {
                        Brand.Text(Chat.MotionLevels.Label(level) + (level == Chat.Motion.Full ? " (recommended)" : "")),
                        Brand.Text(Chat.MotionLevels.Hint(level) + ".", Brand.K.Muted, size: 11),
                    },
                },
            };
            AutomationProperties.SetName(radio, "Chat animations: " + Chat.MotionLevels.Label(level));
            AutomationProperties.SetHelpText(radio, Chat.MotionLevels.Hint(level));
            _motion.Add((radio, level));
            page.Children.Add(Place("ChatAnimations", radio));
        }
        page.Children.Add(Hint("When Windows' Show animations is off (Settings > Accessibility > Visual effects), the chat doesn't animate whatever you pick."));
        page.Children.Add(SubHeading("New chats"));
        page.Children.Add(Hint("Each chat keeps its own type, shown under its name, where you can switch it while none of its jobs is open. New chat (+) offers both types, this one first.", 2));
        var defaultType = Chat.ChatTypes.Parse(_settings.ChatTypeDefault);
        foreach (var type in Chat.ChatTypes.All)
        {
            var radio = new RadioButton
            {
                GroupName = "chattype", IsChecked = type == defaultType, Margin = new Thickness(0, 6, 0, 0), MinHeight = 24,
                Content = new StackPanel
                {
                    Children =
                    {
                        Brand.Text(Chat.ChatTypes.Label(type) + (type == Chat.ChatTypes.Coordinator ? " (recommended)" : "")),
                        Brand.Text(Chat.ChatTypes.Hint(type) + ".", Brand.K.Muted, size: 11),
                    },
                },
            };
            AutomationProperties.SetName(radio, "New chats start as: " + Chat.ChatTypes.Label(type));
            AutomationProperties.SetHelpText(radio, Chat.ChatTypes.Hint(type));
            _chatTypes.Add((radio, type));
            page.Children.Add(Place("ChatTypeDefault", radio));
        }
        page.Children.Add(SubHeading("Model and effort"));
        page.Children.Add(Hint("New chats start on these; the pickers under a chat's name change them for that chat (and its new jobs) until set back to Automatic. Automatic sorts each request and job into simple, everyday or hard work by its words and uses the model and effort below, to save your plan's limits.", 2));
        page.Children.Add(PickRow("New chats' model", "ChatModelDefault", _settings.ChatModelDefault, models: true, automatic: true, b => Place("ChatModelDefault", b)));
        page.Children.Add(PickRow("New chats' effort", "ChatEffortDefault", _settings.ChatEffortDefault, models: false, automatic: true, b => Place("ChatEffortDefault", b)));
        foreach (var kind in Chat.ChatAuto.Kinds)
        {
            var label = Brand.Text(Chat.ChatAuto.KindLabel(kind), bold: true);
            label.Margin = new Thickness(0, 10, 0, 0);
            page.Children.Add(label);
            page.Children.Add(Hint(Chat.ChatAuto.KindHint(kind) + ".", 0));
            var what = Chat.ChatAuto.KindLabel(kind);
            var row = new WrapPanel();
            switch (kind)
            {
                case Chat.ChatAuto.Simple:
                    row.Children.Add(PickRow("Model", "ChatModelSimple", _settings.ChatModelSimple, models: true, automatic: false, b => Place("ChatModelSimple", b), what));
                    row.Children.Add(PickRow("Effort", "ChatEffortSimple", _settings.ChatEffortSimple, models: false, automatic: false, b => Place("ChatEffortSimple", b), what));
                    break;
                case Chat.ChatAuto.Hard:
                    row.Children.Add(PickRow("Model", "ChatModelHard", _settings.ChatModelHard, models: true, automatic: false, b => Place("ChatModelHard", b), what));
                    row.Children.Add(PickRow("Effort", "ChatEffortHard", _settings.ChatEffortHard, models: false, automatic: false, b => Place("ChatEffortHard", b), what));
                    break;
                default:
                    row.Children.Add(PickRow("Model", "ChatModelEveryday", _settings.ChatModelEveryday, models: true, automatic: false, b => Place("ChatModelEveryday", b), what));
                    row.Children.Add(PickRow("Effort", "ChatEffortEveryday", _settings.ChatEffortEveryday, models: false, automatic: false, b => Place("ChatEffortEveryday", b), what));
                    break;
            }
            page.Children.Add(row);
        }
        page.Children.Add(SubHeading("Parallel jobs"));
        page.Children.Add(Hint("A chat With coordinator can split a request into jobs that work side by side, each shown as a card that opens the job's own chat. Reads run together; changes to the model take turns, one at a time.", 2));
        var jobRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var before = Brand.Text("Run up to");
        before.VerticalAlignment = VerticalAlignment.Center;
        jobRow.Children.Add(before);
        AutomationProperties.SetName(_jobLimit, "How many jobs run at once");
        AutomationProperties.SetHelpText(_jobLimit, "Each job uses your plan while it works; more jobs wait to start until one finishes. 3 is usual.");
        jobRow.Children.Add(Place("ChatJobLimit", _jobLimit));
        var after = Brand.Text("jobs at once (3 is usual; fewer keeps more of your plan's limits)");
        after.VerticalAlignment = VerticalAlignment.Center;
        jobRow.Children.Add(after);
        page.Children.Add(jobRow);
        page.Children.Add(SubHeading("Names of the crew"));
        page.Children.Add(Hint("Over a chat With coordinator, a strip shows the work as people: you hand it to the coordinator, who gives each job to a worker; a worker with a change for the model queues for the one who applies changes in Revit, one at a time. Leave a name blank for its default.", 2));
        var names = Chat.CrewNames.From(_settings.ChatCrewYou, _settings.ChatCrewCoordinator, _settings.ChatCrewWorkers, _settings.ChatCrewApplier, Environment.UserName);
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void NameRow(string label, TextBox box, string value, string help)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = Brand.Text(label);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 4, 12, 4);
            Grid.SetRow(text, row);
            grid.Children.Add(text);
            box.Text = value;
            box.Margin = new Thickness(0, 4, 0, 4);
            box.MinWidth = 240;
            AutomationProperties.SetName(box, label);
            AutomationProperties.SetHelpText(box, help);
            box.ToolTip = help;
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
        }
        NameRow("You", Place("ChatCrewYou", _crewYou), names.You, "Your name, on the person who hands over the requests.");
        NameRow("Coordinator", Place("ChatCrewCoordinator", _crewCoordinator), names.Coordinator, "The main chat that hands out the jobs.");
        NameRow("Workers", Place("ChatCrewWorkers", _crewWorkers), string.Join(", ", names.Workers), "One name per job that runs at once, separated by commas. More jobs than names get Worker 4, Worker 5 and so on.");
        NameRow("Applies changes in Revit", Place("ChatCrewApplier", _crewApplier), names.Applier, "Takes each worker's change into the model, one at a time.");
        page.Children.Add(grid);
        page.Children.Add(Hint("The model and Brief before running are chosen in the Chat pane, next to the message box.", 12));
        return page;
    }

    /// <summary>A label and a list of models (Claude Code's aliases) or effort levels, optionally with Automatic first.</summary>
    private FrameworkElement PickRow(string label, string key, string? value, bool models, bool automatic, Func<ComboBox, ComboBox> place, string what = "")
    {
        var box = new ComboBox { MinHeight = 26, MinWidth = 120, Margin = new Thickness(6, 0, 12, 0) };
        if (automatic) box.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = Chat.ChatAuto.Automatic });
        if (models) foreach (var m in Chat.ChatModels.All) box.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id, ToolTip = m.Hint });
        else foreach (var e in Chat.ChatAuto.Efforts) box.Items.Add(new ComboBoxItem { Content = Chat.ChatAuto.EffortLabel(e), Tag = e, ToolTip = Chat.ChatAuto.EffortHint(e) });
        var stored = models ? Chat.ChatAuto.ParseModel(value) : Chat.ChatAuto.ParseEffort(value);
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == stored) ?? box.Items[0];
        AutomationProperties.SetName(box, (what.Length > 0 ? what + ": " : "") + label);
        _picks[key] = box;
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var text = Brand.Text(label);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        row.Children.Add(place(box));
        return row;
    }

    private string Picked(string key, string fallback) => _picks.TryGetValue(key, out var box) && (box.SelectedItem as ComboBoxItem)?.Tag is string v ? v : fallback;

    /// <summary>Firm standards in plain English, and the legend seed file with Browse.</summary>
    private StackPanel StandardsPage()
    {
        var page = new StackPanel();
        var standardsLabel = Brand.Text(
            "Firm standards (plain English). Sketch ProMax follows these in every conversation, e.g. " +
            "'Sheet numbers are A-xxx. View names: LEVEL - PURPOSE. Door tags use the Mark parameter.'");
        standardsLabel.Margin = new Thickness(0, 10, 0, 4);
        page.Children.Add(standardsLabel);
        AutomationProperties.SetName(_standards, "Firm standards");
        AutomationProperties.SetHelpText(_standards, standardsLabel.Text);
        page.Children.Add(Place("FirmStandards", _standards));
        page.Children.Add(Hint("Enter starts a new line here; Ctrl+Enter saves."));

        var seedLabel = Brand.Text(
            "Legend seed file (optional): a firm template or project (.rvt/.rte) with a legend. create_legend copies from it when a model has no legend.");
        seedLabel.Margin = new Thickness(0, 16, 0, 4);
        page.Children.Add(seedLabel);
        var seedRow = new DockPanel();
        var browse = Brand.Button("Browse…");
        AutomationProperties.SetName(browse, "Browse for the legend seed file");
        browse.Click += (_, _) => BrowseSeed();
        DockPanel.SetDock(browse, Dock.Right);
        seedRow.Children.Add(browse);
        AutomationProperties.SetName(_legendSeed, "Legend seed file");
        seedRow.Children.Add(Place("LegendSeedPath", _legendSeed));
        page.Children.Add(seedRow);
        _seedHint.Margin = new Thickness(0, 2, 0, 0);
        page.Children.Add(_seedHint);
        _legendSeed.TextChanged += (_, _) => CheckSeed();
        CheckSeed();
        return page;
    }

    private void BrowseSeed()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick the legend seed file",
            Filter = "Revit projects and templates|*.rvt;*.rte|All files|*.*",
        };
        try
        {
            var current = _legendSeed.Text.Trim().Trim('"');
            if (current.Length > 0 && Path.GetDirectoryName(current) is { Length: > 0 } dir && Directory.Exists(dir)) dialog.InitialDirectory = dir;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        if (dialog.ShowDialog(this) == true) _legendSeed.Text = dialog.FileName;
        _legendSeed.Focus();
    }

    /// <summary>Time saved and working time (Revit/TimeLog): on or off, the idle threshold, the by-hand rates, who sees the totals.</summary>
    private StackPanel TimePage()
    {
        var page = new StackPanel();
        page.Children.Add(Place("TimeTracking", _timeOn));
        var idleRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var before = Brand.Text("Stop counting after");
        before.VerticalAlignment = VerticalAlignment.Center;
        idleRow.Children.Add(before);
        AutomationProperties.SetName(_idle, "Minutes without activity before working time stops counting");
        idleRow.Children.Add(Place("IdleMinutes", _idle));
        var after = Brand.Text("minutes without activity");
        after.VerticalAlignment = VerticalAlignment.Center;
        idleRow.Children.Add(after);
        page.Children.Add(idleRow);
        var ratesRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var rates = Brand.Button("Edit by-hand rates…");
        rates.Margin = new Thickness(0);
        AutomationProperties.SetHelpText(rates, "Minutes per run and per item for each tool. The built-in ones are estimates.");
        rates.Click += (_, _) =>
        {
            var window = new TimeRatesWindow(_rates) { Owner = this };
            if (window.ShowDialog() == true) _rates = window.Result;
        };
        ratesRow.Children.Add(Place("TimeRates", rates));
        var estimate = Brand.Text("Built-in rates are estimates.", Brand.K.Muted, size: 11);
        estimate.VerticalAlignment = VerticalAlignment.Center;
        estimate.Margin = new Thickness(8, 0, 0, 0);
        ratesRow.Children.Add(estimate);
        page.Children.Add(ratesRow);
        page.Children.Add(Hint("The totals are kept in the model and in Project Information, per user: in a shared model, anyone who opens it can see them.", 10));
        return page;
    }

    /// <summary>What the issue check lets through.</summary>
    private StackPanel IssuePage()
    {
        var page = new StackPanel();
        const string off = "Off: an issue waits until every item of the issue check is fixed or accepted with a reason.";
        AutomationProperties.SetHelpText(_openItems, off);
        page.Children.Add(Place("AllowIssueWithOpenItems", _openItems));
        page.Children.Add(Hint(off));
        return page;
    }

    /// <summary>
    /// The pre-submission checklist's sign-off: your name, the role you sign as, your signature picture (kept as a copy
    /// next to the settings, so moving the original doesn't lose it) and the office's checklist Excel file.
    /// </summary>
    private StackPanel SignOffPage()
    {
        var page = new StackPanel();
        page.Children.Add(Label("Your name, as it goes on the signed checklist"));
        AutomationProperties.SetName(_sigName, "Your name on the signed checklist");
        page.Children.Add(Place("SignatureName", _sigName));

        page.Children.Add(Label("You sign as"));
        foreach (var role in new[] { "Trade BIM coordinator", "Trade group leader" }) _sigRole.Items.Add(role);
        AutomationProperties.SetName(_sigRole, "The sign-off role you sign as");
        AutomationProperties.SetHelpText(_sigRole, "Pick one or type the role as the checklist names it. Your signature goes in that row.");
        page.Children.Add(Place("SignatureRole", _sigRole));
        page.Children.Add(Hint("Pick one, or type the role as your checklist names it. Your signature goes in that row; the other signs by hand or with a picture you pick in the checklist."));

        page.Children.Add(Label("Your signature picture (PNG or JPG, on a white or clear background)"));
        var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8), MinHeight = 60, CornerRadius = new CornerRadius(4), Child = _sigPreview };
        frame.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        // White under the picture in both themes: signatures are dark ink on white paper.
        frame.Background = Brand.Brush(System.Windows.Media.Colors.White);
        frame.HorizontalAlignment = HorizontalAlignment.Left;
        frame.MinWidth = 280;
        AutomationProperties.SetName(_sigPreview, "Your signature");
        page.Children.Add(frame);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var choose = Brand.Button("Choose picture…");
        choose.Margin = new Thickness(0);
        AutomationProperties.SetHelpText(choose, "Pick a scan or photo of your signature. A copy is kept with your settings.");
        choose.Click += (_, _) => ChooseSignature();
        row.Children.Add(Place("SignatureImage", choose));
        var remove = Brand.Button("Remove picture", Brand.Weight.Danger);
        AutomationProperties.SetHelpText(remove, "Removes your signature picture from Sketch ProMax when you press Save. Checklists already made keep theirs.");
        remove.Click += (_, _) => { _sigImage = ""; ShowSignature(); };
        row.Children.Add(remove);
        page.Children.Add(row);
        _sigHint.Margin = new Thickness(0, 4, 0, 0);
        page.Children.Add(_sigHint);
        ShowSignature();

        page.Children.Add(SubHeading("Checklist"));
        page.Children.Add(Brand.Text("Optional: an Excel checklist (.xlsx) to use instead of the built-in one. It is followed while it changes; to edit the checklist instead, use Edit the checklist in the checklist window."));
        var pathRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var browse = Brand.Button("Browse…");
        AutomationProperties.SetName(browse, "Browse for the checklist Excel file");
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pick the pre-submission checklist", Filter = "Excel workbooks|*.xlsx|All files|*.*" };
            if (dialog.ShowDialog(this) == true) _checklistPath.Text = dialog.FileName;
            _checklistPath.Focus();
        };
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        AutomationProperties.SetName(_checklistPath, "Checklist Excel file");
        pathRow.Children.Add(Place("ChecklistPath", _checklistPath));
        page.Children.Add(pathRow);
        page.Children.Add(Hint("Empty: the built-in checklist, or the one edited in the checklist window."));

        page.Children.Add(Label("Helper for the checks the model test can't do"));
        AutomationProperties.SetName(_helper, "Checklist helper");
        AutomationProperties.SetHelpText(_helper, "Who the checklist's Ask button hands a check to.");
        page.Children.Add(Place("ChecklistHelper", _helper));
        page.Children.Add(Hint("Sketch ProMax: the check goes to the chat, which looks at the model (and pictures of views) and says pass or fail. " +
                               "SketchPro: its Copilot opens with the question pasted in, for you to send. None: no Ask button. The helper only advises; you still answer each check."));
        return page;
    }

    private static TextBlock Label(string text)
    {
        var label = Brand.Text(text);
        label.Margin = new Thickness(0, 12, 0, 4);
        return label;
    }

    private void ChooseSignature()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pick your signature picture", Filter = "Pictures|*.png;*.jpg;*.jpeg|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            if (Tools.ChecklistDocx.ImageKind(bytes) is null || Tools.ChecklistDocx.ImageSize(bytes) is null)
            {
                _sigHint.Text = "That file isn't a PNG or JPG picture. Save the signature as PNG or JPG and pick it again.";
                _sigHint.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.DangerText);
                return;
            }
            _sigImage = dialog.FileName;
            ShowSignature();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _sigHint.Text = $"Couldn't read the picture ({ex.Message}). Copy it to your PC and pick it again.";
            _sigHint.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.DangerText);
        }
    }

    private void ShowSignature()
    {
        _sigPreview.Source = Signature.Load(_sigImage);
        _sigHint.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.Muted);
        _sigHint.Text = _sigPreview.Source is null
            ? (_sigImage.Length > 0 ? "The picture can't be read any more. Choose it again." : "No signature picture: checklists leave your box empty to sign by hand.")
            : "This picture goes on checklists that are ready to submit (nothing No, nothing unanswered).";
    }

    /// <summary>The change file (Revit/ChangeFile): on or off, and Recover.</summary>
    private StackPanel RecoveryPage()
    {
        var page = new StackPanel();
        page.Children.Add(Place("ChangeFile", _changeFile));
        page.Children.Add(Hint("A text file in Documents\\Sketch ProMax\\Change files lists every element you add, move, change or delete, a few seconds after you do it. " +
                               "Saving or synchronizing clears it. If the model is lost or damaged, open the last good copy and replay the file onto it. It never saves or changes the model by itself."));
        var recover = Brand.Button("Lifeline…");
        recover.HorizontalAlignment = HorizontalAlignment.Left;
        recover.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetHelpText(recover, "Opens the Lifeline window: each model's change file, and Replay onto the open model. It doesn't save the settings here.");
        recover.Click += (_, _) => RecoverWindow.Open();
        page.Children.Add(recover);
        return page;
    }

    /// <summary>Automatic updates, the version installed and the last check, and Check now.</summary>
    private StackPanel UpdatesPage()
    {
        var page = new StackPanel();
        page.Children.Add(Place("AutoUpdate", _autoUpdate));
        page.Children.Add(Hint("New versions install while Revit stays open and switch over when no job is running."));
        page.Children.Add(SubHeading("This version"));
        string state;
        try { state = Update.Updater.Describe(); }
        catch (Exception) { state = "Sketch ProMax " + Update.Updater.CurrentText + "."; }
        page.Children.Add(Brand.Text(state));
        var check = Brand.Button("Check for updates now");
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetHelpText(check, "Looks for a newer version now; the update window shows what it finds. It doesn't save the settings here.");
        check.Click += (_, _) => Commands.CoreCommands.CheckForUpdates();
        page.Children.Add(check);
        return page;
    }

    // ---- Tabs, keys and size ---------------------------------------------------------------------------------------

    /// <summary>The tab list on the left in palette colours (the stock tab look paints fixed light colours, wrong in the dark theme).</summary>
    private static void TabStyles(TabControl tabs)
    {
        const string xaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="SpmSettingsTabs" TargetType="TabControl">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="Padding" Value="0"/>
                <Setter Property="TabStripPlacement" Value="Left"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabControl">
                    <Grid KeyboardNavigation.TabNavigation="Local">
                      <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <Border Background="{DynamicResource SpmSurface}" BorderBrush="{DynamicResource SpmBorder}" BorderThickness="0,0,1,0">
                        <StackPanel IsItemsHost="True" MinWidth="170" Margin="0,10,0,10" KeyboardNavigation.TabIndex="1"
                                    KeyboardNavigation.TabNavigation="Once" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                      </Border>
                      <ContentPresenter Grid.Column="1" x:Name="PART_SelectedContentHost" ContentSource="SelectedContent"
                                        KeyboardNavigation.TabIndex="2" KeyboardNavigation.TabNavigation="Local"/>
                    </Grid>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
              <Style TargetType="TabItem">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="FocusVisualStyle" Value="{DynamicResource SpmFocusVisual}"/>
                <Setter Property="FontSize" Value="13"/>
                <Setter Property="MinHeight" Value="34"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabItem">
                    <Grid Background="Transparent" SnapsToDevicePixels="True">
                      <Border x:Name="bg" Background="Transparent" Margin="6,1,8,1" CornerRadius="4"/>
                      <Rectangle x:Name="bar" Width="3" HorizontalAlignment="Left" Margin="6,7,0,7" RadiusX="1.5" RadiusY="1.5"
                                 Fill="{DynamicResource SpmAccent}" Visibility="Collapsed"/>
                      <ContentPresenter ContentSource="Header" Margin="18,8,16,8" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bg" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
                      <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="bg" Property="Background" Value="{DynamicResource SpmAccentSoft}"/>
                        <Setter TargetName="bar" Property="Visibility" Value="Visible"/>
                        <Setter Property="FontWeight" Value="SemiBold"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
            </ResourceDictionary>
            """;
        // A styling problem never stops Settings from opening: it falls back to the stock tabs (still on the left).
        try
        {
            var dictionary = (ResourceDictionary)XamlReader.Parse(xaml);
            tabs.Resources.MergedDictionaries.Add(dictionary);
            tabs.Style = (Style)dictionary["SpmSettingsTabs"];
        }
        catch (Exception) { tabs.TabStripPlacement = Dock.Left; }
    }

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab (and Ctrl+Page Down / Up) switch tabs from anywhere in the window; Ctrl+Enter saves
    /// from the standards box, where Enter is a new line. Enter elsewhere is Save (IsDefault), Esc is Cancel (IsCancel).
    /// </summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key is Key.Tab or Key.PageDown or Key.PageUp)
        {
            var back = e.Key == Key.PageUp || (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            _tabs.SelectedIndex = SettingsLayout.Next(Math.Max(0, _tabs.SelectedIndex), back);
            (_tabs.SelectedItem as TabItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox { AcceptsReturn: true })
        {
            Save();
            e.Handled = true;
        }
    }

    /// <summary>Focus on opening: the picked approval level on the Approvals tab, else the tab itself.</summary>
    private void FocusPage()
    {
        if ((_tabs.SelectedItem as TabItem)?.Tag as string == SettingsLayout.Approvals
            && _levels.FirstOrDefault(l => l.Button.IsChecked == true).Button is { } picked)
            picked.Focus();
        else (_tabs.SelectedItem as TabItem)?.Focus();
    }

    /// <summary>Shows the tab holding <paramref name="control"/> and focuses it once its page is on screen.</summary>
    private void ShowOnTab(string tab, UIElement control)
    {
        if (_tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag as string == tab) is { } item) _tabs.SelectedItem = item;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => control.Focus()));
    }

    private static string LastTabFile => Path.Combine(Path.GetDirectoryName(AgentSettings.FilePath)!, "settings-tab.txt");

    private static string? ReadLastTab()
    {
        try { return File.Exists(LastTabFile) ? File.ReadAllText(LastTabFile) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static void WriteLastTab(string? key)
    {
        if (key is null) return;
        try { SafeFile.Write(LastTabFile, key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* opens on the first tab next time */ }
    }

    /// <summary>
    /// The work area (taskbar left out) of the monitor Revit is on, in device-independent pixels at that monitor's
    /// scale; (0, 0) when it can't be read (<see cref="SettingsLayout.Fit"/> then assumes a small laptop screen).
    /// </summary>
    internal static (double Width, double Height) RevitWorkArea()
    {
        try
        {
            var revit = App.MainWindow;
            if (revit == IntPtr.Zero) return (0, 0);
            var monitor = MonitorFromWindow(revit, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return (0, 0);
            uint dpi;
            try { dpi = GetDpiForWindow(revit); }
            catch (EntryPointNotFoundException) { dpi = 0; } // before Windows 10 1607
            return SettingsLayout.ToDips(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, dpi > 0 ? dpi / 96.0 : 1);
        }
        catch (Exception) { return (0, 0); }
    }

    /// <summary>Moves the window fully into the work area of Revit's monitor when centring on Revit left part of it outside.</summary>
    private void KeepOnScreen()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
            var monitor = MonitorFromWindow(App.MainWindow != IntPtr.Zero ? App.MainWindow : hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            var x = Math.Max(info.Work.Left, Math.Min(rect.Left, info.Work.Right - w));
            var y = Math.Max(info.Work.Top, Math.Min(rect.Top, info.Work.Bottom - h));
            if (x != rect.Left || y != rect.Top) SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch (Exception) { /* stays where it opened */ }
    }

    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Win32Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Win32Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---- Saving --------------------------------------------------------------------------------------------------

    /// <summary>Says straight away when the legend seed path points nowhere (saving still works; the tool then ignores it).</summary>
    private void CheckSeed()
    {
        var path = _legendSeed.Text.Trim().Trim('"');
        var missing = false;
        if (path.Length > 0)
        {
            try { missing = !File.Exists(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { missing = true; }
        }
        _seedHint.Text = missing ? "No file at this path. Check it (copy the path from File Explorer) or leave it empty." : "";
        _seedHint.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save()
    {
        _settings.Approvals = _levels.FirstOrDefault(l => l.Button.IsChecked == true).Level;
        _settings.NoAskTools = _noAsk.Count > 0 ? _noAsk : null;
        _settings.AutoUpdate = _autoUpdate.IsChecked == true;
        _settings.AllowIssueWithOpenItems = _openItems.IsChecked == true;
        _settings.FirmStandards = _standards.Text;
        _settings.LegendSeedPath = _legendSeed.Text.Trim().Trim('"');
        if (!double.TryParse(_idle.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var idle) || idle < 1 || idle > 60)
        {
            _error.Text = "Type the idle time (Time tracking tab) as a number of minutes from 1 to 60 (5 is usual), then press Save again.";
            _error.Visibility = Visibility.Visible;
            ShowOnTab(SettingsLayout.Time, _idle);
            return;
        }
        _settings.ChangeFile = _changeFile.IsChecked == true;
        _settings.TimeTracking = _timeOn.IsChecked == true;
        var motion = _motion.FirstOrDefault(m => m.Button.IsChecked == true).Level;
        _settings.ChatAnimations = Chat.MotionLevels.Store(motion);
        _settings.ChatJobLimit = (_jobLimit.SelectedItem as ComboBoxItem)?.Tag is int limit ? limit : Chat.JobBoard.DefaultLimit;
        _settings.ChatCrewYou = _crewYou.Text.Trim();
        _settings.ChatCrewCoordinator = _crewCoordinator.Text.Trim();
        _settings.ChatCrewWorkers = _crewWorkers.Text.Trim();
        _settings.ChatCrewApplier = _crewApplier.Text.Trim();
        _settings.ChatTypeDefault = _chatTypes.FirstOrDefault(t => t.Button.IsChecked == true).Type ?? Chat.ChatTypes.Coordinator;
        _settings.ChatModelDefault = Picked("ChatModelDefault", Chat.ChatAuto.Automatic);
        _settings.ChatEffortDefault = Picked("ChatEffortDefault", Chat.ChatAuto.Automatic);
        _settings.ChatModelSimple = Picked("ChatModelSimple", Chat.ChatAuto.DefaultModel(Chat.ChatAuto.Simple));
        _settings.ChatModelEveryday = Picked("ChatModelEveryday", Chat.ChatAuto.DefaultModel(Chat.ChatAuto.Everyday));
        _settings.ChatModelHard = Picked("ChatModelHard", Chat.ChatAuto.DefaultModel(Chat.ChatAuto.Hard));
        _settings.ChatEffortSimple = Picked("ChatEffortSimple", Chat.ChatAuto.DefaultEffort(Chat.ChatAuto.Simple));
        _settings.ChatEffortEveryday = Picked("ChatEffortEveryday", Chat.ChatAuto.DefaultEffort(Chat.ChatAuto.Everyday));
        _settings.ChatEffortHard = Picked("ChatEffortHard", Chat.ChatAuto.DefaultEffort(Chat.ChatAuto.Hard));
        _settings.IdleMinutes = idle;
        _settings.TimeRates = _rates is { Count: > 0 } ? _rates : null;
        _settings.SignatureName = _sigName.Text.Trim();
        _settings.SignatureRole = _sigRole.Text.Trim();
        _settings.ChecklistPath = _checklistPath.Text.Trim().Trim('"');
        _settings.ChecklistHelper = (_helper.SelectedItem as ComboBoxItem)?.Tag as string ?? Tools.ChecklistHelper.ProMax;
        try { _settings.SignatureImage = Signature.Keep(_sigImage); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error.Text = $"Couldn't keep a copy of the signature picture: {ex.Message}\nPick it again from a folder on your PC, then press Save again.";
            _error.Visibility = Visibility.Visible;
            ShowOnTab(SettingsLayout.SignOff, _sigName);
            return;
        }
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _error.Text = $"Couldn't save the settings: {ex.Message}\nClose any program that has the settings file open " +
                          $"(or check you can write to {AgentSettings.FilePath}), then press Save again. Nothing was changed.";
            _error.Visibility = Visibility.Visible;
            return;
        }
        Revit.TimeLog.ReadSettings();
        Revit.ChangeFile.ReadSettings();
        Bridge.Approvals.RaiseLevelChanged();
        Chat.ChatUi.SetMotion(motion);
        Chat.CrewStrip.ReloadNames();
        DialogResult = true;
    }
}
