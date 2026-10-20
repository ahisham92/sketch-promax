using System.IO;

namespace TritoRevit;

/// <summary>
/// The buttons of the installed shared packs, read from each pack's buttons.txt (written by Sketch ProMax's Share
/// tools, see SharedPackMath.ButtonsText in the core): a "tritorevit-buttons" header, one "pack" line (id, name,
/// revision) and one "button" line per tool (id, text, glyph, tooltip, "model"). Lines it doesn't know are skipped, so
/// a later format can add some. Pure: no Revit, so the tests read it.
/// </summary>
internal static class PackButtons
{
    public const string Header = "tritorevit-buttons";

    internal sealed record Button(string Id, string Text, string Glyph, string Tip, bool NeedsModel);

    internal sealed record Pack(string Id, string Name, int Revision, List<Button> Buttons);

    /// <summary>A pack's buttons, or null when the text is not a buttons file (or names no pack).</summary>
    public static Pack? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = text!.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith(Header + "\t", StringComparison.Ordinal)) return null;
        Pack? pack = null;
        var buttons = new List<Button>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split('\t').Select(Unescape).ToArray();
            if (f[0] == "pack" && f.Length >= 3 && pack is null && f[1].Length > 0)
                pack = new Pack(f[1], f[2].Length > 0 ? f[2] : f[1], f.Length > 3 && int.TryParse(f[3], out var r) ? r : 0, buttons);
            else if (f[0] == "button" && f.Length >= 3 && f[1].Length > 0 && f[2].Length > 0 && seen.Add(f[1]))
                buttons.Add(new Button(f[1], f[2], f.Length > 3 && f[3].Length > 0 ? f[3] : "cube", f.Length > 4 ? f[4] : "",
                    f.Length > 5 && f[5].Split(',').Contains("model")));
        }
        return pack;
    }

    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next == 'n' ? '\n' : next);
            }
            else sb.Append(value[i]);
        }
        return sb.ToString();
    }

    /// <summary>The installed packs' folder: %APPDATA%\TritoRevit\packs.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TritoRevit", "packs");

    /// <summary>
    /// Every installed pack, by name. A folder with a dot in its name is one being swapped in or out, and skipped.
    /// Never throws: an unreadable pack is left out until the next look.
    /// </summary>
    public static List<Pack> ReadAll(string? folder = null)
    {
        folder ??= Folder;
        var packs = new List<Pack>();
        try
        {
            if (!Directory.Exists(folder)) return packs;
            foreach (var dir in Directory.GetDirectories(folder))
            {
                var name = Path.GetFileName(dir);
                if (name.IndexOf('.') >= 0) continue;
                try
                {
                    var file = Path.Combine(dir, "buttons.txt");
                    if (File.Exists(file) && Parse(File.ReadAllText(file)) is { } pack && pack.Id == name) packs.Add(pack);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return packs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Changes whenever a pack is added, removed or rewritten: the folders and their buttons files' times.</summary>
    public static string Signature(string? folder = null)
    {
        folder ??= Folder;
        try
        {
            if (!Directory.Exists(folder)) return "-";
            return string.Join("|", Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Select(d =>
            {
                var file = Path.Combine(d, "buttons.txt");
                return Path.GetFileName(d) + "=" + (File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0);
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "?"; }
    }
}
