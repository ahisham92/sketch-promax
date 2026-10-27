namespace SketchProMax.Bridge;

/// <summary>
/// The applier: the one line the chat's parallel jobs (Chat.JobBoard) take turns in for Revit. Revit runs one call at a
/// time on its own thread, and while a call runs Revit can't redraw or take a click. Every call of a job (a change or a
/// read) takes a ticket and waits until it is at the front, then runs and gives the ticket back when its Revit job ends,
/// so the jobs go one call each in the order they asked, none can starve another, and a change never races a read. After
/// each call Revit gets a breather (<see cref="Breather"/>) before the next one starts, so it redraws and answers the user
/// between the jobs' calls instead of running them back to back. First come, first served; a caller that goes away leaves
/// the line. No Revit and no WPF here (the unit tests compile this file on its own); thread-safe.
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

    /// <summary>The line every job's call goes through (one per Revit session).</summary>
    public static JobTurns Shared { get; } = new();

    /// <summary>Raised (outside the lock, on the caller's thread) whenever a ticket joins, moves up or leaves.</summary>
    public event Action? Changed;

    /// <summary>A call of <paramref name="job"/> joins the end of the line.</summary>
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

    // When the next call may start (UTC ticks): the breather after the last call.
    private long _readyAt;

    /// <summary>
    /// How long Revit is left alone after a call that ran <paramref name="ran"/>: a tenth of it, at least a quarter of a
    /// second (time to redraw and take a click) and at most two seconds (the user gets their turn after a long call).
    /// </summary>
    public static TimeSpan Breather(TimeSpan ran) =>
        TimeSpan.FromMilliseconds(Math.Max(250, Math.Min(2000, ran.TotalMilliseconds / 10)));

    /// <summary>A call that ran <paramref name="ran"/> ended: the next one waits for its breather.</summary>
    public void Rested(TimeSpan ran)
    {
        var ready = DateTime.UtcNow.Add(Breather(ran)).Ticks;
        lock (_gate) _readyAt = Math.Max(_readyAt, ready);
    }

    /// <summary>What is left of the breather (zero when the next call may start).</summary>
    public TimeSpan RestLeft
    {
        get
        {
            long left;
            lock (_gate) left = _readyAt - DateTime.UtcNow.Ticks;
            return left > 0 ? new TimeSpan(left) : TimeSpan.Zero;
        }
    }
}
