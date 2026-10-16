using System.IO;
using System.Text;

namespace SketchProMax.Chat;

/// <summary>
/// What the user asked Claude to remember ("remember that ..."), kept in %APPDATA%\SketchProMax\memory.md: a Global
/// section that every chat sees, and one section per model (keyed like the chat's conversations, by the model's path or,
/// for a model never saved, its title) that only that model's chat sees. Plain Markdown, so it can be read and edited
/// by hand; the pane's Notes tab and the memory_note tool edit it through this class. No Revit or WPF here.
///
///   # Sketch ProMax memory
///   ## Global
///   - a note
///   ## Model: C:\Projects\Tower.rvt
///   - a note for that model only
/// </summary>
internal static class MemoryNotes
{
    public const string GlobalHeading = "Global";
    private const string ModelPrefix = "Model: ";
    private const int MaxNote = 1000, MaxPromptChars = 12_000;
    private static readonly object Gate = new();

    public static string File => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "memory.md");

    /// <summary>One section: Key is null for Global, else the model key.</summary>
    internal sealed class Section(string? key)
    {
        public string? Key { get; } = key;
        public List<string> Notes { get; } = [];
    }

    // ---- Pure text: parse and write ----------------------------------------------------------------------------------

    /// <summary>Reads the sections from the file's text. Lines that are not "## " headings or "- " notes are ignored.</summary>
    public static List<Section> Parse(string text)
    {
        var sections = new List<Section>();
        Section? current = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = line.Substring(3).Trim();
                string? key = heading.Equals(GlobalHeading, StringComparison.OrdinalIgnoreCase) ? null
                    : heading.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase) ? heading.Substring(ModelPrefix.Length).Trim()
                    : heading; // a hand-written "## C:\x.rvt" still counts as that model
                current = Find(sections, key);
                if (current is null) sections.Add(current = new Section(key));
            }
            else if (current is not null && (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)))
            {
                var note = line.Substring(2).Trim();
                if (note.Length > 0) current.Notes.Add(note);
            }
        }
        return sections;
    }

    public static string Write(IEnumerable<Section> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sketch ProMax memory");
        sb.AppendLine();
        sb.AppendLine("What you asked Claude to remember in the Revit chat. Global notes go to every chat; a model's notes only to that model's chat.");
        sb.AppendLine("Edit or delete them in the Notes tab of the Sketch ProMax pane, or here (one \"- \" line per note).");
        var list = sections.Where(s => s.Notes.Count > 0 || s.Key is null).OrderBy(s => s.Key is null ? 0 : 1).ToList();
        if (!list.Any(s => s.Key is null)) list.Insert(0, new Section(null));
        foreach (var s in list)
        {
            sb.AppendLine();
            sb.AppendLine("## " + (s.Key is null ? GlobalHeading : ModelPrefix + s.Key));
            sb.AppendLine();
            foreach (var n in s.Notes) sb.AppendLine("- " + n);
        }
        return sb.ToString();
    }

    private static Section? Find(List<Section> sections, string? key) =>
        sections.FirstOrDefault(s => key is null ? s.Key is null : s.Key is not null && string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>A note kept on one line and to a sensible length.</summary>
    public static string Clean(string note)
    {
        var one = string.Join(" ", note.Replace("\r", " ").Replace("\n", " ").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        if (one.StartsWith("- ", StringComparison.Ordinal)) one = one.Substring(2);
        return one.Length > MaxNote ? one.Substring(0, MaxNote).TrimEnd() + "…" : one;
    }

    /// <summary>Global notes and those of <paramref name="modelKey"/>.</summary>
    public static (List<string> Global, List<string> Model) For(List<Section> sections, string? modelKey) =>
        (Find(sections, null)?.Notes.ToList() ?? [],
         string.IsNullOrEmpty(modelKey) ? [] : Find(sections, modelKey)?.Notes.ToList() ?? []);

    /// <summary>The block added to the chat's instructions: global notes and this model's, or "" when there are none.</summary>
    public static string PromptBlock(List<Section> sections, string? modelKey)
    {
        var (global, model) = For(sections, modelKey);
        if (global.Count == 0 && model.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("Notes the user asked you to remember (from memory_note; follow them unless the user now says otherwise):");
        foreach (var n in global) sb.AppendLine("- " + n);
        if (model.Count > 0)
        {
            sb.AppendLine("For this model only:");
            foreach (var n in model) sb.AppendLine("- " + n);
        }
        var text = sb.ToString();
        return text.Length > MaxPromptChars ? text.Substring(0, MaxPromptChars) + "\n(more notes are in memory_note action=list)\n" : text;
    }

    /// <summary>Adds a note (no duplicate). Returns false when it was already there.</summary>
    public static bool Add(List<Section> sections, string? key, string note)
    {
        note = Clean(note);
        if (note.Length == 0) throw new ArgumentException("The note is empty.");
        var section = Find(sections, key);
        if (section is null) sections.Add(section = new Section(key));
        if (section.Notes.Any(n => string.Equals(n, note, StringComparison.OrdinalIgnoreCase))) return false;
        section.Notes.Add(note);
        return true;
    }

    /// <summary>
    /// The notes in <paramref name="key"/>'s section (null: Global) that <paramref name="text"/> names: the one equal to
    /// it, else every one containing it.
    /// </summary>
    public static List<string> Matches(List<Section> sections, string? key, string text)
    {
        var notes = Find(sections, key)?.Notes ?? [];
        text = Clean(text);
        if (text.Length == 0) return [];
        var exact = notes.Where(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact.Take(1).ToList() : notes.Where(n => n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    public static bool Remove(List<Section> sections, string? key, string note)
    {
        var section = Find(sections, key);
        if (section is null) return false;
        var at = section.Notes.FindIndex(n => string.Equals(n, note, StringComparison.Ordinal));
        if (at < 0) at = section.Notes.FindIndex(n => string.Equals(n, note, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return false;
        section.Notes.RemoveAt(at);
        return true;
    }

    public static bool Replace(List<Section> sections, string? key, string old, string note)
    {
        var section = Find(sections, key);
        var at = section?.Notes.FindIndex(n => string.Equals(n, old, StringComparison.Ordinal)) ?? -1;
        if (at < 0) return false;
        note = Clean(note);
        if (note.Length == 0) section!.Notes.RemoveAt(at);
        else section!.Notes[at] = note;
        return true;
    }

    // ---- The file ----------------------------------------------------------------------------------------------------

    public static List<Section> Load()
    {
        lock (Gate)
        {
            try { return System.IO.File.Exists(File) ? Parse(System.IO.File.ReadAllText(File)) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>Loads, changes and saves in one step, so two Revit windows don't undo each other's notes.</summary>
    public static T Change<T>(Func<List<Section>, T> change)
    {
        lock (Gate)
        {
            var sections = System.IO.File.Exists(File) ? Parse(System.IO.File.ReadAllText(File)) : [];
            var result = change(sections);
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var temp = File + ".tmp";
            System.IO.File.WriteAllText(temp, Write(sections), new UTF8Encoding(false));
            if (System.IO.File.Exists(File)) System.IO.File.Replace(temp, File, null);
            else System.IO.File.Move(temp, File);
            return result;
        }
    }

    /// <summary>The prompt block for the chat of <paramref name="modelKey"/>; "" on any trouble.</summary>
    public static string PromptFor(string? modelKey)
    {
        try { return PromptBlock(Load(), modelKey); }
        catch (Exception) { return ""; }
    }
}
