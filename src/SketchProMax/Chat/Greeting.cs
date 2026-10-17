using System.Globalization;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// The home screen's "Hey Ahmed!": a first name from a Windows display name ("Ahmed Mitwally", "Mitwally, Ahmed",
/// "AHMED MITWALLY (Office)") or, failing that, from a sign-in name ("ahmed.mitwally", "DOMAIN\ahmed",
/// "ahmed@example.com"). Pure, so it is tested without Windows; the lookup lives in <see cref="HomeView"/>.
/// </summary>
internal static class Greeting
{
    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase) { "mr", "mrs", "ms", "miss", "dr", "eng", "prof", "sir" };

    /// <summary>"Hey Ahmed!", or "Hey there!" when no first name is known.</summary>
    public static string Line(string? firstName) => string.IsNullOrWhiteSpace(firstName) ? "Hey there!" : $"Hey {firstName!.Trim()}!";

    /// <summary>The first name from the first candidate that has one (display names first, the sign-in name last).</summary>
    public static string? Pick(params string?[] candidates)
    {
        foreach (var candidate in candidates)
            if (FirstName(candidate) is { } first) return first;
        return null;
    }

    /// <summary>
    /// The first name in a display or sign-in name, capitalised when it was all one case; null when there is none that
    /// reads as a name (a single letter, digits, an empty value).
    /// </summary>
    public static string? FirstName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = Regex.Replace(name!, @"\([^)]*\)|\[[^\]]*\]", " ").Trim();
        var login = false;
        if (text.IndexOf('@') > 0)
        {
            text = text.Substring(0, text.IndexOf('@'));
            login = true;
        }
        if (text.LastIndexOf('\\') is var slash and >= 0)
        {
            text = text.Substring(slash + 1);
            login = true;
        }
        if (text.IndexOf(',') is var comma and > 0) text = text.Substring(comma + 1); // "Last, First"
        if (!login && text.IndexOf(' ') < 0) login = true; // one word: a sign-in name such as ahmed.mitwally
        var parts = (login ? text.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries) : text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim().TrimEnd('.'))
            .Where(p => p.Length > 0 && !Titles.Contains(p))
            .ToList();
        if (parts.Count == 0) return null;
        var first = parts[0];
        if (first.Length < 2 || first.Length > 30) return null;
        if (!first.All(c => char.IsLetter(c) || c is '\'' or '-' or '’')) return null;
        return Capitalise(first);
    }

    /// <summary>"AHMED" and "ahmed" become "Ahmed" (each part of "jean-luc" too); mixed case such as "McKay" is kept.</summary>
    private static string Capitalise(string word)
    {
        var letters = word.Where(char.IsLetter).ToList();
        var oneCase = letters.All(c => !char.IsLower(c)) || letters.All(c => !char.IsUpper(c));
        if (!oneCase) return word;
        var lower = word.ToLower(CultureInfo.InvariantCulture);
        var chars = lower.ToCharArray();
        for (var k = 0; k < chars.Length; k++)
            if (k == 0 || chars[k - 1] is '-' or '\'') chars[k] = char.ToUpper(chars[k], CultureInfo.InvariantCulture);
        return new string(chars);
    }
}
