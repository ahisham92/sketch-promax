using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SketchProMax.Bridge;
using SketchProMax.Chat;

/// <summary>
/// The chat's connection inside Revit: HTTP requests read off the wire, MCP's JSON-RPC answers, the tool list's order,
/// and a real round trip on 127.0.0.1 (token, session, job header, event stream with keep-alives, cancelling). Plus the
/// chat's note when a PC blocks the helper program.
/// </summary>
internal static class ChatMcpTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Requests(check);
        Endpoint(check);
        Tools(check);
        RoundTrip(check).GetAwaiter().GetResult();
        Notes(check);
    }

    private static HttpRequest? Read(string raw) =>
        HttpRequest.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw)), CancellationToken.None).GetAwaiter().GetResult();

    private static int Refused(string raw)
    {
        try { Read(raw); return 0; }
        catch (HttpRequest.Refused r) { return r.Status; }
    }

    private static void Requests(Action<string, bool, string> check)
    {
        var post = Read("POST /mcp?x=1 HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: 13\r\nAccept: a\r\naccept: b\r\n\r\n{\"jsonrpc\":1}");
        check("chat mcp http: a POST with Content-Length", post is { Method: "POST", Path: "/mcp" } && Encoding.UTF8.GetString(post.Body) == "{\"jsonrpc\":1}"
            && post.Header("content-type") == "application/json" && post.Header("Accept") == "a, b", post?.Path ?? "null");
        var chunked = Read("POST /mcp HTTP/1.1\nTransfer-Encoding: chunked\n\n4\r\n{\"a\"\r\n3;ext=1\r\n:1}\r\n0\r\n\r\n");
        check("chat mcp http: a chunked body, and lines ending in LF alone", chunked is not null && Encoding.UTF8.GetString(chunked.Body) == "{\"a\":1}",
            chunked is null ? "null" : Encoding.UTF8.GetString(chunked.Body));
        check("chat mcp http: a closed connection is no request", Read("") is null, "");
        check("chat mcp http: bad requests are refused with their status",
            Refused("GARBAGE\r\n\r\n") == 400 && Refused("POST /mcp HTTP/1.1\r\nContent-Length: 10\r\n\r\n{}") == 400
            && Refused("POST /mcp HTTP/1.1\r\nContent-Length: 999999999\r\n\r\n") == 413
            && Refused("GET / HTTP/1.1\r\nX: " + new string('a', HttpRequest.MaxHead + 10) + "\r\n\r\n") == 431,
            $"{Refused("GARBAGE\r\n\r\n")} {Refused("POST /mcp HTTP/1.1\r\nContent-Length: 999999999\r\n\r\n")}");
        var answer = Encoding.ASCII.GetString(HttpRequest.Response(202, [], null));
        check("chat mcp http: every answer closes the connection", answer.StartsWith("HTTP/1.1 202 Accepted\r\n") && answer.Contains("Content-Length: 0\r\n")
            && answer.EndsWith("Connection: close\r\n\r\n"), answer);
    }

    private static McpEndpoint FakeEndpoint(List<string> calls, Func<string, CancellationToken, Task<ConnectorResult>>? slow = null) => new(
        "sketch-promax", "1.2.3", () => "Be careful.",
        session => ConnectorTools.Compose([new ConnectorTools.Def("place_rooms", "Places rooms.", JsonDocument.Parse("""{"type":"object"}""").RootElement)], "/no/such/folder"),
        async (session, name, args, ct) =>
        {
            lock (calls) calls.Add($"{name}|{session.Job}|{session.Model}|{(args is not null && args.TryGetValue("x", out var x) ? x.ToString() : "")}");
            if (slow is not null) return await slow(name, ct);
            return name == "capture" ? new ConnectorResult("a picture", false, "iVBORw0KGgo=") : new ConnectorResult("done " + name, name == "broken");
        });

    private static JsonElement Answer(McpEndpoint endpoint, string message, McpSession? session = null, bool listChanged = false)
    {
        using var doc = JsonDocument.Parse(message);
        var outcome = endpoint.HandleAsync(doc.RootElement, session ?? new McpSession("s", null, null), CancellationToken.None).GetAwaiter().GetResult();
        if (outcome.ListChanged != listChanged) throw new InvalidOperationException("list changed: " + outcome.ListChanged);
        return JsonDocument.Parse(outcome.Response).RootElement.Clone();
    }

    private static void Endpoint(Action<string, bool, string> check)
    {
        var calls = new List<string>();
        var endpoint = FakeEndpoint(calls);
        var init = Answer(endpoint, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}""");
        var result = init.GetProperty("result");
        check("chat mcp: initialize answers the client's version, the tools capability and the instructions",
            init.GetProperty("id").GetInt32() == 1 && result.GetProperty("protocolVersion").GetString() == "2025-06-18"
            && result.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean()
            && result.GetProperty("serverInfo").GetProperty("name").GetString() == "sketch-promax" && result.GetProperty("instructions").GetString() == "Be careful.", init.ToString());
        var future = Answer(endpoint, """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");
        check("chat mcp: an unknown version gets the newest one here", future.GetProperty("id").GetString() == "a"
            && future.GetProperty("result").GetProperty("protocolVersion").GetString() == McpEndpoint.Versions[0], future.ToString());
        check("chat mcp: ping", Answer(endpoint, """{"jsonrpc":"2.0","id":2,"method":"ping"}""").GetProperty("result").ValueKind == JsonValueKind.Object, "");
        var list = Answer(endpoint, """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""").GetProperty("result").GetProperty("tools");
        var names = list.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        check("chat mcp: tools/list has the connector's tools, each with its schema",
            names.SequenceEqual(["list_revit_models", "use_revit_model", "wait_for_job", "cancel_job", "place_rooms", "read_pdf_part"])
            && list.EnumerateArray().All(t => t.GetProperty("inputSchema").GetProperty("type").GetString() == "object"), string.Join(",", names));
        var session = new McpSession("s", "job1", "sonnet");
        var call = Answer(endpoint, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"capture","arguments":{"x":5}}}""", session);
        var content = call.GetProperty("result").GetProperty("content");
        check("chat mcp: tools/call passes the session's job and model, and answers text and picture",
            calls.Last() == "capture|job1|sonnet|5" && content[0].GetProperty("text").GetString() == "a picture"
            && content[1].GetProperty("type").GetString() == "image" && content[1].GetProperty("mimeType").GetString() == "image/png"
            && !call.GetProperty("result").GetProperty("isError").GetBoolean(), call.ToString());
        var failed = Answer(endpoint, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"broken"}}""");
        check("chat mcp: a failed call is an error result, not a protocol error", failed.GetProperty("result").GetProperty("isError").GetBoolean(), failed.ToString());
        Answer(endpoint, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"save_as_tool","arguments":{}}}""", listChanged: true);
        check("chat mcp: saving a learned tool tells the client the list changed", true, "");
        var unknown = Answer(endpoint, """{"jsonrpc":"2.0","id":7,"method":"resources/list"}""");
        check("chat mcp: an unknown method is a JSON-RPC error", unknown.GetProperty("error").GetProperty("code").GetInt32() == -32601, unknown.ToString());
    }

    private static void Tools(Action<string, bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "spm-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "learned"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "bad.json"), "{nope");
            check("connector tools: no catalogue yet is null, a damaged one is empty",
                ConnectorTools.ReadCatalogue(Path.Combine(folder, "tools.json")) is null
                && ConnectorTools.ReadCatalogue(Path.Combine(folder, "bad.json")) is { Count: 0 }, "");
            File.WriteAllText(Path.Combine(folder, "tools.json"), """
                [{"name":"wait_for_job","description":"Revit's own","inputSchema":{"type":"object"}},
                 {"name":"tag_walls","description":"Tags walls.","inputSchema":{"type":"object"}},
                 {"name":"read_pdf_part","description":"Revit's","inputSchema":{"type":"object"}}]
                """);
            File.WriteAllText(Path.Combine(folder, "learned", "b.json"), """{"name":"my_tool","description":"Mine.","inputSchema":{"type":"object"}}""");
            File.WriteAllText(Path.Combine(folder, "learned", "a.json"), """{"name":"tag_walls","description":"Taken.","inputSchema":{"type":"object"}}""");
            var tools = ConnectorTools.Compose(ConnectorTools.ReadCatalogue(Path.Combine(folder, "tools.json"))!, Path.Combine(folder, "learned"));
            check("connector tools: connection and job tools first (Revit's own job tool kept), learned tools last, no name twice",
                tools.Select(t => t.Name).SequenceEqual(["list_revit_models", "use_revit_model", "wait_for_job", "cancel_job", "tag_walls", "read_pdf_part", "my_tool"])
                && tools[2].Description == "Revit's own" && tools.Count(t => t.Name == "read_pdf_part") == 1, string.Join(",", tools.Select(t => t.Name)));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task RoundTrip(Action<string, bool, string> check)
    {
        var calls = new List<string>();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new McpHttpServer(FakeEndpoint(calls, async (name, ct) =>
        {
            if (name != "slow" && name != "hang") return new ConnectorResult("done " + name, false);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                if (name == "slow") await Task.WhenAny(release.Task, Task.Delay(10000, ct));
                else await Task.Delay(Timeout.Infinite, ct);
            }
            return new ConnectorResult("finally", false);
        })) { KeepAliveMs = 50 };
        server.Start();
        check("chat mcp server: listens on 127.0.0.1 with a long random token", server.Port > 0 && server.Url == $"http://127.0.0.1:{server.Port}/mcp"
            && server.Token.Length == 64 && server.Token != new McpHttpServer(FakeEndpoint(calls)).Token, server.Url);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        HttpRequestMessage Post(string body, bool token = true, string? session = null, string accept = "application/json, text/event-stream", string? job = null)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (token) m.Headers.TryAddWithoutValidation("Authorization", "Bearer " + server.Token);
            if (session is not null) m.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
            if (job is not null) m.Headers.TryAddWithoutValidation(McpHttpServer.JobHeader, job);
            m.Headers.TryAddWithoutValidation("Accept", accept);
            return m;
        }
        const string init = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

        var anonymous = await http.SendAsync(Post(init, token: false));
        var wrong = Post(init, token: false);
        wrong.Headers.TryAddWithoutValidation("Authorization", "Bearer " + new string('0', 64));
        var wrongAnswer = await http.SendAsync(wrong);
        var browser = Post(init);
        browser.Headers.TryAddWithoutValidation("Origin", "https://example.com");
        var browserAnswer = await http.SendAsync(browser);
        var get = new HttpRequestMessage(HttpMethod.Get, server.Url);
        get.Headers.TryAddWithoutValidation("Authorization", "Bearer " + server.Token);
        var getAnswer = await http.SendAsync(get);
        var elsewhere = await http.GetAsync($"http://127.0.0.1:{server.Port}/other");
        check("chat mcp server: no token or a wrong one is 401, a browser's request 403, GET 405, another path 404",
            anonymous.StatusCode == HttpStatusCode.Unauthorized && wrongAnswer.StatusCode == HttpStatusCode.Unauthorized && browserAnswer.StatusCode == HttpStatusCode.Forbidden
            && getAnswer.StatusCode == HttpStatusCode.MethodNotAllowed && elsewhere.StatusCode == HttpStatusCode.NotFound,
            $"{anonymous.StatusCode} {wrongAnswer.StatusCode} {browserAnswer.StatusCode} {getAnswer.StatusCode} {elsewhere.StatusCode}");

        var started = await http.SendAsync(Post(init, job: "job7"));
        var session = started.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.First() : "";
        var startedJson = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement;
        check("chat mcp server: initialize over HTTP gives JSON and a session id", started.StatusCode == HttpStatusCode.OK && session.Length > 0
            && started.Content.Headers.ContentType?.MediaType == "application/json" && startedJson.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25",
            started.StatusCode.ToString());
        var note = await http.SendAsync(Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", session: session));
        check("chat mcp server: a notification is accepted with 202", note.StatusCode == HttpStatusCode.Accepted, note.StatusCode.ToString());

        var list = await http.SendAsync(Post("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", session: session));
        var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        check("chat mcp server: tools/list over HTTP", listJson.GetProperty("result").GetProperty("tools").GetArrayLength() == 6, listJson.ToString());

        var plain = await http.SendAsync(Post("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"quick","arguments":{"x":1}}}""", session: session, accept: "application/json"));
        var plainJson = JsonDocument.Parse(await plain.Content.ReadAsStringAsync()).RootElement;
        check("chat mcp server: a call from a client without event streams is answered as JSON, with the session's job",
            plainJson.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() == "done quick" && calls.Last() == "quick|job7||1", calls.LastOrDefault() ?? "");

        // A long call: the event stream starts at once and keeps the connection alive until the result.
        var slowCall = http.SendAsync(Post("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"slow"}}""", session: session), HttpCompletionOption.ResponseHeadersRead);
        var slow = await slowCall;
        var reading = slow.Content.ReadAsStringAsync();
        await Task.Delay(300);
        release.TrySetResult(true);
        var events = await reading;
        var data = events.Split('\n').Where(l => l.StartsWith("data: ")).Select(l => JsonDocument.Parse(l.Substring(6)).RootElement).ToList();
        check("chat mcp server: a long call streams keep-alives, then its answer",
            slow.Content.Headers.ContentType?.MediaType == "text/event-stream" && events.Contains(": keep-alive") && data.Count == 1
            && data[0].GetProperty("id").GetInt32() == 3 && data[0].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() == "finally", events);

        var saved = await (await http.SendAsync(Post("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"save_as_tool"}}""", session: session))).Content.ReadAsStringAsync();
        var savedData = saved.Split('\n').Where(l => l.StartsWith("data: ")).ToList();
        check("chat mcp server: saving a learned tool sends list_changed before the answer", savedData.Count == 2
            && savedData[0].Contains("notifications/tools/list_changed") && savedData[1].Contains("\"id\":4"), saved);

        // Claude Code cancels a call (Stop): the call's work is cancelled.
        cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = http.SendAsync(Post("""{"jsonrpc":"2.0","id":"h1","method":"tools/call","params":{"name":"hang"}}""", session: session), HttpCompletionOption.ResponseHeadersRead);
        await Task.Delay(200);
        await http.SendAsync(Post("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"h1","reason":"stop"}}""", session: session));
        var byNotice = await Task.WhenAny(cancelled.Task, Task.Delay(5000)) == cancelled.Task;
        try { using var r = await hanging; await r.Content.ReadAsStringAsync(); } catch (Exception) { }
        check("chat mcp server: notifications/cancelled cancels the call", byNotice, "");

        // The caller goes away mid-call: the call is cancelled too.
        cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, server.Port);
            var body = """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"hang"}}""";
            var request = $"POST /mcp HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {server.Token}\r\nAccept: text/event-stream\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            var bytes = Encoding.UTF8.GetBytes(request);
            await raw.GetStream().WriteAsync(bytes);
            await Task.Delay(200);
        }
        var byClose = await Task.WhenAny(cancelled.Task, Task.Delay(5000)) == cancelled.Task;
        check("chat mcp server: a caller that disconnects cancels the call", byClose, "");

        server.Dispose();
        var refused = false;
        try { await http.SendAsync(Post(init)); }
        catch (HttpRequestException) { refused = true; }
        check("chat mcp server: stopped, it no longer answers", refused, "");
    }

    private static void Notes(Action<string, bool, string> check)
    {
        check("tools note: a blocked start is recognised, a missing file is not",
            ToolsNote.IsBlocked("Connection failed (EPERM): spawn EPERM: operation not permitted") && ToolsNote.IsBlocked("This program is blocked by group policy.")
            && ToolsNote.IsBlocked("Access is denied") && !ToolsNote.IsBlocked("spawn ENOENT: no such file") && !ToolsNote.IsBlocked(null) && !ToolsNote.IsBlocked(""), "");
        var path = @"C:\Users\x\AppData\Local\SketchProMax\mcp\SketchProMax.Mcp.exe";
        var blocked = ToolsNote.Failed("failed", inside: false, path, blocked: true);
        check("tools note: a blocked helper program is named, with what to ask IT, not Install.cmd",
            blocked.StartsWith($"Your company blocks Sketch ProMax's helper program ({path}). Ask IT to allow it, or") && !blocked.Contains("Install.cmd"), blocked);
        var inside = ToolsNote.Failed("failed", inside: true, "http://127.0.0.1:5000/mcp", blocked: false);
        check("tools note: the connection inside Revit doesn't send the user to Install.cmd", !inside.Contains("Install.cmd") && inside.Contains("(failed)")
            && !ToolsNote.Missing(inside: true).Contains("Install.cmd") && ToolsNote.Missing(inside: false).Contains("Install.cmd"), inside);
        check("tools note: the fallback that isn't blocked still points at the install", ToolsNote.Failed("failed", false, path, false).Contains("Install.cmd")
            && ToolsNote.Failed("failed", false, path, false, job: true).EndsWith("send the job a message."), "");
        var all = new[] { blocked, inside, ToolsNote.Missing(true), ToolsNote.Missing(false, true), ToolsNote.Failed("x", true, "u", true, true) };
        check("tools note: Sketch ProMax wording", all.All(t => !t.Contains("Claude")), string.Join(" | ", all));
    }
}
