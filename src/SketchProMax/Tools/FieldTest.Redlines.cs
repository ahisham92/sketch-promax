using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// The field test's redlines probes (keys in FieldTestCoverage, called from Features): a sheet exported to PDF and read
/// back as an image; a test sheet with markups drawn onto its image run through redline_apply (the markup JSON comes
/// from the test hook, so no Claude plan is used); and, with chat_round_trip only, the real Claude reading of that image.
/// Everything happens in the test project inside a transaction group that is rolled back.
/// </summary>
internal static partial class FieldTest
{
    /// <summary>The marked-up page the pipeline probe drew, for the vision probe (null until it ran).</summary>
    private static string? _redlinePage;

    private sealed record RedlineSet(ViewSheet Sheet, ViewPlan Plan, Wall Wall, TextNote Old, TextNote Struck, XYZ Origin);

    /// <summary>In the probe's group: a plan (a copy of one, cropped round a wall and two text notes far from the model) on a new sheet.</summary>
    private static RedlineSet? RedlineSetup(Document doc, out string why)
    {
        why = "";
        var source = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel is not null && v.CanViewBeDuplicated(ViewDuplicateOption.Duplicate));
        if (source is null) { why = "the test project has no floor plan to put on a sheet"; return null; }
        using var t = new Transaction(doc, "Field test: a sheet to mark up");
        t.Start();
        var plan = (ViewPlan)doc.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate));
        plan.Name = "Field test redlines " + Guid.NewGuid().ToString("N").Substring(0, 4);
        plan.ViewTemplateId = ElementId.InvalidElementId; // a template could hold the scale
        plan.Scale = 100;
        var level = plan.GenLevel!;
        var o = new XYZ(400, 400, level.ProjectElevation);
        var wall = Wall.Create(doc, Line.CreateBound(o, o + new XYZ(30, 0, 0)), level.Id, false);
        var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var old = TextNote.Create(doc, plan.Id, o + new XYZ(0, 15, 0), "OLD NOTE", textType);
        var struck = TextNote.Create(doc, plan.Id, o + new XYZ(0, -15, 0), "DELETE ME", textType);
        var crop = plan.CropBox;
        var inverse = crop.Transform.Inverse;
        var a = inverse.OfPoint(o + new XYZ(-10, -30, 0));
        var b = inverse.OfPoint(o + new XYZ(45, 30, 0));
        plan.CropBox = new BoundingBoxXYZ
        {
            Transform = crop.Transform,
            Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), crop.Min.Z),
            Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), crop.Max.Z),
        };
        plan.CropBoxActive = true;
        plan.CropBoxVisible = false;
        var titleBlock = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().FirstElementId();
        var sheet = ViewSheet.Create(doc, titleBlock ?? ElementId.InvalidElementId);
        sheet.SheetNumber = "RL-" + Guid.NewGuid().ToString("N").Substring(0, 5).ToUpperInvariant();
        sheet.Name = "Field test redlines";
        doc.Regenerate();
        var frame = Redlines.Frame(sheet);
        var center = frame.W > 0.5 ? new XYZ(frame.Cx, frame.Cy, 0) : new XYZ(1.2, 0.8, 0);
        Viewport.Create(doc, sheet.Id, plan.Id, center);
        t.Commit();
        return new RedlineSet(sheet, plan, wall, old, struck, o);
    }

    /// <summary>The markups drawn on the test sheet, as Claude would read them: page fractions from where the elements are.</summary>
    private static List<object> RedlineMarkups(RedlineSet r, out LayoutMath.Rect frame)
    {
        var f = frame = Redlines.Frame(r.Sheet);
        var port = Redlines.Ports(r.Sheet).Single();
        double[] Box(LayoutMath.Rect s)
        {
            var (x0, y0) = Redline.SheetToPage(s.X0, s.Y1, f);
            var (x1, y1) = Redline.SheetToPage(s.X1, s.Y0, f);
            return [x0, y0, x1, y1];
        }
        LayoutMath.Rect Of(Element e)
        {
            var pts = ViewKit.Corners(e.get_BoundingBox(r.Plan)!).Select(port.ToSheet).ToList();
            return new LayoutMath.Rect(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
        }
        LayoutMath.Rect Around(XYZ model, double halfMm)
        {
            var (x, y) = port.ToSheet(model);
            var h = halfMm / 304.8;
            return new LayoutMath.Rect(x - h, y - h, x + h, y + h);
        }
        var (wx, wy) = port.ToSheet(r.Origin + new XYZ(15, 0, 0));
        var wallTip = Redline.SheetToPage(wx, wy, f);
        var struck = Of(r.Struck);
        return
        [
            new { id = "M1", type = "text", text = "NEW NOTE", bbox = Box(Of(r.Old).Grow(1 / 304.8)), target = "the note OLD NOTE", change = new { kind = "edit_text", from = "OLD NOTE", to = "NEW NOTE" } },
            new { id = "M2", type = "arrow", text = "Comments: REDLINE TEST", bbox = Box(Around(r.Origin + new XYZ(15, 8, 0), 4)), point = new[] { wallTip.Fx, wallTip.Fy }, target = "wall", change = new { kind = "set_parameter", parameter = "Comments", to = "REDLINE TEST" } },
            new { id = "M3", type = "strike", text = "DELETE ME", bbox = Box(new LayoutMath.Rect(struck.X0, struck.Cy - 0.5 / 304.8, struck.X1, struck.Cy + 0.5 / 304.8)), target = "the note DELETE ME", change = new { kind = "delete" } },
            new { id = "M4", type = "note", text = "ADDED BY REDLINE", bbox = Box(Around(r.Origin + new XYZ(20, -25, 0), 3)), change = new { kind = "add_note", to = "ADDED BY REDLINE" } },
            new { id = "M5", type = "cloud", text = "check this area", bbox = Box(Around(r.Origin + new XYZ(38, 20, 0), 5)), change = new { kind = "none" } },
        ];
    }

    /// <summary>The test sheet exported to an image (Revit draws the sheet for it, so where things are on it is settled after).</summary>
    private static string RedlineSheetImage(Document doc, RedlineSet r, string folder)
    {
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(folder, "sheet"), ZoomType = ZoomFitType.FitToPage, PixelSize = 2000,
            HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG, ImageResolution = ImageResolution.DPI_150,
        };
        options.SetViewsAndSheets([r.Sheet.Id]);
        var before = new HashSet<string>(Directory.GetFiles(folder, "*.png"), StringComparer.OrdinalIgnoreCase);
        doc.ExportImage(options);
        return Directory.GetFiles(folder, "*.png").FirstOrDefault(f => !before.Contains(f)) ?? throw new InvalidOperationException("Revit exported no image of the test sheet");
    }

    /// <summary>What the probe worked out, short: the sheet frame (mm), whether it has a title block, and M1's box as page fractions.</summary>
    private static string RedlineFrameNote(RedlineSet r, LayoutMath.Rect f, List<object> markups)
    {
        static string Mm(double ft) => (ft * 304.8).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        var titleBlock = new FilteredElementCollector(r.Sheet.Document, r.Sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().GetElementCount() > 0;
        var box = JsonSerializer.SerializeToElement(markups[0]).GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var outside = box.Any(v => v < 0 || v > 1) ? " (outside the page: the tool reads such numbers as percentages)" : "";
        return $"probe: frame x {Mm(f.X0)}..{Mm(f.X1)}, y {Mm(f.Y0)}..{Mm(f.Y1)} mm, title block {(titleBlock ? "yes" : "no")}; " +
               $"M1 box {string.Join(" ", box.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}{outside}";
    }

    /// <summary>The sheet's image with the markups drawn on in red, as a reviewer would.</summary>
    private static string RedlinePageImage(string exported, List<object> markups, string folder)
    {
        System.Windows.Media.Imaging.BitmapSource page;
        using (var input = File.OpenRead(exported))
            page = System.Windows.Media.Imaging.BitmapDecoder.Create(input, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        int w = page.PixelWidth, h = page.PixelHeight;
        var red = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 0, 0));
        var pen = new System.Windows.Media.Pen(red, Math.Max(2, w / 600.0));
        var face = new System.Windows.Media.Typeface("Segoe Print");
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(page, new System.Windows.Rect(0, 0, w, h));
            foreach (var m in markups.Select(x => JsonSerializer.SerializeToElement(x)))
            {
                var b = m.GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                var box = new System.Windows.Rect(b[0] * w, b[1] * h, Math.Max(2, (b[2] - b[0]) * w), Math.Max(2, (b[3] - b[1]) * h));
                var text = m.GetProperty("text").GetString() ?? "";
                var label = new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    face, Math.Max(14, h / 70.0), red, 1.0);
                switch (m.GetProperty("type").GetString())
                {
                    case "strike":
                        dc.DrawLine(pen, new System.Windows.Point(box.Left - 4, box.Top + box.Height / 2), new System.Windows.Point(box.Right + 4, box.Top + box.Height / 2));
                        break;
                    case "cloud":
                        // Scallops along the box's edges.
                        var r0 = Math.Max(6, Math.Min(box.Width, box.Height) / 6);
                        for (var x = box.Left; x <= box.Right; x += r0 * 1.6) { dc.DrawEllipse(null, pen, new System.Windows.Point(x, box.Top), r0, r0); dc.DrawEllipse(null, pen, new System.Windows.Point(x, box.Bottom), r0, r0); }
                        for (var y = box.Top; y <= box.Bottom; y += r0 * 1.6) { dc.DrawEllipse(null, pen, new System.Windows.Point(box.Left, y), r0, r0); dc.DrawEllipse(null, pen, new System.Windows.Point(box.Right, y), r0, r0); }
                        dc.DrawText(label, new System.Windows.Point(box.Right + r0 * 2, box.Top));
                        break;
                    case "arrow":
                        var tip = m.GetProperty("point").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                        var to = new System.Windows.Point(tip[0] * w, tip[1] * h);
                        dc.DrawLine(pen, new System.Windows.Point(box.Left, box.Bottom), to);
                        dc.DrawEllipse(red, null, to, pen.Thickness * 2, pen.Thickness * 2);
                        dc.DrawText(label, new System.Windows.Point(box.Left, box.Top - label.Height));
                        break;
                    case "text":
                        dc.DrawRectangle(null, pen, box);
                        dc.DrawText(label, new System.Windows.Point(box.Right + 8, box.Top - label.Height));
                        break;
                    default:
                        dc.DrawText(label, new System.Windows.Point(box.Left, box.Top));
                        break;
                }
            }
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var path = Path.Combine(folder, "marked-up sheet.png");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var output = File.Create(path)) encoder.Save(output);
        return path;
    }

    /// <summary>A sheet exported to PDF by export_pdf, read back by redline_read: Revit rasterises the page into an image.</summary>
    private static (string, string) RedlinePdf(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var folder = Path.Combine(s.Folder, "redlines-pdf");
        Directory.CreateDirectory(folder);
        using var group = new TransactionGroup(doc, "Field test: redline PDF");
        group.Start();
        try
        {
            if (RedlineSetup(doc, out why) is not { } r) return Fail(why);
            var export = Call(app, s.Registry, "export_pdf", new { view_ids = new[] { r.Sheet.Id.Value }, folder, file_name = "redlines page" });
            var pdf = Path.Combine(folder, "redlines page.pdf");
            if (export.IsError || !File.Exists(pdf)) return Fail("the test sheet was not exported to PDF: " + Short(export.Text, 300));
            var read = Call(app, s.Registry, "redline_read", new { file = pdf, page = 1, pixel_width = 1200 });
            if (read.IsError) return Fail(Short(read.Text, 400));
            if (read.PngImage is not { Length: > 1000 } png) return Fail("redline_read returned no image of the PDF page");
            using var json = JsonDocument.Parse(read.Text);
            // redline_read keeps its image for the chat; the probe's goes now (it is outside the test folder).
            if (json.RootElement.TryGetProperty("image", out var image) && image.GetString() is { } shown && Path.GetDirectoryName(shown) is { } dir)
                Redlines.Remove(dir);
            var pages = json.RootElement.GetProperty("pagesFound").GetInt32();
            return Pass($"export_pdf wrote the test sheet; redline_read rasterised page 1 of {pages} through Revit into a {png.Length / 1024} KB PNG, ready to be read");
        }
        finally { RollBack(group); }
    }

    /// <summary>
    /// The whole pipeline on a marked-up image of a test sheet: the known markup JSON stands in for Claude's reading
    /// (Redlines.TestExtract), every change is asked one by one (review_each) and approved (Redlines.TestApprove) and the one markup with no instruction is
    /// skipped when handed over. Checks the mapping, the changes, clouds and revision, the PDF and the report.
    /// </summary>
    private static (string, string) RedlinePipeline(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var folder = Path.Combine(s.Folder, "redlines");
        Directory.CreateDirectory(folder);
        using var group = new TransactionGroup(doc, "Field test: redlines");
        group.Start();
        try
        {
            if (RedlineSetup(doc, out why) is not { } r) return Fail(why);
            // The markups are worked out after Revit has drawn the sheet for the image (as the tool sees it); if the sheet's
            // mapping moved when it was drawn, the failure says so.
            var early = JsonSerializer.Serialize(RedlineMarkups(r, out _));
            var sheetImage = RedlineSheetImage(doc, r, folder);
            var markups = RedlineMarkups(r, out var frame);
            var moved = early == JsonSerializer.Serialize(markups) ? null
                : $"the sheet's mapping changed once Revit drew it (frame now {frame}; markups before: {Short(early, 160)})";
            // Ids now: the struck-through note is deleted by the run, and a deleted element can't be read.
            long oldId = r.Old.Id.Value, wallId = r.Wall.Id.Value, struckId = r.Struck.Id.Value;
            var page = RedlinePageImage(sheetImage, markups, folder);
            _redlinePage = page;
            var json = JsonSerializer.Serialize(new { sheet_number = r.Sheet.SheetNumber, markups });
            var asked = new List<string>();
            Redlines.TestExtract = (_, _) => json;
            Redlines.TestApprove = p => { asked.Add(p.Markup.Id); return true; };
            Handoff.TestResponder = (_, _) => HandoffChoice.Skip;
            var result = CallHandingOff(app, s.Registry, "redline_apply", new { file = page, folder, revision_description = "Field test redlines", review_each = true });
            if (result.IsError) return Fail(Short(result.Text, 600));

            using var parsed = JsonDocument.Parse(result.Text);
            var root = parsed.RootElement;
            var lines = root.GetProperty("markups").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.Clone());
            string Where(string id) => lines.TryGetValue(id, out var l) ? l.GetProperty("where").GetString() ?? "" : "";
            string Note(string id) => lines.TryGetValue(id, out var l) && l.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
            bool Done(string id) => lines.TryGetValue(id, out var l) && l.GetProperty("done").GetBoolean();
            var problems = new List<string>();

            // Mapping: the right viewport's view and element for each markup.
            foreach (var (id, element) in new[] { ("M1", oldId), ("M2", wallId), ("M3", struckId) })
                if (!Where(id).Contains($"'{r.Plan.Name}'") || !Where(id).Contains(element.ToString()))
                    problems.Add($"{id} was placed at \"{Where(id)}\", not on element {element} in '{r.Plan.Name}' ({Short(Note(id), 160)})");
            // What the probe itself worked out, so a mapping failure can be compared with the tool's distances above.
            // First, so the report's cut-off detail keeps it.
            if (problems.Count > 0) problems.Insert(0, RedlineFrameNote(r, frame, markups));
            if (!asked.SequenceEqual(["M1", "M2", "M3", "M4"])) problems.Add("approval was asked for " + (asked.Count == 0 ? "nothing" : string.Join(", ", asked)) + ", not M1 to M4 one by one");

            // The changes.
            var oldNote = doc.GetElement(new ElementId(oldId)) as TextNote;
            if (oldNote?.Text.Contains("NEW NOTE") != true) problems.Add($"the note still reads \"{oldNote?.Text.Trim()}\"");
            var comments = doc.GetElement(new ElementId(wallId))?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            if (comments != "REDLINE TEST") problems.Add($"the wall's Comments are \"{comments}\"");
            if (doc.GetElement(new ElementId(struckId)) is not null) problems.Add("the struck-through note was not deleted");
            if (!new FilteredElementCollector(doc, r.Plan.Id).OfClass(typeof(TextNote)).Cast<TextNote>().Any(n => n.Text.Contains("ADDED BY REDLINE")))
                problems.Add("the new note was not added to the plan");
            foreach (var id in new[] { "M1", "M2", "M3", "M4" }) if (!Done(id)) problems.Add($"{id} is not reported done");
            if (Done("M5") || lines.TryGetValue("M5", out var m5) && m5.GetProperty("status").GetString() != "handed over")
                problems.Add("the cloud with no instruction was not reported as not done and handed over");

            // Clouds, revision, PDF, report.
            var clouds = new FilteredElementCollector(doc, r.Sheet.Id).OfClass(typeof(RevisionCloud)).GetElementCount();
            if (clouds < 4) problems.Add($"{clouds} revision cloud(s) on the sheet, not 4");
            var revision = root.TryGetProperty("revision", out var rev) && rev.ValueKind == JsonValueKind.Object ? rev.GetProperty("id").GetInt64() : -1;
            if (revision < 0 || !r.Sheet.GetAllRevisionIds().Any(i => i.Value == revision)) problems.Add("the new revision is not on the sheet");
            var pdfs = root.GetProperty("exported").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            var exportFolder = root.TryGetProperty("exportFolder", out var ef) ? ef.GetString() : null;
            if (pdfs.Count != 1 || exportFolder is null || !File.Exists(Path.Combine(exportFolder, pdfs[0]))) problems.Add("the revised sheet was not exported: " + (root.TryGetProperty("exportProblem", out var ep) ? ep.ToString() : ""));
            var report = root.TryGetProperty("report", out var rp) ? rp.GetString() : null;
            if (report is null || !File.Exists(report)) problems.Add("no Excel report");
            var chat = root.GetProperty("chatReport").GetString() ?? "";
            if (!chat.Contains("4 done, 1 not done") || !chat.Contains("| M5 |")) problems.Add("the chat report doesn't say 4 done, 1 not done: " + Short(chat, 200));
            var steps = HandoffSteps(result.Text);
            if (steps is not [("skipped", false)]) problems.Add("the hand-off for the cloud says " + Steps(steps));

            if (problems.Count > 0 && moved is not null) problems.Add(moved);
            return problems.Count == 0
                ? Pass($"5 markups drawn on the test sheet's image ({Path.GetFileName(page)}): the text edit, parameter change, strike-through and new note were matched to " +
                       $"'{r.Plan.Name}' and their elements, approved one by one, applied and clouded ({clouds} clouds) under the new revision; {pdfs[0]} exported; " +
                       $"the cloud with no instruction was handed over (skipped); the Excel report and the chat table say 4 done, 1 not done")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            Redlines.TestExtract = null;
            Redlines.TestApprove = null;
            Handoff.TestResponder = null;
            RollBack(group);
        }
    }

    /// <summary>With chat_round_trip: Claude Code reads the marked-up test page for real (it uses the user's plan).</summary>
    private static (string, string) RedlineVision(State s, ClaudeCliInstall? cli)
    {
        if (!s.O.ChatRoundTrip)
            return ("manual", "manual (checklist): off by default because reading the page uses your plan; the checklist's \"Redlines from a real PDF or photo\" step covers it. Run with chat_round_trip true to test it here.");
        if (_redlinePage is not { } page || !File.Exists(page)) return Fail("redlines.pipeline made no marked-up page to read");
        if (cli?.Install is not { } install) return Fail("Claude Code is not installed (see chat.cli)");
        var text = Redlines.RunVision(install, page, 1, 240_000, out var error);
        if (text is null) return Fail("The page was not read: " + Short(error, 300));
        var found = Redline.Parse(text, out var problems);
        var kinds = string.Join(", ", found.Select(m => $"{m.Id} {m.Type}/{m.Kind}"));
        return found.Count >= 3 && found.Any(m => m.Kind == "delete")
            ? Pass($"Read {found.Count} markups from the page: {kinds}" + (problems.Count > 0 ? $" ({problems.Count} note(s): {Short(problems[0], 100)})" : ""))
            : Fail($"Read {found.Count} markup(s) ({kinds}), expected the 5 drawn, including the strike-through" + (problems.Count > 0 ? ": " + Short(string.Join("; ", problems), 200) : ""));
    }
}
