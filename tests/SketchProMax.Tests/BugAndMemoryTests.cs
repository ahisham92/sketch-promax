using System.IO.Compression;
using SketchProMax.Bugs;
using SketchProMax.Chat;

/// <summary>The bug log's "Not sent yet" rule and the chat's memory notes file, which need no Revit and no WPF.</summary>
internal static class BugAndMemoryTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Bug log
        const string published = """
            # Sketch ProMax bug log

            Intro mentioning 2030-01-01 outside the table.

            | # | Date | What happens | Version | Status |
            |---|---|---|---|---|
            | 1 | 2026-10-16 | Something | 0.29.0 | Fixed in 0.30 (please check) |
            | 2 | 2026-10-17 | Another, report 2026-10-17 0900 | 0.28.1 | Open |
            """;
        check("bug log: newest date read from table rows only", BugLog.NewestDate(published) == new DateTime(2026, 10, 17), $"{BugLog.NewestDate(published)}");
        check("bug log: no rows, no date", BugLog.NewestDate("# x\n\nno table") is null, "");

        var reports = new[]
        {
            new BugLog.Report("a.zip", "2026-10-16 1000", new DateTime(2026, 10, 16, 10, 0, 0), "older than the log"),
            new BugLog.Report("b.zip", "2026-10-17 0900", new DateTime(2026, 10, 17, 9, 0, 0), "named in the log"),
            new BugLog.Report("c.zip", "2026-10-17 1500", new DateTime(2026, 10, 17, 15, 0, 0), "same day, not named"),
            new BugLog.Report("d.zip", "2026-10-18 0800", new DateTime(2026, 10, 18, 8, 0, 0), "newer"),
        };
        var unsent = BugLog.Unsent(published, reports);
        check("bug log: not sent = from the newest day on, not named, newest first",
            unsent.Select(r => r.Id).SequenceEqual(["2026-10-18 0800", "2026-10-17 1500"]), string.Join(", ", unsent.Select(r => r.Id)));
        check("bug log: nothing downloaded yet lists every report", BugLog.Unsent(null, reports).Count == 4, "");
        var local = BugLog.Compose(published, unsent, new DateTime(2026, 10, 18, 9, 30, 0));
        check("bug log: local file keeps the published log and adds Not sent yet",
            local.StartsWith("# Sketch ProMax bug log") && local.Contains("## Not sent yet") && local.Contains("| c.zip | same day, not named |")
            && local.IndexOf("d.zip", StringComparison.Ordinal) < local.IndexOf("c.zip", StringComparison.Ordinal), local);
        check("bug log: empty list says how to report", BugLog.Compose(null, [], null).Contains("Report bug"), "");
        check("bug log: error pages are not taken for the log", !BugLog.LooksLikeLog("<html>404</html>") && BugLog.LooksLikeLog(published.Trim()), "");
        var (name, id) = BugLog.NameFor(new DateTime(2026, 10, 18, 8, 5, 9));
        check("bug log: report names carry their time", name == "Bug report 2026-10-18 0805.zip" && id == "2026-10-18 0805", name);

        // Report zips are read back with their description and time.
        var dir = Path.Combine(Path.GetTempPath(), "spm-bugtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var zip = ZipFile.Open(Path.Combine(dir, name), ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("description.txt").Open())) w.Write("Tags overlap | badly");
            File.WriteAllText(Path.Combine(dir, "Bug report broken.zip"), "not a zip");
            var read = BugLog.Reports(dir).OrderBy(r => r.Id).ToList();
            check("bug log: reports read from zips", read.Count == 2 && read.Any(r => r.Id == id && r.Description == "Tags overlap | badly" && r.Created == new DateTime(2026, 10, 18, 8, 5, 0))
                && read.Any(r => r.Description == "(could not be read)"), string.Join("; ", read));
            check("bug log: pipes in descriptions don't break the table", BugLog.Compose(null, read, null).Contains("Tags overlap / badly"), "");
        }
        finally { Directory.Delete(dir, true); }

        // ---- Memory notes
        var text = """
            # Sketch ProMax memory

            ## Global

            - Use mm for all dimensions
            * Sheets are A1

            ## Model: C:\Projects\Tower.rvt

            - Grid A is the north edge

            ## C:\Other.rvt
            - other model note
            stray line
            """;
        var sections = MemoryNotes.Parse(text);
        var (g, m) = MemoryNotes.For(sections, @"c:\projects\tower.RVT");
        check("memory: global and this model's notes, model key case-insensitive",
            g.SequenceEqual(["Use mm for all dimensions", "Sheets are A1"]) && m.SequenceEqual(["Grid A is the north edge"]), string.Join(" | ", g.Concat(m)));
        check("memory: other models' notes are not given", !MemoryNotes.PromptBlock(sections, @"C:\Projects\Tower.rvt").Contains("other model note")
            && MemoryNotes.For(sections, @"C:\Other.rvt").Model.Count == 1, "");
        check("memory: no notes, no prompt block", MemoryNotes.PromptBlock([], "x") == "", "");
        check("memory: add skips duplicates and keeps one line",
            MemoryNotes.Add(sections, null, "Columns\n are   C40") && !MemoryNotes.Add(sections, null, "columns are c40")
            && MemoryNotes.For(sections, null).Global.Last() == "Columns are C40", "");
        MemoryNotes.Add(sections, "New.rvt", "first note for a new model");
        var matches = MemoryNotes.Matches(sections, null, "are");
        check("memory: forget by words finds every match", matches.Count == 2, string.Join(" | ", matches));
        check("memory: exact text wins", MemoryNotes.Matches(sections, null, "sheets are a1").Count == 1, "");
        check("memory: remove and replace", MemoryNotes.Remove(sections, null, "Sheets are A1")
            && MemoryNotes.Replace(sections, @"C:\Projects\Tower.rvt", "Grid A is the north edge", "Grid 1 is the north edge")
            && !MemoryNotes.Remove(sections, null, "not there"), "");
        var round = MemoryNotes.Parse(MemoryNotes.Write(sections));
        check("memory: survives write and read",
            MemoryNotes.For(round, null).Global.SequenceEqual(["Use mm for all dimensions", "Columns are C40"])
            && MemoryNotes.For(round, @"C:\Projects\Tower.rvt").Model.SequenceEqual(["Grid 1 is the north edge"])
            && MemoryNotes.For(round, "New.rvt").Model.Count == 1, MemoryNotes.Write(sections));
        check("memory: empty model sections are dropped, Global always written",
            MemoryNotes.Write([new MemoryNotes.Section("Gone.rvt")]).Contains("## Global") && !MemoryNotes.Write([new MemoryNotes.Section("Gone.rvt")]).Contains("Gone.rvt"), "");
    }
}
