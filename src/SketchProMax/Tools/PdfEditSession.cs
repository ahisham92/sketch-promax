using System.IO;

namespace SketchProMax.Tools;

/// <summary>
/// One page of the PDF being edited: page <see cref="Index"/> of open file <see cref="Source"/> (-1: a blank page),
/// turned <see cref="Turns"/> quarter turns clockwise. <see cref="Width"/> and <see cref="Height"/> are its size in
/// points as the file shows it, before the turns. Compared by reference: the same page added twice is two pages.
/// </summary>
internal sealed class PdfPage(int source, int index, int turns, double width, double height)
{
    public int Source { get; } = source;
    public int Index { get; } = index;
    public int Turns { get; } = ((turns % 4) + 4) % 4;
    public double Width { get; } = width;
    public double Height { get; } = height;

    /// <summary>Width over height as it shows, the turns applied.</summary>
    public double Aspect => Turns % 2 == 0 ? Width / Math.Max(1, Height) : Height / Math.Max(1, Width);

    public PdfPage Turned(int quarterTurns) => new(Source, Index, Turns + quarterTurns, Width, Height);
}

/// <summary>
/// The PDF editor's document: the pages in their new order, taken from one or more open PDFs, with undo and redo.
/// Edits change only this list; nothing is written until Save, Save as, Extract or Split. Saving over a file keeps
/// the old one in a backups folder first. The open files are read through PDFium on its own thread (<see cref="PdfEngine"/>).
/// No Revit and no WPF here: the tests compile it.
/// </summary>
internal sealed class PdfEditSession : IDisposable
{
    private sealed record SourceFile(string Path, string? Password)
    {
        public PdfEngine.Doc? Doc { get; set; }
    }

    private readonly List<SourceFile> _sources = [];
    private List<PdfPage> _pages = [];
    private readonly Stack<List<PdfPage>> _undo = new(), _redo = new();
    private const int UndoSteps = 100;

    /// <summary>The pages, in order.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>The file Save writes back to: the PDF that was opened, or the last one saved. Null after a merge until saved.</summary>
    public string? File { get; private set; }

    /// <summary>True when there are edits not saved yet.</summary>
    public bool Changed { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>The name of an open file (for a page's tooltip).</summary>
    public string SourceName(int source) => source >= 0 && source < _sources.Count ? Path.GetFileName(_sources[source].Path) : "Blank page";

    /// <summary>The full path of an open file, or null for a blank page.</summary>
    public string? SourcePath(int source) => source >= 0 && source < _sources.Count ? _sources[source].Path : null;

    /// <summary>
    /// Opens <paramref name="path"/> and returns its pages, not yet placed (Insert or Replace places them). Throws
    /// <see cref="PdfEngine.OpenException"/> with what to do; its Password flag asks for a password (or another one).
    /// </summary>
    public List<PdfPage> Load(string path, string? password = null)
    {
        path = System.IO.Path.GetFullPath(path);
        return PdfEngine.Run(() =>
        {
            var doc = PdfEngine.Doc.Open(path, password);
            var source = new SourceFile(path, password) { Doc = doc };
            _sources.Add(source);
            var at = _sources.Count - 1;
            var pages = new List<PdfPage>(doc.Pages);
            for (var i = 0; i < doc.Pages; i++)
            {
                var (w, h) = doc.Size(i);
                pages.Add(new PdfPage(at, i, 0, w, h));
            }
            return pages;
        });
    }

    /// <summary>Opens <paramref name="path"/> as the document (the first file of a session): Save writes back to it.</summary>
    public void Open(string path, string? password = null)
    {
        var pages = Load(path, password);
        _pages = pages;
        _undo.Clear();
        _redo.Clear();
        File = System.IO.Path.GetFullPath(path);
        Changed = false;
    }

    /// <summary>Starts a merge: the pages of the first file, with no file to save back to (Save asks where).</summary>
    public void StartMerge(List<PdfPage> first)
    {
        _pages = [.. first];
        _undo.Clear();
        _redo.Clear();
        File = null;
        Changed = true;
    }

    // ---- Edits (each one can be undone) ----------------------------------------------------------------------------

    private void Edit(List<PdfPage> next)
    {
        _undo.Push(_pages);
        if (_undo.Count > UndoSteps) TrimUndo();
        _redo.Clear();
        _pages = next;
        Changed = true;
    }

    private void TrimUndo()
    {
        var kept = _undo.Take(UndoSteps).Reverse().ToList();
        _undo.Clear();
        foreach (var k in kept) _undo.Push(k);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(_pages);
        _pages = _undo.Pop();
        Changed = true;
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(_pages);
        _pages = _redo.Pop();
        Changed = true;
        return true;
    }

    /// <summary>Puts <paramref name="pages"/> before position <paramref name="at"/> (the page count: at the end).</summary>
    public void Insert(int at, IReadOnlyList<PdfPage> pages)
    {
        if (pages.Count == 0) return;
        at = Math.Max(0, Math.Min(_pages.Count, at));
        var next = new List<PdfPage>(_pages);
        next.InsertRange(at, pages);
        Edit(next);
    }

    /// <summary>Removes the pages at <paramref name="positions"/>.</summary>
    public void Delete(IEnumerable<int> positions)
    {
        var gone = new HashSet<int>(positions.Where(p => p >= 0 && p < _pages.Count));
        if (gone.Count == 0) return;
        Edit(_pages.Where((_, i) => !gone.Contains(i)).ToList());
    }

    /// <summary>Turns the pages at <paramref name="positions"/> by <paramref name="quarterTurns"/> (1: right, -1: left).</summary>
    public void Rotate(IEnumerable<int> positions, int quarterTurns)
    {
        var turn = new HashSet<int>(positions.Where(p => p >= 0 && p < _pages.Count));
        if (turn.Count == 0 || quarterTurns % 4 == 0) return;
        Edit(_pages.Select((p, i) => turn.Contains(i) ? p.Turned(quarterTurns) : p).ToList());
    }

    /// <summary>
    /// Moves the pages at <paramref name="positions"/>, in their order, to just before the page now at
    /// <paramref name="before"/> (the page count: to the end). Returns where the first moved page now is.
    /// </summary>
    public int MoveTo(IEnumerable<int> positions, int before)
    {
        var moving = positions.Where(p => p >= 0 && p < _pages.Count).Distinct().OrderBy(p => p).ToList();
        if (moving.Count == 0) return -1;
        before = Math.Max(0, Math.Min(_pages.Count, before));
        var set = new HashSet<int>(moving);
        var at = before - moving.Count(p => p < before);
        var rest = _pages.Where((_, i) => !set.Contains(i)).ToList();
        var next = new List<PdfPage>(rest);
        next.InsertRange(at, moving.Select(p => _pages[p]));
        if (next.SequenceEqual(_pages)) return at;
        Edit(next);
        return at;
    }

    /// <summary>Moves the pages at <paramref name="positions"/> one place earlier (-1) or later (1). Returns the new positions.</summary>
    public List<int> Nudge(IEnumerable<int> positions, int step)
    {
        var moving = positions.Where(p => p >= 0 && p < _pages.Count).Distinct().OrderBy(p => p).ToList();
        if (moving.Count == 0) return moving;
        var first = moving[0];
        var last = moving[^1];
        var contiguous = last - first + 1 == moving.Count;
        if (step < 0 && first == 0 || step > 0 && last == _pages.Count - 1) return moving;
        if (!contiguous)
        {
            // Pages picked here and there: gather them where the first one is, then they move together next time.
            var at = MoveTo(moving, first);
            return Enumerable.Range(at, moving.Count).ToList();
        }
        var target = step < 0 ? first - 1 : last + 2;
        var start = MoveTo(moving, target);
        return Enumerable.Range(start, moving.Count).ToList();
    }

    /// <summary>
    /// Replaces the pages at <paramref name="positions"/> with <paramref name="with"/>, which go where the first of
    /// them was. Returns that position.
    /// </summary>
    public int Replace(IEnumerable<int> positions, IReadOnlyList<PdfPage> with)
    {
        var gone = positions.Where(p => p >= 0 && p < _pages.Count).Distinct().OrderBy(p => p).ToList();
        if (gone.Count == 0 || with.Count == 0) return -1;
        var set = new HashSet<int>(gone);
        var next = _pages.Where((_, i) => !set.Contains(i)).ToList();
        next.InsertRange(gone[0], with);
        Edit(next);
        return gone[0];
    }

    /// <summary>A blank page the size of <paramref name="like"/> (A4 upright when null).</summary>
    public static PdfPage Blank(PdfPage? like) =>
        like is null ? new PdfPage(-1, 0, 0, 595, 842) : new PdfPage(-1, 0, 0, like.Turns % 2 == 0 ? like.Width : like.Height, like.Turns % 2 == 0 ? like.Height : like.Width);

    // ---- Pictures ----------------------------------------------------------------------------------------------------

    /// <summary>A picture of <paramref name="page"/>, queued behind other PDF work.</summary>
    public Task<PdfEngine.Picture?> DrawLater(PdfPage page, int maxWidth, int maxHeight, Func<bool>? stillWanted = null) =>
        PdfEngine.Later<PdfEngine.Picture?>(() => stillWanted?.Invoke() == false ? null : Draw(page, maxWidth, maxHeight));

    /// <summary>A picture of <paramref name="page"/> now (on the PDF thread when called from elsewhere).</summary>
    public PdfEngine.Picture Draw(PdfPage page, int maxWidth, int maxHeight) =>
        PdfEngine.Run(() => PdfEngine.Draw(page.Source >= 0 ? DocOf(page.Source) : null, page.Index, page.Turns, maxWidth, maxHeight, (page.Width, page.Height)));

    private PdfEngine.Doc DocOf(int source)
    {
        var s = _sources[source];
        return s.Doc ?? throw new InvalidOperationException($"{System.IO.Path.GetFileName(s.Path)} is not open any more.");
    }

    // ---- Writing -----------------------------------------------------------------------------------------------------

    private List<PdfEngine.Out> Outs(IEnumerable<PdfPage> pages) =>
        pages.Select(p => new PdfEngine.Out(p.Source >= 0 ? DocOf(p.Source) : null, p.Index, p.Turns, (p.Width, p.Height))).ToList();

    /// <summary>Writes <paramref name="pages"/> to a new file <paramref name="target"/> (Extract, a split's part). Returns its size.</summary>
    public long WriteCopy(IReadOnlyList<PdfPage> pages, string target, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var list = pages.ToList();
        return PdfEngine.Run(() => PdfEngine.Write(Outs(list), target, progress, ct));
    }

    /// <summary>
    /// Saves the pages to <paramref name="target"/>. They are written to a temporary file first; a file already there
    /// is copied to <paramref name="backups"/> (when given; the newest 20 are kept) and then replaced. Afterwards
    /// <paramref name="target"/> is the document, opened afresh (undo starts again). Returns the backup's path, if one was made.
    /// </summary>
    public string? Save(string target, string? backups, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        target = System.IO.Path.GetFullPath(target);
        var list = _pages.ToList();
        var folder = System.IO.Path.GetDirectoryName(target) ?? "";
        var temp = System.IO.Path.Combine(folder, "~" + System.IO.Path.GetFileNameWithoutExtension(target) + " saving " + Guid.NewGuid().ToString("N").Substring(0, 6) + ".pdf");
        return PdfEngine.Run(() =>
        {
            PdfEngine.Write(Outs(list), temp, progress, ct);
            string? backup = null;
            try
            {
                if (System.IO.File.Exists(target) && backups is not null) backup = Backup(target, backups);
                // The open files are closed so the target can be replaced even when it is one of them, then opened again.
                CloseAll();
                try
                {
                    if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
                    System.IO.File.Move(temp, target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ReopenAll();
                    throw new IOException($"{System.IO.Path.GetFileName(target)} couldn't be replaced: it may be open in another program, or read-only. " +
                                          "Close it there, or use Save as to save under another name.", ex);
                }
            }
            finally
            {
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            // Start again from the saved file: its pages in order, nothing to undo.
            CloseAll();
            _sources.Clear();
            var doc = PdfEngine.Doc.Open(target);
            _sources.Add(new SourceFile(target, null) { Doc = doc });
            var pages = new List<PdfPage>(doc.Pages);
            for (var i = 0; i < doc.Pages; i++)
            {
                var (w, h) = doc.Size(i);
                pages.Add(new PdfPage(0, i, 0, w, h));
            }
            _pages = pages;
            _undo.Clear();
            _redo.Clear();
            File = target;
            Changed = false;
            return backup;
        });
    }

    /// <summary>Copies <paramref name="file"/> into <paramref name="folder"/> with the time in its name, keeping the newest 20.</summary>
    internal static string Backup(string file, string folder)
    {
        Directory.CreateDirectory(folder);
        var copy = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileNameWithoutExtension(file)} {DateTime.Now:yyyy-MM-dd HHmmss}.pdf");
        System.IO.File.Copy(file, copy, true);
        foreach (var old in new DirectoryInfo(folder).GetFiles("*.pdf").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).Skip(20))
        {
            try { old.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return copy;
    }

    private void CloseAll()
    {
        foreach (var s in _sources)
        {
            s.Doc?.Dispose();
            s.Doc = null;
        }
    }

    private void ReopenAll()
    {
        foreach (var s in _sources)
        {
            try { s.Doc ??= PdfEngine.Doc.Open(s.Path, s.Password); }
            catch (PdfEngine.OpenException) { /* gone meanwhile: its pages can't be drawn or saved, and say so then */ }
        }
    }

    /// <summary>
    /// Page numbers typed as "1-3, 5, 8-" (8 to the end) or "all", as 0-based positions in the order typed; null when
    /// something can't be read or a page is outside 1 to <paramref name="count"/>.
    /// </summary>
    internal static List<int>? ParseRange(string text, int count)
    {
        text = text.Trim();
        if (text.Length == 0 || count <= 0) return null;
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase)) return Enumerable.Range(0, count).ToList();
        var pages = new List<int>();
        foreach (var raw in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = raw.IndexOfAny(['-', '–']);
            var a = dash < 0 ? raw : raw.Substring(0, dash);
            var b = dash < 0 ? raw : raw.Substring(dash + 1);
            int from, to;
            if (a.Length == 0) from = 1;
            else if (!int.TryParse(a, out from)) return null;
            if (b.Length == 0) to = count;
            else if (!int.TryParse(b, out to)) return null;
            if (from < 1 || to < 1 || from > count || to > count) return null;
            var step = to >= from ? 1 : -1;
            for (var n = from; n != to + step; n += step) pages.Add(n - 1);
        }
        return pages.Count == 0 ? null : pages;
    }

    /// <summary>"3", "3-5" or "3, 5, 9" for page positions (0-based), as a file name likes them.</summary>
    internal static string Describe(IReadOnlyList<int> positions)
    {
        var sorted = positions.Distinct().OrderBy(p => p).ToList();
        if (sorted.Count == 0) return "";
        if (sorted[sorted.Count - 1] - sorted[0] + 1 == sorted.Count)
            return sorted.Count == 1 ? $"{sorted[0] + 1}" : $"{sorted[0] + 1}-{sorted[sorted.Count - 1] + 1}";
        var text = string.Join(", ", sorted.Take(6).Select(p => p + 1));
        return sorted.Count > 6 ? text + " and more" : text;
    }

    // ---- Splitting ---------------------------------------------------------------------------------------------------

    public enum SplitBy { EveryN, AtSelected, Size }

    public sealed record Part(string File, int First, int Last, long Bytes);

    /// <summary>
    /// Where the parts start (positions, 0-based): every <paramref name="n"/> pages, or at each of
    /// <paramref name="starts"/> (the first page always starts one).
    /// </summary>
    internal static List<int> Starts(int count, SplitBy by, int n, IEnumerable<int>? starts)
    {
        if (count <= 0) return [];
        if (by == SplitBy.EveryN)
        {
            n = Math.Max(1, n);
            return Enumerable.Range(0, (count + n - 1) / n).Select(k => k * n).ToList();
        }
        return new[] { 0 }.Concat(starts ?? []).Where(p => p >= 0 && p < count).Distinct().OrderBy(p => p).ToList();
    }

    /// <summary>"name - part 2 (pages 5-8).pdf" in <paramref name="folder"/>, with " (2)" added when that name is taken.</summary>
    internal static string PartName(string folder, string name, int part, int first, int last)
    {
        var pages = first == last ? $"page {first}" : $"pages {first}-{last}";
        var file = System.IO.Path.Combine(folder, $"{name} - part {part} ({pages}).pdf");
        for (var k = 2; System.IO.File.Exists(file); k++) file = System.IO.Path.Combine(folder, $"{name} - part {part} ({pages}) ({k}).pdf");
        return file;
    }

    /// <summary>
    /// Writes the pages as several PDFs in <paramref name="folder"/>: every <paramref name="n"/> pages, from each of the
    /// <paramref name="starts"/>, or (by size) as many pages as fit in <paramref name="maxBytes"/> (a page larger on its
    /// own is a part of its own). <paramref name="progress"/> gets pages done and the page count.
    /// </summary>
    public List<Part> Split(string folder, string name, SplitBy by, int n = 1, IEnumerable<int>? starts = null, long maxBytes = 0,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var list = _pages.ToList();
        Directory.CreateDirectory(folder);
        var parts = new List<Part>();
        if (by != SplitBy.Size)
        {
            var at = Starts(list.Count, by, n, starts);
            for (var k = 0; k < at.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                var first = at[k];
                var last = k + 1 < at.Count ? at[k + 1] - 1 : list.Count - 1;
                var file = PartName(folder, name, k + 1, first + 1, last + 1);
                var bytes = WriteCopy(list.GetRange(first, last - first + 1), file, null, ct);
                parts.Add(new Part(file, first + 1, last + 1, bytes));
                progress?.Invoke(last + 1, list.Count);
            }
            return parts;
        }
        if (maxBytes < 100 * 1024) throw new ArgumentException("The part size is at least 0.1 MB.");
        // By size: guess the pages from the average page size so far, then halve until the part fits.
        var temp = System.IO.Path.Combine(folder, "~part " + Guid.NewGuid().ToString("N").Substring(0, 6) + ".pdf");
        var start = 0;
        var perPage = 0.0;
        try
        {
            while (start < list.Count)
            {
                ct.ThrowIfCancellationRequested();
                var count = perPage <= 0 ? Math.Min(list.Count - start, 50) : Math.Max(1, Math.Min(list.Count - start, (int)Math.Min(int.MaxValue, maxBytes * 0.9 / perPage)));
                long bytes;
                while (true)
                {
                    bytes = WriteCopy(list.GetRange(start, count), temp, null, ct);
                    perPage = bytes / (double)count;
                    if (bytes <= maxBytes || count == 1) break;
                    count = Math.Max(1, Math.Min(count - 1, (int)(count * (maxBytes * 0.9 / bytes))));
                }
                // Grow a first guess that came out well under the limit (only while nothing was written yet for this part).
                while (start + count < list.Count && bytes < maxBytes * 0.6)
                {
                    var more = Math.Min(list.Count - start, Math.Max(count + 1, (int)Math.Min(int.MaxValue, count * (maxBytes * 0.85 / Math.Max(1, bytes)))));
                    var bigger = WriteCopy(list.GetRange(start, more), temp + ".x", null, ct);
                    if (bigger > maxBytes) break;
                    System.IO.File.Copy(temp + ".x", temp, true);
                    count = more;
                    bytes = bigger;
                    perPage = bytes / (double)count;
                }
                var file = PartName(folder, name, parts.Count + 1, start + 1, start + count);
                System.IO.File.Move(temp, file);
                parts.Add(new Part(file, start + 1, start + count, bytes));
                start += count;
                progress?.Invoke(start, list.Count);
            }
            return parts;
        }
        finally
        {
            foreach (var f in new[] { temp, temp + ".x" })
            {
                try { if (System.IO.File.Exists(f)) System.IO.File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public void Dispose()
    {
        try { PdfEngine.Run(() => { CloseAll(); return 0; }); }
        catch (InvalidOperationException) { }
    }
}
