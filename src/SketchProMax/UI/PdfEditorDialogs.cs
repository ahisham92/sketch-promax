using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SketchProMax.Tools;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>The small questions of the PDF editor: one short question each, the usual answer filled in.</summary>
internal abstract class PdfDialog : Window
{
    protected readonly StackPanel Page = new() { Margin = new Thickness(18, 14, 18, 14) };
    protected readonly TextBlock Problem = Brand.Text("", Brand.K.DangerText, 12);

    protected PdfDialog(Window owner, string title, string heading)
    {
        Title = "Sketch ProMax: " + title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Brand.UseTheme(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        var head = Brand.Text(heading, Brand.K.Text, 14, bold: true);
        head.TextWrapping = TextWrapping.Wrap;
        Page.Children.Add(head);
        Problem.TextWrapping = TextWrapping.Wrap;
        Problem.Visibility = Visibility.Collapsed;
        Content = Page;
    }

    protected TextBlock Line(string text, string key = Brand.K.Muted)
    {
        var t = Brand.Text(text, key, 12);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 6, 0, 0);
        Page.Children.Add(t);
        return t;
    }

    /// <summary>Adds the problem line and Cancel / <paramref name="ok"/> at the foot.</summary>
    protected void Buttons(string ok, Action accept)
    {
        Problem.Margin = new Thickness(0, 8, 0, 0);
        Page.Children.Add(Problem);
        var cancel = Brand.Button("Cancel");
        cancel.IsCancel = true;
        var yes = Brand.Button(ok, Brand.Weight.Primary);
        yes.IsDefault = true;
        yes.Click += (_, _) => accept();
        Page.Children.Add(Brand.ButtonRow(cancel, yes));
    }

    protected void Say(string problem)
    {
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
    }
}

/// <summary>Asks for the password of a locked PDF. The saved copy has none.</summary>
internal sealed class PasswordWindow : PdfDialog
{
    private readonly PasswordBox _box = new() { MinHeight = 28, Margin = new Thickness(0, 8, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private string? _answer;

    private PasswordWindow(Window owner, string file, string said) : base(owner, "password", $"Password for {file}")
    {
        Line(said.Contains("didn't open") ? said : "It is locked. Type its password to open it. What you save has no password.");
        AutomationProperties.SetName(_box, "Password");
        Page.Children.Add(_box);
        Buttons("Open", () => { _answer = _box.Password; DialogResult = true; });
        Loaded += (_, _) => _box.Focus();
    }

    public static string? Ask(Window owner, string file, string said)
    {
        var w = new PasswordWindow(owner, file, said);
        return w.ShowDialog() == true ? w._answer : null;
    }
}

/// <summary>Which pages of a PDF to use (Replace, when it has more pages than are picked).</summary>
internal sealed class PagesWindow : PdfDialog
{
    private readonly TextBox _pages = new() { MinHeight = 28, Margin = new Thickness(0, 8, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private List<int>? _answer;

    private PagesWindow(Window owner, string file, int count, string guess, int picked) : base(owner, "which pages", $"Which pages of {file}?")
    {
        Line($"It has {count} pages; {picked} {(picked == 1 ? "page is" : "pages are")} picked. Type page numbers such as 1-3, 5 or all.");
        _pages.Text = guess;
        AutomationProperties.SetName(_pages, "Pages to use");
        Page.Children.Add(_pages);
        Buttons("Replace", () =>
        {
            _answer = PdfEditSession.ParseRange(_pages.Text, count);
            if (_answer is null) { Say($"Type page numbers from 1 to {count}, such as 1-3, 5 or all."); _pages.Focus(); return; }
            DialogResult = true;
        });
        Loaded += (_, _) => { _pages.Focus(); _pages.SelectAll(); };
    }

    public static List<int>? Ask(Window owner, string file, int count, string guess, int picked)
    {
        var w = new PagesWindow(owner, file, count, guess, picked);
        return w.ShowDialog() == true ? w._answer : null;
    }
}

/// <summary>How to split: every N pages, at the picked pages, or by size; and the folder for the parts.</summary>
internal sealed class SplitChoiceWindow : PdfDialog
{
    public sealed record Choice(PdfEditSession.SplitBy By, int Every, double Mb, string Folder);

    private readonly RadioButton _every = new() { Margin = new Thickness(0, 10, 0, 0), GroupName = "split" };
    private readonly RadioButton _picked = new() { Margin = new Thickness(0, 8, 0, 0), GroupName = "split" };
    private readonly RadioButton _size = new() { Margin = new Thickness(0, 8, 0, 0), GroupName = "split" };
    private readonly TextBox _n = new() { Width = 56, MinHeight = 26, Text = "1", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    private readonly TextBox _mb = new() { Width = 56, MinHeight = 26, Text = SplitPdfWindow.DefaultMb.ToString(), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    private readonly TextBlock _where = Brand.Text("", Brand.K.Muted, 12);
    private string _folder;
    private Choice? _answer;

    private SplitChoiceWindow(Window owner, int pages, IReadOnlyList<int> picked, string folder) : base(owner, "split", $"Split the {pages} pages into several PDFs")
    {
        _folder = folder;
        _every.Content = Row("Every", _n, "pages (1: each page its own PDF)");
        _picked.Content = new TextBlock
        {
            Text = picked.Any(p => p > 0) ? $"At the picked pages: each starts a new PDF (pages {PdfEditSession.Describe(picked)})"
                : "At the picked pages (pick the first page of each part first)",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 430,
        };
        _picked.IsEnabled = picked.Any(p => p > 0);
        _size.Content = Row("Parts up to", _mb, "MB each, to email or attach");
        foreach (var r in new[] { _every, _picked, _size }) { Brand.Fg(r, Brand.K.Text); Page.Children.Add(r); }
        (_picked.IsEnabled ? _picked : _every).IsChecked = true;
        _n.GotFocus += (_, _) => _every.IsChecked = true;
        _mb.GotFocus += (_, _) => _size.IsChecked = true;
        AutomationProperties.SetName(_n, "Pages in each part");
        AutomationProperties.SetName(_mb, "Largest part in MB");

        var where = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var change = Brand.Button("Change…");
        change.Click += (_, _) => ChangeFolder();
        DockPanel.SetDock(change, Dock.Right);
        where.Children.Add(change);
        _where.TextWrapping = TextWrapping.Wrap;
        _where.VerticalAlignment = VerticalAlignment.Center;
        _where.Text = "Saved in " + _folder;
        where.Children.Add(_where);
        Page.Children.Add(where);
        Line("This document doesn't change. A part never replaces a file already there.");
        Buttons("Split", Accept);
    }

    private static StackPanel Row(string before, TextBox box, string after)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = before, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = after, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void ChangeFolder()
    {
        var start = Directory.Exists(_folder) ? _folder : Path.GetDirectoryName(_folder) ?? "";
#if NET8_0_OR_GREATER
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for the parts", InitialDirectory = start };
        if (dialog.ShowDialog(this) != true) return;
        _folder = dialog.FolderName;
#else
        // No folder picker in WPF on .NET Framework: open the folder wanted, then press Save.
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Folder for the parts (open it, then press Save)",
            FileName = "Parts go here",
            InitialDirectory = start,
            Filter = "Folder|*.folder",
            OverwritePrompt = false,
            CheckPathExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        _folder = Path.GetDirectoryName(dialog.FileName) ?? _folder;
#endif
        _where.Text = "Saved in " + _folder;
    }

    private void Accept()
    {
        if (_every.IsChecked == true)
        {
            if (!int.TryParse(_n.Text.Trim(), out var n) || n < 1) { Say("Type how many pages go in each part, such as 1 or 10."); _n.Focus(); return; }
            _answer = new Choice(PdfEditSession.SplitBy.EveryN, n, 0, _folder);
        }
        else if (_picked.IsChecked == true) _answer = new Choice(PdfEditSession.SplitBy.AtSelected, 0, 0, _folder);
        else
        {
            if (!double.TryParse(_mb.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var mb) &&
                !double.TryParse(_mb.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mb) || mb < 0.1 || mb > 4000)
            {
                Say("Type the largest part in MB, such as 10 or 18.");
                _mb.Focus();
                return;
            }
            _answer = new Choice(PdfEditSession.SplitBy.Size, 0, mb, _folder);
        }
        DialogResult = true;
    }

    public static Choice? Ask(Window owner, int pages, IReadOnlyList<int> picked, string folder)
    {
        var w = new SplitChoiceWindow(owner, pages, picked, folder);
        return w.ShowDialog() == true ? w._answer : null;
    }
}

/// <summary>One page, large: drawn at the window's size, with Previous and Next (arrow keys) and Esc to close.</summary>
internal sealed class PdfPreviewWindow : Window
{
    private readonly PdfEditSession _doc;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    private readonly TextBlock _where = Brand.Text("", Brand.K.Text, 13, bold: true);
    private readonly Button _prev, _next;
    private readonly DispatcherTimer _redraw;
    private int _at;
    private int _asked;

    public PdfPreviewWindow(Window owner, PdfEditSession doc, int at)
    {
        _doc = doc;
        _at = at;
        Owner = owner;
        Title = "Sketch ProMax: page";
        Width = Math.Min(1000, SystemParameters.WorkArea.Width * 0.8);
        Height = SystemParameters.WorkArea.Height * 0.88;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Brand.UseTheme(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));
        SetResourceReference(BackgroundProperty, Brand.K.Surface);
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(_image, "Page picture");

        _prev = RichUi.Button(RichUi.Icon.Back, "Previous");
        _prev.Click += (_, _) => Go(-1);
        _next = RichUi.Button(RichUi.Icon.Next, "Next");
        _next.Click += (_, _) => Go(1);
        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var bar = new DockPanel { Margin = new Thickness(14, 8, 14, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_prev);
        buttons.Children.Add(_next);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        _where.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_where, AutomationLiveSetting.Polite);
        bar.Children.Add(_where);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_image);
        Content = root;

        _redraw = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _redraw.Tick += (_, _) => { _redraw.Stop(); _ = Draw(); };
        SizeChanged += (_, _) => { _redraw.Stop(); _redraw.Start(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.PageUp or Key.Up) { Go(-1); e.Handled = true; }
            else if (e.Key is Key.Right or Key.PageDown or Key.Down or Key.Space) { Go(1); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Closed += (_, _) => _redraw.Stop();
        Loaded += (_, _) => { Show(_at); _next.Focus(); };
    }

    private void Go(int step)
    {
        var count = _doc.Pages.Count;
        if (count == 0) return;
        Show(Math.Max(0, Math.Min(count - 1, _at + step)));
    }

    private void Show(int at)
    {
        _at = at;
        var count = _doc.Pages.Count;
        _where.Text = $"Page {at + 1} of {count}";
        Title = $"Sketch ProMax: page {at + 1} of {count}";
        _prev.IsEnabled = at > 0;
        _next.IsEnabled = at < count - 1;
        _ = Draw();
    }

    private async Task Draw()
    {
        if (_at < 0 || _at >= _doc.Pages.Count) return;
        var page = _doc.Pages[_at];
        double dpi;
        try { dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { dpi = 1; }
        var w = (int)Math.Max(200, (_image.ActualWidth > 0 ? _image.ActualWidth : Width - 40) * dpi);
        var h = (int)Math.Max(200, (_image.ActualHeight > 0 ? _image.ActualHeight : Height - 120) * dpi);
        var ticket = ++_asked;
        try
        {
            var picture = await Task.Run(() => _doc.Draw(page, w, h));
            if (ticket != _asked) return;
            var bitmap = BitmapSource.Create(picture.Width, picture.Height, 96 * dpi, 96 * dpi, PixelFormats.Bgr32, null, picture.Pixels, picture.Stride);
            bitmap.Freeze();
            _image.Source = bitmap;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException or ArgumentException)
        {
            if (ticket == _asked) _where.Text = $"Page {_at + 1} couldn't be drawn: {ex.Message}";
        }
    }
}
