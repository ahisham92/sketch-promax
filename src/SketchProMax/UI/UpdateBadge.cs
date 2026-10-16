namespace SketchProMax.UI;

/// <summary>
/// The Update button's one-word badge and what clicking it does. Pure: the caller gathers the facts (update window,
/// restart note, last check, the version whose notes were last opened), so the tests check every case.
/// </summary>
internal static class UpdateBadge
{
    public enum Kind { Working, Restart, WhatsNew, Check, UpToDate }

    /// <summary>How long a successful check counts as "up to date" before the badge asks for a new one.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromDays(2);

    /// <param name="working">A short line while an update is being looked for, downloaded or applied; else null.</param>
    /// <param name="restart">New buttons or pane parts need a Revit restart.</param>
    /// <param name="seen">The version whose release notes were last opened (or that was running the first time); null if unknown.</param>
    public static (string Text, Kind Kind) For(string? working, bool restart, Version current, Version? seen,
        DateTime? lastCheck, bool lastCheckFailed, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(working)) return (working!.Replace('\n', ' ').Trim(), Kind.Working);
        if (restart) return ("Restart Revit", Kind.Restart);
        if (seen is not null && Plain(current) > Plain(seen)) return ("What's new", Kind.WhatsNew);
        if (lastCheckFailed || lastCheck is null || now - lastCheck.Value > Fresh) return ("Check", Kind.Check);
        return ("Up to date", Kind.UpToDate);
    }

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
