using System.IO;
using System.Text;

namespace SketchProMax;

/// <summary>
/// Small settings and chat files written so that a crash, or another Revit writing the same file, never leaves half a
/// file: the text goes to a temporary file with a unique name next to it, which then replaces the file in one step.
/// A temporary file left behind when the swap never happened is read when the file itself is missing.
/// </summary>
internal static class SafeFile
{
    /// <summary>Writes the text (UTF-8, no BOM) through a uniquely named temporary file. Throws what File throws.</summary>
    public static void Write(string file, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = $"{file}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        try { Swap(temp, file); }
        catch
        {
            // The file is still whole (or another writer's): drop the temporary one. Without a file, keep it to read back.
            try { if (File.Exists(file)) File.Delete(temp); } catch (Exception) { }
            throw;
        }
        // Temporary files an earlier, cut-off write left (old enough not to be another writer's, still in progress).
        try
        {
            for (var old = Leftover(file); old is not null && File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddMinutes(-10); old = Leftover(file))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Swap(string temp, string file)
    {
        if (File.Exists(file)) { File.Replace(temp, file, null); return; }
#if NET
        File.Move(temp, file, overwrite: true);
#else
        try { File.Move(temp, file); }
        catch (IOException) when (File.Exists(file)) { File.Replace(temp, file, null); } // written by another meanwhile
#endif
    }

    /// <summary>The file's text; when it is missing, the newest temporary file left beside it; null when there is neither.</summary>
    public static string? Read(string file)
    {
        if (File.Exists(file)) return File.ReadAllText(file);
        return Leftover(file) is { } temp ? File.ReadAllText(temp) : null;
    }

    /// <summary>The newest temporary file a write left beside a missing file, or null.</summary>
    public static string? Leftover(string file)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var name = Path.GetFileName(file);
            return Directory.GetFiles(dir, name + ".*tmp")
                .Where(f => Path.GetFileName(f) is var n && (n == name + ".tmp" || n.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
