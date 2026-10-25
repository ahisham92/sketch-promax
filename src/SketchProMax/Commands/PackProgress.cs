using System.Windows;
using System.Windows.Controls;
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
    /// <summary>How the project pack prefixes its progress lines (the window shows them without it).</summary>
    public const string ProjectPackProgress = "Project pack: ";

    private Dispatcher? _dispatcher;
    private Window? _window;

    // Set by Dispose. Guarded with _dispatcher by _gate, so a close asked for before the window's thread is up is never lost.
    private readonly object _gate = new();
    private bool _closeRequested;

    private PackProgress() { }

    public static PackProgress Show(string title, string headline)
    {
        var progress = new PackProgress();
        // Not disposed: the thread may still set it after Show stopped waiting.
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                lock (progress._gate)
                {
                    if (progress._closeRequested) return;
                    progress._dispatcher = Dispatcher.CurrentDispatcher;
                }
                // This thread is not Revit's, but an exception escaping it would still end Revit: swallow it, close the window.
                progress._dispatcher.UnhandledException += (_, e) =>
                {
                    e.Handled = true;
                    try { progress._window?.Close(); } catch (Exception) { }
                };
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
                        line.Text = p.StartsWith(ProjectPackProgress, StringComparison.Ordinal) ? p.Substring(ProjectPackProgress.Length) : p;
                };
                timer.Start();
                window.Closed += (_, _) => { timer.Stop(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
                window.Show();
            }
            catch (Exception) { progress._window = null; }
            finally { ready.Set(); }
            if (progress._dispatcher is null) return;
            try { Dispatcher.Run(); }
            catch (Exception) { /* never take Revit down with a progress window */ }
        })
        { IsBackground = true, Name = "Sketch ProMax progress" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        return progress;
    }

    public void Dispose()
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            _closeRequested = true;
            dispatcher = _dispatcher;
        }
        // Null: the thread is not up yet, and sees _closeRequested before it shows anything.
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
