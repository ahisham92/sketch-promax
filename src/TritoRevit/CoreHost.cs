using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;

namespace TritoRevit;

/// <summary>
/// One loaded Sketch ProMax core (SketchProMax.dll from %LOCALAPPDATA%\TritoRevit\versions\&lt;version&gt;\Revit&lt;year&gt;),
/// started in its shared-tools mode (CoreEntry.StartShared): no chat, no connection to the Claude app, no Sketch ProMax
/// ribbon; it runs the shared tools and keeps them and itself up to date. Driven by reflection through its static
/// CoreEntry class, the way the Sketch ProMax loader drives it (SketchProMax.Loader CoreHandle), so any core version works.
/// Revit 2024 (.NET Framework 4.8): each version is loaded with Assembly.LoadFile and its dependencies resolved from its
/// own folder (<see cref="Resolver"/>). .NET 8+: each version gets its own load context. Nothing is unloaded.
/// </summary>
internal sealed class CoreHost
{
    public Assembly Assembly { get; }
    public Version Version { get; }
    public string Path { get; }
    public string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(Version.Build, 0)}";

    private readonly MethodInfo _start, _stop, _command, _query, _shutdown;

    private CoreHost(Assembly assembly, string path)
    {
        Assembly = assembly;
        Path = path;
        Version = assembly.GetName().Version ?? new Version(0, 0, 0);
        var entry = assembly.GetType("SketchProMax.CoreEntry", throwOnError: false)
                    ?? throw new InvalidOperationException($"{path} is not a Sketch ProMax core.");
        MethodInfo M(string name) => entry.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                                     ?? throw new InvalidOperationException($"Sketch ProMax {VersionText} is too old for TritoRevit (no CoreEntry.{name}).");
        _start = M("StartShared");
        _stop = M("Stop");
        _command = M("Command");
        _query = M("Query");
        _shutdown = M("Shutdown");
    }

    public static CoreHost Load(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
#if NETFRAMEWORK
        Resolver.AddFolder(folder);
        var assembly = Assembly.LoadFile(path);
#else
        var assembly = new CoreContext(folder).LoadFromAssemblyPath(path);
#endif
        return new CoreHost(assembly, path);
    }

    public void Start(UIApplication app) => Invoke(_start, app);

    public void Stop(UIApplication app) => Invoke(_stop, app);

    public void Shutdown() => Invoke(_shutdown);

    /// <summary>Runs a command in the core: 0 succeeded, 1 cancelled, -1 failed.</summary>
    public int Command(string name, UIApplication app) => Invoke(_command, name, app) as int? ?? -1;

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
/// Resolves the dependencies of cores loaded with Assembly.LoadFile (System.Text.Json, Roslyn, System.Memory, ...), the
/// way the Sketch ProMax loader's resolver does (see its comments for why): a request from one of our core folders is
/// answered with that folder's file through LoadFrom, put first in the AppDomain's list; requests that don't say who
/// asks go last, so other add-ins answer theirs first. It never answers for Sketch ProMax's own add-in, which TritoRevit
/// never runs beside its own core (it uses that one instead).
/// </summary>
internal static class Resolver
{
    private static readonly List<string> Folders = [];
    private static bool _registered;

    public static void AddFolder(string folder)
    {
        lock (Folders)
        {
            if (!_registered)
            {
                _registered = true;
                AddFirst(AppDomain.CurrentDomain, Resolve);
                AppDomain.CurrentDomain.AssemblyResolve += ResolveLast;
            }
            Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            Folders.Insert(0, folder);
        }
    }

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
        AssemblyName wanted;
        try { wanted = new AssemblyName(args.Name); }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException) { return null; }
        var name = wanted.Name ?? "";
        if (name == "SketchProMax") return null;
        var asking = args.RequestingAssembly is { IsDynamic: false } r ? SafeFolder(r) : null;
        if (asking is null) return null;
        string? own;
        lock (Folders) own = Folders.FirstOrDefault(f => string.Equals(f, asking, StringComparison.OrdinalIgnoreCase));
        return own is null ? null : LoadFrom(System.IO.Path.Combine(own, name + ".dll")) ?? Exact(args.Name);
    }

    private static Assembly? ResolveLast(object? sender, ResolveEventArgs args)
    {
        AssemblyName wanted;
        try { wanted = new AssemblyName(args.Name); }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException) { return null; }
        var name = wanted.Name ?? "";
        if (name == "SketchProMax") return null;
        if (args.RequestingAssembly is { IsDynamic: false } r && SafeFolder(r) is not null) return null;
        List<string> folders;
        lock (Folders) folders = Folders.ToList();
        foreach (var folder in folders)
        {
            var path = System.IO.Path.Combine(folder, name + ".dll");
            if (!File.Exists(path)) continue;
            if (AtLeast(path, wanted.Version)) return LoadFrom(path);
            break;
        }
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
        if (!File.Exists(path)) return null;
        try { return Assembly.LoadFrom(path); }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or System.Security.SecurityException) { return null; }
    }

    private static Assembly? Exact(string fullName)
    {
        Assembly? fromBytes = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic || a.FullName != fullName) continue;
            string? location;
            try { location = a.Location; }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { location = null; }
            if (!string.IsNullOrEmpty(location)) return a;
            fromBytes ??= a;
        }
        return fromBytes;
    }

    private static string? SafeFolder(Assembly a)
    {
        try
        {
            var location = a.Location;
            return string.IsNullOrEmpty(location) ? null : System.IO.Path.GetDirectoryName(location);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or PathTooLongException) { return null; }
    }
}
#else
/// <summary>
/// The load context of one core version: its private dependencies (Roslyn, System.Text.Json, the Sketch ProMax loader
/// it references) come from its folder; the Revit API and the runtime's own assemblies from where they are loaded already.
/// </summary>
internal sealed class CoreContext : System.Runtime.Loader.AssemblyLoadContext
{
    private static readonly HashSet<string> Platform = PlatformAssemblies();
    private readonly string _folder;

    public CoreContext(string folder) : base("TritoRevit " + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(folder)), isCollectible: false)
        => _folder = folder;

    protected override Assembly? Load(AssemblyName name)
    {
        var simple = name.Name ?? "";
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
