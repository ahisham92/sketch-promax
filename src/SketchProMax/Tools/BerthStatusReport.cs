using System.Globalization;
using System.Text;
using static SketchProMax.Tools.BerthStatusMath;

namespace SketchProMax.Tools;

/// <summary>
/// The Word report of Submissions status (UI/BerthStatusWindow), as Markdown for OfficeDocx: an overview of every
/// submission, then each one with its berths, its areas, its deliverables, what to fix and its comment. To show anyone.
/// </summary>
internal static class BerthStatusReport
{
    /// <summary>At most this many things to fix per area (the Excel export has them all).</summary>
    private const int FixesPerArea = 15;

    public static string Markdown(Setup setup, State state, IReadOnlyList<Summary> packages, DateTime now)
    {
        var md = new StringBuilder();
        md.Append("Status on ").Append(Date(now, true)).Append(". Percentages are read from the model, set by hand where noted, and from the deliverables' files.\n\n");

        md.Append("## Overview\n\n| Submission | Model | Status | Progress | Due | Expected finish |\n|---|---|---|---:|---|---|\n");
        foreach (var s in packages)
            md.Append("| ").Append(Cell(s.Berth.Name)).Append(" | ").Append(Cell(s.Berth.Model)).Append(" | ").Append(Text(s.Verdict)).Append(" | ")
              .Append(Pct(s.Percent)).Append(" | ").Append(Due(s)).Append(" | ").Append(s.Finish is { } f ? Date(f, false) + (s.FinishByHand ? " (set by hand)" : "") : "-").Append(" |\n");
        md.Append('\n');

        foreach (var s in packages)
        {
            md.Append("## ").Append(Line(s.Berth.Name)).Append(" (").Append(Line(s.Berth.Model)).Append(")\n\n");
            md.Append(Text(s.Verdict)).Append(", ").Append(Pct(s.Percent)).Append(". ");
            if (s.Next is { } next) md.Append("Next submission: ").Append(Line(next.Name)).Append(", due ").Append(Due(s)).Append(". ");
            else if (s.Last is { } last) md.Append("Submitted: ").Append(Line(last.Name)).Append(" on ").Append(Date(last.Date, false)).Append(". ");
            if (s.State.Engineers is { } n) md.Append(n).Append(n == 1 ? " engineer." : " engineers.");
            md.Append("\n\n");

            var children = ChildrenOf(s.Berth);
            if (children.Count > 0)
            {
                md.Append("| Berth | Progress | Modelling | Clashes | Drawings |\n|---|---:|---:|---:|---:|\n");
                foreach (var c in children.Select(c => Summarize(setup, c, state, now)))
                    md.Append("| ").Append(Cell(c.Berth.Name)).Append(" | ").Append(Pct(c.Percent)).Append(" | ").Append(Area(c, Modelling)).Append(" | ")
                      .Append(Area(c, Clashes)).Append(" | ").Append(Area(c, Drawings)).Append(" |\n");
                md.Append('\n');
            }

            md.Append("| Area | Progress | From | What the model says |\n|---|---:|---|---|\n");
            foreach (var a in s.Scores)
                md.Append("| ").Append(Cell(a.Area.Name)).Append(" | ").Append(Pct(a.Percent)).Append(" | ").Append(From(a.From)).Append(" | ")
                  .Append(Cell(a.Hand is { Comment.Length: > 0 } h ? h.Comment : a.Result?.Summary ?? "")).Append(" |\n");
            md.Append('\n');

            if (s.Deliverables.Count > 0)
            {
                md.Append("| Deliverable | Progress | From | Comment |\n|---|---:|---|---|\n");
                foreach (var d in s.Deliverables)
                    md.Append("| ").Append(Cell(d.Deliverable.Name)).Append(" | ").Append(Pct(d.Percent)).Append(" | ").Append(From(d.From)).Append(" | ")
                      .Append(Cell(d.Comment.Length > 0 ? d.Comment : d.File)).Append(" |\n");
                md.Append('\n');
            }

            var fixes = s.Scores.Where(a => a.Result is { Findings.Count: > 0 }).ToList();
            if (fixes.Count > 0)
            {
                md.Append("### What to fix\n\n");
                foreach (var a in fixes)
                {
                    var all = a.Result!.Findings;
                    md.Append("**").Append(Line(a.Area.Name)).Append("** (").Append(all.Count).Append(")\n\n");
                    foreach (var f in all.Take(FixesPerArea)) md.Append("- ").Append(Line(f.Item)).Append(": ").Append(Line(f.Problem)).Append('\n');
                    if (all.Count > FixesPerArea) md.Append("- … and ").Append(all.Count - FixesPerArea).Append(" more (in the Excel export)\n");
                    md.Append('\n');
                }
            }
            if (s.State.Comment.Trim().Length > 0) md.Append("### Comment\n\n").Append(Line(s.State.Comment)).Append("\n\n");
        }
        return md.ToString();
    }

    private static string Area(Summary s, string key) => Pct(s.Scores.FirstOrDefault(a => a.Area.Key == key)?.Percent);

    private static string Due(Summary s) => s.Due is { } d ? Date(d, true) : "-";

    private static string From(Source f) => f switch
    {
        Source.Model => "Model",
        Source.Hand => "By hand",
        Source.Files => "Files",
        _ => "-",
    };

    private static string Date(DateTime d, bool time) => d.ToString(time ? "dd-MMM-yyyy HH:mm" : "dd-MMM-yyyy", CultureInfo.InvariantCulture);

    private static string Line(string s) => (s ?? "").Replace("\r", "").Replace("\n", " ").Trim();

    private static string Cell(string s) => Line(s).Replace("|", "/");
}
