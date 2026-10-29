using System.Globalization;

namespace SketchProMax.Revit;

/// <summary>
/// The sums behind <see cref="UiCost"/>, without Revit (the tests compile it): how long each piece of Sketch ProMax's
/// background work held Revit's thread, a line for a single slow run, and a line every <see cref="ReportEvery"/> with
/// the total, so "is Sketch ProMax making Revit heavy?" is answered by the activity log.
/// </summary>
internal sealed class UiCostTally
{
    /// <summary>A single run longer than this is written on its own (once a minute per kind of work at most).</summary>
    public const int SlowMs = 250;

    /// <summary>How often the total is written.</summary>
    public static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(10);

    /// <summary>A total under this is not worth a line.</summary>
    public const double ReportFromMs = 1000;

    private sealed class Sum { public double Ms; public int Runs; public double Worst; }

    private readonly Dictionary<string, Sum> _sums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastSlow = new(StringComparer.Ordinal);
    private DateTime _since;

    public UiCostTally(DateTime start) => _since = start;

    /// <summary>Counts one run; the line for the activity log when it was slow, else null.</summary>
    public string? Add(string what, double ms, DateTime now)
    {
        if (!_sums.TryGetValue(what, out var sum)) _sums[what] = sum = new Sum();
        sum.Ms += ms;
        sum.Runs++;
        sum.Worst = Math.Max(sum.Worst, ms);
        if (ms < SlowMs) return null;
        if (_lastSlow.TryGetValue(what, out var last) && now - last < TimeSpan.FromMinutes(1)) return null;
        _lastSlow[what] = now;
        return $"Sketch ProMax held Revit for {Ms(ms)} ({what})";
    }

    /// <summary>The line for the time since the last one, when it is due and worth writing (then the sums start again).</summary>
    public string? Due(DateTime now)
    {
        var span = now - _since;
        if (span < ReportEvery) return null;
        var total = _sums.Values.Sum(s => s.Ms);
        var parts = _sums.Where(p => p.Value.Ms >= 1).OrderByDescending(p => p.Value.Ms)
            .Select(p => $"{p.Key} {Ms(p.Value.Ms)} in {p.Value.Runs} run{(p.Value.Runs == 1 ? "" : "s")} (longest {Ms(p.Value.Worst)})")
            .ToList();
        _sums.Clear();
        _since = now;
        if (total < ReportFromMs) return null;
        var share = (total / span.TotalMilliseconds * 100).ToString("0.0", CultureInfo.InvariantCulture);
        return $"Sketch ProMax used {Ms(total)} of Revit's time in the last {Math.Round(span.TotalMinutes)} min ({share}%): {string.Join(", ", parts)}";
    }

    private static string Ms(double ms) =>
        ms >= 1000 ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s" : Math.Round(ms).ToString(CultureInfo.InvariantCulture) + " ms";
}
