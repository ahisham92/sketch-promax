using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
#if NET
using ULong = System.Runtime.InteropServices.CULong;
#else
using ULong = System.UInt32;
#endif

namespace SketchProMax.Tools;

/// <summary>
/// PDFium (BSD-3/Apache 2.0, the PDF engine of Chrome), shipped next to this DLL as SketchProMax.pdfium.dll: a name of
/// our own, so a pdfium.dll that Revit or another add-in loaded is never bound to by mistake. PDFium is not thread safe,
/// so every call runs on one thread of its own (<see cref="Run{T}"/>; page pictures queue behind other work with
/// <see cref="Later{T}"/>). Files are read through a stream, never loaded whole, so a PDF of any size up to 4 GB opens.
/// No Revit and no WPF here: the tests compile it.
/// </summary>
internal static class PdfEngine
{
    // .NET Framework (Revit 2024) passes the DllImport name to LoadLibrary as it is, and LoadLibrary adds ".dll" only to a
    // name with no extension: "SketchProMax.pdfium" reads as a file with the extension ".pdfium", so it was never found
    // (bug: "Unable to load DLL 'SketchProMax.pdfium'"). With ".dll" it binds to the copy Load() loaded from our folder.
#if NET
    internal const string Library = "SketchProMax.pdfium";
#else
    internal const string Library = "SketchProMax.pdfium.dll";
#endif
    internal const string FileName = "SketchProMax.pdfium.dll";

    private static readonly BlockingCollection<Action> Now = new(), Queue = new();
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static string? _loadError;

    /// <summary>Runs <paramref name="work"/> on the PDF thread and waits for it.</summary>
    public static T Run<T>(Func<T> work)
    {
        Start();
        if (Thread.CurrentThread == _thread) return work();
        return Post(Now, work).GetAwaiter().GetResult();
    }

    /// <summary>Queues <paramref name="work"/> behind everything <see cref="Run{T}"/> asks for (page pictures).</summary>
    public static Task<T> Later<T>(Func<T> work)
    {
        Start();
        return Post(Queue, work);
    }

    private static Task<T> Post<T>(BlockingCollection<Action> queue, Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    private static void Start()
    {
        lock (Gate)
        {
            if (_thread is not null) return;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                try { Load(); }
                catch (Exception ex)
                {
                    _loadError = ex.Message;
                }
                ready.Set();
                var queues = new[] { Now, Queue };
                while (true)
                {
                    // TakeFromAny looks at the queues in order, so asked-for work goes before page pictures.
                    BlockingCollection<Action>.TakeFromAny(queues, out var action);
                    action?.Invoke();
                }
            }) { IsBackground = true, Name = "Sketch ProMax PDF" };
            _thread.Start();
            ready.Wait();
        }
    }

    /// <summary>Throws a readable error when the engine couldn't be loaded (call on the PDF thread).</summary>
    private static void Ready()
    {
        if (_loadError is not null)
            throw new InvalidOperationException("The PDF engine (SketchProMax.pdfium.dll) couldn't be loaded: " + _loadError +
                                                " Update Sketch ProMax, or install it again, to put the file back.");
    }

    private static void Load()
    {
        var folder = Path.GetDirectoryName(typeof(PdfEngine).Assembly.Location) ?? "";
        var file = Path.Combine(folder, FileName);
#if NET
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(PdfEngine).Assembly, (name, assembly, path) =>
            {
                if (name != Library) return IntPtr.Zero;
                if (File.Exists(file)) return NativeLibrary.Load(file);
                // The tests: the PDFium package's own library ("pdfium", libpdfium.so on Linux).
                return NativeLibrary.TryLoad("pdfium", assembly, path, out var handle) ? handle : IntPtr.Zero;
            });
        }
        catch (InvalidOperationException) { /* a resolver is already set for this assembly */ }
#else
        if (!File.Exists(file)) throw new DllNotFoundException($"{file} is missing.");
        if (LoadLibraryW(file) == IntPtr.Zero) throw new DllNotFoundException($"{file} couldn't be loaded (Windows error {Marshal.GetLastWin32Error()}).");
#endif
        Native.FPDF_InitLibrary();
    }

#if !NET
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);
#endif

    // ---- Documents -----------------------------------------------------------------------------------------------

    /// <summary>The largest file PDFium reads through a stream (its file length is a 32-bit number on Windows).</summary>
    public static readonly long MaxBytes = Marshal.SizeOf<ULong>() == 4 ? uint.MaxValue : long.MaxValue;

    /// <summary>Why a PDF couldn't be opened: <see cref="Password"/> asks for one (or another).</summary>
    public sealed class OpenException(string message, bool password) : Exception(message)
    {
        public bool Password { get; } = password;
    }

    /// <summary>An open PDF, read through its file (kept open, shared for reading). Use it only on the PDF thread.</summary>
    public sealed class Doc : IDisposable
    {
        public string Path { get; }
        public IntPtr Handle { get; private set; }
        public int Pages { get; }
        private readonly FileStream _stream;
        private readonly Native.GetBlock _read; // kept alive while PDFium may call it
        private readonly IntPtr _access;

        private Doc(string path, FileStream stream, IntPtr handle, IntPtr access, Native.GetBlock read)
        {
            Path = path;
            _stream = stream;
            Handle = handle;
            _access = access;
            _read = read;
            Pages = Native.FPDF_GetPageCount(handle);
        }

        /// <summary>Opens <paramref name="path"/>, or throws <see cref="OpenException"/> with what to do.</summary>
        public static Doc Open(string path, string? password = null)
        {
            Ready();
            var name = System.IO.Path.GetFileName(path);
            FileStream stream;
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
            {
                throw new OpenException($"{name} couldn't be opened: {ex.Message}", false);
            }
            if (stream.Length > MaxBytes) { stream.Dispose(); throw new OpenException($"{name} is larger than 4 GB, the most the PDF editor opens.", false); }
            if (stream.Length == 0) { stream.Dispose(); throw new OpenException($"{name} is empty.", false); }
            var buffer = new byte[1 << 16];
            Native.GetBlock read = (_, position, target, size) =>
            {
                try
                {
                    long at = ToLong(position), left = ToLong(size);
                    stream.Seek(at, SeekOrigin.Begin);
                    var written = 0L;
                    while (left > 0)
                    {
                        var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (n <= 0) return 0;
                        Marshal.Copy(buffer, 0, target + (int)written, n);
                        written += n;
                        left -= n;
                    }
                    return 1;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return 0; }
            };
            var access = Marshal.AllocHGlobal(Marshal.SizeOf<Native.FileAccess>());
            Marshal.StructureToPtr(new Native.FileAccess { Length = FromLong(stream.Length), GetBlock = Marshal.GetFunctionPointerForDelegate(read), Param = IntPtr.Zero }, access, false);
            var handle = Native.FPDF_LoadCustomDocument(access, password);
            if (handle == IntPtr.Zero)
            {
                var error = ToLong(Native.FPDF_GetLastError());
                stream.Dispose();
                Marshal.FreeHGlobal(access);
                throw error switch
                {
                    4 => new OpenException(password is null ? $"{name} is locked with a password." : "That password didn't open it. Try again.", true),
                    5 => new OpenException($"{name} is protected in a way the PDF editor can't open. Save an unlocked copy from a PDF viewer and open that.", false),
                    _ => new OpenException($"{name} couldn't be read as a PDF (it may be damaged). Open it in a PDF viewer and save a fresh copy, then try that.", false),
                };
            }
            return new Doc(path, stream, handle, access, read);
        }

        /// <summary>A page's size in points as it shows (its own rotation applied), or A4 when it can't be read.</summary>
        public (double Width, double Height) Size(int index) =>
            Native.FPDF_GetPageSizeByIndexF(Handle, index, out var size) != 0 && size.Width > 0 && size.Height > 0 ? (size.Width, size.Height) : (595, 842);

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            Native.FPDF_CloseDocument(Handle);
            Handle = IntPtr.Zero;
            _stream.Dispose();
            Marshal.FreeHGlobal(_access);
            GC.KeepAlive(_read);
        }
    }

    // ---- Pictures of pages ---------------------------------------------------------------------------------------

    /// <summary>A page picture: 32-bit BGRx pixels, top row first.</summary>
    public sealed record Picture(int Width, int Height, int Stride, byte[] Pixels);

    /// <summary>
    /// Draws page <paramref name="index"/> of <paramref name="doc"/> (null: a blank page of <paramref name="blank"/>'s
    /// size) turned <paramref name="quarterTurns"/> clockwise, as large as fits in <paramref name="maxWidth"/> by
    /// <paramref name="maxHeight"/> pixels. On the PDF thread.
    /// </summary>
    public static Picture Draw(Doc? doc, int index, int quarterTurns, int maxWidth, int maxHeight, (double W, double H) blank = default)
    {
        Ready();
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        var page = doc is null ? IntPtr.Zero : Native.FPDF_LoadPage(doc.Handle, index);
        try
        {
            double w, h;
            if (page != IntPtr.Zero) { w = Native.FPDF_GetPageWidthF(page); h = Native.FPDF_GetPageHeightF(page); }
            else (w, h) = blank.W > 0 ? blank : (595, 842);
            if (quarterTurns % 2 == 1) (w, h) = (h, w);
            var scale = Math.Min(maxWidth / Math.Max(1, w), maxHeight / Math.Max(1, h));
            var pw = Math.Max(1, (int)Math.Round(w * scale));
            var ph = Math.Max(1, (int)Math.Round(h * scale));
            var bitmap = Native.FPDFBitmap_Create(pw, ph, 0);
            if (bitmap == IntPtr.Zero) throw new OutOfMemoryException("Not enough memory to draw the page.");
            try
            {
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, pw, ph, 0xFFFFFFFF);
                if (page != IntPtr.Zero) Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, pw, ph, quarterTurns, Annotations);
                var stride = Native.FPDFBitmap_GetStride(bitmap);
                var pixels = new byte[stride * ph];
                Marshal.Copy(Native.FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
                return new Picture(pw, ph, stride, pixels);
            }
            finally { Native.FPDFBitmap_Destroy(bitmap); }
        }
        finally { if (page != IntPtr.Zero) Native.FPDF_ClosePage(page); }
    }

    private const int Annotations = 0x01; // FPDF_ANNOT: comments and stamps show, as in a viewer

    // ---- Writing ------------------------------------------------------------------------------------------------

    /// <summary>A page to write: page <see cref="Index"/> of <see cref="Doc"/> (null: a blank page of <see cref="Blank"/>'s size), turned <see cref="QuarterTurns"/> clockwise.</summary>
    public readonly record struct Out(Doc? Doc, int Index, int QuarterTurns, (double W, double H) Blank = default);

    /// <summary>
    /// Writes <paramref name="pages"/>, in order, as a new PDF at <paramref name="target"/> (replaced if it exists).
    /// Runs of pages from one file are copied together so their fonts and pictures are shared, not repeated.
    /// <paramref name="progress"/> gets pages done and the page count. On the PDF thread.
    /// </summary>
    public static long Write(IReadOnlyList<Out> pages, string target, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        Ready();
        if (pages.Count == 0) throw new ArgumentException("There are no pages to save.");
        var doc = Native.FPDF_CreateNewDocument();
        if (doc == IntPtr.Zero) throw new OutOfMemoryException("Not enough memory to make the PDF.");
        try
        {
            var at = 0;
            while (at < pages.Count)
            {
                ct.ThrowIfCancellationRequested();
                var first = pages[at];
                if (first.Doc is null)
                {
                    var (w, h) = first.Blank.W > 0 ? first.Blank : (595, 842);
                    var page = Native.FPDFPage_New(doc, at, w, h);
                    if (page == IntPtr.Zero) throw new InvalidOperationException("A blank page couldn't be added.");
                    Native.FPDF_ClosePage(page);
                    at++;
                    progress?.Invoke(at, pages.Count);
                    continue;
                }
                var run = new List<int>();
                for (var k = at; k < pages.Count && ReferenceEquals(pages[k].Doc, first.Doc) && run.Count < 500; k++) run.Add(pages[k].Index);
                if (Native.FPDF_ImportPagesByIndex(doc, first.Doc.Handle, run.ToArray(), FromLong(run.Count), at) == 0)
                    throw new InvalidOperationException($"Pages of {Path.GetFileName(first.Doc.Path)} couldn't be copied (the file may be damaged).");
                at += run.Count;
                progress?.Invoke(at, pages.Count);
            }
            for (var i = 0; i < pages.Count; i++)
            {
                var turns = ((pages[i].QuarterTurns % 4) + 4) % 4;
                if (turns == 0) continue;
                var page = Native.FPDF_LoadPage(doc, i);
                if (page == IntPtr.Zero) continue;
                Native.FPDFPage_SetRotation(page, (Native.FPDFPage_GetRotation(page) + turns) % 4);
                Native.FPDF_ClosePage(page);
            }
            ct.ThrowIfCancellationRequested();
            var folder = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            try { return Save(doc, target); }
            catch
            {
                try { File.Delete(target); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
        finally { Native.FPDF_CloseDocument(doc); }
    }

    private static long Save(IntPtr doc, string target)
    {
        using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var buffer = new byte[1 << 16];
        Exception? failed = null;
        Native.WriteBlock write = (_, data, size) =>
        {
            try
            {
                long left = ToLong(size), done = 0;
                while (left > 0)
                {
                    var n = (int)Math.Min(buffer.Length, left);
                    Marshal.Copy(data + (int)done, buffer, 0, n);
                    file.Write(buffer, 0, n);
                    done += n;
                    left -= n;
                }
                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = ex; return 0; }
        };
        var writer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.FileWrite>());
        try
        {
            Marshal.StructureToPtr(new Native.FileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(write) }, writer, false);
            if (Native.FPDF_SaveAsCopy(doc, writer, NoIncremental) == 0)
                throw failed is not null ? new IOException($"Couldn't write {Path.GetFileName(target)}: {failed.Message}", failed)
                    : new IOException($"Couldn't write {Path.GetFileName(target)}.");
        }
        finally
        {
            Marshal.FreeHGlobal(writer);
            GC.KeepAlive(write);
        }
        file.Flush();
        return file.Length;
    }

    private const uint NoIncremental = 1 << 1;

#if NET
    private static long ToLong(ULong value) => (long)value.Value;
    private static ULong FromLong(long value) => new((nuint)value);
#else
    private static long ToLong(ULong value) => value;
    private static ULong FromLong(long value) => (uint)value;
#endif

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FileAccess
        {
            public ULong Length;
            public IntPtr GetBlock;
            public IntPtr Param;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FileWrite
        {
            public int Version;
            public IntPtr WriteBlock;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SizeF
        {
            public float Width, Height;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GetBlock(IntPtr param, ULong position, IntPtr buffer, ULong size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int WriteBlock(IntPtr self, IntPtr data, ULong size);

        [DllImport(Library)] public static extern void FPDF_InitLibrary();
        [DllImport(Library)] public static extern ULong FPDF_GetLastError();
        [DllImport(Library)] public static extern IntPtr FPDF_LoadCustomDocument(IntPtr access, [MarshalAs(UnmanagedType.LPStr)] string? password);
        [DllImport(Library)] public static extern void FPDF_CloseDocument(IntPtr doc);
        [DllImport(Library)] public static extern int FPDF_GetPageCount(IntPtr doc);
        [DllImport(Library)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr doc, int index, out SizeF size);
        [DllImport(Library)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
        [DllImport(Library)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Library)] public static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Library)] public static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Library)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Library)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Library)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Library)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Library)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Library)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
        [DllImport(Library)] public static extern IntPtr FPDF_CreateNewDocument();
        [DllImport(Library)] public static extern IntPtr FPDFPage_New(IntPtr doc, int index, double width, double height);
        [DllImport(Library)] public static extern int FPDF_ImportPagesByIndex(IntPtr dest, IntPtr source, int[] indices, ULong length, int index);
        [DllImport(Library)] public static extern int FPDFPage_GetRotation(IntPtr page);
        [DllImport(Library)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
        [DllImport(Library)] public static extern int FPDF_SaveAsCopy(IntPtr doc, IntPtr writer, uint flags);
    }
}
