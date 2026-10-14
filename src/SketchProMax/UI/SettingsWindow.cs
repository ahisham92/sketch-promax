using System.Windows;
using System.Windows.Controls;
using SketchProMax.Settings;

namespace SketchProMax.UI;

/// <summary>Minimal settings dialog: API key, model, effort, approval gate, firm standards.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AgentSettings _settings = AgentSettings.Load();
    private readonly PasswordBox _apiKey = new();
    private readonly TextBox _model = new();
    private readonly ComboBox _effort = new() { ItemsSource = new[] { "low", "medium", "high", "xhigh", "max" } };
    private readonly CheckBox _approval = new() { Content = "Ask before changing the model" };
    private readonly TextBox _standards = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    public SettingsWindow()
    {
        Title = "Sketch ProMax settings";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        _model.Text = _settings.Model;
        _effort.SelectedItem = _settings.Effort;
        _approval.IsChecked = _settings.RequireApprovalForChanges;
        _standards.Text = _settings.FirmStandards;

        var save = new Button { Content = "Save", Width = 80, IsDefault = true, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => Save();

        var stack = new StackPanel { Margin = new Thickness(12) };
        void Row(string label, UIElement control)
        {
            stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2) });
            stack.Children.Add(control);
        }
        Row(_settings.EncryptedApiKey is null ? "Anthropic API key" : "Anthropic API key (saved; leave blank to keep)", _apiKey);
        Row("Model", _model);
        Row("Effort", _effort);
        stack.Children.Add(new Border { Height = 8 });
        stack.Children.Add(_approval);
        Row("Firm standards (plain English, added to every conversation)", _standards);
        stack.Children.Add(save);
        Content = stack;
    }

    private void Save()
    {
        if (_apiKey.Password.Length > 0) _settings.SetApiKey(_apiKey.Password);
        _settings.Model = string.IsNullOrWhiteSpace(_model.Text) ? "claude-opus-5-5" : _model.Text.Trim();
        _settings.Effort = _effort.SelectedItem as string ?? "high";
        _settings.RequireApprovalForChanges = _approval.IsChecked == true;
        _settings.FirmStandards = _standards.Text;
        _settings.Save();
        DialogResult = true;
    }
}
