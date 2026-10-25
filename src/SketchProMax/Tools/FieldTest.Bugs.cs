using System.IO;
using System.Net.Http;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Update;

namespace SketchProMax.Tools;

/// <summary>
/// The probes for purge and cleanup and for the bug log rows no tool case covers (the loader's fixed place, startup
/// time, the update check's fallback), and the report's Bug log section: each fixed row of the bug log with the check
/// that fails if the bug comes back (<see cref="FieldTestCoverage.BugChecks"/>).
/// </summary>
internal static partial class FieldTest
{
    // ---- Purge and cleanup (in the test project, rolled back) ------------------------------------------------------------

    /// <summary>A new wall type that only a new material uses: one purge finds both (the material in a later pass).</summary>
    private static (string, string) CleanupPasses(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var basic = BasicWallType(doc);
        if (basic is null) return Fail("the test project has no basic wall type with layers");
        var tag = Guid.NewGuid().ToString("N").Substring(0, 6);
        using var group = new TransactionGroup(doc, "Field test: purge passes");
        group.Start();
        try
        {
            ElementId materialId, typeId;
            using (var t = new Transaction(doc, "Field test: an unused type and its material"))
            {
                t.Start();
                materialId = Material.Create(doc, "SPM purge probe material " + tag);
                var type = (WallType)basic.Duplicate("SPM purge probe type " + tag);
                var layers = type.GetCompoundStructure();
                layers.SetMaterialId(0, materialId);
                type.SetCompoundStructure(layers);
                typeId = type.Id;
                t.Commit();
            }
            var only = new[] { "SPM purge probe *" + tag };
            var preview = Call(app, s.Registry, "purge_unused", new { only });
            if (preview.IsError) return Fail("preview: " + Short(preview.Text, 400));
            using var p = JsonDocument.Parse(preview.Text);
            var problems = new List<string>();
            var total = p.RootElement.GetProperty("total").GetInt32();
            var summary = p.RootElement.GetProperty("summary").GetString() ?? "";
            // The preview deletes nothing, not even in a rolled-back transaction (Revit 2024 closed inside the old one):
            // it lists what is unused now, the type; the material only goes unused once the type is gone, when applying.
            if (total != 1) problems.Add($"the preview found {total}, not just the unused type");
            if (!summary.Contains(CleanupMath.PreviewPassesNote)) problems.Add("the preview doesn't say applying finds more");
            if (doc.GetElement(typeId) is null || doc.GetElement(materialId) is null) problems.Add("the preview deleted something");
            if (doc.IsModifiable) problems.Add("the preview left a transaction open");
            if (problems.Count > 0) return Fail(string.Join("; ", problems) + ". " + Short(preview.Text, 300));

            var applied = Call(app, s.Registry, "purge_unused", new { only, apply = true });
            if (applied.IsError) return Fail("apply: " + Short(applied.Text, 400));
            using var a = JsonDocument.Parse(applied.Text);
            var passes = a.RootElement.GetProperty("passes").GetInt32();
            if (doc.GetElement(typeId) is not null) problems.Add("the type is still there");
            if (doc.GetElement(materialId) is not null) problems.Add("the material is still there");
            if (passes < 2) problems.Add($"applying took {passes} pass(es): the material only goes unused once the type is gone");
            return problems.Count == 0
                ? Pass($"preview: the type found, nothing deleted and no transaction opened; apply deleted both in {passes} passes ({Short(a.RootElement.GetProperty("summary").GetString() ?? "", 160)}); rolled back")
                : Fail(string.Join("; ", problems) + ". " + Short(applied.Text, 300));
        }
        finally
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
        }
    }

    /// <summary>An unused type another user owns (a test hook says so) is left alone, in the preview and when applying.</summary>
    private static (string, string) CleanupWorkshared(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var basic = BasicWallType(doc);
        if (basic is null) return Fail("the test project has no basic wall type with layers");
        var tag = Guid.NewGuid().ToString("N").Substring(0, 6);
        using var group = new TransactionGroup(doc, "Field test: purge owned");
        group.Start();
        try
        {
            ElementId typeId;
            using (var t = new Transaction(doc, "Field test: an unused type"))
            {
                t.Start();
                typeId = basic.Duplicate("SPM purge owned type " + tag).Id;
                t.Commit();
            }
            Cleanup.TestOwner = id => id == typeId ? "a colleague (field test)" : null;
            var only = new[] { "SPM purge owned type " + tag };
            var preview = Call(app, s.Registry, "purge_unused", new { only });
            var applied = Call(app, s.Registry, "purge_unused", new { only, apply = true });
            var problems = new List<string>();
            foreach (var (name, r) in new[] { ("preview", preview), ("apply", applied) })
            {
                if (r.IsError) { problems.Add($"{name}: {Short(r.Text, 200)}"); continue; }
                using var j = JsonDocument.Parse(r.Text);
                if (j.RootElement.GetProperty("total").GetInt32() != 0) problems.Add($"{name} would delete the owned type");
                var left = j.RootElement.TryGetProperty("leftItems", out var items) && items.ValueKind == JsonValueKind.Array
                    && items.EnumerateArray().Any(i => i.GetProperty("id").GetInt64() == typeId.Value && i.GetProperty("reason").GetString() == Cleanup.Owned);
                if (!left) problems.Add($"{name} doesn't list it as {Cleanup.Owned}");
            }
            if (doc.GetElement(typeId) is null) problems.Add("the owned type was deleted");
            return problems.Count == 0
                ? Pass($"the type owned by someone else stays, in the preview and when applying ({(doc.IsWorkshared ? "workshared" : "not workshared")} test project); rolled back")
                : Fail(string.Join("; ", problems) + ". " + Short(applied.Text, 300));
        }
        finally
        {
            Cleanup.TestOwner = null;
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
        }
    }

    // ---- The central model (in the test project, rolled back) -------------------------------------------------------------

    /// <summary>
    /// A batch asks the central model once, a busy one once more, then nothing for a while; and a batch stops once an item
    /// fails while Revit shows "Unable to Access the Model". Test hooks stand in for a real central model: four new types
    /// look in central and free, and the request fails as if the central model were busy and then didn't answer.
    /// </summary>
    private static (string, string) CentralOnce(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var basic = BasicWallType(doc);
        if (basic is null) return Fail("the test project has no basic wall type with layers");
        var problems = new List<string>();
        if (!Revit.CentralAccess.IsUnavailable("The requested operation cannot be completed because the model Project.rvt is not currently available."))
            problems.Add("Revit's dialog text isn't recognised");
        var tag = Guid.NewGuid().ToString("N").Substring(0, 6);
        using var group = new TransactionGroup(doc, "Field test: central model");
        group.Start();
        try
        {
            var ids = new List<ElementId>();
            using (var t = new Transaction(doc, "Field test: four types"))
            {
                t.Start();
                for (var i = 1; i <= 4; i++) ids.Add(basic.Duplicate($"SPM central {tag} {i}").Id);
                t.Commit();
            }
            object? Rename(ElementId id) { doc.GetElement(id).Name += " x"; return null; }
            ItemBatch.Result Batch(Func<ElementId, object?> apply) =>
                ItemBatch.Run(doc, "Field test: central model", ids, id => id.Value.ToString(), id => [id], apply, cancellable: false);

            Revit.CentralAccess.Reset();
            Revit.CentralAccess.NeedsBorrowForTest = ids.Contains;
            var calls = 0;
            Revit.CentralAccess.FailForTest = () => ++calls == 1 ? ("The central model is busy (field test).", true) : ("it didn't answer (field test).", false);
            var before = Revit.CentralAccess.Requests;
            var first = Batch(Rename);
            var asked = Revit.CentralAccess.Requests - before;
            if (asked != 2) problems.Add($"the first batch asked the central model {asked} times (want 2: once, and once more because it was busy)");
            if (first.Done != 0 || first.Items.Any(i => i.Error is not { } e || !e.StartsWith("not done:") || !e.Contains("Unable to Access the Model")))
                problems.Add("the first batch's items aren't all not done with the reason: " + Short(first.Items.FirstOrDefault()?.Error ?? "", 200));

            before = Revit.CentralAccess.Requests;
            var second = Batch(Rename);
            if (Revit.CentralAccess.Requests != before) problems.Add("a second batch asked the central model again during the pause");
            if (second.Done != 0 || second.Items.Any(i => i.Error?.Contains("waits until") != true))
                problems.Add("the second batch's items don't say when Sketch ProMax asks again: " + Short(second.Items.FirstOrDefault()?.Error ?? "", 200));

            // Nothing needs borrowing now; the dialog shows while the second item runs and the item fails.
            Revit.CentralAccess.Reset();
            Revit.CentralAccess.FailForTest = null;
            Revit.CentralAccess.NeedsBorrowForTest = _ => false;
            before = Revit.CentralAccess.Requests;
            var third = Batch(id =>
            {
                if (id != ids[1]) return Rename(id);
                Revit.CentralAccess.Noticed(doc, "field test");
                throw new InvalidOperationException("the central model is not currently available (field test)");
            });
            if (Revit.CentralAccess.Requests != before) problems.Add("a batch that needed nothing borrowed asked the central model");
            var stoppedRest = third.Items.Skip(2).All(i => i.Error?.Contains("stopped answering") == true);
            if (!third.Items[0].Ok || third.Items[1].Ok || !stoppedRest)
                problems.Add("the third batch didn't keep the first item and stop after the second: " +
                             string.Join(" | ", third.Items.Select(i => i.Ok ? "done" : Short(i.Error ?? "", 80))));
            return problems.Count == 0
                ? Pass($"one request for the batch and one retry while busy, then none during the {Revit.CentralAccess.Pause.TotalSeconds:0} s pause, " +
                       $"each item saying why; a batch stops once an item fails while the dialog shows, keeping what was done; rolled back")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            Revit.CentralAccess.FailForTest = null;
            Revit.CentralAccess.NeedsBorrowForTest = null;
            Revit.CentralAccess.Reset();
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
        }
    }

    private static WallType? BasicWallType(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
            .FirstOrDefault(t => t.Kind == WallKind.Basic && t.GetCompoundStructure() is { LayerCount: > 0 });

    // ---- Bug log rows with no tool case ------------------------------------------------------------------------------------

    /// <summary>Bug #17: the loader is the one at the fixed place in the add-ins folder, and the manifest names it.</summary>
    private static (string, string) LoaderFixed()
    {
        var path = AppDomain.CurrentDomain.GetData("SketchProMax.Loader.Path") as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Fail("this core was not started by the loader (no loader path)");
        var full = Path.GetFullPath(path);
        var problems = new List<string>();
        if (!full.EndsWith(Path.DirectorySeparatorChar + AddinFiles.LoaderPath, StringComparison.OrdinalIgnoreCase))
            problems.Add($"the loader is at {full}, not at <add-ins folder>\\{AddinFiles.LoaderPath}");
        var addins = Path.GetDirectoryName(Path.GetDirectoryName(full));
        var manifest = addins is null ? null : Path.Combine(addins, AddinFiles.ManifestName);
        if (manifest is null || !File.Exists(manifest)) problems.Add($"no {AddinFiles.ManifestName} next to the loader's folder");
        else if (!AddinFiles.IsSettled(File.ReadAllText(manifest))) problems.Add($"{manifest} doesn't name only the loader at {AddinFiles.LoaderPath}");
        return problems.Count == 0 ? Pass($"loader {full}; {manifest} names it (the place Revit's Always Load is remembered for)") : Fail(string.Join("; ", problems));
    }

    /// <summary>Bug #18: what Sketch ProMax took at Revit start, and what its handlers added to opening models.</summary>
    private static (string, string) StartupTimes()
    {
        const long startLimit = 3000, openLimit = 1000;
        var start = Revit.StartupTimings.StartMs;
        var handlers = Revit.StartupTimings.WorstHandlersMs;
        var line = Revit.StartupTimings.StartLine();
        if (start < 0) return ("skip", "not measured in this session (the core started without the loader's timings): " + line);
        var problems = new List<string>();
        if (start > startLimit) problems.Add($"the core took {start} ms to start (limit {startLimit} ms)");
        if (handlers > openLimit) problems.Add($"Sketch ProMax's DocumentOpened handlers took {handlers} ms on one open (limit {openLimit} ms)");
        var detail = line + (handlers >= 0 ? $"; slowest DocumentOpened handlers {handlers} ms" : "; no model opened before the test");
        return problems.Count == 0 ? Pass(detail) : Fail(string.Join("; ", problems) + ". " + detail);
    }

    /// <summary>Bug #23: GitHub's API made to fail; the check still reads the latest tag from the github.com page.</summary>
    private static (string, string) UpdateFallback()
    {
        var url = $"https://github.com/{Updater.Repository}/releases/latest";
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SketchProMax-field-test");
        var (tag, why) = ReleaseSource.ApiThenWebAsync<string>(
            _ => throw new HttpRequestException("refused by the field test"),
            async ct =>
            {
                using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
                return ReleaseSource.TagFromRedirect(response.Headers.Location?.ToString());
            },
            ReleaseSource.ApiTimeout, CancellationToken.None).GetAwaiter().GetResult();
        if (why is null) return Fail("the failing API call was used instead of falling back");
        return tag is not null ? Pass($"the API failed ({why}); github.com named {tag}") : Fail($"the API failed ({why}) and {url} named no release either");
    }

    /// <summary>
    /// An update stopped while unpacking continues on the next try: a small package is unpacked in a temp folder, one file
    /// is taken away as if the unpack had stopped there, and unpacking again keeps the rest and puts that one back.
    /// </summary>
    private static (string, string) UpdateResumeProbe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SketchProMax-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, "SketchProMax-0.0.1.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var name in new[] { "a.dll", "b.dll", "c.dll" })
                    using (var writer = new StreamWriter(archive.CreateEntry("pkg/dist/Revit" + App.RevitYear + "/" + name).Open()))
                        writer.Write(name);
            var sha = UpdateResume.Sha256Of(zip);
            var folder = Path.Combine(dir, "0.0.1.partial");
            UpdateResume.Extract(zip, folder, sha);
            var missing = Path.Combine(folder, "pkg", "dist", "Revit" + App.RevitYear, "b.dll");
            File.Delete(missing);
            var kept = UpdateResume.Extract(zip, folder, sha);
            if (kept != 2 || !File.Exists(missing)) return Fail($"unpacking again kept {kept} of 2 files and {(File.Exists(missing) ? "put back" : "did not put back")} the missing one");
            UpdateResume.MarkComplete(folder, "0.0.1");
            return UpdateResume.IsComplete(folder)
                ? Pass($"unpacking again kept the 2 files already written and added the missing one, in {Path.GetTempPath()}")
                : Fail("the finished version could not be marked as unpacked");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// The field test's breadcrumbs, in a folder of the test folder: a run stopped as if Revit closed during a step is found
    /// as left behind, its interrupted rows end with the crash row, Continue carries what ran and skips the crashed step.
    /// </summary>
    private static (string, string) JournalProbe(string testFolder)
    {
        var folder = Path.Combine(testFolder, "journal");
        var problem = FieldTestJournal.SelfCheck(folder);
        return problem is null
            ? Pass($"a journal stopped as if Revit closed during purge_unused was found as left behind, its report rows end with \"{FieldTestJournal.CrashMessage}\", " +
                   "Continue carries the step that ended and skips the crashed one, the crash is remembered until it runs normally, and a finished run leaves no journal")
            : Fail(problem);
    }

    /// <summary>
    /// The running-call file (bug #37): one left by a Revit that is gone is read back, at the next start, as which call
    /// Revit closed during, with its arguments, and removed. In a folder of the test folder, with a process id that isn't running.
    /// </summary>
    private static (string, string) RunningCallProbe(string testFolder)
    {
        var folder = Path.Combine(testFolder, "running-call");
        var saved = Revit.RunningCall.FolderOverride;
        Revit.RunningCall.FolderOverride = folder;
        try
        {
            Directory.CreateDirectory(folder);
            const int gone = 999_999_003;
            var file = Revit.RunningCall.FileOf(gone);
            File.WriteAllText(file, JsonSerializer.Serialize(new Revit.RunningCall.Entry
            {
                Time = "2099-01-02T03:04:05", Where = "the chat", Tool = "run_revit_query_code", Model = "Field test", Version = "0.0.0", Arguments = "{\"code\":\"return 1;\"}",
            }));
            var lines = Revit.RunningCall.Recover(1, _ => false);
            var line = lines.FirstOrDefault() ?? "";
            if (lines.Count != 1 || !line.Contains("run_revit_query_code") || !line.Contains("return 1;")) return Fail("the call left behind wasn't read back with its arguments: " + string.Join(" | ", lines));
            if (File.Exists(file)) return Fail("the call left behind was read back but not removed");
            return Revit.RunningCall.Recover(1, _ => false).Count == 0
                ? Pass("a call left behind by a Revit that is gone was read back at the next start with its tool and arguments, then removed: " + Short(line, 160))
                : Fail("the call was reported twice");
        }
        finally { Revit.RunningCall.FolderOverride = saved; }
    }

    // ---- The report's Bug log section ----------------------------------------------------------------------------------------

    private static string BugLogSection(RunReport r)
    {
        var rows = r.Rows.Select(x => (x.Tool, x.Status)).ToList();
        return FieldTestCoverage.BugLogMarkdown(FieldTestCoverage.FixedBugs(UI.TestChecklistWindow.BugLogText()), rows);
    }

    /// <summary>The same as JSON: row, what, checks, result.</summary>
    private static object BugLogJson(RunReport r)
    {
        var rows = r.Rows.Select(x => (x.Tool, x.Status)).ToList();
        return FieldTestCoverage.FixedBugs(UI.TestChecklistWindow.BugLogText()).Select(b => new
        {
            row = b.Number,
            what = b.What,
            status = b.Status,
            checks = FieldTestCoverage.BugChecks.FirstOrDefault(c => c.Row == b.Number) is { } c
                ? c.Refs.Select(x => new { check = x, result = FieldTestCoverage.RefStatus(x, rows) })
                : null,
        }).ToList();
    }
}
