using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// The home screen's prompts: "Explore features" (every tool, from its own description, plus hand-written prompts,
/// grouped like the ribbon) and "Tailored prompts" by project type and phase. The hand-written part is the embedded
/// resource chat-prompts.json; tools are added from the registry, so a new tool shows up without editing the file.
/// No WPF and no Revit here, so it can be tested anywhere.
/// </summary>
internal sealed class PromptLibrary
{
    internal sealed record Capability(string Group, string Title, string Summary, string Prompt, string Tool);
    internal sealed record Tailored(string Type, string Phase, string Title, string Prompt);
    internal sealed record Named(string Id, string Name);

    private sealed class Data
    {
        public List<GroupData> Groups { get; set; } = [];
        public Dictionary<string, string> ToolGroups { get; set; } = new();
        public List<CapabilityData> Capabilities { get; set; } = [];
        public List<NamedData> ProjectTypes { get; set; } = [];
        public List<NamedData> Phases { get; set; } = [];
        public List<TailoredData> Tailored { get; set; } = [];
    }

    private sealed class GroupData { public string Name { get; set; } = ""; public string Blurb { get; set; } = ""; }
    private sealed class CapabilityData { public string Group { get; set; } = ""; public string Title { get; set; } = ""; public string Prompt { get; set; } = ""; }
    private sealed class NamedData { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
    private sealed class TailoredData { public string Type { get; set; } = ""; public string Phase { get; set; } = ""; public string Title { get; set; } = ""; public string Prompt { get; set; } = ""; }

    public const string ResourceName = "SketchProMax.chat-prompts.json";

    /// <summary>The groups in ribbon order, with a line on what each holds.</summary>
    public IReadOnlyList<(string Name, string Blurb)> Groups { get; }
    public IReadOnlyList<Capability> Capabilities { get; }
    public IReadOnlyList<Named> ProjectTypes { get; }
    public IReadOnlyList<Named> Phases { get; }
    public IReadOnlyList<Tailored> TailoredPrompts { get; }

    private PromptLibrary(Data data, IEnumerable<(string Name, string Description)> tools)
    {
        Groups = data.Groups.Select(g => (g.Name, g.Blurb)).ToList();
        var known = new HashSet<string>(Groups.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        var caps = data.Capabilities
            .Where(c => c.Title.Length > 0 && c.Prompt.Length > 0)
            .Select(c => new Capability(known.Contains(c.Group) ? c.Group : "Model", c.Title, FirstSentence(c.Prompt), c.Prompt, ""))
            .ToList();
        foreach (var (name, description) in tools.Where(t => t.Name.Length > 0).GroupBy(t => t.Name).Select(g => g.First()).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var group = data.ToolGroups.TryGetValue(name, out var g) && known.Contains(g) ? g : GroupForTool(name);
            if (!known.Contains(group)) group = Groups.Count > 0 ? Groups[0].Name : group;
            caps.Add(ForTool(group, name, description));
        }
        Capabilities = caps;
        ProjectTypes = data.ProjectTypes.Select(t => new Named(t.Id, t.Name)).ToList();
        Phases = data.Phases.Select(p => new Named(p.Id, p.Name)).ToList();
        TailoredPrompts = data.Tailored.Where(t => t.Prompt.Length > 0).Select(t => new Tailored(t.Type, t.Phase, t.Title, t.Prompt)).ToList();
    }

    /// <summary>The library from the JSON text and the registry's tools (name, description).</summary>
    public static PromptLibrary Parse(string json, IEnumerable<(string Name, string Description)> tools)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        Data data;
        try { data = JsonSerializer.Deserialize<Data>(json, options) ?? new Data(); }
        catch (JsonException) { data = new Data(); }
        return new PromptLibrary(data, tools);
    }

    /// <summary>The library from the JSON embedded next to this class (empty when it is missing) and the given tools.</summary>
    public static PromptLibrary Load(IEnumerable<(string Name, string Description)> tools)
    {
        var json = "{}";
        try
        {
            using var stream = typeof(PromptLibrary).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
        }
        catch (IOException) { }
        return Parse(json, tools);
    }

    // Tool name words to ribbon-like groups, first match wins; anything else is Model.
    private static readonly (string Group, Regex Words)[] Rules =
    [
        ("Carbon", new Regex("carbon", RegexOptions.CultureInvariant)),
        ("Rebar", new Regex(@"rebar|reinforce|bar_schedule|mesh|^rft_|tendon|_pt_", RegexOptions.CultureInvariant)),
        ("Steel", new Regex(@"steel|bracing|connection", RegexOptions.CultureInvariant)),
        ("Marine", new Regex(@"marine|vessel|^port_|loading_plan|quay|fender|bollard", RegexOptions.CultureInvariant)),
        ("Issue", new Regex(@"^issue_|revision|export_pdf|drawing_list|transmittal|export_files", RegexOptions.CultureInvariant)),
        ("Check", new Regex(@"check|audit|clash|missing|verify|progress|owners|snapshot|monitor", RegexOptions.CultureInvariant)),
        ("Workflow", new Regex(@"recipe|learned|_code|memory|self_test|field_test|activity|_tool$|workset|relinquish|editable|bug", RegexOptions.CultureInvariant)),
        ("Data", new Regex(@"^query_|^get_|^list_|^read_|^select_|parameter|coordinates|material|^collect_|^export_|change_type|duplicate_type|rename", RegexOptions.CultureInvariant)),
        ("Documents", new Regex(@"sheet|title_block|legend|schedule|tag|dimension|annotation|keynote|text|notes|callout|section|elevation|view|matchline|detail|spot|cloud", RegexOptions.CultureInvariant)),
        ("Structure", new Regex(@"column|beam|slab|floor|raft|pile|foundation|footing|analytical|hollow_core|precast|structure|wall|grid|level|stairs|ramp|roof", RegexOptions.CultureInvariant)),
    ];

    /// <summary>The group a tool falls in by its name (the JSON's toolGroups can say otherwise).</summary>
    public static string GroupForTool(string name)
    {
        foreach (var (group, words) in Rules)
            if (words.IsMatch(name)) return group;
        return "Model";
    }

    /// <summary>"place_columns" → "Place columns".</summary>
    public static string Humanize(string tool)
    {
        var words = tool.Replace('_', ' ').Trim();
        if (words.Length == 0) return words;
        words = Regex.Replace(words, @"\b(3d|2d|pdf|bcf|cad|mep|boq|pt|qa|ifc)\b", m => m.Value.ToUpperInvariant());
        return char.ToUpperInvariant(words[0]) + words.Substring(1);
    }

    /// <summary>The first sentence (at most 200 characters).</summary>
    public static string FirstSentence(string text)
    {
        var t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        var m = Regex.Match(t, @"^.+?[.!?](?=\s|$)");
        var s = m.Success ? m.Value : t;
        return s.Length <= 200 ? s : s.Substring(0, 199).TrimEnd() + "…";
    }

    private static Capability ForTool(string group, string name, string description)
    {
        var title = Humanize(name);
        var summary = FirstSentence(description);
        var prompt = $"{title} ({name}): {summary}\nAsk me for what you need first, then do it in the open model.";
        return new Capability(group, title, summary, prompt, name);
    }

    /// <summary>The capabilities of a group (all groups when null) matching every word of <paramref name="query"/>.</summary>
    public IEnumerable<Capability> Find(string? group, string? query)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Capabilities.Where(c =>
            (group is null || string.Equals(c.Group, group, StringComparison.OrdinalIgnoreCase)) &&
            words.All(w => (c.Title + " " + c.Summary + " " + c.Tool + " " + c.Group).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    /// <summary>How many capabilities each group has, in group order.</summary>
    public IEnumerable<(string Group, int Count)> Counts(string? query = null) =>
        Groups.Select(g => (g.Name, Find(g.Name, query).Count()));

    /// <summary>The tailored prompts for a project type and phase.</summary>
    public IEnumerable<Tailored> For(string type, string phase) =>
        TailoredPrompts.Where(t => string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Phase, phase, StringComparison.OrdinalIgnoreCase));
}
