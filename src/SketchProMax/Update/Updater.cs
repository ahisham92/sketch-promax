using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace SketchProMax.Update;

/// <summary>
/// Keeps Sketch ProMax up to date without reinstalling, and without restarting Revit.
///
/// It asks GitHub for the latest release at Revit start and hourly. When a newer one exists it downloads the zip to
/// %LOCALAPPDATA%\SketchProMax\updates, unpacks it into %LOCALAPPDATA%\SketchProMax\versions\&lt;version&gt;, and:
///   - tells the loader (SketchProMax.Loader, which Revit loads) that a newer core is ready. The loader switches to it
///     the next time Revit is idle with no job running: the old core stops, the new one starts. Nothing is overwritten;
///     the old DLLs stay loaded, unused, until Revit closes.
///   - points each installed Revit version's SketchProMax.addin at the new version's loader, for the next Revit start.
///   - replaces the desktop-app connector (SketchProMax.Mcp.exe). Windows lets a running exe be renamed, so
///     the old one is moved aside and the new one copied in; the Claude app uses it after its next restart. The tool
///     list itself reaches the Claude app straight away: the new core rewrites tools.json, which the connector watches.
/// Settings live in %APPDATA%\SketchProMax and are never touched.
/// </summary>
public static class Updater
{
    public const string Repository = "ahisham92/sketch-promax";

    private static readonly string LocalRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax");

    private static string UpdatesFolder => Path.Combine(LocalRoot, "updates");
    private static string VersionsFolder => Path.Combine(LocalRoot, "versions");
    private static string McpFolder => Path.Combine(LocalRoot, "mcp");
    private static string StateFile => Path.Combine(LocalRoot, "update-state.json");

    private static string RevitAddinsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins");

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    private const string LoaderContractKey = "SketchProMax.Loader.Contract";
    private const string LoaderPendingKey = "SketchProMax.Loader.Pending";
    private const string LoaderPathKey = "SketchProMax.Loader.Path";
    private const string LoaderDll = "SketchProMax.Loader.dll";
    private const string LoaderClass = "SketchProMax.Loader.LoaderApp";

    /// <summary>True when the loader runs this core, so a new version can be switched to without restarting Revit.</summary>
    public static bool CanSwitchLive => AppDomain.CurrentDomain.GetData(LoaderContractKey) is int;

    /// <summary>Version downloaded and ready for the next Revit start, if any.</summary>
    public static string? Staged => LoadState().Staged is { } s && Parse(s) > Current ? s : null;

    /// <summary>When updates were last looked for, and whether that look failed (for the Update button's badge).</summary>
    public static (DateTime? LastCheck, bool Failed) LastCheckState()
    {
        var state = LoadState();
        return (state.LastCheck, state.LastError is not null);
    }

    /// <summary>A one-line description for the Status button and for Claude.</summary>
    public static string Describe()
    {
        var state = LoadState();
        if (Staged is { } staged)
            return CanSwitchLive
                ? $"Sketch ProMax {staged} is downloaded and is switched to as soon as Revit is idle with no job running. No restart needed."
                : $"Sketch ProMax {staged} is downloaded. Close and reopen Revit, and restart the Claude desktop app, to start using it.";
        if (state.LastError is { } error)
            return $"Sketch ProMax {CurrentText}. The last update check failed: {error}";
        return state.LastCheck is { } last
            ? $"Sketch ProMax {CurrentText} is up to date (checked {last.ToLocalTime():g})."
            : $"Sketch ProMax {CurrentText}.";
    }

    /// <summary>
    /// Checks for and stages an update, reporting each stage to <paramref name="progress"/>. When the loader runs this core
    /// it is then asked to switch to the new version (the switch itself happens on Revit's thread, later). Returns the
    /// newly staged version, or null when there is nothing new.
    /// </summary>
    public static async Task<string?> CheckAsync(bool force, UpdateProgress? progress = null, CancellationToken ct = default)
    {
        progress ??= new UpdateProgress();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, progress.Cancellation.Token);
        ct = linked.Token;
        // Only one Revit window updates at a time. A lock file, because a mutex can't be released after an await.
        FileStream? gate;
        try
        {
            Directory.CreateDirectory(LocalRoot);
            gate = new FileStream(Path.Combine(LocalRoot, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress.Report(UpdateStage.Failed, "Another Revit window is updating Sketch ProMax right now. This window follows it when it is done.");
            return null;
        }

        try
        {
            CleanUp();
            var state = LoadState();
            if (!force && state.LastCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(1))
            {
                progress.Report(UpdateStage.Idle);
                return null;
            }

            progress.Report(UpdateStage.Checking, "Asking GitHub for the latest release…");
            var release = await LatestReleaseAsync(ct).ConfigureAwait(false);
            state.LastCheck = DateTime.UtcNow;
            state.LastError = null;
            if (release is null || Parse(release.Version) <= Current)
            {
                SaveState(state);
                progress.Report(UpdateStage.UpToDate, $"Sketch ProMax {CurrentText} is the latest version.", CurrentText);
                return null;
            }
            var stagedFolder = Path.Combine(VersionsFolder, release.Version);
            if (release.Version == state.Staged && Directory.Exists(stagedFolder))
            {
                // Already downloaded (by this or another Revit window). A reinstall or repair may have pointed a manifest
                // elsewhere since; point it back, and switch this window over too.
                if (!AllManifestsPointAt(stagedFolder)) PointAddinsAt(stagedFolder);
                SaveState(state);
                Apply(stagedFolder, release.Version, progress);
                return null;
            }

            progress.Report(UpdateStage.Downloading, $"Downloading Sketch ProMax {release.Version}…", release.Version);
            var folder = await DownloadAndUnpackAsync(release, progress, ct).ConfigureAwait(false);
            progress.Report(UpdateStage.Applying, "Installing…");
            if (PointAddinsAt(folder) == 0)
            {
                state.LastError = "no installed Revit add-in was found to update. Run Install.cmd once.";
                SaveState(state);
                progress.Report(UpdateStage.Failed, "No installed Sketch ProMax add-in was found to update. Run Install.cmd once.");
                return null;
            }
            ReplaceConnector(folder);
            state.Staged = release.Version;
            SaveState(state);
            Apply(folder, release.Version, progress);
            return release.Version;
        }
        catch (OperationCanceledException) when (progress.Cancellation.IsCancellationRequested)
        {
            progress.Report(UpdateStage.Cancelled, "The update was cancelled. Nothing changed; it is offered again at the next check.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or JsonException or InvalidDataException or OperationCanceledException or InvalidOperationException
                                       or System.Xml.XmlException)
        {
            var state = LoadState();
            state.LastCheck = DateTime.UtcNow;
            state.LastError = ex is OperationCanceledException ? "the download took too long" : ex.Message;
            SaveState(state);
            progress.Report(UpdateStage.Failed, $"The update didn't finish: {state.LastError}. Nothing changed; it is tried again later.");
            return null;
        }
        finally
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// Hands a downloaded version to the loader, which switches to it when Revit is idle; the switch reports "updated"
    /// back through <see cref="CoreEntry"/>. Without the loader (or without a build for this Revit), a restart is needed.
    /// </summary>
    private static void Apply(string versionFolder, string version, UpdateProgress progress)
    {
        var core = Path.Combine(versionFolder, "Revit" + App.RevitYear, "SketchProMax.dll");
        if (CanSwitchLive && App.RevitYear.Length > 0 && File.Exists(core))
        {
            progress.Report(UpdateStage.Applying, "Switching over as soon as Revit is idle and no job is running…", version);
            AppDomain.CurrentDomain.SetData(LoaderPendingKey, core);
        }
        else
        {
            progress.Report(UpdateStage.RestartNeeded,
                $"Sketch ProMax {version} is installed. Restart Revit to finish (your work is not interrupted until then).", version);
        }
    }

    /// <summary>
    /// Manifests written by 0.25.x (or by an older install) load SketchProMax.dll directly. Points them at the loader next
    /// to it, so from the next Revit start updates switch over without a restart. Returns how many were changed.
    /// </summary>
    public static int MoveManifestsToLoader()
    {
        var count = 0;
        if (!Directory.Exists(RevitAddinsRoot)) return 0;
        foreach (var revitFolder in Directory.GetDirectories(RevitAddinsRoot))
        {
            var manifest = Path.Combine(revitFolder, "SketchProMax.addin");
            try
            {
                if (!File.Exists(manifest)) continue;
                var xml = XDocument.Load(manifest);
                var assembly = xml.Descendants("Assembly").FirstOrDefault();
                var path = assembly?.Value?.Trim() ?? "";
                if (assembly is null || !Path.GetFileName(path).Equals("SketchProMax.dll", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.IsPathRooted(path) ? path : Path.Combine(revitFolder, path);
                var loader = Path.Combine(Path.GetDirectoryName(full) ?? "", LoaderDll);
                if (!File.Exists(loader)) continue;
                assembly.Value = Path.IsPathRooted(path) ? loader : Path.Combine(Path.GetDirectoryName(path) ?? "", LoaderDll);
                SetClass(xml, LoaderClass);
                Save(xml, manifest);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException) { }
        }
        return count;
    }

    private static void SetClass(XDocument xml, string fullClassName)
    {
        var element = xml.Descendants("FullClassName").FirstOrDefault();
        if (element is not null) element.Value = fullClassName;
    }

    private static void Save(XDocument xml, string manifest)
    {
        var temp = manifest + ".new";
        xml.Save(temp);
        File.Copy(temp, manifest, true);
        File.Delete(temp);
    }

    private sealed record Release(string Version, string ZipUrl, string? Sha256);

    private static HttpClient CreateClient()
    {
#if NETFRAMEWORK
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SketchProMax/{CurrentText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static async Task<Release?> LatestReleaseAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release published yet
        // GitHub's API allows 60 anonymous requests an hour per network address, which an office network shares.
        // The release web page has no such limit, so fall back to it rather than fail.
        if (!response.IsSuccessStatusCode) return await LatestReleaseFromWebAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;
        var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("SketchProMax-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            // GitHub publishes a "sha256:..." digest for each release file; verify it when present.
            var digest = asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7) : null;
            return new Release(version, asset.GetProperty("browser_download_url").GetString()!, sha);
        }
        return null;
    }

    /// <summary>
    /// The latest release without the API: github.com/.../releases/latest redirects to the release's tag, and the
    /// package name follows from the version. There is no published digest this way, so the zip's contents are checked instead.
    /// </summary>
    private static async Task<Release?> LatestReleaseFromWebAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SketchProMax/{CurrentText}");
        using var response = await http.GetAsync($"https://github.com/{Repository}/releases/latest", ct).ConfigureAwait(false);
        var location = response.Headers.Location?.ToString() ?? "";
        var marker = "/releases/tag/";
        var at = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            throw new HttpRequestException($"GitHub did not say which release is the latest ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }
        var tag = Uri.UnescapeDataString(location.Substring(at + marker.Length).Trim('/'));
        var version = tag.TrimStart('v', 'V');
        if (!Version.TryParse(version, out _)) throw new InvalidDataException($"The latest release tag '{tag}' is not a version number.");
        return new Release(version, $"https://github.com/{Repository}/releases/download/{tag}/SketchProMax-{version}.zip", null);
    }

    /// <summary>True when every installed SketchProMax.addin loads a DLL from the given version folder.</summary>
    private static bool AllManifestsPointAt(string folder)
    {
        var manifests = ManifestAssemblies().ToList();
        return manifests.Count > 0 && manifests.All(a => IsUnder(a, folder));
    }

    /// <summary>The Assembly path of every installed SketchProMax.addin, for all Revit versions.</summary>
    private static IEnumerable<string> ManifestAssemblies()
    {
        if (!Directory.Exists(RevitAddinsRoot)) yield break;
        foreach (var revitFolder in Directory.GetDirectories(RevitAddinsRoot))
        {
            var manifest = Path.Combine(revitFolder, "SketchProMax.addin");
            if (!File.Exists(manifest)) continue;
            string? path;
            try { path = XDocument.Load(manifest).Descendants("Assembly").FirstOrDefault()?.Value?.Trim(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { continue; }
            if (!string.IsNullOrEmpty(path)) yield return path!;
        }
    }

    private static bool IsUnder(string path, string folder)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static async Task<string> DownloadAndUnpackAsync(Release release, UpdateProgress progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesFolder);
        var zip = Path.Combine(UpdatesFolder, $"SketchProMax-{release.Version}.zip");
        // HttpClient.Timeout does not cover reading the body after the headers arrive, so the whole download gets a limit.
        using (var download = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            download.CancelAfter(TimeSpan.FromMinutes(5));
            using var http = CreateClient();
            using var response = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, download.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var file = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            progress.ReportBytes(0, total);
            int read;
            while ((read = await body.ReadAsync(buffer, 0, buffer.Length, download.Token).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer, 0, read, download.Token).ConfigureAwait(false);
                done += read;
                progress.ReportBytes(done, total);
            }
        }
        // From here on the update completes: cancelling half an unpack would only leave a mess.
        ct = CancellationToken.None;
        progress.Report(UpdateStage.Verifying, "Checking the download is complete and undamaged…");

        if (release.Sha256 is { } expected)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(zip);
            var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update is damaged (checksum mismatch). It will be retried later.");
        }
        else
        {
            // No published digest: at least make sure the zip opens and holds what an update needs.
            using var archive = ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            if (!names.Any(n => n.Contains("dist/") && n.EndsWith("/SketchProMax.dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The downloaded update is incomplete (no SketchProMax.dll). It will be retried later.");
        }

        ClearDownloadMark(zip);
        progress.Report(UpdateStage.Unpacking, $"Unpacking Sketch ProMax {release.Version}…");
        var target = Path.Combine(VersionsFolder, release.Version);
        var temp = target + ".partial";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        ZipFile.ExtractToDirectory(zip, temp);
        var dist = Directory.GetDirectories(temp, "dist", SearchOption.AllDirectories).FirstOrDefault()
                   ?? throw new InvalidDataException("The update package has no dist folder.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(dist, target);
        Directory.Delete(temp, true);
        foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories)) ClearDownloadMark(file);
        File.Delete(zip);
        return target;
    }

    /// <summary>Rewrites SketchProMax.addin in each Revit version that has it so it loads the new build. Returns how many were updated.</summary>
    private static int PointAddinsAt(string versionFolder)
    {
        var count = 0;
        if (!Directory.Exists(RevitAddinsRoot)) return 0;
        foreach (var revitFolder in Directory.GetDirectories(RevitAddinsRoot))
        {
            var manifest = Path.Combine(revitFolder, "SketchProMax.addin");
            var dll = Path.Combine(versionFolder, "Revit" + Path.GetFileName(revitFolder), "SketchProMax.dll");
            if (!File.Exists(manifest) || !File.Exists(dll)) continue;

            var xml = XDocument.Load(manifest);
            var assembly = xml.Descendants("Assembly").FirstOrDefault();
            if (assembly is null) continue;
            // Packages from 0.26 on carry the loader: the manifest loads it, and it loads the newest core. Older packages
            // only have the core, which is then loaded directly as before.
            var loader = Path.Combine(versionFolder, "Revit" + Path.GetFileName(revitFolder), LoaderDll);
            if (File.Exists(loader))
            {
                assembly.Value = loader;
                SetClass(xml, LoaderClass);
            }
            else
            {
                assembly.Value = dll;
                SetClass(xml, "SketchProMax.App");
            }
            Save(xml, manifest);
            count++;
        }
        return count;
    }

    private static void ReplaceConnector(string versionFolder)
    {
        var source = Path.Combine(versionFolder, "mcp", "SketchProMax.Mcp.exe");
        var target = Path.Combine(McpFolder, "SketchProMax.Mcp.exe");
        if (!File.Exists(source)) return;
        Directory.CreateDirectory(McpFolder);
        // Copy first, so a failed copy leaves the working connector where it was.
        var incoming = target + ".new";
        File.Copy(source, incoming, true);
        string? aside = null;
        try
        {
            if (File.Exists(target))
            {
                // The Claude app may be running it; a running exe can be renamed but not overwritten.
                aside = Path.Combine(McpFolder, $"SketchProMax.Mcp.{DateTime.UtcNow.Ticks}.old");
                File.Move(target, aside);
            }
            File.Move(incoming, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (aside is not null && !File.Exists(target) && File.Exists(aside)) TryDelete(() => File.Move(aside, target));
            TryDelete(() => File.Delete(incoming));
            throw;
        }
    }

    /// <summary>Removes old connector copies and version folders no Revit is using. Anything still locked is left for next time.</summary>
    private static void CleanUp()
    {
        if (Directory.Exists(McpFolder))
            foreach (var old in Directory.GetFiles(McpFolder, "*.old"))
                TryDelete(() => File.Delete(old));

        if (!Directory.Exists(VersionsFolder)) return;
        var running = Path.GetDirectoryName(typeof(Updater).Assembly.Location) ?? "";
        var loaderPath = AppDomain.CurrentDomain.GetData(LoaderPathKey) as string ?? "";
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CurrentText };
        if (LoadState().Staged is { } staged) keep.Add(staged);
        // Any Revit version may load a different build; never delete one a manifest still points at.
        var referenced = ManifestAssemblies().ToList();
        foreach (var folder in Directory.GetDirectories(VersionsFolder))
        {
            var name = Path.GetFileName(folder);
            if (name.EndsWith(".del", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(() => Directory.Delete(folder, true));
                continue;
            }
            if (keep.Contains(name)) continue;
            if (IsUnder(running, folder)) continue;
            if (loaderPath.Length > 0 && IsUnder(loaderPath, folder)) continue;
            if (referenced.Any(a => IsUnder(a, folder))) continue;
            // Renaming fails while Revit has a DLL in it loaded, so a folder in use is never half deleted.
            var doomed = folder + ".del";
            try
            {
                if (Directory.Exists(doomed)) Directory.Delete(doomed, true);
                Directory.Move(folder, doomed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            TryDelete(() => Directory.Delete(doomed, true));
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileNative(string path);

    /// <summary>
    /// Removes the "downloaded from the internet" mark (the Zone.Identifier alternate data stream) so Windows and
    /// .NET Framework (Revit 2024) don't block the files. HttpClient and ZipFile don't normally add it, but security
    /// software or a proxy can. The Win32 call is used because .NET Framework rejects "file:stream" paths.
    /// </summary>
    private static void ClearDownloadMark(string path)
    {
        try { DeleteFileNative(path + ":Zone.Identifier"); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Version Parse(string text) => Normalize(Version.TryParse(text, out var v) ? v : null);

    private static Version Normalize(Version? v) => v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    private sealed class State
    {
        public DateTime? LastCheck { get; set; }
        public string? Staged { get; set; }
        public string? LastError { get; set; }
    }

    private static State LoadState()
    {
        try
        {
            if (File.Exists(StateFile)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    private static void SaveState(State state)
    {
        try
        {
            Directory.CreateDirectory(LocalRoot);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch (IOException) { }
    }
}
