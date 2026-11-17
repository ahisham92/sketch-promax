using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using SketchProMax.Tools;

namespace SketchProMax.Revit;

/// <summary>
/// Keeps Revit's warning and error dialogs from blocking Claude's work. While a tool runs, every transaction's
/// warnings are dismissed and noted, and any error rolls that transaction back instead of asking the user
/// ("Error - cannot be ignored ... Delete Element(s) / Cancel"). The tool's result then says what Revit refused.
/// Hooked to Application.FailuresProcessing, so it covers every transaction, including ones in user code.
/// </summary>
public static class FailureGuard
{
    private static bool _active;
    private static readonly List<string> Errors = [];
    private static readonly List<string> Warnings = [];

    /// <summary>
    /// While above zero, failures are still rolled back but not reported in the tool's result: the caller (ItemBatch)
    /// reports them per item itself.
    /// </summary>
    internal static int Muted;

    /// <summary>
    /// While above zero (the field test's own steps): no Revit dialog may wait for a click, but warnings stay in the
    /// model, since some checks read them back (Revit's "identical instances in the same place" warning, which the
    /// duplicates fix lists). Warnings are kept and shown in Revit's corner instead of a dialog; errors are rolled back.
    /// On 17 Nov 2026 that warning's dialog held the field test, and Revit, for 32 minutes until it was clicked.
    /// </summary>
    internal static int Probing;

    public static void Handle(object? sender, FailuresProcessingEventArgs e)
    {
        if (Probing > 0 && !_active) { KeepWarningsNoDialog(e); return; }
        if (!_active || Muted > 0) return;
        var accessor = e.GetFailuresAccessor();
        var hasError = false;
        foreach (var f in accessor.GetFailureMessages())
        {
            var text = f.GetDescriptionText();
            if (f.GetSeverity() == FailureSeverity.Warning)
            {
                if (Warnings.Count < 20 && !Warnings.Contains(text)) Warnings.Add(text);
                accessor.DeleteWarning(f);
            }
            else
            {
                hasError = true;
                if (Errors.Count < 20 && !Errors.Contains(text)) Errors.Add(text);
            }
        }
        if (!hasError) return;
        var options = accessor.GetFailureHandlingOptions();
        options.SetClearAfterRollback(true);
        options.SetForcedModalHandling(false);
        accessor.SetFailureHandlingOptions(options);
        e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
    }

    private static void KeepWarningsNoDialog(FailuresProcessingEventArgs e)
    {
        var accessor = e.GetFailuresAccessor();
        var messages = accessor.GetFailureMessages();
        if (messages.Count == 0) return;
        var options = accessor.GetFailureHandlingOptions();
        options.SetForcedModalHandling(false);
        options.SetClearAfterRollback(true);
        accessor.SetFailureHandlingOptions(options);
        if (messages.Any(f => f.GetSeverity() != FailureSeverity.Warning)) e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
    }

    /// <summary>Runs a field-test step so that no Revit dialog can wait for a click (see <see cref="Probing"/>).</summary>
    public static T WithoutDialogs<T>(Func<T> body)
    {
        Probing++;
        try { return body(); }
        finally { Probing--; }
    }

    /// <summary>Runs a tool with the guard on and folds what Revit refused into its result.</summary>
    public static ToolResult Run(Func<ToolResult> tool)
    {
        Errors.Clear();
        Warnings.Clear();
        _active = true;
        ToolResult result;
        try { result = tool(); }
        finally { _active = false; }

        if (Errors.Count > 0) Chat.Autopilot.Log(Chat.AutopilotEntry.Popup, "Revit refused a step, which was undone: " + string.Join("; ", Errors));
        if (Warnings.Count > 0) Chat.Autopilot.Log(Chat.AutopilotEntry.Popup, "Revit warnings dismissed: " + string.Join("; ", Warnings));
        if (Errors.Count > 0)
            return new ToolResult(
                $"Revit refused part of this change and undid that step: {string.Join("; ", Errors)}. " +
                "Whatever that step created does not exist; anything below from other steps was kept.\n" + result.Text,
                IsError: true, result.PngImage);
        if (Warnings.Count > 0 && !result.IsError)
            return result with { Text = result.Text + $"\nRevit warnings (dismissed): {string.Join("; ", Warnings)}" };
        return result;
    }
}
