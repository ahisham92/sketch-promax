using SketchProMax.Revit;

/// <summary>The activity log's account of how much of Revit's time Sketch ProMax's background work takes.</summary>
internal static class RevitTimeTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var t0 = new DateTime(2026, 10, 29, 9, 0, 0);
        var tally = new UiCostTally(t0);
        check("revit time: a quick run is only counted", tally.Add("time log", 3, t0) is null, "");
        var slow = tally.Add("Lifeline change file", 420, t0.AddSeconds(1));
        check("revit time: a slow run is written", slow == "Sketch ProMax held Revit for 420 ms (Lifeline change file)", slow ?? "");
        check("revit time: once a minute per kind of work", tally.Add("Lifeline change file", 600, t0.AddSeconds(30)) is null, "");
        check("revit time: again after a minute", tally.Add("Lifeline change file", 300, t0.AddSeconds(70)) is not null, "");
        check("revit time: no total before 10 minutes", tally.Due(t0.AddMinutes(9)) is null, "");
        var total = tally.Due(t0.AddMinutes(10));
        check("revit time: the total, heaviest first, with its share of the time",
            total == "Sketch ProMax used 1.3 s of Revit's time in the last 10 min (0.2%): Lifeline change file 1.3 s in 3 runs (longest 600 ms), time log 3 ms in 1 run (longest 3 ms)", total ?? "");
        check("revit time: the sums start again", tally.Due(t0.AddMinutes(20)) is null, "");
        tally.Add("time log", 20, t0.AddMinutes(21));
        check("revit time: a small total is not written", tally.Due(t0.AddMinutes(31)) is null, "");
        check("revit time: the change file reads in short slices", ChangeFileMath.ReadBudgetMs is > 0 and <= 50, ChangeFileMath.ReadBudgetMs.ToString());
    }
}
