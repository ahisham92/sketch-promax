using SketchProMax.Chat;

/// <summary>"No need to pop up anything, I will not be available for the next hour" (Chat.Away). No Revit here.</summary>
internal static class AwayTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Away.Reset();
        var said = "move the 0.1m is fine, also no need to pop up anything as I will not be available for the next hour";
        check("away: his words of 19 Nov start it, for an hour", Away.Asked(said) == TimeSpan.FromHours(1), Away.Asked(said)?.ToString() ?? "null");
        check("away: the time said is kept, at most 12 hours, at least 10 minutes",
            Away.Asked("I'm away for 2 hours, handle the pop ups") == TimeSpan.FromHours(2) && Away.Asked("no pop ups for 30 minutes") == TimeSpan.FromMinutes(30)
            && Away.Asked("I'll be out for 20 hours") == Away.Longest && Away.Asked("no popups for 1 min") == TimeSpan.FromMinutes(10), "");
        check("away: ordinary messages don't start it",
            Away.Asked("why does the popup menu show lines") is null && Away.Asked("raise the approach slab by 100 mm") is null && Away.Asked("") is null, "");
        var now = new DateTime(2026, 11, 19, 11, 44, 0, DateTimeKind.Utc);
        var note = Away.Heard(said, now, t => t.AddHours(2));
        check("away: the chat says until when (his clock) and what happens meanwhile", note is not null && note.Contains("until 14:44") && note.Contains("Cancel, else No, else OK")
            && note.Contains("risky changes (deletes) wait for you"), note ?? "null");
        Away.Note("Revit asked \"Delete the view?\": answered Cancel.");
        Away.Note("Skipped a step only you can do (cut_slab): finish the sketch");
        var list = Away.Take();
        check("away: what was answered or skipped is said once", list.StartsWith("While you were away:") && list.Contains("answered Cancel.") && list.Contains("finish the sketch.") && Away.Take() == "", list);
        check("away: another message doesn't end it; I'm back does", Away.Heard("also check the pits", now.AddMinutes(5)) is null
            && Away.Heard("I'm back", now.AddMinutes(30)) is { } back && back.Contains("off"), "");
        Away.Heard("no pop ups please, I'm away", now);
        check("away: it ends by itself when the time is up", Away.Heard("hello", now.AddHours(2)) is null, "");
        Away.Reset();
    }
}
