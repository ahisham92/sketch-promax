using System.Globalization;

namespace SketchProMax.Chat;

/// <summary>
/// The XAML of the chat's control styles (see <see cref="ChatUi.Styles"/>): rounded quiet buttons, icon buttons, pills,
/// cards with a soft shadow, the red pill for a task's Stop, the round Send and Stop buttons, the rounded search field, the composer's bare text box and the disclosure toggle, each
/// with a rounded focus ring, a hover that fades in and a press that shrinks the chrome a little (never the label).
/// Also the chat's type scale. Plain text, so a test checks it is well formed without WPF. Colours are palette tokens only.
/// </summary>
internal static class ChatStyles
{
    // ---- The type scale: every size of text in the chat is one of these (Segoe UI Variable, else Segoe UI) -------------

    /// <summary>Captions: names, times, labels, the status line, code and table cells.</summary>
    public const double Caption = 12;
    /// <summary>Messages, fields and buttons.</summary>
    public const double Body = 13;
    /// <summary>Card titles and level-3 headings.</summary>
    public const double Subtitle = 14;
    /// <summary>Level-1 and level-2 headings in an answer (laid out with Ideal text, being large).</summary>
    public const double Title = 16;
    /// <summary>The home screen's greeting (Ideal text).</summary>
    public const double Display = 20;

    /// <summary>Text from this size up is laid out with Ideal text (its shapes kept); smaller text uses Display (snapped to pixels).</summary>
    public const double IdealFrom = 15;

    // ---- The composer's message box ------------------------------------------------------------------------------------

    /// <summary>The hint in the empty message box: what to do, in plain words.</summary>
    public const string ComposerHint = "Ask anything about your model, or tell me what to do…";

    /// <summary>The box's inner padding: the text and the hint both start here, top-left.</summary>
    public const double ComposerPadX = 10, ComposerPadY = 8;

    /// <summary>About two lines of Body text at rest; it grows upward with the text to the most, then scrolls.</summary>
    public const double ComposerMinHeight = 52, ComposerMaxHeight = 180;

    /// <summary>
    /// The box's border: InputBorder (3:1) at rest so it reads as a field, Text on hover, and a 2 px Focus ring while
    /// the caret is in it.
    /// </summary>
    public static (string Brush, double Thickness) ComposerBorder(bool focused, bool hover) =>
        focused ? ("SpmFocus", 2) : hover ? ("SpmText", 1) : ("SpmInputBorder", 1);

    /// <summary>How long hover, colour, opacity and size changes take (ease-out); 0 when Windows' animations are off.</summary>
    public const int MotionMs = 160;

    private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Duration(bool animate) => animate ? "0:0:0." + MotionMs.ToString("000", CultureInfo.InvariantCulture) : "0:0:0";

    /// <summary>
    /// A fade of the hover layer <c>hv</c> in on hover and out after it, ease-out. The layer holds no text, so the
    /// label keeps ClearType while it fades.
    /// </summary>
    private static string HoverFade(bool animate) => Plain ? """<Setter TargetName="hv" Property="Opacity" Value="1"/>""" : """
        <Trigger.EnterActions><BeginStoryboard><Storyboard>
          <DoubleAnimation Storyboard.TargetName="hv" Storyboard.TargetProperty="Opacity" To="1" Duration="DUR"><DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction></DoubleAnimation>
        </Storyboard></BeginStoryboard></Trigger.EnterActions>
        <Trigger.ExitActions><BeginStoryboard><Storyboard>
          <DoubleAnimation Storyboard.TargetName="hv" Storyboard.TargetProperty="Opacity" To="0" Duration="DUR"><DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction></DoubleAnimation>
        </Storyboard></BeginStoryboard></Trigger.ExitActions>
        """.Replace("DUR", Duration(animate));

    /// <summary>
    /// The chrome of a button, drawn apart from its label: the fill, the hover layer (faded in), the border on top; a
    /// press shrinks the chrome to 98 % (never the label, which stays on whole pixels).
    /// </summary>
    private static string Chrome(string radius, string fill, string hover) => """
        <Grid x:Name="chrome" RenderTransformOrigin="0.5,0.5">
          <Border x:Name="fill" Background="FILL" CornerRadius="RADIUS" SnapsToDevicePixels="True"/>
          <Border x:Name="hv" Background="{DynamicResource HOVERFILL}" CornerRadius="RADIUS" Opacity="0" SnapsToDevicePixels="True"/>
          <Border x:Name="bd" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="RADIUS" SnapsToDevicePixels="True"/>
        </Grid>
        """.Replace("HOVERFILL", hover).Replace("FILL", fill).Replace("RADIUS", radius);

    private static string Press => Plain ? "" : PressXaml;

    private const string PressXaml = """
        <Trigger Property="IsPressed" Value="True">
          <Setter TargetName="chrome" Property="RenderTransform"><Setter.Value><ScaleTransform ScaleX="0.98" ScaleY="0.98"/></Setter.Value></Setter>
        </Trigger>
        """;

    /// <summary>
    /// A button template: a rounded chrome (see <see cref="Chrome"/>), and with <paramref name="shadow"/> a soft shadow
    /// on a layer behind it (never on the layer holding the text, which would lose ClearType).
    /// </summary>
    private static string Template(double radius, bool animate, bool shadow = false, string hoverBg = "SpmSurfaceHover", string? hoverBorder = null)
    {
        var r = N(radius);
        var behind = shadow && !Plain
            ? "<Border CornerRadius=\"" + r + "\" Background=\"{TemplateBinding Background}\" Margin=\"1\">" +
              "<Border.Effect><DropShadowEffect BlurRadius=\"10\" ShadowDepth=\"1\" Direction=\"270\" Opacity=\"0.08\" Color=\"#000000\"/></Border.Effect></Border>"
            : "";
        var border = hoverBorder is null ? "" : "<Setter TargetName=\"bd\" Property=\"BorderBrush\" Value=\"{DynamicResource " + hoverBorder + "}\"/>";
        return """
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Grid>
                  BEHIND
                  CHROME
                  <Border BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                    <ContentPresenter RecognizesAccessKey="True"
                                      HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                  </Border>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    FADE
                    HOVERBORDER
                  </Trigger>
                  PRESS
                  <Trigger Property="IsEnabled" Value="False">
                    <Setter TargetName="fill" Property="Background" Value="{DynamicResource SpmSurface}"/>
                    <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
                    <Setter Property="Cursor" Value="Arrow"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
            """.Replace("BEHIND", behind).Replace("CHROME", Chrome(r, "{TemplateBinding Background}", hoverBg))
            .Replace("FADE", HoverFade(animate)).Replace("PRESS", Press).Replace("HOVERBORDER", border);
    }

    /// <summary>A 2 px focus ring in the Focus colour that follows a control's rounded corners, just outside it.</summary>
    private static string Focus(string key, double radius) => """
        <Style x:Key="KEY">
          <Setter Property="Control.Template"><Setter.Value><ControlTemplate>
            <Rectangle Margin="-2" RadiusX="R" RadiusY="R" StrokeThickness="2" Stroke="{DynamicResource SpmFocus}" SnapsToDevicePixels="True"/>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """.Replace("KEY", key).Replace("R\"", N(radius + 2) + "\"");

    /// <summary>A 32 px round button in one palette colour family (Send in the accent, Stop in the danger red).</summary>
    private static string Round(string key, string bg, string hover, string pressed, string fg, bool animate) => """
        <Style x:Key="KEY" TargetType="Button">
          <Setter Property="FocusVisualStyle" Value="{StaticResource SpmChatFocusRound}"/>
          <Setter Property="Width" Value="32"/>
          <Setter Property="Height" Value="32"/>
          <Setter Property="Padding" Value="0"/>
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Background" Value="{DynamicResource BG}"/>
          <Setter Property="Foreground" Value="{DynamicResource FG}"/>
          <Setter Property="BorderThickness" Value="0"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="Button">
              <Grid>
                CHROME
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">FADE</Trigger>
                <Trigger Property="IsPressed" Value="True">
                  <Setter TargetName="fill" Property="Background" Value="{DynamicResource PRESSED}"/>
                  <Setter TargetName="hv" Property="Visibility" Value="Hidden"/>
                  PRESSROUND
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                  <Setter TargetName="fill" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/>
                  <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """.Replace("CHROME", Chrome("16", "{TemplateBinding Background}", hover)).Replace("FADE", HoverFade(animate))
        .Replace("PRESSROUND", Plain ? "" : "<Setter TargetName=\"chrome\" Property=\"RenderTransform\"><Setter.Value><ScaleTransform ScaleX=\"0.96\" ScaleY=\"0.96\"/></Setter.Value></Setter>")
        .Replace("KEY", key).Replace("BG", bg).Replace("FG", fg).Replace("PRESSED", pressed);

    /// <summary>
    /// The whole resource dictionary, with buttons and fields at <paramref name="small"/> corners and cards at
    /// <paramref name="card"/>; hover fades take <see cref="MotionMs"/> when <paramref name="animate"/>, else none.
    /// </summary>
    public static string Xaml(double small, double card, bool animate = true, bool plain = false)
    {
        Plain = plain;
        try { return Build(small, card, animate); }
        finally { Plain = false; }
    }

    /// <summary>
    /// The fallback look: the same rounded shapes and colours with no motion, press scaling or shadows, in case a
    /// Windows or WPF version refuses one of those (the chat then still looks like itself, never like plain Windows buttons).
    /// </summary>
    [ThreadStatic] private static bool Plain;

    private static string Build(double small, double card, bool animate) => """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          FOCUS
          <Style x:Key="SpmChatGhost" TargetType="Button">
            <Setter Property="FocusVisualStyle" Value="{StaticResource SpmChatFocus}"/>
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
            GHOST
            <Style.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{DynamicResource SpmText}"/></Trigger>
              <Trigger Property="IsPressed" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource SpmBorder}"/></Trigger>
            </Style.Triggers>
          </Style>
          <Style x:Key="SpmChatIcon" TargetType="Button" BasedOn="{StaticResource SpmChatGhost}">
            <Setter Property="MinWidth" Value="32"/>
            <Setter Property="MinHeight" Value="32"/>
            <Setter Property="Padding" Value="0"/>
          </Style>
          <Style x:Key="SpmChatPill" TargetType="Button" BasedOn="{StaticResource SpmChatGhost}">
            <Setter Property="FocusVisualStyle" Value="{StaticResource SpmChatFocusRound}"/>
            <Setter Property="Background" Value="{DynamicResource SpmRaised}"/>
            <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource SpmBorderSubtle}"/>
            <Setter Property="Padding" Value="12,4"/>
            <Setter Property="MinHeight" Value="28"/>
            PILL
          </Style>
          <Style x:Key="SpmChatPrimary" TargetType="Button" BasedOn="{StaticResource SpmChatPill}">
            <Setter Property="Background" Value="{DynamicResource SpmAccent}"/>
            <Setter Property="Foreground" Value="{DynamicResource SpmOnAccent}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource SpmAccent}"/>
            PRIMARY
            <Style.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{DynamicResource SpmOnAccent}"/></Trigger>
            </Style.Triggers>
          </Style>
          <Style x:Key="SpmChatDanger" TargetType="Button" BasedOn="{StaticResource SpmChatPill}">
            <Setter Property="Background" Value="{DynamicResource SpmDanger}"/>
            <Setter Property="Foreground" Value="{DynamicResource SpmOnDanger}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource SpmDanger}"/>
            DANGERTPL
            <Style.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{DynamicResource SpmOnDanger}"/></Trigger>
            </Style.Triggers>
          </Style>
          <Style x:Key="SpmChatFloat" TargetType="Button" BasedOn="{StaticResource SpmChatPill}">
            FLOAT
          </Style>
          <Style x:Key="SpmChatCard" TargetType="Button">
            <Setter Property="FocusVisualStyle" Value="{StaticResource SpmChatFocusCard}"/>
            <Setter Property="Background" Value="{DynamicResource SpmRaised}"/>
            <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource SpmBorderSubtle}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="14,12"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            CARD
          </Style>
          <Style x:Key="SpmChatMenuItem" TargetType="Button" BasedOn="{StaticResource SpmChatGhost}">
            <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Padding" Value="10,6"/>
            <Setter Property="MinHeight" Value="32"/>
          </Style>
          SEND
          STOP
          <Style x:Key="SpmChatField" TargetType="TextBox">
            <Setter Property="Background" Value="{DynamicResource SpmField}"/>
            <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource SpmInputBorder}"/>
            <Setter Property="CaretBrush" Value="{DynamicResource SpmText}"/>
            <Setter Property="SelectionBrush" Value="{DynamicResource SpmAccent}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="10,6"/>
            <Setter Property="MinHeight" Value="34"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="TextBox">
                <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="SMALL" SnapsToDevicePixels="True">
                  <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"
                                HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmAccent}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True">
                    <Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/>
                    <Setter TargetName="bd" Property="BorderThickness" Value="2"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style x:Key="SpmChatBare" TargetType="TextBox">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
            <Setter Property="CaretBrush" Value="{DynamicResource SpmText}"/>
            <Setter Property="SelectionBrush" Value="{DynamicResource SpmAccent}"/>
            <Setter Property="SelectionOpacity" Value="0.35"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="VerticalContentAlignment" Value="Top"/>
            <Setter Property="Cursor" Value="IBeam"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="TextBox">
                <!-- The whole box takes the click, its padding too; the first line sits at the top, where the hint is. -->
                <Border x:Name="box" Background="Transparent">
                  <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False" Background="Transparent"
                                VerticalAlignment="Stretch" VerticalContentAlignment="Top"
                                HorizontalScrollBarVisibility="{TemplateBinding HorizontalScrollBarVisibility}"
                                VerticalScrollBarVisibility="{TemplateBinding VerticalScrollBarVisibility}">
                    <ScrollViewer.Template>
                      <ControlTemplate TargetType="ScrollViewer">
                        <Grid Background="{TemplateBinding Background}">
                          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                          <ScrollContentPresenter x:Name="PART_ScrollContentPresenter"
                                                  CanContentScroll="{TemplateBinding CanContentScroll}"/>
                          <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Orientation="Vertical"
                                     Value="{Binding VerticalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}"
                                     Maximum="{TemplateBinding ScrollableHeight}" ViewportSize="{TemplateBinding ViewportHeight}"
                                     Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
                        </Grid>
                      </ControlTemplate>
                    </ScrollViewer.Template>
                  </ScrollViewer>
                </Border>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style x:Key="SpmChatDisclosure" TargetType="ToggleButton">
            <Setter Property="FocusVisualStyle" Value="{StaticResource SpmChatFocus}"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
            <Setter Property="MinHeight" Value="32"/>
            <Setter Property="Padding" Value="8,6"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ToggleButton">
                <Grid>
                  DISCLOSURECHROME
                  <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                    VerticalAlignment="Center"/>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">DISCLOSUREFADE</Trigger>
                  DISCLOSUREPRESS
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """
        .Replace("FOCUS", Focus("SpmChatFocus", small) + Focus("SpmChatFocusCard", card) + Focus("SpmChatFocusRound", 16))
        .Replace("DISCLOSURECHROME", Chrome("SMALL", "{TemplateBinding Background}", "SpmSurfaceHover"))
        .Replace("DISCLOSUREFADE", HoverFade(animate))
        .Replace("DISCLOSUREPRESS", Press)
        .Replace("GHOST", Template(small, animate))
        .Replace("PILL", Template(14, animate, hoverBorder: "SpmBorder"))
        .Replace("PRIMARY", Template(14, animate, hoverBg: "SpmAccentHover", hoverBorder: "SpmAccentHover"))
        .Replace("FLOAT", Template(14, animate, shadow: true, hoverBorder: "SpmBorder"))
        .Replace("DANGERTPL", Template(14, animate, hoverBg: "SpmDangerHover", hoverBorder: "SpmDangerHover"))
        .Replace("CARD", Template(card, animate, shadow: true, hoverBg: "SpmSurface", hoverBorder: "SpmBorder"))
        .Replace("SEND", Round("SpmChatSend", "SpmAccent", "SpmAccentHover", "SpmAccentPressed", "SpmOnAccent", animate))
        .Replace("STOP", Round("SpmChatStop", "SpmDanger", "SpmDangerHover", "SpmDangerPressed", "SpmOnDanger", animate))
        .Replace("SMALL", N(small));
}
