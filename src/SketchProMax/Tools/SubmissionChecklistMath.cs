using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// The technical submission checklist (opened from Submissions status and the Model checklist; UI/SubmissionChecklistWindow):
/// short questions an engineer answers Yes, No or N/A for each submission, which a group leader then approves or returns.
/// The project picks its structure types once (deck on piles, gravity blocks, sheet piles…), so only the questions for
/// those types are asked. The questions come from the questions template (SubmissionChecklistTemplate), which only its
/// owner can change; it travels in the records too, so everyone on a model asks the same questions. The BIM part is the Model checklist, shown beside it as a percentage.
///
/// Sharing goes through the model: each person's answers, approvals and added questions are one record (one DataStorage
/// element each, Revit/SubmissionChecklistStore), so two people never fight over the same element; Synchronize with
/// Central sends a record, Reload Latest brings the others'. <see cref="Merge"/> puts the records together: per question
/// the newest answer and the newest review win; a review counts only when a group leader made it after the answer it
/// reviews; an N/A is hidden only once a group leader approved it. No Revit or WPF here, so the tests compile it.
/// </summary>
internal static class SubmissionChecklistMath
{
    public const string Yes = "Y", No = "N", NotApplicable = "NA";
    public const string Approved = "approved", Returned = "returned";

    // ---- Structure types ------------------------------------------------------------------------------------------

    /// <summary>A kind of marine structure the project has: its key, its name, a few words about it, and its section diagram.</summary>
    public sealed record StructureType(string Key, string Name, string About);

    public static readonly StructureType[] Types =
    [
        new("piled-deck", "Deck on piles", "Open piled wharf or jetty head: deck slab and beams on vertical and raked piles, slope under the deck."),
        new("gravity-blocks", "Gravity blocks", "Blockwork quay wall: precast blocks stacked on a rubble bed, backfilled."),
        new("caissons", "Caissons", "Floated-in concrete caissons on a rubble bed, filled and backfilled."),
        new("sheet-piles", "Sheet pile quay", "Anchored steel sheet pile wall with tie rods to an anchor wall, cope beam on top."),
        new("combi-wall", "Combi-wall quay", "Tubular or H king piles with shorter infill sheet piles between them, anchored, cope beam on top."),
        new("relieving-platform", "Relieving platform quay", "Front sheet or combi-wall with a concrete platform on bearing piles behind it, carrying the fill and surcharge."),
        new("retaining-wall", "Retaining walls", "Cantilever, L-shaped or counterfort reinforced concrete walls holding back fill."),
        new("dolphins", "Dolphins", "Mooring and breasting dolphins: pile groups with a cap, fenders, hooks or bollards, walkways."),
        new("trestle", "Approach trestle", "Piled approach bridge or trestle carrying traffic and pipelines to the berth."),
        new("roro", "Ro-Ro ramp", "Linkspan or fixed ramp for roll-on roll-off vessels."),
        new("pontoon", "Floating pontoon", "Floating pontoon held by guide piles, with a gangway to the quay."),
        new("diaphragm-wall", "Diaphragm or secant pile wall", "Cast in place concrete wall panels, or secant or contiguous bored piles, anchored or propped."),
        new("dredging", "Dredging and reclamation", "Dredged basin or channel, side slopes, and reclaimed land behind the quay."),
        new("crane-rails", "Crane rails", "Ship-to-shore or rail-mounted crane rails, rail beams and fixings."),
        new("pavement", "Yard, pavement and drainage", "Quay apron and yard pavement, falls and drainage behind the quay."),
    ];

    public static StructureType? TypeOf(string key) => Types.FirstOrDefault(t => t.Key == key);

    // ---- Pages (topics) -------------------------------------------------------------------------------------------

    /// <summary>A page of the checklist: its key and title, and one line saying what it covers.</summary>
    public sealed record Topic(string Key, string Title, string About);

    public const string TopicTypes = "types";

    public static readonly Topic[] Topics =
    [
        new("basis", "Design basis and inputs", "The design basis, the data used, and what came back from the last submission."),
        new("levels", "Levels and coordination", "Levels agreed with the grading and marine teams, datums, and every interface."),
        new("loads", "Loads", "Every load the structure carries, and how they are combined."),
        new("geotech", "Geotechnical and foundations", "Soils, capacities, settlement and stability."),
        new(TopicTypes, "Structure checks", "Checks for each structure type this project has."),
        new("durability", "Durability and materials", "Exposure, concrete, steel, protection over the design life."),
        new("furniture", "Marine furniture and safety", "Fenders, bollards, ladders and safety on the quay."),
        new("calcs", "Calculations and reports", "The calculation package and its checking."),
        new("drawings", "Drawings", "The drawings against the model, the calculations and the comments."),
        new("bills", "Bills and costing", "Bills of quantities, their back-up, and the costing."),
        new("specs", "Specifications", "The specification against the drawings and the bills."),
    ];

    public static Topic TopicOf(string key) => Topics.FirstOrDefault(t => t.Key == key) ?? new Topic(key, key, "");

    // ---- Questions --------------------------------------------------------------------------------------------------

    /// <summary>
    /// A question: a stable id, its page, the short question, the details shown on demand, the structure types it is for
    /// (comma separated keys; empty: every project), the teams to agree it with, and the diagram that helps (a type key or "levels").
    /// </summary>
    public sealed record Question(string Id, string Topic, string Text, string Details = "", string Types = "", string Teams = "", string Diagram = "")
    {
        /// <summary>Added by someone on the project (kept in the model), not built in.</summary>
        public bool Added { get; init; }

        [JsonIgnore]
        public IReadOnlyList<string> TypeKeys => Split(Types);

        [JsonIgnore]
        public IReadOnlyList<string> TeamList => Split(Teams);

        public bool AppliesTo(IReadOnlyCollection<string> projectTypes) => TypeKeys.Count == 0 || TypeKeys.Any(projectTypes.Contains);
    }

    public static List<string> Split(string text) =>
        (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    /// <summary>
    /// The questions a project asks, page by page: the built-in ones for its structure types, then those added on the
    /// project (deleted ones left out). The structure page lists each type's questions in the order the types were picked.
    /// </summary>
    public static List<Question> QuestionsFor(Setup setup, IEnumerable<AddedQuestion> added, IReadOnlyList<Question>? template = null)
    {
        template ??= SubmissionChecklistBank.Questions;
        var types = setup.Types.Where(t => TypeOf(t) is not null).ToList();
        var mine = added.Where(a => !a.Deleted && a.Text.Trim().Length > 0)
            .Select(a => new Question(a.Id, TopicOf(a.Topic).Key, a.Text.Trim(), a.Details.Trim(), a.Types, a.Teams) { Added = true }).ToList();
        var list = new List<Question>();
        foreach (var topic in Topics)
        {
            var here = template.Where(q => q.Topic == topic.Key && q.AppliesTo(types)).ToList();
            if (topic.Key == TopicTypes)
                here = types.SelectMany(t => here.Where(q => TypeOfQuestion(q, types) == t)).ToList();
            list.AddRange(here.Where(q => !setup.Skipped.Contains(q.Id)));
            list.AddRange(mine.Where(q => q.Topic == topic.Key && q.AppliesTo(types)));
        }
        return list;
    }

    /// <summary>The structure page's questions by type, in the project's order (a question for several types shows under its first).</summary>
    public static string TypeOfQuestion(Question q, IReadOnlyList<string> projectTypes) =>
        q.TypeKeys.FirstOrDefault(projectTypes.Contains) ?? q.TypeKeys.FirstOrDefault() ?? "";

    // ---- What is shared through the model ---------------------------------------------------------------------------

    /// <summary>The project's setup: its structure types, who approves (Revit user names), and built-in questions it doesn't ask.</summary>
    public sealed class Setup
    {
        public List<string> Types { get; set; } = [];
        public List<string> Leads { get; set; } = [];
        /// <summary>Built-in questions this project never asks (removed by a group leader).</summary>
        public List<string> Skipped { get; set; } = [];
        public string By { get; set; } = "";
        public DateTime At { get; set; }

        [JsonIgnore]
        public bool Done => Types.Count > 0 && Leads.Count > 0;
    }

    /// <summary>A question someone added on the project. Deleted ones stay as a tombstone so the deletion travels too.</summary>
    public sealed class AddedQuestion
    {
        public string Id { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Text { get; set; } = "";
        public string Details { get; set; } = "";
        public string Types { get; set; } = "";
        public string Teams { get; set; } = "";
        public bool Deleted { get; set; }
        public string By { get; set; } = "";
        public DateTime At { get; set; }
    }

    /// <summary>An engineer's answer: Y, N, NA or "" (cleared), a comment, who and when (UTC).</summary>
    public sealed class Answer
    {
        public string Value { get; set; } = "";
        public string Note { get; set; } = "";
        public string By { get; set; } = "";
        public DateTime At { get; set; }
    }

    /// <summary>A group leader's review of an answer: approved, returned (with why) or "" (taken back).</summary>
    public sealed class Review
    {
        public string Verdict { get; set; } = "";
        public string Note { get; set; } = "";
        public string By { get; set; } = "";
        public DateTime At { get; set; }
    }

    /// <summary>A submission as one person holds it: its name and package, due date, answers, reviews and the BIM checklist score they saw.</summary>
    public sealed class SubmissionRecord
    {
        public string Name { get; set; } = "";
        public string Package { get; set; } = "";
        public DateTime? Due { get; set; }
        public bool Deleted { get; set; }
        public DateTime At { get; set; }
        /// <summary>0: the first issue; 1, 2…: resubmitted after comments (see <see cref="StartRevision"/>).</summary>
        public int Revision { get; set; }
        /// <summary>The key of the revision this one replaces ("" for the first issue).</summary>
        public string Of { get; set; } = "";
        /// <summary>Why it was resubmitted: the comments it answers.</summary>
        public string Reason { get; set; } = "";
        public Dictionary<string, Answer> Answers { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Review> Reviews { get; set; } = new(StringComparer.Ordinal);
        /// <summary>The Model checklist's score (answers Yes or N/A out of all) on the PC of whoever saved it, and when.</summary>
        public int? Bim { get; set; }
        public DateTime? BimAt { get; set; }
    }

    /// <summary>Everything one person wrote: kept in their own DataStorage element in the model, and on their PC.</summary>
    public sealed class UserRecord
    {
        public string User { get; set; } = "";
        public DateTime Saved { get; set; }
        public Setup? Setup { get; set; }
        public List<AddedQuestion> Added { get; set; } = [];
        public Dictionary<string, SubmissionRecord> Submissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The questions template this person's checklist uses, when it isn't the built-in one (the newest in the model wins).</summary>
        public SubmissionChecklistTemplate.Template? Template { get; set; }

        public SubmissionRecord For(string key, string name = "", string package = "")
        {
            if (!Submissions.TryGetValue(key, out var s)) Submissions[key] = s = new SubmissionRecord { Name = name, Package = package };
            return s;
        }
    }

    /// <summary>The key of a submission: its package and name, in any case and spacing.</summary>
    public static string KeyOf(string package, string name) =>
        Regex.Replace(((package ?? "").Trim() + " | " + (name ?? "").Trim()).ToLowerInvariant(), @"\s+", " ");

    /// <summary>
    /// The key of a submission as the schedule names it: "T3 Rev 1", "T3 (Rev B)" or "T3 resubmission" is revision 1
    /// (B: 2) of T3, kept apart from T3 itself.
    /// </summary>
    public static string KeyFor(string package, string name)
    {
        var (bare, revision) = SplitRevision(name);
        return RevisionKey(KeyOf(package, bare), revision);
    }

    public static string RevisionKey(string firstKey, int revision) => revision <= 0 ? firstKey : firstKey + " | rev " + revision.ToString(CultureInfo.InvariantCulture);

    /// <summary>A submission's name without its revision, and the revision (0: the first issue). Rev A is 1, Rev 2 is 2, "resubmission" is 1.</summary>
    public static (string Name, int Revision) SplitRevision(string? name)
    {
        var text = (name ?? "").Trim();
        var m = Regex.Match(text, @"^(?<n>.*?)[\s\-_,]*\(?\s*(?<![A-Za-z])(?:rev(?:ision)?\.?\s*(?<r>\d{1,3}|[A-Za-z])|re-?submission(?:\s*(?<s>\d{1,3}))?)\s*\)?$", RegexOptions.IgnoreCase);
        if (!m.Success || m.Groups["n"].Value.Trim().Length == 0) return (text, 0);
        var bare = m.Groups["n"].Value.Trim();
        if (m.Groups["r"].Success)
        {
            var r = m.Groups["r"].Value;
            return (bare, char.IsDigit(r[0]) ? int.Parse(r, CultureInfo.InvariantCulture) : char.ToUpperInvariant(r[0]) - 'A' + 1);
        }
        return (bare, m.Groups["s"].Success ? Math.Max(1, int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture)) : 1);
    }

    /// <summary>A submission as everyone sees it after the records are put together.</summary>
    public sealed class Submission
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Package { get; set; } = "";
        public DateTime? Due { get; set; }
        public int Revision { get; set; }
        public string Of { get; set; } = "";
        public string Reason { get; set; } = "";
        /// <summary>The key of the revision that replaced this one ("" while it is the latest): kept as the record, not changed any more.</summary>
        public string SupersededBy { get; set; } = "";
        public bool Superseded => SupersededBy.Length > 0;
        public Dictionary<string, Answer> Answers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Review> Reviews { get; } = new(StringComparer.Ordinal);
        public int? Bim { get; set; }
        public DateTime? BimAt { get; set; }

        /// <summary>The name with its revision, as the schedule would write it ("T3 Rev 1").</summary>
        public string FullName => Revision > 0 ? $"{Name.Trim()} Rev {Revision}".Trim() : Name.Trim();
        public string Title => Package.Trim().Length == 0 ? FullName : FullName.Length == 0 ? Package : Package.Trim() + " · " + FullName;
    }

    /// <summary>The project as everyone sees it.</summary>
    public sealed class Shared
    {
        public Setup Setup { get; set; } = new();
        public List<AddedQuestion> Added { get; } = [];
        public List<Submission> Submissions { get; } = [];
        /// <summary>Who has a record in the model, with when they last saved it.</summary>
        public List<(string User, DateTime Saved)> People { get; } = [];
        /// <summary>The newest questions template anyone's record carries; null: the built-in questions.</summary>
        public SubmissionChecklistTemplate.Template? Template { get; set; }

        public bool IsLead(string user) => Setup.Leads.Any(l => Same(l, user));
        public Submission? Find(string key) => Submissions.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Same(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts everyone's records together. The setup: the first one made, then each later one by a group leader of the
    /// setup before it (an engineer can't make themselves a group leader). Added questions and submissions: the newest
    /// version of each. Answers: the newest per question. Reviews: the newest per question by someone who is a group
    /// leader; a review older than the answer is kept but no longer counts (see <see cref="StateOf"/>).
    /// </summary>
    public static Shared Merge(IEnumerable<UserRecord> records)
    {
        var all = records.Where(r => r is not null).ToList();
        var shared = new Shared();
        foreach (var r in all.OrderBy(r => r.User, StringComparer.OrdinalIgnoreCase)) shared.People.Add((r.User, r.Saved));

        Setup? setup = null;
        foreach (var s in all.Select(r => r.Setup).Where(s => s is not null).OrderBy(s => s!.At))
            if (setup is null || setup.Leads.Count == 0 || setup.Leads.Any(l => Same(l, s!.By))) setup = s;
        shared.Setup = Copy(setup) ?? new Setup();
        shared.Template = SubmissionChecklistTemplate.Newest(all.Select(r => r.Template).ToArray());

        foreach (var g in all.SelectMany(r => r.Added).Where(a => a.Id.Length > 0).GroupBy(a => a.Id, StringComparer.Ordinal))
            shared.Added.Add(g.OrderBy(a => a.At).Last());

        foreach (var g in all.SelectMany(r => r.Submissions.Select(kv => (Key: kv.Key, Record: kv.Value))).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var newest = g.OrderBy(x => x.Record.At).Last().Record;
            if (newest.Deleted) continue;
            var sub = new Submission { Key = g.Key, Name = newest.Name, Package = newest.Package, Due = newest.Due, Revision = newest.Revision, Of = newest.Of, Reason = newest.Reason };
            foreach (var answer in g.SelectMany(x => x.Record.Answers).GroupBy(kv => kv.Key, StringComparer.Ordinal))
                sub.Answers[answer.Key] = answer.Select(kv => kv.Value).OrderBy(a => a.At).Last();
            foreach (var review in g.SelectMany(x => x.Record.Reviews).Where(kv => shared.IsLead(kv.Value.By)).GroupBy(kv => kv.Key, StringComparer.Ordinal))
                sub.Reviews[review.Key] = review.Select(kv => kv.Value).OrderBy(r => r.At).Last();
            var bim = g.Select(x => x.Record).Where(r => r.Bim is not null && r.BimAt is not null).OrderBy(r => r.BimAt).LastOrDefault();
            if (bim is not null) { sub.Bim = bim.Bim; sub.BimAt = bim.BimAt; }
            shared.Submissions.Add(sub);
        }
        foreach (var sub in shared.Submissions.Where(x => x.Of.Length > 0))
            if (shared.Find(sub.Of) is { } older && (older.SupersededBy.Length == 0 || (shared.Find(older.SupersededBy)?.Revision ?? 0) < sub.Revision))
                older.SupersededBy = sub.Key;
        shared.Submissions.Sort((a, b) => (a.Due ?? DateTime.MaxValue).CompareTo(b.Due ?? DateTime.MaxValue) is var c && c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return shared;
    }

    private static Setup? Copy(Setup? s) => s is null ? null : new Setup { Types = s.Types.ToList(), Leads = s.Leads.ToList(), Skipped = s.Skipped.ToList(), By = s.By, At = s.At };

    // ---- Where each question stands ---------------------------------------------------------------------------------

    public enum ItemState
    {
        /// <summary>Not answered yet.</summary>
        Open,
        /// <summary>Answered Yes, waiting for the group leader.</summary>
        Answered,
        /// <summary>Answered N/A, waiting for the group leader (it stays in view until then).</summary>
        NaWaiting,
        /// <summary>Answered No: something to do before it can be approved.</summary>
        No,
        /// <summary>Returned by the group leader, with why: answer it again.</summary>
        Returned,
        /// <summary>Yes, approved by the group leader.</summary>
        Approved,
        /// <summary>N/A approved by the group leader: hidden from the pages and left out of the scores.</summary>
        Hidden,
    }

    public static string Normal(string? value)
    {
        var v = (value ?? "").Trim().ToUpperInvariant().Replace("/", "").Replace(" ", "");
        return v switch
        {
            "Y" or "YES" or "OK" or "DONE" => Yes,
            "N" or "NO" => No,
            "NA" or "N.A." or "NOTAPPLICABLE" => NotApplicable,
            _ => "",
        };
    }

    /// <summary>The review that still counts: by a group leader, made after the answer it reviews (a changed answer needs a new look).</summary>
    public static Review? LiveReview(Submission s, string id)
    {
        if (!s.Reviews.TryGetValue(id, out var r) || r.Verdict.Length == 0) return null;
        var a = s.Answers.TryGetValue(id, out var found) ? found : null;
        return a is not null && a.At > r.At ? null : r;
    }

    public static ItemState StateOf(Submission s, string id)
    {
        var a = s.Answers.TryGetValue(id, out var found) ? found : null;
        var value = Normal(a?.Value);
        var review = LiveReview(s, id);
        if (review?.Verdict == Returned) return ItemState.Returned;
        return value switch
        {
            Yes => review?.Verdict == Approved ? ItemState.Approved : ItemState.Answered,
            NotApplicable => review?.Verdict == Approved ? ItemState.Hidden : ItemState.NaWaiting,
            No => ItemState.No,
            _ => ItemState.Open,
        };
    }

    /// <summary>What a group leader may do with an answer: approve Yes or N/A, return anything answered.</summary>
    public static bool CanApprove(ItemState state) => state is ItemState.Answered or ItemState.NaWaiting;

    public static bool CanReturn(ItemState state) => state is ItemState.Answered or ItemState.NaWaiting or ItemState.No or ItemState.Approved;

    /// <summary>
    /// The counts of a submission: Total leaves out the hidden (approved N/A) questions. Checked: answered Yes or N/A
    /// (approved or not). Approved: approved Yes.
    /// </summary>
    public sealed record Progress(int Total, int Open, int Answered, int NaWaiting, int No, int Returned, int Approved, int Hidden)
    {
        public int Checked => Answered + NaWaiting + Approved;
        public int CheckedPercent => Total == 0 ? 0 : Round(100.0 * Checked / Total);
        public int ApprovedPercent => Total == 0 ? 0 : Round(100.0 * Approved / Total);
        public int WaitingForLead => Answered + NaWaiting;
        public bool Ready => Total > 0 && Approved == Total;
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    public static Progress Count(IEnumerable<Question> questions, Submission s)
    {
        int total = 0, open = 0, answered = 0, naWaiting = 0, no = 0, returned = 0, approved = 0, hidden = 0;
        foreach (var q in questions)
        {
            var st = StateOf(s, q.Id);
            if (st == ItemState.Hidden) { hidden++; continue; }
            total++;
            switch (st)
            {
                case ItemState.Open: open++; break;
                case ItemState.Answered: answered++; break;
                case ItemState.NaWaiting: naWaiting++; break;
                case ItemState.No: no++; break;
                case ItemState.Returned: returned++; break;
                case ItemState.Approved: approved++; break;
            }
        }
        return new Progress(total, open, answered, naWaiting, no, returned, approved, hidden);
    }

    /// <summary>The submission's checklist in a few words, for the overview and Submissions status.</summary>
    public static string StatusText(Progress p) =>
        p.Total == 0 ? "No questions"
        : p.Ready ? "Approved"
        : p.Returned > 0 ? $"{p.Returned} returned by the group leader"
        : p.Checked == 0 && p.No == 0 ? "Not started"
        : p.Open + p.No > 0 ? $"Checking: {p.Open + p.No} left"
        : $"Waiting for the group leader ({p.WaitingForLead})";

    // ---- Changes (each person writes only their own record) ---------------------------------------------------------

    public static void SetAnswer(UserRecord mine, Submission s, string id, string value, string? note, DateTime now)
    {
        var rec = mine.For(s.Key, s.Name, s.Package);
        var old = s.Answers.TryGetValue(id, out var a) ? a : null;
        var answer = new Answer { Value = Normal(value), Note = note ?? old?.Note ?? "", By = mine.User, At = now };
        rec.Answers[id] = answer;
        rec.At = Max(rec.At, now);
        s.Answers[id] = answer;
        mine.Saved = now;
    }

    public static void SetNote(UserRecord mine, Submission s, string id, string note, DateTime now)
    {
        var old = s.Answers.TryGetValue(id, out var a) ? a : null;
        if ((old?.Note ?? "") == note) return;
        // A comment alone doesn't change the answer, so it keeps its time: an approval made after it still stands.
        var answer = new Answer { Value = old?.Value ?? "", Note = note, By = mine.User, At = old?.At ?? now };
        var rec = mine.For(s.Key, s.Name, s.Package);
        rec.Answers[id] = answer;
        rec.At = Max(rec.At, now);
        s.Answers[id] = answer;
        mine.Saved = now;
    }

    public static void SetReview(UserRecord mine, Submission s, string id, string verdict, string note, DateTime now)
    {
        var rec = mine.For(s.Key, s.Name, s.Package);
        var review = new Review { Verdict = verdict, Note = note, By = mine.User, At = now };
        rec.Reviews[id] = review;
        rec.At = Max(rec.At, now);
        s.Reviews[id] = review;
        mine.Saved = now;
    }

    /// <summary>Starts (or renames) a submission in the user's record. Returns its key.</summary>
    public static string AddSubmission(UserRecord mine, string package, string name, DateTime? due, DateTime now)
    {
        var key = KeyOf(package, name);
        var rec = mine.For(key, name.Trim(), package.Trim());
        rec.Name = name.Trim();
        rec.Package = package.Trim();
        if (due is not null) rec.Due = due;
        rec.Deleted = false;
        rec.At = now;
        mine.Saved = now;
        return key;
    }

    /// <summary>
    /// Starts the next revision of a submission resubmitted after comments: a checklist of its own that starts from the
    /// last revision's answers and comments, with no approvals (the group leader approves each answer again, as the
    /// comments may have changed it). The last revision stays as it was, as the record of what was submitted. Returns
    /// the new revision's key (the existing one when it was started already).
    /// </summary>
    public static string StartRevision(UserRecord mine, Shared shared, Submission last, string reason, DateTime? due, DateTime now, int? revision = null)
    {
        var number = Math.Max(revision ?? 0, last.Revision + 1);
        var first = RevisionKey(KeyOf(last.Package, last.Name), 0);
        var key = RevisionKey(first, number);
        if (shared.Find(key) is not null) return key;
        var rec = mine.For(key, last.Name.Trim(), last.Package.Trim());
        rec.Name = last.Name.Trim();
        rec.Package = last.Package.Trim();
        rec.Revision = number;
        rec.Of = last.Key;
        rec.Reason = (reason ?? "").Trim();
        rec.Due = due ?? last.Due;
        rec.Deleted = false;
        rec.At = now;
        foreach (var kv in last.Answers)
            rec.Answers[kv.Key] = new Answer { Value = kv.Value.Value, Note = kv.Value.Note, By = kv.Value.By, At = now };
        mine.Saved = now;
        return key;
    }

    /// <summary>The latest revision of a submission named as the schedule names it, before <paramref name="revision"/> (null when none was started).</summary>
    public static Submission? LastBefore(Shared shared, string package, string name, int revision) =>
        shared.Submissions.Where(x => Same(x.Package, package) && Same(x.Name, name) && x.Revision < revision).OrderBy(x => x.Revision).LastOrDefault();

    /// <summary>
    /// Starts a submission named as the schedule names it: a revision ("T3 Rev 1") starts from the latest earlier one
    /// that has a checklist (its answers carried, approvals cleared); anything else starts empty. Returns its key.
    /// </summary>
    public static string StartNamed(UserRecord mine, Shared shared, string package, string name, DateTime? due, DateTime now, string reason = "")
    {
        var (bare, revision) = SplitRevision(name);
        if (revision > 0 && LastBefore(shared, package, bare, revision) is { } last)
            return StartRevision(mine, shared, last, reason.Length > 0 ? reason : "Resubmitted after comments", due, now, revision);
        if (revision == 0) return AddSubmission(mine, package, bare, due, now);
        var key = KeyFor(package, name);
        var rec = mine.For(key, bare, package.Trim());
        rec.Name = bare;
        rec.Package = package.Trim();
        rec.Revision = revision;
        if (due is not null) rec.Due = due;
        rec.Deleted = false;
        rec.At = now;
        mine.Saved = now;
        return key;
    }

    public static void SetBim(UserRecord mine, Submission s, int? percent, DateTime now)
    {
        if (percent is null || s.Bim == percent) return;
        var rec = mine.For(s.Key, s.Name, s.Package);
        rec.Bim = percent;
        rec.BimAt = now;
        rec.At = Max(rec.At, now);
        s.Bim = percent;
        s.BimAt = now;
        mine.Saved = now;
    }

    public static string NewQuestionId(string user, DateTime now) =>
        "add." + Regex.Replace((user ?? "").ToLowerInvariant(), "[^a-z0-9]", "") + "." + now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    // ---- The summary Submissions status shows -----------------------------------------------------------------------

    /// <summary>One submission's checklist in numbers, kept on the PC for Submissions status (which doesn't read the model on its own).</summary>
    public sealed class Summary
    {
        public string Model { get; set; } = "";
        public string Package { get; set; } = "";
        public string Name { get; set; } = "";
        public int Checked { get; set; }
        public int Approved { get; set; }
        public int Total { get; set; }
        public int No { get; set; }
        public int Returned { get; set; }
        public int? Bim { get; set; }
        public string Status { get; set; } = "";
        public DateTime At { get; set; }
    }

    public static Summary SummaryOf(string model, Submission s, IEnumerable<Question> questions, DateTime now)
    {
        var p = Count(questions, s);
        return new Summary
        {
            Model = model, Package = s.Package, Name = s.FullName, Checked = p.CheckedPercent, Approved = p.ApprovedPercent, Total = p.Total,
            No = p.No, Returned = p.Returned, Bim = s.Bim, Status = StatusText(p), At = now,
        };
    }

    /// <summary>The summary for a package's submission: by its key, else the package's newest one (null when none).</summary>
    public static Summary? Find(IReadOnlyDictionary<string, Summary> summaries, string package, string submission)
    {
        if (summaries.TryGetValue(KeyFor(package, submission), out var exact)) return exact;
        return summaries.Values.Where(s => Same(s.Package, package)).OrderBy(s => s.At).LastOrDefault();
    }

    // ---- On this PC ---------------------------------------------------------------------------------------------------

    /// <summary>The tests point this at a temporary folder (per thread).</summary>
    [ThreadStatic] internal static string? FolderForTests;

    /// <summary>%APPDATA%\SketchProMax\submission checklists: a copy of the user's own record per model, and the summaries.</summary>
    public static string Folder => FolderForTests ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "submission checklists");

    private static string Clean(string name) => Regex.Replace((name ?? "").Trim(), @"[\\/:*?""<>|]+", "_") is { Length: > 0 } c ? c : "model";

    /// <summary>The user's own record for a model, kept on the PC too: nothing is lost when the model can't be written.</summary>
    public static string RecordPath(string model, string user) => System.IO.Path.Combine(Folder, Clean(model), Clean(user) + ".json");

    public static UserRecord? LoadOwn(string model, string user)
    {
        try
        {
            var path = RecordPath(model, user);
            return System.IO.File.Exists(path) ? ReadRecord(System.IO.File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return null; }
    }

    public static void SaveOwn(string model, UserRecord record)
    {
        try { SafeFile.Write(RecordPath(model, record.User), ToJson(record)); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { /* the model keeps it too */ }
    }

    private static string SummaryPath => System.IO.Path.Combine(Folder, "summary.json");

    /// <summary>Every submission's checklist summary this PC has seen, by <see cref="KeyOf"/>.</summary>
    public static Dictionary<string, Summary> LoadSummaries()
    {
        try
        {
            if (System.IO.File.Exists(SummaryPath) && FromJson<Dictionary<string, Summary>>(System.IO.File.ReadAllText(SummaryPath)) is { } all)
                return new Dictionary<string, Summary>(all, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        return new Dictionary<string, Summary>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records the model's submissions (replacing what this PC had for them).</summary>
    public static void SaveSummaries(IEnumerable<Summary> summaries)
    {
        var all = LoadSummaries();
        foreach (var s in summaries) all[KeyFor(s.Package, s.Name)] = s;
        try { SafeFile.Write(SummaryPath, ToJson(all)); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }

    // ---- JSON ---------------------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? FromJson<T>(string text) where T : class
    {
        try { return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, Json); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>A record read back: dictionaries made case-insensitive again and nulls made empty, so old or hand-edited records still work.</summary>
    public static UserRecord? ReadRecord(string text)
    {
        var r = FromJson<UserRecord>(text);
        if (r is null) return null;
        r.Added ??= [];
        r.Submissions = new Dictionary<string, SubmissionRecord>(r.Submissions ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var s in r.Submissions.Values)
        {
            s.Answers = new Dictionary<string, Answer>(s.Answers ?? new(), StringComparer.Ordinal);
            s.Reviews = new Dictionary<string, Review>(s.Reviews ?? new(), StringComparer.Ordinal);
            s.Of ??= "";
            s.Reason ??= "";
        }
        if (r.Setup is { } setup) { setup.Types ??= []; setup.Leads ??= []; setup.Skipped ??= []; }
        if (r.Template is not null) r.Template = SubmissionChecklistTemplate.Read(ToJson(r.Template));
        return r;
    }
}
