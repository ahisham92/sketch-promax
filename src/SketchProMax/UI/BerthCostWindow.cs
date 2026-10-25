using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// Berth costing (Berth costing button, next to Berth status): the project's figures across the top (total, average,
/// highest and lowest US$/lm, how complete), then either every berth as a card (a ring for how complete its costing is,
/// its cost, US$/lm, a bar of its cost groups) or every element across the berths as a table; click a berth for its
/// costs by group and element, or an element for its cost and quantity per berth. Quantities come from the model; ✎
/// types one by hand. The unit costs and the item rules are editable at any time. Measure reads the open model per
/// berth in short steps (BerthCostFacts), leaving out what a berth excludes (Berth 9's RoRo part, which gets a card of
/// its own under it that counts or not); while the window is open it measures again by itself a few seconds after the
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
    private string _page = "berth"; // berth (the berth or element view), rates, items, details
    private bool _reading, _stop, _closed;
    private string _savedText = "";

    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted);
    private readonly StackPanel _list = new() { Margin = new Thickness(14, 12, 4, 14) };
    private readonly ScrollViewer _listScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly UniformGrid _kpis = new() { Rows = 1 };
    private readonly DockPanel _bar = new() { LastChildFill = true };
    private string _view = "berth"; // berth, element
    private int _item = -1;
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

    /// <summary>The colour of each cost group (C1-C10) in the bars, the legend and the element table.</summary>
    private static readonly SolidColorBrush[] GroupColors = new[]
    {
        (0x3B, 0x6F, 0xD8), (0x7F, 0xA3, 0xEE), (0x1F, 0x9E, 0x8F), (0x7B, 0x61, 0xE8), (0xB0, 0x72, 0xD6),
        (0xE0, 0x8A, 0x2E), (0xC9, 0xA2, 0x27), (0x4B, 0xA3, 0xD9), (0x8A, 0x94, 0xA6), (0x9C, 0x6B, 0x4E),
    }.Select(c => { var b = new SolidColorBrush(Color.FromRgb((byte)c.Item1, (byte)c.Item2, (byte)c.Item3)); b.Freeze(); return b; }).ToArray();

    private static SolidColorBrush GroupColor(string code) =>
        GroupColors[Math.Max(0, Array.FindIndex(Groups, g => g.Code == code)) % GroupColors.Length];

    /// <summary>The small flat buttons (✎, ▸ BOQ description): no frame until hovered or focused.</summary>
    private const string LinkStyle = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="SpmCostLink" TargetType="Button">
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Foreground" Value="{DynamicResource SpmLink}"/>
            <Setter Property="Padding" Value="3,0,3,0"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="b" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="3" Padding="{TemplateBinding Padding}">
                  <ContentPresenter VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """;

    private void Build()
    {
        foreach (var reused in new FrameworkElement[] { _status, _list, _detail, _runStrip, _runText, _runBar, _measure, _stopButton, _subtitle, _live, _kpis, _bar })
            switch (reused.Parent)
            {
                case Panel p: p.Children.Remove(reused); break;
                case Decorator d: d.Child = null; break;
                case ContentControl c: c.Content = null; break;
            }
        Content = null;
        try { Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(LinkStyle)); }
        catch (System.Windows.Markup.XamlParseException) { /* the small buttons fall back to plain ones */ }

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
        _live.SetResourceReference(Control.ForegroundProperty, Brand.K.HeaderText);
        _live.IsEnabled = _project.Length > 0;
        right.Children.Add(_live);
        Top(RichUi.Icon.Edit, "Unit costs", "Change the unit costs and the pile rate basis; every berth's cost follows.", () => { _page = "rates"; ShowDetail(); });
        Top(RichUi.Icon.Settings, "Items", "Which items go in the files, how each is found in the model, its BOQ description and back-up section.", () => { _page = "items"; ShowDetail(); });
        Top(RichUi.Icon.Edit, "Project details", "The header lines, title and document number printed on the BOQ, typed once for the project.", () => { _page = "details"; ShowDetail(); });
        var files = Top(RichUi.Icon.Folder, "Templates", "Export a built-in format to edit in Excel or Word and import it back as this project's own template.", () => { });
        files.Click += (_, _) => TemplatesMenu(files);
        _measure.VerticalAlignment = VerticalAlignment.Center;
        _measure.IsEnabled = _project.Length > 0;
        right.Children.Add(_measure);
        header.Children.Add(right);

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

        // The overview figures, then the view switch and the group colours.
        var kpiBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = _kpis };
        kpiBorder.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        kpiBorder.SetResourceReference(Border.BackgroundProperty, Brand.K.Bg);
        var barBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = _bar, Padding = new Thickness(18, 8, 18, 8) };
        barBorder.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        barBorder.SetResourceReference(Border.BackgroundProperty, Brand.K.Bg);
        var top = new StackPanel { Children = { header, _runStrip, kpiBorder, barBorder } };
        DockPanel.SetDock(top, Dock.Top);

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
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star), MinWidth = 420 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 440 });
        _listScroll.Content = _list;
        // The cards fill the width: as many columns as fit.
        _listScroll.SizeChanged += (_, e) => { if (e.WidthChanged && Columns() != _columns) Refresh(detail: false); };
        var listBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = _listScroll };
        listBorder.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        listBorder.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        body.Children.Add(listBorder);
        Grid.SetColumn(_detail, 1);
        body.Children.Add(_detail);

        var root = new DockPanel();
        root.Children.Add(top);
        root.Children.Add(footBorder);
        root.Children.Add(body);
        Content = root;
        Refresh();
    }

    private int _columns;

    private int Columns() => Math.Max(1, (int)((_listScroll.ActualWidth > 0 ? _listScroll.ActualWidth : Width * 0.55) - 30) / 250);

    // ---- The overview ---------------------------------------------------------------------------------------------

    /// <summary>The berths that count in the totals: the shown ones, without a left-out part that is switched off.</summary>
    private List<Berth> Counting() => Shown().Where(b => b.Counts).ToList();

    private void Refresh(bool detail = true)
    {
        var every = Berths();
        var berths = Shown();
        var whole = berths.Count(b => b.PartOf.Length == 0);
        _subtitle.Text = _project.Length == 0 ? "Open a model of the project first"
            : $"{_project} · " + (OpenSite.Length > 0 && !_allSites ? $"{OpenSite} model open · {whole} berths" : $"{whole} berths") + $" · {_p.Stage}";
        if (_selected.Length == 0 || !berths.Any(b => b.Key == _selected)) _selected = berths.FirstOrDefault(b => b.PartOf.Length == 0)?.Key ?? "";
        Kpis();
        Bar(every);
        _list.Children.Clear();
        _columns = Columns();
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
        if (_view == "element")
        {
            _listScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _list.Children.Add(ElementTable());
        }
        else
        {
            _listScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            foreach (var site in berths.GroupBy(b => b.Site.Length > 0 ? b.Site : "Project"))
            {
                if (_allSites || OpenSite.Length == 0)
                {
                    var title = Brand.Text(site.Key.ToUpperInvariant() + (Same(site.Key, OpenSite) ? " · OPEN" : ""), Brand.K.Muted, 11.5, bold: true);
                    title.Margin = new Thickness(2, 4, 0, 6);
                    _list.Children.Add(title);
                }
                var cards = new UniformGrid { Columns = _columns };
                foreach (var b in site) cards.Children.Add(BerthCard(b));
                _list.Children.Add(cards);
            }
            var add = new Button { Content = Brand.Text("+ Add a berth the berth status doesn't list", Brand.K.Link, 12), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            add.SetResourceReference(StyleProperty, "SpmCostLink");
            var box = AddBerthBox();
            box.Visibility = _adding ? Visibility.Visible : Visibility.Collapsed;
            add.Click += (_, _) => { _adding = !_adding; box.Visibility = _adding ? Visibility.Visible : Visibility.Collapsed; };
            _list.Children.Add(add);
            _list.Children.Add(box);
        }
        if (detail) ShowDetail();
    }

    private bool _adding;

    /// <summary>The figures across the top: the total, the average, highest and lowest US$/lm, how complete it is.</summary>
    private void Kpis()
    {
        _kpis.Children.Clear();
        var berths = Counting();
        var costed = berths.Where(b => b.Measured is not null || b.Hand.Count > 0).ToList();
        var noRates = _p.Rates.All(r => r.Value is null);
        var totals = costed.ToDictionary(b => b.Key, b => Total(_p, b));
        var structural = totals.Values.Sum(t => t.Structural);
        var withLength = costed.Where(b => b.Length is > 0 && totals[b.Key].Structural > 0).ToList();
        var length = withLength.Sum(b => b.Length!.Value);
        var perLm = withLength.Select(b => (Berth: b, PerLm: totals[b.Key].Structural / b.Length!.Value)).OrderByDescending(x => x.PerLm).ToList();
        var known = berths.Select(b => Completeness(_p, b)).OfType<double>().ToList();
        var noLength = berths.Where(b => b.Length is not > 0).Select(b => b.Title.Replace(" (" + b.ExcludeNote.Trim() + ")", "")).ToList();
        var whole = berths.Count(b => b.PartOf.Length == 0);
        var parts = berths.Where(b => b.PartOf.Length > 0).Select(b => b.Part).Distinct().ToList();
        string Left(Berth b) => b.ExcludeNote.Trim().Length > 0 ? $" ({b.ExcludeNote.Trim()})" : "";
        var cells = new List<(string Label, string Value, string Sub)>
        {
            ("STRUCTURAL WORKS", costed.Count == 0 ? "–" : noRates ? "no unit costs" : BerthCostMath.Money(structural, _p.Currency),
                costed.Count == 0 ? "nothing measured yet: Measure the model" : noRates ? "Unit costs, or Templates > Take the unit costs from a workbook"
                : $"{whole} berths" + (parts.Count > 0 ? " + " + string.Join(", ", parts) : "") + ", from the model"),
            ("AVERAGE " + _p.Currency + " / LM", length > 0 ? Math.Round(withLength.Sum(b => totals[b.Key].Structural) / length).ToString("#,0", CultureInfo.InvariantCulture) : "–",
                length > 0 ? $"{length.ToString("#,0", CultureInfo.InvariantCulture)} m of quay with a length" : "type the quay lengths on the cards"),
            ("HIGHEST " + _p.Currency + " / LM", perLm.Count > 0 ? Short(perLm[0].Berth) : "–",
                perLm.Count > 0 ? $"{_p.Currency} {Math.Round(perLm[0].PerLm).ToString("#,0", CultureInfo.InvariantCulture)} / lm{Left(perLm[0].Berth)}" : ""),
            ("LOWEST " + _p.Currency + " / LM", perLm.Count > 1 ? Short(perLm[perLm.Count - 1].Berth) : "–",
                perLm.Count > 1 ? $"{_p.Currency} {Math.Round(perLm[perLm.Count - 1].PerLm).ToString("#,0", CultureInfo.InvariantCulture)} / lm{Left(perLm[perLm.Count - 1].Berth)}" : ""),
            ("COMPLETE", known.Count > 0 ? Math.Round(known.Average()).ToString(CultureInfo.InvariantCulture) + "%" : "–",
                noLength.Count == 0 ? "every berth has a length" : Names(noLength) + (noLength.Count == 1 ? " needs a length" : " need lengths")),
        };
        _kpis.Columns = cells.Count;
        for (var i = 0; i < cells.Count; i++)
        {
            var (label, value, sub) = cells[i];
            var cell = new StackPanel();
            cell.Children.Add(Brand.Text(label, Brand.K.Muted, 10.5, bold: true));
            var v = Brand.Text(value, size: 21, bold: true);
            v.Margin = new Thickness(0, 2, 0, 0);
            cell.Children.Add(v);
            cell.Children.Add(Small(sub));
            var border = new Border { Child = cell, Padding = new Thickness(18, 10, 14, 10), BorderThickness = new Thickness(0, 0, i < cells.Count - 1 ? 1 : 0, 0) };
            border.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
            AutomationProperties.SetName(border, $"{label}: {value}. {sub}");
            _kpis.Children.Add(border);
        }
    }

    /// <summary>"Berth 09" for "Berth 09 (excl. RoRo)": the note goes in the line under it.</summary>
    private static string Short(Berth b) => b.ExcludeNote.Trim().Length > 0 ? b.Title.Replace(" (" + b.ExcludeNote.Trim() + ")", "") : b.Title;

    /// <summary>"Berth 01–04" for a run of berths, else the names with commas.</summary>
    private static string Names(List<string> names)
    {
        var numbers = names.Select(n => Regex.Match(n, @"^Berth (\d+)$") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1).ToList();
        if (names.Count > 2 && numbers.All(n => n >= 0) && numbers.Zip(numbers.Skip(1), (a, b) => b - a).All(d => d == 1))
            return $"Berths {numbers[0]:00}–{numbers[numbers.Count - 1]:00}";
        return string.Join(", ", names);
    }

    /// <summary>The view switch (by berth / by element), the group colours and how many berths are ticked.</summary>
    private void Bar(List<Berth> every)
    {
        _bar.Children.Clear();
        var seg = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
        foreach (var (key, text, help) in new[] { ("berth", "By berth", "Each berth as a card; click one for its costs by group and element."), ("element", "By element", "Each element across the berths; click one for its cost and quantity per berth.") })
        {
            var b = Brand.Button(text, _view == key ? Brand.Weight.Primary : Brand.Weight.Secondary);
            b.Margin = new Thickness(0, 0, 4, 0);
            b.MinWidth = 96;
            AutomationProperties.SetHelpText(b, help);
            AutomationProperties.SetItemStatus(b, _view == key ? "shown" : "");
            b.Click += (_, _) => { if (_view == key && _page == "berth") return; _view = key; _page = "berth"; Refresh(); };
            seg.Children.Add(b);
        }
        DockPanel.SetDock(seg, Dock.Left);
        _bar.Children.Add(seg);
        var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (OpenSite.Length > 0 && every.Any(b => !Same(b.Site, OpenSite)))
        {
            var all = new CheckBox { Content = "Other sites too", IsChecked = _allSites, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            AutomationProperties.SetHelpText(all, "Show the other sites' berths too (their figures are from their last reading).");
            all.Checked += (_, _) => { _allSites = true; Refresh(); };
            all.Unchecked += (_, _) => { _allSites = false; Refresh(); };
            side.Children.Add(all);
        }
        side.Children.Add(Brand.Text($"   {_chosen.Count} ticked for files", Brand.K.Muted, 11.5));
        DockPanel.SetDock(side, Dock.Right);
        _bar.Children.Add(side);
        var legend = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (code, name) in Groups.Where(g => g.Code != "C10"))
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 12, 2) };
            chip.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = GroupColor(code), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            chip.Children.Add(Brand.Text($"{code} {name}", Brand.K.Muted, 11));
            legend.Children.Add(chip);
        }
        _bar.Children.Add(legend);
    }

    // ---- By berth: the cards ----------------------------------------------------------------------------------------

    private FrameworkElement BerthCard(Berth b)
    {
        var done = Completeness(_p, b);
        var t = Total(_p, b);
        var noRates = _p.Rates.All(r => r.Value is null);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Put(grid, new PercentRing(done, 54, 6, 12.5) { VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        var name = Brand.Text(b.Title, size: 13, bold: true);
        name.TextWrapping = TextWrapping.Wrap;
        texts.Children.Add(name);
        texts.Children.Add(Brand.Text(done is null ? "not measured" : noRates ? "no unit costs" : BerthCostMath.Money(t.Structural, _p.Currency), size: 15, bold: true));
        // US$/lm, then the quay length with ✎ to type it (only the length is typed here: the rest comes from the model).
        var meta = new WrapPanel();
        if (t.PerLm > 0) meta.Children.Add(Brand.Text($"{Math.Round(t.PerLm).ToString("#,0", CultureInfo.InvariantCulture)} {_p.Currency}/lm · ", Brand.K.Muted, 11.5));
        meta.Children.Add(Typed(b.Length is { } l ? l.ToString("0.##", CultureInfo.InvariantCulture) + " m" : "type the length", b.Length is { } k ? k.ToString("0.##", CultureInfo.InvariantCulture) : "",
            b.Title + " quay length in m", text =>
            {
                if (text.Length == 0) b.Length = null;
                else if (Num(text) is > 0 and var v) b.Length = v;
                else { _status.Text = "Quay length: type the metres."; return false; }
                return true;
            }));
        texts.Children.Add(meta);
        if (b.PartOf.Length > 0)
        {
            var count = new CheckBox { Content = Brand.Text($"Count the {b.Part} part in the totals", Brand.K.Muted, 11.5), IsChecked = b.Counted, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetHelpText(count, $"Ticked: the {b.Part} part counts in the totals and the element table. It is measured either way.");
            count.Checked += (_, _) => { b.Counted = true; Changed(); };
            count.Unchecked += (_, _) => { b.Counted = false; Changed(); };
            texts.Children.Add(count);
        }
        if (b.Made.Count > 0) texts.Children.Add(Small("made: " + string.Join(", ", b.Made.Keys)));
        Put(grid, texts, 0, 1);
        var tick = new CheckBox { IsChecked = _chosen.Contains(b.Key), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Tick to make its files" };
        AutomationProperties.SetName(tick, "Make files for " + b.Title);
        tick.Checked += (_, _) => { _chosen.Add(b.Key); Bar(Berths()); };
        tick.Unchecked += (_, _) => { _chosen.Remove(b.Key); Bar(Berths()); };
        Put(grid, tick, 0, 2);
        var stack = Stack(t);
        stack.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetColumnSpan(stack, 3);
        Put(grid, stack, 1, 0);
        var on = b.Key == _selected && _page == "berth";
        var card = new Border
        {
            Padding = new Thickness(12, 10, 10, 10), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(on ? 2 : 1), Child = grid, Cursor = Cursors.Hand, Focusable = true,
            Opacity = b.Counts ? 1 : 0.6,
            ToolTip = $"{b.Title}: {(done is null ? "not measured" : Math.Round(done.Value) + "% complete")}. Click for its costs.",
        };
        card.SetResourceReference(Border.BackgroundProperty, on ? Brand.K.AccentSoft : Brand.K.Bg);
        card.SetResourceReference(Border.BorderBrushProperty, on ? Brand.K.Accent : Brand.K.Border);
        AutomationProperties.SetName(card, $"{b.Title}, {(done is null ? "not measured" : Math.Round(done.Value) + "%")}");
        void Pick() { _selected = b.Key; _page = "berth"; Refresh(); }
        card.MouseLeftButtonUp += (_, e) => { if (!Inside<ButtonBase>(e.OriginalSource) && !Inside<TextBox>(e.OriginalSource)) Pick(); };
        card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space && e.OriginalSource == card) { Pick(); e.Handled = true; } };
        return card;
    }

    private static bool Inside<T>(object source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }

    /// <summary>A thin bar split by the cost of each group.</summary>
    private static FrameworkElement Stack(Totals t)
    {
        var grid = new Grid { Height = 7 };
        var sum = t.ByGroup.Where(g => g.Key != "C10").Sum(g => g.Value);
        if (sum <= 0)
        {
            var empty = new Border { CornerRadius = new CornerRadius(3.5) };
            empty.SetResourceReference(Border.BackgroundProperty, Brand.K.BorderSubtle);
            grid.Children.Add(empty);
            return grid;
        }
        var col = 0;
        foreach (var (code, gname) in Groups.Where(g => g.Code != "C10"))
        {
            var v = t.ByGroup.TryGetValue(code, out var x) ? x : 0;
            if (v <= 0) continue;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(v, GridUnitType.Star) });
            var part = new Border { Background = GroupColor(code), ToolTip = $"{code} {gname}: {BerthCostMath.Money(v)}" };
            Grid.SetColumn(part, col++);
            grid.Children.Add(part);
        }
        grid.SizeChanged += (_, e) => grid.Clip = new RectangleGeometry(new Rect(e.NewSize), 3.5, 3.5);
        return grid;
    }

    /// <summary>
    /// A value shown as text with ✎ after it: clicking ✎ turns it into a box (Enter keeps it, Escape goes back, empty
    /// clears it). <paramref name="set"/> says whether the text was taken.
    /// </summary>
    private FrameworkElement Typed(string shown, string value, string name, Func<string, bool> set, string key = Brand.K.Muted, double size = 11.5)
    {
        var holder = new Border();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Brand.Text(shown, key, size));
        var pen = Pencil("Type " + name);
        row.Children.Add(pen);
        holder.Child = row;
        pen.Click += (_, e) =>
        {
            e.Handled = true;
            var box = Field(value, name);
            box.MinWidth = 70;
            box.FontSize = size;
            var finished = false;
            void Done(bool keep)
            {
                if (finished) return;
                finished = true;
                if (keep && box.Text.Trim() != value) { if (set(box.Text.Trim())) { Changed(); return; } }
                holder.Child = row;
            }
            box.PreviewKeyDown += (_, k) =>
            {
                if (k.Key == Key.Enter) { k.Handled = true; Done(true); }
                else if (k.Key == Key.Escape) { k.Handled = true; Done(false); }
            };
            box.LostKeyboardFocus += (_, _) => Done(true);
            holder.Child = box;
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        };
        return holder;
    }

    private static Button Pencil(string name)
    {
        var pen = new Button { Content = new TextBlock { Text = RichUi.Icon.Edit, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 }, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = name };
        pen.SetResourceReference(StyleProperty, "SpmCostLink");
        AutomationProperties.SetName(pen, name);
        return pen;
    }

    /// <summary>Adds a berth by hand: its site, the parameter that names it and its value.</summary>
    private FrameworkElement AddBerthBox()
    {
        var page = new StackPanel();
        page.Children.Add(Small("Its site (as on the comparison workbook), the parameter on its elements and the value."));
        var site = Field(OpenSite.Length > 0 ? OpenSite : _p.Berths.Values.Select(b => b.Site).FirstOrDefault() ?? "", "Site");
        var parameter = Field(_p.Berths.Values.Select(b => b.Parameter).FirstOrDefault(x => x.Length > 0) ?? "Berth Number", "Parameter");
        var value = Field("", "Value");
        var add = RichUi.Button(RichUi.Icon.Add, "Add");
        add.Click += (_, _) =>
        {
            if (value.Text.Trim().Length == 0) { _status.Text = "Give the value that names the berth (for example 11)."; return; }
            var b = AddBerth(_p, site.Text.Trim(), parameter.Text.Trim(), value.Text.Trim());
            _selected = b.Key;
            _adding = false;
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
        return Box(page, top: 6);
    }

    // ---- By element: the table --------------------------------------------------------------------------------------

    /// <summary>The items with something in a counted berth, and whether the table shows costs (else quantities).</summary>
    private (List<Item> Items, bool Costs) ElementRows(List<Berth> berths)
    {
        var lines = berths.ToDictionary(b => b.Key, b => Costs(_p, b));
        var costs = !_p.Rates.All(r => r.Value is null);
        var items = _p.Items.Where(i => i.Group != "C10" && berths.Any(b => lines[b.Key].First(l => l.Item.No == i.No) is var l && (costs ? l.Cost > 0 : l.Qty is > 0))).ToList();
        return (items, costs);
    }

    private FrameworkElement ElementTable()
    {
        var berths = Counting();
        var page = new StackPanel();
        var (items, costs) = ElementRows(berths);
        if (berths.Count == 0 || items.Count == 0)
        {
            page.Children.Add(Para(berths.Count == 0 ? "No berth counts in the totals." : "Nothing measured yet: press Measure the model with a model open.", Brand.K.Muted));
            return page;
        }
        var lines = berths.ToDictionary(b => b.Key, b => Costs(_p, b).ToDictionary(l => l.Item.No));
        double Value(Berth b, Item i) => lines[b.Key][i.No] is var l ? costs ? l.Cost : l.Qty ?? 0 : 0;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto), MinWidth = 170 });
        foreach (var _ in berths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 54 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 64 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Put(grid, Brand.Text("ELEMENT", Brand.K.Muted, 10.5, bold: true), 0, 0);
        for (var c = 0; c < berths.Count; c++)
        {
            var head = Brand.Text(Column(berths[c]), Brand.K.Muted, 10.5, bold: true);
            head.HorizontalAlignment = HorizontalAlignment.Center;
            head.ToolTip = berths[c].Title;
            Put(grid, head, 0, c + 1);
        }
        Put(grid, Right(Brand.Text("TOTAL", Brand.K.Muted, 10.5, bold: true)), 0, berths.Count + 1);
        var max = items.Max(i => berths.Max(b => Value(b, i)));
        var row = 1;
        foreach (var item in items)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var on = item.No == _item && _page == "berth";
            var back = new Border { CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Focusable = true, Margin = new Thickness(-6, 1, -6, 1) };
            back.SetResourceReference(Border.BackgroundProperty, on ? Brand.K.AccentSoft : Brand.K.Surface);
            Grid.SetColumnSpan(back, berths.Count + 2);
            AutomationProperties.SetName(back, item.Name);
            var no = item.No;
            void Pick() { _item = no; _page = "berth"; Refresh(); }
            back.MouseLeftButtonUp += (_, _) => Pick();
            back.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Pick(); e.Handled = true; } };
            Put(grid, back, row, 0);
            var label = Brand.Text(item.Name, size: 12, bold: on);
            label.Margin = new Thickness(0, 6, 10, 6);
            label.IsHitTestVisible = false;
            Put(grid, label, row, 0);
            var brush = GroupColor(item.Group);
            for (var c = 0; c < berths.Count; c++)
            {
                var v = Value(berths[c], item);
                var heat = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(3, 2, 3, 2), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
                var tint = brush.Clone();
                tint.Opacity = max > 0 ? 0.08 + 0.5 * v / max : 0;
                heat.Background = tint;
                var text = Brand.Text(v <= 0 ? "–" : costs ? (v / 1e6).ToString("0.00", CultureInfo.InvariantCulture) : Qty(v), size: 11.5);
                text.HorizontalAlignment = HorizontalAlignment.Center;
                heat.Child = text;
                Put(grid, heat, row, c + 1);
            }
            var sum = berths.Sum(b => Value(b, item));
            var total = Right(Brand.Text(costs ? (sum / 1e6).ToString("0.00", CultureInfo.InvariantCulture) : Qty(sum) + " " + item.Unit, size: 12, bold: true));
            total.IsHitTestVisible = false;
            Put(grid, total, row, berths.Count + 1);
            row++;
        }
        page.Children.Add(grid);
        var note = costs ? $"{_p.Currency} million per berth." : "Quantities from the model (no unit costs yet).";
        if (berths.FirstOrDefault(b => b.ExcludeNote.Trim().Length > 0) is { } left) note += $" * {Short(left)} {left.ExcludeNote.Trim()}" + (berths.Any(b => b.PartOf == left.Key) ? ", which has its own column." : ".");
        page.Children.Add(Small(note));
        return page;
    }

    /// <summary>A berth's short column head: B01, B09* (leaves a part out), B09 RoRo.</summary>
    private static string Column(Berth b) =>
        (b.Number.Length > 0 ? "B" + b.Number.PadLeft(2, '0') : b.Name) + (b.Part.Length > 0 ? " " + b.Part : b.ExcludeNote.Trim().Length > 0 ? "*" : "");

    // ---- The right side -----------------------------------------------------------------------------------------------

    private void ShowDetail()
    {
        _detail.Content = _page switch
        {
            "rates" => RatesPage(),
            "items" => ItemsPage(),
            "details" => DetailsPage(),
            _ when _view == "element" => ElementPage(),
            _ => Selected is { } b ? BerthPage(b) : Para("Pick a berth on the left.", Brand.K.Muted),
        };
    }

    /// <summary>One element across the berths: its cost per berth and its quantity per berth with the per-metre figure.</summary>
    private FrameworkElement ElementPage()
    {
        var page = new StackPanel();
        var berths = Counting();
        var (items, costs) = ElementRows(berths);
        if (_p.ItemOf(_item) is not { } item || !items.Contains(item)) item = items.FirstOrDefault()!;
        if (item is null) return Para("Pick an element on the left.", Brand.K.Muted);
        _item = item.No;
        var lines = berths.Select(b => (Berth: b, Line: Costs(_p, b).First(l => l.Item.No == item.No))).ToList();
        var group = Groups.FirstOrDefault(g => g.Code == item.Group);
        page.Children.Add(Heading(item.Name));
        var rate = lines.Select(l => l.Line.Rate).FirstOrDefault(r => r > 0);
        page.Children.Add(Para($"{group.Code} {group.Name}" + (rate > 0 ? $" · rate {rate.ToString("#,0.##", CultureInfo.InvariantCulture)} {_p.Currency}/{item.Unit}" : "") +
            (costs ? $" · all berths {BerthCostMath.Money(lines.Sum(l => l.Line.Cost), _p.Currency)}" : ""), Brand.K.Muted, 2));
        if (costs)
        {
            page.Children.Add(Section("Cost per berth"));
            page.Children.Add(Bars(lines.Select(l => (l.Berth.Title, l.Line.Cost, BerthCostMath.Money(l.Line.Cost, _p.Currency), GroupColor(item.Group))).ToList()));
        }
        page.Children.Add(Section("Quantity per berth"));
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var w in new[] { 1.6, 1.0, 0.8, 0.8 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = ["BERTH", "QUANTITY", "FROM", $"{item.Unit} / LM"];
        for (var i = 0; i < heads.Length; i++) Put(grid, i == 0 ? Brand.Text(heads[i], Brand.K.Muted, 10.5, bold: true) : Right(Brand.Text(heads[i], Brand.K.Muted, 10.5, bold: true)), 0, i);
        var row = 1;
        foreach (var (b, l) in lines)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Put(grid, Brand.Text(b.Title, size: 12), row, 0);
            Put(grid, Right(Brand.Text(Qty(l.Qty) + " " + item.Unit, size: 12)), row, 1);
            var badge = Badge(l.From);
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            Put(grid, badge, row, 2);
            Put(grid, Right(Brand.Text(b.Length is > 0 && l.Qty is { } q ? (q / b.Length.Value).ToString("#,0.00", CultureInfo.InvariantCulture) : "–", size: 12)), row, 3);
            row++;
        }
        page.Children.Add(grid);
        page.Children.Add(Small("Click a berth card (By berth) to type one of its quantities by hand."));
        return page;
    }

    private FrameworkElement BerthPage(Berth b)
    {
        var page = new StackPanel();
        var done = Completeness(_p, b);
        var t = Total(_p, b);
        var noRates = _p.Rates.All(r => r.Value is null);
        var head = new DockPanel();
        var ring = new PercentRing(done, 72, 8, 16) { VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(ring, Dock.Left);
        head.Children.Add(ring);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        texts.Children.Add(Heading(b.Title));
        texts.Children.Add(Brand.Text((noRates ? "no unit costs yet" : BerthCostMath.Money(t.Structural, _p.Currency)) +
            (t.PerLm > 0 ? $" · {Math.Round(t.PerLm).ToString("#,0", CultureInfo.InvariantCulture)} {_p.Currency}/lm" : b.Length is > 0 ? "" : " · type the quay length on its card for /lm"), size: 14.5, bold: true));
        texts.Children.Add(Small((b.Measured is { } m ? $"measured {m.At:dd-MMM HH:mm} in {m.Model}" : "not measured yet") +
            (done is { } d ? $" · {Math.Round(d)}% complete" : "") + (t.Prelims > 0 ? $" · preliminaries as billed {BerthCostMath.Money(t.Prelims, _p.Currency)}" : "") +
            (b.PartOf.Length > 0 && !b.Counted ? " · not counted in the totals" : "")));
        head.Children.Add(texts);
        page.Children.Add(head);

        if (!noRates && t.Structural > 0)
        {
            page.Children.Add(Section("Cost by group"));
            page.Children.Add(Bars(Groups.Where(g => t.ByGroup[g.Code] > 0).Select(g => ($"{g.Code} {g.Name}", t.ByGroup[g.Code], BerthCostMath.Money(t.ByGroup[g.Code], _p.Currency), GroupColor(g.Code))).ToList()));
        }

        page.Children.Add(Section("Elements"));
        page.Children.Add(Small("From the model. ✎ types a quantity by hand; ↺ goes back to the model's. The tick puts an item in the BOQ and the back-up."));
        page.Children.Add(ElementsTable(b));

        var missing = Missing(_p, b);
        if (missing.Count > 0)
        {
            page.Children.Add(Section("To complete it"));
            foreach (var what in missing.Take(8)) page.Children.Add(Small("• " + what));
            if (missing.Count > 8) page.Children.Add(Small($"… and {missing.Count - 8} more."));
        }

        // The berth's settings, folded: what it leaves out and its design parameters.
        var settings = BerthSettings(b);
        settings.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        var toggle = Fold(_settingsOpen, "Berth settings: what it leaves out, design parameters", open => { _settingsOpen = open; settings.Visibility = open ? Visibility.Visible : Visibility.Collapsed; }, 13);
        toggle.Margin = new Thickness(-3, 18, 0, 0);
        page.Children.Add(toggle);
        page.Children.Add(settings);
        return page;
    }

    private bool _settingsOpen;
    private readonly HashSet<int> _folded = []; // the items whose BOQ description is open
    private bool _everyItem;

    /// <summary>A ▸ / ▾ button that shows or hides something.</summary>
    private static Button Fold(bool open, string text, Action<bool> toggled, double size = 11.5)
    {
        var label = Brand.Text((open ? "▾ " : "▸ ") + text, Brand.K.Link, size, bold: size > 12);
        var b = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(StyleProperty, "SpmCostLink");
        AutomationProperties.SetName(b, text);
        AutomationProperties.SetItemStatus(b, open ? "open" : "closed");
        b.Click += (_, e) =>
        {
            e.Handled = true;
            open = !open;
            label.Text = (open ? "▾ " : "▸ ") + text;
            AutomationProperties.SetItemStatus(b, open ? "open" : "closed");
            toggled(open);
        };
        return b;
    }

    private static FrameworkElement Badge(Source from)
    {
        var (text, key) = from switch
        {
            Source.Model => ("model", Brand.K.BadgeOk),
            Source.Hand => ("by hand", Brand.K.BadgeWarn),
            Source.Worked => ("worked out", Brand.K.BadgeMuted),
            _ => ("–", Brand.K.BadgeMuted),
        };
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = Brand.Text(text, size: 10.5) };
        badge.SetResourceReference(Border.BackgroundProperty, key);
        return badge;
    }

    /// <summary>Horizontal bars: a label, a bar as long as its share of the largest, the figure.</summary>
    private static FrameworkElement Bars(List<(string Label, double Value, string Text, SolidColorBrush Color)> bars)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 210 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 96 });
        var max = bars.Count > 0 ? bars.Max(x => x.Value) : 0;
        var row = 0;
        foreach (var (label, value, text, color) in bars)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Brand.Text(label, size: 12);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = label;
            name.Margin = new Thickness(0, 3, 8, 3);
            Put(grid, name, row, 0);
            var track = new Grid { Height = 9, VerticalAlignment = VerticalAlignment.Center };
            var back = new Border { CornerRadius = new CornerRadius(4.5) };
            back.SetResourceReference(Border.BackgroundProperty, Brand.K.BorderSubtle);
            track.Children.Add(back);
            var share = max > 0 ? value / max : 0;
            var fill = new Grid();
            fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.005), GridUnitType.Star) });
            fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0), GridUnitType.Star) });
            fill.Children.Add(new Border { CornerRadius = new CornerRadius(4.5), Background = color });
            track.Children.Add(fill);
            Put(grid, track, row, 1);
            Put(grid, Right(Brand.Text(text, size: 12)), row, 2);
            row++;
        }
        return grid;
    }

    /// <summary>
    /// The berth's elements: the name with where its quantity came from, its BOQ description folded under it, the quantity
    /// (✎ to type it by hand), the cost, Show to select them in Revit. Items with nothing for this berth are folded away.
    /// </summary>
    private FrameworkElement ElementsTable(Berth b)
    {
        var page = new StackPanel();
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 110 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 92 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heads = new[] { "", "ELEMENT", "QUANTITY", "COST", "" };
        for (var i = 0; i < heads.Length; i++) Put(grid, i is 2 or 3 ? Right(Brand.Text(heads[i], Brand.K.Muted, 10.5, bold: true)) : Brand.Text(heads[i], Brand.K.Muted, 10.5, bold: true), 0, i);
        var row = 1;
        var all = Costs(_p, b);
        var shown = all.Where(l => _everyItem || l.Qty is > 0 || l.From == Source.Hand).ToList();
        foreach (var line in shown)
        {
            var item = line.Item;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rule = new Border { BorderThickness = new Thickness(0, 1, 0, 0) };
            rule.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
            Grid.SetColumnSpan(rule, 5);
            Put(grid, rule, row, 0);
            var inFiles = new CheckBox { IsChecked = item.InFiles, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 8, 8, 0), ToolTip = "In the BOQ and the back-up calculation (for every berth)" };
            AutomationProperties.SetName(inFiles, item.Name + " in the files");
            inFiles.Checked += (_, _) => { item.InFiles = true; Changed(); };
            inFiles.Unchecked += (_, _) => { item.InFiles = false; Changed(); };
            Put(grid, inFiles, row, 0);
            var name = new StackPanel { Margin = new Thickness(0, 6, 8, 6) };
            var title = new WrapPanel();
            var label = Brand.Text(item.Name, item.InFiles ? Brand.K.Text : Brand.K.Muted, 12.5);
            label.TextWrapping = TextWrapping.Wrap;
            title.Children.Add(label);
            title.Children.Add(Badge(line.From));
            name.Children.Add(title);
            // Its BOQ description with the berth's numbers (pile lengths, tips, tonnage), folded until asked for.
            if (item.BoqText.Length > 0 && line.Qty is > 0)
            {
                var desc = Small(Describe(_p, b, item));
                desc.Visibility = _folded.Contains(item.No) ? Visibility.Visible : Visibility.Collapsed;
                var no = item.No;
                var fold = Fold(_folded.Contains(no), "BOQ description", open => { if (open) _folded.Add(no); else _folded.Remove(no); desc.Visibility = open ? Visibility.Visible : Visibility.Collapsed; }, 11);
                fold.Margin = new Thickness(-3, 1, 0, 0);
                name.Children.Add(fold);
                name.Children.Add(desc);
            }
            Put(grid, name, row, 1);
            // The model's quantity; ✎ types one by hand (an empty box goes back to the model's).
            var qty = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 8, 0) };
            var number = item.No;
            qty.Children.Add(Typed(Qty(line.Qty) + " " + item.Unit, b.Hand.TryGetValue(number, out var h) ? h.ToString("0.###", CultureInfo.InvariantCulture) : line.Qty is { } q ? Math.Round(q, 3).ToString("0.###", CultureInfo.InvariantCulture) : "",
                item.Name + " quantity by hand", text =>
                {
                    if (text.Length == 0) return b.Hand.Remove(number);
                    if (Num(text) is not { } v) { _status.Text = $"{item.Name}: type a number, or leave it empty for the model's."; return false; }
                    b.Hand[number] = v;
                    return true;
                }, Brand.K.Text, 12.5));
            if (line.From == Source.Hand)
            {
                var back = new Button { Content = Brand.Text("↺", Brand.K.Link, 12.5), ToolTip = "Back to the model's quantity", Margin = new Thickness(2, 0, 0, 0) };
                back.SetResourceReference(StyleProperty, "SpmCostLink");
                AutomationProperties.SetName(back, item.Name + ": back to the model's quantity");
                back.Click += (_, _) => { b.Hand.Remove(number); Changed(); };
                qty.Children.Add(back);
            }
            Put(grid, qty, row, 2);
            var cost = Right(Brand.Text(line.Cost > 0 ? BerthCostMath.Money(line.Cost, _p.Currency) : "–", size: 12.5));
            cost.VerticalAlignment = VerticalAlignment.Top;
            cost.Margin = new Thickness(0, 7, 8, 0);
            if (line.Rate > 0) cost.ToolTip = $"{Qty(line.Qty)} {item.Unit} × {line.Rate.ToString("#,0.##", CultureInfo.InvariantCulture)} {_p.Currency}/{item.Unit}";
            Put(grid, cost, row, 3);
            if (b.Measured?.Ids.TryGetValue(item.No, out var ids) == true && ids.Count > 0)
            {
                var show = new Button { Content = Brand.Text("Show", Brand.K.Link, 11.5), Margin = new Thickness(0, 5, 0, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = $"Select the {ids.Count} elements in Revit" };
                show.SetResourceReference(StyleProperty, "SpmCostLink");
                AutomationProperties.SetName(show, $"Show the {item.Name} in Revit");
                show.Click += async (_, _) => await ShowInRevit(ids);
                Put(grid, show, row, 4);
            }
            row++;
        }
        page.Children.Add(grid);
        var hidden = all.Count - shown.Count;
        if (hidden > 0 || _everyItem)
        {
            var more = Fold(_everyItem, _everyItem ? "Hide the items with nothing in this berth" : $"{hidden} more items with nothing in this berth", open => { _everyItem = open; ShowDetail(); }, 11.5);
            more.Margin = new Thickness(-3, 6, 0, 0);
            page.Children.Add(more);
        }
        return page;
    }

    /// <summary>What the berth leaves out (or, for a part, where it is) and its design parameters.</summary>
    private FrameworkElement BerthSettings(Berth b)
    {
        var page = new StackPanel();
        page.Children.Add(Small(b.PartOf.Length > 0 ? $"The {b.Part} part: {(b.FindBy == BerthStatusMath.FindBy.ScopeBox ? $"scope box {b.Value}" : $"{b.Parameter} = {b.Value}")}, only inside {b.Only}. Change it on its berth."
            : b.FindBy == BerthStatusMath.FindBy.ScopeBox ? $"Found by scope box {b.Value}." : $"Found by {b.Parameter} = {b.Value}."));
        var grid = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
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
        if (b.PartOf.Length == 0)
        {
            var exclude = Labeled("Leave out scope boxes", b.Exclude, 200, "Scope boxes whose elements are not this berth's (for example BERTH 9 - PART 1); several with ;. The part left out gets a card of its own.");
            exclude.LostFocus += (_, _) => { if (exclude.Text.Trim() != b.Exclude) { b.Exclude = exclude.Text.Trim(); Changed(); } };
            var note = Labeled("Which is", b.ExcludeNote, 150, "A short name for what is left out (excl. RoRo); it goes in the berth's name in the files.");
            note.LostFocus += (_, _) => { if (note.Text.Trim() != b.ExcludeNote) { b.ExcludeNote = note.Text.Trim(); Changed(); } };
            page.Children.Add(grid);
        }
        page.Children.Add(Small("Design parameters: pile sizes come from the model when it is measured; reinforcement rates (kg/m³) from the comparison workbook or typed here."));
        var pars = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (key, label, unit) in BerthCostMath.Params)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 6), Width = 150 };
            cell.Children.Add(Small($"{label} ({unit})"));
            var box = Field(b.Params.TryGetValue(key, out var v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "", label);
            box.LostFocus += (_, _) =>
            {
                if (box.Text.Trim().Length == 0) { if (!b.Params.Remove(key)) return; }
                else if (Num(box.Text) is { } n) { if (b.Params.TryGetValue(key, out var old) && old == n) return; b.Params[key] = n; }
                else { _status.Text = $"{label}: type a number."; return; }
                Changed();
            };
            cell.Children.Add(box);
            pars.Children.Add(cell);
        }
        page.Children.Add(pars);
        return Box(page, top: 6);
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
