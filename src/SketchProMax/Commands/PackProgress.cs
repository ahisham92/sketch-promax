using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SketchProMax.Bridge;

namespace SketchProMax.Commands;

/// <summary>
/// A small progress window for a ribbon job that runs on Revit's thread (Export for Claude). Revit's thread is busy
/// for the whole run, so the window lives on its own UI thread: it keeps painting, shows the job's progress line, and
/// its Stop button asks the job to stop at its next check (the same as cancel_job). Nothing here touches the Revit API.
/// </summary>
internal sealed class PackProgress : IDisposable
{
    private Dispatcher? _dispatcher;
    private Window? _window;

    private PackProgress() { }

    public static PackProgress Show(string title, string headline)
    {
        var progress = new PackProgress();
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                progress._dispatcher = Dispatcher.CurrentDispatcher;
                var line = new TextBlock { Text = "Starting…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12), MinHeight = 34 };
                var bar = new ProgressBar { IsIndeterminate = true, Height = 6 };
                var stop = new Button { Content = "Stop", Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
                stop.Click += (_, _) =>
                {
                    if (Jobs.Running is { } job) job.CancelRequested = true;
                    stop.IsEnabled = false;
                    line.Text = "Stopping after the current step…";
                };
                var panel = new StackPanel { Margin = new Thickness(18) };
                panel.Children.Add(new TextBlock { Text = headline, FontSize = 14, FontWeight = FontWeights.SemiBold });
                panel.Children.Add(line);
                panel.Children.Add(bar);
                panel.Children.Add(stop);
                var window = new Window
                {
                    Title = title, Content = panel, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ShowInTaskbar = false,
                    Background = SystemColors.WindowBrush,
                };
                progress._window = window;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                timer.Tick += (_, _) =>
                {
                    if (stop.IsEnabled && Jobs.Running?.Progress() is { } p)
                        line.Text = p.StartsWith("Export for Claude: ", StringComparison.Ordinal) ? p.Substring(19) : p;
                };
                timer.Start();
                window.Closed += (_, _) => { timer.Stop(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
                window.Show();
            }
            catch (Exception) { progress._window = null; }
            finally { ready.Set(); }
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Sketch ProMax progress" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        return progress;
    }

    public void Dispose()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_window is { } w) w.Close();
                else Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }));
        }
        catch (Exception) { }
    }
}
