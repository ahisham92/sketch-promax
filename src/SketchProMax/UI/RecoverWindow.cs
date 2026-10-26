using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using SketchProMax.Revit;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.UI;

/// <summary>
/// Lifeline (the ribbon, Settings > Lifeline, the pane, and by itself when a model opens with changes that weren't saved
/// last time): the open model's changes that weren't saved (Revit/ChangeFile), how many, and one Replay that plays them
/// onto the open model (Revit/ChangeReplay) and opens the views that were open. What it couldn't do is listed at the
/// end, each with Show to go to the place. Any change file can be picked from disk too, such as one copied from another PC.
/// </summary>
internal sealed class RecoverWindow : System.Windows.Window
{
    private static RecoverWindow? _open;
    private readonly StackPanel _body = new();
    private readonly StackPanel _results = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _result = Brand.Text("", Brand.K.Muted, 12);
    private CancellationTokenSource? _stop;
    private bool _busy;

    public static Autodesk.Revit.UI.Result Open() => Show(null);

    /// <summary>A model opened with a change file left from last time: the window opens on it.</summary>
    public static void OfferLeftover(string file) => Show(file);

    private static Autodesk.Revit.UI.Result Show(string? file)
    {
        if (_open is not { IsLoaded: true })
        {
            _open = new RecoverWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        if (!_open._busy) _ = _open.Fill(file);
        _open.Activate();
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    private RecoverWindow()
    {
        Title = "Sketch ProMax: Lifeline";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height * 0.9);
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));

        var page = new StackPanel { Margin = new Thickness(18, 10, 18, 14) };
        page.Children.Add(_body);
        _result.TextWrapping = TextWrapping.Wrap;
        _result.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);
        page.Children.Add(_result);
        page.Children.Add(_results);

        var pick = Brand.Button("Pick a file…");
        AutomationProperties.SetHelpText(pick, "Pick any Sketch ProMax change file (.txt), such as one copied from another PC, to replay onto the open model.");
        pick.Click += (_, _) => PickFile();
        var close = Brand.Button("Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        page.Children.Add(Brand.ButtonRow(pick, close));

        var root = new DockPanel();
        var header = Brand.Header("Lifeline", "Bring back changes that weren't saved");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = root;
        Closed += (_, _) => _stop?.Cancel();
    }

    /// <summary>The open model: its title, path and central.</summary>
    private sealed record Target(string Title, string Path, string Central);

    private static async Task<Target?> OpenModel()
    {
        try
        {
            return await App.Dispatcher.InvokeAsync(app =>
                app.ActiveUIDocument?.Document is { IsFamilyDocument: false } doc ? new Target(doc.Title, doc.PathName ?? "", ChangeFile.CentralOf(doc)) : null);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The open model's newest file that wasn't saved (or <paramref name="file"/>), with its count and Replay.</summary>
    private async Task Fill(string? file)
    {
        _body.Children.Clear();
        _results.Children.Clear();
        Say("", Brand.K.Muted);
        var target = await OpenModel();
        if (target is null) { _body.Children.Add(Wrap(Brand.Text("Open a model first, then open Lifeline again."))); return; }
        file ??= target.Path.Length > 0 ? ChangeFile.LeftoversFor(target.Path, target.Central).FirstOrDefault() : null;
        var parsed = file is null ? null : M.Read(ChangeFile.ReadLines(file));
        if (file is null || parsed is not { Any: true })
        {
            _body.Children.Add(Wrap(Brand.Text($"{target.Title}: nothing to bring back. Every change was saved.")));
            return;
        }
        Ready(file, parsed, target);
    }

    private void Ready(string file, M.Parsed parsed, Target target)
    {
        _body.Children.Clear();
        _results.Children.Clear();
        var title = Brand.Text(target.Title, Brand.K.Text, 14, bold: true);
        _body.Children.Add(title);
        _body.Children.Add(Wrap(Brand.Text(CountText(parsed), Brand.K.WarningText)));
        if (string.Equals(Path.GetFileName(file), M.LastSaveName, StringComparison.OrdinalIgnoreCase))
            _body.Children.Add(Wrap(Brand.Text("These were cleared when the model was " + (parsed.ClearedBy is { Length: > 0 } by ? by.Split(' ')[0] : "saved") +
                                               (parsed.ClearedAt is { } when ? $" at {when:HH:mm}" : "") + ". Replay brings back only what is missing or different; the rest is left as it is.", Brand.K.Muted, 11)));
        if (parsed.Model is { } from && !string.Equals(from, target.Path, StringComparison.OrdinalIgnoreCase)
            && !(parsed.Central is { Length: > 0 } c && string.Equals(c, target.Central, StringComparison.OrdinalIgnoreCase)))
            _body.Children.Add(Wrap(Brand.Text($"This file is from {M.FileStem(from)}. Only elements of the same model match.", Brand.K.Muted, 11)));
        var replay = Brand.Button("Replay", Brand.Weight.Primary);
        replay.IsDefault = true;
        var stop = Brand.Button("Stop");
        stop.Visibility = System.Windows.Visibility.Collapsed;
        stop.Click += (_, _) => _stop?.Cancel();
        replay.Click += async (_, _) =>
        {
            replay.IsEnabled = false;
            stop.Visibility = System.Windows.Visibility.Visible;
            _busy = true;
            try { await Replay(file, parsed, target); }
            finally { _busy = false; stop.Visibility = System.Windows.Visibility.Collapsed; }
        };
        var row = Brand.ButtonRow(stop, replay);
        row.HorizontalAlignment = HorizontalAlignment.Left;
        row.Margin = new Thickness(0, 8, 0, 0);
        _body.Children.Add(row);
    }

    /// <summary>"23 changes weren't saved (14:02 to 16:40), and 5 views were open."</summary>
    internal static string CountText(M.Parsed p)
    {
        var parts = new List<string>();
        if (p.Elements.Count > 0)
            parts.Add($"{Plural(p.Elements.Count, "change")} {(p.Elements.Count == 1 ? "wasn't" : "weren't")} saved" + (p.First is { } a && p.Last is { } b ? $" ({a:HH:mm} to {b:HH:mm})" : ""));
        if (p.Open.Count > 0) parts.Add($"{Plural(p.Open.Count, "view")} {(p.Open.Count == 1 ? "was" : "were")} open");
        return string.Join(", and ", parts) + ".";
    }

    private async void PickFile()
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pick a change file", Filter = "Change files|*.txt|All files|*.*" };
        try { if (Directory.Exists(ChangeFile.Root)) dialog.InitialDirectory = ChangeFile.Root; } catch (Exception) { }
        if (dialog.ShowDialog(this) != true) return;
        var parsed = M.Read(ChangeFile.ReadLines(dialog.FileName));
        if (!parsed.Any) { Say("That file has no changes Sketch ProMax can read.", Brand.K.DangerText); return; }
        if (await OpenModel() is not { } target) { Say("Open the model to put the changes into first.", Brand.K.DangerText); return; }
        Ready(dialog.FileName, parsed, target);
    }

    // ---- Replay ---------------------------------------------------------------------------------------------------------

    private async Task Replay(string file, M.Parsed parsed, Target target)
    {
        _stop = new CancellationTokenSource();
        _results.Children.Clear();
        Say("Reading the model…", Brand.K.Muted);
        List<ChangeReplay.Step> steps;
        try
        {
            var planned = await App.Dispatcher.InvokeAsync(app =>
                app.ActiveUIDocument?.Document is { } doc && string.Equals(doc.PathName ?? "", target.Path, StringComparison.OrdinalIgnoreCase) ? ChangeReplay.Plan(doc, parsed) : null);
            if (planned is null) { Say($"Make {target.Title} the active model, then press Replay again.", Brand.K.DangerText); return; }
            steps = planned;
        }
        catch (Exception ex) { Say("Couldn't read the model: " + ex.Message, Brand.K.DangerText); LifelineBugs.Note("replay", "the replay couldn't plan", target.Path, ex); return; }

        var outcome = new ChangeReplay.Outcome();
        var done = 0;
        string? stoppedBecause = null;
        for (var i = 0; i < steps.Count; i += M.ReplayChunk)
        {
            if (_stop.IsCancellationRequested) { stoppedBecause = "you pressed Stop"; break; }
            var chunk = steps.Skip(i).Take(M.ReplayChunk).ToList();
            Say($"Replaying… {done} of {steps.Count}", Brand.K.Muted);
            try
            {
                var why = await App.Dispatcher.InvokeAsync(app =>
                {
                    if (app.ActiveUIDocument?.Document is not { } doc || !string.Equals(doc.PathName ?? "", target.Path, StringComparison.OrdinalIgnoreCase))
                        return "another model became the active one";
                    if (doc.IsModifiable) return "a change is being made in the model (finish it or press Esc)";
                    ChangeReplay.Apply(doc, chunk, outcome);
                    return null;
                });
                if (why is not null) { stoppedBecause = why; break; }
            }
            catch (Exception ex) { stoppedBecause = ex.Message; LifelineBugs.Note("replay", "the replay stopped", target.Path, ex); break; }
            done += chunk.Count;
        }
        var opened = stoppedBecause is null ? await OpenViews(parsed, outcome, target) : 0;
        WriteReport(file, target.Path, outcome, done, steps.Count, opened, stoppedBecause);
        // Lifeline's bug list: Revit's errors first, then what replay can't put back yet (what to build next).
        foreach (var error in outcome.Errors) LifelineBugs.Note("replay error", error, target.Path);
        foreach (var n in outcome.NotDone.Except(outcome.Errors)) LifelineBugs.Note("replay couldn't", n, target.Path, key: M.Reason(n));
        if (stoppedBecause is null) ChangeFile.MarkReplayed(file);

        var line = $"Done: {outcome.Set} set back, {outcome.Made} made again, {outcome.Deleted} deleted" +
                   (opened > 0 ? $", {Plural(opened, "view")} opened" : "") + "." +
                   (stoppedBecause is not null ? $" Stopped after {done} of {steps.Count} because {stoppedBecause}." : "");
        Say(line, stoppedBecause is null && outcome.NotDone.Count == 0 ? Brand.K.SuccessText : Brand.K.WarningText);
        ShowNotDone(outcome);
        // The owner asked for this alert: replay can't promise every change, so the user checks before saving.
        MessageBox.Show(this,
            line + (outcome.NotDone.Count > 0 ? $"\n\n{Plural(outcome.NotDone.Count, "item")} weren't done: they are listed in the Lifeline window, each with Show." : "") +
            "\n\nPlease check in the model that every change you made is there before you save or synchronize.",
            "Sketch ProMax: check the changes", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>What wasn't done, each with Show (to the element, or to where it was in its view).</summary>
    private void ShowNotDone(ChangeReplay.Outcome outcome)
    {
        _results.Children.Clear();
        if (outcome.NotDone.Count == 0) return;
        _results.Children.Add(Brand.Text($"Not done ({outcome.NotDone.Count})", Brand.K.Text, 13, bold: true));
        for (var i = 0; i < outcome.NotDone.Count && i < 300; i++)
        {
            var text = Wrap(Brand.Text(outcome.NotDone[i], Brand.K.Text, 12));
            text.Margin = new Thickness(0, 0, 8, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (outcome.Place.TryGetValue(i, out var where))
            {
                var show = Brand.Button("Show");
                AutomationProperties.SetName(show, "Show " + outcome.NotDone[i]);
                var made = outcome.MadeFor.TryGetValue(where.Unique ?? "", out var id) ? id : null;
                show.Click += async (_, _) => Say(await GoTo(where, made), Brand.K.Muted);
                DockPanel.SetDock(show, Dock.Right);
                row.Children.Add(show);
            }
            row.Children.Add(text);
            _results.Children.Add(row);
        }
        if (outcome.NotDone.Count > 300) _results.Children.Add(Wrap(Brand.Text($"and {outcome.NotDone.Count - 300} more in the report.", Brand.K.Muted, 11)));
    }

    /// <summary>Takes Revit to what a line is about: the element (selected and shown), else its view around where it was.</summary>
    private static async Task<string> GoTo(M.Line line, ElementId? made)
    {
        try
        {
            var plan = await App.Dispatcher.InvokeAsync(app =>
            {
                if (app.ActiveUIDocument is not { } ui) return ((View? View, string Say)?)null;
                var doc = ui.Document;
                var element = made is not null ? doc.GetElement(made) : line.Unique is { } u ? doc.GetElement(u) : null;
                if (element is not null && element is not View)
                {
                    ui.Selection.SetElementIds([element.Id]);
                    ui.ShowElements(element.Id);
                    return (null, "Selected in the model.");
                }
                if (element is View v) { ui.RequestViewChange(v); return (null, "Opened."); }
                var view = line.View is { } vu ? doc.GetElement(vu) as View : null;
                view ??= PlanOf(doc, line);
                if (view is null || line.P is null) return (null, "Its place isn't known: look for it by hand.");
                ui.RequestViewChange(view);
                return (view, "This is where it was.");
            });
            if (plan is not { } p) return "Open the model first.";
            if (p.View is { } target && line.P is { } at)
            {
                await Task.Delay(700);
                await App.Dispatcher.InvokeAsync(app =>
                {
                    var ui = app.ActiveUIDocument;
                    var open = ui?.GetOpenUIViews().FirstOrDefault(o => o.ViewId == target.Id);
                    var c = new XYZ(at.X, at.Y, at.Z);
                    open?.ZoomAndCenterRectangle(c - new XYZ(8, 8, 8), c + new XYZ(8, 8, 8));
                    return true;
                });
            }
            return p.Say;
        }
        catch (Exception ex) { return "Couldn't go there: " + ex.Message; }
    }

    /// <summary>A floor plan of the line's level, for a model element that is gone.</summary>
    private static View? PlanOf(Document doc, M.Line line)
    {
        if (line.LevelUnique is not { } u || doc.GetElement(u) is not Level level) return null;
        return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id);
    }

    /// <summary>Opens the views that were open (made again or not), the active one last; how many opened.</summary>
    private static async Task<int> OpenViews(M.Parsed parsed, ChangeReplay.Outcome outcome, Target target)
    {
        if (parsed.Open.Count == 0) return 0;
        var order = parsed.Open.Where(u => u != parsed.Active).Take(20).ToList();
        if (parsed.Active is { } active) order.Add(active);
        var opened = 0;
        foreach (var unique in order)
        {
            try
            {
                var ok = await App.Dispatcher.InvokeAsync(app =>
                {
                    if (app.ActiveUIDocument is not { } ui || !string.Equals(ui.Document.PathName ?? "", target.Path, StringComparison.OrdinalIgnoreCase)) return false;
                    var doc = ui.Document;
                    var view = (outcome.MadeFor.TryGetValue(unique, out var id) ? doc.GetElement(id) : doc.GetElement(unique)) as View;
                    if (view is null || view.IsTemplate) return false;
                    if (ui.ActiveView?.Id == view.Id) return true;
                    try { ui.ActiveView = view; }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { ui.RequestViewChange(view); }
                    return true;
                });
                if (ok) opened++;
            }
            catch (Exception ex) { LifelineBugs.Note("replay", "a view that was open couldn't be opened", target.Path, ex, "open views"); }
        }
        return opened;
    }

    private static void WriteReport(string file, string modelPath, ChangeReplay.Outcome outcome, int done, int total, int opened, string? stopped)
    {
        try
        {
            var report = Path.Combine(Path.GetDirectoryName(file)!, $"replay report {DateTime.Now:yyyy-MM-dd HHmmss}.log");
            var text = new StringBuilder();
            text.AppendLine($"Sketch ProMax: replay of {file}");
            text.AppendLine($"Onto: {modelPath}");
            text.AppendLine($"At: {DateTime.Now:dd MMM yyyy HH:mm}");
            text.AppendLine($"Done {done} of {total}: {outcome.Set} set back, {outcome.Made} made again, {outcome.Deleted} deleted, {outcome.Same} already right, {opened} views opened.");
            if (stopped is not null) text.AppendLine($"Stopped because {stopped}.");
            text.AppendLine();
            text.AppendLine(outcome.NotDone.Count > 0 ? "Not done:" : "Nothing left to do by hand.");
            foreach (var n in outcome.NotDone) text.AppendLine("- " + n);
            File.WriteAllText(report, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- Small things ---------------------------------------------------------------------------------------------------

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static TextBlock Wrap(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 4, 0, 0);
        return text;
    }

    private void Say(string text, string key)
    {
        _result.Text = text;
        Brand.Fg(_result, key);
    }
}
