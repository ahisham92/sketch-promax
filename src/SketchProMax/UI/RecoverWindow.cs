using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Revit;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.UI;

/// <summary>
/// Recover (Settings > Recovery, the pane's Files section, and by itself when a model opens with changes that weren't
/// saved last time): each model's change files (Revit/ChangeFile) and Replay onto the open model (Revit/ChangeReplay),
/// with a preview first. Any change file can be picked from disk too, such as one copied from another PC.
/// </summary>
internal sealed class RecoverWindow : Window
{
    private static RecoverWindow? _open;
    private readonly StackPanel _list = new();
    private readonly StackPanel _preview = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _result = Brand.Text("", Brand.K.Muted, 12);
    private CancellationTokenSource? _stop;

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
        _open.Fill(file);
        _open.Activate();
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    private RecoverWindow()
    {
        Title = "Sketch ProMax: Lifeline";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height * 0.9);
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));

        var page = new StackPanel { Margin = new Thickness(18, 10, 18, 14) };
        page.Children.Add(_list);
        _preview.Margin = new Thickness(0, 12, 0, 0);
        page.Children.Add(_preview);
        _result.TextWrapping = TextWrapping.Wrap;
        _result.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);
        page.Children.Add(_result);

        var pick = RichUi.Button(RichUi.Icon.Open, "Replay a file from disk…");
        AutomationProperties.SetHelpText(pick, "Pick any Sketch ProMax change file (.txt), such as one copied from another PC, to replay onto the open model.");
        pick.Click += (_, _) => PickFile();
        var folder = RichUi.Button(RichUi.Icon.Folder, "Open folder");
        folder.Click += (_, _) => Explore(ChangeFile.Root, null);
        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        page.Children.Add(Brand.ButtonRow(pick, folder, close));

        var root = new DockPanel();
        var header = Brand.Header("Lifeline", "Bring back changes lost when Revit closed or the file was damaged");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = root;
        Closed += (_, _) => _stop?.Cancel();
    }

    /// <summary>One change file on disk, read.</summary>
    private sealed record Item(string File, string Label, M.Parsed Parsed);

    private static List<Item> Files()
    {
        var items = new List<Item>();
        try
        {
            if (!Directory.Exists(ChangeFile.Root)) return items;
            foreach (var dir in Directory.GetDirectories(ChangeFile.Root))
                foreach (var file in Directory.GetFiles(dir, "*.txt"))
                {
                    var name = Path.GetFileName(file);
                    string? label = name == M.CurrentName ? "Changes since the last save"
                        : name == M.LastSaveName ? "Changes before the last save"
                        : M.LeftoverTime(name) is { } at ? $"Not saved: left when the model closed, {at:dd MMM HH:mm}" : null;
                    if (label is null) continue;
                    var parsed = M.Read(ChangeFile.ReadLines(file));
                    if (parsed.Elements.Count > 0) items.Add(new Item(file, label, parsed));
                }
        }
        catch (Exception) { }
        return items.OrderByDescending(i => i.Parsed.Last).ToList();
    }

    private void Fill(string? highlight)
    {
        _list.Children.Clear();
        var items = Files();
        if (items.Count == 0)
        {
            _list.Children.Add(Wrap(Brand.Text("No change files yet. Each model you work on gets one in Documents\\Sketch ProMax\\Change files as soon as you change something; saving clears it.")));
            return;
        }
        if (highlight is not null && items.FirstOrDefault(i => string.Equals(i.File, highlight, StringComparison.OrdinalIgnoreCase)) is { } left)
            _list.Children.Add(Wrap(Brand.Text($"{M.FileStem(left.Parsed.Model ?? "This model")} has {Count(left.Parsed)} that weren't saved last time (Revit closed by itself, or the model was closed without saving). " +
                                               "Replay them onto the open model if you want them back.", Brand.K.WarningText)));
        foreach (var group in items.GroupBy(i => i.Parsed.Model ?? Path.GetDirectoryName(i.File)!))
        {
            var title = Brand.Text(M.FileStem(group.Key), Brand.K.Text, 14, bold: true);
            title.Margin = new Thickness(0, 10, 0, 0);
            _list.Children.Add(title);
            foreach (var item in group) _list.Children.Add(Row(item, string.Equals(item.File, highlight, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static string Count(M.Parsed p) => Plural(p.Elements.Count, "change");

    private UIElement Row(Item item, bool primary)
    {
        var replay = Brand.Button("Replay…", primary ? Brand.Weight.Primary : Brand.Weight.Secondary);
        if (primary) replay.IsDefault = true;
        AutomationProperties.SetName(replay, "Replay " + item.Label);
        replay.Click += async (_, _) => await Preview(item.File, item.Parsed);
        var view = Brand.Button("View");
        AutomationProperties.SetName(view, "View " + item.Label);
        view.Click += (_, _) => Explore(item.File, open: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(view);
        buttons.Children.Add(replay);
        var p = item.Parsed;
        var span = p.First is { } a && p.Last is { } b ? $", {a:HH:mm} to {b:HH:mm}" : "";
        var text = new StackPanel();
        text.Children.Add(Brand.Text(item.Label));
        text.Children.Add(Brand.Text($"{Count(p)}{span}", Brand.K.Muted, 11));
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    private async void PickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pick a change file", Filter = "Change files|*.txt|All files|*.*" };
        try { if (Directory.Exists(ChangeFile.Root)) dialog.InitialDirectory = ChangeFile.Root; } catch (Exception) { }
        if (dialog.ShowDialog(this) != true) return;
        var parsed = M.Read(ChangeFile.ReadLines(dialog.FileName));
        if (parsed.Elements.Count == 0) { Say("That file has no changes Sketch ProMax can read. Pick a change file from Documents\\Sketch ProMax\\Change files.", Brand.K.DangerText); return; }
        await Preview(dialog.FileName, parsed);
    }

    // ---- Preview, then replay -----------------------------------------------------------------------------------------

    private async Task Preview(string file, M.Parsed parsed)
    {
        _preview.Children.Clear();
        _preview.Visibility = Visibility.Visible;
        Say("Reading the open model…", Brand.K.Muted);
        (string Title, string Path, List<ChangeReplay.Step> Steps)? plan;
        try
        {
            plan = await App.Dispatcher.InvokeAsync(app =>
            {
                if (app.ActiveUIDocument?.Document is not { IsFamilyDocument: false } doc) return ((string, string, List<ChangeReplay.Step>)?)null;
                return (doc.Title, doc.PathName ?? "", ChangeReplay.Plan(doc, parsed));
            });
        }
        catch (Exception ex) { Say("Couldn't read the open model: " + ex.Message, Brand.K.DangerText); return; }
        if (plan is not { } p) { Say("Open the model to put the changes into first (the last good copy, or a new local from central), then press Replay again.", Brand.K.DangerText); return; }
        Say("", Brand.K.Muted);

        int Of(ChangeReplay.Act act) => p.Steps.Count(s => s.Act == act);
        var heading = Brand.Text($"Replay onto {p.Title}", Brand.K.Text, 14, bold: true);
        _preview.Children.Add(heading);
        if (parsed.Model is { } from && !string.Equals(M.FileStem(from), M.FileStem(p.Path), StringComparison.OrdinalIgnoreCase))
            _preview.Children.Add(Wrap(Brand.Text($"This file is from {M.FileStem(from)}; the open model is {p.Title}. Elements are matched by their Revit id, so only copies of the same model match.", Brand.K.WarningText)));
        var summary = new List<string>();
        if (Of(ChangeReplay.Act.Set) > 0) summary.Add($"set {Plural(Of(ChangeReplay.Act.Set), "element")} back as they were");
        if (Of(ChangeReplay.Act.Make) > 0) summary.Add($"make {Plural(Of(ChangeReplay.Act.Make), "element")} again");
        if (Of(ChangeReplay.Act.Delete) > 0) summary.Add($"delete {Plural(Of(ChangeReplay.Act.Delete), "element")}");
        _preview.Children.Add(Wrap(Brand.Text(summary.Count > 0 ? "It will " + string.Join(", ", summary) + "." : "Nothing to do: the model already has these changes.")));
        var skipped = p.Steps.Where(s => s.Act == ChangeReplay.Act.Skip && s.Why != "already not in the model").ToList();
        if (skipped.Count > 0)
        {
            _preview.Children.Add(Wrap(Brand.Text($"{Plural(skipped.Count, "change")} can't be replayed and are listed in the report to redo by hand.", Brand.K.WarningText)));
        }
        _preview.Children.Add(Wrap(Brand.Text("Each 100 elements are one undo step. Nothing goes to central until you synchronize. " +
                                              "Afterwards, check in the model that every change came back.", Brand.K.Muted, 11)));

        var go = Brand.Button("Proceed", Brand.Weight.Primary);
        go.IsEnabled = summary.Count > 0;
        var stop = Brand.Button("Stop");
        stop.IsEnabled = false;
        var cancel = Brand.Button("Cancel");
        cancel.Click += (_, _) => { _preview.Visibility = Visibility.Collapsed; Say("", Brand.K.Muted); };
        stop.Click += (_, _) => _stop?.Cancel();
        go.Click += async (_, _) =>
        {
            go.IsEnabled = false;
            cancel.IsEnabled = false;
            stop.IsEnabled = true;
            await Replay(file, p.Path, p.Steps);
            stop.IsEnabled = false;
            cancel.IsEnabled = true;
        };
        _preview.Children.Add(Brand.ButtonRow(cancel, stop, go));
    }

    private async Task Replay(string file, string modelPath, List<ChangeReplay.Step> steps)
    {
        _stop = new CancellationTokenSource();
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
                    if (app.ActiveUIDocument?.Document is not { } doc || !string.Equals(doc.PathName ?? "", modelPath, StringComparison.OrdinalIgnoreCase))
                        return "another model became the active one";
                    if (doc.IsModifiable) return "a change is being made in the model (finish it or press Esc)";
                    ChangeReplay.Apply(doc, chunk, outcome);
                    return null;
                });
                if (why is not null) { stoppedBecause = why; break; }
            }
            catch (Exception ex) { stoppedBecause = ex.Message; Revit.LifelineBugs.Note("replay", "the replay stopped", modelPath, ex); break; }
            done += chunk.Count;
        }
        var report = WriteReport(file, modelPath, outcome, done, steps.Count, stoppedBecause);
        // Lifeline's bug list: Revit's errors first, then what replay can't put back yet (what to build next).
        foreach (var error in outcome.Errors) Revit.LifelineBugs.Note("replay error", error, modelPath);
        foreach (var n in outcome.NotDone.Except(outcome.Errors)) Revit.LifelineBugs.Note("replay couldn't", n, modelPath, key: M.Reason(n));
        var line = $"Replayed {done} of {steps.Count}: {outcome.Set} set back, {outcome.Made} made again, {outcome.Deleted} deleted" +
                   (outcome.Same > 0 ? $", {outcome.Same} already right" : "") + ". " +
                   (outcome.NotDone.Count > 0 ? $"{Plural(outcome.NotDone.Count, "change")} to redo by hand are in the report." : "") +
                   (stoppedBecause is not null ? $" Stopped because {stoppedBecause}." : " Check the model, then save it.");
        Say(line, stoppedBecause is null && outcome.NotDone.Count == 0 ? Brand.K.SuccessText : Brand.K.WarningText);
        // The owner asked for this alert: replay can't promise every change, so the user checks before saving.
        MessageBox.Show(this,
            line + "\n\nPlease check in the model that every change you made is there before you save or synchronize. " +
            "Sketch ProMax puts back what the change file holds, but some edits (sketches, shapes, views, anything listed in the report) need you.",
            "Sketch ProMax: check the changes", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (report is not null)
        {
            var open = Brand.Button("Open the report");
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Click += (_, _) => Explore(report, open: true);
            _preview.Children.Add(open);
        }
    }

    private static string? WriteReport(string file, string modelPath, ChangeReplay.Outcome outcome, int done, int total, string? stopped)
    {
        try
        {
            var report = Path.Combine(Path.GetDirectoryName(file)!, $"replay report {DateTime.Now:yyyy-MM-dd HHmmss}.log");
            var text = new StringBuilder();
            text.AppendLine($"Sketch ProMax: replay of {file}");
            text.AppendLine($"Onto: {modelPath}");
            text.AppendLine($"At: {DateTime.Now:dd MMM yyyy HH:mm}");
            text.AppendLine($"Done {done} of {total}: {outcome.Set} set back, {outcome.Made} made again, {outcome.Deleted} deleted, {outcome.Same} already right.");
            if (stopped is not null) text.AppendLine($"Stopped because {stopped}.");
            text.AppendLine();
            text.AppendLine(outcome.NotDone.Count > 0 ? "Redo by hand:" : "Nothing to redo by hand.");
            foreach (var n in outcome.NotDone) text.AppendLine("- " + n);
            File.WriteAllText(report, text.ToString());
            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- Small things ---------------------------------------------------------------------------------------------------

    private void Explore(string path, bool? open)
    {
        try
        {
            if (open == true) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            else
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say($"Couldn't open {path}: {ex.Message}", Brand.K.DangerText);
        }
    }

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
