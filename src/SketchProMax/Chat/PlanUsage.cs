using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SketchProMax.Chat;

/// <summary>
/// One usage limit of the user's plan as Claude Code last reported it: which window (the 5-hour session, the weekly
/// limit, a per-model weekly limit), how much of it is used (0 to 1; null when the report gave no figure), when it
/// resets, and when that was read.
/// </summary>
internal sealed record UsageWindow(string Key, double? Used, DateTimeOffset? ResetsAt, DateTimeOffset ReadAt);

/// <summary>
/// The plan's usage limits, from the rate_limit_event lines Claude Code writes to its stream-json output with every
/// request on a subscription login (rate_limit_info: status, the limit in force with its utilization and resetsAt, and
/// unifiedWindows with the 5-hour and weekly windows). Nothing is estimated: a window shows only once Claude Code
/// reported it, with the time it was read; a window whose reset time has passed is shown as reset, without a figure,
/// until the next report. Needs no WPF or Revit (the unit tests compile this file).
/// </summary>
internal sealed class PlanUsage
{
    public const string FiveHour = "five_hour", Weekly = "seven_day", WeeklyOpus = "seven_day_opus", WeeklySonnet = "seven_day_sonnet",
        WeeklyIncluded = "seven_day_overage_included", Extra = "overage";

    /// <summary>The share of a limit at which the chat warns (80 %).</summary>
    public const double WarnAt = 0.8;

    /// <summary>The order the windows are listed in: the session first, then the weekly ones.</summary>
    private static readonly string[] Order = [FiveHour, Weekly, WeeklyOpus, WeeklySonnet, WeeklyIncluded, Extra];

    public List<UsageWindow> Windows { get; set; } = [];
    /// <summary>allowed, allowed_warning or rejected (Claude Code's words for the limit in force); "" before a report.</summary>
    public string Status { get; set; } = "";
    /// <summary>The window Claude Code named as the limit in force.</summary>
    public string Binding { get; set; } = "";
    /// <summary>True when the plan's extra usage is being used.</summary>
    public bool UsingExtra { get; set; }
    public DateTimeOffset ReadAt { get; set; }

    /// <summary>The window's name as the pane shows it.</summary>
    public static string Label(string key) => key switch
    {
        FiveHour => "Current session (5-hour limit)",
        Weekly => "Weekly limit, all models",
        WeeklyOpus => "Weekly limit, Opus",
        WeeklySonnet => "Weekly limit, Sonnet",
        WeeklyIncluded => "Weekly limit, included usage",
        Extra => "Extra usage",
        _ => key.Replace('_', ' '),
    };

    /// <summary>The window's short name for the header meter.</summary>
    public static string Short(string key) => key switch
    {
        FiveHour => "5h",
        Weekly => "Week",
        WeeklyOpus => "Opus wk",
        WeeklySonnet => "Sonnet wk",
        WeeklyIncluded => "Incl. wk",
        Extra => "Extra",
        _ => key,
    };

    /// <summary>
    /// The usage in one rate_limit_event line's rate_limit_info, read at <paramref name="now"/>; null when it holds none.
    /// unifiedWindows gives the 5-hour and weekly windows; the limit in force (rateLimitType) adds its own window when
    /// it carries a utilization (a per-model weekly limit is reported only that way).
    /// </summary>
    public static PlanUsage? Parse(JsonElement info, DateTimeOffset now)
    {
        if (info.ValueKind != JsonValueKind.Object) return null;
        var usage = new PlanUsage { Status = Str(info, "status"), Binding = Str(info, "rateLimitType"), ReadAt = now };
        usage.UsingExtra = info.TryGetProperty("isUsingOverage", out var over) && over.ValueKind == JsonValueKind.True;
        if (info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
            foreach (var w in windows.EnumerateObject())
                if (w.Value.ValueKind == JsonValueKind.Object)
                    usage.Windows.Add(new UsageWindow(w.Name, Fraction(w.Value), Time(w.Value, "resetsAt"), now));
        if (usage.Binding.Length > 0 && usage.Windows.All(w => w.Key != usage.Binding))
        {
            var used = Fraction(info);
            var resets = Time(info, "resetsAt");
            if (used is not null || resets is not null) usage.Windows.Add(new UsageWindow(usage.Binding, used, resets, now));
        }
        usage.Windows = Sorted(usage.Windows);
        return usage.Windows.Count > 0 || usage.Status.Length > 0 ? usage : null;
    }

    /// <summary>The usage in a whole stream-json line, when it is a rate_limit_event.</summary>
    public static PlanUsage? ParseLine(string line, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && Str(root, "type") == "rate_limit_event" && root.TryGetProperty("rate_limit_info", out var info)
                ? Parse(info, now) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The latest report over the earlier ones: the windows it gives replace theirs; windows it doesn't mention keep their
    /// last figure (and its read time), so a per-model limit seen once stays listed.
    /// </summary>
    public static PlanUsage Merge(PlanUsage? earlier, PlanUsage latest)
    {
        if (earlier is null) return latest;
        var windows = latest.Windows.ToList();
        windows.AddRange(earlier.Windows.Where(w => latest.Windows.All(l => l.Key != w.Key)));
        return new PlanUsage { Windows = Sorted(windows), Status = latest.Status, Binding = latest.Binding, UsingExtra = latest.UsingExtra, ReadAt = latest.ReadAt };
    }

    private static List<UsageWindow> Sorted(IEnumerable<UsageWindow> windows) =>
        windows.OrderBy(w => Array.IndexOf(Order, w.Key) is var i && i >= 0 ? i : Order.Length).ThenBy(w => w.Key, StringComparer.Ordinal).ToList();

    /// <summary>True once the window's reset time has passed: its last figure no longer holds.</summary>
    public static bool HasReset(UsageWindow w, DateTimeOffset now) => w.ResetsAt is { } at && at <= now;

    /// <summary>The figure to show now: null when none was reported or the window has reset since.</summary>
    public static double? Current(UsageWindow w, DateTimeOffset now) => HasReset(w, now) ? null : w.Used;

    /// <summary>"43%" (rounded down, so 79.6 % doesn't read as 80 %).</summary>
    public static string Percent(double used) => ((int)Math.Floor(used * 100 + 1e-9)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>"Resets in 2 h 10 min (15:30)", "Resets Mon 09:00", or "Reset at 15:30" once passed (local times).</summary>
    public static string ResetText(UsageWindow w, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (w.ResetsAt is not { } at) return "Reset time not reported";
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (at <= now) return $"Reset at {clock}; the new figure shows after the next answer";
        var left = at - now;
        if (left < TimeSpan.FromHours(24))
            return "Resets in " + (left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min") + $" ({clock})";
        return "Resets " + local.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The header meter's words: "5h 43%, 2h 10m left · Week 5%" (only the session and the all-models week). The
    /// session counts down to its reset; once it has reset it says "5h reset" until the next answer brings a new figure.
    /// </summary>
    public string Meter(DateTimeOffset now) => Meter(now, timeLeft: true, narrow: false);

    /// <summary>
    /// The meter's words for the pane's header: without the session's time left (it shows on hover, <see cref="Hover"/>)
    /// when <paramref name="timeLeft"/> is false, and with "Wk" for the week when <paramref name="narrow"/>.
    /// </summary>
    public string Meter(DateTimeOffset now, bool timeLeft, bool narrow)
    {
        var parts = new List<string>();
        foreach (var key in new[] { FiveHour, Weekly })
            if (Windows.FirstOrDefault(w => w.Key == key) is { } w)
                parts.Add(Part(w, now, timeLeft, narrow));
        if (parts.Count == 0 && Windows.FirstOrDefault() is { } first)
            parts.Add(Part(first, now, timeLeft, narrow));
        return string.Join(" · ", parts);
    }

    private static string Part(UsageWindow w, DateTimeOffset now, bool timeLeft, bool narrow)
    {
        var name = narrow && w.Key == Weekly ? "Wk" : Short(w.Key);
        if (HasReset(w, now)) return name + " reset";
        var text = name + " " + (w.Used is { } used ? Percent(used) : "—");
        if (timeLeft && w.Key == FiveHour && w.ResetsAt is { } at) text += ", " + Left(at - now) + " left";
        return text;
    }

    /// <summary>
    /// The meter's hover text: each reported limit on its own line with % used and when it resets ("5h: 8% used, 2h 1m
    /// left (resets 17:30)"), the session first.
    /// </summary>
    public string Hover(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var lines = new List<string>();
        foreach (var w in Windows.OrderBy(w => w.Key == FiveHour ? 0 : w.Key == Weekly ? 1 : 2))
        {
            var line = Label(w.Key) + ": ";
            if (HasReset(w, now)) line += "reset; the new figure shows after the next answer";
            else
            {
                line += w.Used is { } used ? Percent(used) + " used" : "no figure reported";
                if (w.ResetsAt is { } at)
                {
                    var clock = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
                    var reset = ResetText(w, now, zone);
                    line += w.Key == FiveHour && at > now && at - now < TimeSpan.FromHours(24) ? $", {Left(at - now)} left (resets {clock})"
                        : ", " + char.ToLowerInvariant(reset[0]) + reset.Substring(1);
                }
            }
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    /// <summary>"2h 10m", or "25m" under an hour (at least 1m).</summary>
    public static string Left(TimeSpan left) => left.TotalHours >= 1
        ? $"{(int)left.TotalHours}h {left.Minutes}m"
        : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";

    /// <summary>The highest figure shown now (for the meter's colour), or null.</summary>
    public double? Highest(DateTimeOffset now) => Windows.Select(w => Current(w, now)).Where(u => u is not null).Max();

    /// <summary>
    /// The windows that crossed <see cref="WarnAt"/> (or were reached) since <paramref name="warned"/>, each once per reset
    /// (keys "five_hour@1792614600" and "five_hour@…!" for reached); they are added to <paramref name="warned"/>.
    /// </summary>
    public List<(UsageWindow Window, bool Reached)> NewWarnings(ISet<string> warned, DateTimeOffset now)
    {
        var list = new List<(UsageWindow, bool)>();
        foreach (var w in Windows)
        {
            if (Current(w, now) is not { } used || used < WarnAt) continue;
            var key = w.Key + "@" + (w.ResetsAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "");
            var reached = used >= 1 || Status == "rejected" && Binding == w.Key;
            if (reached ? warned.Add(key + "!") | warned.Add(key) : warned.Add(key)) list.Add((w, reached));
        }
        return list;
    }

    /// <summary>The chat's warning line for <paramref name="warnings"/>, naming the jobs running now.</summary>
    public static string Warning(IReadOnlyList<(UsageWindow Window, bool Reached)> warnings, IReadOnlyList<string> runningJobs, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var parts = warnings.Select(x => $"{Label(x.Window.Key)}: {(x.Reached ? "reached" : Percent(x.Window.Used ?? 0) + " used")}, " +
                                         char.ToLowerInvariant(ResetText(x.Window, now, zone)[0]) + ResetText(x.Window, now, zone).Substring(1));
        var reached = warnings.Any(x => x.Reached);
        var text = (reached ? "Your plan's usage limit is reached. " : "Your plan is near its usage limit. ") + string.Join("; ", parts) + ".";
        text += runningJobs.Count > 0
            ? " Running now: " + string.Join(", ", runningJobs) + ". Stop the ones that can wait to save what is left."
            : " No jobs are running.";
        return text;
    }

    // ---- Kept between Revit sessions ----------------------------------------------------------------------------

    /// <summary>The last report, kept in <paramref name="folder"/>\usage.json so the meter shows it (with its time) before the next answer.</summary>
    public static PlanUsage? Load(string folder)
    {
        try
        {
            var file = Path.Combine(folder, "usage.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<PlanUsage>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public void Save(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "usage.json"), JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* shown from the next report instead */ }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Fraction(JsonElement e) =>
        e.TryGetProperty("utilization", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d >= 0 ? d : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
}
