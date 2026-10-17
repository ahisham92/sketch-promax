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

        // Hover fades and presses: on the chrome only, never on the label; no fade when Windows' animations are off.
        var still = ChatStyles.Xaml(8, 12, animate: false);
        check("styles: hover fades in 160 ms, ease-out", xaml.Contains("Duration=\"0:0:0.160\"") && xaml.Contains("CubicEase EasingMode=\"EaseOut\"") && !xaml.Contains("Duration=\"0:0:0\""), "");
        check("styles: no fade with animations off", still.Contains("Duration=\"0:0:0\"") && !still.Contains("0:0:0.160"), "");
        check("styles: press shrinks the chrome, not the text", xaml.Contains("<Setter TargetName=\"chrome\" Property=\"RenderTransform\"><Setter.Value><ScaleTransform ScaleX=\"0.98\"") && !System.Text.RegularExpressions.Regex.IsMatch(xaml, @"Property=""Opacity"" Value=""0\.\d"""), "");
        // A template Setter can only name a FrameworkElement: one naming a ScaleTransform made WPF refuse every chat style (0.32-0.33.1).
        var named = System.Text.RegularExpressions.Regex.Matches(xaml, @"<(\w+)[^>]*x:Name=""(\w+)""").Cast<System.Text.RegularExpressions.Match>()
            .Where(m => m.Groups[1].Value.EndsWith("Transform", StringComparison.Ordinal)).Select(m => m.Groups[2].Value).ToList();
        check("styles: no template setter targets a named transform", named.All(n => !xaml.Contains("TargetName=\"" + n + "\"")), string.Join(",", named));
        var plain = ChatStyles.Xaml(8, 12, animate: false, plain: true);
        check("styles: the plain fallback has the same keys, no press or shadow", System.Xml.Linq.XDocument.Parse(plain) is not null
            && !plain.Contains("ScaleTransform") && !plain.Contains("DropShadowEffect") && plain.Contains("x:Key=\"SpmChatCard\"") && plain.Contains("x:Key=\"SpmChatSend\""), "");
        // The composer's message box: typed text starts top-left where the hint is, the whole box takes the click, the caret shows.
        foreach (var (name, x) in new[] { ("normal", xaml), ("plain", plain) })
        {
            var bare = System.Xml.Linq.XDocument.Parse(x).Descendants()
                .First(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SpmChatBare");
            string? Setter(string property) => bare.Elements().Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property)
                .Select(e => (string?)e.Attribute("Value")).FirstOrDefault();
            var host = bare.Descendants().FirstOrDefault(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "PART_ContentHost");
            check($"composer ({name}): text top-aligned", Setter("VerticalContentAlignment") == "Top" && (string?)host?.Attribute("VerticalContentAlignment") == "Top"
                && host?.Attribute("VerticalAlignment") is null or { Value: "Stretch" }, bare.ToString().Length.ToString());
            check($"composer ({name}): the whole box, padding too, takes the click", host?.Parent is { Name.LocalName: "Border" } root && (string?)root.Attribute("Background") == "Transparent", "");
            check($"composer ({name}): caret and selection follow the theme", Setter("CaretBrush") == "{DynamicResource SpmText}" && Setter("Foreground") == "{DynamicResource SpmText}"
                && Setter("SelectionBrush") == "{DynamicResource SpmAccent}", "");
            check($"composer ({name}): its own scroll template, scroll bar only when needed", host!.Descendants().Any(e => e.Name.LocalName == "ScrollContentPresenter")
                && host.Descendants().Any(e => e.Name.LocalName == "ScrollBar" && ((string?)e.Attribute("Visibility") ?? "").Contains("ComputedVerticalScrollBarVisibility")), "");
        }
        check("composer: border at rest, hover and focus", ChatStyles.ComposerBorder(false, false) == ("SpmInputBorder", 1) && ChatStyles.ComposerBorder(false, true) == ("SpmText", 1)
            && ChatStyles.ComposerBorder(true, false) == ("SpmFocus", 2) && ChatStyles.ComposerBorder(true, true) == ("SpmFocus", 2), "");
        check("composer: plain hint that says what to do", ChatStyles.ComposerHint.StartsWith("Ask anything about your model", StringComparison.Ordinal) && !ChatStyles.ComposerHint.Contains("Sketch ProMax"), ChatStyles.ComposerHint);
        check("composer: about two lines at rest, grows to a limit", ChatStyles.ComposerMinHeight >= 2 * ChatStyles.Body * 1.33 + 2 * ChatStyles.ComposerPadY - 1
            && ChatStyles.ComposerMaxHeight >= 3 * ChatStyles.ComposerMinHeight, ChatStyles.ComposerMinHeight.ToString());
        check("styles: every template has a hover layer", System.Text.RegularExpressions.Regex.Matches(xaml, "x:Name=\"hv\"").Count >= 8, "");

        // The type scale.
        check("type: 12/13/14/16/20", ChatStyles.Caption == 12 && ChatStyles.Body == 13 && ChatStyles.Subtitle == 14 && ChatStyles.Title == 16 && ChatStyles.Display == 20, "");
        check("type: Ideal text from 15 px", ChatStyles.Title >= ChatStyles.IdealFrom && ChatStyles.Subtitle < ChatStyles.IdealFrom, "");

        // The glide to the newest message: ease-out on whole device pixels, never past the end.
        var g1 = SmoothScroll.Next(0, 400, 600, 1);
        check("glide: a quarter of the way per frame", g1 == 100, g1.ToString());
        var g2 = SmoothScroll.Next(0, 401, 600, 1.25);
        check("glide: whole device pixels at 125 %", Math.Abs(g2 * 1.25 - Math.Round(g2 * 1.25)) < 1e-9, g2.ToString());
        check("glide: from far away starts a viewport above the end", SmoothScroll.Next(0, 5000, 600, 1) == 4550, SmoothScroll.Next(0, 5000, 600, 1).ToString());
        check("glide: lands exactly on the end", SmoothScroll.Next(399.6, 400, 600, 1) == 400 && SmoothScroll.Next(398, 400, 600, 1) == 399, SmoothScroll.Next(398, 400, 600, 1).ToString());
        var pos = 0.0;
        var steps = 0;
        while (pos < 300 && steps < 100) { pos = SmoothScroll.Next(pos, 300, 600, 1.5); steps++; }
        check("glide: arrives within half a second at 60 Hz", pos == 300 && steps <= 30, steps.ToString());

        // Frame times.
        var stamps = new[] { 0, 16, 33, 33, 50, 116, 133 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToList();
        var frames = FrameTimes.Of(stamps);
        check("frames: repeated stamps skipped, average and worst", frames.Frames == 5 && Math.Abs(frames.AverageMs - 133.0 / 5) < 1e-9 && frames.WorstMs == 66 && frames.Hitches == 1, $"{frames}");
        check("frames: smooth at 60 fps, not with a long stall", FrameTimes.Smooth(16.7, 40) && !FrameTimes.Smooth(16.7, 400) && !FrameTimes.Smooth(40, 60) && !FrameTimes.Smooth(0, 0), "");
    }
}
