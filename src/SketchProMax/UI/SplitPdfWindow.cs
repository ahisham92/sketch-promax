using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Tools;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// Split PDF (the Split button on a large PDF attached in the chat, or the pane's Activity tab): cuts a large PDF into parts of at most a given size, in page order, saved in a folder next
/// to it, so each part can be attached in the chat whole or sent on. The split runs off Revit's thread and the window is
/// modeless, so Revit stays usable; nothing in the model is read or changed.
/// </summary>
internal sealed class SplitPdfWindow : Window
{
    /// <summary>The default part size: what the chat sends whole (PdfAttachment.MaxBytes).</summary>
    public const int DefaultMb = 18;

    private readonly TextBox _file = new() { MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _size = new() { MinHeight = 28, Width = 80, VerticalContentAlignment = VerticalAlignment.Center, Text = DefaultMb.ToString() };
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted, 12);
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _split, _folder, _close;
    private CancellationTokenSource? _running;
    private string? _output;

    public static Autodesk.Revit.UI.Result Open(string? file = null)
    {
        new SplitPdfWindow(file).Show();
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    /// <summary>The window, not shown (Test before sharing and the field test build it).</summary>
    internal static Window ForTest() => new SplitPdfWindow(null);

    private SplitPdfWindow(string? file)
    {
        Title = "Sketch ProMax: Split PDF";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));

        var page = new StackPanel { Margin = new Thickness(18, 12, 18, 14) };
        page.Children.Add(Para("Cuts a large PDF into parts in page order, each at most the size you set, so a part can be attached in the chat " +
                               $"and read whole (the chat takes up to {DefaultMb} MB and 100 pages of PDFs). The original file is not changed.", Brand.K.Muted));

        page.Children.Add(Label("PDF to split"));
        var browse = RichUi.Button(RichUi.Icon.Open, "Browse…");
        browse.Click += (_, _) => Browse();
        var fileRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        fileRow.Children.Add(browse);
        fileRow.Children.Add(_file);
        _file.Text = file ?? "";
        AutomationProperties.SetName(_file, "PDF to split");
        page.Children.Add(fileRow);

        page.Children.Add(Label("Largest part (MB)"));
        AutomationProperties.SetName(_size, "Largest part in MB");
        AutomationProperties.SetHelpText(_size, $"Each part is at most this size. {DefaultMb} MB parts can be attached in the chat whole.");
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        sizeRow.Children.Add(_size);
        sizeRow.Children.Add(new TextBlock { Text = "  A part also keeps to 100 pages.", VerticalAlignment = VerticalAlignment.Center });
        Brand.Fg((TextBlock)sizeRow.Children[1], Brand.K.Muted);
        page.Children.Add(sizeRow);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        page.Children.Add(_status);
        page.Children.Add(_bar);

        _folder = RichUi.Button(RichUi.Icon.Folder, "Open folder");
        _folder.Visibility = Visibility.Collapsed;
        _folder.Click += (_, _) => OpenFolder();
        _close = RichUi.Button(RichUi.Icon.Close, "Close");
        _close.IsCancel = true;
        _close.Click += (_, _) => Close();
        _split = RichUi.Button(RichUi.Icon.Pdf, "Split", Brand.Weight.Primary);
        _split.IsDefault = true;
        _split.Click += async (_, _) => await Split();
        page.Children.Add(Brand.ButtonRow(_folder, _close, _split));

        var root = new DockPanel();
        var header = Brand.Header("Split PDF", "Parts small enough to attach in the chat");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(page);
        Content = root;
        Loaded += (_, _) => (_file.Text.Length == 0 ? (UIElement)browse : _split).Focus();
        Closing += (_, _) => _running?.Cancel();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "PDF to split", Filter = "PDF files|*.pdf", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _file.Text = dialog.FileName;
    }

    private async Task Split()
    {
        if (_running is not null) { _running.Cancel(); return; } // the button reads Stop while it runs
        var file = _file.Text.Trim().Trim('"');
        if (file.Length == 0 || !File.Exists(file)) { Error("Pick the PDF to split (Browse…)."); _file.Focus(); return; }
        if (!double.TryParse(_size.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var mb) &&
            !double.TryParse(_size.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mb) || mb < 1 || mb > 2000)
        {
            Error("Largest part is a number of MB from 1 to 2000, such as 18.");
            _size.Focus();
            return;
        }
        var folder = OutputFolder(file);
        _running = new CancellationTokenSource();
        var ct = _running.Token;
        _split.Content = "Stop";
        _split.SetResourceReference(StyleProperty, "SpmDangerButton");
        _folder.Visibility = Visibility.Collapsed;
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        Info($"Splitting {Path.GetFileName(file)}…");
        try
        {
            var parts = await Task.Run(() => PdfFiles.Split(file, (long)(mb * 1024 * 1024), folder, (done, total) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _bar.Value = done / (double)total;
                    Info($"Splitting {Path.GetFileName(file)}: page {done} of {total}…");
                })), ct, maxPages: 100), ct);
            _output = folder;
            var big = parts.Where(p => p.Bytes > mb * 1024 * 1024).ToList();
            Info($"Done: {parts.Count} part{(parts.Count == 1 ? "" : "s")} in {folder}.\n" +
                 string.Join("\n", parts.Select(p => $"{Path.GetFileName(p.File)}: {p.Bytes / (1024.0 * 1024):0.#} MB")) +
                 (big.Count > 0 ? $"\n\n{big.Count} page{(big.Count == 1 ? " is" : "s are")} larger than {mb:0.#} MB on {(big.Count == 1 ? "its" : "their")} own, so " +
                                  (big.Count == 1 ? "that part is" : "those parts are") + " larger. The chat reads such a part page by page." : ""));
            _folder.Visibility = Visibility.Visible;
            _folder.Focus();
        }
        catch (OperationCanceledException) { Info("Stopped. Parts already written are in " + folder + "."); _output = folder; _folder.Visibility = Visibility.Visible; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error($"The PDF couldn't be split ({ex.Message}). If it is damaged or has a password, save a fresh copy from a PDF viewer and try that.");
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _bar.Visibility = Visibility.Collapsed;
            _split.Content = "Split";
            _split.SetResourceReference(StyleProperty, "SpmPrimaryButton");
        }
    }

    /// <summary>"name parts" next to the PDF, or under Documents\Sketch ProMax\Split PDFs when that folder can't be written.</summary>
    internal static string OutputFolder(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file) + " parts";
        var next = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)) ?? "", name);
        try
        {
            Directory.CreateDirectory(next);
            var probe = Path.Combine(next, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return next;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Split PDFs", name);
        }
    }

    private void OpenFolder()
    {
        if (_output is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_output}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { Error($"Couldn't open {_output}: {ex.Message}"); }
    }

    private void Info(string text)
    {
        _status.Text = text;
        Brand.Fg(_status, Brand.K.Text);
    }

    private void Error(string text)
    {
        _status.Text = text;
        Brand.Fg(_status, Brand.K.DangerText);
    }

    private static TextBlock Label(string text)
    {
        var t = Brand.Text(text, Brand.K.Text, 13, bold: true);
        t.Margin = new Thickness(0, 14, 0, 0);
        return t;
    }

    private static TextBlock Para(string text, string key)
    {
        var t = Brand.Text(text, key, 12);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }
}
