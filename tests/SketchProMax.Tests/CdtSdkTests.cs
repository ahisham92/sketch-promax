using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>
/// The CDT SDK (Dar, private repository) inside Sketch ProMax: built in from the pinned commit, merged into
/// SketchProMax.dll (no CDT.Wpf.dll beside it for another add-in to swap), its theme XAML inside the DLL, and every
/// window themed by it through Brand (UI/CdtTheme.cs). No SDK source is committed to this public repository.
/// </summary>
internal static class CdtSdkTests
{
    public static void Run(string repoRoot, Action<string, bool, string> check)
    {
        var refFile = Path.Combine(repoRoot, "cdt-sdk.ref");
        var pin = File.Exists(refFile) ? File.ReadAllText(refFile).Trim() : "";
        check("cdt sdk: the build pins one SDK commit (cdt-sdk.ref)", System.Text.RegularExpressions.Regex.IsMatch(pin, "^[0-9a-f]{40}$"), pin);

        var ignore = File.ReadAllText(Path.Combine(repoRoot, ".gitignore"));
        check("cdt sdk: its source folder is git-ignored (the repository is public)", ignore.Split('\n').Any(l => l.Trim() == "external/"), "");

        var release = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));
        check("cdt sdk: the release build checks it out with the CDT_SDK_TOKEN secret",
            release.Contains("Dar-Structural-Computational-Design/CDT-SDK") && release.Contains("secrets.CDT_SDK_TOKEN") && release.Contains("external/CDT-SDK"), "");

        var brand = File.ReadAllText(Path.Combine(repoRoot, "src", "SketchProMax", "UI", "Brand.cs"));
        check("cdt sdk: every themed window gets its style sheet and palette",
            brand.Contains("CdtTheme.Styles") && brand.Contains("CdtTheme.WritePalette") && brand.Contains("\"CDT.PrimaryButton\"") && brand.Contains("\"CDT.TextBox\""), "");
        var ui = Directory.GetFiles(Path.Combine(repoRoot, "src", "SketchProMax", "UI"), "*.cs");
        var system = ui.Where(f => File.ReadAllText(f).Contains("System.Windows.MessageBox.Show")).Select(Path.GetFileName).ToList();
        check("cdt sdk: windows' message boxes are the SDK's (UI.MessageBox), never the plain Windows one", system.Count == 0, string.Join(", ", system));
    }

    /// <summary>The built DLL: SDK merged in, none of its DLLs beside it.</summary>
    public static void RunAddin(string addinFolder, Action<string, bool, string> check)
    {
        var beside = new[] { "CDT.Core.dll", "CDT.Wpf.dll", "WpfMath.dll", "XamlMath.Shared.dll" }.Where(f => File.Exists(Path.Combine(addinFolder, f))).ToList();
        check("cdt sdk: merged into SketchProMax.dll (no SDK DLL beside it)", beside.Count == 0, string.Join(", ", beside));
        var dll = Path.Combine(addinFolder, "SketchProMax.dll");
        if (!File.Exists(dll)) { check("cdt sdk: SketchProMax.dll found", false, dll); return; }
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        var types = md.TypeDefinitions.Select(h => md.GetTypeDefinition(h)).Select(t => md.GetString(t.Namespace) + "." + md.GetString(t.Name)).ToHashSet();
        check("cdt sdk: the DLL holds the SDK's types and references no CDT assembly",
            types.Contains("CDT.Wpf.ThemeManager") && types.Contains("CDT.Wpf.CdtMessageBox") && types.Contains("CDT.Wpf.NumberBox") && !refs.Any(r => r.StartsWith("CDT.")),
            string.Join(", ", refs.Where(r => r.StartsWith("CDT."))));
        var bytes = File.ReadAllBytes(dll);
        // The .g.resources table names its entries in UTF-16.
        var needle = System.Text.Encoding.Unicode.GetBytes("cdt.wpf/themes/modernresources.light.baml");
        check("cdt sdk: the theme's XAML is inside the DLL", bytes.AsSpan().IndexOf(needle) >= 0, "");
    }
}
