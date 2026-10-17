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

    private static string Folder => Path.Combine(ClaudeCli.Folder, "conversations");

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

    /// <summary>The model's latest chat (a new one when it has none).</summary>
    public static Conversation Load(string key, string title)
    {
        try
        {
            if (Directory.Exists(Folder))
            {
                var hash = Hash(key);
                var latest = Directory.GetFiles(Folder, hash + "*.json")
                    .Where(f => Path.GetFileNameWithoutExtension(f) is var n && (n == hash || n.StartsWith(hash + "-", StringComparison.Ordinal)))
                    .OrderByDescending(File.GetLastWriteTimeUtc);
                foreach (var file in latest)
                    if (Read(file) is { } saved)
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
            if (!File.Exists(file) || JsonSerializer.Deserialize<Conversation>(File.ReadAllText(file)) is not { } conv) return null;
            conv.File = file;
            return conv;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
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
    /// Saves a chat; a chat with nothing in it yet is not written. <paramref name="touch"/> off (rename, pin) keeps its
    /// date, so it keeps its place in the history.
    /// </summary>
    public static void Save(Conversation conversation, bool touch = true)
    {
        if (conversation.Items.Count == 0 && conversation.SessionId.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Folder);
            if (conversation.Items.Count > MaxItems) conversation.Items.RemoveRange(0, conversation.Items.Count - MaxItems);
            if (touch || conversation.Updated == default) conversation.Updated = DateTime.UtcNow;
            var legacy = conversation.Id.Length == 0 ? FileFor(conversation) : null;
            if (conversation.Id.Length == 0) conversation.Id = NewId();
            if (conversation.Name.Length == 0)
                conversation.Name = ChatHistory.AutoTitle(conversation.Items.FirstOrDefault(i => i.Kind == "user")?.Text);
            var file = FileFor(conversation);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(conversation), new UTF8Encoding(false));
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
            conversation.File = file;
            if (legacy is not null && File.Exists(legacy)) File.Delete(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { /* not saved: the chat still works */ }
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
