using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace SketchProMax.Tools;

/// <summary>
/// Turns the checklist's Word file into a PDF through Microsoft Word on this PC, late bound (no Office reference, so
/// nothing breaks where Word is missing). Word lays the PDF out exactly as the Word file prints. Windows only.
/// </summary>
internal static class ChecklistPdf
{
    /// <summary>Word isn't installed (or can't be started), so there is nothing to make the PDF with.</summary>
    public sealed class NoWordException(string message) : Exception(message);

    /// <summary>Converts <paramref name="docx"/> to <paramref name="pdf"/> on its own STA thread, so the window stays responsive.</summary>
    public static Task Convert(string docx, string pdf)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Run(docx, pdf); done.SetResult(true); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Sketch ProMax PDF" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void Run(string docx, string pdf)
    {
        var type = Type.GetTypeFromProgID("Word.Application") ?? throw new NoWordException("Microsoft Word isn't installed on this PC.");
        object? word = null, docs = null, doc = null;
        try
        {
            try { word = Activator.CreateInstance(type); }
            catch (COMException ex) { throw new NoWordException("Microsoft Word couldn't be started (" + ex.Message + ")."); }
            if (word is null) throw new NoWordException("Microsoft Word couldn't be started.");
            Set(word, "Visible", false);
            Set(word, "DisplayAlerts", 0); // wdAlertsNone
            docs = Call(word, "Documents", BindingFlags.GetProperty);
            // Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles)
            doc = Call(docs!, "Open", BindingFlags.InvokeMethod, docx, false, true, false);
            // ExportAsFixedFormat(OutputFileName, ExportFormat = wdExportFormatPDF)
            Call(doc!, "ExportAsFixedFormat", BindingFlags.InvokeMethod, pdf, 17);
        }
        finally
        {
            if (doc is not null) { Quietly(() => Call(doc, "Close", BindingFlags.InvokeMethod, 0)); Release(doc); } // wdDoNotSaveChanges
            if (docs is not null) Release(docs);
            if (word is not null) { Quietly(() => Call(word, "Quit", BindingFlags.InvokeMethod, 0)); Release(word); }
        }
        if (!File.Exists(pdf)) throw new IOException("Word finished without writing the PDF.");
    }

    private static object? Call(object target, string name, BindingFlags how, params object[] args)
    {
        try { return target.GetType().InvokeMember(name, how, null, target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }

    private static void Set(object target, string name, object value) => Call(target, name, BindingFlags.SetProperty, value);

    private static void Quietly(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or IOException) { }
    }

    private static void Release(object com)
    {
        try { if (Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); }
        catch (ArgumentException) { }
    }
}
