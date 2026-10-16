using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SketchProMax.Revit;

/// <summary>
/// Which copies of the shared runtime libraries are loaded, and which copy this core, Roslyn and System.Text.Json each
/// actually bound to. On Revit 2024 (.NET Framework) a mismatch (this DLL on one System.Memory or
/// System.Collections.Immutable, Roslyn or System.Text.Json on another) shows up as MissingMethodException in
/// run_revit_code and run_recipe; this makes it visible in reports.
/// </summary>
public static class AssemblyReport
{
    private static readonly string[] Watched =
    [
        "System.Memory", "System.Collections.Immutable", "System.Text.Json", "Microsoft.CodeAnalysis",
        "Microsoft.CodeAnalysis.CSharp", "System.Runtime.CompilerServices.Unsafe", "SketchProMax",
    ];

    private sealed record Use(string Who, string What, Assembly? Copy, string? Error);

    /// <summary>A short Markdown section: every loaded copy of the watched assemblies, who uses which, and a verdict.</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Assemblies");
        sb.AppendLine();
        List<Use> uses;
        List<Assembly> loaded;
        try
        {
            uses = Uses();
            loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
        }
        catch (Exception ex)
        {
            return sb.AppendLine($"Could not inspect the loaded assemblies: {ex.GetType().Name}: {ex.Message}").ToString();
        }
        sb.AppendLine(Verdict(uses));
        sb.AppendLine();
        foreach (var name in Watched)
        {
            var copies = loaded.Where(a => SafeName(a) == name).ToList();
            sb.AppendLine($"- **{name}**: {(copies.Count == 0 ? "not loaded" : copies.Count == 1 ? "1 copy" : $"{copies.Count} copies")}");
            for (var i = 0; i < copies.Count; i++)
            {
                var users = uses.Where(u => u.Copy == copies[i]).Select(u => u.Who).Distinct().ToList();
                if (name == "SketchProMax" && copies[i] == typeof(AssemblyReport).Assembly) users.Insert(0, "this core");
                sb.AppendLine($"  {i + 1}. {Version(copies[i])} at {Where(copies[i])}" +
                              (users.Count > 0 ? $" (used by {string.Join(", ", users)})" : ""));
            }
            foreach (var use in uses.Where(u => u.What == name && (u.Error is not null || (u.Copy is not null && !copies.Contains(u.Copy)))))
                sb.AppendLine($"  - {use.Who}: {use.Error ?? $"{Version(use.Copy!)} at {Where(use.Copy!)}"}");
        }
        return sb.ToString();
    }

    /// <summary>One line for the Status dialog.</summary>
    public static string Summary()
    {
        try { return "Libraries: " + Verdict(Uses()); }
        catch (Exception ex) { return "Libraries: could not check (" + ex.GetType().Name + ")"; }
    }

    private static string Verdict(List<Use> uses)
    {
        var problems = new List<string>();
        foreach (var group in uses.GroupBy(u => u.What))
        {
            var failed = group.Where(u => u.Error is not null).ToList();
            foreach (var f in failed) problems.Add($"{f.Who} can't load {group.Key} ({f.Error})");
            var copies = group.Where(u => u.Copy is not null).Select(u => u.Copy!).Distinct().ToList();
            if (copies.Count > 1)
                problems.Add($"{group.Key} differs: " + string.Join("; ", group.Where(u => u.Copy is not null)
                    .Select(u => $"{u.Who} uses {Version(u.Copy!)} at {Where(u.Copy!)}")));
        }
        return problems.Count == 0
            ? "OK: this core, Roslyn and System.Text.Json share one System.Memory and one System.Collections.Immutable."
            : "MISMATCH (run_revit_code and run_recipe may fail with MissingMethodException): " + string.Join(" | ", problems);
    }

    /// <summary>The copy each of us bound to, found through types that each assembly's own metadata references.</summary>
    private static List<Use> Uses()
    {
        var uses = new List<Use>();
        void Add(string who, string what, Func<Assembly?> find)
        {
            try { uses.Add(new Use(who, what, find(), null)); }
            catch (Exception ex) { uses.Add(new Use(who, what, null, $"{ex.GetType().Name}: {First(ex.Message)}")); }
        }

        Add("SketchProMax", "System.Memory", () => typeof(ReadOnlyMemory<byte>).Assembly);
        Add("SketchProMax", "System.Collections.Immutable", () => typeof(System.Collections.Immutable.ImmutableArray<byte>).Assembly);
        Add("SketchProMax", "System.Text.Json", () => typeof(JsonDocument).Assembly);
        Add("SketchProMax", "Microsoft.CodeAnalysis", () => typeof(Compilation).Assembly);
        Add("SketchProMax", "Microsoft.CodeAnalysis.CSharp", () => typeof(CSharpCompilation).Assembly);

        Add("Microsoft.CodeAnalysis.CSharp", "Microsoft.CodeAnalysis", () => typeof(CSharpCompilation).BaseType?.Assembly);
        Add("Microsoft.CodeAnalysis.CSharp", "System.Collections.Immutable", () => ParameterAssembly(typeof(CSharpCompilationOptions), "System.Collections.Immutable"));
        Add("Microsoft.CodeAnalysis", "System.Collections.Immutable", () =>
            typeof(Microsoft.CodeAnalysis.Emit.EmitResult).GetProperty("Diagnostics")?.PropertyType.Assembly);
        Add("Microsoft.CodeAnalysis", "System.Memory", () => ParameterAssembly(typeof(Compilation).Assembly, "System.Memory", typeof(Microsoft.CodeAnalysis.Text.SourceText)));
        Add("System.Text.Json", "System.Memory", () => ParameterAssembly(typeof(JsonDocument), "System.Memory"));
        Add("System.Collections.Immutable", "System.Memory", () =>
            ParameterAssembly(typeof(System.Collections.Immutable.ImmutableArray<byte>), "System.Memory"));
        // On .NET 8+ the span types live in System.Private.CoreLib: nobody "uses" System.Memory, which is fine.
        uses.RemoveAll(u => u.What == "System.Memory" && u.Copy is not null && SafeName(u.Copy) != "System.Memory");
        uses.RemoveAll(u => u.Copy is null && u.Error is null);
        return uses;
    }

    /// <summary>The assembly named <paramref name="target"/> that a public signature on <paramref name="type"/> names.</summary>
    private static Assembly? ParameterAssembly(Type type, string target)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            try
            {
                var types = method.GetParameters().Select(p => p.ParameterType);
                if (method is MethodInfo mi) types = types.Append(mi.ReturnType);
                foreach (var t in types)
                    if (FindIn(t, target, 0) is { } found) return found;
            }
            catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException or NotSupportedException) { }
        }
        // For a generic struct such as ImmutableArray<T> the span types appear on its open definition too.
        if (type.IsGenericType && !type.IsGenericTypeDefinition) return ParameterAssembly(type.GetGenericTypeDefinition(), target);
        return null;
    }

    private static Assembly? ParameterAssembly(Assembly host, string target, params Type[] first)
    {
        foreach (var t in first)
            if (ParameterAssembly(t, target) is { } found) return found;
        Type[] exported;
        try { exported = host.GetExportedTypes(); }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException or NotSupportedException) { return null; }
        foreach (var t in exported.Take(400))
            if (ParameterAssembly(t, target) is { } found) return found;
        return null;
    }

    private static Assembly? FindIn(Type t, string target, int depth)
    {
        if (depth > 4) return null;
        if (t.HasElementType) return FindIn(t.GetElementType()!, target, depth + 1);
        if (t.IsGenericParameter) return null;
        if (SafeName(t.Assembly) == target) return t.Assembly;
        if (t.IsGenericType)
            foreach (var a in t.GetGenericArguments())
                if (FindIn(a, target, depth + 1) is { } found) return found;
        return null;
    }

    private static string SafeName(Assembly a)
    {
        try { return a.GetName().Name ?? ""; }
        catch (Exception) { return ""; }
    }

    private static string Version(Assembly a)
    {
        try { return a.GetName().Version?.ToString() ?? "?"; }
        catch (Exception) { return "?"; }
    }

    private static string Where(Assembly a)
    {
        string location;
        try { location = a.IsDynamic ? "" : a.Location ?? ""; }
        catch (Exception) { location = ""; }
        var where = location.Length > 0 ? location : "(loaded from bytes, no file)";
#if NETFRAMEWORK
        try { if (a.GlobalAssemblyCache) where += " [GAC]"; } catch (Exception) { }
#else
        try
        {
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(a);
            if (context is not null) where += $" [{context.Name}]";
        }
        catch (Exception) { }
#endif
        return where;
    }

    private static string First(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 200 ? line.Substring(0, 200) + "..." : line;
    }
}
