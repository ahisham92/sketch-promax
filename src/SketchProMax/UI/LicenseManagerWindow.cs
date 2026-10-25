using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Licensing;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// The License manager, on the owner's PC only (Help > License there): make a key for a PC code, change a key's end date,
/// cancel a key, and the list of everyone licensed, exportable to Excel. Keys are signed with the owner's signing secret,
/// kept on this PC protected by Windows (Licensing/LicenseState). A shorter date or a cancel reaches a PC that already has
/// its key only through the published changes list: the window gives the line to send for publishing.
/// </summary>
internal sealed class LicenseManagerWindow : Window
{
    private IssuedList _list = IssuedList.Load();
    private readonly ListBox _entries = new() { MinHeight = 150, MaxHeight = 240 };
    private readonly TextBox _code = Field("PC code"), _name = Field("Name"), _note = Field("Note (optional)");
    private readonly DatePicker _ends = new() { MinHeight = 28, SelectedDate = DateTime.Today.AddYears(1) };
    private readonly CheckBox _noEnd = new() { Content = "No end date", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _key = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54 };
    private readonly DatePicker _newEnds = new() { MinHeight = 28 };
    private readonly CheckBox _newNoEnd = new() { Content = "No end date", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _detail = Brand.Text("", Brand.K.Muted, 12);
    private readonly TextBlock _pending = Brand.Text("", Brand.K.WarningText, 12);
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted, 12);
    private readonly StackPanel _selected = new() { Visibility = Visibility.Collapsed };
    private readonly Button _copyLine, _published, _publish;
    private readonly TextBlock _tokenState = Brand.Text("", Brand.K.Muted, 12);
    private string? _secret = LicenseState.SigningSecret();
    private static LicenseManagerWindow? _open;

    public static Autodesk.Revit.UI.Result Open()
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return Autodesk.Revit.UI.Result.Succeeded;
        }
        _open = new LicenseManagerWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    /// <summary>The window, not shown (the field test builds it).</summary>
    internal static Window ForTest() => new LicenseManagerWindow();

    private LicenseManagerWindow()
    {
        Title = "Sketch ProMax: License manager";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));

        var page = new StackPanel { Margin = new Thickness(18, 10, 18, 14) };
        if (_secret is null) page.Children.Add(SecretBox());

        // ---- New key
        page.Children.Add(LicenseWindow.Label("New key"));
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        AddRow(grid, 0, "PC code", _code);
        AddRow(grid, 1, "Name", _name);
        var endsRow = new StackPanel { Orientation = Orientation.Horizontal };
        AutomationProperties.SetName(_ends, "End date");
        endsRow.Children.Add(_ends);
        endsRow.Children.Add(_noEnd);
        _noEnd.Checked += (_, _) => _ends.IsEnabled = false;
        _noEnd.Unchecked += (_, _) => _ends.IsEnabled = true;
        AddRow(grid, 2, "Ends", endsRow);
        AddRow(grid, 3, "Note", _note);
        page.Children.Add(grid);
        var make = RichUi.Button(RichUi.Icon.Add, "Make key", Brand.Weight.Primary);
        make.Click += (_, _) => Make();
        page.Children.Add(Brand.ButtonRow(make));

        // ---- The list
        page.Children.Add(LicenseWindow.Label("Licensed PCs"));
        AutomationProperties.SetName(_entries, "Licensed PCs");
        _entries.Margin = new Thickness(0, 4, 0, 0);
        _entries.SelectionChanged += (_, _) => ShowSelected();
        page.Children.Add(_entries);

        // ---- The selected key
        _detail.TextWrapping = TextWrapping.Wrap;
        _detail.Margin = new Thickness(0, 8, 0, 4);
        _selected.Children.Add(_detail);
        AutomationProperties.SetName(_key, "Key");
        _selected.Children.Add(_key);
        var copyKey = RichUi.Button(RichUi.Icon.Export, "Copy key");
        copyKey.Click += (_, _) => Copy(_key.Text, "Key copied. Send it to the person; it works only on their PC.");
        var cancel = RichUi.Button(RichUi.Icon.Delete, "Cancel key", Brand.Weight.Danger);
        cancel.Click += async (_, _) => await CancelKey();
        _selected.Children.Add(Brand.ButtonRow(cancel, copyKey));

        var changeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        changeRow.Children.Add(new TextBlock { Text = "New end date  ", VerticalAlignment = VerticalAlignment.Center });
        Brand.Fg((TextBlock)changeRow.Children[0], Brand.K.Text);
        AutomationProperties.SetName(_newEnds, "New end date");
        changeRow.Children.Add(_newEnds);
        changeRow.Children.Add(_newNoEnd);
        _newNoEnd.Checked += (_, _) => _newEnds.IsEnabled = false;
        _newNoEnd.Unchecked += (_, _) => _newEnds.IsEnabled = true;
        var change = RichUi.Button(RichUi.Icon.Edit, "Change end date");
        change.Margin = new Thickness(10, 0, 0, 0);
        change.Click += async (_, _) => await ChangeEnd();
        changeRow.Children.Add(change);
        _selected.Children.Add(changeRow);

        _pending.TextWrapping = TextWrapping.Wrap;
        _pending.Margin = new Thickness(0, 8, 0, 0);
        _selected.Children.Add(_pending);
        _copyLine = RichUi.Button(RichUi.Icon.Export, "Copy line");
        _copyLine.Click += (_, _) => { if (Current() is { } e) Copy("license change: " + e.PendingChange, "Line copied. Send it in the Sketch ProMax project to publish it."); };
        _published = RichUi.Button(RichUi.Icon.Save, "Mark published");
        _published.Click += (_, _) => MarkPublished();
        _publish = RichUi.Button(RichUi.Icon.Export, "Publish now", Brand.Weight.Primary);
        _publish.Click += async (_, _) => await Publish();
        _selected.Children.Add(Brand.ButtonRow(_published, _copyLine, _publish));
        page.Children.Add(_selected);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        page.Children.Add(_status);

        page.Children.Add(TokenBox());

        var export = RichUi.Button(RichUi.Icon.Export, "Export to Excel");
        export.Click += (_, _) => Export();
        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        page.Children.Add(Brand.ButtonRow(export, close));

        var root = new DockPanel();
        var header = Brand.Header("License manager", "Keys for other PCs. Only your PC has this window.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 820 });
        Content = root;
        Fill();
        Loaded += (_, _) => _code.Focus();
    }

    /// <summary>Shown while this PC has no signing secret: paste it once (the backup the owner was given).</summary>
    private FrameworkElement SecretBox()
    {
        var box = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var note = LicenseWindow.Para("This PC doesn't have your signing secret yet, so it can't make keys. Paste it once below " +
                                      "(it starts with SPMSIGN1). It is kept on this PC, protected by Windows for your user only.", Brand.K.WarningText);
        box.Children.Add(note);
        var secret = new PasswordBox { MinHeight = 28, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(secret, "Signing secret");
        box.Children.Add(secret);
        var save = RichUi.Button(RichUi.Icon.Save, "Save secret");
        save.Click += (_, _) =>
        {
            var text = secret.Password.Trim();
            if (!LicenseKey.MatchesBuiltIn(text)) { Error("That isn't the signing secret for this Sketch ProMax."); return; }
            try { LicenseState.SaveSigningSecret(text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Error($"The secret couldn't be saved ({ex.Message}).");
                return;
            }
            _secret = text;
            secret.Clear();
            box.Visibility = Visibility.Collapsed;
            Info("Signing secret saved. You can make keys now.");
        };
        box.Children.Add(Brand.ButtonRow(save));
        return box;
    }

    private void Fill(string? select = null)
    {
        _entries.Items.Clear();
        foreach (var e in _list.Entries.OrderByDescending(e => e.Active).ThenByDescending(e => e.Issued).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var ends = e.Ends is { } d ? d.ToString("d MMM yyyy") : "no end";
            var item = new ListBoxItem { Content = $"{e.Name}   ·   {e.PcCode}   ·   until {ends}   ·   {e.Status(DateTime.Today)}", Tag = e };
            AutomationProperties.SetName(item, $"{e.Name}, {e.PcCode}, until {ends}, {e.Status(DateTime.Today)}");
            _entries.Items.Add(item);
            if (e.Id == select) _entries.SelectedItem = item;
        }
        if (_entries.Items.Count == 0) _entries.Items.Add(new ListBoxItem { Content = "No keys yet. Make the first one above.", IsEnabled = false });
        ShowSelected();
    }

    private IssuedList.Entry? Current() => (_entries.SelectedItem as ListBoxItem)?.Tag as IssuedList.Entry;

    private void ShowSelected()
    {
        var e = Current();
        _selected.Visibility = e is null ? Visibility.Collapsed : Visibility.Visible;
        if (e is null) return;
        _detail.Text = $"{e.Name}, PC {e.PcCode}. Key {e.Id}, made {e.Issued:d MMM yyyy}, " +
                       (e.Ends is { } d ? $"ends {d:d MMM yyyy}" : "no end date") + $". {e.Status(DateTime.Today)}." +
                       (e.Note.Length > 0 ? " Note: " + e.Note : "");
        _key.Text = e.Key;
        _newEnds.SelectedDate = e.Ends ?? DateTime.Today.AddYears(1);
        _newNoEnd.IsChecked = false;
        var pending = e.PendingChange.Length > 0;
        var token = ChangesPublisher.HasToken;
        _pending.Text = !pending ? "" : token
            ? "This key still works on that PC until the change is published. Click Publish now (their PC picks it up at its next Revit start)."
            : $"This key still works on that PC until the change is published. Add your GitHub token below to publish from here, or send this line in the Sketch ProMax project: license change: {e.PendingChange}";
        _pending.Visibility = _published.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        _copyLine.Visibility = pending && !token ? Visibility.Visible : Visibility.Collapsed;
        _publish.Visibility = pending && token ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Make()
    {
        if (_secret is null) { Error("Save your signing secret first (the box at the top)."); return; }
        DateTime? ends = _noEnd.IsChecked == true ? null : _ends.SelectedDate;
        if (_noEnd.IsChecked != true && ends is null) { Error("Pick an end date, or tick No end date."); _ends.Focus(); return; }
        try
        {
            var entry = _list.Issue(_secret, _code.Text, _name.Text, ends, _note.Text, DateTime.Today);
            _list.Save();
            _code.Clear();
            _name.Clear();
            _note.Clear();
            Fill(entry.Id);
            Copy(entry.Key, $"Key made for {entry.Name} and copied. Send it to them; it works only on PC {entry.PcCode}.");
        }
        catch (ArgumentException ex) { Error(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error($"The list couldn't be saved ({ex.Message})."); }
    }

    private async Task ChangeEnd()
    {
        if (Current() is not { } e) return;
        if (_secret is null) { Error("Save your signing secret first (the box at the top)."); return; }
        if (!e.Active) { Error("This key was already replaced or cancelled. Pick the person's newest key."); return; }
        DateTime? ends = _newNoEnd.IsChecked == true ? null : _newEnds.SelectedDate;
        if (_newNoEnd.IsChecked != true && ends is null) { Error("Pick the new end date, or tick No end date."); return; }
        try
        {
            var next = _list.ChangeEnd(_secret, e, ends, DateTime.Today);
            _list.Save();
            Fill(next.Id);
            Copy(next.Key, $"New key for {next.Name} copied" + (next.Ends is { } d ? $" (until {d:d MMM yyyy})" : " (no end date)") + ". Send it to them." +
                           (e.PendingChange.Length > 0 && !ChangesPublisher.HasToken ? " The date is earlier than their old key's, so also publish the change shown on the old key." : ""));
            if (e.PendingChange.Length > 0 && ChangesPublisher.HasToken && await PublishEntry(e))
                Info($"New key for {next.Name} copied, and the earlier date published for their old key. Send them the new key.");
        }
        catch (ArgumentException ex) { Error(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error($"The list couldn't be saved ({ex.Message})."); }
    }

    private async Task CancelKey()
    {
        if (Current() is not { } e || !e.Active) return;
        var sure = MessageBox.Show(this, $"Cancel {e.Name}'s key? It stops working on their PC once the change is published.",
            "Sketch ProMax: Cancel key", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (sure != MessageBoxResult.Yes) return;
        _list.Cancel(e);
        try { _list.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error($"The list couldn't be saved ({ex.Message})."); return; }
        Fill(e.Id);
        if (!ChangesPublisher.HasToken) { Info("Cancelled here. Copy the line below and send it in the Sketch ProMax project to publish it."); return; }
        if (await PublishEntry(e)) Info($"{e.Name}'s key is cancelled and published. It stops working the next time Revit starts on their PC.");
    }

    private async Task Publish()
    {
        if (Current() is not { } e || e.PendingChange.Length == 0) return;
        if (await PublishEntry(e)) Info("Published. Their PC applies it the next time Revit starts there.");
    }

    /// <summary>Publishes <paramref name="e"/>'s pending change; true when done (an error is shown otherwise).</summary>
    private async Task<bool> PublishEntry(IssuedList.Entry e)
    {
        Brand.SetBusy(_publish, true, "Publishing…");
        Info("Publishing the change…");
        try
        {
            await ChangesPublisher.PublishAsync([e.PendingChange], CancellationToken.None);
            e.PendingChange = "";
            _list.Save();
            Fill(Current()?.Id ?? e.Id);
            return true;
        }
        catch (InvalidOperationException ex) { Error(ex.Message); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Error($"Couldn't reach GitHub ({ex.Message}). Check the internet connection, then select the old key and click Publish now.");
        }
        finally { Brand.SetBusy(_publish, false); }
        return false;
    }

    /// <summary>The GitHub token that lets this window publish changes (a fine-grained token for this repository only).</summary>
    private FrameworkElement TokenBox()
    {
        var box = new StackPanel();
        box.Children.Add(LicenseWindow.Label("Publishing changes"));
        _tokenState.TextWrapping = TextWrapping.Wrap;
        _tokenState.Margin = new Thickness(0, 4, 0, 0);
        box.Children.Add(_tokenState);
        var entry = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        entry.Children.Add(LicenseWindow.Para("On GitHub: Make token > name it Sketch ProMax licenses > Repository access: Only select repositories, " +
                                              "pick sketch-promax > Permissions: Contents, Read and write > Generate token. Copy it and paste it here.", Brand.K.Muted));
        var tokenBox = new PasswordBox { MinHeight = 28, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(tokenBox, "GitHub token");
        entry.Children.Add(tokenBox);
        var github = RichUi.Button(RichUi.Icon.Open, "Make token on GitHub");
        github.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(ChangesPublisher.NewTokenUrl) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { Error($"Couldn't open the browser ({ex.Message})."); }
        };
        var save = RichUi.Button(RichUi.Icon.Save, "Save token");
        save.Click += (_, _) =>
        {
            var t = tokenBox.Password.Trim();
            if (t.Length < 20) { Error("Paste the whole token from GitHub (it starts with github_pat_)."); return; }
            try { ChangesPublisher.SaveToken(t); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Error($"The token couldn't be saved ({ex.Message}).");
                return;
            }
            tokenBox.Clear();
            entry.Visibility = Visibility.Collapsed;
            ShowToken();
            ShowSelected();
            Info("GitHub token saved. Cancelled keys and earlier dates can be published from here now.");
        };
        entry.Children.Add(Brand.ButtonRow(github, save));
        box.Children.Add(entry);
        _tokenButton = RichUi.Button(RichUi.Icon.Edit, "Add token");
        _tokenButton.Click += (_, _) =>
        {
            if (ChangesPublisher.HasToken && entry.Visibility != Visibility.Visible)
            {
                ChangesPublisher.ForgetToken();
                ShowToken();
                ShowSelected();
                Info("GitHub token removed from this PC.");
                return;
            }
            entry.Visibility = entry.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (entry.Visibility == Visibility.Visible) tokenBox.Focus();
        };
        box.Children.Add(Brand.ButtonRow(_tokenButton));
        ShowToken();
        return box;
    }

    private Button? _tokenButton;

    private void ShowToken()
    {
        var has = ChangesPublisher.HasToken;
        _tokenState.Text = has
            ? "GitHub token added: cancelled keys and earlier end dates are published from here."
            : "Making keys and later end dates need nothing more. To cancel a key or set an earlier date from here, add a GitHub token once " +
              "(other PCs read changes from your GitHub). Without it, the window gives you a line to send in the Sketch ProMax project.";
        if (_tokenButton is not null) RichUi.Set(_tokenButton, has ? RichUi.Icon.Delete : RichUi.Icon.Edit, has ? "Remove token" : "Add token");
    }

    private void MarkPublished()
    {
        if (Current() is not { } e) return;
        e.PendingChange = "";
        try { _list.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error($"The list couldn't be saved ({ex.Message})."); return; }
        Fill(e.Id);
        Info("Marked as published.");
    }

    private void Export()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Licenses.csv");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, _list.Csv(DateTime.Today), new System.Text.UTF8Encoding(true));
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
            Info($"Saved {file}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Error($"The list couldn't be exported ({ex.Message}). If Licenses.csv is open in Excel, close it and try again.");
        }
    }

    private void Copy(string text, string done)
    {
        try { Clipboard.SetText(text); Info(done); }
        catch (System.Runtime.InteropServices.ExternalException) { Error("The clipboard is busy. Try again."); }
    }

    private void Info(string text)
    {
        _status.Text = text;
        Brand.Fg(_status, Brand.K.SuccessText);
    }

    private void Error(string text)
    {
        _status.Text = text;
        Brand.Fg(_status, Brand.K.DangerText);
    }

    private static TextBox Field(string name)
    {
        var box = new TextBox { MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static void AddRow(Grid grid, int row, string label, UIElement field)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = Brand.Text(label, Brand.K.Text, 12);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(0, 4, 12, 4);
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        if (field is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
    }
}
