using SketchProMax.Bridge;

namespace SketchProMax.Tools;

/// <summary>
/// Stop points for the heavy single-operation tools (one big transaction or loop), so cancel_job and Cancel in Revit
/// take effect between items instead of at the end. Two shapes, matching what the bridge already does:
/// <list type="bullet">
/// <item>Read-only scans and work between committed steps call <see cref="ThrowIfRequested"/>: the bridge turns the
/// exception into "Stopped on request (cancel_job)..." and keeps what was already committed.</item>
/// <item>A loop inside one open transaction calls <see cref="Stop"/> per item and breaks out when it says so; the tool
/// commits what it did (still one Undo step) and puts <see cref="Note"/> in its result, so nothing finished is lost.</item>
/// </list>
/// Checking is cheap (two volatile reads), so it is done every item; progress goes to the job every tenth item.
/// </summary>
internal static class CancelPoint
{
    public const string Stopped = "Stopped on request (cancel_job)";

    public static bool Requested => Jobs.CancelRequested;

    /// <summary>The field test's hook: told each item <see cref="Stop"/> checks (what, index), so it can ask for a stop part-way. Null otherwise.</summary>
    internal static Action<string, int>? TestItem;

    /// <summary>Throws the bridge's cancel exception when a stop was asked for.</summary>
    public static void ThrowIfRequested(string? more = null)
    {
        if (Jobs.CancelRequested) throw new OperationCanceledException(more is null ? Stopped + "." : $"{Stopped}. {more}");
    }

    /// <summary>
    /// For item <paramref name="index"/> (0-based) of <paramref name="count"/> in a loop: reports progress on long runs
    /// and returns true when the loop should stop now.
    /// </summary>
    public static bool Stop(string what, int index, int count)
    {
        if (count > 20 && index > 0 && index % 10 == 0) Jobs.Report($"{what}: {index} of {count}");
        TestItem?.Invoke(what, index);
        return Jobs.CancelRequested;
    }

    /// <summary>The result's note after a loop stopped early (null when it ran to the end).</summary>
    public static string? Note(bool stopped, int reached, int count) =>
        stopped ? $"{Stopped} after {reached} of {count}; the rest was not done. What was done is kept (one Undo step)." : null;
}
