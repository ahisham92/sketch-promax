using System.Globalization;
using System.IO;
using System.Text;
using SketchProMax.Chat;

namespace SketchProMax.Tools;

/// <summary>
/// A PowerPoint file (.pptx), 16:9: a title slide, then one slide per entry with a title and any of bullets (nested,
/// with bold and italic), a picture from this PC and a table (shaded header; a long table carries on over more slides
/// with its header repeated). Text sizes shrink to fit the slide. Plain PresentationML written by hand; no Office,
/// Revit or WPF.
/// </summary>
internal static class OfficePptx
{
    internal sealed record Bullet(string Text, int Level);

    internal sealed record TableData(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

    internal sealed record Slide(string Title, IReadOnlyList<Bullet> Bullets, string Image, string Caption, TableData? Table);

    private const long W = 12192000, H = 6858000, Pad = 457200, Emu = 12700; // EMU; Emu = one point
    private const string Accent = "1F3A5F", Text = "262626", Muted = "7F7F7F", Grid = "BFBFBF", HeadFill = "DCE6F2", Light = "EEF2F7";

    /// <summary>Writes the file; returns the pictures that couldn't be put in.</summary>
    public static List<string> Write(string path, string title, string subtitle, IReadOnlyList<Slide> slides)
    {
        var missing = new List<string>();
        OfficeDocs.WriteFile(path, s => missing = Write(s, title, subtitle, slides, DateTime.Now));
        return missing;
    }

    /// <summary>The slides as written: a long table split over several slides (each with its header).</summary>
    public static List<Slide> Expand(IReadOnlyList<Slide> slides)
    {
        var list = new List<Slide>();
        foreach (var s in slides)
        {
            if (s.Table is not { } t || t.Rows.Count <= RowsPerSlide(s)) { list.Add(s); continue; }
            var first = true;
            for (var i = 0; i < t.Rows.Count;)
            {
                // Later slides have no bullets above the table, so they hold more rows.
                var rows = t.Rows.Skip(i).Take(first ? RowsPerSlide(s) : RowsPerSlide(s with { Bullets = [] })).ToList();
                list.Add(first ? s with { Table = t with { Rows = rows } } : new Slide(s.Title + " (continued)", [], "", "", t with { Rows = rows }));
                i += rows.Count;
                first = false;
            }
        }
        return list;
    }

    private static int RowsPerSlide(Slide s) => s.Bullets.Count > 0 ? 9 : 13;

    public static List<string> Write(Stream output, string title, string subtitle, IReadOnlyList<Slide> input, DateTime when)
    {
        var slides = Expand(input);
        var missing = new List<string>();
        var media = new List<(string Name, byte[] Bytes)>();
        var slideXml = new List<(string Xml, string Rels)>();
        slideXml.Add((TitleSlide(title, subtitle, when), Rels(1, [])));
        for (var i = 0; i < slides.Count; i++)
        {
            var images = new List<string>();
            slideXml.Add((ContentSlide(slides[i], title, i + 2, media, images, missing), Rels(2, images)));
        }

        using var zip = new OfficeDocs.Package(output);
        var types = new StringBuilder(OfficeDocs.XmlHead + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
            "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
            "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
            "<Override PartName=\"/ppt/slideLayouts/slideLayout2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
            "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>" +
            "<Override PartName=\"/ppt/presProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presProps+xml\"/>" +
            "<Override PartName=\"/ppt/viewProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml\"/>" +
            "<Override PartName=\"/ppt/tableStyles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
        for (var i = 0; i < slideXml.Count; i++)
            types.Append($"<Override PartName=\"/ppt/slides/slide{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        types.Append("</Types>");
        zip.Text("[Content_Types].xml", types.ToString());
        zip.Text("_rels/.rels", OfficeDocs.XmlHead +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "</Relationships>");
        zip.Text("docProps/core.xml", OfficeDocs.Core(title, when));

        var ids = new StringBuilder();
        var rels = new StringBuilder(OfficeDocs.XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps\" Target=\"presProps.xml\"/>" +
            "<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/viewProps\" Target=\"viewProps.xml\"/>" +
            "<Relationship Id=\"rId5\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/tableStyles\" Target=\"tableStyles.xml\"/>");
        for (var i = 0; i < slideXml.Count; i++)
        {
            ids.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rId{10 + i}\"/>");
            rels.Append($"<Relationship Id=\"rId{10 + i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide{i + 1}.xml\"/>");
        }
        rels.Append("</Relationships>");
        zip.Text("ppt/presentation.xml", OfficeDocs.XmlHead + $"<p:presentation {Ns} saveSubsetFonts=\"1\">" +
            "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
            $"<p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx=\"{W}\" cy=\"{H}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
        zip.Text("ppt/_rels/presentation.xml.rels", rels.ToString());
        zip.Text("ppt/presProps.xml", OfficeDocs.XmlHead + $"<p:presentationPr {Ns}/>");
        zip.Text("ppt/viewProps.xml", OfficeDocs.XmlHead + $"<p:viewPr {Ns}><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>");
        zip.Text("ppt/tableStyles.xml", OfficeDocs.XmlHead + "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>");
        zip.Text("ppt/theme/theme1.xml", Theme());
        zip.Text("ppt/slideMasters/slideMaster1.xml", Master());
        zip.Text("ppt/slideMasters/_rels/slideMaster1.xml.rels", OfficeDocs.XmlHead +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout2.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/>" +
            "</Relationships>");
        zip.Text("ppt/slideLayouts/slideLayout1.xml", Layout("title", "Title Slide", Placeholder(2, "Title 1", "ctrTitle", null, Pad, 2200000, W - 2 * Pad, 1300000)));
        zip.Text("ppt/slideLayouts/slideLayout2.xml", Layout("obj", "Title and Content",
            Placeholder(2, "Title 1", "title", null, Pad, 300000, W - 2 * Pad, 800000) + Placeholder(3, "Content 2", null, 1, Pad, 1400000, W - 2 * Pad, H - 1400000 - Pad)));
        for (var l = 1; l <= 2; l++)
            zip.Text($"ppt/slideLayouts/_rels/slideLayout{l}.xml.rels", OfficeDocs.XmlHead +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
        for (var i = 0; i < slideXml.Count; i++)
        {
            zip.Text($"ppt/slides/slide{i + 1}.xml", slideXml[i].Xml);
            zip.Text($"ppt/slides/_rels/slide{i + 1}.xml.rels", slideXml[i].Rels);
        }
        foreach (var (name, bytes) in media) zip.Bytes("ppt/media/" + name, bytes);
        return missing;
    }

    private const string Ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                              "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                              "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    private static string Rels(int layout, List<string> images)
    {
        var sb = new StringBuilder(OfficeDocs.XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout{layout}.xml\"/>");
        for (var i = 0; i < images.Count; i++)
            sb.Append($"<Relationship Id=\"rId{i + 2}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/{images[i]}\"/>");
        return sb.Append("</Relationships>").ToString();
    }

    private const string Group = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                                 "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static string SlideXml(string shapes, string? background = null) =>
        OfficeDocs.XmlHead + $"<p:sld {Ns}><p:cSld>" +
        (background is null ? "" : $"<p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"{background}\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>") +
        "<p:spTree>" + Group + shapes + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";

    private static string TitleSlide(string title, string subtitle, DateTime when)
    {
        var shapes = new StringBuilder();
        shapes.Append(Rect(2, "Accent band", 0, H - 228600, W, 228600, "4F81BD"));
        shapes.Append(Box(3, "Title", Pad + 228600, 2000000, W - 2 * Pad - 457200, 1500000,
            Para(Runs(title, Fit([title], W - 2 * Pad - 457200, 1500000, [44, 40, 36, 32, 28, 24]), "FFFFFF", bold: true)), anchor: "b", placeholder: "ctrTitle"));
        var lines = new StringBuilder();
        // The date, unless the subtitle already carries one.
        if (subtitle.Length > 0) lines.Append(Para(Runs(subtitle, 22, "DCE6F2")));
        else lines.Append(Para(Runs(when.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), 16, "B4C7E7")));
        shapes.Append(Box(4, "Subtitle", Pad + 228600, 3650000, W - 2 * Pad - 457200, 1400000, lines.ToString()));
        return SlideXml(shapes.ToString(), Accent);
    }

    private static string ContentSlide(Slide s, string deck, int number, List<(string Name, byte[] Bytes)> media, List<string> images, List<string> missing)
    {
        var shapes = new StringBuilder();
        var id = 2;
        shapes.Append(Box(id++, "Title", Pad, 300000, W - 2 * Pad, 800000,
            Para(Runs(s.Title, Fit([s.Title], W - 2 * Pad, 800000, [32, 28, 24, 20]), Accent, bold: true)), anchor: "b", placeholder: "title"));
        shapes.Append(Rect(id++, "Title rule", Pad, 1150000, W - 2 * Pad, 25400, "4F81BD"));
        shapes.Append(Box(id++, "Footer", Pad, H - 380000, W / 2, 250000, Para(Runs(deck, 10, Muted))));
        shapes.Append(Box(id++, "Slide number", W - Pad - 1000000, H - 380000, 1000000, 250000, Para(Runs(number.ToString(CultureInfo.InvariantCulture), 10, Muted), "r")));

        long top = 1400000, bottom = H - 500000, left = Pad, width = W - 2 * Pad;
        OfficeDocs.Picture? picture = null;
        if (s.Image.Length > 0)
        {
            picture = OfficeDocs.LoadPicture(s.Image);
            if (picture is null) missing.Add(s.Image);
        }
        if (s.Table is { } table)
        {
            if (s.Bullets.Count > 0)
            {
                var h = Math.Min((bottom - top) * 2 / 5, 300000L + s.Bullets.Count * 380000L);
                shapes.Append(BulletBox(id++, s.Bullets, left, top, width, h, [20, 18, 16, 14]));
                top += h + 100000;
            }
            shapes.Append(Table(id++, table, left, top, width, bottom - top));
            if (picture is not null) missing.Add(s.Image + " (a slide with a table shows no picture)");
        }
        else if (picture is not null)
        {
            long areaL = left, areaW = width;
            if (s.Bullets.Count > 0)
            {
                var textW = width * 9 / 20;
                shapes.Append(BulletBox(id++, s.Bullets, left, top, textW, bottom - top, [24, 22, 20, 18, 16, 14, 12]));
                areaL = left + textW + 300000;
                areaW = width - textW - 300000;
            }
            var captionH = s.Caption.Length > 0 ? 350000L : 0;
            var areaH = bottom - top - captionH;
            var scale = Math.Min((double)areaW / picture.Width, (double)areaH / picture.Height);
            long cx = (long)(picture.Width * scale), cy = (long)(picture.Height * scale);
            long x = areaL + (areaW - cx) / 2, y = top + (areaH - cy) / 2;
            var name = $"image{media.Count + 1}.{picture.Ext}";
            media.Add((name, picture.Bytes));
            images.Add(name);
            shapes.Append($"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"Picture {id}\" descr=\"{OfficeDocs.Esc(s.Caption.Length > 0 ? s.Caption : Path.GetFileName(s.Image))}\"/>" +
                          "<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
                          $"<p:blipFill><a:blip r:embed=\"rId{images.Count + 1}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                          $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
                          $"<a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"{Grid}\"/></a:solidFill></a:ln></p:spPr></p:pic>");
            id++;
            if (s.Caption.Length > 0)
                shapes.Append(Box(id++, "Caption", areaL, y + cy + 60000, areaW, 300000, Para(Runs(s.Caption, 12, Muted, italic: true), "ctr")));
        }
        else if (s.Bullets.Count > 0)
            shapes.Append(BulletBox(id++, s.Bullets, left, top, width, bottom - top, [28, 24, 22, 20, 18, 16, 14, 12]));
        return SlideXml(shapes.ToString());
    }

    private static string BulletBox(int id, IReadOnlyList<Bullet> bullets, long x, long y, long cx, long cy, int[] sizes)
    {
        var size = Fit(bullets.Select(b => new string(' ', b.Level * 4) + OfficeDocs.Plain(b.Text)).ToList(), cx - 400000, cy, sizes, spacing: 0.5);
        var sb = new StringBuilder();
        foreach (var b in bullets)
        {
            var level = Math.Max(0, Math.Min(3, b.Level));
            var sz = Math.Max(10, size - 2 * level);
            var mar = 342900L * (level + 1);
            var ch = level == 0 ? "•" : level == 1 ? "–" : "▪";
            sb.Append($"<a:p><a:pPr marL=\"{mar}\" indent=\"-285750\"><a:spcBef><a:spcPts val=\"{sz * 50}\"/></a:spcBef>" +
                      $"<a:buClr><a:srgbClr val=\"4F81BD\"/></a:buClr><a:buFont typeface=\"Arial\"/><a:buChar char=\"{ch}\"/></a:pPr>" +
                      Runs(b.Text, sz, Text) + "</a:p>");
        }
        return Box(id, "Content", x, y, cx, cy, sb.ToString(), placeholder: null);
    }

    private static string Table(int id, TableData t, long x, long y, long cx, long maxH)
    {
        var columns = Math.Max(t.Columns.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
        if (columns == 0) return "";
        var weights = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            var longest = c < t.Columns.Count ? OfficeDocs.Plain(t.Columns[c]).Length : 0;
            foreach (var row in t.Rows) if (c < row.Count) longest = Math.Max(longest, OfficeDocs.Plain(row[c]).Length);
            weights[c] = Math.Max(4, Math.Min(36, longest));
        }
        var total = weights.Sum();
        var widths = weights.Select(w => (long)(cx * w / total)).ToArray();
        var rows = t.Rows.Count + (t.Columns.Count > 0 ? 1 : 0);
        var sz = rows <= 7 ? 16 : rows <= 10 ? 14 : 12;
        var rowH = (long)(sz * 2.1 * Emu);
        var right = new bool[columns];
        for (var c = 0; c < columns; c++)
        {
            var cells = t.Rows.Where(r => c < r.Count && OfficeDocs.Plain(r[c]).Trim().Length > 0).Select(r => r[c]).ToList();
            right[c] = cells.Count > 0 && cells.All(OfficeDocs.LooksNumeric);
        }
        string Cell(string text, int c, bool head, bool band)
        {
            var fill = head ? HeadFill : band ? "F7F9FC" : "FFFFFF";
            var line = $"<a:solidFill><a:srgbClr val=\"{Grid}\"/></a:solidFill>";
            return "<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>" + Para(Runs(text, sz, Text, bold: head), right[c] && !head ? "r" : null) + "</a:txBody>" +
                   "<a:tcPr marL=\"76200\" marR=\"76200\" marT=\"38100\" marB=\"38100\" anchor=\"ctr\">" +
                   $"<a:lnL w=\"6350\">{line}</a:lnL><a:lnR w=\"6350\">{line}</a:lnR><a:lnT w=\"6350\">{line}</a:lnT><a:lnB w=\"6350\">{line}</a:lnB>" +
                   $"<a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill></a:tcPr></a:tc>";
        }
        var sb = new StringBuilder();
        sb.Append($"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table {id}\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>" +
                  $"<p:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{Math.Min(maxH, rowH * rows)}\"/></p:xfrm>" +
                  "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr firstRow=\"1\" bandRow=\"1\"/><a:tblGrid>");
        foreach (var w in widths) sb.Append($"<a:gridCol w=\"{w}\"/>");
        sb.Append("</a:tblGrid>");
        if (t.Columns.Count > 0)
        {
            sb.Append($"<a:tr h=\"{rowH}\">");
            for (var c = 0; c < columns; c++) sb.Append(Cell(c < t.Columns.Count ? t.Columns[c] : "", c, true, false));
            sb.Append("</a:tr>");
        }
        for (var r = 0; r < t.Rows.Count; r++)
        {
            sb.Append($"<a:tr h=\"{rowH}\">");
            for (var c = 0; c < columns; c++) sb.Append(Cell(c < t.Rows[r].Count ? t.Rows[r][c] : "", c, false, r % 2 == 1));
            sb.Append("</a:tr>");
        }
        sb.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
        return sb.ToString();
    }

    /// <summary>The largest of <paramref name="sizes"/> (points) at which the lines fit the box, by an average character width.</summary>
    internal static int Fit(IReadOnlyList<string> lines, long cx, long cy, int[] sizes, double spacing = 0.2)
    {
        foreach (var size in sizes)
        {
            double charW = size * 0.5 * Emu, lineH = size * 1.2 * Emu, used = 0;
            foreach (var line in lines)
            {
                var chars = Math.Max(1, line.Length);
                var perLine = Math.Max(1, (int)(cx / charW));
                used += Math.Ceiling((double)chars / perLine) * lineH + size * spacing * Emu;
            }
            if (used <= cy) return size;
        }
        return sizes[sizes.Length - 1];
    }

    private static string Para(string runs, string? align = null) =>
        "<a:p>" + (align is null ? "" : $"<a:pPr algn=\"{align}\"/>") + runs + "</a:p>";

    /// <summary>Runs for a line of Markdown (bold, italic, code), in one size and colour.</summary>
    private static string Runs(string text, int size, string color, bool bold = false, bool italic = false)
    {
        var sb = new StringBuilder();
        var spans = Markdown.Inline(text.Replace('\n', ' '));
        if (spans.Count == 0) return $"<a:endParaRPr lang=\"en-GB\" sz=\"{size * 100}\" dirty=\"0\"/>";
        foreach (var s in spans)
        {
            var b = bold || s.Style.HasFlag(Markdown.Style.Bold);
            var i = italic || s.Style.HasFlag(Markdown.Style.Italic);
            sb.Append($"<a:r><a:rPr lang=\"en-GB\" sz=\"{size * 100}\"{(b ? " b=\"1\"" : "")}{(i ? " i=\"1\"" : "")}{(s.Style.HasFlag(Markdown.Style.Strike) ? " strike=\"sngStrike\"" : "")} dirty=\"0\">" +
                      $"<a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill>" +
                      (s.Style.HasFlag(Markdown.Style.Code) ? "<a:latin typeface=\"Consolas\"/>" : "<a:latin typeface=\"+mn-lt\"/>") +
                      $"</a:rPr><a:t>{OfficeDocs.Esc(s.Text)}</a:t></a:r>");
        }
        return sb.ToString();
    }

    private static string Box(int id, string name, long x, long y, long cx, long cy, string paragraphs, string anchor = "t", string? placeholder = null)
    {
        var nv = placeholder is null
            ? $"<p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name} {id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>"
            : $"<p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name} {id}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"{placeholder}\"/></p:nvPr></p:nvSpPr>";
        return "<p:sp>" + nv +
               $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>" +
               $"<p:txBody><a:bodyPr wrap=\"square\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" anchor=\"{anchor}\"><a:normAutofit/></a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";
    }

    private static string Rect(int id, string name, long x, long y, long cx, long cy, string fill) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name} {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
        $"<a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp>";

    private static string Placeholder(int id, string name, string? type, int? idx, long x, long y, long cx, long cy) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph" +
        (type is null ? "" : $" type=\"{type}\"") + (idx is null ? "" : $" idx=\"{idx}\"") + "/></p:nvPr></p:nvSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm></p:spPr>" +
        "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-GB\"/></a:p></p:txBody></p:sp>";

    private static string Layout(string type, string name, string shapes) =>
        OfficeDocs.XmlHead + $"<p:sldLayout {Ns} type=\"{type}\" preserve=\"1\"><p:cSld name=\"{name}\"><p:spTree>" + Group + shapes +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string Master() =>
        OfficeDocs.XmlHead + $"<p:sldMaster {Ns}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>" + Group +
        Placeholder(2, "Title Placeholder 1", "title", null, Pad, 300000, W - 2 * Pad, 800000) +
        Placeholder(3, "Text Placeholder 2", "body", 1, Pad, 1400000, W - 2 * Pad, H - 1400000 - Pad) +
        "</p:spTree></p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/><p:sldLayoutId id=\"2147483650\" r:id=\"rId2\"/></p:sldLayoutIdLst>" +
        "<p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"3200\" b=\"1\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/></a:defRPr></a:lvl1pPr></p:titleStyle>" +
        "<p:bodyStyle><a:lvl1pPr marL=\"342900\" indent=\"-342900\"><a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/><a:defRPr sz=\"2400\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:bodyStyle>" +
        "<p:otherStyle><a:defPPr><a:defRPr lang=\"en-GB\"/></a:defPPr></p:otherStyle></p:txStyles></p:sldMaster>";

    private static string Theme()
    {
        string Fill3(string tag) => string.Concat(Enumerable.Repeat($"<a:{tag}><a:schemeClr val=\"phClr\"/></a:{tag}>", 3));
        return OfficeDocs.XmlHead + "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Sketch ProMax\"><a:themeElements>" +
            "<a:clrScheme name=\"Sketch ProMax\"><a:dk1><a:srgbClr val=\"262626\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1>" +
            $"<a:dk2><a:srgbClr val=\"{Accent}\"/></a:dk2><a:lt2><a:srgbClr val=\"{Light}\"/></a:lt2>" +
            "<a:accent1><a:srgbClr val=\"4F81BD\"/></a:accent1><a:accent2><a:srgbClr val=\"C0504D\"/></a:accent2><a:accent3><a:srgbClr val=\"9BBB59\"/></a:accent3>" +
            "<a:accent4><a:srgbClr val=\"8064A2\"/></a:accent4><a:accent5><a:srgbClr val=\"4BACC6\"/></a:accent5><a:accent6><a:srgbClr val=\"F79646\"/></a:accent6>" +
            "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
            "<a:fontScheme name=\"Sketch ProMax\"><a:majorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
            "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
            "<a:fmtScheme name=\"Sketch ProMax\"><a:fillStyleLst>" + Fill3("solidFill") + "</a:fillStyleLst><a:lnStyleLst>" +
            string.Concat(Enumerable.Repeat("<a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>", 3)) +
            "</a:lnStyleLst><a:effectStyleLst>" + string.Concat(Enumerable.Repeat("<a:effectStyle><a:effectLst/></a:effectStyle>", 3)) +
            "</a:effectStyleLst><a:bgFillStyleLst>" + Fill3("solidFill") + "</a:bgFillStyleLst></a:fmtScheme>" +
            "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }
}
