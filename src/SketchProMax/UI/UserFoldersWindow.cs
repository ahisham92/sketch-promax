using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Bugs;
using Button = System.Windows.Controls.Button;

namespace SketchProMax.UI;

/// <summary>
/// Help > Open folders: each folder Sketch ProMax keeps things in (bugs, learned tools, recipes, chats...), with Open and
/// Copy path. Nothing is sent anywhere; to learn from a colleague's PC, copy the folders from there.
/// </summary>
internal sealed class UserFoldersWindow : Window
{
    private readonly TextBlock _result = Brand.Text("", Brand.K.Muted, 12);
    private static UserFoldersWindow? _open;

    public static Autodesk.Revit.UI.Result Open()
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return Autodesk.Revit.UI.Result.Succeeded;
        }
        _open = new UserFoldersWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    /// <summary>The window, not shown (the field test builds it).</summary>
    internal static Window ForTest() => new UserFoldersWindow();

    private UserFoldersWindow()
    {
        Title = "Sketch ProMax: Folders";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));

        var page = new StackPanel { Margin = new Thickness(18, 8, 18, 14) };
        foreach (var folder in UserFolders.List()) page.Children.Add(Row(folder));

        _result.TextWrapping = TextWrapping.Wrap;
        _result.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);
        page.Children.Add(_result);

        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        page.Children.Add(Brand.ButtonRow(close));

        var root = new DockPanel();
        var header = Brand.Header("Folders", "Where Sketch ProMax keeps your bugs, tools and chats");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(page);
        Content = root;
    }

    private UIElement Row(UserFolders.Folder folder)
    {
        var open = RichUi.Button(RichUi.Icon.Folder, "Open");
        AutomationProperties.SetName(open, "Open " + folder.Title);
        open.Click += (_, _) => OpenFolder(folder);
        var copy = RichUi.Button(RichUi.Icon.Export, "Copy path");
        AutomationProperties.SetName(copy, "Copy the path of " + folder.Title);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(folder.Path); Say($"Copied: {folder.Path}", Brand.K.SuccessText); }
            catch (System.Runtime.InteropServices.ExternalException) { Say("The clipboard is busy. Try Copy path again.", Brand.K.DangerText); }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(copy);
        copy.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(open);

        var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        text.Children.Add(Brand.Text(folder.Title, Brand.K.Text, 13, bold: true));
        var path = Brand.Text(folder.Path, Brand.K.Muted, 11);
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        path.ToolTip = folder.Tip;
        text.Children.Add(path);
        text.ToolTip = folder.Tip;

        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    private void OpenFolder(UserFolders.Folder folder)
    {
        try
        {
            Directory.CreateDirectory(folder.Path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.Path}\"") { UseShellExecute = true })?.Dispose();
            Say("", Brand.K.Muted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say($"Couldn't open {folder.Path}: {ex.Message}", Brand.K.DangerText);
        }
    }

    private void Say(string text, string key)
    {
        _result.Text = text;
        Brand.Fg(_result, key);
    }
}
