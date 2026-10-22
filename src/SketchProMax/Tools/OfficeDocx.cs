using System.Globalization;
using System.IO;
using System.Text;
using SketchProMax.Chat;

namespace SketchProMax.Tools;

/// <summary>
/// A Word file (.docx) from the chat's Markdown, A4: the title and subtitle, headings, paragraphs with bold, italic,
/// code and links, bullet and numbered lists (nested), tables (shaded header repeated on each page, numbers right
/// aligned), quotes, code, rules, pictures from this PC scaled to the page, page breaks, and "Page X of Y" in the
/// footer. Plain WordprocessingML written by hand; no Office, Revit or WPF.
/// </summary>
internal static class OfficeDocx
{
    private const string Font = "Calibri", Accent = "1F3A5F", Muted = "595959", Grid = "BFBFBF", HeadFill = "DCE6F2", CodeFill = "F2F2F2";
    private const int A4W = 11906, A4H = 16838, Margin = 1134; // twips (20 mm margins)

    /// <summary>Writes the file; returns the pictures that couldn't be put in (missing or not PNG/JPEG).</summary>
    public static List<string> Write(string path, string title, string subtitle, string markdown, bool landscape)
    {
        var missing = new List<string>();
        OfficeDocs.WriteFile(path, s => missing = Write(s, title, subtitle, markdown, landscape, DateTime.Now));
        return missing;
    }

    public static List<string> Write(Stream output, string title, string subtitle, string markdown, bool landscape, DateTime when)
    {
        var w = new Writer(landscape);
        if (title.Length > 0) w.Body.Append(Para(Run(title, size: 40, bold: true, color: Accent), style: "Title"));
        if (subtitle.Length > 0) w.Body.Append(Para(Run(subtitle, size: 24, color: Muted), style: "Subtitle"));
        foreach (var block in OfficeDocs.Blocks(markdown)) w.Block(block);
        w.Body.Append(w.Section());

        using var zip = new OfficeDocs.Package(output);
        zip.Text("[Content_Types].xml", OfficeDocs.XmlHead +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
            "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>" +
            "<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "</Types>");
        zip.Text("_rels/.rels", OfficeDocs.XmlHead +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "</Relationships>");
        zip.Text("docProps/core.xml", OfficeDocs.Core(title, when));
        zip.Text("word/document.xml", OfficeDocs.XmlHead +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" + w.Body + "</w:body></w:document>");
        var rels = new StringBuilder(OfficeDocs.XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        rels.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        rels.Append("<Relationship Id=\"rIdNumbering\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>");
        rels.Append("<Relationship Id=\"rIdSettings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>");
        rels.Append("<Relationship Id=\"rIdFooter\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>");
        foreach (var (id, name, _) in w.Media)
            rels.Append($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{name}\"/>");
        foreach (var (id, url) in w.Links)
            rels.Append($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{OfficeDocs.Esc(url)}\" TargetMode=\"External\"/>");
        rels.Append("</Relationships>");
        zip.Text("word/_rels/document.xml.rels", rels.ToString());
        zip.Text("word/styles.xml", Styles());
        zip.Text("word/numbering.xml", w.Numbering());
        zip.Text("word/settings.xml", OfficeDocs.XmlHead +
            "<w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:updateFields w:val=\"false\"/>" +
            "<w:defaultTabStop w:val=\"720\"/><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>");
        zip.Text("word/footer1.xml", OfficeDocs.XmlHead +
            "<w:ftr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            $"<w:p><w:pPr><w:tabs><w:tab w:val=\"right\" w:pos=\"{w.Usable}\"/></w:tabs><w:spacing w:after=\"0\"/></w:pPr>" +
            Run(title, size: 16, color: Muted) + "<w:r><w:rPr><w:color w:val=\"" + Muted + "\"/><w:sz w:val=\"16\"/></w:rPr><w:tab/><w:t xml:space=\"preserve\">Page </w:t></w:r>" +
            Field("PAGE", "1") + Run(" of ", size: 16, color: Muted) + Field("NUMPAGES", "1") + "</w:p></w:ftr>");
        foreach (var (_, name, bytes) in w.Media) zip.Bytes("word/media/" + name, bytes);
        return w.Missing;
    }

    private static string Field(string code, string shown) =>
        $"<w:r><w:rPr><w:color w:val=\"{Muted}\"/><w:sz w:val=\"16\"/></w:rPr><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
        $"<w:r><w:rPr><w:color w:val=\"{Muted}\"/><w:sz w:val=\"16\"/></w:rPr><w:instrText xml:space=\"preserve\"> {code} </w:instrText></w:r>" +
        $"<w:r><w:rPr><w:color w:val=\"{Muted}\"/><w:sz w:val=\"16\"/></w:rPr><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
        $"<w:r><w:rPr><w:color w:val=\"{Muted}\"/><w:sz w:val=\"16\"/></w:rPr><w:t>{shown}</w:t></w:r>" +
        $"<w:r><w:rPr><w:color w:val=\"{Muted}\"/><w:sz w:val=\"16\"/></w:rPr><w:fldChar w:fldCharType=\"end\"/></w:r>";

    /// <summary>Builds the body: blocks in order, the pictures and links it uses, and one numbering per numbered list.</summary>
    private sealed class Writer(bool landscape)
    {
        public readonly StringBuilder Body = new();
        public readonly List<(string Id, string Name, byte[] Bytes)> Media = [];
        public readonly List<(string Id, string Url)> Links = [];
        public readonly List<string> Missing = [];
        private int _numberedLists;
        private readonly bool _landscape = landscape;

        public int Usable => (_landscape ? A4H : A4W) - 2 * Margin;

        public string Section() =>
            "<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rIdFooter\"/>" +
            (_landscape ? $"<w:pgSz w:w=\"{A4H}\" w:h=\"{A4W}\" w:orient=\"landscape\"/>" : $"<w:pgSz w:w=\"{A4W}\" w:h=\"{A4H}\"/>") +
            $"<w:pgMar w:top=\"{Margin}\" w:right=\"{Margin}\" w:bottom=\"{Margin}\" w:left=\"{Margin}\" w:header=\"567\" w:footer=\"567\" w:gutter=\"0\"/></w:sectPr>";

        public void Block(Markdown.Block block)
        {
            switch (block)
            {
                case Markdown.Heading h:
                    Body.Append(Para(Spans(h.Text), style: "Heading" + Math.Min(3, h.Level)));
                    break;
                case Markdown.Paragraph p:
                    Body.Append(Para(Spans(p.Text)));
                    break;
                case Markdown.ListBlock list:
                    List(list);
                    break;
                case Markdown.Table t:
                    Table(t);
                    break;
                case Markdown.Code c:
                    foreach (var line in c.Text.Split('\n'))
                        Body.Append(Para(Run(line.Length == 0 ? " " : line, code: true), style: "Code"));
                    break;
                case Markdown.Quote q:
                    foreach (var line in q.Text.Split('\n'))
                        Body.Append(Para(Spans(line), style: "Quote"));
                    break;
                case Markdown.Rule:
                    Body.Append($"<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"{Grid}\"/></w:pBdr><w:spacing w:after=\"120\"/></w:pPr></w:p>");
                    break;
                case OfficeDocs.PageBreak:
                    Body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                    break;
                case OfficeDocs.ImageBlock img:
                    Image(img);
                    break;
            }
        }

        /// <summary>Runs for inline Markdown: bold, italic, strike, code and links (blue, underlined, clickable).</summary>
        public string Spans(string text, bool bold = false, int size = 0)
        {
            var sb = new StringBuilder();
            foreach (var s in Markdown.Inline(text))
            {
                var run = Run(s.Text, size: size, bold: bold || s.Style.HasFlag(Markdown.Style.Bold), italic: s.Style.HasFlag(Markdown.Style.Italic),
                    strike: s.Style.HasFlag(Markdown.Style.Strike), code: s.Style.HasFlag(Markdown.Style.Code), color: s.Link is null ? null : "0563C1", underline: s.Link is not null);
                if (s.Link is { } url && Markdown.IsUrl(url))
                {
                    var id = "rIdLink" + (Links.Count + 1);
                    Links.Add((id, url));
                    sb.Append($"<w:hyperlink r:id=\"{id}\" w:history=\"1\">{run}</w:hyperlink>");
                }
                else sb.Append(run);
            }
            return sb.ToString();
        }

        private void List(Markdown.ListBlock list)
        {
            // Every numbered list starts at its first number: its own w:num, restarting each level.
            var num = 0;
            if (list.Items.Any(i => i.Ordered)) num = 2 + _numberedLists++;
            foreach (var item in list.Items)
            {
                var depth = Math.Min(2, item.Depth);
                var id = item.Ordered ? num : 1;
                Body.Append($"<w:p><w:pPr><w:pStyle w:val=\"ListParagraph\"/><w:numPr><w:ilvl w:val=\"{depth}\"/><w:numId w:val=\"{id}\"/></w:numPr>" +
                            $"<w:spacing w:after=\"60\"/></w:pPr>{Spans(item.Text)}</w:p>");
            }
        }

        public string Numbering()
        {
            var sb = new StringBuilder(OfficeDocs.XmlHead + "<w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">");
            string[] bullets = ["•", "–", "▪"];
            sb.Append("<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
            for (var l = 0; l < 3; l++)
                sb.Append($"<w:lvl w:ilvl=\"{l}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"{bullets[l]}\"/><w:lvlJc w:val=\"left\"/>" +
                          $"<w:pPr><w:ind w:left=\"{360 * (l + 1)}\" w:hanging=\"360\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"{Font}\" w:hAnsi=\"{Font}\"/></w:rPr></w:lvl>");
            sb.Append("</w:abstractNum>");
            string[] formats = ["decimal", "lowerLetter", "lowerRoman"];
            sb.Append("<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
            for (var l = 0; l < 3; l++)
                sb.Append($"<w:lvl w:ilvl=\"{l}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{formats[l]}\"/><w:lvlText w:val=\"%{l + 1}.\"/><w:lvlJc w:val=\"left\"/>" +
                          $"<w:pPr><w:ind w:left=\"{360 * (l + 1)}\" w:hanging=\"360\"/></w:pPr></w:lvl>");
            sb.Append("</w:abstractNum>");
            sb.Append("<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
            for (var n = 0; n < _numberedLists; n++)
            {
                sb.Append($"<w:num w:numId=\"{n + 2}\"><w:abstractNumId w:val=\"1\"/>");
                for (var l = 0; l < 3; l++) sb.Append($"<w:lvlOverride w:ilvl=\"{l}\"><w:startOverride w:val=\"1\"/></w:lvlOverride>");
                sb.Append("</w:num>");
            }
            sb.Append("</w:numbering>");
            return sb.ToString();
        }

        private void Table(Markdown.Table t)
        {
            var columns = Math.Max(t.Header.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
            if (columns == 0) return;
            // Column widths from the longest text in each (header counted), shared out over the page width.
            var weights = new double[columns];
            for (var c = 0; c < columns; c++)
            {
                var longest = c < t.Header.Count ? OfficeDocs.Plain(t.Header[c]).Length : 0;
                foreach (var row in t.Rows) if (c < row.Count) longest = Math.Max(longest, OfficeDocs.Plain(row[c]).Length);
                weights[c] = Math.Max(4, Math.Min(40, longest));
            }
            var total = weights.Sum();
            var widths = weights.Select(x => (int)(Usable * x / total)).ToArray();
            var right = new bool[columns];
            for (var c = 0; c < columns; c++)
            {
                var align = c < t.Align.Count ? t.Align[c] : 'l';
                var cells = t.Rows.Where(r => c < r.Count && OfficeDocs.Plain(r[c]).Trim().Length > 0).Select(r => r[c]).ToList();
                right[c] = align == 'r' || align == 'l' && cells.Count > 0 && cells.All(OfficeDocs.LooksNumeric);
            }
            string Jc(int c) => c < t.Align.Count && t.Align[c] == 'c' ? "center" : right[c] ? "right" : "left";

            var sb = new StringBuilder();
            sb.Append($"<w:tbl><w:tblPr><w:tblW w:w=\"{Usable}\" w:type=\"dxa\"/><w:tblBorders>");
            foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
                sb.Append($"<w:{side} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{Grid}\"/>");
            sb.Append("</w:tblBorders><w:tblLayout w:type=\"fixed\"/><w:tblCellMar><w:top w:w=\"40\" w:type=\"dxa\"/><w:left w:w=\"80\" w:type=\"dxa\"/>" +
                      "<w:bottom w:w=\"40\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>");
            foreach (var width in widths) sb.Append($"<w:gridCol w:w=\"{width}\"/>");
            sb.Append("</w:tblGrid>");
            if (t.Header.Count > 0)
            {
                sb.Append("<w:tr><w:trPr><w:tblHeader/><w:cantSplit/></w:trPr>");
                for (var c = 0; c < columns; c++)
                    sb.Append(Cell(widths[c], c < t.Header.Count ? Spans(t.Header[c], bold: true, size: 19) : "", Jc(c), HeadFill));
                sb.Append("</w:tr>");
            }
            foreach (var row in t.Rows)
            {
                sb.Append("<w:tr><w:trPr><w:cantSplit/></w:trPr>");
                // A row that starts with bold text (a total, a section) is shaded like a total.
                var total2 = row.Count > 0 && Markdown.Inline(row[0]).FirstOrDefault() is { } first && first.Style.HasFlag(Markdown.Style.Bold) && first.Text.Trim().Length > 0;
                for (var c = 0; c < columns; c++)
                    sb.Append(Cell(widths[c], c < row.Count ? Spans(row[c], size: 19) : "", Jc(c), total2 ? "F2F2F2" : null));
                sb.Append("</w:tr>");
            }
            sb.Append("</w:tbl>");
            Body.Append(sb);
            Body.Append("<w:p><w:pPr><w:spacing w:after=\"60\"/></w:pPr></w:p>");
        }

        private static string Cell(int width, string runs, string jc, string? fill) =>
            $"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/>" + (fill is null ? "" : $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill}\"/>") +
            $"</w:tcPr><w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\"/><w:jc w:val=\"{jc}\"/></w:pPr>{runs}</w:p></w:tc>";

        private void Image(OfficeDocs.ImageBlock img)
        {
            var picture = OfficeDocs.LoadPicture(img.Path);
            if (picture is null)
            {
                Missing.Add(img.Path);
                Body.Append(Para(Run($"[Picture not found: {Path.GetFileName(img.Path)}]", italic: true, color: Muted)));
                return;
            }
            var n = Media.Count + 1;
            var id = "rIdImg" + n;
            Media.Add((id, $"image{n}.{picture.Ext}", picture.Bytes));
            // At 96 dpi, no wider than the page and no taller than about two thirds of it.
            const long EmuPerPx = 9525, EmuPerTwip = 635;
            long cx = picture.Width * EmuPerPx, cy = picture.Height * EmuPerPx;
            var maxW = Usable * EmuPerTwip;
            var maxH = ((_landscape ? A4W : A4H) - 2 * Margin) * EmuPerTwip * 2 / 3;
            var scale = Math.Min(1.0, Math.Min((double)maxW / cx, (double)maxH / cy));
            cx = (long)(cx * scale);
            cy = (long)(cy * scale);
            var name = OfficeDocs.Esc(img.Caption.Length > 0 ? img.Caption : Path.GetFileName(img.Path));
            Body.Append("<w:p><w:pPr><w:keepNext/><w:jc w:val=\"center\"/><w:spacing w:before=\"120\" w:after=\"60\"/></w:pPr><w:r><w:drawing>" +
                        $"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{n}\" name=\"Picture {n}\" descr=\"{name}\"/>" +
                        "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
                        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
                        $"<pic:nvPicPr><pic:cNvPr id=\"{n}\" name=\"Picture {n}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                        $"<pic:blipFill><a:blip r:embed=\"{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                        $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
                        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
            if (img.Caption.Length > 0) Body.Append(Para(Spans(img.Caption), style: "Caption"));
        }
    }

    private static string Para(string runs, string? style = null) =>
        "<w:p>" + (style is null ? "" : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>") + runs + "</w:p>";

    private static string Run(string text, int size = 0, bool bold = false, bool italic = false, bool strike = false, bool code = false, string? color = null, bool underline = false)
    {
        var props = new StringBuilder();
        if (code) props.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/>");
        if (bold) props.Append("<w:b/>");
        if (italic) props.Append("<w:i/>");
        if (strike) props.Append("<w:strike/>");
        if (color is not null) props.Append($"<w:color w:val=\"{color}\"/>");
        if (size > 0) props.Append($"<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/>");
        if (underline) props.Append("<w:u w:val=\"single\"/>");
        if (code && size == 0) props.Append("<w:sz w:val=\"19\"/>");
        var rpr = props.Length > 0 ? "<w:rPr>" + props + "</w:rPr>" : "";
        // Line breaks inside a span (a hard break in the Markdown) become Word line breaks.
        var parts = text.Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append("<w:br/>");
            sb.Append($"<w:t xml:space=\"preserve\">{OfficeDocs.Esc(parts[i])}</w:t>");
        }
        return $"<w:r>{rpr}{sb}</w:r>";
    }

    private static string Styles()
    {
        string Heading(int level, int size, int before) =>
            $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
            $"<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before=\"{before}\" w:after=\"80\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>" +
            $"<w:rPr><w:b/><w:color w:val=\"{Accent}\"/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>";
        return OfficeDocs.XmlHead + "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            $"<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"{Font}\" w:hAnsi=\"{Font}\" w:eastAsia=\"{Font}\" w:cs=\"Arial\"/><w:sz w:val=\"21\"/><w:szCs w:val=\"21\"/>" +
            "<w:lang w:val=\"en-GB\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
            "<w:pPr><w:spacing w:after=\"60\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"40\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Subtitle\"><w:name w:val=\"Subtitle\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
            $"<w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"8\" w:space=\"6\" w:color=\"{Accent}\"/></w:pBdr><w:spacing w:after=\"240\"/></w:pPr><w:rPr><w:sz w:val=\"24\"/></w:rPr></w:style>" +
            Heading(1, 30, 280) + Heading(2, 25, 220) + Heading(3, 22, 180) +
            "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:ind w:left=\"720\"/><w:contextualSpacing/></w:pPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/>" +
            $"<w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{CodeFill}\"/><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>" +
            "<w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"19\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/>" +
            $"<w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"{Grid}\"/></w:pBdr><w:ind w:left=\"360\"/></w:pPr><w:rPr><w:i/><w:color w:val=\"{Muted}\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Caption\"><w:name w:val=\"caption\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/>" +
            $"<w:pPr><w:jc w:val=\"center\"/><w:spacing w:after=\"200\"/></w:pPr><w:rPr><w:i/><w:color w:val=\"{Muted}\"/><w:sz w:val=\"18\"/></w:rPr></w:style>" +
            "</w:styles>";
    }
}
