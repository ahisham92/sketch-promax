using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using SketchProMax.Settings;
using SketchProMax.Tools;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using Image = System.Windows.Controls.Image;
using RadioButton = System.Windows.Controls.RadioButton;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace SketchProMax.UI;

/// <summary>
/// The pre-submission checklist (next to Chat): the office's checklist (built in, imported from Excel
/// or edited in ChecklistEditor; ChecklistStore picks which, ChecklistMath reads it) walked
/// through in stages, one per section, between Project details and Review and sign. Each check is a card with what to
/// do, how to check it, where it came from, a hint read from the model (ChecklistFacts), Yes / No / N/A and a comment.
/// Review and sign lists what is still open, takes the signatures and writes the Word file (ChecklistDocx) or a PDF of it (ChecklistPdf), ready to open
/// or print. Answers are kept per model between openings. The window doesn't block Revit (go and look, then come back);
/// it reads the model only, never changes it.
/// Keys: Y, N, A answer the check in focus and move on; Ctrl+Tab / Ctrl+Shift+Tab change stage; Esc closes.
/// </summary>
internal sealed class ChecklistWindow : Window
{
    private static ChecklistWindow? _open;

    private readonly string _docKey;
    private readonly string _draftPath;
    private ChecklistMath.Definition? _def;
    private string _loadError = "";
    private ChecklistFacts.Result _facts;
    private ChecklistMath.State _state = new();
    private readonly AgentSettings _settings = AgentSettings.Load();

    private readonly TabControl _stages = new();
    private readonly TextBlock _status = Brand.Text("");
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _back = RichUi.Button(RichUi.Icon.Back, "Back");
    private readonly Button _next = RichUi.Button(RichUi.Icon.Next, "Next", Brand.Weight.Primary);
    private readonly Button _pdf = RichUi.Button(RichUi.Icon.Pdf, "Create PDF");
    private readonly Chat.Motion _motion;
    // The run strip under the header: not checked yet, reading (with its percentage), checked, or out of date.
    private readonly Border _runStrip = new() { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 10, 18, 10) };
    private readonly TextBlock _runText = Brand.Text("", size: 13.5, bold: true);
    private readonly TextBlock _runSub = Brand.Text("", Brand.K.Muted, size: 12);
    private readonly ProgressBar _runBar = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 7, 0, 0) };
    private readonly FrameworkElement _runSpin = Brand.Spinner();
    private readonly Button _runNow = RichUi.Button(RichUi.Icon.Refresh, "Run checks", Brand.Weight.Primary);
    private readonly Button _runStop = RichUi.Button(RichUi.Icon.Close, "Stop");
    private bool _checked, _stale, _stopReading;
    private string _staleWhy = "";
    // The project the model belongs to (its project checklist, if it has one), and whether the editing password was given.
    private readonly string _project;
    private bool _unlocked;
    private DateTime _checkedAt;
    private readonly ScoreRing _answersRing, _autoRing;
    private readonly DispatcherTimer _saveSoon = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private readonly Dictionary<string, Card> _cards = new(StringComparer.Ordinal);
    private readonly List<StageHeader> _headers = [];
    private readonly Dictionary<string, TextBox> _fieldBoxes = new(StringComparer.Ordinal);
    private readonly List<(ChecklistMath.Signature Sig, ComboBox Mode, Image Preview, TextBlock Hint)> _signs = [];
    private Review? _review;
    private bool _loading;

    // The details panel on the right: the check picked in the list, what the model found, how to fix it. Kept between builds.
    private readonly Border _detail = new() { Width = 390, BorderThickness = new Thickness(1, 0, 0, 0) };
    private readonly ScrollViewer _detailScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(18, 14, 18, 18) };
    private string _selectedId = "";
    private RadioButton? _paneYes, _paneNo, _paneNa;
    private TextBox? _paneNote;
    private FrameworkElement? _headerPart, _footPart;
    private ColumnDefinition? _stagesColumn, _detailColumn;

    // Side by side: the window becomes a narrow panel at the right of the screen showing only the details, so the view
    // Show in Revit opens isn't hidden behind it. _fullBounds is where it was, to go back.
    private bool _side;
    private Rect _fullBounds;

    private sealed class Card
    {
        public ChecklistMath.Item Item = null!;
        public int Stage;
        public Border Root = null!;
        public Rectangle Strip = null!;
        public RadioButton Yes = null!, No = null!, Na = null!;
        public TextBox Note = null!;
        public TextBlock Meta = null!, NoteHint = null!;
        /// <summary>The card's own content, and the one line shown instead once it is removed as N/A.</summary>
        public FrameworkElement Body = null!, RemovedRow = null!;
    }

    /// <summary>The checklist without the checks answered N/A (removed for this model): what is scored and written.</summary>
    private ChecklistMath.Definition Kept => ChecklistMath.WithoutRemoved(_def!, _state);

    private sealed class StageHeader
    {
        public Border Circle = null!;
        public TextBlock Glyph = null!, Sub = null!;
        public List<ChecklistMath.Item> Items = [];
        public string Name = "";
        public bool WasDone = true; // no pop for stages already done when the window opens
    }

    private sealed class Review
    {
        public TextBlock Yes = null!, No = null!, Na = null!, Open = null!;
        public TextBlock Score = null!, ScoreSub = null!, AutoScore = null!, AutoSub = null!;
        public StackPanel Disagree = null!, Failing = null!, Manual = null!;
        public Border Banner = null!;
        public TextBlock BannerText = null!;
        public StackPanel Problems = null!;
        public StackPanel Done = null!;
        public TextBlock DoneText = null!, Error = null!;
        public Button OpenFile = null!, Print = null!, Folder = null!;
        public string LastFile = "";
    }

    // ---- Opening --------------------------------------------------------------------------------------------------

    /// <summary>The ribbon command: shows the window for the active model (or brings the open one to the front).</summary>
    public static Autodesk.Revit.UI.Result Command(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            TaskDialog.Show("Sketch ProMax", "Open the project model to check first: the pre-submission checklist is filled in per model.");
            return Autodesk.Revit.UI.Result.Cancelled;
        }
        if (_open is { IsLoaded: true })
        {
            if (_open._docKey == DocKey(doc)) { _open.Activate(); return Autodesk.Revit.UI.Result.Succeeded; }
            _open.Close();
        }
        var settings = AgentSettings.Load();
        var (def, error) = ChecklistStore.Load(settings, ChecklistProjects.ProjectKey(DocKey(doc)));
        // Only the header fields here: the model's hints are read after the window is up, in short steps (ReadModelAgain),
        // so a big model never holds Revit (and this button) for the whole reading.
        ChecklistFacts.Result facts;
        try { facts = ChecklistFacts.Plan(doc, def).Result; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            facts = new ChecklistFacts.Result { Model = doc.Title, Title = doc.Title };
            error = error.Length > 0 ? error : "Couldn't read the model for the hints (" + ex.Message + "). The checklist still works without them.";
        }
        var window = new ChecklistWindow(DocKey(doc), def, error, facts);
        _open = window;
        window.Closed += (_, _) => { if (_open == window) _open = null; };
        window.Show();
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    /// <summary>The window as the button opens it on <paramref name="doc"/>, not shown (Test before sharing builds it), and its draft file.</summary>
    internal static (Window Window, string Draft) ForTest(Autodesk.Revit.DB.Document doc, ChecklistMath.Definition def)
    {
        var window = new ChecklistWindow(DocKey(doc), def, "", ChecklistFacts.Plan(doc, def).Result);
        return (window, window._draftPath);
    }

    private static string DocKey(Autodesk.Revit.DB.Document doc)
    {
        try { return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName; }
        catch (Exception) { return doc.Title; }
    }

    private ChecklistWindow(string docKey, ChecklistMath.Definition? def, string error, ChecklistFacts.Result facts)
    {
        _docKey = docKey;
        _project = ChecklistProjects.ProjectKey(docKey);
        _def = def;
        _loadError = error;
        _facts = facts;
        _draftPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "checklists",
            ChecklistMath.SafeName(Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(facts.Model))) + ".json");

        Title = "Sketch ProMax pre-submission checklist";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1320, Math.Floor(area.Width * 0.92));
        Height = Math.Min(780, Math.Floor(area.Height * 0.9));
        MinWidth = Math.Min(900, Width);
        MinHeight = Math.Min(480, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        Brand.Prepare(this);
        Styles();
        _motion = RichUi.MotionOf(_settings);
        RichUi.Apply(this, _motion);
        _answersRing = new ScoreRing("Your answers", _motion);
        _autoRing = new ScoreRing("Automatic check", _motion);
        _answersRing.Clicked = () => GoToScores(auto: false);
        _autoRing.Clicked = () => GoToScores(auto: true);
        _detail.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        _detail.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        _detail.Child = _detailScroll;
        AutomationProperties.SetName(_detail, "Details of the check");

        _saveSoon.Tick += (_, _) => { _saveSoon.Stop(); SaveDraft(); };
        RunStrip();
        _back.Click += (_, _) => Move(-1);
        _next.Click += async (_, _) => { if (_stages.SelectedIndex == _stages.Items.Count - 1) await CreateFile(pdf: false); else Move(1); };
        _pdf.Click += async (_, _) => await CreateFile(pdf: true);
        AutomationProperties.SetHelpText(_pdf, "Makes the same checklist as a PDF, through Microsoft Word on this PC.");
        _stages.SelectionChanged += (_, e) => { if (e.Source == _stages) StageChanged(); };
        PreviewKeyDown += OnKey;
        Closed += (_, _) => { _closed = true; _saveSoon.Stop(); SaveDraft(); };

        Build();
        Loaded += (_, _) => FocusStage();
    }

    // ---- Layout ---------------------------------------------------------------------------------------------------

    /// <summary>Builds (or rebuilds, after a checklist is picked) the whole window from the definition and the draft.</summary>
    private void Build()
    {
        _loading = true;
        _cards.Clear();
        _headers.Clear();
        _fieldBoxes.Clear();
        _signs.Clear();
        _review = null;
        _stages.Items.Clear();
        // The stage list, the progress and the footer buttons are kept between builds: take them out of the old layout first.
        foreach (var reused in new FrameworkElement[] { _stages, _status, _bar, _back, _pdf, _next, _detail, _runStrip })
            if (reused.Parent is System.Windows.Controls.Panel old) old.Children.Remove(reused);
        Content = null;
        LoadDraft();

        var subtitle = _def is null ? "Pick your office's checklist to start" : $"{_def.Title} · {Shown(_facts.Model)}";
        // The header band, with the two scores always in view on the right (they follow every answer).
        var band = Brand.Header("Pre-submission checklist", subtitle);
        band.Padding = new Thickness(16, 12, _def is null ? 16 : 470, 12);
        var header = new Grid();
        header.Children.Add(band);
        if (_def is not null)
        {
            foreach (var ring in new[] { _answersRing, _autoRing })
                if (ring.Parent is System.Windows.Controls.Panel old) old.Children.Remove(ring);
            var rings = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
            // Run the checks and the checklist's settings, always in view (they were only on Project details).
            var run = RichUi.Button(RichUi.Icon.Refresh, "Run checks");
            run.VerticalAlignment = VerticalAlignment.Center;
            run.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetHelpText(run, "Tests the model with the quick automatic checks (seconds). Nothing is tested until you press it. The slow ones are in Settings.");
            run.Click += async (_, _) => await ReadModelAgain(run);
            var gear = RichUi.Button(RichUi.Icon.Settings, "Settings");
            gear.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetHelpText(gear, "Run all checks, the helper for hand checks, edit the checklist and its checking rules, signatures.");
            gear.Click += (_, _) => OpenSettings();
            rings.Children.Add(run);
            rings.Children.Add(gear);
            rings.Children.Add(_answersRing);
            rings.Children.Add(_autoRing);
            header.Children.Add(rings);
        }
        DockPanel.SetDock(header, Dock.Top);

        // The footer: progress on the left, Back / Next (or Create PDF and Create Word file) on the right. Always on screen.
        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _status.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetName(_bar, "Checks answered");
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Children = { _status, _bar } };
        // Report a problem: one zip with a picture of Revit and what failed (TritoRevit: in Documents\TritoRevit - <pack>\Bugs).
        var problem = RichUi.Button(RichUi.Icon.Bug, "Report a problem");
        AutomationProperties.SetHelpText(problem, "Saves one small zip with a picture of Revit, the versions and what failed, to send to whoever looks after this checklist.");
        problem.Click += async (_, _) => await ReportProblem(problem);
        var buttons = Brand.ButtonRow(problem, close, _back, _pdf, _next);
        buttons.Margin = new Thickness(0);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        var footGrid = new Grid();
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footGrid.Children.Add(left);
        Grid.SetColumn(buttons, 1);
        footGrid.Children.Add(buttons);
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 12), Child = footGrid };
        foot.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        DockPanel.SetDock(foot, Dock.Bottom);

        AutomationProperties.SetName(_stages, "Checklist stages");
        var names = _def is null ? ["Project details"] : ChecklistMath.Stages(_def);
        for (var i = 0; i < names.Count; i++)
        {
            FrameworkElement page;
            List<ChecklistMath.Item> items = [];
            if (i == 0) page = DetailsPage();
            else if (_def is not null && i == names.Count - 1) page = ReviewPage();
            else
            {
                var section = _def!.Sections[i - 1];
                items = section.Items.ToList();
                page = SectionPage(i, section);
            }
            var tab = new TabItem { Content = page, Header = StageHeaderFor(i, names[i], items) };
            AutomationProperties.SetName(tab, $"Stage {i + 1}: {names[i]}");
            _stages.Items.Add(tab);
        }

        // The stages (list on the left, the page in the middle) and the details panel on the right.
        var body = new Grid();
        _stagesColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        _detailColumn = new ColumnDefinition { Width = GridLength.Auto };
        body.ColumnDefinitions.Add(_stagesColumn);
        body.ColumnDefinitions.Add(_detailColumn);
        body.Children.Add(_stages);
        Grid.SetColumn(_detail, 1);
        body.Children.Add(_detail);

        var root = new DockPanel();
        root.Children.Add(header);
        if (_def is not null)
        {
            DockPanel.SetDock(_runStrip, Dock.Top);
            root.Children.Add(_runStrip);
            if (!_reading) ShowRun();
        }
        root.Children.Add(foot);
        root.Children.Add(body);
        Content = root;
        _headerPart = header;
        _footPart = foot;

        _loading = false;
        _stages.SelectedIndex = _def is null ? 0 : Math.Max(0, Math.Min(_state.Stage, _stages.Items.Count - 1));
        Refresh();
        StageChanged();
        if (_selectedId.Length > 0 && _cards.TryGetValue(_selectedId, out var picked)) ShowDetail(picked);
        else ShowDetail(null);
        Layout();
    }

    private static string Shown(string model) => string.IsNullOrWhiteSpace(model) ? "model not saved" : model;

    /// <summary>A stage in the list on the left: a numbered circle (a tick when done, ! when a check is No), the name and its count.</summary>
    private FrameworkElement StageHeaderFor(int index, string name, List<ChecklistMath.Item> items)
    {
        var h = new StageHeader { Items = items, Name = name };
        h.Glyph = Brand.Text((index + 1).ToString(CultureInfo.InvariantCulture), size: 12, bold: true);
        h.Glyph.HorizontalAlignment = HorizontalAlignment.Center;
        h.Glyph.VerticalAlignment = VerticalAlignment.Center;
        h.Circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1.5), Child = h.Glyph, VerticalAlignment = VerticalAlignment.Center };
        var title = Brand.Text(name, size: 13);
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxWidth = 190;
        h.Sub = Brand.Text("", Brand.K.Muted, size: 11);
        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { title, h.Sub } };
        _headers.Add(h);
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { h.Circle, texts } };
    }

    private static TextBlock Heading(string text, double size = 17)
    {
        var heading = Brand.Text(text, size: size, bold: true);
        heading.TextWrapping = TextWrapping.Wrap;
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
        return heading;
    }

    private static TextBlock Para(string text, string key = Brand.K.Text, double size = 12, double top = 4)
    {
        var t = Brand.Text(text, key, size);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, top, 0, 0);
        return t;
    }

    private static Border CardBox(UIElement child, string bg = Brand.K.Raised, string border = Brand.K.BorderSubtle, double top = 12)
    {
        var b = new Border { Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, top, 0, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        b.SetResourceReference(Border.BorderBrushProperty, border);
        return b;
    }

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        Padding = new Thickness(22, 16, 22, 20),
    };

    // ---- Stage 1: project details ---------------------------------------------------------------------------------

    private FrameworkElement DetailsPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("Project details"));
        page.Children.Add(Para("Fill in once per model and submission. What Sketch ProMax could read from the model is filled in already; check it.", Brand.K.Muted));

        // The checklist file: pick it (first time), or which one is in use, with Change.
        var source = new StackPanel();
        if (_def is null)
        {
            source.Children.Add(Brand.Text("Pick your office's checklist", size: 14, bold: true));
            source.Children.Add(Para("The checklist that comes with Sketch ProMax couldn't be read. Choose the pre-submission checklist Excel file (.xlsx): " +
                                     "Sketch ProMax takes its stages, checks, examples and sign-off into Revit and keeps its own copy on this PC."));
            if (_loadError.Length > 0) source.Children.Add(Para(_loadError, Brand.K.DangerText));
            var pick = RichUi.Button(RichUi.Icon.Import, "Choose the checklist…", Brand.Weight.Primary);
            pick.HorizontalAlignment = HorizontalAlignment.Left;
            pick.Margin = new Thickness(0, 10, 0, 0);
            pick.Click += (_, _) => PickChecklist();
            source.Children.Add(pick);
            page.Children.Add(CardBox(source, Brand.K.AccentSoft, Brand.K.Border, 16));
            return Scroll(page);
        }
        var checks = _def.Items.Count();
        source.Children.Add(Brand.Text(_def.Title, size: 14, bold: true));
        source.Children.Add(Para($"{checks} checks in {_def.Sections.Count} stages · signed by {string.Join(" and ", _def.Signatories)}", Brand.K.Muted));
        var origin = _def.SourcePath switch
        {
            ChecklistMath.BuiltInSource => "Built into Sketch ProMax. Edit the checklist to add, change or delete checks, here or in Excel.",
            ChecklistMath.EditedSource => "Edited in Sketch ProMax" + (_def.SourceStamp.Length > 0 ? " on " + _def.SourceStamp : "") + ", kept on this PC.",
            _ when ChecklistProjects.NameOf(_def.SourcePath) is { } own => $"Project {_project}'s own checklist, {own}, kept on this PC (Settings, Checklist for this project).",
            _ => "Imported from " + _def.SourcePath + " (a newer revision saved over it is picked up).",
        };
        var file = Para(origin, Brand.K.Muted, 11);
        file.TextTrimming = TextTrimming.CharacterEllipsis;
        file.TextWrapping = TextWrapping.NoWrap;
        file.ToolTip = _def.SourcePath;
        source.Children.Add(file);
        if (_loadError.Length > 0) source.Children.Add(Para(_loadError, Brand.K.WarningText));
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var change = RichUi.Button(RichUi.Icon.Edit, "Edit the checklist…");
        change.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetHelpText(change, "Add, change, move or delete checks and stages, or export the checklist to Excel and import it back.");
        change.Click += (_, _) => EditChecklist();
        var reread = RichUi.Button(RichUi.Icon.Refresh, "Run checks");
        reread.Margin = new Thickness(0);
        AutomationProperties.SetHelpText(reread, "Tests the model with the quick automatic checks, and reads the file names for the header.");
        reread.Click += async (_, _) => await ReadModelAgain(reread);
        var rules = RichUi.Button(RichUi.Icon.Edit, "Checking rules…");
        rules.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetHelpText(rules, "What the automatic check compares the model with: name patterns, approved scales and worksets, the classification and IFC parameters.");
        rules.Click += (_, _) => EditRules();
        reread.Margin = new Thickness(0, 0, 8, 0);
        var slowRun = RichUi.Button(RichUi.Icon.Refresh, "Run the slow checks");
        slowRun.Margin = new Thickness(0);
        AutomationProperties.SetHelpText(slowRun, "Runs the slower automatic checks: duplicates, floating elements, clashes inside your trade, the model check score and the standard template. It can take a few minutes on a big model.");
        slowRun.Click += async (_, _) => await ReadModelAgain(slowRun, slow: true);
        row.Children.Add(change);
        row.Children.Add(rules);
        row.Children.Add(reread);
        row.Children.Add(slowRun);
        source.Children.Add(row);
        page.Children.Add(CardBox(source, top: 16));

        // How it's checked, in plain words: what the model test does, what's yours, and when it runs.
        var autoCount = _def.Items.Count(Automatic);
        var pending = _def.Items.Count(i => i.Auto.Length > 0 && Pending(i.Auto));
        var how = new StackPanel();
        how.Children.Add(Brand.Text("How this checklist is checked", size: 14, bold: true));
        if (!_checked)
        {
            how.Children.Add(Para($"Sketch ProMax can test {autoCount} of the {checks} checks on the model for you: they're marked Automatic. " +
                                  $"The other {checks - autoCount} are marked You check this: you answer them yourself, or ask the helper.", Brand.K.Text));
            how.Children.Add(Para("Nothing is tested until you press Run checks at the top." +
                                  (pending > 0 ? $" {pending} of the automatic checks take longer (such as duplicates, clashes and the model score), so they only run when you press Run the slow checks." : ""), Brand.K.Text));
            how.Children.Add(Para("Some automatic checks compare the model with your office's files or names, such as the MIDP or the clash report. Set those in Checking rules; until one is set, you answer that check yourself.", Brand.K.Muted));
        }
        else
        {
            var off = _def.Items.Count(i => i.Auto.Length > 0 && !Automatic(i));
            how.Children.Add(Para($"The model was checked at {_checkedAt:HH:mm}. {autoCount} of the {checks} checks were tested automatically (marked Automatic). " +
                                  $"You answer the other {checks - autoCount} yourself (marked You check this), or ask the helper.", Brand.K.Text));
            if (off > 0) how.Children.Add(Para($"{off} of those could be tested automatically too: set their file or name in Checking rules, then press Run checks.", Brand.K.Text));
            if (pending > 0) how.Children.Add(Para($"{pending} slow check{(pending == 1 ? " hasn't" : "s haven't")} run yet. Press Run the slow checks when you have a few minutes.", Brand.K.Text));
        }
        page.Children.Add(CardBox(how, Brand.K.AccentSoft, Brand.K.Border));

        // The header fields, two columns: label above its box, the example as the box's hint.
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var r = 0;
        for (var i = 0; i < _def.Fields.Count; i++)
        {
            var f = _def.Fields[i];
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cell = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var label = Brand.Text(f.Label, bold: true);
            cell.Children.Add(label);
            var box = new TextBox { MinHeight = 28, Margin = new Thickness(0, 4, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
            box.Text = _state.Fields.TryGetValue(f.Label, out var v) ? v : "";
            AutomationProperties.SetName(box, f.Label);
            var from = Brand.Text("", Brand.K.Muted, size: 11);
            var auto = FieldFromModel(f);
            void ShowFrom() => from.Text = auto.Length > 0 && box.Text.Trim() == auto ? "From the model" : f.Example.Length > 0 ? "e.g. " + f.Example : "";
            box.TextChanged += (_, _) =>
            {
                if (_loading) return;
                _state.Fields[f.Label] = box.Text;
                ShowFrom();
                Changed();
            };
            ShowFrom();
            from.TextTrimming = TextTrimming.CharacterEllipsis;
            cell.Children.Add(Placeholder(box, f.Example.Length > 0 ? "e.g. " + f.Example : ""));
            cell.Children.Add(from);
            _fieldBoxes[f.Label] = box;
            Grid.SetRow(cell, r);
            Grid.SetColumn(cell, i % 2 == 0 ? 0 : 2);
            grid.Children.Add(cell);
            if (i % 2 == 1) r++;
        }
        page.Children.Add(CardBox(grid));
        page.Children.Add(Para("Keys: Y yes, N no, A not applicable on the check in focus (it moves on to the next); Ctrl+Tab next stage. " +
                               "Your answers are kept for this model until you start again, and Revit stays usable while this window is open.", Brand.K.Muted, 11, 14));
        return Scroll(page);
    }

    /// <summary>The value a header field gets from the model or Settings, or "".</summary>
    private string FieldFromModel(ChecklistMath.Field f)
    {
        // A checklist saved before a field had a source (an older edit or import) still gets it from its label.
        var source = f.Source.Length > 0 ? f.Source : ChecklistMath.FieldSource(f.Label);
        if (source.Length == 0) return "";
        if (_facts.Fields.TryGetValue(source, out var v) && v.Length > 0) return v;
        var role = (_settings.SignatureRole ?? "").Trim();
        if (source is "coordinator" or "leader" && role.Length > 0 && Matches(role, f.Label)) return (_settings.SignatureName ?? "").Trim();
        return "";
    }

    /// <summary>"BIM coordinator" matches the label "Trade BIM coordinator" (either contains the other, case ignored).</summary>
    private static bool Matches(string role, string label) =>
        role.Length > 0 && (label.IndexOf(role, StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>Only when the built-in checklist can't be read: imports the office's Excel file.</summary>
    private void PickChecklist()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pick the pre-submission checklist", Filter = "Excel workbooks|*.xlsx|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _saveSoon.Stop();
        SaveDraft();
        try
        {
            var def = ChecklistMath.ReadXlsx(dialog.FileName);
            _def = def;
            _loadError = "";
            try
            {
                ChecklistStore.Save(def);
                _settings.ChecklistPath = dialog.FileName;
                _settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _loadError = $"The checklist is in use, but Sketch ProMax couldn't keep it for next time ({ex.Message}).";
            }
        }
        catch (InvalidDataException ex) { _loadError = $"Couldn't read the checklist: {ex.Message}"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadError = $"Couldn't open the checklist ({ex.Message}). If it is open in Excel, that is fine; check the file is on a drive you can reach, then try again.";
        }
        Build();
        OutOfDate("You picked another checklist.");
    }

    /// <summary>The checklist's settings (the gear in the header); a changed helper redraws the Ask buttons.</summary>
    private void OpenSettings()
    {
        if (_def is null) return;
        var automatic = _def.Items.Count(Automatic);
        var pending = _def.Items.Count(i => i.Auto.Length > 0 && Pending(i.Auto));
        var actions = new ChecklistSettingsWindow.Actions(
            () => _ = ReadModelAgain(null),
            () => _ = ReadModelAgain(null, slow: true),
            EditChecklist,
            EditRules,
            UseForProject,
            NewChecklist,
            ImportForProject,
            ExportChecklist,
            DeleteProjectChecklist,
            () => { ChecklistProjectWindows.ChangePassword(this, _unlocked); },
            MakeDefault,
            TestBeforeSharing);
        var project = new ChecklistSettingsWindow.Project(_project, ChecklistProjects.NameOf(_def.SourcePath) ?? "", ChecklistProjects.Names());
        if (!ChecklistSettingsWindow.Edit(this, actions, automatic, pending, project)) return;
        var stage = _stages.SelectedIndex;
        Build();
        _stages.SelectedIndex = stage;
    }

    /// <summary>Opens the checking rules; after Save the model is read again with them.</summary>
    private void EditRules()
    {
        if (_def is null) return; // rules are per person (their files and folders): no password, even on the Default
        _saveSoon.Stop();
        SaveDraft();
        if (ChecklistRulesWindow.Edit(this, _def) is not { } def) return;
        _def = def;
        _loadError = "";
        OutOfDate("The checking rules changed.");
    }

    /// <summary>Whether the model test settles this check (it gives a pass or a fail); else the user checks it.</summary>
    // Before the first Run checks, every check with a model test counts (its rule may still turn it off).
    private bool Automatic(ChecklistMath.Item item) => item.Auto.Length > 0 && (!_checked || Verdict(item.Auto) is not null || Pending(item.Auto));

    /// <summary>A slow automatic check not run yet (Run the slow checks runs it).</summary>
    private bool Pending(string key) => _facts.Hints.TryGetValue(key, out var h) && h.Pending;

    /// <summary>Whether a checking rule could make this check automatic (it has one, turned off or not readable now).</summary>
    private static bool RuleCould(ChecklistMath.Item item) => item.Auto.Length > 0 && ChecklistMath.RuleDefs.Any(r => r.Check == item.Auto);

    /// <summary>Opens the checklist editor; after Save the stages are rebuilt, and answers stay with their checks.</summary>
    private void EditChecklist()
    {
        if (_def is null || !OwnChecklist && !Unlock("change the Default checklist")) return;
        _saveSoon.Stop();
        SaveDraft();
        if (ChecklistEditor.Edit(this, _def, _settings) is not { } def) return;
        _def = def;
        _loadError = "";
        Build();
        OutOfDate("The checklist changed.");
    }

    // ---- Project checklists ----------------------------------------------------------------------------------------

    /// <summary>Whether the checklist shown is the user's own (a project checklist), free to change without the password.</summary>
    private bool OwnChecklist => ChecklistProjects.NameOf(_def?.SourcePath) is not null;

    /// <summary>Asks the editing password once per window; true when it was given.</summary>
    private bool Unlock(string what) => _unlocked || (_unlocked = ChecklistProjectWindows.Unlock(this, what));

    /// <summary>Shows another checklist in this window (answers stay with their checks by wording); the results are out of date.</summary>
    private void Switch(ChecklistMath.Definition def, string why)
    {
        _saveSoon.Stop();
        SaveDraft();
        _def = def;
        _loadError = "";
        Build();
        OutOfDate(why);
        _status.Text = why;
    }

    private void Problem(string text) => MessageBox.Show(this, text, "Sketch ProMax", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Sets the checklist this project uses ("" for the office one).</summary>
    private void UseForProject(string name)
    {
        try { ChecklistProjects.Assign(_project, name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem($"Couldn't save the choice ({ex.Message})."); return; }
        var (def, note) = ChecklistStore.Load(AgentSettings.Load(), _project);
        if (def is null) { Problem(note.Length > 0 ? note : "That checklist can't be read."); return; }
        Switch(def, name.Length > 0 ? $"Project {_project} now uses the checklist {name}." : $"Project {_project} now uses the Default checklist.");
    }

    /// <summary>A new checklist for this project: a copy of the Default, opened in the editor to change as needed.</summary>
    private void NewChecklist()
    {
        var (from, note) = ChecklistStore.Load(AgentSettings.Load());
        if (from is null) { Problem(note.Length > 0 ? note : "The Default checklist can't be read."); return; }
        var name = ChecklistProjectWindows.Ask(this, "New checklist", "Name of the new checklist (for example Ports). It starts as a copy of the Default checklist; change it as you need next.",
            _project.Length > 0 ? _project + " checklist" : "");
        if (name is null) return;
        name = ChecklistProjects.CleanName(name);
        if (name.Length == 0) { Problem("The checklist needs a name. Nothing was made."); return; }
        if (ChecklistProjects.Exists(name) && MessageBox.Show(this, $"There is already a checklist called {name} on this PC. Replace it with a copy of the Default?", "Sketch ProMax",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var def = ChecklistProjects.Copy(from, name);
        try
        {
            ChecklistProjects.Save(name, def);
            ChecklistProjects.Assign(_project, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem($"Couldn't save the new checklist ({ex.Message})."); return; }
        ActivityLog.Note("checklist_project", $"new {name} from the Default: {def.Items.Count()} checks, used for {_project}");
        Switch(def, $"Made the checklist {name} as a copy of the Default; project {_project} uses it now.");
        EditChecklist();
    }

    /// <summary>Saves the checklist shown as the next Default for everyone, for the next update to carry (password asked).</summary>
    private void MakeDefault()
    {
        if (_def is null || !Unlock("make the Default checklist for everyone")) return;
        if (MessageBox.Show(this, $"Make \"{_def.Title}\" ({_def.Items.Count()} checks) the Default checklist for everyone? It goes into the next Sketch ProMax update once you ask for it to be published. " +
                                  "Check it holds no client or project names: the Default is public.", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            var path = ChecklistProjects.SaveNextDefault(_def);
            _status.Text = "Saved as the next Default. Ask in the Sketch ProMax project to publish your Default: it goes into the next update.";
            ActivityLog.Note("checklist_default", $"next Default saved: {_def.Items.Count()} checks, {path}");
            MessageBox.Show(this, "Saved as the next Default checklist:\n" + path + "\n\nNow ask in the Sketch ProMax project for your Default to be published. Every PC gets it with the next update.",
                "Sketch ProMax", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem($"Couldn't save it ({ex.Message})."); }
    }

    /// <summary>
    /// Test before sharing, for whoever looks after the Default (password): the checklist and Share tools tested in depth
    /// on the field test's throwaway project; the report goes to Documents\Sketch ProMax\Test reports to upload.
    /// </summary>
    private void TestBeforeSharing()
    {
        if (!Unlock("run the test before sharing")) return;
        if (MessageBox.Show(this, "Test the checklist and Share tools before sharing them?\n\nSketch ProMax builds a throwaway project, opens it in its own window, " +
                                  "tests the checklist (reading the model, the windows, the Default, project checklists, Excel, Word and PDF, the password) and Share tools " +
                                  "(the zip, installing on a colleague's PC, updates, problem reports), then closes it without saving. Your models, checklists and shared tools aren't changed. " +
                                  "Save your open models first: Revit switches windows while it runs. It takes a few minutes.\n\n" +
                                  "The report is saved in Documents\\Sketch ProMax\\Test reports. Upload it in the Sketch ProMax project to share the results.",
                "Sketch ProMax", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) return;
        if (FieldTest.StartBeforeSharing() is { } why) { Problem(why); return; }
        ActivityLog.Note("test_before_sharing", "started");
        // The test opens and closes its own project: this window would only show a model that isn't active any more.
        Close();
    }

    /// <summary>A checklist exported to Excel (from another project, say) becomes a project checklist used here.</summary>
    private void ImportForProject()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import a checklist for this project", Filter = "Excel workbooks|*.xlsx|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        ChecklistMath.Definition def;
        try { def = ChecklistMath.ReadXlsx(dialog.FileName); }
        catch (InvalidDataException ex) { Problem($"Couldn't read the checklist: {ex.Message}"); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem($"Couldn't open the file ({ex.Message}). If Excel has it locked, save and close it, then try again.");
            return;
        }
        var name = ChecklistProjectWindows.Ask(this, "Name the checklist", "What should this checklist be called on this PC?", ChecklistProjects.CleanName(Path.GetFileNameWithoutExtension(dialog.FileName)));
        if (name is null) return;
        name = ChecklistProjects.CleanName(name);
        if (name.Length == 0) { Problem("The checklist needs a name. Nothing was imported."); return; }
        if (ChecklistProjects.Exists(name) && MessageBox.Show(this, $"There is already a checklist called {name} on this PC. Replace it?", "Sketch ProMax",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            ChecklistProjects.Save(name, def);
            ChecklistProjects.Assign(_project, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem($"Couldn't keep the checklist ({ex.Message})."); return; }
        ActivityLog.Note("checklist_project", $"imported {name}: {def.Items.Count()} checks, used for {_project}");
        Switch(def, $"Imported the checklist {name} ({def.Items.Count()} checks); project {_project} uses it now.");
    }

    /// <summary>Saves the checklist shown as Excel, in the office's layout, to import in another project or edit in Excel.</summary>
    private void ExportChecklist()
    {
        if (_def is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the checklist to Excel", Filter = "Excel workbook|*.xlsx",
            FileName = ChecklistMath.SafeName(ChecklistProjects.NameOf(_def.SourcePath) ?? _def.Title) + ".xlsx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ChecklistXlsx.Write(dialog.FileName, _def);
            _status.Text = "Exported the checklist to " + dialog.FileName + ".";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Problem($"Couldn't write the Excel file ({ex.Message}). If it is open in Excel, close it and try again.");
        }
    }

    /// <summary>Deletes the project checklist in use; the projects that used it go back to the office checklist.</summary>
    private void DeleteProjectChecklist()
    {
        if (_def is null || ChecklistProjects.NameOf(_def.SourcePath) is not { } name) return;
        if (MessageBox.Show(this, $"Delete the checklist {name} from this PC? Every project that uses it goes back to the Default checklist. Answers you gave are kept.",
                "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { ChecklistProjects.Delete(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem($"Couldn't delete it ({ex.Message})."); return; }
        var (def, note) = ChecklistStore.Load(AgentSettings.Load(), _project);
        if (def is null) { Problem(note); return; }
        Switch(def, $"Deleted the checklist {name}; project {_project} uses the Default checklist again.");
    }

    /// <summary>Reads the hints and file names from the model again, through Revit (the window itself runs outside a Revit call).</summary>
    private async Task ReadModelAgain(Button? button, bool slow = false)
    {
        // One reading at a time: asked again meanwhile (a fix, new rules), it reads once more when this one ends.
        if (_reading) { _readAgain = true; _readAgainSlow |= slow; return; }
        if (button is not null) { if (Brand.IsBusy(button)) return; Brand.SetBusy(button, true, slow ? "Running the slow checks…" : "Reading…"); }
        _reading = true;
        try
        {
            var def = _def;
            var key = _docKey;
            var label = slow ? "Running the slow checks…" : "Reading the model…";
            _status.Text = label;
            _stopReading = false;
            ShowRun(label, 0, "Getting ready.");
            var reading = await App.Dispatcher.InvokeAsync(app =>
            {
                foreach (Autodesk.Revit.DB.Document d in app.Application.Documents)
                    if (!d.IsFamilyDocument && !d.IsLinked && DocKey(d) == key) return ChecklistFacts.Plan(d, def, slow ? app : null);
                return null;
            });
            if (reading is null) { _status.Text = "The model this checklist is for isn't open any more, so it can't be read again."; return; }
            // One short Revit call at a time (about a fifth of a second each): between them Revit repaints and takes clicks,
            // where one call for the whole reading froze it on a big model.
            var total = Math.Max(1, reading.Left);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!_closed && !_stopReading && await App.Dispatcher.InvokeAsync(_ => reading.RunFor(StepBudget)))
            {
                var done = total - reading.Left;
                _status.Text = label + " " + (100 * done / total) + "%";
                var now = CheckName(reading.NextName);
                ShowRun(label, 100 * done / total, (now.Length > 0 ? now + " · " : "") + "You can keep working in Revit meanwhile; Stop ends it.");
            }
            if (_closed) return;
            if (_stopReading) { _status.Text = "Stopped. The results are as they were before you pressed Run."; return; }
            var facts = reading.Result;
            // How long the slowest checks took, so a slow model can be looked into from the activity log.
            ActivityLog.Note("checklist_timing", $"{(slow ? "slow" : "quick")} run {clock.Elapsed.TotalSeconds:0.0} s; slowest: " +
                string.Join(", ", reading.Slowest.Take(5).Select(t => $"{t.Key} {t.Value.TotalSeconds:0.0} s")));
            // A quick read keeps the slow checks' last results (they say when they ran).
            foreach (var old in _facts.Hints)
                if (facts.Hints.TryGetValue(old.Key, out var now) && now.Pending && !old.Value.Pending) facts.Hints[old.Key] = old.Value;
            _facts = facts;
            _checked = true;
            _stale = false;
            _checkedAt = DateTime.Now;
            SaveDraft();
            var stage = _stages.SelectedIndex;
            Build();
            _stages.SelectedIndex = stage;
            _status.Text = (slow ? "Ran the slow checks. " : "Checked the model. ") + _status.Text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = $"Couldn't read the model ({ex.Message}). Finish what Revit is doing (an edit mode or a dialog), then try again.";
        }
        finally
        {
            _reading = false;
            _stopReading = false;
            if (button is not null) Brand.SetBusy(button, false);
            ShowRun();
        }
        if (_readAgain && !_closed)
        {
            var again = _readAgainSlow;
            _readAgain = _readAgainSlow = false;
            await ReadModelAgain(null, again);
        }
    }

    /// <summary>The short title of the check a model test key belongs to, for the progress line.</summary>
    private string CheckName(string key) =>
        key.Length == 0 ? "" : _def?.Items.FirstOrDefault(i => i.Auto == key) is { } item ? $"#{item.Number} {item.Title}" : "";

    /// <summary>Lays out the run strip once; <see cref="ShowRun"/> fills it.</summary>
    private void RunStrip()
    {
        _runSpin.Margin = new Thickness(0, 0, 10, 0);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _runText, _runSub, _runBar } };
        _runSub.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(_runText, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_runBar, "Checking the model");
        _runNow.Margin = new Thickness(12, 0, 0, 0);
        _runStop.Margin = new Thickness(12, 0, 0, 0);
        AutomationProperties.SetHelpText(_runNow, "Tests the model with the quick automatic checks. It takes a few seconds, and Revit stays usable.");
        AutomationProperties.SetHelpText(_runStop, "Stops checking. The results stay as they were.");
        _runNow.Click += async (_, _) => await ReadModelAgain(null);
        _runStop.Click += (_, _) => { _stopReading = true; Brand.SetBusy(_runStop, true, "Stopping…"); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _runNow, _runStop } };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_runSpin);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        _runStrip.Child = grid;
        _runStrip.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
    }

    /// <summary>Shows the run strip: reading (with <paramref name="label"/> and its percentage), or else where the results stand.</summary>
    private void ShowRun(string? label = null, int percent = 0, string detail = "")
    {
        var reading = label is not null;
        _runSpin.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
        _runBar.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
        _runStop.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
        _runNow.Visibility = reading ? Visibility.Collapsed : Visibility.Visible;
        if (!reading && Brand.IsBusy(_runStop)) Brand.SetBusy(_runStop, false);
        string background;
        if (reading)
        {
            background = Brand.K.AccentSoft;
            _runText.Text = $"{label} {percent}%";
            _runSub.Text = detail;
            RichUi.To(_runBar, percent, _motion);
        }
        else if (!_checked)
        {
            background = Brand.K.NoticeBg;
            _runText.Text = "The model hasn't been checked yet";
            _runSub.Text = "Press Run checks to test it. The quick checks take a few seconds, and you can keep working in Revit meanwhile.";
            RichUi.Set(_runNow, RichUi.Icon.Refresh, "Run checks");
        }
        else if (_stale)
        {
            background = Brand.K.NoticeBg;
            _runText.Text = "The results may be out of date";
            _runSub.Text = _staleWhy + " Press Run checks to update them.";
            RichUi.Set(_runNow, RichUi.Icon.Refresh, "Run checks");
        }
        else
        {
            background = Brand.K.Raised;
            var tested = _def?.Items.Where(i => i.Auto.Length > 0).Select(i => Verdict(i.Auto)).Where(v => v is not null).ToList() ?? [];
            var pass = tested.Count(v => v == true);
            var pending = _def?.Items.Count(i => i.Auto.Length > 0 && Pending(i.Auto)) ?? 0;
            _runText.Text = $"✓ Checked at {_checkedAt:HH:mm}";
            _runSub.Text = $"{pass} automatic check{(pass == 1 ? "" : "s")} pass, {tested.Count - pass} need fixing." +
                           (pending > 0 ? $" {pending} slow check{(pending == 1 ? "" : "s")} not run yet (Settings, Run the slow checks)." : "");
            RichUi.Set(_runNow, RichUi.Icon.Refresh, "Run again");
        }
        _runStrip.SetResourceReference(Border.BackgroundProperty, background);
    }

    /// <summary>Something changed what the results would say: they're kept, marked out of date until Run checks.</summary>
    private void OutOfDate(string why)
    {
        if (_checked) { _stale = true; _staleWhy = why; }
        if (_def is not null && !_reading) ShowRun();
    }

    private static readonly TimeSpan StepBudget = TimeSpan.FromMilliseconds(200);
    private bool _reading, _readAgain, _readAgainSlow, _closed;

    /// <summary>The model check's verdict on a check's rule: passes, fails, or null when the model can't tell.</summary>
    private bool? Verdict(string key) => _facts.Hints.TryGetValue(key, out var h) ? h.Level switch
    {
        ChecklistFacts.Level.Ok => true,
        ChecklistFacts.Level.Warn => false,
        _ => null,
    } : null;

    /// <summary>Runs something on the model this checklist is for, through Revit; a sentence for the user either way.</summary>
    private async Task ReportProblem(Button button)
    {
        if (Brand.IsBusy(button)) return;
        var shot = Bugs.BugReport.Screenshot();
        if (Bugs.BugReport.Ask() is not { } description) return;
        Brand.SetBusy(button, true, "Saving…");
        try
        {
            var file = await App.Dispatcher.InvokeAsync(app => Bugs.BugReport.Create(app, description, shot, "the checklist's Report a problem"));
            _status.Text = "Problem report saved: " + Path.GetFileName(file);
            Bugs.BugReport.Saved(file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = "The problem report couldn't be saved: " + ex.Message; }
        finally { Brand.SetBusy(button, false); }
    }

    private async Task<string> OnModel(Func<UIDocument, string> work)
    {
        var key = _docKey;
        try
        {
            return await App.Dispatcher.InvokeAsync(app =>
            {
                var ui = app.ActiveUIDocument;
                if (ui is null || ui.Document.IsFamilyDocument || DocKey(ui.Document) != key)
                    return "Switch Revit to the model this checklist is for (" + Shown(_facts.Model) + "), then try again.";
                return work(ui);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"Revit couldn't do it ({ex.Message}). Finish what Revit is doing (an edit mode or a dialog), then try again.";
        }
    }

    private async Task ShowInRevit(Button button, TextBlock said, string key)
    {
        if (Brand.IsBusy(button)) return;
        Brand.SetBusy(button, true, "Opening…");
        try
        {
            var opened = false;
            said.Text = await OnModel(ui => { var text = ChecklistViews.Show(ui, key); opened = true; return text; });
            said.Visibility = Visibility.Visible;
            RichUi.FadeIn(said, _motion, fromY: 4);
            ActivityLog.Note("checklist_show", key + ": " + said.Text);
            // The view opens in Revit's window, which this one covered: move aside so it can be seen, and give Revit the
            // focus so it switches views straight away. The panel stays on top (Revit owns it).
            if (opened)
            {
                var moved = !_side;
                SetSide(true);
                if (moved && _detailScroll.Content is StackPanel page) page.Children.Insert(Math.Min(page.Children.Count, 1), Para(said.Text, Brand.K.SuccessText, 12, 6));
                BringRevitForward();
            }
        }
        finally { Brand.SetBusy(button, false); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void BringRevitForward()
    {
        try { if (App.MainWindow != IntPtr.Zero) SetForegroundWindow(App.MainWindow); }
        catch (Exception) { /* only a nicety */ }
    }

    private async Task DeleteCheckingViews(Button button, TextBlock result)
    {
        if (Brand.IsBusy(button)) return;
        Brand.SetBusy(button, true, "Deleting…");
        try
        {
            result.Text = await OnModel(ChecklistViews.Cleanup);
            result.Visibility = Visibility.Visible;
            RichUi.FadeIn(result, _motion, fromY: 4);
            ActivityLog.Note("checklist_views_deleted", result.Text);
        }
        finally { Brand.SetBusy(button, false); }
    }

    // ---- The details panel ----------------------------------------------------------------------------------------

    /// <summary>Picks a check: its card is outlined and the details panel shows it.</summary>
    private void Select(Card card)
    {
        if (card.Item.Id == _selectedId) return;
        if (_selectedId.Length > 0 && _cards.TryGetValue(_selectedId, out var old)) Outline(old, false);
        _selectedId = card.Item.Id;
        Outline(card, true);
        ShowDetail(card);
    }

    private static void Outline(Card card, bool on)
    {
        card.Root.SetResourceReference(Border.BorderBrushProperty, on ? Brand.K.Accent : Brand.K.BorderSubtle);
        card.Root.BorderThickness = new Thickness(on ? 2 : 1);
        card.Root.Padding = on ? new Thickness(13, 11, 13, 11) : new Thickness(14, 12, 14, 12);
    }

    /// <summary>Goes to a check from anywhere (the review lists, Next / Previous in the panel): its stage, its card, its details.</summary>
    private void GoTo(Card card)
    {
        if (_stages.SelectedIndex != card.Stage) _stages.SelectedIndex = card.Stage;
        Select(card);
        if (!_side) FocusCard(card);
    }

    /// <summary>Fills the details panel for <paramref name="card"/> (null: how to use it).</summary>
    private void ShowDetail(Card? card)
    {
        _paneYes = _paneNo = _paneNa = null;
        _paneNote = null;
        var page = new StackPanel();
        if (card is not null && _cards.TryGetValue(card.Item.Id, out var live)) card = live;
        if (card is null || _def is null)
        {
            page.Children.Add(Heading("Details", 15));
            page.Children.Add(Para("Click a check to see what the model found, what's missing and how to fix it. Where Sketch ProMax can fix it for you, a Fix button shows here.", Brand.K.Muted));
            if (_side) page.Children.Add(SideButton());
            _detailScroll.Content = page;
            return;
        }
        if (card.Item.Id == _selectedId) Outline(card, true);
        var item = card.Item;
        var stageCards = _cards.Values.OrderBy(c => c.Stage).ToList();
        var at = stageCards.IndexOf(card);

        // Where it is, with Previous / Next through every check.
        var nav = new DockPanel();
        var prev = RichUi.Button(RichUi.Icon.Back, "");
        AutomationProperties.SetName(prev, "Previous check");
        prev.MinWidth = 36;
        prev.IsEnabled = at > 0;
        prev.Click += (_, _) => { if (at > 0) GoTo(stageCards[at - 1]); };
        var next = RichUi.Button(RichUi.Icon.Next, "");
        AutomationProperties.SetName(next, "Next check");
        next.MinWidth = 36;
        next.IsEnabled = at >= 0 && at + 1 < stageCards.Count;
        next.Click += (_, _) => { if (at >= 0 && at + 1 < stageCards.Count) GoTo(stageCards[at + 1]); };
        var navButtons = new StackPanel { Orientation = Orientation.Horizontal, Children = { prev, next } };
        DockPanel.SetDock(navButtons, Dock.Right);
        nav.Children.Add(navButtons);
        var where = Brand.Text($"Check #{item.Number} of {stageCards.Count} · {_def.Sections[card.Stage - 1].Title}", Brand.K.Muted, 11.5);
        where.TextWrapping = TextWrapping.Wrap;
        where.VerticalAlignment = VerticalAlignment.Center;
        nav.Children.Add(where);
        page.Children.Add(nav);

        var title = Heading(item.Title, 16);
        title.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(title);
        if (item.High || item.Waiting.Length > 0)
        {
            var badges = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (item.High) badges.Children.Add(Badge("High priority", Brand.K.BadgeError, Brand.K.OnDanger));
            if (item.Waiting.Length > 0) badges.Children.Add(Badge("Waiting on an answer", Brand.K.NoticeBg, Brand.K.WarningText));
            page.Children.Add(badges);
        }

        // What the model says: passes, needs fixing (with everything it found), or can't tell.
        _facts.Hints.TryGetValue(item.Auto, out var hint);
        if (item.Auto.Length == 0) hint = null;
        var notRun = !_checked && item.Auto.Length > 0 && hint?.Pending != true;
        if (notRun) hint = null;
        var (headKey, headText, bg, border) = notRun ? (Brand.K.Text, "Automatic check: not run yet", Brand.K.Raised, Brand.K.BorderSubtle) : hint?.Level switch
        {
            ChecklistFacts.Level.Ok => (Brand.K.SuccessText, "✓ The model check passes", Brand.K.Raised, Brand.K.SuccessText),
            ChecklistFacts.Level.Warn => (Brand.K.WarningText, "! The model check found something to fix", Brand.K.NoticeBg, Brand.K.NoticeBorder),
            ChecklistFacts.Level.Info when hint.Pending => (Brand.K.Text, "Slow automatic check: not run yet", Brand.K.Raised, Brand.K.BorderSubtle),
            ChecklistFacts.Level.Info => (Brand.K.Text, "i What the model says", Brand.K.Raised, Brand.K.BorderSubtle),
            _ => (Brand.K.Muted, RuleCould(item) ? "You check this one (set its checking rule to make it automatic)" : "You check this one: the model test can't", Brand.K.Raised, Brand.K.BorderSubtle),
        };
        var status = new StackPanel();
        status.Children.Add(Brand.Text(headText, headKey, 12.5, bold: true));
        if (hint is not null)
        {
            status.Children.Add(Para(hint.Text, Brand.K.Text, 12, 4));
            if (hint.Items.Count > 0)
            {
                status.Children.Add(Para(hint.Level == ChecklistFacts.Level.Warn ? "What's missing or wrong:" : "Found:", Brand.K.Text, 12, 8));
                foreach (var line in hint.Items.Take(40)) status.Children.Add(Para("•  " + line, Brand.K.Text, 12, 2));
                if (hint.Items.Count > 40) status.Children.Add(Para($"…and {hint.Items.Count - 40} more.", Brand.K.Muted, 11.5, 2));
            }
        }
        else status.Children.Add(Para(notRun ? "Press Run checks at the top to test it on the model, or check it yourself with How to check below." : "Use How to check below, then answer.", Brand.K.Muted, 12, 4));
        page.Children.Add(CardBox(status, bg, border, 12));

        // The actions: look at it in Revit, let Sketch ProMax fix it, read the model again after fixing it yourself.
        var result = Para("", Brand.K.Text, 12, 6);
        result.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Polite);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (item.Auto.Length > 0 && ChecklistViews.ButtonFor(item.Auto) is { } showText)
        {
            var show = RichUi.Button(RichUi.Icon.Eye, showText);
            show.Margin = new Thickness(0, 0, 8, 6);
            AutomationProperties.SetHelpText(show, "Opens it in Revit and moves this window to the side so you can see it. Views made for checking are named " + ChecklistViews.Prefix + "…");
            var key = item.Auto;
            show.Click += async (_, _) => await ShowInRevit(show, result, key);
            actions.Children.Add(show);
        }
        if (item.Auto.Length > 0 && hint?.Level != ChecklistFacts.Level.Ok && ChecklistFixes.For(item.Auto) is { } offer)
        {
            var fix = RichUi.Button(RichUi.Icon.Fix, "Fix it: " + offer.Button, Brand.Weight.Primary);
            fix.Margin = new Thickness(0, 0, 8, 6);
            AutomationProperties.SetHelpText(fix, offer.Question);
            var key = item.Auto;
            fix.Click += async (_, _) => await FixInRevit(fix, result, key, offer);
            actions.Children.Add(fix);
        }
        if (item.Auto.Length > 0)
        {
            var slow = ChecklistFacts.Slow.Contains(item.Auto);
            var again = RichUi.Button(RichUi.Icon.Refresh, slow ? "Run the slow checks" : "Check the model again", slow && hint?.Pending == true ? Brand.Weight.Primary : Brand.Weight.Secondary);
            again.Margin = new Thickness(0, 0, 8, 6);
            AutomationProperties.SetHelpText(again, slow
                ? "Runs the slower checks (model check, clashes inside your trade, the standard template). It can take a few minutes on a big model; Revit is busy meanwhile."
                : "After you fix something in Revit, reads the model again to update this check and the automatic score.");
            again.Click += async (_, _) => await ReadModelAgain(again, slow);
            actions.Children.Add(again);
        }
        var helper = ChecklistHelper.Parse(AgentSettings.Load().ChecklistHelper);
        if (ChecklistHelper.Button(helper) is { } askText)
        {
            var ask = RichUi.Button(RichUi.Icon.Ask, askText);
            ask.Margin = new Thickness(0, 0, 8, 6);
            AutomationProperties.SetHelpText(ask, helper == ChecklistHelper.ProMax
                ? "Hands this check to the Sketch ProMax chat, which looks at the model and says pass or fail. It changes nothing; you still answer."
                : "Copies the question so you can paste it into SketchPro's chat. You still answer.");
            var it = item;
            var said = hint?.Pending == true ? null : hint?.Text;
            ask.Click += async (_, _) => await AskHelper(ask, result, it, said, helper);
            actions.Children.Add(ask);
        }
        if (actions.Children.Count > 0) page.Children.Add(actions);
        page.Children.Add(result);

        // How to fix, how to check, the office's full wording.
        Section(page, "How to fix", item.Fix.Length > 0 ? item.Fix : "Put it right in the model, then answer Yes. If it can't be done before submission, answer No and say why.");
        if (item.Example.Length > 0) Section(page, "How to check", item.Example);
        if (item.Waiting.Length > 0)
            page.Children.Add(CardBox(Para($"Open point: {item.Waiting}. Follow the example until the answer comes, and write what you did in the comment.", Brand.K.Text, 12, 0), Brand.K.NoticeBg, Brand.K.NoticeBorder, 10));
        Section(page, "The checklist's wording", item.Check);
        var origin = string.Join(" · ", new[] { item.From.Length > 0 ? "From: " + item.From : "", item.FoundOn.Length > 0 ? "Found on " + item.FoundOn : "" }.Where(t => t.Length > 0));
        if (origin.Length > 0) page.Children.Add(Para(origin, Brand.K.Muted, 11, 6));

        // The answer, here too, so the panel works on its own beside Revit.
        var answerHead = Brand.Text("Your answer", Brand.K.Text, 13, bold: true);
        answerHead.Margin = new Thickness(0, 16, 0, 6);
        page.Children.Add(answerHead);
        var group = "d" + item.Id;
        _paneYes = Segment("Yes", "SpmSegYes", group, $"Yes, #{item.Number} is done");
        _paneNo = Segment("No", "SpmSegNo", group, $"No, #{item.Number} is not done");
        _paneNa = Segment("N/A", "SpmSegNa", group, $"Not applicable: #{item.Number}");
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _paneYes, _paneNo, _paneNa } });
        _paneNote = new TextBox { MinHeight = 54, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxLength = 1000, Margin = new Thickness(0, 8, 0, 0), Text = card.Note.Text };
        AutomationProperties.SetName(_paneNote, $"Comment on #{item.Number}");
        page.Children.Add(Placeholder(_paneNote, item.Waiting.Length > 0 ? "What you did while this waits (needed)" : "Comment (optional; needed for No)"));
        SyncPane(card);
        var c = card;
        foreach (var (button, value) in new[] { (_paneYes, ChecklistMath.Yes), (_paneNo, ChecklistMath.No), (_paneNa, ChecklistMath.NotApplicable) })
            button.Checked += (_, _) =>
            {
                if (_loading) return;
                Answer(c, value, move: false);
                if (value == ChecklistMath.No || c.Item.Waiting.Length > 0 && c.Note.Text.Trim().Length == 0) _paneNote?.Focus();
                else if (at >= 0 && at + 1 < stageCards.Count) GoTo(stageCards[at + 1]);
            };
        _paneNote.TextChanged += (_, _) => { if (!_loading && _paneNote is { } box && c.Note.Text != box.Text) c.Note.Text = box.Text; };

        page.Children.Add(SideButton());
        _detailScroll.Content = page;
        _detailScroll.ScrollToTop();
        RichUi.FadeIn(page, _motion, fromX: 10, fromY: 0, ms: 160);
    }

    private static void Section(StackPanel page, string heading, string text)
    {
        var h = Brand.Text(heading, Brand.K.Text, 13, bold: true);
        h.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(h);
        page.Children.Add(Para(text, Brand.K.Text, 12, 4));
    }

    /// <summary>The panel's answer follows the card's (answered either place, or by key).</summary>
    private void SyncPane(Card card)
    {
        if (card.Item.Id != _selectedId || _paneYes is null || _paneNo is null || _paneNa is null) return;
        var was = _loading;
        _loading = true;
        var done = ChecklistMath.Normal(_state.Answers.TryGetValue(card.Item.Id, out var a) ? a.Done : "");
        _paneYes.IsChecked = done == ChecklistMath.Yes;
        _paneNo.IsChecked = done == ChecklistMath.No;
        _paneNa.IsChecked = done == ChecklistMath.NotApplicable;
        if (_paneNote is not null && _paneNote.Text != card.Note.Text) _paneNote.Text = card.Note.Text;
        _loading = was;
    }

    private Button SideButton()
    {
        var side = RichUi.Button(_side ? RichUi.Icon.Expand : RichUi.Icon.Side, _side ? "Back to the full checklist" : "Put beside Revit");
        side.HorizontalAlignment = HorizontalAlignment.Left;
        side.Margin = new Thickness(0, 18, 0, 0);
        AutomationProperties.SetHelpText(side, _side ? "Shows the whole checklist again." : "Makes this window a narrow panel at the right of the screen, so you can work in Revit and go through the checks here.");
        side.Click += (_, _) => SetSide(!_side);
        return side;
    }

    /// <summary>Side by side with Revit (only the details, at the right edge of the screen), or back to the full window.</summary>
    private void SetSide(bool side)
    {
        if (side == _side) return;
        var area = SystemParameters.WorkArea;
        if (side)
        {
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            _fullBounds = new Rect(Left, Top, Width, Height);
            _side = true;
            MinWidth = 360;
            Width = Math.Min(440, area.Width / 2);
            Height = area.Height;
            Left = area.Right - Width;
            Top = area.Top;
        }
        else
        {
            _side = false;
            if (_fullBounds.Width > 0)
            {
                Left = _fullBounds.Left;
                Top = _fullBounds.Top;
                Width = _fullBounds.Width;
                Height = _fullBounds.Height;
            }
            MinWidth = Math.Min(900, Width);
        }
        Layout();
        ShowDetail(_selectedId.Length > 0 && _cards.TryGetValue(_selectedId, out var card) ? card : null);
    }

    /// <summary>Shows what the mode and the stage call for: full (stages, page, details on section stages) or side (details only).</summary>
    private void Layout()
    {
        var section = _def is not null && _stages.SelectedIndex > 0 && _stages.SelectedIndex < _stages.Items.Count - 1;
        _stages.Visibility = _side ? Visibility.Collapsed : Visibility.Visible;
        // Side by side, the details column takes the whole width; otherwise the page does and the details keep 390.
        if (_stagesColumn is not null) _stagesColumn.Width = _side ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (_detailColumn is not null) _detailColumn.Width = _side ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        _detail.Visibility = _side || section ? Visibility.Visible : Visibility.Collapsed;
        _detail.Width = _side ? double.NaN : 390;
        _detail.BorderThickness = new Thickness(_side ? 0 : 1, 0, 0, 0);
        if (_headerPart is not null) _headerPart.Visibility = _side ? Visibility.Collapsed : Visibility.Visible;
        if (_footPart is not null) _footPart.Visibility = _side ? Visibility.Collapsed : Visibility.Visible;
        Title = _side ? "Sketch ProMax checklist" : "Sketch ProMax pre-submission checklist";
    }

    /// <summary>A score ring was clicked: Review and sign, at the breakdown of that score.</summary>
    private void GoToScores(bool auto)
    {
        if (_def is null || _review is null) return;
        if (_side) SetSide(false);
        _stages.SelectedIndex = _stages.Items.Count - 1;
        var target = auto ? (FrameworkElement)_review.Failing : _review.Banner;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => target.BringIntoView()));
    }

    /// <summary>Hands a check to the helper picked in Settings: the Sketch ProMax chat, or the clipboard for SketchPro.</summary>
    private async Task AskHelper(Button button, TextBlock said, ChecklistMath.Item item, string? found, string helper)
    {
        if (Brand.IsBusy(button)) return;
        var question = ChecklistHelper.Question(item, Shown(_facts.Model), found);
        said.Visibility = Visibility.Visible;
        if (helper == ChecklistHelper.SketchPro)
        {
            try { Clipboard.SetText(question); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                said.Text = "Couldn't copy the question (another program is using the clipboard). Try again.";
                return;
            }
            // Open SketchPro's Copilot (its own ribbon button), then click into its message box and paste. Enter is yours.
            Brand.SetBusy(button, true, "Opening SketchPro…");
            try
            {
                var moved = !_side;
                SetSide(true);
                var problem = await OnModel(ui => SketchProCopilot.Open(ui.Application));
                var pasted = problem.Length == 0 && await Task.Run(() => SketchProCopilot.Paste(App.MainWindow, TimeSpan.FromSeconds(12)));
                said.Text = pasted ? "Pasted into SketchPro's Copilot. Check it there and press Send, then answer here from what it says."
                    : (problem.Length > 0 ? problem + " " : "SketchPro's Copilot opened, but its message box wasn't found. ") + "The question is copied: click in Copilot's message box and press Ctrl+V.";
                ActivityLog.Note("checklist_ask", "sketchpro #" + item.Number + (pasted ? " pasted" : " copied"));
                if (moved && _detailScroll.Content is StackPanel page) page.Children.Insert(Math.Min(page.Children.Count, 1), Para(said.Text, Brand.K.SuccessText, 12, 6));
            }
            finally { Brand.SetBusy(button, false); }
            return;
        }
        Brand.SetBusy(button, true, "Asking…");
        try
        {
            // The chat lives in Sketch ProMax's pane in Revit: show it (a Revit call), then send the question as your message.
            var shown = await OnModel(ui =>
            {
                try { ui.Application.GetDockablePane(ChatPaneId).Show(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
                return "";
            });
            var problem = shown.Length > 0 ? shown : Chat.ChatPane.Ask(question);
            said.Text = problem.Length > 0 ? problem : "Asked. Sketch ProMax's answer appears in the Chat pane: answer here from what it finds.";
            ActivityLog.Note("checklist_ask", "promax #" + item.Number);
            if (said.Text.StartsWith("Asked.", StringComparison.Ordinal))
            {
                var moved = !_side;
                SetSide(true);
                if (moved && _detailScroll.Content is StackPanel page) page.Children.Insert(Math.Min(page.Children.Count, 1), Para(said.Text, Brand.K.SuccessText, 12, 6));
            }
        }
        finally { Brand.SetBusy(button, false); }
    }

    /// <summary>The Sketch ProMax pane (the loader registers it with this id; the chat is its Chat tab).</summary>
    private static readonly DockablePaneId ChatPaneId = new(new Guid("448734fe-1b1a-47e2-94a5-1fd138cb74e7"));

    /// <summary>Runs a check's automatic fix after asking, then reads the model again so the check and the score update.</summary>
    private async Task FixInRevit(Button button, TextBlock said, string key, ChecklistFixes.Offer offer)
    {
        if (Brand.IsBusy(button)) return;
        var detached = _facts.Hints.TryGetValue("links", out var links) && links.Text.StartsWith("This copy is detached", StringComparison.Ordinal);
        var question = offer.Question + (!offer.ChangesModel ? "" :
                       "\n\nThis changes the model " + Shown(_facts.Model) + ". Ctrl+Z in Revit undoes it" + (key == "links" ? " (for links: reload them in Manage Links)" : "") + "." +
                       (detached ? "" : "\n\nThis doesn't look like a detached copy: fixes are meant for the copy you submit."));
        // A plain message box: this window is modeless, outside a Revit API call, where Revit's own dialogs aren't allowed.
        if (MessageBox.Show(this, question, "Sketch ProMax: fix it", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Brand.SetBusy(button, true, offer.ChangesModel ? "Fixing…" : "Working…");
        var def = _def;
        try
        {
            said.Text = await OnModel(ui => ChecklistFixes.Run(ui, key, def));
            said.Visibility = Visibility.Visible;
            ActivityLog.Note("checklist_fix", key + ": " + said.Text);
        }
        finally { Brand.SetBusy(button, false); }
        var message = said.Text;
        OutOfDate("A fix changed the model.");
        _status.Text = message + " Press Run checks to see the result.";
        if (_detailScroll.Content is StackPanel page) page.Children.Insert(Math.Min(page.Children.Count, 1), Para(message + " Press Run checks to see the result.", Brand.K.SuccessText, 12, 6));
    }

    // ---- Section stages -------------------------------------------------------------------------------------------

    private FrameworkElement SectionPage(int stage, ChecklistMath.Section section)
    {
        var page = new StackPanel();
        page.Children.Add(Heading(section.Title));
        var high = section.Items.Count(i => i.High);
        var waiting = section.Items.Count(i => i.Waiting.Length > 0);
        var auto = section.Items.Count(Automatic);
        page.Children.Add(Para($"{section.Items.Count} checks: {auto} {(_checked ? "tested automatically" : "automatic (press Run checks)")}, {section.Items.Count - auto} you check yourself" + (high > 0 ? $"; {high} high priority" : "") + (waiting > 0 ? $"; {waiting} waiting on an answer" : "") +
                               ". Click a check for its details. Answer each one; a No needs a comment saying why.", Brand.K.Muted));
        foreach (var item in section.Items) page.Children.Add(ItemCard(stage, item));
        return Scroll(page);
    }

    private Border ItemCard(int stage, ChecklistMath.Item item)
    {
        var card = new Card { Item = item, Stage = stage };
        var body = new StackPanel();

        // Number and the short title, then the badges: the full wording, how to check and how to fix are in the details
        // panel on the right, which shows the card clicked.
        var top = new DockPanel();
        var number = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        number.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        number.Child = Brand.Text("#" + item.Number, Brand.K.Muted, 12, bold: true);
        DockPanel.SetDock(number, Dock.Left);
        top.Children.Add(number);
        var more = Brand.Text("Details ›", Brand.K.Link, 11.5);
        more.VerticalAlignment = VerticalAlignment.Top;
        more.Margin = new Thickness(10, 2, 0, 0);
        DockPanel.SetDock(more, Dock.Right);
        top.Children.Add(more);
        var check = Brand.Text(item.Title, size: 14, bold: true);
        check.TextWrapping = TextWrapping.Wrap;
        check.ToolTip = item.Check;
        top.Children.Add(check);
        body.Children.Add(top);

        var badges = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (_facts.Hints.TryGetValue(item.Auto, out var hint) && item.Auto.Length > 0 && hint.Level != ChecklistFacts.Level.Info)
        {
            var ok = hint.Level == ChecklistFacts.Level.Ok;
            badges.Children.Add(Badge(ok ? "✓ Automatic: passes" : "! Automatic: needs fixing", ok ? Brand.K.Surface : Brand.K.NoticeBg, ok ? Brand.K.SuccessText : Brand.K.WarningText));
        }
        else if (item.Auto.Length > 0 && Pending(item.Auto)) badges.Children.Add(Badge("Automatic (slow check): not run yet", Brand.K.Surface, Brand.K.Muted));
        else badges.Children.Add(Badge(RuleCould(item) ? "You check this (a checking rule can make it automatic)" : "You check this", Brand.K.Surface, Brand.K.Muted));
        if (item.High) badges.Children.Add(Badge("High priority", Brand.K.BadgeError, Brand.K.OnDanger));
        if (item.Waiting.Length > 0) badges.Children.Add(Badge("Waiting on an answer", Brand.K.NoticeBg, Brand.K.WarningText));
        if (badges.Children.Count > 0) body.Children.Add(badges);

        // Yes / No / N/A, then the comment.
        var answer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var group = "q" + item.Id;
        card.Yes = Segment("Yes", "SpmSegYes", group, $"Yes, #{item.Number} is done");
        card.No = Segment("No", "SpmSegNo", group, $"No, #{item.Number} is not done");
        card.Na = Segment("N/A", "SpmSegNa", group, $"Not applicable: #{item.Number}");
        var segs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Children = { card.Yes, card.No, card.Na } };
        DockPanel.SetDock(segs, Dock.Left);
        answer.Children.Add(segs);
        card.Note = new TextBox { MinHeight = 30, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 1000 };
        AutomationProperties.SetName(card.Note, $"Comment on #{item.Number}");
        answer.Children.Add(Placeholder(card.Note, item.Waiting.Length > 0 ? "What you did while this waits (needed)" : "Comment (optional; needed for No)"));
        body.Children.Add(answer);
        card.NoteHint = Para("", Brand.K.DangerText, 11, 3);
        card.NoteHint.Visibility = Visibility.Collapsed;
        body.Children.Add(card.NoteHint);
        card.Meta = Para("", Brand.K.Muted, 11, 4);
        body.Children.Add(card.Meta);

        var a = _state.Answers.TryGetValue(item.Id, out var found) ? found : null;
        switch (ChecklistMath.Normal(a?.Done))
        {
            case ChecklistMath.Yes: card.Yes.IsChecked = true; break;
            case ChecklistMath.No: card.No.IsChecked = true; break;
            case ChecklistMath.NotApplicable: card.Na.IsChecked = true; break;
        }
        card.Note.Text = a?.Note ?? "";
        foreach (var (button, value) in new[] { (card.Yes, ChecklistMath.Yes), (card.No, ChecklistMath.No), (card.Na, ChecklistMath.NotApplicable) })
            button.Checked += (_, _) => { if (!_loading) Answer(card, value, move: false); };
        card.Note.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _state.For(item.Id).Note = card.Note.Text;
            ShowCard(card);
            Changed();
        };

        // Answered N/A, the check is removed from this model's checklist: one line in its place, with Put back.
        var removed = new DockPanel();
        var putBack = RichUi.Button(RichUi.Icon.Undo, "Put back");
        AutomationProperties.SetName(putBack, $"Put back #{item.Number}");
        putBack.Click += (_, e) => { e.Handled = true; PutBack(card); };
        DockPanel.SetDock(putBack, Dock.Right);
        removed.Children.Add(putBack);
        var removedText = Brand.Text($"#{item.Number} {item.Title}: removed as not applicable. It is out of the score and the Word file and PDF.", Brand.K.Muted, 12);
        removedText.TextWrapping = TextWrapping.Wrap;
        removedText.VerticalAlignment = VerticalAlignment.Center;
        removedText.Margin = new Thickness(0, 0, 10, 0);
        removed.Children.Add(removedText);
        card.Body = body;
        card.RemovedRow = removed;

        // A coloured strip on the left says the answer at a glance (green yes, red no, grey N/A).
        card.Strip = new Rectangle { Width = 4, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 12, 0) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(card.Strip);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        Grid.SetColumn(removed, 1);
        grid.Children.Add(removed);
        card.Root = CardBox(grid);
        card.Root.Tag = card;
        card.Root.Cursor = Cursors.Hand;
        card.Root.PreviewMouseLeftButtonDown += (_, _) => Select(card);
        card.Root.GotKeyboardFocus += (_, _) => Select(card);
        AutomationProperties.SetName(card.Root, $"Check #{item.Number}: {item.Check}");
        _cards[item.Id] = card;
        ShowCard(card);
        return card.Root;
    }

    private static Border Badge(string text, string bg, string fg)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 6, 4), Child = Brand.Text(text, fg, 11, bold: true) };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        return b;
    }

    private static RadioButton Segment(string text, string style, string group, string name)
    {
        var b = new RadioButton { Content = text, GroupName = group, MinWidth = 58, MinHeight = 30, Margin = new Thickness(0, 0, 6, 0) };
        b.SetResourceReference(StyleProperty, style);
        AutomationProperties.SetName(b, name);
        return b;
    }

    /// <summary>A text box with a hint shown while it is empty.</summary>
    private static Grid Placeholder(TextBox box, string hint)
    {
        var grid = new Grid();
        grid.Children.Add(box);
        if (hint.Length == 0) return grid;
        var text = Brand.Text(hint, Brand.K.Muted, 12);
        text.IsHitTestVisible = false;
        text.Margin = new Thickness(9, 0, 9, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        void Show() => text.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Show();
        Show();
        AutomationProperties.SetHelpText(box, hint);
        grid.Children.Add(text);
        return grid;
    }

    private void Answer(Card card, string value, bool move)
    {
        var a = _state.For(card.Item.Id);
        a.Check = card.Item.Check;
        a.Done = value;
        a.By = Signer();
        a.Date = Today();
        _loading = true;
        var chosen = value switch { ChecklistMath.Yes => card.Yes, ChecklistMath.No => card.No, _ => card.Na };
        chosen.IsChecked = true;
        _loading = false;
        RichUi.Pop(chosen, _motion);
        ShowCard(card);
        Changed();
        if (!move) return;
        // A No asks why: the comment box. Otherwise on to the next check (or the next stage when this was the last).
        if (value == ChecklistMath.No || card.Item.Waiting.Length > 0 && card.Note.Text.Trim().Length == 0) { card.Note.Focus(); return; }
        var stageCards = _cards.Values.Where(c => c.Stage == card.Stage).ToList();
        var at = stageCards.IndexOf(card);
        if (at >= 0 && at + 1 < stageCards.Count) FocusCard(stageCards[at + 1]);
        else _next.Focus();
    }

    /// <summary>A check removed as N/A comes back unanswered.</summary>
    private void PutBack(Card card)
    {
        var a = _state.For(card.Item.Id);
        a.Done = "";
        _loading = true;
        card.Na.IsChecked = false;
        _loading = false;
        ShowCard(card);
        Changed();
        ActivityLog.Note("checklist_put_back", "#" + card.Item.Number);
        FocusCard(card);
    }

    private string Signer()
    {
        var name = (_settings.SignatureName ?? "").Trim();
        return name.Length > 0 ? name : Environment.UserName;
    }

    private static string Today() => DateTime.Now.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);

    private void ShowCard(Card card)
    {
        var a = _state.Answers.TryGetValue(card.Item.Id, out var found) ? found : null;
        var done = ChecklistMath.Normal(a?.Done);
        card.Strip.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, done switch
        {
            ChecklistMath.Yes => Brand.K.BadgeOk,
            ChecklistMath.No => Brand.K.BadgeError,
            ChecklistMath.NotApplicable => Brand.K.BadgeMuted,
            _ => Brand.K.BorderSubtle,
        });
        card.Meta.Text = done.Length == 0 ? "Not answered yet." : $"Marked {Word(done)} by {a!.By}, {a.Date}.";
        var note = card.Note.Text.Trim();
        var need = done == ChecklistMath.No && note.Length == 0 ? "Say why it isn't done, and what happens next."
                 : card.Item.Waiting.Length > 0 && done.Length > 0 && note.Length == 0 ? "This check waits on an answer: write what you did meanwhile."
                 : "";
        card.NoteHint.Text = need;
        card.NoteHint.Visibility = need.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var removed = done == ChecklistMath.NotApplicable;
        card.Body.Visibility = removed ? Visibility.Collapsed : Visibility.Visible;
        card.RemovedRow.Visibility = removed ? Visibility.Visible : Visibility.Collapsed;
        SyncPane(card);
    }

    private static string Word(string done) => done switch { ChecklistMath.Yes => "Yes", ChecklistMath.No => "No", _ => "N/A" };

    // ---- Last stage: review and sign ------------------------------------------------------------------------------

    private FrameworkElement ReviewPage()
    {
        var r = _review = new Review();
        var page = new StackPanel();
        page.Children.Add(Heading("Review and sign"));
        page.Children.Add(Para("Check the totals, clear anything still open, then create the Word file or PDF to print and sign.", Brand.K.Muted));

        // Two scores side by side: your answers, and what the model check says on its own (you can be at 100% while it is at 50%).
        var scores = new UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        (TextBlock, TextBlock) Score(string label)
        {
            var number = Brand.Text("0%", Brand.K.Text, 30, bold: true);
            var sub = Brand.Text("", Brand.K.Muted, 12);
            sub.TextWrapping = TextWrapping.Wrap;
            var stack = new StackPanel { Children = { Brand.Text(label, Brand.K.Text, 13, bold: true), number, sub } };
            var b = CardBox(stack, top: 0);
            b.Margin = new Thickness(0, 0, 10, 0);
            AutomationProperties.SetName(b, label);
            scores.Children.Add(b);
            return (number, sub);
        }
        (r.Score, r.ScoreSub) = Score("Your answers");
        (r.AutoScore, r.AutoSub) = Score("Automatic model check");
        page.Children.Add(scores);
        r.Disagree = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(r.Disagree, "Answered Yes but failed by the model check");
        page.Children.Add(r.Disagree);
        r.Failing = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(r.Failing, "Where the automatic score loses points");
        page.Children.Add(r.Failing);
        r.Manual = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(r.Manual, "Checks you do yourself");
        page.Children.Add(r.Manual);

        // Four tiles: Yes, No, N/A, not answered.
        var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 0) };
        TextBlock Tile(string label, string key)
        {
            var number = Brand.Text("0", key, 26, bold: true);
            var stack = new StackPanel { Children = { number, Brand.Text(label, Brand.K.Muted, 12) } };
            var b = CardBox(stack, top: 0);
            b.Margin = new Thickness(0, 0, 10, 0);
            AutomationProperties.SetName(b, label);
            tiles.Children.Add(b);
            return number;
        }
        r.Yes = Tile("Yes", Brand.K.SuccessText);
        r.No = Tile("No", Brand.K.DangerText);
        r.Na = Tile("Not applicable", Brand.K.Text);
        r.Open = Tile("Not answered", Brand.K.WarningText);
        page.Children.Add(tiles);

        r.BannerText = Para("", Brand.K.Text, 13, 0);
        r.BannerText.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(r.BannerText, AutomationLiveSetting.Polite);
        r.Banner = CardBox(r.BannerText, top: 14);
        page.Children.Add(r.Banner);
        r.Problems = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(r.Problems, "Still open");
        page.Children.Add(r.Problems);

        // Sign-off: one card per signatory.
        var signOff = Heading("Sign-off", 15);
        signOff.Margin = new Thickness(0, 18, 0, 0);
        page.Children.Add(signOff);
        page.Children.Add(Para("Signatures go in only when the checklist is ready (nothing No, nothing unanswered). Until then the file says NOT READY in red, " +
                               "without signatures, so it can be printed for review but can't pass for a signed copy.", Brand.K.Muted));
        foreach (var role in _def!.Signatories) page.Children.Add(SignCard(role));

        // After the file is made (Word or PDF): what was written, Open, Print, Show in folder.
        r.Error = Para("", Brand.K.DangerText, 12, 12);
        r.Error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(r.Error, AutomationLiveSetting.Assertive);
        page.Children.Add(r.Error);
        r.DoneText = Para("", Brand.K.Text, 13, 0);
        r.OpenFile = RichUi.Button(RichUi.Icon.Open, "Open", Brand.Weight.Primary);
        r.Print = RichUi.Button(RichUi.Icon.Print, "Print");
        r.Folder = RichUi.Button(RichUi.Icon.Folder, "Show in folder");
        r.OpenFile.Margin = new Thickness(0, 0, 8, 0);
        r.OpenFile.Click += (_, _) => Shell(r.LastFile, null);
        r.Print.Click += (_, _) => Shell(r.LastFile, "print");
        r.Folder.Click += (_, _) => Shell("explorer.exe", null, $"/select,\"{r.LastFile}\"");
        AutomationProperties.SetHelpText(r.Print, "Sends the file to your default printer.");
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { r.OpenFile, r.Print, r.Folder } };
        r.Done = new StackPanel { Children = { r.DoneText, buttons } };
        var done = CardBox(r.Done, Brand.K.Raised, Brand.K.Border, 14);
        done.Visibility = Visibility.Collapsed;
        done.Name = "DonePanel";
        r.Done.Tag = done;
        page.Children.Add(done);

        // The views Show in Revit made: delete them before the model goes out.
        var viewsHead = Heading("Checking views", 15);
        viewsHead.Margin = new Thickness(0, 18, 0, 0);
        page.Children.Add(viewsHead);
        var viewsText = Para("Show in Revit makes views named \"" + ChecklistViews.Prefix + "…\" to check in. Delete them before you submit the model.", Brand.K.Muted);
        var deleteViews = RichUi.Button(RichUi.Icon.Delete, "Delete the checking views", Brand.Weight.Danger);
        deleteViews.HorizontalAlignment = HorizontalAlignment.Left;
        deleteViews.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetHelpText(deleteViews, "Deletes every view named " + ChecklistViews.Prefix + "… from this model. Your own views are not touched.");
        var viewsResult = Para("", Brand.K.Text, 12, 6);
        viewsResult.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(viewsResult, AutomationLiveSetting.Polite);
        deleteViews.Click += async (_, _) => await DeleteCheckingViews(deleteViews, viewsResult);
        page.Children.Add(CardBox(new StackPanel { Children = { viewsText, deleteViews, viewsResult } }, top: 8));

        var startAgain = RichUi.Button(RichUi.Icon.Undo, "Start again", Brand.Weight.Danger);
        startAgain.HorizontalAlignment = HorizontalAlignment.Left;
        startAgain.Margin = new Thickness(0, 24, 0, 0);
        AutomationProperties.SetHelpText(startAgain, "Clears every answer, comment and signature for this model. Word files and PDFs already made are kept.");
        startAgain.Click += (_, _) => StartAgain();
        page.Children.Add(startAgain);
        return Scroll(page);
    }

    private Border SignCard(string role)
    {
        var sig = _state.Signatures.FirstOrDefault(s => s.Role == role);
        var mine = Matches((_settings.SignatureRole ?? "").Trim(), role);
        if (sig is null)
        {
            sig = new ChecklistMath.Signature { Role = role, Date = Today() };
            var field = _def!.Fields.FirstOrDefault(f => Matches(role, f.Label) || Matches(f.Label, role));
            sig.Name = field is not null && _state.Fields.TryGetValue(field.Label, out var n) && n.Trim().Length > 0 ? n.Trim() : mine ? (_settings.SignatureName ?? "").Trim() : "";
            sig.Mode = mine && Signature.ForWord(_settings.SignatureImage) is not null ? ChecklistMath.SignMode.Mine : ChecklistMath.SignMode.Hand;
            _state.Signatures.Add(sig);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        left.Children.Add(Brand.Text(role, size: 14, bold: true));
        left.Children.Add(Para("Name", Brand.K.Text, 12, 8));
        var name = new TextBox { MinHeight = 28, Text = sig.Name, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(name, $"{role}: name");
        name.TextChanged += (_, _) => { sig.Name = name.Text; Changed(); };
        left.Children.Add(name);
        left.Children.Add(Para("Date", Brand.K.Text, 12, 8));
        var date = new TextBox { MinHeight = 28, Text = sig.Date, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
        AutomationProperties.SetName(date, $"{role}: date");
        date.TextChanged += (_, _) => { sig.Date = date.Text; Changed(); };
        left.Children.Add(date);
        grid.Children.Add(left);

        var right = new StackPanel();
        right.Children.Add(Para("Signed with", Brand.K.Text, 12, 0));
        var mode = new ComboBox { MinHeight = 28, Margin = new Thickness(0, 4, 0, 0) };
        mode.Items.Add(new ComboBoxItem { Content = "My signature (Settings > Sign-off)", Tag = ChecklistMath.SignMode.Mine });
        mode.Items.Add(new ComboBoxItem { Content = "A picture…", Tag = ChecklistMath.SignMode.Image });
        mode.Items.Add(new ComboBoxItem { Content = "By hand on the printout", Tag = ChecklistMath.SignMode.Hand });
        AutomationProperties.SetName(mode, $"{role}: how it is signed");
        right.Children.Add(mode);
        var frame = new Border { BorderThickness = new Thickness(1), Height = 76, Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(4), Padding = new Thickness(6) };
        frame.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        frame.Background = Brand.Brush(Colors.White); // signatures are dark ink on white paper, in both themes
        var preview = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(preview, $"{role}: signature");
        frame.Child = preview;
        right.Children.Add(frame);
        var hint = Para("", Brand.K.Muted, 11, 4);
        right.Children.Add(hint);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        _signs.Add((sig, mode, preview, hint));
        mode.SelectedIndex = sig.Mode switch { ChecklistMath.SignMode.Mine => 0, ChecklistMath.SignMode.Image => 1, _ => 2 };
        ShowSign(sig, preview, hint);
        mode.SelectionChanged += (_, _) =>
        {
            if ((mode.SelectedItem as ComboBoxItem)?.Tag is not string m) return;
            if (m == ChecklistMath.SignMode.Image)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Pick the signature picture for {role}", Filter = "Pictures|*.png;*.jpg;*.jpeg" };
                if (dialog.ShowDialog(this) == true && Signature.ForWord(dialog.FileName) is not null) sig.ImagePath = dialog.FileName;
                else if (Signature.ForWord(sig.ImagePath) is null)
                {
                    hint.Text = "That isn't a PNG or JPG picture. Pick another, or sign by hand.";
                    hint.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.DangerText);
                    mode.SelectedIndex = 2;
                    return;
                }
            }
            sig.Mode = m;
            ShowSign(sig, preview, hint);
            Changed();
        };
        return CardBox(grid);
    }

    private void ShowSign(ChecklistMath.Signature sig, Image preview, TextBlock hint)
    {
        var path = SignaturePath(sig);
        preview.Source = path.Length > 0 ? Signature.Load(path) : null;
        hint.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.Muted);
        hint.Text = sig.Mode switch
        {
            ChecklistMath.SignMode.Hand => "The box stays empty to sign on the printout.",
            ChecklistMath.SignMode.Mine when preview.Source is null => "No signature picture in Settings > Sign-off yet: add one there, or pick a picture here.",
            ChecklistMath.SignMode.Mine => "Your signature from Settings > Sign-off.",
            _ => preview.Source is null ? "The picture can't be read any more. Pick it again." : Path.GetFileName(sig.ImagePath),
        };
        if (sig.Mode == ChecklistMath.SignMode.Mine && preview.Source is null) hint.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.WarningText);
    }

    private string SignaturePath(ChecklistMath.Signature sig) => sig.Mode switch
    {
        ChecklistMath.SignMode.Mine => _settings.SignatureImage ?? "",
        ChecklistMath.SignMode.Image => sig.ImagePath,
        _ => "",
    };

    private void RefreshReview()
    {
        if (_review is not { } r || _def is null) return;
        var p = ChecklistMath.Count(Kept.Items, _state);
        r.Yes.Text = p.Yes.ToString(CultureInfo.CurrentCulture);
        r.No.Text = p.No.ToString(CultureInfo.CurrentCulture);
        // N/A checks are removed from the checklist: the N/A count is how many were.
        var removed = _def.Items.Count() - p.Total;
        r.Na.Text = removed.ToString(CultureInfo.CurrentCulture);
        r.Score.Text = ChecklistMath.Percent(p).ToString(CultureInfo.CurrentCulture) + "%";
        r.ScoreSub.Text = $"{p.Yes + p.NotApplicable} of {p.Total} checks Yes" + (removed > 0 ? $" ({removed} removed as not applicable)" : "");
        var auto = ChecklistMath.Auto(Kept.Items, Verdict, _state);
        r.AutoScore.Text = auto.Tested == 0 ? "–" : auto.Percent.ToString(CultureInfo.CurrentCulture) + "%";
        r.AutoScore.SetResourceReference(TextBlock.ForegroundProperty, auto.Tested == 0 ? Brand.K.Muted : auto.Percent == 100 ? Brand.K.SuccessText : Brand.K.WarningText);
        r.AutoSub.Text = auto.Tested == 0 ? "The model couldn't test any check. Read the model again on Project details."
            : $"{auto.Pass + auto.ManualDone} of all {auto.Total} checks pass. The model test passes {auto.Pass} of the {auto.Tested} it can test ({auto.Fail} fail, listed below). " +
              $"The other {auto.Manual} are checked by hand: each counts once you answer it Yes or N/A ({auto.ManualDone} so far), so the model test alone reaches at most {auto.Ceiling}%." +
              (Kept.Items.Count(i => i.Auto.Length > 0 && Pending(i.Auto)) is var slow and > 0 ? $" {slow} of them are slow checks not run yet: Run the slow checks on Project details." : "");
        r.Disagree.Children.Clear();
        var disagree = ChecklistMath.Disagree(Kept.Items, _state, Verdict);
        if (disagree.Count > 0) r.Disagree.Children.Add(Para("You answered Yes, but the model check says otherwise. Have another look:", Brand.K.WarningText, 12, 2));
        foreach (var item in disagree.Take(12))
        {
            var said = _facts.Hints.TryGetValue(item.Auto, out var h) ? h.Text : "";
            var link = new Button { Content = Brand.Text($"#{item.Number}: {said}", Brand.K.Link, 12), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(4, 2, 4, 2) };
            link.SetResourceReference(StyleProperty, "SpmLinkButton");
            AutomationProperties.SetName(link, $"Go to #{item.Number}");
            var id = item.Id;
            link.Click += (_, _) => { if (_cards.TryGetValue(id, out var c)) GoTo(c); };
            r.Disagree.Children.Add(link);
        }
        // Where the automatic score loses its points: every check the model test fails, with what it found.
        r.Failing.Children.Clear();
        var failing = Kept.Items.Where(i => i.Auto.Length > 0 && Verdict(i.Auto) == false).ToList();
        if (failing.Count > 0)
        {
            var head = Brand.Text($"Where the automatic score loses points ({failing.Count} checks fail)", Brand.K.Text, 13, bold: true);
            head.Margin = new Thickness(0, 6, 0, 2);
            r.Failing.Children.Add(head);
            r.Failing.Children.Add(Para("Each one is a check the model test fails. Click one to see what's missing and how to fix it (some have a Fix button); then Check the model again.", Brand.K.Muted, 12, 0));
            foreach (var item in failing)
            {
                var said = _facts.Hints.TryGetValue(item.Auto, out var h) ? h.Text : "";
                var link = new Button { Content = Para($"#{item.Number} {item.Title}: {said}", Brand.K.Link, 12, 0), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(4, 2, 4, 2) };
                link.SetResourceReference(StyleProperty, "SpmLinkButton");
                AutomationProperties.SetName(link, $"Go to #{item.Number}");
                var id = item.Id;
                link.Click += (_, _) => { if (_cards.TryGetValue(id, out var c)) GoTo(c); };
                r.Failing.Children.Add(link);
            }
        }
        else if (auto.Tested > 0) r.Failing.Children.Add(Para("Every check the model can test passes.", Brand.K.SuccessText, 12, 4));
        // The checks the model test doesn't settle: yours (or the helper's), each with its answer so far.
        r.Manual.Children.Clear();
        var manual = Kept.Items.Where(i => !Automatic(i)).ToList();
        if (manual.Count > 0)
        {
            var head = Brand.Text($"Checks you do yourself ({manual.Count}): each counts in the automatic score once you answer it Yes or N/A", Brand.K.Text, 13, bold: true);
            head.Margin = new Thickness(0, 10, 0, 2);
            r.Manual.Children.Add(head);
            foreach (var item in manual)
            {
                var done = ChecklistMath.Normal(_state.Answers.TryGetValue(item.Id, out var a) ? a.Done : "");
                var mark = done.Length == 0 ? "not answered" : Word(done);
                var link = new Button { Content = Para($"#{item.Number} {item.Title} ({mark})", done.Length == 0 ? Brand.K.Link : Brand.K.Muted, 12, 0), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0), Padding = new Thickness(4, 1, 4, 1) };
                link.SetResourceReference(StyleProperty, "SpmLinkButton");
                AutomationProperties.SetName(link, $"Go to #{item.Number}");
                var id = item.Id;
                link.Click += (_, _) => { if (_cards.TryGetValue(id, out var c)) GoTo(c); };
                r.Manual.Children.Add(link);
            }
        }
        r.Open.Text = p.Open.ToString(CultureInfo.CurrentCulture);
        r.BannerText.Text = p.Ready
            ? $"Ready for submission: all {p.Total} checks are Yes or N/A. The Word file or PDF will carry the signatures."
            : $"Not ready yet: {p.Open} not answered and {p.No} marked No. You can still make the Word file or PDF for review; it says NOT READY and has no signatures.";
        r.BannerText.SetResourceReference(TextBlock.ForegroundProperty, p.Ready ? Brand.K.SuccessText : Brand.K.DangerText);
        r.Banner.SetResourceReference(Border.BorderBrushProperty, p.Ready ? Brand.K.SuccessText : Brand.K.DangerText);

        r.Problems.Children.Clear();
        var problems = ChecklistMath.Problems(Kept, _state);
        foreach (var (id, text) in problems.Take(12))
        {
            var link = new Button { Content = Brand.Text(text, Brand.K.Link, 12), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(4, 2, 4, 2) };
            link.SetResourceReference(StyleProperty, "SpmLinkButton");
            AutomationProperties.SetName(link, "Go to " + text);
            link.Click += (_, _) => { if (_cards.TryGetValue(id, out var c)) GoTo(c); };
            r.Problems.Children.Add(link);
        }
        if (problems.Count > 12) r.Problems.Children.Add(Para($"…and {problems.Count - 12} more. Go through the stages on the left: a red ! marks a stage with a No.", Brand.K.Muted, 12, 2));
    }

    // ---- The Word file or PDF-----------------------------------------------------------------------------------------

    private async Task CreateFile(bool pdf)
    {
        if (_def is null || _review is not { } r) return;
        var button = pdf ? _pdf : _next;
        if (Brand.IsBusy(_next) || Brand.IsBusy(_pdf)) return;
        Brand.SetBusy(button, true, pdf ? "Creating PDF…" : "Creating…");
        _pdf.IsEnabled = _next.IsEnabled = false;
        string? temp = null;
        try
        {
            SaveDraft();
            var images = new Dictionary<string, ChecklistDocx.Image>(StringComparer.Ordinal);
            foreach (var (sig, _, _, _) in _signs)
                if (Signature.ForWord(SignaturePath(sig)) is { } img) images[sig.Role] = img;
            var folder = Sharing.UserFolder.Checklists;
            var model = Path.GetFileNameWithoutExtension(ChecklistMath.WorkingName(_facts.Model));
            // READY or NOT READY in the name, so check 46 can find the signed checklist.
            var name = $"{ChecklistMath.SafeName(model)} pre-submission checklist {DateTime.Now:yyyy-MM-dd HHmm} - {(ChecklistMath.Count(Kept.Items, _state).Ready ? "READY" : "NOT READY")}";
            var docx = Path.Combine(folder, name + ".docx");
            var info = new ChecklistDocx.Info(_facts.Model, Update.Updater.CurrentText, DateTime.Now,
                ChecklistMath.AutoLine(ChecklistMath.Auto(Kept.Items, Verdict, _state), ChecklistMath.Disagree(Kept.Items, _state, Verdict)));
            var path = docx;
            var note = "";
            if (pdf)
            {
                // The PDF is the Word file laid out by Word, so both look the same. The Word file is only a step on the way.
                temp = Path.Combine(Path.GetTempPath(), "SketchProMax-checklist-" + Guid.NewGuid().ToString("N") + ".docx");
                ChecklistDocx.Write(temp, Kept, _state, info, images);
                path = Path.Combine(folder, name + ".pdf");
                Directory.CreateDirectory(folder);
                try { await ChecklistPdf.Convert(temp, path); }
                catch (Exception ex) when (ex is not OutOfMemoryException) // any Word trouble (missing, blocked, an old version): never lose the file
                {
                    // No Word to make the PDF with: keep the Word file instead, so the work isn't lost.
                    File.Copy(temp, docx, overwrite: true);
                    path = docx;
                    note = (ex is ChecklistPdf.NoWordException ? ex.Message : "Word couldn't make the PDF (" + ex.Message + ").") +
                           " The PDF is made through Word, so the Word file was saved instead. Open it and use File > Save As > PDF if you need one.";
                }
            }
            else ChecklistDocx.Write(docx, Kept, _state, info, images);

            var p = ChecklistMath.Count(Kept.Items, _state);
            var kind = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "Word file";
            r.LastFile = path;
            r.Error.Text = note;
            r.Error.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            r.DoneText.Text = (p.Ready ? $"Signed checklist created ({kind}): " : $"Checklist created for review ({kind}, NOT READY, no signatures): ") + Path.GetFileName(path) +
                              "\nIn " + folder.Replace(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents") + ".";
            RichUi.Set(r.OpenFile, RichUi.Icon.Open, kind == "PDF" ? "Open PDF" : "Open in Word");
            ((Border)r.Done.Tag).Visibility = Visibility.Visible;
            RichUi.FadeIn((Border)r.Done.Tag, _motion);
            r.OpenFile.Focus();
            ActivityLog.Note(kind == "PDF" ? "checklist_pdf" : "checklist_word", $"{p.Yes} yes, {p.No} no, {p.NotApplicable} n/a, {p.Open} open, ready {p.Ready}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            r.Error.Text = $"Couldn't write the {(pdf ? "PDF" : "Word file")} ({ex.Message}). Check that Documents\\Sketch ProMax\\Checklists can be written to, isn't full, " +
                           "and that an older copy isn't open in another program, then try again.";
            r.Error.Visibility = Visibility.Visible;
        }
        finally
        {
            if (temp is not null) { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            Brand.SetBusy(button, false);
            _pdf.IsEnabled = _next.IsEnabled = true;
            StageButtonText();
        }
    }

    private void Shell(string file, string? verb, string? args = null)
    {
        try
        {
            var info = args is null ? new ProcessStartInfo(file) { UseShellExecute = true } : new ProcessStartInfo(file, args) { UseShellExecute = true };
            if (verb is not null) info.Verb = verb;
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            if (_review is null) return;
            _review.Error.Text = verb == "print"
                ? $"Couldn't print it ({ex.Message}). Open it and print from there."
                : $"Couldn't open it ({ex.Message}). The file is {_review.LastFile}.";
            _review.Error.Visibility = Visibility.Visible;
        }
    }

    // ---- Moving around --------------------------------------------------------------------------------------------

    private void Move(int by)
    {
        var to = _stages.SelectedIndex + by;
        if (to < 0 || to >= _stages.Items.Count) return;
        _stages.SelectedIndex = to;
        FocusStage();
    }

    private void StageChanged()
    {
        if (_loading) return;
        var i = Math.Max(0, _stages.SelectedIndex);
        _state.Stage = i;
        var last = i == _stages.Items.Count - 1 && _def is not null;
        _back.IsEnabled = i > 0;
        if (last) RefreshReview();
        StageButtonText();
        _next.IsEnabled = _def is not null && _stages.Items.Count > 1;
        _pdf.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        if (_stages.SelectedItem is TabItem { Content: UIElement page }) RichUi.FadeIn(page, _motion, fromX: 14, fromY: 0);
        Layout();
        Changed(refresh: false);
    }

    /// <summary>On a stage change: the first unanswered check there (else its first), or the stage's first control.</summary>
    private void FocusStage()
    {
        var stage = _stages.SelectedIndex;
        var cards = _cards.Values.Where(c => c.Stage == stage).ToList();
        var target = cards.FirstOrDefault(c => ChecklistMath.Normal(_state.Answers.TryGetValue(c.Item.Id, out var a) ? a.Done : "").Length == 0) ?? cards.FirstOrDefault();
        if (target is not null) { FocusCard(target); return; }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_fieldBoxes.Count > 0 && stage == 0) _fieldBoxes.Values.First().Focus();
            else _next.Focus();
        }));
    }

    private void FocusCard(Card card) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            card.Root.BringIntoView();
            var target = card.Yes.IsChecked == true ? card.Yes : card.No.IsChecked == true ? card.No : card.Na.IsChecked == true ? card.Na : card.Yes;
            target.Focus();
        }));

    private void OnKey(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is Key.Tab or Key.PageDown or Key.PageUp)
        {
            var back = e.Key == Key.PageUp || (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            Move(back ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is TextBox or ComboBox) return;
        var value = e.Key switch { Key.Y => ChecklistMath.Yes, Key.N => ChecklistMath.No, Key.A => ChecklistMath.NotApplicable, _ => "" };
        if (value.Length == 0) return;
        for (DependencyObject? node = Keyboard.FocusedElement as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Border { Tag: Card card })
            {
                Answer(card, value, move: true);
                e.Handled = true;
                return;
            }
            // In the details panel: the check it shows (the panel's own buttons move on).
            if (node == _detail && _cards.TryGetValue(_selectedId, out var shown))
            {
                var button = value switch { ChecklistMath.Yes => _paneYes, ChecklistMath.No => _paneNo, _ => _paneNa };
                if (button is not null) button.IsChecked = true;
                e.Handled = true;
                return;
            }
        }
    }

    // ---- Progress, draft ------------------------------------------------------------------------------------------

    private void Changed(bool refresh = true)
    {
        if (_loading) return;
        if (refresh) Refresh();
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    private void Refresh()
    {
        if (_def is null)
        {
            _status.Text = "Pick your office's checklist to start.";
            _bar.Value = 0;
            foreach (var h in _headers) Paint(h, done: false, bad: false, active: true, 0);
            return;
        }
        var p = ChecklistMath.Count(Kept.Items, _state);
        RichUi.To(_bar, p.Total == 0 ? 0 : (double)p.Answered / p.Total, _motion);
        var ringAuto = ChecklistMath.Auto(Kept.Items, Verdict, _state);
        var mine = ChecklistMath.Percent(p);
        _answersRing.Show(mine, System.Windows.Media.Colors.White, $"Your answers: {p.Yes + p.NotApplicable} of {p.Total} checks Yes or N/A ({mine}%).");
        _autoRing.Show(ringAuto.Tested == 0 ? null : ringAuto.Percent,
            ringAuto.Percent == 100 ? System.Windows.Media.Color.FromRgb(0x86, 0xEF, 0xAC) : System.Windows.Media.Color.FromRgb(0xFC, 0xD3, 0x4D),
            ringAuto.Tested == 0 ? "The model couldn't test any check yet."
                : $"Automatic check: {ringAuto.Pass + ringAuto.ManualDone} of {ringAuto.Total} checks pass ({ringAuto.Percent}%): {ringAuto.Pass} by the model test, {ringAuto.ManualDone} of the {ringAuto.Manual} checked by hand.");
        var auto = ChecklistMath.Auto(Kept.Items, Verdict, _state);
        _status.Text = $"{p.Answered} of {p.Total} answered ({ChecklistMath.Percent(p)}% Yes or N/A)" + (auto.Tested > 0 ? $" · automatic check {auto.Percent}%" : "") + (p.No > 0 ? $" · {p.No} No" : "") + (p.WaitingWithoutNote > 0 ? $" · {p.WaitingWithoutNote} open points without a comment" : "") +
                       (p.Ready ? " · ready to sign" : "");
        for (var i = 0; i < _headers.Count; i++)
        {
            var h = _headers[i];
            if (i == 0)
            {
                var filled = _def.Fields.Count(f => _state.Fields.TryGetValue(f.Label, out var v) && v.Trim().Length > 0);
                h.Sub.Text = $"{filled} of {_def.Fields.Count} filled";
                Paint(h, filled == _def.Fields.Count, false, filled > 0, i);
            }
            else if (i == _headers.Count - 1)
            {
                h.Sub.Text = p.Ready ? "Ready to sign" : $"{p.Open + p.No} still open";
                Paint(h, false, false, p.Answered > 0, i);
            }
            else
            {
                var s = ChecklistMath.Count(h.Items, _state);
                h.Sub.Text = $"{s.Answered} of {s.Total}" + (s.No > 0 ? $" · {s.No} No" : "");
                Paint(h, s.Open == 0 && s.No == 0, s.No > 0, s.Answered > 0, i);
            }
        }
        RefreshReview();
    }

    /// <summary>The primary button: "Next: (stage)", or Create Word file on the last stage (Create PDF sits beside it).</summary>
    private void StageButtonText()
    {
        if (Brand.IsBusy(_next)) return;
        var i = Math.Max(0, _stages.SelectedIndex);
        var names = _def is null ? ["Project details"] : ChecklistMath.Stages(_def);
        if (_def is not null && i == names.Count - 1) RichUi.Set(_next, RichUi.Icon.Word, "Create Word file");
        else RichUi.Set(_next, RichUi.Icon.Next, i + 1 < names.Count ? "Next: " + names[i + 1] : "Next");
    }

    /// <summary>A stage's circle: green with a tick when done, red with ! when a check is No, accent outline once started, grey before.</summary>
    private void Paint(StageHeader h, bool done, bool bad, bool active, int index)
    {
        if (done && !h.WasDone && !_loading) RichUi.Pop(h.Circle, _motion, 0.6);
        h.WasDone = done;
        h.Glyph.Text = done ? "✓" : bad ? "!" : (index + 1).ToString(CultureInfo.InvariantCulture);
        if (done || bad)
        {
            var key = done ? Brand.K.BadgeOk : Brand.K.BadgeError;
            h.Circle.SetResourceReference(Border.BackgroundProperty, key);
            h.Circle.SetResourceReference(Border.BorderBrushProperty, key);
            h.Glyph.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.OnAccent);
        }
        else
        {
            h.Circle.SetResourceReference(Border.BackgroundProperty, Brand.K.Bg);
            h.Circle.SetResourceReference(Border.BorderBrushProperty, active ? Brand.K.Accent : Brand.K.InputBorder);
            h.Glyph.SetResourceReference(TextBlock.ForegroundProperty, active ? Brand.K.Link : Brand.K.Muted);
        }
        AutomationProperties.SetItemStatus(h.Circle, done ? "Done" : bad ? "Has a No" : active ? "Started" : "Not started");
    }

    private void LoadDraft()
    {
        var keepStage = _state.Stage;
        _state = new ChecklistMath.State();
        try
        {
            if (File.Exists(_draftPath) && ChecklistMath.FromJson<ChecklistMath.State>(File.ReadAllText(_draftPath)) is { } saved) _state = saved;
            else _state.Stage = keepStage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _state.Fields ??= new(StringComparer.Ordinal);
        _state.Answers ??= new(StringComparer.Ordinal);
        _state.Signatures ??= [];
        if (_def is null) return;
        _state.Checklist = _def.Title;
        var dropped = ChecklistMath.Reconcile(_def, _state);
        if (dropped > 0 && _loadError.Length == 0)
            _loadError = $"The checklist changed since this model was last checked: {dropped} answers whose check was reworded or removed were cleared. Answer them again.";
        // Empty header fields get what the model (or Settings) says.
        foreach (var f in _def.Fields)
            if (!(_state.Fields.TryGetValue(f.Label, out var v) && v.Trim().Length > 0) && FieldFromModel(f) is { Length: > 0 } auto)
                _state.Fields[f.Label] = auto;
    }

    private void SaveDraft()
    {
        if (_def is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_draftPath)!);
            SafeFile.Write(_draftPath, ChecklistMath.ToJson(_state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the draft is a convenience; the window still works */ }
    }

    private void StartAgain()
    {
        // A plain message box: this window is modeless, outside a Revit API call, where Revit's own dialogs aren't allowed.
        if (MessageBox.Show(this, "Clear every answer, comment and signature for this model? Word files and PDFs you made are kept.", "Sketch ProMax pre-submission checklist",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { if (File.Exists(_draftPath)) File.Delete(_draftPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _state = new ChecklistMath.State();
        Build();
        _stages.SelectedIndex = 0;
        FocusStage();
    }

    // ---- Styles ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The stage list on the left (as in Settings) and the Yes / No / N/A buttons, in palette colours so they follow
    /// Revit's theme. White on the filled answer colours is at least 5:1 in both themes.
    /// </summary>
    private void Styles()
    {
        static string Seg(string key, string on) => $$"""
              <Style x:Key="{{key}}" TargetType="RadioButton">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="FontSize" Value="12.5"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="RadioButton">
                    <Border x:Name="b" Background="{DynamicResource SpmField}" BorderBrush="{DynamicResource SpmInputBorder}" BorderThickness="1" CornerRadius="5" Padding="12,4,12,5">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
                      <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="b" Property="Background" Value="{DynamicResource {{on}}}"/>
                        <Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource {{on}}}"/>
                        <Setter Property="Foreground" Value="{DynamicResource SpmOnAccent}"/>
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/>
                        <Setter TargetName="b" Property="BorderThickness" Value="2"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
            """;
        var xaml = $$"""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              {{Seg("SpmSegYes", Brand.K.BadgeOk)}}
              {{Seg("SpmSegNo", Brand.K.BadgeError)}}
              {{Seg("SpmSegNa", Brand.K.BadgeMuted)}}
              <Style x:Key="SpmLinkButton" TargetType="Button">
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="b" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="3" Padding="{TemplateBinding Padding}">
                      <ContentPresenter/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
              <Style x:Key="SpmStages" TargetType="TabControl">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="Padding" Value="0"/>
                <Setter Property="TabStripPlacement" Value="Left"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabControl">
                    <Grid KeyboardNavigation.TabNavigation="Local">
                      <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <Border Background="{DynamicResource SpmSurface}" BorderBrush="{DynamicResource SpmBorder}" BorderThickness="0,0,1,0">
                        <ScrollViewer VerticalScrollBarVisibility="Auto" Focusable="False">
                          <StackPanel IsItemsHost="True" Width="260" Margin="0,12,0,12" KeyboardNavigation.TabIndex="1"
                                      KeyboardNavigation.TabNavigation="Once" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                        </ScrollViewer>
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
                <Setter Property="MinHeight" Value="48"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabItem">
                    <Grid Background="Transparent" SnapsToDevicePixels="True">
                      <Border x:Name="bg" Background="Transparent" Margin="8,2,10,2" CornerRadius="6"/>
                      <Rectangle x:Name="bar" Width="3" HorizontalAlignment="Left" Margin="8,9,0,9" RadiusX="1.5" RadiusY="1.5"
                                 Fill="{DynamicResource SpmAccent}" Visibility="Collapsed"/>
                      <ContentPresenter ContentSource="Header" Margin="20,8,14,8" VerticalAlignment="Center"/>
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
        // A styling problem never stops the checklist: it falls back to stock controls (stages still on the left).
        try
        {
            var dictionary = (ResourceDictionary)XamlReader.Parse(xaml);
            Resources.MergedDictionaries.Add(dictionary);
            _stages.Style = (Style)dictionary["SpmStages"];
        }
        catch (Exception) { _stages.TabStripPlacement = Dock.Left; }
    }
}
