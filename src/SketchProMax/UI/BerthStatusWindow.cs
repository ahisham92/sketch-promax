using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using SketchProMax.Tools;
using static SketchProMax.Tools.BerthStatusMath;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace SketchProMax.UI;

/// <summary>
/// The berth status tracker (Berth status button, next to the pre-submission checklist): every berth of the project with
/// its status percentage, its next submission (due 08:00 on the day, or the setup's time) and the expected finish; click a
/// berth for its areas, what to fix, hand entries, deliverables and comments, and its Excel file. The model is read only
/// when Update status is pressed, in short steps (BerthStatusFacts), so Revit is never held; the window otherwise shows
/// the last saved results. The setup comes from the user's Excel (Setup > Import). Reads the model only.
/// </summary>
internal sealed class BerthStatusWindow : Window
{
    private static BerthStatusWindow? _open;
    private static readonly TimeSpan StepBudget = TimeSpan.FromMilliseconds(200);

    private string _project;
    private Setup? _setup;
    private State _state = new();
    private string _selected = "";
    private string _area = "";
    private bool _reading, _stop, _closed;
    private string _savedText = "";
    private string _file = "";

    /// <summary>The setup's model of the open file, or null to show every model.</summary>
    private Model? ShownModel => _setup is null || _file.Length == 0 ? null : ModelFor(_setup, _file);

    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted);
    private readonly StackPanel _list = new() { Margin = new Thickness(14, 12, 14, 14) };
    private readonly ScrollViewer _detail = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(20, 14, 20, 20), Focusable = false };
    private readonly Border _runStrip = new() { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 8, 18, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock _runText = Brand.Text("", size: 12.5, bold: true);
    private readonly ProgressBar _runBar = new() { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _update = RichUi.Button(RichUi.Icon.Refresh, "Update status", Brand.Weight.Primary);
    private readonly Button _stopButton = RichUi.Button(RichUi.Icon.Close, "Stop");
    private readonly ScoreRing _ring;
    private readonly TextBlock _subtitle = Brand.Text("", Brand.K.HeaderSub);

    // ---- Opening --------------------------------------------------------------------------------------------------

    public static Result Command(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        var project = doc is null || doc.IsFamilyDocument ? Projects().FirstOrDefault() ?? "" : ChecklistProjects.ProjectKey(DocName(doc));
        // Each model shows its own packages: the open model's file name picks it (none matching: every model).
        var file = doc is null || doc.IsFamilyDocument ? "" : DocName(doc);
        if (_open is { IsLoaded: true })
        {
            if (_open._project == project || project.Length == 0) { _open._file = file.Length > 0 ? file : _open._file; _open.Refresh(); _open.Activate(); return Result.Succeeded; }
            _open.Close();
        }
        var window = new BerthStatusWindow(project) { _file = file };
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

    private BerthStatusWindow(string project)
    {
        _project = project;
        Title = "Sketch ProMax berth status";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1440, Math.Floor(area.Width * 0.92));
        Height = Math.Min(900, Math.Floor(area.Height * 0.92));
        MinWidth = Math.Min(860, Width);
        MinHeight = Math.Min(480, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        var motion = RichUi.MotionOf(Settings.AgentSettings.Load());
        RichUi.Apply(this, motion);
        _ring = new ScoreRing("All packages", motion);
        _update.Click += async (_, _) => await UpdateStatus();
        AutomationProperties.SetHelpText(_update, "Reads the open models for their berths, in short steps so Revit stays usable. Nothing is read until you press it.");
        _stopButton.Click += (_, _) => { _stop = true; Brand.SetBusy(_stopButton, true, "Stopping…"); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !_reading) Close(); };
        Closed += (_, _) => { _closed = true; _stop = true; Save(); };
        // The chat can set percentages and comments too (berth_status): coming back to the window shows them.
        Deactivated += (_, _) => { if (!_reading) Save(); };
        Activated += (_, _) =>
        {
            if (_reading || _project.Length == 0 || _setup is null) return;
            if (StateText(_project) is { Length: > 0 } text && text != _savedText) { _state = LoadState(_project); _savedText = text; Refresh(); }
        };
        Load();
        Build();
    }

    private void Load()
    {
        _setup = _project.Length == 0 ? null : LoadSetup(_project);
        // The setup Excel changed since it was imported: take the new one (the user keeps the schedule in that file).
        if (_setup is { SourcePath.Length: > 0 } s && File.Exists(s.SourcePath) && ChecklistMath.Stamp(s.SourcePath) != s.SourceStamp)
        {
            try
            {
                var fresh = ReadSetup(s.SourcePath);
                SaveSetup(_project, fresh);
                _setup = fresh;
                _status.Text = "The setup Excel changed: its new schedule and berths are in.";
            }
            catch (InvalidDataException ex) { _status.Text = "The setup Excel changed but couldn't be read: " + ex.Message; }
        }
        _state = _project.Length == 0 ? new State() : LoadState(_project);
        _savedText = _project.Length == 0 ? "" : StateText(_project);
    }

    private void Save()
    {
        if (_project.Length == 0) return;
        try { SaveState(_project, _state); _savedText = StateText(_project); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "Couldn't save (" + ex.Message + ")."; }
    }

    // ---- Layout ---------------------------------------------------------------------------------------------------

    private void Build()
    {
        // Every control kept across rebuilds leaves its old parent first: one still attached throws and leaves the window blank.
        foreach (var reused in new FrameworkElement[] { _status, _list, _detail, _runStrip, _runText, _runBar, _update, _stopButton, _ring, _subtitle })
            switch (reused.Parent)
            {
                case Panel p: p.Children.Remove(reused); break;
                case Decorator d: d.Child = null; break;
                case ContentControl c: c.Content = null; break;
            }
        Content = null;

        var band = Brand.Header("Berth status", null);
        if (band.Child is DockPanel row && row.Children.OfType<StackPanel>().LastOrDefault() is { } texts) texts.Children.Add(_subtitle);
        _subtitle.Text = _setup is null ? (_project.Length == 0 ? "Open a model of the project first" : $"{_project}: not set up yet") :
            $"{(_setup.Title.Length > 0 ? _setup.Title : "Berth status")} · {_project} · " +
            (ShownModel is { } shown ? $"{shown.Name} model · {_setup.Berths.Count(b => Same(b.Model, shown.Name))} submissions" : $"{_setup.Berths.Count} submissions in {_setup.Models.Count} models");
        band.Padding = new Thickness(16, 12, 420, 12);
        var header = new Grid();
        header.Children.Add(band);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        var setupButton = RichUi.Button(RichUi.Icon.Settings, "Setup");
        setupButton.VerticalAlignment = VerticalAlignment.Center;
        setupButton.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetHelpText(setupButton, "Import the setup Excel (models, berths, submissions, planned counts, deliverables, weights, working week), open it, or start one.");
        setupButton.Click += (_, _) => SetupMenu(setupButton);
        _update.VerticalAlignment = VerticalAlignment.Center;
        _update.IsEnabled = _setup is not null;
        right.Children.Add(setupButton);
        right.Children.Add(_update);
        right.Children.Add(_ring);
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
        var exportAll = RichUi.Button(RichUi.Icon.Export, "Export all to Excel");
        AutomationProperties.SetHelpText(exportAll, "Writes one Excel file per berth (status, submissions, deliverables, what to fix, comments, history) and opens the folder.");
        exportAll.Click += (_, _) => ExportAll();
        exportAll.IsEnabled = _setup is not null;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var foot = new DockPanel { Margin = new Thickness(18, 10, 18, 12) };
        var buttons = Brand.ButtonRow(exportAll, close);
        buttons.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        foot.Children.Add(_status);
        var footBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };
        footBorder.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        DockPanel.SetDock(footBorder, Dock.Bottom);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 400 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
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
        if (_setup is null)
        {
            root.Children.Add(Welcome());
            _ring.Show(null, Colors.White, "Not set up yet");
        }
        else
        {
            root.Children.Add(body);
            Refresh();
        }
        Content = root;
    }

    /// <summary>The first page: what the tracker needs and how to give it (the setup Excel).</summary>
    private FrameworkElement Welcome()
    {
        var page = new StackPanel { MaxWidth = 760, Margin = new Thickness(28, 22, 28, 22), HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Heading(_project.Length == 0 ? "Open a model of the project" : $"Set up the berth status for {_project}"));
        page.Children.Add(Para("The tracker needs one Excel file with your project: the models (and the text in their file names), the berths and how to find each " +
            "in the model (a scope box, a parameter, a workset, or the whole model), the submission schedule, how many piles, beams, drawings, details and " +
            "schedules each berth should have, the deliverables (calculations, bills, back-up calculations of the bills, costing) and the working week " +
            "(Sunday to Thursday, due at 08:00 on the submission day). It stays on this PC; nothing of the project goes anywhere else."));
        page.Children.Add(Para("Start from the example, fill it in and import it. Keep the file: when you change the schedule in it later, the tracker takes the change the next time it opens.", top: 10));
        var make = RichUi.Button(RichUi.Icon.Export, "Make the setup Excel to fill in", Brand.Weight.Primary);
        make.Click += (_, _) => WriteSetupExcel(Example(), open: true);
        var import = RichUi.Button(RichUi.Icon.Import, "Import the setup Excel…");
        import.Click += (_, _) => ImportSetup();
        make.IsEnabled = import.IsEnabled = _project.Length > 0;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Children = { make, import } };
        import.Margin = new Thickness(8, 0, 0, 0);
        page.Children.Add(row);
        return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    private void SetupMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string text, Action go, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => go();
            menu.Items.Add(mi);
            return mi;
        }
        Item("Import the setup Excel…", ImportSetup, _project.Length > 0);
        Item("Open the setup Excel", () => Shell(_setup!.SourcePath), _setup is { SourcePath.Length: > 0 } s && File.Exists(s.SourcePath));
        Item("Save the setup to a new Excel", () => WriteSetupExcel(_setup!, open: true), _setup is not null);
        Item("Make an example setup Excel", () => WriteSetupExcel(Example(), open: true), _project.Length > 0);
        menu.Items.Add(new Separator());
        Item("Open the exports folder", () => Shell(ExportFolder()), _project.Length > 0);
        menu.IsOpen = true;
    }

    private string ExportFolder()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Berth status", ChecklistProjects.CleanName(_project));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void WriteSetupExcel(Setup setup, bool open)
    {
        try
        {
            var path = OfficeDocs.FreePath(ExportFolder(), OfficeDocs.SafeName($"{_project} berth status setup"), ".xlsx");
            OfficeXlsx.Write(path, "Berth status setup", SetupSheets(setup));
            _status.Text = $"Saved {Path.GetFileName(path)}. Fill it in, save it, then Setup > Import the setup Excel.";
            if (open) Shell(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "Couldn't write the setup Excel (" + ex.Message + ")."; }
    }

    private void ImportSetup()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "The berth status setup Excel", Filter = "Excel workbook (*.xlsx)|*.xlsx", InitialDirectory = ExportFolder() };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var setup = ReadSetup(dialog.FileName);
            SaveSetup(_project, setup);
            _setup = setup;
            ActivityLog.Note("berth_status_setup", $"{_project}: {setup.Models.Count} models, {setup.Berths.Count} berths, {setup.Submissions.Count} submissions");
            Build();
            _status.Text = $"Imported: {setup.Berths.Count} berths, {setup.Submissions.Count} submissions. Press Update status with a model open to read it.";
        }
        catch (InvalidDataException ex) { _status.Text = "Couldn't import it: " + ex.Message; }
        catch (IOException ex) { _status.Text = "Couldn't open it (" + ex.Message + "). Close it in Excel and try again."; }
    }

    // ---- The berth list -------------------------------------------------------------------------------------------

    private List<Summary> Summaries()
    {
        var now = DateTime.Now;
        var model = ShownModel;
        return _setup!.Berths.Where(b => model is null || Same(b.Model, model.Name)).Select(b => Summarize(_setup, b, _state, now)).ToList();
    }

    /// <summary>
    /// Redraws the overview from the setup and the saved results: a ring per submission package, the berths of the chosen
    /// package (each with its own ring), the coming submissions with time left and the progress each engineer needs a
    /// working day, and the chosen package's or berth's details on the right.
    /// </summary>
    private void Refresh()
    {
        if (_setup is null) return;
        var now = DateTime.Now;
        var all = Summaries();
        _list.Children.Clear();
        var known = all.Where(s => s.Percent is not null).ToList();
        double? overall = known.Count == 0 ? null : known.Average(s => s.Percent!.Value);
        var (r, g, b) = RingColor(overall);
        _ring.Show(overall is null ? null : (int)Math.Round(overall.Value), overall is null ? Colors.White : Color.FromRgb(r, g, b), "The average status of the packages that have been read");
        var children = all.SelectMany(s => ChildrenOf(s.Berth).Select(c => Summarize(_setup, c, _state, now))).ToList();
        var every = all.Concat(children).ToList();
        if (_selected.Length == 0 || every.All(s => s.Berth.Key != _selected)) _selected = all.FirstOrDefault()?.Berth.Key ?? "";
        var selected = every.FirstOrDefault(s => s.Berth.Key == _selected);
        var package = selected is null ? null : all.FirstOrDefault(s => s.Berth.Key == PackageKey(selected.Berth));
        foreach (var model in all.GroupBy(s => s.Berth.Model, StringComparer.OrdinalIgnoreCase))
        {
            var title = Brand.Text(model.Key.ToUpperInvariant() + " SUBMISSIONS", Brand.K.Muted, 12, bold: true);
            title.Margin = new Thickness(2, _list.Children.Count == 0 ? 0 : 14, 0, 6);
            _list.Children.Add(title);
            var tiles = new WrapPanel();
            foreach (var s in model) tiles.Children.Add(PackageTile(s, s.Berth.Key == package?.Berth.Key));
            _list.Children.Add(tiles);
        }
        if (package is not null)
            _list.Children.Add(BerthsPanel(package, children.Where(c => PackageKey(c.Berth) == package.Berth.Key).ToList()));
        _list.Children.Add(Coming(all, now));
        ShowDetail(selected);
    }

    /// <summary>The package a berth belongs to (itself when it is a package).</summary>
    private static string PackageKey(Berth b) => b.Parent.Length > 0 ? KeyOf(b.Model, b.Parent) : b.Key;

    private static string DueText(Summary s) =>
        s.Next is null
            ? (s.Last is { } last ? $"Submitted: {last.Name}{When(last, "dd-MMM", ", ")}" : "No submission set")
            : $"{s.Next.Name}: due {s.Due:ddd dd-MMM HH:mm}" + (s.Due > DateTime.Now ? $" · {Math.Round(s.HoursLeft):0} working hours left" : " · overdue");

    private FrameworkElement PackageTile(Summary s, bool on)
    {
        var stack = new StackPanel { Width = 104 };
        stack.Children.Add(new PercentRing(s.Percent, 76, 8, 17) { HorizontalAlignment = HorizontalAlignment.Center });
        var name = Brand.Text(s.Berth.Name, size: 12.5, bold: true);
        name.TextAlignment = TextAlignment.Center;
        name.TextWrapping = TextWrapping.Wrap;
        name.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(name);
        var chip = StageChip(StageOf(s));
        chip.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(chip);
        var tip = $"{s.Berth.Name} · {Pct(s.Percent)} · {DueText(s)}" + (ChildrenOf(s.Berth).Count > 0 ? ". Click to see its berths." : ". Click for its details.");
        return Tile(stack, on, tip, $"{s.Berth.Name}, {Pct(s.Percent)}, {Text(StageOf(s))}", () => { _selected = s.Berth.Key; Refresh(); }, new Thickness(8, 8, 8, 8));
    }

    /// <summary>The second level: the chosen package's berths, each with its own ring.</summary>
    private FrameworkElement BerthsPanel(Summary package, List<Summary> berths)
    {
        var page = new StackPanel();
        page.Children.Add(Brand.Text(berths.Count > 0 ? $"{package.Berth.Name}: its {berths.Count} berths" : package.Berth.Name, size: 14, bold: true));
        page.Children.Add(Small(berths.Count > 0
            ? $"Each berth is measured on its own; {package.Berth.Name}'s ring is their average. Click a berth for its details."
            : "Not split into berths: list its berths in the setup's Berths sheet (Value, with ;) to see each on its own."));
        var tiles = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in berths)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new PercentRing(b.Percent, 58, 7, 14));
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
            texts.Children.Add(Brand.Text(b.Berth.Name, size: 13.5, bold: true));
            texts.Children.Add(Small(b.State.ReadAt is { } at ? $"read {at:dd-MMM HH:mm}" : "not read yet"));
            row.Children.Add(texts);
            tiles.Children.Add(Tile(row, b.Berth.Key == _selected, $"{b.Berth.Name} of {package.Berth.Name}: its own status. Click for its areas and what to fix.",
                $"{b.Berth.Name} of {package.Berth.Name}, {Pct(b.Percent)}", () => { _selected = b.Berth.Key; Refresh(); }, new Thickness(8, 8, 14, 8)));
        }
        if (berths.Count > 0) page.Children.Add(tiles);
        var box = Box(page, top: 6);
        box.BorderThickness = new Thickness(2);
        box.SetResourceReference(Border.BorderBrushProperty, Brand.K.Accent);
        return box;
    }

    /// <summary>The coming submissions: due time, working time left, engineers and what each must add a working day.</summary>
    private FrameworkElement Coming(List<Summary> all, DateTime now)
    {
        var page = new StackPanel();
        page.Children.Add(Brand.Text("Coming submissions", size: 14, bold: true));
        var week = _setup!.Week;
        page.Children.Add(Small($"Due {week.Due:hh\\:mm} on the day unless the setup says otherwise · working days {string.Join(", ", week.Days.Select(d => d.ToString().Substring(0, 3)))}, {week.Start:hh\\:mm}-{week.End:hh\\:mm}"));
        var coming = all.Where(s => s.Next is not null).OrderBy(s => s.Due).ToList();
        if (coming.Count == 0) { page.Children.Add(Para("None: every package is submitted or has no date in the Submissions sheet.", Brand.K.Muted)); return Box(page, top: 12); }
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var w in new[] { 1.4, 0.8, 1.4, 1.1, 1.3, 1.2 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        void Cell(UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); grid.Children.Add(e); }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = ["PACKAGE", "STATUS", "DUE", "TIME LEFT", "ENGINEERS", "EACH / DAY"];
        for (var i = 0; i < heads.Length; i++) Cell(Brand.Text(heads[i], Brand.K.Muted, 10.5, bold: true), 0, i);
        var row = 1;
        foreach (var s in coming)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Brand.Text(s.Berth.Name, size: 12, bold: true);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = s.Berth.Name;
            Cell(name, row, 0);
            Cell(Brand.Text(Pct(s.Percent).Replace("not measured", "–"), PercentKey(s.Percent), 12, bold: true), row, 1);
            Cell(Brand.Text($"{s.Due:ddd dd-MMM HH:mm}", size: 12), row, 2);
            var late = s.Due <= now;
            Cell(Brand.Text(late ? "Overdue" : $"{s.HoursLeft:0} h · {s.HoursLeft / Math.Max(1, week.HoursPerDay):0.0} days", late ? Brand.K.DangerText : Brand.K.Text, 12, bold: late), row, 3);
            Cell(Engineers(s), row, 4);
            var each = PerEngineer(s.Percent is { } p && s.Due is { } due ? NeededPerDay(p, due, now, week) : null, _state.For(s.Berth.Key).Engineers);
            var need = Brand.Text(each is { } e ? $"{e:0.#}% a day" : "–", each > 10 ? Brand.K.DangerText : Brand.K.Text, 12, bold: each > 10);
            need.ToolTip = "The progress each engineer must add every working day to finish by the due time: what is left, over the working days left, shared between the engineers.";
            Cell(need, row, 5);
            foreach (var cell in grid.Children.OfType<FrameworkElement>().Where(c => Grid.GetRow(c) == row)) { cell.Margin = new Thickness(0, 4, 6, 0); cell.VerticalAlignment = VerticalAlignment.Center; }
            row++;
        }
        page.Children.Add(grid);
        return Box(page, top: 12);
    }

    /// <summary>The engineers on a package (− n +): the progress needed per working day is shared between them.</summary>
    private FrameworkElement Engineers(Summary s)
    {
        var state = _state.For(PackageKey(s.Berth));
        var n = state.Engineers ?? 1;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        Button Step(string text, string help, int by)
        {
            var b = new Button { Content = text, Width = 24, Height = 22, Padding = new Thickness(0) };
            b.SetResourceReference(StyleProperty, "SpmSecondaryButton");
            AutomationProperties.SetName(b, help);
            b.ToolTip = help;
            b.Click += (_, _) => { state.Engineers = Math.Max(1, Math.Min(50, n + by)); Save(); Refresh(); };
            return b;
        }
        panel.Children.Add(Step("−", "One engineer fewer", -1));
        var count = Brand.Text(n.ToString(CultureInfo.CurrentCulture), size: 12.5, bold: true);
        count.Width = 24;
        count.TextAlignment = TextAlignment.Center;
        count.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(count, "Engineers");
        panel.Children.Add(count);
        panel.Children.Add(Step("+", "One engineer more", 1));
        panel.ToolTip = "Engineers working on this package: the progress needed per working day is shared between them.";
        return panel;
    }

    /// <summary>A clickable tile (mouse, Enter or Space) with its description on hover.</summary>
    private static Border Tile(UIElement child, bool on, string tip, string name, Action pick, Thickness padding)
    {
        var tile = new Border
        {
            Padding = padding, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(on ? 2 : 1), Child = child, Cursor = Cursors.Hand, Focusable = true, ToolTip = tip,
        };
        tile.SetResourceReference(Border.BackgroundProperty, on ? Brand.K.AccentSoft : Brand.K.Bg);
        tile.SetResourceReference(Border.BorderBrushProperty, on ? Brand.K.Accent : Brand.K.Border);
        AutomationProperties.SetName(tile, name);
        AutomationProperties.SetHelpText(tile, tip);
        tile.MouseLeftButtonUp += (_, _) => pick();
        tile.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { pick(); e.Handled = true; } };
        return tile;
    }

    private static string PercentKey(double? p) => p is null ? Brand.K.Muted : p >= 90 ? Brand.K.SuccessText : p >= 50 ? Brand.K.WarningText : Brand.K.DangerText;

    private static Border StageChip(Stage stage)
    {
        var key = stage switch
        {
            Stage.Submitted => Brand.K.BadgeOk,
            Stage.InProgress => Brand.K.BadgeWarn,
            Stage.Overdue => Brand.K.BadgeError,
            _ => Brand.K.BadgeMuted,
        };
        var chip = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = Brand.Text(Text(stage), Brand.K.OnAccent, 11, bold: true) };
        chip.SetResourceReference(Border.BackgroundProperty, key);
        return chip;
    }

    // ---- The berth's details --------------------------------------------------------------------------------------

    private void ShowDetail(Summary? s)
    {
        var page = new StackPanel();
        _detail.Content = page;
        if (s is null) { page.Children.Add(Para("The setup has no berths.")); return; }
        var b = s.State;
        // What is typed here is kept on the berth's own state (a package's status shown is put together from its berths).
        var own = _state.For(s.Berth.Key);
        var now = DateTime.Now;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new PercentRing(s.Percent, 104, 10, 22));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        texts.Children.Add(Heading(s.Berth.Name));
        texts.Children.Add(Small(s.Berth.Model + (s.Berth.Parent.Length > 0 ? $" · part of {s.Berth.Parent}" : "") + " · " +
            (b.ReadAt is { } at ? $"read {at:ddd dd-MMM HH:mm} from {b.ReadFrom}" : "not read yet: open its model and press Update status")));
        texts.Children.Add(StageChip(StageOf(s)));
        head.Children.Add(texts);
        page.Children.Add(head);

        // Due, expected finish and engineers, side by side.
        var facts = new UniformGrid { Columns = 3, Margin = new Thickness(0, 14, 0, 0) };
        facts.Children.Add(Fact("DUE", s.Next is null ? (s.Last is not null ? "Submitted" : "No date") : $"{s.Due:ddd dd-MMM HH:mm}",
            s.Next is null ? (s.Last is not null ? "Still checked for the BIM fixes" : "Set it in the Submissions sheet") : s.Due > now ? $"{s.HoursLeft:0} working hours · {s.HoursLeft / Math.Max(1, _setup!.Week.HoursPerDay):0.0} days" : "Overdue",
            "Due at its time (08:00 unless the setup says otherwise) on the submission day"));
        var forecast = s.Percent is null ? null : Forecast(b.History, s.Percent.Value, now, _setup!.Week);
        facts.Children.Add(Fact("EXPECTED FINISH", s.Finish is { } f ? $"{f:ddd dd-MMM HH:mm}" : "–",
            (s.FinishByHand ? "Set by you" : forecast is not null ? "From your readings" : "Needs two readings") + " · " + Text(s.Verdict),
            "From the progress between your readings, or the date you set yourself (below)"));
        var engineers = new StackPanel();
        engineers.Children.Add(Brand.Text("ENGINEERS", Brand.K.Muted, 10.5, bold: true));
        var stepper = Engineers(s);
        stepper.Margin = new Thickness(0, 4, 0, 2);
        engineers.Children.Add(stepper);
        var perDay = s.Percent is { } pc && s.Due is { } due ? NeededPerDay(pc, due, now, _setup!.Week) : null;
        var each = PerEngineer(perDay, _state.For(PackageKey(s.Berth)).Engineers);
        engineers.Children.Add(Small(perDay is { } pd ? $"{pd:0.#}% a day in all · {each:0.#}% each" : s.Next is null ? "Nothing due" : "Measure it first"));
        facts.Children.Add(FactBox(engineers, "Engineers on the package: the progress needed per working day is shared between them"));
        page.Children.Add(facts);

        // The expected finish by hand or asked of the AI, folded.
        var byHand = new DatePicker { SelectedDate = b.FinishByHand?.Date, Width = 140, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(byHand, "Expected finish set by you");
        var handTime = new TextBox { Text = b.FinishByHand is { } fh ? fh.ToString("HH:mm", CultureInfo.InvariantCulture) : "17:00", Width = 60, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(handTime, "Time");
        void SetHand()
        {
            own.FinishByHand = byHand.SelectedDate is { } d ? d.Date + (ParseTime(handTime.Text) ?? TimeSpan.FromHours(17)) : null;
            Save();
            Refresh();
        }
        byHand.SelectedDateChanged += (_, _) => SetHand();
        handTime.LostFocus += (_, _) => { if (byHand.SelectedDate is not null) SetHand(); };
        var clear = RichUi.Button(RichUi.Icon.Undo, "Use the readings");
        clear.Click += (_, _) => { own.FinishByHand = null; Save(); Refresh(); };
        var ask = RichUi.Button(RichUi.Icon.Ask, "Ask Sketch ProMax for an estimate");
        AutomationProperties.SetHelpText(ask, "Sends this berth's status, schedule and history to the Sketch ProMax chat, which estimates the finish. Set its date yourself if you agree.");
        ask.Click += (_, _) =>
        {
            var problem = Chat.ChatPane.Ask(Question(_setup!, s, DateTime.Now));
            _status.Text = problem.Length > 0 ? problem : "Asked the Sketch ProMax chat (in the pane): set its date as the expected finish if you agree.";
        };
        var handRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        handRow.Children.Add(byHand);
        handRow.Children.Add(handTime);
        handRow.Children.Add(clear);
        handRow.Children.Add(ask);
        clear.Margin = ask.Margin = new Thickness(0, 4, 8, 4);
        page.Children.Add(new Expander { Header = "Set the expected finish myself, or ask Sketch ProMax", Content = handRow, Margin = new Thickness(0, 10, 0, 0), IsExpanded = b.FinishByHand is not null });

        // The areas as rings: what each measures on hover, click one for what to fix.
        page.Children.Add(Section("Status by area"));
        page.Children.Add(Small("Point at a ring for what it measures; click it for what to fix."));
        if (s.Scores.Count > 0 && s.Scores.All(a => a.Area.Key != _area))
            _area = (s.Scores.FirstOrDefault(a => a.Result is { Findings.Count: > 0 } && a.Area.Key != Deliverables) ?? s.Scores[0]).Area.Key;
        var rings = new UniformGrid { Columns = 5, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var a in s.Scores)
        {
            var stack = new StackPanel();
            stack.Children.Add(new PercentRing(a.Percent, 58, 7, 14) { HorizontalAlignment = HorizontalAlignment.Center });
            var name = Brand.Text(a.Area.Name, size: 11.5, bold: true);
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(name);
            var badge = Small($"weight {a.Weight:0.#}" + (a.From == Source.Hand ? " · set by you" : a.From == Source.Files ? " · from files" : ""));
            badge.TextAlignment = TextAlignment.Center;
            stack.Children.Add(badge);
            var key = a.Area.Key;
            var tile = Tile(stack, key == _area, $"{a.Area.Name} (weight {a.Weight:0.#}): {a.Area.Measures}" + (a.Result?.Summary is { Length: > 0 } sum ? "\n" + sum : ""),
                $"{a.Area.Name}, {Pct(a.Percent)}", () => { _area = key; ShowDetail(s); }, new Thickness(4, 8, 4, 8));
            tile.Margin = new Thickness(0, 0, 6, 6);
            rings.Children.Add(tile);
        }
        page.Children.Add(rings);
        if (s.Scores.FirstOrDefault(a => a.Area.Key == _area) is { } chosen) page.Children.Add(AreaRow(s, chosen));

        // Deliverables.
        page.Children.Add(Section("Deliverables"));
        if (s.Deliverables.Count == 0) page.Children.Add(Para("None in the setup (Deliverables sheet).", Brand.K.Muted));
        foreach (var d in s.Deliverables) page.Children.Add(DeliverableRow(s, d));

        // The berth's comment, and its Excel.
        page.Children.Add(Section("Comments"));
        var comment = new TextBox { Text = b.Comment, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        AutomationProperties.SetName(comment, "Comment on the berth");
        comment.LostFocus += (_, _) => { if (own.Comment != comment.Text) { own.Comment = comment.Text; Save(); } };
        page.Children.Add(comment);
        var export = RichUi.Button(RichUi.Icon.Export, "Export this berth to Excel", Brand.Weight.Primary);
        AutomationProperties.SetHelpText(export, "Status of every area, submissions, deliverables, what to fix per area, comments and history, one sheet each.");
        export.Click += (_, _) => Export(s, open: true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { export } };
        page.Children.Add(actions);
    }

    private static Border Fact(string label, string value, string sub, string tip)
    {
        var stack = new StackPanel();
        stack.Children.Add(Brand.Text(label, Brand.K.Muted, 10.5, bold: true));
        var v = Brand.Text(value, size: 15, bold: true);
        v.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(v);
        stack.Children.Add(Small(sub));
        return FactBox(stack, tip);
    }

    private static Border FactBox(UIElement child, string tip)
    {
        var box = new Border { Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(10), Child = child, ToolTip = tip };
        box.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        AutomationProperties.SetHelpText(box, tip);
        return box;
    }

    private FrameworkElement AreaRow(Summary s, AreaScore a)
    {
        var row = new StackPanel();
        var top = new DockPanel();
        var value = Brand.Text(Pct(a.Percent), PercentKey(a.Percent), 13, bold: true);
        DockPanel.SetDock(value, Dock.Right);
        top.Children.Add(value);
        top.Children.Add(Brand.Text($"{a.Area.Name}  ·  weight {a.Weight:0.#}" + (a.From == Source.Hand ? "  ·  set by you" : a.From == Source.Files ? "  ·  from files" : ""), size: 13, bold: true));
        row.Children.Add(top);
        row.Children.Add(Small(a.Result?.Summary is { Length: > 0 } sum ? sum : a.Area.Measures));
        // What the area measures shows when the pointer is on it, so the page stays light.
        row.ToolTip = a.Area.Measures;
        AutomationProperties.SetName(row, $"{a.Area.Name}: {Pct(a.Percent)}");
        AutomationProperties.SetHelpText(row, a.Area.Measures);

        // Hand entry: a percentage that replaces the model's, and a comment.
        var hand = s.State.HandFor(a.Area.Key);
        var pct = new TextBox { Text = hand.Percent?.ToString("0", CultureInfo.InvariantCulture) ?? "", Width = 52, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(pct, a.Area.Name + " percentage by hand");
        var note = new TextBox { Text = hand.Comment, MinWidth = 260, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(note, a.Area.Name + " comment");
        pct.LostFocus += (_, _) =>
        {
            double? v = double.TryParse(pct.Text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out var n) ? Clamp(n) : null;
            if (v == hand.Percent) return;
            hand.Percent = v;
            Save();
            Refresh();
        };
        note.LostFocus += (_, _) => { if (hand.Comment != note.Text) { hand.Comment = note.Text; Save(); } };
        var handRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var label = Small("Set % myself:");
        label.ToolTip = "Type your own percentage for this area when the model can't measure it or you know better (for example design sections done). It replaces the one read from the model; empty it to go back.";
        pct.ToolTip = label.ToolTip;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(pct, Dock.Left);
        var noteLabel = Small("Comment:");
        noteLabel.VerticalAlignment = VerticalAlignment.Center;
        noteLabel.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(noteLabel, Dock.Left);
        handRow.Children.Add(label);
        handRow.Children.Add(pct);
        handRow.Children.Add(noteLabel);
        handRow.Children.Add(note);
        row.Children.Add(handRow);

        // What to fix, folded; a Fix for everything that can be fixed in one go.
        if (a.Result is { Findings.Count: > 0 } r && a.Area.Key != Deliverables)
        {
            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var fixable = r.Findings.Where(f => f.Fix is not null).ToList();
            if (fixable.Count > 0)
            {
                var all = RichUi.Button(RichUi.Icon.Fix, FixAllText(fixable), Brand.Weight.Primary);
                all.Margin = new Thickness(0, 2, 0, 6);
                all.HorizontalAlignment = HorizontalAlignment.Left;
                AutomationProperties.SetHelpText(all, "Fixes what can be fixed in the model for this area. Ctrl+Z in Revit undoes it.");
                all.Click += async (_, _) => await Fix(s, all, fixable);
                list.Children.Add(all);
            }
            foreach (var f in r.Findings.Take(40)) list.Children.Add(FindingRow(s, f));
            if (r.Findings.Count > 40) list.Children.Add(Small($"…and {r.Findings.Count - 40} more in the Excel export."));
            var fold = new Expander { Header = $"What to fix ({r.Findings.Count})", Content = list, Margin = new Thickness(0, 6, 0, 0), IsExpanded = true };
            row.Children.Add(fold);
        }
        return Box(row, top: 8);
    }

    private static string FixAllText(List<Finding> fixes) =>
        fixes.All(f => f.Fix == "uniformat") ? "Fill the Assembly Codes" :
        fixes.All(f => f.Fix!.StartsWith("tag:", StringComparison.Ordinal)) ? $"Tag what is missing ({fixes.Count} views)" :
        fixes.All(f => f.Fix!.StartsWith("join:", StringComparison.Ordinal)) ? $"Join them ({fixes.Count})" : $"Fix all ({fixes.Count})";

    private FrameworkElement FindingRow(Summary s, Finding f)
    {
        var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        if (f.Fix is { } fix && fix != "uniformat")
        {
            var button = new Button { Content = "Fix", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
            button.SetResourceReference(StyleProperty, "SpmSecondaryButton");
            AutomationProperties.SetHelpText(button, fix.StartsWith("tag:", StringComparison.Ordinal) ? "Tags the untagged elements in this view with their category's tag." : "Joins the two elements so they no longer clash.");
            button.ToolTip = AutomationProperties.GetHelpText(button);
            button.Click += async (_, _) => await Fix(s, button, [f]);
            DockPanel.SetDock(button, Dock.Right);
            line.Children.Add(button);
        }
        if (f.Id is { } id)
        {
            var show = new Button { Content = "Show", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
            show.SetResourceReference(StyleProperty, "SpmSecondaryButton");
            AutomationProperties.SetHelpText(show, "Opens the view or sheet, or selects the element in Revit and zooms to it.");
            show.Click += async (_, _) => await Show(s, id);
            DockPanel.SetDock(show, Dock.Right);
            line.Children.Add(show);
        }
        var text = Brand.Text($"{f.Item}: {f.Problem}", size: 12);
        text.TextWrapping = TextWrapping.Wrap;
        line.Children.Add(text);
        return line;
    }

    private FrameworkElement DeliverableRow(Summary s, DeliverableScore d)
    {
        var row = new StackPanel();
        var top = new DockPanel();
        var value = Brand.Text(Pct(d.Percent), PercentKey(d.Percent), 13, bold: true);
        DockPanel.SetDock(value, Dock.Right);
        top.Children.Add(value);
        top.Children.Add(Brand.Text(d.Deliverable.Name, size: 13, bold: true));
        row.Children.Add(top);
        row.Children.Add(Small(d.From switch
        {
            Source.Hand => "By hand." + (d.File.Length > 0 ? " File: " + Path.GetFileName(d.File) : ""),
            Source.Files when d.File.Length > 0 => "Found: " + d.File,
            Source.Files => $"Not in {d.Deliverable.Folder} yet.",
            _ => "Type its percentage (no folder set for it in the setup).",
        }));
        // Deliverables belong to the package: a berth of it edits the package's.
        var hand = (s.Berth.Parent.Length > 0 ? _state.For(KeyOf(s.Berth.Model, s.Berth.Parent)) : _state.For(s.Berth.Key)).DeliveredFor(d.Deliverable.Name);
        var pct = new TextBox { Text = hand.Percent?.ToString("0", CultureInfo.InvariantCulture) ?? "", Width = 52, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(pct, d.Deliverable.Name + " percentage");
        var note = new TextBox { Text = hand.Comment, MinWidth = 260 };
        AutomationProperties.SetName(note, d.Deliverable.Name + " comment");
        pct.LostFocus += (_, _) =>
        {
            double? v = double.TryParse(pct.Text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out var n) ? Clamp(n) : null;
            if (v == hand.Percent) return;
            hand.Percent = v;
            Save();
            Refresh();
        };
        note.LostFocus += (_, _) => { if (hand.Comment != note.Text) { hand.Comment = note.Text; Save(); } };
        var handRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var label = Small("%:");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(pct, Dock.Left);
        handRow.Children.Add(label);
        handRow.Children.Add(pct);
        handRow.Children.Add(note);
        row.Children.Add(handRow);
        return Box(row, top: 8);
    }

    /// <summary>
    /// Fixes findings in the model the results were read from (it must be in front): fills the Assembly Codes from the
    /// project checklist's UniFormat rules, tags the missing elements in a view, or joins two clashing elements.
    /// </summary>
    private async Task Fix(Summary s, Button button, List<Finding> fixes)
    {
        if (Brand.IsBusy(button) || fixes.Count == 0) return;
        var from = s.State.ReadFrom;
        var what = FixAllText(fixes);
        var question = $"{what} in {from}?\n\nThis changes the model. Ctrl+Z in Revit undoes it." +
                       (from.IndexOf("detached", StringComparison.OrdinalIgnoreCase) >= 0 ? "" : "\n\nThis doesn't look like a detached copy: fixes are meant for the copy you submit.");
        if (MessageBox.Show(this, question, "Sketch ProMax: fix it", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Brand.SetBusy(button, true, "Fixing…");
        var project = _project;
        try
        {
            var said = new List<string>();
            foreach (var group in fixes.GroupBy(f => f.Fix!.Split(':')[0] == "uniformat" ? "uniformat" : f.Fix!))
            {
                if (_closed) break;
                said.Add(await App.Dispatcher.InvokeAsync(app =>
                {
                    var ui = app.ActiveUIDocument;
                    if (ui is null || !string.Equals(ui.Document.Title, from, StringComparison.OrdinalIgnoreCase))
                        return $"Bring {from} to the front in Revit first: these results were read from it.";
                    return BerthStatusFacts.RunFix(app, group.Key, ChecklistStore.Load(Settings.AgentSettings.Load(), project).Def);
                }));
                if (said[^1].StartsWith("Bring ", StringComparison.Ordinal)) break;
            }
            var text = string.Join(" ", said.Distinct().Take(6)) + (said.Count > 6 ? $" (and {said.Count - 6} more)." : "");
            ActivityLog.Note("berth_status_fix", $"{what}: {text}");
            _status.Text = text + " Press Update status to see the new status.";
            button.Content = "Done";
            button.IsEnabled = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = $"Couldn't fix it ({ex.Message})."; }
        finally { if (button.IsEnabled) Brand.SetBusy(button, false); }
    }

    /// <summary>Opens the view or sheet, or selects the element, when the berth's model is the one in front.</summary>
    private async Task Show(Summary s, long id)
    {
        var from = s.State.ReadFrom;
        _status.Text = await App.Dispatcher.InvokeAsync(app =>
        {
            var ui = app.ActiveUIDocument;
            if (ui is null || !string.Equals(ui.Document.Title, from, StringComparison.OrdinalIgnoreCase))
                return $"Bring {from} to the front in Revit first: these results were read from it.";
            var el = ui.Document.GetElement(new Autodesk.Revit.DB.ElementId(id));
            if (el is null) return "It isn't in the model any more.";
            if (el is Autodesk.Revit.DB.View v && !v.IsTemplate) { ui.RequestViewChange(v); return $"Opened {v.Name}."; }
            return ChecklistFixes.Select(ui, [id]);
        });
    }

    // ---- Update status (on demand only, in steps) --------------------------------------------------------------------

    private async Task UpdateStatus()
    {
        if (_reading || _setup is null) return;
        _reading = true;
        _stop = false;
        Brand.SetBusy(_update, true, "Reading…");
        _runStrip.Visibility = Visibility.Visible;
        Brand.SetBusy(_stopButton, false);
        var setup = _setup;
        var read = new List<string>();
        try
        {
            _runText.Text = "Finding the project's open models…";
            _runBar.Value = 0;
            // The open models of this project (not links, not families), each with its setup model.
            var plans = await App.Dispatcher.InvokeAsync(app =>
            {
                var list = new List<BerthStatusFacts.Reading>();
                foreach (Autodesk.Revit.DB.Document d in app.Application.Documents)
                    if (!d.IsFamilyDocument && !d.IsLinked && ModelFor(setup, DocName(d)) is { } m)
                        list.Add(BerthStatusFacts.Plan(d, setup, m));
                return list;
            });
            if (plans.Count == 0)
            {
                _status.Text = "None of the open models is in the setup (Models sheet, File name contains). Open a model of the project, then press Update status.";
                return;
            }
            var total = Math.Max(1, plans.Sum(p => p.Total));
            var clock = Stopwatch.StartNew();
            foreach (var plan in plans)
            {
                while (!_closed && !_stop && await App.Dispatcher.InvokeAsync(_ => plan.RunFor(StepBudget)))
                {
                    var done = plans.Sum(p => p.Total - p.Left);
                    _runBar.Value = 100.0 * done / total;
                    var step = plan.NextName.Split('|');
                    _runText.Text = $"Reading {plan.Model}: {(step.Length == 2 ? AreaOf(step[0]).Name + " of " + step[1] : step[0])} ({100 * done / total}%). Revit stays usable; Stop ends it.";
                }
                if (_closed || _stop) break;
                var now = DateTime.Now;
                foreach (var berth in plan.Berths)
                    Record(setup, berth, _state, plan.Results[berth.Key], plan.Model, now);
                RecordPackages(setup, _state, now);
                read.Add($"{plan.Model} ({plan.Berths.Count} berths)");
            }
            if (_closed) return;
            Save();
            ActivityLog.Note("berth_status_read", $"{_project}: {string.Join(", ", read)} in {clock.Elapsed.TotalSeconds:0.0} s; " + string.Join(" / ", plans.Select(BerthStatusFacts.Describe)));
            Refresh();
            _status.Text = _stop
                ? (read.Count == 0 ? "Stopped. The results are as they were." : "Stopped after " + string.Join(", ", read) + ".")
                : $"Updated {string.Join(", ", read)} at {DateTime.Now:HH:mm}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = $"Couldn't read the model ({ex.Message}). Finish what Revit is doing (an edit mode or a dialog), then try again.";
        }
        finally
        {
            _reading = false;
            Brand.SetBusy(_update, false);
            Brand.SetBusy(_stopButton, false);
            _runStrip.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Excel ----------------------------------------------------------------------------------------------------

    private string? Export(Summary s, bool open)
    {
        try
        {
            var now = DateTime.Now;
            var path = ExportPath(_project, s.Berth, now);
            OfficeXlsx.Write(path, $"{s.Berth.Name} status", BerthSheets(_setup!, s, now));
            _status.Text = $"Saved {Path.GetFileName(path)}.";
            if (open) Shell(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Couldn't write the Excel file (" + ex.Message + "). Close it in Excel and try again.";
            return null;
        }
    }

    private void ExportAll()
    {
        if (_setup is null) return;
        var written = Summaries().Select(s => Export(s, open: false)).OfType<string>().ToList();
        _status.Text = $"Saved {written.Count} berth files.";
        if (written.Count > 0) Shell("explorer.exe", $"/select,\"{written[0]}\"");
    }

    private void Shell(string file, string? args = null)
    {
        try { Process.Start(args is null ? new ProcessStartInfo(file) { UseShellExecute = true } : new ProcessStartInfo(file, args) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { _status.Text = $"Couldn't open {file} ({ex.Message})."; }
    }

    // ---- Small parts ----------------------------------------------------------------------------------------------

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
