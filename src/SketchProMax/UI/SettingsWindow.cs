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
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _error.Text = $"Couldn't save the settings: {ex.Message}\nClose any program that has the settings file open " +
                          $"(or check you can write to {AgentSettings.FilePath}), then press Save again. Nothing was changed.";
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
