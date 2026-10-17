using System.Globalization;

namespace SketchProMax.Chat;

/// <summary>
/// The XAML of the chat's control styles (see <see cref="ChatUi.Styles"/>): rounded quiet buttons, icon buttons, pills,
/// cards with a soft shadow, the round Send and Stop buttons, the rounded search field, the composer's bare text box and the disclosure toggle, each
/// with a rounded focus ring. Plain text, so a test checks it is well formed without WPF. Colours are palette tokens only.
/// </summary>
internal static class ChatStyles
{
    private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A button template: a rounded border, and with <paramref name="shadow"/> a soft shadow on a layer behind it (never
    /// on the layer holding the text, which would lose ClearType).
    /// </summary>
    private static string Template(double radius, bool shadow = false, string hoverBg = "SpmSurfaceHover", string? hoverBorder = null)
    {
        var r = N(radius);
        var behind = shadow
            ? "<Border CornerRadius=\"" + r + "\" Background=\"{TemplateBinding Background}\" Margin=\"1\">" +
              "<Border.Effect><DropShadowEffect BlurRadius=\"10\" ShadowDepth=\"1\" Direction=\"270\" Opacity=\"0.08\" Color=\"#000000\"/></Border.Effect></Border>"
            : "";
        var border = hoverBorder is null ? "" : "<Setter TargetName=\"bd\" Property=\"BorderBrush\" Value=\"{DynamicResource " + hoverBorder + "}\"/>";
        return """
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Grid>
                  BEHIND
                  <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="RADIUS" SnapsToDevicePixels="True">
                    <ContentPresenter Margin="{TemplateBinding Padding}" RecognizesAccessKey="True"
                                      HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                  </Border>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="bd" Property="Background" Value="{DynamicResource HOVERBG}"/>
                    HOVERBORDER
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.6"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
            """.Replace("BEHIND", behind).Replace("RADIUS", r).Replace("HOVERBG", hoverBg).Replace("HOVERBORDER", border);
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
    private static string Round(string key, string bg, string hover, string pressed, string fg) => """
        <Style x:Key="KEY" TargetType="Button">
          <Setter Property="FocusVisualStyle" Value="{StaticResource SpmChatFocusRound}"/>
          <Setter Property="Width" Value="32"/>
          <Setter Property="Height" Value="32"/>
          <Setter Property="Padding" Value="0"/>
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Background" Value="{DynamicResource BG}"/>
          <Setter Property="Foreground" Value="{DynamicResource FG}"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="Button">
              <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="16" SnapsToDevicePixels="True">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="{DynamicResource HOVER}"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="bd" Property="Background" Value="{DynamicResource PRESSED}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False">
                  <Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/>
                  <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """.Replace("KEY", key).Replace("BG", bg).Replace("FG", fg).Replace("HOVER", hover).Replace("PRESSED", pressed);

    /// <summary>The whole resource dictionary, with buttons and fields at <paramref name="small"/> corners and cards at <paramref name="card"/>.</summary>
    public static string Xaml(double small, double card) => """
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
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="TextBox">
                <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False" Background="Transparent"
                              HorizontalScrollBarVisibility="{TemplateBinding HorizontalScrollBarVisibility}"
                              VerticalScrollBarVisibility="{TemplateBinding VerticalScrollBarVisibility}"/>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style x:Key="SpmChatDisclosure" TargetType="ToggleButton">
            <Setter Property="FocusVisualStyle" Value="{StaticResource SpmChatFocus}"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
            <Setter Property="MinHeight" Value="32"/>
            <Setter Property="Padding" Value="8,6"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ToggleButton">
                <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="SMALL" SnapsToDevicePixels="True">
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
        """
        .Replace("FOCUS", Focus("SpmChatFocus", small) + Focus("SpmChatFocusCard", card) + Focus("SpmChatFocusRound", 16))
        .Replace("GHOST", Template(small))
        .Replace("PILL", Template(14, hoverBorder: "SpmBorder"))
        .Replace("PRIMARY", Template(14, hoverBg: "SpmAccentHover", hoverBorder: "SpmAccentHover"))
        .Replace("FLOAT", Template(14, shadow: true, hoverBorder: "SpmBorder"))
        .Replace("CARD", Template(card, shadow: true, hoverBg: "SpmRaised", hoverBorder: "SpmBorder"))
        .Replace("SEND", Round("SpmChatSend", "SpmAccent", "SpmAccentHover", "SpmAccentPressed", "SpmOnAccent"))
        .Replace("STOP", Round("SpmChatStop", "SpmDanger", "SpmDangerHover", "SpmDangerPressed", "SpmOnDanger"))
        .Replace("SMALL", N(small));
}
