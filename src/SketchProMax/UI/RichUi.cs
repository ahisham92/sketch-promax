using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SketchProMax.Chat;
using Button = System.Windows.Controls.Button;

namespace SketchProMax.UI;

/// <summary>
/// The richer look used by the pre-submission checklist and its editor: buttons with an icon, rounded corners, a soft
/// shadow and a lift on hover; score rings that sweep to their value; pages that slide in; answers that pop. Colours are
/// palette tokens, so both Revit themes work. How much moves follows Settings > Chat animations (Full, Reduced, Off):
/// Off shows every change at once, Reduced keeps only the quick fades.
/// </summary>
internal static class RichUi
{
    /// <summary>Segoe icon glyphs (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10).</summary>
    public static class Icon
    {
        public const string Next = "", Back = "", Close = "", Word = "", Pdf = "", Print = "", Folder = "",
            Open = "", Delete = "", Edit = "", Refresh = "", Eye = "", Undo = "", Add = "", Up = "",
            Down = "", Save = "", Import = "", Export = "", Picture = "", Reset = "";
        /// <summary>Repair, dock right, full screen, chat bubbles, gear.</summary>
        public const string Fix = "\uE90F", Side = "\uE90D", Expand = "\uE740", Ask = "\uE8F2", Settings = "\uE713";
    }

    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static Motion MotionOf(Settings.AgentSettings settings) => MotionLevels.Parse(settings.ChatAnimations);

    /// <summary>Adds the rich button styles to <paramref name="root"/>'s resources (no hover lift when motion is Off).</summary>
    public static void Apply(FrameworkElement root, Motion motion)
    {
        var lift = motion != Chat.Motion.Off;
        string Style(string key, string bg, string hover, string fg, string border, double shine)
        {
            var anim = lift ? """
                        <Trigger.EnterActions><BeginStoryboard><Storyboard>
                          <DoubleAnimation Storyboard.TargetName="b" Storyboard.TargetProperty="(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)" To="-1.5" Duration="0:0:0.12"/>
                        </Storyboard></BeginStoryboard></Trigger.EnterActions>
                        <Trigger.ExitActions><BeginStoryboard><Storyboard>
                          <DoubleAnimation Storyboard.TargetName="b" Storyboard.TargetProperty="(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)" To="0" Duration="0:0:0.15"/>
                        </Storyboard></BeginStoryboard></Trigger.ExitActions>
                """ : "";
            var press = lift ? """
                        <Trigger.EnterActions><BeginStoryboard><Storyboard>
                          <DoubleAnimation Storyboard.TargetName="b" Storyboard.TargetProperty="(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)" To="0.97" Duration="0:0:0.06"/>
                          <DoubleAnimation Storyboard.TargetName="b" Storyboard.TargetProperty="(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleY)" To="0.97" Duration="0:0:0.06"/>
                        </Storyboard></BeginStoryboard></Trigger.EnterActions>
                        <Trigger.ExitActions><BeginStoryboard><Storyboard>
                          <DoubleAnimation Storyboard.TargetName="b" Storyboard.TargetProperty="(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)" To="1" Duration="0:0:0.12"/>
                          <DoubleAnimation Storyboard.TargetName="b" Storyboard.TargetProperty="(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleY)" To="1" Duration="0:0:0.12"/>
                        </Storyboard></BeginStoryboard></Trigger.ExitActions>
                """ : "";
            var shineValue = shine.ToString(CultureInfo.InvariantCulture);
            return $$"""
                  <Style x:Key="{{key}}" TargetType="Button">
                    <Setter Property="Foreground" Value="{DynamicResource {{fg}}}"/>
                    <Setter Property="Cursor" Value="Hand"/>
                    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                    <Setter Property="MinHeight" Value="34"/>
                    <Setter Property="Padding" Value="14,6"/>
                    <Setter Property="FontWeight" Value="SemiBold"/>
                    <Setter Property="Template"><Setter.Value>
                      <ControlTemplate TargetType="Button">
                        <Border x:Name="b" CornerRadius="8" BorderThickness="1" BorderBrush="{DynamicResource {{border}}}" Background="{DynamicResource {{bg}}}"
                                RenderTransformOrigin="0.5,0.5" SnapsToDevicePixels="True">
                          <Border.RenderTransform><TransformGroup><ScaleTransform ScaleX="1" ScaleY="1"/><TranslateTransform Y="0"/></TransformGroup></Border.RenderTransform>
                          <Border.Effect><DropShadowEffect BlurRadius="8" ShadowDepth="1" Direction="270" Opacity="0.22"/></Border.Effect>
                          <Grid>
                            <Border CornerRadius="7" Opacity="{{shineValue}}" IsHitTestVisible="False">
                              <Border.Background>
                                <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                                  <GradientStop Color="#FFFFFFFF" Offset="0"/>
                                  <GradientStop Color="#00FFFFFF" Offset="0.65"/>
                                </LinearGradientBrush>
                              </Border.Background>
                            </Border>
                            <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                          </Grid>
                        </Border>
                        <ControlTemplate.Triggers>
                          <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="b" Property="Background" Value="{DynamicResource {{hover}}}"/>
                            {{anim}}
                          </Trigger>
                          <Trigger Property="IsPressed" Value="True">
                            {{press}}
                          </Trigger>
                          <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/>
                            <Setter TargetName="b" Property="BorderThickness" Value="2"/>
                          </Trigger>
                          <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="b" Property="Opacity" Value="0.45"/>
                          </Trigger>
                        </ControlTemplate.Triggers>
                      </ControlTemplate>
                    </Setter.Value></Setter>
                  </Style>
                """;
        }
        var xaml = $$"""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              {{Style("SpmRichPrimary", Brand.K.Accent, Brand.K.AccentHover, Brand.K.OnAccent, Brand.K.AccentPressed, 0.22)}}
              {{Style("SpmRichSecondary", Brand.K.Surface, Brand.K.SurfaceHover, Brand.K.Text, Brand.K.InputBorder, 0.06)}}
              {{Style("SpmRichDanger", Brand.K.Danger, Brand.K.DangerHover, Brand.K.OnDanger, Brand.K.DangerPressed, 0.18)}}
            </ResourceDictionary>
            """;
        try { root.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml)); }
        catch (Exception) { /* the buttons fall back to the plain Sketch ProMax buttons */ }
    }

    /// <summary>A button with an icon and its text (text may be empty for an icon-only button: give it a name).</summary>
    public static Button Button(string glyph, string text, Brand.Weight weight = Brand.Weight.Secondary)
    {
        var button = Brand.Button(text, weight);
        button.SetResourceReference(FrameworkElement.StyleProperty, weight switch
        {
            Brand.Weight.Primary => "SpmRichPrimary",
            Brand.Weight.Danger => "SpmRichDanger",
            _ => "SpmRichSecondary",
        });
        Set(button, glyph, text);
        return button;
    }

    /// <summary>Changes a rich button's icon and text.</summary>
    public static void Set(Button button, string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
        if (text.Length > 0)
            row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        button.Content = row;
        if (text.Length > 0) AutomationProperties.SetName(button, text);
    }

    // ---- Motion ---------------------------------------------------------------------------------------------------

    private static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fades an element in while it slides into place (a new page, a result appearing).</summary>
    public static void FadeIn(UIElement element, Motion motion, double fromX = 0, double fromY = 8, int ms = 220)
    {
        if (motion == Chat.Motion.Off) { element.Opacity = 1; return; }
        var move = new TranslateTransform(motion == Chat.Motion.Full ? fromX : 0, motion == Chat.Motion.Full ? fromY : 0);
        element.RenderTransform = move;
        var time = TimeSpan.FromMilliseconds(motion == Chat.Motion.Full ? ms : 120);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = Out });
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(move.X, 0, time) { EasingFunction = Out });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(move.Y, 0, time) { EasingFunction = Out });
    }

    /// <summary>A small pop: the element grows a little and settles (an answer chosen, a stage done). Full motion only.</summary>
    public static void Pop(FrameworkElement element, Motion motion, double from = 0.85)
    {
        if (motion != Chat.Motion.Full) return;
        var scale = new ScaleTransform(from, from);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    /// <summary>Moves a progress bar to <paramref name="value"/> smoothly (at once when motion is Off).</summary>
    public static void To(System.Windows.Controls.Primitives.RangeBase bar, double value, Motion motion)
    {
        if (motion == Chat.Motion.Off) { bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null); bar.Value = value; return; }
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new DoubleAnimation(value, TimeSpan.FromMilliseconds(motion == Chat.Motion.Full ? 350 : 150)) { EasingFunction = Out });
    }
}

/// <summary>
/// A percentage ring with the number in the middle and a label under it, drawn for the blue header band (the same in
/// both themes). The ring sweeps to a new value; null shows a dash.
/// </summary>
internal sealed class ScoreRing : StackPanel
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(ScoreRing), new PropertyMetadata(0.0, (d, _) => ((ScoreRing)d).Draw()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private const double Size = 46, Thick = 5;
    private readonly Path _arc = new() { StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly TextBlock _number = new() { FontSize = 12.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
    private int? _target;
    private readonly Motion _motion;

    public ScoreRing(string label, Motion motion)
    {
        _motion = motion;
        Margin = new Thickness(14, 0, 0, 0);
        var ring = new Grid { Width = Size, Height = Size, HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)), StrokeThickness = Thick, Margin = new Thickness(Thick / 2) });
        ring.Children.Add(_arc);
        ring.Children.Add(_number);
        Children.Add(ring);
        Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });
        AutomationProperties.SetName(this, label);
        // Clickable (and Enter / Space with the keyboard) when someone listens: the checklist opens the score's breakdown.
        Background = Brushes.Transparent;
        MouseLeftButtonUp += (_, e) => { if (Clicked is { } go) { go(); e.Handled = true; } };
        KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space && Clicked is { } go) { go(); e.Handled = true; } };
        Draw();
    }

    private Action? _clicked;

    /// <summary>Runs when the ring is clicked; setting it makes the ring look and act like a button.</summary>
    public Action? Clicked
    {
        get => _clicked;
        set
        {
            _clicked = value;
            Cursor = value is null ? null : Cursors.Hand;
            Focusable = value is not null;
        }
    }

    /// <summary>Sets the percentage (null: unknown) and its colour; the arc sweeps there and the number counts up.</summary>
    public void Show(int? percent, Color color, string tip)
    {
        _arc.Stroke = new SolidColorBrush(color);
        ToolTip = tip;
        AutomationProperties.SetHelpText(this, tip);
        AutomationProperties.SetItemStatus(this, percent is null ? "not known" : percent + "%");
        _target = percent;
        var to = percent ?? 0;
        if (_motion == Motion.Off) { BeginAnimation(ValueProperty, null); Value = to; return; }
        BeginAnimation(ValueProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(_motion == Motion.Full ? 650 : 200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Draw()
    {
        var v = Math.Max(0, Math.Min(100, Value));
        _number.Text = _target is null ? "–" : Math.Round(v).ToString(CultureInfo.CurrentCulture) + "%";
        var r = (Size - Thick) / 2;
        var c = Size / 2;
        if (v <= 0.05) { _arc.Data = null; return; }
        if (v >= 99.95) { _arc.Data = new EllipseGeometry(new Point(c, c), r, r); return; }
        var angle = v / 100 * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, v > 50, SweepDirection.Clockwise, true));
        _arc.Data = new PathGeometry([figure]);
    }
}
