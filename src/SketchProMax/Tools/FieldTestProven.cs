using System.IO;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// What a real field test already proved, so the next one only runs what is new or changed. Data/field-test-proven.json
/// (written by scripts/field-test-proven.py from the last full reports) lists, per Revit year, the rows that passed there
/// and whose code has not changed since; the field test marks those "proven" instead of running them, unless a step that
/// does run depends on them (<see cref="Bundles"/>). "Code unchanged" is inferred file by file by the script, so a full
/// run stays one click away. It also lists the manual checklist features whose code is unchanged since that version.
/// No Revit here: the unit tests compile this file.
/// </summary>
internal static class FieldTestProven
{
    /// <summary>One Revit year's baseline: the full field test it comes from and the row labels ("tool", "tool #2") it proved.</summary>
    internal sealed record Baseline(string Revit, string Date, string Version, IReadOnlyCollection<string> Labels)
    {
        /// <summary>The row message for a step that is not run again.</summary>
        public string Message => $"proven earlier ({Date}, Revit {Revit}, {Version}); code unchanged";

        /// <summary>For the report header and the summaries.</summary>
        public string Describe => $"the full field test of Sketch ProMax {Version} in Revit {Revit} on {Date} ({Labels.Count} rows proven)";
    }

    internal sealed record Data(string Commit, string Version, IReadOnlyList<Baseline> Baselines, IReadOnlyCollection<string> ManualUnchanged)
    {
        public static readonly Data Empty = new("", "", [], []);

        /// <summary>The baseline for a Revit year ("2024"), or null when that year has none (then everything runs).</summary>
        public Baseline? For(string? revitYear) => Baselines.FirstOrDefault(b => b.Revit == (revitYear ?? "").Trim());
    }

    /// <summary>Reads the JSON the script writes. Throws JsonException (or KeyNotFoundException) when it isn't that.</summary>
    internal static Data Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        static List<string> List(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
        var baselines = root.GetProperty("baselines").EnumerateArray().Select(b => new Baseline(
            Str(b, "revit"), Str(b, "date"), Str(b, "version"), new HashSet<string>(List(b, "proven"), StringComparer.Ordinal))).ToList();
        return new Data(Str(root, "commit"), Str(root, "version"), baselines, new HashSet<string>(List(root, "manual_unchanged"), StringComparer.Ordinal));
    }

    private static Data? _loaded;

    /// <summary>The baseline built into this version (Data/field-test-proven.json); empty when it can't be read, so everything runs.</summary>
    internal static Data Load()
    {
        if (_loaded is not null) return _loaded;
        try
        {
            using var stream = typeof(FieldTestProven).Assembly.GetManifestResourceStream("SketchProMax.field-test-proven.json");
            if (stream is null) return _loaded = Data.Empty;
            using var reader = new StreamReader(stream);
            return _loaded = Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return _loaded = Data.Empty;
        }
    }

    /// <summary>
    /// Steps that only work together: one leaves something on disk or in the project that the next uses (a saved recipe
    /// that run_recipe runs, the learned tool save_as_tool saves and the next three call and delete), or puts back what
    /// an earlier one changed and the rollback can't (relinquish_mine after borrowing). When any of them runs, they all run.
    /// </summary>
    internal static readonly string[][] Bundles =
    [
        ["save_recipe", "run_recipe"],
        ["save_recipe #2", "run_recipe #2"],
        ["save_as_tool", "self_test_count_walls", "run_learned_tool", "delete_learned_tool"],
        ["make_worksets_editable", "borrow_elements", "relinquish_mine"],
    ];

    /// <summary>A field-test step as the field test lists it: its tool and the bug log row its case guards (0: none).</summary>
    internal sealed record Step(string Tool, int Bug = 0);

    /// <summary>The rows' labels, in step order: "tool" for a tool's first case, "tool #2" for its second, and so on.</summary>
    internal static List<string> Labels(IEnumerable<Step> steps)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var labels = new List<string>();
        foreach (var s in steps)
        {
            counts[s.Tool] = counts.TryGetValue(s.Tool, out var n) ? n + 1 : 1;
            labels.Add(counts[s.Tool] == 1 ? s.Tool : $"{s.Tool} #{counts[s.Tool]}");
        }
        return labels;
    }

    /// <param name="Proven">Indexes of the steps not run again.</param>
    /// <param name="Forced">Labels the baseline proved that still run, because a step in their bundle runs.</param>
    internal sealed record Plan(HashSet<int> Proven, List<string> Forced);

    /// <summary>
    /// Which steps can be marked proven: the baseline proved its label, it guards no bug log row (bug checks always run),
    /// and every other step of its bundle in this run is proven too. With no baseline, none.
    /// </summary>
    internal static Plan PlanFor(IReadOnlyList<Step> steps, Baseline? baseline)
    {
        var proven = new HashSet<int>();
        var forced = new List<string>();
        if (baseline is null) return new(proven, forced);
        var labels = Labels(steps);
        bool Candidate(int i) => steps[i].Bug == 0 && baseline.Labels.Contains(labels[i]);
        var runs = new HashSet<string>(Enumerable.Range(0, steps.Count).Where(i => !Candidate(i)).Select(i => labels[i]), StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            if (!Candidate(i)) continue;
            if (Bundles.Any(b => b.Contains(labels[i]) && b.Any(runs.Contains))) forced.Add(labels[i]);
            else proven.Add(i);
        }
        return new(proven, forced);
    }

    /// <summary>
    /// How long a run takes, from the share of steps that run: Revit builds and opens the test project first (a couple of
    /// minutes), then each step takes about 2 s (Revit 2024) to 4 s (Revit 2025 and later) with the pauses in between.
    /// </summary>
    internal static string Estimate(int stepsToRun)
    {
        var low = (int)Math.Round(1.5 + stepsToRun * 1.9 / 60);
        var high = (int)Math.Round(2.5 + stepsToRun * 4.3 / 60);
        return $"about {Math.Max(2, low)} to {Math.Max(3, high)} minutes";
    }

    /// <summary>The test checklist's note on a manual item whose code is unchanged since the baseline's version.</summary>
    internal static string ManualNote(Data data) => $"unchanged since {data.Version}: not asked again";
}
