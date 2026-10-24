using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using SketchProMax.Revit;
using SketchProMax.Settings;
using SketchProMax.Tools;
using static SketchProMax.Tools.SubmissionChecklistMath;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using RadioButton = System.Windows.Controls.RadioButton;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace SketchProMax.UI;

/// <summary>
/// The technical submission checklist (opened from Submissions status for a submission, and from the Model checklist):
/// one checklist per submission, page by page. The engineer answers each short question Yes, No or N/A (details and a
/// sketch on demand, a comment where it helps); the group leader approves or returns each answer. An N/A stays in view
/// until the group leader approves it, then it is hidden. The project's structure types (Project setup) decide which
/// questions are asked; anyone can add a question. The BIM part is the Model checklist, shown as its score.
///
/// Shared through the model (Revit/SubmissionChecklistStore, one record per person): changes are saved into the model a
/// moment after they are made, and Synchronize with Central shares them; coming back to the window after Reload Latest
/// shows the others'. Each change is also kept on this PC. Ends in a Word file or PDF (SubmissionChecklistDocx).
/// </summary>
internal sealed class SubmissionChecklistWindow : Window
{
    private static SubmissionChecklistWindow? _open;

    private readonly string _docKey, _model, _project, _user;
    private List<UserRecord> _others;
    private readonly UserRecord _mine;
    private Shared _shared = new();
    private List<Question> _questions = [];
    private string _selected = "";
    private string _othersStamp;
    private DateTime _readAt = DateTime.Now;
    private bool _closed, _dirty, _writing, _loading;
    private string _lastFile = "";

    private readonly TabControl _pages = new();
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted);
    private readonly ScoreRing _checkedRing, _approvedRing, _bimRing;
    private readonly DispatcherTimer _saveSoon = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly Chat.Motion _motion;
    private readonly Dictionary<string, Border> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pageOf = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openDetails = new(StringComparer.Ordinal), _openComments = new(StringComparer.Ordinal);
    private readonly List<(TextBlock Sub, Border Circle, TextBlock Glyph, List<Question> Items)> _heads = [];

    private Submission? Current => _shared.Find(_selected);
    private bool IsLead => _shared.IsLead(_user);
    private bool SetupEditable => !_shared.Setup.Done || IsLead;
    private List<string> ProjectTypes => _shared.Setup.Types.Where(t => TypeOf(t) is not null).ToList();

    // ---- Opening --------------------------------------------------------------------------------------------------

    /// <summary>Shows the checklist for the active model; with a package and name, on that submission (started if new).</summary>
    public static Result Command(UIApplication app, string package = "", string name = "", DateTime? due = null)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            TaskDialog.Show("Sketch ProMax", "Open the project model first: the submission checklist is kept in the model, so everyone working on it sees the same answers.");
            return Result.Cancelled;
        }
        var key = DocKey(doc);
        if (_open is { IsLoaded: true })
        {
            if (_open._docKey == key)
            {
                if (name.Trim().Length > 0) _open.Pick(package, name, due);
                _open.Activate();
                return Result.Succeeded;
            }
            _open.Close();
        }
        List<UserRecord> records;
        try { records = SubmissionChecklistStore.ReadAll(doc); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { records = []; Debug.WriteLine(ex); }
        var window = new SubmissionChecklistWindow(doc, records, package, name, due);
        _open = window;
        window.Closed += (_, _) => { if (_open == window) _open = null; };
        window.Show();
        return Result.Succeeded;
    }

    private static string DocKey(Autodesk.Revit.DB.Document doc)
    {
        try { return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName; }
        catch (Exception) { return doc.Title; }
    }

    private static string FileOf(Autodesk.Revit.DB.Document doc)
    {
        try { return string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileName(doc.PathName); }
        catch (Exception) { return doc.Title; }
    }

    private SubmissionChecklistWindow(Autodesk.Revit.DB.Document doc, List<UserRecord> records, string package, string name, DateTime? due)
    {
        _docKey = DocKey(doc);
        _model = FileOf(doc);
        _project = ChecklistProjects.ProjectKey(_docKey);
        _user = SubmissionChecklistStore.UserOf(doc);
        // The model's record and this PC's copy: the newer one is the user's (a write that failed is sent again).
        var inModel = records.FirstOrDefault(r => Same(r.User, _user));
        var onPc = LoadOwn(ChecklistMath.WorkingName(_model), _user);
        _mine = onPc is not null && (inModel is null || onPc.Saved > inModel.Saved) ? onPc : inModel ?? new UserRecord { User = _user };
        _mine.User = _user;
        _dirty = !ReferenceEquals(_mine, inModel) && _mine.Saved > DateTime.MinValue;
        _others = records.Where(r => !Same(r.User, _user)).ToList();
        _othersStamp = Stamp(_others);
        Remerge();
        if (name.Trim().Length > 0 && _shared.Find(KeyOf(package, name)) is null) { AddSubmission(_mine, package, name, due, DateTime.UtcNow); _dirty = true; Remerge(); }
        _selected = name.Trim().Length > 0 ? KeyOf(package, name) : _shared.Submissions.FirstOrDefault()?.Key ?? "";

        Title = "Sketch ProMax submission checklist";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1280, Math.Floor(area.Width * 0.9));
        Height = Math.Min(860, Math.Floor(area.Height * 0.9));
        MinWidth = Math.Min(860, Width);
        MinHeight = Math.Min(500, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        var settings = AgentSettings.Load();
        _motion = RichUi.MotionOf(settings);
        RichUi.Apply(this, _motion);
        if (ChecklistWindow.StageStyles() is { } styles) { Resources.MergedDictionaries.Add(styles); _pages.Style = (Style)styles["SpmStages"]; }
        else _pages.TabStripPlacement = Dock.Left;
        _checkedRing = new ScoreRing("Checked", _motion);
        _approvedRing = new ScoreRing("Approved", _motion);
        _bimRing = new ScoreRing("BIM checklist", _motion);
        _bimRing.Clicked = () => GoTo(_pages.Items.Count - 2);
        _approvedRing.Clicked = () => GoTo(_pages.Items.Count - 1);
        _checkedRing.Clicked = () => GoTo(_pages.Items.Count - 1);
        _saveSoon.Tick += async (_, _) => { _saveSoon.Stop(); await SaveNow(); };
        Activated += async (_, _) => await ReadAgain(quiet: true);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { _closed = true; _saveSoon.Stop(); if (_dirty) { SaveOwn(ChecklistMath.WorkingName(_model), _mine); _ = SaveNow(); } };
        UpdateBim();
        Build();
        if (_dirty) _saveSoon.Start();
    }

    private static string Stamp(IEnumerable<UserRecord> records) =>
        string.Join("|", records.OrderBy(r => r.User, StringComparer.OrdinalIgnoreCase).Select(r => r.User + "@" + r.Saved.Ticks.ToString(CultureInfo.InvariantCulture)));

    private void Remerge()
    {
        _shared = Merge(_others.Concat([_mine]));
        _questions = QuestionsFor(_shared.Setup, _shared.Added);
    }

    /// <summary>Opens on a submission (started in the user's record when new), from Submissions status.</summary>
    private void Pick(string package, string name, DateTime? due)
    {
        var key = KeyOf(package, name);
        if (_shared.Find(key) is null) { AddSubmission(_mine, package, name, due, DateTime.UtcNow); Remerge(); Changed(); }
        _selected = key;
        UpdateBim();
        Build();
    }

    // ---- Layout ---------------------------------------------------------------------------------------------------

    private void Build()
    {
        _loading = true;
        var keepPage = Math.Max(0, _pages.SelectedIndex);
        _cards.Clear();
        _pageOf.Clear();
        _heads.Clear();
        _pages.Items.Clear();
        foreach (var reused in new FrameworkElement[] { _pages, _status, _checkedRing, _approvedRing, _bimRing })
            if (reused.Parent is System.Windows.Controls.Panel old) old.Children.Remove(reused);

        // Header: the submission, and its three scores always in view.
        var s = Current;
        var band = Brand.Header("Submission checklist", s is null ? "Pick or start a submission" : $"{s.Title} · {ChecklistMath.WorkingName(_model)}");
        band.Padding = new Thickness(16, 12, 300, 12);
        var header = new Grid();
        header.Children.Add(band);
        var rings = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        rings.Children.Add(_checkedRing);
        rings.Children.Add(_approvedRing);
        rings.Children.Add(_bimRing);
        header.Children.Add(rings);
        DockPanel.SetDock(header, Dock.Top);

        // The bar under it: which submission, who you are, read the model again.
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(0) };
        var subs = new ComboBox { MinWidth = 280, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(subs, "Submission");
        foreach (var sub in _shared.Submissions)
        {
            var p = Count(_questions, sub);
            subs.Items.Add(new ComboBoxItem { Content = $"{sub.Title}{(sub.Due is { } d ? " · due " + d.ToString("dd MMM", CultureInfo.CurrentCulture) : "")} · {p.CheckedPercent}% checked", Tag = sub.Key });
        }
        subs.SelectedIndex = _shared.Submissions.FindIndex(x => x.Key == _selected);
        subs.SelectionChanged += (_, _) =>
        {
            if (_loading || subs.SelectedItem is not ComboBoxItem { Tag: string key }) return;
            _selected = key;
            UpdateBim();
            Build();
        };
        var add = RichUi.Button(RichUi.Icon.Add, "New submission");
        add.Click += (_, _) => GoTo(0);
        AutomationProperties.SetHelpText(add, "Start the checklist for another submission (on Project setup).");
        var role = Brand.Text(IsLead ? $"You: group leader ({_user})" : $"You: engineer ({_user})", Brand.K.Muted, 12, bold: true);
        role.VerticalAlignment = VerticalAlignment.Center;
        role.ToolTip = IsLead ? "You approve or return the answers." : "You answer; a group leader approves. Group leaders are set on Project setup.";
        var refresh = RichUi.Button(RichUi.Icon.Refresh, "Show the latest");
        AutomationProperties.SetHelpText(refresh, "Reads everyone's answers from the model again. After Reload Latest (or Synchronize), it shows what the others did.");
        refresh.Click += async (_, _) => await ReadAgain(quiet: false);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Brand.Text("Submission", Brand.K.Muted, 12, bold: true));
        left.Children[0].SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        ((FrameworkElement)left.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(subs);
        left.Children.Add(add);
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(role);
        refresh.Margin = new Thickness(12, 0, 0, 0);
        right.Children.Add(refresh);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        var barBox = new Border { Padding = new Thickness(18, 8, 18, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        barBox.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        barBox.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        DockPanel.SetDock(barBox, Dock.Top);

        // Footer: what was saved, Back / Next, Close.
        _status.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var back = RichUi.Button(RichUi.Icon.Back, "Back");
        back.Click += (_, _) => GoTo(_pages.SelectedIndex - 1);
        var next = RichUi.Button(RichUi.Icon.Next, "Next", Brand.Weight.Primary);
        next.Click += (_, _) => GoTo(_pages.SelectedIndex + 1);
        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var buttons = Brand.ButtonRow(close, back, next);
        buttons.Margin = new Thickness(0);
        var foot = new DockPanel { Margin = new Thickness(18, 10, 18, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 16, 0);
        foot.Children.Add(_status);
        var footBox = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };
        footBox.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        DockPanel.SetDock(footBox, Dock.Bottom);

        // Pages: setup, one per topic with questions, the BIM checklist, review and export.
        AutomationProperties.SetName(_pages, "Checklist pages");
        AddPage("Project setup", SetupPage(), []);
        if (s is not null && _shared.Setup.Done)
            foreach (var (topic, items) in SubmissionChecklistDocx.Pages(_questions))
            {
                foreach (var q in items) _pageOf[q.Id] = _pages.Items.Count;
                AddPage(topic.Title, TopicPage(topic, items, s), items);
            }
        AddPage("BIM checklist", BimPage(), []);
        AddPage("Review and export", ReviewPage(), []);

        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(barBox);
        root.Children.Add(footBox);
        root.Children.Add(_pages);
        Content = root;
        _pages.SelectedIndex = !_shared.Setup.Done || s is null ? 0 : Math.Min(keepPage, _pages.Items.Count - 1);
        _pages.SelectionChanged -= PageChanged;
        _pages.SelectionChanged += PageChanged;
        _loading = false;
        RefreshScores();
    }

    private void PageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != _pages || _loading) return;
        if (_pages.SelectedItem is TabItem { Content: FrameworkElement page }) RichUi.FadeIn(page, _motion, fromX: 14, fromY: 0);
    }

    private void AddPage(string name, FrameworkElement content, List<Question> items)
    {
        var glyph = Brand.Text((_pages.Items.Count + 1).ToString(CultureInfo.InvariantCulture), size: 12, bold: true);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1.5), Child = glyph, VerticalAlignment = VerticalAlignment.Center };
        circle.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        var title = Brand.Text(name, size: 13);
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxWidth = 190;
        var sub = Brand.Text("", Brand.K.Muted, size: 11);
        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { title, sub } };
        _heads.Add((sub, circle, glyph, items));
        var tab = new TabItem { Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { circle, texts } }, Content = Scroll(content) };
        AutomationProperties.SetName(tab, $"Page {_pages.Items.Count + 1}: {name}");
        _pages.Items.Add(tab);
    }

    private void GoTo(int index)
    {
        if (index < 0 || index >= _pages.Items.Count) return;
        _pages.SelectedIndex = index;
    }

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        Padding = new Thickness(24, 16, 24, 24),
    };

    private static TextBlock Heading(string text, double size = 18)
    {
        var h = Brand.Text(text, size: size, bold: true);
        h.TextWrapping = TextWrapping.Wrap;
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(h, AutomationHeadingLevel.Level2);
#endif
        return h;
    }

    private static TextBlock Para(string text, string key = Brand.K.Text, double size = 12.5, double top = 4)
    {
        var t = Brand.Text(text, key, size);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, top, 0, 0);
        return t;
    }

    private static Border Box(UIElement child, double top = 12, string bg = Brand.K.Raised)
    {
        var b = new Border { Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, top, 0, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        b.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        return b;
    }

    private static Button Link(string text, string? help = null)
    {
        var b = new Button { Content = Brand.Text(text, Brand.K.Link, 12, bold: true), Padding = new Thickness(4, 1, 4, 2), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand };
        b.SetResourceReference(StyleProperty, "SpmLinkButton");
        AutomationProperties.SetName(b, text);
        if (help is not null) { b.ToolTip = help; AutomationProperties.SetHelpText(b, help); }
        return b;
    }

    private static Border Chip(string text, string bg, string fg = Brand.K.OnAccent)
    {
        var chip = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 0, 6, 0), Child = Brand.Text(text, fg, 11, bold: true), VerticalAlignment = VerticalAlignment.Center };
        chip.SetResourceReference(Border.BackgroundProperty, bg);
        return chip;
    }

    // ---- Project setup --------------------------------------------------------------------------------------------

    private FrameworkElement SetupPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("Project setup"));
        page.Children.Add(Para(_shared.Setup.Done
            ? $"Set by {_shared.Setup.By}" + (_shared.Setup.At > DateTime.MinValue ? $" on {_shared.Setup.At.ToLocalTime():dd MMM yyyy}" : "") + (SetupEditable ? ". You can change it." : ". Only a group leader can change it.")
            : "Once per project: pick its structures so only their questions are asked, and say who approves.", Brand.K.Muted));

        // Structures.
        var picked = new HashSet<string>(_shared.Setup.Types, StringComparer.Ordinal);
        page.Children.Add(Para("Which structures does the project have?", size: 14, top: 16));
        ((TextBlock)page.Children[page.Children.Count - 1]).FontWeight = FontWeights.SemiBold;
        var tiles = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var boxes = new List<(string Key, CheckBox Box)>();
        foreach (var t in Types)
        {
            var check = new CheckBox { IsChecked = picked.Contains(t.Key), IsEnabled = SetupEditable, Content = Brand.Text(t.Name, size: 12.5, bold: true), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(check, t.Name);
            AutomationProperties.SetHelpText(check, t.About);
            var stack = new StackPanel { Width = 196 };
            stack.Children.Add(check);
            if (SubmissionChecklistDiagrams.Find(t.Key) is { } d)
            {
                var view = SubmissionChecklistDiagramView.View(d, 196);
                view.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(view);
            }
            var about = Para(t.About, Brand.K.Muted, 11, 4);
            about.MaxHeight = 44;
            about.TextTrimming = TextTrimming.WordEllipsis;
            about.ToolTip = t.About;
            stack.Children.Add(about);
            var tile = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Child = stack, Cursor = SetupEditable ? Cursors.Hand : null };
            void Paint()
            {
                var on = check.IsChecked == true;
                tile.SetResourceReference(Border.BackgroundProperty, on ? Brand.K.AccentSoft : Brand.K.Bg);
                tile.SetResourceReference(Border.BorderBrushProperty, on ? Brand.K.Accent : Brand.K.Border);
            }
            Paint();
            check.Checked += (_, _) => Paint();
            check.Unchecked += (_, _) => Paint();
            tile.MouseLeftButtonUp += (_, _) => { if (SetupEditable && !check.IsMouseOver) check.IsChecked = check.IsChecked != true; };
            tiles.Children.Add(tile);
            boxes.Add((t.Key, check));
        }
        page.Children.Add(tiles);

        // Who approves.
        page.Children.Add(Para("Who approves the answers (group leaders)?", size: 14, top: 10));
        ((TextBlock)page.Children[page.Children.Count - 1]).FontWeight = FontWeights.SemiBold;
        var leads = new TextBox { Text = _shared.Setup.Leads.Count > 0 ? string.Join(", ", _shared.Setup.Leads) : _user, IsEnabled = SetupEditable, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Width = 420, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(leads, "Group leaders");
        page.Children.Add(leads);
        page.Children.Add(Para($"Their Revit user names, comma separated (Revit: Options > General > Username). Yours is \"{_user}\". Everyone else answers as an engineer.", Brand.K.Muted, 11.5));
        if (SetupEditable)
        {
            var save = RichUi.Button(RichUi.Icon.Save, _shared.Setup.Done ? "Save the setup" : "Start the checklist", Brand.Weight.Primary);
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Margin = new Thickness(0, 12, 0, 0);
            save.Click += (_, _) =>
            {
                var types = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Key).ToList();
                var names = Split(leads.Text);
                if (types.Count == 0) { _status.Text = "Pick at least one structure."; return; }
                if (names.Count == 0) { _status.Text = "Name at least one group leader."; return; }
                // Keep the order types were first picked in (the structure page follows it).
                var ordered = _shared.Setup.Types.Where(types.Contains).Concat(types.Where(t => !_shared.Setup.Types.Contains(t))).ToList();
                _mine.Setup = new Setup { Types = ordered, Leads = names, Skipped = _shared.Setup.Skipped.ToList(), By = _user, At = DateTime.UtcNow };
                _mine.Saved = DateTime.UtcNow;
                Remerge();
                Changed();
                _status.Text = $"Setup saved: {ordered.Count} structure types, {_questions.Count} questions.";
                Build();
                if (Current is not null) GoTo(1);
            };
            page.Children.Add(save);
        }

        // Submissions.
        page.Children.Add(Para("Submissions", size: 14, top: 22));
        ((TextBlock)page.Children[page.Children.Count - 1]).FontWeight = FontWeights.SemiBold;
        page.Children.Add(Para("Each submission has its own checklist. Opening it from Submissions status starts it for you.", Brand.K.Muted, 11.5));
        foreach (var sub in _shared.Submissions)
        {
            var p = Count(_questions, sub);
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var open = Link(sub.Key == _selected ? "Open (shown)" : "Open");
            open.Click += (_, _) => { _selected = sub.Key; UpdateBim(); Build(); GoTo(1); };
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            row.Children.Add(Brand.Text($"{sub.Title}{(sub.Due is { } d ? " · due " + d.ToString("ddd dd MMM", CultureInfo.CurrentCulture) : "")} · {p.CheckedPercent}% checked · {p.ApprovedPercent}% approved · {StatusText(p)}", size: 12.5));
            page.Children.Add(row);
        }
        // Submissions the Submissions status setup knows for this model, one click to start.
        var known = KnownSubmissions().Where(k => _shared.Find(KeyOf(k.Package, k.Name)) is null).Take(12).ToList();
        if (known.Count > 0)
        {
            var quick = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            quick.Children.Add(Brand.Text("From Submissions status:", Brand.K.Muted, 12));
            ((FrameworkElement)quick.Children[0]).Margin = new Thickness(0, 4, 8, 0);
            foreach (var k in known)
            {
                var b = Link($"+ {k.Package} · {k.Name}");
                b.Click += (_, _) => { StartSubmission(k.Package, k.Name, k.Due); };
                quick.Children.Add(b);
            }
            page.Children.Add(quick);
        }
        var form = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var pkg = new TextBox { Width = 120, Margin = new Thickness(0, 0, 8, 4) };
        var nm = new TextBox { Width = 220, Margin = new Thickness(0, 0, 8, 4) };
        var dueBox = new DatePicker { Width = 140, Margin = new Thickness(0, 0, 8, 4) };
        AutomationProperties.SetName(pkg, "Package, e.g. P1");
        AutomationProperties.SetName(nm, "Submission name, e.g. Detailed design");
        AutomationProperties.SetName(dueBox, "Due date");
        var start = RichUi.Button(RichUi.Icon.Add, "Start this submission");
        start.Click += (_, _) =>
        {
            if (nm.Text.Trim().Length == 0 && pkg.Text.Trim().Length == 0) { _status.Text = "Type the package or the submission's name."; return; }
            StartSubmission(pkg.Text, nm.Text, dueBox.SelectedDate);
        };
        form.Children.Add(Labelled("Package", pkg));
        form.Children.Add(Labelled("Submission", nm));
        form.Children.Add(Labelled("Due", dueBox));
        start.VerticalAlignment = VerticalAlignment.Bottom;
        start.Margin = new Thickness(0, 0, 0, 4);
        form.Children.Add(start);
        page.Children.Add(form);

        // How sharing works, folded.
        var how = new StackPanel();
        how.Children.Add(Para("Answers, approvals and added questions are kept in the model, one record per person, so nobody waits for anybody. " +
                              "Everything you change is saved into the model a moment later; Synchronize with Central sends it. To see the others' work, Reload Latest (or Synchronize), then come back to this window or press Show the latest.", Brand.K.Muted, 12));
        how.Children.Add(Para("Engineers answer Yes, No or N/A. The group leader approves Yes and N/A, or returns any answer with a reason. An N/A is hidden only once the group leader approves it. Changing an answer after it was approved asks for a new approval.", Brand.K.Muted, 12));
        page.Children.Add(new Expander { Header = "How sharing and approval work", Content = how, Margin = new Thickness(0, 18, 0, 0) });
        return page;
    }

    private static FrameworkElement Labelled(string label, FrameworkElement field)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        s.Children.Add(Brand.Text(label, Brand.K.Muted, 11, bold: true));
        s.Children.Add(field);
        return s;
    }

    private void StartSubmission(string package, string name, DateTime? due)
    {
        _selected = AddSubmission(_mine, package, name, due, DateTime.UtcNow);
        Remerge();
        Changed();
        UpdateBim();
        Build();
        if (_shared.Setup.Done) GoTo(1);
    }

    /// <summary>This model's submissions in the Submissions status setup on this PC (package, name, due).</summary>
    private List<(string Package, string Name, DateTime? Due)> KnownSubmissions()
    {
        try
        {
            if (BerthStatusMath.LoadSetup(_project) is not { } setup) return [];
            var model = BerthStatusMath.ModelFor(setup, _model);
            return setup.Berths.Where(b => model is null || BerthStatusMath.Same(b.Model, model.Name))
                .SelectMany(b => BerthStatusMath.SubmissionsFor(setup, b).Where(x => !x.Submitted).Take(1).Select(x => (b.Name, x.Name, (DateTime?)x.Date)))
                .Distinct().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return []; }
    }

    // ---- A page of questions --------------------------------------------------------------------------------------

    private FrameworkElement TopicPage(Topic topic, List<Question> items, Submission s)
    {
        var page = new StackPanel();
        page.Children.Add(Heading(topic.Title));
        page.Children.Add(Para(topic.About, Brand.K.Muted));
        var types = ProjectTypes;
        string? group = null;
        var number = _questions.IndexOf(items[0]);
        foreach (var q in items)
        {
            number++;
            if (StateOf(s, q.Id) == ItemState.Hidden) continue;
            if (topic.Key == TopicTypes && TypeOfQuestion(q, types) is var g && g != group)
            {
                group = g;
                var head = Brand.Text(TypeOf(g)?.Name ?? "Other", Brand.K.Link, 13.5, bold: true);
                head.Margin = new Thickness(0, 18, 0, 0);
                page.Children.Add(head);
            }
            var card = new Border { Margin = new Thickness(0, 10, 0, 0) };
            _cards[q.Id] = card;
            card.Tag = number;
            Fill(card, q, s, number);
            page.Children.Add(card);
        }

        // The N/A the group leader agreed: hidden, listed folded at the end.
        var hidden = items.Where(q => StateOf(s, q.Id) == ItemState.Hidden).ToList();
        if (hidden.Count > 0)
        {
            var list = new StackPanel();
            foreach (var q in hidden)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                if (IsLead)
                {
                    var back = Link("Put back", "Takes back the approval: the question shows again as N/A waiting for approval.");
                    back.Click += (_, _) => { SetReview(_mine, s, q.Id, "", "", DateTime.UtcNow); Changed(); Build(); };
                    DockPanel.SetDock(back, Dock.Right);
                    row.Children.Add(back);
                }
                var note = s.Answers.TryGetValue(q.Id, out var a) && a.Note.Trim().Length > 0 ? " · " + a.Note.Trim() : "";
                row.Children.Add(Para(q.Text + note, Brand.K.Muted, 12, 0));
                list.Children.Add(row);
            }
            page.Children.Add(new Expander { Header = $"{hidden.Count} not applicable (agreed by the group leader)", Content = list, Margin = new Thickness(0, 18, 0, 0) });
        }
        page.Children.Add(AddQuestionBox(topic));
        return page;
    }

    /// <summary>(Re)draws one question's card: the answer, its state, details on demand, the comment and the group leader's buttons.</summary>
    private void Fill(Border card, Question q, Submission s, int number)
    {
        var st = StateOf(s, q.Id);
        var a = s.Answers.TryGetValue(q.Id, out var found) ? found : null;
        var value = Normal(a?.Value);
        var live = LiveReview(s, q.Id);
        var (stripKey, stateText, stateKey) = st switch
        {
            ItemState.Approved => (Brand.K.BadgeOk, $"Approved by {live!.By}, {live.At.ToLocalTime():dd MMM HH:mm}", Brand.K.SuccessText),
            ItemState.Returned => (Brand.K.BadgeError, $"Returned by {live!.By}: {live.Note}", Brand.K.DangerText),
            ItemState.No => (Brand.K.BadgeError, "No: put it right, then answer Yes", Brand.K.DangerText),
            ItemState.NaWaiting => (Brand.K.BadgeMuted, "N/A: hidden once the group leader approves it", Brand.K.WarningText),
            ItemState.Answered => (Brand.K.BadgeWarn, "Waiting for the group leader", Brand.K.WarningText),
            _ => (Brand.K.BorderSubtle, "", Brand.K.Muted),
        };
        if (st is ItemState.Answered or ItemState.NaWaiting && s.Reviews.TryGetValue(q.Id, out var old) && old.Verdict.Length > 0 && live is null)
            stateText = "Changed after the group leader's review: waiting for a new look";

        var body = new StackPanel();
        // Top row: number, question, Yes / No / N/A.
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var num = Brand.Text(number.ToString(CultureInfo.InvariantCulture), Brand.K.Muted, 12, bold: true);
        num.Margin = new Thickness(0, 2, 10, 0);
        num.MinWidth = 22;
        top.Children.Add(num);
        var texts = new StackPanel();
        var text = Brand.Text(q.Text, size: 13.5, bold: true);
        text.TextWrapping = TextWrapping.Wrap;
        texts.Children.Add(text);
        var meta = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var team in q.TeamList) meta.Children.Add(Chip("Agree with " + team, Brand.K.AccentSoft, Brand.K.Link));
        if (q.Added) meta.Children.Add(Chip("Added on the project", Brand.K.BadgeMuted));
        if (stateText.Length > 0)
        {
            var stLine = Brand.Text(stateText, stateKey, 11.5, bold: true);
            stLine.TextWrapping = TextWrapping.Wrap;
            stLine.VerticalAlignment = VerticalAlignment.Center;
            meta.Children.Add(stLine);
        }
        if (meta.Children.Count > 0) texts.Children.Add(meta);
        Grid.SetColumn(texts, 1);
        top.Children.Add(texts);
        var seg = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
        foreach (var (label, v, style) in new[] { ("Yes", Yes, "SpmSegYes"), ("No", No, "SpmSegNo"), ("N/A", NotApplicable, "SpmSegNa") })
        {
            var r = new RadioButton { Content = label, GroupName = "q:" + q.Id, IsChecked = value == v, Margin = new Thickness(4, 0, 0, 0), MinWidth = 52 };
            if (TryFindResource(style) is Style sty) r.Style = sty;
            AutomationProperties.SetName(r, label);
            AutomationProperties.SetHelpText(r, q.Text);
            r.Click += (_, _) => Answer(q, v);
            seg.Children.Add(r);
        }
        Grid.SetColumn(seg, 2);
        top.Children.Add(seg);
        body.Children.Add(top);

        // Links: details, comment, clear.
        var links = new WrapPanel { Margin = new Thickness(32, 6, 0, 0) };
        var detailsOpen = _openDetails.Contains(q.Id);
        var details = Link(detailsOpen ? "Hide details" : "Details", "What to look at, and a sketch where it helps.");
        details.Click += (_, _) => { if (!_openDetails.Remove(q.Id)) _openDetails.Add(q.Id); Fill(card, q, s, number); };
        links.Children.Add(details);
        var noteText = a?.Note ?? "";
        var commentOpen = noteText.Trim().Length > 0 || value is No or NotApplicable || st == ItemState.Returned || _openComments.Contains(q.Id);
        if (!commentOpen)
        {
            var comment = Link("Comment");
            comment.Click += (_, _) => { _openComments.Add(q.Id); Fill(card, q, s, number); };
            links.Children.Add(comment);
        }
        if (value.Length > 0)
        {
            var clear = Link("Clear answer");
            clear.Click += (_, _) => Answer(q, "");
            links.Children.Add(clear);
        }
        if (a is not null && value.Length > 0)
            links.Children.Add(Brand.Text($"{a.By}, {a.At.ToLocalTime():dd MMM HH:mm}", Brand.K.Muted, 11));
        body.Children.Add(links);

        if (detailsOpen)
        {
            var d = new StackPanel { Margin = new Thickness(32, 6, 0, 0) };
            d.Children.Add(Para(q.Details.Length > 0 ? q.Details : "No details for this question.", Brand.K.Text, 12.5, 0));
            if (SubmissionChecklistDiagrams.For(q, ProjectTypes) is { } diagram)
            {
                var view = SubmissionChecklistDiagramView.View(diagram, 420);
                view.Margin = new Thickness(0, 8, 0, 0);
                d.Children.Add(view);
                d.Children.Add(Para(diagram.Title + ": typical, not to scale.", Brand.K.Muted, 11, 2));
            }
            var manage = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (q.Added && (IsLead || _shared.Added.FirstOrDefault(x => x.Id == q.Id)?.By is { } by && Same(by, _user)))
            {
                var del = Link("Delete this question", "Takes it off every submission of the project.");
                del.Click += (_, _) => DeleteQuestion(q);
                manage.Children.Add(del);
            }
            else if (!q.Added && IsLead)
            {
                var skip = Link("Don't ask this on this project", "Leaves it out of every submission of the project. Project setup can't put it back yet, so ask Sketch ProMax to.");
                skip.Click += (_, _) => SkipQuestion(q);
                manage.Children.Add(skip);
            }
            if (manage.Children.Count > 0) d.Children.Add(manage);
            body.Children.Add(d);
        }

        if (commentOpen)
        {
            var box = new TextBox { Text = noteText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 30, Margin = new Thickness(32, 6, 0, 0) };
            AutomationProperties.SetName(box, "Comment on " + q.Text);
            var hint = value == NotApplicable ? "Why it doesn't apply (the group leader reads this)" : value == No ? "What is missing, and who does it" : "Comment";
            box.ToolTip = hint;
            box.LostFocus += (_, _) =>
            {
                if (box.Text == noteText) return;
                SetNote(_mine, s, q.Id, box.Text, DateTime.UtcNow);
                noteText = box.Text;
                Changed();
            };
            body.Children.Add(Placeholder(box, hint));
        }

        // The group leader's buttons.
        if (IsLead && value.Length > 0)
        {
            var lead = new WrapPanel { Margin = new Thickness(32, 8, 0, 0) };
            if (CanApprove(st))
            {
                var approve = RichUi.Button("", value == NotApplicable ? "Approve N/A (hide it)" : "Approve", Brand.Weight.Primary);
                approve.Margin = new Thickness(0, 0, 8, 0);
                approve.Click += (_, _) => Review(q, Approved, "");
                lead.Children.Add(approve);
            }
            if (st == ItemState.Approved)
            {
                var undo = RichUi.Button(RichUi.Icon.Undo, "Take back the approval");
                undo.Margin = new Thickness(0, 0, 8, 0);
                undo.Click += (_, _) => Review(q, "", "");
                lead.Children.Add(undo);
            }
            if (CanReturn(st))
            {
                var why = new TextBox { Width = 300, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(why, "Why it is returned");
                var ret = RichUi.Button(RichUi.Icon.Back, "Return");
                AutomationProperties.SetHelpText(ret, "Sends it back to the engineer with the reason typed beside it.");
                ret.Click += (_, _) =>
                {
                    if (why.Text.Trim().Length == 0) { _status.Text = "Type why it is returned, then press Return."; why.Focus(); return; }
                    Review(q, Returned, why.Text.Trim());
                };
                lead.Children.Add(Placeholder(why, "Why return it?"));
                lead.Children.Add(ret);
            }
            if (st == ItemState.Returned)
            {
                var undo = RichUi.Button(RichUi.Icon.Undo, "Take back");
                undo.Click += (_, _) => Review(q, "", "");
                lead.Children.Add(undo);
            }
            body.Children.Add(lead);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var strip = new Rectangle { RadiusX = 2, RadiusY = 2 };
        strip.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, stripKey);
        grid.Children.Add(strip);
        var inner = new Border { Padding = new Thickness(14, 10, 14, 12), Child = body };
        Grid.SetColumn(inner, 1);
        grid.Children.Add(inner);
        card.Child = grid;
        card.CornerRadius = new CornerRadius(8);
        card.BorderThickness = new Thickness(1);
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        AutomationProperties.SetName(card, $"{number}. {q.Text}");
        AutomationProperties.SetItemStatus(card, st.ToString());
    }

    private static Grid Placeholder(TextBox box, string hint)
    {
        var text = Brand.Text(hint, Brand.K.Muted, 12);
        text.IsHitTestVisible = false;
        text.VerticalAlignment = VerticalAlignment.Top;
        text.Margin = new Thickness(box.Margin.Left + 6, box.Margin.Top + 4, 0, 0);
        var grid = new Grid { HorizontalAlignment = box.Width > 0 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch };
        grid.Children.Add(box);
        grid.Children.Add(text);
        void Show() => text.Visibility = box.Text.Length == 0 && !box.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Show();
        box.GotKeyboardFocus += (_, _) => Show();
        box.LostKeyboardFocus += (_, _) => Show();
        Show();
        return grid;
    }

    private void Answer(Question q, string value)
    {
        if (Current is not { } s) return;
        if (Normal(value) == Normal(s.Answers.TryGetValue(q.Id, out var a) ? a.Value : "") && value.Length > 0) return;
        SetAnswer(_mine, s, q.Id, value, null, DateTime.UtcNow);
        if (Normal(value) is No or NotApplicable) _openComments.Add(q.Id);
        Changed();
        Redraw(q);
        var lead = IsLead && Normal(value) == Yes ? " As group leader, approve it too when you are happy." : "";
        _status.Text = Normal(value) switch
        {
            Yes => "Yes." + lead,
            No => "No: add what is missing in the comment.",
            NotApplicable => "N/A: say why in the comment; it hides once the group leader approves it.",
            _ => "Answer cleared.",
        };
    }

    private void Review(Question q, string verdict, string note)
    {
        if (Current is not { } s || !IsLead) return;
        SetReview(_mine, s, q.Id, verdict, note, DateTime.UtcNow);
        Changed();
        // An approved N/A leaves the page: rebuild it so it moves into the agreed list.
        if (StateOf(s, q.Id) == ItemState.Hidden || verdict.Length == 0 && Normal(s.Answers.TryGetValue(q.Id, out var a) ? a.Value : "") == NotApplicable) { Build(); return; }
        Redraw(q);
    }

    private void Redraw(Question q)
    {
        if (Current is { } s && _cards.TryGetValue(q.Id, out var card) && card.Tag is int number)
        {
            Fill(card, q, s, number);
            RichUi.Pop(card, _motion, 0.98);
        }
        RefreshScores();
    }

    private FrameworkElement AddQuestionBox(Topic topic)
    {
        var panel = new StackPanel();
        var text = new TextBox { Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(text, "The question");
        var details = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(details, "Details (optional)");
        var teams = new TextBox { Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(teams, "Agree with (teams, optional)");
        ComboBox? type = null;
        panel.Children.Add(Placeholder(text, "Short question, e.g. Levels agreed with the port authority?"));
        panel.Children.Add(Placeholder(details, "Details shown on demand (optional)"));
        panel.Children.Add(Placeholder(teams, "Agree with, e.g. Grading, Marine (optional)"));
        if (topic.Key == TopicTypes)
        {
            type = new ComboBox { Margin = new Thickness(0, 6, 0, 0), Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var t in ProjectTypes) type.Items.Add(new ComboBoxItem { Content = TypeOf(t)!.Name, Tag = t });
            type.SelectedIndex = 0;
            AutomationProperties.SetName(type, "For which structure");
            panel.Children.Add(type);
        }
        var add = RichUi.Button(RichUi.Icon.Add, "Add the question", Brand.Weight.Primary);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 0);
        add.Click += (_, _) =>
        {
            var q = text.Text.Trim();
            if (q.Length == 0) { _status.Text = "Type the question first."; text.Focus(); return; }
            if (!q.EndsWith("?", StringComparison.Ordinal)) q += "?";
            var now = DateTime.UtcNow;
            _mine.Added.Add(new AddedQuestion
            {
                Id = NewQuestionId(_user, now), Topic = topic.Key, Text = q, Details = details.Text.Trim(), Teams = teams.Text.Trim(),
                Types = type?.SelectedItem is ComboBoxItem { Tag: string tk } ? tk : "", By = _user, At = now,
            });
            _mine.Saved = now;
            Remerge();
            Changed();
            _status.Text = "Question added for every submission of the project.";
            Build();
        };
        panel.Children.Add(add);
        return new Expander { Header = "Add a question to this page", Content = panel, Margin = new Thickness(0, 18, 0, 0) };
    }

    private void DeleteQuestion(Question q)
    {
        if (MessageBox.Show(this, $"Delete \"{q.Text}\" from every submission of the project?", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var now = DateTime.UtcNow;
        var old = _shared.Added.First(x => x.Id == q.Id);
        _mine.Added.RemoveAll(x => x.Id == q.Id);
        _mine.Added.Add(new AddedQuestion { Id = old.Id, Topic = old.Topic, Text = old.Text, Deleted = true, By = _user, At = now });
        _mine.Saved = now;
        Remerge();
        Changed();
        Build();
    }

    private void SkipQuestion(Question q)
    {
        if (MessageBox.Show(this, $"Stop asking \"{q.Text}\" on this project (every submission)?", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var s = _shared.Setup;
        _mine.Setup = new Setup { Types = s.Types.ToList(), Leads = s.Leads.ToList(), Skipped = s.Skipped.Concat([q.Id]).Distinct().ToList(), By = _user, At = DateTime.UtcNow };
        _mine.Saved = DateTime.UtcNow;
        Remerge();
        Changed();
        Build();
    }

    // ---- BIM checklist ----------------------------------------------------------------------------------------------

    /// <summary>Takes this PC's Model checklist score for the model into the submission (when it has one).</summary>
    private void UpdateBim()
    {
        if (Current is not { } s) return;
        if (ChecklistWindow.BimScore(_model, _project) is { } bim && bim != s.Bim) { SetBim(_mine, s, bim, DateTime.UtcNow); Changed(); }
    }

    private FrameworkElement BimPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("BIM checklist"));
        page.Children.Add(Para("The BIM part of the submission is the Model checklist: its score shows here and on the submission in Submissions status.", Brand.K.Muted));
        var s = Current;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(new PercentRing(s?.Bim, 110, 11, 24));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        texts.Children.Add(Brand.Text(s?.Bim is { } b ? $"{b}% of the Model checklist is Yes or N/A" : "The Model checklist isn't filled in for this model yet", size: 15, bold: true));
        texts.Children.Add(Para(s?.BimAt is { } at ? $"As saved {at.ToLocalTime():ddd dd MMM HH:mm}. It updates each time this window opens on the PC where the Model checklist is filled in." : "Fill it in with the Model checklist; the score comes here by itself.", Brand.K.Muted, 12));
        var open = RichUi.Button(RichUi.Icon.Open, "Open the Model checklist", Brand.Weight.Primary);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 10, 0, 0);
        open.Click += async (_, _) =>
        {
            var said = await OnModel(ui => ChecklistWindow.Command(ui.Application) == Result.Succeeded ? "" : "The Model checklist didn't open.");
            if (said.Length > 0) _status.Text = said;
        };
        texts.Children.Add(open);
        var again = Link("Take the score again", "Reads the Model checklist's score on this PC now.");
        again.Margin = new Thickness(0, 8, 0, 0);
        again.HorizontalAlignment = HorizontalAlignment.Left;
        again.Click += (_, _) => { UpdateBim(); Build(); };
        texts.Children.Add(again);
        row.Children.Add(texts);
        page.Children.Add(row);
        return page;
    }

    // ---- Review and export --------------------------------------------------------------------------------------------

    private FrameworkElement ReviewPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("Review and export"));
        if (Current is not { } s) { page.Children.Add(Para("Start a submission on Project setup first.", Brand.K.Muted)); return page; }
        var p = Count(_questions, s);
        var rings = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (label, value) in new[] { ("Checked", (double?)p.CheckedPercent), ("Approved", p.ApprovedPercent), ("BIM checklist", s.Bim) })
        {
            var st = new StackPanel { Margin = new Thickness(0, 0, 26, 0) };
            st.Children.Add(new PercentRing(value, 96, 10, 21) { HorizontalAlignment = HorizontalAlignment.Center });
            var l = Brand.Text(label, size: 12.5, bold: true);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.Margin = new Thickness(0, 4, 0, 0);
            st.Children.Add(l);
            rings.Children.Add(st);
        }
        var counts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        counts.Children.Add(Brand.Text(StatusText(p), p.Ready ? Brand.K.SuccessText : p.No + p.Returned > 0 ? Brand.K.DangerText : Brand.K.Text, 16, bold: true));
        counts.Children.Add(Para($"{p.Total} questions: {p.Approved} approved, {p.WaitingForLead} waiting for the group leader, {p.No} No, {p.Returned} returned, {p.Open} not answered." +
                                 (p.Hidden > 0 ? $" {p.Hidden} not applicable (agreed, hidden)." : ""), Brand.K.Muted, 12));
        rings.Children.Add(counts);
        page.Children.Add(rings);

        // What is left, each line goes to its question.
        void List(string title, ItemState[] states, string key, int max = 40)
        {
            var items = _questions.Where(q => states.Contains(StateOf(s, q.Id))).ToList();
            if (items.Count == 0) return;
            var box = new StackPanel();
            box.Children.Add(Brand.Text($"{title} ({items.Count})", key, 13.5, bold: true));
            foreach (var q in items.Take(max))
            {
                var go = Link($"{_questions.IndexOf(q) + 1}. {q.Text}");
                go.HorizontalAlignment = HorizontalAlignment.Left;
                go.Click += (_, _) => Show(q);
                box.Children.Add(go);
            }
            if (items.Count > max) box.Children.Add(Para($"and {items.Count - max} more.", Brand.K.Muted, 12));
            page.Children.Add(Box(box));
        }
        List("Returned by the group leader", [ItemState.Returned], Brand.K.DangerText);
        List("Answered No", [ItemState.No], Brand.K.DangerText);
        List("Not answered yet", [ItemState.Open], Brand.K.Text, 15);
        List("Waiting for the group leader", [ItemState.Answered, ItemState.NaWaiting], Brand.K.WarningText, 15);
        if (IsLead && p.WaitingForLead > 0)
        {
            var all = RichUi.Button("", $"Approve all {p.WaitingForLead} waiting");
            all.HorizontalAlignment = HorizontalAlignment.Left;
            all.Margin = new Thickness(0, 10, 0, 0);
            AutomationProperties.SetHelpText(all, "Approves every Yes and N/A waiting for you on this submission (N/A ones are then hidden).");
            all.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Approve all {p.WaitingForLead} answers waiting for you on {s.Title}? Approved N/A questions are hidden.", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                var now = DateTime.UtcNow;
                foreach (var q in _questions.Where(q => CanApprove(StateOf(s, q.Id)))) SetReview(_mine, s, q.Id, Approved, "", now);
                Changed();
                Build();
            };
            page.Children.Add(all);
        }

        // Export.
        page.Children.Add(Para("Export", size: 14, top: 22));
        ((TextBlock)page.Children[page.Children.Count - 1]).FontWeight = FontWeights.SemiBold;
        page.Children.Add(Para("A cover with the status and the three scores, a summary by page, the structures with their sketches, every question with its answer, comment and the group leader's review, the agreed N/A, and the sign-off.", Brand.K.Muted, 12));
        var word = RichUi.Button(RichUi.Icon.Word, "Word file", Brand.Weight.Primary);
        var pdf = RichUi.Button(RichUi.Icon.Pdf, "PDF");
        var folder = RichUi.Button(RichUi.Icon.Folder, "Open the folder");
        AutomationProperties.SetHelpText(pdf, "Makes the Word file and turns it into a PDF through Microsoft Word on this PC.");
        word.Click += async (_, _) => await Export(word, pdf: false);
        pdf.Click += async (_, _) => await Export(pdf, pdf: true);
        folder.Click += (_, _) => Shell(ExportFolder(), null);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new[] { word, pdf, folder }) { b.Margin = new Thickness(0, 0, 8, 8); row.Children.Add(b); }
        page.Children.Add(row);
        return page;
    }

    private void Show(Question q)
    {
        if (!_pageOf.TryGetValue(q.Id, out var index)) return;
        GoTo(index);
        if (_cards.TryGetValue(q.Id, out var card))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { card.BringIntoView(); RichUi.Pop(card, _motion, 0.96); }));
    }

    private string ExportFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Submission checklists", ChecklistMath.SafeName(Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(_model))));

    private async Task Export(Button button, bool pdf)
    {
        if (Current is not { } s) return;
        Brand.SetBusy(button, true, pdf ? "Making the PDF…" : "Making the Word file…");
        try
        {
            var images = new Dictionary<string, ChecklistDocx.Image>(StringComparer.Ordinal);
            foreach (var t in ProjectTypes)
                if (SubmissionChecklistDiagrams.Find(t) is { } d && SubmissionChecklistDiagramView.Png(d) is { } png) images[t] = new ChecklistDocx.Image(png, "png");
            var p = Count(_questions, s);
            var name = $"{ChecklistMath.SafeName(s.Title)} submission checklist {DateTime.Now:yyyy-MM-dd HHmm} - {(p.Ready ? "APPROVED" : "NOT READY")}";
            var docx = Path.Combine(ExportFolder(), name + ".docx");
            var info = new SubmissionChecklistDocx.Info(ChecklistMath.WorkingName(_model), Update.Updater.CurrentText, DateTime.Now, images);
            var shared = _shared;
            var questions = _questions;
            await Task.Run(() => SubmissionChecklistDocx.Write(docx, shared, s, questions, info));
            var file = docx;
            if (pdf)
            {
                file = Path.ChangeExtension(docx, ".pdf");
                try { await ChecklistPdf.Convert(docx, file); }
                catch (ChecklistPdf.NoWordException ex)
                {
                    _status.Text = ex.Message + " The Word file is made: open it and save it as PDF from any program that opens Word files.";
                    file = docx;
                }
            }
            _lastFile = file;
            if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || !pdf) _status.Text = $"Made {Path.GetFileName(file)} in Documents\\Sketch ProMax\\Submission checklists.";
            Shell(file, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _status.Text = "The file couldn't be made: " + ex.Message + (ex is IOException ? " Close it in Word if it is open, then try again." : "");
        }
        finally { Brand.SetBusy(button, false); }
    }

    private void Shell(string file, string? verb)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = verb ?? "" }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            if (!Directory.Exists(file) && verb is null) Directory.CreateDirectory(ExportFolder());
            _status.Text = "Couldn't open it: " + ex.Message;
        }
    }

    // ---- Scores -------------------------------------------------------------------------------------------------------

    private void RefreshScores()
    {
        var s = Current;
        var p = s is null ? null : Count(_questions, s);
        Color Of(int? v) { var (r, g, b) = BerthStatusMath.RingColor(v); return v is null ? Colors.White : Color.FromRgb(r, g, b); }
        _checkedRing.Show(p?.CheckedPercent, Of(p?.CheckedPercent), "Questions answered Yes or N/A, out of all asked. Click for what is left.");
        _approvedRing.Show(p?.ApprovedPercent, Of(p?.ApprovedPercent), "Questions the group leader approved. Click for what is waiting.");
        _bimRing.Show(s?.Bim, Of(s?.Bim), "The Model checklist's score (the BIM part). Click for more.");
        if (s is null) return;
        foreach (var (sub, circle, glyph, items) in _heads)
        {
            if (items.Count == 0) continue;
            var tp = Count(items, s);
            sub.Text = $"{tp.Checked} of {tp.Total} checked" + (tp.WaitingForLead > 0 ? $" · {tp.WaitingForLead} waiting" : "") + (tp.Hidden > 0 ? $" · {tp.Hidden} N/A" : "");
            var bad = tp.No + tp.Returned > 0;
            glyph.Text = tp.Ready ? "✓" : bad ? "!" : glyph.Text is "✓" or "!" ? (_heads.FindIndex(h => h.Sub == sub) + 1).ToString(CultureInfo.InvariantCulture) : glyph.Text;
            circle.SetResourceReference(Border.BorderBrushProperty, tp.Ready ? Brand.K.BadgeOk : bad ? Brand.K.BadgeError : tp.Checked == tp.Total ? Brand.K.BadgeWarn : Brand.K.Border);
            circle.SetResourceReference(Border.BackgroundProperty, tp.Ready ? Brand.K.BadgeOk : Brand.K.Bg);
            glyph.SetResourceReference(TextBlock.ForegroundProperty, tp.Ready ? Brand.K.OnAccent : bad ? Brand.K.DangerText : Brand.K.Text);
        }
    }

    // ---- Saving and reading the model ------------------------------------------------------------------------------

    /// <summary>Something changed: keep it on the PC now, and in the model a moment later.</summary>
    private void Changed()
    {
        _dirty = true;
        SaveOwn(ChecklistMath.WorkingName(_model), _mine);
        SaveSummaries(_shared.Submissions.Select(x => SummaryOf(_model, x, _questions, DateTime.Now)));
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    private async Task SaveNow()
    {
        if (!_dirty || _writing) return;
        _writing = true;
        try
        {
            var record = ReadRecord(ToJson(_mine))!;
            var why = await OnModel(ui => SubmissionChecklistStore.Write(ui.Document, record) ?? "");
            if (why.Length == 0)
            {
                _dirty = false;
                if (!_closed) _status.Text = "Saved in the model. Synchronize with Central to share it.";
            }
            else if (!_closed) _status.Text = "Kept on this PC; not in the model yet: " + why.TrimEnd('.') + ". It is saved into the model the next time you change something here.";
        }
        finally { _writing = false; }
    }

    /// <summary>Reads everyone's records from the model again and shows what changed (after Reload Latest or a sync).</summary>
    private async Task ReadAgain(bool quiet)
    {
        if (_closed || _writing || quiet && DateTime.Now - _readAt < TimeSpan.FromSeconds(5)) return;
        _readAt = DateTime.Now;
        List<UserRecord>? records = null;
        var why = await OnModel(ui => { records = SubmissionChecklistStore.ReadAll(ui.Document); return ""; }, quiet);
        if (records is null) { if (!quiet && why.Length > 0) _status.Text = why; return; }
        var others = records.Where(r => !Same(r.User, _user)).ToList();
        var stamp = Stamp(others);
        if (stamp == _othersStamp)
        {
            if (!quiet) _status.Text = "Up to date with the model. Reload Latest in Revit to bring the others' latest work.";
            return;
        }
        _others = others;
        _othersStamp = stamp;
        Remerge();
        SaveSummaries(_shared.Submissions.Select(x => SummaryOf(_model, x, _questions, DateTime.Now)));
        if (_shared.Find(_selected) is null) _selected = _shared.Submissions.FirstOrDefault()?.Key ?? "";
        Build();
        _status.Text = "Showing the latest answers and approvals from the model.";
    }

    private async Task<string> OnModel(Func<UIDocument, string> work, bool quiet = false)
    {
        var key = _docKey;
        try
        {
            return await App.Dispatcher.InvokeAsync(app =>
            {
                var ui = app.ActiveUIDocument;
                if (ui is null || ui.Document.IsFamilyDocument || DocKey(ui.Document) != key)
                    return quiet ? "" : $"Switch Revit to {ChecklistMath.WorkingName(_model)} (this checklist's model), then try again";
                return work(ui);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"Revit couldn't do it now ({ex.Message}). Finish what Revit is doing (an edit mode or a dialog)";
        }
    }
}
