using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// Files a step made (make_document, and exports such as a bill of quantities): one card per step under the step, with
/// each file's name, kind, size and folder, and Open, Show in folder and Save a copy. Saved with the chat, so the card is
/// there when the chat is opened again; a file moved or deleted since says so.
/// </summary>
internal sealed partial class ChatPane
{
    /// <summary>The card item for a finished step's files, or null when its result names none.</summary>
    internal static ChatItem? FilesItem(string tool, string result)
    {
        var files = ChatFiles.Find(result);
        return files.Count == 0 ? null : new ChatItem { Kind = "file", Name = tool, Files = files };
    }

    private UIElement FileCard(ChatItem item)
    {
        var list = new StackPanel();
        foreach (var path in item.Files) list.Children.Add(FileRow(path, list.Children.Count > 0));
        var card = ChatUi.Elevated(new Border { Child = list, Padding = new Thickness(ChatUi.S4, ChatUi.S3, ChatUi.S4, ChatUi.S3) }, out _);
        card.Margin = new Thickness(0, ChatUi.S2, 0, ChatUi.S2);
        AutomationProperties.SetName(card, item.Files.Count == 1 ? "File made: " + Path.GetFileName(item.Files[0]) : $"{item.Files.Count} files made");
        return card;
    }

    private UIElement FileRow(string path, bool separated)
    {
        var name = Path.GetFileName(path);
        var row = new StackPanel { Margin = new Thickness(0, separated ? ChatUi.S3 : 0, 0, 0) };
        var head = new DockPanel();
        var icon = ChatUi.Glyph(ChatUi.GlyphFile, Brand.K.Accent, 18);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, ChatUi.S3, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var text = new StackPanel();
        var title = ChatUi.Small(name, Brand.K.Text, bold: true);
        title.TextWrapping = TextWrapping.Wrap;
        title.ToolTip = path;
        text.Children.Add(title);
        var exists = File.Exists(path);
        var folder = Path.GetDirectoryName(path) ?? "";
        string about;
        try { about = exists ? $"{ChatFiles.Kind(path)} · {ChatFiles.Size(new FileInfo(path).Length)} · in {Path.GetFileName(folder)}" : "Moved or deleted since it was made"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { about = ChatFiles.Kind(path); }
        var detail = ChatUi.Small(about, exists ? Brand.K.Muted : Brand.K.WarningText);
        detail.TextWrapping = TextWrapping.Wrap;
        detail.ToolTip = path;
        text.Children.Add(detail);
        head.Children.Add(text);
        row.Children.Add(head);
        if (!exists)
        {
            AutomationProperties.SetName(row, name + ", moved or deleted since it was made");
            return row;
        }

        var status = ChatUi.Small("");
        status.Visibility = Visibility.Collapsed;
        status.TextWrapping = TextWrapping.Wrap;
        Button Pill(string label, bool primary, string help, Action click)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, ChatUi.S2, 0), ToolTip = help };
            b.SetResourceReference(StyleProperty, primary ? "SpmChatPrimary" : "SpmChatPill");
            AutomationProperties.SetName(b, label + " " + name);
            AutomationProperties.SetHelpText(b, help);
            b.Click += (_, _) =>
            {
                try { click(); }
                catch (Exception ex) // never into Revit's dispatcher
                {
                    status.Text = "That didn't work: " + ex.Message;
                    status.Visibility = Visibility.Visible;
                }
            };
            return b;
        }
        var buttons = new WrapPanel { Margin = new Thickness(ChatUi.S3 + 18, ChatUi.S2, 0, 0) };
        buttons.Children.Add(Pill("Open", true, "Open it in its app (Excel, Word, PowerPoint or your PDF reader)", () => ChatUi.Open(path)));
        buttons.Children.Add(Pill("Show in folder", false, "Open its folder with the file selected: " + folder, () =>
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false })?.Dispose()));
        buttons.Children.Add(Pill("Save a copy…", false, "Save a copy somewhere else, for example on the project drive", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = name, Filter = ChatFiles.Filter(path), DefaultExt = Path.GetExtension(path), AddExtension = true, OverwritePrompt = true,
                Title = "Save a copy of " + name,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
            File.Copy(path, dialog.FileName, overwrite: true);
            status.Text = "Saved a copy to " + dialog.FileName;
            status.Visibility = Visibility.Visible;
        }));
        row.Children.Add(buttons);
        status.Margin = new Thickness(ChatUi.S3 + 18, ChatUi.S1, 0, 0);
        row.Children.Add(status);
        AutomationProperties.SetName(row, $"{name}, {about}");
        return row;
    }
}
