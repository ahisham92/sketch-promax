using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SketchProMax.Tools;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// The small windows behind project checklists: the editing password, a name, and picking checks for a new checklist
/// (a ports checklist made from the office one, say). Modal; no Revit API.
/// </summary>
internal static class ChecklistProjectWindows
{
    /// <summary>Asks for the editing password; true when it is right. Wrong answers can be tried again.</summary>
    public static bool Unlock(Window owner, string what)
    {
        while (true)
        {
            var entered = Ask(owner, "Password for changing checks", $"Changing checks is protected. Type the password to {what}.", "", password: true);
            if (entered is null) return false;
            if (ChecklistProjects.PasswordIs(entered)) return true;
            MessageBox.Show(owner, "That isn't the password. Ask whoever looks after the checklist for it.", "Sketch ProMax", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Asks the current password (unless already given), then the new one twice, and saves it.</summary>
    public static void ChangePassword(Window owner, bool unlocked)
    {
        if (!unlocked && !Unlock(owner, "change it")) return;
        var first = Ask(owner, "New password", "Type the new password for changing checks (at least 4 characters).", "", password: true);
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
    /// Picks checks from <paramref name="from"/> for a new project checklist: its name and the checks ticked, or null
    /// when cancelled. Every check starts ticked; a stage's box ticks or clears all its checks.
    /// </summary>
    public static (string Name, ChecklistMath.Definition Def)? Pick(Window owner, ChecklistMath.Definition from, string project)
    {
        var window = new Window
        {
            Title = "Sketch ProMax: new checklist from picked checks", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var area = SystemParameters.WorkArea;
        window.Width = Math.Min(720, area.Width * 0.9);
        window.Height = Math.Min(760, area.Height * 0.9);
        window.MinWidth = 480;
        window.MinHeight = 400;
        Brand.Prepare(window);

        var name = new TextBox { MinHeight = 28, Text = project.Length > 0 ? project + " checklist" : "", Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(name, "Name of the new checklist");
        var top = new StackPanel { Margin = new Thickness(18, 12, 18, 8) };
        top.Children.Add(Brand.Text("Name of the new checklist (for example Ports)", size: 12.5, bold: true));
        top.Children.Add(name);
        var hint = Brand.Text("Tick the checks it keeps. A stage's box ticks or clears all its checks. The office checklist isn't changed; the new one is kept on this PC and used for this project.", Brand.K.Muted, 12);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 8, 0, 0);
        top.Children.Add(hint);

        var boxes = new List<(CheckBox Box, string Id)>();
        var list = new StackPanel { Margin = new Thickness(18, 0, 18, 8) };
        var count = Brand.Text("", Brand.K.Muted, 12);
        void Count() => count.Text = $"{boxes.Count(b => b.Box.IsChecked == true)} of {boxes.Count} checks ticked";
        foreach (var section in from.Sections)
        {
            var stage = new CheckBox { IsChecked = true, Margin = new Thickness(0, 12, 0, 2), Content = Brand.Text(section.Title, size: 13.5, bold: true) };
            AutomationProperties.SetName(stage, "Stage " + section.Title);
            list.Children.Add(stage);
            var mine = new List<CheckBox>();
            foreach (var item in section.Items)
            {
                var label = Brand.Text($"#{item.Number}  {item.Title}", size: 12.5);
                label.TextWrapping = TextWrapping.Wrap;
                var box = new CheckBox { IsChecked = true, Margin = new Thickness(24, 4, 0, 0), Content = label, ToolTip = item.Check };
                AutomationProperties.SetName(box, $"Check {item.Number}: {item.Title}");
                box.Checked += (_, _) => Count();
                box.Unchecked += (_, _) => Count();
                mine.Add(box);
                boxes.Add((box, item.Id));
                list.Children.Add(box);
            }
            var setting = false;
            stage.Checked += (_, _) => { if (setting) return; setting = true; foreach (var b in mine) b.IsChecked = true; setting = false; };
            stage.Unchecked += (_, _) => { if (setting) return; setting = true; foreach (var b in mine) b.IsChecked = false; setting = false; };
        }
        Count();
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var error = Brand.Text("", Brand.K.WarningText, 12);
        error.TextWrapping = TextWrapping.Wrap;
        error.Visibility = Visibility.Collapsed;
        (string, ChecklistMath.Definition)? result = null;
        var all = RichUi.Button(RichUi.Icon.Add, "Tick all");
        all.Click += (_, _) => { foreach (var b in boxes) b.Box.IsChecked = true; };
        var none = RichUi.Button(RichUi.Icon.Close, "Clear all");
        none.Click += (_, _) => { foreach (var b in boxes) b.Box.IsChecked = false; };
        var create = RichUi.Button(RichUi.Icon.Save, "Create the checklist", Brand.Weight.Primary);
        create.Click += (_, _) =>
        {
            var clean = ChecklistProjects.CleanName(name.Text);
            var picked = new HashSet<string>(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id), StringComparer.Ordinal);
            string? why = clean.Length == 0 ? "Give the new checklist a name, for example Ports."
                : picked.Count == 0 ? "Tick at least one check."
                : null;
            if (why is null && ChecklistProjects.Exists(clean)
                && MessageBox.Show(window, $"There is already a checklist called {clean} on this PC. Replace it?", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            if (why is not null) { error.Text = why; error.Visibility = Visibility.Visible; return; }
            result = (clean, ChecklistProjects.Pick(from, clean, picked));
            window.DialogResult = true;
        };
        var cancel = RichUi.Button(RichUi.Icon.Close, "Cancel");
        cancel.IsCancel = true;
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { all, none, count } };
        all.Margin = new Thickness(0, 0, 8, 0);
        none.Margin = new Thickness(0, 0, 12, 0);
        count.VerticalAlignment = VerticalAlignment.Center;
        var right = Brand.ButtonRow(cancel, create);
        right.Margin = new Thickness(0);
        var footGrid = new Grid();
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footGrid.Children.Add(left);
        Grid.SetColumn(right, 1);
        footGrid.Children.Add(right);
        var foot = new StackPanel { Margin = new Thickness(18, 8, 18, 14), Children = { error, footGrid } };

        var header = Brand.Header("New checklist from picked checks", from.Title);
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(top);
        root.Children.Add(foot);
        root.Children.Add(scroll);
        window.Content = root;
        window.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        return window.ShowDialog() == true ? result : null;
    }
}
