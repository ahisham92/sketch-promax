using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.UI;
using SketchProMax.Settings;
using SketchProMax.Tools;
using static SketchProMax.Tools.BerthStatusMath;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using Visibility = System.Windows.Visibility;

namespace SketchProMax.UI;

/// <summary>
/// Make it BIM-ready: what the project's BEP and LOI still need in the open model, and one button that fills everything
/// the model can give (BimReadyPlan), each fix in its own short Revit call. The pre-submission checklist stays the checker;
/// this window reads the same checks and lists what is left for the user. With the project's Berth status set up, it also
/// reads the chosen berths (BerthStatusFacts: clashes, services through the structure, modelling, drawings, tags...),
/// joins the structural clashes and tags what is untagged on their sheets, and lists the rest of each berth for the user.
/// Opened from Berth status (for the chosen berths) and from the pre-submission checklist.
/// </summary>
internal sealed class BimReadyWindow : Window
{
    private static BimReadyWindow? _open;
    private static readonly TimeSpan StepBudget = TimeSpan.FromMilliseconds(200);
    private const int LoiTypesPerCall = 10;

    private readonly string _docKey;
    private readonly ChecklistMath.Definition? _def;
    private ChecklistFacts.Result? _facts;
    private bool _busy, _stop, _closed;
    private readonly List<string> _done = [];
    private readonly string _project;
    private readonly Setup? _setup;
    private readonly Model? _model;
    private readonly List<Berth> _berths = [];
    private readonly HashSet<string> _chosen = new(StringComparer.OrdinalIgnoreCase);
    private BerthStatusFacts.Reading? _berthRead;
    private readonly WrapPanel _berthPicks = new() { Margin = new Thickness(18, 10, 18, 0) };

    private readonly StackPanel _list = new() { Margin = new Thickness(18, 14, 18, 18) };
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted);
    private readonly Border _runStrip = new() { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 8, 18, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock _runText = Brand.Text("", size: 12.5, bold: true);
    private readonly ProgressBar _runBar = new() { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _make = RichUi.Button(RichUi.Icon.Fix, "Make it BIM-ready", Brand.Weight.Primary);
    private readonly Button _check = RichUi.Button(RichUi.Icon.Refresh, "Check again");
    private readonly Button _stopButton = RichUi.Button(RichUi.Icon.Close, "Stop");

    /// <param name="berths">The berth keys to make ready (Berth status's choice); null: every berth of the open model.</param>
    public static Result Command(UIApplication app, IEnumerable<string>? berths = null)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            TaskDialog.Show("Sketch ProMax", "Open the project model first: it is made BIM-ready by the rules of its project's checklist.");
            return Result.Cancelled;
        }
        var key = DocKey(doc);
        if (_open is { IsLoaded: true })
        {
            if (_open._docKey == key && berths is null) { _open.Activate(); return Result.Succeeded; }
            _open.Close();
        }
        var (def, _) = ChecklistStore.Load(AgentSettings.Load(), ChecklistProjects.ProjectKey(key));
        var window = new BimReadyWindow(key, def, Shown(doc), berths);
        _open = window;
        window.Closed += (_, _) => { if (_open == window) _open = null; };
        window.Show();
        // The model is read after the window is up, in short steps, so a big model never holds Revit.
        window.Dispatcher.BeginInvoke(new Action(async () => await window.Read()));
        return Result.Succeeded;
    }

    private static string DocKey(Autodesk.Revit.DB.Document doc)
    {
        try { return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName; }
        catch (Exception) { return doc.Title; }
    }

    private static string Shown(Autodesk.Revit.DB.Document doc)
    {
        try { return string.IsNullOrEmpty(doc.PathName) ? doc.Title : System.IO.Path.GetFileName(doc.PathName); }
        catch (Exception) { return doc.Title; }
    }

    private BimReadyWindow(string docKey, ChecklistMath.Definition? def, string model, IEnumerable<string>? berths)
    {
        _docKey = docKey;
        _def = def;
        // The project's Berth status setup, when it has one: this model's berths, one per berth of a package.
        _project = ChecklistProjects.ProjectKey(model);
        _setup = _project.Length == 0 ? null : LoadSetup(_project);
        _model = _setup is null ? null : ModelFor(_setup, model);
        if (_setup is not null && _model is not null) _berths.AddRange(Units(BerthsOf(_setup, _model)));
        var wanted = berths?.ToList();
        foreach (var b in _berths)
            if (wanted is null || wanted.Any(w => string.Equals(w, b.Key, StringComparison.OrdinalIgnoreCase) || b.Key.StartsWith(w + " / ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(KeyOf(b.Model, b.Parent), w, StringComparison.OrdinalIgnoreCase)))
                _chosen.Add(b.Key);
        Title = "Sketch ProMax: make it BIM-ready";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(860, Math.Floor(area.Width * 0.9));
        Height = Math.Min(820, Math.Floor(area.Height * 0.9));
        MinWidth = Math.Min(560, Width);
        MinHeight = Math.Min(420, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(AgentSettings.Load()));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) Close(); };
        Closed += (_, _) => { _closed = true; _stop = true; };

        var header = Brand.Header("Make it BIM-ready", model + " · by your project's BEP and LOI rules");
        DockPanel.SetDock(header, Dock.Top);

        _runStrip.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        _runStrip.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        var runGrid = new Grid();
        runGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        runGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        runGrid.Children.Add(new StackPanel { Children = { _runText, _runBar } });
        _stopButton.VerticalAlignment = VerticalAlignment.Center;
        _stopButton.Click += (_, _) => { _stop = true; Brand.SetBusy(_stopButton, true, "Stopping…"); };
        Grid.SetColumn(_stopButton, 1);
        runGrid.Children.Add(_stopButton);
        _runStrip.Child = runGrid;
        DockPanel.SetDock(_runStrip, Dock.Top);

        AutomationProperties.SetHelpText(_make, "Fills everything the BEP and LOI need that the model can give: units, UniFormat codes, worksets, the LOI data, names from the URS, title blocks, the model name on sheets, view templates, view folders, the Navisworks view and the starting view. Nothing is deleted. Then checks again.");
        _make.Click += async (_, _) => await MakeReady();
        AutomationProperties.SetHelpText(_check, "Reads the model again for what is still missing, in short steps.");
        _check.Click += async (_, _) => await Read();
        var checklist = RichUi.Button(RichUi.Icon.Open, "Open the checklist");
        AutomationProperties.SetHelpText(checklist, "The pre-submission checklist: the checker, with every check, its details and the signed Word file.");
        checklist.Click += async (_, _) =>
        {
            try { await App.Dispatcher.InvokeAsync(a => ChecklistWindow.Command(a)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = $"Couldn't open the checklist ({ex.Message}). Finish what Revit is doing, then try again."; }
        };
        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.Click += (_, _) => Close();
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var foot = new DockPanel { Margin = new Thickness(18, 10, 18, 12) };
        var buttons = Brand.ButtonRow(_check, checklist, _make, close);
        buttons.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        foot.Children.Add(_status);
        var footBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };
        footBorder.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        DockPanel.SetDock(footBorder, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(_runStrip);
        if (_berths.Count > 0)
        {
            var pickLabel = Brand.Text("Berths:", Brand.K.Text, 12.5, bold: true);
            pickLabel.Margin = new Thickness(0, 4, 10, 4);
            pickLabel.VerticalAlignment = VerticalAlignment.Center;
            _berthPicks.Children.Add(pickLabel);
            foreach (var b in _berths)
            {
                var box = new CheckBox { Content = BerthName(b), IsChecked = _chosen.Contains(b.Key), Margin = new Thickness(0, 4, 14, 4), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetHelpText(box, "Make this berth BIM-ready: its clashes, services, tags and what is left in it. Press Check again after changing the berths.");
                var key = b.Key;
                box.Checked += (_, _) => { _chosen.Add(key); _status.Text = "Press Check again to read the berths you chose."; };
                box.Unchecked += (_, _) => { _chosen.Remove(key); _status.Text = "Press Check again to read the berths you chose."; };
                _berthPicks.Children.Add(box);
            }
            DockPanel.SetDock(_berthPicks, Dock.Top);
            root.Children.Add(_berthPicks);
        }
        root.Children.Add(footBorder);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        Content = root;
        Fill();
    }

    // ---- What is left ---------------------------------------------------------------------------------------------

    private bool? Flagged(string key) =>
        _facts is null || !_facts.Hints.TryGetValue(key, out var hint) || hint.Pending ? null : hint.Level switch
        {
            ChecklistFacts.Level.Warn => true,
            ChecklistFacts.Level.Ok => false,
            _ => null,
        };

    private string CheckName(string key) =>
        _def?.Items.FirstOrDefault(i => i.Auto == key) is { } item ? item.Title : BimReadyPlan.Steps.FirstOrDefault(s => s.Key == key)?.What ?? key;

    /// <summary>Lays out the lists: what the button fills, what is left for you, and what passes.</summary>
    private void Fill()
    {
        _list.Children.Clear();
        _make.IsEnabled = !_busy && _def is not null;
        _check.IsEnabled = !_busy && _def is not null;
        if (_def is null)
        {
            _list.Children.Add(Card("No checklist for this project yet",
                "The BEP and LOI rules come from the pre-submission checklist: open it, import the project's checklist Excel (with its Checking rules), then come back.", Brand.K.WarningText));
            return;
        }
        if (_done.Count > 0) _list.Children.Add(Card("Done", string.Join("\n", _done), Brand.K.SuccessText));
        if (_facts is null)
        {
            _list.Children.Add(Card("Reading the model…", "What the BEP and LOI still need appears here. You can keep working in Revit meanwhile.", Brand.K.Text));
            return;
        }

        var plan = BimReadyPlan.ToRun(r => ChecklistMath.Rule(_def, r), Flagged);
        var warn = _facts.Hints.Where(h => !h.Value.Pending && h.Value.Level == ChecklistFacts.Level.Warn && _def.Items.Any(i => i.Auto == h.Key)).ToList();
        var mine = warn.Where(h => BimReadyPlan.Fixes(h.Key)).Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var yours = warn.Where(h => !BimReadyPlan.Fixes(h.Key)).ToList();
        var passing = _facts.Hints.Count(h => !h.Value.Pending && h.Value.Level == ChecklistFacts.Level.Ok);

        _list.Children.Add(Heading(plan.Count == 0 ? "Nothing for the button to fill" : $"Make it BIM-ready fills {plan.Count} thing{(plan.Count == 1 ? "" : "s")}"));
        foreach (var step in plan)
        {
            var text = _facts.Hints.TryGetValue(step.Key, out var hint) && mine.Contains(step.Key) ? hint.Text : "Fills whatever is still empty.";
            _list.Children.Add(Card(Cap(step.What), text, Brand.K.WarningText, hint?.Items));
        }
        // Flagged checks the button would fix but can't yet: their rule is empty.
        foreach (var key in mine.Where(k => plan.All(s => s.Key != k)))
        {
            var rule = BimReadyPlan.Steps.First(s => s.Key == key).Rule;
            var name = ChecklistMath.RuleDefs.FirstOrDefault(d => d.Key == rule)?.Label ?? rule;
            _list.Children.Add(Card(CheckName(key), _facts.Hints[key].Text + $"\n\nSet \"{name}\" in the checklist's Checking rules, and the button fills it.", Brand.K.WarningText));
        }

        _list.Children.Add(Heading(yours.Count == 0 ? "Nothing left for you" : $"Left for you: {yours.Count}"));
        foreach (var (key, hint) in yours.Select(h => (h.Key, h.Value)))
            _list.Children.Add(key == "warnings"
                // Each kind of warning with what to do about it (the off-axis and not-touching joins are the button's).
                ? Card(CheckName(key), hint.Text + " To reach zero, each kind needs:", Brand.K.Text, hint.Items.Select(i => i + " → " + CleanupMath.WarningHint(i)).ToList())
                : Card(CheckName(key), hint.Text + (ChecklistFixes.For(key, _def) is { } offer ? $"\n\nIn the checklist: {offer.Button}." : ""), Brand.K.Text, hint.Items));
        _list.Children.Add(Para($"{passing} automatic check{(passing == 1 ? "" : "s")} pass. The checklist has every check and its details."));
        FillBerths();
    }

    private static string BerthName(Berth b) => b.Parent.Length > 0 ? b.Parent + " / " + b.Name : b.Name;

    /// <summary>A berth finding's fix the button runs: joining a structural clash, tagging a view (the Assembly Codes and LOI are filled for the whole model).</summary>
    private static bool AutoFix(Finding f) => f.Fix is { } fix && (fix.StartsWith("join:", StringComparison.Ordinal) || fix.StartsWith("tag:", StringComparison.Ordinal));

    /// <summary>Each chosen berth: its clashes first (structural, then services through the structure), then the rest by area.</summary>
    private void FillBerths()
    {
        if (_berthRead is null) return;
        foreach (var b in _berthRead.Berths)
        {
            if (!_berthRead.Results.TryGetValue(b.Key, out var areas)) continue;
            _list.Children.Add(Heading(BerthName(b)));
            foreach (var key in new[] { Clashes, Coordination })
            {
                var r = areas.TryGetValue(key, out var got) ? got : null;
                var name = AreaOf(key).Name;
                if (r is null) { _list.Children.Add(Card(name, "Not read.", Brand.K.Muted)); continue; }
                var joins = r.Findings.Count(AutoFix);
                var rest = r.Findings.Where(f => !AutoFix(f)).ToList();
                var text = r.Findings.Count == 0 ? (r.Summary.Length > 0 ? r.Summary : "No clashes.") :
                    r.Summary + (joins > 0 ? $"\n\nMake it BIM-ready joins {joins} of them (no element moves; Revit cuts one with the other)." : "") +
                    (rest.Count > 0 ? $"\n\nLeft for you: {rest.Count}" + (key == Coordination ? " (add an opening or sleeve where each service passes, or move the service)." : " (move, cut or resize one of each pair).") : "");
                _list.Children.Add(Card(name, text, r.Findings.Count == 0 ? Brand.K.SuccessText : Brand.K.WarningText, rest.Select(f => $"{f.Item}: {f.Problem}" + (f.Id is { } id ? $" (id {id})" : "")).ToList()));
            }
            foreach (var area in Areas.Where(a => a.Key is not (Clashes or Coordination or Deliverables)))
            {
                if (!areas.TryGetValue(area.Key, out var r) || r.Findings.Count == 0) continue;
                var fixes = r.Findings.Count(AutoFix);
                var rest = r.Findings.Where(f => !AutoFix(f) && f.Fix is not ("uniformat" or "loi")).ToList();
                if (fixes == 0 && rest.Count == 0) continue;
                _list.Children.Add(Card(area.Name, r.Summary + (fixes > 0 ? $"\n\nMake it BIM-ready fixes {fixes} of these." : "") + (rest.Count > 0 ? $"\n\nLeft for you: {rest.Count}." : ""),
                    rest.Count > 0 ? Brand.K.WarningText : Brand.K.Text, rest.Select(f => $"{f.Item}: {f.Problem}").ToList()));
            }
        }
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s.Substring(1);

    private static TextBlock Heading(string text)
    {
        var t = Brand.Text(text, Brand.K.Text, 15, bold: true);
        t.Margin = new Thickness(0, 14, 0, 6);
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level2);
#endif
        return t;
    }

    private static TextBlock Para(string text)
    {
        var t = Brand.Text(text, Brand.K.Muted);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 12, 0, 0);
        return t;
    }

    private static Border Card(string title, string text, string titleKey, IReadOnlyList<string>? items = null)
    {
        var body = new StackPanel();
        body.Children.Add(Brand.Text(title, titleKey, 13, bold: true));
        var said = Brand.Text(text, Brand.K.Text);
        said.TextWrapping = TextWrapping.Wrap;
        said.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(said);
        if (items is { Count: > 0 })
        {
            var shown = Brand.Text(string.Join("\n", items.Take(8)) + (items.Count > 8 ? $"\n… and {items.Count - 8} more" : ""), Brand.K.Muted);
            shown.TextWrapping = TextWrapping.Wrap;
            shown.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(shown);
        }
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), Child = body };
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        return card;
    }

    // ---- Reading and filling --------------------------------------------------------------------------------------

    private void Running(string text, int percent)
    {
        _runStrip.Visibility = Visibility.Visible;
        _runText.Text = text;
        _runBar.Value = percent;
        _status.Text = text;
    }

    private void Ran()
    {
        _runStrip.Visibility = Visibility.Collapsed;
        Brand.SetBusy(_stopButton, false);
        _stop = false;
    }

    /// <summary>Reads the checklist's quick checks, a fifth of a second per Revit call, so Revit stays usable.</summary>
    private async Task Read()
    {
        if (_busy || _def is null) return;
        _busy = true;
        Fill();
        try
        {
            Running("Reading the model…", 0);
            var def = _def;
            var key = _docKey;
            var reading = await App.Dispatcher.InvokeAsync(app =>
            {
                foreach (Autodesk.Revit.DB.Document d in app.Application.Documents)
                    if (!d.IsFamilyDocument && !d.IsLinked && DocKey(d) == key) return ChecklistFacts.Plan(d, def);
                return null;
            });
            if (reading is null) { _status.Text = "This model isn't open any more."; return; }
            var total = Math.Max(1, reading.Left);
            while (!_closed && !_stop && await App.Dispatcher.InvokeAsync(_ => reading.RunFor(StepBudget)))
                Running("Reading the model… " + (100 * (total - reading.Left) / total) + "%", 100 * (total - reading.Left) / total);
            if (_closed) return;
            if (_stop) { _status.Text = "Stopped."; return; }
            _facts = reading.Result;
            _berthRead = null;
            if (_setup is not null && _model is not null && _chosen.Count > 0)
            {
                var setup = _setup;
                var model = _model;
                var chosen = _chosen.ToList();
                var berths = await App.Dispatcher.InvokeAsync(app =>
                {
                    foreach (Autodesk.Revit.DB.Document d in app.Application.Documents)
                        if (!d.IsFamilyDocument && !d.IsLinked && DocKey(d) == key) return BerthStatusFacts.Plan(d, setup, model, chosen, def);
                    return null;
                });
                if (berths is not null)
                {
                    var all = Math.Max(1, berths.Total);
                    while (!_closed && !_stop && await App.Dispatcher.InvokeAsync(_ => berths.RunFor(StepBudget)))
                    {
                        var step = berths.NextName.Split('|');
                        Running($"Reading the berths: {(step.Length == 2 ? AreaOf(step[0]).Name + " of " + step[1] : step[0])} ({100 * (all - berths.Left) / all}%)", 100 * (all - berths.Left) / all);
                    }
                    if (_closed) return;
                    if (_stop) { _status.Text = "Stopped."; return; }
                    _berthRead = berths;
                    // Berth status shows the same reading.
                    var state = LoadState(_project);
                    var now = DateTime.Now;
                    foreach (var b in berths.Berths) Record(setup, b, state, berths.Results[b.Key], berths.Model, now);
                    RecordPackages(setup, state, now);
                    try { SaveState(_project, state); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                }
            }
            _status.Text = "Checked " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + ".";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = $"Couldn't read the model ({ex.Message}). Finish what Revit is doing, then press Check again."; }
        finally
        {
            _busy = false;
            Ran();
            if (!_closed) Fill();
        }
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
                    return "Switch Revit to the model this is for, then try again.";
                return work(ui);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"Revit couldn't do it ({ex.Message}). Finish what Revit is doing (an edit mode or a dialog), then try again.";
        }
    }

    /// <summary>Runs every fix of the plan, each in its own Revit call (the LOI data a few types at a time), then reads again.</summary>
    private async Task MakeReady()
    {
        if (_busy || _def is null) return;
        if (_facts is null) await Read();
        if (_facts is null) return;
        var plan = BimReadyPlan.ToRun(r => ChecklistMath.Rule(_def, r), Flagged);
        var berthFixes = _berthRead is null ? [] : _berthRead.Results.SelectMany(b => b.Value.Values.SelectMany(a => a.Findings)).Where(AutoFix).Select(f => f.Fix!).Distinct().ToList();
        var joins = berthFixes.Count(f => f.StartsWith("join:", StringComparison.Ordinal));
        var tags = berthFixes.Count - joins;
        if (plan.Count == 0 && berthFixes.Count == 0) { _status.Text = "Nothing for the button to fill: the model already has what the rules ask for, or the rules aren't set."; return; }
        var detached = _facts.Hints.TryGetValue("links", out var links) && links.Text.StartsWith("This copy is detached", StringComparison.Ordinal);
        var parts = plan.Select(s => s.What).ToList();
        if (joins > 0) parts.Add($"join {joins} structural clash{(joins == 1 ? "" : "es")} in the berths");
        if (tags > 0) parts.Add($"tag what is untagged in {tags} view{(tags == 1 ? "" : "s")} on the berths' sheets");
        var question = "Fix " + string.Join(", ", parts) + "?\n\nValues already filled are left alone and nothing is deleted; off-axis lines move no more than 2 mm at an end. " +
                       "Each part is its own step in Revit's undo list." + (detached ? "" : "\n\nThis doesn't look like a detached copy: test on a copy first if you aren't sure.");
        if (MessageBox.Show(this, question, "Sketch ProMax: make it BIM-ready", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var def = _def;
        _busy = true;
        _done.Clear();
        Brand.SetBusy(_make, true, "Filling…");
        Fill();
        try
        {
            for (var i = 0; i < plan.Count && !_stop && !_closed; i++)
            {
                var step = plan[i];
                Running($"Filling {step.What}…", 100 * i / plan.Count);
                var said = step.Key == "loi" ? await FillLoi(def, i, plan.Count) : await OnModel(ui => ChecklistFixes.Run(ui, step.Key, def, step.Key == "viewfolders" ? "" : null));
                if (said.Length > 0) _done.Add("• " + said);
                if (said.StartsWith("Switch Revit", StringComparison.Ordinal)) break;
            }
            int joined = 0, tagged = 0;
            for (var i = 0; i < berthFixes.Count && !_stop && !_closed; i++)
            {
                var fix = berthFixes[i];
                Running($"Fixing the berths: {i + 1} of {berthFixes.Count}…", 100 * i / berthFixes.Count);
                string said;
                try { said = await App.Dispatcher.InvokeAsync(app => app.ActiveUIDocument is { } ui && DocKey(ui.Document) == _docKey ? BerthStatusFacts.RunFix(app, fix, def) : "Switch Revit to the model this is for, then try again."); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { said = ex.Message; }
                if (said.StartsWith("Switch Revit", StringComparison.Ordinal)) { _done.Add("• " + said); break; }
                if (said.StartsWith("Joined", StringComparison.Ordinal)) joined++;
                else if (said.StartsWith("Tagged", StringComparison.Ordinal)) tagged++;
            }
            if (joins > 0) _done.Add($"• Joined {joined} of {joins} structural clashes" + (joins > joined ? "; the rest couldn't be joined (move or cut one of each pair: listed under each berth)." : "."));
            if (tags > 0) _done.Add($"• Tagged what was untagged in {tagged} of {tags} views.");
            if (_stop) _done.Add("• Stopped: what was filled before stays.");
            ActivityLog.Note("bim_ready", string.Join(" ", _done));
        }
        finally
        {
            _busy = false;
            Brand.SetBusy(_make, false);
            Ran();
        }
        await Read();
    }

    /// <summary>The LOI data the model gives (LoiFill), ten types per Revit call.</summary>
    private async Task<string> FillLoi(ChecklistMath.Definition def, int at, int steps)
    {
        var spec = LoiFill.SpecOf(def);
        var ids = await OnModel(ui => "ids:" + string.Join(",", LoiFill.TypeIds(ui.Document, spec)));
        if (!ids.StartsWith("ids:", StringComparison.Ordinal)) return ids;
        var list = ids.Substring(4).Split([','], StringSplitOptions.RemoveEmptyEntries).Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToList();
        int filled = 0;
        for (var i = 0; i < list.Count && !_stop && !_closed; i += LoiTypesPerCall)
        {
            Running($"Filling the LOI data: {i} of {list.Count} types…", (100 * at + 100 * i / Math.Max(1, list.Count)) / steps);
            var batch = list.Skip(i).Take(LoiTypesPerCall).ToList();
            var part = await OnModel(ui => LoiFill.AutoAnswer(ui.Document, spec, batch) is { Length: > 0 } answer ? LoiFill.Apply(ui.Document, spec, answer) : "");
            if (part.StartsWith("Filled", StringComparison.Ordinal)) filled++;
            else if (part.StartsWith("Switch Revit", StringComparison.Ordinal) || part.StartsWith("Revit couldn't", StringComparison.Ordinal)) return part;
        }
        return list.Count == 0 ? "The LOI data: nothing missing." :
            filled == 0 ? $"The LOI data: {list.Count} types checked, nothing the model can give is missing (the rest is yours: Export the BEP data Excel in the checklist)." :
            $"The LOI data: filled what the model gives on the {list.Count} types in use.";
    }
}
