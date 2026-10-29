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
/// own (CrewPlan says who has which job). Every worker the chat may use (Settings: jobs at once) has a desk: one without
/// a job sits back at a dark screen, "Ready for work" under the name; one with a job shows how far along its plan is
/// under the name and as a bar on the screen ("60% · 2 left"), and says a few words in a bubble now and then (a big
/// job, its turn at Revit, job done). Workers think and read the model at their
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
    public const double StripHeight = 114;
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
        public TextBlock Name = null!, Status = null!, Clock = null!, Look = null!;
        /// <summary>Under the status: a bar of how far along the job's plan is (hidden without one).</summary>
        public Canvas Meter = null!;
        public Rectangle MeterFill = null!;
    }

    private sealed class Worker
    {
        public int Number, Slot;
        public Figure Person = null!;
        public Canvas Desk = null!;
        public Button Hit = null!;
        public readonly List<(Rectangle Line, ScaleTransform Scale)> Lines = [];
        public TextBlock Mark = null!;
        public Rectangle Progress = null!;
        public CrewMood Mood;
        public int Line = -1;
        public bool Leaving, Drawn;
    }

    private readonly Canvas _stage = new() { ClipToBounds = true, Height = StripHeight };
    private readonly TextBlock _caption = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(FirstDesk, 2, 130, 0) };
    private readonly Dictionary<int, Worker> _workers = new();
    /// <summary>The workers without a job, by desk: sitting back, ready for one.</summary>
    private readonly Dictionary<int, Worker> _idle = new();
    /// <summary>Over everything (the caption too): what the workers say, by job.</summary>
    private readonly Canvas _bubbles = new() { IsHitTestVisible = false };
    private readonly Dictionary<int, Border> _said = new();
    private readonly Figure _you, _coordinator, _applier;
    private readonly Canvas _station = new();
    private readonly Path _modelTop = new(), _modelLeft = new(), _modelRight = new();
    private readonly DispatcherTimer _typing;
    private readonly Random _random = new(7);
    private int? _applying = -1;
    /// <summary>How many changes the applier put into Revit while this strip watched (the line under the name).</summary>
    private int _applied;
    private int _inLine;
    private string _applyingFor = "";
    /// <summary>The path on the floor from the desks to the applier: where the changes go.</summary>
    private readonly Line _path = new() { StrokeThickness = 1.5, StrokeDashArray = [3, 3], IsHitTestVisible = false };
    private int _tick, _desks = 3;
    private readonly Canvas _tray = new();
    private readonly TextBlock _trayCount = new() { FontSize = 9, FontWeight = FontWeights.SemiBold };
    private readonly ScaleTransform _scale = new(1, 1);

    /// <summary>Worker <c>slot</c> was clicked (or pressed with the keyboard): the pane lists that worker's jobs.</summary>
    public event Action<int, Button>? WorkerPicked;

    /// <summary>The coordinator was clicked: the pane shows the coordinator's chat.</summary>
    public event Action<Button>? CoordinatorPicked;

    /// <summary>A worker was added or let go with + and − on the strip: the new number of workers (Settings' jobs at once).</summary>
    public event Action<int>? WorkersChanged;

    private readonly TextBlock _teamSize = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
    private readonly Button _fewer = new(), _more = new();

    /// <summary>Smaller, for the chat in its pane at the side of Revit.</summary>
    public bool Compact
    {
        get => _scale.ScaleX < 1;
        set
        {
            _scale.ScaleX = _scale.ScaleY = value ? 0.8 : 1;
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
        grid.Children.Add(_bubbles);
        grid.Children.Add(TeamSize());
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
        _path.SetResourceReference(Shape.StrokeProperty, Brand.K.BorderSubtle);
        _path.Y1 = _path.Y2 = Top + 70;
        _stage.Children.Add(_path);
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

    /// <summary>Top right: − 3 workers +, to let a worker go or take one on (1 to the most jobs at once), there and then.</summary>
    private FrameworkElement TeamSize()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, ChatUi.S2, 0) };
        Brand.Fg(_teamSize, Brand.K.Muted);
        foreach (var (button, glyph, name, step) in new[] { (_fewer, "−", "One worker fewer", -1), (_more, "+", "One more worker", 1) })
        {
            button.Content = glyph;
            button.MinWidth = 22;
            button.Padding = new Thickness(6, 0, 6, 1);
            button.SetResourceReference(StyleProperty, "SpmChatPill");
            button.SetResourceReference(BackgroundProperty, Brand.K.Raised);
            button.ToolTip = name;
            AutomationProperties.SetName(button, name);
            var by = step;
            button.Click += (_, _) => WorkersChanged?.Invoke(Math.Max(JobBoard.MinLimit, Math.Min(JobBoard.MaxLimit, _desks + by)));
        }
        row.Children.Add(_fewer);
        row.Children.Add(_teamSize);
        row.Children.Add(_more);
        ShowTeamSize();
        return row;
    }

    private void ShowTeamSize()
    {
        _teamSize.Text = _desks == 1 ? "1 worker" : $"{_desks} workers";
        _fewer.IsEnabled = _desks > JobBoard.MinLimit;
        _more.IsEnabled = _desks < JobBoard.MaxLimit;
    }

    /// <summary>Where the applier sits (the model block is just right of them).</summary>
    /// <summary>
    /// Where the applier sits (the model block is just right of them): close after the last desk, with room for the line
    /// of workers in between, or at the right edge when the window is narrower than that.
    /// </summary>
    private double StationX
    {
        get
        {
            var desks = Math.Max(_desks, _workers.Values.Select(w => w.Slot + 1).DefaultIfEmpty(1).Max());
            var close = FirstDesk + (desks - 1) * MaxGap + 102 + (desks - 1) * InLine;
            return Math.Max(FirstDesk + MaxGap, Math.Min(close, _stage.ActualWidth - ApplierRoom));
        }
    }

    /// <summary>Shows the crew as the plan has it now and acts out what just happened.</summary>
    public void Show(IReadOnlyList<CrewSeat> seats, IReadOnlyList<CrewEvent> events, int workers, int unassigned)
    {
        var names = Names;
        _desks = Math.Max(1, workers);
        ShowTeamSize();
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
            var status = CrewPlan.Status(seat, seat.Slot);
            worker.Person.Status.Text = status;
            // The figure under the name stands out while a job is under way; the bar under it fills with the plan.
            worker.Person.Status.SetResourceReference(TextBlock.ForegroundProperty, seat.Percent >= 0 && seat.Mood is not (CrewMood.Done or CrewMood.Failed) ? Brand.K.Text : Brand.K.Muted);
            worker.Person.Status.FontWeight = seat.Percent >= 0 ? FontWeights.SemiBold : FontWeights.Normal;
            worker.Person.Meter.Visibility = seat.Percent >= 0 ? Visibility.Visible : Visibility.Collapsed;
            worker.Person.MeterFill.Width = 44 * Math.Max(0, Math.Min(100, seat.Percent)) / 100.0;
            worker.Progress.Width = seat.Percent < 0 || seat.Mood is CrewMood.Done or CrewMood.Failed ? 0 : 26 * seat.Percent / 100.0;
            worker.Hit.ToolTip = names.Worker(seat.Slot) + "'s jobs";
            AutomationProperties.SetName(worker.Hit, $"{names.Worker(seat.Slot)}'s jobs (now job {seat.Number}, {Words(seat.Mood)}, {status})");
            AutomationProperties.SetName(worker.Person.Root, $"{names.Worker(seat.Slot)}, job {seat.Number}: {seat.Title}, {Words(seat.Mood)}, {status}");
        }
        // Anyone still here whom the plan no longer has (another chat): out.
        foreach (var stale in _workers.Values.Where(w => !w.Leaving && seats.All(s => s.Number != w.Number)).ToList()) Leave(stale.Number);
        ShowIdle();
        Arrange(animate: true);
        foreach (var e in events)
        {
            if (e.Cue == CrewCue.Arrive && _workers.TryGetValue(e.Number, out var arriving)) Arrive(arriving);
            else if (e.Cue == CrewCue.Handover && _workers.TryGetValue(e.Number, out var given)) HandTo(given);
            if (e.Cue == CrewCue.Huddle)
            {
                Reach(_coordinator.Arm, -55);
                SayAt(Coordinator, CoordX, e.Say, TimeSpan.Zero);
            }
            else if (e.Cue == CrewCue.Share) Call(e.From, e.Number, e.Say);
            else if (e.Cue != CrewCue.Leave && e.Say.Length > 0) Say(e.Number, e.Say, e.Cue is CrewCue.Arrive or CrewCue.Handover ? Fly.TimeSpan : TimeSpan.Zero);
        }
        var applying = seats.FirstOrDefault(s => s.Mood == CrewMood.Applying);
        ShowNames();
        _inLine = seats.Count(s => s.Mood == CrewMood.Waiting);
        Apply(applying?.Number, applying is null ? "" : names.Worker(applying.Slot));
        ShowApplier();
        _caption.Text = CrewPlan.Caption(seats, names, unassigned);
        _caption.ToolTip = seats.Count == 0 ? null : string.Join("\n", seats.Select(s => $"{names.Worker(s.Slot)} · job {s.Number} · {s.Title}: {Words(s.Mood)}, {CrewPlan.Status(s, s.Slot)}"));
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
        foreach (var bubble in _said.Values) bubble.BeginAnimation(OpacityProperty, null);
        _said.Clear();
        _bubbles.Children.Clear();
        ShowIdle();
        Arrange(animate: false);
        ShowNames();
        _inLine = 0;
        _applied = 0;
        Apply(null, "");
        ShowApplier();
        _caption.Text = CrewPlan.Caption([], Names);
        _caption.ToolTip = null;
        Pace();
    }

    private void ShowNames()
    {
        var names = Names;
        _you.Name.Text = names.You;
        _coordinator.Name.Text = names.Coordinator;
        _applier.Name.Text = names.Applier;
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
        var run = IsVisible && ChatUi.Lively && (_applying is not null || _workers.Values.Any(w => !w.Leaving && w.Mood is CrewMood.Thinking or CrewMood.Reading));
        if (run && !_typing.IsEnabled) _typing.Start();
        else if (!run && _typing.IsEnabled)
        {
            _typing.Stop();
            foreach (var w in _workers.Values) Rest(w);
            _applier.Arm.Angle = 0;
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
        // The applier works the change into the model: a hand on it, moving.
        _applier.Arm.Angle = _applying is null ? 0 : _tick % 4 < 2 ? -24 : -12;
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
        Canvas.SetLeft(_station, StationX);
        _path.X1 = FirstDesk - 4;
        _path.X2 = StationX + 20;
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
        ArrangeIdle();
    }

    private void ArrangeIdle()
    {
        foreach (var idle in _idle.Values)
        {
            var desk = DeskX(idle.Slot);
            Canvas.SetLeft(idle.Desk, desk);
            Canvas.SetTop(idle.Desk, Top);
            idle.Person.At.X = desk;
            idle.Person.At.Y = Top;
        }
    }

    /// <summary>
    /// A desk for each worker the chat may use: one without a job (or whose job just left) sits back at a dark screen,
    /// ready for work; a click lists the jobs they did.
    /// </summary>
    private void ShowIdle()
    {
        var names = Names;
        foreach (var slot in _idle.Keys.Where(k => k >= _desks).ToList())
        {
            _stage.Children.Remove(_idle[slot].Desk);
            _stage.Children.Remove(_idle[slot].Person.Root);
            _idle.Remove(slot);
        }
        for (var slot = 0; slot < _desks; slot++)
        {
            if (!_idle.TryGetValue(slot, out var idle))
            {
                var at = slot;
                idle = new Worker { Number = -1 - slot, Slot = slot, Mood = CrewMood.Resting };
                idle.Person = Person(Shirts[slot % Shirts.Length], null, standing: false);
                idle.Desk = Desk(idle);
                foreach (var (line, _) in idle.Lines) line.Visibility = Visibility.Collapsed;
                idle.Hit = HitArea(-14, 66, "", b => WorkerPicked?.Invoke(at, b));
                idle.Desk.Children.Add(idle.Hit);
                idle.Person.Root.IsHitTestVisible = false;
                // Sitting back from the desk, a little faded: nothing to do yet.
                idle.Person.Root.Opacity = 0.6;
                idle.Person.Arm.Angle = 35;
                _idle[slot] = idle;
                _stage.Children.Insert(0, idle.Desk);
                _stage.Children.Add(idle.Person.Root);
            }
            var busy = _workers.Values.Any(w => w.Slot == slot);
            idle.Desk.Visibility = idle.Person.Root.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            idle.Person.Name.Text = names.Worker(slot);
            idle.Person.Status.Text = CrewPlan.Status(null, slot);
            idle.Hit.ToolTip = names.Worker(slot) + "'s jobs";
            AutomationProperties.SetName(idle.Hit, $"{names.Worker(slot)}'s jobs ({CrewPlan.Status(null, slot).ToLowerInvariant()})");
            AutomationProperties.SetName(idle.Person.Root, $"{names.Worker(slot)}, {CrewPlan.Status(null, slot).ToLowerInvariant()}");
        }
    }

    /// <summary>
    /// Job <paramref name="number"/>'s worker says a few words: a small bubble over the head, a couple of seconds, then
    /// gone (at Off it just shows and goes). Over the caption, so it can be read.
    /// </summary>
    private void Say(int number, string words, TimeSpan delay)
    {
        if (_workers.TryGetValue(number, out var w)) SayAt(number, w.Person.At.X + 4, words, delay, w.Line >= 0 ? 2 : 4);
    }

    /// <summary>A bubble with <paramref name="words"/> centred on <paramref name="x"/>, for <paramref name="key"/> (a job, or the applier).</summary>
    private void SayAt(int key, double x, string words, TimeSpan delay, double top = 4)
    {
        if (!IsVisible) return;
        var number = key;
        if (_said.TryGetValue(number, out var old))
        {
            old.BeginAnimation(OpacityProperty, null);
            _bubbles.Children.Remove(old);
        }
        var text = Brand.Text(words, Brand.K.Text, 9);
        text.FontWeight = FontWeights.SemiBold;
        text.TextWrapping = TextWrapping.NoWrap;
        var bubble = new Border { Child = text, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(5, 1, 5, 1), Opacity = 0 };
        bubble.SetResourceReference(BackgroundProperty, Brand.K.Raised);
        bubble.SetResourceReference(BorderBrushProperty, Brand.K.Accent);
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(bubble, Math.Max(0, Math.Min(x - bubble.DesiredSize.Width / 2, _stage.ActualWidth - bubble.DesiredSize.Width - 2)));
        Canvas.SetTop(bubble, top);
        _bubbles.Children.Add(bubble);
        _said[number] = bubble;
        var show = new DoubleAnimationUsingKeyFrames { Duration = new Duration(delay + TimeSpan.FromMilliseconds(2600)) };
        var soft = ChatUi.Animate;
        show.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        show.KeyFrames.Add(soft ? new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(200))) : new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay)));
        show.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(2200))));
        show.KeyFrames.Add(soft ? new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(2600))) : new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(2600))));
        Timeline.SetDesiredFrameRate(show, 20);
        show.Completed += (_, _) =>
        {
            _bubbles.Children.Remove(bubble);
            if (_said.TryGetValue(number, out var now) && now == bubble) _said.Remove(number);
        };
        bubble.BeginAnimation(OpacityProperty, show);
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
        var lift = new DoubleAnimationUsingKeyFrames { Duration = Fly, FillBehavior = FillBehavior.Stop };
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
        var finished = _applying is >= 0 && _workers.ContainsKey(_applying.Value);
        _applying = number;
        _applyingFor = worker;
        var lit = number is not null;
        _modelTop.BeginAnimation(OpacityProperty, null);
        _modelTop.SetResourceReference(Shape.FillProperty, lit ? Brand.K.Accent : Brand.K.Surface);
        _modelLeft.SetResourceReference(Shape.FillProperty, lit ? Brand.K.AccentHover : Brand.K.SurfaceHover);
        _modelRight.SetResourceReference(Shape.FillProperty, lit ? Brand.K.AccentPressed : Brand.K.Border);
        // The change before went in: the applier says so (and counts it).
        if (finished)
        {
            _applied++;
            SayAt(Applier, StationX + 10, _applied == 1 ? "In the model!" : _applied % 5 == 0 ? $"{_applied} changes in!" : "Applied!", TimeSpan.Zero);
        }
        if (!lit || !ChatUi.Lively || !IsVisible) return;
        Reach(_applier.Arm, -30);
        Sheet(StationX - 30, StationX + 40, Top - 6);
        // A slow, soft glow for as long as the change goes in (20 frames a second, only then).
        var glow = new DoubleAnimation(1, 0.65, new Duration(TimeSpan.FromMilliseconds(700))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(glow, 20);
        _modelTop.BeginAnimation(OpacityProperty, glow);
    }

    private const int Applier = int.MinValue, Coordinator = int.MinValue + 1;

    /// <summary>
    /// What the coordinator does now in the main chat (<paramref name="tool"/>: the step it runs; null when its turn
    /// ended): reading the model shows the magnifier, handing out jobs says so, under the coordinator's name.
    /// </summary>
    public void CoordinatorDoes(string? tool)
    {
        var reading = tool is not null && tool != Tools.ChatJobsTool.ToolName && StepNames.ReadOnly(tool) && !tool.StartsWith("Task", StringComparison.Ordinal) && tool != "TodoWrite" && tool != "ToolSearch";
        _coordinator.Look.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
        _coordinator.Status.Text = tool is null ? "" : tool == Tools.ChatJobsTool.ToolName ? "Handing out work" : reading ? "Reading the model" : "Working";
    }

    /// <summary>
    /// A call: the worker at desk <paramref name="from"/> passes what their job found to job <paramref name="number"/>'s
    /// worker; a dashed arc joins the two for a moment, with a phone on it, and the one called says thanks.
    /// </summary>
    private void Call(int from, int number, string words)
    {
        if (!IsVisible || !_workers.TryGetValue(number, out var w)) return;
        var x1 = DeskX(from) + 2;
        var x2 = w.Person.At.X;
        if (Math.Abs(x2 - x1) < 8) return;
        var mid = (x1 + x2) / 2;
        var arc = new Path { StrokeThickness = 1.5, StrokeDashArray = [2, 2], Data = Geometry.Parse(Inv($"M{x1},{Top + 24} Q{mid},{Top - 6} {x2},{Top + 24}")), IsHitTestVisible = false };
        arc.SetResourceReference(Shape.StrokeProperty, Brand.K.Accent);
        var phone = ChatUi.Glyph("\uE717", Brand.K.Accent, 10);
        Canvas.SetLeft(phone, mid - 5);
        Canvas.SetTop(phone, Top + 2);
        var call = new Canvas { IsHitTestVisible = false };
        call.Children.Add(arc);
        call.Children.Add(phone);
        _bubbles.Children.Insert(0, call);
        var show = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(2600)) };
        show.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        show.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2200))));
        show.KeyFrames.Add(ChatUi.Animate ? new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))) : new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        Timeline.SetDesiredFrameRate(show, 20);
        show.Completed += (_, _) => _bubbles.Children.Remove(call);
        call.BeginAnimation(OpacityProperty, show);
        Say(number, words, TimeSpan.FromMilliseconds(400));
    }

    /// <summary>The line under the applier's name: whose change goes in, who waits, or how many went in.</summary>
    private void ShowApplier()
    {
        var waiting = _inLine == 1 ? "1 waits" : $"{_inLine} wait";
        _applier.Status.Text = _applying is not null
            ? _inLine > 0 ? $"Applying {_applyingFor}'s · {waiting}" : $"Applying {_applyingFor}'s change"
            : _inLine > 0 ? $"{waiting} in line"
            : _applied > 0 ? $"{_applied} applied · ready" : "Ready for changes";
        AutomationProperties.SetName(_station, $"{Names.Applier} applies changes in Revit, one at a time: {_applier.Status.Text}");
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
        if (_said.TryGetValue(w.Number, out var bubble))
        {
            bubble.BeginAnimation(OpacityProperty, null);
            _bubbles.Children.Remove(bubble);
            _said.Remove(w.Number);
        }
        ShowIdle();
        ArrangeIdle();
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
        // How far along the job's plan is: a thin bar along the bottom of the screen.
        w.Progress = Bar(desk, 19, 47, 0, 2, null, ScreenOk);
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
        // Under the name, for the workers: how far along, or ready for work.
        f.Status = Brand.Text("", Brand.K.Muted, 10);
        f.Status.TextWrapping = TextWrapping.NoWrap;
        f.Status.TextTrimming = TextTrimming.CharacterEllipsis;
        f.Status.Width = 84;
        f.Status.TextAlignment = TextAlignment.Center;
        Canvas.SetLeft(f.Status, -42);
        Canvas.SetTop(f.Status, NameTop + 13);
        f.Root.Children.Add(f.Status);
        f.Meter = new Canvas { Visibility = Visibility.Collapsed };
        var track = Bar(f.Meter, -22, NameTop + 28, 44, 4, Brand.K.BorderSubtle, null);
        track.RadiusX = track.RadiusY = 2;
        f.MeterFill = Bar(f.Meter, -22, NameTop + 28, 0, 4, null, ScreenOk);
        f.MeterFill.RadiusX = f.MeterFill.RadiusY = 2;
        f.Root.Children.Add(f.Meter);
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
