using System.Windows;
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

    public SettingsWindow()
    {
        Title = "Sketch ProMax settings";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        _approval.IsChecked = _settings.RequireApprovalForChanges;
        _autoUpdate.IsChecked = _settings.AutoUpdate;
        _standards.Text = _settings.FirmStandards;
        _legendSeed.Text = _settings.LegendSeedPath ?? "";

        var save = new Button { Content = "Save", Width = 80, IsDefault = true, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => Save();

        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(_approval);
        stack.Children.Add(_autoUpdate);
        stack.Children.Add(new TextBlock
        {
            Text = "Firm standards (plain English). Claude follows these in every conversation, e.g. " +
                   "'Sheet numbers are A-xxx. View names: LEVEL - PURPOSE. Door tags use the Mark parameter.'",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4),
        });
        stack.Children.Add(_standards);
        stack.Children.Add(new TextBlock
        {
            Text = "Legend seed file (optional): a firm template or project (.rvt/.rte) with a legend. create_legend copies from it when a model has no legend.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4),
        });
        stack.Children.Add(_legendSeed);
        stack.Children.Add(new TextBlock
        {
            Text = "Changes apply to new chats in the Claude app.", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(save);
        Content = stack;
    }

    private void Save()
    {
        _settings.RequireApprovalForChanges = _approval.IsChecked == true;
        _settings.AutoUpdate = _autoUpdate.IsChecked == true;
        _settings.FirmStandards = _standards.Text;
        _settings.LegendSeedPath = _legendSeed.Text.Trim().Trim('"');
        _settings.Save();
        DialogResult = true;
    }
}
