using System.IO.Compression;
using System.Net;
using System.Net.Http;
using SketchProMax.Update;

/// <summary>An update that stopped part way continues from there: the download from its last byte, the unpack from its last file.</summary>
internal static class UpdateResumeTests
{
    /// <summary>A small release server on this machine: serves one file, honours Range and If-Range, and can be told to misbehave.</summary>
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        public byte[] Content = Array.Empty<byte>();
        public string Etag = "\"v1\"";
        public int CutAfter = -1;          // stop the connection after this many body bytes
        public int StallAfter = -1;        // stop sending (but keep the connection) after this many body bytes
        public bool IgnoreRange;
        public readonly List<string?> Ranges = new();
        public string Url { get; }

        public Server()
        {
            for (var port = 18431; ; port++)
            {
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    Url = $"http://127.0.0.1:{port}/SketchProMax-9.9.9.zip";
                    break;
                }
                catch (HttpListenerException) when (port < 18530) { }
            }
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(() => Serve(ctx));
            }
        }

        private async Task Serve(HttpListenerContext ctx)
        {
            var range = ctx.Request.Headers["Range"];
            lock (Ranges) Ranges.Add(range);
            var ifRange = ctx.Request.Headers["If-Range"];
            var response = ctx.Response;
            response.Headers["ETag"] = Etag;
            long from = 0;
            if (range is not null && !IgnoreRange && (ifRange is null || ifRange == Etag))
            {
                from = long.Parse(range.Substring("bytes=".Length).TrimEnd('-'));
                if (from >= Content.Length)
                {
                    response.StatusCode = 416;
                    response.Headers["Content-Range"] = $"bytes */{Content.Length}";
                    response.Close();
                    return;
                }
                response.StatusCode = 206;
                response.Headers["Content-Range"] = $"bytes {from}-{Content.Length - 1}/{Content.Length}";
            }
            response.ContentLength64 = Content.Length - from;
            try
            {
                var body = Content.AsMemory((int)from);
                var limit = CutAfter >= 0 ? CutAfter : StallAfter >= 0 ? StallAfter : body.Length;
                await response.OutputStream.WriteAsync(body.Slice(0, Math.Min(limit, body.Length)));
                await response.OutputStream.FlushAsync();
                if (CutAfter >= 0 && CutAfter < body.Length) { response.Abort(); return; }
                if (StallAfter >= 0 && StallAfter < body.Length) { await Task.Delay(5000); response.Abort(); return; }
                response.Close();
            }
            catch (Exception) { try { response.Abort(); } catch (Exception) { } }
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch (Exception) { } }
    }

    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "spm-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Download(dir, check);
            Unpack(dir, check);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    private static void Download(string dir, Action<string, bool, string> check)
    {
        var data = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(data);
        using var server = new Server { Content = data };
        using var http = new HttpClient();
        var zip = Path.Combine(dir, "SketchProMax-9.9.9.zip");
        long lastReported = -1;
        Task Get(TimeSpan? stall = null) => UpdateResume.DownloadAsync(http, server.Url, zip, (b, _) => lastReported = b, stall ?? TimeSpan.FromSeconds(30), CancellationToken.None);
        Exception? Fails(TimeSpan? stall = null) { try { Get(stall).GetAwaiter().GetResult(); return null; } catch (Exception ex) { return ex; } }

        // 1. The connection drops a third of the way: the bytes so far are kept, not the complete file.
        server.CutAfter = 1024 * 1024;
        var cut = Fails();
        var kept = UpdateResume.Downloaded(zip);
        check("update resume: a dropped download keeps what arrived", cut is IOException && kept > 0 && kept <= data.Length && !File.Exists(zip),
            $"{cut?.GetType().Name}: {cut?.Message}; kept {kept}");

        // 2. Trying again asks only for the rest, and the result is the whole file.
        server.CutAfter = -1;
        server.Ranges.Clear();
        var resumed = Fails();
        check("update resume: Try again continues from the last byte", resumed is null && server.Ranges.FirstOrDefault() == $"bytes={kept}-"
            && File.Exists(zip) && File.ReadAllBytes(zip).AsSpan().SequenceEqual(data) && !File.Exists(UpdateResume.PartOf(zip)),
            $"{resumed?.Message}; range {server.Ranges.FirstOrDefault()}");
        check("update resume: progress counts the bytes from the earlier try", lastReported == data.Length, lastReported.ToString());

        // 3. The release was replaced between tries: If-Range makes the server send the new file whole, not glued onto the old one.
        File.Delete(zip);
        server.CutAfter = 512 * 1024;
        Fails();
        var replaced = new byte[data.Length + 100];
        new Random(9).NextBytes(replaced);
        server.Content = replaced;
        server.Etag = "\"v2\"";
        server.CutAfter = -1;
        var fresh = Fails();
        check("update resume: a replaced release is downloaded afresh", fresh is null && File.ReadAllBytes(zip).AsSpan().SequenceEqual(replaced), fresh?.Message ?? "");

        // 4. A server that ignores ranges sends the whole file: it is written from the start, not appended.
        File.Delete(zip);
        server.CutAfter = 512 * 1024;
        Fails();
        server.CutAfter = -1;
        server.IgnoreRange = true;
        var whole = Fails();
        check("update resume: a server that can't continue gets the whole file", whole is null && File.ReadAllBytes(zip).AsSpan().SequenceEqual(replaced), whole?.Message ?? "");
        server.IgnoreRange = false;

        // 5. A download that stops receiving data is ended, its bytes kept for the next try.
        File.Delete(zip);
        server.StallAfter = 256 * 1024;
        var stalled = Fails(TimeSpan.FromSeconds(1));
        check("update resume: a stalled download stops and keeps its bytes", stalled is TimeoutException && UpdateResume.Downloaded(zip) > 0,
            $"{stalled?.GetType().Name}: {stalled?.Message}");
        server.StallAfter = -1;
        var after = Fails();
        check("update resume: ...and the next try finishes it", after is null && File.ReadAllBytes(zip).AsSpan().SequenceEqual(replaced), after?.Message ?? "");

        // 6. Everything had arrived but the rename didn't happen: the server says there is nothing more, and the file is used.
        File.Move(zip, UpdateResume.PartOf(zip));
        File.WriteAllText(zip + ".etag", server.Etag);
        var done = Fails();
        check("update resume: a download that had all its bytes is finished, not fetched again", done is null && File.ReadAllBytes(zip).AsSpan().SequenceEqual(replaced), done?.Message ?? "");

        // 7. Bytes with no ETag to vouch for them are not trusted: the download starts over.
        File.Delete(zip);
        File.WriteAllBytes(UpdateResume.PartOf(zip), new byte[1000]);
        server.Ranges.Clear();
        var untrusted = Fails();
        check("update resume: a partial download with no ETag starts over", untrusted is null && server.Ranges.FirstOrDefault() is null
            && File.ReadAllBytes(zip).AsSpan().SequenceEqual(replaced), untrusted?.Message ?? "");
    }

    private static void Unpack(string dir, Action<string, bool, string> check)
    {
        var zip = Path.Combine(dir, "pack.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("pkg/dist/Revit2025/SketchProMax.dll", "core"), ("pkg/dist/Revit2025/Other.dll", "other"), ("pkg/dist/mcp/SketchProMax.Mcp.exe", "mcp") })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        var sha = UpdateResume.Sha256Of(zip);
        var folder = Path.Combine(dir, "9.9.9.partial");
        var first = UpdateResume.Extract(zip, folder, sha);
        var core = Path.Combine(folder, "pkg", "dist", "Revit2025", "SketchProMax.dll");
        check("update resume: unpacks every file", first == 0 && File.ReadAllText(core) == "core"
            && File.ReadAllText(Path.Combine(folder, "pkg", "dist", "mcp", "SketchProMax.Mcp.exe")) == "mcp", first.ToString());

        // An unpack that stopped part way: one file missing, one left half-written under its temporary name.
        File.Delete(Path.Combine(folder, "pkg", "dist", "Revit2025", "Other.dll"));
        File.WriteAllText(Path.Combine(folder, "pkg", "dist", "Revit2025", "Other.dll.unzipping"), "ot");
        var again = UpdateResume.Extract(zip, folder, sha);
        check("update resume: unpacking again keeps the files already written", again == 2
            && File.ReadAllText(Path.Combine(folder, "pkg", "dist", "Revit2025", "Other.dll")) == "other", again.ToString());

        // A folder from a different zip is not mixed in.
        var stray = Path.Combine(folder, "stray.txt");
        File.WriteAllText(stray, "old");
        var other = UpdateResume.Extract(zip, folder, "different");
        check("update resume: files from another download are thrown away", other == 0 && !File.Exists(stray) && File.ReadAllText(core) == "core", other.ToString());

        var marked = Path.Combine(dir, "9.9.9");
        Directory.CreateDirectory(marked);
        var before = UpdateResume.IsComplete(marked);
        UpdateResume.MarkComplete(marked, "9.9.9");
        check("update resume: a fully unpacked version is marked so the next try skips to installing", !before && UpdateResume.IsComplete(marked), "");

        var evil = Path.Combine(dir, "evil.zip");
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open()))
            writer.Write("x");
        var refused = false;
        try { UpdateResume.Extract(evil, Path.Combine(dir, "evil"), "e"); }
        catch (InvalidDataException) { refused = true; }
        check("update resume: a package file outside its folder is refused", refused && !File.Exists(Path.Combine(dir, "outside.txt")), "");
    }
}
