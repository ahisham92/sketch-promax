using System.Globalization;
using System.IO;
using System.Text;
using static SketchProMax.Tools.SubmissionChecklistMath;

namespace SketchProMax.Tools;

/// <summary>
/// All the submission checklist questions as a document for someone to review and comment on (not a filled checklist):
/// the short version lists each question with the structures it applies to, who to agree it with and an empty
/// Comments column; the long version adds each question's details and the section sketches. Written as Markdown and
/// turned into Word and PDF by OfficeDocx and OfficePdf, so no Office is needed. Numbers run through the whole list and
/// are the same in both versions, so a comment on "question 42" finds the same question in either. No Revit or WPF.
/// </summary>
internal static class SubmissionChecklistReview
{
    // A writing line (escaped so it isn't read as emphasis), so the Comments column keeps room for a comment.
    private static readonly string Blank = string.Concat(Enumerable.Repeat("\\_", 22));

    /// <summary>
    /// The Markdown. <paramref name="sketchPath"/> gives a PNG on disk for a sketch key (SubmissionChecklistDiagrams),
    /// or null to leave it out; only the long version asks for sketches.
    /// </summary>
    public static string Markdown(IReadOnlyList<Question> questions, bool details, Func<string, string?> sketchPath, string made)
    {
        var md = new StringBuilder();
        var pages = SubmissionChecklistDocx.Pages(questions);
        md.AppendLine($"{questions.Count} questions on {pages.Count} pages. {made}");
        md.AppendLine();
        md.AppendLine(details
            ? "Each question with what to look at (the details), the structures it applies to, the team it must be agreed with, and the section sketches. Write your comments in the last column, by question number."
            : "Each question with the structures it applies to and the team it must be agreed with. Write your comments in the last column, by question number. The long version has the details and sketches.");
        md.AppendLine();
        md.AppendLine("| Page | Questions |");
        md.AppendLine("|---|---:|");
        foreach (var (topic, items) in pages) md.AppendLine($"| {Cell(topic.Title)} | {items.Count} |");
        md.AppendLine();

        var shown = new HashSet<string>(StringComparer.Ordinal);
        var number = 0;
        foreach (var (topic, items) in pages)
        {
            md.AppendLine("[page break]");
            md.AppendLine();
            md.AppendLine("## " + topic.Title);
            md.AppendLine();
            md.AppendLine("*" + topic.About + "*");
            md.AppendLine();
            if (topic.Key == TopicTypes)
            {
                // One group per structure type, in the type list's order; a question for several types sits under its first.
                foreach (var type in Types)
                {
                    var group = items.Where(q => q.TypeKeys.FirstOrDefault() == type.Key).ToList();
                    if (group.Count == 0) continue;
                    md.AppendLine("### " + type.Name);
                    md.AppendLine();
                    if (details)
                    {
                        md.AppendLine("*" + type.About + "*");
                        md.AppendLine();
                        Sketches(md, [type.Key, .. group.Select(q => q.Diagram)], sketchPath, shown);
                    }
                    Table(md, group, details, ref number);
                }
            }
            else
            {
                if (details) Sketches(md, items.Select(q => q.Diagram), sketchPath, shown);
                Table(md, items, details, ref number);
            }
        }
        return md.ToString();
    }

    /// <summary>Writes the Word file and the PDF next to each other; returns their paths.</summary>
    public static (string Docx, string Pdf) Write(string folder, string name, IReadOnlyList<Question> questions, bool details, Func<string, string?> sketchPath, string made)
    {
        Directory.CreateDirectory(folder);
        var title = "Submission checklist questions";
        var subtitle = details ? "Long version: with details and sketches, for review and comments" : "Short version: questions only, for review and comments";
        var markdown = Markdown(questions, details, sketchPath, made);
        var docx = Path.Combine(folder, name + ".docx");
        var pdf = Path.Combine(folder, name + ".pdf");
        OfficeDocx.Write(docx, title, subtitle, markdown, landscape: details);
        OfficePdf.Write(pdf, title, subtitle, markdown, landscape: details);
        return (docx, pdf);
    }

    /// <summary>The structure names a question applies to ("All structures" when it names none).</summary>
    public static string AppliesTo(Question q)
    {
        var missing = Types.Where(t => !q.TypeKeys.Contains(t.Key)).Select(t => t.Name).ToList();
        if (q.TypeKeys.Count == 0 || missing.Count == 0) return "All structures";
        // Most of the list: say what is left out, which is shorter to read.
        if (missing.Count <= 4 && q.TypeKeys.Count > missing.Count) return "All except " + string.Join(", ", missing);
        return string.Join(", ", q.TypeKeys.Select(k => TypeOf(k)?.Name ?? k));
    }

    private static void Table(StringBuilder md, List<Question> items, bool details, ref int number)
    {
        md.AppendLine(details ? "| # | Question | Details | Applies to | Agree with | Comments |" : "| # | Question | Applies to | Agree with | Comments |");
        md.AppendLine(details ? "|---:|---|---|---|---|---|" : "|---:|---|---|---|---|");
        foreach (var q in items)
        {
            number++;
            var cells = new List<string> { number.ToString(CultureInfo.InvariantCulture), Cell(q.Text) };
            if (details) cells.Add(Cell(q.Details));
            cells.Add(Cell(AppliesTo(q)));
            cells.Add(Cell(string.Join(", ", q.TeamList)));
            cells.Add(Blank);
            md.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
        md.AppendLine();
    }

    private static void Sketches(StringBuilder md, IEnumerable<string> keys, Func<string, string?> sketchPath, HashSet<string> shown)
    {
        foreach (var key in keys.Where(k => k.Length > 0).Distinct())
        {
            if (!shown.Add(key) || SubmissionChecklistDiagrams.Find(key) is not { } d || sketchPath(key) is not { } path) continue;
            md.AppendLine($"![{d.Title}]({path})");
            md.AppendLine();
        }
    }

    private static string Cell(string text) => (text ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
}
