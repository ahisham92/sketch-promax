using System.IO;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// The models the chat offers, as Claude Code's aliases (passed with --model), so each follows the latest of its family;
/// the remembered choice; and how a model the user's plan doesn't allow is recognised. Each chat keeps its own choice
/// (Automatic or one of these, ChatAuto); model.txt in %LOCALAPPDATA%\SketchProMax\chat is what older versions kept.
/// </summary>
internal static class ChatModels
{
    internal sealed record Choice(string Id, string Label, string Hint);

    public static readonly IReadOnlyList<Choice> All =
    [
        new("opus", "Opus", "Hard reasoning: specs against the model, redlines, tricky modelling"),
        new("sonnet", "Sonnet", "Most everyday Revit work"),
        new("haiku", "Haiku", "Quick, simple work: rename, tag, list"),
        new("fable", "Fable", "Available on plans that include it"),
    ];

    public const string DefaultId = "sonnet";

    /// <summary>
    /// The alias <paramref name="id"/> names: the alias itself, or a full model name that contains it (what older
    /// versions saved); null when it names none.
    /// </summary>
    public static string? Known(string? id)
    {
        var text = id?.Trim() ?? "";
        if (text.Length == 0) return null;
        return All.FirstOrDefault(c => string.Equals(c.Id, text, StringComparison.OrdinalIgnoreCase))?.Id
            ?? All.FirstOrDefault(c => text.IndexOf(c.Id, StringComparison.OrdinalIgnoreCase) >= 0)?.Id;
    }

    /// <summary>The choice with this id (an alias, or a full name containing one); the default for an unknown or empty id.</summary>
    public static Choice Find(string? id) => All.First(c => c.Id == (Known(id) ?? DefaultId));

    // Claude Code's own words when the plan or account can't use the model asked for, and nothing looser (a Revit tool's
    // "model ... not found" must not count): "There's an issue with the selected model (…). It may not exist or you may
    // not have access to it", the API's 404 not_found_error for "model: …", model_not_found, "not available on your plan".
    private static readonly Regex Rejected = new(
        @"issue with the selected model|selected model\b.{0,120}(may not exist|not have access)|\bmodel_not_found\b|not_found_error.{0,80}message\W{1,8}model:" +
        @"|\bmodel\b.{0,60}\b(not available|not included) (on|in|with) your (plan|subscription)",
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
