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

    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
