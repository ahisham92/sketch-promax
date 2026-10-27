using System.Globalization;
using SketchProMax.Tools;
using M = SketchProMax.Tools.ModelStorageMath;

/// <summary>Model storage's pure parts: fitting to the file, the bar, the words, the colours, recommendations and kept measures.</summary>
internal static class ModelStorageTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { RunInvariant(check); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static M.Item Item(string kind, string name, long bytes, bool measured = false, bool unused = false, int count = 1) =>
        new() { Kind = kind, Name = name, Bytes = bytes, Measured = measured, Unused = unused, Count = count };

    private const long MB = 1024 * 1024;

    private static void RunInvariant(Action<string, bool, string> check)
    {
        // ---- Fitting to the file
        var room = M.Balance(100 * MB, [Item(M.Families, "Door", 10 * MB, measured: true), Item(M.Model, "Walls", 20 * MB)]);
        check("storage: what the items don't fill is Revit's own data, last in the bar", room.Total == 100 * MB && room.OnDisk
            && room.Items.Single(i => i.Kind == M.Revit).Bytes == 70 * MB && room.ByKind[^1].Kind.Key == M.Revit
            && room.Items.Sum(i => i.Bytes) == 100 * MB, string.Join(", ", room.Items.Select(i => i.Name + " " + i.Bytes)));
        check("storage: heaviest first", room.Items.Select(i => i.Name).SequenceEqual(["Revit's own data", "Walls", "Door"])
            && room.ByKind.Select(k => k.Kind.Key).SequenceEqual([M.Model, M.Families, M.Revit]), string.Join(", ", room.Items.Select(i => i.Name)));

        var squeezed = M.Balance(100 * MB, [Item(M.Families, "Door", 40 * MB, measured: true), Item(M.Model, "Walls", 90 * MB), Item(M.Views, "Plans", 30 * MB)]);
        check("storage: estimates shrink to what measured sizes leave, measured sizes stay",
            squeezed.Items.Single(i => i.Name == "Door").Bytes == 40 * MB && squeezed.Items.Sum(i => i.Bytes) <= 100 * MB
            && squeezed.Items.Sum(i => i.Bytes) > 100 * MB - 10 && squeezed.Items.Single(i => i.Name == "Walls").Bytes > squeezed.Items.Single(i => i.Name == "Plans").Bytes * 2,
            string.Join(", ", squeezed.Items.Select(i => i.Name + " " + i.Bytes)));

        var over = M.Balance(100 * MB, [Item(M.Families, "A", 150 * MB, measured: true), Item(M.Families, "B", 50 * MB, measured: true), Item(M.Model, "Walls", 50 * MB)]);
        check("storage: measured sizes past the file shrink with everything, in proportion",
            over.Items.Sum(i => i.Bytes) <= 100 * MB && over.Items.Single(i => i.Name == "A").Bytes == 60 * MB && over.Items.Single(i => i.Name == "B").Bytes == 20 * MB,
            string.Join(", ", over.Items.Select(i => i.Name + " " + i.Bytes)));

        var cloud = M.Balance(null, [Item(M.Model, "Walls", 3 * MB), Item(M.Families, "Door", 2 * MB, measured: true), Item(M.Views, "Nothing", 0)]);
        check("storage: without a file the total is the sum, with no Revit row and no empty rows",
            !cloud.OnDisk && cloud.Total == 5 * MB && cloud.Items.Count == 2 && cloud.Items.All(i => i.Kind != M.Revit), cloud.Total.ToString());

        // ---- Words
        check("storage: sizes as Explorer counts them", M.Size(0) == "0 KB" && M.Size(830 * 1024) == "830 KB" && M.Size(12 * MB + 400 * 1024) == "12.4 MB"
            && M.Size(250 * MB) == "250 MB" && M.Size(1300 * MB) == "1.27 GB", M.Size(12 * MB + 400 * 1024) + " " + M.Size(1300 * MB));
        check("storage: the used line", M.UsedLine(130 * MB, 200) == "130 MB of 200 MB used" && M.UsedLine(5 * MB, 0) == "5.0 MB of 200 MB used", M.UsedLine(130 * MB, 200));
        check("storage: over the limit says by how much, within it says nothing", M.OverLine(245 * MB, 200) == "45.0 MB over the limit" && M.OverLine(200 * MB, 200) is null,
            M.OverLine(245 * MB, 200) ?? "null");
        check("storage: the limit stays within 10 MB and 100,000 MB, 200 by default",
            M.ClampLimit(0) == 200 && M.ClampLimit(-5) == 200 && M.ClampLimit(3) == 10 && M.ClampLimit(500) == 500 && M.ClampLimit(int.MaxValue) == 100_000
            && M.DefaultLimitMb == 200, "");

        // ---- The bar
        var bar = M.Bar(room, 200);
        check("storage: under the limit the bar spans the limit and ends with the free part",
            Math.Abs(bar.Sum(b => b.Fraction) - 1) < 1e-9 && bar[^1].Kind is null && Math.Abs(bar[^1].Fraction - 0.5) < 1e-9, string.Join(", ", bar.Select(b => b.Fraction)));
        var full = M.Bar(room, 50);
        check("storage: over the limit the bar is full, with no free part", Math.Abs(full.Sum(b => b.Fraction) - 1) < 1e-9 && full.All(b => b.Kind is not null), "");

        // ---- Colours: each kind keeps 3:1 against the window's light and dark backgrounds (Raised and Bg in both themes)
        uint[] backgrounds = [0xFFFFFF, 0x32373F, 0x2B2F36];
        var weak = M.Kinds.SelectMany(k => backgrounds.Where(b => Contrast(k.Colour, b) < 3).Select(b => $"{k.Name} on {b:X6} {Contrast(k.Colour, b):0.00}")).ToList();
        check("storage: every kind's colour shows on both themes", weak.Count == 0, string.Join("; ", weak));
        check("storage: kinds are unique, with distinct colours", M.Kinds.Select(k => k.Key).Distinct().Count() == M.Kinds.Length
            && M.Kinds.Select(k => k.Colour).Distinct().Count() == M.Kinds.Length && M.KindOf("nope").Key == M.Revit, "");

        // ---- Estimates
        check("storage: estimates grow with what they hold", M.CadBytes(10_000, 0, 0) > M.CadBytes(100, 0, 0) && M.ShapeBytes(100, true) > M.ShapeBytes(100, false)
            && M.FamilyEstimate(5) > M.FamilyEstimate(1) && M.FamilyEstimate(-1) == M.FamilyEstimate(0), "");
        check("storage: an image is its file when known, else its pixels", M.ImageBytes(123_456, 4000, 3000) == 123_456 && M.ImageBytes(null, 4000, 3000) == 4_200_000
            && M.ImageBytes(0, 10, 10) == 20_000, M.ImageBytes(null, 4000, 3000).ToString());

        // ---- Recommendations
        var model = M.Balance(300 * MB,
        [
            Item(M.Families, "Old door", 30 * MB, measured: true, unused: true), Item(M.Families, "Old window", 10 * MB, measured: true, unused: true),
            Item(M.Families, "Column", 5 * MB, measured: true), Item(M.Cad, "site.dwg", 60 * MB), Item(M.Views, "Floor plans not on sheets", 2 * MB, unused: true, count: 40),
            Item(M.Groups, "Old group", 1 * MB, unused: true),
        ]);
        var tips = M.Recommendations(model, new M.Extras(1240, 500 * MB, @"P:\Central.rvt"));
        check("storage: recommendations, biggest saving first, advice after",
            tips.Select(t => t.Tool).SequenceEqual(["cleanup_imports", "purge_unused", "delete_unused_views", "cleanup_model", "cleanup_model", ""]),
            string.Join(", ", tips.Select(t => t.Title)));
        check("storage: a recommendation says how much and how many", tips[1].Text.Contains("40.0 MB") && tips[1].Text.Contains("2 families have nothing placed")
            && tips[2].Text.Contains("40 views are on no sheet") && tips[4].Text.Contains("1,240 warnings") && tips[5].Text.Contains("Compact Central Model"),
            string.Join(" | ", tips.Select(t => t.Text)));
        var tidy = M.Recommendations(M.Balance(100 * MB, [Item(M.Families, "Column", 5 * MB, measured: true)]), new M.Extras(0, 110 * MB, ""));
        check("storage: a tidy model gets no recommendations (a central a little bigger is normal)", tidy.Count == 0, string.Join(", ", tidy.Select(t => t.Title)));

        // ---- Kept measures
        var now = new DateTime(2026, 10, 27, 19, 0, 0);
        var kept = new Dictionary<string, M.Measure>
        {
            [M.FamilyKey("Door", "Doors")] = new() { Bytes = 400_000, At = now.AddDays(-1) },
            [M.FamilyKey("Old", "Doors")] = new() { Bytes = 300_000, At = now.AddDays(-8) },
            [M.FamilyKey("Zero", "Doors")] = new() { Bytes = 0, At = now },
        };
        var read = M.ParseMeasures(M.WriteMeasures(kept), now);
        check("storage: measures are kept a week, empty ones dropped", read.Count == 1 && read[M.FamilyKey("Door", "Doors")].Bytes == 400_000, string.Join(", ", read.Keys));
        check("storage: unreadable kept measures are ignored", M.ParseMeasures("{not json", now).Count == 0 && M.ParseMeasures(null, now).Count == 0, "");
        var a = M.MeasuresFile(@"C:\Models\Port A_local.rvt");
        check("storage: one measures file per model path", a.StartsWith("Port A_local ") && a.EndsWith(".json") && a != M.MeasuresFile(@"D:\Models\Port A_local.rvt")
            && a == M.MeasuresFile(@"c:\models\PORT A_local.rvt".Replace("PORT", "Port")) && M.MeasuresFile("").StartsWith("model "), a);
    }

    private static double Contrast(uint a, uint b)
    {
        static double Lin(uint c) { var v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        static double L(uint c) => 0.2126 * Lin((c >> 16) & 255) + 0.7152 * Lin((c >> 8) & 255) + 0.0722 * Lin(c & 255);
        double x = L(a), y = L(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
