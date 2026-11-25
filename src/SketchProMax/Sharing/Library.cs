using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SketchProMax.Licensing;
using SketchProMax.Settings;
using SketchProMax.Tools;

namespace SketchProMax.Sharing;

/// <summary>
/// The owner's library on both sides (format and lock in <see cref="LibraryMath"/>). All of it runs off Revit's thread,
/// in the background loop (App.LibraryLoopAsync); nothing here can block or cancel a Revit command.
///
/// Owner's PC: <see cref="Publish"/> packs %APPDATA%\SketchProMax's ready-made tools, recipes, kept code, job recipes and
/// checklist, and, when that changed since the last upload, locks it and uploads it to the "library" release (a
/// pre-release, so updates never take it for the latest version) with the GitHub token the License manager keeps.
///
/// Licensed PCs whose key carries the library key: <see cref="Receive"/> downloads it when its stamp changed, opens it into
/// %APPDATA%\SketchProMax\library, installs the ready-made tools and recipes (never over the PC's own or ones changed
/// there) and the checklist (unless the PC has its own). Kept code and job recipes are read from there as they are
/// (WorkedCode, JobRecipes), beside the PC's own.
/// </summary>
internal static class Library
{
    private static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");

    /// <summary>Where a licensed PC keeps the library it got (the owner's own files stay where they are).</summary>
    public static string Folder => Path.Combine(Roaming, "library");

    private static string PublishedFile => Path.Combine(Roaming, "library-published.txt");
    private static string AppliedFile => Path.Combine(Folder, "stamp.txt");
    private static string GivenFile => Path.Combine(Roaming, "library-given.json");

    private const string Repo = ChangesPublisher.Repo;
    private static string Download(string asset) => $"https://github.com/{Repo}/releases/download/{LibraryMath.Tag}/{asset}";

    /// <summary>The last thing that happened, for the License manager and the field test ("" before the first round).</summary>
    public static string LastNote { get; private set; } = "";

    // ---- The owner's PC ------------------------------------------------------------------------------------------------

    /// <summary>The library files on this PC (relative path and bytes). A file in use is skipped this time.</summary>
    internal static List<(string Relative, byte[] Data)> Collect(string root)
    {
        var files = new List<(string, byte[])>();
        foreach (var (folder, pattern, deep) in LibraryMath.Parts)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, pattern, deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                var rel = folder + "/" + file.Substring(dir.Length + 1).Replace('\\', '/');
                if (LibraryMath.Leave(rel)) continue;
                try { files.Add((rel, File.ReadAllBytes(file))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        var checklist = Path.Combine(root, LibraryMath.ChecklistFile);
        try { if (File.Exists(checklist)) files.Add((LibraryMath.ChecklistFile, File.ReadAllBytes(checklist))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>
    /// Uploads the library when it changed since the last upload. Owner's PC only, with the signing secret and a GitHub
    /// token on it; otherwise only notes why not. Never throws.
    /// </summary>
    public static async Task Publish(CancellationToken ct)
    {
        using var gate = Gate();
        if (gate is null) return; // another Revit window on this PC is doing it
        try
        {
            if (!LicenseState.IsOwnerPc) return;
            var secret = LicenseState.SigningSecret();
            var key = LicenseKey.LibraryBytes(LicenseKey.LibraryKey(secret));
            if (key is null) { LastNote = "Your library isn't uploaded: License manager needs your signing secret once."; return; }
            var token = ChangesPublisher.Token();
            if (token is null) { LastNote = "Your library isn't uploaded yet: add your GitHub token in License manager (once)."; return; }

            // Done jobs whose steps were never read: read now, so the ones on this PC travel with their steps.
            JobRecipes.FillSteps();
            var files = Collect(Roaming);
            var fingerprint = LibraryMath.Fingerprint(files);
            if (ReadText(PublishedFile)?.Split('|')[0] == fingerprint) return;
            var summary = LibraryMath.Summary(files.Select(f => f.Relative));
            var locked = LibraryMath.Lock(LibraryMath.Pack(files), key);
            var stamp = LibraryMath.Stamp(fingerprint, DateTime.UtcNow, summary);
            await UploadAsync(token, locked, stamp, ct).ConfigureAwait(false);
            SafeFile.Write(PublishedFile, stamp);
            LastNote = $"Your library was uploaded, locked, {DateTime.Now:d MMM HH:mm}: {summary}.";
            Note(LastNote);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastNote = "Your library couldn't be uploaded this time (" + ex.Message + "). It is tried again later.";
        }
    }

    /// <summary>When the library was last uploaded and what it held, for the License manager ("" when never).</summary>
    public static string PublishedText()
    {
        var s = LibraryMath.ReadStamp(ReadText(PublishedFile));
        if (s is null) return LastNote;
        var when = DateTime.TryParse(s.Value.Made, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("d MMM yyyy HH:mm") : s.Value.Made;
        return $"Your library (locked, only PCs with your keys can open it): uploaded {when}, {s.Value.Summary}. It uploads by itself when it changes.";
    }

    private static async Task UploadAsync(string token, byte[] locked, string stamp, CancellationToken ct)
    {
#if NETFRAMEWORK
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SketchProMax-Library");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var api = $"https://api.github.com/repos/{Repo}/releases";

        string id;
        using (var got = await http.GetAsync($"{api}/tags/{LibraryMath.Tag}", ct).ConfigureAwait(false))
        {
            var body = await got.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (got.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var create = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["tag_name"] = LibraryMath.Tag, ["target_commitish"] = "main", ["name"] = "Sketch ProMax library (locked)",
                    ["body"] = "The owner's library, locked: only Sketch ProMax on a PC with the owner's key can open it. Not a version to install.",
                    ["prerelease"] = true, ["make_latest"] = "false",
                });
                using var made = await http.PostAsync(api, new StringContent(create, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                body = await made.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!made.IsSuccessStatusCode) throw new InvalidOperationException(Refused(made.StatusCode, "making the library release"));
            }
            else if (!got.IsSuccessStatusCode) throw new InvalidOperationException(Refused(got.StatusCode, "reading the library release"));
            using var doc = JsonDocument.Parse(body);
            id = doc.RootElement.GetProperty("id").GetRawText();
            // The old files go first (an asset's name can't be taken twice).
            if (doc.RootElement.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    if (name is not (LibraryMath.Asset or LibraryMath.StampAsset)) continue;
                    using var gone = await http.DeleteAsync($"{api}/assets/{a.GetProperty("id").GetRawText()}", ct).ConfigureAwait(false);
                }
        }
        // The big file first, the stamp last: a PC that reads the new stamp finds the new file.
        await PutAsset(http, id, LibraryMath.Asset, locked, "application/octet-stream", ct).ConfigureAwait(false);
        await PutAsset(http, id, LibraryMath.StampAsset, Encoding.UTF8.GetBytes(stamp), "text/plain", ct).ConfigureAwait(false);
    }

    private static async Task PutAsset(HttpClient http, string releaseId, string name, byte[] data, string type, CancellationToken ct)
    {
        var url = $"https://uploads.github.com/repos/{Repo}/releases/{releaseId}/assets?name={Uri.EscapeDataString(name)}";
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        using var sent = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        if (!sent.IsSuccessStatusCode) throw new InvalidOperationException(Refused(sent.StatusCode, "uploading " + name));
    }

    private static string Refused(System.Net.HttpStatusCode code, string what) => code switch
    {
        System.Net.HttpStatusCode.Unauthorized => "GitHub refused the token (it may have expired); add a new one in License manager",
        System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound =>
            "the GitHub token can't write to the Sketch ProMax repository; it needs Contents: Read and write",
        _ => $"GitHub answered {(int)code} when {what}",
    };

    // ---- A licensed PC ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Gets the owner's library when it changed, on a licensed PC whose key carries the library key; puts its tools,
    /// recipes and checklist in place. Never throws; a failure is tried again at the next round.
    /// </summary>
    public static async Task Receive(CancellationToken ct)
    {
        using var gate = Gate();
        if (gate is null) return; // another Revit window on this PC is doing it
        try
        {
            var status = LicenseState.Current;
            if (status.Kind != LicenseState.Kind.Licensed) return;
            var key = LicenseKey.LibraryBytes(status.License?.Library);
            if (key is null) { LastNote = "This PC's key was made before the shared library; ask for a new key to get it."; return; }
#if NETFRAMEWORK
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SketchProMax-Library");
            string stampText;
            using (var s = await http.GetAsync(Download(LibraryMath.StampAsset) + "?t=" + DateTime.UtcNow.Ticks, ct).ConfigureAwait(false))
            {
                if (!s.IsSuccessStatusCode) return; // none uploaded yet
                stampText = (await s.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
            }
            if (LibraryMath.ReadStamp(stampText) is not { } stamp) return;
            if (ReadText(AppliedFile)?.Split('|')[0] == stamp.Fingerprint) return;

            byte[] data;
            using (var got = await http.GetAsync(Download(LibraryMath.Asset), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!got.IsSuccessStatusCode) return;
                if (got.Content.Headers.ContentLength is > LibraryMath.MaxBytes) return;
                data = await got.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            var plain = LibraryMath.Unlock(data, key);
            if (plain is null) { LastNote = "The shared library couldn't be opened with this PC's key (it may be newer than the key); ask for a new key."; return; }
            var files = LibraryMath.Unpack(plain);
            if (LibraryMath.Fingerprint(files) != stamp.Fingerprint) return; // the stamp and the file are from different uploads: next round
            WriteFolder(files);
            var notes = Apply(Folder);
            SafeFile.Write(AppliedFile, stampText);
            LastNote = $"Got the shared library ({stamp.Summary}).";
            Note(LastNote + (notes.Count > 0 ? " " + string.Join("; ", notes) : ""));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastNote = "The shared library couldn't be fetched this time (" + ex.Message + ").";
        }
    }

    /// <summary>The library opened into <see cref="Folder"/> afresh: written beside it first, then swapped in.</summary>
    private static void WriteFolder(List<(string Relative, byte[] Data)> files)
    {
        var fresh = Folder + ".new";
        if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        foreach (var (rel, data) in files)
        {
            var path = Path.Combine(fresh, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        var old = Folder + ".old";
        if (Directory.Exists(old)) Directory.Delete(old, true);
        if (Directory.Exists(Folder)) Directory.Move(Folder, old);
        Directory.Move(fresh, Folder);
        try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Ready-made tools and recipes from the library folder put where Sketch ProMax reads them (<see cref="LibraryMath.Decide"/>),
    /// and the checklist unless this PC has its own. What was kept back and why.
    /// </summary>
    internal static List<string> Apply(string folder)
    {
        var notes = new List<string>();
        var given = ReadGiven();
        foreach (var (part, target) in new[] { ("learned-tools", LearnedTools.Folder), ("recipes", Recipes.Folder) })
        {
            var dir = Path.Combine(folder, part);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var name = Path.GetFileName(file);
                if (part == "learned-tools" && !LearnedTools.ValidName.IsMatch(Path.GetFileNameWithoutExtension(name))) continue;
                var text = File.ReadAllText(file);
                var local = Path.Combine(target, name);
                var key = part + "/" + name;
                var localHash = File.Exists(local) ? LibraryMath.Hash(File.ReadAllText(local)) : null;
                var libraryHash = LibraryMath.Hash(text);
                given.TryGetValue(key, out var had);
                var what = LibraryMath.Decide(localHash, had, libraryHash);
                if (what == "keep")
                {
                    if (localHash == libraryHash)
                    {
                        given[key] = libraryHash;
                        if (part == "learned-tools") try { LearnedTools.MarkApproved(LearnedTool.FromJson(text).Code); } catch (Exception) { }
                    }
                    else if (localHash is not null) notes.Add($"kept this PC's own {Path.GetFileNameWithoutExtension(name)}");
                    continue;
                }
                Directory.CreateDirectory(target);
                SafeFile.Write(local, text);
                given[key] = libraryHash;
                // The library is signed by the owner's key (its MAC), so the owner's tools run as on the owner's PC,
                // without asking first (the owner, 25 Nov 2026: "work with him as it works with me exactly").
                if (part == "learned-tools") try { LearnedTools.MarkApproved(LearnedTool.FromJson(text).Code); } catch (Exception) { }
            }
        }
        WriteGiven(given);
        if (ApplyChecklist(folder) is { Length: > 0 } note) notes.Add(note);
        return notes;
    }

    private static string ApplyChecklist(string folder)
    {
        var file = Path.Combine(folder, LibraryMath.ChecklistFile);
        if (!File.Exists(file)) return "";
        var settings = AgentSettings.Load();
        var savedExists = File.Exists(ChecklistStore.SavedPath);
        string? savedSource = null;
        if (savedExists)
            try { savedSource = ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(ChecklistStore.SavedPath))?.SourcePath; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        var (write, why) = SharedPackMath.DecideChecklist(LibraryMath.Tag, savedExists, savedSource, settings.ChecklistPath);
        if (!write) return "checklist: " + why;
        try
        {
            var def = ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(file));
            if (def is null || !def.Items.Any()) return "";
            def.SourcePath = SharedPackMath.ChecklistSource(LibraryMath.Tag);
            ChecklistStore.Save(def);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return "checklist: couldn't be saved (" + ex.Message + ")"; }
    }

    private static Dictionary<string, string> ReadGiven()
    {
        try { return File.Exists(GivenFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(GivenFile)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private static void WriteGiven(Dictionary<string, string> given) => SafeFile.Write(GivenFile, JsonSerializer.Serialize(given));

    /// <summary>One Revit window at a time does the library's work (bug 138: two windows at once wiped the done jobs' index).</summary>
    private static FileStream? Gate()
    {
        try
        {
            Directory.CreateDirectory(Roaming);
            return new FileStream(Path.Combine(Roaming, "library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadText(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Note(string line)
    {
        try { ActivityLog.Note("library", line); } catch (Exception) { }
    }
}
