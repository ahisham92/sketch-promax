namespace SketchProMax.Loader;

/// <summary>
/// What a core (0.30 on) asks the loader to show besides the buttons the loader builds itself: its answer to
/// Query("ribbon"), one item per line, fields separated by tabs:
///   kind \t group \t id \t text \t command \t glyph \t tooltip \t long description \t flags
/// Kinds:
///   button    a push button in panel "group"; runs core command "command"
///   pulldown  a drop-down in panel "group" (no command); its entries are "item" lines
///   split     a split button in panel "group": the face runs the entry used last, the arrow lists them all
///   item      an entry of the pulldown or split button whose id is "group"
///   pane      a button in the pane's Activity view, under the section heading "group"
///   tab       a pane tab: id is the name CoreEntry.PaneContent(name) answers for, text the tab header, tooltip its
///             accessible name
///   hide      hides a loader-built button by id (e.g. one the core retired or replaced)
/// Flags (comma separated):
///   model       needs an open model (greyed out without one)
///   small       a small button: consecutive small buttons and drop-downs in a panel are stacked, up to three high
///   break       (with small) starts a new stack here, so a panel can stack two and two
///   live:key    text, tooltip, icon and state follow Query("live|key") every two seconds, answered as
///               "text \t tooltip \t glyph \t state" (state "disabled" greys it out; empty fields keep what is shown)
/// Glyphs are the loader's icon names (Icons.Get), or "logo" / "logo:green|amber|red|gray" for the logo with a status dot.
/// Text fields can't hold tabs or line breaks; "\n" (backslash n) stands for a line break, "\\" for a backslash.
/// Lines of a kind this loader doesn't know are kept as <see cref="Parsed.Unknown"/>, so it can say a restart is needed.
/// Pure text handling, no Revit types, so the tests compile it.
/// </summary>
internal static class RibbonManifest
{
    public sealed record Item(string Kind, string Group, string Id, string Text, string Command, string Glyph, string Tip, string More, string Flags)
    {
        private IEnumerable<string> FlagList => Flags.Split(',').Select(f => f.Trim());
        public bool NeedsModel => FlagList.Contains("model");
        public bool Small => FlagList.Contains("small");
        public bool Break => FlagList.Contains("break");
        public string? LiveKey => FlagList.FirstOrDefault(f => f.StartsWith("live:", StringComparison.Ordinal))?.Substring(5) is { Length: > 0 } k ? k : null;
        public bool IsDropDown => Kind is "pulldown" or "split";
    }

    /// <param name="Controls">Ribbon buttons, pulldowns and split buttons, in manifest order.</param>
    /// <param name="Entries">The entries of each pulldown or split button, by its id, in order.</param>
    public sealed record Parsed(List<Item> Controls, Dictionary<string, List<Item>> Entries, List<Item> PaneActions, List<Item> Tabs,
        List<string> Hide, List<string> Unknown);

    public static Parsed Parse(string? text)
    {
        var result = new Parsed([], new Dictionary<string, List<Item>>(StringComparer.Ordinal), [], [], [], []);
        if (string.IsNullOrEmpty(text)) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<Item>();
        foreach (var raw in text!.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var f = line.Split('\t');
            string F(int i) => i < f.Length ? Unescape(f[i].Trim()) : "";
            var item = new Item(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8));
            if (item.Id.Length == 0) { result.Unknown.Add(line); continue; }
            // Ribbon ids are one namespace (buttons, drop-downs and their entries); the first line for an id wins.
            var space = item.Kind is "button" or "pulldown" or "split" or "item" ? "ribbon" : item.Kind;
            if (!seen.Add(space + "\u0001" + item.Id)) continue;
            switch (item.Kind)
            {
                case "button" when item.Group.Length > 0 && item.Command.Length > 0 && item.Text.Length > 0:
                case "pulldown" or "split" when item.Group.Length > 0 && item.Text.Length > 0:
                    result.Controls.Add(item);
                    break;
                case "item" when item.Group.Length > 0 && item.Command.Length > 0 && item.Text.Length > 0:
                    entries.Add(item);
                    break;
                case "pane" when item.Command.Length > 0 && item.Text.Length > 0:
                    result.PaneActions.Add(item);
                    break;
                case "tab":
                    result.Tabs.Add(item with { Text = item.Text.Length > 0 ? item.Text : item.Id });
                    break;
                case "hide":
                    result.Hide.Add(item.Id);
                    break;
                default:
                    result.Unknown.Add(item.Text.Length > 0 ? item.Text.Replace('\n', ' ') : item.Id);
                    break;
            }
        }
        // Entries may come before or after their drop-down; one whose drop-down isn't declared is unknown.
        var dropDowns = new HashSet<string>(result.Controls.Where(c => c.IsDropDown).Select(c => c.Id), StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!dropDowns.Contains(e.Group)) { result.Unknown.Add(e.Text.Replace('\n', ' ')); continue; }
            if (!result.Entries.TryGetValue(e.Group, out var list)) result.Entries[e.Group] = list = [];
            list.Add(e);
        }
        return result;
    }

    /// <summary>
    /// How the controls of one panel are added, in order: a large control alone, or a run of consecutive small ones
    /// stacked two or three high (a lone small one is added large: Revit stacks only two or three). A "break" flag starts
    /// a new stack.
    /// </summary>
    public static List<List<Item>> Units(IEnumerable<Item> panelControls)
    {
        var units = new List<List<Item>>();
        List<Item>? stack = null;
        foreach (var c in panelControls)
        {
            if (!c.Small) { stack = null; units.Add([c]); continue; }
            if (stack is null || stack.Count == 3 || c.Break && stack.Count > 0) { stack = []; units.Add(stack); }
            stack.Add(c);
        }
        return units;
    }

    /// <summary>A Query("live|key") answer: text, tooltip, glyph (null when empty) and whether it is enabled.</summary>
    public static (string? Text, string? Tip, string? Glyph, bool Enabled) Live(string? answer)
    {
        var f = (answer ?? "").Split('\t');
        string? F(int i) => i < f.Length && f[i].Length > 0 ? Unescape(f[i]) : null;
        return (F(0), F(1), F(2), F(3) != "disabled");
    }

    /// <summary>The other way round, for a core writing its manifest: escapes one field.</summary>
    public static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ");

    public static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var n = value[++i];
                sb.Append(n == 'n' ? '\n' : n);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The sentence the update window and the pane show when part of a new version can only appear after a restart.
    /// <paramref name="what"/>: the names of the buttons or tabs that are missing (may be empty).
    /// </summary>
    public static string RestartNote(IReadOnlyCollection<string> what)
    {
        var names = what.Where(w => w.Length > 0).Select(w => w.Replace('\n', ' ')).Distinct().Take(6).ToList();
        return names.Count == 0
            ? "Restart Revit to see the new buttons and pane. Everything else in this version already works."
            : $"Restart Revit to see the new buttons and pane ({string.Join(", ", names)}). Everything else in this version already works.";
    }
}
