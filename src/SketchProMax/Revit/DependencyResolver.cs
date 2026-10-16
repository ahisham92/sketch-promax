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
    }

    public static void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
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

        var requester = args.RequestingAssembly is { IsDynamic: false } r ? FolderOf(r) : null;
        var ours = requester is not null && string.Equals(requester, Folder, StringComparison.OrdinalIgnoreCase);
        // Another add-in's own dependencies are left to it. When nobody says who is asking (Type.GetType, XAML,
        // serializers, a run_revit_code snippet loaded from bytes): the exact version already loaded, else our file.
        if (!ours && requester is not null) return null;

        var path = Path.Combine(Folder, name + ".dll");
        if (ours && File.Exists(path)) return LoadFrom(path);
        var exact = Exact(args.Name);
        if (exact is not null) return exact;
        return File.Exists(path) ? LoadFrom(path) : null;
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
