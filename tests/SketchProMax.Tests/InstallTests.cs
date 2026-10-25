using System.Xml.Linq;
using static SketchProMax.Update.AddinFiles;

/// <summary>What Revit sees of the add-in stays put between releases: the fixed loader path, one entry, no needless writes.</summary>
internal static class InstallTests
{
    public static void Run(string? repoRoot, Action<string, bool, string> check)
    {
        // Loader replacement: only when the bytes differ, never by an older loader.
        var v30 = new Version(0, 30, 0, 0);
        var v31 = new Version(0, 31, 0, 0);
        check("loader: identical bytes are left alone", DecideLoader("AB", v31, "ab", v30) == LoaderAction.Keep, "");
        check("loader: none installed is copied", DecideLoader("AB", v30, null, null) == LoaderAction.Replace, "");
        check("loader: a changed loader replaces the old one", DecideLoader("AB", v31, "CD", v30) == LoaderAction.Replace, "");
        check("loader: same version, other bytes is kept (no ping-pong between rebuilds)", DecideLoader("AB", v30, "CD", v30) == LoaderAction.Keep
            && DecideLoader("CD", v30, "AB", v30) == LoaderAction.Keep, "");
        check("loader: unknown versions with other bytes replace", DecideLoader("AB", null, "CD", v30) == LoaderAction.Replace, "");
        check("loader: an older core never puts back an older loader", DecideLoader("AB", v30, "CD", v31) == LoaderAction.KeepNewer, "");
        check("loader: nothing to copy", DecideLoader(null, null, "CD", v30) == LoaderAction.Keep, "");

        // The PDF engine on Revit 2024 (.NET Framework): LoadLibrary adds ".dll" only to a name with no extension, so the
        // DllImport name must end in .dll there ("Unable to load DLL 'SketchProMax.pdfium'" on 0.35.3 to 0.35.5).
        var engine = repoRoot is null ? "" : File.ReadAllText(Path.Combine(repoRoot, "src", "SketchProMax", "Tools", "PdfEngine.cs"));
        check("pdf engine: the .NET Framework build imports SketchProMax.pdfium.dll by its full file name",
            System.Text.RegularExpressions.Regex.IsMatch(engine, @"#else\s+internal const string Library = ""SketchProMax\.pdfium\.dll"";")
            && engine.Contains("internal const string FileName = \"SketchProMax.pdfium.dll\";") && engine.Contains("Path.Combine(folder, FileName)"), "");

        // The shipped manifest is the settled one: one entry, the fixed loader path, the ClientId Revit trusts it under.
        var shippedPath = repoRoot is null ? null : Path.Combine(repoRoot, "src", "SketchProMax", ManifestName);
        if (shippedPath is not null && File.Exists(shippedPath))
        {
            var shipped = File.ReadAllText(shippedPath);
            check("manifest: shipped one names the fixed loader path", IsSettled(shipped), "");
            check("manifest: shipped one keeps the ClientId", XDocument.Parse(shipped).Descendants("ClientId").Single().Value == ClientId, "");
            check("manifest: settling the shipped one changes nothing", SameManifest(shipped, Settle(shipped)), Settle(shipped) ?? "null");
        }

        // A 0.26 to 0.29 manifest that the updater pointed at a version folder moves to the fixed path, once.
        const string head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<RevitAddIns>\n";
        string Entry(string assembly, string cls) =>
            $"  <AddIn Type=\"Application\">\n    <Name>Sketch ProMax</Name>\n    <Assembly>{assembly}</Assembly>\n" +
            $"    <FullClassName>{cls}</FullClassName>\n    <ClientId>{ClientId}</ClientId>\n    <VendorId>SPMX</VendorId>\n  </AddIn>\n";
        var versioned = head + Entry(@"C:\Users\u\AppData\Local\SketchProMax\versions\0.29.0\Revit2025\SketchProMax.Loader.dll", LoaderClass) + "</RevitAddIns>";
        var settled = Settle(versioned);
        check("manifest: version-folder path moves to the fixed one", settled is not null && IsSettled(settled) && !SameManifest(versioned, settled), settled ?? "null");
        check("manifest: settling twice writes nothing the second time", SameManifest(settled, Settle(settled)), "");
        var core = head + Entry(@"SketchProMax\SketchProMax.dll", "SketchProMax.App") + "</RevitAddIns>";
        check("manifest: one naming the core is moved to the loader", IsSettled(Settle(core)), Settle(core) ?? "null");
        var twice = head + Entry(@"SketchProMax\SketchProMax.Loader.dll", LoaderClass) + Entry(@"x\SketchProMax.dll", "SketchProMax.App") + "</RevitAddIns>";
        check("manifest: a second Sketch ProMax entry is dropped", IsSettled(Settle(twice)) && !IsSettled(twice), Settle(twice) ?? "null");
        var other = head + Entry(@"SketchProMax\SketchProMax.Loader.dll", LoaderClass)
                         + "  <AddIn Type=\"Command\">\n    <Assembly>Other.dll</Assembly>\n    <ClientId>11111111-2222-3333-4444-555555555555</ClientId>\n  </AddIn>\n</RevitAddIns>";
        check("manifest: another add-in's entry is kept", Settle(other) is { } o && o.Contains("Other.dll") && o.Split("<AddIn ").Length == 3, Settle(other) ?? "null");
        check("manifest: whitespace and line endings don't count as a change",
            SameManifest(settled, settled!.Replace("\n", "\r\n").Replace("<Assembly>", "<Assembly> ")), "");
        check("manifest: a different path does count", !SameManifest(settled, settled.Replace("SketchProMax\\SketchProMax.Loader.dll", "Elsewhere\\SketchProMax.Loader.dll")), "");
        check("manifest: unreadable text is left alone", Settle("not xml") is null && Settle("") is null && !SameManifest("x", "x"), "");

        // Other .addin files that would load Sketch ProMax a second time are found; other add-ins are not.
        check("duplicates: our ClientId or our DLLs", NamesSketchProMax(versioned) && NamesSketchProMax(core)
            && NamesSketchProMax(head + Entry(@"D:\dev\bin\SketchProMax.Loader.dll", "X") + "</RevitAddIns>"), "");
        check("duplicates: another add-in is not ours", !NamesSketchProMax(head
            + "  <AddIn Type=\"Application\">\n    <Assembly>SketchPro\\SketchPro.dll</Assembly>\n    <ClientId>11111111-2222-3333-4444-555555555555</ClientId>\n  </AddIn>\n</RevitAddIns>"), "");

        check("duplicates: a file of only our entries may be set aside", OnlySketchProMax(versioned) && OnlySketchProMax(twice), "");
        check("duplicates: a file that also loads another add-in is left alone", NamesSketchProMax(other) && !OnlySketchProMax(other), "");
        check("duplicates: an empty or unreadable file is not ours", !OnlySketchProMax(head + "</RevitAddIns>") && !OnlySketchProMax("not xml"), "");

        // The loader's release (its fixed file version) must be at least the release the core expects its ribbon from.
        var csproj = repoRoot is null ? null : Path.Combine(repoRoot, "src", "SketchProMax.Loader", "SketchProMax.Loader.csproj");
        if (csproj is not null && File.Exists(csproj))
        {
            var loaderVersion = XDocument.Load(csproj).Descendants("LoaderVersion").FirstOrDefault()?.Value;
            check("loader: LoaderVersion is set and not older than LoaderCompat.RibbonChangedIn",
                Version.TryParse(loaderVersion, out var lv) && lv >= SketchProMax.UI.LoaderCompat.RibbonChangedIn, loaderVersion ?? "missing");
        }
    }
}
