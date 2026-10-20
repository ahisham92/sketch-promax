namespace SketchProMax.UI;

/// <summary>
/// The Settings window's tabs, which setting lives on which tab, and how big the window may be (no Revit or WPF, so
/// the tests compile it); the window itself is SettingsWindow.cs.
/// </summary>
internal static class SettingsLayout
{
    /// <summary>A tab: its key (remembered between openings), the name in the tab list and the line at the top of its page.</summary>
    public sealed record Tab(string Key, string Name, string Intro);

    public const string Approvals = "approvals", Chat = "chat", Standards = "standards", Time = "time", Issue = "issue", SignOff = "signoff", Updates = "updates";

    public static readonly Tab[] Tabs =
    [
        new(Approvals, "Approvals", "When Revit asks before Sketch ProMax changes the model."),
        new(Chat, "Chat", "How the Chat pane looks and moves."),
        new(Standards, "Firm standards", "Your office's rules, followed in every conversation."),
        new(Time, "Time tracking", "Working time and time saved, kept in each model."),
        new(Issue, "Issue & drawings", "What the issue check lets through."),
        new(SignOff, "Sign-off", "Your signature and the checklist used by the Pre-submission checklist button."),
        new(Updates, "Updates", "Keeping Sketch ProMax up to date."),
    ];

    /// <summary>
    /// Every stored setting (an AgentSettings property the window edits) and its one tab. RequireApprovalForChanges is
    /// not here: it is written from ApprovalLevel for older versions, never edited on its own.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Settings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ApprovalLevel"] = Approvals,
        ["NoAskTools"] = Approvals,
        ["ChatAnimations"] = Chat,
        ["FirmStandards"] = Standards,
        ["LegendSeedPath"] = Standards,
        ["TimeTracking"] = Time,
        ["IdleMinutes"] = Time,
        ["TimeRates"] = Time,
        ["AllowIssueWithOpenItems"] = Issue,
        ["SignatureName"] = SignOff,
        ["SignatureRole"] = SignOff,
        ["SignatureImage"] = SignOff,
        ["ChecklistPath"] = SignOff,
        ["ChecklistHelper"] = SignOff,
        ["AutoUpdate"] = Updates,
    };

    /// <summary>The tab to open on: the one opened last time when it still exists, else the first.</summary>
    public static int StartIndex(string? remembered)
    {
        var key = (remembered ?? "").Trim();
        for (var i = 0; i < Tabs.Length; i++)
            if (string.Equals(Tabs[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>The tab after (or before) <paramref name="index"/>, wrapping round, as Ctrl+Tab and Ctrl+Shift+Tab move.</summary>
    public static int Next(int index, bool back) => ((index + (back ? -1 : 1)) % Tabs.Length + Tabs.Length) % Tabs.Length;

    /// <summary>Largest share of the monitor's work area the window takes.</summary>
    public const double MaxShare = 0.85;

    /// <summary>The size the window opens at when there is room, and the smallest it may be resized to (device-independent pixels).</summary>
    public const double PreferredWidth = 780, PreferredHeight = 640, MinWidth = 560, MinHeight = 400;

    /// <summary>
    /// Room the header band and the pinned Save / Cancel bar take at most (a two-line error included), so the tab page
    /// keeps at least <see cref="MinPage"/> at the smallest size.
    /// </summary>
    public const double Chrome = 200, MinPage = 180;

    /// <summary>
    /// The window size for a work area of <paramref name="workWidth"/> × <paramref name="workHeight"/> device-independent
    /// pixels (the monitor Revit is on, taskbar left out): the preferred size, never more than <see cref="MaxShare"/> of
    /// the area, and minimums that are themselves capped by it, so the whole window, Save included, is always on screen.
    /// </summary>
    public static (double Width, double Height, double MinWidth, double MinHeight) Fit(double workWidth, double workHeight)
    {
        if (!(workWidth > 0) || !(workHeight > 0)) { workWidth = 1280; workHeight = 680; } // unreadable: a small laptop screen
        var maxW = Math.Floor(workWidth * MaxShare);
        var maxH = Math.Floor(workHeight * MaxShare);
        var minW = Math.Min(MinWidth, maxW);
        var minH = Math.Min(MinHeight, maxH);
        return (Math.Min(PreferredWidth, maxW), Math.Min(PreferredHeight, maxH), minW, minH);
    }

    /// <summary>A work area in device pixels as device-independent pixels at <paramref name="scale"/> (1.5 at 150 %).</summary>
    public static (double Width, double Height) ToDips(int widthPx, int heightPx, double scale) =>
        scale > 0 ? (widthPx / scale, heightPx / scale) : (widthPx, heightPx);
}
