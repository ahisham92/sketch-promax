using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// "No need to pop up anything, I will not be available for the next hour" (19 Nov 2026): the user is away and nothing
/// may wait on a click. Said in a chat message, it holds for the time said (an hour when none is said, at most 12) or
/// until the user says they are back. Meanwhile, as in an Autopilot run: Revit's dialogs during Sketch ProMax's own work
/// are answered (Cancel, else No, else OK; Revit.UnattendedDialogs), a step only the user can do is skipped and listed
/// (Handoff), a normal change isn't asked about, and a risky one (a delete, a big change) is not made but left for the
/// user. What was answered or skipped is said in the chat when the user writes next. The user's own commands' dialogs are
/// never touched, except a command left open with nobody at Revit: when Revit has taken no work for two minutes while
/// the user is away, Esc ends it (RevitBlock.EndCommandNow; "if there is an order like that, skip it", 19 Nov 2026,
/// after a Spot Elevation left open held the jobs for 16 minutes). No Revit or WPF here (the tests compile it).
/// </summary>
internal static class Away
{
    private static readonly object Gate = new();
    private static DateTime _until;
    private static readonly List<string> Said = [];

    /// <summary>The longest an away spell lasts, whatever was said.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromHours(12);

    /// <summary>True while the user said they are away (any thread).</summary>
    public static bool On
    {
        get { lock (Gate) return DateTime.UtcNow < _until; }
    }

    public static DateTime Until
    {
        get { lock (Gate) return _until; }
    }

    private static readonly Regex Asks = new(
        @"\b(no\s+(need\s+(to|for)\s+)?(any\s+)?pop\s*-?\s*ups?|(do\s*n[o']?t|never)\s+(pop\s*(up)?|show|ask)\s+(me\s+)?(anything|any\w*)|" +
        @"no\s+pop\s*(up)?\s+anything|without\s+(any\s+)?(pop\s*-?\s*ups?|asking\s+me)|handle\s+(the\s+)?(pop\s*-?\s*ups?|dialogs?|prompts?)|" +
        @"(i\s*'?\s*a?m|i\s+will|i\s*'?\s*ll)\s+(be\s+)?(away|out|offline|not\s+(be\s+)?(available|here|around|at\s+(my|the)\s+(pc|desk|computer)))|" +
        @"i\s+(won\s*['\u2019]?\s*t|wont)\s+be\s+(available|here|around|at\s+(my|the)\s+(pc|desk|computer))|i\s*['\u2019]?\s*m\s+not\s+(available|here|around)|" +
        @"handle\s+(the\s+)?waiting|(skip|escape|end)\s+(any\s+)?(stuck|running|open)\s+commands?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Back = new(@"\b(i\s*'?\s*a?m\s+back|back\s+now|i\s*'?\s*a?m\s+here\s+(now|again)|you\s+can\s+(ask|pop\s*up)\s+(me\s+)?(again|now))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Span = new(@"\b(?<n>\d+(?:\.\d+)?|an?|one|two|three|four|five|six|half\s+an?)\s*(?<u>hours?|hrs?|h|minutes?|mins?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>How long the message says the user is away (null when it doesn't say they are), at most <see cref="Longest"/>.</summary>
    public static TimeSpan? Asked(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Asks.IsMatch(text)) return null;
        var span = TimeSpan.FromHours(1);
        if (Span.Match(text) is { Success: true } m)
        {
            var n = m.Groups["n"].Value.ToLowerInvariant();
            double count = n switch
            {
                "a" or "an" or "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6,
                _ when n.StartsWith("half") => 0.5,
                _ => double.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1,
            };
            var unit = m.Groups["u"].Value.ToLowerInvariant();
            span = unit.StartsWith("m") ? TimeSpan.FromMinutes(count) : TimeSpan.FromHours(count);
        }
        if (span < TimeSpan.FromMinutes(10)) span = TimeSpan.FromMinutes(10);
        return span > Longest ? Longest : span;
    }

    /// <summary>True when the message says the user is back.</summary>
    public static bool IsBack(string text) => !string.IsNullOrWhiteSpace(text) && Back.IsMatch(text);

    /// <summary>
    /// A message the user typed: starts or ends an away spell. The note for the chat (local time), or null when nothing
    /// changed. <paramref name="local"/> turns the end time into the user's clock.
    /// </summary>
    public static string? Heard(string text, DateTime now, Func<DateTime, DateTime>? local = null)
    {
        lock (Gate)
        {
            if (Asked(text) is { } span)
            {
                _until = now + span;
                var end = (local ?? (t => t.ToLocalTime()))(_until);
                return $"Away mode until {end:HH:mm}: nothing pops up. Revit's pop-ups during the jobs' work are answered (Cancel, else No, else OK), " +
                       "steps only you can do are skipped, and risky changes (deletes) wait for you. You get the list when you write next.";
            }
            if (_until != default && (IsBack(text) || now >= _until))
            {
                var was = _until > now;
                _until = default;
                return was ? "Away mode is off: pop-ups and questions show again." : null;
            }
            return null;
        }
    }

    /// <summary>Something answered or skipped while away, for the list the user gets (any thread).</summary>
    public static void Note(string text)
    {
        lock (Gate)
        {
            Said.Add(text);
            if (Said.Count > 40) Said.RemoveAt(0);
        }
    }

    /// <summary>What was answered or skipped while away, once ("" when nothing was).</summary>
    public static string Take()
    {
        lock (Gate)
        {
            if (Said.Count == 0) return "";
            var list = "While you were away: " + string.Join(" ", Said.Select(s => s.TrimEnd('.') + "."));
            Said.Clear();
            return list;
        }
    }

    /// <summary>For the tests: ends the spell and forgets the list.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _until = default;
            Said.Clear();
        }
    }
}
