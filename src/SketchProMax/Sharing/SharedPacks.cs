using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SketchProMax.Settings;
using SketchProMax.Tools;
using static SketchProMax.Sharing.SharedPackMath;

namespace SketchProMax.Sharing;

/// <summary>
/// Shared tool packs on disk, on both sides (the format is in <see cref="SharedPackMath"/>).
///
/// Sharing (Sketch ProMax, Share tools): <see cref="Export"/> writes "TritoRevit - &lt;name&gt;.zip" (the pack, the
/// installer and a readme) and, when an update folder is chosen (a shared drive, a synced Teams or OneDrive folder),
/// publishes the pack there. Each export of the same pack is the next revision. <see cref="AutoPublish"/> publishes a
/// new revision by itself when a learned tool, recipe or checklist in a published pack changes on the sharer's PC.
///
/// Receiving (TritoRevit, or Sketch ProMax when it runs the TritoRevit tab): packs live in %APPDATA%\TritoRevit\packs.
/// <see cref="Sync"/> copies in a later revision from a pack's update folder and <see cref="Apply"/> puts its learned
/// tools, recipes and checklist where the add-in reads them, never over a file the user made or changed. The TritoRevit
/// add-in sees the buttons file change and updates the tab, with Revit open.
/// No Revit API here.
/// </summary>
internal static class SharedPacks
{
    /// <summary>For the field test: a folder that stands in for %APPDATA% on this thread only, so a test pack touches nothing real.</summary>
    [ThreadStatic] internal static string? TestRoot;

    public static string Root => TestRoot is { } test ? Path.Combine(test, "TritoRevit")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TritoRevit");

    public static string PacksFolder => Path.Combine(Root, "packs");

    private static string LedgerFile => Path.Combine(Root, "installed-files.json");

    /// <summary>What this PC shared: one record per pack (<see cref="Record"/>).</summary>
    public static string RecordsFolder => TestRoot is { } test ? Path.Combine(test, "shared-packs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "shared-packs");

    /// <summary>Where Share tools writes its zips unless the user picks another folder.</summary>
    public static string DefaultZipFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Shared tools");

    private const string AppliedFile = "applied.txt";

    private static readonly object Gate = new();

    // ---- Receiving ----------------------------------------------------------------------------------------------------

    public sealed record Installed(Pack Pack, string Folder, string Notes, bool Applied);

    /// <summary>The installed packs, by name. Never throws.</summary>
    public static List<Installed> List()
    {
        var list = new List<Installed>();
        try
        {
            if (!Directory.Exists(PacksFolder)) return list;
            foreach (var dir in Directory.GetDirectories(PacksFolder))
            {
                if (Path.GetFileName(dir).IndexOf('.') >= 0) continue;
                var pack = Read(dir, out _);
                if (pack is null || pack.Id != Path.GetFileName(dir)) continue;
                var (revision, notes) = ReadApplied(dir);
                list.Add(new Installed(pack, dir, notes, revision == pack.Revision));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.OrderBy(i => i.Pack.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Any()
    {
        try { return Directory.Exists(PacksFolder) && Directory.GetDirectories(PacksFolder).Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static Pack? Read(string folder, out string why)
    {
        why = "";
        var file = Path.Combine(folder, PackFile);
        try
        {
            if (!File.Exists(file)) { why = "it has no pack.json"; return null; }
            return Parse(File.ReadAllText(file), out why);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { why = ex.Message; return null; }
    }

    /// <summary>The installed pack <paramref name="id"/>, or null.</summary>
    public static Pack? Find(string id) => ValidId(id) ? Read(Path.Combine(PacksFolder, id), out _) : null;

    /// <summary>
    /// Looks at every installed pack's update folder and copies in a later revision, then applies every pack not yet
    /// applied at its revision. Returns one line per pack updated (empty when nothing changed). Never throws. Runs on a
    /// background thread: an update folder on a network drive that is offline only costs that pack its check.
    /// </summary>
    public static List<string> Sync(CancellationToken stop = default)
    {
        var changed = new List<string>();
        foreach (var installed in List())
        {
            if (stop.IsCancellationRequested) break;
            var pack = installed.Pack;
            try
            {
                if (pack.UpdateFrom.Length > 0)
                {
                    var source = Path.Combine(pack.UpdateFrom, pack.Id);
                    var published = Directory.Exists(source) ? Read(source, out _) : null;
                    if (IsNewer(published, pack) && CopyIn(source, pack.Id, out _) is { } updated)
                    {
                        changed.Add($"{updated.Name}: revision {updated.Revision}");
                        pack = updated;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            try { Apply(pack.Id); } catch (Exception) { /* tried again at the next sync */ }
        }
        return changed;
    }

    /// <summary>
    /// Installs (or updates) a pack from a folder holding its pack.json: copied to packs\&lt;id&gt; through a temporary
    /// folder, so the add-in never sees half a pack. Only the pack's own kinds of files are copied. Returns the pack, or
    /// null with why.
    /// </summary>
    public static Pack? CopyIn(string source, string? expectedId, out string why)
    {
        var pack = Read(source, out why);
        if (pack is null) return null;
        if (expectedId is not null && pack.Id != expectedId) { why = $"the folder holds the pack '{pack.Id}', not '{expectedId}'"; return null; }
        lock (Gate)
        {
            Directory.CreateDirectory(PacksFolder);
            var target = Path.Combine(PacksFolder, pack.Id);
            var incoming = target + ".incoming";
            var old = target + ".old";
            if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
            Directory.CreateDirectory(incoming);
            foreach (var file in PackFiles(source))
            {
                var to = Path.Combine(incoming, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(source, file.Replace('/', Path.DirectorySeparatorChar)), to, true);
            }
            // The pack's buttons file is written here from pack.json if the source has none, so the tab always matches it.
            var buttons = Path.Combine(incoming, ButtonsFile);
            if (!File.Exists(buttons)) File.WriteAllText(buttons, ButtonsText(pack), new UTF8Encoding(false));
            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (Directory.Exists(target)) Directory.Move(target, old);
            Directory.Move(incoming, target);
            try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return pack;
    }

    /// <summary>The files of a pack folder that belong to it (relative, "/"-separated): pack.json, buttons.txt, checklist.json and the tool files.</summary>
    private static IEnumerable<string> PackFiles(string folder)
    {
        foreach (var name in new[] { PackFile, ButtonsFile, ChecklistFile, SubmissionTemplateFile })
            if (File.Exists(Path.Combine(folder, name))) yield return name;
        foreach (var sub in new[] { LearnedFolder, RecipesFolder })
        {
            var dir = Path.Combine(folder, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.json")) yield return sub + "/" + Path.GetFileName(file);
        }
    }

    /// <summary>
    /// Installs a pack from a "TritoRevit - ….zip" (its "pack" folder) and applies it. Returns the pack, or null with why.
    /// </summary>
    public static Pack? InstallZip(string zip, out string why)
    {
        var temp = Path.Combine(Path.GetTempPath(), "TritoRevit-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entries = archive.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("pack/", StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0).ToList();
                if (!entries.Any(e => e.FullName.Replace('\\', '/').Equals("pack/" + PackFile, StringComparison.OrdinalIgnoreCase)))
                {
                    why = "this zip is not a shared tools pack (it has no pack\\pack.json)";
                    return null;
                }
                foreach (var entry in entries)
                {
                    var relative = entry.FullName.Replace('\\', '/').Substring(5);
                    if (!SafeRelative(relative)) continue;
                    var to = Path.Combine(temp, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    entry.ExtractToFile(to, true);
                }
            }
            var pack = CopyIn(temp, null, out why);
            if (pack is not null) Apply(pack.Id, force: true);
            return pack;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            why = ex.Message;
            return null;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Puts the pack's learned tools, recipes and checklist in place (see <see cref="Decide"/>), removes the ones a
    /// previous revision put there that it no longer has, and notes what was kept and why. Does nothing when it is
    /// already applied at this revision, unless <paramref name="force"/>. Returns the notes ("" when all went in).
    /// </summary>
    public static string Apply(string id, bool force = false)
    {
        lock (Gate)
        {
            var folder = Path.Combine(PacksFolder, id);
            var pack = Read(folder, out _);
            if (pack is null) return "";
            var (applied, _) = ReadApplied(folder);
            if (!force && applied == pack.Revision) return "";
            var notes = new List<string>();
            var ledger = ReadLedger();
            var wrote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in pack.Tools)
            {
                if (tool.File is null || tool.Kind is not ("learned" or "recipe")) continue;
                var source = Path.Combine(folder, tool.File.Replace('/', Path.DirectorySeparatorChar));
                string? target;
                if (tool.Kind == "learned") target = LearnedTools.ValidName.IsMatch(tool.Name) ? LearnedTools.PathFor(tool.Name) : null;
                else target = tool.Name.Trim().Length > 0 ? Recipes.PathFor(tool.Name) : null;
                if (target is null || !File.Exists(source)) { notes.Add($"{tool.Title}: its file is missing from the pack"); continue; }
                var text = File.ReadAllText(source);
                if (tool.Kind == "learned" && !LearnedNameMatches(text, tool.Name)) { notes.Add($"{tool.Title}: its file names another tool, so it was not installed"); continue; }
                var sourceHash = Hash(text);
                var targetHash = File.Exists(target) ? Hash(File.ReadAllText(target)) : null;
                var key = target.ToLowerInvariant();
                ledger.TryGetValue(key, out var owner);
                var (step, why) = Decide(pack.Id, sourceHash, targetHash, owner);
                if (step == Step.Keep) { notes.Add($"{tool.Title}: {why}"); continue; }
                if (step == Step.Copy)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    SafeFile.Write(target, text);
                }
                ledger[key] = new Owned(pack.Id, sourceHash);
                wrote.Add(key);
            }
            // Tools an earlier revision of this pack put in place and this one no longer has: removed, unless changed here.
            foreach (var pair in ledger.Where(p => p.Value.Pack == pack.Id && !wrote.Contains(p.Key)).ToList())
            {
                ledger.Remove(pair.Key);
                try
                {
                    if (File.Exists(pair.Key) && Hash(File.ReadAllText(pair.Key)) == pair.Value.Hash) File.Delete(pair.Key);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            WriteLedger(ledger);
            if (ApplyChecklist(pack, folder) is { Length: > 0 } checklistNote) notes.Add(checklistNote);
            ApplySubmissionTemplate(pack, folder);
            var text2 = string.Join("\n", notes);
            WriteApplied(folder, pack.Revision, text2);
            return text2;
        }
    }

    /// <summary>A learned tool's file must carry its own name ("name" in the JSON), or the add-in won't run it under that name.</summary>
    private static bool LearnedNameMatches(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("name", out var n) && n.GetString() == name;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// The sharer's checklist, when the pack carries one (Include my checklist): put in place unless this PC has its own
    /// (<see cref="DecideChecklist"/>). When the pack no longer carries one, the copy it put here goes and the built-in
    /// checklist is used again. Users' project checklists are separate files and never touched.
    /// </summary>
    private static string ApplyChecklist(Pack pack, string folder)
    {
        var settings = AgentSettings.Load();
        var savedExists = File.Exists(ChecklistStore.SavedPath);
        string? savedSource = null;
        if (savedExists)
            try { savedSource = ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(ChecklistStore.SavedPath))?.SourcePath; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        var mine = savedSource == ChecklistSource(pack.Id);
        if (pack.Checklist is null || !pack.Tools.Any(t => t.Name == "pre-submission"))
        {
            if (mine) try { ChecklistStore.Reset(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return "";
        }
        var (write, why) = DecideChecklist(pack.Id, savedExists, savedSource, settings.ChecklistPath);
        if (!write) return "Pre-submission checklist: " + why;
        var file = Path.Combine(folder, pack.Checklist.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var def = File.Exists(file) ? ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(file)) : null;
            if (def is null || !def.Items.Any()) return "Pre-submission checklist: the shared checklist can't be read, so the one here is used";
            def.SourcePath = ChecklistSource(pack.Id);
            def.SourceStamp = pack.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ChecklistStore.Save(def);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "Pre-submission checklist: couldn't be saved (" + ex.Message + ")";
        }
    }

    /// <summary>The sharer's submission checklist questions, when the pack carries them: kept on this PC when newer than the ones here.</summary>
    private static void ApplySubmissionTemplate(Pack pack, string folder)
    {
        if (pack.SubmissionTemplate is null || !pack.Tools.Any(t => t.Name == "submission-checklist")) return;
        try
        {
            var file = Path.Combine(folder, pack.SubmissionTemplate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file)) SubmissionChecklistTemplate.TakeIfNewer(SubmissionChecklistTemplate.Read(File.ReadAllText(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the questions here are used */ }
    }

    /// <summary>Removes an installed pack: its buttons go from the tab, and the files it put in place (unless changed here).</summary>
    public static void Remove(string id)
    {
        if (!ValidId(id)) return;
        lock (Gate)
        {
            var ledger = ReadLedger();
            foreach (var pair in ledger.Where(p => p.Value.Pack == id).ToList())
            {
                ledger.Remove(pair.Key);
                try { if (File.Exists(pair.Key) && Hash(File.ReadAllText(pair.Key)) == pair.Value.Hash) File.Delete(pair.Key); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            WriteLedger(ledger);
            try
            {
                if (File.Exists(ChecklistStore.SavedPath)
                    && ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(ChecklistStore.SavedPath))?.SourcePath == ChecklistSource(id))
                    ChecklistStore.Reset();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            var folder = Path.Combine(PacksFolder, id);
            var doomed = folder + ".removed";
            if (Directory.Exists(doomed)) Directory.Delete(doomed, true);
            if (Directory.Exists(folder)) Directory.Move(folder, doomed);
            try { Directory.Delete(doomed, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static (int Revision, string Notes) ReadApplied(string folder)
    {
        try
        {
            var file = Path.Combine(folder, AppliedFile);
            if (!File.Exists(file)) return (-1, "");
            var text = File.ReadAllText(file).Replace("\r", "");
            var cut = text.IndexOf('\n');
            var first = cut < 0 ? text : text.Substring(0, cut);
            return (int.TryParse(first.Trim(), out var r) ? r : -1, cut < 0 ? "" : text.Substring(cut + 1).Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (-1, ""); }
    }

    private static void WriteApplied(string folder, int revision, string notes) =>
        File.WriteAllText(Path.Combine(folder, AppliedFile), revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + notes);

    private static Dictionary<string, Owned> ReadLedger()
    {
        try
        {
            if (SafeFile.Read(LedgerFile) is { } json)
                return JsonSerializer.Deserialize<Dictionary<string, Owned>>(json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new Dictionary<string, Owned>(StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteLedger(Dictionary<string, Owned> ledger)
    {
        Directory.CreateDirectory(Root);
        SafeFile.Write(LedgerFile, JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- Sharing --------------------------------------------------------------------------------------------------------

    /// <summary>What this PC shared before, newest first.</summary>
    public static List<Record> Records()
    {
        var list = new List<Record>();
        try
        {
            if (!Directory.Exists(RecordsFolder)) return list;
            foreach (var file in Directory.GetFiles(RecordsFolder, "*.json"))
                if (ParseRecord(SafeFile.Read(file)) is { } r && r.Id == Path.GetFileNameWithoutExtension(file)) list.Add(r);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.OrderByDescending(r => r.Revision).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void SaveRecord(Record record)
    {
        Directory.CreateDirectory(RecordsFolder);
        SafeFile.Write(Path.Combine(RecordsFolder, record.Id + ".json"), SerializeRecord(record));
    }

    /// <summary>What Share tools is asked to share.</summary>
    public sealed record Request(string Name, IReadOnlyList<string> Items, string UpdateFrom, string Note, string ZipFolder, bool AutoPublish,
        bool IncludeChecklist = false);

    public sealed record Result(Pack Pack, string Zip, string? Published, List<string> Notes);

    /// <summary>
    /// The pack for <paramref name="request"/> and its files (relative path → text), without its revision: what is
    /// fingerprinted and written. <paramref name="catalog"/> describes the ribbon tools (title, glyph, tip).
    /// </summary>
    public static (Pack Pack, Dictionary<string, string> Files, List<string> Notes) Build(string id, Request request,
        Func<string, (string Title, string Glyph, string Tip)?> catalog, string version)
    {
        var notes = new List<string>();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        string? learned(string name)
        {
            if (!LearnedTools.ValidName.IsMatch(name) || LearnedTools.Load(name) is not { } tool) return null;
            if (Read(LearnedTools.PathFor(name)) is not { } text) return null;
            files[LearnedFolder + "/" + name + ".json"] = text;
            return tool.Summary + (tool.ModifiesModel ? " (changes the model)" : "");
        }
        string? recipe(string name)
        {
            if (Read(Recipes.PathFor(name)) is not { } text) return null;
            files[RecipesFolder + "/" + FileName(name) + ".json"] = text;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } description) return description;
                var steps = root.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0;
                return $"Recipe of {steps} step(s).";
            }
            catch (JsonException) { return "Recipe."; }
        }
        var tools = ToolsFor(request.Items, catalog, learned, recipe);
        foreach (var key in request.Items)
            if (tools.All(t => !KeyMatches(t, key))) notes.Add($"{key.Substring(key.IndexOf(':') + 1).Replace('_', ' ')}: not found here, so it was left out");
        // The sharer's checklist travels only when they ticked Include my checklist; otherwise colleagues use the built-in one.
        string? checklist = null;
        if (request.IncludeChecklist && tools.Any(t => t.Kind == "command" && t.Name == "pre-submission"))
        {
            var (def, note) = ChecklistStore.Load(AgentSettings.Load());
            if (def is not null)
            {
                files[ChecklistFile] = ChecklistMath.ToJson(def);
                checklist = ChecklistFile;
            }
            else notes.Add("Pre-submission checklist: " + (note.Length > 0 ? note : "no checklist could be read") + "; colleagues use the built-in one");
        }
        // The submission checklist's questions travel with it when the sharer changed them (the template is theirs to change).
        string? template = null;
        if (tools.Any(t => t.Kind == "command" && t.Name == "submission-checklist") && SubmissionChecklistTemplate.LoadPc() is { BuiltIn: false } mine)
        {
            files[SubmissionTemplateFile] = SubmissionChecklistMath.ToJson(mine);
            template = SubmissionTemplateFile;
        }
        var pack = new Pack
        {
            Id = id, Name = request.Name.Trim(), UpdateFrom = request.UpdateFrom.Trim(), Note = request.Note.Trim(),
            MinVersion = version, Tools = tools, Checklist = checklist, SubmissionTemplate = template,
        };
        return (pack, files, notes);
    }

    private static bool KeyMatches(Tool tool, string key) => key switch
    {
        _ when key.StartsWith("tool:", StringComparison.Ordinal) => tool.Name == key,
        _ when key.StartsWith("command:", StringComparison.Ordinal) => tool.Kind == "command" && tool.Name == key.Substring(8),
        _ when key.StartsWith("learned:", StringComparison.Ordinal) => tool.Kind == "learned" && tool.Name == key.Substring(8),
        _ when key.StartsWith("recipe:", StringComparison.Ordinal) => tool.Kind == "recipe" && tool.Name == key.Substring(7),
        _ => false,
    };

    /// <summary>
    /// Shares <paramref name="request"/>: the next revision of its pack, as a zip in its zip folder and, with an update
    /// folder, published there (with a copy of the zip for new colleagues). Throws IOException / UnauthorizedAccessException
    /// when the zip can't be written; a failure to publish is a note, and the zip is still made.
    /// </summary>
    public static Result Export(Request request, Func<string, (string Title, string Glyph, string Tip)?> catalog, string version, string? id = null)
    {
        id ??= Records().FirstOrDefault(r => string.Equals(r.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? Slug(request.Name);
        var (pack, files, notes) = Build(id, request, catalog, version);
        if (pack.Tools.Count == 0) throw new InvalidOperationException("None of the chosen tools could be shared.");
        var record = ParseRecord(SafeFile.Read(Path.Combine(RecordsFolder, id + ".json"))) ?? new Record { Id = id };
        var publishedRevision = 0;
        if (pack.UpdateFrom.Length > 0)
            try { publishedRevision = Read(Path.Combine(pack.UpdateFrom, id), out _)?.Revision ?? 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        pack.Revision = NextRevision(record.Revision, publishedRevision);
        pack.Published = DateTime.UtcNow;
        files[PackFile] = Serialize(pack);
        files[ButtonsFile] = ButtonsText(pack);

        Directory.CreateDirectory(request.ZipFolder);
        var zip = Path.Combine(request.ZipFolder, ZipName(pack.Name));
        WriteZip(zip, pack, files);

        string? published = null;
        if (pack.UpdateFrom.Length > 0)
        {
            try
            {
                Publish(pack, files);
                File.Copy(zip, Path.Combine(pack.UpdateFrom, ZipName(pack.Name)), true);
                published = Path.Combine(pack.UpdateFrom, pack.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                notes.Add($"The update folder couldn't be written ({ex.Message}). The zip is made; the people who have it get this revision once it is published there.");
            }
        }
        record.Name = pack.Name;
        record.Revision = pack.Revision;
        record.UpdateFrom = pack.UpdateFrom;
        record.Note = pack.Note;
        record.Items = request.Items.ToList();
        record.AutoPublish = request.AutoPublish;
        record.IncludeChecklist = request.IncludeChecklist;
        record.LastZip = zip;
        // Fingerprinted without the files that carry the revision and date.
        record.Fingerprint = Fingerprint(pack, files.Where(f => f.Key is not (PackFile or ButtonsFile)));
        SaveRecord(record);
        return new Result(pack, zip, published, notes);
    }

    /// <summary>
    /// For every pack this PC published with automatic updates on: when a learned tool, recipe or checklist in it (or
    /// a tool's wording) changed since, the next revision is published. Returns one line per pack published. Never throws.
    /// </summary>
    public static List<string> AutoPublish(Func<string, (string Title, string Glyph, string Tip)?> catalog, string version)
    {
        var done = new List<string>();
        foreach (var record in Records())
        {
            if (!record.AutoPublish || record.UpdateFrom.Length == 0) continue;
            try
            {
                if (!Directory.Exists(record.UpdateFrom)) continue; // offline: tried again later
                var request = new Request(record.Name, record.Items, record.UpdateFrom, record.Note,
                    record.LastZip.Length > 0 ? Path.GetDirectoryName(record.LastZip)! : DefaultZipFolder, true, record.IncludeChecklist);
                var (pack, files, _) = Build(record.Id, request, catalog, version);
                if (pack.Tools.Count == 0) continue;
                if (Fingerprint(pack, files.Where(f => f.Key is not (PackFile or ButtonsFile))) == record.Fingerprint) continue;
                var result = Export(request, catalog, version, record.Id);
                if (result.Published is not null) done.Add($"{result.Pack.Name}: revision {result.Pack.Revision}");
            }
            catch (Exception) { /* tried again at the next round */ }
        }
        return done;
    }

    /// <summary>Writes the pack into &lt;update folder&gt;\&lt;id&gt;: every other file first, pack.json last, so a PC that sees the new revision finds its files there.</summary>
    private static void Publish(Pack pack, Dictionary<string, string> files)
    {
        var folder = Path.Combine(pack.UpdateFrom, pack.Id);
        Directory.CreateDirectory(folder);
        foreach (var file in files.Where(f => f.Key != PackFile))
        {
            var path = Path.Combine(folder, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value, new UTF8Encoding(false));
        }
        // Tool files the previous revision had and this one doesn't.
        foreach (var stale in PackFiles(folder).Where(f => !files.ContainsKey(f)).ToList())
            try { File.Delete(Path.Combine(folder, stale.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        File.WriteAllText(Path.Combine(folder, PackFile), files[PackFile], new UTF8Encoding(false));
    }

    /// <summary>The zip: the installer at the top, the readme, and the pack under "pack/".</summary>
    private static void WriteZip(string zip, Pack pack, Dictionary<string, string> files)
    {
        var temp = zip + ".new";
        if (File.Exists(temp)) File.Delete(temp);
        using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(name.EndsWith(".ps1", StringComparison.Ordinal)));
                writer.Write(text);
            }
            Add("Install TritoRevit.cmd", Resource("install-tritorevit.cmd"));
            Add("install-tritorevit.ps1", Resource("install-tritorevit.ps1"));
            Add("README.txt", Readme(pack));
            foreach (var file in files.OrderBy(f => f.Key, StringComparer.Ordinal)) Add("pack/" + file.Key, file.Value);
        }
        if (File.Exists(zip)) File.Delete(zip);
        File.Move(temp, zip);
    }

    /// <summary>The installer files shipped in the add-in (Sharing\install-tritorevit.*).</summary>
    internal static string Resource(string name)
    {
        using var stream = typeof(SharedPacks).Assembly.GetManifestResourceStream("SketchProMax.Sharing." + name)
                           ?? throw new IOException($"The installer ({name}) is missing from this build of Sketch ProMax.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
