using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SketchProMax.Licensing;

/// <summary>
/// License keys bound to one PC. A PC's code is a hash of its Windows machine ID (never the ID itself); a key names
/// that code, who it is for and its end date, and is signed with the owner's private signing key. The add-in holds
/// only the public half, so it can check a key but not make one. Pure logic, no Revit and no files (the tests run it).
///
/// Key: "SPM1." + base64url(payload) + "." + base64url(ECDSA P-256 signature, 64 bytes), where payload is
/// "1|id|pc code|name|issued yyyy-MM-dd|ends yyyy-MM-dd or -".
/// </summary>
internal static class LicenseKey
{
    public const string Prefix = "SPM1.";

    /// <summary>The public half of the owner's signing key (P-256 point). The private half is never in this repository.</summary>
    internal const string PublicX = "KcVQyBbKMn5vEbN8NTrmQDqYumNep0YUvj93keOPzYk=";
    internal const string PublicY = "4mzzBuU1OdbDmXBwUdwE6PKjCRQZomhpVilszZUNivY=";

    /// <summary>
    /// The owner's PCs: never locked, and the only PCs with the License manager. SHA-256 of "SketchProMax/owner/" plus the
    /// machine ID, so the IDs themselves are not published.
    /// </summary>
    internal static readonly string[] OwnerHashes =
    [
        "e20b89e45536d9edd3356fbcec7b6132ec910045122da81533dfea0599deaf4f",
    ];

    // Crockford's base 32: no I, L, O or U, so a code read aloud or typed is hard to get wrong.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>"SPM-XXXX-XXXX-XXXX" for a Windows machine ID; "" when there is none.</summary>
    public static string PcCode(string? machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return "";
        var hash = Sha256("SketchProMax/pc/" + machineId!.Trim().ToLowerInvariant());
        var chars = new char[12];
        ulong bits = 0;
        for (var i = 0; i < 8; i++) bits = (bits << 8) | hash[i];
        for (var i = 0; i < 12; i++) chars[i] = Alphabet[(int)((bits >> (64 - 5 * (i + 1))) & 31)];
        var s = new string(chars);
        return $"SPM-{s.Substring(0, 4)}-{s.Substring(4, 4)}-{s.Substring(8, 4)}";
    }

    /// <summary>A typed or pasted PC code in its standard form (spaces, case, missing dashes and look-alike letters forgiven), or null.</summary>
    public static string? NormalizeCode(string? text)
    {
        if (text is null) return null;
        var sb = new StringBuilder();
        var t = text.Trim().ToUpperInvariant();
        if (t.StartsWith("SPM", StringComparison.Ordinal)) t = t.Substring(3);
        foreach (var c in t)
        {
            var ch = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (Alphabet.IndexOf(ch) >= 0) sb.Append(ch);
            else if (c is not ('-' or ' ' or '\t')) return null;
        }
        if (sb.Length != 12) return null;
        var s = sb.ToString();
        return $"SPM-{s.Substring(0, 4)}-{s.Substring(4, 4)}-{s.Substring(8, 4)}";
    }

    public static bool IsOwner(string? machineId) =>
        !string.IsNullOrWhiteSpace(machineId) &&
        Array.IndexOf(OwnerHashes, Hex(Sha256("SketchProMax/owner/" + machineId!.Trim().ToLowerInvariant()))) >= 0;

    /// <summary>What a key says. <see cref="Ends"/> null: no end date.</summary>
    internal sealed record License(string Id, string PcCode, string Name, DateTime Issued, DateTime? Ends);

    internal enum Check { Valid, Expired, WrongPc, Cancelled, Invalid }

    /// <summary>A short random id per key ("K7Q2M9XA"), so a key can be found in the list or cancelled without naming anyone.</summary>
    public static string NewId()
    {
        var bytes = new byte[8];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return new string(bytes.Select(b => Alphabet[b & 31]).ToArray());
    }

    public static string Sign(License license, ECDsa signer)
    {
        var payload = Encoding.UTF8.GetBytes(Payload(license));
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256);
        return Prefix + Base64Url(payload) + "." + Base64Url(signature);
    }

    /// <summary>The license a key carries when its signature is good (whatever PC or date), else null.</summary>
    public static License? Read(string? key, ECDsa? verifier = null)
    {
        if (key is null) return null;
        var k = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!k.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = k.Substring(Prefix.Length).Split('.');
        if (parts.Length != 2) return null;
        byte[] payload, signature;
        try { payload = FromBase64Url(parts[0]); signature = FromBase64Url(parts[1]); }
        catch (FormatException) { return null; }
        if (signature.Length != 64) return null;
        var owned = verifier is null;
        var ecdsa = verifier ?? PublicKey();
        try
        {
            if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return null;
        }
        catch (CryptographicException) { return null; }
        finally { if (owned) ecdsa.Dispose(); }
        return Parse(Encoding.UTF8.GetString(payload));
    }

    /// <summary>Whether <paramref name="key"/> unlocks the PC with <paramref name="pcCode"/> on <paramref name="today"/>.</summary>
    public static Check Verify(string? key, string pcCode, DateTime today, LicenseChanges? changes = null, ECDsa? verifier = null) =>
        Verify(Read(key, verifier), pcCode, today, changes);

    public static Check Verify(License? license, string pcCode, DateTime today, LicenseChanges? changes = null)
    {
        if (license is null) return Check.Invalid;
        if (!string.Equals(license.PcCode, pcCode, StringComparison.Ordinal)) return Check.WrongPc;
        if (changes?.IsCancelled(license.Id) == true) return Check.Cancelled;
        var ends = EndsOf(license, changes);
        if (ends is { } e && today.Date > e.Date) return Check.Expired;
        return Check.Valid;
    }

    /// <summary>The end date after the owner's changes (an earlier date the owner set online wins over the key's own).</summary>
    public static DateTime? EndsOf(License license, LicenseChanges? changes)
    {
        var ends = license.Ends;
        if (changes?.EndsFor(license.Id) is { } changed && (ends is null || changed < ends)) ends = changed;
        return ends;
    }

    internal static string Payload(License l) => string.Join("|",
        "1", l.Id, l.PcCode, Clean(l.Name), l.Issued.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        l.Ends?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-");

    private static License? Parse(string text)
    {
        var f = text.Split('|');
        if (f.Length != 6 || f[0] != "1") return null;
        if (!DateTime.TryParseExact(f[4], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued)) return null;
        DateTime? ends = null;
        if (f[5] != "-")
        {
            if (!DateTime.TryParseExact(f[5], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)) return null;
            ends = e;
        }
        return new License(f[1], f[2], f[3], issued, ends);
    }

    /// <summary>A name as it goes into a key: one line, no field separator.</summary>
    public static string Clean(string name) =>
        string.Join(" ", (name ?? "").Replace('|', '/').Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    internal static ECDsa PublicKey() => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = Convert.FromBase64String(PublicX), Y = Convert.FromBase64String(PublicY) },
    });

    /// <summary>The owner's signing secret, "SPMSIGN1:d:x:y" (base 64), as a signer; null when the text isn't one.</summary>
    public static ECDsa? Signer(string? secret)
    {
        var f = (secret ?? "").Trim().Split(':');
        if (f.Length != 4 || f[0] != "SPMSIGN1") return null;
        try
        {
            return ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = Convert.FromBase64String(f[1]),
                Q = new ECPoint { X = Convert.FromBase64String(f[2]), Y = Convert.FromBase64String(f[3]) },
            });
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return null; }
    }

    /// <summary>True when <paramref name="secret"/> is the private half of the key built into this add-in.</summary>
    public static bool MatchesBuiltIn(string? secret)
    {
        var f = (secret ?? "").Trim().Split(':');
        return f.Length == 4 && f[2] == PublicX && f[3] == PublicY && Signer(secret) is { } s && Works(s);

        static bool Works(ECDsa s)
        {
            using (s)
            {
                var probe = Encoding.UTF8.GetBytes("probe");
                using var pub = PublicKey();
                return pub.VerifyData(probe, s.SignData(probe, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256);
            }
        }
    }

    private static byte[] Sha256(string text)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(text));
    }

    private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}

/// <summary>
/// The owner's later changes to keys already given out, read online (licenses/changes.txt in the repository, fetched at
/// start and kept for offline days): a shorter end date, or a cancelled key. One per line: "id ends yyyy-MM-dd" or
/// "id cancelled"; "#" starts a comment. Lists ids only, never names or PC codes.
/// </summary>
internal sealed class LicenseChanges
{
    private readonly Dictionary<string, DateTime> _ends = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);

    public static readonly LicenseChanges None = new();

    public static LicenseChanges Parse(string? text)
    {
        var c = new LicenseChanges();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var f = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 2 && f[1].Equals("cancelled", StringComparison.OrdinalIgnoreCase)) c._cancelled.Add(f[0]);
            else if (f.Length == 3 && f[1].Equals("ends", StringComparison.OrdinalIgnoreCase) &&
                     DateTime.TryParseExact(f[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                c._ends[f[0]] = c._ends.TryGetValue(f[0], out var had) && had < d ? had : d;
        }
        return c;
    }

    public bool IsCancelled(string id) => _cancelled.Contains(id);

    public DateTime? EndsFor(string id) => _ends.TryGetValue(id, out var d) ? d : null;

    /// <summary>The line the owner sends to have a change published.</summary>
    public static string Line(string id, DateTime? ends) =>
        ends is { } d ? $"{id} ends {d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : $"{id} cancelled";
}
