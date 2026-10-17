using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SketchProMax.Tools;

// Compiles snippets with the same wrapper the add-in uses, against stand-in Revit types, and runs them.
const string RevitStubs = """
    namespace Autodesk.Revit.DB { public class Document { } }
    namespace Autodesk.Revit.DB.Architecture { }
    namespace Autodesk.Revit.DB.Structure { }
    namespace Autodesk.Revit.DB.Mechanical { }
    namespace Autodesk.Revit.DB.Plumbing { }
    namespace Autodesk.Revit.DB.Electrical { }
    namespace Autodesk.Revit.UI { public class UIApplication { } public class UIDocument { } }
    """;

var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail == "" ? "" : ": " + detail)}");
    if (!ok) failures++;
}

(object? Result, string Errors) Run(string code, Dictionary<string, object>? args = null)
{
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p));
    var compilation = CSharpCompilation.Create("t" + Guid.NewGuid().ToString("N"),
        [CSharpSyntaxTree.ParseText(RevitStubs), CSharpSyntaxTree.ParseText(ScriptSource.Wrap(code), new CSharpParseOptions(LanguageVersion.Latest))],
        references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
    using var pe = new MemoryStream();
    var emitted = compilation.Emit(pe);
    if (!emitted.Success)
        return (null, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"line {d.Location.GetMappedLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")));
    var method = Assembly.Load(pe.ToArray()).GetType("SketchProMaxScript")!.GetMethod("Run")!;
    return (method.Invoke(null, [null, null, null, new StringBuilder(), args ?? new Dictionary<string, object>()]), "");
}

var one = Run("return 1 + 1;");
Check("one-line snippet compiles and runs", one.Errors == "" && Equals(one.Result, 2), one.Errors);

var multi = Run("var items = new List<int> { 1, 2, 3 };\nlog.Append(\"ok\");\nreturn items.Sum();");
Check("multi-line snippet with usings", multi.Errors == "" && Equals(multi.Result, 6), multi.Errors);

var withArgs = Run("return Convert.ToDouble(args[\"width\"]) * 2;", new() { ["width"] = 1.5 });
Check("snippet reads args", withArgs.Errors == "" && Equals(withArgs.Result, 3.0), withArgs.Errors);

var bad = Run("var x = 1;\nreturn y;");
Check("errors report the snippet's own line number", bad.Errors.StartsWith("line 2:"), bad.Errors);

ChatTests.Run((name, ok, detail) => Check(name, ok, detail));
ChatViewTests.Run((name, ok, detail) => Check(name, ok, detail));
ChatModelTests.Run((name, ok, detail) => Check(name, ok, detail));
ChatHistoryTests.Run((name, ok, detail) => Check(name, ok, detail));
SavedPromptTests.Run((name, ok, detail) => Check(name, ok, detail));
PromptLibraryTests.Run((name, ok, detail) => Check(name, ok, detail));
ModelRulesTests.Run((name, ok, detail) => Check(name, ok, detail));
LoaderTests.Run((name, ok, detail) => Check(name, ok, detail));
BugAndMemoryTests.Run((name, ok, detail) => Check(name, ok, detail));
LayoutTests.Run((name, ok, detail) => Check(name, ok, detail));
RibbonFormTests.Run((name, ok, detail) => Check(name, ok, detail));
FavouritesTests.Run((name, ok, detail) => Check(name, ok, detail));
UpdateNoticeTests.Run((name, ok, detail) => Check(name, ok, detail));
CarbonTests.Run((name, ok, detail) => Check(name, ok, detail));
VeTests.Run((name, ok, detail) => Check(name, ok, detail));
HandoffTests.Run((name, ok, detail) => Check(name, ok, detail));
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    InstallTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    StartupTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
}

// With --addin <folder of a built SketchProMax.dll>, also check the whole tool catalogue.
var addinAt = Array.IndexOf(args, "--addin");
if (addinAt >= 0 && addinAt + 1 < args.Length)
{
    var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    if (!File.Exists(Path.Combine(repoRoot, "README.md"))) repoRoot = Directory.GetCurrentDirectory();
    CatalogTests.Run(Path.GetFullPath(args[addinAt + 1]), repoRoot, (name, ok, detail) => Check(name, ok, detail));
}

Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} test(s) failed.");
return failures == 0 ? 0 : 1;
