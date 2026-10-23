using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Licensing;

/// <summary>
/// Publishes a license change (a shorter date or a cancelled key) from the License manager: appends its line to
/// licenses/changes.txt on the repository's main branch through GitHub's contents API, with the owner's own GitHub token
/// (fine-grained, Contents read and write on this repository only), kept on this PC protected by Windows.
/// </summary>
internal static class ChangesPublisher
{
    internal const string Repo = "ahisham92/sketch-promax";
    internal const string PathInRepo = "licenses/changes.txt";
    /// <summary>Where the owner makes the token (GitHub's page for a new fine-grained token).</summary>
    internal const string NewTokenUrl = "https://github.com/settings/personal-access-tokens/new";

    private static string TokenFile => Path.Combine(LicenseState.Folder, "github.dat");

    public static bool HasToken => Token() is not null;

    public static string? Token()
    {
        try
        {
            if (!File.Exists(TokenFile)) return null;
            var t = Encoding.UTF8.GetString(LicenseState.Dpapi.Unprotect(File.ReadAllBytes(TokenFile))).Trim();
            return t.Length == 0 ? null : t;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }

    public static void SaveToken(string token)
    {
        Directory.CreateDirectory(LicenseState.Folder);
        File.WriteAllBytes(TokenFile, LicenseState.Dpapi.Protect(Encoding.UTF8.GetBytes(token.Trim())));
    }

    public static void ForgetToken()
    {
        try { File.Delete(TokenFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Appends <paramref name="lines"/> (already in the file: skipped). Throws InvalidOperationException with the user's words.</summary>
    public static async Task PublishAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        var token = Token() ?? throw new InvalidOperationException("Add your GitHub token first.");
#if NETFRAMEWORK
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SketchProMax-License");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var url = $"https://api.github.com/repos/{Repo}/contents/{PathInRepo}";

        string text = "", sha = "";
        using (var got = await http.GetAsync(url + "?ref=main", ct).ConfigureAwait(false))
        {
            var body = await got.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (got.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("GitHub refused the token (it may have expired). Add a new one.");
            if (got.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                sha = doc.RootElement.GetProperty("sha").GetString() ?? "";
                text = Encoding.UTF8.GetString(Convert.FromBase64String((doc.RootElement.GetProperty("content").GetString() ?? "").Replace("\n", "")));
            }
            else if (got.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException($"GitHub answered {(int)got.StatusCode} when reading the changes list.");
        }

        var next = Append(text, lines);
        if (next == text) return;
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["message"] = "License changes: " + string.Join(", ", lines),
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(next)),
            ["branch"] = "main",
            ["sha"] = sha,
        }.Where(p => p.Value.Length > 0).ToDictionary(p => p.Key, p => p.Value));
        using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        using var sent = await http.SendAsync(put, ct).ConfigureAwait(false);
        if (sent.IsSuccessStatusCode) return;
        throw new InvalidOperationException(sent.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "GitHub refused the token (it may have expired). Add a new one.",
            System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound =>
                "The token can't write to the Sketch ProMax repository. Make one with Contents: Read and write on that repository.",
            System.Net.HttpStatusCode.Conflict => "The list changed meanwhile. Click Publish again.",
            _ => $"GitHub answered {(int)sent.StatusCode} when saving the changes list.",
        });
    }

    /// <summary>The list with <paramref name="lines"/> added at the end, skipping lines it already has.</summary>
    internal static string Append(string text, IEnumerable<string> lines)
    {
        var have = new HashSet<string>(text.Split('\n').Select(l => l.Trim()), StringComparer.Ordinal);
        var sb = new StringBuilder(text);
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !have.Contains(l)))
        {
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
            sb.Append(line).Append('\n');
            have.Add(line);
        }
        return sb.ToString();
    }
}
