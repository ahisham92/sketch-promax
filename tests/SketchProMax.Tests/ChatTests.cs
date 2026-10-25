using System.Text;
using System.Text.Json;
using SketchProMax.Chat;

/// <summary>The chat pane's stream-json reader and Markdown reader, which need no Revit and no WPF.</summary>
internal static class ChatTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // A turn as Claude Code streams it with --include-partial-messages: init, streamed text, a tool call, its result,
        // the complete assistant messages, the result. Split mid-line to check partial lines are kept.
        var lines = new[]
        {
            """{"type":"system","subtype":"init","session_id":"s-1","model":"claude-x","mcp_servers":[{"name":"sketch-promax","status":"connected"}],"capabilities":["interrupt_receipt_v1"],"tools":["Read"]}""",
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_1"}},"parent_tool_use_id":null,"session_id":"s-1"}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo **there**"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tu_1","name":"mcp__sketch-promax__get_model_context","input":{}}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{}"}},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"text","text":"Hello **there**"},{"type":"tool_use","id":"tu_1","name":"mcp__sketch-promax__get_model_context","input":{"detail":"short"}}]},"parent_tool_use_id":null,"session_id":"s-1"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_1","content":[{"type":"text","text":"units: mm"}],"is_error":false}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_sub","content":[{"type":"text","text":"subagent text"}]},"parent_tool_use_id":"tu_9"}""",
            """{"type":"some_future_event","x":1}""",
            "not json at all",
            """{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":500,"error_status":529,"error":"overloaded"}""",
            """{"type":"assistant","message":{"id":"msg_2","content":[{"type":"text","text":"Done."}]},"parent_tool_use_id":null}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"s-1","usage":{"input_tokens":10,"cache_read_input_tokens":5,"output_tokens":7}}""",
        };
        var all = string.Join("\n", lines) + "\n";
        var parser = new StreamParser();
        var events = new List<ChatEvent>();
        for (var at = 0; at < all.Length; at += 37) events.AddRange(parser.Feed(all.Substring(at, Math.Min(37, all.Length - at))));

        check("stream: init gives the session id and MCP status",
            events.OfType<InitEvent>().SingleOrDefault() is { SessionId: "s-1" } init && init.Servers.Count == 1 && init.Servers[0].Status == "connected", "");
        var streamed = string.Concat(events.OfType<TextDeltaEvent>().Select(d => d.Text));
        check("stream: text deltas join across split lines", streamed == "Hello **there**", streamed);
        check("stream: streamed text is not repeated from the complete message",
            !events.OfType<AssistantTextEvent>().Any(e => e.MessageId == "msg_1"), "");
        check("stream: unstreamed message text is shown", events.OfType<AssistantTextEvent>().Any(e => e.Text == "Done."), "");
        var tools = events.OfType<ToolUseEvent>().ToList();
        check("stream: tool call seen at start and with its input",
            tools.Count == 2 && tools[0].InputJson == "" && tools[1].InputJson.Contains("short") && StreamParser.ShortName(tools[1].Name) == "get_model_context",
            string.Join(" | ", tools));
        check("stream: tool result matched by id", events.OfType<ToolResultEvent>().SingleOrDefault() is { ToolUseId: "tu_1", IsError: false, Text: "units: mm" }, "");
        check("stream: subagent and unknown lines ignored", !events.OfType<AssistantTextEvent>().Any(e => e.Text.Contains("subagent")), "");
        check("stream: retry event", events.OfType<RetryEvent>().SingleOrDefault() is { Attempt: 1, Error: "overloaded" }, "");
        check("stream: result with usage", events.OfType<ResultEvent>().SingleOrDefault() is { IsError: false, SessionId: "s-1", InputTokens: 15, OutputTokens: 7 }, "");
        var error = new StreamParser().ParseLine("""{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["boom"],"session_id":"s"}""");
        check("stream: error result", error.SingleOrDefault() is ResultEvent { IsError: true } r && r.Errors.SequenceEqual(new[] { "boom" }), "");

        // Several lines in one chunk, a chunk that ends exactly at a newline, empty chunks, CRLF line ends.
        var bulk = new StreamParser();
        var got = new List<ChatEvent>();
        got.AddRange(bulk.Feed(""));
        got.AddRange(bulk.Feed(lines[3] + "\r\n" + lines[4] + "\n"));
        got.AddRange(bulk.Feed(""));
        got.AddRange(bulk.Feed(lines[14].Substring(0, 20)));
        var beforeEnd = got.Count;
        got.AddRange(bulk.Feed(lines[14].Substring(20) + "\n"));
        check("stream: many lines per chunk, CRLF and empty chunks",
            string.Concat(got.OfType<TextDeltaEvent>().Select(d => d.Text)) == "Hello **there**" && beforeEnd == 2 && got.Last() is ResultEvent, "");
        // A long line in many small pieces: each piece is searched once, so this stays fast.
        var longText = new string('x', 400_000);
        var longLine = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"" + longText + "\",\"session_id\":\"s\"}\n";
        var slow = new StreamParser();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var longEvents = new List<ChatEvent>();
        for (var at = 0; at < longLine.Length; at += 64) longEvents.AddRange(slow.Feed(longLine.Substring(at, Math.Min(64, longLine.Length - at))));
        check("stream: a long line in small pieces parses quickly",
            longEvents.SingleOrDefault() is ResultEvent && timer.ElapsedMilliseconds < 2000, $"{timer.ElapsedMilliseconds} ms");

        // Caller-supplied output paths.
        string?[] fine = [null, "", @"C:\Users\me\Documents\packs", @"D:\Projects\Tower", "packs", @"sub\name", "Console notes", "Connection.csv", "my.con.txt"];
        foreach (var p in fine)
            check($"paths: '{p}' is allowed", SketchProMax.Tools.OutputPaths.Problem(p) is null, SketchProMax.Tools.OutputPaths.Problem(p) ?? "");
        string[] refused = [@"\\server\share\packs", "//server/share", @"\\?\C:\x", @"\\.\PhysicalDrive0", "CON", @"C:\temp\nul.txt", "com1", @"C:\x\LPT9 ", @"C:\x\file.txt:stream", "ab:c", @" \\server\s"];
        foreach (var p in refused)
            check($"paths: '{p}' is refused", SketchProMax.Tools.OutputPaths.Problem(p) is not null, "");
        var threw = false;
        try { SketchProMax.Tools.OutputPaths.Checked(@"\\server\share"); } catch (ArgumentException) { threw = true; }
        check("paths: Checked throws ArgumentException", threw && SketchProMax.Tools.OutputPaths.Checked(@"C:\a") == @"C:\a", "");

        check("chat approval: relative paths shown in full", SketchProMax.Tools.OutputPaths.FullPath("out") == Path.GetFullPath("out"), "");

        // What the pane writes.
        using (var doc = JsonDocument.Parse(StreamInput.UserMessage("hi \"there\"", new[] { ("image/png", "AAAA") })))
        {
            var m = doc.RootElement;
            var content = m.GetProperty("message").GetProperty("content");
            check("input: user message shape",
                m.GetProperty("type").GetString() == "user" && m.GetProperty("message").GetProperty("role").GetString() == "user" &&
                content[0].GetProperty("type").GetString() == "image" && content[0].GetProperty("source").GetProperty("media_type").GetString() == "image/png" &&
                content[1].GetProperty("text").GetString() == "hi \"there\"", m.GetRawText());
        }
        using (var doc = JsonDocument.Parse(StreamInput.UserMessage("read it", [], [("spec.pdf", "JVBERg==")])))
        {
            var content = doc.RootElement.GetProperty("message").GetProperty("content");
            check("input: a PDF goes as a document block before the text",
                content[0].GetProperty("type").GetString() == "document" &&
                content[0].GetProperty("source").GetProperty("media_type").GetString() == "application/pdf" &&
                content[0].GetProperty("source").GetProperty("data").GetString() == "JVBERg==" &&
                content[0].GetProperty("title").GetString() == "spec.pdf" && content[1].GetProperty("text").GetString() == "read it", doc.RootElement.GetRawText());
        }
        {
            var plain = PdfAttachment.Assess(PdfAttachment.Sample(3));
            var packed = PdfAttachment.Assess(PdfAttachment.Sample(7, compressed: true));
            var many = PdfAttachment.Assess(PdfAttachment.Sample(101));
            var locked = PdfAttachment.Assess(PdfAttachment.Sample(2, locked: true));
            var full = PdfAttachment.Assess(PdfAttachment.Sample(10), usedPages: 95);
            var big = PdfAttachment.Assess(PdfAttachment.Sample(1), usedBytes: PdfAttachment.MaxBytes);
            var notPdf = PdfAttachment.Assess(Encoding.ASCII.GetBytes("hello"));
            check("pdf: short PDFs go whole, page objects counted also inside compressed object streams",
                plain is { Verdict: PdfAttachment.Verdict.Send, Pages: 3 } && packed is { Verdict: PdfAttachment.Verdict.Send, Pages: 7 }, $"{plain} {packed}");
            check("pdf: over 100 pages (alone or with the others), over the size, locked or not a PDF are not sent whole",
                many.Verdict == PdfAttachment.Verdict.TooManyPages && full.Verdict == PdfAttachment.Verdict.TooManyPages &&
                big.Verdict == PdfAttachment.Verdict.TooBig && locked.Verdict == PdfAttachment.Verdict.Locked && notPdf.Verdict == PdfAttachment.Verdict.NotPdf,
                $"{many} {full} {big} {locked} {notPdf}");
            var noText = PdfAttachment.Note(many, null, false);
            var withText = PdfAttachment.Note(big, @"C:\a\spec - text.txt", false);
            var pictures = PdfAttachment.Note(many, @"C:\a\set - text.txt", true);
            check("pdf: a PDF not sent whole points to its text file, and to redline_read for pages",
                noText.Contains("redline_read") && noText.Contains("101 pages") && withText.Contains(@"C:\a\spec - text.txt") && withText.Contains("Grep") &&
                withText.Contains("Split PDF") && pictures.Contains("little or no text") && PdfAttachment.Note(locked, null, false).Contains("unlocked copy"), withText);
            var counted = PdfAttachment.Assess(PdfAttachment.Sample(1), knownPages: 30);
            check("pdf: PdfPig's page count is used when larger", counted.Pages == 30, counted.ToString());
        }
        {
            // PdfPig: the text of every page, and splitting by size.
            var folder = Path.Combine(Path.GetTempPath(), "spm-pdf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var plain = Path.Combine(folder, "plain.pdf");
                File.WriteAllBytes(plain, SketchProMax.Tools.PdfFiles.Sample(5));
                check("pdf files: page count", SketchProMax.Tools.PdfFiles.PageCount(File.ReadAllBytes(plain)) == 5, "");
                var txt = Path.Combine(folder, "plain.txt");
                var text = SketchProMax.Tools.PdfFiles.WriteText(plain, txt);
                var written = File.ReadAllText(txt);
                check("pdf files: every page's text, headed by page", text.Error is null && text.Pages == 5 && text.PagesWithText == 5 && !text.MostlyPictures &&
                    written.Contains("=== Page 3 of 5 ===") && written.Contains("Sample page 3") && written.IndexOf("Sample page 2") < written.IndexOf("Sample page 3"), text + written);
                var heavy = Path.Combine(folder, "heavy.pdf");
                File.WriteAllBytes(heavy, SketchProMax.Tools.PdfFiles.Sample(6, noise: 420));
                var size = new FileInfo(heavy).Length;
                var parts = SketchProMax.Tools.PdfFiles.Split(heavy, 1024 * 1024, Path.Combine(folder, "parts"));
                var pagesBack = parts.Sum(p => SketchProMax.Tools.PdfFiles.PageCount(File.ReadAllBytes(p.File)) ?? 0);
                check("pdf files: split into parts under the size, every page once, in order",
                    size > 2 * 1024 * 1024 && parts.Count >= 3 && parts.All(p => p.Bytes <= 1024 * 1024 && new FileInfo(p.File).Length == p.Bytes) &&
                    parts[0].FirstPage == 1 && parts[^1].LastPage == 6 && parts.Zip(parts.Skip(1), (a, b) => b.FirstPage == a.LastPage + 1).All(x => x) && pagesBack == 6 &&
                    Path.GetFileName(parts[0].File).StartsWith("heavy - part 1 (pages 1"),
                    $"{size} bytes: " + string.Join("; ", parts.Select(p => $"{Path.GetFileName(p.File)} {p.Bytes}")));
                var capped = SketchProMax.Tools.PdfFiles.Split(plain, 20 * 1024 * 1024, Path.Combine(folder, "capped"), maxPages: 2);
                check("pdf files: a part keeps to the page limit", capped.Count == 3 && capped.All(p => p.LastPage - p.FirstPage < 2), capped.Count.ToString());
                var ready = SketchProMax.Tools.PdfFiles.ForChat(heavy, Path.Combine(folder, "chat"), 1024 * 1024);
                var manifest = File.ReadAllText(Path.Combine(ready.Folder, "parts.json"));
                var again = SketchProMax.Tools.PdfFiles.ForChat(heavy, Path.Combine(folder, "chat"), 1024 * 1024);
                check("pdf files: a large PDF made ready for the chat: parts, their pages in parts.json, its text, reused when attached again",
                    ready.Parts.Count >= 3 && ready.Pages == 6 && ready.TextFile is not null && File.ReadAllText(ready.TextFile).Contains("Sample page 6") &&
                    manifest.Contains("\"source\":\"heavy.pdf\"") && manifest.Contains("\"pages\":6") && manifest.Contains("\"first\":1") &&
                    again.Folder == ready.Folder && again.Parts.Count == ready.Parts.Count && again.TextFile == ready.TextFile, manifest);
                var note = PdfAttachment.PartsNote(PdfAttachment.Assess(File.ReadAllBytes(heavy), usedBytes: PdfAttachment.MaxBytes),
                    ready.Parts.Select(p => (p.File, p.FirstPage, p.LastPage)).ToList(), ready.Pages, ready.TextFile);
                check("pdf: the parts note lists each part's pages and file and says to ask them with read_pdf_part",
                    note.Contains("Part 1: pages 1-") && note.Contains(ready.Parts[^1].File) && note.Contains("read_pdf_part") && note.Contains(ready.TextFile!) &&
                    note.Contains("every part"), note);
                var junk = Path.Combine(folder, "junk.pdf");
                File.WriteAllText(junk, "not a pdf");
                var junkRefused = false;
                try { SketchProMax.Tools.PdfFiles.Split(junk, 1024 * 1024, Path.Combine(folder, "junk")); } catch (ArgumentException) { junkRefused = true; }
                check("pdf files: a damaged PDF is refused with a readable error, and its text says why", junkRefused && SketchProMax.Tools.PdfFiles.WriteText(junk, txt).Error is not null, "");
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        }
        {
            var doc = OfficeText.Read(OfficeText.Sample(word: true), word: true).Text;
            check("word: headings, paragraphs, bullets and tables, without deleted text",
                doc.Contains("# Concrete works") && doc.Contains("Slabs are C40.") && !doc.Contains("C30") && doc.Contains("- Cover 40 mm") &&
                doc.Contains("| Element | Grade |") && doc.Contains("| Columns | C50 |"), doc);
            var slides = OfficeText.Read(OfficeText.Sample(word: false), word: false).Text;
            check("powerpoint: slides in order with their text",
                slides.Contains("=== Slide 1 of 2 ===") && slides.Contains("Slide 2 title") && slides.Contains("Point 2") &&
                slides.IndexOf("Slide 1 title") < slides.IndexOf("Slide 2 title"), slides);
            var oldRefused = false;
            try { OfficeText.Read(Encoding.ASCII.GetBytes("old binary .doc"), word: true); } catch (InvalidDataException) { oldRefused = true; }
            check("word: an old .doc or damaged file is refused with a readable reason", oldRefused, "");
            var book = OfficeText.Read(OfficeText.SampleWorkbook(), ".xlsx").Text;
            check("excel: sheets in order as tables with row numbers and column letters",
                book.Contains("=== Sheet 1 of 2: Piles ===") && book.Contains("| Row | A | B | C | D | E | F |") &&
                book.Contains("| 1 | Mark | Diameter (m) | Cast | Done |  |  |") && book.IndexOf("Piles") < book.IndexOf("Notes"), book);
            check("excel: shared and rich text, numbers without binary noise, dates, percents, formula results and TRUE",
                book.Contains("| 2 | P-01 | 0.3 | 2025-01-01 | 85% |  |  |") && book.Contains("| 4 | P-01b | 0.6 |  | TRUE |  | far / away |"), book);
            check("excel: empty rows left out, hidden sheets named as hidden", !book.Contains("| 3 |") && book.Contains("=== Sheet 2 of 2: Notes (hidden) ===") && book.Contains("Check toe levels"), book);
            var xlsRefused = false;
            try { OfficeText.Read(Encoding.ASCII.GetBytes("old binary .xls"), ".xlsx"); } catch (InvalidDataException) { xlsRefused = true; }
            check("excel: an old .xls or damaged file is refused with a readable reason", xlsRefused, "");
        }
        using (var doc = JsonDocument.Parse(StreamInput.Interrupt("r1")))
            check("input: interrupt request", doc.RootElement.GetProperty("request").GetProperty("subtype").GetString() == "interrupt", "");

        // Markdown.
        var md = Markdown.Parse("""
            # Title
            Some **bold** and *italic* text with `code` and a [link](https://example.com).
            second line

            - one
            - two
              - nested
            1. first
            2. second

            | Level | Elevation |
            |:------|----------:|
            | L1 | 0 |
            | L2 | 3000 |

            ```csharp
            var x = 1;
            ```
            > quoted
            ---
            ```
            still streaming
            """);
        var kinds = string.Join(",", md.Select(b => b.GetType().Name));
        check("markdown: block kinds", kinds == "Heading,Paragraph,ListBlock,Table,Code,Quote,Rule,Code", kinds);
        var list = md.OfType<Markdown.ListBlock>().First().Items;
        check("markdown: list depth and numbering",
            list.Count == 5 && list[2].Depth == 1 && list[3].Ordered && list[4].Number == 2 && !list[0].Ordered,
            string.Join(" | ", list));
        var table = md.OfType<Markdown.Table>().First();
        check("markdown: table cells and alignment", table.Header.Count == 2 && table.Rows.Count == 2 && table.Rows[1][1] == "3000" && table.Align[1] == 'r', "");
        check("markdown: open fence runs to the end", md.Last() is Markdown.Code { Text: "still streaming" }, md.Last().ToString());
        var spans = Markdown.Inline("a **b** *c* `d` [e](https://x.y) snake_case_name https://q.r/s.");
        check("markdown: inline styles",
            spans.Any(s => s.Text == "b" && s.Style == Markdown.Style.Bold) && spans.Any(s => s.Text == "c" && s.Style == Markdown.Style.Italic) &&
            spans.Any(s => s.Text == "d" && s.Style == Markdown.Style.Code) && spans.Any(s => s.Text == "e" && s.Link == "https://x.y") &&
            spans.Any(s => s.Text.Contains("snake_case_name") && s.Style == Markdown.Style.None) && spans.Any(s => s.Link == "https://q.r/s"),
            string.Join(" | ", spans));
        check("markdown: half-typed emphasis stays plain", Markdown.Inline("**bol").Single().Text == "**bol", "");
    }
}
