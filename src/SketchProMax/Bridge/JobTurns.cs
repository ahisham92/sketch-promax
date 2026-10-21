namespace SketchProMax.Bridge;

/// <summary>
/// The one queue the chat's parallel jobs (Chat.JobBoard) take turns in to change the model. Revit allows one change at a
/// time: a job's model-changing call takes a ticket and waits until it is at the front, then runs and gives the ticket
/// back when its Revit job ends. Reads take no ticket (they run as soon as Revit is free), so they never wait behind a
/// queue of changes. First come, first served; a caller that goes away leaves the queue. No Revit and no WPF here (the
/// unit tests compile this file on its own); thread-safe.
/// </summary>
internal sealed class JobTurns
{
    /// <summary>One call waiting for (or holding) its turn.</summary>
    internal sealed class Ticket
    {
        public string Job { get; init; } = "";
        public string Tool { get; init; } = "";
        internal readonly TaskCompletionSource<bool> Front = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly List<Ticket> _line = [];

    /// <summary>The queue every job's change goes through (one per Revit session).</summary>
    public static JobTurns Shared { get; } = new();

    /// <summary>Raised (outside the lock, on the caller's thread) whenever a ticket joins, moves up or leaves.</summary>
    public event Action? Changed;

    /// <summary>A change of <paramref name="job"/> joins the end of the queue.</summary>
    public Ticket Enter(string job, string tool)
    {
        var ticket = new Ticket { Job = job, Tool = tool };
        lock (_gate)
        {
            _line.Add(ticket);
            if (_line.Count == 1) ticket.Front.TrySetResult(true);
        }
        Changed?.Invoke();
        return ticket;
    }

    /// <summary>The ticket's change ended, or its caller went away: the next one moves to the front. Safe to call twice.</summary>
    public void Leave(Ticket ticket)
    {
        lock (_gate)
        {
            if (!_line.Remove(ticket)) return;
            if (_line.Count > 0) _line[0].Front.TrySetResult(true);
        }
        Changed?.Invoke();
    }

    /// <summary>1 when it is the ticket's turn, 2 when one change is ahead, …; 0 once it left.</summary>
    public int Position(Ticket ticket)
    {
        lock (_gate) return _line.IndexOf(ticket) + 1;
    }

    /// <summary>The queue as it stands, front first: each change's job and tool.</summary>
    public List<(string Job, string Tool)> Snapshot()
    {
        lock (_gate) return _line.Select(t => (t.Job, t.Tool)).ToList();
    }

    /// <summary>
    /// Waits until it is the ticket's turn. False when <paramref name="ct"/> ended the wait first or it took longer than
    /// <paramref name="timeout"/>; the ticket is then out of the queue.
    /// </summary>
    public async Task<bool> WaitAsync(Ticket ticket, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var gone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (limit.Token.Register(() => gone.TrySetResult(false)))
        {
            if (await Task.WhenAny(ticket.Front.Task, gone.Task).ConfigureAwait(false) == ticket.Front.Task) return true;
        }
        Leave(ticket);
        return false;
    }
}
