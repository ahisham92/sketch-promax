using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using SketchProMax.Tools;
using static SketchProMax.Tools.BerthCostMath;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Grid = System.Windows.Controls.Grid;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace SketchProMax.UI;

/// <summary>
/// Berth costing (Berth costing button, next to Berth status): every berth of the project with how complete its
/// costing is (a ring), its cost and US$ per linear metre; click a berth for its costs by group and item, where each
/// quantity came from (the model, by hand, worked out) and what is missing. The unit costs and the item rules are
/// editable at any time. Measure reads the open model per berth in short steps (BerthCostFacts), leaving out what a
/// berth excludes (Berth 9's RoRo part); while the window is open it measures again by itself a few seconds after the
/// model changes, so the costs and US$/lm follow the model. The ticked berths go out as a costing workbook, a BOQ Excel
/// each and one Word back-up calculation in the built-in formats (BerthCostDocs) with the ticked items, their
/// descriptions filled from the model; a project can export a format, edit it in Excel or Word and import it back as its
/// own template (BerthCostExports fills it). Each file marks its deliverable done in the berth status. Reads the model only.
/// </summary>
internal sealed class BerthCostWindow : Window
{
    private static BerthCostWindow? _open;
    private static readonly TimeSpan StepBudget = TimeSpan.FromMilliseconds(200);

    private readonly string _project;
    private string _file = "";
    private Project _p = new();
    private BerthStatusMath.Setup? _setup;
    private string _selected = "";
    private readonly HashSet<string> _chosen = new(StringComparer.OrdinalIgnoreCase);
    private string _page = "berth"; // berth, rates, items, details
    private bool _reading, _stop, _closed;
    private string _savedText = "";

    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted);
    private readonly StackPanel _list = new() { Margin = new Thickness(14, 12, 14, 14) };
    private readonly ScrollViewer _detail = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(20, 14, 20, 20), Focusable = false };
    private readonly Border _runStrip = new() { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 8, 18, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock _runText = Brand.Text("", size: 12.5, bold: true);
    private readonly ProgressBar _runBar = new() { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _measure = RichUi.Button(RichUi.Icon.Refresh, "Measure the model", Brand.Weight.Primary);
    private readonly Button _stopButton = RichUi.Button(RichUi.Icon.Close, "Stop");
    private readonly TextBlock _subtitle = Brand.Text("", Brand.K.HeaderSub);
    private readonly CheckBox _live = new() { Content = "Follow the model", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly System.Windows.Threading.DispatcherTimer _follow = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _seen = -1;
    private DateTime _changedAt = DateTime.MinValue;

    // ---- Opening --------------------------------------------------------------------------------------------------

    public static Result Command(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        var file = doc is null || doc.IsFamilyDocument ? "" : DocName(doc);
        var project = file.Length == 0 ? BerthStatusMath.Projects().FirstOrDefault() ?? "" : ChecklistProjects.ProjectKey(file);
        if (_open is { IsLoaded: true })
        {
            if (_open._project == project || project.Length == 0) { _open._file = file.Length > 0 ? file : _open._file; _open.Refresh(); _open.Activate(); return Result.Succeeded; }
            _open.Close();
        }
        var window = new BerthCostWindow(project, file);
        _open = window;
        window.Closed += (_, _) => { if (_open == window) _open = null; };
        window.Show();
        return Result.Succeeded;
    }

    private static string DocName(Autodesk.Revit.DB.Document doc)
    {
        try { return string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileName(doc.PathName); }
        catch (Exception) { return doc.Title; }
    }

    private BerthCostWindow(string project, string file)
    {
        _project = project;
        _file = file;
        Title = "Sketch ProMax berth costing";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1440, Math.Floor(area.Width * 0.92));
        Height = Math.Min(900, Math.Floor(area.Height * 0.92));
        MinWidth = Math.Min(900, Width);
        MinHeight = Math.Min(480, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));
        _measure.Click += async (_, _) => await Measure();
        AutomationProperties.SetHelpText(_measure, "Reads the open model for the chosen berths (or all of this model's berths), in short steps so Revit stays usable.");
        _stopButton.Click += (_, _) => { _stop = true; Brand.SetBusy(_stopButton, true, "Stopping…"); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !_reading) Close(); };
        Closed += (_, _) => { _closed = true; _stop = true; _follow.Stop(); Save(); };
        Deactivated += (_, _) => { if (!_reading) Save(); };
        // The chat can change rates and quantities too (berth_costing): coming back shows them.
        Activated += (_, _) =>
        {
            if (_reading || _project.Length == 0) return;
            if (SavedText(_project) is { Length: > 0 } text && text != _savedText) { Load(); Refresh(); }
        };
        Load();
        Build();
        _live.IsChecked = _p.Follow;
        AutomationProperties.SetHelpText(_live, "Measure the open model again by itself a few seconds after it changes, so the costs follow the model.");
        _live.Checked += (_, _) => { _p.Follow = true; Save(); _seen = -1; };
        _live.Unchecked += (_, _) => { _p.Follow = false; Save(); };
        _follow.Tick += async (_, _) => await Follow();
        _follow.Start();
    }

    /// <summary>
    /// Every second: when the models changed (the change count moved) and have been quiet for 3 seconds, the open
    /// model's berths are measured again in steps. The first tick after opening measures once if the open model's
    /// berths were never measured or measured over an hour ago.
    /// </summary>
    private async Task Follow()
    {
        if (_closed || _reading || !_p.Follow || _project.Length == 0 || !IsLoaded) return;
        var now = Revit.TimeLog.Changes;
        if (_seen < 0)
        {
            _seen = now;
            var mine = Berths().Where(b => OpenSite.Length == 0 || Same(b.Site, OpenSite)).ToList();
            if (_file.Length > 0 && mine.Count > 0 && mine.Any(b => b.Measured is null || (DateTime.Now - b.Measured.At).TotalHours > 1)) await Measure(quiet: true);
            return;
        }
        if (now != _seen) { _seen = now; _changedAt = DateTime.Now; return; }
        if (_changedAt == DateTime.MinValue || (DateTime.Now - _changedAt).TotalSeconds < 3) return;
        _changedAt = DateTime.MinValue;
        await Measure(quiet: true);
    }

    private void Load()
    {
        _setup = _project.Length == 0 ? null : BerthStatusMath.LoadSetup(_project);
        _p = _project.Length == 0 ? new Project() : BerthCostMath.Load(_project);
        SyncBerths(_p, _setup);
        _savedText = _project.Length == 0 ? "" : SavedText(_project);
    }

    private void Save()
    {
        if (_project.Length == 0) return;
        try { BerthCostMath.Save(_project, _p); _savedText = SavedText(_project); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "Couldn't save (" + ex.Message + ")."; }
    }

    /// <summary>The site (setup model) of the open file, or "" when the setup doesn't know it.</summary>
    private string OpenSite => _setup is not null && _file.Length > 0 && BerthStatusMath.ModelFor(_setup, _file) is { } m ? m.Name : "";

    private List<Berth> Berths() => SyncBerths(_p, _setup);

    /// <summary>The berths shown: the open model's (its site's) only, unless Show every site is on or no site is known.</summary>
    private List<Berth> Shown() => Berths().Where(b => _allSites || OpenSite.Length == 0 || Same(b.Site, OpenSite)).ToList();

    private bool _allSites;

    private Berth? Selected => _p.Berths.TryGetValue(_selected, out var b) ? b : null;

    // ---- Layout ---------------------------------------------------------------------------------------------------

    private void Build()
    {
        foreach (var reused in new FrameworkElement[] { _status, _list, _detail, _runStrip, _runText, _runBar, _measure, _stopButton, _subtitle, _live })
            switch (reused.Parent)
            {
                case Panel p: p.Children.Remove(reused); break;
                case Decorator d: d.Child = null; break;
                case ContentControl c: c.Content = null; break;
            }
        Content = null;

        var band = Brand.Header("Berth costing", null);
        if (band.Child is DockPanel row && row.Children.OfType<StackPanel>().LastOrDefault() is { } texts) texts.Children.Add(_subtitle);
        band.Padding = new Thickness(16, 12, 720, 12);
        var header = new Grid();
        header.Children.Add(band);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        Button Top(string glyph, string text, string help, Action go)
        {
            var b = RichUi.Button(glyph, text);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetHelpText(b, help);
            b.Click += (_, _) => go();
            right.Children.Add(b);
            return b;
        }
        Top(RichUi.Icon.Edit, "Unit costs", "Change the unit costs and the pile rate basis; every berth's cost follows.", () => { _page = "rates"; ShowDetail(); });
        Top(RichUi.Icon.Settings, "Items", "Which items go in the files, how each is found in the model, its BOQ description and back-up section.", () => { _page = "items"; ShowDetail(); });
        Top(RichUi.Icon.Edit, "Project details", "The header lines, title and document number printed on the BOQ, typed once for the project.", () => { _page = "details"; ShowDetail(); });
        var files = Top(RichUi.Icon.Folder, "Templates", "Export a built-in format to edit in Excel or Word and import it back as this project's own template.", () => { });
        files.Click += (_, _) => TemplatesMenu(files);
        _live.IsEnabled = _project.Length > 0;
        right.Children.Add(_live);
        _measure.VerticalAlignment = VerticalAlignment.Center;
        _measure.IsEnabled = _project.Length > 0;
        right.Children.Add(_measure);
        header.Children.Add(right);
        DockPanel.SetDock(header, Dock.Top);

        _runStrip.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        _runStrip.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        var runGrid = new Grid();
        runGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        runGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        runGrid.Children.Add(new StackPanel { Children = { _runText, _runBar } });
        _stopButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_stopButton, 1);
        runGrid.Children.Add(_stopButton);
        _runStrip.Child = runGrid;
        DockPanel.SetDock(_runStrip, Dock.Top);

        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.Click += (_, _) => Close();
        var all = RichUi.Button(RichUi.Icon.Export, "Make all three", Brand.Weight.Primary);
        AutomationProperties.SetHelpText(all, "Makes the costing, a BOQ per berth and the back-up calculation for the ticked berths, and marks them done in the berth status.");
        all.Click += (_, _) => Export(Made.Comparison, Made.Boq, Made.Backup);
        var comparison = RichUi.Button(RichUi.Icon.Export, "Costing Excel");
        comparison.Click += (_, _) => Export(Made.Comparison);
        var boq = RichUi.Button(RichUi.Icon.Export, "BOQ Excel");
        boq.Click += (_, _) => Export(Made.Boq);
        var backup = RichUi.Button(RichUi.Icon.Word, "Back-up (Word)");
        backup.Click += (_, _) => Export(Made.Backup);
        var folder = RichUi.Button(RichUi.Icon.Folder, "Open folder");
        folder.Click += (_, _) => Shell(OutputFolder());
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var foot = new DockPanel { Margin = new Thickness(18, 10, 18, 12) };
        var buttons = Brand.ButtonRow(comparison, boq, backup, all, folder, close);
        buttons.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        foot.Children.Add(_status);
        var footBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };
        footBorder.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        DockPanel.SetDock(footBorder, Dock.Bottom);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star), MinWidth = 380 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star), MinWidth = 480 });
        var listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var listBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = listScroll };
        listBorder.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        listBorder.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        body.Children.Add(listBorder);
        Grid.SetColumn(_detail, 1);
        body.Children.Add(_detail);

        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(_runStrip);
        root.Children.Add(footBorder);
        root.Children.Add(body);
        Content = root;
        Refresh();
    }

    // ---- The berth list -------------------------------------------------------------------------------------------

    private void Refresh(bool detail = true)
    {
        var every = Berths();
        var berths = Shown();
        _subtitle.Text = _project.Length == 0 ? "Open a model of the project first"
            : $"{_project} · " + (OpenSite.Length > 0 && !_allSites ? $"{OpenSite} model open · {berths.Count} berths" : $"{berths.Count} berths") + $" · {_p.Stage}";
        if (_selected.Length == 0 || !berths.Any(b => b.Key == _selected)) _selected = berths.FirstOrDefault()?.Key ?? "";
        _list.Children.Clear();
        if (every.Count == 0)
        {
            _list.Children.Add(Heading("No berths yet"));
            _list.Children.Add(Para(_setup is null
                ? "Set up the berth status (Submissions status > Setup) and its berths come here; or add a berth below by the parameter that names it."
                : "The berth status setup has no berths found by a parameter or a scope box. Add one below."));
            _list.Children.Add(AddBerthBox());
            ShowDetail();
            return;
        }
        // The overview: the whole project, then each site with its berths.
        var known = berths.Select(b => Completeness(_p, b)).OfType<double>().ToList();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new PercentRing(known.Count > 0 ? known.Average() : null, 76, 8, 17));
        var sums = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var costed = berths.Where(b => b.Measured is not null || b.Hand.Count > 0).ToList();
        var structural = costed.Sum(b => Total(_p, b).Structural);
        var length = costed.Sum(b => b.Length ?? 0);
        sums.Children.Add(Brand.Text(OpenSite.Length > 0 && !_allSites ? OpenSite : "All berths", size: 14, bold: true));
        var noRates = _p.Rates.All(r => r.Value is null);
        sums.Children.Add(Small(costed.Count == 0 ? "Nothing measured yet: press Measure the model with a model open." :
            noRates ? $"{costed.Count} of {berths.Count} measured · no unit costs yet, so no cost: Unit costs, or Templates > Take the unit costs from a cost comparison workbook" :
            $"{costed.Count} of {berths.Count} costed · {BerthCostMath.Money(structural, _p.Currency)}" + (length > 0 ? $" · {BerthCostMath.Money(structural / length, _p.Currency)} / lm" : " · type the quay lengths for US$ / lm")));
        sums.Children.Add(Small($"Tick berths to make their files; {_chosen.Count} ticked." + (_p.Follow ? " Costs follow the model." : "")));
        top.Children.Add(sums);
        _list.Children.Add(top);
        if (OpenSite.Length > 0 && every.Any(b => !Same(b.Site, OpenSite)))
        {
            var all = new CheckBox { Content = "Show the other sites' berths too", IsChecked = _allSites, Margin = new Thickness(2, 2, 0, 0) };
            all.Checked += (_, _) => { _allSites = true; Refresh(); };
            all.Unchecked += (_, _) => { _allSites = false; Refresh(); };
            _list.Children.Add(all);
        }

        foreach (var site in berths.GroupBy(b => b.Site.Length > 0 ? b.Site : "Project"))
        {
            var title = Brand.Text(site.Key.ToUpperInvariant() + (Same(site.Key, OpenSite) ? " · OPEN" : ""), Brand.K.Muted, 12, bold: true);
            title.Margin = new Thickness(2, 14, 0, 6);
            _list.Children.Add(title);
            var tiles = new WrapPanel();
            foreach (var b in site) tiles.Children.Add(BerthTile(b));
            _list.Children.Add(tiles);
        }
        _list.Children.Add(AddBerthBox());
        if (detail) ShowDetail();
    }

    private FrameworkElement BerthTile(Berth b)
    {
        var done = Completeness(_p, b);
        var t = Total(_p, b);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var tick = new CheckBox { IsChecked = _chosen.Contains(b.Key), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0) };
        AutomationProperties.SetName(tick, "Make files for " + b.Title);
        tick.Checked += (_, _) => { _chosen.Add(b.Key); };
        tick.Unchecked += (_, _) => { _chosen.Remove(b.Key); };
        row.Children.Add(tick);
        row.Children.Add(new PercentRing(done, 58, 7, 14));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), Width = 150 };
        texts.Children.Add(Brand.Text(b.Title, size: 13, bold: true));
        texts.Children.Add(Small(done is null ? "not measured" : t.Structural <= 0 && _p.Rates.All(r => r.Value is null) ? "measured · no unit costs" : BerthCostMath.Money(t.Structural, _p.Currency)));
        if (t.PerLm > 0) texts.Children.Add(Small(BerthCostMath.Money(t.PerLm, _p.Currency) + " / lm"));
        var made = b.Made.Count > 0 ? "made: " + string.Join(", ", b.Made.Keys) : "";
        if (made.Length > 0) texts.Children.Add(Small(made));
        row.Children.Add(texts);
        var on = b.Key == _selected && _page == "berth";
        var tile = new Border
        {
            Padding = new Thickness(8, 8, 10, 8), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(on ? 2 : 1), Child = row, Cursor = Cursors.Hand, Focusable = true,
            ToolTip = $"{b.Title}: {(done is null ? "not measured" : Math.Round(done.Value) + "% complete")}. Click for its costs.",
        };
        tile.SetResourceReference(Border.BackgroundProperty, on ? Brand.K.AccentSoft : Brand.K.Bg);
        tile.SetResourceReference(Border.BorderBrushProperty, on ? Brand.K.Accent : Brand.K.Border);
        AutomationProperties.SetName(tile, $"{b.Title}, {(done is null ? "not measured" : Math.Round(done.Value) + "%")}");
        void Pick() { _selected = b.Key; _page = "berth"; Refresh(); }
        tile.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) Pick(); };
        tile.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space && e.OriginalSource == tile) { Pick(); e.Handled = true; } };
        return tile;
    }

    /// <summary>Adds a berth by hand: its site, the parameter that names it and its value.</summary>
    private FrameworkElement AddBerthBox()
    {
        var page = new StackPanel();
        page.Children.Add(Brand.Text("Add a berth", size: 13, bold: true));
        page.Children.Add(Small("For a berth the berth status doesn't list: its site (as on the comparison workbook), the parameter on its elements and the value."));
        var site = Field(OpenSite.Length > 0 ? OpenSite : _p.Berths.Values.Select(b => b.Site).FirstOrDefault() ?? "", "Site");
        var parameter = Field(_p.Berths.Values.Select(b => b.Parameter).FirstOrDefault(x => x.Length > 0) ?? "Berth Number", "Parameter");
        var value = Field("", "Value");
        var add = RichUi.Button(RichUi.Icon.Add, "Add");
        add.Click += (_, _) =>
        {
            if (value.Text.Trim().Length == 0) { _status.Text = "Give the value that names the berth (for example 11)."; return; }
            var b = AddBerth(_p, site.Text.Trim(), parameter.Text.Trim(), value.Text.Trim());
            _selected = b.Key;
            Save();
            Refresh();
        };
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, box) in new[] { ("Site", site), ("Parameter", parameter), ("Value", value) })
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
            cell.Children.Add(Small(label));
            box.Width = label == "Value" ? 70 : 120;
            cell.Children.Add(box);
            row.Children.Add(cell);
        }
        add.VerticalAlignment = VerticalAlignment.Bottom;
        add.Margin = new Thickness(0, 0, 0, 4);
        row.Children.Add(add);
        page.Children.Add(row);
        return Box(page, top: 16);
    }

    // ---- The right side -----------------------------------------------------------------------------------------------

    private void ShowDetail()
    {
        _detail.Content = _page switch
        {
            "rates" => RatesPage(),
            "items" => ItemsPage(),
            "details" => DetailsPage(),
            _ => Selected is { } b ? BerthPage(b) : Para("Pick a berth on the left.", Brand.K.Muted),
        };
    }

    private FrameworkElement BerthPage(Berth b)
    {
        var page = new StackPanel();
        var done = Completeness(_p, b);
        var t = Total(_p, b);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new PercentRing(done, 104, 10, 22));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        texts.Children.Add(Heading(b.Title));
        texts.Children.Add(Para($"{b.Site} · {b.Package}" + (b.Measured is { } m ? $" · measured {m.At:dd-MMM HH:mm} in {m.Model}" : " · not measured yet"), Brand.K.Muted, 2));
        texts.Children.Add(Brand.Text($"{BerthCostMath.Money(t.Structural, _p.Currency)} structural works" + (t.PerLm > 0 ? $" · {BerthCostMath.Money(t.PerLm, _p.Currency)} / lm" : ""), size: 15, bold: true));
        if (t.Prelims > 0) texts.Children.Add(Small($"Preliminaries and demolition as billed: {BerthCostMath.Money(t.Prelims, _p.Currency)}"));
        head.Children.Add(texts);
        page.Children.Add(head);

        // What it is and what it leaves out.
        page.Children.Add(Section("The berth"));
        var grid = new WrapPanel();
        TextBox Labeled(string label, string text, double width, string help)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 6) };
            cell.Children.Add(Small(label));
            var box = Field(text, label);
            box.Width = width;
            AutomationProperties.SetHelpText(box, help);
            cell.Children.Add(box);
            grid.Children.Add(cell);
            return box;
        }
        var len = Labeled("Quay length (m)", b.Length is { } l ? l.ToString("0.##", CultureInfo.InvariantCulture) : "", 110, "The berth's quay length, for US$ per linear metre.");
        len.LostFocus += (_, _) => { b.Length = Num(len.Text) is > 0 and var v ? v : null; Changed(); };
        var exclude = Labeled("Leave out scope boxes", b.Exclude, 200, "Scope boxes whose elements are not this berth's (for example BERTH 9 - PART 1); several with ;");
        exclude.LostFocus += (_, _) => { if (exclude.Text.Trim() != b.Exclude) { b.Exclude = exclude.Text.Trim(); Changed(); } };
        var note = Labeled("Which is", b.ExcludeNote, 150, "A short name for what is left out (excl. RoRo); it goes in the berth's name in the files.");
        note.LostFocus += (_, _) => { if (note.Text.Trim() != b.ExcludeNote) { b.ExcludeNote = note.Text.Trim(); Changed(); } };
        page.Children.Add(grid);
        page.Children.Add(Small(b.FindBy == BerthStatusMath.FindBy.ScopeBox ? $"Found by scope box {b.Value}." : $"Found by {b.Parameter} = {b.Value}."));

        // Design parameters.
        page.Children.Add(Section("Design parameters"));
        page.Children.Add(Small("Pile sizes come from the model when it is measured; reinforcement rates (kg/m³) from the comparison workbook or typed here."));
        var pars = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (key, label, unit) in BerthCostMath.Params)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 6), Width = 150 };
            cell.Children.Add(Small($"{label} ({unit})"));
            var box = Field(b.Params.TryGetValue(key, out var v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "", label);
            box.LostFocus += (_, _) =>
            {
                if (box.Text.Trim().Length == 0) b.Params.Remove(key);
                else if (Num(box.Text) is { } n) b.Params[key] = n;
                else { _status.Text = $"{label}: type a number."; return; }
                Changed();
            };
            cell.Children.Add(box);
            pars.Children.Add(cell);
        }
        page.Children.Add(pars);

        // Costs by group, then by item.
        page.Children.Add(Section("Cost by group"));
        var groups = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gr = 0;
        foreach (var (code, name) in Groups)
        {
            groups.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(groups, Brand.Text($"{code}  {name}", size: 12.5), gr, 0);
            Put(groups, Right(Brand.Text(BerthCostMath.Money(t.ByGroup[code], _p.Currency), size: 12.5)), gr, 1);
            gr++;
        }
        page.Children.Add(groups);

        page.Children.Add(Section("Items"));
        page.Children.Add(Small("Tick the items that go in the files. Type a quantity to set it by hand (empty: the model's). Worked-out items follow the concrete and the kg/m³. Under each item, its BOQ description with this berth's numbers. Show selects the elements in Revit."));
        page.Children.Add(ItemsTable(b));

        var missing = Missing(_p, b);
        if (missing.Count > 0)
        {
            page.Children.Add(Section("To complete it"));
            foreach (var what in missing.Take(30)) page.Children.Add(Para("• " + what, Brand.K.Text, 2));
        }
        if (b.Measured is { Notes.Count: > 0 } meas)
            page.Children.Add(Small("Not found: " + string.Join("; ", meas.Notes.Select(n => (_p.ItemOf(n.Key)?.Name ?? n.Key.ToString(CultureInfo.InvariantCulture)) + " (" + n.Value + ")"))));
        return page;
    }

    private FrameworkElement ItemsTable(Berth b)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var w in new[] { 0.35, 2.6, 1.0, 0.5, 0.8, 1.0, 1.1, 0.6 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = ["IN FILES", "#", "ITEM", "QUANTITY", "UNIT", "FROM", "RATE", "COST", ""];
        for (var i = 0; i < heads.Length; i++) Put(grid, Brand.Text(heads[i], Brand.K.Muted, 10.5, bold: true), 0, i);
        var row = 1;
        foreach (var line in Costs(_p, b))
        {
            var item = line.Item;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Ticked items go in the BOQ and the back-up (for every berth of the project); the cost counts every item.
            var inFiles = new CheckBox { IsChecked = item.InFiles, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 4, 10, 0), ToolTip = "In the BOQ and the back-up calculation" };
            AutomationProperties.SetName(inFiles, item.Name + " in the files");
            inFiles.Checked += (_, _) => { item.InFiles = true; Changed(); };
            inFiles.Unchecked += (_, _) => { item.InFiles = false; Changed(); };
            Put(grid, inFiles, row, 0);
            Put(grid, Brand.Text(item.No.ToString(CultureInfo.InvariantCulture), Brand.K.Muted, 12), row, 1);
            var name = new StackPanel { Margin = new Thickness(0, 2, 8, 4) };
            name.Children.Add(Brand.Text(item.Name, item.InFiles ? Brand.K.Text : Brand.K.Muted, 12));
            // Its BOQ description with the berth's numbers (pile lengths, tips, tonnage) as it will be printed.
            if (item.BoqText.Length > 0 && line.Qty is > 0) name.Children.Add(Small(Describe(_p, b, item)));
            Put(grid, name, row, 2);
            if (item.No is InfillRebar or SuperRebar && line.From != Source.Hand)
                Put(grid, Brand.Text(Qty(line.Qty), size: 12), row, 3);
            else
            {
                var box = Field(b.Hand.TryGetValue(item.No, out var h) ? h.ToString("0.###", CultureInfo.InvariantCulture) : "", item.Name + " quantity");
                box.Margin = new Thickness(0, 1, 6, 1);
                SetHint(box, line.From == Source.Model ? Qty(line.Qty) : "");
                var no = item.No;
                box.LostFocus += (_, _) =>
                {
                    var text = box.Text.Trim();
                    if (text.Length == 0) { if (!b.Hand.Remove(no)) return; }
                    else if (Num(text) is { } v) { if (b.Hand.TryGetValue(no, out var old) && old == v) return; b.Hand[no] = v; }
                    else { _status.Text = $"{item.Name}: type a number, or leave it empty for the model's."; return; }
                    Changed();
                };
                box.VerticalAlignment = VerticalAlignment.Top;
                Put(grid, box, row, 3);
            }
            Put(grid, Brand.Text(item.Unit, Brand.K.Muted, 12), row, 4);
            Put(grid, Brand.Text(line.From switch { Source.Model => "model", Source.Hand => "by hand", Source.Worked => "worked out", _ => "–" },
                line.From == Source.Hand ? Brand.K.WarningText : Brand.K.Muted, 11.5), row, 5);
            Put(grid, Right(Brand.Text(line.Rate > 0 ? line.Rate.ToString("#,0.##", CultureInfo.InvariantCulture) : "–", size: 12)), row, 6);
            Put(grid, Right(Brand.Text(line.Cost > 0 ? Math.Round(line.Cost).ToString("#,0", CultureInfo.InvariantCulture) : "–", size: 12)), row, 7);
            if (b.Measured?.Ids.TryGetValue(item.No, out var ids) == true && ids.Count > 0)
            {
                var show = new Button { Content = "Show", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 1, 0, 1), ToolTip = $"Select the {ids.Count} elements in Revit" };
                show.Click += async (_, _) => await ShowInRevit(ids);
                show.VerticalAlignment = VerticalAlignment.Top;
                Put(grid, show, row, 8);
            }
            row++;
        }
        return grid;
    }

    /// <summary>The model's figure, shown greyed in the empty box (typing replaces it by hand).</summary>
    private static void SetHint(TextBox box, string hint)
    {
        if (hint.Length == 0) return;
        box.ToolTip = "The model gives " + hint + ". Type to set it by hand; empty it to use the model's again.";
        AutomationProperties.SetHelpText(box, "The model gives " + hint);
        var placeholder = new VisualBrush(new TextBlock { Text = hint, FontSize = 12, Margin = new Thickness(4, 0, 0, 0) })
        { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, Opacity = 0.55 };
        void Update() => box.Background = box.Text.Length == 0 && !box.IsKeyboardFocused ? placeholder : null;
        box.TextChanged += (_, _) => Update();
        box.GotKeyboardFocus += (_, _) => Update();
        box.LostKeyboardFocus += (_, _) => Update();
        box.Loaded += (_, _) => Update();
    }

    // ---- Unit costs ---------------------------------------------------------------------------------------------------

    private FrameworkElement RatesPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("Unit costs"));
        page.Children.Add(Para("Change any rate at any time; every berth's cost follows. Templates > Take the unit costs from a cost comparison workbook fills them from one.", Brand.K.Muted));
        page.Children.Add(Section("Bored pile rate"));
        page.Children.Add(Small("A bored pile's rate per lm is rebuilt from the given pile rate (R_PILE_GIVEN) for its own diameter and kg/m³: installation scaled by area (or by diameter), plus its concrete and reinforcement."));
        var basis = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Basis(string label, double value, Action<double> set)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 6) };
            cell.Children.Add(Small(label));
            var box = Field(value.ToString("0.###", CultureInfo.InvariantCulture), label);
            box.Width = 110;
            box.LostFocus += (_, _) => { if (Num(box.Text) is > 0 and var v && v != value) { set(v); Changed(); } };
            cell.Children.Add(box);
            basis.Children.Add(cell);
        }
        Basis("Given rate's pile diameter (m)", _p.Pile.RefDiameter, v => _p.Pile.RefDiameter = v);
        Basis("Given rate's kg/m³", _p.Pile.RefKg, v => _p.Pile.RefKg = v);
        var scaling = new ComboBox { Width = 140, ItemsSource = new[] { "By area (volume)", "By diameter" }, SelectedIndex = _p.Pile.ByVolume ? 0 : 1, Margin = new Thickness(0, 14, 0, 0) };
        AutomationProperties.SetName(scaling, "Installation scaling");
        scaling.SelectionChanged += (_, _) => { _p.Pile.ByVolume = scaling.SelectedIndex == 0; Changed(); };
        basis.Children.Add(scaling);
        page.Children.Add(basis);

        page.Children.Add(Section("Rates"));
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var w in new[] { 2.6, 0.6, 1.0 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = ["ITEM", "UNIT", $"RATE ({_p.Currency})"];
        for (var i = 0; i < heads.Length; i++) Put(grid, Brand.Text(heads[i], Brand.K.Muted, 10.5, bold: true), 0, i);
        var row = 1;
        foreach (var rate in _p.Rates)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // The rate's short code (R_PIPE) is only how items point at it (Items page); it shows on hover.
            var rateName = Brand.Text(rate.Name, size: 12);
            rateName.ToolTip = $"Code {rate.Code}: the items' rate column on the Items page points at it.";
            Put(grid, rateName, row, 0);
            Put(grid, Brand.Text(rate.Unit, Brand.K.Muted, 12), row, 1);
            var box = Field(rate.Value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "", rate.Name + " rate");
            box.Margin = new Thickness(0, 1, 8, 1);
            var r = rate;
            box.LostFocus += (_, _) =>
            {
                var text = box.Text.Trim();
                double? value = text.Length == 0 ? null : Num(text);
                if (text.Length > 0 && value is null) { _status.Text = $"{r.Name}: type a number."; return; }
                if (value == r.Value) return;
                r.Value = value;
                r.Assumed = false;
                Changed();
                _status.Text = $"{r.Name}: {(value is { } x ? x.ToString("#,0.##", CultureInfo.InvariantCulture) : "no rate")}. Every berth's cost follows.";
            };
            Put(grid, box, row, 2);
            row++;
        }
        page.Children.Add(grid);
        return page;
    }

    // ---- Items ----------------------------------------------------------------------------------------------------------

    private FrameworkElement ItemsPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("Items"));
        page.Children.Add(Para("How each item is found. Model rule: cat=category; a parameter=value (* any text, several with ,; != for not); thick=mm,mm for floors; " +
            "over=parameter=value for floors over those elements; measure=count, length, volume, area or weight; table=tube, core, pile or slab for the back-up; group=a parameter, thickness or number; " +
            "OR between two ways. BOQ description: the words printed in the BOQ, with {count}, {height}, {tips}, {tip}, {od}, {id}, {t}, {d}, {dmm}, {rebar}, {thicks}, {type}, {items} " +
            "filled from the model per berth (a figure the model doesn't give prints as …). BOQ section: SECTION|Sub-heading ({n} the berth). Own BOQ row: for a project's own BOQ template, " +
            "words in its row's description (; all of them, | choices, ^ starts with, = the whole text). Untick an item to leave it out of the BOQ and the back-up.", Brand.K.Muted));
        var reset = RichUi.Button(RichUi.Icon.Reset, "Back to the standard items");
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 8, 0, 4);
        reset.Click += (_, _) => { _p.Items = DefaultItems(); Changed(); _status.Text = "Items are back to the standard ones. Measure again to use them."; };
        page.Children.Add(reset);
        foreach (var item in _p.Items)
        {
            var box = new StackPanel();
            var tick = new CheckBox
            {
                IsChecked = item.InFiles, Content = Brand.Text($"{item.No}. {item.Name}  ·  {item.Unit}  ·  {item.Group}  ·  rate {item.RateCode}", size: 12.5, bold: true),
                ToolTip = "Ticked: in the BOQ and the back-up calculation",
            };
            tick.Checked += (_, _) => { item.InFiles = true; Save(); };
            tick.Unchecked += (_, _) => { item.InFiles = false; Save(); };
            box.Children.Add(tick);
            TextBox Line(string label, string text, Action<string> set)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
                var l = Small(label);
                l.Width = 90;
                l.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(l, Dock.Left);
                row.Children.Add(l);
                var t = Field(text, item.Name + " " + label);
                t.TextWrapping = TextWrapping.Wrap;
                t.LostFocus += (_, _) => { if (t.Text.Trim() != text) { set(t.Text.Trim()); text = t.Text.Trim(); Changed(); } };
                row.Children.Add(t);
                box.Children.Add(row);
                return t;
            }
            var rule = Line("Model rule", item.Rule, v =>
            {
                try { if (v.Length > 0) ParseRule(v); item.Rule = v; }
                catch (FormatException ex) { _status.Text = $"{item.Name}: {ex.Message} Not changed."; }
            });
            Line("BOQ description", item.BoqText, v => item.BoqText = v);
            Line("BOQ section", item.Section, v => item.Section = v);
            Line("BOQ unit", item.BoqUnit, v => item.BoqUnit = v);
            Line("Own BOQ row", item.Boq, v => item.Boq = v);
            Line("Back-up", item.Backup, v => item.Backup = v);
            page.Children.Add(Box(box, top: 8));
        }
        return page;
    }

    // ---- Project details ------------------------------------------------------------------------------------------------

    private FrameworkElement DetailsPage()
    {
        var d = _p.Details;
        var page = new StackPanel();
        page.Children.Add(Heading("Project details"));
        page.Children.Add(Para("What the built-in BOQ prints about the project, typed once. {n} is the berth's number, {nn} the same with two digits, {length} its quay length.", Brand.K.Muted));
        void Text(string label, string help, string value, Action<string> set, bool lines = false)
        {
            page.Children.Add(Section(label));
            page.Children.Add(Small(help));
            var box = Field(value, label);
            box.Margin = new Thickness(0, 4, 0, 0);
            if (lines) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.MinHeight = 64; box.VerticalContentAlignment = VerticalAlignment.Top; }
            box.LostFocus += (_, _) => { var v = box.Text.Replace("\r\n", "\n").TrimEnd(); if (v != value) { value = v; set(v); Save(); } };
            page.Children.Add(box);
        }
        Text("Page header, left", "The project's lines at the top left of every BOQ page (one per line).", d.HeaderLeft, v => d.HeaderLeft = v, lines: true);
        Text("Page header, right", "Lines at the top right (for example the stage or the client's reference).", d.HeaderRight, v => d.HeaderRight = v, lines: true);
        Text("Structure title", "Under the works' name at the top of the bill.", d.Title, v => d.Title = v);
        Text("Document number", "In the footer and the file name, for example PRJ1-B{nn}-BOQ. Empty: the file is named BOQ Berth nn.", d.DocNumber, v => d.DocNumber = v);
        Text("Works", "The works' name: the bill's heading, the footer and the summary line.", d.Works, v => d.Works = v);
        var rates = new CheckBox { Content = "Print the unit rates and amounts in the BOQ (unticked: an empty bill for pricing)", IsChecked = d.BoqRates, Margin = new Thickness(0, 14, 0, 0) };
        rates.Checked += (_, _) => { d.BoqRates = true; Save(); };
        rates.Unchecked += (_, _) => { d.BoqRates = false; Save(); };
        page.Children.Add(rates);
        page.Children.Add(Section("Where the files go"));
        page.Children.Add(Small(OutputFolder()));
        return page;
    }

    // ---- Templates ----------------------------------------------------------------------------------------------------

    private void TemplatesMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string text, Action go, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled && _project.Length > 0 };
            mi.Click += (_, _) => go();
            menu.Items.Add(mi);
        }
        string Using(string path) => File.Exists(path) ? "this project's own (" + Strip(path) + ")" : "built in";
        menu.Items.Add(new MenuItem { Header = $"BOQ: {Using(_p.BoqTemplate)}", IsEnabled = false });
        Item("Export the BOQ format to edit in Excel…", () => ExportFormat(Made.Boq), Selected is not null);
        Item("Use an edited BOQ as this project's…", () => Pick(Made.Boq));
        Item("BOQ back to the built-in format", () => Base(Made.Boq), File.Exists(_p.BoqTemplate));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = $"Back-up calculation: {Using(_p.BackupTemplate)}", IsEnabled = false });
        Item("Export the back-up format to edit in Word…", () => ExportFormat(Made.Backup));
        Item("Use an edited back-up calculation as this project's…", () => Pick(Made.Backup));
        Item("Back-up back to the built-in format", () => Base(Made.Backup), File.Exists(_p.BackupTemplate));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = $"Costing: {(File.Exists(_p.ComparisonTemplate) ? "your cost comparison workbook (" + Strip(_p.ComparisonTemplate) + ")" : "built in")}", IsEnabled = false });
        Item("Make the costing in a cost comparison workbook…", () => Pick(Made.Comparison));
        Item("Costing back to the built-in workbook", () => Base(Made.Comparison), File.Exists(_p.ComparisonTemplate));
        Item("Take the unit costs from a cost comparison workbook…", () => { if (Choose(false, "A cost comparison workbook") is { } path) ImportComparison(path); });
        menu.Items.Add(new Separator());
        Item("Open the project's templates folder", () => { var f = Path.Combine(ProjectFolder(_project), "templates"); Directory.CreateDirectory(f); Shell(f); });
        menu.IsOpen = true;
    }

    private string? Choose(bool word, string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = word ? "Word document (*.docx)|*.docx" : "Excel workbook (*.xlsx)|*.xlsx" };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>Writes the built-in format (with this project's figures) to edit and opens it; Use … brings it back.</summary>
    private void ExportFormat(string kind)
    {
        try
        {
            var folder = Path.Combine(ProjectFolder(_project), "templates");
            Directory.CreateDirectory(folder);
            string path;
            if (kind == Made.Boq)
            {
                if (Selected is not { } b) { _status.Text = "Pick a berth first: its BOQ is the format to edit."; return; }
                path = OfficeDocs.FreePath(OutputFolder(), "BOQ format to edit", ".xlsx");
                BerthCostDocs.WriteBoq(_p, b, path);
            }
            else
            {
                var berths = Chosen();
                path = OfficeDocs.FreePath(OutputFolder(), "Back-up format to edit", ".docx");
                BerthCostDocs.WriteBackup(_p, berths, path, example: true);
            }
            _status.Text = $"Wrote {Path.GetFileName(path)}. Change its layout, words and styles (keep one table of each kind), save it, then Templates > Use an edited … to make it this project's.";
            Shell(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "Couldn't write it: " + ex.Message; }
    }

    /// <summary>Copies the picked file into the project's templates folder (so it stays as it was) and uses it from there.</summary>
    private void Pick(string kind)
    {
        var word = kind == Made.Backup;
        var picked = Choose(word, kind switch { Made.Comparison => "Your cost comparison workbook", Made.Boq => "The edited BOQ (one berth's)", _ => "The edited back-up calculation" });
        if (picked is null) return;
        try
        {
            var folder = Path.Combine(ProjectFolder(_project), "templates");
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, kind + " - " + Path.GetFileName(picked));
            File.Copy(picked, copy, overwrite: true);
            File.SetAttributes(copy, FileAttributes.Normal);
            switch (kind)
            {
                case Made.Comparison:
                    using (var book = XlsxBook.Read(copy)) BerthCostExports.Layout.Of(book);
                    _p.ComparisonTemplate = copy;
                    break;
                case Made.Boq: _p.BoqTemplate = copy; break;
                default: _p.BackupTemplate = copy; break;
            }
            Changed();
            _status.Text = $"This project's {(kind == Made.Boq ? "BOQ" : kind == Made.Backup ? "back-up calculation" : "costing")} is now made in {Path.GetFileName(picked)}. Templates > back to the built-in undoes it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            _status.Text = "Couldn't use it: " + ex.Message;
        }
    }

    private void Base(string kind)
    {
        switch (kind)
        {
            case Made.Comparison: _p.ComparisonTemplate = ""; break;
            case Made.Boq: _p.BoqTemplate = ""; break;
            default: _p.BackupTemplate = ""; break;
        }
        Changed();
        _status.Text = "Back to the built-in format (the project's copy stays in its templates folder).";
    }

    private void ImportComparison(string path)
    {
        try
        {
            var said = BerthCostExports.Import(_p, path, Berths());
            Changed();
            _status.Text = "Took " + said + " from the comparison workbook.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException) { _status.Text = "Couldn't read the comparison workbook: " + ex.Message; }
    }

    // ---- Measuring --------------------------------------------------------------------------------------------------

    /// <param name="quiet">Following the model: all the open model's berths, nothing said when no model is open, and a
    /// box being typed in keeps its focus.</param>
    private async Task Measure(bool quiet = false)
    {
        if (_reading || _project.Length == 0) return;
        _reading = true;
        _stop = false;
        Brand.SetBusy(_measure, true, quiet ? "Updating…" : "Measuring…");
        _runStrip.Visibility = Visibility.Visible;
        Brand.SetBusy(_stopButton, false);
        try
        {
            _runText.Text = "Finding the open model…";
            _runBar.Value = 0;
            var setup = _setup;
            var all = Berths();
            var plan = await App.Dispatcher.InvokeAsync<(BerthCostFacts.Reading Reading, List<Berth> Berths, string Model)?>(app =>
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc is null || doc.IsFamilyDocument) return null;
                _file = DocName(doc);
                var site = setup is not null && BerthStatusMath.ModelFor(setup, _file) is { } m ? m.Name : "";
                // This model's berths: the ticked ones of its site (or every one of its site); with no setup, every berth.
                var mine = all.Where(b => site.Length == 0 || Same(b.Site, site)).ToList();
                var ticked = quiet ? new List<Berth>() : mine.Where(b => _chosen.Contains(b.Key)).ToList();
                var list = ticked.Count > 0 ? ticked : mine;
                return list.Count == 0 ? null : (Reading: BerthCostFacts.Plan(doc, _p, list), Berths: list, Model: doc.Title);
            });
            if (plan is null)
            {
                if (quiet) return;
                _status.Text = "No berth of the open model to measure: open the model (the berth status setup says which file is which site), or add a berth.";
                return;
            }
            var (reading, berths, model) = plan.Value;
            var clock = Stopwatch.StartNew();
            while (!_closed && !_stop && await App.Dispatcher.InvokeAsync(_ => reading.RunFor(StepBudget)))
            {
                var done = reading.Total - reading.Left;
                _runBar.Value = 100.0 * done / Math.Max(1, reading.Total);
                _runText.Text = $"{(quiet ? "The model changed, updating" : "Measuring")} {model}: {reading.NextName} ({100 * done / Math.Max(1, reading.Total)}%). Revit stays usable; Stop ends it.";
            }
            if (_closed) return;
            if (_stop) { _status.Text = "Stopped. The quantities are as they were."; return; }
            BerthCostFacts.Apply(_p, reading);
            Save();
            if (!quiet) ActivityLog.Note("berth_costing_measure", $"{_project}: {string.Join(", ", berths.Select(b => b.Title))} in {model}, {clock.Elapsed.TotalSeconds:0.0} s");
            // A box being typed in keeps its focus: only the berths on the left change then.
            Refresh(detail: !(quiet && Keyboard.FocusedElement is TextBox typing && typing.IsDescendantOf(_detail)));
            _status.Text = $"{(quiet ? "Updated from the model" : $"Measured {berths.Count} berths")} at {DateTime.Now:HH:mm:ss}." + (reading.Problems.Count > 0 ? " " + string.Join(" ", reading.Problems.Take(4)) : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = quiet ? $"Couldn't update from the model just now ({ex.Message}); it tries again after the next change."
                : $"Couldn't read the model ({ex.Message}). Finish what Revit is doing (an edit mode or a dialog), then try again.";
        }
        finally
        {
            _reading = false;
            Brand.SetBusy(_measure, false);
            Brand.SetBusy(_stopButton, false);
            _runStrip.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ShowInRevit(List<long> ids)
    {
        try
        {
            var shown = await App.Dispatcher.InvokeAsync(app =>
            {
                var ui = app.ActiveUIDocument;
                if (ui is null) return 0;
                var list = ids.Select(i => new Autodesk.Revit.DB.ElementId(i)).Where(id => ui.Document.GetElement(id) is not null).ToList();
                ui.Selection.SetElementIds(list);
                if (list.Count > 0) ui.ShowElements(list);
                return list.Count;
            });
            _status.Text = shown > 0 ? $"Selected {shown} elements in Revit." : "Those elements aren't in the open model.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = "Couldn't select them (" + ex.Message + ")."; }
    }

    // ---- Making the files ---------------------------------------------------------------------------------------------

    private string OutputFolder()
    {
        var folder = _p.OutputFolder.Length > 0 ? _p.OutputFolder : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Berth costing", ChecklistProjects.CleanName(_project));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The ticked berths, or the one picked when none is ticked.</summary>
    private List<Berth> Chosen()
    {
        var berths = Berths().Where(b => _chosen.Contains(b.Key)).ToList();
        if (berths.Count == 0 && Selected is { } one) berths.Add(one);
        return berths;
    }

    private void Export(params string[] kinds)
    {
        var berths = Chosen();
        if (berths.Count == 0) { _status.Text = "Tick the berths to make the files for (or pick one)."; return; }
        var notes = new List<string>();
        var made = new List<(string Kind, string File, List<Berth> Berths)>();
        var now = DateTime.Now;
        var stamp = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var names = string.Join(", ", berths.Select(b => b.Number.Length > 0 ? b.Number : b.Name));
        try
        {
            // The built-in formats, or the project's own template where one was imported.
            foreach (var kind in kinds)
                switch (kind)
                {
                    case Made.Comparison:
                        if (File.Exists(_p.ComparisonTemplate))
                        {
                            var c = OfficeDocs.FreePath(OutputFolder(), OfficeDocs.SafeName($"{Path.GetFileNameWithoutExtension(Strip(_p.ComparisonTemplate))} {stamp}"), ".xlsx");
                            notes.AddRange(BerthCostExports.WriteComparison(_p, berths, _p.ComparisonTemplate, c, b => $"Sketch ProMax model take-off, {b.Measured?.Model ?? "by hand"} {now:dd-MMM-yyyy}"));
                            made.Add((kind, c, berths));
                        }
                        else
                        {
                            var c = OfficeDocs.FreePath(OutputFolder(), OfficeDocs.SafeName($"Costing Berth {names} {stamp}"), ".xlsx");
                            BerthCostDocs.WriteCosting(_p, berths, c);
                            made.Add((kind, c, berths));
                        }
                        break;
                    case Made.Boq:
                        foreach (var b in berths)
                        {
                            var own = File.Exists(_p.BoqTemplate);
                            var path = OfficeDocs.FreePath(OutputFolder(), OfficeDocs.SafeName(own ? BerthCostExports.BoqName(Strip(_p.BoqTemplate), b) : BerthCostDocs.BoqName(_p, b)), ".xlsx");
                            notes.AddRange(own ? BerthCostExports.WriteBoq(_p, b, _p.BoqTemplate, path) : BerthCostDocs.WriteBoq(_p, b, path));
                            made.Add((kind, path, [b]));
                        }
                        break;
                    case Made.Backup:
                        var w = OfficeDocs.FreePath(OutputFolder(), OfficeDocs.SafeName($"Back-up calculation Berth {names}"), ".docx");
                        notes.AddRange(File.Exists(_p.BackupTemplate) ? BerthCostExports.WriteBackup(_p, berths, _p.BackupTemplate, w) : BerthCostDocs.WriteBackup(_p, berths, w));
                        made.Add((kind, w, berths));
                        break;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            _status.Text = "Couldn't make it: " + ex.Message + (ex is IOException ? " Close the file in Excel or Word and try again." : "");
            return;
        }
        // Each file marks its deliverable done for its berths in the berth status.
        var marked = new List<string>();
        if (_setup is not null && made.Count > 0)
        {
            var state = BerthStatusMath.LoadState(_project);
            foreach (var (kind, file, list) in made)
                if (MarkDelivered(_setup, state, list, kind, file, now) is { } d && !marked.Contains(d)) marked.Add(d);
            try { BerthStatusMath.SaveState(_project, state); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { marked.Clear(); notes.Add("Couldn't update the berth status: " + ex.Message); }
        }
        foreach (var (kind, _, list) in made) foreach (var b in list) b.Made[kind] = now;
        Save();
        WriteNotes(notes, now);
        ActivityLog.Note("berth_costing_export", $"{_project}: {string.Join(", ", made.Select(m => Path.GetFileName(m.File)))}");
        Refresh();
        _status.Text = (made.Count > 0 ? $"Made {made.Count} file{(made.Count == 1 ? "" : "s")} for berth {names}" : "Nothing made") +
            (marked.Count > 0 ? $"; marked {string.Join(", ", marked)} done in the berth status" : "") +
            (notes.Count > 0 ? $". {notes.Count} notes to check in the notes file." : ".");
        if (made.Count > 0) Shell("explorer.exe", $"/select,\"{made[0].File}\"");
    }

    /// <summary>The template's own name, without the kind added when it was copied in.</summary>
    private static string Strip(string path)
    {
        var name = Path.GetFileName(path);
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 && name.Substring(0, dash) is "comparison" or "boq" or "backup" ? name.Substring(dash + 3) : name;
    }

    private void WriteNotes(List<string> notes, DateTime now)
    {
        if (notes.Count == 0) return;
        try { File.WriteAllLines(Path.Combine(OutputFolder(), $"Notes {now:yyyy-MM-dd HHmm}.txt"), notes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Changed()
    {
        Save();
        Refresh();
    }

    private void Shell(string file, string? args = null)
    {
        try { Process.Start(args is null ? new ProcessStartInfo(file) { UseShellExecute = true } : new ProcessStartInfo(file, args) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { _status.Text = $"Couldn't open {file} ({ex.Message})."; }
    }

    // ---- Small parts ----------------------------------------------------------------------------------------------

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void Put(Grid grid, UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); grid.Children.Add(e); }

    private static FrameworkElement Right(TextBlock t) { t.HorizontalAlignment = HorizontalAlignment.Right; t.Margin = new Thickness(0, 0, 8, 0); return t; }

    private static TextBox Field(string text, string name)
    {
        var box = new TextBox { Text = text, Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, name);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Keyboard.ClearFocus(); } };
        return box;
    }

    private static TextBlock Heading(string text)
    {
        var t = Brand.Text(text, size: 18, bold: true);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    private static TextBlock Section(string text)
    {
        var t = Brand.Text(text, size: 14, bold: true);
        t.Margin = new Thickness(0, 18, 0, 2);
        return t;
    }

    private static TextBlock Para(string text, string key = Brand.K.Text, double top = 4)
    {
        var t = Brand.Text(text, key, 12.5);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, top, 0, 0);
        return t;
    }

    private static TextBlock Small(string text)
    {
        var t = Brand.Text(text, Brand.K.Muted, 11.5);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 2, 0, 0);
        return t;
    }

    private static Border Box(UIElement child, double top = 12)
    {
        var b = new Border { Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, top, 0, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        b.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        return b;
    }
}
