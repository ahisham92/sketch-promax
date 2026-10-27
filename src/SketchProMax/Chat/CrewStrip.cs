using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The small strip over the large chat With coordinator: the coordinator at a desk on the left and, for each job at
/// work, a worker at a computer (CrewPlan says who sits where), and on the right the one Revit station. A new job's
/// worker gets a sheet from the coordinator and sits down; a message to a job is a sheet handed over. Workers think and
/// read the model at their own desks all at once (typing; a magnifier while reading), but changes go to the model one at a time: the worker whose call Revit
/// runs sends its sheet to the Revit station, which lights up with its job's number, while the others wait with a
/// clock. A finished worker's screen turns to a tick before the worker leaves. Your message makes the coordinator nod.
///
/// Cheap on purpose: shapes, no images, no layout work while it moves; the typing is a slow timer (about 6 a second)
/// that runs only while the strip is on screen, the chat's animations are Full and someone is working, and the one-off
/// moves ask for 30 frames a second. At Reduced only the arrivals and leavings move; at Off everything jumps.
/// </summary>
internal sealed class CrewStrip : Border
{
    public const double StripHeight = 72;
    private const double CoordX = 34, FirstSeat = 128, MaxGap = 104, MinGap = 64, FigWidth = 64, StationWidth = 84;
    private static readonly Duration Move = new(TimeSpan.FromMilliseconds(450)), Fly = new(TimeSpan.FromMilliseconds(800));

    // Decorative colours that read on both themes: the people, and the screens (always dark, like a monitor).
    private static readonly Brush Skin = Brand.Brush(Color.FromRgb(0xE3, 0xBC, 0x96));
    private static readonly Brush Hair = Brand.Brush(Color.FromRgb(0x3B, 0x3B, 0x45));
    private static readonly Brush Screen = Brand.Brush(Color.FromRgb(0x1E, 0x29, 0x3B));
    private static readonly Brush ScreenLine = Brand.Brush(Color.FromRgb(0x93, 0xB4, 0xFF));
    private static readonly Brush ScreenOk = Brand.Brush(Color.FromRgb(0x6E, 0xDC, 0x9A));
    private static readonly Brush ScreenBad = Brand.Brush(Color.FromRgb(0xFF, 0x9A, 0x92));
    private static readonly Brush[] Shirts =
    [
        Brand.Brush(Color.FromRgb(0x3E, 0x9C, 0x7A)), Brand.Brush(Color.FromRgb(0xC0, 0x7A, 0x45)),
        Brand.Brush(Color.FromRgb(0x8A, 0x6B, 0xC7)), Brand.Brush(Color.FromRgb(0x4F, 0x8F, 0xB8)),
        Brand.Brush(Color.FromRgb(0xB8, 0x5C, 0x7A)), Brand.Brush(Color.FromRgb(0x7A, 0x8C, 0x3E)),
    ];

    private sealed class Worker
    {
        public int Number;
        public Canvas Fig = null!;
        public TranslateTransform Place = null!;
        public RotateTransform Arm = null!;
        public readonly List<(Rectangle Line, ScaleTransform Scale)> Lines = [];
        public TextBlock Clock = null!, Look = null!, Mark = null!;
        public CrewMood Mood;
        public bool Leaving, Fresh;
    }

    private readonly Canvas _stage = new() { ClipToBounds = true, Height = StripHeight };
    private readonly TextBlock _caption = new() { FontSize = ChatUi.SmallSize, MaxWidth = 240, VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(ChatUi.S2, 0, ChatUi.S3, ChatUi.S2) };
    private readonly Dictionary<int, Worker> _workers = new();
    private readonly RotateTransform _coordArm = new(0);
    private readonly TranslateTransform _coordHead = new();
    // The Revit station: a model on a stand, lit while a job's call runs in Revit.
    private readonly Canvas _station = new() { Width = StationWidth, Height = StripHeight };
    private readonly Path _modelTop = new(), _modelLeft = new(), _modelRight = new();
    private readonly TextBlock _stationLabel = Brand.Text("Revit", Brand.K.Muted, 10);
    private int? _applying;
    private readonly DispatcherTimer _typing;
    private readonly Random _random = new(7);
    private int _tick;

    public CrewStrip()
    {
        Height = StripHeight;
        BorderThickness = new Thickness(0, 0, 0, 1);
        SetResourceReference(BorderBrushProperty, Brand.K.BorderSubtle);
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        Brand.Fg(_caption, Brand.K.Muted);
        AutomationProperties.SetLiveSetting(_caption, AutomationLiveSetting.Polite);
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_caption, Dock.Right);
        dock.Children.Add(_caption);
        dock.Children.Add(_stage);
        Child = dock;
        AutomationProperties.SetName(this, "Who is working: the coordinator and its jobs");
        Focusable = false;
        _stage.IsHitTestVisible = false; // to look at only: the jobs column does the picking

        _stage.Children.Add(Coordinator());
        BuildStation();
        _stage.Children.Add(_station);
        _caption.Text = CrewPlan.Caption([]);

        _typing = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(170) };
        _typing.Tick += (_, _) => Type();
        IsVisibleChanged += (_, _) => Pace();
        _stage.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            Canvas.SetLeft(_station, Math.Max(FirstSeat + FigWidth, _stage.ActualWidth - StationWidth));
            Arrange(animate: false);
        };
    }

    /// <summary>Shows the crew as the plan has it now and acts out what just happened.</summary>
    public void Show(IReadOnlyList<CrewSeat> seats, IReadOnlyList<CrewEvent> events)
    {
        foreach (var gone in events.Where(e => e.Cue == CrewCue.Leave)) Leave(gone.Number);
        foreach (var seat in seats)
        {
            if (!_workers.TryGetValue(seat.Number, out var worker) || worker.Leaving)
            {
                if (worker is not null) Remove(worker);
                worker = Add(seat.Number);
            }
            SetMood(worker, seat.Mood);
            AutomationProperties.SetName(worker.Fig, $"Job {seat.Number}: {seat.Title}, {Words(seat.Mood)}");
        }
        // Anyone still seated whom the plan no longer has (a chat switched over): out at once.
        foreach (var stale in _workers.Values.Where(w => !w.Leaving && seats.All(s => s.Number != w.Number)).ToList()) Leave(stale.Number);
        Arrange(animate: true);
        foreach (var e in events)
        {
            if (e.Cue == CrewCue.Arrive && _workers.TryGetValue(e.Number, out var arriving)) Arrive(arriving);
            else if (e.Cue == CrewCue.Handover && _workers.TryGetValue(e.Number, out var given)) HandTo(given);
        }
        Apply(seats.FirstOrDefault(s => s.Mood == CrewMood.Applying)?.Number);
        _caption.Text = CrewPlan.Caption(seats);
        _caption.ToolTip = string.Join("\n", seats.Select(s => $"Job {s.Number} · {s.Title}: {Words(s.Mood)}"));
        Pace();
    }

    /// <summary>You sent the coordinator a message: a small nod.</summary>
    public void Heard()
    {
        if (!ChatUi.Lively || !IsVisible) return;
        var nod = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(900)) };
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(2.5, KeyTime.FromPercent(0.25)));
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(2.5, KeyTime.FromPercent(0.75)));
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Timeline.SetDesiredFrameRate(nod, 30);
        _coordHead.BeginAnimation(TranslateTransform.YProperty, nod);
    }

    /// <summary>Everyone leaves at once (another chat is shown): nothing is acted out.</summary>
    public void Clear()
    {
        foreach (var worker in _workers.Values.ToList()) Remove(worker);
        Apply(null);
        _caption.Text = CrewPlan.Caption([]);
        _caption.ToolTip = null;
        Pace();
    }

    private static string Words(CrewMood mood) => mood switch
    {
        CrewMood.Applying => "applying its change in Revit",
        CrewMood.Waiting => "waiting to apply its change",
        CrewMood.Reading => "reading the model",
        CrewMood.Done => "done",
        CrewMood.Failed => "ended with a problem",
        _ => "thinking",
    };

    // ---- Moving -----------------------------------------------------------------------------------------------------

    /// <summary>The typing runs only while it can be seen, at Full, with someone working.</summary>
    private void Pace()
    {
        var run = IsVisible && ChatUi.Lively && _workers.Values.Any(w => !w.Leaving && w.Mood is CrewMood.Thinking or CrewMood.Reading);
        if (run && !_typing.IsEnabled) _typing.Start();
        else if (!run && _typing.IsEnabled)
        {
            _typing.Stop();
            foreach (var w in _workers.Values) Rest(w);
        }
    }

    /// <summary>One tick of typing: the hand taps and the screen's lines grow, a little differently for each worker.</summary>
    private void Type()
    {
        if (!IsVisible || !ChatUi.Lively)
        {
            Pace();
            return;
        }
        _tick++;
        foreach (var w in _workers.Values)
        {
            if (w.Leaving || w.Mood is not (CrewMood.Thinking or CrewMood.Reading)) continue;
            w.Arm.Angle = (_tick + w.Number) % 2 == 0 ? -7 : 2;
            var line = w.Lines[(_tick / 3 + w.Number) % w.Lines.Count];
            line.Scale.ScaleX = line.Scale.ScaleX >= 1 ? 0.15 : Math.Min(1, line.Scale.ScaleX + 0.2 + _random.NextDouble() * 0.2);
        }
    }

    private static void Rest(Worker w)
    {
        w.Arm.Angle = 0;
        foreach (var (_, scale) in w.Lines) scale.ScaleX = 1;
    }

    /// <summary>Where each seat goes: after the coordinator, closer together when there are many or the window is narrow.</summary>
    private void Arrange(bool animate)
    {
        var seated = _workers.Values.Where(w => !w.Leaving).OrderBy(w => w.Number).ToList();
        var room = Math.Max(0, _stage.ActualWidth - StationWidth - FirstSeat - FigWidth);
        var gap = seated.Count > 1 ? Math.Max(MinGap, Math.Min(MaxGap, room / (seated.Count - 1))) : MaxGap;
        foreach (var (w, i) in seated.Select((w, i) => (w, i)))
        {
            // A worker just added takes its seat straight away; the others slide along to make room.
            To(w.Place, TranslateTransform.XProperty, FirstSeat + i * gap, animate && !w.Fresh && ChatUi.Animate);
            w.Fresh = false;
        }
    }

    private Worker Add(int number)
    {
        var w = Figure(number);
        w.Fresh = true;
        _workers[number] = w;
        _stage.Children.Insert(_stage.Children.Count - 1, w.Fig); // under the flying sheet
        return w;
    }

    /// <summary>A new worker: the coordinator hands over a sheet, then the worker fades in, settling into the chair.</summary>
    private void Arrive(Worker w)
    {
        if (!ChatUi.Animate)
        {
            w.Fig.Opacity = 1;
            return;
        }
        w.Fig.Opacity = 0;
        var settle = (TranslateTransform)((TransformGroup)w.Fig.RenderTransform).Children[1];
        settle.Y = 8;
        var delay = ChatUi.Lively ? Fly.TimeSpan : TimeSpan.Zero;
        if (ChatUi.Lively) HandTo(w);
        var fade = new DoubleAnimation(0, 1, Move) { BeginTime = delay };
        var sit = new DoubleAnimation(8, 0, Move) { BeginTime = delay, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(fade, 30);
        Timeline.SetDesiredFrameRate(sit, 30);
        w.Fig.BeginAnimation(OpacityProperty, fade);
        settle.BeginAnimation(TranslateTransform.YProperty, sit);
    }

    /// <summary>The coordinator raises an arm and a sheet flies over to the worker's desk.</summary>
    private void HandTo(Worker w)
    {
        if (!ChatUi.Lively || !IsVisible) return;
        var arm = new DoubleAnimationUsingKeyFrames { Duration = Fly };
        arm.KeyFrames.Add(new EasingDoubleKeyFrame(-55, KeyTime.FromPercent(0.2)));
        arm.KeyFrames.Add(new EasingDoubleKeyFrame(-55, KeyTime.FromPercent(0.6)));
        arm.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Timeline.SetDesiredFrameRate(arm, 30);
        _coordArm.BeginAnimation(RotateTransform.AngleProperty, arm);
        Sheet(CoordX + 14, w.Place.X + 30);
    }

    /// <summary>A sheet flies in an arc from one x to another and is gone when it lands.</summary>
    private void Sheet(double from, double to)
    {
        var sheet = new Rectangle { Width = 9, Height = 11, RadiusX = 1, RadiusY = 1, StrokeThickness = 1, IsHitTestVisible = false };
        sheet.SetResourceReference(Shape.FillProperty, Brand.K.Raised);
        sheet.SetResourceReference(Shape.StrokeProperty, Brand.K.Accent);
        var at = new TranslateTransform();
        sheet.RenderTransform = at;
        Canvas.SetLeft(sheet, from);
        Canvas.SetTop(sheet, 34);
        _stage.Children.Add(sheet);
        var x = new DoubleAnimation(0, to - from, Fly) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        var y = new DoubleAnimationUsingKeyFrames { Duration = Fly };
        y.KeyFrames.Add(new EasingDoubleKeyFrame(-14, KeyTime.FromPercent(0.5), new SineEase { EasingMode = EasingMode.EaseOut }));
        y.KeyFrames.Add(new EasingDoubleKeyFrame(4, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseIn }));
        var fade = new DoubleAnimationUsingKeyFrames { Duration = Fly };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.85)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        foreach (var t in new Timeline[] { x, y, fade }) Timeline.SetDesiredFrameRate(t, 30);
        fade.Completed += (_, _) => _stage.Children.Remove(sheet);
        at.BeginAnimation(TranslateTransform.XProperty, x);
        at.BeginAnimation(TranslateTransform.YProperty, y);
        sheet.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Job <paramref name="number"/>'s call now runs in Revit (null: none): its sheet goes to the station, which lights up
    /// with the job's number. Only one at a time, as Revit takes them.
    /// </summary>
    private void Apply(int? number)
    {
        if (number == _applying) return;
        _applying = number;
        var lit = number is not null;
        foreach (var face in new[] { _modelTop, _modelLeft, _modelRight }) face.BeginAnimation(OpacityProperty, null);
        _modelTop.SetResourceReference(Shape.FillProperty, lit ? Brand.K.Accent : Brand.K.Surface);
        _modelLeft.SetResourceReference(Shape.FillProperty, lit ? Brand.K.AccentHover : Brand.K.SurfaceHover);
        _modelRight.SetResourceReference(Shape.FillProperty, lit ? Brand.K.AccentPressed : Brand.K.Border);
        _stationLabel.Text = lit ? $"Revit · Job {number}" : "Revit";
        _stationLabel.SetResourceReference(TextBlock.ForegroundProperty, lit ? Brand.K.Text : Brand.K.Muted);
        if (!lit || !_workers.TryGetValue(number!.Value, out var w)) return;
        if (ChatUi.Lively && IsVisible)
        {
            Sheet(w.Place.X + 30, Canvas.GetLeft(_station) + StationWidth / 2 - 4);
            // A slow, soft glow while the change runs (stops by itself; at most a few seconds of frames).
            var glow = new DoubleAnimation(1, 0.7, new Duration(TimeSpan.FromMilliseconds(700))) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
            Timeline.SetDesiredFrameRate(glow, 20);
            _modelTop.BeginAnimation(OpacityProperty, glow);
        }
    }

    private void Leave(int number)
    {
        if (!_workers.TryGetValue(number, out var w) || w.Leaving) return;
        w.Leaving = true;
        if (!ChatUi.Animate || !IsVisible)
        {
            Remove(w);
            return;
        }
        var fade = new DoubleAnimation(0, Move);
        Timeline.SetDesiredFrameRate(fade, 30);
        fade.Completed += (_, _) =>
        {
            if (w.Leaving) Remove(w);
        };
        w.Fig.BeginAnimation(OpacityProperty, fade);
    }

    private void Remove(Worker w)
    {
        w.Fig.BeginAnimation(OpacityProperty, null);
        _stage.Children.Remove(w.Fig);
        if (_workers.TryGetValue(w.Number, out var now) && now == w) _workers.Remove(w.Number);
    }

    private void SetMood(Worker w, CrewMood mood)
    {
        if (w.Mood == mood && w.Fig.Tag is not null) return;
        w.Mood = mood;
        w.Fig.Tag = mood;
        var finished = mood is CrewMood.Done or CrewMood.Failed;
        foreach (var (line, _) in w.Lines) line.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
        w.Mark.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        w.Mark.Text = mood == CrewMood.Failed ? ChatUi.GlyphError : ChatUi.GlyphCheck;
        w.Mark.Foreground = mood == CrewMood.Failed ? ScreenBad : ScreenOk;
        w.Clock.Visibility = mood == CrewMood.Waiting ? Visibility.Visible : Visibility.Collapsed;
        w.Look.Visibility = mood == CrewMood.Reading ? Visibility.Visible : Visibility.Collapsed;
        if (mood is not (CrewMood.Thinking or CrewMood.Reading)) Rest(w);
    }

    private static void To(TranslateTransform t, DependencyProperty property, double value, bool animate)
    {
        if (!animate)
        {
            t.BeginAnimation(property, null);
            t.SetValue(property, value);
            return;
        }
        var from = (double)t.GetValue(property);
        t.BeginAnimation(property, null);
        t.SetValue(property, value);
        if (Math.Abs(from - value) < 0.5) return;
        var slide = new DoubleAnimation(from, value, Move) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(slide, 30);
        t.BeginAnimation(property, slide);
    }

    // ---- Drawing ----------------------------------------------------------------------------------------------------

    private FrameworkElement Coordinator()
    {
        var fig = new Canvas { Width = 80, Height = StripHeight };
        Canvas.SetLeft(fig, CoordX - 12);
        Person(fig, Brand.K.Accent, null, _coordHead);
        var arm = Bar(fig, 16, 42, 13, 3.5, Brand.K.Accent, null);
        arm.RenderTransformOrigin = new Point(0, 0.5);
        arm.RenderTransform = _coordArm;
        Bar(fig, 16, 54, 34, 4, Brand.K.Border, null);
        Bar(fig, 46, 58, 3, 6, Brand.K.Border, null);
        // The tray of sheets on the desk.
        var tray = Bar(fig, 26, 49, 13, 5, Brand.K.Raised, null);
        tray.SetResourceReference(Shape.StrokeProperty, Brand.K.Border);
        tray.StrokeThickness = 1;
        Label(fig, "Coordinator", 12);
        return fig;
    }

    /// <summary>The Revit station: a small model block on a stand, with its label under it.</summary>
    private void BuildStation()
    {
        double cx = StationWidth / 2, top = 22;
        _modelTop.Data = Geometry.Parse($"M{cx},{top} L{cx + 14},{top + 7} L{cx},{top + 14} L{cx - 14},{top + 7} Z");
        _modelLeft.Data = Geometry.Parse($"M{cx - 14},{top + 7} L{cx},{top + 14} L{cx},{top + 30} L{cx - 14},{top + 23} Z");
        _modelRight.Data = Geometry.Parse($"M{cx + 14},{top + 7} L{cx},{top + 14} L{cx},{top + 30} L{cx + 14},{top + 23} Z");
        foreach (var face in new[] { _modelTop, _modelLeft, _modelRight })
        {
            face.StrokeThickness = 1;
            face.SetResourceReference(Shape.StrokeProperty, Brand.K.Border);
            _station.Children.Add(face);
        }
        _stationLabel.TextWrapping = TextWrapping.NoWrap;
        _stationLabel.Width = StationWidth;
        _stationLabel.TextAlignment = TextAlignment.Center;
        Canvas.SetTop(_stationLabel, 58);
        _station.Children.Add(_stationLabel);
        AutomationProperties.SetName(_station, "Revit: changes go in one at a time");
        _applying = -1;
        Apply(null);
    }

    private Worker Figure(int number)
    {
        var w = new Worker { Number = number, Place = new TranslateTransform(), Arm = new RotateTransform(0) };
        var fig = new Canvas { Width = FigWidth, Height = StripHeight };
        var group = new TransformGroup();
        group.Children.Add(w.Place);
        group.Children.Add(new TranslateTransform());
        fig.RenderTransform = group;
        var shirt = Shirts[(number - 1) % Shirts.Length];
        Person(fig, null, shirt, null);
        var arm = Bar(fig, 16, 42, 12, 3.5, null, shirt);
        arm.RenderTransformOrigin = new Point(0, 0.5);
        arm.RenderTransform = w.Arm;
        // Desk, its leg, and the computer.
        Bar(fig, 14, 54, 44, 4, Brand.K.Border, null);
        Bar(fig, 52, 58, 3, 6, Brand.K.Border, null);
        var monitor = Bar(fig, 28, 28, 28, 20, null, Screen);
        monitor.RadiusX = monitor.RadiusY = 2.5;
        Bar(fig, 40, 48, 4, 6, Brand.K.Border, null);
        foreach (var (y, width) in new[] { (32.0, 16.0), (37.0, 20.0), (42.0, 12.0) })
        {
            var scale = new ScaleTransform(1, 1);
            var line = Bar(fig, 32, y, width, 2, null, ScreenLine);
            line.RenderTransformOrigin = new Point(0, 0.5);
            line.RenderTransform = scale;
            w.Lines.Add((line, scale));
        }
        w.Mark = new TextBlock { FontFamily = ChatUi.Icons, FontSize = 11, Visibility = Visibility.Collapsed };
        Canvas.SetLeft(w.Mark, 37);
        Canvas.SetTop(w.Mark, 32);
        fig.Children.Add(w.Mark);
        // The clock over the head while the job waits its turn in Revit.
        w.Clock = ChatUi.Glyph("", Brand.K.WarningText, 10);
        w.Clock.Visibility = Visibility.Collapsed;
        Canvas.SetLeft(w.Clock, 7);
        Canvas.SetTop(w.Clock, 8);
        fig.Children.Add(w.Clock);
        // A magnifier over the head while it reads the model (reads run alongside the others).
        w.Look = ChatUi.Glyph("\uE721", Brand.K.Link, 10);
        w.Look.Visibility = Visibility.Collapsed;
        Canvas.SetLeft(w.Look, 7);
        Canvas.SetTop(w.Look, 8);
        fig.Children.Add(w.Look);
        Label(fig, "Job " + number.ToString(System.Globalization.CultureInfo.CurrentCulture), 12);
        AutomationProperties.SetName(fig, "Job " + number);
        w.Fig = fig;
        return w;
    }

    /// <summary>A seated person facing right, centred on x = 12: body, head and hair.</summary>
    private static void Person(Canvas fig, string? shirtKey, Brush? shirt, TranslateTransform? head)
    {
        var body = Bar(fig, 3, 36, 18, 22, shirtKey, shirt);
        body.RadiusX = body.RadiusY = 7;
        var top = new Canvas { RenderTransform = head ?? Transform.Identity };
        var face = new Ellipse { Width = 16, Height = 16, Fill = Skin };
        Canvas.SetLeft(face, 4);
        Canvas.SetTop(face, 20);
        top.Children.Add(face);
        top.Children.Add(new Path { Data = Geometry.Parse("M4,27 A8,8 0 0 1 20,25 Q12,22 4,27 Z"), Fill = Hair });
        fig.Children.Add(top);
    }

    private static Rectangle Bar(Canvas fig, double x, double y, double width, double height, string? key, Brush? fill)
    {
        var r = new Rectangle { Width = width, Height = height, RadiusX = Math.Min(2, height / 2), RadiusY = Math.Min(2, height / 2) };
        if (key is not null) r.SetResourceReference(Shape.FillProperty, key);
        else r.Fill = fill;
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        fig.Children.Add(r);
        return r;
    }

    private static void Label(Canvas fig, string text, double centre)
    {
        var label = Brand.Text(text, Brand.K.Muted, 10);
        label.TextWrapping = TextWrapping.NoWrap;
        label.Width = 70;
        label.TextAlignment = TextAlignment.Center;
        Canvas.SetLeft(label, centre - 35);
        Canvas.SetTop(label, 58);
        fig.Children.Add(label);
    }
}
