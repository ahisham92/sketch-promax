using System.IO;

namespace SketchProMax.Tools;

/// <summary>
/// Step-by-step help guides shipped inside the add-in (docs/guides/*.md, embedded), so a tool or the hand-off window
/// can give the full guide where it meets the problem, offline. No Revit here: the tests read them too.
/// </summary>
public static class HelpGuides
{
    /// <summary>Converting in-place families to loadable ones (docs/guides/in-place-to-loadable.md).</summary>
    public const string InPlaceToLoadable = "in-place-to-loadable";

    /// <summary>Where the same guide reads best on the web (the repository's docs folder).</summary>
    public static string WebUrl(string name) => $"https://github.com/ahisham92/sketch-promax/blob/main/docs/guides/{name}.md";

    /// <summary>The guides shipped, by name.</summary>
    public static IReadOnlyList<string> Names =>
        typeof(HelpGuides).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
            .Select(n => n.Substring(Prefix.Length, n.Length - Prefix.Length - 3)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private const string Prefix = "SketchProMax.guides.";

    /// <summary>The guide's Markdown, or null when there is no guide of that name.</summary>
    public static string? Read(string name)
    {
        using var stream = typeof(HelpGuides).Assembly.GetManifestResourceStream(Prefix + name + ".md");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>The guide's title (its first "# " heading).</summary>
    public static string Title(string markdown) =>
        markdown.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?.Substring(2).Trim() ?? "";

    /// <summary>
    /// The numbered steps under "## Steps", each as plain text on one line ("1. Note what ..."), with the Markdown bold
    /// and code marks removed and any lines that continue a step folded into it. For tool results and short lists.
    /// </summary>
    public static List<string> Steps(string markdown)
    {
        var steps = new List<string>();
        var inSteps = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal)) { inSteps = line.Substring(3).Trim().Equals("Steps", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSteps || line.Trim().Length == 0) continue;
            var trimmed = line.TrimStart();
            var dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and <= 3 && trimmed.Substring(0, dot).All(char.IsDigit) && raw.Length - trimmed.Length == 0)
                steps.Add(Plain(trimmed));
            else if (steps.Count > 0)
                steps[steps.Count - 1] += " " + Plain(trimmed);
        }
        return steps;
    }

    private static string Plain(string text) => text.Replace("**", "").Replace("`", "").Replace("*", "").Trim();
}
