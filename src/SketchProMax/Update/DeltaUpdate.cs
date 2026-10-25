using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace SketchProMax.Update;

/// <summary>
/// Updates that download only the files that changed. Each release publishes, beside its zip,
/// SketchProMax-&lt;version&gt;.files.json (every file of the package's dist folder with its size and SHA-256) and each of
/// those files gzipped under its own hash (&lt;sha256&gt;.gz). The updater builds the new version folder from the
/// installed files whose hash matches and downloads only the rest. Anything unexpected (no list, a failed download, a
/// hash that doesn't match) returns false, and the caller downloads the whole zip as before.
/// </summary>
internal static class DeltaUpdate
{
    public sealed record Entry(string Path, long Size, string Sha256);

    /// <summary>The file list's address: the zip's, with .files.json for .zip.</summary>
    public static string ManifestUrl(string zipUrl) =>
        zipUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? zipUrl.Substring(0, zipUrl.Length - 4) + ".files.json" : zipUrl + ".files.json";

    /// <summary>A file's address: next to the zip, named by its hash.</summary>
    public static string FileUrl(string zipUrl, string sha256) => zipUrl.Substring(0, zipUrl.LastIndexOf('/') + 1) + sha256.ToLowerInvariant() + ".gz";

    /// <summary>Reads the file list; null when it isn't one (then the whole zip is used).</summary>
    public static IReadOnlyList<Entry>? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Entry>();
            foreach (var f in files.EnumerateArray())
            {
                var path = f.GetProperty("path").GetString() ?? "";
                var sha = f.GetProperty("sha256").GetString() ?? "";
                // Only plain relative paths inside the version folder, and real SHA-256 hashes.
                if (path.Length == 0 || path.StartsWith("/") || path.Contains("..") || path.Contains(":") || sha.Length != 64) return null;
                list.Add(new Entry(path.Replace('\\', '/'), f.GetProperty("size").GetInt64(), sha.ToLowerInvariant()));
            }
            return list.Count > 0 && list.Any(e => e.Path.EndsWith("/SketchProMax.dll", StringComparison.OrdinalIgnoreCase)) ? list : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// For each listed file, an installed copy with the same contents, looked for at the same place under each of
    /// <paramref name="bases"/> (installed version folders) and, for the connector, in <paramref name="connectorFolder"/>.
    /// Files with no match are left out: they are the ones to download.
    /// </summary>
    public static Dictionary<string, string> LocalCopies(IReadOnlyList<Entry> files, IEnumerable<string> bases, string? connectorFolder)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var baseList = bases.Where(Directory.Exists).ToList();
        foreach (var e in files)
        {
            var candidates = baseList.Select(b => Path.Combine(b, e.Path.Replace('/', Path.DirectorySeparatorChar))).ToList();
            if (connectorFolder is not null && e.Path.StartsWith("mcp/", StringComparison.OrdinalIgnoreCase))
                candidates.Add(Path.Combine(connectorFolder, Path.GetFileName(e.Path)));
            foreach (var c in candidates)
            {
                try
                {
                    if (!File.Exists(c) || new FileInfo(c).Length != e.Size) continue;
                    if (!hashes.TryGetValue(c, out var h)) hashes[c] = h = UpdateResume.Sha256Of(c);
                    if (!h.Equals(e.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                    found[e.Path] = c;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return found;
    }

    /// <summary>
    /// Builds <paramref name="folder"/> from the list: local copies where they match, downloads (through
    /// <paramref name="fetch"/>, given a file's address) for the rest, each checked against its hash. Files already in
    /// <paramref name="folder"/> from an earlier try are kept when their hash matches. <paramref name="report"/> gets
    /// (bytes downloaded, bytes to download). Returns the bytes downloaded; throws on any failure.
    /// </summary>
    public static async Task<long> AssembleAsync(IReadOnlyList<Entry> files, IReadOnlyDictionary<string, string> local, string folder,
        string zipUrl, Func<string, CancellationToken, Task<Stream>> fetch, Action<long, long?> report, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var fetched = 0L;
        var toFetch = files.Where(e => !local.ContainsKey(e.Path)).GroupBy(e => e.Sha256).Sum(g => g.First().Size);
        var done = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // hash -> a file already written this time
        foreach (var e in files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(folder, e.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && Matches(target, e)) { done[e.Sha256] = target; continue; }
            var temp = target + ".new";
            if (local.TryGetValue(e.Path, out var source)) File.Copy(source, temp, true);
            else if (done.TryGetValue(e.Sha256, out var same)) File.Copy(same, temp, true);
            else
            {
                using (var download = await fetch(FileUrl(zipUrl, e.Sha256), ct).ConfigureAwait(false))
                using (var gz = new GZipStream(download, CompressionMode.Decompress))
                using (var output = File.Create(temp))
                    await gz.CopyToAsync(output, 81920, ct).ConfigureAwait(false);
                fetched += e.Size;
                report(fetched, toFetch);
            }
            if (!Matches(temp, e))
            {
                File.Delete(temp);
                throw new InvalidDataException($"{e.Path} doesn't match its published checksum.");
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            done[e.Sha256] = target;
        }
        return fetched;
    }

    private static bool Matches(string path, Entry e) =>
        new FileInfo(path).Length == e.Size && UpdateResume.Sha256Of(path).Equals(e.Sha256, StringComparison.OrdinalIgnoreCase);
}
