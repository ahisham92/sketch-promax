using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Code written again and again becomes a one-click tool. Every run_revit_code and run_revit_query_code that worked is
/// counted by its shape: the code with its names, numbers and comments taken out, so the same work on other levels, sizes
/// or ids counts as the same code. The third time a shape works (within 30 days, in any chat or job), its result says to
/// save it with save_as_tool, with what changed between uses as the tool's inputs; a saved tool is one click in Quick tools
/// and needs no code written next time. Said once per shape. Short snippets (a quick count) aren't worth a tool and
/// aren't counted. Kept in %APPDATA%\SketchProMax\code-repeats.json; no Revit here (the tests compile it).
/// </summary>
internal static class CodeRepeats
{
    public const int Times = 3;
    public const int MinLength = 300;
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);
    private const int MaxShapes = 500;

    internal sealed class Entry
    {
        public int Count { get; set; }
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
        public string Description { get; set; } = "";
        public bool Said { get; set; }
    }

    private static readonly object Gate = new();

    public static string StoreFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "code-repeats.json");

    private static readonly Regex Comments = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Strings = new(@"@""(?:[^""]|"""")*""|\$?""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)'", RegexOptions.Compiled);
    private static readonly Regex Numbers = new(@"\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?[fFdDmMlL]?\b", RegexOptions.Compiled);
    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The code's shape: every text and number the same, no comments, spaces collapsed.</summary>
    public static string Shape(string code)
    {
        var s = Strings.Replace(code ?? "", "\"\"");
        s = Comments.Replace(s, " ");
        s = Numbers.Replace(s, "0");
        return Space.Replace(s, " ").Trim();
    }

    public static string Key(string shape)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(shape))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// Counts one run of <paramref name="code"/> that worked, in <paramref name="shapes"/>; the tip for its result when this
    /// run makes it the <see cref="Times"/>th (null otherwise). Old shapes are dropped.
    /// </summary>
    public static string? Count(Dictionary<string, Entry> shapes, string code, string description, DateTime now)
    {
        foreach (var old in shapes.Where(p => now - p.Value.Last > Window).Select(p => p.Key).ToList()) shapes.Remove(old);
        var shape = Shape(code);
        if (shape.Length < MinLength) return null;
        var key = Key(shape);
        if (!shapes.TryGetValue(key, out var entry))
        {
            if (shapes.Count >= MaxShapes) shapes.Remove(shapes.OrderBy(p => p.Value.Last).First().Key);
            shapes[key] = entry = new Entry { First = now };
        }
        entry.Count++;
        entry.Last = now;
        if (description.Trim().Length > 0) entry.Description = description.Trim();
        if (entry.Count < Times || entry.Said) return null;
        entry.Said = true;
        return Tip(entry.Count);
    }

    public static string Tip(int times) =>
        $"Tip: code of this shape has now been written {times} times. Save it with save_as_tool, with what changes between uses " +
        "(names, levels, sizes, ids) as its inputs, so next time it is one call (and one click in Quick tools) instead of code " +
        "written again. Tell the user in one line that you saved it.";

    /// <summary><see cref="Count"/> against the file on this PC. Never throws: counting must not break a call.</summary>
    public static string? Note(string code, string description)
    {
        try
        {
            lock (Gate)
            {
                var shapes = Read();
                var tip = Count(shapes, code, description, DateTime.UtcNow);
                Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
                var temp = StoreFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(shapes));
                if (File.Exists(StoreFile)) File.Replace(temp, StoreFile, null);
                else File.Move(temp, StoreFile);
                return tip;
            }
        }
        catch (Exception) { return null; }
    }

    private static Dictionary<string, Entry> Read()
    {
        try
        {
            if (File.Exists(StoreFile))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(StoreFile)) ?? new();
        }
        catch (Exception) { /* a broken file starts again */ }
        return new();
    }
}
