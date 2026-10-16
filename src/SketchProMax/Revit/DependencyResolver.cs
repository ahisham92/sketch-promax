#if NETFRAMEWORK
using System.IO;
using System.Reflection;

namespace SketchProMax.Revit;

/// <summary>
/// Revit 2024 runs on .NET Framework 4.8, where an add-in cannot ship binding redirects. This resolves the assemblies
/// that ship next to SketchProMax.dll (System.Text.Json, System.Memory, System.Collections.Immutable, Roslyn, ...) by
/// simple name, ignoring the requested version.
///
/// A request from this core, or from anything loaded from its folder, is answered with Assembly.LoadFrom of the file in
/// this folder. LoadFrom returns the one LoadFrom-context assembly with that identity, which is also what Roslyn and
/// System.Text.Json (loaded from this folder) bind to on their own, so this DLL and they share one System.Memory and
/// one System.Collections.Immutable. It never hands out "whatever is loaded under that name": another add-in's copy
/// (a different version, or one loaded from bytes) is what made run_revit_code and run_recipe fail with
/// MissingMethodException on Revit 2024. The loader's resolver (SketchProMax.Loader.Resolver) does the same for every
/// core folder; this one also covers a core Revit loaded directly from an old manifest.
/// </summary>
internal static class DependencyResolver
{
    private static readonly string Folder = Path.GetDirectoryName(typeof(DependencyResolver).Assembly.Location)!;
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        AddFirst(AppDomain.CurrentDomain, Resolve);
        // Requests nobody else answered and that don't say who asks go last, so other add-ins' resolvers answer theirs.
        AppDomain.CurrentDomain.AssemblyResolve += ResolveLast;
    }

    public static void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
        AppDomain.CurrentDomain.AssemblyResolve -= ResolveLast;
    }

    /// <summary>
    /// Puts the handler first: .NET Framework asks the AssemblyResolve handlers in order and takes the first non-null
    /// answer, so another add-in's resolver would otherwise answer for our assemblies. Falls back to a plain subscription.
    /// </summary>
    private static void AddFirst(AppDomain domain, ResolveEventHandler handler)
    {
        try
        {
            var field = typeof(AppDomain).GetField("_AssemblyResolve", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null && field.FieldType == typeof(ResolveEventHandler))
            {
                lock (domain)
                {
                    var current = field.GetValue(domain) as ResolveEventHandler;
                    field.SetValue(domain, (ResolveEventHandler)Delegate.Combine(handler, current));
                }
                return;
            }
        }
        catch (Exception ex) when (ex is FieldAccessException or MemberAccessException or ArgumentException or System.Security.SecurityException) { }
        domain.AssemblyResolve += handler;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        string name;
        try { name = new AssemblyName(args.Name).Name ?? ""; }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException) { return null; }
        // After an update is switched to without restarting Revit, two versions of the core are loaded: never hand one
        // version's SketchProMax.dll (or the loader) to the other.
        if (name == "SketchProMax") return args.Name == typeof(DependencyResolver).Assembly.FullName ? typeof(DependencyResolver).Assembly : null;

        // The loader answers for itself; loading a second copy from a versions folder would split its state.
        if (name == "SketchProMax.Loader") return null;

        // First in line: only requests from this core's folder. Everything else waits for ResolveLast.
        var requester = args.RequestingAssembly is { IsDynamic: false } r ? FolderOf(r) : null;
        if (requester is null || !string.Equals(requester, Folder, StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(Folder, name + ".dll");
        return File.Exists(path) ? LoadFrom(path) : null;
    }

    /// <summary>
    /// Last in line, for requests that don't say who asks (Type.GetType, XAML, serializers, a run_revit_code snippet
    /// loaded from bytes): our file when it is at least the version asked for, so a snippet binds to the same copy as
    /// this core, else the exact version already loaded.
    /// </summary>
    private static Assembly? ResolveLast(object? sender, ResolveEventArgs args)
    {
        AssemblyName wanted;
        try { wanted = new AssemblyName(args.Name); }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException) { return null; }
        var name = wanted.Name ?? "";
        if (name is "SketchProMax" or "SketchProMax.Loader") return null;
        if (args.RequestingAssembly is { IsDynamic: false } r && FolderOf(r) is not null) return null;
        var path = Path.Combine(Folder, name + ".dll");
        if (File.Exists(path) && AtLeast(path, wanted.Version)) return LoadFrom(path);
        return Exact(args.Name);
    }

    private static bool AtLeast(string path, Version? wanted)
    {
        if (wanted is null) return true;
        try { return AssemblyName.GetAssemblyName(path).Version is { } v && v >= wanted; }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException or System.Security.SecurityException) { return false; }
    }

    private static Assembly? LoadFrom(string path)
    {
        try { return Assembly.LoadFrom(path); }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or System.Security.SecurityException) { return null; }
    }

    /// <summary>An already loaded assembly with exactly this identity, preferring one loaded from a file.</summary>
    private static Assembly? Exact(string fullName)
    {
        Assembly? fromBytes = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic || a.FullName != fullName) continue;
            if (LocationOf(a).Length > 0) return a;
            fromBytes ??= a;
        }
        return fromBytes;
    }

    internal static string LocationOf(Assembly a)
    {
        try { return a.IsDynamic ? "" : a.Location ?? ""; }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { return ""; }
    }

    private static string? FolderOf(Assembly a)
    {
        var location = LocationOf(a);
        if (location.Length == 0) return null;
        try { return Path.GetDirectoryName(location); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { return null; }
    }
}
#endif
