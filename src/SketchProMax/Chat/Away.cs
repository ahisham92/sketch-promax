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

    /// <summary>The file the spell's end is kept in (set by the chat at start), so a version switch keeps it; null: memory only.</summary>
    public static string? StoreFile { get; set; }

    /// <summary>Raised (any thread) when away mode starts or ends, for the Away button.</summary>
    public static event Action? Changed;

    /// <summary>The Away button: on for <paramref name="span"/> (at most <see cref="Longest"/>), or off.</summary>
    public static void Set(bool on, DateTime now, TimeSpan? span = null)
    {
        lock (Gate)
        {
            _until = on ? now + (span is { } t && t < Longest ? t : Longest) : default;
            Write();
        }
        Changed?.Invoke();
    }

    /// <summary>Reads the spell back after a version switch or a restart.</summary>
    public static void Restore()
    {
        try
        {
            if (StoreFile is not { } file || !System.IO.File.Exists(file)) return;
            if (DateTime.TryParse(System.IO.File.ReadAllText(file).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var until))
                lock (Gate) _until = until;
        }
        catch (Exception) { /* not away */ }
    }

    private static void Write()
    {
        try
        {
            if (StoreFile is not { } file) return;
            if (_until == default) { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); return; }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, _until.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception) { /* kept in memory */ }
    }

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

    // Any way of saying "I'm not at Revit": I (will|'ll|won't|am|'m|am going to|'ll be|might) be + away/out/off/not around/
    // unavailable/busy/in a meeting/asleep..., going home/out/to sleep, leaving, back in an hour, don't wait for me, carry
    // on without me, afk. The owner, 19 Nov 2026: "I won't be available, I will be away, everything I say regarding this".
    private const string Me = @"(i|i\s*['’]?\s*m|i\s*['’]?\s*ll|i\s+am|i\s+will|i\s+shall|i\s+(might|may|could|would)|i\s*['’]?\s*m\s+going\s+to|i\s+am\s+going\s+to|i\s*['’]?\s*m\s+gonna|i\s+have\s+to|i\s+need\s+to|i\s+must)";
    private const string MeNot = @"(i\s*['’]?\s*m\s+not|i\s+am\s+not|i\s+(won\s*['’]?\s*t|wont|will\s+not|shall\s+not)|i\s+(might|may|could|would)\s+not|i\s*['’]?\s*ll\s+not)";
    // Said plainly ("I'll be away"): gone.
    private const string Gone = @"(away|out(\s+of\s+(the\s+)?office)?|off(line)?|unavailable|unreachable|back\s+(soon|later|tomorrow|in|after)|" +
                                @"busy|in\s+(a|the)\s+(meeting|call)|on\s+(a\s+)?(call|break|leave|site)|asleep|sleeping|leav(e|ing)(\s+now|\s+the\s+office)?\s*([,.!]|$)|" +
                                @"go(ing)?\s+(home|out|offline|now|to\s+(sleep|bed|pray|lunch|a\s+meeting))|step(ping)?\s+(out|away)|head(ing)?\s+(home|out|off)|log(ging)?\s+off|sign(ing)?\s+off)";
    // Said with a not ("I won't be available"): not there.
    private const string There = @"(available|reachable|around|here|present|at\s+(my|the)\s+(pc|desk|computer|laptop|office)|in\s+(the\s+)?office|online)";

    private static readonly Regex Asks = new(
        @"\b(no\s+(need\s+(to|for)\s+)?(any\s+)?pop\s*-?\s*ups?|(do\s*n[o'’]?t|never)\s+(pop\s*(up)?|show|ask)\s+(me\s+)?(anything|any\w*)|" +
        @"no\s+pop\s*(up)?\s+anything|without\s+(any\s+)?(pop\s*-?\s*ups?|asking\s+me)|handle\s+(the\s+)?(pop\s*-?\s*ups?|dialogs?|prompts?|waiting)|" +
        Me + @"\s+(be\s+)?(going\s+to\s+be\s+)?" + Gone + "|" + MeNot + @"\s+(be\s+)?(going\s+to\s+be\s+)?" + There + "|" +
        @"(don\s*['’]?\s*t|do\s+not|no\s+need\s+to)\s+wait\s+(for|on)\s+me|(carry\s+on|continue|keep\s+going|work)\s+without\s+me|while\s+i\s*['’]?\s*(m|am)\s+(away|out|gone)|" +
        @"\bafk\b|\bbrb\b|(skip|escape|end)\s+(any\s+)?(stuck|running|open)\s+commands?)",
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
        string? note = null;
        lock (Gate)
        {
            if (Asked(text) is { } span)
            {
                _until = now + span;
                Write();
                var end = (local ?? (t => t.ToLocalTime()))(_until);
                note = $"Away mode until {end:HH:mm}: nothing pops up. Revit's pop-ups during the jobs' work are answered (Cancel, else No, else OK), " +
                       "steps only you can do are skipped, and risky changes (deletes) wait for you. You get the list when you write next.";
            }
            else if (_until != default && (IsBack(text) || now >= _until))
            {
                var was = _until > now;
                _until = default;
                Write();
                note = was ? "Away mode is off: pop-ups and questions show again." : null;
                if (!was) return null;
            }
            else return null;
        }
        Changed?.Invoke();
        return note;
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
