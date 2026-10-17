using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using SketchProMax.Bridge;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The job card's controls while a step keeps Revit's thread busy. The chat pane lives on Revit's thread, so during a long
/// batch it can't paint or take a click; this small window runs on a thread of its own (like Export for Claude's progress
/// window) and shows the step's progress with Pause/Resume, Skip step, Stop and a message box. Nothing here touches the
/// Revit API: it only reads the job feed and sets the task's <see cref="JobControl"/>. It opens when a step has run a
/// moment (<see cref="JobFeed.LongStep"/>) and closes when the task ends.
///
/// <see cref="WaitPaused"/> is the other half: on Revit's thread, at an item boundary, a small modal Paused window. Revit
/// keeps painting but takes no edits while the batch's transaction group is open; no transaction is open meanwhile, and
/// the batch stays one Undo step. Resume, Skip step and Stop work from either window.
/// </summary>
internal static class JobWindow
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static JobControl? _for;
    private static bool _closeRequested;

    /// <summary>
    /// Opens the window for <paramref name="control"/>'s task, once (closing it by hand keeps it closed for that task).
    /// Any thread. <paramref name="sent"/> is told each message typed here (its JobNotes id and text), on this window's thread.
    /// </summary>
    public static void Open(JobControl control, bool dark, Action<string, string> sent)
    {
        lock (Gate)
        {
            if (_for == control) return;
            _for = control;
            _closeRequested = false;
        }
        var thread = new Thread(() => Run(control, dark, sent)) { IsBackground = true, Name = "Sketch ProMax job window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Closes the window (the task ended). Any thread.</summary>
    public static void Close()
    {
        Dispatcher? dispatcher;
        lock (Gate)
        {
            _closeRequested = true;
            _for = null;
            dispatcher = _dispatcher;
            _dispatcher = null;
        }
        try { dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal); } catch (Exception) { }
    }

    private static void Run(JobControl control, bool dark, Action<string, string> sent)
    {
        try
        {
            lock (Gate)
                if (_closeRequested || _for != control) return; // closed before it opened: no dispatcher made for nothing
            var dispatcher = Dispatcher.CurrentDispatcher;
            lock (Gate)
            {
                if (_closeRequested || _for != control) return;
                _dispatcher = dispatcher;
            }
            // This thread is not Revit's, but an exception escaping it would still end Revit: swallow it and close.
            dispatcher.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                try { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); } catch (Exception) { }
            };
            var window = Build(control, dark, sent);
            window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            window.Show();
            Dispatcher.Run();
            try { window.Close(); } catch (Exception) { }
        }
        catch (Exception) { /* the window is a convenience: the task goes on without it */ }
    }

    private static Window Build(JobControl control, bool dark, Action<string, string> sent)
    {
        var window = new Window
        {
            Title = "Sketch ProMax task", Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        // The palette and the chat's styles, made on this thread (the pane's own can't be shared across threads).
        window.Resources.MergedDictionaries.Add(Brand.Palette(dark));
        try { window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(ChatStyles.Xaml(ChatUi.RadiusSmall, ChatUi.Radius, SystemParameters.ClientAreaAnimation))); }
        catch (Exception) { /* plain buttons */ }
        window.SetResourceReference(Control.BackgroundProperty, Brand.K.Bg);
        window.SetResourceReference(Control.ForegroundProperty, Brand.K.Text);
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 24;
        window.Top = area.Bottom - 300;

        var panel = new StackPanel { Margin = new Thickness(ChatUi.S4) };
        var head = Brand.Text("Task running in Revit", Brand.K.Text, ChatUi.TitleSize, bold: true);
        panel.Children.Add(head);
        var why = Brand.Text("Revit is busy with this step, so the chat waits. Pause, skip or stop it here.", Brand.K.Muted, ChatUi.SmallSize);
        why.Margin = new Thickness(0, 2, 0, ChatUi.S3);
        panel.Children.Add(why);
        var now = Brand.Text("", Brand.K.Text, ChatUi.BodySize);
        AutomationProperties.SetLiveSetting(now, AutomationLiveSetting.Polite);
        panel.Children.Add(now);
        // A still bar (a track and a filled part), like the card's: nothing in it moves on its own.
        var filled = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var fill = new Border { CornerRadius = new CornerRadius(2) };
        fill.SetResourceReference(Border.BackgroundProperty, Brand.K.Accent);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(filled);
        grid.ColumnDefinitions.Add(rest);
        grid.Children.Add(fill);
        var bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), Child = grid, Margin = new Thickness(0, ChatUi.S2, 0, ChatUi.S1) };
        bar.SetResourceReference(Border.BackgroundProperty, Brand.K.BorderSubtle);
        AutomationProperties.SetName(bar, "Step progress");
        panel.Children.Add(bar);
        var line = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
        panel.Children.Add(line);

        Button Pill(string text, string style, string help)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, ChatUi.S2, 0), MinWidth = 72, ToolTip = help };
            b.SetResourceReference(FrameworkElement.StyleProperty, style);
            AutomationProperties.SetName(b, text);
            AutomationProperties.SetHelpText(b, help);
            return b;
        }
        var pause = Pill("Pause", "SpmChatPill", "Pauses at the next item boundary; Resume carries on.");
        var skip = Pill("Skip step", "SpmChatPill", "Ends this step at the next item (what it finished is kept); Claude moves on.");
        var stop = Pill("Stop", "SpmChatDanger", "Stops the task: the item in progress is rolled back, finished items are kept.");
        pause.Click += (_, _) => control.Apply(control.State is JobState.PauseRequested or JobState.Paused ? JobAction.Resume : JobAction.Pause);
        skip.Click += (_, _) => control.Apply(JobAction.Skip);
        stop.Click += (_, _) => control.Apply(JobAction.Stop);
        var buttons = new WrapPanel { Margin = new Thickness(0, ChatUi.S3, 0, 0) };
        buttons.Children.Add(pause);
        buttons.Children.Add(skip);
        buttons.Children.Add(stop);
        panel.Children.Add(buttons);

        // A message for Claude, applied when this step ends (the chat shows it once Revit is free).
        var label = Brand.Text("Message for Claude", Brand.K.Text, ChatUi.SmallSize, bold: true);
        label.Margin = new Thickness(0, ChatUi.S4, 0, ChatUi.S1);
        panel.Children.Add(label);
        var box = new TextBox { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        box.SetResourceReference(FrameworkElement.StyleProperty, "SpmChatField");
        AutomationProperties.SetName(box, "Message for Claude");
        AutomationProperties.SetHelpText(box, "Type a message and press Enter: Claude gets it when this step ends.");
        var send = Pill("Send", "SpmChatPrimary", "Sends the message; Claude gets it when this step ends.");
        send.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(send, Dock.Right);
        row.Children.Add(send);
        row.Children.Add(box);
        panel.Children.Add(row);
        var ack = Brand.Text("", Brand.K.SuccessText, ChatUi.SmallSize);
        ack.Margin = new Thickness(0, ChatUi.S1, 0, 0);
        AutomationProperties.SetLiveSetting(ack, AutomationLiveSetting.Polite);
        panel.Children.Add(ack);
        void Send()
        {
            var text = box.Text.Trim();
            if (text.Length == 0) { ack.Text = "Type a message first."; return; }
            // The task ended meanwhile (this window closes in a moment): a note queued now would wait for the next task.
            if (control.Over) { ack.Text = "The task has ended. Type your message in the chat."; return; }
            var id = JobNotes.Add(text);
            box.Clear();
            ack.Text = "Got it, will apply after this item.";
            try { sent(id, text); } catch (Exception) { }
        }
        send.Click += (_, _) => Send();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Send();
        };
        window.Content = panel;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var state = control.State;
            if (control.Over) { window.Close(); return; }
            var snap = JobFeed.Now;
            var title = JobMath.Title(snap.Tool);
            var text = title.Length == 0 ? "Working…" : title + (snap.Label.Length > 0 ? " · " + snap.Label : "");
            if (now.Text != text) now.Text = text;
            var percent = snap.Total > 0 ? JobMath.Percent(snap.Done, snap.Total) : 0;
            filled.Width = new GridLength(percent, GridUnitType.Star);
            rest.Width = new GridLength(100 - percent, GridUnitType.Star);
            AutomationProperties.SetName(bar, $"Step progress {percent} percent");
            var said = state switch
            {
                JobState.PauseRequested => "Pausing at the next item…",
                JobState.Paused => "Paused. Resume, Skip step or Stop.",
                JobState.Skipping => "Skipping this step…",
                JobState.Stopping => "Stopping…",
                _ => JobMath.Line(snap, DateTime.UtcNow),
            };
            if (line.Text != said) line.Text = said;
            var paused = state is JobState.PauseRequested or JobState.Paused;
            pause.Content = paused ? "Resume" : "Pause";
            AutomationProperties.SetName(pause, paused ? "Resume" : "Pause");
            pause.IsEnabled = state is JobState.Running or JobState.PauseRequested or JobState.Paused;
            skip.IsEnabled = JobControl.Can(state, JobAction.Skip);
            stop.IsEnabled = JobControl.Can(state, JobAction.Stop);
        };
        timer.Start();
        window.Closed += (_, _) => timer.Stop();
        return window;
    }

    // ---- Paused at an item boundary (Revit's thread) ------------------------------------------------------------------

    /// <summary>
    /// Waits on Revit's thread while <paramref name="control"/> is paused at an item boundary of <paramref name="what"/>:
    /// a small modal window (Resume is the default; closing it resumes too). It closes by itself when Resume, Skip step
    /// or Stop is pressed in the job window.
    /// </summary>
    public static void WaitPaused(JobControl control, string what)
    {
        var window = new Window
        {
            Title = "Sketch ProMax: paused", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Brand.Prepare(window);
        var panel = new StackPanel();
        panel.Children.Add(Brand.Header("Paused", what));
        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(Brand.Text(
            "The task is paused between two items. Finished items are kept; nothing is half done. Revit waits here until you " +
            "choose, so the model can't be edited meanwhile.", Brand.K.Text));
        var resume = Brand.Button("Resume", Brand.Weight.Primary);
        resume.IsDefault = true;
        var skip = Brand.Button("Skip step");
        var stop = Brand.Button("Stop", Brand.Weight.Danger);
        AutomationProperties.SetHelpText(resume, "Carries on with the next item.");
        AutomationProperties.SetHelpText(skip, "Ends this step here; Claude moves on to the next one.");
        AutomationProperties.SetHelpText(stop, "Stops the task here; finished items are kept.");
        resume.Click += (_, _) => { control.Apply(JobAction.Resume); window.Close(); };
        skip.Click += (_, _) => { control.Apply(JobAction.Skip); window.Close(); };
        stop.Click += (_, _) => { control.Apply(JobAction.Stop); window.Close(); };
        body.Children.Add(Brand.ButtonRow(stop, skip, resume));
        panel.Children.Add(body);
        window.Content = panel;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { if (control.State != JobState.Paused) window.Close(); };
        window.Loaded += (_, _) => { timer.Start(); resume.Focus(); };
        window.Closed += (_, _) => timer.Stop();
        try { window.ShowDialog(); }
        catch (InvalidOperationException) { /* no dialog possible now: carry on */ }
        if (control.State == JobState.Paused) control.Apply(JobAction.Resume);
    }
}
