using SketchProMax.Chat;

/// <summary>The chat's model picker: the list, the remembered choice, and falling back when the plan refuses a model.</summary>
internal static class ChatModelTests
{
    public static void Run(Action<string, bool, string> check)
    {
        check("models: four choices, default among them", ChatModels.All.Count == 4 && ChatModels.All.Any(c => c.Id == ChatModels.DefaultId), "");
        check("models: unknown id is the default", ChatModels.Find("nope").Id == ChatModels.DefaultId && ChatModels.Find(null).Id == ChatModels.DefaultId, "");
        check("models: Claude Code's aliases, so each follows the latest", ChatModels.All.Select(c => c.Id).SequenceEqual(["opus", "sonnet", "haiku", "fable"]) && ChatModels.DefaultId == "sonnet", "");
        check("models: find ignores case and spaces", ChatModels.Find(" OPUS\n").Label == "Opus", "");
        check("models: a full name saved by an older version finds its alias", ChatModels.Find("vendor-haiku-full-name").Id == "haiku" && ChatModels.Known("nope") is null, "");

        check("models: refusal recognised", ChatModels.IsRejection(
            "There's an issue with the selected model (opus). It may not exist or you may not have access to it. Run /model to pick a different model."), "");
        check("models: 404 for the model recognised", ChatModels.IsRejection("API Error: 404 {\"type\":\"error\",\"error\":{\"type\":\"not_found_error\",\"message\":\"model: claude-x\"}}"), "");
        check("models: plan wording recognised", ChatModels.IsRejection("This model is not available on your plan."), "");
        check("models: other errors are not refusals", !ChatModels.IsRejection("Claude Code stopped unexpectedly") && !ChatModels.IsRejection("rate limit reached") && !ChatModels.IsRejection(""), "");
        check("models: a Revit tool's model errors are not refusals", !ChatModels.IsRejection("The model element 123 was not found.")
            && !ChatModels.IsRejection("Linked model 'Site' is not available (unloaded).") && !ChatModels.IsRejection("Invalid model path") && !ChatModels.IsRejection("This model may not exist on disk"), "");
        check("models: escaped JSON 404 recognised", ChatModels.IsRejection("API Error: 404 {\\\"type\\\":\\\"not_found_error\\\",\\\"message\\\":\\\"model: claude-x\\\"}"), "");

        check("models: back to the last that worked", ChatModels.Fallback("opus", "haiku") == "haiku", "");
        check("models: back to the default when the last one is refused", ChatModels.Fallback("opus", "opus") == ChatModels.DefaultId, "");
        check("models: nothing left when the default is refused", ChatModels.Fallback(ChatModels.DefaultId, ChatModels.DefaultId) is null, "");

        var folder = Path.Combine(Path.GetTempPath(), "spm-model-" + Guid.NewGuid().ToString("N"));
        try
        {
            check("models: default before a choice", ChatModels.Load(folder) == ChatModels.DefaultId, "");
            ChatModels.Save(folder, "haiku");
            check("models: last choice remembered", ChatModels.Load(folder) == "haiku", "");
            File.WriteAllText(Path.Combine(folder, "model.txt"), "garbage");
            check("models: damaged file is the default", ChatModels.Load(folder) == ChatModels.DefaultId, "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
