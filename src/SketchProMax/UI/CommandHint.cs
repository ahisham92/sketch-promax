using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SketchProMax.UI;

/// <summary>
/// A small note in the corner of the screen before a command that froze Revit before (Revit.HeavyCommands): it never
/// asks, never takes the focus and never stops the command (the user, 30 Oct 2026: "don't block any option, just give me
/// a hint"). It goes away by itself after a few seconds, or on a click; "Don't show again" stops it for that command.
/// </summary>
internal sealed class CommandHint : Window
{
    private static CommandHint? _shown;

    public static void Show(string main, string details, Action dontShowAgain)
    {
        try
        {
            _shown?.Close();
            _shown = new CommandHint(main, details, dontShowAgain);
            _shown.Show();
        }
        catch (Exception) { /* a hint is never worth an error */ }
    }

    private CommandHint(string main, string details, Action dontShowAgain)
    {
        Title = "Sketch ProMax";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 360;
        Topmost = true;
        ShowActivated = false; // Revit keeps the focus: the command runs as if nothing were here
        Focusable = false;
        Brand.Prepare(this);
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, Brand.K.InputBorder);

        var page = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        var head = Brand.Text(main, Brand.K.Text, 12, bold: true);
        head.TextWrapping = TextWrapping.Wrap;
        page.Children.Add(head);
        var body = Brand.Text(details, Brand.K.Muted, 11);
        body.TextWrapping = TextWrapping.Wrap;
        body.Margin = new Thickness(0, 4, 0, 6);
        page.Children.Add(body);
        var never = Brand.Button("Don't show this again");
        never.HorizontalAlignment = HorizontalAlignment.Right;
        never.Click += (_, _) => { dontShowAgain(); Close(); };
        page.Children.Add(never);
        Content = page;
        AutomationProperties.SetLiveSetting(head, AutomationLiveSetting.Polite);

        MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button) Close(); };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); if (_shown == this) _shown = null; };
    }
}
