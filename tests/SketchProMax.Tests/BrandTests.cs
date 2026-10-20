using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SketchProMax.Chat;
using SketchProMax.UI;

/// <summary>
/// The assistant is Sketch ProMax in everything the user reads: the loader's words rewritten at run time, friendly step
/// rows, and a scan of the user-visible sources so the model's name doesn't come back.
/// </summary>
internal static class BrandTests
{
    // Source files whose string literals the user reads (chat, pane, ribbon, dialogs, windows, field test and checklist).
    // Tool code (Tools/*Tools.cs, Agent/) is not scanned: its descriptions and results go to the model.
    private static readonly string[] Scanned =
    [
        "src/SketchProMax/Chat", "src/SketchProMax/UI", "src/SketchProMax/Sharing", "src/TritoRevit", "src/SketchProMax/Commands", "src/SketchProMax/Bugs",
        "src/SketchProMax/Bridge/Approvals.cs", "src/SketchProMax/Bridge/JobControl.cs", "src/SketchProMax/CoreEntry.cs",
        "src/SketchProMax/App.cs", "src/SketchProMax/Update/Updater.cs", "src/SketchProMax/Tools/OutputPaths.cs",
        "src/SketchProMax/Tools/FieldTest.cs", "src/SketchProMax/Tools/FieldTest.Features.cs", "src/SketchProMax/Tools/FieldTest.Redlines.cs",
        "src/SketchProMax/Tools/FieldTest.Extras.cs", "src/SketchProMax/Tools/FieldTest.Package.cs", "src/SketchProMax/Tools/FieldTestCoverage.cs",
        "src/SketchProMax/Tools/FieldTest.Sharing.cs",
    ];

    // Files that name the model on purpose: the rewriting itself.
    private static readonly string[] SkippedFiles = ["src/SketchProMax/UI/BrandText.cs", "src/SketchProMax/UI/LoaderBrand.cs",
        // The summary run's instructions and transcript labels: read by the model only, never shown.
        "src/SketchProMax/Chat/ChatRecall.cs"];

    /// <summary>Literals that may still say Claude, each with its reason. A literal passes when it contains one of them.</summary>
    public static readonly (string Text, string Why)[] Allowed =
    [
        ("Claude", "the first ribbon panel's name: the loader and the manifest find it by it (its title shows Assistant)"),
        ("Claude.exe", "the desktop app's file name, to start it"),
        ("!Claude", "the Microsoft Store desktop app's id, to start it"),
        ("runs only when Claude calls it", "a tool error returned to the model"),
        ("never Claude", "the checklist step that checks the name is gone"),
    ];

    public static void Run(string repoRoot, Action<string, bool, string> check)
    {
        // ---- The loader's words, rewritten where they show
        check("brand: the pane header", BrandText.Rewrite("Claude: connected") == "Sketch ProMax: connected"
            && BrandText.Rewrite("Claude: not running") == "Sketch ProMax: not running", BrandText.Rewrite("Claude: connected"));
        check("brand: rewriting twice changes nothing", BrandText.Rewrite(BrandText.Rewrite("Claude: ready")) == "Sketch ProMax: ready"
            && BrandText.Rewrite("No job") == "No job" && BrandText.Rewrite(null) == "", "");
        check("brand: products the user acts on stay named",
            BrandText.Rewrite("Nothing is running. Ask Claude something about this model in the Chat tab or the Claude app")
                == "Nothing is running. Ask Sketch ProMax something about this model in the Chat tab or the Claude app"
            && BrandText.Rewrite("Install Claude Code") == "Install Claude Code",
            BrandText.Rewrite("Nothing is running. Ask Claude something about this model in the Chat tab or the Claude app"));
        check("brand: loader phrases", BrandText.Rewrite("Export for Claude") == "Export project pack"
            && BrandText.Rewrite("Chat with Claude") == "Chat with Sketch ProMax"
            && BrandText.Rewrite("to upload to a Claude chat. The folder opens") == "to upload to an AI chat. The folder opens"
            && BrandText.Rewrite("Ask Claude in the chat what went wrong") == "Ask in the chat what went wrong", "");
        check("brand: identifiers are not words", BrandText.Clean("ClaudeMore OpenClaude AnthropicClaude") && !BrandText.Clean("Claude is thinking"), "");

        // ---- Step rows: a friendly name and the plain description, never the tool id or key=value
        check("steps: friendly names", StepNames.Title("run_revit_query_code") == "Read model data" && StepNames.Title("run_revit_code") == "Change the model"
            && StepNames.Title("mcp__sketch-promax__tag_elements_in_view") == "Tag elements in view" && StepNames.Title("export_bcf") == "Export BCF"
            && StepNames.Title("") == "" && StepNames.Title("Grep") == "Search files", StepNames.Title("export_bcf"));
        var json = """{"description":"Read 79 selected piles: current base and top","code":"var x = 1;\nreturn x;"}""";
        check("steps: the call's own description", StepNames.Describe("run_revit_query_code", json) == "Read 79 selected piles: current base and top"
            && StepNames.Describe("get_elements", """{"category":"Walls"}""") == "" && StepNames.Describe("x", "not json") == ""
            && StepNames.Describe("Read", """{"file_path":"C:\\a\\b\\notes.md"}""") == "notes.md", StepNames.Describe("run_revit_query_code", json));
        var transcript = """{"file_path":"C:\\Users\\a\\.claude\\projects\\C--Users-a-AppData\\5474e2d4-10c8-4f92-9391-6323656e34f9.jsonl"}""";
        check("steps: the engine's own files in plain words", StepNames.Describe("Read", transcript) == StepNames.EarlierMessages
            && StepNames.Describe("Read", """{"file_path":"C:\\Users\\a\\.claude\\projects\\x\\tool-results\\toolu_1.txt"}""") == StepNames.SavedResult
            && StepNames.Line("Read", "5474e2d4-10c8-4f92-9391-6323656e34f9.jsonl") == StepNames.EarlierMessages
            && StepNames.Describe("Read", """{"file_path":"C:\\Jobs\\piles.csv"}""") == "piles.csv", StepNames.Describe("Read", transcript));
        check("steps: the row line", StepNames.Line("run_revit_query_code", "Read 79 selected piles") == "Read 79 selected piles"
            && StepNames.Line("run_revit_code", "") == "Change the model"
            && StepNames.Line("run_revit_code", "description=Set 79 selected pile tops to hi…, code=Fu…") == "Set 79 selected pile tops to hi…"
            && StepNames.Line("get_elements", "category=Walls, level=L1") == "Get elements", StepNames.Line("run_revit_code", "description=Set 79 selected pile tops to hi…, code=Fu…"));
        var running = StepNames.Running("run_revit_code", "Set 79 selected pile tops", mayAsk: true);
        check("steps: the running line", running == "Running: Set 79 selected pile tops… If Revit asks, answer it there."
            && StepNames.Running("run_revit_code", "Set 79 selected pile tops", mayAsk: false) == "Running: Set 79 selected pile tops…", running);
        check("steps: reads never ask", StepNames.ReadOnly("run_revit_query_code") && StepNames.ReadOnly("mcp__x__get_elements") && !StepNames.ReadOnly("run_revit_code"), "");
        check("steps: the job card uses the friendly name", SketchProMax.Bridge.JobMath.Title("run_revit_code") == "Change the model", "");

        // ---- The scan: no user-visible literal names the model outside the products the user acts on and the allow-list
        var found = new List<string>();
        var word = new Regex(@"\bClaude\b", RegexOptions.CultureInvariant);
        var files = Scanned.SelectMany(p =>
        {
            var full = Path.Combine(repoRoot, p);
            return Directory.Exists(full) ? Directory.GetFiles(full, "*.cs", SearchOption.AllDirectories) : File.Exists(full) ? [full] : [];
        }).Where(f => !SkippedFiles.Any(s => Path.GetFullPath(Path.Combine(repoRoot, s)) == Path.GetFullPath(f))).ToList();
        foreach (var file in files)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Latest));
            foreach (var token in tree.GetRoot().DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.InterpolatedStringTextToken)
                    && !token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) && !token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)) continue;
                var text = token.ValueText;
                if (!word.IsMatch(text) || BrandText.Clean(text)) continue;
                if (Allowed.Any(a => text == a.Text || a.Text.Length > 6 && text.Contains(a.Text))) continue;
                var line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                found.Add($"{Path.GetRelativePath(repoRoot, file)}:{line} \"{(text.Length > 60 ? text.Substring(0, 60) + "…" : text)}\"");
            }
        }
        check("brand: no user-visible text says Claude (outside products to act on and the allow-list)", files.Count > 30 && found.Count == 0,
            files.Count <= 30 ? $"only {files.Count} files found under {repoRoot}" : string.Join("; ", found.Take(12)) + (found.Count > 12 ? $" (+{found.Count - 12} more)" : ""));

        // ---- The loader (unchanged between releases): every word it shows comes out clean once rewritten
        var loaderBad = new List<string>();
        foreach (var name in new[] { "Pane.cs", "Ribbon.cs" })
        {
            var path = Path.Combine(repoRoot, "src", "SketchProMax.Loader", name);
            if (!File.Exists(path)) { loaderBad.Add("missing " + name); continue; }
            foreach (var token in CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.InterpolatedStringTextToken)) continue;
                var text = token.ValueText;
                if (text == "Claude" || !word.IsMatch(text)) continue;
                if (!BrandText.Clean(BrandText.Rewrite(text))) loaderBad.Add($"{name}: {text}");
            }
        }
        check("brand: the loader's pane and ribbon words come out clean", loaderBad.Count == 0, string.Join("; ", loaderBad.Take(8)));
    }
}
