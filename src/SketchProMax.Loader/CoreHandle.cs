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

    // Optional (cores from 0.29 on): content for a tab of the dockable pane. Older cores don't have it, which is fine:
    // the pane then shows only its own activity view. Not part of Contract, so no core needs a newer loader for it.
    private readonly MethodInfo? _paneContent;

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
        _paneContent = entry.GetMethod("PaneContent", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
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
        Resolver.AddCore(assembly);
#else
        var assembly = new CoreContext(folder).LoadFromAssemblyPath(path);
#endif
        return new CoreHandle(assembly, path);
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

    /// <summary>
    /// A WPF element the core provides for a tab of the pane ("chat"), or null when this core has none (older cores) or
    /// it failed. Call on Revit's UI thread, after Start.
    /// </summary>
    public System.Windows.FrameworkElement? PaneContent(string name)
    {
        if (_paneContent is null) return null;
        try { return Invoke(_paneContent, name) as System.Windows.FrameworkElement; }
        catch (Exception) { return null; }
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
/// Resolves the dependencies of cores loaded with Assembly.LoadFile (System.Text.Json, Roslyn, System.Memory, ...).
///
/// Why it works this way: a LoadFile'd core is in no binding context, so its references never probe its folder; they
/// go to Revit's own folder (the Load context) and then to AssemblyResolve. Roslyn and System.Text.Json, which we hand
/// out with LoadFrom, are in the LoadFrom context, where their references DO probe their folder and reuse whatever
/// LoadFrom-context assembly already has the same identity. For SketchProMax.dll and Roslyn/System.Text.Json to see
/// the same System.Memory and System.Collections.Immutable, the core's own references must land in that same place:
/// so a request from one of our core folders is answered with Assembly.LoadFrom of the file in THAT folder, which
/// returns exactly the instance the LoadFrom context hands Roslyn and System.Text.Json (the one with that identity,
/// or the Load-context one when Revit's folder has it, in which case the core never asks here at all).
/// It is never answered with "some loaded assembly of that name": another add-in may have loaded its own copy with
/// Assembly.Load(bytes) or a different version, and binding the core to it is what broke run_revit_code and run_recipe
/// on Revit 2024 (MissingMethodException on members whose signatures use ImmutableArray or ReadOnlyMemory).
/// The handler is put first in the AppDomain's AssemblyResolve list, because handlers run in order and the first
/// non-null answer wins: another add-in's (or an old core's) resolver would otherwise answer for our assemblies.
/// Requests from anyone else get the old behaviour: the exact loaded version, else our folders.
/// </summary>
internal static class Resolver
{
    private static readonly List<string> Folders = [];
    private static readonly List<Assembly> Loaded = [];
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        AddFirst(AppDomain.CurrentDomain, Resolve);
        // Requests that don't say who asks go last, so other add-ins' resolvers answer theirs first.
        AppDomain.CurrentDomain.AssemblyResolve += ResolveLast;
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

    /// <summary>A core this process loaded (by us with LoadFile, or by Revit from an old manifest).</summary>
    public static void AddCore(Assembly core)
    {
        Register();
        lock (Folders)
            if (!Loaded.Contains(core)) Loaded.Add(core);
    }

    /// <summary>
    /// Adds <paramref name="handler"/> at the front of AssemblyResolve (.NET Framework keeps the handlers in the private
    /// _AssemblyResolve delegate and returns the first non-null answer). Falls back to a plain subscription.
    /// </summary>
    internal static void AddFirst(AppDomain domain, ResolveEventHandler handler)
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
        if (name == "SketchProMax.Loader") return typeof(Resolver).Assembly;
        if (name == "SketchProMax")
            lock (Folders) return Loaded.FirstOrDefault(a => a.FullName == args.Name);

        List<string> folders;
        List<Assembly> cores;
        lock (Folders)
        {
            folders = Folders.ToList();
            cores = Loaded.ToList();
        }

        // Asked by a core or by something we loaded from a core folder (a core Revit loaded itself counts too): that
        // folder's file, or nothing of ours.
        var asking = args.RequestingAssembly is { IsDynamic: false } r ? SafeFolder(r) : null;
        var own = asking is null ? null
            : folders.Concat(cores.Select(SafeFolder)).FirstOrDefault(f => f is not null && string.Equals(f, asking, StringComparison.OrdinalIgnoreCase));
        if (own is not null)
            return LoadFrom(System.IO.Path.Combine(own, name + ".dll")) ?? Exact(args.Name);
        // Anything else waits for ResolveLast: this handler runs before other add-ins' handlers.
        return null;
    }

    /// <summary>
    /// Last in line, for requests that don't say who asks (Type.GetType, XAML, a run_revit_code snippet loaded from
    /// bytes): the newest core folder's file when it is at least the version asked for, else the exact version loaded.
    /// </summary>
    private static Assembly? ResolveLast(object? sender, ResolveEventArgs args)
    {
        AssemblyName wanted;
        try { wanted = new AssemblyName(args.Name); }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException) { return null; }
        var name = wanted.Name ?? "";
        if (name is "SketchProMax" or "SketchProMax.Loader") return null;
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

    /// <summary>An already loaded assembly with exactly this identity, preferring one loaded from a file.</summary>
    private static Assembly? Exact(string fullName)
    {
        Assembly? fromBytes = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic || a.FullName != fullName) continue;
            if (!string.IsNullOrEmpty(SafeLocation(a))) return a;
            fromBytes ??= a;
        }
        return fromBytes;
    }

    private static string? SafeLocation(Assembly a)
    {
        try { return a.Location; }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { return null; }
    }

    private static string? SafeFolder(Assembly a)
    {
        var location = SafeLocation(a);
        if (string.IsNullOrEmpty(location)) return null;
        try { return System.IO.Path.GetDirectoryName(location); }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or PathTooLongException) { return null; }
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
