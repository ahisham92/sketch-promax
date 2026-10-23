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
    private bool _reading, _stop, _closed;
    private string _savedText = "";

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
        if (_open is { IsLoaded: true })
        {
            if (_open._project == project || project.Length == 0) { _open.Activate(); return Result.Succeeded; }
            _open.Close();
        }
        var window = new BerthStatusWindow(project);
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
        Width = Math.Min(1280, Math.Floor(area.Width * 0.9));
        Height = Math.Min(800, Math.Floor(area.Height * 0.9));
        MinWidth = Math.Min(860, Width);
        MinHeight = Math.Min(480, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        var motion = RichUi.MotionOf(Settings.AgentSettings.Load());
        RichUi.Apply(this, motion);
        _ring = new ScoreRing("All berths", motion);
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
            $"{(_setup.Title.Length > 0 ? _setup.Title : "Berth status")} · {_project} · {_setup.Berths.Count} berths in {_setup.Models.Count} models";
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
        var exportAll = RichUi.Button(RichUi.Icon.Export, "Export all berths");
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
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
        return _setup!.Berths.Select(b => Summarize(_setup, b, _state, now)).ToList();
    }

    /// <summary>Redraws the list and the selected berth's details from the setup and the saved results.</summary>
    private void Refresh()
    {
        if (_setup is null) return;
        var all = Summaries();
        _list.Children.Clear();
        var known = all.Where(s => s.Percent is not null).ToList();
        double? overall = known.Count == 0 ? null : known.Average(s => s.Percent!.Value);
        _ring.Show(overall is null ? null : (int)Math.Round(overall.Value), Colors.White, "The average status of the berths that have been read");
        foreach (var model in all.GroupBy(s => s.Berth.Model, StringComparer.OrdinalIgnoreCase))
        {
            var title = Brand.Text(model.Key, Brand.K.Muted, 12, bold: true);
            title.Margin = new Thickness(2, _list.Children.Count == 0 ? 0 : 14, 0, 6);
            _list.Children.Add(title);
            foreach (var s in model) _list.Children.Add(Card(s));
        }
        if (_selected.Length == 0 || all.All(s => s.Berth.Key != _selected)) _selected = all.FirstOrDefault()?.Berth.Key ?? "";
        ShowDetail(all.FirstOrDefault(s => s.Berth.Key == _selected));
    }

    private FrameworkElement Card(Summary s)
    {
        var selected = s.Berth.Key == _selected;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel();
        texts.Children.Add(Brand.Text(s.Berth.Name, size: 14, bold: true));
        var due = s.Next is null ? (s.Last is { } last ? $"Submitted: {last.Name}{When(last, "dd-MMM", ", ")}" : "No submission set") : $"{s.Next.Name}: {s.Due:ddd dd-MMM HH:mm}" + (s.Due > DateTime.Now ? $" · {Math.Round(s.HoursLeft):0} working h left" : "");
        texts.Children.Add(Small(due));
        texts.Children.Add(Small("Expected finish: " + (s.Finish is { } f ? f.ToString("ddd dd-MMM HH:mm", CultureInfo.CurrentCulture) + (s.FinishByHand ? " (by hand)" : "") : "needs two readings")));
        texts.Children.Add(Chip(s.Verdict));
        grid.Children.Add(texts);
        var pct = Brand.Text(Pct(s.Percent).Replace("not measured", "–"), PercentKey(s.Percent), 22, bold: true);
        pct.VerticalAlignment = VerticalAlignment.Center;
        pct.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(pct, 1);
        grid.Children.Add(pct);
        var bar = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Value = s.Percent ?? 0, Margin = new Thickness(0, 8, 0, 0) };
        var stack = new StackPanel { Children = { grid, bar } };
        var card = new Border
        {
            Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(selected ? 2 : 1), Child = stack, Cursor = Cursors.Hand, Focusable = true,
        };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Bg);
        card.SetResourceReference(Border.BorderBrushProperty, selected ? Brand.K.Accent : Brand.K.Border);
        AutomationProperties.SetName(card, $"{s.Berth.Name}, {Pct(s.Percent)}, {Text(s.Verdict)}");
        void Pick() { _selected = s.Berth.Key; Refresh(); }
        card.MouseLeftButtonUp += (_, _) => Pick();
        card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Pick(); e.Handled = true; } };
        return card;
    }

    private static string PercentKey(double? p) => p is null ? Brand.K.Muted : p >= 90 ? Brand.K.SuccessText : p >= 60 ? Brand.K.WarningText : Brand.K.DangerText;

    private static FrameworkElement Chip(Verdict v)
    {
        var key = v switch
        {
            Verdict.Done or Verdict.OnTrack => Brand.K.BadgeOk,
            Verdict.AtRisk or Verdict.Submitted => Brand.K.BadgeWarn,
            Verdict.Behind or Verdict.Overdue => Brand.K.BadgeError,
            _ => Brand.K.BadgeMuted,
        };
        var chip = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = Brand.Text(Text(v), Brand.K.OnAccent, 11, bold: true) };
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
        var title = new DockPanel();
        var pct = Brand.Text(Pct(s.Percent), PercentKey(s.Percent), 26, bold: true);
        DockPanel.SetDock(pct, Dock.Right);
        title.Children.Add(pct);
        title.Children.Add(new StackPanel { Children = { Heading($"{s.Berth.Name}"), Small($"{s.Berth.Model} · " + (b.ReadAt is { } at ? $"read {at:ddd dd-MMM HH:mm} from {b.ReadFrom}" : "not read yet: open its model and press Update status")) } });
        page.Children.Add(title);
        page.Children.Add(Chip(s.Verdict));

        // The submission, the time left and the expected finish (from the readings, by hand, or asked of the AI).
        var sub = new StackPanel();
        sub.Children.Add(Brand.Text("Next submission", size: 13, bold: true));
        if (s.Next is null) sub.Children.Add(Para(s.Last is { } last
            ? $"Submitted: {last.Name}{When(last, "dddd dd-MMM-yyyy", ", ")}. It is still checked: what is below to fix belongs to the BIM submission."
            : "No submission for this berth in the setup's Submissions sheet."));
        else
        {
            sub.Children.Add(Para($"{s.Next.Name}: due {s.Due:dddd dd-MMM-yyyy HH:mm}" + (s.Due > DateTime.Now ? $", {s.HoursLeft:0} working hours left" : ", overdue") + "."));
            if (s.Percent is { } p && s.Due is { } due && NeededPerDay(p, due, DateTime.Now, _setup!.Week) is { } need)
                sub.Children.Add(Para($"Needs about {need:0.#}% a working day to make it."));
        }
        var forecast = s.Percent is null ? null : Forecast(b.History, s.Percent.Value, DateTime.Now, _setup!.Week);
        sub.Children.Add(Para("From the readings: " + (forecast is { } fc ? $"finishes {fc:ddd dd-MMM HH:mm}." : "needs two readings with progress between them (press Update status again after some work).")));
        var byHand = new DatePicker { SelectedDate = b.FinishByHand?.Date, Width = 140, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(byHand, "Expected finish by hand");
        var handTime = new TextBox { Text = b.FinishByHand is { } fh ? fh.ToString("HH:mm", CultureInfo.InvariantCulture) : "17:00", Width = 60, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(handTime, "Time");
        void SetHand()
        {
            b.FinishByHand = byHand.SelectedDate is { } d ? d.Date + (ParseTime(handTime.Text) ?? TimeSpan.FromHours(17)) : null;
            Save();
            Refresh();
        }
        byHand.SelectedDateChanged += (_, _) => SetHand();
        handTime.LostFocus += (_, _) => { if (byHand.SelectedDate is not null) SetHand(); };
        var clear = RichUi.Button(RichUi.Icon.Undo, "Use the readings");
        clear.Click += (_, _) => { b.FinishByHand = null; Save(); Refresh(); };
        var ask = RichUi.Button(RichUi.Icon.Ask, "Ask Sketch ProMax for an estimate");
        AutomationProperties.SetHelpText(ask, "Sends this berth's status, schedule and history to the Sketch ProMax chat, which estimates the finish. Type its date by hand if you agree.");
        ask.Click += (_, _) =>
        {
            var problem = Chat.ChatPane.Ask(Question(_setup!, s, DateTime.Now));
            _status.Text = problem.Length > 0 ? problem : "Asked the Sketch ProMax chat (in the pane): put its date in Expected finish by hand if you agree.";
        };
        var handRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        handRow.Children.Add(new TextBlock { Text = "Expected finish by hand:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        handRow.Children.Add(byHand);
        handRow.Children.Add(handTime);
        handRow.Children.Add(clear);
        handRow.Children.Add(ask);
        clear.Margin = ask.Margin = new Thickness(0, 4, 8, 4);
        sub.Children.Add(handRow);
        page.Children.Add(Box(sub));

        // The areas.
        page.Children.Add(Section("Status by area"));
        foreach (var a in s.Scores) page.Children.Add(AreaRow(s, a));

        // Deliverables.
        page.Children.Add(Section("Deliverables"));
        if (s.Deliverables.Count == 0) page.Children.Add(Para("None in the setup (Deliverables sheet).", Brand.K.Muted));
        foreach (var d in s.Deliverables) page.Children.Add(DeliverableRow(s, d));

        // The berth's comment, and its Excel.
        page.Children.Add(Section("Comments"));
        var comment = new TextBox { Text = b.Comment, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        AutomationProperties.SetName(comment, "Comment on the berth");
        comment.LostFocus += (_, _) => { if (b.Comment != comment.Text) { b.Comment = comment.Text; Save(); } };
        page.Children.Add(comment);
        var export = RichUi.Button(RichUi.Icon.Export, "Export this berth to Excel", Brand.Weight.Primary);
        AutomationProperties.SetHelpText(export, "Status of every area, submissions, deliverables, what to fix per area, comments and history, one sheet each.");
        export.Click += (_, _) => Export(s, open: true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { export } };
        page.Children.Add(actions);
    }

    private FrameworkElement AreaRow(Summary s, AreaScore a)
    {
        var row = new StackPanel();
        var top = new DockPanel();
        var value = Brand.Text(Pct(a.Percent), PercentKey(a.Percent), 13, bold: true);
        DockPanel.SetDock(value, Dock.Right);
        top.Children.Add(value);
        top.Children.Add(Brand.Text($"{a.Area.Name}  ·  weight {a.Weight:0.#}" + (a.From == Source.Hand ? "  ·  by hand" : a.From == Source.Files ? "  ·  from files" : ""), size: 13, bold: true));
        row.Children.Add(top);
        row.Children.Add(new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Value = a.Percent ?? 0, Margin = new Thickness(0, 5, 0, 2) });
        row.Children.Add(Small(a.Result?.Summary is { Length: > 0 } sum ? sum : a.Area.Measures));
        AutomationProperties.SetName(row, $"{a.Area.Name}: {Pct(a.Percent)}");

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
        var label = Small("By hand %:");
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

        // What to fix, folded.
        if (a.Result is { Findings.Count: > 0 } r && a.Area.Key != Deliverables)
        {
            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var f in r.Findings.Take(40)) list.Children.Add(FindingRow(s, f));
            if (r.Findings.Count > 40) list.Children.Add(Small($"…and {r.Findings.Count - 40} more in the Excel export."));
            var fold = new Expander { Header = $"What to fix ({r.Findings.Count})", Content = list, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(fold);
        }
        return Box(row, top: 8);
    }

    private FrameworkElement FindingRow(Summary s, Finding f)
    {
        var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
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
        var hand = s.State.DeliveredFor(d.Deliverable.Name);
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
