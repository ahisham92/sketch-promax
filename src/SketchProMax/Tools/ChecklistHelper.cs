namespace SketchProMax.Tools;

/// <summary>
/// The checklist's helper for checks the model test can't settle (Settings > Sign-off > Helper): the Sketch ProMax chat
/// (the question goes to it and it looks at the model), SketchPro (the question is copied to paste into its chat; it is
/// another company's add-in, so Sketch ProMax can't type into it), or none. The helper only advises: the user still
/// answers. No Revit or WPF here, so the tests check the question.
/// </summary>
internal static class ChecklistHelper
{
    public const string ProMax = "promax", SketchPro = "sketchpro", None = "none";

    public static readonly (string Tag, string Text)[] Choices =
    [
        (ProMax, "Sketch ProMax chat (recommended)"),
        (SketchPro, "SketchPro (copy the question to paste)"),
        (None, "None"),
    ];

    public static string Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        SketchPro => SketchPro,
        None => None,
        _ => ProMax,
    };

    /// <summary>The Ask button's text, or null when there is no helper.</summary>
    public static string? Button(string helper) => Parse(helper) switch
    {
        ProMax => "Ask Sketch ProMax to check",
        SketchPro => "Copy the question for SketchPro",
        _ => null,
    };

    /// <summary>The question for the helper: the check, how to check it, what the model test found, and how to answer.</summary>
    public static string Question(ChecklistMath.Item item, string model, string? found)
    {
        var lines = new List<string>
        {
            $"Pre-submission checklist, check #{item.Number} for the open model {model}: {item.Check}",
        };
        if (item.Example.Length > 0) lines.Add("How to check: " + item.Example.Replace("\r", "").Replace("\n", " / "));
        if (!string.IsNullOrWhiteSpace(found)) lines.Add("The automatic model test said: " + found);
        lines.Add("Check it in the open model with your tools: query the elements, list the views, sheets and schedules, and look at pictures of the views where it helps. Don't change anything in the model.");
        lines.Add("Answer with PASS, FAIL or CAN'T TELL on the first line, then what you found (names and counts), then how to fix anything that fails.");
        return string.Join("\n", lines);
    }
}
