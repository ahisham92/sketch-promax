using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SketchProMax.Commands;
using SketchProMax.Update;

namespace SketchProMax.UI;

/// <summary>
/// Says when there is a new version, without getting in the way: a small toast at the bottom right of Revit's window
/// (<see cref="UpdateToast"/>; it never takes the focus and hides by itself) and the same message as a band at the top
/// of the chat (<see cref="Banner"/>).
///   - "Sketch ProMax 0.32.0 is available", with the top lines of its release notes: Update now, What's new, Later.
///     When a check finds it and automatic updates are off (on, the update just happens).
///   - "Updated to 0.32.0": after the switch to it in this Revit session (automatic updates), with What's new.
///   - "Sketch ProMax 0.32.0 is installed": downloaded, but Revit must restart to use it.
/// The toast waits while a tool, a job or the chat is working, or a Revit dialog is open, and shows at most once per
/// version per Revit session (remembered in the AppDomain, so a version switch doesn't show it again).
/// Offer may be called from any thread; everything else runs on Revit's UI thread.
/// </summary>
internal static partial class UpdateNotice
{
    private const string ShownKey = "SketchProMax.UpdateNotice.Shown", DismissedKey = "SketchProMax.UpdateNotice.Dismissed";

    /// <summary>The message the chat's band shows, or null.</summary>
    public static Notice? Current { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public static event Action? Changed;

    private static Notice? _waiting;
    private static DispatcherTimer? _timer;

    // ---- In Revit --------------------------------------------------------------------------------------------------

    /// <summary>A new message: the chat's band shows it now, the toast when nothing is running. Any thread.</summary>
    public static void Offer(Notice notice)
    {
        var ui = App.Ui;
        if (ui is null) return;
        ui.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                if (!Versions(AppDomain.CurrentDomain.GetData(DismissedKey) as string).Contains(Plain(notice.Version), StringComparer.OrdinalIgnoreCase))
                {
                    Current = notice;
                    Changed?.Invoke();
                }
                _waiting = notice;
                if (_timer is null)
                {
                    _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                    _timer.Tick += (_, _) => Pump();
                }
                _timer.Start();
                Pump();
            }
            catch (Exception) { /* the Update button's badge still says it */ }
        }));
    }

    /// <summary>Shows the waiting toast if nothing is running now. UI thread; cheap when nothing waits.</summary>
    public static void Pump()
    {
        if (_waiting is not { } notice)
        {
            _timer?.Stop();
            return;
        }
        var shown = Versions(AppDomain.CurrentDomain.GetData(ShownKey) as string);
        var decision = Decide(notice.Version, shown, App.IsBusy || RevitDialogOpen());
        if (decision == Decision.Wait) return;
        _waiting = null;
        _timer?.Stop();
        if (decision == Decision.Skip) return;
        AppDomain.CurrentDomain.SetData(ShownKey, Remember(AppDomain.CurrentDomain.GetData(ShownKey) as string, notice.Version));
        UpdateToast.Open(notice);
    }

    /// <summary>The band (or the toast's Later) was closed: no band for this version again in this session.</summary>
    public static void Dismiss()
    {
        if (Current is { } notice)
            AppDomain.CurrentDomain.SetData(DismissedKey, Remember(AppDomain.CurrentDomain.GetData(DismissedKey) as string, notice.Version));
        Current = null;
        Changed?.Invoke();
    }

    /// <summary>This core is stopping (a version switch): its toast closes and nothing more is shown from it.</summary>
    public static void Stop()
    {
        _waiting = null;
        _timer?.Stop();
        _timer = null;
        Current = null;
        Changed = null;
        UpdateToast.CloseOpen();
    }

    /// <summary>Update now: the update window opens and the update runs as from the Update button.</summary>
    public static void UpdateNow()
    {
        UpdateToast.CloseOpen();
        Current = null;
        Changed?.Invoke();
        App.CheckForUpdatesInteractive();
    }

    public static void WhatsNew() => CoreCommands.WhatsNew();

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    /// <summary>True while a modal Revit dialog (or one of ours) is open: Revit's main window is disabled then.</summary>
    private static bool RevitDialogOpen()
    {
        try { return App.MainWindow != IntPtr.Zero && !IsWindowEnabled(App.MainWindow); }
        catch (Exception) { return false; }
    }

    // ---- The chat's band -------------------------------------------------------------------------------------------

    /// <summary>
    /// The band for the top of the chat: hidden until there is a message. It follows <see cref="Changed"/> only while it
    /// is on screen, so a chat element replaced by a version switch holds no static event.
    /// </summary>
    public static FrameworkElement Banner()
    {
        var band = new Border
        {
            Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 10, 4), Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        };
        band.SetResourceReference(Border.BackgroundProperty, Brand.K.NoticeBg);
        band.SetResourceReference(Border.BorderBrushProperty, Brand.K.NoticeBorder);
        AutomationProperties.SetLiveSetting(band, AutomationLiveSetting.Polite);
        var title = Brand.Text("", Brand.K.Text, 12, bold: true);
        var line = Brand.Text("", Brand.K.Text, 12);
        var next = Brand.Text("", Brand.K.Muted, 11);
        var update = BandButton("Update now", primary: true);
        var notes = BandButton("What's new", primary: false);
        var close = BandButton("Dismiss", primary: false);
        AutomationProperties.SetHelpText(close, "Hides this message. The Update button on the ribbon still shows the version.");
        update.Click += (_, _) => UpdateNow();
        notes.Click += (_, _) => WhatsNew();
        close.Click += (_, _) => Dismiss();
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(update);
        buttons.Children.Add(notes);
        buttons.Children.Add(close);
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(line);
        stack.Children.Add(next);
        stack.Children.Add(buttons);
        band.Child = stack;

        void Render()
        {
            if (Current is not { } n)
            {
                band.Visibility = Visibility.Collapsed;
                return;
            }
            title.Text = n.Title;
            line.Text = n.Lines.Count > 0 ? n.Lines[0] : "";
            line.Visibility = line.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            next.Text = n.Next;
            update.Visibility = n.Kind == Kind.Available ? Visibility.Visible : Visibility.Collapsed;
            update.IsDefault = false;
            AutomationProperties.SetName(band, n.Title + ". " + line.Text);
            band.Visibility = Visibility.Visible;
        }
        void OnChanged() => Render();
        band.Loaded += (_, _) =>
        {
            Changed -= OnChanged;
            Changed += OnChanged;
            Render();
        };
        band.Unloaded += (_, _) => Changed -= OnChanged;
        return band;
    }

    private static Button BandButton(string text, bool primary)
    {
        var button = Brand.Button(text, primary ? Brand.Weight.Primary : Brand.Weight.Secondary);
        button.MinWidth = 0;
        button.MinHeight = 24;
        button.Padding = new Thickness(10, 2, 10, 2);
        button.Margin = new Thickness(0, 0, 6, 2);
        return button;
    }
}

/// <summary>
/// The update toast: a small themed window at the bottom right of Revit's main window. It is shown without taking the
/// focus (typing in Revit carries on), owned by Revit (so it stays with it, never above other programs), and closes by
/// itself after 15 seconds unless the pointer or the keyboard is on it or its details are open.
/// </summary>
internal sealed class UpdateToast : Window
{
    private static UpdateToast? _open;

    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(15) };

    private UpdateToast(UpdateNotice.Notice notice)
    {
        Title = notice.Title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false;
        Topmost = false;
        Left = -10000;
        Top = -10000;
        Brand.Prepare(this);
        AutomationProperties.SetName(this, notice.Title);

        var title = Brand.Text(notice.Title, Brand.K.Text, 14, bold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
        var later = Brand.Button(notice.Kind == UpdateNotice.Kind.Available ? "Later" : "Close");
        later.MinWidth = 72;
        AutomationProperties.SetHelpText(later, "Closes this message. The Update button on the ribbon still shows the version.");
        later.Click += (_, _) =>
        {
            if (notice.Kind == UpdateNotice.Kind.Available) UpdateNotice.Dismiss();
            Close();
        };

        var head = new DockPanel { LastChildFill = true };
        if (Brand.Logo is { } logo)
        {
            var image = new Image { Source = logo, Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(image, "Sketch ProMax");
            DockPanel.SetDock(image, Dock.Left);
            head.Children.Add(image);
        }
        head.Children.Add(title);

        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        body.Children.Add(head);
        // The first line of the notes always; the rest, and the next step, under Show details.
        var first = notice.Lines.Take(1).ToList();
        foreach (var l in first) body.Children.Add(Line(l));
        var details = new StackPanel { Visibility = Visibility.Collapsed };
        foreach (var l in notice.Lines.Skip(1)) details.Children.Add(Line(l));
        var next = Brand.Text(notice.Next, Brand.K.Muted, 12);
        next.Margin = new Thickness(0, 6, 0, 0);
        details.Children.Add(next);
        body.Children.Add(details);

        var more = Brand.Button("Show details");
        more.MinWidth = 0;
        more.SetResourceReference(StyleProperty, "SpmSecondaryButton");
        AutomationProperties.SetName(more, "Show details");
        more.Click += (_, _) =>
        {
            var open = details.Visibility != Visibility.Visible;
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            more.Content = open ? "Hide details" : "Show details";
            AutomationProperties.SetName(more, (string)more.Content);
            if (open) _hide.Stop(); // reading: it stays until closed
        };

        var notes = Brand.Button("What's new", notice.Kind == UpdateNotice.Kind.Available ? Brand.Weight.Secondary : Brand.Weight.Primary);
        notes.Click += (_, _) =>
        {
            UpdateNotice.WhatsNew();
            Close();
        };
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in new[] { more, later, notes }) { b.Margin = new Thickness(6, 0, 0, 4); row.Children.Add(b); }
        if (notice.Kind == UpdateNotice.Kind.Available)
        {
            // The one primary action. Never IsDefault: the toast is never focused by itself, and a stray Enter must not update.
            var update = Brand.Button("Update now", Brand.Weight.Primary);
            update.Margin = new Thickness(6, 0, 0, 4);
            update.Click += (_, _) => UpdateNotice.UpdateNow();
            row.Children.Add(update);
        }
        body.Children.Add(row);

        var frame = new Border { BorderThickness = new Thickness(1), Child = body };
        frame.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        frame.SetResourceReference(Border.BackgroundProperty, Brand.K.Bg);
        Content = frame;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        _hide.Tick += (_, _) =>
        {
            // Not while it is being used.
            if (IsMouseOver || IsKeyboardFocusWithin) return;
            _hide.Stop();
            Close();
        };
        SizeChanged += (_, _) => Place();
        Closed += (_, _) =>
        {
            _hide.Stop();
            if (_open == this) _open = null;
        };
    }

    private static TextBlock Line(string text)
    {
        var block = Brand.Text("• " + text, Brand.K.Text, 12);
        block.Margin = new Thickness(0, 6, 0, 0);
        return block;
    }

    /// <summary>Shows the toast for <paramref name="notice"/> in place of any other. UI thread.</summary>
    public static void Open(UpdateNotice.Notice notice)
    {
        CloseOpen();
        var toast = new UpdateToast(notice);
        _open = toast;
        toast.Show();
        toast._hide.Start();
    }

    public static void CloseOpen()
    {
        try { _open?.Close(); }
        catch (InvalidOperationException) { }
        _open = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    /// <summary>Bottom right of Revit's main window, above its status bar; of the work area when Revit's window is unknown.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        double right = area.Right, bottom = area.Bottom;
        try
        {
            if (App.MainWindow != IntPtr.Zero && GetWindowRect(App.MainWindow, out var r) && r.Right > r.Left
                && PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                var corner = target.TransformFromDevice.Transform(new Point(r.Right, r.Bottom));
                right = corner.X;
                bottom = corner.Y;
            }
        }
        catch (Exception) { /* the work area's corner */ }
        Left = Math.Max(area.Left, right - ActualWidth - 24);
        Top = Math.Max(area.Top, Math.Min(bottom, area.Bottom) - ActualHeight - 40);
    }
}
