// Iris client (copied from the Iris app's iris_app/clients/csharp/IrisClient.cs; change it there too).
// Iris client for .NET tools (.NET Framework 4.8 and .NET 8+). One file; needs
// System.Text.Json. Copy it into the tool and keep IrisClient.State somewhere
// private to the user (it holds the token and the message key).
//
//   var iris = new IrisClient(state);                       // state loaded from disk, or new
//   var link = await iris.StartPairingAsync("https://site/iris", "My tool", Environment.MachineName, new[] { "Stop" });
//   // show link as a QR code, then poll:
//   while (await iris.CheckPairingAsync() == "waiting") await Task.Delay(2500);
//   var qid = await iris.AskAsync("4 lengths or 3?", new[] { "4", "3" }, assumed: "4");
//   foreach (var m in await iris.InboxAsync()) { ... m.Kind, m.ReplyTo, m.Text ... }
//
// The sealed format is the same in every Iris client; see docs/api.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SketchProMax.Phone
{
    /// <summary>What a tool keeps between runs. Save it after every call that changes it.</summary>
    internal sealed class IrisState
    {
        public string Site { get; set; } = "";
        public string Key { get; set; } = "";
        public string Token { get; set; } = "";
        public long ToolId { get; set; }
        public long After { get; set; }
        public long PairId { get; set; }
        public string PairSecret { get; set; } = "";
        public string PairCode { get; set; } = "";
        public DateTime PairStarted { get; set; }

        public bool Connected => Token.Length > 0 && Key.Length > 0 && Site.Length > 0;
        public bool Pairing => PairId > 0 && PairSecret.Length > 0;
    }

    /// <summary>One message from the phone, decrypted.</summary>
    internal sealed class IrisMessage
    {
        public long Id { get; set; }
        public string Kind { get; set; } = "";
        public long? ReplyTo { get; set; }
        public DateTime Created { get; set; }
        /// <summary>The answer or message text, or the command name.</summary>
        public string Text { get; set; } = "";
        public string Error { get; set; } = "";
    }

    internal sealed class IrisException : Exception
    {
        public int Status { get; }
        public IrisException(int status, string message) : base(message) { Status = status; }
        /// <summary>The tool was disconnected (from the phone, or the token was lost).</summary>
        public bool Disconnected => Status == 401;
    }

    internal sealed class IrisClient
    {
        static readonly HttpClient Shared = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        readonly HttpClient http;
        public IrisState State { get; }

        public IrisClient(IrisState? state = null, HttpClient? http = null)
        {
            State = state ?? new IrisState();
            this.http = http ?? Shared;
        }

        // ---- connecting --------------------------------------------------------

        /// <summary>Ask to be connected. Returns the link to show as a QR code.</summary>
        public async Task<string> StartPairingAsync(string site, string name, string machine, IEnumerable<string>? commands = null, CancellationToken ct = default)
        {
            var key = new byte[64];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            State.Site = site.TrimEnd('/');
            State.Key = B64u(key);
            State.Token = "";
            State.ToolId = 0;
            State.After = 0;
            var body = new JsonObject
            {
                ["name"] = name,
                ["machine"] = machine,
                ["commands"] = new JsonArray((commands ?? Enumerable.Empty<string>()).Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            };
            var got = await CallAsync(HttpMethod.Post, "/api/tool/pair", body, null, ct).ConfigureAwait(false);
            State.PairId = got["pair_id"]!.GetValue<long>();
            State.PairSecret = got["secret"]!.GetValue<string>();
            State.PairCode = got["code"]!.GetValue<string>();
            State.PairStarted = DateTime.UtcNow;
            return PairingLink;
        }

        /// <summary>The link the phone scans: the key rides after the '#', so the server never sees it.</summary>
        public string PairingLink => $"{State.Site}/pair?c={State.PairCode}#k={State.Key}";

        /// <summary>"waiting", "expired" or "connected" (and then the tool is ready).</summary>
        public async Task<string> CheckPairingAsync(CancellationToken ct = default)
        {
            var got = await CallAsync(HttpMethod.Get, $"/api/tool/pair/{State.PairId}", null,
                new Dictionary<string, string> { ["X-Pair-Secret"] = State.PairSecret }, ct).ConfigureAwait(false);
            var status = got["status"]!.GetValue<string>();
            if (status == "connected")
            {
                State.Token = got["token"]!.GetValue<string>();
                State.ToolId = got["tool_id"]!.GetValue<long>();
                State.After = 0;
                State.PairId = 0;
                State.PairSecret = "";
                State.PairCode = "";
            }
            return status;
        }

        /// <summary>Cuts the link from this side. Forgets the token and key even if the site cannot be reached.</summary>
        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            try { if (State.Connected) await CallAsync(HttpMethod.Delete, "/api/tool/me", null, null, ct).ConfigureAwait(false); }
            finally { Forget(); }
        }

        public void Forget()
        {
            State.Token = "";
            State.Key = "";
            State.ToolId = 0;
            State.After = 0;
            State.PairId = 0;
            State.PairSecret = "";
            State.PairCode = "";
        }

        /// <summary>The name the tool speaks under on the phone (the coordinator's: Sam, ...).</summary>
        public Task<JsonNode> SetAgentAsync(string agent, CancellationToken ct = default) =>
            CallAsync(HttpMethod.Post, "/api/tool/me", new JsonObject { ["agent"] = agent }, null, ct);

        /// <summary>Whether the site still knows this tool. Throws IrisException(401) when it was disconnected.</summary>
        public Task<JsonNode> CheckAsync(CancellationToken ct = default) => CallAsync(HttpMethod.Get, "/api/tool/me", null, null, ct);

        // ---- talking -----------------------------------------------------------

        public async Task<long> SendAsync(string kind, JsonObject value, long? replyTo = null, CancellationToken ct = default)
        {
            var body = new JsonObject { ["kind"] = kind, ["body"] = Seal(KeyBytes, value.ToJsonString()) };
            if (replyTo != null) body["reply_to"] = replyTo.Value;
            var got = await CallAsync(HttpMethod.Post, "/api/tool/messages", body, null, ct).ConfigureAwait(false);
            return got["id"]!.GetValue<long>();
        }

        public Task<long> UpdateAsync(string text, CancellationToken ct = default) =>
            SendAsync("update", new JsonObject { ["text"] = text }, null, ct);

        /// <summary>Ask a question. Returns its id; the answer arrives through InboxAsync with ReplyTo = that id.</summary>
        public Task<long> AskAsync(string text, IEnumerable<string>? options = null, string? assumed = null, DateTime? deadlineUtc = null, CancellationToken ct = default)
        {
            var value = new JsonObject { ["text"] = text };
            var opts = options?.Where(o => !string.IsNullOrWhiteSpace(o)).ToArray() ?? Array.Empty<string>();
            if (opts.Length > 0) value["options"] = new JsonArray(opts.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray());
            if (!string.IsNullOrWhiteSpace(assumed)) value["assumed"] = assumed;
            if (deadlineUtc != null) value["deadline"] = deadlineUtc.Value.ToUniversalTime().ToString("o");
            return SendAsync("question", value, null, ct);
        }

        /// <summary>Close a question on this side (went with the assumption, or answered on the PC).</summary>
        public Task<long> SettleAsync(long questionId, string text, CancellationToken ct = default) =>
            SendAsync("closed", new JsonObject { ["text"] = text }, questionId, ct);

        /// <summary>New answers, commands and messages from the phone, oldest first. Advances State.After.</summary>
        public async Task<List<IrisMessage>> InboxAsync(CancellationToken ct = default)
        {
            var got = await CallAsync(HttpMethod.Get, $"/api/tool/inbox?after={State.After}", null, null, ct).ConfigureAwait(false);
            var list = new List<IrisMessage>();
            foreach (var m in got["messages"]!.AsArray())
            {
                var msg = new IrisMessage
                {
                    Id = m!["id"]!.GetValue<long>(),
                    Kind = m["kind"]!.GetValue<string>(),
                    ReplyTo = m["reply_to"] is JsonNode r ? r.GetValue<long>() : (long?)null,
                    Created = DateTimeOffset.FromUnixTimeMilliseconds((long)(m["created"]!.GetValue<double>() * 1000)).UtcDateTime,
                };
                try
                {
                    var value = JsonNode.Parse(Open(KeyBytes, m["body"]!.GetValue<string>()))!;
                    msg.Text = (value["text"] ?? value["command"])?.GetValue<string>() ?? "";
                }
                catch (Exception e) { msg.Error = e.Message; }
                list.Add(msg);
                State.After = Math.Max(State.After, msg.Id);
            }
            return list;
        }

        // ---- the wire ------------------------------------------------------------

        byte[] KeyBytes => FromB64u(State.Key);

        async Task<JsonNode> CallAsync(HttpMethod method, string path, JsonNode? body, Dictionary<string, string>? headers, CancellationToken ct)
        {
            if (State.Site.Length == 0) throw new IrisException(0, "No Iris address set.");
            using var req = new HttpRequestMessage(method, State.Site + path);
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            if (State.Token.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + State.Token);
            if (headers != null) foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            using var reply = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await reply.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonNode? json = null;
            try { json = text.Length > 0 ? JsonNode.Parse(text) : new JsonObject(); } catch (JsonException) { }
            if (!reply.IsSuccessStatusCode)
            {
                var message = json?["error"]?.GetValue<string>() ?? $"Iris answered {(int)reply.StatusCode}.";
                if ((int)reply.StatusCode == 401) State.Token = "";
                throw new IrisException((int)reply.StatusCode, message);
            }
            return json ?? throw new IrisException((int)reply.StatusCode, "Iris did not answer with JSON. Is the address right?");
        }

        // ---- the sealed format (docs/api.md) ---------------------------------------

        public static string Seal(byte[] key, string json)
        {
            using var aes = Aes.Create();
            aes.Key = key.Take(32).ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();
            byte[] ct;
            using (var enc = aes.CreateEncryptor())
            {
                var plain = Encoding.UTF8.GetBytes(json);
                ct = enc.TransformFinalBlock(plain, 0, plain.Length);
            }
            var signed = aes.IV.Concat(ct).ToArray();
            using var mac = new HMACSHA256(key.Skip(32).Take(32).ToArray());
            return "v1." + B64u(signed.Concat(mac.ComputeHash(signed)).ToArray());
        }

        public static string Open(byte[] key, string sealedText)
        {
            if (!sealedText.StartsWith("v1.", StringComparison.Ordinal)) throw new CryptographicException("Not an Iris message.");
            var all = FromB64u(sealedText.Substring(3));
            if (all.Length < 64) throw new CryptographicException("Message too short.");
            var signed = all.Take(all.Length - 32).ToArray();
            var tag = all.Skip(all.Length - 32).ToArray();
            using (var mac = new HMACSHA256(key.Skip(32).Take(32).ToArray()))
            {
                var want = mac.ComputeHash(signed);
                var diff = 0;
                for (var i = 0; i < 32; i++) diff |= want[i] ^ tag[i];
                if (diff != 0) throw new CryptographicException("Message was changed or the key is wrong.");
            }
            using var aes = Aes.Create();
            aes.Key = key.Take(32).ToArray();
            aes.IV = signed.Take(16).ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(signed, 16, signed.Length - 16);
            return Encoding.UTF8.GetString(plain);
        }

        public static string B64u(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromB64u(string text)
        {
            var s = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
        }
    }
}
