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

        // "Jump to latest": only over messages that continue below the viewport.
        check("jump: never on the empty home screen", !ScrollFollow.JumpVisible(false, false, 900, 0), "");
        check("jump: shows when reading back", ScrollFollow.JumpVisible(true, false, 900, 100), "");
        check("jump: hidden while following", !ScrollFollow.JumpVisible(true, true, 900, 100), "");
        check("jump: hidden within the slack of the bottom", !ScrollFollow.JumpVisible(true, false, 900, 900 - ScrollFollow.Slack + 1), "");

        // The home screen's greeting.
        check("greeting: display name", Greeting.FirstName("Ahmed Mitwally") == "Ahmed", Greeting.FirstName("Ahmed Mitwally") ?? "null");
        check("greeting: last, first", Greeting.FirstName("Mitwally, Ahmed") == "Ahmed", Greeting.FirstName("Mitwally, Ahmed") ?? "null");
        check("greeting: capitals and a note in brackets", Greeting.FirstName("AHMED MITWALLY (Structures)") == "Ahmed", Greeting.FirstName("AHMED MITWALLY (Structures)") ?? "null");
        check("greeting: title skipped", Greeting.FirstName("Eng. Sara Ali") == "Sara" && Greeting.FirstName("Dr Omar Hassan") == "Omar", "");
        check("greeting: sign-in name", Greeting.FirstName("ahmed.mitwally") == "Ahmed" && Greeting.FirstName("CORP\\sara_ali") == "Sara", "");
        check("greeting: e-mail", Greeting.FirstName("jean-luc.picard@example.com") == "Jean", Greeting.FirstName("jean-luc.picard@example.com") ?? "null");
        check("greeting: hyphenated display name kept", Greeting.FirstName("jean-luc picard") == "Jean-Luc", Greeting.FirstName("jean-luc picard") ?? "null");
        check("greeting: mixed case kept", Greeting.FirstName("McKay Smith") == "McKay", "");
        check("greeting: not a name", Greeting.FirstName("a.mitwally") is null && Greeting.FirstName("user123") is null && Greeting.FirstName("  ") is null && Greeting.FirstName(null) is null, "");
        check("greeting: non-Latin name", Greeting.FirstName("أحمد متولي") == "أحمد", Greeting.FirstName("أحمد متولي") ?? "null");
        check("greeting: first usable candidate", Greeting.Pick(null, "", "x1", "ahmed") == "Ahmed" && Greeting.Pick("user1") is null, "");
        // The chat's styles are parsed at run time with a plain fallback, so a slip would quietly lose the whole look.
        var xaml = ChatStyles.Xaml(8, 12);
        System.Xml.Linq.XDocument? styles = null;
        try { styles = System.Xml.Linq.XDocument.Parse(xaml); } catch (System.Xml.XmlException ex) { check("styles: well formed", false, ex.Message); }
        if (styles is not null)
        {
            var keys = styles.Descendants().Select(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))).Where(k => k is not null).ToList();
            var wanted = new[] { "SpmChatFocus", "SpmChatFocusCard", "SpmChatFocusRound", "SpmChatGhost", "SpmChatIcon", "SpmChatPill", "SpmChatPrimary", "SpmChatFloat", "SpmChatCard", "SpmChatMenuItem", "SpmChatSend", "SpmChatStop", "SpmChatField", "SpmChatBare", "SpmChatDisclosure" };
            check("styles: well formed with every key", wanted.All(keys.Contains) && keys.Count == keys.Distinct().Count(), string.Join(",", keys));
            var left = System.Text.RegularExpressions.Regex.Matches(xaml, @"\b(FOCUS|GHOST|PILL|PRIMARY|FLOAT|CARD|SEND|STOP|SMALL|KEY|BEHIND|RADIUS|HOVERBG|HOVERBORDER|BG|FG|HOVER|PRESSED)\b").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).ToList();
            check("styles: no placeholder left", left.Count == 0, string.Join(",", left));
            check("styles: corners from the spacing scale", xaml.Contains("CornerRadius=\"8\"") && xaml.Contains("CornerRadius=\"12\"") && xaml.Contains("RadiusX=\"10\""), "");
        }
        check("greeting: line", Greeting.Line("Ahmed") == "Hey Ahmed!" && Greeting.Line(null) == "Hey there!", "");
    }
}
