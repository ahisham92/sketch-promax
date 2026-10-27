using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using SketchProMax.Chat;
using SketchProMax.Revit;

namespace SketchProMax.UI;

/// <summary>
/// The Rescue window: opens by itself when Revit stops responding (or waits for a dialog out of sight) for
/// <see cref="FreezeWatch.OpenAfter"/>. It runs on a thread of its own (like the chat's job window), so it paints and
/// takes clicks while Revit's thread is stuck. It says what Revit is busy with (from Revit's journal) and whose work it
/// is, how many unsaved changes Lifeline keeps, and offers, in order of how gentle they are: Stop the Sketch ProMax task,
/// Show the dialog, Press Esc in Revit, Wait, and Close Revit (asks once more). After a long freeze from a Revit command
/// it offers to warn before that command next time (Revit/HeavyCommands). When Revit responds again it says so and
/// closes. Nothing here touches the Revit API.
/// </summary>
internal static class RescueWindow
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static Action<TimeSpan>? _responding;

    /// <summary>Opens the window for a freeze that began at <paramref name="since"/>. Any thread; once per freeze.</summary>
    public static void Open(FreezeWatch.Kind kind, DateTime since, bool dark)
    {
        lock (Gate) if (_dispatcher is not null) return;
        var revit = App.MainWindow;
        var thread = new Thread(() => Run(kind, since, dark, revit)) { IsBackground = true, Name = "Sketch ProMax rescue window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Revit responds again: the window says so and closes in a few seconds. Any thread.</summary>
    public static void Responding(TimeSpan lasted)
    {
        Action<TimeSpan>? tell;
        Dispatcher? dispatcher;
        lock (Gate) { tell = _responding; dispatcher = _dispatcher; }
        if (tell is null || dispatcher is null) return;
        try { dispatcher.BeginInvoke(tell, lasted); } catch (Exception) { }
    }

    /// <summary>Closes the window now (the core stops). Any thread.</summary>
    public static void Close()
    {
        Dispatcher? dispatcher;
        lock (Gate) { dispatcher = _dispatcher; _dispatcher = null; _responding = null; }
        try { dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal); } catch (Exception) { }
    }

    private static void Run(FreezeWatch.Kind kind, DateTime since, bool dark, IntPtr revit)
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            lock (Gate)
            {
                if (_dispatcher is not null) return;
                _dispatcher = dispatcher;
            }
            // This thread is not Revit's, but an exception escaping it would still end Revit: swallow it and close.
            dispatcher.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                try { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); } catch (Exception) { }
            };
            var window = Build(kind, since, dark, revit);
            window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            window.Show();
            Dispatcher.Run();
            try { window.Close(); } catch (Exception) { }
        }
        catch (Exception) { /* the window is a convenience */ }
        finally
        {
            lock (Gate)
                if (_dispatcher == Dispatcher.CurrentDispatcher) { _dispatcher = null; _responding = null; }
        }
    }

    private static Window Build(FreezeWatch.Kind kind, DateTime since, bool dark, IntPtr revit)
    {
        var dialog = kind == FreezeWatch.Kind.HiddenDialog;
        var window = new Window
        {
            Title = "Sketch ProMax Rescue", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = true, ShowActivated = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Resources.MergedDictionaries.Add(Brand.Palette(dark));
        try { window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(ChatStyles.Xaml(ChatUi.RadiusSmall, ChatUi.Radius, ChatUi.Animate))); }
        catch (Exception) { /* plain buttons */ }
        window.SetResourceReference(Control.BackgroundProperty, Brand.K.Bg);
        window.SetResourceReference(Control.ForegroundProperty, Brand.K.Text);
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 24;
        window.Top = area.Bottom - 360;
        window.Loaded += (_, _) => JobWindow.PlaceOnRevitMonitor(window, revit);

        var panel = new StackPanel { Margin = new Thickness(ChatUi.S4) };
        var head = Brand.Text(dialog ? "Revit is waiting for a dialog" : "Revit isn't responding", Brand.K.Text, ChatUi.TitleSize, bold: true);
        AutomationProperties.SetLiveSetting(head, AutomationLiveSetting.Assertive);
        panel.Children.Add(head);
        var line = Brand.Text("", Brand.K.Text, ChatUi.BodySize);
        line.Margin = new Thickness(0, ChatUi.S2, 0, 0);
        panel.Children.Add(line);
        var whose = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
        whose.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(whose);
        var lifeline = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
        lifeline.Margin = new Thickness(0, ChatUi.S2, 0, 0);
        panel.Children.Add(lifeline);

        Button Pill(string text, string style, string help)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, ChatUi.S2, ChatUi.S2, 0), MinWidth = 72, ToolTip = help };
            b.SetResourceReference(FrameworkElement.StyleProperty, style);
            AutomationProperties.SetName(b, text);
            AutomationProperties.SetHelpText(b, help);
            return b;
        }
        var stopOurs = Pill("Stop the Sketch ProMax task", "SpmChatPrimary", "Stops the running Sketch ProMax task at its next check. Finished items are kept.");
        var show = Pill("Show the dialog", "SpmChatPrimary", "Brings Revit's dialog to the front, in the middle of Revit's window, so you can answer it.");
        var esc = Pill("Press Esc in Revit", "SpmChatPill", "Some long Revit operations stop when Esc is pressed. Nothing is lost.");
        var wait = Pill("Wait", "SpmChatPill", "Hides this window. Revit keeps working; this window opens again next time Revit freezes.");
        var close = Pill("Close Revit…", "SpmChatDanger", "Ends Revit without saving. Lifeline keeps your changes since the last save to replay.");
        var buttons = new WrapPanel { Margin = new Thickness(0, ChatUi.S2, 0, 0) };
        foreach (var b in new[] { stopOurs, show, esc, wait, close }) buttons.Children.Add(b);
        panel.Children.Add(buttons);
        var said = Brand.Text("", Brand.K.SuccessText, ChatUi.SmallSize);
        said.Margin = new Thickness(0, ChatUi.S2, 0, 0);
        AutomationProperties.SetLiveSetting(said, AutomationLiveSetting.Polite);
        panel.Children.Add(said);

        // "Close Revit…" asks once more, here (a modal message box would wait on this thread's own queue just the same).
        var confirm = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, ChatUi.S3, 0, 0) };
        var confirmText = Brand.Text("", Brand.K.DangerText, ChatUi.BodySize);
        confirm.Children.Add(confirmText);
        var closeNow = Pill("Close Revit now", "SpmChatDanger", "Ends Revit at once, without saving.");
        var back = Pill("Back", "SpmChatPill", "Keeps Revit open.");
        var confirmRow = new WrapPanel();
        confirmRow.Children.Add(back);
        confirmRow.Children.Add(closeNow);
        confirm.Children.Add(confirmRow);
        panel.Children.Add(confirm);

        // After a long freeze from a Revit command: warn before it next time (ticked; saved when the window closes).
        var warn = new CheckBox { Visibility = Visibility.Collapsed, IsChecked = true, Margin = new Thickness(0, ChatUi.S3, 0, 0) };
        warn.SetResourceReference(Control.ForegroundProperty, Brand.K.Text);
        panel.Children.Add(warn);
        window.Content = panel;

        FreezeCause.Busy? busy = null;
        var lastRead = DateTime.MinValue;
        var lastLifeline = DateTime.MinValue;
        var lifelineText = "";
        DateTime? over = null;
        TimeSpan lasted = TimeSpan.Zero;

        void Refresh()
        {
            var frozen = (over is null ? DateTime.Now : since + lasted) - since;
            if (over is null && DateTime.Now - lastRead > TimeSpan.FromSeconds(5))
            {
                busy = FreezeWatch.Cause();
                lastRead = DateTime.Now;
            }
            var ours = FreezeWatch.OursRunning;
            if (over is null)
            {
                line.Text = dialog
                    ? $"A Revit dialog has been waiting for {FreezeCause.Duration(frozen)} and may be behind a window or on another screen."
                    : FreezeCause.Line(busy!, frozen);
                whose.Text = dialog ? "" : FreezeCause.Whose(busy!, ours);
                whose.Visibility = whose.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (DateTime.Now - lastLifeline > TimeSpan.FromSeconds(5))
            {
                lastLifeline = DateTime.Now;
                var files = FreezeWatch.Lifeline();
                lifelineText = files.Count == 0
                    ? "Lifeline has no unsaved changes to keep."
                    : string.Join(" ", files.Select(f => $"Lifeline keeps {f.Changes} change{(f.Changes == 1 ? "" : "s")} to {f.Model}" +
                                                         (f.Since is { } t ? $" since {t:HH:mm}." : ".")));
            }
            lifeline.Text = lifelineText;
            stopOurs.Visibility = over is null && ours ? Visibility.Visible : Visibility.Collapsed;
            show.Visibility = over is null && dialog ? Visibility.Visible : Visibility.Collapsed;
            esc.Visibility = over is null && !dialog ? Visibility.Visible : Visibility.Collapsed;
            close.Visibility = over is null ? Visibility.Visible : Visibility.Collapsed;
            wait.Content = over is null ? "Wait" : "Close";
            if (over is null && !dialog && busy is not null && FreezeCause.OfferWarning(busy, frozen) && warn.Visibility != Visibility.Visible)
            {
                warn.Content = new TextBlock { Text = $"Warn me before {busy.What} next time", TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetName(warn, $"Warn me before {busy.What} next time");
                warn.Visibility = Visibility.Visible;
            }
        }

        void Remember()
        {
            if (warn.Visibility != Visibility.Visible || warn.IsChecked != true || busy?.CommandId is not { } id) return;
            var frozen = (over is null ? DateTime.Now : since + lasted) - since;
            FreezeCause.Learn(id, busy.What, frozen, since);
            HeavyCommands.Refresh();
        }

        stopOurs.Click += (_, _) => { FreezeWatch.StopOurs(); said.Text = "Asked the task to stop. It stops at its next check."; };
        show.Click += (_, _) => said.Text = FreezeWatch.ShowDialog() ? "The dialog is in front of Revit now." : "No dialog was found. Revit may be busy instead: try Press Esc.";
        esc.Click += (_, _) => { FreezeWatch.PressEsc(); said.Text = "Pressed Esc in Revit. If Revit doesn't respond in a minute, wait or close it."; };
        wait.Click += (_, _) => window.Close();
        close.Click += (_, _) =>
        {
            var files = FreezeWatch.Lifeline();
            var count = files.Sum(f => f.Changes);
            confirmText.Text = "Revit closes without saving. " + (count > 0
                ? $"Lifeline keeps your {count} change{(count == 1 ? "" : "s")} since the last save: open the model again and press Replay in the Lifeline window."
                : "Lifeline has no unsaved changes, so nothing since your last save is kept.");
            confirm.Visibility = Visibility.Visible;
            buttons.Visibility = Visibility.Collapsed;
            back.Focus();
        };
        back.Click += (_, _) => { confirm.Visibility = Visibility.Collapsed; buttons.Visibility = Visibility.Visible; };
        closeNow.Click += (_, _) =>
        {
            Remember();
            FreezeWatch.CloseRevit(dialog ? "a dialog out of sight" : busy?.What ?? "unknown");
        };
        window.Closed += (_, _) => Remember();

        lock (Gate)
            _responding = took =>
            {
                over = DateTime.Now;
                lasted = took;
                head.Text = "Revit is responding again";
                line.Text = $"It was busy for {FreezeCause.Duration(took)}.";
                whose.Visibility = Visibility.Collapsed;
                confirm.Visibility = Visibility.Collapsed;
                buttons.Visibility = Visibility.Visible;
                said.Text = "";
                Refresh();
                // Closes by itself unless the user is reading it or the warning box is there to tick.
                var closing = new DispatcherTimer { Interval = TimeSpan.FromSeconds(warn.Visibility == Visibility.Visible ? 30 : 8) };
                closing.Tick += (_, _) =>
                {
                    if (window.IsMouseOver) return;
                    closing.Stop();
                    window.Close();
                };
                closing.Start();
            };

        // Always on top (unlike the job window): while Revit hangs, Windows puts a "Not Responding" ghost of Revit's window
        // in front, which belongs to another process, so "only while Revit is in front" would hide this window.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        Refresh();
        timer.Start();
        window.Closed += (_, _) => timer.Stop();
        return window;
    }
}
