using System.Globalization;
using System.Text;

namespace SketchProMax.Tools;

/// <summary>
/// One tool's by-hand rate: minutes a person would take per run, plus minutes per item the run processed (a sheet, a
/// tag, a bar). The built-in rates are estimates; Settings lets the user replace any of them.
/// </summary>
public sealed record TimeRate(string Tool, double PerRun, double PerItem, bool Estimate = true);

/// <summary>
/// Time saved and working time: the arithmetic behind <c>Revit.TimeLog</c>, which needs no Revit (so the tests run it).
///   Working time   active minutes only: a model change, a selection, a view activation or input in that model's window.
///                  Counting stops after the idle threshold; the gap is only bridged when it is shorter.
///   AI time        from the start to the end of a tool or chat task, approval waits included.
///   By-hand time   every other active second; a second is never counted twice (a minute shared with a task is split).
///   Time saved     by-hand estimate (rate × items) minus the AI time it took, never below zero.
/// </summary>
internal static class TimeMath
{
    /// <summary>One task's by-hand estimate is capped (a 20,000-bar rebar run would otherwise claim weeks).</summary>
    public const double MaxEstimateMinutes = 40 * 60;

    /// <summary>Rows for the work done by hand carry this tool name; chat tasks carry <see cref="ChatTool"/>.</summary>
    public const string HandTool = "";
    public const string ChatTool = "chat";

    /// <summary>The rate for tools without their own that change the model, and for those that only read it.</summary>
    public const string OtherChanges = "(other tools that change the model)";
    public const string OtherReads = "(other tools that only read)";

    /// <summary>
    /// Built-in rates (minutes per run, minutes per item), all estimates. Tools that read only and produce a report are
    /// per run; tools that make or change things are mostly per item.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (double Run, double Item)> Defaults = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
    {
        // Documentation
        ["tag_elements_in_view"] = (2, 0.25), ["arrange_tags"] = (2, 0.3), ["add_keynote_tags"] = (2, 0.3),
        ["dimension_grids"] = (3, 0.5), ["dimension_walls"] = (3, 0.75), ["dimension_openings"] = (3, 0.75),
        ["dimension_rooms"] = (3, 0.75), ["dimension_elements"] = (2, 0.75), ["add_dimensions"] = (2, 0.75),
        ["copy_dimensions"] = (2, 0.5), ["add_spot_elevations"] = (2, 0.5), ["add_annotations"] = (2, 0.5),
        ["reflow_annotations"] = (2, 0.3), ["create_plan_views"] = (1, 2), ["duplicate_views"] = (1, 1),
        ["apply_view_template"] = (1, 0.5), ["create_sheets"] = (2, 2), ["place_views_on_sheet"] = (2, 1.5),
        ["rename_elements"] = (1, 0.3), ["create_schedule"] = (8, 0), ["format_schedule"] = (5, 0), ["create_sheet_list"] = (8, 0),
        ["create_revision"] = (2, 0), ["cloud_elements"] = (1, 0.5), ["update_text_notes"] = (1, 0.3),
        ["fill_title_blocks"] = (2, 0.5), ["set_sheet_title_block"] = (1, 0.5), ["set_sheet_revisions"] = (1, 0.3),
        ["create_section"] = (3, 0), ["create_elevations"] = (1, 2), ["create_callout"] = (3, 0), ["create_3d_view"] = (3, 0),
        ["set_view_graphics"] = (2, 0.5), ["copy_view_detailing"] = (5, 0.3), ["relocate_view_with_detailing"] = (10, 0),
        ["duplicate_view_to_location"] = (5, 0), ["trim_matchlines"] = (3, 0.5), ["create_legend"] = (10, 0),
        ["place_legend_on_sheets"] = (1, 0.5), ["general_notes"] = (15, 0), ["typical_details"] = (10, 2),
        ["issue_drawings"] = (20, 1), ["apply_text"] = (2, 0.3), ["create_view_filter"] = (3, 0), ["apply_view_filter"] = (1, 0.5),
        ["color_by_parameter"] = (10, 0), ["create_project_parameters"] = (3, 1),
        // Modelling
        ["create_levels"] = (1, 1), ["create_grids"] = (1, 1), ["create_walls"] = (1, 1.5), ["create_floors"] = (2, 3),
        ["create_ceilings"] = (2, 3), ["create_roof"] = (5, 0), ["place_columns"] = (1, 0.5), ["place_beams"] = (1, 0.75),
        ["place_family_instances"] = (1, 0.5), ["place_hosted_families"] = (1, 0.75), ["place_many"] = (2, 0.5),
        ["transform_elements"] = (1, 0.2), ["set_parameters"] = (1, 0.2), ["change_type"] = (1, 0.2), ["duplicate_type"] = (1, 1),
        ["place_rooms"] = (1, 0.5), ["delete_elements"] = (1, 0.1), ["create_stairs"] = (10, 0), ["create_railings"] = (3, 1),
        ["create_mep_runs"] = (2, 1), ["create_mep_openings"] = (2, 3), ["place_void_cuts"] = (2, 3), ["cut_openings_in_joined"] = (2, 3),
        ["cut_slab"] = (3, 0), ["add_slab_edges"] = (2, 0.5), ["create_raft"] = (15, 0), ["create_piles"] = (2, 0.5),
        ["create_floors_from_faces"] = (2, 3), ["arch_to_structure"] = (10, 1), ["convert_cad"] = (10, 0.5),
        ["reinforce_beam"] = (10, 0.5), ["reinforce_column"] = (10, 0.5), ["reinforce_footing"] = (10, 0.5), ["reinforce_slab_wall"] = (10, 0.5),
        ["place_additional_reinforcement"] = (3, 1), ["place_mesh_symbols"] = (2, 0.5), ["place_column_section_details"] = (3, 2),
        ["place_bracing"] = (2, 1), ["create_steel_connections"] = (3, 2), ["place_detail_components"] = (2, 0.5),
        // Reports and checks (per run)
        ["export_pdf"] = (10, 0), ["export_schedule"] = (5, 0), ["export_drawing_list"] = (20, 0), ["export_quantities"] = (30, 0),
        ["run_model_audit"] = (30, 0), ["check_model"] = (45, 0), ["find_clashes"] = (30, 0), ["check_drawings"] = (45, 0),
        ["check_code_rules"] = (60, 0), ["spec_check"] = (60, 0), ["boq_check"] = (60, 0), ["cost_estimate"] = (90, 0),
        ["embodied_carbon"] = (120, 0), ["carbon_compare"] = (60, 0), ["ve_opportunities"] = (120, 0), ["ve_compare_options"] = (240, 0),
        ["what_if"] = (240, 0),
        ["export_bar_schedule"] = (60, 0), ["export_steel_takeoff"] = (45, 0), ["export_project_pack"] = (60, 0),
        ["find_missing_items"] = (30, 0), ["coordination_progress"] = (30, 0), ["check_schedule"] = (15, 0),
        [OtherChanges] = (1, 0.5),
        [OtherReads] = (0, 0),
    };

    /// <summary>The rate for a tool: the user's own when set, else the built-in estimate, else the fallback for its kind.</summary>
    public static TimeRate RateFor(string tool, bool modifiesModel, IEnumerable<TimeRate>? overrides)
    {
        var mine = overrides?.Where(r => r is not null).ToList() ?? [];
        TimeRate? Find(string name)
        {
            var own = mine.LastOrDefault(r => string.Equals(r.Tool, name, StringComparison.OrdinalIgnoreCase));
            if (own is not null) return own with { Tool = name, Estimate = false };
            return Defaults.TryGetValue(name, out var d) ? new TimeRate(name, d.Run, d.Item) : null;
        }
        return Find(tool) ?? (Find(modifiesModel ? OtherChanges : OtherReads) ?? new TimeRate(tool, 0, 0)) with { Tool = tool };
    }

    /// <summary>Every rate the settings show: the built-in ones (or the user's replacement), then the user's extra tools.</summary>
    public static List<TimeRate> RateTable(IEnumerable<TimeRate>? overrides)
    {
        var mine = overrides?.Where(r => r is not null && r.Tool.Trim().Length > 0).ToList() ?? [];
        var list = Defaults.Keys.Select(k => RateFor(k, true, mine)).ToList();
        foreach (var own in mine)
            if (!list.Any(r => string.Equals(r.Tool, own.Tool, StringComparison.OrdinalIgnoreCase))) list.Add(own with { Estimate = false });
        return list.OrderBy(r => r.Tool.StartsWith("(", StringComparison.Ordinal) ? 1 : 0).ThenBy(r => r.Tool, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Only the rates that differ from the built-in estimates, to keep in the settings file.</summary>
    public static List<TimeRate> Overrides(IEnumerable<TimeRate> table) =>
        table.Where(r => !(Defaults.TryGetValue(r.Tool, out var d) && Same(d.Run, r.PerRun) && Same(d.Item, r.PerItem)))
            .Select(r => r with { Estimate = false }).ToList();

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;

    /// <summary>The items a run processed: what an item batch finished, else the elements it added, else those it changed.</summary>
    public static int Units(int batchDone, int added, int modified) => batchDone > 0 ? batchDone : added > 0 ? added : Math.Max(0, modified);

    /// <summary>
    /// Minutes the work would take by hand: per run plus per item. A run that failed counts only the items it finished.
    /// </summary>
    public static double EstimateMinutes(TimeRate rate, int units, bool failed = false)
    {
        if (failed && units <= 0) return 0;
        var minutes = (failed ? 0 : Math.Max(0, rate.PerRun)) + Math.Max(0, rate.PerItem) * Math.Max(0, units);
        return Math.Min(MaxEstimateMinutes, minutes);
    }

    /// <summary>Saved seconds: by-hand estimate minus the time the task took, never below zero.</summary>
    public static double Saved(double estimateSeconds, double tookSeconds) => Math.Max(0, estimateSeconds - Math.Max(0, tookSeconds));

    /// <summary>
    /// A chat task's saving, shared out over the tools it ran in proportion to their by-hand estimates: the chat's own
    /// time (thinking included) is taken off once, not once per tool. Returns each tool's saved seconds, in order.
    /// </summary>
    public static List<double> SplitChat(double chatSeconds, IReadOnlyList<double> toolEstimateSeconds)
    {
        var total = toolEstimateSeconds.Sum(e => Math.Max(0, e));
        if (total <= 0) return toolEstimateSeconds.Select(_ => 0.0).ToList();
        var saved = Saved(total, chatSeconds);
        return toolEstimateSeconds.Select(e => saved * Math.Max(0, e) / total).ToList();
    }

    /// <summary>"40 s", "12 min", "1 h 05 min".</summary>
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        if (seconds < 59.5) return $"{Math.Round(seconds).ToString(CultureInfo.InvariantCulture)} s";
        var minutes = (long)Math.Round(seconds / 60);
        if (minutes < 60) return $"{minutes} min";
        return $"{minutes / 60} h {(minutes % 60).ToString("00", CultureInfo.InvariantCulture)} min";
    }

    /// <summary>The line shown after a task: "Done in X. By hand: about Y. Saved: Z."</summary>
    public static string Line(double tookSeconds, double estimateSeconds) =>
        $"Done in {Duration(tookSeconds)}. By hand: about {Duration(estimateSeconds)}. Saved: {Duration(Saved(estimateSeconds, tookSeconds))}.";

    /// <summary>Hours for the Project Information parameters, to two decimals.</summary>
    public static double Hours(double seconds) => Math.Round(seconds / 3600, 2);

    public static string Day(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One user's time on one day with one tool (or by hand, or in chat). Every field only ever grows.</summary>
internal sealed class TimeRow
{
    public string Day { get; set; } = "";
    public string Tool { get; set; } = "";
    public double Ai { get; set; }
    public double ByHand { get; set; }
    public double Estimate { get; set; }
    public double Saved { get; set; }
    public int Runs { get; set; }
    public int Units { get; set; }

    public string Key => Day + "\t" + Tool;

    public TimeRow Copy() => (TimeRow)MemberwiseClone();
}

/// <summary>
/// One user's time on one PC for one model: cumulative rows keyed by day and tool. The PC keeps one as its log and
/// queue, and the model keeps one per user and PC. Because every field only grows, merging takes the larger value of
/// each field: writing the same sheet twice, or an older copy over a newer one, never counts anything twice or loses it.
/// </summary>
internal sealed class TimeSheet
{
    public string User { get; set; } = "";
    public string Pc { get; set; } = "";
    public Dictionary<string, TimeRow> Rows { get; } = new(StringComparer.Ordinal);

    public TimeSheet() { }

    public TimeSheet(string user, string pc)
    {
        User = user;
        Pc = pc;
    }

    public TimeRow Row(string day, string tool)
    {
        tool = Clean(tool);
        var key = day + "\t" + tool;
        if (!Rows.TryGetValue(key, out var row)) Rows[key] = row = new TimeRow { Day = day, Tool = tool };
        return row;
    }

    public void Add(string day, string tool, double ai = 0, double byHand = 0, double estimate = 0, double saved = 0, int runs = 0, int units = 0)
    {
        var row = Row(day, tool);
        row.Ai += Math.Max(0, ai);
        row.ByHand += Math.Max(0, byHand);
        row.Estimate += Math.Max(0, estimate);
        row.Saved += Math.Max(0, saved);
        row.Runs += Math.Max(0, runs);
        row.Units += Math.Max(0, units);
    }

    /// <summary>Takes the larger value of every field of every row (see the class summary). Returns true when anything grew.</summary>
    public bool MergeMax(TimeSheet other)
    {
        var grew = false;
        foreach (var o in other.Rows.Values)
        {
            var row = Row(o.Day, o.Tool);
            double Max(double a, double b) { if (b > a + 1e-6) { grew = true; return b; } return a; }
            int MaxI(int a, int b) { if (b > a) { grew = true; return b; } return a; }
            row.Ai = Max(row.Ai, o.Ai);
            row.ByHand = Max(row.ByHand, o.ByHand);
            row.Estimate = Max(row.Estimate, o.Estimate);
            row.Saved = Max(row.Saved, o.Saved);
            row.Runs = MaxI(row.Runs, o.Runs);
            row.Units = MaxI(row.Units, o.Units);
        }
        return grew;
    }

    /// <summary>True when some field of <paramref name="other"/> is larger than this sheet's: it has time this one lacks.</summary>
    public bool IsBehind(TimeSheet other)
    {
        var copy = Copy();
        return copy.MergeMax(other);
    }

    public TimeSheet Copy()
    {
        var copy = new TimeSheet(User, Pc);
        foreach (var r in Rows.Values) copy.Rows[r.Key] = r.Copy();
        return copy;
    }

    /// <summary>One line per row: day, tool, AI s, by-hand s, estimate s, saved s, runs, items (tab separated).</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var r in Rows.Values.OrderBy(r => r.Day, StringComparer.Ordinal).ThenBy(r => r.Tool, StringComparer.Ordinal))
            sb.Append(r.Day).Append('\t').Append(r.Tool).Append('\t').Append(N(r.Ai)).Append('\t').Append(N(r.ByHand)).Append('\t')
              .Append(N(r.Estimate)).Append('\t').Append(N(r.Saved)).Append('\t').Append(r.Runs.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.Units.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Reads <see cref="Serialize"/>'s lines; lines it can't read are skipped (a newer version may add fields).</summary>
    public static TimeSheet Parse(string user, string pc, string? text)
    {
        var sheet = new TimeSheet(user, pc);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 8 || f[0].Length != 10) continue;
            if (!D(f[2], out var ai) || !D(f[3], out var hand) || !D(f[4], out var est) || !D(f[5], out var saved)) continue;
            if (!int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var runs) || !int.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var units)) continue;
            var other = new TimeSheet();
            var row = other.Row(f[0], f[1]);
            (row.Ai, row.ByHand, row.Estimate, row.Saved, row.Runs, row.Units) = (ai, hand, est, saved, runs, units);
            sheet.MergeMax(other);
        }
        return sheet;
    }

    private static bool D(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;

    private static string N(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Clean(string tool) => (tool ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>
    /// Sheets of the same user and PC (several copies in the model, the PC's own queue) become one, field by field the
    /// larger; different users or PCs stay apart, so totals cover everyone.
    /// </summary>
    public static List<TimeSheet> Combine(IEnumerable<TimeSheet> sheets)
    {
        var byOwner = new Dictionary<string, TimeSheet>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sheets)
        {
            var key = s.User + "\n" + s.Pc;
            if (!byOwner.TryGetValue(key, out var merged)) byOwner[key] = merged = new TimeSheet(s.User, s.Pc);
            merged.MergeMax(s);
        }
        return byOwner.Values.ToList();
    }
}

/// <summary>Totals over sheets, in seconds.</summary>
internal sealed record TimeTotals(double Working, double Ai, double ByHand, double Estimate, double Saved, int Runs)
{
    /// <summary>An estimate of the same work done entirely by hand: the time worked plus the time saved.</summary>
    public double FullyByHand => Working + Saved;

    public static TimeTotals Of(IEnumerable<TimeRow> rows)
    {
        double ai = 0, hand = 0, est = 0, saved = 0;
        var runs = 0;
        foreach (var r in rows)
        {
            ai += r.Ai;
            hand += r.ByHand;
            est += r.Estimate;
            saved += r.Saved;
            if (r.Tool != TimeMath.HandTool) runs += r.Runs;
        }
        return new TimeTotals(ai + hand, ai, hand, est, saved, runs);
    }
}

/// <summary>What the summary window, the Excel file and Export for Claude show for one model.</summary>
internal sealed class TimeReport
{
    public string Model { get; init; } = "";
    public List<TimeSheet> Sheets { get; init; } = [];
    /// <summary>Why some of this PC's time is not in the model yet (it is kept on the PC and added later), or null.</summary>
    public string? Queued { get; init; }

    private IEnumerable<(string User, string Pc, TimeRow Row)> All => Sheets.SelectMany(s => s.Rows.Values.Select(r => (s.User, s.Pc, r)));

    public TimeTotals Totals => TimeTotals.Of(All.Select(x => x.Row));

    public static string ToolLabel(string tool) => tool == TimeMath.HandTool ? "(by hand)" : tool == TimeMath.ChatTool ? "Chat" : tool;

    /// <summary>Per tool (by-hand row left out), largest saving first.</summary>
    public List<(string Tool, TimeTotals Totals, int Units)> ByTool() =>
        All.Where(x => x.Row.Tool != TimeMath.HandTool).GroupBy(x => x.Row.Tool, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, TimeTotals.Of(g.Select(x => x.Row)), g.Sum(x => x.Row.Units)))
            .OrderByDescending(t => t.Item2.Saved).ThenByDescending(t => t.Item2.Ai).ToList();

    /// <summary>Per day, newest first.</summary>
    public List<(string Day, TimeTotals Totals)> ByDay() =>
        All.GroupBy(x => x.Row.Day).Select(g => (g.Key, TimeTotals.Of(g.Select(x => x.Row)))).OrderByDescending(t => t.Key, StringComparer.Ordinal).ToList();

    public List<(string User, TimeTotals Totals)> ByUser() =>
        All.GroupBy(x => x.User, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, TimeTotals.Of(g.Select(x => x.Row))))
            .OrderByDescending(t => t.Item2.Working).ToList();

    private static double H(double seconds) => Math.Round(seconds / 3600, 3);

    /// <summary>The Excel sheets: summary, per tool and per day with a user column, and every stored row.</summary>
    public List<(string Name, IList<object?[]> Rows)> ExcelSheets()
    {
        var t = Totals;
        var summary = new List<object?[]>
        {
            new object?[] { "Measure", "Hours", "Note" },
            new object?[] { "Working time", H(t.Working), "Active time only: stops after the idle threshold." },
            new object?[] { "AI time", H(t.Ai), "From the start to the end of each tool or chat task, approval waits included." },
            new object?[] { "By-hand time", H(t.ByHand), "Every other active minute." },
            new object?[] { "Time saved", H(t.Saved), "By-hand estimate minus AI time, per task. Rates are estimates unless set in Settings." },
            new object?[] { "By-hand estimate of the AI tasks", H(t.Estimate), "Rate per run plus rate per item." },
            new object?[] { "Fully by hand (estimate)", H(t.FullyByHand), "Working time plus time saved." },
            new object?[] { "Model", Model, Queued ?? "" },
        };
        var byTool = new List<object?[]> { new object?[] { "User", "Tool", "Runs", "Items", "AI h", "By-hand estimate h", "Saved h" } };
        foreach (var g in All.Where(x => x.Row.Tool != TimeMath.HandTool).GroupBy(x => (x.User, x.Row.Tool)).OrderBy(g => g.Key.User).ThenBy(g => g.Key.Tool))
        {
            var tt = TimeTotals.Of(g.Select(x => x.Row));
            byTool.Add(new object?[] { g.Key.User, ToolLabel(g.Key.Tool), g.Sum(x => x.Row.Runs), g.Sum(x => x.Row.Units), H(tt.Ai), H(tt.Estimate), H(tt.Saved) });
        }
        var byDay = new List<object?[]> { new object?[] { "User", "Day", "Working h", "AI h", "By-hand h", "Saved h" } };
        foreach (var g in All.GroupBy(x => (x.User, x.Row.Day)).OrderByDescending(g => g.Key.Day).ThenBy(g => g.Key.User))
        {
            var tt = TimeTotals.Of(g.Select(x => x.Row));
            byDay.Add(new object?[] { g.Key.User, g.Key.Day, H(tt.Working), H(tt.Ai), H(tt.ByHand), H(tt.Saved) });
        }
        var rows = new List<object?[]> { new object?[] { "User", "PC", "Day", "Tool", "AI h", "By-hand h", "By-hand estimate h", "Saved h", "Runs", "Items" } };
        foreach (var x in All.OrderBy(x => x.Row.Day).ThenBy(x => x.User).ThenBy(x => x.Row.Tool))
            rows.Add(new object?[] { x.User, x.Pc, x.Row.Day, ToolLabel(x.Row.Tool), H(x.Row.Ai), H(x.Row.ByHand), H(x.Row.Estimate), H(x.Row.Saved), x.Row.Runs, x.Row.Units });
        return [("Summary", summary), ("By tool", byTool), ("By day", byDay), ("All rows", rows)];
    }

    /// <summary>The same as CSV sections, for Export for Claude.</summary>
    public string Csv()
    {
        var sb = new StringBuilder();
        foreach (var (name, rows) in ExcelSheets())
        {
            sb.Append(Cell(name)).Append('\n');
            foreach (var row in rows) sb.Append(string.Join(",", row.Select(c => Cell(c switch
            {
                null => "",
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => c.ToString() ?? "",
            })))).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Cell(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>
/// Active minutes and the AI versus by-hand split, minute by minute. Fed with activity (a model change, a selection, a
/// view activation, input while the model's window is in front) and task starts and ends; <see cref="Drain"/> hands
/// back the by-hand seconds of each finished minute. Times are local clock times.
///
/// A minute counts when there was activity in it, or when it lies between two activities in the same model that are
/// at most <see cref="Idle"/> apart (a longer gap counts nothing: the user was away). Activity in another model ends
/// the bridge. A minute belongs to the first model it was counted for. Seconds inside a task are AI time, so a minute
/// shared with a task gives only its other seconds to by-hand time, and activity during a task is not by-hand work.
/// </summary>
internal sealed class ActivityClock
{
    public TimeSpan Idle { get; set; } = TimeSpan.FromMinutes(5);

    private string? _doc;
    private DateTime? _last;
    private readonly SortedDictionary<long, string> _minutes = new();
    private readonly List<Span> _tasks = [];
    // Minutes before this one are drained: never counted again.
    private long _drained;

    /// <summary>A task's time; <see cref="TaskStart"/> hands it out for <see cref="TaskEnd"/>.</summary>
    public sealed class Span
    {
        public DateTime Start { get; init; }
        public DateTime? End { get; set; }
    }

    public int Running => _tasks.Count(t => t.End is null);

    private static long Minute(DateTime t) => t.Ticks / TimeSpan.TicksPerMinute;

    public void Activity(string doc, DateTime at)
    {
        if (doc.Length == 0 || Running > 0) return;
        if (!string.Equals(_doc, doc, StringComparison.OrdinalIgnoreCase)) { _doc = doc; _last = null; }
        var m = Minute(at);
        if (_last is { } last && at >= last && at - last <= Idle)
            for (var k = Minute(last); k <= m; k++) Claim(k, doc);
        else Claim(m, doc);
        if (_last is null || at > _last) _last = at;
    }

    private void Claim(long minute, string doc)
    {
        if (minute >= _drained && !_minutes.ContainsKey(minute)) _minutes[minute] = doc;
    }

    /// <summary>A task starts: its seconds are AI time.</summary>
    public Span TaskStart(DateTime at)
    {
        var span = new Span { Start = at };
        _tasks.Add(span);
        return span;
    }

    /// <summary>
    /// A task ends. Its end is where a bridge may start (the user reads the result), but it is not activity itself: a
    /// user who left during a long task is not counted for the minutes after it.
    /// </summary>
    public void TaskEnd(Span span, string doc, DateTime at)
    {
        if (span.End is not null) return;
        span.End = at < span.Start ? span.Start : at;
        if (doc.Length > 0 && Running == 0)
        {
            _doc = doc;
            _last = at;
        }
    }

    /// <summary>
    /// Finished minutes: each model's by-hand seconds per day. A minute is finished once no bridge can reach it any more
    /// (it is more than <see cref="Idle"/> old) and no running task covers it; task intervals no minute needs are let go.
    /// </summary>
    public List<(string Doc, string Day, double Seconds)> Drain(DateTime now)
    {
        var result = new List<(string, string, double)>();
        var limit = Minute(now - Idle);
        foreach (var t in _tasks) if (t.End is null && Minute(t.Start) < limit) limit = Minute(t.Start);
        foreach (var m in _minutes.Keys.ToList())
        {
            if (m >= limit) break;
            var doc = _minutes[m];
            _minutes.Remove(m);
            var hand = 60 - AiSeconds(m);
            if (hand > 0.0001) result.Add((doc, TimeMath.Day(new DateTime(m * TimeSpan.TicksPerMinute)), hand));
        }
        if (limit > _drained) _drained = limit;
        var needed = new DateTime(_drained * TimeSpan.TicksPerMinute);
        _tasks.RemoveAll(t => t.End is { } end && end < needed);
        return result;
    }

    /// <summary>Seconds of the minute covered by any task (overlapping tasks counted once).</summary>
    private double AiSeconds(long minute)
    {
        var from = new DateTime(minute * TimeSpan.TicksPerMinute);
        var to = from.AddMinutes(1);
        var spans = _tasks.Select(t => (Start: t.Start > from ? t.Start : from, End: (t.End ?? to) < to ? t.End ?? to : to))
            .Where(s => s.End > s.Start).OrderBy(s => s.Start).ToList();
        double total = 0;
        DateTime? curStart = null, curEnd = null;
        foreach (var s in spans)
        {
            if (curEnd is { } e && s.Start <= e) { if (s.End > e) curEnd = s.End; continue; }
            if (curStart is { } cs && curEnd is { } ce) total += (ce - cs).TotalSeconds;
            (curStart, curEnd) = (s.Start, s.End);
        }
        if (curStart is { } fs && curEnd is { } fe) total += (fe - fs).TotalSeconds;
        return Math.Min(60, total);
    }

    /// <summary>Everything counted so far (Revit closing or the core stopping): drains as if it were much later.</summary>
    public List<(string Doc, string Day, double Seconds)> Flush(DateTime now) => Drain(now + Idle + TimeSpan.FromMinutes(2));
}
