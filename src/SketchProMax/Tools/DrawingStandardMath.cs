using System.IO;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// The office's drawing standard, learned from drawings already issued, so new views are annotated the same way
/// instead of from scratch each time (2026-11-03: "all tags are so bad as if we did not do this exercise before").
/// For each kind of view (plan, section, elevation, detail…): the scales and view templates used, which tag type each
/// category gets and whether with a leader, how much of each category is tagged, and the dimension, text and spot
/// level types. A view is then checked against it. Kept by name in %APPDATA%\SketchProMax\drawing-standards.json with
/// the models that use it (a working model and its documentation model share one). No Revit here, so the tests run it;
/// DrawingStandardTool reads the views.
/// </summary>
internal static class DrawingStandard
{
    /// <summary>One tag in a view: what it tags, its type, and its box in the view's own coordinates.</summary>
    public sealed class TagFact
    {
        public string Category { get; set; } = "";
        public string Type { get; set; } = "";
        public bool Leader { get; set; }
        public bool Empty { get; set; }
        public bool HasBox { get; set; }
        public double U0 { get; set; }
        public double V0 { get; set; }
        public double U1 { get; set; }
        public double V1 { get; set; }
        public bool Overlaps(TagFact o) => HasBox && o.HasBox && U0 < o.U1 && o.U0 < U1 && V0 < o.V1 && o.V0 < V1;
    }

    /// <summary>What one view holds.</summary>
    public sealed class ViewFacts
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Scale { get; set; }
        public string Template { get; set; } = "";
        public List<TagFact> Tags { get; set; } = [];
        /// <summary>Elements seen in the view and those of them tagged, by category (only categories with tags here or in the standard).</summary>
        public Dictionary<string, int> Visible { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Tagged { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Dimensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Text { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Spots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int Marks => Tags.Count + Dimensions.Values.Sum() + Text.Values.Sum() + Spots.Values.Sum();
    }

    /// <summary>How one category is tagged in one kind of view.</summary>
    public sealed class CategoryTags
    {
        public Dictionary<string, int> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int Total { get; set; }
        public int Leaders { get; set; }
        public int Visible { get; set; }
        public int Tagged { get; set; }
        /// <summary>The share of the category's elements that carry a tag (0 when none were seen).</summary>
        public double Coverage() => Visible > 0 ? Math.Min(1.0, (double)Tagged / Visible) : 0;
        public double LeaderShare() => Total > 0 ? (double)Leaders / Total : 0;
    }

    public sealed class Example
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Model { get; set; } = "";
        public int Marks { get; set; }
    }

    /// <summary>The standard for one kind of view.</summary>
    public sealed class KindStandard
    {
        public int Views { get; set; }
        public Dictionary<string, int> Scales { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CategoryTags> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Dimensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Text { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Spots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int WithDimensions { get; set; }
        public int WithText { get; set; }
        public int WithSpots { get; set; }
        /// <summary>The best annotated views it was learned from: what a new view is compared with by eye.</summary>
        public List<Example> Examples { get; set; } = [];
    }

    public sealed class Standard
    {
        public string Name { get; set; } = "";
        public string Learned { get; set; } = "";
        /// <summary>What it was learned from, in words ("sheets S-301 to S-312").</summary>
        public string From { get; set; } = "";
        /// <summary>The models that use it (Checks.ModelKey).</summary>
        public List<string> Models { get; set; } = [];
        public Dictionary<string, KindStandard> Kinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record Issue(string What, string Fix);

    public const int MaxExamples = 3;

    /// <summary>A category is expected to be tagged when the standard tags this share of it or more, in at least this many tags.</summary>
    public const double ExpectedCoverage = 0.6;
    public const int ExpectedTags = 3;

    /// <summary>Most views of the kind have it (dimensions, text, levels) at this share or more.</summary>
    public const double Usual = 0.8;

    private static void Count(Dictionary<string, int> into, string key, int n = 1)
    {
        if (string.IsNullOrWhiteSpace(key) || n <= 0) return;
        into[key] = (into.TryGetValue(key, out var have) ? have : 0) + n;
    }

    /// <summary>The names with the largest counts, largest first.</summary>
    public static List<string> Top(Dictionary<string, int> counts, int max = 3) =>
        counts.Where(c => c.Value > 0).OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Take(max).Select(c => c.Key).ToList();

    /// <summary>Adds views to a standard's kinds (learning in several calls adds up).</summary>
    public static void Learn(Standard standard, IEnumerable<ViewFacts> views, string model)
    {
        foreach (var v in views)
        {
            if (v.Kind.Length == 0 || v.Marks == 0) continue; // a view with nothing on it teaches nothing
            if (!standard.Kinds.TryGetValue(v.Kind, out var kind)) standard.Kinds[v.Kind] = kind = new KindStandard();
            kind.Views++;
            if (v.Scale > 0) Count(kind.Scales, "1:" + v.Scale);
            Count(kind.Templates, v.Template);
            foreach (var tag in v.Tags)
            {
                if (tag.Category.Length == 0) continue;
                if (!kind.Tags.TryGetValue(tag.Category, out var cat)) kind.Tags[tag.Category] = cat = new CategoryTags();
                Count(cat.Types, tag.Type);
                cat.Total++;
                if (tag.Leader) cat.Leaders++;
            }
            foreach (var pair in v.Tagged)
            {
                if (!kind.Tags.TryGetValue(pair.Key, out var cat)) continue;
                cat.Tagged += pair.Value;
                cat.Visible += Math.Max(pair.Value, v.Visible.TryGetValue(pair.Key, out var seen) ? seen : 0);
            }
            foreach (var d in v.Dimensions) Count(kind.Dimensions, d.Key, d.Value);
            foreach (var d in v.Text) Count(kind.Text, d.Key, d.Value);
            foreach (var d in v.Spots) Count(kind.Spots, d.Key, d.Value);
            if (v.Dimensions.Count > 0) kind.WithDimensions++;
            if (v.Text.Count > 0) kind.WithText++;
            if (v.Spots.Count > 0) kind.WithSpots++;
            kind.Examples.RemoveAll(e => e.Id == v.Id && e.Model == model);
            kind.Examples.Add(new Example { Id = v.Id, Name = v.Name, Model = model, Marks = v.Marks });
            kind.Examples = kind.Examples.OrderByDescending(e => e.Marks).Take(MaxExamples).ToList();
        }
    }

    /// <summary>The tag type the standard uses for a category in a kind of view, and whether with a leader; null when it has none.</summary>
    public static (string Type, bool Leader)? Preferred(Standard? standard, string kind, string category)
    {
        if (standard is null || !standard.Kinds.TryGetValue(kind, out var k) || !k.Tags.TryGetValue(category, out var cat) || cat.Types.Count == 0) return null;
        return (Top(cat.Types, 1)[0], cat.LeaderShare() >= 0.5);
    }

    /// <summary>What in a view differs from the standard for its kind, each with what to do. Empty when it matches.</summary>
    public static List<Issue> Check(ViewFacts view, Standard standard)
    {
        var issues = new List<Issue>();
        if (!standard.Kinds.TryGetValue(view.Kind, out var kind))
        {
            issues.Add(new Issue($"The standard '{standard.Name}' has no {view.Kind} views to compare with.", $"Learn it from an issued {view.Kind} (drawing_standard action=learn, add=true)."));
            return issues;
        }
        string Quote(IEnumerable<string> names) => string.Join(", ", names.Select(n => "'" + n + "'"));

        var scale = "1:" + view.Scale;
        if (view.Scale > 0 && kind.Scales.Count > 0 && !kind.Scales.ContainsKey(scale))
            issues.Add(new Issue($"Scale is {scale}; issued {view.Kind} views use {string.Join(", ", Top(kind.Scales))}.", "Set the view's scale to one of those, unless the user chose this one."));
        var template = Top(kind.Templates, 1).FirstOrDefault();
        if (template is not null && kind.Templates[template] >= Usual * kind.Views && !string.Equals(view.Template, template, StringComparison.OrdinalIgnoreCase))
            issues.Add(new Issue(view.Template.Length == 0 ? $"No view template; issued {view.Kind} views use '{template}'." : $"View template is '{view.Template}'; issued {view.Kind} views use '{template}'.",
                $"apply_view_template '{template}'."));

        // Tag types and leaders, category by category.
        foreach (var group in view.Tags.Where(t => t.Category.Length > 0).GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase))
        {
            if (!kind.Tags.TryGetValue(group.Key, out var cat) || cat.Types.Count == 0) continue;
            var want = Top(cat.Types, 1)[0];
            var other = group.Where(t => !cat.Types.ContainsKey(t.Type)).ToList();
            if (other.Count > 0)
                issues.Add(new Issue($"{other.Count} {group.Key} tag(s) are {Quote(other.Select(t => t.Type).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))}; issued drawings use '{want}'.",
                    $"Change those tags to '{want}' (change_type)."));
            var share = cat.LeaderShare();
            var leaders = group.Count(t => t.Leader);
            if (share <= 0.1 && leaders > 0) issues.Add(new Issue($"{leaders} {group.Key} tag(s) have leaders; issued drawings show them without.", "arrange_tags action=leaders_off for that tag category, then fix_overlaps."));
            else if (share >= 0.9 && leaders < group.Count()) issues.Add(new Issue($"{group.Count() - leaders} {group.Key} tag(s) have no leader; issued drawings show them with one.", "arrange_tags action=leaders_on for that tag category."));
        }

        // Categories the issued drawings tag, and this view leaves untagged.
        foreach (var pair in kind.Tags)
        {
            var cat = pair.Value;
            if (cat.Total < ExpectedTags || cat.Coverage() < ExpectedCoverage) continue;
            var seen = view.Visible.TryGetValue(pair.Key, out var n) ? n : 0;
            var tagged = view.Tagged.TryGetValue(pair.Key, out var t) ? t : 0;
            if (seen == 0 || (double)tagged / seen >= cat.Coverage() * 0.5) continue;
            issues.Add(new Issue($"{seen - tagged} of {seen} {pair.Key} have no tag; issued drawings tag {Math.Round(cat.Coverage() * 100)}% of them.",
                $"tag_elements_in_view category '{pair.Key}' (it takes the standard's tag '{Top(cat.Types, 1)[0]}')."));
        }

        var empty = view.Tags.Count(t => t.Empty);
        if (empty > 0) issues.Add(new Issue($"{empty} tag(s) show no text or '?'.", "Fill the parameter the tag reads on those elements, or remove the tag."));

        var clashing = 0;
        for (var i = 0; i < view.Tags.Count; i++)
            for (var j = i + 1; j < view.Tags.Count; j++)
                if (view.Tags[i].Overlaps(view.Tags[j])) { clashing++; break; }
        if (clashing > 0) issues.Add(new Issue($"{clashing} tag(s) sit on top of another tag.", "arrange_tags action=fix_overlaps, then look at the view."));

        void Types(string what, Dictionary<string, int> have, Dictionary<string, int> standardTypes, int withIt)
        {
            if (standardTypes.Count == 0) return;
            if (have.Count == 0)
            {
                if (withIt >= Usual * kind.Views && kind.Views >= 2)
                    issues.Add(new Issue($"No {what}; issued {view.Kind} views have them ({Quote(Top(standardTypes, 2))}).", $"Add the {what} the same way as in the example view."));
                return;
            }
            var odd = have.Where(h => !standardTypes.ContainsKey(h.Key)).ToList();
            if (odd.Count > 0)
                issues.Add(new Issue($"{odd.Sum(o => o.Value)} {what} use {Quote(odd.Select(o => o.Key).Take(3))}; issued drawings use {Quote(Top(standardTypes))}.",
                    $"Change them to '{Top(standardTypes, 1)[0]}' (change_type)."));
        }
        Types("dimensions", view.Dimensions, kind.Dimensions, kind.WithDimensions);
        Types("text notes", view.Text, kind.Text, kind.WithText);
        Types("spot levels", view.Spots, kind.Spots, kind.WithSpots);
        return issues;
    }

    // ---- The file --------------------------------------------------------------------------------------------------------

    public static string File => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "drawing-standards.json");

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>All standards from the file's text, by name; empty on anything unreadable.</summary>
    public static Dictionary<string, Standard> Parse(string? json)
    {
        var all = new Dictionary<string, Standard>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return all;
        try
        {
            foreach (var s in JsonSerializer.Deserialize<List<Standard>>(json!) ?? [])
                if (s is { Name.Length: > 0 }) all[s.Name] = Tidy(s);
        }
        catch (JsonException) { }
        return all;
    }

    /// <summary>A standard read from a file gets case-blind dictionaries again, and no nulls.</summary>
    private static Standard Tidy(Standard s)
    {
        static Dictionary<string, int> Blind(Dictionary<string, int>? d) => new(d ?? [], StringComparer.OrdinalIgnoreCase);
        s.Models ??= [];
        var kinds = new Dictionary<string, KindStandard>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in s.Kinds ?? [])
        {
            var k = pair.Value ?? new KindStandard();
            k.Scales = Blind(k.Scales);
            k.Templates = Blind(k.Templates);
            k.Dimensions = Blind(k.Dimensions);
            k.Text = Blind(k.Text);
            k.Spots = Blind(k.Spots);
            k.Examples ??= [];
            var tags = new Dictionary<string, CategoryTags>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in k.Tags ?? [])
            {
                var cat = t.Value ?? new CategoryTags();
                cat.Types = Blind(cat.Types);
                tags[t.Key] = cat;
            }
            k.Tags = tags;
            kinds[pair.Key] = k;
        }
        s.Kinds = kinds;
        return s;
    }

    public static string Write(Dictionary<string, Standard> all) =>
        JsonSerializer.Serialize(all.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), Json);

    /// <summary>
    /// The standard a model uses: the one named, else the one that lists the model, else the only one in use by a model.
    /// Null when there is none, or several and nothing says which.
    /// </summary>
    public static Standard? For(Dictionary<string, Standard> all, string model, string? name = null)
    {
        if (!string.IsNullOrWhiteSpace(name)) return all.TryGetValue(name!.Trim(), out var named) ? named : null;
        var mine = all.Values.Where(s => s.Models.Contains(model, StringComparer.OrdinalIgnoreCase)).OrderByDescending(s => s.Learned, StringComparer.Ordinal).FirstOrDefault();
        var inUse = all.Values.Where(s => s.Models.Count > 0).ToList();
        return mine ?? (inUse.Count == 1 ? inUse[0] : null);
    }

    public static Dictionary<string, Standard> Load()
    {
        lock (Gate)
        {
            try { return Parse(SafeFile.Read(File)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Parse(null); }
        }
    }

    /// <summary>Loads, changes and saves in one step.</summary>
    public static void Change(Action<Dictionary<string, Standard>> change)
    {
        lock (Gate)
        {
            Dictionary<string, Standard> all;
            try { all = Parse(SafeFile.Read(File)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { all = Parse(null); }
            change(all);
            SafeFile.Write(File, Write(all));
        }
    }

    /// <summary>The standard in a few lines per kind of view, for the chat.</summary>
    public static object Summary(Standard s) => new
    {
        name = s.Name,
        learned = s.Learned,
        from = s.From,
        kinds = s.Kinds.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => new
        {
            kind = k.Key,
            views = k.Value.Views,
            scales = Top(k.Value.Scales),
            view_templates = Top(k.Value.Templates, 2),
            tags = k.Value.Tags.OrderByDescending(t => t.Value.Total).Take(20).Select(t => new
            {
                category = t.Key,
                tag = Top(t.Value.Types, 1).FirstOrDefault() ?? "",
                leader = t.Value.LeaderShare() >= 0.5,
                tagged_percent = Math.Round(t.Value.Coverage() * 100),
            }),
            dimensions = Top(k.Value.Dimensions),
            text = Top(k.Value.Text),
            spot_levels = Top(k.Value.Spots),
            examples = k.Value.Examples.Select(e => e.Name),
        }),
    };
}
