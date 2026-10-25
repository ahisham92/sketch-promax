using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SketchProMax.Chat;
using SketchProMax.Tools;

/// <summary>
/// make_document's writers (Excel, Word, PowerPoint, PDF), read back with the readers the chat uses for attachments, and
/// the chat's file card finding files in a step's result. No Office, Revit or WPF.
/// </summary>
internal static class DocumentTests
{
    private const string Jpeg = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAAGAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDgKKKK+eP2E//Z";

    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "spm-documents-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var png = Path.Combine(dir, "level 1.png");
            File.WriteAllBytes(png, Png(40, 30, alpha: true));
            var rgb = Path.Combine(dir, "section.png");
            File.WriteAllBytes(rgb, Png(30, 20, alpha: false));
            var jpg = Path.Combine(dir, "photo.jpg");
            File.WriteAllBytes(jpg, Convert.FromBase64String(Jpeg));
            Excel(dir, check);
            Word(dir, png, jpg, check);
            Slides(dir, png, check);
            Pdf(dir, png, rgb, jpg, check);
            Files(check);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static void Excel(string dir, Action<string, bool, string> check)
    {
        var path = Path.Combine(dir, "bill.xlsx");
        var bill = new OfficeXlsx.Sheet("Bill: concrete/steel", "Bill of quantities", ["Item", "Description", "Qty", "Rate", "Amount"],
        [
            new([1, "Columns C40", 12.5, 950.0, new OfficeXlsx.Formula("=C4*D4")]),
            new([2, "=not a formula, typed text", 3, 100, new OfficeXlsx.Formula("C5*D5")]),
            new(["", "Total", null, null, new OfficeXlsx.Formula("SUM(E4:E5)")], Bold: true),
        ], [], [], Landscape: true);
        var notes = new OfficeXlsx.Sheet("Bill: concrete/steel", "", [], [new(["Rates from the cost plan"])], [], [], false);
        OfficeXlsx.Write(path, "Bill", [bill, notes]);
        var rows = Xlsx.Read(path);
        check("excel: title, header, data and total read back",
            rows.Count == 5 && rows[0][0] == "Bill of quantities" && rows[1][2] == "Qty" && rows[2][1] == "Columns C40" && rows[2][2] == "12.5" && rows[4][1] == "Total",
            string.Join(" / ", rows.Select(r => string.Join(",", r))));
        check("excel: the first data row is 4 with a title, 2 without, 1 with no header",
            OfficeXlsx.FirstDataRow(bill) == 4 && OfficeXlsx.FirstDataRow(bill with { Title = "" }) == 2 && OfficeXlsx.FirstDataRow(notes) == 1, "");
        var names = OfficeXlsx.SheetNames([bill, notes]);
        check("excel: sheet names lose : and / and stay unique", names.Count == 2 && names[0] == "Bill concretesteel" && names[1] != names[0] && names[1].Length <= 31, string.Join(" | ", names));
        using var zip = ZipFile.OpenRead(path);
        var bad = Parts(zip);
        check("excel: every part is well-formed XML", bad.Count == 0, string.Join(", ", bad));
        var sheet = Entry(zip, "xl/worksheets/sheet1.xml");
        check("excel: formulas are written as formulas", sheet.Contains("<f>C4*D4</f>") && sheet.Contains("<f>SUM(E4:E5)</f>") && sheet.Contains("<f>C5*D5</f>"), "");
        check("excel: typed text that starts with = stays text", sheet.Contains("=not a formula, typed text") && !sheet.Contains("<f>not a formula"), "");
        check("excel: header frozen, filters on, printed landscape and fitted to the width",
            sheet.Contains("state=\"frozen\"") && sheet.Contains("<autoFilter ref=\"A3:E6\"/>") && sheet.Contains("orientation=\"landscape\"") && sheet.Contains("fitToWidth=\"1\""), "");
        var formats = OfficeXlsx.ColumnFormats(bill, 5);
        check("excel: a column with decimals shows 2 decimals, whole numbers stay general", formats[2] == 2 && formats[3] == 0 && formats[0] == 0, string.Join(",", formats));
        check("excel: the header repeats on printed pages", Entry(zip, "xl/workbook.xml").Contains("_xlnm.Print_Titles"), "");
    }

    private static void Word(string dir, string png, string jpg, Action<string, bool, string> check)
    {
        var path = Path.Combine(dir, "report.docx");
        var markdown = $"""
            # Concrete works

            The columns are **C40** and the slabs *C32/40*. See https://example.com/spec.

            - Columns
              - 600 x 600
            - Slabs

            1. Pour
            2. Cure

            | Element | Volume (m³) |
            |---|---:|
            | Columns | 12.5 |
            | **Total** | **12.5** |

            ![Level 1 plan]({png})
            ![A photo]({jpg})
            ![Gone](C:\nowhere\missing.png)

            [page break]

            > A note.
            """;
        var missing = OfficeDocx.Write(path, "Model report", "Project PRJ1", markdown, landscape: false);
        var text = OfficeText.Read(File.ReadAllBytes(path), word: true).Text;
        check("word: headings, bullets, numbered list and table read back",
            text.Contains("Concrete works") && text.Contains("Columns") && text.Contains("600 x 600") && text.Contains("Cure") && text.Contains("| Columns | 12.5 |"), text);
        using var zip = ZipFile.OpenRead(path);
        var bad = Parts(zip);
        check("word: every part is well-formed XML", bad.Count == 0, string.Join(", ", bad));
        check("word: both pictures are in, the missing one is reported",
            zip.GetEntry("word/media/image1.png") is not null && zip.GetEntry("word/media/image2.jpeg") is not null && missing.Count == 1 && missing[0].EndsWith("missing.png"), string.Join(", ", missing));
        var doc = Entry(zip, "word/document.xml");
        check("word: a link is clickable, the page break and the footer page numbers are there",
            Entry(zip, "word/_rels/document.xml.rels").Contains("https://example.com/spec") && doc.Contains("w:type=\"page\"") && Entry(zip, "word/footer1.xml").Contains("NUMPAGES"), "");
        check("word: a numbered list restarts at 1", Entry(zip, "word/numbering.xml").Contains("<w:startOverride w:val=\"1\"/>"), "");
        check("word: number columns are right aligned", doc.Contains("<w:jc w:val=\"right\"/>"), "");
    }

    private static void Slides(string dir, string png, Action<string, bool, string> check)
    {
        var path = Path.Combine(dir, "deck.pptx");
        var rows = Enumerable.Range(1, 30).Select(i => (IReadOnlyList<string>)[$"P{i}", (i * 100).ToString()]).ToList();
        var slides = new List<OfficePptx.Slide>
        {
            new("Scope", [new("Piles **bored**", 0), new("900 mm", 1)], "", "", null),
            new("Level 1", [new("Columns 600 x 600", 0)], png, "Level 1 plan", null),
            new("Picture only", [], png, "", null),
            new("Pile loads", [new("Working loads", 0)], "", "", new OfficePptx.TableData(["Pile", "Load (kN)"], rows)),
        };
        var expanded = OfficePptx.Expand(slides);
        check("powerpoint: a long table carries on over more slides, each with its header",
            expanded.Count == 6 && expanded[3].Table!.Rows.Count == 9 && expanded[4].Title == "Pile loads (continued)" && expanded.Skip(3).Sum(s => s.Table!.Rows.Count) == 30, string.Join(" | ", expanded.Select(s => s.Title)));
        var missing = OfficePptx.Write(path, "Client presentation", "Stage 3", slides);
        var text = OfficeText.Read(File.ReadAllBytes(path), word: false).Text;
        check("powerpoint: title slide then every slide in order",
            text.Contains("=== Slide 1 of 7 ===") && text.Contains("Client presentation") && text.Contains("=== Slide 7 of 7 ===") && text.IndexOf("Scope") < text.IndexOf("Pile loads"), text);
        using var zip = ZipFile.OpenRead(path);
        var bad = Parts(zip);
        check("powerpoint: every part is well-formed XML", bad.Count == 0, string.Join(", ", bad));
        check("powerpoint: pictures are in and nothing is missing", zip.Entries.Count(e => e.FullName.StartsWith("ppt/media/")) == 2 && missing.Count == 0, string.Join(", ", missing));
        check("powerpoint: bullets keep their level and bold", Entry(zip, "ppt/slides/slide2.xml").Contains("b=\"1\"") && Entry(zip, "ppt/slides/slide2.xml").Contains("marL=\"685800\""), "");
        check("powerpoint: text shrinks to fit a slide", OfficePptx.Fit([new string('x', 2000)], 10_000_000, 4_000_000, [28, 20, 12]) == 12 && OfficePptx.Fit(["Short"], 10_000_000, 4_000_000, [28, 20, 12]) == 28, "");
    }

    private static void Pdf(string dir, string png, string rgb, string jpg, Action<string, bool, string> check)
    {
        var path = Path.Combine(dir, "report.pdf");
        var table = new StringBuilder("| Pile | Load (kN) | Note |\n|---|---:|---|\n");
        for (var i = 1; i <= 80; i++) table.Append($"| P{i} | {i * 100:N0} | A note long enough to wrap across the narrow column of this table, row {i} |\n");
        var markdown = $"""
            # Concrete works

            Columns are **C40**, slabs *C32/40*, spacing ≤ 200 mm, area 12 m², 5 °C – “quoted”. Link: https://example.com/spec

            - First
              - Nested
            1. One
            2. Two

            ```
            code line
            ```

            ![Level 1]({png})
            ![Section]({rgb})
            ![Photo]({jpg})
            ![Missing](C:\none.png)

            ## Piles

            {table}
            """;
        var (pages, missing) = OfficePdf.Write(path, "Model report", "Project PRJ1", markdown, landscape: false);
        var bytes = File.ReadAllBytes(path);
        var assess = PdfAttachment.Assess(bytes);
        check("pdf: a valid PDF the chat could send, with the pages the writer counted",
            assess.Verdict == PdfAttachment.Verdict.Send && assess.Pages == pages && pages >= 3, $"{assess.Verdict}, {assess.Pages} pages, writer {pages}");
        var textFile = Path.Combine(dir, "report.txt");
        var read = PdfFiles.WriteText(path, textFile);
        var text = File.Exists(textFile) ? File.ReadAllText(textFile) : "";
        check("pdf: its text reads back (title, words, numbers, the last table row)",
            read.Error is null && text.Contains("Model report") && text.Contains("C40") && text.Contains("8,000") && text.Contains("row 80"), read.Error ?? text.Substring(0, Math.Min(300, text.Length)));
        check("pdf: symbols Helvetica lacks are spelled out", text.Contains("<= 200") && text.Contains("m²"), "");
        check("pdf: the table header repeats on each page it runs onto", System.Text.RegularExpressions.Regex.Matches(text, "Load \\(kN\\)").Count >= 2, "");
        check("pdf: pictures go in (PNG with and without transparency, JPEG), the missing one is reported",
            missing.Count == 1 && missing[0] == @"C:\none.png" && Count(bytes, "/Subtype /Image") == 4 && Count(bytes, "/SMask") == 1 && Count(bytes, "/DCTDecode") == 1, $"{string.Join(", ", missing)}; images {Count(bytes, "/Subtype /Image")}");
        check("pdf: page numbers in the footer and the link clickable", text.Contains($"Page {pages} of {pages}") && Count(bytes, "/URI (https://example.com/spec)") == 1, "");
        var (landPages, _) = OfficePdf.Write(Path.Combine(dir, "land.pdf"), "", "", "Just a line.", landscape: true);
        check("pdf: landscape, no title, one page", landPages == 1 && PdfAttachment.Assess(File.ReadAllBytes(Path.Combine(dir, "land.pdf"))).Pages == 1, "");
        check("pdf: text outside Western letters shows as ?", OfficePdf.Encode("مرحبا Ab") == "????? Ab" && OfficePdf.Encode("“a” – b…") == "\u0093a\u0094 \u0096 b\u0085", OfficePdf.Encode("مرحبا Ab"));
    }

    private static void Files(Action<string, bool, string> check)
    {
        var one = ChatFiles.Find("""{"file":"C:\\Users\\me\\Documents\\Sketch ProMax\\Made in chat\\Bill.xlsx","format":"Excel workbook"}""");
        check("chat files: a step's file is found", one.Count == 1 && one[0] == @"C:\Users\me\Documents\Sketch ProMax\Made in chat\Bill.xlsx", string.Join(", ", one));
        var many = ChatFiles.Find("""{"files":["C:\\out\\A.pdf", "C:\\out\\B.pdf","C:\\out\\A.pdf"],"file":"C:\\out\\notes.json"}""");
        check("chat files: a list is found once each, other kinds are left out", many.Count == 2 && many[0] == @"C:\out\A.pdf" && many[1] == @"C:\out\B.pdf", string.Join(", ", many));
        check("chat files: relative paths and results cut short are safe",
            ChatFiles.Find("""{"file":"Bill.xlsx"}""").Count == 0 && ChatFiles.Find("""{"file":"C:\\out\\Bi""").Count == 0 && ChatFiles.Find(null).Count == 0, "");
        check("chat files: kinds in words", ChatFiles.Kind("a.xlsx") == "Excel workbook" && ChatFiles.Kind("a.PPTX") == "PowerPoint presentation" && ChatFiles.Size(2048) == "2 KB" && ChatFiles.Size(1572864) == "1.5 MB", "");
        check("chat files: the step says what it makes",
            StepNames.Describe("mcp__sketch-promax__make_document", """{"format":"excel","title":"Bill of quantities"}""") == "Make an Excel workbook: Bill of quantities"
            && StepNames.Title("make_document") == "Make a file" && StepNames.ReadOnly("make_document"), StepNames.Describe("make_document", """{"format":"excel","title":"Bill of quantities"}"""));
        check("file names: no characters Windows refuses, a free name", OfficeDocs.SafeName("BOQ: Level 1/2 *final*?") == "BOQ Level 1 2 final" && OfficeDocs.SafeName("  ") == "Document", OfficeDocs.SafeName("BOQ: Level 1/2 *final*?"));
        var blocks = OfficeDocs.Blocks("Text\n![a](C:\\a.png)\n![b](C:\\b.png)\n\n[page break]\n\nMore");
        check("markdown: picture lines and page breaks are their own blocks",
            blocks.Count == 5 && blocks[1] is OfficeDocs.ImageBlock { Path: @"C:\a.png", Caption: "a" } && blocks[2] is OfficeDocs.ImageBlock && blocks[3] is OfficeDocs.PageBreak,
            string.Join(" | ", blocks.Select(b => b.GetType().Name)));
    }

    // ---- Helpers

    private static List<string> Parts(ZipArchive zip)
    {
        var bad = new List<string>();
        foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
        {
            try { XDocument.Parse(Entry(zip, e.FullName)); }
            catch (System.Xml.XmlException ex) { bad.Add(e.FullName + ": " + ex.Message); }
        }
        return bad;
    }

    private static string Entry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static int Count(byte[] bytes, string what) => System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(bytes), System.Text.RegularExpressions.Regex.Escape(what)).Count;

    /// <summary>A small PNG (RGB or RGBA) with every row filter, as Revit and screenshots write them.</summary>
    private static byte[] Png(int w, int h, bool alpha)
    {
        var channels = alpha ? 4 : 3;
        var raw = new MemoryStream();
        var prev = new byte[w * channels];
        for (var y = 0; y < h; y++)
        {
            var row = new byte[w * channels];
            for (var x = 0; x < w; x++)
            {
                row[x * channels] = (byte)(x * 6);
                row[x * channels + 1] = (byte)(y * 8);
                row[x * channels + 2] = 120;
                if (alpha) row[x * channels + 3] = (byte)(x < w / 2 ? 255 : 128);
            }
            var filter = (byte)(y % 3); // none, sub, up
            raw.WriteByte(filter);
            for (var i = 0; i < row.Length; i++)
            {
                var left = i >= channels ? row[i - channels] : 0;
                raw.WriteByte(filter switch { 1 => (byte)(row[i] - left), 2 => (byte)(row[i] - prev[i]), _ => row[i] });
            }
            prev = row;
        }
        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);
        void Chunk(string kind, byte[] data)
        {
            var head = new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
            png.Write(head);
            var body = Encoding.ASCII.GetBytes(kind).Concat(data).ToArray();
            png.Write(body);
            var crc = Crc(body);
            png.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        }
        var ihdr = new byte[13];
        ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
        ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
        ihdr[8] = 8;
        ihdr[9] = (byte)(alpha ? 6 : 2);
        Chunk("IHDR", ihdr);
        Chunk("IDAT", OfficePdf.Deflate(raw.ToArray()));
        Chunk("IEND", []);
        return png.ToArray();
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
