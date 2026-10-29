using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// A Simple chat is a talk with one worker directly. After each answer this says whether that worker should be spared:
/// the chat carries so much that every message costs a lot (overloaded), its requests jump between unrelated topics
/// (mixed), or the last request is several separate tasks (several). Each points to a chat With coordinator, where the
/// coordinator keeps a short memory and hands each part to its own worker. Plain functions, tested.
/// </summary>
internal static class DirectAdvice
{
    public const string Overloaded = "overloaded", Mixed = "mixed", Several = "several";

    /// <summary>Tokens a message carries over the chat's floor at which the worker is overloaded (well before the auto shrink).</summary>
    public const long HeavyTokens = 60_000;

    /// <summary>...or this many requests in one chat.</summary>
    public const int ManyRequests = 15;

    /// <summary>Two requests share less than this of their words: different topics.</summary>
    public const double Unrelated = 0.1;

    private static readonly Regex Word = new(@"[\p{L}\p{N}]{4,}", RegexOptions.CultureInvariant);
    private static readonly Regex ListLine = new(@"^\s*(\d+[.)]|[-*•])\s+\S", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "please", "could", "would", "should", "with", "this", "that", "these", "those", "then", "than", "from", "into",
        "also", "make", "have", "will", "what", "when", "where", "which", "there", "their", "them", "they", "your",
        "just", "like", "some", "more", "each", "every", "model", "view", "views", "revit", "thanks", "check", "show",
        "about", "after", "before", "again", "only", "need", "want", "level", "levels",
    };

    /// <summary>The advice to give now (or null): <paramref name="requests"/> are the chat's requests, the latest last.</summary>
    public static string? Check(IReadOnlyList<string> requests, long contextTokens, long floor, ICollection<string> given)
    {
        if (requests.Count == 0) return null;
        string? Once(string advice) => given.Contains(advice) ? null : advice;
        if (IsSeveral(requests[requests.Count - 1]) && Once(Several) is { } several) return several;
        if ((floor > 0 && contextTokens - floor >= HeavyTokens || requests.Count >= ManyRequests) && Once(Overloaded) is { } heavy) return heavy;
        if (IsMixed(requests) && Once(Mixed) is { } mixed) return mixed;
        return null;
    }

    /// <summary>The request lists three or more separate tasks (numbered or bulleted lines, or parts split by ";").</summary>
    public static bool IsSeveral(string request) =>
        ListLine.Matches(request).Count >= 3 || request.Split(';').Count(p => Words(p).Count > 0) >= 3;

    /// <summary>The last three requests are each about something else: none shares much with the one before it.</summary>
    public static bool IsMixed(IReadOnlyList<string> requests)
    {
        var real = requests.Select(Words).Where(w => w.Count >= 3).ToList();
        if (real.Count < 3) return false;
        var c = real[real.Count - 1];
        var b = real[real.Count - 2];
        var a = real[real.Count - 3];
        return Overlap(c, b) < Unrelated && Overlap(b, a) < Unrelated && Overlap(c, a) < Unrelated;
    }

    /// <summary>The words two requests share, of all their words (0 to 1).</summary>
    public static double Overlap(HashSet<string> a, HashSet<string> b)
    {
        var all = a.Count + b.Count - a.Count(b.Contains);
        return all == 0 ? 0 : (double)a.Count(b.Contains) / all;
    }

    /// <summary>A request's meaningful words, lower case, with a plural's s dropped.</summary>
    public static HashSet<string> Words(string text) =>
        new(Word.Matches(text ?? "").Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Where(w => !Common.Contains(w))
            .Select(w => w.Length > 4 && w.EndsWith("s", StringComparison.Ordinal) ? w.Substring(0, w.Length - 1) : w));

    /// <summary>What the chat says, with the worker's name.</summary>
    public static string Say(string advice, string worker) => advice switch
    {
        Several => $"This asks {worker} for several separate things, one after another. With coordinator, the team does them side by side, each part on its own worker.",
        Overloaded => $"{worker} has a lot on: this chat carries a long history, so every message costs more of your plan and gets slower. Shrink it, or switch to With coordinator: the coordinator keeps a short memory and hands each task to a fresh worker.",
        _ => $"These are different topics in one chat, so {worker} carries all of them with every answer. With coordinator, each topic goes to its own worker and costs less.",
    };
}
