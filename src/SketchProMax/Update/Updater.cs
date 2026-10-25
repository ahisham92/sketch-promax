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
/// It asks GitHub for the latest release at Revit start and hourly (see App.UpdateLoopAsync). With automatic updates off
/// it only says a newer one is out (<see cref="Latest"/>, UI/UpdateNotice). Otherwise, when a newer one exists it downloads the zip to
/// %LOCALAPPDATA%\SketchProMax\updates, unpacks it into %LOCALAPPDATA%\SketchProMax\versions\&lt;version&gt;, and:
///   - tells the loader (SketchProMax.Loader, which Revit loads) that a newer core is ready. The loader switches to it
///     the next time Revit is idle with no job running: the old core stops, the new one starts. Nothing is overwritten;
///     the old DLLs stay loaded, unused, until Revit closes.
///   - puts the new loader in place of the installed one (Addins\&lt;year&gt;\SketchProMax\SketchProMax.Loader.dll), only when
///     it changed. SketchProMax.addin keeps naming that one path, so Revit's "Always Load" for it holds (AddinFiles).
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
    private const string LoaderDll = AddinFiles.LoaderDll;

    /// <summary>True when the loader runs this core, so a new version can be switched to without restarting Revit.</summary>
    public static bool CanSwitchLive => AppDomain.CurrentDomain.GetData(LoaderContractKey) is int;

    /// <summary>Version downloaded and ready for the next Revit start, if any.</summary>
    public static string? Staged => LoadState().Staged is { } s && Parse(s) > Current ? s : null;

    /// <summary>
    /// The newest release the last successful check saw, when it is newer than this version; null otherwise. The state
    /// file is shared by every Revit window, and another window may run a newer version: a check that window made says
    /// "up to date" for it, not for this one, so the badge compares this version with what was seen.
    /// </summary>
    public static string? Latest => LoadState().Latest is { } l && Parse(l) > Current ? l : null;

    /// <summary>The release notes of <see cref="Latest"/> (GitHub's text, Markdown), when the check could read them.</summary>
    public static string? LatestNotes => Latest is null ? null : LoadState().Notes;

    /// <summary>The release notes the last check read for <paramref name="version"/>, or null.</summary>
    public static string? NotesFor(string version) => LoadState() is { Latest: { } l, Notes: { } n } && Parse(l) == Parse(version) ? n : null;

    /// <summary>When updates were last looked for, and why that look failed (null when it worked), for the Update button's badge.</summary>
    public static (DateTime? LastCheck, string? Error) LastCheckState()
    {
        var state = LoadState();
        return (state.LastCheck, state.LastError);
    }

    /// <summary>
    /// True when the last check (by any Revit window) is at least <paramref name="every"/> old by the wall clock, or
    /// there was none, or the clock went back. The update loop asks every few minutes rather than sleeping an hour: a
    /// timer stands still while Windows sleeps, so after a night asleep an hourly timer still had most of its hour to go.
    /// </summary>
    public static bool IsDue(TimeSpan every) => UI.UpdateBadge.CheckDue(LoadState().LastCheck, every, DateTime.UtcNow);

    /// <summary>A one-line description for the Status button and for Claude.</summary>
    public static string Describe()
    {
        var state = LoadState();
        if (Latest is { } latest && Staged is null)
            return $"Sketch ProMax {latest} is available (this is {CurrentText}). Click Update to install it; Revit stays open.";
        if (Staged is { } staged)
            return CanSwitchLive
                ? $"Sketch ProMax {staged} is downloaded and is switched to as soon as Revit is idle with no job running. No restart needed."
                : $"Sketch ProMax {staged} is downloaded. Close and reopen Revit (and restart the Claude desktop app if you use it) to start using it.";
        if (state.LastError is { } error)
            return $"Sketch ProMax {CurrentText}. The last update check failed: {error}";
        return state.LastCheck is { } last
            ? $"Sketch ProMax {CurrentText} is up to date (checked {last.ToLocalTime():g})."
            : $"Sketch ProMax {CurrentText}.";
    }

    /// <summary>
    /// Checks for and stages an update, reporting each stage to <paramref name="progress"/>. When the loader runs this core
    /// it is then asked to switch to the new version (the switch itself happens on Revit's thread, later). Returns the
    /// newly staged version, or null when there is nothing new. With <paramref name="install"/> false (automatic updates
    /// off) a newer release is only recorded and reported as <see cref="UpdateStage.Available"/>; nothing is downloaded.
    /// </summary>
    public static async Task<string?> CheckAsync(bool force, UpdateProgress? progress = null, CancellationToken ct = default, bool install = true)
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
            state.Latest = release?.Version;
            state.Notes = release?.Notes is { Length: > 4000 } full ? full.Substring(0, 4000) : release?.Notes;
            if (release is null || Parse(release.Version) <= Current)
            {
                SaveState(state);
                progress.Report(UpdateStage.UpToDate, $"Sketch ProMax {CurrentText} is the latest version.", CurrentText);
                return null;
            }
            var stagedFolder = Path.Combine(VersionsFolder, release.Version);
            var downloaded = release.Version == state.Staged && Directory.Exists(stagedFolder);
            if (!install && !downloaded)
            {
                SaveState(state);
                progress.Report(UpdateStage.Available, $"Sketch ProMax {release.Version} is available. Automatic updates are off, so nothing was downloaded.", release.Version);
                return null;
            }
            if (downloaded)
            {
                // Already downloaded (by this or another Revit window). Make sure its loader is in place (nothing is written
                // when it is), and switch this window over too.
                SettleInstall(stagedFolder);
                SaveState(state);
                Apply(stagedFolder, release.Version, progress);
                return null;
            }

            // What was found is kept even if the download then fails: the badge still offers the new version.
            SaveState(state);
            var folder = await DownloadAndUnpackAsync(release, progress, ct).ConfigureAwait(false);
            progress.Report(UpdateStage.Applying, "Installing…");
            if (SettleInstall(folder) == 0)
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
                                       or System.Xml.XmlException or TimeoutException)
        {
            var state = LoadState();
            state.LastCheck = DateTime.UtcNow;
            state.LastError = ex is OperationCanceledException ? "the download took too long" : ex.Message;
            SaveState(state);
            progress.Report(UpdateStage.Failed, $"The update didn't finish: {state.LastError}. Nothing changed. " +
                                                "Try again (or the next automatic check) continues from where it stopped.");
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
    /// Puts each installed Revit version's add-in in its settled form (see <see cref="AddinFiles"/>): the newest loader at
    /// Addins\&lt;year&gt;\SketchProMax\SketchProMax.Loader.dll, and SketchProMax.addin naming that path with one entry. Manifests
    /// from older releases (naming the core, or a loader in a version folder) are moved there once. Nothing is written
    /// when it is already so, because Revit asks about an unsigned add-in again when the file or its place changes.
    /// <paramref name="packageFolder"/> is a just-downloaded version folder; without it the running version's files are
    /// used. Returns how many Revit versions have Sketch ProMax installed and settled.
    /// </summary>
    public static int SettleInstall(string? packageFolder = null)
    {
        var count = 0;
        if (!Directory.Exists(RevitAddinsRoot)) return 0;
        foreach (var revitFolder in Directory.GetDirectories(RevitAddinsRoot))
        {
            try { if (Settle(revitFolder, packageFolder)) count++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException) { }
        }
        return count;
    }

    private static bool Settle(string revitFolder, string? packageFolder)
    {
        var manifest = Path.Combine(revitFolder, AddinFiles.ManifestName);
        if (!File.Exists(manifest)) return false;
        var year = Path.GetFileName(revitFolder);
        var loader = Path.Combine(revitFolder, AddinFiles.LoaderFolder, LoaderDll);
        var source = PackageFolders(year, packageFolder).FirstOrDefault(f => File.Exists(Path.Combine(f, LoaderDll)));
        if (source is not null) PlaceLoader(Path.Combine(source, LoaderDll), loader);
        // Never name a loader that isn't there: the manifest stays as it is until one is.
        if (!File.Exists(loader)) return false;

        var existing = File.ReadAllText(manifest);
        var templatePath = source is null ? null : Path.Combine(source, AddinFiles.ManifestName);
        var template = templatePath is not null && File.Exists(templatePath) ? File.ReadAllText(templatePath) : null;
        if (AddinFiles.IsSettled(template))
        {
            if (!AddinFiles.SameManifest(existing, template)) WriteManifest(manifest, template!);
        }
        else if (AddinFiles.Settle(existing) is { } settled && !AddinFiles.SameManifest(existing, settled))
        {
            WriteManifest(manifest, settled);
        }
        DisableOtherManifests(revitFolder, manifest);
        return true;
    }

    /// <summary>Where a loader (and settled manifest) for Revit <paramref name="year"/> may come from, best first.</summary>
    private static IEnumerable<string> PackageFolders(string year, string? packageFolder)
    {
        if (packageFolder is not null) yield return Path.Combine(packageFolder, "Revit" + year);
        if (year == App.RevitYear && Path.GetDirectoryName(typeof(Updater).Assembly.Location) is { Length: > 0 } running) yield return running;
        yield return Path.Combine(VersionsFolder, CurrentText, "Revit" + year);
    }

    /// <summary>
    /// Replaces the installed loader with <paramref name="source"/> when <see cref="AddinFiles.DecideLoader"/> says so.
    /// Revit may have it loaded: Windows lets a loaded DLL be renamed but not overwritten, so it is moved aside (removed
    /// by a later clean-up) and the new one copied in. When even that fails the installed loader stays; it runs any core.
    /// </summary>
    internal static void PlaceLoader(string source, string target, string prefix = "SketchProMax.Loader")
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        var exists = File.Exists(target);
        var action = AddinFiles.DecideLoader(Hash(source), FileVersionOf(source), exists ? Hash(target) : null, exists ? FileVersionOf(target) : null);
        if (action != AddinFiles.LoaderAction.Replace) return;
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        var stamp = DateTime.UtcNow.Ticks;
        var incoming = Path.Combine(folder, $"{prefix}.{stamp}.new");
        string? aside = null;
        try
        {
            File.Copy(source, incoming, true);
            ClearDownloadMark(incoming);
            if (exists)
            {
                aside = Path.Combine(folder, $"{prefix}.{stamp}.old");
                File.Move(target, aside);
            }
            File.Move(incoming, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (aside is not null && !File.Exists(target) && File.Exists(aside)) TryDelete(() => File.Move(aside, target));
            TryDelete(() => File.Delete(incoming));
        }
    }

    private static void WriteManifest(string manifest, string text)
    {
        var temp = manifest + $".{DateTime.UtcNow.Ticks}.new";
        File.WriteAllText(temp, text);
        File.Copy(temp, manifest, true);
        File.Delete(temp);
    }

    /// <summary>
    /// Other manifests in the same folder that load only Sketch ProMax (a test copy, an older install's leftover) are
    /// renamed to .disabled, so Revit sees one entry for it, not two. A file that also loads another add-in is never
    /// touched: it is written to the update log (update.log) for the user or IT to tidy by hand.
    /// </summary>
    private static void DisableOtherManifests(string revitFolder, string manifest)
    {
        foreach (var other in Directory.GetFiles(revitFolder, "*.addin"))
        {
            if (string.Equals(other, manifest, StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try { text = File.ReadAllText(other); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (!AddinFiles.NamesSketchProMax(text)) continue;
            if (AddinFiles.OnlySketchProMax(text)) TryDelete(() => File.Move(other, other + ".disabled"));
            else LogWarning($"{other} loads Sketch ProMax a second time but also loads other add-ins, so it was left as it is. " +
                            "Remove its Sketch ProMax entry by hand; Revit then loads Sketch ProMax once.");
        }
    }

    private static string LogFile => Path.Combine(LocalRoot, "update.log");

    /// <summary>Appends a dated line to update.log. Never throws.</summary>
    private static void LogWarning(string text)
    {
        try
        {
            Directory.CreateDirectory(LocalRoot);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm} warning: {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? Hash(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static Version? FileVersionOf(string path)
    {
        try { return Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion, out var v) ? v : null; }
        catch (Exception ex) when (ex is IOException or FileNotFoundException) { return null; }
    }

    internal sealed record Release(string Version, string ZipUrl, string? Sha256, string? Notes = null);

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

    /// <summary>
    /// The latest release: GitHub's API first, with a short time limit; on any failure of it (an error status, a refused or
    /// timed-out connection, an unreadable answer) the github.com release page, whose package has a fixed address there.
    /// </summary>
    internal static async Task<Release?> LatestReleaseAsync(CancellationToken ct)
    {
        var (release, _) = await ReleaseSource.ApiThenWebAsync(LatestReleaseFromApiAsync, LatestReleaseFromWebAsync, ReleaseSource.ApiTimeout, ct).ConfigureAwait(false);
        return release;
    }

    private static async Task<ReleaseSource.Answer<Release>> LatestReleaseFromApiAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return ReleaseSource.Found<Release>(null); // no release published yet
        // GitHub's API allows 60 anonymous requests an hour per network address, which an office network shares.
        // The release web page has no such limit, so fall back to it rather than fail.
        if (!response.IsSuccessStatusCode) return ReleaseSource.Fallback<Release>($"GitHub's API answered {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;
        var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return ReleaseSource.Found<Release>(null);
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return ReleaseSource.Found<Release>(null);
        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("SketchProMax-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                // GitHub publishes a "sha256:..." digest for each release file; verify it when present.
                var digest = asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7) : null;
                return ReleaseSource.Found(new Release(version, asset.GetProperty("browser_download_url").GetString()!, sha, notes));
            }
        // A newer release whose package isn't listed (yet): not "up to date". Its package has the usual name; when it is
        // not there the download fails, the error is shown and the next check tries again.
        if (!Version.TryParse(version, out _)) return ReleaseSource.Found<Release>(null);
        return ReleaseSource.Found(new Release(version, ReleaseSource.DownloadUrl(Repository, tag, version), null, notes));
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
        if (ReleaseSource.TagFromRedirect(response.Headers.Location?.ToString()) is not { } tag)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            throw new HttpRequestException($"GitHub did not say which release is the latest ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }
        var version = tag.TrimStart('v', 'V');
        if (!Version.TryParse(version, out _)) throw new InvalidDataException($"The latest release tag '{tag}' is not a version number.");
        return new Release(version, ReleaseSource.DownloadUrl(Repository, tag, version), null);
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

    /// <summary>
    /// Downloads and unpacks <paramref name="release"/> into &lt;root&gt;\versions\&lt;version&gt; (root: %LOCALAPPDATA%\SketchProMax,
    /// or TritoRevit's own folder for its copy). Returns that folder. An earlier try that stopped part way is continued, not
    /// started again (<see cref="UpdateResume"/>): a folder already unpacked is used as it is, a complete download is only
    /// checked, a partial one continues from where it stopped, and unpacking skips files already written.
    /// </summary>
    internal static async Task<string> DownloadAndUnpackAsync(Release release, UpdateProgress progress, CancellationToken ct, string? root = null)
    {
        var updatesFolder = root is null ? UpdatesFolder : Path.Combine(root, "updates");
        var versionsFolder = root is null ? VersionsFolder : Path.Combine(root, "versions");
        var target = Path.Combine(versionsFolder, release.Version);
        if (UpdateResume.IsComplete(target))
        {
            progress.Report(UpdateStage.Unpacking, $"Sketch ProMax {release.Version} was already downloaded and unpacked by an earlier try.", release.Version);
            return target;
        }
        Directory.CreateDirectory(updatesFolder);
        var zip = Path.Combine(updatesFolder, $"SketchProMax-{release.Version}.zip");

        // Only the files that changed, when the release lists its files and no zip download is under way already.
        if (!File.Exists(zip) && UpdateResume.Downloaded(zip) == 0
            && await TryChangedFilesAsync(release, target, versionsFolder, progress, ct).ConfigureAwait(false))
            return target;

        // A complete download from an earlier try is checked rather than fetched again; a damaged one is fetched afresh.
        string? sha = null;
        if (File.Exists(zip))
        {
            progress.Report(UpdateStage.Verifying, "Checking the download an earlier try finished…", release.Version);
            try { sha = Verify(zip, release); }
            catch (InvalidDataException) { UpdateResume.Discard(zip); }
        }
        if (sha is null)
        {
            var have = UpdateResume.Downloaded(zip);
            progress.Report(UpdateStage.Downloading, have > 0
                ? $"Continuing the download of Sketch ProMax {release.Version} from {UpdateSnapshot.Megabytes(have)}…"
                : $"Downloading Sketch ProMax {release.Version}…", release.Version);
            using (var http = CreateClient())
                await UpdateResume.DownloadAsync(http, release.ZipUrl, zip, progress.ReportBytes, UpdateResume.Stall, ct).ConfigureAwait(false);
            progress.Report(UpdateStage.Verifying, "Checking the download is complete and undamaged…");
            try { sha = Verify(zip, release); }
            catch (InvalidDataException)
            {
                // Damaged: the next try downloads it afresh rather than continuing it.
                UpdateResume.Discard(zip);
                throw;
            }
        }
        // From here on the update completes: cancelling half an unpack would only leave a mess.
        ClearDownloadMark(zip);
        var temp = target + ".partial";
        var kept = 0;
        progress.Report(UpdateStage.Unpacking, $"Unpacking Sketch ProMax {release.Version}…");
        try { kept = UpdateResume.Extract(zip, temp, sha); }
        catch (InvalidDataException)
        {
            // The zip itself is unreadable (a check without a published digest can't see every fault): fetch it afresh next time.
            UpdateResume.Discard(zip);
            throw;
        }
        if (kept > 0) progress.Report(UpdateStage.Unpacking, $"Unpacking Sketch ProMax {release.Version} (continuing: {kept} files were already unpacked)…");
        var dist = Directory.GetDirectories(temp, "dist", SearchOption.AllDirectories).FirstOrDefault()
                   ?? throw new InvalidDataException("The update package has no dist folder.");
        if (Directory.Exists(target)) UpdateResume.WithRetry(() => Directory.Delete(target, true));
        UpdateResume.WithRetry(() => Directory.Move(dist, target));
        foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories)) ClearDownloadMark(file);
        UpdateResume.MarkComplete(target, release.Version);
        TryDelete(() => Directory.Delete(temp, true));
        TryDelete(() => File.Delete(zip));
        return target;
    }

    /// <summary>
    /// Builds &lt;versions&gt;\&lt;version&gt; from the installed files that are unchanged and downloads only the rest
    /// (<see cref="DeltaUpdate"/>). False when that isn't possible (an older release without a file list, a failed
    /// download, a file that doesn't match its checksum): the whole zip is downloaded instead.
    /// </summary>
    private static async Task<bool> TryChangedFilesAsync(Release release, string target, string versionsFolder, UpdateProgress progress, CancellationToken ct)
    {
        var v = release.Version;
        var temp = target + ".delta";
        try
        {
            using var http = CreateClient();
            string json;
            using (var r = await http.GetAsync(DeltaUpdate.ManifestUrl(release.ZipUrl), ct).ConfigureAwait(false))
            {
                if (!r.IsSuccessStatusCode) return false;
                json = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            if (DeltaUpdate.Parse(json) is not { } files) return false;
            progress.Report(UpdateStage.Downloading, "Finding the files that changed…", v);
            var local = DeltaUpdate.LocalCopies(files, InstalledVersionFolders(versionsFolder, target), McpFolder);
            var changed = files.Where(e => !local.ContainsKey(e.Path)).GroupBy(e => e.Sha256).Sum(g => g.First().Size);
            progress.Report(UpdateStage.Downloading,
                $"Downloading only the files that changed in Sketch ProMax {v} ({UpdateSnapshot.Megabytes(changed)} before compression)…", v);
            await DeltaUpdate.AssembleAsync(files, local, temp, release.ZipUrl, async (url, t) =>
            {
                var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, t).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            }, progress.ReportBytes, ct).ConfigureAwait(false);
            progress.Report(UpdateStage.Unpacking, $"Putting Sketch ProMax {v} together…");
            if (Directory.Exists(target)) UpdateResume.WithRetry(() => Directory.Delete(target, true));
            UpdateResume.WithRetry(() => Directory.Move(temp, target));
            foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories)) ClearDownloadMark(file);
            UpdateResume.MarkComplete(target, v);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWarning($"Downloading only the changed files of {v} failed, downloading the whole package instead: {ex.Message}");
            return false;
        }
    }

    /// <summary>Version folders whose files can be reused: the running one first, then every complete one.</summary>
    private static IEnumerable<string> InstalledVersionFolders(string versionsFolder, string target)
    {
        // The running core's Revit20xx folder sits in its version's folder (empty Location: loaded from bytes).
        var location = typeof(Updater).Assembly.Location;
        if (location.Length > 0 && Path.GetDirectoryName(Path.GetDirectoryName(location)!) is { Length: > 0 } running) yield return running;
        foreach (var root in new[] { versionsFolder, VersionsFolder }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var folder in Directory.GetDirectories(root).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
                if (!folder.Equals(target, StringComparison.OrdinalIgnoreCase) && UpdateResume.IsComplete(folder)) yield return folder;
        }
    }

    /// <summary>
    /// Checks a downloaded zip against the published digest, or (without one) that it opens and holds what an update
    /// needs. Returns its SHA-256; throws <see cref="InvalidDataException"/> when it is damaged or incomplete.
    /// </summary>
    private static string Verify(string zip, Release release)
    {
        var actual = UpdateResume.Sha256Of(zip);
        if (release.Sha256 is { } expected)
        {
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update is damaged (checksum mismatch). The next try downloads it again.");
            return actual;
        }
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        if (!names.Any(n => n.Contains("dist/") && n.EndsWith("/SketchProMax.dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The downloaded update is incomplete (no SketchProMax.dll). The next try downloads it again.");
        return actual;
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
        // Loaders moved aside by PlaceLoader; one Revit still has loaded stays until it closes.
        if (Directory.Exists(RevitAddinsRoot))
            foreach (var revitFolder in Directory.GetDirectories(RevitAddinsRoot))
            {
                var folder = Path.Combine(revitFolder, AddinFiles.LoaderFolder);
                if (!Directory.Exists(folder)) continue;
                foreach (var old in Directory.GetFiles(folder, "SketchProMax.Loader.*.old").Concat(Directory.GetFiles(folder, "SketchProMax.Loader.*.new")))
                    TryDelete(() => File.Delete(old));
            }

        if (!Directory.Exists(VersionsFolder)) return;
        var running = Path.GetDirectoryName(typeof(Updater).Assembly.Location) ?? "";
        var loaderPath = AppDomain.CurrentDomain.GetData(LoaderPathKey) as string ?? "";
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CurrentText };
        var saved = LoadState();
        if (saved.Staged is { } staged) keep.Add(staged);
        // What a try of the newest release has done so far (unpacked, or partly), so the next try continues it.
        var latest = saved.Latest is { } l && Parse(l) > Current ? l : null;
        if (latest is not null) keep.Add(latest);
        if (latest is not null) keep.Add(latest + ".partial");
        if (latest is not null) keep.Add(latest + ".delta");
        // Downloads (whole or partial) of any other version are no longer wanted.
        if (Directory.Exists(UpdatesFolder))
            foreach (var file in Directory.GetFiles(UpdatesFolder, "SketchProMax-*"))
            {
                var name = Path.GetFileName(file);
                if (latest is not null && name.StartsWith($"SketchProMax-{latest}.zip", StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(() => File.Delete(file));
            }

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
    internal static void ClearDownloadMark(string path)
    {
        try { DeleteFileNative(path + ":Zone.Identifier"); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static Version Parse(string text) => Normalize(Version.TryParse(text, out var v) ? v : null);

    private static Version Normalize(Version? v) => v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    private sealed class State
    {
        public DateTime? LastCheck { get; set; }
        public string? Staged { get; set; }
        public string? LastError { get; set; }
        public string? Latest { get; set; }
        public string? Notes { get; set; }
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
