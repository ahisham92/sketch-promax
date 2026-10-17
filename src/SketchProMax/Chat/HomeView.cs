using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The chat's home screen, shown while a chat is empty: a greeting, then three tabs. Explore features is a searchable
/// library of what Claude can do here, grouped like the ribbon with a count per group; Saved prompts are the user's
/// own; Tailored prompts are picked by project type and phase. Choosing a prompt puts it in the message box to edit.
/// The library is read the first time the home screen is shown, not when the pane is made.
/// </summary>
internal sealed class HomeView : StackPanel
{
    private const int PerGroupWhenSearching = 12;

    private readonly Action<string> _use;
    private readonly Func<List<SavedPrompt>> _saved;
    private readonly TextBlock _greeting = Brand.Text("", Brand.K.Text, 16, bold: true);
    private readonly TextBlock _sub = Brand.Text("", Brand.K.Muted, ChatUi.BodySize);
    private readonly WrapPanel _tabs = new() { Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S2) };
    private readonly ContentControl _body = new() { Focusable = false };
    private PromptLibrary? _library;
    private string _tab = "explore", _query = "", _type = "building", _phase = "concept";
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _showAll = new(StringComparer.OrdinalIgnoreCase);
    private TextBox? _search;
    private StackPanel? _results;
    private bool _built;

    public HomeView(Action<string> use, Func<List<SavedPrompt>> saved)
    {
        _use = use;
        _saved = saved;
        Margin = new Thickness(0, ChatUi.S4, 0, ChatUi.S2);
        Children.Add(_greeting);
        _sub.Margin = new Thickness(0, 2, 0, 0);
        Children.Add(_sub);
        Children.Add(_tabs);
        Children.Add(_body);
        AutomationProperties.SetName(this, "Home: ideas to start a chat");
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Greet();
            if (!_built) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Build));
            else if (_tab == "saved") Draw();
        };
    }

    /// <summary>Saved prompts changed: redraw that tab when it shows.</summary>
    public void SavedChanged()
    {
        if (_built) DrawTabs();
        if (_built && _tab == "saved") Draw();
    }

    private void Greet()
    {
        var hour = DateTime.Now.Hour;
        _greeting.Text = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        var model = ChatPane.CurrentModelTitle;
        _sub.Text = model.Length > 0 ? $"What shall we do in {model}? Pick an idea below to edit it first, or type your own." : "Open a model, then pick an idea below or type your own.";
    }

    private void Build()
    {
        if (_built) return;
        _built = true;
        try
        {
            var registry = App.Bridge?.Registry ?? Tools.ToolRegistry.CreateDefault();
            _library = PromptLibrary.Load(registry.All.Select(t => (t.Name, t.Description)).ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or TypeLoadException or MissingMethodException)
        {
            _library = PromptLibrary.Load([]);
        }
        DrawTabs();
        Draw();
    }

    private void DrawTabs()
    {
        _tabs.Children.Clear();
        Tab("explore", $"Explore features ({_library?.Capabilities.Count ?? 0})", "Everything Claude can do here, grouped like the ribbon.");
        Tab("saved", $"Saved prompts ({_saved().Count})", "Your prompts, saved from any message with ⋯ › Save prompt.");
        Tab("tailored", "Tailored prompts", "Ideas for your project type and design phase.");
    }

    private void Tab(string id, string text, string tip)
    {
        var selected = _tab == id;
        var button = Pill(text, selected, tip);
        AutomationProperties.SetItemStatus(button, selected ? "Selected" : "");
        button.Click += (_, _) =>
        {
            _tab = id;
            DrawTabs();
            Draw();
            if (_tabs.Children.OfType<Button>().FirstOrDefault(b => AutomationProperties.GetItemStatus(b) == "Selected") is { } now) now.Focus();
        };
        _tabs.Children.Add(button);
    }

    private static Button Pill(string text, bool selected, string tip)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S2), ToolTip = tip, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal };
        button.SetResourceReference(StyleProperty, "SpmChatPill");
        if (selected)
        {
            button.SetResourceReference(Control.BorderBrushProperty, Brand.K.Accent);
            button.BorderThickness = new Thickness(2);
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
        if (_library is null) return;
        var panel = new StackPanel();
        _search = new TextBox { Text = _query, MinHeight = 28, Padding = new Thickness(ChatUi.S2, 4, ChatUi.S2, 4) };
        var hint = Brand.Text($"Search {_library.Capabilities.Count} capabilities", Brand.K.Muted, 12);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(ChatUi.S2 + 3, 6, 0, 0);
        hint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_search, "Search capabilities");
        AutomationProperties.SetHelpText(_search, "Type words such as rebar, sheets or clash; the groups below show what matches.");
        _search.TextChanged += (_, _) =>
        {
            _query = _search.Text;
            hint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            DrawGroups();
        };
        var field = new Grid { Margin = new Thickness(0, 0, 0, ChatUi.S2) };
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
        if (_library is null || _results is null) return;
        _results.Children.Clear();
        var searching = _query.Trim().Length > 0;
        var any = false;
        foreach (var (group, blurb) in _library.Groups)
        {
            var found = _library.Find(group, _query).ToList();
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
            toggle.Click += (_, _) =>
            {
                if (!_open.Remove(group)) _open.Add(group);
                DrawGroups();
                FocusGroup(group);
            };
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
                more.Click += (_, _) =>
                {
                    _showAll.Add(group);
                    DrawGroups();
                };
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
        button.Click += (_, _) => _use(prompt);
        return button;
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
        if (_library is null) return;
        var panel = new StackPanel();
        WrapPanel Choices(string label, IEnumerable<PromptLibrary.Named> options, string current, Action<string> pick)
        {
            panel.Children.Add(ChatUi.Small(label, Brand.K.Muted, bold: true));
            var row = new WrapPanel { Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S1) };
            foreach (var option in options)
            {
                var selected = string.Equals(option.Id, current, StringComparison.OrdinalIgnoreCase);
                var button = Pill(option.Name, selected, $"{label}: {option.Name}");
                AutomationProperties.SetItemStatus(button, selected ? "Selected" : "");
                var id = option.Id;
                button.Click += (_, _) =>
                {
                    pick(id);
                    DrawTailored();
                };
                row.Children.Add(button);
            }
            panel.Children.Add(row);
            return row;
        }
        Choices("Project type", _library.ProjectTypes, _type, id => _type = id);
        Choices("Phase", _library.Phases, _phase, id => _phase = id);
        var ideas = _library.For(_type, _phase).ToList();
        if (ideas.Count == 0) panel.Children.Add(Brand.Text("No ideas for this pair yet.", Brand.K.Muted, ChatUi.SmallSize + 0.5));
        foreach (var idea in ideas) panel.Children.Add(Idea(idea.Title, PromptLibrary.FirstSentence(idea.Prompt), idea.Prompt));
        _body.Content = panel;
    }
}
