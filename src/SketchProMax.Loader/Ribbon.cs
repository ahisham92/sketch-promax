using System.Windows.Media;
using Autodesk.Revit.UI;

namespace SketchProMax.Loader;

/// <summary>
/// The Sketch ProMax tab. The loader makes the panels, in workflow order, and only the two things that must work
/// without a running core: the start-up status (shown until a core runs, then hidden; it explains why none started)
/// and the My tools pulldown, whose recipes and learned tools change while Revit runs. Everything else on the tab
/// (Claude, Model, Documents, Check, Issue and the Sketch ProMax panel) is declared by the running core
/// (UI/CoreRibbonManifest, applied by <see cref="CoreRibbon"/>), so a core update changes it live.
/// A change here appears only after Revit restarts with the new loader: also raise UI/LoaderCompat.RibbonChangedIn in
/// the core, so a newer core running under an older loader tells the user to restart for its buttons.
/// Panels with nothing visible (no core yet) are hidden. Destructive buttons use the red danger tile.
/// A core that declares no buttons (one older than 0.30, or one whose manifest is empty or could not be applied) would
/// leave the tab almost empty: the status stays and a small fallback set (pane, updates, settings, help) is shown, using
/// commands every core handles, until a manifest applies.
/// </summary>
internal static class Ribbon
{
    public const string Tab = "Sketch ProMax";
    public const string HelpUrl = "https://github.com/ahisham92/sketch-promax#readme";
    public const int Slots = 20;

    /// <summary>The panels, left to right. A core fills them by name; a panel a core names that isn't here goes at the end.</summary>
    public static readonly string[] Panels = ["Claude", "Model", "Documents", "Check", "Issue", "My tools", "Sketch ProMax"];

    private static PushButton? _status;
    private static PulldownButton? _quick;
    private static readonly List<PushButton> Fallback = [];
    private static bool _fallbackShown;
    private static readonly List<RibbonPanel> Made = [];
    private static readonly PushButton?[] SlotButtons = new PushButton?[Slots];
    private static readonly string?[] SlotKeys = new string?[Slots];
    private static readonly Dictionary<RibbonItem, string> Shown = new();
    private static int _ticks;

    private static string Assembly => typeof(Ribbon).Assembly.Location;

    private static PushButtonData Button<T>(string id, string text, string glyph, string tip, string? more = null, bool needsModel = false)
        where T : ForwardCommand => Button<T>(id, text, Icons.Get(glyph, 16), Icons.Get(glyph, 32), tip, more, needsModel);

    private static PushButtonData Button<T>(string id, string text, ImageSource small, ImageSource large, string tip, string? more, bool needsModel)
        where T : ForwardCommand
    {
        var data = new PushButtonData("SketchProMax." + id, text, Assembly, typeof(T).FullName)
        {
            Image = small,
            LargeImage = large,
            ToolTip = tip,
            AvailabilityClassName = needsModel ? typeof(ModelAvailable).FullName : typeof(AlwaysAvailable).FullName,
        };
        if (more is not null) data.LongDescription = more;
        data.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, HelpUrl));
        return data;
    }

    public static void Build(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(Tab); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* already there */ }

        var panels = new Dictionary<string, RibbonPanel>(StringComparer.Ordinal);
        foreach (var name in Panels)
        {
            var panel = app.CreateRibbonPanel(Tab, name);
            panels[name] = panel;
            Made.Add(panel);
        }

        // Shown until a core runs: "starting", or why Sketch ProMax could not start (click for the details).
        _status = (PushButton)panels["Claude"].AddItem(Button<StatusCommand>("Starting", "Claude\nstarting", Icons.Logo(16, Icons.Gray), Icons.Logo(32, Icons.Gray),
            "Sketch ProMax is starting. Its buttons appear here as soon as Revit is ready.",
            "If it stays like this, click for the reason Sketch ProMax could not start and what to do.", false));

        // Fallback for a core that declares no buttons (see the summary): hidden until one is needed. Their names are not
        // "SketchProMax.<id>", so no manifest button is ever taken as one of them.
        var tools = panels["Sketch ProMax"];
        foreach (var data in new[]
                 {
                     Button<PaneCommand>("Fallback.Pane", "Show or\nhide pane", "pane", "Shows or hides the Sketch ProMax pane: connection, jobs and quick tools."),
                     Button<UpdateCheckCommand>("Fallback.Update", "Check for\nupdates", "update", "Looks for a newer Sketch ProMax now. A newer version brings back the full set of buttons."),
                     Button<SettingsCommand>("Fallback.Settings", "Settings", "settings", "Firm standards, whether Revit asks before Claude changes the model, and automatic updates."),
                     Button<HelpCommand>("Fallback.Help", "Help", "help", "Opens the guide: how to use Sketch ProMax, what it can do and what to try first."),
                 })
        {
            var button = (PushButton)tools.AddItem(data);
            button.Visible = false;
            Fallback.Add(button);
        }

        // My tools: saved recipes and learned tools, run straight in Revit without AI or internet.
        var pulldown = new PulldownButtonData("SketchProMax.Quick", "My\ntools")
        {
            Image = Icons.Get("bolt", 16),
            LargeImage = Icons.Get("bolt", 32),
            ToolTip = "Your saved recipes and learned tools. They run right here in Revit: no AI, no internet, no chat needed.",
            LongDescription = "Ask Claude to \"save that as a recipe\" after a workflow you liked, or to \"save it as a tool\" when it " +
                              "wrote code that worked. They appear here. Blanks such as {level} are asked for when you run one.",
        };
        pulldown.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, HelpUrl));
        _quick = (PulldownButton)panels["My tools"].AddItem(pulldown);
        var slotTypes = new[]
        {
            typeof(QuickSlot00), typeof(QuickSlot01), typeof(QuickSlot02), typeof(QuickSlot03), typeof(QuickSlot04),
            typeof(QuickSlot05), typeof(QuickSlot06), typeof(QuickSlot07), typeof(QuickSlot08), typeof(QuickSlot09),
            typeof(QuickSlot10), typeof(QuickSlot11), typeof(QuickSlot12), typeof(QuickSlot13), typeof(QuickSlot14),
            typeof(QuickSlot15), typeof(QuickSlot16), typeof(QuickSlot17), typeof(QuickSlot18), typeof(QuickSlot19),
        };
        for (var i = 0; i < Slots; i++)
        {
            var data = new PushButtonData($"SketchProMax.Quick{i:00}", i == 0 ? "No recipes or learned tools yet" : $"Slot {i + 1}", Assembly, slotTypes[i].FullName)
            {
                Image = Icons.Get("recipe", 16),
                LargeImage = Icons.Get("recipe", 32),
                ToolTip = "Ask Claude to save a workflow as a recipe, or code that worked as a tool, and it appears here.",
                AvailabilityClassName = typeof(ModelAvailable).FullName,
            };
            SlotButtons[i] = _quick.AddPushButton(data);
            if (i > 0) SlotButtons[i]!.Visible = false;
        }
        _quick.AddSeparator();
        _quick.AddPushButton(Button<QuickRefreshCommand>("QuickRefresh", "Refresh list", "refresh",
            "Reads the recipes and learned tools again (the list also refreshes by itself every half minute)."));
        _quick.AddPushButton(Button<QuickFolderCommand>("QuickFolder", "Open recipes folder", "folder",
            "Opens the folder with your recipes and learned tools. Copy files from a colleague into it to share them."));
        Tidy();
    }

    /// <summary>Hides the panels that show nothing (before a core has filled them) and shows them once they do. Never throws.</summary>
    public static void Tidy()
    {
        foreach (var panel in Made)
        {
            try
            {
                var any = panel.GetItems().Any(i => i is not null && i.Visible);
                if (panel.Visible != any) panel.Visible = any;
            }
            catch (Exception) { /* cosmetic */ }
        }
    }

    private static void Set(RibbonItem? item, string text, string? tip = null)
    {
        if (item is null) return;
        var key = text + "\u0001" + tip;
        if (Shown.TryGetValue(item, out var was) && was == key) return;
        Shown[item] = key;
        item.ItemText = text;
        if (tip is not null) item.ToolTip = tip;
    }

    private static readonly Dictionary<RibbonButton, string> Images = new();

    private static void Image(RibbonButton? button, string key, Func<int, ImageSource> make)
    {
        if (button is null) return;
        if (Images.TryGetValue(button, out var was) && was == key) return;
        Images[button] = key;
        button.Image = make(16);
        button.LargeImage = make(32);
    }

    /// <summary>Updates the start-up status (the core's own buttons are refreshed by CoreRibbon). Cheap.</summary>
    public static void Refresh()
    {
        if (++_ticks % 15 == 0) RefreshQuickTools();
        if (_status is null) return;
        if (Host.Core is { } core)
        {
            // Hidden once the core's own buttons are on the ribbon; otherwise the status and the fallback set stay.
            var fallback = !CoreRibbon.Applied;
            if (fallback)
            {
                Set(_status, "Claude\nstatus", $"Sketch ProMax {core.VersionText} is running. Click for the connection status. " +
                    "Its full set of buttons comes with a newer version: use Check for updates, or restart Revit.");
                Image(_status, "logo", s => Icons.Logo(s));
            }
            ShowFallback(fallback);
            return;
        }
        var failed = Host.BootError is not null;
        Set(_status, failed ? "Claude\nnot started" : "Claude\nstarting", Host.BootError ?? "Sketch ProMax starts as soon as Revit is ready.");
        Image(_status, failed ? "red" : "gray", s => Icons.Logo(s, failed ? Icons.Red : Icons.Gray));
        if (!_status.Visible) { _status.Visible = true; Tidy(); }
    }

    /// <summary>Shows or hides the status and the fallback set together (only when that changes). Never throws.</summary>
    private static void ShowFallback(bool show)
    {
        if (_status is null || (_fallbackShown == show && _status.Visible == show)) return;
        _fallbackShown = show;
        try
        {
            if (_status.Visible != show) _status.Visible = show;
            foreach (var button in Fallback)
                if (button.Visible != show) button.Visible = show;
        }
        catch (Exception) { /* cosmetic */ }
        Tidy();
    }

    /// <summary>Fills the quick-tool slots from the running core's list of recipes and learned tools.</summary>
    public static void RefreshQuickTools(bool reload = false)
    {
        if (Host.Core is null || SlotButtons[0] is null) return;
        // One "kind \t name \t summary" line per item, recipes first.
        // An older core may not know "quick-reload"; it then answers empty and the cached list is asked for.
        var text = reload ? Host.Query("quick-reload") : "";
        if (text.Length == 0) text = Host.Query("quick");
        var items = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t')).Where(p => p.Length >= 3).Take(Slots).ToList();
        for (var i = 0; i < Slots; i++)
        {
            var button = SlotButtons[i]!;
            if (i < items.Count)
            {
                var (kind, name, summary) = (items[i][0], items[i][1], items[i][2]);
                SlotKeys[i] = $"quick\t{kind}\t{name}";
                Set(button, Pretty(name), (kind == "recipe" ? "Recipe. " : "Learned tool. ") + summary);
                Image(button, kind, s => Icons.Get(kind == "recipe" ? "recipe" : "learned", s));
                if (!button.Visible) button.Visible = true;
                if (!button.Enabled) button.Enabled = true;
            }
            else
            {
                SlotKeys[i] = null;
                if (i == 0)
                {
                    Set(button, "No recipes or learned tools yet",
                        "Ask Claude to save a workflow as a recipe (\"save that as a recipe\"), or code that worked as a tool, and it appears here.");
                    Image(button, "recipe", s => Icons.Get("recipe", s));
                }
                else if (button.Visible) button.Visible = false;
            }
        }
    }

    /// <summary>The pane's own button is core-declared now ("Show or hide pane"); kept for the pane's callers.</summary>
    public static void SetPaneShown(bool shown) { }

    /// <summary>The core command for a slot, or null when the slot is empty.</summary>
    public static string? QuickKey(int slot) => slot >= 0 && slot < Slots ? SlotKeys[slot] : null;

    private static string Pretty(string name)
    {
        var text = name.Replace('_', ' ').Trim();
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}

/// <summary>
/// Puts the Sketch ProMax tab right after the SketchPro add-in's tab (or after Add-Ins when SketchPro is a panel there).
/// Revit only appends API tabs, and add-ins load in any order, so this is tried when Revit has finished starting, on its
/// first idle moments and whenever the tab list changes, until that window closes. It never throws and leaves the tab
/// where it is when SketchPro isn't installed. Uses AdWindows (the ribbon control Revit is built on), which has no
/// Revit API equivalent.
/// </summary>
internal static class TabOrder
{
    private const int IdleTries = 12;   // Idling events after startup during which the order is still checked
    private const int MaxMoves = 4;     // never fight another add-in that also moves tabs
    private static int _idles, _moves;
    private static bool _initialized, _done, _hooked, _queued, _moving, _broken;
    private static object? _tabs;

    /// <summary>Revit raised ApplicationInitialized: every add-in's OnStartup has run.</summary>
    public static void OnInitialized()
    {
        _initialized = true;
        Try();
    }

    /// <summary>
    /// After switching to a new core (which may have added panels): checks the order again, for a fresh window of idle
    /// moments, with a fresh move budget. Does nothing once AdWindows has failed.
    /// </summary>
    public static void Again()
    {
        if (_broken) return;
        _done = false;
        _idles = 0;
        _moves = 0;
        Try();
    }

    /// <summary>Called on Idling; cheap once the window has closed.</summary>
    public static void OnIdling()
    {
        if (_done || _broken) return;
        _initialized = true; // Revit only idles once it has started
        Try();
        if (_initialized && ++_idles >= IdleTries) Finish();
    }

    private static void Finish()
    {
        _done = true;
        try { Unhook(); } catch (Exception) { }
    }

    private static void Try()
    {
        if (_done || _broken || _moving) return;
        try
        {
            Hook();
            Place();
        }
        catch (Exception) { _broken = true; try { Unhook(); } catch (Exception) { } }
    }

    // AdWindows is touched only inside these NoInlining methods, so a missing or changed AdWindows ends up in the
    // catch above instead of failing the caller's JIT.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Hook()
    {
        if (_hooked) return;
        var tabs = Autodesk.Windows.ComponentManager.Ribbon?.Tabs;
        if (tabs is System.Collections.Specialized.INotifyCollectionChanged changes)
        {
            changes.CollectionChanged += OnTabsChanged;
            _tabs = changes;
            _hooked = true;
        }
    }

    private static void Unhook()
    {
        if (_tabs is System.Collections.Specialized.INotifyCollectionChanged changes) changes.CollectionChanged -= OnTabsChanged;
        _tabs = null;
        _hooked = false;
    }

    private static void OnTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_moving || _queued || _done || _broken) return;
        // The collection can't be changed inside its own CollectionChanged event: look again once it is over.
        _queued = true;
        try
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(() => { _queued = false; Try(); }));
        }
        catch (Exception) { _queued = false; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Place()
    {
        var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
        if (ribbon?.Tabs is not { } tabs) return;
        Autodesk.Windows.RibbonTab? ours = null, sketchPro = null, addIns = null;
        foreach (var tab in tabs)
        {
            if (tab is null) continue;
            if (ours is null && (Is(tab.Id, Ribbon.Tab) || Is(tab.Title, Ribbon.Tab))) ours = tab;
            else if (sketchPro is null && (IsSketchPro(tab.Title) || IsSketchPro(tab.Id))) sketchPro = tab;
            else if (addIns is null && (Is(tab.Id, "Add-Ins") || Is(tab.Title, "Add-Ins"))) addIns = tab;
        }
        if (ours is null) return;
        var anchor = sketchPro;
        if (anchor is null && addIns is not null && HasSketchProPanel(addIns)) anchor = addIns;
        if (anchor is null) return; // SketchPro not (yet) loaded: leave the tab where Revit put it
        if (tabs.IndexOf(ours) == tabs.IndexOf(anchor) + 1) return;
        if (_moves >= MaxMoves) return;
        _moves++;
        _moving = true;
        try
        {
            var wasActive = ribbon.ActiveTab == ours;
            tabs.Remove(ours);
            var at = tabs.IndexOf(anchor);
            tabs.Insert(at < 0 ? tabs.Count : at + 1, ours);
            if (wasActive) ribbon.ActiveTab = ours;
        }
        finally { _moving = false; }
    }

    private static bool HasSketchProPanel(Autodesk.Windows.RibbonTab tab)
    {
        foreach (var panel in tab.Panels)
            if (panel?.Source is { } source && (IsSketchPro(source.Title) || IsSketchPro(source.Name))) return true;
        return false;
    }

    private static bool Is(string? a, string b) => a is not null && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);

    /// <summary>"SketchPro", "Sketch Pro", "Sketch-Pro 2" and the like; not "Sketch ProMax".</summary>
    internal static bool IsSketchPro(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var plain = new string(text!.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (!plain.StartsWith("sketchpro", StringComparison.Ordinal)) return false;
        var rest = plain.Substring("sketchpro".Length);
        return rest.Length == 0 || rest.All(char.IsDigit);
    }
}
