using System.IO;
using Autodesk.Revit.UI;
using SketchProMax.Commands;
using SketchProMax.Tools;

namespace SketchProMax.UI;

/// <summary>
/// The favourites on the My tools panel while Revit runs (the list itself and the slot layout: <see cref="Favourites"/>).
/// The 0.30 loader reads the manifest only when a core starts, so a pin or unpin reaches the ribbon two ways, both
/// core-side and without a new loader:
///   - each slot button is a live label: every two seconds the loader asks Query("live|favN") and shows the answer
///     (text, tooltip, icon, greyed out when empty), and "fav:N" runs whatever is in slot N when clicked;
///   - the core shows the slots that hold something and hides the rest itself, through the Revit API (RibbonItem.Visible
///     on the loader's buttons, found by name on the My tools panel), right after the change and again after every
///     manifest the loader applies (its "restartnote" query follows each one). Should that ever fail, empty slots stay
///     visible, greyed out and labelled "Empty".
/// Everything here runs on Revit's UI thread.
/// </summary>
internal static class FavouritesRibbon
{
    private const string Tab = "Sketch ProMax";

    private static UIApplication? _app;
    private static List<string>? _pinned;
    private static DateTime _stamp;
    private static int _checked;
    private static List<(string Tool, string Text, string Glyph, string Tip)>? _tools;
    private static Dictionary<string, Favourites.Item> _catalog = new(StringComparer.Ordinal);
    private static List<Favourites.Item> _shown = [];
    // The slot buttons (index 1..Max), found by name on the My tools panel; what the core last set on them.
    private static readonly RibbonItem?[] Slots = new RibbonItem?[Favourites.Max + 1];
    private static bool _found;
    private static int _lastFind = Environment.TickCount - 60000;
    private static string _applied = "";

    /// <summary>From the core's start: the application whose ribbon holds the slot buttons.</summary>
    public static void Attach(UIApplication app) => _app = app;

    /// <summary>True when the slot buttons were found, so pins show on the ribbon without a restart.</summary>
    public static bool IsLive
    {
        get
        {
            if (!_found) Find();
            return _found;
        }
    }

    /// <summary>The pinned keys, in order (missing items included).</summary>
    public static List<string> Pinned
    {
        get
        {
            Refresh();
            return _pinned!.ToList();
        }
    }

    /// <summary>Everything that can be pinned now, by key.</summary>
    public static IReadOnlyDictionary<string, Favourites.Item> Catalog
    {
        get
        {
            Refresh();
            return _catalog;
        }
    }

    /// <summary>What the slots show: the pinned items that exist, in order.</summary>
    public static IReadOnlyList<Favourites.Item> Shown
    {
        get
        {
            Refresh();
            return _shown;
        }
    }

    public static bool IsPinned(string key) => Pinned.Contains(key);

    public static bool CanPin(string key) => Catalog.ContainsKey(key);

    /// <summary>Re-reads the file (when it changed, e.g. in another Revit window) and the recipes, at most once a second.</summary>
    private static void Refresh(bool force = false)
    {
        if (!force && _pinned is not null && unchecked(Environment.TickCount - _checked) < 1000) return;
        _checked = Environment.TickCount;
        var stamp = Stamp();
        if (_pinned is null || stamp != _stamp)
        {
            _pinned = Favourites.Load();
            _stamp = stamp;
        }
        // The ribbon's tools don't change while a core runs; recipes and learned tools come from the My tools list,
        // which rereads its folders only when they changed.
        if (_tools is null)
        {
            var registry = App.Bridge?.Registry ?? ToolRegistry.CreateDefault();
            _tools = CoreRibbonManifest.Tools().Where(t => registry.IsBuiltIn(t.Tool)).ToList();
        }
        string quick;
        try { quick = QuickTools.List(); }
        catch (Exception) { quick = ""; }
        _catalog = Favourites.Catalog(_tools, quick);
        _shown = Favourites.Resolve(_pinned, _catalog);
    }

    private static DateTime Stamp()
    {
        try { return File.Exists(Favourites.DefaultPath) ? File.GetLastWriteTimeUtc(Favourites.DefaultPath) : DateTime.MinValue; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    // ---- Changes ------------------------------------------------------------------------------------------------

    /// <summary>Pins or unpins <paramref name="key"/> and updates the ribbon. Returns what went wrong, or null.</summary>
    public static string? SetPinned(string key, bool pin) => Change(list =>
    {
        if (!pin) return Favourites.Unpin(list, key) ? null : "";
        if (list.Contains(key)) return "";
        return Favourites.Pin(list, key) ? null
            : $"{Favourites.Max} are pinned already, the most My tools holds. Unpin one in Favourites first.";
    });

    /// <summary>Moves a pin up (-1) or down (+1).</summary>
    public static string? Move(string key, int delta) => Change(list => Favourites.Move(list, key, delta) ? null : "");

    /// <summary>Applies <paramref name="edit"/> to the pins (null: changed, "": nothing to do, else a problem) and saves.</summary>
    private static string? Change(Func<List<string>, string?> edit)
    {
        Refresh(force: true);
        var list = _pinned!.ToList();
        var problem = edit(list);
        if (problem is not null) return problem.Length == 0 ? null : problem;
        if (!Favourites.Save(list))
            return "Your favourites could not be saved. Check that the folder %APPDATA%\\SketchProMax can be written to, then try again.";
        _pinned = list;
        _stamp = Stamp();
        _shown = Favourites.Resolve(_pinned, _catalog);
        Apply();
        return null;
    }

    // ---- The ribbon ---------------------------------------------------------------------------------------------

    /// <summary>The loader's Query("live|favN"); also keeps which slots are visible in step.</summary>
    public static string Live(int slot)
    {
        Refresh();
        Apply();
        return Favourites.LiveLine(slot, _shown);
    }

    /// <summary>The loader applied a manifest (it may have made or shown the slot buttons): find them again and fix them.</summary>
    public static void AfterSync()
    {
        _found = false;
        _applied = "";
        Find(force: true);
        Refresh();
        Apply();
    }

    /// <summary>Shows the slots that hold an item, with its text and tooltip, and hides the others. Cheap when nothing changed.</summary>
    private static void Apply()
    {
        var signature = string.Join("\n", _shown.Select(i => i.Key + "\t" + i.Text + "\t" + i.Tip));
        if (!_found) Find();
        if (!_found || signature == _applied) return;
        _applied = signature;
        for (var slot = 1; slot <= Favourites.Max; slot++)
        {
            var item = Slots[slot];
            if (item is null) continue;
            try
            {
                var used = slot <= _shown.Count;
                if (used)
                {
                    // The icon follows with the loader's next live refresh (within two seconds).
                    var (text, tip, _, _) = Favourites.Label(slot, _shown);
                    if (item.ItemText != text) item.ItemText = text;
                    if (item.ToolTip != tip) item.ToolTip = tip;
                    if (!item.Enabled) item.Enabled = true;
                }
                if (item.Visible != used) item.Visible = used;
            }
            catch (Exception) { _found = false; _applied = ""; /* found and applied again next time */ }
        }
    }

    /// <summary>Finds the slot buttons on the My tools panel (at most every ten seconds unless forced). Never throws.</summary>
    private static void Find(bool force = false)
    {
        if (_app is null || (!force && unchecked(Environment.TickCount - _lastFind) < 10000)) return;
        _lastFind = Environment.TickCount;
        Array.Clear(Slots, 0, Slots.Length);
        var generation = new int[Favourites.Max + 1];
        try
        {
            foreach (var panel in _app.GetRibbonPanels(Tab))
            {
                if (panel is null || panel.Name != Favourites.Panel) continue;
                foreach (var item in panel.GetItems())
                {
                    if (item is null) continue;
                    for (var slot = 1; slot <= Favourites.Max; slot++)
                    {
                        // The loader names a button "SketchProMax.<id>", and "SketchProMax.<id>~2"... when it had to make
                        // it again elsewhere (the old one is hidden): the newest is the live one.
                        var name = "SketchProMax." + Favourites.SlotId(slot);
                        var n = item.Name == name ? 1 : item.Name.StartsWith(name + "~", StringComparison.Ordinal)
                            && int.TryParse(item.Name.Substring(name.Length + 1), out var g) ? g : 0;
                        if (n > generation[slot]) { generation[slot] = n; Slots[slot] = item; }
                    }
                }
            }
        }
        catch (Exception) { Array.Clear(Slots, 0, Slots.Length); }
        _found = Slots.Any(s => s is not null);
    }

    // ---- Commands -----------------------------------------------------------------------------------------------

    /// <summary>A favourite slot button: runs what is pinned there, the way its own button or My tools entry does.</summary>
    public static Result Run(UIApplication app, int slot)
    {
        _app ??= app;
        Refresh(force: true);
        if (slot < 1 || slot > _shown.Count)
        {
            Apply();
            TaskDialog.Show("Sketch ProMax", "Nothing is pinned here any more. Click Favourites on the My tools panel to pin a tool, recipe or learned tool.");
            return Result.Cancelled;
        }
        var item = _shown[slot - 1];
        return item.Kind == "tool" ? RibbonTools.Run(app, item.Name) : QuickTools.Run(app, item.Kind, item.Name);
    }

    /// <summary>The Favourites button: the manager window.</summary>
    public static Result Manage(UIApplication app)
    {
        _app ??= app;
        Refresh(force: true);
        new FavouritesWindow().ShowDialog();
        return Result.Succeeded;
    }
}
