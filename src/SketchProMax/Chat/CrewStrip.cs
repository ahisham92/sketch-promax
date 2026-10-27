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
/// The small strip over the large chat With coordinator, drawn as people (names from Settings > Chat, CrewNames): you
/// on the left hand your requests to the coordinator at a desk, who gives each job to a worker at a computer of their
/// own (CrewPlan says who has which job); a worker without a job is hidden. Workers think and read the model at their
/// desks all at once (typing; a magnifier while reading). A worker with a change for the model gets up and joins the
/// line at the applier on the right, who takes one change at a time into Revit (the model block lights up and the
/// applier's name says whose change it is); the next steps up, and each goes back to the desk after. A finished
/// worker's screen turns to a tick before the worker leaves. Your message is a sheet handed to the coordinator.
///
/// Cheap on purpose: shapes, no images, no layout work while it moves; the typing is a slow timer (about 6 a second)
/// that runs only while the strip is on screen, the chat's animations are Full and someone is thinking or reading, and
/// the one-off moves ask for 30 frames a second. At Reduced only walking, arriving and leaving move; at Off everything
/// jumps to its place.
/// </summary>
internal sealed class CrewStrip : Border
{
    public const double StripHeight = 100;
    private const double Top = 10, YouX = 22, CoordX = 82, FirstDesk = 158, MaxGap = 96, MinGap = 66, ApplierRoom = 74, InLine = 32, NameTop = 76;
    private static readonly Duration Move = new(TimeSpan.FromMilliseconds(450)), Walk = new(TimeSpan.FromMilliseconds(800)), Fly = new(TimeSpan.FromMilliseconds(800));

    // Decorative colours that read on both themes: the people, and the screens (always dark, like a monitor).
    private static readonly Brush Skin = Brand.Brush(Color.FromRgb(0xE3, 0xBC, 0x96));
    private static readonly Brush Hair = Brand.Brush(Color.FromRgb(0x3B, 0x3B, 0x45));
    private static readonly Brush Screen = Brand.Brush(Color.FromRgb(0x1E, 0x29, 0x3B));
    private static readonly Brush ScreenLine = Brand.Brush(Color.FromRgb(0x93, 0xB4, 0xFF));
    private static readonly Brush ScreenOk = Brand.Brush(Color.FromRgb(0x6E, 0xDC, 0x9A));
    private static readonly Brush ScreenBad = Brand.Brush(Color.FromRgb(0xFF, 0x9A, 0x92));
    private static readonly Brush YouShirt = Brand.Brush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush ApplierShirt = Brand.Brush(Color.FromRgb(0x0F, 0x76, 0x6E));
    private static readonly Brush[] Shirts =
    [
        Brand.Brush(Color.FromRgb(0x3E, 0x9C, 0x7A)), Brand.Brush(Color.FromRgb(0xC0, 0x7A, 0x45)),
        Brand.Brush(Color.FromRgb(0x8A, 0x6B, 0xC7)), Brand.Brush(Color.FromRgb(0x4F, 0x8F, 0xB8)),
        Brand.Brush(Color.FromRgb(0xB8, 0x5C, 0x7A)), Brand.Brush(Color.FromRgb(0x7A, 0x8C, 0x3E)),
    ];

    private static CrewNames? _names;

    /// <summary>The names from Settings > Chat, read once and again after the settings are saved.</summary>
    public static CrewNames Names
    {
        get
        {
            if (_names is { } known) return known;
            try
            {
                var s = Settings.AgentSettings.Load();
                _names = CrewNames.From(s.ChatCrewYou, s.ChatCrewCoordinator, s.ChatCrewWorkers, s.ChatCrewApplier, Environment.UserName);
            }
            catch (Exception) { _names = CrewNames.From(null, null, null, null, Environment.UserName); }
            return _names;
        }
    }

    /// <summary>Raised on the UI thread after the names were changed in Settings.</summary>
    public static event Action? NamesChanged;

    /// <summary>Settings were saved: the names are read again and every open strip redraws.</summary>
    public static void ReloadNames()
    {
        _names = null;
        NamesChanged?.Invoke();
    }

    /// <summary>A person and the parts that move: where they are, the arm, the head (a nod), the legs and the name.</summary>
    private sealed class Figure
    {
        public Canvas Root = null!;
        public TranslateTransform At = null!, Head = null!;
        public RotateTransform Arm = null!;
        public UIElement Legs = null!;
        public TextBlock Name = null!, Clock = null!, Look = null!;
    }

    private sealed class Worker
    {
        public int Number, Slot;
        public Figure Person = null!;
        public Canvas Desk = null!;
        public Button Hit = null!;
        public readonly List<(Rectangle Line, ScaleTransform Scale)> Lines = [];
        public TextBlock Mark = null!;
        public CrewMood Mood;
        public int Line = -1;
        public bool Leaving, Drawn;
    }

    private readonly Canvas _stage = new() { ClipToBounds = true, Height = StripHeight };
    private readonly TextBlock _caption = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(FirstDesk, 2, ChatUi.S3, 0) };
    private readonly Dictionary<int, Worker> _workers = new();
    private readonly Figure _you, _coordinator, _applier;
    private readonly Canvas _station = new();
    private readonly Path _modelTop = new(), _modelLeft = new(), _modelRight = new();
    private readonly DispatcherTimer _typing;
    private readonly Random _random = new(7);
    private int? _applying = -1;
    private int _tick, _desks = 3;
    private readonly Canvas _tray = new();
    private readonly TextBlock _trayCount = new() { FontSize = 9, FontWeight = FontWeights.SemiBold };
    private readonly ScaleTransform _scale = new(1, 1);

    /// <summary>Worker <c>slot</c> was clicked (or pressed with the keyboard): the pane lists that worker's jobs.</summary>
    public event Action<int, Button>? WorkerPicked;

    /// <summary>The coordinator was clicked: the pane shows the coordinator's chat.</summary>
    public event Action<Button>? CoordinatorPicked;

    /// <summary>Smaller, for the chat in its pane at the side of Revit.</summary>
    public bool Compact
    {
        get => _scale.ScaleX < 1;
        set
        {
            _scale.ScaleX = _scale.ScaleY = value ? 0.72 : 1;
            Height = StripHeight * _scale.ScaleY;
        }
    }

    public CrewStrip()
    {
        Height = StripHeight;
        BorderThickness = new Thickness(0, 0, 0, 1);
        SetResourceReference(BorderBrushProperty, Brand.K.BorderSubtle);
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        Brand.Fg(_caption, Brand.K.Muted);
        AutomationProperties.SetLiveSetting(_caption, AutomationLiveSetting.Polite);
        var grid = new Grid { LayoutTransform = _scale };
        grid.Children.Add(_stage);
        grid.Children.Add(_caption);
        Child = grid;
        AutomationProperties.SetName(this, "Who is working: you, the coordinator, its workers and who applies changes in Revit");
        Focusable = false;

        _you = Person(YouShirt, null, standing: true);
        _you.At.X = YouX;
        _you.At.Y = Top - 6;
        _stage.Children.Add(_you.Root);

        _coordinator = Person(null, Brand.K.Accent, standing: false);
        Bar(_coordinator.Root, 6, 56, 32, 4, Brand.K.Border, null);
        Bar(_coordinator.Root, 32, 60, 3, 8, Brand.K.Border, null);
        var tray = Bar(_coordinator.Root, 14, 50, 13, 6, Brand.K.Raised, null);
        tray.SetResourceReference(Shape.StrokeProperty, Brand.K.Border);
        tray.StrokeThickness = 1;
        // The jobs waiting for a free worker: a little stack of sheets on the coordinator's desk, with their number.
        for (var k = 0; k < 3; k++)
        {
            var sheet = Bar(_tray, 14 + k * 2, 45 - k * 3, 11, 13, Brand.K.Raised, null);
            sheet.SetResourceReference(Shape.StrokeProperty, Brand.K.Accent);
            sheet.StrokeThickness = 1;
        }
        Brand.Fg(_trayCount, Brand.K.Accent);
        Canvas.SetLeft(_trayCount, 30);
        Canvas.SetTop(_trayCount, 34);
        _tray.Children.Add(_trayCount);
        _tray.Visibility = Visibility.Collapsed;
        _coordinator.Root.Children.Add(_tray);
        _coordinator.At.X = CoordX;
        _coordinator.At.Y = Top;
        _coordinator.Root.Children.Add(HitArea(-14, 46, "Show the coordinator's chat", b => CoordinatorPicked?.Invoke(b)));
        _stage.Children.Add(_coordinator.Root);

        _applier = Person(ApplierShirt, null, standing: false);
        _applier.At.Y = Top;
        BuildStation();
        _stage.Children.Add(_station);

        ShowNames();
        _caption.Text = CrewPlan.Caption([], Names);

        _typing = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(170) };
        _typing.Tick += (_, _) => Type();
        IsVisibleChanged += (_, _) => Pace();
        _stage.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            Canvas.SetLeft(_station, StationX);
            Arrange(animate: false);
        };
    }

    /// <summary>Where the applier sits (the model block is just right of them).</summary>
    private double StationX => Math.Max(FirstDesk + MaxGap, _stage.ActualWidth - ApplierRoom);

    /// <summary>Shows the crew as the plan has it now and acts out what just happened.</summary>
    public void Show(IReadOnlyList<CrewSeat> seats, IReadOnlyList<CrewEvent> events, int workers, int unassigned)
    {
        var names = Names;
        _desks = Math.Max(1, workers);
        _tray.Visibility = unassigned > 0 ? Visibility.Visible : Visibility.Collapsed;
        _trayCount.Text = unassigned.ToString(System.Globalization.CultureInfo.CurrentCulture);
        foreach (var gone in events.Where(e => e.Cue == CrewCue.Leave)) Leave(gone.Number);
        foreach (var seat in seats)
        {
            if (!_workers.TryGetValue(seat.Number, out var worker) || worker.Leaving || worker.Slot != seat.Slot)
            {
                if (worker is not null) Remove(worker);
                worker = Add(seat.Number, seat.Slot);
            }
            SetMood(worker, seat.Mood);
            worker.Line = seat.Line;
            worker.Person.Name.Text = names.Worker(seat.Slot);
            worker.Hit.ToolTip = names.Worker(seat.Slot) + "'s jobs";
            AutomationProperties.SetName(worker.Hit, $"{names.Worker(seat.Slot)}'s jobs (now job {seat.Number}, {Words(seat.Mood)})");
            AutomationProperties.SetName(worker.Person.Root, $"{names.Worker(seat.Slot)}, job {seat.Number}: {seat.Title}, {Words(seat.Mood)}");
        }
        // Anyone still here whom the plan no longer has (another chat): out.
        foreach (var stale in _workers.Values.Where(w => !w.Leaving && seats.All(s => s.Number != w.Number)).ToList()) Leave(stale.Number);
        Arrange(animate: true);
        foreach (var e in events)
        {
            if (e.Cue == CrewCue.Arrive && _workers.TryGetValue(e.Number, out var arriving)) Arrive(arriving);
            else if (e.Cue == CrewCue.Handover && _workers.TryGetValue(e.Number, out var given)) HandTo(given);
        }
        var applying = seats.FirstOrDefault(s => s.Mood == CrewMood.Applying);
        ShowNames();
        Apply(applying?.Number, applying is null ? "" : names.Worker(applying.Slot));
        _caption.Text = CrewPlan.Caption(seats, names, unassigned);
        _caption.ToolTip = seats.Count == 0 ? null : string.Join("\n", seats.Select(s => $"{names.Worker(s.Slot)} · job {s.Number} · {s.Title}: {Words(s.Mood)}"));
        Pace();
    }

    /// <summary>You sent the coordinator a message: a sheet goes from you to the coordinator, who nods.</summary>
    public void Heard()
    {
        if (!ChatUi.Lively || !IsVisible) return;
        Reach(_you.Arm, -40);
        Sheet(YouX + 10, CoordX - 6, Top - 2);
        var nod = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(900)), BeginTime = Fly.TimeSpan };
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(2.5, KeyTime.FromPercent(0.25)));
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(2.5, KeyTime.FromPercent(0.75)));
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Timeline.SetDesiredFrameRate(nod, 30);
        _coordinator.Head.BeginAnimation(TranslateTransform.YProperty, nod);
    }

    /// <summary>Everyone leaves at once (another chat is shown): nothing is acted out.</summary>
    public void Clear()
    {
        foreach (var worker in _workers.Values.ToList()) Remove(worker);
        ShowNames();
        Apply(null, "");
        _caption.Text = CrewPlan.Caption([], Names);
        _caption.ToolTip = null;
        Pace();
    }

    private void ShowNames()
    {
        var names = Names;
        _you.Name.Text = names.You;
        _coordinator.Name.Text = names.Coordinator;
        if (_applying is null) _applier.Name.Text = names.Applier;
        AutomationProperties.SetName(_you.Root, names.You + ", you");
        AutomationProperties.SetName(_coordinator.Root, names.Coordinator + ", the coordinator");
        AutomationProperties.SetName(_station, names.Applier + " applies changes in Revit, one at a time");
    }

    private static string Words(CrewMood mood) => mood switch
    {
        CrewMood.Applying => "having the change applied in Revit",
        CrewMood.Waiting => "in line to apply a change",
        CrewMood.Reading => "reading the model",
        CrewMood.Resting => "waiting to start again",
        CrewMood.Done => "done",
        CrewMood.Failed => "ended with a problem",
        _ => "thinking",
    };

    // ---- Moving -----------------------------------------------------------------------------------------------------

    /// <summary>The typing runs only while it can be seen, at Full, with someone thinking or reading at a desk.</summary>
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
            w.Person.Arm.Angle = (_tick + w.Number) % 2 == 0 ? -7 : 2;
            var line = w.Lines[(_tick / 3 + w.Number) % w.Lines.Count];
            line.Scale.ScaleX = line.Scale.ScaleX >= 1 ? 0.15 : Math.Min(1, line.Scale.ScaleX + 0.2 + _random.NextDouble() * 0.2);
        }
    }

    private static void Rest(Worker w)
    {
        w.Person.Arm.Angle = 0;
        foreach (var (_, scale) in w.Lines) scale.ScaleX = 1;
    }

    /// <summary>The n-th worker's desk: after the coordinator, closer together when the window is narrow.</summary>
    private double DeskX(int slot)
    {
        var desks = Math.Max(_desks, _workers.Values.Select(w => w.Slot + 1).DefaultIfEmpty(1).Max());
        var room = StationX - Math.Max(1, _workers.Count) * InLine - 24 - FirstDesk - 50;
        var gap = desks > 1 ? Math.Max(MinGap, Math.Min(MaxGap, room / (desks - 1))) : MaxGap;
        return FirstDesk + slot * gap;
    }

    /// <summary>Each worker at their desk, or standing in the line at the applier (0: in front of the applier).</summary>
    private void Arrange(bool animate)
    {
        foreach (var w in _workers.Values.Where(w => !w.Leaving))
        {
            var desk = DeskX(w.Slot);
            Canvas.SetLeft(w.Desk, desk);
            Canvas.SetTop(w.Desk, Top);
            var standing = w.Line >= 0;
            var move = animate && w.Drawn && ChatUi.Animate;
            To(w.Person.At, TranslateTransform.XProperty, standing ? StationX - 34 - w.Line * InLine : desk, move);
            To(w.Person.At, TranslateTransform.YProperty, standing ? Top - 6 : Top, move);
            w.Person.Legs.Visibility = standing ? Visibility.Visible : Visibility.Collapsed;
            w.Drawn = true;
        }
    }

    private Worker Add(int number, int slot)
    {
        var w = new Worker { Number = number, Slot = slot };
        w.Person = Person(Shirts[slot % Shirts.Length], null, standing: false);
        w.Desk = Desk(w);
        w.Hit = HitArea(-14, 66, Names.Worker(slot) + "'s jobs", b => WorkerPicked?.Invoke(slot, b));
        w.Desk.Children.Add(w.Hit);
        w.Person.Root.IsHitTestVisible = false; // the desk's button takes the click, wherever the worker stands
        _workers[number] = w;
        _stage.Children.Insert(0, w.Desk); // behind everyone
        _stage.Children.Add(w.Person.Root);
        return w;
    }

    /// <summary>A new worker: the coordinator hands over a sheet, then the worker and the desk fade in.</summary>
    private void Arrive(Worker w)
    {
        if (!ChatUi.Animate) return;
        var delay = ChatUi.Lively && IsVisible ? Fly.TimeSpan : TimeSpan.Zero;
        HandTo(w);
        foreach (var part in new UIElement[] { w.Person.Root, w.Desk })
        {
            part.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, Move) { BeginTime = delay };
            Timeline.SetDesiredFrameRate(fade, 30);
            part.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>The coordinator raises an arm and a sheet flies over to the worker.</summary>
    private void HandTo(Worker w)
    {
        if (!ChatUi.Lively || !IsVisible) return;
        Reach(_coordinator.Arm, -55);
        Sheet(CoordX + 14, w.Person.At.X + 8, Top);
    }

    private static void Reach(RotateTransform arm, double angle)
    {
        var lift = new DoubleAnimationUsingKeyFrames { Duration = Fly };
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(angle, KeyTime.FromPercent(0.2)));
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(angle, KeyTime.FromPercent(0.6)));
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Timeline.SetDesiredFrameRate(lift, 30);
        arm.BeginAnimation(RotateTransform.AngleProperty, lift);
    }

    /// <summary>A sheet flies in an arc from one x to another and is gone when it lands.</summary>
    private void Sheet(double from, double to, double top)
    {
        var sheet = new Rectangle { Width = 9, Height = 11, RadiusX = 1, RadiusY = 1, StrokeThickness = 1, IsHitTestVisible = false };
        sheet.SetResourceReference(Shape.FillProperty, Brand.K.Raised);
        sheet.SetResourceReference(Shape.StrokeProperty, Brand.K.Accent);
        var at = new TranslateTransform();
        sheet.RenderTransform = at;
        Canvas.SetLeft(sheet, from);
        Canvas.SetTop(sheet, top + 34);
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
    /// Job <paramref name="number"/>'s change is going into Revit (null: none): the worker in front of the applier hands
    /// over the sheet, the model block lights up and the applier's name says whose change it is. One at a time.
    /// </summary>
    private void Apply(int? number, string worker)
    {
        if (number == _applying) return;
        _applying = number;
        var lit = number is not null;
        _modelTop.BeginAnimation(OpacityProperty, null);
        _modelTop.SetResourceReference(Shape.FillProperty, lit ? Brand.K.Accent : Brand.K.Surface);
        _modelLeft.SetResourceReference(Shape.FillProperty, lit ? Brand.K.AccentHover : Brand.K.SurfaceHover);
        _modelRight.SetResourceReference(Shape.FillProperty, lit ? Brand.K.AccentPressed : Brand.K.Border);
        _applier.Name.Text = lit ? $"{Names.Applier} · {worker}" : Names.Applier;
        if (!lit || !ChatUi.Lively || !IsVisible) return;
        Reach(_applier.Arm, -30);
        Sheet(StationX - 30, StationX + 40, Top - 6);
        // A slow, soft glow while the change goes in (stops by itself; a few seconds of frames at most).
        var glow = new DoubleAnimation(1, 0.7, new Duration(TimeSpan.FromMilliseconds(700))) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
        Timeline.SetDesiredFrameRate(glow, 20);
        _modelTop.BeginAnimation(OpacityProperty, glow);
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
        w.Person.Root.BeginAnimation(OpacityProperty, fade);
        w.Desk.BeginAnimation(OpacityProperty, fade);
    }

    private void Remove(Worker w)
    {
        w.Person.Root.BeginAnimation(OpacityProperty, null);
        w.Desk.BeginAnimation(OpacityProperty, null);
        _stage.Children.Remove(w.Person.Root);
        _stage.Children.Remove(w.Desk);
        if (_workers.TryGetValue(w.Number, out var now) && now == w) _workers.Remove(w.Number);
    }

    private void SetMood(Worker w, CrewMood mood)
    {
        if (w.Mood == mood && w.Desk.Tag is not null) return;
        w.Mood = mood;
        w.Desk.Tag = mood;
        var finished = mood is CrewMood.Done or CrewMood.Failed;
        foreach (var (line, _) in w.Lines) line.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
        w.Mark.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        w.Mark.Text = mood == CrewMood.Failed ? ChatUi.GlyphError : ChatUi.GlyphCheck;
        w.Mark.Foreground = mood == CrewMood.Failed ? ScreenBad : ScreenOk;
        w.Person.Clock.Visibility = mood == CrewMood.Waiting ? Visibility.Visible : Visibility.Collapsed;
        w.Person.Look.Visibility = mood == CrewMood.Reading ? Visibility.Visible : Visibility.Collapsed;
        if (mood is not (CrewMood.Thinking or CrewMood.Reading)) Rest(w);
    }

    private static void To(TranslateTransform t, DependencyProperty property, double value, bool animate)
    {
        var from = (double)t.GetValue(property);
        t.BeginAnimation(property, null);
        t.SetValue(property, value);
        if (!animate || Math.Abs(from - value) < 0.5) return;
        var slide = new DoubleAnimation(from, value, Walk) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(slide, 30);
        t.BeginAnimation(property, slide);
    }

    // ---- Drawing ----------------------------------------------------------------------------------------------------

    /// <summary>The applier's place: the person, and the model block on its stand to their right.</summary>
    private void BuildStation()
    {
        _station.Children.Add(_applier.Root);
        double cx = 46, top = Top + 22;
        _modelTop.Data = Geometry.Parse(Inv($"M{cx},{top} L{cx + 14},{top + 7} L{cx},{top + 14} L{cx - 14},{top + 7} Z"));
        _modelLeft.Data = Geometry.Parse(Inv($"M{cx - 14},{top + 7} L{cx},{top + 14} L{cx},{top + 30} L{cx - 14},{top + 23} Z"));
        _modelRight.Data = Geometry.Parse(Inv($"M{cx + 14},{top + 7} L{cx},{top + 14} L{cx},{top + 30} L{cx + 14},{top + 23} Z"));
        foreach (var face in new[] { _modelTop, _modelLeft, _modelRight })
        {
            face.StrokeThickness = 1;
            face.SetResourceReference(Shape.StrokeProperty, Brand.K.Border);
            _station.Children.Add(face);
        }
        Bar(_station, 30, Top + 56, 32, 4, Brand.K.Border, null);
        Apply(null, "");
    }

    private static string Inv(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A worker's desk and computer (the worker sits just left of it).</summary>
    private static Canvas Desk(Worker w)
    {
        var desk = new Canvas();
        Bar(desk, 4, 56, 44, 4, Brand.K.Border, null);
        Bar(desk, 42, 60, 3, 8, Brand.K.Border, null);
        var monitor = Bar(desk, 18, 30, 28, 20, null, Screen);
        monitor.RadiusX = monitor.RadiusY = 2.5;
        Bar(desk, 30, 50, 4, 6, Brand.K.Border, null);
        foreach (var (y, width) in new[] { (34.0, 16.0), (39.0, 20.0), (44.0, 12.0) })
        {
            var scale = new ScaleTransform(1, 1);
            var line = Bar(desk, 22, y, width, 2, null, ScreenLine);
            line.RenderTransformOrigin = new Point(0, 0.5);
            line.RenderTransform = scale;
            w.Lines.Add((line, scale));
        }
        w.Mark = new TextBlock { FontFamily = ChatUi.Icons, FontSize = 11, Visibility = Visibility.Collapsed };
        Canvas.SetLeft(w.Mark, 27);
        Canvas.SetTop(w.Mark, 34);
        desk.Children.Add(w.Mark);
        return desk;
    }

    /// <summary>A person facing right, centred on x = 0: body, head, hair, an arm, legs when standing, and a name.</summary>
    private static Figure Person(Brush? shirt, string? shirtKey, bool standing)
    {
        var f = new Figure { Root = new Canvas(), At = new TranslateTransform(), Arm = new RotateTransform(0), Head = new TranslateTransform() };
        f.Root.RenderTransform = f.At;
        var legs = new Canvas { Visibility = standing ? Visibility.Visible : Visibility.Collapsed };
        Bar(legs, -6, 58, 5, 12, null, Hair);
        Bar(legs, 1, 58, 5, 12, null, Hair);
        f.Root.Children.Add(legs);
        f.Legs = legs;
        var body = Bar(f.Root, -9, 38, 18, 22, shirtKey, shirt);
        body.RadiusX = body.RadiusY = 7;
        var head = new Canvas { RenderTransform = f.Head };
        var face = new Ellipse { Width = 16, Height = 16, Fill = Skin };
        Canvas.SetLeft(face, -8);
        Canvas.SetTop(face, 22);
        head.Children.Add(face);
        head.Children.Add(new Path { Data = Geometry.Parse("M-8,29 A8,8 0 0 1 8,27 Q0,24 -8,29 Z"), Fill = Hair });
        f.Root.Children.Add(head);
        var arm = Bar(f.Root, 4, 42, 12, 3.5, shirtKey, shirt);
        arm.RenderTransformOrigin = new Point(0, 0.5);
        arm.RenderTransform = f.Arm;
        // Over the head: a clock while in line for Revit, a magnifier while reading the model.
        f.Clock = ChatUi.Glyph("", Brand.K.WarningText, 10);
        f.Look = ChatUi.Glyph("", Brand.K.Link, 10);
        foreach (var mark in new[] { f.Clock, f.Look })
        {
            mark.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(mark, 4);
            Canvas.SetTop(mark, 8);
            f.Root.Children.Add(mark);
        }
        f.Name = Brand.Text("", Brand.K.Muted, 10);
        f.Name.TextWrapping = TextWrapping.NoWrap;
        f.Name.TextTrimming = TextTrimming.CharacterEllipsis;
        f.Name.Width = 84;
        f.Name.TextAlignment = TextAlignment.Center;
        Canvas.SetLeft(f.Name, -42);
        Canvas.SetTop(f.Name, NameTop);
        f.Root.Children.Add(f.Name);
        return f;
    }

    /// <summary>
    /// A see-through button over a person (and desk), so a click or the keyboard picks them; it shows only its focus
    /// ring. Its name says what it does for screen readers.
    /// </summary>
    private static Button HitArea(double x, double width, string name, Action<Button> picked)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var button = new Button
        {
            Width = width, Height = 74, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = name,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
        };
        button.SetResourceReference(FocusVisualStyleProperty, "SpmChatFocus");
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => picked(button);
        Canvas.SetLeft(button, x);
        Canvas.SetTop(button, 6);
        return button;
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
}
