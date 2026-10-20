using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Tools;

namespace SketchProMax.Commands;

/// <summary>
/// The ribbon's Redlines to sheets when Claude Code reads the pages (a file given, no markups). A page's reading can take
/// minutes, so it runs off Revit's thread as a job: the Job button shows the page it is on and stops it, and Revit stays
/// usable meanwhile. Only the short steps come back to Revit through the dispatcher, each its own API call: the page
/// image (Revit rasterises a PDF page) and, once every page is read, the apply itself (the approvals, the changes, the
/// clouds, the export), which takes the markups read here instead of reading them again (<see cref="Redline.HandOver"/>).
/// The result window opens when it ends.
/// </summary>
internal static class RibbonRedlines
{
    /// <summary>
    /// True for a ribbon redline_apply that would have Claude Code read the pages: a file and no markups, and not a
    /// repeat of markups just applied (that returns the earlier run's result at once, with nothing to read).
    /// </summary>
    public static bool ReadsPages(string tool, Dictionary<string, JsonElement> args, Document doc)
    {
        var a = new ToolArgs(args);
        if (tool != "redline_apply" || a.Has("markups") || string.IsNullOrWhiteSpace(a.OptString("file"))) return false;
        if (a.OptBool("dry_run") || a.OptBool("force")) return true;
        return RecentRuns.Repeat(tool, RecentRuns.Key(tool, Checks.ModelKey(doc), args), DateTime.Now) is null;
    }

    /// <summary>Starts the reading as a job and returns at once; the apply and the result follow when every page is read.</summary>
    public static Autodesk.Revit.UI.Result Start(UIApplication app, IRevitTool tool, Dictionary<string, JsonElement> args, string title)
    {
        var doc = app.ActiveUIDocument.Document;
        string file;
        List<int> pages;
        try
        {
            var a = new ToolArgs(args);
            file = Redlines.CheckedFile(a.OptString("file"));
            pages = Redlines.Pages(a, file);
        }
        catch (ArgumentException ex)
        {
            TaskDialog.Show("Sketch ProMax", ex.Message);
            return Autodesk.Revit.UI.Result.Failed;
        }
        if (Redlines.TestExtract is null && Chat.ClaudeCli.Find() is null)
        {
            TaskDialog.Show("Sketch ProMax", "Claude Code reads the marked-up pages, and it was not found on this PC. Open Chat on the Sketch ProMax tab " +
                                              "to install it, or attach the page in the chat and ask for the redlines to be applied there.");
            return Autodesk.Revit.UI.Result.Failed;
        }
        var job = Jobs.TryCreate(tool.Name, out var inTheWay);
        if (job is null)
        {
            TaskDialog.Show("Sketch ProMax", Jobs.Busy(inTheWay).Text);
            return Autodesk.Revit.UI.Result.Cancelled;
        }
        job.TryStart();
        Jobs.Running = job;
        job.StartedSignal.TrySetResult(true);
        Jobs.Report($"Reading redlines: page {pages[0]} (1 of {pages.Count})");
        var modelKey = Checks.ModelKey(doc);
        job.Task = Task.Run(() => Run(job, tool, args, title, file, pages, modelKey));
        TaskDialog.Show("Sketch ProMax",
            $"Claude Code is reading {pages.Count} page(s) of {Path.GetFileName(file)}, which can take a few minutes a page. Revit stays usable " +
            "meanwhile: the Job button shows the page it is on, and clicking it stops the reading. When every page is read, Revit asks " +
            "about the changes as usual and shows the result.");
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    private static async Task<ToolResult> Run(Job job, IRevitTool tool, Dictionary<string, JsonElement> args, string title, string file, List<int> pages, string modelKey)
    {
        ToolResult result;
        var markups = new List<Redline.Markup>();
        var notes = new List<string>();
        var problems = new List<string>();
        var stopped = false;
        try
        {
            var folder = Redlines.WorkFolder();
            // The page images are only for this reading: removed afterwards, whatever happens.
            try
            {
                for (var i = 0; i < pages.Count; i++)
                {
                    if (job.CancelRequested) { stopped = true; break; }
                    var page = pages[i];
                    Jobs.Report($"Reading redlines: page {page} ({i + 1} of {pages.Count})");
                    JobFeed.Item("Reading redlines", i, pages.Count, $"page {page}");
                    string png;
                    try { png = await App.Dispatcher.InvokeAsync(app => Redlines.PageImage(Model(app, modelKey), file, page, folder)).ConfigureAwait(false); }
                    catch (ArgumentException ex) when (i > 0 && Redlines.IsPdf(file)) { notes.Add($"page {page}: {ex.Message}"); break; } // past the last page
                    // Here, off Revit's thread: Claude Code reads the image (it is ended when the job is asked to stop).
                    var answer = Redlines.Extract(png, page, out var error);
                    if (answer is null) { notes.Add($"page {page}: the markups could not be read ({error})"); continue; }
                    Redline.AddPage(markups, problems, answer, page);
                }
            }
            catch (OperationCanceledException) { stopped = true; }
            finally { Redlines.Remove(folder); }

            if (stopped || job.CancelRequested)
                result = ToolResult.Error("Stopped on request while Claude Code read the pages. Nothing in the model was changed.");
            else
            {
                Redline.UniqueIds(markups);
                notes.AddRange(problems);
                Jobs.Report($"Applying the redlines ({markups.Count} markup(s) read)");
                // The apply: one Revit API call, the same run as any ribbon tool, with the markups just read.
                result = await App.Dispatcher.InvokeAsync(app =>
                {
                    var doc = Model(app, modelKey);
                    Redline.HandOver(new Redline.ReadAhead(file, pages, markups, notes));
                    try
                    {
                        using (PackProgress.Show(title, $"{title} on '{doc.Title}'"))
                            return QuickTools.RunForJob(app, tool, args, job);
                    }
                    finally { Redline.Discard(); }
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            result = ToolResult.Error($"{ex.Message} Nothing in the model was changed.");
        }
        catch (Exception ex)
        {
            result = ToolResult.Error($"Redlines to sheets stopped unexpectedly ({ex.GetType().Name}: {ex.Message}). Nothing in the model was changed.");
        }
        finally
        {
            job.Finished = DateTime.UtcNow;
            if (Jobs.Running == job) Jobs.Running = null;
        }
        var shown = result;
        App.Ui?.BeginInvoke(new Action(() =>
        {
            try { RibbonTools.ShowResult(title, shown); }
            catch (Exception) { /* the result is still with the job (the Job button) */ }
        }));
        return result;
    }

    /// <summary>The model the run started on; throws when another one is active now (the markups are for its sheets).</summary>
    private static Document Model(UIApplication app, string modelKey)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || Checks.ModelKey(doc) != modelKey)
            throw new InvalidOperationException("The model Redlines to sheets started on is not the active one any more. Switch back to it and run Redlines to sheets again.");
        return doc;
    }
}
