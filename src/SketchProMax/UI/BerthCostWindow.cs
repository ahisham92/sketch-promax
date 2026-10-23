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
/// berth excludes (Berth 9's RoRo part). The chosen berths go out as the cost comparison workbook, a BOQ Excel each and
/// one Word back-up calculation, each made from the user's own file so it has its format, and each file marks its
/// deliverable done in the berth status. Reads the model only.
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
    private string _page = "berth"; // berth, rates, items
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
        var window = new BerthCostWindow(project) { _file = file };
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

    private BerthCostWindow(string project)
    {
        _project = project;
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
        Closed += (_, _) => { _closed = true; _stop = true; Save(); };
        Deactivated += (_, _) => { if (!_reading) Save(); };
        // The chat can change rates and quantities too (berth_costing): coming back shows them.
        Activated += (_, _) =>
        {
            if (_reading || _project.Length == 0) return;
            if (SavedText(_project) is { Length: > 0 } text && text != _savedText) { Load(); Refresh(); }
        };
        Load();
        Build();
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

    private Berth? Selected => _p.Berths.TryGetValue(_selected, out var b) ? b : null;

    // ---- Layout ---------------------------------------------------------------------------------------------------

    private void Build()
    {
        foreach (var reused in new FrameworkElement[] { _status, _list, _detail, _runStrip, _runText, _runBar, _measure, _stopButton, _subtitle })
            switch (reused.Parent)
            {
                case Panel p: p.Children.Remove(reused); break;
                case Decorator d: d.Child = null; break;
                case ContentControl c: c.Content = null; break;
            }
        Content = null;

        var band = Brand.Header("Berth costing", null);
        if (band.Child is DockPanel row && row.Children.OfType<StackPanel>().LastOrDefault() is { } texts) texts.Children.Add(_subtitle);
        band.Padding = new Thickness(16, 12, 520, 12);
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
        Top(RichUi.Icon.Settings, "Items", "How each item is found in the model and in the BOQ, and its back-up section.", () => { _page = "items"; ShowDetail(); });
        var files = Top(RichUi.Icon.Folder, "Your files", "Pick the cost comparison workbook, the BOQ and the back-up calculation the exports are made from.", () => { });
        files.Click += (_, _) => FilesMenu(files);
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
        AutomationProperties.SetHelpText(all, "Makes the cost comparison, a BOQ per berth and the back-up calculation for the ticked berths, and marks them done in the berth status.");
        all.Click += (_, _) => Export(Made.Comparison, Made.Boq, Made.Backup);
        var comparison = RichUi.Button(RichUi.Icon.Export, "Cost comparison");
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

    private void Refresh()
    {
        var berths = Berths();
        _subtitle.Text = _project.Length == 0 ? "Open a model of the project first"
            : $"{_project} · {berths.Count} berths" + (OpenSite.Length > 0 ? $" · {OpenSite} model open" : "") + $" · {_p.Stage}";
        if (_selected.Length == 0 || !_p.Berths.ContainsKey(_selected)) _selected = berths.FirstOrDefault(b => OpenSite.Length == 0 || Same(b.Site, OpenSite))?.Key ?? "";
        _list.Children.Clear();
        if (berths.Count == 0)
        {
            _list.Children.Add(Heading("No berths yet"));
            _list.Children.Add(Para(_setup is null
                ? "Set up the berth status (Berth status > Setup) and its berths come here; or add a berth below by the parameter that names it."
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
        sums.Children.Add(Brand.Text("All berths", size: 14, bold: true));
        sums.Children.Add(Small(costed.Count == 0 ? "Nothing measured yet: press Measure the model with a model open." :
            $"{costed.Count} of {berths.Count} costed · {BerthCostMath.Money(structural, _p.Currency)}" + (length > 0 ? $" · {BerthCostMath.Money(structural / length, _p.Currency)} / lm" : "")));
        sums.Children.Add(Small($"Tick berths to make their files; {_chosen.Count} ticked."));
        top.Children.Add(sums);
        _list.Children.Add(top);

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
        ShowDetail();
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
        texts.Children.Add(Small(done is null ? "not measured" : BerthCostMath.Money(t.Structural, _p.Currency)));
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
        page.Children.Add(Small("Type a quantity to set it by hand (empty: the model's). Worked-out items follow the concrete and the kg/m³. Show selects the elements in Revit."));
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
        foreach (var w in new[] { 0.35, 2.6, 1.0, 0.5, 0.8, 1.0, 1.1, 0.6 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = ["#", "ITEM", "QUANTITY", "UNIT", "FROM", "RATE", "COST", ""];
        for (var i = 0; i < heads.Length; i++) Put(grid, Brand.Text(heads[i], Brand.K.Muted, 10.5, bold: true), 0, i);
        var row = 1;
        foreach (var line in Costs(_p, b))
        {
            var item = line.Item;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(grid, Brand.Text(item.No.ToString(CultureInfo.InvariantCulture), Brand.K.Muted, 12), row, 0);
            Put(grid, Brand.Text(item.Name, size: 12), row, 1);
            if (item.No is InfillRebar or SuperRebar && line.From != Source.Hand)
                Put(grid, Brand.Text(Qty(line.Qty), size: 12), row, 2);
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
                Put(grid, box, row, 2);
            }
            Put(grid, Brand.Text(item.Unit, Brand.K.Muted, 12), row, 3);
            Put(grid, Brand.Text(line.From switch { Source.Model => "model", Source.Hand => "by hand", Source.Worked => "worked out", _ => "–" },
                line.From == Source.Hand ? Brand.K.WarningText : Brand.K.Muted, 11.5), row, 4);
            Put(grid, Right(Brand.Text(line.Rate > 0 ? line.Rate.ToString("#,0.##", CultureInfo.InvariantCulture) : "–", size: 12)), row, 5);
            Put(grid, Right(Brand.Text(line.Cost > 0 ? Math.Round(line.Cost).ToString("#,0", CultureInfo.InvariantCulture) : "–", size: 12)), row, 6);
            if (b.Measured?.Ids.TryGetValue(item.No, out var ids) == true && ids.Count > 0)
            {
                var show = new Button { Content = "Show", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 1, 0, 1), ToolTip = $"Select the {ids.Count} elements in Revit" };
                show.Click += async (_, _) => await ShowInRevit(ids);
                Put(grid, show, row, 7);
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
        page.Children.Add(Para("Change any rate at any time; every berth's cost follows. They come from your cost comparison workbook (Your files > Take the unit costs from it) and go back into it when you make it.", Brand.K.Muted));
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
        foreach (var w in new[] { 2.6, 0.6, 1.0, 1.1 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = ["ITEM", "UNIT", $"RATE ({_p.Currency})", "CODE"];
        for (var i = 0; i < heads.Length; i++) Put(grid, Brand.Text(heads[i], Brand.K.Muted, 10.5, bold: true), 0, i);
        var row = 1;
        foreach (var rate in _p.Rates)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(grid, Brand.Text(rate.Name, size: 12), row, 0);
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
            Put(grid, Brand.Text(rate.Code, Brand.K.Muted, 11), row, 3);
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
            "OR between two ways. BOQ row: words in its description (; all of them, | choices, ^ starts with, = the whole text).", Brand.K.Muted));
        var reset = RichUi.Button(RichUi.Icon.Reset, "Back to the standard items");
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 8, 0, 4);
        reset.Click += (_, _) => { _p.Items = DefaultItems(); Changed(); _status.Text = "Items are back to the standard ones. Measure again to use them."; };
        page.Children.Add(reset);
        foreach (var item in _p.Items)
        {
            var box = new StackPanel();
            box.Children.Add(Brand.Text($"{item.No}. {item.Name}  ·  {item.Unit}  ·  {item.Group}  ·  rate {item.RateCode}", size: 12.5, bold: true));
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
            Line("BOQ row", item.Boq, v => item.Boq = v);
            Line("Back-up", item.Backup, v => item.Backup = v);
            page.Children.Add(Box(box, top: 8));
        }
        return page;
    }

    // ---- Your files ---------------------------------------------------------------------------------------------------

    private void FilesMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string text, Action go, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => go();
            menu.Items.Add(mi);
        }
        string Named(string path) => path.Length > 0 && File.Exists(path) ? " (" + Path.GetFileName(path) + ")" : " (not picked)";
        Item("Cost comparison workbook…" + Named(_p.ComparisonTemplate), () => Pick("comparison"), _project.Length > 0);
        Item("BOQ of a berth…" + Named(_p.BoqTemplate), () => Pick("boq"), _project.Length > 0);
        Item("Back-up calculation (Word)…" + Named(_p.BackupTemplate), () => Pick("backup"), _project.Length > 0);
        menu.Items.Add(new Separator());
        Item("Take the unit costs and berth data from the comparison workbook", () => ImportComparison(_p.ComparisonTemplate), File.Exists(_p.ComparisonTemplate));
        Item("Open the costing folder", () => Shell(OutputFolder()), _project.Length > 0);
        menu.IsOpen = true;
    }

    /// <summary>Copies the picked file into the project's costing folder (so it stays as it was) and uses it from there.</summary>
    private void Pick(string kind)
    {
        var word = kind == "backup";
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = kind switch { "comparison" => "Your cost comparison workbook", "boq" => "The BOQ Excel of one berth", _ => "The back-up calculation Word file" },
            Filter = word ? "Word document (*.docx)|*.docx" : "Excel workbook (*.xlsx)|*.xlsx",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var folder = Path.Combine(ProjectFolder(_project), "templates");
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, kind + " - " + Path.GetFileName(dialog.FileName));
            File.Copy(dialog.FileName, copy, overwrite: true);
            File.SetAttributes(copy, FileAttributes.Normal);
            switch (kind)
            {
                case "comparison":
                    using (var book = XlsxBook.Read(copy)) BerthCostExports.Layout.Of(book);
                    _p.ComparisonTemplate = copy;
                    ImportComparison(copy);
                    return;
                case "boq": _p.BoqTemplate = copy; break;
                default: _p.BackupTemplate = copy; break;
            }
            Changed();
            _status.Text = $"Using {Path.GetFileName(dialog.FileName)} for the {(kind == "boq" ? "BOQ" : "back-up calculation")}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            _status.Text = "Couldn't use it: " + ex.Message;
        }
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

    private async Task Measure()
    {
        if (_reading || _project.Length == 0) return;
        _reading = true;
        _stop = false;
        Brand.SetBusy(_measure, true, "Measuring…");
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
                var ticked = mine.Where(b => _chosen.Contains(b.Key)).ToList();
                var list = ticked.Count > 0 ? ticked : mine;
                return list.Count == 0 ? null : (Reading: BerthCostFacts.Plan(doc, _p, list), Berths: list, Model: doc.Title);
            });
            if (plan is null)
            {
                _status.Text = "No berth of the open model to measure: open the model (the berth status setup says which file is which site), or add a berth.";
                return;
            }
            var (reading, berths, model) = plan.Value;
            var clock = Stopwatch.StartNew();
            while (!_closed && !_stop && await App.Dispatcher.InvokeAsync(_ => reading.RunFor(StepBudget)))
            {
                var done = reading.Total - reading.Left;
                _runBar.Value = 100.0 * done / Math.Max(1, reading.Total);
                _runText.Text = $"Measuring {model}: {reading.NextName} ({100 * done / Math.Max(1, reading.Total)}%). Revit stays usable; Stop ends it.";
            }
            if (_closed) return;
            if (_stop) { _status.Text = "Stopped. The quantities are as they were."; return; }
            BerthCostFacts.Apply(_p, reading);
            Save();
            ActivityLog.Note("berth_costing_measure", $"{_project}: {string.Join(", ", berths.Select(b => b.Title))} in {model}, {clock.Elapsed.TotalSeconds:0.0} s");
            Refresh();
            _status.Text = $"Measured {berths.Count} berths at {DateTime.Now:HH:mm}." + (reading.Problems.Count > 0 ? " " + string.Join(" ", reading.Problems.Take(4)) : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = $"Couldn't read the model ({ex.Message}). Finish what Revit is doing (an edit mode or a dialog), then try again.";
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

    private void Export(params string[] kinds)
    {
        var berths = Berths().Where(b => _chosen.Contains(b.Key)).ToList();
        if (berths.Count == 0 && Selected is { } one) berths.Add(one);
        if (berths.Count == 0) { _status.Text = "Tick the berths to make the files for (or pick one)."; return; }
        var said = new List<string>();
        var notes = new List<string>();
        var made = new List<(string Kind, string File, List<Berth> Berths)>();
        var now = DateTime.Now;
        var stamp = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var names = string.Join(", ", berths.Select(b => b.Number.Length > 0 ? b.Number : b.Name));
        try
        {
            foreach (var kind in kinds)
                switch (kind)
                {
                    case Made.Comparison:
                        if (!File.Exists(_p.ComparisonTemplate)) { said.Add("no cost comparison workbook picked (Your files)"); break; }
                        var c = OfficeDocs.FreePath(OutputFolder(), OfficeDocs.SafeName($"{Path.GetFileNameWithoutExtension(Strip(_p.ComparisonTemplate))} {stamp}"), ".xlsx");
                        notes.AddRange(BerthCostExports.WriteComparison(_p, berths, _p.ComparisonTemplate, c, b => $"Sketch ProMax model take-off, {b.Measured?.Model ?? "by hand"} {now:dd-MMM-yyyy}"));
                        made.Add((kind, c, berths));
                        break;
                    case Made.Boq:
                        if (!File.Exists(_p.BoqTemplate)) { said.Add("no BOQ picked (Your files)"); break; }
                        foreach (var b in berths)
                        {
                            var path = OfficeDocs.FreePath(OutputFolder(), OfficeDocs.SafeName(BerthCostExports.BoqName(Strip(_p.BoqTemplate), b)), ".xlsx");
                            notes.AddRange(BerthCostExports.WriteBoq(_p, b, _p.BoqTemplate, path));
                            made.Add((kind, path, [b]));
                        }
                        break;
                    case Made.Backup:
                        if (!File.Exists(_p.BackupTemplate)) { said.Add("no back-up calculation picked (Your files)"); break; }
                        var w = OfficeDocs.FreePath(OutputFolder(), OfficeDocs.SafeName($"Back-up calculation Berth {names}"), ".docx");
                        notes.AddRange(BerthCostExports.WriteBackup(_p, berths, _p.BackupTemplate, w));
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
            (said.Count > 0 ? "; " + string.Join("; ", said) : "") + (notes.Count > 0 ? $". {notes.Count} notes to check in the notes file." : ".");
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
