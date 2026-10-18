using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SketchProMax.Revit;

/// <summary>
/// Bug #18 (opening a model was slow while Sketch ProMax loaded): the timings bookkeeping, and a guard that nothing
/// known to be heavy runs synchronously on the core's start path or in its DocumentOpened handler.
/// </summary>
internal static class StartupTests
{
    // Network, disk-wide scans, Roslyn, reports and pane content: fine on first use, a background thread or an idle
    // moment, never inline while Revit starts or opens a model.
    private static readonly string[] Heavy =
    [
        "CheckAsync", "UpdateLoopAsync", "SyncBugLogAsync", "BugLog.SyncAsync", "SettleInstall", "ClaudeRegistration.Ensure",
        "ScriptCompiler", "AssemblyReport", "FieldTest", "SelfTest", "ChatPane.Get", "NotesView.Get", "ChatStore.Load",
        "QuickTools.List", "LearnedTools.All", "AgentSettings.Load", "WriteToolCatalogue", "FilteredElementCollector",
        "ModelSnapshot", "CheckModel", "GetLinkedDocuments", ".Wait(", ".Result",
    ];

    public static void Run(string? repoRoot, Action<string, bool, string> check)
    {
        // ---- Timings
        StartupTimings.Reset();
        check("startup: nothing measured says so", StartupTimings.StartLine().Contains("not measured"), StartupTimings.StartLine());
        StartupTimings.Phase("dispatcher", 3);
        StartupTimings.Phase("tool registry", 41);
        StartupTimings.Phase("dispatcher", 99);
        StartupTimings.StartDone(57);
        var line = StartupTimings.StartLine();
        check("startup: total and phases in order, first report of a phase kept", line == "core start 57 ms (dispatcher 3, tool registry 41; ms)", line);
        check("startup: no open, no idle line", StartupTimings.FirstIdle() is null && !StartupTimings.IsOpening, "");
        StartupTimings.Opening();
        check("startup: opening is seen", StartupTimings.IsOpening, "");
        StartupTimings.Opened("Model A", 0);
        check("startup: opened ends opening", !StartupTimings.IsOpening && StartupTimings.AwaitingIdle, "");
        var first = StartupTimings.FirstIdle();
        check("startup: first idle after open gives one line", first is not null && first.Contains("Model A") && first.Contains("first Idling")
            && StartupTimings.FirstIdle() is null, first ?? "null");
        check("startup: report section lists start and opens", StartupTimings.Describe().Contains("core start 57 ms") && StartupTimings.Describe().Contains("Model A"),
            StartupTimings.Describe());
        StartupTimings.Opening();
        StartupTimings.Opened("Copy", 0);
        var gone = StartupTimings.FirstIdle(_ => false);
        check("startup: a model closed before the first idle says so", gone?.Contains("closed again before Revit was idle") == true && !gone.Contains("first Idling"), gone ?? "null");
        check("startup: unknown open time shows ?", StartupTimings.OpenLine("m", -1, 0, 5).Contains("took ? ms"), "");
        // Bug #18's field-test probe reads these.
        StartupTimings.Opening();
        StartupTimings.Opened("Slow handlers", 640);
        StartupTimings.Opened("Quick handlers", 12);
        check("startup: start time and the slowest handlers", StartupTimings.StartMs == 57 && StartupTimings.WorstHandlersMs == 640, $"{StartupTimings.StartMs} {StartupTimings.WorstHandlersMs}");
        StartupTimings.Reset();
        check("startup: reset forgets them", StartupTimings.StartMs == -1 && StartupTimings.WorstHandlersMs == -1, "");
        check("startup: an ordinary open is not labelled as a test", !first!.Contains("(test running)"), first);
        StartupTimings.TestRunning = true;
        StartupTimings.Opening();
        StartupTimings.Opened("Test project", 0);
        StartupTimings.TestRunning = false;
        var during = StartupTimings.FirstIdle();
        check("startup: an open during a test says (test running)", during?.EndsWith("after open (test running)") == true, during ?? "null");
        StartupTimings.Reset();

        // ---- Nothing heavy inline on the start path. First, that the check itself sees the 0.30 shapes it guards against.
        const string before = """
            class A {
                static object PaneContent(string n) => n switch { "chat" => Chat.ChatPane.Get(), _ => null };
                static void StartCore() { Bridge.Start(); if (AgentSettings.Load().AutoUpdate) Task.Run(() => UpdateLoopAsync(t)); }
                static void Start() { StartupTimings.Time("x", () => Updater.SettleInstall()); Later(() => QuickTools.List()); }
                static object Pane() => LazyPane.For("chat", Chat.ChatPane.Get);
            }
            """;
        check("start path check: sees an inline pane build", HeavyInline(before, "PaneContent") is ["ChatPane.Get"], "");
        check("start path check: sees an inline settings read, not the background loop",
            HeavyInline(before, "StartCore") is ["AgentSettings.Load"], string.Join(",", HeavyInline(before, "StartCore") ?? []));
        check("start path check: a timed phase runs inline, the idle queue doesn't",
            HeavyInline(before, "Start") is ["SettleInstall"], string.Join(",", HeavyInline(before, "Start") ?? []));
        check("start path check: a method group handed on is not a call", HeavyInline(before, "Pane") is [], "");
        if (repoRoot is null) return;
        var core = Path.Combine(repoRoot, "src", "SketchProMax");
        if (!Directory.Exists(core)) return;
        CheckInline(check, Path.Combine(core, "App.cs"), "StartCore");
        CheckInline(check, Path.Combine(core, "App.cs"), "OnDocumentOpened");
        CheckInline(check, Path.Combine(core, "App.cs"), "OnDocumentOpening");
        CheckInline(check, Path.Combine(core, "CoreEntry.cs"), "PaneContent");
        CheckInline(check, Path.Combine(core, "Bridge", "BridgeServer.cs"), "Start");
        CheckInline(check, Path.Combine(core, "Chat", "ChatPane.cs"), "SetModel");
        CheckInline(check, Path.Combine(core, "Chat", "ChatPane.cs"), "Start");
    }

    private static void CheckInline(Action<string, bool, string> check, string file, string method)
    {
        var name = $"start path: {Path.GetFileName(file)} {method} runs nothing heavy inline";
        if (!File.Exists(file)) { check(name, false, "file not found"); return; }
        var found = HeavyInline(File.ReadAllText(file), method);
        check(name, found is { Count: 0 }, found is null ? "method not found" : string.Join(", ", found));
    }

    /// <summary>
    /// Heavy calls made directly in <paramref name="method"/>: not in a lambda it hands to a background task, the idle
    /// queue or an event, and not a method group passed along to be called later. Null when the method isn't there.
    /// </summary>
    internal static List<string>? HeavyInline(string source, string method)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var bodies = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == method).ToList();
        if (bodies.Count == 0) return null;
        var found = new List<string>();
        foreach (var body in bodies)
            foreach (var node in body.DescendantNodes().Where(n => n is InvocationExpressionSyntax or MemberAccessExpressionSyntax))
            {
                // Inside a timed phase (StartupTimings.Time("...", () => ...)) it does run now.
                if (node.Ancestors().TakeWhile(a => a != body).Any(a => a is AnonymousFunctionExpressionSyntax && !IsTimedPhase(a))) continue;
                if (node is MemberAccessExpressionSyntax && node.Parent is ArgumentSyntax) continue; // a method group, called later
                var text = node is InvocationExpressionSyntax call ? call.Expression + "(" : node.ToString();
                found.AddRange(Heavy.Where(h => text.Contains(h, StringComparison.Ordinal)));
            }
        return found.Distinct().ToList();
    }

    private static bool IsTimedPhase(SyntaxNode lambda) =>
        lambda.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } }
        && call.Expression.ToString().EndsWith("StartupTimings.Time", StringComparison.Ordinal);
}
