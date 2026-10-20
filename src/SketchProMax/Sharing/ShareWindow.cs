using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using UIApplication = Autodesk.Revit.UI.UIApplication;
using TaskDialogCommandLinkId = Autodesk.Revit.UI.TaskDialogCommandLinkId;
using TaskDialogResult = Autodesk.Revit.UI.TaskDialogResult;
using SketchProMax.UI;
using SketchProMax.Update;

namespace SketchProMax.Sharing;

/// <summary>
/// Share tools: pick any number of tools (windows such as the pre-submission checklist, ribbon tools, learned tools,
/// recipes), name the pack, optionally pick an update folder everyone can reach, and make the zip. The zip installs
/// TritoRevit (when it isn't installed) and the tools; with an update folder, later changes reach everyone by themselves.
/// Choosing a pack shared before fills everything in, so sharing it again is the next revision of the same pack.
/// </summary>
internal sealed class ShareWindow : Window
{
    private readonly List<SharedTools.Item> _catalog;
    private readonly HashSet<string> _chosen = new(StringComparer.Ordinal);
    private readonly ComboBox _packs = new() { MinHeight = 28 };
    private readonly TextBox _name = new() { MinHeight = 28 };
    private readonly TextBox _search = new() { MinHeight = 28 };
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = Brand.Text("", Brand.K.Muted);
    private readonly TextBox _folder = new() { MinHeight = 28 };
    private readonly CheckBox _mine = new() { Content = "Include my checklist as everyone's Default", IsChecked = false, Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _auto = new() { Content = "Publish changes to these tools by themselves", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _note = new() { MinHeight = 28 };
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText);
    private readonly Button _make;
    private readonly List<SharedPackMath.Record> _records = SharedPacks.Records();
    private string? _id;

    public bool Made { get; private set; }

    public ShareWindow(List<SharedTools.Item> catalog)
    {
        _catalog = catalog;
        Title = "Share tools";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        body.Children.Add(Brand.Text("Makes one zip for colleagues. They extract it and double-click Install TritoRevit.cmd: it installs the TritoRevit add-in " +
                                     "when they don't have it, and these tools on its tab. Only the first install needs a Revit restart."));

        body.Children.Add(Label("Pack", _packs, "A pack shared before is shared again as its next revision."));
        _packs.Items.Add(new ComboBoxItem { Content = "New pack", Tag = null });
        foreach (var r in _records) _packs.Items.Add(new ComboBoxItem { Content = $"{r.Name} (revision {r.Revision})", Tag = r });
        _packs.SelectedIndex = 0;
        _packs.SelectionChanged += (_, _) => Load((_packs.SelectedItem as ComboBoxItem)?.Tag as SharedPackMath.Record);
        body.Children.Add(_packs);

        body.Children.Add(Label("Name *", _name, "Shown as the panel's title on the TritoRevit tab."));
        body.Children.Add(_name);

        var listLabel = Label("Tools", _search, "Search, then tick any number of tools.");
        body.Children.Add(listLabel);
        var searchRow = new DockPanel();
        DockPanel.SetDock(_count, Dock.Right);
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(10, 0, 0, 0);
        searchRow.Children.Add(_count);
        AutomationProperties.SetName(_search, "Search the tools");
        _search.TextChanged += (_, _) => Rebuild();
        searchRow.Children.Add(_search);
        body.Children.Add(searchRow);
        AutomationProperties.SetName(_list, "Tools you can share");
        body.Children.Add(new Border
        {
            Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 8, 4),
            Child = new ScrollViewer { Content = _list, Height = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        });
        ((Border)body.Children[body.Children.Count - 1]).SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);

        // Off by default: colleagues start from the built-in checklist. Ticked: the pack carries this PC's checklist, and
        // with automatic publishing its later edits reach everyone who hasn't changed or imported their own.
        _mine.SetResourceReference(ForegroundProperty, Brand.K.Text);
        var mineHelp = "Only with Pre-submission checklist ticked. Off: colleagues start from the built-in checklist. On: they get yours as their Default, " +
                       "and your later edits reach them too. A checklist they edited or imported themselves is never replaced.";
        AutomationProperties.SetHelpText(_mine, mineHelp);
        _mine.ToolTip = mineHelp;
        body.Children.Add(_mine);

        body.Children.Add(Label("Update folder (optional)", _folder,
            "A folder everyone can reach: a shared drive or a synced Teams or OneDrive folder. Colleagues' tools update from it by themselves, with Revit open. Leave empty for a zip only."));
        var folderRow = new DockPanel();
        var browse = Brand.Button("Browse…");
        browse.Click += (_, _) => Browse();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        AutomationProperties.SetName(_folder, "Update folder");
        _folder.TextChanged += (_, _) => _auto.IsEnabled = _folder.Text.Trim().Length > 0;
        folderRow.Children.Add(_folder);
        body.Children.Add(folderRow);
        _auto.IsEnabled = false;
        _auto.SetResourceReference(ForegroundProperty, Brand.K.Text);
        AutomationProperties.SetHelpText(_auto, "When a learned tool, recipe or your included checklist changes on this PC, the next revision is published to the update folder within a few minutes.");
        body.Children.Add(_auto);

        body.Children.Add(Label("Note (optional)", _note, "A line for the readme, such as who the tools are for."));
        body.Children.Add(_note);

        _error.Visibility = Visibility.Collapsed;
        _error.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        body.Children.Add(_error);

        var cancel = Brand.Button("Cancel");
        cancel.IsCancel = true;
        _make = Brand.Button("Make zip", Brand.Weight.Primary);
        _make.IsDefault = true;
        _make.Click += (_, _) => Make();
        body.Children.Add(Brand.ButtonRow(cancel, _make));

        var root = new DockPanel();
        var header = Brand.Header("Share tools", "For colleagues with or without Sketch ProMax, on the TritoRevit tab.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;
        // The pre-submission checklist is ticked to start with: the tool most often shared.
        _chosen.Add("command:pre-submission");
        Rebuild();
        Loaded += (_, _) => _name.Focus();
    }

    private static TextBlock Label(string text, Control target, string help)
    {
        var label = Brand.Text(text, bold: true);
        label.Margin = new Thickness(0, 12, 0, 2);
        AutomationProperties.SetLabeledBy(target, label);
        AutomationProperties.SetHelpText(target, help);
        target.ToolTip = help;
        return label;
    }

    private void Load(SharedPackMath.Record? record)
    {
        _id = record?.Id;
        _chosen.Clear();
        if (record is null)
        {
            _name.Text = "";
            _folder.Text = "";
            _note.Text = "";
            _auto.IsChecked = true;
            _mine.IsChecked = false;
        }
        else
        {
            _name.Text = record.Name;
            _folder.Text = record.UpdateFrom;
            _note.Text = record.Note;
            _auto.IsChecked = record.AutoPublish;
            _mine.IsChecked = record.IncludeChecklist;
            foreach (var key in record.Items) _chosen.Add(key);
        }
        _name.IsReadOnly = record is not null; // the same pack keeps its name; a new name is a new pack
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var query = _search.Text.Trim();
        foreach (var group in _catalog.Where(i => query.Length == 0 || i.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                                                  || i.Tip.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).GroupBy(i => i.Group))
        {
            var heading = Brand.Text(group.Key, bold: true, size: 13);
            heading.Margin = new Thickness(0, 6, 0, 2);
            _list.Children.Add(heading);
            foreach (var item in group)
            {
                var box = new CheckBox { IsChecked = _chosen.Contains(item.Key), Margin = new Thickness(0, 3, 0, 3), Tag = item.Key };
                var text = new StackPanel();
                text.Children.Add(Brand.Text(item.Title));
                if (item.Tip.Length > 0) text.Children.Add(Brand.Text(Trim(item.Tip), Brand.K.Muted, size: 11));
                box.Content = text;
                AutomationProperties.SetName(box, item.Title);
                box.Checked += (_, _) => { _chosen.Add(item.Key); Count(); };
                box.Unchecked += (_, _) => { _chosen.Remove(item.Key); Count(); };
                _list.Children.Add(box);
            }
        }
        if (_list.Children.Count == 0) _list.Children.Add(Brand.Text($"Nothing matches \"{query}\".", Brand.K.Muted));
        Count();
    }

    private void Count()
    {
        var known = _chosen.Count(k => _catalog.Any(i => i.Key == k));
        _count.Text = known == 0 ? "None chosen" : $"{known} chosen";
    }

    private static string Trim(string text) => text.Length <= 150 ? text : text.Substring(0, 149).TrimEnd() + "…";

    private void Browse()
    {
#if NETFRAMEWORK
        // Revit 2024's .NET has no folder picker in WPF: a file dialog whose "file" names the folder.
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Pick the update folder (open it, then click Save)", FileName = "Use this folder", Filter = "Folder|*.folder" };
        if (_folder.Text.Trim().Length > 0 && Directory.Exists(_folder.Text.Trim())) dialog.InitialDirectory = _folder.Text.Trim();
        if (dialog.ShowDialog(this) == true) _folder.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
#else
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pick the update folder" };
        if (_folder.Text.Trim().Length > 0 && Directory.Exists(_folder.Text.Trim())) dialog.InitialDirectory = _folder.Text.Trim();
        if (dialog.ShowDialog(this) == true) _folder.Text = dialog.FolderName;
#endif
    }

    private void Fail(string text, Control? focus = null)
    {
        _error.Text = text;
        _error.Visibility = Visibility.Visible;
        focus?.Focus();
    }

    private void Make()
    {
        _error.Visibility = Visibility.Collapsed;
        var name = _name.Text.Trim();
        if (name.Length == 0) { Fail("Give the pack a name.", _name); return; }
        if (_id is null && _records.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Fail($"A pack named '{name}' was shared before. Pick it under Pack to share it again, or use another name.", _packs);
            return;
        }
        var items = _catalog.Where(i => _chosen.Contains(i.Key)).Select(i => i.Key).ToList();
        if (items.Count == 0) { Fail("Tick at least one tool.", _search); return; }
        var folder = _folder.Text.Trim().Trim('"');
        if (folder.Length > 0)
        {
            try
            {
                if (!Path.IsPathRooted(folder)) { Fail("The update folder needs a full path, such as \\\\server\\share\\Revit tools or a synced folder.", _folder); return; }
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Fail($"The update folder can't be reached ({ex.Message}). Check the path, or leave it empty to make the zip only.", _folder);
                return;
            }
        }
        var request = new SharedPacks.Request(name, items, folder, _note.Text.Trim(), SharedPacks.DefaultZipFolder, _auto.IsChecked == true,
            _mine.IsChecked == true && items.Contains("command:pre-submission"));
        SharedPacks.Result result;
        Brand.SetBusy(_make, true, "Making…");
        try { result = SharedPacks.Export(request, SharedTools.RibbonTool, Updater.CurrentText, _id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Fail("The zip was not made: " + ex.Message);
            return;
        }
        finally { Brand.SetBusy(_make, false); }
        Made = true;
        Close();
        Report(result);
    }

    private static void Report(SharedPacks.Result result)
    {
        var pack = result.Pack;
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = $"'{pack.Name}' is ready to share (revision {pack.Revision}, {pack.Tools.Count} tool(s))",
            MainContent = $"Send {Path.GetFileName(result.Zip)} to colleagues. They extract it and double-click Install TritoRevit.cmd; " +
                          "the tools are on the TritoRevit tab (after one Revit restart the first time)." +
                          (result.Published is not null
                              ? $"\n\nPublished to {result.Published}. Everyone who installed this pack gets this revision within a few minutes, with Revit open. A copy of the zip is there too, for new colleagues."
                              : pack.UpdateFrom.Length == 0 ? "\n\nNo update folder: to send changes later, share this pack again and send the new zip." : "") +
                          (result.Notes.Count > 0 ? "\n\n" + string.Join("\n", result.Notes) : ""),
            AllowCancellation = true,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Show the zip", result.Zip);
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Close");
        dialog.DefaultButton = TaskDialogResult.CommandLink1;
        if (dialog.Show() != TaskDialogResult.CommandLink1) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{result.Zip}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

/// <summary>
/// The Shared tools button on the TritoRevit tab: each installed pack with its revision, tools, update folder and
/// anything that was kept back, with Remove; Check for updates now; Add a pack from a zip.
/// </summary>
internal sealed class SharedToolsWindow : Window
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted);
    private readonly Button _check;
    private readonly UIApplication? _app;

    /// <param name="app">The command's Revit, for Report a problem (the window is modal, so still inside the command).</param>
    public SharedToolsWindow(UIApplication? app = null)
    {
        _app = app;
        Title = "Shared tools";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var body = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        DockPanel.SetDock(_status, Dock.Top);
        body.Children.Add(_status);

        var add = Brand.Button("Add a pack from a zip…");
        add.Click += (_, _) => AddZip();
        _check = Brand.Button("Check for updates now");
        _check.Click += async (_, _) => await CheckAsync();
        var close = Brand.Button("Close", Brand.Weight.Primary);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var problem = Brand.Button("Report a problem…");
        AutomationProperties.SetHelpText(problem, "Saves one small zip with a picture of Revit, the versions and what failed, in Documents\\TritoRevit - <pack>\\Bugs.");
        problem.Click += (_, _) => ReportProblem();
        problem.Visibility = _app is null ? Visibility.Collapsed : Visibility.Visible;
        var buttons = Brand.ButtonRow(problem, add, _check, close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);

        AutomationProperties.SetName(_list, "Installed shared packs");
        body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 460, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var root = new DockPanel();
        var header = Brand.Header("Shared tools", "Tools shared with you, on the TritoRevit tab. They update by themselves.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;
        Rebuild();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F5) { e.Handled = true; _ = CheckAsync(); } };
    }

    private void Rebuild(string? status = null)
    {
        _list.Children.Clear();
        var packs = SharedPacks.List();
        _status.Text = status ?? (packs.Count == 0
            ? "No shared tools are installed yet. Ask for a TritoRevit zip, or add one here with Add a pack from a zip."
            : $"{packs.Count} pack(s). Updates are looked for every few minutes while Revit is open.");
        foreach (var installed in packs)
        {
            var pack = installed.Pack;
            var row = new Grid { Margin = new Thickness(0, 4, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            text.Children.Add(Brand.Text($"{pack.Name}  ·  revision {pack.Revision}", bold: true));
            text.Children.Add(Brand.Text(string.Join(", ", pack.Tools.Select(t => t.Title)), Brand.K.Muted, size: 11));
            text.Children.Add(Brand.Text(pack.UpdateFrom.Length > 0 ? "Updates from " + pack.UpdateFrom : "No update folder: install a newer zip to update it.", Brand.K.Muted, size: 11));
            if (pack.Note.Length > 0) text.Children.Add(Brand.Text(pack.Note, Brand.K.Muted, size: 11));
            if (installed.Notes.Length > 0) text.Children.Add(Brand.Text(installed.Notes, Brand.K.WarningText, size: 11));
            row.Children.Add(text);
            var remove = Brand.Button("Remove", Brand.Weight.Danger);
            AutomationProperties.SetName(remove, "Remove " + pack.Name);
            remove.VerticalAlignment = VerticalAlignment.Top;
            remove.Click += (_, _) => Remove(pack);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            AutomationProperties.SetName(row, pack.Name);
            _list.Children.Add(row);
        }
    }

    private void Remove(SharedPackMath.Pack pack)
    {
        var ask = new TaskDialog(SharedTools.Product)
        {
            MainInstruction = $"Remove '{pack.Name}'?",
            MainContent = "Its buttons go from the TritoRevit tab within a few seconds, with the learned tools and recipes it installed (unless you changed them). Install its zip again to get it back.",
            AllowCancellation = true,
        };
        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Remove it");
        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Keep it");
        ask.DefaultButton = TaskDialogResult.CommandLink2;
        if (ask.Show() != TaskDialogResult.CommandLink1) return;
        try
        {
            SharedPacks.Remove(pack.Id);
            Rebuild($"'{pack.Name}' was removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Rebuild($"'{pack.Name}' couldn't be removed: {ex.Message}"); }
    }

    private void ReportProblem()
    {
        if (_app is null) return;
        var shot = Bugs.BugReport.Screenshot();
        if (Bugs.BugReport.Ask() is not { } description) return;
        try
        {
            var file = Bugs.BugReport.Create(_app, description, shot, "Shared tools, Report a problem");
            Rebuild("Problem report saved: " + file);
            Bugs.BugReport.Saved(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Rebuild("The problem report couldn't be saved: " + ex.Message);
        }
    }

    private void AddZip()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pick a TritoRevit zip", Filter = "Shared tools zip|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        var pack = SharedPacks.InstallZip(dialog.FileName, out var why);
        Rebuild(pack is null ? $"That zip was not installed: {why}." : $"'{pack.Name}' (revision {pack.Revision}) is installed: its tools are on the TritoRevit tab within a few seconds.");
    }

    private async Task CheckAsync()
    {
        if (Brand.IsBusy(_check)) return;
        Brand.SetBusy(_check, true, "Checking…");
        List<string> changed;
        try
        {
            changed = await Task.Run(() => SharedPacks.Sync()).ConfigureAwait(true);
            if (App.Shared)
                try { await TritoRuntime.CheckAsync(CancellationToken.None).ConfigureAwait(true); } catch (Exception) { /* checked again later */ }
        }
        finally { Brand.SetBusy(_check, false); }
        Rebuild(changed.Count == 0 ? "Everything is up to date." : "Updated: " + string.Join("; ", changed) + ".");
    }
}
