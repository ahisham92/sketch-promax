using System.IO.Compression;
using SketchProMax.Tools;
using UglyToad.PdfPig;
using static SketchProMax.Tools.SignaturePages;

/// <summary>
/// Signature pages: the list read from Excel (with or without a header, REV written after the number), revisions, file
/// names and folders, the Word file and PDF of each document read back, and the signatures locked with each person's PIN.
/// </summary>
internal static class SignaturePagesTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "spm-sigpages-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        var oldRoot = Root;
        Root = Path.Combine(dir, "pc");
        try
        {
            Revisions(check);
            List(dir, check);
            Files(dir, check);
            People(check);
            Kept(check);
            Idc(dir, check);
            Admin(check);
            Numbering(check);
        }
        finally
        {
            Root = oldRoot;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static void Revisions(Action<string, bool, string> check)
    {
        check("sigpages: revisions read as the table shows them", CleanRev("Rev 01") == "1" && CleanRev("01") == "1" && CleanRev("rev1") == "1" && CleanRev("R2") == "2" && CleanRev("0") == "0" && CleanRev("P02") == "P02", "");
        check("sigpages: REV after the number has two digits when set", RevText("1", true) == "REV 01" && RevText("0", false) == "REV 0" && RevText("", true) == "REV 00" && RevText("P02", true) == "REV P02", RevText("1", true));
        check("sigpages: a number typed with its REV is split", SplitNumber("PRJ1-FD-SITE-00-MOM-ST-01 REV 00") == ("PRJ1-FD-SITE-00-MOM-ST-01", "0") && SplitNumber("PRJ1-A-01_Rev1") == ("PRJ1-A-01", "1") && SplitNumber("PRJ1-A-01") == ("PRJ1-A-01", ""),
            SplitNumber("PRJ1-FD-SITE-00-MOM-ST-01 REV 00").ToString());
        var setup = new Setup();
        check("sigpages: the type is read from the number", TypeFromNumber(setup, "PRJ1-FD-SITE-06-BOQ-ST-01") == "BOQ" && TypeFromNumber(setup, "PRJ1-X-01") == "", "");
        check("sigpages: every built-in type has a heading and no form code (the office fills its own)", DefaultTypes().All(t => t.Heading.Length > 0 && t.Form.Length == 0), "");
        check("sigpages: an unknown type's heading is its code", TypeOf(setup, "xyz").Heading == "XYZ" && TypeOf(setup, "boq").Heading == "Project bill of quantities", "");
    }

    private static void List(string dir, Action<string, bool, string> check)
    {
        var setup = new Setup();
        var cells = new List<(int, int, string)>
        {
            (1, 1, "Submission 3"),
            (1, 3, "Doc type"), (2, 3, "Document No."), (3, 3, "Revision"), (4, 3, "Title"),
            (1, 4, "BOQ"), (2, 4, "PRJ1-FD-SITE-05-BOQ-ST-01"), (3, 4, "Rev 0"), (4, 4, "Berth 5 - Bill of Quantities"),
            (2, 5, "PRJ1-FD-SITE-00-SPC-ST-01 REV 01"), (4, 5, "Specifications"),
            (4, 6, "a note without a number"),
        };
        var docs = FromCells(setup, cells);
        check("sigpages: Excel list with a header row", docs.Count == 2 && docs[0] == new Doc("BOQ", "PRJ1-FD-SITE-05-BOQ-ST-01", "0", "Berth 5 - Bill of Quantities")
            && docs[1] == new Doc("SPC", "PRJ1-FD-SITE-00-SPC-ST-01", "1", "Specifications"), string.Join(" | ", docs));

        var bare = FromCells(setup, [(1, 1, "PRJ1-FD-SITE-00-RPT-ST-01"), (2, 1, "Final Design Report - Structural"), (3, 1, "1"),
                                     (1, 2, "PRJ1-FD-SITE-00-MOM-ST-02 REV 02"), (2, 2, "Method of Measurement")]);
        check("sigpages: Excel list without a header", bare.Count == 2 && bare[0].Type == "RPT" && bare[0].Title == "Final Design Report - Structural" && bare[1].Rev == "2" && bare[1].Type == "MOM",
            string.Join(" | ", bare));

        var template = Path.Combine(dir, "template.xlsx");
        WriteTemplate(template);
        var read = ReadList(setup, template);
        check("sigpages: the Excel template reads back", read.Count == 2 && read[0].Type == "BOQ" && read[1].Rev == "1" && read[1].Title.Length > 0, string.Join(" | ", read));

        var csv = Path.Combine(dir, "list.csv");
        File.WriteAllLines(csv, ["Number,Rev,Title", "\"PRJ1-FD-SITE-08-BOQ-ST-01\",2,\"Berth 8, Bill of Quantities\""]);
        var fromCsv = ReadList(setup, csv);
        check("sigpages: a CSV list reads", fromCsv.Count == 1 && fromCsv[0].Title == "Berth 8, Bill of Quantities" && fromCsv[0].Rev == "2" && fromCsv[0].Type == "BOQ", string.Join(" | ", fromCsv));
        var xls = Path.Combine(dir, "old.xls");
        File.WriteAllText(xls, "x");
        string? refused = null;
        try { ReadList(setup, xls); } catch (InvalidDataException ex) { refused = ex.Message; }
        check("sigpages: an old .xls is refused with what to do", refused?.Contains(".xlsx") == true, refused ?? "");
    }

    private static void Files(string dir, Action<string, bool, string> check)
    {
        var docs = new List<Doc>
        {
            new("BOQ", "PRJ1-FD-SITE-05-BOQ-ST-01", "0", "Final Design Structures Berth 5 - Bill of Quantities"),
            new("BOQ", "PRJ1-FD-SITE-05-BOQ-ST-01", "0", "the same number again"),
            new("SPC", "PRJ1-FD-SITE-00-SPC-ST-01", "1", "Final Design Structures Specifications"),
        };
        var plan = Plan(docs, dir, new Setup());
        check("sigpages: each document has its Word file and PDF in TYPE\\Word and TYPE\\PDF", plan.Count == 3
            && plan[0].Word == Path.Combine(dir, "BOQ", "Word", "PRJ1-FD-SITE-05-BOQ-ST-01 REV 00 signature page.docx")
            && plan[0].Pdf == Path.Combine(dir, "BOQ", "PDF", "PRJ1-FD-SITE-05-BOQ-ST-01 REV 00 signature page.pdf")
            && plan[1].Word.EndsWith("signature page (2).docx") && plan[2].Pdf.Contains(Path.Combine("SPC", "PDF")), plan[1].Word);
        check("sigpages: only specifications get an IDC table, next to their signature page", plan[0].IdcPdf == "" && plan[0].IdcWord == ""
            && plan[2].IdcPdf == Path.Combine(dir, "SPC", "PDF", "PRJ1-FD-SITE-00-SPC-ST-01 REV 01 IDC table.pdf")
            && plan[2].IdcWord == Path.Combine(dir, "SPC", "Word", "PRJ1-FD-SITE-00-SPC-ST-01 REV 01 IDC table.docx"), plan[2].IdcPdf);
        check("sigpages: file names are safe for Windows", FileName(new Doc("DWG", "A/B:C*01", "1", ""), true) == "A_B_C_01 REV 01 signature page", FileName(new Doc("DWG", "A/B:C*01", "1", ""), true));

        var setup = new Setup
        {
            Project = "Consultancy services for a port: a long project name that has to wrap onto a second line on the page",
            Discipline = "Structures Engineering", By = "A.B.", Checked = "C.D.", Approved = "E.F.",
        };
        setup.Types[0].Form = "FORM-01 (F1) REV 9";
        var sign = OfficeDocs.ReadPicture(DocumentTestsPng(60, 20))!;
        var common = new Common(setup, new DateTime(2026, 5, 14), sign, sign, null, null);
        var outDir = Path.Combine(dir, "out");
        var written = Make(docs, outDir, common);
        check("sigpages: Make writes a Word file and a PDF per document, plus the IDC table for specifications", written.Count == 8 && written.All(File.Exists), string.Join(", ", written.Select(Path.GetFileName)));

        using (var pdf = PdfDocument.Open(plan[0].Pdf.Replace(dir, outDir)))
        {
            var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
            var images = pdf.GetPage(1).GetImages().Count();
            check("sigpages: the PDF is one page with the heading, title, table, number and form code", pdf.NumberOfPages == 1
                && text.Contains("Project bill of quantities") && text.Contains("Berth 5 - Bill of Quantities") && text.Contains("14/05/2026") && text.Contains("Approved Issue")
                && text.Contains("A.B.") && text.Contains("E.F.") && text.Contains("PRJ1-FD-SITE-05-BOQ-ST-01 REV 00") && text.Contains("FORM-01") && text.Contains("page 1/1"), text);
            check("sigpages: the PDF has the logo and only the signatures given (By)", images == 2, images.ToString());
        }
        var layout = Layout(docs[0], common);
        var project = layout.Where(m => m.Kind == Kind.Text && setup.Project.Contains(m.Text)).ToList();
        check("sigpages: a long project name wraps inside the page", project.Count >= 2 && project.All(m => OfficePdf.TextWidth(m.Text, false, m.Size) <= Right - Left), project.Count.ToString());
        var signs = layout.Where(m => m.Kind == Kind.Picture && m.Y == SignTop).ToList();
        check("sigpages: the By signature sits under the By column", signs.Count == 1 && signs[0].X > ColumnX[3] && signs[0].X + signs[0].W < ColumnX[4], signs.Count.ToString());

        using (var zip = ZipFile.OpenRead(plan[2].Word.Replace(dir, outDir)))
        {
            var xml = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
            var footer = new StreamReader(zip.GetEntry("word/footer1.xml")!.Open()).ReadToEnd();
            check("sigpages: the Word file has the heading, title, table, number and REV", xml.Contains("Project specification") && xml.Contains("Final Design Structures Specifications")
                && xml.Contains("PRJ1-FD-SITE-00-SPC-ST-01 REV 01") && xml.Contains(">Approved<") && xml.Contains("14/05/2026") && xml.Contains("w:tblpY"), "");
            check("sigpages: the Word file has the signature and the logo as pictures", zip.Entries.Count(e => e.FullName.StartsWith("word/media/")) == 2 && xml.Contains("page 1/1"), "");
            System.Xml.Linq.XDocument.Parse(xml);
            System.Xml.Linq.XDocument.Parse(footer);
        }
    }

    private static void People(Action<string, bool, string> check)
    {
        var picture = DocumentTestsPng(30, 10);
        var signer = Lock("E.N.", "Engineer", picture, "4821");
        check("sigpages: the right PIN gives the signature back", Unlock(signer, "4821")?.Bytes.SequenceEqual(picture) == true, "");
        check("sigpages: a wrong PIN gives nothing", Unlock(signer, "4822") is null && Unlock(signer, "") is null, "");
        check("sigpages: the signature isn't kept readable on the PC", !Convert.FromBase64String(signer.Locked).Take(8).SequenceEqual(picture.Take(8)), "");
        check("sigpages: a PIN under 4 characters is refused", PinProblem("123") is not null && PinProblem("1234") is null, "");
        check("sigpages: people are found by initials, dots ignored", FindSigner([signer], "en")?.Name == "Engineer" && FindSigner([signer], "E.N") is not null && FindSigner([signer], "A.P.") is null, "");
    }

    private static void Kept(Action<string, bool, string> check)
    {
        var setup = new Setup { Project = "Port works", Discipline = "Structures", By = "E.N." };
        setup.Docs.Add(new Doc("BOQ", "PRJ1-01", "0", "Bill"));
        SaveSetup("PRJ1", setup);
        var back = LoadSetup("PRJ1");
        check("sigpages: the project's setup is kept on the PC", back.Project == "Port works" && back.By == "E.N." && back.Docs.Count == 1 && back.Docs[0].Number == "PRJ1-01" && Projects().Contains("PRJ1"), "");
        SaveSigners([Lock("C.K.", "", DocumentTestsPng(10, 10), "9999")]);
        check("sigpages: people are kept on the PC", LoadSigners() is [{ Initials: "C.K." } one] && Unlock(one, "9999") is not null, "");
    }

    private static void Idc(string dir, Action<string, bool, string> check)
    {
        var cells = new List<(int, int, string)>
        {
            (1, 1, "Ref"), (2, 1, "Division - section title"), (3, 1, "Lead"), (4, 1, "IDC"),
            (1, 2, "032000"), (2, 2, "CONCRETE - CONCRETE REINFORCING"), (3, 2, "ST"), (4, 2, "GE"),
            (1, 3, "355933"), (2, 3, "WATERWAYS AND MARINE WORKS - BOLLARDS"), (3, 3, "ST"),
            (2, 4, "a title without a reference"),
        };
        var sections = SectionsFromCells(cells);
        check("sigpages: spec sections read from Excel", sections.Count == 3 && sections[0] == new Section("032000", "CONCRETE - CONCRETE REINFORCING", "ST", "GE")
            && sections[1].Idc == "" && sections[2].Ref == "", string.Join(" | ", sections));
        var template = Path.Combine(dir, "sections.xlsx");
        WriteTemplate(template);
        check("sigpages: the Excel template has a spec sections sheet that reads back", ReadSections(template) is { Count: > 0 } back && back[0].Lead.Length > 0, "");

        var setup = new Setup { IdcForm = "FORM-03 (F1A) REV 6" };
        setup.Sections.AddRange(sections);
        var doc = new Doc("SPC", "PRJ1-FD-SITE-00-SPC-ST-01", "1", "Specifications");
        var common = new Common(setup, new DateTime(2026, 5, 14), null, null, null, null);
        var pdfPath = Path.Combine(dir, "idc.pdf");
        OfficeDocs.WriteFile(pdfPath, s => WriteIdcPdf(s, doc, common));
        using (var pdf = PdfDocument.Open(pdfPath))
        {
            var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
            check("sigpages: the IDC table PDF has the number, the sections, the departments and the form code", pdf.NumberOfPages == 1
                && text.Contains("PRJ1-FD-SITE-00-SPC-ST-01 REV 01") && text.Contains("contents") && text.Contains("032000") && text.Contains("BOLLARDS")
                && text.Contains("IDC department") && text.Contains("DSS") && text.Contains("S&B") && text.Contains("FORM-03") && text.Contains("page 1/1"), text);
        }
        var marks = IdcLayout(doc, common);
        check("sigpages: the IDC table stays on the page", marks.All(m => m.Y < PageH - 20 && m.X + m.W <= PageW), "");
        setup.Sections.AddRange(Enumerable.Range(0, 60).Select(i => new Section($"{i:000000}", "SECTION", "ST", "GE")));
        check("sigpages: many sections still fit one page", IdcLayout(doc, common).All(m => m.Y < 760 || m.Text.Contains("REV") || m.Text.Contains("page") || m.Text.Contains("FORM")), "");

        var wordPath = Path.Combine(dir, "idc.docx");
        OfficeDocs.WriteFile(wordPath, s => WriteIdcWord(s, doc, common));
        using var zip = ZipFile.OpenRead(wordPath);
        var xml = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
        System.Xml.Linq.XDocument.Parse(xml);
        check("sigpages: the IDC table Word file has the table, the departments and the number", xml.Contains("032000") && xml.Contains("Design discipline") && xml.Contains("IDC department")
            && xml.Contains("S&amp;B") && xml.Contains("PRJ1-FD-SITE-00-SPC-ST-01 REV 01") && xml.Contains("w:vMerge"), "");
    }

    private static void Admin(Action<string, bool, string> check)
    {
        check("sigpages: no admin until one is set", !HasAdmin() && !IsAdmin("anything"), "");
        check("sigpages: an admin password under 6 characters is refused", AdminPasswordProblem("12345") is not null && AdminPasswordProblem("123456") is null, "");
        var code = SetAdmin("admin-pass");
        check("sigpages: setting the admin password gives a recovery code", HasAdmin() && System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"), code);
        check("sigpages: only the admin password opens the admin panel", IsAdmin("admin-pass") && !IsAdmin("admin-pas") && !IsAdmin(""), "");
        check("sigpages: the recovery code works typed any way, and nothing else does", IsRecoveryCode(code) && IsRecoveryCode(code.ToLowerInvariant().Replace("-", " ")) && !IsRecoveryCode("AAAA-BBBB-CCCC"), "");
        var next = SetAdmin("new-password");
        check("sigpages: a new admin password replaces the old one and the old recovery code", IsAdmin("new-password") && !IsAdmin("admin-pass") && !IsRecoveryCode(code) && IsRecoveryCode(next), "");
        check("sigpages: the admin file holds no password or code", !File.ReadAllText(Path.Combine(Root, "admin.json")).Contains("new-password") && !File.ReadAllText(Path.Combine(Root, "admin.json")).Contains(next), "");

        var person = NewPerson(" G.L. ", "Group leader");
        check("sigpages: a person the admin adds isn't set up until they choose a PIN", !person.IsSetUp && person.Initials == "G.L." && Unlock(person, "1234") is null, "");
        var picture = DocumentTestsPng(12, 6);
        SetUp(person, picture, "2468");
        check("sigpages: the person sets their own signature and PIN", person.IsSetUp && Unlock(person, "2468")?.Bytes.SequenceEqual(picture) == true, "");
        check("sigpages: changing the PIN needs the old one", !ChangePin(person, "1111", "3579") && Unlock(person, "2468") is not null, "");
        check("sigpages: the PIN changes and the signature stays", ChangePin(person, "2468", "3579") && Unlock(person, "3579")?.Bytes.SequenceEqual(picture) == true && Unlock(person, "2468") is null, "");
        ResetPin(person);
        check("sigpages: a reset (lost PIN) removes the signature; the person sets it up again", !person.IsSetUp && Unlock(person, "3579") is null && person.Initials == "G.L.", "");
    }

    private static void Numbering(Action<string, bool, string> check)
    {
        var types = DefaultTypes().Select(t => t.Code).ToList();
        var pattern = LearnPattern("PRJ1-0100D-FD-AAA-05-BOQ-15A-ST-01", ["AAA", "BBB"], types);
        check("sigpages: the numbering is learned from one number", pattern == "PRJ1-0100D-FD-{SITE}-{AREA:2}-{TYPE}-15A-ST-{SEQ:2}", pattern ?? "null");
        check("sigpages: a number is built from site, area, type and sequence", ComposeNumber(pattern!, new NumberParts("bbb", "6", "CAL", "3")) == "PRJ1-0100D-FD-BBB-06-CAL-15A-ST-03", ComposeNumber(pattern!, new NumberParts("bbb", "6", "CAL", "3")));
        check("sigpages: a number is read back into its parts", ReadNumber(pattern!, "PRJ1-0100D-FD-BBB-00-SPC-15A-ST-12") == new NumberParts("BBB", "00", "SPC", "12")
            && ReadNumber(pattern!, "OTHER-01") is null, "");
        string[] have = ["PRJ1-0100D-FD-AAA-05-BOQ-15A-ST-01", "PRJ1-0100D-FD-AAA-05-BOQ-15A-ST-02", "PRJ1-0100D-FD-BBB-05-BOQ-15A-ST-07"];
        check("sigpages: the next sequence is the next free one for that site, area and type", NextSeq(pattern!, have, "AAA", "5", "BOQ") == "03" && NextSeq(pattern!, have, "AAA", "06", "BOQ") == "01", NextSeq(pattern!, have, "AAA", "5", "BOQ"));
        var other = ForSite(pattern!, [new Doc("BOQ", have[0], "1", "Berth 5 - Bill of Quantities"), new Doc("DWG", "X-1", "0", "")], "BBB");
        check("sigpages: the same set for the other site keeps revisions and titles", other is [{ Number: "PRJ1-0100D-FD-BBB-05-BOQ-15A-ST-01", Rev: "1", Title: "Berth 5 - Bill of Quantities" }], string.Join(" | ", other));
        check("sigpages: sites are typed as code = name", ParseSites("aaa = Site A, BBB=Site B; CCC") is [{ Code: "AAA", Name: "Site A" }, { Code: "BBB", Name: "Site B" }, { Code: "CCC" }]
            && SitesText(ParseSites("AAA = Site A, CCC")) == "AAA = Site A, CCC", SitesText(ParseSites("aaa = Site A, BBB=Site B; CCC")));
        check("sigpages: a number with no type or sequence teaches nothing", LearnPattern("ABC", ["AAA"], types) is null && LearnPattern("PRJ1-AAA-05-XYZ-AB", ["AAA"], types) is null, "");
    }

    /// <summary>A small RGBA PNG.</summary>
    private static byte[] DocumentTestsPng(int w, int h)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < w; x++) { raw.WriteByte(20); raw.WriteByte(40); raw.WriteByte(160); raw.WriteByte((byte)(x % 2 == 0 ? 255 : 0)); }
        }
        var data = OfficePdf.Deflate(raw.ToArray());
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] body)
        {
            var len = new[] { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length };
            png.Write(len);
            var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
            png.Write(typed);
            var crc = Crc(typed);
            png.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        }
        Chunk("IHDR", [0, 0, 0, (byte)w, 0, 0, 0, (byte)h, 8, 6, 0, 0, 0]);
        Chunk("IDAT", data);
        Chunk("IEND", []);
        return png.ToArray();
    }

    private static uint Crc(byte[] bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }
}
