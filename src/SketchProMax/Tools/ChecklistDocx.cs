using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SketchProMax.Tools;

/// <summary>
/// Writes the filled pre-submission checklist as a Word file (.docx), A4 landscape, ready to print: the title and a
/// ready / not ready banner, the project details, one table per section (check, found on, priority, Y/N/NA in colour,
/// who checked it and when, the comment), then the sign-off with each signatory's name, signature image and date, and
/// page numbers in the footer. Plain WordprocessingML written by hand (no Office or library needed); no Revit or WPF.
///
/// Signatures go in only when the checklist is ready (nothing unanswered, no N): a checklist that is not ready says so
/// in red and leaves the signature boxes empty, so it can be printed for review but can't pass for a signed copy.
/// </summary>
internal static class ChecklistDocx
{
    /// <summary>About the file: the model it is for, this add-in's version, when it was made.</summary>
    /// <param name="AutoCheck">The automatic model check in one line ("Automatic model check: 6 of 12 passed (50%)…"), or empty.</param>
    public sealed record Info(string Model, string Version, DateTime When, string AutoCheck = "");

    /// <summary>A signature image: the file's bytes and its extension (png or jpeg).</summary>
    public sealed record Image(byte[] Bytes, string Extension);

    private const string Font = "Calibri";
    private const int PageW = 16838, PageH = 11906, Margin = 720, Usable = PageW - 2 * Margin; // A4 landscape, twips
    private const string Accent = "1D4ED8", Muted = "475569", Grid = "CBD5E1", LabelFill = "F1F5F9";
    private const string OkText = "15803D", OkFill = "E8F5EC", BadText = "B91C1C", BadFill = "FDECEC", NaText = "475569", NaFill = "EEF1F5";

    /// <summary>Writes the file (replacing one already there). <paramref name="images"/>: each signatory's image by role, when it has one.</summary>
    public static void Write(string path, ChecklistMath.Definition def, ChecklistMath.State state, Info info, IReadOnlyDictionary<string, Image> images)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        using (var file = File.Create(temp)) Write(file, def, state, info, images);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    public static void Write(Stream output, ChecklistMath.Definition def, ChecklistMath.State state, Info info, IReadOnlyDictionary<string, Image> images)
    {
        var progress = ChecklistMath.Count(def.Items, state);
        var media = new List<(string Name, string RelId, Image Image)>();
        var body = new StringBuilder();

        // ---- Title and status
        body.Append(Para(Run(def.Title, bold: true, size: 32, color: Accent), after: 40));
        var sub = $"Model {Or(info.Model, "not saved")} · made {info.When.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture)} with Sketch ProMax {info.Version}";
        body.Append(Para(Run(sub, size: 18, color: Muted), after: 160));
        var ready = progress.Ready;
        var counts = $"{progress.Total} checks: {progress.Yes} Yes, {progress.NotApplicable} N/A, {progress.No} No, {progress.Open} not answered.";
        var banner = ready
            ? Run("READY FOR SUBMISSION", bold: true, color: OkText) + Run("   " + counts, color: OkText)
            : Run("NOT READY · DO NOT SUBMIT", bold: true, color: BadText) + Run("   " + counts + " Signatures are left out until every check is Y or NA.", color: BadText);
        body.Append(Table([Usable], Row(Cell(Usable, Para(banner, after: 0), fill: ready ? OkFill : BadFill, border: ready ? OkText : BadText))));
        body.Append(info.AutoCheck.Length > 0 ? Para(Run(info.AutoCheck, size: 18, color: Muted), after: 120) : Para("", after: 120));

        // ---- Project details: label | value | label | value
        body.Append(Heading("Project details"));
        var fields = def.Fields.Where(f => f.Label.Length > 0).ToList();
        int lw = 3000, vw = Usable / 2 - 3000;
        var rows = new StringBuilder();
        for (var i = 0; i < fields.Count; i += 2)
        {
            var row = new StringBuilder();
            for (var j = i; j < i + 2; j++)
            {
                var f = j < fields.Count ? fields[j] : null;
                row.Append(Cell(lw, Para(Run(f?.Label ?? "", bold: true, size: 18), after: 0), fill: LabelFill));
                var value = f is null ? "" : state.Fields.TryGetValue(f.Label, out var v) ? v : "";
                row.Append(Cell(vw, Para(Run(value, size: 18), after: 0)));
            }
            rows.Append(Row(row.ToString()));
        }
        body.Append(Table([lw, vw, lw, vw], rows.ToString()));

        // ---- The checks, a table per section
        int[] cols = [600, 6200, 1500, 900, 900, 1500, 1300, 0];
        cols[7] = Usable - cols.Take(7).Sum();
        string[] heads = ["#", "Check", "Found on", "Priority", "Done", "Checked by", "Date", "Comment"];
        foreach (var section in def.Sections)
        {
            body.Append(Heading(section.Title));
            var t = new StringBuilder();
            t.Append(Row(string.Concat(heads.Select((h, i) => Cell(cols[i], Para(Run(h, bold: true, size: 17, color: "FFFFFF"), after: 0), fill: Accent))), header: true));
            foreach (var item in section.Items)
            {
                var a = state.Answers.TryGetValue(item.Id, out var found) ? found : new ChecklistMath.Answer();
                var done = ChecklistMath.Normal(a.Done);
                var check = Para(Run(item.Check, size: 17), after: 0);
                if (item.Waiting.Length > 0) check += Para(Run("Open point: " + item.Waiting, size: 15, color: Muted, italic: true), after: 0);
                var (label, text, fill) = done switch
                {
                    ChecklistMath.Yes => ("Y", OkText, OkFill),
                    ChecklistMath.No => ("N", BadText, BadFill),
                    ChecklistMath.NotApplicable => ("NA", NaText, NaFill),
                    _ => ("–", BadText, (string?)null),
                };
                t.Append(Row(
                    Cell(cols[0], Para(Run(item.Number, size: 17), after: 0)) +
                    Cell(cols[1], check) +
                    Cell(cols[2], Para(Run(item.FoundOn, size: 16, color: Muted), after: 0)) +
                    Cell(cols[3], Para(Run(item.Priority, size: 16, bold: item.High, color: item.High ? BadText : Muted), after: 0)) +
                    Cell(cols[4], Para(Run(label, bold: true, size: 18, color: text), after: 0, center: true), fill: fill) +
                    Cell(cols[5], Para(Run(done.Length > 0 ? a.By : "", size: 16), after: 0)) +
                    Cell(cols[6], Para(Run(done.Length > 0 ? a.Date : "", size: 16), after: 0)) +
                    Cell(cols[7], Para(Run(a.Note.Trim(), size: 16), after: 0))));
            }
            body.Append(Table(cols, t.ToString()));
        }

        // ---- Sign-off
        // The sign-off stays on one page with its heading and the line under it (every paragraph keeps with the next).
        body.Append(Heading("Sign-off"));
        int[] sc = [3600, 4200, 4600, 0];
        sc[3] = Usable - sc.Take(3).Sum();
        var s = new StringBuilder();
        s.Append(Row(string.Concat(new[] { "Role", "Name", "Signature", "Date" }.Select((h, i) => Cell(sc[i], Para(Run(h, bold: true, size: 17, color: "FFFFFF"), after: 0, keep: true), fill: Accent))), header: true));
        foreach (var role in def.Signatories)
        {
            var sig = state.Signatures.FirstOrDefault(x => x.Role == role) ?? new ChecklistMath.Signature { Role = role };
            var picture = "";
            if (ready && sig.Mode != ChecklistMath.SignMode.Hand && images.TryGetValue(role, out var img) && ImageSize(img.Bytes) is { } size)
            {
                var n = media.Count + 1;
                var name = $"signature{n}.{(img.Extension == "png" ? "png" : "jpeg")}";
                media.Add((name, "rIdImg" + n, img));
                picture = Drawing("rIdImg" + n, n, size.Width, size.Height, maxWidthMm: 70, maxHeightMm: 18);
            }
            s.Append(Row(
                Cell(sc[0], Para(Run(role, bold: true, size: 18), after: 0, keep: true)) +
                Cell(sc[1], Para(Run(sig.Name, size: 18), after: 0, keep: true)) +
                Cell(sc[2], picture.Length > 0 ? Para(picture, after: 0, keep: true) : Para("", after: 0, keep: true)) +
                Cell(sc[3], Para(Run(ready ? sig.Date : "", size: 18), after: 0, keep: true)), height: 1200));
        }
        body.Append(Table(sc, s.ToString()));
        body.Append(Para(Run(ready
            ? "Signed as ready for submission. The signed copy goes with the submission."
            : "Not signed: this checklist is not ready for submission.", size: 16, color: Muted, italic: true), after: 0));

        var footerText = $"{def.Title} · {Or(info.Model, "model not saved")}";
        WritePackage(output, Document(body.ToString()), Footer(footerText), media);
    }

    // ---- WordprocessingML pieces ------------------------------------------------------------------------------------

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    internal static string Heading(string text) =>
        $"<w:p><w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"80\"/></w:pPr>{Run(text, bold: true, size: 24, color: Accent)}</w:p>";

    internal static string Para(string runs, int after = 80, bool center = false, bool keep = false) =>
        $"<w:p><w:pPr>{(keep ? "<w:keepNext/>" : "")}<w:spacing w:before=\"0\" w:after=\"{after}\"/>{(center ? "<w:jc w:val=\"center\"/>" : "")}</w:pPr>{runs}</w:p>";

    internal static string Run(string text, bool bold = false, int size = 20, string? color = null, bool italic = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var props = $"<w:rPr><w:rFonts w:ascii=\"{Font}\" w:hAnsi=\"{Font}\" w:cs=\"{Font}\"/>{(bold ? "<w:b/>" : "")}{(italic ? "<w:i/>" : "")}" +
                    $"{(color is null ? "" : $"<w:color w:val=\"{color}\"/>")}<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr>";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder("<w:r>").Append(props);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append("<w:br/>");
            sb.Append("<w:t xml:space=\"preserve\">").Append(Esc(lines[i])).Append("</w:t>");
        }
        return sb.Append("</w:r>").ToString();
    }

    internal static string Table(int[] widths, string rows)
    {
        var borders = string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }
            .Select(b => $"<w:{b} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{Grid}\"/>"));
        return $"<w:tbl><w:tblPr><w:tblW w:w=\"{widths.Sum()}\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders>{borders}</w:tblBorders>" +
               "<w:tblCellMar><w:top w:w=\"50\" w:type=\"dxa\"/><w:left w:w=\"80\" w:type=\"dxa\"/><w:bottom w:w=\"50\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>" +
               $"<w:tblGrid>{string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>"))}</w:tblGrid>{rows}</w:tbl>";
    }

    internal static string Row(string cells, bool header = false, int height = 0) =>
        $"<w:tr><w:trPr><w:cantSplit/>{(header ? "<w:tblHeader/>" : "")}{(height > 0 ? $"<w:trHeight w:val=\"{height}\" w:hRule=\"atLeast\"/>" : "")}</w:trPr>{cells}</w:tr>";

    internal static string Cell(int width, string content, string? fill = null, string? border = null)
    {
        var props = $"<w:tcW w:w=\"{width}\" w:type=\"dxa\"/>";
        if (border is not null)
            props += "<w:tcBorders>" + string.Concat(new[] { "top", "left", "bottom", "right" }.Select(b => $"<w:{b} w:val=\"single\" w:sz=\"12\" w:space=\"0\" w:color=\"{border}\"/>")) + "</w:tcBorders>";
        if (fill is not null) props += $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill}\"/>";
        props += "<w:vAlign w:val=\"center\"/>";
        return $"<w:tc><w:tcPr>{props}</w:tcPr>{(content.Length == 0 ? "<w:p/>" : content)}</w:tc>";
    }

    /// <summary>An inline picture, scaled to fit the box and keep its proportions.</summary>
    internal static string Drawing(string relId, int id, int pxW, int pxH, double maxWidthMm, double maxHeightMm, string name = "Signature")
    {
        const long PerMm = 36000; // EMU
        var scale = Math.Min(maxWidthMm / pxW, maxHeightMm / pxH);
        long cx = (long)(pxW * scale * PerMm), cy = (long)(pxH * scale * PerMm);
        return "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
               $"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{id}\" name=\"{Esc(name)} {id}\" descr=\"{Esc(name)}\"/>" +
               "<wp:cNvGraphicFramePr><a:graphicFrameLocks xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
               "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
               "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
               $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"signature{id}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
               $"<pic:blipFill><a:blip r:embed=\"{relId}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
               $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
               "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";
    }

    internal const string Namespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"";

    private static string Document(string body) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {Namespaces}><w:body>{body}" +
        $"<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rIdFooter\"/><w:pgSz w:w=\"{PageW}\" w:h=\"{PageH}\" w:orient=\"landscape\"/>" +
        $"<w:pgMar w:top=\"{Margin}\" w:right=\"{Margin}\" w:bottom=\"{Margin}\" w:left=\"{Margin}\" w:header=\"360\" w:footer=\"360\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";

    private static string Footer(string text)
    {
        // Simple fields: Word and LibreOffice both work the numbers out when the page is drawn.
        static string Field(string code) =>
            $"<w:fldSimple w:instr=\" {code} \"><w:r><w:rPr><w:sz w:val=\"16\"/><w:color w:val=\"{Muted}\"/></w:rPr><w:t>1</w:t></w:r></w:fldSimple>";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:ftr {Namespaces}><w:p><w:pPr><w:tabs><w:tab w:val=\"right\" w:pos=\"{Usable}\"/></w:tabs></w:pPr>" +
               Run(text, size: 16, color: Muted) + $"<w:r><w:rPr><w:sz w:val=\"16\"/></w:rPr><w:tab/></w:r>" + Run("Page ", size: 16, color: Muted) + Field("PAGE") +
               Run(" of ", size: 16, color: Muted) + Field("NUMPAGES") + "</w:p></w:ftr>";
    }

    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"20\"/><w:szCs w:val=\"20\"/><w:lang w:val=\"en-GB\"/></w:rPr></w:rPrDefault>" +
        "<w:pPrDefault><w:pPr><w:spacing w:after=\"80\" w:line=\"252\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
        "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/>" +
        "<w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
        "</w:styles>";

    private const string Settings =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        "<w:updateFields w:val=\"false\"/><w:defaultTabStop w:val=\"720\"/><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>";

    internal static void WritePackage(Stream output, string document, string footer, List<(string Name, string RelId, Image Image)> media, string title = "Pre-submission checklist")
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Add(string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        Add("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>" +
            "<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>");
        Add("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
        Add("docProps/core.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:title>{Esc(title)}</dc:title><dc:creator>Sketch ProMax</dc:creator></cp:coreProperties>");
        var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "<Relationship Id=\"rIdSettings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>" +
            "<Relationship Id=\"rIdFooter\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>");
        foreach (var (name, relId, _) in media)
            rels.Append($"<Relationship Id=\"{relId}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{name}\"/>");
        Add("word/_rels/document.xml.rels", rels.Append("</Relationships>").ToString());
        Add("word/document.xml", document);
        Add("word/styles.xml", Styles);
        Add("word/settings.xml", Settings);
        Add("word/footer1.xml", footer);
        foreach (var (name, _, image) in media)
        {
            var entry = zip.CreateEntry("word/media/" + name, CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(image.Bytes, 0, image.Bytes.Length);
        }
    }

    /// <summary>XML text: the five special characters escaped, characters XML can't hold dropped.</summary>
    internal static string Esc(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (ch == '\t' || ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF) sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The kind of a signature image from its first bytes: "png", "jpeg" or null (not a picture Word can take here).</summary>
    public static string? ImageKind(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "png";
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "jpeg";
        return null;
    }

    /// <summary>A PNG's or JPEG's size in pixels, read from its header; null when it can't be read.</summary>
    public static (int Width, int Height)? ImageSize(byte[] b)
    {
        try
        {
            if (ImageKind(b) == "png" && b.Length >= 24)
            {
                int w = b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19], h = b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23];
                return w > 0 && h > 0 ? (w, h) : null;
            }
            if (ImageKind(b) == "jpeg")
            {
                var i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var marker = b[i + 1];
                    if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                    var len = b[i + 2] << 8 | b[i + 3];
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    {
                        int h = b[i + 5] << 8 | b[i + 6], w = b[i + 7] << 8 | b[i + 8];
                        return w > 0 && h > 0 ? (w, h) : null;
                    }
                    i += 2 + len;
                }
            }
        }
        catch (IndexOutOfRangeException) { }
        return null;
    }
}
