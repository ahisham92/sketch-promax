using System.Globalization;

namespace SketchProMax.Chat;

/// <summary>
/// The chat view's rules that need no WPF, so they can be tested anywhere: which Markdown blocks to draw again while an
/// answer streams, how often to draw, when the list follows new text, how much history to show, and timestamps.
/// </summary>
internal static class StreamRender
{
    /// <summary>
    /// The first block whose source differs between what is drawn and the new text (the count of <paramref name="next"/>
    /// when nothing changed). Blocks before it stay drawn; only this one and the ones after are drawn again.
    /// </summary>
    public static int FirstChanged(IReadOnlyList<string> shown, IReadOnlyList<string> next)
    {
        var n = Math.Min(shown.Count, next.Count);
        for (var k = 0; k < n; k++)
            if (!string.Equals(shown[k], next[k], StringComparison.Ordinal)) return k;
        return n;
    }

    /// <summary>
    /// A short time for a message: "14:05" today, "Yesterday 14:05", "Mon 14:05" this week, "12 Oct 14:05" this year,
    /// "12 Oct 2025" before. Empty for messages saved before times were kept.
    /// </summary>
    public static string Stamp(DateTime local, DateTime nowLocal)
    {
        if (local == default) return "";
        var c = CultureInfo.InvariantCulture;
        var days = (nowLocal.Date - local.Date).TotalDays;
        if (days <= 0) return local.ToString("HH:mm", c);
        if (days < 2) return "Yesterday " + local.ToString("HH:mm", c);
        if (days < 7) return local.ToString("ddd HH:mm", c);
        if (local.Year == nowLocal.Year) return local.ToString("d MMM HH:mm", c);
        return local.ToString("d MMM yyyy", c);
    }
}

/// <summary>
/// Limits how often streamed text is drawn: the first change draws after a short wait, and changes that arrive
/// meanwhile are drawn together. Between draws at least <see cref="Interval"/> passes.
/// </summary>
internal sealed class Throttle(TimeSpan interval)
{
    private DateTime _last = DateTime.MinValue;

    public TimeSpan Interval { get; } = interval;

    /// <summary>True while a draw is waiting.</summary>
    public bool Pending { get; private set; }

    /// <summary>
    /// Something changed at <paramref name="now"/>: the wait before drawing (zero: draw at once), or null when a draw is
    /// already waiting and will include this change.
    /// </summary>
    public TimeSpan? Request(DateTime now)
    {
        if (Pending) return null;
        Pending = true;
        var wait = _last == DateTime.MinValue ? TimeSpan.Zero : _last + Interval - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>Everything changed so far was drawn at <paramref name="now"/>.</summary>
    public void Drawn(DateTime now)
    {
        Pending = false;
        _last = now;
    }
}

/// <summary>
/// Whether the message list follows new text. It follows while the reader is at the bottom; scrolling up stops that
/// (new text then counts as unseen and a "Jump to latest" button shows) and scrolling back to the bottom resumes it.
/// </summary>
internal sealed class ScrollFollow
{
    /// <summary>How close to the bottom (in pixels) still counts as at the bottom.</summary>
    public const double Slack = 24;

    public bool Following { get; private set; } = true;

    /// <summary>Messages added while not following.</summary>
    public int Unseen { get; private set; }

    public bool ShowJump => !Following;

    /// <summary>
    /// "Jump to latest" shows only over a conversation with messages below what the reader sees: never on the empty
    /// home screen (its own list scrolls too), and not while the list follows the latest message.
    /// </summary>
    public static bool JumpVisible(bool hasMessages, bool following, double scrollable, double offset) =>
        hasMessages && !following && scrollable - offset > Slack;

    /// <summary>
    /// The list scrolled. A change of content or pane size moves the offset without the reader doing anything, so it
    /// keeps the current mode; otherwise the reader moved it, and the list follows when they are at the bottom.
    /// </summary>
    public void Scrolled(double offset, double scrollable, double extentChange, double viewportChange)
    {
        if (extentChange != 0 || viewportChange != 0) return;
        SetFollowing(offset >= scrollable - Slack);
    }

    /// <summary>The reader turned the wheel up or pressed a key that scrolls up: stop following at once.</summary>
    public void ReaderScrolledUp() => SetFollowing(false);

    /// <summary>A message was added; counted when the reader is not following.</summary>
    public void Added()
    {
        if (!Following) Unseen++;
    }

    /// <summary>"Jump to latest", a new message sent, or a new conversation.</summary>
    public void Jump() => SetFollowing(true);

    private void SetFollowing(bool following)
    {
        Following = following;
        if (following) Unseen = 0;
    }
}

/// <summary>How much of a long conversation is drawn: the latest page, and a page more per "Show earlier".</summary>
internal static class History
{
    public const int Page = 40;

    /// <summary>The first item to draw when a conversation of <paramref name="count"/> items opens.</summary>
    public static int FirstShown(int count) => Math.Max(0, count - Page);

    /// <summary>The first item to draw after "Show earlier" when <paramref name="firstShown"/> is drawn first now.</summary>
    public static int Earlier(int firstShown) => Math.Max(0, firstShown - Page);

    /// <summary>
    /// How many of the oldest drawn items to drop at the end of a turn so the list stays light: none unless the reader
    /// is at the bottom and more than two pages are drawn; then back to one page.
    /// </summary>
    public static int Trim(int drawn, bool following) => following && drawn > 2 * Page ? drawn - Page : 0;
}
