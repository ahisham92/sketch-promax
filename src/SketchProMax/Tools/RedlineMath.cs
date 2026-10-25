using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Redlines in, revised sheets out: the parts that need no Revit, so the tests run them. Reading the markup list Claude
/// gives (JSON, with prose or a code fence around it allowed), mapping a markup's place on the page to sheet coordinates
/// and into a viewport's view, choosing the element a markup points at (or saying why it can't), and the done / not done
/// report for Excel and the chat.
///
/// Coordinates: a markup's box and point are fractions of the drawing sheet's outer border (0..1, origin top left, as
/// on the page or photo); the sheet frame is the title block's extent on the sheet (else the sheet outline), in sheet
/// coordinates (feet, origin bottom left, y up). Paper distances are in mm.
/// </summary>
internal static class Redline
{
    /// <summary>What a markup asks for, once read.</summary>
    public static readonly string[] Kinds = ["edit_text", "add_note", "set_parameter", "set_dimension", "delete", "none"];

    public sealed class Markup
    {
        public string Id { get; set; } = "";
        public int Page { get; set; } = 1;
        public string? Sheet { get; set; }
        /// <summary>text, cloud, arrow, strike, dimension or note: what was drawn.</summary>
        public string Type { get; set; } = "text";
        /// <summary>What is written, as read.</summary>
        public string Text { get; set; } = "";
        /// <summary>[x0, y0, x1, y1] as fractions of the sheet border, top-left origin; null when not given.</summary>
        public double[]? Box { get; set; }
        /// <summary>[x, y] an arrow's tip, as fractions; null when there is none.</summary>
        public double[]? Point { get; set; }
        /// <summary>Free words naming what it is about ("beam B12", "the general note", "slab").</summary>
        public string Target { get; set; } = "";
        /// <summary>One of <see cref="Kinds"/>.</summary>
        public string Kind { get; set; } = "none";
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Parameter { get; set; }
        /// <summary>set_dimension: the new value in mm, when it is a number.</summary>
        public double? ValueMm { get; set; }
        /// <summary>set_dimension: override (replace the dimension's text) or move (move an element so it measures ValueMm). Default override.</summary>
        public string Mode { get; set; } = "override";
        /// <summary>The reader's confidence, 0..1 (1 when not given).</summary>
        public double Confidence { get; set; } = 1;

        /// <summary>The change in a few words, for the list and the report.</summary>
        public string Describe() => Kind switch
        {
            "edit_text" => From is { Length: > 0 } ? $"Text '{Clip(From, 40)}' -> '{Clip(To ?? "", 40)}'" : $"Text -> '{Clip(To ?? "", 60)}'",
            "add_note" => $"Add note '{Clip(To ?? Text, 60)}'",
            "set_parameter" => $"{Parameter} = '{Clip(To ?? "", 40)}'",
            "set_dimension" => Mode == "move" && ValueMm is { } mm ? $"Move to make the dimension {F(mm)} mm" : $"Dimension text -> '{Clip(To ?? (ValueMm is { } v ? F(v) : ""), 30)}'",
            "delete" => "Delete (struck through)",
            _ => "No change read: " + Clip(Text.Length > 0 ? Text : Type, 60),
        };
    }

    /// <summary>The confidence below which a markup is not applied but handed over.</summary>
    public const double MinConfidence = 0.5;

    // ---- Reading the pages ---------------------------------------------------------------------------------------------------

    /// <summary>Pages read from one PDF at most (each is a Claude reading when nothing else gives the markups).</summary>
    public const int MaxPages = 20;

    /// <summary>
    /// The pages to read: those given (from 1, each once), else every page of the file up to <see cref="MaxPages"/>
    /// (<paramref name="pagesInFile"/> is asked only then). Throws when none is left or too many are given.
    /// </summary>
    public static List<int> Pages(IReadOnlyList<int>? given, Func<int> pagesInFile)
    {
        var pages = given is not null ? given.Where(p => p >= 1).Distinct().ToList() : Enumerable.Range(1, Math.Max(1, Math.Min(MaxPages, pagesInFile()))).ToList();
        if (pages.Count == 0) throw new ArgumentException("pages lists no page (pages count from 1).");
        if (pages.Count > MaxPages) throw new ArgumentException($"At most {MaxPages} pages at a time; give pages.");
        return pages;
    }

    /// <summary>One page's answer added to <paramref name="into"/>: its markups marked with the page, its problems named by page.</summary>
    public static void AddPage(List<Markup> into, List<string> problems, string answer, int page)
    {
        var found = Parse(answer, out var pageProblems);
        foreach (var m in found) m.Page = page;
        problems.AddRange(pageProblems.Select(p => $"page {page}: {p}"));
        into.AddRange(found);
    }

    /// <summary>Ids unique across pages: a repeat gets its page in front ("P2-M1").</summary>
    public static void UniqueIds(IEnumerable<Markup> markups)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in markups) if (!seen.Add(m.Id)) { m.Id = $"P{m.Page}-{m.Id}"; seen.Add(m.Id); }
    }

    /// <summary>Pages read ahead off Revit's thread (the ribbon's Redlines to sheets), for the redline_apply call that follows.</summary>
    internal sealed record ReadAhead(string File, IReadOnlyList<int> Pages, List<Markup> Markups, List<string> Notes);

    private static ReadAhead? _readAhead;

    /// <summary>Hands pages read ahead to the next redline_apply on the same file and pages, so it doesn't read them again.</summary>
    public static void HandOver(ReadAhead read) => _readAhead = read;

    /// <summary>Drops pages read ahead that the apply did not take (it failed before reading), so a later call can't pick them up.</summary>
    public static void Discard() => _readAhead = null;

    /// <summary>The pages read ahead for this file and these pages, once; null otherwise (they are then read as usual).</summary>
    public static ReadAhead? TakeRead(string file, IReadOnlyList<int> pages)
    {
        var r = _readAhead;
        _readAhead = null;
        return r is not null && SamePath(r.File, file) && r.Pages.SequenceEqual(pages) ? r : null;
    }

    private static bool SamePath(string a, string b)
    {
        static string Full(string p)
        {
            try { return System.IO.Path.GetFullPath(p.Trim().Trim('"')); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return p; }
        }
        return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Reading the markup list ------------------------------------------------------------------------------------------

    /// <summary>
    /// The markups in Claude's answer or a tool argument: a list of markups, {"sheet_number", "markups": [...]}, or
    /// {"pages": [{"page", "sheet_number", "markups"}]}. Prose and a code fence around the JSON are allowed. Each problem
    /// with one markup is a line in <paramref name="problems"/>; the markup is kept when it can still be reported.
    /// </summary>
    public static List<Markup> Parse(string? text, out List<string> problems)
    {
        problems = [];
        var json = JsonPart(text);
        if (json is null) { problems.Add("no JSON list of markups found"); return []; }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return Parse(doc.RootElement, problems);
        }
        catch (JsonException ex) { problems.Add("the markup JSON can't be read: " + ex.Message); return []; }
    }

    public static List<Markup> Parse(JsonElement root, List<string> problems)
    {
        var list = new List<Markup>();
        void Page(JsonElement e, int page, string? sheet)
        {
            if (e.ValueKind != JsonValueKind.Array) { problems.Add("markups is not a list"); return; }
            foreach (var m in e.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object) list.Add(One(m, page, sheet, problems));
                else problems.Add("a markup that is not an object was left out");
        }
        if (root.ValueKind == JsonValueKind.Array) Page(root, 1, null);
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var sheet = Str(root, "sheet_number") ?? Str(root, "sheet");
            var page = Int(root, "page") ?? 1;
            if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var p in pages.EnumerateArray())
                {
                    i++;
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    if (p.TryGetProperty("markups", out var ms)) Page(ms, Int(p, "page") ?? i, Str(p, "sheet_number") ?? Str(p, "sheet") ?? sheet);
                }
            }
            else if (root.TryGetProperty("markups", out var ms)) Page(ms, page, sheet);
            else if (root.TryGetProperty("type", out _) || root.TryGetProperty("bbox", out _)) list.Add(One(root, page, sheet, problems));
            else problems.Add("the JSON has no markups list");
        }
        else problems.Add("the markup JSON is not a list or object");

        // Ids: as given when unique, else M1, M2... in order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var id = list[i].Id.Trim();
            if (id.Length == 0 || !seen.Add(id))
            {
                var n = i + 1;
                while (!seen.Add("M" + n)) n++;
                list[i].Id = "M" + n;
            }
            else list[i].Id = id;
        }
        return list;
    }

    private static Markup One(JsonElement m, int page, string? sheet, List<string> problems)
    {
        var change = m.TryGetProperty("change", out var c) && c.ValueKind == JsonValueKind.Object ? c : m;
        string? S(string key) => Str(change, key) ?? Str(m, key);
        var markup = new Markup
        {
            Id = Str(m, "id") ?? "",
            Page = Int(m, "page") ?? page,
            Sheet = Str(m, "sheet_number") ?? Str(m, "sheet") ?? sheet,
            Type = TypeOf(Str(m, "type")),
            Text = Str(m, "text") ?? "",
            Target = Str(m, "target") ?? Str(m, "target_hint") ?? "",
            From = S("from"),
            To = S("to") ?? S("new_text") ?? S("value"),
            Parameter = S("parameter"),
            Mode = (S("mode") ?? "override").Trim().ToLowerInvariant() == "move" ? "move" : "override",
            Confidence = Num(m, "confidence") is { } conf ? Math.Max(0, Math.Min(1, conf > 1 ? conf / 100 : conf)) : 1,
        };
        markup.ValueMm = Num(change, "value_mm") ?? Num(m, "value_mm") ?? (markup.Type == "dimension" ? Number(markup.To ?? markup.Text) : null);
        var label = markup.Id.Length > 0 ? markup.Id : $"markup {markup.Text}".Trim();
        markup.Box = Box(m, "bbox", 4, label, problems) ?? Box(m, "box", 4, label, problems);
        markup.Point = Box(m, "point", 2, label, problems);
        markup.Kind = KindOf(S("kind") ?? S("action"), markup);
        if (markup.Box is null && markup.Point is null) problems.Add($"{label}: no bbox or point, so it can't be placed on the sheet");
        return markup;
    }

    /// <summary>What was drawn, from the words a reader may use.</summary>
    public static string TypeOf(string? type)
    {
        var t = (type ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return t switch
        {
            "" => "text",
            "strike" or "strikethrough" or "strike_through" or "struck" or "crossed_out" or "cross_out" or "delete" => "strike",
            "dim" or "dimension" or "dimension_change" or "dimension_text" => "dimension",
            "cloud" or "revision_cloud" or "bubble" => "cloud",
            "arrow" or "leader" => "arrow",
            "note" or "comment" or "callout" => "note",
            _ => "text",
        };
    }

    /// <summary>What the markup asks for: the kind given (in any of the usual words), else inferred from what was drawn.</summary>
    public static string KindOf(string? given, Markup m)
    {
        var k = (given ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        switch (k)
        {
            case "edit_text" or "replace_text" or "change_text" or "text" or "edit_note" or "replace": return "edit_text";
            case "add_note" or "add_text" or "new_note" or "note" or "add": return "add_note";
            case "set_parameter" or "parameter" or "change_parameter" or "set_value" or "change_value": return "set_parameter";
            case "set_dimension" or "dimension" or "change_dimension" or "override_dimension" or "move": return "set_dimension";
            case "delete" or "remove" or "strike" or "delete_element": return "delete";
            case "none" or "no_change" or "comment" or "query" or "question": return "none";
        }
        if (k.Length > 0) return "none";
        if (m.Type == "strike") return "delete";
        if (m.Type == "dimension" && (m.ValueMm is not null || !string.IsNullOrWhiteSpace(m.To))) return "set_dimension";
        if (m.Parameter is { Length: > 0 } && m.To is not null) return "set_parameter";
        if (m.To is { Length: > 0 } && m.From is { Length: > 0 }) return "edit_text";
        return "none";
    }

    /// <summary>The JSON in a reply: a fenced block when there is one, else from the first [ or { to the matching last one.</summary>
    internal static string? JsonPart(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text!.Trim();
        var fence = Regex.Match(t, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.CultureInvariant);
        if (fence.Success) t = fence.Groups[1].Value.Trim();
        int a = t.IndexOf('['), b = t.IndexOf('{');
        var start = a < 0 ? b : b < 0 ? a : Math.Min(a, b);
        if (start < 0) return null;
        var end = t[start] == '[' ? t.LastIndexOf(']') : t.LastIndexOf('}');
        return end > start ? t.Substring(start, end - start + 1) : null;
    }

    private static double[]? Box(JsonElement m, string key, int count, string label, List<string> problems)
    {
        if (!m.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Array) return null;
        var values = e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToArray();
        if (values.Length != count) { problems.Add($"{label}: {key} needs {count} numbers"); return null; }
        // Percentages (0..100) are read as fractions.
        if (values.Any(v => v > 1.5)) values = values.Select(v => v / 100).ToArray();
        if (values.Any(v => v < -0.05 || v > 1.05)) { problems.Add($"{label}: {key} is outside the sheet (use fractions 0..1 of the sheet border)"); return null; }
        values = values.Select(v => Math.Max(0, Math.Min(1, v))).ToArray();
        if (count == 4) return [Math.Min(values[0], values[2]), Math.Min(values[1], values[3]), Math.Max(values[0], values[2]), Math.Max(values[1], values[3])];
        return values;
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static int? Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? Num(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String ? Number(v.GetString()) : null;
    }

    /// <summary>A number in a dimension text ("3000", "3,000 mm", "3.0 m" is 3000), or null.</summary>
    public static double? Number(string? text)
    {
        var m = Regex.Match(text ?? "", @"(-?\d[\d,]*(?:\.\d+)?)\s*(mm|m)?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        return m.Groups[2].Value.Equals("m", StringComparison.OrdinalIgnoreCase) ? v * 1000 : v;
    }

    // ---- Where it is ------------------------------------------------------------------------------------------------------

    /// <summary>A point given as fractions of the sheet border (top-left origin) in sheet coordinates.</summary>
    public static (double X, double Y) PageToSheet(double fx, double fy, LayoutMath.Rect frame) =>
        (frame.X0 + fx * frame.W, frame.Y1 - fy * frame.H);

    /// <summary>The reverse of <see cref="PageToSheet"/>.</summary>
    public static (double Fx, double Fy) SheetToPage(double x, double y, LayoutMath.Rect frame) =>
        (frame.W > 0 ? (x - frame.X0) / frame.W : 0, frame.H > 0 ? (frame.Y1 - y) / frame.H : 0);

    /// <summary>The markup's box on the sheet, or a small square round its point; null when it has neither.</summary>
    public static LayoutMath.Rect? BoxOnSheet(Markup m, LayoutMath.Rect frame, double pointHalfSize)
    {
        if (m.Box is { } b)
        {
            var (x0, y1) = PageToSheet(b[0], b[1], frame);
            var (x1, y0) = PageToSheet(b[2], b[3], frame);
            return new LayoutMath.Rect(x0, y0, x1, y1);
        }
        if (m.Point is { } p)
        {
            var (x, y) = PageToSheet(p[0], p[1], frame);
            return new LayoutMath.Rect(x - pointHalfSize, y - pointHalfSize, x + pointHalfSize, y + pointHalfSize);
        }
        return null;
    }

    /// <summary>Where a markup points, in sheet coordinates: its arrow's tip, else the middle of its box.</summary>
    public static (double X, double Y)? Anchor(Markup m, LayoutMath.Rect frame)
    {
        if (m.Point is { } p) return PageToSheet(p[0], p[1], frame);
        if (m.Box is { } b) return PageToSheet((b[0] + b[2]) / 2, (b[1] + b[3]) / 2, frame);
        return null;
    }

    /// <summary>The viewport a point falls in: the smallest box holding it (a small viewport drawn over a large one wins). -1 when none.</summary>
    public static int ViewportAt(double x, double y, IReadOnlyList<LayoutMath.Rect> boxes)
    {
        var best = -1;
        for (var i = 0; i < boxes.Count; i++)
            if (boxes[i].Contains(x, y) && (best < 0 || boxes[i].Area < boxes[best].Area)) best = i;
        return best;
    }

    /// <summary>
    /// A view point (along the view's right and up directions, model feet) on the sheet, when Revit's own transform is
    /// not available: the viewport's centre shows the view's centre (<paramref name="cu"/>, <paramref name="cv"/>), at
    /// 1 : <paramref name="scale"/>, turned by <paramref name="rotationDeg"/> (counter-clockwise on the sheet).
    /// </summary>
    public static (double X, double Y) ViewToSheet(double u, double v, double cu, double cv, double cx, double cy, double scale, double rotationDeg)
    {
        var s = scale <= 0 ? 1 : scale;
        double du = (u - cu) / s, dv = (v - cv) / s;
        var a = rotationDeg * Math.PI / 180;
        return (cx + du * Math.Cos(a) - dv * Math.Sin(a), cy + du * Math.Sin(a) + dv * Math.Cos(a));
    }

    /// <summary>The reverse of <see cref="ViewToSheet"/>.</summary>
    public static (double U, double V) SheetToView(double x, double y, double cu, double cv, double cx, double cy, double scale, double rotationDeg)
    {
        var s = scale <= 0 ? 1 : scale;
        var a = -rotationDeg * Math.PI / 180;
        double dx = x - cx, dy = y - cy;
        return (cu + (dx * Math.Cos(a) - dy * Math.Sin(a)) * s, cv + (dx * Math.Sin(a) + dy * Math.Cos(a)) * s);
    }

    /// <summary>The gap between two rectangles (0 when they touch or overlap); a point is an empty rectangle.</summary>
    public static double Distance(LayoutMath.Rect a, LayoutMath.Rect b)
    {
        var dx = Math.Max(0, Math.Max(b.X0 - a.X1, a.X0 - b.X1));
        var dy = Math.Max(0, Math.Max(b.Y0 - a.Y1, a.Y0 - b.Y1));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>A point as an empty rectangle.</summary>
    public static LayoutMath.Rect At(double x, double y) => new(x, y, x, y);

    // ---- Which element ----------------------------------------------------------------------------------------------------

    /// <summary>An element a markup may mean: its box on the sheet and how well it matches the markup's words (0..).</summary>
    public sealed record Candidate(long Id, string Label, LayoutMath.Rect Box, double Hint);

    /// <summary>
    /// The element a markup points at: the best word match, then the nearest to the markup (<paramref name="at"/>: its
    /// box, or its arrow's tip as an empty box) within <paramref name="radius"/> (sheet units), then the smaller of two
    /// that both touch it (a beam inside a slab's box). Null with the reason when nothing is near, or when two are equally
    /// likely (the user decides those).
    /// </summary>
    public static (Candidate? Pick, string? Why) Pick(IReadOnlyList<Candidate> candidates, LayoutMath.Rect at, double radius)
    {
        var near = candidates.Select(c => (c, d: Distance(at, c.Box))).Where(t => t.d <= radius)
            .OrderByDescending(t => t.c.Hint).ThenBy(t => t.d).ThenBy(t => t.c.Box.Area).ToList();
        if (near.Count == 0)
        {
            if (candidates.Count == 0) return (null, "nothing of that kind in that part of the sheet");
            // Say how far the nearest is, so a markup drawn a little off (or a mapping that is off) shows as a distance.
            var nearest = candidates.Select(c => (c, d: Distance(at, c.Box))).OrderBy(t => t.d).First();
            return (null, $"nothing of that kind close enough to the markup (the nearest, {nearest.c.Label}, is {nearest.d * 304.8:0} mm away on the sheet; within {radius * 304.8:0} mm counts)");
        }
        var best = near[0];
        if (near.Count == 1) return (best.c, null);
        var second = near[1];
        if (second.c.Hint < best.c.Hint) return (best.c, null);
        var close = radius * 0.1;
        if (best.d == 0 && second.d == 0)
        {
            // Both hold the point: the clearly smaller one is what the markup is drawn on.
            if (best.c.Box.Area <= second.c.Box.Area * 0.5) return (best.c, null);
            return (null, $"it could be {best.c.Label} or {second.c.Label} (both under the markup)");
        }
        if (second.d - best.d < close) return (null, $"it could be {best.c.Label} or {second.c.Label} (equally close)");
        return (best.c, null);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "to", "of", "on", "in", "at", "for", "and", "or", "this", "that", "is", "be", "with", "change", "new", "add", "delete", "remove",
    };

    /// <summary>
    /// How well an element matches the markup's words: the share of the words found in what is known about it (category,
    /// type, mark, text), plus 1 when <paramref name="exact"/> (the text to replace, or an id) is in it.
    /// </summary>
    public static double HintScore(string? words, string? exact, params string?[] facts)
    {
        var all = string.Join(" ", facts.Where(f => !string.IsNullOrWhiteSpace(f))).ToLowerInvariant();
        var score = 0.0;
        if (!string.IsNullOrWhiteSpace(exact) && Squash(all).Contains(Squash(exact!.ToLowerInvariant()))) score += 1;
        var tokens = Regex.Split((words ?? "").ToLowerInvariant(), @"[^a-z0-9]+").Where(t => t.Length >= 2 && !StopWords.Contains(t)).Distinct().ToList();
        if (tokens.Count > 0) score += tokens.Count(t => all.Contains(t)) / (double)tokens.Count;
        return score;
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>
    /// A dimension made to measure <paramref name="wantFeet"/>: how far, along the dimension line, the moving element goes
    /// (it is <paramref name="movingAlong"/> and the fixed one <paramref name="fixedAlong"/> along that line).
    /// </summary>
    public static double MoveAlong(double currentFeet, double wantFeet, double movingAlong, double fixedAlong) =>
        (wantFeet - currentFeet) * (movingAlong >= fixedAlong ? 1 : -1);

    /// <summary>The text after an edit: the part named <paramref name="from"/> replaced (any case) when it is there, else the whole text.</summary>
    public static string EditText(string current, string? from, string to)
    {
        if (!string.IsNullOrEmpty(from))
        {
            var at = current.IndexOf(from!, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return current.Substring(0, at) + to + current.Substring(at + from!.Length);
        }
        return to;
    }

    // ---- The report -------------------------------------------------------------------------------------------------------

    /// <summary>One markup's line in the report. Status: done, skipped, handed over, failed, not matched, planned.</summary>
    public sealed record Line(string Id, int Page, string Sheet, string Type, string Text, string Change, string Where, string Status, string Note)
    {
        public bool Done => Status == "done";
    }

    /// <summary>"3 done, 2 not done (1 skipped, 1 handed over)".</summary>
    public static string Summary(IReadOnlyList<Line> lines)
    {
        var done = lines.Count(l => l.Done);
        var why = lines.Where(l => !l.Done).GroupBy(l => l.Status).Select(g => $"{g.Count()} {g.Key}").ToList();
        return $"{done} done, {lines.Count - done} not done" + (why.Count > 0 ? $" ({string.Join(", ", why)})" : "");
    }

    /// <summary>The Excel workbook's sheets: every markup, then a summary.</summary>
    public static List<(string Name, IList<object?[]> Rows)> Workbook(IReadOnlyList<Line> lines, string source, string revision, IEnumerable<string> exported)
    {
        var rows = new List<object?[]> { new object?[] { "Markup", "Page", "Sheet", "Drawn as", "Text read", "Change", "Where", "Done", "Status", "Note" } };
        rows.AddRange(lines.Select(l => new object?[] { l.Id, l.Page, l.Sheet, l.Type, l.Text, l.Change, l.Where, l.Done ? "done" : "not done", l.Status, l.Note }));
        var summary = new List<object?[]>
        {
            new object?[] { "Redlines", source },
            new object?[] { "Result", Summary(lines) },
            new object?[] { "Done", lines.Count(l => l.Done) },
            new object?[] { "Not done", lines.Count(l => !l.Done) },
            new object?[] { "Revision", revision },
        };
        summary.AddRange(exported.Select(f => new object?[] { "Revised sheet", f }));
        return [("Markups", rows), ("Summary", summary)];
    }

    /// <summary>The chat's report: a summary line and a narrow table in markup order, so it reads like the redlines.</summary>
    public static string Markdown(IReadOnlyList<Line> lines, int max = 40)
    {
        var sb = new StringBuilder();
        sb.Append("**Redlines: ").Append(Summary(lines)).AppendLine("**").AppendLine();
        sb.AppendLine("| Markup | Sheet | Change | Done |").AppendLine("|---|---|---|---|");
        foreach (var l in lines.Take(max))
            sb.AppendLine($"| {l.Id} | {Cell(l.Sheet)} | {Cell(l.Change)} | {(l.Done ? "done" : "not done: " + Cell(l.Status + (l.Note.Length > 0 ? ", " + Clip(l.Note, 80) : "")))} |");
        if (lines.Count > max) sb.AppendLine($"| … | | {lines.Count - max} more in the Excel report | |");
        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "/").Replace("\r", "").Replace("\n", " ");

    internal static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    internal static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    // ---- Asking Claude to read the markups ------------------------------------------------------------------------------

    /// <summary>What Claude is asked to return for one page: the format the tools read.</summary>
    public const string Format = """
        Return JSON only: {"sheet_number": "<the sheet number in the title block, or null>", "markups": [ ... ]} with one entry per markup:
          {"id": "M1", "type": "text|cloud|arrow|strike|dimension|note", "text": "what is written",
           "bbox": [x0, y0, x1, y1], "point": [x, y] (only for an arrow: its tip),
           "target": "words naming what it is about (e.g. 'beam B12', 'general note 3', 'slab thickness')",
           "change": {"kind": "edit_text|add_note|set_parameter|set_dimension|delete|none",
                      "from": "text to replace", "to": "new text or value", "parameter": "parameter name",
                      "value_mm": 3000, "mode": "override|move"},
           "confidence": 0.0-1.0}
        Coordinates are fractions (0..1) of the drawing sheet's outer border, origin at its top-left corner (for a photo, of
        the sheet in the photo, not of the photo). A struck-through item is kind delete; a cloud or comment with no clear
        instruction is kind none. Use set_dimension with mode move only when the markup says to move something to make a
        dimension; otherwise override. Don't guess: give a low confidence when unsure.
        """;

    /// <summary>The one-shot reading: the image to read and the format.</summary>
    public static string VisionMessage(string imagePath, int page) =>
        $"Read the image at {imagePath} with the Read tool. It is page {page} of a structural drawing with hand-drawn or PDF " +
        "markups (redlines) on it. List every markup: text, revision clouds, arrows, strike-throughs and dimension changes.\n\n" + Format;
}
