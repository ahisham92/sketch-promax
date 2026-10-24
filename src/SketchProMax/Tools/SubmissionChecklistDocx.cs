using System.Globalization;
using System.IO;
using System.Text;
using static SketchProMax.Tools.ChecklistDocx;
using W = SketchProMax.Tools.ChecklistDocx;
using static SketchProMax.Tools.SubmissionChecklistMath;

namespace SketchProMax.Tools;

/// <summary>
/// The technical submission checklist as a Word file, A4 portrait, ready to print or to save as PDF: a cover with the
/// submission, its status banner and three scores (checked, approved, BIM checklist) drawn as bars, the structures with
/// their section sketches, a summary table per page; then each page's questions with the answer in colour, who answered
/// and when, the group leader's review and the comments; then the questions the group leader agreed are not applicable;
/// and the sign-off. Plain WordprocessingML (ChecklistDocx's pieces); no Office, Revit or WPF.
/// </summary>
internal static class SubmissionChecklistDocx
{
    /// <summary>About the file. <paramref name="Diagrams"/>: PNG pictures of the section sketches by key (SubmissionChecklistDiagrams), when the window could draw them.</summary>
    public sealed record Info(string Model, string Version, DateTime When, IReadOnlyDictionary<string, Image> Diagrams);

    private const int PageW = 11906, PageH = 16838, Margin = 850, Usable = PageW - 2 * Margin; // A4 portrait, twips
    private const string Accent = "1D4ED8", Navy = "0B1E4F", Muted = "475569", Soft = "EEF2FF", LabelFill = "F1F5F9", Track = "E2E8F0";
    private const string OkText = "15803D", OkFill = "E8F5EC", BadText = "B91C1C", BadFill = "FDECEC", WarnText = "A16207", WarnFill = "FEF6E0", NaText = "475569", NaFill = "EEF1F5";

    public static void Write(string path, Shared shared, Submission s, IReadOnlyList<Question> questions, Info info)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        using (var file = File.Create(temp)) Write(file, shared, s, questions, info);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    public static void Write(Stream output, Shared shared, Submission s, IReadOnlyList<Question> questions, Info info)
    {
        var p = Count(questions, s);
        var media = new List<(string Name, string RelId, Image Image)>();
        var body = new StringBuilder();
        var types = shared.Setup.Types.Where(t => TypeOf(t) is not null).ToList();

        // ---- Cover: title, submission, status banner
        body.Append(Para(W.Run("TECHNICAL SUBMISSION CHECKLIST", bold: true, size: 18, color: Accent), after: 40));
        body.Append(Para(W.Run(s.Title, bold: true, size: 40, color: Navy), after: 40));
        var sub = $"Model {Or(info.Model, "not saved")}" + (s.Due is { } due ? $" · due {due.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture)}" : "") +
                  $" · made {info.When.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)} with Sketch ProMax {info.Version}";
        body.Append(Para(W.Run(sub, size: 17, color: Muted), after: 200));
        var (bannerText, bannerColor, bannerFill) = p.Ready
            ? ("APPROVED · READY FOR SUBMISSION", OkText, OkFill)
            : p.No + p.Returned > 0 ? ("NOT READY · " + StatusText(p).ToUpperInvariant(), BadText, BadFill)
            : ("IN REVIEW · " + StatusText(p).ToUpperInvariant(), WarnText, WarnFill);
        var counts = $"{p.Total} questions: {p.Approved} approved, {p.WaitingForLead} waiting for the group leader, {p.No} No, {p.Returned} returned, {p.Open} not answered" +
                     (p.Hidden > 0 ? $"; {p.Hidden} not applicable (agreed)." : ".");
        body.Append(Table([Usable], Row(Cell(Usable, Para(W.Run(bannerText, bold: true, size: 22, color: bannerColor), after: 30) + Para(W.Run(counts, size: 17, color: bannerColor), after: 0),
            fill: bannerFill, border: bannerColor))));
        body.Append(Para("", after: 160));

        // ---- Scores as bars
        body.Append(Heading("Scores"));
        body.Append(Table([2600, Usable - 2600 - 1100, 1100],
            ScoreRow("Checked", "answered Yes or N/A", p.CheckedPercent, Accent) +
            ScoreRow("Approved", "by the group leader", p.ApprovedPercent, OkText) +
            (s.Bim is { } bim ? ScoreRow("BIM checklist", "the Model checklist" + (s.BimAt is { } at ? ", " + at.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture) : ""), bim, "7C3AED") : "")));
        if (s.Bim is null) body.Append(Para(W.Run("BIM checklist: not filled in for this model yet (Model checklist button).", size: 16, color: Muted, italic: true), after: 0));

        // ---- Summary per page
        body.Append(Heading("Summary by page"));
        int[] sc = [Usable - 5 * 1050, 1050, 1050, 1050, 1050, 1050];
        var sum = new StringBuilder(HeaderRow(sc, "Page", "Questions", "Approved", "Waiting", "No / returned", "Not answered"));
        foreach (var (topic, items) in Pages(questions))
        {
            var tp = Count(items, s);
            sum.Append(Row(
                Cell(sc[0], Para(W.Run(topic.Title, bold: true, size: 17), after: 0)) +
                Num(sc[1], tp.Total, Muted) + Num(sc[2], tp.Approved, OkText) + Num(sc[3], tp.WaitingForLead, WarnText) +
                Num(sc[4], tp.No + tp.Returned, BadText) + Num(sc[5], tp.Open, Muted)));
        }
        body.Append(Table(sc, sum.ToString()));

        body.Append(PageBreak());
        // ---- The structures, with their sketches two to a row
        body.Append(Heading("Structures in this project"));
        if (types.Count == 0) body.Append(Para(W.Run("None picked: only the general questions are asked.", size: 18, color: Muted), after: 80));
        var half = Usable / 2;
        var cells = new List<string>();
        foreach (var t in types)
        {
            var type = TypeOf(t)!;
            var content = Para(W.Run(type.Name, bold: true, size: 19, color: Navy), after: 20) + Para(W.Run(type.About, size: 15, color: Muted), after: 40, keep: true);
            if (info.Diagrams.TryGetValue(t, out var img) && ImageSize(img.Bytes) is { } size)
            {
                var n = media.Count + 1;
                media.Add(($"diagram{n}.png", "rIdImg" + n, img));
                content += Para(Drawing("rIdImg" + n, n, size.Width, size.Height, maxWidthMm: 78, maxHeightMm: 44, name: type.Name + " section"), after: 0);
            }
            cells.Add(Cell(half, content));
        }
        if (cells.Count % 2 == 1) cells.Add(Cell(half, ""));
        if (cells.Count > 0)
            body.Append(Table([half, half], string.Concat(Enumerable.Range(0, cells.Count / 2).Select(i => Row(cells[2 * i] + cells[2 * i + 1])))));

        // ---- Each page's questions
        int[] cols = [500, Usable - 500 - 900 - 1900 - 2100, 900, 1900, 2100];
        var number = 0;
        var first = true;
        foreach (var (topic, items) in Pages(questions))
        {
            var shown = items.Where(q => StateOf(s, q.Id) != ItemState.Hidden).ToList();
            if (shown.Count == 0) continue;
            body.Append(first ? PageBreak() : "");
            first = false;
            body.Append(Heading(topic.Title));
            body.Append(Para(W.Run(topic.About, size: 17, color: Muted, italic: true), after: 100));
            var t = new StringBuilder(HeaderRow(cols, "#", "Question", "Answer", "Answered", "Group leader"));
            string? group = null;
            foreach (var q in shown)
            {
                if (topic.Key == TopicTypes && TypeOfQuestion(q, types) is var g && g != group)
                {
                    group = g;
                    t.Append(Row(Span(Cell(Usable, Para(W.Run(TypeOf(g)?.Name ?? "Other", bold: true, size: 18, color: Navy), after: 0), fill: Soft), 5)));
                }
                number++;
                t.Append(QuestionRow(cols, number, q, s));
            }
            body.Append(Table(cols, t.ToString()));
        }

        // ---- Not applicable, agreed
        var hidden = questions.Where(q => StateOf(s, q.Id) == ItemState.Hidden).ToList();
        if (hidden.Count > 0)
        {
            body.Append(Heading("Not applicable (agreed by the group leader)"));
            int[] hc = [Usable - 2400 - 2400, 2400, 2400];
            var h = new StringBuilder(HeaderRow(hc, "Question", "Why not applicable", "Agreed by"));
            foreach (var q in hidden)
            {
                var a = s.Answers.TryGetValue(q.Id, out var x) ? x : new Answer();
                var r = LiveReview(s, q.Id);
                h.Append(Row(Cell(hc[0], Para(W.Run(q.Text, size: 16), after: 0)) + Cell(hc[1], Para(W.Run(a.Note, size: 16, color: Muted), after: 0)) +
                             Cell(hc[2], Para(W.Run(r is null ? "" : $"{r.By}, {Date(r.At)}", size: 16, color: Muted), after: 0))));
            }
            body.Append(Table(hc, h.ToString()));
        }

        // ---- Sign-off
        body.Append(Heading("Sign-off"));
        var engineers = s.Answers.Values.Where(a => a.Value.Length > 0).GroupBy(a => a.By, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        var leads = s.Reviews.Values.Where(r => r.Verdict == Approved).Select(r => r.By).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (leads.Count == 0) leads = shared.Setup.Leads.ToList();
        int[] gc = [2600, 3200, Usable - 2600 - 3200 - 1800, 1800];
        var sign = new StringBuilder(HeaderRow(gc, "Role", "Name", "Signature", "Date"));
        var lastAnswer = s.Answers.Values.Where(a => a.Value.Length > 0).Select(a => a.At).DefaultIfEmpty().Max();
        var lastReview = s.Reviews.Values.Where(r => r.Verdict == Approved).Select(r => r.At).DefaultIfEmpty().Max();
        sign.Append(Row(Cell(gc[0], Para(W.Run("Engineer", bold: true, size: 18), after: 0, keep: true)) + Cell(gc[1], Para(W.Run(string.Join(", ", engineers), size: 18), after: 0, keep: true)) +
                        Cell(gc[2], "") + Cell(gc[3], Para(W.Run(lastAnswer == default ? "" : Date(lastAnswer), size: 18), after: 0, keep: true)), height: 900));
        sign.Append(Row(Cell(gc[0], Para(W.Run("Group leader", bold: true, size: 18), after: 0, keep: true)) + Cell(gc[1], Para(W.Run(string.Join(", ", leads), size: 18), after: 0, keep: true)) +
                        Cell(gc[2], "") + Cell(gc[3], Para(W.Run(p.Ready && lastReview != default ? Date(lastReview) : "", size: 18), after: 0, keep: true)), height: 900));
        body.Append(Table(gc, sign.ToString()));
        body.Append(Para(W.Run(p.Ready
            ? "Every question is answered and approved by the group leader. The BIM part is the Model checklist, scored above."
            : "Not every question is approved yet: this copy is for review, not for submission.", size: 16, color: Muted, italic: true), after: 0));

        WritePackage(output, Document(body.ToString()), Footer($"Technical submission checklist · {s.Title} · {Or(info.Model, "model not saved")}"), media, "Technical submission checklist");
    }

    /// <summary>The questions grouped by page, in the checklist's page order (pages with no question left out).</summary>
    public static List<(Topic Topic, List<Question> Items)> Pages(IReadOnlyList<Question> questions) =>
        Topics.Select(t => (t, questions.Where(q => q.Topic == t.Key).ToList())).Where(x => x.Item2.Count > 0).ToList();

    private static string QuestionRow(int[] cols, int number, Question q, Submission s)
    {
        var a = s.Answers.TryGetValue(q.Id, out var found) ? found : new Answer();
        var st = StateOf(s, q.Id);
        var review = s.Reviews.TryGetValue(q.Id, out var r) ? r : null;
        var live = LiveReview(s, q.Id);
        var text = Para(W.Run(q.Text, size: 17, bold: false), after: 0);
        if (q.TeamList.Count > 0) text += Para(W.Run("Agree with: " + string.Join(", ", q.TeamList), size: 14, color: Accent), after: 0);
        if (a.Note.Trim().Length > 0) text += Para(W.Run("Comment: " + a.Note.Trim(), size: 15, color: Muted, italic: true), after: 0);
        if (q.Added) text += Para(W.Run("Added on the project", size: 13, color: Muted), after: 0);
        var value = Normal(a.Value);
        var (label, color, fill) = value switch
        {
            Yes => ("YES", OkText, OkFill),
            No => ("NO", BadText, BadFill),
            NotApplicable => ("N/A", NaText, NaFill),
            _ => ("–", BadText, (string?)null),
        };
        var answered = value.Length > 0 ? $"{a.By}\n{Date(a.At)}" : "";
        string gl;
        string? glFill = null, glColor = Muted;
        switch (st)
        {
            case ItemState.Approved: gl = $"Approved\n{live!.By}, {Date(live.At)}"; glColor = OkText; glFill = OkFill; break;
            case ItemState.Returned: gl = $"Returned: {live!.Note}\n{live.By}, {Date(live.At)}"; glColor = BadText; glFill = BadFill; break;
            default:
                gl = value.Length == 0 ? "" : review is { Verdict.Length: > 0 } old && live is null ? "Answer changed after review: look again" : "Waiting";
                if (gl.Length > 0) { glColor = WarnText; glFill = WarnFill; }
                break;
        }
        return Row(
            Cell(cols[0], Para(W.Run(number.ToString(CultureInfo.InvariantCulture), size: 16, color: Muted), after: 0)) +
            Cell(cols[1], text) +
            Cell(cols[2], Para(W.Run(label, bold: true, size: 17, color: color), after: 0, center: true), fill: fill) +
            Cell(cols[3], Para(W.Run(answered, size: 15, color: Muted), after: 0)) +
            Cell(cols[4], Para(W.Run(gl, size: 15, color: glColor, bold: st == ItemState.Approved), after: 0), fill: glFill));
    }

    private static string ScoreRow(string label, string sub, int percent, string color)
    {
        // The bar: a table of 20 cells, the first percent/5 filled.
        var filled = (int)Math.Round(Math.Max(0, Math.Min(100, percent)) / 5.0, MidpointRounding.AwayFromZero);
        var w = (Usable - 2600 - 1100 - 200) / 20;
        var bar = Table(Enumerable.Repeat(w, 20).ToArray(), Row(string.Concat(Enumerable.Range(0, 20).Select(i => Cell(w, Para("", after: 0), fill: i < filled ? color : Track))), height: 260));
        return Row(
            Cell(2600, Para(W.Run(label, bold: true, size: 20, color: Navy), after: 0) + Para(W.Run(sub, size: 14, color: Muted), after: 0), fill: LabelFill) +
            Cell(Usable - 2600 - 1100, bar + Para("", after: 0)) +
            Cell(1100, Para(W.Run(percent.ToString(CultureInfo.InvariantCulture) + "%", bold: true, size: 28, color: color), after: 0, center: true)));
    }

    private static string HeaderRow(int[] widths, params string[] heads) =>
        Row(string.Concat(heads.Select((h, i) => Cell(widths[i], Para(W.Run(h, bold: true, size: 16, color: "FFFFFF"), after: 0), fill: Accent))), header: true);

    private static string Num(int width, int value, string color) =>
        Cell(width, Para(W.Run(value == 0 ? "–" : value.ToString(CultureInfo.InvariantCulture), bold: value > 0, size: 17, color: value == 0 ? Muted : color), after: 0, center: true));

    private static string Date(DateTime at) => (at.Kind == DateTimeKind.Utc ? at.ToLocalTime() : at).ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>A cell made to span <paramref name="columns"/> grid columns (gridSpan goes right after the width, as Word expects).</summary>
    private static string Span(string cell, int columns)
    {
        var at = cell.IndexOf("/>", cell.IndexOf("<w:tcW", StringComparison.Ordinal), StringComparison.Ordinal) + 2;
        return cell.Insert(at, $"<w:gridSpan w:val=\"{columns}\"/>");
    }

    private static string PageBreak() => "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>";

    private static string Document(string body) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {Namespaces}><w:body>{body}" +
        $"<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rIdFooter\"/><w:pgSz w:w=\"{PageW}\" w:h=\"{PageH}\"/>" +
        $"<w:pgMar w:top=\"{Margin}\" w:right=\"{Margin}\" w:bottom=\"{Margin}\" w:left=\"{Margin}\" w:header=\"400\" w:footer=\"400\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";

    private static string Footer(string text)
    {
        static string Field(string code) =>
            $"<w:fldSimple w:instr=\" {code} \"><w:r><w:rPr><w:sz w:val=\"15\"/><w:color w:val=\"{Muted}\"/></w:rPr><w:t>1</w:t></w:r></w:fldSimple>";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:ftr {Namespaces}><w:p><w:pPr><w:pBdr><w:top w:val=\"single\" w:sz=\"4\" w:space=\"4\" w:color=\"{Track}\"/></w:pBdr>" +
               $"<w:tabs><w:tab w:val=\"right\" w:pos=\"{Usable}\"/></w:tabs></w:pPr>" +
               W.Run(text, size: 15, color: Muted) + "<w:r><w:rPr><w:sz w:val=\"15\"/></w:rPr><w:tab/></w:r>" + W.Run("Page ", size: 15, color: Muted) + Field("PAGE") +
               W.Run(" of ", size: 15, color: Muted) + Field("NUMPAGES") + "</w:p></w:ftr>";
    }
}
