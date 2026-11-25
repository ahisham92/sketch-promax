using System.Security.Cryptography;
using System.Text;
using SketchProMax.Licensing;
using SketchProMax.Sharing;
using SketchProMax.Tools;

/// <summary>The owner's locked library (Sharing/Library, LibraryMath): the key in licenses, the lock, the zip, what is installed.</summary>
internal static class LibraryTests
{
    public static void Run(string root, Action<string, bool, string> check)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = signer.ExportParameters(true);
        var secret = $"SPMSIGN1:{Convert.ToBase64String(p.D!)}:{Convert.ToBase64String(p.Q.X!)}:{Convert.ToBase64String(p.Q.Y!)}";
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = other.ExportParameters(true);
        var otherSecret = $"SPMSIGN1:{Convert.ToBase64String(q.D!)}:{Convert.ToBase64String(q.Q.X!)}:{Convert.ToBase64String(q.Q.Y!)}";

        var libKey = LicenseKey.LibraryKey(secret);
        check("library: the key comes from the signing secret, always the same", libKey.Length > 40 && libKey == LicenseKey.LibraryKey(secret)
            && libKey != LicenseKey.LibraryKey(otherSecret) && LicenseKey.LibraryKey("nonsense") == "" && LicenseKey.LibraryBytes(libKey) is { Length: 32 }, libKey);

        // A key carries it (seventh field); a key without it still reads.
        var today = new DateTime(2026, 11, 25);
        var lic = new LicenseKey.License("ABCDEFGH", "SPM-TEST-0000-ABCD", "Test Friend", today, null, libKey);
        var key = LicenseKey.Sign(lic, signer);
        var read = LicenseKey.Read(key, signer);
        check("library: a key carries the library key", read is not null && read.Library == libKey
            && LicenseKey.Verify(key, "SPM-TEST-0000-ABCD", today, verifier: signer) == LicenseKey.Check.Valid, read?.ToString() ?? "null");
        var oldKey = LicenseKey.Sign(lic with { Library = "" }, signer);
        check("library: a key from before still reads, without it", LicenseKey.Read(oldKey, signer) is { Library: "" }
            && !LicenseKey.Payload(lic with { Library = "" }).EndsWith("|", StringComparison.Ordinal), LicenseKey.Payload(lic with { Library = "" }));
        var list = new IssuedList();
        var issued = list.Issue(secret, "SPM-TEST-0000-ABCD", "Test Friend", null, "", today);
        check("library: every key the License manager makes opens the library", LicenseKey.Read(issued.Key, signer)?.Library == libKey, issued.Key);

        // The lock.
        var files = new List<(string, byte[])>
        {
            ("learned-tools/pile_tags.json", Encoding.UTF8.GetBytes("{\"name\":\"pile_tags\"}")),
            ("worked-code/Floors/abc.cs", Encoding.UTF8.GetBytes("return 1;")),
            ("worked-code/index.json", Encoding.UTF8.GetBytes("[]")),
            ("job-recipes/steps/k1.json", Encoding.UTF8.GetBytes("[]")),
            (LibraryMath.ChecklistFile, Encoding.UTF8.GetBytes("{}")),
            ("../outside.txt", Encoding.UTF8.GetBytes("no")),
        };
        var zip = LibraryMath.Pack(files);
        var bytes = LibraryMath.LibraryBytesOf(libKey);
        var locked = LibraryMath.Lock(zip, bytes);
        var opened = LibraryMath.Unlock(locked, bytes);
        var back = opened is null ? [] : LibraryMath.Unpack(opened);
        check("library: locked and opened again, a path outside the folder left out", back.Count == 5 && back.All(b => b.Relative != "../outside.txt")
            && Encoding.UTF8.GetString(back.First(b => b.Relative.EndsWith("abc.cs", StringComparison.Ordinal)).Data) == "return 1;", string.Join(",", back.Select(b => b.Relative)));
        check("library: the lock hides the contents", Encoding.UTF8.GetString(locked).IndexOf("return 1;", StringComparison.Ordinal) < 0
            && Encoding.UTF8.GetString(locked).IndexOf("pile_tags", StringComparison.Ordinal) < 0, "");
        check("library: another key doesn't open it", LibraryMath.Unlock(locked, LibraryMath.LibraryBytesOf(LicenseKey.LibraryKey(otherSecret))) is null, "");
        var changed = (byte[])locked.Clone();
        changed[changed.Length / 2] ^= 1;
        check("library: a changed file is refused", LibraryMath.Unlock(changed, bytes) is null && LibraryMath.Unlock(new byte[10], bytes) is null, "");
        check("library: the fingerprint follows the contents, not the order", LibraryMath.Fingerprint(files) == LibraryMath.Fingerprint(Enumerable.Reverse(files))
            && LibraryMath.Fingerprint(files) != LibraryMath.Fingerprint(files.Take(3)), LibraryMath.Fingerprint(files));
        var stamp = LibraryMath.Stamp(LibraryMath.Fingerprint(files), new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Utc), LibraryMath.Summary(files.Select(f => f.Item1)));
        check("library: the stamp reads back with what it holds", LibraryMath.ReadStamp(stamp) is { } st && st.Fingerprint == LibraryMath.Fingerprint(files)
            && st.Summary == "1 ready-made tool, 0 recipes, 1 kept code, 1 done job, the checklist" && LibraryMath.ReadStamp("junk") is null, stamp);
        check("library: this PC's bookkeeping stays out", LibraryMath.Leave("worked-code/merged.txt") && LibraryMath.Leave("job-recipes/index.json.tmp")
            && !LibraryMath.Leave("worked-code/index.json"), "");

        // What a licensed PC does with a ready-made tool or recipe that arrives.
        check("library: a new tool is installed", LibraryMath.Decide(null, null, "L") == "install", "");
        check("library: the PC's own tool of that name is kept", LibraryMath.Decide("MINE", null, "L") == "keep", "");
        check("library: one still as given is updated", LibraryMath.Decide("OLD", "OLD", "L") == "update", "");
        check("library: one changed on that PC is kept", LibraryMath.Decide("EDITED", "OLD", "L") == "keep", "");
        check("library: one deleted there on purpose stays deleted", LibraryMath.Decide(null, "OLD", "L") == "keep", "");

        // Kept code and done jobs from the library are found beside the PC's own, never over them.
        var mine = new List<WorkedCode.Entry> { new() { Id = "a", Description = "trim beams" } };
        var shared = new List<WorkedCode.Entry> { new() { Id = "a", Description = "other" }, new() { Id = "b", Description = "tag piles" }, new() { Id = "c", Description = "" } };
        var all = WorkedCode.WithShared(mine, shared);
        check("library: kept code from the library adds to the PC's own", all.Count == 2 && all[0].Description == "trim beams" && !all[0].Shared && all[1] is { Id: "b", Shared: true }
            && WorkedCode.Line(all[1]).Contains("shared library"), string.Join(",", all.Select(e => e.Id)));
        var jobs = JobRecipes.WithShared([new JobRecipes.Recipe { Key = "k1", Steps = 3 }],
            [new JobRecipes.Recipe { Key = "k1", Steps = 5 }, new JobRecipes.Recipe { Key = "k2", Steps = 4 }, new JobRecipes.Recipe { Key = "k3", Steps = -1 }]);
        check("library: done jobs from the library add to the PC's own (only ones with steps)", jobs.Count == 2 && jobs[1] is { Key: "k2", Shared: true }, string.Join(",", jobs.Select(j => j.Key)));

        // The installer shows the same PC code as the add-in.
        check("library: the installer's PC code matches the add-in's", LicenseKey.PcCode("11111111-2222-3333-4444-555555555555") == "SPM-YEX9-X05P-6G8D", LicenseKey.PcCode("11111111-2222-3333-4444-555555555555"));
        var install = File.ReadAllText(Path.Combine(root, "install.ps1"));
        check("library: the installer shows and copies the PC code", install.Contains("SketchProMax/pc/") && install.Contains("Set-Clipboard")
            && install.Contains(LicenseKey.OwnerHashes[0]), "");
    }
}
