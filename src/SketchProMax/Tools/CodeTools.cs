using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// The catch-all: Claude writes a short C# snippet against the Revit API and it runs in the open model.
/// This covers anything the dedicated tools don't (stairs, railings, materials, unusual parameters, bulk edits...).
/// run_revit_code can also leave transactions to the snippet (own_transactions) for edit scopes such as StairsEditScope.
/// The read-only variant runs inside a transaction (in a group) that is always rolled back, so probes may create or
/// change elements and nothing they do is kept.
/// </summary>
public abstract class RevitCodeToolBase : IRevitTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract bool ModifiesModel { get; }

    public virtual string PropertiesJson => """
    {
      "code": {"type": "string", "description": "C# method body. Variables available: app (UIApplication), uidoc (UIDocument), doc (Document), log (StringBuilder for notes). Must end with a return statement; return counts, ids or small anonymous objects, not Revit elements. Namespaces imported: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB (+ .Architecture, .Structure, .Mechanical, .Plumbing, .Electrical), Autodesk.Revit.UI. Lengths are in feet (Revit internal units); convert with UnitUtils."},
      "description": {"type": "string", "description": "What the code does, in a few words. Shown to the user and used as the Undo name."}
    }
    """;

    public string[] Required => ["code", "description"];

    protected static readonly string CodeGuidance =
        " Write a C# method body using the Revit API of the running Revit version. Use dedicated tools first when one fits; " +
        "use this for anything they can't do. If compilation fails, the errors come back with line numbers so you can fix and retry. " +
        "Big models (tens of thousands of elements): never collect a whole class or category unfiltered (all CurveElements includes every " +
        "sketch line and can freeze Revit for minutes); filter by category AND view, level or BoundingBoxIntersectsFilter/" +
        "ElementIntersectsElementFilter, and use quick filters before slow ones. Revit is frozen for the user while the code runs, so " +
        $"keep each call short: a call still running after {CapSeconds} s is stopped at its next loop step and everything it did is rolled " +
        "back. Do big work in batches (for example 50 to 200 elements per call), one call after another, each one quick. log.AppendLine " +
        "progress or partial results: wait_for_job shows the end of the log while it runs, and the log comes back even if the code stops " +
        "early. A call that runs past about 45 s returns 'still running as job N'; collect the result with wait_for_job.";

    /// <summary>
    /// The longest one code call may hold Revit's thread. Past it the snippet stops at its next loop step (the checks are
    /// added to every snippet, ScriptSource.Variants) and is rolled back, so no snippet keeps Revit frozen for minutes
    /// (2026-10-27: a 12-minute dry run froze Revit until it was closed).
    /// </summary>
    internal const int CapSeconds = 90;

    internal static string CapText(int seconds) =>
        $"Stopped after {seconds} s so Revit doesn't stay frozen: while one call runs the user can't work in Revit. Nothing it did was " +
        "kept. Do the work in smaller steps: fewer elements per call (for example 50 to 200), one call after another, each finishing well " +
        "under a minute, and keep any whole-model collection filtered.";

    public virtual ToolResult Execute(UIApplication app, ToolArgs args) =>
        RunScript(app, args.String("code"), args.String("description"), ModifiesModel, new Dictionary<string, object?>(), capSeconds: CapSeconds);

    /// <summary>
    /// Compiles and runs a snippet, inside one transaction when it changes the model. Learned tools use this too.
    /// With ownTransactions the snippet opens and commits its own transactions (needed for StairsEditScope and other
    /// edit scopes, which cannot start inside an open transaction); a transaction group around it keeps the run
    /// atomic and makes it one Undo step.
    /// </summary>
    internal static ToolResult RunScript(UIApplication app, string code, string description, bool modifiesModel,
        IDictionary<string, object?> values, bool ownTransactions = false, int capSeconds = 0)
    {
        // The time cap, unless the caller set its own (the field test's per-step cap).
        if (capSeconds <= 0 || Bridge.Jobs.Running is not { Limit: null } capped)
            return RunScriptCore(app, code, description, modifiesModel, values, ownTransactions);
        var limit = new Bridge.StepLimit { Reason = CapText(capSeconds) };
        capped.Limit = limit;
        using var timer = new Timer(_ => limit.TryHit(), null, capSeconds * 1000, Timeout.Infinite);
        try { return RunScriptCore(app, code, description, modifiesModel, values, ownTransactions); }
        finally
        {
            limit.Finish();
            if (capped.Limit == limit) capped.Limit = null;
        }
    }

    private static ToolResult RunScriptCore(UIApplication app, string code, string description, bool modifiesModel,
        IDictionary<string, object?> values, bool ownTransactions)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc.Document;

        var (run, errors) = ScriptCompiler.Compile(code);
        if (run is null) return ToolResult.Error("The code did not compile:\n" + errors);

        var log = new StringBuilder();
        // wait_for_job shows the end of the log as progress while a long snippet runs.
        if (Bridge.Jobs.Running is { } job) job.Log = log;
        object? result;
        if (modifiesModel && ownTransactions)
        {
            using var group = new TransactionGroup(doc, $"Sketch ProMax: {Trim(description, 60)}");
            group.Start();
            try
            {
                result = run(app, uidoc, doc, log, values);
            }
            catch (Exception ex)
            {
                var undone = TryRollBack(group);
                return ToolResult.Error($"The code failed{(undone ? " and everything it changed was rolled back" : "")}. {Unwrap(ex)}\n{log}");
            }
            if (doc.IsModifiable)
            {
                // A transaction the snippet started is still open: nothing can be committed safely.
                var undone = TryRollBack(group);
                return ToolResult.Error("The code left a transaction open (start and commit each Transaction inside a using block). " +
                                        $"{(undone ? "Everything it changed was rolled back." : "")}\n{log}");
            }
            // An empty group (the snippet committed nothing) can end as RolledBack; that is not a failure.
            var status = group.Assimilate();
            if (status != TransactionStatus.Committed && status != TransactionStatus.RolledBack)
                return ToolResult.Error($"Revit did not accept the change ({status}), so nothing was changed.\n{log}");
        }
        else if (modifiesModel)
        {
            using var t = new Transaction(doc, $"Sketch ProMax: {Trim(description, 60)}");
            t.Start();
            try
            {
                result = run(app, uidoc, doc, log, values);
            }
            catch (Exception ex)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                return ToolResult.Error($"The code failed and nothing was changed. {Unwrap(ex)}\n{log}");
            }
            var status = t.Commit();
            if (status != TransactionStatus.Committed)
                return ToolResult.Error($"Revit did not accept the change ({status}), so nothing was changed.\n{log}");
        }
        else
        {
            // A transaction is opened so probes that create or change elements work, inside a group, and both are
            // always rolled back, so nothing a read-only snippet does is kept. With ownTransactions only the group is
            // opened: the snippet starts its own (and can call EditFamily, which refuses to run inside one).
            // Inside someone else's open transaction nothing could be rolled back afterwards: refuse rather than keep changes.
            if (doc.IsModifiable && !doc.IsReadOnly)
                return ToolResult.Error("A transaction is already open in this model (another command or tool is still running), so the query " +
                                        "could not be rolled back afterwards and was not run. Finish or cancel that first, then run it again.");
            var group = doc.IsReadOnly ? null : new TransactionGroup(doc, "Sketch ProMax: read-only code (rolled back)");
            Transaction? probe = null;
            JsonElement json;
            try
            {
                group?.Start();
                if (group is not null && !ownTransactions)
                {
                    probe = new Transaction(doc, "Sketch ProMax: probe (rolled back)");
                    // Warnings from a probe must not stop it with a dialog; it is rolled back anyway.
                    var fo = probe.GetFailureHandlingOptions();
                    fo.SetClearAfterRollback(true);
                    fo.SetForcedModalHandling(false);
                    probe.SetFailureHandlingOptions(fo);
                    probe.Start();
                }
                result = run(app, uidoc, doc, log, values);
                // Read the result now: elements the snippet made are gone after the rollback.
                json = SafeJson(result);
            }
            catch (Exception ex)
            {
                var hint = probe is not null && Unwrap(ex).IndexOf("transaction", StringComparison.OrdinalIgnoreCase) >= 0
                    ? " A transaction is already open for the code (and rolled back after it); don't start your own, or set own_transactions to true (needed for EditFamily)."
                    : "";
                return ToolResult.Error($"The code failed. {Unwrap(ex)}{hint}\n{log}");
            }
            finally
            {
                if (probe is not null)
                {
                    try { if (probe.HasStarted() && !probe.HasEnded()) probe.RollBack(); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { /* the group rollback below still undoes it */ }
                    probe.Dispose();
                }
                if (group is not null)
                {
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                    group.Dispose();
                }
            }
            return new ToolResult(JsonSerializer.Serialize(new { result = json, log = log.Length > 0 ? log.ToString() : null, rolledBack = group is not null ? true : (bool?)null }));
        }

        return new ToolResult(JsonSerializer.Serialize(new { result = SafeJson(result), log = log.Length > 0 ? log.ToString() : null }));
    }

    private static bool TryRollBack(TransactionGroup group)
    {
        try
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            return true;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
    }

    internal static string Unwrap(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
        var line = ScriptCompiler.LineOf(ex);
        return $"{ex.GetType().Name}: {ex.Message}{(line is null ? "" : $" (line {line})")}{ScriptSource.RuntimeHint(ex.Message)}";
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    /// <summary>Turns the snippet's return value into JSON, falling back to text for objects that don't serialize.</summary>
    private static JsonElement SafeJson(object? value)
    {
        string json;
        try
        {
            json = value switch
            {
                null => "null",
                Element e => JsonSerializer.Serialize(RevitHelpers.ElementSummary(e.Document, e)),
                ElementId id => id.Value.ToString(),
                _ => JsonSerializer.Serialize(value, new JsonSerializerOptions
                {
                    ReferenceHandler = ReferenceHandler.IgnoreCycles, MaxDepth = 8,
                }),
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException
                                       or Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
        {
            json = JsonSerializer.Serialize(value?.ToString());
        }
        if (json.Length > 60000) json = JsonSerializer.Serialize(json.Substring(0, 60000) + " …(truncated)");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

public sealed class RunRevitQueryCodeTool : RevitCodeToolBase
{
    public override string Name => "run_revit_query_code";
    public override string Description =>
        "Run read-only C# against the Revit API to answer a question the other tools can't (any element, parameter, " +
        "geometry or setting). It runs inside a transaction that is opened for it and always rolled back, so it can create " +
        "or change elements to probe (e.g. measure a temporary element) but nothing is kept; do not start your own " +
        "Transaction. To call EditFamily or open your own transactions, set own_transactions to true (still all rolled back)." + CodeGuidance;
    public override bool ModifiesModel => false;

    public override string PropertiesJson => """
    {
      "code": {"type": "string", "description": "C# method body. Variables available: app (UIApplication), uidoc (UIDocument), doc (Document), log (StringBuilder for notes). Must end with a return statement; return counts, ids or small anonymous objects, not Revit elements. Namespaces imported: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB (+ .Architecture, .Structure, .Mechanical, .Plumbing, .Electrical), Autodesk.Revit.UI. Lengths are in feet (Revit internal units); convert with UnitUtils."},
      "description": {"type": "string", "description": "What the code does, in a few words."},
      "own_transactions": {"type": "boolean", "description": "Default false. true: no transaction is opened for you (needed for EditFamily); open your own. Everything is still rolled back at the end."}
    }
    """;

    public override ToolResult Execute(UIApplication app, ToolArgs args) =>
        RunScript(app, args.String("code"), args.String("description"), false, new Dictionary<string, object?>(), args.OptBool("own_transactions"),
            CapSeconds);
}

public sealed class RunRevitCodeTool : RevitCodeToolBase
{
    public override string Name => "run_revit_code";
    public override string Description =>
        "Run C# that changes the model through the Revit API, for anything the other tools can't do (stairs, railings, " +
        "materials, curtain walls, MEP, bulk edits...). By default the code runs inside one transaction that is opened and " +
        "committed for you: do not start your own. Edit scopes (StairsEditScope for stairs runs and landings, " +
        "EditScope-based APIs) cannot start inside a transaction: set own_transactions to true, then start the scope " +
        "first and open/commit your own Transaction(s) inside it. Either way, if it throws, everything is rolled back. " +
        "The user sees the code before it runs and can undo it with Ctrl+Z." + CodeGuidance;
    public override bool ModifiesModel => true;

    public override string PropertiesJson => """
    {
      "code": {"type": "string", "description": "C# method body. Variables available: app (UIApplication), uidoc (UIDocument), doc (Document), log (StringBuilder for notes). Must end with a return statement; return counts, ids or small anonymous objects, not Revit elements. Namespaces imported: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB (+ .Architecture, .Structure, .Mechanical, .Plumbing, .Electrical), Autodesk.Revit.UI. Lengths are in feet (Revit internal units); convert with UnitUtils."},
      "description": {"type": "string", "description": "What the code does, in a few words. Shown to the user and used as the Undo name."},
      "own_transactions": {"type": "boolean", "description": "Default false. true: no transaction is opened for you; the code opens and commits its own (required for StairsEditScope). All of it is still one Undo step and is rolled back if the code throws."}
    }
    """;

    public override ToolResult Execute(UIApplication app, ToolArgs args) =>
        RunScript(app, args.String("code"), args.String("description"), ModifiesModel, new Dictionary<string, object?>(),
            args.OptBool("own_transactions"), CapSeconds);
}

/// <summary>Compiles snippets with Roslyn, caching by source so a retried snippet doesn't compile twice.</summary>
internal static class ScriptCompiler
{
    public delegate object? Script(UIApplication app, UIDocument uidoc, Document doc, StringBuilder log, IDictionary<string, object?> args);

    private sealed record Entry(Script Run, object? Context);

    private static readonly Dictionary<string, Entry> Cache = new();
    private static List<MetadataReference>? _references;

    public static (Script? Run, string Errors) Compile(string code) => Compile(code, null, null);

    /// <summary>
    /// Compiles the snippet with stop checks added (ScriptSource.Variants: the most checked version that compiles; the
    /// errors reported are those of the snippet as written). <paramref name="cancel"/> and <paramref name="reason"/>
    /// replace the running job's stop request (the field test's probe); such a compile isn't cached.
    /// </summary>
    internal static (Script? Run, string Errors) Compile(string code, Func<bool>? cancel, Func<string?>? reason)
    {
        var shared = cancel is null;
        if (shared && Cache.TryGetValue(code, out var cached)) return (cached.Run, "");

        // The most checked version first. When it doesn't compile, the snippet as written is tried before the
        // in-between one, so a snippet with a real mistake costs two compiles, not three, and its own errors come back.
        var variants = ScriptSource.Variants(code);
        var order = variants.Count == 3 ? new List<string> { variants[0], variants[2], variants[1] } : variants;
        var errors = "";
        for (var i = 0; i < order.Count; i++)
        {
            var (assembly, context, failed) = Emit(order[i]);
            if (assembly is null)
            {
                if (order[i] == code) return (null, failed);
                errors = failed;
                continue;
            }
            // The snippet as written compiled after a checked version didn't: use the in-between one if it compiles.
            if (order[i] == code && i + 1 < order.Count && Emit(order[i + 1]) is { Assembly: { } better } loops)
                (assembly, context) = (better, loops.Context);
            var type = assembly.GetType("SketchProMaxScript")!;
            var method = type.GetMethod("Run")!;
            // Lets a long snippet stop when the user asks (cancel_job, Stop, the Rescue window) or its time is up: the
            // added checks and CheckCancel() throw, Cancelled() tells, and the message says why.
            type.GetField("CancelProbe")?.SetValue(null, cancel ?? (() => Bridge.Jobs.CancelRequested));
            type.GetField("StopReason")?.SetValue(null, reason is null ? () => Bridge.Jobs.StopReason : new Func<string?>(reason));
            Script run = (app, uidoc, doc, log, args) => method.Invoke(null, [app, uidoc, doc, log, args]);
            if (!shared) return (run, "");
            if (Cache.Count > 50) Evict();
            Cache[code] = new Entry(run, context);
            return (run, "");
        }
        return (null, errors);
    }

    private static (Assembly? Assembly, object? Context, string Errors) Emit(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(ScriptSource.Wrap(code), new CSharpParseOptions(LanguageVersion.Latest));
        // No member whose signature names ImmutableArray (System.Collections.Immutable) is called from here: the
        // options come from Roslyn's own defaults via With*, and the diagnostics are read as a plain IEnumerable. On
        // Revit 2024 this DLL and Roslyn could otherwise disagree about which System.Collections.Immutable they use
        // (another add-in's copy), and the CSharpCompilationOptions constructor was then "not found".
        var compilation = CSharpCompilation.Create("SketchProMaxScript_" + Guid.NewGuid().ToString("N"), [tree], References());
        compilation = compilation.WithOptions(compilation.Options
            .WithOutputKind(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithNullableContextOptions(NullableContextOptions.Disable));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emitted = compilation.Emit(pe, pdb);
        if (!emitted.Success)
        {
            var errors = Diagnostics(emitted)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(15)
                .Select(d => $"line {d.Location.GetMappedLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}{MemberHint(compilation, d)}");
            return (null, null, string.Join("\n", errors));
        }

        var (assembly, context) = Load(pe.ToArray(), pdb.ToArray());
        return (assembly, context, "");
    }

    /// <summary>
    /// For "'X' does not contain a definition for 'Y'" (CS1061, CS0117): the members of X closest to Y in this Revit
    /// version's API, so the next try uses a name that exists. "" when none is close or anything goes wrong.
    /// </summary>
    private static string MemberHint(CSharpCompilation compilation, Diagnostic d)
    {
        if (d.Id is not ("CS1061" or "CS0117") || d.Location.SourceTree is not { } tree) return "";
        try
        {
            var node = tree.GetRoot().FindNode(d.Location.SourceSpan);
            var access = node.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>().FirstOrDefault();
            if (access is null) return "";
            var model = compilation.GetSemanticModel(tree);
            var type = model.GetTypeInfo(access.Expression).Type ?? model.GetSymbolInfo(access.Expression).Symbol as ITypeSymbol;
            if (type is null || type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Error) return "";
            var names = new List<string>();
            for (var t = type; t is not null && t.SpecialType != Microsoft.CodeAnalysis.SpecialType.System_Object; t = t.BaseType)
                names.AddRange(Members(t).Where(m => m.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public && m.CanBeReferencedByName
                    && m.Kind is Microsoft.CodeAnalysis.SymbolKind.Property or Microsoft.CodeAnalysis.SymbolKind.Method or Microsoft.CodeAnalysis.SymbolKind.Field or Microsoft.CodeAnalysis.SymbolKind.Event).Select(m => m.Name));
            var close = ScriptSource.Closest(access.Name.Identifier.ValueText, names);
            return close.Count > 0 ? $" (closest members of {type.Name}: {string.Join(", ", close)})" : "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>A type's members without naming ImmutableArray in this assembly (see Compile).</summary>
    private static IEnumerable<ISymbol> Members(ITypeSymbol type) =>
        (typeof(INamespaceOrTypeSymbol).GetMethod("GetMembers", Type.EmptyTypes)?.Invoke(type, null) as System.Collections.IEnumerable)?.OfType<ISymbol>() ?? [];

    /// <summary>EmitResult.Diagnostics without naming its ImmutableArray type in this assembly (see Compile).</summary>
    private static IEnumerable<Diagnostic> Diagnostics(Microsoft.CodeAnalysis.Emit.EmitResult result) =>
        (result.GetType().GetProperty("Diagnostics")?.GetValue(result) as System.Collections.IEnumerable)?.OfType<Diagnostic>() ?? [];

    /// <summary>Line within the snippet where an exception was thrown, when the stack trace has it.</summary>
    public static int? LineOf(Exception ex)
    {
        var trace = new System.Diagnostics.StackTrace(ex, true);
        foreach (var frame in trace.GetFrames() ?? [])
            if (frame.GetFileName() == "snippet" && frame.GetFileLineNumber() > 0) return frame.GetFileLineNumber();
        return null;
    }

    /// <summary>Loads a compiled snippet. On .NET 8+ also returns its load context, so it can be unloaded later.</summary>
    private static (Assembly Assembly, object? Context) Load(byte[] image, byte[] symbols)
    {
#if NETFRAMEWORK
        // .NET Framework can't unload an assembly short of the whole AppDomain; the cache limit keeps this small.
        return (Assembly.Load(image, symbols), null);
#else
        // Each snippet gets its own collectible context, unloaded when it leaves the cache, so a long session
        // doesn't keep every snippet it ever ran in memory. Revit API types resolve from the context that loaded Revit.
        var context = new System.Runtime.Loader.AssemblyLoadContext("SketchProMaxScript", isCollectible: true);
        return (context.LoadFromStream(new MemoryStream(image), new MemoryStream(symbols)), context);
#endif
    }

    /// <summary>Empties the cache. Snippets only run on Revit's main thread, as does this, so none is running now.</summary>
    private static void Evict()
    {
#if !NETFRAMEWORK
        // Unload only starts the process: a context stays alive while anything (an event handler a snippet
        // registered, say) still references it, and is freed once nothing does.
        foreach (var entry in Cache.Values)
            if (entry.Context is System.Runtime.Loader.AssemblyLoadContext context) context.Unload();
#endif
        Cache.Clear();
    }

    private static List<MetadataReference> References()
    {
        if (_references is not null) return _references;

        // One reference per assembly name; the first one found wins.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var name = Path.GetFileNameWithoutExtension(path!);
            if (!byName.ContainsKey(name)) byName[name] = path!;
        }

        Add(typeof(Document).Assembly.Location);
        Add(typeof(UIApplication).Assembly.Location);
#if NETFRAMEWORK
        Add(typeof(object).Assembly.Location);
        var facades = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "Facades", "netstandard.dll");
        Add(facades);
#else
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            foreach (var path in tpa.Split(Path.PathSeparator))
                if (Path.GetFileName(path).StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase))
                    Add(path);
#endif
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            var name = assembly.GetName().Name ?? "";
            if (name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.CSharp", StringComparison.Ordinal) ||
                name is "mscorlib" or "netstandard" or "RevitAPI" or "RevitAPIUI")
            {
                try { Add(assembly.Location); }
                catch (NotSupportedException) { }
            }
        }

        _references = byName.Values.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        return _references;
    }
}
