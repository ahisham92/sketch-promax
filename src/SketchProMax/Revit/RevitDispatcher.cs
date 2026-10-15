using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SketchProMax.Revit;

/// <summary>
/// The Revit API may only be called on Revit's main thread inside a valid API context.
/// The agent loop runs on a background thread (it awaits HTTP calls to Claude), so every
/// Revit operation is queued here and executed through a single <see cref="ExternalEvent"/>.
/// Callers get a Task that completes when Revit has run the work.
///
/// Revit only delivers an external event when its message loop turns over, so with no mouse or keyboard activity
/// a request could sit queued until the user clicked in Revit. To avoid that, queued work is also run from the Idling
/// event (kept firing while work waits), and a timer nudges Revit's main window with an empty message until the
/// queue is empty.
/// </summary>
public sealed class RevitDispatcher : IExternalEventHandler
{
    private readonly ConcurrentQueue<Action<UIApplication>> _queue = new();
    private ExternalEvent? _event;
    private IntPtr _mainWindow;
    private Timer? _wake;
    private volatile bool _running;

    /// <summary>True while Revit is running queued work (a tool call), as opposed to work merely waiting its turn.</summary>
    public bool IsRunning => _running;

    /// <summary>Must be called from a valid Revit API context (e.g. OnStartup).</summary>
    public void Initialize() => _event = ExternalEvent.Create(this);

    /// <summary>Like <see cref="Initialize()"/>, and also keeps queued work moving while Revit sits idle.</summary>
    public void Initialize(UIControlledApplication application)
    {
        Initialize();
        try { _mainWindow = application.MainWindowHandle; }
        catch (Exception) { _mainWindow = IntPtr.Zero; }
        application.Idling += OnIdling;
        _wake = new Timer(_ => Nudge(), null, 250, 250);
    }

    public Task<T> InvokeAsync<T>(Func<UIApplication, T> work) => InvokeAsync(work, CancellationToken.None);

    /// <summary>
    /// Queues work for Revit's main thread. Cancelling <paramref name="ct"/> before Revit gets to it (for example when
    /// the caller has gone away) means the work never runs; once it has started it runs to the end.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<UIApplication, T> work, CancellationToken ct)
    {
        if (_event is null) throw new InvalidOperationException("RevitDispatcher not initialized.");

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled();
            return tcs.Task;
        }
        var registration = ct.CanBeCanceled ? ct.Register(() => tcs.TrySetCanceled()) : default;
        _queue.Enqueue(app =>
        {
            // Cancelled while it waited: the caller has its answer already, so don't run it late.
            registration.Dispose();
            if (tcs.Task.IsCompleted) return;
            try { tcs.TrySetResult(work(app)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });

        // Pending (an earlier Raise() will drain this too), Denied or TimedOut are not final: the Idling handler and
        // the wake timer still get the queue run, so the request is never failed here.
        _event.Raise();
        Nudge();

        return tcs.Task;
    }

    public void Execute(UIApplication app) => Drain(app);

    public string GetName() => "Sketch ProMax dispatcher";

    private void Drain(UIApplication app)
    {
        // A modal dialog shown by a tool (the approval prompt) pumps messages; don't start other work inside it.
        if (_running) return;
        _running = true;
        try
        {
            // Drain everything queued so far; one Raise() may cover several items.
            while (_queue.TryDequeue(out var work)) work(app);
        }
        finally { _running = false; }
    }

    private void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (_queue.IsEmpty) return;
        if (sender is UIApplication app) Drain(app);
        else _event?.Raise();
        // Keep Idling coming without waiting for user input while anything is still queued.
        if (!_queue.IsEmpty) e.SetRaiseWithoutDelay();
    }

    /// <summary>Wakes Revit's message loop so it notices the queued work, without needing a click or mouse move.</summary>
    private void Nudge()
    {
        if (_queue.IsEmpty || _running || _mainWindow == IntPtr.Zero) return;
        try { PostMessage(_mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception) { /* the window is gone: nothing to wake */ }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
