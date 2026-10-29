using System.IO;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Writer;

namespace SketchProMax.Tools;

/// <summary>
/// PDF work that needs no Revit, done with PdfPig (Apache 2.0): the page count, the text of every page (what the chat
/// reads from a PDF too large to send whole), and splitting a PDF into parts of at most a given size (Split PDF in the
/// pane, and on a large PDF in the chat). All of it runs off Revit's thread.
/// </summary>
internal static class PdfFiles
{
    private static ParsingOptions Lenient => new() { UseLenientParsing = true, SkipMissingFonts = true };

    /// <summary>The page count, or null when PdfPig can't open the file (damaged, or locked with a password).</summary>
    public static int? PageCount(byte[] bytes)
    {
        try
        {
            using var doc = PdfDocument.Open(bytes, Lenient);
            return doc.NumberOfPages;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    public sealed record TextResult(int Pages, int Characters, int PagesWithText, string? Error)
    {
        /// <summary>True when most pages have no text layer (scans, or drawings exported as pictures).</summary>
        public bool MostlyPictures => Error is null && PagesWithText * 2 < Pages;
    }

    /// <summary>
    /// Writes the text of every page to <paramref name="target"/>, each page headed "=== Page N of M ===" so a page can be
    /// found with a search. A page whose text can't be read is noted and skipped.
    /// </summary>
    public static TextResult WriteText(string pdf, string target, CancellationToken ct = default)
    {
        try
        {
            using var doc = PdfDocument.Open(SharedRead.AllBytes(pdf), Lenient);
            var total = doc.NumberOfPages;
            int characters = 0, withText = 0;
            using var output = new StreamWriter(target, false, new UTF8Encoding(false));
            output.WriteLine($"Text of {Path.GetFileName(pdf)}, {total} page{(total == 1 ? "" : "s")}, read by Sketch ProMax.");
            for (var n = 1; n <= total; n++)
            {
                ct.ThrowIfCancellationRequested();
                output.WriteLine();
                output.WriteLine($"=== Page {n} of {total} ===");
                string text;
                try { text = ContentOrderTextExtractor.GetText(doc.GetPage(n)).Trim(); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { text = $"(the text of this page couldn't be read: {ex.Message})"; }
                if (text.Length == 0) text = "(no text on this page: a picture or a drawing)";
                else if (!text.StartsWith("(the text", StringComparison.Ordinal)) { withText++; characters += text.Length; }
                output.WriteLine(text);
            }
            return new TextResult(total, characters, withText, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new TextResult(0, 0, 0, ex.Message);
        }
    }

    public sealed record Part(string File, int FirstPage, int LastPage, long Bytes);

    /// <summary>
    /// Splits <paramref name="pdf"/> into parts of at most <paramref name="maxBytes"/>, in page order, saved in
    /// <paramref name="folder"/> as "name - part 1 (pages 1-40).pdf". A part's pages are guessed from the average page
    /// size and halved until it fits; a single page larger than the limit becomes a part of its own.
    /// A part also has at most <paramref name="maxPages"/> pages. <paramref name="progress"/> gets the last page done and the page count.
    /// </summary>
    public static List<Part> Split(string pdf, long maxBytes, string folder, Action<int, int>? progress = null, CancellationToken ct = default, int maxPages = int.MaxValue)
    {
        if (maxBytes < 1024 * 1024) throw new ArgumentException("The part size is at least 1 MB.");
        var bytes = SharedRead.AllBytes(pdf);
        int total;
        try
        {
            using var doc = PdfDocument.Open(bytes, Lenient);
            total = doc.NumberOfPages;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ArgumentException($"{Path.GetFileName(pdf)} couldn't be opened as a PDF ({ex.Message}). If it has a password, save an unlocked copy first.");
        }
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(pdf);
        var parts = new List<Part>();
        var perPage = Math.Max(1.0, bytes.Length / (double)Math.Max(1, total));
        var first = 1;
        while (first <= total)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Max(1, Math.Min(Math.Min(total - first + 1, maxPages), (int)Math.Min(int.MaxValue, maxBytes * 0.9 / perPage)));
            byte[] part;
            while (true)
            {
                part = PdfMerger.Merge([bytes], [Enumerable.Range(first, count).ToList()]);
                if (part.Length <= maxBytes || count == 1) break;
                ct.ThrowIfCancellationRequested();
                count = Math.Max(1, Math.Min(count - 1, (int)(count * (maxBytes * 0.9 / part.Length))));
            }
            var last = first + count - 1;
            var file = Path.Combine(folder, $"{name} - part {parts.Count + 1} (pages {(first == last ? first.ToString() : $"{first}-{last}")}).pdf");
            File.WriteAllBytes(file, part);
            parts.Add(new Part(file, first, last, part.Length));
            progress?.Invoke(last, total);
            first = last + 1;
        }
        return parts;
    }

    public sealed record ChatParts(string Folder, int Pages, List<Part> Parts, string? TextFile, TextResult Text);

    /// <summary>
    /// A large PDF made ready for the chat: split into parts the chat's helpers read whole (at most
    /// <paramref name="maxBytes"/> and 100 pages each), its text page by page, and "parts.json" (the original's name, its
    /// pages and each part's pages, which read_pdf_part reads). Kept in a folder under <paramref name="root"/> named by
    /// the file's path, size and time, so the same file attached again uses the same parts, and their helpers' chats.
    /// </summary>
    public static ChatParts ForChat(string pdf, string root, long maxBytes, CancellationToken ct = default)
    {
        var info = new FileInfo(pdf);
        string hash;
        using (var sha = System.Security.Cryptography.SHA1.Create())
            hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes($"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"))
                .Take(4).Select(b => b.ToString("x2")));
        var name = Path.GetFileNameWithoutExtension(pdf);
        var folder = Path.Combine(root, $"{(name.Length > 60 ? name.Substring(0, 60) : name)} parts {hash}");
        var manifest = Path.Combine(folder, "parts.json");
        var textFile = Path.Combine(folder, name + " - text.txt");
        if (Read(manifest, folder) is { } kept && File.Exists(textFile))
        {
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow); // kept while it is used (attachments are cleaned after two weeks)
            return kept with { TextFile = textFile };
        }
        Directory.CreateDirectory(folder);
        var text = WriteText(pdf, textFile, ct);
        var parts = Split(pdf, maxBytes, folder, null, ct, maxPages: 100);
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = Path.GetFileName(pdf),
            ["pages"] = parts.Count == 0 ? 0 : parts[^1].LastPage,
            ["parts"] = new System.Text.Json.Nodes.JsonArray(parts.Select(p => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["file"] = Path.GetFileName(p.File), ["first"] = p.FirstPage, ["last"] = p.LastPage,
            }).ToArray()),
        };
        File.WriteAllText(manifest, json.ToJsonString(), new UTF8Encoding(false));
        return new ChatParts(folder, parts.Count == 0 ? 0 : parts[^1].LastPage, parts, text.Error is null ? textFile : null, text);
    }

    private static ChatParts? Read(string manifest, string folder)
    {
        try
        {
            if (!File.Exists(manifest)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            var r = doc.RootElement;
            var parts = r.GetProperty("parts").EnumerateArray().Select(p =>
            {
                var file = Path.Combine(folder, p.GetProperty("file").GetString() ?? "");
                return new Part(file, p.GetProperty("first").GetInt32(), p.GetProperty("last").GetInt32(), File.Exists(file) ? new FileInfo(file).Length : -1);
            }).ToList();
            if (parts.Count == 0 || parts.Any(p => p.Bytes < 0)) return null;
            var pages = r.GetProperty("pages").GetInt32();
            return new ChatParts(folder, pages, parts, null, new TextResult(pages, 0, pages, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    // ---- Samples for the tests and the field test ---------------------------------------------------------------------

    /// <summary>
    /// A PDF of <paramref name="pages"/> pages, each with the line "Sample page N", and, with <paramref name="noise"/>
    /// over 0, a picture of random pixels that size square on each page, so the file is large and doesn't compress.
    /// </summary>
    public static byte[] Sample(int pages, int noise = 0)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var random = new Random(pages * 31 + noise);
        for (var n = 1; n <= pages; n++)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText($"Sample page {n}", 14, new UglyToad.PdfPig.Core.PdfPoint(60, 780), font);
            if (noise > 0) page.AddPng(NoisePng(noise, random), new UglyToad.PdfPig.Core.PdfRectangle(60, 200, 535, 675));
        }
        return builder.Build();
    }

    /// <summary>A PNG of random pixels (8-bit RGB, stored with no filter), written by hand so no imaging library is needed.</summary>
    private static byte[] NoisePng(int size, Random random)
    {
        var raw = new byte[size * (size * 3 + 1)];
        random.NextBytes(raw);
        for (var row = 0; row < size; row++) raw[row * (size * 3 + 1)] = 0; // filter type: none
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0, 8);
        var header = new byte[13];
        BigEndian(header, 0, size);
        BigEndian(header, 4, size);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        Chunk(png, "IHDR", header);
        using (var zlib = new MemoryStream())
        {
            zlib.WriteByte(0x78);
            zlib.WriteByte(0x01);
            using (var deflate = new System.IO.Compression.DeflateStream(zlib, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(raw, 0, raw.Length);
            var adler = Adler32(raw);
            var tail = new byte[4];
            BigEndian(tail, 0, (int)adler);
            zlib.Write(tail, 0, 4);
            Chunk(png, "IDAT", zlib.ToArray());
        }
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        BigEndian(length, 0, data.Length);
        png.Write(length, 0, 4);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, 0, 4, body, 0);
        Buffer.BlockCopy(data, 0, body, 4, data.Length);
        png.Write(body, 0, body.Length);
        var crc = new byte[4];
        BigEndian(crc, 0, (int)Crc32(body));
        png.Write(crc, 0, 4);
    }

    private static void BigEndian(byte[] target, int at, int value)
    {
        target[at] = (byte)(value >> 24);
        target[at + 1] = (byte)(value >> 16);
        target[at + 2] = (byte)(value >> 8);
        target[at + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
