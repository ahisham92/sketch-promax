#if NETFRAMEWORK
using System.IO;
using System.Reflection;

namespace SketchProMax.Revit;

/// <summary>
/// Revit 2024 runs on .NET Framework 4.8, where an add-in cannot ship binding redirects.
/// The Claude SDK depends on newer System.* packages (System.Text.Json, System.Memory, ...)
/// whose exact versions Revit won't find on its own. This resolves any assembly that sits
/// next to SketchProMax.dll by simple name, ignoring the requested version.
/// </summary>
internal static class DependencyResolver
{
    private static readonly string Folder = Path.GetDirectoryName(typeof(DependencyResolver).Assembly.Location)!;

    public static void Register() => AppDomain.CurrentDomain.AssemblyResolve += Resolve;

    public static void Unregister() => AppDomain.CurrentDomain.AssemblyResolve -= Resolve;

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        // After an update is switched to without restarting Revit, two versions of the core are loaded: never hand one
        // version's SketchProMax.dll (or the loader) to the other. The exact version wins, then any by name.
        if (name == "SketchProMax") return args.Name == typeof(DependencyResolver).Assembly.FullName ? typeof(DependencyResolver).Assembly : null;
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
        var already = loaded.FirstOrDefault(a => a.FullName == args.Name) ?? loaded.FirstOrDefault(a => a.GetName().Name == name);
        if (already != null) return already;

        var path = Path.Combine(Folder, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
#endif
