using System.IO;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// The Claude models the chat offers (passed to Claude Code with --model), the remembered choice, and how a model the
/// user's plan doesn't allow is recognised. The last choice is kept in %LOCALAPPDATA%\SketchProMax\chat\model.txt.
/// </summary>
internal static class ChatModels
{
    internal sealed record Choice(string Id, string Label, string Hint);

    public static readonly IReadOnlyList<Choice> All =
    [
        new("claude-opus-5-5", "Opus 5.5", "Best for big packages and multi-step work"),
        new("claude-sonnet-5-5", "Sonnet 5.5", "The everyday default"),
        new("claude-haiku-5-5", "Haiku 5.5", "Quick queries"),
        new("claude-fable-5-1", "Fable 5.1", "Available on plans that include it"),
    ];

    public const string DefaultId = "claude-sonnet-5-5";

    /// <summary>The choice with this id; the default for an unknown or empty id.</summary>
    public static Choice Find(string? id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All.First(c => c.Id == DefaultId);

    // Claude Code's words when the plan or account can't use the model asked for ("There's an issue with the selected
    // model (…). It may not exist or you may not have access to it", a 404 for the model, "not available on your plan").
    private static readonly Regex Rejected = new(
        @"selected model|model\W.{0,80}(may not exist|not (be )?(available|allowed|supported|found|enabled)|do(es)? not have access|don't have access|isn't available)|invalid model|model_not_found|not_found_error.{0,80}model|(not available|not included) (on|in|with) your (plan|subscription)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>True when Claude Code's error says the chosen model can't be used.</summary>
    public static bool IsRejection(string? text) => !string.IsNullOrEmpty(text) && Rejected.IsMatch(text!);

    /// <summary>
    /// The model to go back to after <paramref name="rejected"/> was refused: the last one that worked, or the default
    /// when that is the one refused too; null when there is nothing else to try.
    /// </summary>
    public static string? Fallback(string rejected, string lastWorking)
    {
        if (!string.Equals(rejected, lastWorking, StringComparison.OrdinalIgnoreCase)) return Find(lastWorking).Id;
        return string.Equals(rejected, DefaultId, StringComparison.OrdinalIgnoreCase) ? null : DefaultId;
    }

    /// <summary>The last model chosen, kept in <paramref name="folder"/> (the default at first).</summary>
    public static string Load(string folder)
    {
        var file = Path.Combine(folder, "model.txt");
        try { return File.Exists(file) ? Find(File.ReadAllText(file)).Id : DefaultId; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DefaultId; }
    }

    public static void Save(string folder, string id)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "model.txt"), Find(id).Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not remembered: the default next time */ }
    }
}
