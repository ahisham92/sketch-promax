using System.IO;
using System.Text.Json;
using Autodesk.Revit.UI;
using SketchProMax.Chat;

namespace SketchProMax.Tools;

/// <summary>
/// make_document: an Excel, Word, PowerPoint or PDF file the chat writes on request (a bill of quantities, a report, a
/// presentation, a letter…). The writers are OfficeXlsx, OfficeDocx, OfficePptx and OfficePdf; nothing needs Office.
/// The chat shows the file as a card with Open, Show in folder and Save a copy (ChatFiles). Not a model change.
/// </summary>
public sealed class MakeDocumentTool : IRevitTool
{
    public string Name => "make_document";

    public string Description =>
        "Make a file for the user: an Excel workbook, a Word document, a PowerPoint presentation or a PDF (a bill of " +
        "quantities, a cost plan, a report, a schedule, a letter, a presentation…). Get the facts first (from the model with " +
        "the other tools, or from the user), then write the whole file in one call. format=excel: 'sheets', each with " +
        "columns (the header), rows (arrays of cells: numbers as JSON numbers, text as strings, a formula as a string starting " +
        "with '=' such as \"=D4*E4\"; a row can be {\"cells\": [...], \"bold\": true} for totals), optional title, formats per " +
        "column (auto, text, integer, decimal, percent) and widths. Rows are numbered as in Excel: without a sheet title the " +
        "header is row 1 and data starts at row 2; with a title the header is row 3 and data starts at row 4. Use formulas " +
        "for amounts and totals so the user can edit quantities and rates. format=word or pdf: 'content' in Markdown " +
        "(# headings, paragraphs, **bold**, *italic*, - and 1. lists, | tables |, > quotes, --- rules, a picture as " +
        "![caption](C:\\full\\path.png), and a line [page break]). format=powerpoint: 'slides', each with a title and any of " +
        "bullets (indent a sub-point with two spaces), image (a PNG or JPEG path, e.g. from capture_view_image), caption, " +
        "table {columns, rows}; a title slide is added from title and subtitle. The file goes to Documents\\Sketch ProMax\\" +
        "Made in chat unless folder is given, and never replaces a file (a new name gets (2), (3)…). The chat then shows it " +
        "with Open, Show in folder and Save a copy, so don't repeat the path. Not a model change.";

    public string PropertiesJson => """
    {
      "format": {"type": "string", "enum": ["excel", "word", "powerpoint", "pdf"], "description": "The kind of file."},
      "title": {"type": "string", "description": "The document's title: at the top, on the title slide, and the file name unless file_name is given."},
      "subtitle": {"type": "string", "description": "A line under the title (project, date, revision…)."},
      "content": {"type": "string", "description": "word and pdf: the document in Markdown (see the description). Also accepted for excel (each table becomes a sheet) and powerpoint (each heading starts a slide)."},
      "sheets": {"type": "array", "description": "excel: the sheets.", "items": {"type": "object", "properties": {
        "name": {"type": "string", "description": "The sheet tab (31 characters at most)."},
        "title": {"type": "string", "description": "A bold title in row 1 (the header then is row 3)."},
        "columns": {"type": "array", "items": {"type": "string"}, "description": "The header row."},
        "rows": {"type": "array", "items": {"anyOf": [{"type": "array", "items": {"anyOf": [{"type": "string"}, {"type": "number"}, {"type": "boolean"}, {"type": "null"}]}}, {"type": "object", "properties": {"cells": {"type": "array", "items": {"anyOf": [{"type": "string"}, {"type": "number"}, {"type": "boolean"}, {"type": "null"}]}}, "bold": {"type": "boolean"}}}]}, "description": "Each row: an array of cells, or {\"cells\": [...], \"bold\": true}."},
        "formats": {"type": "array", "items": {"type": "string", "enum": ["auto", "text", "integer", "decimal", "percent"]}, "description": "Per column; auto shows 2 decimals where a number has them."},
        "widths": {"type": "array", "items": {"type": "number"}, "description": "Per column, in characters (0 = fit the content)."}
      }}},
      "slides": {"type": "array", "description": "powerpoint: the slides after the title slide.", "items": {"type": "object", "properties": {
        "title": {"type": "string"},
        "bullets": {"type": "array", "items": {"type": "string"}, "description": "Points; two leading spaces per level for sub-points; **bold** works."},
        "image": {"type": "string", "description": "Full path of a PNG or JPEG picture on this PC."},
        "caption": {"type": "string"},
        "table": {"type": "object", "properties": {"columns": {"type": "array", "items": {"type": "string"}}, "rows": {"type": "array", "items": {"type": "array", "items": {"anyOf": [{"type": "string"}, {"type": "number"}, {"type": "boolean"}, {"type": "null"}]}}}}}
      }}},
      "landscape": {"type": "boolean", "description": "Landscape pages (word, pdf, and how excel prints). Default false."},
      "file_name": {"type": "string", "description": "The file name without folder (default: the title). The extension is added."},
      "folder": {"type": "string", "description": "Where to save it. Default Documents\\Sketch ProMax\\Made in chat; leave it out unless the user asked for a place."}
    }
    """;

    public string[] Required => ["format", "title"];
    public bool ModifiesModel => false;

    /// <summary>Where files go when the call names no folder (inside Sketch ProMax's own folders, so it never asks).</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Made in chat");

    public ToolResult Execute(UIApplication app, ToolArgs args) => Make(args, DefaultFolder);

    /// <summary>Makes the file (no Revit needed, so the tests and the field test call it directly).</summary>
    internal static ToolResult Make(ToolArgs args, string defaultFolder)
    {
        var format = DocumentArgs.Format(args.OptString("format"));
        if (format is null) return ToolResult.Error("format must be excel, word, powerpoint or pdf.");
        var title = (args.OptString("title") ?? "").Trim();
        var subtitle = (args.OptString("subtitle") ?? "").Trim();
        var content = args.OptString("content") ?? "";
        var landscape = args.OptBool("landscape");
        string folder;
        try { folder = OutputPaths.Checked(args.OptString("folder")) is { Length: > 0 } f ? f.Trim().Trim('"') : defaultFolder; }
        catch (ArgumentException ex) { return ToolResult.Error(ex.Message); }
        var name = OfficeDocs.SafeName(Path.GetFileNameWithoutExtension((args.OptString("file_name") ?? "").Trim()) is { Length: > 0 } n ? n : title);
        var ext = format switch { "excel" => ".xlsx", "word" => ".docx", "powerpoint" => ".pptx", _ => ".pdf" };

        try
        {
            Directory.CreateDirectory(folder);
            var path = OfficeDocs.FreePath(folder, name, ext);
            var missing = new List<string>();
            object details;
            switch (format)
            {
                case "excel":
                {
                    var sheets = DocumentArgs.Sheets(args, content, landscape);
                    if (sheets.Count == 0) return ToolResult.Error("Give the workbook's sheets in 'sheets' (columns and rows), or Markdown tables in 'content'.");
                    OfficeXlsx.Write(path, title, sheets);
                    var names = OfficeXlsx.SheetNames(sheets);
                    details = new { sheets = sheets.Select((s, i) => new { name = names[i], rows = s.Rows.Count, first_data_row = OfficeXlsx.FirstDataRow(s) }).ToList() };
                    break;
                }
                case "word":
                    if (content.Trim().Length == 0) return ToolResult.Error("Give the document in 'content' (Markdown).");
                    missing = OfficeDocx.Write(path, title, subtitle, content, landscape);
                    details = new { };
                    break;
                case "powerpoint":
                {
                    var slides = DocumentArgs.Slides(args, content);
                    if (slides.Count == 0) return ToolResult.Error("Give the slides in 'slides' (title with bullets, image or table), or Markdown with a heading per slide in 'content'.");
                    missing = OfficePptx.Write(path, title, subtitle, slides);
                    details = new { slides = OfficePptx.Expand(slides).Count + 1 };
                    break;
                }
                default:
                {
                    if (content.Trim().Length == 0) return ToolResult.Error("Give the document in 'content' (Markdown).");
                    var (pages, lost) = OfficePdf.Write(path, title, subtitle, content, landscape);
                    missing = lost;
                    details = new { pages };
                    break;
                }
            }
            var size = new FileInfo(path).Length;
            return ToolResult.Ok(new
            {
                file = path,
                format = DocumentArgs.Kind(format),
                title,
                size_kb = Math.Max(1, (int)Math.Round(size / 1024.0)),
                details,
                missing_pictures = missing,
                shown = "The chat shows this file as a card with Open, Show in folder and Save a copy. Say in a line or two what is in it; don't repeat the path.",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            return ToolResult.Error($"Couldn't write the file in {folder}: {ex.Message}");
        }
    }
}

/// <summary>Reads make_document's arguments into what the writers take. No Revit here, so the tests run it.</summary>
internal static class DocumentArgs
{
    public static string? Format(string? text) => (text ?? "").Trim().ToLowerInvariant().TrimStart('.') switch
    {
        "excel" or "xlsx" or "xls" or "spreadsheet" or "workbook" => "excel",
        "word" or "docx" or "doc" or "document" => "word",
        "powerpoint" or "pptx" or "ppt" or "presentation" or "slides" or "deck" => "powerpoint",
        "pdf" => "pdf",
        _ => null,
    };

    public static string Kind(string format) => format switch
    {
        "excel" => "Excel workbook", "word" => "Word document", "powerpoint" => "PowerPoint presentation", _ => "PDF",
    };

    public static List<OfficeXlsx.Sheet> Sheets(ToolArgs args, string content, bool landscape)
    {
        var sheets = new List<OfficeXlsx.Sheet>();
        foreach (var s in args.Array("sheets"))
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var rows = new List<OfficeXlsx.Row>();
            if (s.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray())
                {
                    if (r.ValueKind == JsonValueKind.Array) rows.Add(new OfficeXlsx.Row(r.EnumerateArray().Select(Cell).ToList()));
                    else if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
                        rows.Add(new OfficeXlsx.Row(cells.EnumerateArray().Select(Cell).ToList(), r.TryGetProperty("bold", out var b) && b.ValueKind == JsonValueKind.True));
                }
            sheets.Add(new OfficeXlsx.Sheet(Str(s, "name") ?? "Sheet" + (sheets.Count + 1), Str(s, "title") ?? "", Strings(s, "columns"), rows,
                Strings(s, "formats"), Numbers(s, "widths"), landscape));
        }
        if (sheets.Count > 0) return sheets;
        // No sheets: each Markdown table becomes one, named after the heading above it.
        var heading = "";
        foreach (var block in OfficeDocs.Blocks(content))
        {
            if (block is Markdown.Heading h) heading = OfficeDocs.Plain(h.Text);
            if (block is not Markdown.Table t) continue;
            var rows = t.Rows.Select(r => new OfficeXlsx.Row(r.Select(c => (object?)TableCell(c)).ToList(),
                Markdown.Inline(r.Count > 0 ? r[0] : "").FirstOrDefault()?.Style.HasFlag(Markdown.Style.Bold) == true)).ToList();
            sheets.Add(new OfficeXlsx.Sheet(heading.Length > 0 ? heading : "Sheet" + (sheets.Count + 1), "", t.Header.Select(OfficeDocs.Plain).ToList(), rows, [], [], landscape));
        }
        return sheets;
    }

    /// <summary>A cell: numbers stay numbers, "=…" is a formula, true/false is Yes/No, anything else its text.</summary>
    internal static object? Cell(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.GetDouble();
            case JsonValueKind.String:
            {
                var s = e.GetString() ?? "";
                return s.Length > 1 && s[0] == '=' && !char.IsWhiteSpace(s[1]) && s[1] != '=' ? new OfficeXlsx.Formula(s) : s;
            }
            case JsonValueKind.True: return "Yes";
            case JsonValueKind.False: return "No";
            case JsonValueKind.Object:
                if (e.TryGetProperty("formula", out var f) && f.ValueKind == JsonValueKind.String) return new OfficeXlsx.Formula(f.GetString() ?? "");
                if (e.TryGetProperty("value", out var v)) return Cell(v);
                return e.GetRawText();
            case JsonValueKind.Array:
                return string.Join(", ", e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()));
            default:
                return null;
        }
    }

    /// <summary>A Markdown table cell for Excel: a plain number ("1,250.5", not "12 m") becomes a number.</summary>
    private static object TableCell(string markdown)
    {
        var text = OfficeDocs.Plain(markdown).Trim();
        var plain = text.Replace(",", "");
        return System.Text.RegularExpressions.Regex.IsMatch(plain, @"^-?\d+(\.\d+)?$") && !(plain.Length > 1 && plain[0] == '0' && plain[1] != '.')
            && double.TryParse(plain, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : text;
    }

    public static List<OfficePptx.Slide> Slides(ToolArgs args, string content)
    {
        var slides = new List<OfficePptx.Slide>();
        foreach (var s in args.Array("slides"))
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var bullets = new List<OfficePptx.Bullet>();
            if (s.TryGetProperty("bullets", out var bs) && bs.ValueKind == JsonValueKind.Array)
                foreach (var b in bs.EnumerateArray())
                {
                    if (b.ValueKind == JsonValueKind.String) bullets.Add(Bullet(b.GetString() ?? ""));
                    else if (b.ValueKind == JsonValueKind.Object && Str(b, "text") is { } text)
                        bullets.Add(new OfficePptx.Bullet(text.Trim(), b.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0));
                }
            OfficePptx.TableData? table = null;
            if (s.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                var rows = t.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array
                    ? rs.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Array)
                        .Select(r => (IReadOnlyList<string>)r.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ValueKind == JsonValueKind.Null ? "" : c.GetRawText()).ToList()).ToList()
                    : [];
                table = new OfficePptx.TableData(Strings(t, "columns"), rows);
            }
            slides.Add(new OfficePptx.Slide(Str(s, "title") ?? "", bullets.Where(b => b.Text.Length > 0).ToList(), (Str(s, "image") ?? "").Trim().Trim('"'), Str(s, "caption") ?? "", table));
        }
        if (slides.Count > 0 || content.Trim().Length == 0) return slides;

        // No slides: each heading in the Markdown starts one; lists and paragraphs are its points.
        string title = "", image = "", caption = "";
        var points = new List<OfficePptx.Bullet>();
        OfficePptx.TableData? tbl = null;
        void Flush()
        {
            if (title.Length > 0 || points.Count > 0 || image.Length > 0 || tbl is not null)
                slides.Add(new OfficePptx.Slide(title, points.ToList(), image, caption, tbl));
            title = image = caption = "";
            points.Clear();
            tbl = null;
        }
        foreach (var block in OfficeDocs.Blocks(content))
        {
            switch (block)
            {
                case Markdown.Heading h:
                    Flush();
                    title = h.Text;
                    break;
                case Markdown.ListBlock l:
                    points.AddRange(l.Items.Select(i => new OfficePptx.Bullet(i.Text, i.Depth)));
                    break;
                case Markdown.Paragraph p:
                    points.Add(new OfficePptx.Bullet(p.Text.Replace('\n', ' '), 0));
                    break;
                case Markdown.Table tb:
                    tbl = new OfficePptx.TableData(tb.Header, tb.Rows);
                    break;
                case OfficeDocs.ImageBlock img:
                    image = img.Path;
                    caption = img.Caption;
                    break;
            }
        }
        Flush();
        return slides;
    }

    /// <summary>A bullet from text: two leading spaces (or a tab) per level, and a typed "- " or "• " dropped.</summary>
    internal static OfficePptx.Bullet Bullet(string text)
    {
        var expanded = text.Replace("\t", "  ");
        var spaces = expanded.Length - expanded.TrimStart(' ').Length;
        var t = expanded.Trim();
        if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal) || t.StartsWith("\u2022 ", StringComparison.Ordinal)) t = t.Substring(2).Trim();
        return new OfficePptx.Bullet(t, Math.Min(3, spaces / 2));
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strings(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ValueKind == JsonValueKind.Null ? "" : x.GetRawText()).ToList()
            : [];

    private static List<double> Numbers(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToList()
            : [];
}
