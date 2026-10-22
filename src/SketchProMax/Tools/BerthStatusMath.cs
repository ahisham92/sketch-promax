using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// The berth status tracker (Berth status button): for a project with several models and berths, how far each berth is
/// towards its next submission, as a percentage made of areas (modelling, categories and data, coordination, clashes,
/// drawings, details, annotation, schedules and bills, deliverables such as calculations and costing), with the
/// expected finish against the submission's due time (08:00 on the submission day, counting only working hours).
///
/// The setup (models, berths and how to find them in the model, the submission schedule, planned counts, deliverables,
/// weights and the working week) comes from an Excel file the user keeps, imported once (Setup > Import); nothing of a
/// project is in this code. The model is read only when the user presses Update status (BerthStatusFacts, in short
/// steps); the window otherwise shows the last saved results. Results, hand entries, comments and history are kept per
/// project in %APPDATA%\SketchProMax\berth status. No Revit or WPF here, so the tests compile it.
/// </summary>
internal static class BerthStatusMath
{
    // ---- Areas ------------------------------------------------------------------------------------------------------

    /// <summary>An area of a berth's status: its key, its name and what it measures (shown in the window and the Excel).</summary>
    public sealed record Area(string Key, string Name, string Measures, double Weight);

    public const string Modelling = "modelling", CategoryData = "categories", Coordination = "coordination", Clashes = "clashes",
        Drawings = "drawings", Details = "details", Annotation = "annotation", Schedules = "schedules", Deliverables = "deliverables";

    public static readonly Area[] Areas =
    [
        new(Modelling, "Modelling", "Elements modelled in the berth against the planned counts (Planned sheet).", 20),
        new(CategoryData, "Categories and data", "Elements on the right category and ready for the bills: no Generic Models, no in-place families, Assembly Code filled.", 10),
        new(Coordination, "Services and openings", "Pipes, conduits, cable trays and ducts (in this model and every loaded link) that pass through the berth's structure with no opening, deeper than the clash tolerance; open clashes in the Navisworks clash report when one is set; links not loaded.", 15),
        new(Clashes, "Structural clashes", "Structural elements in the berth that run into each other (not joined), deeper than the clash tolerance.", 10),
        new(Drawings, "Drawings", "The berth's sheets against the planned drawings: each with views and a title block.", 20),
        new(Details, "Details", "Detail and section views placed on the berth's sheets, empty drafting views, details not on a sheet.", 10),
        new(Annotation, "Annotation", "On the berth's sheets: elements without a tag, and views without dimensions.", 10),
        new(Schedules, "Schedules and bills", "The berth's schedules exist and are not empty.", 5),
        new(Deliverables, "Deliverables", "Calculations, bills, back-up calculations of the bills, costing and the rest of the Deliverables sheet: by hand, or done when the file is in its folder.", 15),
    ];

    public static Area AreaOf(string key) => Areas.FirstOrDefault(a => a.Key == key) ?? new Area(key, key, "", 0);

    // ---- The setup (from the user's Excel) --------------------------------------------------------------------------

    public sealed class Setup
    {
        public string Title { get; set; } = "";
        public List<Model> Models { get; set; } = [];
        public List<Berth> Berths { get; set; } = [];
        public List<Submission> Submissions { get; set; } = [];
        public List<Planned> Planned { get; set; } = [];
        public List<Deliverable> Deliverables { get; set; } = [];
        /// <summary>Weight per area key; an area missing here keeps its default weight.</summary>
        public Dictionary<string, double> Weights { get; set; } = new(StringComparer.Ordinal);
        public Calendar Week { get; set; } = new();
        /// <summary>The categories whose elements should carry a tag on the sheets (Annotation).</summary>
        public List<string> TagCategories { get; set; } = ["Structural Columns", "Structural Framing", "Structural Foundations", "Specialty Equipment"];
        /// <summary>Overlaps thinner than this (mm) are not clashes: below it the fix isn't buildable or worth doing.</summary>
        public double ClashToleranceMm { get; set; } = 50;
        public string SourcePath { get; set; } = "";
        public string SourceStamp { get; set; } = "";

        public double WeightOf(string area) => Weights.TryGetValue(area, out var w) ? w : AreaOf(area).Weight;
    }

    /// <summary>A model of the project: matched to the open file by text in its file name.</summary>
    /// <param name="Links">Linked models whose elements count for this model's berths (text in the link's name, comma separated): a documentation model issued from linked modelling models.</param>
    /// <param name="ClashReport">The Navisworks clash report (XML), or the folder it is saved in (the newest report).</param>
    public sealed record Model(string Name, string FileContains, string Links = "", string ClashReport = "");

    public static class FindBy
    {
        public const string ScopeBox = "Scope box", Parameter = "Parameter", Workset = "Workset", WholeModel = "Whole model";
        public static readonly string[] All = [ScopeBox, Parameter, Workset, WholeModel];

        public static string Parse(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();
            if (t.Contains("scope")) return ScopeBox;
            if (t.Contains("param")) return Parameter;
            if (t.Contains("workset")) return Workset;
            return WholeModel;
        }
    }

    /// <summary>A berth: which model it is in, and how its elements, sheets and schedules are found there.</summary>
    /// <param name="Value">The scope box name, the parameter value or the text in the workset name.</param>
    /// <param name="Parameter">The parameter holding the berth (FindBy Parameter).</param>
    /// <param name="Sheets">Text in the sheet number or name of the berth's sheets (empty: sheets showing the scope box, or every sheet for a whole model).</param>
    /// <param name="Schedules">Text in the names of the berth's schedules (empty: the berth's name).</param>
    public sealed record Berth(string Model, string Name, string FindBy, string Value = "", string Parameter = "", string Sheets = "", string Schedules = "")
    {
        public string Key => KeyOf(Model, Name);
        public string ScheduleText => Schedules.Trim().Length > 0 ? Schedules.Trim() : Name.Trim();
    }

    public static string KeyOf(string model, string berth) => model.Trim() + " | " + berth.Trim();

    /// <summary>A submission: for one berth, or every berth of the model when Berth is empty. Due at Time (default 08:00) on Date.</summary>
    public sealed record Submission(string Model, string Berth, string Name, DateTime Date, TimeSpan? Time = null, bool Submitted = false);

    /// <summary>How many of something the berth should have when done: a category ("Structural Columns", "Structural Columns / Pile" for types containing Pile), Drawings, Details or Schedules.</summary>
    public sealed record Planned(string Model, string Berth, string What, int Count);

    /// <summary>A deliverable every berth has: done by hand, or when a file whose name has <see cref="Contains"/> ({berth}: the berth's name) is in <see cref="Folder"/>.</summary>
    public sealed record Deliverable(string Name, string Folder = "", string Contains = "");

    /// <summary>The working week: which days, the working hours, and when a submission is due on its day.</summary>
    public sealed class Calendar
    {
        public List<DayOfWeek> Days { get; set; } = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday];
        public TimeSpan Start { get; set; } = TimeSpan.FromHours(8);
        public TimeSpan End { get; set; } = TimeSpan.FromHours(17);
        public TimeSpan Due { get; set; } = TimeSpan.FromHours(8);

        public double HoursPerDay => Math.Max(0, (End - Start).TotalHours);
    }

    // ---- The working calendar ---------------------------------------------------------------------------------------

    /// <summary>The date of a submission made with no date noted.</summary>
    public static readonly DateTime NoDate = DateTime.MinValue;

    /// <summary>The submission's date as text after <paramref name="lead"/>, or "" when it has none.</summary>
    public static string When(Submission s, string format, string lead = " ") => s.Date == NoDate ? "" : lead + s.Date.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>When a submission is due: its time (or the calendar's due time, 08:00) on its date.</summary>
    public static DateTime DueAt(Submission s, Calendar week) => s.Date.Date + (s.Time ?? week.Due);

    /// <summary>Working hours from <paramref name="from"/> to <paramref name="to"/> (0 when to is earlier): only the working days, between start and end.</summary>
    public static double WorkHours(DateTime from, DateTime to, Calendar week)
    {
        if (to <= from || week.HoursPerDay <= 0) return 0;
        double hours = 0;
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            if (!week.Days.Contains(day.DayOfWeek)) continue;
            var a = day + week.Start;
            var b = day + week.End;
            var s = from > a ? from : a;
            var e = to < b ? to : b;
            if (e > s) hours += (e - s).TotalHours;
        }
        return hours;
    }

    /// <summary>The moment <paramref name="hours"/> working hours after <paramref name="from"/>.</summary>
    public static DateTime AddWorkHours(DateTime from, double hours, Calendar week)
    {
        if (week.Days.Count == 0 || week.HoursPerDay <= 0) return from.AddHours(hours);
        var at = from;
        var left = Math.Max(0, hours);
        for (var guard = 0; guard < 3700; guard++)
        {
            var day = at.Date;
            if (week.Days.Contains(day.DayOfWeek))
            {
                var a = day + week.Start;
                var b = day + week.End;
                if (at < a) at = a;
                if (at < b)
                {
                    var room = (b - at).TotalHours;
                    if (left <= room) return at.AddHours(left);
                    left -= room;
                }
            }
            at = day.AddDays(1);
        }
        return at;
    }

    // ---- Results, hand entries and history (saved per project) ------------------------------------------------------

    /// <summary>Something to fix found in the model: the area, what (sheet, view, element), the problem, and its element id when it has one.</summary>
    public sealed record Finding(string Area, string Item, string Problem, long? Id = null);

    /// <summary>What the model reading measured for one area of a berth. Percent null: not measured (no rule or plan for it).</summary>
    public sealed class AreaResult
    {
        public double? Percent { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public string Summary { get; set; } = "";
        public List<Finding> Findings { get; set; } = [];
    }

    /// <summary>A percentage typed by hand (replaces what the model says) and a comment.</summary>
    public sealed class HandEntry
    {
        public double? Percent { get; set; }
        public string Comment { get; set; } = "";
    }

    public sealed record Snapshot(DateTime At, double Percent);

    public sealed class BerthState
    {
        public DateTime? ReadAt { get; set; }
        public string ReadFrom { get; set; } = "";
        public Dictionary<string, AreaResult> Results { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, HandEntry> Hand { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Per deliverable name: its percentage by hand and comment.</summary>
        public Dictionary<string, HandEntry> Delivered { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime? FinishByHand { get; set; }
        public string Comment { get; set; } = "";
        public List<Snapshot> History { get; set; } = [];

        public HandEntry HandFor(string area) => Hand.TryGetValue(area, out var h) ? h : Hand[area] = new HandEntry();
        public HandEntry DeliveredFor(string name) => Delivered.TryGetValue(name, out var h) ? h : Delivered[name] = new HandEntry();
    }

    public sealed class State
    {
        public Dictionary<string, BerthState> Berths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public BerthState For(string key) => Berths.TryGetValue(key, out var b) ? b : Berths[key] = new BerthState();
    }

    // ---- Scores ---------------------------------------------------------------------------------------------------------

    /// <summary>Where an area's percentage came from.</summary>
    public enum Source { Model, Hand, Files, None }

    public sealed record AreaScore(Area Area, double? Percent, Source From, double Weight, AreaResult? Result, HandEntry? Hand);

    /// <summary>A deliverable's state: by hand, else 100 when its file is found, else 0 (or not known when it has no folder).</summary>
    public sealed record DeliverableScore(Deliverable Deliverable, double? Percent, Source From, string File, string Comment);

    public static List<DeliverableScore> DeliverableScores(Setup setup, Berth berth, BerthState state, Func<Deliverable, Berth, string>? findFile = null)
    {
        findFile ??= FindDeliverable;
        var list = new List<DeliverableScore>();
        foreach (var d in setup.Deliverables)
        {
            var hand = state.Delivered.TryGetValue(d.Name, out var h) ? h : null;
            var file = "";
            try { file = findFile(d, berth); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            if (hand?.Percent is { } p) list.Add(new(d, Clamp(p), Source.Hand, file, hand.Comment));
            else if (file.Length > 0) list.Add(new(d, 100, Source.Files, file, hand?.Comment ?? ""));
            else if (d.Folder.Trim().Length > 0) list.Add(new(d, 0, Source.Files, "", hand?.Comment ?? ""));
            else list.Add(new(d, null, Source.None, "", hand?.Comment ?? ""));
        }
        return list;
    }

    /// <summary>The newest file in the deliverable's folder (and its subfolders) whose name has its text, with {berth} as the berth's name; "" when none.</summary>
    public static string FindDeliverable(Deliverable d, Berth berth)
    {
        var folder = d.Folder.Trim().Trim('"');
        if (folder.Length == 0 || !Directory.Exists(folder)) return "";
        var text = (d.Contains.Trim().Length > 0 ? d.Contains : "{berth}").Replace("{berth}", berth.Name.Trim());
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal) && Path.GetFileName(f).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
    }

    public static List<AreaScore> Scores(Setup setup, Berth berth, BerthState state, IReadOnlyList<DeliverableScore>? deliverables = null)
    {
        var list = new List<AreaScore>();
        foreach (var area in Areas)
        {
            var weight = setup.WeightOf(area.Key);
            var hand = state.Hand.TryGetValue(area.Key, out var h) ? h : null;
            state.Results.TryGetValue(area.Key, out var result);
            if (hand?.Percent is { } p) { list.Add(new(area, Clamp(p), Source.Hand, weight, result, hand)); continue; }
            if (area.Key == Deliverables)
            {
                var known = (deliverables ?? DeliverableScores(setup, berth, state)).Where(d => d.Percent is not null).ToList();
                list.Add(new(area, known.Count == 0 ? null : known.Average(d => d.Percent!.Value), known.Count == 0 ? Source.None : Source.Files, weight, null, hand));
                continue;
            }
            list.Add(new(area, result?.Percent is { } m ? Clamp(m) : null, result?.Percent is null ? Source.None : Source.Model, weight, result, hand));
        }
        return list;
    }

    /// <summary>The berth's status: the weighted average of the areas that have a percentage (null when none has).</summary>
    public static double? Overall(IEnumerable<AreaScore> scores)
    {
        var known = scores.Where(s => s.Percent is not null && s.Weight > 0).ToList();
        var weights = known.Sum(s => s.Weight);
        return weights <= 0 ? null : known.Sum(s => s.Percent!.Value * s.Weight) / weights;
    }

    public static double Clamp(double p) => Math.Max(0, Math.Min(100, p));

    /// <summary>A part of a whole as a percentage: 100 when there is nothing to have.</summary>
    public static double Part(double done, double total) => total <= 0 ? 100 : Clamp(100.0 * done / total);

    // ---- Schedule, forecast and verdict -----------------------------------------------------------------------------

    /// <summary>The submissions for a berth (its own, and the model's for every berth), by due time.</summary>
    public static List<Submission> SubmissionsFor(Setup setup, Berth berth) =>
        setup.Submissions.Where(s => Same(s.Model, berth.Model) && (s.Berth.Trim().Length == 0 || Same(s.Berth, berth.Name)))
            .OrderBy(s => DueAt(s, setup.Week)).ToList();

    /// <summary>The next submission not yet made (an overdue one first), or null.</summary>
    public static Submission? Next(Setup setup, Berth berth) => SubmissionsFor(setup, berth).FirstOrDefault(s => !s.Submitted);

    /// <summary>
    /// When the berth should reach 100% at the pace of its readings: the rate between the oldest reading of the last
    /// <paramref name="days"/> days that was lower and now, in working hours. Null without two readings that moved forward.
    /// </summary>
    public static DateTime? Forecast(IReadOnlyList<Snapshot> history, double percent, DateTime now, Calendar week, int days = 21)
    {
        if (percent >= 100) return now;
        var from = history.Where(h => h.At >= now.AddDays(-days) && h.At < now && h.Percent < percent).OrderBy(h => h.At).FirstOrDefault();
        if (from is null) return null;
        var hours = WorkHours(from.At, now, week);
        if (hours < 1) return null;
        var rate = (percent - from.Percent) / hours;
        if (rate <= 0) return null;
        return AddWorkHours(now, (100 - percent) / rate, week);
    }

    public enum Verdict { Done, OnTrack, AtRisk, Behind, Overdue, NotKnown, NoSubmission, Submitted }

    /// <summary>
    /// How the berth stands for its next submission: Done at 100% (or submitted), Overdue when the due time has passed,
    /// On track when the expected finish is before it, At risk when it is less than a working day late, Behind otherwise,
    /// and Not known without an expected finish.
    /// </summary>
    public static Verdict Judge(double? percent, Submission? next, DateTime? finish, DateTime now, Calendar week)
    {
        if (next is null) return percent >= 100 ? Verdict.Done : Verdict.NoSubmission;
        if (percent >= 100) return Verdict.Done;
        var due = DueAt(next, week);
        if (due <= now) return Verdict.Overdue;
        if (finish is null) return Verdict.NotKnown;
        if (finish <= due) return Verdict.OnTrack;
        return WorkHours(due, finish.Value, week) <= Math.Max(1, week.HoursPerDay) ? Verdict.AtRisk : Verdict.Behind;
    }

    public static string Text(Verdict v) => v switch
    {
        Verdict.Done => "Done",
        Verdict.OnTrack => "On track",
        Verdict.AtRisk => "At risk",
        Verdict.Behind => "Behind",
        Verdict.Overdue => "Overdue",
        Verdict.NoSubmission => "No submission set",
        Verdict.Submitted => "Submitted, fixes open",
        _ => "Needs two readings",
    };

    /// <summary>The percentage needed per working day from now to finish by the due time (null when due has passed).</summary>
    public static double? NeededPerDay(double percent, DateTime due, DateTime now, Calendar week)
    {
        var hours = WorkHours(now, due, week);
        if (hours <= 0) return null;
        return (100 - Clamp(percent)) / hours * Math.Max(1, week.HoursPerDay);
    }

    /// <summary>One line per berth for the overview and the AI question.</summary>
    /// <param name="Last">When every submission of the berth is made: the last one (its status still counts what is left to fix).</param>
    public sealed record Summary(Berth Berth, double? Percent, Submission? Next, DateTime? Due, double HoursLeft, DateTime? Finish, bool FinishByHand, Verdict Verdict, List<AreaScore> Scores, List<DeliverableScore> Deliverables, BerthState State, Submission? Last = null);

    public static Summary Summarize(Setup setup, Berth berth, State state, DateTime now, Func<Deliverable, Berth, string>? findFile = null)
    {
        var b = state.For(berth.Key);
        var deliverables = DeliverableScores(setup, berth, b, findFile);
        var scores = Scores(setup, berth, b, deliverables);
        var percent = Overall(scores);
        var next = Next(setup, berth);
        DateTime? due = next is null ? null : DueAt(next, setup.Week);
        var hours = due is null ? 0 : WorkHours(now, due.Value, setup.Week);
        var forecast = percent is null ? null : Forecast(b.History, percent.Value, now, setup.Week);
        var finish = b.FinishByHand ?? forecast;
        // Submitted already: still checked, so what the BIM submission needs fixed stays in view.
        var last = next is null ? SubmissionsFor(setup, berth).LastOrDefault(x => x.Submitted) : null;
        var verdict = last is not null ? (percent >= 100 ? Verdict.Done : Verdict.Submitted) : Judge(percent, next, finish, now, setup.Week);
        return new Summary(berth, percent, next, due, hours, finish, b.FinishByHand is not null, verdict, scores, deliverables, b, last);
    }

    /// <summary>The berth a text names: its name, its code (the first word, such as T3), its key, or the only one containing the text.</summary>
    public static Berth? FindBerth(Setup setup, string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        var exact = setup.Berths.Where(b => Same(b.Name, t) || Same(b.Key, t) || Same(b.Name.Split(' ')[0], t)).ToList();
        if (exact.Count == 1) return exact[0];
        var part = setup.Berths.Where(b => b.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 || b.Key.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        return part.Count == 1 ? part[0] : null;
    }

    /// <summary>
    /// Sets a percentage (null: back to the model or the files) and/or a comment on an area or a deliverable of a berth,
    /// as typed in the window or told to the chat. Returns what was set, or throws ArgumentException naming the choices.
    /// </summary>
    public static string SetByHand(Setup setup, State state, Berth berth, string what, double? percent, string? comment, bool clearPercent = false)
    {
        var b = state.For(berth.Key);
        var w = (what ?? "").Trim();
        HandEntry entry;
        string name;
        if (Same(w, "berth") || Same(w, "comment") || w.Length == 0)
        {
            if (comment is null) throw new ArgumentException("Give the comment for the berth.");
            b.Comment = comment;
            return $"{berth.Name}: comment saved.";
        }
        var area = Areas.FirstOrDefault(a => Same(a.Key, w) || Same(a.Name, w)) ?? Areas.FirstOrDefault(a => a.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        var deliverable = setup.Deliverables.FirstOrDefault(d => Same(d.Name, w)) ?? setup.Deliverables.FirstOrDefault(d => d.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        if (deliverable is not null && (area is null || Same(deliverable.Name, w))) { entry = b.DeliveredFor(deliverable.Name); name = deliverable.Name; }
        else if (area is not null) { entry = b.HandFor(area.Key); name = area.Name; }
        else throw new ArgumentException($"\"{w}\" is not an area or a deliverable. Areas: {string.Join(", ", Areas.Select(a => a.Name))}. Deliverables: {string.Join(", ", setup.Deliverables.Select(d => d.Name))}.");
        if (clearPercent) entry.Percent = null;
        else if (percent is { } p) entry.Percent = Clamp(p);
        if (comment is not null) entry.Comment = comment;
        return $"{berth.Name}, {name}: " + (entry.Percent is { } now ? $"{now:0}% by hand" : "from the model or files") + (entry.Comment.Length > 0 ? $"; comment: {entry.Comment}" : "") + ".";
    }

    /// <summary>Saves a reading's results for the berth and adds its percentage to the history (one point per hour at most).</summary>
    public static void Record(Setup setup, Berth berth, State state, Dictionary<string, AreaResult> results, string model, DateTime now)
    {
        var b = state.For(berth.Key);
        // A new reading replaces the last one whole: an area it couldn't measure now isn't kept from before.
        b.Results = new Dictionary<string, AreaResult>(results, StringComparer.Ordinal);
        b.ReadAt = now;
        b.ReadFrom = model;
        if (Overall(Scores(setup, berth, b)) is { } p)
        {
            b.History.RemoveAll(h => h.At > now.AddHours(-1) && h.At <= now);
            b.History.Add(new Snapshot(now, Math.Round(p, 1)));
            if (b.History.Count > 400) b.History.RemoveRange(0, b.History.Count - 400);
        }
    }

    /// <summary>The question the Ask button sends to the chat for an expected finish (the AI estimate).</summary>
    public static string Question(Setup setup, Summary s, DateTime now)
    {
        var lines = new List<string>
        {
            $"Berth status tracker: estimate when {s.Berth.Name} ({s.Berth.Model}) will be ready for submission. Don't change the model.",
            $"Now: {now:ddd dd-MMM-yyyy HH:mm}. Working days: {string.Join(", ", setup.Week.Days.Select(d => d.ToString().Substring(0, 3)))}, {setup.Week.Start:hh\\:mm}-{setup.Week.End:hh\\:mm}. A submission is due at its time (default {setup.Week.Due:hh\\:mm}) on its day, with calculations, bills, their back-up calculations and costing.",
            $"Status now: {Pct(s.Percent)}." + (s.Next is null ? (s.Last is { } last ? $" Already submitted ({last.Name}); estimate when what is left to fix will be done." : " No submission set.") : $" Next submission: {s.Next.Name}, due {s.Due:ddd dd-MMM-yyyy HH:mm} ({s.HoursLeft:0} working hours left)."),
        };
        foreach (var a in s.Scores) lines.Add($"- {a.Area.Name}: {Pct(a.Percent)}" + (a.Result?.Summary is { Length: > 0 } sum ? $" ({sum})" : "") + (a.Hand?.Comment is { Length: > 0 } c ? $" Comment: {c}" : ""));
        foreach (var d in s.Deliverables) lines.Add($"- Deliverable {d.Deliverable.Name}: {Pct(d.Percent)}" + (d.Comment.Length > 0 ? $" ({d.Comment})" : ""));
        if (s.State.History.Count > 0) lines.Add("Readings: " + string.Join(", ", s.State.History.Skip(Math.Max(0, s.State.History.Count - 12)).Select(h => $"{h.At:dd-MMM HH:mm} {h.Percent:0}%")));
        if (s.State.Comment.Length > 0) lines.Add("Berth comment: " + s.State.Comment);
        lines.Add("Use the model to judge what is left where it helps (sheets, views, tags, schedules). Answer with the expected finish date and time on the first line, then the reasons and the three things that would bring it forward most.");
        return string.Join("\n", lines);
    }

    public static string Pct(double? p) => p is null ? "not measured" : Math.Round(p.Value).ToString(CultureInfo.InvariantCulture) + "%";

    public static bool Same(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The setup's model whose file-name text is in <paramref name="file"/> (the longest text wins), or null.</summary>
    public static Model? ModelFor(Setup setup, string file)
    {
        var name = Path.GetFileName(file ?? "");
        return setup.Models.Where(m => m.FileContains.Trim().Length > 0 && name.IndexOf(m.FileContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(m => m.FileContains.Trim().Length).FirstOrDefault();
    }

    public static List<Berth> BerthsOf(Setup setup, Model model) => setup.Berths.Where(b => Same(b.Model, model.Name)).ToList();

    /// <summary>The planned count of <paramref name="what"/> for the berth, or null.</summary>
    public static int? PlannedCount(Setup setup, Berth berth, string what) =>
        setup.Planned.Where(p => Same(p.Model, berth.Model) && Same(p.Berth, berth.Name) && Same(p.What, what)).Select(p => (int?)p.Count).FirstOrDefault();

    /// <summary>The berth's planned element counts (not Drawings, Details or Schedules), split into category and type text.</summary>
    public static List<(string Category, string Type, int Count, string What)> PlannedElements(Setup setup, Berth berth) =>
        setup.Planned.Where(p => Same(p.Model, berth.Model) && Same(p.Berth, berth.Name) && !IsPlanKind(p.What))
            .Select(p =>
            {
                var parts = p.What.Split(['/'], 2);
                return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "", p.Count, p.What.Trim());
            }).ToList();

    private static bool IsPlanKind(string what) => Same(what, "Drawings") || Same(what, "Details") || Same(what, "Schedules");

    // ---- Storage ------------------------------------------------------------------------------------------------------

    /// <summary>The tests point this at a temporary folder.</summary>
    [ThreadStatic] internal static string? FolderForTests;

    public static string Folder => FolderForTests ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "berth status");

    private static string ProjectFolder(string project) => Path.Combine(Folder, ChecklistProjects.CleanName(project.Length == 0 ? "Project" : project));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Setup? LoadSetup(string project)
    {
        var file = Path.Combine(ProjectFolder(project), "setup.json");
        try { return SafeFile.Read(file) is { } text ? JsonSerializer.Deserialize<Setup>(text) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    public static void SaveSetup(string project, Setup setup) => SafeFile.Write(Path.Combine(ProjectFolder(project), "setup.json"), JsonSerializer.Serialize(setup, Json));

    /// <summary>The saved results' file text (to notice a change made elsewhere, such as by the chat), or "".</summary>
    public static string StateText(string project)
    {
        try { return SafeFile.Read(Path.Combine(ProjectFolder(project), "status.json")) ?? ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public static State LoadState(string project)
    {
        var file = Path.Combine(ProjectFolder(project), "status.json");
        try { return SafeFile.Read(file) is { } text ? JsonSerializer.Deserialize<State>(text) ?? new State() : new State(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return new State(); }
    }

    public static void SaveState(string project, State state) => SafeFile.Write(Path.Combine(ProjectFolder(project), "status.json"), JsonSerializer.Serialize(state, Json));

    /// <summary>The projects set up on this PC.</summary>
    public static List<string> Projects() =>
        !Directory.Exists(Folder) ? [] : Directory.GetDirectories(Folder).Where(d => File.Exists(Path.Combine(d, "setup.json"))).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList();

    // ---- The setup Excel ----------------------------------------------------------------------------------------------

    private static readonly string[] ModelCols = ["Model", "File name contains", "Linked models to include", "Clash report"];
    private static readonly string[] BerthCols = ["Model", "Berth", "Find elements by", "Value", "Parameter", "Sheets contain", "Schedules contain"];
    private static readonly string[] SubmissionCols = ["Model", "Berth", "Submission", "Date", "Time", "Submitted"];
    private static readonly string[] PlannedCols = ["Model", "Berth", "What", "Planned"];
    private static readonly string[] DeliverableCols = ["Deliverable", "Folder", "File name contains"];
    private static readonly string[] WeightCols = ["Area", "Weight", "What it measures"];
    private static readonly string[] WeekCols = ["Setting", "Value"];

    /// <summary>The setup as an Excel workbook to fill in or change, and import back.</summary>
    public static List<OfficeXlsx.Sheet> SetupSheets(Setup s)
    {
        OfficeXlsx.Sheet Sheet(string name, string title, string[] cols, IEnumerable<object?[]> rows, double[] widths) =>
            new(name, title, cols, rows.Select(r => new OfficeXlsx.Row(r)).ToList(), cols.Select(_ => "text").ToArray(), widths, true);
        var week = s.Week;
        return
        [
            Sheet("Models", "Models: one row per model; File name contains matches the open file", ModelCols,
                s.Models.Select(m => new object?[] { m.Name, m.FileContains, m.Links, m.ClashReport }), [18, 26, 30, 50]),
            Sheet("Berths", "Berths: Find elements by is Scope box, Parameter, Workset or Whole model", BerthCols,
                s.Berths.Select(b => new object?[] { b.Model, b.Name, b.FindBy, b.Value, b.Parameter, b.Sheets, b.Schedules }), [18, 18, 18, 22, 18, 18, 18]),
            Sheet("Submissions", "Submissions: Berth empty means every berth of the model; Time empty means the due time (08:00)", SubmissionCols,
                s.Submissions.Select(x => new object?[] { x.Model, x.Berth, x.Name, x.Date == NoDate ? "" : x.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x.Time is { } t ? t.ToString(@"hh\:mm", CultureInfo.InvariantCulture) : "", x.Submitted ? "Yes" : "" }), [18, 18, 34, 14, 10, 12]),
            Sheet("Planned", "Planned: a category (Structural Columns / Pile for its types with Pile), or Drawings, Details, Schedules", PlannedCols,
                s.Planned.Select(p => new object?[] { p.Model, p.Berth, p.What, p.Count.ToString(CultureInfo.InvariantCulture) }), [18, 18, 34, 12]),
            Sheet("Deliverables", "Deliverables of every berth: done by hand, or when a file with the text ({berth} = the berth's name) is in the folder", DeliverableCols,
                s.Deliverables.Select(d => new object?[] { d.Name, d.Folder, d.Contains }), [34, 50, 26]),
            Sheet("Weights", "Weights: how much each area counts in a berth's status", WeightCols,
                Areas.Select(a => new object?[] { a.Name, s.WeightOf(a.Key).ToString(CultureInfo.InvariantCulture), a.Measures }), [22, 10, 90]),
            Sheet("Week", "Working week", WeekCols,
            [
                ["Title", s.Title],
                ["Working days", string.Join(", ", week.Days.Select(d => d.ToString()))],
                ["Start", week.Start.ToString(@"hh\:mm", CultureInfo.InvariantCulture)],
                ["End", week.End.ToString(@"hh\:mm", CultureInfo.InvariantCulture)],
                ["Due time", week.Due.ToString(@"hh\:mm", CultureInfo.InvariantCulture)],
                ["Tag categories", string.Join(", ", s.TagCategories)],
                ["Clash tolerance (mm)", s.ClashToleranceMm.ToString(CultureInfo.InvariantCulture)],
            ], [22, 70]),
        ];
    }

    /// <summary>A setup to start from, with made-up names (the user replaces them with the project's).</summary>
    public static Setup Example() => new()
    {
        Title = "Berth status",
        Models = [new("Port A", "PORTA"), new("Port B", "PORTB-DOC", "PORTB")],
        Berths =
        [
            new("Port A", "Berth 1", FindBy.ScopeBox, "SB_Berth 1", "", "B01"),
            new("Port A", "Berth 2", FindBy.Parameter, "Berth 2", "Zone", "B02"),
            new("Port B", "Berth 3", FindBy.WholeModel),
        ],
        Submissions =
        [
            new("Port A", "Berth 1", "Stage 2 submission", DateTime.Today.AddDays(14)),
            new("Port B", "", "Stage 2 submission", DateTime.Today.AddDays(21)),
        ],
        Planned =
        [
            new("Port A", "Berth 1", "Structural Columns / Pile", 120),
            new("Port A", "Berth 1", "Structural Framing", 60),
            new("Port A", "Berth 1", "Drawings", 25),
            new("Port A", "Berth 1", "Details", 12),
            new("Port A", "Berth 1", "Schedules", 4),
        ],
        Deliverables =
        [
            new("Calculations"), new("Bills of quantities"), new("Back-up calculations of the bills"), new("Costing"),
        ],
    };

    /// <summary>Reads the setup Excel. Throws InvalidDataException with a plain reason.</summary>
    public static Setup ReadSetup(string path)
    {
        Dictionary<string, List<(int Row, Dictionary<int, string> Cells)>> sheets;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            sheets = ChecklistMath.ReadSheets(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or NotSupportedException)
        {
            throw new InvalidDataException("it isn't an Excel workbook (.xlsx) Sketch ProMax can read. Save it from Excel as .xlsx and pick it again.", ex);
        }
        var setup = FromSheets(sheets);
        setup.SourcePath = path;
        setup.SourceStamp = ChecklistMath.Stamp(path);
        return setup;
    }

    internal static Setup FromSheets(Dictionary<string, List<(int Row, Dictionary<int, string> Cells)>> sheets)
    {
        var s = new Setup { Title = "Berth status" };
        var problems = new List<string>();
        List<Dictionary<string, string>> Table(string sheet, string[] cols)
        {
            var list = new List<Dictionary<string, string>>();
            if (!sheets.TryGetValue(sheet, out var rows)) return list;
            Dictionary<int, string>? map = null;
            foreach (var (_, cells) in rows)
            {
                if (map is null)
                {
                    // The heading row: the first that names the first two columns.
                    var named = cells.Where(c => cols.Any(h => Same(h, c.Value))).ToDictionary(c => c.Key, c => cols.First(h => Same(h, c.Value)));
                    if (named.Count >= Math.Min(2, cols.Length)) map = named;
                    continue;
                }
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in map) row[pair.Value] = cells.TryGetValue(pair.Key, out var v) ? v.Trim() : "";
                if (row.Values.Any(v => v.Length > 0)) list.Add(row);
            }
            return list;
        }
        string V(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : "";

        foreach (var r in Table("Models", ModelCols))
            if (V(r, "Model").Length > 0) s.Models.Add(new(V(r, "Model"), V(r, "File name contains").Length > 0 ? V(r, "File name contains") : V(r, "Model"), V(r, "Linked models to include"), V(r, "Clash report")));
        foreach (var r in Table("Berths", BerthCols))
        {
            if (V(r, "Berth").Length == 0) continue;
            var model = V(r, "Model").Length > 0 ? V(r, "Model") : s.Models.FirstOrDefault()?.Name ?? "";
            if (s.Models.Count > 0 && !s.Models.Any(m => Same(m.Name, model))) problems.Add($"berth {V(r, "Berth")}: model \"{model}\" isn't on the Models sheet");
            s.Berths.Add(new(model, V(r, "Berth"), FindBy.Parse(V(r, "Find elements by")), V(r, "Value"), V(r, "Parameter"), V(r, "Sheets contain"), V(r, "Schedules contain")));
        }
        foreach (var r in Table("Submissions", SubmissionCols))
        {
            // A package submitted before its date was noted: Submitted with no date.
            if (V(r, "Date").Length == 0)
            {
                if (IsYes(V(r, "Submitted"))) s.Submissions.Add(new(V(r, "Model"), V(r, "Berth"), V(r, "Submission").Length > 0 ? V(r, "Submission") : "Submission", NoDate, null, true));
                continue;
            }
            if (ParseDate(V(r, "Date")) is not { } date) { problems.Add($"submission \"{V(r, "Submission")}\": \"{V(r, "Date")}\" isn't a date"); continue; }
            var time = ParseTime(V(r, "Time"));
            s.Submissions.Add(new(V(r, "Model"), V(r, "Berth"), V(r, "Submission").Length > 0 ? V(r, "Submission") : "Submission", date, time, IsYes(V(r, "Submitted"))));
        }
        foreach (var r in Table("Planned", PlannedCols))
            if (V(r, "What").Length > 0 && double.TryParse(V(r, "Planned"), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                s.Planned.Add(new(V(r, "Model"), V(r, "Berth"), V(r, "What"), (int)Math.Round(n)));
        foreach (var r in Table("Deliverables", DeliverableCols))
            if (V(r, "Deliverable").Length > 0) s.Deliverables.Add(new(V(r, "Deliverable"), V(r, "Folder"), V(r, "File name contains")));
        foreach (var r in Table("Weights", WeightCols))
        {
            var area = Areas.FirstOrDefault(a => Same(a.Name, V(r, "Area")) || Same(a.Key, V(r, "Area")));
            if (area is not null && double.TryParse(V(r, "Weight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) s.Weights[area.Key] = Math.Max(0, w);
        }
        foreach (var r in Table("Week", WeekCols))
        {
            var value = V(r, "Value");
            switch (V(r, "Setting").ToLowerInvariant())
            {
                case "title": if (value.Length > 0) s.Title = value; break;
                case "working days": if (ParseDays(value) is { Count: > 0 } days) s.Week.Days = days; break;
                case "start": if (ParseTime(value) is { } a) s.Week.Start = a; break;
                case "end": if (ParseTime(value) is { } b) s.Week.End = b; break;
                case "due time": if (ParseTime(value) is { } d) s.Week.Due = d; break;
                case "clash tolerance (mm)":
                case "clash tolerance":
                    if (double.TryParse(value.Replace("mm", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tol) && tol >= 0) s.ClashToleranceMm = tol;
                    break;
                case "tag categories": s.TagCategories = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); break;
            }
        }
        if (s.Berths.Count == 0) problems.Insert(0, "the Berths sheet has no berths (it needs the columns Model and Berth)");
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems) + ".");
        return s;
    }

    private static bool IsYes(string v) => v.Trim().ToLowerInvariant() is "yes" or "y" or "x" or "done" or "submitted" or "true" or "1";

    /// <summary>A date from the sheet: an Excel date number, or text such as 2026-10-29, 29/10/2026 or 29-Oct-2026.</summary>
    public static DateTime? ParseDate(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > 20000 && serial < 80000)
            return DateTime.FromOADate(Math.Floor(serial)).Date;
        string[] formats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "dd/MM/yyyy", "d/M/yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yy", "d MMMM yyyy", "dddd d MMMM yyyy", "ddd dd-MMM-yyyy", "yyyy/MM/dd", "dd.MM.yyyy"];
        if (DateTime.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d.Date;
        return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d) ? d.Date : null;
    }

    /// <summary>A time: 08:00, 8:00 AM, 8 am, or an Excel time fraction (0.333).</summary>
    public static TimeSpan? ParseTime(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f >= 0 && f < 1) return TimeSpan.FromMinutes(Math.Round(f * 24 * 60));
        var m = Regex.Match(t, @"^(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (m.Groups[3].Success)
        {
            var pm = m.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            if (h == 12) h = pm ? 12 : 0;
            else if (pm) h += 12;
        }
        return h < 24 && min < 60 ? new TimeSpan(h, min, 0) : null;
    }

    /// <summary>Days from "Sunday, Monday" or "Sun-Thu".</summary>
    public static List<DayOfWeek> ParseDays(string text)
    {
        static DayOfWeek? Day(string s)
        {
            s = s.Trim().ToLowerInvariant();
            if (s.Length < 2) return null;
            foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
                if (d.ToString().ToLowerInvariant().StartsWith(s.Length >= 3 ? s.Substring(0, 3) : s, StringComparison.Ordinal)) return d;
            return null;
        }
        var days = new List<DayOfWeek>();
        foreach (var part in (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split(['-', '–'], 2);
            if (range.Length == 2 && Day(range[0]) is { } a && Day(range[1]) is { } b)
            {
                for (var d = (int)a; ; d = (d + 1) % 7) { if (!days.Contains((DayOfWeek)d)) days.Add((DayOfWeek)d); if (d == (int)b) break; }
            }
            else if (Day(part) is { } one && !days.Contains(one)) days.Add(one);
        }
        return days;
    }

    // ---- The berth's Excel status file ------------------------------------------------------------------------------

    /// <summary>The berth's status workbook: Status, Submissions, Deliverables, one sheet per area with what to fix, Comments and History.</summary>
    public static List<OfficeXlsx.Sheet> BerthSheets(Setup setup, Summary s, DateTime now)
    {
        var sheets = new List<OfficeXlsx.Sheet>();
        var b = s.State;
        string From(Source f) => f switch { Source.Model => "Model", Source.Hand => "By hand", Source.Files => "Files", _ => "Not measured" };
        object? P(double? p) => p is null ? "" : Math.Round(p.Value / 100, 3);

        var status = new List<OfficeXlsx.Row>();
        foreach (var a in s.Scores)
            status.Add(new([a.Area.Name, P(a.Percent), a.Weight, From(a.From), a.Result?.Done.ToString(CultureInfo.InvariantCulture) ?? "", a.Result?.Total.ToString(CultureInfo.InvariantCulture) ?? "", a.Result?.Summary ?? "", a.Hand?.Comment ?? ""]));
        status.Add(new(["Overall", P(s.Percent), s.Scores.Where(x => x.Percent is not null).Sum(x => x.Weight), "", "", "", Text(s.Verdict), b.Comment], Bold: true));
        status.Add(new(["", "", "", "", "", "", "", ""]));
        status.Add(new(["Next submission", "", "", "", "", "", s.Next?.Name ?? (s.Last is { } last ? $"All submitted (last: {last.Name}{When(last, "dd-MMM-yyyy", ", ")}); what is left to fix still counts" : "None set"), ""]));
        status.Add(new(["Due", "", "", "", "", "", s.Due is { } due ? due.ToString("ddd dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture) : "", ""]));
        status.Add(new(["Working hours left", "", "", "", "", "", s.Due is null ? "" : Math.Round(s.HoursLeft, 1).ToString(CultureInfo.InvariantCulture), ""]));
        status.Add(new(["Expected finish", "", "", s.FinishByHand ? "By hand" : "From the readings", "", "", s.Finish is { } f ? f.ToString("ddd dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture) : "Needs two readings", ""]));
        if (s.Percent is { } pc && s.Due is { } d2 && NeededPerDay(pc, d2, now, setup.Week) is { } need)
            status.Add(new(["Needed per working day", Math.Round(need / 100, 3), "", "", "", "", "", ""]));
        status.Add(new(["Model read", "", "", "", "", "", b.ReadAt is { } r ? r.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture) + " from " + b.ReadFrom : "Not read yet", ""]));
        sheets.Add(new("Status", $"{s.Berth.Name} ({s.Berth.Model}) status, {now:dd-MMM-yyyy HH:mm}",
            ["Area", "Status", "Weight", "From", "Done", "Of", "What the model shows", "Comment"], status,
            ["text", "percent", "decimal", "text", "text", "text", "text", "text"], [24, 10, 9, 14, 8, 8, 60, 40], true));

        var subs = SubmissionsFor(setup, s.Berth).Select(x =>
        {
            var due = DueAt(x, setup.Week);
            return new OfficeXlsx.Row([x.Name, x.Date == NoDate ? "" : due.ToString("ddd dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture), x.Submitted ? "" : Math.Round(WorkHours(now, due, setup.Week), 1).ToString(CultureInfo.InvariantCulture), x.Submitted ? "Submitted" : due <= now ? "Overdue" : "To come"]);
        }).ToList();
        sheets.Add(new("Submissions", "Submissions (due at their time on the day; working hours only)", ["Submission", "Due", "Working hours left", "State"], subs, ["text", "text", "text", "text"], [34, 24, 18, 14], false));

        var dels = s.Deliverables.Select(x => new OfficeXlsx.Row([x.Deliverable.Name, P(x.Percent), From(x.From), x.File, x.Comment])).ToList();
        sheets.Add(new("Deliverables", "Calculations, bills, back-up calculations, costing", ["Deliverable", "Status", "From", "File", "Comment"], dels, ["text", "percent", "text", "text", "text"], [36, 10, 12, 60, 40], true));

        foreach (var a in s.Scores.Where(x => x.Result?.Findings.Count > 0))
            sheets.Add(new(a.Area.Name, $"{a.Area.Name}: what to fix ({a.Result!.Findings.Count})", ["Item", "Problem", "Element id", "Comment"],
                a.Result.Findings.Select(f => new OfficeXlsx.Row([f.Item, f.Problem, f.Id?.ToString(CultureInfo.InvariantCulture) ?? "", ""])).ToList(),
                ["text", "text", "text", "text"], [44, 60, 14, 40], true));

        var comments = new List<OfficeXlsx.Row>();
        if (b.Comment.Length > 0) comments.Add(new(["Berth", b.Comment]));
        foreach (var a in s.Scores.Where(x => x.Hand?.Comment.Length > 0)) comments.Add(new([a.Area.Name, a.Hand!.Comment]));
        foreach (var x in s.Deliverables.Where(x => x.Comment.Length > 0)) comments.Add(new([x.Deliverable.Name, x.Comment]));
        sheets.Add(new("Comments", "Comments", ["About", "Comment"], comments, ["text", "text"], [30, 90], false));

        sheets.Add(new("History", "Status at each reading", ["Read at", "Status"],
            b.History.Select(h => new OfficeXlsx.Row([h.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Math.Round(h.Percent / 100, 3)])).ToList(), ["text", "percent"], [20, 10], false));
        return sheets;
    }

    /// <summary>Where the berth's Excel goes: Documents\Sketch ProMax\Berth status\Project, named by berth and time.</summary>
    public static string ExportPath(string project, Berth berth, DateTime now)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Berth status", ChecklistProjects.CleanName(project.Length == 0 ? "Project" : project));
        Directory.CreateDirectory(folder);
        return OfficeDocs.FreePath(folder, OfficeDocs.SafeName($"{berth.Name} {berth.Model} status {now:yyyy-MM-dd HHmm}"), ".xlsx");
    }
}
