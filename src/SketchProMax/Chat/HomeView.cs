using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The chat's home screen, shown while a chat is empty: "Hey Ahmed!" with the brand mark, then a DISCOVER row of cards
/// that each open a library view (with a back arrow). Explore all features is a searchable library of what Claude can
/// do here, grouped like the ribbon with a count per group; Saved prompts are the user's own; Tailored prompts are
/// picked by project type and phase. Choosing a prompt puts it in the message box to edit. The library is read the
/// first time the home screen is shown, and the Windows name off the UI thread, not when the pane is made.
/// </summary>
internal sealed class HomeView : StackPanel
{
    private const int PerGroupWhenSearching = 12;

    private readonly Action<string> _use;
    private readonly Func<List<SavedPrompt>> _saved;
    private readonly TextBlock _greeting = Brand.Text(Greeting.Line(null), Brand.K.Text, ChatUi.GreetingSize, bold: true);
    private readonly TextBlock _sub = Brand.Text("What can I take care of for you today?", Brand.K.Muted, ChatUi.BodySize);
    private readonly StackPanel _home = new();
    private readonly Grid _cards = new() { Margin = new Thickness(0, ChatUi.S2, 0, 0) };
    private readonly StackPanel _library = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _libraryTitle = Brand.Text("", Brand.K.Text, ChatUi.TitleSize + 1, bold: true);
    private readonly TextBlock _libraryCount = ChatUi.Small("");
    private readonly Button _back;
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly Dictionary<string, (Button Card, TextBlock Line)> _cardFor = new();
    private PromptLibrary? _lib;
    private string _tab = "home", _query = "", _type = "building", _phase = "concept";
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _showAll = new(StringComparer.OrdinalIgnoreCase);
    private TextBox? _search;
    private StackPanel? _results;
    private bool _built;

    /// <summary>The first name for the greeting, read once per Revit session off the UI thread (null: none found).</summary>
    private static Task<string?>? _firstName;

    public HomeView(Action<string> use, Func<List<SavedPrompt>> saved)
    {
        _use = use;
        _saved = saved;
        Margin = new Thickness(ChatUi.S1, ChatUi.S6, ChatUi.S1, ChatUi.S2);

        // Home: the mark and greeting, a muted line, then DISCOVER and its cards.
        var hello = new DockPanel { Margin = new Thickness(0, ChatUi.S6, 0, 0) };
        var mark = ChatUi.Mark();
        mark.Margin = new Thickness(0, 0, ChatUi.S3, 0);
        DockPanel.SetDock(mark, Dock.Left);
        hello.Children.Add(mark);
        _greeting.VerticalAlignment = VerticalAlignment.Center;
        TextOptions.SetTextFormattingMode(_greeting, TextFormattingMode.Ideal); // large text: Ideal keeps its shapes
        hello.Children.Add(_greeting);
        _home.Children.Add(hello);
        _sub.Margin = new Thickness(0, ChatUi.S2, 0, 0);
        _home.Children.Add(_sub);
        var discover = SectionLabel("Discover");
        discover.Margin = new Thickness(0, ChatUi.S6 + ChatUi.S2, 0, 0);
        _home.Children.Add(discover);
        Card("explore", ChatUi.Paths.Grid, "Explore all features", "Everything Sketch ProMax can do here, grouped like the ribbon.");
        Card("saved", ChatUi.Paths.Bookmark, "Saved prompts", "Your prompts, saved from any message with ⋯ › Save prompt.");
        Card("tailored", ChatUi.Paths.Sparkle, "Tailored prompts", "Ideas for your project type and design phase.");
        _cards.SizeChanged += (_, e) => { if (e.WidthChanged) LayoutCards(); };
        _home.Children.Add(_cards);
        LayoutCards();
        Children.Add(_home);

        // A library view: back arrow, its title and count, then the list.
        var head = new DockPanel { Margin = new Thickness(-ChatUi.S1, 0, 0, ChatUi.S3) };
        _back = ChatUi.IconButton(ChatUi.Paths.Back, "Back to home", "Back to the home screen (Esc)");
        _back.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        _back.Click += (_, _) => Guarded(() => Show("home"));
        DockPanel.SetDock(_back, Dock.Left);
        head.Children.Add(_back);
        _libraryCount.Margin = new Thickness(ChatUi.S2, 2, 0, 0);
        DockPanel.SetDock(_libraryCount, Dock.Right);
        head.Children.Add(_libraryCount);
        _libraryTitle.VerticalAlignment = VerticalAlignment.Center;
        _libraryTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _libraryTitle.TextWrapping = TextWrapping.NoWrap;
        head.Children.Add(_libraryTitle);
        _library.Children.Add(head);
        _library.Children.Add(_body);
        _library.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || e.Handled) return;
            e.Handled = true;
            Guarded(() => Show("home"));
        };
        Children.Add(_library);

        AutomationProperties.SetName(this, "Home: ideas to start a chat");
        _firstName ??= Task.Run(WindowsFirstName);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Greet();
            if (_tab != "home") Show("home", focus: false); // a new or emptied chat starts on the home screen
            if (!_built) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Build));
            else Counts();
        };
    }

    /// <summary>Saved prompts changed: update the card, and the list when it shows.</summary>
    public void SavedChanged()
    {
        if (!_built) return;
        Counts();
        if (_tab == "saved") Show("saved", focus: false);
    }

    // ---- Greeting ------------------------------------------------------------------------------------------------

    private void Greet()
    {
        if (_firstName is not { } name) return;
        if (name.IsCompleted) _greeting.Text = Greeting.Line(name.Status == TaskStatus.RanToCompletion ? name.Result : null);
        else name.ContinueWith(t => Dispatcher.BeginInvoke(new Action(Greet)), TaskScheduler.Default);
        AutomationProperties.SetName(_greeting, _greeting.Text);
    }

    /// <summary>
    /// The first name in the Windows account's display name, from what Windows and Office keep on this PC (the name the
    /// sign-in screen shows, Office's user name), then the sign-in name. Local reads only: never the network or a
    /// domain controller, so it is quick and works offline.
    /// </summary>
    private static string? WindowsFirstName()
    {
        string? logon = null, office = null;
        try
        {
            var user = Environment.UserName;
            var domainUser = Environment.UserDomainName + "\\" + user;
            bool Mine(object? who) => who is string w && (string.Equals(w, domainUser, StringComparison.OrdinalIgnoreCase) ||
                                                         w.EndsWith("\\" + user, StringComparison.OrdinalIgnoreCase));
            using var logonUi = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI");
            if (logonUi is not null)
            {
                using (var sessions = logonUi.OpenSubKey("SessionData"))
                    if (sessions is not null)
                        foreach (var id in sessions.GetSubKeyNames())
                        {
                            using var session = sessions.OpenSubKey(id);
                            if (session is null || !(Mine(session.GetValue("LoggedOnSAMUser")) || Mine(session.GetValue("LoggedOnUser")))) continue;
                            logon = session.GetValue("LoggedOnDisplayName") as string;
                            if (!string.IsNullOrWhiteSpace(logon)) break;
                        }
                if (string.IsNullOrWhiteSpace(logon) && Mine(logonUi.GetValue("LastLoggedOnSAMUser")))
                    logon = logonUi.GetValue("LastLoggedOnDisplayName") as string;
            }
            using var info = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\Common\UserInfo");
            office = info?.GetValue("UserName") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or InvalidOperationException) { }
        try { return Greeting.Pick(logon, office, Environment.UserName); }
        catch (Exception) { return null; } // the plain "Hey there!"
    }

    // ---- Discover cards ------------------------------------------------------------------------------------------

    /// <summary>A small letter-spaced, muted section label (spaced with hair spaces; read as the plain word).</summary>
    private static TextBlock SectionLabel(string text)
    {
        var spaced = string.Join("\u200A", text.ToUpperInvariant().ToCharArray());
        var label = Brand.Text(spaced, Brand.K.Muted, 11, bold: true);
        AutomationProperties.SetName(label, text);
        return label;
    }

    private void Card(string id, string icon, string title, string tip)
    {
        var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(ChatUi.RadiusSmall), Margin = new Thickness(0, 0, ChatUi.S3, 0), VerticalAlignment = VerticalAlignment.Top };
        tile.SetResourceReference(Border.BackgroundProperty, Brand.K.AccentSoft);
        var glyph = ChatUi.Icon(icon);
        Brand.Fg(glyph, Brand.K.Link);
        tile.Child = glyph;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Brand.Text(title, Brand.K.Text, ChatUi.BodySize, bold: true);
        text.Children.Add(name);
        var line = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
        line.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(line);
        var chevron = ChatUi.Icon(ChatUi.Paths.ChevronRight);
        Brand.Fg(chevron, Brand.K.Muted);
        chevron.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(tile, Dock.Left);
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(tile);
        row.Children.Add(chevron);
        row.Children.Add(text);
        var card = new Button { Content = row, ToolTip = tip, Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S2) };
        card.SetResourceReference(StyleProperty, "SpmChatCard");
        AutomationProperties.SetHelpText(card, tip + " Opens the list; Back or Esc returns here.");
        card.Click += (_, _) => Guarded(() => Show(id));
        _cardFor[id] = (card, line);
        _cards.Children.Add(card);
        Counts();
    }

    /// <summary>Two cards side by side when the pane is wide enough (the third spans both), one per row when narrow.</summary>
    private void LayoutCards()
    {
        var two = _cards.ActualWidth >= 420;
        _cards.ColumnDefinitions.Clear();
        _cards.RowDefinitions.Clear();
        _cards.ColumnDefinitions.Add(new ColumnDefinition());
        if (two) _cards.ColumnDefinitions.Add(new ColumnDefinition());
        var k = 0;
        foreach (var card in _cards.Children.OfType<Button>())
        {
            var row = two ? k / 2 : k;
            while (_cards.RowDefinitions.Count <= row) _cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(card, row);
            Grid.SetColumn(card, two ? k % 2 : 0);
            Grid.SetColumnSpan(card, two && k == _cards.Children.Count - 1 && k % 2 == 0 ? 2 : 1);
            k++;
        }
    }

    /// <summary>The counts on the cards.</summary>
    private void Counts()
    {
        void Set(string id, string title, string line)
        {
            if (!_cardFor.TryGetValue(id, out var c)) return;
            c.Line.Text = line;
            AutomationProperties.SetName(c.Card, title + ". " + line);
        }
        var features = _lib?.Capabilities.Count;
        Set("explore", "Explore all features", features is { } n ? $"{n} things Sketch ProMax can do" : "Browse what Sketch ProMax can do");
        var saved = _built ? _saved().Count : -1;
        Set("saved", "Saved prompts", saved switch { < 0 => "Your prompt library", 0 => "None yet: save one from any message", 1 => "1 saved prompt", _ => $"{saved} saved prompts" });
        Set("tailored", "Tailored prompts", "Ideas for your project type and phase");
    }

    /// <summary>Shows the home screen or one library view, and puts the keyboard where the reader continues.</summary>
    private void Show(string tab, bool focus = true)
    {
        var from = _tab;
        _tab = tab;
        var home = tab == "home";
        _home.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        _library.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        if (home)
        {
            _body.Content = null;
            _search = null;
            _results = null;
            if (focus && _cardFor.TryGetValue(from, out var c)) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => c.Card.Focus()));
            return;
        }
        if (!_built) Build();
        _libraryTitle.Text = tab switch { "saved" => "Saved prompts", "tailored" => "Tailored prompts", _ => "Explore all features" };
        _libraryCount.Text = tab switch { "saved" => _saved().Count.ToString(), "explore" => (_lib?.Capabilities.Count ?? 0).ToString(), _ => "" };
        Draw();
        if (focus)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_tab == "explore" && _search is not null) _search.Focus();
                else _back.Focus();
            }));
    }

    private void Build()
    {
        if (_built) return;
        _built = true;
        try
        {
            var registry = App.Bridge?.Registry ?? Tools.ToolRegistry.CreateDefault();
            _lib = PromptLibrary.Load(registry.All.Select(t => (t.Name, t.Description)).ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or TypeLoadException or MissingMethodException)
        {
            _lib = PromptLibrary.Load([]);
        }
        Counts();
    }

    /// <summary>A rounded choice pill; the selected one is drawn in the accent tint with an accent border.</summary>
    private static Button Pill(string text, bool selected, string tip)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S2), ToolTip = tip, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal };
        button.SetResourceReference(StyleProperty, "SpmChatPill");
        if (selected)
        {
            button.SetResourceReference(Control.BackgroundProperty, Brand.K.AccentSoft);
            button.SetResourceReference(Control.BorderBrushProperty, Brand.K.Accent);
        }
        AutomationProperties.SetName(button, text);
        AutomationProperties.SetHelpText(button, tip);
        return button;
    }

    private void Draw()
    {
        switch (_tab)
        {
            case "saved": DrawSaved(); break;
            case "tailored": DrawTailored(); break;
            default: DrawExplore(); break;
        }
    }

    // ---- Explore features ----------------------------------------------------------------------------------------

    private void DrawExplore()
    {
        if (_lib is null) return;
        var panel = new StackPanel();
        _search = new TextBox { Text = _query, FontSize = ChatUi.BodySize };
        _search.SetResourceReference(StyleProperty, "SpmChatField");
        var hint = Brand.Text($"Search {_lib.Capabilities.Count} capabilities", Brand.K.Muted, ChatUi.BodySize);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(13, 8, 0, 0);
        hint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_search, "Search capabilities");
        AutomationProperties.SetHelpText(_search, "Type words such as rebar, sheets or clash; the groups below show what matches.");
        _search.TextChanged += (_, _) =>
        {
            _query = _search.Text;
            hint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            DrawGroups();
        };
        var field = new Grid { Margin = new Thickness(0, 0, 0, ChatUi.S3) };
        field.Children.Add(_search);
        field.Children.Add(hint);
        panel.Children.Add(field);
        _results = new StackPanel();
        panel.Children.Add(_results);
        _body.Content = panel;
        DrawGroups();
    }

    private void DrawGroups()
    {
        if (_lib is null || _results is null) return;
        _results.Children.Clear();
        var searching = _query.Trim().Length > 0;
        var any = false;
        foreach (var (group, blurb) in _lib.Groups)
        {
            var found = _lib.Find(group, _query).ToList();
            if (found.Count == 0) continue;
            any = true;
            var open = searching || _open.Contains(group);
            var head = new DockPanel();
            var chevron = ChatUi.Glyph(open ? ChatUi.GlyphDown : ChatUi.GlyphRight, Brand.K.Muted, 9);
            chevron.Margin = new Thickness(0, 0, ChatUi.S2, 0);
            DockPanel.SetDock(chevron, Dock.Left);
            head.Children.Add(chevron);
            var count = ChatUi.Small(found.Count.ToString(), Brand.K.Muted, bold: true);
            DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(count);
            var names = new StackPanel();
            names.Children.Add(ChatUi.Small(group, Brand.K.Text, bold: true));
            names.Children.Add(ChatUi.Small(blurb));
            head.Children.Add(names);
            var toggle = new ToggleButton { Content = head, IsChecked = open };
            toggle.SetResourceReference(StyleProperty, "SpmChatDisclosure");
            AutomationProperties.SetName(toggle, $"{group}, {found.Count} capabilities. {blurb}");
            AutomationProperties.SetItemStatus(toggle, open ? "Expanded" : "Collapsed");
            toggle.Click += (_, _) => Guarded(() =>
            {
                if (!_open.Remove(group)) _open.Add(group);
                DrawGroups();
                FocusGroup(group);
            });
            toggle.Tag = group;
            _results.Children.Add(toggle);
            if (!open) continue;
            var list = new StackPanel { Margin = new Thickness(ChatUi.S4, 0, 0, ChatUi.S2) };
            var limit = searching && !_showAll.Contains(group) ? PerGroupWhenSearching : int.MaxValue;
            foreach (var cap in found.Take(limit)) list.Children.Add(Idea(cap.Title, cap.Summary, cap.Prompt));
            if (found.Count > limit)
            {
                var more = ChatUi.Ghost(ChatUi.GlyphDown, $"Show all {found.Count}", $"Show all {found.Count} in {group}", "Lists every match in this group.");
                more.HorizontalAlignment = HorizontalAlignment.Left;
                more.Click += (_, _) => Guarded(() =>
                {
                    _showAll.Add(group);
                    DrawGroups();
                });
                list.Children.Add(more);
            }
            _results.Children.Add(list);
        }
        if (!any)
        {
            var none = Brand.Text("Nothing matches. Try another word, or just ask Claude in the box below.", Brand.K.Muted, ChatUi.SmallSize + 0.5);
            _results.Children.Add(none);
        }
    }

    private void FocusGroup(string group) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            _results?.Children.OfType<ToggleButton>().FirstOrDefault(t => (string)t.Tag == group)?.Focus()));

    /// <summary>One idea: its title and a line on it; choosing it puts the prompt in the message box.</summary>
    private Button Idea(string title, string summary, string prompt)
    {
        var text = new StackPanel();
        var name = ChatUi.Small(title, Brand.K.Text, bold: true);
        text.Children.Add(name);
        if (summary.Length > 0)
        {
            var line = Brand.Text(summary, Brand.K.Muted, ChatUi.SmallSize + 0.5);
            line.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(line);
        }
        var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(ChatUi.S2, 4, ChatUi.S2, 4), ToolTip = prompt };
        button.SetResourceReference(StyleProperty, "SpmChatGhost");
        AutomationProperties.SetName(button, title + ". " + summary);
        AutomationProperties.SetHelpText(button, "Puts this prompt in the message box to edit before sending.");
        button.Click += (_, _) => Guarded(() => _use(prompt));
        return button;
    }

    /// <summary>Runs a click's work; nothing it throws may reach Revit's dispatcher (that would end Revit).</summary>
    private static void Guarded(Action work)
    {
        try { work(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Sketch ProMax home screen: " + ex); }
    }

    // ---- Saved prompts -------------------------------------------------------------------------------------------

    private void DrawSaved()
    {
        var panel = new StackPanel();
        var saved = _saved();
        if (saved.Count == 0)
            panel.Children.Add(Brand.Text("No saved prompts yet. Save one from any message: ⋯ › Save prompt. They are kept on this PC for your Windows account.", Brand.K.Muted, ChatUi.SmallSize + 0.5));
        foreach (var prompt in saved)
            panel.Children.Add(Idea(prompt.Title, prompt.Text.Length > prompt.Title.Length + 2 ? ChatHistory.AutoTitle(prompt.Text, 120) : "", prompt.Text));
        _body.Content = panel;
    }

    // ---- Tailored prompts ----------------------------------------------------------------------------------------

    private void DrawTailored()
    {
        if (_lib is null) return;
        var panel = new StackPanel();
        WrapPanel Choices(string label, IEnumerable<PromptLibrary.Named> options, string current, Action<string> pick)
        {
            var heading = SectionLabel(label);
            heading.Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : ChatUi.S2, 0, 0);
            panel.Children.Add(heading);
            var row = new WrapPanel { Margin = new Thickness(0, ChatUi.S2, 0, ChatUi.S1) };
            foreach (var option in options)
            {
                var selected = string.Equals(option.Id, current, StringComparison.OrdinalIgnoreCase);
                var button = Pill(option.Name, selected, $"{label}: {option.Name}");
                AutomationProperties.SetItemStatus(button, selected ? "Selected" : "");
                var id = option.Id;
                button.Click += (_, _) => Guarded(() =>
                {
                    pick(id);
                    DrawTailored();
                });
                row.Children.Add(button);
            }
            panel.Children.Add(row);
            return row;
        }
        Choices("Project type", _lib.ProjectTypes, _type, id => _type = id);
        Choices("Phase", _lib.Phases, _phase, id => _phase = id);
        var ideas = _lib.For(_type, _phase).ToList();
        if (ideas.Count == 0) panel.Children.Add(Brand.Text("No ideas for this pair yet.", Brand.K.Muted, ChatUi.SmallSize + 0.5));
        foreach (var idea in ideas) panel.Children.Add(Idea(idea.Title, PromptLibrary.FirstSentence(idea.Prompt), idea.Prompt));
        _body.Content = panel;
    }
}
