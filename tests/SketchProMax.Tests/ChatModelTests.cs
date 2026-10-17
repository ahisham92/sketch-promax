using SketchProMax.Chat;

/// <summary>The chat's model picker: the list, the remembered choice, and falling back when the plan refuses a model.</summary>
internal static class ChatModelTests
{
    public static void Run(Action<string, bool, string> check)
    {
        check("models: four choices, default among them", ChatModels.All.Count == 4 && ChatModels.All.Any(c => c.Id == ChatModels.DefaultId), "");
        check("models: unknown id is the default", ChatModels.Find("nope").Id == ChatModels.DefaultId && ChatModels.Find(null).Id == ChatModels.DefaultId, "");
        check("models: find ignores case and spaces", ChatModels.Find(" CLAUDE-OPUS-5-5\n").Label == "Opus 5.5", "");

        check("models: refusal recognised", ChatModels.IsRejection(
            "There's an issue with the selected model (claude-opus-5-5). It may not exist or you may not have access to it. Run /model to pick a different model."), "");
        check("models: 404 for the model recognised", ChatModels.IsRejection("API Error: 404 {\"type\":\"error\",\"error\":{\"type\":\"not_found_error\",\"message\":\"model: claude-x\"}}"), "");
        check("models: plan wording recognised", ChatModels.IsRejection("This model is not available on your plan."), "");
        check("models: other errors are not refusals", !ChatModels.IsRejection("Claude Code stopped unexpectedly") && !ChatModels.IsRejection("rate limit reached") && !ChatModels.IsRejection(""), "");

        check("models: back to the last that worked", ChatModels.Fallback("claude-opus-5-5", "claude-haiku-5-5") == "claude-haiku-5-5", "");
        check("models: back to the default when the last one is refused", ChatModels.Fallback("claude-opus-5-5", "claude-opus-5-5") == ChatModels.DefaultId, "");
        check("models: nothing left when the default is refused", ChatModels.Fallback(ChatModels.DefaultId, ChatModels.DefaultId) is null, "");

        var folder = Path.Combine(Path.GetTempPath(), "spm-model-" + Guid.NewGuid().ToString("N"));
        try
        {
            check("models: default before a choice", ChatModels.Load(folder) == ChatModels.DefaultId, "");
            ChatModels.Save(folder, "claude-haiku-5-5");
            check("models: last choice remembered", ChatModels.Load(folder) == "claude-haiku-5-5", "");
            File.WriteAllText(Path.Combine(folder, "model.txt"), "garbage");
            check("models: damaged file is the default", ChatModels.Load(folder) == ChatModels.DefaultId, "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
