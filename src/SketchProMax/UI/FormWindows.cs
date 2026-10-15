using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SketchProMax.UI;

/// <summary>
/// Asks for the inputs of a recipe or learned tool run from the ribbon, and doubles as the confirmation to run it.
/// Built from the tool's input schema (or the recipe's {placeholders}), so it needs no per-tool code.
/// </summary>
internal sealed class InputsWindow : Window
{
    public sealed record Field(string Key, string Label, string? Help, string Type, bool Required, string[]? Choices, string? Value);

    private readonly List<(Field Field, FrameworkElement Control)> _controls = [];
    private readonly TextBlock _error = new() { Foreground = Brand.Brush(Brand.Red), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox? _option;

    public Dictionary<string, string> Values { get; } = new();
    public bool OptionChecked => _option?.IsChecked == true;

    public InputsWindow(string title, string subtitle, string? intro, IReadOnlyList<string> details, IReadOnlyList<Field> fields,
        string runLabel, string? optionLabel = null)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        if (!string.IsNullOrWhiteSpace(intro))
            body.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        if (details.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var d in details)
                list.Children.Add(new TextBlock { Text = "•  " + d, TextWrapping = TextWrapping.Wrap, Foreground = Brand.Brush(Brand.Muted) });
            body.Children.Add(list);
        }

        foreach (var field in fields)
        {
            var label = new TextBlock
            {
                Text = field.Label + (field.Required ? " *" : ""), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2),
            };
            FrameworkElement control;
            if (field.Type == "boolean")
            {
                control = new CheckBox { Content = field.Label, IsChecked = field.Value is "true" or "True", Margin = new Thickness(0, 8, 0, 0) };
            }
            else if (field.Choices is { Length: > 0 } choices)
            {
                var combo = new ComboBox { IsEditable = false };
                if (!field.Required) combo.Items.Add("");
                foreach (var c in choices) combo.Items.Add(c);
                combo.SelectedItem = field.Value is { } v && choices.Contains(v) ? v : field.Required ? choices[0] : "";
                control = combo;
            }
            else
            {
                control = new TextBox { Text = field.Value ?? "", Padding = new Thickness(4, 2, 4, 2) };
            }
            if (field.Type != "boolean") body.Children.Add(label);
            body.Children.Add(control);
            if (!string.IsNullOrWhiteSpace(field.Help))
                body.Children.Add(new TextBlock { Text = field.Help, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brand.Brush(Brand.Muted) });
            _controls.Add((field, control));
        }

        if (optionLabel is not null)
        {
            _option = new CheckBox { Content = optionLabel, Margin = new Thickness(0, 12, 0, 0) };
            body.Children.Add(_option);
        }
        body.Children.Add(_error);

        var run = Brand.Button(runLabel, primary: true);
        run.IsDefault = true;
        run.Click += (_, _) => Submit();
        var cancel = Brand.Button("Cancel");
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(run);
        body.Children.Add(buttons);

        var root = new DockPanel();
        var header = Brand.Header(title, subtitle);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += (_, _) => (_controls.FirstOrDefault().Control as Control ?? run).Focus();
    }

    private void Submit()
    {
        Values.Clear();
        var missing = new List<string>();
        foreach (var (field, control) in _controls)
        {
            var value = control switch
            {
                CheckBox box => box.IsChecked == true ? "true" : "false",
                ComboBox combo => combo.SelectedItem as string ?? "",
                TextBox text => text.Text.Trim(),
                _ => "",
            };
            if (value.Length == 0)
            {
                if (field.Required) missing.Add(field.Label);
                continue;
            }
            Values[field.Key] = value;
        }
        if (missing.Count > 0)
        {
            _error.Text = "Please fill in: " + string.Join(", ", missing);
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}

/// <summary>
/// Shows what a quick tool or check did, without blocking Revit: a status line, the full result to read or copy, and
/// the folder of any file it wrote.
/// </summary>
internal sealed class ResultWindow : Window
{
    public ResultWindow(string title, bool ok, string headline, string text)
    {
        Title = title;
        Width = 640;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);

        var status = new TextBlock
        {
            Text = (ok ? "✓  " : "✕  ") + headline, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = Brand.Brush(ok ? Brand.Green : Brand.Red), Margin = new Thickness(0, 0, 0, 10),
        };
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var file = FindFile(text);
        if (file is not null)
        {
            var open = Brand.Button("Open folder");
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
                catch (Exception) { }
            };
            buttons.Children.Add(open);
        }
        var copy = Brand.Button("Copy");
        copy.Click += (_, _) => ClipboardText.TrySet(text);
        var close = Brand.Button("Close", primary: true);
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var body = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(status);
        body.Children.Add(buttons);
        body.Children.Add(box);

        var root = new DockPanel();
        var header = Brand.Header(title, "Ran in Revit without the AI. Ctrl+Z undoes any change.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;
    }

    /// <summary>The first existing file path in a result (a CSV, a BCF, a report), if any.</summary>
    private static string? FindFile(string text)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z]:\\\\?[^""\r\n<>|*?]+?\.(csv|bcf|bcfzip|xlsx|txt|json|pdf|html)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var path = m.Value.Replace(@"\\", @"\");
            try { if (File.Exists(path)) return path; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return null;
    }
}

internal static class ClipboardText
{
    /// <summary>Sets the clipboard, retrying briefly when another program holds it.</summary>
    public static bool TrySet(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(60); }
        }
        return false;
    }
}
