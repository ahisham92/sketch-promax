using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// How an attached PDF goes to the chat: whole, as a document block (read like the Claude app reads it, text and pages),
/// when it fits the API's limits with what the chat already sent; otherwise named by path with its text in a file
/// and a note on how to look at its pages. Claude Code's Read
/// tool can't be relied on for PDFs on Windows (larger ones need page rendering it doesn't have there).
/// </summary>
internal static class PdfAttachment
{
    /// <summary>The API reads at most 100 PDF pages per request, and each request carries the whole chat.</summary>
    public const int MaxPages = 100;

    /// <summary>Raw bytes of all PDFs and pictures in one chat: base64 grows them by a third, and a request is at most 32 MB.</summary>
    public const long MaxBytes = 18L * 1024 * 1024;

    public enum Verdict { Send, TooBig, TooManyPages, PagesUnknown, Locked, NotPdf }

    public sealed record Check(Verdict Verdict, int Pages, long Bytes);

    /// <summary>
    /// Whether <paramref name="bytes"/> can go whole, given what this chat already sent (<paramref name="usedPages"/>,
    /// <paramref name="usedBytes"/>): Claude Code sends the whole conversation with each message, earlier PDFs and pictures
    /// too, and past the API's limit it quietly leaves them out. <paramref name="knownPages"/> is PdfPig's count, when it
    /// could open the file. A PDF whose pages can't be counted goes only when it is small, so a long one that hides
    /// its page tree never makes the API refuse the whole message.
    /// </summary>
    public static Check Assess(byte[] bytes, int usedPages = 0, long usedBytes = 0, int? knownPages = null)
    {
        var text = Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
        if (!text.StartsWith("%PDF", StringComparison.Ordinal) && text.IndexOf("%PDF", 0, Math.Min(text.Length, 1024), StringComparison.Ordinal) < 0)
            return new Check(Verdict.NotPdf, 0, bytes.Length);
        var pages = Math.Max(knownPages ?? 0, Pages(text));
        if (Regex.IsMatch(text, @"/Encrypt\s+(\d+\s+\d+\s+R|<<)")) return new Check(Verdict.Locked, pages, bytes.Length);
        if (usedBytes + bytes.Length > MaxBytes) return new Check(Verdict.TooBig, pages, bytes.Length);
        if (pages == 0) return new Check(bytes.Length <= 2_000_000 && usedPages + 20 <= MaxPages ? Verdict.Send : Verdict.PagesUnknown, 0, bytes.Length);
        if (usedPages + pages > MaxPages) return new Check(Verdict.TooManyPages, pages, bytes.Length);
        return new Check(Verdict.Send, pages, bytes.Length);
    }

    /// <summary>
    /// The pages: page objects, or the page tree's /Count, whichever is larger (an outline's /Count can only overcount),
    /// looked for in the file and in its compressed object streams. 0 when none can be found.
    /// </summary>
    public static int Pages(string text)
    {
        var pages = Count(text);
        foreach (Match m in Regex.Matches(text, @"/Type\s*/ObjStm\b"))
        {
            var start = text.IndexOf("stream", m.Index, StringComparison.Ordinal);
            if (start < 0) continue;
            start += 6;
            if (start < text.Length && text[start] == '\r') start++;
            if (start < text.Length && text[start] == '\n') start++;
            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            if (Inflate(text, start, end - start) is { } inner) pages = Math.Max(pages, Count(inner));
        }
        return pages;
    }

    private static int Count(string text)
    {
        var objects = Regex.Matches(text, @"/Type\s*/Page(?![a-zA-Z])").Count;
        var tree = 0;
        foreach (Match m in Regex.Matches(text, @"/Count\s+(\d{1,6})"))
            tree = Math.Max(tree, int.Parse(m.Groups[1].Value));
        return Math.Max(objects, tree);
    }

    /// <summary>A Flate stream's text (zlib: a 2-byte header, then deflate), or null when it isn't one.</summary>
    private static string? Inflate(string text, int start, int length)
    {
        if (length < 3) return null;
        var data = Encoding.GetEncoding("ISO-8859-1").GetBytes(text.Substring(start, length));
        try
        {
            using var input = new MemoryStream(data, 2, data.Length - 2);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > 16 * 1024 * 1024) break; // an object stream is small; never inflate a bomb
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(output.ToArray());
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// The note on a PDF that is not sent whole: why, and how to read it instead: its text, page by page, in
    /// <paramref name="textFile"/> (null when it couldn't be read), and any page as a picture with redline_read.
    /// </summary>
    public static string Note(Check check, string? textFile, bool mostlyPictures)
    {
        if (check.Verdict == Verdict.NotPdf) return "named .pdf but not a PDF file; read it with the Read tool if it is text";
        var why = check.Verdict switch
        {
            Verdict.Locked => "PDF with a password or restrictions, so it can't be sent whole",
            Verdict.TooManyPages => $"PDF of {check.Pages} pages, more than can be sent whole (at most {MaxPages} pages in a chat)",
            Verdict.TooBig => $"PDF of {check.Bytes / (1024.0 * 1024):0.#} MB, more than can be sent whole (at most {MaxBytes / (1024 * 1024)} MB of PDFs and pictures in a chat)",
            _ => "large PDF whose pages couldn't be counted, so it isn't sent whole",
        };
        if (textFile is null)
            return why + (check.Verdict == Verdict.Locked
                ? ", and its text couldn't be read either. Ask the user for an unlocked copy, or to export the pages they mean as pictures"
                : ". Look at the pages that matter with redline_read (file and page): it shows any PDF page as a picture. Tell the user in one line that you are reading it page by page");
        return why + $". Its text, page by page, is in {textFile}: search it with Grep (pages are headed \"=== Page N of M ===\") and read the parts you need with the Read tool. " +
               (mostlyPictures ? "Most pages have little or no text (drawings or scans), so look at the pages that matter with redline_read (file and page), which shows a PDF page as a picture. "
                               : "For a page's drawings or layout, look at it with redline_read (file and page), which shows a PDF page as a picture. ") +
               "Tell the user in one line that the file is large and you read the parts that matter, and that Split PDF in the pane's Activity tab makes parts that can be attached whole";
    }

    /// <summary>
    /// The note on a large PDF that the chat split by itself: its parts, each read in full by its own helper chat
    /// (read_pdf_part), and its text page by page to find which parts to ask. <paramref name="parts"/> are each part's
    /// file and pages.
    /// </summary>
    public static string PartsNote(Check check, IReadOnlyList<(string File, int First, int Last)> parts, int pages, string? textFile)
    {
        var sb = new StringBuilder(check.Verdict switch
        {
            Verdict.TooManyPages => $"PDF of {check.Pages} pages",
            _ => $"PDF of {check.Bytes / (1024.0 * 1024):0.#} MB" + (pages > 0 ? $" and {pages} pages" : ""),
        });
        sb.Append($", too large to send whole with what this chat holds, so Sketch ProMax split it into {parts.Count} part{(parts.Count == 1 ? " that is" : "s that are")} read whole:");
        for (var i = 0; i < parts.Count; i++)
            sb.Append($"\n    Part {i + 1}: pages {parts[i].First}-{parts[i].Last}: {parts[i].File}");
        if (textFile is not null)
            sb.Append($"\n  Its text, page by page, is in {textFile} (pages headed \"=== Page N of M ===\"). ");
        else sb.Append("\n  Its text couldn't be read, so go by the parts. ");
        sb.Append("To answer: find the pages the question is about (search the text file with Grep), then ask the part or parts covering them with " +
                  "read_pdf_part (file and the question in full; parts in parallel when it spans them). Each part is read in full by its own helper, " +
                  "text and drawings, and answers with page numbers. For a question about the whole file (a summary, every mention of something), when the " +
                  "user asks for a detailed check or to check all parts (or everything), or when the text can't show where the answer is (drawings or scans with no text), ask every part. Combine the answers into one reply that says which pages each point comes from. Tell the user in one line that the " +
                  "file was large and which parts you read; they never need to split it themselves");
        return sb.ToString();
    }

    /// <summary>
    /// A small PDF of <paramref name="pages"/> blank pages for the tests and the field test: its page objects in the file,
    /// or inside a compressed object stream as newer exporters write them; <paramref name="locked"/> adds an /Encrypt entry.
    /// </summary>
    public static byte[] Sample(int pages, bool compressed = false, bool locked = false)
    {
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{i + 3} 0 R"));
        var objects = new StringBuilder();
        for (var i = 0; i < pages; i++) objects.Append($"{i + 3} 0 obj <</Type /Page /Parent 2 0 R /MediaBox [0 0 595 842]>> endobj\n");
        var tree = $"1 0 obj <</Type /Catalog /Pages 2 0 R>> endobj\n2 0 obj <</Type /Pages /Kids [{kids}] /Count {pages}>> endobj\n" + objects;
        var latin = Encoding.GetEncoding("ISO-8859-1");
        var sb = new StringBuilder("%PDF-1.5\n");
        if (!compressed) sb.Append(tree);
        else
        {
            using var packed = new MemoryStream();
            packed.WriteByte(0x78);
            packed.WriteByte(0x9C);
            using (var deflate = new DeflateStream(packed, CompressionMode.Compress, leaveOpen: true))
            {
                var raw = latin.GetBytes(tree);
                deflate.Write(raw, 0, raw.Length);
            }
            var data = latin.GetString(packed.ToArray());
            sb.Append($"{pages + 3} 0 obj <</Type /ObjStm /N {pages + 2} /Filter /FlateDecode /Length {data.Length}>>\nstream\n")
              .Append(data).Append("\nendstream\nendobj\n");
        }
        sb.Append("trailer <</Root 1 0 R").Append(locked ? " /Encrypt 99 0 R" : "").Append(">>\n%%EOF\n");
        return latin.GetBytes(sb.ToString());
    }
}
