namespace SketchProMax.Tools;

/// <summary>
/// The class wrapped around every C# snippet Claude writes. Kept free of Revit types so the tests can compile it.
/// </summary>
internal static class ScriptSource
{
    internal const string Header = """
        using System;
        using System.Linq;
        using System.Collections.Generic;
        using Autodesk.Revit.DB;
        using Autodesk.Revit.DB.Architecture;
        using Autodesk.Revit.DB.Structure;
        using Autodesk.Revit.DB.Mechanical;
        using Autodesk.Revit.DB.Plumbing;
        using Autodesk.Revit.DB.Electrical;
        using Autodesk.Revit.UI;
        public static class SketchProMaxScript
        {
            public static Func<bool> CancelProbe;
            static bool Cancelled() => CancelProbe != null && CancelProbe();
            static void CheckCancel() { if (Cancelled()) throw new OperationCanceledException("Stopped on request (cancel_job)."); }
            public static object Run(UIApplication app, UIDocument uidoc, Document doc, System.Text.StringBuilder log, IDictionary<string, object> args)
            {
        #line 1 "snippet"
        """;

    internal const string Footer = """

        #line default
            }
        }
        """;

    /// <summary>The full source for a snippet. The newline matters: without it the snippet's first line lands on the #line directive.</summary>
    public static string Wrap(string code) => Header + "\n" + code + Footer;

    /// <summary>
    /// Revit API failures snippets often hit, with the fix, so the next try is right the first time. Matched on the
    /// exception's message (any case). Add a row when a failure keeps coming back in real use.
    /// </summary>
    private static readonly (string Match, string Fix)[] Hints =
    [
        ("outside of Revit design limits", "Revit only takes points within about 32 km of the internal origin. This model's own coordinates can be far from it: build points from the elements' own geometry (bounding boxes, locations), never from shared/survey values or values in metres or millimetres not converted to feet."),
        ("revision numbering is per sheet", "With per-sheet revision numbering, read a revision's number on a sheet with sheet.GetRevisionNumberOnSheet(revisionId) instead of Revision.RevisionNumber."),
        ("Zoom Type is NOT Zoom", "Set PrintParameters.ZoomType = ZoomType.Zoom before setting Zoom (and ZoomType.FitToPage before PageOrientation-only changes)."),
        ("Zoom Type is NOT", "Set PrintParameters.ZoomType to the matching ZoomType before setting that value."),
        ("Object reference not set", "Something was null on that line: get_Parameter/LookupParameter, doc.GetElement, Category, Location, get_Geometry and ActiveView return null when absent. Check them (?. or 'is not null') before use."),
        ("no open transaction", "run_revit_query_code already runs inside a rolled-back transaction; don't start or commit your own there. In run_revit_code use own_transactions only for edit scopes."),
        ("is not a valid object", "An element or object was deleted or regenerated (for example after doc.Regenerate or a delete). Fetch it again by id after the change."),
        ("Can't cut joined element", "These elements are already joined or cut in a way that blocks this join; skip them and report them rather than unjoining anything."),
    ];

    /// <summary>The fix for a snippet's failure message, as a line to append ("" when none is known).</summary>
    public static string RuntimeHint(string message)
    {
        foreach (var (match, fix) in Hints)
            if (message.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) return " Hint: " + fix;
        return "";
    }

    /// <summary>
    /// The names in <paramref name="names"/> closest to <paramref name="wanted"/> (an API member that doesn't exist): ones
    /// containing it or contained in it first, then by edit distance; at most <paramref name="max"/>.
    /// </summary>
    public static List<string> Closest(string wanted, IEnumerable<string> names, int max = 6)
    {
        var w = wanted.ToLowerInvariant();
        return names.Distinct(StringComparer.Ordinal)
            .Select(n => (Name: n, Lower: n.ToLowerInvariant()))
            .Select(n => (n.Name, Score: n.Lower.Contains(w) || (w.Length >= 4 && w.Contains(n.Lower) && n.Lower.Length >= 4) ? 0 : Distance(w, n.Lower)))
            .Where(n => n.Score <= Math.Max(2, w.Length / 3))
            .OrderBy(n => n.Score).ThenBy(n => n.Name.Length).ThenBy(n => n.Name, StringComparer.Ordinal)
            .Take(max).Select(n => n.Name).ToList();
    }

    private static int Distance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var keep = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = keep;
            }
        }
        return d[b.Length];
    }
}
