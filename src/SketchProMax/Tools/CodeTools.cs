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
/// The read-only variant runs without a transaction, so Revit itself refuses any attempt to change the model.
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

    public ToolResult Execute(UIApplication app, ToolArgs args) =>
        RunScript(app, args.String("code"), args.String("description"), ModifiesModel, new Dictionary<string, object?>());

    /// <summary>Compiles and runs a snippet, inside one transaction when it changes the model. Learned tools use this too.</summary>
    internal static ToolResult RunScript(UIApplication app, string code, string description, bool modifiesModel,
        IDictionary<string, object?> values)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc.Document;

        var (run, errors) = ScriptCompiler.Compile(code);
        if (run is null) return ToolResult.Error("The code did not compile:\n" + errors);

        var log = new StringBuilder();
        object? result;
        if (modifiesModel)
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
            try { result = run(app, uidoc, doc, log, values); }
            catch (Exception ex) { return ToolResult.Error($"The code failed. {Unwrap(ex)}\n{log}"); }
        }

        return new ToolResult(JsonSerializer.Serialize(new { result = SafeJson(result), log = log.Length > 0 ? log.ToString() : null }));
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
        "geometry or setting). Runs without a transaction, so it cannot change the model." + CodeGuidance;
    public override bool ModifiesModel => false;
}

public sealed class RunRevitCodeTool : RevitCodeToolBase
{
    public override string Name => "run_revit_code";
    public override string Description =>
        "Run C# that changes the model through the Revit API, for anything the other tools can't do (stairs, railings, " +
        "materials, curtain walls, MEP, bulk edits...). The code runs inside one transaction that is opened and committed " +
        "for you: do not start your own. If it throws, everything is rolled back. The user sees the code before it runs " +
        "and can undo it with Ctrl+Z." + CodeGuidance;
    public override bool ModifiesModel => true;
}

/// <summary>Compiles snippets with Roslyn, caching by source so a retried snippet doesn't compile twice.</summary>
internal static class ScriptCompiler
{
    public delegate object? Script(UIApplication app, UIDocument uidoc, Document doc, StringBuilder log, IDictionary<string, object?> args);

    private const string Header = """
        using System;
        using System.Linq;
        using System.Collections.Generic;
        using Autodesk.Revit.DB;
        using Autodesk.Revit.DB.Architecture;
        using Autodesk.Revit.DB.Structure;
        using Autodesk.Revit.DB.Mechanical;
        using Autodesk.Revit.DB.Plumbing;
        using Autodesk.Revit.DB.Electrical;
        using Autodesk.Revit.UI;
        public static class SketchProMaxScript
        {
            public static object Run(UIApplication app, UIDocument uidoc, Document doc, System.Text.StringBuilder log, IDictionary<string, object> args)
            {
        #line 1 "snippet"
        """;

    private const string Footer = """

        #line default
            }
        }
        """;

    private static readonly Dictionary<string, Script> Cache = new();
    private static List<MetadataReference>? _references;

    public static (Script? Run, string Errors) Compile(string code)
    {
        if (Cache.TryGetValue(code, out var cached)) return (cached, "");

        var tree = CSharpSyntaxTree.ParseText(Header + code + Footer, new CSharpParseOptions(LanguageVersion.Latest));
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

        var assembly = Load(pe.ToArray(), pdb.ToArray());
        var method = assembly.GetType("SketchProMaxScript")!.GetMethod("Run")!;
        Script run = (app, uidoc, doc, log, args) => method.Invoke(null, [app, uidoc, doc, log, args]);
        if (Cache.Count > 50) Cache.Clear();
        Cache[code] = run;
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

    private static Assembly Load(byte[] image, byte[] symbols)
    {
#if NETFRAMEWORK
        return Assembly.Load(image, symbols);
#else
        // Each snippet gets its own context; Revit API types resolve from the context that loaded Revit.
        var context = new System.Runtime.Loader.AssemblyLoadContext("SketchProMaxScript", isCollectible: false);
        return context.LoadFromStream(new MemoryStream(image), new MemoryStream(symbols));
#endif
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
