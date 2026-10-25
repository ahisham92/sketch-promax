using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Tools;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// The small windows behind project checklists: the password for the Default checklist, a name, and the new names a
/// rename fix suggests. Modal; no Revit API.
/// </summary>
internal static class ChecklistProjectWindows
{
    /// <summary>Asks for the editing password; true when it is right. Wrong answers can be tried again.</summary>
    public static bool Unlock(Window owner, string what)
    {
        while (true)
        {
            var entered = Ask(owner, "Password for the Default checklist", $"The Default checklist is protected. Type the password to {what}.", "", password: true);
            if (entered is null) return false;
            if (ChecklistProjects.PasswordIs(entered)) return true;
            MessageBox.Show(owner, "That isn't the password. Ask whoever looks after the Default checklist for it.", "Sketch ProMax", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Asks the current password (unless already given), then the new one twice, and saves it.</summary>
    public static void ChangePassword(Window owner, bool unlocked)
    {
        if (!unlocked && !Unlock(owner, "change it")) return;
        var first = Ask(owner, "New password", "Type the new password for the Default checklist (at least 4 characters).", "", password: true);
        if (first is null) return;
        if (first.Length < 4) { MessageBox.Show(owner, "Use at least 4 characters. The password wasn't changed.", "Sketch ProMax", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var again = Ask(owner, "New password", "Type the new password again.", "", password: true);
        if (again is null) return;
        if (again != first) { MessageBox.Show(owner, "The two don't match. The password wasn't changed.", "Sketch ProMax", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            ChecklistProjects.SetPassword(first);
            MessageBox.Show(owner, "The password is changed on this PC.", "Sketch ProMax", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, $"Couldn't save the password ({ex.Message}).", "Sketch ProMax", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>A one-line question; the text typed, or null when cancelled.</summary>
    public static string? Ask(Window owner, string title, string question, string initial, bool password = false)
    {
        var window = new Window
        {
            Title = "Sketch ProMax: " + title, Owner = owner, Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        Brand.Prepare(window);
        var text = Brand.Text(question, size: 12.5);
        text.TextWrapping = TextWrapping.Wrap;
        Control input = password ? new PasswordBox { MinHeight = 28, Margin = new Thickness(0, 10, 0, 0) } : new TextBox { Text = initial, MinHeight = 28, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(input, title);
        string? result = null;
        var ok = RichUi.Button(RichUi.Icon.Next, "OK", Brand.Weight.Primary);
        ok.IsDefault = true;
        ok.Click += (_, _) => { result = input is PasswordBox p ? p.Password : ((TextBox)input).Text; window.DialogResult = true; };
        var cancel = RichUi.Button(RichUi.Icon.Close, "Cancel");
        cancel.IsCancel = true;
        var buttons = Brand.ButtonRow(cancel, ok);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        window.Content = new StackPanel { Margin = new Thickness(18, 16, 18, 16), Children = { text, input, buttons } };
        window.Loaded += (_, _) => { input.Focus(); if (input is TextBox t) t.SelectAll(); };
        return window.ShowDialog() == true ? result : null;
    }

    /// <summary>
    /// The renames a fix suggests (or the values it fills: <paramref name="intro"/> and <paramref name="ok"/> say so), each
    /// with a tick and its new name to change: the new names in the same order (null for one unticked), or null when cancelled.
    /// </summary>
    public static List<string?>? EditNames(Window owner, string title, List<(string Old, string New)> rows, string? intro = null, string ok = "Rename")
    {
        var window = new Window
        {
            Title = "Sketch ProMax: " + title, Owner = owner, Width = 760, Height = 560, ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        Brand.Prepare(window);
        var top = Brand.Text(intro ?? $"{rows.Count} types to rename. Change any new name, or untick the ones to keep. Nothing changes until you press Rename; Ctrl+Z in Revit undoes it.", size: 12.5);
        top.TextWrapping = TextWrapping.Wrap;
        top.Margin = new Thickness(0, 0, 0, 10);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ticks = new List<CheckBox>();
        var boxes = new List<TextBox>();
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tick = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
            AutomationProperties.SetName(tick, ok + " " + rows[i].Old);
            var old = Brand.Text(rows[i].Old, size: 12);
            old.TextTrimming = TextTrimming.CharacterEllipsis;
            old.ToolTip = rows[i].Old;
            old.VerticalAlignment = VerticalAlignment.Center;
            old.Margin = new Thickness(0, 3, 10, 3);
            var box = new TextBox { Text = rows[i].New, MinHeight = 26, Margin = new Thickness(0, 3, 0, 3) };
            AutomationProperties.SetName(box, (ok == "Rename" ? "New name for " : "Value for ") + rows[i].Old);
            tick.Checked += (_, _) => box.IsEnabled = true;
            tick.Unchecked += (_, _) => box.IsEnabled = false;
            Grid.SetRow(tick, i); Grid.SetRow(old, i); Grid.SetRow(box, i);
            Grid.SetColumn(old, 1); Grid.SetColumn(box, 2);
            grid.Children.Add(tick); grid.Children.Add(old); grid.Children.Add(box);
            ticks.Add(tick);
            boxes.Add(box);
        }
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        List<string?>? result = null;
        var okButton = RichUi.Button(RichUi.Icon.Next, ok, Brand.Weight.Primary);
        okButton.Click += (_, _) =>
        {
            result = ticks.Select((t, i) => t.IsChecked == true && boxes[i].Text.Trim().Length > 0 ? boxes[i].Text.Trim() : null).ToList();
            window.DialogResult = true;
        };
        var cancel = RichUi.Button(RichUi.Icon.Close, "Cancel");
        cancel.IsCancel = true;
        var buttons = Brand.ButtonRow(cancel, okButton);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        var layout = new DockPanel { Margin = new Thickness(18, 16, 18, 16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(buttons);
        layout.Children.Add(scroll);
        window.Content = layout;
        return window.ShowDialog() == true ? result : null;
    }
}
