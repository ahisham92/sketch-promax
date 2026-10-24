using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Commands;
using SketchProMax.Tools;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace SketchProMax.UI;

/// <summary>
/// The zero-mistake issue window: the issue check's pass/fail list per sheet (and Model), with Fix on each item (selects
/// and shows it in Revit; the window closes so the user can fix it, and Issue check runs again after), Accept for the
/// ticked items with a reason (kept with the model), Re-check, and Issue, which stays off until nothing is open (or, when
/// Settings allows it, with "issue with open items" ticked and a reason, recorded in the issue record).
///
/// Modal, inside the ribbon command, so Re-check and Accept run on Revit's thread. It opens from the Issue check button,
/// and before Issue drawings or Export PDF of sheets from the ribbon when the check finds open items
/// (<see cref="Before"/>); the chat gets the same list from issue_check.
/// </summary>
internal sealed class IssueGateWindow : Window
{
    internal enum Choice { Close, Issue, Fix }

    private const int MaxRowsPerGroup = 300;

    private readonly UIApplication _app;
    private readonly Func<IssueGate.Run?> _recheck;
    private readonly bool _overrideAllowed;
    private IssueGate.Run _run;
    private readonly StackPanel _list = new();
    private readonly TextBlock _verdict = Brand.Text("", bold: true, size: 13);
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted);
    private readonly TextBox _reason = new() { MinHeight = 28 };
    private readonly CheckBox _override = new() { Content = "Submit with open items (recorded in the submission record with the reason)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _accept = Brand.Button("_Accept ticked");
    private readonly Button _recheckButton = Brand.Button("_Re-check");
    private readonly Button _issue;
    private readonly List<(CheckBox Box, IssueGateMath.Item Item)> _ticks = [];

    public Choice Result { get; private set; } = Choice.Close;
    public string? OverrideReason { get; private set; }
    public long FixId { get; private set; }

    private IssueGateWindow(UIApplication app, IssueGate.Run run, Func<IssueGate.Run?> recheck, string primary)
    {
        _app = app;
        _run = run;
        _recheck = recheck;
        _overrideAllowed = IssueGate.OverrideAllowed;
        _issue = Brand.Button(primary, Brand.Weight.Primary);

        Title = "Sketch ProMax: submit check";
        Width = 760;
        Height = 680;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var body = new DockPanel { Margin = new Thickness(18, 12, 18, 14) };
        var top = new StackPanel();
        AutomationProperties.SetLiveSetting(_verdict, AutomationLiveSetting.Assertive);
        top.Children.Add(_verdict);
        var help = Brand.Text("Fix takes you to the item in Revit and closes this window: fix it, then click Submit check again. " +
                              "Or tick items and accept them with a reason; they won't be asked again for this model unless the finding changes.", Brand.K.Muted);
        help.Margin = new Thickness(0, 4, 0, 0);
        top.Children.Add(help);
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);

        var foot = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var reasonLabel = Brand.Text("Reason (for Accept, and for issuing with open items)");
        foot.Children.Add(reasonLabel);
        AutomationProperties.SetName(_reason, "Reason");
        AutomationProperties.SetHelpText(_reason, "Why the ticked items are acceptable. Kept with the model and shown in the submission record.");
        _reason.TextChanged += (_, _) => Refresh();
        foot.Children.Add(_reason);
        if (_overrideAllowed)
        {
            AutomationProperties.SetHelpText(_override, "Allowed in Settings. The submission record says it went with open items, and why.");
            _override.Checked += (_, _) => Refresh();
            _override.Unchecked += (_, _) => Refresh();
            foot.Children.Add(_override);
        }
        _status.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        foot.Children.Add(_status);

        _accept.ToolTip = "Accept every ticked item with the reason above.";
        AutomationProperties.SetHelpText(_accept, "Accepts the ticked items with the reason. They stop blocking the submission.");
        _accept.Click += (_, _) => AcceptTicked();
        _recheckButton.ToolTip = "Run the four checks again.";
        AutomationProperties.SetHelpText(_recheckButton, "Runs check_drawings, spec_check, boq_check and check_model again on the sheets to submit.");
        _recheckButton.Click += (_, _) => Recheck();
        var close = Brand.Button("Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        AutomationProperties.SetHelpText(_issue, "Goes on with the submission. Off while any item is open.");
        _issue.Click += (_, _) => Issue();
        foot.Children.Add(Brand.ButtonRow(_accept, _recheckButton, close, _issue));
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(foot);

        AutomationProperties.SetName(_list, "Pass or fail per sheet");
        body.Children.Add(new ScrollViewer
        {
            Content = _list, Margin = new Thickness(0, 10, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        var root = new DockPanel();
        var header = Brand.Header("Submit check", $"{run.Sheets.Count} sheet(s) of '{run.Model}': drawings, spec, BOQ and model");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;

        Fill();
        Loaded += (_, _) => (_ticks.Count > 0 ? (UIElement)_ticks[0].Box : _recheckButton).Focus();
    }

    // ---- The list ----------------------------------------------------------------------------------------------------

    private void Fill()
    {
        _list.Children.Clear();
        _ticks.Clear();
        foreach (var g in _run.Groups.Where(g => g.Group != IssueGateMath.ModelGroup || g.Items.Count > 0))
        {
            var head = new DockPanel { Margin = new Thickness(0, _list.Children.Count == 0 ? 0 : 12, 0, 4), MinHeight = 24 };
            var badge = Brand.Text(g.Pass ? (g.AcceptedCount > 0 ? "PASS (accepted)" : "PASS") : $"FAIL: {g.Open} open", g.Pass ? Brand.K.SuccessText : Brand.K.DangerText, bold: true);
            badge.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(badge, Dock.Right);
            head.Children.Add(badge);
            var openItems = g.Items.Where(i => i.Status == IssueGateMath.Status.Open).ToList();
            if (openItems.Count > 1)
            {
                var all = new CheckBox { Content = Brand.Text(g.Title, bold: true, size: 13), VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(all, $"Tick every open item of {g.Title}");
                var first = _ticks.Count;
                all.Checked += (_, _) => SetTicks(first, Math.Min(openItems.Count, MaxRowsPerGroup), true);
                all.Unchecked += (_, _) => SetTicks(first, Math.Min(openItems.Count, MaxRowsPerGroup), false);
                head.Children.Add(all);
            }
            else head.Children.Add(Brand.Text(g.Title, bold: true, size: 13));
            var heading = (FrameworkElement)head.Children[head.Children.Count - 1];
#if !NETFRAMEWORK
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
            AutomationProperties.SetName(head, $"{g.Title}: {(g.Pass ? "pass" : $"fail, {g.Open} open")}");
            _list.Children.Add(head);
            if (g.Items.Count == 0)
            {
                var none = Brand.Text("Nothing found by the four checks.", Brand.K.Muted);
                none.Margin = new Thickness(0, 0, 0, 4);
                _list.Children.Add(none);
                continue;
            }
            // Open first (in the order the tick-all box counts them), then accepted.
            foreach (var item in openItems.Concat(g.Items.Where(i => i.Status == IssueGateMath.Status.Accepted)).Take(MaxRowsPerGroup))
                _list.Children.Add(Row(g, item));
            if (g.Items.Count > MaxRowsPerGroup)
            {
                var more = Brand.Text($"{g.Items.Count - MaxRowsPerGroup} more in the Excel report. Tick the group to accept all of its open items.", Brand.K.Muted);
                _list.Children.Add(more);
            }
        }
        Refresh();
    }

    private Border Row(IssueGateMath.GroupResult g, IssueGateMath.Item item)
    {
        var f = item.Finding;
        var open = item.Status == IssueGateMath.Status.Open;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = $"{f.Tool}: {f.Check}" + (f.What.Length > 0 ? $" · {f.What}" : "") + (f.ElementId > 0 ? $" · id {f.ElementId}" : "");
        var text = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
        text.Children.Add(Brand.Text(f.Issue, open ? Brand.K.Text : Brand.K.Muted));
        text.Children.Add(Brand.Text(label + (open ? "" : $" · accepted: {item.Acceptance?.Reason} ({item.Acceptance?.By}, {item.Acceptance?.At})"), Brand.K.Muted, size: 11));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (open)
        {
            var box = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
            AutomationProperties.SetName(box, $"Tick: {f.Issue}");
            box.Checked += (_, _) => Refresh();
            box.Unchecked += (_, _) => Refresh();
            grid.Children.Add(box);
            _ticks.Add((box, item));
        }
        else
        {
            var done = Brand.Text("✓", Brand.K.SuccessText, bold: true);
            done.Margin = new Thickness(4, 0, 4, 0);
            AutomationProperties.SetName(done, "Accepted");
            grid.Children.Add(done);
        }
        // Fix: the element, or the sheet for a finding about the sheet itself.
        var target = f.ElementId > 0 ? f.ElementId : 0;
        if (open && target > 0)
        {
            var fix = Brand.Button("Fix");
            fix.MinWidth = 56;
            fix.MinHeight = 24;
            fix.Padding = new Thickness(8, 0, 8, 0);
            fix.VerticalAlignment = VerticalAlignment.Top;
            fix.ToolTip = "Select it and show it in Revit. This window closes; click Submit check again after fixing it.";
            AutomationProperties.SetName(fix, $"Fix: {f.Issue}");
            fix.Click += (_, _) => { FixId = target; Result = Choice.Fix; Close(); };
            Grid.SetColumn(fix, 2);
            grid.Children.Add(fix);
        }
        var row = new Border { Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = grid, MinHeight = 24 };
        row.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        row.SetResourceReference(Border.BorderBrushProperty, open ? Brand.K.Border : Brand.K.BorderSubtle);
        AutomationProperties.SetName(row, $"{(open ? "Open" : "Accepted")}: {f.Issue} ({g.Title})");
        return row;
    }

    private void SetTicks(int first, int count, bool on)
    {
        for (var i = first; i < Math.Min(_ticks.Count, first + count); i++) _ticks[i].Box.IsChecked = on;
        Refresh();
    }

    /// <summary>The ticked items; a ticked group also counts its open items beyond the rows shown.</summary>
    private List<IssueGateMath.Item> Ticked()
    {
        var items = _ticks.Where(t => t.Box.IsChecked == true).Select(t => t.Item).ToList();
        foreach (var g in _run.Groups)
        {
            var open = g.Items.Where(i => i.Status == IssueGateMath.Status.Open).ToList();
            if (open.Count > MaxRowsPerGroup && open.Take(MaxRowsPerGroup).All(i => items.Contains(i))) items.AddRange(open.Skip(MaxRowsPerGroup));
        }
        return items;
    }

    private void Refresh()
    {
        var blocked = _run.Blocked;
        _verdict.Text = IssueGateMath.Verdict(_run.Groups);
        _verdict.SetResourceReference(TextBlock.ForegroundProperty, blocked ? Brand.K.DangerText : Brand.K.SuccessText);
        var ticked = _ticks.Count(t => t.Box.IsChecked == true);
        var reason = _reason.Text.Trim().Length > 0;
        _accept.IsEnabled = ticked > 0 && reason;
        _accept.Content = ticked > 0 ? $"_Accept {ticked} ticked" : "_Accept ticked";
        _override.Visibility = _overrideAllowed && blocked ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        var overriding = _overrideAllowed && blocked && _override.IsChecked == true;
        _issue.IsEnabled = !blocked || (overriding && reason);
        // Going ahead with open items is the risky path: red, never the default (Enter doesn't do it).
        _issue.SetResourceReference(StyleProperty, overriding ? "SpmDangerButton" : "SpmPrimaryButton");
        _issue.IsDefault = !blocked;
        _status.Text = !blocked ? "Nothing is open. Submit goes on with the submission."
            : ticked > 0 && !reason ? "Type a reason, then Accept."
            : overriding && !reason ? "Type why the submission goes ahead with open items; it goes into the submission record."
            : $"{_run.Open.Count()} open: fix each one (Fix, then Submit check again) or tick it and accept it with a reason."
              + (_overrideAllowed ? "" : " Submitting with open items is off in Settings.");
    }

    // ---- Actions -----------------------------------------------------------------------------------------------------

    private void AcceptTicked()
    {
        var items = Ticked();
        var reason = _reason.Text.Trim();
        if (items.Count == 0 || reason.Length == 0) return;
        try
        {
            var n = IssueGate.Accept(_app.ActiveUIDocument.Document, _run, items, reason);
            Fill();
            _status.Text = $"Accepted {n} item(s) with the reason. They won't be asked again for this model unless the finding changes.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = $"Couldn't save the acceptance ({ex.Message}). Check that %APPDATA%\\SketchProMax can be written to, then try again.";
        }
    }

    private void Recheck()
    {
        if (Brand.IsBusy(_recheckButton)) return;
        Brand.SetBusy(_recheckButton, true, "Checking…");
        _issue.IsEnabled = false;
        // Let the spinner draw before the checks hold Revit's thread.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                if (_recheck() is { } run) { _run = run; Fill(); _status.Text = "Checked again. " + _status.Text; }
                else _status.Text = "The check didn't finish (stopped, or an error was shown). The list is from the last check.";
            }
            finally { Brand.SetBusy(_recheckButton, false); Refresh(); }
        }));
    }

    private void Issue()
    {
        var (allowed, used, why) = IssueGateMath.CanIssue(_run.Groups, _overrideAllowed, _override.IsChecked == true, _reason.Text);
        if (!allowed) { _status.Text = why; return; }
        OverrideReason = used ? _reason.Text.Trim() : null;
        Result = Choice.Issue;
        Close();
    }

    // ---- From the ribbon ---------------------------------------------------------------------------------------------

    /// <summary>The Issue check button: checks the issue set (the sheets Issue drawings used last, or every sheet) and shows the list.</summary>
    public static Autodesk.Revit.UI.Result Command(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            TaskDialog.Show("Sketch ProMax", "Open a project model first, then click Submit check again.");
            return Autodesk.Revit.UI.Result.Cancelled;
        }
        if (Jobs.Running is { } running)
        {
            TaskDialog.Show("Sketch ProMax", $"Revit is still running {running.Tool}. Wait for it to finish, or click the Job button to stop it, then try again.");
            return Autodesk.Revit.UI.Result.Cancelled;
        }
        var (numbers, prefix) = RememberedSet();
        var run = RunGate(app, numbers, prefix, null, null);
        if (run is null) return Autodesk.Revit.UI.Result.Cancelled;
        var window = new IssueGateWindow(app, run, () => RunGate(app, numbers, prefix, null, null), "_Submit…");
        window.ShowDialog();
        switch (window.Result)
        {
            case Choice.Fix: return Fix(app, window.FixId);
            case Choice.Issue:
                IssueGate.Clear(window._run, window.OverrideReason);
                // Cancelling the Issue drawings form must not leave the cleared check for a later call to pick up.
                try { return RibbonTools.Run(app, "issue_drawings"); }
                finally { IssueGate.Discard(); }
            default: return Autodesk.Revit.UI.Result.Cancelled;
        }
    }

    /// <summary>The tools the ribbon runs only after the issue check: issuing, and exporting sheets to PDF.</summary>
    public static bool Gated(string tool) => tool is "issue_drawings" or "export_pdf";

    /// <summary>
    /// Before Issue drawings or Export PDF from the ribbon, with the options the user chose: runs the issue check on
    /// those sheets; when it is clear the tool goes ahead, otherwise the window decides. True: go ahead.
    /// </summary>
    public static bool Before(UIApplication app, string tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        var doc = app.ActiveUIDocument.Document;
        var a = new ToolArgs(args);
        List<string> numbers;
        string? prefix = null;
        if (tool == "export_pdf")
        {
            numbers = a.LongList("view_ids").Select(id => doc.GetElement(new ElementId(id))).OfType<ViewSheet>().Select(s => s.SheetNumber).ToList();
            if (numbers.Count == 0) return true; // views, not sheets: not an issue set
        }
        else
        {
            numbers = a.StringList("sheet_numbers");
            prefix = a.OptString("number_starts_with");
        }
        var set = IssueGate.Sheets(doc, numbers, prefix).Select(s => s.SheetNumber).ToList();
        if (set.Count == 0) return true; // the tool says no sheets match
        // A run the Issue check window just cleared for exactly these sheets is used as it is.
        if (tool == "issue_drawings" && IssueGate.TakeCleared(doc, set) is { } done)
        {
            IssueGate.Clear(done.Run, done.OverrideReason);
            return true;
        }
        var spec = a.OptString("spec_checklist");
        var boq = a.OptString("boq_file");
        var run = RunGate(app, set, null, spec, boq);
        if (run is null) return false;
        if (!run.Blocked)
        {
            if (tool == "issue_drawings") IssueGate.Clear(run, null);
            return true;
        }
        var window = new IssueGateWindow(app, run, () => RunGate(app, set, null, spec, boq), tool == "export_pdf" ? "_Export…" : "_Submit…");
        window.ShowDialog();
        switch (window.Result)
        {
            case Choice.Issue:
                if (tool == "issue_drawings") IssueGate.Clear(window._run, window.OverrideReason);
                return true;
            case Choice.Fix:
                Fix(app, window.FixId);
                return false;
            default:
                return false;
        }
    }

    /// <summary>Runs issue_check as a ribbon job (progress window with Stop). Null when it failed or was stopped (already said).</summary>
    private static IssueGate.Run? RunGate(UIApplication app, IReadOnlyCollection<string> numbers, string? prefix, string? spec, string? boq)
    {
        var args = new Dictionary<string, object?> { ["sheet_numbers"] = numbers.ToArray(), ["number_starts_with"] = prefix, ["spec_checklist"] = spec, ["boq_file"] = boq };
        var json = JsonSerializer.SerializeToElement(args.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
        var dict = json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        IssueGate.Last = null;
        ToolResult result;
        using (PackProgress.Show("Submit check", $"Submit check on '{app.ActiveUIDocument.Document.Title}'"))
            result = QuickTools.Execute(app, "issue_check", dict);
        if (result.IsError || IssueGate.Last is null)
        {
            QuickTools.Show("Submit check", result, result.Text.StartsWith("Stopped", StringComparison.Ordinal) ? "Stopped; nothing was submitted" : "The submit check did not run");
            return null;
        }
        return IssueGate.Last;
    }

    /// <summary>The sheets Issue drawings was last run on from the ribbon (its remembered options), else every sheet.</summary>
    private static (List<string> Numbers, string? Prefix) RememberedSet()
    {
        var remembered = ToolDefaults.Load().For("issue_drawings");
        if (remembered is null) return ([], null);
        var tool = new IssueDrawingsTool();
        var fields = ToolForm.Fields(tool.PropertiesJson, tool.Required).Where(f => f.Key is "sheet_numbers" or "number_starts_with").ToList();
        var json = ToolForm.Arguments(fields, remembered, []);
        if (json is null) return ([], null);
        var a = new ToolArgs(ToolForm.Parse(json));
        return (a.StringList("sheet_numbers"), a.OptString("number_starts_with"));
    }

    /// <summary>Fix: selects the element and shows it (a sheet or view opens). Still inside the ribbon command, so the API is available.</summary>
    private static Autodesk.Revit.UI.Result Fix(UIApplication app, long id)
    {
        var uidoc = app.ActiveUIDocument;
        var e = uidoc.Document.GetElement(new ElementId(id));
        if (e is null) return Autodesk.Revit.UI.Result.Cancelled;
        try
        {
            if (e is View view && !view.IsTemplate) uidoc.RequestViewChange(view);
            else
            {
                uidoc.Selection.SetElementIds([e.Id]);
                uidoc.ShowElements(e.Id);
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            TaskDialog.Show("Sketch ProMax", $"Couldn't show element {id} ({ex.Message}). Find it with Manage > Select by ID, fix it, then click Submit check again.");
        }
        return Autodesk.Revit.UI.Result.Succeeded;
    }
}
