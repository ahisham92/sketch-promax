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
using SketchProMax.Chat;
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
            rows.Add(new("ve", "whatif.copy", "fail", 0, "no copy to run it in (see the Test project notes)"));
            rows.Add(new("ve", "whatif.link", "fail", 0, "no copy to run it in (see the Test project notes)"));
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
                rows.AddRange(WhatIfOnCopy(app, s, active));
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

    // ---- Cost plan and what if: what_if on a copy of the batch copy, then the cost plan's options read it

    /// <summary>The small rate table the cost probes use, written here as .xlsx in the test folder (two regions, a row in EUR).</summary>
    private static string FieldRates(string folder)
    {
        var file = Path.Combine(folder, "Field test rates.xlsx");
        if (!File.Exists(file)) Xlsx.Write(file, [("Rates", Cost.SampleTable())]);
        return file;
    }

    private static readonly Dictionary<string, double> FieldExchange = new() { ["EUR"] = 1.1 };

    private static Floor? FirstSlab(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => !f.FloorType.IsFoundationSlab && f.FloorType.GetCompoundStructure() is not null)
            .OrderBy(f => f.Id.Value).FirstOrDefault();

    /// <summary>A plain value of a result, for the probes' checks ("" when missing).</summary>
    private static string Prop(JsonElement e, params string[] path)
    {
        foreach (var p in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return "";
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();
    }

    private static double Num(JsonElement e, params string[] path) =>
        double.TryParse(Prop(e, path), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    /// <summary>what_if confirmed on a copy of the open batch copy (not workshared), then cost_estimate options [model, what_if].</summary>
    private static List<Row> WhatIfOnCopy(UIApplication app, State s, Document active)
    {
        var rows = new List<Row>();
        var watch = Stopwatch.StartNew();
        var rates = FieldRates(s.Folder);
        var slab = FirstSlab(active);
        if (slab is null)
        {
            rows.Add(new("ve", "whatif.copy", "fail", 0, "the copy has no slab with layers to make thicker"));
            rows.Add(new("ve", "whatif.link", "fail", 0, "no what-if to compare (see whatif.copy)"));
            return rows;
        }
        var typeBefore = slab.GetTypeId();
        var pathBefore = active.PathName;
        var modifiedBefore = active.IsModified;
        var thick = Math.Round(slab.FloorType.GetCompoundStructure()!.GetWidth() * 304.8) + 100;
        var copy = Path.Combine(s.BatchFolder!, "FieldTest what if copy.rvt");
        try
        {
            var res = Call(app, s.Registry, "what_if", new
            {
                confirm = true, copy_path = copy, name = "field test", rates_file = rates, region = "North", currency = "USD", exchange_rates = FieldExchange, folder = s.Folder,
                changes = new[] { new { kind = "slab_thickness", element_ids = new[] { slab.Id.Value }, thickness_mm = thick } },
            });
            if (res.IsError) rows.Add(new("ve", "whatif.copy", "fail", watch.ElapsedMilliseconds, Short(res.Text, 600)));
            else
            {
                using var j = JsonDocument.Parse(res.Text);
                var side = j.RootElement.GetProperty("sideBySide").EnumerateArray().ToList();
                double Change(string metric) => side.Where(x => Prop(x, "Metric").StartsWith(metric, StringComparison.Ordinal)).Select(x => Num(x, "change")).DefaultIfEmpty(double.NaN).First();
                var xlsx = Prop(j.RootElement, "files", "xlsx");
                var problems = new List<string>();
                if (!File.Exists(copy)) problems.Add($"the copy {copy} is not there");
                if (!(Change("Concrete") > 0)) problems.Add($"concrete change {Change("Concrete")} m³, expected more");
                if (!(Change("Cost") > 0)) problems.Add($"cost change {Change("Cost")}, expected more");
                if (xlsx.Length == 0 || !File.Exists(xlsx)) problems.Add("no Excel file");
                else
                {
                    using var zip = ZipFile.OpenRead(xlsx);
                    using var r = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
                    if (!r.ReadToEnd().Contains("Drawings affected")) problems.Add("the Excel file has no Drawings affected sheet");
                }
                if (active.GetElement(slab.Id) is not Floor still || still.GetTypeId() != typeBefore) problems.Add("the open model's slab changed type");
                // Never Save or SaveAs on the open model: its path and its saved state stay as they were.
                if (active.PathName != pathBefore) problems.Add($"the open model's path changed to {active.PathName}");
                if (active.IsModified != modifiedBefore) problems.Add("the open model's saved state changed");
                rows.Add(new("ve", "whatif.copy", problems.Count == 0 ? "pass" : "fail", watch.ElapsedMilliseconds,
                    (problems.Count == 0 ? $"Slab {slab.Id.Value} to {thick} mm on {Path.GetFileName(copy)}: concrete {Ve.F(Change("Concrete"), 2)} m³, cost {Cost.Money(Change("Cost"))} USD, " +
                                           $"{Prop(j.RootElement, "drawingsAffected", "views")} view(s) and {Prop(j.RootElement, "drawingsAffected", "sheets")} sheet(s) affected; the open model kept its slab type. "
                        : string.Join("; ", problems) + ". ") + Short(Prop(j.RootElement, "summaryCard"), 300)));
            }

            var link = Stopwatch.StartNew();
            var cost = Call(app, s.Registry, "cost_estimate", new { rates_file = rates, region = "North", currency = "USD", exchange_rates = FieldExchange, options = new[] { "model", "what_if:field test" }, folder = s.Folder, excel = false });
            if (cost.IsError) rows.Add(new("ve", "whatif.link", "fail", link.ElapsedMilliseconds, Short(cost.Text, 600)));
            else
            {
                using var j = JsonDocument.Parse(cost.Text);
                var opts = j.RootElement.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array ? o.EnumerateArray().ToList() : [];
                var ok = opts.Count == 2 && Prop(opts[1], "option").StartsWith("What if", StringComparison.Ordinal) && Num(opts[1], "costChange") > 0;
                rows.Add(new("ve", "whatif.link", ok ? "pass" : "fail", link.ElapsedMilliseconds,
                    ok ? $"cost_estimate options: {Prop(opts[0], "option")} {Prop(opts[0], "total")} vs {Prop(opts[1], "option")} {Prop(opts[1], "total")} ({Prop(opts[1], "costPercent")}%), {Prop(opts[1], "tCO2e")} tCO2e, {Prop(opts[1], "frameWeeks")} weeks"
                       : "the options don't set the what-if against the model with a higher cost: " + Short(cost.Text, 500)));
            }
        }
        finally
        {
            try { Directory.Delete(CostRun.Store(active), true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return rows;
    }

    /// <summary>The documented template: written by cost_estimate, read back, its rows chosen by region.</summary>
    private static (string, string) CostTemplate(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is null) return Fail(why);
        var path = Path.Combine(s.Folder, "Rate table template.xlsx");
        var r = Call(app, s.Registry, "cost_estimate", new { template = true, template_path = path });
        if (r.IsError) return Fail(Short(r.Text, 500));
        if (!File.Exists(path)) return Fail($"cost_estimate said it wrote the template, but {path} is not there");
        var sel = Cost.SelectRates(Table.Read(path, out var sep), "Region B", null, null, sep);
        using var zip = ZipFile.OpenRead(path);
        var sheets = zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal));
        var c01 = sel.Rows.FirstOrDefault(x => x.Ref == "C-01");
        return sheets == 2 && c01?.Rate == 175
            ? Pass($"{Path.GetFileName(path)}: Rates and Read me sheets; read back, {sel.Rows.Count} rates for Region B in {sel.Currency} (C-01 at its own rate {c01.Rate})")
            : Fail($"{sheets} sheet(s); C-01 for Region B is {c01?.Rate.ToString() ?? "missing"}, expected 175");
    }

    /// <summary>A cost plan of the test project from the small rate-table xlsx, with every add-on.</summary>
    private static (string, string) CostPlan(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is null) return Fail(why);
        var rates = FieldRates(s.Folder);
        var r = Call(app, s.Registry, "cost_estimate", new
        {
            rates_file = rates, region = "North", currency = "USD", exchange_rates = FieldExchange, prelims_percent = 10.0, contingency_percent = 5.0,
            escalation_percent_per_year = 4.0, escalation_from = "2026-01-01", escalation_to = "2028-01-01", vat_percent = 5.0, quay_length_m = 120.0, folder = s.Folder,
        });
        if (r.IsError) return Fail(Short(r.Text, 600));
        using var j = JsonDocument.Parse(r.Text);
        var root = j.RootElement;
        var problems = new List<string>();
        var addOns = root.GetProperty("addOns").EnumerateArray().ToList();
        var works = Num(addOns[0], "amount");
        var expected = Cost.Apply(works, new Cost.AddOns { PrelimsPct = 10, ContingencyPct = 5, EscalationPctPerYear = 4, BaseDate = new DateTime(2026, 1, 1), TargetDate = new DateTime(2028, 1, 1), VatPct = 5 }).Total;
        var total = Num(root, "total");
        if (!(works > 0)) problems.Add("nothing was measured (measured works 0)");
        if (Math.Abs(total - expected) > 1) problems.Add($"total {total} is not works {works} with the add-ons ({expected:0.##})");
        var items = root.GetProperty("items").EnumerateArray().ToList();
        double Rate(string reference) => items.Where(i => Prop(i, "Ref") == reference).Select(i => Num(i, "Rate")).DefaultIfEmpty(double.NaN).First();
        if (Rate("T-01") != 120) problems.Add($"slab concrete at {Rate("T-01")}, not North's 120");
        if (Math.Abs(Rate("T-02") - 220) > 1e-6) problems.Add($"walls at {Rate("T-02")}, not 200 EUR × 1.1");
        var sections = root.GetProperty("perUnit").EnumerateArray().Select(u => Prop(u, "Section")).ToList();
        foreach (var want in new[] { "Per m² GFA", "Per m³ of concrete", "Per linear m" })
            if (!sections.Contains(want)) problems.Add($"no '{want}' rate");
        var card = Prop(root, "summaryCard");
        if (card.Length == 0 || card.Split('\n').Length > 10 || !card.Contains("Total")) problems.Add("the summary card is missing or long");
        if (!Prop(root, "ratesFrom").Contains("remembered")) problems.Add("the rate table was not remembered");
        var xlsx = Prop(root, "files", "xlsx");
        if (xlsx.Length == 0 || !File.Exists(xlsx)) problems.Add("no Excel cost plan");
        else
        {
            using var zip = ZipFile.OpenRead(xlsx);
            string Text(string e) { using var sr = new StreamReader(zip.GetEntry(e)!.Open()); return sr.ReadToEnd(); }
            var book = Text("xl/workbook.xml");
            var plan = Text("xl/worksheets/sheet1.xml");
            if (!book.Contains("\"Basis\"") || !book.Contains("\"Cost plan\"")) problems.Add("the workbook has no Cost plan or Basis sheet");
            if (!plan.Contains("SUM(Basis!H") || !plan.Contains("HYPERLINK")) problems.Add("the cost plan's lines don't link to their Basis rows");
        }
        return problems.Count == 0
            ? Pass($"Total {Cost.Money(total, "USD")} = works {Cost.Money(works)} with prelims 10%, contingency 5%, escalation 4%/yr 2026-01 to 2028-01, VAT 5%; GFA {Prop(root, "gfa", "m2")} m² from {Prop(root, "gfa", "source")}; " +
                   $"zones {Prop(root, "zones")}; {sections.Count} rates per unit; Excel {Path.GetFileName(xlsx)} links lines to Basis rows. Card: {Short(card.Replace('\n', ' '), 200)}")
            : Fail(string.Join("; ", problems) + ". " + Short(r.Text, 300));
    }

    /// <summary>A second run with no table: the one remembered for the test project, its region, currency and add-ons.</summary>
    private static (string, string) CostRemembered(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is null) return Fail(why);
        var r = Call(app, s.Registry, "cost_estimate", new { folder = s.Folder, excel = false });
        if (r.IsError) return Fail(Short(r.Text, 500));
        using var j = JsonDocument.Parse(r.Text);
        var from = Prop(j.RootElement, "ratesFrom");
        var prelims = j.RootElement.GetProperty("addOns").EnumerateArray().Select(a => Prop(a, "Label")).FirstOrDefault(l => l.StartsWith("Preliminaries", StringComparison.Ordinal)) ?? "";
        var slab = j.RootElement.GetProperty("items").EnumerateArray().Where(i => Prop(i, "Ref") == "T-01").Select(i => Num(i, "Rate")).DefaultIfEmpty(double.NaN).First();
        return from.Contains("remembered for this model") && Prop(j.RootElement, "region") == "North" && slab == 120 && prelims == "Preliminaries 10%"
            ? Pass($"Used {from}, region North, {Prop(j.RootElement, "currency")}, {prelims}")
            : Fail($"rates from '{from}', region '{Prop(j.RootElement, "region")}', slab rate {slab}, '{prelims}'");
    }

    /// <summary>The model against a flat slab and a steel frame swapped in for its floors: cost, carbon and frame weeks side by side.</summary>
    private static (string, string) CostOptions(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        try
        {
            var r = Call(app, s.Registry, "cost_estimate", new { options = new[] { "model", "flat_slab", "steel_composite" }, folder = s.Folder, excel = false });
            if (r.IsError) return Fail(Short(r.Text, 500));
            using var j = JsonDocument.Parse(r.Text);
            var opts = j.RootElement.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array ? o.EnumerateArray().ToList() : [];
            var ok = opts.Count == 3 && Num(opts[0], "costChange") == 0 && opts.All(x => !double.IsNaN(Num(x, "tCO2e")) && Num(x, "frameWeeks") >= 0)
                     && opts.Any(x => Prop(x, "best").Contains("cheapest"));
            return ok
                ? Pass(string.Join("; ", opts.Select(x => $"{Prop(x, "option")}: {Prop(x, "total")} ({Prop(x, "costPercent")}%), {Prop(x, "tCO2e")} tCO2e, {Prop(x, "frameWeeks")} wk{(Prop(x, "best").Length > 0 ? ", " + Prop(x, "best") : "")}")))
                : Fail("expected 3 options against the first with carbon, weeks and a cheapest: " + Short(r.Text, 500));
        }
        finally
        {
            // The table and add-ons remembered for the throwaway test project go with it.
            try { Directory.Delete(CostRun.Store(doc), true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>what_if without confirm: a preview with the plan, the figures and the drawings; nothing in the model changes.</summary>
    private static (string, string) WhatIfPreview(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var slab = FirstSlab(doc);
        if (slab is null) return Fail("the test project has no slab with layers");
        var type = slab.GetTypeId();
        var count = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
        var r = Call(app, s.Registry, "what_if", new { changes = new[] { new { kind = "slab_thickness", element_ids = new[] { slab.Id.Value }, thickness_mm = 275.0 } }, folder = s.Folder });
        if (r.IsError) return Fail(Short(r.Text, 500));
        using var j = JsonDocument.Parse(r.Text);
        var after = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
        var problems = new List<string>();
        if (Prop(j.RootElement, "preview") != "True" && Prop(j.RootElement, "preview") != "true") problems.Add("not a preview");
        if (Prop(j.RootElement, "ask").Length == 0) problems.Add("no question for the user");
        if (!j.RootElement.TryGetProperty("drawingsAffected", out _)) problems.Add("no drawings affected");
        if (after != count) problems.Add($"the model's element count went from {count} to {after}");
        if (doc.GetElement(slab.Id) is not Floor f || f.GetTypeId() != type) problems.Add("the slab changed type");
        return problems.Count == 0
            ? Pass($"Preview of slab {slab.Id.Value} at 275 mm: {Prop(j.RootElement, "drawingsAffected", "views")} view(s), {Prop(j.RootElement, "drawingsAffected", "sheets")} sheet(s) affected; " +
                   $"current {Prop(j.RootElement, "current", "concreteM3")} m³, {Prop(j.RootElement, "current", "tCO2e")} tCO2e; {(Prop(j.RootElement, "refused").Length > 0 ? $"the run would be refused here ({Short(Prop(j.RootElement, "refused"), 80)}); " : "")}nothing changed")
            : Fail(string.Join("; ", problems) + ". " + Short(r.Text, 300));
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
        await Probe(s, "chat.survives_update", _ => ChatSurvivesUpdate(s.Folder)).ConfigureAwait(false);
        await Probe(s, "chat.recall", _ => ChatRecallProbe(s.Folder)).ConfigureAwait(false);
        await Probe(s, "chat.dpi", _ => ChatDpi(s)).ConfigureAwait(false);
        await ProbeCore(s, "chat.frames", () => ChatFrames()).ConfigureAwait(false);
        await Probe(s, "chat.composer", _ => ChatComposer()).ConfigureAwait(false);
        await Probe(s, "update.loader", _ => Loader()).ConfigureAwait(false);
        await ProbeCore(s, "revit.blocked", RevitBlockedCard).ConfigureAwait(false);
        // In the test project, each rolled back afterwards (time records excepted: they are what is checked).
        await Probe(s, "handoff.continue", app => HandoffContinue(app, s)).ConfigureAwait(false);
        await Probe(s, "handoff.skip_stop", app => HandoffSkipStop(app, s)).ConfigureAwait(false);
        await Probe(s, "handoff.static", app => HandoffStatic(app, s)).ConfigureAwait(false);
        await Probe(s, "cancel.fast", app => CancelFast(app, s)).ConfigureAwait(false);
        await Probe(s, "job.progress", app => JobProgress(app, s)).ConfigureAwait(false);
        await Probe(s, "job.pause_resume", app => JobPauseResume(app, s)).ConfigureAwait(false);
        await Probe(s, "job.stop", app => JobStop(app, s)).ConfigureAwait(false);
        await Probe(s, "job.skip", app => JobSkip(app, s)).ConfigureAwait(false);
        await Probe(s, "job.message", app => JobMessage(app, s)).ConfigureAwait(false);
        await Probe(s, "job.thinking", _ => JobThinking()).ConfigureAwait(false);
        await Probe(s, "approvals.flow", app => ApprovalFlow(app, s)).ConfigureAwait(false);
        await Probe(s, "time.tracking", app => TimeTracking(app, s)).ConfigureAwait(false);
        await Probe(s, "time.export", app => TimeExport(app, s)).ConfigureAwait(false);
        await Probe(s, "time.queue", app => TimeQueue(app, s)).ConfigureAwait(false);
        await Probe(s, "issue.gate", app => IssueGateProbe(app, s)).ConfigureAwait(false);
        await Probe(s, "package.sentence", app => PackageSentence(app, s)).ConfigureAwait(false);
        await Probe(s, "package.skip", app => PackageSkip(app, s)).ConfigureAwait(false);
        await Probe(s, "redlines.pdf", app => RedlinePdf(app, s)).ConfigureAwait(false);
        await Probe(s, "redlines.pipeline", app => RedlinePipeline(app, s)).ConfigureAwait(false);
        await Probe(s, "cost.template", app => CostTemplate(app, s)).ConfigureAwait(false);
        await Probe(s, "cost.plan", app => CostPlan(app, s)).ConfigureAwait(false);
        await Probe(s, "cost.remembered", app => CostRemembered(app, s)).ConfigureAwait(false);
        await Probe(s, "cost.options", app => CostOptions(app, s)).ConfigureAwait(false);
        await Probe(s, "whatif.preview", app => WhatIfPreview(app, s)).ConfigureAwait(false);
        // Off Revit's thread: processes, files and the network.
        await Probe(s, "chat.cli", () => { cli = new ClaudeCliInstall(Chat.ClaudeCli.Find()); return cli.Row(); }).ConfigureAwait(false);
        await Probe(s, "chat.signin", () => SignIn(cli)).ConfigureAwait(false);
        await Probe(s, "chat.roundtrip", () => RoundTrip(cli, s.O.ChatRoundTrip)).ConfigureAwait(false);
        await Probe(s, "chat.shrink", ShrinkProbe).ConfigureAwait(false);
        await ProbeCore(s, "chat.recall_run", () => RecallRun(cli, s.O.ChatRoundTrip)).ConfigureAwait(false);
        await ProbeCore(s, "brief.plan", () => BriefPlan(s)).ConfigureAwait(false);
        await ProbeCore(s, "brief.choose", () => BriefChoose(s)).ConfigureAwait(false);
        await Probe(s, "redlines.vision", () => RedlineVision(s, cli)).ConfigureAwait(false);
        await Probe(s, "checklist.build", () => ChecklistBuild(s.Folder)).ConfigureAwait(false);
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

    /// <summary>
    /// Shrink chat without using the plan: the /compact line the pane sends, Claude Code's compaction and context-size
    /// lines read as the pane needs them, and when a chat shrinks by itself.
    /// </summary>
    private static (string, string) ShrinkProbe()
    {
        var problems = new List<string>();
        var line = Chat.StreamInput.Compact(Chat.ChatRecall.CompactInstructions);
        using (var doc = System.Text.Json.JsonDocument.Parse(line))
        {
            var content = doc.RootElement.GetProperty("message").GetProperty("content");
            if (content.ValueKind != System.Text.Json.JsonValueKind.String || !content.GetString()!.StartsWith("/compact ", StringComparison.Ordinal))
                problems.Add("the shrink message is not a plain /compact command: " + Short(line, 120));
        }
        var parser = new Chat.StreamParser();
        var events = parser.ParseLine("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","usage":{"input_tokens":12,"cache_read_input_tokens":70000,"cache_creation_input_tokens":500}}}}""");
        events.AddRange(parser.ParseLine("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual","pre_tokens":70512}}"""));
        if (events.OfType<Chat.ContextEvent>().SingleOrDefault() is not { Tokens: 70512 }) problems.Add("the size of a request was not read from its usage");
        if (events.OfType<Chat.CompactedEvent>().SingleOrDefault() is not { Trigger: "manual", PreTokens: 70512 }) problems.Add("the compaction line was not read");
        const long floor = 50_000; // an empty chat: the instructions and the Revit tools
        if (!Chat.ChatRecall.ShouldAutoShrink(floor + Chat.ChatRecall.AutoShrinkTokens, floor) || Chat.ChatRecall.ShouldAutoShrink(floor + Chat.ChatRecall.AutoShrinkTokens - 1, floor))
            problems.Add("the automatic shrink doesn't start " + Chat.ChatRecall.AutoShrinkTokens + " tokens above an empty chat");
        return problems.Count == 0
            ? Pass($"Shrink sends Claude Code's /compact; the compaction and each request's size are read from the stream; a chat shrinks by itself once a message sends {Chat.ChatRecall.AutoShrinkTokens / 1000}k tokens more than the chat did empty (or {Chat.ChatRecall.AutoShrinkMaxTokens / 1000}k in all). Pressing Shrink in a real chat is in the checklist")
            : Fail(string.Join("; ", problems));
    }

    /// <summary>
    /// Chats that recall each other, in a temporary history folder: a new chat gets the summaries of the same model's
    /// other chats (not its own, not another model's), a search finds a chat of another model by its project's name,
    /// open gives its steps, and a chat is summarised again only when it has new messages.
    /// </summary>
    private static (string, string) ChatRecallProbe(string folder)
    {
        var root = Path.Combine(folder, "chats-recall");
        Chat.ChatStore.RootOverride = root;
        try
        {
            Chat.Conversation Make(string key, string request, string recall)
            {
                var c = Chat.ChatStore.New(key, key);
                c.Items.Add(new Chat.ChatItem { Kind = "user", Text = request, Time = DateTime.UtcNow });
                c.Items.Add(new Chat.ChatItem { Kind = "tool", Name = "create_columns", Text = "600x600 at grid crossings", Input = "{\"size\": \"600x600\"}", State = "done" });
                c.Items.Add(new Chat.ChatItem { Kind = "assistant", Text = "Done.", Time = DateTime.UtcNow });
                c.Recall = recall;
                c.RecallCount = recall.Length > 0 ? c.Items.Count : 0;
                Chat.ChatStore.Save(c);
                return c;
            }
            var a1 = Make("Field test Tin Can", "Columns on every grid", "- Columns are 600x600 C40 at every grid crossing");
            var a2 = Make("Field test Tin Can", "Now the beams", "");
            var b1 = Make("Field test Tower", "Pile caps for the Tower", "- Pile caps 1.5 m deep, 4 piles each");
            var problems = new List<string>();
            var block = Chat.ChatRecall.PromptBlock(Chat.ChatStore.ForModel(a2.Key), a2.Key, a2.Id);
            if (!block.Contains("600x600")) problems.Add("a new chat didn't get its model's earlier summary");
            if (block.Contains("Pile caps")) problems.Add("a new chat got another model's summary");
            if (Chat.ChatRecall.PromptBlock(Chat.ChatStore.ForModel(a1.Key), a1.Key, a1.Id).Contains("600x600")) problems.Add("a chat got its own summary back");
            var hits = Chat.ChatRecall.Search(Chat.ChatStore.All(), "the same way as in the Tower project");
            if (hits.FirstOrDefault()?.Chat.Id != b1.Id) problems.Add("searching \"the same way as in the Tower project\" didn't find the Tower chat first");
            var (summary, requests, steps) = Chat.ChatRecall.Details(b1);
            if (summary.Length == 0 || requests.Count != 1 || steps.Count != 1 || !steps[0].Contains("600x600")) problems.Add("opening a chat didn't give its summary, request and steps");
            if (Chat.ChatRecall.NeedsSummary(a1)) problems.Add("a chat with nothing new would be summarised again");
            if (!Chat.ChatRecall.NeedsSummary(a2)) problems.Add("a chat with no summary yet wouldn't be summarised");
            if (Chat.ChatRecall.CleanSummary("NOTHING").Length != 0 || Chat.ChatRecall.CleanSummary("Summary:\n* one\n- two") != "- one\n- two") problems.Add("a summary answer isn't kept as bullets");
            return problems.Count == 0
                ? Pass($"a new chat starts with its model's earlier summaries only; a search finds another model's chat by its project's name ({hits.Count} match(es)); open gives its summary, requests and steps; a chat is summarised again only with new messages (in a temporary history folder)")
                : Fail(string.Join("; ", problems));
        }
        finally { Chat.ChatStore.RootOverride = null; }
    }

    /// <summary>The real summary run on a short sample chat (uses the plan, so only with chat_round_trip).</summary>
    private static async Task<(string, string)> RecallRun(ClaudeCliInstall? cli, bool wanted)
    {
        if (!wanted)
            return ("manual", "manual (checklist): off by default because writing a summary uses your Claude plan; the checklist's \"Chats remember each other in the pane\" step covers it. Run with chat_round_trip true to test it here.");
        if (cli?.Install is not { } install) return Fail("Claude Code is not installed (see chat.cli)");
        var conv = new Chat.Conversation { Key = "Field test", Title = "Field test", Id = "fieldtest", Name = "Columns" };
        conv.Items.Add(new Chat.ChatItem { Kind = "user", Text = "Put 600x600 C40 columns at every grid crossing on Levels 1 to 4. We always use C40 for columns on this job." });
        conv.Items.Add(new Chat.ChatItem { Kind = "tool", Name = "create_columns", Text = "600x600 C40, Levels 1-4, 36 grid crossings", State = "done" });
        conv.Items.Add(new Chat.ChatItem { Kind = "assistant", Text = "Placed 144 columns, 600x600 C40, at the 36 grid crossings on Levels 1 to 4." });
        var (text, error) = await Chat.BriefRunner.Run(install, Chat.ChatRecall.SummaryInstructions, Chat.ChatRecall.Transcript(conv), readTools: false,
            Chat.ChatRecall.SummaryModel, Chat.BriefRunner.DistilTimeoutMs, CancellationToken.None).ConfigureAwait(false);
        if (error.Length > 0) return Fail("no summary: " + Short(error, 300));
        var summary = Chat.ChatRecall.CleanSummary(text);
        return summary.Length > 0 && summary.Contains("C40")
            ? Pass("a sample chat was summarised: " + Short(summary.Replace("\n", " "), 200))
            : Fail("the summary misses what the chat decided: " + Short(text, 300));
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

    /// <summary>
    /// The open chat across a version switch, as ChatPane does it: saved and handed over (Retire), then taken and reopened
    /// by the next pane. In a temporary history folder; a handover already waiting (none, normally) is put back after.
    /// </summary>
    private static (string, string) ChatSurvivesUpdate(string folder)
    {
        var root = Path.Combine(folder, "chats-update");
        var waiting = AppDomain.CurrentDomain.GetData(Chat.ChatHandover.DataKey);
        Chat.ChatStore.RootOverride = root;
        try
        {
            const string key = "Field test model (update)";
            var older = Chat.ChatStore.New(key, key);
            older.Items.Add(new Chat.ChatItem { Kind = "user", Text = "Field test open chat", Time = DateTime.UtcNow });
            older.SessionId = "field-test-session";
            Chat.ChatStore.Save(older);
            // A newer chat of the same model: the model's latest, which a fresh pane would open instead.
            var newer = Chat.ChatStore.New(key, key);
            newer.Items.Add(new Chat.ChatItem { Kind = "user", Text = "Field test newer chat", Time = DateTime.UtcNow });
            Chat.ChatStore.Save(newer);
            older.Updated = DateTime.UtcNow.AddDays(-1);
            Chat.ChatStore.Save(older, touch: false);
            older.Items.Add(new Chat.ChatItem { Kind = "assistant", Text = "Field test answer", Time = DateTime.UtcNow });
            Chat.ChatStore.Save(older, touch: false);
            Chat.ChatHandover.Leave(older, "Field test draft", []);
            var left = Chat.ChatHandover.Take();
            var back = Chat.ChatHandover.Open(left, key, key);
            var problems = new List<string>();
            if (!string.Equals(back.File, older.File, StringComparison.OrdinalIgnoreCase)) problems.Add($"reopened {Path.GetFileName(back.File)}, not the open chat {Path.GetFileName(older.File)}");
            if (back.Items.Count != 2 || back.Items[1].Text != "Field test answer") problems.Add($"{back.Items.Count} messages came back, not 2");
            if (back.SessionId != "field-test-session") problems.Add("the Claude Code session was not kept");
            if (left?.Draft != "Field test draft") problems.Add("the unsent message was not kept");
            if (Chat.ChatHandover.Take() is not null) problems.Add("the handover was not cleared after use");
            if (Chat.ChatStore.All().Count != 2) problems.Add($"{Chat.ChatStore.All().Count} chats on disk, not 2");
            return problems.Count == 0
                ? Pass("a chat with its session and an unsent message was saved, handed over as an update does, and the same chat reopened (in a temporary history folder)")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            Chat.ChatStore.RootOverride = null;
            AppDomain.CurrentDomain.SetData(Chat.ChatHandover.DataKey, waiting);
        }
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

    // ---- Hand-off, Cancel and time tracking (in the test project) --------------------------------------------------------

    /// <summary>The test project when it is open and the active model; else null and why.</summary>
    private static Document? TestDoc(UIApplication app, State s, out string why)
    {
        why = "";
        if (s.Test is not { IsValidObject: true } test) { why = "the test project is not open (see the Test project notes)"; return null; }
        if (app.ActiveUIDocument?.Document.Equals(test) != true) { why = "the test project is not the active model"; return null; }
        return test;
    }

    /// <summary>A tool called as the bridge calls it: a step only the user can do becomes a hand-off (answered by Handoff.TestResponder).</summary>
    private static ToolResult CallHandingOff(UIApplication app, ToolRegistry registry, string name, object args)
    {
        var tool = registry.Find(name) ?? throw new InvalidOperationException($"{name} is not in the registry");
        var dict = JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return Revit.FailureGuard.Run(() =>
        {
            try { return tool.Execute(app, new ToolArgs(dict)); }
            catch (ManualStepException manual) { return Handoff.FromException(tool, app, new ToolArgs(dict), manual); }
        });
    }

    /// <summary>The result's "handoff" section, one (choice, done) per step; null when it has none.</summary>
    private static List<(string Choice, bool Done)>? HandoffSteps(string text)
    {
        JsonElement? section = null;
        try
        {
            using var whole = JsonDocument.Parse(text);
            if (whole.RootElement.ValueKind == JsonValueKind.Object && whole.RootElement.TryGetProperty("handoff", out var h)) section = h.Clone();
        }
        catch (JsonException) { }
        if (section is null)
        {
            // A plain-text result carries it on its last line.
            var at = text.LastIndexOf("\nhandoff: ", StringComparison.Ordinal);
            if (at < 0) return null;
            try { using var tail = JsonDocument.Parse(text.Substring(at + "\nhandoff: ".Length)); section = tail.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        if (!section.Value.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return null;
        return steps.EnumerateArray().Select(x => (x.GetProperty("choice").GetString() ?? "", x.GetProperty("done").GetBoolean())).ToList();
    }

    private static string Steps(List<(string Choice, bool Done)>? steps) =>
        steps is null ? "no handoff section" : string.Join(", ", steps.Select(x => x.Choice + (x.Done ? " (done)" : "")));

    private sealed record DimSetup(ViewPlan Plan, List<ViewPlan> Targets, Autodesk.Revit.DB.Grid A, Autodesk.Revit.DB.Grid B, Dimension Dim);

    /// <summary>Inside the probe's transaction group: two grids, a dimension between them in a floor plan, and copies of that plan.</summary>
    private static DimSetup? DimensionSetup(Document doc, int copies, out string why)
    {
        why = "";
        var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel is not null && v.CanViewBeDuplicated(ViewDuplicateOption.Duplicate));
        if (plan is null) { why = "the test project has no floor plan to dimension in"; return null; }
        using var t = new Transaction(doc, "Field test: grids and a dimension");
        t.Start();
        var a = Autodesk.Revit.DB.Grid.Create(doc, Line.CreateBound(new XYZ(-6, -10, 0), new XYZ(-6, 30, 0)));
        var b = Autodesk.Revit.DB.Grid.Create(doc, Line.CreateBound(new XYZ(4, -10, 0), new XYZ(4, 30, 0)));
        // New grids have no geometry in the plan until the model regenerates; a dimension to them before that is refused
        // ("The references are not geometric references").
        doc.Regenerate();
        var dim = Dimension(doc, plan, a, b);
        var targets = Enumerable.Range(0, copies).Select(_ => (ViewPlan)doc.GetElement(plan.Duplicate(ViewDuplicateOption.Duplicate))).ToList();
        t.Commit();
        return new DimSetup(plan, targets, a, b, dim);
    }

    private static Dimension Dimension(Document doc, View view, Autodesk.Revit.DB.Grid a, Autodesk.Revit.DB.Grid b)
    {
        var z = (view as ViewPlan)?.GenLevel?.ProjectElevation ?? 0;
        var line = Line.CreateBound(new XYZ(-10, 20, z), new XYZ(8, 20, z));
        var refs = new ReferenceArray();
        refs.Append(new Reference(a));
        refs.Append(new Reference(b));
        Dimension? dim;
        try { dim = doc.Create.NewDimension(view, line, refs); }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Some views take only the grid line's own reference, read from its geometry in that view.
            refs = new ReferenceArray();
            refs.Append(GridLineReference(a, view));
            refs.Append(GridLineReference(b, view));
            dim = doc.Create.NewDimension(view, line, refs);
        }
        return dim ?? throw new InvalidOperationException($"Revit made no dimension between the grids in '{view.Name}'");
    }

    private static Reference GridLineReference(Autodesk.Revit.DB.Grid grid, View view) =>
        grid.get_Geometry(new Autodesk.Revit.DB.Options { View = view, ComputeReferences = true, IncludeNonVisibleObjects = true })?
            .OfType<Line>().Select(l => l.Reference).FirstOrDefault(r => r is not null)
        ?? throw new InvalidOperationException($"grid {grid.Name} has no line in '{view.Name}' to dimension to");

    /// <summary>The step the user would do for a cut-edge hand-off: a dimension added by hand in the target view.</summary>
    private static void AddDimensionByHand(Document doc, View view, DimSetup d)
    {
        using var t = new Transaction(doc, "Field test: the user's step");
        t.Start();
        Dimension(doc, view, d.A, d.B);
        t.Commit();
    }

    private static void RollBack(TransactionGroup group)
    {
        if (group.HasStarted() && !group.HasEnded()) group.RollBack();
    }

    /// <summary>
    /// A real hand-off answered by the test responder: copy_dimensions on a dimension it treats as cut-edge hands the step
    /// over; the first Continue comes before the step is done (the re-check must refuse it), the second after.
    /// </summary>
    private static (string, string) HandoffContinue(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        using var group = new TransactionGroup(doc, "Field test: hand-off Continue");
        group.Start();
        var asked = new List<string?>();
        try
        {
            if (DimensionSetup(doc, 1, out why) is not { } d) return Fail(why);
            var target = d.Targets[0];
            CopyDimensionsTool.CutEdgeForTest = dim => dim.Id == d.Dim.Id;
            Handoff.TestResponder = (step, note) =>
            {
                asked.Add(note);
                if (note is not null) AddDimensionByHand(doc, target, d);
                return HandoffChoice.Continue;
            };
            var result = CallHandingOff(app, s.Registry, "copy_dimensions",
                new { source_view_id = d.Plan.Id.Value, target_view_ids = new[] { target.Id.Value }, dimension_ids = new[] { d.Dim.Id.Value } });
            var steps = HandoffSteps(result.Text);
            var last = Handoff.Recent.LastOrDefault();
            var problems = new List<string>();
            if (steps is not [("continued", true)]) problems.Add($"the result's handoff section says {Steps(steps)}: {Short(result.Text, 300)}");
            if (asked.Count != 2 || asked[0] is not null || asked[1]?.StartsWith("Not done yet", StringComparison.Ordinal) != true)
                problems.Add($"expected a Continue the re-check refused, then one it passed; it asked {asked.Count} time(s)" + (asked.Count > 1 ? $" ({asked[1]})" : ""));
            if (last is not { Done: true, Choice: "continued" }) problems.Add($"the recent hand-offs end with {last?.Choice ?? "nothing"}");
            return problems.Count == 0
                ? Pass($"copy_dimensions on a cut-edge dimension handed over \"{last!.What}\"; Continue before the step was asked again (\"{Short(asked[1]!, 80)}\"), Continue after it passed the re-check; the result and the recent hand-offs say done")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            Handoff.TestResponder = null;
            CopyDimensionsTool.CutEdgeForTest = null;
            RollBack(group);
        }
    }

    /// <summary>Skip on add_legend_components with no component to copy; Skip, Stop and the step after a Stop on copy_dimensions.</summary>
    private static (string, string) HandoffSkipStop(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        using var group = new TransactionGroup(doc, "Field test: hand-off Skip and Stop");
        group.Start();
        try
        {
            var problems = new List<string>();
            var notes = new List<string>();

            // Skip: no legend component in the test project (and, normally, none in other open models or a seed file).
            var legendAsked = new List<HandoffStep>();
            Handoff.TestResponder = (step, _) => { legendAsked.Add(step); return HandoffChoice.Skip; };
            var legend = LegendSeeds.Legends(doc).FirstOrDefault();
            var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).FirstElementId().Value.ToString();
            object legendArgs = legend is null ? new { types = new[] { wallType }, labels = false } : new { legend = legend.Name, types = new[] { wallType }, labels = false };
            ToolResult legendResult;
            try { legendResult = CallHandingOff(app, s.Registry, "add_legend_components", legendArgs); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                legendResult = ToolResult.Error(ex.Message);
            }
            if (legendAsked.Count == 0 && legendResult.IsError)
                problems.Add($"add_legend_components failed without handing anything over: {Short(legendResult.Text, 200)}");
            else if (legendAsked.Count == 0)
                notes.Add("add_legend_components found a component to copy (another open model or a legend seed), so it had nothing to hand over; Skip is checked on copy_dimensions only");
            else
            {
                var steps = HandoffSteps(legendResult.Text);
                if (steps is not [("skipped", false)]) problems.Add($"add_legend_components: the handoff section says {Steps(steps)}");
                if (Handoff.Recent.LastOrDefault() is not { Choice: "skipped" }) problems.Add("add_legend_components: the recent hand-offs don't end with skipped");
                notes.Add($"add_legend_components handed over \"{legendAsked[0].What}\" ({legendAsked[0].Kind}) and Skip was reported");
            }

            // Skip, Stop, and a third step that is never asked: one cut-edge hand-off per target view.
            if (DimensionSetup(doc, 3, out why) is not { } d) return Fail(why);
            CopyDimensionsTool.CutEdgeForTest = dim => dim.Id == d.Dim.Id;
            var asked = 0;
            Handoff.TestResponder = (_, _) => ++asked switch { 1 => HandoffChoice.Skip, 2 => HandoffChoice.Stop, _ => HandoffChoice.Continue };
            var result = CallHandingOff(app, s.Registry, "copy_dimensions",
                new { source_view_id = d.Plan.Id.Value, target_view_ids = d.Targets.Select(v => v.Id.Value).ToArray(), dimension_ids = new[] { d.Dim.Id.Value } });
            var three = HandoffSteps(result.Text);
            if (three is not [("skipped", false), ("stopped", false), ("stopped", false)]) problems.Add($"copy_dimensions: the handoff section says {Steps(three)}");
            if (asked != 2) problems.Add($"copy_dimensions: {asked} steps were asked, not 2 (the one after Stop must not be)");
            if (!result.Text.Contains("The user stopped here")) problems.Add("copy_dimensions: the result doesn't tell Claude the user stopped");
            var recent = Handoff.Recent.Skip(Math.Max(0, Handoff.Recent.Count - 3)).Select(o => o.Choice).ToList();
            if (!recent.SequenceEqual(["skipped", "stopped", "stopped"])) problems.Add($"copy_dimensions: the recent hand-offs end with {string.Join(", ", recent)}");
            notes.Add("copy_dimensions to three views: Skip, then Stop; the third step was not asked and counts as stopped, and Claude is told the user stopped");
            return problems.Count == 0 ? Pass(string.Join(". ", notes) + ".") : Fail(string.Join("; ", problems) + ". " + string.Join(". ", notes));
        }
        finally
        {
            Handoff.TestResponder = null;
            CopyDimensionsTool.CutEdgeForTest = null;
            RollBack(group);
        }
    }

    /// <summary>copy_dimensions static_fallback: a cut-edge dimension becomes a detail line with ticks and "(static)" value notes.</summary>
    private static (string, string) HandoffStatic(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        using var group = new TransactionGroup(doc, "Field test: static stand-ins");
        group.Start();
        try
        {
            if (DimensionSetup(doc, 1, out why) is not { } d) return Fail(why);
            var target = d.Targets[0];
            CopyDimensionsTool.CutEdgeForTest = dim => dim.Id == d.Dim.Id;
            var asked = 0;
            Handoff.TestResponder = (_, _) => { asked++; return HandoffChoice.Stop; };
            var result = Call(app, s.Registry, "copy_dimensions",
                new { source_view_id = d.Plan.Id.Value, target_view_ids = new[] { target.Id.Value }, dimension_ids = new[] { d.Dim.Id.Value }, static_fallback = true });
            var lines = new FilteredElementCollector(doc, target.Id).OfClass(typeof(CurveElement)).OfType<DetailCurve>()
                .Count(c => c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == StaticDimensionText.Comment);
            var texts = new FilteredElementCollector(doc, target.Id).OfClass(typeof(TextNote)).Cast<TextNote>().Select(n => n.Text ?? "").ToList();
            var values = texts.Count(t => t.Contains("(static)"));
            var labelled = texts.Any(t => t.Contains("not a live dimension"));
            var problems = new List<string>();
            if (result.IsError) problems.Add("copy_dimensions failed: " + Short(result.Text, 300));
            if (lines == 0) problems.Add("no detail line marked as a static stand-in in the target view");
            if (values == 0) problems.Add("no \"(static)\" value note in the target view");
            if (!labelled) problems.Add("no note saying it is not a live dimension");
            if (asked > 0) problems.Add("it handed the dimension over instead of drawing a stand-in");
            return problems.Count == 0
                ? Pass($"with static_fallback the cut-edge dimension became {lines} detail line(s) (the line and its ticks) and {values} \"(static)\" value note(s), labelled not live; nothing was handed over")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            Handoff.TestResponder = null;
            CopyDimensionsTool.CutEdgeForTest = null;
            RollBack(group);
        }
    }

    /// <summary>place_many on 60 placements, with a stop asked for after the first ten (the field test's own job, as cancel_job would).</summary>
    private static (string, string) CancelFast(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (Jobs.Running != s.Job) return Fail("the field test is not the running job, so no stop can be asked for");
        var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(f => f.Family?.FamilyPlacementType == FamilyPlacementType.OneLevelBased && f.Category?.CategoryType == CategoryType.Model)
            .OrderBy(f => f.Category.Id.Value == (long)BuiltInCategory.OST_Furniture || f.Category.Id.Value == (long)BuiltInCategory.OST_GenericModel ? 0 : 1)
            .FirstOrDefault();
        if (symbol is null) return Fail("the test project has no level-based model family to place");
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).First();
        const int count = 60, chunk = 10;
        var placements = Enumerable.Range(0, count).Select(i => new
        {
            x = Revit.RevitHelpers.FromInternal(doc, -40 - i % 10 * 3.0),
            y = Revit.RevitHelpers.FromInternal(doc, -40 - i / 10 * 3.0),
        }).ToArray();
        var limit = new StepLimit();
        s.Job.Limit = limit;
        CancelPoint.TestItem = (_, index) => { if (index >= chunk) limit.TryHit(); };
        using var group = new TransactionGroup(doc, "Field test: Cancel");
        group.Start();
        try
        {
            var watch = Stopwatch.StartNew();
            var result = Call(app, s.Registry, "place_many", new { kind = "family", source = $"{symbol.FamilyName}: {symbol.Name}", default_level = level.Name, placements });
            if (result.IsError) return Fail("place_many failed: " + Short(result.Text, 400));
            using var json = JsonDocument.Parse(result.Text);
            var placed = json.RootElement.GetProperty("placed").GetInt32();
            var stopped = json.RootElement.TryGetProperty("stopped", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
            var expected = $"after {chunk} of {count}";
            return stopped.StartsWith(CancelPoint.Stopped, StringComparison.Ordinal) && stopped.Contains(expected) && placed is > 0 and <= chunk
                ? Pass($"place_many on {count} x {symbol.FamilyName}: {symbol.Name} stopped {expected} in {watch.ElapsedMilliseconds} ms with {placed} placed and kept; it said: {stopped}")
                : Fail($"place_many did not stop {expected} with a partial result: placed {placed}, stopped \"{stopped}\". {Short(result.Text, 300)}");
        }
        finally
        {
            CancelPoint.TestItem = null;
            limit.Finish();
            s.Job.Limit = null;
            RollBack(group);
        }
    }

    // ---- The live job card: rename_elements on drafting views made for the probe, as a chat task (rolled back after)

    private const int JobItems = 24;
    private const string JobBatch = "Rename elements";

    /// <summary>What one run of the job probes' batch did: each item's status, Claude's text after the step, and the step's state.</summary>
    private sealed record JobRun(ToolResult Result, List<string> Statuses, string Text, string State)
    {
        public int Ok => Statuses.Count(st => st == "ok");
    }

    /// <summary>
    /// Makes <see cref="JobItems"/> drafting views in the test project and runs <paramref name="body"/> with a chat task's
    /// control in place (the field test is not a chat task, so it stands in for one); everything is rolled back after and
    /// the hooks cleared.
    /// </summary>
    private static (string, string) WithJob(UIApplication app, State s, string title, Func<Document, List<ViewDrafting>, JobControl, (string, string)> body)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting);
        if (type is null) return Fail("the test project has no drafting view type");
        var keepControl = JobControl.Current;
        var keepTool = JobControl.Tool;
        var keepLong = JobFeed.LongStep;
        using var group = new TransactionGroup(doc, "Field test: " + title);
        group.Start();
        try
        {
            var views = new List<ViewDrafting>();
            var stamp = Guid.NewGuid().ToString("N").Substring(0, 6);
            using (var t = new Transaction(doc, "Field test: drafting views for the job card"))
            {
                t.Start();
                for (var i = 0; i < JobItems; i++)
                {
                    var view = ViewDrafting.Create(doc, type.Id);
                    view.Name = $"SPM job {stamp} {i:00}";
                    views.Add(view);
                }
                t.Commit();
            }
            var control = new JobControl();
            JobControl.Current = control;
            JobControl.Tool = control; // as the bridge sets it for a chat call
            JobFeed.LongStep = null; // no job window during the test
            JobFeed.Reset();
            JobNotes.Take();
            return body(doc, views, control);
        }
        finally
        {
            JobControl.TestBoundary = null;
            JobControl.TestAfterItem = null;
            JobControl.TestWait = null;
            JobControl.Current = keepControl;
            JobControl.Tool = keepTool;
            JobFeed.LongStep = keepLong;
            JobNotes.Take();
            RollBack(group);
        }
    }

    /// <summary>rename_elements on the views, then what the bridge does when a chat step ends (Skip/Stop text, messages).</summary>
    private static JobRun RenameViews(UIApplication app, State s, List<ViewDrafting> views, JobControl control)
    {
        var stamp = Guid.NewGuid().ToString("N").Substring(0, 6);
        var result = Call(app, s.Registry, "rename_elements",
            new { items = views.Select((v, i) => new { element_id = v.Id.Value, name = $"SPM renamed {stamp} {i:00}" }).ToArray() });
        var statuses = new List<string>();
        if (!result.IsError)
        {
            using var json = JsonDocument.Parse(result.Text);
            foreach (var row in json.RootElement.EnumerateArray()) statuses.Add(row.GetProperty("status").GetString() ?? "");
        }
        var text = JobControl.AfterStep(control, "rename_elements", result.Text, result.IsError, out var state);
        return new JobRun(result, statuses, text, state);
    }

    /// <summary>The step's text is still one JSON value (the card's lines are added as fields).</summary>
    private static bool Json(string text)
    {
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    private static bool Renamed(ViewDrafting view) => view.Name.StartsWith("SPM renamed", StringComparison.Ordinal);

    /// <summary>The feed says item n of 24 at each boundary and ends at 24 of 24 (100%), matching the items renamed.</summary>
    private static (string, string) JobProgress(UIApplication app, State s) => WithJob(app, s, "job card progress", (_, views, control) =>
    {
        var seen = new List<(int Done, int Total)>();
        JobControl.TestBoundary = (what, _) => { if (what == JobBatch) seen.Add((JobFeed.Now.Done, JobFeed.Now.Total)); };
        var run = RenameViews(app, s, views, control);
        var end = JobFeed.Now;
        var model = JobCardModel.FromPlan(["rename_elements"], DateTime.UtcNow);
        var step = model.Begin("rename_elements")!;
        (step.Done, step.Total) = (end.Done, end.Total);
        model.End(step, run.State, "");
        var problems = new List<string>();
        if (run.Result.IsError) problems.Add("rename_elements failed: " + Short(run.Result.Text, 300));
        if (run.Ok != JobItems || views.Count(Renamed) != JobItems) problems.Add($"{run.Ok} of {JobItems} renamed ({views.Count(Renamed)} by name)");
        if (seen.Count != JobItems || seen.Where((p, i) => p.Done != i || p.Total != JobItems).Any())
            problems.Add($"the boundaries reported {string.Join(", ", seen.Take(6).Select(p => $"{p.Done}/{p.Total}"))}… ({seen.Count} of them), not 0/24 to 23/24");
        if (end.What != JobBatch || end.Done != JobItems || end.Total != JobItems || JobMath.Percent(end.Done, end.Total) != 100)
            problems.Add($"the feed ended at {end.What}: {end.Done} of {end.Total} ({JobMath.Percent(end.Done, end.Total)}%)");
        if (run.State != "done" || model.Headline() != "1 of 1 step done") problems.Add($"the step ended {run.State}; the card says {model.Headline()}");
        return problems.Count == 0
            ? Pass($"rename_elements on {JobItems} views: the feed went 0/{JobItems} to {JobItems}/{JobItems}, ended at {JobMath.Count(end.Done, end.Total)} and all {run.Ok} were renamed; the card says {model.Headline()}")
            : Fail(string.Join("; ", problems));
    });

    /// <summary>Pause at item 8: the batch waits at the next boundary with no transaction open; Resume renames the rest.</summary>
    private static (string, string) JobPauseResume(UIApplication app, State s) => WithJob(app, s, "job card pause", (doc, views, control) =>
    {
        var last = -1;
        int? pausedAt = null;
        var modifiable = true;
        var pausedState = JobState.Running;
        var feedPaused = false;
        JobControl.TestBoundary = (what, i) =>
        {
            if (what != JobBatch) return;
            last = i;
            if (i == 8) control.Apply(JobAction.Pause);
        };
        JobControl.TestWait = (c, _) =>
        {
            pausedAt = last;
            modifiable = doc.IsModifiable;
            pausedState = c.State;
            feedPaused = JobFeed.Now.Paused;
            c.Apply(JobAction.Resume);
        };
        var run = RenameViews(app, s, views, control);
        var problems = new List<string>();
        if (pausedAt != 8) problems.Add(pausedAt is null ? "it never paused" : $"it paused at item {pausedAt}, not 8");
        if (modifiable) problems.Add("a transaction was open while paused");
        if (pausedState != JobState.Paused || !feedPaused) problems.Add($"while waiting the state was {pausedState} and the feed {(feedPaused ? "" : "not ")}paused");
        if (run.Ok != JobItems || views.Count(Renamed) != JobItems) problems.Add($"after Resume {run.Ok} of {JobItems} were renamed");
        if (control.State != JobState.Running || run.State != "done") problems.Add($"after Resume the task is {control.State} and the step {run.State}");
        return problems.Count == 0
            ? Pass($"Pause at item 8 waited at the boundary before item 9 with no transaction open (the feed said paused); Resume renamed all {JobItems}")
            : Fail(string.Join("; ", problems));
    });

    /// <summary>Stop while item 9 runs: that item is rolled back, the 8 before it kept, the rest not done, and Claude is told.</summary>
    private static (string, string) JobStop(UIApplication app, State s) => WithJob(app, s, "job card stop", (_, views, control) =>
    {
        var before = views.Select(v => v.Name).ToList();
        JobControl.TestAfterItem = (what, i) => { if (what == JobBatch && i == 8) control.Apply(JobAction.Stop); };
        var run = RenameViews(app, s, views, control);
        var problems = new List<string>();
        if (run.Statuses.Count != JobItems) problems.Add("rename_elements gave no per-item result: " + Short(run.Result.Text, 300));
        else
        {
            if (run.Statuses.Take(8).Any(st => st != "ok") || !views.Take(8).All(Renamed)) problems.Add("the 8 finished items were not all kept");
            if (run.Statuses[8] != JobControl.RolledBack || views[8].Name != before[8]) problems.Add($"item 9 was not rolled back: \"{run.Statuses[8]}\", named {views[8].Name}");
            if (run.Statuses.Skip(9).Any(st => !st.StartsWith("not done: the user stopped", StringComparison.Ordinal)) || views.Skip(9).Any(Renamed))
                problems.Add("items after the stop were not all left undone");
        }
        if (run.State != "stopped" || !run.Text.Contains(JobControl.StoppedText) || !Json(run.Text)) problems.Add($"the step ended {run.State} and Claude's text starts \"{Short(run.Text, 80)}\"");
        return problems.Count == 0
            ? Pass("Stop while item 9 of 24 ran: it was rolled back (name unchanged), items 1-8 kept, 10-24 not done; Claude is told the user stopped")
            : Fail(string.Join("; ", problems));
    });

    /// <summary>Skip step at item 6: the step ends with 5 kept and says skipped; the task carries on with the next step.</summary>
    private static (string, string) JobSkip(UIApplication app, State s) => WithJob(app, s, "job card skip", (_, views, control) =>
    {
        JobControl.TestBoundary = (what, i) => { if (what == JobBatch && i == 5) control.Apply(JobAction.Skip); };
        var run = RenameViews(app, s, views, control);
        var problems = new List<string>();
        if (run.Ok != 5 || views.Count(Renamed) != 5) problems.Add($"{run.Ok} renamed ({views.Count(Renamed)} by name), not 5");
        if (run.Statuses.Skip(5).Any(st => !st.StartsWith("not done: the user skipped", StringComparison.Ordinal))) problems.Add("the items after the skip don't say skipped");
        if (run.State != "skipped" || !run.Text.Contains(JobControl.SkippedText) || !Json(run.Text)) problems.Add($"the step ended {run.State}");
        if (control.State != JobState.Running) problems.Add($"after the step the task is {control.State}, not running (the skip was not used up)");
        // The next step runs normally.
        JobControl.TestBoundary = null;
        var next = RenameViews(app, s, views.Skip(5).ToList(), control);
        if (next.Ok != JobItems - 5 || next.State != "done") problems.Add($"the next step renamed {next.Ok} of {JobItems - 5} and ended {next.State}");
        return problems.Count == 0
            ? Pass($"Skip step at item 6: 5 kept, the rest skipped, Claude is told to move on; the next step then renamed the other {JobItems - 5}")
            : Fail(string.Join("; ", problems));
    });

    /// <summary>A message queued mid-batch is added once to that step's result (what Claude reads next) and reported delivered.</summary>
    private static (string, string) JobMessage(UIApplication app, State s) => WithJob(app, s, "job card message", (_, views, control) =>
    {
        const string note = "Field test: use the prefix B- for the next sheets.";
        string? id = null;
        var delivered = new List<(string Tool, IReadOnlyList<string> Ids)>();
        void Got(string tool, IReadOnlyList<string> ids) => delivered.Add((tool, ids));
        JobControl.TestBoundary = (what, i) => { if (what == JobBatch && i == 3) id = JobNotes.Add(note); };
        JobNotes.Delivered += Got;
        try
        {
            var run = RenameViews(app, s, views, control);
            var problems = new List<string>();
            var times = Regex.Matches(run.Text, Regex.Escape(note)).Count;
            if (id is null) problems.Add("the hook never queued the message");
            if (times != 1 || !run.Text.Contains("[Message from the user")) problems.Add($"the step's result carries the message {times} time(s)");
            if (!Json(run.Text)) problems.Add("the result with the message is no longer valid JSON");
            if (JobNotes.Count != 0) problems.Add($"{JobNotes.Count} message(s) still waiting");
            if (delivered.Count != 1 || delivered[0].Tool != "rename_elements" || !delivered[0].Ids.SequenceEqual([id!])) problems.Add($"delivered {delivered.Count} time(s)");
            if (run.Ok != JobItems) problems.Add($"the batch was interrupted: {run.Ok} of {JobItems} renamed");
            return problems.Count == 0
                ? Pass($"a message queued during item 4 reached the end of rename_elements' result once (what Claude reads next) and was reported delivered; all {JobItems} items ran. The chat's live round trip is the checklist's \"job.ui\" step")
                : Fail(string.Join("; ", problems));
        }
        finally { JobNotes.Delivered -= Got; }
    });

    /// <summary>
    /// When Revit asks (Bridge/Approvals): each level's decision for a normal change on the open model, the dialog's
    /// answers (a test hook answers it, so no dialog shows and your settings are not written), "Allow the rest of this
    /// task", a risky call that can't be silenced, a tool's own dialog after the bridge asked, and the typical task's count.
    /// </summary>
    private static (string, string) ApprovalFlow(UIApplication app, State s)
    {
        var problems = new List<string>();
        var tool = s.Registry.Find("create_sheets");
        if (tool is null) return Fail("create_sheets is not registered");
        if (Bridge.Approvals.Remembered().Contains(tool.Name)) return ("skip", "you chose not to be asked about create_sheets again; clear it in Settings to run this check");
        var args = new Dictionary<string, JsonElement>();
        var asked = new List<string>();
        try
        {
            foreach (var level in ApprovalPolicy.Levels)
            {
                Bridge.Approvals.TestLevel = level;
                var fresh = new Bridge.JobControl();
                var d = Bridge.Approvals.Decide(app, tool, args, true, fresh);
                if (d.Ask != (level is not (ApprovalLevel.RiskyOnly or ApprovalLevel.AlwaysAllow))) problems.Add($"{ApprovalPolicy.Label(level)}: a new sheet {(d.Ask ? "asked" : "did not ask")}");
                fresh.ApprovedForTask = true;
                if (Bridge.Approvals.Decide(app, tool, args, true, fresh).Ask) problems.Add($"{ApprovalPolicy.Label(level)}: an approved task still asked");
            }

            // At "Ask once per task", Allow on the first change approves the rest of the chat task.
            Bridge.Approvals.TestLevel = ApprovalLevel.OncePerTask;
            var chat = new Bridge.JobControl();
            Bridge.Approvals.TestAnswer = (t, d) => { asked.Add(t + ":" + d.Kind); return Bridge.Approvals.Answer.RestOfTask; };
            var first = Bridge.Approvals.Decide(app, tool, args, true, chat);
            if (!first.Ask || !Bridge.Approvals.Ask(tool, args, first, chat) || !chat.ApprovedForTask) problems.Add("Allow the rest of this task did not approve the task");
            if (Bridge.Approvals.Decide(app, tool, args, true, chat).Ask) problems.Add("the second change of an approved task asked");

            // Chat code is risky: asked even in the approved task, and its answer can't approve anything else.
            var code = s.Registry.Find("run_revit_query_code");
            var codeArgs = new Dictionary<string, JsonElement> { ["code"] = JsonDocument.Parse("\"return 1;\"").RootElement.Clone() };
            var other = new Bridge.JobControl();
            var risky = code is null ? null : Bridge.Approvals.Decide(app, code, codeArgs, true, other);
            if (risky is not { Ask: true, CanSilence: false }) problems.Add("chat code was not asked as risky");
            else if (Bridge.Approvals.Ask(code!, codeArgs, risky, other) && other.ApprovedForTask) problems.Add("allowing chat code approved the whole task");

            // A tool with its own dialog: asks a normal change there, but not again after the bridge asked; the ribbon asks only at every change.
            Bridge.Approvals.Call = new Bridge.Approvals.CallState(first, chat, Asked: false);
            if (!Bridge.Approvals.AskOwn("drawing_package")) problems.Add("the package plan was not asked for a normal change");
            Bridge.Approvals.Call = new Bridge.Approvals.CallState(first, chat, Asked: true);
            if (Bridge.Approvals.AskOwn("drawing_package")) problems.Add("the package plan asked again after the bridge asked");
            Bridge.Approvals.Call = null;
            Bridge.Approvals.TestLevel = ApprovalLevel.RiskyOnly;
            if (Bridge.Approvals.AskOwn("redline_apply")) problems.Add("from the ribbon, redlines asked at risky only");
            Bridge.Approvals.TestLevel = ApprovalLevel.EveryChange;
            if (!Bridge.Approvals.AskOwn("redline_apply") && !Bridge.Approvals.Remembered().Contains("redline_apply")) problems.Add("from the ribbon, redlines did not ask at every change");

            // "Always allow": nothing asks, chat code and the ribbon's own dialogs included; the issue check's accept still does.
            Bridge.Approvals.TestLevel = ApprovalLevel.AlwaysAllow;
            if (code is not null && Bridge.Approvals.Decide(app, code, codeArgs, true, new Bridge.JobControl()).Ask) problems.Add("Always allow: chat code asked");
            if (Bridge.Approvals.AskOwn("redline_apply")) problems.Add("Always allow: the ribbon's redlines asked");
            var before = asked.Count;
            if (!Bridge.Approvals.Show("run_revit_code", new ApprovalDecision(true, AskKind.Risky, "test"), "", null) || asked.Count != before)
                problems.Add("Always allow: a tool's own risky dialog was shown");
            if (s.Registry.Find("issue_check") is { } gate)
            {
                var accept = new Dictionary<string, JsonElement> { ["accept"] = JsonDocument.Parse("[{\"key\":\"x\",\"reason\":\"test\"}]").RootElement.Clone() };
                if (!Bridge.Approvals.Decide(app, gate, accept, true, new Bridge.JobControl()).Ask) problems.Add("Always allow: accepting issue check items did not ask");
            }
        }
        finally
        {
            Bridge.Approvals.TestLevel = null;
            Bridge.Approvals.TestAnswer = null;
            Bridge.Approvals.Call = null;
        }
        var typical = new List<(string, bool, IReadOnlyDictionary<string, JsonElement>)>();
        foreach (var name in new[] { "get_model_context", "create_plan_views", "apply_view_template", "create_sheets", "place_views_on_sheet", "tag_elements_in_view" })
            if (s.Registry.Find(name) is { } t) typical.Add((name, t.ModifiesModel, args));
        var counts = ApprovalPolicy.Levels.Select(l => $"{ApprovalPolicy.Label(l)} {ApprovalPolicy.CountAsks(typical, l, false)}").ToList();
        return problems.Count == 0
            ? Pass($"each level decided as set; Allow the rest of this task approved the task; chat code asked as risky; asked: {string.Join(", ", asked)}; " +
                   $"dialogs for a plans, sheets and tags task: {string.Join(", ", counts)}")
            : Fail(string.Join("; ", problems));
    }

    /// <summary>A sample stream with a streamed thinking block, and one without streaming, build the card's Thinking line.</summary>
    private static (string, string) JobThinking()
    {
        string[] streamed =
        [
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_ft1"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"The sheets need title blocks.\n"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Fill the sheet numbers first, then the dates."}}}""",
            """{"type":"assistant","message":{"id":"msg_ft1","content":[{"type":"thinking","thinking":"The sheets need title blocks.\nFill the sheet numbers first, then the dates."}]}}""",
        ];
        var (card, events) = Chat.ChatPane.CardFromSample(streamed);
        var problems = new List<string>();
        var line = card.ThinkingShown;
        if (events != 3) problems.Add($"{events} thinking events from the streamed sample, not 3 (start and two deltas; the complete message repeats them)");
        if (!line.StartsWith("Thinking…", StringComparison.Ordinal) || !line.Contains("Fill the sheet numbers first")) problems.Add($"the one-line Thinking says \"{line}\"");
        if (card.ThinkingToggle.Visibility != System.Windows.Visibility.Visible) problems.Add("the Thinking line is hidden");
        card.ThinkingToggle.IsChecked = true;
        if (!card.ThinkingFull.Contains("The sheets need title blocks.") || !card.ThinkingFull.Contains("then the dates.")) problems.Add($"opened, it shows \"{Short(card.ThinkingFull, 120)}\"");
        var (whole, wholeEvents) = Chat.ChatPane.CardFromSample([streamed[4]]);
        if (wholeEvents != 1 || !whole.ThinkingShown.Contains("then the dates.")) problems.Add($"without streaming: {wholeEvents} event(s), \"{whole.ThinkingShown}\"");
        return problems.Count == 0
            ? Pass($"streamed: \"{line}\", opening it shows both lines; a complete message without streaming shows it too")
            : Fail(string.Join("; ", problems));
    }

    private static double Seconds(JsonElement e, string part, string field) => e.GetProperty(part).GetProperty(field).GetDouble();

    private static double?[] TimeParameters(Document doc) =>
        Revit.TimeLog.Parameters.Select(p => doc.ProjectInformation?.get_Parameter(p.Guid)?.AsDouble()).ToArray();

    /// <summary>Where the four parameters differ from the totals (Saved, Working, AI, By hand, in hours); empty when they match.</summary>
    private static List<string> ParameterProblems(double?[] values, TimeTotals totals)
    {
        double[] want = [TimeMath.Hours(totals.Saved), TimeMath.Hours(totals.Working), TimeMath.Hours(totals.Ai), TimeMath.Hours(totals.ByHand)];
        var problems = new List<string>();
        for (var i = 0; i < want.Length; i++)
        {
            var name = Revit.TimeLog.Parameters[i].Name;
            if (values[i] is not { } v) problems.Add($"{name} is missing");
            else if (Math.Abs(v - want[i]) > 0.011) problems.Add($"{name} is {v:0.##}, the totals say {want[i]:0.##}");
        }
        return problems;
    }

    /// <summary>
    /// A scripted session (6 min active, 8 min idle, beyond the threshold, 3 min AI) replayed on a private clock and
    /// written to the test project: counts, no double count, the Done in line, parameters, Extensible Storage, PC log.
    /// </summary>
    private static (string, string) TimeTracking(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        const string tool = "create_sheets";
        var problems = new List<string>();
        var ownership = NoBorrow(doc, problems);
        var before = Revit.TimeLog.Report(doc).Totals;
        var sim = JsonSerializer.SerializeToElement(Revit.TimeLog.SimulateForTest(doc, activeMinutes: 6, idleMinutes: 8, aiMinutes: 3, units: 4, tool: tool));
        var report = Revit.TimeLog.Report(doc);
        var after = report.Totals;

        var (expHand, expAi) = (Seconds(sim, "expected", "byHandSeconds"), Seconds(sim, "expected", "aiSeconds"));
        var (hand, ai, estimate) = (Seconds(sim, "counted", "byHandSeconds"), Seconds(sim, "counted", "aiSeconds"), Seconds(sim, "counted", "estimateSeconds"));
        if (!sim.GetProperty("ok").GetBoolean() || Math.Abs(expHand - hand) > 0.5 || Math.Abs(expAi - ai) > 0.5)
            problems.Add($"expected {TimeMath.Duration(expHand)} by hand and {TimeMath.Duration(expAi)} AI, counted {TimeMath.Duration(hand)} and {TimeMath.Duration(ai)}");
        var (dWork, dAi, dHand) = (after.Working - before.Working, after.Ai - before.Ai, after.ByHand - before.ByHand);
        if (Math.Abs(dAi - ai) > 1 || Math.Abs(dHand - hand) > 1 || Math.Abs(dWork - (ai + hand)) > 1)
            problems.Add($"the model's totals grew by working {dWork:0} s, AI {dAi:0} s and by hand {dHand:0} s; expected AI {ai:0} s + by hand {hand:0} s = {ai + hand:0} s (more is a double count)");
        var line = sim.GetProperty("line").GetString() ?? "";
        if (!line.StartsWith("Done in ", StringComparison.Ordinal) || line != TimeMath.Line(ai, estimate)) problems.Add($"the line reads \"{line}\"");
        var written = sim.GetProperty("written").GetString() ?? "";
        if (!written.StartsWith("written", StringComparison.Ordinal)) problems.Add($"the model write: {written}");
        problems.AddRange(ParameterProblems(TimeParameters(doc), after).Select(p => "Project Information: " + p));
        var row = Revit.TimeLog.StoredRowForTest(doc, TimeMath.Day(DateTime.Now), tool);
        if (row is null || row.Ai + 0.1 < ai) problems.Add($"Extensible Storage has {(row is null ? "no row" : $"{row.Ai:0} s AI")} for {tool} today");
        var logged = LoggedSimulation(Revit.TimeLog.LogFile, Revit.TimeLog.Key(doc), ai);
        if (!logged) problems.Add($"the PC log {Revit.TimeLog.LogFile} has no line for this session");

        var t = report.Totals;
        s.Result.TimeSaved =
            $"Scripted session in the test project ({(doc.IsWorkshared ? "workshared" : "not workshared")}): 6 min active, 8 min idle (beyond the threshold, not counted), " +
            $"3 min AI. Counted {TimeMath.Duration(hand)} by hand and {TimeMath.Duration(ai)} AI. Line: \"{line}\". The test project's totals now: working " +
            $"{TimeMath.Duration(t.Working)}, AI {TimeMath.Duration(t.Ai)}, by hand {TimeMath.Duration(t.ByHand)}, saved {TimeMath.Duration(t.Saved)}. " +
            $"Model write: {written}. PC log: {Revit.TimeLog.LogFile}." + (ownership.Length > 0 ? " " + ownership : "");
        return problems.Count == 0
            ? Pass((ownership.Length > 0 ? ownership + " Then: " : "") + $"counted {TimeMath.Duration(hand)} by hand and {TimeMath.Duration(ai)} AI as expected (8 idle minutes not counted, input during the task not counted twice); " +
                   $"working time grew by exactly AI + by hand; \"{line}\"; the four Project Information parameters match the totals; the row is in Extensible Storage ({written}) and in the PC log")
            : Fail(string.Join("; ", problems));
    }

    /// <summary>
    /// In a workshared test project: a write while this user doesn't own Project Information must borrow nothing (neither
    /// Project Information nor any time record) and leave the parameters alone. Then Project Information is checked out
    /// in the test project, as a user who edits it would own it, so the probes after this can check the parameters.
    /// Returns what was checked ("" when the project isn't workshared).
    /// </summary>
    private static string NoBorrow(Document doc, List<string> problems)
    {
        if (!doc.IsWorkshared || doc.ProjectInformation is not { } info) return "";
        CheckoutStatus Status(ElementId id) => WorksharingUtils.GetCheckoutStatus(doc, id);
        var storage = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.ExtensibleStorage.DataStorage)).ToElementIds()
            .Where(id => Status(id) != CheckoutStatus.OwnedByCurrentUser).ToList();
        var infoBefore = Status(info.Id);
        var parameters = TimeParameters(doc);
        var write = Revit.TimeLog.WriteForTest(doc);
        var checkedNow = infoBefore != CheckoutStatus.OwnedByCurrentUser;
        if (checkedNow)
        {
            if (Status(info.Id) == CheckoutStatus.OwnedByCurrentUser) problems.Add("a time write borrowed Project Information");
            if (!TimeParameters(doc).SequenceEqual(parameters)) problems.Add("the parameters changed although this user doesn't own Project Information");
        }
        var borrowed = storage.Count(id => doc.GetElement(id) is not null && Status(id) == CheckoutStatus.OwnedByCurrentUser);
        if (borrowed > 0) problems.Add($"a time write borrowed {borrowed} time record element(s) nobody owned");
        if (Status(info.Id) != CheckoutStatus.OwnedByCurrentUser) WorksharingUtils.CheckoutElements(doc, new List<ElementId> { info.Id });
        return checkedNow
            ? $"Without owning Project Information, a write ({write}) borrowed nothing and left the parameters alone; then Project Information was checked out in the test project, as a user who edits it would own it."
            : $"Project Information was already this user's in the test project, so the no-borrow check covered only the {storage.Count} unowned time record(s) ({write}).";
    }

    /// <summary>True when the PC log has the simulated session for this model.</summary>
    private static bool LoggedSimulation(string file, string key, double aiSeconds)
    {
        if (!File.Exists(file)) return false;
        foreach (var text in Enumerable.Reverse(File.ReadAllLines(file)).Take(200))
        {
            try
            {
                using var line = JsonDocument.Parse(text);
                var r = line.RootElement;
                if (r.TryGetProperty("simulated", out var sim) && sim.ValueKind == JsonValueKind.True && r.GetProperty("key").GetString() == key &&
                    Math.Abs(r.GetProperty("tookSeconds").GetDouble() - aiSeconds) < 0.5) return true;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return false;
    }

    // ---- The issue check (in the test project, rolled back) ---------------------------------------------------------------

    /// <summary>
    /// The zero-mistake gate end to end on known faults: a new sheet with no views and placeholder title block fields.
    /// The gate must block and list them under that sheet, and issue_drawings must refuse without changing the model;
    /// an accepted item must come back when its finding changes; filling the fields and placing a view must clear the
    /// sheet on re-check; accepting the rest with a reason must clear the gate, and stay accepted on the next check.
    /// The gate's per-model file goes to the test folder.
    /// </summary>
    private static (string, string) IssueGateProbe(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var gateFolder = Path.Combine(s.Folder, "issue-gate");
        var reports = Path.Combine(s.Folder, "issue-check");
        if (Directory.Exists(gateFolder)) foreach (var f in Directory.GetFiles(gateFolder)) File.Delete(f);
        IssueGate.FolderOverride = gateFolder;
        using var group = new TransactionGroup(doc, "Field test: issue check");
        group.Start();
        var problems = new List<string>();
        // Which part ran last, so a crash names it.
        var step = "making the sheet";
        try
        {
            const string number = "FT-ISSUE";
            ViewSheet sheet;
            using (var t = new Transaction(doc, "Field test: a sheet with known faults"))
            {
                t.Start();
                sheet = ViewSheet.Create(doc, ElementId.InvalidElementId);
                sheet.SheetNumber = number;
                sheet.Name = "Issue check probe";
                t.Commit();
            }
            IssueGateMath.GroupResult Mine(IssueGate.Run r) => r.Groups.First(g => g.Group == number);
            bool Has(IssueGate.Run r, string check, IssueGateMath.Status status) => Mine(r).Items.Any(i => i.Finding.Check == check && i.Status == status);

            // A check that crashed shows as "could not run" (and blocks): name it, it is a fault in that check.
            void Crashed(IssueGate.Run r)
            {
                foreach (var (tool, outcome) in r.Checks.Where(c => c.Outcome.StartsWith("could not run", StringComparison.Ordinal)))
                    if (!problems.Any(p => p.StartsWith(tool + " ", StringComparison.Ordinal))) problems.Add($"{tool} {Short(outcome, 300)}");
            }

            // 1. Blocked, with the seeded faults on the sheet.
            step = "1. first check";
            var run = IssueGate.Check(app, [sheet], null, null, reports);
            Crashed(run);
            if (!run.Blocked) problems.Add("the gate did not block a sheet with no views and empty title block fields");
            if (!Has(run, "empty_sheets", IssueGateMath.Status.Open) || !Has(run, "sheet_fields", IssueGateMath.Status.Open))
                problems.Add($"the sheet's group lists {string.Join(", ", Mine(run).Items.Select(i => i.Finding.Check))}, not empty_sheets and sheet_fields");
            if (run.Report is null || !File.Exists(run.Report)) problems.Add("no Excel report");

            // 2. issue_drawings refuses and changes nothing.
            step = "2. issue_drawings while blocked";
            var revisions = new FilteredElementCollector(doc).OfClass(typeof(Revision)).GetElementCount();
            var refused = Call(app, s.Registry, "issue_drawings", new { sheet_numbers = new[] { number }, revision_description = "Field test issue", folder = Path.Combine(s.Folder, "issue") });
            if (refused.IsError || !refused.Text.Contains("\"stopped\"")) problems.Add("issue_drawings was not stopped: " + Short(refused.Text, 200));
            if (new FilteredElementCollector(doc).OfClass(typeof(Revision)).GetElementCount() != revisions) problems.Add("issue_drawings made a revision while blocked");

            // 3. Accept the title block item, then change the finding (one field filled): it must come back open.
            step = "3. accept, then change the finding";
            IssueGate.Accept(doc, run, Mine(run).Items.Where(i => i.Finding.Check == "sheet_fields"), "Field test: filled at issue");
            if (!Has(run, "sheet_fields", IssueGateMath.Status.Accepted)) problems.Add("the accepted item did not show as accepted");
            void Set(Action<ViewSheet> edit)
            {
                using var t = new Transaction(doc, "Field test: fix the sheet");
                t.Start();
                edit(sheet);
                t.Commit();
            }
            Set(x => x.get_Parameter(BuiltInParameter.SHEET_DRAWN_BY)?.Set("FT"));
            run = IssueGate.Check(app, [sheet], null, null, reports);
            Crashed(run);
            if (!Has(run, "sheet_fields", IssueGateMath.Status.Open)) problems.Add("a changed finding did not come back open after it was accepted");

            // 4. Fix the sheet: the fields filled and a view placed, then re-check: the sheet passes.
            step = "4. fix the sheet";
            Set(x =>
            {
                x.get_Parameter(BuiltInParameter.SHEET_CHECKED_BY)?.Set("FT");
                x.get_Parameter(BuiltInParameter.SHEET_APPROVED_BY)?.Set("FT");
                x.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE)?.Set("2026-01-01");
                var view = Drafting.Create(doc, "Issue check probe view " + Guid.NewGuid().ToString("N").Substring(0, 6), 50);
                // Revit won't put an empty view on a sheet.
                TextNote.Create(doc, view.Id, XYZ.Zero, "Issue check probe", doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType));
                if (Drafting.Place(doc, x, view, 0) is null) problems.Add("the probe's drafting view could not be placed on the sheet");
            });
            step = "4. re-check after the fix";
            run = IssueGate.Check(app, [sheet], null, null, reports);
            Crashed(run);
            if (!Mine(run).Pass) problems.Add("after the fix the sheet still has: " + string.Join("; ", Mine(run).Items.Where(i => i.Status == IssueGateMath.Status.Open).Select(i => i.Finding.Issue).Take(3)));

            // 5. Accept the rest with a reason: clear, and still clear on the next check.
            step = "5. accept the rest";
            var rest = run.Open.ToList();
            if (rest.Count > 0) IssueGate.Accept(doc, run, rest, "Field test: known in the test project");
            if (run.Blocked) problems.Add("still blocked after accepting every open item");
            var again = IssueGate.Check(app, [sheet], null, null, reports);
            if (again.Blocked) problems.Add($"the acceptances were not kept: {again.Open.Count()} open on the next check");
            if (!File.Exists(IssueGate.StorePath(doc))) problems.Add("no per-model file with the acceptances");

            return problems.Count == 0
                ? Pass($"blocked on the seeded sheet ({string.Join(", ", new[] { "empty_sheets", "sheet_fields" })}) and issue_drawings refused with no revision; an accepted item came back when its text changed; " +
                       $"the fixed sheet passed on re-check; {rest.Count} other item(s) accepted with a reason cleared the gate and stayed accepted; report {Path.GetFileName(again.Report ?? "")}")
                : Fail(string.Join("; ", problems));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add($"at step {step}: {ex.GetType().Name}: {ex.Message}{IssueGate.Where(ex)}");
            return Fail(string.Join("; ", problems));
        }
        finally
        {
            IssueGate.FolderOverride = null;
            RollBack(group);
        }
    }

    /// <summary>The summary written as the Time saved window's Excel file (in the test folder) and read back.</summary>
    private static (string, string) TimeExport(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var report = Revit.TimeLog.Report(doc);
        var file = Path.Combine(s.Folder, "Time saved field test.xlsx");
        var sheets = report.ExcelSheets();
        Xlsx.Write(file, sheets);
        using var zip = ZipFile.OpenRead(file);
        var worksheets = zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal));
        var text = string.Concat(zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal)).Select(e =>
        {
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        }));
        var missing = new[] { "Working time", "AI time", "By-hand time", "Time saved", "create_sheets" }.Where(w => !text.Contains(w)).ToList();
        return worksheets == sheets.Count && missing.Count == 0
            ? Pass($"{Path.GetFileName(file)} has {worksheets} sheets ({string.Join(", ", sheets.Select(x => x.Name))}) with the summary and the test session's rows ({new FileInfo(file).Length / 1024.0:0.#} KB)")
            : Fail($"{file}: {worksheets} of {sheets.Count} sheets" + (missing.Count > 0 ? "; missing " + string.Join(", ", missing) : ""));
    }

    /// <summary>
    /// The workshared queue path: with Project Information and the time records owned by someone else (a test hook: one
    /// Revit session has only one user), the parameters must wait while the totals stay complete (this user's own new
    /// record and the PC log); once this user owns Project Information again, the next write brings the parameters up.
    /// </summary>
    private static (string, string) TimeQueue(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var where = doc.IsWorkshared ? "the workshared test project (central and local)" : "the test project (not workshared; the hook stands in for worksharing)";
        var before = TimeParameters(doc);
        var totalsBefore = Revit.TimeLog.Report(doc).Totals;
        JsonElement sim;
        double?[] held;
        TimeTotals totalsHeld;
        Revit.TimeLog.OwnedElsewhereForTest = "Field test colleague";
        try
        {
            sim = JsonSerializer.SerializeToElement(Revit.TimeLog.SimulateForTest(doc, activeMinutes: 2, idleMinutes: 8, aiMinutes: 1, units: 2));
            held = TimeParameters(doc);
            totalsHeld = Revit.TimeLog.Report(doc).Totals;
        }
        finally { Revit.TimeLog.OwnedElsewhereForTest = null; }
        var problems = new List<string>();
        if (!held.SequenceEqual(before)) problems.Add("the parameters changed while someone else held Project Information");
        if (totalsHeld.Ai < totalsBefore.Ai + Seconds(sim, "counted", "aiSeconds") - 1) problems.Add("the session's AI time was not kept while the model was held");
        if (doc.IsWorkshared && doc.ProjectInformation is { } info && WorksharingUtils.GetCheckoutStatus(doc, info.Id) != CheckoutStatus.OwnedByCurrentUser)
            WorksharingUtils.CheckoutElements(doc, new List<ElementId> { info.Id }); // owned, as by a user who edits it (test project only)
        var write = Revit.TimeLog.WriteForTest(doc);
        var after = TimeParameters(doc);
        var totals = Revit.TimeLog.Report(doc).Totals;
        problems.AddRange(ParameterProblems(after, totals).Select(p => "after release: " + p));
        return problems.Count == 0
            ? Pass($"on {where}, with every element owned by someone else (test hook): the session's time stayed in the totals (model write: {sim.GetProperty("written").GetString()}) and the " +
                   $"parameters waited, nothing borrowed; once this user owned Project Information, the next write ({write}) brought them to the totals (working {TimeMath.Hours(totals.Working):0.##} h, AI {TimeMath.Hours(totals.Ai):0.##} h)")
            : Fail($"on {where}: " + string.Join("; ", problems));
    }

    // ---- The chat's look and feel: screenshots per scale, frame times ---------------------------------------------------

    /// <summary>
    /// The chat pane with sample content (a table, code, steps, a brief) drawn at 100, 125, 150 and 175 % in both
    /// themes, saved next to the report. A test can't judge sharpness: it checks the files are there, and the
    /// checklist's "Chat is sharp at every scale" step has a person compare them.
    /// </summary>
    private static (string, string) ChatDpi(State s)
    {
        var folder = Path.Combine(s.O.ReportFolder ?? DefaultReportFolder, $"{s.Result.Started:yyyyMMdd-HHmmss} chat-dpi");
        var files = Chat.ChatPane.Snapshot.Render(folder);
        // The chat's rounded styles must load as designed; if WPF refused them the pane falls back to a plainer look.
        if (Chat.ChatUi.StylesError is { } styles)
            return Fail("The chat's styles didn't load as designed, so it shows a plainer look: " + styles + $" (screenshots in {folder})");
        var bad = files.Where(f => !File.Exists(f) || new FileInfo(f).Length < 1000).Select(Path.GetFileName).ToList();
        var expected = Chat.ChatPane.Snapshot.Scales.Length * 2;
        if (files.Count != expected || bad.Count > 0)
            return Fail($"{files.Count} of {expected} screenshots made" + (bad.Count > 0 ? "; empty: " + string.Join(", ", bad) : "") + $" (in {folder})");
        return Pass($"{files.Count} screenshots (light and dark at 100, 125, 150 and 175 %) saved in {folder}: " +
                    string.Join(", ", files.Select(Path.GetFileName)) + ". Compare them (checklist: \"Chat is sharp at every scale\")");
    }

    /// <summary>
    /// A request to an idle Revit is picked up within a few seconds; the ribbon can be read for an edit mode's Finish
    /// button (and shows none now); the "Revit is busy" card names the step in plain words, never by its tool id.
    /// Off Revit's thread.
    /// </summary>
    private static async Task<(string, string)> RevitBlockedCard()
    {
        var problems = new List<string>();
        var clock = Stopwatch.StartNew();
        var work = App.Dispatcher.InvokeAsync(_ => true);
        var why = await App.Dispatcher.WaitForPickupAsync(work, "The field test's empty request", 8000, 4000, CancellationToken.None).ConfigureAwait(false);
        var pickup = clock.ElapsedMilliseconds;
        if (why is not null) problems.Add("an empty request to an idle Revit wasn't picked up in 8 s: " + Short(why, 200));
        var shown = await Revit.EditModeProbe.ShownAsync(3000).ConfigureAwait(false);
        if (shown is null) problems.Add("Revit's ribbon couldn't be read, so an edit mode can't be told apart (the card then says \"Revit is busy\" and Esc)");
        else if (shown == true) problems.Add("a Finish button is on the ribbon outside an edit mode, so the card would wrongly say edit mode");
        var card = Revit.RevitBlock.Card(new Revit.RevitBlock.State(Revit.RevitBlock.Kind.Unknown, null), Revit.RevitBlock.Friendly("get_model_context", false));
        if (card.Text.Contains("get_model_context") || !card.Where.Contains("Esc") || card.Text.Contains("edit mode")) problems.Add("the busy card reads: " + card.Text);
        return problems.Count == 0
            ? Pass($"An empty request was picked up in {pickup} ms; the ribbon was read and shows no Finish button; the busy card reads \"{card.What}. {card.Where}. {card.Why}\"")
            : Fail(string.Join("; ", problems));
    }

    /// <summary>The message box in a hidden window: text where the hint was, growing upward, a visible caret, clicks reach it.</summary>
    private static (string, string) ChatComposer()
    {
        var (problems, measured) = Chat.ChatPane.ComposerCheck();
        return problems.Count == 0
            ? Pass("Typed text starts top-left where the hint was, more lines grow the box with the first line in place, the caret is visible, " +
                   "and a click on the card or in the box's padding puts the caret in the box" + (measured.Length > 0 ? $" ({measured})" : ""))
            : Fail(string.Join("; ", problems));
    }

    /// <summary>
    /// Frame times in a hidden window while the history drawer opens and closes and a sample reply streams in: the
    /// average and the worst frame go in the report. How it feels is the checklist's "Chat feels smooth" step.
    /// </summary>
    private static async Task<(string, string)> ChatFrames()
    {
        var run = await App.Dispatcher.InvokeAsync(_ => Chat.ChatPane.Snapshot.Frames()).ConfigureAwait(false);
        var (average, worst, frames, hitches, animated) = await run.ConfigureAwait(false);
        var line = $"{frames} frames while the drawer opened and closed and a reply streamed: average {average:0.0} ms " +
                   $"({(average > 0 ? 1000 / average : 0):0} fps), worst {worst:0} ms, {hitches} over {Chat.FrameTimes.HitchMs:0} ms" +
                   (animated ? "" : ". Windows' animations are off, so the pane doesn't animate");
        return Chat.FrameTimes.Smooth(average, worst) ? Pass(line) : Fail(line + ". Expected under 25 ms on average and no frame over 200 ms");
    }

    // ---- Prompt enhancer (with chat_round_trip) and the guided checklist ------------------------------------------------

    private const string Ambiguous = "Make the walls thicker";

    private static (string, string) BriefManual() =>
        ("manual", "manual (checklist): off by default because planning uses your Claude plan; the checklist's \"The brief card in the chat\" step covers it. Run with chat_round_trip true to test it here.");

    private static int ElementCount(UIApplication app, State s) =>
        TestDoc(app, s, out _) is { } doc ? new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount() : -1;

    /// <summary>The real planning run on an ambiguous request: assumptions and questions, and nothing in the model changes.</summary>
    private static async Task<(string, string)> BriefPlan(State s)
    {
        if (!s.O.ChatRoundTrip) return BriefManual();
        var before = await App.Dispatcher.InvokeAsync(app => ElementCount(app, s)).ConfigureAwait(false);
        var (brief, error) = await Chat.ChatPane.BriefTest.Plan(Ambiguous).ConfigureAwait(false);
        var after = await App.Dispatcher.InvokeAsync(app => ElementCount(app, s)).ConfigureAwait(false);
        if (brief is null) return Fail("no brief: " + Short(error, 300));
        s.Brief = brief;
        var problems = new List<string>();
        if (brief.Assumptions.Count == 0) problems.Add("the brief has no assumptions");
        if (brief.Questions.Count == 0) problems.Add("the brief asks no questions");
        if (before < 0 || before != after) problems.Add($"the test project's element count went from {before} to {after} before anything was approved");
        return problems.Count == 0
            ? Pass($"\"{Ambiguous}\" became a {brief.Kind} brief with {brief.Assumptions.Count} assumption(s) and {brief.Questions.Count} question(s) " +
                   $"(e.g. \"{Short(brief.Questions[0].Text, 80)}\"); the test project still has {after} elements. While the field test runs, the planner's model reads are answered busy, so it plans from the request alone")
            : Fail(string.Join("; ", problems));
    }

    /// <summary>On a brief card in the pane: Run as I typed sends the typed text unchanged; an edit is remembered as a note, then forgotten.</summary>
    private static async Task<(string, string)> BriefChoose(State s)
    {
        if (!s.O.ChatRoundTrip) return BriefManual();
        var brief = s.Brief ?? new Chat.PromptBrief
        {
            Kind = "change", Request = Ambiguous, Prompt = "Change every basic wall on Level 1 to the type 50 mm thicker than its own.",
            Assumptions = ["Only basic walls on Level 1"],
        };
        var typed = await App.Dispatcher.InvokeAsync(_ =>
        {
            Chat.ChatPane.Get(); // the pane's content, as the dockable pane makes it; nothing is shown or sent
            return Chat.ChatPane.BriefTest.Choose(brief, Chat.BriefChoice.RunAsTyped);
        }).ConfigureAwait(false);
        if (typed.Problem.Length > 0) return Fail("Run as I typed: " + typed.Problem);
        var problems = new List<string>();
        if (typed.Sent != brief.Request) problems.Add($"Run as I typed would send \"{Short(typed.Sent ?? "nothing", 120)}\", not \"{brief.Request}\"");

        var edited = Chat.PromptBriefs.Compose(brief) + "\nAlways give wall thicknesses in millimetres.";
        var edit = await App.Dispatcher.InvokeAsync(_ => Chat.ChatPane.BriefTest.Choose(brief, Chat.BriefChoice.RunEdited, edited)).ConfigureAwait(false);
        if (edit.Problem.Length > 0) return Fail(string.Join("; ", problems.Append("Edit: " + edit.Problem)));
        if (edit.Sent is null) problems.Add("the edited brief would not be sent");
        var note = "";
        if (edit.Remembered is null) problems.Add("the edit was not remembered");
        else if (await Task.WhenAny(edit.Remembered, Task.Delay(TimeSpan.FromMinutes(2))).ConfigureAwait(false) != edit.Remembered) problems.Add("remembering the edit took over 2 minutes");
        else
        {
            note = await edit.Remembered.ConfigureAwait(false);
            if (note.Length == 0) problems.Add("the edit was remembered as nothing");
            else
            {
                bool Kept() => Chat.MemoryNotes.For(Chat.MemoryNotes.Load(), null).Global.Contains(note);
                if (!Kept()) problems.Add("the note is not in the Global notes");
                if (!Chat.ChatPane.BriefTest.Forget(note) || Kept()) problems.Add("the note could not be removed again; delete it in the Notes tab: " + note);
            }
        }
        return problems.Count == 0
            ? Pass($"Run as I typed sends \"{Ambiguous}\" unchanged; the edited brief was sent and remembered as \"{Short(note, 120)}\", then removed from your notes")
            : Fail(string.Join("; ", problems));
    }

    /// <summary>The guided checklist's items (manual items plus the fixed bug rows) and its result file, saved to the test folder.</summary>
    private static (string, string) ChecklistBuild(string folder)
    {
        var items = TestChecklistWindow.Items();
        var manual = FieldTestCoverage.Features.Count(f => f.How == FieldTestCoverage.How.Manual) + FieldTestCoverage.ManualTools.Count;
        var bugs = items.Count(i => i.Area == FieldTestCoverage.BugFixes);
        var problems = new List<string>();
        if (items.Count - bugs != manual) problems.Add($"{items.Count - bugs} manual items, the coverage table has {manual}");
        if (bugs == 0) problems.Add("no fixed bug rows (the bug log built into this version was not read)");
        if (items.Select(i => i.Id).Distinct().Count() != items.Count) problems.Add("an item is listed twice");
        if (items.FirstOrDefault(i => i.Steps.Trim().Length == 0) is { } bare) problems.Add($"{bare.Id} has no steps");
        if (items.Count < 2) return Fail(string.Join("; ", problems.Append("too few items to mark")));
        var marks = new Dictionary<string, FieldTestCoverage.Mark>
        {
            [items[0].Id] = new(items[0].Id, "pass", "field test"),
            [items[^1].Id] = new(items[^1].Id, "fail", "field test | note"),
        };
        var path = TestChecklistWindow.SaveTo(Path.Combine(folder, "checklist"), items, marks, Update.Updater.CurrentText, "field test", "Field test project", DateTime.Now);
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        if (!text.Contains($"1 pass, 1 fail, {items.Count - 2} not checked") || !text.Contains("field test / note")) problems.Add($"the saved file {path} doesn't hold the marks");
        return problems.Count == 0
            ? Pass($"{items.Count} items: {manual} manual steps and {bugs} fixed bug rows; a result with one pass and one fail was saved to {path}")
            : Fail(string.Join("; ", problems));
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
