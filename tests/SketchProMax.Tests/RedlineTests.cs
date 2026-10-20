using System.IO.Compression;
using SketchProMax.Tools;
using static SketchProMax.Tools.Redline;

/// <summary>Redlines in, revised sheets out: reading the markup JSON, the coordinate mapping, picking the element and the report.</summary>
internal static class RedlineTests
{
    public static void Run(Action<string, bool, string> check)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

        // ---- Markup JSON
        var bare = Parse("""[{"id": "A", "type": "text", "text": "NEW NOTE", "bbox": [0.1, 0.2, 0.3, 0.25], "change": {"kind": "edit_text", "from": "OLD", "to": "NEW"}}]""", out var p1);
        check("redlines: a bare list is read", bare is [{ Id: "A", Type: "text", Kind: "edit_text", From: "OLD", To: "NEW", Page: 1 }] && p1.Count == 0 && bare[0].Box!.SequenceEqual([0.1, 0.2, 0.3, 0.25]), string.Join("; ", p1));

        var wrapped = Parse("""
            Here are the markups I found:
            ```json
            {"sheet_number": "S-101", "markups": [
              {"type": "strikethrough", "text": "BEAM B3", "bbox": [30, 40, 10, 45]},
              {"type": "dim", "text": "3,000 mm", "bbox": [0.5, 0.5, 0.6, 0.55], "confidence": 85},
              {"type": "arrow", "text": "Comments: CHECK", "point": [0.7, 0.2], "target": "column C4", "kind": "set_parameter", "parameter": "Comments", "to": "CHECK"},
            ]}
            ```
            """, out var p2);
        check("redlines: JSON in a code fence with prose, sheet number, trailing comma", wrapped.Count == 3 && wrapped.All(m => m.Sheet == "S-101") && p2.Count == 0, string.Join("; ", p2));
        check("redlines: a strike-through is a delete; percentages and reversed corners are fixed",
            wrapped[0] is { Type: "strike", Kind: "delete" } && wrapped[0].Box!.Zip(new[] { 0.1, 0.4, 0.3, 0.45 }, Near).All(x => x), string.Join(",", wrapped[0].Box ?? []));
        check("redlines: a dimension's new value is read in mm, confidence as a fraction",
            wrapped[1] is { Type: "dimension", Kind: "set_dimension", ValueMm: 3000, Mode: "override" } && Near(wrapped[1].Confidence, 0.85), $"{wrapped[1].Kind} {wrapped[1].ValueMm} {wrapped[1].Confidence}");
        check("redlines: change fields may sit on the markup itself", wrapped[2] is { Kind: "set_parameter", Parameter: "Comments", To: "CHECK", Target: "column C4" } && wrapped[2].Point!.SequenceEqual([0.7, 0.2]), wrapped[2].Kind);
        check("redlines: ids given out in order when missing", wrapped.Select(m => m.Id).SequenceEqual(["M1", "M2", "M3"]), string.Join(",", wrapped.Select(m => m.Id)));

        var pages = Parse("""{"pages": [{"page": 2, "sheet_number": "S-201", "markups": [{"id": "X", "type": "cloud", "text": "?", "bbox": [0, 0, 0.1, 0.1]}]}, {"markups": [{"id": "X", "type": "note", "bbox": [0, 0, 0.1, 0.1], "change": {"kind": "add_note", "to": "N"}}]}]}""", out _);
        check("redlines: pages, each with its sheet; a repeated id is renumbered", pages.Count == 2 && pages[0] is { Page: 2, Sheet: "S-201", Kind: "none", Id: "X" } && pages[1] is { Page: 2, Kind: "add_note" } && pages[1].Id != "X",
            string.Join(" | ", pages.Select(m => $"{m.Id} p{m.Page} {m.Sheet} {m.Kind}")));

        var noPlace = Parse("""[{"type": "text", "text": "hello"}, {"type": "text", "bbox": [0.1, 0.2]}, {"type": "text", "bbox": [0.1, 0.2, 1.4, 0.4]}]""", out var p3);
        check("redlines: markups with no place on the sheet are kept and named", noPlace.Count == 3 && noPlace.All(m => m.Box is null) && p3.Count(x => x.Contains("no bbox or point")) == 3
                                                                               && p3.Any(x => x.Contains("needs 4 numbers")) && p3.Any(x => x.Contains("outside the sheet")), string.Join("; ", p3));
        check("redlines: no JSON is a problem, not a crash", Parse("I couldn't read anything.", out var p4).Count == 0 && p4.Count == 1
                                                             && Parse("[{\"type\": ]", out var p5).Count == 0 && p5[0].Contains("can't be read"), string.Join("; ", p4));
        check("redlines: kinds from the usual words", KindOf("Replace text", new()) == "edit_text" && KindOf("remove", new()) == "delete" && KindOf("move", new()) == "set_dimension"
                                                      && KindOf("something else", new()) == "none" && KindOf(null, new() { Type = "cloud" }) == "none", "");
        check("redlines: numbers in dimension text", Number("3,000") == 3000 && Number("2.4 m") == 2400 && Number("450mm") == 450 && Number("TBC") is null, "");
        check("redlines: what each change says", new Markup { Kind = "edit_text", From = "A", To = "B" }.Describe() == "Text 'A' -> 'B'"
                                                  && new Markup { Kind = "set_dimension", Mode = "move", ValueMm = 3000 }.Describe().Contains("3000 mm")
                                                  && new Markup { Kind = "delete" }.Describe().StartsWith("Delete"), new Markup { Kind = "edit_text", From = "A", To = "B" }.Describe());

        // ---- The pages to read, page by page, and the pages read ahead off Revit's thread (the ribbon)
        static bool ThrowsArg(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
        var asked = 0;
        check("redlines: pages given are kept from 1, each once, and the file is not counted",
            Pages([0, 2, 2, 3], () => { asked++; return 9; }).SequenceEqual([2, 3]) && asked == 0, "");
        check("redlines: no pages given reads every page, at most the limit",
            Pages(null, () => 3).SequenceEqual([1, 2, 3]) && Pages(null, () => 50).Count == MaxPages && Pages(null, () => 0).SequenceEqual([1]), "");
        check("redlines: no page left, or too many, is refused",
            ThrowsArg(() => Pages([0, -1], () => 1)) && ThrowsArg(() => Pages(Enumerable.Range(1, MaxPages + 1).ToList(), () => 1)), "");
        var read = new List<Markup>();
        var readProblems = new List<string>();
        AddPage(read, readProblems, """[{"id": "M1", "type": "text", "bbox": [0, 0, 0.1, 0.1]}, {"id": "M2", "type": "text"}]""", 1);
        AddPage(read, readProblems, """[{"id": "M1", "type": "strike", "bbox": [0, 0, 0.1, 0.1]}]""", 2);
        UniqueIds(read);
        check("redlines: each page's markups carry their page; a page's problems are named by page",
            read.Select(m => m.Page).SequenceEqual([1, 1, 2]) && readProblems.Count == 1 && readProblems[0].StartsWith("page 1: M2"), string.Join("; ", readProblems));
        check("redlines: an id repeated on a later page gets its page in front", read.Select(m => m.Id).SequenceEqual(["M1", "M2", "P2-M1"]), string.Join(",", read.Select(m => m.Id)));
        var marked = Path.Combine(Path.GetTempPath(), "Marked up.pdf");
        HandOver(new ReadAhead(marked, [1, 2], read, ["page 2: note"]));
        var taken = TakeRead(marked.ToUpperInvariant(), [1, 2]);
        check("redlines: pages read ahead are taken once, for the same file and pages",
            taken is { Markups.Count: 3 } && taken.Notes.Single() == "page 2: note" && TakeRead(marked, [1, 2]) is null, "");
        HandOver(new ReadAhead(marked, [1, 2], read, []));
        check("redlines: pages read ahead for other pages or another file are not used, and dropped",
            TakeRead(marked, [1]) is null && TakeRead(marked, [1, 2]) is null, "");
        HandOver(new ReadAhead(marked, [1], read, []));
        Discard();
        check("redlines: pages read ahead and discarded can't be picked up later", TakeRead(marked, [1]) is null, "");

        // ---- Coordinates: page fractions (top-left origin) to the sheet (feet, bottom-left origin) and back
        var frame = new LayoutMath.Rect(0.1, 0.2, 2.9, 2.2);
        check("redlines: page top-left is the frame's top-left on the sheet", PageToSheet(0, 0, frame) is var tl && Near(tl.X, 0.1) && Near(tl.Y, 2.2) && PageToSheet(1, 1, frame) is var br && Near(br.X, 2.9) && Near(br.Y, 0.2), PageToSheet(0, 0, frame).ToString());
        var (fx, fy) = SheetToPage(1.5, 1.0, frame);
        var (bx, by) = PageToSheet(fx, fy, frame);
        check("redlines: sheet to page and back", Near(bx, 1.5) && Near(by, 1.0) && Near(fx, 0.5) && Near(fy, 0.6), $"{fx} {fy}");
        var m1 = new Markup { Box = [0.25, 0.25, 0.5, 0.5] };
        var box = BoxOnSheet(m1, frame, 0)!.Value;
        check("redlines: a markup's box on the sheet", Near(box.X0, 0.8) && Near(box.X1, 1.5) && Near(box.Y0, 1.2) && Near(box.Y1, 1.7), box.ToString());
        check("redlines: the anchor is the arrow's tip, else the box middle",
            Anchor(new Markup { Box = [0, 0, 0.5, 0.5], Point = [1, 1] }, frame) is { } tip && Near(tip.X, 2.9) && Near(tip.Y, 0.2) && Anchor(m1, frame) is { } a && Near(a.X, 1.15) && Near(a.Y, 1.45)
            && Anchor(new Markup(), frame) is null && BoxOnSheet(new Markup { Point = [0.5, 0.5] }, frame, 0.1) is { } pb && Near(pb.W, 0.2), "");
        var ports = new List<LayoutMath.Rect> { new(0, 0, 2, 2), new(0.5, 0.5, 1, 1), new(3, 3, 4, 4) };
        check("redlines: the smallest viewport holding the point wins", ViewportAt(0.7, 0.7, ports) == 1 && ViewportAt(1.5, 1.5, ports) == 0 && ViewportAt(5, 5, ports) == -1, "");

        // View (right/up, model feet) to sheet at 1:100 around the viewport centre, unrotated and turned.
        var (sx, sy) = ViewToSheet(110, 50, 10, 50, 1.0, 1.0, 100, 0);
        check("redlines: 100 ft right of the view centre is 1 ft right of the viewport centre at 1:100", Near(sx, 2.0) && Near(sy, 1.0), $"{sx} {sy}");
        var (rx, ry) = ViewToSheet(110, 50, 10, 50, 1.0, 1.0, 100, 90);
        check("redlines: a viewport turned 90 degrees counter-clockwise puts the view's right upwards", Near(rx, 1.0) && Near(ry, 2.0), $"{rx} {ry}");
        var roundTrips = new[] { 0.0, 90, -90, 30 }.All(rot =>
        {
            var (x, y) = ViewToSheet(-37.5, 812.25, 12, -4, 1.3, 0.9, 50, rot);
            var (u, v) = SheetToView(x, y, 12, -4, 1.3, 0.9, 50, rot);
            return Near(u, -37.5) && Near(v, 812.25);
        });
        check("redlines: sheet to view and back at any rotation", roundTrips, "");

        // ---- Which element
        var mm = 1 / 304.8;
        var beam = new Candidate(1, "Structural Framing 1", new(1.0, 1.0, 1.1, 1.02), 0);
        var slab = new Candidate(2, "Floors 2", new(0.5, 0.5, 2.0, 2.0), 0);
        var column = new Candidate(3, "Structural Columns 3", new(1.1 + 45 * mm, 1.0, 1.1 + 48 * mm, 1.01), 0);
        check("redlines: the nearer of two", Pick([beam, column], At(1.1 + 40 * mm, 1.005), 50 * mm) is ({ Id: 3 }, null), "");
        check("redlines: nothing within the radius", Pick([beam], At(2.5, 2.5), 20 * mm) is (null, { } why) && why.Contains("close enough"), "");
        check("redlines: the markup's words beat distance", Pick([beam, column with { Hint = 1 }], At(1.05, 1.01), 80 * mm) is ({ Id: 3 }, null), "");
        check("redlines: of two under the markup, the clearly smaller", Pick([slab, beam], At(1.05, 1.01), 20 * mm) is ({ Id: 1 }, null), "");
        var twin = beam with { Id = 4, Label = "Structural Framing 4", Box = beam.Box.Move(0, 0.005) };
        check("redlines: two alike under the markup are for the user to choose", Pick([beam, twin], At(1.05, 1.01), 20 * mm) is (null, { } w2) && w2.Contains("Framing 1") && w2.Contains("Framing 4"), "");
        var left = new Candidate(5, "Walls 5", new(1.0 - 10 * mm, 1, 1.0 - 9 * mm, 1.1), 0);
        var right = new Candidate(6, "Walls 6", new(1.0 + 9 * mm, 1, 1.0 + 10 * mm, 1.1), 0);
        check("redlines: two equally close are for the user to choose", Pick([left, right], At(1.0, 1.05), 20 * mm) is (null, { } w3) && w3.Contains("equally close"), "");
        check("redlines: a markup box touching an element is distance 0", Distance(new(0, 0, 1, 1), new(1, 0.5, 2, 2)) == 0 && Near(Distance(new(0, 0, 1, 1), new(4, 5, 6, 6)), 5), "");
        check("redlines: word matching", Near(HintScore("beam B12", null, "Structural Framing", "B12", "UB 305x165x40"), 0.5) && Near(HintScore("the note", "old note", "Text Notes", "OLD  NOTE here"), 2)
                                         && HintScore("", null, "Walls") == 0 && HintScore("column C4", null, "Structural Columns", "C4") >= 1, HintScore("beam B12", null, "Structural Framing", "B12").ToString());
        check("redlines: a move goes towards or away from the fixed end", Near(MoveAlong(10, 12, 5, 0), 2) && Near(MoveAlong(10, 12, -5, 0), -2) && Near(MoveAlong(10, 9, 5, 0), -1), "");
        check("redlines: a text edit replaces the part named, else the whole", EditText("BEAM B3 TO BE 300 DEEP", "300", "450") == "BEAM B3 TO BE 450 DEEP"
                                                                               && EditText("ALL DIMS IN MM", "all dims", "NOTE: ALL DIMS") == "NOTE: ALL DIMS IN MM" && EditText("X", "Y", "Z") == "Z", "");

        // ---- The report
        var lines = new List<Line>
        {
            new("M1", 1, "S-101", "text", "NEW", "Text 'OLD' -> 'NEW'", "sheet S-101, view 'Level 1', Text Notes 12", "done", ""),
            new("M2", 1, "S-101", "cloud", "check", "No change read: check", "sheet S-101", "handed over", "no change could be read from it"),
            new("M3", 1, "S-101", "strike", "B3", "Delete (struck through)", "sheet S-101", "skipped", "you skipped it"),
        };
        check("redlines: summary counts done and not done with why", Summary(lines) == "1 done, 2 not done (1 handed over, 1 skipped)", Summary(lines));
        var md = Markdown(lines);
        check("redlines: chat report table", md.Contains("**Redlines: 1 done, 2 not done") && md.Contains("| M1 | S-101 | Text 'OLD' -> 'NEW' | done |")
                                              && md.Contains("| M2 | S-101 |") && md.Contains("not done: handed over, no change could be read from it"), md);
        check("redlines: a long report is cut with a pointer to Excel", Markdown(Enumerable.Range(1, 50).Select(i => lines[0] with { Id = "M" + i }).ToList(), 40).Contains("10 more in the Excel report"), "");
        var book = Workbook(lines, "marked.pdf", "3 Redlines", ["S-101 - Plan - 3.pdf"]);
        check("redlines: Excel has every markup and a summary", book.Count == 2 && book[0].Rows.Count == 4 && (string?)book[0].Rows[1][7] == "done" && (string?)book[0].Rows[2][7] == "not done"
                                                                && book[1].Rows.Any(r => (string?)r[0] == "Revised sheet" && (string?)r[1] == "S-101 - Plan - 3.pdf"), "");
        var file = Path.Combine(Path.GetTempPath(), "spm-redlines-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            Xlsx.Write(file, book);
            using var zip = ZipFile.OpenRead(file);
            var workbook = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open()).ReadToEnd();
            check("redlines: the Excel report writes", workbook.Contains("Markups") && workbook.Contains("Summary") && zip.GetEntry("xl/worksheets/sheet1.xml") is not null, workbook.Length.ToString());
        }
        finally { try { File.Delete(file); } catch (IOException) { } }

        check("redlines: the reading prompt names the format and the coordinates", VisionMessage(@"C:\x\page-1.png", 1).Contains(@"C:\x\page-1.png") && Format.Contains("\"bbox\"")
                                                                                     && Format.Contains("top-left") && Kinds.All(k => Format.Contains(k)), "");
    }
}
