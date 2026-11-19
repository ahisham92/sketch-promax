using System.Diagnostics;
using System.IO;
using System.Text;
using SketchProMax.Settings;

namespace SketchProMax.Chat;

/// <summary>
/// The prompt enhancer's runs of Claude Code, apart from the chat's own process: the planning run that reads the model
/// with read-only tools and answers with a brief, and the short run that distils an edited brief into a preference.
/// Everything here blocks on a process: call it off Revit's UI thread (the planning run's tool calls need that thread).
/// </summary>
internal static class BriefRunner
{
    public const int PlanTimeoutMs = 180_000, DistilTimeoutMs = 60_000;

    /// <summary>Runs one message through a one-shot Claude Code and returns the answer, or an error to show.</summary>
    public static async Task<(string Text, string Error)> Run(ClaudeCli.Install install, string instructions, string message, bool readTools,
        string? model, int timeoutMs, CancellationToken cancel)
    {
        ChildProcess? process = null;
        try
        {
            var info = ClaudeCli.OneShotStart(install, instructions, readTools, model);
            process = ChildProcess.Start(info); // inherits none of Revit's open files (bug 72)
            // Stop and closing the pane cancel on Revit's UI thread, and the kill can wait seconds (taskkill): not there.
            var started = process;
            using var stop = cancel.Register(() => Task.Run(() => ClaudeCli.KillTree(started.Process)));
            var stdout = process.StandardOutput!.ReadToEndAsync();
            var stderr = process.StandardError!.ReadToEndAsync();
            // UTF-8 without a byte order mark on every framework (on .NET Framework StandardInput uses the console code page).
            // Fed in the background, inside the timeout: a Claude Code that never reads its input can't hold the brief.
            var feed = Task.Run(async () =>
            {
                using var input = started.StandardInput!;
                await input.WriteAsync(message).ConfigureAwait(false);
            });
            _ = feed.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var done = await Task.WhenAny(stdout, Task.Delay(timeoutMs, cancel)).ConfigureAwait(false);
            if (done != stdout)
            {
                ClaudeCli.KillTree(process.Process);
                return ("", cancel.IsCancellationRequested ? "Stopped." : "The answer took too long.");
            }
            var output = await stdout.ConfigureAwait(false);
            var errors = stderr.Wait(2000) ? stderr.Result : "";
            var (text, isError) = PromptBriefs.ResultOf(output);
            if (isError || text.Length == 0)
            {
                var why = (text.Length > 0 ? text : errors).Trim();
                return ("", why.Length > 0 ? (why.Length > 300 ? why.Substring(0, 300) + "…" : why) : "No answer came back.");
            }
            return (text, "");
        }
        catch (OperationCanceledException) { return ("", "Stopped."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ("", ex.Message);
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) ClaudeCli.KillTree(process.Process); }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    /// <summary>The planning run: reads the model (read-only tools only) and returns the brief, or why there is none.</summary>
    public static async Task<(PromptBrief? Brief, string Error)> Plan(ClaudeCli.Install install, string request, string planMessage, string? model, CancellationToken cancel)
    {
        var standards = AgentSettings.Load().FirmStandards.Trim();
        var instructions = PromptBriefs.PlannerInstructions + (standards.Length > 0 ? "\nThe firm's standards:\n" + standards + "\n" : "");
        var (text, error) = await Run(install, instructions, planMessage, readTools: true, model, PlanTimeoutMs, cancel).ConfigureAwait(false);
        if (error.Length > 0) return (null, error);
        var brief = PromptBriefs.Parse(text, request);
        return brief is null ? (null, "The answer had no brief in it.") : (brief, "");
    }

    /// <summary>
    /// Distils a correction ("no", "still there") into one lesson and keeps it in the memory notes: Global when it holds
    /// for every model, else under <paramref name="modelKey"/>. Every later chat and job reads the notes. Returns the
    /// note kept, or "" when there was nothing to learn, it was already there, or Claude couldn't be asked.
    /// </summary>
    public static async Task<string> RememberLesson(ClaudeCli.Install? install, string request, string answer, string correction, string? modelKey, string? model)
    {
        if (install is null) return "";
        // The notes kept so far go with it, so a lesson that changes one replaces it instead of standing beside it.
        List<(string? Key, string Note)> kept;
        try
        {
            var (g, m) = MemoryNotes.For(MemoryNotes.Load(), modelKey);
            kept = g.Select(n => ((string?)null, n)).Concat(m.Select(n => ((string?)modelKey, n))).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { kept = []; }
        var (text, error) = await Run(install, ChatFocus.LessonInstructions, ChatFocus.LessonMessage(request, answer, correction, kept.Select(k => k.Note).ToList()),
            readTools: false, model, DistilTimeoutMs, CancellationToken.None).ConfigureAwait(false);
        if (error.Length > 0 || ChatFocus.ParseLesson(text) is not { } lesson) return "";
        var key = lesson.Global || string.IsNullOrEmpty(modelKey) ? null : modelKey;
        var replaced = ChatFocus.ParseReplaces(text, kept.Count).Select(n => kept[n - 1]).ToList();
        try
        {
            return MemoryNotes.Change(sections =>
            {
                foreach (var (k, n) in replaced) MemoryNotes.Remove(sections, k, n);
                return MemoryNotes.Add(sections, key, lesson.Note);
            }) ? lesson.Note : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }

    /// <summary>
    /// Distils an edited brief into one preference and saves it to the Global memory notes (the Notes tab). Returns the
    /// note saved, or "" when there was nothing general to keep. Falls back to the plain diff when Claude can't be asked.
    /// </summary>
    public static async Task<string> RememberEdit(ClaudeCli.Install? install, string request, string brief, string edited, string? model)
    {
        if (!PromptBriefs.IsEdit(brief, edited)) return "";
        var note = "";
        if (install is not null)
        {
            var (text, error) = await Run(install, "You distil a user's edit to a brief into one preference. Use no tools.",
                PromptBriefs.DistilMessage(request, brief, edited), readTools: false, model, DistilTimeoutMs, CancellationToken.None).ConfigureAwait(false);
            if (error.Length == 0)
            {
                note = PromptBriefs.PreferenceNote(text);
                if (note.Length == 0) return ""; // Claude said the edit teaches nothing general
            }
        }
        if (note.Length == 0) note = PromptBriefs.FallbackNote(request, brief, edited);
        if (note.Length == 0) return "";
        try
        {
            MemoryNotes.Change(sections => MemoryNotes.Add(sections, null, note));
            return note;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }
}
