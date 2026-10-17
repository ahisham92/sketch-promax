using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Settings;

namespace SketchProMax.UI;

/// <summary>Settings dialog opened from the ribbon: approval gate and firm standards.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AgentSettings _settings = AgentSettings.Load();
    private readonly CheckBox _approval = new() { Content = "Ask me in Revit before Claude changes the model" };
    private readonly CheckBox _autoUpdate = new() { Content = "Update Sketch ProMax automatically", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _standards = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
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

        _approval.IsChecked = _settings.RequireApprovalForChanges;
        _autoUpdate.IsChecked = _settings.AutoUpdate;
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
        stack.Children.Add(_approval);
        stack.Children.Add(_autoUpdate);
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
        Loaded += (_, _) => _approval.Focus();
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
        _settings.RequireApprovalForChanges = _approval.IsChecked == true;
        _settings.AutoUpdate = _autoUpdate.IsChecked == true;
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
