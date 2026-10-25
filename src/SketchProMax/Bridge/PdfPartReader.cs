using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SketchProMax.Bridge;

/// <summary>
/// read_pdf_part: one part of a large PDF that the Revit chat split by itself (each at most 18 MB and 100 pages), read in
/// full by its own hidden Claude Code chat, which answers the main chat's question about it. The part's chat is kept
/// (its session id in the parts folder's sessions.json), so a follow-up about the same part doesn't send the part again.
/// It runs in the connector, never on Revit's thread, so Revit is never held while a part is read: in the connector
/// program for the desktop app, and in the chat's connection inside Revit (ChatMcp). The pane writes "parts.json" next to
/// the parts: the original file, its page count, and each part's pages. No Revit and no MCP library here.
/// </summary>
internal static class PdfPartReader
{
    public const string ToolName = "read_pdf_part";

    private static readonly SemaphoreSlim SessionsLock = new(1, 1);

    private sealed record PartInfo(string Source, int Total, int First, int Last, int Number, int Count);

    /// <param name="claude">The Claude Code the chat runs (it reads the part); null when the caller isn't the chat.</param>
    /// <param name="model">The chat's model, used for the part too; null for Claude Code's default.</param>
    public static async Task<ConnectorResult> CallAsync(IDictionary<string, JsonElement>? arguments, string? claude, string? model, CancellationToken ct)
    {
        var file = Arg(arguments, "file").Trim().Trim('"');
        var question = Arg(arguments, "question").Trim();
        if (file.Length == 0 || question.Length == 0) return new("Give file (a part listed in the attachment note) and question.", true);
        if (!File.Exists(file)) return new($"{file} was not found. Use a part file exactly as the attachment note lists it.", true);
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var info = Info(folder, Path.GetFileName(file));
        if (string.IsNullOrEmpty(claude) || !File.Exists(claude))
            return new("The part can't be read here: Claude Code wasn't passed to the Sketch ProMax connector (only the chat in Revit reads parts). " +
                       "Answer from the text file instead.", true);

        var key = Path.GetFileName(file);
        var session = await SessionAsync(folder, key, null, ct).ConfigureAwait(false);
        var (answer, newSession, error) = await AskAsync(claude!, model, folder, file, info, question, session, ct).ConfigureAwait(false);
        if (error is not null && session is not null)
        {
            // The part's chat couldn't be reopened (deleted, or from another PC): read the part again in a new one.
            (answer, newSession, error) = await AskAsync(claude!, model, folder, file, info, question, null, ct).ConfigureAwait(false);
        }
        if (error is not null) return new($"The part couldn't be read ({error}). Answer from the text file, or try once more.", true);
        if (newSession is not null && newSession != session) await SessionAsync(folder, key, newSession, ct).ConfigureAwait(false);
        var head = info is null ? Path.GetFileName(file)
            : $"Part {info.Number} of {info.Count} of {info.Source} (pages {info.First}-{info.Last} of {info.Total})";
        return new(head + ":\n" + answer, false);
    }

    private static async Task<(string Answer, string? Session, string? Error)> AskAsync(string claude, string? model, string folder, string file, PartInfo? info,
        string question, string? session, CancellationToken ct)
    {
        var args = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--tools", "",
                                      "--strict-mcp-config", "--permission-mode", "dontAsk" };
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model!); }
        // Its own session id, always: a Claude Code started from inside another can otherwise carry on the parent's session.
        if (session is not null) { args.Add("--resume"); args.Add(session); }
        else { args.Add("--session-id"); args.Add(Guid.NewGuid().ToString()); }

        var content = new JsonArray();
        if (session is null)
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(file), ct).ConfigureAwait(false);
            content.Add(new JsonObject
            {
                ["type"] = "document",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = Convert.ToBase64String(bytes) },
                ["title"] = Path.GetFileName(file),
            });
        }
        var where = info is null ? "" :
            $" It is part {info.Number} of {info.Count} of {info.Source}: pages {info.First} to {info.Last} of {info.Total}. Page n of this part is page {info.First} + n - 1 of the original; always give the original's page numbers.";
        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = (session is null
                ? "You read one part of a large PDF for another assistant, who will pass your answer on to an engineer." + where +
                  " Answer only from this part: quote the exact wording, numbers and units that matter and give the page of each. Describe drawings " +
                  "when they answer it. If this part doesn't cover the question, say so in one line (NOT IN THIS PART) and name anything close by.\n\n"
                : "Another question about the same part." + where + "\n\n") + "Question: " + question,
        });
        var line = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            ["parent_tool_use_id"] = null,
        }.ToJsonString();

        var command = string.Join(" ", args.Select(Quote));
        var start = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(claude) + " " + command + "\"")
            : new ProcessStartInfo(claude, command);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.WorkingDirectory = folder;
        // The helper is a chat of its own, not one of Revit's: it gets no Revit tools and no instructions to ask in Revit.
        // It runs on the user's Claude plan, like the chat: an API key in the environment would bill the API instead.
        start.Environment.Remove("SKETCHPROMAX_CHAT");
        start.Environment.Remove("ANTHROPIC_API_KEY");
        foreach (var name in start.Environment.Keys.Where(k => k == "CLAUDECODE" || k.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal) && k.Contains("SESSION")).ToList())
            start.Environment.Remove(name);

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return ("", null, ex.Message); }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromMinutes(10));
        using var kill = stop.Token.Register(() => KillTree(process));
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            await stdin.WriteLineAsync(line).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
            stdin.Close();
        }
        catch (IOException) { /* it ended at once; the output says why */ }

        string? answer = null, newSession = null, failure = null;
        while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } output)
        {
            if (!output.StartsWith("{", StringComparison.Ordinal) || output.IndexOf("\"result\"", StringComparison.Ordinal) < 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(output);
                var r = doc.RootElement;
                if (r.TryGetProperty("type", out var t) && t.GetString() == "result")
                {
                    newSession = r.TryGetProperty("session_id", out var s) ? s.GetString() : null;
                    var text = r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String ? res.GetString() ?? "" : "";
                    if (r.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True) failure = text.Length > 0 ? text : "the helper stopped with an error";
                    else answer = text;
                }
            }
            catch (JsonException) { }
        }
        await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (stop.IsCancellationRequested) return ("", null, "it took more than 10 minutes");
        if (answer is { Length: > 0 }) return (answer, newSession, null);
        var why = failure ?? Last((await errors.ConfigureAwait(false)).Trim(), 300);
        return ("", null, why.Length > 0 ? why : "the helper gave no answer");
    }

    private static void KillTree(Process process)
    {
        try
        {
#if NETFRAMEWORK
            if (process.HasExited) return;
            using var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true });
            kill?.WaitForExit(5000);
            if (!process.HasExited) process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
        }
        catch (Exception) { }
    }

    private static PartInfo? Info(string folder, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "parts.json")));
            var r = doc.RootElement;
            var parts = r.GetProperty("parts").EnumerateArray().ToList();
            for (var i = 0; i < parts.Count; i++)
                if (string.Equals(parts[i].GetProperty("file").GetString(), name, StringComparison.OrdinalIgnoreCase))
                    return new PartInfo(r.GetProperty("source").GetString() ?? "", r.GetProperty("pages").GetInt32(),
                        parts[i].GetProperty("first").GetInt32(), parts[i].GetProperty("last").GetInt32(), i + 1, parts.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        return null;
    }

    /// <summary>The part's kept chat (null when none), or, with <paramref name="set"/>, keeps that one.</summary>
    private static async Task<string?> SessionAsync(string folder, string key, string? set, CancellationToken ct)
    {
        var file = Path.Combine(folder, "sessions.json");
        await SessionsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            JsonObject map;
            try { map = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? [] : []; }
            catch (Exception ex) when (ex is IOException or JsonException) { map = []; }
            if (set is null) return map[key]?.GetValue<string>();
            map[key] = set;
            try { File.WriteAllText(file, map.ToJsonString()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return set;
        }
        finally { SessionsLock.Release(); }
    }

    private static string Arg(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Quote(string s) =>
        s.Length > 0 && s.IndexOfAny([' ', '"', '\t']) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    private static string Last(string text, int max) => text.Length <= max ? text : "…" + text.Substring(text.Length - max);
}
