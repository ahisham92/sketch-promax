namespace SketchProMax.Update;

public enum UpdateStage
{
    Idle, Checking, UpToDate, Downloading, Verifying, Unpacking, Applying, Updated, RestartNeeded, Failed, Cancelled,
    /// <summary>A newer version is out, but automatic updates are off: nothing was downloaded.</summary>
    Available,
}

/// <summary>An immutable picture of an update in progress, safe to hand to any thread.</summary>
public sealed record UpdateSnapshot(UpdateStage Stage, string? Version, long Bytes, long? Total, string? Detail, DateTime Changed)
{
    public bool IsFinal => Stage is UpdateStage.UpToDate or UpdateStage.Updated or UpdateStage.RestartNeeded
        or UpdateStage.Failed or UpdateStage.Cancelled or UpdateStage.Available;

    public int? Percent => Total is > 0 ? (int)Math.Min(100, Bytes * 100 / Total.Value) : null;

    public static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}

/// <summary>
/// The state of one update run. The updater reports into it from its background thread; the update window and the
/// ribbon read snapshots of it. <see cref="Changed"/> is raised on the reporting thread, so a window must marshal to its
/// own Dispatcher.
/// </summary>
public sealed class UpdateProgress
{
    private readonly object _gate = new();
    private UpdateSnapshot _state = new(UpdateStage.Idle, null, 0, null, null, DateTime.UtcNow);
    private int _lastPercent = -1;
    private DateTime _lastBytesReport;

    /// <summary>The run the ribbon and the update window show: the latest one in this core.</summary>
    public static UpdateProgress? Current { get; set; }

    /// <summary>True once the update window has been shown for this run, so the ribbon shows its state too.</summary>
    public volatile bool Surfaced;

    /// <summary>True when the user asked for it (Check now), so even "nothing new" is shown.</summary>
    public bool Interactive { get; init; }

    /// <summary>Cancel while checking or downloading. Once unpacking has started the update finishes.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public event Action<UpdateSnapshot>? Changed;

    public UpdateSnapshot Snapshot { get { lock (_gate) return _state; } }

    public void Report(UpdateStage stage, string? detail = null, string? version = null)
    {
        UpdateSnapshot now;
        lock (_gate)
        {
            // A final state is final; only a later switch-over result may replace "applying".
            if (_state.IsFinal && stage != UpdateStage.Updated && stage != UpdateStage.RestartNeeded) return;
            now = _state = _state with
            {
                Stage = stage, Detail = detail, Version = version ?? _state.Version, Changed = DateTime.UtcNow,
            };
        }
        Changed?.Invoke(now);
    }

    /// <summary>Download progress. Raised at most for each new percent, or every quarter second when the size is unknown.</summary>
    public void ReportBytes(long bytes, long? total)
    {
        UpdateSnapshot now;
        lock (_gate)
        {
            _state = _state with { Bytes = bytes, Total = total };
            var percent = _state.Percent ?? -1;
            var due = percent >= 0 ? percent != _lastPercent : DateTime.UtcNow - _lastBytesReport > TimeSpan.FromMilliseconds(250);
            if (!due) return;
            _lastPercent = percent;
            _lastBytesReport = DateTime.UtcNow;
            now = _state;
        }
        Changed?.Invoke(now);
    }

    /// <summary>A short line for the ribbon's Updates button, e.g. "Downloading\n45%".</summary>
    public static string Short(UpdateSnapshot s) => s.Stage switch
    {
        UpdateStage.Checking => "Checking…",
        UpdateStage.Downloading => s.Percent is { } p ? $"Downloading\n{p}%" : $"Downloading\n{UpdateSnapshot.Megabytes(s.Bytes)}",
        UpdateStage.Verifying => "Verifying…",
        UpdateStage.Unpacking => "Unpacking…",
        UpdateStage.Applying => "Applying…",
        UpdateStage.Updated => $"Updated\nto {s.Version}",
        UpdateStage.RestartNeeded => "Restart\nto finish",
        UpdateStage.UpToDate => "Up to date",
        UpdateStage.Failed => "Update\nfailed",
        UpdateStage.Cancelled => "Update\ncancelled",
        UpdateStage.Available => $"Update\n{s.Version}",
        _ => "",
    };
}
