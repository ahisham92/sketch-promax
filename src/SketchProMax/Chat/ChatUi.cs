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
/// tokens only, so both Revit themes work. Spacing follows an 8 px grid with 4 px half steps (<see cref="S1"/>..<see cref="S6"/>);
/// corners are <see cref="RadiusSmall"/> on buttons and fields and <see cref="Radius"/> on cards and the composer. Text
/// sizes come from the type scale in <see cref="ChatStyles"/>.
///
/// Crispness: nothing in the chat is drawn to a bitmap and scaled (no RenderTargetBitmap outside the DPI snapshot, no
/// BitmapCache, no Viewbox or ScaleTransform on text, no effect on an element holding text); icons are vector paths or
/// icon-font glyphs; borders are whole pixels. Motion (<see cref="Animate"/>) moves wrappers and layers without text, and
/// takes its transform off again when it ends.
/// </summary>
internal static class ChatUi
{
    public const double S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20, S6 = 24, S8 = 32;
    public const double BodySize = ChatStyles.Body, SmallSize = ChatStyles.Caption, CodeSize = ChatStyles.Caption, TitleSize = ChatStyles.Subtitle;
    public const double HeadingSize = ChatStyles.Title, GreetingSize = ChatStyles.Display;
    public const double RadiusSmall = 8, Radius = 12;

    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>Windows 11's text face where it is installed, Segoe UI elsewhere (WPF falls back along the list).</summary>
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");

    /// <summary>The same face cut for large sizes (headings, the greeting).</summary>
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");

    /// <summary>
    /// False when the chat's animation setting is Off, Windows' "Show animations" is off, or in the DPI snapshot: every
    /// transition then jumps to its end. Read each time, so changing either setting applies without restarting Revit.
    /// </summary>
    public static bool Animate => Level != Motion.Off;

    /// <summary>
    /// True only at Full: the looping and decorative motion (the thinking mark, the shimmer's sweep, new text fading in,
    /// a step's check popping in, Send and Stop morphing, the finished glow). Reduced keeps only quick opens and closes.
    /// </summary>
    public static bool Lively => Level == Motion.Full;

    /// <summary>How much the chat moves: the setting, unless Windows' animations are off (then nothing moves).</summary>
    public static Motion Level => NoMotion || !SystemParameters.ClientAreaAnimation ? Motion.Off : Setting;

    /// <summary>The chat's animation setting (Settings, "Chat animations"), read once and then kept up to date by <see cref="SetMotion"/>.</summary>
    public static Motion Setting
    {
        get
        {
            if (_setting is { } known) return known;
            try { _setting = MotionLevels.Parse(Settings.AgentSettings.Load().ChatAnimations); }
            catch (Exception) { _setting = Motion.Full; }
            return _setting.Value;
        }
    }

    private static Motion? _setting;
    private static bool _watching;

    /// <summary>Raised on the UI thread when the effective level may have changed (the setting, or Windows' animations).</summary>
    public static event Action? MotionChanged;

    /// <summary>Applies a new animation setting straight away to every open chat pane.</summary>
    public static void SetMotion(Motion level)
    {
        if (_setting == level) return;
        _setting = level;
        MotionChanged?.Invoke();
    }

    /// <summary>Starts following Windows' "Show animations" so a change there reaches the panes without a restart.</summary>
    internal static void WatchWindows()
    {
        if (_watching) return;
        _watching = true;
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) MotionChanged?.Invoke();
        };
    }

    /// <summary>Set while the DPI snapshot draws, so it captures the settled state.</summary>
    internal static bool NoMotion;

    /// <summary>Large text keeps its shapes (Ideal); smaller text is snapped to the pixel grid (Display), see ChatStyles.IdealFrom.</summary>
    public static T Large<T>(T element) where T : DependencyObject
    {
        TextOptions.SetTextFormattingMode(element, TextFormattingMode.Ideal);
        return element;
    }

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
        public const string ChevronDown = "M4.5,6.5 L8,10 L11.5,6.5";
        public const string ChevronRight = "M6,3.5 L10.5,8 L6,12.5";
        public const string ArrowDown = "M8,3 L8,12.5 M4,8.5 L8,12.5 L12,8.5";
        public const string Square = "M5,5 L11,5 L11,11 L5,11 Z";
        public const string Check = "M3.5,8.5 L6.5,11.5 L12.5,4.5";
        public const string Cross = "M4.5,4.5 L11.5,11.5 M11.5,4.5 L4.5,11.5";
        public const string Shrink = "M8,1.5 L8,6 M5.5,3.5 L8,6 L10.5,3.5 M8,14.5 L8,10 M5.5,12.5 L8,10 L10.5,12.5 M3,8 L13,8";
    }

    private static readonly Dictionary<bool, ResourceDictionary> _styles = new();

    /// <summary>Why the chat's styles didn't load as designed (null when they did), for the field test and Diagnostics.</summary>
    public static string? StylesError { get; private set; }

    /// <summary>
    /// The chat's own styles (<see cref="ChatStyles"/>), merged into the pane next to the Brand styles: with hover fades
    /// while <see cref="Animate"/>, without them otherwise (a pane swaps them on <see cref="MotionChanged"/>).
    /// </summary>
    public static ResourceDictionary Styles() => Styles(Animate);

    public static ResourceDictionary Styles(bool animate)
    {
        if (_styles.TryGetValue(animate, out var known)) return known;
        ResourceDictionary styles;
        try { styles = (ResourceDictionary)XamlReader.Parse(ChatStyles.Xaml(RadiusSmall, Radius, animate)); }
        catch (Exception ex)
        {
            StylesError = ex.Message;
            System.Diagnostics.Trace.WriteLine("Sketch ProMax chat styles: " + ex.Message);
            // The same look without motion, press scaling or shadows; only if that fails too, plain Windows buttons.
            try { styles = (ResourceDictionary)XamlReader.Parse(ChatStyles.Xaml(RadiusSmall, Radius, animate: false, plain: true)); }
            catch (Exception ex2)
            {
                StylesError += " | plain: " + ex2.Message;
                styles = new ResourceDictionary(); // never a pane that doesn't open
            }
        }
        _styles[animate] = styles;
        return styles;
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
        Opaque(frame);
        frame.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        var grid = new Grid();
        grid.Children.Add(shadow);
        grid.Children.Add(frame);
        return grid;
    }

    /// <summary>
    /// The chat's own mark: a rounded tile in the brand blues with a white isometric block, drawn as vectors at the size
    /// asked for (the geometry is scaled, not the drawing, so strokes stay at least a pixel wide and nothing is a bitmap).
    /// </summary>
    public static FrameworkElement Mark(double size = 28)
    {
        var k = size / 28;
        var scale = new ScaleTransform(k, k);
        scale.Freeze();
        Geometry Scaled(string data)
        {
            if (k == 1) return Geometry(data);
            // Geometry.Parse hands back a frozen geometry: setting Transform on it throws, so transform a copy.
            var g = System.Windows.Media.Geometry.Parse(data).Clone();
            g.Transform = scale;
            g.Freeze();
            return g;
        }
        var tile = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(Math.Round(8 * k)),
            Background = Frozen(new LinearGradientBrush(Brand.Blue, Brand.Navy, new Point(0, 0), new Point(1, 1))),
        };
        var block = new System.Windows.Shapes.Path
        {
            Data = Scaled("M14,6.5 L20.5,10 L20.5,17.5 L14,21 L7.5,17.5 L7.5,10 Z M7.5,10 L14,13.5 L20.5,10 M14,13.5 L14,21"),
            Stroke = Brushes.White, StrokeThickness = Math.Max(1, 1.4 * k), StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None,
        };
        var spark = new System.Windows.Shapes.Path
        {
            Data = Scaled("M22,3 L22.7,5.3 L25,6 L22.7,6.7 L22,9 L21.3,6.7 L19,6 L21.3,5.3 Z"), Fill = Frozen(new SolidColorBrush(Color.FromRgb(0xFD, 0xE0, 0x47))),
            Stretch = Stretch.None,
        };
        var mark = new Grid { Width = size, Height = size, SnapsToDevicePixels = true };
        mark.Children.Add(tile);
        mark.Children.Add(block);
        mark.Children.Add(spark);
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
        var icon = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        if (!string.IsNullOrEmpty(text))
            row.Children.Add(new TextBlock { Text = text, FontSize = SmallSize, Margin = new Thickness(S1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
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

    /// <summary>
    /// Sketch ProMax is thinking: its mark, which breathes (a slow, slight swell) while its spark twinkles. Vectors only,
    /// no text, so scaling it keeps everything crisp. Runs only while shown and at <see cref="Lively"/>; otherwise the
    /// mark stands still, so the working line never looks disabled.
    /// </summary>
    public static FrameworkElement Thinking()
    {
        var mark = (Grid)Mark(20);
        mark.Margin = new Thickness(0, 0, S2, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.RenderTransformOrigin = new Point(0.5, 0.5);
        AutomationProperties.SetName(mark, "Working");
        var swell = new ScaleTransform(1, 1);
        mark.RenderTransform = swell;
        var spark = mark.Children[2];
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation Loop(double from, double to, double seconds) =>
            new(from, to, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        void Stop()
        {
            swell.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            swell.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            spark.BeginAnimation(UIElement.OpacityProperty, null);
        }
        void Start()
        {
            Stop();
            if (!Lively) return;
            var breathe = Loop(1, 0.88, 0.9);
            swell.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
            swell.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
            spark.BeginAnimation(UIElement.OpacityProperty, Loop(1, 0.25, 0.6));
        }
        Brand.WhileVisible(mark, Start, Stop);
        return mark;
    }

    /// <summary>A step's end mark: a check (done) or a cross (failed) drawn as vectors, so it can pop in crisply.</summary>
    public static FrameworkElement StepMark(bool done)
    {
        var icon = Icon(done ? Paths.Check : Paths.Cross, 16, stroke: 2);
        icon.SetResourceReference(TextElement.ForegroundProperty, done ? Brand.K.SuccessText : Brand.K.DangerText);
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        return icon;
    }

    /// <summary>
    /// A step's running mark: the spinning arc over a faint full ring, so the step reads as "in progress" at a glance.
    /// </summary>
    public static FrameworkElement StepRing()
    {
        var grid = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var track = new Ellipse { StrokeThickness = 2, Margin = new Thickness(0) };
        track.SetResourceReference(Shape.StrokeProperty, Brand.K.BorderSubtle);
        grid.Children.Add(track);
        var spinner = Brand.Spinner();
        spinner.Margin = new Thickness(0);
        spinner.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Accent);
        grid.Children.Add(spinner);
        AutomationProperties.SetName(grid, "Running");
        return grid;
    }

    /// <summary>
    /// Pops an element without text in (a step's check, Send or Stop taking each other's place): it grows from
    /// <paramref name="from"/> with a slight overshoot and fades in, then the transform comes off. Only at <see cref="Lively"/>.
    /// </summary>
    public static void Pop(UIElement element, double from = 0.4, double ms = 260)
    {
        if (!Lively) return;
        var scale = new ScaleTransform(from, from);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        var grow = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
        grow.Completed += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (element.RenderTransform == scale) element.ClearValue(UIElement.RenderTransformProperty);
            element.BeginAnimation(UIElement.OpacityProperty, null);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms * 0.6)));
    }

    /// <summary>
    /// A soft accent outline that glows around <paramref name="element"/> and fades (a long task just finished), drawn
    /// on the adorner layer above it: nothing is drawn through the text, which keeps ClearType. Only at <see cref="Lively"/>.
    /// </summary>
    public static void Glow(FrameworkElement element)
    {
        if (!Lively || element.ActualWidth <= 0) return;
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(element);
        if (layer is null) return;
        var glow = new GlowAdorner(element);
        layer.Add(glow);
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1800) };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        pulse.Completed += (_, _) => layer.Remove(glow);
        glow.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    /// <summary>
    /// Flashes Revit's taskbar button until Revit is in front again, when another program is in front now (a long task
    /// finished while you were elsewhere). Not when the animation level is Off. Quietly does nothing if Windows refuses.
    /// </summary>
    public static bool FlashIfAway(Visual from)
    {
        if (!Animate) return false;
        try
        {
            if (PresentationSource.FromVisual(from) is not System.Windows.Interop.HwndSource source) return false;
            var root = NativeMethods.GetAncestor(source.Handle, NativeMethods.GaRootOwner);
            if (root == IntPtr.Zero) root = source.Handle;
            var front = NativeMethods.GetForegroundWindow();
            if (front != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(front, out var pid);
                if (pid == (uint)Process.GetCurrentProcess().Id) return false; // Revit (or one of its windows) is in front
            }
            var info = new NativeMethods.FlashInfo
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FlashInfo>(), Window = root,
                Flags = NativeMethods.FlashTray | NativeMethods.FlashUntilFront, Count = 3,
            };
            return NativeMethods.FlashWindowEx(ref info);
        }
        catch (Exception) { return false; } // cosmetic
    }

    private static class NativeMethods
    {
        public const uint GaRootOwner = 3, FlashTray = 2, FlashUntilFront = 12;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool FlashWindowEx(ref FlashInfo info);
    }

    /// <summary>The finished glow's outline: a 2 px accent ring with a faint tint, 6 px outside the element's box.</summary>
    private sealed class GlowAdorner : System.Windows.Documents.Adorner
    {
        public GlowAdorner(UIElement element) : base(element)
        {
            IsHitTestVisible = false;
            Opacity = 0;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var accent = (TryFindResource(Brand.K.Accent) as SolidColorBrush)?.Color ?? Brand.Blue;
            var stroke = new SolidColorBrush(accent);
            stroke.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(0x14, accent.R, accent.G, accent.B));
            fill.Freeze();
            var box = new Rect(AdornedElement.RenderSize);
            box.Inflate(6, 4);
            dc.DrawRoundedRectangle(fill, new Pen(stroke, 2), box, Radius, Radius);
        }
    }

    // ---- Crispness and motion ------------------------------------------------------------------------------------

    /// <summary>
    /// Marks an opaque container: its text keeps ClearType even inside a layered window (the model menu's popup) or
    /// while a parent fades.
    /// </summary>
    public static T Opaque<T>(T element) where T : DependencyObject
    {
        RenderOptions.SetClearTypeHint(element, ClearTypeHint.Enabled);
        return element;
    }

    private static DoubleAnimation Ease(double? from, double to, Action? done = null, double ms = ChatStyles.MotionMs)
    {
        var a = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done is not null) a.Completed += (_, _) => done();
        return a;
    }

    /// <summary>
    /// A new message eases in: the element fades from 0 and rises <paramref name="rise"/> px (8 by default) over 200 ms.
    /// When it ends the animation and the transform are taken off, so the text settles at full opacity on whole pixels.
    /// </summary>
    public static void Arrive(UIElement element, double rise = S2)
    {
        if (!Animate) return;
        var lift = new TranslateTransform(0, rise);
        element.RenderTransform = lift;
        void Settle()
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.ClearValue(UIElement.OpacityProperty);
            lift.BeginAnimation(TranslateTransform.YProperty, null);
            if (element.RenderTransform == lift) element.ClearValue(UIElement.RenderTransformProperty);
        }
        element.BeginAnimation(UIElement.OpacityProperty, Ease(0, 1, Settle, rise > S2 ? 260 : 200));
        lift.BeginAnimation(TranslateTransform.YProperty, Ease(rise, 0, null, rise > S2 ? 260 : 200));
    }

    /// <summary>
    /// Opens or closes a small element (a tool step's details, the typing line) by animating its height, clipped, then
    /// hands the height back to layout. Only for small elements: a large tree would lay out again on every frame.
    /// </summary>
    public static void Reveal(FrameworkElement element, bool open, Action? closed = null)
    {
        // Each call outdates the one before: a late Completed of a replaced animation must not collapse an element that
        // is opening again, or hand back a height.
        var turn = Turns.GetOrCreateValue(element);
        var mine = ++turn.Value;
        bool Current() => turn.Value == mine;
        element.BeginAnimation(FrameworkElement.HeightProperty, null);
        var shown = element.Visibility == Visibility.Visible;
        if (!Animate || (open && shown && double.IsNaN(element.Height)) || (!open && !shown))
        {
            element.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            element.Height = double.NaN;
            if (!open) closed?.Invoke();
            return;
        }
        element.ClipToBounds = true;
        if (open)
        {
            var from = shown ? element.ActualHeight : 0;
            element.Visibility = Visibility.Visible;
            var width = element.ActualWidth > 0 ? element.ActualWidth : (element.Parent as FrameworkElement)?.ActualWidth ?? double.PositiveInfinity;
            element.Height = double.NaN;
            element.Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
            var to = element.DesiredSize.Height - element.Margin.Top - element.Margin.Bottom;
            if (to <= 0) return;
            element.Height = from;
            element.BeginAnimation(FrameworkElement.HeightProperty, Ease(from, to, () =>
            {
                if (!Current()) return;
                element.BeginAnimation(FrameworkElement.HeightProperty, null);
                element.Height = double.NaN;
            }));
        }
        else
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, Ease(element.ActualHeight, 0, () =>
            {
                if (!Current()) return;
                element.BeginAnimation(FrameworkElement.HeightProperty, null);
                element.Visibility = Visibility.Collapsed;
                element.Height = double.NaN;
                closed?.Invoke();
            }));
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, System.Runtime.CompilerServices.StrongBox<int>> Turns = new();

    /// <summary>Slides an element in from <paramref name="dx"/> px to the side (or back out), ease-out; no transform once settled.</summary>
    public static void Slide(UIElement element, double dx, bool open, Action? done = null)
    {
        if (!Animate)
        {
            element.RenderTransform = null;
            done?.Invoke();
            return;
        }
        var move = new TranslateTransform(open ? dx : 0, 0);
        element.RenderTransform = move;
        move.BeginAnimation(TranslateTransform.XProperty, Ease(open ? dx : 0, open ? 0 : dx, () =>
        {
            move.BeginAnimation(TranslateTransform.XProperty, null);
            if (element.RenderTransform == move) element.ClearValue(UIElement.RenderTransformProperty);
            done?.Invoke();
        }, 200));
    }

    /// <summary>Fades a layer without text (the drawer's scrim) in or out.</summary>
    public static void Fade(UIElement element, bool show, Action? done = null)
    {
        if (!Animate)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            done?.Invoke();
            return;
        }
        element.BeginAnimation(UIElement.OpacityProperty, Ease(show ? 0 : element.Opacity, show ? 1 : 0, done));
    }

    /// <summary>
    /// A placeholder while Claude starts its answer: three rounded bars with a soft light sweeping across them. The
    /// sweep moves a brush, never text; with animations off the bars just stand still.
    /// </summary>
    public static FrameworkElement Shimmer()
    {
        var panel = new StackPanel { Margin = new Thickness(0, S2, 0, 0), IsHitTestVisible = false, Focusable = false };
        AutomationProperties.SetName(panel, "Loading");
        foreach (var width in new[] { 0.92, 0.78, 0.55 })
        {
            var bar = new Grid { Height = S2 + 2, Margin = new Thickness(0, 0, 0, S2) };
            var fill = new Border { CornerRadius = new CornerRadius(5) };
            fill.SetResourceReference(Border.BackgroundProperty, Brand.K.SurfaceHover);
            bar.Children.Add(fill);
            var shift = new TranslateTransform(-1, 0);
            var sweep = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), RelativeTransform = shift };
            sweep.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.2));
            sweep.GradientStops.Add(new GradientStop(Color.FromArgb(0x48, 255, 255, 255), 0.5));
            sweep.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.8));
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = sweep });
            // The sweep runs only while the placeholder shows: it is made once per pane and spends most of its life hidden.
            Brand.WhileVisible(bar, () =>
            {
                if (Lively) shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
            }, () => shift.BeginAnimation(TranslateTransform.XProperty, null));
            // A share of the width, so the bars read as lines of text at any pane width.
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - width, GridUnitType.Star) });
            row.Children.Add(bar);
            panel.Children.Add(row);
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
