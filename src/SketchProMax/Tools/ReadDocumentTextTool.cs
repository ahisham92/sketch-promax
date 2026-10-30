using System.IO;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// read_document_text: the text of a Word, PowerPoint or Excel file on this PC (Chat.OfficeText), or the newest such
/// files in a folder, so the chat can write a new report, letter or minutes the way the office's earlier ones are written.
/// The chat's Read tool reads plain text and PDFs but not Office files. Reads files only; never the model.
/// </summary>
public sealed class ReadDocumentTextTool : IRevitTool
{
    public const int DefaultChars = 30_000, MaxChars = 120_000, MaxListed = 30;

    public string Name => "read_document_text";
    public string Description =>
        "Reads the text of a Word (.docx), PowerPoint (.pptx) or Excel (.xlsx, .xlsm) file on this PC, in reading order (headings, " +
        "paragraphs, tables, slides, sheets as tables). Give a folder instead to list its Office, text and PDF files, newest first. Use it " +
        "to learn the layout, headings, numbering and tone of the office's earlier reports, letters, minutes or BOQs before writing a new " +
        "one with make_document. Plain text and PDF files: use the Read tool. Not a model change.";
    public string PropertiesJson => """
    {
      "path": {"type": "string", "description": "The file's or folder's full path."},
      "max_chars": {"type": "integer", "description": "A file: at most this much text (default 30000, up to 120000)."}
    }
    """;
    public string[] Required => ["path"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args) => Read(args.OptString("path") ?? "", (int)(args.OptLong("max_chars") ?? DefaultChars));

    /// <summary>The answer for a path; no Revit needed.</summary>
    internal static ToolResult Read(string path, int maxChars)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return ToolResult.Error("Give the file's or folder's full path in 'path'.");
        try
        {
            if (Directory.Exists(path)) return ToolResult.Ok(new { folder = path, files = List(path), next = "Read two or three of the newest of the same kind (path = the file) and follow their layout and tone." });
            if (!File.Exists(path)) return ToolResult.Error($"There is no file or folder at {path}.");
            if (!Chat.OfficeText.IsOffice(path))
                return ToolResult.Error("read_document_text reads Word, PowerPoint and Excel files (.docx, .pptx, .xlsx, .xlsm). Use the Read tool for text and PDF files; older .doc, .ppt and .xls files can't be read: ask the user for a .docx or PDF copy.");
            var text = Chat.OfficeText.Read(path).Text;
            var limit = Math.Max(1_000, Math.Min(MaxChars, maxChars));
            var cut = text.Length > limit;
            return ToolResult.Ok(new
            {
                file = Path.GetFileName(path),
                modified = File.GetLastWriteTime(path).ToString("yyyy-MM-dd"),
                text = cut ? text.Substring(0, limit) : text,
                cut = cut ? $"Only the first {limit} of {text.Length} characters; pass a larger max_chars for more." : null,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException or NotSupportedException or ArgumentException)
        {
            return ToolResult.Error($"Couldn't read {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>The folder's Office, text and PDF files (not its subfolders), newest first.</summary>
    internal static List<object> List(string folder) =>
        new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => !f.Name.StartsWith("~$", StringComparison.Ordinal)
                        && (Chat.OfficeText.IsOffice(f.Name) || f.Extension.ToLowerInvariant() is ".pdf" or ".txt" or ".md" or ".csv"))
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxListed)
            .Select(f => (object)new { name = f.Name, path = f.FullName, modified = f.LastWriteTime.ToString("yyyy-MM-dd"), kb = (f.Length + 1023) / 1024 })
            .ToList();
}
