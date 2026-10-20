using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;

namespace SketchProMax.Tools;

/// <summary>
/// Plumbing that lets the field test run the self-test's cases (same list, same setup steps, same per-step rollback)
/// against the throwaway project it opened, instead of the model the user had open. No case content lives here.
/// </summary>
internal static partial class SelfTest
{
    /// <param name="Bug">The bug log row the case guards (0: none).</param>
    internal sealed record FieldStep(int Index, string Tool, bool Heavy, int Bug = 0);

    internal interface IFieldSession
    {
        IReadOnlyList<FieldStep> Steps { get; }

        /// <summary>One case, on Revit's thread. Status "stop" means the active model is no longer the test project.</summary>
        Outcome RunCase(UIApplication app, FieldStep step);

        /// <summary>Deletes the recipes and learned tool the cases save on this PC.</summary>
        void Cleanup();
    }

    /// <summary>Reads the inputs from <paramref name="doc"/>, which must be the active document while cases run.</summary>
    internal static IFieldSession OpenFieldSession(UIApplication app, Document doc, ToolRegistry registry, string temp)
    {
        Directory.CreateDirectory(temp);
        var ctx = Collect(app, doc, registry, temp);
        ctx.Throwaway = true;
        LearnedTools.MarkApproved(LearnedToolCode);
        var run = new Run
        {
            Report = new Report
            {
                Model = doc.Title,
                RevitVersion = app.Application.VersionNumber,
                AddinVersion = typeof(SelfTest).Assembly.GetName().Version?.ToString() ?? "",
                Mode = "field",
            },
            Ctx = ctx, Mode = Mode.Full, Big = false, Steps = AllCases().ToList(),
        };
        return new FieldSession(run, registry);
    }

    private sealed class FieldSession(Run run, ToolRegistry registry) : IFieldSession
    {
        public IReadOnlyList<FieldStep> Steps { get; } = run.Steps.Select((c, i) => new FieldStep(i, c.Tool, c.Heavy, c.Bug)).ToList();

        public Outcome RunCase(UIApplication app, FieldStep step) => RunStep(app, run, registry, run.Steps[step.Index]);

        public void Cleanup()
        {
            try { System.IO.File.Delete(Recipes.PathFor("sketch-promax-self-test")); } catch (Exception) { }
            try { System.IO.File.Delete(Recipes.PathFor("sketch-promax-self-test-typed")); } catch (Exception) { }
            try { System.IO.File.Delete(LearnedTools.PathFor("self_test_count_walls")); } catch (Exception) { }
        }
    }
}

/// <summary>
/// The field test: builds a throwaway project (levels, grids, concrete and steel columns and beams, walls, a floor with a
/// slab edge, foundations, an architectural link, views, a schedule and a sheet), opens it in its own window, runs every
/// self-test case against it, and writes one report grouped by tool family. The models the user had open are never
/// edited; Revit only switches back to one of them at the end so the test project can be closed.
/// </summary>
internal static partial class FieldTest
{
    internal sealed class Options
    {
        public HashSet<string>? Families;
        public bool IncludeHeavy = true;
        public bool KeepProject;
        public bool Workshared = true;
        /// <summary>Also send one short chat message through Claude Code (it uses the user's Claude plan).</summary>
        public bool ChatRoundTrip;
        public string? ReportFolder;
        public int ToolCapSeconds = 120;
        /// <summary>What the run is called in its report and file name ("test before sharing" for Test before sharing).</summary>
        public string Title = "field test";
    }

    internal sealed record Row(string Family, string Tool, string Status, long Ms, string Message);

    internal sealed class RunReport
    {
        public string RevitVersion = "", RevitBuild = "", RevitName = "", AddinVersion = "";
        public string Title = "field test";
        public DateTime Started = DateTime.Now;
        public TimeSpan Took;
        public string Template = "";
        public bool Workshared;
        public List<string> Sample = [];
        public List<Row> Rows = [];
        public string? StoppedBecause;
        public string? Fatal;
        public string? ProjectPath;
        public string? StillOpen;
        public string? ReportPath, JsonPath;
        public HashSet<string>? Families;
        /// <summary>The registry's tools and the tools with a self-test case, for the coverage section.</summary>
        public List<string> Tools = [];
        public HashSet<string> CaseTools = [];
        /// <summary>What the time-tracking probe counted in the test project, for the report's "Time saved" section.</summary>
        public string? TimeSaved;

        public int Count(string status) => Rows.Count(r => r.Status == status);

        public string Summary()
        {
            var sb = new StringBuilder();
            if (Fatal is not null) sb.Append($"The field test could not run: {Fatal}\n");
            sb.Append($"{char.ToUpperInvariant(Title[0])}{Title.Substring(1)} in Revit {RevitVersion} ({RevitBuild}), Sketch ProMax {AddinVersion}: {Count("pass")} passed, " +
                      $"{Count("fail")} failed, {Count("manual")} manual (checklist), {Count("skip")} not run in {Jobs.Duration(Took)}.");
            if (StoppedBecause is not null) sb.Append($" Stopped early: {StoppedBecause}.");
            foreach (var g in Groups.Where(g => Rows.Any(r => r.Family == g.Key)))
            {
                var rows = Rows.Where(r => r.Family == g.Key).ToList();
                sb.Append($"\n{g.Title}: {rows.Count(r => r.Status == "pass")} pass, {rows.Count(r => r.Status == "fail")} fail" +
                          (rows.Any(r => r.Status == "manual") ? $", {rows.Count(r => r.Status == "manual")} manual" : "") +
                          (rows.Any(r => r.Status == "skip") ? $", {rows.Count(r => r.Status == "skip")} not run" : ""));
            }
            var failures = Rows.Where(r => r.Status == "fail").ToList();
            foreach (var f in failures.Take(15)) sb.Append($"\n• {f.Tool}: {Short(f.Message, 200)}");
            if (failures.Count > 15) sb.Append($"\n• … and {failures.Count - 15} more in the report");
            if (StillOpen is not null) sb.Append($"\n{StillOpen}");
            return sb.ToString();
        }
    }

    /// <summary>The families the report groups tools into, in report order.</summary>
    internal static readonly (string Key, string Title, string[] Aliases)[] Groups =
    [
        ("steel", "Steel", ["steelwork", "steelconnections"]),
        ("rebar", "Rebar", ["reinforcement", "reinforcing", "bars"]),
        ("precast_pt", "Precast / post-tensioning", ["precast", "pt", "posttensioning", "tendons", "hollowcore"]),
        ("clashes", "Clashes and openings", ["clash", "clashes", "openings", "mep"]),
        ("arch_to_structure", "Arch to structure", ["arch", "architecture", "archtostructure"]),
        ("marine", "Marine / infrastructure", ["infrastructure", "port", "ports", "marineinfrastructure"]),
        ("checkers", "Checkers / spec check", ["checker", "check", "checks", "speccheck", "spec", "qa", "checkersspeccheck"]),
        ("boq", "BOQ / quantities", ["quantities", "quantity", "boqquantities", "cost", "costs", "carbon", "embodiedcarbon"]),
        ("ve", "Value engineering", ["valueengineering", "vengineering"]),
        ("analytical", "Analytical export", ["analysis", "analyticalexport"]),
        ("views_sheets", "Views, sheets and annotation", ["views", "sheets", "annotation", "documentation", "drawings", "viewssheets", "viewssheetsannotation", "viewssheetsandannotation"]),
        ("worksharing", "Worksharing", ["worksets", "workset"]),
        ("other", "Everything else", ["everythingelse", "rest", "modelling", "modeling", "general"]),
        (SharingFamily, "Checklist and Share tools (test before sharing)", ["checklist", "share", "sharetools", "tritorevit", "beforesharing", "testbeforesharing"]),
        ("features", "Non-tool features", ["feature", "nontool", "nontoolfeatures", "ribbon", "chat", "updates", "bugs"]),
    ];

    private static readonly Dictionary<string, string[]> Explicit = new()
    {
        ["steel"] = ["load_steel_sections", "place_bracing", "list_steel_connection_types", "create_steel_connections", "export_steel_takeoff"],
        ["rebar"] = ["export_bar_schedule"],
        ["precast_pt"] = ["export_precast_schedule", "place_pt_tendons", "export_tendon_schedule", "hollow_core_layout"],
        ["clashes"] = ["find_clashes", "create_mep_openings", "sleeve_schedule", "place_void_cuts", "verify_void_cut", "cut_openings_in_joined", "export_bcf", "coordination_progress"],
        ["arch_to_structure"] = ["arch_to_structure", "create_ramp", "equipment_pads", "create_floors_from_faces", "copy_monitor", "link_model"],
        ["marine"] = ["create_piles", "marine_structure", "port_furniture", "design_vessel", "vessel_collision_check", "progressive_collapse_check", "create_toposolid", "create_raft"],
        ["checkers"] = ["check_drawings", "check_code_rules", "check_model", "find_missing_items", "run_model_audit", "model_snapshot", "check_schedule", "spec_check", "issue_check"],
        ["boq"] = ["boq_check", "boq_item_breakdown", "cost_estimate", "export_quantities", "work_progress", "construction_sequence", "embodied_carbon", "carbon_compare", "carbon_view"],
        ["ve"] = ["ve_opportunities", "ve_compare_options", "ve_register", "ve_view", "ve_apply", "what_if"],
        ["analytical"] = ["analytical_check", "export_analytical", "loading_plan"],
        ["worksharing"] = ["list_worksets", "make_worksets_editable", "borrow_elements", "element_owners", "relinquish_mine", "manage_worksets"],
        ["views_sheets"] = ["export_pdf", "export_drawing_list", "issue_drawings", "general_notes", "typical_details", "cloud_elements", "collect_text", "apply_text",
            "color_by_parameter", "capture_view_image", "add_spot_elevations", "arrange_tags", "reflow_annotations", "trim_matchlines", "create_elevations",
            "create_callout", "create_section", "export_files", "rename_elements", "place_detail_components", "create_drafting_view", "import_image"],
    };

    private static readonly string[] ViewWords =
        ["view", "sheet", "title_block", "dimension", "tag", "annotation", "revision", "text_note", "schedule", "legend", "detail", "drafting"];

    internal static string FamilyOf(string tool)
    {
        foreach (var pair in Explicit)
            if (pair.Value.Contains(tool)) return pair.Key;
        if (tool.StartsWith("reinforce_", StringComparison.Ordinal) || tool.StartsWith("rebar_", StringComparison.Ordinal)) return "rebar";
        if (ViewWords.Any(w => tool.Contains(w))) return "views_sheets";
        return "other";
    }

    /// <summary>Turns user-typed family names into keys; throws ArgumentException naming the valid ones.</summary>
    internal static HashSet<string>? ParseFamilies(IEnumerable<string> names)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var keys = new HashSet<string>();
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var n = Norm(name);
            var hit = Groups.FirstOrDefault(g => Norm(g.Key) == n || Norm(g.Title) == n || g.Aliases.Contains(n));
            if (hit.Key is null)
                throw new ArgumentException($"Unknown family '{name}'. Use any of: {string.Join(", ", Groups.Select(g => g.Key))}.");
            keys.Add(hit.Key);
        }
        return keys.Count == 0 ? null : keys;
    }

    /// <summary>A tool with no self-test case, no field-test step and no ManualTools row: a gap the unit tests also catch.</summary>
    private const string NoCase = "no field-test coverage: add a self-test case (SelfTest.cs) or a ManualTools row (FieldTestCoverage.cs)";

    internal const string Estimate = "about 5 to 15 minutes";

    /// <summary>Everything one run carries from one Revit API call to the next.</summary>
    private sealed class State(Job job, ToolRegistry registry, Options o)
    {
        public readonly Job Job = job;
        public readonly ToolRegistry Registry = registry;
        public readonly Options O = o;
        public readonly RunReport Result = new() { Families = o.Families, Title = o.Title };
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public string Folder = "";
        public Document? Original, Built, Test;
        public string? LinkPath, ProjectFile, NoView;
        /// <summary>Two closed copies of the test project for batch_run_recipe (and ve_apply on a copy).</summary>
        public string? BatchFolder;
        public Library? Lib;
        public FamilyTemplates? Templates;
        public SelfTest.IFieldSession? Session;
        /// <summary>The brief brief.plan made, for brief.choose.</summary>
        public Chat.PromptBrief? Brief;
        public bool KeepFolder = o.KeepProject;
    }

    /// <summary>
    /// Starts the field test for <paramref name="job"/>, which must already be <see cref="Jobs.Running"/>, and returns at
    /// once. Each building step and each tool is its own Revit API call queued from a background task, so Revit redraws
    /// and answers clicks in between, and cancel_job, the pane's Cancel and the ribbon's Cancel job stop it before the
    /// next tool (one tool call that is already running still runs to its end). Steps that create, open, switch or close
    /// documents are queued with <see cref="Revit.RevitDispatcher.InvokeFromEventAsync{T}"/>, since the API refuses
    /// those inside the Idling event. Whatever happens, the returned task completes with the job's result, and the job
    /// is marked finished and Jobs.Running cleared first; <paramref name="onDone"/> then runs on Revit's thread.
    /// </summary>
    internal static Task<ToolResult> Start(Job job, ToolRegistry registry, Options o, Action<RunReport?, ToolResult>? onDone = null)
    {
        var done = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            RunReport? report = null;
            ToolResult? final = null;
            // The test project's open is timed like any other; its first-Idling time includes the test's own steps.
            SketchProMax.Revit.StartupTimings.TestRunning = true;
            try
            {
                report = await RunSteps(new State(job, registry, o)).ConfigureAwait(false);
                final = ToToolResult(report, o);
            }
            catch (Exception ex)
            {
                final = ToolResult.Error($"The field test stopped unexpectedly ({ex.GetType().Name}: {ex.Message}). The test project may still be open: close it without saving.");
            }
            finally
            {
                SketchProMax.Revit.StartupTimings.TestRunning = false;
                job.Finished = DateTime.UtcNow;
                if (Jobs.Running == job) Jobs.Running = null;
                done.TrySetResult(final ?? ToolResult.Error("The field test stopped unexpectedly. The test project may still be open: close it without saving."));
            }
            if (onDone is not null)
            {
                try { await App.Dispatcher.InvokeAsync(_ => { onDone(report, final!); return true; }).ConfigureAwait(false); }
                catch (Exception) { /* the result is still with the job (wait_for_job, the Job button) */ }
            }
        });
        return done.Task;
    }

    /// <summary>One step that creates, opens, switches or closes documents: only from the external event.</summary>
    private static Task<bool> OnRevit(Action<UIApplication> body) =>
        App.Dispatcher.InvokeFromEventAsync(app => { body(app); return true; });

    private static async Task<RunReport> RunSteps(State s)
    {
        var result = s.Result;
        bool Cancelled()
        {
            if (!s.Job.CancelRequested) return false;
            result.StoppedBecause ??= "stopped on request (cancel_job or Cancel in Revit)";
            return true;
        }
        try
        {
            await OnRevit(app =>
            {
                var a = app.Application;
                result.RevitVersion = a.VersionNumber;
                result.RevitBuild = a.VersionBuild;
                result.RevitName = a.VersionName;
                result.AddinVersion = $"{Update.Updater.CurrentText} ({typeof(FieldTest).Assembly.GetName().Version})";
                s.Original = app.ActiveUIDocument?.Document;
                s.Folder = Path.Combine(Path.GetTempPath(), $"SketchProMax-field-test-{DateTime.Now:yyyyMMdd-HHmmss}");
                Directory.CreateDirectory(s.Folder);
                Jobs.Report("Field test: building the architectural link model");
                s.Lib = new Library(a);
                s.Templates = new FamilyTemplates(a);
                s.LinkPath = BuildArchLink(a, s, result);
            }).ConfigureAwait(false);
            if (!Cancelled())
                await OnRevit(app =>
                {
                    Jobs.Report("Field test: building the test project");
                    s.Built = NewProject(app.Application, result);
                    Build(app.Application, s.Built, s, result);
                }).ConfigureAwait(false);
            if (!Cancelled())
                await OnRevit(app =>
                {
                    Jobs.Report("Field test: saving the test project");
                    s.ProjectFile = SaveForTest(s.Built!, s.Folder, s.O.Workshared, result);
                    result.ProjectPath = s.ProjectFile;
                    MakeCopies(s, result);
                }).ConfigureAwait(false);
            if (!Cancelled())
                await OnRevit(app => OpenTest(app, s)).ConfigureAwait(false);
            if (!Cancelled())
                await RunCases(s).ConfigureAwait(false);
            if (!Cancelled())
                await RunExtras(s).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Fatal = $"{ex.GetType().Name}: {ex.Message}";
        }

        try { await OnRevit(app => Finish(app, s)).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Revit could not run the clean-up (Sketch ProMax was stopped or updated meanwhile): still write the report.
            result.Fatal ??= $"clean-up did not run ({ex.GetType().Name}: {ex.Message})";
            result.StillOpen ??= "The test project may still be open: close it without saving." +
                                 (string.IsNullOrEmpty(s.Folder) ? "" : $" Its files are in {s.Folder}.");
            if (result.ReportPath is null)
            {
                result.Took = s.Watch.Elapsed;
                WriteReport(result, s.O.ReportFolder);
            }
        }
        return result;
    }

    /// <summary>Opens the saved test project in its own window and makes it the active model.</summary>
    private static void OpenTest(UIApplication app, State s)
    {
        Jobs.Report("Field test: opening the test project");
        try
        {
            s.Test = app.OpenAndActivateDocument(s.ProjectFile!)?.Document;
            if (s.Test is null || app.ActiveUIDocument?.Document.Equals(s.Test) != true)
                s.NoView = "Revit did not make the test project the active model";
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            s.NoView = $"Revit could not open the test project in a window ({ex.Message})";
        }
        // Revit may have opened the file before it failed to activate it: find it, so it is still closed at the end.
        s.Test ??= app.Application.Documents.Cast<Document>().FirstOrDefault(d => !d.IsFamilyDocument && SamePath(d.PathName, s.ProjectFile));
        if (s.NoView is null) s.Session = SelfTest.OpenFieldSession(app, s.Test!, s.Registry, Path.Combine(s.Folder, "output"));
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record StepRun(SelfTest.Outcome Outcome, bool CapHit, long Ms, bool LeftOpen);

    /// <summary>One case, on Revit's thread, under its own time cap.</summary>
    private static StepRun RunOne(UIApplication app, State s, SelfTest.FieldStep step)
    {
        var limit = new StepLimit();
        s.Job.Limit = limit;
        var watch = Stopwatch.StartNew();
        // Tools that check for cancel stop at the cap. The cap belongs to this step only: a timer that fires after the step
        // has ended changes nothing, and a cancel_job sent meanwhile stays set and stops the run.
        using var cap = new Timer(_ => limit.TryHit(), null, s.O.ToolCapSeconds * 1000, Timeout.Infinite);
        SelfTest.Outcome outcome;
        try { outcome = s.Session!.RunCase(app, step); }
        catch (Exception ex) { outcome = new(step.Tool, "fail", watch.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            limit.Finish();
            s.Job.Limit = null;
        }
        var leftOpen = s.Test is { IsValidObject: true } test && test.IsModifiable;
        return new(outcome, limit.Hit, watch.ElapsedMilliseconds, leftOpen);
    }

    private static bool Wanted(Options o, string tool) => o.Families is null || o.Families.Contains(FamilyOf(tool));

    private static async Task RunCases(State s)
    {
        var o = s.O;
        var result = s.Result;
        var toRun = (s.Session?.Steps ?? []).Where(st => Wanted(o, st.Tool)).ToList();
        var counts = new Dictionary<string, int>();
        var done = 0;
        foreach (var step in toRun)
        {
            done++;
            var family = FamilyOf(step.Tool);
            counts[step.Tool] = counts.TryGetValue(step.Tool, out var n) ? n + 1 : 1;
            var label = counts[step.Tool] == 1 ? step.Tool : $"{step.Tool} #{counts[step.Tool]}";
            // A case that guards a bug log row says so, for the report's Bug log section.
            if (step.Bug > 0) label += $" {FieldTestCoverage.BugTag(step.Bug)}";
            if (result.StoppedBecause is null && s.Job.CancelRequested) result.StoppedBecause = "stopped on request (cancel_job or Cancel in Revit)";
            if (result.StoppedBecause is not null) { result.Rows.Add(new(family, label, "skip", 0, "not reached: " + result.StoppedBecause)); continue; }
            if (step.Heavy && !o.IncludeHeavy) { result.Rows.Add(new(family, label, "skip", 0, "slow tool; run with include_heavy to test it")); continue; }

            Jobs.Report($"Field test {done} of {toRun.Count}: {label} ({Title(family)})");
            StepRun run;
            try { run = await App.Dispatcher.InvokeAsync(app => RunOne(app, s, step)).ConfigureAwait(false); }
            catch (Exception ex) { run = new(new(step.Tool, "fail", 0, $"{ex.GetType().Name}: {ex.Message}"), false, 0, false); }
            var outcome = run.Outcome;

            var message = outcome.Message;
            if (run.CapHit)
                message = $"hit the {o.ToolCapSeconds} s cap after {run.Ms / 1000} s" +
                          (outcome.Status == "pass" ? " (it cannot be stopped part-way, so it ran to the end)" : "") + ". " + message;
            if (outcome.Status == "stop")
            {
                result.StoppedBecause = outcome.Message;
                result.Rows.Add(new(family, label, "skip", 0, "not reached: " + outcome.Message));
                continue;
            }
            // Skipped for want of something the API can't make (a legend component): the checklist covers it.
            var status = outcome.Status;
            if (status == "skip" && FieldTestCoverage.ManualWhenSkipped.TryGetValue(step.Tool, out var feature)
                && FieldTestCoverage.Features.FirstOrDefault(f => f.Key == feature) is { } manual)
            {
                status = "manual";
                message = $"manual (checklist: \"{manual.Title}\"): {message}";
            }
            result.Rows.Add(new(family, label, status, outcome.Ms, message));

            // A tool that leaves a transaction open would block every tool after it (and switching away at the end).
            if (run.LeftOpen)
                result.StoppedBecause = $"{step.Tool} left a transaction open in the test project";

            // Give Revit room to redraw and handle the user between tools.
            await Task.Delay(150).ConfigureAwait(false);
        }
    }

    /// <summary>Rows for the tools that did not run: no case, not reached, or nothing to run them in.</summary>
    private static void AddUncovered(State s)
    {
        var result = s.Result;
        var hasRow = new HashSet<string>(result.Rows.Select(r => r.Tool.Split(' ')[0]));
        var covered = new HashSet<string>((s.Session?.Steps ?? []).Select(st => st.Tool));
        foreach (var tool in s.Registry.All.Where(t => Wanted(s.O, t.Name)).OrderBy(t => t.Name))
        {
            if (hasRow.Contains(tool.Name)) continue;
            var family = FamilyOf(tool.Name);
            if (FieldTestCoverage.ManualTools.TryGetValue(tool.Name, out var manual))
            {
                result.Rows.Add(new(family, tool.Name, "manual", 0, "manual (checklist): " + manual.Reason));
                continue;
            }
            if (!covered.Contains(tool.Name) && !FieldTestCoverage.FieldSteps.Contains(tool.Name))
            {
                result.Rows.Add(new(family, tool.Name, "fail", 0, NoCase));
                continue;
            }
            var why = s.NoView is not null ? $"needs an open view: {s.NoView}"
                : s.Session is null ? result.StoppedBecause is { } stop ? "not reached: " + stop : "the test project could not be built (see the error above)"
                : "not reached: " + (result.StoppedBecause ?? result.Fatal ?? "the run ended early");
            result.Rows.Add(new(family, tool.Name, "skip", 0, why));
        }
    }

    /// <summary>The last step, from the external event: list what didn't run, close the test project, write the report.</summary>
    private static void Finish(UIApplication app, State s)
    {
        var result = s.Result;
        Jobs.Report("Field test: closing the test project and writing the report");
        AddUncovered(s);
        result.Tools = s.Registry.All.Select(t => t.Name).ToList();
        result.CaseTools = new HashSet<string>((s.Session?.Steps ?? []).Select(st => st.Tool));
        try { s.Session?.Cleanup(); } catch (Exception) { }
        // The project being built is only open in the background; if building failed part-way, close it here.
        try { if (s.Built is { IsValidObject: true } built && (s.Test is null || !built.Equals(s.Test))) built.Close(false); }
        catch (Exception) { }
        // The time probes' rows for the test project on this PC: the project is thrown away, so are they.
        try { if (s.Test is { IsValidObject: true } tested) SketchProMax.Revit.TimeLog.ForgetForTest(tested); } catch (Exception) { }
        // The cost probes' remembered rate table and add-ons for the test project live in %APPDATA%: they go with it too.
        try { if (s.Test is { IsValidObject: true } costed && Directory.Exists(CostRun.Store(costed))) Directory.Delete(CostRun.Store(costed), true); } catch (Exception) { }
        CloseTest(app, s.Test, s.Original, result, ref s.KeepFolder);
        if (!s.KeepFolder && !string.IsNullOrEmpty(s.Folder))
        {
            TryDeleteFolder(s.Folder);
            result.ProjectPath = null;
        }
        if (s.O.KeepProject && result.ProjectPath is not null) result.Sample.Add($"Kept the test project: {result.ProjectPath}");
        result.Took = s.Watch.Elapsed;
        WriteReport(result, s.O.ReportFolder);
    }

    /// <summary>The job's result for Claude (and the Job button): counts, summary, report paths.</summary>
    internal static ToolResult ToToolResult(RunReport result, Options o)
    {
        var payload = new
        {
            summary = result.Summary(),
            passed = result.Count("pass"), failed = result.Count("fail"), manual = result.Count("manual"), skipped = result.Count("skip"),
            report = result.ReportPath, report_json = result.JsonPath,
            kept_project = o.KeepProject ? result.ProjectPath : null,
            still_open = result.StillOpen,
            next = result.ReportPath is null ? null : "Read the report for every tool's detail; fix failures family by family.",
        };
        return result.Fatal is not null && result.Rows.Count(r => r.Status != "skip") == 0
            ? ToolResult.Error(JsonSerializer.Serialize(payload))
            : ToolResult.Ok(payload);
    }

    private static string Title(string family) => Groups.FirstOrDefault(g => g.Key == family).Title ?? family;

    // ---- Building the test project ----------------------------------------------------------------------------------

    private const double Mm = 1 / 304.8;
    private static XYZ P(double xMm, double yMm, double zFt = 0) => new(xMm * Mm, yMm * Mm, zFt);

    /// <summary>Dismisses warnings and rolls back on errors, so building never shows a Revit dialog.</summary>
    private sealed class Quiet : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var error = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
                else error = true;
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }

    /// <summary>One building step in its own transaction; a failure is noted and the rest carries on.</summary>
    private static T? Step<T>(Document doc, RunReport result, string what, Func<T?> body) where T : class
    {
        using var t = new Transaction(doc, "Sketch ProMax field test: " + what);
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new Quiet());
        options.SetClearAfterRollback(true);
        options.SetForcedModalHandling(false);
        t.SetFailureHandlingOptions(options);
        try
        {
            t.Start();
            var value = body();
            if (t.Commit() == TransactionStatus.Committed) return value;
            result.Sample.Add($"{what}: Revit refused it");
            return null;
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            result.Sample.Add($"{what}: {ex.Message}");
            return null;
        }
    }

    private static Document NewProject(Autodesk.Revit.ApplicationServices.Application a, RunReport result)
    {
        var template = a.DefaultProjectTemplate;
        if (!string.IsNullOrEmpty(template) && File.Exists(template))
        {
            try
            {
                var doc = a.NewProjectDocument(template);
                result.Template = template;
                return doc;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                result.Sample.Add($"default template {template} did not open ({ex.Message}); used Revit's metric defaults");
            }
        }
        result.Template = "none (Revit's built-in metric project)";
        return a.NewProjectDocument(UnitSystem.Metric);
    }

    private sealed class Shell
    {
        public Level Base = null!;
        public Level Upper = null!;
        public ViewPlan? Plan;
        public Wall? Wall;
    }

    /// <summary>Levels at 0 / 4 / 8 m, floor plans, four walls round a 14 m square, a door, an opening and a room.</summary>
    private static Shell? BuildShell(Document doc, RunReport result, string who)
    {
        var shell = new Shell();
        var ok = Step(doc, result, $"{who}: levels", () =>
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            Level At(double mm) => levels.FirstOrDefault(l => Math.Abs(l.Elevation - mm * Mm) < 0.01) ?? AddLevel(mm);
            Level AddLevel(double mm) { var l = Level.Create(doc, mm * Mm); levels.Add(l); return l; }
            shell.Base = At(0);
            shell.Upper = At(4000);
            At(8000);
            return shell;
        });
        if (ok is null) return null;

        Step(doc, result, $"{who}: floor plans", () =>
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan) ?? throw new InvalidOperationException("no floor plan view type");
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan).ToList();
            foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                if (!plans.Any(p => p.GenLevel?.Id == level.Id)) plans.Add(ViewPlan.Create(doc, type.Id, level.Id));
            shell.Plan = plans.FirstOrDefault(p => p.GenLevel?.Id == shell.Base.Id);
            return shell;
        });

        var walls = Step(doc, result, $"{who}: walls", () =>
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(w => w.Kind == WallKind.Basic)
                           .OrderBy(w => w.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).FirstOrDefault()
                       ?? throw new InvalidOperationException("no basic wall type");
            var corners = new[] { P(-1000, -1000), P(13000, -1000), P(13000, 13000), P(-1000, 13000) };
            var made = new List<Wall>();
            for (var i = 0; i < 4; i++)
                made.Add(Wall.Create(doc, Line.CreateBound(corners[i], corners[(i + 1) % 4]), type.Id, shell.Base.Id, 4000 * Mm, 0, false, true));
            shell.Wall = made[0];
            return made;
        });
        if (walls is null) return shell;

        Step(doc, result, $"{who}: door and opening", () =>
        {
            var door = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Doors).Cast<FamilySymbol>().FirstOrDefault();
            if (door is not null)
            {
                if (!door.IsActive) door.Activate();
                doc.Create.NewFamilyInstance(P(6000, -1000), door, shell.Wall!, shell.Base, StructuralType.NonStructural);
            }
            else result.Sample.Add($"{who}: no door family in the template, so no door");
            doc.Create.NewOpening(shell.Wall!, P(9000, -1000, 1000 * Mm), P(10000, -1000, 2000 * Mm));
            return shell;
        });

        Step(doc, result, $"{who}: room", () => doc.Create.NewRoom(shell.Base, new UV(6000 * Mm, 6000 * Mm)));
        return shell;
    }

    private static string? BuildArchLink(Autodesk.Revit.ApplicationServices.Application a, State s, RunReport result)
    {
        Document? doc = null;
        try
        {
            doc = NewProject(a, new RunReport());
            var shell = BuildShell(doc, result, "link");
            if (shell is not null)
            {
                // A structural floor arch_to_structure must convert, and a thin finish floor it must skip.
                Step(doc, result, "link: structural floor", () =>
                {
                    var floor = Floor.Create(doc, [Rectangle(-1000, -1000, 13000, 13000)], FloorTypeId(doc), shell.Upper.Id);
                    floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(1);
                    return floor;
                });
                Step(doc, result, "link: finish floor", () => Floor.Create(doc, [Rectangle(1000, 1000, 5000, 5000)], FinishFloorType(doc).Id, shell.Base.Id));
                BuildLinkColumns(a, doc, s, shell, result);
            }
            var path = Path.Combine(s.Folder, "FieldTest-Arch.rvt");
            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            return path;
        }
        catch (Exception ex)
        {
            result.Sample.Add($"architectural link model: {ex.Message} (link-based tools will be skipped)");
            return null;
        }
        finally
        {
            try { doc?.Close(false); } catch (Exception) { }
        }
    }

    private static CurveLoop Rectangle(double x0, double y0, double x1, double y1)
    {
        var loop = new CurveLoop();
        var c = new[] { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) };
        for (var i = 0; i < 4; i++) loop.Append(Line.CreateBound(c[i], c[(i + 1) % 4]));
        return loop;
    }

    private static ElementId FloorTypeId(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(f => !f.IsFoundationSlab)
            .OrderBy(f => f.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).FirstOrDefault()?.Id
        ?? throw new InvalidOperationException("no floor type");

    private static void Build(Autodesk.Revit.ApplicationServices.Application a, Document doc, State s, RunReport result)
    {
        var shell = BuildShell(doc, result, "project") ?? throw new InvalidOperationException("could not create levels in the test project");
        var lib = s.Lib ??= new Library(a);
        var linkPath = s.LinkPath;

        Step(doc, result, "grids", () =>
        {
            string[] letters = ["A", "B", "C"], numbers = ["1", "2", "3"];
            for (var i = 0; i < 3; i++)
            {
                var v = Grid.Create(doc, Line.CreateBound(P(i * 6000, -3000), P(i * 6000, 15000)));
                var h = Grid.Create(doc, Line.CreateBound(P(-3000, i * 6000), P(15000, i * 6000)));
                try { v.Name = letters[i]; h.Name = numbers[i]; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            }
            return doc;
        });

        // Columns: concrete along grid 1 and 2, steel along grid 3.
        var concreteColumn = Symbol(doc, result, lib, BuiltInCategory.OST_StructuralColumns, StructuralMaterialType.Concrete, "concrete column");
        var steelColumn = Symbol(doc, result, lib, BuiltInCategory.OST_StructuralColumns, StructuralMaterialType.Steel, "steel column");
        var concretePoints = new[] { (0.0, 0.0), (6000.0, 0.0), (12000.0, 0.0), (0.0, 6000.0), (12000.0, 6000.0) };
        var steelPoints = new[] { (0.0, 12000.0), (6000.0, 12000.0), (12000.0, 12000.0) };
        PlaceColumns(doc, result, shell, concreteColumn, concretePoints, "concrete columns");
        PlaceColumns(doc, result, shell, steelColumn, steelPoints, "steel columns");

        // Beams at the upper level: concrete on grid 1, steel on grid 3.
        var concreteBeam = Symbol(doc, result, lib, BuiltInCategory.OST_StructuralFraming, StructuralMaterialType.Concrete, "concrete beam",
                               orElse: "the field test makes one")
                           ?? ConcreteBeam(a, doc, s, result);
        var steelBeam = Symbol(doc, result, lib, BuiltInCategory.OST_StructuralFraming, StructuralMaterialType.Steel, "steel beam");
        var beams = new List<(FamilyInstance Beam, Line Line)>();
        beams.AddRange(PlaceBeams(doc, result, shell, concreteBeam, 0, "concrete beams"));
        beams.AddRange(PlaceBeams(doc, result, shell, steelBeam, 12000, "steel beams"));

        Step(doc, result, "analytical members", () =>
        {
            var manager = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
            foreach (var (beam, line) in beams)
            {
                var member = AnalyticalMember.Create(doc, line);
                try { manager?.AddAssociation(member.Id, beam.Id); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            }
            if (beams.Count == 0) AnalyticalMember.Create(doc, Line.CreateBound(P(0, 6000, shell.Upper.Elevation), P(12000, 6000, shell.Upper.Elevation)));
            return doc;
        });

        // Floor at the upper level, then a slab edge on one of its top edges.
        var floor = Step(doc, result, "floor", () =>
        {
            var f = Floor.Create(doc, [Rectangle(-1000, -1000, 13000, 13000)], FloorTypeId(doc), shell.Upper.Id);
            f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(1);
            return f;
        });
        if (floor is not null)
            Step(doc, result, "slab edge", () =>
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(SlabEdgeType)).FirstElement() as SlabEdgeType
                           ?? throw new InvalidOperationException("the template has no slab edge type");
                var edge = TopEdge(floor) ?? throw new InvalidOperationException("no top edge found on the floor");
                return doc.Create.NewSlabEdge(type, edge);
            });

        // Foundations: pads under the concrete columns, a pile under a steel column, a strip under the first wall.
        var footing = Symbol(doc, result, lib, BuiltInCategory.OST_StructuralFoundation, null, "pad footing", "Footing");
        if (footing is not null)
            Step(doc, result, "pad footings", () =>
            {
                if (!footing.IsActive) footing.Activate();
                foreach (var (x, y) in concretePoints.Take(3))
                    doc.Create.NewFamilyInstance(P(x, y, shell.Base.Elevation), footing, shell.Base, StructuralType.Footing);
                return doc;
            });
        var pile = Symbol(doc, result, lib, BuiltInCategory.OST_StructuralFoundation, null, "pile", "Pile");
        if (pile is not null)
            Step(doc, result, "piles", () =>
            {
                if (!pile.IsActive) pile.Activate();
                doc.Create.NewFamilyInstance(P(6000, 12000, shell.Base.Elevation), pile, shell.Base, StructuralType.Footing);
                return doc;
            });
        if (shell.Wall is not null)
            Step(doc, result, "wall foundation", () =>
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(WallFoundationType)).FirstElementId();
                if (type == ElementId.InvalidElementId) throw new InvalidOperationException("the template has no wall foundation type");
                return WallFoundation.Create(doc, type, shell.Wall.Id);
            });

        BuildViews(doc, result, shell);
        BuildExtras(a, doc, s, shell, result);

        if (linkPath is not null)
            Step(doc, result, "architectural link", () =>
            {
                var load = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(linkPath), new RevitLinkOptions(false));
                return RevitLinkInstance.Create(doc, load.ElementId);
            });

        int Count(BuiltInCategory c) => new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType().GetElementCount();
        result.Sample.Add($"built: {Count(BuiltInCategory.OST_Levels)} levels, {Count(BuiltInCategory.OST_Grids)} grids, {Count(BuiltInCategory.OST_Walls)} walls, " +
                          $"{Count(BuiltInCategory.OST_Floors)} floors, {Count(BuiltInCategory.OST_EdgeSlab)} slab edges, {Count(BuiltInCategory.OST_StructuralColumns)} columns, " +
                          $"{Count(BuiltInCategory.OST_StructuralFraming)} beams, {Count(BuiltInCategory.OST_StructuralFoundation)} foundations, {Count(BuiltInCategory.OST_Rooms)} rooms, " +
                          $"{new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount()} links, " +
                          $"{new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount()} sheets");
    }

    private static void BuildViews(Document doc, RunReport result, Shell shell)
    {
        ViewFamilyType? Vft(ViewFamily f) => new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == f);

        Step(doc, result, "section", () =>
        {
            var type = Vft(ViewFamily.Section) ?? throw new InvalidOperationException("no section view type");
            var box = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = P(-8000, -1000, -1500 * Mm),
                Max = P(8000, 10000, 3000 * Mm),
            };
            var t = Transform.Identity;
            t.Origin = P(6000, 6000);
            t.BasisX = XYZ.BasisX;
            t.BasisY = XYZ.BasisZ;
            t.BasisZ = -XYZ.BasisY;
            box.Transform = t;
            var section = ViewSection.CreateSection(doc, type.Id, box);
            try { section.Name = "FT Section"; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            return section;
        });

        Step(doc, result, "3D view", () =>
        {
            var type = Vft(ViewFamily.ThreeDimensional) ?? throw new InvalidOperationException("no 3D view type");
            var view = View3D.CreateIsometric(doc, type.Id);
            try { view.Name = "FT 3D"; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            return view;
        });

        Step(doc, result, "structural plan", () =>
        {
            var type = Vft(ViewFamily.StructuralPlan);
            return type is null ? null : ViewPlan.Create(doc, type.Id, shell.Upper.Id);
        });

        Step(doc, result, "column schedule", () =>
        {
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_StructuralColumns));
            var fields = schedule.Definition.GetSchedulableFields();
            foreach (var f in fields.Where(f => f.FieldType == ScheduleFieldType.Instance || f.FieldType == ScheduleFieldType.ElementType).Take(4))
                schedule.Definition.AddField(f);
            try { schedule.Name = "FT Column Schedule"; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            return schedule;
        });

        if (shell.Plan is { } plan)
        {
            Step(doc, result, "view template", () =>
            {
                var template = plan.CreateViewTemplate();
                try { template.Name = "FT Plan Template"; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                return template;
            });
            Step(doc, result, "sheet", () =>
            {
                var titleBlock = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElementId();
                var sheet = ViewSheet.Create(doc, titleBlock);
                try { sheet.SheetNumber = "FT-01"; sheet.Name = "Field test"; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                if (Viewport.CanAddViewToSheet(doc, sheet.Id, plan.Id)) Viewport.Create(doc, sheet.Id, plan.Id, new XYZ(1.0, 0.7, 0));
                if (titleBlock == ElementId.InvalidElementId) result.Sample.Add("sheet: the template has no title block, so the sheet has none");
                return sheet;
            });
            // Open on the ground floor plan, which is what the cases read as "the plan".
            Step(doc, result, "starting view", () =>
            {
                var settings = StartingViewSettings.GetStartingViewSettings(doc);
                if (settings.IsAcceptableStartingView(plan.Id)) settings.ViewId = plan.Id;
                return settings;
            });
        }
        else result.Sample.Add("no floor plan on the base level, so no sheet was made");
    }

    private static Reference? TopEdge(Floor floor)
    {
        var geometry = floor.get_Geometry(new Autodesk.Revit.DB.Options { ComputeReferences = true });
        foreach (var solid in geometry?.OfType<Solid>() ?? [])
            foreach (Face face in solid.Faces)
                if (face is PlanarFace { FaceNormal.Z: > 0.9 } top && top.EdgeLoops.Size > 0 && top.EdgeLoops.get_Item(0).Size > 0)
                    return top.EdgeLoops.get_Item(0).get_Item(0).Reference;
        return null;
    }

    private static void PlaceColumns(Document doc, RunReport result, Shell shell, FamilySymbol? symbol, (double X, double Y)[] points, string what)
    {
        if (symbol is null) return;
        Step(doc, result, what, () =>
        {
            if (!symbol.IsActive) symbol.Activate();
            foreach (var (x, y) in points)
            {
                var column = doc.Create.NewFamilyInstance(P(x, y, shell.Base.Elevation), symbol, shell.Base, StructuralType.Column);
                column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(shell.Upper.Id);
                column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(0.0);
            }
            return doc;
        });
        result.Sample.Add($"{what}: {points.Length} × {symbol.FamilyName} : {symbol.Name}");
    }

    private static List<(FamilyInstance, Line)> PlaceBeams(Document doc, RunReport result, Shell shell, FamilySymbol? symbol, double y, string what)
    {
        var made = new List<(FamilyInstance, Line)>();
        if (symbol is null) return made;
        var placed = Step(doc, result, what, () =>
        {
            if (!symbol.IsActive) symbol.Activate();
            var z = shell.Upper.Elevation;
            foreach (var x in new[] { 0.0, 6000.0 })
            {
                var line = Line.CreateBound(P(x, y, z), P(x + 6000, y, z));
                made.Add((doc.Create.NewFamilyInstance(line, symbol, shell.Upper, StructuralType.Beam), line));
            }
            return made;
        });
        if (placed is null) return [];
        result.Sample.Add($"{what}: {made.Count} × {symbol.FamilyName} : {symbol.Name}");
        return made;
    }

    /// <summary>A loaded type of the category and material, else one loaded from Revit's content library.</summary>
    private static FamilySymbol? Symbol(Document doc, RunReport result, Library lib, BuiltInCategory category, StructuralMaterialType? material,
        string what, string? nameWord = null, string? orElse = null)
    {
        bool Fits(FamilySymbol s) =>
            (material is null || s.Family?.StructuralMaterialType == material) &&
            (nameWord is null || s.FamilyName.IndexOf(nameWord, StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf(nameWord, StringComparison.OrdinalIgnoreCase) >= 0);
        var loaded = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>().FirstOrDefault(Fits);
        if (loaded is not null) return loaded;

        var file = lib.Find(category, material, nameWord);
        if (file is null)
        {
            result.Sample.Add(orElse is not null
                ? $"{what}: none in the template and none found in the Revit content library (searched {lib.Searched}); {orElse}."
                : $"{what}: none in the template and none found in the Revit content library (searched {lib.Searched}), so tools needing one will skip. " +
                  "Install the Revit content library (Imperial or Metric) to test them.");
            return null;
        }
        return Step(doc, result, $"load {what} ({Path.GetFileName(file)})", () =>
        {
            var catalog = Path.ChangeExtension(file, ".txt");
            if (File.Exists(catalog) && Library.FirstCatalogType(catalog) is { } typeName)
                return doc.LoadFamilySymbol(file, typeName, out FamilySymbol symbol) ? symbol : null;
            if (!doc.LoadFamily(file, out Family family)) return null;
            return family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().FirstOrDefault();
        });
    }

    /// <summary>
    /// Structural families in Revit's content library folders, listed once per run: the library paths set in Revit's
    /// options first, then every library under %ProgramData%\Autodesk\RVT {version}\Libraries (Imperial and Metric,
    /// any language), since the configured paths often point at a library that isn't installed. Capped so a huge
    /// network library can't stall the build.
    /// </summary>
    private sealed class Library(Autodesk.Revit.ApplicationServices.Application app)
    {
        private const int MaxFiles = 20000;
        private List<string>? _files;
        private readonly List<string> _searched = [];

        /// <summary>Where Find looked, for the report when nothing is found.</summary>
        public string Searched => _searched.Count == 0 ? "no content library folder exists on this PC" : string.Join("; ", _searched.Take(6)) + (_searched.Count > 6 ? "; ..." : "");

        private List<string> Files()
        {
            if (_files is not null) return _files;
            _files = [];
            var roots = new List<string>();
            try { roots.AddRange(app.GetLibraryPaths().Values.Where(Directory.Exists)); }
            catch (Exception) { }
            try
            {
                var libraries = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", $"RVT {app.VersionNumber}", "Libraries");
                if (Directory.Exists(libraries))
                    roots.AddRange(Directory.GetDirectories(libraries).OrderBy(d => Path.GetFileName(d).IndexOf("Imperial", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Select(r => r.TrimEnd('\\', '/')).Where(r => seen.Add(r)))
            {
                // A configured path inside one already listed (or containing it) would list the same files twice.
                if (_searched.Any(done => root.StartsWith(done + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                _searched.Add(root);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories))
                    {
                        if ((Path.GetDirectoryName(f) ?? "").IndexOf("Structural", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        _files.Add(f);
                        if (_files.Count >= MaxFiles) return _files;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
            return _files;
        }

        public string? Find(BuiltInCategory category, StructuralMaterialType? material, string? nameWord)
        {
            var folder = category switch
            {
                BuiltInCategory.OST_StructuralColumns => "Columns",
                BuiltInCategory.OST_StructuralFraming => "Framing",
                _ => "Foundations",
            };
            bool Has(string text, string word) => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
            var inFolder = Files().Where(f => Has(Path.GetDirectoryName(f) ?? "", folder)).ToList();
            IEnumerable<string> hits = inFolder;
            if (nameWord is not null) hits = hits.Where(f => Has(Path.GetFileName(f), nameWord));
            if (material == StructuralMaterialType.Concrete)
                // e.g. Structural Framing\Concrete\Concrete-Rectangular Beam.rfa (Imperial) or M_Concrete-Rectangular Beam.rfa (Metric);
                // precast shapes (tees, hollow core) last.
                hits = hits.Where(f => Has(f, "Concrete") && !Has(Path.GetFileName(f), "Joist"))
                    .OrderBy(f => Has(Path.GetFileName(f), "Rectangular") ? 0 : Has(f, "Precast") ? 2 : 1);
            else if (material == StructuralMaterialType.Steel)
                hits = hits.Where(f => Has(Path.GetDirectoryName(f) ?? "", "Steel") && File.Exists(Path.ChangeExtension(f, ".txt")))
                    .OrderBy(f => Has(Path.GetFileName(f), "Wide Flange") || Has(Path.GetFileName(f), "Universal") ? 0 : 1);
            else if (nameWord is not null && nameWord.Equals("Pile", StringComparison.OrdinalIgnoreCase))
                // Pile-Steel Pipe.rfa / M_Pile-Steel Pipe.rfa before Pile Cap-2 Pile.rfa (a cap with nested piles).
                hits = hits.OrderBy(f => Path.GetFileNameWithoutExtension(f).TrimStart('M', '_').StartsWith("Pile-", StringComparison.OrdinalIgnoreCase) ? 0 : Has(Path.GetFileName(f), "Cap") ? 2 : 1);
            else hits = hits.OrderBy(f => Has(Path.GetFileName(f), "Rectangular") ? 0 : 1);
            return hits.FirstOrDefault();
        }

        public static string? FirstCatalogType(string catalog)
        {
            try
            {
                // A catalog Revit can't parse would raise a modal dialog in LoadFamilySymbol: treat it as no catalog.
                var lines = File.ReadAllLines(catalog);
                return TypeCatalog.Problem(lines) is null ? TypeCatalog.Types(lines).FirstOrDefault() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>
    /// Saves the built project into the test folder and closes it. Workshared: a central plus a new local file (so
    /// borrowing and relinquishing work as in a real project); otherwise, or if that fails, a plain file. Returns the
    /// file to open.
    /// </summary>
    private static string SaveForTest(Document doc, string folder, bool workshared, RunReport result)
    {
        var plain = Path.Combine(folder, "FieldTest.rvt");
        var closed = false;
        try
        {
            doc.SaveAs(plain, new SaveAsOptions { OverwriteExistingFile = true });
            if (!workshared) return plain;
            try
            {
                if (!doc.IsWorkshared)
                {
                    if (!doc.CanEnableWorksharing()) throw new InvalidOperationException("Revit says worksharing can't be enabled in this project");
                    doc.EnableWorksharing("Shared Levels and Grids", "Workset1");
                }
                var central = Path.Combine(folder, "FieldTest-central.rvt");
                var options = new SaveAsOptions { OverwriteExistingFile = true };
                options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                doc.SaveAs(central, options);
                doc.Close(false);
                closed = true;
                var local = Path.Combine(folder, "FieldTest-local.rvt");
                WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central), ModelPathUtils.ConvertUserVisiblePathToModelPath(local));
                result.Workshared = true;
                return local;
            }
            catch (Exception ex)
            {
                result.Sample.Add($"worksharing: {ex.Message}; the test project is not workshared, so worksharing tools will skip");
                return plain;
            }
        }
        finally
        {
            if (!closed) try { doc.Close(false); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Switches Revit back to the model the user was in (or any other saved model that is open) and closes the test
    /// project without saving. Call from the external event (OpenAndActivateDocument is refused in event handlers).
    /// The API cannot close the active model, so with nothing saved to switch to, a blank project is opened first (it then
    /// stays open, and the report says so); only if that fails too does the test project stay open. A transaction left open in it
    /// by a tool blocks both switching and closing, and the API gives no handle to roll back a transaction someone else
    /// started (Document.IsModifiable only says one is open), so then it is left open and the report says what to do.
    /// </summary>
    private static void CloseTest(UIApplication app, Document? test, Document? original, RunReport result, ref bool keepFolder)
    {
        if (test is null || !test.IsValidObject) return;
        if (test.IsModifiable)
        {
            keepFolder = true;
            result.StillOpen = $"The test project '{test.Title}' is still open: a tool left a transaction open in it, and Revit can't switch " +
                               "models or close one while a transaction is open (the API has no way to roll back a transaction another tool " +
                               "started). Go to its window, press Esc (or Cancel any edit mode), then close it and don't save. Its files are in " +
                               Path.GetDirectoryName(test.PathName) + ".";
            return;
        }
        var active = app.ActiveUIDocument?.Document;
        if (active is not null && active.Equals(test))
        {
            var candidates = new List<Document>();
            if (original is not null && original.IsValidObject) candidates.Add(original);
            candidates.AddRange(app.Application.Documents.Cast<Document>()
                .Where(d => !d.IsFamilyDocument && !d.IsLinked && !d.Equals(test) && (original is null || !d.Equals(original))));
            foreach (var d in candidates)
            {
                if (string.IsNullOrEmpty(d.PathName) || !File.Exists(d.PathName)) continue;
                try
                {
                    app.OpenAndActivateDocument(d.PathName);
                    if (app.ActiveUIDocument?.Document.Equals(test) != true) break;
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { }
            }
        }
        // Nothing saved to switch to (Revit was on its Home screen, or only unsaved models are open): open a small blank
        // project instead, since the API can't close the model you are in. It stays open; the report says so.
        string? blank = null, blankProblem = null;
        if (app.ActiveUIDocument?.Document.Equals(test) == true)
        {
            try
            {
                blank = BlankProject(app.Application);
                app.OpenAndActivateDocument(blank);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                blankProblem = ex.Message;
            }
        }
        if (app.ActiveUIDocument?.Document.Equals(test) == true)
        {
            keepFolder = true;
            result.StillOpen = $"The test project '{test.Title}' is still open: Revit can't close the active model through the API, there was no " +
                               "saved model to switch back to" + (blankProblem is null ? "" : $", and a blank project could not be opened instead ({blankProblem})") +
                               ". Close its window (File > Close, or the x on its view tab); there is nothing to save. Its files are in " +
                               Path.GetDirectoryName(test.PathName) + ". Next time, open any saved model before starting the field test.";
            return;
        }
        if (blank is not null && app.ActiveUIDocument?.Document is { } shown && SamePath(shown.PathName, blank))
            result.Sample.Add($"Revit now shows '{Path.GetFileNameWithoutExtension(blank)}', an empty project the field test opened so it could close " +
                              "the test project (there was no saved model to switch back to). Close it; there is nothing to save.");
        try { test.Close(false); }
        catch (Exception ex)
        {
            keepFolder = true;
            result.StillOpen = $"The test project '{test.Title}' could not be closed ({ex.Message}). Close its window; there is nothing to save.";
        }
    }

    /// <summary>
    /// An empty metric project saved in the temp folder (made once per Revit version and reused), to switch to when
    /// nothing saved is open. Kept out of the test folder, which is deleted while it may still be open. One file per
    /// version: an older Revit can't open a newer one's file, and a newer one would upgrade an older one at every open.
    /// </summary>
    private static string BlankProject(Autodesk.Revit.ApplicationServices.Application a)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Sketch ProMax blank {a.VersionNumber}.rvt");
        if (File.Exists(path)) return path;
        var doc = a.NewProjectDocument(UnitSystem.Metric);
        try { doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true }); }
        finally { try { doc.Close(false); } catch (Exception) { } }
        return path;
    }

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- Report -----------------------------------------------------------------------------------------------------

    internal static string DefaultReportFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "field-tests");

    private static void WriteReport(RunReport r, string? folder)
    {
        try
        {
            folder ??= DefaultReportFolder;
            Directory.CreateDirectory(folder);
            var name = $"{r.Started:yyyyMMdd-HHmmss} {r.Title} Revit {r.RevitVersion}";
            // Test before sharing: only its own sections (the coverage and bug log are the whole field test's).
            var whole = r.Title == "field test";
            r.ReportPath = Path.Combine(folder, name + ".md");
            r.JsonPath = Path.Combine(folder, name + ".json");

            var md = new StringBuilder();
            md.AppendLine($"# Sketch ProMax {r.Title}").AppendLine();
            md.AppendLine($"- Revit: {r.RevitName} ({r.RevitVersion}, build {r.RevitBuild})");
            md.AppendLine($"- Sketch ProMax: {r.AddinVersion}");
            md.AppendLine($"- Date: {r.Started:yyyy-MM-dd HH:mm}, took {Jobs.Duration(r.Took)}");
            md.AppendLine($"- Template: {r.Template}");
            md.AppendLine($"- Test project workshared: {(r.Workshared ? "yes (central + local in a temp folder)" : "no")}");
            if (r.Families is not null) md.AppendLine($"- Families tested: {string.Join(", ", r.Families.Select(Title))}");
            md.AppendLine($"- Result: **{r.Count("pass")} passed, {r.Count("fail")} failed, {r.Count("manual")} manual (checklist), {r.Count("skip")} not run**");
            if (r.StoppedBecause is not null) md.AppendLine($"- Stopped early: {r.StoppedBecause}");
            if (r.Fatal is not null) md.AppendLine($"- Could not run: {r.Fatal}");
            if (r.StillOpen is not null) md.AppendLine($"- {r.StillOpen}");
            md.AppendLine();

            md.AppendLine("## By family").AppendLine().AppendLine("| Family | Pass | Fail | Manual | Not run |").AppendLine("|---|---:|---:|---:|---:|");
            foreach (var g in Groups.Where(g => r.Rows.Any(x => x.Family == g.Key)))
            {
                var rows = r.Rows.Where(x => x.Family == g.Key).ToList();
                md.AppendLine($"| {g.Title} | {rows.Count(x => x.Status == "pass")} | {rows.Count(x => x.Status == "fail")} | {rows.Count(x => x.Status == "manual")} | {rows.Count(x => x.Status == "skip")} |");
            }
            md.AppendLine();

            foreach (var g in Groups.Where(g => r.Rows.Any(x => x.Family == g.Key)))
            {
                md.AppendLine($"## {g.Title}").AppendLine().AppendLine("| Tool | Status | Time | Detail |").AppendLine("|---|---|---:|---|");
                foreach (var row in r.Rows.Where(x => x.Family == g.Key).OrderBy(x => x.Status == "fail" ? 0 : x.Status == "pass" ? 1 : x.Status == "manual" ? 2 : 3).ThenBy(x => x.Tool))
                    md.AppendLine($"| {row.Tool} | {(row.Status == "fail" ? "**fail**" : row.Status)} | {(row.Ms > 0 ? $"{row.Ms / 1000.0:0.0} s" : "")} | {Cell(row.Message)} |");
                md.AppendLine();
            }

            md.AppendLine("## Test project").AppendLine();
            foreach (var note in r.Sample) md.AppendLine($"- {note}");
            md.AppendLine();
            // How long Sketch ProMax took to start, and what opening models cost while it ran (bug #18).
            md.AppendLine(SketchProMax.Revit.StartupTimings.Describe());
            // Which copies of the shared libraries (System.Memory, Roslyn, System.Text.Json) this Revit really uses.
            try { md.AppendLine(SketchProMax.Revit.AssemblyReport.Describe()); }
            catch (Exception ex) { md.AppendLine("## Assemblies").AppendLine().AppendLine("Could not be read: " + ex.Message); }
            if (r.TimeSaved is not null) md.AppendLine("## Time saved").AppendLine().AppendLine(r.TimeSaved).AppendLine();
            // Last, so it is easy to find: every tool and feature, tested or manual (the checklist's source).
            // Each fixed bug log row with the check that fails if it comes back: which rows can be marked Confirmed.
            if (!whole)
            {
                md.AppendLine("## What each check covers").AppendLine();
                foreach (var f in FieldTestCoverage.Features.Where(f => r.Rows.Any(x => x.Tool == f.Key)))
                    md.AppendLine($"- **{f.Key}** ({f.Title}): {f.Reason}");
                md.AppendLine();
            }
            var coverage = whole ? Coverage(r) : new List<CoverageRow>();
            if (whole)
            {
                md.AppendLine(BugLogSection(r));
                md.AppendLine(CoverageMarkdown(coverage));
            }
            File.WriteAllText(r.ReportPath, md.ToString());

            File.WriteAllText(r.JsonPath, JsonSerializer.Serialize(new
            {
                revit = new { name = r.RevitName, version = r.RevitVersion, build = r.RevitBuild },
                addin = r.AddinVersion,
                date = r.Started,
                seconds = (int)r.Took.TotalSeconds,
                template = r.Template,
                workshared = r.Workshared,
                families_tested = r.Families,
                passed = r.Count("pass"), failed = r.Count("fail"), manual = r.Count("manual"), skipped = r.Count("skip"),
                stopped_early = r.StoppedBecause,
                could_not_run = r.Fatal,
                still_open = r.StillOpen,
                kept_project = r.ProjectPath,
                by_family = Groups.Where(g => r.Rows.Any(x => x.Family == g.Key)).Select(g => new
                {
                    family = g.Key, title = g.Title,
                    pass = r.Rows.Count(x => x.Family == g.Key && x.Status == "pass"),
                    fail = r.Rows.Count(x => x.Family == g.Key && x.Status == "fail"),
                    manual = r.Rows.Count(x => x.Family == g.Key && x.Status == "manual"),
                    skip = r.Rows.Count(x => x.Family == g.Key && x.Status == "skip"),
                    tools = r.Rows.Where(x => x.Family == g.Key).Select(x => new { tool = x.Tool, status = x.Status, ms = x.Ms, detail = x.Message }),
                }),
                test_project = r.Sample,
                startup = new { start = SketchProMax.Revit.StartupTimings.StartLine(), opens = SketchProMax.Revit.StartupTimings.OpenLines() },
                bug_log = BugLogJson(r),
                coverage = coverage.Select(c => new { name = c.Name, kind = c.Kind, coverage = c.Status, detail = c.Detail }),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            r.Sample.Add($"report not written: {ex.Message}");
            r.ReportPath = r.JsonPath = null;
        }
    }

    private static string Cell(string s) => Short(s, 900).Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");

    private static string Short(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    // ---- Ribbon -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The ribbon's Field test button (CoreEntry "field-test"): asks first, then starts the run as a job and returns at
    /// once; the result is shown when it finishes. Clicked again while it runs, it shows progress and can stop it.
    /// </summary>
    internal static Autodesk.Revit.UI.Result RibbonCommand(UIApplication app)
    {
        if (Jobs.Running is { Tool: "run_field_test" } ours)
        {
            var progress = new TaskDialog("Sketch ProMax field test")
            {
                MainInstruction = ours.CancelRequested ? "The field test is stopping" : "The field test is running",
                MainContent = $"{Jobs.Describe(ours)}." + (ours.Progress() is { } p ? $"\n\n{p}" : ""),
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            if (!ours.CancelRequested) progress.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Stop it after the current tool",
                "The test project is then closed without saving and the report lists what ran.");
            if (progress.Show() == TaskDialogResult.CommandLink1) ours.CancelRequested = true;
            return Autodesk.Revit.UI.Result.Succeeded;
        }
        if (Jobs.Running is not null || App.Dispatcher.IsRunning || SelfTest.IsRunning)
        {
            TaskDialog.Show("Sketch ProMax field test", SelfTest.IsRunning ? "A self-test is running. Wait for it to finish, then try again." : Jobs.Busy(Jobs.Running).Text);
            return Autodesk.Revit.UI.Result.Cancelled;
        }
        var ask = new TaskDialog("Sketch ProMax field test")
        {
            MainInstruction = "Run the field test?",
            MainContent =
                "Sketch ProMax creates a new throwaway project (levels, grids, concrete and steel columns and beams, walls, a floor " +
                "with a slab edge, foundations, a linked architectural model, views and a sheet), opens it in its own window and " +
                "runs every tool family against it, one tool at a time. Then it closes that project without saving and writes " +
                "one report.\n\nThe models you have open are not changed: Revit only switches back to your model at the end. " +
                "Save your open models first all the same, since Revit switches windows while it runs.\n\n" +
                $"It takes {Estimate}. Revit stays responsive between tools, but leave the test project's window active until it " +
                "finishes. Click Field test again to see progress or stop it (Cancel job and the pane's Cancel also stop it before the next tool).",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.Yes,
        };
        if (ask.Show() != TaskDialogResult.Yes) return Autodesk.Revit.UI.Result.Cancelled;

        var job = Jobs.TryCreate("run_field_test", out var inTheWay);
        if (job is null)
        {
            TaskDialog.Show("Sketch ProMax field test", Jobs.Busy(inTheWay).Text);
            return Autodesk.Revit.UI.Result.Cancelled;
        }
        job.TryStart();
        Jobs.Running = job;
        job.StartedSignal.TrySetResult(true);
        job.Task = Start(job, App.Bridge?.Registry ?? ToolRegistry.CreateDefault(), new Options(), ShowResult);
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    /// <summary>
    /// Test before sharing (checklist settings, behind the Default's password): the throwaway project and the "sharing"
    /// family only, its report saved in Documents\Sketch ProMax\Test reports for the user to upload. Starts it as a job
    /// and returns at once; the result is shown when it ends. Returns why it can't start, or null.
    /// </summary>
    internal static string? StartBeforeSharing()
    {
        if (Jobs.Running is not null || App.Dispatcher.IsRunning || SelfTest.IsRunning)
            return SelfTest.IsRunning ? "A self-test is running. Wait for it to finish, then try again." : Jobs.Busy(Jobs.Running).Text;
        var job = Jobs.TryCreate("run_field_test", out var inTheWay);
        if (job is null) return Jobs.Busy(inTheWay).Text;
        job.TryStart();
        Jobs.Running = job;
        job.StartedSignal.TrySetResult(true);
        var o = new Options { Families = [SharingFamily], ReportFolder = TestReportFolder, Title = SharingTitle };
        job.Task = Start(job, App.Bridge?.Registry ?? ToolRegistry.CreateDefault(), o, ShowBeforeSharing);
        return null;
    }

    /// <summary>Test before sharing's result: the counts, the failures, and where the report is to upload it.</summary>
    private static void ShowBeforeSharing(RunReport? result, ToolResult outcome)
    {
        ActivityLog.Note("test_before_sharing", result is null ? Short(outcome.Text, 900)
            : $"{result.Summary()} Failed: {string.Join("; ", result.Rows.Where(x => x.Status == "fail").Select(x => x.Tool))}. Report: {result.ReportPath}");
        if (result is null)
        {
            TaskDialog.Show("Sketch ProMax test before sharing", Short(outcome.Text, 1800));
            return;
        }
        var failed = result.Rows.Where(x => x.Status == "fail").ToList();
        var show = new TaskDialog("Sketch ProMax test before sharing")
        {
            MainInstruction = result.Fatal is not null ? "The test could not run"
                : failed.Count == 0 ? $"All {result.Count("pass")} checks passed" : $"{failed.Count} of {result.Rows.Count} checks failed",
            MainContent = (failed.Count == 0 ? "" : "Failed: " + string.Join(", ", failed.Select(x => x.Tool)) + "\n\n") +
                          (result.ReportPath is null ? Short(result.Summary(), 1500) : $"The report is saved as:\n{result.ReportPath}"),
            FooterText = result.ReportPath is null ? "" : "Upload the report in the Sketch ProMax project to share the results.",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        if (result.ReportPath is not null)
        {
            show.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Show the report in its folder", "To upload it in the Sketch ProMax project.");
            show.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the report");
        }
        var choice = show.Show();
        try
        {
            if (choice == TaskDialogResult.CommandLink1 && result.ReportPath is not null)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.ReportPath}\"") { UseShellExecute = true })?.Dispose();
            else if (choice == TaskDialogResult.CommandLink2 && result.ReportPath is not null)
                Process.Start(new ProcessStartInfo(result.ReportPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskDialog.Show("Sketch ProMax test before sharing", $"Couldn't open {result.ReportPath}: {ex.Message}");
        }
    }

    /// <summary>The ribbon run's result dialog, on Revit's thread once the run has ended.</summary>
    private static void ShowResult(RunReport? result, ToolResult outcome)
    {
        if (result is null)
        {
            TaskDialog.Show("Sketch ProMax field test", Short(outcome.Text, 1800));
            return;
        }
        var show = new TaskDialog("Sketch ProMax field test")
        {
            MainInstruction = result.Fatal is not null ? "The field test could not run"
                : result.Count("fail") == 0 ? "No tool failed" : $"{result.Count("fail")} tool(s) failed",
            MainContent = Short(result.Summary(), 1800),
            FooterText = result.ReportPath is null ? "" : "Attach the report to a chat to get the failures fixed.",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        if (result.ReportPath is not null)
        {
            show.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the report", result.ReportPath);
            show.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the report folder");
        }
        var choice = show.Show();
        try
        {
            if (choice == TaskDialogResult.CommandLink1 && result.ReportPath is not null)
                Process.Start(new ProcessStartInfo(result.ReportPath) { UseShellExecute = true })?.Dispose();
            else if (choice == TaskDialogResult.CommandLink2 && result.ReportPath is not null)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.ReportPath}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskDialog.Show("Sketch ProMax field test", $"Couldn't open {result.ReportPath}: {ex.Message}");
        }
    }
}

public sealed class RunFieldTestTool : IRevitTool
{
    public string Name => "run_field_test";
    public string Description =>
        "Test every Sketch ProMax tool family in real Revit without touching the user's models: creates a throwaway project " +
        "(levels, grids, concrete and steel columns and beams, walls, a floor with a slab edge, foundations, a linked " +
        "architectural model, plan, section, 3D view, schedule and sheet), opens it in its own window, runs every self-test " +
        "case against it, then batch_run_recipe on two copies, the self-test in safe mode and the non-tool features (ribbon, " +
        "option windows, favourites, chat, bug log, exports, updates), closes it without saving and switches back to the user's " +
        "model. Writes a Markdown + JSON report (per-family pass/fail counts, per-tool status, error and time, and a coverage " +
        "section listing every tool and feature as tested or manual) and returns a summary and the report path. Takes " +
        "several minutes: the call returns a job id; use wait_for_job for progress and the result, cancel_job to stop between tools.";
    public string PropertiesJson => """
    {
      "families": {"type": "array", "items": {"type": "string"}, "description": "Only test these families: steel, rebar, precast_pt, clashes, arch_to_structure, marine, checkers, boq, ve, analytical, views_sheets, worksharing, other. Default: all."},
      "include_heavy": {"type": "boolean", "description": "Also run the slow tools (exports, whole-model checks, image capture). Default true; the test project is small."},
      "keep_project": {"type": "boolean", "description": "Keep the test project files in a temp folder and return the path. Default false: closed without saving and deleted."},
      "workshared": {"type": "boolean", "description": "Make the test project workshared (central + local) so worksharing tools run. Default true."},
      "chat_round_trip": {"type": "boolean", "description": "Also send one short message through the in-Revit chat's Claude Code (uses the user's Claude plan). Default false: the checklist covers it."},
      "report_folder": {"type": "string", "description": "Folder for the report. Default %LOCALAPPDATA%\\SketchProMax\\field-tests."},
      "tool_time_cap_seconds": {"type": "integer", "description": "Ask a tool to stop after this long (tools that support cancel stop; others run to the end). Default 120."}
    }
    """;
    public string[] Required => [];
    // The user's models are never changed: everything happens in a new project that is closed without saving.
    public bool ModifiesModel => false;

    private readonly ToolRegistry _registry;
    public RunFieldTestTool(ToolRegistry registry) => _registry = registry;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        if (SelfTest.IsRunning) return ToolResult.Error("A self-test is running. Wait for it (self_test_status) before starting the field test.");
        var options = new FieldTest.Options
        {
            Families = FieldTest.ParseFamilies(args.StringList("families")),
            IncludeHeavy = args.OptBool("include_heavy", true),
            KeepProject = args.OptBool("keep_project"),
            Workshared = args.OptBool("workshared", true),
            ChatRoundTrip = args.OptBool("chat_round_trip"),
            ReportFolder = OutputPaths.Checked(args.OptString("report_folder")),
            ToolCapSeconds = (int)Math.Max(10, Math.Min(1800, args.OptLong("tool_time_cap_seconds") ?? 120)),
        };
        if (Jobs.CanContinue(Name) is not { } job)
            return ToolResult.Error("run_field_test runs only when Claude calls it directly or from the ribbon's Field test button, not from a recipe or quick tool.");
        // Each tool then runs in its own Revit API call, so Revit stays responsive; the job's result arrives through wait_for_job.
        job.Continuation = FieldTest.Start(job, _registry, options);
        return ToolResult.Ok(new
        {
            status = "started", job = job.Id,
            note = $"The field test runs one tool at a time ({FieldTest.Estimate}). Call wait_for_job (job_id {job.Id}) for progress and the " +
                   "result; cancel_job stops it before the next tool. Don't send other model work meanwhile.",
        });
    }
}
