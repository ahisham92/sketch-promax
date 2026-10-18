using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using SketchProMax.Tools;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// The guided test checklist (ribbon Help > Test checklist, and the pane's Bugs section): the items the field test
/// leaves to a person (FieldTestCoverage's manual features) and one step per fixed bug row of the bug log, each with
/// what to do and why it is manual. The user marks each pass or fail with an optional note while working in Revit (the
/// window doesn't block Revit); marks are kept as a draft between openings, and Save results writes a short Markdown
/// file to the Bugs folder (Documents\Sketch ProMax\Bugs) and offers to open the folder. Changes nothing in the model.
/// </summary>
internal sealed class TestChecklistWindow : Window
{
    private static TestChecklistWindow? _open;

    private static readonly string[] Choices = ["Not checked", "Pass", "Fail"];

    private readonly List<FieldTestCoverage.CheckItem> _items;
    private readonly Dictionary<string, (ComboBox Result, TextBox Note)> _rows = new();
    private readonly TextBlock _status = Brand.Text("");
    private readonly TextBlock _saved = Brand.Text("", Brand.K.SuccessText);
    private readonly Button _save = Brand.Button("Save results", Brand.Weight.Primary);
    private readonly Button _folder = Brand.Button("Open Bugs folder");
    private readonly string _version, _revit, _model;
    private bool _loading;

    /// <summary>The ribbon and pane command: shows the window (or brings the open one to the front).</summary>
    public static Autodesk.Revit.UI.Result Open(UIApplication app)
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return Autodesk.Revit.UI.Result.Succeeded;
        }
        var doc = app.ActiveUIDocument?.Document;
        var window = new TestChecklistWindow(Update.Updater.CurrentText, app.Application.VersionNumber, doc is null || doc.IsFamilyDocument ? "" : doc.Title);
        _open = window;
        window.Closed += (_, _) => _open = null;
        window.Show();
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    private TestChecklistWindow(string version, string revit, string model)
    {
        _version = version;
        _revit = revit;
        _model = model;
        _items = Items();

        Title = "Sketch ProMax test checklist";
        Width = 640;
        Height = 720;
        MinWidth = 480;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var body = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        var top = new StackPanel();
        var intro = Brand.Text("Do these on a _detached copy of a real model, not on a working model. Revit stays usable while this " +
                               "window is open. Mark each item Pass or Fail and add a note if something looks wrong; your marks are " +
                               "kept until you start again.");
        intro.TextWrapping = TextWrapping.Wrap;
        top.Children.Add(intro);
        if (model.Length > 0 && model.IndexOf("detached", StringComparison.OrdinalIgnoreCase) < 0)
        {
            var warn = Brand.Text($"The open model '{model}' doesn't look like a _detached copy. Open a detached copy before changing anything.", Brand.K.WarningText);
            warn.TextWrapping = TextWrapping.Wrap;
            warn.Margin = new Thickness(0, 6, 0, 0);
            top.Children.Add(warn);
        }
        _status.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);

        // Footer: what was saved, then the buttons (start again is destructive: red, never the default).
        var foot = new StackPanel();
        _saved.TextWrapping = TextWrapping.Wrap;
        _saved.Visibility = Visibility.Collapsed;
        _saved.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_saved, AutomationLiveSetting.Polite);
        foot.Children.Add(_saved);
        var reset = Brand.Button("Start again", Brand.Weight.Danger);
        AutomationProperties.SetHelpText(reset, "Clears every mark and note in this window. Saved result files are kept.");
        reset.Click += (_, _) => StartAgain();
        _folder.Visibility = Visibility.Collapsed;
        _folder.Click += (_, _) => OpenFolder();
        var close = Brand.Button("Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        _save.IsDefault = true;
        AutomationProperties.SetHelpText(_save, "Writes the marks to a small Markdown file in Documents\\Sketch ProMax\\Bugs.");
        _save.Click += (_, _) => Save();
        foot.Children.Add(Brand.ButtonRow(reset, _folder, close, _save));
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(foot);

        var list = new StackPanel();
        AutomationProperties.SetName(list, "Checklist items");
        string? area = null;
        foreach (var item in _items)
        {
            if (item.Area != area)
            {
                area = item.Area;
                var heading = Brand.Text(area, bold: true, size: 14);
                heading.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 14, 0, 4);
#if !NETFRAMEWORK
                AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
                list.Children.Add(heading);
            }
            list.Children.Add(Card(item));
        }
        var scroll = new ScrollViewer
        {
            Content = list, Margin = new Thickness(0, 10, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        body.Children.Add(scroll);

        var root = new DockPanel();
        var header = Brand.Header("Test checklist", $"{_items.Count} checks only you can do, on a _detached copy");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;

        LoadDraft();
        Refresh();
        Loaded += (_, _) => _rows.Values.FirstOrDefault().Result?.Focus();
    }

    private Border Card(FieldTestCoverage.CheckItem item)
    {
        var panel = new StackPanel();
        var title = Brand.Text(item.Title, bold: true);
        title.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(title);
        var steps = Brand.Text(item.Steps);
        steps.TextWrapping = TextWrapping.Wrap;
        steps.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(steps);
        var why = Brand.Text((item.Area == FieldTestCoverage.BugFixes ? "Bug log: " : "Why by hand: ") + item.Why, Brand.K.Muted, size: 11);
        why.TextWrapping = TextWrapping.Wrap;
        why.Margin = new Thickness(0, 2, 0, 6);
        panel.Children.Add(why);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var result = new ComboBox { ItemsSource = Choices, SelectedIndex = 0, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(result, $"Result for {item.Title}");
        var note = new TextBox { MinHeight = 28, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(note, $"Note for {item.Title} (optional)");
        AutomationProperties.SetHelpText(note, "What you saw, especially when it failed.");
        result.SelectionChanged += (_, _) => Changed();
        note.TextChanged += (_, _) => Changed();
        row.Children.Add(result);
        Grid.SetColumn(note, 1);
        row.Children.Add(note);
        panel.Children.Add(row);
        _rows[item.Id] = (result, note);

        var card = new Border { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = panel };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        AutomationProperties.SetName(card, item.Title);
        return card;
    }

    private Dictionary<string, FieldTestCoverage.Mark> Marks() =>
        _rows.ToDictionary(r => r.Key, r => new FieldTestCoverage.Mark(r.Key,
            r.Value.Result.SelectedIndex switch { 1 => "pass", 2 => "fail", _ => "" }, r.Value.Note.Text.Trim()));

    private void Changed()
    {
        if (_loading) return;
        SaveDraft();
        Refresh();
    }

    private void Refresh()
    {
        var marks = Marks();
        int pass = marks.Values.Count(m => m.Result == "pass"), fail = marks.Values.Count(m => m.Result == "fail");
        var left = _items.Count - pass - fail;
        _status.Text = $"{pass} pass, {fail} fail, {left} still to check." +
                       (left == 0 ? " All checked: click Save results." : " Save results any time; unchecked items are saved as \"not checked\".");
    }

    // ---- Files ----------------------------------------------------------------------------------------------------------

    /// <summary>The downloaded bug log in the Bugs folder when there is one, merged over the copy built into this version.</summary>
    internal static string BugLogText()
    {
        string? local = null, builtIn = null;
        try { if (File.Exists(Bugs.BugLog.LocalLog)) local = File.ReadAllText(Bugs.BugLog.LocalLog); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            using var stream = typeof(TestChecklistWindow).Assembly.GetManifestResourceStream("SketchProMax.bug-log.md");
            if (stream is not null) using (var reader = new StreamReader(stream)) builtIn = reader.ReadToEnd();
        }
        catch (IOException) { }
        // Rows are keyed by number, so the later text (the downloaded log) wins where both have a row.
        return (builtIn ?? "") + "\n" + (local ?? "");
    }

    private static string DraftPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "test-checklist-draft.json");

    private void SaveDraft()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DraftPath)!);
            File.WriteAllText(DraftPath, JsonSerializer.Serialize(Marks().Values.Where(m => m.Result.Length > 0 || m.Note.Length > 0).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the draft is a convenience */ }
    }

    private void LoadDraft()
    {
        try
        {
            if (!File.Exists(DraftPath)) return;
            var marks = JsonSerializer.Deserialize<List<FieldTestCoverage.Mark>>(File.ReadAllText(DraftPath)) ?? [];
            _loading = true;
            foreach (var m in marks)
                if (_rows.TryGetValue(m.Id, out var row))
                {
                    row.Result.SelectedIndex = m.Result switch { "pass" => 1, "fail" => 2, _ => 0 };
                    row.Note.Text = m.Note;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        finally { _loading = false; }
    }

    private void StartAgain()
    {
        // A plain message box: this window is modeless, outside a Revit API call, where Revit's own dialogs aren't allowed.
        if (MessageBox.Show(this, "Clear every mark and note? Result files you saved stay in the Bugs folder.", "Sketch ProMax test checklist",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _loading = true;
        foreach (var (result, note) in _rows.Values) { result.SelectedIndex = 0; note.Text = ""; }
        _loading = false;
        SaveDraft();
        Refresh();
        _saved.Visibility = Visibility.Collapsed;
    }

    private void Save()
    {
        if (Brand.IsBusy(_save)) return;
        Brand.SetBusy(_save, true, "Saving…");
        try
        {
            var path = SaveTo(Bugs.BugLog.Root, _items, Marks(), _version, _revit, _model, DateTime.Now);
            _saved.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.SuccessText);
            _saved.Text = $"Saved {Path.GetFileName(path)} in the Bugs folder. Send it with your bug reports, or attach it in the chat.";
            _folder.Visibility = Visibility.Visible;
            _folder.Tag = path;
            _folder.Focus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saved.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.DangerText);
            _saved.Text = $"Couldn't save the results ({ex.Message}). Check that Documents\\Sketch ProMax\\Bugs can be written to, then try again.";
        }
        finally
        {
            _saved.Visibility = Visibility.Visible;
            Brand.SetBusy(_save, false);
        }
    }

    /// <summary>The checklist's items as the window lists them: the manual features and tools, then the fixed bug rows.</summary>
    internal static List<FieldTestCoverage.CheckItem> Items() => FieldTestCoverage.Checklist(FieldTestCoverage.FixedBugs(BugLogText()));

    /// <summary>Writes the result file into <paramref name="folder"/> (Save results; the field test, in a temporary folder). Returns its path.</summary>
    internal static string SaveTo(string folder, IReadOnlyList<FieldTestCoverage.CheckItem> items, IReadOnlyDictionary<string, FieldTestCoverage.Mark> marks,
        string version, string revit, string model, DateTime when)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Test checklist {when:yyyy-MM-dd HHmm}.md");
        File.WriteAllText(path, FieldTestCoverage.ResultMarkdown(items, marks, version, revit, model, when));
        return path;
    }

    private void OpenFolder()
    {
        try
        {
            var arg = _folder.Tag is string file ? $"/select,\"{file}\"" : $"\"{Bugs.BugLog.Root}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _saved.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.DangerText);
            _saved.Text = $"Couldn't open the folder ({ex.Message}). It is {Bugs.BugLog.Root}.";
        }
    }
}
