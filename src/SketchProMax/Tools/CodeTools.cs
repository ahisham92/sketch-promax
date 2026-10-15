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
/// The read-only variant runs inside a transaction group that is always rolled back, so nothing it does is kept.
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

    protected const string CodeGuidance =
        " Write a C# method body using the Revit API of the running Revit version. Use dedicated tools first when one fits; " +
        "use this for anything they can't do. If compilation fails, the errors come back with line numbers so you can fix and retry.";

    public virtual ToolResult Execute(UIApplication app, ToolArgs args) =>
        RunScript(app, args.String("code"), args.String("description"), ModifiesModel, new Dictionary<string, object?>());

    /// <summary>
    /// Compiles and runs a snippet, inside one transaction when it changes the model. Learned tools use this too.
    /// With ownTransactions the snippet opens and commits its own transactions (needed for StairsEditScope and other
    /// edit scopes, which cannot start inside an open transaction); a transaction group around it keeps the run
    /// atomic and makes it one Undo step.
    /// </summary>
    internal static ToolResult RunScript(UIApplication app, string code, string description, bool modifiesModel,
        IDictionary<string, object?> values, bool ownTransactions = false)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc.Document;

        var (run, errors) = ScriptCompiler.Compile(code);
        if (run is null) return ToolResult.Error("The code did not compile:\n" + errors);

        var log = new StringBuilder();
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
            // Without a transaction Revit refuses changes, but a snippet could open and commit its own; the group
            // around it is always rolled back, so nothing a read-only snippet does is kept.
            var group = doc.IsReadOnly ? null : new TransactionGroup(doc, "Sketch ProMax: read-only code (rolled back)");
            try
            {
                group?.Start();
                result = run(app, uidoc, doc, log, values);
            }
            catch (Exception ex) { return ToolResult.Error($"The code failed. {Unwrap(ex)}\n{log}"); }
            finally
            {
                if (group is not null)
                {
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                    group.Dispose();
                }
            }
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
        return $"{ex.GetType().Name}: {ex.Message}{(line is null ? "" : $" (line {line})")}";
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
        "geometry or setting). Anything it changes is rolled back, so it cannot change the model." + CodeGuidance;
    public override bool ModifiesModel => false;
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
            args.OptBool("own_transactions"));
}

/// <summary>Compiles snippets with Roslyn, caching by source so a retried snippet doesn't compile twice.</summary>
internal static class ScriptCompiler
{
    public delegate object? Script(UIApplication app, UIDocument uidoc, Document doc, StringBuilder log, IDictionary<string, object?> args);

    private sealed record Entry(Script Run, object? Context);

    private static readonly Dictionary<string, Entry> Cache = new();
    private static List<MetadataReference>? _references;

    public static (Script? Run, string Errors) Compile(string code)
    {
        if (Cache.TryGetValue(code, out var cached)) return (cached.Run, "");

        var tree = CSharpSyntaxTree.ParseText(ScriptSource.Wrap(code), new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "SketchProMaxScript_" + Guid.NewGuid().ToString("N"),
            [tree],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Disable));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emitted = compilation.Emit(pe, pdb);
        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(15)
                .Select(d => $"line {d.Location.GetMappedLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}");
            return (null, string.Join("\n", errors));
        }

        var (assembly, context) = Load(pe.ToArray(), pdb.ToArray());
        var method = assembly.GetType("SketchProMaxScript")!.GetMethod("Run")!;
        Script run = (app, uidoc, doc, log, args) => method.Invoke(null, [app, uidoc, doc, log, args]);
        if (Cache.Count > 50) Evict();
        Cache[code] = new Entry(run, context);
        return (run, "");
    }

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
