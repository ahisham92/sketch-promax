using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Chat;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Redlines in, revised sheets out, on the Revit side: a marked-up PDF page or photo as an image, the markups read from
/// it (by Claude in the chat, or by a one-shot Claude Code run from the ribbon), each one placed on its sheet, in a
/// viewport's view and on an element, then applied one approved change at a time with a revision cloud on the sheet,
/// the revision added and the revised sheets exported. What it can't place or apply goes to the user as a hand-off; the
/// report lists every markup as done or not done, in Excel and in the chat. The arithmetic is in <see cref="Redline"/>.
/// </summary>
internal static class Redlines
{
    /// <summary>The field test's hook: the markup JSON for a page image (path, page) instead of asking Claude. Null otherwise.</summary>
    internal static Func<string, int, string?>? TestExtract;

    /// <summary>The field test's hook: answers each change's approval (true apply, false skip, null stop). Null asks in Revit.</summary>
    internal static Func<Planned, bool?>? TestApprove;

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif"];

    /// <summary>Pages read from one PDF at most (each is a Claude reading when nothing else gives the markups).</summary>
    public const int MaxPages = 20;

    /// <summary>How far from a markup (on paper) an element can be and still be the one it means.</summary>
    public const double RadiusMm = 20;

    /// <summary>One markup worked out against the model: where it is and what it would change, or why it can't be done.</summary>
    public sealed class Planned
    {
        public Redline.Markup Markup { get; init; } = new();
        public ViewSheet? Sheet { get; set; }
        /// <summary>The view the change happens in: a viewport's view, or the sheet itself.</summary>
        public View? View { get; set; }
        public Element? Element { get; set; }
        /// <summary>add_note: where the note goes, in <see cref="View"/>.</summary>
        public XYZ? At { get; set; }
        /// <summary>Where the revision cloud goes, on the sheet.</summary>
        public LayoutMath.Rect? Cloud { get; set; }
        public int Segment { get; set; } = -1;
        public ElementId? MoveId { get; set; }
        public XYZ? MoveBy { get; set; }
        /// <summary>Why it can't be applied; null when it is ready.</summary>
        public string? Problem { get; set; }

        public string Where
        {
            get
            {
                var parts = new List<string>();
                if (Sheet is not null) parts.Add("sheet " + Sheet.SheetNumber);
                if (View is not null && View is not ViewSheet) parts.Add($"view '{View.Name}'");
                if (Element is not null) parts.Add($"{Element.Category?.Name ?? Element.GetType().Name} {Element.Id.Value}");
                return string.Join(", ", parts);
            }
        }
    }

    // ---- The page images ------------------------------------------------------------------------------------------------

    /// <summary>A folder for this run's page images, where the one-shot Claude Code run may read them.</summary>
    public static string WorkFolder()
    {
        var folder = Path.Combine(ClaudeCli.WorkFolder, "redlines", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static bool IsPdf(string file) => Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static string CheckedFile(string? file)
    {
        var path = (file ?? "").Trim().Trim('"');
        if (path.Length == 0) throw new ArgumentException("Give file: the marked-up PDF, or a photo or image of the markups (PNG or JPG).");
        if (!File.Exists(path)) throw new ArgumentException($"{path} was not found.");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".pdf" && !ImageExtensions.Contains(ext))
            throw new ArgumentException($"{Path.GetFileName(path)} is not a PDF or an image. Export the marked-up pages to PDF or PNG first.");
        return path;
    }

    /// <summary>The pages of a PDF, counted from its page objects (1 when they can't be counted, e.g. compressed object streams).</summary>
    public static int PdfPages(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 200L * 1024 * 1024) return 1;
            var text = Encoding.GetEncoding("ISO-8859-1").GetString(File.ReadAllBytes(file));
            return Math.Max(1, Regex.Matches(text, @"/Type\s*/Page(?![a-zA-Z])").Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
    }

    /// <summary>
    /// A PNG of one page: an image is used as it is (scaled down when large); a PDF page is rasterised by Revit itself (an
    /// image type made from the page in a transaction that is rolled back), so no PDF library is needed. Call with no
    /// transaction open.
    /// </summary>
    public static string PageImage(Document doc, string file, int page, string folder, int maxPixels = 2400)
    {
        var png = Path.Combine(folder, $"page-{page}.png");
        if (!IsPdf(file))
        {
            if (page != 1) throw new ArgumentException("An image has one page; leave pages out.");
            File.WriteAllBytes(png, Scaled(file, maxPixels));
            return png;
        }
        if (doc.IsModifiable) throw new InvalidOperationException("Reading a PDF page needs no transaction open.");
        var raw = Path.Combine(folder, $"page-{page}-raw.png");
        using (var t = new Transaction(doc, "Sketch ProMax: read PDF page"))
        {
            t.Start();
            try
            {
                var options = new ImageTypeOptions(file, false, ImageTypeSource.Import) { PageNumber = page, Resolution = 100 };
                var type = ImageType.Create(doc, options);
                // GetImage gives a System.Drawing bitmap; called by reflection so the add-in needs no System.Drawing.Common
                // reference on .NET 8 and later (Revit supplies it). Save(path) writes a PNG for an in-memory bitmap.
                var bitmap = typeof(ImageType).GetMethod("GetImage")!.Invoke(type, null) ?? throw new InvalidOperationException("Revit gave no image.");
                try { bitmap.GetType().GetMethod("Save", [typeof(string)])!.Invoke(bitmap, [raw]); }
                finally { (bitmap as IDisposable)?.Dispose(); }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException or System.Reflection.TargetInvocationException)
            {
                var why = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
                throw new ArgumentException($"Revit could not read page {page} of {Path.GetFileName(file)} ({why}). " +
                                            "Export the marked-up page to PNG (any PDF viewer can) and give that file instead.");
            }
            finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
        }
        File.WriteAllBytes(png, Scaled(raw, maxPixels));
        try { File.Delete(raw); } catch (IOException) { }
        return png;
    }

    /// <summary>An image file as a PNG whose longest side is at most <paramref name="maxPixels"/>.</summary>
    public static byte[] Scaled(string path, int maxPixels)
    {
        using var input = File.OpenRead(path);
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(input, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        System.Windows.Media.Imaging.BitmapSource image = frame;
        var k = maxPixels / (double)Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (k < 1) image = new System.Windows.Media.Imaging.TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(k, k));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    // ---- Reading the markups ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The markups: the "markups" argument when given (Claude read the page in the chat), else each page read by a one-shot
    /// Claude Code run (from the ribbon), or by the field test's hook. <paramref name="source"/> names where they came from.
    /// </summary>
    public static List<Redline.Markup> Load(Document doc, ToolArgs args, List<string> notes, out string source)
    {
        var problems = new List<string>();
        List<Redline.Markup> markups;
        var file = args.OptString("file") is { Length: > 0 } f ? CheckedFile(f) : null;
        if (args.Has("markups"))
        {
            var raw = args.Raw["markups"];
            markups = raw.ValueKind == JsonValueKind.String ? Redline.Parse(raw.GetString(), out problems) : Redline.Parse(raw, problems);
            source = file is null ? "markups given" : Path.GetFileName(file);
        }
        else
        {
            if (file is null) throw new ArgumentException("Give file (the marked-up PDF or image), or markups read from it (see redline_read).");
            source = Path.GetFileName(file);
            var pages = args.Has("pages") ? args.Array("pages").Select(p => p.GetInt32()).Where(p => p >= 1).Distinct().ToList()
                : Enumerable.Range(1, IsPdf(file) ? Math.Min(MaxPages, PdfPages(file)) : 1).ToList();
            if (pages.Count == 0) throw new ArgumentException("pages lists no page (pages count from 1).");
            if (pages.Count > MaxPages) throw new ArgumentException($"At most {MaxPages} pages at a time; give pages.");
            var folder = WorkFolder();
            markups = [];
            for (var i = 0; i < pages.Count; i++)
            {
                if (CancelPoint.Stop("Reading redlines", i, pages.Count)) { notes.Add(CancelPoint.Note(true, i, pages.Count)!); break; }
                var page = pages[i];
                string png;
                try { png = PageImage(doc, file, page, folder); }
                catch (ArgumentException ex) when (i > 0 && IsPdf(file)) { notes.Add($"page {page}: {ex.Message}"); break; } // past the last page
                var answer = Extract(png, page, out var error);
                if (answer is null) { notes.Add($"page {page}: the markups could not be read ({error})"); continue; }
                var found = Redline.Parse(answer, out var pageProblems);
                foreach (var m in found) m.Page = page;
                problems.AddRange(pageProblems.Select(p => $"page {page}: {p}"));
                markups.AddRange(found);
            }
            // Ids unique across pages.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in markups) if (!seen.Add(m.Id)) { m.Id = $"P{m.Page}-{m.Id}"; seen.Add(m.Id); }
        }
        notes.AddRange(problems);
        if (args.StringList("markup_ids") is { Count: > 0 } only)
        {
            var wanted = new HashSet<string>(only.Select(o => o.Trim()), StringComparer.OrdinalIgnoreCase);
            markups = markups.Where(m => wanted.Contains(m.Id)).ToList();
        }
        return markups;
    }

    /// <summary>The markup JSON for one page image: the field test's hook, else a one-shot Claude Code run. Null with the error when it can't.</summary>
    public static string? Extract(string png, int page, out string error)
    {
        error = "";
        if (TestExtract is { } hook)
        {
            var given = hook(png, page);
            if (given is null) error = "the test gave no markups";
            return given;
        }
        var install = ClaudeCli.Find();
        if (install is null)
        {
            error = "Claude Code was not found on this PC. Open Chat on the Sketch ProMax tab to install it, or ask in the chat (Claude reads the page there)";
            return null;
        }
        return RunVision(install, png, page, 240_000, out error);
    }

    /// <summary>
    /// One Claude Code run that reads the page image: print mode, Read only (for the image), no MCP server, nothing that
    /// could act on the PC or the model. Blocks until it answers, Cancel is pressed or the timeout passes.
    /// </summary>
    internal static string? RunVision(ClaudeCli.Install install, string png, int page, int timeoutMs, out string error)
    {
        error = "";
        var engine = Path.Combine(ClaudeCli.Folder, "engine");
        Directory.CreateDirectory(engine);
        Directory.CreateDirectory(ClaudeCli.WorkFolder);
        var promptFile = Path.Combine(engine, "redlines-" + Process.GetCurrentProcess().Id + ".md");
        File.WriteAllText(promptFile, "You read markups (redlines) on structural drawings and answer with JSON only. Use no tool but Read.", new UTF8Encoding(false));
        var args = new List<string>
        {
            "-p", "--output-format", "json", "--append-system-prompt-file", promptFile, "--tools", "Read", "--allowedTools", "Read",
            "--strict-mcp-config", "--permission-mode", "dontAsk", "--max-turns", "4",
        };
        var command = string.Join(" ", args.Select(ClaudeCli.Quote));
        var info = install.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + ClaudeCli.Quote(install.Path) + " " + command + "\"")
            : new ProcessStartInfo(install.Path, command);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.WorkingDirectory = ClaudeCli.WorkFolder;
        // It runs on the user's Claude plan, like the chat.
        info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
        Process? process = null;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Claude Code didn't start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                input.Write(Redline.VisionMessage(png, page));
            var watch = Stopwatch.StartNew();
            while (!stdout.Wait(500))
            {
                if (Jobs.CancelRequested) { ClaudeCli.KillTree(process); throw new OperationCanceledException(CancelPoint.Stopped + "."); }
                if (watch.ElapsedMilliseconds > timeoutMs) { ClaudeCli.KillTree(process); error = "Claude took too long to read the page"; return null; }
            }
            var (text, isError) = PromptBriefs.ResultOf(stdout.Result);
            if (isError || text.Length == 0)
            {
                var why = (text.Length > 0 ? text : stderr.Wait(2000) ? stderr.Result : "").Trim();
                error = why.Length > 0 ? Redline.Clip(why, 300) : "Claude Code gave no answer";
                return null;
            }
            return text;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) ClaudeCli.KillTree(process); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    // ---- Where each markup is ---------------------------------------------------------------------------------------------

    /// <summary>The sheet's drawing border in sheet coordinates: its title block's extent, else the sheet outline.</summary>
    public static LayoutMath.Rect Frame(ViewSheet sheet)
    {
        var doc = sheet.Document;
        var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();
        if (tb?.get_BoundingBox(sheet) is { } bb && bb.Max.X - bb.Min.X > 1e-6 && bb.Max.Y - bb.Min.Y > 1e-6)
            return new LayoutMath.Rect(bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y);
        var o = sheet.Outline;
        return new LayoutMath.Rect(o.Min.U, o.Min.V, o.Max.U, o.Max.V);
    }

    /// <summary>
    /// The sheet a markup is on: the number read from the title block (or given for the page), else the sheet_number
    /// argument, else a sheet number in the file name, else the active sheet. Null when none fits.
    /// </summary>
    public static ViewSheet? SheetFor(Document doc, Redline.Markup m, string? given, string? file, List<ViewSheet> sheets)
    {
        ViewSheet? ByNumber(string? n) => string.IsNullOrWhiteSpace(n) ? null
            : sheets.FirstOrDefault(s => s.SheetNumber.Equals(n!.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? sheets.FirstOrDefault(s => Squash(s.SheetNumber) == Squash(n!));
        if (ByNumber(m.Sheet) is { } read) return read;
        if (ByNumber(given) is { } arg) return arg;
        return InFileName(file, sheets) ?? doc.ActiveView as ViewSheet;
    }

    /// <summary>The sheet whose number is in the file name (the longest such number), or null.</summary>
    public static ViewSheet? InFileName(string? file, List<ViewSheet> sheets)
    {
        if (file is null) return null;
        var name = Path.GetFileNameWithoutExtension(file);
        return sheets.Where(s => s.SheetNumber.Length >= 2 &&
                Regex.IsMatch(name, @"(?<![A-Za-z0-9])" + Regex.Escape(s.SheetNumber) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
            .OrderByDescending(s => s.SheetNumber.Length).FirstOrDefault();
    }

    private static string Squash(string s) => Regex.Replace(s, "[^A-Za-z0-9]", "").ToUpperInvariant();

    /// <summary>A viewport and how its view maps onto the sheet.</summary>
    public sealed class Port
    {
        public required Viewport Viewport { get; init; }
        public required View View { get; init; }
        public required LayoutMath.Rect Box { get; init; }
        public required Func<XYZ, (double X, double Y)> ToSheet { get; init; }
        public required Func<double, double, XYZ> FromSheet { get; init; }
    }

    /// <summary>The viewports on a sheet with their mapping: Revit's own transforms, else the view's scale and centre.</summary>
    public static List<Port> Ports(ViewSheet sheet)
    {
        var doc = sheet.Document;
        var ports = new List<Port>();
        foreach (var id in sheet.GetAllViewports())
        {
            if (doc.GetElement(id) is not Viewport vp || doc.GetElement(vp.ViewId) is not View view) continue;
            var o = vp.GetBoxOutline();
            var box = new LayoutMath.Rect(o.MinimumPoint.X, o.MinimumPoint.Y, o.MaximumPoint.X, o.MaximumPoint.Y);
            Func<XYZ, (double, double)> toSheet;
            Func<double, double, XYZ> fromSheet;
            try
            {
                var p2s = vp.GetProjectionToSheetTransform();
                var m2p = view.GetModelToProjectionTransforms() is { Count: > 0 } list ? list[0].GetModelToProjectionTransform() : Transform.Identity;
                Transform s2p = p2s.Inverse, p2m = m2p.Inverse;
                toSheet = p => { var s = p2s.OfPoint(m2p.OfPoint(p)); return (s.X, s.Y); };
                fromSheet = (x, y) => OnPlane(view, p2m.OfPoint(s2p.OfPoint(new XYZ(x, y, 0))));
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // The view's scale and centre (crop box), turned as the viewport is.
                XYZ right = view.RightDirection, up = view.UpDirection, origin = view.Origin;
                var c = view.CropBox is { } cb ? cb.Transform.OfPoint((cb.Min + cb.Max) / 2) : origin;
                double cu = (c - origin).DotProduct(right), cv = (c - origin).DotProduct(up);
                var center = vp.GetBoxCenter();
                var rotation = vp.Rotation switch { ViewportRotation.Clockwise => -90.0, ViewportRotation.Counterclockwise => 90.0, _ => 0.0 };
                var scale = Math.Max(1, view.Scale);
                toSheet = p => Redline.ViewToSheet((p - origin).DotProduct(right), (p - origin).DotProduct(up), cu, cv, center.X, center.Y, scale, rotation);
                fromSheet = (x, y) =>
                {
                    var (u, v) = Redline.SheetToView(x, y, cu, cv, center.X, center.Y, scale, rotation);
                    return OnPlane(view, origin + right * u + up * v);
                };
            }
            ports.Add(new Port { Viewport = vp, View = view, Box = box, ToSheet = toSheet, FromSheet = fromSheet });
        }
        return ports;
    }

    /// <summary>A point on the view's plane (a plan's level), where Revit places annotation.</summary>
    private static XYZ OnPlane(View view, XYZ p)
    {
        if (view is ViewPlan { GenLevel: { } level }) return new XYZ(p.X, p.Y, level.ProjectElevation);
        var n = view.ViewDirection;
        return p - n * (p - view.Origin).DotProduct(n);
    }

    /// <summary>An element's box on the sheet, seen in <paramref name="view"/>; null when it has none there.</summary>
    private static LayoutMath.Rect? BoxOnSheet(Element e, View view, Func<XYZ, (double X, double Y)> toSheet)
    {
        var bb = e.get_BoundingBox(view);
        if (bb is null) return null;
        var pts = ViewKit.Corners(bb).Select(toSheet).ToList();
        return new LayoutMath.Rect(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
    }

    private static readonly HashSet<BuiltInCategory> Never =
    [
        BuiltInCategory.OST_TitleBlocks, BuiltInCategory.OST_Viewports, BuiltInCategory.OST_Views, BuiltInCategory.OST_Viewers,
        BuiltInCategory.OST_Cameras, BuiltInCategory.OST_ElevationMarks, BuiltInCategory.OST_Sheets, BuiltInCategory.OST_SectionBox,
        BuiltInCategory.OST_RevisionClouds, BuiltInCategory.OST_RevisionCloudTags, BuiltInCategory.OST_Matchline, BuiltInCategory.OST_ReferenceViewer,
    ];

    /// <summary>What a markup of this kind can change in a view: text notes, dimensions, model elements, or (for delete) also annotation.</summary>
    private static IEnumerable<Element> Targets(Document doc, View view, string kind)
    {
        var all = new FilteredElementCollector(doc, view.Id);
        IEnumerable<Element> list = kind switch
        {
            "edit_text" => all.OfClass(typeof(TextNote)).ToElements(),
            "set_dimension" => all.OfClass(typeof(Dimension)).Where(d => d is not SpotDimension).ToList(),
            _ => all.WhereElementIsNotElementType().Where(e => e.Category is { } c && !Never.Contains((BuiltInCategory)c.Id.Value) && e is not View
                && (kind == "delete"
                    ? !(e is Level || e is Autodesk.Revit.DB.Grid || e is ReferencePlane)
                    : c.CategoryType == CategoryType.Model || e is Level || e is Autodesk.Revit.DB.Grid)).ToList(),
        };
        return list.Take(5000);
    }

    /// <summary>What is known about an element, for matching the markup's words.</summary>
    private static string?[] Facts(Document doc, Element e) =>
    [
        e.Category?.Name, e.Name, (doc.GetElement(e.GetTypeId()) as ElementType)?.FamilyName, doc.GetElement(e.GetTypeId())?.Name,
        e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(), (e as TextNote)?.Text, (e as Dimension)?.ValueString, e.Id.Value.ToString(),
    ];

    /// <summary>
    /// Works out each markup against the model: its sheet, the viewport and view it falls in, the element it means and the
    /// exact change, or why it can't be done (then the user does it).
    /// </summary>
    public static List<Planned> Plan(Document doc, IReadOnlyList<Redline.Markup> markups, string? sheetNumber, string? file)
    {
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
        var ports = new Dictionary<long, List<Port>>();
        var plans = new List<Planned>();
        var radius = RadiusMm / 304.8;
        for (var i = 0; i < markups.Count; i++)
        {
            var m = markups[i];
            CancelPoint.ThrowIfRequested();
            var p = new Planned { Markup = m };
            plans.Add(p);
            p.Sheet = SheetFor(doc, m, sheetNumber, file, sheets);
            if (p.Sheet is null) { p.Problem = "which sheet it is on is not known: give sheet_number, or open the sheet"; continue; }
            var frame = Frame(p.Sheet);
            var at = Redline.BoxOnSheet(m, frame, 0);
            if (at is not { } mark) { p.Problem = "it has no position on the sheet"; continue; }
            p.Cloud = mark.Grow(3 / 304.8);
            if (m.Kind == "none") { p.Problem = "no change could be read from it" + (m.Text.Length > 0 ? $" (\"{Redline.Clip(m.Text, 60)}\")" : ""); continue; }
            if (m.Confidence < Redline.MinConfidence) { p.Problem = $"it was read with low confidence ({m.Confidence:0.##})"; continue; }

            if (!ports.TryGetValue(p.Sheet.Id.Value, out var onSheet)) ports[p.Sheet.Id.Value] = onSheet = Ports(p.Sheet);
            var (ax, ay) = Redline.Anchor(m, frame)!.Value;
            var index = Redline.ViewportAt(ax, ay, onSheet.Select(o => o.Box).ToList());
            var port = index >= 0 ? onSheet[index] : null;
            p.View = port?.View ?? p.Sheet;
            Func<XYZ, (double X, double Y)> toSheet = port is null ? q => (q.X, q.Y) : port.ToSheet;
            Func<double, double, XYZ> fromSheet = port is null ? (x, y) => new XYZ(x, y, 0) : port.FromSheet;

            if (m.Kind == "add_note")
            {
                if (p.View is View3D or ViewSchedule || p.View.ViewType == ViewType.Rendering)
                { p.Problem = $"notes can't be added to '{p.View.Name}' ({p.View.ViewType})"; continue; }
                if (string.IsNullOrWhiteSpace(m.To ?? m.Text)) { p.Problem = "no note text was read"; continue; }
                p.At = fromSheet(mark.X0, mark.Y1);
                continue;
            }

            // The element: the markup's own box (or its arrow's tip), matched against what that view shows.
            var target = m.Point is not null ? Redline.At(ax, ay) : mark;
            var candidates = new List<Redline.Candidate>();
            foreach (var e in Targets(doc, p.View, m.Kind))
                if (BoxOnSheet(e, p.View, toSheet) is { } box)
                    candidates.Add(new Redline.Candidate(e.Id.Value, $"{e.Category?.Name ?? e.GetType().Name} {e.Id.Value}", box,
                        Redline.HintScore(m.Target + " " + (m.Kind == "set_parameter" ? "" : m.Text), m.Kind == "edit_text" ? m.From : null, Facts(doc, e))));
            var (pick, why) = Redline.Pick(candidates, target, radius);
            if (pick is null) { p.Problem = $"no single element matches it in {(port is null ? "the sheet" : $"'{p.View.Name}'")}: {why}"; continue; }
            p.Element = doc.GetElement(new ElementId(pick.Id));
            switch (m.Kind)
            {
                case "edit_text":
                    if (string.IsNullOrEmpty(m.To)) p.Problem = "no new text was read";
                    break;
                case "set_parameter":
                    if (string.IsNullOrWhiteSpace(m.Parameter) || m.To is null) { p.Problem = "the parameter or its new value was not read"; break; }
                    var param = p.Element!.LookupParameter(m.Parameter) ?? doc.GetElement(p.Element.GetTypeId())?.LookupParameter(m.Parameter);
                    if (param is null) p.Problem = $"{p.Where} has no parameter '{m.Parameter}'";
                    else if (param.IsReadOnly) p.Problem = $"'{m.Parameter}' is read-only on {p.Where}";
                    else if (param.Element.Id != p.Element.Id) p.Problem = $"'{m.Parameter}' is a type parameter: changing it changes every element of that type; do it by hand if that is meant";
                    break;
                case "set_dimension":
                    PlanDimension(doc, p, ax, ay, toSheet);
                    break;
                case "delete":
                    if (p.Element!.Pinned) p.Problem = $"{p.Where} is pinned";
                    break;
            }
        }
        return plans;
    }

    private static void PlanDimension(Document doc, Planned p, double ax, double ay, Func<XYZ, (double X, double Y)> toSheet)
    {
        var m = p.Markup;
        var dim = (Dimension)p.Element!;
        if (dim.NumberOfSegments > 0)
        {
            // The segment nearest the markup.
            var best = double.MaxValue;
            for (var i = 0; i < dim.NumberOfSegments; i++)
            {
                var (x, y) = toSheet(dim.Segments.get_Item(i).Origin);
                var d = (x - ax) * (x - ax) + (y - ay) * (y - ay);
                if (d < best) { best = d; p.Segment = i; }
            }
        }
        if (m.Mode != "move")
        {
            if (string.IsNullOrWhiteSpace(m.To) && m.ValueMm is null) p.Problem = "the new dimension text was not read";
            return;
        }
        if (m.ValueMm is not { } mm) { p.Problem = "a move needs the new dimension as a number"; return; }
        if (dim.NumberOfSegments > 0) { p.Problem = "moving to suit one segment of a dimension string is left to you (the other segments would change too)"; return; }
        if (dim.Curve is not Line line || dim.Value is not { } current) { p.Problem = "only a straight dimension can drive a move"; return; }
        var ends = new List<Element>();
        foreach (Reference r in dim.References)
            if (doc.GetElement(r) is { } e && ends.All(x => x.Id != e.Id)) ends.Add(e);
        if (ends.Count != 2) { p.Problem = "the dimension does not measure between two elements"; return; }
        double Score(Element e) => Redline.HintScore(m.Target, null, Facts(doc, e));
        var movable = ends.Where(e => !e.Pinned).ToList();
        Element moving;
        if (movable.Count == 0) { p.Problem = "both ends of the dimension are pinned"; return; }
        if (movable.Count == 1) moving = movable[0];
        else if (Math.Abs(Score(ends[0]) - Score(ends[1])) > 1e-9) moving = Score(ends[0]) > Score(ends[1]) ? ends[0] : ends[1];
        else { p.Problem = $"it doesn't say which end moves ({ends[0].Category?.Name} {ends[0].Id.Value} or {ends[1].Category?.Name} {ends[1].Id.Value})"; return; }
        var fixedEnd = ends.First(e => e.Id != moving.Id);
        double Along(Element e) => e.get_BoundingBox(null) is { } bb ? ((bb.Min + bb.Max) / 2 - line.Origin).DotProduct(line.Direction) : 0;
        var by = Redline.MoveAlong(current, mm / 304.8, Along(moving), Along(fixedEnd));
        p.MoveId = moving.Id;
        p.MoveBy = line.Direction * by;
    }

    // ---- Applying ---------------------------------------------------------------------------------------------------------

    /// <summary>Asks whether to apply one change: true apply, false skip, null stop asking (skip the rest).</summary>
    public static bool? Ask(Planned p, int index, int count)
    {
        if (TestApprove is { } hook) return hook(p);
        var m = p.Markup;
        var dialog = new TaskDialog("Sketch ProMax: redlines")
        {
            MainInstruction = $"Apply markup {m.Id}? ({index} of {count})",
            MainContent = $"{m.Describe()}\nWhere: {p.Where}" + (m.Text.Length > 0 ? $"\nThe markup reads: \"{Redline.Clip(m.Text, 200)}\"" : "") +
                          (m.Kind == "delete" ? "\n\nThis DELETES the element (it was struck through)." : "") +
                          "\n\nA revision cloud is drawn round it on the sheet. Ctrl+Z undoes the whole set.",
            AllowCancellation = true,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, m.Kind == "delete" ? "Delete it" : "Apply this change");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Skip it", "It is listed as not done in the report.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Stop asking", "Skips this markup and the ones after it.");
        dialog.DefaultButton = TaskDialogResult.CommandLink2;
        return dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 => true,
            TaskDialogResult.CommandLink2 => false,
            _ => null,
        };
    }

    /// <summary>Applies one approved change and clouds it on the sheet, inside the item's transaction. Returns the cloud's id (or why there is none).</summary>
    public static object? Apply(Document doc, Planned p, ElementId revision)
    {
        var m = p.Markup;
        switch (m.Kind)
        {
            case "edit_text":
                var note = (TextNote)p.Element!;
                note.Text = Redline.EditText(note.Text.TrimEnd('\r'), m.From, m.To!);
                break;
            case "add_note":
                var typeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                TextNote.Create(doc, p.View!.Id, p.At!, m.To ?? m.Text, typeId);
                break;
            case "set_parameter":
                var param = p.Element!.LookupParameter(m.Parameter!) ?? throw new InvalidOperationException($"no parameter '{m.Parameter}'");
                if (!SetParametersTool.Write(param, m.To!)) throw new InvalidOperationException($"Revit did not take '{m.To}' for {m.Parameter}");
                break;
            case "set_dimension":
                if (p.MoveId is { } moving) ElementTransformUtils.MoveElement(doc, moving, p.MoveBy!);
                else
                {
                    var text = m.To is { Length: > 0 } t ? t : Redline.F(m.ValueMm ?? 0);
                    var dim = (Dimension)p.Element!;
                    if (p.Segment >= 0) dim.Segments.get_Item(p.Segment).ValueOverride = text;
                    else dim.ValueOverride = text;
                }
                break;
            case "delete":
                doc.Delete(p.Element!.Id);
                break;
            default:
                throw new InvalidOperationException("nothing to apply");
        }
        if (p.Sheet is null || p.Cloud is not { } c) return null;
        XYZ P(double x, double y) => new(x, y, 0);
        var curves = new List<Curve>
        {
            Line.CreateBound(P(c.X0, c.Y0), P(c.X1, c.Y0)), Line.CreateBound(P(c.X1, c.Y0), P(c.X1, c.Y1)),
            Line.CreateBound(P(c.X1, c.Y1), P(c.X0, c.Y1)), Line.CreateBound(P(c.X0, c.Y1), P(c.X0, c.Y0)),
        };
        try { return RevisionCloud.Create(doc, p.Sheet, revision, curves).Id.Value; }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
        { return "no cloud: " + ex.Message; }
    }

    /// <summary>The hand-off for a markup the user does by hand.</summary>
    public static HandoffStep Step(Planned p, string why) => new()
    {
        What = $"Do redline {p.Markup.Id} by hand: {p.Markup.Describe()}" + (p.Markup.Text.Length > 0 && p.Markup.Kind != "none" ? $" (it reads \"{Redline.Clip(p.Markup.Text, 80)}\")" : ""),
        Where = p.Where.Length > 0 ? p.Where : "the sheet the markup is on",
        Why = why,
        Kind = "redline",
        Item = p.Markup.Id,
        RunAgain = false,
    };

    /// <summary>One plan entry for a tool result.</summary>
    public static object Row(Planned p) => new
    {
        id = p.Markup.Id, page = p.Markup.Page, sheet = p.Sheet?.SheetNumber, view = p.View is null or ViewSheet ? null : p.View.Name, viewId = p.View?.Id.Value,
        elementId = p.Element?.Id.Value, element = p.Element is null ? null : $"{p.Element.Category?.Name} {(p.Element as TextNote)?.Text.TrimEnd('\r') ?? p.Element.Name}".Trim(),
        drawnAs = p.Markup.Type, text = p.Markup.Text, change = p.Markup.Describe(),
        status = p.Problem is null ? "ready" : "can't be applied", why = p.Problem,
    };

    public static Redline.Line ReportLine(Planned p, string status, string note) =>
        new(p.Markup.Id, p.Markup.Page, p.Sheet?.SheetNumber ?? "", p.Markup.Type, p.Markup.Text, p.Markup.Describe(), p.Where, status, note);

    /// <summary>The arguments both redline_plan and redline_apply take.</summary>
    public const string CommonJson = """
      "file": {"type": "string", "description": "The marked-up PDF, or a photo or image (PNG/JPG) of the markups."},
      "markups": {"type": "array", "items": {"type": "object"}, "description": "The markups as read from the page (redline_read gives the format). Leave out to have each page read by Claude Code here (it uses the user's Claude plan)."},
      "pages": {"type": "array", "items": {"type": "integer"}, "description": "PDF pages to read when markups are not given (default all, at most 20)."},
      "sheet_number": {"type": "string", "description": "The sheet the markups are on, when the title block can't be read (default: the number read, one in the file name, or the active sheet)."},
      "markup_ids": {"type": "array", "items": {"type": "string"}, "description": "Only these markups (ids as listed)."}
    """;
}

public sealed class RedlineReadTool : IRevitTool
{
    public string Name => "redline_read";
    public string Description =>
        "Redlines in, revised sheets out, step 1: shows you one page of a marked-up PDF, or a photo of markups, as an image " +
        "to read. A PDF page is rasterised by Revit (no PDF reader needed); a photo is used as it is. Read every markup " +
        "(text, clouds, arrows, strike-throughs, dimension changes) into the JSON format in the result, then call redline_plan " +
        "with file and markups to see each one matched to a sheet, view and element.";
    public string PropertiesJson => """
    {
      "file": {"type": "string", "description": "The marked-up PDF, or a photo or image (PNG/JPG) of the markups."},
      "page": {"type": "integer", "description": "PDF page, from 1 (default 1)."},
      "pixel_width": {"type": "integer", "description": "Longest side of the image, 800 to 2400 pixels (default 1800)."}
    }
    """;
    public string[] Required => ["file"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var file = Redlines.CheckedFile(args.String("file"));
        var page = (int)(args.OptLong("page") ?? 1);
        if (page < 1) throw new ArgumentException("page counts from 1.");
        var pages = Redlines.IsPdf(file) ? Redlines.PdfPages(file) : 1;
        var max = (int)Math.Min(2400, Math.Max(800, args.OptLong("pixel_width") ?? 1800));
        var png = Redlines.PageImage(doc, file, page, Redlines.WorkFolder(), max);
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        var guess = Redlines.InFileName(file, sheets);
        var text = JsonSerializer.Serialize(new
        {
            file, page, pagesFound = pages, image = png,
            sheetFromFileName = guess?.SheetNumber,
            format = Redline.Format,
            next = "Read every markup on this page into the format above (coordinates as fractions of the drawing sheet's border), then call " +
                   "redline_plan with file and markups and show the user the planned changes. When they agree, call redline_apply with the same " +
                   "markups: Revit asks them to approve each change, clouds it, adds the revision, exports the revised sheets and reports done / not done." +
                   (pages > page ? $" This PDF seems to have {pages} pages; read the others with page." : ""),
        });
        return new ToolResult(text, PngImage: File.ReadAllBytes(png));
    }
}

public sealed class RedlinePlanTool : IRevitTool
{
    public string Name => "redline_plan";
    public string Description =>
        "Redlines in, revised sheets out, step 2: matches each markup to its sheet (the title block number read, sheet_number, " +
        "or the file name), the viewport and view it falls in, and the element it means (nearest, matching its words), and " +
        "lists the planned changes: text and note edits, new notes, parameter values, dimension overrides or moves where a " +
        "dimension change is explicit, deletes for strike-throughs. Says why any markup can't be applied. Reads the model only. " +
        "Without markups, each page is read by Claude Code here.";
    public string PropertiesJson => "{" + Redlines.CommonJson + "}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var notes = new List<string>();
        var markups = Redlines.Load(doc, args, notes, out var source);
        if (markups.Count == 0)
            return ToolResult.Ok(new { source, markups = 0, notes, next = "No markups were found. Check the file, or read the page with redline_read and pass markups." });
        var plan = Redlines.Plan(doc, markups, args.OptString("sheet_number"), args.OptString("file"));
        var lines = plan.Select(p => Redlines.ReportLine(p, p.Problem is null ? "planned" : "can't be applied", p.Problem ?? "")).ToList();
        return ToolResult.Ok(new
        {
            source, markups = markups.Count, ready = plan.Count(p => p.Problem is null), cantApply = plan.Count(p => p.Problem is not null),
            plan = plan.Select(Redlines.Row).ToList(),
            markupsRead = markups,
            notes = notes.Count > 0 ? notes : null,
            list = Redline.Markdown(lines),
            next = "Show the user this list. To apply, call redline_apply with the same file and markups (markup_ids for a part of them): " +
                   "Revit asks them to approve each change one by one; what can't be applied goes to them as a manual step.",
        });
    }
}

public sealed class RedlineApplyTool : IRevitTool
{
    public string Name => "redline_apply";
    public string Description =>
        "Redlines in, revised sheets out: applies the markups of a marked-up PDF or photo to the model. Each markup is matched " +
        "to its sheet, view and element (as redline_plan lists), Revit asks the user to approve each change one by one, then " +
        "each approved change is applied (text and notes, parameter values, dimension overrides or explicit moves, deletes " +
        "for strike-throughs) with a revision cloud round it on the sheet, under one new revision, and the revised sheets are " +
        "exported to PDF. Markups it can't interpret go to the user as manual steps. Writes a done / not done report to Excel " +
        "and returns it for the chat. One Undo step.";
    public string PropertiesJson => "{" + Redlines.CommonJson + """
      ,
      "revision_description": {"type": "string", "description": "The new revision's description (default 'Redlines <file> <date>')."},
      "revision_id": {"type": "integer", "description": "Use this existing, not issued revision instead of a new one."},
      "export": {"type": "boolean", "description": "Export the revised sheets to PDF (default true)."},
      "folder": {"type": "string", "description": "Where the report and PDFs go (default Documents\\SketchProMax)."},
      "dry_run": {"type": "boolean", "description": "Only list the planned changes, as redline_plan (default false)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var notes = new List<string>();
        var markups = Redlines.Load(doc, args, notes, out var source);
        var plan = markups.Count == 0 ? [] : Redlines.Plan(doc, markups, args.OptString("sheet_number"), args.OptString("file"));
        if (args.OptBool("dry_run") || markups.Count == 0)
            return ToolResult.Ok(new
            {
                dryRun = args.OptBool("dry_run"), source, markups = markups.Count, plan = plan.Select(Redlines.Row).ToList(), notes = notes.Count > 0 ? notes : null,
                note = markups.Count == 0 ? "No markups were found; nothing was changed." : "Nothing was changed (dry run).",
            });

        // Approval, one change at a time, before anything changes.
        var ready = plan.Where(p => p.Problem is null).ToList();
        var approved = new List<Redlines.Planned>();
        var status = new Dictionary<Redlines.Planned, (string Status, string Note)>();
        var stopped = false;
        for (var i = 0; i < ready.Count; i++)
        {
            var p = ready[i];
            if (stopped) { status[p] = ("skipped", "not asked: you stopped the approvals"); continue; }
            switch (Redlines.Ask(p, i + 1, ready.Count))
            {
                case true: approved.Add(p); break;
                case false: status[p] = ("skipped", "you skipped it"); break;
                default: stopped = true; status[p] = ("skipped", "you stopped the approvals here"); break;
            }
        }

        // The revision and the changes: one Undo step.
        Revision? revision = null;
        var newRevision = false;
        ItemBatch.Result? batch = null;
        if (approved.Count > 0)
        {
            if (args.OptLong("revision_id") is { } rid)
            {
                revision = doc.GetElement(new ElementId(rid)) as Revision ?? throw new ArgumentException("revision_id is not a revision. Use list_revisions, or leave it out for a new one.");
                if (revision.Issued) throw new ArgumentException($"Revision {revision.SequenceNumber} is issued, so Revit won't add clouds to it. Leave revision_id out for a new one.");
            }
            using var group = new TransactionGroup(doc, "Sketch ProMax: redlines");
            group.Start();
            try
            {
                if (revision is null)
                {
                    using var t = RevitHelpers.StartTransaction(doc, "Redlines revision");
                    revision = Revision.Create(doc);
                    revision.Description = args.OptString("revision_description") ?? $"Redlines {Path.GetFileNameWithoutExtension(source)} {DateTime.Now:yyyy-MM-dd}".Trim();
                    revision.RevisionDate = DateTime.Now.ToString("yyyy-MM-dd");
                    t.Commit();
                    newRevision = true;
                }
                var revId = revision.Id;
                batch = ItemBatch.Run(doc, "Redlines", approved, p => $"{p.Markup.Id}: {p.Markup.Describe()}",
                    p => p.Element is null ? [] : [p.Element.Id], p => Redlines.Apply(doc, p, revId),
                    manual: (p, why) => Redlines.Step(p, "Revit did not take the change: " + why));
            }
            finally
            {
                if (group.HasStarted() && !group.HasEnded())
                {
                    if (batch is { Done: > 0 }) group.Assimilate();
                    else group.RollBack();
                }
            }
            if (batch.Done == 0 && newRevision) revision = null;
            for (var i = 0; i < approved.Count; i++)
            {
                var o = batch.Items[i];
                status[approved[i]] = o.Ok
                    ? ("done", ((o.Value is string cloudNote ? cloudNote + "; " : "") + (o.Error ?? "")).Trim(' ', ';'))
                    : ("failed", o.Error ?? "");
            }
        }

        // The revised sheets, exported.
        var folder = Path.Combine(Checks.Folder(args), $"Redlines {DateTime.Now:yyyy-MM-dd HHmm}");
        Directory.CreateDirectory(folder);
        var doneSheets = plan.Where(p => status.TryGetValue(p, out var s) && s.Status == "done" && p.Sheet is not null)
            .Select(p => p.Sheet!).GroupBy(s => s.Id.Value).Select(g => g.First()).ToList();
        var exported = new List<string>();
        string? exportProblem = null;
        if (args.OptBool("export", true) && doneSheets.Count > 0)
        {
            var started = DateTime.UtcNow.AddSeconds(-2);
            var raw = JsonSerializer.SerializeToElement(new { view_ids = doneSheets.Select(s => s.Id.Value).ToArray(), folder, name_pattern = "{number} - {name} - {revision}" });
            try
            {
                var r = new ExportPdfTool().Execute(app, new ToolArgs(raw.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())));
                if (r.IsError) exportProblem = r.Text;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException) { exportProblem = ex.Message; }
            exported = Directory.GetFiles(folder, "*.pdf").Where(f => File.GetLastWriteTimeUtc(f) >= started).Select(Path.GetFileName).OfType<string>().OrderBy(f => f).ToList();
            if (exported.Count == 0) exportProblem ??= "Revit wrote no PDF.";
        }

        // Hand-offs: what couldn't be placed or read, and what Revit refused.
        var handoffs = new List<HandoffItem>();
        foreach (var p in plan.Where(p => p.Problem is not null))
        {
            status[p] = ("handed over", p.Problem!);
            handoffs.Add(new HandoffItem(Redlines.Step(p, p.Problem!), null));
        }
        if (batch is not null)
            foreach (var h in batch.Handoffs)
            {
                handoffs.Add(h);
                var p = approved.FirstOrDefault(a => h.Step.Item == a.Markup.Id);
                if (p is not null) status[p] = ("handed over", status[p].Note);
            }

        var lines = plan.Select(p => status.TryGetValue(p, out var s) ? Redlines.ReportLine(p, s.Status, s.Note) : Redlines.ReportLine(p, "not done", "")).ToList();
        var revisionText = revision is null ? "" : $"{revision.SequenceNumber} {revision.Description}";
        var report = Path.Combine(folder, "Redlines report.xlsx");
        string? reportProblem = null;
        try { Xlsx.Write(report, Redline.Workbook(lines, source, revisionText, exported)); }
        catch (IOException ex) { reportProblem = ex.Message; report = ""; }
        catch (UnauthorizedAccessException ex) { reportProblem = ex.Message; report = ""; }

        var result = ToolResult.Ok(new
        {
            source, summary = Redline.Summary(lines),
            revision = revision is null ? null : new { id = revision.Id.Value, sequence = revision.SequenceNumber, description = revision.Description },
            applied = batch?.Summary(),
            sheets = doneSheets.Select(s => s.SheetNumber).ToList(),
            exported, exportFolder = exported.Count > 0 ? folder : null, exportProblem,
            report = report.Length > 0 ? report : null, reportProblem,
            markups = lines.Select(l => new { id = l.Id, sheet = l.Sheet, change = l.Change, where = l.Where, done = l.Done, status = l.Status, note = l.Note }).ToList(),
            notes = notes.Count > 0 ? notes : null,
            chatReport = Redline.Markdown(lines),
            next = "Show the user chatReport (the done / not done table) and where the report and PDFs are.",
        });
        return Handoff.Finish(Name, result, handoffs);
    }
}
