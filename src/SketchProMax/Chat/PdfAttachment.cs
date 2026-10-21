using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// How an attached PDF goes to the chat: whole, as a document block (read like the Claude app reads it, text and pages),
/// when it fits the API's limits; otherwise named by path with a note on how to look at its pages. Claude Code's Read
/// tool can't be relied on for PDFs on Windows (larger ones need page rendering it doesn't have there).
/// </summary>
internal static class PdfAttachment
{
    /// <summary>The API reads at most 100 PDF pages per request.</summary>
    public const int MaxPages = 100;

    /// <summary>Raw bytes of all PDFs in one message: base64 grows them by a third, and a request is at most 32 MB with the pictures.</summary>
    public const long MaxBytes = 18L * 1024 * 1024;

    public enum Verdict { Send, TooBig, TooManyPages, PagesUnknown, Locked, NotPdf }

    public sealed record Check(Verdict Verdict, int Pages, long Bytes);

    /// <summary>
    /// Whether <paramref name="bytes"/> can go whole, given what is already in the message (<paramref name="usedPages"/>,
    /// <paramref name="usedBytes"/>). A PDF whose pages can't be counted goes only when it is small, so a long one that hides
    /// its page tree never makes the API refuse the whole message.
    /// </summary>
    public static Check Assess(byte[] bytes, int usedPages = 0, long usedBytes = 0)
    {
        var text = Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
        if (!text.StartsWith("%PDF", StringComparison.Ordinal) && text.IndexOf("%PDF", 0, Math.Min(text.Length, 1024), StringComparison.Ordinal) < 0)
            return new Check(Verdict.NotPdf, 0, bytes.Length);
        var pages = Pages(text);
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

    /// <summary>The note on a PDF that is not sent whole: why, and how to look at it instead.</summary>
    public static string Note(Check check) => check.Verdict switch
    {
        Verdict.Locked => "password-protected PDF, so it can't be read. Ask the user for an unlocked copy, or to export the pages they mean as pictures",
        Verdict.NotPdf => "named .pdf but not a PDF file; read it with the Read tool if it is text",
        _ => (check.Verdict switch
             {
                 Verdict.TooManyPages => $"PDF of {check.Pages} pages, more than the {MaxPages} that can be sent whole in one message",
                 Verdict.TooBig => $"PDF of {check.Bytes / (1024.0 * 1024):0.#} MB, more than can be sent whole in one message",
                 _ => "large PDF whose pages couldn't be counted, so it isn't sent whole",
             }) +
             ". Look at the pages that matter with redline_read (file and page): it shows any PDF page as a picture. " +
             "Tell the user in one line that you are reading it page by page",
    };

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
