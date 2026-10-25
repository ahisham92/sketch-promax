using System.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace SketchProMax.Tools;

/// <summary>
/// What the PDF editor must do, run on sample PDFs in a folder: open, merge, delete, turn, move, replace, blank pages,
/// undo, save over the file (keeping a backup), extract, split three ways and draw a page. Each saved file is read back
/// with PdfPig (a second PDF library), so the check doesn't trust the engine about its own output. The unit tests and
/// the field test's "pdf.editor" probe both run it. No Revit and no WPF.
/// </summary>
internal static class PdfEditCheck
{
    /// <summary>Runs every check in <paramref name="folder"/>, reporting each to <paramref name="check"/> (name, passed, detail).</summary>
    public static void Run(string folder, Action<string, bool, string> check)
    {
        Directory.CreateDirectory(folder);
        // A name with spaces and non-Latin letters, as drawings often have.
        var a = Path.Combine(folder, "Drawings set A – رسومات.pdf");
        var b = Path.Combine(folder, "set B.pdf");
        File.WriteAllBytes(a, Labeled("A", 5));
        File.WriteAllBytes(b, Labeled("B", 3, landscape: true));

        using var s = new PdfEditSession();
        s.Open(a);
        check("pdf editor: opens a PDF with its pages and sizes", s.Pages.Count == 5 && s.File == Path.GetFullPath(a) && !s.Changed
            && Math.Abs(s.Pages[0].Width - 595) < 1 && Math.Abs(s.Pages[0].Height - 842) < 1, $"{s.Pages.Count} pages");

        var picture = s.Draw(s.Pages[0], 120, 160);
        var ink = 0;
        for (var y = 0; y < picture.Height; y++)
            for (var x = 0; x < picture.Width; x++)
                if (picture.Pixels[y * picture.Stride + x * 4] < 128) ink++;
        check("pdf editor: draws a page picture that fits and shows its text", picture.Width <= 120 && picture.Height == 160 && picture.Width >= 110 && ink > 10,
            $"{picture.Width}x{picture.Height}, {ink} dark pixels");
        var turned = s.Draw(s.Pages[0].Turned(1), 160, 160);
        check("pdf editor: a turned page is drawn on its side", turned.Width > turned.Height, $"{turned.Width}x{turned.Height}");

        // Merge: B's pages after A's page 2.
        var bPages = s.Load(b);
        s.Insert(2, bPages);
        check("pdf editor: inserts another PDF's pages where asked", Labels(s) == "A1 A2 B1 B2 B3 A3 A4 A5", Labels(s));
        s.Delete([0, 4]);
        check("pdf editor: deletes the selected pages", Labels(s) == "A2 B1 B2 A3 A4 A5", Labels(s));
        s.Rotate([1], 1);
        s.Rotate([1], 1);
        s.Rotate([3], -1);
        check("pdf editor: turns pages left and right", s.Pages[1].Turns == 2 && s.Pages[3].Turns == 3 && s.Pages[0].Turns == 0, "");
        var at = s.MoveTo([4, 5], 0);
        check("pdf editor: moves pages to a new place", at == 0 && Labels(s) == "A4 A5 A2 B1 B2 A3", Labels(s));
        var nudged = s.Nudge([2], 1);
        check("pdf editor: moves a page one place later", nudged.SequenceEqual([3]) && Labels(s) == "A4 A5 B1 A2 B2 A3", Labels(s));
        s.Replace([2, 4], [bPages[2]]);
        check("pdf editor: replaces the selected pages with another page", Labels(s) == "A4 A5 B3 A2 A3", Labels(s));
        s.Insert(5, [PdfEditSession.Blank(s.Pages[4])]);
        check("pdf editor: adds a blank page the size of its neighbour", s.Pages.Count == 6 && s.Pages[5].Source == -1 && s.Pages[5].Turns == 0
            && Math.Abs(s.Pages[5].Aspect - s.Pages[4].Aspect) < 0.01 && Math.Abs(s.Pages[5].Width - 842) < 1, $"{s.Pages[5].Width}x{s.Pages[5].Height}"); // A3 shows on its side
        var before = Labels(s);
        s.Delete([0, 1, 2]);
        var undone = s.Undo();
        var afterUndo = Labels(s);
        var redone = s.Redo();
        var afterRedo = Labels(s);
        s.Undo();
        check("pdf editor: undo and redo", undone && redone && afterUndo == before && afterRedo == "A2 A3 _" && Labels(s) == before && s.Changed, $"{afterUndo} / {afterRedo}");

        // Extract and save, read back with PdfPig.
        var extract = Path.Combine(folder, "extract.pdf");
        s.WriteCopy([s.Pages[2], s.Pages[3]], extract);
        check("pdf editor: extracts the selected pages to a new PDF", Read(extract) == "B3 A2", Read(extract));

        var backups = Path.Combine(folder, "backups");
        var backup = s.Save(a, backups);
        check("pdf editor: saves over the file, keeping the old one as a backup", Read(a) == "A4 A5 B3 A2 A3 _" && backup is not null && Read(backup) == "A1 A2 A3 A4 A5"
            && !s.Changed && !s.CanUndo && s.Pages.Count == 6 && s.File == Path.GetFullPath(a), $"{Read(a)} / {(backup is null ? "no backup" : Read(backup))}");
        using (var doc = PdfDocument.Open(a))
        {
            var r = Enumerable.Range(1, doc.NumberOfPages).Select(n => doc.GetPage(n).Rotation.Value).ToList();
            var landscape = doc.GetPage(3).Width > doc.GetPage(3).Height || r[2] % 180 == 90;
            check("pdf editor: saved pages keep their turns and sizes", r[0] == 0 && landscape, string.Join(",", r));
        }

        // A turned page saved: B1 turned twice, then saved and read back.
        var turnedFile = Path.Combine(folder, "turned.pdf");
        using (var t = new PdfEditSession())
        {
            t.Open(b);
            t.Rotate([0], 1);
            t.Rotate([1], -1);
            t.Save(turnedFile, null);
            using var doc = PdfDocument.Open(turnedFile);
            var r = Enumerable.Range(1, 3).Select(n => doc.GetPage(n).Rotation.Value).ToList();
            check("pdf editor: a turn is saved as the page's rotation", r.SequenceEqual([90, 270, 0]), string.Join(",", r));
        }

        // Splits.
        var every = s.Split(Path.Combine(folder, "every"), "doc", PdfEditSession.SplitBy.EveryN, n: 4);
        check("pdf editor: splits every N pages", every.Count == 2 && Read(every[0].File) == "A4 A5 B3 A2" && Read(every[1].File) == "A3 _"
            && Path.GetFileName(every[1].File) == "doc - part 2 (pages 5-6).pdf", string.Join(" | ", every.Select(p => Path.GetFileName(p.File))));
        var picked = s.Split(Path.Combine(folder, "picked"), "doc", PdfEditSession.SplitBy.AtSelected, starts: [2, 5]);
        check("pdf editor: splits at the selected pages", picked.Count == 3 && Read(picked[1].File) == "B3 A2 A3" && Read(picked[2].File) == "_", string.Join(" | ", picked.Select(p => Read(p.File))));
        var again = s.Split(Path.Combine(folder, "picked"), "doc", PdfEditSession.SplitBy.AtSelected, starts: [2, 5]);
        check("pdf editor: a split never overwrites earlier parts", again.All(p => File.Exists(p.File)) && again[0].File != picked[0].File && File.Exists(picked[0].File), Path.GetFileName(again[0].File));

        var heavy = Path.Combine(folder, "heavy.pdf");
        File.WriteAllBytes(heavy, PdfFiles.Sample(8, noise: 300));
        using (var h = new PdfEditSession())
        {
            h.Open(heavy);
            var limit = 600 * 1024L;
            var bySize = h.Split(Path.Combine(folder, "size"), "heavy", PdfEditSession.SplitBy.Size, maxBytes: limit);
            var pages = bySize.Sum(p => PageCount(p.File));
            check("pdf editor: splits by size, every page once, each part under the size", bySize.Count >= 3 && pages == 8 && bySize.All(p => p.Bytes <= limit && new FileInfo(p.File).Length == p.Bytes)
                && bySize[0].First == 1 && bySize[^1].Last == 8 && !Directory.GetFiles(Path.Combine(folder, "size"), "~*").Any(),
                string.Join(" | ", bySize.Select(p => $"{p.First}-{p.Last} {p.Bytes / 1024} KB")));
        }

        // Errors a person can act on.
        var junk = Path.Combine(folder, "not a pdf.pdf");
        File.WriteAllText(junk, "hello");
        string? said = null;
        try { new PdfEditSession().Load(junk); }
        catch (PdfEngine.OpenException ex) { said = ex.Message; }
        check("pdf editor: a damaged file is refused with what to do", said is not null && said.Contains("save a fresh copy"), said ?? "opened");
        var missing = Path.Combine(folder, "missing.pdf");
        said = null;
        try { new PdfEditSession().Load(missing); }
        catch (PdfEngine.OpenException ex) { said = ex.Message; }
        check("pdf editor: a missing file is refused", said is not null && said.Contains("missing.pdf"), said ?? "opened");
    }

    /// <summary>The pages' labels in order ("A1 B2 _" with _ for a blank page).</summary>
    private static string Labels(PdfEditSession s) =>
        string.Join(" ", s.Pages.Select(p => p.Source < 0 ? "_" : s.SourceName(p.Source).StartsWith("set B", StringComparison.Ordinal) ? $"B{p.Index + 1}" : $"A{p.Index + 1}"));

    /// <summary>The label of each page of a saved file, read with PdfPig ("_" for a page without text).</summary>
    private static string Read(string file)
    {
        using var doc = PdfDocument.Open(file);
        return string.Join(" ", Enumerable.Range(1, doc.NumberOfPages).Select(n =>
        {
            var text = string.Concat(doc.GetPage(n).Letters.Select(l => l.Value)).Trim();
            return text.StartsWith("Page ", StringComparison.Ordinal) ? text.Substring(5) : "_";
        }));
    }

    private static int PageCount(string file)
    {
        using var doc = PdfDocument.Open(file);
        return doc.NumberOfPages;
    }

    /// <summary>A PDF of <paramref name="pages"/> A4 pages (landscape when asked), each with the line "Page {prefix}{n}".</summary>
    internal static byte[] Labeled(string prefix, int pages, bool landscape = false)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var n = 1; n <= pages; n++)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4, !landscape);
            page.AddText($"Page {prefix}{n}", 28, new UglyToad.PdfPig.Core.PdfPoint(60, landscape ? 500 : 760), font);
        }
        return builder.Build();
    }
}
