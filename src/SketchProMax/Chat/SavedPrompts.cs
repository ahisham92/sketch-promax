using System.IO;
using System.Text.Json;

namespace SketchProMax.Chat;

/// <summary>A prompt the user saved from a message (⋯ › Save prompt), to use again from the composer or the home screen.</summary>
internal sealed class SavedPrompt
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Saved { get; set; }
}

/// <summary>
/// The user's saved prompts, kept in %APPDATA%\SketchProMax\prompts.json (roaming, so they follow the user), newest
/// first, without repeats.
/// </summary>
internal static class SavedPrompts
{
    public const int Max = 200;

    public static string DefaultFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "prompts.json");

    /// <summary>The saved prompts; an empty list when there are none or the file is damaged (never an error).</summary>
    public static List<SavedPrompt> Load(string file)
    {
        try
        {
            if (SafeFile.Read(file) is not { } json) return [];
            return (JsonSerializer.Deserialize<List<SavedPrompt>?>(json) ?? [])
                .Where(p => p?.Text is { } t && t.Trim().Length > 0)
                .Select(p => { p.Title ??= ""; return p; })
                .Take(Max).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Saves the list; false when it couldn't be written.</summary>
    public static bool Save(string file, List<SavedPrompt> prompts)
    {
        try
        {
            SafeFile.Write(file, JsonSerializer.Serialize(prompts.Take(Max).ToList(), new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Reads the file again, applies <paramref name="change"/> (Add or Remove) and saves, so prompts saved meanwhile in
    /// another Revit are kept. Returns the list as saved, and whether it was written.
    /// </summary>
    public static (List<SavedPrompt> List, bool Saved) Update(string file, Func<List<SavedPrompt>, List<SavedPrompt>> change)
    {
        var list = change(Load(file));
        return (list, Save(file, list));
    }

    /// <summary>The list with <paramref name="text"/> added at the top; the same text saved before moves up instead of repeating.</summary>
    public static List<SavedPrompt> Add(IEnumerable<SavedPrompt> prompts, string text, DateTime now)
    {
        var clean = text.Trim();
        var list = prompts.Where(p => !Same(p.Text, clean)).ToList();
        if (clean.Length == 0) return list;
        list.Insert(0, new SavedPrompt { Text = clean, Title = ChatHistory.AutoTitle(clean, 60), Saved = now });
        return list.Take(Max).ToList();
    }

    public static List<SavedPrompt> Remove(IEnumerable<SavedPrompt> prompts, string text) => prompts.Where(p => !Same(p.Text, text)).ToList();

    private static bool Same(string a, string b) =>
        string.Equals(string.Join(" ", a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
                      string.Join(" ", b.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);
}
