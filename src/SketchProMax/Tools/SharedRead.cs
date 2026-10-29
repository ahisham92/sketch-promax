using System.IO;

namespace SketchProMax.Tools;

/// <summary>
/// Reads a file another program has open. Excel and Word keep the file they show open for writing, and
/// File.ReadAllBytes refuses it then ("being used by another process", 2026-10-28: a drawing list attached from the
/// project folder while it was open in Excel). Sharing read and write lets us read the last saved copy.
/// </summary>
internal static class SharedRead
{
    public static byte[] AllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
