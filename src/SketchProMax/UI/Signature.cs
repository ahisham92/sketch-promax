using System.IO;
using System.Windows.Media.Imaging;

namespace SketchProMax.UI;

/// <summary>
/// The user's signature picture for the pre-submission checklist: kept as a copy in %APPDATA%\SketchProMax (Settings >
/// Sign-off), so moving or deleting the original doesn't lose it, and read without locking the file.
/// </summary>
internal static class Signature
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");

    /// <summary>
    /// The path to store in the settings for <paramref name="picked"/>: the kept copy (made now when the picture is new),
    /// or "" when there is none, which also removes an old copy.
    /// </summary>
    public static string Keep(string picked)
    {
        picked = (picked ?? "").Trim().Trim('"');
        var old = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "signature.*") : [];
        if (picked.Length == 0)
        {
            foreach (var f in old) File.Delete(f);
            return "";
        }
        var full = Path.GetFullPath(picked);
        if (old.Any(f => string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase))) return full;
        var bytes = File.ReadAllBytes(full);
        var kind = Tools.ChecklistDocx.ImageKind(bytes) ?? throw new IOException("the picture is not a PNG or JPG");
        Directory.CreateDirectory(Folder);
        foreach (var f in old) File.Delete(f);
        var target = Path.Combine(Folder, "signature." + (kind == "png" ? "png" : "jpg"));
        File.WriteAllBytes(target, bytes);
        return target;
    }

    /// <summary>The picture for showing on screen, or null when there is none or it can't be read.</summary>
    public static BitmapSource? Load(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>The picture's bytes and kind for the Word file, or null when it can't be used.</summary>
    public static Tools.ChecklistDocx.Image? ForWord(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return Tools.ChecklistDocx.ImageKind(bytes) is { } kind && Tools.ChecklistDocx.ImageSize(bytes) is not null ? new Tools.ChecklistDocx.Image(bytes, kind) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
