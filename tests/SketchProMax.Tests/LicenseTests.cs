using System.Security.Cryptography;
using SketchProMax.Licensing;

/// <summary>Machine-locked license keys: PC codes, signing and checking, the owner's changes, and the License manager's list.</summary>
internal static class LicenseTests
{
    // Made with the owner's real signing secret (not in this repository) for a made-up PC code: proves the built-in public
    // key and the key format match what the owner's tools make.
    private const string RealKey = "SPM1.MXxNTTlGN0Y4OHxTUE0tVEVTVC0wMDAwLUFCQ0R8VGVzdCBQZXJzb258MjAyNi0xMC0yM3wyMDMwLTAxLTMx.LwwEbUCSRLPxG2IVG4VLn19xmOxoYVhhgg-KDulOIeyN5isyiKYwHARik7LkOnIvexx52SWlgL99rpfKB_H0Sw";

    public static void Run(string root, Action<string, bool, string> check)
    {
        var today = new DateTime(2026, 10, 23);
        var code = LicenseKey.PcCode("36953C6F-979D-44BC-88FA-E3F96DB72BBB");
        check("license: PC code form", System.Text.RegularExpressions.Regex.IsMatch(code, "^SPM-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$"), code);
        check("license: PC code ignores case and differs per PC", code == LicenseKey.PcCode("36953c6f-979d-44bc-88fa-e3f96db72bbb")
            && code != LicenseKey.PcCode("36953c6f-979d-44bc-88fa-e3f96db72bbc") && LicenseKey.PcCode("") == "", code);
        check("license: typed codes are forgiven", LicenseKey.NormalizeCode(" spm 7k2q-9xw4-m3td ") == "SPM-7K2Q-9XW4-M3TD"
            && LicenseKey.NormalizeCode("7K2Q9XW4M3TO") == "SPM-7K2Q-9XW4-M3T0" && LicenseKey.NormalizeCode("SPM-7K2Q") is null
            && LicenseKey.NormalizeCode("SPM-7K2Q-9XW4-M3T!") is null, LicenseKey.NormalizeCode(" spm 7k2q-9xw4-m3td ") ?? "null");
        check("license: the owner's PC is recognised, others are not", LicenseKey.IsOwner("36953c6f-979d-44bc-88fa-e3f96db72bbb")
            && LicenseKey.IsOwner("36953C6F-979D-44BC-88FA-E3F96DB72BBB") && !LicenseKey.IsOwner("36953c6f-979d-44bc-88fa-e3f96db72bbc") && !LicenseKey.IsOwner(""), "");

        var real = LicenseKey.Read(RealKey);
        check("license: a key from the owner's signing secret is valid on its PC", real is { Name: "Test Person", PcCode: "SPM-TEST-0000-ABCD" }
            && real.Ends == new DateTime(2030, 1, 31) && LicenseKey.Verify(RealKey, "SPM-TEST-0000-ABCD", today) == LicenseKey.Check.Valid, real?.ToString() ?? "null");
        check("license: the same key on another PC fails", LicenseKey.Verify(RealKey, code, today) == LicenseKey.Check.WrongPc, "");
        check("license: the key ends after its end date", LicenseKey.Verify(RealKey, "SPM-TEST-0000-ABCD", new DateTime(2030, 1, 31)) == LicenseKey.Check.Valid
            && LicenseKey.Verify(RealKey, "SPM-TEST-0000-ABCD", new DateTime(2030, 2, 1)) == LicenseKey.Check.Expired, "");
        check("license: pasted with spaces and line breaks", LicenseKey.Verify(" " + RealKey.Insert(40, "\r\n ") + "\n", "SPM-TEST-0000-ABCD", today) == LicenseKey.Check.Valid, "");
        var payloadAt = LicenseKey.Prefix.Length + 30;
        var tampered = RealKey.Substring(0, payloadAt) + (RealKey[payloadAt] == 'A' ? 'B' : 'A') + RealKey.Substring(payloadAt + 1);
        check("license: an edited key is refused", LicenseKey.Verify(tampered, "SPM-TEST-0000-ABCD", today) == LicenseKey.Check.Invalid
            && LicenseKey.Verify("hello", code, today) == LicenseKey.Check.Invalid && LicenseKey.Verify("", code, today) == LicenseKey.Check.Invalid, "");

        // Someone else's signing key can't make keys this add-in takes.
        using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var forged = LicenseKey.Sign(new LicenseKey.License("ABCDEFGH", code, "X", today, null), other);
            check("license: a key signed by anyone else is refused", LicenseKey.Verify(forged, code, today) == LicenseKey.Check.Invalid
                && LicenseKey.Verify(forged, code, today, verifier: other) == LicenseKey.Check.Valid, "");
            var p = other.ExportParameters(true);
            var secret = $"SPMSIGN1:{Convert.ToBase64String(p.D!)}:{Convert.ToBase64String(p.Q.X!)}:{Convert.ToBase64String(p.Q.Y!)}";
            check("license: only the matching secret is accepted as the owner's", LicenseKey.Signer(secret) is not null && !LicenseKey.MatchesBuiltIn(secret)
                && LicenseKey.Signer("nonsense") is null, "");

            // The owner's changes, published online by key id.
            var changes = LicenseChanges.Parse("# comment\nABCDEFGH ends 2026-11-01\nZZZZZZZZ cancelled\nbad line\nABCDEFGH ends 2026-12-01\n");
            var l = new LicenseKey.License("ABCDEFGH", code, "X", today, new DateTime(2027, 1, 1));
            check("license: an earlier end date published online applies", LicenseKey.Verify(l, code, new DateTime(2026, 11, 2), changes) == LicenseKey.Check.Expired
                && LicenseKey.Verify(l, code, new DateTime(2026, 11, 1), changes) == LicenseKey.Check.Valid && LicenseKey.EndsOf(l, changes) == new DateTime(2026, 11, 1), "");
            check("license: a later date online never lengthens a key", LicenseKey.EndsOf(l, LicenseChanges.Parse("ABCDEFGH ends 2030-01-01")) == new DateTime(2027, 1, 1), "");
            check("license: a cancelled key stops", LicenseKey.Verify(l with { Id = "ZZZZZZZZ" }, code, today, changes) == LicenseKey.Check.Cancelled, "");
            check("license: change lines", LicenseChanges.Line("ABCDEFGH", new DateTime(2026, 11, 1)) == "ABCDEFGH ends 2026-11-01"
                && LicenseChanges.Line("ABCDEFGH", null) == "ABCDEFGH cancelled", "");
            check("license: publishing appends once", ChangesPublisher.Append("# head\nA cancelled", ["A cancelled", "B ends 2026-11-01", "B ends 2026-11-01"])
                == "# head\nA cancelled\nB ends 2026-11-01\n", ChangesPublisher.Append("# head\nA cancelled", ["A cancelled", "B ends 2026-11-01"]));

            // The License manager's list.
            var list = new IssuedList();
            var first = list.Issue(secret, "spm-7k2q-9xw4-m3td", "  Site  engineer | A ", new DateTime(2027, 1, 1), "note", today);
            check("license manager: makes a key for the PC code", first.PcCode == "SPM-7K2Q-9XW4-M3TD" && first.Name == "Site engineer / A"
                && LicenseKey.Verify(first.Key, "SPM-7K2Q-9XW4-M3TD", today, verifier: other) == LicenseKey.Check.Valid, first.Name);
            var threw = new List<string>();
            foreach (var (c, n, e) in new[] { ("bad", "A", (DateTime?)null), ("SPM-7K2Q-9XW4-M3TD", " ", null), ("SPM-7K2Q-9XW4-M3TD", "A", today.AddDays(-1)) })
                try { list.Issue(secret, c, n, e, "", today); } catch (ArgumentException ex) { threw.Add(ex.Message); }
            check("license manager: wrong inputs are explained", threw.Count == 3, string.Join(" / ", threw));
            var longer = list.ChangeEnd(secret, first, new DateTime(2028, 1, 1), today);
            check("license manager: a later date makes a new key and needs nothing published", longer.Ends == new DateTime(2028, 1, 1)
                && first.ReplacedBy == longer.Id && first.PendingChange == "" && longer.Active && !first.Active, first.PendingChange);
            var shorter = list.ChangeEnd(secret, longer, new DateTime(2026, 12, 1), today);
            check("license manager: an earlier date needs the old key's change published", longer.PendingChange == $"{longer.Id} ends 2026-12-01" && shorter.Ends == new DateTime(2026, 12, 1), longer.PendingChange);
            list.Cancel(shorter);
            check("license manager: cancel", shorter.Status(today) == "Cancelled" && shorter.PendingChange == $"{shorter.Id} cancelled", shorter.PendingChange);
            var file = Path.Combine(Path.GetTempPath(), "spm-issued-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                list.Save(file);
                var back = IssuedList.Load(file);
                check("license manager: the list is kept", back.Entries.Count == 3 && back.Entries[2].PendingChange == shorter.PendingChange && back.Entries[0].Key == first.Key, back.Entries.Count.ToString());
            }
            finally { File.Delete(file); }
            check("license manager: Excel export", list.Csv(today).Split('\n')[0].StartsWith("Name,PC code,Issued,Ends,Status") && list.Csv(today).Contains("\"Site engineer / A\""), "");
        }

        // Wiring: the License button, the gate on buttons, tools and the chat, and no signing secret in the repository.
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));
        check("license: the ribbon has the License button", Read("src/SketchProMax/UI/CoreRibbonManifest.cs").Contains("\"license\""), "");
        var entry = Read("src/SketchProMax/CoreEntry.cs");
        check("license: buttons are blocked without a key, License and updates are not", entry.Contains("LicenseWindow.BlockIfLocked()") && entry.Contains("\"license\" => LicenseWindow.Command()")
            && System.Text.RegularExpressions.Regex.IsMatch(entry, "WithoutLicense[^;]*\"license\"[^;]*\"update-button\""), "");
        check("license: tools are off without a key", Read("src/SketchProMax/Bridge/BridgeServer.cs").Contains("LicenseState.Allowed"), "");
        check("license: the chat sends nothing without a key", Read("src/SketchProMax/Chat/ChatPane.cs").Contains("LicenseWindow.BlockIfLocked()"), "");
        var leaked = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && new FileInfo(f).Length < 2_000_000)
            .Where(f => { try { return File.ReadAllText(f).Contains("SPMSIGN1:" + "6"); } catch (IOException) { return false; } })
            .ToList();
        check("license: the owner's signing secret is not in the repository", leaked.Count == 0, string.Join(", ", leaked));
    }
}
