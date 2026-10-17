using System.Text.RegularExpressions;

namespace SketchProMax.UI;

/// <summary>The update notice's texts and rules (no Revit or WPF, so the tests compile it); the rest is in UpdateNotice.cs.</summary>
internal static partial class UpdateNotice
{
    public enum Kind { Available, Updated, Installed }

    public enum Decision { Show, Wait, Skip }

    public sealed record Notice(Kind Kind, string Version, IReadOnlyList<string> Lines, string? Note = null)
    {
        public string Title => Kind switch
        {
            Kind.Available => $"Sketch ProMax {Version} is available",
            Kind.Updated => $"Updated to {Version}",
            _ => $"Sketch ProMax {Version} is installed",
        };

        /// <summary>The next step, one line.</summary>
        public string Next => Kind switch
        {
            Kind.Available => "Update now installs it while Revit stays open. Your work is not interrupted.",
            Kind.Updated => Note ?? "You can keep working; no restart needed.",
            _ => Note ?? "Save your work and restart Revit when it suits you to start using it.",
        };
    }

    /// <summary>Whether the toast for <paramref name="version"/> shows now, later (something is running) or never (already shown).</summary>
    public static Decision Decide(string version, IEnumerable<string> shown, bool busy) =>
        shown.Contains(Plain(version), StringComparer.OrdinalIgnoreCase) ? Decision.Skip : busy ? Decision.Wait : Decision.Show;

    /// <summary>A "|"-separated list of versions with <paramref name="version"/> added (once).</summary>
    public static string Remember(string? list, string version)
    {
        var all = Versions(list).ToList();
        if (!all.Contains(Plain(version), StringComparer.OrdinalIgnoreCase)) all.Add(Plain(version));
        return string.Join("|", all);
    }

    public static IEnumerable<string> Versions(string? list) => (list ?? "").Split('|').Where(v => v.Length > 0);

    private static readonly Regex Markup = new(@"(\*\*|__|`|\[(?<text>[^\]]*)\]\([^)]*\))", RegexOptions.CultureInvariant);

    /// <summary>
    /// The first <paramref name="max"/> lines of release notes worth reading, as plain text: headings, bullet marks,
    /// links and emphasis removed, blank lines and rules skipped, each cut to 140 characters.
    /// </summary>
    public static IReadOnlyList<string> TopLines(string? notes, int max = 3)
    {
        var lines = new List<string>();
        foreach (var raw in (notes ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '>', ' ').Trim();
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ ")) line = line.Substring(2).Trim();
            line = Markup.Replace(line, m => m.Groups["text"].Success ? m.Groups["text"].Value : "").Trim();
            if (line.Length == 0 || line.All(c => c is '-' or '=' or '*' or '_' or '|' or ' ' or ':')) continue;
            if (line.StartsWith("<!--", StringComparison.Ordinal)) continue;
            lines.Add(line.Length > 140 ? line.Substring(0, 139).TrimEnd() + "…" : line);
            if (lines.Count == max) break;
        }
        return lines;
    }

    private static string Plain(string version) => System.Version.TryParse(version.Trim().TrimStart('v', 'V'), out var v)
        ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}"
        : version.Trim();
}
