using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SketchProMax.Loader;

namespace TritoRevit;

/// <summary>
/// What TritoRevit.addin loads. Makes the TritoRevit tab, with a panel per installed shared pack, and keeps it in step
/// with %APPDATA%\TritoRevit\packs while Revit runs: a pack installed, updated or removed changes its buttons within a
/// few seconds, with no restart. Only the first install needs one, for Revit to read TritoRevit.addin.
///
/// The buttons run in a Sketch ProMax core: when Sketch ProMax itself runs in this Revit, through its loader (so one
/// core serves both tabs); otherwise in TritoRevit's own copy (%LOCALAPPDATA%\TritoRevit\versions), started in its
/// shared-tools mode on Revit's first idle moment and switched to a newer one, when one has been downloaded, the next
/// time Revit is idle with nothing running.
/// </summary>
public sealed class TritoApp : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application) => Host.Startup(application);

    public Result OnShutdown(UIControlledApplication application)
    {
        Host.Shutdown();
        return Result.Succeeded;
    }
}

internal static class Host
{
    public const string Tab = "TritoRevit";
    // AppDomain-wide keys: the core in shared mode finds TritoRevit by them, and asks it to switch to a downloaded version.
    public const string ContractKey = "TritoRevit.Contract";
    public const string PendingKey = "TritoRevit.Pending";
    public const string PathKey = "TritoRevit.Path";
    public const string DispatchKey = "TritoRevit.Dispatch";
    // Set by the Sketch ProMax loader: when it is there, its core runs the shared tools.
    private const string ProMaxDispatchKey = "SketchProMax.Loader.Dispatch";

    private static UIControlledApplication? _application;
    private static CoreHost? _core;
    private static string _year = "";
    private static string? _bootError;
    private static bool _decided, _booting;
    private static Func<string, UIApplication, int>? _proMax;
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
    private static System.Windows.Threading.DispatcherTimer? _timer;
    private static int _lastScan = Environment.TickCount - 120000;
    private static string _signature = "";
    private static bool _syncDue = true;
    private static IntPtr _mainWindow;

    public static string Year => _year;

    public static Result Startup(UIControlledApplication application)
    {
        if (_application is not null) return Result.Succeeded;
        _application = application;
        _year = application.ControlledApplication.VersionNumber;
        try { _mainWindow = application.MainWindowHandle; } catch (Exception) { _mainWindow = IntPtr.Zero; }
        var domain = AppDomain.CurrentDomain;
        domain.SetData(ContractKey, 1);
        domain.SetData(PathKey, typeof(Host).Assembly.Location);
        domain.SetData(DispatchKey, (Func<string, UIApplication, int>)Dispatch);
        try
        {
            Buttons.Build(application);
            _signature = PackButtons.Signature();
            Buttons.Sync(PackButtons.ReadAll(), application.GetRibbonPanels, application.CreateRibbonPanel);
            _syncDue = false;
        }
        catch (Exception ex) { _bootError = "The TritoRevit tab could not be built: " + ex.Message; }
        application.Idling += OnIdling;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        return Result.Succeeded;
    }

    public static void Shutdown()
    {
        _timer?.Stop();
        try { _core?.Shutdown(); } catch (Exception) { /* Revit is closing */ }
    }

    private static void Tick()
    {
        // A pack installed, updated or removed: its buttons change at the next idle moment.
        var signature = PackButtons.Signature();
        if (signature != _signature)
        {
            _signature = signature;
            _syncDue = true;
        }
        if (_syncDue || !_decided || (_core is not null && Pending() is not null)) Nudge();
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (sender is not UIApplication app) return;
        if (!_decided) Decide(app);
        if (_syncDue)
        {
            _syncDue = false;
            try { Buttons.Sync(PackButtons.ReadAll(), app.GetRibbonPanels, app.CreateRibbonPanel); }
            catch (Exception) { _syncDue = true; }
        }
        if (_core is not null && Pending() is { } next && _core.Query("busy") != "1") Swap(app, next);
    }

    /// <summary>
    /// At the first idle moment, when every add-in has started: Sketch ProMax's core if its loader runs here, else ours.
    /// </summary>
    private static void Decide(UIApplication app)
    {
        _decided = true;
        if (AppDomain.CurrentDomain.GetData(ProMaxDispatchKey) is Func<string, UIApplication, int> proMax)
        {
            _proMax = proMax;
            return;
        }
        Boot(app);
    }

    private static void Boot(UIApplication app)
    {
        if (_booting || _core is not null || _proMax is not null) return;
        _booting = true;
        var errors = new List<string>();
        try
        {
            foreach (var candidate in Candidates())
            {
                try
                {
                    var core = CoreHost.Load(candidate.Path);
                    core.Start(app);
                    _core = core;
                    _bootError = null;
                    break;
                }
                catch (Exception ex)
                {
                    Failed.Add(candidate.Path);
                    errors.Add($"{candidate.Path}: {Inner(ex).Message}");
                }
            }
            if (_core is null)
                _bootError = errors.Count == 0
                    ? "TritoRevit's tools are not installed for Revit " + _year + ". Run \"Install TritoRevit.cmd\" from a shared tools zip again."
                    : "TritoRevit could not start:\n" + string.Join("\n", errors);
        }
        finally { _booting = false; }
    }

    private static void Swap(UIApplication app, Candidate target)
    {
        AppDomain.CurrentDomain.SetData(PendingKey, null);
        var old = _core!;
        CoreHost next;
        try { next = CoreHost.Load(target.Path); }
        catch (Exception) { Failed.Add(target.Path); return; }
        try { old.Stop(app); } catch (Exception) { }
        try { next.Start(app); }
        catch (Exception)
        {
            Failed.Add(target.Path);
            try { next.Stop(app); } catch (Exception) { }
            try { old.Start(app); } catch (Exception) { _core = null; }
            return;
        }
        _core = next;
        _syncDue = true;
    }

    private static Candidate? _pendingFromScan;

    /// <summary>A newer core to switch to: one the running core downloaded, or (every minute) one found on disk.</summary>
    private static Candidate? Pending()
    {
        if (_core is null) return null;
        if (AppDomain.CurrentDomain.GetData(PendingKey) is string path)
        {
            if (File.Exists(path) && !Failed.Contains(path) && CoreHost.PeekVersion(path) is { } v && v > _core.Version)
                return new Candidate(path, v);
            AppDomain.CurrentDomain.SetData(PendingKey, null);
        }
        if (unchecked(Environment.TickCount - _lastScan) < 60000) return _pendingFromScan;
        _lastScan = Environment.TickCount;
        var newest = Candidates().FirstOrDefault();
        _pendingFromScan = newest is not null && newest.Version > _core.Version ? newest : null;
        return _pendingFromScan;
    }

    internal sealed record Candidate(string Path, Version Version);

    /// <summary>Every core for this Revit version that can be started, newest first.</summary>
    private static List<Candidate> Candidates()
    {
        var list = new List<Candidate>();
        var versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TritoRevit", "versions");
        try
        {
            if (Directory.Exists(versions))
                foreach (var folder in Directory.GetDirectories(versions))
                {
                    // Only finished folders: downloads unpack to "<version>.partial" and are renamed when complete.
                    if (!Version.TryParse(Path.GetFileName(folder), out _)) continue;
                    var path = Path.Combine(folder, "Revit" + _year, "SketchProMax.dll");
                    if (File.Exists(path) && !Failed.Contains(path) && CoreHost.PeekVersion(path) is { } v) list.Add(new Candidate(path, v));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.OrderByDescending(c => c.Version).ToList();
    }

    /// <summary>Runs a TritoRevit button: "slot:N" (a shared tool) or "manage". Reached through an AppDomain delegate.</summary>
    public static int Dispatch(string name, UIApplication app)
    {
        string command;
        if (name.StartsWith("slot:", StringComparison.Ordinal))
        {
            var found = int.TryParse(name.Substring(5), out var slot) ? Buttons.Command(slot) : null;
            if (found is null)
            {
                TaskDialog.Show("TritoRevit", "This button belongs to a shared pack that was removed or changed. Restart Revit to tidy up the tab.");
                return 1;
            }
            command = found;
        }
        else if (name == "manage") command = "shared-manage";
        else return -1;

        if (!_decided) Decide(app);
        if (_proMax is { } proMax) return proMax(command, app);
        if (_core is null) Boot(app);
        if (_core is null)
        {
            TaskDialog.Show("TritoRevit", _bootError ?? "TritoRevit is still starting. Try again in a moment.");
            return -1;
        }
        return _core.Command(command, app);
    }

    private static Exception Inner(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private static void Nudge()
    {
        if (_mainWindow == IntPtr.Zero) return;
        try { PostMessage(_mainWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception) { }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// The tab's buttons. Revit adds panels and buttons while it runs but never removes one, and a button's command class
/// is fixed when it is made, so every shared tool gets one of a fixed set of slot commands (<see cref="Slot00"/>...),
/// which runs whatever tool the slot holds now; a tool no longer shared is hidden and its slot never reused this session.
/// Each pack is a panel titled with its name; a removed pack's panel is hidden.
/// </summary>
internal static class Buttons
{
    public const int Slots = 96;

    private sealed class Made
    {
        public PushButton Button = null!;
        public int Slot;
        public string Shown = "";
    }

    private static readonly Dictionary<string, Made> Active = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, RibbonPanel> Panels = new(StringComparer.Ordinal);
    private static readonly string?[] SlotCommands = new string?[Slots];
    private static readonly HashSet<int> Spent = [];
    private static int _made;
    private static RibbonPanel? _empty;

    private static string Assembly => typeof(Buttons).Assembly.Location;

    public static string? Command(int slot) => slot >= 0 && slot < Slots ? SlotCommands[slot] : null;

    /// <summary>The tab and its first panel, with the Shared tools button. At start-up only.</summary>
    public static void Build(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(Host.Tab); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* already there */ }
        var panel = app.CreateRibbonPanel(Host.Tab, "TritoRevit");
        var data = new PushButtonData("TritoRevit.Manage", "Shared\ntools", Assembly, typeof(ManageCommand).FullName)
        {
            Image = Icons.Get("pack", 16),
            LargeImage = Icons.Get("pack", 32),
            ToolTip = "The shared tools installed here: their packs, where they update from, check for updates now, add a pack from a zip, or remove one.",
            LongDescription = "Shared tools update by themselves while Revit is open, from the folder they were shared to, and TritoRevit updates itself from its releases. Only the first install needs a Revit restart.",
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
        };
        panel.AddItem(data);
        _empty = panel;
    }

    /// <summary>Brings the tab in line with the installed packs. Call on Revit's UI thread. Throws only when Revit's ribbon can't be read.</summary>
    public static void Sync(List<PackButtons.Pack> packs, Func<string, List<RibbonPanel>> panels, Func<string, string, RibbonPanel> createPanel)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var shownPanels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            if (!Panels.TryGetValue(pack.Id, out var panel))
            {
                // Created under its id (unique on the tab), titled with its name.
                panel = panels(Host.Tab).FirstOrDefault(p => p.Name == "TritoRevit.Pack." + pack.Id)
                        ?? createPanel(Host.Tab, "TritoRevit.Pack." + pack.Id);
                Panels[pack.Id] = panel;
            }
            try
            {
                if (panel.Title != pack.Name) panel.Title = pack.Name;
                if (!panel.Visible) panel.Visible = true;
            }
            catch (Exception) { }
            shownPanels.Add(pack.Id);
            foreach (var b in pack.Buttons)
            {
                var key = pack.Id + "/" + b.Id;
                wanted.Add(key);
                if (!Active.TryGetValue(key, out var made))
                {
                    var slot = FreeSlot();
                    if (slot < 0) continue; // out of slots this session: shown after a restart
                    PushButton? button;
                    try
                    {
                        var data = new PushButtonData("TritoRevit.B" + (++_made), b.Text, Assembly, SlotTypes[slot].FullName)
                        {
                            Image = Icons.Get(b.Glyph, 16),
                            LargeImage = Icons.Get(b.Glyph, 32),
                            ToolTip = Tip(b),
                            AvailabilityClassName = b.NeedsModel ? typeof(ModelAvailable).FullName : typeof(AlwaysAvailable).FullName,
                        };
                        button = panel.AddItem(data) as PushButton;
                    }
                    catch (Exception) { button = null; }
                    if (button is null) { Spent.Add(slot); continue; }
                    made = new Made { Button = button, Slot = slot };
                    Active[key] = made;
                }
                SlotCommands[made.Slot] = "shared\t" + pack.Id + "\t" + b.Id;
                Apply(made, b);
            }
        }
        foreach (var key in Active.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            var made = Active[key];
            Active.Remove(key);
            SlotCommands[made.Slot] = null;
            Spent.Add(made.Slot);
            try { made.Button.Visible = false; } catch (Exception) { }
        }
        foreach (var pair in Panels)
            if (!shownPanels.Contains(pair.Key))
                try { if (pair.Value.Visible) pair.Value.Visible = false; } catch (Exception) { }
    }

    private static string Tip(PackButtons.Button b) => b.Tip.Length > 0 ? b.Tip : b.Text;

    private static void Apply(Made made, PackButtons.Button b)
    {
        var key = string.Join("\u0001", b.Text, b.Tip, b.Glyph, b.NeedsModel);
        if (made.Shown == key) return;
        made.Shown = key;
        try
        {
            var button = made.Button;
            if (!button.Visible) button.Visible = true;
            if (button.ItemText != b.Text) button.ItemText = b.Text;
            if (button.ToolTip != Tip(b)) button.ToolTip = Tip(b);
            button.Image = Icons.Get(b.Glyph, 16);
            button.LargeImage = Icons.Get(b.Glyph, 32);
            button.AvailabilityClassName = b.NeedsModel ? typeof(ModelAvailable).FullName : typeof(AlwaysAvailable).FullName;
        }
        catch (Exception) { /* cosmetic */ }
    }

    private static int FreeSlot()
    {
        var used = new HashSet<int>(Active.Values.Select(m => m.Slot));
        used.UnionWith(Spent);
        for (var i = 0; i < Slots; i++)
            if (!used.Contains(i)) return i;
        return -1;
    }

    private static readonly Type[] SlotTypes = typeof(Buttons).Assembly.GetTypes()
        .Where(t => t.Name.StartsWith("Slot", StringComparison.Ordinal) && typeof(ForwardCommand).IsAssignableFrom(t))
        .OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
}

/// <summary>Every TritoRevit button: forwards to the host through a delegate on the AppDomain.</summary>
public abstract class ForwardCommand : IExternalCommand
{
    protected abstract string Name { get; }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (AppDomain.CurrentDomain.GetData(Host.DispatchKey) is not Func<string, UIApplication, int> dispatch)
        {
            message = "TritoRevit has not started. Restart Revit.";
            return Result.Failed;
        }
        try
        {
            return dispatch(Name, commandData.Application) switch { 0 => Result.Succeeded, 1 => Result.Cancelled, _ => Result.Failed };
        }
        catch (Exception ex)
        {
            if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
            TaskDialog.Show("TritoRevit", $"That didn't work: {ex.Message}\n\nIf it keeps happening, tell whoever shared these tools: {ex.GetType().Name} in {Name}.");
            return Result.Failed;
        }
    }
}

public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

public sealed class ModelAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
        applicationData.ActiveUIDocument?.Document is { IsFamilyDocument: false };
}

[Transaction(TransactionMode.Manual)] public sealed class ManageCommand : ForwardCommand { protected override string Name => "manage"; }
