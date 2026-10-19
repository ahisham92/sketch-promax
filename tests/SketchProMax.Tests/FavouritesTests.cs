using SketchProMax.Loader;
using SketchProMax.UI;

/// <summary>Favourites on the My tools panel: the file, pin/unpin/move, the cap, missing items, and the manifest lines and live labels.</summary>
internal static class FavouritesTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- The file
        var dir = Path.Combine(Path.GetTempPath(), "spm-fav-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "favourites.json");
        try
        {
            check("favourites: no file is no pins", Favourites.Load(path).Count == 0, "");
            var saved = Favourites.Save(["tool:place_columns", "recipe:my level setup", "learned:tidy_tags"], path);
            var back = Favourites.Load(path);
            check("favourites: saved and read back in order", saved && back.SequenceEqual(["tool:place_columns", "recipe:my level setup", "learned:tidy_tags"]),
                string.Join(",", back));
            File.WriteAllText(path, "{ not json");
            check("favourites: a damaged file is no pins, not an error", Favourites.Load(path).Count == 0, "");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        var parsed = Favourites.Parse("""{"pinned": ["tool:a", "tool:a", "bogus", "other:x", 5, "recipe:", "recipe:r"]}""");
        check("favourites: repeats, unknown kinds and malformed entries dropped", parsed.SequenceEqual(["tool:a", "recipe:r"]), string.Join(",", parsed));
        check("favourites: key split", Favourites.Split("learned:x:y") == ("learned", "x:y") && Favourites.Split("x") == ("", ""), "");

        // ---- Pin, unpin, move, cap
        var list = new List<string>();
        check("favourites: pin adds at the end", Favourites.Pin(list, "tool:a") && Favourites.Pin(list, "tool:b") && list.SequenceEqual(["tool:a", "tool:b"]), "");
        check("favourites: pinning twice does nothing", !Favourites.Pin(list, "tool:a") && list.Count == 2, "");
        check("favourites: move down and up", Favourites.Move(list, "tool:a", 1) && list.SequenceEqual(["tool:b", "tool:a"])
            && Favourites.Move(list, "tool:a", -1) && list.SequenceEqual(["tool:a", "tool:b"]), string.Join(",", list));
        check("favourites: no move past either end", !Favourites.Move(list, "tool:a", -1) && !Favourites.Move(list, "tool:b", 1)
            && !Favourites.Move(list, "tool:zz", 1), "");
        check("favourites: unpin", Favourites.Unpin(list, "tool:a") && !Favourites.Unpin(list, "tool:a") && list.SequenceEqual(["tool:b"]), "");
        for (var i = 0; list.Count < Favourites.Max; i++) Favourites.Pin(list, "tool:t" + i);
        check("favourites: capped at Max", list.Count == Favourites.Max && !Favourites.Pin(list, "tool:one-more"), list.Count.ToString());

        // ---- Catalogue and what shows
        var catalog = Favourites.Catalog(
            [("place_columns", "Columns", "column", "Places columns."), ("check_model", "Model check", "check", "Checks the model.")],
            "recipe\tlevel_setup\tSets up levels\nlearned\ttidy_tags\tTidies tags\nnonsense line\nother\tx\ty");
        check("favourites: catalogue has tools, recipes and learned tools", catalog.Count == 4
            && catalog["recipe:level_setup"] is { Kind: "recipe", Text: "Level setup", Glyph: "recipe" }
            && catalog["learned:tidy_tags"].Glyph == "learned" && catalog["tool:check_model"].Text == "Model check", string.Join(",", catalog.Keys));
        var shown = Favourites.Resolve(["learned:tidy_tags", "recipe:gone", "tool:place_columns", "tool:removed_tool"], catalog);
        check("favourites: missing items skipped, order kept", shown.Select(i => i.Key).SequenceEqual(["learned:tidy_tags", "tool:place_columns"]),
            string.Join(",", shown.Select(i => i.Key)));
        var many = Enumerable.Range(0, 20).Select(i => ("t" + i, "T" + i, "cube", "")).ToList();
        check("favourites: never more than Max shown", Favourites.Resolve(many.Select(t => "tool:" + t.Item1), Favourites.Catalog(many, "")).Count == Favourites.Max, "");

        // ---- Manifest lines and live labels
        var lines = Favourites.ManifestLines(shown).ToList();
        var m = RibbonManifest.Parse(string.Join("\n", lines));
        var slots = m.Controls.Where(c => c.Id.StartsWith("FavouriteSlot", StringComparison.Ordinal)).ToList();
        check("favourites: manager button and Max slots on My tools", m.Unknown.Count == 0 && m.Controls.Count == Favourites.Max + 1
            && m.Controls.All(c => c.Group == "My tools") && m.Controls[0] is { Id: "Favourites", Command: "favourites", Small: false }
            && slots.Count == Favourites.Max, string.Join("|", m.Unknown));
        check("favourites: slots are small live buttons running fav:N", slots.Select((c, i) => c.Small && c.NeedsModel && c.LiveKey == "fav" + (i + 1)
            && c.Command == "fav:" + (i + 1) && c.Id == $"FavouriteSlot{i + 1:00}").All(ok => ok), "");
        check("favourites: slots start with what is pinned", slots[0].Text == "Tidy tags" && slots[0].Glyph == "learned" && slots[1].Text == "Columns"
            && slots[2].Text == "Empty", slots[0].Text + "|" + slots[2].Text);
        check("favourites: slots stack three high", RibbonManifest.Units(m.Controls).Select(u => u.Count).SequenceEqual([1, 3, 3, 3, 3]),
            string.Join(",", RibbonManifest.Units(m.Controls).Select(u => u.Count)));
        var live1 = RibbonManifest.Live(Favourites.LiveLine(1, shown));
        var live5 = RibbonManifest.Live(Favourites.LiveLine(5, shown));
        check("favourites: live label shows the item, empty slots greyed out", live1.Text == "Tidy tags" && live1.Enabled && live1.Tip!.StartsWith("Learned tool, pinned.")
            && live5.Text == "Empty" && !live5.Enabled, live1 + " / " + live5);
        check("favourites: slot from live key and command", Favourites.Slot("fav3", "fav") == 3 && Favourites.Slot("fav:12", "fav:") == 12
            && Favourites.Slot("fav13", "fav") == 0 && Favourites.Slot("fav:x", "fav:") == 0 && Favourites.Slot("favourites", "fav") == 0, "");
        check("favourites: long names cut for a small button", Favourites.Short("A very long recipe name that goes on and on").Length == 24
            && Favourites.Short("Model\ncheck") == "Model check", "");

        // ---- The whole manifest fits the loader's 128 command slots with room to spare
        var shipped = RibbonManifest.Parse(CoreRibbonManifest.Text);
        var used = shipped.Controls.Count(c => !c.IsDropDown) + shipped.Entries.Values.Sum(e => e.Count);
        // 23 spare (24 until the pre-submission checklist button in 0.35): an update adding more new buttons than that in one
        // session asks for a restart instead of adding them live. Free slots (merge buttons) rather than lowering this again.
        check("favourites: the manifest leaves at least 23 of 128 loader slots free for in-session updates", used <= 128 - 23, $"{used} used");
        check("favourites: every ribbon tool can be pinned", CoreRibbonManifest.Tools().Count() == shipped.Entries.Values.SelectMany(e => e).Count(e => e.Command.StartsWith("tool:"))
            + shipped.Controls.Count(c => c.Command.StartsWith("tool:")) && CoreRibbonManifest.Tools().All(t => !t.Text.Contains('\n') && t.Glyph.Length > 0), "");
    }
}
