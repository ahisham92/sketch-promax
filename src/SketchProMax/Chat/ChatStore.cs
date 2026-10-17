using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Chat;

/// <summary>One line of a saved conversation, as the pane shows it.</summary>
internal sealed class ChatItem
{
    /// <summary>user, assistant, tool, note or error.</summary>
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Tools: the short tool name. User: attachment names.</summary>
    public string Name { get; set; } = "";
    /// <summary>Tools: running, done or failed.</summary>
    public string State { get; set; } = "";
    /// <summary>When it was added (UTC); default for items saved before 0.31, which then show no time.</summary>
    public DateTime Time { get; set; }
    /// <summary>Tools: the input, indented JSON (shortened), for the step's details.</summary>
    public string Input { get; set; } = "";
    /// <summary>Tools: the start of the result, for the step's details and its element ids.</summary>
    public string Result { get; set; } = "";
    /// <summary>User: the full paths of the attached files, for thumbnails.</summary>
    public List<string> Files { get; set; } = [];
}

/// <summary>A saved conversation: the Claude Code session to resume and what the pane showed.</summary>
internal sealed class Conversation
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DateTime Updated { get; set; }
    public List<ChatItem> Items { get; set; } = [];
}

/// <summary>
/// Conversations are kept per Revit model, in %LOCALAPPDATA%\SketchProMax\chat\conversations (local to this PC and
/// user): reopening a model in a later Revit session shows its conversation again and resumes the same Claude Code
/// session with --resume.
/// </summary>
internal static class ChatStore
{
    private const int MaxItems = 300;

    private static string Folder => Path.Combine(ClaudeCli.Folder, "conversations");

    private static string FileFor(string key)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
        return Path.Combine(Folder, string.Concat(hash.Take(10).Select(b => b.ToString("x2"))) + ".json");
    }

    public static Conversation Load(string key, string title)
    {
        try
        {
            var file = FileFor(key);
            if (File.Exists(file) && JsonSerializer.Deserialize<Conversation>(File.ReadAllText(file)) is { } saved)
            {
                saved.Title = title;
                return saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return new Conversation { Key = key, Title = title };
    }

    /// <summary>Every saved conversation (all models), unreadable files skipped.</summary>
    public static List<Conversation> All()
    {
        var list = new List<Conversation>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var file in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<Conversation>(File.ReadAllText(file)) is { } conv) list.Add(conv);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    public static void Save(Conversation conversation)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (conversation.Items.Count > MaxItems) conversation.Items.RemoveRange(0, conversation.Items.Count - MaxItems);
            conversation.Updated = DateTime.UtcNow;
            var file = FileFor(conversation.Key);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(conversation), new UTF8Encoding(false));
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { /* not saved: the chat still works */ }
    }
}
