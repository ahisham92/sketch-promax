using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// Small building blocks of the chat view: quiet icon buttons (copy, show earlier), the tool-step disclosure toggle,
/// rounded cards with a soft shadow, vector icons, picture thumbnails and the typing dots. Colours come from the Brand
/// tokens only, so both Revit themes work. Spacing follows a 4 px scale (<see cref="S1"/>..<see cref="S6"/>); corners
/// are <see cref="RadiusSmall"/> on buttons and fields and <see cref="Radius"/> on cards and the composer.
/// </summary>
internal static class ChatUi
{
    public const double S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20, S6 = 24;
    public const double BodySize = 13, SmallSize = 11.5, CodeSize = 12, TitleSize = 14, GreetingSize = 22;
    public const double RadiusSmall = 8, Radius = 12;

    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>Windows 11's text face where it is installed, Segoe UI elsewhere (WPF falls back along the list).</summary>
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");

    // Segoe MDL2 / Fluent glyphs.
    public const string GlyphCopy = "\uE8C8", GlyphCheck = "\uE73E", GlyphCross = "\uE711", GlyphStop = "\uE71A";
    public const string GlyphRight = "\uE76C", GlyphDown = "\uE70D", GlyphArrowDown = "\uE74B", GlyphError = "\uE783";
    public const string GlyphInfo = "\uE946", GlyphPicture = "\uEB9F", GlyphUp = "\uE70E", GlyphFile = "\uE8A5";
    public const string GlyphMenu = "\uE700", GlyphAdd = "\uE710", GlyphRefresh = "\uE72C", GlyphMore = "\uE712";
    public const string GlyphRename = "\uE8AC", GlyphPin = "\uE718", GlyphSave = "\uE74E", GlyphDelete = "\uE74D", GlyphPrompts = "\uE8A4";

    /// <summary>Vector icons on a 16 px grid, drawn with a 1.5 px round stroke in the text colour of whatever holds them.</summary>
    public static class Paths
    {
        public const string Plus = "M8,3 L8,13 M3,8 L13,8";
        public const string Menu = "M2.5,4.5 L13.5,4.5 M2.5,8 L13.5,8 M2.5,11.5 L13.5,11.5";
        public const string Refresh = "M13,8 A5,5 0 1 1 11.54,4.46 M11.5,1.8 L11.5,4.5 L8.8,4.5";
        public const string Send = "M8,13 L8,3.5 M4,7.5 L8,3.5 L12,7.5";
        public const string Back = "M13,8 L3.5,8 M7.5,4 L3.5,8 L7.5,12";
        public const string Bookmark = "M4.5,2.5 L11.5,2.5 L11.5,13.5 L8,10.5 L4.5,13.5 Z";
        public const string Camera = "M2.5,5.5 L5,5.5 L6.2,3.8 L9.8,3.8 L11,5.5 L13.5,5.5 L13.5,12.5 L2.5,12.5 Z M10.2,9 A2.2,2.2 0 1 1 5.8,9 A2.2,2.2 0 1 1 10.2,9";
        public const string Grid = "M2.5,2.5 L6.5,2.5 L6.5,6.5 L2.5,6.5 Z M9.5,2.5 L13.5,2.5 L13.5,6.5 L9.5,6.5 Z M2.5,9.5 L6.5,9.5 L6.5,13.5 L2.5,13.5 Z M9.5,9.5 L13.5,9.5 L13.5,13.5 L9.5,13.5 Z";
        public const string Sparkle = "M8,2 L9.3,6.7 L14,8 L9.3,9.3 L8,14 L6.7,9.3 L2,8 L6.7,6.7 Z";
        public const string ChevronUp = "M4.5,10 L8,6.5 L11.5,10";
        public const string ChevronRight = "M6,3.5 L10.5,8 L6,12.5";
        public const string ArrowDown = "M8,3 L8,12.5 M4,8.5 L8,12.5 L12,8.5";
        public const string Square = "M5,5 L11,5 L11,11 L5,11 Z";
    }

    private static ResourceDictionary? _styles;

    /// <summary>The chat's own styles (<see cref="ChatStyles"/>), merged into the pane next to the Brand styles.</summary>
    public static ResourceDictionary Styles()
    {
        if (_styles is not null) return _styles;
        try { _styles = (ResourceDictionary)XamlReader.Parse(ChatStyles.Xaml(RadiusSmall, Radius)); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("Sketch ProMax chat styles: " + ex.Message);
            _styles = new ResourceDictionary(); // the plain look, never a pane that doesn't open
        }
        return _styles;
    }

    /// <summary>A crisp vector icon (see <see cref="Paths"/>), stroked or filled in the inherited text colour.</summary>
    public static System.Windows.Shapes.Path Icon(string data, double size = 16, bool fill = false, double stroke = 1.5)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry(data), Width = size, Height = size, Stretch = size == 16 ? Stretch.None : Stretch.Uniform,
            StrokeThickness = fill ? 0 : stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, SnapsToDevicePixels = true, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false, Focusable = false,
        };
        var binding = new System.Windows.Data.Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = System.Windows.Data.RelativeSource.Self };
        path.SetBinding(fill ? Shape.FillProperty : Shape.StrokeProperty, binding);
        return path;
    }

    private static readonly Dictionary<string, Geometry> Geometries = new();

    private static Geometry Geometry(string data)
    {
        if (Geometries.TryGetValue(data, out var g)) return g;
        g = System.Windows.Media.Geometry.Parse(data);
        g.Freeze();
        Geometries[data] = g;
        return g;
    }

    /// <summary>An icon-only 32 px button: muted until hovered, with a rounded focus ring and a name for screen readers.</summary>
    public static Button IconButton(string path, string name, string tip)
    {
        var button = new Button { Content = Icon(path), ToolTip = tip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "SpmChatIcon");
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, tip);
        return button;
    }

    /// <summary>
    /// A rounded card on the raised surface with a hairline border and a soft shadow. The shadow sits on a separate layer
    /// behind <paramref name="frame"/>, so the text inside keeps ClearType instead of being drawn through an effect.
    /// </summary>
    public static Grid Elevated(UIElement content, out Border frame, double radius = Radius)
    {
        var shadow = new Border { CornerRadius = new CornerRadius(radius), Margin = new Thickness(1) };
        shadow.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        shadow.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.09, Color = Colors.Black };
        frame = new Border { Child = content, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
        frame.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        frame.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        var grid = new Grid();
        grid.Children.Add(shadow);
        grid.Children.Add(frame);
        return grid;
    }

    /// <summary>
    /// The chat's own mark: a rounded tile in the brand blues with a white isometric block, drawn as vectors so it is
    /// sharp at any scale (the bitmap logo blurs at small sizes).
    /// </summary>
    public static FrameworkElement Mark(double size = 28)
    {
        var tile = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8),
            Background = Frozen(new LinearGradientBrush(Brand.Blue, Brand.Navy, new Point(0, 0), new Point(1, 1))),
        };
        var block = new System.Windows.Shapes.Path
        {
            Data = Geometry("M14,6.5 L20.5,10 L20.5,17.5 L14,21 L7.5,17.5 L7.5,10 Z M7.5,10 L14,13.5 L20.5,10 M14,13.5 L14,21"),
            Stroke = Brushes.White, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None,
        };
        var spark = new System.Windows.Shapes.Path
        {
            Data = Geometry("M22,3 L22.7,5.3 L25,6 L22.7,6.7 L22,9 L21.3,6.7 L19,6 L21.3,5.3 Z"), Fill = Frozen(new SolidColorBrush(Color.FromRgb(0xFD, 0xE0, 0x47))),
            Stretch = Stretch.None,
        };
        var canvas = new Grid { Width = 28, Height = 28 };
        canvas.Children.Add(tile);
        canvas.Children.Add(block);
        canvas.Children.Add(spark);
        FrameworkElement mark = size == 28 ? canvas : new Viewbox { Child = canvas, Width = size, Height = size };
        AutomationProperties.SetName(mark, "Sketch ProMax");
        return mark;
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>An icon glyph in a palette colour.</summary>
    public static TextBlock Glyph(string glyph, string key, double size = 12)
    {
        var t = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        return Brand.Fg(t, key);
    }

    /// <summary>A quiet button: an icon and optional label, muted until hovered. At least 24 px, keyboard focusable.</summary>
    public static Button Ghost(string glyph, string? text, string name, string tip)
    {
        var button = new Button { Content = GhostContent(glyph, text), ToolTip = tip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "SpmChatGhost");
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, tip);
        return button;
    }

    private static object GhostContent(string glyph, string? text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        if (!string.IsNullOrEmpty(text))
            row.Children.Add(new TextBlock { Text = text, FontSize = SmallSize, Margin = new Thickness(S1 + 1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>A copy button: copies what <paramref name="text"/> returns and says "Copied" for a moment.</summary>
    public static Button CopyButton(Func<string> text, string what, string? label = "Copy")
    {
        var button = Ghost(GlyphCopy, label, "Copy " + what, "Copy " + what + " to the clipboard");
        button.Click += (_, _) => Copy(button, text(), label);
        return button;
    }

    private static void Copy(Button button, string text, string? label)
    {
        bool ok;
        try
        {
            Clipboard.SetText(text);
            ok = true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException) { ok = false; } // the clipboard is busy
        button.Content = GhostContent(ok ? GlyphCheck : GlyphError, label is null ? null : ok ? "Copied" : "Not copied");
        AutomationProperties.SetItemStatus(button, ok ? "Copied" : "The clipboard is busy; try again");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = GhostContent(GlyphCopy, label);
            AutomationProperties.SetItemStatus(button, "");
        };
        timer.Start();
    }

    /// <summary>Read-only text the reader can select and copy, without a box around it.</summary>
    public static TextBox Selectable(string text, string name)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(0), MinHeight = 0, FontSize = BodySize, IsTabStop = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        box.SetResourceReference(Control.ForegroundProperty, Brand.K.Text);
        AutomationProperties.SetName(box, name);
        return box;
    }

    /// <summary>
    /// A picture's thumbnail, decoded off the UI thread at twice its size; a picture glyph until (or unless) it loads.
    /// Clicking opens the file.
    /// </summary>
    public static Button Thumbnail(string path, double size)
    {
        var name = System.IO.Path.GetFileName(path);
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new TextBlock
        {
            Text = GlyphPicture, FontFamily = Icons, FontSize = Math.Min(20, size / 2), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var image = new Image { Stretch = Stretch.UniformToFill, Width = size, Height = size };
        grid.Children.Add(image);
        var frame = new Border { Child = grid, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true };
        frame.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        frame.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        var button = new Button { Content = frame, Padding = new Thickness(0), ToolTip = name + "\n(click to open)" };
        button.SetResourceReference(FrameworkElement.StyleProperty, "SpmChatGhost");
        AutomationProperties.SetName(button, "Picture " + name + ", open it");
        button.Click += (_, _) => Open(path);
        var pixels = (int)Math.Ceiling(size * 2);
        Task.Run(() => LoadThumbnail(path, pixels)).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } bitmap)
                image.Dispatcher.BeginInvoke(new Action(() => image.Source = bitmap));
        }, TaskScheduler.Default);
        return button;
    }

    private static BitmapSource? LoadThumbnail(string path, int pixels)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            image.DecodePixelWidth = pixels;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null; // not a picture WPF can read (WebP on older Windows), or gone: the glyph stays
        }
    }

    /// <summary>Opens a file or http(s) link with its default app; quietly does nothing when that fails.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { }
    }

    /// <summary>Three dots that pulse in turn: Claude is working (distinct from a disabled control).</summary>
    public static FrameworkElement Dots()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, S2, 0) };
        for (var k = 0; k < 3; k++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 3, 0), Opacity = 0.35 };
            dot.SetResourceReference(Shape.FillProperty, Brand.K.Accent);
            var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.2), RepeatBehavior = RepeatBehavior.Forever };
            var start = 0.2 * k;
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start))));
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + 0.3))));
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + 0.6))));
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
            dot.BeginAnimation(UIElement.OpacityProperty, pulse);
            panel.Children.Add(dot);
        }
        return panel;
    }

    /// <summary>A small muted line of text (names, times, labels).</summary>
    public static TextBlock Small(string text, string key = Brand.K.Muted, bool bold = false)
    {
        var t = Brand.Text(text, key, SmallSize, bold);
        t.TextWrapping = TextWrapping.NoWrap;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    /// <summary>Adds plain text with line breaks to an inline collection.</summary>
    public static void AddLines(InlineCollection inlines, string text)
    {
        var parts = text.Replace("\r", "").Split('\n');
        for (var k = 0; k < parts.Length; k++)
        {
            if (k > 0) inlines.Add(new LineBreak());
            if (parts[k].Length > 0) inlines.Add(new Run(parts[k]));
        }
    }
}
