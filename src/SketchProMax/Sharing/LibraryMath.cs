using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SketchProMax.Sharing;

/// <summary>
/// The owner's library, locked (the owner's ask, 2026-11-25: "publish the codes and ready-made tools to GitHub ... so the
/// library becomes part of the application itself", locked because the repository is public). The owner's PC packs its
/// ready-made tools, recipes, kept code, job recipes and checklist into one zip, locks it with the library key (made from
/// the signing secret, LicenseKey.LibraryKey) and uploads it to the repository's "library" release. Every key the owner
/// makes carries the library key, so a licensed PC can open it; anyone else downloads only scrambled bytes.
///
/// Locked file: "SPML1" + 16-byte IV + AES-256-CBC ciphertext + HMAC-SHA256 (over everything before it), with an
/// encryption key and a MAC key each derived from the library key. Pure logic, no Revit and no files (the tests run it).
/// </summary>
internal static class LibraryMath
{
    public const string Magic = "SPML1";
    public const string Tag = "library";
    public const string Asset = "library.bin";
    /// <summary>A small text asset beside it: the fingerprint, when it was made and what it holds, read before the big file.</summary>
    public const string StampAsset = "library.txt";

    /// <summary>The largest locked library downloaded or opened (a broken or hostile file is not read into memory whole).</summary>
    public const long MaxBytes = 400L * 1024 * 1024;

    /// <summary>What goes in, relative to %APPDATA%\SketchProMax ("/"-separated): folders with the files kept in them.</summary>
    public static readonly (string Folder, string Pattern, bool Deep)[] Parts =
    [
        ("learned-tools", "*.json", false),
        ("recipes", "*.json", false),
        ("worked-code", "*", true),
        ("job-recipes", "*.json", true),
    ];

    /// <summary>The checklist as imported or edited on the owner's PC (ChecklistStore.SavedPath's file name).</summary>
    public const string ChecklistFile = "pre-submission checklist.json";

    /// <summary>Files in a part that are this PC's own bookkeeping, not library: left out.</summary>
    public static bool Leave(string relative)
    {
        var name = relative.Substring(relative.LastIndexOf('/') + 1);
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".moved", StringComparison.OrdinalIgnoreCase)
            || name is "rebuilt from history.txt" or "merged.txt" or "from history.txt";
    }

    /// <summary>The 32 bytes of a library key (Licensing.LicenseKey.LibraryBytes without the null); the tests use it.</summary>
    internal static byte[] LibraryBytesOf(string key) => Licensing.LicenseKey.LibraryBytes(key) ?? throw new ArgumentException("Not a library key.");

    public static byte[] Lock(byte[] plain, byte[] key)
    {
        var (enc, mac) = Keys(key);
        using var aes = Aes.Create();
        aes.Key = enc;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        byte[] cipher;
        using (var e = aes.CreateEncryptor()) cipher = e.TransformFinalBlock(plain, 0, plain.Length);
        var head = Encoding.ASCII.GetBytes(Magic);
        var body = new byte[head.Length + 16 + cipher.Length];
        Buffer.BlockCopy(head, 0, body, 0, head.Length);
        Buffer.BlockCopy(aes.IV, 0, body, head.Length, 16);
        Buffer.BlockCopy(cipher, 0, body, head.Length + 16, cipher.Length);
        using var h = new HMACSHA256(mac);
        var tag = h.ComputeHash(body);
        var all = new byte[body.Length + tag.Length];
        Buffer.BlockCopy(body, 0, all, 0, body.Length);
        Buffer.BlockCopy(tag, 0, all, body.Length, tag.Length);
        return all;
    }

    /// <summary>The plain bytes, or null when the file isn't a locked library or this key doesn't open it (or it was changed).</summary>
    public static byte[]? Unlock(byte[] data, byte[] key)
    {
        var head = Encoding.ASCII.GetBytes(Magic);
        if (data.Length < head.Length + 16 + 16 + 32) return null;
        for (var i = 0; i < head.Length; i++) if (data[i] != head[i]) return null;
        var (enc, mac) = Keys(key);
        using (var h = new HMACSHA256(mac))
        {
            var want = h.ComputeHash(data, 0, data.Length - 32);
            var diff = 0;
            for (var i = 0; i < 32; i++) diff |= want[i] ^ data[data.Length - 32 + i];
            if (diff != 0) return null;
        }
        using var aes = Aes.Create();
        aes.Key = enc;
        var iv = new byte[16];
        Buffer.BlockCopy(data, head.Length, iv, 0, 16);
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        try
        {
            using var d = aes.CreateDecryptor();
            return d.TransformFinalBlock(data, head.Length + 16, data.Length - head.Length - 16 - 32);
        }
        catch (CryptographicException) { return null; }
    }

    private static (byte[] Enc, byte[] Mac) Keys(byte[] key)
    {
        if (key is not { Length: 32 }) throw new ArgumentException("A library key is 32 bytes.");
        using var h = new HMACSHA256(key);
        return (h.ComputeHash(Encoding.ASCII.GetBytes("enc")), h.ComputeHash(Encoding.ASCII.GetBytes("mac")));
    }

    /// <summary>The files as one zip (relative "/"-separated paths), in path order so the same files give the same fingerprint.</summary>
    public static byte[] Pack(IEnumerable<(string Relative, byte[] Data)> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (rel, data) in files.OrderBy(f => f.Relative, StringComparer.Ordinal))
            {
                if (!SafeRelative(rel)) continue;
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    /// <summary>The files in a library zip; a path that would leave the folder is skipped.</summary>
    public static List<(string Relative, byte[] Data)> Unpack(byte[] zipBytes)
    {
        var list = new List<(string, byte[])>();
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var rel = entry.FullName.Replace('\\', '/');
            if (rel.EndsWith("/", StringComparison.Ordinal) || !SafeRelative(rel)) continue;
            using var s = entry.Open();
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            list.Add((rel, copy.ToArray()));
        }
        return list;
    }

    public static bool SafeRelative(string rel) =>
        rel.Length > 0 && !rel.StartsWith("/", StringComparison.Ordinal) && rel.IndexOf(':') < 0 &&
        rel.Split('/').All(p => p.Length > 0 && p != "." && p != "..");

    /// <summary>A short fingerprint of the files (paths and contents), to tell whether anything changed.</summary>
    public static string Fingerprint(IEnumerable<(string Relative, byte[] Data)> files)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var (rel, data) in files.OrderBy(f => f.Relative, StringComparer.Ordinal))
            sb.Append(rel).Append('\n').Append(Hex(sha.ComputeHash(data))).Append('\n');
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16);
    }

    public static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

    /// <summary>
    /// What to do with a ready-made tool or recipe from the library on a PC that got it: "install" when the PC doesn't have
    /// it and never had it from the library, "update" when the copy there is still the one the library gave, "keep"
    /// otherwise (the PC's own of that name, one changed there, or one deleted there on purpose). From the team folder of
    /// 0.35.70 (WorkedCode.Decide then).
    /// </summary>
    public static string Decide(string? localHash, string? givenHash, string libraryHash) =>
        localHash is null ? (givenHash is null ? "install" : "keep")
        : localHash == libraryHash ? "keep"
        : givenHash is not null && localHash == givenHash ? "update" : "keep";

    /// <summary>The stamp asset: "fingerprint|made yyyy-MM-ddTHH:mm:ssZ|summary".</summary>
    public static string Stamp(string fingerprint, DateTime madeUtc, string summary) =>
        fingerprint + "|" + madeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) + "|" + summary.Replace('|', '/').Replace('\n', ' ');

    public static (string Fingerprint, string Made, string Summary)? ReadStamp(string? text)
    {
        var f = (text ?? "").Trim().Split(['|'], 3);
        return f.Length == 3 && f[0].Length == 16 ? (f[0], f[1], f[2]) : null;
    }

    /// <summary>What a library holds, in words ("12 ready-made tools, 3 recipes, 4,210 kept codes, 85 done jobs, the checklist").</summary>
    public static string Summary(IEnumerable<string> relatives)
    {
        var list = relatives.ToList();
        int Count(Func<string, bool> f) => list.Count(f);
        var tools = Count(r => r.StartsWith("learned-tools/", StringComparison.Ordinal));
        var recipes = Count(r => r.StartsWith("recipes/", StringComparison.Ordinal));
        var code = Count(r => r.StartsWith("worked-code/", StringComparison.Ordinal) && r.EndsWith(".cs", StringComparison.Ordinal));
        var jobs = Count(r => r.StartsWith("job-recipes/steps/", StringComparison.Ordinal));
        var parts = new List<string>
        {
            $"{tools:N0} ready-made tool{(tools == 1 ? "" : "s")}", $"{recipes:N0} recipe{(recipes == 1 ? "" : "s")}",
            $"{code:N0} kept code{(code == 1 ? "" : "s")}", $"{jobs:N0} done job{(jobs == 1 ? "" : "s")}",
        };
        if (list.Contains(ChecklistFile)) parts.Add("the checklist");
        return string.Join(", ", parts);
    }
}
