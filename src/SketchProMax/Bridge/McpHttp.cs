using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Bridge;

/// <summary>
/// The MCP server the chat's Claude Code talks to, inside Revit: MCP's Streamable HTTP transport on this PC's own address
/// (127.0.0.1, a free port), so the chat needs no helper program (a company PC may block starting one). Small on purpose:
/// one HTTP/1.1 request per connection, POST /mcp with one JSON-RPC message, answered as JSON, or as an event stream for
/// tool calls, which may wait a long time (approvals, a job's turn): the stream sends a keep-alive line every few seconds
/// so nothing between gives up on a quiet connection. Every request needs the bearer token this server made at start; the
/// chat's MCP config carries it. No Revit here: ChatMcp gives it the tools and the calls, and the unit tests run it.
/// </summary>
internal sealed class McpHttpServer : IDisposable
{
    /// <summary>The header a parallel job's config adds, naming the job (its changes take turns with the other jobs').</summary>
    public const string JobHeader = "X-SketchProMax-Job";

    /// <summary>The header naming the chat's model, which read_pdf_part reads a part with.</summary>
    public const string ModelHeader = "X-SketchProMax-Model";

    private readonly McpEndpoint _endpoint;
    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    /// <summary>How often a waiting tool call's event stream sends a keep-alive line.</summary>
    internal int KeepAliveMs { get; set; } = 15000;

    public McpHttpServer(McpEndpoint endpoint)
    {
        _endpoint = endpoint;
        var bytes = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        Token = string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    /// <summary>The secret every request must carry (Authorization: Bearer), new for each server.</summary>
    public string Token { get; }

    public int Port { get; private set; }

    public string Url => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>Listens on 127.0.0.1 (a free port when 0). Throws SocketException when it can't.</summary>
    public void Start(int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoop(listener, _stop.Token));
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (SocketException) { }
        foreach (var session in _sessions.Values) session.CancelAll();
        _sessions.Clear();
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Out of sockets or similar: keep listening rather than go quiet for good.
                try { await Task.Delay(500, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Task.Run(() => ServeAsync(client, stop));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                // Only this PC: the listener is on 127.0.0.1, and anything else is turned away here as well.
                if (client.Client.RemoteEndPoint is not IPEndPoint { } remote || !IPAddress.IsLoopback(remote.Address)) return;
                var stream = client.GetStream();
                HttpRequest? request;
                using (var head = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    // A connection that doesn't send its request in time is closed.
                    head.CancelAfter(30000);
                    using (head.Token.Register(() => { try { client.Close(); } catch (Exception) { } }))
                    {
                        try { request = await HttpRequest.ReadAsync(stream, head.Token).ConfigureAwait(false); }
                        catch (HttpRequest.Refused refused)
                        {
                            await WriteAsync(stream, HttpRequest.Response(refused.Status, Json(new { error = refused.Message }), "application/json")).ConfigureAwait(false);
                            return;
                        }
                    }
                }
                if (request is null) return;
                await RouteAsync(request, stream, stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException) { }
            catch (Exception) { /* one bad request must never take the server down */ }
        }
    }

    private async Task RouteAsync(HttpRequest request, NetworkStream stream, CancellationToken stop)
    {
        if (request.Path != "/mcp")
        {
            await WriteAsync(stream, HttpRequest.Response(404, Json(new { error = "not found" }), "application/json")).ConfigureAwait(false);
            return;
        }
        // A web page in a browser sends its Origin; only Claude Code, which sends none, may talk to Revit.
        if (request.Header("Origin") is { Length: > 0 })
        {
            await WriteAsync(stream, HttpRequest.Response(403, Json(new { error = "forbidden" }), "application/json")).ConfigureAwait(false);
            return;
        }
        if (!Authorized(request.Header("Authorization")))
        {
            await WriteAsync(stream, HttpRequest.Response(401, Json(new { error = "unauthorized" }), "application/json")).ConfigureAwait(false);
            return;
        }
        switch (request.Method)
        {
            case "POST":
                break;
            case "DELETE":
                // The client ends its session.
                if (request.Header("Mcp-Session-Id") is { } ended && _sessions.TryRemove(ended, out var gone)) gone.CancelAll();
                await WriteAsync(stream, HttpRequest.Response(200, [], null)).ConfigureAwait(false);
                return;
            default:
                // No stream of server messages of its own (GET): what the server has to say goes with each call's answer.
                await WriteAsync(stream, HttpRequest.Response(405, [], null, new Dictionary<string, string> { ["Allow"] = "POST, DELETE" })).ConfigureAwait(false);
                return;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(request.Body); }
        catch (JsonException)
        {
            await WriteAsync(stream, HttpRequest.Response(400, McpEndpoint.Error(null, -32700, "Parse error"), "application/json")).ConfigureAwait(false);
            return;
        }
        using (doc)
        {
            var message = doc.RootElement;
            if (message.ValueKind != JsonValueKind.Object)
            {
                await WriteAsync(stream, HttpRequest.Response(400, McpEndpoint.Error(null, -32600, "Send one JSON-RPC message per request"), "application/json")).ConfigureAwait(false);
                return;
            }
            var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
            var session = SessionFor(request, method == "initialize");
            if (method is null || !hasId)
            {
                // A notification (initialized, cancelled) or a client's answer: accepted, nothing to send back.
                if (method == "notifications/cancelled" && message.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("requestId", out var cancelled))
                    session.Cancel(cancelled.GetRawText());
                await WriteAsync(stream, HttpRequest.Response(202, [], null)).ConfigureAwait(false);
                return;
            }

            var headers = new Dictionary<string, string> { ["Mcp-Session-Id"] = session.Id };
            using var call = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var key = id.GetRawText();
            session.Track(key, call);
            try
            {
                var streamed = method == "tools/call" && (request.Header("Accept") ?? "").IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
                if (streamed)
                {
                    await WriteAsync(stream, HttpRequest.StreamHead(headers)).ConfigureAwait(false);
                }
                var work = _endpoint.HandleAsync(message, session, call.Token);
                // The caller went away (Claude Code stopped the call, or ended): the call is cancelled, so work still
                // waiting for Revit is dropped.
                WatchForDisconnect(stream, call);
                while (!work.IsCompleted)
                {
                    if (await Task.WhenAny(work, Task.Delay(KeepAliveMs, stop)).ConfigureAwait(false) == work) break;
                    if (stop.IsCancellationRequested) return; // the server stopped (Revit closing, or a new core)
                    if (streamed) await WriteAsync(stream, Encoding.UTF8.GetBytes(": keep-alive\n\n")).ConfigureAwait(false);
                }
                var outcome = await work.ConfigureAwait(false);
                if (!streamed)
                {
                    await WriteAsync(stream, HttpRequest.Response(200, Encoding.UTF8.GetBytes(outcome.Response), "application/json", headers)).ConfigureAwait(false);
                    return;
                }
                var events = new StringBuilder();
                // A learned tool was saved or deleted: the client reads the tool list again.
                if (outcome.ListChanged) events.Append("event: message\ndata: ").Append(McpEndpoint.ListChangedNotification).Append("\n\n");
                events.Append("event: message\ndata: ").Append(outcome.Response).Append("\n\n");
                await WriteAsync(stream, Encoding.UTF8.GetBytes(events.ToString())).ConfigureAwait(false);
            }
            finally
            {
                session.Untrack(key, call);
                try { call.Cancel(); } catch (ObjectDisposedException) { } // ends the disconnect watch's wait
            }
        }
    }

    /// <summary>
    /// The client sends its whole request and then only reads (each answer closes the connection), so a read that ends
    /// means it went away: the call is cancelled then.
    /// </summary>
    private static void WatchForDisconnect(NetworkStream stream, CancellationTokenSource call)
    {
        var buffer = new byte[1];
        Task<int> read;
        try { read = stream.ReadAsync(buffer, 0, 1, call.Token); }
        catch (Exception) { return; }
        read.ContinueWith(t =>
        {
            if (t.IsCanceled || (t.Status == TaskStatus.RanToCompletion && t.Result > 0)) return;
            _ = t.Exception; // observed
            try { call.Cancel(); } catch (ObjectDisposedException) { }
        }, TaskScheduler.Default);
    }

    private McpSession SessionFor(HttpRequest request, bool initialize)
    {
        var job = request.Header(JobHeader) is { Length: > 0 } j ? j : null;
        var model = request.Header(ModelHeader) is { Length: > 0 } m ? m : null;
        // A new session at each initialize. A request naming a session this server doesn't know gets one under that id,
        // rather than an error the client might not recover from; one naming none shares one per job.
        var id = initialize ? Guid.NewGuid().ToString("N")
            : request.Header("Mcp-Session-Id") is { Length: > 0 } given ? given : "shared:" + job + ":" + model;
        var session = _sessions.GetOrAdd(id, key => new McpSession(key, job, model));
        session.LastUsed = DateTime.UtcNow;
        if (_sessions.Count > 200)
        {
            // Claude Code sessions that ended without saying so: the oldest idle ones go.
            foreach (var old in _sessions.Values.Where(s => s.Idle && s != session).OrderBy(s => s.LastUsed).Take(_sessions.Count - 150).ToList())
                _sessions.TryRemove(old.Id, out _);
        }
        return session;
    }

    private bool Authorized(string? header)
    {
        const string scheme = "Bearer ";
        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var given = header.Substring(scheme.Length).Trim();
        // Compared in constant time, so the answer's timing says nothing about the token.
        var diff = given.Length ^ Token.Length;
        for (var i = 0; i < Math.Min(given.Length, Token.Length); i++) diff |= given[i] ^ Token[i];
        return diff == 0;
    }

    private static async Task WriteAsync(Stream stream, byte[] bytes)
    {
        await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
}

/// <summary>One Claude Code session on the chat's connection: its job and model, its tool calls in flight, and the caller's own state.</summary>
internal sealed class McpSession
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _calls = new(StringComparer.Ordinal);

    public McpSession(string id, string? job, string? model)
    {
        Id = id;
        Job = job;
        Model = model;
    }

    public string Id { get; }

    /// <summary>The chat's parallel job this session is (from its config's header), or null for the main chat.</summary>
    public string? Job { get; }

    public string? Model { get; }

    public DateTime LastUsed { get; set; } = DateTime.UtcNow;

    /// <summary>What the caller keeps per session (ChatMcp: the Revit window and model Claude works on).</summary>
    public object? State { get; set; }

    public bool Idle => _calls.IsEmpty;

    internal void Track(string id, CancellationTokenSource call) => _calls[id] = call;

    internal void Untrack(string id, CancellationTokenSource call) => ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_calls).Remove(new(id, call));

    /// <summary>The client cancelled a call (notifications/cancelled, with the call's id as JSON).</summary>
    internal void Cancel(string id)
    {
        if (_calls.TryGetValue(id, out var call))
            try { call.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal void CancelAll()
    {
        foreach (var call in _calls.Values)
            try { call.Cancel(); } catch (ObjectDisposedException) { }
    }
}

/// <summary>
/// MCP's JSON-RPC messages, without the transport: initialize, ping, tools/list and tools/call. Answers the protocol
/// version the client asks for when it is one of these, otherwise the newest here.
/// </summary>
internal sealed class McpEndpoint
{
    public static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    public const string ListChangedNotification = """{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""";

    private readonly string _name, _version;
    private readonly Func<string> _instructions;
    private readonly Func<McpSession, IReadOnlyList<ConnectorTools.Def>> _tools;
    private readonly Func<McpSession, string, IDictionary<string, JsonElement>?, CancellationToken, Task<ConnectorResult>> _call;

    public McpEndpoint(string name, string version, Func<string> instructions, Func<McpSession, IReadOnlyList<ConnectorTools.Def>> tools,
        Func<McpSession, string, IDictionary<string, JsonElement>?, CancellationToken, Task<ConnectorResult>> call)
    {
        _name = name;
        _version = version;
        _instructions = instructions;
        _tools = tools;
        _call = call;
    }

    /// <summary>The answer to one request (a JSON-RPC response), and whether the tool list changed with it.</summary>
    internal readonly record struct Outcome(string Response, bool ListChanged = false);

    public async Task<Outcome> HandleAsync(JsonElement message, McpSession session, CancellationToken ct)
    {
        var id = message.GetProperty("id");
        var method = message.GetProperty("method").GetString();
        var p = message.TryGetProperty("params", out var given) && given.ValueKind == JsonValueKind.Object ? given : default;
        switch (method)
        {
            case "initialize":
            {
                var asked = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var version = Versions.Contains(asked) ? asked! : Versions[0];
                return new(Result(id, w =>
                {
                    w.WriteString("protocolVersion", version);
                    w.WriteStartObject("capabilities");
                    // Learned tools can be added mid-chat; the client is told to refresh its list when that happens.
                    w.WriteStartObject("tools");
                    w.WriteBoolean("listChanged", true);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteStartObject("serverInfo");
                    w.WriteString("name", _name);
                    w.WriteString("version", _version);
                    w.WriteEndObject();
                    w.WriteString("instructions", _instructions());
                }));
            }
            case "ping":
                return new(Result(id, _ => { }));
            case "tools/list":
            {
                var tools = _tools(session);
                return new(Result(id, w =>
                {
                    w.WriteStartArray("tools");
                    foreach (var t in tools)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", t.Name);
                        if (t.Description is not null) w.WriteString("description", t.Description);
                        w.WritePropertyName("inputSchema");
                        t.InputSchema.WriteTo(w);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }));
            }
            case "tools/call":
            {
                var name = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                if (name.Length == 0) return new(Encoding.UTF8.GetString(Error(id, -32602, "tools/call needs a tool name")));
                var args = p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
                    : new Dictionary<string, JsonElement>();
                ConnectorResult result;
                try { result = await _call(session, name, args, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = new("The call was cancelled.", true); }
                catch (Exception ex) { result = new($"{ex.GetType().Name}: {ex.Message}", true); }
                return new(Result(id, w =>
                {
                    w.WriteStartArray("content");
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", result.Text);
                    w.WriteEndObject();
                    if (result.PngBase64 is { } png)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "image");
                        w.WriteString("data", png);
                        w.WriteString("mimeType", "image/png");
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteBoolean("isError", result.IsError);
                }), ConnectorTools.ChangesList(name) && !result.IsError);
            }
            default:
                return new(Encoding.UTF8.GetString(Error(id, -32601, $"Method not found: {method}")));
        }
    }

    private static string Result(JsonElement id, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WriteStartObject("result");
            body(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A JSON-RPC error, for the request <paramref name="id"/> (null when it couldn't be read).</summary>
    internal static byte[] Error(JsonElement? id, int code, string message)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id is { } known) known.WriteTo(w);
            else w.WriteNullValue();
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }
}

/// <summary>One HTTP/1.1 request: its line, headers and body (Content-Length or chunked), and the answers this server writes.</summary>
internal sealed class HttpRequest
{
    /// <summary>A request turned away before it was read whole, with the status to answer.</summary>
    internal sealed class Refused(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    public const int MaxHead = 64 * 1024, MaxBody = 64 * 1024 * 1024;

    public string Method { get; private set; } = "";

    /// <summary>The path, without the query.</summary>
    public string Path { get; private set; } = "";

    public byte[] Body { get; private set; } = [];

    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Reads one request; null when the connection closed before one began. Throws <see cref="Refused"/> for a bad one.</summary>
    public static async Task<HttpRequest?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var reader = new Reader(stream);
        var line = await reader.LineAsync(MaxHead, ct).ConfigureAwait(false);
        if (line is null) return null;
        var parts = line.Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new Refused(400, "bad request line");
        var request = new HttpRequest { Method = parts[0], Path = parts[1].Split('?')[0] };
        var headSize = line.Length;
        while (true)
        {
            var header = await reader.LineAsync(MaxHead, ct).ConfigureAwait(false) ?? throw new Refused(400, "the request ended in its headers");
            if (header.Length == 0) break;
            headSize += header.Length;
            if (headSize > MaxHead) throw new Refused(431, "headers too large");
            var colon = header.IndexOf(':');
            if (colon <= 0) throw new Refused(400, "bad header");
            var name = header.Substring(0, colon).Trim();
            var value = header.Substring(colon + 1).Trim();
            request._headers[name] = request._headers.TryGetValue(name, out var earlier) ? earlier + ", " + value : value;
        }
        if ((request.Header("Transfer-Encoding") ?? "").IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var size = await reader.LineAsync(MaxHead, ct).ConfigureAwait(false) ?? throw new Refused(400, "the request ended in its body");
                var hex = size.Split(';')[0].Trim();
                if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0)
                    throw new Refused(400, "bad chunk size");
                if (body.Length + n > MaxBody) throw new Refused(413, "request too large");
                if (n == 0)
                {
                    // Trailers, if any, end with an empty line.
                    while ((await reader.LineAsync(MaxHead, ct).ConfigureAwait(false) ?? "").Length > 0) { }
                    break;
                }
                var chunk = await reader.BytesAsync(n, ct).ConfigureAwait(false);
                body.Write(chunk, 0, chunk.Length);
                await reader.LineAsync(MaxHead, ct).ConfigureAwait(false);
            }
            request.Body = body.ToArray();
        }
        else if (request.Header("Content-Length") is { } length)
        {
            if (!long.TryParse(length, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) throw new Refused(400, "bad Content-Length");
            if (n > MaxBody) throw new Refused(413, "request too large");
            request.Body = await reader.BytesAsync((int)n, ct).ConfigureAwait(false);
        }
        return request;
    }

    /// <summary>A whole answer: status, headers and body. Every answer closes the connection.</summary>
    public static byte[] Response(int status, byte[] body, string? contentType, IDictionary<string, string>? headers = null)
    {
        var head = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\n");
        if (contentType is not null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        foreach (var h in headers ?? new Dictionary<string, string>()) head.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(head.ToString());
        var all = new byte[bytes.Length + body.Length];
        Buffer.BlockCopy(bytes, 0, all, 0, bytes.Length);
        Buffer.BlockCopy(body, 0, all, bytes.Length, body.Length);
        return all;
    }

    /// <summary>The head of an event-stream answer; its events follow, and it ends when the connection closes.</summary>
    public static byte[] StreamHead(IDictionary<string, string> headers)
    {
        var head = new StringBuilder("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n");
        foreach (var h in headers) head.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(head.ToString());
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 413 => "Payload Too Large", 431 => "Request Header Fields Too Large", _ => "Error",
    };

    /// <summary>Buffered reads of lines (ending CRLF or LF) and exact byte counts.</summary>
    private sealed class Reader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[16384];
        private int _start, _end;

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            if (_start > 0 && _start == _end) _start = _end = 0;
            if (_end == _buffer.Length)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            var read = await stream.ReadAsync(_buffer, _end, _buffer.Length - _end, ct).ConfigureAwait(false);
            _end += read;
            return read > 0;
        }

        public async Task<string?> LineAsync(int max, CancellationToken ct)
        {
            var line = new MemoryStream();
            while (true)
            {
                for (var i = _start; i < _end; i++)
                {
                    if (_buffer[i] != (byte)'\n') continue;
                    line.Write(_buffer, _start, i - _start);
                    _start = i + 1;
                    var bytes = line.ToArray();
                    var length = bytes.Length > 0 && bytes[bytes.Length - 1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
                    return Encoding.UTF8.GetString(bytes, 0, length);
                }
                line.Write(_buffer, _start, _end - _start);
                _start = _end;
                if (line.Length > max) throw new Refused(431, "line too long");
                if (!await FillAsync(ct).ConfigureAwait(false)) return line.Length == 0 ? null : throw new Refused(400, "the request ended mid-line");
            }
        }

        public async Task<byte[]> BytesAsync(int count, CancellationToken ct)
        {
            var result = new byte[count];
            var have = 0;
            while (have < count)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false)) throw new Refused(400, "the request ended early");
                var take = Math.Min(count - have, _end - _start);
                Buffer.BlockCopy(_buffer, _start, result, have, take);
                _start += take;
                have += take;
            }
            return result;
        }
    }
}
