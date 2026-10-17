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
/// picture thumbnails and the typing dots. Colours come from the Brand tokens only, so both Revit themes work.
/// Spacing follows a 4 px scale (<see cref="S1"/>..<see cref="S4"/>).
/// </summary>
internal static class ChatUi
{
    public const double S1 = 4, S2 = 8, S3 = 12, S4 = 16;
    public const double BodySize = 12.5, SmallSize = 11, CodeSize = 11.5;

    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    // Segoe MDL2 / Fluent glyphs.
    public const string GlyphCopy = "\uE8C8", GlyphCheck = "\uE73E", GlyphCross = "\uE711", GlyphStop = "\uE71A";
    public const string GlyphRight = "\uE76C", GlyphDown = "\uE70D", GlyphArrowDown = "\uE74B", GlyphError = "\uE783";
    public const string GlyphInfo = "\uE946", GlyphPicture = "\uEB9F", GlyphUp = "\uE70E", GlyphFile = "\uE8A5";
    public const string GlyphMenu = "\uE700", GlyphAdd = "\uE710", GlyphRefresh = "\uE72C", GlyphMore = "\uE712";
    public const string GlyphRename = "\uE8AC", GlyphPin = "\uE718", GlyphSave = "\uE74E", GlyphDelete = "\uE74D";

    private static ResourceDictionary? _styles;

    /// <summary>The chat's own styles, merged into the pane next to the Brand styles.</summary>
    public static ResourceDictionary Styles()
    {
        if (_styles is not null) return _styles;
        const string xaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="SpmChatGhost" TargetType="Button">
                <Setter Property="FocusVisualStyle" Value="{DynamicResource SpmFocusVisual}"/>
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
                <Setter Property="BorderBrush" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="MinHeight" Value="24"/>
                <Setter Property="MinWidth" Value="24"/>
                <Setter Property="Padding" Value="6,2"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="HorizontalContentAlignment" Value="Center"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" SnapsToDevicePixels="True">
                      <ContentPresenter Margin="{TemplateBinding Padding}" RecognizesAccessKey="True"
                                        HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                        VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/>
                        <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                      </Trigger>
                      <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmBorder}"/>
                      </Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.6"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
              <Style x:Key="SpmChatPill" TargetType="Button" BasedOn="{StaticResource SpmChatGhost}">
                <Setter Property="Background" Value="{DynamicResource SpmField}"/>
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SpmInputBorder}"/>
                <Setter Property="Padding" Value="10,3"/>
                <Setter Property="MinHeight" Value="28"/>
              </Style>
              <Style x:Key="SpmChatDisclosure" TargetType="ToggleButton">
                <Setter Property="FocusVisualStyle" Value="{DynamicResource SpmFocusVisual}"/>
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="MinHeight" Value="28"/>
                <Setter Property="Padding" Value="8,4"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="ToggleButton">
                    <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="5" SnapsToDevicePixels="True">
                      <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                        VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
            </ResourceDictionary>
            """;
        try { _styles = (ResourceDictionary)XamlReader.Parse(xaml); }
        catch (Exception) { _styles = new ResourceDictionary(); }
        return _styles;
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
