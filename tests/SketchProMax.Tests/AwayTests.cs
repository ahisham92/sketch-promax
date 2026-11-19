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
        check("away: his words of 19 Nov evening start it too (won't, handle the waiting)",
            Away.Asked("Continue as I won't be available and handle the waiting to apply or waiting for revit to be free") == TimeSpan.FromHours(1)
            && Away.Asked("I wont be here for 2 hours") == TimeSpan.FromHours(2) && Away.Asked("skip any stuck command") is not null, "");
        var ways = new[]
        {
            "I will be away", "I'll be away for 2 hours", "I won\u2019t be available", "I am not available now", "Im going home", "I'm leaving now",
            "I'll be back in an hour", "I'm in a meeting", "I have to go to a meeting, continue", "I'm going to sleep, carry on", "don't wait for me",
            "carry on without me", "I might not be around", "I'll be offline", "afk 30 min", "I'm stepping out", "I won't be at my desk", "I will not be here",
            "I'm heading home", "while I'm away handle everything",
        };
        var missed = ways.Where(w => Away.Asked(w) is null).ToList();
        check("away: any way of saying he isn't at Revit starts it", missed.Count == 0, string.Join(" | ", missed));
        var wrong = new[] { "the bollard type is not available", "move the beam out by 100 mm", "is the slab here?", "I am checking the pile heads", "I need to see the sections", "leave the RoRo as is", "I'm here", "I am available now", "I'll be at my desk", "I need to leave the pile heads as they are" }
            .Where(w => Away.Asked(w) is not null).ToList();
        check("away: work messages that mention such words don't", wrong.Count == 0, string.Join(" | ", wrong));
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

/// <summary>Chat transcripts kept for good (Chat.HistoryArchive), in a temporary folder.</summary>
internal static class HistoryArchiveTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "spm-history-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "projects");
        var archive = Path.Combine(root, "archive");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "C--Users-me"));
            var a = Path.Combine(source, "C--Users-me", "one.jsonl");
            var b = Path.Combine(source, "C--Users-me", "two.jsonl");
            File.WriteAllText(a, "{\"n\":1}\n{\"n\":2}\n");
            File.WriteAllText(b, "{\"n\":3}\n");
            var first = SketchProMax.Chat.HistoryArchive.Copy(source, archive);
            var again = SketchProMax.Chat.HistoryArchive.Copy(source, archive);
            check("history: every transcript is copied, compressed, once until it changes", first == 2 && again == 0
                && File.Exists(Path.Combine(archive, "C--Users-me", "one.jsonl.gz")), first + " " + again);
            var bothLive = SketchProMax.Chat.HistoryArchive.All(source, archive);
            File.Delete(a); // Claude Code's 30-day clean-up
            var after = SketchProMax.Chat.HistoryArchive.All(source, archive);
            var lines = after.SelectMany(f => f()).ToList();
            check("history: a transcript Claude Code deleted is still read from its copy, and none is read twice",
                bothLive.Count == 2 && after.Count == 2 && lines.Count == 3 && lines.Contains("{\"n\":1}") && lines.Contains("{\"n\":3}"), string.Join(",", lines));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
