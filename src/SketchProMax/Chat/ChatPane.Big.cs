using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The chat in a large window, like Sketch ProMax's other tools: the header's expand button moves this same pane (its
/// conversation, the message box, a running answer and its jobs) out of Revit's dockable pane into a maximised window
/// owned by Revit, with the coordinator and its jobs listed in a column on the right. Minimising or closing that window,
/// or the same button (now "back to the side"), puts the pane back in the dock; meanwhile the dock says where the chat is.
/// Nothing is reloaded or restarted either way: it is the same element, only its parent changes.
///
/// A narrow pane (under <see cref="NarrowWidth"/>) shortens the header's and the message box's words instead of
/// crowding them: "Effort: Auto", "5h 8% · Wk 20%", and the brief pill as its icon (each keeps its full tooltip).
/// </summary>
internal sealed partial class ChatPane
{
    private const double NarrowWidth = 420, SideWidth = 280;

    private bool _narrow;
    private Button _bigButton = null!;
    private Window? _big;
    private ContentControl? _dockHost;
    private FrameworkElement? _dockNote;
    private bool _leavingBig;
    // The large window's jobs column (docked right, over the whole height) and where the switcher lives otherwise.
    private Border? _side;
    private StackPanel? _sideList;
    private readonly TextBlock _sideEmpty = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
    private Panel? _switcherHome;
    private bool _jobsSide;

    /// <summary>True while the chat shows in its large window.</summary>
    internal bool IsBig => _big is not null;

    /// <summary>The header's expand button (back to the side once large), and the narrow pane's shorter words.</summary>
    private void BuildBigButton()
    {
        _bigButton = ChatUi.IconButton(ChatUi.Paths.Expand, "", "");
        _bigButton.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        _bigButton.Click += (_, _) =>
        {
            if (_big is null) OpenBig();
            else CloseBig();
        };
        ShowBigButton();
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) Narrow(e.NewSize.Width < NarrowWidth);
        };
    }

    private void ShowBigButton()
    {
        var big = _big is not null;
        _bigButton.Content = ChatUi.Icon(big ? ChatUi.Paths.Collapse : ChatUi.Paths.Expand);
        _bigButton.ToolTip = big
            ? "Back to the side: the chat returns to its pane on the right of Revit (minimising or closing this window does the same)."
            : "Open the chat large: a big window with the jobs listed on the right. Minimise or close it to bring the chat back to the side.";
        AutomationProperties.SetName(_bigButton, big ? "Back to the side" : "Open the chat large");
        AutomationProperties.SetHelpText(_bigButton, (string)_bigButton.ToolTip);
    }

    /// <summary>The pane got narrow (or wide again): the header and the brief pill shorten their words.</summary>
    private void Narrow(bool narrow)
    {
        if (narrow == _narrow) return;
        _narrow = narrow;
        ShowUsage();
        ShowPickers();
        ShowBriefChoice();
    }

    /// <summary>Moves the pane into the large window (maximised), leaving a note in the dock.</summary>
    private void OpenBig()
    {
        if (_sample || _stopped) return;
        if (_big is { } open)
        {
            open.Activate();
            return;
        }
        if (Parent is not ContentControl host)
        {
            Status("The chat opens large from its pane on the side. Click Chat on the ribbon, then the expand button.", spin: false);
            return;
        }
        CloseDrawer();
        var note = DockNote();
        host.Content = note; // lets go of this pane
        _dockHost = host;
        _dockNote = note;
        var area = SystemParameters.WorkArea;
        var window = new Window
        {
            Title = "Sketch ProMax chat",
            Width = Math.Min(1400, Math.Floor(area.Width * 0.85)),
            Height = Math.Floor(area.Height * 0.85),
            MinWidth = 640,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        Brand.Prepare(window);
        window.SetResourceReference(BackgroundProperty, Brand.K.Bg);
        window.Content = this;
        // Minimised: back to the side, as asked (not to a small bar at the foot of the screen).
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized) Dispatcher.BeginInvoke(new Action(CloseBig));
        };
        window.Closing += (_, _) =>
        {
            if (!_leavingBig) PutBack();
        };
        _big = window;
        ShowBig();
        window.Show();
        window.WindowState = WindowState.Maximized;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusInput));
    }

    /// <summary>Back to the side: the pane returns to the dock and the large window closes.</summary>
    private void CloseBig()
    {
        if (_big is not { } window) return;
        _leavingBig = true;
        try
        {
            PutBack();
            window.Close();
        }
        catch (Exception) { /* the pane is back in the dock whatever the window did */ }
        finally { _leavingBig = false; }
    }

    private void PutBack()
    {
        if (_big is not { } window) return;
        _big = null;
        window.Content = null;
        if (_dockHost is { } host) host.Content = this;
        if (_dockNote is not null) Brand.ReleaseTheme(_dockNote);
        _dockHost = null;
        _dockNote = null;
        if (!_stopped)
        {
            ShowBig();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusInput));
        }
    }

    /// <summary>What the dock shows while the chat is large: where it is, and the way back.</summary>
    private FrameworkElement DockNote()
    {
        var panel = new StackPanel { Margin = new Thickness(ChatUi.S4), VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(Brand.Text("The chat is open in a large window.", Brand.K.Text, 13, bold: true));
        var line = Brand.Text("Minimise or close that window, or press Back to the side, to bring the chat back here. Nothing in it stops meanwhile.", Brand.K.Muted, 12);
        line.Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S3);
        panel.Children.Add(line);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var back = Brand.Button("Back to the side", Brand.Weight.Primary);
        back.Margin = new Thickness(0);
        back.Click += (_, _) => CloseBig();
        buttons.Children.Add(back);
        var show = Brand.Button("Show the window");
        show.Click += (_, _) => _big?.Activate();
        buttons.Children.Add(show);
        panel.Children.Add(buttons);
        var root = new Border { Child = panel };
        Brand.UseTheme(root);
        root.SetResourceReference(Border.BackgroundProperty, Brand.K.Bg);
        AutomationProperties.SetName(root, "The chat is open in a large window");
        return root;
    }

    /// <summary>Large: the jobs column shows and the switcher moves into it; at the side it goes back over the conversation.</summary>
    private void ShowBig()
    {
        var big = _big is not null;
        ShowBigButton();
        if (big && _side is null) BuildSide();
        if (_side is not null) _side.Visibility = big ? Visibility.Visible : Visibility.Collapsed;
        if (big != _jobsSide && _sideList is not null && _switcherHome is not null)
        {
            _jobsSide = big;
            (big ? _switcherHome : _sideList).Children.Remove(_switcher);
            if (big) _sideList.Children.Insert(0, _switcher);
            else _switcherHome.Children.Insert(0, _switcher);
            _switchShown = ""; // drawn again in the other layout
        }
        ShowSwitcher();
    }

    /// <summary>The column on the right of the large window: Coordinator and each job, one per row with its state.</summary>
    private void BuildSide()
    {
        var heading = Brand.Text("Jobs", Brand.K.Text, ChatUi.BodySize, bold: true);
        var help = Brand.Text("Pick one to open its own chat; Coordinator is the main chat that hands them out.", Brand.K.Muted, ChatUi.SmallSize);
        help.Margin = new Thickness(0, 2, 0, ChatUi.S3);
        _sideEmpty.Margin = new Thickness(0, ChatUi.S1, 0, 0);
        _sideList = new StackPanel();
        _sideList.Children.Add(_sideEmpty);
        var scroll = new ScrollViewer { Content = _sideList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var stack = new DockPanel { Margin = new Thickness(ChatUi.S3) };
        var head = new StackPanel();
        head.Children.Add(heading);
        head.Children.Add(help);
        DockPanel.SetDock(head, Dock.Top);
        stack.Children.Add(head);
        stack.Children.Add(scroll);
        _side = new Border { Child = stack, Width = SideWidth, BorderThickness = new Thickness(1, 0, 0, 0), Visibility = Visibility.Collapsed };
        _side.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        AutomationProperties.SetName(_side, "Jobs");
        SetDock(_side, Dock.Right);
        KeyboardNavigation.SetTabIndex(_side, 5);
        // Docked first, so it runs the whole height on the right and the header, conversation and message box sit beside it.
        Children.Insert(0, _side);
    }

    /// <summary>The jobs column's words when the chat has no jobs (none in a Simple chat).</summary>
    private void ShowSideEmpty(int jobs)
    {
        if (!_jobsSide) return;
        _sideEmpty.Text = IsCoordinator
            ? "No jobs yet. Ask for several separate things at once and the coordinator hands them to jobs that work side by side; they show here."
            : "This is a simple chat: Sketch ProMax does the work here itself, without jobs. Switch it to With coordinator (under its name) to use jobs.";
        _sideEmpty.Visibility = jobs == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
