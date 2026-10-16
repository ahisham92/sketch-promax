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
///
/// Some calls (OpenAndActivateDocument, closing or creating documents) are not allowed inside an event handler such as
/// Idling. Work queued with <see cref="InvokeFromEventAsync{T}(Func{UIApplication, T}, CancellationToken)"/> is only run
/// from <see cref="Execute"/> (the external event's own API context); Idling skips it and raises the event instead.
/// </summary>
public sealed class RevitDispatcher : IExternalEventHandler
{
    private readonly ConcurrentQueue<Action<UIApplication>> _queue = new();
    // Work that must run from ExternalEvent.Execute; the second action fails the caller when Revit lets go first.
    private readonly ConcurrentQueue<(Action<UIApplication> Run, Action Fail)> _eventOnly = new();
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

    /// <summary>
    /// The same, from a running Revit (the loader starts the core on Revit's first idle moment, and again after an update
    /// is switched to). Call from a valid API context.
    /// </summary>
    public void Initialize(UIApplication application)
    {
        Initialize();
        try { _mainWindow = application.MainWindowHandle; }
        catch (Exception) { _mainWindow = IntPtr.Zero; }
        // The loader starts the core from inside Revit's Idling event, and Revit refuses a subscription to an event while
        // that event runs ("Can not subscribe to an event during execution of that event"). Then hook Idling from the
        // external event instead, which is its own API context; until then the external event and the wake timer keep
        // queued work moving.
        try
        {
            application.Idling += OnIdling;
            _idlingHooked = true;
        }
        catch (Exception)
        {
            _hookIdlingLater = true;
            try { _event?.Raise(); } catch (Exception) { }
        }
        _wake = new Timer(_ => Nudge(), null, 250, 250);
    }

    private volatile bool _idlingHooked;
    private volatile bool _hookIdlingLater;
    private volatile bool _stopped;

    /// <summary>True while anything is queued or running, so switching to a new core must wait.</summary>
    public bool HasWork => _running || !_queue.IsEmpty || !_eventOnly.IsEmpty;

    /// <summary>
    /// Lets go of Revit: no more Idling, wake timer or external event. Anything that slipped into the queue meanwhile is
    /// run first, so no caller is left waiting. Call from a valid API context.
    /// </summary>
    public void Shutdown(UIApplication application)
    {
        _stopped = true;
        _hookIdlingLater = false;
        // Revit may refuse this too when the loader switches versions from inside Idling; the handler then does nothing.
        if (_idlingHooked)
        {
            try { application.Idling -= OnIdling; _idlingHooked = false; } catch (Exception) { }
        }
        Drain(application, fromEvent: false);
        // Work that may only run from the external event can't run here (this may be an event handler): fail it.
        while (_eventOnly.TryDequeue(out var pending)) pending.Fail();
        _wake?.Dispose();
        _wake = null;
        var ev = _event;
        _event = null;
        try { ev?.Dispose(); } catch (Exception) { }
    }

    public Task<T> InvokeAsync<T>(Func<UIApplication, T> work) => InvokeAsync(work, CancellationToken.None);

    /// <summary>
    /// Queues work for Revit's main thread. Cancelling <paramref name="ct"/> before Revit gets to it (for example when
    /// the caller has gone away) means the work never runs; once it has started it runs to the end.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<UIApplication, T> work, CancellationToken ct) => Enqueue(work, ct, eventOnly: false);

    /// <summary>
    /// Like <see cref="InvokeAsync{T}(Func{UIApplication, T}, CancellationToken)"/>, for work the Revit API only allows
    /// from an external command or event (switching, opening or closing documents): it runs only from
    /// <see cref="Execute"/>, never from the Idling event.
    /// </summary>
    public Task<T> InvokeFromEventAsync<T>(Func<UIApplication, T> work, CancellationToken ct = default) => Enqueue(work, ct, eventOnly: true);

    private Task<T> Enqueue<T>(Func<UIApplication, T> work, CancellationToken ct, bool eventOnly)
    {
        var ev = _event ?? throw new InvalidOperationException("Sketch ProMax is not running in this Revit window (it may have just been updated). Try again.");

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled();
            return tcs.Task;
        }
        var registration = ct.CanBeCanceled ? ct.Register(() => tcs.TrySetCanceled()) : default;
        void Run(UIApplication app)
        {
            // Cancelled while it waited: the caller has its answer already, so don't run it late.
            registration.Dispose();
            if (tcs.Task.IsCompleted) return;
            try { tcs.TrySetResult(work(app)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
        if (eventOnly)
            _eventOnly.Enqueue((Run, () =>
            {
                registration.Dispose();
                tcs.TrySetException(new InvalidOperationException("Sketch ProMax stopped in this Revit window before the work could run."));
            }));
        else _queue.Enqueue(Run);

        // Pending (an earlier Raise() will drain this too), Denied or TimedOut are not final: the Idling handler and
        // the wake timer still get the queue run, so the request is never failed here.
        ev.Raise();
        Nudge();

        return tcs.Task;
    }

    public void Execute(UIApplication app)
    {
        if (_hookIdlingLater && !_stopped)
        {
            _hookIdlingLater = false;
            try
            {
                app.Idling += OnIdling;
                _idlingHooked = true;
            }
            catch (Exception) { /* the external event and the wake timer still move queued work */ }
        }
        Drain(app, fromEvent: true);
    }

    public string GetName() => "Sketch ProMax dispatcher";

    private void Drain(UIApplication app, bool fromEvent)
    {
        // A modal dialog shown by a tool (the approval prompt) pumps messages; don't start other work inside it.
        if (_running) return;
        _running = true;
        try
        {
            // Drain everything queued so far; one Raise() may cover several items. Event-only work runs only from Execute.
            while (true)
            {
                if (_queue.TryDequeue(out var work)) work(app);
                else if (fromEvent && _eventOnly.TryDequeue(out var only)) only.Run(app);
                else break;
            }
        }
        finally { _running = false; }
    }

    private void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (_stopped) return;
        if (_queue.IsEmpty && _eventOnly.IsEmpty) return;
        if (!_queue.IsEmpty)
        {
            if (sender is UIApplication app) Drain(app, fromEvent: false);
            else _event?.Raise();
        }
        // Work that must not run inside an event handler: have the external event pick it up.
        if (!_eventOnly.IsEmpty) _event?.Raise();
        // Keep Idling coming without waiting for user input while anything is still queued.
        if (!_queue.IsEmpty || !_eventOnly.IsEmpty) e.SetRaiseWithoutDelay();
    }

    /// <summary>What is keeping Revit from running queued work right now (a dialog, a busy UI thread, an edit mode).</summary>
    internal RevitBlock.State Blocker() => RevitBlock.Read(_mainWindow);

    /// <summary>
    /// Waits until <paramref name="pickedUp"/> completes (Revit started the work), for at most <paramref name="maxMs"/>.
    /// Returns null once it is picked up (or <paramref name="ct"/> ends the wait), else why Revit didn't take it, worded
    /// for Claude (<see cref="RevitBlock.Explain"/>). A modal dialog that stays open for <paramref name="dialogMs"/> ends
    /// the wait early: Revit runs no add-in work until it is closed, so waiting longer only delays the answer.
    /// The caller drops the queued work when this returns a reason.
    /// </summary>
    internal async Task<string?> WaitForPickupAsync(Task pickedUp, string what, int maxMs, int dialogMs, CancellationToken ct)
    {
        var started = Environment.TickCount;
        var dialogSince = -1;
        while (true)
        {
            var elapsed = unchecked(Environment.TickCount - started);
            if (elapsed >= maxMs) return RevitBlock.Explain(Blocker(), what, (elapsed + 500) / 1000);
            var step = Task.Delay(Math.Min(1000, maxMs - elapsed), ct);
            if (await Task.WhenAny(pickedUp, step).ConfigureAwait(false) == pickedUp || ct.IsCancellationRequested) return null;
            // Our own work (a tool, its approval prompt) disables Revit's window too: only look while nothing of ours runs.
            if (_running) { dialogSince = -1; continue; }
            var state = Blocker();
            if (state.Kind != RevitBlock.Kind.Dialog) { dialogSince = -1; continue; }
            var now = Environment.TickCount;
            if (dialogSince < 0) dialogSince = now;
            else if (unchecked(now - dialogSince) >= dialogMs)
                return RevitBlock.Explain(state, what, (unchecked(now - started) + 500) / 1000);
        }
    }

    /// <summary>Wakes Revit's message loop so it notices the queued work, without needing a click or mouse move.</summary>
    private void Nudge()
    {
        if ((_queue.IsEmpty && _eventOnly.IsEmpty && !_hookIdlingLater) || _running || _mainWindow == IntPtr.Zero) return;
        try { PostMessage(_mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception) { /* the window is gone: nothing to wake */ }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
