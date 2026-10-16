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

        // Which chat calls Revit always asks about.
        Dictionary<string, JsonElement> Args(string json) =>
            JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        check("chat approval: code tools", SketchProMax.Tools.OutputPaths.ChatApprovalReason("run_revit_query_code", Args("{}")) is not null &&
                                          SketchProMax.Tools.OutputPaths.ChatApprovalReason("run_recipe", Args("""{"name":"x"}""")) is not null, "");
        check("chat approval: a folder Claude chose",
            SketchProMax.Tools.OutputPaths.ChatApprovalReason("export_project_pack", Args("""{"folder":"C:\\x"}"""))?.Contains(@"C:\x") == true, "");
        check("chat approval: defaults need none",
            SketchProMax.Tools.OutputPaths.ChatApprovalReason("export_project_pack", Args("""{"include_chat":true,"folder":""}""")) is null &&
            SketchProMax.Tools.OutputPaths.ChatApprovalReason("import_cad", Args("""{"path":"C:\\a.dwg"}""")) is null &&
            SketchProMax.Tools.OutputPaths.ChatApprovalReason("save_legend_seed", Args("""{"path":"C:\\a.rvt"}""")) is not null, "");

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
