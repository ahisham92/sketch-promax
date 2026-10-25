using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SketchProMax.Update;

/// <summary>
/// The parts of an update that pick up where an interrupted one stopped, so Try again (or the next automatic check)
/// doesn't start over:
///   - the download goes to &lt;zip&gt;.part and continues from its end with an HTTP range request. The server's ETag is
///     kept beside it (&lt;zip&gt;.etag) and sent as If-Range, so a release that was replaced since is downloaded afresh
///     rather than glued onto the old bytes. Only a complete download is renamed to &lt;zip&gt;.
///   - a complete zip is checked, not downloaded again.
///   - unpacking skips files an earlier try already wrote. Each file is written under a temporary name and renamed
///     when whole, so a file under its real name is always complete. The folder notes which zip it came from
///     (<see cref="SourceFile"/>), so files from another download are never mixed in.
///   - a version folder with <see cref="CompleteMark"/> in it is unpacked completely; only the install steps remain.
/// </summary>
internal static class UpdateResume
{
    /// <summary>Written last into a fully unpacked version folder.</summary>
    public const string CompleteMark = ".complete";

    /// <summary>In a partly unpacked folder: the SHA-256 of the zip it is being unpacked from.</summary>
    public const string SourceFile = ".source";

    /// <summary>A download that receives nothing for this long is stopped; the next try continues from there.</summary>
    public static readonly TimeSpan Stall = TimeSpan.FromSeconds(90);

    public static string PartOf(string zip) => zip + ".part";

    private static string EtagOf(string zip) => zip + ".etag";

    public static bool IsComplete(string versionFolder) => File.Exists(Path.Combine(versionFolder, CompleteMark));

    public static void MarkComplete(string versionFolder, string version) =>
        File.WriteAllText(Path.Combine(versionFolder, CompleteMark), version);

    /// <summary>Bytes an earlier try already downloaded for <paramref name="zip"/> (0 when none).</summary>
    public static long Downloaded(string zip)
    {
        var part = PartOf(zip);
        return File.Exists(part) ? new FileInfo(part).Length : 0;
    }

    /// <summary>Throws away a download (complete or not), so the next try starts it afresh.</summary>
    public static void Discard(string zip)
    {
        foreach (var path in new[] { zip, PartOf(zip), EtagOf(zip) })
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="zip"/>, continuing a partial download an earlier try left.
    /// <paramref name="report"/> gets (bytes so far, total or null). On failure the partial download is kept for the next
    /// try. Throws <see cref="TimeoutException"/> when no data arrives for <paramref name="stall"/>.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, string url, string zip, Action<long, long?> report, TimeSpan stall, CancellationToken ct)
    {
        var part = PartOf(zip);
        var etagFile = EtagOf(zip);
        // Twice at most: once more from the start when the server can't continue what is here.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var have = Downloaded(zip);
            RangeConditionHeaderValue? condition = null;
            if (have > 0)
            {
                var etag = File.Exists(etagFile) ? File.ReadAllText(etagFile).Trim() : null;
                // Without a strong ETag there is no telling whether the bytes here belong to the file there now.
                if (etag is null || !RangeConditionHeaderValue.TryParse(etag, out condition) || condition.EntityTag is not { IsWeak: false })
                {
                    Discard(zip);
                    have = 0;
                    condition = null;
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0)
            {
                request.Headers.Range = new RangeHeaderValue(have, null);
                request.Headers.IfRange = condition;
            }
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
            watchdog.CancelAfter(stall);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Stalled(stall); }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Nothing after what is here: it is all here, unless the file there is now shorter.
                    if (response.Content.Headers.ContentRange?.Length == have) { Finish(part, zip); return; }
                    Discard(zip);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var resumed = response.StatusCode == HttpStatusCode.PartialContent;
                long? total;
                if (resumed)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != have) { Discard(zip); continue; }
                    total = range.Length;
                }
                else
                {
                    // A whole file: the server ignored the range, or the file changed. Start over with it.
                    have = 0;
                    total = response.Content.Headers.ContentLength;
                    var tag = response.Headers.ETag;
                    if (tag is { IsWeak: false }) File.WriteAllText(etagFile, tag.ToString());
                    else if (File.Exists(etagFile)) File.Delete(etagFile);
                }

                var done = have;
                report(done, total);
                using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        watchdog.CancelAfter(stall);
                        int read;
                        try { read = await body.ReadAsync(buffer, 0, buffer.Length, watchdog.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Stalled(stall); }
                        if (read == 0) break;
                        await file.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        done += read;
                        report(done, total);
                    }
                }
                if (total is { } expected && done != expected)
                    throw new IOException($"the download stopped at {Megabytes(done)} of {Megabytes(expected)}");
                Finish(part, zip);
                return;
            }
        }
        throw new IOException("the server could not continue the download");
    }

    private static TimeoutException Stalled(TimeSpan stall) =>
        new($"the download stopped receiving data for {stall.TotalSeconds:0} seconds");

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    private static void Finish(string part, string zip)
    {
        if (File.Exists(zip)) File.Delete(zip);
        File.Move(part, zip);
        try { File.Delete(EtagOf(zip)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    /// <summary>
    /// Unpacks <paramref name="zip"/> into <paramref name="folder"/>, keeping the files an earlier try of the same zip
    /// (by <paramref name="zipSha256"/>) already wrote. Returns how many files were already there.
    /// </summary>
    public static int Extract(string zip, string folder, string zipSha256)
    {
        var source = Path.Combine(folder, SourceFile);
        var same = File.Exists(source) && string.Equals(File.ReadAllText(source).Trim(), zipSha256, StringComparison.OrdinalIgnoreCase);
        if (!same && Directory.Exists(folder)) WithRetry(() => Directory.Delete(folder, true));
        Directory.CreateDirectory(folder);
        if (!same) File.WriteAllText(source, zipSha256);

        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var kept = 0;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(folder, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The update package has a file outside its folder ({entry.FullName}).");
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            if (File.Exists(path) && new FileInfo(path).Length == entry.Length)
            {
                kept++;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".unzipping";
            using (var input = entry.Open())
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            WithRetry(() =>
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            });
        }
        return kept;
    }

    /// <summary>
    /// Runs a file-system step, trying again briefly when it fails: right after files are written, antivirus or the
    /// Windows search indexer often holds them open for a moment.
    /// </summary>
    public static void WithRetry(Action step, int attempts = 4)
    {
        for (var i = 1; ; i++)
        {
            try { step(); return; }
            catch (Exception ex) when (i < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250 * i);
            }
        }
    }
}
