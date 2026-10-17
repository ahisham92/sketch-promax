using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Chat;

/// <summary>
/// Chats are kept per Revit model, several per model, in %LOCALAPPDATA%\SketchProMax\chat\conversations (local to this
/// PC and user), one file each: "&lt;model hash&gt;-&lt;chat id&gt;.json". Reopening a model shows its latest chat again and
/// resumes the same Claude Code session with --resume; the history drawer lists the others. A file saved before 0.31
/// ("&lt;model hash&gt;.json", one chat per model) is read as that model's chat and renamed the next time it is saved.
/// </summary>
internal static class ChatStore
{
    private const int MaxItems = 300;

    /// <summary>Used instead of the real folder while a test runs (reset it in a finally).</summary>
    internal static string? RootOverride;

    // ClaudeCli.Folder's "conversations", spelled out so the store builds without the Claude Code code (unit tests).
    private static string Folder => RootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "chat", "conversations");

    private static string Hash(string key)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
        return string.Concat(hash.Take(10).Select(b => b.ToString("x2")));
    }

    private static string FileFor(Conversation conv) =>
        Path.Combine(Folder, Hash(conv.Key) + (conv.Id.Length > 0 ? "-" + conv.Id : "") + ".json");

    /// <summary>A new, empty chat for a model.</summary>
    public static Conversation New(string key, string title) => new() { Key = key, Title = title, Id = NewId() };

    private static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

    /// <summary>
    /// The id a chat saved before 0.31 gets when it is moved to the new name: made from its model key, so two Revit
    /// windows moving the same file give it the same name instead of two copies.
    /// </summary>
    internal static string MigratedId(string key) => "m" + Hash("migrated\u0001" + key).Substring(0, 11);

    /// <summary>The model's latest chat (a new one when it has none).</summary>
    public static Conversation Load(string key, string title)
    {
        try
        {
            if (Directory.Exists(Folder))
            {
                var hash = Hash(key);
                bool Ours(string n) => n == hash || n.StartsWith(hash + "-", StringComparison.Ordinal);
                var files = Directory.GetFiles(Folder, hash + "*.json").Where(f => Ours(Path.GetFileNameWithoutExtension(f))).ToList();
                // A chat whose save was cut off between writing and swapping is only in its temporary file.
                files.AddRange(Directory.GetFiles(Folder, hash + "*.json.*tmp").Select(t => t.Substring(0, t.IndexOf(".json.", StringComparison.OrdinalIgnoreCase) + 5))
                    .Where(f => Ours(Path.GetFileNameWithoutExtension(f)) && !File.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase));
                // The latest by its own date (opening or switching a chat doesn't change it), not by the file's.
                if (files.Select(Read).OfType<Conversation>().OrderByDescending(c => c.Updated).FirstOrDefault() is { } saved)
                {
                    saved.Title = title;
                    return saved;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return New(key, title);
    }

    /// <summary>The chat saved in <paramref name="file"/>, or null when it can't be read.</summary>
    public static Conversation? Read(string file)
    {
        try
        {
            if (SafeFile.Read(file) is not { } json || JsonSerializer.Deserialize<Conversation>(json) is not { } conv) return null;
            conv.Items ??= [];
            conv.File = file;
            conv.SavedItems = JsonSerializer.Serialize(conv.Items);
            conv.SavedJson = JsonSerializer.Serialize(conv);
            return conv;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>Every saved chat (all models), unreadable files skipped.</summary>
    public static List<Conversation> All()
    {
        var list = new List<Conversation>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var file in Directory.GetFiles(Folder, "*.json"))
                if (Read(file) is { } conv) list.Add(conv);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Every chat of one model, newest first.</summary>
    public static List<Conversation> ForModel(string key) =>
        All().Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.Updated).ToList();

    /// <summary>Every saved chat as the history drawer lists it. Reads every file: call it off the UI thread.</summary>
    public static List<ChatSummary> Summaries() => All().Where(c => c.Items.Count > 0).Select(ChatHistory.Summarize).ToList();

    /// <summary>
    /// Saves a chat; a chat with nothing in it yet is not written, nor one that hasn't changed since it was read or
    /// saved. Its date moves only when its messages changed (and <paramref name="touch"/> is on), so opening, switching
    /// or closing a chat never moves it up the history; rename and pin pass touch off.
    /// </summary>
    public static void Save(Conversation conversation, bool touch = true)
    {
        if (conversation.Items.Count == 0 && conversation.SessionId.Length == 0) return;
        try
        {
            if (conversation.Items.Count > MaxItems) conversation.Items.RemoveRange(0, conversation.Items.Count - MaxItems);
            var items = JsonSerializer.Serialize(conversation.Items);
            var dirty = items != conversation.SavedItems;
            if (touch && dirty) conversation.Updated = DateTime.UtcNow;
            else if (conversation.Updated == default)
                conversation.Updated = conversation.File.Length > 0 && File.Exists(conversation.File) ? File.GetLastWriteTimeUtc(conversation.File) : DateTime.UtcNow;
            var legacy = conversation.Id.Length == 0 ? FileFor(conversation) : null;
            if (conversation.Id.Length == 0) conversation.Id = MigratedId(conversation.Key);
            if (conversation.Name.Length == 0)
                conversation.Name = ChatHistory.AutoTitle(conversation.Items.FirstOrDefault(i => i.Kind == "user")?.Text);
            var file = FileFor(conversation);
            var json = JsonSerializer.Serialize(conversation);
            if (json == conversation.SavedJson && string.Equals(file, conversation.File, StringComparison.OrdinalIgnoreCase) && File.Exists(file)) return;
            SafeFile.Write(file, json);
            conversation.File = file;
            conversation.SavedItems = items;
            conversation.SavedJson = json;
            if (legacy is not null && File.Exists(legacy)) File.Delete(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { /* not saved: the chat still works */ }
    }

    /// <summary>Deletes a saved chat; false when the file couldn't be deleted.</summary>
    public static bool Delete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
