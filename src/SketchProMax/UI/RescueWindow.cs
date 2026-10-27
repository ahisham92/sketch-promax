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
/// <see cref="FreezeWatch.OpenAfter"/>, and from the Sketch ProMax tray icon at any time (RescueTray). It runs on a thread
/// of its own (like the chat's job window), so it paints and takes clicks while Revit's thread is stuck. It follows
/// Revit's state live (FreezeWatch.Current) and shows:
///   - whether Revit is working or looks stuck, from its processor, memory, file and journal activity (FreezeCause.Judge),
///     in one coloured line with what to do and the figures;
///   - what Revit is busy with (from Revit's journal) and whose work it is;
///   - how many unsaved changes Lifeline keeps.
/// Buttons, gentlest first: Stop the Sketch ProMax task, Show the dialog, Press Esc in Revit, Wait, Restart Revit (ends
/// Revit, starts it again on the same model, Lifeline offers Replay) and Close Revit; the last two ask once more. After a
/// long freeze from a Revit command it offers to warn before that command next time (Revit/HeavyCommands). When Revit
/// responds again it says so and, if it opened by itself, closes. Nothing here touches the Revit API.
/// </summary>
internal static class RescueWindow
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static Window? _window;
    private static Action<TimeSpan>? _responding;

    /// <summary>
    /// Opens the window (once; a second call brings it to the front). <paramref name="automatic"/>: opened by the watch
    /// for a freeze (it closes by itself when Revit responds again), not from the tray icon. Any thread.
    /// </summary>
    public static void Open(bool automatic)
    {
        Dispatcher? open;
        lock (Gate) open = _dispatcher;
        if (open is not null)
        {
            try { open.BeginInvoke(() => { if (_window is { } w) { w.Show(); w.Activate(); } }); } catch (Exception) { }
            return;
        }
        var revit = App.MainWindow;
        var dark = FreezeWatch.Dark;
        var thread = new Thread(() => Run(automatic, dark, revit)) { IsBackground = true, Name = "Sketch ProMax rescue window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Revit responds again after <paramref name="lasted"/>: the window says so. Any thread.</summary>
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
        lock (Gate) { dispatcher = _dispatcher; _dispatcher = null; _responding = null; _window = null; }
        try { dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal); } catch (Exception) { }
    }

    private static void Run(bool automatic, bool dark, IntPtr revit)
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
            var window = Build(automatic, dark, revit);
            lock (Gate) _window = window;
            window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            window.Show();
            if (!automatic) window.Activate();
            Dispatcher.Run();
            try { window.Close(); } catch (Exception) { }
        }
        catch (Exception) { /* the window is a convenience */ }
        finally
        {
            lock (Gate)
                if (_dispatcher == Dispatcher.CurrentDispatcher) { _dispatcher = null; _responding = null; _window = null; }
        }
    }

    private static Window Build(bool automatic, bool dark, IntPtr revit)
    {
        var window = new Window
        {
            Title = "Sketch ProMax Rescue", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = true, ShowActivated = !automatic, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Resources.MergedDictionaries.Add(Brand.Palette(dark));
        try { window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(ChatStyles.Xaml(ChatUi.RadiusSmall, ChatUi.Radius, ChatUi.Animate))); }
        catch (Exception) { /* plain buttons */ }
        window.SetResourceReference(Control.BackgroundProperty, Brand.K.Bg);
        window.SetResourceReference(Control.ForegroundProperty, Brand.K.Text);
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 24;
        window.Top = area.Bottom - 420;
        window.Loaded += (_, _) => JobWindow.PlaceOnRevitMonitor(window, revit);

        var panel = new StackPanel { Margin = new Thickness(ChatUi.S4) };
        var head = Brand.Text("", Brand.K.Text, ChatUi.TitleSize, bold: true);
        AutomationProperties.SetLiveSetting(head, AutomationLiveSetting.Assertive);
        panel.Children.Add(head);
        // Working or stuck: the verdict in colour, what to do, and the figures behind it.
        var verdict = Brand.Text("", Brand.K.SuccessText, ChatUi.BodySize, bold: true);
        verdict.Margin = new Thickness(0, ChatUi.S2, 0, 0);
        AutomationProperties.SetLiveSetting(verdict, AutomationLiveSetting.Polite);
        panel.Children.Add(verdict);
        var advice = Brand.Text("", Brand.K.Text, ChatUi.BodySize);
        panel.Children.Add(advice);
        var figures = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
        figures.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(figures);
        var line = Brand.Text("", Brand.K.Text, ChatUi.BodySize);
        line.Margin = new Thickness(0, ChatUi.S3, 0, 0);
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
        var wait = Pill("Wait", "SpmChatPill", "Hides this window. Revit keeps working; the tray icon opens it again.");
        var restart = Pill("Restart Revit…", "SpmChatDanger", "Ends Revit and starts it again on the same model. Lifeline offers your unsaved changes to replay.");
        var close = Pill("Close Revit…", "SpmChatDanger", "Ends Revit without saving. Lifeline keeps your changes since the last save to replay.");
        var buttons = new WrapPanel { Margin = new Thickness(0, ChatUi.S2, 0, 0) };
        foreach (var b in new[] { stopOurs, show, esc, wait, restart, close }) buttons.Children.Add(b);
        panel.Children.Add(buttons);
        var said = Brand.Text("", Brand.K.SuccessText, ChatUi.SmallSize);
        said.Margin = new Thickness(0, ChatUi.S2, 0, 0);
        AutomationProperties.SetLiveSetting(said, AutomationLiveSetting.Polite);
        panel.Children.Add(said);

        // Restart and Close ask once more, here (a modal message box would wait on this thread's own queue just the same).
        var confirm = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, ChatUi.S3, 0, 0) };
        var confirmText = Brand.Text("", Brand.K.DangerText, ChatUi.BodySize);
        confirm.Children.Add(confirmText);
        var confirmNow = Pill("", "SpmChatDanger", "");
        var back = Pill("Back", "SpmChatPill", "Keeps Revit open.");
        var confirmRow = new WrapPanel();
        confirmRow.Children.Add(back);
        confirmRow.Children.Add(confirmNow);
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
        var wasFrozen = false;
        DateTime frozeSince = DateTime.Now;
        string? doing = null; // "restart" or "close", while confirming

        void Refresh()
        {
            var (kind, since) = FreezeWatch.Current;
            var frozen = kind != FreezeWatch.Kind.Responding;
            var dialog = kind == FreezeWatch.Kind.HiddenDialog;
            if (frozen) { wasFrozen = true; frozeSince = since; }
            var length = DateTime.Now - since;
            if (DateTime.Now - lastRead > TimeSpan.FromSeconds(5))
            {
                busy = FreezeWatch.Cause();
                lastRead = DateTime.Now;
            }
            var ours = FreezeWatch.OursRunning;
            var activity = FreezeWatch.Activity();

            if (frozen) head.Text = dialog ? "Revit is waiting for a dialog" : "Revit isn't responding";
            else if (wasFrozen) head.Text = "Revit is responding again";
            else head.Text = "Revit is responding";

            if (frozen && !dialog)
            {
                verdict.Text = FreezeCause.Verdict(activity);
                verdict.SetResourceReference(TextBlock.ForegroundProperty, activity.Progress switch
                {
                    FreezeCause.Progress.Stuck => Brand.K.DangerText,
                    FreezeCause.Progress.Quiet => Brand.K.WarningText,
                    _ => Brand.K.SuccessText,
                });
                advice.Text = FreezeCause.Advice(activity, busy!);
            }
            else
            {
                verdict.Text = "";
                advice.Text = dialog ? "Answer the dialog and Revit carries on. Nothing is lost." : frozen ? "" : "Nothing is stuck. This window shows what Revit does if it stops responding.";
            }
            verdict.Visibility = verdict.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            figures.Text = FreezeCause.Figures(activity);

            line.Text = dialog
                ? $"A Revit dialog has been waiting for {FreezeCause.Duration(length)} and may be behind a window or on another screen."
                : frozen ? FreezeCause.Line(busy!, length, ours)
                : wasFrozen ? "Revit answers clicks again." : "";
            line.Visibility = line.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            whose.Text = frozen && !dialog ? FreezeCause.Whose(busy!, ours) : "";
            whose.Visibility = whose.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

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

            stopOurs.Visibility = FreezeWatch.OursCancellable ? Visibility.Visible : Visibility.Collapsed;
            show.Visibility = dialog ? Visibility.Visible : Visibility.Collapsed;
            esc.Visibility = frozen && !dialog ? Visibility.Visible : Visibility.Collapsed;
            restart.Visibility = frozen ? Visibility.Visible : Visibility.Collapsed;
            close.Visibility = frozen ? Visibility.Visible : Visibility.Collapsed;
            wait.Content = frozen ? "Wait" : "Close";
            AutomationProperties.SetName(wait, frozen ? "Wait" : "Close");
            if (!frozen && doing is not null) Back();
            if (frozen && !dialog && busy is not null && FreezeCause.OfferWarning(busy, length, ours) && warn.Visibility != Visibility.Visible)
            {
                warn.Content = new TextBlock { Text = $"Warn me before {busy.What} next time", TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetName(warn, $"Warn me before {busy.What} next time");
                warn.Visibility = Visibility.Visible;
            }
        }

        void Remember()
        {
            if (warn.Visibility != Visibility.Visible || warn.IsChecked != true || busy?.CommandId is not { } id) return;
            FreezeCause.Learn(id, busy.What, DateTime.Now - frozeSince, frozeSince);
            HeavyCommands.Refresh();
        }

        void Ask(string what)
        {
            doing = what;
            var count = FreezeWatch.Lifeline().Sum(f => f.Changes);
            var kept = count > 0
                ? $"Lifeline keeps your {count} change{(count == 1 ? "" : "s")} since the last save"
                : "Lifeline has no unsaved changes, so nothing since your last save is kept";
            if (what == "restart")
            {
                var model = FreezeWatch.ModelToReopen;
                confirmText.Text = "Revit closes without saving and starts again" +
                                   (model is null ? "." : $" on {System.IO.Path.GetFileNameWithoutExtension(model)}.") +
                                   $" {kept}" + (count > 0 ? ": press Replay in the Lifeline window when the model opens." : ".");
                confirmNow.Content = "Restart Revit now";
            }
            else
            {
                confirmText.Text = $"Revit closes without saving. {kept}" + (count > 0 ? ": open the model again and press Replay in the Lifeline window." : ".");
                confirmNow.Content = "Close Revit now";
            }
            AutomationProperties.SetName(confirmNow, (string)confirmNow.Content);
            confirm.Visibility = Visibility.Visible;
            buttons.Visibility = Visibility.Collapsed;
            back.Focus();
        }

        void Back()
        {
            doing = null;
            confirm.Visibility = Visibility.Collapsed;
            buttons.Visibility = Visibility.Visible;
        }

        stopOurs.Click += (_, _) => { FreezeWatch.StopOurs(); said.Text = "Asked the task to stop. It stops at its next check."; };
        show.Click += (_, _) => said.Text = FreezeWatch.ShowDialog() ? "The dialog is in front of Revit now." : "No dialog was found. Revit may be busy instead: try Press Esc.";
        esc.Click += (_, _) => { FreezeWatch.PressEsc(); said.Text = "Pressed Esc in Revit. Watch the line above: it says when Revit works again."; };
        wait.Click += (_, _) => window.Close();
        restart.Click += (_, _) => Ask("restart");
        close.Click += (_, _) => Ask("close");
        back.Click += (_, _) => Back();
        confirmNow.Click += (_, _) =>
        {
            Remember();
            var why = FreezeWatch.Current.Kind == FreezeWatch.Kind.HiddenDialog ? "a dialog out of sight" : busy?.What ?? "unknown";
            if (doing == "restart") FreezeWatch.RestartRevit(why);
            else FreezeWatch.CloseRevit(why);
        };
        window.Closed += (_, _) => Remember();

        lock (Gate)
            _responding = took =>
            {
                said.Text = $"Revit was busy for {FreezeCause.Duration(took)}.";
                Refresh();
                if (!automatic) return;
                // Opened by itself: closes unless the user is reading it or the warning box is there to tick.
                var closing = new DispatcherTimer { Interval = TimeSpan.FromSeconds(warn.Visibility == Visibility.Visible ? 30 : 8) };
                closing.Tick += (_, _) =>
                {
                    if (window.IsMouseOver || FreezeWatch.Current.Kind != FreezeWatch.Kind.Responding) return;
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
