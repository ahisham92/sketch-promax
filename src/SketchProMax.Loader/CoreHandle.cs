using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;

namespace SketchProMax.Loader;

/// <summary>
/// One loaded version of the core (SketchProMax.dll), driven through its static SketchProMax.CoreEntry class by reflection,
/// so the loader never binds to a particular core version at compile time.
///
/// Revit 2024 (.NET Framework 4.8): each version is loaded with Assembly.LoadFile, which loads that exact file even when
/// another SketchProMax.dll is already loaded, and its dependencies are resolved from its own folder by
/// <see cref="Resolver"/>. .NET 8+: each version gets its own AssemblyLoadContext that loads the core's private
/// dependencies from its folder and takes the loader, the Revit API and the framework from the context they are already in.
/// Nothing is ever unloaded; an old version stays in memory, idle, until Revit closes.
/// </summary>
internal sealed class CoreHandle
{
    /// <summary>The CoreEntry contract this loader speaks. A core that needs a newer loader says so and is not started.</summary>
    public const int Contract = 1;

    public Assembly Assembly { get; }
    public Version Version { get; }
    public string Path { get; }
    public string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(Version.Build, 0)}";

    private readonly MethodInfo _start, _stop, _command, _query, _shutdown;

    private CoreHandle(Assembly assembly, string path)
    {
        Assembly = assembly;
        Path = path;
        Version = assembly.GetName().Version ?? new Version(0, 0, 0);
        var entry = assembly.GetType("SketchProMax.CoreEntry", throwOnError: false)
                    ?? throw new InvalidOperationException($"{path} is too old to be switched to without a restart (no CoreEntry).");
        var needs = entry.GetField("MinimumLoaderContract")?.GetValue(null) as int? ?? 1;
        if (needs > Contract)
            throw new InvalidOperationException($"Sketch ProMax {VersionText} needs a newer loader; it starts after Revit is restarted.");
        MethodInfo M(string name) => entry.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                                     ?? throw new InvalidOperationException($"CoreEntry.{name} is missing in {path}.");
        _start = M("Start");
        _stop = M("Stop");
        _command = M("Command");
        _query = M("Query");
        _shutdown = M("Shutdown");
    }

    /// <summary>Wraps a core Revit already loaded (an older manifest pointed straight at SketchProMax.dll).</summary>
    public static CoreHandle FromLoaded(Assembly assembly) => new(assembly, assembly.Location);

    /// <summary>Loads the core at <paramref name="path"/> side by side with anything already loaded.</summary>
    public static CoreHandle Load(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
#if NETFRAMEWORK
        Resolver.AddFolder(folder);
        var assembly = Assembly.LoadFile(path);
#else
        var assembly = new CoreContext(folder).LoadFromAssemblyPath(path);
#endif
        var handle = new CoreHandle(assembly, path);
#if NETFRAMEWORK
        Resolver.Loaded.Add(assembly);
#endif
        return handle;
    }

    public void Start(UIApplication app) => Invoke(_start, app);

    public void Stop(UIApplication app) => Invoke(_stop, app);

    public void Shutdown() => Invoke(_shutdown);

    /// <summary>Runs a ribbon command in the core. Returns a Revit Result as int (0 succeeded, 1 cancelled, -1 failed).</summary>
    public int Command(string name, UIApplication app) => Invoke(_command, name, app) as int? ?? -1;

    /// <summary>A small text answer for the ribbon ("version", "claude", "job", "update", "quick", "busy", ...).</summary>
    public string Query(string key)
    {
        try { return Invoke(_query, key) as string ?? ""; }
        catch (Exception) { return ""; }
    }

    private static object? Invoke(MethodInfo method, params object?[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>The version of a SketchProMax.dll on disk, read without loading it.</summary>
    public static Version? PeekVersion(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version; }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}

#if NETFRAMEWORK
/// <summary>
/// Resolves the dependencies of cores loaded with Assembly.LoadFile (System.Text.Json, Roslyn, ...) from their own
/// folders, newest first, reusing an already loaded assembly when it is the exact same version.
/// </summary>
internal static class Resolver
{
    private static readonly List<string> Folders = [];
    public static readonly List<Assembly> Loaded = [];
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    public static void AddFolder(string folder)
    {
        Register();
        lock (Folders)
        {
            Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            Folders.Insert(0, folder);
        }
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        AssemblyName wanted;
        try { wanted = new AssemblyName(args.Name); }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException) { return null; }
        var name = wanted.Name ?? "";
        if (name == "SketchProMax.Loader") return typeof(Resolver).Assembly;
        if (name == "SketchProMax")
            lock (Folders) return Loaded.FirstOrDefault(a => a.FullName == args.Name);

        var exact = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.FullName == args.Name);
        if (exact is not null) return exact;

        List<string> folders;
        lock (Folders) folders = Folders.ToList();
        // The folder of the assembly asking, when it is one of ours, before the others.
        var asking = args.RequestingAssembly is { IsDynamic: false } r ? SafeFolder(r) : null;
        if (asking is not null && folders.Any(f => string.Equals(f, asking, StringComparison.OrdinalIgnoreCase)))
        {
            folders.RemoveAll(f => string.Equals(f, asking, StringComparison.OrdinalIgnoreCase));
            folders.Insert(0, asking);
        }
        foreach (var folder in folders)
        {
            var path = System.IO.Path.Combine(folder, name + ".dll");
            if (!File.Exists(path)) continue;
            try { return Assembly.LoadFrom(path); }
            catch (Exception ex) when (ex is IOException or BadImageFormatException) { }
        }
        return null;
    }

    private static string? SafeFolder(Assembly a)
    {
        try { return System.IO.Path.GetDirectoryName(a.Location); }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { return null; }
    }
}
#else
/// <summary>
/// The load context of one core version. Its private dependencies (Roslyn, for run_revit_code) come from its folder;
/// the loader, the Revit API and everything the runtime ships come from the context they are already loaded in, so
/// types such as UIApplication are the same for the loader, every core and Revit.
/// </summary>
internal sealed class CoreContext : System.Runtime.Loader.AssemblyLoadContext
{
    private static readonly HashSet<string> Platform = PlatformAssemblies();
    private readonly string _folder;

    public CoreContext(string folder) : base("SketchProMax " + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(folder)), isCollectible: false)
        => _folder = folder;

    protected override Assembly? Load(AssemblyName name)
    {
        var simple = name.Name ?? "";
        if (simple == "SketchProMax.Loader") return typeof(CoreContext).Assembly;
        if (Platform.Contains(simple) || simple.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase)
            || simple is "AdWindows" or "UIFramework" or "UIFrameworkServices") return null;
        var path = System.IO.Path.Combine(_folder, simple + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    private static HashSet<string> PlatformAssemblies()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            foreach (var p in tpa.Split(System.IO.Path.PathSeparator))
                if (p.Length > 0) set.Add(System.IO.Path.GetFileNameWithoutExtension(p));
        return set;
    }
}
#endif
