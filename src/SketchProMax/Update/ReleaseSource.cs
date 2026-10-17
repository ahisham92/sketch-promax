using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace SketchProMax.Update;

/// <summary>
/// Where the latest release is read from. GitHub's API (api.github.com) says the most (the package's digest, the notes)
/// but is the less reachable of the two: some networks block or time out on it, and it allows 60 anonymous requests an
/// hour per address, which an office shares. github.com/&lt;repo&gt;/releases/latest redirects to the latest tag and the
/// package has a fixed address under github.com, so any failure of the API (an error status, a refused or timed-out
/// connection, an unreadable answer) falls back to that. No Revit here: the tests check it.
/// </summary>
internal static class ReleaseSource
{
    /// <summary>How long the API gets before the web page is asked instead.</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The package's address under github.com for a release tag (e.g. v0.33.2).</summary>
    public static string DownloadUrl(string repository, string tag, string version) =>
        $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(tag)}/SketchProMax-{version}.zip";

    /// <summary>The tag a releases/latest redirect names (…/releases/tag/v0.33.2), or null when it names none.</summary>
    public static string? TagFromRedirect(string? location)
    {
        if (string.IsNullOrEmpty(location)) return null;
        const string marker = "/releases/tag/";
        var at = location!.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var tag = Uri.UnescapeDataString(location.Substring(at + marker.Length).Split('?', '#')[0].Trim('/'));
        return tag.Length == 0 ? null : tag;
    }

    /// <summary>Whether a failure of the API call is one to fall back from (everything but the caller's own cancel).</summary>
    public static bool FallBackFrom(Exception ex, CancellationToken callerToken) =>
        ex is OperationCanceledException ? !callerToken.IsCancellationRequested
            : ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException
                or InvalidDataException or FormatException or System.Net.Sockets.SocketException;

    /// <summary>
    /// <paramref name="api"/> with <paramref name="apiTimeout"/>; when it fails in any way (it throws, times out, or says
    /// to fall back by returning <see cref="Fallback"/>), <paramref name="web"/>. <paramref name="why"/> says why the
    /// API was not used (null when it was). The caller's own cancel is never swallowed.
    /// </summary>
    public static async Task<(T? Result, string? Why)> ApiThenWebAsync<T>(Func<CancellationToken, Task<Answer<T>>> api, Func<CancellationToken, Task<T?>> web,
        TimeSpan apiTimeout, CancellationToken ct) where T : class
    {
        string why;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(apiTimeout);
            try
            {
                var answer = await api(limit.Token).ConfigureAwait(false);
                if (!answer.FallBack) return (answer.Value, null);
                why = answer.Why ?? "GitHub's API did not answer";
            }
            catch (Exception ex) when (FallBackFrom(ex, ct))
            {
                why = ex is OperationCanceledException ? $"GitHub's API did not answer within {apiTimeout.TotalSeconds:0} s" : "GitHub's API could not be reached: " + ex.Message;
            }
        }
        return (await web(ct).ConfigureAwait(false), why);
    }

    /// <summary>What the API call gave: a value (null for "no release"), or a reason to ask the web page instead.</summary>
    public readonly struct Answer<T>(T? value, bool fallBack, string? why) where T : class
    {
        public T? Value { get; } = value;
        public bool FallBack { get; } = fallBack;
        public string? Why { get; } = why;
    }

    public static Answer<T> Found<T>(T? value) where T : class => new(value, false, null);

    public static Answer<T> Fallback<T>(string why) where T : class => new(null, true, why);
}
