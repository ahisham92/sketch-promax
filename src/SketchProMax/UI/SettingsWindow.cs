using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Settings;

namespace SketchProMax.UI;

/// <summary>Settings dialog opened from the ribbon: when to ask before changes, firm standards, time saved.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AgentSettings _settings = AgentSettings.Load();
    private readonly List<(RadioButton Button, Tools.ApprovalLevel Level)> _levels = [];
    private readonly List<string> _noAsk;
    private readonly StackPanel _noAskList = new();
    private readonly CheckBox _autoUpdate = new() { Content = "Update Sketch ProMax automatically", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _openItems = new()
    {
        Content = "Allow issue with open items (each such issue says so, with the reason, in its issue record)", Margin = new Thickness(0, 6, 0, 0),
    };
    private readonly TextBox _standards = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private readonly TextBox _legendSeed = new();
    private readonly TextBlock _seedHint = Brand.Text("", Brand.K.WarningText, size: 11);
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText);
    private readonly CheckBox _timeOn = new() { Content = "Count working time and time saved in each model", Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _idle = new() { Width = 56, MinHeight = 26, Margin = new Thickness(6, 0, 6, 0) };
    private List<Tools.TimeRate>? _rates;

    public SettingsWindow()
    {
        Title = "Sketch ProMax settings";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Brand.UseTheme(this);

        _noAsk = [.. _settings.NoAskTools ?? []];
        _autoUpdate.IsChecked = _settings.AutoUpdate;
        _openItems.IsChecked = _settings.AllowIssueWithOpenItems;
        _standards.Text = _settings.FirmStandards;
        _legendSeed.Text = _settings.LegendSeedPath ?? "";
        _timeOn.IsChecked = _settings.TimeTracking;
        _idle.Text = _settings.IdleMinutes.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        _rates = _settings.TimeRates;

        // One primary action: Save (Enter, except inside the standards box where Enter starts a new line). Esc cancels.
        var save = Brand.Button("Save", Brand.Weight.Primary);
        save.IsDefault = true;
        save.Click += (_, _) => Save();
        var cancel = Brand.Button("Cancel");
        cancel.IsCancel = true;

        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(ApprovalSection());
        stack.Children.Add(_autoUpdate);
        AutomationProperties.SetHelpText(_openItems, "Off: an issue waits until every item of the issue check is fixed or accepted with a reason.");
        stack.Children.Add(_openItems);
        var standardsLabel = Brand.Text(
            "Firm standards (plain English). Claude follows these in every conversation, e.g. " +
            "'Sheet numbers are A-xxx. View names: LEVEL - PURPOSE. Door tags use the Mark parameter.'");
        standardsLabel.Margin = new Thickness(0, 12, 0, 4);
        stack.Children.Add(standardsLabel);
        AutomationProperties.SetName(_standards, "Firm standards");
        AutomationProperties.SetHelpText(_standards, standardsLabel.Text);
        stack.Children.Add(_standards);
        var seedLabel = Brand.Text(
            "Legend seed file (optional): a firm template or project (.rvt/.rte) with a legend. create_legend copies from it when a model has no legend.");
        seedLabel.Margin = new Thickness(0, 12, 0, 4);
        stack.Children.Add(seedLabel);
        AutomationProperties.SetName(_legendSeed, "Legend seed file");
        stack.Children.Add(_legendSeed);
        _seedHint.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(_seedHint);
        _legendSeed.TextChanged += (_, _) => CheckSeed();
        CheckSeed();
        stack.Children.Add(TimeSection());
        var note = Brand.Text("Changes apply to new chats in the Claude app.", Brand.K.Muted);
        note.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(note);
        _error.Margin = new Thickness(0, 8, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        stack.Children.Add(_error);
        stack.Children.Add(Brand.ButtonRow(cancel, save));

        var root = new DockPanel();
        var header = Brand.Header("Settings", "How Claude works in this Revit.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(stack);
        Content = root;
        Loaded += (_, _) => _levels.FirstOrDefault(l => l.Button.IsChecked == true).Button?.Focus();
    }

    /// <summary>
    /// When Revit asks before Claude changes something: the three levels, and the tools the user said not to ask about
    /// again, each with "Ask again". What is always asked (code, deletes that take other elements, overwriting files,
    /// what-ifs, the issue gate) is said under the levels.
    /// </summary>
    private StackPanel ApprovalSection()
    {
        var section = new StackPanel();
        var heading = Brand.Text("Ask before changes", bold: true);
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
        section.Children.Add(heading);
        var current = _settings.Approvals;
        foreach (var level in Tools.ApprovalPolicy.Levels)
        {
            var radio = new RadioButton
            {
                GroupName = "approvals", IsChecked = level == current, Margin = new Thickness(0, 6, 0, 0), MinHeight = 24,
                Content = new StackPanel
                {
                    Children =
                    {
                        Brand.Text(Tools.ApprovalPolicy.Label(level) + (level == Tools.ApprovalLevel.RiskyOnly ? " (recommended)" : "")),
                        Brand.Text(Tools.ApprovalPolicy.Hint(level) + ".", Brand.K.Muted, size: 11),
                    },
                },
            };
            AutomationProperties.SetName(radio, Tools.ApprovalPolicy.Label(level));
            AutomationProperties.SetHelpText(radio, Tools.ApprovalPolicy.Hint(level));
            _levels.Add((radio, level));
            section.Children.Add(radio);
        }
        var always = Brand.Text(
            "Always asked, whatever you choose: code Claude wrote, deletes that take other elements with them, overwriting a " +
            "file outside Documents\\SketchProMax, what-ifs, value engineering on the open model, and accepting or issuing past " +
            "open issue check items. A brief you run counts as approving its task.", Brand.K.Muted, size: 11);
        always.Margin = new Thickness(0, 6, 0, 0);
        section.Children.Add(always);
        var noAskHeading = Brand.Text("Not asked again", bold: true);
        noAskHeading.Margin = new Thickness(0, 10, 0, 2);
        section.Children.Add(noAskHeading);
        section.Children.Add(_noAskList);
        FillNoAsk();
        return section;
    }

    /// <summary>The tools picked with "Don't ask again", each with Ask again, or a line saying there are none.</summary>
    private void FillNoAsk()
    {
        _noAskList.Children.Clear();
        if (_noAsk.Count == 0)
        {
            _noAskList.Children.Add(Brand.Text("None. In an approval dialog, \"Don't ask again\" adds a tool here.", Brand.K.Muted, size: 11));
            return;
        }
        foreach (var tool in _noAsk.OrderBy(t => t, StringComparer.Ordinal).ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var again = Brand.Button("Ask again");
            again.MinHeight = 24;
            AutomationProperties.SetName(again, "Ask again for " + Tools.ApprovalPolicy.Friendly(tool));
            again.Click += (_, _) =>
            {
                _noAsk.Remove(tool);
                FillNoAsk();
                (_noAskList.Children.OfType<DockPanel>().FirstOrDefault()?.Children.OfType<Button>().FirstOrDefault() as UIElement ?? _levels[0].Button).Focus();
            };
            DockPanel.SetDock(again, Dock.Right);
            row.Children.Add(again);
            var name = Brand.Text(Tools.ApprovalPolicy.Friendly(tool));
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            _noAskList.Children.Add(row);
        }
        if (_noAsk.Count > 1)
        {
            var all = Brand.Button("Ask again for all");
            all.HorizontalAlignment = HorizontalAlignment.Left;
            all.Margin = new Thickness(0, 4, 0, 0);
            all.MinHeight = 24;
            all.Click += (_, _) => { _noAsk.Clear(); FillNoAsk(); _levels[0].Button.Focus(); };
            _noAskList.Children.Add(all);
        }
    }

    /// <summary>Time saved and working time (Revit/TimeLog): on or off, the idle threshold, the by-hand rates, who sees the totals.</summary>
    private StackPanel TimeSection()
    {
        var section = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var heading = Brand.Text("Time saved and working time", bold: true);
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
        section.Children.Add(heading);
        section.Children.Add(_timeOn);
        var idleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var before = Brand.Text("Stop counting after");
        before.VerticalAlignment = VerticalAlignment.Center;
        idleRow.Children.Add(before);
        AutomationProperties.SetName(_idle, "Minutes without activity before working time stops counting");
        idleRow.Children.Add(_idle);
        var after = Brand.Text("minutes without activity");
        after.VerticalAlignment = VerticalAlignment.Center;
        idleRow.Children.Add(after);
        section.Children.Add(idleRow);
        var ratesRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var rates = Brand.Button("Edit by-hand rates…");
        rates.Margin = new Thickness(0);
        AutomationProperties.SetHelpText(rates, "Minutes per run and per item for each tool. The built-in ones are estimates.");
        rates.Click += (_, _) =>
        {
            var window = new TimeRatesWindow(_rates) { Owner = this };
            if (window.ShowDialog() == true) _rates = window.Result;
        };
        ratesRow.Children.Add(rates);
        var estimate = Brand.Text("Built-in rates are estimates.", Brand.K.Muted, size: 11);
        estimate.VerticalAlignment = VerticalAlignment.Center;
        estimate.Margin = new Thickness(8, 0, 0, 0);
        ratesRow.Children.Add(estimate);
        section.Children.Add(ratesRow);
        var shared = Brand.Text("The totals are kept in the model and in Project Information, per user: in a shared model, anyone who opens it can see them.",
            Brand.K.Muted, size: 11);
        shared.Margin = new Thickness(0, 6, 0, 0);
        section.Children.Add(shared);
        return section;
    }

    /// <summary>Says straight away when the legend seed path points nowhere (saving still works; the tool then ignores it).</summary>
    private void CheckSeed()
    {
        var path = _legendSeed.Text.Trim().Trim('"');
        var missing = false;
        if (path.Length > 0)
        {
            try { missing = !File.Exists(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { missing = true; }
        }
        _seedHint.Text = missing ? "No file at this path. Check it (copy the path from File Explorer) or leave it empty." : "";
        _seedHint.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save()
    {
        _settings.Approvals = _levels.FirstOrDefault(l => l.Button.IsChecked == true).Level;
        _settings.NoAskTools = _noAsk.Count > 0 ? _noAsk : null;
        _settings.AutoUpdate = _autoUpdate.IsChecked == true;
        _settings.AllowIssueWithOpenItems = _openItems.IsChecked == true;
        _settings.FirmStandards = _standards.Text;
        _settings.LegendSeedPath = _legendSeed.Text.Trim().Trim('"');
        if (!double.TryParse(_idle.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var idle) || idle < 1 || idle > 60)
        {
            _error.Text = "Type the idle time as a number of minutes from 1 to 60 (5 is usual), then press Save again.";
            _error.Visibility = Visibility.Visible;
            _idle.Focus();
            return;
        }
        _settings.TimeTracking = _timeOn.IsChecked == true;
        _settings.IdleMinutes = idle;
        _settings.TimeRates = _rates is { Count: > 0 } ? _rates : null;
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _error.Text = $"Couldn't save the settings: {ex.Message}\nClose any program that has the settings file open " +
                          $"(or check you can write to {AgentSettings.FilePath}), then press Save again. Nothing was changed.";
            _error.Visibility = Visibility.Visible;
            return;
        }
        Revit.TimeLog.ReadSettings();
        DialogResult = true;
    }
}
