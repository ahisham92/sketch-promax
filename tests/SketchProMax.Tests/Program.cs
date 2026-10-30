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

// Like the add-in (ScriptCompiler.Compile): the snippet with stop checks added, the most checked version that compiles;
// a snippet that doesn't compile as written reports its own errors.
(Type? Type, string Errors) Build(string code)
{
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p)).ToList();
    (Type? Type, string Errors) One(string source)
    {
        var compilation = CSharpCompilation.Create("t" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(RevitStubs), CSharpSyntaxTree.ParseText(ScriptSource.Wrap(source), new CSharpParseOptions(LanguageVersion.Latest))],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
        using var pe = new MemoryStream();
        var emitted = compilation.Emit(pe);
        if (!emitted.Success)
            return (null, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"line {d.Location.GetMappedLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")));
        return (Assembly.Load(pe.ToArray()).GetType("SketchProMaxScript")!, "");
    }
    var variants = ScriptSource.Variants(code);
    var full = One(variants[0]);
    if (full.Type is not null || variants.Count == 1) return full;
    var plain = One(code);
    if (plain.Type is null || variants.Count < 3) return plain;
    var loops = One(variants[1]);
    return loops.Type is not null ? loops : plain;
}

(object? Result, string Errors) Run(string code, Dictionary<string, object>? args = null)
{
    var (type, errors) = Build(code);
    if (type is null) return (null, errors);
    return (type.GetMethod("Run")!.Invoke(null, [null, null, null, new StringBuilder(), args ?? new Dictionary<string, object>()]), "");
}

// ---- Stop checks added to every snippet (ScriptSource.Variants), so a long snippet stops when asked or out of time
Check("checks: a loop's one-line body gets braces and a check on the same line",
    ScriptSource.Checkpoints("for (var i = 0; i < 3; i++) n++;", false) == "for (var i = 0; i < 3; i++) { SpmCheckpoint(); n++; }",
    ScriptSource.Checkpoints("for (var i = 0; i < 3; i++) n++;", false));
Check("checks: blocks, lambdas and nested loops", ScriptSource.Checkpoints("foreach (var a in b) { while (x) y(); }\nvar f = items.Select(i => i * 2);\nAction g = () => { z(); };", true)
    == "foreach (var a in b) { SpmCheckpoint(); while (x) { SpmCheckpoint(); y(); } }\nvar f = items.Select(i => SpmCheckpoint(i * 2));\nAction g = () => { SpmCheckpoint(); z(); };",
    ScriptSource.Checkpoints("foreach (var a in b) { while (x) y(); }\nvar f = items.Select(i => i * 2);\nAction g = () => { z(); };", true));
var lineCode = "var n = 0;\nfor (var i = 0; i < 3; i++)\n    n += i;\ndo n++; while (n < 10);\nreturn n;";
Check("checks: no new lines, so errors keep the snippet's line numbers", ScriptSource.Variants(lineCode).All(v => v.Split('\n').Length == lineCode.Split('\n').Length));
Check("checks: a checked snippet gives the same result", Equals(Run(lineCode).Result, 10), Run(lineCode).Errors);
Check("checks: a lambda that returns nothing still compiles (loops-only version)",
    Equals(Run("var l = new List<int>(); Enumerable.Range(0, 3).ToList().ForEach(i => l.Add(i)); return l.Count;").Result, 3),
    Run("var l = new List<int>(); Enumerable.Range(0, 3).ToList().ForEach(i => l.Add(i)); return l.Count;").Errors);
Check("checks: a mistake is reported on its own line", Run("var x = 1;\nfor (var i = 0; i < 3; i++) { x += i; }\nreturn y;").Errors.StartsWith("line 3"),
    Run("var x = 1;\nfor (var i = 0; i < 3; i++) { x += i; }\nreturn y;").Errors);
Check("checks: a snippet that can't be parsed is compiled as written", ScriptSource.Variants("return 1;").Last() == "return 1;");
foreach (var (what, code) in new[]
{
    ("a loop", "var end = Environment.TickCount + 15000; long n = 0; while (Environment.TickCount - end < 0) { n++; } return n;"),
    ("a LINQ lambda", "var end = Environment.TickCount + 15000; return Enumerable.Range(0, int.MaxValue).Select(i => (long)i).TakeWhile(i => Environment.TickCount - end < 0).Count();"),
    ("a one-line loop body", "var end = Environment.TickCount + 15000; long n = 0; for (;;) if (Environment.TickCount - end < 0) n++; else break; return n;"),
})
{
    var (type, errors) = Build(code);
    var stopAt = Environment.TickCount + 300;
    type?.GetField("CancelProbe")!.SetValue(null, (Func<bool>)(() => Environment.TickCount - stopAt >= 0));
    type?.GetField("StopReason")!.SetValue(null, (Func<string>)(() => "out of time"));
    var watch = System.Diagnostics.Stopwatch.StartNew();
    string said = "";
    try { type?.GetMethod("Run")!.Invoke(null, [null, null, null, new StringBuilder(), new Dictionary<string, object>()]); }
    catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException stop) { said = stop.Message; }
    Check($"checks: {what} that would run 15 s stops when asked and says why", type is not null && said == "out of time" && watch.ElapsedMilliseconds < 3000,
        $"{errors} said '{said}' after {watch.ElapsedMilliseconds} ms");
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
ChatSwitchTests.Run((name, ok, detail) => Check(name, ok, detail));
ChatRecallTests.Run((name, ok, detail) => Check(name, ok, detail));
ChatHandoverTests.Run((name, ok, detail) => Check(name, ok, detail));
SavedPromptTests.Run((name, ok, detail) => Check(name, ok, detail));
PromptBriefTests.Run((name, ok, detail) => Check(name, ok, detail));
PromptLibraryTests.Run((name, ok, detail) => Check(name, ok, detail));
ModelRulesTests.Run((name, ok, detail) => Check(name, ok, detail));
LoaderTests.Run((name, ok, detail) => Check(name, ok, detail));
BugAndMemoryTests.Run((name, ok, detail) => Check(name, ok, detail));
HistoryLessonsTests.Run((name, ok, detail) => Check(name, ok, detail));
PromptIdeasTests.Run((name, ok, detail) => Check(name, ok, detail));
LayoutTests.Run((name, ok, detail) => Check(name, ok, detail));
RibbonFormTests.Run((name, ok, detail) => Check(name, ok, detail));
FavouritesTests.Run((name, ok, detail) => Check(name, ok, detail));
UpdateNoticeTests.Run((name, ok, detail) => Check(name, ok, detail));
ReleaseSourceTests.Run((name, ok, detail) => Check(name, ok, detail));
DeltaUpdateTests.Run((name, ok, detail) => Check(name, ok, detail));
UpdateResumeTests.Run((name, ok, detail) => Check(name, ok, detail));
TypeCatalogTests.Run((name, ok, detail) => Check(name, ok, detail));
CarbonTests.Run((name, ok, detail) => Check(name, ok, detail));
VeTests.Run((name, ok, detail) => Check(name, ok, detail));
RedlineTests.Run((name, ok, detail) => Check(name, ok, detail));
CostTests.Run((name, ok, detail) => Check(name, ok, detail));
HandoffTests.Run((name, ok, detail) => Check(name, ok, detail));
TimeTests.Run((name, ok, detail) => Check(name, ok, detail));
IssueGateTests.Run((name, ok, detail) => Check(name, ok, detail));
ChecklistTests.Run((name, ok, detail) => Check(name, ok, detail));
ChecklistProjectTests.Run((name, ok, detail) => Check(name, ok, detail));
PdfEditorTests.Run((name, ok, detail) => Check(name, ok, detail));
BerthStatusTests.Run((name, ok, detail) => Check(name, ok, detail));
SubmissionChecklistTests.Run((name, ok, detail) => Check(name, ok, detail));
BerthCostTests.Run((name, ok, detail) => Check(name, ok, detail));
JobCardTests.Run((name, ok, detail) => Check(name, ok, detail));
ParallelJobTests.Run((name, ok, detail) => Check(name, ok, detail));
AutoSyncTests.Run((name, ok, detail) => Check(name, ok, detail));
CrewTests.Run((name, ok, detail) => Check(name, ok, detail));
AutopilotTests.Run((name, ok, detail) => Check(name, ok, detail));
JobChecksTests.Run((name, ok, detail) => Check(name, ok, detail));
AskByEmailTests.Run((name, ok, detail) => Check(name, ok, detail));
ConnectionWatchTests.Run((name, ok, detail) => Check(name, ok, detail));
TwoModelTests.Run((name, ok, detail) => Check(name, ok, detail));
RunningCallTests.Run((name, ok, detail) => Check(name, ok, detail));
FreezeTests.Run((name, ok, detail) => Check(name, ok, detail));
RevitTimeTests.Run((name, ok, detail) => Check(name, ok, detail));
ScriptHintTests.Run((name, ok, detail) => Check(name, ok, detail));
ChatMcpTests.Run((name, ok, detail) => Check(name, ok, detail));
ResultViewTests.Run((name, ok, detail) => Check(name, ok, detail));
ChatAutoTests.Run((name, ok, detail) => Check(name, ok, detail));
PlanUsageTests.Run((name, ok, detail) => Check(name, ok, detail));
PackageTests.Run((name, ok, detail) => Check(name, ok, detail));
ApprovalTests.Run((name, ok, detail) => Check(name, ok, detail));
CleanupTests.Run((name, ok, detail) => Check(name, ok, detail));
ModelStorageTests.Run((name, ok, detail) => Check(name, ok, detail));
DocumentTests.Run((name, ok, detail) => Check(name, ok, detail));
SignaturePagesTests.Run((name, ok, detail) => Check(name, ok, detail));
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    InstallTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    StartupTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    ChatRecallTests.RunSources(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    FieldTestCoverageTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    FieldTestProvenTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    BrandTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    HistoryLessonsTests.RunSources(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    PromptIdeasTests.RunSources(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    SettingsTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    BerthStatusTests.RunSource(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    SharingTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    CrashSafetyTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    RecoveryTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
    LicenseTests.Run(File.Exists(Path.Combine(root, "README.md")) ? root : Directory.GetCurrentDirectory(), (name, ok, detail) => Check(name, ok, detail));
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
