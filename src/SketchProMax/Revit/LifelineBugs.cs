using System.Diagnostics;
using System.IO;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Lifeline's own bug list: one text file on this PC (Documents\Sketch ProMax\Lifeline bugs\Lifeline bugs.txt) with
/// a line for each problem, newest last: an element the change file couldn't read, a write that failed, a replay
/// error, and each change a replay couldn't put back. Each problem is written once per Revit session; the file keeps
/// the newest <see cref="ChangeFileMath.MaxBugLines"/> lines. Never throws.
/// </summary>
internal static class LifelineBugs
{
    internal static string? FolderForTest;

    public static string Folder => FolderForTest ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", M.BugsFolderName);

    public static string FilePath => Path.Combine(Folder, M.BugsFileName);

    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>
    /// Notes a problem: <paramref name="where"/> is the part of Lifeline (capture, write, replay), <paramref name="what"/> what
    /// went wrong. Problems with the same <paramref name="key"/> (else the same text) are written once a session.
    /// </summary>
    public static void Note(string where, string what, string? model = null, Exception? ex = null, string? key = null)
    {
        try
        {
            var text = ex is null ? what : $"{what}: {ex.GetType().Name}: {ex.Message}{(Site(ex) is { } site ? " (in " + site + ")" : "")}";
            lock (Gate)
            {
                if (Seen.Count >= M.MaxBugsPerSession || !Seen.Add(where + "|" + (key ?? text))) return;
                var line = M.BugLine(DateTime.Now, Update.Updater.CurrentText, App.RevitYear, model is null ? null : Path.GetFileNameWithoutExtension(model), where, text);
                Directory.CreateDirectory(Folder);
                var file = FilePath;
                if (!File.Exists(file)) File.WriteAllText(file, M.BugsHeader + Environment.NewLine);
                File.AppendAllText(file, line + Environment.NewLine);
                if (new FileInfo(file).Length > M.MaxBugLines * 400L)
                    File.WriteAllLines(file, M.TrimBugs(File.ReadAllLines(file).ToList(), M.MaxBugLines));
            }
        }
        catch (Exception) { /* the bug list is a help, never a new problem */ }
    }

    /// <summary>The add-in's method the exception came from, so the line says where to look.</summary>
    private static string? Site(Exception ex)
    {
        try
        {
            var method = new StackTrace(ex).GetFrames()?.Select(f => f.GetMethod())
                .FirstOrDefault(m => m?.DeclaringType?.Namespace?.StartsWith("SketchProMax", StringComparison.Ordinal) == true);
            return method is null ? null : $"{method.DeclaringType!.Name}.{method.Name}";
        }
        catch (Exception) { return null; }
    }

    internal static void ForgetForTest() { lock (Gate) Seen.Clear(); }
}
