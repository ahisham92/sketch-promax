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

    public Task<T> InvokeAsync<T>(Func<UIApplication, T> work)
    {
        if (_event is null) throw new InvalidOperationException("RevitDispatcher not initialized.");

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(app =>
        {
            try { tcs.SetResult(work(app)); }
            catch (Exception ex) { tcs.SetException(ex); }
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
