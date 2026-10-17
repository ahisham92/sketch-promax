namespace SketchProMax.UI;

/// <summary>
/// The Update button's badge and what clicking it does. Pure: the caller gathers the facts (the running update, a newer
/// version seen or downloaded, restart note, last check, the version whose notes were last opened), so the tests check
/// every case. Each text says only what is known: "Up to date" only after a check that worked, with its time.
/// The button is large, so the texts are two short lines.
/// </summary>
internal static class UpdateBadge
{
    public enum Kind { Working, Ready, Restart, WhatsNew, Failed, Check, UpToDate }

    /// <summary>
    /// How long a successful check counts as "up to date". The update loop checks hourly, so an older check means none
    /// has run lately (Revit just started, or the network is off): the badge then offers a check instead.
    /// </summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(3);

    /// <param name="working">A short line while an update is being looked for, downloaded or unpacked; else null.</param>
    /// <param name="ready">A newer version that is out, downloaded, or waiting to be switched to; else null.</param>
    /// <param name="restart">New buttons or pane parts need a Revit restart.</param>
    /// <param name="seen">The version whose release notes were last opened (or that was running the first time); null if unknown.</param>
    /// <param name="lastError">Why the last check failed, or null when it worked (or none ran).</param>
    public static (string Text, Kind Kind) For(string? working, string? ready, bool restart, Version current, Version? seen,
        DateTime? lastCheck, string? lastError, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(working)) return (working!.Trim(), Kind.Working);
        if (!string.IsNullOrWhiteSpace(ready)) return ($"Update ready\n{ready!.Trim()}", Kind.Ready);
        if (restart) return ("Restart\nRevit", Kind.Restart);
        if (seen is not null && Plain(current) > Plain(seen)) return ("Updated –\nWhat's new", Kind.WhatsNew);
        if (lastError is not null) return ("Couldn't\ncheck", Kind.Failed);
        if (lastCheck is not { } last || now - last > Fresh || last > now.AddMinutes(5)) return ("Check for\nupdates", Kind.Check);
        return ($"Up to date\n(checked {Clock(last)})", Kind.UpToDate);
    }

    /// <summary>The tooltip for a badge; <paramref name="describe"/> is the updater's one-line state.</summary>
    public static string Tip(Kind kind, string version, string? lastError, DateTime? lastCheck, string describe, string? restartNote, string? working) =>
        kind switch
        {
            Kind.Working => working ?? describe,
            Kind.Ready => $"{describe} Click to update now.",
            Kind.Restart => $"Sketch ProMax {version}. {restartNote}",
            Kind.WhatsNew => $"Sketch ProMax {version} is installed. Click to read what's new in it.",
            Kind.Failed => $"Sketch ProMax {version}. The update check {(lastCheck is { } at ? "at " + Clock(at) + " " : "")}didn't work: " +
                           $"{(lastError ?? "").Trim().TrimEnd('.')}. Click to try again.",
            Kind.Check => lastCheck is { } at
                ? $"Sketch ProMax {version}. Not checked for updates since {Clock(at)} on {Local(at):d MMM}. Click to check now."
                : $"Sketch ProMax {version}. Not checked for updates yet. Click to check now.",
            _ => $"{describe} Click for the version, a check now and the release notes.",
        };

    /// <summary>
    /// True when a check <paramref name="every"/> apart is due: none yet, the last one at least that old by the wall
    /// clock, or the clock went back past it.
    /// </summary>
    public static bool CheckDue(DateTime? last, TimeSpan every, DateTime now) =>
        last is not { } l || now - l >= every || l > now + TimeSpan.FromMinutes(5);

    /// <summary>The local time of day of a UTC time, "14:05".</summary>
    public static string Clock(DateTime utc) => Local(utc).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime Local(DateTime utc) => utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    /// <summary>
    /// The "seen" version to record the first time this version runs with nothing recorded yet. An older version was here
    /// before (an older version folder, or update state written before this run) means this is an update: its newest such
    /// version, or 0.0.0 when only the state shows it, so the "What's new" badge shows. Otherwise a fresh install: the
    /// current version, so it doesn't start with the badge.
    /// </summary>
    public static Version FirstRunSeen(Version current, IEnumerable<string> versionFolders, bool earlierUpdateState)
    {
        var older = versionFolders.Select(n => Version.TryParse(n, out var v) ? v : null)
            .Where(v => v is not null && Plain(v) < Plain(current)).Select(v => Plain(v!)).OrderByDescending(v => v).FirstOrDefault();
        return older ?? (earlierUpdateState ? new Version(0, 0, 0) : Plain(current));
    }

    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
