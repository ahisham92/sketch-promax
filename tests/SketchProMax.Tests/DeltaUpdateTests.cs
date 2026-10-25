using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SketchProMax.Update;

/// <summary>Updates that download only the files that changed, falling back to the whole zip on anything unexpected.</summary>
internal static class DeltaUpdateTests
{
    public static void Run(Action<string, bool, string> check)
    {
        const string zip = "https://github.com/o/r/releases/download/v1.2.3/SketchProMax-1.2.3.zip";
        check("changed files: the list sits next to the zip", DeltaUpdate.ManifestUrl(zip) == "https://github.com/o/r/releases/download/v1.2.3/SketchProMax-1.2.3.files.json", DeltaUpdate.ManifestUrl(zip));
        var h = new string('a', 64);
        check("changed files: each file is named by its hash", DeltaUpdate.FileUrl(zip, h.ToUpperInvariant()) == $"https://github.com/o/r/releases/download/v1.2.3/{h}.gz", DeltaUpdate.FileUrl(zip, h));

        string List(params (string Path, long Size, string Sha)[] files) =>
            "{\"version\":\"1.2.3\",\"files\":[" + string.Join(",", files.Select(f => $"{{\"path\":\"{f.Path}\",\"size\":{f.Size},\"sha256\":\"{f.Sha}\"}}")) + "]}";
        check("changed files: a good list is read", DeltaUpdate.Parse(List(("Revit2025/SketchProMax.dll", 3, h), ("mcp/SketchProMax.Mcp.exe", 4, h)))?.Count == 2, "");
        check("changed files: a list without the core is refused", DeltaUpdate.Parse(List(("mcp/SketchProMax.Mcp.exe", 4, h))) is null, "");
        check("changed files: paths leaving the folder are refused", DeltaUpdate.Parse(List(("Revit2025/SketchProMax.dll", 3, h), ("../evil.dll", 1, h))) is null
            && DeltaUpdate.Parse(List(("Revit2025/SketchProMax.dll", 3, h), ("C:/evil.dll", 1, h))) is null
            && DeltaUpdate.Parse(List(("Revit2025/SketchProMax.dll", 3, h), ("/evil.dll", 1, h))) is null, "");
        check("changed files: a short hash is refused", DeltaUpdate.Parse(List(("Revit2025/SketchProMax.dll", 3, "abc"))) is null, "");
        check("changed files: anything not a list is refused", DeltaUpdate.Parse("<html>") is null && DeltaUpdate.Parse("{}") is null && DeltaUpdate.Parse("{\"files\":[{}]}") is null, "");

        var root = Path.Combine(Path.GetTempPath(), "spm-delta-" + Guid.NewGuid().ToString("N"));
        try
        {
            static string Sha(byte[] b) { using var s = SHA256.Create(); return BitConverter.ToString(s.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }
            byte[] same = Encoding.UTF8.GetBytes("unchanged library"), core = Encoding.UTF8.GetBytes("new core"), exe = Encoding.UTF8.GetBytes("connector");
            var old = Path.Combine(root, "versions", "1.2.2");
            Directory.CreateDirectory(Path.Combine(old, "Revit2025"));
            File.WriteAllBytes(Path.Combine(old, "Revit2025", "Lib.dll"), same);
            File.WriteAllBytes(Path.Combine(old, "Revit2025", "SketchProMax.dll"), Encoding.UTF8.GetBytes("old core"));
            var connector = Path.Combine(root, "mcp");
            Directory.CreateDirectory(connector);
            File.WriteAllBytes(Path.Combine(connector, "SketchProMax.Mcp.exe"), exe);
            var files = new List<DeltaUpdate.Entry>
            {
                new("Revit2025/Lib.dll", same.Length, Sha(same)),
                new("Revit2024/Lib.dll", same.Length, Sha(same)),
                new("Revit2025/SketchProMax.dll", core.Length, Sha(core)),
                new("Revit2024/SketchProMax.dll", core.Length, Sha(core)),
                new("mcp/SketchProMax.Mcp.exe", exe.Length, Sha(exe)),
            };
            var local = DeltaUpdate.LocalCopies(files, new[] { old, Path.Combine(root, "missing") }, connector);
            check("changed files: unchanged installed files are reused", local.ContainsKey("Revit2025/Lib.dll") && local.ContainsKey("mcp/SketchProMax.Mcp.exe")
                && !local.ContainsKey("Revit2025/SketchProMax.dll") && !local.ContainsKey("Revit2024/Lib.dll"), string.Join(", ", local.Keys));

            var served = new Dictionary<string, byte[]> { [DeltaUpdate.FileUrl(zip, Sha(same))] = same, [DeltaUpdate.FileUrl(zip, Sha(core))] = core };
            var asked = new List<string>();
            Task<Stream> Fetch(string url, CancellationToken _)
            {
                asked.Add(url);
                if (!served.TryGetValue(url, out var body)) throw new FileNotFoundException(url);
                var gz = new MemoryStream();
                using (var z = new GZipStream(gz, CompressionMode.Compress, true)) z.Write(body, 0, body.Length);
                gz.Position = 0;
                return Task.FromResult<Stream>(gz);
            }
            var target = Path.Combine(root, "versions", "1.2.3.delta");
            long reported = 0;
            var got = DeltaUpdate.AssembleAsync(files, local, target, zip, Fetch, (b, _) => reported = b, CancellationToken.None).GetAwaiter().GetResult();
            var allThere = files.All(f => File.Exists(Path.Combine(target, f.Path.Replace('/', Path.DirectorySeparatorChar))))
                && File.ReadAllText(Path.Combine(target, "Revit2024", "SketchProMax.dll")) == "new core";
            check("changed files: only the changed files are downloaded, once each", allThere && asked.Count == 1 && got == core.Length && reported == core.Length,
                $"asked {asked.Count}, got {got}");

            asked.Clear();
            DeltaUpdate.AssembleAsync(files, local, target, zip, Fetch, (_, _) => { }, CancellationToken.None).GetAwaiter().GetResult();
            check("changed files: a second try keeps what the first wrote", asked.Count == 0, $"asked {asked.Count}");

            served[DeltaUpdate.FileUrl(zip, Sha(core))] = Encoding.UTF8.GetBytes("tampered");
            var threw = false;
            try { DeltaUpdate.AssembleAsync(files, new Dictionary<string, string>(), Path.Combine(root, "bad"), zip, Fetch, (_, _) => { }, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { threw = true; }
            check("changed files: a file that doesn't match its checksum stops it (whole zip instead)", threw, "");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
