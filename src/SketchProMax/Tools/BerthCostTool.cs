using System.Globalization;
using System.IO;
using Autodesk.Revit.UI;
using static SketchProMax.Tools.BerthCostMath;

namespace SketchProMax.Tools;

/// <summary>
/// The berth costing for the chat: each berth's cost and how complete it is, one berth's costs by item, the unit costs,
/// and changing a rate, a quantity or a berth's length and exclusions as the user asks; 'measure' reads the open model
/// for berths (the window's Measure does the same in steps). The files are made from the Berth costing window.
/// </summary>
public sealed class BerthCostTool : IRevitTool
{
    public string Name => "berth_costing";
    public string Description =>
        "Berth costing (Berth costing button): 'list' gives each berth's structural cost, US$ per lm and how complete its costing is; 'details' one berth's " +
        "costs per item (quantity, where it came from: model, hand or worked out, rate, cost) and what is missing; 'rates' the unit costs; 'set_rate' changes " +
        "a unit cost (code or name) and every berth follows; 'set_quantity' sets an item's quantity by hand (clear: back to the model's); 'set_berth' sets " +
        "quay length, scope boxes to leave out (exclude, e.g. BERTH 9 - PART 1) and what that is (exclude_note, e.g. excl. RoRo) or a design parameter " +
        "(param + value: MiddleD, MiddleKg, RearD, RearKg, InfillKg, FrontBeamKg, RearBeamKg, TransverseKg, DeckKg, ApproachKg); 'measure' reads the open " +
        "model for the berths (all of the open model's when none given); 'set_details' sets what the built-in BOQ prints (header_left, header_right, title, " +
        "doc_number with {nn}/{n}/{length}, works, boq_rates); 'in_files' ticks an item in or out of the BOQ and back-up (item + value 1 or 0); 'describe' gives one berth's BOQ " +
        "descriptions as they will print (pile lengths, tips, reinforcement from the model). The costing, BOQ and back-up files are made in the Berth costing window.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["list", "details", "rates", "set_rate", "set_quantity", "set_berth", "measure", "set_details", "in_files", "describe"]},
      "berth": {"type": "string", "description": "details, set_quantity, set_berth: the berth (Berth 9, 9, or its name). measure: berths with ';' (default: the open model's)."},
      "rate": {"type": "string", "description": "set_rate: the rate's code (R_PIPE) or name."},
      "item": {"type": "integer", "description": "set_quantity: the item number (1-42)."},
      "value": {"type": "number", "description": "set_rate, set_quantity, set_berth (with param or length): the new value."},
      "clear": {"type": "boolean", "description": "set_quantity: back to the model's quantity. set_rate: no rate."},
      "length": {"type": "number", "description": "set_berth: quay length in m."},
      "exclude": {"type": "string", "description": "set_berth: scope boxes whose elements are left out, ';' between."},
      "exclude_note": {"type": "string", "description": "set_berth: what is left out, in the berth's name (excl. RoRo)."},
      "param": {"type": "string", "description": "set_berth: a design parameter to set with value."},
      "header_left": {"type": "string", "description": "set_details: the BOQ page header's left lines (new lines with \\n)."},
      "header_right": {"type": "string", "description": "set_details: the BOQ page header's right lines."},
      "title": {"type": "string", "description": "set_details: the structure title ({n}, {length})."},
      "doc_number": {"type": "string", "description": "set_details: the document number ({nn}, {n}); also the BOQ file name."},
      "works": {"type": "string", "description": "set_details: the works' name (STRUCTURAL WORKS)."},
      "boq_rates": {"type": "boolean", "description": "set_details: print unit rates and amounts in the BOQ."},
      "project": {"type": "string", "description": "The project code; default: the open model's."}
    }
    """;
    public string[] Required => ["action"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument?.Document is { IsFamilyDocument: false } d ? d : null;
        var file = "";
        if (doc is not null) try { file = Path.GetFileName(doc.PathName ?? ""); if (file.Length == 0) file = doc.Title; } catch (ArgumentException) { file = doc.Title; }
        var project = args.OptString("project")?.Trim() is { Length: > 0 } given ? given : file.Length > 0 ? ChecklistProjects.ProjectKey(file) : "";
        if (project.Length == 0) return ToolResult.Error("Open a model of the project (or give project).");
        var setup = BerthStatusMath.LoadSetup(project);
        var p = Load(project);
        var berths = SyncBerths(p, setup);
        if (berths.Count == 0)
            return ToolResult.Error($"No berths to cost for '{project}': set up the berth status (Submissions status > Setup) or add berths in the Berth costing window.");

        Berth Pick(string? text = null)
        {
            text ??= args.OptString("berth") ?? throw new ArgumentException("Give the berth.");
            var t = text.Trim();
            var num = System.Text.RegularExpressions.Regex.Match(t, @"\d+").Value.TrimStart('0');
            var hits = berths.Where(b => string.Equals(b.Name, t, StringComparison.OrdinalIgnoreCase) || string.Equals(b.Title, t, StringComparison.OrdinalIgnoreCase)).ToList();
            // "9" is Berth 9 itself, not its left-out part (that one goes by its title, "Berth 09 RoRo").
            if (hits.Count == 0 && num.Length > 0) hits = berths.Where(b => b.Number == num && b.PartOf.Length == 0).ToList();
            if (hits.Count > 1 && doc is not null && setup is not null && BerthStatusMath.ModelFor(setup, file) is { } m) hits = hits.Where(b => string.Equals(b.Site, m.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            return hits.Count == 1 ? hits[0] : throw new ArgumentException($"No single berth matches '{t}'. Berths: {string.Join("; ", berths.Select(b => b.Title + " (" + b.Site + ")"))}.");
        }

        switch (args.String("action"))
        {
            case "list":
                return ToolResult.Ok(new
                {
                    project,
                    currency = p.Currency,
                    berths = berths.Select(b =>
                    {
                        var t = Total(p, b);
                        return new
                        {
                            berth = b.Title, site = b.Site, complete = Completeness(p, b) is { } c ? Math.Round(c) + "%" : "not measured",
                            structural = Math.Round(t.Structural), perLm = Math.Round(t.PerLm), length = b.Length, measured = b.Measured?.At.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture),
                            missing = Missing(p, b).Count, made = b.Made.Keys,
                        };
                    }),
                });
            case "details":
            {
                var b = Pick();
                var t = Total(p, b);
                return ToolResult.Ok(new
                {
                    berth = b.Title, site = b.Site, length = b.Length, exclude = b.Exclude, excludeNote = b.ExcludeNote, parameters = b.Params,
                    structural = Math.Round(t.Structural), prelimsAsBilled = Math.Round(t.Prelims), perLm = Math.Round(t.PerLm),
                    groups = Groups.Select(g => new { g.Code, g.Name, cost = Math.Round(t.ByGroup[g.Code]) }),
                    items = Costs(p, b).Where(c => c.Qty is not null || c.Item.Rule.Length > 0).Select(c => new
                    {
                        no = c.Item.No, c.Item.Name, qty = c.Qty is { } q ? Math.Round(q, 2) : (double?)null, c.Item.Unit, from = c.From.ToString().ToLowerInvariant(),
                        rate = Math.Round(c.Rate, 2), cost = Math.Round(c.Cost),
                    }),
                    missing = Missing(p, b),
                });
            }
            case "rates":
                return ToolResult.Ok(new
                {
                    currency = p.Currency,
                    pileBasis = new { p.Pile.RefDiameter, p.Pile.RefKg, scaling = p.Pile.ByVolume ? "volume" : "diameter" },
                    rates = p.Rates.Select(r => new { r.Code, r.Name, r.Unit, r.Value }),
                });
            case "set_rate":
            {
                var name = (args.OptString("rate") ?? throw new ArgumentException("Give the rate (code or name).")).Trim();
                var rate = p.RateOf(name) ?? p.Rates.FirstOrDefault(r => r.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? throw new ArgumentException($"No rate '{name}'. Rates: {string.Join(", ", p.Rates.Select(r => r.Code + " " + r.Name))}.");
                var old = rate.Value;
                rate.Value = args.OptBool("clear") ? null : args.OptDouble("value") ?? throw new ArgumentException("Give value (or clear).");
                rate.Assumed = false;
                Save(project, p);
                return ToolResult.Ok(new { rate = rate.Code, rate.Name, was = old, now = rate.Value, note = "Every berth's cost follows. The Berth costing window shows it when it is next focused." });
            }
            case "set_quantity":
            {
                var b = Pick();
                var no = (int?)args.OptLong("item") ?? throw new ArgumentException("Give item (its number).");
                var item = p.ItemOf(no) ?? throw new ArgumentException($"No item {no}.");
                if (args.OptBool("clear")) b.Hand.Remove(no);
                else b.Hand[no] = args.OptDouble("value") ?? throw new ArgumentException("Give value (or clear).");
                Save(project, p);
                var (q, from) = Quantity(p, b, no);
                return ToolResult.Ok(new { berth = b.Title, item = item.Name, qty = q, from = from.ToString().ToLowerInvariant(), structural = Math.Round(Total(p, b).Structural) });
            }
            case "set_berth":
            {
                var b = Pick();
                var said = new List<string>();
                if (args.OptDouble("length") is { } len) { b.Length = len > 0 ? len : null; said.Add("length"); }
                if (args.OptString("exclude") is { } ex) { b.Exclude = ex.Trim(); said.Add("exclude"); }
                if (args.OptString("exclude_note") is { } en) { b.ExcludeNote = en.Trim(); said.Add("exclude_note"); }
                if (args.OptString("param") is { Length: > 0 } key)
                {
                    var known = BerthCostMath.Params.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Label, key, StringComparison.OrdinalIgnoreCase));
                    if (known.Key is null) throw new ArgumentException($"No parameter '{key}'. They are: {string.Join(", ", BerthCostMath.Params.Select(x => x.Key))}.");
                    b.Params[known.Key] = args.OptDouble("value") ?? throw new ArgumentException("Give value for the parameter.");
                    said.Add(known.Key);
                }
                if (said.Count == 0) throw new ArgumentException("Give length, exclude, exclude_note or param with value.");
                Save(project, p);
                return ToolResult.Ok(new { berth = b.Title, set = said, note = said.Contains("exclude") ? "Measure again (action measure, or Measure in the window) to leave those elements out." : null });
            }
            case "measure":
            {
                if (doc is null) return ToolResult.Error("Open the model to measure.");
                var site = setup is not null && BerthStatusMath.ModelFor(setup, file) is { } m ? m.Name : "";
                var list = args.OptString("berth") is { Length: > 0 } names
                    ? BerthStatusMath.Values(names).Select(n => Pick(n)).ToList()
                    : berths.Where(b => site.Length == 0 || string.Equals(b.Site, site, StringComparison.OrdinalIgnoreCase)).ToList();
                if (list.Count == 0) return ToolResult.Error("None of the berths is in the open model (the berth status setup says which file is which site).");
                var reading = BerthCostFacts.Read(doc, p, list);
                BerthCostFacts.Apply(p, reading);
                Save(project, p);
                return ToolResult.Ok(new
                {
                    measured = list.Select(b => new { berth = b.Title, complete = Completeness(p, b) is { } c ? Math.Round(c) + "%" : "–", structural = Math.Round(Total(p, b).Structural), quantities = b.Measured?.Quantities }),
                    problems = reading.Problems,
                });
            }
            case "set_details":
            {
                var det = p.Details;
                var said = new List<string>();
                if (args.OptString("header_left") is { } hl) { det.HeaderLeft = hl.Replace("\\n", "\n").Trim(); said.Add("header_left"); }
                if (args.OptString("header_right") is { } hr) { det.HeaderRight = hr.Replace("\\n", "\n").Trim(); said.Add("header_right"); }
                if (args.OptString("title") is { } ti) { det.Title = ti.Trim(); said.Add("title"); }
                if (args.OptString("doc_number") is { } dn) { det.DocNumber = dn.Trim(); said.Add("doc_number"); }
                if (args.OptString("works") is { } wk) { det.Works = wk.Trim(); said.Add("works"); }
                if (args.Has("boq_rates")) { det.BoqRates = args.OptBool("boq_rates"); said.Add("boq_rates"); }
                if (said.Count == 0) return ToolResult.Ok(new { details = det });
                Save(project, p);
                return ToolResult.Ok(new { set = said, details = det });
            }
            case "in_files":
            {
                var no = (int?)args.OptLong("item") ?? throw new ArgumentException("Give item (its number).");
                var item = p.ItemOf(no) ?? throw new ArgumentException($"No item {no}.");
                item.InFiles = (args.OptDouble("value") ?? 1) != 0;
                Save(project, p);
                return ToolResult.Ok(new { item = item.Name, inFiles = item.InFiles });
            }
            case "describe":
            {
                var b = Pick();
                var unknown = new List<string>();
                return ToolResult.Ok(new
                {
                    berth = b.Title,
                    rows = BerthCostDocs.BoqItems(p, b).Select(x => new { no = x.Item.No, section = x.Item.Section, qty = Math.Round(x.Qty, 2), unit = x.Item.BoqUnit, description = Describe(p, b, x.Item, unknown) }),
                    notFromModel = unknown.Distinct(),
                });
            }
            default:
                throw new ArgumentException("action is list, details, rates, set_rate, set_quantity, set_berth, measure, set_details, in_files or describe.");
        }
    }
}
