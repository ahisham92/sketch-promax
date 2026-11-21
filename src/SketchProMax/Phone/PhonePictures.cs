using System.IO;

namespace SketchProMax.Phone;

/// <summary>
/// Pictures on the phone chat (Ahmed, 21 Nov 2026: "I don't get pictures or send pictures"). A picture the user sends from
/// the phone is saved as a file and attached to the chat's message; a view the chat captured while answering the phone
/// (capture_view_image) goes back with the reply. No WPF or Revit (the tests compile it).
/// </summary>
internal static class PhonePictures
{
    private static readonly object Gate = new();
    private static readonly List<(string? Chat, DateTime At, byte[] Png)> Kept = [];

    /// <summary>The most pictures sent with one reply.</summary>
    public const int MaxPerReply = 2;

    /// <summary>
    /// Saves a picture sent from the phone (a data URL: data:image/jpeg;base64,...) into <paramref name="folder"/>. Its
    /// path, or null when it isn't a picture.
    /// </summary>
    public static string? Save(string dataUrl, string folder, DateTime nowUtc)
    {
        if (Decode(dataUrl) is not { } got) return null;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"phone-{nowUtc.ToLocalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 6)}{got.Extension}");
        File.WriteAllBytes(path, got.Bytes);
        return path;
    }

    /// <summary>The bytes and file extension of a picture data URL, or null.</summary>
    public static (byte[] Bytes, string Extension)? Decode(string dataUrl)
    {
        var text = (dataUrl ?? "").Trim();
        var comma = text.IndexOf(',');
        if (!text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || comma < 0) return null;
        var head = text.Substring(5, comma - 5).ToLowerInvariant();
        if (!head.EndsWith(";base64", StringComparison.Ordinal)) return null;
        var ext = head.StartsWith("image/png", StringComparison.Ordinal) ? ".png"
            : head.StartsWith("image/jpeg", StringComparison.Ordinal) || head.StartsWith("image/jpg", StringComparison.Ordinal) ? ".jpg"
            : head.StartsWith("image/webp", StringComparison.Ordinal) ? ".webp" : null;
        if (ext is null) return null;
        try
        {
            var bytes = Convert.FromBase64String(text.Substring(comma + 1));
            return bytes.Length > 0 ? (bytes, ext) : null;
        }
        catch (FormatException) { return null; }
    }

    public static string DataUrl(byte[] bytes, string mime) => $"data:{mime};base64," + Convert.ToBase64String(bytes);

    /// <summary>A view the chat captured (<paramref name="chat"/>: the caller's chat key, null for the chat shown).</summary>
    public static void Keep(string? chat, byte[] png, DateTime nowUtc)
    {
        lock (Gate)
        {
            Kept.Add((chat, nowUtc, png));
            Kept.RemoveAll(k => nowUtc - k.At > TimeSpan.FromHours(2));
            while (Kept.Count > 12) Kept.RemoveAt(0);
        }
    }

    /// <summary>
    /// The last views chat <paramref name="chatKey"/> (or the chat shown, <paramref name="shown"/>) captured since
    /// <paramref name="sinceUtc"/>, oldest first, at most <see cref="MaxPerReply"/>; taken, so they go once.
    /// </summary>
    public static List<byte[]> Take(string chatKey, bool shown, DateTime sinceUtc)
    {
        lock (Gate)
        {
            var mine = Kept.Where(k => k.At >= sinceUtc && (k.Chat == chatKey || shown && k.Chat is null)).ToList();
            foreach (var k in mine) Kept.Remove(k);
            return mine.Skip(Math.Max(0, mine.Count - MaxPerReply)).Select(k => k.Png).ToList();
        }
    }
}
