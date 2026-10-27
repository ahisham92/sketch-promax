using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// The pure parts of the Model storage window (UI/ModelStorageWindow, read by Revit/ModelStorageReader): the kinds of
/// things a model holds and their colours, the size estimates, how the measured and estimated sizes are fitted to the
/// file on disk, the limit line, the coloured bar and the recommendations. No Revit types, so the tests compile it.
///
/// Revit doesn't say how many bytes anything inside a model takes. The total is the real file on disk; a loaded family
/// is measured by saving it to a temporary file; an image by its file or its pixels; everything else is estimated from
/// what it contains (counts, lines, faces). Estimates are then fitted so the whole adds up to the file, and what is
/// left is Revit's own data (history, worksharing, settings), as a phone shows System Data.
/// </summary>
internal static class ModelStorageMath
{
    public const int DefaultLimitMb = 200, MinLimitMb = 10, MaxLimitMb = 100_000;

    /// <summary>The limit as set in Settings, kept within <see cref="MinLimitMb"/> to <see cref="MaxLimitMb"/>.</summary>
    public static int ClampLimit(int mb) => mb <= 0 ? DefaultLimitMb : Math.Max(MinLimitMb, Math.Min(MaxLimitMb, mb));

    public static long Bytes(int mb) => (long)mb * 1024 * 1024;

    // ---- Kinds ---------------------------------------------------------------------------------------------------------

    public const string Families = "families", Cad = "cad", Images = "images", Shapes = "shapes", Model = "model", Views = "views",
        Annotation = "annotation", Groups = "groups", Types = "types", Ours = "ours", Revit = "revit";

    /// <param name="Colour">RGB. Every colour keeps 3:1 against the window's light and dark backgrounds (tests check).</param>
    /// <param name="What">One line on what the kind holds and what makes it heavy, shown when a row is opened.</param>
    public sealed record Kind(string Key, string Name, uint Colour, string What);

    public static readonly Kind[] Kinds =
    [
        new(Families, "Families", 0xE5484D, "Loaded families. Each one keeps its whole family file inside the model, used or not."),
        new(Cad, "Imported CAD", 0xD46A12, "DWG, DXF and other CAD imported into the model, not linked. Every line is copied into the file."),
        new(Images, "Images", 0xB58A00, "Pictures and PDFs placed or loaded as images. The whole picture is kept in the model."),
        new(Shapes, "In-place and shapes", 0xA066EC, "In-place families and DirectShapes. Their geometry is stored element by element, so they get heavy."),
        new(Model, "Model elements", 0x3B82F6, "Walls, floors, columns, beams and every other model element, by category."),
        new(Views, "Views and sheets", 0x0E9478, "Views, sheets and schedules with their settings and crop. Views not on sheets are often left over."),
        new(Annotation, "Annotation and details", 0xDB4F9A, "Dimensions, tags, text, detail lines and detail items."),
        new(Groups, "Groups", 0x7A7CF4, "Group types. A group type nobody placed still keeps its members' definitions."),
        new(Types, "Types and materials", 0xA67C5B, "System types, materials, fill patterns, line styles and other settings."),
        new(Ours, "Sketch ProMax data", 0x0B8DB0, "What Sketch ProMax keeps in the model: settings, checklist answers and the time-saved record."),
        new(Revit, "Revit's own data", 0x8691A0, "History, worksharing data, previews and settings Revit keeps for itself. It shrinks when the central model is compacted."),
    ];

    public static Kind KindOf(string key) => Kinds.FirstOrDefault(k => k.Key == key) ?? Kinds[^1];

    // ---- Items ---------------------------------------------------------------------------------------------------------

    /// <summary>One row of the list.</summary>
    public sealed class Item
    {
        public string Kind = Model;
        public string Name = "";
        /// <summary>The grey line under the name, e.g. "Doors · 3 types · 120 placed".</summary>
        public string Detail = "";
        public long Bytes;
        /// <summary>True when the size was measured (a family saved to a file, an image file), false when estimated.</summary>
        public bool Measured;
        /// <summary>How many elements the row stands for.</summary>
        public int Count;
        /// <summary>True when nothing of it is placed (a family with no instances, a group type never placed, a view on no sheet).</summary>
        public bool Unused;
        /// <summary>The elements to select from the row (instances, the import, the shapes); empty: nothing to select.</summary>
        public List<long> Ids = [];
        /// <summary>The family's id when the row is a loaded family (to measure it), else 0.</summary>
        public long FamilyId;
    }

    // ---- Estimates (bytes) ---------------------------------------------------------------------------------------------
    // Typical sizes in Revit files, used only where nothing can be measured. They are deliberately round numbers: the list
    // is for finding the heaviest things, and the whole is fitted to the real file (Balance).

    public const long ModelElement = 1_500, View = 12_000, Sheet = 15_000, Schedule = 8_000, Annotate = 600, Room = 2_500,
        GroupInstance = 1_000, GroupType = 5_000, TypeOrSetting = 2_000, LinkInstance = 2_000;

    /// <summary>An imported CAD file: a base plus each line, point and face its geometry holds.</summary>
    public static long CadBytes(long curves, long faces, long points) => 30_000 + curves * 120 + faces * 1_500 + points * 60;

    /// <summary>An in-place family instance or a DirectShape: a base plus each face of its geometry.</summary>
    public static long ShapeBytes(long faces, bool inPlace) => (inPlace ? 20_000 : 5_000) + faces * 1_500;

    /// <summary>A family that couldn't be measured (not editable, or measuring was stopped): a base plus each type.</summary>
    public static long FamilyEstimate(int types) => 60_000 + Math.Max(0, types) * 25_000L;

    /// <summary>An image: its file when it can be read, else its pixels at a typical compression.</summary>
    public static long ImageBytes(long? file, long width, long height) =>
        file is > 0 ? file.Value : Math.Max(20_000, (long)(Math.Max(0, width) * Math.Max(0, height) * 0.35));

    // ---- Fitting to the file -------------------------------------------------------------------------------------------

    public sealed record Share(Kind Kind, long Bytes);

    /// <param name="Total">The model's size: the file on disk, or the sum of the items when there is no file.</param>
    /// <param name="OnDisk">True when <paramref name="Total"/> is the real file.</param>
    public sealed record Report(long Total, bool OnDisk, List<Item> Items, List<Share> ByKind);

    /// <summary>
    /// Fits the items to the file and sorts them, heaviest first. Measured sizes are kept as they are while they leave at
    /// least a tenth of the file; estimates are scaled down to what is left when together they would pass the file; what
    /// the items don't fill is one "Revit's own data" row. Without a file (a cloud model, never saved) the total is the sum.
    /// The items passed in are changed (their Bytes), so pass fresh ones.
    /// </summary>
    public static Report Balance(long? fileBytes, IEnumerable<Item> items)
    {
        var list = items.Where(i => i.Bytes > 0).ToList();
        var measured = list.Where(i => i.Measured).Sum(i => i.Bytes);
        var estimated = list.Where(i => !i.Measured).Sum(i => i.Bytes);
        long total;
        var onDisk = fileBytes is > 0;
        if (!onDisk) total = measured + estimated;
        else
        {
            total = fileBytes!.Value;
            if (measured + estimated > total)
            {
                if (measured <= total * 0.9)
                    Scale(list.Where(i => !i.Measured), (double)(total - measured) / Math.Max(1, estimated));
                else Scale(list, (double)total / (measured + estimated));
            }
            var left = total - list.Sum(i => i.Bytes);
            if (left > 0)
                list.Add(new Item
                {
                    Kind = Revit, Name = "Revit's own data", Bytes = left,
                    Detail = "History, worksharing data, previews and settings",
                });
        }
        list = list.Where(i => i.Bytes > 0).OrderByDescending(i => i.Bytes).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Kinds heaviest first, Revit's own data always last (as System Data is).
        var byKind = list.GroupBy(i => i.Kind).Select(g => new Share(KindOf(g.Key), g.Sum(i => i.Bytes)))
            .OrderBy(s => s.Kind.Key == Revit ? 1 : 0).ThenByDescending(s => s.Bytes).ToList();
        return new Report(total, onDisk, list, byKind);
    }

    private static void Scale(IEnumerable<Item> items, double factor)
    {
        factor = Math.Max(0, Math.Min(1, factor));
        foreach (var i in items) i.Bytes = (long)Math.Floor(i.Bytes * factor);
    }

    // ---- Words and the bar ---------------------------------------------------------------------------------------------

    /// <summary>A size as Windows Explorer counts it (1 KB = 1024 bytes): "830 KB", "12.4 MB", "1.27 GB".</summary>
    public static string Size(long bytes)
    {
        var c = CultureInfo.CurrentCulture;
        if (bytes < 1024 * 1024) return Math.Max(0, (long)Math.Round(bytes / 1024.0)).ToString("N0", c) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString(bytes < 100L * 1024 * 1024 ? "0.0" : "0", c) + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", c) + " GB";
    }

    /// <summary>The line next to the model's name: "130.0 MB of 200 MB used".</summary>
    public static string UsedLine(long total, int limitMb) => $"{Size(total)} of {Size(Bytes(ClampLimit(limitMb)))} used";

    /// <summary>"45.2 MB over the limit", or null while the model is within it.</summary>
    public static string? OverLine(long total, int limitMb)
    {
        var over = total - Bytes(ClampLimit(limitMb));
        return over > 0 ? Size(over) + " over the limit" : null;
    }

    /// <summary>
    /// The bar's segments, left to right: each kind's share of the bar, then the free part up to the limit (Kind null).
    /// The bar spans the limit, or the model when it is bigger, so a model over the limit fills it.
    /// </summary>
    public static List<(Kind? Kind, double Fraction)> Bar(Report report, int limitMb)
    {
        var span = Math.Max(report.Total, Bytes(ClampLimit(limitMb)));
        var bar = new List<(Kind?, double)>();
        if (span <= 0) return bar;
        foreach (var s in report.ByKind) bar.Add((s.Kind, (double)s.Bytes / span));
        var free = span - report.ByKind.Sum(s => s.Bytes);
        if (free > 0) bar.Add((null, (double)free / span));
        return bar;
    }

    // ---- Recommendations -----------------------------------------------------------------------------------------------

    /// <param name="Tool">The cleanup tool the button runs (its preview comes first), or "" for advice only.</param>
    public sealed record Tip(string Title, string Text, string Tool, string Button, long Bytes);

    /// <summary>What the model reads besides its items: warnings and the central model's file.</summary>
    public sealed record Extras(int Warnings, long? CentralBytes, string CentralPath);

    /// <summary>The recommendations, the biggest saving first; advice without a saving (warnings, the central) after.</summary>
    public static List<Tip> Recommendations(Report report, Extras extras)
    {
        var tips = new List<Tip>();
        var c = CultureInfo.CurrentCulture;
        string N(int n) => n.ToString("N0", c);
        long Sum(IEnumerable<Item> items) => items.Sum(i => i.Bytes);

        var families = report.Items.Where(i => i.Kind == Families && i.Unused).ToList();
        if (families.Count > 0)
            tips.Add(new("Purge unused families", $"Save up to about {Size(Sum(families))}. {N(families.Count)} famil{(families.Count == 1 ? "y has" : "ies have")} nothing placed. The preview shows exactly what can go.",
                "purge_unused", "Purge unused", Sum(families)));
        var cad = report.Items.Where(i => i.Kind == Cad).ToList();
        if (cad.Count > 0)
            tips.Add(new("Remove imported CAD", $"About {Size(Sum(cad))} in {N(cad.Count)} import{(cad.Count == 1 ? "" : "s")}. Link CAD files instead of importing them.",
                "cleanup_imports", "Clean up imports", Sum(cad)));
        var views = report.Items.Where(i => i.Kind == Views && i.Unused).ToList();
        if (views.Count > 0)
        {
            var count = views.Sum(v => Math.Max(1, v.Count));
            tips.Add(new("Delete views not on sheets", $"About {Size(Sum(views))}. {N(count)} view{(count == 1 ? " is" : "s are")} on no sheet. Views named WORKING or _keep are kept.",
                "delete_unused_views", "Delete unused views", Sum(views)));
        }
        var unusedRest = report.Items.Where(i => i.Unused && i.Kind is Groups or Images).ToList();
        if (unusedRest.Count > 0)
            tips.Add(new("Remove unplaced groups and images", $"About {Size(Sum(unusedRest))} in {N(unusedRest.Count)} group type{(unusedRest.Count == 1 ? "" : "s")} or image{(unusedRest.Count == 1 ? "" : "s")} nobody placed.",
                "cleanup_model", "Model health clean", Sum(unusedRest)));
        tips.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        if (extras.Warnings > 0)
            tips.Add(new("Fix warnings", $"{N(extras.Warnings)} warning{(extras.Warnings == 1 ? "" : "s")}. They make every edit and sync slower, even when the file is small.",
                "cleanup_model", "Model health clean", 0));
        if (extras.CentralBytes is { } central && report.OnDisk && central > report.Total * 1.25 && central - report.Total > 20L * 1024 * 1024)
            tips.Add(new("Compact the central model", $"The central model is {Size(central)}, your local copy {Size(report.Total)}. Tick Compact Central Model the next time you synchronize.",
                "", "", 0));
        return tips;
    }

    // ---- Measured families, kept between openings ----------------------------------------------------------------------

    /// <summary>What a family measured, and when: reused for a week so opening the window again is quick.</summary>
    public sealed class Measure
    {
        public long Bytes { get; set; }
        public DateTime At { get; set; }
    }

    public static readonly TimeSpan KeepMeasures = TimeSpan.FromDays(7);

    /// <summary>A family's key in the kept measures: its name and category, as names can repeat across categories.</summary>
    public static string FamilyKey(string name, string category) => category + "|" + name;

    public static Dictionary<string, Measure> ParseMeasures(string? json, DateTime now)
    {
        var kept = new Dictionary<string, Measure>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return kept;
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, Measure>>(json!);
            if (read is not null)
                foreach (var pair in read)
                    if (pair.Value is { Bytes: > 0 } m && now - m.At < KeepMeasures && m.At <= now.AddMinutes(5)) kept[pair.Key] = m;
        }
        catch (JsonException) { }
        return kept;
    }

    public static string WriteMeasures(Dictionary<string, Measure> measures) => JsonSerializer.Serialize(measures);

    /// <summary>A file name for a model's kept measures: the model's file name and a short hash of its full path.</summary>
    public static string MeasuresFile(string modelPath)
    {
        // Split on both separators, so a Windows path names the same file wherever this runs.
        var name = modelPath.Substring(modelPath.LastIndexOfAny(['\\', '/']) + 1);
        if (name.LastIndexOf('.') is var dot and > 0) name = name.Substring(0, dot);
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        if (name.Length == 0) name = "model";
        uint hash = 2166136261;
        foreach (var ch in modelPath.ToUpperInvariant()) hash = (hash ^ ch) * 16777619;
        return $"{name} {hash:x8}.json";
    }
}
