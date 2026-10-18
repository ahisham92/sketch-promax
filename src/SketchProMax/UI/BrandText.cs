using System.Text.RegularExpressions;

namespace SketchProMax.UI;

/// <summary>
/// The assistant's name in what the user reads: Sketch ProMax. The model behind it is named only where the user has to
/// act on that product (install or sign in to the chat engine, start the desktop app, the account and its plan).
/// <see cref="Rewrite"/> brings text this core can't change at its source (the loader's pane header, its Activity view
/// and its ribbon buttons; the loader stays unchanged between releases) into line. No WPF: unit-tested.
/// </summary>
internal static class BrandText
{
    public const string Name = "Sketch ProMax";

    /// <summary>The first ribbon panel's title (its name stays "Claude": the loader and the manifest find it by that name).</summary>
    public const string PanelTitle = "Assistant";

    /// <summary>Product names the user acts on, kept as they are.</summary>
    public static readonly string[] Kept = ["Claude desktop app", "Claude app", "Claude Code", "Claude account", "Claude plan", "claude.ai", "claude update"];

    // Whole phrases with a better wording than the plain name swap, first.
    private static readonly (string From, string To)[] Phrases =
    [
        ("Export for Claude", "Export project pack"),
        ("a Claude chat", "an AI chat"),
        ("send this to Claude:", "send this with a bug report:"),
        ("Ask Claude in the chat", "Ask in the chat"),
        ("Chat with Claude", "Chat with Sketch ProMax"),
    ];

    private static readonly Regex Word = new(@"\bClaude\b", RegexOptions.CultureInvariant);

    /// <summary>"Claude: connected" → "Sketch ProMax: connected". Idempotent; text without the name comes back as it is.</summary>
    public static string Rewrite(string? text)
    {
        if (string.IsNullOrEmpty(text) || text!.IndexOf("Claude", StringComparison.Ordinal) < 0) return text ?? "";
        foreach (var (from, to) in Phrases) text = text.Replace(from, to);
        for (var i = 0; i < Kept.Length; i++) text = text.Replace(Kept[i], "\u0001" + i + "\u0002");
        text = Word.Replace(text, Name);
        for (var i = 0; i < Kept.Length; i++) text = text.Replace("\u0001" + i + "\u0002", Kept[i]);
        return text;
    }

    /// <summary>True when <paramref name="text"/> names the model only as a product the user acts on (see <see cref="Kept"/>).</summary>
    public static bool Clean(string text)
    {
        foreach (var k in Kept) text = text.Replace(k, "");
        return !Word.IsMatch(text);
    }
}
