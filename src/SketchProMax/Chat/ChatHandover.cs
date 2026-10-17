using System.Text.Json;

namespace SketchProMax.Chat;

/// <summary>What the chat pane of an outgoing core leaves for the next one when the loader switches versions.</summary>
internal sealed class ChatHandoverState
{
    /// <summary>The open chat's file ("" for a new chat with nothing in it yet).</summary>
    public string File { get; set; } = "";
    /// <summary>Its model (ChatPane.ModelKey) and the model's title.</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>The message being typed, and the files attached to it, not sent yet.</summary>
    public string Draft { get; set; } = "";
    public List<string> Attachments { get; set; } = [];
    public DateTime At { get; set; }
}

/// <summary>
/// Carries the open chat across an in-session update: the old core saves it and leaves its file here (in the Revit
/// process, as AppDomain data, which both cores and the unchanged loader share), and the new core's pane reopens that
/// same chat (not merely the model's latest one), with the Claude Code session it resumes and the unsent message.
/// Only strings cross over, so any two core versions understand each other. Nothing is written to disk.
/// </summary>
internal static class ChatHandover
{
    internal const string DataKey = "SketchProMax.Chat.Handover";

    /// <summary>A handover older than this is from a switch long gone (it failed, or the pane was never made): ignored.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>Leaves the (already saved) chat for the next core.</summary>
    public static void Leave(Conversation conv, string draft, IEnumerable<string> attachments, DateTime? now = null)
    {
        var state = new ChatHandoverState
        {
            File = conv.File, Key = conv.Key, Title = conv.Title, Draft = draft ?? "",
            Attachments = attachments.ToList(), At = now ?? DateTime.UtcNow,
        };
        try { AppDomain.CurrentDomain.SetData(DataKey, JsonSerializer.Serialize(state)); }
        catch (Exception) { /* the next core opens the model's latest chat instead */ }
    }

    /// <summary>What was left, without taking it (null when nothing, or too old).</summary>
    public static ChatHandoverState? Peek(DateTime? now = null)
    {
        try
        {
            if (AppDomain.CurrentDomain.GetData(DataKey) is not string json || JsonSerializer.Deserialize<ChatHandoverState>(json) is not { } state) return null;
            return (now ?? DateTime.UtcNow) - state.At > MaxAge ? null : state;
        }
        catch (Exception) { return null; }
    }

    /// <summary>What was left, taken (the next pane, or a later switch, doesn't see it again).</summary>
    public static ChatHandoverState? Take(DateTime? now = null)
    {
        var state = Peek(now);
        try { AppDomain.CurrentDomain.SetData(DataKey, null); }
        catch (Exception) { }
        return state;
    }

    /// <summary>
    /// The chat to show for model <paramref name="key"/>: the one handed over when it is that model's (a new, empty chat
    /// when that was a new chat), otherwise the model's latest (ChatStore.Load).
    /// </summary>
    public static Conversation Open(ChatHandoverState? state, string key, string title)
    {
        if (state is not null && string.Equals(state.Key, key, StringComparison.OrdinalIgnoreCase))
        {
            if (state.File.Length == 0) return ChatStore.New(key, title);
            if (ChatStore.Read(state.File) is { } conv && string.Equals(conv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                conv.Title = title;
                return conv;
            }
        }
        return ChatStore.Load(key, title);
    }
}
