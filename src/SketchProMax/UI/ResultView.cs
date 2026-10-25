using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.UI;

/// <summary>
/// What a tool's JSON result shows in the result window (0.35): a one-line summary, figures across the top, one bar
/// chart where the result has a breakdown, and lists whose rows open for their details and select their elements in
/// Revit. The raw text stays behind "Full details". Built from any tool's result with no per-tool code; a result that
/// isn't JSON gives null and the window shows the text as it is. No WPF: unit-tested.
/// </summary>
internal sealed record ResultView(string? Summary, IReadOnlyList<ResultView.Figure> Figures, ResultView.Chart? Bars,
    IReadOnlyList<ResultView.Section> Sections, IReadOnlyList<string> Files)
{
    public sealed record Figure(string Label, string Value);
    public sealed record Chart(string Title, IReadOnlyList<(string Label, double Value)> Bars);
    public sealed record Section(string Title, int Total, IReadOnlyList<Row> Rows);
    public sealed record Row(string Text, string? Figure, IReadOnlyList<(string Label, string Value)> Details, IReadOnlyList<long> ElementIds);

    public const int MaxFigures = 6, MaxBars = 12, MaxRows = 200;

    private static readonly string[] SummaryKeys = ["summary", "message", "headline", "result", "note"];
    private static readonly string[] TextKeys = ["name", "title", "label", "check", "warning", "issue", "sheet", "type", "category", "what", "description", "text", "message", "family", "level", "view"];
    private static readonly string[] FigureKeys = ["count", "findings", "total", "score", "value", "quantity", "length", "area", "volume", "weight", "tonnes", "cost"];
    private static readonly string[] IdKeys = ["id", "elementId", "element_id"];
    private static readonly string[] IdListKeys = ["ids", "elementIds", "element_ids", "elements"];

    /// <summary>The view of a result, or null when it isn't a JSON object or array.</summary>
    public static ResultView? From(string? text)
    {
        var trimmed = (text ?? "").TrimStart();
        if (!(trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))) return null;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return Build(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static ResultView Build(JsonElement root)
    {
        string? summary = null;
        var figures = new List<Figure>();
        var sections = new List<Section>();
        var files = new List<string>();
        Chart? chart = null;
        if (root.ValueKind == JsonValueKind.Array)
        {
            chart = AsChart("Results", root);
            if (chart is null) sections.Add(AsSection("Results", root));
            return new ResultView(null, figures, chart, sections, files);
        }
        if (root.ValueKind != JsonValueKind.Object) return new ResultView(Scalar(root), figures, null, sections, files);

        foreach (var p in root.EnumerateObject())
            if (summary is null && SummaryKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                summary = p.Value.GetString();
        foreach (var p in root.EnumerateObject())
        {
            var v = p.Value;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number when figures.Count < MaxFigures && !IsId(p.Name):
                    figures.Add(new Figure(Human(p.Name), Number(v)));
                    break;
                case JsonValueKind.String when LooksLikeFile(v.GetString()):
                    files.Add(v.GetString()!);
                    break;
                case JsonValueKind.Array when v.GetArrayLength() > 0:
                    if (chart is null && AsChart(Human(p.Name), v) is { } c) { chart = c; break; }
                    if (v.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) && v.EnumerateArray().All(e => LooksLikeFile(e.GetString())))
                    {
                        files.AddRange(v.EnumerateArray().Select(e => e.GetString()!));
                        break;
                    }
                    sections.Add(AsSection(Human(p.Name), v));
                    break;
                case JsonValueKind.Object:
                    if (chart is null && AsChart(Human(p.Name), v) is { } oc) { chart = oc; break; }
                    var rows = Details(v).Select(d => new Row(d.Label + ": " + d.Value, null, [], [])).ToList();
                    foreach (var f in v.EnumerateObject().Where(f => f.Value.ValueKind == JsonValueKind.String && LooksLikeFile(f.Value.GetString())))
                        files.Add(f.Value.GetString()!);
                    if (rows.Count > 0) sections.Add(new Section(Human(p.Name), rows.Count, rows));
                    break;
            }
        }
        return new ResultView(summary, figures, chart, sections, files.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// A breakdown worth a bar chart: an object of two or more numbers ({"Walls": 12, "Floors": 4}), or an array of two or
    /// more objects that each have one label and one number ([{"warning": "...", "count": 61}, ...]). Largest first.
    /// </summary>
    internal static Chart? AsChart(string title, JsonElement value)
    {
        var bars = new List<(string, double)>();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var props = value.EnumerateObject().ToList();
            if (props.Count < 2 || !props.All(p => p.Value.ValueKind == JsonValueKind.Number)) return null;
            bars.AddRange(props.Select(p => (p.Name, p.Value.GetDouble())));
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToList();
            if (items.Count < 2) return null;
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                var props = item.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).ToList();
                var labels = props.Where(p => p.Value.ValueKind == JsonValueKind.String).ToList();
                var numbers = props.Where(p => p.Value.ValueKind == JsonValueKind.Number && !IsId(p.Name)).ToList();
                if (props.Count != 2 || labels.Count != 1 || numbers.Count != 1) return null;
                bars.Add((labels[0].Value.GetString() ?? "", numbers[0].Value.GetDouble()));
            }
            var unit = items[0].EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Number).Name;
            if (!string.IsNullOrEmpty(unit) && !FigureKeys.Take(2).Contains(unit, StringComparer.OrdinalIgnoreCase)) title += " (" + Human(unit).ToLowerInvariant() + ")";
        }
        else return null;
        return new Chart(title, bars.OrderByDescending(b => b.Item2).Take(MaxBars).ToList());
    }

    private static Section AsSection(string title, JsonElement array)
    {
        var rows = new List<Row>();
        var total = array.GetArrayLength();
        foreach (var item in array.EnumerateArray().Take(MaxRows))
        {
            if (item.ValueKind != JsonValueKind.Object) { rows.Add(new Row(Scalar(item), null, [], [])); continue; }
            var props = item.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).ToList();
            var text = Pick(props, TextKeys, JsonValueKind.String) ?? First(props, p => p.Value.ValueKind == JsonValueKind.String);
            var number = Pick(props, FigureKeys, JsonValueKind.Number)
                         ?? First(props, p => p.Value.ValueKind == JsonValueKind.Number && !IsId(p.Name));
            var ids = new List<long>();
            foreach (var p in props)
            {
                if (IdKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var id)) ids.Add(id);
                if (IdListKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                    ids.AddRange(p.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _)).Select(e => e.GetInt64()));
            }
            var label = text is { } t ? t.Value.GetString() ?? "" : Human(title);
            var figure = number is { } n
                ? Number(n.Value) + (FigureKeys.Take(2).Contains(n.Name, StringComparer.OrdinalIgnoreCase) ? "" : " " + Human(n.Name).ToLowerInvariant())
                : null;
            var skip = new HashSet<string>(StringComparer.Ordinal);
            if (text is { } st) skip.Add(st.Name);
            var details = Details(item).Where(d => d.Key is null || !skip.Contains(d.Key)).Select(d => (d.Label, d.Value)).ToList();
            rows.Add(new Row(label, figure, details, ids.Distinct().ToList()));
        }
        return new Section(title, total, rows);
    }

    /// <summary>An object's values as "Label: value" lines; a short list of plain values is joined, a longer one counted.</summary>
    private static IEnumerable<(string? Key, string Label, string Value)> Details(JsonElement item)
    {
        foreach (var p in item.EnumerateObject())
        {
            var v = p.Value;
            switch (v.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined: continue;
                case JsonValueKind.Array:
                    var n = v.GetArrayLength();
                    if (n == 0) continue;
                    var plain = v.EnumerateArray().All(e => e.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False);
                    yield return (p.Name, Human(p.Name), plain && n <= 8 ? string.Join(", ", v.EnumerateArray().Select(Scalar)) : $"{n} item(s)");
                    break;
                case JsonValueKind.Object:
                    var parts = v.EnumerateObject().Where(f => f.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)).Take(6)
                        .Select(f => Human(f.Name) + " " + Scalar(f.Value)).ToList();
                    if (parts.Count > 0) yield return (p.Name, Human(p.Name), string.Join(", ", parts));
                    break;
                default:
                    yield return (p.Name, Human(p.Name), Scalar(v));
                    break;
            }
        }
    }

    private static JsonProperty? Pick(List<JsonProperty> props, string[] keys, JsonValueKind kind)
    {
        foreach (var key in keys)
            foreach (var p in props)
                if (p.Value.ValueKind == kind && string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && !(kind == JsonValueKind.Number && IsId(p.Name)))
                    return p;
        return null;
    }

    private static JsonProperty? First(List<JsonProperty> props, Func<JsonProperty, bool> match)
    {
        foreach (var p in props) if (match(p)) return p;
        return null;
    }

    private static bool IsId(string key) =>
        key.Equals("id", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Id", StringComparison.Ordinal) || key.EndsWith("_id", StringComparison.OrdinalIgnoreCase);

    private static string Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => Number(v),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        _ => v.GetRawText(),
    };

    /// <summary>12,345 for whole numbers, 1,234.57 otherwise (two decimals at most).</summary>
    internal static string Number(JsonElement v) =>
        v.TryGetInt64(out var whole) ? whole.ToString("N0", CultureInfo.InvariantCulture) : Number(v.GetDouble());

    internal static string Number(double d) =>
        Math.Abs(d - Math.Round(d)) < 1e-9 ? d.ToString("N0", CultureInfo.InvariantCulture) : d.ToString("#,0.##", CultureInfo.InvariantCulture);

    /// <summary>"overallScore" and "checked_items" → "Overall score", "Checked items"; "BOQ" stays.</summary>
    internal static string Human(string key)
    {
        if (key.Length == 0) return key;
        var spaced = Regex.Replace(key.Replace('_', ' ').Replace('-', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ").Trim();
        var words = spaced.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => w.Length > 1 && w.All(char.IsUpper) ? w : i == 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant() : w.ToLowerInvariant());
        return string.Join(" ", words);
    }

    private static bool LooksLikeFile(string? text) =>
        text is { Length: > 4 and < 400 } && Regex.IsMatch(text, @"^([A-Za-z]:\\|\\\\)[^\r\n<>|*?""]+\.[A-Za-z0-9]{2,6}$");
}
