using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SketchProMax.UI;

/// <summary>
/// The product look for Sketch ProMax windows: logo, the light/dark theme palette, control styles and a few shared
/// building blocks. Every colour lives in <see cref="Tokens"/> (a light and a dark value per token); windows refer to
/// tokens by resource key only, so they follow Revit's UI theme and switch live when it changes.
/// The loader's pane mirrors this palette (SketchProMax.Loader.Theme, which can't reference the core): keep both in step.
/// </summary>
internal static class Brand
{
    /// <summary>The header band's gradient: the same in both themes (it is the brand, with white text on it).</summary>
    public static readonly Color Blue = Color.FromRgb(0x1D, 0x4E, 0xD8);
    public static readonly Color Navy = Color.FromRgb(0x0B, 0x1E, 0x4F);

    /// <summary>Resource keys of the palette. Use them with SetResourceReference so colours follow the theme.</summary>
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

    /// <summary>
    /// The palette: (key, light, dark). Every text pair meets WCAG AA 4.5:1 in both themes, hover and disabled states
    /// included (disabled text is Muted on Surface); the focus ring and input borders meet 3:1 against the background.
    /// Recheck the ratios when changing a value.
    /// </summary>
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

    public enum Weight { Secondary, Primary, Danger }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Rgb(uint v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    // ---- Theme -------------------------------------------------------------------------------------------------

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

    private static readonly List<FrameworkElement> Themed = [];
    private static readonly ConditionalWeakTable<FrameworkElement, object> DarkShown = new();
    private static DispatcherTimer? _poll;
    private static ResourceDictionary? _styles;

    /// <summary>
    /// Gives a window (or any root) the palette and the control styles, and keeps it in step with Revit's theme: a
    /// cheap check every 2 s while it is open (the core gets no theme event of its own) and whenever it is activated.
    /// Safe to call more than once.
    /// </summary>
    public static void UseTheme(FrameworkElement root)
    {
        var styles = Styles();
        if (!root.Resources.MergedDictionaries.Contains(styles)) root.Resources.MergedDictionaries.Add(styles);
        Apply(root, IsDark);
        if (root is Control control)
        {
            control.SetResourceReference(Control.BackgroundProperty, K.Bg);
            control.SetResourceReference(Control.ForegroundProperty, K.Text);
        }
        if (Themed.Contains(root)) return;
        Themed.Add(root);
        if (root is Window window)
        {
            window.Activated += (_, _) => Recheck();
            window.Closed += (_, _) =>
            {
                Themed.Remove(root);
                if (Themed.Count == 0) _poll?.Stop();
            };
        }
        if (_poll is null)
        {
            _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _poll.Tick += (_, _) => Recheck();
        }
        _poll.Start();
    }

    private static void Recheck()
    {
        var dark = IsDark;
        foreach (var root in Themed.ToList()) Apply(root, dark);
    }

    private static void Apply(FrameworkElement root, bool dark)
    {
        if (DarkShown.TryGetValue(root, out var was) && (bool)was == dark) return;
        DarkShown.Remove(root);
        DarkShown.Add(root, dark);
        foreach (var (key, light, darkValue) in Tokens) root.Resources[key] = Brush(Rgb(dark ? darkValue : light));
    }

    /// <summary>Points an element's text colour at a palette token.</summary>
    public static T Fg<T>(T element, string key) where T : FrameworkElement
    {
        element.SetResourceReference(TextElement.ForegroundProperty, key);
        return element;
    }

    /// <summary>A wrapping text block in a palette colour.</summary>
    public static TextBlock Text(string text, string key = K.Text, double size = 12, bool bold = false) =>
        Fg(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        }, key);

    // ---- Styles ------------------------------------------------------------------------------------------------

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

    /// <summary>
    /// Control styles for both themes, built once. Templates are replaced (not just recoloured) because the stock
    /// Windows templates paint fixed light colours on hover and press, which would put light text on a light fill in the
    /// dark theme. Every state uses palette tokens only.
    /// </summary>
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
                <Setter Property="Padding" Value="12,4"/>
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

              <Style TargetType="TextBox">
                <Setter Property="Background" Value="{DynamicResource SpmField}"/>
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SpmInputBorder}"/>
                <Setter Property="CaretBrush" Value="{DynamicResource SpmText}"/>
                <Setter Property="SelectionBrush" Value="{DynamicResource SpmAccent}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="4,3"/>
                <Setter Property="MinHeight" Value="26"/>
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TextBox">
                    <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="3" SnapsToDevicePixels="True">
                      <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"
                                    HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmAccent}"/></Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/>
                        <Setter TargetName="bd" Property="BorderThickness" Value="2"/>
                      </Trigger>
                      <Trigger Property="IsEnabled" Value="False">
                        <Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmSurface}"/>
                        <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="CheckBox">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="MinHeight" Value="24"/>
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="CheckBox">
                    <Grid Background="Transparent">
                      <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <Border x:Name="box" Width="16" Height="16" CornerRadius="3" BorderThickness="1.5" VerticalAlignment="Center"
                              BorderBrush="{DynamicResource SpmInputBorder}" Background="{DynamicResource SpmField}" SnapsToDevicePixels="True">
                        <Path x:Name="mark" Data="M2.5,6.5 L5.5,9.5 L10.5,3.5" Stroke="{DynamicResource SpmOnAccent}" StrokeThickness="2" Visibility="Collapsed"/>
                      </Border>
                      <ContentPresenter Grid.Column="1" Margin="8,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource SpmAccent}"/></Trigger>
                      <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="box" Property="Background" Value="{DynamicResource SpmAccent}"/>
                        <Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource SpmAccent}"/>
                        <Setter TargetName="mark" Property="Visibility" Value="Visible"/>
                      </Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="ComboBoxItem">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="MinHeight" Value="24"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="ComboBoxItem">
                    <Border x:Name="bd" Background="Transparent" Padding="6,3" SnapsToDevicePixels="True">
                      <ContentPresenter VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
                      <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmAccent}"/>
                        <Setter Property="Foreground" Value="{DynamicResource SpmOnAccent}"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="ComboBox">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="MinHeight" Value="26"/>
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="ComboBox">
                    <Grid>
                      <ToggleButton Focusable="False" ClickMode="Press"
                                    IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                        <ToggleButton.Template>
                          <ControlTemplate TargetType="ToggleButton">
                            <Border x:Name="bd" Background="{DynamicResource SpmField}" BorderBrush="{DynamicResource SpmInputBorder}"
                                    BorderThickness="1" CornerRadius="3" SnapsToDevicePixels="True">
                              <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,9,0" Data="M0,0 L4,4 L8,0"
                                    Stroke="{DynamicResource SpmText}" StrokeThickness="1.5"/>
                            </Border>
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmAccent}"/></Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </ToggleButton.Template>
                      </ToggleButton>
                      <ContentPresenter IsHitTestVisible="False" Margin="7,3,26,3" VerticalAlignment="Center"
                                        Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
                      <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                             IsOpen="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}">
                        <Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                                MaxHeight="{TemplateBinding MaxDropDownHeight}" Background="{DynamicResource SpmField}"
                                BorderBrush="{DynamicResource SpmInputBorder}" BorderThickness="1">
                          <ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                        </Border>
                      </Popup>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

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

    // ---- Logo and window chrome --------------------------------------------------------------------------------

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

    /// <summary>
    /// Applies the window icon and the theme, and makes Revit the owner, so the window stays with Revit and never
    /// hides behind it.
    /// </summary>
    public static void Prepare(Window window)
    {
        if (Icon is { } icon) window.Icon = icon;
        window.ShowInTaskbar = false;
        UseTheme(window);
        var main = App.MainWindow;
        if (main != IntPtr.Zero)
        {
            try { new WindowInteropHelper(window).Owner = main; }
            catch (Exception) { /* no owner: still works */ }
        }
    }

    /// <summary>The blue header band used at the top of Sketch ProMax windows (the same in both themes).</summary>
    public static Border Header(string title, string? subtitle)
    {
        var gradient = new LinearGradientBrush(Blue, Navy, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        var row = new DockPanel { LastChildFill = true };
        if (Logo is { } logo)
        {
            var image = new Image { Source = logo, Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) };
            AutomationProperties.SetName(image, "Sketch ProMax");
            DockPanel.SetDock(image, Dock.Left);
            row.Children.Add(image);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(title, K.HeaderText, 16, bold: true));
        if (subtitle is not null) text.Children.Add(Text(subtitle, K.HeaderSub));
        row.Children.Add(text);
        return new Border { Background = gradient, Padding = new Thickness(16, 12, 16, 12), Child = row };
    }

    /// <summary>
    /// A dialog button. Each window has one <see cref="Weight.Primary"/> action (make it IsDefault); destructive
    /// actions (delete, overwrite, roll back, cancel a job, stop) are <see cref="Weight.Danger"/> and never IsDefault.
    /// </summary>
    public static Button Button(string text, Weight weight = Weight.Secondary)
    {
        var button = new Button { Content = text, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, weight switch
        {
            Weight.Primary => "SpmPrimaryButton",
            Weight.Danger => "SpmDangerButton",
            _ => "SpmSecondaryButton",
        });
        return button;
    }

    /// <summary>The right-aligned button row at the foot of a dialog: secondary actions first, the primary one last.</summary>
    public static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    // ---- Busy state --------------------------------------------------------------------------------------------

    private static readonly ConditionalWeakTable<Button, object> BusyContent = new();

    /// <summary>True while <see cref="SetBusy"/> shows the button as working.</summary>
    public static bool IsBusy(Button button) => BusyContent.TryGetValue(button, out _);

    /// <summary>
    /// Shows a button as working: full colour (not the disabled look) with a small spinner and an optional label such
    /// as "Stopping…". Clicks are ignored until it is set back. No-op when it is already busy.
    /// </summary>
    public static void SetBusy(Button button, bool busy, string? text = null)
    {
        if (busy)
        {
            if (BusyContent.TryGetValue(button, out var original)) return; // already working: keep the spinner turning
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

    /// <summary>A 12 px spinning arc in the text colour of whatever holds it.</summary>
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
