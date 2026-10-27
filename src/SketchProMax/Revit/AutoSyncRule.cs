namespace SketchProMax.Revit;

/// <summary>
/// The auto-sync rule (Settings > Chat): when Claude's work has brought a workshared model to the set number of changes
/// since its last synchronize (250 by default), every chat job is paused, the model is synchronized with central, and the
/// work carries on where it stopped. A change is an element added, changed or deleted (each element once, however often it
/// is touched), counted from Revit's DocumentChanged like Lifeline's change file; synchronizing (by anyone's hand) starts
/// the count again. A step bigger than what is left is split across syncs at an item boundary (ItemBatch); one code call
/// can't be split, so Claude is asked to keep each under what is left. The user can turn the rule off in Settings, or say
/// in the chat to skip it for a task (<see cref="Skip"/>: until their next message to the main chat, at most two hours).
/// No Revit and no WPF here (the unit tests compile this file on its own); thread-safe. Revit's side is AutoSync.cs.
/// </summary>
internal sealed class AutoSyncRule
{
    /// <summary>The default number of changes, and the range Settings allows.</summary>
    public const int DefaultLimit = 250, MinLimit = 20, MaxLimit = 5000;

    /// <summary>The longest a skip lasts when no new message ends it first.</summary>
    public static readonly TimeSpan SkipFor = TimeSpan.FromHours(2);

    /// <summary>Elements kept per model: past this the count still goes up, ids are no longer told apart.</summary>
    private const int IdCap = 200_000;

    private sealed class Model
    {
        public readonly HashSet<long> Ids = [];
        public int Extra;
        public bool Shared;
        public int Count => Ids.Count + Extra;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Model> _models = new(StringComparer.OrdinalIgnoreCase);
    private bool _on = true;
    private int _limit = DefaultLimit;
    private (int Task, DateTime Until, string Why)? _skip;

    /// <summary>The rule of this Revit session.</summary>
    public static AutoSyncRule Shared { get; } = new();

    /// <summary>The number Settings stored, kept in range (0 or unreadable: the default).</summary>
    public static int Clamp(int limit) => limit <= 0 ? DefaultLimit : Math.Max(MinLimit, Math.Min(MaxLimit, limit));

    /// <summary>Settings were read: on or off, and after how many changes.</summary>
    public void Configure(bool on, int limit)
    {
        lock (_gate) { _on = on; _limit = Clamp(limit); }
    }

    public bool On { get { lock (_gate) return _on; } }
    public int Limit { get { lock (_gate) return _limit; } }

    /// <summary>
    /// Revit reported changed elements in <paramref name="model"/>; <paramref name="shared"/>: it is a local copy of a
    /// central model (workshared, not detached), the only kind that can be synchronized.
    /// </summary>
    public void Changed(string model, bool shared, IEnumerable<long> ids)
    {
        lock (_gate)
        {
            if (!_models.TryGetValue(model, out var m)) _models[model] = m = new Model();
            m.Shared = shared;
            foreach (var id in ids)
                if (m.Ids.Count < IdCap) m.Ids.Add(id);
                else if (!m.Ids.Contains(id)) m.Extra++;
        }
    }

    /// <summary>The model was synchronized (or closed): its count starts again.</summary>
    public void Synced(string model)
    {
        lock (_gate) _models.Remove(model);
    }

    /// <summary>Changes in <paramref name="model"/> since its last synchronize (that Sketch ProMax saw).</summary>
    public int Count(string model)
    {
        lock (_gate) return _models.TryGetValue(model, out var m) ? m.Count : 0;
    }

    /// <summary>How many more changes before the next sync (the limit when the rule is off or skipped: nothing is waiting).</summary>
    public int Left(string model)
    {
        lock (_gate) return Math.Max(0, _limit - (_models.TryGetValue(model, out var m) ? m.Count : 0));
    }

    /// <summary>
    /// True when <paramref name="model"/> is due a sync before more work: the rule is on and not skipped for the task
    /// (<paramref name="task"/>: the main chat's task number now), the model is a local copy of a central model, and it has
    /// reached the limit.
    /// </summary>
    public bool Due(string model, int task, DateTime now)
    {
        lock (_gate)
        {
            if (!_on || SkippedLocked(task, now)) return false;
            return _models.TryGetValue(model, out var m) && m.Shared && m.Count >= _limit;
        }
    }

    /// <summary>The user said to skip the rule for the task (<paramref name="task"/>: the main chat's task number now).</summary>
    public void Skip(int task, DateTime now, string why)
    {
        lock (_gate) _skip = (task, now + SkipFor, why);
    }

    /// <summary>The rule applies again (the user changed their mind, or the skipped work is done).</summary>
    public void Resume()
    {
        lock (_gate) _skip = null;
    }

    /// <summary>True while a skip holds: the same main chat task and not past <see cref="SkipFor"/>.</summary>
    public bool Skipped(int task, DateTime now)
    {
        lock (_gate) return SkippedLocked(task, now);
    }

    private bool SkippedLocked(int task, DateTime now)
    {
        if (_skip is not { } s) return false;
        if (s.Task == task && now < s.Until) return true;
        _skip = null;
        return false;
    }

    /// <summary>For the auto_sync tool and Claude: the rule as it stands for <paramref name="model"/>, in plain words.</summary>
    public string Describe(string? model, int task, DateTime now)
    {
        bool on, skipped, shared;
        int limit, count;
        lock (_gate)
        {
            on = _on;
            limit = _limit;
            skipped = SkippedLocked(task, now);
            var m = model is null ? null : _models.TryGetValue(model, out var x) ? x : null;
            shared = m?.Shared ?? false;
            count = m?.Count ?? 0;
        }
        if (!on) return "Auto sync is off (Settings > Chat): Sketch ProMax doesn't synchronize by itself.";
        var rule = $"Auto sync is on: after every {limit} changes Sketch ProMax pauses every job, synchronizes with central, and carries on.";
        if (skipped) return rule + " The user asked to skip it for this task: no pause, no sync and no split until their next message.";
        if (model is null) return rule;
        if (!shared) return rule + $" \"{model}\" isn't a local copy of a central model, so it is never synchronized.";
        return rule + $" \"{model}\": {count} changes since the last sync, {Math.Max(0, limit - count)} left before the next one.";
    }
}
