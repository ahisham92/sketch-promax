using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
            public static Func<string> StopReason;
            static bool Cancelled() => CancelProbe != null && CancelProbe();
            static void CheckCancel() { if (Cancelled()) throw new OperationCanceledException((StopReason != null ? StopReason() : null) ?? "Stopped on request (cancel_job)."); }
            static int spmLast;
            static void SpmCheckpoint() { var now = Environment.TickCount; if (spmLast != 0 && unchecked(now - spmLast) < 20) return; spmLast = now == 0 ? 1 : now; CheckCancel(); }
            static T SpmCheckpoint<T>(T value) { SpmCheckpoint(); return value; }
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
    /// The snippet as it is compiled, most checked first: with a stop check (SpmCheckpoint) at the start of every loop
    /// step and lambda (expression lambdas too), then with checks in loops and block lambdas only (when the first doesn't
    /// compile, such as a lambda that returns nothing), then as written (its errors are the ones reported). So a snippet
    /// stops at its next step when the user, the Rescue window or the time cap asks, even without calling CheckCancel()
    /// itself, and Revit is never held by one snippet for long. The checks go in without new lines, so line numbers in
    /// errors stay the snippet's own.
    /// </summary>
    public static List<string> Variants(string code)
    {
        var list = new List<string>();
        foreach (var lambdas in new[] { true, false })
        {
            string checkedCode;
            try { checkedCode = Checkpoints(code, lambdas); }
            catch (Exception) { continue; } // a snippet the parser can't take is compiled as written
            if (!list.Contains(checkedCode)) list.Add(checkedCode);
        }
        if (!list.Contains(code)) list.Add(code);
        return list;
    }

    /// <summary>
    /// <paramref name="code"/> with SpmCheckpoint() at the start of each loop body (a single-statement body is wrapped in
    /// braces) and each block-bodied lambda, anonymous method and local function; with <paramref name="expressionLambdas"/>
    /// an expression lambda's body is wrapped too (x => SpmCheckpoint(body)).
    /// </summary>
    internal static string Checkpoints(string code, bool expressionLambdas)
    {
        var prefix = Header.Length + 1;
        var root = CSharpSyntaxTree.ParseText(Wrap(code), new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
        var edits = new List<(int At, int Order, string Text)>();
        bool Inside(SyntaxNode n) => n.SpanStart >= prefix && n.Span.End <= prefix + code.Length;
        void AtBlockStart(BlockSyntax block) => edits.Add((block.OpenBraceToken.Span.End - prefix, 0, " SpmCheckpoint();"));
        void Body(StatementSyntax body)
        {
            if (body is BlockSyntax block) AtBlockStart(block);
            else
            {
                edits.Add((body.SpanStart - prefix, 1, "{ SpmCheckpoint(); "));
                edits.Add((body.Span.End - prefix, -1, " }"));
            }
        }
        foreach (var node in root.DescendantNodes())
        {
            if (!Inside(node)) continue;
            switch (node)
            {
                case ForStatementSyntax f: Body(f.Statement); break;
                case ForEachStatementSyntax f: Body(f.Statement); break;
                case ForEachVariableStatementSyntax f: Body(f.Statement); break;
                case WhileStatementSyntax w: Body(w.Statement); break;
                case DoStatementSyntax d: Body(d.Statement); break;
                case LocalFunctionStatementSyntax { Body: { } block }: AtBlockStart(block); break;
                case AnonymousMethodExpressionSyntax { Block: { } block }: AtBlockStart(block); break;
                case LambdaExpressionSyntax { Block: { } block }: AtBlockStart(block); break;
                case LambdaExpressionSyntax { ExpressionBody: { } body } when expressionLambdas && body is not ThrowExpressionSyntax:
                    edits.Add((body.SpanStart - prefix, 1, "SpmCheckpoint("));
                    edits.Add((body.Span.End - prefix, -1, ")"));
                    break;
            }
        }
        // From the end, so earlier positions stay right. At one position a closing goes in before an opening, and the
        // closing of an inner body before that of the outer one (their order between equal closings doesn't matter).
        var text = new System.Text.StringBuilder(code);
        foreach (var (at, _, insert) in edits.OrderByDescending(e => e.At).ThenByDescending(e => e.Order))
            text.Insert(at, insert);
        return text.ToString();
    }

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
