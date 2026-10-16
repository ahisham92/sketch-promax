using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SketchProMax.Loader;

/// <summary>
/// The light/dark palette and control styles of the pane. A mirror of the core's UI.Brand (the loader can't reference
/// the core, and the core can't rely on new loader types because the loader is never updated in place): the same
/// token keys and values, so a change to one must be made to the other.
/// </summary>
internal static class Theme
{
    public static readonly Color Blue = Color.FromRgb(0x1D, 0x4E, 0xD8);
    public static readonly Color Navy = Color.FromRgb(0x0B, 0x1E, 0x4F);

    public static class K
    {
        public const string Bg = "SpmBg", Surface = "SpmSurface", SurfaceHover = "SpmSurfaceHover", Border = "SpmBorder";
        public const string Field = "SpmField", InputBorder = "SpmInputBorder", Text = "SpmText", Muted = "SpmMuted";
        public const string Accent = "SpmAccent", AccentHover = "SpmAccentHover", AccentPressed = "SpmAccentPressed", OnAccent = "SpmOnAccent";
        public const string Link = "SpmLink", Danger = "SpmDanger", DangerHover = "SpmDangerHover", DangerPressed = "SpmDangerPressed";
        public const string OnDanger = "SpmOnDanger", DangerText = "SpmDangerText", SuccessText = "SpmSuccessText", WarningText = "SpmWarningText";
        public const string Focus = "SpmFocus", NoticeBg = "SpmNoticeBg", NoticeBorder = "SpmNoticeBorder";
        public const string BadgeOk = "SpmBadgeOk", BadgeWarn = "SpmBadgeWarn", BadgeMuted = "SpmBadgeMuted", BadgeError = "SpmBadgeError";
        public const string HeaderText = "SpmHeaderText", HeaderSub = "SpmHeaderSub";
    }

    /// <summary>(key, light, dark). Identical to SketchProMax.UI.Brand.Tokens; see there for the contrast rules.</summary>
    public static readonly (string Key, uint Light, uint Dark)[] Tokens =
    [
        (K.Bg, 0xFFFFFF, 0x2B2F36),
        (K.Surface, 0xF1F5F9, 0x353A42),
        (K.SurfaceHover, 0xE2E8F0, 0x424852),
        (K.Border, 0xCBD5E1, 0x4B525C),
        (K.Field, 0xFFFFFF, 0x23272D),
        (K.InputBorder, 0x64748B, 0x8A93A0),
        (K.Text, 0x0F172A, 0xECEFF3),
        (K.Muted, 0x475569, 0xB4BCC8),
        (K.Accent, 0x1D4ED8, 0x2563EB),
        (K.AccentHover, 0x1E40AF, 0x1D4ED8),
        (K.AccentPressed, 0x1E3A8A, 0x1E40AF),
        (K.OnAccent, 0xFFFFFF, 0xFFFFFF),
        (K.Link, 0x1D4ED8, 0x93B4FF),
        (K.Danger, 0xB91C1C, 0xC62828),
        (K.DangerHover, 0x991B1B, 0xB71C1C),
        (K.DangerPressed, 0x7F1D1D, 0x991B1B),
        (K.OnDanger, 0xFFFFFF, 0xFFFFFF),
        (K.DangerText, 0xB91C1C, 0xFF9A92),
        (K.SuccessText, 0x15803D, 0x6EDC9A),
        (K.WarningText, 0xB45309, 0xFBC96B),
        (K.Focus, 0x1D4ED8, 0x93B4FF),
        (K.NoticeBg, 0xFFF7E0, 0x3A3220),
        (K.NoticeBorder, 0xD97706, 0xB7791F),
        (K.BadgeOk, 0x15803D, 0x15803D),
        (K.BadgeWarn, 0xB45309, 0xB45309),
        (K.BadgeMuted, 0x475569, 0x475569),
        (K.BadgeError, 0xB91C1C, 0xB91C1C),
        (K.HeaderText, 0xFFFFFF, 0xFFFFFF),
        (K.HeaderSub, 0xE0E7FF, 0xE0E7FF),
    ];

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Rgb(uint v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    /// <summary>True when Revit's UI theme is dark (Revit 2024+). The last theme read when it can't be read now (light at first).</summary>
    private static bool _lastDark;

    public static bool IsDark
    {
        get
        {
            try { return _lastDark = ReadDark(); }
            catch (Exception) { return _lastDark; } // not readable right now: keep the last theme read
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadDark() => Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark;

    private static readonly ConditionalWeakTable<FrameworkElement, object> DarkShown = new();
    private static ResourceDictionary? _styles;

    /// <summary>Gives a root the styles and the palette of the current theme; call again when the theme changes.</summary>
    public static void Use(FrameworkElement root)
    {
        var styles = Styles();
        if (!root.Resources.MergedDictionaries.Contains(styles)) root.Resources.MergedDictionaries.Add(styles);
        var dark = IsDark;
        if (DarkShown.TryGetValue(root, out var was) && (bool)was == dark) return;
        DarkShown.Remove(root);
        DarkShown.Add(root, dark);
        foreach (var (key, light, darkValue) in Tokens) root.Resources[key] = Brush(Rgb(dark ? darkValue : light));
    }

    public static T Fg<T>(T element, string key) where T : FrameworkElement
    {
        element.SetResourceReference(TextElement.ForegroundProperty, key);
        return element;
    }

    public static Button Button(object content, string style = "SpmSecondaryButton")
    {
        var button = new Button { Content = content };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        return button;
    }

    private static string ButtonStyle(string key, string bg, string hover, string pressed, string fg, string border, string pressedBorder) => $$"""
        <Style x:Key="{{key}}" TargetType="Button" BasedOn="{StaticResource SpmButtonBase}">
          <Setter Property="Background" Value="{DynamicResource {{bg}}}"/>
          <Setter Property="Foreground" Value="{DynamicResource {{fg}}}"/>
          <Setter Property="BorderBrush" Value="{DynamicResource {{border}}}"/>
          <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource {{hover}}}"/></Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter Property="Background" Value="{DynamicResource {{pressed}}}"/>
              <Setter Property="BorderBrush" Value="{DynamicResource {{pressedBorder}}}"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Background" Value="{DynamicResource SpmSurface}"/>
              <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
              <Setter Property="BorderBrush" Value="{DynamicResource SpmBorder}"/>
            </Trigger>
          </Style.Triggers>
        </Style>
        """;

    /// <summary>The pane's subset of the core's styles: focus ring, buttons, progress bar, links.</summary>
    private static ResourceDictionary Styles()
    {
        if (_styles is not null) return _styles;
        var xaml = $$$"""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="SpmFocusVisual">
                <Setter Property="Control.Template"><Setter.Value><ControlTemplate>
                  <Rectangle Margin="-3" RadiusX="6" RadiusY="6" StrokeThickness="2" Stroke="{DynamicResource SpmFocus}" SnapsToDevicePixels="True"/>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style x:Key="SpmButtonBase" TargetType="Button">
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
                <Setter Property="MinHeight" Value="28"/>
                <Setter Property="MinWidth" Value="28"/>
                <Setter Property="Padding" Value="10,3"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="HorizontalContentAlignment" Value="Center"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" SnapsToDevicePixels="True">
                      <ContentPresenter Margin="{TemplateBinding Padding}" RecognizesAccessKey="True"
                                        HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                        VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                    </Border>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
              {{{ButtonStyle("SpmSecondaryButton", K.Surface, K.SurfaceHover, K.SurfaceHover, K.Text, K.InputBorder, K.Accent)}}}
              {{{ButtonStyle("SpmPrimaryButton", K.Accent, K.AccentHover, K.AccentPressed, K.OnAccent, K.Accent, K.AccentPressed)}}}
              {{{ButtonStyle("SpmDangerButton", K.Danger, K.DangerHover, K.DangerPressed, K.OnDanger, K.Danger, K.DangerPressed)}}}
              <Style TargetType="Button" BasedOn="{StaticResource SpmSecondaryButton}"/>
              <Style TargetType="ProgressBar">
                <Setter Property="Foreground" Value="{DynamicResource SpmAccent}"/>
                <Setter Property="Background" Value="{DynamicResource SpmSurfaceHover}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SpmBorder}"/>
              </Style>
              <Style TargetType="Hyperlink">
                <Setter Property="Foreground" Value="{DynamicResource SpmLink}"/>
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
              </Style>
            </ResourceDictionary>
            """;
        // Never let a styling problem stop a window or the pane from opening: fall back to the plain WPF look.
        try { _styles = (ResourceDictionary)XamlReader.Parse(xaml); }
        catch (Exception) { _styles = new ResourceDictionary(); }
        return _styles;
    }

    private static readonly ConditionalWeakTable<Button, object> BusyContent = new();

    public static bool IsBusy(Button button) => BusyContent.TryGetValue(button, out _);

    /// <summary>Full-strength "working" look: a small spinner and a label, clicks ignored. No-op when already in that state.</summary>
    public static void SetBusy(Button button, bool busy, string? text = null)
    {
        if (busy)
        {
            if (BusyContent.TryGetValue(button, out var original)) return;
            original = button.Content ?? "";
            BusyContent.Add(button, original);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Spinner());
            row.Children.Add(new TextBlock { Text = text ?? original as string ?? "Working…", VerticalAlignment = VerticalAlignment.Center });
            button.Content = row;
            button.IsHitTestVisible = false;
            AutomationProperties.SetItemStatus(button, "Busy");
        }
        else if (BusyContent.TryGetValue(button, out var original))
        {
            BusyContent.Remove(button);
            button.Content = original;
            button.IsHitTestVisible = true;
            AutomationProperties.SetItemStatus(button, "");
        }
    }

    public static FrameworkElement Spinner()
    {
        var arc = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6,1 A5,5 0 1 1 1,6"), StrokeThickness = 2, Width = 12, Height = 12, Stretch = Stretch.None,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        arc.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self });
        var spin = new RotateTransform();
        arc.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        AutomationProperties.SetName(arc, "Working");
        return arc;
    }
}
