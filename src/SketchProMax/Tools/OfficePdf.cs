using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using SketchProMax.Chat;

namespace SketchProMax.Tools;

/// <summary>
/// A PDF from the chat's Markdown, A4, laid out here (no Word or printer needed): the title and subtitle, headings,
/// paragraphs with bold, italic, code and clickable links, nested lists, tables (shaded header repeated on each page,
/// numbers right aligned, long text wrapped), quotes, code, rules, PNG and JPEG pictures from this PC, page breaks, and
/// "Page X of Y" in the footer. It uses the PDF's built-in Helvetica, so text outside Western European letters (Arabic,
/// Chinese…) shows as "?": for those, make a Word file and save it as PDF from Word. No Revit or WPF.
/// </summary>
internal static class OfficePdf
{
    private const double MarginX = 50, MarginTop = 56, MarginBottom = 64;
    private const string Accent = "1F3A5F", Body = "222222", Muted = "6B6B6B", Grid = "BFBFBF", HeadFill = "DCE6F2", TotalFill = "F2F2F2", Band = "F7F9FC", CodeFill = "F2F2F2";

    private enum F { Regular = 1, Bold = 2, Italic = 3, BoldItalic = 4, Mono = 5 }

    /// <summary>Writes the file; returns the page count and the pictures that couldn't be put in.</summary>
    public static (int Pages, List<string> Missing) Write(string path, string title, string subtitle, string markdown, bool landscape)
    {
        (int, List<string>) result = (0, []);
        OfficeDocs.WriteFile(path, s => result = Write(s, title, subtitle, markdown, landscape, DateTime.Now));
        return result;
    }

    public static (int Pages, List<string> Missing) Write(Stream output, string title, string subtitle, string markdown, bool landscape, DateTime when)
    {
        var doc = new Layout(landscape ? 841.89 : 595.28, landscape ? 595.28 : 841.89);
        if (title.Length > 0) doc.TitleBlock(title, subtitle);
        foreach (var block in OfficeDocs.Blocks(markdown)) doc.Block(block);
        doc.Footers(title);
        Save(output, doc, title, when);
        return (doc.Pages.Count, doc.Missing);
    }

    // ---- Text: WinAnsi bytes and Helvetica widths ---------------------------------------------------------------------

    // Helvetica and Helvetica-Bold advance widths (1/1000 em) for ' ' to '~'.
    private static readonly int[] Regular =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833,
        556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly int[] BoldW =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        333, 333, 584, 584, 584, 611, 975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, 333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889,
        611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    private static double Width(string encoded, F font, double size)
    {
        if (font == F.Mono) return encoded.Length * 0.6 * size;
        var table = font is F.Bold or F.BoldItalic ? BoldW : Regular;
        double units = 0;
        foreach (var c in encoded)
            units += c is >= ' ' and <= '~' ? table[c - 32] : c == '\u0095' ? 350 : c is '\u0096' ? 556 : c is '\u0097' ? 1000 : c is '\u0091' or '\u0092' ? 222 : 556;
        return units / 1000 * size;
    }

    private static readonly Dictionary<char, char> Cp1252 = new()
    {
        ['€'] = '\u0080', ['‚'] = '\u0082', ['ƒ'] = '\u0083', ['„'] = '\u0084', ['…'] = '\u0085', ['†'] = '\u0086', ['‡'] = '\u0087',
        ['ˆ'] = '\u0088', ['‰'] = '\u0089', ['Š'] = '\u008A', ['‹'] = '\u008B', ['Œ'] = '\u008C', ['Ž'] = '\u008E', ['\u2018'] = '\u0091',
        ['\u2019'] = '\u0092', ['\u201C'] = '\u0093', ['\u201D'] = '\u0094', ['•'] = '\u0095', ['–'] = '\u0096', ['—'] = '\u0097',
        ['˜'] = '\u0098', ['™'] = '\u0099', ['š'] = '\u009A', ['›'] = '\u009B', ['œ'] = '\u009C', ['ž'] = '\u009E', ['Ÿ'] = '\u009F',
    };

    private static readonly Dictionary<char, string> Spelled = new()
    {
        ['≤'] = "<=", ['≥'] = ">=", ['≠'] = "!=", ['≈'] = "~", ['→'] = "->", ['←'] = "<-", ['↑'] = "^", ['↓'] = "v", ['✓'] = "v", ['✔'] = "v",
        ['✗'] = "x", ['✘'] = "x", ['−'] = "-", ['‐'] = "-", ['‑'] = "-", ['\u00A0'] = " ", ['\u2009'] = " ", ['\u202F'] = " ", ['\u2002'] = " ",
        ['\u2003'] = " ", ['√'] = "sqrt", ['∑'] = "Sum", ['Δ'] = "Delta", ['∆'] = "Delta", ['φ'] = "phi", ['Ø'] = "Ø", ['⌀'] = "Ø", ['π'] = "pi",
        ['α'] = "alpha", ['β'] = "beta", ['γ'] = "gamma", ['σ'] = "sigma", ['τ'] = "tau", ['ε'] = "e", ['∞'] = "inf", ['\t'] = "    ",
    };

    /// <summary>The text as WinAnsi bytes (one char per byte): what Helvetica can show, the rest spelled out or "?".</summary>
    internal static string Encode(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~' || c is >= '\u00A1' and <= '\u00FF') sb.Append(c);
            else if (Cp1252.TryGetValue(c, out var b)) sb.Append(b);
            else if (Spelled.TryGetValue(c, out var s)) sb.Append(s);
            else if (c is '\n' or '\r') sb.Append(' ');
            else if (char.IsLowSurrogate(c) || c < ' ' || c is '\uFE0F' or '\u200B' or '\u200D') { }
            else sb.Append('?');
        }
        return sb.ToString();
    }

    // ---- Layout ----------------------------------------------------------------------------------------------------

    private sealed record Frag(string Text, F Font, double Size, string Color, string? Link = null);

    private sealed record Piece(Frag Frag, double X, double Width);

    private sealed class Line
    {
        public readonly List<Piece> Pieces = [];
        public double Width;
    }

    internal sealed class Page
    {
        public readonly StringBuilder Content = new();
        public readonly List<(double X, double Y, double W, double H, string Url)> Links = [];
        public readonly HashSet<int> Images = [];
    }

    internal sealed class Image(int w, int h, string colorSpace, int bpc, byte[] data, string? parms, byte[]? alpha)
    {
        public int W = w, H = h, Bpc = bpc;
        public string ColorSpace = colorSpace;
        public byte[] Data = data;
        public string? Parms = parms;
        public byte[]? Alpha = alpha;
    }

    private sealed class Layout(double pageW, double pageH)
    {
        public readonly List<Page> Pages = [];
        public readonly List<Image> Images = [];
        public readonly List<string> Missing = [];
        private readonly double _pageW = pageW, _pageH = pageH;
        private double _y; // from the top of the page
        private Page _page = null!;
        private bool _empty = true;

        private double ContentW => _pageW - 2 * MarginX;
        private double Bottom => _pageH - MarginBottom;

        private Page Current
        {
            get
            {
                if (Pages.Count == 0) NewPage();
                return _page;
            }
        }

        private void NewPage()
        {
            _page = new Page();
            Pages.Add(_page);
            _y = MarginTop;
            _empty = true;
        }

        private void Ensure(double height)
        {
            _ = Current;
            if (!_empty && _y + height > Bottom) NewPage();
        }

        public void TitleBlock(string title, string subtitle)
        {
            _ = Current;
            Paragraph(Frags(title, F.Bold, 22, Accent), 22 * 1.2, 0);
            if (subtitle.Length > 0)
            {
                _y += 2;
                Paragraph(Frags(subtitle, F.Regular, 12, Muted), 12 * 1.35, 0);
            }
            _y += 6;
            Fill(MarginX, _y, ContentW, 1.2, Accent);
            _y += 16;
        }

        public void Block(Markdown.Block block)
        {
            switch (block)
            {
                case Markdown.Heading h:
                {
                    var size = h.Level switch { 1 => 16.0, 2 => 13.5, _ => 11.5 };
                    if (!_empty) _y += h.Level == 1 ? 12 : 9;
                    // Kept with what follows: a heading never sits alone at the bottom of a page.
                    Ensure(size * 1.3 + 40);
                    Paragraph(Frags(h.Text, F.Bold, size, Accent), size * 1.3, 0);
                    _y += 4;
                    break;
                }
                case Markdown.Paragraph p:
                    Paragraph(Frags(p.Text, F.Regular, 10.5, Body), 10.5 * 1.4, 0);
                    _y += 6;
                    break;
                case Markdown.ListBlock list:
                    List(list);
                    break;
                case Markdown.Table t:
                    Table(t);
                    break;
                case Markdown.Code c:
                    Code(c.Text);
                    break;
                case Markdown.Quote q:
                {
                    var from = _y;
                    var page = Current;
                    Paragraph(Frags(q.Text, F.Italic, 10.5, Muted), 10.5 * 1.4, 14);
                    if (Current == page) Fill(MarginX + 2, from, 2.5, _y - from, Grid);
                    _y += 6;
                    break;
                }
                case Markdown.Rule:
                    Ensure(14);
                    _y += 4;
                    Fill(MarginX, _y, ContentW, 0.6, Grid);
                    _y += 10;
                    break;
                case OfficeDocs.PageBreak:
                    if (!_empty) NewPage();
                    break;
                case OfficeDocs.ImageBlock img:
                    Picture(img);
                    break;
            }
        }

        private static List<Frag> Frags(string markdown, F font, double size, string color)
        {
            var list = new List<Frag>();
            foreach (var s in Markdown.Inline(markdown))
            {
                var bold = font is F.Bold or F.BoldItalic || s.Style.HasFlag(Markdown.Style.Bold);
                var italic = font is F.Italic or F.BoldItalic || s.Style.HasFlag(Markdown.Style.Italic);
                var f = s.Style.HasFlag(Markdown.Style.Code) ? F.Mono : bold && italic ? F.BoldItalic : bold ? F.Bold : italic ? F.Italic : F.Regular;
                var link = s.Link is { } url && Markdown.IsUrl(url) ? url : null;
                // Hard line breaks stay as \n so the wrap starts a new line there.
                var parts = s.Text.Split('\n');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (i > 0) list.Add(new Frag("\n", f, size, color));
                    if (parts[i].Length > 0)
                        list.Add(new Frag(Encode(parts[i]), f, f == F.Mono ? size * 0.92 : size, link is null ? color : "0563C1", link));
                }
            }
            return list;
        }

        /// <summary>Breaks the fragments into lines no wider than <paramref name="max"/>; words longer than a line are cut.</summary>
        private static List<Line> Wrap(List<Frag> frags, double max)
        {
            var lines = new List<Line> { new() };
            void Add(Frag f, string text)
            {
                var line = lines[lines.Count - 1];
                var w = Width(text, f.Font, f.Size);
                if (line.Pieces.Count > 0 && line.Pieces[line.Pieces.Count - 1] is var last && last.Frag with { Text = "" } == f with { Text = "" })
                    line.Pieces[line.Pieces.Count - 1] = new Piece(last.Frag with { Text = last.Frag.Text + text }, last.X, last.Width + w);
                else line.Pieces.Add(new Piece(f with { Text = text }, line.Width, w));
                line.Width += w;
            }
            foreach (var f in frags)
            {
                if (f.Text == "\n") { lines.Add(new Line()); continue; }
                foreach (var token in System.Text.RegularExpressions.Regex.Split(f.Text, @"(\s+)"))
                {
                    if (token.Length == 0) continue;
                    var line = lines[lines.Count - 1];
                    var space = token.Trim().Length == 0;
                    if (space)
                    {
                        if (line.Pieces.Count > 0) Add(f, " ");
                        continue;
                    }
                    var w = Width(token, f.Font, f.Size);
                    if (line.Width + w > max && line.Pieces.Count > 0)
                    {
                        Trim(line);
                        lines.Add(new Line());
                    }
                    if (w <= max) { Add(f, token); continue; }
                    // A word longer than the line: cut it where it fills the line.
                    var rest = token;
                    while (rest.Length > 0)
                    {
                        var room = max - lines[lines.Count - 1].Width;
                        var n = 1;
                        while (n < rest.Length && Width(rest.Substring(0, n + 1), f.Font, f.Size) <= room) n++;
                        Add(f, rest.Substring(0, n));
                        rest = rest.Substring(n);
                        if (rest.Length > 0) lines.Add(new Line());
                    }
                }
            }
            foreach (var line in lines) Trim(line);
            return lines;
        }

        private static void Trim(Line line)
        {
            if (line.Pieces.Count == 0) return;
            var last = line.Pieces[line.Pieces.Count - 1];
            var text = last.Frag.Text.TrimEnd(' ');
            if (text.Length == last.Frag.Text.Length) return;
            var w = Width(text, last.Frag.Font, last.Frag.Size);
            line.Width -= last.Width - w;
            if (text.Length == 0) line.Pieces.RemoveAt(line.Pieces.Count - 1);
            else line.Pieces[line.Pieces.Count - 1] = new Piece(last.Frag with { Text = text }, last.X, w);
        }

        private void Paragraph(List<Frag> frags, double lineH, double indent)
        {
            foreach (var line in Wrap(frags, ContentW - indent))
            {
                Ensure(lineH);
                DrawLine(line, MarginX + indent, _y + lineH * 0.78);
                _y += lineH;
                _empty = false;
            }
        }

        private void DrawLine(Line line, double x, double baseline)
        {
            var page = Current;
            foreach (var p in line.Pieces)
            {
                Text(page, p.Frag, x + p.X, baseline);
                if (p.Frag.Link is { } url)
                    page.Links.Add((x + p.X, baseline - p.Frag.Size * 0.8, p.Width, p.Frag.Size * 1.05, url));
            }
        }

        private void Text(Page page, Frag f, double x, double baseline)
        {
            if (f.Text.Length == 0) return;
            page.Content.Append($"BT /F{(int)f.Font} {N(f.Size)} Tf {Rgb(f.Color)} rg 1 0 0 1 {N(x)} {N(_pageH - baseline)} Tm ({Str(f.Text)}) Tj ET\n");
            if (f.Link is not null)
                page.Content.Append($"{Rgb(f.Color)} RG 0.5 w {N(x)} {N(_pageH - baseline - 1.5)} m {N(x + Width(f.Text, f.Font, f.Size))} {N(_pageH - baseline - 1.5)} l S\n");
        }

        private void Fill(double x, double top, double w, double h, string color) =>
            Current.Content.Append($"{Rgb(color)} rg {N(x)} {N(_pageH - top - h)} {N(w)} {N(h)} re f\n");

        private void Stroke(double x, double top, double w, double h, string color) =>
            Current.Content.Append($"{Rgb(color)} RG 0.5 w {N(x)} {N(_pageH - top - h)} {N(w)} {N(h)} re S\n");

        private void List(Markdown.ListBlock list)
        {
            var counters = new int[12];
            var lastDepth = -1;
            foreach (var item in list.Items)
            {
                var depth = Math.Min(5, item.Depth);
                if (depth < lastDepth) for (var d = depth + 1; d < counters.Length; d++) counters[d] = 0;
                if (depth > lastDepth) for (var d = depth; d < counters.Length; d++) counters[d] = 0;
                lastDepth = depth;
                counters[depth]++;
                var indent = 16.0 * (depth + 1);
                const double size = 10.5, lineH = size * 1.4;
                var marker = item.Ordered ? counters[depth].ToString(CultureInfo.InvariantCulture) + "." : depth == 0 ? "\u0095" : depth == 1 ? "\u0096" : "-";
                var lines = Wrap(Frags(item.Text, F.Regular, size, Body), ContentW - indent);
                for (var i = 0; i < lines.Count; i++)
                {
                    Ensure(lineH);
                    var baseline = _y + lineH * 0.78;
                    if (i == 0)
                    {
                        var mw = Width(marker, F.Regular, size);
                        Text(Current, new Frag(marker, F.Regular, size, item.Ordered ? Body : "4F81BD"), MarginX + indent - 5 - mw, baseline);
                    }
                    DrawLine(lines[i], MarginX + indent, baseline);
                    _y += lineH;
                    _empty = false;
                }
                _y += 2;
            }
            _y += 5;
        }

        private void Code(string text)
        {
            const double size = 8.8, lineH = size * 1.35;
            var chars = Math.Max(10, (int)((ContentW - 12) / (size * 0.6)));
            var lines = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = Encode(raw.Replace("\t", "    "));
                if (line.Length == 0) lines.Add("");
                for (var i = 0; i < line.Length; i += chars) lines.Add(line.Substring(i, Math.Min(chars, line.Length - i)));
            }
            _y += 2;
            foreach (var line in lines)
            {
                Ensure(lineH);
                Fill(MarginX, _y, ContentW, lineH, CodeFill);
                Text(Current, new Frag(line, F.Mono, size, Body), MarginX + 6, _y + lineH * 0.76);
                _y += lineH;
                _empty = false;
            }
            _y += 8;
        }

        private void Picture(OfficeDocs.ImageBlock img)
        {
            var picture = OfficeDocs.LoadPicture(img.Path);
            var image = picture is null ? null : ToPdf(picture);
            if (image is null)
            {
                Missing.Add(img.Path);
                Paragraph(Frags($"[Picture not found or not a PNG or JPEG: {Path.GetFileName(img.Path)}]", F.Italic, 9.5, Muted), 13, 0);
                return;
            }
            // At 96 dpi (0.75 pt a pixel), no wider than the text and no taller than two thirds of the page.
            var scale = Math.Min(0.75, Math.Min(ContentW / image.W, (Bottom - MarginTop) * 0.66 / image.H));
            double w = image.W * scale, h = image.H * scale;
            var captionH = img.Caption.Length > 0 ? 18 : 0;
            // Too tall for what is left of the page: shrink it to fit when that keeps at least 60% of its size, else a new page.
            var room = Bottom - _y - captionH - 8;
            if (!_empty && h > room && room >= h * 0.6)
            {
                w *= room / h;
                h = room;
            }
            Ensure(h + captionH + 8);
            _y += 4;
            var index = Images.Count;
            Images.Add(image);
            var page = Current;
            page.Images.Add(index);
            var x = MarginX + (ContentW - w) / 2;
            page.Content.Append($"q {N(w)} 0 0 {N(h)} {N(x)} {N(_pageH - _y - h)} cm /Im{index + 1} Do Q\n");
            _y += h + 4;
            _empty = false;
            if (img.Caption.Length > 0)
            {
                foreach (var line in Wrap(Frags(img.Caption, F.Italic, 9, Muted), ContentW))
                {
                    Ensure(13);
                    DrawLine(line, MarginX + (ContentW - line.Width) / 2, _y + 10);
                    _y += 13;
                }
            }
            _y += 8;
        }

        private void Table(Markdown.Table t)
        {
            var columns = Math.Max(t.Header.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
            if (columns == 0) return;
            const double size = 9, pad = 4, lineH = size * 1.3;
            string CellText(IReadOnlyList<string> row, int c) => c < row.Count ? row[c] : "";
            List<Frag> CellFrags(string text, bool head) => Frags(text, head ? F.Bold : F.Regular, size, Body);

            // Natural width (the longest line) and the longest word of each column, header included.
            var natural = new double[columns];
            var minimum = new double[columns];
            var all = new List<(IReadOnlyList<string> Row, bool Head)>();
            if (t.Header.Count > 0) all.Add((t.Header, true));
            all.AddRange(t.Rows.Select(r => (r, false)));
            foreach (var (row, head) in all)
                for (var c = 0; c < columns; c++)
                {
                    var frags = CellFrags(CellText(row, c), head);
                    natural[c] = Math.Max(natural[c], frags.Sum(f => f.Text == "\n" ? 0 : Width(f.Text, f.Font, f.Size)) + 2 * pad + 1);
                    foreach (var f in frags)
                        foreach (var word in f.Text.Split(' '))
                            minimum[c] = Math.Max(minimum[c], Math.Min(140, Width(word, f.Font, f.Size) + 2 * pad + 1));
                }
            var widths = Widths(natural, minimum, ContentW);

            var right = new bool[columns];
            var center = new bool[columns];
            for (var c = 0; c < columns; c++)
            {
                var align = c < t.Align.Count ? t.Align[c] : 'l';
                var cells = t.Rows.Select(r => CellText(r, c)).Where(x => OfficeDocs.Plain(x).Trim().Length > 0).ToList();
                right[c] = align == 'r' || align == 'l' && cells.Count > 0 && cells.All(OfficeDocs.LooksNumeric);
                center[c] = align == 'c';
            }

            List<List<Line>> Cells(IReadOnlyList<string> row, bool head) =>
                Enumerable.Range(0, columns).Select(c => Wrap(CellFrags(CellText(row, c), head), widths[c] - 2 * pad)).ToList();
            double Height(List<List<Line>> cells) => cells.Max(l => l.Count) * lineH + 2 * pad;

            void Row(List<List<Line>> cells, string? fill)
            {
                var h = Height(cells);
                var x = MarginX;
                for (var c = 0; c < columns; c++)
                {
                    if (fill is not null) Fill(x, _y, widths[c], h, fill);
                    Stroke(x, _y, widths[c], h, Grid);
                    var ly = _y + pad;
                    foreach (var line in cells[c])
                    {
                        var lx = right[c] ? x + widths[c] - pad - line.Width : center[c] ? x + (widths[c] - line.Width) / 2 : x + pad;
                        DrawLine(line, lx, ly + lineH * 0.76);
                        ly += lineH;
                    }
                    x += widths[c];
                }
                _y += h;
                _empty = false;
            }

            var header = t.Header.Count > 0 ? Cells(t.Header, true) : null;
            var headerH = header is null ? 0 : Height(header);
            _y += 2;
            var firstRow = t.Rows.Count > 0 ? Cells(t.Rows[0], false) : null;
            Ensure(headerH + (firstRow is null ? 0 : Height(firstRow)));
            if (header is not null) Row(header, HeadFill);
            for (var r = 0; r < t.Rows.Count; r++)
            {
                var cells = r == 0 ? firstRow! : Cells(t.Rows[r], false);
                var h = Height(cells);
                if (_y + h > Bottom)
                {
                    NewPage();
                    if (header is not null) Row(header, HeadFill);
                }
                var total = Markdown.Inline(CellText(t.Rows[r], 0)).FirstOrDefault() is { } first && first.Style.HasFlag(Markdown.Style.Bold) && first.Text.Trim().Length > 0;
                Row(cells, total ? TotalFill : r % 2 == 1 ? Band : null);
            }
            _y += 10;
        }

        /// <summary>Column widths that fill the text width: grown in proportion when there is room, else shrunk down to their longest word.</summary>
        private static double[] Widths(double[] natural, double[] minimum, double available)
        {
            var n = natural.Length;
            var widths = natural.ToArray();
            var sum = widths.Sum();
            if (sum <= available)
            {
                for (var c = 0; c < n; c++) widths[c] += (available - sum) * natural[c] / sum;
                return widths;
            }
            var floor = minimum.Select((m, c) => Math.Min(m, natural[c])).ToArray();
            var flexible = natural.Select((w, c) => w - floor[c]).Sum();
            var excess = sum - available;
            if (flexible >= excess)
            {
                for (var c = 0; c < n; c++) widths[c] = natural[c] - excess * (natural[c] - floor[c]) / flexible;
                return widths;
            }
            // Even the longest words don't fit: share the width by those (words are cut where needed).
            var floors = floor.Sum();
            return floor.Select(f => available * f / floors).ToArray();
        }

        public void Footers(string title)
        {
            if (Pages.Count == 0) NewPage();
            var t = Encode(title.Length > 60 ? title.Substring(0, 57) + "..." : title);
            for (var i = 0; i < Pages.Count; i++)
            {
                var page = Pages[i];
                var label = Encode($"Page {i + 1} of {Pages.Count}");
                var y = _pageH - MarginBottom + 30;
                page.Content.Append($"{Rgb(Grid)} RG 0.5 w {N(MarginX)} {N(_pageH - y + 12)} m {N(_pageW - MarginX)} {N(_pageH - y + 12)} l S\n");
                if (t.Length > 0) page.Content.Append($"BT /F1 8 Tf {Rgb(Muted)} rg 1 0 0 1 {N(MarginX)} {N(_pageH - y)} Tm ({Str(t)}) Tj ET\n");
                page.Content.Append($"BT /F1 8 Tf {Rgb(Muted)} rg 1 0 0 1 {N(_pageW - MarginX - Width(label, F.Regular, 8))} {N(_pageH - y)} Tm ({Str(label)}) Tj ET\n");
            }
        }

        public double PageW => _pageW;
        public double PageH => _pageH;
    }

    // ---- Pictures --------------------------------------------------------------------------------------------------

    /// <summary>A PNG or JPEG as a PDF image; null for kinds a PDF reader can't take this way (16-bit or interlaced PNG).</summary>
    internal static Image? ToPdf(OfficeDocs.Picture p)
    {
        try
        {
            if (p.Ext == "jpeg")
            {
                var components = OfficeDocs.JpegComponents(p.Bytes);
                var space = components == 1 ? "/DeviceGray" : components == 4 ? "/DeviceCMYK" : "/DeviceRGB";
                return new Image(p.Width, p.Height, space, 8, p.Bytes, null, null) { Parms = "JPEG" };
            }
            return Png(p.Bytes);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    private static Image? Png(byte[] b)
    {
        int width = 0, height = 0, depth = 0, type = 0, interlace = 0;
        byte[]? palette = null;
        var idat = new MemoryStream();
        var at = 8;
        while (at + 8 <= b.Length)
        {
            var length = OfficeDocs.BigEndian(b, at);
            var kind = Encoding.ASCII.GetString(b, at + 4, 4);
            if (length < 0 || at + 12 + length > b.Length) break;
            switch (kind)
            {
                case "IHDR":
                    width = OfficeDocs.BigEndian(b, at + 8);
                    height = OfficeDocs.BigEndian(b, at + 12);
                    depth = b[at + 16];
                    type = b[at + 17];
                    interlace = b[at + 20];
                    break;
                case "PLTE":
                    palette = new byte[length];
                    Array.Copy(b, at + 8, palette, 0, length);
                    break;
                case "IDAT":
                    idat.Write(b, at + 8, length);
                    break;
            }
            if (kind == "IEND") break;
            at += 12 + length;
        }
        if (width <= 0 || height <= 0 || interlace != 0 || idat.Length == 0) return null;
        var data = idat.ToArray();
        switch (type)
        {
            case 0 when depth <= 8:
                return new Image(width, height, "/DeviceGray", depth, data, $"<< /Predictor 15 /Colors 1 /BitsPerComponent {depth} /Columns {width} >>", null);
            case 2 when depth == 8:
                return new Image(width, height, "/DeviceRGB", 8, data, $"<< /Predictor 15 /Colors 3 /BitsPerComponent 8 /Columns {width} >>", null);
            case 3 when palette is not null && depth <= 8:
                var hex = new StringBuilder();
                foreach (var x in palette) hex.Append(x.ToString("X2", CultureInfo.InvariantCulture));
                return new Image(width, height, $"[/Indexed /DeviceRGB {palette.Length / 3 - 1} <{hex}>]", depth, data,
                    $"<< /Predictor 15 /Colors 1 /BitsPerComponent {depth} /Columns {width} >>", null);
            case 4 or 6 when depth == 8:
            {
                // Colour with transparency: split the colour from the alpha (the PDF takes alpha as a soft mask).
                var channels = type == 6 ? 4 : 2;
                var raw = Unfilter(Inflate(data), width, height, channels);
                var colour = channels - 1;
                var rgb = new byte[width * height * colour];
                var alpha = new byte[width * height];
                for (int i = 0, o = 0, a = 0; i < raw.Length; i += channels)
                {
                    for (var c = 0; c < colour; c++) rgb[o++] = raw[i + c];
                    alpha[a++] = raw[i + colour];
                }
                return new Image(width, height, colour == 3 ? "/DeviceRGB" : "/DeviceGray", 8, Deflate(rgb), null, Deflate(alpha));
            }
            default:
                return null;
        }
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var input = new MemoryStream(zlib, 2, zlib.Length - 2); // past the zlib header
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Unfilter(byte[] data, int width, int height, int bpp)
    {
        var stride = width * bpp;
        var result = new byte[stride * height];
        var prev = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = data[y * (stride + 1)];
            var row = new byte[stride];
            Array.Copy(data, y * (stride + 1) + 1, row, 0, stride);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? row[x - bpp] : 0, up = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                row[x] = filter switch
                {
                    1 => (byte)(row[x] + a),
                    2 => (byte)(row[x] + up),
                    3 => (byte)(row[x] + (a + up) / 2),
                    4 => (byte)(row[x] + Paeth(a, up, c)),
                    _ => row[x],
                };
            }
            Array.Copy(row, 0, result, y * stride, stride);
            prev = row;
        }
        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>zlib (what /FlateDecode reads): header, deflate data, Adler-32.</summary>
    internal static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data, 0, data.Length);
        uint s1 = 1, s2 = 0;
        foreach (var x in data)
        {
            s1 = (s1 + x) % 65521;
            s2 = (s2 + s1) % 65521;
        }
        var adler = (s2 << 16) | s1;
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)adler);
        return output.ToArray();
    }

    // ---- The file ----------------------------------------------------------------------------------------------------

    private static void Save(Stream output, Layout doc, string title, DateTime when)
    {
        var objects = new List<byte[]>();
        int Add(string text) { objects.Add(Bytes(text)); return objects.Count; }
        int AddStream(string dict, byte[] data)
        {
            var head = Bytes($"<< {dict} /Length {data.Length} >>\nstream\n");
            var tail = Bytes("\nendstream");
            var all = new byte[head.Length + data.Length + tail.Length];
            head.CopyTo(all, 0);
            data.CopyTo(all, head.Length);
            tail.CopyTo(all, head.Length + data.Length);
            objects.Add(all);
            return objects.Count;
        }

        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add(""); // the page tree, filled in once the pages have numbers
        string[] fonts = ["Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique", "Courier"];
        var fontIds = fonts.Select(f => Add(f == "Courier"
            ? $"<< /Type /Font /Subtype /Type1 /BaseFont /{f} /Encoding /WinAnsiEncoding >>"
            : $"<< /Type /Font /Subtype /Type1 /BaseFont /{f} /Encoding /WinAnsiEncoding >>")).ToArray();
        var fontRes = string.Concat(fontIds.Select((id, i) => $"/F{i + 1} {id} 0 R "));

        var imageIds = new List<int>();
        foreach (var img in doc.Images)
        {
            if (img.Parms == "JPEG")
            {
                imageIds.Add(AddStream($"/Type /XObject /Subtype /Image /Width {img.W} /Height {img.H} /ColorSpace {img.ColorSpace} /BitsPerComponent 8 /Filter /DCTDecode" +
                                       (img.ColorSpace == "/DeviceCMYK" ? " /Decode [1 0 1 0 1 0 1 0]" : ""), img.Data));
                continue;
            }
            var mask = img.Alpha is null ? 0 : AddStream($"/Type /XObject /Subtype /Image /Width {img.W} /Height {img.H} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", img.Alpha);
            imageIds.Add(AddStream($"/Type /XObject /Subtype /Image /Width {img.W} /Height {img.H} /ColorSpace {img.ColorSpace} /BitsPerComponent {img.Bpc} /Filter /FlateDecode" +
                                   (img.Parms is null ? "" : " /DecodeParms " + img.Parms) + (mask > 0 ? $" /SMask {mask} 0 R" : ""), img.Data));
        }

        var pageIds = new List<int>();
        foreach (var page in doc.Pages)
        {
            var content = AddStream("/Filter /FlateDecode", Deflate(Bytes(page.Content.ToString())));
            var annots = page.Links.Select(l => Add(
                $"<< /Type /Annot /Subtype /Link /Rect [{N(l.X)} {N(doc.PageH - l.Y - l.H)} {N(l.X + l.W)} {N(doc.PageH - l.Y)}] /Border [0 0 0] " +
                $"/A << /S /URI /URI ({Str(Encode(l.Url))}) >> >>")).ToList();
            var xobjects = page.Images.Count == 0 ? "" : " /XObject << " + string.Concat(page.Images.Select(i => $"/Im{i + 1} {imageIds[i]} 0 R ")) + ">>";
            pageIds.Add(Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(doc.PageW)} {N(doc.PageH)}] /Contents {content} 0 R " +
                            $"/Resources << /Font << {fontRes}>>{xobjects} >>" + (annots.Count > 0 ? " /Annots [" + string.Join(" ", annots.Select(a => a + " 0 R")) + "]" : "") + " >>"));
        }
        objects[1] = Bytes($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => p + " 0 R"))}] /Count {pageIds.Count} >>");
        var info = Add($"<< /Title ({Str(Encode(title))}) /Producer (Sketch ProMax) /Creator (Sketch ProMax) /CreationDate (D:{when.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}) >>");

        var offsets = new List<long>();
        var head = Bytes("%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");
        output.Write(head, 0, head.Length);
        long position = head.Length;
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(position);
            var open = Bytes($"{i + 1} 0 obj\n");
            var close = Bytes("\nendobj\n");
            output.Write(open, 0, open.Length);
            output.Write(objects[i], 0, objects[i].Length);
            output.Write(close, 0, close.Length);
            position += open.Length + objects[i].Length + close.Length;
        }
        var xref = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) xref.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xref.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info {info} 0 R >>\nstartxref\n{position}\n%%EOF\n");
        var tail = Bytes(xref.ToString());
        output.Write(tail, 0, tail.Length);
    }

    /// <summary>One byte per char (the text is WinAnsi already, or ASCII).</summary>
    private static byte[] Bytes(string s)
    {
        var b = new byte[s.Length];
        for (var i = 0; i < s.Length; i++) b[i] = s[i] < 256 ? (byte)s[i] : (byte)'?';
        return b;
    }

    private static string Str(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "").Replace("\n", " ");

    private static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Rgb(string hex)
    {
        int Part(int i) => int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return $"{N(Part(0) / 255.0)} {N(Part(2) / 255.0)} {N(Part(4) / 255.0)}";
    }
}
