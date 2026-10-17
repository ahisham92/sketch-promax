using System.IO;
using System.Text.Json;

namespace SketchProMax.UI;

/// <summary>
/// Favourites: ribbon tools, recipes and learned tools the user pinned to the My tools panel, in their order
/// (%APPDATA%\SketchProMax\favourites.json). A pin is a key, "tool:&lt;name&gt;", "recipe:&lt;name&gt;" or
/// "learned:&lt;name&gt;"; one whose tool or recipe no longer exists stays in the file (it may come back, e.g. a
/// recipe folder that is offline) but is skipped on the ribbon.
///
/// On the ribbon the core declares a fixed set of <see cref="Max"/> small buttons ("FavouriteSlot01"...), each a live
/// label (live:fav1... → Query("live|fav1")) running "fav:1"...: slot n shows and runs the n-th pinned item that
/// exists. So a pin or unpin changes no manifest line and spends no loader slot; the 0.30 loader picks the new text,
/// tooltip and icon up within two seconds, and the core shows or hides the slots itself (FavouritesRibbon).
/// Pure: no Revit, no WPF, so the tests run it.
/// </summary>
internal static class Favourites
{
    /// <summary>
    /// The most favourites shown, and the number of slot buttons declared. Each takes one of the loader's 128 command
    /// slots for good (the slots never change, so pinning spends none); keep the manifest's total well under 128 so an
    /// update that moves buttons in a session still finds free slots (see the tests).
    /// </summary>
    public const int Max = 12;

    public const string Panel = "My tools";
    public const string ManagerId = "Favourites", ManagerCommand = "favourites";

    /// <summary>Something that can be pinned. Text is one line; Tip the tooltip; Glyph a loader icon name.</summary>
    public sealed record Item(string Key, string Kind, string Name, string Text, string Glyph, string Tip);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "favourites.json");

    public static string Key(string kind, string name) => kind + ":" + name;

    /// <summary>A key's kind ("tool", "recipe", "learned") and name; ("", "") for one that isn't a key.</summary>
    public static (string Kind, string Name) Split(string key)
    {
        var colon = key.IndexOf(':');
        if (colon <= 0 || colon == key.Length - 1) return ("", "");
        var kind = key.Substring(0, colon);
        return kind is "tool" or "recipe" or "learned" ? (kind, key.Substring(colon + 1)) : ("", "");
    }

    // ---- The file --------------------------------------------------------------------------------------------

    /// <summary>The pinned keys in order: {"pinned": [...]}. Unknown, repeated and malformed entries are dropped; a damaged file is no pins.</summary>
    public static List<string> Parse(string? json)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return keys;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("pinned", out var list)
                || list.ValueKind != JsonValueKind.Array) return keys;
            foreach (var e in list.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { } key && Split(key).Kind.Length > 0 && !keys.Contains(key))
                    keys.Add(key);
        }
        catch (JsonException) { }
        return keys;
    }

    public static string Serialize(IEnumerable<string> keys) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["version"] = 1, ["pinned"] = keys.ToList() },
            new JsonSerializerOptions { WriteIndented = true });

    public static List<string> Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return SafeFile.Read(path) is { } json ? Parse(json) : [];
        }
        catch (Exception) { return []; } // a damaged or unreadable file is no pins, never an error
    }

    /// <summary>Writes the pins (through a uniquely named temporary file, so a crash never leaves half a file). False when it couldn't.</summary>
    public static bool Save(IEnumerable<string> keys, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            SafeFile.Write(path, Serialize(keys));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    // ---- Changing the list -----------------------------------------------------------------------------------

    /// <summary>Pins <paramref name="key"/> at the end. False when it is already pinned or <see cref="Max"/> are pinned.</summary>
    public static bool Pin(List<string> pinned, string key)
    {
        if (Split(key).Kind.Length == 0 || pinned.Contains(key) || pinned.Count >= Max) return false;
        pinned.Add(key);
        return true;
    }

    public static bool Unpin(List<string> pinned, string key) => pinned.Remove(key);

    /// <summary>Moves a pin up (delta -1) or down (+1). False at either end or when it isn't pinned.</summary>
    public static bool Move(List<string> pinned, string key, int delta)
    {
        var i = pinned.IndexOf(key);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= pinned.Count) return false;
        pinned.RemoveAt(i);
        pinned.Insert(j, key);
        return true;
    }

    // ---- What can be pinned, and what shows ------------------------------------------------------------------

    /// <summary>
    /// Everything that can be pinned, by key: the ribbon's tools (tool, label, glyph, tooltip; only those this core has)
    /// then the recipes and learned tools as the My tools list gives them ("kind \t name \t summary" lines).
    /// </summary>
    public static Dictionary<string, Item> Catalog(IEnumerable<(string Tool, string Text, string Glyph, string Tip)> tools, string? quickList)
    {
        var catalog = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var (tool, text, glyph, tip) in tools)
        {
            var key = Key("tool", tool);
            if (!catalog.ContainsKey(key)) catalog[key] = new Item(key, "tool", tool, OneLine(text), glyph, tip);
        }
        foreach (var line in (quickList ?? "").Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 3 || f[0] is not ("recipe" or "learned") || f[1].Length == 0) continue;
            var key = Key(f[0], f[1]);
            if (catalog.ContainsKey(key)) continue;
            var recipe = f[0] == "recipe";
            catalog[key] = new Item(key, f[0], f[1], Pretty(f[1]), recipe ? "recipe" : "learned", (recipe ? "Recipe. " : "Learned tool. ") + f[2]);
        }
        return catalog;
    }

    /// <summary>The pinned items that exist, in the user's order, at most <see cref="Max"/>. Missing ones are skipped.</summary>
    public static List<Item> Resolve(IEnumerable<string> pinned, IReadOnlyDictionary<string, Item> catalog) =>
        pinned.Where(catalog.ContainsKey).Select(k => catalog[k]).Take(Max).ToList();

    /// <summary>"Recipe", "Learned tool" or "Ribbon tool", for lists.</summary>
    public static string KindLabel(string kind) => kind switch { "recipe" => "Recipe", "learned" => "Learned tool", _ => "Ribbon tool" };

    // ---- The ribbon ------------------------------------------------------------------------------------------

    public static string SlotId(int slot) => $"FavouriteSlot{slot:00}";
    public static string SlotCommand(int slot) => "fav:" + slot;
    public static string SlotLive(int slot) => "fav" + slot;

    /// <summary>The slot (1-based) a live key ("fav3") or command ("fav:3") names, or 0.</summary>
    public static int Slot(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(text.Substring(prefix.Length), out var n) && n >= 1 && n <= Max ? n : 0;

    /// <summary>
    /// The My tools panel's manifest lines: the Favourites button (opens the manager), then the <see cref="Max"/> slot
    /// buttons, small so they stack three high. Each slot starts with what <paramref name="shown"/> puts there, so the
    /// first look at start-up is already right; after that its live label keeps it current.
    /// </summary>
    public static IEnumerable<string> ManifestLines(IReadOnlyList<Item> shown)
    {
        yield return CoreRibbonManifest.Button(Panel, ManagerId, "Favourites", ManagerCommand, "list",
            "Pin your most used tools, recipes and learned tools here, next to My tools, and put them in your order.",
            $"Opens a short list of everything you can pin. Pinned items show as buttons in this panel, up to {Max}. You can " +
            "also pin a tool from its options window (Pin to My tools).");
        for (var slot = 1; slot <= Max; slot++)
        {
            var (text, tip, glyph, _) = Label(slot, shown);
            yield return CoreRibbonManifest.Button(Panel, SlotId(slot), text, SlotCommand(slot), glyph, tip,
                needsModel: true, small: true, live: SlotLive(slot));
        }
    }

    /// <summary>What slot <paramref name="slot"/> (1-based) shows: its item, or "Empty" (greyed out, and hidden by the core).</summary>
    public static (string Text, string Tip, string Glyph, bool Enabled) Label(int slot, IReadOnlyList<Item> shown)
    {
        if (slot >= 1 && slot <= shown.Count)
        {
            var item = shown[slot - 1];
            return (Short(item.Text), $"{KindLabel(item.Kind)}, pinned. {item.Tip}".Trim(), item.Glyph.Length > 0 ? item.Glyph : "cube", true);
        }
        return ("Empty", "Nothing is pinned here. Click Favourites to pin a tool, recipe or learned tool.", "more", false);
    }

    /// <summary>The live label answer for a slot.</summary>
    public static string LiveLine(int slot, IReadOnlyList<Item> shown)
    {
        var (text, tip, glyph, enabled) = Label(slot, shown);
        return CoreRibbonManifest.LiveLine(text, tip, glyph, enabled);
    }

    /// <summary>One line, short enough for a small ribbon button.</summary>
    public static string Short(string text, int max = 24)
    {
        var one = OneLine(text);
        return one.Length <= max ? one : one.Substring(0, max - 1).TrimEnd() + "…";
    }

    private static string OneLine(string text) => string.Join(" ", (text ?? "").Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));

    private static string Pretty(string name)
    {
        var text = name.Replace('_', ' ').Trim();
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
