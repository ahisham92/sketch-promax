using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace SketchProMax.Loader;

/// <summary>
/// The ribbon a core declares itself (<see cref="RibbonManifest"/>): panels, large and stacked small buttons, pulldown
/// and split buttons with their entries, and live labels. A new or changed button reaches users with a core update,
/// without a new loader and without restarting Revit. Applied when a core starts: at the first idle moment after Revit
/// starts, and after every in-session switch to a newer core.
///
/// Revit adds panels and buttons to an existing tab at any time but never removes one, and a button's command class
/// and place are fixed when it is made. So:
///   - every declared button and entry gets one of a fixed set of loader slot commands (<see cref="CoreSlot00"/>...),
///     which forwards to whatever core command the running core's manifest names for it;
///   - a button the new core no longer declares is hidden (Visible = false); one that moved to another panel, changed
///     kind or size, or whose drop-down moved, is hidden where it was and made again where it belongs;
///   - new buttons go at the end of their panel, and new panels at the end of the tab (the order may differ from a
///     fresh start until Revit restarts);
///   - only what can't be made at all (out of slots, an item kind this loader doesn't know) asks for a restart.
/// Buttons the loader builds itself (<see cref="Ribbon.Build"/>) are left alone, unless a "hide" line names them; a
/// manifest button with the id of a loader-built one is taken as already there.
/// </summary>
internal static class CoreRibbon
{
    public const int Slots = 128;

    private sealed class Made
    {
        public RibbonItem Item = null!;
        public RibbonManifest.Item Spec = null!;
        public string Name = "";
        public int Slot = -1;            // -1 for drop-downs, which run nothing themselves
        public RibbonItem? Parent;       // entries: the drop-down they are in
        public string Shown = "";        // what was last applied, so unchanged items aren't touched
    }

    private static readonly Dictionary<string, Made> Active = new(StringComparer.Ordinal);
    private static readonly List<Made> Retired = [];
    private static readonly string?[] SlotCommands = new string?[Slots];
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Generation = new(StringComparer.Ordinal);
    private static string _applied = "\u0001";
    private static List<string> _restart = [];

    private static string Assembly => typeof(CoreRibbon).Assembly.Location;

    /// <summary>The core command a slot runs now, or null when its button belongs to no running core.</summary>
    public static string? Command(int slot) => slot >= 0 && slot < Slots ? SlotCommands[slot] : null;

    /// <summary>The running core's manifest (its pane buttons and tabs are read by the pane).</summary>
    public static RibbonManifest.Parsed Current { get; private set; } = RibbonManifest.Parse(null);

    /// <summary>
    /// Brings the ribbon in line with the running core's manifest. Returns what can only appear after a restart (empty
    /// when everything was applied). Call on Revit's UI thread once the core has started. Never throws.
    /// </summary>
    public static List<string> Sync(UIApplication app, CoreHandle? core)
    {
        string text;
        try { text = core?.Query("ribbon") ?? ""; }
        catch (Exception) { text = ""; }
        var manifest = RibbonManifest.Parse(text);
        Current = manifest;
        if (text == _applied) return _restart.ToList();
        _applied = text;
        var restart = _restart = [];
        restart.AddRange(manifest.Unknown);

        Dictionary<string, RibbonPanel> panels;
        HashSet<string> existing;
        try { (panels, existing) = Read(app); }
        catch (Exception) { return restart; }
        var ours = new HashSet<string>(Active.Values.Concat(Retired).Select(m => m.Name), StringComparer.Ordinal);
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in manifest.Controls.GroupBy(c => c.Group))
        {
            foreach (var unit in RibbonManifest.Units(group))
            {
                var create = new List<RibbonManifest.Item>();
                foreach (var c in unit)
                {
                    wanted.Add(c.Id);
                    if (Active.TryGetValue(c.Id, out var made))
                    {
                        if (SamePlace(made.Spec, c)) { Update(made, c); continue; }
                        Retire(made);
                    }
                    else if (existing.Contains(BaseName(c.Id)) && !ours.Contains(BaseName(c.Id))) continue; // built by the loader itself
                    create.Add(c);
                }
                if (create.Count > 0) Create(app, panels, create, restart);
            }
        }

        // Entries of the drop-downs.
        foreach (var pair in manifest.Entries)
        {
            if (!Active.TryGetValue(pair.Key, out var parent) || parent.Item is not PulldownButton dropDown) continue;
            foreach (var e in pair.Value)
            {
                wanted.Add(e.Id);
                if (Active.TryGetValue(e.Id, out var made))
                {
                    if (ReferenceEquals(made.Parent, parent.Item)) { Update(made, e); continue; }
                    Retire(made);
                }
                var slot = FreeSlot();
                if (slot < 0) { restart.Add(e.Text); continue; }
                try
                {
                    var name = NextName(e.Id);
                    var button = dropDown.AddPushButton(PushData(name, e, slot));
                    SlotCommands[slot] = e.Command;
                    var m = new Made { Item = button, Spec = e, Name = name, Slot = slot, Parent = parent.Item };
                    Active[e.Id] = m;
                    Update(m, e);
                }
                catch (Exception) { restart.Add(e.Text); }
            }
        }

        // Anything an earlier core declared and this one doesn't: hidden, and its slot runs nothing.
        foreach (var id in Active.Keys.Where(id => !wanted.Contains(id)).ToList()) Retire(Active[id]);

        // Loader-built buttons a core asks to hide (and shown again once a later core no longer asks).
        var hide = new HashSet<string>(manifest.Hide.Select(BaseName), StringComparer.Ordinal);
        ours = new HashSet<string>(Active.Values.Concat(Retired).Select(m => m.Name), StringComparer.Ordinal);
        try
        {
            foreach (var panel in panels.Values)
                foreach (var item in panel.GetItems())
                {
                    if (item is null || ours.Contains(item.Name)) continue;
                    if (hide.Contains(item.Name)) { if (item.Visible) item.Visible = false; Hidden.Add(item.Name); }
                    else if (Hidden.Remove(item.Name) && !item.Visible) item.Visible = true;
                }
        }
        catch (Exception) { /* cosmetic */ }
        Ribbon.Tidy();
        return restart;
    }

    /// <summary>Updates the live labels (status dot, job progress, update badge) from the running core. Cheap; never throws.</summary>
    public static void RefreshLive(CoreHandle? core)
    {
        if (core is null) return;
        foreach (var made in Active.Values)
        {
            if (made.Spec.LiveKey is not { } key) continue;
            try
            {
                var (text, tip, glyph, enabled) = RibbonManifest.Live(core.Query("live|" + key));
                Apply(made, text ?? made.Spec.Text, tip ?? Tip(made.Spec), glyph ?? made.Spec.Glyph, enabled);
            }
            catch (Exception) { /* cosmetic */ }
        }
    }

    private static bool SamePlace(RibbonManifest.Item was, RibbonManifest.Item now) =>
        was.Group == now.Group && was.Kind == now.Kind && was.Small == now.Small;

    private static void Create(UIApplication app, Dictionary<string, RibbonPanel> panels, List<RibbonManifest.Item> unit, List<string> restart)
    {
        var made = new List<Made>();
        var data = new List<RibbonItemData>();
        foreach (var c in unit)
        {
            var name = NextName(c.Id);
            if (c.IsDropDown)
            {
                var d = c.Kind == "split" ? new SplitButtonData(name, c.Text) : new PulldownButtonData(name, c.Text);
                d.Image = Image(Glyph(c.Glyph), 16);
                d.LargeImage = Image(Glyph(c.Glyph), 32);
                d.ToolTip = Tip(c);
                if (c.More.Length > 0) d.LongDescription = c.More;
                d.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, Ribbon.HelpUrl));
                data.Add(d);
                made.Add(new Made { Spec = c, Name = name });
                continue;
            }
            var slot = FreeSlot(made.Select(m => m.Slot));
            if (slot < 0) { restart.Add(c.Text); continue; }
            data.Add(PushData(name, c, slot));
            made.Add(new Made { Spec = c, Name = name, Slot = slot });
        }
        if (data.Count == 0) return;
        try
        {
            var group = unit[0].Group;
            if (!panels.TryGetValue(group, out var panel))
            {
                panel = app.CreateRibbonPanel(Ribbon.Tab, group);
                panels[group] = panel;
            }
            // Revit stacks two or three; anything else is added one by one (large).
            IList<RibbonItem> items = data.Count switch
            {
                2 => panel.AddStackedItems(data[0], data[1]),
                3 => panel.AddStackedItems(data[0], data[1], data[2]),
                _ => data.Select(d => panel.AddItem(d)).ToList(),
            };
            for (var i = 0; i < made.Count && i < items.Count; i++)
            {
                var m = made[i];
                m.Item = items[i];
                if (m.Slot >= 0) SlotCommands[m.Slot] = m.Spec.Command;
                Active[m.Spec.Id] = m;
                Update(m, m.Spec);
            }
        }
        catch (Exception) { restart.AddRange(made.Select(m => m.Spec.Text)); }
    }

    private static PushButtonData PushData(string name, RibbonManifest.Item c, int slot)
    {
        var data = new PushButtonData(name, c.Text, Assembly, SlotTypes[slot].FullName)
        {
            Image = Image(Glyph(c.Glyph), 16),
            LargeImage = Image(Glyph(c.Glyph), 32),
            ToolTip = Tip(c),
            AvailabilityClassName = c.NeedsModel ? typeof(ModelAvailable).FullName : typeof(AlwaysAvailable).FullName,
        };
        if (c.More.Length > 0) data.LongDescription = c.More;
        data.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, Ribbon.HelpUrl));
        return data;
    }

    private static void Update(Made made, RibbonManifest.Item spec)
    {
        made.Spec = spec;
        if (made.Slot >= 0) SlotCommands[made.Slot] = spec.Command;
        try
        {
            if (!made.Item.Visible) made.Item.Visible = true;
            if (spec.More.Length > 0 && made.Item.LongDescription != spec.More) made.Item.LongDescription = spec.More;
        }
        catch (Exception) { }
        // A live label keeps what the core last said until its next refresh.
        if (spec.LiveKey is null) Apply(made, spec.Text, Tip(spec), spec.Glyph, true);
    }

    private static void Apply(Made made, string text, string tip, string glyph, bool enabled)
    {
        var key = string.Join("\u0001", text, tip, glyph, enabled);
        if (made.Shown == key) return;
        made.Shown = key;
        try
        {
            var item = made.Item;
            if (item.ItemText != text) item.ItemText = text;
            if (item.ToolTip != tip) item.ToolTip = tip;
            if (item.Enabled != enabled) item.Enabled = enabled;
            // A split button shows its current entry's picture.
            if (item is RibbonButton button and not SplitButton)
            {
                button.Image = Image(Glyph(glyph), 16);
                button.LargeImage = Image(Glyph(glyph), 32);
            }
        }
        catch (Exception) { /* cosmetic */ }
    }

    private static void Retire(Made made)
    {
        Active.Remove(made.Spec.Id);
        if (made.Slot >= 0 && SlotCommands[made.Slot] == made.Spec.Command) SlotCommands[made.Slot] = null;
        made.Slot = -1; // its slot may serve another button; this one is hidden and can't be clicked
        Retired.Add(made);
        try { if (made.Item.Visible) made.Item.Visible = false; }
        catch (Exception) { }
        // Entries of a retired drop-down go with it.
        foreach (var entry in Active.Values.Where(m => ReferenceEquals(m.Parent, made.Item)).ToList()) Retire(entry);
    }

    private static int FreeSlot(IEnumerable<int>? taken = null)
    {
        var used = new HashSet<int>(Active.Values.Select(m => m.Slot));
        if (taken is not null) used.UnionWith(taken);
        for (var i = 0; i < Slots; i++)
            if (!used.Contains(i)) return i;
        return -1;
    }

    private static (Dictionary<string, RibbonPanel>, HashSet<string>) Read(UIApplication app)
    {
        var panels = new Dictionary<string, RibbonPanel>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in app.GetRibbonPanels(Ribbon.Tab))
        {
            if (panel is null) continue;
            if (!panels.ContainsKey(panel.Name)) panels[panel.Name] = panel;
            foreach (var item in panel.GetItems())
                if (item is not null) names.Add(item.Name);
        }
        return (panels, names);
    }

    private static string Tip(RibbonManifest.Item item) => item.Tip.Length > 0 ? item.Tip : item.Text.Replace('\n', ' ');

    private static string Glyph(string glyph) => glyph.Length > 0 ? glyph : "cube";

    /// <summary>A glyph, or the logo with a status dot ("logo", "logo:green", "logo:amber", "logo:red", "logo:gray").</summary>
    private static ImageSource Image(string glyph, int size)
    {
        if (glyph == "logo") return Icons.Logo(size);
        if (glyph.StartsWith("logo:", StringComparison.Ordinal))
            return Icons.Logo(size, glyph.Substring(5) switch { "green" => Icons.Green, "amber" => Icons.Amber, "red" => Icons.Red, _ => Icons.Gray });
        return Icons.Get(glyph, size);
    }

    private static string BaseName(string id) => "SketchProMax." + id;

    /// <summary>Revit wants unique names; a button made again elsewhere (it moved) gets a new one.</summary>
    private static string NextName(string id)
    {
        Generation.TryGetValue(id, out var n);
        Generation[id] = n + 1;
        return n == 0 ? BaseName(id) : $"{BaseName(id)}~{n + 1}";
    }

    private static readonly Type[] SlotTypes =
    [
        typeof(CoreSlot00), typeof(CoreSlot01), typeof(CoreSlot02), typeof(CoreSlot03), typeof(CoreSlot04), typeof(CoreSlot05),
        typeof(CoreSlot06), typeof(CoreSlot07), typeof(CoreSlot08), typeof(CoreSlot09), typeof(CoreSlot10), typeof(CoreSlot11),
        typeof(CoreSlot12), typeof(CoreSlot13), typeof(CoreSlot14), typeof(CoreSlot15), typeof(CoreSlot16), typeof(CoreSlot17),
        typeof(CoreSlot18), typeof(CoreSlot19), typeof(CoreSlot20), typeof(CoreSlot21), typeof(CoreSlot22), typeof(CoreSlot23),
        typeof(CoreSlot24), typeof(CoreSlot25), typeof(CoreSlot26), typeof(CoreSlot27), typeof(CoreSlot28), typeof(CoreSlot29),
        typeof(CoreSlot30), typeof(CoreSlot31), typeof(CoreSlot32), typeof(CoreSlot33), typeof(CoreSlot34), typeof(CoreSlot35),
        typeof(CoreSlot36), typeof(CoreSlot37), typeof(CoreSlot38), typeof(CoreSlot39), typeof(CoreSlot40), typeof(CoreSlot41),
        typeof(CoreSlot42), typeof(CoreSlot43), typeof(CoreSlot44), typeof(CoreSlot45), typeof(CoreSlot46), typeof(CoreSlot47),
        typeof(CoreSlot48), typeof(CoreSlot49), typeof(CoreSlot50), typeof(CoreSlot51), typeof(CoreSlot52), typeof(CoreSlot53),
        typeof(CoreSlot54), typeof(CoreSlot55), typeof(CoreSlot56), typeof(CoreSlot57), typeof(CoreSlot58), typeof(CoreSlot59),
        typeof(CoreSlot60), typeof(CoreSlot61), typeof(CoreSlot62), typeof(CoreSlot63), typeof(CoreSlot64), typeof(CoreSlot65),
        typeof(CoreSlot66), typeof(CoreSlot67), typeof(CoreSlot68), typeof(CoreSlot69), typeof(CoreSlot70), typeof(CoreSlot71),
        typeof(CoreSlot72), typeof(CoreSlot73), typeof(CoreSlot74), typeof(CoreSlot75), typeof(CoreSlot76), typeof(CoreSlot77),
        typeof(CoreSlot78), typeof(CoreSlot79), typeof(CoreSlot80), typeof(CoreSlot81), typeof(CoreSlot82), typeof(CoreSlot83),
        typeof(CoreSlot84), typeof(CoreSlot85), typeof(CoreSlot86), typeof(CoreSlot87), typeof(CoreSlot88), typeof(CoreSlot89),
        typeof(CoreSlot90), typeof(CoreSlot91), typeof(CoreSlot92), typeof(CoreSlot93), typeof(CoreSlot94), typeof(CoreSlot95),
        typeof(CoreSlot96), typeof(CoreSlot97), typeof(CoreSlot98), typeof(CoreSlot99), typeof(CoreSlot100), typeof(CoreSlot101),
        typeof(CoreSlot102), typeof(CoreSlot103), typeof(CoreSlot104), typeof(CoreSlot105), typeof(CoreSlot106), typeof(CoreSlot107),
        typeof(CoreSlot108), typeof(CoreSlot109), typeof(CoreSlot110), typeof(CoreSlot111), typeof(CoreSlot112), typeof(CoreSlot113),
        typeof(CoreSlot114), typeof(CoreSlot115), typeof(CoreSlot116), typeof(CoreSlot117), typeof(CoreSlot118), typeof(CoreSlot119),
        typeof(CoreSlot120), typeof(CoreSlot121), typeof(CoreSlot122), typeof(CoreSlot123), typeof(CoreSlot124), typeof(CoreSlot125),
        typeof(CoreSlot126), typeof(CoreSlot127),
    ];
}

// Core-declared buttons and entries: each needs its own command class, so there is a fixed set of slots (see CoreRibbon).
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot00 : ForwardCommand { protected override string Name => "core:0"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot01 : ForwardCommand { protected override string Name => "core:1"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot02 : ForwardCommand { protected override string Name => "core:2"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot03 : ForwardCommand { protected override string Name => "core:3"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot04 : ForwardCommand { protected override string Name => "core:4"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot05 : ForwardCommand { protected override string Name => "core:5"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot06 : ForwardCommand { protected override string Name => "core:6"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot07 : ForwardCommand { protected override string Name => "core:7"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot08 : ForwardCommand { protected override string Name => "core:8"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot09 : ForwardCommand { protected override string Name => "core:9"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot10 : ForwardCommand { protected override string Name => "core:10"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot11 : ForwardCommand { protected override string Name => "core:11"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot12 : ForwardCommand { protected override string Name => "core:12"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot13 : ForwardCommand { protected override string Name => "core:13"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot14 : ForwardCommand { protected override string Name => "core:14"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot15 : ForwardCommand { protected override string Name => "core:15"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot16 : ForwardCommand { protected override string Name => "core:16"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot17 : ForwardCommand { protected override string Name => "core:17"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot18 : ForwardCommand { protected override string Name => "core:18"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot19 : ForwardCommand { protected override string Name => "core:19"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot20 : ForwardCommand { protected override string Name => "core:20"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot21 : ForwardCommand { protected override string Name => "core:21"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot22 : ForwardCommand { protected override string Name => "core:22"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot23 : ForwardCommand { protected override string Name => "core:23"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot24 : ForwardCommand { protected override string Name => "core:24"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot25 : ForwardCommand { protected override string Name => "core:25"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot26 : ForwardCommand { protected override string Name => "core:26"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot27 : ForwardCommand { protected override string Name => "core:27"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot28 : ForwardCommand { protected override string Name => "core:28"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot29 : ForwardCommand { protected override string Name => "core:29"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot30 : ForwardCommand { protected override string Name => "core:30"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot31 : ForwardCommand { protected override string Name => "core:31"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot32 : ForwardCommand { protected override string Name => "core:32"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot33 : ForwardCommand { protected override string Name => "core:33"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot34 : ForwardCommand { protected override string Name => "core:34"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot35 : ForwardCommand { protected override string Name => "core:35"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot36 : ForwardCommand { protected override string Name => "core:36"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot37 : ForwardCommand { protected override string Name => "core:37"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot38 : ForwardCommand { protected override string Name => "core:38"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot39 : ForwardCommand { protected override string Name => "core:39"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot40 : ForwardCommand { protected override string Name => "core:40"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot41 : ForwardCommand { protected override string Name => "core:41"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot42 : ForwardCommand { protected override string Name => "core:42"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot43 : ForwardCommand { protected override string Name => "core:43"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot44 : ForwardCommand { protected override string Name => "core:44"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot45 : ForwardCommand { protected override string Name => "core:45"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot46 : ForwardCommand { protected override string Name => "core:46"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot47 : ForwardCommand { protected override string Name => "core:47"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot48 : ForwardCommand { protected override string Name => "core:48"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot49 : ForwardCommand { protected override string Name => "core:49"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot50 : ForwardCommand { protected override string Name => "core:50"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot51 : ForwardCommand { protected override string Name => "core:51"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot52 : ForwardCommand { protected override string Name => "core:52"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot53 : ForwardCommand { protected override string Name => "core:53"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot54 : ForwardCommand { protected override string Name => "core:54"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot55 : ForwardCommand { protected override string Name => "core:55"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot56 : ForwardCommand { protected override string Name => "core:56"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot57 : ForwardCommand { protected override string Name => "core:57"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot58 : ForwardCommand { protected override string Name => "core:58"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot59 : ForwardCommand { protected override string Name => "core:59"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot60 : ForwardCommand { protected override string Name => "core:60"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot61 : ForwardCommand { protected override string Name => "core:61"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot62 : ForwardCommand { protected override string Name => "core:62"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot63 : ForwardCommand { protected override string Name => "core:63"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot64 : ForwardCommand { protected override string Name => "core:64"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot65 : ForwardCommand { protected override string Name => "core:65"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot66 : ForwardCommand { protected override string Name => "core:66"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot67 : ForwardCommand { protected override string Name => "core:67"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot68 : ForwardCommand { protected override string Name => "core:68"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot69 : ForwardCommand { protected override string Name => "core:69"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot70 : ForwardCommand { protected override string Name => "core:70"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot71 : ForwardCommand { protected override string Name => "core:71"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot72 : ForwardCommand { protected override string Name => "core:72"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot73 : ForwardCommand { protected override string Name => "core:73"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot74 : ForwardCommand { protected override string Name => "core:74"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot75 : ForwardCommand { protected override string Name => "core:75"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot76 : ForwardCommand { protected override string Name => "core:76"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot77 : ForwardCommand { protected override string Name => "core:77"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot78 : ForwardCommand { protected override string Name => "core:78"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot79 : ForwardCommand { protected override string Name => "core:79"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot80 : ForwardCommand { protected override string Name => "core:80"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot81 : ForwardCommand { protected override string Name => "core:81"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot82 : ForwardCommand { protected override string Name => "core:82"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot83 : ForwardCommand { protected override string Name => "core:83"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot84 : ForwardCommand { protected override string Name => "core:84"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot85 : ForwardCommand { protected override string Name => "core:85"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot86 : ForwardCommand { protected override string Name => "core:86"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot87 : ForwardCommand { protected override string Name => "core:87"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot88 : ForwardCommand { protected override string Name => "core:88"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot89 : ForwardCommand { protected override string Name => "core:89"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot90 : ForwardCommand { protected override string Name => "core:90"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot91 : ForwardCommand { protected override string Name => "core:91"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot92 : ForwardCommand { protected override string Name => "core:92"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot93 : ForwardCommand { protected override string Name => "core:93"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot94 : ForwardCommand { protected override string Name => "core:94"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot95 : ForwardCommand { protected override string Name => "core:95"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot96 : ForwardCommand { protected override string Name => "core:96"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot97 : ForwardCommand { protected override string Name => "core:97"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot98 : ForwardCommand { protected override string Name => "core:98"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot99 : ForwardCommand { protected override string Name => "core:99"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot100 : ForwardCommand { protected override string Name => "core:100"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot101 : ForwardCommand { protected override string Name => "core:101"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot102 : ForwardCommand { protected override string Name => "core:102"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot103 : ForwardCommand { protected override string Name => "core:103"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot104 : ForwardCommand { protected override string Name => "core:104"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot105 : ForwardCommand { protected override string Name => "core:105"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot106 : ForwardCommand { protected override string Name => "core:106"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot107 : ForwardCommand { protected override string Name => "core:107"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot108 : ForwardCommand { protected override string Name => "core:108"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot109 : ForwardCommand { protected override string Name => "core:109"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot110 : ForwardCommand { protected override string Name => "core:110"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot111 : ForwardCommand { protected override string Name => "core:111"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot112 : ForwardCommand { protected override string Name => "core:112"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot113 : ForwardCommand { protected override string Name => "core:113"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot114 : ForwardCommand { protected override string Name => "core:114"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot115 : ForwardCommand { protected override string Name => "core:115"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot116 : ForwardCommand { protected override string Name => "core:116"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot117 : ForwardCommand { protected override string Name => "core:117"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot118 : ForwardCommand { protected override string Name => "core:118"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot119 : ForwardCommand { protected override string Name => "core:119"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot120 : ForwardCommand { protected override string Name => "core:120"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot121 : ForwardCommand { protected override string Name => "core:121"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot122 : ForwardCommand { protected override string Name => "core:122"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot123 : ForwardCommand { protected override string Name => "core:123"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot124 : ForwardCommand { protected override string Name => "core:124"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot125 : ForwardCommand { protected override string Name => "core:125"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot126 : ForwardCommand { protected override string Name => "core:126"; }
[Transaction(TransactionMode.Manual)] public sealed class CoreSlot127 : ForwardCommand { protected override string Name => "core:127"; }
