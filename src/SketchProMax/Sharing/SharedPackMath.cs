using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SketchProMax.Sharing;

/// <summary>
/// Shared tool packs: the format of what Share tools exports and TritoRevit installs. Pure: no Revit, no WPF, no files,
/// so the tests run all of it.
///
/// A pack is a folder (in the zip under "pack\", installed at %APPDATA%\TritoRevit\packs\&lt;id&gt;, published at
/// &lt;update folder&gt;\&lt;id&gt;):
///   pack.json      what it holds (<see cref="Pack"/>): its id, name, revision, update folder and tools;
///   buttons.txt    the same tools as ribbon lines for the TritoRevit add-in (<see cref="ButtonsText"/>), which has
///                  no JSON reader on Revit 2024; it reads them with TritoRevit's PackButtons;
///   learned\*.json, recipes\*.json  the learned tools and recipes in it, as Sketch ProMax saves them;
///   checklist.json the pre-submission checklist in use when it was shared, when that tool is in it.
/// A ribbon tool travels as its command only: its code is in the add-in, which updates itself from the releases.
/// </summary>
internal static class SharedPackMath
{
    public const int Format = 1;
    public const string PackFile = "pack.json", ButtonsFile = "buttons.txt", ChecklistFile = "checklist.json";
    public const string LearnedFolder = "learned", RecipesFolder = "recipes";

    /// <summary>The checklist's source when a pack put it there, so a later version of the pack may replace it.</summary>
    public static string ChecklistSource(string packId) => "shared:" + packId;

    public sealed class Pack
    {
        [JsonPropertyName("format")] public int FormatVersion { get; set; } = Format;
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("revision")] public int Revision { get; set; }
        [JsonPropertyName("published")] public DateTime Published { get; set; }
        /// <summary>The folder the pack is published to and updates come from (it holds &lt;id&gt;\pack.json); empty: no updates.</summary>
        [JsonPropertyName("updateFrom")] public string UpdateFrom { get; set; } = "";
        /// <summary>The version that exported it: its ribbon tools need at least this add-in version.</summary>
        [JsonPropertyName("minVersion")] public string MinVersion { get; set; } = "";
        [JsonPropertyName("note")] public string Note { get; set; } = "";
        [JsonPropertyName("tools")] public List<Tool> Tools { get; set; } = [];
        [JsonPropertyName("checklist")] public string? Checklist { get; set; }
    }

    public sealed class Tool
    {
        /// <summary>"cmd.&lt;command&gt;", "learned.&lt;name&gt;" or "recipe.&lt;name&gt;"; unique in the pack, the button's id.</summary>
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        /// <summary>"command" (a ribbon tool or window), "learned" or "recipe".</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("glyph")] public string Glyph { get; set; } = "";
        [JsonPropertyName("tip")] public string Tip { get; set; } = "";
        /// <summary>For "command": what the add-in's ribbon runs ("tool:check_model", "pre-submission"). Else the tool's name.</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("needsModel")] public bool NeedsModel { get; set; } = true;
        /// <summary>For learned tools and recipes: its file in the pack ("learned/x.json").</summary>
        [JsonPropertyName("file")] public string? File { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(Pack pack) => JsonSerializer.Serialize(pack, Json);

    /// <summary>A pack.json read back, or null when it isn't one this version can use (with why).</summary>
    public static Pack? Parse(string? json, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(json)) { why = "the pack file is empty"; return null; }
        Pack? pack;
        try { pack = JsonSerializer.Deserialize<Pack>(json!, Json); }
        catch (JsonException ex) { why = "the pack file can't be read: " + ex.Message; return null; }
        if (pack is null) { why = "the pack file is empty"; return null; }
        if (pack.FormatVersion > Format) { why = "it was shared from a newer version; it is read once this one updates"; return null; }
        if (!ValidId(pack.Id)) { why = $"its id '{pack.Id}' is not valid"; return null; }
        pack.Tools = (pack.Tools ?? []).Where(t => t is not null && ValidToolId(t.Id) && t.Kind is "command" or "learned" or "recipe")
            .GroupBy(t => t.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        foreach (var t in pack.Tools)
            if (t.File is { } f && !SafeRelative(f)) t.File = null;
        if (pack.Checklist is { } c && !SafeRelative(c)) pack.Checklist = null;
        return pack;
    }

    private static readonly Regex Id = new("^[a-z0-9][a-z0-9-]{0,47}$");
    private static readonly Regex ToolIdRule = new("^(cmd|learned|recipe)\\.[A-Za-z0-9_:.\\- ]{1,80}$");

    public static bool ValidId(string? id) => id is not null && Id.IsMatch(id);

    public static bool ValidToolId(string? id) => id is not null && ToolIdRule.IsMatch(id);

    /// <summary>A pack id from its name: lower case, words joined by "-" ("QA tools" → "qa-tools").</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in (name ?? "").Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            if (sb.Length >= 40) break;
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "shared-tools" : slug;
    }

    /// <summary>A relative path inside the pack: no rooted path, no "..", no drive or stream.</summary>
    public static bool SafeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path!.Replace('\\', '/');
        if (p.StartsWith("/", StringComparison.Ordinal) || p.Contains(':')) return false;
        return p.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
    }

    /// <summary>The tool ids for a command, a learned tool and a recipe.</summary>
    public static string CommandId(string command) => "cmd." + command;
    public static string LearnedId(string name) => "learned." + name;
    public static string RecipeId(string name) => "recipe." + name;

    /// <summary>What a button runs in the add-in: the ribbon command, or the My tools command of a learned tool or recipe.</summary>
    public static string? CommandFor(Tool tool) => tool.Kind switch
    {
        "command" => tool.Name.Length > 0 && tool.Name.IndexOf('\t') < 0 ? tool.Name : null,
        "learned" => "quick\tlearned\t" + tool.Name,
        "recipe" => "quick\trecipe\t" + tool.Name,
        _ => null,
    };

    // ---- The ribbon lines the TritoRevit add-in reads -------------------------------------------------------------

    public const string ButtonsHeader = "tritorevit-buttons\t1";

    /// <summary>
    /// buttons.txt: the header, one "pack" line (id, name, revision) and one "button" line per tool (id, text, glyph,
    /// tooltip, "model" when it needs an open model). Tabs separate fields; line breaks are "\n" and backslashes "\\".
    /// </summary>
    public static string ButtonsText(Pack pack)
    {
        var lines = new List<string> { ButtonsHeader, Join("pack", pack.Id, pack.Name, pack.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        foreach (var t in pack.Tools)
            lines.Add(Join("button", t.Id, ButtonText(t.Title), t.Glyph.Length > 0 ? t.Glyph : DefaultGlyph(t.Kind), t.Tip, t.NeedsModel ? "model" : ""));
        return string.Join("\n", lines) + "\n";
    }

    public static string DefaultGlyph(string kind) => kind switch { "learned" => "learned", "recipe" => "recipe", _ => "cube" };

    /// <summary>A button's label: one line, at most 32 characters (Revit cuts longer ones).</summary>
    public static string ButtonText(string title)
    {
        var one = string.Join(" ", (title ?? "").Replace("\r", " ").Replace("\n", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= 32 ? one : one.Substring(0, 31).TrimEnd() + "…";
    }

    private static string Join(params string[] fields) => string.Join("\t", fields.Select(Escape));

    public static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ");

    // ---- Updates -------------------------------------------------------------------------------------------------

    /// <summary>True when the published pack is a later revision of the installed one (same id).</summary>
    public static bool IsNewer(Pack? published, Pack? installed) =>
        published is not null && (installed is null || (published.Id == installed.Id && published.Revision > installed.Revision));

    /// <summary>The next revision for an export: one more than both what this PC exported and what is published.</summary>
    public static int NextRevision(int exported, int published) => Math.Max(Math.Max(exported, published), 0) + 1;

    /// <summary>
    /// What Share tools last published for a pack, kept on the sharer's PC: so Share tools offers it again, and the
    /// add-in publishes a new revision by itself when a learned tool, recipe or checklist in it changes (<see cref="Fingerprint"/>).
    /// </summary>
    public sealed class Record
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("revision")] public int Revision { get; set; }
        [JsonPropertyName("updateFrom")] public string UpdateFrom { get; set; } = "";
        [JsonPropertyName("note")] public string Note { get; set; } = "";
        /// <summary>The chosen items, as Favourites keys ("tool:check_model", "command:pre-submission", "learned:x", "recipe:y").</summary>
        [JsonPropertyName("items")] public List<string> Items { get; set; } = [];
        [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
        [JsonPropertyName("autoPublish")] public bool AutoPublish { get; set; } = true;
        [JsonPropertyName("lastZip")] public string LastZip { get; set; } = "";
    }

    public static string SerializeRecord(Record record) => JsonSerializer.Serialize(record, Json);

    public static Record? ParseRecord(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Record>(json!, Json) is { } r && ValidId(r.Id) ? r : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A fingerprint of what a pack carries, apart from its revision and date: the tool list and every file's content.
    /// Unchanged fingerprint, nothing new to publish.
    /// </summary>
    public static string Fingerprint(Pack pack, IEnumerable<KeyValuePair<string, string>> files)
    {
        var sb = new StringBuilder();
        sb.Append(pack.Id).Append('\n').Append(pack.Name).Append('\n').Append(pack.Note).Append('\n').Append(pack.UpdateFrom).Append('\n');
        foreach (var t in pack.Tools) sb.Append(t.Id).Append('|').Append(t.Title).Append('|').Append(t.Glyph).Append('|').Append(t.Tip).Append('\n');
        foreach (var f in files.OrderBy(f => f.Key, StringComparer.Ordinal)) sb.Append(f.Key).Append('=').Append(Hash(f.Value)).Append('\n');
        return Hash(sb.ToString());
    }

    public static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").ToLowerInvariant();
    }

    // ---- Installing learned tools and recipes from a pack ------------------------------------------------------------

    /// <summary>Who wrote a file a pack put in place, and what it was then.</summary>
    public sealed record Owned(string Pack, string Hash);

    public enum Step { Copy, Same, Keep }

    /// <summary>
    /// Whether a pack's file goes in place of <paramref name="targetHash"/> (null: no file there). A file the user made
    /// or changed is never overwritten: one with the same name that no pack wrote, or one a pack wrote and the user
    /// edited since, is kept (and the pack's copy skipped). A file another pack owns is that pack's.
    /// </summary>
    public static (Step Step, string Why) Decide(string packId, string sourceHash, string? targetHash, Owned? owner)
    {
        if (targetHash is null) return (Step.Copy, "");
        if (targetHash == sourceHash) return (Step.Same, "");
        if (owner is null) return (Step.Keep, "a tool of the same name made on this PC is kept");
        if (owner.Pack != packId) return (Step.Keep, $"the pack '{owner.Pack}' already has a tool of that name");
        if (owner.Hash != targetHash) return (Step.Keep, "it was changed on this PC, so that copy is kept");
        return (Step.Copy, "");
    }

    /// <summary>
    /// Whether the checklist saved on this PC goes back to the built-in one when a pack is applied. A pack shares the
    /// checklist window, never the sharer's checklist: colleagues start from the built-in checklist, and get the sharer's
    /// only by importing the Excel file the sharer exports (Edit the checklist &gt; Export to Excel). So a checklist an
    /// older pack put here (source "shared:&lt;pack&gt;", never changed here) goes; anything else stays.
    /// </summary>
    public static bool ResetsChecklist(string? savedSource) =>
        savedSource is not null && savedSource.StartsWith("shared:", StringComparison.Ordinal);

    // ---- What can be shared ------------------------------------------------------------------------------------------

    /// <summary>Ribbon tools that need the AI, so they can't run in TritoRevit: left out of Share tools.</summary>
    public static readonly HashSet<string> NeedsAi = new(StringComparer.Ordinal) { "redline_apply" };

    /// <summary>
    /// Windows and commands besides the tools with an options window that can be shared (command, title, glyph, tip).
    /// Anything about the chat, updates, settings, bugs or diagnostics stays with Sketch ProMax.
    /// </summary>
    public static readonly (string Command, string Title, string Glyph, string Tip)[] Commands =
    [
        ("pre-submission", "Pre-submission checklist", "list",
            "Goes through the office's pre-submission checklist stage by stage, with hints from the model, and makes the signed Word file. Colleagues start from the built-in checklist; to give them yours, export it to Excel and they import it."),
        ("issue-check", "Issue check", "check",
            "Drawing, spec, BOQ and model checks together on the issue set, pass or fail per sheet. Reads the model only."),
        ("cleanup", "Purge & cleanup", "refresh",
            "Purge unused, delete unused views, clean up imports or a model health clean. Each shows a preview first and asks before it deletes."),
        ("export-pack", "Export project pack", "pack",
            "Writes one small zip describing this project (data, quantities, sheets, revisions, schedules, check results). Read-only."),
        ("time-saved", "Time saved", "job",
            "Working time in this model and the time saved, per tool and per day. Exports to Excel."),
    ];

    /// <summary>Whether a ribbon command may go in a pack (a "tool:" one whose tool runs without the AI, or one of <see cref="Commands"/>).</summary>
    public static bool Shareable(string command) =>
        command.StartsWith("tool:", StringComparison.Ordinal) ? !NeedsAi.Contains(command.Substring(5)) && command.Length > 5
            : Commands.Any(c => c.Command == command);

    /// <summary>The tools of a pack made from <paramref name="items"/> (Favourites-style keys), in the order given; unknown keys are dropped.</summary>
    public static List<Tool> ToolsFor(IEnumerable<string> items, Func<string, (string Title, string Glyph, string Tip)?> ribbonTool,
        Func<string, string?> learnedSummary, Func<string, string?> recipeSummary)
    {
        var tools = new List<Tool>();
        foreach (var key in items)
        {
            var colon = key.IndexOf(':');
            if (colon <= 0) continue;
            var kind = key.Substring(0, colon);
            var name = key.Substring(colon + 1);
            Tool? tool = null;
            switch (kind)
            {
                case "tool" when Shareable("tool:" + name) && ribbonTool(name) is { } t:
                    tool = new Tool { Id = CommandId("tool:" + name), Kind = "command", Name = "tool:" + name, Title = t.Title, Glyph = t.Glyph, Tip = t.Tip };
                    break;
                case "command" when Commands.FirstOrDefault(c => c.Command == name) is { Command.Length: > 0 } c:
                    tool = new Tool { Id = CommandId(name), Kind = "command", Name = name, Title = c.Title, Glyph = c.Glyph, Tip = c.Tip };
                    break;
                case "learned" when learnedSummary(name) is { } summary:
                    tool = new Tool { Id = LearnedId(name), Kind = "learned", Name = name, Title = Pretty(name), Glyph = "learned", Tip = summary, File = LearnedFolder + "/" + name + ".json" };
                    break;
                case "recipe" when recipeSummary(name) is { } summary:
                    tool = new Tool { Id = RecipeId(name), Kind = "recipe", Name = name, Title = Pretty(name), Glyph = "recipe", Tip = summary, File = RecipesFolder + "/" + FileName(name) + ".json" };
                    break;
            }
            if (tool is not null && ValidToolId(tool.Id) && tools.All(x => x.Id != tool.Id)) tools.Add(tool);
        }
        return tools;
    }

    /// <summary>"fix_door_tags" → "Fix door tags".</summary>
    public static string Pretty(string name)
    {
        var text = (name ?? "").Replace('_', ' ').Trim();
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    /// <summary>A file name for a recipe key (the add-in keys recipes by file name).</summary>
    public static string FileName(string name) =>
        string.Concat((name ?? "").Trim().Select(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || c < ' ' ? '_' : c));

    /// <summary>The zip's file name for a pack ("TritoRevit - QA tools.zip").</summary>
    public static string ZipName(string packName)
    {
        var safe = FileName(string.IsNullOrWhiteSpace(packName) ? "Shared tools" : packName.Trim());
        return $"TritoRevit - {safe}.zip";
    }

    /// <summary>The readme in the zip: what it is and how to install it, in plain words.</summary>
    public static string Readme(Pack pack) =>
        $"TritoRevit: {pack.Name}\r\n" +
        new string('=', 12 + pack.Name.Length) + "\r\n\r\n" +
        $"{pack.Tools.Count} shared Revit tool(s), revision {pack.Revision}:\r\n" +
        string.Concat(pack.Tools.Select(t => $"  - {t.Title}\r\n")) +
        (pack.Note.Length > 0 ? "\r\n" + pack.Note + "\r\n" : "") +
        "\r\nTo install\r\n----------\r\n" +
        "1. Extract this zip (right-click > Extract All).\r\n" +
        "2. Double-click \"Install TritoRevit.cmd\". No admin rights are needed.\r\n" +
        "3. The first time only, close and reopen Revit. The tools are on the TritoRevit tab.\r\n\r\n" +
        "If TritoRevit is already installed, the tools appear on its tab within a few seconds, with Revit open.\r\n" +
        "The first install downloads the TritoRevit add-in (about 40 MB) from GitHub, so it needs internet once.\r\n" +
        (pack.UpdateFrom.Length > 0
            ? $"\r\nUpdates: when these tools change they are updated by themselves from\r\n  {pack.UpdateFrom}\r\nwhile Revit is open, with no restart.\r\n"
            : "\r\nUpdates: the TritoRevit add-in updates itself. To get changed tools, install a newer zip of this pack.\r\n");

    // ---- TritoRevit's folder in Documents (Sharing/UserFolder)

    /// <summary>"TritoRevit - Pre Submission Checklist" for a pack named so; "TritoRevit - Shared tools" with no pack.</summary>
    public static string FolderName(IEnumerable<Pack> packs)
    {
        var list = packs.ToList();
        var pack = list.FirstOrDefault(p => p.Tools.Any(t => t.Kind == "command" && t.Name == "pre-submission")) ?? list.FirstOrDefault();
        var name = CleanName(pack?.Name ?? "");
        return "TritoRevit - " + (name.Length > 0 ? name : "Shared tools");
    }

    /// <summary>A pack name as a folder name: characters Windows refuses dropped, spaces collapsed, at most 60 long.</summary>
    internal static string CleanName(string name)
    {
        var bad = new HashSet<char>(System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));
        var kept = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        kept = string.Join(" ", kept.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return kept.Length > 60 ? kept.Substring(0, 60).TrimEnd('.', ' ') : kept;
    }

    internal static string ReadMe(IEnumerable<Pack> packs)
    {
        var withFolder = packs.Where(p => p.UpdateFrom.Length > 0).Select(p => p.UpdateFrom).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return "This folder is made by TritoRevit for the shared tools on this PC.\r\n\r\n" +
               "Checklists: the checklist Word files and PDFs you create are saved here.\r\n\r\n" +
               "Bugs: when something goes wrong, use Report a problem (at the bottom of the checklist window, or in Shared tools on the TritoRevit tab) " +
               "and say in one line what happened. TritoRevit also saves a report by itself when a tool fails. Each report is one small zip " +
               "in the Bugs folder (a picture of the Revit window, the versions, the model's name and what failed). Nothing is sent over the internet.\r\n\r\n" +
               (withFolder.Count > 0
                   ? "A copy of each report also goes to \"Bug reports\" in the shared tools' update folder (" + string.Join(", ", withFolder) + ") when this PC can reach it. " +
                     "If it can't, send the zips in Bugs to the person who shared the tools.\r\n"
                   : "Send the zips in the Bugs folder to the person who shared the tools.\r\n");
    }
}
