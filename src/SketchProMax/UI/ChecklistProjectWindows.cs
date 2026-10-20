using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SketchProMax.Tools;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// The small windows behind project checklists: the password for the Default checklist and a name. Modal; no Revit API.
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
}
