using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// A small Markdown reader for Claude's answers: headings, paragraphs, bullet and numbered lists (nested by indent),
/// fenced code, tables, quotes and rules; inline bold, italic, strikethrough, code and links. It never throws and
/// copes with a half-streamed answer (an open code fence runs to the end). No WPF here, so it can be tested anywhere;
/// <see cref="MarkdownView"/> draws the blocks.
/// </summary>
internal static class Markdown
{
    internal abstract record Block;
    internal sealed record Heading(int Level, string Text) : Block;
    internal sealed record Paragraph(string Text) : Block;
    internal sealed record ListItem(string Text, int Depth, bool Ordered, int Number);
    internal sealed record ListBlock(IReadOnlyList<ListItem> Items) : Block;
    internal sealed record Code(string Language, string Text) : Block;
    internal sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<char> Align, IReadOnlyList<IReadOnlyList<string>> Rows) : Block;
    internal sealed record Quote(string Text) : Block;
    internal sealed record Rule : Block;

    [Flags]
    internal enum Style { None = 0, Bold = 1, Italic = 2, Code = 4, Strike = 8 }

    internal sealed record Span(string Text, Style Style, string? Link = null);

    private static readonly Regex HeadingLine = new(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Bullet = new(@"^(\s*)([-*+])\s+(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Numbered = new(@"^(\s*)(\d{1,9})[.)]\s+(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^\s{0,3}(`{3,}|~{3,})\s*([^`\s]*)", RegexOptions.CultureInvariant);
    private static readonly Regex RuleLine = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$", RegexOptions.CultureInvariant);

    /// <summary>A block with the source lines it was read from.</summary>
    internal readonly record struct Sourced(Block Block, string Source);

    public static List<Block> Parse(string? text) => ParseWithSource(text).Select(b => b.Block).ToList();

    /// <summary>
    /// The blocks, each with its source lines. While an answer streams, text is only added at the end, so every block
    /// whose source is unchanged can stay drawn as it is (see <see cref="StreamRender.FirstChanged"/>).
    /// </summary>
    public static List<Sourced> ParseWithSource(string? text)
    {
        var blocks = new List<Sourced>();
        if (string.IsNullOrEmpty(text)) return blocks;
        var lines = text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        var paragraph = new StringBuilder();
        var paragraphFrom = 0;

        void Add(Block block, int from, int to) =>
            blocks.Add(new Sourced(block, string.Join("\n", lines, from, Math.Max(0, Math.Min(to, lines.Length) - from))));

        void Flush()
        {
            if (paragraph.Length == 0) return;
            Add(new Paragraph(paragraph.ToString().TrimEnd()), paragraphFrom, i);
            paragraph.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { Flush(); i++; continue; }

            var fence = Fence.Match(line);
            if (fence.Success)
            {
                Flush();
                var from = i;
                var marker = fence.Groups[1].Value;
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal))
                {
                    code.Append(lines[i]).Append('\n');
                    i++;
                }
                i++; // the closing fence (or past the end while it is still streaming)
                Add(new Code(fence.Groups[2].Value, code.ToString().TrimEnd('\n')), from, i);
                continue;
            }

            var heading = HeadingLine.Match(line);
            if (heading.Success)
            {
                Flush();
                Add(new Heading(heading.Groups[1].Value.Length, heading.Groups[2].Value), i, i + 1);
                i++;
                continue;
            }

            if (RuleLine.IsMatch(line) && paragraph.Length == 0)
            {
                Add(new Rule(), i, i + 1);
                i++;
                continue;
            }

            if (line.Contains("|") && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]) && lines[i + 1].Contains("-"))
            {
                Flush();
                var from = i;
                var header = Cells(line);
                var align = Cells(lines[i + 1]).Select(c =>
                {
                    var t = c.Trim();
                    return t.StartsWith(":") && t.EndsWith(":") ? 'c' : t.EndsWith(":") ? 'r' : 'l';
                }).ToList();
                i += 2;
                var rows = new List<IReadOnlyList<string>>();
                while (i < lines.Length && lines[i].Trim().Length > 0 && lines[i].Contains("|"))
                {
                    rows.Add(Cells(lines[i]));
                    i++;
                }
                Add(new Table(header, align, rows), from, i);
                continue;
            }

            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                Flush();
                var from = i;
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    var t = lines[i].TrimStart().Substring(1);
                    if (t.StartsWith(" ")) t = t.Substring(1);
                    quote.Append(t).Append('\n');
                    i++;
                }
                Add(new Quote(quote.ToString().TrimEnd()), from, i);
                continue;
            }

            if (Bullet.IsMatch(line) || Numbered.IsMatch(line))
            {
                Flush();
                var from = i;
                var items = new List<ListItem>();
                var indents = new List<int>();
                while (i < lines.Length)
                {
                    var l = lines[i];
                    var b = Bullet.Match(l);
                    var n = b.Success ? Match.Empty : Numbered.Match(l);
                    if (b.Success || n.Success)
                    {
                        var m = b.Success ? b : n;
                        var indent = m.Groups[1].Value.Replace("\t", "    ").Length;
                        // Depth from the indents seen so far: deeper than the last item nests one level.
                        while (indents.Count > 0 && indent < indents[indents.Count - 1]) indents.RemoveAt(indents.Count - 1);
                        if (indents.Count == 0 || indent > indents[indents.Count - 1]) indents.Add(indent);
                        var number = n.Success && int.TryParse(n.Groups[2].Value, out var v) ? v : 0;
                        items.Add(new ListItem(m.Groups[3].Value, indents.Count - 1, n.Success, number));
                        i++;
                        continue;
                    }
                    if (l.Trim().Length == 0)
                    {
                        // A blank line ends the list unless another item follows.
                        if (i + 1 < lines.Length && (Bullet.IsMatch(lines[i + 1]) || Numbered.IsMatch(lines[i + 1]))) { i++; continue; }
                        break;
                    }
                    if (char.IsWhiteSpace(l[0]) && items.Count > 0)
                    {
                        // A continuation line of the last item.
                        var last = items[items.Count - 1];
                        items[items.Count - 1] = last with { Text = last.Text + " " + l.Trim() };
                        i++;
                        continue;
                    }
                    break;
                }
                Add(new ListBlock(items), from, i);
                continue;
            }

            // Paragraph text; a line ending in two spaces or a backslash is a hard break.
            var hard = line.EndsWith("  ", StringComparison.Ordinal) || line.EndsWith("\\", StringComparison.Ordinal);
            var trimmed = line.Trim();
            if (trimmed.EndsWith("\\", StringComparison.Ordinal)) trimmed = trimmed.Substring(0, trimmed.Length - 1);
            if (paragraph.Length == 0) paragraphFrom = i;
            if (paragraph.Length > 0 && paragraph[paragraph.Length - 1] != '\n') paragraph.Append(' ');
            paragraph.Append(trimmed);
            if (hard) paragraph.Append('\n');
            i++;
        }
        Flush();
        return blocks;
    }

    private static List<string> Cells(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|")) t = t.Substring(1);
        if (t.EndsWith("|") && !t.EndsWith("\\|")) t = t.Substring(0, t.Length - 1);
        var cells = new List<string>();
        var cell = new StringBuilder();
        var code = false;
        for (var k = 0; k < t.Length; k++)
        {
            var c = t[k];
            if (c == '\\' && k + 1 < t.Length && t[k + 1] == '|') { cell.Append('|'); k++; continue; }
            if (c == '`') code = !code;
            if (c == '|' && !code) { cells.Add(cell.ToString().Trim()); cell.Clear(); continue; }
            cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    // ---- Inline ----------------------------------------------------------------------------------------------------

    public static List<Span> Inline(string text)
    {
        var spans = new List<Span>();
        Inline(text ?? "", Style.None, spans);
        // Merge neighbours with the same look, so the renderer makes fewer runs.
        var merged = new List<Span>();
        foreach (var s in spans)
        {
            if (s.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[merged.Count - 1] is { } last && last.Style == s.Style && last.Link == s.Link)
                merged[merged.Count - 1] = last with { Text = last.Text + s.Text };
            else merged.Add(s);
        }
        return merged;
    }

    private static void Inline(string s, Style style, List<Span> spans, string? link = null)
    {
        var plain = new StringBuilder();
        void Emit()
        {
            if (plain.Length == 0) return;
            spans.Add(new Span(plain.ToString(), style, link));
            plain.Clear();
        }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && char.IsPunctuation(s[i + 1]) || c == '\\' && i + 1 < s.Length && char.IsSymbol(s[i + 1]))
            {
                plain.Append(s[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var ticks = 1;
                while (i + ticks < s.Length && s[i + ticks] == '`') ticks++;
                var fence = new string('`', ticks);
                var end = s.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                if (end > i + ticks)
                {
                    Emit();
                    spans.Add(new Span(s.Substring(i + ticks, end - i - ticks).Trim(), style | Style.Code, link));
                    i = end + ticks;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c && Opens(s, i, 2))
            {
                var marker = new string(c, 2);
                var end = Closing(s, marker, i + 2);
                if (end > i + 2)
                {
                    Emit();
                    Inline(s.Substring(i + 2, end - i - 2), style | Style.Bold, spans, link);
                    i = end + 2;
                    continue;
                }
            }
            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
            {
                var end = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Emit();
                    Inline(s.Substring(i + 2, end - i - 2), style | Style.Strike, spans, link);
                    i = end + 2;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && Opens(s, i, 1))
            {
                var end = Closing(s, c.ToString(), i + 1);
                if (end > i + 1)
                {
                    Emit();
                    Inline(s.Substring(i + 1, end - i - 1), style | Style.Italic, spans, link);
                    i = end + 1;
                    continue;
                }
            }
            if (c == '[' && link is null)
            {
                var close = s.IndexOf("](", i + 1, StringComparison.Ordinal);
                var end = close > i ? s.IndexOf(')', close + 2) : -1;
                if (close > i + 1 && end > close + 2 && s.IndexOf('\n', i, end - i) < 0)
                {
                    var url = s.Substring(close + 2, end - close - 2).Trim();
                    var space = url.IndexOf(' ');
                    if (space > 0) url = url.Substring(0, space); // drop a "title"
                    Emit();
                    Inline(s.Substring(i + 1, close - i - 1), style, spans, url);
                    i = end + 1;
                    continue;
                }
            }
            if (c == '<' && link is null)
            {
                var end = s.IndexOf('>', i + 1);
                if (end > i && IsUrl(s.Substring(i + 1, end - i - 1)))
                {
                    Emit();
                    var url = s.Substring(i + 1, end - i - 1);
                    spans.Add(new Span(url, style, url));
                    i = end + 1;
                    continue;
                }
            }
            if ((c == 'h' || c == 'H') && link is null && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && IsUrlStart(s, i))
            {
                var end = i;
                while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '<' && s[end] != '>' && s[end] != '"') end++;
                while (end > i && ".,;:!?)]'".IndexOf(s[end - 1]) >= 0) end--;
                Emit();
                var url = s.Substring(i, end - i);
                spans.Add(new Span(url, style, url));
                i = end;
                continue;
            }
            plain.Append(c);
            i++;
        }
        Emit();
    }

    /// <summary>A * or _ run opens emphasis when text follows it; _ only at a word start (snake_case stays as is).</summary>
    private static bool Opens(string s, int i, int width)
    {
        if (i + width >= s.Length || char.IsWhiteSpace(s[i + width])) return false;
        return s[i] != '_' || i == 0 || !char.IsLetterOrDigit(s[i - 1]);
    }

    private static int Closing(string s, string marker, int from)
    {
        var at = from;
        while (true)
        {
            var end = s.IndexOf(marker, at, StringComparison.Ordinal);
            if (end < 0) return -1;
            // For a single marker, skip a double one (that is bold inside the italic text).
            if (marker.Length == 1 && end + 1 < s.Length && s[end + 1] == marker[0]) { at = end + 2; continue; }
            if (end > from && !char.IsWhiteSpace(s[end - 1]) &&
                (marker[0] != '_' || end + marker.Length >= s.Length || !char.IsLetterOrDigit(s[end + marker.Length])))
                return end;
            at = end + 1;
        }
    }

    private static bool IsUrlStart(string s, int i) =>
        string.Compare(s, i, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) == 0 ||
        string.Compare(s, i, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0;

    public static bool IsUrl(string s) =>
        (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) &&
        s.IndexOfAny(new[] { ' ', '\n', '\t', '"' }) < 0;
}
