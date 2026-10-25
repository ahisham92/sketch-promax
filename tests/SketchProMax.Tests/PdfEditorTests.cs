/// <summary>The PDF editor's engine and document on sample PDFs (PdfEditCheck), plus its pure rules.</summary>
internal static class PdfEditorTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "spm-pdfedit-" + Guid.NewGuid().ToString("N"));
        try { SketchProMax.Tools.PdfEditCheck.Run(folder, check); }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }

        var starts = SketchProMax.Tools.PdfEditSession.Starts(10, SketchProMax.Tools.PdfEditSession.SplitBy.EveryN, 3, null);
        check("pdf editor: parts every 3 of 10 pages start at 1, 4, 7 and 10", starts.SequenceEqual([0, 3, 6, 9]), string.Join(",", starts));
        var picked = SketchProMax.Tools.PdfEditSession.Starts(10, SketchProMax.Tools.PdfEditSession.SplitBy.AtSelected, 0, [7, 3, 3, 0, 12]);
        var range = SketchProMax.Tools.PdfEditSession.ParseRange("1-3, 5, 8-", 10);
        check("pdf editor: page numbers typed as 1-3, 5, 8-", range is not null && range.SequenceEqual([0, 1, 2, 4, 7, 8, 9]), range is null ? "null" : string.Join(",", range));
        check("pdf editor: all, backwards ranges and wrong pages", SketchProMax.Tools.PdfEditSession.ParseRange("all", 3)!.Count == 3
            && SketchProMax.Tools.PdfEditSession.ParseRange("3-1", 3)!.SequenceEqual([2, 1, 0]) && SketchProMax.Tools.PdfEditSession.ParseRange("0", 3) is null
            && SketchProMax.Tools.PdfEditSession.ParseRange("4", 3) is null && SketchProMax.Tools.PdfEditSession.ParseRange("a", 3) is null
            && SketchProMax.Tools.PdfEditSession.ParseRange("", 3) is null, "");
        check("pdf editor: selected pages named for a file", SketchProMax.Tools.PdfEditSession.Describe([4, 2, 3]) == "3-5"
            && SketchProMax.Tools.PdfEditSession.Describe([0]) == "1" && SketchProMax.Tools.PdfEditSession.Describe([0, 4]) == "1, 5", "");
        check("pdf editor: parts at the selected pages always start with the first", picked.SequenceEqual([0, 3, 7]), string.Join(",", picked));
    }
}
