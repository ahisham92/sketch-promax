using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.UI;

namespace SketchProMax.Tools;

/// <summary>
/// What the field test runs after the self-test cases, so the report has no skips: batch_run_recipe on two copies of
/// the test project (and ve_apply on a copy, in the last of them), the self-test in safe mode on the test project, the
/// non-tool features (FieldTestCoverage's probes: ribbon, option windows, favourites, chat, bug log, updates, the
/// Claude app), and the coverage section built from the registry against FieldTestCoverage. Each probe is one row in
/// the "features" family, named by its coverage key.
/// </summary>
internal static partial class FieldTest
{
    private const string BatchFolderName = "batch";

    /// <summary>Two closed copies of the saved (not workshared) test project, for batch_run_recipe. Call once the project is closed.</summary>
    private static void MakeCopies(State s, RunReport result)
    {
        var plain = Path.Combine(s.Folder, "FieldTest.rvt");
        try
        {
            if (!File.Exists(plain)) throw new FileNotFoundException("the test project was not saved");
            var folder = Path.Combine(s.Folder, BatchFolderName);
            Directory.CreateDirectory(folder);
            File.Copy(plain, Path.Combine(folder, "FieldTest copy A.rvt"), true);
            File.Copy(plain, Path.Combine(folder, "FieldTest copy B.rvt"), true);
            s.BatchFolder = folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Sample.Add($"batch copies: not made ({ex.Message}), so batch_run_recipe and ve_apply on a copy can't run");
        }
    }

    private static bool StoppedOrCancelled(State s)
    {
        if (s.Result.StoppedBecause is null && s.Job.CancelRequested) s.Result.StoppedBecause = "stopped on request (cancel_job or Cancel in Revit)";
        return s.Result.StoppedBecause is not null;
    }

    private static async Task RunExtras(State s)
    {
        var o = s.O;
        if (s.Session is not null && s.Test is not null)
        {
            if (Wanted(o, "batch_run_recipe") && !StoppedOrCancelled(s)) await BatchAndCopy(s).ConfigureAwait(false);
            if (Wanted(o, "run_self_test") && !StoppedOrCancelled(s)) await SelfTestRun(s).ConfigureAwait(false);
        }
        // TODO(hand-off): when the manual-step hand-off window is merged, add a probe here that opens it on a sample step
        // (Continue / Skip this item / Stop) and closes it again, with its FieldTestCoverage feature row and ribbon mapping.
        // TODO(time-tracking): add a probe that writes and reads back one time-log entry in the test project, likewise.
        if ((o.Families is null || o.Families.Contains("features")) && !StoppedOrCancelled(s)) await Features(s).ConfigureAwait(false);
        if (Wanted(o, "run_field_test"))
            s.Result.Rows.Add(new(FamilyOf("run_field_test"), "run_field_test", "pass", 0, "this report is the test"));
    }

    /// <summary>A tool called the way the self-test calls it: arguments as JSON, Revit's failures handled.</summary>
    private static ToolResult Call(UIApplication app, ToolRegistry registry, string name, object args)
    {
        var tool = registry.Find(name) ?? throw new InvalidOperationException($"{name} is not in the registry");
        var raw = JsonSerializer.SerializeToElement(args);
        var dict = raw.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return Revit.FailureGuard.Run(() => tool.Execute(app, new ToolArgs(dict)));
    }

    // ---- batch_run_recipe and ve_apply on a copy ------------------------------------------------------------------------

    private static async Task BatchAndCopy(State s)
    {
        Jobs.Report("Field test: batch_run_recipe on two copies of the test project, then ve_apply on a copy");
        var watch = Stopwatch.StartNew();
        List<Row> rows;
        // Opens and closes documents: only from the external event.
        try { rows = await App.Dispatcher.InvokeFromEventAsync(app => BatchOnRevit(app, s)).ConfigureAwait(false); }
        catch (Exception ex) { rows = [new(FamilyOf("batch_run_recipe"), "batch_run_recipe", "fail", watch.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}")]; }
        s.Result.Rows.AddRange(rows);
        await Task.Delay(150).ConfigureAwait(false);
    }

    private static List<Row> BatchOnRevit(UIApplication app, State s)
    {
        var rows = new List<Row>();
        var family = FamilyOf("batch_run_recipe");
        if (s.BatchFolder is null)
        {
            rows.Add(new(family, "batch_run_recipe", "fail", 0, "the two copies of the test project could not be made (see the Test project notes)"));
            rows.Add(new("ve", "ve.apply_copy", "fail", 0, "no copy to run it in (see the Test project notes)"));
            return rows;
        }
        const string recipe = "sketch-promax-field-batch";
        var watch = Stopwatch.StartNew();
        try
        {
            var saved = Call(app, s.Registry, "save_recipe", new { name = recipe, description = "Field test batch", steps = new[] { new { tool = "get_model_context", arguments = new { } } } });
            if (saved.IsError)
            {
                rows.Add(new(family, "batch_run_recipe", "fail", watch.ElapsedMilliseconds, "its recipe could not be saved: " + Short(saved.Text, 400)));
                return rows;
            }
            var batch = Call(app, s.Registry, "batch_run_recipe", new { recipe, folder = s.BatchFolder });
            var wrong = batch.IsError ? null : BatchProblem(batch.Text);
            rows.Add(new(family, "batch_run_recipe", batch.IsError || wrong is not null ? "fail" : "pass", watch.ElapsedMilliseconds,
                (wrong is null ? "" : $"Ran, but {wrong}. ") + Short(batch.Text, 600)));

            // ve_apply's default target, a saved copy: in the last batch copy, which batch_run_recipe leaves open and active
            // (a plain, not workshared, copy of the test project: the test project itself is workshared and refuses it).
            var ve = Stopwatch.StartNew();
            var active = app.ActiveUIDocument?.Document;
            if (active is null || (s.Test is { IsValidObject: true } t && active.Equals(t)))
                rows.Add(new("ve", "ve.apply_copy", "fail", 0, "batch_run_recipe left no copy open to run it in"));
            else
            {
                var wall = new FilteredElementCollector(active).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault(w => w.WallType.Kind == WallKind.Basic);
                if (wall is null) rows.Add(new("ve", "ve.apply_copy", "fail", 0, "the copy has no wall to change"));
                else
                {
                    var copy = Path.Combine(s.BatchFolder, "FieldTest VE copy.rvt");
                    var result = Call(app, s.Registry, "ve_apply", new
                    {
                        target = "copy", copy_path = copy,
                        changes = new[] { new { kind = "change_type", element_ids = new[] { wall.Id.Value }, to_type = wall.Name } },
                    });
                    var ok = !result.IsError && File.Exists(copy);
                    rows.Add(new("ve", "ve.apply_copy", ok ? "pass" : "fail", ve.ElapsedMilliseconds,
                        (ok || result.IsError ? "" : $"Ran, but the copy {copy} is not there. ") + Short(result.Text, 600)));
                }
            }
        }
        finally
        {
            try { File.Delete(Recipes.PathFor(recipe)); } catch (Exception) { }
            SwitchBack(app, s);
        }
        return rows;
    }

    /// <summary>Null when both copies ran the recipe; otherwise what went wrong.</summary>
    internal static string? BatchProblem(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var models = r.GetProperty("models").GetInt32();
            var errors = r.GetProperty("report").EnumerateArray().Where(m => m.TryGetProperty("error", out _)).Select(m => m.GetProperty("error").GetString()).ToList();
            if (models != 2) return $"it ran on {models} models, not the 2 copies";
            return errors.Count == 0 ? null : "a copy failed: " + string.Join("; ", errors);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return $"its result could not be read ({ex.Message})"; }
    }

    /// <summary>Makes the test project active again and closes what the batch left open. Stops the run when it can't.</summary>
    private static void SwitchBack(UIApplication app, State s)
    {
        if (s.Test is not { IsValidObject: true } test) return;
        if (app.ActiveUIDocument?.Document.Equals(test) != true)
        {
            try { app.OpenAndActivateDocument(s.ProjectFile!); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { }
        }
        foreach (var d in app.Application.Documents.Cast<Document>().ToList())
        {
            if (d.Equals(test) || d.IsLinked || string.IsNullOrEmpty(d.PathName)) continue;
            if (!d.PathName.StartsWith(s.BatchFolder ?? "\0", StringComparison.OrdinalIgnoreCase)) continue;
            if (app.ActiveUIDocument?.Document.Equals(d) == true) continue;
            try { d.Close(false); } catch (Exception) { }
        }
        if (app.ActiveUIDocument?.Document.Equals(test) != true)
            s.Result.StoppedBecause ??= "Revit could not switch back to the test project after batch_run_recipe";
    }

    // ---- The self-test, safe mode, on the test project ------------------------------------------------------------------

    private static async Task SelfTestRun(State s)
    {
        var family = FamilyOf("run_self_test");
        Jobs.Report("Field test: run_self_test (safe mode) on the test project");
        var watch = Stopwatch.StartNew();
        ToolResult start;
        try { start = await App.Dispatcher.InvokeAsync(app => Call(app, s.Registry, "run_self_test", new { mode = "safe" })).ConfigureAwait(false); }
        catch (Exception ex) { start = ToolResult.Error($"{ex.GetType().Name}: {ex.Message}"); }
        if (start.IsError)
        {
            s.Result.Rows.Add(new(family, "run_self_test", "fail", watch.ElapsedMilliseconds, Short(start.Text, 600)));
            return;
        }
        // Poll until it is done; Cancel stops it, and so does a cap well above its usual minute or two.
        string? stopped = null;
        while (SelfTest.IsRunning)
        {
            if (s.Job.CancelRequested) { SelfTest.Stop(); stopped ??= "stopped on request"; }
            if (watch.Elapsed > TimeSpan.FromMinutes(20)) { SelfTest.Stop(); stopped ??= "stopped after 20 minutes"; }
            await Task.Delay(1000).ConfigureAwait(false);
        }
        var report = SelfTest.LastReport;
        var counts = report is null ? "no report" : $"{report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped by the self-test's own rules";
        var failures = report is null ? "" : string.Join("; ", report.Results.Where(r => r.Status == "fail").Take(5).Select(r => $"{r.Tool}: {Short(r.Message, 120)}"));
        s.Result.Rows.Add(new(family, "run_self_test", report is not null && report.Failed == 0 && stopped is null ? "pass" : "fail", watch.ElapsedMilliseconds,
            $"Safe mode on the test project: {counts}" + (stopped is null ? "" : $" ({stopped})") + (failures.Length > 0 ? ". Failures: " + failures : "") +
            (report?.File is { } file ? $". Report: {file}" : "")));

        var poll = Stopwatch.StartNew();
        ToolResult status;
        try { status = await App.Dispatcher.InvokeAsync(app => Call(app, s.Registry, "self_test_status", new { })).ConfigureAwait(false); }
        catch (Exception ex) { status = ToolResult.Error($"{ex.GetType().Name}: {ex.Message}"); }
        var finished = !status.IsError && status.Text.Contains("\"running\":false");
        s.Result.Rows.Add(new(family, "self_test_status", finished ? "pass" : "fail", poll.ElapsedMilliseconds,
            (finished ? "Polled until done; " : "Did not say the self-test had finished; ") + Short(status.Text, 400)));
    }

    // ---- Non-tool features ----------------------------------------------------------------------------------------------

    private static async Task Features(State s)
    {
        ClaudeCliInstall? cli = null;
        // On Revit's thread: the ribbon, WPF windows, favourites (they reach the ribbon), the chat history (the pane saves there).
        await Probe(s, "ribbon.registered", app => RibbonRegistered(app, s.Registry)).ConfigureAwait(false);
        await Probe(s, "ribbon.dialogs", _ => Dialogs(s.Registry, s.Folder)).ConfigureAwait(false);
        await Probe(s, "favourites", _ => FavouriteRoundTrip()).ConfigureAwait(false);
        await Probe(s, "chat.history", _ => ChatHistoryRoundTrip(s.Folder)).ConfigureAwait(false);
        await Probe(s, "update.loader", _ => Loader()).ConfigureAwait(false);
        // Off Revit's thread: processes, files and the network.
        await Probe(s, "chat.cli", () => { cli = new ClaudeCliInstall(Chat.ClaudeCli.Find()); return cli.Row(); }).ConfigureAwait(false);
        await Probe(s, "chat.signin", () => SignIn(cli)).ConfigureAwait(false);
        await Probe(s, "chat.roundtrip", () => RoundTrip(cli, s.O.ChatRoundTrip)).ConfigureAwait(false);
        await Probe(s, "chat.models", () => Models(s.Folder)).ConfigureAwait(false);
        await Probe(s, "chat.prompts", () => Prompts(s.Folder)).ConfigureAwait(false);
        await Probe(s, "chat.notes", Notes).ConfigureAwait(false);
        await Probe(s, "bugs.sync", () => BugSync(s.Folder)).ConfigureAwait(false);
        string? latest = null;
        await Probe(s, "update.endpoint", () => { var (row, tag) = Endpoint(); latest = tag; return row; }).ConfigureAwait(false);
        await Probe(s, "update.version", () => VersionCompare(latest)).ConfigureAwait(false);
        await Probe(s, "claude.msix", Msix).ConfigureAwait(false);
        await Probe(s, "diagnostics", () => Diagnostics(s.Result)).ConfigureAwait(false);
    }

    /// <summary>One probe on Revit's thread.</summary>
    private static Task Probe(State s, string key, Func<UIApplication, (string Status, string Message)> body) =>
        ProbeCore(s, key, () => App.Dispatcher.InvokeAsync(body));

    /// <summary>One probe off Revit's thread.</summary>
    private static Task Probe(State s, string key, Func<(string Status, string Message)> body) =>
        ProbeCore(s, key, () => Task.Run(body));

    private static async Task ProbeCore(State s, string key, Func<Task<(string Status, string Message)>> run)
    {
        if (StoppedOrCancelled(s)) { s.Result.Rows.Add(new("features", key, "skip", 0, "not reached: " + s.Result.StoppedBecause)); return; }
        Jobs.Report($"Field test: feature {key}");
        var watch = Stopwatch.StartNew();
        (string Status, string Message) outcome;
        try { outcome = await run().ConfigureAwait(false); }
        catch (Exception ex) { outcome = ("fail", $"{ex.GetType().Name}: {ex.Message}"); }
        s.Result.Rows.Add(new("features", key, outcome.Status, watch.ElapsedMilliseconds, outcome.Message));
    }

    private static (string, string) Pass(string message) => ("pass", message);
    private static (string, string) Fail(string message) => ("fail", message);

    // ---- Ribbon, option windows, favourites

    /// <summary>Every button, drop-down and entry the core declares is on the Sketch ProMax tab, visible and enabled.</summary>
    private static (string, string) RibbonRegistered(UIApplication app, ToolRegistry registry)
    {
        var lines = CoreRibbonManifest.Build(name => registry.Find(name) is null ? null : "", FavouritesRibbon.Shown).Split('\n').Select(l => l.Split('\t')).ToList();
        var declared = lines.Where(f => f.Length > 8 && f[0] is "button" or "pulldown" or "split" or "item").ToList();
        var found = new Dictionary<string, Autodesk.Revit.UI.RibbonItem>(StringComparer.Ordinal);
        void Add(Autodesk.Revit.UI.RibbonItem item)
        {
            var name = item.Name ?? "";
            var id = name.StartsWith("SketchProMax.", StringComparison.Ordinal) ? name.Substring("SketchProMax.".Length) : name;
            if (id.IndexOf('~') is var tilde and >= 0) id = id.Substring(0, tilde);
            // A button made again elsewhere leaves a hidden old one: the visible one counts.
            if (item.Visible || !found.ContainsKey(id)) found[id] = item;
            if (item is PulldownButton drop) foreach (var sub in drop.GetItems()) Add(sub);
        }
        foreach (var panel in app.GetRibbonPanels("Sketch ProMax"))
            foreach (var item in panel.GetItems())
                if (item is not null) Add(item);
        var missing = declared.Where(f => !found.TryGetValue(f[2], out var i) || !i.Visible).Select(f => f[2]).ToList();
        // Live buttons (Job, Update, the favourite slots) are greyed out on purpose when there is nothing to do.
        var disabled = declared.Where(f => !f[8].Contains("live:") && found.TryGetValue(f[2], out var i) && i.Visible && !i.Enabled).Select(f => f[2]).ToList();
        var summary = $"{declared.Count - missing.Count} of {declared.Count} declared buttons, drop-downs and entries are on the Sketch ProMax tab";
        if (missing.Count == 0 && disabled.Count == 0) return Pass(summary + ", all enabled.");
        return Fail(summary + "." + (missing.Count > 0 ? $" Missing: {string.Join(", ", missing.Take(20))}." : "") +
                    (disabled.Count > 0 ? $" Disabled: {string.Join(", ", disabled.Take(20))}." : "") +
                    (UpdateWindowsOld() ? " The loader is older than 0.30, so new buttons need a Revit restart." : ""));
    }

    private static bool UpdateWindowsOld() => LoaderCompat.RibbonOutdated(AppDomain.CurrentDomain.GetData(LoaderCompat.ReleaseKey) as string);

    /// <summary>Every ribbon tool's options window builds; a value remembered from last time comes back in it.</summary>
    private static (string, string) Dialogs(ToolRegistry registry, string folder)
    {
        var tools = CoreRibbonManifest.Tools().Select(t => t.Tool).Distinct().Select(registry.Find).OfType<IRevitTool>().ToList();
        var problems = new List<string>();
        foreach (var tool in tools)
        {
            try { Window(tool, null).Close(); }
            catch (Exception ex) { problems.Add($"{tool.Name}: {ex.Message}"); }
        }

        // Saved values: remembered in a temporary copy of the defaults file, read back, and shown in the window.
        const string value = "FT saved value";
        string? saved = null;
        foreach (var tool in tools)
        {
            var fields = ToolForm.Fields(tool.PropertiesJson, tool.Required);
            var field = fields.FirstOrDefault(f => f.Type == "string" && !f.ElementIds && f.Choices is null);
            if (field is null) continue;
            var path = Path.Combine(folder, "tool-defaults.json");
            var defaults = ToolDefaults.Load(path);
            defaults.Remember(tool.Name, fields, new Dictionary<string, string> { [field.Key] = value });
            if (!defaults.Save()) { problems.Add("the defaults file could not be written"); break; }
            var window = Window(tool, ToolDefaults.Load(path).For(tool.Name));
            var shown = Texts(window.Content as System.Windows.DependencyObject).Contains(value);
            window.Close();
            saved = shown ? $"{tool.Name}'s {field.Label} came back as typed last time" : null;
            if (!shown) problems.Add($"{tool.Name}: the value saved for {field.Label} was not filled in again");
            break;
        }
        if (saved is null && problems.Count == 0) problems.Add("no ribbon tool has a text field to check saved values with");
        return problems.Count == 0
            ? Pass($"{tools.Count} option windows built; {saved}.")
            : Fail($"{tools.Count - problems.Count} of {tools.Count} option windows built. " + string.Join("; ", problems.Take(10)));
    }

    private static InputsWindow Window(IRevitTool tool, IReadOnlyDictionary<string, string>? remembered)
    {
        var form = ToolForm.Fields(tool.PropertiesJson, tool.Required)
            .Select(f => new InputsWindow.Field(f.Key, f.Label, f.Help, f.Type, f.Required, f.Choices, ToolForm.Initial(f, remembered), f.ElementIds)).ToList();
        return new InputsWindow(ToolForm.Label(tool.Name), "Field test", null, [], form, "Run");
    }

    private static HashSet<string> Texts(System.Windows.DependencyObject? root)
    {
        var texts = new HashSet<string>(StringComparer.Ordinal);
        void Walk(object node)
        {
            if (node is System.Windows.Controls.TextBox box) texts.Add(box.Text);
            if (node is System.Windows.DependencyObject d)
                foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        if (root is not null) Walk(root);
        return texts;
    }

    /// <summary>A ribbon tool pinned to My tools, found there, and unpinned (always unpinned again).</summary>
    private static (string, string) FavouriteRoundTrip()
    {
        var pinned = FavouritesRibbon.Pinned;
        if (pinned.Count >= Favourites.Max)
        {
            // Full: the same steps on a list in memory, so the user's pins are not touched.
            var list = pinned.ToList();
            var key0 = FavouritesRibbon.Catalog.Keys.FirstOrDefault(k => !list.Contains(k));
            return Favourites.Pin(list, key0 ?? "tool:x")
                ? Fail("pinning a 13th item was allowed")
                : Pass($"My tools is full ({Favourites.Max}); a 13th pin is refused as it should be. Unpin one to check pin and unpin on the ribbon.");
        }
        var key = FavouritesRibbon.Catalog.Keys.FirstOrDefault(k => k.StartsWith("tool:", StringComparison.Ordinal) && !pinned.Contains(k));
        if (key is null) return Fail("nothing to pin: the catalog of ribbon tools is empty");
        try
        {
            if (FavouritesRibbon.SetPinned(key, true) is { } problem) return Fail($"pinning {key} failed: {problem}");
            if (!FavouritesRibbon.Pinned.Contains(key) || FavouritesRibbon.Shown.All(i => i.Key != key)) return Fail($"{key} was pinned but is not shown on My tools");
            if (FavouritesRibbon.SetPinned(key, false) is { } again) return Fail($"unpinning {key} failed: {again}");
            if (FavouritesRibbon.Pinned.Contains(key)) return Fail($"{key} is still pinned after unpinning");
            return Pass($"pinned {key} to My tools ({(FavouritesRibbon.IsLive ? "live on the ribbon" : "saved; the ribbon shows it after a restart")}) and unpinned it");
        }
        finally
        {
            if (FavouritesRibbon.Pinned.Contains(key) && !pinned.Contains(key)) FavouritesRibbon.SetPinned(key, false);
        }
    }

    // ---- Chat

    private sealed class ClaudeCliInstall(Chat.ClaudeCli.Install? install)
    {
        public Chat.ClaudeCli.Install? Install { get; } = install;

        public (string, string) Row() => Install is { } i
            ? Pass($"Claude Code {i.Version} at {i.Path}")
            : Fail("Claude Code was not found where the chat looks (~\\.local\\bin, %LOCALAPPDATA%\\Programs\\claude, WinGet, PATH, npm). Open Chat: it offers to install it.");
    }

    private static (string, string) SignIn(ClaudeCliInstall? cli)
    {
        if (cli?.Install is not { } install) return Fail("Claude Code is not installed (see chat.cli), so its sign-in can't be read");
        var (state, method) = Chat.ClaudeCli.CheckSignIn(install);
        return state switch
        {
            Chat.ClaudeCli.SignIn.SignedIn => Pass("signed in" + (method.Length > 0 ? $" ({method})" : "")),
            Chat.ClaudeCli.SignIn.SignedOut => Fail("signed out: open Chat and sign in with your Claude account"),
            _ => Pass("this Claude Code doesn't report its sign-in (older version); the chat's first message shows whether it works"),
        };
    }

    private static (string, string) RoundTrip(ClaudeCliInstall? cli, bool wanted)
    {
        if (!wanted)
            return ("manual", "manual (checklist): off by default because it uses your Claude plan; the checklist's \"Chat in the pane\" step covers it. Run with chat_round_trip true to test it here.");
        if (cli?.Install is not { } install) return Fail("Claude Code is not installed (see chat.cli)");
        var (code, output) = Chat.ClaudeCli.Run(install.Path, "-p \"Reply with the single word OK.\"", 90000);
        return code == 0 && output.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0
            ? Pass("one short message answered: " + Short(output.Trim(), 120))
            : Fail($"no answer (exit {code}): {Short(output.Trim(), 300)}");
    }

    private static (string, string) Models(string folder)
    {
        var all = Chat.ChatModels.All;
        if (all.Count == 0 || Chat.ChatModels.Find(null).Id != Chat.ChatModels.DefaultId) return Fail("the model list is empty or has no default");
        var temp = Path.Combine(folder, "chat-model");
        var pick = all.Last().Id;
        Chat.ChatModels.Save(temp, pick);
        var back = Chat.ChatModels.Load(temp);
        return back == pick
            ? Pass($"{all.Count} models ({string.Join(", ", all.Select(m => m.Label))}); default {Chat.ChatModels.DefaultId}; a choice is saved and read back")
            : Fail($"saved {pick} but read back {back}");
    }

    private static (string, string) ChatHistoryRoundTrip(string folder)
    {
        var root = Path.Combine(folder, "chats");
        Chat.ChatStore.RootOverride = root;
        try
        {
            var conv = Chat.ChatStore.New("Field test model", "Field test model");
            conv.Items.Add(new Chat.ChatItem { Kind = "user", Text = "Field test chat", Time = DateTime.UtcNow });
            Chat.ChatStore.Save(conv);
            var all = Chat.ChatStore.All();
            var summaries = Chat.ChatStore.Summaries();
            if (all.Count != 1 || all[0].Items.Count != 1 || summaries.Count != 1) return Fail($"saved one chat but read back {all.Count} ({summaries.Count} listed)");
            if (!Chat.ChatStore.Delete(all[0].File) || Chat.ChatStore.All().Count != 0) return Fail("the saved chat could not be deleted");
            return Pass($"a chat was saved, listed as \"{summaries[0].Name}\" and deleted (in a temporary history folder)");
        }
        finally { Chat.ChatStore.RootOverride = null; }
    }

    private static (string, string) Prompts(string folder)
    {
        var file = Path.Combine(folder, "prompts.json");
        const string text = "Field test saved prompt";
        var (added, saved) = Chat.SavedPrompts.Update(file, list => Chat.SavedPrompts.Add(list, text, DateTime.Now));
        if (!saved || !Chat.SavedPrompts.Load(file).Any(p => p.Text == text)) return Fail("a saved prompt was not read back");
        Chat.SavedPrompts.Update(file, list => Chat.SavedPrompts.Remove(list, text));
        var mine = Chat.SavedPrompts.Load(Chat.SavedPrompts.DefaultFile).Count;
        return Chat.SavedPrompts.Load(file).Count == 0
            ? Pass($"a prompt was added, read back and removed (temporary file); you have {mine} saved prompt(s)")
            : Fail("the prompt was not removed");
    }

    private static (string, string) Notes()
    {
        var sections = Chat.MemoryNotes.Parse("");
        Chat.MemoryNotes.Add(sections, null, "Field test global note");
        Chat.MemoryNotes.Add(sections, "Field test model", "Field test model note");
        var back = Chat.MemoryNotes.Parse(Chat.MemoryNotes.Write(sections));
        var (global, model) = Chat.MemoryNotes.For(back, "Field test model");
        var yours = Chat.MemoryNotes.Load();
        return global.Contains("Field test global note") && model.Contains("Field test model note")
            ? Pass($"notes were written and read back (in memory); your Notes file has {yours.Sum(x => x.Notes.Count)} note(s) and was only read")
            : Fail("a note written was not read back");
    }

    // ---- Bug log, updates, the Claude app

    private static (string, string) BugSync(string folder)
    {
        string published;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SketchProMax-field-test");
            published = http.GetStringAsync(Bugs.BugLog.PublishedUrl).GetAwaiter().GetResult();
        }
        if (!Bugs.BugLog.LooksLikeLog(published)) return Fail("the published bug log did not download as a bug log");
        // A report the log can't list yet, in a temporary Reports folder.
        var reports = Path.Combine(folder, "bug-reports");
        Directory.CreateDirectory(reports);
        var zip = Path.Combine(reports, "Bug report 2099-01-01 0000.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(archive.CreateEntry("description.txt").Open(), new UTF8Encoding(false)))
            w.Write("Field test report");
        var unsent = Bugs.BugLog.Unsent(published, Bugs.BugLog.Reports(reports));
        var text = Bugs.BugLog.Compose(published, unsent, DateTime.Now);
        var rows = FieldTestCoverage.FixedBugs(published).Count;
        return unsent.Count == 1 && text.Contains("## Not sent yet") && text.Contains("Bug report 2099-01-01 0000.zip") && text.Contains("Field test report")
            ? Pass($"downloaded the bug log ({rows} fixed rows); a report it doesn't list shows under \"Not sent yet\"; {Bugs.BugLog.UnsentReports().Count} of your reports are not sent yet")
            : Fail("a report the log doesn't list is missing from \"Not sent yet\"");
    }

    /// <summary>The releases page's redirect names the latest version, as the updater reads it when GitHub's API is rate-limited.</summary>
    private static ((string, string) Row, string? Tag) Endpoint()
    {
        var url = $"https://github.com/{Update.Updater.Repository}/releases/latest";
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SketchProMax-field-test");
        using var response = http.GetAsync(url).GetAwaiter().GetResult();
        var location = response.Headers.Location?.ToString() ?? "";
        var m = Regex.Match(location, @"/tag/v?(\d+\.\d+\.\d+)");
        return m.Success
            ? (Pass($"{url} answers {(int)response.StatusCode} and names {m.Groups[1].Value}"), m.Groups[1].Value)
            : (Fail($"{url} answered {(int)response.StatusCode} without naming a release ({Short(location, 120)})"), null);
    }

    private static (string, string) VersionCompare(string? latest)
    {
        // The comparison the updater makes: numeric, so 0.31.10 is newer than 0.31.9.
        if (!(new Version(0, 31, 10) > new Version(0, 31, 9))) return Fail("version compare is not numeric");
        if (latest is null || !Version.TryParse(latest, out var l)) return Fail("the latest release could not be read (see update.endpoint)");
        var current = Update.Updater.Current;
        var newer = new Version(l.Major, l.Minor, Math.Max(0, l.Build)) > current;
        return Pass($"this is {Update.Updater.CurrentText}, the latest release is {latest}: {(newer ? "an update is available" : "up to date")}. The Update button says: {Update.Updater.Describe()}");
    }

    private static (string, string) Loader()
    {
        var path = AppDomain.CurrentDomain.GetData("SketchProMax.Loader.Path") as string;
        var contract = AppDomain.CurrentDomain.GetData("SketchProMax.Loader.Contract");
        var release = AppDomain.CurrentDomain.GetData(LoaderCompat.ReleaseKey) as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Fail("this core was not started by the loader (no loader path), so updates need a Revit restart");
        var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return Pass($"loader {path}, file version {version}, release {release ?? "before 0.30"}, contract {contract ?? "?"}" +
                    (LoaderCompat.RibbonOutdated(release) ? "; it is older than 0.30, so new ribbon buttons need a restart" : ""));
    }

    private static (string, string) Msix()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(local, "Packages");
        var family = new Regex(@"(^|\.)Claude_[a-z0-9]+$", RegexOptions.IgnoreCase);
        var store = Directory.Exists(packages) ? Directory.GetDirectories(packages).Select(Path.GetFileName).Where(p => p is not null && family.IsMatch(p)).ToList() : [];
        var installer = new[] { Path.Combine(local, "AnthropicClaude", "claude.exe"), Path.Combine(local, "Programs", "Claude", "Claude.exe") }.FirstOrDefault(File.Exists);
        var registered = Update.ClaudeRegistration.Registered();
        var found = store.Count > 0 ? $"Microsoft Store Claude app found ({string.Join(", ", store)})" : "no Microsoft Store Claude app";
        return Pass($"{found}; normal installer: {installer ?? "not found"}; the connector is listed in {registered.Count} Claude config file(s)" +
                    (store.Count > 0 && !Update.ClaudeRegistration.ConfigFolders().Any(f => f.IndexOf("Packages", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? "; but the Store app's config folder was not found" : ""));
    }

    private static (string, string) Diagnostics(RunReport result)
    {
        var self = result.Rows.FirstOrDefault(r => r.Tool == "run_self_test");
        var items = FieldTestCoverage.Checklist([]).Count;
        var manifest = CoreRibbonManifest.Text;
        var missing = new[] { "self-test", "field-test", "test-checklist" }.Where(c => !manifest.Contains("\t" + c + "\t")).ToList();
        if (missing.Count > 0) return Fail("the ribbon doesn't declare " + string.Join(", ", missing));
        return Pass($"self-test: {(self is null ? "not run in this report" : self.Status)}; field test: this report; the test checklist has {items} manual steps plus the fixed bugs");
    }

    // ---- Coverage --------------------------------------------------------------------------------------------------------

    internal sealed record CoverageRow(string Name, string Kind, string Status, string Detail);

    /// <summary>Every registered tool and every declared feature, with what this run proved: tested, manual (checklist), or not run.</summary>
    private static List<CoverageRow> Coverage(RunReport r)
    {
        var list = new List<CoverageRow>();
        string Of(IEnumerable<Row> rows, string missing)
        {
            var mine = rows.ToList();
            if (mine.Count == 0) return missing;
            if (mine.Any(x => x.Status == "fail")) return "tested: FAIL";
            if (mine.Any(x => x.Status == "pass")) return "tested";
            if (mine.Any(x => x.Status == "manual")) return "manual (checklist)";
            return "not run";
        }
        foreach (var tool in r.Tools.OrderBy(t => t))
        {
            var rows = r.Rows.Where(x => x.Tool == tool || x.Tool.StartsWith(tool + " #", StringComparison.Ordinal)).ToList();
            var status = Of(rows, FieldTestCoverage.ManualTools.ContainsKey(tool) ? "manual (checklist)" : "not run");
            var detail = FieldTestCoverage.ManualTools.TryGetValue(tool, out var m) ? m.Reason
                : rows.Count == 0 ? "no row in this run" : $"{rows.Count} case(s)" + (rows.FirstOrDefault(x => x.Status != "pass") is { } odd ? ": " + Short(odd.Message, 160) : "");
            list.Add(new(tool, "tool", status, detail));
        }
        foreach (var f in FieldTestCoverage.Features)
        {
            var rows = f.How switch
            {
                FieldTestCoverage.How.Probe => r.Rows.Where(x => x.Tool == f.Key),
                FieldTestCoverage.How.Case => r.Rows.Where(x => x.Tool == f.By || x.Tool.StartsWith(f.By + " #", StringComparison.Ordinal)),
                _ => [],
            };
            var status = f.How == FieldTestCoverage.How.Manual ? "manual (checklist)" : Of(rows, "not run");
            list.Add(new(f.Key, "feature", status, f.How == FieldTestCoverage.How.Case ? $"{f.Reason} (by {f.By})" : f.Reason));
        }
        return list;
    }

    private static string CoverageMarkdown(List<CoverageRow> rows)
    {
        var md = new StringBuilder("## Coverage\n\n");
        int Count(string s) => rows.Count(x => x.Status == s);
        md.Append($"{rows.Count(x => x.Kind == "tool")} tools and {rows.Count(x => x.Kind == "feature")} features: {Count("tested") + Count("tested: FAIL")} tested " +
                  $"({Count("tested: FAIL")} failing), {Count("manual (checklist)")} manual (checklist: Sketch ProMax tab > Help > Test checklist), {Count("not run")} not run in this report.\n\n");
        md.Append("| Tool or feature | Kind | Coverage | Detail |\n|---|---|---|---|\n");
        foreach (var row in rows.OrderBy(x => x.Status == "tested: FAIL" ? 0 : x.Status == "not run" ? 1 : x.Status.StartsWith("manual", StringComparison.Ordinal) ? 2 : 3).ThenBy(x => x.Kind).ThenBy(x => x.Name))
            md.Append($"| {row.Name} | {row.Kind} | {(row.Status == "tested" ? "tested" : "**" + row.Status + "**")} | {Cell(row.Detail)} |\n");
        return md.ToString();
    }
}
