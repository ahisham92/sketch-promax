using System.IO;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// The field test's breadcrumbs, so a run Revit closes in the middle of still says what it was doing. While a run goes,
/// %LOCALAPPDATA%\SketchProMax\field-test\field test running {pid}.jsonl gets one JSON line, flushed to disk, before each
/// step starts (family, label, index, time) and after it ends (its status), with every report row so far. The file is
/// deleted when the run ends by itself (finished, stopped, cancelled or failed). One left behind by a Revit that is gone
/// means Revit closed while its last started step ran: the next start (or the next click on Field test) writes the
/// rows that ran as an "(interrupted)" report with that step as a "crash" row, tells the user once, and keeps the file
/// (renamed "field test crashed") so Continue can carry those rows into a new run that starts after that step.
/// Steps that closed Revit are remembered per Revit year (crashed-steps.json) so a later Continue skips them; a normal
/// run still runs everything, and a step that then ends normally is forgotten. No Revit here: the unit tests compile this file.
/// </summary>
internal static class FieldTestJournal
{
    /// <summary>Steps that build or close the test project: always run again, so a crash in one can't be continued after.</summary>
    public const string SetupPrefix = "setup: ";

    public const string Running = "field test running ", Crashed = "field test crashed ";

    /// <summary>The crash row's message (the report reads "step: Revit closed while this step ran").</summary>
    public const string CrashMessage = "Revit closed while this step ran";

    internal static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "field-test");

    /// <summary>The unit tests and the probe point this elsewhere.</summary>
    internal static string? FolderForTests;

    internal static string Folder => FolderForTests ?? DefaultFolder;

    /// <summary>How the run was started, so Continue starts the next one the same way.</summary>
    internal sealed record Header(int Pid, string Revit, string Addin, DateTime Started, string Title, string? ReportFolder,
        string? TestFolder, List<string>? Families, bool SkipProven, bool IncludeHeavy, bool Workshared, bool ChatRoundTrip, int ToolCapSeconds, bool Continued);

    /// <summary>One report row, with the step that was running when it was added (null: between steps).</summary>
    internal sealed record Entry(string Family, string Tool, string Status, long Ms, string Message, string? Step);

    internal sealed record Started(string Family, string Step, int Index, DateTime At);

    /// <summary>A journal read back: its header, its rows, the step that started and never ended (if any) and the steps that ended.</summary>
    internal sealed class Contents
    {
        public Header Header = null!;
        public readonly List<Entry> Rows = [];
        public Started? Open;
        public readonly HashSet<string> Ended = new(StringComparer.Ordinal);
        public string? Project;
        public string? Report;
        public bool Shown;
        public string? Path;
    }

    // ---- Writing --------------------------------------------------------------------------------------------------------

    /// <summary>The journal of the run in progress. Thread-safe; never throws (a breadcrumb that can't be written is skipped).</summary>
    internal sealed class Writer : IDisposable
    {
        private readonly object _gate = new();
        private FileStream? _file;
        private int _synced;
        private string? _current;
        public readonly HashSet<string> Ended = new(StringComparer.Ordinal);

        public string? Path { get; }

        public Writer(Header header, string? folder = null)
        {
            try
            {
                folder ??= Folder;
                Directory.CreateDirectory(folder);
                Path = System.IO.Path.Combine(folder, Running + header.Pid + ".jsonl");
                _file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                Line(new { kind = "run", header });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                _file = null;
            }
        }

        /// <summary>Before a step: the rows added since the last breadcrumb, then which step starts.</summary>
        public void Begin(string family, string step, int index, int rows, Func<int, Entry> row)
        {
            lock (_gate)
            {
                SyncLocked(rows, row);
                _current = step;
                Line(new { kind = "start", family, step, index, at = DateTime.Now });
            }
        }

        /// <summary>After a step: its rows, then that it ended and how.</summary>
        public void End(string step, string status, int rows, Func<int, Entry> row)
        {
            lock (_gate)
            {
                SyncLocked(rows, row);
                _current = null;
                Ended.Add(step);
                Line(new { kind = "end", step, status, at = DateTime.Now });
            }
        }

        public void Note(string kind, string value)
        {
            lock (_gate) Line(new { kind, value });
        }

        /// <summary>Rows 0..<paramref name="rows"/>-1 of the report so far; those not written yet go now, under the running step.</summary>
        private void SyncLocked(int rows, Func<int, Entry> row)
        {
            for (; _synced < rows; _synced++)
            {
                var r = row(_synced);
                Line(new { kind = "row", family = r.Family, tool = r.Tool, status = r.Status, ms = r.Ms, message = r.Message, step = _current });
            }
        }

        private void Line(object value)
        {
            if (_file is null) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
                _file.Write(bytes, 0, bytes.Length);
                // To the disk, not only the OS cache: the point is to survive Revit closing on the next line.
                _file.Flush(true);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or UnauthorizedAccessException) { }
        }

        /// <summary>The run ended by itself: the journal goes.</summary>
        public void Complete()
        {
            lock (_gate)
            {
                Dispose();
                try { if (Path is not null) File.Delete(Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { _file?.Dispose(); } catch (IOException) { }
                _file = null;
            }
        }
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------------

    /// <summary>A journal's text read back; null when it has no header. A last line cut short by the crash is ignored.</summary>
    internal static Contents? Parse(string text)
    {
        Contents? c = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(e, "kind");
                if (kind == "run" && e.TryGetProperty("header", out var h))
                {
                    try { c = new Contents { Header = h.Deserialize<Header>()! }; }
                    catch (JsonException) { return null; }
                    continue;
                }
                if (c is null) continue;
                switch (kind)
                {
                    case "start":
                        c.Open = new Started(Str(e, "family"), Str(e, "step"), e.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0,
                            e.TryGetProperty("at", out var at) && at.TryGetDateTime(out var when) ? when : DateTime.MinValue);
                        break;
                    case "end":
                        c.Ended.Add(Str(e, "step"));
                        if (c.Open?.Step == Str(e, "step")) c.Open = null;
                        break;
                    case "row":
                        c.Rows.Add(new Entry(Str(e, "family"), Str(e, "tool"), Str(e, "status"), e.TryGetProperty("ms", out var ms) && ms.TryGetInt64(out var m) ? m : 0,
                            Str(e, "message"), e.TryGetProperty("step", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null));
                        break;
                    case "project": c.Project = Str(e, "value"); break;
                    case "reported": c.Report = Str(e, "value"); break;
                    case "shown": c.Shown = true; break;
                }
            }
        }
        return c;
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// <summary>
    /// True when the journal's Revit is gone. <paramref name="alive"/> says whether a process id is a running Revit: the
    /// same check the running-call file uses (RunningCall), so both agree on which Revit closed.
    /// </summary>
    internal static bool IsLeftover(Header h, int currentPid, Func<int, bool> alive) => h.Pid != currentPid && !alive(h.Pid);

    /// <summary>The rows of the interrupted report: what ran, then the crashed step as a "crash" row (a failure).</summary>
    internal static List<Entry> InterruptedRows(Contents c)
    {
        var rows = new List<Entry>(c.Rows);
        if (c.Open is { } open)
            rows.Add(new Entry(open.Family, open.Step, "crash", 0,
                $"{CrashMessage} (step {open.Index}, started {open.At:HH:mm:ss})", open.Step));
        return rows;
    }

    /// <summary>The line the user is told: "Revit closed during the field test, while running X (Family). A report of what ran is in F."</summary>
    internal static string Tell(string step, string familyTitle, string folder) =>
        $"Revit closed during the field test, while running {step} ({familyTitle}). A report of what ran is in {folder}.";

    // ---- Continuing -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Where a continued run starts: the steps that ended before the crash are not run again (their rows are carried into
    /// the new report), and the crashed step, with every step that closed Revit before in this Revit year, is not run
    /// either: it is a "crash" row, counted as a failure.
    /// </summary>
    internal sealed class Resume
    {
        public readonly Dictionary<string, List<Entry>> Carried = new(StringComparer.Ordinal);
        public readonly HashSet<string> Crashed = new(StringComparer.Ordinal);
        public string From = "";
        public DateTime When;

        /// <summary>True when the step is not run again: done before the crash, or it closed Revit.</summary>
        public bool Skips(string step) => Carried.ContainsKey(step) || Crashed.Contains(step);

        public string CarriedMessage(string message) => $"done before Revit closed ({When:yyyy-MM-dd HH:mm}): {message}";

        public string CrashedMessage(string step) =>
            $"not run again: Revit closed while this step ran ({(step == From ? "in the run this continues" : "in an earlier run")}). Run everything to run it again.";
    }

    /// <summary>Can the run be continued after its crash? Not when Revit closed while building or closing the test project.</summary>
    internal static bool CanContinue(Contents c) => c.Open is { } open && !open.Step.StartsWith(SetupPrefix, StringComparison.Ordinal);

    internal static Resume ResumeFrom(Contents c, IEnumerable<string> earlierCrashes)
    {
        var r = new Resume { From = c.Open?.Step ?? "", When = c.Header.Started };
        foreach (var row in c.Rows)
        {
            if (row.Step is not { } step || !c.Ended.Contains(step) || step.StartsWith(SetupPrefix, StringComparison.Ordinal)) continue;
            if (!r.Carried.TryGetValue(step, out var list)) r.Carried[step] = list = [];
            list.Add(row);
        }
        if (c.Open is { } open) r.Crashed.Add(open.Step);
        foreach (var step in earlierCrashes) r.Crashed.Add(step);
        foreach (var step in r.Crashed) r.Carried.Remove(step);
        return r;
    }

    // ---- Steps that closed Revit ----------------------------------------------------------------------------------------

    internal sealed record CrashedStep(string Revit, string Step, string Family, string Date, string Version);

    private static string CrashFile => System.IO.Path.Combine(Folder, "crashed-steps.json");

    internal static List<CrashedStep> LoadCrashes()
    {
        try { return File.Exists(CrashFile) ? JsonSerializer.Deserialize<List<CrashedStep>>(File.ReadAllText(CrashFile)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static void SaveCrashes(List<CrashedStep> list)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(CrashFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static void RememberCrash(CrashedStep step)
    {
        var list = LoadCrashes();
        if (!list.Any(x => x.Revit == step.Revit && x.Step == step.Step)) list.Add(step);
        SaveCrashes(list);
    }

    /// <summary>A step that ended normally in a later run of this Revit year is not a crash any more.</summary>
    internal static void Forget(string revit, IEnumerable<string> ended)
    {
        var done = new HashSet<string>(ended, StringComparer.Ordinal);
        var list = LoadCrashes();
        if (list.RemoveAll(x => x.Revit == revit && done.Contains(x.Step)) > 0) SaveCrashes(list);
    }

    internal static IEnumerable<string> CrashesFor(string revit) => LoadCrashes().Where(x => x.Revit == revit).Select(x => x.Step);

    // ---- Leftovers --------------------------------------------------------------------------------------------------------

    /// <summary>Journals of this Revit year whose Revit is gone (oldest first), read back.</summary>
    internal static List<Contents> Leftovers(string revit, int currentPid, Func<int, bool> alive)
    {
        var found = new List<Contents>();
        try
        {
            if (!Directory.Exists(Folder)) return found;
            foreach (var file in Directory.GetFiles(Folder, Running + "*.jsonl"))
            {
                if (Read(file) is not { } c || c.Header.Revit != revit || !IsLeftover(c.Header, currentPid, alive)) continue;
                found.Add(c);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found.OrderBy(c => c.Header.Started).ToList();
    }

    /// <summary>The newest crashed journal of this Revit year that can still be continued, or null.</summary>
    internal static Contents? LastCrash(string revit)
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;
            return Directory.GetFiles(Folder, Crashed + "*.jsonl").Select(Read).OfType<Contents>()
                .Where(c => c.Header.Revit == revit).OrderByDescending(c => c.Header.Started).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static Contents? Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var c = Parse(reader.ReadToEnd());
            if (c is not null) c.Path = file;
            return c;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A leftover journal, reported: kept as "field test crashed …" with where its report went, for Continue.</summary>
    internal static string? MarkReported(Contents c, string? report)
    {
        if (c.Path is null) return null;
        try
        {
            File.AppendAllText(c.Path, JsonSerializer.Serialize(new { kind = "reported", value = report ?? "" }) + "\n");
            var to = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(c.Path)!, Crashed + c.Header.Pid + " " + c.Header.Started.ToString("yyyyMMdd-HHmmss") + ".jsonl");
            if (File.Exists(to)) File.Delete(to);
            File.Move(c.Path, to);
            c.Path = to;
            c.Report = report;
            return to;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The user has been told about this crash: not again.</summary>
    internal static void MarkShown(Contents c)
    {
        c.Shown = true;
        try { if (c.Path is not null) File.AppendAllText(c.Path, JsonSerializer.Serialize(new { kind = "shown" }) + "\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Continued (or a full run finished): the crashed journals of this Revit year are done with.</summary>
    internal static void ClearCrashed(string revit)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.GetFiles(Folder, Crashed + "*.jsonl"))
                if (Read(file) is { } c && c.Header.Revit == revit)
                    try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- The field test's own check of all this ---------------------------------------------------------------------------

    /// <summary>
    /// Writes a journal in <paramref name="folder"/> as a run would, stops as if Revit closed during its third step, then
    /// reads it back the way the next start does: found as left behind by a Revit that is gone, the interrupted rows end in
    /// a crash row, Continue carries the steps that ended and skips the crashed one, the crash is remembered and forgotten
    /// once that step ends normally, and a run that ends by itself leaves no journal. Null when all is right, else what isn't.
    /// </summary>
    internal static string? SelfCheck(string folder)
    {
        var saved = FolderForTests;
        FolderForTests = folder;
        try
        {
            const int dead = 999_999_001;
            var problems = new List<string>();
            var rows = new List<Entry>();
            Entry Row(int i) => rows[i];
            var header = new Header(dead, "2099", "0.0.0", new DateTime(2099, 1, 2, 3, 4, 5), "field test", null, null, null, true, true, true, false, 120, false);
            using (var w = new Writer(header, folder))
            {
                if (w.Path is null) return "the journal couldn't be written in " + folder;
                w.Begin("setup", SetupPrefix + "building the test project", 1, rows.Count, Row);
                w.End(SetupPrefix + "building the test project", "done", rows.Count, Row);
                w.Begin("other", "get_model_context", 2, rows.Count, Row);
                rows.Add(new("other", "get_model_context", "pass", 12, "ok", null));
                w.End("get_model_context", "pass", rows.Count, Row);
                w.Begin("other", "purge_unused", 3, rows.Count, Row);
                // Revit closes here: no End, no Complete.
            }
            var found = Leftovers("2099", 1, _ => false);
            if (found.Count != 1) return $"{found.Count} journals found as left behind, not 1";
            var c = found[0];
            if (Leftovers("2099", 1, _ => true).Count != 0) problems.Add("a journal of a Revit still running was taken as left behind");
            if (Leftovers("2099", dead, _ => false).Count != 0) problems.Add("this Revit's own journal was taken as left behind");
            if (c.Open?.Step != "purge_unused" || c.Open.Family != "other") problems.Add("the crashed step isn't the one that started last");
            var interrupted = InterruptedRows(c);
            if (interrupted.Count != 2 || interrupted[1].Status != "crash" || !interrupted[1].Message.StartsWith(CrashMessage, StringComparison.Ordinal))
                problems.Add("the interrupted rows don't end with the crash row");
            if (!CanContinue(c)) problems.Add("a crash in a tool step can't be continued after");
            var resume = ResumeFrom(c, ["old_crash"]);
            if (!resume.Carried.ContainsKey("get_model_context") || resume.Carried.ContainsKey(SetupPrefix + "building the test project"))
                problems.Add("Continue doesn't carry exactly the tool steps that ended");
            if (!resume.Crashed.SetEquals(["purge_unused", "old_crash"])) problems.Add("Continue would run a step that closed Revit");
            RememberCrash(new("2099", "purge_unused", "other", "2099-01-02", "0.0.0"));
            if (!CrashesFor("2099").Contains("purge_unused")) problems.Add("the crashed step isn't remembered");
            Forget("2099", ["purge_unused"]);
            if (CrashesFor("2099").Any()) problems.Add("a step that ended normally later is still remembered as a crash");
            if (MarkReported(c, Path.Combine(folder, "report.md")) is null || LastCrash("2099") is not { Shown: false } last) problems.Add("the reported crash can't be found for Continue");
            else
            {
                MarkShown(last);
                if (LastCrash("2099")?.Shown != true) problems.Add("the crash would be told twice");
            }
            ClearCrashed("2099");
            if (LastCrash("2099") is not null) problems.Add("the crashed journal stays after a continued run");
            var done = new Writer(header with { Pid = dead + 1 }, folder);
            done.Begin("other", "x", 1, rows.Count, Row);
            done.End("x", "pass", rows.Count, Row);
            done.Complete();
            if (done.Path is null || File.Exists(done.Path)) problems.Add("a run that ended by itself left its journal");
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }
        finally { FolderForTests = saved; }
    }
}
