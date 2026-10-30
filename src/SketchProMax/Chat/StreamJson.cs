using System.IO;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Chat;

/// <summary>What the chat pane needs from one line of Claude Code's stream-json output.</summary>
internal abstract record ChatEvent;

/// <summary>system/init: the session started (or resumed). Servers: (name, status) of each MCP server.</summary>
internal sealed record InitEvent(string SessionId, string Model, IReadOnlyList<(string Name, string Status)> Servers, IReadOnlyList<string> Capabilities) : ChatEvent;

/// <summary>A streamed text block starts; deltas with the same key follow.</summary>
internal sealed record TextStartEvent(string Key) : ChatEvent;

internal sealed record TextDeltaEvent(string Key, string Text) : ChatEvent;

/// <summary>Text from a complete assistant message whose text was not streamed (no partial messages).</summary>
internal sealed record AssistantTextEvent(string MessageId, string Text) : ChatEvent;

/// <summary>A tool call: from the stream as soon as it starts (input "" then), and again from the complete message.</summary>
internal sealed record ToolUseEvent(string Id, string Name, string InputJson) : ChatEvent;

internal sealed record ToolResultEvent(string ToolUseId, bool IsError, string Text) : ChatEvent;

/// <summary>The model is thinking (a thinking block started).</summary>
internal sealed record ThinkingEvent : ChatEvent;

/// <summary>Text of the thinking block being streamed (its reasoning, or the summary of it, as Claude Code passes it on).</summary>
internal sealed record ThinkingDeltaEvent(string Text) : ChatEvent;

/// <summary>A whole thinking block from a complete assistant message whose thinking was not streamed.</summary>
internal sealed record ThinkingTextEvent(string MessageId, string Text) : ChatEvent;

/// <summary>The end of a turn: success, or an error subtype (error_max_turns, error_during_execution, ...).</summary>
/// <remarks><paramref name="CostUsd"/>: the API-equivalent dollars of the whole process so far (Claude Code's total_cost_usd).</remarks>
internal sealed record ResultEvent(string Subtype, bool IsError, string Text, string SessionId, long InputTokens, long OutputTokens, IReadOnlyList<string> Errors, double CostUsd = 0, long ApiMs = 0) : ChatEvent;

/// <summary>system/api_retry: a request failed and is retried.</summary>
internal sealed record RetryEvent(int Attempt, int MaxRetries, string Error, int DelayMs) : ChatEvent;

/// <summary>A system message the user should see (a denied permission, a warning).</summary>
internal sealed record NoticeEvent(string Text) : ChatEvent;

/// <summary>
/// system/compact_boundary: Claude Code summarised the earlier conversation (Trigger "manual" for /compact, "auto" when
/// it was nearly full). PreTokens: how big the conversation was before.
/// </summary>
internal sealed record CompactedEvent(string Trigger, long PreTokens) : ChatEvent;

/// <summary>
/// How much of the context a request of the main conversation used (input, cache read and cache write tokens at
/// message_start): what the next message costs to send again, so the pane knows when to shrink the chat.
/// </summary>
internal sealed record ContextEvent(long Tokens) : ChatEvent;

internal sealed record ControlResponseEvent(string RequestId, bool Ok, string Error) : ChatEvent;

/// <summary>Claude Code's report of the plan's usage limits (a rate_limit_event line): see <see cref="PlanUsage"/>.</summary>
internal sealed record UsageEvent(PlanUsage Usage) : ChatEvent;

/// <summary>
/// Turns Claude Code's stream-json output (claude -p --output-format stream-json --verbose --include-partial-messages)
/// into <see cref="ChatEvent"/>s. Lines that are not JSON, event types it doesn't know and fields that are missing are
/// ignored, so a newer Claude Code that adds events doesn't break the pane. Messages of subagents (parent_tool_use_id
/// set) are left out: the pane shows the main conversation. Not thread-safe: feed it from one reader.
/// </summary>
internal sealed class StreamParser
{
    private readonly StringBuilder _pending = new();
    private readonly HashSet<string> _streamed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _thoughtStreamed = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _blockTool = new();
    private string _messageId = "";
    private int _anonymous;

    /// <summary>Feeds raw output; complete lines are parsed and a partial last line is kept for the next call.</summary>
    public List<ChatEvent> Feed(string chunk)
    {
        var events = new List<ChatEvent>();
        // Only the new chunk is searched for line ends: a long line arriving in many pieces is not rescanned each time.
        var start = 0;
        for (var nl = chunk.IndexOf('\n'); nl >= 0; nl = chunk.IndexOf('\n', start))
        {
            _pending.Append(chunk, start, nl - start);
            var line = _pending.ToString();
            _pending.Clear();
            start = nl + 1;
            events.AddRange(ParseLine(line));
        }
        _pending.Append(chunk, start, chunk.Length - start);
        return events;
    }

    /// <summary>Parses one complete line.</summary>
    public List<ChatEvent> ParseLine(string line)
    {
        var events = new List<ChatEvent>();
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{') return events;
        try
        {
            using var doc = JsonDocument.Parse(line);
            Parse(doc.RootElement, events);
        }
        catch (JsonException) { /* not one of ours (a stray log line): skip */ }
        catch (InvalidOperationException) { /* a field of an unexpected kind: skip the line */ }
        return events;
    }

    private void Parse(JsonElement root, List<ChatEvent> events)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var type = Str(root, "type");
        // Subagents and forked skills: their messages carry the id of the tool call that started them.
        if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String && type != "result") return;
        switch (type)
        {
            case "system":
                SystemEvent(root, events);
                break;
            case "stream_event":
                if (root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object) Stream(ev, events);
                break;
            case "assistant":
                Assistant(root, events);
                break;
            case "user":
                User(root, events);
                break;
            case "result":
                events.Add(Result(root));
                break;
            case "rate_limit_event":
                if (root.TryGetProperty("rate_limit_info", out var limits) && PlanUsage.Parse(limits, DateTimeOffset.UtcNow) is { } usage)
                    events.Add(new UsageEvent(usage));
                break;
            case "control_response":
                if (root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object)
                    events.Add(new ControlResponseEvent(Str(r, "request_id"), Str(r, "subtype") == "success", Str(r, "error")));
                break;
        }
    }

    private static void SystemEvent(JsonElement root, List<ChatEvent> events)
    {
        switch (Str(root, "subtype"))
        {
            case "init":
            {
                var servers = new List<(string, string)>();
                if (root.TryGetProperty("mcp_servers", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var s in list.EnumerateArray())
                        if (s.ValueKind == JsonValueKind.Object) servers.Add((Str(s, "name"), Str(s, "status")));
                var caps = new List<string>();
                if (root.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
                    foreach (var x in c.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.String) caps.Add(x.GetString()!);
                events.Add(new InitEvent(Str(root, "session_id"), Str(root, "model"), servers, caps));
                break;
            }
            case "api_retry":
                events.Add(new RetryEvent(Int(root, "attempt"), Int(root, "max_retries"), Str(root, "error"), Int(root, "retry_delay_ms")));
                break;
            case "compact_boundary":
            {
                var meta = root.TryGetProperty("compact_metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
                events.Add(meta.ValueKind == JsonValueKind.Object
                    ? new CompactedEvent(Str(meta, "trigger"), Long(meta, "pre_tokens"))
                    : new CompactedEvent("", 0));
                break;
            }
            case "permission_denied":
            {
                var tool = Str(root, "tool_name");
                events.Add(new NoticeEvent(tool.Length > 0
                    ? $"Sketch ProMax tried a step the Revit chat doesn't allow ({StepNames.Title(tool)}). It carries on without it."
                    : "Sketch ProMax tried a step the Revit chat doesn't allow. It carries on without it."));
                break;
            }
        }
    }

    private void Stream(JsonElement ev, List<ChatEvent> events)
    {
        switch (Str(ev, "type"))
        {
            case "message_start":
                _messageId = ev.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "id") : "";
                if (_messageId.Length == 0) _messageId = "anon-" + ++_anonymous;
                _blockTool.Clear();
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    var used = Long(usage, "input_tokens") + Long(usage, "cache_read_input_tokens") + Long(usage, "cache_creation_input_tokens");
                    if (used > 0) events.Add(new ContextEvent(used));
                }
                break;
            case "content_block_start":
            {
                if (!ev.TryGetProperty("content_block", out var block) || block.ValueKind != JsonValueKind.Object) break;
                var index = Int(ev, "index");
                switch (Str(block, "type"))
                {
                    case "text":
                        _streamed.Add(_messageId);
                        events.Add(new TextStartEvent(Key(index)));
                        var initial = Str(block, "text");
                        if (initial.Length > 0) events.Add(new TextDeltaEvent(Key(index), initial));
                        break;
                    case "tool_use":
                    case "server_tool_use":
                    case "mcp_tool_use":
                        _blockTool[index] = Str(block, "id");
                        events.Add(new ToolUseEvent(Str(block, "id"), Str(block, "name"), ""));
                        break;
                    case "thinking":
                    case "redacted_thinking":
                        events.Add(new ThinkingEvent());
                        break;
                }
                break;
            }
            case "content_block_delta":
            {
                if (!ev.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) break;
                switch (Str(delta, "type"))
                {
                    case "text_delta":
                        _streamed.Add(_messageId);
                        events.Add(new TextDeltaEvent(Key(Int(ev, "index")), Str(delta, "text")));
                        break;
                    case "thinking_delta":
                        var thought = Str(delta, "thinking");
                        _thoughtStreamed.Add(_messageId);
                        if (thought.Length > 0) events.Add(new ThinkingDeltaEvent(thought));
                        break;
                }
                break;
            }
        }
    }

    private string Key(int index) => _messageId + "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Assistant(JsonElement root, List<ChatEvent> events)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        var id = Str(message, "id");
        if (!message.TryGetProperty("content", out var content)) return;
        if (content.ValueKind == JsonValueKind.String)
        {
            if (!_streamed.Contains(id)) events.Add(new AssistantTextEvent(id, content.GetString()!));
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            switch (Str(block, "type"))
            {
                case "text":
                    // Already shown from the stream when partial messages are on.
                    if (!_streamed.Contains(id)) events.Add(new AssistantTextEvent(id, Str(block, "text")));
                    break;
                case "tool_use":
                case "server_tool_use":
                case "mcp_tool_use":
                    events.Add(new ToolUseEvent(Str(block, "id"), Str(block, "name"),
                        block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"));
                    break;
                case "thinking":
                    // Already shown from the stream when partial messages are on; empty when the reasoning isn't shown.
                    var thought = Str(block, "thinking");
                    if (!_thoughtStreamed.Contains(id) && thought.Trim().Length > 0) events.Add(new ThinkingTextEvent(id, thought));
                    break;
            }
        }
    }

    private static void User(JsonElement root, List<ChatEvent> events)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || Str(block, "type") != "tool_result") continue;
            var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            events.Add(new ToolResultEvent(Str(block, "tool_use_id"), isError, ResultText(block)));
        }
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return Cap(content.GetString()!);
        if (content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var kind = Str(part, "type");
            if (kind == "text") sb.Append(Str(part, "text")).Append('\n');
            else if (kind == "image") sb.Append("[image]\n");
            if (sb.Length > 4000) break;
        }
        return Cap(sb.ToString().TrimEnd());
    }

    private static ResultEvent Result(JsonElement root)
    {
        long input = 0, output = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Long(usage, "input_tokens") + Long(usage, "cache_read_input_tokens") + Long(usage, "cache_creation_input_tokens");
            output = Long(usage, "output_tokens");
        }
        var errors = new List<string>();
        if (root.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
                errors.Add(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.ValueKind == JsonValueKind.Object ? Str(e, "message") : e.GetRawText());
        var subtype = Str(root, "subtype");
        var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True || subtype.StartsWith("error", StringComparison.Ordinal);
        var cost = root.TryGetProperty("total_cost_usd", out var usd) && usd.ValueKind == JsonValueKind.Number ? usd.GetDouble() : 0;
        var apiMs = root.TryGetProperty("duration_api_ms", out var api) && api.ValueKind == JsonValueKind.Number ? (long)api.GetDouble() : 0;
        return new ResultEvent(subtype, isError, Str(root, "result"), Str(root, "session_id"), input, output, errors, cost, apiMs);
    }

    /// <summary>"mcp__sketch-promax__create_walls" → "create_walls"; built-in tools keep their name.</summary>
    public static string ShortName(string tool)
    {
        if (!tool.StartsWith("mcp__", StringComparison.Ordinal)) return tool;
        var at = tool.IndexOf("__", 5, StringComparison.Ordinal);
        return at > 0 ? tool.Substring(at + 2) : tool.Substring(5);
    }

    private static string Cap(string s) => s.Length <= 4000 ? s : s.Substring(0, 4000) + "…";

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : v.GetRawText() : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;
}

/// <summary>The lines the pane writes to Claude Code's stdin (--input-format stream-json).</summary>
internal static class StreamInput
{
    /// <summary>One user turn: text plus images, and PDFs as document blocks, all base64.</summary>
    public static string UserMessage(string text, IEnumerable<(string MediaType, string Base64)> images,
                                     IEnumerable<(string Title, string Base64)>? pdfs = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("type", "user");
            w.WriteStartObject("message");
            w.WriteString("role", "user");
            w.WriteStartArray("content");
            foreach (var (media, data) in images)
            {
                w.WriteStartObject();
                w.WriteString("type", "image");
                w.WriteStartObject("source");
                w.WriteString("type", "base64");
                w.WriteString("media_type", media);
                w.WriteString("data", data);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            foreach (var (title, data) in pdfs ?? [])
            {
                w.WriteStartObject();
                w.WriteString("type", "document");
                w.WriteStartObject("source");
                w.WriteString("type", "base64");
                w.WriteString("media_type", "application/pdf");
                w.WriteString("data", data);
                w.WriteEndObject();
                w.WriteString("title", title);
                w.WriteEndObject();
            }
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteNull("parent_tool_use_id");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Claude Code's /compact as a user turn: the earlier conversation is replaced by a summary that keeps what the
    /// instructions name. The content is a plain string, which is how Claude Code recognises a slash command.
    /// </summary>
    public static string Compact(string instructions) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + JsonSerializer.Serialize(("/compact " + instructions).Trim()) +
        "},\"parent_tool_use_id\":null}";

    /// <summary>Asks Claude Code to stop the current turn (the Agent SDK's interrupt()).</summary>
    public static string Interrupt(string requestId) =>
        "{\"type\":\"control_request\",\"request_id\":" + JsonSerializer.Serialize(requestId) + ",\"request\":{\"subtype\":\"interrupt\"}}";
}
