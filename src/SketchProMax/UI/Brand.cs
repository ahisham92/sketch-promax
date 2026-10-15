using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SketchProMax.UI;

/// <summary>The product look for Sketch ProMax windows: logo, palette and a few shared building blocks.</summary>
internal static class Brand
{
    public static readonly Color Blue = Color.FromRgb(0x1D, 0x4E, 0xD8);
    public static readonly Color Navy = Color.FromRgb(0x0B, 0x1E, 0x4F);
    public static readonly Color Amber = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color Green = Color.FromRgb(0x16, 0xA3, 0x4A);
    public static readonly Color Red = Color.FromRgb(0xDC, 0x26, 0x26);
    public static readonly Color Muted = Color.FromRgb(0x64, 0x74, 0x8B);

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static BitmapSource? _logo;
    private static BitmapFrame? _icon;

    /// <summary>The 64 px logo, embedded in the core.</summary>
    public static BitmapSource? Logo => _logo ??= Load("SketchProMax.logo-64.png");

    /// <summary>The window icon (.ico).</summary>
    public static BitmapFrame? Icon
    {
        get
        {
            if (_icon is not null) return _icon;
            try
            {
                using var stream = typeof(Brand).Assembly.GetManifestResourceStream("SketchProMax.sketch-promax.ico");
                if (stream is null) return null;
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                _icon = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException) { }
            return _icon;
        }
    }

    private static BitmapSource? Load(string name)
    {
        try
        {
            using var stream = typeof(Brand).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Applies the window icon and makes Revit the owner, so the window stays with Revit and never hides behind it.</summary>
    public static void Prepare(Window window)
    {
        if (Icon is { } icon) window.Icon = icon;
        window.ShowInTaskbar = false;
        var main = App.MainWindow;
        if (main != IntPtr.Zero)
        {
            try { new WindowInteropHelper(window).Owner = main; }
            catch (Exception) { /* no owner: still works */ }
        }
    }

    /// <summary>The blue header band used at the top of Sketch ProMax windows.</summary>
    public static Border Header(string title, string? subtitle)
    {
        var gradient = new LinearGradientBrush(Blue, Navy, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        var row = new DockPanel { LastChildFill = true };
        if (Logo is { } logo)
        {
            var image = new Image { Source = logo, Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(image, Dock.Left);
            row.Children.Add(image);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (subtitle is not null)
            text.Children.Add(new TextBlock { Text = subtitle, Foreground = Brush(Color.FromRgb(0xC7, 0xD2, 0xFE)), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        return new Border { Background = gradient, Padding = new Thickness(16, 12, 16, 12), Child = row };
    }

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Content = text, MinWidth = 88, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0),
        };
        if (primary)
        {
            button.Background = Brush(Blue);
            button.Foreground = Brushes.White;
            button.BorderBrush = Brush(Navy);
        }
        return button;
    }
}
