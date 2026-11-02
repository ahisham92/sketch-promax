using System.IO;
using System.Text.RegularExpressions;

namespace SketchProMax.Phone;

/// <summary>
/// Which project a model belongs to on the phone: each project has its own chat there, named "Sam (Harbour)" after the
/// coordinator and the project. The central model, every local copy of it and a "_detached" copy are the same project, so
/// the user finds one conversation whichever file they work in (2 Nov 2026). The name comes from the model's file: the
/// central's for a local copy, without the user name Revit adds, "_detached", "Copy", backup marks and the like; a model
/// named the ISO way (PRJ1-ORG-RVT-HBR-SB) gives its first letters-only field after the first (HBR); otherwise the first
/// word before an underscore. Settings › Chat › "Phone chats" renames them ("HBR = Harbour"), which also joins two names
/// into one chat. Needs no Revit or WPF (the unit tests compile this file).
/// </summary>
internal static class PhoneProjects
{
    /// <summary>File-type and discipline codes that never name a project in a dashed file name.</summary>
    private static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RVT", "DWG", "PDF", "IFC", "NWC", "NWD", "CAD", "BIM", "M3", "M2", "DR", "MOD", "ZZ", "XX", "WIP",
        "ARC", "ARCH", "STR", "STRU", "MEP", "MEC", "ELE", "PLU", "CIV", "LAN",
    };

    private static readonly Regex Marks = new(
        @"(_detached|_detach|[ _-]copy( ?\(?\d+\)?)?|\(recovery\)|_recovered|_backup\d*| autopilot backup.*|\.\d{4})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UserMark = new(@"_[A-Za-z][A-Za-z0-9-]*\.[A-Za-z][A-Za-z0-9.-]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The project of a model: <paramref name="key"/> is its chats' key (the central's path for a workshared model, else its
    /// file; ChatModelKeys), <paramref name="title"/> its title, <paramref name="user"/> the Windows user (a local copy is
    /// "central_user"), <paramref name="names"/> the user's renames (Settings). "" when nothing can be made of it.
    /// </summary>
    public static string Of(string? key, string? title, string? user = null, string? names = null)
    {
        var source = key is { Length: > 0 } k && !k.StartsWith("unsaved:", StringComparison.Ordinal) ? k : title ?? "";
        var guess = Guess(source, user ?? "");
        if (guess.Length == 0 && !ReferenceEquals(source, title)) guess = Guess(title ?? "", user ?? "");
        return Renamed(guess, names);
    }

    /// <summary>The project guessed from a model's file or title (before the user's renames).</summary>
    public static string Guess(string source, string user = "")
    {
        var name = (source ?? "").Trim();
        name = name.Substring(name.LastIndexOfAny(['\\', '/']) + 1);
        if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rte", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        for (var i = 0; i < 4; i++)
        {
            var before = name;
            name = Marks.Replace(name, "").Trim();
            if (user.Length > 0 && name.EndsWith("_" + user, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - user.Length - 1);
            name = UserMark.Replace(name, "").Trim();
            if (name == before) break;
        }
        if (name.Length == 0) return "";
        var parts = name.Split('-').Select(p => p.Trim()).ToArray();
        if (parts.Length >= 3)
        {
            var field = parts.Skip(1).FirstOrDefault(p => p.Length is >= 2 and <= 8 && p.All(char.IsLetter) && !Codes.Contains(p));
            return Tidy(field ?? parts[0]);
        }
        var word = name.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        return Tidy(word);
    }

    /// <summary>"harbour" and "HARBOUR" read "Harbour"; mixed case stays as typed; a code of three letters or fewer stays capitals.</summary>
    private static string Tidy(string word)
    {
        word = word.Trim();
        if (word.Length == 0) return "";
        if (word.Length <= 3 && word.All(char.IsLetter)) return word.ToUpperInvariant();
        var lower = word.ToLowerInvariant();
        return word == lower || word == word.ToUpperInvariant() ? char.ToUpperInvariant(lower[0]) + lower.Substring(1) : word;
    }

    /// <summary>
    /// The user's renames, "HBR = Harbour, TWR = Tower" (commas, semicolons or new lines between them): the name it goes by on
    /// the phone. A guess with no rename keeps its own name.
    /// </summary>
    public static string Renamed(string guess, string? names)
    {
        if (guess.Length == 0 || string.IsNullOrWhiteSpace(names)) return guess;
        foreach (var pair in names!.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            if (at <= 0) continue;
            var from = pair.Substring(0, at).Trim();
            var to = pair.Substring(at + 1).Trim();
            if (to.Length > 0 && from.Equals(guess, StringComparison.OrdinalIgnoreCase)) return to.Length > 40 ? to.Substring(0, 40).Trim() : to;
        }
        return guess;
    }

    /// <summary>Two names are the same chat (the phone keeps the case it was sent with).</summary>
    public static bool Same(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
