using SketchProMax.Chat;

/// <summary>The chat view's pure rules: block sources for streaming, the draw throttle, following the latest message, history pages, times.</summary>
internal static class ChatViewTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // Streaming: only the changed tail of an answer is drawn again.
        var a = Markdown.ParseWithSource("# Title\n\nFirst para\nline two\n\n- one\n- two\n\nWor");
        var b = Markdown.ParseWithSource("# Title\n\nFirst para\nline two\n\n- one\n- two\n\nWorld and more");
        var keysA = a.Select(x => x.Source).ToList();
        var keysB = b.Select(x => x.Source).ToList();
        check("stream: block sources", keysA.SequenceEqual(new[] { "# Title", "First para\nline two", "- one\n- two", "Wor" }), string.Join("|", keysA));
        check("stream: only the last block changes", StreamRender.FirstChanged(keysA, keysB) == 3, StreamRender.FirstChanged(keysA, keysB).ToString());
        check("stream: same text, nothing to draw", StreamRender.FirstChanged(keysB, keysB) == keysB.Count, "");
        var growing = Markdown.ParseWithSource("Intro\n\n```cs\nvar a = 1;").Select(x => x.Source).ToList();
        var grown = Markdown.ParseWithSource("Intro\n\n```cs\nvar a = 1;\nvar b = 2;\n```\n\nAfter").Select(x => x.Source).ToList();
        check("stream: open code block keeps the blocks before it", StreamRender.FirstChanged(growing, grown) == 1 && grown.Count == 3, string.Join("|", grown));
        var para = Markdown.ParseWithSource("Intro\na | b").Select(x => x.Source).ToList();
        var table = Markdown.ParseWithSource("Intro\na | b\n--|--\n1 | 2").Select(x => x.Source).ToList();
        check("stream: a paragraph that turns into a table is drawn again", StreamRender.FirstChanged(para, table) == 0 && table.Count == 2, string.Join("|", table));
        check("stream: Parse matches ParseWithSource", Markdown.Parse("a\n\n- b").Count == 2, "");

        // Throttle.
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var throttle = new Throttle(TimeSpan.FromMilliseconds(40));
        check("throttle: first change draws at once", throttle.Request(t0) == TimeSpan.Zero, "");
        check("throttle: changes while waiting join that draw", throttle.Request(t0.AddMilliseconds(5)) is null && throttle.Pending, "");
        throttle.Drawn(t0.AddMilliseconds(10));
        check("throttle: next change waits out the interval", throttle.Request(t0.AddMilliseconds(20)) == TimeSpan.FromMilliseconds(30), "");
        throttle.Drawn(t0.AddMilliseconds(50));
        check("throttle: after a pause it draws at once", throttle.Request(t0.AddMilliseconds(500)) == TimeSpan.Zero, "");

        // Following the latest message.
        var follow = new ScrollFollow();
        follow.Scrolled(0, 500, extentChange: 120, viewportChange: 0);
        check("scroll: content growth keeps following", follow.Following, "");
        follow.Scrolled(200, 500, 0, 0);
        check("scroll: reader scrolling up stops following", !follow.Following && follow.ShowJump, "");
        follow.Added();
        follow.Added();
        check("scroll: messages added meanwhile count as unseen", follow.Unseen == 2, follow.Unseen.ToString());
        follow.Scrolled(200, 900, extentChange: 400, viewportChange: 0);
        check("scroll: growth while reading back doesn't follow", !follow.Following, "");
        follow.Scrolled(890, 900, 0, 0);
        check("scroll: back at the bottom follows again", follow.Following && follow.Unseen == 0 && !follow.ShowJump, "");
        follow.ReaderScrolledUp();
        check("scroll: wheel up stops at once", !follow.Following, "");
        follow.Jump();
        check("scroll: jump to latest follows", follow.Following, "");

        // History pages.
        check("history: short chat drawn whole", History.FirstShown(10) == 0, "");
        check("history: long chat draws the last page", History.FirstShown(300) == 300 - History.Page, "");
        check("history: show earlier adds a page", History.Earlier(100) == 100 - History.Page && History.Earlier(5) == 0, "");
        check("history: trims back to a page only at the bottom", History.Trim(2 * History.Page + 1, true) == History.Page + 1 && History.Trim(500, false) == 0 && History.Trim(History.Page, true) == 0, "");

        // Times.
        var now = new DateTime(2026, 10, 16, 15, 0, 0);
        check("time: today", StreamRender.Stamp(new DateTime(2026, 10, 16, 9, 5, 0), now) == "09:05", "");
        check("time: yesterday", StreamRender.Stamp(new DateTime(2026, 10, 15, 23, 59, 0), now) == "Yesterday 23:59", "");
        check("time: this week", StreamRender.Stamp(new DateTime(2026, 10, 12, 8, 0, 0), now) == "Mon 08:00", StreamRender.Stamp(new DateTime(2026, 10, 12, 8, 0, 0), now));
        check("time: this year", StreamRender.Stamp(new DateTime(2026, 3, 2, 8, 0, 0), now) == "2 Mar 08:00", "");
        check("time: older", StreamRender.Stamp(new DateTime(2025, 3, 2, 8, 0, 0), now) == "2 Mar 2025", "");
        check("time: none for old saves", StreamRender.Stamp(default, now) == "", "");
    }
}
