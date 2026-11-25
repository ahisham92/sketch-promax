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
    public static readonly Color Blue = Color.FromRgb(0x1C, 0x63, 0xBC); // the CDT SDK accent
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
        // The chat's softer surfaces: raised cards and the composer, hairline borders, the accent tint of your messages.
        public const string Raised = "SpmRaised", BorderSubtle = "SpmBorderSubtle", AccentSoft = "SpmAccentSoft";
        // The composer's message box: a field a shade off the card, so it reads as the place to type.
        public const string ComposerField = "SpmComposerField";
        /// <summary>The chat's conversation area (cards and the message box sit raised on it) and the soft fills of its status badges.</summary>
        public const string Canvas = "SpmCanvas", SuccessSoft = "SpmSuccessSoft", DangerSoft = "SpmDangerSoft";
    }

    /// <summary>
    /// The palette: (key, light, dark), taken from the CDT SDK's palette (CDT.Wpf Palette.Light / Palette.Dark: Surface0
    /// for Bg, Surface2 for Field and Raised, Text0 / Text2 for Text / Muted, its accent, status and soft colours) so the
    /// windows' own surfaces match the SDK's controls; InputBorder, SurfaceHover and light Muted are a shade off the SDK's
    /// to keep the ratios below. Every text pair meets WCAG AA 4.5:1 in both themes, hover and disabled states
    /// included (disabled text is Muted on Surface); the focus ring and input borders meet 3:1 against the background.
    /// Recheck the ratios when changing a value.
    /// </summary>
    public static readonly (string Key, uint Light, uint Dark)[] Tokens =
    [
        (K.Bg, 0xF2F0E8, 0x1B1B19),
        (K.Surface, 0xE7E4DA, 0x2A2A27),
        (K.SurfaceHover, 0xE2DFD4, 0x323230),
        (K.Border, 0xDED9CC, 0x353532),
        (K.Field, 0xFFFFFF, 0x232321),
        (K.InputBorder, 0x7D786B, 0x7A766E),
        (K.Text, 0x1E1D1A, 0xF2EFE9),
        (K.Muted, 0x5F5C55, 0x9F9B92),
        (K.Accent, 0x1C63BC, 0x4B9FF3),
        (K.AccentHover, 0x1958A8, 0x3A96F2),
        (K.AccentPressed, 0x164E94, 0x2A8DF1),
        (K.OnAccent, 0xFFFFFF, 0x1B1B19),
        (K.Link, 0x1C63BC, 0x84BEF7),
        (K.Danger, 0xC42020, 0xF87171),
        (K.DangerHover, 0xB01D1D, 0xF76060),
        (K.DangerPressed, 0x9D1A1A, 0xF64F4F),
        (K.OnDanger, 0xFFFFFF, 0x1B1B19),
        (K.DangerText, 0xC42020, 0xFAA2A2),
        (K.SuccessText, 0x207410, 0x6BDE4A),
        (K.WarningText, 0x8D5918, 0xFB9B24),
        (K.Focus, 0x1C63BC, 0x84BEF7),
        (K.NoticeBg, 0xF4EEE8, 0x3D3121),
        (K.NoticeBorder, 0x8D5918, 0xFB9B24),
        (K.BadgeOk, 0x207410, 0x207410),
        (K.BadgeWarn, 0x8D5918, 0x8D5918),
        (K.BadgeMuted, 0x67645C, 0x67645C),
        (K.BadgeError, 0xC42020, 0xC42020),
        (K.HeaderText, 0xFFFFFF, 0xFFFFFF),
        (K.HeaderSub, 0xE4ECF7, 0xE4ECF7),
        // Core only (the loader's pane doesn't use them). Text and Muted on Raised and AccentSoft stay above 4.7:1 in
        // both themes; BorderSubtle is decorative (a focused field turns to Focus).
        (K.Raised, 0xFFFFFF, 0x2A2A27),
        (K.BorderSubtle, 0xDED9CC, 0x353532),
        (K.AccentSoft, 0xE4ECF7, 0x28323A),
        // Text 16:1 / 14:1, Muted (the hint) 6.4:1 / 5.7:1, InputBorder 4.2:1 / 3.5:1, Focus 5.6:1 / 8.0:1 on it.
        (K.ComposerField, 0xFBFAF6, 0x232321),
        // The chat's conversation area: Text 15:1, Muted 5.8:1 / 6.2:1, Link, WarningText, SuccessText and DangerText 5.1:1
        // or more on it. Badges: SuccessText on SuccessSoft 5.1:1 / 7.1:1, DangerText on DangerSoft 5.4:1 / 7.7:1, Link on
        // AccentSoft 5.0:1 / 6.6:1.
        (K.Canvas, 0xF2F0E8, 0x1B1B19),
        (K.SuccessSoft, 0xE9F1E7, 0x2C3926),
        (K.DangerSoft, 0xFDF3F3, 0x332321),
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
        // The CDT SDK's style sheet first, then Sketch ProMax's (its implicit and Spm* button styles are based on the
        // SDK's, see Styles), so a window gets every SDK style by key and the SDK look on plain controls.
        var cdt = CdtTheme.Styles;
        if (cdt is not null && !root.Resources.MergedDictionaries.Contains(cdt)) root.Resources.MergedDictionaries.Add(cdt);
        var styles = Styles();
        if (!root.Resources.MergedDictionaries.Contains(styles)) root.Resources.MergedDictionaries.Add(styles);
        var dark = IsDark;
        Apply(root, dark);
        CdtTheme.Follow(dark);
        if (root is Control control)
        {
            control.SetResourceReference(Control.BackgroundProperty, K.Bg);
            control.SetResourceReference(Control.ForegroundProperty, K.Text);
            if (cdt is not null) control.SetResourceReference(Control.FontFamilyProperty, "CDT.UIFont");
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

    /// <summary>Stops following the theme for a root that is going away (the chat element of a core being replaced).</summary>
    public static void ReleaseTheme(FrameworkElement root)
    {
        Themed.Remove(root);
        if (Themed.Count == 0) _poll?.Stop();
    }

    /// <summary>Shows a root in the given theme now, whatever Revit's is (the chat's DPI snapshot draws both).</summary>
    public static void ShowTheme(FrameworkElement root, bool dark) => Apply(root, dark);

    private static void Recheck()
    {
        var dark = IsDark;
        CdtTheme.Follow(dark);
        foreach (var root in Themed.ToList()) Apply(root, dark);
    }

    /// <summary>
    /// Themes a root that something else themes too (the loader's pane, which writes its own older copy of the palette
    /// when Revit's theme changes): applies the theme, and writes it again whenever the other writer has put its colours
    /// back. Cheap enough for a timer: one brush is compared.
    /// </summary>
    public static void Claim(FrameworkElement root)
    {
        UseTheme(root);
        var dark = IsDark;
        var (_, light, darkValue) = Tokens[0];
        var want = Rgb(dark ? darkValue : light);
        if (root.Resources[K.Bg] is SolidColorBrush b && b.Color == want) return;
        DarkShown.Remove(root);
        Apply(root, dark);
    }

    /// <summary>Takes this core's styles back off a root it claimed (the core is being replaced).</summary>
    public static void Unclaim(FrameworkElement root)
    {
        ReleaseTheme(root);
        if (CdtTheme.Styles is { } cdt) root.Resources.MergedDictionaries.Remove(cdt);
        if (_styles is not null) root.Resources.MergedDictionaries.Remove(_styles);
        DarkShown.Remove(root);
    }

    private static void Apply(FrameworkElement root, bool dark)
    {
        if (DarkShown.TryGetValue(root, out var was) && (bool)was == dark) return;
        DarkShown.Remove(root);
        DarkShown.Add(root, dark);
        foreach (var (key, light, darkValue) in Tokens) root.Resources[key] = Brush(Rgb(dark ? darkValue : light));
        CdtTheme.WritePalette(root.Resources, dark);
        if (root is Window window) CdtTheme.TitleBar(window, dark);
    }

    /// <summary>
    /// The palette as a new dictionary of frozen brushes, for a window on a thread of its own (the chat's job window),
    /// which can't share this thread's resources. Read <see cref="IsDark"/> on Revit's thread and pass it in.
    /// </summary>
    public static ResourceDictionary Palette(bool dark)
    {
        var dictionary = new ResourceDictionary();
        foreach (var (key, light, darkValue) in Tokens) dictionary[key] = Brush(Rgb(dark ? darkValue : light));
        // The SDK's palette too, as frozen copies (the SDK's own brushes belong to this thread).
        var cdt = new ResourceDictionary();
        CdtTheme.WritePalette(cdt, dark);
        foreach (System.Collections.DictionaryEntry e in cdt)
            dictionary[e.Key] = e.Value is SolidColorBrush b ? Brush(b.Color) : e.Value;
        return dictionary;
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
        UseCdtStyles(_styles);
        return _styles;
    }

    /// <summary>
    /// The plain controls and the Spm* button styles take the CDT SDK's styles (CDT.SecondaryButton, CDT.TextBox,
    /// CDT.ComboBox, CDT.DataGrid, …): every state, the focus ring and the validation template come from the SDK, and the
    /// colours from the palette each window carries. A style the SDK doesn't have keeps Sketch ProMax's own.
    /// </summary>
    private static void UseCdtStyles(ResourceDictionary styles)
    {
        if (CdtTheme.Styles is null) return;
        void Base(object key, Type type, string cdt)
        {
            try { if (CdtTheme.Style(cdt) is { } basedOn && basedOn.TargetType.IsAssignableFrom(type)) styles[key] = new Style(type, basedOn); }
            catch (Exception) { /* keep Sketch ProMax's style */ }
        }
        Base("SpmSecondaryButton", typeof(Button), "CDT.SecondaryButton");
        Base("SpmPrimaryButton", typeof(Button), "CDT.PrimaryButton");
        Base("SpmDangerButton", typeof(Button), "CDT.DangerButton");
        Base("SpmGhostButton", typeof(Button), "CDT.GhostButton");
        foreach (var (type, cdt) in new (Type, string)[]
        {
            (typeof(Button), "CDT.SecondaryButton"), (typeof(TextBox), "CDT.TextBox"),
            (typeof(CheckBox), "CDT.CheckBox"), (typeof(RadioButton), "CDT.RadioButton"), (typeof(System.Windows.Controls.Primitives.ToggleButton), "CDT.ToggleButton"),
            (typeof(ComboBox), "CDT.ComboBox"), (typeof(ComboBoxItem), "CDT.ComboBoxItem"), (typeof(ProgressBar), "CDT.ProgressBar"),
            (typeof(Hyperlink), "CDT.Hyperlink"), (typeof(ListBox), "CDT.ListBox"), (typeof(ListBoxItem), "CDT.ListBoxItem"),
            (typeof(ListView), "CDT.ListView"), (typeof(ListViewItem), "CDT.ListViewItem"), (typeof(GridViewColumnHeader), "CDT.GridViewColumnHeader"),
            (typeof(TreeView), "CDT.TreeView"), (typeof(TreeViewItem), "CDT.TreeViewItem"), (typeof(TabControl), "CDT.TabControl"), (typeof(TabItem), "CDT.TabItem"),
            (typeof(DataGrid), "CDT.DataGrid"), (typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), "CDT.DataGridColumnHeader"), (typeof(DataGridRow), "CDT.DataGridRow"),
            (typeof(DataGridCell), "CDT.DataGridCell"), (typeof(ToolTip), "CDT.ToolTip"), (typeof(ContextMenu), "CDT.ContextMenu"), (typeof(MenuItem), "CDT.MenuItem"),
            (typeof(Expander), "CDT.Expander"), (typeof(GroupBox), "CDT.GroupBox"), (typeof(Slider), "CDT.Slider"), (typeof(DatePicker), "CDT.DatePicker"),
            (typeof(GridSplitter), "CDT.GridSplitter"), (typeof(Menu), "CDT.Menu"), (typeof(System.Windows.Controls.Primitives.StatusBar), "CDT.StatusBar"), (typeof(RichTextBox), "CDT.RichTextBox"),
        })
            Base(type, type, cdt);
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
        CdtTheme.Focus(window);
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
        WhileVisible(arc,
            () => spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever }),
            () => spin.BeginAnimation(RotateTransform.AngleProperty, null));
        AutomationProperties.SetName(arc, "Working");
        return arc;
    }

    /// <summary>
    /// Runs a looping animation only while <paramref name="element"/> is on screen: a clock that repeats forever keeps
    /// WPF drawing every frame and stays alive with its element, so one left on a hidden, collapsed or discarded element
    /// (a step's spinner replaced by a check, a closed pane) would never stop.
    /// </summary>
    public static void WhileVisible(UIElement element, Action start, Action stop)
    {
        element.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) start();
            else stop();
        };
        if (element.IsVisible) start();
    }
}
