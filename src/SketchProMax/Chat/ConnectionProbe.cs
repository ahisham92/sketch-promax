using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace SketchProMax.Chat;

/// <summary>
/// The probes behind ConnectionWatch, off Revit's thread with short time limits: the internet is up when the chat's
/// service answers at all (any HTTP status, through the PC's proxy), and the company network is up when the central
/// model's folder can be reached. Only a central on a shared folder (\\server\… or a mapped network drive) is probed.
/// </summary>
internal static class ConnectionProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static HttpClient? _http;

    public static async Task<bool> InternetAsync()
    {
        try
        {
            _http ??= Client();
            using var cts = new CancellationTokenSource(Limit);
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://api.anthropic.com/");
            using var _ = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return true; // any answer, even an error status: the line works
        }
        catch (Exception) { return false; }
    }

    /// <summary>Whether the folder of <paramref name="central"/> can be reached; null when it isn't on a company share.</summary>
    public static async Task<bool?> NetworkAsync(string central)
    {
        if (CompanyFolder(central) is not { } folder) return null;
        var look = Task.Run(() => Directory.Exists(folder));
        // A share that is gone can take a minute to say so: past the limit it counts as not reachable.
        return await Task.WhenAny(look, Task.Delay(Limit)).ConfigureAwait(false) == look && look.Result;
    }

    internal static string? CompanyFolder(string central)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(central) || !Path.IsPathRooted(central)) return null;
            var folder = Path.GetDirectoryName(central);
            if (string.IsNullOrEmpty(folder)) return null;
            if (central.StartsWith(@"\\", StringComparison.Ordinal)) return folder;
            var root = Path.GetPathRoot(central);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network ? folder : null;
        }
        catch (Exception) { return null; }
    }

    private static HttpClient Client()
    {
#if NETFRAMEWORK
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
        var handler = new HttpClientHandler { UseProxy = true, UseDefaultCredentials = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials };
        return new HttpClient(handler) { Timeout = Limit };
    }
}
