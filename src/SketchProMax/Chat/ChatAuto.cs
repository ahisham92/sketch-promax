using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>What Automatic chose for a request or a job: its kind of work, the model and effort, and why, in a few words.</summary>
internal sealed record AutoPick(string Kind, string Model, string Effort, bool ModelAuto, bool EffortAuto, string Why)
{
    /// <summary>"Sonnet, medium effort" as the pane shows it.</summary>
    public string Label => ChatAuto.ModelLabel(Model) + (Effort.Length > 0 ? ", " + ChatAuto.EffortLabel(Effort).ToLowerInvariant() + " effort" : "");
}

/// <summary>
/// Automatic model and effort (the chat's pickers and Settings > Chat): a request or a job's task is sorted into simple,
/// everyday or hard work by its words, cheaply and visibly (no model call), and each kind has a model and an effort,
/// editable in Settings. A model or effort picked by hand in a chat sticks for that chat and its new jobs until it is set
/// back to Automatic. Models are Claude Code's aliases (haiku, sonnet, opus), so they follow the latest of each. Needs
/// no WPF or Revit (the unit tests compile this file).
/// </summary>
internal static class ChatAuto
{
    /// <summary>The stored value of a picker set to Automatic ("" in a chat saved before the pickers counts as it too).</summary>
    public const string Automatic = "auto";

    /// <summary>The kinds of work.</summary>
    public const string Simple = "simple", Everyday = "everyday", Hard = "hard";
    public static readonly string[] Kinds = [Simple, Everyday, Hard];

    /// <summary>
    /// The effort levels Claude Code takes with --effort (its help: low, medium, high, xhigh, max). An older Claude Code
    /// without --effort is started again without it (ClaudeCli.EffortRefused).
    /// </summary>
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>The model each kind of work gets by default (Settings > Chat changes them).</summary>
    public static string DefaultModel(string kind) => kind switch { Simple => "haiku", Hard => "opus", _ => "sonnet" };

    /// <summary>The effort each kind of work gets by default (Settings > Chat changes them).</summary>
    public static string DefaultEffort(string kind) => kind switch { Simple => "low", Hard => "high", _ => "medium" };

    public static bool IsAuto(string? value) => string.IsNullOrWhiteSpace(value) || value!.Trim().Equals(Automatic, StringComparison.OrdinalIgnoreCase);

    /// <summary>A stored model choice: Automatic, or one of ChatModels' aliases (an unknown one is Automatic).</summary>
    public static string ParseModel(string? value) => IsAuto(value) ? Automatic : ChatModels.Known(value) ?? Automatic;

    /// <summary>A stored effort choice: Automatic, or one of <see cref="Efforts"/> (an unknown one is Automatic).</summary>
    public static string ParseEffort(string? value) =>
        IsAuto(value) ? Automatic : Efforts.FirstOrDefault(e => e.Equals(value!.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Automatic;

    public static string KindLabel(string kind) => kind switch { Simple => "Simple work", Hard => "Hard reasoning", _ => "Everyday work" };

    public static string KindHint(string kind) => kind switch
    {
        Simple => "rename, tag, list, count, fill parameters, short summaries",
        Hard => "specs or a BEP against the model, redlines, tricky modelling logic, costing decisions",
        _ => "sheets, views, dimensions, checks, normal modelling",
    };

    public static string ModelLabel(string model) => IsAuto(model) ? "Automatic" : ChatModels.Find(model).Label;

    public static string EffortLabel(string effort) => effort switch
    {
        "" or Automatic => "Automatic",
        "xhigh" => "Extra high",
        "max" => "Max",
        _ => char.ToUpperInvariant(effort[0]) + effort.Substring(1),
    };

    public static string EffortHint(string effort) => effort switch
    {
        "low" => "Quickest and lightest on your plan",
        "medium" => "Balanced",
        "high" => "Thinks harder before acting",
        "xhigh" => "Thinks much harder; slower and heavier on your plan",
        "max" => "The most thinking; slowest and heaviest on your plan",
        _ => "Picks per request: low for simple work, higher for hard reasoning",
    };

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Hard reasoning: comparing documents with the model, redlines, costs and design logic.
    private static readonly Regex HardWords = new(
        @"\b(bep|spec|specs|specification|specifications|redlines?|mark-?ups?|compare|comparison|against the (model|drawings?|spec)|complian\w*|audit|" +
        @"cost|costs|costing|estimate|estimating|budget|boq|bill of quantities|value engineering|why|reason|root cause|design (check|logic|option)|" +
        @"strategy|optimi[sz]e|trade-?offs?|clash(es)?|coordinat\w+ (issue|problem)s?|structural (check|design|logic)|load path|calculat\w*|analy[sz]\w*|tricky|complex)\b", Opt);

    // Simple, mechanical work.
    private static readonly Regex SimpleWords = new(
        @"\b(rename|renumber|tag|tags|untag|list|count|how many|fill|set (the )?parameters?|parameters?|copy (the )?values?|summari[sz]e|summary|" +
        @"what is|what's|which|show me|select|isolate|hide|unhide|colou?r|open (the )?view|go to|zoom|print|export)\b", Opt);

    /// <summary>
    /// The kind of work <paramref name="text"/> asks for, and why (the words that decided it). Hard words win; simple words
    /// count only for a short request with nothing hard in it; anything else is everyday work. A job report the main
    /// chat gets is simple (it only sums up). Attached PDFs or office files (<paramref name="documents"/>) are hard.
    /// </summary>
    public static (string Kind, string Why) Classify(string? text, bool documents = false)
    {
        text ??= "";
        if (text.StartsWith("[Job report", StringComparison.Ordinal)) return (Simple, "a job report to sum up");
        var hard = HardWords.Match(text);
        if (hard.Success) return (Hard, $"\"{hard.Value.ToLowerInvariant()}\"");
        if (documents) return (Hard, "attached documents");
        if (text.Length > 1200) return (Hard, "a long request");
        var simple = SimpleWords.Match(text);
        if (simple.Success && text.Length <= 240) return (Simple, $"\"{simple.Value.ToLowerInvariant()}\"");
        return (Everyday, "everyday model work");
    }

    /// <summary>
    /// The model and effort for a request or a job: the chat's own choice where it has one (picked by hand, it sticks),
    /// otherwise what Settings gives the kind of work (<paramref name="models"/>, <paramref name="efforts"/>: kind → value;
    /// missing ones use the defaults). A model the plan refused this session (<paramref name="refused"/>) is skipped:
    /// the next of sonnet, haiku, opus that isn't refused.
    /// </summary>
    public static AutoPick Pick(string? text, string? chatModel, string? chatEffort, IReadOnlyDictionary<string, string>? models = null,
        IReadOnlyDictionary<string, string>? efforts = null, string? kind = null, bool documents = false, ICollection<string>? refused = null)
    {
        var (k, why) = kind is Simple or Everyday or Hard ? (kind, "picked by the coordinator") : Classify(text, documents);
        var modelAuto = IsAuto(chatModel);
        var effortAuto = IsAuto(chatEffort);
        var model = modelAuto ? ParseModel(models is not null && models.TryGetValue(k, out var m) ? m : null) : ParseModel(chatModel);
        if (IsAuto(model)) model = DefaultModel(k);
        if (modelAuto && refused is { Count: > 0 } && refused.Contains(model))
            model = new[] { "sonnet", "haiku", "opus" }.FirstOrDefault(a => !refused.Contains(a)) ?? model;
        var effort = effortAuto ? ParseEffort(efforts is not null && efforts.TryGetValue(k, out var e) ? e : null) : ParseEffort(chatEffort);
        if (IsAuto(effort)) effort = DefaultEffort(k);
        return new AutoPick(k, model, effort, modelAuto, effortAuto, why);
    }

    private static readonly Regex EffortRefused = new(@"(unknown|unrecognized|invalid) (option|argument)\W{0,4}'?--effort|--effort\b.{0,60}\b(unknown|unrecognized|not supported)", Opt);

    /// <summary>True when Claude Code ended because it doesn't know --effort (an older version): start it again without.</summary>
    public static bool IsEffortRefusal(string? text) => !string.IsNullOrEmpty(text) && EffortRefused.IsMatch(text!);

    /// <summary>The line the chat shows when Automatic picks something new: "Automatic: Haiku, low effort (simple work: "rename")."</summary>
    public static string Said(AutoPick pick) =>
        (pick.ModelAuto && pick.EffortAuto ? "Automatic: " : pick.ModelAuto ? "Automatic model: " : "Automatic effort: ") +
        pick.Label + $" ({KindLabel(pick.Kind).ToLowerInvariant()}: {pick.Why}).";
}
