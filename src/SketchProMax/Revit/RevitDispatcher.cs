using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace SketchProMax.Revit;

/// <summary>
/// The Revit API may only be called on Revit's main thread inside a valid API context.
/// The agent loop runs on a background thread (it awaits HTTP calls to Claude), so every
/// Revit operation is queued here and executed through a single <see cref="ExternalEvent"/>.
/// Callers get a Task that completes when Revit has run the work.
/// </summary>
public sealed class RevitDispatcher : IExternalEventHandler
{
    private readonly ConcurrentQueue<Action<UIApplication>> _queue = new();
    private ExternalEvent? _event;

    /// <summary>Must be called from a valid Revit API context (e.g. OnStartup).</summary>
    public void Initialize() => _event = ExternalEvent.Create(this);

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
            // Refused, timed out or cancelled while it waited: the caller has its answer already, so don't run it late.
            registration.Dispose();
            if (tcs.Task.IsCompleted) return;
            try { tcs.TrySetResult(work(app)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });

        var request = _event.Raise();
        if (request is ExternalEventRequest.Denied or ExternalEventRequest.TimedOut)
            tcs.TrySetException(new InvalidOperationException($"Revit refused the request ({request})."));

        return tcs.Task;
    }

    public void Execute(UIApplication app)
    {
        // Drain everything queued so far; one Raise() may cover several items.
        while (_queue.TryDequeue(out var work)) work(app);
    }

    public string GetName() => "Sketch ProMax dispatcher";
}
